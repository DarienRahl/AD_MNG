# Shared settings and helpers of the AdMng Watchdog lab scripts (sourced, never executed).
#
# Lab passwords are public test values (tools/testlab/README.md). They are handed to the OpenLDAP tools through
# a process-substitution file descriptor (-y), never on the command line and never written to disk.
# shellcheck shell=bash disable=SC2034  # the settings are used by the scripts that source this file

LAB_SERVER="${ADMNG_TEST_LDAP_SERVER:-127.0.0.1}"
LAB_DOMAIN_DNS="${ADMNG_TEST_LDAP_DOMAIN:-contoso.test}"
LAB_BASE_DN="DC=${LAB_DOMAIN_DNS//./,DC=}"
LAB_ADMIN_USER="${ADMNG_TEST_LDAP_USER:-Administrator@${LAB_DOMAIN_DNS}}"
LAB_ADMIN_PASSWORD="${ADMNG_TEST_LDAP_PASSWORD:-P@ssw0rd!2026}"
LAB_READONLY_USER="${ADMNG_TEST_LDAP_READONLY_USER:-odczyt.test@${LAB_DOMAIN_DNS}}"
LAB_READONLY_PASSWORD="${ADMNG_TEST_LDAP_READONLY_PASSWORD:-Passw0rd!Lab2026}"

# Runtime state of the lab helpers (PID files, logs of the sinks) – never directory data.
WATCHDOG_LAB_STATE="${ADMNG_WATCHDOG_LAB_STATE:-$HOME/.cache/admng/watchdog-lab}"
# The watchdog API of the Lab launch profile (appsettings.Lab.json, Hosting.Urls).
WATCHDOG_API="${ADMNG_WATCHDOG_API:-http://127.0.0.1:5089}"

MAILPIT_VERSION="v1.31.5"
MAILPIT_HOME="${ADMNG_MAILPIT_HOME:-$HOME/.cache/admng/mailpit}"
MAILPIT_SMTP_PORT="${ADMNG_MAILPIT_SMTP_PORT:-1025}"
MAILPIT_UI_PORT="${ADMNG_MAILPIT_UI_PORT:-8025}"

SINK_PORT="${SINK_PORT:-8091}"
SINK_URL="http://127.0.0.1:$SINK_PORT"
# Standard Webhooks signing secret shared by the sink and configure-lab.sh (public lab value, 32 bytes).
SINK_SECRET="${SINK_SECRET:-whsec_QWRNbmctV2F0Y2hkb2ctbGFiLXdlYmhvb2stMjAyNiE=}"

# The throwaway OU of lab-scenario.sh and its group that configure-lab.sh lists as a privileged root.
DEMO_OU="OU=${ADMNG_WATCHDOG_DEMO_OU:-WatchdogDemo},$LAB_BASE_DN"
DEMO_TIER0_GROUP="CN=Demo Tier0,$DEMO_OU"
DEMO_TIER0_SAM="wd-demo-tier0"
LAB_ADMIN_MAILBOX="admin@$LAB_DOMAIN_DNS"
LAB_HELPDESK_MAILBOX="helpdesk@$LAB_DOMAIN_DNS"

log() { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*" >&2; }
die() { log "ERROR: $*"; exit 1; }

require() {
  local tool
  for tool in "$@"; do
    command -v "$tool" > /dev/null 2>&1 || die "'$tool' is required (Ubuntu: apt-get install ldap-utils curl jq python3 openssl)."
  done
}

# True when something listens on 127.0.0.1:<port>.
port_open() { (exec 3<> "/dev/tcp/127.0.0.1/$1") 2> /dev/null; }

# Waits up to <seconds> for 127.0.0.1:<port>.
wait_for_port() {
  local port="$1" seconds="${2:-30}" i
  for ((i = 0; i < seconds * 10; i++)); do
    if port_open "$port"; then
      return 0
    fi
    sleep 0.1
  done
  return 1
}

# PID from a PID file when that process is still running.
running_pid() {
  local file="$1" pid
  [ -f "$file" ] || return 1
  pid="$(cat "$file")"
  [ -n "$pid" ] && kill -0 "$pid" 2> /dev/null && printf '%s\n' "$pid"
}

# Stops the process of a PID file – its whole process group when it leads one (processes started with setsid, e.g.
# "dotnet run" and the service it spawned) – with TERM, then KILL after 10 s, and removes the file.
stop_pid_file() {
  local file="$1" name="$2" pid target i
  if pid="$(running_pid "$file")"; then
    target="$pid"
    if [ "$(ps -o pgid= -p "$pid" 2> /dev/null | tr -d ' ')" = "$pid" ]; then
      target="-$pid"
    fi
    kill -TERM -- "$target" 2> /dev/null || true
    for ((i = 0; i < 100; i++)); do
      kill -0 -- "$target" 2> /dev/null || break
      sleep 0.1
    done
    kill -KILL -- "$target" 2> /dev/null || true
    log "$name stopped (PID $pid)."
  else
    log "$name is not running."
  fi
  rm -f -- "$file"
}

# OpenLDAP tools as the lab Administrator / the read-only account (simple bind, lab only).
ldap_admin() {
  local tool="$1"
  shift
  "$tool" -x -H "ldap://$LAB_SERVER" -D "$LAB_ADMIN_USER" -y <(printf '%s' "$LAB_ADMIN_PASSWORD") "$@"
}

ldap_readonly() {
  local tool="$1"
  shift
  "$tool" -x -H "ldap://$LAB_SERVER" -D "$LAB_READONLY_USER" -y <(printf '%s' "$LAB_READONLY_PASSWORD") "$@"
}

# The watchdog API with the headers every call needs (Development authentication: loopback only).
watchdog_api() {
  local method="$1" path="$2"
  shift 2
  curl -sS --fail-with-body -X "$method" -H 'X-AdMng-Watchdog-Api: 1' -H 'X-AdMng-Client: 1' "$@" "$WATCHDOG_API$path"
}
