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
	link    *lan.Link
	packet  *proto.Packet
	message resumeMessage
}

type resumeRun struct {
	transfer  *Transfer
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
}

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
		w = &resumeWorker{queue: make(chan resumePacket, 8), events: make(chan struct{}, 1), runs: map[string]*resumeRun{}}
		d.resumeWorkers[dev.ID] = w
		go d.runResumeWorker(dev, w)
	}
	select {
	case w.queue <- resumePacket{l, p, b}:
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
			d.sweepResumeRuns(dev, w)
			d.processResumePacket(dev, w, pkt)
			lastWork = time.Now()
		case <-w.events:
			d.sweepResumeRuns(dev, w)
		case <-tick.C:
			d.sweepResumeRuns(dev, w)
		case <-d.ctx.Done():
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
	if run == nil {
		if b.Action == "offer" {
			active := map[string]bool{}
			for id := range w.runs {
				active[incomingFolder(dev.ID, id)] = true
			}
			pruneIncoming(d.resumeDir, time.Now().Add(-7*24*time.Hour), dev.ID, active)
		}
		if !d.startReceive(dev) {
			_ = pkt.link.Send(proto.New(proto.TypeFluxTransfer, resumeMessage{Action: "error", ID: b.ID, Error: fmt.Sprintf("the device sends more than %d files at the same time", maxReceives)}))
			return
		}
		t := d.newTransfer(dev, b.Name, "", "in", b.Size)
		ctx, cancel := context.WithCancel(d.ctx)
		run = &resumeRun{transfer: t, ctx: ctx, cancel: cancel, link: pkt.link, last: time.Now()}
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
	}
	ctx, cancel := context.WithCancel(run.ctx)
	cancelOnLinkDown(ctx, pkt.link, cancel)
	reply, err := d.receiveResumable(ctx, dev, pkt.link, pkt.packet, b, run)
	stopped := ctx.Err() != nil
	if stopped && (err == nil || errors.Is(err, context.Canceled)) {
		err = d.resumeStop(run)
	}
	cancel()
	if err != nil {
		reply = resumeMessage{Action: "error", ID: b.ID, Error: err.Error()}
	}
	_ = pkt.link.Send(proto.New(proto.TypeFluxTransfer, reply))
	if err != nil || reply.Action == "done" || stopped || run.ctx.Err() != nil {
		d.endResumeRun(dev, w, b.ID, err)
	} else {
		run.last = time.Now()
	}
}

// errLinkDown ends a resumable receive whose link closed. The partial data
// stays, so the sender can resume.
var errLinkDown = errors.New("the device disconnected")

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

func (d *Daemon) sweepResumeRuns(dev *Device, w *resumeWorker) {
	for id, run := range w.runs {
		select {
		case <-run.link.Done():
			d.endResumeRun(dev, w, id, errLinkDown)
			continue
		default:
		}
		if run.ctx.Err() != nil {
			d.endResumeRun(dev, w, id, d.resumeStop(run))
		} else if time.Since(run.last) >= receiveIdle {
			d.endResumeRun(dev, w, id, errStalled)
		}
	}
}

func (d *Daemon) endResumeRun(dev *Device, w *resumeWorker, id string, err error) {
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
		dir := filepath.Join(d.resumeDir, incomingFolder(dev.ID, id))
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
	d.endReceive(dev)
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
func (d *Daemon) admitOffer(device string) error {
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
	sort.Slice(old, func(i, j int) bool { return old[i].at.Before(old[j].at) })
	for _, f := range old {
		if records < maxRecords {
			break
		}
		_ = os.RemoveAll(f.folder)
		records--
	}
	return nil
}

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

func (d *Daemon) receiveResumable(ctx context.Context, dev *Device, l *lan.Link, p *proto.Packet, b resumeMessage, run *resumeRun) (resumeMessage, error) {
	ack := resumeMessage{Action: "offset", ID: b.ID}
	if !transferHash.MatchString(b.Hash) || b.Size < 0 || b.Size > 1<<40 || safeName(b.Name) != b.Name || b.Name == "" {
		return ack, errors.New("invalid transfer metadata")
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
	if err := os.MkdirAll(d.resumeDir, 0o700); err != nil {
		return ack, err
	}
	dir := filepath.Join(d.resumeDir, incomingFolder(dev.ID, b.ID))
	metaPath, part := filepath.Join(dir, "state.json"), filepath.Join(dir, "data")
	touch := func() { _ = os.Chtimes(dir, time.Now(), time.Now()) }
	r := incomingRecord{Name: b.Name, Size: b.Size, Hash: b.Hash, Device: dev.ID}
	if data, err := os.ReadFile(metaPath); err == nil {
		var old incomingRecord
		if json.Unmarshal(data, &old) != nil || old.Name != r.Name || old.Size != r.Size || old.Hash != r.Hash {
			return ack, errors.New("transfer metadata changed")
		}
		r = old
		if r.Canceled {
			return ack, errors.New("the transfer was canceled")
		}
		if r.Path != "" {
			ack.Action, ack.Offset = "done", r.Size
			return ack, nil
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return ack, err
	} else if b.Action != "offer" {
		return ack, errors.New("no offer for this transfer")
	} else if err := d.admitOffer(dev.ID); err != nil {
		return ack, err
	} else if err := saveJSON(metaPath, r); err != nil {
		return ack, err
	} else {
		touch()
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
	if b.Action == "offer" || b.Action == "chunk" {
		if err := checkSpace(d.resumeDir, b.Size-offset); err != nil {
			return ack, err
		}
	}
	if b.Action == "chunk" {
		if b.Offset != offset || p.PayloadSize <= 0 || p.PayloadSize > transferChunk || p.PayloadSize > b.Size-offset {
			return ack, errors.New("invalid transfer chunk")
		}
		ctx, cancel := context.WithTimeout(ctx, time.Minute)
		defer cancel()
		rc, err := l.FetchPayload(ctx, p)
		if err != nil {
			return ack, err
		}
		defer rc.Close()
		defer context.AfterFunc(ctx, func() { rc.Close() })()
		if _, err := f.Seek(offset, io.SeekStart); err != nil {
			return ack, err
		}
		progress := d.progress(run.transfer)
		src := &countingReader{r: rc, fn: func(n int64) { progress(offset + n) }, check: func() error {
			if !d.stillPaired(dev) {
				return errReceiveUnpaired
			}
			return nil
		}}
		n, err := io.CopyN(f, src, p.PayloadSize)
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
	err = os.Rename(part, r.Path)
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
		temp, err := os.CreateTemp(destDir, partPrefix+"*"+partSuffix)
		if err != nil {
			return ack, err
		}
		defer os.Remove(temp.Name())
		_, err = io.Copy(&spaceWriter{w: temp, dir: destDir, left: b.Size}, ctxReader{ctx, f})
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
