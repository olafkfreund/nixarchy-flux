package core

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"syscall"
	"time"

	"flux/internal/desktop"
	"flux/internal/lan"
	"flux/internal/proto"
)

const transferChunk = 1 << 20

var transferID = regexp.MustCompile(`^[a-f0-9]{12}$`)
var transferHash = regexp.MustCompile(`^[a-f0-9]{64}$`)

type resumeMessage struct {
	Action string `json:"action"`
	ID     string `json:"id"`
	Name   string `json:"name,omitempty"`
	Size   int64  `json:"size,omitempty"`
	Hash   string `json:"hash,omitempty"`
	Offset int64  `json:"offset"`
	Error  string `json:"error,omitempty"`
}

type incomingRecord struct {
	Name     string `json:"name"`
	Size     int64  `json:"size"`
	Hash     string `json:"hash"`
	Path     string `json:"path,omitempty"`
	Canceled bool   `json:"canceled,omitempty"`
	Device   string `json:"device,omitempty"`
}

// maxPartials is the number of unfinished transfers that 1 device can have
// at the same time. maxRecords is the number of records of 1 device.
const (
	maxPartials = 16
	maxRecords  = 64
)

type resumePacket struct {
	// dev is the device as it was when the packet came. A forgotten and
	// rediscovered device is a new *Device with the same ID, so the worker
	// must not keep the one it started with.
	dev     *Device
	link    *lan.Link
	packet  *proto.Packet
	message resumeMessage
	at      time.Time
}

type resumeRun struct {
	transfer  *Transfer
	dev       *Device
	ctx       context.Context
	cancel    context.CancelFunc
	link      *lan.Link
	last      time.Time
	published bool
}

type resumeWorker struct {
	queue  chan resumePacket
	events chan struct{}
	runs   map[string]*resumeRun
	queued map[string][]time.Time
}

type resumeHead struct {
	record       incomingRecord
	exists, done bool
}

type refusalLog struct {
	last    time.Time
	skipped int
}

var afterSweep func()
var copyAcross = io.Copy
var chunkTimeout = time.Minute

func (d *Daemon) sendResumable(ctx context.Context, l *lan.Link, t *Transfer, j outboxJob, path string) error {
	ch := make(chan resumeMessage, 2)
	key := l.DeviceID() + ":" + j.ID
	d.mu.Lock()
	d.resumeReplies[key] = ch
	d.mu.Unlock()
	defer func() { d.mu.Lock(); delete(d.resumeReplies, key); d.mu.Unlock() }()
	wait := func() (resumeMessage, error) {
		select {
		case b := <-ch:
			if b.Action == "error" {
				return b, errors.New(b.Error)
			}
			return b, nil
		case <-ctx.Done():
			return resumeMessage{}, ctx.Err()
		case <-time.After(time.Minute):
			return resumeMessage{}, errors.New("the device did not acknowledge the transfer")
		}
	}
	b := resumeMessage{Action: "offer", ID: j.ID, Name: j.Name, Size: j.Size, Hash: j.Hash}
	if err := l.Send(proto.New(proto.TypeFluxTransfer, b)); err != nil {
		return err
	}
	ack, err := wait()
	if err != nil {
		return err
	}
	if ack.Action == "done" {
		return nil
	}
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	for ack.Action == "offset" && ack.Offset >= 0 && ack.Offset < j.Size {
		offset := ack.Offset
		length := min(int64(transferChunk), j.Size-offset)
		b.Action, b.Offset = "chunk", offset
		progress := d.progress(t)
		if err := l.SendWithPayload(ctx, proto.New(proto.TypeFluxTransfer, b), io.NewSectionReader(f, offset, length), length,
			func(n int64) { progress(offset + n) }); err != nil {
			return err
		}
		ack, err = wait()
		if err != nil {
			return err
		}
		if ack.Action == "done" {
			return nil
		}
		if ack.Offset != offset+length {
			return errors.New("the device acknowledged an unexpected transfer offset")
		}
	}
	return errors.New("the device returned an invalid transfer offset")
}

func (d *Daemon) handleResumable(dev *Device, l *lan.Link, p *proto.Packet) {
	var b resumeMessage
	if p.Decode(&b) != nil || !transferID.MatchString(b.ID) {
		return
	}
	if b.Action == "offset" || b.Action == "done" || b.Action == "error" {
		d.mu.Lock()
		ch := d.resumeReplies[dev.ID+":"+b.ID]
		current := dev.link == l
		d.mu.Unlock()
		if current && ch != nil {
			select {
			case ch <- b:
			default:
			}
		}
		return
	}
	if b.Action != "offer" && b.Action != "chunk" {
		return
	}
	if !transferHash.MatchString(b.Hash) || b.Size < 0 || b.Size > 1<<40 || safeName(b.Name) != b.Name || b.Name == "" {
		_ = l.Send(proto.New(proto.TypeFluxTransfer, resumeMessage{Action: "error", ID: b.ID, Error: "invalid transfer metadata"}))
		return
	}
	d.mu.Lock()
	if d.resumeWorkers == nil {
		d.resumeWorkers = map[string]*resumeWorker{}
	}
	w := d.resumeWorkers[dev.ID]
	if w == nil {
		w = &resumeWorker{queue: make(chan resumePacket, 8), events: make(chan struct{}, 1), runs: map[string]*resumeRun{}, queued: map[string][]time.Time{}}
		d.resumeWorkers[dev.ID] = w
		go d.runResumeWorker(dev, w)
	}
	at := time.Now()
	select {
	case w.queue <- resumePacket{dev: dev, link: l, packet: p, message: b, at: at}:
		w.queued[b.ID] = append(w.queued[b.ID], at)
		d.mu.Unlock()
	default:
		d.mu.Unlock()
		_ = l.Send(proto.New(proto.TypeFluxTransfer, resumeMessage{Action: "error", ID: b.ID, Error: "too many transfer packets"}))
	}
}

func (d *Daemon) runResumeWorker(dev *Device, w *resumeWorker) {
	tick := time.NewTicker(receiveIdle / 4)
	defer tick.Stop()
	lastWork := time.Now()
	for {
		select {
		case pkt := <-w.queue:
			d.sweepResumeRuns(w)
			d.processResumePacket(pkt.dev, w, pkt)
			d.mu.Lock()
			w.queued[pkt.message.ID] = w.queued[pkt.message.ID][1:]
			if len(w.queued[pkt.message.ID]) == 0 {
				delete(w.queued, pkt.message.ID)
			}
			d.mu.Unlock()
			lastWork = time.Now()
		case <-w.events:
			d.sweepResumeRuns(w)
		case <-tick.C:
			d.sweepResumeRuns(w)
		case <-d.ctx.Done():
			// End every run, so a cancel that the sweep has not seen yet
			// still writes its tombstone and deletes its data.
			d.sweepResumeRuns(w)
			return
		}
		d.mu.Lock()
		if len(w.queue) == 0 && len(w.runs) == 0 && time.Since(lastWork) >= receiveIdle {
			delete(d.resumeWorkers, dev.ID)
			d.mu.Unlock()
			return
		}
		d.mu.Unlock()
	}
}

func (d *Daemon) processResumePacket(dev *Device, w *resumeWorker, pkt resumePacket) {
	b := pkt.message
	run := w.runs[b.ID]
	if run != nil && run.dev != dev {
		d.endResumeRun(w, b.ID, errReceiveUnpaired)
		run = nil
	}
	if run == nil {
		if b.Action == "offer" {
			active := map[string]bool{}
			for id := range w.runs {
				active[incomingFolder(dev.ID, id)] = true
			}
			pruneIncoming(d.resumeDir, time.Now().Add(-7*24*time.Hour), dev.ID, active)
		}
		head, err := d.checkResumable(dev, pkt.link, b)
		if err != nil {
			_ = pkt.link.Send(proto.New(proto.TypeFluxTransfer, resumeMessage{Action: "error", ID: b.ID, Error: err.Error()}))
			d.refused(dev, err)
			return
		}
		if head.done {
			_ = pkt.link.Send(proto.New(proto.TypeFluxTransfer, resumeMessage{Action: "done", ID: b.ID, Offset: head.record.Size}))
			return
		}
		if !d.startReceive(dev) {
			err := fmt.Errorf("the device sends more than %d files at the same time", maxReceives)
			_ = pkt.link.Send(proto.New(proto.TypeFluxTransfer, resumeMessage{Action: "error", ID: b.ID, Error: err.Error()}))
			d.refused(dev, err)
			return
		}
		t := d.newTransfer(dev, b.Name, "", "in", b.Size)
		ctx, cancel := context.WithCancel(d.ctx)
		run = &resumeRun{transfer: t, dev: dev, ctx: ctx, cancel: cancel, link: pkt.link, last: time.Now()}
		w.runs[b.ID] = run
		d.mu.Lock()
		t.cancel = func() {
			cancel()
			select {
			case w.events <- struct{}{}:
			default:
			}
		}
		if t.State == "canceled" {
			t.cancel()
		}
		d.mu.Unlock()
		d.processResumeRunPacket(w, pkt, run, head)
		return
	}
	head, err := d.checkResumable(dev, pkt.link, b)
	if err != nil || head.done {
		if head.done {
			_ = pkt.link.Send(proto.New(proto.TypeFluxTransfer, resumeMessage{Action: "done", ID: b.ID, Offset: head.record.Size}))
			d.endResumeRun(w, b.ID, nil)
			return
		}
		d.finishResumePacket(w, pkt, run, resumeMessage{}, err, false)
		return
	}
	d.processResumeRunPacket(w, pkt, run, head)
}

func (d *Daemon) processResumeRunPacket(w *resumeWorker, pkt resumePacket, run *resumeRun, head resumeHead) {
	b := pkt.message
	ctx, cancel := context.WithCancel(run.ctx)
	cancelOnLinkDown(ctx, pkt.link, cancel)
	reply, err := d.receiveResumable(ctx, pkt.dev, pkt.link, pkt.packet, b, run, head)
	stopped := ctx.Err() != nil
	cancel()
	d.finishResumePacket(w, pkt, run, reply, err, stopped)
}

func (d *Daemon) finishResumePacket(w *resumeWorker, pkt resumePacket, run *resumeRun, reply resumeMessage, err error, stopped bool) {
	b := pkt.message
	if (stopped || run.ctx.Err() != nil) && !run.published && (err == nil || errors.Is(err, context.Canceled)) {
		err = d.resumeStop(run)
	}
	if !run.published && (err == nil || errors.Is(err, errLinkDown) || errors.Is(err, errTransport)) && !d.stillPaired(run.dev) {
		err = errReceiveUnpaired
	}
	if err != nil {
		reply = resumeMessage{Action: "error", ID: b.ID, Error: err.Error()}
	}
	_ = pkt.link.Send(proto.New(proto.TypeFluxTransfer, reply))
	if (errors.Is(err, errLinkDown) || errors.Is(err, errTransport)) && run.ctx.Err() == nil && d.stillPaired(run.dev) && !run.published {
		return
	}
	if err != nil || reply.Action == "done" || stopped || run.ctx.Err() != nil {
		d.endResumeRun(w, b.ID, err)
	} else {
		run.last = time.Now()
		run.link = pkt.link
	}
}

// errLinkDown ends a resumable receive whose link closed. The partial data
// stays, so the sender can resume.
var errLinkDown = errors.New("the device disconnected")
var errTransport = errors.New("the transfer connection failed")

// resumeStop is the error of a run whose context ended: context.Canceled
// only for a user cancel, so finishTransfer labels a link drop or a daemon
// stop as failed, not canceled.
func (d *Daemon) resumeStop(run *resumeRun) error {
	d.mu.Lock()
	canceled := run.transfer.State == "canceled"
	d.mu.Unlock()
	if canceled {
		return context.Canceled
	}
	return errLinkDown
}

func (d *Daemon) sweepResumeRuns(w *resumeWorker) {
	d.mu.Lock()
	queued := make(map[string]time.Time, len(w.runs))
	for id := range w.runs {
		if times := w.queued[id]; len(times) > 0 {
			queued[id] = times[0]
		}
	}
	d.mu.Unlock()
	for id, run := range w.runs {
		if run.ctx.Err() != nil {
			d.endResumeRun(w, id, d.resumeStop(run))
		} else if !d.stillPaired(run.dev) {
			d.endResumeRun(w, id, errReceiveUnpaired)
		} else if time.Since(run.last) >= receiveIdle && (queued[id].IsZero() || !queued[id].Before(run.last.Add(receiveIdle))) {
			err := errStalled
			select {
			case <-run.link.Done():
				err = errLinkDown
			default:
			}
			d.endResumeRun(w, id, err)
		}
	}
	if afterSweep != nil {
		afterSweep()
	}
}

func (d *Daemon) endResumeRun(w *resumeWorker, id string, err error) {
	run := w.runs[id]
	if run == nil {
		return
	}
	run.cancel()
	d.mu.Lock()
	canceled := run.transfer.State == "canceled"
	if !canceled {
		_ = d.finishTransferLocked(run.transfer, err)
		received := err == nil && run.published
		d.mu.Unlock()
		d.finishTransferEmit(run.transfer, received)
	} else {
		d.trimTransfersLocked()
		d.mu.Unlock()
		d.markDirty()
		dir := filepath.Join(d.resumeDir, incomingFolder(run.dev.ID, id))
		meta := filepath.Join(dir, "state.json")
		var record incomingRecord
		if data, readErr := os.ReadFile(meta); readErr == nil && json.Unmarshal(data, &record) == nil {
			record.Canceled = true
			if saveErr := saveJSON(meta, record); saveErr != nil {
				d.logf("cancel transfer %s: %v", id, saveErr)
			}
		}
		_ = os.Remove(filepath.Join(dir, "data"))
	}
	d.endReceive(run.dev)
	delete(w.runs, id)
}

// incomingFolder is the name of the folder of one transfer of one device.
func incomingFolder(device, id string) string {
	key := sha256.Sum256([]byte(device + ":" + id))
	return hex.EncodeToString(key[:])
}

// pruneIncoming removes the folders older than before, except the folders in
// keep. With a device it looks only at the records of that device; with ""
// it looks at all folders, also those without a record.
func pruneIncoming(dir string, before time.Time, device string, keep map[string]bool) {
	entries, _ := os.ReadDir(dir)
	for _, entry := range entries {
		if !entry.IsDir() || !transferHash.MatchString(entry.Name()) || keep[entry.Name()] {
			continue
		}
		info, err := entry.Info()
		if err != nil || !info.ModTime().Before(before) {
			continue
		}
		if device != "" {
			if r, ok := readIncoming(filepath.Join(dir, entry.Name())); !ok || r.Device != device {
				continue
			}
		}
		_ = os.RemoveAll(filepath.Join(dir, entry.Name()))
	}
}

func readIncoming(folder string) (incomingRecord, bool) {
	var r incomingRecord
	data, err := os.ReadFile(filepath.Join(folder, "state.json"))
	return r, err == nil && json.Unmarshal(data, &r) == nil
}

// admitOffer checks the limits of a device before a new transfer gets a
// state: it refuses at maxPartials unfinished transfers, and it removes the
// oldest finished or canceled records when the device has maxRecords.
func (d *Daemon) admitOffer(device string, evict bool) error {
	type finished struct {
		folder string
		at     time.Time
	}
	var old []finished
	records, unfinished := 0, 0
	entries, _ := os.ReadDir(d.resumeDir)
	for _, entry := range entries {
		if !entry.IsDir() || !transferHash.MatchString(entry.Name()) {
			continue
		}
		folder := filepath.Join(d.resumeDir, entry.Name())
		r, ok := readIncoming(folder)
		if !ok || r.Device != device {
			continue
		}
		records++
		if r.Path == "" && !r.Canceled {
			unfinished++
		} else if info, err := entry.Info(); err == nil {
			old = append(old, finished{folder, info.ModTime()})
		}
	}
	if unfinished >= maxPartials {
		return errors.New("too many unfinished transfers from this device")
	}
	if evict {
		sort.Slice(old, func(i, j int) bool { return old[i].at.Before(old[j].at) })
		for _, f := range old {
			if records < maxRecords {
				break
			}
			_ = os.RemoveAll(f.folder)
			records--
		}
	}
	return nil
}

func (d *Daemon) refused(dev *Device, err error) {
	now := time.Now()
	d.mu.Lock()
	if d.resumeRefusals == nil {
		d.resumeRefusals = map[string]*refusalLog{}
	}
	entry := d.resumeRefusals[dev.ID]
	if entry == nil {
		entry = &refusalLog{}
		d.resumeRefusals[dev.ID] = entry
	}
	if !entry.last.IsZero() && now.Sub(entry.last) < receiveIdle {
		entry.skipped++
		d.mu.Unlock()
		return
	}
	name, skipped := dev.Name, entry.skipped
	entry.last, entry.skipped = now, 0
	d.mu.Unlock()
	if skipped > 0 {
		d.logf("resumable transfer from %s refused: %v (and %d more)", name, err, skipped)
	} else {
		d.logf("resumable transfer from %s refused: %v", name, err)
	}
}

// renameFile is os.Rename. Tests replace it to act as another file system.
var renameFile = os.Rename

type ctxReader struct {
	ctx context.Context
	r   io.Reader
}

func (r ctxReader) Read(b []byte) (int, error) {
	if err := r.ctx.Err(); err != nil {
		return 0, err
	}
	n, err := r.r.Read(b)
	if ctxErr := r.ctx.Err(); ctxErr != nil {
		return n, ctxErr
	}
	return n, err
}

type transportReader struct{ io.Reader }

func (r transportReader) Read(b []byte) (int, error) {
	n, err := r.Reader.Read(b)
	if err != nil && !errors.Is(err, io.EOF) {
		err = fmt.Errorf("%w: %w", errTransport, err)
	}
	return n, err
}

func (d *Daemon) checkResumable(dev *Device, l *lan.Link, b resumeMessage) (resumeHead, error) {
	var head resumeHead
	if err := os.MkdirAll(d.resumeDir, 0o700); err != nil {
		return head, err
	}
	if !transferHash.MatchString(b.Hash) || b.Size < 0 || b.Size > 1<<40 || safeName(b.Name) != b.Name || b.Name == "" {
		return head, errors.New("invalid transfer metadata")
	}
	d.mu.Lock()
	var peerErr error
	if !dev.Paired {
		peerErr = errReceiveUnpaired
	} else {
		select {
		case <-l.Done():
			peerErr = errLinkDown
		default:
			if dev.link != l {
				peerErr = errLinkDown
			} else {
				peerErr = d.ctx.Err()
			}
		}
	}
	d.mu.Unlock()
	if peerErr != nil {
		return head, peerErr
	}
	dir := filepath.Join(d.resumeDir, incomingFolder(dev.ID, b.ID))
	metaPath, part := filepath.Join(dir, "state.json"), filepath.Join(dir, "data")
	head.record = incomingRecord{Name: b.Name, Size: b.Size, Hash: b.Hash, Device: dev.ID}
	if data, err := os.ReadFile(metaPath); err == nil {
		var old incomingRecord
		if json.Unmarshal(data, &old) != nil || old.Name != head.record.Name || old.Size != head.record.Size || old.Hash != head.record.Hash {
			return head, errors.New("transfer metadata changed")
		}
		head.record, head.exists = old, true
		if old.Canceled {
			return head, errors.New("the transfer was canceled")
		}
		if old.Path != "" {
			head.done = true
			return head, nil
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return head, err
	} else if b.Action != "offer" {
		return head, errors.New("no offer for this transfer")
	}
	dataSize := int64(0)
	if stat, err := os.Stat(part); err == nil {
		dataSize = stat.Size()
	} else if !errors.Is(err, os.ErrNotExist) {
		return head, err
	}
	if dataSize > b.Size {
		return head, errors.New("partial file exceeds the transfer size")
	}
	if err := checkSpace(d.resumeDir, b.Size-dataSize); err != nil {
		return head, err
	}
	if !head.exists {
		if err := d.admitOffer(dev.ID, false); err != nil {
			return head, err
		}
	}
	return head, nil
}

func (d *Daemon) receiveResumable(ctx context.Context, dev *Device, l *lan.Link, p *proto.Packet, b resumeMessage, run *resumeRun, head resumeHead) (resumeMessage, error) {
	ack := resumeMessage{Action: "offset", ID: b.ID}
	dir := filepath.Join(d.resumeDir, incomingFolder(dev.ID, b.ID))
	metaPath, part := filepath.Join(dir, "state.json"), filepath.Join(dir, "data")
	touch := func() { _ = os.Chtimes(dir, time.Now(), time.Now()) }
	r := head.record
	if !head.exists {
		if err := d.admitOffer(dev.ID, true); err != nil {
			return ack, err
		}
		if err := saveJSON(metaPath, r); err != nil {
			return ack, err
		}
		touch()
	}
	checkPeer := func() error {
		d.mu.Lock()
		defer d.mu.Unlock()
		if !dev.Paired {
			return errReceiveUnpaired
		}
		if dev.link != l {
			return errLinkDown
		}
		return ctx.Err()
	}
	if err := checkPeer(); err != nil {
		return ack, err
	}
	f, err := os.OpenFile(part, os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return ack, err
	}
	defer f.Close()
	stat, err := f.Stat()
	if err != nil {
		return ack, err
	}
	offset := stat.Size()
	if offset > b.Size {
		return ack, errors.New("partial file exceeds the transfer size")
	}
	if b.Action == "chunk" {
		if b.Offset != offset || p.PayloadSize <= 0 || p.PayloadSize > transferChunk || p.PayloadSize > b.Size-offset {
			return ack, errors.New("invalid transfer chunk")
		}
		ctx, cancel := context.WithTimeout(ctx, chunkTimeout)
		defer cancel()
		rc, err := l.FetchPayload(ctx, p)
		if err != nil {
			return ack, fmt.Errorf("%w: %w", errTransport, err)
		}
		defer rc.Close()
		defer context.AfterFunc(ctx, func() { rc.Close() })()
		if _, err := f.Seek(offset, io.SeekStart); err != nil {
			return ack, err
		}
		progress := d.progress(run.transfer)
		src := &countingReader{r: transportReader{rc}, fn: func(n int64) { progress(offset + n) }, check: func() error {
			if !d.stillPaired(dev) {
				return errReceiveUnpaired
			}
			return nil
		}}
		n, err := io.CopyN(f, src, p.PayloadSize)
		// io.CopyN returns io.EOF for a payload that ended short; read errors
		// are already wrapped by transportReader.
		if err == io.EOF {
			err = fmt.Errorf("%w: %w", errTransport, err)
		}
		if syncErr := f.Sync(); err == nil {
			err = syncErr
		}
		if err != nil {
			return ack, err
		}
		offset += n
		touch()
	}
	ack.Offset = offset
	if offset != b.Size {
		return ack, nil
	}
	if _, err := f.Seek(0, io.SeekStart); err != nil {
		return ack, err
	}
	h := sha256.New()
	if _, err := io.Copy(h, ctxReader{ctx, f}); err != nil {
		return ack, err
	}
	if hex.EncodeToString(h.Sum(nil)) != b.Hash {
		_ = f.Truncate(0)
		return ack, errors.New("the file checksum does not match")
	}
	d.mu.Lock()
	destDir := d.cfg.DownloadPath()
	d.mu.Unlock()
	if err := os.MkdirAll(destDir, 0o755); err != nil {
		return ack, err
	}
	r.Path, err = uniquePath(ctx, filepath.Join(destDir, b.Name))
	if err != nil {
		return ack, err
	}
	published := false
	defer func() {
		if !published {
			_ = os.Remove(r.Path)
		}
	}()
	publish := func() error {
		d.mu.Lock()
		defer d.mu.Unlock()
		if !dev.Paired {
			return errReceiveUnpaired
		}
		if dev.link != l {
			return errLinkDown
		}
		// The link can close before its reader clears dev.link.
		select {
		case <-l.Done():
			return errLinkDown
		default:
		}
		if run.transfer.State == "canceled" {
			return context.Canceled
		}
		if err := ctx.Err(); err != nil {
			return err
		}
		run.transfer.State = "done"
		run.transfer.Done = run.transfer.Size
		return nil
	}
	if err := publish(); err != nil {
		return ack, err
	}
	err = renameFile(part, r.Path)
	if errors.Is(err, syscall.EXDEV) {
		d.mu.Lock()
		run.transfer.State = "active"
		d.mu.Unlock()
		if err := checkSpace(destDir, b.Size); err != nil {
			return ack, err
		}
		if _, err := f.Seek(0, io.SeekStart); err != nil {
			return ack, err
		}
		// err stays the outer error, so the result of the copy and the
		// rename below replaces the EXDEV of the first rename.
		var temp *os.File
		temp, err = os.CreateTemp(destDir, partPrefix+"*"+partSuffix)
		if err != nil {
			return ack, err
		}
		defer os.Remove(temp.Name())
		_, err = copyAcross(&spaceWriter{w: temp, dir: destDir, left: b.Size}, ctxReader{ctx, f})
		if err == nil {
			err = temp.Sync()
		}
		if closeErr := temp.Close(); err == nil {
			err = closeErr
		}
		if err != nil {
			return ack, err
		}
		if err := publish(); err != nil {
			return ack, err
		}
		err = os.Rename(temp.Name(), r.Path)
	}
	if err != nil {
		return ack, err
	}
	published = true
	if err := saveJSON(metaPath, r); err != nil {
		d.logf("save received transfer %s: %v", b.ID, err)
	}
	_ = os.Remove(part)
	d.mu.Lock()
	run.transfer.Path = r.Path
	d.mu.Unlock()
	run.published = true
	d.notify(desktop.Notification{AppName: "Flux", Title: "Received " + b.Name, Body: "Saved as " + r.Path,
		Actions: []desktop.Action{{Key: "open:" + r.Path, Label: "Open"}}})
	ack.Action = "done"
	return ack, nil
}
