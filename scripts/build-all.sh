#!/usr/bin/env bash
# Full-matrix release build: frontend -> Go cores for every supported OS/arch
# -> Linux tarball packages + Windows core zips + Windows GUI single-file builds.
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
export PATH="$NODE_DIR:$PATH"
export GOTOOLCHAIN="${GOTOOLCHAIN:-local}"

VERSION="${VERSION:-$(git describe --tags --always --dirty 2>/dev/null || echo dev)}"
COMMIT="${COMMIT:-$(git rev-parse --short HEAD 2>/dev/null || echo none)}"
BUILD_TIME="${BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"
LDFLAGS="-s -w -X carp/internal/platform/version.Version=$VERSION -X carp/internal/platform/version.Commit=$COMMIT -X carp/internal/platform/version.BuildTime=$BUILD_TIME"

PY_BIN=""
for cand in "${PYTHON_BIN:-}" "$(command -v python3 2>/dev/null || true)" "$(command -v python 2>/dev/null || true)" "C:/Users/hm/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/python/python.exe"; do
  if [ -n "$cand" ] && "$cand" -c 'import sys' >/dev/null 2>&1; then
    PY_BIN="$cand"
    break
  fi
done
if [ -z "$PY_BIN" ]; then
  echo "build failed: a working python is required for packaging" >&2
  exit 1
fi

echo "> building frontend"
find web/dist -mindepth 1 ! -name '.keep' -exec rm -rf {} + 2>/dev/null || true
run_pnpm --dir web run build
if [ ! -f web/dist/index.html ]; then
  echo "build failed: web/dist/index.html is missing" >&2
  exit 1
fi

build_target() {
  local goos="$1" goarch="$2" out="$3" goarm="${4:-}"
  mkdir -p "$(dirname "$out")"
  echo "> building $goos/$goarch$([ -n "$goarm" ] && echo " (GOARM=$goarm)")"
  if [ -n "$goarm" ]; then
    GOOS="$goos" GOARCH="$goarch" GOARM="$goarm" "$GO_BIN" build -trimpath -ldflags "$LDFLAGS" -o "$out" ./cmd/carp
  else
    GOOS="$goos" GOARCH="$goarch" "$GO_BIN" build -trimpath -ldflags "$LDFLAGS" -o "$out" ./cmd/carp
  fi
}

build_target windows amd64 dist/core/carp.exe
build_target windows amd64 dist/win/amd64/carp.exe
build_target windows arm64 dist/win/arm64/carp.exe
build_target linux amd64 dist/linux/carp-linux-amd64
build_target linux arm64 dist/linux/carp-linux-arm64
build_target linux arm dist/linux/carp-linux-armv7 7

for arch in amd64 arm64 armv7; do
  bin="dist/linux/carp-linux-$arch"
  if [ "$(head -c 4 "$bin" | od -An -tx1 | tr -d ' \n')" != "7f454c46" ]; then
    echo "build failed: $bin is not an ELF binary" >&2
    exit 1
  fi
done

package_linux() {
  local arch="$1" bin="$2"
  local PKG="carp-$VERSION-linux-$arch"
  local PKG_DIR="dist/linux/package/$PKG"
  echo "> packaging $PKG"
  rm -rf "$PKG_DIR"
  mkdir -p "$PKG_DIR/scripts"
  cp "$bin" "$PKG_DIR/carp"
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
}

package_linux amd64 dist/linux/carp-linux-amd64
package_linux arm64 dist/linux/carp-linux-arm64
package_linux armv7 dist/linux/carp-linux-armv7

package_windows_core() {
  local arch="$1" bin="$2"
  local dir="dist/win/package-$arch"
  local zip="dist/win/carp-$VERSION-windows-$arch-core.zip"
  echo "> packaging Windows $arch core"
  rm -rf "$dir" "$zip"
  mkdir -p "$dir"
  cp "$bin" "$dir/carp.exe"
  printf '%s\ncommit: %s\nbuilt: %s\n' "$VERSION" "$COMMIT" "$BUILD_TIME" > "$dir/VERSION"
  cat > "$dir/README.md" <<'CORE_EOF'
# Carp Windows core (CLI + built-in web UI)

Run `carp.exe serve` and open http://127.0.0.1:50011.
The GUI build (`Carp-<version>-win-<arch>.zip`) embeds this core and is the
recommended desktop entry point.
CORE_EOF
  "$PY_BIN" - "$dir" "$zip" <<'PYEOF'
import os, sys, zipfile

root, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
    for name in sorted(os.listdir(root)):
        zf.write(os.path.join(root, name), name)
print("wrote", out)
PYEOF
}

package_windows_core amd64 dist/win/amd64/carp.exe
package_windows_core arm64 dist/win/arm64/carp.exe

echo "> cores + packages done"
for f in dist/core/carp.exe dist/win/amd64/carp.exe dist/win/arm64/carp.exe \
  dist/linux/carp-linux-amd64 dist/linux/carp-linux-arm64 dist/linux/carp-linux-armv7 \
  dist/linux/carp-$VERSION-linux-amd64.tar.gz dist/linux/carp-$VERSION-linux-arm64.tar.gz dist/linux/carp-$VERSION-linux-armv7.tar.gz \
  dist/win/carp-$VERSION-windows-amd64-core.zip dist/win/carp-$VERSION-windows-arm64-core.zip; do
  printf '  %-70s %s\n' "$f" "$(du -h "$f" | cut -f1)"
done
