<!-- DRAFT of the next release notes (docs/dev/releasing.md). Every user-visible change adds its paragraph here while it
     is being developed. `tools/release/release.sh` turns this file into docs/releases/v<version>.md: it drops this
     comment, fills {{VERSION}}, {{PREVIOUS}} and {{DOWNLOADS}}, and refuses to release while a TODO comment or an
     empty section is left. Sections "Nowe: ...", "Inne zmiany" and "Co dalej" may be removed or repeated; the others
     are required. Notes are in Polish with an English summary at the end. -->
<!-- TODO: wstęp – jeden akapit: która to wersja zapoznawcza i co w niej najważniejsze (pogrubione nazwy funkcji) -->

> **Wersja zapoznawcza ({{VERSION}}).** <!-- TODO: czym zostało sprawdzone i czego nie uruchomiono na prawdziwej domenie Windows Server -->
> Lista kontrolna pilotażu: [PILOTAZ.md](https://github.com/DarienRahl/AD_MNG/blob/v{{VERSION}}/docs/PILOTAZ.md).

## Pobieranie

{{DOWNLOADS}}

## Nowe: odnowa interfejsu – dokończenie etapu V2

Etap V2 odnowy interfejsu (wiersz stanu, przyciski akcji z anulowaniem, uwagi) z wersji {{PREVIOUS}} jest dokończony: wiersz stanu
nie miga przy szybkich operacjach, czytnik ekranu mówi, ile znaleziono, puste listy mówią, dlaczego są puste, wyniki sprawdzeń są
plakietkami, przycisk okna na pasku zadań pokazuje postęp, a zamknięcie okna w trakcie zapisu najpierw pyta. Działanie funkcji
się nie zmienia, a zapisy do katalogu nadal idą wyłącznie przez plan zmian z podglądem.

- **Wiersz stanu nie błyska przy szybkich operacjach, a czytnik ekranu mówi, ile znaleziono.** Stan *Trwa* z kółkiem
  postępu pojawia się dopiero po około 0,15 s od początku operacji: operacja, która kończy się od razu (np. odczyt jednej
  zakładki właściwości albo przygotowanie zmian przed podglądem), nie miga już wierszem stanu. Do tego czasu wiersz
  pokazuje to, co pokazywał – a pusty zostaje pustym paskiem, więc zawartość pod nim się nie przesuwa; przycisk, którym
  uruchomiono operację, zmienia się na *Anuluj* od razu. Zmieniająca się liczba w podsumowaniu strony – np. liczba
  ustawień znalezionych w wyszukiwarce ustawień zasad grupy – jest odczytywana przez czytnik ekranu uprzejmie (bez
  przerywania mowy) i tylko raz, około 0,8 s po ostatniej zmianie, a nie po każdym wpisanym znaku; nie jest odczytywana,
  gdy okno nie jest aktywne ani gdy strona nie jest na wierzchu, i nie jest odczytywane pierwsze podsumowanie otwartej
  strony. Uwaga o replikacji w wynikach kreatora zakładania kont jest informacją, więc ma ikonę informacji zamiast
  ostrzeżenia (kolor uwagi bez zmian).
- **Puste listy mówią, dlaczego są puste, i podpowiadają następny krok; pierwszy odczyt pokazuje szare wiersze zastępcze;
  wyniki sprawdzeń są plakietkami.** Lista obiektów (okno główne i okno *Znajdź*), lista obiektów zasad grupy i lista
  szablonów certyfikatów, które zostały odczytane i nie mają wierszy, pokazują duży obrazek, jedno zdanie i jedno
  działanie na środku listy: *Brak elementów do pokazania w tym widoku.* z działaniem *Odśwież*, a gdy filtr lub
  wyszukiwanie ukrywa wszystko – np. *Żaden obiekt nie pasuje do filtra.* z działaniem *Wyczyść filtr* (usuwa tekst i
  wszystkie przyciski filtrów i czyta listę jeden raz), *Żaden GPO nie pasuje do wyszukiwania.* z *Wyczyść wyszukiwanie*,
  *Żaden szablon nie pasuje do filtrów.* z *Wyczyść filtry*. Dawny osobny tekst o braku dopasowania na liście zasad grupy
  zniknął – zdanie jest jedno. Lista, którą odczytywano z błędem albo zatrzymano, nie jest „pusta”: mówi o tym wiersz
  stanu, jak dotąd. Gdy lista obiektów czyta kontener po raz pierwszy (nie przy odświeżeniu, które ma już wiersze), pod
  nagłówkami kolumn pokazują się szare wiersze zastępcze (najwyżej 12) – po około 0,15 s, więc odczyt, który kończy się od
  razu, ich nie pokazuje; bez ruchu stoją nieruchomo, z ruchem delikatnie „oddychają”. Wynik każdego sprawdzenia w raporcie
  gotowości środowiska i stan każdego sprawdzenia na karcie *Stan* AdMng Watchdog to teraz plakietka: obrazek stanu i
  słowo (OK, Ostrzeżenie, Błąd, Nie sprawdzono…), w jednej kolumnie przed tytułem; obszary raportu mają plakietkę swojego
  najgorszego wyniku. Stan nie jest już podawany samym kolorem ani powtórzony za tytułem.
- **Przycisk okna na pasku zadań pokazuje postęp, a zamknięcie okna w trakcie zapisu najpierw pyta.** Okno AD Managera, które
  ma własny przycisk na pasku zadań – okno główne, edytor zbiorczy, Kosz AD, *Korespondencja*, wiadomości do użytkowników,
  przeglądy kont i komputerów, działania zdalne, porównania, okno PowerShell – pokazuje na nim to, co mówi jego wiersz stanu
  (okna dialogowe bez własnego przycisku, np. właściwości, reset hasła, podgląd planu, kopia zapasowa GPO, mówią to tylko
  w wierszu stanu): ile już zrobiono (zielony pasek – np. przy
  wykonywaniu zmian na wielu obiektach albo przy wysyłce, która trwa minuty), że operacja trwa, a nie wiadomo, ile jeszcze
  (pasek w ruchu), albo – gdy operacja, której postęp było widać, skończyła się stanem, który trwa (np. nie udało się odczytać
  listy) – żółty lub czerwony pasek, dopóki strona nie powie czegoś innego. Pasek pojawia się po tym samym czasie co stan
  *Trwa* (około 0,15 s), działa także przy wyłączonych animacjach, a okno główne pokazuje postęp karty, która jest na wierzchu.
  Zamknięcie okna w trakcie wykonania zmian (edytor zbiorczy i kreator zakładania kont, reset hasła, przywracanie z Kosza AD)
  pyta, co zrobić z wykonaniem, i daje trzy odpowiedzi, tak samo z paska tytułu, przyciskiem *Zamknij* (*Anuluj*) i klawiszem
  Esc: *Zatrzymaj i zamknij* – nic nowego nie jest zaczynane, obiekt w toku zostaje dokończony, a okno zamyka się po końcu
  wykonania; *Zamknij i kontynuuj w tle* – okno zamyka się od razu, a wykonanie trwa do końca na liście operacji w tle (w edytorze
  zbiorczym i w Koszu AD tak było przed tą wersją); *Nie zamykaj* (także Enter i Esc). Jak skończyło się wykonanie, którego wyniku
  okno już nie pokaże – bo zostało w tle albo okno zamknęło się zaraz po jego końcu – mówi okno główne. Gdy nie było
  niepowodzeń, jest to powiadomienie, które przy zatrzymaniu mówi, ile obiektów wykonano (np. *Wykonywanie „…” w domenie …
  zatrzymano: wykonano 1 z 5 obiektów, niepowodzenia: 0…*); gdy coś się nie udało albo całego wykonania nie dało się
  przeprowadzić – okno z przyczynami, które otwiera się dopiero po zamknięciu tamtego okna. Każda zmiana jest w dzienniku sesji.
  Tak jest też w edytorze zbiorczym, który swoje powiadomienie z liczbami udanych, nieudanych i pominiętych obiektów pokazuje
  tylko wtedy, gdy zostaje otwarty. Kontynuacji w tle nie ma, gdy wykonanie generuje hasła (istnieją
  tylko w oknie), w podglądzie planu zmian GPO (okno, z którego go otwarto, czeka na wynik) i w kreatorze, dopóki nie powstanie
  pierwsze konto – pytanie mówi wtedy, dlaczego. Inne zapisy – wysyłka wiadomości, aktualizacja magazynu centralnego, kopia
  zapasowa GPO, polecenie dla komputera – pytają z dwiema odpowiedziami: *Zatrzymaj i zamknij* albo *Nie zamykaj*; zapis
  właściwości obiektu, utworzenie obiektu i zapis konfiguracji Watchdoga, których nie da się zatrzymać, nie są pytaniem: okno
  mówi, że jeszcze pracuje, i zostaje otwarte do ich końca. Okno, które tylko czyta, zamyka się bez pytania, a odczyt jest
  przerywany, jak dotąd. Okno główne zadaje jedno pytanie o wszystko, co trwa w jego kartach, w jego oknach i w tle, bez
  kontynuacji w tle (jego zamknięcie kończy połączenie), a *Zatrzymaj i zamknij* czeka na koniec obiektów w toku. Pytanie
  obejmuje także zapis, który stracił wiersz stanu na rzecz odczytu, i zapis zaczęty, gdy pytanie było już otwarte. Okno zamyka
  się zgodnie z odpowiedzią także wtedy, gdy wykonanie skończyło się, kiedy pytanie było otwarte, albo gdy okno po wykonaniu
  jeszcze odczytuje listę (Kosz AD) – wtedy po końcu tego odczytu; wykonanie zatrzymane wcześniej (*Zatrzymaj* w wierszu stanu
  albo na liście operacji w tle) nie potrzebuje pytania: okno zamyka się po jego końcu. Drugie zamknięcie, gdy okno czeka na
  zatrzymanie, nie pyta ponownie. W wersji {{PREVIOUS}} przy zamknięciu w trakcie wysyłki pytały okna *Korespondencja*
  i *Wyślij wiadomość do użytkowników*, a okno główne, gdy trwały operacje w tle, pytało: *Czy mimo to zamknąć okno? Trwające
  operacje zostaną przerwane.* – i zamknięcie okna przerywało te operacje. Tego pytania już nie ma: zastępuje je opisane wyżej
  jedno pytanie okna głównego, którego *Zatrzymaj i zamknij* czeka na koniec obiektów w toku.

## Nowe: okno PowerShell (R1)

Z okna połączenia można teraz otworzyć okno **PowerShell** (*Narzędzia → PowerShell…*, **Ctrl+Shift+P** albo paleta poleceń):
wiersz poleceń z podpowiadaniem, kolorowaniem i historią, w którym działają polecenia AD Managera – tylko do odczytu – na
sesji tego okna, czyli tym samym kontrolerze domeny i z tymi samymi uprawnieniami co okno główne. Nic w nim nie zapisuje do
katalogu: polecenia zapisu (przez plan zmian) i moduły RSAT są dopiero w następnych etapach ścieżki R (R2 i R3). Instrukcja:
[POWERSHELL.md](https://github.com/DarienRahl/AD_MNG/blob/v{{VERSION}}/docs/POWERSHELL.md).

- **Polecenia.** `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer`, `Get-AdmGroupMember` (z `-Recursive`
  dla członków zagnieżdżonych – wynik zawiera też same grupy zagnieżdżone, tak jak lista *Wszystkie (zagnieżdżone)*;
  `-ExcludeGroups` je pomija), `Search-AdmObject` (wartości są zawsze dosłowne, więc `Łukasz (Kraków)*` znajduje tylko to, co
  napisano), `Get-AdmVersion`, `Out-AdmGrid`, `Open-AdmObject` i `Export-AdmList`. Wyniki są zwykłymi obiektami PowerShella
  (`Where-Object`, `Select-Object`, `Sort-Object`… działają) i domyślnie są jedną tabelą także wtedy, gdy są różnych rodzajów
  (członkowie grupy, wyniki wyszukiwania), z wartościami dekodowanymi tak jak w oknach programu, a hasła i
  klucze, których edytor atrybutów nie pokazuje, są zamaskowane. Wyniki są czytane strumieniem – 100 000 obiektów nie blokuje
  okna, a **Stop** (przycisk albo Ctrl+C) przerywa odczyt na kontrolerze domeny. Okno przyjmuje wyniki porcjami, najwyżej jedną
  co 40 ms, i dopiero po klawiaturze, myszy i rysowaniu, więc polecenie, które pisze bez przerwy (`while ($true) { 'x' }`), nie
  odbiera działania Ctrl+C ani przyciskowi **Stop** (Ctrl+C sprawdza test automatyczny, kliknięcia na ekranie nikt jeszcze nie
  próbował). Błędy i ostrzeżenia poleceń są w języku
  interfejsu (pomoc `Get-Help` i komunikaty samego silnika PowerShell są po angielsku), a odczyt, którego kontroler domeny nie
  wykonał (przekroczony czas, odmowa dostępu), jest jednym błędem tej przyczyny, nie także „nie znaleziono”. Okno zachowuje ostatnie 20 000 wierszy po
  najwyżej 8000 znaków (dłuższy tekst ciągnie się w następnym wierszu), a postęp pisany kropka po kropce
  (`Write-Host -NoNewline`) nie spowalnia go.
- **Co jest w oknie.** Wiersz stanu na górze mówi, co robi polecenie, i ma **Stop**; błędy i ostrzeżenia mają ikonę
  (nie tylko kolor), a nazwa takiego wiersza dla czytnika ekranu zaczyna się od „Błąd:” albo „Ostrzeżenie:” (uwagi o zasadach – od
  „Uwaga:”); nazwa wyróżniająca w wyniku jest odnośnikiem (*Otwórz w programie*, prawy przycisk: *Kopiuj nazwę
  wyróżniającą*, Enter otwiera pierwszą nazwę zaznaczonego wiersza) – z wyjątkiem nazwy uciętej albo takiej, którą `Format-List`
  mógł złamać na szerokości okna, bo ta otworzyłaby inny obiekt. **Tab** uzupełnia (polecenia, parametry, zmienne, nazwy
  atrybutów ze schematu i kontenery, które okno już zna – bez odczytu z katalogu przy każdym znaku; nie ma uzupełniania nazw
  plików; uzupełnianie trwa najwyżej 5 sekund, więc zapętlona własna funkcja `TabExpansion2` nie blokuje okna), **Enter**
  uruchamia kompletne polecenie albo dodaje wiersz, gdy jest niedokończone (otwarty cudzysłów, nawias, ukośnik
  pionowy), **Shift+Enter** zawsze dodaje wiersz, **Góra/Dół** chodzą po wierszach, a potem po historii, **Ctrl+R** przeszukuje
  historię, **Esc** zamyka listę, **Ctrl+L** czyści okno – tak samo `cls` (`Clear-Host`) w poleceniu, w miejscu, w którym stoi.
  `exit` kończy polecenie tam, gdzie stoi, a okno mówi to z kodem wyjścia i zostaje otwarte. Gdy polecenie czeka na odpowiedź, puste pole mówi
  „Wpisz odpowiedź i naciśnij Enter.” zamiast podpowiedzi do polecenia. O parametr obowiązkowy, który
  przyjmuje listę (np. `-Identity`), okno pyta element po elemencie (`Identity[0]`, `Identity[1]`…) aż do pustej odpowiedzi,
  jak konsola PowerShella – odpowiedź nie jest dzielona na przecinkach, więc nazwa wyróżniająca jest jedną tożsamością. Pytanie,
  które przyjdzie w trakcie pisania następnego polecenia, nie kasuje wpisanego tekstu (wraca po odpowiedzi), a pierwsze puste
  Enter po takim pytaniu z wyborem pyta jeszcze raz zamiast wybrać odpowiedź domyślną. Hasło z pola hasła trafia do polecenia
  wprost jako `SecureString`, a pole jest puste po odpowiedzi. Historia jest tylko w pamięci okna; wiersz z hasłem jawnym
  (`-AsPlainText`, przy `ConvertTo-SecureString` także skrócone do `-As` albo `-A`, `ConvertTo-SecureString '…'`, napis zamieniany
  na `[securestring]`, wartość wpisana wprost – także w nawiasach albo złożona z samych napisów – dla parametru, klucza tablicy
  skrótów – także przekazanej przez `@h` albo złożonego ze zmiennej, jak `"Pass$x"` – albo zmiennej o nazwie z `Password`, `Passphrase`, `Pwd`,
  `Secret`, `Token`, `ApiKey` i podobnych, również skróconej do `-Pa`, wartość bez nazwy parametru dla polecenia, którego nazwa
  mówi o haśle, napis, który trafia tam przez zmienną z tego samego wiersza, poświadczenie złożone z napisu i hasło programu
  konsolowego – `net user jan hasło`, `/pass:…`, `/pd:…`, `/rp …`, `psexec -p …`) nie trafia do niej, a w obszarze wyników
  rozpoznane hasło takiego wiersza jest zastąpione przez `***`. Filtr nie rozpozna każdego hasła,
  więc hasła nie wpisuje się do polecenia w ogóle.
- **Lista poleceń, tryb ograniczony i tryb zaufany.** Okno zaczyna w ConstrainedLanguage i ma zamkniętą listę poleceń: polecenia
  programu oraz zwykłe cmdlety do pracy z obiektami (`Where-Object`, `Select-Object`, `Sort-Object`, `Group-Object`,
  `Measure-Object`, `ForEach-Object` – bez `-Parallel` i `-AsJob`), formatowania, zmiennych i odczytu plików (`Get-Content`,
  `Import-Csv`); pełna lista jest w instrukcji. Nie ma w nim zadań w tle, wątków i osobnych runspace'ów (`-Parallel`,
  `Start-Job`, operator `&` na końcu potoku), sesji zdalnych, `Invoke-Expression`, `Start-Process`, ładowania modułów
  (`Import-Module`, `using module`) ani zestawów (`using assembly`, `Add-Type`), profili ani PSReadLine, a pliki skryptów
  (`.ps1`) się nie uruchamiają – na Windows okno odrzuca je niezależnie od polityki wykonywania. Kilka nazw, które łatwo wpisać
  odruchowo (`Start-Job`, `Export-Csv`, `Out-GridView` i inne), odpowiada komunikatem, czego użyć zamiast nich. Polecenie dłuższe
  niż 262 144 znaki albo zagnieżdżone zbyt głęboko (setki nawiasów lub bloków skryptu jeden w drugim albo łańcuch setek
  operatorów) okno odrzuca w całości z komunikatem, zanim cokolwiek z niego ruszy, i nie zapamiętuje go w historii: silnik
  PowerShella mógłby na nim zakończyć cały program. Żadne zadanie
  nie działa dłużej niż polecenie, które je uruchomiło: zadanie zostawione w sesji okno zatrzymuje, gdy polecenie się kończy,
  przy **Stop** i przy zamknięciu okna. Jedno dopasowanie wyrażenia regularnego (`-match`, `-replace`, `-split`,
  `switch -regex`, `Select-String`, `[regex]`) trwa najwyżej 2 sekundy, a potem kończy się błędem polecenia, więc wzorzec
  z katastrofalnym nawracaniem (np. `'^(a+)+$'`) nie zawiesza okna ani nie zajmuje procesora do zamknięcia programu; limit
  obowiązuje każde wyrażenie regularne programu, które nie ma własnego. Polecenie, którego **Stop** nie przerywa (kod .NET
  trybu zaufanego albo `[regex]` z własnym, dłuższym limitem czasu), okno zostawia po 5 sekundach i mówi o tym: następne
  polecenie działa od razu w nowej sesji, bez zmiennych i funkcji z wcześniejszych poleceń, a pozostawione polecenie nie
  przeszkadza zamknięciu programu.
  Przełącznik **Tryb zaufany (FullLanguage)** po potwierdzeniu, które mówi, co daje (dowolny kod .NET, zapis do katalogu
  z pominięciem planu zmian – bez podglądu, dziennika i cofania), włącza pełny język tylko w tym oknie, na stałej uwadze i
  w tytule okna; nie jest pamiętany. Nie dodaje poleceń – tylko kod .NET, a ten sięga do całego procesu AD Managera, także do
  sesji okna i jej danych logowania. **Wyłączenie** trybu uruchamia w oknie nową sesję PowerShell w ConstrainedLanguage (wiersz
  stanu mówi o tym, a polecenia, Tab i przełącznik czekają na nią ułamek sekundy): zmienne, funkcje, aliasy i bloki skryptu
  z trybu zaufanego znikają razem ze starą sesją, więc nic z nich nie działa dalej w pełnym języku, choć okno mówi
  ConstrainedLanguage. Historia i zawartość okna zostają; mówi o tym okno, a z góry już pytanie przy włączaniu. Gdy nowej sesji
  nie da się uruchomić, okno nie zostaje w trybie zaufanym, tylko mówi, że PowerShella nie można uruchomić. To, co kod .NET
  trybu zaufanego zmienił poza sesją – wątek albo licznik czasu, który uruchomił, składowa dodana przez `Add-Member` do typu .NET
  całego procesu (np. `[string]`) – zostaje do zamknięcia programu, także w innych oknach. Blok skryptu, który zostawiony przez
  taki kod licznik czasu wywoła po zamknięciu okna, kończy się błędem, ale nie zamyka już całego programu z pozostałymi oknami.
  Wyłączony przełącznik zawsze podaje powód: trwa polecenie, PowerShell się uruchamia, wyłącza tryb zaufany albo nie dał się
  uruchomić, sesja połączenia się skończyła albo system wymusza ConstrainedLanguage (WDAC, AppLocker) – wtedy okno w nim
  zostaje. Wydanie bez silnika PowerShella mówi, żeby użyć standardowego pakietu, a powód, dla którego PowerShella nie można
  uruchomić, jest w bieżącym języku interfejsu, także po jego zmianie. To ochrona przed pomyłką, nie granica bezpieczeństwa: granicą są uprawnienia konta w AD, a programy
  konsolowe (`whoami`, `nltest`) działają jak w każdej konsoli – jako osobny proces z tożsamością operatora; to, co wypisują,
  także na wyjście błędów, jest w oknie (wcześniej program na końcu polecenia pisał poza nie, do niewidocznej konsoli).
- **Zasady PowerShella tego komputera.** Przy otwarciu okno czyta zasady grupy PowerShella (transkrypcję, rejestrowanie bloków
  skryptów i modułów) oraz oba pliki `powershell.config.json` i, zanim uruchomisz pierwsze polecenie, mówi stałą uwagą, co jest
  gdzie zapisywane („transkrypcja jest włączona zasadą komputera: polecenia i wyniki zapisuje Windows w …”). Czyta je według
  reguł samego silnika: każdą zasadę osobno, najpierw z zasad komputera, potem użytkownika, a zasadę z zaznaczonym *Use Windows
  PowerShell Policy setting* – z klucza Windows PowerShell; pliki liczą się dopiero wtedy, gdy zasady grupy jej nie ustawiają.
  Gdy klucza albo pliku nie da się odczytać, uwaga mówi który i że nie wiadomo, czy polecenia są rejestrowane. Program niczego
  nie zmienia i niczego nie obchodzi; plik `powershell.config.json` obok `AdMng.exe` wyłącza tylko to, co włączył plik
  użytkownika (zasada komputera ma pierwszeństwo przed nim). Wyniki pokazują te same sekrety, które pokazuje Edytor atrybutów
  (np. hasło LAPS jawnym tekstem), więc przy włączonej transkrypcji trafią one do transkrypcji – uwaga o zasadach mówi to z
  góry.
- **`Out-AdmGrid`, `Open-AdmObject`, `Export-AdmList`.** `Out-AdmGrid` otwiera okno z siatką jak lista obiektów: kolumny z
  właściwości obiektów, sortowanie kliknięciem w nagłówek, filtr, kopiowanie (Ctrl+C), eksport CSV z ochroną formuł i otwieranie
  obiektu (Enter) – każde wywołanie to osobne okno. `Open-AdmObject` otwiera właściwości obiektu (nazwa wyróżniająca,
  `objectGUID`, SID, nazwa konta; najwyżej 10 okien w jednym poleceniu). `Export-AdmList` zapisuje CSV tak jak eksport listy (formuły
  chronione, separator z ustawień regionalnych, UTF-8 z BOM) tylko do pliku z `-Path` albo wybranego w oknie *Zapisz jako*;
  istniejący plik zastępuje tylko z `-Force`, a bez ścieżki i bez wybranego pliku nic nie zapisuje. Ścieżkę, `-Force` i
  separator sprawdza, zanim polecenie przed nim cokolwiek przeczyta. Kolumny z sekretami (hasło LAPS jawnym tekstem, hasło
  odzyskiwania BitLockera, atrybuty poufne) `Export-AdmList` i eksport siatki zapisują dopiero po tym samym pytaniu co eksport
  listy programu, a kopiowanie wierszy siatki je pomija. *Zatrzymaj* przy eksporcie siatki przerywa zapis i usuwa zapisaną część
  pliku, a wiersz stanu mówi to własnymi słowami.
- **Nic na dysku.** Wynik, historia i zawartość okna są tylko w pamięci i znikają z oknem. Silnik (PowerShell 7.6 wbudowany w
  program) ma wyłączone: telemetrię, kanał diagnostyczny, sprawdzanie aktualizacji, automatyczne ładowanie modułów i zapis
  pamięci podręcznej analizy modułów; test „bez plików” sprawdza, że praca w oknie nie zostawia pliku w żadnym folderze, w
  którym silnik trzyma pliki (na Windows także w folderach PowerShella w profilu użytkownika, porównanych przed i po). Na Windows silnik sam z siebie zapisuje w `%TEMP%` dwa pliki testowe
  `__PSScriptPolicyTest_…` (sprawdzenie AppLockera, bez danych z katalogu) i od razu je usuwa – raz na uruchomienie programu;
  uprzedź o tym zespół bezpieczeństwa. W trybie ograniczonym nie jest znany żaden sposób, w jaki skrypt dosięgnąłby sesji okna
  albo jej danych logowania: żadna zmienna, parametr ani `$Host` jej nie przekazuje (polecenia znajdują swoje okno w tablicy
  silnika), polecenia czytają przez widok tylko do odczytu tej sesji, a każdy sposób znaleziony przez audyt i późniejsze próby
  obejścia okna jest testem, który musi pozostać odrzucony. Plik zapisują tylko
  `Export-AdmList` (lista z ochroną formuł) i `Start-Transcript -Path` (zapis sesji do pliku, który wskażesz); `Start-Transcript`
  bez ścieżki – który zapisałby polecenia i ich wyniki do pliku w folderze *Dokumenty* – oraz `Out-File`, `Set-Content`,
  `Add-Content`, `Export-Csv`, `Export-Clixml` i przekierowanie `>` do pliku są odrzucane z komunikatem (`> $null` działa).
- **Rozmiar.** Silnik zwiększa `AdMng.exe` (jeden plik, bez kompresji) o około 38 MB – z 143,5 MB w {{PREVIOUS}} do 181,9 MB
  (173,5 MiB) – a folder publikacji o około 40 MB (z 164,7 do 204,3 MB); spakowane archiwum rośnie o około 14 MB (z 68,8 do
  82,4 MB w artefakcie CI). Silnik działa w samym `AdMng.exe`; obok niego leżą tylko `powershell.config.json` i plik pomocy.
  Każda poprawka bezpieczeństwa PowerShella oznacza nową wersję AD Managera. Serwis AdMng Watchdog nie zawiera silnika.

## Inne zmiany

- **Zatrzymanie wykonania kończy obiekt, który trwa.** *Zatrzymaj* w wierszu stanu (albo *Anuluj* na przycisku *Wykonaj*)
  w oknach, które wykonują zmiany na wielu obiektach – reset hasła, edytor zbiorczy, kreator zakładania kont, przywracanie
  z Kosza AD oraz podgląd planu zasad grupy i jego cofnięcie – nie zaczyna już żadnego nowego obiektu, ale obiekt, którego
  zmiany już ruszyły, dostaje wszystkie swoje zmiany. Konto nie zostaje więc z nowym hasłem i bez pozostałych ustawień,
  a konto z kreatora – wyłączone, bez hasła i bez grup. Wiersz stanu mówi, ile obiektów jest gotowych i że ten, który
  trwał, został dokończony, np. *Wykonywanie „Resetuj hasło” w domenie contoso.test zatrzymano: wykonano 1 z 2 obiektów,
  niepowodzenia: 0, obiekt, który był w toku, został dokończony.* Zatrzymanie nie przerywa też ponawiania żądania, które
  kontroler odrzucił jako chwilowo zajęty (kilka sekund). Inaczej jest przy awarii: żądania, na które kontroler nie
  odpowiedział (przekroczony czas, zerwane połączenie), po zatrzymaniu program nie wysyła ponownie – czeka tylko na
  żądanie będące w drodze (najwyżej limit czasu żądania, domyślnie 60 s) i kończy obiekt tym błędem. Zatrzymanie, które
  przyszło, gdy trwały już ostatnie obiekty planu, niczego nie zostawia niewykonanego – wiersz stanu podaje wtedy zwykły
  wynik (*Wykonano…*), a nie „zatrzymano: wykonano 1 z 1”. Plan cofnięcia i dziennik sesji obejmują każdą wykonaną
  zmianę.

## AdMng Watchdog {{VERSION}}

- Bez zmian w działaniu usługi i bez migracji bazy.

## Aktualizacja z {{PREVIOUS}}

- **AD Manager:** rozpakuj archiwum w miejsce poprzedniego; ustawienia w `%APPDATA%\AdMng` zostają bez zmian.
- **AdMng Watchdog:** aktualizacja nie jest potrzebna (bez zmian w działaniu); pakiet {{VERSION}} można zainstalować
  `apt install ./admng-watchdog_{{VERSION}}-1_amd64.deb`.
- **Powrót do {{PREVIOUS}}:** bez ograniczeń (starsza wersja pomija nowe ustawienia).

## Znane ograniczenia

<!-- TODO: ograniczenia nowych funkcji i to, czego nie sprawdzono na prawdziwej domenie -->
- Wszystkie ograniczenia z wersji {{PREVIOUS}} obowiązują nadal, z jednym wyjątkiem: zatrzymanie wykonania nie zostawia już
  obiektu w połowie (zob. „Inne zmiany”).
- Zatrzymanie kończy obiekt, który trwa, ale nie całą akcję: plan złożony z kilku obiektów wykonywanych po kolei (np. nowy GPO –
  kontener z plikami, kontenery Machine i User, dowiązanie) zatrzymany między dwoma z nich zostawia GPO, które trzeba cofnąć
  planem cofnięcia (tak było dotąd); dotyczy to też podglądu planu zmian GPO zamkniętego przyciskiem *Zatrzymaj i zamknij* – jego
  *Cofnij* znika z oknem (ile zrobiono, mówi po jego zamknięciu okno główne: powiadomieniem, a przy niepowodzeniach oknem
  z przyczynami). Wiersz listy operacji w tle mówi o zatrzymanym wykonaniu tylko „Anulowano” – zdanie z liczbą obiektów jest
  w wierszu stanu okna, a gdy okno zamknięto, w powiadomieniu okna głównego albo – przy niepowodzeniach – w jego oknie
  z przyczynami.
- Wykonanie pozostawione w tle traci okno: jego wyniki, *Ponów nieudane* i *Cofnij* znikają z oknem (zmiany są w dzienniku sesji).
  W kreatorze okno główne mówi wtedy tylko o kontach tworzonych po pierwszym (pierwsze jest osobnym wierszem listy operacji
  w tle).
  Zapisu właściwości obiektu i utworzenia obiektu nie da się zostawić w tle: to jeden obiekt, którego zapisu nie można zatrzymać,
  a to, co następuje po nim (ponowny odczyt, zachowane wartości przy odmowie kontrolera, zaznaczenie nowego obiektu), istnieje
  tylko w oknie. *Zatrzymaj i zamknij* w oknie głównym czeka bez limitu czasu; wylogowanie systemu nie zadaje pytań, a program
  zamyka sesje w ciągu 10 s. Pytanie z trzema odpowiedziami i to, co mówi potem okno główne, sprawdzono testami automatycznymi (model na
  Linuksie, okna na Windows); nikt nie oglądał ich jeszcze na ekranie. To, że okno z niepowodzeniami otwiera się dopiero po
  zamknięciu okna, którego dotyczy, sprawdzają testy modelu i jeden test interfejsu na Windows (prawdziwe okno Kosza AD
  i prawdziwe okno dialogowe); na ekranie nikt tego nie oglądał.
- Odczytywanie zmienionego podsumowania i opóźnienie stanu *Trwa* sprawdzono testami automatycznymi i na podstawie kodu;
  nie sprawdzono ich z prawdziwym czytnikiem ekranu (Narrator, NVDA) ani na ekranie.
- Plakietki stanu, puste listy i szare wiersze zastępcze sprawdzono testami automatycznymi (właściwości, zasoby, stan
  modeli widoku), ale nikt ich jeszcze nie oglądał na ekranie: wygląd w jasnym, ciemnym i wysokim kontraście oraz szerokość
  plakietek w wąskich oknach czeka na pilotaż. Plakietki stanu kanałów i kontrolerów domeny na karcie *Stan* Watchdoga nie
  zostały wprowadzone (te kolumny są wąskie) – zostają przy ikonie z podpowiedzią.
- Postęp na pasku zadań i pytanie o zamknięcie okna sprawdzono testami automatycznymi (model na Linuksie, okna na Windows),
  ale nikt nie oglądał przycisku na prawdziwym pasku zadań ani w sesji zdalnej. Zapis na stronie, której nie widać (np. wysyłka
  na zakładce *Korespondencji*, gdy pokazana jest inna), nie jest na pasku postępu – nie ma go też w wierszu stanu – za to pytanie
  o zamknięcie okna go uwzględnia. Zamknięcie okna przez zamknięcie okna, do którego należy, albo przez zamknięcie programu nie
  zadaje pytania samo: pyta okno główne.
- Okno PowerShell (R1) sprawdzono testami automatycznymi: silnik, polecenia i model okna na Linuksie i Windows oraz prawdziwe
  okno z prawdziwym silnikiem nad sztuczną domeną na Windows; nikt nie oglądał go jeszcze na ekranie – także tego, że zalane
  wynikami okno przyjmuje kliknięcie **Stop** (sprawdza to test automatyczny, który zalewa okno i naciska Ctrl+C). To, że wyjście programów
  konsolowych (`whoami`, `nltest`) trafia do okna, sprawdzono na Linuksie i testem z `cmd.exe` na Windows, ale nie w uruchomionym
  `AdMng.exe`. Wygląd kolorowanego tekstu
  (tekst w polu jest przezroczysty, kolory rysuje warstwa pod nim), listy uzupełnień pod kursorem, odnośników do nazw i okna siatki
  w jasnym, ciemnym i wysokim kontraście, a także to, czy czytnik ekranu czyta okno sensownie (lista uzupełnień nie jest
  ogłaszana), czeka na pilotaż. Zachowanie silnika pod WDAC/AppLocker i przy włączonych zasadach transkrypcji nie zostało
  sprawdzone na prawdziwej maszynie z takimi zasadami, a polecenia nie były uruchamiane na prawdziwym Windows Server AD (tylko
  nad sztucznym katalogiem i kontrolerem Samba). Nie ma jeszcze poleceń zapisu ani modułów RSAT (etapy R2 i R3), uzupełniania
  nazw plików ani `N obiektów` w wierszu stanu (liczy wiersze danych wyjściowych); `LockedOut` nie uwzględnia czasu blokady z
  zasad domeny, gdy kontroler nie zwraca własnej flagi. W trybie ograniczonym `[pscustomobject]@{}` i wywołania metod typów spoza
  rdzenia są odrzucane (`New-Object PSObject -Property` działa); skrypt w trybie zaufanym, który używa `System.Data.SqlClient`, nie
  znajdzie `sni.dll` (pakiet bez licencji OSI jest wyłączony).

## Co dalej

<!-- TODO: następne kroki z ROADMAP.md (albo usuń ten rozdział) -->

## English summary

<!-- TODO: one paragraph in English: "AD Manager {{VERSION}} (preview) adds ...", links to docs/en with the tag v{{VERSION}}.
     One sentence of it is written already (the close question, D-A); fold it into that paragraph. -->
Closing a window while it executes changes asks whether to stop and close, close and let the execution continue in the
background, or keep the window open; once the window has closed, the main window says how the execution ended.
