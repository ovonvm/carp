package sqlstore

import (
	"context"
	"fmt"
	"time"
)

// UserPreferences loads every preference row for one account.
func (d *DB) UserPreferences(ctx context.Context, userID int64) (map[string]string, error) {
	rows, err := d.QueryContext(ctx, `SELECT key, value FROM user_preferences WHERE user_id = ? ORDER BY key`, userID)
	if err != nil {
		return nil, fmt.Errorf("sqlstore: list user preferences: %w", err)
	}
	defer rows.Close()

	out := map[string]string{}
	for rows.Next() {
		var key, value string
		if err := rows.Scan(&key, &value); err != nil {
			return nil, fmt.Errorf("sqlstore: scan user preference: %w", err)
		}
		out[key] = value
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("sqlstore: list user preferences: %w", err)
	}
	return out, nil
}

// SetUserPreference upserts one preference for one account.
func (d *DB) SetUserPreference(ctx context.Context, userID int64, key, value string, now time.Time) error {
	_, err := d.ExecContext(ctx, `
		INSERT INTO user_preferences (user_id, key, value, updated_at)
		VALUES (?, ?, ?, ?)
		ON CONFLICT(user_id, key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at`,
		userID, key, value, now.Unix())
	if err != nil {
		return fmt.Errorf("sqlstore: upsert user preference: %w", err)
	}
	return nil
}
