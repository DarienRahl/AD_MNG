#!/usr/bin/env bash
# B4 acceptance under systemd (docs/dev/watchdog-linux-design.md 12.2, 12.4, 21 B4). Installs admng-openwec and
# admng-watchdog into a privileged container with systemd as PID 1 (host network, so the lab DC and the host's curl reach
# it) and checks with the real units:
#   openwecd.service – starts under its sandbox with the keytab that the package's admng-watchdog-keytab init wrote for a
#     fresh computer account (own OU, deleted at the end), stays active (no restart loop), writes no access-log file,
#     answers a request without a ticket with 401 and accepts SPNEGO from the host (curl --negotiate with the lab
#     administrator's ticket for HTTP/<fqdn>, mutual-authentication token in the answer); exposure <= 2.0;
#   admng-wec-rotate.service – renames the non-empty active files and reloads openwecd (ExecStartPost), deletes a rotated
#     file the progress file lists complete, keeps an unconsumed one, deletes one older than MAX_UNCONSUMED_DAYS with a
#     warning, and runs while admng-watchdog is stopped (/run/admng-watchdog missing: nothing counts as consumed).
#
#   eval "$(tools/testlab/watchdog/keytab-lab.sh env --admin)"     # the lab administrator's ticket (LDAP, curl)
#   tools/testlab/watchdog/wec-units-lab.sh IMAGE ADMNG_OPENWEC_DEB ADMNG_WATCHDOG_DEB
#
# IMAGE: a Debian/Ubuntu image with systemd >= 252 and aspnetcore-runtime-10.0 installed (e.g. the B1 lab image). The
# container is removed at the end. Needs ldapadd/ldapdelete (SASL GSSAPI), curl with GSS-API and base64 on the host.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

[ $# -eq 3 ] || { sed -n '2,20p' "$0"; exit 2; }
image=$1
openwec_deb=$(readlink -f "$2")
watchdog_deb=$(readlink -f "$3")
[ "${KRB5CCNAME:-}" != "${KRB5CCNAME#FILE:}" ] || die "Run eval \"\$(tools/testlab/watchdog/keytab-lab.sh env --admin)\" first."
require docker ldapadd ldapdelete ldapsearch curl iconv base64

dc=${ADMNG_TEST_GSSAPI_SERVER:?keytab-lab.sh env}
realm=$(tr '[:lower:]' '[:upper:]' <<< "$LAB_DOMAIN_DNS")
base=$(ldapsearch -Y GSSAPI -Q -LLL -H "ldap://$dc" -b '' -s base defaultNamingContext | sed -n 's/^defaultNamingContext: //p')
account=WDB4$(od -An -N2 -tx1 /dev/urandom | tr -d ' \n' | tr '[:lower:]' '[:upper:]')
fqdn=$(tr '[:upper:]' '[:lower:]' <<< "$account").$LAB_DOMAIN_DNS
ou="OU=AdMng B4 units $$,$base"
otp="Otp-$(head -c 24 /dev/urandom | base64 | tr -d '/+=')-9a!"
name=admng-b4-units-$$
fail() { echo "FAILED: $*" >&2; docker logs "$name" 2>&1 | tail -n 20 >&2 || true; exit 1; }
run_in() { docker exec "$name" bash -c "$1"; }
cleanup() {
    docker rm -f "$name" > /dev/null 2>&1 || true
    ldapdelete -Y GSSAPI -Q -H "ldap://$dc" -r "$ou" > /dev/null 2>&1 || true
}
trap cleanup EXIT

log "Computer account $account ($fqdn) in $ou with a one-time password (design 4.2)."
password=$(printf '"%s"' "$otp" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)
ldapadd -Y GSSAPI -Q -H "ldap://$dc" > /dev/null << LDIF
dn: $ou
objectClass: organizationalUnit

dn: CN=$account,$ou
objectClass: computer
sAMAccountName: $account\$
userAccountControl: 1052672
dNSHostName: $fqdn
servicePrincipalName: HTTP/$fqdn
servicePrincipalName: HOST/$fqdn
servicePrincipalName: HOST/$account
msDS-SupportedEncryptionTypes: 24
unicodePwd:: $password
LDIF

log "Container $name from $image (systemd as PID 1, host network)."
docker run -d --name "$name" --privileged --cgroupns=host --network host -v /sys/fs/cgroup:/sys/fs/cgroup:rw \
    --tmpfs /run --tmpfs /run/lock -v "$openwec_deb:/tmp/admng-openwec.deb:ro" -v "$watchdog_deb:/tmp/admng-watchdog.deb:ro" \
    -v "$KRB5_CONFIG:/tmp/lab-krb5.conf:ro" --entrypoint /lib/systemd/systemd "$image" > /dev/null
for _ in $(seq 1 60); do
    state=$(docker exec "$name" systemctl is-system-running 2>/dev/null || true)
    case "$state" in running|degraded) break ;; esac
    sleep 1
done
run_in 'systemctl --version | head -n 1'

log "Packages: admng-openwec and admng-watchdog (offline: dependencies are in the image)."
run_in 'systemctl stop admng-watchdog.service admng-watchdog-keytab.timer > /dev/null 2>&1 || true
    dpkg -i /tmp/admng-openwec.deb /tmp/admng-watchdog.deb > /tmp/dpkg.log 2>&1 || { tail -n 30 /tmp/dpkg.log; exit 1; }
    systemctl stop admng-watchdog.service > /dev/null 2>&1 || true
    dpkg -s admng-openwec admng-watchdog | grep -E "^(Package|Version|Status):"'

log "admng-watchdog-keytab init in the container (the package's tool, as root)."
run_in "install -m 0644 /tmp/lab-krb5.conf /etc/admng-watchdog/krb5.conf"
printf '%s\n' "$otp" | docker exec -i "$name" admng-watchdog-keytab init --password-stdin --principal "$account\$@$realm" --fqdn "$fqdn" \
    --replication-timeout 0 || fail "admng-watchdog-keytab init"
run_in 'stat -c "%U:%G %a %n" /etc/admng-watchdog/admng-wd.keytab'

log "openwecd.service under its sandbox with that keytab (HTTP/$fqdn@$realm)."
run_in "
    sed -i -e 's#admng-wd.contoso.com@CONTOSO.COM#$fqdn@$realm#' -e 's#admng-wd.contoso.com#$fqdn#' /etc/openwec.conf.toml
    install -d /etc/systemd/system/openwecd.service.d
    printf '[Service]\nIPAddressAllow=127.0.0.1 ::1\n' > /etc/systemd/system/openwecd.service.d/admng-dcs.conf
    systemctl daemon-reload
    systemctl restart openwecd.service"
sleep 3
[ "$(run_in 'systemctl is-active openwecd.service')" = active ] || fail "openwecd.service is not active"
run_in 'systemctl show -p NRestarts -p ExecMainPID -p User openwecd.service'
anonymous=$(curl -s -o /dev/null -w '%{http_code}' -X POST "http://127.0.0.1:5985/wsman/SubscriptionManager/WEC")
[ "$anonymous" = 401 ] || fail "a request without a ticket got $anonymous"
headers=$(curl -s -D - -o /dev/null --negotiate -u : --resolve "$fqdn:5985:127.0.0.1" \
    -X POST -H 'Content-Type: application/soap+xml;charset=UTF-8' "http://$fqdn:5985/wsman/SubscriptionManager/WEC")
echo "$headers" | head -n 1
echo "$headers" | grep -q '^HTTP/1.1 401' && fail "SPNEGO with a ticket for HTTP/$fqdn was refused"
echo "$headers" | grep -qi '^WWW-Authenticate: Negotiate [A-Za-z0-9+/=]' || fail "no mutual-authentication token in the answer"
sleep 6
[ "$(run_in 'systemctl show -p NRestarts --value openwecd.service')" = 0 ] || fail "openwecd restarted"
files=$(run_in 'find /var/lib/openwec -type f ! -path "/var/lib/openwec/db/*" | sort')
[ -z "$files" ] || fail "files besides the database below /var/lib/openwec: $files"
run_in 'ls -la / | grep -i none || true; test ! -e /None' || fail "an access-log file /None exists"
run_in 'systemd-analyze security --no-pager openwecd.service | tail -n 1'
exposure=$(run_in 'systemd-analyze security --no-pager openwecd.service | tail -n 1' | sed -n 's/.*exposure level for openwecd.service: \([0-9.]*\).*/\1/p')
awk -v e="$exposure" 'BEGIN { exit !(e != "" && e <= 2.0) }' || fail "openwecd.service exposure $exposure > 2.0"

log "admng-wec-rotate.service (the real unit): rotation, reload, deletion after consumption, MAX_UNCONSUMED_DAYS."
run_in 'set -e
    out=/var/lib/openwec/out
    d=$out/DC01@CONTOSO.TEST
    install -d -m 2750 -o openwec -g admng-watchdog "$d"
    old=$(date -u -d "-2 hours" +%Y%m%dT%H%M%SZ); older=$(date -u -d "-3 hours" +%Y%m%dT%H%M%SZ); ancient=$(date -u -d "-9 days" +%Y%m%dT%H%M%SZ)
    printf "a\nb\n" > "$d/security.jsonl.$old";    touch -d "-2 hours" "$d/security.jsonl.$old"
    printf "c\nd\n" > "$d/security.jsonl.$older";  touch -d "-3 hours" "$d/security.jsonl.$older"
    printf "e\n" > "$d/security.jsonl.$ancient";   touch -d "-9 days" "$d/security.jsonl.$ancient"
    printf "f\n" > "$d/security.jsonl"
    chown openwec:admng-watchdog "$d"/security.jsonl*; chmod 0640 "$d"/security.jsonl*
    # The progress file of the service: the older rotated file consumed completely, the other one not.
    install -d -m 0700 -o admng-watchdog -g admng-watchdog /run/admng-watchdog
    entry() { read -r dev ino size < <(stat -c "%d %i %s" "$1"); sha=$(head -n 1 "$1" | sha256sum | cut -d " " -f 1)
              printf "{\"client\":\"DC01@CONTOSO.TEST\",\"name\":\"%s\",\"device\":%s,\"inode\":%s,\"firstLineSha256\":\"%s\",\"offset\":%s,\"size\":%s,\"complete\":%s}" \
                "${1##*/}" "$dev" "$ino" "$sha" "$2" "$size" "$3"; }
    { printf "{\"version\":1,\"writtenAt\":\"x\",\"files\":[\n"; entry "$d/security.jsonl.$older" 4 true; printf ",\n"; entry "$d/security.jsonl.$old" 2 false; printf "\n]}\n"; } > /run/admng-watchdog/wec-progress.json
    chown admng-watchdog:admng-watchdog /run/admng-watchdog/wec-progress.json; chmod 0600 /run/admng-watchdog/wec-progress.json
    pid=$(systemctl show -p MainPID --value openwecd.service)
    systemctl start admng-wec-rotate.service
    systemctl show -p Result --value admng-wec-rotate.service | grep -qx success
    ls "$d"
    test ! -e "$d/security.jsonl.$older"            # consumed: deleted
    test -e "$d/security.jsonl.$old"                # unconsumed: kept
    test ! -e "$d/security.jsonl.$ancient"          # MAX_UNCONSUMED_DAYS=7
    test ! -e "$d/security.jsonl"                   # rotated
    ls "$d" | grep -q "^security.jsonl.2"
    test "$(systemctl show -p MainPID --value openwecd.service)" = "$pid"   # reloaded, not restarted
    journalctl -u openwecd.service --no-pager | grep -q "Reload" || journalctl -u openwecd.service --no-pager | grep -qi "reload"
    journalctl -u admng-wec-rotate.service --no-pager -p warning | grep -q "older than MAX_UNCONSUMED_DAYS"
    echo "rotation ok"' || fail "admng-wec-rotate.service"

log "admng-wec-rotate.service while admng-watchdog is stopped (no /run/admng-watchdog)."
run_in 'set -e
    rm -rf /run/admng-watchdog
    d=/var/lib/openwec/out/DC01@CONTOSO.TEST
    printf "g\n" > "$d/security.jsonl"; chown openwec:admng-watchdog "$d/security.jsonl"
    before=$(ls "$d" | grep -c "^security.jsonl.2")
    sleep 1
    systemctl start admng-wec-rotate.service
    systemctl show -p Result --value admng-wec-rotate.service | grep -qx success
    test "$(ls "$d" | grep -c "^security.jsonl.2")" -gt "$before"   # rotated, nothing consumed deleted
    journalctl -u admng-wec-rotate.service --no-pager | grep -q "nothing counts as consumed"
    echo "stopped-service run ok"' || fail "admng-wec-rotate.service without the progress file"
run_in 'systemd-analyze security --no-pager admng-wec-rotate.service | tail -n 1'
log "OK: openwecd.service and admng-wec-rotate.service under systemd."
