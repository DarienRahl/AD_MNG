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
- **Przycisk okna na pasku zadań pokazuje postęp, a zamknięcie okna w trakcie zapisu najpierw pyta.** Każde okno AD Managera
  pokazuje na swoim przycisku na pasku zadań to, co mówi jego wiersz stanu: ile już zrobiono (zielony pasek – np. przy
  wykonywaniu zmian na wielu obiektach albo przy wysyłce, która trwa minuty), że operacja trwa, a nie wiadomo, ile jeszcze
  (pasek w ruchu), albo – gdy operacja, której postęp było widać, skończyła się stanem, który trwa (np. nie udało się odczytać
  listy) – żółty lub czerwony pasek, dopóki strona nie powie czegoś innego. Pasek pojawia się po tym samym czasie co stan
  *Trwa* (około 0,15 s), działa także przy wyłączonych animacjach, a okno główne pokazuje postęp karty, która jest na wierzchu.
  Zamknięcie okna w trakcie operacji, która coś zapisuje – wykonania zmian, wysyłki wiadomości, przywracania, aktualizacji
  magazynu centralnego, zapisu konfiguracji Watchdoga, polecenia dla komputera – zadaje jedno pytanie: *Zatrzymać trwającą
  operację i zamknąć okno?*, z tym, co trwa, i z tym, co się stanie (przy wykonaniu zmian: nic nowego nie jest zaczynane, a obiekt
  w toku zostaje dokończony). *Tak* zatrzymuje operację i zamyka okno dopiero po jej końcu; *Nie* zostawia okno. Operacja, której
  nie da się zatrzymać (np. zapis właściwości obiektu, utworzenie obiektu, zapis konfiguracji Watchdoga), nie jest pytaniem: okno
  mówi, że jeszcze pracuje, i zostaje otwarte do jej końca. Okno, które tylko czyta, zamyka się bez pytania, a odczyt jest przerywany,
  jak dotąd. To samo pytanie zadaje okno główne o zapisy w swoich kartach i w oknach, które do niego należą, i karta Watchdoga przy
  zamknięciu. Wcześniej pytało tylko okno *Korespondencja* i tylko przy wysyłce, a edytor zbiorczy zamknięty w trakcie wykonania
  zostawiał je w tle.

## Inne zmiany

- **Zatrzymanie wykonania kończy obiekt, który trwa.** *Zatrzymaj* w wierszu stanu (albo *Anuluj* na przycisku *Wykonaj*)
  w oknach, które wykonują zmiany na wielu obiektach – reset hasła, edytor zbiorczy, kreator zakładania kont, przywracanie
  z Kosza AD oraz podgląd planu zasad grupy i jego cofnięcie – nie zaczyna już żadnego nowego obiektu, ale obiekt, którego
  zmiany już ruszyły, dostaje wszystkie swoje zmiany. Konto nie zostaje więc z nowym hasłem i bez pozostałych ustawień,
  a konto z kreatora – wyłączone, bez hasła i bez grup. Wiersz stanu mówi, ile obiektów jest gotowych i że ten, który
  trwał, został dokończony, np. *Wykonywanie „Resetuj hasło” w domenie contoso.test zatrzymano: wykonano 1 z 2 obiektów,
  niepowodzenia: 0, obiekt, który był w toku, został dokończony.* Zatrzymanie nie przerywa też ponawiania żądania, które
  kontroler odrzucił jako chwilowo zajęty – przy awarii kontrolera czeka więc na te próby (kilka sekund) i dopiero wtedy
  kończy obiekt błędem. Plan cofnięcia i dziennik sesji obejmują każdą wykonaną zmianę.

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
  planem cofnięcia (tak było dotąd). Wiersz listy operacji w tle mówi o zatrzymanym wykonaniu tylko „Anulowano” – zdanie
  z liczbą obiektów jest w wierszu stanu okna.
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

## Co dalej

<!-- TODO: następne kroki z ROADMAP.md (albo usuń ten rozdział) -->

## English summary

<!-- TODO: one paragraph in English: "AD Manager {{VERSION}} (preview) adds ...", links to docs/en with the tag v{{VERSION}} -->
