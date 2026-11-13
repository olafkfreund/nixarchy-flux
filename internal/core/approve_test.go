package core

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"log"
	"math"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"flux/internal/config"
	"flux/internal/proto"
)

func approveDaemon() (*Daemon, *Device) {
	d := &Daemon{cfg: &config.Config{}, devices: map[string]*Device{}, logger: log.New(io.Discard, "", 0)}
	dev := &Device{ID: "phone1", Name: "Pixel 8", Paired: true}
	dev.Incoming = []string{proto.TypeFluxApprove}
	d.devices[dev.ID] = dev
	return d, dev
}

func approveRaw(t *testing.T, fields map[string]any) json.RawMessage {
	t.Helper()
	base := map[string]any{
		"device": "phone1", "host": "omarchy-xps", "user": "alice", "service": "sudo",
		"tty": "/dev/pts/3", "time": time.Now().Unix(), "nonce": strings.Repeat("ab", 32),
	}
	for k, v := range fields {
		base[k] = v
	}
	b, err := json.Marshal(base)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

// errCode returns the code of an API error, or "" for another error.
func errCode(err error) string {
	var e *Error
	if errors.As(err, &e) {
		return e.Code
	}
	return ""
}

func TestApproveRequestChecks(t *testing.T) {
	d, dev := approveDaemon()
	cases := []struct {
		name   string
		fields map[string]any
		code   string
	}{
		{"a newline in a field", map[string]any{"user": "alice\nservice=sshd"}, "bad_params"},
		{"a short nonce", map[string]any{"nonce": "abcd"}, "bad_params"},
		{"a negative time", map[string]any{"time": int64(math.MinInt64)}, "bad_params"},
		{"an unknown phone", map[string]any{"device": "other"}, "not_found"},
		{"an offline phone", nil, "offline"},
	}
	for _, c := range cases {
		_, err := d.ApproveRequest(approveRaw(t, c.fields))
		if errCode(err) != c.code {
			t.Errorf("%s: got %v, want the code %s", c.name, err, c.code)
		}
	}
	dev.Paired = false
	if _, err := d.ApproveRequest(approveRaw(t, nil)); errCode(err) != "not_paired" {
		t.Errorf("a phone that is not paired: %v", err)
	}
}

func TestApproveTimeoutLimits(t *testing.T) {
	d, _ := approveDaemon()
	for set, want := range map[int]int{0: 20, 1: 5, 30: 30, 900: 120} {
		d.cfg.ApproveTimeout = set
		if got := d.approveTimeout(); got != want {
			t.Errorf("approve_timeout %d: got %d, want %d", set, got, want)
		}
	}
}

func TestApprovalBook(t *testing.T) {
	var b approvalBook
	a := &approval{id: "a1", kind: "request", device: "phone1", deadline: time.Now().Add(time.Minute)}
	if err := b.add(a); err != nil {
		t.Fatal(err)
	}
	if err := b.add(&approval{id: "a2", kind: "request", device: "phone1", deadline: time.Now().Add(time.Minute)}); errCode(err) != "busy" {
		t.Fatalf("a second request for the same phone: %v", err)
	}

	// No answer yet: the wait returns pending after its slice.
	res, _, expired, err := b.wait(context.Background(), "a1", 20*time.Millisecond)
	if err != nil || expired || res.State != "pending" {
		t.Fatalf("pending: %+v %v %v", res, expired, err)
	}

	// Another phone and a wrong kind cannot answer.
	if b.deliver("phone2", "a1", approvalResult{State: "denied"}) {
		t.Fatal("another phone answered the request")
	}
	if b.deliver("phone1", "a1", approvalResult{State: "enrolled"}) {
		t.Fatal("an enrollment answered an approval")
	}
	if !b.deliver("phone1", "a1", approvalResult{State: "approved", Signature: "c2ln"}) {
		t.Fatal("the phone could not answer")
	}
	if b.deliver("phone1", "a1", approvalResult{State: "denied"}) {
		t.Fatal("the phone answered twice")
	}
	res, _, _, err = b.wait(context.Background(), "a1", time.Second)
	if err != nil || res.State != "approved" || res.Signature != "c2ln" {
		t.Fatalf("the answer: %+v %v", res, err)
	}
	if _, _, _, err := b.wait(context.Background(), "a1", time.Second); errCode(err) != "not_found" {
		t.Fatalf("a request after its answer: %v", err)
	}
}

func TestApprovalViewsAndCancel(t *testing.T) {
	d, _ := approveDaemon()
	stamp := time.Now()
	for _, a := range []*approval{
		{id: "sudo", kind: "request", device: "phone1", since: stamp, deadline: stamp.Add(time.Minute)},
		{id: "enroll", kind: "enroll", device: "phone2", deadline: stamp.Add(time.Minute)},
		{id: "expired", kind: "request", device: "phone3", deadline: stamp.Add(-time.Second)},
	} {
		if err := d.approvals.add(a); err != nil {
			t.Fatal(err)
		}
	}
	if got := d.approvals.views(); !slices.Equal(got, []ApprovalView{{Kind: "sudo", Device: "phone1", Since: stamp.Unix()}}) {
		t.Fatalf("sudo views: %+v", got)
	}
	s := d.sshSigns.add("phone4")
	if got := d.sshSigns.views(); len(got) != 0 {
		t.Fatalf("queued SSH view: %+v", got)
	}
	d.sshSigns.started(s.id)
	var state struct {
		Approvals []ApprovalView `json:"approvals"`
	}
	if err := json.Unmarshal(d.Snapshot(), &state); err != nil {
		t.Fatal(err)
	}
	if len(state.Approvals) != 2 || state.Approvals[0].Kind != "sudo" || state.Approvals[1].Kind != "ssh" {
		t.Fatalf("snapshot approvals: %+v", state.Approvals)
	}
	d.sshSigns.deliver("phone4", s.id, sshAnswer{denied: true})
	d.sshSigns.remove(s.id)
	d.approvals.deliver("phone1", "sudo", approvalResult{State: "denied"})
	if got := d.approvals.views(); len(got) != 0 {
		t.Fatalf("answered approval stayed visible: %+v", got)
	}
	d.approvals.cancel("sudo")
	d.approvals.remove("enroll")
	d.approvals.remove("expired")
	if err := json.Unmarshal(d.Snapshot(), &state); err != nil || state.Approvals == nil || len(state.Approvals) != 0 {
		t.Fatalf("empty approvals: %+v, %v", state.Approvals, err)
	}
}

func TestApprovalSnapshotConcurrent(t *testing.T) {
	d, _ := approveDaemon()
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		for range 100 {
			var state struct {
				Approvals []ApprovalView `json:"approvals"`
			}
			if err := json.Unmarshal(d.Snapshot(), &state); err != nil || state.Approvals == nil {
				t.Errorf("snapshot approvals: %+v, %v", state.Approvals, err)
				return
			}
		}
	}()
	for range 100 {
		id := newApprovalID()
		if err := d.approvals.add(&approval{id: id, kind: "request", device: "phone1", since: time.Now(), deadline: time.Now().Add(time.Minute)}); err != nil {
			t.Fatal(err)
		}
		d.approvals.cancel(id)
	}
	wg.Wait()
}

func TestApproveCancelWakesWait(t *testing.T) {
	d, dev := approveDaemon()
	a := &approval{id: "a1", kind: "request", device: dev.ID, since: time.Now(), deadline: time.Now().Add(time.Minute)}
	if err := d.approvals.add(a); err != nil {
		t.Fatal(err)
	}
	result := make(chan approvalResult, 1)
	go func() {
		v, _ := d.ApproveWait(context.Background(), a.id)
		if r, ok := v.(approvalResult); ok {
			result <- r
		}
	}()
	time.Sleep(10 * time.Millisecond)
	d.dropApprovals(dev.ID)
	select {
	case r := <-result:
		if r.State != "failed" || r.Error != "cancelled" {
			t.Fatalf("cancel result: %+v", r)
		}
	case <-time.After(100 * time.Millisecond):
		t.Fatal("cancel did not wake approve.wait within 100 ms")
	}
	if d.approvals.pending() != 0 {
		t.Fatal("cancel left a pending approval")
	}
}

func TestApprovalExpiryPublishes(t *testing.T) {
	d, dev, _, _ := sshSetup(t)
	d.mu.Lock()
	dev.Incoming = append(dev.Incoming, proto.TypeFluxApprove)
	d.cfg.ApproveTimeout = 5
	d.dirty = make(chan struct{}, 1)
	d.mu.Unlock()
	if _, err := d.ApproveRequest(approveRaw(t, map[string]any{"device": dev.ID})); err != nil {
		t.Fatal(err)
	}
	select {
	case <-d.dirty:
		if got := d.approvals.views(); len(got) != 0 {
			t.Fatalf("expired approval visible: %+v", got)
		}
	case <-time.After(6 * time.Second):
		t.Fatal("expiry did not publish to idle subscribers")
	}
}

func TestApprovalExpires(t *testing.T) {
	var b approvalBook
	if err := b.add(&approval{id: "a1", kind: "request", device: "phone1", deadline: time.Now().Add(30 * time.Millisecond)}); err != nil {
		t.Fatal(err)
	}
	_, device, expired, err := b.wait(context.Background(), "a1", time.Second)
	if !expired || device != "phone1" || errCode(err) != "timeout" {
		t.Fatalf("expired %v device %q err %v", expired, device, err)
	}
	// The phone is free for a new request.
	if err := b.add(&approval{id: "a2", kind: "request", device: "phone1", deadline: time.Now().Add(time.Minute)}); err != nil {
		t.Fatal(err)
	}
}

func TestHandleApprove(t *testing.T) {
	sig := base64.StdEncoding.EncodeToString([]byte("0123456789"))
	key := base64.StdEncoding.EncodeToString([]byte("key"))
	// The sizes of an RSA 2048 key of Windows Hello and its signature.
	rsaSig := base64.StdEncoding.EncodeToString(make([]byte, 256))
	rsaKey := base64.StdEncoding.EncodeToString(make([]byte, 294))
	longSig := base64.StdEncoding.EncodeToString(make([]byte, approveMaxSignature+1))
	longKey := base64.StdEncoding.EncodeToString(make([]byte, approveMaxPublicKey+1))
	cases := []struct {
		kind  string
		body  map[string]any
		state string
	}{
		{"request", map[string]any{"kind": "response", "denied": true}, "denied"},
		{"request", map[string]any{"kind": "response", "signature": sig}, "approved"},
		{"request", map[string]any{"kind": "response", "error": "No fingerprint is set up"}, "failed"},
		{"enroll", map[string]any{"kind": "enrolled", "signature": sig, "publicKey": key}, "enrolled"},
		{"request", map[string]any{"kind": "response", "signature": rsaSig}, "approved"},
		{"enroll", map[string]any{"kind": "enrolled", "signature": rsaSig, "publicKey": rsaKey}, "enrolled"},
		{"request", map[string]any{"kind": "response", "signature": longSig}, ""},
		{"enroll", map[string]any{"kind": "enrolled", "signature": rsaSig, "publicKey": longKey}, ""},
		{"request", map[string]any{"kind": "response", "signature": "not base64!"}, ""},
		{"request", map[string]any{"kind": "enrolled", "signature": sig, "publicKey": key}, ""},
	}
	for i, c := range cases {
		d, dev := approveDaemon()
		if err := d.approvals.add(&approval{id: "a1", kind: c.kind, device: dev.ID, deadline: time.Now().Add(time.Minute)}); err != nil {
			t.Fatal(err)
		}
		c.body["id"] = "a1"
		d.handleApprove(dev, proto.New(proto.TypeFluxApprove, c.body))
		res, _, _, err := d.approvals.wait(context.Background(), "a1", 10*time.Millisecond)
		if err != nil {
			t.Fatalf("case %d: %v", i, err)
		}
		want := c.state
		if want == "" {
			want = "pending"
		}
		if res.State != want {
			t.Errorf("case %d: state %q, want %q", i, res.State, want)
		}
	}
}
