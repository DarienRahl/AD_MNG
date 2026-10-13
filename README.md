# AD Manager (AD_MNG)

Nowoczesna, szybka konsola do zarządzania Active Directory – następca przystawki **Użytkownicy i komputery usługi
Active Directory** (`dsa.msc`). Wygląda i działa znajomo (drzewo domeny i jednostek organizacyjnych, lista obiektów,
karty właściwości, te same skróty klawiszowe), ale jest zbudowana według dzisiejszych standardów: Windows 11 Fluent,
tryb ciemny, PL/EN, okna niemodalne, wyszukiwanie w trakcie pisania i – przede wszystkim – **edycja zbiorcza** z
podglądem zmian, raportem i cofaniem.

> Status: wczesna wersja rozwojowa (faza 0–2 z [roadmapy](docs/ROADMAP.md)).

## Najważniejsze cechy

- **Płynność zamiast przystawki MMC** – zapytania LDAP asynchroniczne i strumieniowane stronami, pobierane są tylko
  potrzebne atrybuty, brak limitu 2000 obiektów w folderze, wirtualizowane listy, odświeżanie wyłącznie zmienionych
  obiektów.
- **Zero lokalnych danych** – nic z katalogu nie jest zapisywane na dysku (brak bazy, cache, snapshotów); każde
  odświeżenie pyta kontroler domeny na żywo. Na dysku są tylko ustawienia interfejsu, profile połączeń bez haseł i
  definicje widoków.
- **Różne poświadczenia** – logowanie bieżącym kontem Windows albo innym (helpdesk, administrator); Kerberos z
  podpisywaniem i szyfrowaniem lub LDAPS. Interfejs pokazuje, co dane konto może zmienić, a czego nie
  (`allowedAttributesEffective`).
- **Edycja zbiorcza** użytkowników, komputerów, grup i kontaktów: ustawianie/czyszczenie atrybutów, szablony
  (`{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}`), znajdź i zamień, włącz/wyłącz, odblokuj, reset haseł,
  przenoszenie, zmiana nazwy, członkostwo w grupach, usuwanie, import CSV – zawsze z podglądem różnic, walidacją,
  raportem, eksportem do CSV/PowerShell i możliwością cofnięcia.
- **Widoki dopasowane do Waszego AD** – przypinanie OU (np. *Pracownicy*, *Komputery*), zapisane zapytania LDAP,
  gotowe szybkie filtry (zablokowane, wyłączone, nieaktywne > N dni, hasło nigdy nie wygasa…), eksport/import widoków
  dla zespołu.
- **Poczta i Kosz AD** (priorytet z ustaleń) – karta adresów e-mail (proxyAddresses, mailNickname, ukrycie w GAL) oraz
  przeglądanie i przywracanie usuniętych obiektów.

## Wymagania

- Windows 10 22H2 / Windows 11 / Windows Server 2019+ (stacja w domenie lub z dostępem do DC).
- Do uruchomienia wersji self-contained nie jest potrzebny .NET ani RSAT.
- Do budowania: .NET SDK 10.0.

## Budowanie i uruchamianie

```powershell
dotnet build AdMng.slnx
dotnet run --project src/AdMng.App
# wersja jednoplikowa:
dotnet publish src/AdMng.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Gotowe pliki `AdMng.exe` są też publikowane jako artefakt `AdMng-win-x64` każdego przebiegu CI (zakładka *Actions*).

Testy:

```bash
dotnet test tests/AdMng.Core.Tests
dotnet test tests/AdMng.Presentation.Tests
# testy integracyjne na laboratorium Samba AD DC – zob. tools/testlab/README.md
```

## Dokumentacja

- [Roadmapa i fazy prac](docs/ROADMAP.md)
- [Architektura](docs/ARCHITECTURE.md) i [decyzje architektoniczne (ADR)](docs/adr/)
- [Wyniki analizy: dsa.msc, ADAC, produkty konkurencyjne, techniki LDAP](docs/RESEARCH.md)
- [Laboratorium testowe](tools/testlab/README.md)
- [Notatki dla deweloperów](CLAUDE.md)
