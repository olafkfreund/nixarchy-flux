package core

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"testing"
	"time"

	"flux/internal/config"
	"flux/internal/lan"
	"flux/internal/proto"
)

// spaceStub replaces freeSpace. low makes every check fail. stall holds the
// next checks until the returned function runs, so a test can stop the
// worker at a known place.
type spaceStub struct {
	low, block atomic.Bool
	entered    chan struct{}
	mu         sync.Mutex
	hold       chan struct{}
}

func stubSpace(t *testing.T) *spaceStub {
	t.Helper()
	s := &spaceStub{entered: make(chan struct{}, 1)}
	old := freeSpace
	freeSpace = func(string) (uint64, uint64, error) {
		if s.block.Load() {
			s.mu.Lock()
			h := s.hold
			s.mu.Unlock()
			select {
			case s.entered <- struct{}{}:
			default:
			}
			<-h
		}
		if s.low.Load() {
			return 0, 100 << 30, nil
		}
		return 100 << 30, 100 << 30, nil
	}
	t.Cleanup(func() { freeSpace = old })
	return s
}

func (s *spaceStub) stall() (release func()) {
	for len(s.entered) > 0 {
		<-s.entered
	}
	h := make(chan struct{})
	s.mu.Lock()
	s.hold = h
	s.mu.Unlock()
	s.block.Store(true)
	return func() { s.block.Store(false); close(h) }
}

func waitWorkers(t *testing.T, d *Daemon) {
	t.Helper()
	buf := make([]byte, 1<<20)
	for range 500 {
		if !bytes.Contains(buf[:runtime.Stack(buf, true)], []byte("runResumeWorker")) {
			// The mutex orders the last reads of the worker before the restore.
			d.mu.Lock()
			d.mu.Unlock()
			testSweepMu.Lock()
			testSweepMu.Unlock()
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Error("a resume worker did not end")
}

func shortIdle(t *testing.T, d time.Duration) {
	t.Helper()
	old := receiveIdle
	receiveIdle = d
	t.Cleanup(func() { receiveIdle = old })
}

type rfile struct {
	id, name string
	data     []byte
}

func mkfile(n, size int) rfile {
	data := make([]byte, size)
	for i := range data {
		data[i] = byte(i*7 + n)
	}
	return rfile{id: fmt.Sprintf("%012x", n), name: fmt.Sprintf("f%d.bin", n), data: data}
}

func (f rfile) msg(action string, offset int64) resumeMessage {
	sum := sha256.Sum256(f.data)
	return resumeMessage{Action: action, ID: f.id, Name: f.name, Size: int64(len(f.data)), Hash: hex.EncodeToString(sum[:]), Offset: offset}
}

// rx is a daemon that receives resumable transfers from a device. The test
// plays the sender on the other end of a real link.
type rx struct {
	t                *testing.T
	ctx              context.Context
	b                *Daemon
	peer             *Device
	onDesk, onPhone  *lan.Link
	replies          chan resumeMessage
	downloads, inbox string
}

func newRx(t *testing.T) *rx {
	t.Helper()
	t.Setenv("XDG_DATA_HOME", t.TempDir())
	ctx, cancel := context.WithCancel(context.Background())
	// The workers must end before a test restores freeSpace or receiveIdle.
	r := &rx{t: t, ctx: ctx, replies: make(chan resumeMessage, 256), downloads: t.TempDir()}
	t.Cleanup(func() { cancel(); waitWorkers(t, r.b) })
	// The folder does not exist yet, as on a fresh install.
	r.inbox = filepath.Join(t.TempDir(), "incoming")
	r.b = &Daemon{ctx: ctx, cfg: &config.Config{DownloadDir: r.downloads}, devices: map[string]*Device{},
		resumeDir: r.inbox, resumeWorkers: map[string]*resumeWorker{}, logger: log.New(io.Discard, "", 0)}
	r.attach()
	return r
}

// attach links a new pair of links to the receiver, as after a reconnect.
func (r *rx) attach() {
	onDesk, onPhone, deskID, _ := linkPair(r.t, r.ctx)
	if r.peer == nil {
		r.peer = &Device{ID: deskID, Name: "desk", Paired: true}
		r.b.devices[deskID] = r.peer
	}
	r.b.mu.Lock()
	r.peer.link = onPhone
	r.b.mu.Unlock()
	r.onDesk, r.onPhone = onDesk, onPhone
	go onDesk.Receive(func(p *proto.Packet) {
		var m resumeMessage
		if p.Type == proto.TypeFluxTransfer && p.Decode(&m) == nil {
			select {
			case r.replies <- m:
			default:
			}
		}
	})
	go onPhone.Receive(func(p *proto.Packet) { r.b.handlePacket(r.peer, onPhone, p) })
}

func (r *rx) next() resumeMessage {
	r.t.Helper()
	select {
	case m := <-r.replies:
		return m
	case <-time.After(5 * time.Second):
		r.t.Fatal("no reply within 5 seconds")
		return resumeMessage{}
	}
}

func (r *rx) expect(action string) resumeMessage {
	r.t.Helper()
	m := r.next()
	if m.Action != action {
		r.t.Fatalf("reply %+v, want %s", m, action)
	}
	return m
}

func (r *rx) send(m resumeMessage) {
	r.t.Helper()
	if err := r.onDesk.Send(proto.New(proto.TypeFluxTransfer, m)); err != nil {
		r.t.Fatal(err)
	}
}

// sendReader sends a chunk without waiting for the reply.
func (r *rx) sendReader(m resumeMessage, rd io.Reader, size int64) {
	p := proto.New(proto.TypeFluxTransfer, m)
	go func() { _ = r.onDesk.SendWithPayload(r.ctx, p, rd, size, func(int64) {}) }()
}

func (r *rx) sendChunk(f rfile, off, n int64) {
	r.sendReader(f.msg("chunk", off), bytes.NewReader(f.data[off:off+n]), n)
}

func (r *rx) offer(f rfile) resumeMessage {
	r.t.Helper()
	r.send(f.msg("offer", 0))
	return r.next()
}

func (r *rx) chunk(f rfile, off, n int64) resumeMessage {
	r.t.Helper()
	r.sendChunk(f, off, n)
	return r.next()
}

func (r *rx) folder(f rfile) string  { return filepath.Join(r.inbox, incomingFolder(r.peer.ID, f.id)) }
func (r *rx) partial(f rfile) string { return filepath.Join(r.folder(f), "data") }

func (r *rx) record(f rfile) (incomingRecord, bool) { return readIncoming(r.folder(f)) }

// find returns the newest incoming transfer of the file.
func (r *rx) find(f rfile) (id, state, errText string) {
	r.b.mu.Lock()
	defer r.b.mu.Unlock()
	for _, t := range r.b.transfers {
		if t.Name == f.name && t.Dir == "in" {
			return t.ID, t.State, t.Error
		}
	}
	return "", "", ""
}

func (r *rx) state(f rfile) string { _, s, _ := r.find(f); return s }

func (r *rx) waitState(f rfile, want string) {
	r.t.Helper()
	waitFor(r.t, "transfer "+f.name+" "+want, func() bool { return r.state(f) == want })
}

func (r *rx) size(path string) int64 {
	info, err := os.Stat(path)
	if err != nil {
		return -1
	}
	return info.Size()
}

// seed writes a record of a device, aged by age.
func (r *rx) seed(device string, n int, rec incomingRecord, age time.Duration) string {
	r.t.Helper()
	id := fmt.Sprintf("%012x", n)
	dir := filepath.Join(r.inbox, incomingFolder(device, id))
	if rec.Device == "" && device != "legacy" {
		rec.Device = device
	}
	rec.Name, rec.Size, rec.Hash = "x.bin", 10, strings.Repeat("a", 64)
	if err := saveJSON(filepath.Join(dir, "state.json"), rec); err != nil {
		r.t.Fatal(err)
	}
	at := time.Now().Add(-age)
	if err := os.Chtimes(dir, at, at); err != nil {
		r.t.Fatal(err)
	}
	return dir
}

func exists(path string) bool { _, err := os.Stat(path); return err == nil }

func TestResumeCompletesByRename(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 3000)
	if rep := r.offer(f); rep.Action != "offset" || rep.Offset != 0 {
		t.Fatal(rep)
	}
	// The sender sends the chunk right after the offset reply.
	if rep := r.chunk(f, 0, 1000); rep.Action != "offset" || rep.Offset != 1000 {
		t.Fatal(rep)
	}
	before, err := os.Stat(r.partial(f))
	if err != nil {
		t.Fatal(err)
	}
	if rep := r.chunk(f, 1000, 2000); rep.Action != "done" {
		t.Fatal(rep)
	}
	final := filepath.Join(r.downloads, f.name)
	got, err := os.ReadFile(final)
	if err != nil || !bytes.Equal(got, f.data) {
		t.Fatalf("final file: %d bytes, %v", len(got), err)
	}
	after, _ := os.Stat(final)
	if !os.SameFile(before, after) {
		t.Fatal("the same file system must complete by rename")
	}
	if rec, _ := r.record(f); rec.Path != final || rec.Device != r.peer.ID {
		t.Fatalf("record %+v", rec)
	}
	r.waitState(f, "done")
}

// TestResumeCompletesAcrossFileSystems makes the first rename fail with
// EXDEV, as when incoming and the download folder are on 2 file systems.
// The copy must complete, keep the file, and leave no temporary file.
func TestResumeCompletesAcrossFileSystems(t *testing.T) {
	// Registered before newRx, so it runs after the receiver's cleanup has
	// stopped the worker that reads renameFile.
	t.Cleanup(func() { renameFile = os.Rename })
	renameFile = func(old, new string) error {
		if filepath.Base(old) == "data" {
			return &os.LinkError{Op: "rename", Old: old, New: new, Err: syscall.EXDEV}
		}
		return os.Rename(old, new)
	}
	r := newRx(t)
	f := mkfile(3, 3000)
	r.expect2(r.offer(f), "offset")
	if rep := r.chunk(f, 0, 3000); rep.Action != "done" {
		t.Fatal(rep)
	}
	final := filepath.Join(r.downloads, f.name)
	got, err := os.ReadFile(final)
	if err != nil || !bytes.Equal(got, f.data) {
		t.Fatalf("final file: %d bytes, %v", len(got), err)
	}
	if rec, _ := r.record(f); rec.Path != final {
		t.Fatalf("record %+v", rec)
	}
	entries, _ := os.ReadDir(r.downloads)
	for _, e := range entries {
		if strings.HasSuffix(e.Name(), partSuffix) {
			t.Fatalf("temporary file %s left behind", e.Name())
		}
	}
	r.waitState(f, "done")
}

func TestResumeLowSpace(t *testing.T) {
	s := stubSpace(t)
	r := newRx(t)
	f := mkfile(2, 3000)
	s.low.Store(true)
	if rep := r.offer(f); rep.Action != "error" || !strings.Contains(rep.Error, "almost full") {
		t.Fatal(rep)
	}
	s.low.Store(false)
	r.expect2(r.offer(f), "offset")
	s.low.Store(true)
	if rep := r.chunk(f, 0, 1000); rep.Action != "error" || !strings.Contains(rep.Error, "almost full") {
		t.Fatal(rep)
	}
	if n := r.size(r.partial(f)); n != 0 {
		t.Fatalf("partial has %d bytes", n)
	}
}

func (r *rx) expect2(m resumeMessage, action string) {
	r.t.Helper()
	if m.Action != action {
		r.t.Fatalf("reply %+v, want %s", m, action)
	}
}

func TestResumeFifthConcurrentTransfer(t *testing.T) {
	r := newRx(t)
	for n := 1; n <= 4; n++ {
		r.expect2(r.offer(mkfile(n, 100)), "offset")
	}
	if rep := r.offer(mkfile(5, 100)); rep.Action != "error" || !strings.Contains(rep.Error, "more than 4") {
		t.Fatal(rep)
	}
}

func TestResumeFullQueue(t *testing.T) {
	s := stubSpace(t)
	r := newRx(t)
	f := mkfile(1, 100)
	release := s.stall()
	r.b.handleResumable(r.peer, r.onPhone, proto.New(proto.TypeFluxTransfer, f.msg("offer", 0)))
	select {
	case <-s.entered:
	case <-time.After(5 * time.Second):
		t.Fatal("the worker did not start")
	}
	goroutines := runtime.NumGoroutine()
	for range 30 {
		r.b.handleResumable(r.peer, r.onPhone, proto.New(proto.TypeFluxTransfer, f.msg("offer", 0)))
	}
	if grew := runtime.NumGoroutine() - goroutines; grew > 3 {
		t.Fatalf("%d goroutines more for 30 packets", grew)
	}
	full := 0
	deadline := time.After(5 * time.Second)
	for full < 21 {
		select {
		case m := <-r.replies:
			if m.Error == "too many transfer packets" {
				full++
			}
		case <-deadline:
			t.Fatalf("only %d refused packets", full)
		}
	}
	release()
}

func TestResumeChunkWithoutOffer(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 3000)
	if rep := r.chunk(f, 0, 1000); rep.Action != "error" || rep.Error != "no offer for this transfer" {
		t.Fatal(rep)
	}
	if exists(r.folder(f)) {
		t.Fatal("a chunk created state")
	}
}

func TestResumeUnfinishedLimit(t *testing.T) {
	r := newRx(t)
	for n := range maxPartials {
		r.seed(r.peer.ID, 1000+n, incomingRecord{}, time.Hour)
	}
	if rep := r.offer(mkfile(1, 100)); rep.Action != "error" || rep.Error != "too many unfinished transfers from this device" {
		t.Fatal(rep)
	}
	// A finished record does not count.
	r.seed(r.peer.ID, 1000, incomingRecord{Path: "/x"}, time.Hour)
	r.expect2(r.offer(mkfile(1, 100)), "offset")
}

func TestResumeParallelOffersStopAtLimit(t *testing.T) {
	r := newRx(t)
	for n := range maxPartials - 2 {
		r.seed(r.peer.ID, 1000+n, incomingRecord{}, time.Hour)
	}
	for n := 1; n <= 4; n++ {
		r.send(mkfile(n, 100).msg("offer", 0))
	}
	ok, refused := 0, 0
	for range 4 {
		switch m := r.next(); {
		case m.Action == "offset":
			ok++
		case strings.Contains(m.Error, "too many unfinished"):
			refused++
		default:
			t.Fatal(m)
		}
	}
	if ok != 2 || refused != 2 {
		t.Fatalf("%d accepted, %d refused", ok, refused)
	}
}

func TestResumeRecordLimitEvictsFinished(t *testing.T) {
	r := newRx(t)
	dev := r.peer.ID
	var unfinished, finished, canceled []string
	for n := range 10 {
		unfinished = append(unfinished, r.seed(dev, 1000+n, incomingRecord{Device: dev}, time.Duration(2000+n)*time.Minute))
	}
	for n := range 40 {
		finished = append(finished, r.seed(dev, 2000+n, incomingRecord{Device: dev, Path: "/x"}, time.Duration(1000-n)*time.Minute))
	}
	for n := range 14 {
		canceled = append(canceled, r.seed(dev, 3000+n, incomingRecord{Device: dev, Canceled: true}, time.Duration(500-n)*time.Minute))
	}
	r.expect2(r.offer(mkfile(1, 100)), "offset")
	if exists(finished[0]) || !exists(finished[1]) || !exists(canceled[0]) {
		t.Fatal("only the oldest finished record goes")
	}
	for _, dir := range unfinished {
		if !exists(dir) {
			t.Fatal("an unfinished record was evicted")
		}
	}
	entries, _ := os.ReadDir(r.inbox)
	if len(entries) != maxRecords {
		t.Fatalf("%d records", len(entries))
	}
}

func TestResumeLimitsAreForOneDevice(t *testing.T) {
	r := newRx(t)
	var all []string
	for n := range maxPartials {
		all = append(all, r.seed("other", 1000+n, incomingRecord{Device: "other"}, time.Hour))
	}
	for n := range maxRecords - maxPartials {
		all = append(all, r.seed("other", 2000+n, incomingRecord{Device: "other", Path: "/x"}, time.Hour))
	}
	// Records without a device count for no device.
	for n := range maxPartials {
		all = append(all, r.seed("legacy", 3000+n, incomingRecord{}, time.Hour))
	}
	r.expect2(r.offer(mkfile(1, 100)), "offset")
	for _, dir := range all {
		if !exists(dir) {
			t.Fatal("a record of another device was removed")
		}
	}
}

func TestResumePruneKeepsOthersAndActiveRuns(t *testing.T) {
	r := newRx(t)
	a, b := mkfile(1, 3000), mkfile(2, 3000)
	r.expect2(r.offer(a), "offset")
	old := time.Now().Add(-8 * 24 * time.Hour)
	if err := os.Chtimes(r.folder(a), old, old); err != nil {
		t.Fatal(err)
	}
	own := r.seed(r.peer.ID, 1000, incomingRecord{Device: r.peer.ID}, 8*24*time.Hour)
	other := r.seed("other", 1001, incomingRecord{Device: "other"}, 8*24*time.Hour)
	legacy := r.seed("legacy", 1002, incomingRecord{}, 8*24*time.Hour)
	r.expect2(r.offer(b), "offset")
	if exists(own) {
		t.Fatal("an old record of the device stayed")
	}
	if !exists(r.folder(a)) || !exists(other) || !exists(legacy) {
		t.Fatal("the worker removed a folder of an active run or of another device")
	}
	// A written chunk refreshes the time of the folder.
	r.expect2(r.chunk(a, 0, 1000), "offset")
	if info, err := os.Stat(r.folder(a)); err != nil || time.Since(info.ModTime()) > time.Minute {
		t.Fatalf("folder time %v, %v", info.ModTime(), err)
	}
}

func TestResumeStartupCleanup(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("XDG_CONFIG_HOME", filepath.Join(home, "config"))
	t.Setenv("XDG_DATA_HOME", filepath.Join(home, "data"))
	t.Setenv("XDG_RUNTIME_DIR", filepath.Join(home, "run"))
	incoming := filepath.Join(config.DataDir(), "incoming")
	seed := func(device string, n int, rec incomingRecord, age time.Duration) string {
		dir := filepath.Join(incoming, incomingFolder(device, fmt.Sprintf("%012x", n)))
		if err := saveJSON(filepath.Join(dir, "state.json"), rec); err != nil {
			t.Fatal(err)
		}
		at := time.Now().Add(-age)
		_ = os.Chtimes(dir, at, at)
		return dir
	}
	oldOwned := seed("phone", 1, incomingRecord{Device: "phone"}, 8*24*time.Hour)
	oldLegacy := seed("", 2, incomingRecord{}, 8*24*time.Hour)
	fresh := seed("phone", 3, incomingRecord{Device: "phone"}, time.Hour)
	downloads := filepath.Join(home, "Downloads")
	if err := os.MkdirAll(downloads, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{".flux-123.part", ".flux-received-abc", "keep.txt"} {
		if err := os.WriteFile(filepath.Join(downloads, name), []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := New(context.Background(), log.New(io.Discard, "", 0), Options{Headless: true}); err != nil {
		t.Fatal(err)
	}
	if exists(oldOwned) || exists(oldLegacy) || !exists(fresh) {
		t.Fatal("the start must remove the old records and keep the fresh one")
	}
	if exists(filepath.Join(downloads, ".flux-123.part")) || exists(filepath.Join(downloads, ".flux-received-abc")) || !exists(filepath.Join(downloads, "keep.txt")) {
		t.Fatal("the start must remove only the temporary files")
	}
}

func TestResumeFreshInstallAcceptsFirstOffer(t *testing.T) {
	r := newRx(t)
	if exists(r.inbox) {
		t.Fatal("the inbox must not exist yet")
	}
	r.expect2(r.offer(mkfile(1, 100)), "offset")
	if !exists(r.inbox) {
		t.Fatal("no inbox")
	}
}

func TestResumeUnpairMidChunk(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	gate := make(chan struct{})
	// CopyN ignores a check error on its last read, so more reads follow the gate.
	rd := io.MultiReader(bytes.NewReader(f.data[:500]), gateReader{gate}, bytes.NewReader(f.data[500:600]), bytes.NewReader(f.data[600:1000]))
	r.sendReader(f.msg("chunk", 0), rd, 1000)
	waitFor(t, "first bytes", func() bool { return r.size(r.partial(f)) > 0 })
	r.b.mu.Lock()
	r.peer.Paired = false
	r.b.mu.Unlock()
	close(gate)
	if rep := r.next(); rep.Action != "error" {
		t.Fatal(rep)
	}
	if !exists(r.partial(f)) {
		t.Fatal("the partial is gone")
	}
	if entries, _ := os.ReadDir(r.downloads); len(entries) != 0 {
		t.Fatalf("final files: %v", entries)
	}
}

type gateReader struct{ gate chan struct{} }

func (g gateReader) Read([]byte) (int, error) { <-g.gate; return 0, io.EOF }

func TestResumeLinkDropKeepsPartialAndResumes(t *testing.T) {
	shortIdle(t, 400*time.Millisecond)
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	r.onDesk.Close()
	waitFor(t, "failed transfer", func() bool { return r.state(f) == "failed" })
	if _, _, msg := r.find(f); msg != errLinkDown.Error() {
		t.Fatalf("error %q", msg)
	}
	if n := r.size(r.partial(f)); n != 1000 {
		t.Fatalf("partial has %d bytes", n)
	}
	r.attach()
	if rep := r.offer(f); rep.Action != "offset" || rep.Offset != 1000 {
		t.Fatal(rep)
	}
}

func TestResumeActiveAndBusy(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	gate := make(chan struct{})
	rd := io.MultiReader(bytes.NewReader(f.data[:500]), gateReader{gate}, bytes.NewReader(f.data[500:1000]))
	r.sendReader(f.msg("chunk", 0), rd, 1000)
	r.waitState(f, "active")
	if got := r.b.Busy(); got != "a file transfer" {
		t.Fatalf("Busy %q", got)
	}
	close(gate)
	r.expect2(r.next(), "offset")
}

func TestResumeCancelDeletesPartialAndRefusesRetry(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	id, _, _ := r.find(f)
	if err := r.b.CancelTransfer(id); err != nil {
		t.Fatal(err)
	}
	waitFor(t, "partial deleted", func() bool { return !exists(r.partial(f)) })
	waitFor(t, "tombstone", func() bool { rec, _ := r.record(f); return rec.Canceled })
	r.waitState(f, "canceled")
	if rep := r.offer(f); rep.Action != "error" || rep.Error != "the transfer was canceled" {
		t.Fatal(rep)
	}
}

// With the event channel full and no later packet, the cancel still ends
// the run and deletes the partial once the worker is free (R4).
func TestResumeCancelWithFullEvents(t *testing.T) {
	shortIdle(t, 200*time.Millisecond)
	s := stubSpace(t)
	r := newRx(t)
	a, b := mkfile(1, 3000), mkfile(2, 3000)
	r.expect2(r.offer(a), "offset")
	r.expect2(r.chunk(a, 0, 1000), "offset")
	id, _, _ := r.find(a)
	release := s.stall()
	r.send(b.msg("offer", 0))
	select {
	case <-s.entered:
	case <-time.After(5 * time.Second):
		t.Fatal("the worker did not block")
	}
	r.b.mu.Lock()
	w := r.b.resumeWorkers[r.peer.ID]
	r.b.mu.Unlock()
	select {
	case w.events <- struct{}{}:
	default:
	}
	if err := r.b.CancelTransfer(id); err != nil {
		t.Fatal(err)
	}
	<-w.events
	release()
	r.expect2(r.next(), "offset")
	waitFor(t, "partial deleted", func() bool { return !exists(r.partial(a)) })
	if rec, _ := r.record(a); !rec.Canceled {
		t.Fatal("cancel tombstone missing")
	}
}

func TestResumeCancelRacesCompletion(t *testing.T) {
	r := newRx(t)
	for i := range 15 {
		f := mkfile(100+i, 2000)
		r.expect2(r.offer(f), "offset")
		r.expect2(r.chunk(f, 0, 1000), "offset")
		id, _, _ := r.find(f)
		r.sendChunk(f, 1000, 1000)
		time.Sleep(time.Duration(i) * 300 * time.Microsecond)
		_ = r.b.CancelTransfer(id)
		r.next()
		final := filepath.Join(r.downloads, f.name)
		waitFor(t, "one final state", func() bool {
			rec, _ := r.record(f)
			r.b.mu.Lock()
			state := ""
			for _, tr := range r.b.transfers {
				if tr.ID == id {
					state = tr.State
				}
			}
			r.b.mu.Unlock()
			switch state {
			case "done":
				return exists(final) && rec.Path == final && !rec.Canceled
			case "canceled":
				return !exists(final) && rec.Canceled && !exists(r.partial(f))
			}
			return false
		})
	}
}

func TestResumeCancelBeforePublishWins(t *testing.T) {
	s := stubSpace(t)
	r := newRx(t)
	f := mkfile(1, 2000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	id, _, _ := r.find(f)
	release := s.stall()
	r.sendChunk(f, 1000, 1000)
	select {
	case <-s.entered:
	case <-time.After(5 * time.Second):
		t.Fatal("space check did not stall")
	}
	if err := r.b.CancelTransfer(id); err != nil {
		t.Fatal(err)
	}
	release()
	r.expect2(r.next(), "error")
	r.waitState(f, "canceled")
	waitFor(t, "canceled record", func() bool { rec, _ := r.record(f); return rec.Canceled && !exists(r.partial(f)) })
	if exists(filepath.Join(r.downloads, f.name)) {
		t.Fatal("canceled file published")
	}
}

func TestResumeCancelAfterPublishKeepsFile(t *testing.T) {
	entered, release := make(chan struct{}), make(chan struct{})
	renameFile = func(old, new string) error { close(entered); <-release; return os.Rename(old, new) }
	t.Cleanup(func() { renameFile = os.Rename })
	r := newRx(t)
	f := mkfile(1, 2000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	id, _, _ := r.find(f)
	r.sendChunk(f, 1000, 1000)
	select {
	case <-entered:
	case <-time.After(5 * time.Second):
		t.Fatal("rename not reached")
	}
	if err := r.b.CancelTransfer(id); err != nil {
		t.Fatal(err)
	}
	close(release)
	r.expect2(r.next(), "done")
	r.waitState(f, "done")
	rec, _ := r.record(f)
	if rec.Path == "" || rec.Canceled || !exists(filepath.Join(r.downloads, f.name)) {
		t.Fatalf("record %+v", rec)
	}
}

func TestResumeRefusalsAddNoHistory(t *testing.T) {
	cases := []string{"chunk without offer", "canceled retry", "unfinished limit", "low space new", "low space partial", "fifth active"}
	for _, tc := range cases {
		t.Run(tc, func(t *testing.T) {
			s := stubSpace(t)
			r := newRx(t)
			f := mkfile(1, 100)
			wantReceives := 0
			record := incomingRecord{Name: f.name, Size: int64(len(f.data)), Hash: f.msg("offer", 0).Hash, Device: r.peer.ID}
			switch tc {
			case "canceled retry":
				record.Canceled = true
				if err := saveJSON(filepath.Join(r.folder(f), "state.json"), record); err != nil {
					t.Fatal(err)
				}
			case "unfinished limit":
				for n := range maxPartials {
					r.seed(r.peer.ID, 1000+n, incomingRecord{}, time.Hour)
				}
			case "low space new":
				s.low.Store(true)
			case "low space partial":
				if err := saveJSON(filepath.Join(r.folder(f), "state.json"), record); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(r.partial(f), f.data[:50], 0o600); err != nil {
					t.Fatal(err)
				}
				s.low.Store(true)
			case "fifth active":
				for n := 2; n <= 5; n++ {
					r.expect2(r.offer(mkfile(n, 100)), "offset")
				}
				wantReceives = 4
			}
			r.b.mu.Lock()
			before := len(r.b.transfers)
			r.b.mu.Unlock()
			var rep resumeMessage
			if tc == "chunk without offer" {
				rep = r.chunk(f, 0, 50)
			} else {
				rep = r.offer(f)
			}
			if rep.Action != "error" || rep.ID != f.id {
				t.Fatal(rep)
			}
			switch tc {
			case "canceled retry":
				if rep.Error != "the transfer was canceled" {
					t.Fatal(rep)
				}
			case "low space new", "low space partial":
				if !strings.Contains(rep.Error, "almost full") {
					t.Fatal(rep)
				}
			}
			r.b.mu.Lock()
			gotHistory, gotReceives := len(r.b.transfers), r.b.content.receives[r.peer.ID]
			r.b.mu.Unlock()
			if gotHistory != before || gotReceives != wantReceives {
				t.Fatalf("history %d->%d, receives %d want %d", before, gotHistory, gotReceives, wantReceives)
			}
		})
	}
}

func TestResumeFifthOfferKeepsRecords(t *testing.T) {
	r := newRx(t)
	var folders []string
	for n := range maxRecords - 4 {
		rec := incomingRecord{Path: "/done"}
		if n == 0 {
			rec = incomingRecord{Canceled: true}
		}
		folders = append(folders, r.seed(r.peer.ID, 1000+n, rec, time.Hour))
	}
	for n := 1; n <= 4; n++ {
		f := mkfile(n, 100)
		r.expect2(r.offer(f), "offset")
		folders = append(folders, r.folder(f))
	}
	if rep := r.offer(mkfile(5, 100)); rep.Action != "error" {
		t.Fatal(rep)
	}
	for _, folder := range folders {
		if !exists(folder) {
			t.Fatalf("record removed: %s", folder)
		}
	}
}

func TestResumeExpiredPartialsArePruned(t *testing.T) {
	r := newRx(t)
	var folders []string
	for n := range maxPartials {
		folders = append(folders, r.seed(r.peer.ID, 1000+n, incomingRecord{}, 8*24*time.Hour))
	}
	if rep := r.offer(mkfile(1, 100)); rep.Action != "offset" || rep.Offset != 0 {
		t.Fatal(rep)
	}
	for _, folder := range folders {
		if exists(folder) {
			t.Fatalf("expired record remains: %s", folder)
		}
	}
}

func TestResumeReofferDoneWhileBusy(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 100)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 100), "done")
	for n := 2; n <= 5; n++ {
		r.expect2(r.offer(mkfile(n, 100)), "offset")
	}
	r.b.mu.Lock()
	before := len(r.b.transfers)
	r.b.mu.Unlock()
	if rep := r.offer(f); rep.Action != "done" || rep.Offset != 100 {
		t.Fatal(rep)
	}
	r.b.mu.Lock()
	after := len(r.b.transfers)
	r.b.mu.Unlock()
	if after != before {
		t.Fatalf("history %d -> %d", before, after)
	}
}

type testLogBuffer struct {
	mu sync.Mutex
	bytes.Buffer
}

var testSweepSink atomic.Pointer[chan struct{}]
var testSweepMu sync.Mutex

func init() {
	afterSweep = func() {
		testSweepMu.Lock()
		defer testSweepMu.Unlock()
		if sink := testSweepSink.Load(); sink != nil {
			select {
			case *sink <- struct{}{}:
			default:
			}
		}
	}
}

func (b *testLogBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.Buffer.Write(p)
}
func (b *testLogBuffer) String() string { b.mu.Lock(); defer b.mu.Unlock(); return b.Buffer.String() }

func TestResumeRefusalLogIsLimited(t *testing.T) {
	shortIdle(t, 200*time.Millisecond)
	r := newRx(t)
	logs := &testLogBuffer{}
	r.b.logger = log.New(logs, "", 0)
	f := mkfile(1, 100)
	for range 3 {
		r.expect2(r.chunk(f, 0, 50), "error")
	}
	if got := strings.Count(logs.String(), "resumable transfer from"); got != 1 {
		t.Fatalf("%d log lines: %s", got, logs.String())
	}
	time.Sleep(300 * time.Millisecond)
	r.expect2(r.chunk(f, 0, 50), "error")
	lines := strings.Split(strings.TrimSpace(logs.String()), "\n")
	if len(lines) != 2 || !strings.HasSuffix(lines[1], "(and 2 more)") {
		t.Fatalf("logs: %q", logs.String())
	}
}

func TestResumeQueuedPacketIsNotStalled(t *testing.T) {
	shortIdle(t, 200*time.Millisecond)
	s := stubSpace(t)
	r := newRx(t)
	a, b := mkfile(1, 3000), mkfile(2, 3000)
	for _, f := range []rfile{a, b} {
		r.expect2(r.offer(f), "offset")
		r.expect2(r.chunk(f, 0, 1000), "offset")
	}
	id, _, _ := r.find(b)
	release := s.stall()
	r.sendChunk(a, 1000, 1000)
	select {
	case <-s.entered:
	case <-time.After(5 * time.Second):
		t.Fatal("space check not stalled")
	}
	r.sendChunk(b, 1000, 1000)
	time.Sleep(300 * time.Millisecond)
	release()
	for range 2 {
		r.expect2(r.next(), "offset")
	}
	if got, state, _ := r.find(b); got != id || state == "failed" {
		t.Fatalf("B %s %s, want %s active", got, state, id)
	}
}

func TestResumeBusyWorkerStillStallsSilentRun(t *testing.T) {
	shortIdle(t, 200*time.Millisecond)
	s := stubSpace(t)
	r := newRx(t)
	a, b := mkfile(1, 3000), mkfile(2, 3000)
	for _, f := range []rfile{a, b} {
		r.expect2(r.offer(f), "offset")
		r.expect2(r.chunk(f, 0, 1000), "offset")
	}
	release := s.stall()
	r.sendChunk(a, 1000, 1000)
	select {
	case <-s.entered:
	case <-time.After(5 * time.Second):
		t.Fatal("space check not stalled")
	}
	gate := make(chan struct{})
	defer close(gate)
	r.sendReader(a.msg("chunk", 2000), io.MultiReader(bytes.NewReader(a.data[2000:2500]), gateReader{gate}, bytes.NewReader(a.data[2500:3000])), 1000)
	waitFor(t, "second A packet queued", func() bool {
		r.b.mu.Lock()
		defer r.b.mu.Unlock()
		return len(r.b.resumeWorkers[r.peer.ID].queued[a.id]) >= 2
	})
	r.b.handleResumable(r.peer, r.onPhone, proto.New(proto.TypeFluxTransfer, a.msg("offer", 0)))
	time.Sleep(300 * time.Millisecond)
	release()
	r.expect2(r.next(), "offset")
	waitFor(t, "second A chunk started", func() bool { return r.size(r.partial(a)) >= 2500 })
	r.waitState(b, "failed")
	if _, _, msg := r.find(b); msg != errStalled.Error() {
		t.Fatalf("B error %q", msg)
	}
	r.b.mu.Lock()
	w := r.b.resumeWorkers[r.peer.ID]
	r.b.mu.Unlock()
	if w == nil {
		t.Fatal("A run ended")
	}
	// The worker owns runs; a live transfer state proves A was kept.
	if state := r.state(a); state != "active" {
		t.Fatalf("A state %s", state)
	}
}

func TestResumeReconnectKeepsRun(t *testing.T) {
	shortIdle(t, 400*time.Millisecond)
	sweeps := make(chan struct{}, 16)
	testSweepSink.Store(&sweeps)
	t.Cleanup(func() { testSweepSink.Store(nil) })
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	id, _, _ := r.find(f)
	old := r.onPhone
	r.onDesk.Close()
	select {
	case <-old.Done():
	case <-time.After(5 * time.Second):
		t.Fatal("link did not close")
	}
	select {
	case <-sweeps:
	case <-time.After(5 * time.Second):
		t.Fatal("no sweep")
	}
	r.attach()
	r.expect2(r.chunk(f, 1000, 1000), "offset")
	r.expect2(r.chunk(f, 2000, 1000), "done")
	if got, state, _ := r.find(f); got != id || state != "done" {
		t.Fatalf("transfer %s %s", got, state)
	}
	r.b.mu.Lock()
	defer r.b.mu.Unlock()
	for _, tr := range r.b.transfers {
		if tr.Name == f.name && tr.State == "failed" {
			t.Fatal("failed history entry")
		}
	}
}

func TestResumeStaleLinkPacketKeepsRun(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	id, _, _ := r.find(f)
	stale := r.onPhone
	r.attach()
	r.b.handleResumable(r.peer, stale, proto.New(proto.TypeFluxTransfer, f.msg("chunk", 1000)))
	r.expect2(r.next(), "error")
	if got, _, _ := r.find(f); got != id {
		t.Fatalf("run changed: %s -> %s", id, got)
	}
	if rep := r.chunk(f, 1000, 1000); rep.Action != "offset" || rep.Offset != 2000 {
		t.Fatal(rep)
	}
}

func TestResumeLinkDropMidChunkKeepsRun(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	id, _, _ := r.find(f)
	old := r.onPhone
	r.sendReader(f.msg("chunk", 0), io.MultiReader(bytes.NewReader(f.data[:500]), closeLinkReader{link: r.onDesk}), 1000)
	select {
	case <-old.Done():
	case <-time.After(5 * time.Second):
		t.Fatal("link stayed open")
	}
	waitFor(t, "500 bytes received", func() bool { return r.size(r.partial(f)) == 500 })
	r.attach()
	if rep := r.offer(f); rep.Action != "offset" || rep.Offset != 500 {
		t.Fatal(rep)
	}
	if got, state, _ := r.find(f); got != id || state != "active" {
		t.Fatalf("transfer %s %s", got, state)
	}
}

type closeLinkReader struct{ link *lan.Link }

func (r closeLinkReader) Read([]byte) (int, error) { r.link.Close(); return 0, io.EOF }

func TestResumeFailingPacketsDoNotExtendRun(t *testing.T) {
	shortIdle(t, 400*time.Millisecond)
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	last := time.Now()
	stale := r.onPhone
	r.attach()
	for time.Since(last) < 700*time.Millisecond && r.state(f) != "failed" {
		r.b.handleResumable(r.peer, stale, proto.New(proto.TypeFluxTransfer, f.msg("chunk", 1000)))
		r.expect2(r.next(), "error")
		time.Sleep(100 * time.Millisecond)
	}
	if state := r.state(f); state != "failed" || time.Since(last) > 700*time.Millisecond {
		t.Fatalf("state %s after %v", state, time.Since(last))
	}
	if _, _, msg := r.find(f); msg != errStalled.Error() && msg != errLinkDown.Error() {
		t.Fatalf("error %q", msg)
	}
}

func TestResumeUnpairEndsIdleRun(t *testing.T) {
	shortIdle(t, 400*time.Millisecond)
	r := newRx(t)
	f := mkfile(1, 100)
	r.expect2(r.offer(f), "offset")
	start := time.Now()
	r.b.mu.Lock()
	r.peer.Paired = false
	r.b.mu.Unlock()
	r.waitState(f, "failed")
	if elapsed := time.Since(start); elapsed >= 250*time.Millisecond {
		t.Fatalf("unpair took %v", elapsed)
	}
	if _, _, msg := r.find(f); msg != errReceiveUnpaired.Error() {
		t.Fatalf("error %q", msg)
	}
}

func TestResumePayloadStall(t *testing.T) {
	shortIdle(t, 400*time.Millisecond)
	chunkTimeout = 200 * time.Millisecond
	t.Cleanup(func() { chunkTimeout = time.Minute })
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	gate := make(chan struct{})
	r.sendReader(f.msg("chunk", 0), io.MultiReader(bytes.NewReader(f.data[:500]), gateReader{gate}), 1000)
	r.expect2(r.next(), "error")
	close(gate)
	r.waitState(f, "failed")
	if n := r.size(r.partial(f)); n != 500 {
		t.Fatalf("partial %d bytes", n)
	}
	if rep := r.offer(f); rep.Action != "offset" || rep.Offset != 500 {
		t.Fatal(rep)
	}
}

func assertNoDownloadParts(t *testing.T, dir, name string) {
	t.Helper()
	if exists(filepath.Join(dir, name)) {
		t.Fatal("published file exists")
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		if strings.HasPrefix(entry.Name(), partPrefix) && strings.HasSuffix(entry.Name(), partSuffix) {
			t.Fatalf("temporary file %s remains", entry.Name())
		}
	}
}

func TestResumeCrossFsCopyFails(t *testing.T) {
	renameFile = func(_, _ string) error { return syscall.EXDEV }
	copyAcross = func(dst io.Writer, src io.Reader) (int64, error) {
		n, err := io.CopyN(dst, src, 1000)
		if err != nil {
			return n, err
		}
		return n, fmt.Errorf("disk failed")
	}
	t.Cleanup(func() { renameFile, copyAcross = os.Rename, io.Copy })
	r := newRx(t)
	f := mkfile(1, 2000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 2000), "error")
	r.waitState(f, "failed")
	assertNoDownloadParts(t, r.downloads, f.name)
	if rec, _ := r.record(f); rec.Path != "" {
		t.Fatalf("record path %q", rec.Path)
	}
	if n := r.size(r.partial(f)); n != 2000 {
		t.Fatalf("partial %d bytes", n)
	}
	renameFile, copyAcross = os.Rename, io.Copy
	if rep := r.offer(f); rep.Action != "done" {
		t.Fatal(rep)
	}
	if !exists(filepath.Join(r.downloads, f.name)) {
		t.Fatal("file not published")
	}
}

func TestResumeCrossFsCopyLinkDrop(t *testing.T) {
	shortIdle(t, 400*time.Millisecond)
	var r *rx
	renameFile = func(_, _ string) error { return syscall.EXDEV }
	copyAcross = func(dst io.Writer, src io.Reader) (int64, error) {
		n, err := io.CopyN(dst, src, 1000)
		if err != nil {
			return n, err
		}
		r.onDesk.Close()
		<-r.onPhone.Done()
		<-src.(ctxReader).ctx.Done()
		more, err := io.Copy(dst, src)
		return n + more, err
	}
	t.Cleanup(func() { renameFile, copyAcross = os.Rename, io.Copy })
	r = newRx(t)
	f := mkfile(1, 2000)
	r.expect2(r.offer(f), "offset")
	r.sendChunk(f, 0, 2000)
	select {
	case <-r.onPhone.Done():
	case <-time.After(5 * time.Second):
		t.Fatal("link stayed open")
	}
	waitFor(t, "partial complete", func() bool { return r.size(r.partial(f)) == 2000 })
	waitFor(t, "failed copy cleanup", func() bool {
		if exists(filepath.Join(r.downloads, f.name)) {
			return false
		}
		entries, _ := os.ReadDir(r.downloads)
		return len(entries) == 0
	})
	assertNoDownloadParts(t, r.downloads, f.name)
	if state := r.state(f); state == "failed" {
		t.Fatal("run failed before idle")
	}
	r.attach()
	renameFile, copyAcross = os.Rename, io.Copy
	if rep := r.offer(f); rep.Action != "done" {
		t.Fatal(rep)
	}
	if !exists(filepath.Join(r.downloads, f.name)) {
		t.Fatal("file not published")
	}
}

// A packet that arrives while the idle worker retires is still handled (R2).
func TestResumePacketRacesWorkerRetirement(t *testing.T) {
	shortIdle(t, 40*time.Millisecond)
	r := newRx(t)
	f := mkfile(1, 100)
	for i := range 25 {
		time.Sleep(receiveIdle + time.Duration(i)*receiveIdle/20)
		r.send(f.msg("offer", 0))
		if rep := r.next(); rep.Action != "offset" {
			t.Fatalf("round %d: %+v", i, rep)
		}
	}
}

func TestResumeIdleEndsRunBetweenPackets(t *testing.T) {
	shortIdle(t, 100*time.Millisecond)
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	r.waitState(f, "failed")
	if !exists(r.partial(f)) {
		t.Fatal("the partial is gone")
	}
}

// A chunk that takes longer than the idle time does not end the run.
func TestResumeSlowChunkIsNotIdle(t *testing.T) {
	shortIdle(t, 150*time.Millisecond)
	sweeps := make(chan struct{}, 16)
	testSweepSink.Store(&sweeps)
	t.Cleanup(func() { testSweepSink.Store(nil) })
	r := newRx(t)
	f := mkfile(1, 3000)
	r.expect2(r.offer(f), "offset")
	id, _, _ := r.find(f)
	gate := make(chan struct{})
	time.AfterFunc(450*time.Millisecond, func() { close(gate) })
	rd := io.MultiReader(bytes.NewReader(f.data[:500]), gateReader{gate}, bytes.NewReader(f.data[500:1000]))
	r.sendReader(f.msg("chunk", 0), rd, 1000)
	r.expect2(r.next(), "offset")
	for range 2 {
		select {
		case <-sweeps:
		case <-time.After(5 * time.Second):
			t.Fatal("no idle sweep")
		}
	}
	if got, state, _ := r.find(f); got != id || state != "active" {
		t.Fatalf("transfer %s %s, want %s active", got, state, id)
	}
	r.b.mu.Lock()
	count := 0
	for _, tr := range r.b.transfers {
		if tr.Name == f.name {
			count++
		}
	}
	r.b.mu.Unlock()
	if count != 1 {
		t.Fatalf("%d history entries", count)
	}
	if rep := r.chunk(f, 1000, 1000); rep.Action != "offset" {
		t.Fatal(rep)
	}
}

// The state cannot be saved after the rename: the published file stays (R6).
func TestResumeStateSaveFailureKeepsFile(t *testing.T) {
	s := stubSpace(t)
	r := newRx(t)
	f := mkfile(1, 2000)
	r.expect2(r.offer(f), "offset")
	r.expect2(r.chunk(f, 0, 1000), "offset")
	release := s.stall()
	r.sendChunk(f, 1000, 1000)
	select {
	case <-s.entered:
	case <-time.After(5 * time.Second):
		t.Fatal("the worker did not block")
	}
	state := filepath.Join(r.folder(f), "state.json")
	if err := os.Remove(state); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(state, "x"), 0o700); err != nil {
		t.Fatal(err)
	}
	release()
	r.expect2(r.next(), "done")
	got, err := os.ReadFile(filepath.Join(r.downloads, f.name))
	if err != nil || !bytes.Equal(got, f.data) {
		t.Fatalf("final file: %d bytes, %v", len(got), err)
	}
}

// A link drop after the publish point leaves the published receive done.
func TestResumeDoneSurvivesLateLinkDrop(t *testing.T) {
	t.Cleanup(func() { renameFile = os.Rename })
	r := newRx(t)
	renameFile = func(old, new string) error {
		r.onDesk.Close()
		<-r.onPhone.Done()
		time.Sleep(50 * time.Millisecond)
		return os.Rename(old, new)
	}
	f := mkfile(7, 2000)
	r.expect2(r.offer(f), "offset")
	r.sendChunk(f, 0, 2000)
	waitFor(t, "saved", func() bool { rec, _ := r.record(f); return rec.Path != "" })
	time.Sleep(300 * time.Millisecond)
	_, st, msg := r.find(f)
	rec, _ := r.record(f)
	t.Logf("state=%s err=%q recordPath=%q fileExists=%v", st, msg, rec.Path, exists(filepath.Join(r.downloads, f.name)))
	if st != "done" {
		t.Errorf("published file labelled %s", st)
	}
}

// A new offer refused for space leaves no record that counts as unfinished.
func TestResumeLowSpaceOfferLeavesNoRecord(t *testing.T) {
	s := stubSpace(t)
	r := newRx(t)
	s.low.Store(true)
	for n := 1; n <= maxPartials; n++ {
		r.expect2(r.offer(mkfile(n, 100)), "error")
	}
	s.low.Store(false)
	rep := r.offer(mkfile(99, 100))
	t.Logf("after %d low-space refusals, a new offer gets %+v", maxPartials, rep)
	if rep.Action != "offset" {
		t.Errorf("low-space refusals used up the unfinished limit")
	}
}

// A forgotten and rediscovered device is a new *Device with the same ID;
// its packets must not be checked against the old one.
func TestResumeWorkerUsesTheCurrentDevice(t *testing.T) {
	r := newRx(t)
	f := mkfile(1, 100)
	r.expect2(r.offer(f), "offset")
	r.b.mu.Lock()
	old := r.peer
	old.Paired = false
	nd := &Device{ID: old.ID, Name: "desk", Paired: true, link: r.onPhone}
	r.b.devices[old.ID] = nd
	r.b.mu.Unlock()
	g := mkfile(2, 100)
	r.b.handleResumable(nd, r.onPhone, proto.New(proto.TypeFluxTransfer, g.msg("offer", 0)))
	rep := r.next()
	t.Logf("re-paired device offer: %+v", rep)
	if rep.Action != "offset" {
		t.Errorf("worker uses the stale device")
	}
}
