# Komputery w AD Managerze (etap K1)

Ten dokument opisuje pracę z kontami komputerów: **Przegląd komputerów** (inwentarz i raporty), kartę **Stan** we
właściwościach komputera, **cykl życia kont** (przygotowanie konta przed dołączeniem, reset, wyłączanie, przenoszenie,
usuwanie) oraz **zdalne akcje** przez WinRM. Projekt techniczny: [docs/dev/k-computers-and-gpo.md](dev/k-computers-and-gpo.md)
(rozdział 4).

Zasady, które obowiązują wszędzie:

- **Nic z katalogu nie trafia na dysk.** Wyniki przeglądu, adresy IP, lokacje, wyniki zdalnych akcji i dziennik okna
  istnieją tylko w pamięci okna. Plik powstaje wyłącznie wtedy, gdy operator wybierze *Eksportuj CSV…*.
- **Każda zmiana w AD przechodzi przez plan zmian** (podgląd, walidacja, dziennik sesji, cofnięcie tam, gdzie się da).
- **Jeden kontroler domeny na okno.** Wszystkie odczyty i zmiany idą do kontrolera okna. Wyjątek to *Dokładne ostatnie
  logowanie*, które z założenia pyta każdy kontroler (krótkie sesje tylko do odczytu). Nazwy DNS komputerów rozwiązuje
  resolver stacji operatora, nie kontroler domeny.
- **Hasła tylko w pamięci.** Zdalne akcje i narzędzia używają poświadczeń okna. Nie są nigdzie zapisywane, nie trafiają do
  wiersza poleceń, zmiennych środowiskowych ani Menedżera poświadczeń.

## 1. Przegląd komputerów

*Narzędzia → Przegląd komputerów…* (paleta poleceń: `tools.computerReview`). W każdym oknie połączenia jest jedno takie
okno.

**Zakres:** cała domena, jednostka organizacyjna (z poddrzewem), zapisany widok albo członkowie grupy (jak w *Przeglądzie
kont*). *Sprawdź* (F5) czyta konta komputerów jednym stronicowanym zapytaniem (`sAMAccountType=805306369`, bez kont usług
zarządzanych). Wyniki pojawiają się na bieżąco, a *Przerwij* zatrzymuje odczyt.

**Raporty** (kafelki z liczbą komputerów):

| Kafelek | Co pokazuje |
|---|---|
| *Systemy po końcu wsparcia* | system po końcu wsparcia albo kończący się w ciągu N dni (domyślnie 90) według katalogu wersji |
| *Wersje Windows 11 i Server* | osobny kafelek dla każdej wersji (np. *Windows 11 24H2*) z liczbą komputerów |
| *Nieaktywne od N dni* | `lastLogonTimestamp` starszy niż N dni (domyślnie 90). Wartość jest replikowana z opóźnieniem do 14 dni, więc przed decyzją warto użyć *Dokładnego ostatniego logowania*. |
| *Nigdy niezalogowane* | konta utworzone ponad N dni temu, na które nikt się nie zalogował (zwykle przygotowane i niedołączone) |
| *Serwery bez właściciela* | serwer bez `managedBy` albo z właścicielem wyłączonym lub usuniętym |
| *Komputery w niewłaściwych OU* | komputer w domyślnym kontenerze (np. `CN=Computers`), kontroler domeny poza `OU=Domain Controllers`, inny komputer w tej OU oraz reguły z ustawień |
| *Wyłączone* | wyłączone konta komputerów (informacyjnie) |
| *Bez systemu operacyjnego* | konta bez `operatingSystem` (przygotowane albo systemy inne niż Windows) |

**Nieaktywne komputery to tylko raport.** AD Manager niczego nie wyłącza ani nie usuwa sam. Operator zaznacza wiersze i
wybiera *Wyłącz…*, *Przenieś…* albo *Usuń…*. Każda z tych akcji pokazuje plan zmian i wymaga potwierdzenia.

**Katalog wersji Windows.** O wersji decyduje **numer kompilacji**, a nie nazwa systemu. Wczesne kompilacje Windows 11
zapisywały w `operatingSystem` „Windows 10 …”, więc kompilacja 22000 lub wyższa oznacza Windows 11. Nazwa systemu
określa rodzinę (Server) i klasę wydania (Home/Pro, Enterprise/Education, LTSC, IoT LTSC, Server), bo daty końca wsparcia
różnią się między wydaniami. Okno podaje datę katalogu (*Katalog wersji z dnia …*) i ostrzega, gdy ma on ponad 9
miesięcy. Własne daty, na przykład po wykupieniu ESU dla Windows 10, wpisuje się w *Ustawienia → Komputery*. Mają one
pierwszeństwo przed katalogiem.

**Kolumny i szczegóły.** Każdy kafelek ma własne kolumny: nazwa z ikoną typu (stacja, serwer, kontroler domeny,
wyłączony), nazwa DNS, typ, system, wersja, stan wsparcia z datą, ostatnie logowanie, dokładne ostatnie logowanie,
lokacja i podsieć, twórca (`mS-DS-CreatorSID`), właściciel (`managedBy`), lokalizacja, opis i OU. Panel szczegółów pokazuje
wszystkie fakty o zaznaczonym komputerze, ustalenia raportów i wiersz katalogu. Kliknięcie nagłówka sortuje listę.

**Akcje** (dla zaznaczenia, a gdzie ma to sens – dla całej listy):

- *Właściwości*, *Edycja zbiorcza…*, *Eksportuj CSV…* (plik wskazany przez operatora, separator z ustawień regionalnych,
  ochrona przed formułami, kolumna DN).
- *Dokładne ostatnie logowanie* – `lastLogon` z każdego kontrolera domeny (do 500 komputerów, najwyżej 4 kontrolery
  naraz). Komputer, który okazuje się aktywny, znika z raportów nieaktywności.
- *Ustal lokację* – nazwa DNS komputera jest rozwiązywana resolverem stacji operatora (limit 2 s, 16 zapytań naraz), a
  adres dopasowywany do podsieci lasu (najdłuższy prefiks, także IPv6). Lokacja kontrolera domeny to lokacja jego obiektu
  serwera. Przy ponad 500 komputerach okno najpierw pyta o zgodę. Komunikaty: *brak podsieci*, *nie rozwiązano*.
- *Zdalne akcje…*, *Pulpit zdalny*, *Ping* – rozdział 4.
- *Ustaw właściciela…* (`managedBy` z okna wyboru), *Wyłącz…*, *Przenieś…*, *Resetuj konto…*, *Usuń…* – rozdział 3.

Po każdej opublikowanej zmianie zmienione wiersze są odczytywane ponownie.

## 2. Właściwości komputera

- Karta *Ogólne* pokazuje też **typ** komputera, a karta *System operacyjny* – **wersję** z katalogu i **stan wsparcia**
  z datą.
- Karta **Stan**: ostatnie logowanie (przybliżone) z przyciskiem *Sprawdź na wszystkich DC*, lokacja z przyciskiem
  *Ustal*, twórca konta, właściciel obiektu (z deskryptora zabezpieczeń), data odnowienia hasła konta i informacja, czy
  komputer ma LAPS (tylko obecność daty wygaśnięcia – **hasło LAPS nie jest odczytywane**). Na dole karty są przyciski
  *Zdalne akcje…*, *Pulpit zdalny*, *Zarządzanie komputerem*, *Podgląd zdarzeń* i *Ping*.

## 3. Cykl życia kont komputerów

### Przygotowanie konta (Nowy → Komputer)

Kreator *Nowy → Komputer* ma trzy dodatkowe pola:

- **Użytkownik lub grupa, która może dołączyć ten komputer do domeny.** Domyślnie nikt, czyli dołączyć mogą tylko
  administratorzy. Wybranie osoby lub grupy dodaje do planu, zaraz po utworzeniu konta, krok **zmiany uprawnień** z
  czterema wpisami ACE, które zapisuje ADUC: *Resetowanie hasła*, odczyt i zapis zestawu *Ograniczenia konta*, zatwierdzony
  zapis nazwy hosta DNS i zatwierdzony zapis SPN (zezwolenie, bez dziedziczenia).
- **Zarządzany przez** (`managedBy`) i **Lokalizacja** (`location`).

Opcji ADUC *Przypisz to konto komputera jako komputer sprzed Windows 2000* celowo nie ma. Ustawia ona hasło pochodne od
nazwy, a przewidywalne hasła kont komputerów to znana ścieżka ataku.

**Utwardzenie dołączania do domeny (KB5020276).** Od aktualizacji z lat 2022–2023 ponowne użycie istniejącego konta
komputera udaje się tylko wtedy, gdy:

- dołączający utworzył to konto,
- dołączający jest administratorem domeny lub przedsiębiorstwa albo wbudowanym Administratorem,
- właściciel konta należy do tych grup,
- właściciela wskazuje zasada *Kontroler domeny: Zezwalaj na ponowne użycie konta komputera podczas dołączania do domeny*.

Właścicielem przygotowanego konta jest konto okna AD Managera. Jeśli to konto nie jest administratorem, a dołączać ma
ktoś inny, podsumowanie kreatora uprzedza, że bez tej zasady dołączenie się nie uda.

### Resetuj konto

*Komputer → Resetuj konto…* ustawia kontu komputera losowe hasło o długości 120 znaków. Nikt go nie widzi: nie trafia do
podglądu (maska), raportu, schowka ani eksportu PowerShell. Podgląd ostrzega, że **komputer straci relację zaufania z
domeną** – trzeba go dołączyć ponownie albo naprawić kanał poleceniem `Test-ComputerSecureChannel -Repair`. Potwierdzenie
wymaga wpisania nazwy domeny. Kont kontrolerów domeny nie da się resetować (do tego służy obniżenie roli kontrolera).

### Wyłącz, przenieś, usuń

Akcje działają jak dla innych obiektów, z dodatkowymi regułami dla komputerów:

- wyłączenie, usunięcie albo reset konta **kontrolera domeny** to błąd planu,
- przeniesienie kontrolera domeny poza `OU=Domain Controllers` to ostrzeżenie: przestaje go wtedy obejmować *Default Domain
  Controllers Policy*,
- usunięcie komputera z obiektami podrzędnymi (informacje odzyskiwania BitLocker, obiekty Hyper-V lub klastra) usuwa całe
  poddrzewo. Potwierdzenie ostrzega, że **hasła odzyskiwania BitLocker zostaną usunięte razem z kontem**.

## 4. Zdalne akcje (WinRM, Kerberos)

*Komputer → Zdalne akcje…* otwiera okno komputera (jedno na komputer w oknie połączenia). W nagłówku okna widać nazwę
komputera, jego nazwę DNS i sposób połączenia, np. *Połączenie: WinRM (HTTP 5985, Kerberos) jako CONTOSO\admin*.

| Akcja | Co robi |
|---|---|
| *Sprawdź stan* (F5) | ping (4 próby), dostępność WinRM, system zgłaszany przez sam komputer (z numerem poprawki UBR) i czas ostatniego uruchomienia |
| *Pokaż zalogowanych* | konto przy konsoli i właściciele procesów `explorer.exe` z numerem sesji. CIM nie zna stanu sesji, więc rozłączone sesje RDP też są na liście. |
| *Pokaż usługi* | usługi z trybem uruchamiania i kontem, z filtrem *automatyczne, zatrzymane*. Uruchamianie i zatrzymywanie usług nie należy do K1. |
| *Odśwież zasady grupy* | `gpupdate /target:computer /force` na komputerze. Okno czeka na zakończenie (do 10 minut) i podaje czas z rejestru zasad. Odświeżenie zasad zalogowanych użytkowników (zadanie zaplanowane) włącza się w ustawieniach – do potwierdzenia w pilotażu. |
| *Uruchom ponownie…* | opóźnienie 0–1440 minut (domyślnie 10), komunikat do 500 znaków (wyświetlany zalogowanym użytkownikom dosłownie), opcja *Zamknij aplikacje bez pytania*. Nazwę komputera trzeba wpisać dla wszystkiego poza znaną stacją roboczą: serwera, kontrolera domeny i komputera nieznanego typu (bez `operatingSystem` – na przykład obiekt nazwy klastra, którego ponowne uruchomienie trafia do węzła będącego jego właścicielem). |
| *Anuluj zaplanowane ponowne uruchomienie* | `shutdown /a` na komputerze. Okno nie zna wyniku polecenia, więc zapisuje tylko, że anulowanie zlecono – jeśli żadne ponowne uruchomienie nie było zaplanowane, nic się nie zmienia. |

Ważne zasady:

- **Tylko Kerberos.** Bez awaryjnego przejścia na NTLM, więc poświadczenia administratora nigdy nie trafiają do komputera
  przez NTLM. Komputer musi mieć w AD nazwę DNS (`dNSHostName`). Adresy IP nie są obsługiwane, bo Kerberos ich nie
  przyjmuje.
- **Poświadczenia okna.** Okno zalogowane bieżącym kontem Windows używa tokenu procesu. Okno z innymi poświadczeniami
  przekazuje je do WinRM jako kopię hasła z pamięci, usuwaną po każdej akcji. Okno z prostym wiązaniem po DN nie ma konta
  Windows i zdalnych akcji nie oferuje.
- **Wymagane uprawnienia:** konto okna musi być **lokalnym administratorem** komputera (na kontrolerach domeny: Domain
  Admins). Brak uprawnień okno zgłasza jako *Odmowa dostępu*.
- **Limit czasu** to 30 s (ustawienie: 5–300 s). Zamknięcie okna albo okna połączenia przerywa trwające wywołanie.
- **Dziennik okna** istnieje tylko w pamięci. Każda akcja zmieniająca stan trafia do dziennika sesji (bez treści
  komunikatu o restarcie). Jej odpowiednik PowerShell (`New-CimSession … -Authentication Kerberos`, `Invoke-CimMethod …`)
  trafia do historii PowerShell – bez hasła.
- Komunikaty o błędach mówią, co sprawdzić: *komputer nie odpowiada*, *WinRM niedostępny*, *uwierzytelnianie Kerberos
  nie powiodło się* (brak SPN, różnica czasu, złe konto), *odmowa dostępu*, *przekroczony limit czasu*.

### Narzędzia

- **Ping** – w samym programie, bez poświadczeń.
- **Pulpit zdalny** – `mstsc /v:<nazwa DNS>`. **Nie dostaje żadnych poświadczeń:** mstsc używa zapisanych poświadczeń
  operatora albo o nie pyta. AD Manager niczego nie zapisuje w Menedżerze poświadczeń.
- **Zarządzanie komputerem** i **Podgląd zdarzeń** – `compmgmt.msc /computer:` i `eventvwr`. Okno zalogowane innym kontem
  uruchamia je tak jak `runas /netonly`: lokalnie działa konto Windows operatora, a w sieci – poświadczenia okna. Przed
  uruchomieniem okno o to pyta. Poświadczenia żyją wtedy w pamięci sesji logowania narzędzia do jego zamknięcia, nigdy na
  dysku.

## 5. Włączenie WinRM na komputerach zasadą grupy

Zdalne akcje potrzebują usługi WinRM i jej odbiornika HTTP (5985) z szyfrowaniem Kerberos. Przykładowy przepis GPO dla
OU ze stacjami roboczymi i serwerami (nazwy z polskiej wersji Windows, w nawiasach – angielskie):

1. *Konfiguracja komputera → Zasady → Ustawienia systemu Windows → Ustawienia zabezpieczeń → Usługi systemowe*:
   **Zdalne zarządzanie systemem Windows (WS-Management)** (*Windows Remote Management (WS-Management)*) – tryb
   uruchamiania **Automatyczny**.
2. *Konfiguracja komputera → Zasady → Szablony administracyjne → Składniki systemu Windows → Zdalne zarządzanie systemem
   Windows (WinRM) → Usługa WinRM*: **Zezwalaj na zdalne zarządzanie serwerem przy użyciu usługi WinRM** (*Allow remote
   server management through WinRM*) – **Włączone**. Filtr IPv4: tylko podsieć stacji administratorów (np.
   `10.10.5.0-10.10.5.255`), filtr IPv6 pusty albo odpowiedni zakres.
3. *Konfiguracja komputera → Zasady → Ustawienia systemu Windows → Ustawienia zabezpieczeń → Zapora Windows Defender z
   zabezpieczeniami zaawansowanymi → Reguły przychodzące*: nowa reguła wstępnie zdefiniowana **Zdalne zarządzanie systemem
   Windows** (*Windows Remote Management*). Zostaw tylko **Zdalne zarządzanie systemem Windows (HTTP — ruch przychodzący)**
   dla profilu *Domena*, a we właściwościach reguły, na karcie *Zakres*, ogranicz adresy zdalne do podsieci
   administratorów.
4. Uprawnienia: konto (grupa) administratorów stacji musi być w lokalnej grupie *Administratorzy*, np. przez *Grupy z
   ograniczeniami* albo preferencje GPO *Lokalni użytkownicy i grupy*.

Sprawdzenie z komputera administratora:

```powershell
Test-WSMan -ComputerName pc01.contoso.test -Authentication Kerberos
```

HTTPS (5986) wymaga certyfikatu na każdym komputerze i odbiornika HTTPS. Włącza się je w *Ustawienia → Komputery*
(*Używaj HTTPS*). Kerberos szyfruje ruch także po HTTP.

## 6. Ustawienia (karta „Komputery”)

*Ustawienia → Komputery*:

- **WinRM:** limit czasu (5–300 s), HTTPS zamiast HTTP, własny port (puste: 5985 albo 5986) i odświeżanie zasad
  zalogowanych użytkowników (domyślnie wyłączone do czasu pilotażu).
- **Ponowne uruchomienie:** domyślne opóźnienie i domyślny komunikat (puste: tekst wbudowany w języku interfejsu).
- **Własne daty końca wsparcia:** kompilacja, klasa wydania i data. Mają pierwszeństwo przed katalogiem. Tu też ustawia się
  horyzont ostrzeżenia (dni naprzód).
- **Oczekiwane kontenery** stacji roboczych i serwerów: jeden kontener w wierszu (razem z poddrzewem), `{domain}` oznacza
  domenę połączenia, np. `OU=Stacje robocze,{domain}`. Puste pole wyłącza sprawdzanie danego typu.

Ustawienia nie zawierają nazw komputerów ani wyników. Parametry raportów, wybrane raporty i zakres ostatniego przeglądu
zapamiętuje samo okno przeglądu.

## 7. Ograniczenia i pilotaż

Do potwierdzenia na Windows Server 2025 i Windows 11 24H2/25H2 (laboratorium Samba tego nie sprawdzi):

- `Win32ShutdownTracker` z komunikatem i opóźnieniem wywoływany przez zdalnego administratora bez podniesienia uprawnień,
- odświeżanie zasad użytkowników zadaniem zaplanowanym (`RefreshUserPolicy`),
- CIM tylko z Kerberosem i jawnymi poświadczeniami innego konta niż konto Windows,
- `CreateProcessWithLogonW` z `RunAsInvoker` dla `mmc`,
- reguły KB5020276 dla przygotowanych kont,
- inwentarz zgodny z `Get-ADComputer` na prawdziwej domenie.

Daty końca wsparcia w katalogu trzeba okresowo porównywać ze stronami Microsoft Lifecycle (okno ostrzega po 9
miesiącach). Uruchamianie i zatrzymywanie usług, inwentarz oprogramowania, Wake-on-LAN i odczyt haseł LAPS nie należą do
K1.
