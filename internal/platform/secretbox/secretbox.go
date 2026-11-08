// Package secretbox seals small application secrets (currently the
// qBittorrent WebUI password) with AES-256-GCM so an administrator can store
// deployment credentials in the settings table without writing them in clear.
//
// The key is either supplied through CARP_SECRET_KEY (32 bytes, base64 or hex)
// or generated on first use at <data>/secret.key with mode 0600. A missing or
// unreadable key file is fatal for the caller: silently falling back to a new
// key would make every previously sealed value unreadable.
package secretbox

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// KeySize is the AES-256 key length.
const KeySize = 32

// ErrNoKey reports a key file that exists but does not decode to 32 bytes.
var ErrNoKey = errors.New("secretbox: invalid key")

// LoadKey resolves the sealing key. A non-empty envKey wins; otherwise
// <dataDir>/secret.key is read, or created when absent.
func LoadKey(dataDir, envKey string) ([]byte, error) {
	if trimmed := strings.TrimSpace(envKey); trimmed != "" {
		return decodeKey(trimmed)
	}
	path := filepath.Join(dataDir, "secret.key")
	raw, err := os.ReadFile(path)
	if err == nil {
		return decodeKey(strings.TrimSpace(string(raw)))
	}
	if !os.IsNotExist(err) {
		return nil, fmt.Errorf("secretbox: read key file: %w", err)
	}
	if err := os.MkdirAll(dataDir, 0o700); err != nil {
		return nil, fmt.Errorf("secretbox: create data dir: %w", err)
	}
	key := make([]byte, KeySize)
	if _, err := rand.Read(key); err != nil {
		return nil, fmt.Errorf("secretbox: generate key: %w", err)
	}
	if err := os.WriteFile(path, []byte(base64.StdEncoding.EncodeToString(key)+"\n"), 0o600); err != nil {
		return nil, fmt.Errorf("secretbox: write key file: %w", err)
	}
	return key, nil
}

// decodeKey accepts base64 (standard or raw) and hex encodings of a 32 byte
// key, which covers every form a deployment script is likely to produce.
func decodeKey(value string) ([]byte, error) {
	candidates := []func(string) ([]byte, error){
		base64.StdEncoding.DecodeString,
		base64.RawStdEncoding.DecodeString,
		hex.DecodeString,
	}
	for _, decode := range candidates {
		if key, err := decode(value); err == nil && len(key) == KeySize {
			return key, nil
		}
	}
	if len(value) == KeySize {
		return []byte(value), nil
	}
	return nil, ErrNoKey
}

// Seal encrypts plaintext and returns base64(nonce || ciphertext). An empty
// plaintext still produces a valid box, so "password set to empty" is stored
// as an empty plaintext rather than a missing key.
func Seal(key []byte, plaintext string) (string, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return "", fmt.Errorf("secretbox: cipher: %w", err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", fmt.Errorf("secretbox: gcm: %w", err)
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return "", fmt.Errorf("secretbox: nonce: %w", err)
	}
	box := gcm.Seal(nonce, nonce, []byte(plaintext), nil)
	return base64.StdEncoding.EncodeToString(box), nil
}

// Open reverses Seal. A decoding or authentication failure is reported as an
// error; callers treat it as "password must be entered again".
func Open(key []byte, encoded string) (string, error) {
	raw, err := base64.StdEncoding.DecodeString(strings.TrimSpace(encoded))
	if err != nil {
		return "", fmt.Errorf("secretbox: decode: %w", err)
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return "", fmt.Errorf("secretbox: cipher: %w", err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", fmt.Errorf("secretbox: gcm: %w", err)
	}
	if len(raw) < gcm.NonceSize() {
		return "", ErrNoKey
	}
	nonce, box := raw[:gcm.NonceSize()], raw[gcm.NonceSize():]
	plaintext, err := gcm.Open(nil, nonce, box, nil)
	if err != nil {
		return "", fmt.Errorf("secretbox: open: %w", err)
	}
	return string(plaintext), nil
}
