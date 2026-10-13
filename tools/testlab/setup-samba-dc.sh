#!/usr/bin/env bash
# Sets up a disposable Samba AD DC with test data for AD_MNG development.
#
# Intended for a throw-away Linux VM/container (Ubuntu 24.04) or CI runner.
# NEVER run it on a machine that is a member of a real domain: it rewrites
# /etc/samba/smb.conf and /etc/krb5.conf.
#
# Environment variables (all optional):
#   LAB_REALM        default CONTOSO.TEST
#   LAB_DOMAIN       default CONTOSO
#   LAB_ADMIN_PASS   default P@ssw0rd!2026
#   LAB_USERS        default 400   regular employees
#   LAB_COMPUTERS    default 250
#   LAB_BULK_USERS   default 0     extra users in OU=Masowe (performance tests)
#   LAB_SKIP_INSTALL set to 1 when samba/ldap-utils are already installed
set -euo pipefail

REALM="${LAB_REALM:-CONTOSO.TEST}"
DOMAIN="${LAB_DOMAIN:-CONTOSO}"
ADMIN_PASS="${LAB_ADMIN_PASS:-P@ssw0rd!2026}"
USERS="${LAB_USERS:-400}"
COMPUTERS="${LAB_COMPUTERS:-250}"
BULK_USERS="${LAB_BULK_USERS:-0}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dns_domain="$(echo "$REALM" | tr '[:upper:]' '[:lower:]')"
base_dn="DC=${dns_domain//./,DC=}"
admin_upn="Administrator@${dns_domain}"

# samba-tool is a Python script bound to the distro Python; prefer the
# interpreter that ships the samba modules (Ubuntu 24.04: python3.12).
samba_python=/usr/bin/python3
for candidate in /usr/bin/python3.12 /usr/bin/python3; do
  if [ -x "$candidate" ] && "$candidate" -c 'import samba' 2>/dev/null; then
    samba_python="$candidate"
    break
  fi
done
samba_tool() { "$samba_python" /usr/bin/samba-tool "$@"; }

if [ "${LAB_SKIP_INSTALL:-0}" != "1" ]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -q
  apt-get install -y -q samba samba-ad-dc samba-ad-provision winbind krb5-user ldap-utils python3 >/dev/null
fi

for svc in smbd nmbd winbind samba-ad-dc; do
  systemctl stop "$svc" 2>/dev/null || true
done
if pgrep -x samba >/dev/null; then
  kill $(pgrep -x samba) || true
  sleep 3
fi

rm -f /etc/samba/smb.conf
rm -rf /var/lib/samba/private/* /var/lib/samba/sysvol/* 2>/dev/null || true

samba_tool domain provision \
  --realm="$REALM" --domain="$DOMAIN" --server-role=dc --dns-backend=NONE \
  --adminpass="$ADMIN_PASS" --use-rfc2307 \
  --option="interfaces = lo" --option="bind interfaces only = yes" >/dev/null

# Lab only: allow simple binds without TLS so that ldapsearch -x works. The
# application itself always uses Negotiate (Kerberos/NTLM) with signing or TLS.
sed -i 's/^\[global\]$/[global]\n\tldap server require strong auth = no/' /etc/samba/smb.conf
cp /var/lib/samba/private/krb5.conf /etc/krb5.conf

samba -D
for _ in $(seq 1 60); do
  if ldapsearch -x -H ldap://127.0.0.1 -D "$admin_upn" -w "$ADMIN_PASS" -b "$base_dn" -s base dn >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

# Lab password policy: lockout after 5 bad attempts so that "unlock" can be tested.
samba_tool domain passwordsettings set --account-lockout-threshold=5 \
  --account-lockout-duration=30 --reset-account-lockout-after=30 >/dev/null

seed="$(mktemp)"
python3 "$HERE/generate_ldif.py" --base "$base_dn" --domain "$dns_domain" \
  --users "$USERS" --computers "$COMPUTERS" --bulk-users "$BULK_USERS" > "$seed"
# unicodePwd requires an encrypted channel - load over LDAPS (self-signed lab cert).
LDAPTLS_REQCERT=never ldapadd -x -c -H ldaps://127.0.0.1 -D "$admin_upn" -w "$ADMIN_PASS" -f "$seed" >/dev/null
rm -f "$seed"

# Delegation for the helpdesk group on OU=Pracownicy (inherited to user objects):
#   reset password, read/write lockoutTime + pwdLastSet, write telephone numbers.
helpdesk_sid="$(ldapsearch -x -LLL -H ldap://127.0.0.1 -D "$admin_upn" -w "$ADMIN_PASS" -b "$base_dn" \
  '(sAMAccountName=GG_Helpdesk)' objectSid | sed -n 's/^objectSid:: //p' | "$samba_python" -c '
import base64, sys
from samba.dcerpc import security
from samba.ndr import ndr_unpack
print(str(ndr_unpack(security.dom_sid, base64.b64decode(sys.stdin.read().strip()))))')"
user_class=bf967aba-0de6-11d0-a285-00aa003049e2
reset_password=00299570-246d-11d0-a768-00aa006e0529
lockout_time=28630ebf-41d5-11d1-a9c1-0000f80367c1
pwd_last_set=bf967a0a-0de6-11d0-a285-00aa003049e2
telephone_number=bf967a49-0de6-11d0-a285-00aa003049e2
mobile=f0f8ffa3-1191-11d0-a060-00aa006c33ed
employees_ou="OU=Pracownicy,OU=Firma,$base_dn"
for ace in \
  "(OA;CIIO;CR;$reset_password;$user_class;$helpdesk_sid)" \
  "(OA;CIIO;RPWP;$lockout_time;$user_class;$helpdesk_sid)" \
  "(OA;CIIO;RPWP;$pwd_last_set;$user_class;$helpdesk_sid)" \
  "(OA;CIIO;RPWP;$telephone_number;$user_class;$helpdesk_sid)" \
  "(OA;CIIO;RPWP;$mobile;$user_class;$helpdesk_sid)"; do
  samba_tool dsacl set -H ldap://127.0.0.1 -U "Administrator%$ADMIN_PASS" \
    --objectdn="$employees_ou" --sddl="$ace" >/dev/null
done

# Lock out a few accounts by failing binds (threshold = 5).
for sam in $(ldapsearch -x -LLL -H ldap://127.0.0.1 -D "$admin_upn" -w "$ADMIN_PASS" \
    -b "OU=Sprzedaż,OU=Pracownicy,OU=Firma,$base_dn" '(&(objectCategory=person)(objectClass=user))' sAMAccountName 2>/dev/null \
    | sed -n 's/^sAMAccountName: //p' | head -n 3); do
  for _ in 1 2 3 4 5 6; do
    ldapsearch -x -H ldap://127.0.0.1 -D "$sam@$dns_domain" -w wrong-password -b "" -s base >/dev/null 2>&1 || true
  done
done

cat <<EOF
Samba AD DC is running.
  LDAP:           ldap://127.0.0.1:389  (ldaps://127.0.0.1:636, self-signed)
  Base DN:        $base_dn
  Administrator:  $admin_upn / $ADMIN_PASS
  Helpdesk:       helpdesk.test@$dns_domain / Passw0rd!Lab2026 (delegated on OU=Pracownicy)
  Read-only user: odczyt.test@$dns_domain / Passw0rd!Lab2026
Integration tests:
  export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='$admin_upn' ADMNG_TEST_LDAP_PASSWORD='$ADMIN_PASS'
EOF
