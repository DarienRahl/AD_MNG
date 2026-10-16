#!/usr/bin/env bash
# Runs the AdMng Watchdog service against the Samba lab (launch profile "Lab", appsettings.Lab.json).
#
#   tools/testlab/watchdog/run-watchdog-lab.sh [--reset] [--background] [--no-build]
#   tools/testlab/watchdog/run-watchdog-lab.sh --stop
#
#   --reset       delete the lab data folder (~/.local/share/admng-watchdog-lab: database, key ring, backups) first –
#                 the next start takes a new baseline
#   --background  start the service in the background (log: ~/.cache/admng/watchdog-lab/watchdog.log) and return
#                 once /healthz answers; stop it with --stop
#   --no-build    pass --no-build to dotnet run
#   --stop        stop a background service, the webhook sink and Mailpit
#
# Starts Mailpit (start-mailpit.sh) and the webhook sink (webhook-sink.py, 127.0.0.1:8091) first. The service reads
# the directory as odczyt.test with a simple bind; its password is handed over only in the environment variable
# ADMNG_WATCHDOG_LDAP_PASSWORD (from ADMNG_TEST_LDAP_READONLY_PASSWORD, default: the public lab value) and is never
# written to a file. API: http://127.0.0.1:5089 (Development authentication, loopback only) – configure-lab.sh
# points SMTP at Mailpit and a webhook channel at the sink.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

DATA_DIR="$HOME/.local/share/admng-watchdog-lab"
SERVICE_PID="$WATCHDOG_LAB_STATE/watchdog.pid"
SERVICE_LOG="$WATCHDOG_LAB_STATE/watchdog.log"
SINK_PID="$WATCHDOG_LAB_STATE/webhook-sink.pid"
SINK_LOG="$WATCHDOG_LAB_STATE/webhook-sink.log"

reset=false
background=false
stop=false
run_args=()
for arg in "$@"; do
  case "$arg" in
    --reset) reset=true ;;
    --background) background=true ;;
    --no-build) run_args+=(--no-build) ;;
    --stop) stop=true ;;
    -h | --help)
      sed -n '2,20p' "$0"
      exit 0
      ;;
    *) die "Unknown option: $arg" ;;
  esac
done

mkdir -p "$WATCHDOG_LAB_STATE"
chmod 700 "$WATCHDOG_LAB_STATE"

if [ "$stop" = true ]; then
  stop_pid_file "$SERVICE_PID" "AdMng Watchdog"
  stop_pid_file "$SINK_PID" "Webhook sink"
  "$HERE/stop-mailpit.sh"
  exit 0
fi

require dotnet python3 curl setsid

if pid="$(running_pid "$SERVICE_PID")"; then
  die "The lab service is already running in the background (PID $pid) – use --stop first."
fi

if [ "$reset" = true ]; then
  log "Deleting the lab data folder $DATA_DIR."
  rm -rf -- "$DATA_DIR"
fi

"$HERE/start-mailpit.sh"

if pid="$(running_pid "$SINK_PID")"; then
  log "Webhook sink already running (PID $pid)."
else
  port_open "$SINK_PORT" && die "Port $SINK_PORT is already in use by another process."
  SINK_PORT="$SINK_PORT" SINK_SECRET="$SINK_SECRET" setsid python3 -u "$HERE/webhook-sink.py" > "$SINK_LOG" 2>&1 < /dev/null &
  echo $! > "$SINK_PID"
  wait_for_port "$SINK_PORT" 10 || die "The webhook sink did not start (see $SINK_LOG)."
  log "Webhook sink on http://127.0.0.1:$SINK_PORT (log $SINK_LOG)."
fi

export ASPNETCORE_ENVIRONMENT=Development
export ADMNG_WATCHDOG_LDAP_PASSWORD="$LAB_READONLY_PASSWORD"
export DOTNET_NOLOGO=1

# The launch profile names appsettings.Lab.json relative to the project folder, so run from there.
cd "$REPO/src/AdMng.Watchdog.Service"
if [ "$background" = false ]; then
  log "Starting AdMng Watchdog (Ctrl+C stops it; Mailpit and the sink keep running – stop them with --stop)."
  exec dotnet run --launch-profile Lab "${run_args[@]}"
fi

setsid dotnet run --launch-profile Lab "${run_args[@]}" > "$SERVICE_LOG" 2>&1 < /dev/null &
echo $! > "$SERVICE_PID"
log "AdMng Watchdog starting in the background (PID $(cat "$SERVICE_PID"), log $SERVICE_LOG)…"
for ((i = 0; i < 1800; i++)); do
  if curl -fsS "$WATCHDOG_API/healthz" > /dev/null 2>&1; then
    log "AdMng Watchdog is up: $WATCHDOG_API (Mailpit UI http://127.0.0.1:$MAILPIT_UI_PORT)."
    exit 0
  fi
  if ! running_pid "$SERVICE_PID" > /dev/null; then
    tail -n 40 "$SERVICE_LOG" >&2 || true
    die "The service exited during start-up."
  fi
  sleep 0.1
done
die "The service did not answer /healthz within 3 minutes (see $SERVICE_LOG)."
