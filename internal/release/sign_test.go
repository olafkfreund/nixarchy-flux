package release

import (
	"crypto/ed25519"
	"encoding/base64"
	"testing"
)

func TestPublicKey(t *testing.T) {
	if PublicKey == "" {
		t.Fatal("PublicKey is empty")
	}
	key, err := base64.StdEncoding.DecodeString(PublicKey)
	if err != nil || len(key) != ed25519.PublicKeySize {
		t.Fatalf("PublicKey is not a valid Ed25519 public key: %v", err)
	}
	pub, priv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	message := []byte("different release")
	sig := ed25519.Sign(priv, message)
	if !ed25519.Verify(pub, message, sig) {
		t.Fatal("the fresh key did not verify its own signature")
	}
	if ed25519.Verify(key, message, sig) {
		t.Fatal("PublicKey verified a signature from a different key")
	}
}
