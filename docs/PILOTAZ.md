# Pilotaż AD Managera – lista kontrolna

Prawie wszystko, co AD Manager potrafi, sprawdzono testami na fałszywym katalogu, laboratorium Samba i w testach
interfejsu na Windows. **Nie sprawdzono jeszcze tego, co wymaga prawdziwego Windows Server AD**: kontrolerów Windows,
GPMC i klientów stosujących zasady grupy, dzienników zdarzeń czytanych przez RPC, zdalnych akcji, klasycznego Outlooka
i Exchange. Ten dokument prowadzi przez pilotaż: przygotowanie, sprawdzenie gotowości, scenariusze z oczekiwanym
wynikiem i sposób zgłaszania problemów. Szczegółowe listy usługi AdMng Watchdog i Korespondencji są w ich instrukcjach
(odnośniki niżej) – tu ich nie powtarzamy.

> Zacznijcie od **domeny testowej** albo konta z ograniczonymi uprawnieniami. Każda zmiana w AD i SYSVOL przechodzi przez
> podgląd planu zmian z możliwością cofnięcia, ale pilotaż ma dopiero potwierdzić zgodność z narzędziami Microsoftu.

---

## 1. Środowisko

| Element | Zalecenie |
|---|---|
| Kontrolery domeny | co najmniej dwa: Windows Server 2025 i Windows Server 2022 (różne poziomy funkcjonalne do sprawdzenia), jeden z nich emulator PDC |
| Stacja administratora | Windows 11 24H2/25H2, konto z uprawnieniami do OU testowej (osobno: konto helpdesku z ograniczonymi uprawnieniami) |
| Komputery testowe | stacja Windows 11 i serwer członkowski w OU testowej – do zdalnych akcji, zasad grupy i dzienników zdarzeń |
| AdMng Watchdog (opcjonalnie) | Debian 13 według [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) |
| Korespondencja (opcjonalnie) | klasyczny Outlook z profilem Exchange (lokalny lub Exchange Online) |
| Narzędzia do porównań | GPMC, `gpresult /h`, `secedit`, LockoutStatus i EventCombMT, Podgląd zdarzeń, ADUC/ADAC, PowerShell `ActiveDirectory` i `GroupPolicy` |

Pliki AD Managera nie są jeszcze podpisane cyfrowo: SmartScreen, AppLocker albo WDAC mogą zablokować `AdMng.exe` –
na czas pilotażu potrzebny jest wyjątek dla folderu programu albo skrótu pliku (`SHA256SUMS.txt` z wydania).

## 2. Przygotowanie i gotowość

1. Na kontrolerach i komputerach testowych włączcie to, czego potrzebują funkcje (szczegóły w instrukcjach):
   - zdalne akcje – WinRM ([KOMPUTERY.md, rozdział 5](KOMPUTERY.md#5-włączenie-winrm-na-komputerach-zasadą-grupy));
   - dzienniki zdarzeń – grupa reguł zapory *Zdalne zarządzanie dziennikiem zdarzeń*, konto operatora w grupie
     *Czytelnicy dziennika zdarzeń*, inspekcja na DC ([DZIENNIKI-ZDARZEN.md, rozdział 2](DZIENNIKI-ZDARZEN.md#2-wymagania)).
2. Uruchomcie **Narzędzia → Sprawdź gotowość środowiska…** – najpierw *Szybkie*, potem *Pełne sprawdzenie* z
   komputerem testowym. Każdy błąd i ostrzeżenie ma podpowiedź, co zrobić. Zapiszcie raport (HTML) jako punkt wyjścia.
3. Zanotujcie wersję AD Managera (prawy dolny róg) i wersje systemów.

## 3. Scenariusze

Wpisujcie wynik w tabeli z rozdziału 5. *Porównaj z* – narzędzie Microsoftu, z którym wynik ma się zgadzać.

### 3.1 Codzienna praca (faza 1–2)

| # | Scenariusz | Oczekiwany wynik | Porównaj z |
|---|---|---|---|
| A1 | Połączenie bieżącym kontem i innym kontem (Kerberos, podpisywanie i szyfrowanie; LDAPS) | połączenie bez ostrzeżeń, w pasku stanu DC i sposób zabezpieczenia | – |
| A2 | Duże OU (10 000+ obiektów), przewijanie, wyszukiwanie w trakcie pisania | pierwsza strona < 300 ms w LAN, okno nie zamarza | ADUC |
| A3 | Karty właściwości użytkownika, grupy, komputera (kraj, godziny logowania, wygasanie konta, zakres grupy) | te same wartości co w ADUC | ADUC |
| A4 | Konto helpdesku: pola bez uprawnień | pola tylko do odczytu, zrozumiałe komunikaty (odmowa dostępu, zasady haseł) | ADUC |
| A5 | Edycja zbiorcza 50 kont z podglądem, wykonaniem i *Cofnij* | zmiany jak w podglądzie, cofnięcie przywraca stan | PowerShell |
| A6 | Kosz AD: usunięcie i przywrócenie konta i OU z poddrzewem | obiekty i członkostwa wracają | ADAC |
| A7 | „Żadnych danych katalogu na dysku”: po pracy przejrzyjcie `%APPDATA%\AdMng` i `%TEMP%` | tylko ustawienia, profile bez haseł, definicje widoków | – |

### 3.2 Komputery (K1)

[KOMPUTERY.md, rozdział 7](KOMPUTERY.md#7-ograniczenia-i-pilotaż): zdalne akcje z kontem innym niż konto Windows,
ponowne uruchomienie z komunikatem, odświeżenie zasad użytkowników, uruchamianie narzędzi (`mmc`, Podgląd zdarzeń),
przygotowanie konta komputera i dołączenie do domeny (KB5020276), inwentarz zgodny z `Get-ADComputer`.

### 3.3 Zasady grupy (K2–K7)

| # | Scenariusz | Oczekiwany wynik | Porównaj z |
|---|---|---|---|
| G1 | Nowe GPO, połączenie z OU testową, kolejność, wymuszanie, blokowanie dziedziczenia | GPMC pokazuje to samo; `gpresult /h` na komputerze testowym | GPMC, `gpresult` |
| G2 | Filtrowanie zabezpieczeń i delegowanie | GPMC nie zgłasza niespójnych uprawnień folderu | GPMC |
| G3 | Kopia zapasowa w AD Managerze → przywrócenie w GPMC i odwrotnie | ustawienia i uprawnienia odtworzone | GPMC |
| G4 | Szablony administracyjne: kilka zasad (Windows, Edge), zapis | `Registry.pol` daje w GPMC te same ustawienia; klient je stosuje | GPMC, `gpresult` |
| G5 | Aktualizacja magazynu centralnego (paczka Windows 11 / Edge) | różnice przed zapisem, kopia poprzedniej wersji | – |
| G6 | Ustawienia zabezpieczeń: grupa z ograniczeniami, prawo użytkownika, zasada haseł, zaawansowana inspekcja | GPMC pokazuje je jak swoje; `secedit /analyze` i `gpresult` na kliencie | GPMC, `secedit` |
| G7 | Model „co dostanie komputer” dla komputera testowego | lista GPO i wygrywające ustawienia jak w *Modelowaniu zasad grupy* i `gpresult` | GPMC, `gpresult` |
| G8 | Zakładka *Ustawienia* i *Raport HTML…* | nazwy i wartości jak w raporcie GPMC (`Get-GPOReport`) | `Get-GPOReport` |
| G9 | Diagnostyka zasad grupy (pełna) | uwagi trafne; *Napraw…* działa przez podgląd planu | wiedza o domenie |
| G10 | *Wyszukiwarka ustawień GPO*: ustawienie (np. minimalna długość hasła), wartość, klucz techniczny, konto (nazwa i SID) | te same GPO i wartości co *Znajdź* w edytorze GPMC; wygrywająca wartość zgodna z `gpresult /h` | GPMC (*Znajdź*), `gpresult` |
| G11 | *Kopiuj do innego GPO…*: zasada ADMX, prawo użytkownika, ustawienie zabezpieczeń, mapowanie dysku do testowego GPO; potem *Cofnij* | GPMC pokazuje skopiowane ustawienia jak swoje; `gpresult /h` na komputerze testowym je stosuje; po cofnięciu znikają; hasło GPP odrzucone z powodem | GPMC, `gpresult` |
| G12 | *Scal z…* dwóch testowych GPO połączonych z tą samą OU | ustawienia w docelowym GPO, połączenie scalanego zastąpione, scalany wyłączony; `gpresult /h` bez zmian w działaniu; *Cofnij* przywraca stan | GPMC, `gpresult` |
| G13 | *Porządki w GPO*: każda propozycja (puste, bez połączeń, wyłączone, z jednym ustawieniem, duplikaty, kandydaci do scalenia) | propozycje zgodne z obrazem w GPMC; *Usuń GPO…* proponuje kopię zapasową i odmawia, gdy GPO zmieniono od analizy | GPMC |

Ograniczenia i szczegóły: [ZASADY-GRUPY.md, rozdział 15](ZASADY-GRUPY.md#15-ograniczenia-i-pilotaż).

### 3.4 Dzienniki zdarzeń (E)

| # | Scenariusz | Oczekiwany wynik | Porównaj z |
|---|---|---|---|
| E1 | Czytnik: kanał *Zabezpieczenia* wszystkich DC z ostatnich 24 godzin, filtr po koncie | te same zdarzenia co w Podglądzie zdarzeń; zasięg dziennika podany | Podgląd zdarzeń |
| E2 | Komputer bez reguły zapory / konto spoza *Czytelników dziennika zdarzeń* | czytelna przyczyna i podpowiedź | – |
| E3 | Zablokowanie konta testowego z komputera testowego (złe hasło 5×), potem *Skąd blokada konta* | źródło: komputer testowy; stan na wszystkich DC zgodny z LockoutStatus | LockoutStatus, EventCombMT |
| E4 | Usługa albo zadanie zaplanowane ze starym hasłem konta testowego | *Sprawdź komputer źródłowy* wskazuje usługę lub zadanie | – |
| E5 | Nieudane logowania: kilka kont ze złym hasłem z jednego komputera | ustalenie *Rozpylanie haseł* albo *Zgadywanie hasła* | Podgląd zdarzeń |
| E6 | Plik `.evtx` zapisany na DC („Zapisz wszystkie zdarzenia jako…”) | zdarzenia i opisy jak w Podglądzie zdarzeń | Podgląd zdarzeń |

Ograniczenia: [DZIENNIKI-ZDARZEN.md, rozdział 7](DZIENNIKI-ZDARZEN.md#7-ograniczenia-i-pilotaż).

### 3.5 AdMng Watchdog i Korespondencja

- **Watchdog:** lista kontrolna
  [WATCHDOG-LINUX.md, rozdział 7](WATCHDOG-LINUX.md#7-pilotaż--lista-kontrolna) – przekazywanie zdarzeń z DC do
  OpenWEC, „kto i skąd”, treść GPO „było → jest”, powiadomienia.
- **Korespondencja:** lista kontrolna
  [KORESPONDENCJA.md, rozdział 14](KORESPONDENCJA.md#14-lista-kontrolna-pilotażu) – klasyczny Outlook 32- i 64-bitowy,
  wysyłka jako i w imieniu, obrazy, głosowania, Exchange lokalny i Online.

## 4. Zgłaszanie problemów

**Pomoc → Zgłoś problem…** tworzy opis bez danych katalogu: wersja programu i systemu, sposób połączenia (bez nazw
serwerów i kont), wyniki sprawdzenia gotowości jako kody i ostatnie wpisy dziennika diagnostycznego jako szablony
komunikatów (bez wartości). Dołączcie go do zgłoszenia razem z numerem scenariusza, krokami i tym, co pokazało
narzędzie Microsoftu. Zrzuty ekranu i eksporty z nazwami z katalogu przekazujcie tylko zgodnie z zasadami organizacji.

## 5. Wyniki

| # | Data | Wersja | Wynik (zgodne / różnice / błąd) | Uwagi i numer zgłoszenia |
|---|---|---|---|---|
| A1 | | | | |
| … | | | | |

Pilotaż kończy się, gdy wszystkie scenariusze A, G i E (oraz listy Watchdoga i Korespondencji, jeśli są używane) mają
wynik „zgodne” albo zgłoszone i poprawione różnice; wtedy wersję można uznać za stabilną.
