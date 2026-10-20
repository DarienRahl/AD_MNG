#!/usr/bin/env bash
# Install test of the admng-watchdog .deb in a plain Debian 12 or 13 container, as root (CI job "linux-package",
# docs/dev/watchdog-linux-design.md 20 item 4, 21 B1). No systemd runs as PID 1: units are verified offline.
#
#   tools/packaging/test-deb-install.sh PATH/admng-watchdog_*.deb [PACKAGING_DIR]
#
# The container needs CAP_NET_ADMIN for the nftables syntax check (docker run --cap-add=NET_ADMIN); without it the
# check is skipped with a notice.
set -euo pipefail

deb=$(readlink -f "$1")
packaging=$(readlink -f "${2:-$(dirname "${BASH_SOURCE[0]}")/../../packaging/admng-watchdog}")
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
apt-get install -y --no-install-recommends "$deb"
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

step "Postinst is idempotent"
dpkg-reconfigure admng-watchdog >/dev/null
expect_stat /etc/admng-watchdog/credentials/dp-key.cred "root:root 600"

step "systemd-analyze verify and security (12.x)"
verify_root=$(mktemp -d)
for unit in admng-watchdog-keytab.service admng-watchdog-keytab.timer admng-wec-rotate.service admng-wec-rotate.timer; do
    cp "$packaging/systemd/$unit" /etc/systemd/system/
done
# The keytab tool (B2) and wec-rotate (B4) are not in this package yet: stubs for the executable checks.
for stub in admng-watchdog-keytab wec-rotate; do
    [ -e "/usr/lib/admng-watchdog/$stub" ] || { printf '#!/bin/sh\nexit 0\n' > "/usr/lib/admng-watchdog/$stub"; chmod 0755 "/usr/lib/admng-watchdog/$stub"; }
done
# OpenWEC (admng-openwec, B4) is ordered After= but not required.
SYSTEMD_LOG_LEVEL=notice systemd-analyze verify \
    /usr/lib/systemd/system/admng-watchdog.service /usr/lib/systemd/system/admng-watchdog-failed.service \
    /etc/systemd/system/admng-watchdog-keytab.service /etc/systemd/system/admng-watchdog-keytab.timer \
    /etc/systemd/system/admng-wec-rotate.service /etc/systemd/system/admng-wec-rotate.timer 2>&1 | tee "$verify_root/verify.txt"
if grep -v -e 'openwecd.service' -e '^$' "$verify_root/verify.txt" | grep -q .; then
    fail "systemd-analyze verify reported problems"
fi
rm -f /etc/systemd/system/admng-watchdog-keytab.* /etc/systemd/system/admng-wec-rotate.* \
    /usr/lib/admng-watchdog/admng-watchdog-keytab /usr/lib/admng-watchdog/wec-rotate
systemd-analyze security --offline=true --no-pager admng-watchdog.service | tail -n 1 | tee "$verify_root/security.txt"
exposure=$(sed -n 's/.*exposure level for admng-watchdog.service: \([0-9.]*\).*/\1/p' "$verify_root/security.txt")
awk -v e="$exposure" 'BEGIN { exit !(e != "" && e <= 2.0) }' || fail "exposure $exposure > 2.0"

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

step "remove keeps configuration and data, purge keeps only the data"
apt-get remove -y admng-watchdog >/dev/null
[ -f /etc/admng-watchdog/appsettings.json ] || fail "remove deleted the configuration"
apt-get purge -y admng-watchdog
[ ! -e /etc/admng-watchdog ] || fail "/etc/admng-watchdog left after purge"
[ ! -e /var/lib/admng-watchdog-keytab ] || fail "/var/lib/admng-watchdog-keytab left after purge"
[ -f /var/lib/admng-watchdog/watchdog.db ] || fail "purge deleted the history"
getent passwd admng-watchdog >/dev/null || fail "purge removed the system user"
echo
echo "admng-watchdog package test on $PRETTY_NAME: OK"
