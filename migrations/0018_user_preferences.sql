-- 0018_user_preferences: per-account UI preferences.
--
-- Generic key/value table so new preferences (theme, view mode, easter-egg
-- effects, ...) do not need a migration each time. Keys and values are
-- validated in the account service; unknown rows are ignored by clients.
--
-- ON DELETE CASCADE: purging a user removes their preferences with it.

CREATE TABLE IF NOT EXISTS user_preferences (
  user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  key        TEXT NOT NULL,
  value      TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (user_id, key)
);

CREATE INDEX IF NOT EXISTS user_preferences_user ON user_preferences(user_id);
