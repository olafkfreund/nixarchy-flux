package core

import (
	"archive/zip"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"io"
	"log"
	"os"
	"path/filepath"
	"testing"
	"time"

	"flux/internal/config"
	"flux/internal/proto"
)

func TestOutboxRestartAndResumableDelivery(t *testing.T) {
	t.Setenv("XDG_DATA_HOME", t.TempDir())
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	onDesk, onPhone, deskID, phoneID := linkPair(t, ctx)
	dev := &Device{ID: phoneID, Name: "phone", Paired: true, Incoming: []string{proto.TypeFluxTransfer}}
	newDesk := func() *Daemon {
		d := &Daemon{ctx: ctx, cfg: &config.Config{}, devices: map[string]*Device{phoneID: dev},
			resumeReplies: map[string]chan resumeMessage{}, logger: log.New(io.Discard, "", 0)}
		if err := d.loadOutbox(); err != nil {
			t.Fatal(err)
		}
		return d
	}
	a := newDesk()
	source := filepath.Join(t.TempDir(), "sample.bin")
	want := bytes.Repeat([]byte("resume-data"), transferChunk/2)
	if err := os.WriteFile(source, want, 0o600); err != nil {
		t.Fatal(err)
	}
	ts, err := a.SendFiles(dev, []string{source})
	if err != nil || len(ts) != 1 {
		t.Fatalf("queue: %v, %v", ts, err)
	}
	if ts[0].State != "waiting" {
		t.Fatal(ts[0].State)
	}
	if err := os.WriteFile(source, []byte("changed"), 0o600); err != nil {
		t.Fatal(err)
	}
	a = newDesk()
	if len(a.transfers) != 1 {
		t.Fatal("the restart lost the outbox")
	}
	dev.link = onDesk
	peer := &Device{ID: deskID, Name: "desk", Paired: true, link: onPhone}
	downloads := t.TempDir()
	b := &Daemon{ctx: ctx, cfg: &config.Config{DownloadDir: downloads}, devices: map[string]*Device{deskID: peer},
		resumeDir: t.TempDir(), logger: log.New(io.Discard, "", 0)}
	replies := make(chan resumeMessage, 4)
	go onDesk.Receive(func(p *proto.Packet) {
		// a handles the packet first, so a reply that the test reads has
		// already reached a's sender, and a stale one cannot arrive later.
		a.handlePacket(dev, onDesk, p)
		var reply resumeMessage
		if p.Type == proto.TypeFluxTransfer && p.Decode(&reply) == nil {
			select {
			case replies <- reply:
			default:
			}
		}
	})
	go onPhone.Receive(func(p *proto.Packet) { b.handlePacket(peer, onPhone, p) })
	offerReply := func(offer resumeMessage) resumeMessage {
		b.handleResumable(peer, onPhone, proto.New(proto.TypeFluxTransfer, offer))
		select {
		case reply := <-replies:
			return reply
		case <-time.After(5 * time.Second):
			t.Fatal("resumable offer reply timed out")
			return resumeMessage{}
		}
	}
	j := a.outbox.jobs[ts[0].ID]
	// Restore a partial file left by a previous receiver process.
	offer := resumeMessage{Action: "offer", ID: j.ID, Name: j.Name, Size: j.Size, Hash: j.Hash}
	if reply := offerReply(offer); reply.Action != "offset" || reply.Offset != 0 {
		t.Fatal(reply)
	}
	key := sha256.Sum256([]byte(deskID + ":" + j.ID))
	part := filepath.Join(b.resumeDir, hex.EncodeToString(key[:]), "data")
	if err := os.WriteFile(part, want[:4096], 0o600); err != nil {
		t.Fatal(err)
	}
	resume := offerReply(offer)
	if resume.Action != "offset" || resume.Offset != 4096 {
		t.Fatalf("resume: %+v", resume)
	}
	a.sendOutbox(ctx, j)
	got, err := os.ReadFile(filepath.Join(downloads, "sample.bin"))
	if err != nil || !bytes.Equal(got, want) {
		t.Fatalf("delivery: %d bytes, %v", len(got), err)
	}
	if len(a.outbox.jobs) != 0 || a.transfers[0].State != "done" {
		t.Fatal("delivery was not acknowledged")
	}
	// A lost final acknowledgement must not create a second destination.
	for len(replies) > 0 {
		<-replies
	}
	ack := offerReply(offer)
	if ack.Action != "done" {
		t.Fatalf("repeat: %+v", ack)
	}
	files, _ := os.ReadDir(downloads)
	if len(files) != 1 {
		t.Fatal("duplicate delivery")
	}
}

func TestFolderArchive(t *testing.T) {
	root := filepath.Join(t.TempDir(), "project")
	if err := os.MkdirAll(filepath.Join(root, "empty"), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "hello.txt"), []byte("hello"), 0o600); err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	if err := zipFolder(context.Background(), root, &out); err != nil {
		t.Fatal(err)
	}
	z, err := zip.NewReader(bytes.NewReader(out.Bytes()), int64(out.Len()))
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]bool{"project/": true, "project/empty/": true, "project/hello.txt": true}
	for _, f := range z.File {
		if !want[f.Name] {
			t.Fatal(f.Name)
		}
		delete(want, f.Name)
	}
	if len(want) != 0 {
		t.Fatal(want)
	}
	if err := os.Symlink("/etc", filepath.Join(root, "link")); err != nil {
		t.Fatal(err)
	}
	if err := zipFolder(context.Background(), root, io.Discard); err == nil {
		t.Fatal("archived a symbolic link")
	}
}
