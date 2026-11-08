package admin

// qBittorrent offline-download settings (M6.6). The WebUI URL and username are
// stored in the settings table; the password is sealed with
// internal/platform/secretbox so it never lands in the database in clear.
//
// Deployment environment values (CARP_QB_URL / CARP_QB_USERNAME /
// CARP_QB_PASSWORD) win over the admin page: operators who configure the
// service by environment file keep full control and the page shows the fields
// read-only. This prevents a "saved in the UI but nothing changed" trap.

import (
	"context"
	"errors"
	"net"
	"net/url"
	"strings"
	"syscall"
	"time"
	"unicode/utf8"

	"carp/internal/domain/access"
	"carp/internal/infra/qb"
	"carp/internal/infra/sql"
	"carp/internal/platform/apperr"
	"carp/internal/platform/secretbox"
)

// QBEnv is the deployment-provided qBittorrent configuration.
type QBEnv struct {
	URL      string
	Username string
	Password string
}

// QBView is the admin API view: no password ever leaves the server.
type QBView struct {
	Enabled     bool   `json:"enabled"`
	URL         string `json:"url"`
	Username    string `json:"username"`
	HasPassword bool   `json:"has_password"`
	// Locked reports that environment variables control the configuration;
	// the admin page renders the fields read-only.
	Locked bool `json:"locked"`
	// Source is "env" or "db"; "none" when neither is configured.
	Source string `json:"source"`
}

// QBUpdate is the PUT body; nil fields are kept and an empty password means
// "keep the current one" unless ClearPassword is set.
type QBUpdate struct {
	URL           *string `json:"url"`
	Username      *string `json:"username"`
	Password      *string `json:"password"`
	ClearPassword bool    `json:"clear_password"`
}

// SetQB configures the deployment environment values and the sealing key.
// main calls it once after the admin service is built.
func (s *Service) SetQB(key []byte, env QBEnv) {
	s.qbKey = key
	s.qbEnv = QBEnv{
		URL:      strings.TrimSpace(env.URL),
		Username: strings.TrimSpace(env.Username),
		Password: env.Password,
	}
}

// QBEffective returns the values the downloader should use right now: env
// wins, otherwise the stored settings. A sealed password that cannot be opened
// (key rotated) is reported as an error rather than silently dropping it.
func (s *Service) QBEffective(ctx context.Context) (QBEnv, error) {
	if s.qbEnv.URL != "" {
		return s.qbEnv, nil
	}
	var out QBEnv
	var err error
	if out.URL, _, err = s.db.Setting(ctx, sqlstore.SettingQBURL); err != nil {
		return out, err
	}
	if out.Username, _, err = s.db.Setting(ctx, sqlstore.SettingQBUsername); err != nil {
		return out, err
	}
	sealed, ok, err := s.db.Setting(ctx, sqlstore.SettingQBPasswordEnc)
	if err != nil {
		return out, err
	}
	if !ok || sealed == "" {
		return out, nil
	}
	plain, err := secretbox.Open(s.qbKey, sealed)
	if err != nil {
		return out, apperr.ErrInternal.WithReason(apperr.ReasonQBPasswordUnreadable).WithCause(err)
	}
	out.Password = plain
	return out, nil
}

// GetQB returns the current view. Root only: qB credentials let the caller
// control the downloader, so delegated admins must not see them.
func (s *Service) GetQB(ctx context.Context, p access.Principal) (QBView, error) {
	if err := s.requireRoot(ctx, p); err != nil {
		return QBView{}, err
	}
	return s.qbView(ctx)
}

func (s *Service) qbView(ctx context.Context) (QBView, error) {
	if s.qbEnv.URL != "" {
		return QBView{
			Enabled:     true,
			URL:         s.qbEnv.URL,
			Username:    s.qbEnv.Username,
			HasPassword: s.qbEnv.Password != "",
			Locked:      true,
			Source:      "env",
		}, nil
	}
	out := QBView{Source: "none"}
	var err error
	if out.URL, _, err = s.db.Setting(ctx, sqlstore.SettingQBURL); err != nil {
		return QBView{}, err
	}
	if out.Username, _, err = s.db.Setting(ctx, sqlstore.SettingQBUsername); err != nil {
		return QBView{}, err
	}
	sealed, ok, err := s.db.Setting(ctx, sqlstore.SettingQBPasswordEnc)
	if err != nil {
		return QBView{}, err
	}
	if out.URL != "" || out.Username != "" || (ok && sealed != "") {
		out.Source = "db"
	}
	out.Enabled = strings.TrimSpace(out.URL) != ""
	if ok && sealed != "" {
		// Decryptability decides has_password: a key rotation must not look
		// like a configured password that silently fails at download time.
		if _, err := secretbox.Open(s.qbKey, sealed); err == nil {
			out.HasPassword = true
		}
	}
	return out, nil
}

// UpdateQB validates and persists the settings, then returns the new view.
func (s *Service) UpdateQB(ctx context.Context, p access.Principal, upd QBUpdate) (QBView, error) {
	if err := s.requireRoot(ctx, p); err != nil {
		return QBView{}, err
	}
	if s.qbEnv.URL != "" {
		return QBView{}, apperr.ErrForbidden.WithReason(apperr.ReasonQBEnvLocked)
	}
	if upd.URL != nil {
		raw := strings.TrimSpace(*upd.URL)
		if raw != "" {
			if len(raw) > 512 {
				return QBView{}, apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
			}
			u, err := url.Parse(raw)
			if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
				return QBView{}, apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue).WithCause(err)
			}
			raw = strings.TrimRight(u.String(), "/")
		}
		if err := s.db.SetSetting(ctx, sqlstore.SettingQBURL, raw); err != nil {
			return QBView{}, err
		}
	}
	if upd.Username != nil {
		name := strings.TrimSpace(*upd.Username)
		if utf8.RuneCountInString(name) > 128 {
			return QBView{}, apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
		}
		if err := s.db.SetSetting(ctx, sqlstore.SettingQBUsername, name); err != nil {
			return QBView{}, err
		}
	}
	passwordChanged := "kept"
	switch {
	case upd.ClearPassword:
		if err := s.db.SetSetting(ctx, sqlstore.SettingQBPasswordEnc, ""); err != nil {
			return QBView{}, err
		}
		passwordChanged = "cleared"
	case upd.Password != nil && *upd.Password != "":
		if utf8.RuneCountInString(*upd.Password) > 256 {
			return QBView{}, apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
		}
		sealed, err := secretbox.Seal(s.qbKey, *upd.Password)
		if err != nil {
			return QBView{}, apperr.ErrInternal.WithCause(err)
		}
		if err := s.db.SetSetting(ctx, sqlstore.SettingQBPasswordEnc, sealed); err != nil {
			return QBView{}, err
		}
		passwordChanged = "changed"
	}
	out, err := s.qbView(ctx)
	if err != nil {
		return QBView{}, err
	}
	// Never log the password, the sealed value or the full URL query.
	s.audit(ctx, p, "settings.qb_update", "settings", 0,
		"enabled="+boolString(out.Enabled)+" username="+out.Username+" password="+passwordChanged)
	return out, nil
}

func boolString(v bool) string {
	if v {
		return "true"
	}
	return "false"
}

// QBTestResult reports the outcome of a "test connection" attempt. Failures
// are returned as data (HTTP 200) so the admin page can render the reason
// without special-casing error payloads; credentials are never echoed.
type QBTestResult struct {
	OK      bool   `json:"ok"`
	Version string `json:"version,omitempty"`
	Error   string `json:"error,omitempty"`
	Reason  string `json:"reason,omitempty"`
}

const qbTestTimeout = 5 * time.Second

// TestQB logs in to the qBittorrent WebUI with the submitted form values
// (blank fields fall back to the saved/environment configuration) and returns
// the reported version. Root only; no settings are written.
func (s *Service) TestQB(ctx context.Context, p access.Principal, upd QBUpdate) (QBTestResult, error) {
	if err := s.requireRoot(ctx, p); err != nil {
		return QBTestResult{}, err
	}
	eff, err := s.QBEffective(ctx)
	if err != nil {
		return QBTestResult{}, err
	}
	rawURL, username, password := eff.URL, eff.Username, eff.Password
	if upd.URL != nil {
		rawURL = strings.TrimSpace(*upd.URL)
	}
	if upd.Username != nil {
		username = strings.TrimSpace(*upd.Username)
	}
	if upd.Password != nil && *upd.Password != "" {
		password = *upd.Password
	}
	if rawURL == "" {
		return QBTestResult{Error: "请先填写 WebUI 地址", Reason: "invalid_url"}, nil
	}
	u, err := url.Parse(rawURL)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return QBTestResult{Error: "地址需以 http:// 或 https:// 开头", Reason: "invalid_url"}, nil
	}
	client, err := qb.New(rawURL, username, password)
	if err != nil {
		if errors.Is(err, qb.ErrDisabled) {
			return QBTestResult{Error: "请先填写 WebUI 地址", Reason: "invalid_url"}, nil
		}
		return QBTestResult{Error: "地址无效：" + err.Error(), Reason: "invalid_url"}, nil
	}
	testCtx, cancel := context.WithTimeout(ctx, qbTestTimeout)
	defer cancel()
	version, err := client.Version(testCtx)
	if err != nil {
		switch {
		case errors.Is(err, qb.ErrAuth):
			return QBTestResult{Error: "认证失败：用户名或密码错误", Reason: "auth"}, nil
		case errors.Is(err, context.DeadlineExceeded), errors.Is(err, context.Canceled):
			return QBTestResult{Error: "连接超时（5 秒）", Reason: "timeout"}, nil
		default:
			return QBTestResult{Error: qbUnreachableMessage(err), Reason: "unreachable"}, nil
		}
	}
	return QBTestResult{OK: true, Version: version}, nil
}

// qbUnreachableMessage turns transport errors into an actionable sentence
// instead of leaking Go's dial error text to the admin page.
func qbUnreachableMessage(err error) string {
	var dnsErr *net.DNSError
	if errors.As(err, &dnsErr) {
		return "无法连接：域名解析失败，请检查 WebUI 地址"
	}
	// Windows socket errors arrive as WSAECONNREFUSED (10061) etc. inside a
	// connectex error, which errors.Is does not always unwrap to ECONNREFUSED.
	var errno syscall.Errno
	if errors.As(err, &errno) {
		switch uintptr(errno) {
		case 10061:
			return "无法连接：目标端口拒绝连接，请检查地址、端口和 qBittorrent 是否运行"
		case 10060:
			return "连接超时（5 秒），请检查地址与网络"
		case 10051:
			return "无法连接：网络不可达，请检查地址或防火墙"
		}
	}
	if errors.Is(err, syscall.ECONNREFUSED) {
		return "无法连接：目标端口拒绝连接，请检查地址、端口和 qBittorrent 是否运行"
	}
	if errors.Is(err, syscall.EHOSTUNREACH) || errors.Is(err, syscall.ENETUNREACH) {
		return "无法连接：网络不可达，请检查地址或防火墙"
	}
	if errors.Is(err, syscall.ECONNRESET) || errors.Is(err, syscall.EPIPE) {
		return "无法连接：连接被重置，请确认地址指向 qBittorrent WebUI"
	}
	return "无法连接，请检查地址、端口与网络"
}
