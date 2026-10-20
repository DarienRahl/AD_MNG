# AdMng Watchdog w laboratorium (Samba AD DC)

Skrypty z tego katalogu uruchamiają usługę **AdMng Watchdog** na Linuksie przeciwko laboratoryjnemu kontrolerowi
domeny z `tools/testlab` (`CONTOSO.TEST`), z lokalnym serwerem pocztowym **Mailpit** i odbiornikiem webhooków.
Wszystko działa tylko na pętli zwrotnej (`127.0.0.1`), a scenariusz zmienia katalog wyłącznie w jednorazowych OU,
które sam tworzy i usuwa. Hasła laboratorium są publicznymi wartościami testowymi – nigdy nie używaj tych skryptów
w prawdziwej domenie.

| Plik | Do czego służy |
|---|---|
| `start-mailpit.sh` / `stop-mailpit.sh` | pobiera przypiętą wersję Mailpit **v1.31.5** (SHA-256 sprawdzany przed rozpakowaniem, do `~/.cache/admng/mailpit/`), uruchamia SMTP `127.0.0.1:1025` (STARTTLS z certyfikatem samopodpisanym, dowolny login) i UI/API `http://127.0.0.1:8025`; `--require-starttls` wymusza STARTTLS |
| `webhook-sink.py` | odbiornik `http://127.0.0.1:8091` (sama biblioteka standardowa Pythona): sprawdza podpisy Standard Webhooks (`SINK_SECRET`) i tolerancję znacznika czasu 5 min, wypisuje typ / id / obiekt, tryb Teams dla ścieżek `/teams…` (koperta `message` z kartą Adaptive Card – wypisuje tytuł karty); odpowiedzi sterowane `SINK_STATUS` (`202`, `429`, `500`) i `SINK_RETRY_AFTER` lub w locie `PUT /_sink/response`; `GET /_sink/requests` zwraca podsumowanie odebranych wywołań |
| `appsettings.Lab.json` | plik startowy usługi (profil `Lab`): uwierzytelnianie `Development` (tylko pętla zwrotna), `http://127.0.0.1:5089`, odczyt katalogu jako `odczyt.test` prostym wiązaniem bez TLS (`AllowInsecureLab`, tylko w środowisku Development), dane w `~/.local/share/admng-watchdog-lab` |
| `run-watchdog-lab.sh` | uruchamia Mailpit, odbiornik i usługę (`dotnet run --launch-profile Lab`); hasło LDAP trafia do usługi wyłącznie przez zmienną `ADMNG_WATCHDOG_LDAP_PASSWORD` (z `ADMNG_TEST_LDAP_READONLY_PASSWORD`), nigdy do pliku; `--reset` usuwa folder danych (nowy baseline), `--background` uruchamia usługę w tle, `--stop` zatrzymuje wszystko |
| `configure-lab.sh` | konfiguruje usługę przez jej API: SMTP → Mailpit (certyfikat przypięty SHA-256), grupy odbiorców `it-admins` = `admin@contoso.test`, `helpdesk` = `helpdesk@contoso.test`, kanały `siem-lab` (webhook) i `teams-lab` (Teams) → odbiornik, raport dzienny za 2 minuty; `--test` wysyła wiadomości testowe |
| `lab-scenario.sh` | scenariusz demonstracyjny w `OU=WatchdogDemo` (Zofia Żak, Łukasz Wiśniewski, Paweł Lewandowski, Grażyna Dąbrowska); `--verify` sprawdza zdarzenia w API, e-maile w Mailpit i podpisy w odbiorniku; `--cleanup` usuwa OU |
| `krb5-lab.sh` | opcjonalny prawdziwy Kerberos dla testu `KerberosNegotiateTests`: konto usługi z SPN `HTTP/localhost` w `OU=WatchdogKerberos`, keytab (`samba-tool domain exportkeytab`) i prywatny `krb5.conf`; `--cleanup` |
| `keytab-lab.sh` | środowisko Kerberos klienta dla narzędzia `admng-watchdog-keytab` i LDAP przez SASL GSSAPI: prywatne `krb5.conf` i `ldap.conf` (jak w pakiecie), zmienne `KRB5_*`, `LDAPCONF`; `env [--admin]`, `cleanup` |
| `common.sh` | wspólne ustawienia i funkcje (dołączany przez pozostałe skrypty) |

Wymagania: laboratorium z `tools/testlab/setup-samba-dc.sh`, .NET SDK 10, `ldap-utils`, `curl`, `jq`, `openssl`,
`python3`.

## Szybki start

```bash
# 1. Usługa + Mailpit + odbiornik webhooków (w tle; bez --background działa na pierwszym planie, Ctrl+C kończy)
tools/testlab/watchdog/run-watchdog-lab.sh --reset --background

# 2. Kanały powiadomień i odbiorcy (przez API usługi)
tools/testlab/watchdog/configure-lab.sh --test

# 3. Zmiany w katalogu i sprawdzenie, że zostały zgłoszone (ok. 2 minut)
tools/testlab/watchdog/lab-scenario.sh --verify

# 4. Sprzątanie
tools/testlab/watchdog/run-watchdog-lab.sh --stop
```

Po kroku 3 widać:

- zdarzenia: `curl -s -H 'X-AdMng-Watchdog-Api: 1' http://127.0.0.1:5089/api/v1/events | jq` albo zakładka
  **Watchdog** w AD_MNG (adres usługi `http://127.0.0.1:5089` jest dozwolony tylko dla pętli zwrotnej w trybie
  deweloperskim) – strona pojedynczego zdarzenia: link „Otwórz w przeglądarce” w e-mailu;
- e-maile (polskie tematy, np. „[AD Watchdog][WYSOKI] Dodano do grupy uprzywilejowanej: Zofia Żak…”, „Nowe konta: 4”,
  „Zablokowano konto: Paweł Lewandowski” dla helpdesku) w Mailpit: <http://127.0.0.1:8025>;
- wywołania webhooka i karty Teams w dzienniku odbiornika `~/.cache/admng/watchdog-lab/webhook-sink.log`
  (`signatureValid: true`) albo `curl -s http://127.0.0.1:8091/_sink/requests | jq`;
- raport dzienny o godzinie ustawionej przez `configure-lab.sh` (domyślnie 2 minuty po konfiguracji).

Dziennik usługi (`~/.cache/admng/watchdog-lab/watchdog.log` w trybie `--background`) nie zawiera wartości z katalogu
ani sekretów – tak samo jak w produkcji.

## Co robi scenariusz

1. Tworzy `OU=WatchdogDemo` z OU `Pracownicy`, `Przeniesieni`, `Komputery`, `Grupy`, grupy `Demo Tier0` (korzeń
   uprzywilejowany – `configure-lab.sh` dopisuje ją do `privilegedGroups.highSamAccountNames`), `Demo Operatorzy`
   (zagnieżdżona w `Demo Tier0`) i `Demo Projekt`.
2. Zakłada konto Zofii Żak w trzech krokach (wyłączone → hasło przez StartTLS → włączone) – w oknie
   `creationSettleWindow` (w laboratorium 30 s) to jedno zdarzenie **UserCreated** ze stanem końcowym; do tego trzy
   kolejne konta i komputer `WD-KRK-PC01` z obiektem podrzędnym.
3. Zmienia dział i stanowisko Łukasza Wiśniewskiego (**AttributesChanged** stara → nowa wartość), ustawia wygaśnięcie
   konta za 5 dni (**AccountExpirationChanged**, a przy najbliższym skanie **AccountExpiring**), dodaje go do grupy
   (**GroupMemberAdded**), dodaje Zofię do grupy zagnieżdżonej (**PrivilegeGranted** z członkostwem pośrednim),
   ustawia `pwdLastSet` 0 → -1 u Grażyny Dąbrowskiej (**PasswordAgeReset**) i wyłącza jej konto (**AccountDisabled**).
4. Pięć błędnych logowań Pawła Lewandowskiego (próg blokady 5) → **AccountLockedOut**, potem odblokowanie przez
   administratora → **AccountUnlocked**.
5. Przenosi Łukasza (**ObjectMoved**), zmienia nazwę OU `Pracownicy` → `Zespół Demo` (jedno **ObjectRenamed**, obiekty
   podrzędne nie są zgłaszane), usuwa Grażynę i komputer z obiektem podrzędnym.

Usługa czyta katalog jako `odczyt.test`, który nie widzi `CN=Deleted Objects`: usunięcia zgłasza dopiero nocne
uzgodnienie jako **ObjectDisappeared** („zniknął”), a stan zdrowia pokazuje ostrzeżenie `permission.deletedObjects`
– tak jak w domenie bez delegacji odczytu usuniętych obiektów (`docs/WATCHDOG.md`). Testy integracyjne sprawdzają
również wariant z kontem administratora (**ObjectDeleted**).

Konfiguracja laboratoryjna celowo różni się od domyślnej: tryb wdrożenia (rollout) jest zakończony, godziny pracy
obejmują cały tydzień bez świąt, okna agregacji mają 1 minutę, a limit wiadomości na regułę – 1000 na godzinę, żeby
kolejne przebiegi scenariusza nie trafiały wyłącznie do raportu dziennego.

## Kerberos (opcjonalnie)

Domyślnie laboratorium używa uwierzytelniania `Development`. Prawdziwy Kerberos (Negotiate) sprawdza test
`KerberosNegotiateTests` – na Linuksie tylko uwierzytelnienie (handler Negotiate nie daje tu SID-ów grup, więc
`/api/v1/me` odpowiada 403, nie 401); ścieżkę ról po SID-ach sprawdzają testy na Windows w CI.

```bash
eval "$(sudo -E tools/testlab/watchdog/krb5-lab.sh)"   # konto wd-krb-http, SPN HTTP/localhost, keytab, krb5.conf
ADMNG_TEST_KERBEROS=1 ADMNG_TEST_LDAP_SERVER=127.0.0.1 dotnet test tests/AdMng.Watchdog.IntegrationTests \
  --filter "FullyQualifiedName~KerberosNegotiateTests"
sudo -E tools/testlab/watchdog/krb5-lab.sh --cleanup
```

Keytab i `krb5.conf` leżą w `~/.cache/admng/watchdog-lab/kerberos` (uprawnienia 0700) i znikają z `--cleanup`.

## Konto usługi i keytab

Etap B2 wersji linuksowej (`docs/dev/watchdog-linux-design.md` 4.4–4.7, 5.1): narzędzie `admng-watchdog-keytab` ustawia
hasło konta komputera z hasła jednorazowego (`init`) i je rotuje (`rotate`), a usługa łączy się z DC przez LDAP 389 z
Kerberosem i szyfrowaniem SASL (SSF ≥ 56) jako to konto. Testy `KeytabLabTests` zakładają we własnym OU świeże konto
komputera (wyłączone, z hasłem jednorazowym, SPN `HTTP/` i `HOST/`, AES, `NOT_DELEGATED`), wykonują `init` i `rotate`
w procesie testu i sprawdzają, że otwarta sesja LDAP oraz klient Negotiate (akceptor z `KRB5_KTNAME`) działają dalej
bez restartu, a monitor konta usługi zgłasza kody zdrowia 4.6/4.7 dla wstrzykniętych błędów.

```bash
export ADMNG_TEST_LDAP_SERVER=127.0.0.1
eval "$(tools/testlab/watchdog/keytab-lab.sh env)"          # pusty bufor MEMORY: – tożsamość z keytabu testu
dotnet test tests/AdMng.Watchdog.IntegrationTests --filter "FullyQualifiedName~KeytabLabTests"

eval "$(tools/testlab/watchdog/keytab-lab.sh env --admin)"  # bilet Administratora – testy GssapiTests
dotnet test tests/AdMng.Directory.IntegrationTests --filter "FullyQualifiedName~GssapiTests"
tools/testlab/watchdog/keytab-lab.sh cleanup
```

Zmienne muszą być w środowisku procesu testów (libkrb5, libldap i Cyrus SASL czytają je natywnie; zmiana w procesie
.NET do nich nie dociera). Pliki leżą w `~/.cache/admng/watchdog-lab/keytab` (0700). Wymaga SPN-ów `ldap/<nazwa DC>`
i rozwiązywalnej nazwy DC – patrz [`../README.md`](../README.md) („Kerberos (SASL GSSAPI) przez LDAP”). Samba zwalnia
konto komputera z minimalnego wieku hasła, więc ścieżki `otpActive` nie da się tu wywołać (sprawdzają ją testy
jednostkowe); jeden DC – opóźnienie replikacji test symuluje drugim, nieosiągalnym KDC w `krb5.conf`.

## Testy integracyjne

```bash
tools/testlab/watchdog/start-mailpit.sh
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
export ADMNG_TEST_LDAP_READONLY_USER='odczyt.test@contoso.test' ADMNG_TEST_LDAP_READONLY_PASSWORD='Passw0rd!Lab2026'
export ADMNG_TEST_SMTP_HOST=127.0.0.1 ADMNG_TEST_SMTP_PORT=1025 ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025
dotnet test tests/AdMng.Watchdog.IntegrationTests
tools/testlab/watchdog/stop-mailpit.sh
```

Testy tworzą własne OU `OU=WatchdogIT-<identyfikator przebiegu>` i patrzą wyłącznie na zdarzenia obiektów z tego OU –
laboratorium bywa współdzielone. Opis OU wskazuje proces testów; na starcie usuwane są OU przebiegów, które się
skończyły (proces na tym hoście już nie działa albo OU jest starsze niż 2 godziny), a OU trwających przebiegów
zostają. Usuwanie odbywa się po stronie klienta, obiekt po obiekcie (zob. „Sprzątanie po testach” w
[`tools/testlab/README.md`](../README.md)). Bez
`ADMNG_TEST_LDAP_SERVER` są pomijane; test end-to-end bez `ADMNG_TEST_SMTP_HOST` uruchamia własny serwer SMTP w procesie
zamiast Mailpit.

## Sprzątanie

```bash
tools/testlab/watchdog/lab-scenario.sh --cleanup        # OU=WatchdogDemo
tools/testlab/watchdog/run-watchdog-lab.sh --stop       # usługa w tle, odbiornik, Mailpit (wiadomości znikają)
tools/testlab/watchdog/run-watchdog-lab.sh --reset ...  # przy następnym starcie: nowa baza i nowy baseline
rm -rf ~/.local/share/admng-watchdog-lab ~/.cache/admng # wszystko, łącznie z pobranym Mailpit
```
