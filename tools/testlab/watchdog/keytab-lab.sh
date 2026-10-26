#!/usr/bin/env bash
# Kerberos client environment for admng-watchdog-keytab and GSSAPI LDAP on the Samba lab (Linux design 4.4, 4.8, 5.1).
#
#   eval "$(tools/testlab/watchdog/keytab-lab.sh env)"           # keytab identity: KRB5_CLIENT_KTNAME + empty MEMORY ccache
#   eval "$(tools/testlab/watchdog/keytab-lab.sh env --admin)"   # … plus an Administrator ticket in a private FILE ccache
#   tools/testlab/watchdog/keytab-lab.sh cleanup
#
# Writes krb5.conf (the shape of packaging/admng-watchdog/etc/krb5.conf: kdc, primary_kdc and kpasswd_server = the DC
# host name, AES only, no DNS lookups) and ldap.conf (SASL_NOCANON on, SASL_SECPROPS minssf=56,noanonymous,noplain) into
# ~/.cache/admng/watchdog-lab/keytab (0700) and prints the exports. The keytab itself is written by the tests
# (KeytabLabTests: init with a one-time password, rotate) at $ADMNG_TEST_KEYTAB_DIR/admng-wd.keytab; libkrb5, libldap
# and Cyrus SASL read the variables natively, so they must be in the environment of the test process.
# No AD object is created here; the tests create and delete their own OU.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

KT_DIR="$WATCHDOG_LAB_STATE/keytab"
REALM="$(tr '[:lower:]' '[:upper:]' <<< "$LAB_DOMAIN_DNS")"

usage() { sed -n '2,13p' "$0"; }

command="${1:-}"
shift || true
admin=false
for arg in "$@"; do
  case "$arg" in
    --admin) admin=true ;;
    *) die "Unknown option: $arg" ;;
  esac
done

case "$command" in
  env) ;;
  cleanup)
    rm -rf -- "$KT_DIR"
    log "Removed $KT_DIR."
    exit 0
    ;;
  -h | --help | "")
    usage
    exit 0
    ;;
  *) die "Unknown command: $command" ;;
esac

require ldapsearch getent
dc="$(ldapsearch -x -LLL -H "ldap://$LAB_SERVER" -b '' -s base dnsHostName 2> /dev/null | sed -n 's/^dnsHostName: //p')"
[ -n "$dc" ] || die "The DC at $LAB_SERVER does not answer (samba -D?)."
getent hosts "$dc" > /dev/null || die "$dc does not resolve - see tools/testlab/README.md (Kerberos (SASL GSSAPI) przez LDAP)."

install -d -m 0700 "$KT_DIR"
cat > "$KT_DIR/krb5.conf" << KRB
[libdefaults]
    default_realm = $REALM
    dns_lookup_kdc = false
    dns_lookup_realm = false
    dns_canonicalize_hostname = false
    rdns = false
    forwardable = false
    udp_preference_limit = 0
    allow_weak_crypto = false
    permitted_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tgs_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tkt_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96

[realms]
    $REALM = {
        kdc = $dc
        primary_kdc = $dc
        kpasswd_server = $dc
        admin_server = $dc
    }

[domain_realm]
    .$LAB_DOMAIN_DNS = $REALM
    $LAB_DOMAIN_DNS = $REALM
KRB
cat > "$KT_DIR/ldap.conf" << 'LDAP'
# AdMng Watchdog lab – private libldap configuration (LDAPCONF), as packaging/admng-watchdog/etc/ldap.conf
SASL_NOCANON   on
SASL_SECPROPS  minssf=56,noanonymous,noplain
REFERRALS      off
TLS_REQCERT    demand
LDAP
chmod 0644 "$KT_DIR/krb5.conf" "$KT_DIR/ldap.conf"

ccache="MEMORY:admng-wd-lab"
if [ "$admin" = true ]; then
  require kinit
  ccache="FILE:$KT_DIR/admin.ccache"
  KRB5_CONFIG="$KT_DIR/krb5.conf" KRB5CCNAME="$ccache" kinit "${LAB_ADMIN_USER%@*}@$REALM" <<< "$LAB_ADMIN_PASSWORD" > /dev/null \
    || die "kinit as the lab administrator failed."
  log "Administrator ticket in $KT_DIR/admin.ccache."
fi

log "krb5.conf and ldap.conf in $KT_DIR (DC $dc, realm $REALM)."
printf 'export KRB5_CONFIG=%q\n' "$KT_DIR/krb5.conf"
printf 'export LDAPCONF=%q\n' "$KT_DIR/ldap.conf"
printf 'export KRB5_KTNAME=%q\n' "FILE:$KT_DIR/admng-wd.keytab"
printf 'export KRB5_CLIENT_KTNAME=%q\n' "FILE:$KT_DIR/admng-wd.keytab"
printf 'export KRB5CCNAME=%q\n' "$ccache"
printf 'export ADMNG_TEST_GSSAPI_SERVER=%q\n' "$dc"
printf 'export ADMNG_TEST_KEYTAB_DIR=%q\n' "$KT_DIR"
