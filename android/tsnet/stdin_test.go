package main

import (
	"os"
	"strings"
	"testing"
)

func TestReadStdin(t *testing.T) {
	var rechecks, cancels int
	readStdin(strings.NewReader("net\nnoise\nnet\n"), func() { cancels++ }, func() { rechecks++ })
	if rechecks != 2 || cancels != 1 {
		t.Fatalf("rechecks=%d cancels=%d, want 2 and 1", rechecks, cancels)
	}
}

func TestStatusLines(t *testing.T) {
	f, err := os.CreateTemp(t.TempDir(), "out")
	if err != nil {
		t.Fatal(err)
	}
	old := statusOut
	statusOut = f
	t.Cleanup(func() { statusOut = old; up = false; approved = false })

	approve()
	approve()
	markReady("100.64.0.1")
	approve()
	line("login")
	if !isUp() {
		t.Fatal("isUp after ready")
	}
	got, _ := os.ReadFile(f.Name())
	want := "approve\nready 100.64.0.1\nlogin\n"
	if string(got) != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}
