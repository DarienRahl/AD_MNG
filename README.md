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

Testy UI (tylko Windows, PowerShell):

```powershell
$env:ADMNG_UI_TESTS = "1"; dotnet test tests/AdMng.App.UiTests
```

Rozwiązanie buduje się także na Linuksie (aplikacja WPF jest tam tylko kompilowana); testy jednostkowe i integracyjne
działają na obu systemach.

## Dokumentacja

- [Roadmapa, fazy prac i znane ograniczenia](docs/ROADMAP.md)
- [Architektura](docs/ARCHITECTURE.md) i [decyzje architektoniczne (ADR)](docs/adr/)
- [Wyniki analizy: dsa.msc, ADAC, produkty konkurencyjne, techniki LDAP](docs/RESEARCH.md)
- [Laboratorium testowe](tools/testlab/README.md)
- [Notatki dla deweloperów](CLAUDE.md)
