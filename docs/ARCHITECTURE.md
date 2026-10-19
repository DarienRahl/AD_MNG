# Architektura AD Manager

Dokument opisuje budowę aplikacji i zasady, które ją kształtują. Decyzje z uzasadnieniem: [`docs/adr`](adr/).

## Założenia

| Wymaganie | Jak je realizujemy |
|---|---|
| Wygląd i nawyki jak w `dsa.msc` | Drzewo domeny/OU po lewej, lista obiektów po prawej, karty właściwości o tych samych nazwach i kolejności, skróty F5 / Del / F2 / Alt+Enter / Ctrl+F |
| Nowoczesne standardy | WPF .NET 10 + motyw Fluent (Windows 11, jasny/ciemny), MVVM, DI, okna niemodalne, panel szczegółów, paleta poleceń (Ctrl+K), pasek adresu z DN, PL/EN przełączane w locie, dostępność (UI Automation, klawiatura, wysoki kontrast) |
| Płynniej niż przystawka | Asynchroniczne LDAP, strumieniowanie wyników stronami, pobieranie tylko widocznych kolumn, brak limitu 2000 obiektów, wyszukiwanie w trakcie pisania (ANR), wirtualizacja list, odświeżanie tylko zmienionych obiektów |
| Brak lokalnych danych | Każde zapytanie idzie do DC; dane tylko w pamięci, dopóki są wyświetlane ([ADR-0003](adr/0003-brak-lokalnych-danych.md)) |
| Różne poziomy uprawnień | Logowanie bieżącym kontem Windows lub innymi poświadczeniami; UI wyłącza pola i akcje niedozwolone dla operatora ([ADR-0006](adr/0006-ui-swiadome-uprawnien.md)) |
| Edycja zbiorcza | Jeden potok zmian: plan → podgląd różnic → walidacja → wykonanie → raport → cofnięcie ([ADR-0004](adr/0004-jeden-potok-zmian.md)) |
| Specyficzne podziały AD | Konfigurowalne „Widoki”: przypięte OU, zapisane zapytania LDAP, gotowe szybkie filtry; eksport/import widoków dla zespołu |

## Warstwy

```
┌───────────────────────────────────────────────────────────────────────────┐
│ AdMng.App (net10.0-windows, WPF)                                          │
│  Widoki XAML, motyw Fluent, Generic Host + DI, usługi Windows (okna,      │
│  schowek, pliki), test dymny --smoke-test                                 │
├───────────────────────────────────────────────────────────────────────────┤
│ AdMng.Presentation (net10.0) – bez typów WPF, testowalne na każdym OS     │
│  ViewModele: logowanie, powłoka, drzewo, listy, wyszukiwanie, widoki,     │
│  karty właściwości, edytor zbiorczy, import CSV, dziennik sesji;          │
│  lokalizacja (resx en/pl), ustawienia (JSON bez haseł)                    │
├──────────────────────────────────────┬────────────────────────────────────┤
│ AdMng.Directory (net10.0)            │ AdMng.Core (net10.0)               │
│  System.DirectoryServices.Protocols  │  DN (RFC 4514), filtry (RFC 4515), │
│  LdapDirectoryConnector/Session,     │  flagi i konwersje AD, model       │
│  pula połączeń do 1 DC, schemat,     │  obiektu i schematu, kontrakty     │
│  stronicowanie, VLV, ranged          │  sesji katalogu, silnik zmian      │
│  retrieval, uprawnienia efektywne,   │  (planer, wykonawca, rollback),    │
│  zapisy, reset hasła, kosz AD,       │  szablony wartości, CSV, eksport   │
│  powiadomienia o zmianach            │  PowerShell, dziennik sesji        │
└──────────────────────────────────────┴────────────────────────────────────┘
```

Zależności idą wyłącznie w dół. `AdMng.Core` nie zależy od żadnej biblioteki LDAP, więc cała logika domenowa jest
testowana jednostkowo z użyciem `FakeDirectorySession` (katalog w pamięci, tylko w testach).

## Sesja katalogu

- `IDirectoryConnector.ConnectAsync` lokalizuje DC (DsGetDcName z uwzględnieniem lokacji albo DC wskazany ręcznie),
  wiąże się (Negotiate/Kerberos z podpisywaniem i szyfrowaniem lub LDAPS), czyta RootDSE, tożsamość operatora,
  nazwę NetBIOS, obecność Kosza AD, schematu Exchange i LAPS oraz schemat (atrybuty, klasy, nazwy wyświetlane z
  display specifiers 415/409) – równolegle, cel < 1 s.
- `IDirectorySession` jest przypięta do jednego DC ([ADR-0005](adr/0005-sesja-przypieta-do-dc.md)) i ma pulę kilku
  połączeń. Wszystkie wywołania są asynchroniczne z `CancellationToken`.
- „Zmień kontroler domeny…” nie przełącza istniejącej sesji: `IDirectoryConnector.ConnectToServerAsync` otwiera NOWĄ
  sesję na wybranym DC z opcjami i poświadczeniem bieżącej (hasło zostaje w pamięci warstwy `AdMng.Directory`),
  `ISessionManager.SwitchDomainControllerAsync` sprawdza ją (ta sama domena, inny DC) i dopiero wtedy zastępuje
  bieżącą i zamyka poprzednią. Błąd lub anulowanie zostawia bieżącą sesję bez zmian. Okna otwarte w poprzedniej sesji
  zostają przy niej (nie czytają i nie zapisują) i informują, że należą do poprzedniego DC; karty głównego okna
  przeładowują się z nowego DC, a dziennik sesji zapisuje zmianę.
- Wyszukiwanie (`SearchAsync`) zwraca `IAsyncEnumerable` – pierwsza strona trafia na ekran, zanim serwer zwróci
  kolejne. Dla ogromnych kontenerów dostępny jest VLV (`GetVirtualListPageAsync`) – okno przewijania sortowane po
  stronie serwera.
- Duże atrybuty wielowartościowe (członkowie grup > 1500) czytane są przez ranged retrieval.
- Wartości są dekodowane wg schematu (tekst vs binaria: GUID, SID, deskryptor bezpieczeństwa, zdjęcie).
- Błędy LDAP i kody rozszerzone AD (np. `0000052D` – polityka haseł, `data 775` – konto zablokowane) są mapowane na
  `DirectoryErrorKind` i zrozumiałe komunikaty PL/EN.

## Potok zmian (edycja pojedyncza i zbiorcza)

```
 zaznaczenie / wynik zapytania / CSV
            │
            ▼
   BatchOperation[]  ──►  IChangePlanner.PlanAsync ──► ChangePlan
   (ustaw, wyczyść,        • świeży odczyt z DC            ObjectPlan[]
    dodaj/usuń wartości,   • szablony {givenName|lower}      PlannedChange[] (stara → nowa)
    znajdź/zamień, flagi,  • pominięcie zmian bez efektu     ValidationIssue[]
    odblokuj, hasło,       • schemat, uprawnienia,
    przenieś, zmień nazwę,   unikalność, obiekty chronione
    grupy, usuń)                   │
                                   ▼
                        podgląd różnic + potwierdzenie
                                   │
                                   ▼
                     IBatchExecutor.ExecuteAsync ──► BatchResult ──► eksport CSV / PowerShell
                     • równoległość ograniczona          │
                     • optymistyczna współbieżność       ├──► dziennik sesji (IAuditLog, bez haseł)
                     • ponawianie błędów przejściowych   │
                     • anulowanie                        └──► IRollbackPlanner ──► plan cofnięcia
```

Kolejność kroków na obiekcie: reset hasła → modyfikacja atrybutów → członkostwo w grupach → zmiana nazwy →
przeniesienie (usuwanie zawsze osobno). Zmiana jednej wartości w oknie właściwości korzysta z tego samego potoku.
Wartości w planie są tekstem; atrybutów binarnych nie edytuje się zbiorczo – wyjątkiem jest `logonHours` (okno „Godziny
logowania”), który w planie, dzienniku i cofnięciu jest zapisem szesnastkowym, a do DC trafia jako 21 bajtów
(`PipelineValues`).

## Interfejs użytkownika

- **Okno główne:** pasek menu i narzędzi (jak ADUC), pasek adresu (DN lub ścieżka `contoso.test/Firma/IT`),
  globalne wyszukiwanie (Ctrl+K / Ctrl+E), drzewo z sekcją „Widoki”, karty z listami, panel szczegółów, pasek stanu
  (DC, tożsamość – z wyróżnieniem kont uprzywilejowanych, liczba obiektów, czas zapytania, operacje w tle).
- **Listy:** strumieniowo ładowane, wirtualizowane, kolumny wybierane z atrybutów schematu, szybki filtr tekstowy,
  filtry-chipy wykonywane po stronie serwera (zablokowane, wyłączone, nieaktywne N dni, …), sortowanie, eksport CSV.
  Kontener, który przekroczy 5000 obiektów, przechodzi w listę wirtualną (`VirtualRowList`): VLV sortowany na serwerze,
  strony czytane w trakcie przewijania, ograniczona pamięć podręczna stron, wyszukiwanie po wpisaniu przez wartość
  `seek` VLV, odświeżanie widocznych stron po zmianach ([dev/phase1-navigation.md](dev/phase1-navigation.md)).
- **Drzewo:** kontenery dobrze znane rozpoznawane po GUID z `wellKnownObjects`/`otherWellKnownObjects` głowy domeny
  (znacznik i opis roli); polecenia „Nowy” bez kontekstu kontenera tworzą obiekty w domyślnym kontenerze Users/Computers.
- **Właściwości:** okna niemodalne, karty jak w ADUC + Edytor atrybutów; pola niedostępne dla operatora tylko do odczytu.
- **Edytor zbiorczy:** kreator operacje → podgląd → wykonanie → wyniki → cofnięcie; import CSV z mapowaniem kolumn.
- **Lokalizacja:** zasoby `Strings.resx` (en) i `Strings.pl.resx`; nazwy atrybutów z display specifiers AD (jak w
  polskim ADUC), przełączanie języka bez restartu.

## Bezpieczeństwo

- Hasła logowania i resetowane są tylko w pamięci; nie trafiają do ustawień, logów ani `ToString()`.
- Domyślnie Kerberos z podpisywaniem i szyfrowaniem (zgodne z wymuszaniem podpisywania LDAP w Windows Server 2025);
  proste wiązanie (simple bind) tylko po TLS lub w laboratorium za jawną zgodą.
- Filtry i DN budowane wyłącznie przez `LdapFilter` / `DistinguishedName` (escaping RFC 4515/4514).
- Eksport CSV chroniony przed wstrzykiwaniem formuł Excela; eksport haseł wymaga osobnego potwierdzenia.
- Ostrzeżenia dla obiektów chronionych (adminCount=1, obiekty krytyczne systemu, konta wbudowane) i potwierdzenie
  przez wpisanie liczby obiektów dla dużych/ryzykownych operacji.
- Aplikacja nie jest granicą bezpieczeństwa – o uprawnieniach decydują ACL w AD; UI jedynie je odzwierciedla.

## AdMng Watchdog – usługa monitorująca

AD_MNG jest konsolą bez lokalnych danych, więc wykrywanie zmian w katalogu (kto, co, kiedy, skąd) działa w osobnym
procesie: usłudze Windows **AdMng Watchdog** (`AdMng.Watchdog.exe`, ASP.NET Core + Worker, 24/7, konto gMSA tylko do
odczytu). Zakładka **Watchdog** w AD_MNG jest jej klientem. Wdrożenie i obsługa: [WATCHDOG.md](WATCHDOG.md); wiążąca
specyfikacja implementacji: [dev/watchdog-spec.md](dev/watchdog-spec.md).

```
              ┌──────────────── serwer w domenie (Tier 0), gMSA CONTOSO\svc-admngwd$ ──────────────┐
              │ AdMng.Watchdog.exe (usługa "AdMngWatchdog"; tryb konsolowy w laboratorium)          │
  DC (PDC) ◄──┤ ChangeDetectionEngine: RootDSE, uSNChanged, Extended DN, metadane replikacji,       │
  LDAP 389    │   kosz/Deleted Objects, partycje Configuration i Schema ──┐                         │
  podpis+szyfr│ ScheduledScanJob: wygasanie kont i haseł, blokady,        ├──► SQLite (WAL) w       │
  wszystkie DC│   samokontrola uprawnień, serie (masowe blokady/usunięcia) │    <DataDir>\watchdog.db│
  ◄── RPC ────┤ AttributionEngine: dzienniki Security DC (47xx, 5136…)  ──┘    zdarzenia, migawki,  │
  (Event Log  │ RuleEvaluationJob → NotificationDispatchJob → OutboxSenderJob   kolejka, sekrety (DP)│
   Readers)   │   ──► SMTP (MailKit) / Teams Workflows / webhook (Standard Webhooks)               │
              │ DigestJob, HeartbeatJob, RetentionJob                                               │
              │ API HTTPS (Kestrel lub HTTP.sys), Kerberos (Negotiate), role wg SID grup AD         │
              └──────────────────────────────────▲──────────────────────────────────────────────────┘
                                                 │ HTTPS 8443, Kerberos
                               AD_MNG – zakładka Watchdog (IWatchdogClient)
```

| Projekt | Rola |
|---|---|
| `AdMng.Watchdog.Contracts` | model zdarzenia, DTO API, konfiguracja, trasy, JSON (bez zależności) |
| `AdMng.Watchdog.Core` | cała logika (wykrywanie, migawki, atrybucja, reguły, planowanie powiadomień, formatery e-mail/Teams/webhook, raport dzienny) – testowana na Linuksie z `FakeDirectorySession` i zarejestrowanymi zdarzeniami; rejestracja `AddWatchdogCore()` |
| `AdMng.Watchdog.Service` | host: API, autoryzacja, magazyn SQLite, Data Protection, transporty (MailKit, `HttpClient`), czytnik dzienników Windows, hosty zadań, skrypty instalacyjne |
| `AdMng.Watchdog.Client` | klient HTTPS/Kerberos API (`IWatchdogClient`) używany przez AD_MNG |
| `AdMng.Presentation/Watchdog`, `AdMng.App/Views/Watchdog` | ViewModele i widoki zakładki Watchdog |

Kierunek zależności: Contracts ← Core ← Service; Contracts ← Client ← Presentation/App. `AdMng.Watchdog.Core` korzysta
z `AdMng.Core` (DN, filtry, sesja katalogu), usługa – z `AdMng.Directory` (LDAP).

Zasady, które odróżniają usługę od reszty rozwiązania:

- **Dane tylko w folderze usługi** ([ADR-0008](adr/0008-watchdog-dane-w-osobnym-folderze.md), częściowo zmienia
  ADR-0003): migawki obserwowanych atrybutów, zdarzenia, rekordy dzienników DC i kolejka wysyłek leżą w jednej bazie
  SQLite (`secure_delete`, retencja, kopie `VACUUM INTO`) w folderze z wymuszoną listą ACL (usługa nie startuje przy
  szerszych uprawnieniach). AD_MNG, Presentation, Core i klient niczego z usługi nie zapisują.
- **Sekrety kanałów** (hasło SMTP, sekret OAuth, adresy Teams/webhooków, klucze podpisu) tylko w `ISecretStore` –
  ASP.NET Core Data Protection z kluczami chronionymi DPAPI-NG dla gMSA – i **powiązane z miejscem docelowym**: zmiana
  serwera lub adresu bez ponownego wpisania sekretu jest odrzucana. API nigdy ich nie zwraca.
- **Wykrywanie** odpytuje `uSNChanged` jednego DC (domyślnie PDC) z migawką kluczowaną `objectGUID`; atrybuty DN są
  czytane z kontrolką Extended DN (porównanie po GUID – zmiana nazwy członka nie jest zmianą grupy); znacznik postępu
  (watermark) jest zapamiętywany per DC, pełna resynchronizacja tylko dla nowego DC, przywrócenia DC lub cofnięcia USN;
  codzienne uzgodnienie wykrywa obiekty, które „zniknęły”.
- **Atrybucja** (kto i skąd) jest osobnym, asynchronicznym wzbogaceniem z dzienników Security kontrolerów; metadane
  replikacji wskazują DC i czas. Gdy nie da się ustalić wykonawcy, zdarzenie pokazuje „nieustalony” z kodem przyczyny –
  nigdy zgadywanie.
- **Dyscyplina szumu:** tryb wdrożenia przez pierwsze 7 dni, okno ustalania przy zakładaniu kont (wieloetapowe
  założenie = jedno zdarzenie), ważność zależna od wykonawcy, tłumienie powtórzeń, agregacja, godziny pracy wg
  kalendarza polskich świąt.
- **API:** HTTPS (TLS 1.2/1.3), wyłącznie Kerberos (NTLM odrzucany – Kestrel w .NET 10 nie ma channel binding),
  role Viewer/Admin wg SID-ów grup z lokalnego pliku startowego, którego konto usługi nie może zmienić; ochrona CSRF
  nagłówkiem `X-AdMng-Client`; zmiany konfiguracji same są zdarzeniem (Tier 0 nie da się wyciszyć przez API).
- **Dzienniki diagnostyczne bez wartości z katalogu** – wyjątki tylko przez `SafeExceptionLog` (typ, kody, stos –
  nigdy `Message`), filtr redakcji na dostawcach logów.

## Testy i jakość

| Poziom | Gdzie | Jak |
|---|---|---|
| Jednostkowe | `AdMng.Core.Tests`, `AdMng.Presentation.Tests` | xUnit v3, `FakeDirectorySession` |
| Integracyjne | `AdMng.Directory.IntegrationTests` | Samba AD DC z `tools/testlab` (lokalnie i w CI na Ubuntu) |
| Dymne UI | CI Windows | `AdMng.exe --smoke-test` tworzy wszystkie widoki z danymi testowymi |
| Watchdog – silnik | `AdMng.Watchdog.Core.Tests` | `FakeDirectorySession` z emulacją USN, metadanych i kontrolera; zarejestrowane zdarzenia 47xx/5136; fałszywy zegar |
| Watchdog – usługa | `AdMng.Watchdog.Service.Tests` | host w procesie (`WebApplicationFactory`), SQLite w folderze tymczasowym, odbiorniki SMTP/HTTP, testy higieny dzienników; testy tylko-Windows (dzienniki Security, DPAPI-NG) w CI Windows |
| Watchdog – laboratorium | `AdMng.Watchdog.IntegrationTests` | prawdziwe silniki z `AddWatchdogCore()` przeciw Samba AD DC (konto tylko do odczytu), scenariusze z 22.4 specyfikacji w jednorazowym OU, usługa end-to-end z Mailpit i podpisanym webhookiem, opcjonalnie prawdziwy Kerberos |
| Watchdog – narzędzia | `tools/testlab/watchdog` | usługa w trybie laboratoryjnym, Mailpit, odbiornik webhooków, scenariusz `lab-scenario.sh --verify` (także w CI) |
| Watchdog – dymny | CI Windows | `AdMng.Watchdog.exe --smoke-test` opublikowanej usługi (migracje, `/healthz`, `/api/v1/status`, wszystkie formatery); artefakt `AdMng-Watchdog-win-x64` |
| Statyczne | build | ostrzeżenia = błędy, analiza stylu kodu |
