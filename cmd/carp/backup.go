package main

import (
	"archive/zip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"carp/internal/infra/sql"
	"carp/internal/platform/config"
	"carp/internal/platform/version"
)

type backupManifest struct {
	CreatedAt         string `json:"created_at"`
	Version           string `json:"version"`
	DataDir           string `json:"data_dir"`
	Label             string `json:"label,omitempty"`
	DBFile            string `json:"db_file"`
	DBSHA256          string `json:"db_sha256"`
	StorageFiles      int    `json:"storage_files"`
	SecretKeyExcluded bool   `json:"secret_key_excluded"`
}

// runBackupCLI implements `carp backup`: a portable, single-file snapshot for
// the Windows control panel. It is intentionally independent from the Linux
// backup.sh layout (directory snapshots with rsync hard links).
func runBackupCLI(args []string) int {
	flags, err := parseCommandFlags(args, map[string]bool{
		"data": true, "out": true, "no-secret": false, "label": true,
	})
	if err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: %v\n", err)
		return 2
	}
	dataDir := flags["data"]
	if dataDir == "" {
		dataDir = config.DefaultDataDir()
	}
	absData, err := filepath.Abs(dataDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: data dir: %v\n", err)
		return 1
	}
	dbPath := filepath.Join(absData, "db", "carp.db")
	if _, err := os.Stat(dbPath); err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: database not found: %s\n", dbPath)
		return 1
	}
	outDir := flags["out"]
	if outDir == "" {
		fmt.Fprintln(os.Stderr, "carp backup: -out <dir> is required")
		return 2
	}
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: create out dir: %v\n", err)
		return 1
	}
	staging, err := os.MkdirTemp(outDir, ".carp-backup-staging-")
	if err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: temp dir: %v\n", err)
		return 1
	}
	defer os.RemoveAll(staging)

	snapDB := filepath.Join(staging, "carp.db")
	if err := snapshotSQLite(context.Background(), dbPath, snapDB); err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: snapshot database: %v\n", err)
		return 1
	}
	if err := os.Chmod(snapDB, 0o600); err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: chmod snapshot: %v\n", err)
		return 1
	}
	dbSum, err := fileSHA256(snapDB)
	if err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: hash database: %v\n", err)
		return 1
	}

	ts := time.Now().UTC().Format("20060102-150405")
	name := "carp-backup-" + ts
	if label := sanitizeLabel(flags["label"]); label != "" {
		name += "-" + label
	}
	zipPath := filepath.Join(outDir, name+".zip")
	for i := 1; ; i++ {
		if _, err := os.Stat(zipPath); os.IsNotExist(err) {
			break
		}
		zipPath = filepath.Join(outDir, fmt.Sprintf("%s-%d.zip", name, i))
	}
	tmpZip, err := os.CreateTemp(outDir, ".carp-backup-*.zip")
	if err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: create zip: %v\n", err)
		return 1
	}
	tmpZipPath := tmpZip.Name()
	defer os.Remove(tmpZipPath)

	storageFiles, err := writeBackupZip(tmpZip, snapDB, filepath.Join(absData, "storage"), backupManifest{
		CreatedAt:         time.Now().UTC().Format(time.RFC3339),
		Version:           version.String(),
		DataDir:           absData,
		Label:             flags["label"],
		DBFile:            "db/carp.db",
		DBSHA256:          dbSum,
		SecretKeyExcluded: true,
	})
	if err != nil {
		_ = tmpZip.Close()
		fmt.Fprintf(os.Stderr, "carp backup: write zip: %v\n", err)
		return 1
	}
	if err := tmpZip.Close(); err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: close zip: %v\n", err)
		return 1
	}
	if err := os.Rename(tmpZipPath, zipPath); err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: publish zip: %v\n", err)
		return 1
	}
	sum, err := fileSHA256(zipPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "carp backup: hash zip: %v\n", err)
		return 1
	}
	fmt.Printf("backup: %s\n", zipPath)
	fmt.Printf("sha256: %s\n", sum)
	fmt.Printf("storage files: %d (secret.key excluded)\n", storageFiles)
	return 0
}

func snapshotSQLite(ctx context.Context, src, dst string) error {
	db, err := sqlstore.Open(ctx, src)
	if err != nil {
		return err
	}
	defer db.Close()
	// VACUUM INTO takes a string literal, not a bind parameter. Paths are
	// internal (data dir + temp dir), but escape single quotes defensively.
	escaped := strings.ReplaceAll(filepath.ToSlash(dst), "'", "''")
	if _, err := db.ExecContext(ctx, "VACUUM INTO '"+escaped+"'"); err != nil {
		return err
	}
	return nil
}

func writeBackupZip(dst io.Writer, dbFile, storageRoot string, m backupManifest) (int, error) {
	zw := zip.NewWriter(dst)
	add := func(name string, mode os.FileMode, data io.Reader) error {
		h := &zip.FileHeader{Name: name, Method: zip.Deflate, Modified: time.Now()}
		h.SetMode(mode)
		w, err := zw.CreateHeader(h)
		if err != nil {
			return err
		}
		_, err = io.Copy(w, data)
		return err
	}
	db, err := os.Open(dbFile)
	if err != nil {
		return 0, err
	}
	if err := add("db/carp.db", 0o600, db); err != nil {
		_ = db.Close()
		return 0, err
	}
	_ = db.Close()

	files := 0
	if storageRoot != "" {
		err = filepath.Walk(storageRoot, func(path string, info os.FileInfo, walkErr error) error {
			if walkErr != nil {
				return walkErr
			}
			if info.IsDir() {
				return nil
			}
			rel, err := filepath.Rel(storageRoot, path)
			if err != nil {
				return err
			}
			rel = filepath.ToSlash(rel)
			if rel == "secret.key" || strings.HasSuffix(rel, "/secret.key") {
				return nil
			}
			f, err := os.Open(path)
			if err != nil {
				return err
			}
			defer f.Close()
			if err := add("storage/"+rel, info.Mode().Perm(), f); err != nil {
				return err
			}
			files++
			return nil
		})
		if err != nil && !os.IsNotExist(err) {
			_ = zw.Close()
			return files, err
		}
	}
	m.StorageFiles = files
	raw, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		_ = zw.Close()
		return files, err
	}
	raw = append(raw, '\n')
	if err := add("manifest.json", 0o644, strings.NewReader(string(raw))); err != nil {
		_ = zw.Close()
		return files, err
	}
	return files, zw.Close()
}

func fileSHA256(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func sanitizeLabel(s string) string {
	s = strings.TrimSpace(strings.ToLower(s))
	var b strings.Builder
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-' || r == '_' {
			b.WriteRune(r)
		}
	}
	return b.String()
}
