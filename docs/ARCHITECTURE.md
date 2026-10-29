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

**Wiadomości do użytkowników (U1, U1c).** `AdMng.Mail` (net10.0, MailKit) wysyła wiadomości bezpośrednio przez SMTP i
buduje treść MIME (`UserMessageMimeBody`: `multipart/alternative` tekstu i HTML, a z obrazami osadzonymi
`multipart/related` z `Content-ID`) – tej samej klasy używa usługa Watchdog (`AdMng.Watchdog.Service` odwołuje się do
`AdMng.Mail`), więc oba sposoby wysyłki dają tę samą strukturę wiadomości. `AdMng.Core` korzysta z **AngleSharp**
(parser HTML5, czysto zarządzany, działa na Linuksie) w sanitizerze HTML wiadomości (`Messaging/Html`), wspólnym dla AD
Managera i walidatora usługi; CSS (parser i wpisywanie reguł w `style`) to własny, mały kod. `AdMng.App` jako jedyny
używa **WebView2** (`Microsoft.Web.WebView2`, środowisko Evergreen) do podglądu sformatowanej wiadomości – bez skryptów,
nawigacji i sieci, z treścią i obrazami z pamięci, a bez środowiska WebView2 pokazuje źródło HTML
([docs/dev/u1c-u1d-rich-html-and-salutations.md](dev/u1c-u1d-rich-html-and-salutations.md) rozdz. 4 i 10). Od U5
trzecim sposobem wysyłki jest klasyczny Outlook operatora (`OutlookUserMessageTransport` w `AdMng.Outlook`, rozdział
[Korespondencja (U5)](#korespondencja-u5)).

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
  przeładowują się z nowego DC, a dziennik sesji zapisuje zmianę. Lista DC łączy odczyt z katalogu bieżącej sesji
  (`server`/`nTDSDSA` w `CN=Sites`, `fSMORoleOwner`) z lokatorem (DsGetDcName/DNS SRV, lokacja klienta)
  ([dev/phase1-changedc.md](dev/phase1-changedc.md)).
- Kontenery dobrze znane (`wellKnownObjects`/`otherWellKnownObjects` głowy domeny) czyta
  `ISessionManager.GetWellKnownObjectsAsync` raz na sesję i trzyma w pamięci (`WellKnownObjectMap`); nowa sesja – także
  po zmianie DC – czyta je ponownie. Nieczytelny atrybut daje pustą mapę: bez znaczników i bez przekierowania, nazwy
  kontenerów nie są zgadywane.
- Wyszukiwanie (`SearchAsync`) zwraca `IAsyncEnumerable` – pierwsza strona trafia na ekran, zanim serwer zwróci
  kolejne. Dla ogromnych kontenerów dostępny jest VLV (`GetVirtualListPageAsync`) – okno przewijania sortowane po
  stronie serwera.
- Duże atrybuty wielowartościowe (członkowie grup > 1500) czytane są przez ranged retrieval.
- Wartości są dekodowane wg schematu (tekst vs binaria: GUID, SID, deskryptor bezpieczeństwa, zdjęcie).
- Błędy LDAP i kody rozszerzone AD (np. `0000052D` – polityka haseł, `data 775` – konto zablokowane) są mapowane na
  `DirectoryErrorKind` i zrozumiałe komunikaty PL/EN.

### Wiele połączeń: okno główne na połączenie

Kilka domen (albo ta sama domena z innym kontem) pracuje jednocześnie w jednym procesie, każda w osobnym oknie
głównym ([dev/multi-window.md](dev/multi-window.md)):

- **Zakres DI na okno.** `ConnectionWindowManager` (App, singleton) tworzy dla każdego okna połączenia
  `AsyncServiceScope`. W zakresie żyją: `ISessionManager` (jedna sesja; wiązanie, które skończy się po zamknięciu okna,
  jest od razu zamykane), `ConnectionContext` (id, profil, kolor, tożsamość, `Lifetime`), magistrala zmian, dziennik
  audytu, wykonawca, centrum aktywności, koordynator, `DialogServices`, powłoka (`ShellViewModel`), usługi zakładki
  Watchdog, a po stronie WPF `ConnectionNavigator`, `WindowService`, `DialogService`, `ToolWindows`, powiadomienia,
  schowek i okna plików. Wspólne (singletony): ustawienia, widoki, zapisane filtry (`FilterLibrary`), lokalizacja,
  motyw, konektor, silniki, połączenie z usługą Watchdog, pamięć hasła SMTP i dzienny limit wiadomości,
  `ConnectionRegistry`, a także źródła uprawnień przeglądu kont (delegowania w ACL, uprawnienia lokalne z GPO) i czytnik
  SYSVOL – bezstanowe: każdy przebieg dostaje sesję okna, które go uruchomiło, i czyta tylko przez nią (SYSVOL z jej
  kontrolera i z jej poświadczeniem). Usług okna nigdy nie pobiera się z dostawcy głównego (`ValidateScopes`; testy
  rejestracji sprawdzają czas życia każdej z listy).
- **Okna potomne** powstają z zakresu okna, więc korzystają z jego sesji, dialogów i schowka; każde ma dołączoną
  właściwość `ConnectionChrome.ConnectionId`, właściciela z tego samego połączenia, przyrostek domeny w tytule oraz
  pasek i nagłówek w kolorze połączenia (migawka tożsamości z chwili otwarcia). Wyszukiwanie otwartych okien („jedno na
  połączenie”) widzi tylko okna tego połączenia; przeciągnięcie obiektów z innego połączenia jest odrzucane.
- **Zapis tylko w sesji planu.** `ChangeCoordinator.ApplyAsync` wykonuje plan w sesji, w której go zaplanował; jeśli
  sesja się zmieniła (Rozłącz, ponowne połączenie), nic nie jest zapisywane. Potwierdzenia nazywają domenę sesji, która
  wykona operację.
- **Zamknięcie okna:** koniec `Lifetime`, zamknięte dialogi i powiadomienia okna (pozostała praca w tle niczego nie
  pokaże), anulowanie jego operacji i zamknięcie sesji poza wątkiem UI (z limitem czasu), potem zwolnienie zakresu;
  ostatnie okno kończy program, `App.OnExit` zamyka sesje okien, które jeszcze zostały.
- **Ustawienia** są wspólne; `JsonSettingsStore` zmienia kopię (copy-on-write) pod blokadą i zapisuje migawki z numerem
  generacji, więc okna nie nadpisują sobie zmian, a czytający nigdy nie widzi zmienianej listy.
- **Porównanie obiektów między połączeniami** jest tylko do odczytu. Okno porównania (zakres okna A) dostaje od
  `ConnectionContext.Pin()` okna B (`IConnectionHandle` z `ConnectionRegistry`) `ConnectionReadContext`: czytnik,
  formatowanie, schemat, tożsamość i politykę **jednej** sesji B. Czytnik to `IDirectoryReader`
  (`IDirectorySession : IDirectoryReader`) za `ReadOnlyDirectoryReader`, którego nie da się rzutować z powrotem na sesję,
  z limitem 2 jednoczesnych żądań; kontekst kończy się przy zmianie sesji B albo zamknięciu okna B (odczyty rzucają
  `ObjectDisposedException`, strona B jest oznaczana jako zakończona). Strona A czyta tak samo przez przypiętą sesję
  okna A (reguła 4: każda strona przez kontroler swojego okna, bez nowego wiązania). Czysty diff (`ObjectDiffer`,
  `ObjectSnapshot`, `MembershipComparer`, `GroupKey`, `ComparisonAttributes`) jest w AdMng.Core, dopasowanie i odczyt
  (`ObjectMatcher`, `ComparisonReader`) oraz `ObjectComparisonViewModel` w Presentation. Wyniki żyją tylko w pamięci okna;
  CSV powstaje wyłącznie na żądanie operatora.

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

**SYSVOL w potoku zmian (zasada 5 obejmuje także pliki GPO).** Od K2 plan może zawierać kroki SYSVOL obok kroków LDAP:
`CreateGpoFolder`, `WriteGpoFile`, `DeleteGpoFile`, `SyncGpoFolderSecurity`, `SetGpoFolderSecurity`, `SetGpoVersion`,
`DeleteGpoFolder` (`PlannedChange.Sysvol`, `.GpoVersion`), a plan GPO jest sekwencyjny (`ChangePlan.Sequential`) i niesie
semantyczne odpowiedniki PowerShell (`ObjectPlan.PowerShell`: `New-GPO`, `Set-GPLink`, `Restore-GPO` …). Wykonawca
otwiera jedno połączenie SYSVOL na przebieg – na kontrolerze sesji, poświadczeniami okna (`ISysvolFileWriter`, zasady 2
i 4) – a pliki jednego GPO zapisuje transakcją `GpoTransaction`: pliki tymczasowe obok docelowych, sprawdzenie,
podmiana, na końcu wersja w `GPT.INI` i porównanie-z-zamianą `versionNumber` w AD; błąd lub zmiana GPO przez kogoś
innego przywraca pliki z pamięci. Wersje tylko rosną (także przy cofaniu), plan cofnięcia odwraca kolejność obiektów.
Dziennik audytu zapisuje ścieżki, rozmiary i SHA-256 plików – nigdy treść. Nic poza potokiem nie pisze do SYSVOL.

### Zasady grupy (K2)

`AdMng.Core/GroupPolicy`: `GroupPolicyReader` (GPO, połączenia, dziedziczenie, filtry WMI, lokacje, emulator PDC,
foldery GPO wczytywane do pamięci w limitach), `GroupPolicyPlanner` (plan każdej operacji – tworzenie, kopia, zmiana
nazwy, usunięcie, `gPLink`/`gPOptions`, stan, filtry WMI, filtrowanie zabezpieczeń i delegowanie przez `ModifySecurity`
K1 z poziomami GPMC, wyrównanie wersji, uprawnienia folderu, przywracanie i import), `GpmcBackupFormat` (kopie w
układzie GPMC + `admng.json`, odczyt jak niezaufanych danych), `GroupPolicyComparer` oraz parsery treści GPO wspólne z
Watchdogiem (`GroupPolicy/Content`). `AdMng.Presentation/GroupPolicy`: usługi i akcje na okno połączenia (Scoped),
cztery karty (`IShellDocument`), węzły drzewa, modele okien i okno podglądu planu (`ChangePlanPreviewViewModel`).
`AdMng.App/GroupPolicy`: widoki WPF, okna i `WindowsSysvolFileWriter` (`AdMng.Directory`). Kopie zapasowe i eksport
porównania trafiają wyłącznie do miejsc wskazanych przez operatora (zasada 1). Opis dla użytkownika:
[ZASADY-GRUPY.md](ZASADY-GRUPY.md); specyfikacja: [dev/k-computers-and-gpo.md](dev/k-computers-and-gpo.md).

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
  „Godziny logowania…” (karta Konto i operacja edytora zbiorczego) to siatka 7 × 24 w czasie lokalnym operatora:
  `LogonHours` (Core) przelicza 168 bitów UTC na siatkę lokalną z bieżącym przesunięciem strefy, jak ADUC
  ([dev/phase1-logonhours.md](dev/phase1-logonhours.md)).
- **Kopiowanie:** przyciski „Kopiuj” w panelu szczegółów i na kartach właściwości, Ctrl+C wierszy listy (widoczne
  kolumny, tabulatory, formatowane poza wątkiem UI), „Kopiuj jako…” dla zaznaczenia w kolejności listy – formaty inne
  niż nazwa, DN i ścieżka czytają obiekty na żywo z DC sesji paczkami (`DirectoryLookup.ReadManyAsync`), odczyt jest
  anulowany przy zmianie sesji. Do schowka pisze wyłącznie `IClipboardService`; nic nie trafia na dysk
  ([dev/phase1-copy.md](dev/phase1-copy.md)).
- **Edytor zbiorczy:** kreator operacje → podgląd → wykonanie → wyniki → cofnięcie; import CSV z mapowaniem kolumn.
- **Lokalizacja:** zasoby `Strings.resx` (en) i `Strings.pl.resx`; nazwy atrybutów z display specifiers AD (jak w
  polskim ADUC), przełączanie języka bez restartu.

## Komputery (K1)

Projekt: [dev/k-computers-and-gpo.md](dev/k-computers-and-gpo.md), rozdz. 3.1 i 4; opis dla administratora:
[KOMPUTERY.md](KOMPUTERY.md).

- **Core/Computers** – czysta logika bez LDAP: `ComputerFacts` (fakty z jednego wyniku wyszukiwania), `ComputerClassifier`
  (stacja / serwer / DC / RODC z `userAccountControl`, grupy podstawowej i `operatingSystem`), `WindowsReleaseCatalog`
  (kompilacja + klasa wydania → data końca wsparcia, nadpisania z ustawień), `ComputerInventory.Evaluate` (stan
  komputera dla raportów), `ComputerReports` (filtry LDAP raportów przez `LdapFilter`), `PlacementRules`, `SubnetMap` i
  `SiteTopology` (lokacje i podsieci z konfiguracji DC sesji), `ComputerJoinRights` (cztery ACE prawa dołączenia).
- **Zapisy uprawnień (K1.1)** – akcja `ChangeAction.ModifySecurity` w potoku zmian: `AclChange` to delta ACE, którą
  wykonawca nakłada na świeżo odczytany DACL (`DaclEditor`) i zapisuje z kontrolką SD flags `DACL_SECURITY_INFORMATION`
  (`ModifyOptions.SecurityDescriptorFlags`) – właściciel, grupa i SACL nie są zapisywane. Cofnięcie to delta odwrotna,
  eksport PowerShell – `Get-Acl`/`Set-Acl` na dysku `AD:` przypiętym do DC planu. Wynik kroku
  (`ChangeResult.AppliedAcl`) to ta część delty, która naprawdę zmieniła DACL – z niej korzystają dziennik audytu i
  cofnięcie, więc ACE dodany w międzyczasie przez kogoś innego nie jest ani przypisywany planowi, ani usuwany. Z tej akcji
  korzysta też K2 (filtrowanie zabezpieczeń, delegowanie GPO); kopia GPO z uprawnieniami i przywrócenie usuniętego GPO
  używają delty „dokładnej” (`AclChange.ReplacesExplicit`), która usuwa też jawne ACE, których nie ma w źródle.
- **Cykl życia** – przygotowanie konta w kreatorze „Nowy → Komputer” (krok tworzenia + `ModifySecurity` w jednym planie),
  `ResetComputerAccountOperation` (losowe 120 znaków, tylko w pamięci planu) i reguły `PlanValidation` dla kont DC.
- **AdMng.Remote** (osobny projekt, `net10.0`, wywołania Windows chronione) – `CimRemoteComputerClient`
  (`Microsoft.Management.Infrastructure`: CIM przez WS-Management, tylko Kerberos, poświadczenia okna z
  `SessionLogons` w AdMng.Directory jako `SecureString` usuwany z sesją CIM), `WindowsLocalToolLauncher` (mstsc bez
  poświadczeń; `compmgmt.msc`/`eventvwr` przez `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)`), DNS i ping. Odwołuje
  się do niego tylko `AdMng.App`, więc pakiet CIM nie trafia do usługi Watchdog na Linuksie.
- **Presentation/Computers** – `ComputerReviewViewModel` (strumień jak w przeglądzie kont, kafelki, kolumny raportu,
  odczyt na żądanie z wszystkich DC i lokacji), `RemoteComputerViewModel` i `RestartComputerViewModel`, `ComputerActions`
  (cel zdalny, narzędzia, ping, reset konta), karta „Stan” właściwości i karta „Komputery” ustawień.
  `ComputerViewModelFactory` i `ComputerActions` są **Scoped** (okno połączenia); klient CIM, narzędzia, ping i resolver
  DNS są singletonami bez stanu – każde wywołanie dostaje sesję (poświadczenie) okna, które pyta.
- **Dane** – wyniki przeglądu, adresy, lokacje i wyniki zdalnych akcji są tylko w pamięci okna; dziennik sesji zapisuje
  akcje zdalne bez treści komunikatu restartu, a logi diagnostyczne nie zawierają nazw komputerów.

## Korespondencja (U5)

Projekt: [dev/u5-outlook-correspondence.md](dev/u5-outlook-correspondence.md); opis dla użytkownika:
[KORESPONDENCJA.md](KORESPONDENCJA.md). Okno *Korespondencja* to program *Korespondencja WPiA* (masowa wysyłka przez
klasycznego Outlooka) przeniesiony do AD Managera, z odbiorcami z AD i zapytaniami do właścicieli skrzynek. Niczego nie
zapisuje w katalogu (zasada 5 nie ma zastosowania – wiadomość nie jest zmianą AD); wszystkie odczyty idą przez sesję
okna połączenia (zasada 4).

```text
AdMng.App/Correspondence ── okno WPF, edytor RichMessageEditor + FlowDocumentHtml, DPAPI, folder Pobrane, podgląd 127.0.0.1
        │
AdMng.Presentation/Correspondence ── CorrespondenceViewModel + strony, dziennik, szablony, połączenie z Outlookiem
        │                                    │
AdMng.Core/Correspondence                 AdMng.Outlook ── COM (dynamic) na wątku STA ──► classic Outlook (outlook.exe)
  Import, Fields, Composition,              (tylko App; nigdy w pakiecie Watchdoga)
  Journal, Mailboxes, Outlook (kontrakty)
```

- **AdMng.Outlook** (osobny projekt, `net10.0`, działa tylko na Windows, kompiluje się wszędzie – jak `AdMng.Remote`):
  klasyczny Outlook przez **późne wiązanie COM** (`dynamic`, bez bibliotek PIA), każde wywołanie na jednym wątku STA
  sesji (`StaWorker`) z filtrem komunikatów COM (ponawianie przy „Outlook zajęty” do 60 s). `OutlookClient` dołącza do
  uruchomionego Outlooka albo go uruchamia (3 próby); `OutlookSession` wysyła lub zapisuje wersję roboczą
  (`OutlookMailBuilder` – kolejność kroków jak w Korespondencji WPiA: konto, *w imieniu*, adresaci z rozpoznaniem,
  temat, HTML, potwierdzenie, dostarczenie później, głosowanie, właściwości nazwane, obrazki `cid:` jako ukryte
  załączniki, załączniki, zapis, wysyłka), buduje podgląd w Outlooku, diagnozuje środowisko i czyta głosy z *Elementów
  wysłanych* (`OutlookCampaigns`, właściwości nazwane `AdMngCampaign`/`AdMngKeys`). `OutlookErrors` zamienia HRESULT i
  krok na kod `outlook.*` – treść wyjątku nigdy nie jest zachowywana (może zawierać adresy). Obrazki dla Outlooka trafiają
  do prywatnego folderu tymczasowego usuwanego zaraz po zapisie wiadomości (`OutlookTempFiles`). Tu są też podpisy
  Outlooka (`OutlookSignatureFiles`) i transport U1 `OutlookUserMessageTransport`. Odwołuje się do niego tylko
  `AdMng.App`.
- **Core/Correspondence** – czysta logika: `Import` (adresy, wyłuskiwanie z tekstu i tabel, czytniki CSV, XLSX, DOCX,
  ODT/ODS, RTF i HTML z limitami i ochroną przed bombami zip), `Fields` (pola `{{Pole}}`/`{{Pole|tekst}}`, naprawa pól
  rozciętych znacznikami, zbiór pól odbiorcy), `Composition` (dokument wiadomości, renderer z `{{lista}}`, parser
  podpisów Outlooka, kontrola zgodności z pocztą, grupowanie), `Journal` (skróty HMAC-SHA-256 i format pliku dziennika),
  `Mailboxes` (rodzaje skrzynek z `msExchRecipientTypeDetails`, imię i nazwisko właściciela, indeks osób ze składaniem
  polskich znaków, dopasowanie z poziomami i pełnomocnictwami) oraz kontrakty Outlooka (`IOutlookClient`,
  `IOutlookSession`, `OutlookMessage`, `OutlookFailureCodes`). Pola z AD to wyrażenia `UserMessageFields` (U1, U1d,
  nowe `{$created}`).
- **Presentation/Correspondence** – `CorrespondenceViewModel` (okno, `WindowViewModelBase`) i strony:
  `OutlookConnectionViewModel`, `MailboxOwnersPageViewModel`, `RecipientsPageViewModel`, `MessagePageViewModel`,
  `PreviewPageViewModel`, `SendPageViewModel`, `ResponsesPageViewModel`. Singletony aplikacji: `CorrespondenceJournal`
  (plik dziennika z blokadą `journal.lock` między oknami i procesami), `CorrespondenceTemplateLibrary` (szablony
  wbudowane i operatora, eksport/import, ostatni szkic; obrazki we wspólnym `MessageImageStore`) i
  `LoopbackPreviewServer` (podgląd w przeglądarce z pamięci na `127.0.0.1` pod losowym tokenem, 15 minut).
  **Scoped** (okno połączenia): `CorrespondenceServices`, `CorrespondenceViewModelFactory`, dialogi i launcher
  (`ICorrespondenceDialogs`, `ICorrespondenceLauncher` w `DialogService` App). Polecenia powłoki `tools.correspondence`,
  `object.correspondence`, `object.mailboxOwners`. Ustawienia: `CorrespondenceSettings` w `settings.json` (opcje,
  szablony, ostatni szkic, definicje pól, opcje skrzynek – nazwy, filtry LDAP i DN przenośne, nigdy wartości z katalogu).
- **App/Correspondence** – `CorrespondenceWindow` (zakładki, siatki z kolumnami pól), `RichMessageEditor` (WPF
  `RichTextBox`, wklejanie przez potok U1c) z konwerterem `FlowDocumentHtml` (dokument ↔ ograniczony, oczyszczony HTML),
  `DpapiKeyProtector` (klucz dziennika, DPAPI bieżącego użytkownika), `WindowsCorrespondencePlatform` (folder *Pobrane*),
  `LoopbackBrowserPreview` i rejestracja `AddAdMngCorrespondenceApp()` (klient Outlooka i transport U1 jako singletony).
  Test dymny i testy UI podmieniają Outlooka na fałszywy – nigdy go nie uruchamiają.
- **Dane** (zasada 1) – lista odbiorców, wartości z AD, podgląd, wyniki, dopasowania skrzynek i odpowiedzi są tylko w
  pamięci okna. Jedyny wyjątek to dziennik „już wysłane” (`correspondence/journal.txt` obok ustawień): skróty adresów
  (HMAC-SHA-256 z kluczem użytkownika chronionym DPAPI), czas i temat, bez adresów
  ([ADR-0003](adr/0003-brak-lokalnych-danych.md), uzupełnienie). Dziennik sesji dostaje jeden wpis na wysyłkę bez
  adresów i tematu.
- **Testy** – `AdMng.Core.Tests/Correspondence` (czytniki plików, pola, kompozycja, dziennik, dopasowanie skrzynek na
  próbce `AddMailboxOwnerSample`), `AdMng.Outlook.Tests` (fałszywy model obiektowy Outlooka w CLR, Linux i Windows),
  `AdMng.Presentation.Tests/Correspondence` (z `FakeOutlookClient` z `AdMng.Testing`), testy UI i zrzuty 26–28 na
  Windows. Prawdziwy Outlook – tylko pilotaż.

## Bezpieczeństwo

- Hasła logowania i resetowane są tylko w pamięci; nie trafiają do ustawień, logów ani `ToString()`.
- Domyślnie Kerberos z podpisywaniem i szyfrowaniem (zgodne z wymuszaniem podpisywania LDAP w Windows Server 2025);
  proste wiązanie (simple bind) tylko po TLS lub w laboratorium za jawną zgodą.
- Filtry i DN budowane wyłącznie przez `LdapFilter` / `DistinguishedName` (escaping RFC 4515/4514).
- Eksport CSV chroniony przed wstrzykiwaniem formuł Excela; eksport haseł wymaga osobnego potwierdzenia.
- Schowek: polecenia kopiowania pomijają wartości poufne (hasła, LAPS, BitLocker, atrybuty poufne wg schematu – kolumny
  z nimi są pomijane przy Ctrl+C); sekret trafia do schowka tylko po jawnym odsłonięciu w Edytorze atrybutów, z
  wykluczeniem z historii schowka i wyczyszczeniem po 60 s. Wiersze kopiowane do Excela mają tę samą ochronę przed
  formułami co eksport CSV.
- Ostrzeżenia dla obiektów chronionych (adminCount=1, obiekty krytyczne systemu, konta wbudowane) i potwierdzenie
  przez wpisanie liczby obiektów dla dużych/ryzykownych operacji.
- Aplikacja nie jest granicą bezpieczeństwa – o uprawnieniach decydują ACL w AD; UI jedynie je odzwierciedla.

## AdMng Watchdog – usługa monitorująca

AD_MNG jest konsolą bez lokalnych danych, więc wykrywanie zmian w katalogu (kto, co, kiedy, skąd) działa w osobnym
procesie: usłudze **AdMng Watchdog** (ASP.NET Core + Worker, 24/7). Od wersji 0.3.0 działa ona wyłącznie na Linuksie –
jednostka systemd `admng-watchdog.service` na serwerze Debian 12/13 poza domeną, z kontem komputera `ADMNG-WD$`
(keytab, hasło zmieniane co 30 dni) i odbiornikiem zdarzeń OpenWEC; usługa Windows z gMSA była wydaniem 0.2.0
([ADR-0009](adr/0009-watchdog-na-linuksie.md)). Zakładka **Watchdog** w AD_MNG jest jej klientem. Funkcje:
[WATCHDOG.md](WATCHDOG.md), wdrożenie: [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md); wiążące specyfikacje implementacji:
[dev/watchdog-spec.md](dev/watchdog-spec.md) i [dev/watchdog-linux-design.md](dev/watchdog-linux-design.md).

```text
serwer Debian 12/13 poza domeną (Tier 0), konto komputera CONTOSO\ADMNG-WD$ z keytabem
├─ admng-watchdog.service (AdMng.Watchdog, konto admng-watchdog, piaskownica systemd)
│   ├─ ChangeDetectionEngine ──► DC (PDC): LDAP 389, SASL GSSAPI podpis + szyfrowanie – RootDSE, uSNChanged,
│   │    Extended DN, metadane replikacji, Deleted Objects, partycje Configuration i Schema
│   ├─ GpoContentJob ──► DC (PDC): SMB 3 (smbclient, Kerberos, podpisywanie) – pliki GPO z SYSVOL
│   ├─ WecFileIngestionJob ◄── /var/lib/openwec/out/<DC>/security.jsonl (pliki OpenWEC)
│   ├─ AttributionEngine (rekordy 47xx, 5136…, 4624/4625/4771/4776 z magazynu), ScheduledScanJob, ServiceAccountMonitor
│   ├─ RuleEvaluationJob → NotificationDispatchJob → OutboxSenderJob ──► SMTP (MailKit) / Teams / webhook
│   ├─ DigestJob, HeartbeatJob, RetentionJob, StorageGuardJob
│   ├─ SQLite (WAL) /var/lib/admng-watchdog/watchdog.db: zdarzenia, migawki, rekordy DC, kolejka, sekrety (DP)
│   └─ API HTTPS 8443 (Kestrel), Kerberos (Negotiate, keytab), role z tokenGroups po SID grup AD
├─ openwecd.service (OpenWEC, konto openwec) ◄── WEF z każdego DC: HTTP 5985 + Kerberos
└─ admng-watchdog-keytab.timer (zmiana hasła), admng-wec-rotate.timer (rotacja plików OpenWEC)
                      ▲ HTTPS 8443, Kerberos
   AD_MNG – zakładka Watchdog (IWatchdogClient)
```

| Projekt | Rola |
|---|---|
| `AdMng.Watchdog.Contracts` | model zdarzenia, DTO API, konfiguracja, trasy, JSON (bez zależności) |
| `AdMng.Watchdog.Core` | cała logika (wykrywanie, migawki, atrybucja, reguły, planowanie powiadomień, formatery e-mail/Teams/webhook, raport dzienny) – testowana na Linuksie z `FakeDirectorySession` i zarejestrowanymi zdarzeniami; rejestracja `AddWatchdogCore()` |
| `AdMng.Watchdog.Service` | host: API, autoryzacja (role z `tokenGroups`), magazyn SQLite, Data Protection (klucz w poświadczeniu systemd), transporty (MailKit, `HttpClient`; treść MIME wiadomości do użytkowników z `AdMng.Mail`), odczyt plików OpenWEC, czytnik SYSVOL (smbclient), samokontrola konta usługi, hosty zadań; do usunięcia po 0.3.0 – hosting Windows, HTTP.sys, DPAPI-NG, czytnik dzienników przez RPC, skrypty instalacyjne |
| `AdMng.Watchdog.Keytab` | narzędzie `admng-watchdog-keytab`: zmiana hasła konta `ADMNG-WD$` i keytab (MIT libkrb5) |
| `packaging/`, `tools/packaging` | pakiety Debiana `admng-watchdog` i `admng-openwec`: jednostki systemd, skrypty instalacyjne, budowanie i test instalacji |
| `AdMng.Watchdog.Client` | klient HTTPS/Kerberos API (`IWatchdogClient`) używany przez AD_MNG |
| `AdMng.Presentation/Watchdog`, `AdMng.App/Views/Watchdog` | ViewModele i widoki zakładki Watchdog |

Kierunek zależności: Contracts ← Core ← Service; Contracts ← Client ← Presentation/App. `AdMng.Watchdog.Core` korzysta
z `AdMng.Core` (DN, filtry, sesja katalogu), usługa – z `AdMng.Directory` (LDAP).

Zasady, które odróżniają usługę od reszty rozwiązania:

- **Dane tylko w folderze usługi** ([ADR-0008](adr/0008-watchdog-dane-w-osobnym-folderze.md), częściowo zmienia
  ADR-0003; na Linuksie wg [ADR-0009](adr/0009-watchdog-na-linuksie.md)): migawki obserwowanych atrybutów i ustawień
  GPO, zdarzenia (3 lata), rekordy dzienników DC (logowania 48 h) i kolejka wysyłek leżą w jednej bazie SQLite
  (`secure_delete`, retencja, kopie `VACUUM INTO`) w `/var/lib/admng-watchdog` (0700, sprawdzane przy starcie – usługa
  nie startuje przy szerszych uprawnieniach); surowe zdarzenia z DC leżą w plikach OpenWEC tylko do ich odczytu. AD_MNG,
  Presentation, Core i klient niczego z usługi nie zapisują.
- **Sekrety kanałów** (hasło SMTP, sekret OAuth, adresy Teams/webhooków, klucze podpisu) tylko w `ISecretStore` –
  ASP.NET Core Data Protection z kluczami szyfrowanymi kluczem z poświadczenia systemd (TPM2 + klucz hosta albo klucz
  hosta) – i **powiązane z miejscem docelowym**: zmiana
  serwera lub adresu bez ponownego wpisania sekretu jest odrzucana. API nigdy ich nie zwraca.
- **Wykrywanie** odpytuje `uSNChanged` jednego DC (domyślnie PDC) z migawką kluczowaną `objectGUID`; atrybuty DN są
  czytane z kontrolką Extended DN (porównanie po GUID – zmiana nazwy członka nie jest zmianą grupy); znacznik postępu
  (watermark) jest zapamiętywany per DC, pełna resynchronizacja tylko dla nowego DC, przywrócenia DC lub cofnięcia USN;
  codzienne uzgodnienie wykrywa obiekty, które „zniknęły”.
- **Atrybucja** (kto i skąd) jest osobnym, asynchronicznym wzbogaceniem z dzienników Security kontrolerów, które
  kontrolery przekazują przez Windows Event Forwarding do OpenWEC; metadane replikacji wskazują DC i czas. Gdy nie da się ustalić wykonawcy, zdarzenie pokazuje „nieustalony” z kodem przyczyny –
  nigdy zgadywanie.
- **Dyscyplina szumu:** tryb wdrożenia przez pierwsze 7 dni, okno ustalania przy zakładaniu kont (wieloetapowe
  założenie = jedno zdarzenie), ważność zależna od wykonawcy, tłumienie powtórzeń, agregacja, godziny pracy wg
  kalendarza polskich świąt.
- **API:** HTTPS (TLS 1.2/1.3), wyłącznie Kerberos (NTLM odrzucany – Kestrel w .NET 10 nie ma channel binding),
  role Viewer/Admin wg SID-ów grup z lokalnego pliku startowego, którego konto usługi nie może zmienić (członkostwo
  usługa czyta sama z `tokenGroups`); ochrona CSRF
  nagłówkiem `X-AdMng-Client`; zmiany konfiguracji same są zdarzeniem (Tier 0 nie da się wyciszyć przez API).
- **Dzienniki diagnostyczne bez wartości z katalogu** – wyjątki tylko przez `SafeExceptionLog` (typ, kody, stos –
  nigdy `Message`), filtr redakcji na dostawcach logów.

## Testy i jakość

| Poziom | Gdzie | Jak |
|---|---|---|
| Jednostkowe | `AdMng.Core.Tests`, `AdMng.Presentation.Tests` | xUnit v3, `FakeDirectorySession` |
| Integracyjne | `AdMng.Directory.IntegrationTests` | Samba AD DC z `tools/testlab` (lokalnie i w CI na Ubuntu) |
| Outlook (U5) | `AdMng.Outlook.Tests` | fałszywy model obiektowy Outlooka w CLR: kolejność kroków budowania wiadomości, obrazki `cid:`, klasyfikacja błędów, wątek STA, pliki tymczasowe, głosy, transport U1 (Linux i Windows); prawdziwy Outlook – tylko pilotaż |
| Dymne UI | CI Windows | `AdMng.exe --smoke-test` tworzy wszystkie widoki z danymi testowymi |
| Watchdog – silnik | `AdMng.Watchdog.Core.Tests` | `FakeDirectorySession` z emulacją USN, metadanych i kontrolera; zarejestrowane zdarzenia 47xx/5136; fałszywy zegar |
| Watchdog – usługa | `AdMng.Watchdog.Service.Tests` | host w procesie (`WebApplicationFactory`), SQLite w folderze tymczasowym, odbiorniki SMTP/HTTP, testy higieny dzienników, odczyt plików OpenWEC z awarią na każdym kroku, rozbiór listingu smbclient, role z `tokenGroups`; testy tylko-Windows w CI Windows |
| Watchdog – laboratorium | `AdMng.Watchdog.IntegrationTests` | prawdziwe silniki z `AddWatchdogCore()` przeciw Samba AD DC (konto tylko do odczytu), scenariusze z 22.4 specyfikacji w jednorazowym OU, usługa end-to-end z Mailpit i podpisanym webhookiem, konto komputera z keytabem (zmiana hasła, GSSAPI, Negotiate z rolami), OpenWEC, treść GPO z SYSVOL |
| Watchdog – pakiety | CI Ubuntu (`openwec-package`, `linux-package`) | budowanie `admng-openwec` (Debian 12 i 13, źródła GPL-3.0) i `admng-watchdog`, test instalacji w kontenerach Debian 12 i 13 (konta, tryby, poświadczenie, `systemd-analyze verify` i `security`, `--smoke-test`, start z TLS, usunięcie), lintian |
| Watchdog – narzędzia | `tools/testlab/watchdog` | usługa w trybie laboratoryjnym, Mailpit, odbiornik webhooków, scenariusz `lab-scenario.sh --verify` (także w CI) |
| Watchdog – dymny | CI (Windows i test instalacji pakietu) | `--smoke-test` opublikowanej usługi (migracje, `/healthz`, `/api/v1/status`, wszystkie formatery); w pakiecie jako konto `admng-watchdog` |
| Statyczne | build | ostrzeżenia = błędy, analiza stylu kodu |
