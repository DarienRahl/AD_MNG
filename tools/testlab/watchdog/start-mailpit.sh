#!/usr/bin/env bash
# Starts Mailpit (SMTP sink with a web UI and REST API) for the AdMng Watchdog lab and the integration tests.
#
#   tools/testlab/watchdog/start-mailpit.sh [--require-starttls]
#
# SMTP 127.0.0.1:1025 (STARTTLS offered with a self-signed "localhost" certificate, any user/password accepted),
# UI and API http://127.0.0.1:8025 (GET /api/v1/messages). The pinned release is downloaded once into
# ~/.cache/admng/mailpit/<version>/ – into its own empty directory, verified by SHA-256 before it is unpacked.
# Messages live only in Mailpit's temporary database and disappear with stop-mailpit.sh.
#
# Environment: ADMNG_MAILPIT_HOME, ADMNG_MAILPIT_SMTP_PORT, ADMNG_MAILPIT_UI_PORT.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

require_starttls=false
for arg in "$@"; do
  case "$arg" in
    --require-starttls) require_starttls=true ;;
    -h | --help)
      sed -n '2,12p' "$0"
      exit 0
      ;;
    *) die "Unknown option: $arg" ;;
  esac
done

require curl tar sha256sum

case "$(uname -m)" in
  x86_64 | amd64)
    asset=mailpit-linux-amd64.tar.gz
    sha256=5924ace84f38c83c29f941a644a2d7be6c11f339eb4a9a12ab526414897ceed1
    ;;
  aarch64 | arm64)
    asset=mailpit-linux-arm64.tar.gz
    sha256=ff9b03dd4a01e9680d89837d391296942c0529d545d160a98ed62882eec0e628
    ;;
  *) die "No pinned Mailpit build for $(uname -m)." ;;
esac

install_dir="$MAILPIT_HOME/$MAILPIT_VERSION"
binary="$install_dir/mailpit"
pid_file="$MAILPIT_HOME/mailpit.pid"
log_file="$MAILPIT_HOME/mailpit.log"
mkdir -p "$MAILPIT_HOME"

if [ ! -x "$binary" ]; then
  download="$(mktemp -d "$MAILPIT_HOME/download.XXXXXX")"
  trap 'rm -rf -- "$download"' EXIT
  url="https://github.com/axllent/mailpit/releases/download/$MAILPIT_VERSION/$asset"
  log "Downloading Mailpit $MAILPIT_VERSION ($asset)…"
  curl -fsSL --retry 3 -o "$download/$asset" "$url"
  echo "$sha256  $download/$asset" | sha256sum -c --quiet - || die "SHA-256 of $asset does not match the pinned value."
  mkdir -p "$download/unpacked"
  tar -xzf "$download/$asset" -C "$download/unpacked" mailpit
  mkdir -p "$install_dir"
  install -m 0755 "$download/unpacked/mailpit" "$binary"
  log "Mailpit installed in $install_dir."
fi

if pid="$(running_pid "$pid_file")"; then
  log "Mailpit is already running (PID $pid): SMTP 127.0.0.1:$MAILPIT_SMTP_PORT, UI http://127.0.0.1:$MAILPIT_UI_PORT"
  exit 0
fi

if port_open "$MAILPIT_SMTP_PORT" || port_open "$MAILPIT_UI_PORT"; then
  die "Port $MAILPIT_SMTP_PORT or $MAILPIT_UI_PORT is already in use by another process."
fi

args=(
  --smtp "127.0.0.1:$MAILPIT_SMTP_PORT"
  --listen "127.0.0.1:$MAILPIT_UI_PORT"
  --smtp-auth-accept-any
  --smtp-tls-cert sans:localhost
  --smtp-tls-key sans:localhost
  --enable-chaos
  --disable-version-check
  --quiet
)
if [ "$require_starttls" = true ]; then
  args+=(--smtp-require-starttls)
fi

setsid "$binary" "${args[@]}" > "$log_file" 2>&1 < /dev/null &
echo $! > "$pid_file"

if ! wait_for_port "$MAILPIT_SMTP_PORT" 20 || ! wait_for_port "$MAILPIT_UI_PORT" 20; then
  cat "$log_file" >&2 || true
  stop_pid_file "$pid_file" Mailpit
  die "Mailpit did not start."
fi

log "Mailpit $MAILPIT_VERSION running (PID $(cat "$pid_file")): SMTP 127.0.0.1:$MAILPIT_SMTP_PORT, UI http://127.0.0.1:$MAILPIT_UI_PORT"
