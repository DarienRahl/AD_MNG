#!/usr/bin/env bash
# Optional real Kerberos for the watchdog API on the Samba lab (spec 22.4, KerberosNegotiateTests).
#
#   sudo -E tools/testlab/watchdog/krb5-lab.sh            # prints the export lines
#   eval "$(sudo -E tools/testlab/watchdog/krb5-lab.sh)"  # … or applies them to the current shell
#   sudo -E tools/testlab/watchdog/krb5-lab.sh --cleanup
#
# Creates OU=WatchdogKerberos with the service account wd-krb-http (random password set over StartTLS, AES only,
# SPN HTTP/localhost), exports its keys with samba-tool on the DC host into a keytab and writes a private krb5.conf
# (kdc = 127.0.0.1, no DNS lookups) – both in ~/.cache/admng/watchdog-lab/kerberos (mode 0700). Prints KRB5_CONFIG,
# KRB5_KTNAME (server keytab) and KRB5CCNAME (a private client cache: kinit odczyt.test@CONTOSO.TEST into it).
# Then: ADMNG_TEST_KERBEROS=1 dotnet test tests/AdMng.Watchdog.IntegrationTests --filter KerberosNegotiateTests
# Needs root (samba-tool reads the local sam.ldb) and runs only on the lab DC itself.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$HERE/common.sh"

cleanup_only=false
for arg in "$@"; do
  case "$arg" in
    --cleanup) cleanup_only=true ;;
    -h | --help)
      sed -n '2,15p' "$0"
      exit 0
      ;;
    *) die "Unknown option: $arg" ;;
  esac
done

KRB_OU="OU=WatchdogKerberos,$LAB_BASE_DN"
KRB_SAM="wd-krb-http"
KRB_DN="CN=$KRB_SAM,$KRB_OU"
KRB_SPN="HTTP/localhost"
REALM="$(tr '[:lower:]' '[:upper:]' <<< "$LAB_DOMAIN_DNS")"
KRB_DIR="$WATCHDOG_LAB_STATE/kerberos"

exists() { ldap_admin ldapsearch -LLL -b "$1" -s base '(objectClass=*)' 1.1 > /dev/null 2>&1; }

if [ "$cleanup_only" = true ]; then
  if exists "$KRB_OU"; then
    ldap_admin ldapdelete -r "$KRB_OU"
    log "Deleted $KRB_OU."
  fi
  rm -rf -- "$KRB_DIR"
  log "Removed $KRB_DIR."
  exit 0
fi

require ldapadd ldapmodify ldapsearch ldapdelete iconv base64 python3
[ "$(id -u)" = 0 ] || die "Run as root (sudo -E): samba-tool exportkeytab reads the local sam.ldb."

# samba-tool is bound to the distro Python that ships the samba modules (Ubuntu 24.04: python3.12).
samba_python=""
for candidate in /usr/bin/python3.12 /usr/bin/python3; do
  if [ -x "$candidate" ] && "$candidate" -c 'import samba' 2> /dev/null; then
    samba_python="$candidate"
    break
  fi
done
[ -n "$samba_python" ] || die "No Python with the samba modules found – run this on the lab DC."

# The SPN must be unique in the forest.
owners="$(ldap_admin ldapsearch -LLL -b "$LAB_BASE_DN" "(servicePrincipalName=$KRB_SPN)" 1.1 2> /dev/null | grep -c '^dn:' || true)"
if exists "$KRB_OU"; then
  ldap_admin ldapdelete -r "$KRB_OU"
  owners=0
fi
[ "$owners" = 0 ] || die "$KRB_SPN is already registered on another account."

password="$(python3 -c 'import secrets; print("Krb-" + secrets.token_urlsafe(24) + "-9a!")')"
encoded="$(printf '"%s"' "$password" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)"
ldap_admin ldapadd > /dev/null << EOF
dn: $KRB_OU
objectClass: organizationalUnit
description: AdMng Watchdog Kerberos lab (safe to delete)

dn: $KRB_DN
objectClass: user
sAMAccountName: $KRB_SAM
userPrincipalName: $KRB_SAM@$LAB_DOMAIN_DNS
userAccountControl: 514
servicePrincipalName: $KRB_SPN
msDS-SupportedEncryptionTypes: 24
EOF
LDAPTLS_REQCERT=never ldap_admin ldapmodify -ZZ > /dev/null << EOF
dn: $KRB_DN
changetype: modify
replace: unicodePwd
unicodePwd:: $encoded
-
replace: userAccountControl
userAccountControl: 66048
EOF
unset password encoded

install -d -m 0700 "$KRB_DIR"
rm -f -- "$KRB_DIR/http.keytab"
"$samba_python" /usr/bin/samba-tool domain exportkeytab "$KRB_DIR/http.keytab" --principal="$KRB_SAM@$REALM" > /dev/null
"$samba_python" /usr/bin/samba-tool domain exportkeytab "$KRB_DIR/http.keytab" --principal="$KRB_SPN" > /dev/null 2>&1 || true
chmod 0600 "$KRB_DIR/http.keytab"

cat > "$KRB_DIR/krb5.conf" << EOF
[libdefaults]
    default_realm = $REALM
    dns_lookup_kdc = false
    dns_lookup_realm = false
    rdns = false
    default_tgs_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tkt_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96

[realms]
    $REALM = {
        kdc = $LAB_SERVER
    }

[domain_realm]
    localhost = $REALM
    .$LAB_DOMAIN_DNS = $REALM
    $LAB_DOMAIN_DNS = $REALM
EOF
chmod 0600 "$KRB_DIR/krb5.conf"

log "Service account $KRB_SAM ($KRB_SPN) in $KRB_OU, keytab and krb5.conf in $KRB_DIR."
log "Client ticket: KRB5_CONFIG=$KRB_DIR/krb5.conf KRB5CCNAME=FILE:$KRB_DIR/client.ccache kinit $LAB_READONLY_USER"
printf 'export KRB5_CONFIG=%q\n' "$KRB_DIR/krb5.conf"
printf 'export KRB5_KTNAME=%q\n' "FILE:$KRB_DIR/http.keytab"
printf 'export KRB5CCNAME=%q\n' "FILE:$KRB_DIR/client.ccache"
