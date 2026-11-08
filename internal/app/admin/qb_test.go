package admin

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"carp/internal/platform/apperr"
)

// fakeQB serves the two endpoints TestQB needs: login and version.
func fakeQB(t *testing.T, version string, loginStatus int) *httptest.Server {
	t.Helper()
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v2/auth/login", func(w http.ResponseWriter, r *http.Request) {
		if loginStatus != http.StatusOK {
			w.WriteHeader(loginStatus)
			return
		}
		_, _ = w.Write([]byte("Ok."))
	})
	mux.HandleFunc("/api/v2/app/version", func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(version))
	})
	srv := httptest.NewServer(mux)
	t.Cleanup(srv.Close)
	return srv
}

func strPtr(v string) *string { return &v }

func TestTestQBReportsVersion(t *testing.T) {
	_, svc, adminID := newTestEnv(t)
	svc.SetQB([]byte("0123456789abcdef0123456789abcdef"), QBEnv{})
	srv := fakeQB(t, "v5.2.3", http.StatusOK)

	got, err := svc.TestQB(context.Background(), adminPrincipal(adminID), QBUpdate{
		URL:      strPtr(srv.URL),
		Username: strPtr("admin"),
		Password: strPtr("secret"),
	})
	if err != nil {
		t.Fatalf("test qb: %v", err)
	}
	if !got.OK || got.Version != "v5.2.3" || got.Error != "" {
		t.Fatalf("unexpected result: %+v", got)
	}
}

func TestTestQBReportsAuthFailure(t *testing.T) {
	_, svc, adminID := newTestEnv(t)
	svc.SetQB([]byte("0123456789abcdef0123456789abcdef"), QBEnv{})
	srv := fakeQB(t, "", http.StatusOK)
	// Wrong credentials: qBittorrent answers 200 with "Fails." — simulate via
	// a dedicated server whose login handler returns that body.
	bad := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/v2/auth/login" {
			_, _ = w.Write([]byte("Fails."))
			return
		}
		_, _ = w.Write([]byte("v5.2.3"))
	}))
	t.Cleanup(bad.Close)
	_ = srv

	got, err := svc.TestQB(context.Background(), adminPrincipal(adminID), QBUpdate{
		URL:      strPtr(bad.URL),
		Username: strPtr("admin"),
		Password: strPtr("wrong"),
	})
	if err != nil {
		t.Fatalf("test qb: %v", err)
	}
	if got.OK || got.Reason != "auth" || !strings.Contains(got.Error, "认证失败") {
		t.Fatalf("unexpected result: %+v", got)
	}
}

func TestTestQBRejectsInvalidURL(t *testing.T) {
	_, svc, adminID := newTestEnv(t)
	svc.SetQB([]byte("0123456789abcdef0123456789abcdef"), QBEnv{})

	got, err := svc.TestQB(context.Background(), adminPrincipal(adminID), QBUpdate{URL: strPtr("ftp://example.com")})
	if err != nil {
		t.Fatalf("test qb: %v", err)
	}
	if got.OK || got.Reason != "invalid_url" {
		t.Fatalf("unexpected result: %+v", got)
	}
}

func TestTestQBRequiresRoot(t *testing.T) {
	_, svc, adminID := newTestEnv(t)
	svc.SetQB([]byte("0123456789abcdef0123456789abcdef"), QBEnv{})
	ctx := context.Background()
	plain, _, err := svc.CreateUser(ctx, adminPrincipal(adminID), CreateRequest{
		Username: "plain", Password: "plain-password-1",
	})
	if err != nil {
		t.Fatalf("create plain user: %v", err)
	}

	_, err = svc.TestQB(ctx, adminPrincipal(plain.ID), QBUpdate{URL: strPtr("http://127.0.0.1:8080")})
	if err == nil || apperr.CodeOf(err) != apperr.CodeForbidden {
		t.Fatalf("expected forbidden, got %v", err)
	}
}
