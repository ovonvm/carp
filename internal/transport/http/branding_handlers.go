package httpx

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"unicode/utf8"

	"carp/internal/domain/access"
	domainidentity "carp/internal/domain/identity"
	sqlstore "carp/internal/infra/sql"
	"carp/internal/platform/apperr"
)

// M6.8 branding: logo replaces the default wordmark in the sidebar and login
// page; login copy and the global footer are root-editable plain text. Every
// asset lives in the existing blobs table and every text key in settings, so
// there is no new table and nothing to migrate beyond users.avatar_*.
const (
	defaultLoginTitle    = "你的文件，\n放在自己的服务器。"
	defaultLoginSubtitle = "自托管网盘 · 数据始终在你自己手里。支持离线下载、分享、组空间与媒体播放。"

	maxLoginTitleRunes    = 120
	maxLoginSubtitleRunes = 160
	maxFooterTextRunes    = 200
	maxFooterLinks        = 3
	maxFooterLabelRunes   = 40

	maxLogoBytes   = 512 << 10
	maxAvatarBytes = 2 << 20
)

// isRootUser reloads the account so the root flag (users.is_root) decides,
// not the role code: an ordinary admin is not root.
func (s *Server) isRootUser(r *http.Request, p access.Principal) bool {
	u, err := s.identity.User(r.Context(), p.UserID)
	if err != nil {
		return false
	}
	return u.IsRoot
}

type siteLink struct {
	Label string `json:"label"`
	URL   string `json:"url"`
}

type brandingView struct {
	SiteName      string
	LoginTitle    string
	LoginSubtitle string
	FooterText    string
	FooterLinks   []siteLink
	LogoBlobID    int64
	LogoMime      string
}

// loadBranding reads every branding key in one pass. Unset keys stay empty so
// the caller can decide whether to expose defaults (public config) or the raw
// value (admin form).
func (s *Server) loadBranding(r *http.Request) (brandingView, error) {
	ctx := r.Context()
	read := func(key string) (string, error) {
		v, ok, err := s.db.Setting(ctx, key)
		if err != nil || !ok {
			return "", err
		}
		return v, nil
	}
	var b brandingView
	var err error
	if b.SiteName, err = read(sqlstore.SettingSiteName); err != nil {
		return b, err
	}
	if b.LoginTitle, err = read(sqlstore.SettingLoginTitle); err != nil {
		return b, err
	}
	if b.LoginSubtitle, err = read(sqlstore.SettingLoginSubtitle); err != nil {
		return b, err
	}
	if b.FooterText, err = read(sqlstore.SettingFooterText); err != nil {
		return b, err
	}
	if raw, err := read(sqlstore.SettingFooterLinks); err == nil && raw != "" {
		_ = json.Unmarshal([]byte(raw), &b.FooterLinks)
	} else if err != nil {
		return b, err
	}
	if id, err := read(sqlstore.SettingSiteLogoBlobID); err == nil && id != "" {
		b.LogoBlobID, _ = strconv.ParseInt(id, 10, 64)
	} else if err != nil {
		return b, err
	}
	if b.LogoMime, err = read(sqlstore.SettingSiteLogoMime); err != nil {
		return b, err
	}
	return b, nil
}

func logoURLFor(blobID int64) string {
	if blobID <= 0 {
		return ""
	}
	return fmt.Sprintf("/api/v1/public/site/logo?v=%d", blobID)
}

func avatarURLFor(u domainidentity.User) string {
	if u.AvatarBlobID <= 0 {
		return ""
	}
	// The query parameter doubles as a cache bust: a new upload produces a new
	// blob id, so the browser can keep the old avatar cached forever.
	return fmt.Sprintf("/api/v1/users/%d/avatar?v=%d", u.ID, u.AvatarBlobID)
}

func (s *Server) handleSiteConfig(w http.ResponseWriter, r *http.Request) {
	b, err := s.loadBranding(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	title := b.LoginTitle
	subtitle := b.LoginSubtitle
	if title == "" {
		title = defaultLoginTitle
	}
	if subtitle == "" {
		subtitle = defaultLoginSubtitle
	}
	links := b.FooterLinks
	if links == nil {
		links = []siteLink{}
	}
	s.writeJSON(w, http.StatusOK, map[string]any{
		"site_name":     b.SiteName,
		"logo_url":      logoURLFor(b.LogoBlobID),
		"login":         map[string]string{"title": title, "subtitle": subtitle},
		"footer":        map[string]any{"text": b.FooterText, "links": links},
		"login_default": map[string]string{"title": defaultLoginTitle, "subtitle": defaultLoginSubtitle},
	})
}

func (s *Server) handleSiteLogo(w http.ResponseWriter, r *http.Request) {
	b, err := s.loadBranding(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if b.LogoBlobID <= 0 {
		http.NotFound(w, r)
		return
	}
	s.serveBlob(w, r, b.LogoBlobID, b.LogoMime, "public, max-age=86400")
}

// serveBlob streams one content-addressed object with a strong ETag. It is
// shared by the site logo and user avatars.
func (s *Server) serveBlob(w http.ResponseWriter, r *http.Request, blobID int64, mime, cacheControl string) {
	ctx := r.Context()
	blob, err := s.db.BlobByID(ctx, blobID)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	etag := `"` + blob.SHA256 + `"`
	if match := r.Header.Get("If-None-Match"); match != "" && strings.Contains(match, etag) {
		w.WriteHeader(http.StatusNotModified)
		return
	}
	st, err := s.registry.Get(ctx, blob.StoragePolicyID)
	if err != nil {
		s.log.Warn("branding: storage policy unavailable", "err", err, "blob", blob.ID)
		http.NotFound(w, r)
		return
	}
	f, err := st.Open(blob.SHA256)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	defer f.Close()
	if mime == "" {
		mime = "application/octet-stream"
	}
	w.Header().Set("Content-Type", mime)
	w.Header().Set("Cache-Control", cacheControl)
	w.Header().Set("ETag", etag)
	w.Header().Set("Content-Length", strconv.FormatInt(blob.Size, 10))
	w.WriteHeader(http.StatusOK)
	_, _ = io.Copy(w, f)
}

func (s *Server) requireRootPanel(w http.ResponseWriter, r *http.Request) (access.Principal, bool) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return access.Principal{}, false
	}
	if err := s.authz.Require(p, access.ActionAdminSettings, access.SystemResource()); err != nil {
		s.writeError(w, r, err)
		return access.Principal{}, false
	}
	if p.Role != "root" {
		s.writeError(w, r, apperr.ErrForbidden.WithReason(apperr.ReasonRootAdminRequired))
		return access.Principal{}, false
	}
	return p, true
}

type brandingPatch struct {
	LoginTitle    *string     `json:"login_title"`
	LoginSubtitle *string     `json:"login_subtitle"`
	FooterText    *string     `json:"footer_text"`
	FooterLinks   *[]siteLink `json:"footer_links"`
}

func (s *Server) handleAdminGetBranding(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if err := s.authz.Require(p, access.ActionAdminSettings, access.SystemResource()); err != nil {
		s.writeError(w, r, err)
		return
	}
	if !s.isRootUser(r, p) {
		s.writeError(w, r, apperr.ErrForbidden.WithReason(apperr.ReasonRootAdminRequired))
		return
	}
	_ = p
	b, err := s.loadBranding(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	links := b.FooterLinks
	if links == nil {
		links = []siteLink{}
	}
	s.writeJSON(w, http.StatusOK, map[string]any{
		"site_name":      b.SiteName,
		"login_title":    b.LoginTitle,
		"login_subtitle": b.LoginSubtitle,
		"footer_text":    b.FooterText,
		"footer_links":   links,
		"logo_url":       logoURLFor(b.LogoBlobID),
		"logo_set":       b.LogoBlobID > 0,
		"max_links":      maxFooterLinks,
		"default_login":  map[string]string{"title": defaultLoginTitle, "subtitle": defaultLoginSubtitle},
	})
}

func (s *Server) handleAdminPutBranding(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if err := s.authz.Require(p, access.ActionAdminSettings, access.SystemResource()); err != nil {
		s.writeError(w, r, err)
		return
	}
	if !s.isRootUser(r, p) {
		s.writeError(w, r, apperr.ErrForbidden.WithReason(apperr.ReasonRootAdminRequired))
		return
	}
	var patch brandingPatch
	if err := decodeJSON(w, r, &patch); err != nil {
		s.writeError(w, r, err)
		return
	}
	type kv struct{ key, value string }
	var writes []kv
	validateText := func(name string, v *string, max int) (string, error) {
		if v == nil {
			return "", nil
		}
		t := strings.TrimSpace(*v)
		if utf8.RuneCountInString(t) > max {
			return "", apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
		}
		return t, nil
	}
	if patch.LoginTitle != nil {
		v, err := validateText("login_title", patch.LoginTitle, maxLoginTitleRunes)
		if err != nil {
			s.writeError(w, r, err)
			return
		}
		writes = append(writes, kv{sqlstore.SettingLoginTitle, v})
	}
	if patch.LoginSubtitle != nil {
		v, err := validateText("login_subtitle", patch.LoginSubtitle, maxLoginSubtitleRunes)
		if err != nil {
			s.writeError(w, r, err)
			return
		}
		writes = append(writes, kv{sqlstore.SettingLoginSubtitle, v})
	}
	if patch.FooterText != nil {
		v, err := validateText("footer_text", patch.FooterText, maxFooterTextRunes)
		if err != nil {
			s.writeError(w, r, err)
			return
		}
		writes = append(writes, kv{sqlstore.SettingFooterText, v})
	}
	if patch.FooterLinks != nil {
		links := *patch.FooterLinks
		if len(links) > maxFooterLinks {
			s.writeError(w, r, apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue))
			return
		}
		cleaned := make([]siteLink, 0, len(links))
		for _, l := range links {
			label := strings.TrimSpace(l.Label)
			raw := strings.TrimSpace(l.URL)
			if label == "" || utf8.RuneCountInString(label) > maxFooterLabelRunes {
				s.writeError(w, r, apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue))
				return
			}
			if !validFooterURL(raw) {
				s.writeError(w, r, apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue))
				return
			}
			cleaned = append(cleaned, siteLink{Label: label, URL: raw})
		}
		raw, err := json.Marshal(cleaned)
		if err != nil {
			s.writeError(w, r, err)
			return
		}
		writes = append(writes, kv{sqlstore.SettingFooterLinks, string(raw)})
	}
	for _, wv := range writes {
		if err := s.db.SetSetting(r.Context(), wv.key, wv.value); err != nil {
			s.writeError(w, r, err)
			return
		}
	}
	s.log.Info("branding updated", "by", p.UserID, "keys", len(writes))
	s.respondBranding(w, r)
}

// validFooterURL accepts absolute http(s) links and site-relative paths. The
// footer renders in the SPA, so javascript:/data: must never get through.
func validFooterURL(raw string) bool {
	if raw == "" {
		return false
	}
	if strings.HasPrefix(raw, "/") && !strings.HasPrefix(raw, "//") {
		return true
	}
	u, err := url.Parse(raw)
	if err != nil {
		return false
	}
	return (u.Scheme == "http" || u.Scheme == "https") && u.Host != ""
}

func (s *Server) handleAdminPutLogo(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if err := s.authz.Require(p, access.ActionAdminSettings, access.SystemResource()); err != nil {
		s.writeError(w, r, err)
		return
	}
	if !s.isRootUser(r, p) {
		s.writeError(w, r, apperr.ErrForbidden.WithReason(apperr.ReasonRootAdminRequired))
		return
	}
	_ = p
	data, mime, err := s.readImageUpload(w, r, maxLogoBytes, "image/png", "image/jpeg")
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	blobID, err := s.storeImage(r, data)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	b, err := s.loadBranding(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if err := s.db.SetSetting(r.Context(), sqlstore.SettingSiteLogoBlobID, strconv.FormatInt(blobID, 10)); err != nil {
		s.writeError(w, r, err)
		return
	}
	if err := s.db.SetSetting(r.Context(), sqlstore.SettingSiteLogoMime, mime); err != nil {
		s.writeError(w, r, err)
		return
	}
	s.releaseBlob(r, b.LogoBlobID)
	s.log.Info("branding logo updated", "blob", blobID, "mime", mime)
	s.respondBranding(w, r)
}

func (s *Server) handleAdminDeleteLogo(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if err := s.authz.Require(p, access.ActionAdminSettings, access.SystemResource()); err != nil {
		s.writeError(w, r, err)
		return
	}
	if !s.isRootUser(r, p) {
		s.writeError(w, r, apperr.ErrForbidden.WithReason(apperr.ReasonRootAdminRequired))
		return
	}
	_ = p
	b, err := s.loadBranding(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if err := s.db.SetSetting(r.Context(), sqlstore.SettingSiteLogoBlobID, ""); err != nil {
		s.writeError(w, r, err)
		return
	}
	_ = s.db.SetSetting(r.Context(), sqlstore.SettingSiteLogoMime, "")
	s.releaseBlob(r, b.LogoBlobID)
	s.respondBranding(w, r)
}

func (s *Server) handlePutMyAvatar(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	data, mime, err := s.readImageUpload(w, r, maxAvatarBytes, "image/png", "image/jpeg", "image/webp")
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	blobID, err := s.storeImage(r, data)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	old, err := s.currentAvatarID(r, p.UserID)
	if err != nil {
		s.releaseBlob(r, blobID)
		s.writeError(w, r, err)
		return
	}
	if _, err := s.db.ExecContext(r.Context(),
		`UPDATE users SET avatar_blob_id = ?, avatar_mime = ? WHERE id = ?`,
		blobID, mime, p.UserID); err != nil {
		s.releaseBlob(r, blobID)
		s.writeError(w, r, err)
		return
	}
	s.releaseBlob(r, old)
	u, err := s.identity.User(r.Context(), p.UserID)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	s.writeJSON(w, http.StatusOK, map[string]any{"avatar_url": avatarURLFor(u)})
}

func (s *Server) handleDeleteMyAvatar(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	old, err := s.currentAvatarID(r, p.UserID)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	if _, err := s.db.ExecContext(r.Context(),
		`UPDATE users SET avatar_blob_id = NULL, avatar_mime = '' WHERE id = ?`, p.UserID); err != nil {
		s.writeError(w, r, err)
		return
	}
	s.releaseBlob(r, old)
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleUserAvatar(w http.ResponseWriter, r *http.Request) {
	if _, err := requireUser(r); err != nil {
		s.writeError(w, r, err)
		return
	}
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil || id <= 0 {
		http.NotFound(w, r)
		return
	}
	blobID, mime, err := s.userAvatar(r, id)
	if err != nil || blobID <= 0 {
		http.NotFound(w, r)
		return
	}
	// Avatars are visible to every logged-in user (they appear in group member
	// lists); the image itself is non-sensitive.
	s.serveBlob(w, r, blobID, mime, "private, max-age=86400")
}

func (s *Server) currentAvatarID(r *http.Request, userID int64) (int64, error) {
	var id int64
	err := s.db.QueryRowContext(r.Context(),
		`SELECT COALESCE(avatar_blob_id, 0) FROM users WHERE id = ?`, userID).Scan(&id)
	if err != nil {
		return 0, err
	}
	return id, nil
}

func (s *Server) userAvatar(r *http.Request, userID int64) (int64, string, error) {
	var id int64
	var mime string
	err := s.db.QueryRowContext(r.Context(),
		`SELECT COALESCE(avatar_blob_id, 0), avatar_mime FROM users WHERE id = ?`, userID).Scan(&id, &mime)
	return id, mime, err
}

// readImageUpload parses a multipart "file" field and rejects anything larger
// than max bytes or outside the allowed MIME list.
func (s *Server) readImageUpload(w http.ResponseWriter, r *http.Request, max int64, allowed ...string) ([]byte, string, error) {
	r.Body = http.MaxBytesReader(w, r.Body, max+(1<<20))
	if err := r.ParseMultipartForm(max + (1 << 20)); err != nil {
		return nil, "", apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
	}
	file, _, err := r.FormFile("file")
	if err != nil {
		return nil, "", apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, max+1))
	if err != nil {
		return nil, "", apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
	}
	if int64(len(data)) > max || len(data) == 0 {
		return nil, "", apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
	}
	mime := http.DetectContentType(data)
	for _, a := range allowed {
		if mime == a {
			return data, mime, nil
		}
	}
	return nil, "", apperr.ErrBadRequest.WithReason(apperr.ReasonInvalidValue)
}

// storeImage writes the bytes into the default storage policy and records (or
// re-references) the blob row. The returned refcount is owned by the caller.
func (s *Server) storeImage(r *http.Request, data []byte) (int64, error) {
	ctx := r.Context()
	policy, err := s.db.DefaultStoragePolicy(ctx)
	if err != nil {
		return 0, err
	}
	st, err := s.registry.Get(ctx, policy.ID)
	if err != nil {
		return 0, err
	}
	sha, size, err := st.Put(ctx, bytes.NewReader(data))
	if err != nil {
		return 0, err
	}
	blob, created, err := s.db.InsertBlobInPolicy(ctx, policy.ID, sha, size)
	if err != nil {
		return 0, err
	}
	if !created {
		if err := s.db.IncRef(ctx, blob.ID); err != nil {
			return 0, err
		}
	}
	return blob.ID, nil
}

func (s *Server) releaseBlob(r *http.Request, blobID int64) {
	if blobID <= 0 {
		return
	}
	if _, err := s.db.DecRef(r.Context(), blobID); err != nil && apperr.CodeOf(err) != apperr.CodeNotFound {
		s.log.Warn("branding: release blob", "err", err, "blob", blobID)
	}
}

func (s *Server) respondBranding(w http.ResponseWriter, r *http.Request) {
	b, err := s.loadBranding(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	links := b.FooterLinks
	if links == nil {
		links = []siteLink{}
	}
	s.writeJSON(w, http.StatusOK, map[string]any{
		"site_name":      b.SiteName,
		"login_title":    b.LoginTitle,
		"login_subtitle": b.LoginSubtitle,
		"footer_text":    b.FooterText,
		"footer_links":   links,
		"logo_url":       logoURLFor(b.LogoBlobID),
		"logo_set":       b.LogoBlobID > 0,
		"max_links":      maxFooterLinks,
		"default_login":  map[string]string{"title": defaultLoginTitle, "subtitle": defaultLoginSubtitle},
	})
}
