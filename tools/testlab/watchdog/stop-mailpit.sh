#!/usr/bin/env bash
# Stops the Mailpit instance started by start-mailpit.sh (its messages are discarded).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

stop_pid_file "$MAILPIT_HOME/mailpit.pid" Mailpit
