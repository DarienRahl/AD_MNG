# Zasady grupy (GPO) w AD Managerze

Od etapu **K2** AD Manager zarządza obiektami zasad grupy bez konsoli GPMC: drzewo GPO w oknie głównym, karta
*Zasady grupy* przy domenie, OU i lokacji, tworzenie, kopiowanie, zmiana nazwy i usuwanie GPO, łączenie z kontenerami
(kolejność, wymuszanie, wyłączanie połączeń, blokowanie dziedziczenia), stan GPO, filtrowanie zabezpieczeń, filtry
WMI, delegowanie, kopie zapasowe zgodne z GPMC (przywracanie i import ustawień) oraz porównanie dwóch GPO.

**Każda zmiana – w AD i w SYSVOL – przechodzi przez podgląd planu zmian** (zasada 5): najpierw widać, co się zmieni
(atrybuty, pliki z różnicami, wersje GPO, uprawnienia folderu) i odpowiednik w PowerShell, potem *Wykonaj*, wynik i
*Cofnij*. Edycji samych ustawień (szablony ADMX, ustawienia zabezpieczeń) K2 jeszcze nie ma – to etapy K3 i K4; karta
*Ustawienia* pokazuje je tylko do odczytu.

Specyfikacja dla programistów (po angielsku): [dev/k-computers-and-gpo.md](dev/k-computers-and-gpo.md), rozdziały 3 i
5 oraz *K2 as built* (15).

---

## 1. Gdzie to jest w programie

| Miejsce | Co daje |
|---|---|
| Drzewo, pod domeną | węzły **Obiekty zasad grupy** (wszystkie GPO domeny, alfabetycznie), **Filtry WMI** i **Lokacje** (z lokacjami jako węzłami) – zawsze na górze, przed kontenerami domeny |
| *Widok → Zasady grupy w drzewie* | ukrywa i pokazuje te węzły (ustawienie zapamiętywane) |
| *Akcja → Zasady grupy* | *Zasady grupy* bieżącego kontenera, *Utwórz GPO w tej domenie i połącz je tutaj…*, *Połącz istniejący GPO…*, *Zablokuj dziedziczenie*, *Obiekty zasad grupy*, *Filtry WMI* |
| Menu kontekstowe domeny i OU w drzewie | te same polecenia dla klikniętego kontenera (lokacja: bez blokowania dziedziczenia) |
| Menu kontekstowe GPO w drzewie | *Otwórz*, *Stan GPO*, *Kopia zapasowa…*, *Kopiuj…*, *Zmień nazwę*, *Usuń…* (niedostępne dla dwóch domyślnych GPO), *Porównaj z…*, *Historia w Watchdogu* |
| Lista obiektów | przycisk **Zasady grupy** na pasku (dla domeny i OU, także dla jednej zaznaczonej OU) i pozycja w menu kontekstowym |
| Paleta poleceń (Ctrl+K) | `gpo.objects`, `gpo.wmiFilters`, `gpo.containerPolicies`, `gpo.newLinked`, `gpo.linkExisting`, `gpo.blockInheritance`, `view.groupPolicyNodes` |

Każdy widok GPO otwiera się jako karta obok list obiektów (jedna karta na cel – drugie kliknięcie wraca do niej).

## 2. Karty

### Obiekty zasad grupy

Wszystkie GPO domeny: nazwa, stan (włączone / wyłączona część użytkownika / komputera / wszystko), filtr WMI,
połączenia, wersje w AD i w `GPT.INI` (znacznik przy niezgodności), właściciel, data zmiany. Wybór wielu wierszy.
Polecenia: *Nowe…*, *Kopiuj…*, *Zmień nazwę*, *Usuń…*, *Kopia zapasowa…* (zaznaczone GPO), *Przywróć z kopii
zapasowej…*, *Importuj ustawienia…*, *Porównaj* (dokładnie dwa zaznaczone), *Historia w Watchdogu*.

### Jeden obiekt GPO (pięć zakładek)

- **Zakres** – połączenia (kontener, wymuszone, włączone; *Wymuszone*, *Połączenie włączone*, *Usuń połączenie*,
  przejście do kontenera), **Filtrowanie zabezpieczeń** (*Dodaj…* / *Usuń*) i **Filtrowanie WMI** (lista filtrów domeny,
  *Zastosuj*).
- **Szczegóły** – domena, właściciel, daty, wersje użytkownika i komputera w AD i w SYSVOL, GUID, **Stan GPO**
  (włączone, wyłączone ustawienia użytkownika, komputera, wszystkie).
- **Ustawienia** – ustawienia z plików GPO (szablon zabezpieczeń, `audit.csv`, `Registry.pol`, preferencje, skrypty,
  komentarz) w postaci „Komputer / Zabezpieczenia / …”, tylko do odczytu; hasła preferencji (`cpassword`) i wartości
  rejestru wyglądające na hasła są zamaskowane.
- **Delegowanie** – kto ma jaki poziom uprawnień GPMC: *Odczyt*, *Odczyt (z filtrowania zabezpieczeń)*, *Edycja
  ustawień*, *Edycja ustawień, usuwanie, modyfikowanie zabezpieczeń*, *Niestandardowe*; *Dodaj…*, *Zmień poziom*,
  *Usuń*. Wpisy odziedziczone są tylko pokazywane.
- **Stan** – spójność AD i SYSVOL: SYSVOL dostępny, folder GPO istnieje, wersje AD i `GPT.INI`, uprawnienia folderu
  zgodne z uprawnieniami GPO w AD, nazwy rozszerzeń zgodne z plikami, pozostałości przerwanego zapisu. Przyciski
  **Wyrównaj wersje** i **Napraw uprawnienia folderu**. GPO usunięte z AD z pozostawionym folderem: *Usuń pozostały
  folder*.

### Zasady grupy kontenera (domena, OU, lokacja)

- **Połączone obiekty zasad grupy** – kolejność łączy (1 = najwyższe pierwszeństwo), GPO, wymuszone, połączenie
  włączone, stan GPO, filtr WMI, data zmiany; *Wyżej* / *Niżej* (Alt+↑ / Alt+↓), *Wymuszone*, *Połączenie włączone*,
  *Usuń połączenie* (Delete), *Połącz istniejący GPO…*, *Utwórz GPO w tej domenie i połącz je tutaj…*; dwuklik
  otwiera GPO.
- **Zablokuj dziedziczenie** (nie dla lokacji).
- **Dziedziczenie zasad grupy** – GPO stosowane w tym miejscu od najwyższego pierwszeństwa, z miejscem połączenia i
  znacznikiem wymuszenia; blokada dziedziczenia przepuszcza tylko wymuszone GPO z wyższych poziomów. Połączenia lokacji
  są pokazywane tylko dla lokacji (jak w GPMC).

### Filtry WMI

Filtry domeny z zapytaniami, autorem, datą i listą GPO, które ich używają; *Nowy…*, *Edytuj…*, *Usuń…* (usunięcie
najpierw odłącza filtr od tych GPO). Edytor filtru: nazwa, opis i zapytania WQL (przestrzeń nazw + zapytanie). Próby
zapytania na wybranym komputerze jeszcze nie ma – wymaga rozszerzenia zdalnych akcji z etapu K1 (CIM przez WinRM,
[KOMPUTERY.md](KOMPUTERY.md)) o zapytania WQL.

## 3. Operacje

| Operacja | Co zmienia | Odpowiednik w PowerShell |
|---|---|---|
| Nowe GPO (opcjonalnie od razu połączone) | obiekt `groupPolicyContainer` z `CN=Machine` i `CN=User`, folder `{GUID}` w SYSVOL z `GPT.INI`, uprawnienia folderu, połączenie | `New-GPO`, `New-GPLink` |
| Kopiuj | nowe GPO z plikami, stanem i filtrem WMI źródła; uprawnienia domyślne albo – na życzenie – dokładnie jawne wpisy źródła (jego filtrowanie zabezpieczeń zostaje) | `Copy-GPO [-CopyAcl]` |
| Zmień nazwę | `displayName` | `Rename-GPO` |
| Usuń | połączenia w domenie i lokacjach, obiekt GPO, folder SYSVOL; dwóch domyślnych GPO nie da się usunąć; w lesie z innymi domenami podgląd ostrzega, że ich połączenia zostają | `Remove-GPO`, `Remove-GPLink` (lokacje) |
| Połącz / odłącz / kolejność / wymuszone / włączone | `gPLink` kontenera | `New-GPLink`, `Remove-GPLink`, `Set-GPLink` |
| Zablokuj dziedziczenie | `gPOptions` domeny lub OU | `Set-GPInheritance` |
| Stan GPO | `flags` | `(Get-GPO …).GpoStatus = …` |
| Filtr WMI GPO | `gPCWQLFilter` | `(Get-GPO …).WmiFilter = …` |
| Filtrowanie zabezpieczeń, delegowanie | uprawnienia obiektu GPO i, zgodnie z nimi, folderu SYSVOL | `Set-GPPermission` |
| Filtry WMI: nowy / zmiana / usunięcie | obiekty `msWMI-Som` | `New-ADObject`, `Set-ADObject`, `Remove-ADObject` |
| Wyrównaj wersje, napraw uprawnienia folderu | `versionNumber` i `GPT.INI`; uprawnienia folderu SYSVOL | – |

**Usuwanie GPO** wymaga przepisania nazwy GPO; komunikat podaje domenę. Najpierw okno proponuje kopię zapasową
(*Utwórz kopię zapasową przed usunięciem* – domyślnie włączone, wybór jest zapamiętywany). Gdy folderu nie udało się
usunąć, karta GPO pokazuje *Usuń pozostały folder* (odmawia, gdy GPO znów istnieje). *Cofnij* po usunięciu GPO lub
filtru WMI nie przywraca połączeń ani filtrów wskazujących usunięty obiekt.

**Ustawienia przechowywane w katalogu** – instalacja oprogramowania (magazyn klas), zasady sieci bezprzewodowej i
przewodowej – leżą pod `CN=Machine`/`CN=User` obiektu GPO, nie w SYSVOL. Kopiowanie, kopia zapasowa, przywracanie i
import ich nie przenoszą; podgląd i okno kopii zapasowej mówią o tym wprost.

**Filtrowanie zabezpieczeń i MS16-072.** Od czerwca 2016 komputery czytają GPO użytkownika w kontekście komputera.
Usunięcie *Użytkowników uwierzytelnionych* z filtrowania zostawia im prawo *Odczyt* (jak GPMC), a podgląd ostrzega,
gdy ani *Użytkownicy uwierzytelnieni*, ani *Komputery domeny* nie mogą odczytać GPO.

**Uprawnienia operatora.** Program nie sprawdza z góry, czy konto okna może zmienić dany GPO lub kontener: zmiana bez
prawa kończy się błędem w wynikach podglądu, a zmiany w SYSVOL zostają cofnięte (GPO jak przedtem).

## 4. Podgląd planu zmian i bezpieczeństwo zapisu

Okno podglądu ma trzy zakładki: **Zmiany** (atrybuty AD, pliki SYSVOL z rozmiarami, wersja GPO, folder i jego
uprawnienia; *Pokaż różnice* dla pliku – ustawienia z zamaskowanymi sekretami albo wiersze tekstu), **PowerShell**
(odpowiedniki z cmdletami modułu `GroupPolicy`, z kontrolerem okna) i **Wyniki**. Ostrzeżenia (np. MS16-072, hasła
GPP, odwołania do innej domeny) są przy wierszach.

Zapis GPO jest transakcją na jednym kontrolerze domeny – tym, z którym pracuje okno (zasada 4):

1. nowe pliki są zapisywane obok docelowych jako pliki tymczasowe i sprawdzane,
2. dopiero potem podmieniane, a na końcu podnoszona jest wersja GPO w `GPT.INI` i w AD (`versionNumber`, porównanie
   z wartością sprzed planu – gdy ktoś inny zmienił GPO w międzyczasie, zmiana jest odrzucana i pliki wracają do stanu
   sprzed planu),
3. błąd w trakcie przywraca poprzednie pliki z pamięci; gdyby to się nie udało, karta *Stan* pokazuje niespójność i
   *Wyrównaj wersje*.

Wersje rosną zawsze (także przy cofaniu), więc klienci zawsze pobierają nowy stan. *Cofnij* w oknie podglądu pokazuje
plan odwrotny i wykonuje go tak samo.

## 5. Kopie zapasowe, przywracanie i import

**Format zgodny z GPMC**: `manifest.xml` (scalany z istniejącym), w folderze `{BackupId}` pliki `Backup.xml`,
`bkupInfo.xml`, `DomainSysvol\GPO\…` (pliki GPO bajt w bajt) oraz `admng.json` AD Managera (sumy SHA-256 plików,
połączenia GPO w chwili kopii, definicja filtru WMI). Dzięki temu kopię odtworzy też `Restore-GPO` lub GPMC, a AD
Manager odtwarza kopie zrobione w GPMC (także pakiety Microsoft Security Compliance Toolkit przez *Importuj ustawienia*).
Zgodność w obie strony potwierdzi pilotaż na kontrolerze Windows – okno kopii mówi o tym wprost.

- **Kopia zapasowa** – zapisuje wyłącznie do folderu wskazanego przez operatora (lokalnego lub UNC; *Przeglądaj…*,
  folder jest zapamiętywany), z komentarzem. Postęp i wynik widać w centrum aktywności; okno pokazuje odpowiednik
  `Backup-GPO`. Katalog nie jest zmieniany, a nieudana kopia usuwa to, co zdążyła zapisać.
- **Przywróć z kopii zapasowej** – lista kopii w folderze (nazwa, domena, czas, komentarz); istniejące GPO dostaje
  pliki, nazwę, stan, nazwy rozszerzeń, filtr WMI (jeśli istnieje) i jawne uprawnienia z kopii, wersje rosną; usunięte
  GPO powstaje ponownie z tym samym GUID i dokładnie jawnymi uprawnieniami z kopii, z wersją powyżej ostatniej wersji
  usuniętego GPO (Kosz AD ją przechowuje; bez Kosza wersja rośnie o 1001 ponad wersję z kopii). Pozostały folder
  SYSVOL z tym samym GUID trzeba najpierw usunąć. **Przywróć też połączenia (N)** – opcja, domyślnie wyłączona (GPMC nie
  przywraca połączeń). Kopia z innej domeny nie jest przywracana – można zaimportować jej ustawienia.
- **Importuj ustawienia** – do wybranego GPO trafia tylko zawartość (pliki, nazwy rozszerzeń, wersja), bez nazwy,
  stanu, filtru WMI i uprawnień. Podmioty zabezpieczeń i ścieżki UNC innej domeny są wymieniane w ostrzeżeniu (tabel
  migracji K2 nie obsługuje).
- **Odczyt kopii jest odczytem niezaufanych danych**: XML bez DTD, ścieżki tylko wewnątrz `DomainSysvol\GPO`, limity
  rozmiaru, sumy SHA-256 z `admng.json` sprawdzane, gdy plik istnieje. Hasła preferencji (`cpassword`, MS14-025) w
  przywracanych lub importowanych plikach dają ostrzeżenie w podglądzie (wartości nie są pokazywane).

## 6. Porównanie dwóch GPO

*Porównaj* (dwa zaznaczone GPO) albo *Porównaj z…* (wybór drugiego GPO) otwiera okno z kolumnami obu GPO: ustawienia
podstawowe (stan, wersje, filtr WMI, filtrowanie zabezpieczeń, delegowanie, połączenia), ustawienia z plików i lista
plików (takie same, różne, tylko w A, tylko w B). Domyślnie **tylko różnice**. Wartości zamaskowane (hasła) nigdy nie
są pokazywane. *Eksportuj CSV…* zapisuje porównanie tylko na żądanie. Oba GPO pochodzą z domeny okna.

## 7. Kontroler domeny, emulator PDC i SYSVOL

- Okno czyta i zapisuje AD oraz SYSVOL **tylko na swoim kontrolerze domeny**. Gdy nie jest nim emulator PDC (z którego
  domyślnie korzysta GPMC), karty GPO pokazują: *Zmiany GPO wykonujesz na X, a emulatorem PDC jest Y* z przyciskiem
  *Zmień kontroler…*; program nigdy nie przełącza kontrolera sam.
- SYSVOL otwierany jest poświadczeniami okna (hasło tylko w pamięci, zasada 2) tylko na czas odczytu, planowania i
  wykonania. Gdy jest niedostępny, pasek podaje przyczynę (połączenie otwarte nazwą wyróżniającą, środowisko
  nieobsługiwane, odrzucone poświadczenia, odmowa dostępu, brak połączenia z udziałem). Wtedy wyłączone są tylko
  zmiany zapisujące SYSVOL (nowe GPO, kopia, usuwanie, filtrowanie zabezpieczeń, delegowanie, przywracanie, import,
  wyrównanie wersji, naprawa uprawnień); połączenia, stan GPO, zmiana nazwy, filtr WMI i blokowanie dziedziczenia
  zmieniają tylko AD i działają dalej.

## 8. Co AD Manager zapisuje na dysku

Nic z katalogu ani z SYSVOL (zasada 1): GPO, połączenia, ustawienia i różnice są tylko w pamięci, dopóki są widoczne.
Zapamiętywane są wyłącznie: widoczność węzłów GPO w drzewie, ostatni folder kopii zapasowych, szablon komentarza kopii i
wybór *Utwórz kopię zapasową przed usunięciem*. Pliki powstają tylko tam, gdzie operator je wskaże: kopie zapasowe GPO i
eksport porównania do CSV. Dziennik audytu zawiera przy zmianach SYSVOL ścieżki plików w folderze GPO, rozmiary i sumy
SHA-256 – nie treść plików.

## 9. Ograniczenia i pilotaż

- Sprawdzone testami na fałszywym katalogu i na laboratorium Samba 4.19 (zapis SYSVOL przez `smbclient`); zapis SYSVOL
  na Windows (`WindowsSysvolFileWriter`), okna WPF, zrzut ekranu 23 i test dymny sprawdza tylko CI na Windows.
- Do potwierdzenia w pilotażu (Windows Server 2025, Windows 11): GPMC odtwarza kopie AD Managera, a AD Manager kopie
  GPMC (z `gpreport.xml` i bez); GPMC nie zgłasza niespójnych uprawnień po zmianach AD Managera; maski poziomów
  uprawnień GPMC i format `msWMI-Parm2` filtrów WMI; GPO utworzone w AD Managerze działa jak utworzone w GPMC
  (`gpresult /h`).
- Brak: edycji ustawień (K3, K4), raportu „co dostanie komputer” (K5), tabel migracji i kopiowania GPO między
  domenami, GPO wzorcowych (Starter GPO), porównania GPO z innego okna połączenia, próby filtru WMI na komputerze
  (wymaga zapytań WQL w zdalnych akcjach K1), sprawdzania uprawnień operatora przed zmianą.
