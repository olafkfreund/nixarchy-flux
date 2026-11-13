// Command nologs checks what quiet.Apply turns off, in a process of its own:
// inside a test binary the upload transport is a no-op anyway
// (logpolicy.go:895). nologs_test.go runs it; with -control it skips Apply.
// It prints "sent=<n> logged=<n> buffered=<n>": requests that reached a local
// server through tsnet's upload transport, upload requests from a logger
// that starts with 1 buffered entry, and the logger's buffer writes.
package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"time"

	"flux-android-tsnet/internal/quiet"
	"tailscale.com/logpolicy"
	"tailscale.com/logtail"
	"tailscale.com/types/logid"
)

// countingBuffer is a memory buffer that counts the logger's writes.
type countingBuffer struct {
	logtail.Buffer
	writes atomic.Int32
}

func (b *countingBuffer) Write(p []byte) (int, error) {
	b.writes.Add(1)
	return b.Buffer.Write(p)
}

func main() {
	control := flag.Bool("control", false, "skip quiet.Apply")
	flag.Parse()
	if !*control {
		quiet.Apply()
	}

	var sent, logged atomic.Int32
	ts := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/transport" {
			sent.Add(1)
		} else {
			logged.Add(1)
		}
		_, _ = io.Copy(io.Discard, r.Body)
	}))
	defer ts.Close()

	// The transport tsnet uploads with (tsnet.go:1132), to a local server, so
	// nothing leaves the machine.
	tr := logpolicy.NewLogtailTransport(logtail.DefaultHost, nil, nil, log.Printf)
	req, err := http.NewRequest("POST", ts.URL+"/transport", strings.NewReader("x"))
	if err != nil {
		log.Fatal(err)
	}
	resp, err := tr.RoundTrip(req)
	if err != nil {
		log.Fatal(err)
	}
	if resp.Body != nil {
		resp.Body.Close()
	}

	// A logger as tsnet makes one, with a counting buffer and the local server.
	priv, err := logid.NewPrivateID()
	if err != nil {
		log.Fatal(err)
	}
	// One entry is already buffered, as a #48 state dir has: Disable must stop its upload too.
	buf := &countingBuffer{Buffer: logtail.NewMemoryBuffer(100)}
	if _, err := buf.Buffer.Write([]byte(`{"text":"from before"}` + "\n")); err != nil {
		log.Fatal(err)
	}
	lg := logtail.NewLogger(logtail.Config{
		Collection: "nologs.flux.test",
		PrivateID:  priv,
		BaseURL:    ts.URL,
		HTTPC:      ts.Client(),
		Buffer:     buf,
		Stderr:     io.Discard,
	}, log.Printf)
	for i := range 10 {
		lg.Logf("line %d", i)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_ = lg.Shutdown(ctx) // uploads what is buffered, unless disabled
	fmt.Printf("sent=%d logged=%d buffered=%d\n", sent.Load(), logged.Load(), buf.writes.Load())
}
