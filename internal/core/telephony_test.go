package core

import (
	"context"
	"encoding/json"
	"io"
	"log"
	"slices"
	"sync"
	"testing"
	"time"

	"flux/internal/config"
	"flux/internal/desktop"
	"flux/internal/lan"
	"flux/internal/proto"
)

// fakeMedia is a set of desktop players that records each action.
type fakeMedia struct {
	players []desktop.Player
	actions []string
}

func (f *fakeMedia) Players() []desktop.Player { return slices.Clone(f.players) }

func (f *fakeMedia) Action(name, action string) error {
	f.actions = append(f.actions, name+":"+action)
	for i := range f.players {
		if f.players[i].Name == name {
			f.players[i].Playing = action == "Play"
		}
	}
	return nil
}

func (f *fakeMedia) set(name string, playing bool) {
	for i := range f.players {
		if f.players[i].Name == name {
			f.players[i].Playing = playing
		}
	}
}

func TestCallPausePausesOnlyPlayingPlayers(t *testing.T) {
	m := &fakeMedia{players: []desktop.Player{{Name: "spotify", Playing: true}, {Name: "mpv"}, {Name: "firefox", Playing: true}}}
	var c callPause
	c.pause(m)
	if !slices.Equal(m.actions, []string{"spotify:Pause", "firefox:Pause"}) {
		t.Fatalf("pause actions: %v", m.actions)
	}
	// A second ring, or ringing then talking, pauses nothing again.
	c.pause(m)
	if len(m.actions) != 2 {
		t.Fatalf("second pause: %v", m.actions)
	}
	m.actions = nil
	c.resume(m)
	if !slices.Equal(m.actions, []string{"spotify:Play", "firefox:Play"}) {
		t.Fatalf("resume actions: %v", m.actions)
	}
	if len(c.paused) != 0 {
		t.Fatalf("the list must be empty after the call: %v", c.paused)
	}
}

func TestCallResumeLeavesChangedPlayers(t *testing.T) {
	m := &fakeMedia{players: []desktop.Player{{Name: "spotify", Playing: true}, {Name: "vlc", Playing: true}}}
	var c callPause
	c.pause(m)
	// During the call, the user plays spotify again and closes vlc.
	m.set("spotify", true)
	m.players = m.players[:1]
	m.actions = nil
	c.resume(m)
	if len(m.actions) != 0 {
		t.Fatalf("resume must not touch a player that plays or closed: %v", m.actions)
	}
}

func telephonyPacket(body map[string]any) *proto.Packet {
	return proto.New(proto.TypeTelephony, body)
}

func TestHandleTelephony(t *testing.T) {
	m := &fakeMedia{players: []desktop.Player{{Name: "spotify", Playing: true}, {Name: "mpv"}}}
	d := &Daemon{cfg: &config.Config{PauseMediaOnCall: true}, logger: log.New(io.Discard, "", 0), callPlayers: m}
	dev := &Device{ID: "p1", Name: "Pixel 8", Paired: true}

	d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "ringing", "phoneNumber": "+4712345678"}))
	d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "talking", "phoneNumber": "+4712345678"}))
	waitIdle(t, d, &d.content.mediaQ)
	if !slices.Equal(m.actions, []string{"spotify:Pause"}) {
		t.Fatalf("ringing and talking: %v", m.actions)
	}
	// The phone sends isCancel as a boolean or as a string.
	d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "talking", "isCancel": "true"}))
	waitIdle(t, d, &d.content.mediaQ)
	if !slices.Equal(m.actions, []string{"spotify:Pause", "spotify:Play"}) {
		t.Fatalf("after the call: %v", m.actions)
	}

	// With pause_media_on_call off, a call leaves the players alone.
	d.cfg.PauseMediaOnCall = false
	m.actions = nil
	d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "ringing"}))
	d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "ringing", "isCancel": true}))
	waitIdle(t, d, &d.content.mediaQ)
	if len(m.actions) != 0 {
		t.Fatalf("pause_media_on_call off: %v", m.actions)
	}
}

func TestCallViews(t *testing.T) {
	d, dev := approveDaemon()
	current := time.Unix(1_700_000_000, 0)
	now = func() time.Time { return current }
	t.Cleanup(func() { now = time.Now })
	d.callEvent(dev, nil, callBody{Event: "ringing", ContactName: "  Mum  ", PhoneNumber: "123"})
	first := dev.view().Call
	if first == nil || first.State != "ringing" || first.Caller != "Mum" || first.Number != "123" {
		t.Fatalf("ringing: %+v", first)
	}
	current = current.Add(time.Minute)
	d.callEvent(dev, nil, callBody{Event: "ringing", ContactName: "Mum Smith", PhoneNumber: "123"})
	d.callEvent(dev, nil, callBody{Event: "talking", ContactName: "Mum Smith", PhoneNumber: "123"})
	current = current.Add(time.Minute)
	d.callEvent(dev, nil, callBody{Event: "talking", ContactName: "Mum Smith", PhoneNumber: "123"})
	if got := dev.view().Call; got == nil || got.State != "talking" || got.Since != first.Since || got.Caller != "Mum Smith" || first.State != "ringing" {
		t.Fatalf("talking: %+v", got)
	}
	d.callEvent(dev, nil, callBody{Event: "talking", IsCancel: "true"})
	if got := dev.view().Call; got != nil {
		t.Fatalf("cancel: %+v", got)
	}
	d.callEvent(dev, nil, callBody{Event: "talking", PhoneNumber: "456"})
	if got := dev.view().Call; got == nil || got.Since == 0 || got.Number != "456" {
		t.Fatalf("outgoing: %+v", got)
	}
	d.callEvent(dev, nil, callBody{Event: "missedCall", ContactName: "Mum", PhoneNumber: "123"})
	d.callEvent(dev, nil, callBody{Event: "ringing", IsCancel: "true"})
	if v := dev.view(); v.Call != nil || v.MissedCall == nil || v.MissedCall.Caller != "Mum" {
		t.Fatalf("missed and trailing cancel: %+v", v)
	}
	d.calls[dev.ID].link = &lan.Link{}
	d.dropCall(dev, d.calls[dev.ID].link)
	if dev.view().MissedCall == nil {
		t.Fatal("link drop cleared missed call")
	}
	d.callEvent(dev, nil, callBody{Event: "ringing"})
	if dev.view().MissedCall != nil {
		t.Fatal("new call kept missed call")
	}
}

func TestCallFieldsAndEndReasons(t *testing.T) {
	d, dev := approveDaemon()
	base := time.Unix(1_700_000_000, 0)
	now = func() time.Time { return base }
	t.Cleanup(func() { now = time.Now })
	handsFree := []string{"p620"}
	d.callEvent(dev, nil, callBody{Event: "ringing", CallID: "one", HandsFree: handsFree, Waiting: true})
	handsFree[0] = "changed"
	v := dev.view()
	if v.Call == nil || v.Call.ID != "one" || !v.Call.Waiting || !slices.Equal(v.Call.HandsFree, []string{"p620"}) {
		t.Fatalf("call fields: %+v", v.Call)
	}
	v.Call.HandsFree[0] = "changed"
	if dev.view().Call.HandsFree[0] != "p620" {
		t.Fatal("snapshot shares handsFree")
	}
	d.callEvent(dev, nil, callBody{Event: "ringing", IsCancel: "true"})
	if got := dev.view().LastCall; got == nil || got.ID != "one" || got.End != "missed" {
		t.Fatalf("ring cancel: %+v", got)
	}
	d.callEvent(dev, nil, callBody{Event: "missedCall", CallID: "one"})
	if got := dev.view().MissedCall; got == nil || got.ID != "one" {
		t.Fatalf("missed id: %+v", got)
	}
	d.callEvent(dev, nil, callBody{Event: "ringing", CallID: "two"})
	d.callEvent(dev, nil, callBody{Event: "talking", CallID: "two"})
	d.callEvent(dev, nil, callBody{Event: "talking", IsCancel: "true"})
	if got := dev.view().LastCall; got == nil || got.ID != "two" || got.End != "hangup" {
		t.Fatalf("talking cancel: %+v", got)
	}
	d.callEvent(dev, nil, callBody{Event: "ringing", CallID: "three"})
	l := &lan.Link{}
	d.calls[dev.ID].link = l
	d.dropCall(dev, l)
	if got := dev.view().LastCall; got == nil || got.ID != "three" || got.End != "lost" {
		t.Fatalf("link drop: %+v", got)
	}
	d.callEvent(dev, nil, callBody{Event: "ringing"})
	if got := dev.view().Call; got == nil || got.ID != "" || got.Waiting || len(got.HandsFree) != 0 {
		t.Fatalf("old packet: %+v", got)
	}
	now = func() time.Time { return base.Add(30 * time.Second) }
	if dev.view().LastCall != nil {
		t.Fatal("lastCall stayed after 30 seconds")
	}
}

func TestCallActionMethod(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	onDesk, onPhone, _, phoneID := linkPair(t, ctx)
	got := make(chan *proto.Packet, 2)
	go onPhone.Receive(func(p *proto.Packet) { got <- p })
	d := &Daemon{cfg: &config.Config{}, devices: map[string]*Device{}, logger: log.New(io.Discard, "", 0), ctx: ctx}
	dev := &Device{ID: phoneID, Name: "Pixel 8", Paired: true, link: onDesk, callOK: true,
		call: CallView{ID: "current", State: "ringing"}}
	d.devices[phoneID] = dev
	call := func(id, action string) string {
		b, _ := json.Marshal(map[string]string{"device": phoneID, "id": id, "action": action})
		_, err := d.Call(ctx, "call.action", b)
		return errCode(err)
	}
	if got := call("current", "answer"); got != "not_supported" {
		t.Fatalf("capability: %q", got)
	}
	d.mu.Lock()
	dev.Incoming = []string{proto.TypeFluxTelephonyAction}
	d.mu.Unlock()
	if !slices.Contains(dev.plugins(), "telephony.action") {
		t.Fatal("capability absent from plugins")
	}
	for _, tc := range []struct {
		name, id, action, state string
		waiting, callOK         bool
	}{
		{"empty id", "", "answer", "ringing", false, true},
		{"wrong id", "other", "answer", "ringing", false, true},
		{"unknown action", "current", "hangup", "ringing", false, true},
		{"waiting", "current", "answer", "ringing", true, true},
		{"talking", "current", "answer", "talking", false, true},
		{"idle", "current", "answer", "ringing", false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			d.mu.Lock()
			dev.call.State, dev.call.Waiting, dev.callOK = tc.state, tc.waiting, tc.callOK
			d.mu.Unlock()
			if code := call(tc.id, tc.action); code == "" {
				t.Fatal("refused call action succeeded")
			}
			select {
			case p := <-got:
				t.Fatalf("refused action sent %+v", p)
			default:
			}
		})
	}
	d.mu.Lock()
	dev.call.State, dev.call.Waiting, dev.callOK = "ringing", false, true
	d.mu.Unlock()
	for _, action := range []string{"answer", "reject"} {
		if code := call("current", action); code != "" {
			t.Fatalf("%s: %q", action, code)
		}
		select {
		case p := <-got:
			var body struct{ Action, ID string }
			if p.Type != proto.TypeFluxTelephonyAction || p.Decode(&body) != nil || body.Action != action || body.ID != "current" {
				t.Fatalf("sent %+v, body %+v", p, body)
			}
		case <-time.After(5 * time.Second):
			t.Fatal("no telephony action")
		}
	}
	d.mu.Lock()
	dev.link = nil
	d.mu.Unlock()
	if got := call("current", "answer"); got != "offline" {
		t.Fatalf("offline: %q", got)
	}
	d.mu.Lock()
	dev.Paired = false
	d.mu.Unlock()
	if got := call("current", "answer"); got != "not_paired" {
		t.Fatalf("unpaired: %q", got)
	}
}

func TestMissedCallAgeing(t *testing.T) {
	d, dev := approveDaemon()
	base := time.Unix(1_700_000_000, 0)
	now = func() time.Time { return base }
	t.Cleanup(func() { now = time.Now })
	d.callEvent(dev, nil, callBody{Event: "missedCall"})
	if dev.view().MissedCall == nil {
		t.Fatal("fresh missed call omitted")
	}
	now = func() time.Time { return base.Add(10 * time.Minute) }
	if dev.view().MissedCall != nil {
		t.Fatal("old missed call visible")
	}
}

func TestCallSnapshotConcurrentUnpair(t *testing.T) {
	d, dev := approveDaemon()
	t.Setenv("XDG_DATA_HOME", t.TempDir())
	var err error
	d.trust, err = config.LoadTrust()
	if err != nil {
		t.Fatal(err)
	}
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		for i := 0; i < 100; i++ {
			var snapshot struct {
				Devices []DeviceView `json:"devices"`
			}
			if err := json.Unmarshal(d.Snapshot(), &snapshot); err != nil {
				t.Errorf("snapshot: %v", err)
				return
			}
			for _, v := range snapshot.Devices {
				if !v.Paired && (v.Call != nil || v.MissedCall != nil || v.LastCall != nil) {
					t.Errorf("unpaired call data: %+v", v)
				}
			}
		}
	}()
	for i := 0; i < 100; i++ {
		d.callEvent(dev, nil, callBody{Event: "ringing"})
		d.callEvent(dev, nil, callBody{Event: "talking"})
	}
	d.callEvent(dev, nil, callBody{Event: "missedCall"})
	d.mu.Lock()
	_, err = d.dropTrustLocked(dev)
	d.mu.Unlock()
	if err != nil {
		t.Fatal(err)
	}
	wg.Wait()
	if v := dev.view(); v.Call != nil || v.MissedCall != nil || v.LastCall != nil {
		t.Fatalf("unpair kept call data: %+v", v)
	}
}

// TestDropCall checks that the call state ends when the link that reported
// the call drops, so that a later call does not play players that it did
// not pause.
func TestDropCall(t *testing.T) {
	m := &fakeMedia{players: []desktop.Player{{Name: "spotify", Playing: true}}}
	d := &Daemon{cfg: &config.Config{PauseMediaOnCall: true}, logger: log.New(io.Discard, "", 0), callPlayers: m}
	dev := &Device{ID: "p1", Name: "Pixel 8", Paired: true}
	first, second := &lan.Link{}, &lan.Link{}
	d.callEvent(dev, nil, callBody{Event: "ringing"})
	d.calls[dev.ID].link = first

	// A link that did not report the call changes nothing.
	d.dropCall(dev, second)
	if d.calls[dev.ID] == nil {
		t.Fatal("the drop of another link ended the call")
	}
	d.dropCall(dev, first)
	if d.calls[dev.ID] != nil {
		t.Fatal("the call state stays after its link dropped")
	}
	// spotify stays paused. The end of the next call does not play it,
	// because that call did not pause it.
	m.actions = nil
	d.callEvent(dev, nil, callBody{Event: "talking", IsCancel: "true"})
	if len(m.actions) != 0 {
		t.Fatalf("the end of a new call ran %v", m.actions)
	}
}

// stuckMedia is a set of players that does not answer until release
// closes, like a stopped mpv that keeps its bus name.
type stuckMedia struct{ release chan struct{} }

func (s *stuckMedia) Players() []desktop.Player { <-s.release; return nil }

func (s *stuckMedia) Action(string, string) error { <-s.release; return nil }

// TestTelephonyDoesNotBlockReadLoop checks that a call event returns at
// once while the players do not answer. The read loop of the phone then
// handles the next packets, such as an approval answer.
func TestTelephonyDoesNotBlockReadLoop(t *testing.T) {
	m := &stuckMedia{release: make(chan struct{})}
	d := &Daemon{cfg: &config.Config{PauseMediaOnCall: true}, logger: log.New(io.Discard, "", 0), callPlayers: m}
	dev := &Device{ID: "p1", Name: "Pixel 8", Paired: true}
	done := make(chan struct{})
	go func() {
		defer close(done)
		d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "ringing"}))
		d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "ringing", "isCancel": true}))
	}()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("a call event waits for the players")
	}
	close(m.release)
	waitIdle(t, d, &d.content.mediaQ)
}

func TestCaller(t *testing.T) {
	cases := []struct {
		b    callBody
		want string
	}{
		{callBody{ContactName: "Mom", PhoneNumber: "+47 123"}, "Mom"},
		{callBody{PhoneNumber: "+47 123"}, "+47 123"},
		{callBody{ContactName: "  "}, "Unknown caller"},
	}
	for _, c := range cases {
		if got := c.b.caller(); got != c.want {
			t.Errorf("%+v: got %q, want %q", c.b, got, c.want)
		}
	}
}

// TestCallEventNeedsPairedLink checks that a call event that waits on the
// media worker does nothing after an unpair or after its link changed.
func TestCallEventNeedsPairedLink(t *testing.T) {
	m := &fakeMedia{players: []desktop.Player{{Name: "spotify", Playing: true}}}
	d := &Daemon{cfg: &config.Config{PauseMediaOnCall: true}, logger: log.New(io.Discard, "", 0), callPlayers: m}
	old := &lan.Link{}
	dev := &Device{ID: "p1", Name: "Pixel 8", Paired: true}
	// The phone connected again, so the event of the old link is old.
	d.callEvent(dev, old, callBody{Event: "ringing"})
	dev.Paired = false
	d.callEvent(dev, nil, callBody{Event: "ringing"})
	if len(m.actions) != 0 || d.calls[dev.ID] != nil {
		t.Fatalf("actions %v, call state %+v", m.actions, d.calls[dev.ID])
	}
}

// slowMedia is a set of players that reports each Players call and then
// answers late.
type slowMedia struct{ called chan struct{} }

func (s *slowMedia) Players() []desktop.Player {
	s.called <- struct{}{}
	time.Sleep(50 * time.Millisecond)
	return nil
}

func (s *slowMedia) Action(string, string) error { return nil }

// TestCallEventReadsNameUnderLock checks that a call event on the media
// worker reads the device name under the lock, while a new identity of the
// phone changes the name. Run it with -race.
func TestCallEventReadsNameUnderLock(t *testing.T) {
	m := &slowMedia{called: make(chan struct{}, 1)}
	d := &Daemon{cfg: &config.Config{PauseMediaOnCall: true}, logger: log.New(io.Discard, "", 0), callPlayers: m}
	dev := &Device{ID: "p1", Name: "Pixel 8", Paired: true}
	d.handleTelephony(dev, telephonyPacket(map[string]any{"event": "ringing"}))
	<-m.called
	d.mu.Lock()
	dev.setIdentity(proto.Identity{DeviceName: "Pixel 9"})
	d.mu.Unlock()
	waitIdle(t, d, &d.content.mediaQ)
}
