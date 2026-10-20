#!/usr/bin/env bash
# Install test of the admng-watchdog .deb in a plain Debian 12 or 13 container, as root (CI job "linux-package",
# docs/dev/watchdog-linux-design.md 20 item 4, 21 B1). No systemd runs as PID 1: units are verified offline.
#
#   tools/packaging/test-deb-install.sh PATH/admng-watchdog_*.deb [PACKAGING_DIR] [PATH/admng-openwec_*.deb]
#
# admng-watchdog depends on admng-openwec (B4): the third argument (or ADMNG_OPENWEC_DEB) is the admng-openwec package of
# the container's release, installed together with it.
#
# The container needs CAP_NET_ADMIN for the nftables syntax check (docker run --cap-add=NET_ADMIN); without it the
# check is skipped with a notice.
set -euo pipefail

deb=$(readlink -f "$1")
packaging=$(readlink -f "${2:-$(dirname "${BASH_SOURCE[0]}")/../../packaging/admng-watchdog}")
openwec_deb=$(readlink -f "${3:-${ADMNG_OPENWEC_DEB:?the admng-openwec .deb (third argument or ADMNG_OPENWEC_DEB)}}")
export DEBIAN_FRONTEND=noninteractive
. /etc/os-release
fail() { echo "FAILED: $*" >&2; exit 1; }
step() { echo; echo "=== $* ==="; }
expect_stat() {
    # $1 path, $2 "owner:group mode"
    actual=$(stat -c '%U:%G %a' "$1")
    [ "$actual" = "$2" ] || fail "$1 is $actual, expected $2"
    echo "ok $1 $actual"
}

step "$PRETTY_NAME: Microsoft repository and the package"
apt-get update -qq
apt-get install -y -qq --no-install-recommends ca-certificates curl >/dev/null
if [ "$ID" = debian ]; then
    # Debian has no .NET: the runtime comes from packages.microsoft.com (13.1 prerequisite).
    curl -fsSLo /tmp/packages-microsoft-prod.deb "https://packages.microsoft.com/config/debian/$VERSION_ID/packages-microsoft-prod.deb"
    dpkg -i /tmp/packages-microsoft-prod.deb >/dev/null
    apt-get update -qq
fi
apt-get install -y --no-install-recommends "$openwec_deb" "$deb"
dpkg -s aspnetcore-runtime-10.0 | grep '^Version'

step "Recommended TPM2 libraries (names of 13.1) and BPF framework (12.1)"
for name in libtss2-esys-3.0.2-0 libtss2-esys-3.0.2-0t64 libtss2-mu0 libtss2-mu-4.0.1-0t64 libtss2-rc0 libtss2-rc0t64 \
            libtss2-tcti-device0 libtss2-tcti-device0t64; do
    if apt-cache show "$name" >/dev/null 2>&1; then echo "available: $name"; fi
done
systemctl --version | head -n 1
systemctl --version | grep -o '[+-]BPF_FRAMEWORK' || echo "BPF_FRAMEWORK not listed"

step "Users, groups, ownership and modes (section 11)"
getent passwd admng-watchdog | grep -q ':/var/lib/admng-watchdog:/usr/sbin/nologin$' || fail "admng-watchdog user"
id -nG admng-watchdog | tr ' ' '\n' | grep -qx admng-keytab || fail "admng-watchdog not in admng-keytab"
id -nG openwec | tr ' ' '\n' | grep -qx admng-keytab || fail "openwec not in admng-keytab"
expect_stat /etc/admng-watchdog "root:root 755"
expect_stat /etc/admng-watchdog/appsettings.json "root:admng-watchdog 640"
expect_stat /etc/admng-watchdog/krb5.conf "root:root 644"
expect_stat /etc/admng-watchdog/ldap.conf "root:root 644"
expect_stat /etc/admng-watchdog/smb-client.conf "root:root 644"
expect_stat /etc/admng-watchdog/tls "root:admng-watchdog 750"
expect_stat /etc/admng-watchdog/tls/admng-wd.crt "root:admng-watchdog 644"
expect_stat /etc/admng-watchdog/tls/admng-wd.key "root:admng-watchdog 640"
expect_stat /etc/admng-watchdog/credentials "root:root 700"
expect_stat /etc/admng-watchdog/credentials/dp-key.cred "root:root 600"
expect_stat /etc/admng-watchdog/dp-key.variant "root:root 644"
expect_stat /var/lib/admng-watchdog "admng-watchdog:admng-watchdog 700"
expect_stat /var/lib/admng-watchdog/keys "admng-watchdog:admng-watchdog 700"
expect_stat /var/lib/admng-watchdog/backup "admng-watchdog:admng-watchdog 700"
expect_stat /var/lib/admng-watchdog-keytab "root:admng-watchdog 750"
expect_stat /var/lib/openwec/out "openwec:admng-watchdog 2750"
[ "$(cat /etc/admng-watchdog/dp-key.variant)" = host ] || fail "credential variant (no TPM in a container)"
[ "$(systemd-creds decrypt --name=dp-key /etc/admng-watchdog/credentials/dp-key.cred - | wc -c)" = 32 ] || fail "dp-key"
openssl x509 -noout -checkend 0 -ext extendedKeyUsage -in /etc/admng-watchdog/tls/admng-wd.crt
admng-watchdog cert-fingerprint | grep -Eq '^([0-9A-F]{2}:){31}[0-9A-F]{2}$' || fail "cert-fingerprint"

step "admng-openwec (7.1, 13.2, B4): binaries, user, folders, database, configuration, unit enabled"
expect_stat /usr/bin/openwecd "root:root 755"
expect_stat /etc/openwec.conf.toml "root:openwec 640"
expect_stat /etc/openwec/subscriptions "root:root 755"
[ -z "$(ls -A /etc/openwec/subscriptions)" ] || fail "files shipped below /etc/openwec/subscriptions"
expect_stat /var/lib/openwec "openwec:openwec 751"
expect_stat /var/lib/openwec/db "openwec:openwec 700"
# SQLite sets 0644 on files it creates whatever the umask; the database folder (0700) is the protection.
[ "$(stat -c %U:%G /var/lib/openwec/db/openwec.sqlite)" = openwec:openwec ] || fail "openwec.sqlite owner"
getent passwd openwec | grep -q ':/var/lib/openwec:/usr/sbin/nologin$' || fail "openwec user"
[ -L /etc/systemd/system/multi-user.target.wants/openwecd.service ] || fail "openwecd.service not enabled"
grep -q 'access_logs' /etc/openwec.conf.toml && ! grep -Eq '^[[:space:]]*access_logs' /etc/openwec.conf.toml || fail "access_logs must stay unset"
dpkg -s admng-openwec | grep -E '^(Version|Depends):'

step "print-wec-subscription with the shipped placeholders (7.3): the configured uuid, loadable by openwec"
sub=$(mktemp -d)
admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' --dc 'DC02$@CONTOSO.COM' > "$sub/admng-dc-security.toml"
grep -qx 'uuid = "4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"' "$sub/admng-dc-security.toml" || fail "subscription uuid"
diff <(grep -v '^#' /usr/share/admng-openwec/admng-dc-security.toml.example | sed '/./,$!d') "$sub/admng-dc-security.toml" \
    || fail "the shipped example differs from print-wec-subscription"
set +e
admng-watchdog print-wec-subscription > /dev/null 2>&1; code=$?
set -e
[ "$code" = 2 ] || fail "print-wec-subscription without --dc exited $code, expected 2"
chown -R openwec:openwec "$sub"
setpriv --reuid=openwec --regid=openwec --init-groups openwec -c /etc/openwec.conf.toml subscriptions load -y "$sub/admng-dc-security.toml"
setpriv --reuid=openwec --regid=openwec --init-groups openwec -c /etc/openwec.conf.toml subscriptions | grep -q 'admng-dc-security' \
    || fail "subscription not loaded"
rm -rf "$sub"

step "admng-openwec postinst loads /etc/openwec/subscriptions/admng-dc-security.toml only when it is alone there (7.1)"
admng-watchdog print-wec-subscription --dc 'DC01$@FABRIKAM.TEST' | install -m 0644 /dev/stdin /etc/openwec/subscriptions/admng-dc-security.toml
dpkg-reconfigure admng-openwec 2>&1 | tee /tmp/reconfigure.txt
grep -q 'subscription admng-dc-security loaded' /tmp/reconfigure.txt || fail "the subscription was not loaded"
cp /etc/openwec/subscriptions/admng-dc-security.toml /etc/openwec/subscriptions/admng-dc-security.toml.dpkg-dist
dpkg-reconfigure admng-openwec 2>&1 | tee /tmp/reconfigure.txt
grep -q 'holds other files' /tmp/reconfigure.txt || fail "a second file below subscriptions/ was not refused"
rm -f /etc/openwec/subscriptions/admng-dc-security.toml.dpkg-dist /etc/openwec/subscriptions/admng-dc-security.toml

step "OpenWEC file rotation (7.4, 12.4): script, defaults, timer"
expect_stat /usr/lib/admng-watchdog/wec-rotate "root:root 755"
expect_stat /etc/default/admng-wec-rotate "root:root 644"
[ -L /etc/systemd/system/timers.target.wants/admng-wec-rotate.timer ] || fail "admng-wec-rotate.timer not enabled"
grep -qx 'MAX_UNCONSUMED_DAYS=7' /etc/default/admng-wec-rotate || fail "MAX_UNCONSUMED_DAYS default"

step "Keytab tool (4.4, B2): binary, /usr/sbin link, timer enabled"
expect_stat /usr/lib/admng-watchdog/admng-watchdog-keytab "root:root 755"
[ "$(readlink -f /usr/sbin/admng-watchdog-keytab)" = /usr/lib/admng-watchdog/admng-watchdog-keytab ] || fail "/usr/sbin/admng-watchdog-keytab link"
[ -L /etc/systemd/system/timers.target.wants/admng-watchdog-keytab.timer ] || fail "admng-watchdog-keytab.timer not enabled"
admng-watchdog-keytab --help | grep -q 'rotate' || fail "admng-watchdog-keytab --help"
set +e
admng-watchdog-keytab init < /dev/null > /dev/null 2>&1; code_init=$?
admng-watchdog-keytab status > /dev/null 2>&1; code_status=$?
set -e
[ "$code_init" = 1 ] || fail "init with a non-terminal stdin exited $code_init, expected 1 (refused)"
[ "$code_status" = 2 ] || fail "status without init exited $code_status, expected 2"

step "Postinst is idempotent"
dpkg-reconfigure admng-watchdog >/dev/null
dpkg-reconfigure admng-openwec >/dev/null   # db upgrade on an existing database
expect_stat /etc/admng-watchdog/credentials/dp-key.cred "root:root 600"

step "systemd-analyze verify and security (12.x)"
verify_root=$(mktemp -d)
SYSTEMD_LOG_LEVEL=notice systemd-analyze verify \
    /usr/lib/systemd/system/admng-watchdog.service /usr/lib/systemd/system/admng-watchdog-failed.service \
    /usr/lib/systemd/system/admng-watchdog-keytab.service /usr/lib/systemd/system/admng-watchdog-keytab.timer \
    /usr/lib/systemd/system/admng-wec-rotate.service /usr/lib/systemd/system/admng-wec-rotate.timer \
    /usr/lib/systemd/system/openwecd.service 2>&1 | tee "$verify_root/verify.txt"
if grep -v -e '^$' "$verify_root/verify.txt" | grep -q .; then
    fail "systemd-analyze verify reported problems"
fi
for unit in admng-watchdog.service openwecd.service; do
    systemd-analyze security --offline=true --no-pager "$unit" | tail -n 1 | tee "$verify_root/security.txt"
    exposure=$(sed -n "s/.*exposure level for $unit: \\([0-9.]*\\).*/\\1/p" "$verify_root/security.txt")
    awk -v e="$exposure" 'BEGIN { exit !(e != "" && e <= 2.0) }' || fail "$unit exposure $exposure > 2.0"
done

step "nftables example (16)"
if apt-get install -y -qq --no-install-recommends nftables >/dev/null && nft list tables >/dev/null 2>&1; then
    nft -c -f /usr/share/admng-watchdog/nftables/admng-watchdog.nft && echo "nft syntax ok"
else
    echo "notice: no CAP_NET_ADMIN, nft -c skipped"
fi

step "--check-config with the shipped placeholders fails with the documented message"
set +e
out=$(admng-watchdog --check-config 2>&1); code=$?
set -e
echo "$out"
[ "$code" = 2 ] || fail "--check-config exit $code, expected 2"
echo "$out" | grep -q 'Replace the role group SIDs' || fail "placeholder message"

step "--check-config with real SIDs succeeds"
sed -i 's/"S-1-5-21-0-0-0-0"/"S-1-5-21-1004336348-1177238915-682003330-1105"/g' /etc/admng-watchdog/appsettings.json
admng-watchdog --check-config

step "--smoke-test as admng-watchdog"
admng-watchdog --smoke-test

step "Production start without systemd (credential as \$CREDENTIALS_DIRECTORY): start-up checks, TLS, /healthz"
creds=$(mktemp -d)
systemd-creds decrypt --name=dp-key /etc/admng-watchdog/credentials/dp-key.cred "$creds/dp-key"
chown -R admng-watchdog:admng-watchdog "$creds"
chmod 0700 "$creds"; chmod 0400 "$creds/dp-key"
log=$(mktemp)
(cd /var/lib/admng-watchdog && umask 077 && setpriv --reuid=admng-watchdog --regid=admng-watchdog --init-groups \
    env -i PATH=/usr/bin:/bin HOME=/var/lib/admng-watchdog ASPNETCORE_ENVIRONMENT=Production DOTNET_gcServer=0 \
    CREDENTIALS_DIRECTORY="$creds" LDAPCONF=/etc/admng-watchdog/ldap.conf KRB5_CONFIG=/etc/admng-watchdog/krb5.conf \
    /usr/lib/admng-watchdog/AdMng.Watchdog --contentRoot /usr/lib/admng-watchdog --console > "$log" 2>&1) &
pid=$!
healthy=""
for _ in $(seq 1 60); do
    if curl -fsk --max-time 2 https://127.0.0.1:8443/healthz 2>/dev/null | grep -qx ok; then healthy=yes; break; fi
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
done
fingerprint=$(admng-watchdog cert-fingerprint)
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
cat "$log"
rm -rf "$creds"
[ -n "$healthy" ] || fail "the service did not answer /healthz"
grep -q "$fingerprint" "$log" || fail "fingerprint not logged (1008)"
expect_stat /var/lib/admng-watchdog/watchdog.db "admng-watchdog:admng-watchdog 600"
grep -rl 'urn:admng:dp' /var/lib/admng-watchdog/keys >/dev/null || fail "key ring not encrypted with the credential"
if grep -rl '<masterKey' /var/lib/admng-watchdog/keys >/dev/null; then fail "plaintext master key in the key ring"; fi

step "wec-rotate without OpenWEC files and without the service's progress file (7.4 step 5)"
/usr/lib/admng-watchdog/wec-rotate

step "remove keeps configuration and data, purge keeps only the data"
apt-get remove -y admng-watchdog >/dev/null
[ -f /etc/admng-watchdog/appsettings.json ] || fail "remove deleted the configuration"
apt-get purge -y admng-watchdog
[ ! -e /etc/admng-watchdog ] || fail "/etc/admng-watchdog left after purge"
[ ! -e /var/lib/admng-watchdog-keytab ] || fail "/var/lib/admng-watchdog-keytab left after purge"
[ ! -e /usr/sbin/admng-watchdog-keytab ] || fail "/usr/sbin/admng-watchdog-keytab left after purge"
[ ! -e /etc/systemd/system/timers.target.wants/admng-watchdog-keytab.timer ] || fail "timer link left after purge"
[ -f /var/lib/admng-watchdog/watchdog.db ] || fail "purge deleted the history"
getent passwd admng-watchdog >/dev/null || fail "purge removed the system user"
[ ! -e /etc/default/admng-wec-rotate ] || fail "/etc/default/admng-wec-rotate left after purge"
apt-get purge -y admng-openwec
[ ! -e /var/lib/openwec ] || fail "/var/lib/openwec left after purging admng-openwec (ADR-0009)"
[ ! -e /etc/openwec.conf.toml ] || fail "/etc/openwec.conf.toml left after purge"
getent passwd openwec >/dev/null || fail "purge removed the openwec user"
echo
echo "admng-watchdog + admng-openwec package test on $PRETTY_NAME: OK"
