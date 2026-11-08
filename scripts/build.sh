#!/usr/bin/env bash
# Release build: frontend -> embedded Go binary at dist/carp.exe.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

GO_BIN="${GO_BIN:-$(command -v go || true)}"
if [ -z "$GO_BIN" ]; then
  GO_BIN="/d/dshwork/.tools/go/bin/go"
fi
NODE_BIN="${NODE_BIN:-$(command -v node || true)}"
if [ -z "$NODE_BIN" ]; then
  NODE_BIN="C:/Users/hm/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/node/bin/node.exe"
fi
if [ -z "${PNPM_JS:-}" ]; then
  if command -v pnpm >/dev/null 2>&1; then
    PNPM_JS=""
  else
    PNPM_JS="$(cd "$(dirname "$NODE_BIN")/../.." && pwd)/pnpm/bin/pnpm.mjs"
  fi
fi
run_pnpm() {
  if [ -n "$PNPM_JS" ]; then
    "$NODE_BIN" "$PNPM_JS" "$@"
  else
    pnpm "$@"
  fi
}
NODE_DIR="$(dirname "$NODE_BIN")"
if command -v cygpath >/dev/null 2>&1; then
  NODE_DIR="$(cygpath -u "$NODE_DIR")"
fi
# pnpm's run-script shims resolve `node` from PATH; keep ours first.
export PATH="$NODE_DIR:$PATH"
export GOTOOLCHAIN="${GOTOOLCHAIN:-local}"

VERSION="${VERSION:-$(git describe --tags --always --dirty 2>/dev/null || echo dev)}"
COMMIT="${COMMIT:-$(git rev-parse --short HEAD 2>/dev/null || echo none)}"
BUILD_TIME="${BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"

if [ ! -d web/node_modules ]; then
  echo "> installing frontend dependencies"
  run_pnpm --dir web install --frozen-lockfile
fi

echo "> building frontend"
# Vite runs with emptyOutDir=false so the committed web/dist/.keep survives;
# clear the stale bundle ourselves, keeping .keep.
find web/dist -mindepth 1 ! -name '.keep' -exec rm -rf {} + 2>/dev/null || true
run_pnpm --dir web run build
if [ ! -f web/dist/index.html ]; then
  echo "build failed: web/dist/index.html is missing" >&2
  exit 1
fi

echo "> building core carp.exe ($VERSION, $COMMIT, $BUILD_TIME)"
mkdir -p dist/core dist/linux
"$GO_BIN" build -trimpath \
  -ldflags "-s -w \
    -X carp/internal/platform/version.Version=$VERSION \
    -X carp/internal/platform/version.Commit=$COMMIT \
    -X carp/internal/platform/version.BuildTime=$BUILD_TIME" \
  -o dist/core/carp.exe ./cmd/carp

echo "> building carp-linux-amd64"
( export GOOS=linux GOARCH=amd64
  "$GO_BIN" build -trimpath \
    -ldflags "-s -w \
      -X carp/internal/platform/version.Version=$VERSION \
      -X carp/internal/platform/version.Commit=$COMMIT \
      -X carp/internal/platform/version.BuildTime=$BUILD_TIME" \
    -o dist/linux/carp-linux-amd64 ./cmd/carp )
if [ "$(head -c 4 dist/linux/carp-linux-amd64 | od -An -tx1 | tr -d ' \n')" != "7f454c46" ]; then
  echo "build failed: dist/linux/carp-linux-amd64 is not an ELF binary" >&2
  exit 1
fi

PKG="carp-$VERSION-linux-amd64"
PKG_DIR="dist/linux/package/$PKG"
echo "> packaging $PKG"
rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR/scripts"
cp dist/linux/carp-linux-amd64 "$PKG_DIR/carp"
cp scripts/install.sh scripts/carp.service scripts/carp.env.example "$PKG_DIR/"
cp scripts/nginx-carp.conf "$PKG_DIR/"
cp scripts/backup.sh scripts/restore.sh scripts/healthcheck.sh "$PKG_DIR/scripts/"
cat > "$PKG_DIR/INSTALL.md" <<'INSTALLEOF'
# Carp Linux 安装

- `sudo ./install.sh`：安装为 systemd 服务（推荐）
- `./carp serve`：便携直跑
- 完整手册：https://ovonvm.github.io/carp/
INSTALLEOF

printf '%s\ncommit: %s\nbuilt: %s\n' "$VERSION" "$COMMIT" "$BUILD_TIME" > "$PKG_DIR/VERSION"
chmod +x "$PKG_DIR/install.sh" "$PKG_DIR/scripts/"*.sh "$PKG_DIR/carp"
( cd "$PKG_DIR" && sha256sum carp install.sh carp.service carp.env.example nginx-carp.conf INSTALL.md VERSION scripts/backup.sh scripts/restore.sh scripts/healthcheck.sh > SHA256SUMS )
# Git Bash cannot store POSIX exec bits on NTFS, so build the tarball with
# explicit modes instead of `tar -czf` (which would drop +x from carp/scripts).
PY_BIN=""
for cand in "${PYTHON_BIN:-}" "$(command -v python3 2>/dev/null || true)" "$(command -v python 2>/dev/null || true)" "C:/Users/hm/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/python/python.exe"; do
  if [ -n "$cand" ] && "$cand" -c 'import sys' >/dev/null 2>&1; then
    PY_BIN="$cand"
    break
  fi
done
if [ -z "$PY_BIN" ]; then
  echo "build failed: a working python is required to package the tarball" >&2
  exit 1
fi
"$PY_BIN" - "$PKG_DIR" "dist/linux/$PKG.tar.gz" "$PKG" <<'PYEOF'
import os, sys, tarfile

root, out, name = sys.argv[1], sys.argv[2], sys.argv[3]

def add_mode(ti):
    base = os.path.basename(ti.name)
    if ti.isdir():
        ti.mode = 0o755
    elif base in ("carp", "install.sh") or base.endswith(".sh"):
        ti.mode = 0o755
    else:
        ti.mode = 0o644
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = "root"
    return ti

with tarfile.open(out, "w:gz") as tf:
    tf.add(root, arcname=name, filter=add_mode)
print("wrote", out)
PYEOF

echo "> done: dist/core/carp.exe ($(du -h dist/core/carp.exe | cut -f1))"
echo "  linux binary: dist/linux/carp-linux-amd64 ($(du -h dist/linux/carp-linux-amd64 | cut -f1))"
echo "  linux package: dist/linux/$PKG.tar.gz ($(du -h "dist/linux/$PKG.tar.gz" | cut -f1))"
echo "  run: data dir defaults to <exe dir>/data; open http://127.0.0.1:50011"
