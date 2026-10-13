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

## Interfejs użytkownika

- **Okno główne:** pasek menu i narzędzi (jak ADUC), pasek adresu (DN lub ścieżka `contoso.test/Firma/IT`),
  globalne wyszukiwanie (Ctrl+K / Ctrl+E), drzewo z sekcją „Widoki”, karty z listami, panel szczegółów, pasek stanu
  (DC, tożsamość – z wyróżnieniem kont uprzywilejowanych, liczba obiektów, czas zapytania, operacje w tle).
- **Listy:** strumieniowo ładowane, wirtualizowane, kolumny wybierane z atrybutów schematu, szybki filtr tekstowy,
  filtry-chipy wykonywane po stronie serwera (zablokowane, wyłączone, nieaktywne N dni, …), sortowanie, eksport CSV.
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

## Testy i jakość

| Poziom | Gdzie | Jak |
|---|---|---|
| Jednostkowe | `AdMng.Core.Tests`, `AdMng.Presentation.Tests` | xUnit v3, `FakeDirectorySession` |
| Integracyjne | `AdMng.Directory.IntegrationTests` | Samba AD DC z `tools/testlab` (lokalnie i w CI na Ubuntu) |
| Dymne UI | CI Windows | `AdMng.exe --smoke-test` tworzy wszystkie widoki z danymi testowymi |
| Statyczne | build | ostrzeżenia = błędy, analiza stylu kodu |
