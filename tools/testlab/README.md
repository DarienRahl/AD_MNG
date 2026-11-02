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

Jeśli laboratorium nie odpowiada na porcie 389 (np. po restarcie kontenera), uruchom je ponownie poleceniem
`samba -D` i poczekaj, aż port 389 zacznie przyjmować połączenia.

### ServerUnavailable (LDAP 81) w testach

Sporadyczne w CI błędy „Add failed: ServerUnavailable (LDAP 81)” podczas masowego tworzenia kontaktów nie pochodziły od
Samby. W laboratorium ten sam błąd pojawiał się przy równoległych zapisach, gdy kończyły się procesy potomne procesu
testów, przy czym Samba nie zamykała żadnego połączenia (w nagraniu ruchu każde FIN/RST wysyłał klient, w jej dzienniku
na poziomie 3 są tylko rozłączenia klienta), a każdy „nieudany” obiekt istniał po teście. Przyczyna leżała w kliencie:
zakończenie procesu potomnego (`ldapsearch`, `samba-tool`, `smbclient` uruchamiane przez testy) wysyła do procesu testów
sygnał `SIGCHLD`, który przerywa `poll()` libldap czekającej na odpowiedź, a libldap bez opcji `LDAP_OPT_RESTART`
zgłasza to jako „server down”. Każde połączenie ustawia teraz tę opcję (`src/AdMng.Directory/OpenLdapRestart.cs`, test
`tests/AdMng.Directory.IntegrationTests/Integration/SignalTests.cs`).

Jeśli LDAP 81 wróci, najpierw sprawdź, kto zamyka połączenia: przełącz procesy serwera LDAP Samby na poziom dziennika 3
(ok. 1 KB dziennika na operację; potem z powrotem `debug 1`) i policz powody zakończenia połączeń:

```bash
for pid in $(ps -eo pid,comm | awk '$2 ~ /^ldap/ {print $1}'); do sudo smbcontrol "$pid" debug 3; done
sudo grep -o "Terminating connection - '[^']*'" /var/log/samba/log.samba | sort | uniq -c
```

`NT_STATUS_LOCAL_DISCONNECT` (po żądaniu unbind) oraz `NT_STATUS_CONNECTION_DISCONNECTED` i
`NT_STATUS_CONNECTION_RESET` (klient zamknął TCP) to rozłączenia, które zaczął klient; inny powód wskazuje, że połączenie
zakończyła Samba.

### Kerberos (SASL GSSAPI) przez LDAP

AdMng Watchdog na Linuksie łączy się z DC przez LDAP 389 z Kerberosem i szyfrowaniem SASL (GSSAPI, SSF ≥ 56). Do tego
konto DC musi mieć SPN-y `ldap/<nazwa DC>` (np. `ldap/vm.contoso.test`), a pełna nazwa DC musi się rozwiązywać.
Wcześniej laboratorium miało tylko SPN-y `HOST/…` i GSSAPI kończyło się błędem `Server not found in Kerberos
database`. Przyczyna: Samba dopisuje SPN-y `ldap/` zadaniem `dnsupdate`, które uruchamia `samba_spnupdate` przez
`#!/usr/bin/python3`; gdy `/usr/bin/python3` to inna wersja niż ta, dla której zbudowano moduły Samby (tu 3.13 zamiast
3.12), skrypt kończy się po cichu błędem `No module named 'ldb'`. Dodatkowo `--dns-backend=NONE` nie daje żadnego
rekordu dla nazwy DC.

`setup-samba-dc.sh` robi teraz oba kroki sam (bez zmiany `smb.conf`). W działającym, starszym laboratorium wystarczy
raz (jako root):

```bash
dc=$(ldapsearch -x -LLL -H ldap://127.0.0.1 -b '' -s base dnsHostName | sed -n 's/^dnsHostName: //p')
getent hosts "$dc" || echo "127.0.0.1 $dc" >> /etc/hosts
/usr/bin/python3.12 /usr/sbin/samba_spnupdate --verbose      # interpreter z modułami Samby
```

Sprawdzenie (prywatny `krb5.conf` z `kdc = <nazwa DC>`, bilet w prywatnej pamięci podręcznej):

```bash
KRB5_CONFIG=… KRB5CCNAME=FILE:… kinit Administrator@CONTOSO.TEST
LDAPCONF=… ldapsearch -Y GSSAPI -H "ldap://$dc" -b 'DC=contoso,DC=test' -s base dn   # SASL SSF: 256
```

`LDAPCONF` wskazuje plik z `SASL_NOCANON on` i `SASL_SECPROPS minssf=56,noanonymous,noplain` (jak
`packaging/admng-watchdog/etc/ldap.conf`, w pakiecie `/etc/admng-watchdog/ldap.conf`). Testy narzędzia keytabu i GSSAPI opisuje
[`watchdog/README.md`](watchdog/README.md) („Konto usługi i keytab”).

### Sprzątanie po testach

Każdy przebieg testów pracuje we własnym OU bezpośrednio pod domeną: `OU=AdMngIntegration-<czas UTC>-<losowe>`
(`tests/AdMng.Directory.IntegrationTests`) albo `OU=WatchdogIT-<czas UTC>-<losowe>`
(`tests/AdMng.Watchdog.IntegrationTests`), więc równoległe przebiegi (kilka kopii roboczych, zadania CI) nie
przeszkadzają sobie. Opis OU wskazuje proces testów (`[run <host> pid <id> started <czas>]`). Na starcie przebieg usuwa
OU przebiegów, które się skończyły – proces na tym samym hoście już nie działa (przebieg przerwany) albo OU jest
starsze niż 2 godziny (właściciel na innym hoście, stare `OU=AdMngIntegration` wcześniejszych wersji) – a OU
trwających przebiegów zostawia. Na końcu usuwa własne OU.

Samba wykonuje naraz tylko jedną transakcję zapisu (w laboratorium ok. 0,1 s na usunięty obiekt) i przerywa każde
żądanie LDAP po 300 s. Usunięcie drzewa po stronie serwera (kontrolka tree delete) dla OU z tysiącami obiektów –
przebieg testów katalogu tworzy dwie grupy po 1600 członków i 3200 kontaktów, czyli ok. 5 minut pracy – kończyło się
więc błędem `timeLimitExceeded`, zostawiało obiekty i przez cały ten czas blokowało zapisy innych przebiegów. Dlatego
testy usuwają drzewa po stronie klienta (`tests/AdMng.Directory.IntegrationTests/Infrastructure/LabCleanup.cs`): grupy
tracą członków jedną modyfikacją, potem każdy obiekt jest usuwany osobno, od najgłębszych, z limitem 10 minut na
żądanie. Każde żądanie trwa ułamek sekundy, więc zapisy innych przebiegów przeplatają się z nimi. Jeśli laboratorium
jest zajęte (przekroczony czas, LDAP busy) albo nie działa, przebieg kończy się komunikatem, co zrobić; pozostałości,
których nie udało się usunąć, są tylko zgłaszane (komunikaty diagnostyczne:
`dotnet test ... -- xUnit.DiagnosticMessages=true`) i usunie je następny przebieg.

Ręcznie (tylko własne OU testów, nigdy `OU=Firma`):

```bash
ldapsearch -x -H ldap://127.0.0.1 -D 'Administrator@contoso.test' -w 'P@ssw0rd!2026' \
  -b 'DC=contoso,DC=test' -s one '(|(ou=AdMngIntegration*)(ou=WatchdogIT-*))' dn description whenCreated
ldapdelete -x -H ldap://127.0.0.1 -D 'Administrator@contoso.test' -w 'P@ssw0rd!2026' -r 'OU=WatchdogIT-…,DC=contoso,DC=test'
```

## AdMng Watchdog

Usługę monitorującą **AdMng Watchdog** uruchamia się przeciwko temu laboratorium skryptami z
[`watchdog/`](watchdog/README.md): Mailpit jako serwer SMTP z podglądem wiadomości, odbiornik webhooków i kart Teams,
scenariusz zmian w jednorazowym `OU=WatchdogDemo` oraz opcjonalny prawdziwy Kerberos.

```bash
tools/testlab/watchdog/run-watchdog-lab.sh --reset --background   # usługa + Mailpit + odbiornik
tools/testlab/watchdog/configure-lab.sh --test                    # SMTP, kanały, odbiorcy (przez API)
tools/testlab/watchdog/lab-scenario.sh --verify                   # zmiany w katalogu i sprawdzenie powiadomień
tools/testlab/watchdog/run-watchdog-lab.sh --stop
```

Testy integracyjne usługi (`tests/AdMng.Watchdog.IntegrationTests`) potrzebują dodatkowo konta tylko do odczytu i
Mailpit:

```bash
tools/testlab/watchdog/start-mailpit.sh
export ADMNG_TEST_LDAP_READONLY_USER='odczyt.test@contoso.test' ADMNG_TEST_LDAP_READONLY_PASSWORD='Passw0rd!Lab2026'
export ADMNG_TEST_SMTP_HOST=127.0.0.1 ADMNG_TEST_SMTP_PORT=1025 ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025
dotnet test tests/AdMng.Watchdog.IntegrationTests
```
