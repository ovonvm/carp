#!/usr/bin/env bash
# 构建 Windows 单文件控制面板（.NET 8 WPF 自包含 + 内嵌 Go 核，需管理员运行）。
# 用法：scripts/build-gui.sh [x64|arm64]   （默认 x64）
# 产物：dist/Carp.exe（x64）/ dist/Carp-win-arm64.exe，以及对应 zip；
#       单文件可直接发布，zip 仅便于分发。
# 前置：先运行 scripts/build-all.sh 生成 dist/core/carp.exe 与 dist/win/arm64/carp.exe。
set -euo pipefail
cd "$(dirname "$0")/.."

ARCH="${1:-x64}"
case "$ARCH" in
  x64) RID="win-x64"; CORE="dist/core/carp.exe"; EXE="dist/Carp.exe" ;;
  arm64) RID="win-arm64"; CORE="dist/win/arm64/carp.exe"; EXE="dist/Carp-win-arm64.exe" ;;
  *) echo "ERROR: 未知架构 '$ARCH'（支持 x64 / arm64）" >&2; exit 2 ;;
esac

VERSION="${VERSION:-$(git describe --tags --always --dirty 2>/dev/null || echo dev)}"
VERSION="${VERSION#v}"
# AssemblyVersion 必须是纯数字；从 VERSION 取 x.y.z，取不到就 0.0.0。
# InformationalVersion 原样使用 VERSION，才能与 Go 核的 version.Version 精确一致
# （两者不一致会让 GUI 进入只读模式）。
NUM_VERSION="${VERSION%%-*}"
NUM_VERSION="${NUM_VERSION#v}"
if ! [[ "$NUM_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then NUM_VERSION="0.0.0"; fi
DIST=dist
GUI_DIR=gui/Carp.Gui
OUT="$DIST/gui-publish-$ARCH"

DOTNET=""
if command -v dotnet >/dev/null 2>&1; then
  DOTNET=dotnet
elif [ -x "/c/Program Files/dotnet/dotnet.exe" ]; then
  DOTNET="/c/Program Files/dotnet/dotnet.exe"
else
  echo "ERROR: 未找到 dotnet SDK（需要 .NET 8 SDK 构建 GUI）" >&2
  exit 1
fi

if [ ! -f "$CORE" ]; then
  echo "ERROR: 缺少 $CORE，请先运行 scripts/build-all.sh" >&2
  exit 1
fi

rm -rf "$OUT"
mkdir -p "$GUI_DIR/payload"

echo "> staging $ARCH core payload"
cp "$CORE" "$GUI_DIR/payload/carp-core.exe"
echo "  core version: $("$CORE" version 2>/dev/null | head -1)"

echo "> publish single-file GUI ($ARCH, $VERSION)"
"$DOTNET" publish "$GUI_DIR" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:Version="$NUM_VERSION" \
  -p:FileVersion="$NUM_VERSION.0" \
  -p:InformationalVersion="$VERSION" \
  -o "$OUT" >/dev/null

if [ ! -f "$OUT/Carp.exe" ]; then
  echo "ERROR: publish 未生成 Carp.exe" >&2
  exit 1
fi
DLL_COUNT=$(find "$OUT" -maxdepth 1 -name '*.dll' | wc -l)
if [ "$DLL_COUNT" -gt 0 ]; then
  echo "ERROR: 单文件发布目录仍包含 $DLL_COUNT 个 dll，请检查 csproj/发布参数" >&2
  exit 1
fi

cp "$OUT/Carp.exe" "$EXE"
rm -rf "$OUT" "$GUI_DIR/payload"

ZIP="$DIST/Carp-${VERSION}-win-${ARCH}.zip"
rm -f "$ZIP"
PY_BIN=""
for cand in "${PYTHON_BIN:-}" "$(command -v python3 2>/dev/null || true)" "$(command -v python 2>/dev/null || true)" "C:/Users/hm/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/python/python.exe"; do
  if [ -n "$cand" ] && "$cand" -c 'import sys' >/dev/null 2>&1; then
    PY_BIN="$cand"
    break
  fi
done
if [ -n "$PY_BIN" ]; then
  "$PY_BIN" - "$EXE" "$ZIP" <<'PYEOF'
import sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
    zf.write(src, "Carp.exe")
print("wrote", out)
PYEOF
fi

echo "  exe: $EXE ($(du -h "$EXE" | cut -f1))"
if [ -f "$ZIP" ]; then
  echo "  zip: $ZIP ($(du -h "$ZIP" | cut -f1))"
fi
