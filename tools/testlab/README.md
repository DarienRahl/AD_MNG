# Laboratorium testowe (Samba AD DC)

Jednorazowy kontroler domeny do rozwoju i testów integracyjnych – **nigdy** nie uruchamiaj tych skryptów
na komputerze należącym do prawdziwej domeny (nadpisują `/etc/samba/smb.conf` i `/etc/krb5.conf`).

```bash
sudo LAB_USERS=400 LAB_COMPUTERS=250 LAB_BULK_USERS=0 tools/testlab/setup-samba-dc.sh
```

Skrypt instaluje Sambę (Ubuntu 24.04), tworzy domenę `CONTOSO.TEST` i ładuje dane wygenerowane przez
`generate_ldif.py` (deterministyczne, ziarno 2026):

```
DC=contoso,DC=test
└── OU=Firma
    ├── OU=Pracownicy        OU=IT, Księgowość, Sprzedaż, Kadry, Marketing, Logistyka, Zarząd
    ├── OU=Komputery         OU=Warszawa, Kraków, Gdańsk, Wrocław, Poznań  (każde: Laptopy, Stacjonarne)
    ├── OU=Serwery
    ├── OU=Grupy             OU=Bezpieczeństwa, OU=Dystrybucyjne (grupy globalne/lokalne/uniwersalne, zagnieżdżenia)
    ├── OU=Konta serwisowe   konta testowe i usługowe
    ├── OU=Wyłączone
    ├── OU=Kontakty
    └── OU=Masowe            tylko gdy LAB_BULK_USERS > 0 (testy wydajności, grupa > 1500 członków)
```

| Konto | Hasło | Uprawnienia |
|---|---|---|
| `Administrator@contoso.test` | `P@ssw0rd!2026` | Domain Admin |
| `helpdesk.test@contoso.test` | `Passw0rd!Lab2026` | delegacja na `OU=Pracownicy`: reset hasła, odblokowanie, `pwdLastSet`, telefony |
| `odczyt.test@contoso.test` | `Passw0rd!Lab2026` | zwykły użytkownik – tylko odczyt |

Kilka kont w `OU=Sprzedaż` jest celowo zablokowanych (próg blokady = 5).

Testy integracyjne:

```bash
export ADMNG_TEST_LDAP_SERVER=127.0.0.1
export ADMNG_TEST_LDAP_USER='Administrator@contoso.test'
export ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
dotnet test tests/AdMng.Directory.IntegrationTests
```

Lab dopuszcza proste wiązanie (simple bind) bez TLS wyłącznie po to, by działały `ldapsearch -x` i testy na
Linuksie. Aplikacja produkcyjnie używa Negotiate (Kerberos) z podpisywaniem i szyfrowaniem lub LDAPS.
