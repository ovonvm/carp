package installer

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestConfiguredPort(t *testing.T) {
	dir := t.TempDir()
	if got := ConfiguredPort(dir); got != "50011" {
		t.Fatalf("missing env: got %q", got)
	}
	if err := os.WriteFile(filepath.Join(dir, "carp.env"), []byte("CARP_ADDR=127.0.0.1:18080\nCARP_DATA_DIR=/tmp\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if got := ConfiguredPort(dir); got != "18080" {
		t.Fatalf("configured env: got %q", got)
	}
}

func TestWrapLine(t *testing.T) {
	line := "局域网 IP   192.168.1.20（默认仅本机监听；局域网/公网请配 nginx 反代）"
	parts := wrapLine(line, 40)
	if len(parts) < 2 {
		t.Fatalf("expected wrapping, got %q", parts)
	}
	for _, p := range parts {
		if w := displayWidth(p); w > 40 {
			t.Fatalf("chunk too wide (%d): %q", w, p)
		}
	}
	if strings.Join(parts, "") != line {
		t.Fatalf("wrap lost characters: %q", parts)
	}
}
