[English](en/PILOT.md) · **Polski**

# Pilotaż AD Managera – lista kontrolna

Prawie wszystko, co AD Manager potrafi, sprawdzono testami na fałszywym katalogu, laboratorium Samba i w testach
interfejsu na Windows. **Nie sprawdzono jeszcze tego, co wymaga prawdziwego Windows Server AD**: kontrolerów Windows,
GPMC i klientów stosujących zasady grupy, dzienników zdarzeń czytanych przez RPC, zdalnych akcji, urzędów certyfikacji
(AD CS), klasycznego Outlooka i Exchange. Ten dokument prowadzi przez pilotaż: przygotowanie, sprawdzenie gotowości,
scenariusze z oczekiwanym wynikiem i sposób zgłaszania problemów. Szczegółowe listy usługi AdMng Watchdog i
Korespondencji są w ich instrukcjach (odnośniki niżej) – tu ich nie powtarzamy.

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
| Usługi certyfikatów (opcjonalnie, P1) | AD CS z urzędem głównym i urzędem przedsiębiorstwa wystawiającym, listy CRL publikowane w AD i pod adresem HTTP; celowo podatne kopie szablonów tylko w domenie testowej (przepis laboratorium: [WATCHDOG-LINUX.md, 1.7](WATCHDOG-LINUX.md#17-urząd-certyfikacji-w-laboratorium-opcjonalnie)) |
| Narzędzia do porównań | GPMC, `gpresult /h`, `secedit`, LockoutStatus i EventCombMT, Podgląd zdarzeń, ADUC/ADAC, PowerShell `ActiveDirectory` i `GroupPolicy`; dla PKI: `pkiview.msc`, `certtmpl.msc`, `certlm.msc`, `certutil`, Certipy (`certipy find -vulnerable`) i Locksmith |

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

### 3.1 Codzienna praca (fazy 1–3)

| # | Scenariusz | Oczekiwany wynik | Porównaj z |
|---|---|---|---|
| A1 | Połączenie bieżącym kontem i innym kontem (Kerberos, podpisywanie i szyfrowanie; LDAPS) | połączenie bez ostrzeżeń, w pasku stanu DC i sposób zabezpieczenia | – |
| A2 | Duże OU (10 000+ obiektów), przewijanie, wyszukiwanie w trakcie pisania | pierwsza strona < 300 ms w LAN, okno nie zamarza | ADUC |
| A3 | Karty właściwości użytkownika, grupy, komputera (kraj, godziny logowania, wygasanie konta, zakres grupy) | te same wartości co w ADUC | ADUC |
| A4 | Konto helpdesku: pola bez uprawnień | pola tylko do odczytu, zrozumiałe komunikaty (odmowa dostępu, zasady haseł) | ADUC |
| A5 | Edycja zbiorcza 50 kont z podglądem, wykonaniem i *Cofnij* | zmiany jak w podglądzie, cofnięcie przywraca stan | PowerShell |
| A6 | Kosz AD: usunięcie i przywrócenie konta i OU z poddrzewem | obiekty i członkostwa wracają | ADAC |
| A7 | „Żadnych danych katalogu na dysku”: po pracy przejrzyjcie `%APPDATA%\AdMng` i `%TEMP%` | tylko ustawienia, profile bez haseł, definicje widoków | – |
| A8 | *Wiele kont…* w OU testowej: wklejenie z Excela ok. 20 wierszy (*Telefon komórkowy, Imię, Nazwisko, Dział, E-mail*), profil *Konta numerowane (kd201…)*, sufiks UPN, grupa; *Sprawdzenie*, *Podgląd*, *Wykonaj*, *Kopiuj tabelę z hasłami…*; logowanie jednym kontem na stacji testowej; na koniec *Cofnij* | numery od pierwszego wolnego, UPN i atrybuty jak w kroku *Sprawdzenie*, konta włączone z wymuszoną zmianą hasła i członkostwem; pierwsze logowanie wymusza zmianę hasła; cofnięcie usuwa wszystkie utworzone konta (przy włączonym Koszu AD trafiają do Kosza) | ADUC, `Get-ADUser -Properties *` |
| A9 | Ten sam arkusz w kreatorze otwartym kontem z delegowanym tworzeniem kont w OU, ale bez prawa resetu hasła | wykonanie zatrzymuje się po pierwszym koncie (*Wstrzymano po pierwszym koncie…*), konto zostaje wyłączone i bez grup, pozostałe wiersze niewykonane | ADUC |
| A10 | Zamknięcie okna w trakcie wykonania: edytor zbiorczy z 300 obiektami (np. opis), trzy razy – *Zatrzymaj i zamknij*, *Zamknij i kontynuuj w tle*, *Nie zamykaj*; potem okno główne zamknięte, gdy wykonanie trwa w tle, i *Rozłącz*, gdy trwa w tle | *Zatrzymaj i zamknij*: obiekt w toku dokończony, okno zamyka się po końcu, powiadomienie *zatrzymano*; *w tle*: okno znika od razu, wiersz na liście operacji w tle dochodzi do końca, powiadomienie okna głównego (niepowodzenia – okno z przyczynami); *Nie zamykaj*: nic się nie zmienia; okno główne pyta raz, wymienia wykonanie w tle, nie proponuje tła i zamyka się po końcu obiektów w toku; *Rozłącz* czeka na ich koniec; każda zmiana w dzienniku sesji | dziennik sesji, `Get-ADUser -Properties description` |

**Zakładanie wielu kont – do potwierdzenia na kontrolerach Windows** (fałszywy katalog testów i laboratorium Samba z jedną
domeną w lesie tego nie rozstrzygną; szczegóły:
[ZAKLADANIE-KONT.md, rozdział 12](ZAKLADANIE-KONT.md#12-ograniczenia-i-pilotaż)):

- las z kilkoma domenami: unikalność UPN i adresów sprawdzana przez wykaz globalny kontrolera okna (*Unikalność UPN i
  adresów: cały las (wykaz globalny na …)*), *sprawdzone tylko w domenie*, gdy kontroler nie jest wykazem globalnym, i
  kontrola unikalności UPN w lesie, którą Windows wykonuje sam przy zapisie (błąd 0x21C8 → *UPN może być już używany w
  innej domenie lasu.*);
- unikalność nazwy obiektu w kontenerze dla różnych typów (np. grupa *Księgowość* obok jednostki *Księgowość*);
- konta komputerów: KB5020276 (dołączenie do przygotowanego konta przez osobę spoza administratorów) i KB5008102 (zasady
  znaku `$`), przebieg „wyłączone → losowe hasło → włączone” oraz okno *Nowy komputer* (K1), które zakłada konto włączone
  bez hasła;
- wyczerpanie puli RID przy dużej paczce (ostrzeżenie o małej puli, błędy kolejnych kont, *Ponów nieudane*);
- logowanie nowym kontem przez inny kontroler przed zakończeniem replikacji;
- administrator OU z delegowanymi uprawnieniami bez prawa resetu hasła – zatrzymanie po pierwszym koncie (scenariusz A9).

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

### 3.5 Usługi certyfikatów (P1)

Wszystko tylko do odczytu; celowo podatne szablony i wpisy ACL twórzcie wyłącznie w domenie testowej i usuńcie je po
pilotażu.

| # | Scenariusz | Oczekiwany wynik | Porównaj z |
|---|---|---|---|
| P1 | *Usługi kluczy publicznych* w domenie z urzędem głównym i wystawiającym: urzędy, zaufane urzędy i NTAuth, listy CRL, karta *Zdrowie*; potem *Sprawdź adresy HTTP* | stany certyfikatów urzędów, lokalizacji AIA i CDP oraz list podstawowych i różnicowych jak w *PKI przedsiębiorstwa*; adresy HTTP kontaktowane dopiero po przycisku | `pkiview.msc` |
| P2 | *Szablony certyfikatów*: szablony domyślne i ich kopie (wersje schematu 1–4) – flagi, okresy, dostawcy, *Wystawiany przez*, *Kto może się zarejestrować* i *Kto może zmienić szablon*, także z wpisem odmowy i grupą zagnieżdżoną | te same właściwości i uprawnienia co w konsoli; liczby kont zgodne z członkostwem | `certtmpl.msc`, `Get-ADGroupMember -Recursive` |
| P3 | *Przegląd bezpieczeństwa AD CS* w niezmienionym, domyślnym PKI – z okna domeny głównej i domeny podrzędnej | brak uwag; zakładka *Uprawnienia* (*Przegląd kont*) bez delegowań na obiektach PKI | Certipy, Locksmith |
| P4 | Celowo podatne kopie szablonów (ESC1, ESC2, ESC3, ESC4, ESC9, ESC13, ESC15, ESC17), prawo zapisu na kontenerze PKI dla grupy testowej (ESC5), słabe mapowanie konta Tier 0 testowego (ESC14) | każde ustalenie `certipy find -vulnerable` i Locksmith dla ESC z listy LDAP ma odpowiednik; różnice tylko te z [CERTYFIKATY.md, rozdział 5.4](CERTYFIKATY.md#54-różnice-względem-certipy-find--vulnerable-i-locksmith); eksport CSV i HTML na żądanie | `certipy find -vulnerable`, Locksmith |
| P5 | Karta *Certyfikaty LDAPS* na wszystkich DC (także RODC i wykaz globalny), jeden DC bez certyfikatu LDAPS, stacja bez dostępu do internetu | certyfikat i łańcuch jak w magazynie komputera DC; DC bez certyfikatu – *DC bez certyfikatu LDAPS (uzgadnianie TLS nie powiodło się)*; nic nie jest pobierane z sieci | `certlm.msc` na DC |
| P6 | *Gotowość na silne mapowanie i NTAuth* z *Sprawdź certyfikaty LDAPS*: konto ze słabym i konto z silnym mapowaniem, urząd spoza NTAuth wystawiający szablon logowania, logowanie certyfikatem testowym | wiersze zgodne z rzeczywistością; wynik logowania zgodny z oceną (przy pełnym wymuszaniu słabe mapowanie odrzucone) | zdarzenia KDC 39/40/41 w dzienniku *System* DC |
| P7 | Karty *Opublikowane certyfikaty*, *Mapowania nazw* i *Poświadczenia kluczy* użytkownika z certyfikatem S/MIME z Outlooka, mapowaniem z karty ADUC i kluczem Windows Hello dla firm | te same certyfikaty i mapowania co w ADUC; mapowanie z ADUC oznaczone jako słabe; klucze zgodne z `msDS-KeyCredentialLink` | ADUC (*Funkcje zaawansowane*), `Get-ADUser -Properties *` |
| P8 | Edytor atrybutów i zapisany widok z kolumną `msPKIAccountCredentials` na koncie z poświadczeniami mobilnymi | wartość tylko jako maska, *Edytuj* i *Kopiuj* nieaktywne, CSV listy z maską | – |

Do scenariusza A7 dołączcie pracę z dokumentami PKI: poza wskazanymi eksportami nie powstaje żaden plik. Ograniczenia
i szczegóły: [CERTYFIKATY.md, rozdział 11](CERTYFIKATY.md#11-ograniczenia-i-pilotaż).

### 3.6 AdMng Watchdog i Korespondencja

- **Watchdog:** lista kontrolna
  [WATCHDOG-LINUX.md, rozdział 7](WATCHDOG-LINUX.md#7-pilotaż--lista-kontrolna) – przekazywanie zdarzeń z DC do
  OpenWEC, „kto i skąd”, treść GPO „było → jest”, powiadomienia.
- **Korespondencja:** lista kontrolna
  [KORESPONDENCJA.md, rozdział 14](KORESPONDENCJA.md#14-lista-kontrolna-pilotażu) – klasyczny Outlook 32- i 64-bitowy,
  wysyłka jako i w imieniu, obrazy, głosowania, Exchange lokalny i Online.

### 3.7 Okno PowerShell (R1)

Tylko odczyt: okno niczego nie zmienia w AD. Nikt jeszcze nie widział go na ekranie, więc pierwsze scenariusze dotyczą
patrzenia. Uruchom je raz kontem helpdesku i raz administratorem pilotażu.

| # | Scenariusz | Oczekiwany wynik | Porównaj z |
|---|---|---|---|
| PW1 | *Narzędzia → PowerShell…* i **Ctrl+Shift+P** z dwóch okien połączeń; wpisz `Get-AdmUser -Department 'IT' -Pro` i naciśnij **Tab**; obejrzyj okno w motywie jasnym, ciemnym i wysokiego kontrastu przy skalowaniu 125 %–200 % | jedno okno na połączenie, w tytule domena, DC i konto; kolory dobrze się czyta, kursor stoi na tekście, lista uzupełnień otwiera się przy kursorze i nie wychodzi poza ekran | – |
| PW2 | `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer` z kilkoma filtrami (także `-Properties`, `-SearchBase`), 20 obiektów porównanych jeden po drugim | te same obiekty i wartości co na listach programu i w Edytorze atrybutów; daty w czasie lokalnym, flagi jako nazwy | listy programu, `Get-ADUser` |
| PW3 | `Get-AdmGroupMember <grupa> -Recursive` dla grupy z grupami zagnieżdżonymi (i cyklem); z `-ExcludeGroups` | ta sama lista co *Wszystkie (zagnieżdżone)* na stronie *Członkowie*; `-ExcludeGroups` daje tylko konta i komputery | `Get-ADGroupMember -Recursive` |
| PW4 | `Search-AdmObject` z wartościami zawierającymi `(`, `)`, `*`, `\` i polskie litery (utwórz takie obiekty w jednostce testowej) | tylko obiekty z dokładnie taką wartością (albo `-Match StartsWith`); bez dodatkowych wyników | – |
| PW5 | `Get-AdmUser … \| Out-AdmGrid`: sortowanie, filtr, **Enter** na wierszu, **Ctrl+C** do Excela, *Eksportuj listę…*; `Export-AdmList` ze ścieżką, bez ścieżki (*Zapisz jako*), na istniejący plik | siatka i CSV mają te same wartości; formuła w wartości (`=1+1`) nie uruchamia się w Excelu; istniejący plik jest zastępowany tylko z `-Force` | lista obiektów |
| PW6 | Włącz transkrypcję (potem rejestrowanie bloków skryptów) zasadami grupy dla konta pilotażu, otwórz okno, wykonaj polecenie | przed pierwszym poleceniem okno mówi, co jest rejestrowane i gdzie; transkrypcja zawiera polecenia i wyniki; okno nie próbuje tego omijać | GPO, folder transkrypcji, dziennik *PowerShellCore/Operational* |
| PW7 | Komputer z WDAC albo AppLocker wymuszającym ConstrainedLanguage | okno zostaje w trybie ograniczonym, przełącznik *Tryb zaufany* jest wyłączony z podanym powodem, polecenia AD Managera i `ForEach-Object` okna działają | `$ExecutionContext.SessionState.LanguageMode`; `1, 2 \| ForEach-Object { $_ * 2 }` |
| PW8 | Tryb zaufany: włącz, zdefiniuj funkcję z wywołaniem .NET (`function Get-Tmp { [System.IO.Path]::GetTempPath() }`) i ją wywołaj, wyłącz tryb, wywołaj ją znowu; potem `cls` i `exit 3`; zamknij okno, otwórz nowe | najpierw pytanie z nazwą domeny, które mówi też, że wyłączenie uruchamia nową sesję; tytuł i uwaga mówią *tryb zaufany*; przy wyłączaniu wiersz stanu przez chwilę mówi *Wyłączanie trybu zaufanego…*, okno pisze, że sesja jest nowa, a `Get-Tmp` jest nierozpoznane; `cls` czyści okno, `exit 3` mówi o kodzie wyjścia, a okno zostaje otwarte; nowe okno znów jest w trybie ograniczonym | – |
| PW9 | Polecenie, które zwraca 100 000 obiektów, potem **Zatrzymaj**; zamknięcie okna w trakcie polecenia | okno pozostaje responsywne, Zatrzymaj kończy odczyt na DC, zamknięcie zadaje pytanie; po zamknięciu w profilu i w *Temp* nie zostaje plik z danymi katalogu (sprawdzenie AppLockera przez silnik tworzy i od razu usuwa w *Temp* dwa pliki `__PSScriptPolicyTest_*` przy otwarciu pierwszego okna – wiersz komentarza, żadnych danych z katalogu) | Process Monitor |
| PW10 | Narrator albo NVDA w oknie | wiersz poleceń, wyniki i wiersz stanu są czytane; wiersz błędu albo ostrzeżenia jest czytany z „Błąd:” / „Ostrzeżenie:” na początku; lista uzupełnień nie jest ogłaszana (znane) | – |
| PW11 | `while ($true) { 'x' }`, potem kliknięcie **Zatrzymaj** myszą; jeszcze raz i zatrzymanie przez **Ctrl+C**; jeszcze raz, **Ctrl+L** w trakcie napływu wierszy, potem **Zatrzymaj** | okno przyjmuje kliknięcia i klawisze, choć wiersze napływają bez przerwy, Zatrzymaj i Ctrl+C kończą polecenie w ciągu sekundy, Ctrl+L czyści wyniki bez komunikatu o błędzie (dotąd okno zalewał tylko test automatyczny) | – |

Ograniczenia i szczegóły: [POWERSHELL.md, rozdział 12](POWERSHELL.md#12-ograniczenia-i-pilotaż).

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

Pilotaż kończy się, gdy wszystkie scenariusze A, G, E i P (oraz listy Watchdoga i Korespondencji, jeśli są używane)
mają wynik „zgodne” albo zgłoszone i poprawione różnice; wtedy wersję można uznać za stabilną.
