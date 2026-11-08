package httpx

import (
	"net/http"
)

// handleGetMyPreferences returns the signed-in account's UI preferences.
func (s *Server) handleGetMyPreferences(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	prefs, err := s.account.Preferences(r.Context(), p.UserID)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	s.writeJSON(w, http.StatusOK, map[string]any{"preferences": prefs})
}

// handlePutMyPreferences validates and stores a partial preference patch.
func (s *Server) handlePutMyPreferences(w http.ResponseWriter, r *http.Request) {
	p, err := requireUser(r)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	var req struct {
		Preferences map[string]string `json:"preferences"`
	}
	if err := decodeJSON(w, r, &req); err != nil {
		s.writeError(w, r, err)
		return
	}
	prefs, err := s.account.SetPreferences(r.Context(), p.UserID, req.Preferences)
	if err != nil {
		s.writeError(w, r, err)
		return
	}
	s.writeJSON(w, http.StatusOK, map[string]any{"preferences": prefs})
}
