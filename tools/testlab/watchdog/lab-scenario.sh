#!/usr/bin/env bash
# Plays the watchdog demo scenario (spec 22.4) against the Samba lab as Administrator – only inside the throwaway
# OU=WatchdogDemo, which is created first (leftovers of an aborted run are removed) and tree-deleted by --cleanup.
#
#   tools/testlab/watchdog/lab-scenario.sh [--verify] [--keep]
#   tools/testlab/watchdog/lab-scenario.sh --cleanup
#
#   --verify    afterwards wait for the expected events in the watchdog API (run-watchdog-lab.sh + configure-lab.sh),
#               check the Polish e-mails in Mailpit and the signed webhooks of the sink; exit 1 when something is missing
#   --keep      leave OU=WatchdogDemo in place after --verify (default: it stays anyway without --verify)
#   --cleanup   tree-delete OU=WatchdogDemo and exit
#
# Steps: OUs and groups (Demo Tier0 is a privileged root in configure-lab.sh, Demo Operatorzy is nested in it);
# Zofia Żak created in three steps (disabled → password over StartTLS → enabled = one UserCreated); Łukasz Wiśniewski,
# Paweł Lewandowski, Grażyna Dąbrowska and a computer with a child object; after the creation settle window:
# department/title change, account expiry in 5 days, group membership, Zofia into the nested privileged group
# (PrivilegeGranted with Via), five bad binds of Paweł (lockout) and the admin unlock, pwdLastSet 0 → -1 (password age
# reset), disable + delete of Grażyna (read-only watchdog: ObjectDisappeared at the nightly reconciliation), user move,
# OU rename (children not reported), deletion of the computer with its child (one event).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

verify=false
keep=false
cleanup_only=false
for arg in "$@"; do
  case "$arg" in
    --verify) verify=true ;;
    --keep) keep=true ;;
    --cleanup) cleanup_only=true ;;
    -h | --help)
      sed -n '2,20p' "$0"
      exit 0
      ;;
    *) die "Unknown option: $arg" ;;
  esac
done

require ldapadd ldapmodify ldapdelete ldapsearch ldapwhoami base64 iconv

PEOPLE="OU=Pracownicy,$DEMO_OU"
TEAM="OU=Zespół Demo,$DEMO_OU"
MOVED="OU=Przeniesieni,$DEMO_OU"
COMPUTERS="OU=Komputery,$DEMO_OU"
GROUPS_OU="OU=Grupy,$DEMO_OU"
OPERATORS="CN=Demo Operatorzy,$GROUPS_OU"
PROJECT="CN=Demo Projekt,$GROUPS_OU"
ZOFIA="CN=Zofia Żak,$PEOPLE"
LUKASZ="CN=Łukasz Wiśniewski,$PEOPLE"
PAWEL="CN=Paweł Lewandowski,$PEOPLE"
GRAZYNA="CN=Grażyna Dąbrowska,$PEOPLE"
COMPUTER="CN=WD-KRK-PC01,$COMPUTERS"
PASSWORD='Zażółć-Gęślą-Jaźń-2026!'

# "attr: value", or "attr:: base64" for values that are not plain printable ASCII (LDIF, RFC 2849).
ldif() {
  local attr="$1" value="$2"
  if LC_ALL=C grep -q '[^ -~]' <<< "$value" || [[ "$value" == [\ :\<]* ]]; then
    printf '%s:: %s\n' "$attr" "$(printf '%s' "$value" | base64 -w0)"
  else
    printf '%s: %s\n' "$attr" "$value"
  fi
}

exists() { ldap_admin ldapsearch -LLL -b "$1" -s base '(objectClass=*)' 1.1 > /dev/null 2>&1; }

# add <dn> <attr=value>…
add() {
  local dn="$1" pair
  shift
  {
    ldif dn "$dn"
    for pair in "$@"; do
      ldif "${pair%%=*}" "${pair#*=}"
    done
  } | ldap_admin ldapadd > /dev/null
}

# replace <dn> <attr> <value>   /   add_value <dn> <attr> <value>
modify() {
  local operation="$1" dn="$2" attr="$3" value="$4"
  {
    ldif dn "$dn"
    echo "changetype: modify"
    echo "$operation: $attr"
    ldif "$attr" "$value"
  } | ldap_admin ldapmodify > /dev/null
}
replace() { modify replace "$@"; }
add_value() { modify add "$@"; }

# Passwords only over an encrypted channel (StartTLS with the lab's self-signed certificate).
set_password() {
  local dn="$1" encoded
  encoded="$(printf '"%s"' "$PASSWORD" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)"
  {
    ldif dn "$dn"
    printf 'changetype: modify\nreplace: unicodePwd\nunicodePwd:: %s\n' "$encoded"
  } | LDAPTLS_REQCERT=never ldap_admin ldapmodify -ZZ > /dev/null
}

user() {
  local dn="$1" sam="$2" given="$3" surname="$4"
  shift 4
  add "$dn" objectClass=user "sAMAccountName=$sam" "userPrincipalName=$sam@$LAB_DOMAIN_DNS" "givenName=$given" "sn=$surname" \
    "displayName=$given $surname" userAccountControl=514 "$@"
}

rename() {
  local dn="$1" rdn="$2" superior="${3:-}"
  {
    ldif dn "$dn"
    echo "changetype: modrdn"
    ldif newrdn "$rdn"
    echo "deleteoldrdn: 1"
    if [ -n "$superior" ]; then
      ldif newsuperior "$superior"
    fi
  } | ldap_admin ldapmodify > /dev/null
}

api_up() { curl -fsS "$WATCHDOG_API/healthz" > /dev/null 2>&1; }

# Events of this run (id above START_ID) of one kind under the demo OU (object DN or member DN), newest first.
START_ID=0
events_of() {
  watchdog_api GET "/api/v1/events?kind=$1&limit=500" |
    jq -c --arg ou "$DEMO_OU" --argjson start "$START_ID" '[.items[] | select(.id > $start)
      | select((.object.dn // "" | ascii_downcase | endswith($ou | ascii_downcase))
        or (.member.dn // "" | ascii_downcase | endswith($ou | ascii_downcase)))]'
}

# Waits until the watchdog reported <kind> for an object whose DN contains <text> (or <seconds> passed).
wait_for_event() {
  local kind="$1" text="$2" seconds="${3:-90}" i
  if ! api_up; then
    sleep 20
    return 0
  fi
  for ((i = 0; i < seconds; i += 2)); do
    if events_of "$kind" | jq -e --arg t "$text" 'any(.[]; ((.object.dn // "") + " " + (.member.dn // "")) | contains($t))' > /dev/null; then
      log "  → watchdog: $kind ($text)"
      return 0
    fi
    sleep 2
  done
  log "  → watchdog did not report $kind ($text) within $seconds s."
  return 1
}

cleanup() {
  if exists "$DEMO_OU"; then
    ldap_admin ldapdelete -r "$DEMO_OU"
    log "Deleted $DEMO_OU."
  else
    log "$DEMO_OU does not exist."
  fi
}

if [ "$cleanup_only" = true ]; then
  cleanup
  exit 0
fi

if exists "$DEMO_OU"; then
  log "Removing leftovers of an earlier run."
  cleanup
fi

if api_up; then
  START_ID="$(watchdog_api GET '/api/v1/events?limit=1' | jq '.items[0].id // 0')"
fi
if [ "$verify" = true ]; then
  api_up || die "--verify needs the watchdog API ($WATCHDOG_API) – start run-watchdog-lab.sh and configure-lab.sh."
  # Only this run's e-mails and deliveries count.
  curl -fsS -X DELETE "http://127.0.0.1:$MAILPIT_UI_PORT/api/v1/messages" > /dev/null
  curl -fsS -X DELETE "$SINK_URL/_sink/requests" > /dev/null
fi

log "1/6 OUs, groups, users and a computer in $DEMO_OU"
add "$DEMO_OU" objectClass=organizationalUnit "description=AdMng Watchdog – scenariusz demonstracyjny (do usunięcia)"
for ou in "$PEOPLE" "$MOVED" "$COMPUTERS" "$GROUPS_OU"; do
  add "$ou" objectClass=organizationalUnit
done
add "$DEMO_TIER0_GROUP" objectClass=group "sAMAccountName=$DEMO_TIER0_SAM" groupType=-2147483646 "description=Grupa uprzywilejowana laboratorium"
add "$OPERATORS" objectClass=group sAMAccountName=wd-demo-operatorzy groupType=-2147483646 "description=Operatorzy (zagnieżdżeni w Demo Tier0)"
add_value "$DEMO_TIER0_GROUP" member "$OPERATORS"
add "$PROJECT" objectClass=group sAMAccountName=wd-demo-projekt groupType=-2147483646 "description=Zespół projektowy"

# Zofia Żak in three steps inside the creation settle window: one UserCreated with the final state.
user "$ZOFIA" wd.zofia.zak Zofia Żak "department=IT" "title=Administratorka"
set_password "$ZOFIA"
replace "$ZOFIA" userAccountControl 512
for spec in "$LUKASZ|wd.lukasz.wisniewski|Łukasz|Wiśniewski|Sprzedaż|Handlowiec" \
  "$PAWEL|wd.pawel.lewandowski|Paweł|Lewandowski|Logistyka|Magazynier" \
  "$GRAZYNA|wd.grazyna.dabrowska|Grażyna|Dąbrowska|Kadry|Specjalistka ds. kadr"; do
  IFS='|' read -r dn sam given surname department title <<< "$spec"
  user "$dn" "$sam" "$given" "$surname" "department=$department" "title=$title"
  set_password "$dn"
  replace "$dn" userAccountControl 512
done
add "$COMPUTER" objectClass=computer sAMAccountName='WD-KRK-PC01$' userAccountControl=4096 dNSHostName=wd-krk-pc01.contoso.test
add "CN=Demo SCP,$COMPUTER" objectClass=serviceConnectionPoint keywords=AdMngWatchdogDemo

log "2/6 Waiting for the creation events (settle window)…"
wait_for_event UserCreated "Zofia Żak" 120 || true
wait_for_event ComputerCreated "WD-KRK-PC01" 60 || true

log "3/6 Attribute, expiry and membership changes"
replace "$LUKASZ" department "Księgowość"
replace "$LUKASZ" title "Księgowy"
expires=$(((($(date -u -d '+5 days' +%s)) + 11644473600) * 10000000))
replace "$LUKASZ" accountExpires "$expires"
add_value "$PROJECT" member "$LUKASZ"
add_value "$OPERATORS" member "$ZOFIA"
replace "$GRAZYNA" pwdLastSet 0
replace "$GRAZYNA" pwdLastSet -1
replace "$GRAZYNA" userAccountControl 514

log "4/6 Five bad binds of Paweł Lewandowski (lockout threshold 5), then the admin unlock"
for _ in 1 2 3 4 5; do
  ldapwhoami -x -H "ldap://$LAB_SERVER" -D "wd.pawel.lewandowski@$LAB_DOMAIN_DNS" -y <(printf '%s' 'Błędne-Hasło-1') > /dev/null 2>&1 || true
done
wait_for_event AccountLockedOut "Paweł Lewandowski" 90 || true
replace "$PAWEL" lockoutTime 0

log "5/6 Move, OU rename and deletions"
rename "$LUKASZ" "CN=Łukasz Wiśniewski" "$MOVED"
rename "$PEOPLE" "OU=Zespół Demo"
ldap_admin ldapdelete "CN=Grażyna Dąbrowska,$TEAM"
ldap_admin ldapdelete -r "$COMPUTER"

if [ "$verify" = false ]; then
  log "6/6 Done. Events: $WATCHDOG_API/api/v1/events · e-mails: http://127.0.0.1:$MAILPIT_UI_PORT · cleanup: $0 --cleanup"
  exit 0
fi

log "6/6 Verifying events, e-mails and webhooks"
failed=0
expect() { wait_for_event "$@" || failed=1; }
expect AttributesChanged "Łukasz Wiśniewski" 90
expect AccountExpirationChanged "Łukasz Wiśniewski" 60
expect GroupMemberAdded "Łukasz Wiśniewski" 60
expect GroupMemberAdded "Zofia Żak" 60
expect PrivilegeGranted "Zofia Żak" 60
expect AccountUnlocked "Paweł Lewandowski" 60
expect PasswordAgeReset "Grażyna Dąbrowska" 60
expect AccountDisabled "Grażyna Dąbrowska" 60
expect ObjectMoved "Łukasz Wiśniewski" 60
expect ObjectRenamed "OU=Zespół Demo" 60

created="$(events_of UserCreated | jq '[.[] | select(.object.dn | contains("Zofia Żak"))] | length')"
[ "$created" = 1 ] || {
  log "  ✗ expected exactly one UserCreated for Zofia Żak, got $created"
  failed=1
}
renamed_children="$(events_of ObjectMoved | jq '[.[] | select(.object.dn | contains("Zespół Demo"))] | length')"
[ "$renamed_children" = 0 ] || {
  log "  ✗ the OU rename was reported for $renamed_children children"
  failed=1
}

# E-mails (Polish subjects) in Mailpit – aggregated for one minute in the lab configuration, so a message may cover
# several events ("Dostęp uprzywilejowany: 3"): match the subject part and look for the object in the text body.
mail_found() {
  local subject="$1" to="$2" text="$3" id
  for id in $(curl -fsS "http://127.0.0.1:$MAILPIT_UI_PORT/api/v1/messages?limit=500" |
    jq -r --arg s "$subject" --arg to "$to" '.messages[] | select((.Subject | contains($s)) and any(.To[]; .Address == $to)) | .ID'); do
    if curl -fsS "http://127.0.0.1:$MAILPIT_UI_PORT/api/v1/message/$id" | jq -e --arg t "$text" '.Text | contains($t)' > /dev/null; then
      return 0
    fi
  done
  return 1
}
expect_mail() {
  local subject="$1" to="$2" text="$3" i
  for ((i = 0; i < 90; i++)); do
    if mail_found "$subject" "$to" "$text"; then
      log "  → Mailpit: \"…$subject…\" to $to about $text"
      return 0
    fi
    sleep 2
  done
  log "  ✗ no e-mail \"…$subject…\" to $to about $text in Mailpit"
  failed=1
}
expect_mail "uprzywilej" "$LAB_ADMIN_MAILBOX" "Zofia Żak"
expect_mail "Nowe kont" "$LAB_ADMIN_MAILBOX" "Zofia Żak"
expect_mail "Zablokowano konto" "$LAB_HELPDESK_MAILBOX" "Paweł Lewandowski"
hooks="$(curl -fsS "$SINK_URL/_sink/requests" | jq '[.[] | select(.channel == "webhook" and .signatureValid and (.type // "" | startswith("ad.")))] | length')"
invalid="$(curl -fsS "$SINK_URL/_sink/requests" | jq '[.[] | select(.channel == "webhook" and (.signatureValid | not))] | length')"
if [ "$hooks" -gt 0 ] && [ "$invalid" = 0 ]; then
  log "  → sink: $hooks signed ad.* webhook(s), none invalid"
else
  log "  ✗ sink: $hooks signed ad.* webhooks, $invalid invalid"
  failed=1
fi

if [ "$keep" = false ]; then
  cleanup
fi
[ "$failed" = 0 ] || die "The scenario was not reported completely."
log "Scenario verified."
