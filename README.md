# AD Manager (AD_MNG)

Nowoczesna, szybka konsola do zarządzania Active Directory – następca przystawki **Użytkownicy i komputery usługi
Active Directory** (`dsa.msc`). Wygląda i działa znajomo (drzewo domeny i jednostek organizacyjnych, lista obiektów,
karty właściwości, te same skróty klawiszowe), ale jest zbudowana według dzisiejszych standardów: Windows 11 Fluent,
tryb ciemny, PL/EN, okna niemodalne, wyszukiwanie w trakcie pisania i – przede wszystkim – **edycja zbiorcza** z
podglądem zmian, raportem i cofaniem.

> **Status:** wersja rozwojowa. Fazy 1 (codzienna praca zamiast ADUC) i 2 (edycja zbiorcza) z [roadmapy](docs/ROADMAP.md)
> są zaimplementowane, podobnie jak Kosz AD z fazy 4. Aplikacja jest testowana automatycznie (testy jednostkowe, testy UI
> na Windows, testy integracyjne na kontrolerze Samba AD), ale **nie przeszła jeszcze pilotażu na produkcyjnym Windows
> AD** – zacznijcie od domeny testowej lub konta z ograniczonymi uprawnieniami.

## Najważniejsze cechy

- **Płynność zamiast przystawki MMC** – zapytania LDAP asynchroniczne i strumieniowane stronami, pobierane są tylko
  potrzebne atrybuty, brak limitu 2000 obiektów w folderze, wirtualizowane listy, karty (Ctrl+T), paleta poleceń
  (Ctrl+K), listy odświeżane na żywo przez powiadomienia LDAP – zmienione obiekty, a nie cały folder.
- **Zero lokalnych danych** – nic z katalogu nie jest zapisywane na dysku (brak bazy, cache, snapshotów); każde
  odświeżenie pyta kontroler domeny na żywo. Na dysku (`%APPDATA%\AdMng`) są tylko ustawienia interfejsu, profile
  połączeń bez haseł i definicje widoków. Hasła nigdy nie trafiają do dzienników; do pliku – tylko w eksporcie wyników
  resetu haseł, o który operator wyraźnie poprosi.
- **Różne poświadczenia** – logowanie bieżącym kontem Windows albo innym (helpdesk, administrator); Kerberos z
  podpisywaniem i szyfrowaniem, LDAPS lub StartTLS; cała sesja pracuje z jednym kontrolerem domeny. Interfejs pokazuje,
  co dane konto może zmienić, a czego nie (`allowedAttributesEffective`), i wyróżnia konta uprzywilejowane.
- **Znajome karty właściwości** – użytkownik, komputer, grupa, kontakt, OU: Ogólne, Adres, Konto, Profil, Telefony,
  Organizacja, Członek grup (bezpośrednio i w pełni, z zagnieżdżeniem), Członkowie, Zarządzany przez, Obiekt, Edytor
  atrybutów; akcje helpdesk (reset hasła, odblokowanie, włącz/wyłącz, dodaj do grupy), tworzenie, przenoszenie
  (także przeciągnij-upuść), zmiana nazwy (F2), usuwanie.
- **Edycja zbiorcza** użytkowników, komputerów, grup i kontaktów: ustawianie/czyszczenie atrybutów, szablony
  (`{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}`), znajdź i zamień, włącz/wyłącz, odblokuj, reset haseł,
  przenoszenie, zmiana nazwy, członkostwo w grupach, usuwanie, import CSV (także `;` i cp1250 z polskiego Excela) –
  zawsze z podglądem różnic, walidacją, ochroną przed nadpisaniem cudzych zmian, raportem, eksportem do CSV/PowerShell i
  możliwością cofnięcia.
- **Widoki dopasowane do Waszego AD** – przypinanie OU (np. *Pracownicy*, *Komputery*), zapisane zapytania LDAP z
  kolumnami, gotowe szybkie filtry (zablokowane, wyłączone, nieaktywne > N dni, hasło nigdy nie wygasa…), eksport/import
  widoków dla zespołu.
- **Poczta (Exchange)** – karta *Adresy e-mail*: `proxyAddresses` z dokładnie jednym głównym adresem SMTP, `mail`,
  `mailNickname`, `targetAddress`, ukrycie w książce adresowej; atrybuty poczty edytowalne też zbiorczo.
- **Kosz AD** – okno *Kosz Active Directory* (Ctrl+Shift+R): usunięte obiekty czytane na żywo, filtry, przywracanie do
  pierwotnego lub wskazanego miejsca, całe poddrzewo OU jednym planem w kolejności rodzic → dzieci.
- **Przejrzystość** – dziennik sesji, historia równoważnych poleceń PowerShell, centrum aktywności z postępem i
  anulowaniem, komunikaty błędów AD po polsku.
- **AdMng Watchdog – monitor zmian w AD** – osobna usługa Windows (24/7, konto gMSA tylko do odczytu) wykrywa nowe i
  usunięte obiekty, zmiany atrybutów (stara → nowa wartość), blokady, wygasające konta, zmiany członkostwa i
  **uprzywilejowanego dostępu** (także przez grupy zagnieżdżone) oraz zmiany Tier 0; ustala wykonawcę z dzienników
  zabezpieczeń kontrolerów domeny i powiadamia e-mailem (od razu i raportem dziennym), w Microsoft Teams (Workflows) i
  podpisanym webhookiem (SIEM). W AD_MNG zakładka **Watchdog** (Narzędzia → Watchdog) pokazuje zdarzenia, reguły,
  kanały i stan usługi. Usługa jako jedyna część rozwiązania przechowuje dane – we własnym, chronionym folderze
  ([ADR-0008](docs/adr/0008-watchdog-dane-w-osobnym-folderze.md)). Wdrożenie: [docs/WATCHDOG.md](docs/WATCHDOG.md).

Czego jeszcze brakuje (m.in. LAPS, BitLocker, edytor ACL, zakładanie kont z CSV, „Kopiuj użytkownika”): zob.
[roadmapę](docs/ROADMAP.md#znane-ograniczenia).

## Wymagania

- Windows 10 22H2 / Windows 11 / Windows Server 2019+ (stacja w domenie lub z dostępem sieciowym do DC).
- Do uruchomienia wersji self-contained nie jest potrzebny .NET ani RSAT.
- Do budowania: .NET SDK 10.0.

## Pobranie gotowej wersji

Każdy przebieg CI publikuje jednoplikowy `AdMng.exe` (win-x64, self-contained):

1. Zakładka **Actions** repozytorium → workflow **CI** → najnowszy zielony przebieg.
2. Sekcja *Artifacts* → **AdMng-win-x64** (archiwum ZIP, przechowywane 14 dni).
3. Rozpakuj i uruchom `AdMng.exe`. Plik nie jest jeszcze podpisany (podpis i MSI – faza 7), więc SmartScreen lub
   AppLocker/WDAC mogą go zablokować.

Usługa **AdMng Watchdog** jest publikowana obok, jako artefakt **AdMng-Watchdog-win-x64** (zależna od środowiska
uruchomieniowego: wymaga ASP.NET Core Runtime ≥ 10.0.13 x64 na serwerze, aktualizowanego przez Microsoft Update) – razem
ze skryptami `Deploy\Install-AdMngWatchdog.ps1` / `Uninstall-AdMngWatchdog.ps1`. Instalacja krok po kroku (gMSA, SPN,
certyfikat, uprawnienia, inspekcja): [docs/WATCHDOG.md](docs/WATCHDOG.md#4-wdrożenie-krok-po-kroku).

Pierwsze uruchomienie: okno połączenia podpowiada domenę komputera i listę kontrolerów; wybierz bieżące konto Windows albo
podaj inne poświadczenia (`DOMENA\login` lub UPN) i tryb zabezpieczenia połączenia (domyślnie Kerberos z podpisywaniem i
szyfrowaniem). Profil połączenia można zapisać – bez hasła.

## Budowanie i uruchamianie

```powershell
dotnet build AdMng.slnx
dotnet run --project src/AdMng.App
# wersja jednoplikowa:
dotnet publish src/AdMng.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
# szybki test uruchomienia wszystkich okien bez połączenia z AD:
publish\AdMng.exe --smoke-test
```

Testy:

```bash
dotnet test tests/AdMng.Core.Tests
dotnet test tests/AdMng.Presentation.Tests
# testy integracyjne na laboratorium Samba AD DC – zob. tools/testlab/README.md
```

AdMng Watchdog (każdy projekt testów osobnym wywołaniem `dotnet test`):

```bash
dotnet test tests/AdMng.Watchdog.Core.Tests        # silnik: wykrywanie, atrybucja, reguły, powiadomienia, formatery
dotnet test tests/AdMng.Watchdog.Service.Tests     # usługa w procesie: API, SQLite, transporty, hosting
dotnet test tests/AdMng.Watchdog.IntegrationTests  # laboratorium Samba + Mailpit (zob. tools/testlab/watchdog/README.md)
# usługa przeciw laboratorium (Mailpit, odbiornik webhooków, scenariusz zmian):
tools/testlab/watchdog/run-watchdog-lab.sh --reset --background
tools/testlab/watchdog/configure-lab.sh --test
tools/testlab/watchdog/lab-scenario.sh --verify
```

Publikacja usługi i jej test dymny (tymczasowy folder danych, pętla zwrotna, bez katalogu – jak w CI):

```powershell
dotnet publish src/AdMng.Watchdog.Service -c Release -r win-x64 --self-contained false -o publish-watchdog
publish-watchdog\AdMng.Watchdog.exe --smoke-test
```

Testy UI (tylko Windows, PowerShell):

```powershell
$env:ADMNG_UI_TESTS = "1"; dotnet test tests/AdMng.App.UiTests
```

Zrzuty ekranu (polski interfejs na przykładowej firmie z testów, PNG + `manifest.json` z opisami) powstają przy każdym
pushu: krok CI **Screenshots** zapisuje je jako artefakt `screenshots`, a zadanie **publish-screenshots** commituje je do
gałęzi `screenshots` – folder o nazwie gałęzi źródłowej (`/` zamienione na `_`), z `README.md` (commit, przebieg CI,
opisy obrazów). Pobranie bez przełączania gałęzi:

```bash
git fetch origin screenshots && mkdir -p zrzuty && git archive origin/screenshots main | tar -x -C zrzuty
```

Lokalnie (Windows): `$env:ADMNG_SCREENSHOTS_DIR = "$PWD\zrzuty"; dotnet test tests/AdMng.App.UiTests --filter "FullyQualifiedName~ScreenshotTests"`.

Rozwiązanie buduje się także na Linuksie (aplikacja WPF jest tam tylko kompilowana); testy jednostkowe i integracyjne
działają na obu systemach.

## Dokumentacja

- [Roadmapa, fazy prac i znane ograniczenia](docs/ROADMAP.md)
- [Architektura](docs/ARCHITECTURE.md) i [decyzje architektoniczne (ADR)](docs/adr/)
- [Wyniki analizy: dsa.msc, ADAC, produkty konkurencyjne, techniki LDAP](docs/RESEARCH.md)
- [AdMng Watchdog – wdrożenie i konfiguracja usługi monitorującej](docs/WATCHDOG.md)
- [Laboratorium testowe](tools/testlab/README.md) i [Watchdog w laboratorium](tools/testlab/watchdog/README.md)
- [Notatki dla deweloperów](CLAUDE.md)
