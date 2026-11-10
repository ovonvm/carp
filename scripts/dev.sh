#!/usr/bin/env bash
# Carp dev loop: SQLite + Go API on :50011 and the Vite dev server on :5173.
# The Vite server proxies /api and /health to the API, so open
# http://127.0.0.1:5173 in a browser.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

GO_BIN="${GO_BIN:-$(command -v go || true)}"
if [ -z "$GO_BIN" ]; then
  GO_BIN="$(go env GOPATH 2>/dev/null)/bin/go"
fi
NODE_BIN="${NODE_BIN:-$(command -v node || true)}"
if [ -z "$NODE_BIN" ]; then
  NODE_BIN="C:/Users/hm/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/node/bin/node.exe"
fi
PNPM_JS="${PNPM_JS:-$(cd "$(dirname "$NODE_BIN")/../.." && pwd)/pnpm/bin/pnpm.mjs}"
NODE_DIR="$(dirname "$NODE_BIN")"
if command -v cygpath >/dev/null 2>&1; then
  NODE_DIR="$(cygpath -u "$NODE_DIR")"
fi
# pnpm's run-script shims resolve `node` from PATH; keep ours first.
export PATH="$NODE_DIR:$PATH"
export GOTOOLCHAIN="${GOTOOLCHAIN:-local}"

API_ADDR="${API_ADDR:-127.0.0.1:50011}"
WEB_ORIGIN="${WEB_ORIGIN:-http://127.0.0.1:5173}"
# Vite proxies /api and /health here (see web/vite.config.ts).
export CARP_API_ORIGIN="http://$API_ADDR"
DEV_BIN="$ROOT/data/.dev/carp.exe"
mkdir -p "$ROOT/data/.dev"

"$GO_BIN" build -o "$DEV_BIN" ./cmd/carp

pids=()
cleanup() {
  for pid in "${pids[@]:-}"; do
    kill "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo "> API   $API_ADDR (log level debug, Vite origin allowed)"
"$DEV_BIN" serve -addr "$API_ADDR" -log-level debug -allow-origin "$WEB_ORIGIN" &
pids+=("$!")

echo "> Web   http://127.0.0.1:5173"
"$NODE_BIN" "$PNPM_JS" --dir web run dev &
pids+=("$!")

wait
