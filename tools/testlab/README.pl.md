[English](README.md) · **Polski**

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
[`watchdog/README.md`](watchdog/README.pl.md) („Konto usługi i keytab”).

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

### Przebieg, który kończy się kodem wyjścia 134 (DLR-4)

Testy integracyjne Watchdoga w przebiegu CI 45409758368 (commit `7e13cff6`) skończyły się po ok. 9 s komunikatem
`Catastrophic failure: Test process crashed with exit code 134` (SIGABRT), bez żadnego nieudanego testu. Raport awarii
wskazał przyczynę: `__libc_free` ← `LdapConnection.ConstructEntry` ← `ldap_next_attribute` ← `ConstructEntry` ←
`SendRequest` ← `AdMng.Directory.LdapAsync` (wątek roboczy, który wykonuje wyszukiwanie). System.DirectoryServices.Protocols
zwalnia każdą nazwę atrybutu wpisu odpowiedzi (`ldap_memfree`), a potem wywołuje `ldap_next_attribute` z uchwytem
połączenia; gdy połączenie zostało w międzyczasie zwolnione (Dispose), to wywołanie rzuca wyjątek, a blok `finally` metody
zwalnia tę samą nazwę drugi raz ([dotnet/runtime#35189](https://github.com/dotnet/runtime/issues/35189), otwarte od 2020).
glibc przerywa proces (`free(): double free detected in tcache 2`, `corrupted double-linked list`) albo sterta zostaje
uszkodzona i proces kończy się później sygnałem SIGSEGV (kod wyjścia 139) lub zawiesza się w `malloc`. AD Manager zwalniał
połączenia z puli, gdy wyszukiwanie na nich jeszcze budowało odpowiedź: na Linuksie wyszukiwanie wykonuje blokujące
`SendRequest` na wątku roboczym, które trwa dalej po anulowaniu wywołującego, a zamknięcie sesji zwalniało od razu
wszystkie połączenia – host Watchdoga, który się zatrzymuje (każdy host testowy, `systemctl stop` lub `restart`,
aktualizacja pakietu), sesja porzucana przez usługę po chwilowym błędzie, gdy inne zadanie czyta, połączenie anulowane
lub nieudane w trakcie pierwszych odczytów. Ten sam kod działa na Windows (sesja okna połączenia); tam błędu nie
odtworzono.

Od tej poprawki połączenie nigdy nie jest zwalniane, gdy trwa na nim żądanie (`src/AdMng.Directory/LdapConnectionPool.cs`).
Żądanie trwa, dopóki S.DS.P z nim nie skończy, także gdy wywołujący dawno przestał czekać; połączenie, które opuszcza pulę
(zerwane, bezczynne albo sesja się zamyka), nie przyjmuje nowych żądań i jest zwalniane po zakończeniu ostatniego z nich.
Zamknięcie sesji anuluje jej żądania (wywołujący dostają od razu `ObjectDisposedException`), zamyka gniazdo zajętego
połączenia (żądanie czekające na serwer wraca) i czeka najwyżej 2 s; połączenie, na którym żądanie trwa dłużej, jest
zwalniane po jego zakończeniu. Tak samo nawiązywanie połączenia postępuje ze swoim pierwszym połączeniem.

Sprawdza to `tests/AdMng.Directory.IntegrationTests`: `Unit/PoolCloseTests` ze sztucznym nadawcą (bez laboratorium) oraz
`Integration/CloseWhileRequestRunsTests` przeciw laboratorium w procesie potomnym – program `tests/AdMng.Directory.DisposeProbe`
zamyka sesję w wielu chwilach wyszukiwania wszystkich użytkowników i komputerów ze wszystkimi atrybutami, a chwilę, w której
S.DS.P buduje wpisy, rozpoznaje po pamięci, którą przydziela (`ldap_result` czekając nie przydziela żadnej). Kontrola
pozytywna zwalnia w ten sam sposób zwykłe połączenia S.DS.P i ten proces potomny musi zginąć. W laboratorium, 20 przebiegów
po 20 zamknięć na przypadek: przed poprawką żaden z 60 procesów potomnych nie przetrwał (zwolnienie w trakcie wyszukiwania
6 × 134, 10 × 139 i 4 × żądanie na zwolnionym połączeniu; anulowanie, potem zwolnienie 6 × 134 i 14 × 139; anulowane
nawiązywanie połączenia 16 × 134 i 4 × 139), po niej każdy z 60 zakończył się kodem wyjścia 0 (1200 zamknięć). Z biblioteką
System.DirectoryServices.Protocols 10.0.13, którą dostarcza Watchdog (ten projekt testów dostaje 10.0.15 przez PowerShell
SDK), wynik jest ten sam: 15 z 15 przebiegów trzech przypadków przetrwało, 5 z 5 przebiegów kontroli pozytywnej zginęło.
Same testy integracyjne Watchdoga przed poprawką nie wywróciły się w 20 lokalnych przebiegach – przerwanie wymaga, by
zamknięcie trafiło w pętlę wpisów dużej odpowiedzi, a to wymusza dopiero ten program – a po niej przeszły w 20 z 20
przebiegów. Ręcznie:

```bash
dotnet build tests/AdMng.Directory.IntegrationTests -c Release
cd tests/AdMng.Directory.IntegrationTests/bin/Release/net10.0
dotnet AdMng.Directory.DisposeProbe.dll dispose-during-search 20   # także cancel-then-dispose, cancel-connect: kod wyjścia 0
dotnet AdMng.Directory.DisposeProbe.dll raw 60                     # kontrola pozytywna: ginie (134 lub 139) albo się zawiesza
```

Gdyby przebieg znów skończył się kodem wyjścia 134, zadanie CI „Integration tests” wypisuje raport awarii procesu (krok
„Crash reports”, `crash-report-summary.py`: wątek, który się wywrócił, z ramkami natywnymi i zarządzanymi).

## AdMng Watchdog

Usługę monitorującą **AdMng Watchdog** uruchamia się przeciwko temu laboratorium skryptami z
[`watchdog/`](watchdog/README.pl.md): Mailpit jako serwer SMTP z podglądem wiadomości, odbiornik webhooków i kart Teams,
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
