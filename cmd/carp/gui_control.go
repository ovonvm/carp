package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net"
	stdhttp "net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"carp/internal/platform/config"
	"carp/internal/platform/version"
)

// guiEvent is one JSON line written to stdout for the Windows control panel.
// Logs stay on stderr so the protocol stream is machine readable.
type guiEvent struct {
	Event   string `json:"event"`
	State   string `json:"state,omitempty"`
	Addr    string `json:"addr,omitempty"`
	DataDir string `json:"data_dir,omitempty"`
	Version string `json:"version,omitempty"`
	Pid     int    `json:"pid,omitempty"`
	Error   string `json:"error,omitempty"`
}

func writeGUIEvent(ev guiEvent) {
	line, err := json.Marshal(ev)
	if err != nil {
		return
	}
	_, _ = os.Stdout.Write(append(line, '\n'))
}

// runGUIControl keeps the process warm and starts/stops the HTTP listener and
// maintenance loop on demand. Commands are read from stdin one per line:
// START, STOP, STATUS, SHUTDOWN. Events are JSON lines on stdout.
func runGUIControl(ctx context.Context, cfg config.Config, handler stdhttp.Handler, log *slog.Logger, maintenance func(context.Context)) int {
	pidPath := filepath.Join(cfg.DataDir, "carp.pid")
	writeGUIEvent(guiEvent{
		Event:   "ready",
		State:   "stopped",
		Addr:    cfg.Addr,
		DataDir: cfg.DataDir,
		Version: version.Version,
		Pid:     os.Getpid(),
	})

	var (
		mu      sync.Mutex
		srv     *stdhttp.Server
		cancel  context.CancelFunc
		maintWG sync.WaitGroup
		serveWG sync.WaitGroup
		running bool
	)
	state := func() string {
		if running {
			return "running"
		}
		return "stopped"
	}
	emitState := func() {
		writeGUIEvent(guiEvent{Event: "state", State: state(), Addr: cfg.Addr})
	}

	stopLocked := func() {
		if !running {
			return
		}
		if srv != nil {
			shutdownCtx, cancelShutdown := context.WithTimeout(context.Background(), 10*time.Second)
			if err := srv.Shutdown(shutdownCtx); err != nil {
				_ = srv.Close()
			}
			cancelShutdown()
		}
		if cancel != nil {
			cancel()
		}
		maintWG.Wait()
		serveWG.Wait()
		_ = os.Remove(pidPath)
		running = false
		emitState()
	}

	startLocked := func() error {
		if running {
			emitState()
			return nil
		}
		listener, err := net.Listen("tcp", cfg.Addr)
		if err != nil {
			writeGUIEvent(guiEvent{Event: "error", State: "stopped", Addr: cfg.Addr, Error: err.Error()})
			return err
		}
		if err := os.WriteFile(pidPath, []byte(strconv.Itoa(os.Getpid())), 0o600); err != nil {
			log.Warn("cannot write pid file", "err", err, "path", pidPath)
		}
		mctx, mcancel := context.WithCancel(ctx)
		cancel = mcancel
		maintWG.Add(1)
		go func() {
			defer maintWG.Done()
			maintenance(mctx)
		}()
		srv = &stdhttp.Server{
			Addr:              cfg.Addr,
			Handler:           handler,
			ReadHeaderTimeout: 10 * time.Second,
			ReadTimeout:       60 * time.Second,
			WriteTimeout:      120 * time.Second,
			IdleTimeout:       120 * time.Second,
		}
		running = true
		writeGUIEvent(guiEvent{Event: "listening", State: "running", Addr: cfg.Addr})
		serveWG.Add(1)
		go func() {
			defer serveWG.Done()
			err := srv.Serve(listener)
			if err != nil && err != stdhttp.ErrServerClosed {
				log.Error("http server failed", "err", err)
				mu.Lock()
				writeGUIEvent(guiEvent{Event: "error", State: "stopped", Error: err.Error()})
				running = false
				mu.Unlock()
			}
		}()
		return nil
	}

	shutdown := func() {
		mu.Lock()
		stopLocked()
		mu.Unlock()
	}

	go func() {
		<-ctx.Done()
		shutdown()
	}()

	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		cmd := strings.ToUpper(strings.TrimSpace(scanner.Text()))
		switch cmd {
		case "START":
			mu.Lock()
			err := startLocked()
			mu.Unlock()
			if err != nil {
				fmt.Fprintln(os.Stderr, "start:", err)
			}
		case "STOP":
			shutdown()
		case "STATUS":
			mu.Lock()
			emitState()
			mu.Unlock()
		case "SHUTDOWN":
			shutdown()
			return 0
		case "":
			continue
		default:
			writeGUIEvent(guiEvent{Event: "error", Error: "unknown command: " + cmd})
		}
	}
	shutdown()
	return 0
}
