#!/usr/bin/env bash
# Points the lab watchdog (run-watchdog-lab.sh) at the lab sinks through its API (Development authentication).
#
#   tools/testlab/watchdog/configure-lab.sh [--keep-rollout] [--digest-in <minutes>] [--test]
#
# One PUT /api/v1/config (If-Match with the current version) that sets:
#   * SMTP → Mailpit 127.0.0.1:1025, STARTTLS with the certificate pinned (SHA-256 read now with openssl – run the
#     script again after restarting Mailpit), Basic authentication with a random password (Mailpit accepts any);
#   * recipient groups it-admins = admin@contoso.test, helpdesk = helpdesk@contoso.test;
#   * channels "siem-lab" (webhook → the sink, Standard Webhooks signature with SINK_SECRET) and "teams-lab"
#     (Teams Workflows envelope → the sink's /teams path), both on every immediate rule and the digest;
#   * business hours all week 00:00–23:59 without holidays and rollout mode ended (unless --keep-rollout), so every
#     immediate rule notifies at once;
#   * aggregation windows of 1 minute (global and every rule that sets one) so the demo e-mails arrive quickly, and an
#     hourly cap of 1000 messages per rule so repeated scenario runs are not throttled into the digest;
#   * the group "Demo Tier0" of lab-scenario.sh as a privileged root (High, by sAMAccountName – found live in every
#     cycle, so a group re-created by the next scenario run counts as well) and a 30 s creation settle window (the
#     scenario's multi-step account creation still folds into one event, later changes are reported sooner);
#   * the digest time = now + 2 minutes (Europe/Warsaw; --digest-in changes the delay).
# --test sends a test e-mail and a test call to both channels afterwards and prints the stages.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

keep_rollout=false
digest_in=2
run_tests=false
while [ $# -gt 0 ]; do
  case "$1" in
    --keep-rollout) keep_rollout=true ;;
    --digest-in)
      shift
      [[ "${1:-}" =~ ^[0-9]+$ ]] || die "--digest-in needs a number of minutes."
      digest_in="$1"
      ;;
    --test) run_tests=true ;;
    -h | --help)
      sed -n '2,23p' "$0"
      exit 0
      ;;
    *) die "Unknown option: $1" ;;
  esac
  shift
done

require curl jq openssl python3

curl -fsS "$WATCHDOG_API/healthz" > /dev/null 2>&1 || die "The watchdog API $WATCHDOG_API does not answer – start run-watchdog-lab.sh first."
port_open "$MAILPIT_SMTP_PORT" || die "Mailpit does not listen on 127.0.0.1:$MAILPIT_SMTP_PORT – start start-mailpit.sh first."
port_open "$SINK_PORT" || log "WARNING: the webhook sink does not listen on $SINK_URL (run-watchdog-lab.sh starts it)."

# SHA-256 of Mailpit's self-signed STARTTLS certificate (Mailpit creates a new one on every start).
pin="$(openssl s_client -starttls smtp -connect "127.0.0.1:$MAILPIT_SMTP_PORT" -servername localhost < /dev/null 2> /dev/null |
  openssl x509 -outform DER 2> /dev/null | sha256sum | cut -d' ' -f1)"
# (the SHA-256 of empty input means openssl printed no certificate)
if ! [[ "$pin" =~ ^[0-9a-f]{64}$ ]] || [ "$pin" = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" ]; then
  die "Could not read Mailpit's STARTTLS certificate."
fi

smtp_password="$(python3 -c 'import secrets; print(secrets.token_urlsafe(18))')"
digest_time="$(TZ=Europe/Warsaw date -d "+$digest_in min" +%H:%M:00)"
rollout_ended=true
[ "$keep_rollout" = true ] && rollout_ended=false

envelope="$(watchdog_api GET /api/v1/config)"
version="$(jq -r '.version' <<< "$envelope")"

updated="$(jq \
  --arg pin "$pin" --arg password "$smtp_password" --arg digest "$digest_time" \
  --arg admins "$LAB_ADMIN_MAILBOX" --arg helpdesk "$LAB_HELPDESK_MAILBOX" \
  --arg webhook "$SINK_URL/webhook/siem?token=lab-token" \
  --arg teams "$SINK_URL/teams/workflows/lab/triggers/manual/paths/invoke?api-version=1&sig=lab-signature" \
  --arg secret "$SINK_SECRET" --arg tier0 "$DEMO_TIER0_SAM" --arg base "$WATCHDOG_API" \
  --argjson rolloutEnded "$rollout_ended" \
  '{version: .version, configuration: (.configuration
    | .general.instanceName = "AD Watchdog – laboratorium"
    | .general.publicBaseUrl = $base
    | .general.rolloutEnded = $rolloutEnded
    | .businessHours = (.businessHours + {days: ["Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"],
        start: "00:00:00", end: "23:59:00", holidayCalendar: "None"})
    | .smtp = (.smtp + {enabled: true, host: "127.0.0.1", port: 1025, security: "StartTls", authentication: "Basic",
        userName: "watchdog@contoso.test", password: {newValue: $password}, from: "AD Watchdog <watchdog@contoso.test>",
        localDomain: "watchdog.contoso.test", certificateValidation: "PinnedSha256", pinnedSha256: $pin, checkRevocation: false})
    | .recipientGroups |= map(if .id == "it-admins" then .addresses = [$admins]
                              elif .id == "helpdesk" then .addresses = [$helpdesk] else . end)
    | .channels = ([.channels[] | select(.id != "siem-lab" and .id != "teams-lab")] + [
        {id: "siem-lab", type: "Webhook", name: "SIEM – laboratorium", detailLevel: "Minimal",
         url: {newValue: $webhook}, webhook: {signingSecret: {newValue: $secret}}},
        {id: "teams-lab", type: "Teams", name: "Teams – laboratorium", detailLevel: "Standard",
         url: {newValue: $teams}, teams: {authMode: "Sas"}}])
    | .notifications.aggregationWindow = "00:01:00"
    | .notifications.maxMessagesPerRulePerHour = 1000
    | .rules |= map(if .delivery.aggregationWindow != null and .delivery.aggregationWindow != "00:00:00"
                    and ([.id] | inside(["tier0-badsuccessor", "tier0-machine-quota", "watchdog-self", "tier0-gpo"]) | not)
                    then .delivery.aggregationWindow = "00:01:00" else . end)
    | .rules |= map(if .delivery.immediate and .id != "health"
                    then .delivery.channelIds = ((.delivery.channelIds + ["siem-lab", "teams-lab"]) | unique) else . end)
    | .directory.creationSettleWindow = "00:00:30"
    | .digest.time = $digest
    | .digest.channelIds = ["siem-lab", "teams-lab"]
    | .privilegedGroups.highSamAccountNames = (((.privilegedGroups.highSamAccountNames // []) + [$tier0]) | unique)
  )}' <<< "$envelope")"

if ! response="$(watchdog_api PUT /api/v1/config -H 'Content-Type: application/json' -H "If-Match: \"$version\"" --data-binary @- <<< "$updated")"; then
  printf '%s\n' "$response" | jq -r '"\(.title // "error") (\(.status // ""))", (.issues[]? | "  \(.severity) \(.code) \(.path): \(.message)")' >&2 2> /dev/null ||
    printf '%s\n' "$response" >&2
  die "The configuration was rejected."
fi

log "Configuration version $(jq -r '.version' <<< "$response") saved: SMTP → Mailpit (pinned certificate), channels siem-lab + teams-lab → $SINK_URL, digest at $digest_time (Europe/Warsaw)."
[ "$keep_rollout" = true ] || log "Rollout mode ended: immediate rules notify at once."

if [ "$run_tests" = true ]; then
  log "Test e-mail to $LAB_ADMIN_MAILBOX:"
  watchdog_api POST /api/v1/test/email -H 'Content-Type: application/json' --data-binary "{\"recipients\":[\"$LAB_ADMIN_MAILBOX\"]}" |
    jq -r '"  success: \(.success)", (.stages[] | "  \(.stage): \(.success) \(.detail // "")")'
  for channel in siem-lab teams-lab; do
    log "Test call of channel $channel:"
    watchdog_api POST "/api/v1/test/channels/$channel" -H 'Content-Type: application/json' --data-binary '{}' |
      jq -r '"  success: \(.success)", (.stages[] | "  \(.stage): \(.success) \(.detail // "")")'
  done
fi
