package account

import (
	"context"
	"strings"

	"carp/internal/platform/apperr"
)

// Preference keys are deliberately a small allowlist: unknown keys are
// rejected instead of silently stored, so a typo in a client never silently
// breaks persistence for that setting.
const (
	PreferenceTheme   = "theme"
	PreferenceView    = "view_mode"
	maxPreferenceSize = 64
)

var preferenceValues = map[string]map[string]bool{
	PreferenceTheme: {"sunset": true, "forest": true, "coast": true, "blossom": true, "dark": true},
	PreferenceView:  {"grid": true, "list": true},
}

// Preferences returns every stored preference for one account.
func (s *Service) Preferences(ctx context.Context, userID int64) (map[string]string, error) {
	return s.db.UserPreferences(ctx, userID)
}

// SetPreferences validates and upserts a partial preference patch, returning
// the complete map afterwards so clients can stay in sync.
func (s *Service) SetPreferences(ctx context.Context, userID int64, patch map[string]string) (map[string]string, error) {
	if len(patch) == 0 {
		return nil, apperr.ErrBadRequest.WithCause(errInvalidPreference("empty patch"))
	}
	for key, value := range patch {
		allowed, ok := preferenceValues[key]
		if !ok {
			return nil, apperr.ErrBadRequest.WithCause(errInvalidPreference("unknown key: " + key))
		}
		value = strings.TrimSpace(value)
		if len(value) > maxPreferenceSize || !allowed[value] {
			return nil, apperr.ErrBadRequest.WithCause(errInvalidPreference("bad value for " + key))
		}
		patch[key] = value
	}
	now := s.now().UTC()
	for key, value := range patch {
		if err := s.db.SetUserPreference(ctx, userID, key, value, now); err != nil {
			return nil, err
		}
	}
	return s.db.UserPreferences(ctx, userID)
}

type invalidPreference string

func (e invalidPreference) Error() string { return "invalid preference: " + string(e) }

func errInvalidPreference(detail string) error { return invalidPreference(detail) }
