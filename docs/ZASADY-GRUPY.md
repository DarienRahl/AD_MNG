[English](en/GROUP-POLICY.md) · **Polski**

# Zasady grupy (GPO) w AD Managerze

Od etapu **K2** AD Manager zarządza obiektami zasad grupy bez konsoli GPMC: drzewo GPO w oknie głównym, karta
*Zasady grupy* przy domenie, OU i lokacji, tworzenie, kopiowanie, zmiana nazwy i usuwanie GPO, łączenie z kontenerami
(kolejność, wymuszanie, wyłączanie połączeń, blokowanie dziedziczenia), stan GPO, filtrowanie zabezpieczeń, filtry
WMI, delegowanie, kopie zapasowe zgodne z GPMC (przywracanie i import ustawień) oraz porównanie dwóch GPO.

**Każda zmiana – w AD i w SYSVOL – przechodzi przez podgląd planu zmian** (zasada 5): najpierw widać, co się zmieni
(atrybuty, pliki z różnicami, wersje GPO, uprawnienia folderu) i odpowiednik w PowerShell, potem *Wykonaj*, wynik i
*Cofnij*.

Od etapu **K3** ustawienia **szablonów administracyjnych (ADMX)** edytuje się w AD Managerze – jak w *Edytorze
zarządzania zasadami grupy* Microsoftu – a **magazyn centralny** szablonów domeny aktualizuje się z paczek producentów
(Windows, Microsoft 365 Apps, Edge, Chrome, Firefox, własne ADMX) albo pobraniem najnowszych szablonów Edge, Chrome i
Firefox prosto od producenta (rozdziały 7 i 8). Od etapu **K5** model *„co dostanie komputer”* pokazuje, które GPO i
ustawienia dostanie komputer i użytkownik (rozdział 9). Od etapu **K4** **ustawienia zabezpieczeń** – grupy lokalne,
prawa użytkownika, zasady kont i opcje zabezpieczeń, inspekcję i usługi – edytuje się na karcie *Ustawienia
zabezpieczeń* (rozdział 10). Od etapu **K6** ustawienia wszędzie mówią językiem GPMC (nazwy, wartości z jednostkami,
grupy i konta zamiast SID-ów), edycja ma skróty klawiszowe, *Edytuj to ustawienie* i podsumowanie zmian przed zapisem,
a **Diagnostyka zasad grupy** wyszukuje błędy logiczne i słabe ustawienia zabezpieczeń we wszystkich GPO domeny
(rozdział 11). Od etapu **K7** **Wyszukiwarka ustawień GPO** pokazuje, które GPO domeny ustawiają dane ustawienie,
wartość albo konto, wybrane ustawienia można **skopiować do innego GPO**, a **Porządki w GPO** proponują usunięcie
pustych i nieużywanych GPO, duplikatów ustawień oraz scalenie GPO o tym samym zasięgu (rozdział 12).

Specyfikacja dla programistów (po angielsku): [dev/k-computers-and-gpo.md](dev/k-computers-and-gpo.md), rozdziały 3, 5
i 6 oraz *K2 as built* (15), *K3 as built* (17), K5 (18), K4 (19), K6 (20) i K7 (21).

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
| Karta *Obiekty zasad grupy* i karta GPO | **Edytuj szablony administracyjne** (pasek, menu kontekstowe listy GPO, nagłówek karty GPO i zakładka *Ustawienia*) – edytor ustawień ADMX, rozdział 7 |
| Karta *Obiekty zasad grupy* i karta GPO | **Edytuj ustawienia zabezpieczeń** (obok *Edytuj szablony administracyjne*) – grupy lokalne, prawa, zasady kont, inspekcja, usługi, rozdział 10 |
| Karta *Obiekty zasad grupy* i edytor szablonów | **Zaktualizuj szablony…** – aktualizacja magazynu centralnego szablonów ADMX, rozdział 8 |
| *Akcja*, lista, karta *Zasady grupy*, strona *Stan* komputera | **Co dostanie komputer…**, **Co dostanie użytkownik…**, **Modeluj zasady…** – modelowanie, rozdział 9 |
| Karta *Obiekty zasad grupy*, *Akcja → Zasady grupy*, menu węzła *Obiekty zasad grupy* | **Diagnostyka zasad grupy…** – błędy logiczne, spójność i bezpieczeństwo wszystkich GPO domeny, rozdział 11 |
| Karta *Obiekty zasad grupy*, *Akcja → Zasady grupy*, menu węzła *Obiekty zasad grupy*, zakładka *Ustawienia* GPO | **Wyszukaj ustawienie we wszystkich GPO…** (na pasku: *Wyszukaj ustawienia…*, na zakładce: *Znajdź we wszystkich GPO*) i **Porządki w GPO…** (na pasku: *Porządki…*) – rozdział 12 |
| Klawiatura | **Ctrl+S** – zapisz zmiany aktywnego edytora, **Ctrl+F** – wyszukiwanie karty, **Esc** – wyczyść wyszukiwanie (rozdział 11) |
| Paleta poleceń (Ctrl+K) | `gpo.objects`, `gpo.wmiFilters`, `gpo.containerPolicies`, `gpo.newLinked`, `gpo.linkExisting`, `gpo.blockInheritance`, `gpo.model`, `gpo.modelUser`, `gpo.diagnose`, `gpo.searchSettings`, `gpo.cleanup`, `gpo.editTemplates`, `gpo.editSecurity`, `document.save`, `view.groupPolicyNodes` |

Każdy widok GPO otwiera się jako karta obok list obiektów (jedna karta na cel – drugie kliknięcie wraca do niej).

## 2. Karty

### Obiekty zasad grupy

Wszystkie GPO domeny: nazwa, stan (włączone / wyłączona część użytkownika / komputera / wszystko), filtr WMI,
połączenia, wersje w AD i w `GPT.INI` (znacznik przy niezgodności), właściciel, data zmiany. Wybór wielu wierszy.
Polecenia: *Nowe…*, *Kopiuj…*, *Zmień nazwę*, *Usuń…*, *Kopia zapasowa…* (zaznaczone GPO), *Przywróć z kopii
zapasowej…*, *Importuj ustawienia…*, *Porównaj* (dokładnie dwa zaznaczone), *Historia w Watchdogu*, *Edytuj szablony
administracyjne* (jedno zaznaczone GPO), *Zaktualizuj szablony…* i *Diagnostyka…*. Pole wyszukiwania (Ctrl+F) zawęża
listę po nazwie, GUID-zie, filtrze WMI i połączonym kontenerze.

### Jeden obiekt GPO (pięć zakładek)

- **Zakres** – połączenia (kontener, wymuszone, włączone; *Wymuszone*, *Połączenie włączone*, *Usuń połączenie*,
  przejście do kontenera), **Filtrowanie zabezpieczeń** (*Dodaj…* / *Usuń*) i **Filtrowanie WMI** (lista filtrów domeny,
  *Zastosuj*).
- **Szczegóły** – domena, właściciel, daty, wersje użytkownika i komputera w AD i w SYSVOL, GUID, **Stan GPO**
  (włączone, wyłączone ustawienia użytkownika, komputera, wszystkie).
- **Ustawienia** – ustawienia z plików GPO (szablon zabezpieczeń, `audit.csv`, `Registry.pol`, preferencje, skrypty,
  komentarz) nazwane i pogrupowane jak w raporcie GPMC, z wyszukiwaniem, szczegółami, *Edytuj to ustawienie*, *Kopiuj*
  i *Raport HTML…* (rozdział 11); tylko do odczytu; hasła preferencji (`cpassword`) i wartości rejestru wyglądające na
  hasła są zamaskowane. Ustawienia `Registry.pol` zmienia się w edytorze szablonów
  administracyjnych (przycisk *Edytuj szablony administracyjne*, rozdział 7), szablon zabezpieczeń, `audit.csv` i
  grupy lokalne preferencji – na karcie *Ustawienia zabezpieczeń* (rozdział 10).
- **Delegowanie** – kto ma jaki poziom uprawnień GPMC: *Odczyt*, *Odczyt (z filtrowania zabezpieczeń)*, *Edycja
  ustawień*, *Edycja ustawień, usuwanie, modyfikowanie zabezpieczeń*, *Niestandardowe*; *Dodaj…*, *Zmień poziom*,
  *Usuń*. Wpisy odziedziczone są tylko pokazywane.
- **Stan** – diagnostyka tego GPO (rozdział 11): połączenia, zakres, filtrowanie, wersje AD i `GPT.INI`, folder
  SYSVOL i jego uprawnienia, nazwy rozszerzeń, pozostałości przerwanego zapisu, ustawienia i bezpieczeństwo – po
  jednym wierszu na obszar, potem uwagi z *Napraw…*. Przyciski **Wyrównaj wersje** i **Napraw uprawnienia folderu**.
  Przebieg sprawdzania (z poleceniem *Zatrzymaj*) i to, kiedy GPO sprawdzono, pokazuje wiersz stanu karty. GPO usunięte
  z AD z pozostawionym folderem: wiersz stanu mówi o tym i podaje polecenie *Usuń pozostały folder*.

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
usunąć, wiersz stanu karty GPO podaje polecenie *Usuń pozostały folder* (odmawia, gdy GPO znów istnieje). *Cofnij* po usunięciu GPO lub
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
plików (takie same, różne, tylko w A, tylko w B). Ustawienia są nazwane jak w GPMC, z kolumną *Obszar* i kluczem
technicznym w podpowiedzi (rozdział 11). Domyślnie **tylko różnice**. Wartości zamaskowane (hasła) nigdy nie
są pokazywane. *Eksportuj CSV…* zapisuje porównanie tylko na żądanie. Oba GPO pochodzą z domeny okna.

## 7. Szablony administracyjne (ADMX)

*Edytuj szablony administracyjne* otwiera kartę **Szablony administracyjne – <GPO>**: ustawienia z plików
`Registry.pol` GPO opisane szablonami ADMX, jak w *Edytorze zarządzania zasadami grupy* Microsoftu.

- **Skąd szablony.** Z magazynu centralnego domeny na kontrolerze okna
  (`\\<DC>\SYSVOL\<domena>\Policies\PolicyDefinitions`). Gdy domena go nie ma – z szablonów tego komputera
  (`%WINDIR%\PolicyDefinitions`), tak jak edytor Microsoftu. Pasek nad drzewem mówi, z których szablonów korzysta karta i
  ile plików wczytano; *Wczytaj szablony ponownie* czyta je od nowa. Nazwy i opisy są w języku programu (polskie z plików
  `pl-PL`, brakujące z `en-US`). Problemy z szablonami – nieczytelny plik, brak pliku językowego, dwa pliki z tą samą
  przestrzenią nazw (np. stara i nowa wersja szablonu Edge; używany jest pierwszy), nieznana kategoria, kategorie
  zagnieżdżone głębiej niż 32 poziomy (pokazane na najwyższym poziomie) – są wymienione pod *Problemy z szablonami*;
  uszkodzony plik nie blokuje pozostałych, a powód jest podany w języku programu.
- **Drzewo i lista.** *Konfiguracja komputera* i *Konfiguracja użytkownika* z kategoriami szablonów, *Wszystkie
  ustawienia* oraz *Ustawienia rejestru spoza szablonów* – wpisy `Registry.pol`, których nie opisuje żaden wczytany
  szablon (zapisane innym narzędziem albo przez szablon, którego brak w magazynie); można je tylko usunąć. Lista
  ustawień: nazwa, stan (*Nie skonfigurowano*, *Włączone*, *Wyłączone*, *Niejednoznaczne*), kategoria; wyszukiwanie w
  nazwach, opisach i wartościach rejestru; filtry *Tylko skonfigurowane* i *Tylko zmienione*.
- **Edycja ustawienia.** *Nie skonfigurowano* / *Włączone* / *Wyłączone* i opcje z szablonu: pole wyboru, liczba (z
  zakresem i przyciskami), tekst, tekst wielowierszowy, lista rozwijana, lista wartości albo par nazwa–wartość. Obok:
  opis z szablonu (*Pomoc*), *Obsługiwane w*, klucz i wartości rejestru oraz plik szablonu. Błędna wartość (poza
  zakresem, puste wymagane pole, powtórzone lub puste wpisy listy, za długi tekst) jest opisana przy ustawieniu i
  blokuje zapis. Stan *Niejednoznaczne* oznacza wartości, które nie odpowiadają ani stanowi *Włączone*, ani
  *Wyłączone* (inne narzędzie, inna wersja szablonu) – wybór stanu je zastępuje. Ustawienie z własną wartością
  rejestru, której plik nie zawiera, jest *Nie skonfigurowano* – także wtedy, gdy inne ustawienia zapisały wartości,
  które z nim dzieli (np. szablon strefy IE i pojedyncze akcje tej strefy).
- **Zapis.** *Zapisz zmiany…* tworzy jeden plan dla GPO: nowy `Machine\Registry.pol` lub `User\Registry.pol` (zmieniane
  są tylko wpisy edytowanych ustawień – reszta pliku zostaje bez zmian i w tej samej kolejności), nazwy rozszerzeń w AD
  (`gPCMachineExtensionNames` / `gPCUserExtensionNames`: rozszerzenie *Rejestr* i narzędzie *Szablony
  administracyjne*, a także rozszerzenia klienta, których wymaga szablon) i podniesienie wersji zmienionej części GPO.
  Wartości są zapisywane według reguł szablonu jak w edytorze Microsoftu: *Wyłączone* zapisuje wartości wyłączenia z
  szablonu (mają pierwszeństwo przed usunięciem wartości opcji, które z nimi dzielą nazwę) albo polecenie usunięcia
  wartości (`**del.`), listy są zastępowane w całości (`**delvals.`), *Nie skonfigurowano* usuwa wpisy ustawienia.
  Lista zmienia tylko swoje wpisy: wartości innych ustawień w tym samym kluczu (i, przy liście dopisywanej, wartości
  inne niż tekst) zostają nietknięte. Podgląd planu pokazuje pliki z różnicami ustawień i odpowiednik w PowerShell
  (`Set-GPRegistryValue`, `Remove-GPRegistryValue`); ostrzega, gdy zmieniana część GPO jest wyłączona w stanie GPO
  (klienci zastosują ustawienia dopiero po jej włączeniu). Na czas zapisu edytor jest tylko do odczytu. *Odrzuć
  zmiany* wraca do zapisanego stanu, *Cofnij* w podglądzie – do stanu sprzed zapisu. Zamknięcie karty (albo okna
  połączenia) z niezapisanymi zmianami wymaga potwierdzenia.
- **Zmiana przez kogoś innego.** Gdy `Registry.pol` GPO zmieni się na kontrolerze od rozpoczęcia edycji, zapis jest
  odrzucany jako konflikt (plik zostaje nietknięty), a karta prosi o odrzucenie zmian, by pokazać aktualne ustawienia.

## 8. Aktualizacja szablonów ADMX (magazyn centralny)

*Zaktualizuj szablony…* otwiera okno **Aktualizacja szablonów ADMX** dla magazynu centralnego na kontrolerze okna:

1. **Szablony.** *Z pliku…*: archiwum ZIP producenta (Chrome, Firefox, Edge), CAB (Edge), MSI (szablony Windows), EXE
   (Microsoft 365 Apps) albo jeden plik ADMX – sam ten szablon z jego plikami ADML z folderów językowych obok; *Z
   folderu…* (rozpakowana paczka, własne szablony); *Szablony tego komputera* (`%WINDIR%\PolicyDefinitions` – tak
   tworzy się magazyn centralny, gdy domena go nie ma); **Pobierz Microsoft Edge / Google Chrome / Mozilla Firefox** –
   najnowsze szablony prosto od producenta.
   Szablony Windows i Microsoft 365 Apps nie mają stałego adresu – pobierasz je ze strony Microsoftu i wskazujesz plik.
   Paczki MSI i EXE rozpakowuje Windows (`msiexec /a`, `/extract`), a archiwum CAB – AD Manager w pamięci, biblioteką
   CAB systemu Windows (`cabinet.dll`), bez zapisywania jego plików na dysku. Zawsze z kopii w prywatnym folderze
   tymczasowym profilu – kopia jest otwarta tylko do odczytu, więc nikt jej nie podmieni między sprawdzeniem podpisu a
   rozpakowaniem – a folder jest usuwany zaraz po wczytaniu plików. CAB, który rozpakowałby się do ponad 512 MiB, jest
   częścią zestawu kilku plików CAB albo zawiera plik spoza swojego folderu, nie jest rozpakowywany; z CAB brane są tylko
   szablony i archiwa ZIP. Instalator, który zakończy się bez rozpakowania czegokolwiek, to błąd rozpakowania (a nie
   paczka bez szablonów). Paczka może mieć
   najwyżej 512 MiB, 5000 szablonów i 512 MiB plików ADMX/ADML razem – większa jest odrzucana. Pliki paczki, których
   okno nie użyje (np. drugi szablon o tej samej nazwie w innym folderze, za duży plik), są wymienione osobno.
2. **Zaufanie.** Okno mówi, skąd są pliki: *Podpisany przez …* (podpis Authenticode sprawdzony przez Windows, w oknie
   organizacja z certyfikatu podpisującego; instalator MSI lub EXE jest uruchamiany wyłącznie wtedy, gdy podpisał go
   Microsoft: łańcuch certyfikatu kończy się w głównym urzędzie certyfikacji Microsoftu, a organizacja certyfikatu to
   dokładnie „Microsoft Corporation”), *Pobrany przez HTTPS z adresu producenta* (tylko adresy producentów, także po
   przekierowaniach; suma SHA-256 i rozmiar sprawdzane, gdy producent je publikuje –
   Edge zawsze, Firefox gdy wydanie na GitHubie podaje sumę, Chrome nie publikuje sumy; plik niezgodny jest odrzucany),
   *Niepodpisany przez producenta* (np. CAB bez ważnego podpisu albo podpisany certyfikatem, który podaje się za
   Microsoft bez łańcucha Microsoftu) albo *Pliki wskazane przez Ciebie* (pochodzenia nie da
   się sprawdzić) – dwa ostatnie z ostrzeżeniem.
3. **Języki.** Foldery językowe paczki do skopiowania; domyślnie zaznaczone `en-US`, język programu i języki, które
   magazyn centralny już ma (inaczej ich pliki zostałyby w starej wersji). Folder `pl` paczki trafia do `pl-PL`
   magazynu (tak jak w szablonach Microsoftu).
4. **Pliki magazynu centralnego.** Każdy plik z oceną: *Nowy*, *Zastąpiony* (z liczbą ustawień, które nowy szablon
   dodaje i usuwa), *Bez zmian*, *Pominięty: zajęta przestrzeń nazw* (inny plik magazynu definiuje tę samą przestrzeń
   nazw – np. stary szablon Edge pod inną nazwą; edytor Microsoftu odrzuciłby oba), *Pominięty: nieprawidłowy szablon*
   (także plik archiwum, którego nie da się rozpakować – np. spakowany metodą LZMA albo uszkodzony),
   *Usunięty* (stary plik kolizji, gdy zaznaczysz *Zastąp stare szablony z tą samą przestrzenią nazw* – razem z jego
   plikami językowymi), *Usunięty (pozostałość)* przerwanej aktualizacji i *Zostaje w starej wersji* (plik językowy
   magazynu dla zastępowanego szablonu, w języku, którego aktualizacja nie zapisuje – zaznacz ten język albo usuń
   plik). Gdy sama paczka ma dwa szablony z tą samą przestrzenią nazw (np. stary i nowy plik po aktualizacji Windows),
   używany jest ten, który magazyn już ma, a drugi jest pominięty. Pliki magazynu, których paczka nie zawiera, zostają.
   Porównanie odświeża się po każdej zmianie języków lub opcji, a *Zaktualizuj…* czeka na jego wynik.
5. **Kopia zapasowa.** Przed zapisem cały magazyn centralny jest kopiowany do wskazanego folderu (podfolder
   `PolicyDefinitions-<DC>-<data-godzina>`; domyślnie włączone, folder zapamiętywany wspólnie z folderem kopii GPO).
   Gdy kopia się nie uda, nic nie jest zmieniane.
6. **Zaktualizuj….** Plan przez podgląd: *Zapisz plik szablonu* / *Usuń plik szablonu (magazyn centralny)* z
   rozmiarami i różnicami, a gdy domena nie ma jeszcze magazynu centralnego – informacja, że od tej chwili edytor
   każdego administratora będzie używał go zamiast szablonów z własnego komputera. Zapis jest transakcją jak przy GPO:
   pliki tymczasowe `*.admng-*.tmp` obok docelowych, sprawdzenie, że nikt nie zmienił magazynu po podglądzie (inaczej
   konflikt i nic nie jest podmieniane), podmiana; błąd przywraca poprzednie pliki, a foldery, które przebieg utworzył,
   znikają. *Cofnij* przywraca poprzednie pliki (magazyn nie ma numerów wersji), a gdy aktualizacja utworzyła magazyn
   centralny – usuwa go razem z folderami (pusty `PolicyDefinitions` odebrałby edytorom wszystkie szablony). Kopia
   zapasowa, plan i podgląd działają w tle; w tym czasie okna nie można zamknąć. Po aktualizacji otwarte edytory
   wczytują szablony ponownie.

## 9. Co dostanie komputer (modelowanie zasad grupy)

Od etapu **K5** AD Manager odpowiada na pytanie *„które GPO i jakie ustawienia dostanie ten komputer (i ten
użytkownik)?”* – bez logowania się na komputer i bez `gpresult`, jak *Modelowanie zasad grupy* w GPMC, ale liczone w
programie z tego, co jest w AD i SYSVOL kontrolera okna (nic nie jest uruchamiane na kontrolerze ani na komputerze).

**Gdzie:** *Akcja* i menu kontekstowe listy – **Co dostanie komputer…** (zaznaczony komputer) i **Co dostanie
użytkownik…** (zaznaczony użytkownik); karta *Zasady grupy* domeny lub OU – **Modeluj zasady…** (nowy komputer i nowy
użytkownik w tym kontenerze); właściwości komputera, strona *Stan* – **Co dostanie komputer…**; paleta poleceń –
`gpo.model`, `gpo.modelUser`. Każdy model otwiera się jako karta (jedna karta na zestaw celów).

**Dane wejściowe** (zmiana nie przelicza modelu sama – pasek pokazuje *kliknij „Przelicz”*):

| Pole | Co daje |
|---|---|
| *Komputer*, *Użytkownik* | konto (*Wybierz komputer…* / *Wybierz użytkownika…*), nowe konto w kontenerze (*Kontener…*) albo *Brak* |
| *Lokacja* | lokacje lasu albo *(nieznana)*; dla konta komputera domyślnie lokacja z jego podsieci (jak w *Przeglądzie komputerów*), wybór operatora nie jest nadpisywany |
| *Filtry WMI* | *Przyjmij jako spełnione* albo *niespełnione* – program nie wykonuje zapytań WQL na komputerze |
| *Pętla zwrotna* | *Z ustawień komputera* (z GPO komputera, jak klient), *Wyłączona*, *Scalanie*, *Zastępowanie* |

**Jak liczy** (tak jak klient zasad grupy, [MS-GPOL]):

- **lista GPO** – łącza lokacji, domeny i kolejnych OU nad kontem, kolejność łączy, **wymuszone** łącza na końcu
  (najgłębszy kontener wygrywa wśród wymuszonych), **blokowanie dziedziczenia** (także dla łączy lokacji), łącza
  wyłączone, to samo GPO połączone kilka razy, GPO nieistniejące, z wyłączoną częścią komputera lub użytkownika i
  puste (wersja 0);
- **filtrowanie zabezpieczeń** – konto musi mieć prawo *Odczyt* i *Zastosuj zasady grupy*; wpisy listy ACL są
  oceniane w kolejności, jak w Windows (pierwszy pasujący wpis decyduje). Token: konto, jego grupy zabezpieczeń z
  `tokenGroups` (zagnieżdżone i grupa podstawowa), *Użytkownicy uwierzytelnieni*, *Wszyscy*, *Ta organizacja*, a
  dla kontrolera domeny *Kontrolery domeny przedsiębiorstwa*. Dla części użytkownika sprawdzane jest też, czy
  **komputer może odczytać GPO** (MS16-072);
- **filtr WMI** – GPO z filtrem jest *zastosowany, jeśli filtr „…” jest spełniony* albo pomijany, według wyboru;
- **pętla zwrotna** – `UserPolicyMode` z wygrywającego GPO komputera: *scalanie* dokłada GPO z łańcucha komputera
  (pierwszeństwo nad GPO użytkownika), *zastępowanie* używa tylko łańcucha komputera;
- **ustawienia** – dla każdego ustawienia wygrywa GPO o najwyższym pierwszeństwie; przesłonięte GPO są pokazane w
  *Dlaczego*. Skrypty są **kumulatywne** (działają wszystkie). Na kontrolerze domeny zasady kont (hasła, blokada,
  Kerberos) liczą się tylko z GPO połączonych z domeną – ostrzeżenie wskazuje pozostałe. Szablony administracyjne są
  pokazywane jako zasady z ADMX (stan i opcje), wartości `Registry.pol` bez definicji – jako wartości rejestru.
  Wartość `Registry.pol`, której nie opisuje żadna skonfigurowana zasada tej części (np. wartość zasady użytkownika w
  pliku komputera albo opcja bez wartości samej zasady), zostaje na liście jako wartość rejestru – jak w GPMC.
  Elementy preferencji z kierowaniem na poziomie elementu mają uwagę *zależy od komputera*.

**Wynik:** dla komputera i dla użytkownika – lista GPO z wynikiem (*Zastosowany*, *Zablokowany przez blokowanie
dziedziczenia w …*, *Filtrowanie zabezpieczeń: brak prawa Zastosuj zasady grupy*, *Komputer nie może odczytać GPO
(MS16-072)*, *Łącze wyłączone*, *Pusty (wersja 0)* i inne) i kolejnością, lista ustawień (obszar, ustawienie, wartość,
wygrywający GPO, przesłonięte, uwagi) z panelem **Dlaczego**, grupy tokenu i ostrzeżenia (lokacja nieznana, folder GPO
nieczytelny w SYSVOL, brak `tokenGroups`, brak szablonów ADMX). **Eksportuj do CSV…** zapisuje listy GPO i ustawień
obu części do pliku wskazanego przez operatora.

**Czym to nie jest:** model, nie wynik z komputera (RSoP z `gpresult`). Nie uwzględnia filtrów WMI, kierowania
elementów preferencji, lokalnych zasad komputera, GPO z innych domen lasu (łącze lokacji lub OU do GPO innej domeny ma
wynik *GPO innej domeny (nie modelowany)* – nigdy nie jest mylone z GPO tej domeny o tym samym GUID, jak *Default
Domain Policy*), powolnego łącza i stanu przetwarzania na kliencie. Gdy SYSVOL jest niedostępny, listy GPO są pełne, a
ustawień brak.

## 10. Ustawienia zabezpieczeń (grupy lokalne, prawa, zasady kont, inspekcja, usługi)

*Edytuj ustawienia zabezpieczeń* otwiera kartę **Ustawienia zabezpieczeń – <GPO>**: część *Konfiguracja komputera →
Ustawienia systemu Windows → Ustawienia zabezpieczeń* oraz grupy lokalne z *Preferencji*, jak w edytorze Microsoftu.
Karta czyta trzy pliki GPO: szablon zabezpieczeń `GptTmpl.inf`, zaawansowane zasady inspekcji `audit.csv` i grupy
lokalne preferencji `Groups.xml`. Zmienia w nich tylko wiersze edytowanych ustawień – wszystko inne (uprawnienia
plików i rejestru, dzienniki zdarzeń, zapory, użytkownicy lokalni z preferencji, celowanie na poziomie elementu,
nieznane wpisy) zostaje w pliku bez zmian.

- **Grupy lokalne.** Obok siebie *Grupy z ograniczeniami* (szablon zabezpieczeń: *Członkowie* – dokładnie ta lista,
  *Członek grup* – grupa dopisywana do wskazanych grup) i grupy lokalne *Preferencji* (akcja *Utwórz*, *Zastąp*,
  *Aktualizuj*, *Usuń*, *Usuń wszystkich użytkowników / wszystkie grupy członkowskie*, członkowie dodawani i usuwani).
  Grupę wybiera się z listy wbudowanych grup lokalnych (zapisywana z SID-em) albo wpisuje nazwę. Element preferencji z
  celowaniem na poziomie elementu jest oznaczony – celowanie zostaje przy zapisie. **Pierwszeństwo:** *Członkowie* z
  grup z ograniczeniami zastępują członkostwo przy każdym odświeżeniu zasad (wygrywa GPO o najwyższym priorytecie),
  preferencje zmieniają członkostwo w kolejności elementów, a kolejność obu rozszerzeń na kliencie nie jest
  gwarantowana – tej samej grupy nie należy ustawiać w obu miejscach. Ten sam opis ma zakładka *Uprawnienia* (U4c).
- **Prawa użytkownika.** Wszystkie prawa z *Przypisywania praw użytkownika* (nazwa i stała, np.
  `SeRemoteInteractiveLogonRight`), *Zdefiniowane* i lista podmiotów; zdefiniowana pusta lista znaczy „nikt”. Prawa
  z pliku spoza listy programu są pokazane po nich.
- **Zasady kont i opcje zabezpieczeń.** Zasady haseł, blokady konta i Kerberos, opcje kont (włączenie i nazwa konta
  Administrator i Gość) oraz najczęściej używane opcje zabezpieczeń (LAN Manager i NTLM, podpisywanie SMB i LDAP, bezpieczny
  kanał, logowanie interaktywne, Kontrola konta użytkownika, inspekcja), każda *Nie zdefiniowano* albo z wartością
  (liczba z zakresem, włączone/wyłączone, lista wyboru, tekst, tekst wielowierszowy). Inne wartości z pliku są
  pokazane jako surowe wpisy (klucz i wartość) – można je usunąć albo zmienić surową wartość. Zasady haseł i blokady
  kont domenowych działają tylko z GPO połączonego z domeną (szczegółowe zasady haseł to osobny mechanizm); w innych
  GPO dotyczą kont lokalnych komputerów.
- **Inspekcja.** Kategorie podstawowe (`[Event Audit]`) i podkategorie zaawansowane (`audit.csv`), każda *Nie
  skonfigurowano*, *Bez inspekcji*, *Powodzenie*, *Niepowodzenie* albo *Powodzenie i niepowodzenie*. Gdy GPO ma
  jedne i drugie, a opcja *Inspekcja: wymuś ustawienia podkategorii zasad inspekcji…* nie jest włączona, karta
  ostrzega, że kategorie podstawowe mogą nadpisać podkategorie.
- **Usługi.** Nazwa usługi i tryb uruchamiania (*Automatyczny*, *Ręczny*, *Wyłączony*); uprawnienia usługi (SDDL) są
  pokazane i zostają bez zmian, nowa usługa ich nie ustawia.

**Podmioty** (członkowie, posiadacze praw) wybiera się w oknie wyboru obiektów – zapisywany jest SID – albo wpisuje
nazwę (`KONTOSO\Serwisanci`), którą każdy komputer rozwiązuje sam; karta to zaznacza. **Zapis** – *Zapisz zmiany…*
tworzy jeden plan: nazwy rozszerzeń komputera w AD (*Zabezpieczenia*, *Konfiguracja zasad inspekcji*, *Użytkownicy i
grupy lokalne* – tylko dla zapisywanych plików), zmienione pliki i wersja komputera. Grupa PowerShell `GroupPolicy` nie
ma poleceń dla tych plików, więc skrypt w podglądzie tylko wskazuje GPO. Zmiana pliku przez kogoś innego od
rozpoczęcia edycji jest konfliktem (plik zostaje nietknięty), *Odrzuć zmiany* wraca do zapisanego stanu, *Cofnij* w
podglądzie – do stanu sprzed zapisu, a zamknięcie karty z niezapisanymi zmianami wymaga potwierdzenia – jak w
edytorze szablonów (rozdział 7). Plik, którego nie da się odczytać, blokuje tylko swoje strony.

## 11. Czytelne ustawienia, wygoda edycji i diagnostyka (K6)

### Ustawienia w języku GPMC

Wszędzie, gdzie program pokazuje ustawienia GPO, mówią one językiem konsoli Microsoftu, a nie kluczami z plików:

| Było (klucz techniczny) | Jest |
|---|---|
| `Computer/Security/System Access/MaximumPasswordAge = 42` | *Konfiguracja komputera › Zasady › Ustawienia systemu Windows › Ustawienia zabezpieczeń › Zasady konta › Zasady haseł* – **Maksymalny okres ważności hasła: 42 dni** |
| `Computer/Security/System Access/LockoutBadCount = 0` | **Próg blokady konta: Konto nie jest blokowane** |
| `Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight = *S-1-5-32-544, *S-1-5-21-…-1107` | **Zezwalaj na logowanie za pomocą usług pulpitu zdalnego: Administratorzy, KONTOSO\Serwisanci** |
| `Computer/Audit/{0cce923f-…} = Success and Failure` | **Inspekcja weryfikacji poświadczeń: Powodzenie i niepowodzenie** |
| `Computer/Registry/HKLM\Software\Policies\…\AU!NoAutoUpdate = REG_DWORD:0` | zasada ADMX **Konfiguruj aktualizacje automatyczne: Włączone** z opcjami, jak w edytorze szablonów |
| `Computer/Preferences/Groups/{uid}@name`, `…/groupSid`, `…/Members` … (kilka wierszy) | jeden wiersz w *Preferencje › Ustawienia panelu sterowania › Lokalni użytkownicy i grupy*: **Administrators (built-in): Aktualizuj: + KONTOSO\Serwisanci, − KONTOSO\Goście firmowi**, w szczegółach typ elementu, grupa, opcje i kierowanie na poziomie elementu |

- **Wartości** mają jednostki z poprawną odmianą (*1 dzień*, *2 dni*, *5 dni*; *3 nieudane próby logowania*), objaśnione
  wartości specjalne (*Hasło nie wygasa*, *Hasło można zmienić od razu*, *Do odblokowania przez administratora*),
  nazwy wyborów z katalogu K4, typy i akcje rejestru (*Usuń wartość*, *Utwórz klucz*), tryby usług, fazy skryptów
  (*Uruchamianie*, *Logowanie* …) i rozmiary plików skryptów. SID-y są zamieniane na nazwy (grupy lokalne i podmioty
  dobrze znane od razu, pozostałe jednym zapytaniem do katalogu na widok). Hasła i inne sekrety zostają zamaskowane
  wszędzie – w wierszu, szczegółach, kopii, raporcie i eksporcie.
- **Zakładka *Ustawienia* GPO** jest pogrupowana jak raport GPMC (*Konfiguracja komputera* / *Konfiguracja
  użytkownika* → ścieżka folderów, grupy zwijane), ma wyszukiwanie (nazwa, wartość, klucz techniczny; **Ctrl+F**), panel
  szczegółów (gdzie GPMC pokazuje ustawienie, pełna wartość, objaśnienie, klucz techniczny), **Edytuj to ustawienie**
  (dwuklik, Enter, menu kontekstowe), **Kopiuj** i **Raport HTML…** – samodzielny raport ustawień GPO zapisany do pliku
  wskazanego przez operatora. Ustawienia `Registry.pol` są najpierw pokazane jako wartości rejestru, a po wczytaniu
  szablonów ADMX (w tle) – jako zasady z nazwami i opcjami; gdy szablonów nie ma, karta to mówi.
- **Ten sam opis** mają: model *co dostanie komputer* (K5, też z *Edytuj to ustawienie*), porównanie dwóch GPO (z
  kolumnami *Obszar* i *Klucz techniczny*, także w CSV), podgląd planu zmian (różnice plików jako zdania, linia
  techniczna w podpowiedzi) i zmiany GPO w zakładce Watchdog. Powiadomienia Watchdoga (e-mail, Teams, webhook) nadal
  podają klucz techniczny.

### Wygoda edycji

- **Klawiatura.** **Ctrl+S** zapisuje zmiany aktywnej karty edytora (*Zapisz zmiany…* z podglądem planu), **Ctrl+F**
  przechodzi do wyszukiwania karty (edytory, zakładka *Ustawienia*, lista GPO, diagnostyka; na innych kartach – do
  wyszukiwania w katalogu), **Esc** czyści wyszukiwanie, **Enter** na ustawieniu przechodzi do jego edycji. Paleta
  poleceń (Ctrl+K): *Zapisz zmiany*, *Edytuj szablony administracyjne*, *Edytuj ustawienia zabezpieczeń*, *Diagnostyka
  zasad grupy*.
- **Edytuj to ustawienie** – z zakładki *Ustawienia*, z modelu K5, z diagnostyki i z menu połączenia na karcie *Zasady
  grupy* kontenera (*Edytuj…*) – otwiera właściwy edytor od razu na tym ustawieniu: szablony administracyjne na
  zasadzie, ustawienia zabezpieczeń na właściwej stronie i wierszu. Otwarta już karta edytora tego GPO jest używana
  ponownie. Ustawienia, których żaden edytor nie zmienia (skrypty, pozostałe preferencje), nie mają tego polecenia.
- **Przed zapisem** podgląd planu ma sekcję **Zmiany ustawień (N)**: ustawienie, wartość przed i po, zdaniami jak na
  zakładce *Ustawienia* – nad listą plików i atrybutów.
- **Edytor szablonów (K3):** licznik skonfigurowanych ustawień nadąża za zmianami; błędnie wpisane opcje nie znikają
  po przejściu do innego ustawienia – zostają w edytorze z ostrzeżeniem przy zasadzie i blokują zapis, aż zostaną
  poprawione albo odrzucone; brak magazynu centralnego jest ostrzeżeniem na karcie szablonów; kolumna kategorii
  pojawia się tylko wtedy, gdy lista obejmuje kilka kategorii; klucz i wartość rejestru są w dwóch wierszach.
- **Edytor ustawień zabezpieczeń (K4):** jedno wyszukiwanie i filtry **Tylko zdefiniowane** / **Tylko zmienione** nad
  wszystkimi pięcioma stronami (przy filtrze opcje z wszystkich grup na jednej liście z kolumną grupy); edytowany
  wiersz nie znika z listy, dopóki filtr się nie zmieni.
- **Lista GPO** ma wyszukiwanie (nazwa, GUID, filtr WMI, połączony kontener). Nagłówki kart nie ucinają tytułu ani
  stanu (pełny tekst w podpowiedzi), a podziały między drzewem, listą i szczegółami da się przesuwać.

### Diagnostyka zasad grupy

**Diagnostyka zasad grupy…** (przycisk *Diagnostyka…* na karcie *Obiekty zasad grupy*, *Akcja → Zasady grupy*, menu
węzła *Obiekty zasad grupy* w drzewie, paleta `gpo.diagnose`) otwiera kartę, która szuka błędów logicznych, problemów
spójności i słabych ustawień zabezpieczeń we wszystkich GPO domeny okna.

- **Szybkie sprawdzenie** czyta z AD obiekty GPO, połączenia, uprawnienia i filtry WMI oraz `GPT.INI` każdego GPO – zwykle
  kilka sekund. **Pełne sprawdzenie** czyta także foldery SYSVOL i ustawienia wszystkich GPO, liczy użytkowników i
  komputery pod połączeniami, sprawdza SID-y i porównuje ustawienia GPO działających na ten sam kontener. Postęp,
  *Anuluj*, czas ostatniego sprawdzenia; gdy zasady grupy zmienią się w tym oknie, karta mówi, że wyniki mogą być
  nieaktualne (nie sprawdza sama od nowa).
- **Wyniki:** liczniki według ważności (*Krytyczne*, *Wysokie*, *Średnie*, *Niskie*, *Informacje* – kliknięcie
  filtruje), filtry ważności, obszaru (*Połączenia*, *Zakres*, *Filtrowanie*, *Wersje*, *Folder SYSVOL*, *Ustawienia*,
  *Konflikty*, *Bezpieczeństwo*), GPO i tekstu, lista uwag i szczegóły: *Dlaczego to ważne*, *Co zrobić*, *Czego dotyczy*
  oraz **Napraw…**, *Otwórz GPO*, *Edytuj to ustawienie*, *Pokaż w modelu*, *Kopiuj*. Pasek nad listą wymienia, czego
  nie sprawdzono i dlaczego (szybkie sprawdzenie, niedostępny SYSVOL, folder, którego nie udało się odczytać).
- **Napraw…** otwiera zwykły podgląd planu zmian (zasada 5) – z odpowiednikiem PowerShell, wykonaniem i *Cofnij* – a
  po wykonaniu karta sprawdza ponownie. Naprawy: *włącz połączenie*, *usuń połączenie*, *pozostaw jedno połączenie (to,
  które obowiązuje)*, *ustaw stan obiektu GPO*, *nadaj uprawnienie „Odczyt”* (MS16-072), *usuń przypisanie filtru WMI*,
  *usuń filtr WMI*, *wyrównaj wersje*, *napraw uprawnienia folderu*, *usuń konto z uprawnień*, *dodaj brakujące nazwy
  rozszerzeń*.
- **Eksport** do CSV albo HTML – tylko do pliku wskazanego przez operatora; wyniki są tylko w pamięci karty.
- **Zakładka *Stan* GPO** pokazuje wyniki tego samego sprawdzenia dla jednego GPO (pełnego, gdy SYSVOL jest dostępny):
  po jednym wierszu na obszar, potem uwagi z *Napraw…*; *Wyrównaj wersje* i *Napraw uprawnienia folderu* zostają.
  Sprawdzenie rusza, gdy zakładka jest widoczna (nie przy każdym otwarciu karty GPO).

**Co jest sprawdzane** (ważność w nawiasie; ✚ – z naprawą w programie):

- **Połączenia:** GPO niepołączony (informacja); wszystkie połączenia wyłączone (niska ✚); połączenie z nieistniejącym GPO
  (średnia ✚) – a z GPO, którego to konto nie może odczytać, tylko informacja, bez usuwania połączenia; nieczytelne lub obce wpisy połączeń (niska); GPO połączony kilka razy z tym samym kontenerem (niska ✚);
  połączony GPO z wyłączonymi wszystkimi ustawieniami (średnia); połączony pusty GPO (niska).
- **Zakres:** wyłączona część z ustawieniami (średnia ✚); włączona pusta część (informacja ✚); ustawienia użytkownika
  bez użytkowników i ustawienia komputera bez komputerów pod połączeniami (średnia); zasady konta poza poziomem domeny
  (średnia); sprzężenie zwrotne w GPO bez komputerów (niska).
- **Filtrowanie:** nikt nie może zastosować GPO (wysoka); komputery nie mogą odczytać GPO z ustawieniami użytkownika –
  MS16-072 (wysoka ✚); brakujący filtr WMI (średnia ✚); nieużywany filtr WMI (informacja ✚); wpisy odmowy
  (informacja); nieznane konto w uprawnieniach (niska ✚).
- **Wersje i folder SYSVOL:** różne wersje w AD i `GPT.INI` (średnia ✚); brak folderu GPO (wysoka); uprawnienia folderu
  różne od uprawnień GPO (średnia ✚); pliki ustawień pomijane przez klientów, bo brakuje nazw rozszerzeń (średnia ✚);
  nieczytelny plik ustawień (średnia); pozostałości przerwanego zapisu (niska); folder GPO poza SYSVOL domeny (wysoka).
- **Ustawienia:** ta sama grupa w *Grupach z ograniczeniami* i w preferencjach (średnia); *Administratorzy* zdefiniowani
  bez administratorów (wysoka); podstawowa i zaawansowana inspekcja jednocześnie (średnia, gdy wymuszanie podkategorii jest
  wyłączone; niska, gdy nie jest zdefiniowane – wtedy domyślnie działa wymuszanie); minimalna długość hasła
  powyżej 14 bez opcji zezwalającej tam, gdzie działa (średnia); podmiot zapisany nazwą (informacja); nieznany SID (niska); odmowa
  logowania dla szerokiej grupy (wysoka).
- **Konflikty:** GPO działające na ten sam kontener ustawiają jedno ustawienie różnie (wygrywający i przegrywający
  wymienieni z nazwy) albo tak samo (powtórzenie) – informacja.
- **Bezpieczeństwo:** hasło w preferencjach `cpassword`, MS14-025 (krytyczna); szeroka grupa (*Wszyscy*, *Użytkownicy
  uwierzytelnieni*, *Użytkownicy domeny* …) może edytować GPO, ma wrażliwe prawo (debugowanie, kopia zapasowa,
  przywracanie, przejmowanie na własność, ładowanie sterowników …) albo jest członkiem uprzywilejowanej grupy lokalnej
  (wysoka); hasła z odwracalnym szyfrowaniem i niezaszyfrowane hasła do serwerów SMB (wysoka); słaby poziom
  uwierzytelniania LAN Manager, przechowywane skróty LM, podpisywanie SMB lub LDAP niewymagane na kontrolerach domeny,
  dostęp anonimowy, włączone konto Gość, wyłączona kontrola konta użytkownika, słabe zasady haseł domeny (średnia).
  Reguły kontrolerów domeny i zasad haseł domeny oceniają ustawienie, które na nich faktycznie wygrywa (pierwszeństwo
  GPO, wyłączona część komputera), a „część ma ustawienia” – przy pełnym sprawdzeniu – według plików, nie numeru wersji.

Kontrole działają na danych z jednego kontrolera domeny (zasada 4) i niczego nie zmieniają; każda zmiana idzie przez
*Napraw…* i podgląd planu. Część reguł zakłada, że filtry WMI są spełnione, a sprzężenie zwrotne jest wyłączone (jak w
modelu K5) – szczegóły w opisie każdej uwagi.

## 12. Wyszukiwanie, kopiowanie ustawień i porządki w GPO (K7)

Trzy narzędzia dla wszystkich GPO domeny okna: **Wyszukiwarka ustawień GPO** odpowiada na pytania „gdzie ustawiono
minimalną długość hasła?”, „które GPO mapuje dysk K:?”, „kto daje prawo logowania przez usługi pulpitu zdalnego?”;
**kopiowanie ustawień** przenosi wybrane ustawienia z jednego lub kilku GPO do innego GPO; **Porządki w GPO** wskazują
puste, nieużywane i wyłączone GPO, powtórzone ustawienia i GPO, które można scalić. Ustawienia są opisane językiem GPMC
(rozdział 11). Każda zmiana – kopiowanie, usunięcie ustawienia, scalenie, odłączenie, wyłączenie, usunięcie GPO – idzie
przez podgląd planu zmian (rozdział 4) na kontrolerze domeny okna (rozdział 13); każdą poza usunięciem GPO można cofnąć.

### Gdzie to jest

| Miejsce | Wyszukiwarka | Porządki |
|---|---|---|
| Paleta poleceń (Ctrl+K) | `gpo.searchSettings` – *Wyszukaj ustawienie we wszystkich GPO…* | `gpo.cleanup` – *Porządki w GPO…* |
| *Akcja → Zasady grupy* | *Wyszukaj ustawienie we wszystkich GPO…* | *Porządki w GPO…* |
| Menu węzła *Obiekty zasad grupy* w drzewie | *Wyszukaj ustawienie we wszystkich GPO…* | *Porządki w GPO…* |
| Pasek karty *Obiekty zasad grupy* | przycisk **Wyszukaj ustawienia…** | przycisk **Porządki…** |
| Zakładka *Ustawienia* karty GPO | **Znajdź we wszystkich GPO** – szuka tekstu z pola wyszukiwania zakładki (a gdy jest puste – nazwy zaznaczonego ustawienia) | – |
| Karta *Porządki w GPO* | *Pokaż w wyszukiwarce* – otwiera wyszukiwarkę z nazwą ustawienia propozycji | – |

Obie karty są jedne na okno połączenia: drugie wywołanie wraca do otwartej karty (wyszukiwarka dostaje nowy tekst).

### Odczyt ustawień wszystkich GPO

Pierwsze otwarcie wyszukiwarki albo porządków czyta z kontrolera domeny okna obiekty GPO, połączenia (domena, OU,
lokacje), wersje `GPT.INI` i foldery wszystkich GPO w SYSVOL – najwyżej cztery odczyty naraz – rozpoznaje nazwy kont
i grupuje ustawienia. Postęp pokazuje etapy *Odczyt obiektów GPO i połączeń*, *Odczyt wersji GPT.INI*, *Odczyt
folderów GPO*, *Rozpoznawanie nazw kont*, *Grupowanie ustawień*; **Anuluj** kończy czekanie tej karty (poprzednie
wyniki zostają). Obie karty korzystają z **jednego odczytu** w oknie: otwarcie drugiej nie czyta folderów jeszcze raz.

- **Odśwież** (F5) czyta ponownie tylko GPO zmienione od ostatniego odczytu (inna wersja w AD lub `GPT.INI`), usunięte
  znikają, połączenia są zawsze świeże. **Odczytaj wszystko ponownie** czyta foldery wszystkich GPO.
- Gdy zasady grupy zmienią się w tym oknie (także przez kopiowanie lub porządki), karta pokazuje pasek *Zmieniono zasady
  grupy w tym oknie – wyniki mogą być nieaktualne.* z przyciskiem *Odśwież* – nie odczytuje sama od nowa. Po wykonaniu
  akcji z tych kart odświeżenie (tylko zmienionych GPO) rusza samo.
- Zmiana kontrolera domeny, ponowne połączenie i rozłączenie usuwają odczyt (pokazane wyniki zostają z paskiem
  nieaktualności); zmiana języka tylko przepisuje teksty (bez ponownego czytania folderów).
- **Limity:** plik ustawień większy niż 16 MiB albo ustawienia GPO ponad 64 MiB nie są czytane (GPO ma stan *Nie
  wszystkie ustawienia odczytane*, tak samo jak przy pliku `Registry.pol` z ponad 10 000 wpisów); po 1 GiB odczytu
  pozostałe GPO mają stan *Nie odczytano*. Inne stany: *Nie znaleziono folderu w SYSVOL*, *Odmowa dostępu*, *Folder jest
  za duży*, *Nie udało się odczytać*. GPO, których konto okna nie może odczytać w AD, w ogóle nie są widoczne – nagłówek
  karty podaje liczbę przeszukiwanych GPO i czas odczytu (*Przeszukiwane obiekty GPO: N · odczyt …*).
- Bez dostępu do SYSVOL karta ma tylko dane z katalogu (pasek *SYSVOL jest niedostępny (…): odczytano tylko dane z
  katalogu*); ustawień z plików nie da się wtedy przeszukać ani ocenić.

### Wyszukiwarka ustawień GPO

**Pole wyszukiwania** (*Ustawienie, wartość, klucz lub konto (co najmniej 2 znaki)*, Ctrl+F, Esc czyści) szuka w trakcie
pisania (Enter – od razu). Wielkość liter i polskie znaki nie mają znaczenia: „dlugosc” znajdzie „długość”.

| Filtr | Wybory |
|---|---|
| **Szukaj w** | *Wszystko*, *Nazwa* (nazwa ustawienia jak w GPMC), *Wartość*, *Klucz techniczny* (np. `MinimumPasswordLength`, `HKLM\Software\Policies\…`), *Konto* |
| **Konfiguracja** | *Komputera i użytkownika*, *Komputera*, *Użytkownika* |
| **Obszar** | *Każdy obszar*, *Szablony administracyjne* (także wartości rejestru spoza szablonów), *Ustawienia zabezpieczeń*, *Zaawansowane zasady inspekcji*, *Preferencje – typ* (osobno każdy typ preferencji występujący w domenie, np. mapowania dysków), *Skrypty*, *Inne* |
| **Tylko różne wartości** | tylko ustawienia, którym GPO nadają różne wartości |

*Wyczyść filtry* wraca do ustawień domyślnych. **Konto** przyjmuje nazwę (`KONTOSO\Serwisanci`, `serwis@kontoso.pl`,
*Administratorzy*) albo SID i znajduje ustawienia, w których konto jest zapisane SID-em lub nazwą (prawa użytkownika,
grupy z ograniczeniami, grupy lokalne preferencji, kierowanie na poziomie elementu), a także konta, których nazwa
zawiera wpisany tekst. **Wartości ukryte** (hasła, community SNMP …) nigdy nie są przeszukiwane – tylko ich nazwy i
klucze.

**Krótkie zapytanie** (pusty tekst albo jeden znak) pokazuje tylko podsumowanie: dla każdego obszaru liczbę ustawień,
wystąpień i GPO, a na końcu razem – nigdy całej listy wszystkich ustawień domeny.

**Wyniki** są pogrupowane według ustawienia. Nagłówek grupy to nazwa ustawienia, ścieżka w konsoli GPMC i podsumowanie:
*ustawione w N GPO – wartości zgodne*, *– wartości różne* albo *– wartości nieporównywalne* (np. wartość ukryta,
element z kierowaniem na poziomie elementu, skrypty; elementy wyłączone nie są liczone; gdy ustawienie działa tylko z
jednego GPO – samo *ustawione w N GPO*). Pod nagłówkiem jest **każde** GPO, które ustawia to ustawienie, także z inną
wartością, z kolumnami *Obiekt zasad grupy*, *Wartość*, *Połączenia* (wyłączone połączenie z dopiskiem *(wyłączone)*) i
*Uwagi*: *warunkowe (kierowanie na poziomie elementu)*, *element wyłączony*, *wartość ukryta*, *GPO odczytany
częściowo*. Wiersz nad listą: *Znalezione ustawienia: N (wystąpienia: M)*. Gdy grup jest więcej niż **2 000**, karta
pokazuje pierwsze 2 000 i informację *Zawęź wyszukiwanie – pokazano 2 000 z N grup*.

**Pasek uwag** nad wynikami mówi, czego nie przeszukano: GPO, których folderów nie odczytano albo odczytano częściowo
(liczba, nazwy i stany w podpowiedzi), niewczytane szablony administracyjne (zasady są wtedy pokazane jako wartości
rejestru i nie można ich wyszukać po nazwie), liczba wartości ukrytych, niedostępny SYSVOL, połączenia z innych domen
lasu (niewidoczne z tego kontrolera domeny).

**Szczegóły** zaznaczonego wiersza: nazwa, GPO, pełna wartość z elementami, *Miejsce w konsoli GPMC*, *Połączenia*,
*Klucz techniczny*, uwagi i – gdy ustawienia nie da się skopiować – *Nie można skopiować: powód*.

**Działania** (przyciski w szczegółach i menu kontekstowe wiersza):

- **Otwórz GPO** (Enter, dwuklik) – karta GPO na zakładce *Ustawienia*;
- **Edytuj to ustawienie** – edytor szablonów (K3) albo ustawień zabezpieczeń (K4) od razu na tym ustawieniu (gdy
  ustawienie ma edytor, rozdział 11);
- **Porównaj GPO** – dwa zaznaczone wiersze: porównanie ich GPO; jeden wiersz: porównanie jego GPO z wybranym (rozdział
  6);
- **Kopiuj do innego GPO…** – zaznaczone wiersze (także z różnych GPO) do wybranego GPO, poniżej;
- **Kopiuj** (Ctrl+C) – tekst zaznaczonych wierszy do schowka (wartości ukryte zostają ukryte);
- **Eksportuj do CSV…** i **Eksportuj do HTML…** – bieżące wyniki do pliku wskazanego przez operatora (CSV z ochroną
  przed formułami; raport HTML z kontrolerem domeny, zapytaniem i czasem odczytu).

### Kopiowanie ustawień do innego GPO

**Kopiuj do innego GPO…** (w wyszukiwarce i w porządkach) najpierw pyta o **docelowy obiekt GPO** (*Kopiuj ustawienia –
wybierz docelowy obiekt GPO*; jeden GPO, bez GPO źródłowych), potem otwiera okno **Kopiuj ustawienia do …**: lista
ustawień z kolumnami *Ustawienie*, *Wartość*, *Źródłowy obiekt GPO* i *Uwaga*, pola wyboru (*Zaznacz wszystkie*,
*Odznacz wszystkie*, licznik *Zaznaczono: X z Y*) i wybór **Gdy docelowy obiekt GPO ma już to ustawienie:**

| Tryb | Skutek |
|---|---|
| **Zastąp** (domyślnie) | wartość w docelowym GPO zostaje zastąpiona kopiowaną; gdy docelowy GPO ma już tę samą wartość, nic nie jest zapisywane (*docelowy obiekt GPO ma już tę samą wartość*); elementy preferencji z innym kierowaniem na poziomie elementu (np. kilka mapowań dysku K: dla różnych grup) zostają, a kopia jest dodana za nimi |
| **Pomiń** | docelowy GPO zachowuje swoją wartość, ustawienie nie jest kopiowane |
| **Połącz listy** | przypisania praw użytkownika i grupy z ograniczeniami dostają konta z obu list (to samo konto zapisane nazwą i SID-em liczy się raz); pozostałe ustawienia są zastępowane |

Jedno kopiowanie obejmuje ustawienia z **najwyżej 10** GPO źródłowych (okno mówi, gdy zaznaczono więcej). Ustawienie
wybrane jednocześnie z dwóch GPO nie jest kopiowane wcale – trzeba je wybrać z jednego.

**Czego nie da się skopiować.** Ustawienia odrzucone są w oknie wyszarzone, z powodem w kolumnie *Uwaga*, i nie
zatrzymują kopiowania pozostałych:

| Ustawienie | Powód w oknie |
|---|---|
| hasła w preferencjach (`cpassword`, **MS14-025**), `DefaultPassword`, community SNMP i inne wartości ukryte | *Zawiera wartość tajną lub ukrytą (np. hasło GPP, MS14-025) – nigdy nie jest kopiowane.* |
| skrypty uruchamiania, zamykania, logowania i wylogowania oraz ich pliki | *Skrypty i pliki skryptów nie są kopiowane – dodaj je w GPMC.* |
| uprawnienia rejestru i systemu plików (*Rejestr*, *System plików* w ustawieniach zabezpieczeń) | *Uprawnienia rejestru i systemu plików nie są kopiowane – ustaw je w GPMC.* |
| opcje inspekcji i globalna inspekcja dostępu do obiektów | *Opcje inspekcji i globalna inspekcja dostępu do obiektów nie są kopiowane – ustaw je w GPMC.* |
| komentarze GPO i ustawień | *Komentarze nie są kopiowane.* |
| ustawienie z pliku, którego nie udało się odczytać; ustawienie o zbyt długiej nazwie | *Nie udało się odczytać pliku.* / *Nazwa ustawienia jest zbyt długa, aby je jednoznacznie rozpoznać.* |
| element preferencji, którego program nie umie jednoznacznie rozpoznać, i ustawienia innych rodzajów | *AD Manager nie kopiuje ustawień tego rodzaju – zmień je w GPMC.* |
| element preferencji w kolekcji z kierowaniem na poziomie elementu albo w wyłączonej kolekcji (skopiowany poza kolekcję straciłby jej warunki) | *Element jest w kolekcji z określaniem docelowym lub w wyłączonej kolekcji – skopiuj go w GPMC.* |

Odrzucane są też, z komunikatem w podglądzie planu: zasada ADMX, gdy szablony administracyjne nie są wczytane;
wartość rejestru, która w docelowym GPO należy do skonfigurowanej zasady ADMX; preferencja, której rozszerzenia
przetwarzającego (CSE) nie ma ani w GPO źródłowym, ani w docelowym (klienci by ją pominęli); wszystkie ustawienia GPO
odczytanego częściowo. **Nigdy nie są kopiowane** ustawienia przechowywane w katalogu (instalacja oprogramowania, zasady
sieci przewodowej i bezprzewodowej) – podgląd o tym ostrzega, jak przy kopii GPO (rozdział 3).

**Ostrzeżenia w podglądzie** (kopiowanie idzie dalej): wartość zmieniła się od wyszukania (kopiowana jest wartość
odczytana teraz); ustawienie pochodzi z wyłączonej części GPO źródłowego (tam nie działa, w docelowym zacznie działać);
zasady haseł i blokady kont w GPO niepołączonym z domeną (działają tylko dla kont lokalnych komputerów), zasady Kerberos
w takim GPO (bez skutku); element preferencji z opcją *Usuń ten element, gdy nie jest już stosowany* (klienci najpierw
go cofną – odłączą dysk, usuną i ponownie utworzą użytkownika lub grupę – zanim zadziała nowy element).

**Podgląd planu** to jeden plan dla docelowego GPO: nazwy rozszerzeń (tylko gdy się zmieniają), każdy zmieniony plik
(`Registry.pol`, `GptTmpl.inf`, `audit.csv`, pliki preferencji – z *Pokaż różnice*) i jedno podniesienie wersji. Sekcja
**Zmiany ustawień (N)** pokazuje każde ustawienie z wartością przed i po (grupa: *nazwa GPO › obszar*; wartości ukryte
zostają ukryte). Wszystkie bajty pochodzą z plików odczytanych w chwili planowania, nie z wyników wyszukiwania; nowy
element preferencji dostaje nowy identyfikator i trafia na koniec listy docelowego GPO (stosowany jako ostatni).
Zakładka *PowerShell* podaje `Set-GPRegistryValue` dla wartości rejestru i komentarz, że pozostałe pliki zapisuje plan
(moduł `GroupPolicy` nie ma na nie poleceń). Gdy nic by się nie zmieniło (każde ustawienie odrzucone, pominięte albo
już takie samo), zamiast podglądu jest okno błędu *Nic nie zmieniłoby się w …* z każdym ustawieniem i powodem – odmowa
MS14-025 nigdy nie znika bez słowa. **Cofnij** w oknie podglądu przywraca pliki docelowego GPO (z kolejnym podniesieniem
wersji).

### Usuwanie ustawienia

**Usuń duplikat z tego GPO** (porządki, propozycja *Zduplikowane ustawienie*) usuwa ustawienie z jednego GPO: zasadę
ADMX ustawia na *Nie skonfigurowano*, usuwa wiersze `GptTmpl.inf` i `audit.csv`, wpisy `Registry.pol` i elementy
preferencji (pusta kolekcja zostaje, jak w GPMC). Ten sam podgląd, te same odmowy i to samo ostrzeżenie o opcji *Usuń
ten element, gdy nie jest już stosowany*; *Cofnij* przywraca ustawienie.

### Scalanie dwóch GPO

**Scal z…** (porządki, propozycje *Kandydat do scalenia* i *Obiekt GPO z jednym ustawieniem*) otwiera okno
**Scal A z B** i jednym planem z podglądem i *Cofnij*:

1. **kopiuje** do B wszystkie ustawienia A (bez ustawień wyłączonej części A – tam nie działały; komentarze i nieużywane
   pliki skryptów nie są przenoszone, podgląd to mówi);
2. **zastępuje połączenia**: w każdym kontenerze domeny i lokacji, z którym połączony jest A, B zajmuje miejsce A z jego
   opcjami (kolejność, wymuszenie, włączenie); gdy B jest tam już połączony, połączenie A jest tylko usuwane (połączenie
   B zostaje);
3. **wyłącza A** (stan *Wszystkie ustawienia wyłączone*). Usunięcie A to osobny krok (*Usuń GPO…* z propozycji, z
   kopią zapasową) – usunięcia nie da się cofnąć.

**Które wartości wygrywają**, gdy oba GPO ustawiają to samo – okno podpowiada według kolejności połączeń
(*Według kolejności połączeń: Zastąp* albo *Pomiń*): gdy B ma wyższe pierwszeństwo wszędzie tam, gdzie oba są połączone,
wartości B zostają; gdy A – wartości A zastępują wartości B; gdy nie mają wspólnego kontenera – wartości A zastępują
wartości B. Gdy kolejność jest różna w różnych miejscach, trzeba wybrać samemu (*Zastąp*, *Pomiń*, *Połącz listy*).

**Sprawdź przed scaleniem** – okno i podgląd ostrzegają, gdy: GPO są stosowane do różnych użytkowników, grup lub
komputerów (filtrowanie zabezpieczeń) albo mają różne filtry WMI; są połączone w różnych miejscach (*Tylko A jest
połączony z: … – po scaleniu te miejsca dostaną też pozostałe ustawienia B* i odwrotnie); A ma we wspólnym kontenerze
wyższe pierwszeństwo, wymuszenie lub włączone połączenie, gdy B nie (ustawienia A zajmą słabszą pozycję B – B nie jest
przesuwany); las ma inne domeny (ich połączenia z A nie są zastępowane); część B wyłączona tam, gdzie A ma ustawienia;
elementy preferencji A z opcją *Usuń ten element, gdy nie jest już stosowany*.

**Scalenie jest odmawiane**, gdy A ma ustawienia, których nie da się skopiować (lista *Tych ustawień nie można
skopiować, więc obiektów GPO nie można scalić:* z powodami), ustawienia przechowywane w katalogu, pliki, których AD
Manager nie odczytuje (np. przekierowanie folderów, instalacja oprogramowania), gdy A lub B odczytano częściowo, gdy A
jest jednym z dwóch domyślnych GPO albo gdy A i B to ten sam GPO. **Cofnij** przywraca pliki B, połączenia i stan A.

### Porządki w GPO

Karta **Porządki w GPO** analizuje odczyt wszystkich GPO (ten sam co wyszukiwarka) i pokazuje propozycje: liczniki
według rodzaju nad listą (kliknięcie filtruje), filtry *Rodzaj:* i *Obiekt GPO:*, wyszukiwanie w propozycjach (Ctrl+F),
lista z kolumnami *Rodzaj*, *Obiekt zasad grupy*, *Propozycja* i *Działania*, a w szczegółach: **Co znaleziono**, **Co
zrobić**, **Obiekty GPO** (z połączeniami), **Ustawienie** (w języku GPMC) i **Uwagi**. *Odśwież* (F5), *Odczytaj
wszystko ponownie*, *Anuluj*, pasek *Zasady grupy zmieniono po analizie – propozycje mogą być nieaktualne.* i eksport
(*Eksportuj do CSV…*, *Eksportuj do HTML…* – do pliku operatora) działają jak w wyszukiwarce. Treść GPO oceniana jest
tylko dla GPO odczytanych w całości; pozostałe mają ocenione tylko połączenia i stan (pasek *Nie odczytano wszystkich
ustawień N obiektów GPO …*).

| Rodzaj | Kiedy | Działania | Uwagi |
|---|---|---|---|
| **Pusty obiekt GPO** | GPO nie ma żadnych ustawień – także w katalogu i w plikach, których program nie odczytuje; wyłączony element preferencji to też ustawienie (uśpiona treść to nie „pusty GPO”) | *Usuń GPO…*, *Odłącz wszędzie…* (gdy połączony) | połączony GPO: usunięcie usunie też połączenia; domyślne GPO – bez usuwania |
| **Obiekt GPO bez połączeń** | brak połączenia z domeną, OU i lokacją tej domeny – nie jest stosowany do nikogo | *Usuń GPO…*, *Wyłącz GPO* | las z innymi domenami: ich połączeń nie widać z tego kontrolera |
| **Wszystkie połączenia wyłączone** | każde połączenie GPO jest wyłączone | *Odłącz wszędzie…*, *Wyłącz GPO*, *Usuń GPO…* | |
| **Obiekt GPO wyłączony** | wyłączone obie części (komputera i użytkownika) | *Usuń GPO…*, *Odłącz wszędzie…* (gdy połączony) | |
| **Obiekt GPO z jednym ustawieniem** | dokładnie jedno ustawienie, bez ustawień w katalogu i nieodczytywanych plików | *Kopiuj do innego GPO…* (potem osobno *Wyłącz GPO*); *Scal z…*, gdy istnieje GPO połączony w tych samych miejscach (kontener, włączenie, wymuszenie), z tym samym filtrowaniem zabezpieczeń i filtrem WMI | ustawienia nie da się skopiować – bez kopiowania i scalania |
| **Zduplikowane ustawienie** | ustawienie ma tę samą wartość w innym GPO, który jest stosowany wszędzie tam, gdzie ten (opis niżej) | *Usuń duplikat z tego GPO* | opcja *Usuń ten element, gdy nie jest już stosowany*; zasady konta tylko w GPO połączonych z domeną |
| **Różne wartości jednego ustawienia** | to samo ustawienie ma różne wartości w różnych GPO, które działają | tylko informacja: *Otwórz GPO*, *Porównaj*, *Pokaż w wyszukiwarce* | |
| **Kandydat do scalenia** | dwa lub więcej GPO połączonych w tych samych miejscach, z tym samym filtrowaniem zabezpieczeń i filtrem WMI; każdy jest proponowany do scalenia z tym, który ma najwięcej ustawień | *Scal z…* | kolejność połączeń różna; opcja *Usuń ten element…*; bez scalania, gdy GPO ma ustawienia, których nie da się skopiować, ustawienia w katalogu, nieodczytywane pliki albo wyłączoną część z ustawieniami |

**Zduplikowane ustawienie** jest ostrożne, bo ta sama wartość w dwóch GPO nie zawsze czyni jedną z nich zbędną (prawa
użytkownika i grupy z ograniczeniami zastępują się w całości, preferencje działają po kolei, zasięgi mogą się nie
pokrywać). Usunięcie z GPO X jest proponowane tylko, gdy: oba GPO działają (mają włączone połączenie, część z
ustawieniem nie jest wyłączona, wartość jest porównywalna – nie ukryta, nie warunkowa, nie skrypt); żadne inne
działające GPO nie ustawia tu innej wartości (wtedy jest *Różne wartości jednego ustawienia*); drugi GPO Y jest
stosowany **wszędzie** tam, gdzie X – w każdej domenie, OU i lokacji (z blokowaniem dziedziczenia i wymuszeniem), do
tych samych użytkowników i komputerów (filtrowanie zabezpieczeń) i z tym samym filtrem WMI albo bez filtru. Dwa GPO z
tym samym mapowaniem dysku na rozłącznych OU nie dają propozycji – pokaże je wyszukiwarka. GPO są uszeregowane, więc
wykonanie wszystkich propozycji nigdy nie usuwa ostatniej kopii ustawienia. Zasady konta (hasła, blokada, Kerberos) są
oceniane tylko w GPO połączonych z domeną; wpisy usuwające cały klucz rejestru (`**delvals.`) nie są proponowane.

**Działania** – każde przez podgląd planu (z *Cofnij* – poza usunięciem GPO), a po wykonaniu karta odświeża odczyt i
analizuje ponownie:

- **Usuń GPO…** – najpierw program sprawdza, czy GPO nie zmienił się od analizy (wersja i połączenia): gdy w innym oknie
  lub przez innego administratora dostał ustawienia albo połączenia, usunięcie jest odmawiane (*… zmienił się
  (ustawienia lub połączenia) od analizy, więc nie zostanie usunięty*), a lista jest odczytywana ponownie. Potem zwykłe
  usuwanie z rozdziału 3: propozycja kopii zapasowej (*Utwórz kopię zapasową przed usunięciem*) i przepisanie nazwy GPO;
  plan sprawdza stan jeszcze raz aż do wykonania. Usunięcia GPO **nie da się cofnąć** – dlatego kopia zapasowa.
- **Odłącz wszędzie…** – usuwa połączenia GPO ze wszystkich kontenerów domeny i lokacji (ostrzeżenie, gdy las ma inne
  domeny); **Wyłącz GPO** – stan *Wszystkie ustawienia wyłączone*;
- **Kopiuj do innego GPO…**, **Usuń duplikat z tego GPO**, **Scal z…** – jak wyżej;
- **Otwórz GPO**, **Porównaj** (z powiązanym GPO), **Pokaż w wyszukiwarce**, **Kopiuj** (tekst propozycji).

Domyślnych GPO (*Default Domain Policy*, *Default Domain Controllers Policy*) porządki nigdy nie proponują usunąć ani
scalić.

### Co zostaje tylko w pamięci

Odczyt ustawień wszystkich GPO, nazwy kont, wyniki wyszukiwania i propozycje porządków są tylko w pamięci okna
połączenia – znikają przy zmianie kontrolera, rozłączeniu i zamknięciu okna. Program nie zapamiętuje historii
wyszukiwania; dziennik diagnostyczny zawiera tylko liczby i czasy. Pliki powstają wyłącznie przy eksporcie do CSV lub
HTML – tam, gdzie wskaże operator (rozdział 14).

## 13. Kontroler domeny, emulator PDC i SYSVOL

- Okno czyta i zapisuje AD oraz SYSVOL **tylko na swoim kontrolerze domeny**. Gdy nie jest nim emulator PDC (z którego
  domyślnie korzysta GPMC), karty GPO pokazują pod wierszem stanu uwagę: *Zmiany GPO wykonujesz na X, a emulatorem PDC
  jest Y* z odnośnikiem *Zmień kontroler…*; program nigdy nie przełącza kontrolera sam.
- SYSVOL otwierany jest poświadczeniami okna (hasło tylko w pamięci, zasada 2) tylko na czas odczytu, planowania i
  wykonania. Gdy jest niedostępny, uwaga pod wierszem stanu karty podaje przyczynę (połączenie otwarte nazwą
  wyróżniającą, środowisko nieobsługiwane, odrzucone poświadczenia, odmowa dostępu, brak połączenia z udziałem), a
  operacja, która potrzebuje SYSVOL, mówi w wierszu stanu, że niczego nie zmieniono. Wtedy wyłączone są tylko
  zmiany zapisujące SYSVOL (nowe GPO, kopia, usuwanie, filtrowanie zabezpieczeń, delegowanie, przywracanie, import,
  wyrównanie wersji, naprawa uprawnień, zapis szablonów administracyjnych, aktualizacja magazynu centralnego,
  kopiowanie i usuwanie ustawień, scalanie GPO); połączenia, stan GPO, zmiana nazwy, filtr WMI i blokowanie
  dziedziczenia zmieniają tylko AD i działają dalej. Wyszukiwarka i porządki (rozdział 12) mają wtedy tylko dane z
  katalogu.

## 14. Co AD Manager zapisuje na dysku

Nic z katalogu ani z SYSVOL (zasada 1): GPO, połączenia, ustawienia, szablony ADMX i różnice są tylko w pamięci, dopóki
są widoczne. Zapamiętywane są wyłącznie: widoczność węzłów GPO w drzewie, ostatni folder kopii zapasowych, szablon
komentarza kopii i wybór *Utwórz kopię zapasową przed usunięciem*. Pliki powstają tylko tam, gdzie operator je wskaże:
kopie zapasowe GPO, kopia magazynu centralnego przed aktualizacją, eksport porównania i modelu (rozdział 9) do CSV,
raport HTML ustawień GPO, eksport diagnostyki (rozdział 11), wyników wyszukiwania ustawień i propozycji porządków
(rozdział 12) do CSV lub HTML. Wyniki diagnostyki są tylko w pamięci karty, a odczyt ustawień wszystkich GPO, wyniki
wyszukiwania i propozycje porządków – tylko w pamięci okna połączenia (bez historii wyszukiwania).
Ustawienia zabezpieczeń (rozdział 10) są tylko w pamięci karty do zapisu w SYSVOL. Paczki szablonów
producentów (pliki producentów, nie dane katalogu) są trzymane w pamięci; tylko instalatory MSI i EXE oraz archiwa CAB
są kopiowane do folderu tymczasowego profilu (instalatory są tam też rozpakowywane, CAB – w pamięci), usuwanego zaraz po
wczytaniu. Dziennik audytu zawiera
przy zmianach SYSVOL ścieżki plików w folderze GPO lub magazynie centralnym, rozmiary i sumy SHA-256 – nie treść
plików.

## 15. Ograniczenia i pilotaż

- Sprawdzone testami na fałszywym katalogu i na laboratorium Samba 4.19 (zapis SYSVOL przez `smbclient`); zapis SYSVOL
  na Windows (`WindowsSysvolFileWriter`), okna WPF, zrzut ekranu 23 i test dymny sprawdza tylko CI na Windows.
- Szablony administracyjne i magazyn centralny (K3) sprawdzono testami na fałszywym katalogu i syntetycznych szablonach
  (polskie ADMX/ADML przygotowane do testów, nie pliki producentów); edytor i okno aktualizacji – CI na Windows.
  Pobieranie szablonów Edge, Chrome i Firefox sprawdzono na odpowiedziach przygotowanych według formatów producentów
  (środowisko testowe nie ma dostępu do ich serwerów). Rozpakowywanie MSI, EXE i CAB oraz sprawdzanie podpisu działa
  tylko w Windows: sprawdzanie podpisu (na bibliotece .NET podpisanej przez Microsoft) i rozpakowywanie CAB (na plikach
  testowych z `makecab`) sprawdza CI na Windows, instalatory MSI i EXE Microsoftu – pilotaż.
- Do potwierdzenia w pilotażu (Windows Server 2025, Windows 11): GPMC odtwarza kopie AD Managera, a AD Manager kopie
  GPMC (z `gpreport.xml` i bez); GPMC nie zgłasza niespójnych uprawnień po zmianach AD Managera; maski poziomów
  uprawnień GPMC i format `msWMI-Parm2` filtrów WMI; GPO utworzone w AD Managerze działa jak utworzone w GPMC
  (`gpresult /h`); `Registry.pol` zapisany przez edytor szablonów jest identyczny z plikiem edytora Microsoftu dla tych
  samych ustawień, a klient stosuje je jak ustawione w GPMC; aktualizacja magazynu centralnego z bieżących paczek
  Windows 11 / Server 2025, Microsoft 365 Apps, Edge, Chrome i Firefox – w tym to, że instalatory MSI i EXE Microsoftu
  przechodzą sprawdzenie łańcucha certyfikatu Microsoftu (gdy któryś nie przejdzie: rozpakuj go ręcznie, np. `msiexec
  /a plik.msi TARGETDIR=C:\Szablony`, i wskaż folder przez *Z folderu…*).
- Modelowanie (K5) sprawdzono testami na fałszywym katalogu i na laboratorium Samba (model komputera w OU z
  blokowaniem dziedziczenia zgodny z `samba-tool gpo list`); w pilotażu – porównanie z *Modelowaniem zasad grupy* GPMC i
  z `gpresult /h` na komputerze testowym.
- Ustawienia zabezpieczeń (K4) sprawdzono testami na fałszywym katalogu (zapis wierszy szablonu, `audit.csv` i
  `Groups.xml` przy zachowaniu reszty pliku, plan, konflikt); w pilotażu – że GPMC pokazuje zapisane ustawienia jak
  swoje, `secedit /analyze` i `gpresult /h` na kliencie oraz że klient stosuje zaawansowaną inspekcję i grupy
  preferencji z nazwami rozszerzeń zapisanymi przez AD Managera.
- Czytelne ustawienia, wygodę edycji i diagnostykę (K6) sprawdzono testami na fałszywym katalogu (każda reguła
  diagnostyki na przykładach z polskimi danymi, teksty wszystkich uwag w obu językach); okna – CI na Windows; w
  pilotażu – porównanie nazw ustawień z raportem GPMC (`Get-GPOReport`) i uwag diagnostyki z wiedzą o domenie.
- Wyszukiwanie, kopiowanie ustawień i porządki (K7) sprawdzono testami na fałszywym katalogu (przykładowa firma i GPO
  *Porządki – …* z polskimi danymi: kopiowanie i usuwanie każdego rodzaju ustawienia z wykonaniem i cofnięciem, odmowy z
  MS14-025 włącznie, scalanie z zastąpieniem połączeń, każdy rodzaj propozycji razem z przypadkami, w których propozycji
  być nie może); okna i zrzuty 34 i 35 – CI na Windows; **nie sprawdzono na prawdziwej domenie Windows**. W pilotażu:
  wyniki wyszukiwania wobec *Znajdź* w edytorze GPMC i `gpresult /h`, skopiowane ustawienia w GPMC i `gpresult /h` na
  komputerze testowym, scalenie dwóch testowych GPO, propozycje porządków wobec obrazu w GPMC. Nie są kopiowane ani
  scalane: skrypty, uprawnienia plików i rejestru, opcje inspekcji, komentarze, wartości ukryte (hasła GPP – MS14-025),
  elementy kolekcji z kierowaniem lub wyłączonych, ustawienia w katalogu; brak kopiowania do nowego GPO i między
  domenami; scalenie nie przesuwa połączenia B na pozycję A.
- Brak: edycji reguł zapory, uprawnień plików i rejestru, dzienników zdarzeń, użytkowników lokalnych i pozostałych
  preferencji, grup lokalnych w konfiguracji użytkownika, komentarzy do ustawień
  szablonów, filtrów edytora według platformy i słów kluczowych, starych szablonów ADM, tabel migracji i kopiowania GPO
  między domenami, GPO wzorcowych (Starter GPO), porównania GPO z innego okna połączenia, próby filtru WMI na komputerze
  (wymaga zapytań WQL w zdalnych akcjach K1), sprawdzania uprawnień operatora przed zmianą.
