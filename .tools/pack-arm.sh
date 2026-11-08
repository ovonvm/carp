#!/usr/bin/env bash
# Package a Linux ARM release from the same tree as scripts/build.sh.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

GO_BIN="${GO_BIN:-C:/Users/hm/go-sdk/go/bin/go.exe}"
VERSION="${VERSION:-$(git describe --tags --always --dirty 2>/dev/null || echo dev)}"
COMMIT="${COMMIT:-$(git rev-parse --short HEAD 2>/dev/null || echo none)}"
BUILD_TIME="${BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"
GOARCH_ARM="${1:?usage: pack-arm.sh <arm64|armv7>}"
case "$GOARCH_ARM" in
  arm64) export GOOS=linux GOARCH=arm64; GOARM_VALUE="";;
  armv7) export GOOS=linux GOARCH=arm GOARM=7; GOARM_VALUE="GOARM=7";;
  *) echo "unknown arm target: $GOARCH_ARM" >&2; exit 2;;
esac

BIN="dist/linux/carp-linux-$GOARCH_ARM"
echo "> building $BIN"
"$GO_BIN" build -trimpath \
  -ldflags "-s -w \
    -X carp/internal/platform/version.Version=$VERSION \
    -X carp/internal/platform/version.Commit=$COMMIT \
    -X carp/internal/platform/version.BuildTime=$BUILD_TIME" \
  -o "$BIN" ./cmd/carp
if [ "$(head -c 4 "$BIN" | od -An -tx1 | tr -d ' \n')" != "7f454c46" ]; then
  echo "build failed: $BIN is not an ELF binary" >&2
  exit 1
fi

PKG="carp-$VERSION-linux-$GOARCH_ARM"
PKG_DIR="dist/linux/package/$PKG"
rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR/scripts"
cp "$BIN" "$PKG_DIR/carp"
cp scripts/install.sh scripts/carp.service scripts/carp.env.example "$PKG_DIR/"
cp scripts/nginx-carp.conf "$PKG_DIR/"
cp scripts/backup.sh scripts/restore.sh scripts/healthcheck.sh "$PKG_DIR/scripts/"
cat > "$PKG_DIR/INSTALL.md" <<EOF
# Carp $VERSION for Linux $GOARCH_ARM

1. 解压后运行 \`sudo ./install.sh\`（会安装到 /opt/carp 并注册 systemd 服务）。
2. 浏览器打开 http://<服务器IP>:50011，用安装时创建的 root 账号登录。
3. 首次启动的管理员初始密码在 /opt/carp/data/bootstrap/initial-admin.txt。

动态依赖：无（静态链接）。ARM 版本与 amd64 使用同一份前端与配置。
EOF
printf '%s\ncommit: %s\nbuilt: %s\narch: linux/%s\n' "$VERSION" "$COMMIT" "$BUILD_TIME" "$GOARCH_ARM" > "$PKG_DIR/VERSION"
chmod +x "$PKG_DIR/install.sh" "$PKG_DIR/scripts/"*.sh "$PKG_DIR/carp"
( cd "$PKG_DIR" && sha256sum carp install.sh carp.service carp.env.example nginx-carp.conf INSTALL.md VERSION scripts/backup.sh scripts/restore.sh scripts/healthcheck.sh > SHA256SUMS )

PY_BIN=""
for cand in "${PYTHON_BIN:-}" "$(command -v python3 2>/dev/null || true)" "C:/Users/hm/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/python/python.exe"; do
  if [ -n "$cand" ] && "$cand" -c 'import sys' >/dev/null 2>&1; then PY_BIN="$cand"; break; fi
done
"$PY_BIN" - "$PKG_DIR" "dist/linux/$PKG.tar.gz" "$PKG" <<'PYEOF'
import os, sys, tarfile
root, out, name = sys.argv[1], sys.argv[2], sys.argv[3]
def add_mode(ti):
    base = os.path.basename(ti.name)
    ti.mode = 0o755 if (ti.isdir() or base in ("carp", "install.sh") or base.endswith(".sh")) else 0o644
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = "root"
    return ti
with tarfile.open(out, "w:gz") as tf:
    tf.add(root, arcname=name, filter=add_mode)
print("wrote", out)
PYEOF
echo "  $PKG.tar.gz $(du -h "dist/linux/$PKG.tar.gz" | cut -f1)"
