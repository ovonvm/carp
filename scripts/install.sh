#!/usr/bin/env bash
# Carp Linux installer (thin wrapper around `carp install`).
#
# Usage:
#   sudo ./install.sh                    # /opt/carp, user carp, 127.0.0.1:50011
#   sudo ./install.sh --no-service       # only copy files, no systemd unit
#   sudo ./install.sh --prefix /srv/carp --user carp --port 50011
#   sudo ./install.sh --uninstall        # remove everything (including data)
#
# All logic lives in the binary so the installer and `carp install` can never
# drift apart. This script only locates the binary and checks root.
set -Eeuo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="${CARP_BINARY:-$HERE/carp}"

if [[ ! -x "$BIN" ]]; then
  echo "install.sh: carp binary not found at $BIN" >&2
  echo "           set CARP_BINARY=/path/to/carp if it lives elsewhere" >&2
  exit 1
fi

for arg in "$@"; do
  case "$arg" in
    -h|--help)
      sed -n '2,16p' "$0"
      exit 0
      ;;
  esac
done

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "install.sh: root is required; run: sudo $0 $*" >&2
  exit 1
fi

if [[ "${1:-}" == "--uninstall" ]]; then
  shift || true
  exec "$BIN" uninstall "$@"
fi

exec "$BIN" install "$@"
