package core

import (
	"context"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"flux/internal/herdr"
	"flux/internal/proto"
)

func TestHerdrQualify(t *testing.T) {
	if got := herdrQualify("", "w1:p1"); got != "w1:p1" {
		t.Errorf("default: %q", got)
	}
	if got := herdrQualify("work", "w1:p1"); got != "work/w1:p1" {
		t.Errorf("named: %q", got)
	}
	if s, id := herdrSplit("work/w1:p1"); s != "work" || id != "w1:p1" {
		t.Errorf("split named: %q %q", s, id)
	}
	if s, id := herdrSplit("w1:p1"); s != "" || id != "w1:p1" {
		t.Errorf("split default: %q %q", s, id)
	}
	if herdrSessionName("default") != "" || herdrSessionName("") != "" || herdrSessionName("work") != "work" {
		t.Error("session names")
	}
}

func TestHerdrSessionsFindsSockets(t *testing.T) {
	dir := t.TempDir()
	newFakeHerdrAt(t, herdr.SessionSocket(dir, "work"))
	skip := newFakeHerdrAt(t, herdr.SessionSocket(dir, "main"))
	// A folder without a socket and a file in place of the socket are no
	// sessions.
	if err := os.MkdirAll(filepath.Join(dir, "stopped"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(dir, "plain"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(herdr.SessionSocket(dir, "plain"), nil, 0o600); err != nil {
		t.Fatal(err)
	}
	if got := herdr.Sessions(dir, skip.path); !slices.Equal(got, []string{"work"}) {
		t.Errorf("sessions %v", got)
	}
	if got := herdr.Sessions(filepath.Join(dir, "missing"), ""); got != nil {
		t.Errorf("missing folder: %v", got)
	}
}

func TestHerdrFollowsNamedSessions(t *testing.T) {
	root := t.TempDir()
	sessions := filepath.Join(root, "sessions")
	main := newFakeHerdrAt(t, filepath.Join(root, "herdr.sock"))
	work := newFakeHerdrAt(t, herdr.SessionSocket(sessions, "work"))
	snap := `{"protocol":22,"workspaces":[{"workspace_id":"w1","label":"LABEL","number":1}],` +
		`"panes":[{"pane_id":"w1:p1","workspace_id":"w1"},{"pane_id":"w1:p2","workspace_id":"w1"}],` +
		`"agents":[{"pane_id":"w1:p1","workspace_id":"w1","agent":"claude","agent_status":"idle"}]}`
	main.set(strings.ReplaceAll(snap, "LABEL", "home"))
	work.set(strings.ReplaceAll(snap, "LABEL", "job"))

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	d := herdrDaemon(ctx, main.path)
	d.herdrSessionsDir = sessions
	d.cfg.HerdrControl, d.cfg.HerdrTerminals = true, true
	dev := &Device{ID: "phone1", Name: "Pixel 8", Paired: true}
	done := make(chan struct{})
	go func() {
		d.herdrLoop(ctx)
		close(done)
	}()
	waitFor(t, "both sessions", func() bool {
		_, agents := d.herdrStatus()
		return len(agents) == 2
	})
	if names := d.herdrSessionNames(); !slices.Equal(names, []string{"work"}) {
		t.Fatalf("named sessions %v", names)
	}

	// An older phone sees the default session only, with the IDs of
	// herdr. The sessions list has each session, with qualified IDs.
	d.mu.Lock()
	v := d.herdrViewForLocked(dev.ID)
	d.mu.Unlock()
	if len(v.Agents) != 1 || v.Agents[0].Pane != "w1:p1" || len(v.Panes) != 1 || v.Panes[0].Pane != "w1:p2" ||
		len(v.Workspaces) != 1 || v.Workspaces[0].ID != "w1" {
		t.Fatalf("default view %+v", v)
	}
	if len(v.Sessions) != 2 {
		t.Fatalf("sessions %+v", v.Sessions)
	}
	def, named := v.Sessions[0], v.Sessions[1]
	if def.Name != "default" || !def.Default || !def.Running || len(def.Agents) != 1 || def.Agents[0].Pane != "w1:p1" {
		t.Errorf("default session %+v", def)
	}
	if named.Name != "work" || named.Default || !named.Running || len(named.Agents) != 1 ||
		named.Agents[0].Pane != "work/w1:p1" || named.Panes[0].Pane != "work/w1:p2" ||
		named.Workspaces[0].ID != "work/w1" || named.Workspaces[0].Label != "job" {
		t.Errorf("named session %+v", named)
	}

	// A reply and a read of a qualified pane go to the socket of its
	// session, with the ID of herdr.
	main.takeCalls()
	work.takeCalls()
	if body := sentBody(t, d.herdrKeys(dev, "work/w1:p1", []string{"enter"})); body["error"] != nil || body["pane"] != "work/w1:p1" {
		t.Fatalf("keys: %v", body)
	}
	work.read = `"result":{"type":"pane_read","read":{"pane_id":"w1:p1","text":"from work","truncated":false}}`
	if body := outputBody(t, d.readHerdr("work/w1:p1", 10, false)); body["text"] != "from work" {
		t.Errorf("read: %v", body)
	}
	if calls := main.takeCalls(); len(calls) != 0 {
		t.Errorf("the default session got %v", calls)
	}
	calls := work.takeCalls()
	if len(calls) != 2 || calls[0] != `agent.send_keys {"keys":["enter"],"target":"w1:p1"}` ||
		!strings.Contains(calls[1], `"target":"w1:p1"`) {
		t.Errorf("work calls %v", calls)
	}

	// A terminal without a workspace opens in the session of the phone.
	work.mu.Lock()
	work.replies = map[string]string{
		"workspace.create": `"result":{"type":"workspace_created","workspace":{"workspace_id":"w2"},"root_pane":{"pane_id":"w2:p1"}}`,
	}
	work.mu.Unlock()
	work.set(`{"protocol":22,"workspaces":[],"panes":[{"pane_id":"w2:p1","workspace_id":"w2"}],"agents":[]}`)
	body := createdBody(t, d.herdrCreate(dev, "terminal", "", "~", "", "work"), "created")
	if body["error"] != nil || body["pane"] != "work/w2:p1" {
		t.Fatalf("terminal: %v", body)
	}
	if calls := main.takeCalls(); len(calls) != 0 {
		t.Errorf("the default session got %v", calls)
	}
	body = createdBody(t, d.herdrCreate(dev, "terminal", "", "~", "", "gone"), "created")
	if body["error"] != "The herdr session gone does not run" {
		t.Errorf("unknown session: %v", body)
	}

	// A session that goes away leaves the state.
	if err := os.RemoveAll(filepath.Join(sessions, "work")); err != nil {
		t.Fatal(err)
	}
	d.wakeHerdr()
	waitFor(t, "the removed session", func() bool {
		_, agents := d.herdrStatus()
		return len(agents) == 1 && agents[0].Pane == "w1:p1" && len(d.herdrSessionNames()) == 0
	})

	cancel()
	<-done
}

func TestHerdrNamedLive(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	d, main := terminalDaemon(ctx, t, bridgeEcho)
	d.herdrSessionsDir = filepath.Join(t.TempDir(), "sessions")
	work := newFakeHerdrAt(t, herdr.SessionSocket(d.herdrSessionsDir, "work"))
	client, err := net.Listen("unix", herdr.ClientSocketPath(work.path))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { client.Close() })
	work.setReply("pane.layout", `"result":{"type":"pane_layout","layout":{"tab_id":"w1:t1",`+
		`"panes":[{"pane_id":"w1:p1","rect":{"x":0,"y":0,"width":96,"height":32}}]}}`)
	// The bridge must receive the named socket and the unqualified pane.
	d.herdrBin = fakeBridge(t, fmt.Sprintf("test \"$HERDR_SOCKET_PATH\" = %q || exit 1\n", work.path)+
		"test \"$4\" = w1:p1 || exit 1\n"+bridgeEcho)
	d.herdrNamed = map[string]bool{"work": true}
	d.herdrAgents = append(d.herdrAgents, HerdrAgent{Pane: "work/w1:p1", Agent: "claude", Status: "idle"})
	desk, phone, _, _ := linkPair(t, ctx)
	dev := &Device{ID: "phone1", Paired: true}
	answers := herdrAnswers(t, phone)
	d.handleHerdr(dev, desk, proto.New(proto.TypeFluxHerdr, map[string]any{
		"kind": "terminal_open", "pane": "work/w1:p1", "mode": "observe", "request": 1}))
	opened := nextOpened(t, answers)
	if opened["pane"] != "work/w1:p1" || opened["session"] != "ts1" ||
		opened["width"] != 96.0 || opened["height"] != 32.0 {
		t.Fatalf("named terminal: %v", opened)
	}
	if frame := nextAnswer(t, answers); frame["kind"] != "terminal_frame" || frameText(t, frame) != "ready" {
		t.Fatalf("named frame: %v", frame)
	}
	if calls := main.takeCalls(); len(calls) != 0 {
		t.Fatalf("the default session got %v", calls)
	}
	if calls := work.takeCalls(); len(calls) != 1 || calls[0] != `pane.layout {"pane_id":"w1:p1"}` {
		t.Fatalf("named layout: %v", calls)
	}
	d.dropHerdrSession("work")
	if closed := nextClosed(t, answers); closed["session"] != "ts1" || closed["code"] != herdrTermStopped {
		t.Fatalf("removed named terminal: %v", closed)
	}
	if running, agents := d.herdrStatus(); !running || len(agents) != 1 || agents[0].Pane != "w1:p1" {
		t.Fatalf("the default session changed: running=%v agents=%v", running, agents)
	}
}

// herdrSessionNames returns the names of the running named sessions.
func (d *Daemon) herdrSessionNames() []string {
	d.mu.Lock()
	defer d.mu.Unlock()
	var names []string
	for name, running := range d.herdrNamed {
		if running {
			names = append(names, name)
		}
	}
	slices.Sort(names)
	return names
}
