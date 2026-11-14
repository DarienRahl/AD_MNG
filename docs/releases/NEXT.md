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

## Nowe: odnowa interfejsu – etap V1

- **Kolorowe ikony obiektów.** Rodzaj obiektu widać teraz po kształcie i kolorze, zanim przeczyta się nazwę:
  użytkownik, komputer, serwer, kontroler domeny, grupa, kontakt, jednostka organizacyjna, zasada grupy, certyfikat
  i pozostałe rodzaje mają własne rysunki, a konto wyłączone i zablokowane – własny wariant ze znaczkiem (strzałka
  w dół, kłódka). Ikony są w drzewie, na listach, na kartach, w nagłówku panelu szczegółów, w oknach właściwości
  i wyboru obiektów oraz w oknie połączenia; w wierszu wyszarzonym (np. wyłączone konto na liście obiektów) ikona
  jest przygaszona razem z tekstem. **Gdzie:** *Ustawienia → Ogólne → Wygląd → Ikony*: *Kolorowe*
  (domyślnie) albo *Jednobarwne* (dotychczasowy wygląd); po zapisaniu zmiana działa od razu we wszystkich otwartych
  oknach.
  W trybie dużego kontrastu systemu Windows program zawsze używa ikon jednobarwnych, żeby obowiązywały kolory
  systemowe.
- **Panele okna głównego.** Drzewo, dokumenty i panel szczegółów to teraz trzy zaokrąglone panele na tle okna,
  a pasek narzędzi z paskiem adresu i szybkim wyszukiwaniem – jedna karta. Odstępy między panelami nadal służą do
  zmiany ich szerokości (myszą albo z klawiatury). Dokumenty są tak samo szerokie jak dotąd; drzewo jest domyślnie
  o 36 px węższe.
- **Tło okna.** *Ustawienia → Ogólne → Wygląd → Tło*: *Zgodnie z motywem* (domyślnie – w systemie Windows 11
  materiał Mica) albo *Jednolite*, dla sesji pulpitu zdalnego i wolniejszych komputerów. Zmiana działa po ponownym
  uruchomieniu programu. Karta *Ogólne* w ustawieniach przewija się teraz tak jak pozostałe karty.
- **Animacje.** Interfejs porusza się tam, gdzie ruch coś mówi: przyciski z samą ikoną (paski narzędzi, przyciski
  w wierszach, zamykanie kart, kopiowanie wartości) podświetlają się płynnie pod wskaźnikiem myszy; dokument
  otwierany albo wybrany kartą wsuwa się na miejsce od dołu; zawartość panelu szczegółów pojawia się płynnie, gdy
  panel zostaje otwarty; powiadomienia w rogu okna wsuwają się z prawej strony i wysuwają po zamknięciu. Każdy
  taki ruch trwa najwyżej ćwierć sekundy i niczego nie opóźnia – program od pierwszej chwili reaguje na klawiaturę
  i mysz; odświeżenie listy, stronicowanie i zmiana zaznaczenia niczego nie animują. **Gdzie:** *Ustawienia →
  Ogólne → Wygląd → Animacje*: *Zgodnie z systemem Windows* (domyślnie: animacje działają, gdy w systemie włączone
  są „Efekty animacji”, a obraz jest rysowany z akceleracją sprzętową – bez niej, np. w części sesji pulpitu
  zdalnego i maszyn wirtualnych, są wyłączone), *Włączone* albo *Wyłączone*; zmiana działa od razu po zapisaniu.
  Wskaźniki postępu działają zawsze. Wyłączony przycisk z ikoną nie ma już bladej ramki – zostaje sama przygaszona
  ikona.

## Nowe: odnowa interfejsu – etap V2

- **Wiersz stanu listy obiektów.** Lista obiektów mówi teraz w jednym stałym miejscu – w wierszu pod nagłówkiem
  z nazwą kontenera, nad wierszami listy – co robi i co się stało. W spoczynku jest tam podsumowanie („Obiektów: 428 ·
  0,17 s · stan na 14:35:02”, a po zaznaczeniu wierszy także „Zaznaczono: 3”). W czasie wczytywania: **Trwa** –
  „Wczytywanie „Kraków”: na razie 1 500 obiektów…”, z kółkiem postępu i poleceniem **Zatrzymaj**. Po zatrzymaniu:
  **Uwaga** – ile obiektów wczytano i że lista jest niepełna, z poleceniem **Odśwież**. Gdy listy nie udało się
  wczytać: **Błąd** – co się nie udało, dlaczego i co zrobić, z kodem wyniku na końcu i poleceniem **Ponów**; ten
  komunikat zostaje, dopóki lista nie zostanie wczytana. Podsumowanie i trwająca operacja zajmują zawsze jeden
  wiersz, więc wiersze listy nie przesuwają się już, gdy wczytywanie się zaczyna i kończy – dotąd nad listą pojawiał
  się na ten czas pasek postępu z przyciskiem, a błąd był w ramce na liście. Wynik (**Gotowe**, **Uwaga**, **Błąd**),
  który nie mieści się w jednym wierszu, zawija się – najwyżej do trzech wierszy – żeby przyczynę i radę dało się
  przeczytać bez wskazywania myszą; dłuższy tekst jest ucinany wielokropkiem, a w całości pokazuje go dymek. Wiersz
  jest wtedy odpowiednio wyższy i wraca do jednego wiersza po zamknięciu komunikatu. Rodzaj komunikatu widać po ikonie i po słowie na początku, nie tylko
  po kolorze; czytnik ekranu sam odczytuje nowy komunikat. Komunikat, który można zamknąć, ma przycisk zamknięcia –
  po zamknięciu wraca podsumowanie.
- **Pasek stanu okna głównego** nie powtarza już podsumowania listy i liczby zaznaczonych obiektów – dla karty
  z listą obiektów są one w wierszu stanu listy, a dla kart zasad grupy – w wierszu stanu karty. Połączenie, kontroler
  domeny, zabezpieczenia połączenia, działania, wersja i znaczek listy wirtualnej zostają na swoim miejscu; dla
  kart dzienników zdarzeń, gotowości środowiska, certyfikatów i Watchdog jest to wiersz stanu karty.
- **Okno główne: kółko postępu zamiast pasków.** Szybkie wyszukiwanie pokazuje na czas szukania kółko w miejscu lupy
  (dotąd: cienki pasek pod polem), a przycisk działań w pasku stanu – kółko obok liczby trwających operacji (dotąd:
  pasek). Pasek postępu każdej operacji na liście operacji w tle został bez zmian. „Konto chronione (adminCount=1)”
  w panelu szczegółów jest uwagą ze znakiem ostrzeżenia zamiast ramki.
- **Okno *Znajdź* (Ctrl+F): jeden przycisk do szukania i przerywania.** Przycisk **Znajdź teraz** na czas
  wyszukiwania zamienia się w **Anuluj** z kółkiem postępu – wyszukiwanie przerywa się tam, gdzie się je zaczęło; po
  naciśnięciu pokazuje „Zatrzymywanie…”, dopóki wyszukiwanie się nie skończy. Osobnego przycisku *Zatrzymaj* już nie
  ma, a przycisk nie zmienia szerokości między stanami. Enter w polu nazwy nadal zaczyna wyszukiwanie; naciśnięty
  jeszcze raz w trakcie niczego nie przerywa. U góry okna jest wiersz stanu: przed pierwszym wyszukiwaniem
  podpowiedź, potem przebieg wyszukiwania z poleceniem **Zatrzymaj**, podsumowanie wyników, informacja o zatrzymaniu
  (z **Odśwież**) albo błąd (z **Ponów**), a także to, że sesja okna się zakończyła – dotąd: ramka u góry okna
  i tekst w stopce.
- **Okno *Właściwości* obiektu: jeden wiersz stanu zamiast ramek i pasków.** Między nagłówkiem a kartami jest
  wiersz, w którym okno mówi, co robi i co się stało. W spoczynku: skąd i kiedy odczytano obiekt („Odczytano
  z dc01.contoso.test · stan na 14:35:02”), a na kartach z listą – jej podsumowanie („Grupy: 12”, „Członkowie: 40”,
  „Opublikowane certyfikaty: 2”). W czasie odczytu: **Trwa** – „Wczytywanie „Jan Kowalski”…” z poleceniem
  **Zatrzymaj**; po zatrzymaniu okno mówi, że może nie pokazywać wszystkiego, i podaje **Odśwież**. Po **Zastosuj**:
  „Zapisywanie zmian obiektu…”, a potem **Gotowe** – „Zmiany obiektu „Jan Kowalski” zapisano w domenie contoso.test.
  Raport jest na liście operacji w tle.”; gdy niczego nie zapisano – **Uwaga** z powodem; gdy zapis się nie udał –
  **Błąd** z przyczyną i z tym, co zrobić. Komunikaty, które dotąd były osobnymi ramkami u góry okna (nie udało się
  odczytać obiektu – z **Ponów**, obiekt usunięto, obiekt zmieniono w innym miejscu – z **Odśwież**, sesja okna się
  zakończyła), są teraz w tym wierszu i zostają w nim, dopóki trwa ich przyczyna; wynik zapisu można zamknąć i wtedy
  wraca komunikat, który nadal obowiązuje.
- **Karty okna *Właściwości* mówią w tym samym wierszu.** Karta, której nie udało się odczytać (*Członek grupy*,
  *Członkowie*, *Edytor atrybutów*, *Opublikowane certyfikaty*, *Poświadczenia kluczy*), mówi o tym, gdy jest
  wybrana – z poleceniem **Ponów** – a nie ramką na karcie; na innych kartach wiersz tego nie pokazuje. Lista
  *Wszystkie (zagnieżdżone)* ma jeden przycisk **Odśwież**, który na czas odczytu zamienia się w **Anuluj** (osobnego
  *Zatrzymaj* już nie ma), a okno pamięta, którą z dwóch list karty oglądano. *Sprawdź na wszystkich DC* na karcie
  *Konto* i na karcie *Stan* komputera oraz *Ustal* (lokację) można teraz zatrzymać w wierszu stanu; gdy nie
  wszystkie kontrolery domeny odpowiedziały, wiersz mówi które. Paski postępu pod nagłówkiem, w stopce i na kartach
  zniknęły, więc zawartość kart nie przesuwa się w czasie odczytu. Okno jest domyślnie o tyle wyższe, ile zajmuje
  wiersz stanu.
- **Uwagi przy treści zamiast ramek.** Ostrzeżenia karty *Mapowania nazw* (słabe mapowania, nieprawidłowe wartości)
  są teraz zwykłym tekstem ze znakiem ostrzeżenia nad listami – to uwagi o tym, co widać na karcie, a nie komunikat
  o działaniu okna. Tak samo jak dotąd wyglądają komunikaty przy polach (błędna wartość, uwaga planisty zmian).
- **Edytor zbiorczy i import z CSV: wiersz stanu pod paskiem kroków.** Okno mówi w jednym wierszu, co robi i jak się
  to skończyło. W pierwszym kroku: ile obiektów wybrano i jakich („3 obiekty · Użytkownik: 2, Grupa: 1”), a przy
  imporcie – który plik wczytano („konta.csv · Wiersze: 120, separator: ;”); wczytywanie obiektów i pliku można
  zatrzymać poleceniem **Zatrzymaj**, a gdy się nie uda, wiersz podaje przyczynę i polecenie **Ponów** (przy pliku:
  „Wybierz inny plik.”). Po *Podgląd*: **Trwa** – „Sprawdzanie zmian: 120 z 300…”, z kółkiem pokazującym wykonaną
  część; w kroku podglądu wiersz podaje jego podsumowanie („Obiekty: 300 · do zmiany: 280 · …”). Po *Wykonaj*:
  „Wykonywanie „Edycja zbiorcza (obiekty: 300)” w domenie contoso.test: 12 z 300…”, a na końcu **Gotowe** – „Wykonano
  „Edycja zbiorcza (obiekty: 300)” w domenie contoso.test: zmieniono 300 obiektów. Raport jest na liście operacji
  w tle.” Gdy części obiektów nie udało się zmienić albo wykonanie zatrzymano – **Uwaga**: ile obiektów zmieniono
  i ile było niepowodzeń (ich przyczyny są na liście wyników), z poleceniem **Ponów nieudane**; gdy niczego nie
  zmieniono – **Błąd**. Po zamknięciu komunikatu wiersz pokazuje liczby wyników („Powodzenie: 118, niepowodzenie: 2,
  pominięte: 5”). Dotąd: ramka błędu pod paskiem kroków, pasek planowania w stopce, paski nad listą obiektów, przy
  pliku CSV i w kroku wykonywania oraz osobne teksty podsumowań. Okno jest domyślnie o tyle wyższe, ile zajmuje
  wiersz stanu.
- **Edytor zbiorczy: sprawdzanie i wykonywanie przerywa się tym przyciskiem, którym się je zaczęło.** Przycisk
  *Podgląd* na czas sprawdzania zmian, a *Wykonaj* na czas wykonywania (zostaje wtedy w stopce) zamienia się
  w **Anuluj** z kółkiem postępu; po naciśnięciu pokazuje „Zatrzymywanie…”, dopóki operacja się nie skończy. Osobnych
  przycisków *Zatrzymaj* już nie ma. Zatrzymane sprawdzanie niczego nie zmienia – wiersz stanu mówi „Sprawdzanie
  zmian zatrzymano: niczego nie zmieniono.” i podaje **Ponów**; zatrzymać można teraz także sprawdzanie zaczęte przez
  *Ponów nieudane*. Wykonanie przerwane w oknie albo na liście operacji w tle kończy krok, który już trwa, a wiersz
  mówi, ile obiektów zmieniono i że pozostałych nie wykonano.
- **Uwagi w edytorze zbiorczym zamiast ramek.** Reguła listy operacji („Usuwania nie można łączyć z innymi
  operacjami.”), ostrzeżenie przy operacji usuwania i informacja nad podglądem, że pokazano plan cofnięcia, są teraz
  zwykłym tekstem ze znakiem ostrzeżenia w swoim miejscu.
- **Podgląd planu zmian (zasady grupy): wiersz stanu pod tytułem.** W spoczynku podsumowanie planu („Obiektów: 3,
  kroków: 9, kontroler: dc01.contoso.test.”), w czasie wykonywania – postęp z poleceniem **Zatrzymaj**, potem wynik
  z nazwą domeny; gdy coś wykonano, ale nie wszystko się udało albo wykonanie zatrzymano, wiersz podaje polecenie
  **Cofnij**. *Wykonaj* na czas wykonywania zamienia się w **Anuluj** – osobnego przycisku już nie ma. Dotąd: pasek
  i podsumowanie w stopce, ramka błędu i karta z wynikiem nad zakładkami. „Pokazano plan cofnięcia. Wykonaj go, aby
  odwrócić zmianę.” jest uwagą nad zakładkami, dopóki plan cofnięcia czeka na wykonanie.
- **Kosz AD: wiersz stanu u góry okna.** Okno mówi w jednym wierszu, co robi i jak się to skończyło. Gdy widać listę,
  jest tam jej podsumowanie („Obiektów: 12 · 0,21 s · stan na 14:35:02”, po zaznaczeniu wierszy także „Zaznaczono: 3”),
  a w czasie odczytu: **Trwa** – „Wczytywanie usuniętych obiektów domeny contoso.test: na razie 1 500 obiektów…”
  z poleceniem **Zatrzymaj**. Po zatrzymaniu: **Uwaga** – ile obiektów wczytano, z poleceniem **Odśwież**. Gdy listy
  nie udało się wczytać (na przykład konto nie ma prawa do odczytu usuniętych obiektów): **Błąd** z przyczyną
  i poleceniem **Ponów** – zostaje, dopóki lista nie zostanie wczytana. Po *Przywróć*: „Sprawdzanie obiektów do
  przywrócenia (3)…”, w podglądzie – jego podsumowanie („Obiekty do przywrócenia: 3 z 4.”), a po potwierdzeniu:
  „Wykonywanie „Przywracanie z Kosza AD (3)” w domenie contoso.test: 2 z 3…” i wynik z nazwą domeny. Gdy części
  obiektów nie udało się przywrócić, wiersz podaje liczby, przyczyny (każdą raz) i to, że nieprzywrócone obiekty
  pozostały na liście; które obiekty i dlaczego – widać w raporcie na liście operacji w tle (dotąd: osobne okno
  komunikatu). Dotąd: ramka błędu i pasek postępu nad listą oraz pasek i teksty w stopce. Okno jest domyślnie o tyle
  wyższe, ile zajmuje wiersz stanu.
- **Kosz AD: odświeżanie i przywracanie przerywa się tym przyciskiem, którym się je zaczęło.** *Odśwież* – teraz
  z napisem, nie sama ikona – na czas wczytywania listy zamienia się w **Anuluj**, a przycisk *Przywróć (3)*
  w podglądzie – na czas przywracania; osobnego przycisku *Zatrzymaj* już nie ma. Sprawdzanie obiektów przed
  podglądem zatrzymuje się poleceniem **Zatrzymaj** w wierszu stanu: niczego wtedy nie przywrócono, a **Ponów**
  sprawdza to samo jeszcze raz. Informacja o tym, co można przywrócić w tym lesie (Kosz AD włączony albo tylko
  obiekty tombstone), jest uwagą nad filtrami – ze znakiem informacji albo ostrzeżenia – zamiast karty z tłem.
- **Przegląd kont: wiersz stanu u góry okna.** Sprawdzanie widać w wierszu niezależnie od tego, która karta jest
  otwarta: **Trwa** – „Sprawdzanie kont w zakresie „contoso.test”: odczytano: 1 200, z uwagami: 56…”, a dla karty
  *Uprawnienia* – „Sprawdzanie uprawnień kont w zakresie „…”: odczytano: 300, z uprawnieniami ponad użytkownika
  domeny: 17…”; zakres jest nazwany tak, jak go wybrano (domena, jednostka organizacyjna, grupa albo zapisany widok).
  W spoczynku wiersz pokazuje podsumowanie oglądanej karty („Stan na godz. 12:00 · odczytano kont: 1234 · kont
  z uwagami: 56”, z liczbą zaznaczonych kont). Zatrzymane sprawdzanie: **Uwaga** – wyniki są niepełne, z poleceniem
  **Sprawdź**; nieudane: **Błąd** z przyczyną i poleceniem **Ponów** – na tej karcie, której dotyczyło, dopóki nie
  zostanie sprawdzona ponownie. Dotąd: dwie ramki błędu, ramka o zakończonej sesji, pasek w stopce, paski nad listami
  i teksty w stopce. Okno jest domyślnie o tyle wyższe, ile zajmuje wiersz stanu.
- **Przegląd kont: *Sprawdź* i *Dokładne ostatnie logowanie (wszystkie DC)* przerywa się tym samym przyciskiem.**
  Każdy z nich na czas działania zamienia się w **Anuluj**; osobnego przycisku *Zatrzymaj* już nie ma. Wynik odczytu
  dokładnego ostatniego logowania jest w wierszu stanu, a nie w powiadomieniu: „Dokładne ostatnie logowanie odczytano
  na kontrolerach domeny: 3 z 3. Konta, które jednak są aktywne: 2.”; gdy któryś kontroler nie odpowiedział –
  **Uwaga** z jego nazwą („Brak odpowiedzi: dc02.contoso.test.”). To, czego nie udało się odczytać ze źródeł
  uprawnień (SYSVOL, pliki zasad grupy), jest uwagą nad listą karty *Uprawnienia* zamiast ramki.
- **Porównanie obiektów między połączeniami: wiersz stanu u góry okna.** **Trwa** – „Porównywanie obiektu „Łukasz
  Wiśniewski” z połączeniem fabrikam.test · FABRIKAM\anna.zielinska…”, potem „Odczytywanie członkostwa
  zagnieżdżonego…” (a po zaznaczeniu *Pokaż członków zagnieżdżonych* – „Odczytywanie członków zagnieżdżonych…”),
  każde z poleceniem **Zatrzymaj**. W spoczynku: z którym obiektem porównano („Znaleziono: CN=…”). Gdy karty są
  puste, wiersz mówi dlaczego i zostaje tak do następnego porównania: „Znaleziono 2 obiekty – wybierz jeden.”
  (kandydaci są na liście pod polem połączenia), „Nie znaleziono (sprawdzone reguły: …).” albo **Błąd** – „Nie udało
  się porównać obiektu „…”: …” z poleceniem **Ponów**. Zatrzymane porównanie: **Uwaga** – karty mogą nie pokazywać
  wszystkiego, z poleceniem **Porównaj**. Dotąd: ramki błędów u góry okna, pasek i tekst w stopce oraz pasek na
  karcie *Członkostwo*. Okno jest domyślnie o tyle wyższe, ile zajmuje wiersz stanu.
- **Porównanie obiektów: *Porównaj* przerywa porównanie.** Przycisk na czas porównywania zamienia się w **Anuluj** –
  także wtedy, gdy porównanie zaczęło *Odśwież*, wybór kandydata albo samo okno po otwarciu (dotąd *Przerwij* działał
  tylko po *Porównaj*); osobnego przycisku *Przerwij* już nie ma. Uwagi zostały przy tym, czego dotyczą, jako tekst
  ze znakiem ostrzeżenia zamiast ramek: brak innego otwartego połączenia – pod polem połączenia, zamknięte albo
  zmienione połączenie drugiej strony – na jej karcie, „To ten sam obiekt widziany przez dwa konta…” – nad kartami.
  O zakończeniu sesji okna mówi wiersz stanu (dotąd: ramka u góry okna i druga na karcie strony A).
- **Okno połączenia: wiersz stanu pod nagłówkiem.** Okno *Połącz z usługą Active Directory* mówi w jednym wierszu, co
  robi i co się nie udało. **Trwa** – „Łączenie z contoso.test…” (z nazwą kontrolera domeny, gdy go wskazano) albo
  „Wyszukiwanie kontrolerów domeny contoso.test…”, z poleceniem **Zatrzymaj**. Gdy brakuje danych: **Uwaga** – „Nie
  połączono: Wprowadź hasło.”. Gdy połączenie się nie udało: **Błąd** – „Nie udało się połączyć z contoso.test: …”
  z przyczyną, radą, co sprawdzić, i kodem wyniku na końcu. Po zatrzymaniu: „Łączenie z contoso.test zatrzymano: nie
  otwarto sesji. Wybierz „Połącz”, aby spróbować ponownie.” – z poleceniem **Połącz**. Po wyszukaniu kontrolerów wiersz
  podaje ich liczbę („Znalezione kontrolery domeny: 2”), a gdy wyszukiwanie się nie udało – przyczynę i polecenie
  **Ponów**. W wierszu jest nazwa domeny albo kontrolera domeny, nigdy nazwa konta ani hasło. Wybranie innego profilu
  zamyka komunikat o poprzedniej próbie. Dotąd: ramka błędu pod polami oraz pasek postępu i tekst w stopce. Długi
  komunikat zawija się i okno jest wtedy o tyle wyższe.
- **Okno połączenia: *Połącz* i *Znajdź kontrolery domeny* przerywa się tym samym przyciskiem.** Każdy z nich na czas
  swojego działania zamienia się w **Anuluj** z kółkiem postępu, a po naciśnięciu pokazuje „Zatrzymywanie…”, dopóki
  operacja się nie skończy. Osobnych przycisków *Anuluj* w stopce już nie ma. Enter nadal zaczyna łączenie; naciśnięty
  jeszcze raz w trakcie niczego nie przerywa.
- **Okno *Zmień kontroler domeny*: wiersz stanu pod nagłówkiem.** W spoczynku: ile kontrolerów domeny jest na liście („3
  kontrolery domeny”). Wczytywanie listy widać w wierszu – „Wczytywanie kontrolerów domeny contoso.test…” – i można je
  zatrzymać poleceniem **Zatrzymaj** (dotąd nie dało się go przerwać inaczej niż zamknięciem okna); po zatrzymaniu
  wiersz mówi, że lista może nie pokazywać wszystkich, i podaje **Odśwież**. Gdy listy nie udało się wczytać: **Uwaga**
  – „Nie udało się wczytać listy kontrolerów domeny: …” – teraz z przyczyną, kodem wyniku i poleceniem **Ponów**; nazwę
  kontrolera nadal można wpisać. Zmiana kontrolera: „Łączenie z dc02.contoso.test…”, a gdy się nie uda – **Błąd**: „Nie
  udało się przenieść sesji na dc02.contoso.test: …” z przyczyną, radą i zdaniem „Sesja pozostaje połączona
  z dc01.contoso.test.”. Przycisk *Zmień* na czas łączenia zamienia się w **Anuluj**; po zatrzymaniu sesja zostaje na
  dotychczasowym kontrolerze, a wiersz podaje polecenie **Zmień**. *Anuluj* okna działa jak dotąd: zamyka okno,
  a trwającą zmianę najpierw przerywa. Dotąd: ramka błędu i ramka ostrzeżenia nad listą, pasek obok *Odśwież* oraz pasek
  i tekst w stopce. Okno jest domyślnie o tyle wyższe, ile zajmuje wiersz stanu.
- **Okno *Resetuj hasło*: wiersz stanu u góry okna.** Po **OK**: „Sprawdzanie kont (2)…”, potem „Wykonywanie „Resetuj
  hasło” w domenie contoso.test: 1 z 2…”, a na końcu wynik z nazwą domeny – **Gotowe** („Wykonano „Resetuj hasło”
  w domenie contoso.test: zmieniono 2 obiekty. Raport jest na liście operacji w tle.”), **Uwaga**, gdy części haseł nie
  udało się zresetować albo wykonanie zatrzymano, **Błąd**, gdy nie zresetowano żadnego. Po zamknięciu komunikatu wiersz
  pokazuje liczby wyników („Powodzenie: 2, niepowodzenie: 0, pominięte: 0”); lista wyników ma nagłówek *Wyniki*. Gdy
  niczego nie zaczęto: **Uwaga** – „Nie zresetowano żadnego hasła: Hasła nie są zgodne.”; gdy żadnego z kont nie można
  zresetować, wiersz podaje powody. Wczytywanie nazw kont i stanu blokady („Wczytywanie kont (2)…”) można zatrzymać; gdy
  się nie uda albo zostanie zatrzymane, wiersz podaje polecenie **Ponów**, a lista pokazuje konta nazwami z ich nazw
  wyróżniających. Hasło – ani wpisane, ani wygenerowane – nigdy nie trafia do wiersza: wygenerowane jest tam, gdzie
  dotąd, w wierszu wyniku. Dotąd: ramka błędu na dole okna, pasek w stopce i tekst z liczbami nad listą wyników.
- **Okno *Resetuj hasło*: OK przerywa resetowanie.** Przycisk na czas sprawdzania kont i resetowania zamienia się
  w **Anuluj** z kółkiem postępu (dotąd resetowanie można było przerwać tylko na liście operacji w tle). Zatrzymanie
  w czasie sprawdzania niczego nie zmienia – „Sprawdzanie kont zatrzymano: nie zresetowano żadnego hasła.” – i wiersz
  podaje **Ponów**. Zatrzymanie w czasie resetowania kończy krok, który już trwa, a pozostałych kont nie rusza – wiersz
  mówi, ile obiektów zmieniono i że reszty nie wykonano. Konto, przy którym zatrzymano, może mieć już nowe hasło bez
  pozostałych ustawień (na przykład bez wymuszenia zmiany hasła przy następnym logowaniu) i jest na liście wyników jako
  niepowodzenie. *Anuluj* okna zamyka je jak dotąd.
- **Okno *Nowy obiekt*: wiersz stanu u góry okna.** Po **OK**: **Trwa** – „Tworzenie obiektu „Projekty” w domenie
  contoso.test…” (tworzenia nie przerywa się w oknie – można je, jak dotąd, anulować na liście operacji w tle). Gdy
  niczego nie zaczęto: **Uwaga** – „Niczego nie utworzono: Wprowadź nazwę.” (także: hasła nie są zgodne, nazwa albo
  nazwa logowania jest zajęta, uwagi planisty zmian). Gdy tworzenie się nie udało: **Błąd** – „Nie udało się utworzyć
  obiektu „Projekty”: …” z przyczyną i kodem wyniku. Po utworzeniu obiektu okno zamyka się jak dotąd, a o wyniku mówi
  powiadomienie w oknie głównym. Okno otwarte bez połączenia z domeną mówi o tym od razu, a nie dopiero po **OK**. Uwaga
  „Hasło nigdy nie wygasa, więc użytkownik nie będzie musiał go zmienić przy następnym logowaniu.” jest zwykłym tekstem
  ze znakiem ostrzeżenia pod opcjami hasła zamiast ramki. Dotąd: ramka błędu pod polami i pasek w stopce.
- **Okno *Wyślij wiadomość do użytkowników*: wiersz stanu u góry okna.** Okno mówi w jednym wierszu, co robi z
  wiadomościami i jak się to skończyło. Po *Przygotuj podgląd*: **Trwa** – „Wczytywanie kont (4) i przygotowywanie
  wiadomości…” z poleceniem **Zatrzymaj**; w podglądzie wiersz pokazuje jego podsumowanie („Gotowe: 3 · Z ostrzeżeniami:
  1 · Błędy: 0 · Pominięte: 2 · Wykluczone: 0”), które zmienia się razem z zaznaczeniami na liście. Gdy kont nie udało
  się wczytać: **Błąd** – „Nie udało się wczytać kont: …” z przyczyną, kodem wyniku i poleceniem **Ponów**; gdy szablon
  nie pozwala przygotować wiadomości (na przykład brakuje wartości pola): **Uwaga** – „Nie przygotowano żadnej
  wiadomości: …” z powodami. W czasie wysyłki: „Wysyłanie wiadomości: 12 z 300…” z kółkiem pokazującym wykonaną część, a
  gdy coś się nie udało – także „nieudane: 2”. Na końcu: **Gotowe** – „Serwer przyjął 300 wiadomości dla użytkowników
  domeny contoso.test. Raport jest na liście poniżej.”; gdy serwer przyjął tylko część albo wysyłkę zatrzymano –
  **Uwaga** z liczbami („przyjęte przez serwer: 298, nieudane: 2, niewysłane: 0”), z informacją, że przyczyny są w
  raporcie, i z poleceniem **Ponów nieudane**; gdy nie przyjął żadnej – **Błąd**. Po zamknięciu komunikatu wiersz
  pokazuje liczby raportu („Przyjęte przez serwer: 298 · Nieudane: 2 · Niewysłane: 0 · Poza tą wysyłką: 1”). Wiadomość
  testowa – „Wysyłanie wiadomości testowej do …” i jej wynik – też jest w tym wierszu i też ma **Zatrzymaj** (dotąd nie
  dało się jej przerwać). Gdy niczego nie zaczęto: **Uwaga** – „Nie wysłano żadnej wiadomości: Wpisz hasło SMTP…”. W
  wierszu jest nazwa domeny, a przy wiadomości testowej – adres operatora z ustawień; nigdy adres odbiorcy, treść
  wiadomości ani hasło SMTP. Dotąd: ramka błędu i ramka o zakończonej sesji pod nagłówkiem, pasek i tekst w stopce,
  pasek postępu wysyłki, podsumowania przy filtrze i nad raportem oraz tekst wyniku wiadomości testowej pod podglądem.
  Okno jest domyślnie o tyle wyższe, ile zajmuje wiersz stanu.
- **Wiadomości do użytkowników: podgląd, wysyłkę i wklejanie przerywa się tym przyciskiem, którym się je zaczęło.**
  *Przygotuj podgląd* na czas wczytywania kont, *Wyślij (300)* na czas wysyłki (zostaje wtedy w stopce) i *Wklej
  sformatowaną treść* na czas wklejania zamienia się w **Anuluj** z kółkiem postępu; po naciśnięciu pokazuje
  „Zatrzymywanie…”, dopóki operacja się nie skończy. Osobnych przycisków *Zatrzymaj* i *Przerwij wklejanie* już nie ma.
  Zatrzymane przygotowywanie niczego nie zmienia – „Przygotowywanie wiadomości zatrzymano: nie przygotowano żadnej
  wiadomości.”, z poleceniem **Ponów**; zatrzymana wysyłka kończy wiadomość, która jest w drodze, a wiersz mówi, ile
  wiadomości serwer przyjął i ilu nie wysłano; zatrzymane wklejanie zostawia treść bez zmian. Krok wysyłki nie ma już
  własnego paska postępu – postęp jest w wierszu stanu – i przypomina, że zamknięcie okna przerywa wysyłkę (okno, jak
  dotąd, pyta o to przy zamykaniu). To, co edytor zrobił z wklejaną treścią („Wklejono: obrazy: 2, uwagi poniżej: 3.”,
  „Niczego nie wklejono: Schowek nie zawiera obrazu.”), jest w wierszu stanu, dopóki widać edytor.
- **Wiadomości do użytkowników: uwagi przy treści zamiast ramek.** Uwagi sprawdzania szablonu (błąd składni, pole,
  którego nie wolno wysyłać) są zwykłym tekstem ze znakiem błędu pod edytorem. To, że wiadomości nie mieszczą się w
  wybranym sposobie wysyłki (na przykład AdMng Watchdog nie przyjmuje obrazów), jest uwagą nad stopką – z tymi samymi
  przyciskami co dotąd (*Wyślij bezpośrednio przez SMTP*, *Wyślij bez obrazów*, *Anuluj*). Obrazy szablonu, których nie
  udało się odczytać, są uwagą przy pasku obrazów, z przyciskiem *Usuń brakujące obrazy*; w czasie wczytywania obrazów
  szablonu przy napisie *Obrazy w treści* obraca się kółko. Podgląd sformatowany, który pokazuje źródło HTML (brak
  WebView2 Runtime albo podgląd wyłączony w ustawieniach), mówi o tym uwagą zamiast ramki – także w oknie
  *Korespondencja*.
- **Ustawienia: wiersz stanu nad kartami.** Testy z karty *Wiadomości e-mail* odpowiadają teraz w wierszu u góry okna –
  widocznym niezależnie od tego, jak daleko przewinięto kartę: **Trwa** – „Testowanie połączenia z
  smtp.contoso.com:587…”, potem **Gotowe** – „Połączenie z smtp.contoso.com:587 działa: szyfrowane (Tls13),
  uwierzytelnianie: LOGIN.” albo **Błąd** – „Połączenie z smtp.contoso.com:587 nie działa: Etap „TLS” nie powiódł się:
  …”. Tak samo *Wyślij wiadomość testową do mnie* i *Pobierz konta z Outlooka* („Profil Outlooka „Outlook” ma 2 konta.
  Wybierz z listy konto, z którego będą wysyłane wiadomości.”). Każdą z tych trzech operacji można teraz zatrzymać
  poleceniem **Zatrzymaj** w wierszu (dotąd trzeba było czekać na odpowiedź serwera albo Outlooka); działają pojedynczo
  – w czasie testu nie pobiera się kont i odwrotnie. Gdy brakuje hasła: **Uwaga** – „Nie przetestowano połączenia: Wpisz
  hasło SMTP…”; hasło ani nazwa użytkownika SMTP nigdy nie trafiają do wiersza. Pod przyciskami testów zostaje to, co
  serwer pokazał w ostatnim teście: oferowane mechanizmy uwierzytelniania i odcisk certyfikatu, który przypina *Przypnij
  certyfikat z testu*. Na pozostałych kartach wiersz jest pusty. Powód, dla którego nie można zapisać ustawień (pierwsza
  nieprawidłowa wartość), jest uwagą ze znakiem błędu nad przyciskami, a ostrzeżenie o wysyłce bez TLS – uwagą przy tym
  wyborze, z polem potwierdzenia pod nią; dotąd były to ramki. Karty przewijają się odpowiednio wcześniej, więc okno z
  długą kartą ma tę samą wysokość co dotąd.
- **Okno *Godziny logowania*: uwagi zamiast ramek.** Trzy ostrzeżenia okna – dlaczego godzin nie można zmienić, że
  zapisanej wartości nie da się pokazać na siatce, i „Żadna godzina nie jest dozwolona – użytkownik nie będzie mógł się
  zalogować.” – są zwykłym tekstem ze znakiem ostrzeżenia w tych samych miejscach, nad siatką i pod nią. Okno niczego
  samo nie odczytuje ani nie zapisuje, więc nie ma wiersza stanu.
- **Kreator filtrów i edytor zapisanego widoku: wiersz stanu u góry okna.** Przycisk *Testuj* liczy pasujące obiekty
  na kontrolerze domeny, a odpowiedź jest teraz w wierszu stanu. **Trwa** – „Liczenie pasujących obiektów w domenie
  contoso.test…” (w edytorze widoku wiersz podaje bazę wyszukiwania: „…w „contoso.test/Firma/IT”…”), potem **Gotowe** –
  „Pasujące obiekty w domenie contoso.test: 42 (0,21 s).”. Gdy test się nie uda: **Błąd** – „Nie udało się przetestować
  filtru w domenie contoso.test: …” z przyczyną, kodem wyniku i poleceniem **Ponów**; bez połączenia z domeną:
  **Uwaga** – „Filtru nie przetestowano: Brak połączenia z domeną.” (filtr i widok nadal można edytować i zapisać).
  *Testuj* na czas liczenia zamienia się w **Anuluj** z kółkiem postępu – osobnego przycisku *Zatrzymaj* już nie ma; po
  zatrzymaniu wiersz mówi „Liczenie pasujących obiektów zatrzymano: filtru nie przetestowano.” i podaje polecenie
  **Testuj**. Zmiana filtru (w edytorze widoku także bazy wyszukiwania albo zakresu) zamyka wynik testu i kończy test,
  który jeszcze trwa – liczba dotyczyłaby już innego filtru. To, co jest nie tak z samym filtrem (warunek bez atrybutu,
  niepoprawny filtr LDAP), jest zwykłym tekstem ze znakiem błędu pod filtrem zamiast ramki. Dotąd: ramka błędu pod
  filtrem oraz przycisk *Zatrzymaj* i tekst wyniku obok przycisku *Testuj*. Kreator filtrów jest domyślnie o tyle
  wyższy, ile zajmuje wiersz stanu.
- **Dziennik sesji: wiersz stanu u góry okna.** Liczba wpisów („Wpisy: 12 z 40”) jest teraz w wierszu stanu nad
  filtrem, a nie w stopce. Gdy sesja, w której otwarto okno, się zakończy, mówi o tym ten sam wiersz (dotąd: ramka u
  góry okna); wpisy zostają – można je filtrować, kopiować i eksportować – ale z tego okna nie da się już otworzyć
  właściwości obiektu. Eksport, jak dotąd, potwierdza powiadomienie. Okno jest domyślnie o tyle wyższe, ile zajmuje
  wiersz stanu.
- **Okno *Wybieranie kontenera*: wiersz stanu u góry okna.** Wczytywanie drzewa po otwarciu okna widać w wierszu –
  „Wczytywanie kontenerów domeny contoso.test…” – zamiast cienkiego paska nad drzewem; z drzewa można w tym czasie
  korzystać, a *Anuluj* zamyka okno jak dotąd. Gdy zawartości kontenera nie udało się wczytać (na przykład z braku
  uprawnień), wiersz mówi o tym, dopóki ten kontener jest zaznaczony: **Błąd** – „Nie udało się wczytać kontenerów w
  „Pracownicy”: …” z przyczyną, kodem wyniku i poleceniem **Ponów**. Dotąd mówił o tym tylko znak błędu przy
  kontenerze w drzewie, z przyczyną w podpowiedzi; znak zostaje, a sam kontener nadal można wybrać. Okno otwarte bez
  połączenia z domeną albo po zakończeniu sesji mówi o tym w wierszu. Okno jest domyślnie o tyle wyższe, ile zajmuje
  wiersz stanu.
- **Okno wybierania obiektów: wiersz stanu u góry okna.** To, co okno *Wybieranie: Użytkownicy, Grupy…* mówiło pod
  polem nazw, jest teraz w wierszu stanu. *Znajdź teraz*: **Trwa** – „Wyszukiwanie w „contoso.test/Firma”…” z
  poleceniem **Zatrzymaj** (dotąd wyszukiwania nie dało się przerwać), a potem liczba obiektów na liście („Znaleziono:
  12 obiektów”); gdy pasuje więcej, niż mieści lista: **Uwaga** – „Pasuje ponad 500 obiektów: lista pokazuje
  pierwsze 500. Wpisz nazwę albo wybierz inną lokalizację, aby zawęzić wyszukiwanie.”. *Sprawdź nazwy* (i **OK**
  z nazwami w polu): „Sprawdzanie nazw (3)…”, też z poleceniem **Zatrzymaj**; gdy nie wszystkie nazwy udało się
  rozpoznać: **Uwaga** – „Nie wszystkie nazwy rozpoznano: Nazwie „GG_” odpowiada kilka obiektów - wybierz jeden
  z listy. Nie znaleziono obiektu dla „nikt”.” (wiersz wymienia najwyżej trzy nazwy; wszystkie nierozpoznane zostają
  w polu). Gdy rozpoznano wszystkie, wiersz nic nie mówi – obiekty pojawiają się wśród wybranych. Zatrzymane albo
  nieudane sprawdzanie niczego nie dodaje (dotąd nazwy rozpoznane przed błędem były już dodane), a zatrzymane albo
  nieudane wyszukiwanie zostawia listę bez zmian; wiersz podaje wtedy polecenie **Znajdź teraz**, **Sprawdź nazwy**
  albo – po błędzie, z przyczyną i kodem wyniku – **Ponów**. Po zakończeniu sesji okna wiersz mówi o tym, a wyszukiwanie
  i sprawdzanie nazw są niedostępne. Dotąd: pasek postępu i tekst pod polem nazw. Okno jest domyślnie o tyle wyższe,
  ile zajmuje wiersz stanu.
- **Karty zasad grupy: wiersz stanu pod nagłówkiem karty.** Karty *Obiekty zasad grupy*, *Zasady grupy* kontenera
  (domeny, jednostki organizacyjnej, lokacji), karta jednego GPO i *Filtry WMI* mówią teraz w jednym wierszu – pod
  nagłówkiem albo paskiem poleceń karty, nad jej zawartością – co robią i co się stało. W spoczynku jest tam
  podsumowanie, które dotąd było w pasku stanu okna: „Obiekty zasad grupy: 7, z różnymi wersjami w AD i SYSVOL: 1”, a
  po zaznaczeniu wierszy także „Zaznaczono: 2”; „Połączone GPO: 2, stosowane: 5”; „Połączenia: 1, ustawienia: 14”;
  „Filtry WMI: 1”. Odczyt karty: **Trwa** – „Wczytywanie „Obiekty zasad grupy”…” z poleceniem **Zatrzymaj** (dotąd
  odczytu nie dało się przerwać, a widać go było tylko po znaczku na karcie i słowie „Ładowanie…” w pasku stanu). Po
  zatrzymaniu: **Uwaga** – karta może nie pokazywać wszystkiego, z poleceniem **Odśwież**. Gdy odczyt się nie udał:
  **Błąd** – „Nie udało się wczytać „Obiekty zasad grupy”: …” z przyczyną, kodem wyniku i poleceniem **Ponów**. Te
  komunikaty, a także „Brak połączenia z domeną.”, „Kontener już nie istnieje.” i „Obiekt GPO już nie istnieje.”,
  zostają w wierszu, dopóki trwa ich przyczyna; dotąd były ramką nad zawartością karty. Zawartość każdej karty zasad
  grupy zaczyna się o wysokość wiersza stanu niżej.
- **Operacje na zasadach grupy mówią w wierszu karty, co się z nimi stało.** Po wybraniu polecenia, które kończy się
  podglądem planu zmian (nowe GPO, kopia, zmiana nazwy, usunięcie, połączenia i ich opcje, blokada dziedziczenia, stan
  GPO, filtrowanie zabezpieczeń, delegowanie, wyrównanie wersji, naprawa uprawnień folderu, filtry WMI i ich wybór),
  karta pokazuje **Trwa** – „Zmiana nazwy obiektu GPO „Stacje robocze – Zasady haseł”: przygotowywanie zmian…” – z
  poleceniem **Zatrzymaj**, dopóki plan powstaje; dotąd nic nie było wtedy widać i nie dało się tego przerwać. Gdy z
  operacji nic nie wyszło, wiersz mówi dlaczego: „… – nic nie zmieniono: SYSVOL niedostępny na dc01.contoso.test: …”,
  „… – nic nie zmieniono: …” z przyczyną i kodem wyniku albo „… – nic nie zmieniono: Przygotowywanie zmian
  zatrzymano.” – i podaje polecenie **Ponów**, które przygotowuje tę samą zmianę jeszcze raz, z wpisaną już nazwą albo
  wybranym kontem. Dotąd był to osobny komunikat błędu. „…: brak zmian – obiekty mają już żądane wartości.” też jest
  teraz w wierszu karty, a nie w powiadomieniu. Podgląd planu zmian, potwierdzenia i wykonanie działają jak dotąd; po
  wykonaniu karta wczytuje się ponownie. Polecenia wybrane z drzewa albo z menu okna – bez karty, która mogłaby
  odpowiedzieć – nadal odpowiadają komunikatem.
- **Karta GPO: zakładka *Stan* mówi w wierszu stanu karty.** Sprawdzanie GPO widać w wierszu – „Sprawdzanie obiektu
  GPO…”, potem jego etapy („Odczyt folderów SYSVOL: 1 z 1”) – z poleceniem **Zatrzymaj**; po sprawdzeniu wiersz
  podaje, jak i kiedy GPO sprawdzono: „Ostatnie sprawdzenie: pełne, 12.11.2026 14:32 – 2 uwagi”. Na pozostałych
  zakładkach wiersz pokazuje podsumowanie GPO. Zatrzymane sprawdzanie: **Uwaga** – „Sprawdzanie obiektu GPO
  zatrzymano: wyników nie zaktualizowano.” z poleceniem **Sprawdź ponownie**; nieudane: **Błąd** – „Nie udało się
  sprawdzić obiektu GPO: …” z przyczyną i poleceniem **Ponów**, widoczny, gdy wybrana jest zakładka *Stan*. Pasek
  postępu i tekst pod opisem zakładki zniknęły. To, czego sprawdzenie nie mogło ocenić (SYSVOL niedostępny,
  nieodczytany folder GPO), jest uwagą ze znakiem ostrzeżenia nad wynikami, a nie ramką. Gdy GPO usunięto z AD, a jego
  folder w SYSVOL pozostał, wiersz mówi o tym i podaje polecenie **Usuń pozostały folder** – dotąd był to przycisk w
  osobnej ramce.
- **Uwagi o SYSVOL i o emulatorze PDC zamiast ramek.** „SYSVOL niedostępny na dc01.contoso.test: … – zmiany zapisujące
  SYSVOL są wyłączone.” oraz „Zmiany GPO wykonujesz na dc01.contoso.test, a emulatorem PDC jest dc02.contoso.test –
  GPMC domyślnie używa emulatora PDC.” stoją teraz pod wierszem stanu każdej karty zasad grupy jako zwykły tekst ze
  znakiem ostrzeżenia albo informacji; *Zmień kontroler…* jest odnośnikiem zaraz za zdaniem. To uwagi o tym, co karta
  pokazuje i gdzie zapisuje, a nie komunikaty o jej działaniu – widać je razem z podsumowaniem w wierszu stanu, dopóki
  karta jest otwarta.
- **Wyszukiwarka ustawień GPO: odczyt, wynik i stan w wierszu stanu karty.** Wiersz stanu mówi, ile ustawień znaleziono
  i kiedy odczytano obiekty GPO – „Znalezione ustawienia: 1 (wystąpienia: 2) · Przeszukiwane obiekty GPO: 7 · odczyt
  14.11.2026 10:44” – a po zaznaczeniu wiersza także, które to ustawienie. Odczyt pokazuje swoje etapy z postępem
  („Odczyt folderów GPO: 12 z 300”) i poleceniem **Zatrzymaj**; dotychczasowy pasek postępu, osobny przycisk *Anuluj*,
  tekst wyniku nad listą i ramka *Zmieniono zasady grupy w tym oknie* zniknęły. Przyciski **Odśwież** i **Odczytaj
  wszystko ponownie** są teraz przyciskami z anulowaniem: ten z nich, który rozpoczął odczyt, zamienia się w
  **Anuluj**, a potem w „Zatrzymywanie…”. Zatrzymany odczyt zostawia poprzednie wyniki i mówi (**Uwaga**), że karta
  może nie pokazywać wszystkiego, z poleceniem **Odśwież**, które odczytuje jeszcze raz to, co przerwano; odczyt, który
  się nie udał, pokazuje **Błąd** z przyczyną, kodem wyniku i poleceniem **Ponów**. Zmiana zasad grupy w tym oknie
  zostawia wyniki i mówi w wierszu: „Zmieniono zasady grupy w tym oknie – wyniki mogą być nieaktualne.” z poleceniem
  **Odśwież**. Czego indeks nie odczytał (foldery GPO, SYSVOL, szablony, ukryte wartości) i że pokazano tylko część grup,
  jest zwykłą uwagą ze znakiem ostrzeżenia nad wynikami, a nie ramką ani kartą z ikoną informacji.
- **Porządki w GPO: to samo w wierszu stanu.** Wiersz mówi, ile jest propozycji i kiedy odczytano obiekty GPO („16
  propozycji · Przeanalizowano obiektów GPO: 16, ustawienia odczytane 14.11.2026 10:44”), pokazuje odczyt i analizę z
  etapami oraz poleceniem **Zatrzymaj**, a zmianę zasad grupy po analizie, zatrzymany albo nieudany odczyt – jako stan
  karty z poleceniem **Odśwież**, **Ponów**. **Odśwież** (wyróżniony) i **Odczytaj wszystko ponownie** są przyciskami z
  anulowaniem. Czego analiza nie widziała, jest uwagą nad listą. Gdy akcja propozycji (usunięcie GPO, scalenie) nie może
  ruszyć, bo okno nie ma połączenia albo nie da się odczytać obiektu GPO do porównania, wiersz karty mówi dlaczego
  („Usunięcie obiektu GPO „…” – nic nie zmieniono: …”) – dotąd było to okno z błędem; pytanie, czy obiekt GPO zmienił się
  od analizy, i lista ustawień, które niczego by nie zmieniły, zostają oknami.
- **Diagnostyka zasad grupy: to samo w wierszu stanu.** Wiersz mówi, ile jest uwag oraz kiedy i jak głęboko sprawdzono
  obiekty GPO („12 uwag · Ostatnie sprawdzenie: pełne, 14.11.2026 10:44”), pokazuje sprawdzanie z etapami („Odczyt
  folderów SYSVOL: 3 z 8”) i poleceniem **Zatrzymaj**, a zmianę zasad grupy od ostatniego sprawdzenia, zatrzymane i
  nieudane sprawdzanie – jako stan karty z poleceniami **Sprawdź ponownie**, **Odśwież** i **Ponów** (zatrzymane albo
  nieudane sprawdzanie powtarza się z tą samą głębokością). **Szybkie sprawdzenie** (wyróżnione) i **Pełne sprawdzenie**
  są przyciskami z anulowaniem. Czego sprawdzenie nie obejmuje, jest uwagą nad listą. **Napraw…** pokazuje w wierszu karty
  „…: przygotowywanie zmian…” z poleceniem **Zatrzymaj**, a gdy planu nie da się przygotować – dlaczego, z poleceniem
  **Ponów**.
- **Co dostanie komputer: wiersz stanu zamiast pogrubionego podsumowania.** Wiersz mówi, co dostaną komputer i
  użytkownik („Komputer: 3 GPO, 12 ustawień; Użytkownik: 1 GPO, 2 ustawienia”), pokazuje obliczanie modelu z poleceniem
  **Zatrzymaj** (rusza je **Przelicz** i odświeżanie na pasku) oraz – jako stan karty – przerwane obliczanie z poleceniem
  **Odśwież** i nieudane z przyczyną i poleceniem **Ponów**. Czego model nie mógł użyć (konto lub kontener, który nie
  istnieje, SYSVOL niedostępny), jest uwagą ze znakiem ostrzeżenia nad wynikami. „Dane wejściowe zmienione – kliknij
  »Przelicz«” zostaje zdaniem przy polach.
- **Szablony administracyjne: wiersz stanu zamiast ramek.** Wiersz mówi, ile jest ustawień i ile skonfigurowano („12
  ustawień, 3 skonfigurowane”), a po zmianach także ile ich zmieniono i nie zapisano („… · Zmienione ustawienia: 1
  (niezapisane)”). Pokazuje odczyt szablonów i plików `Registry.pol` z poleceniem **Zatrzymaj** (z etapem „Wczytywanie
  plików ADMX i ADML…”), przygotowanie planu zapisu („Szablony administracyjne GPO „…”: przygotowywanie zmian…”) i – gdy
  planu nie da się przygotować – dlaczego, z poleceniem **Ponów**; edycje zostają. Gdy `Registry.pol` zmieni się na
  kontrolerze od rozpoczęcia edycji, wiersz mówi o konflikcie jako stan karty (nie da się go zamknąć) i podaje polecenie
  **Odrzuć zmiany** – dotąd był to żółty pasek bez polecenia. Skąd są szablony (magazyn centralny, szablony tego
  komputera, brak szablonów) i który plik `Registry.pol` nie daje się odczytać, jest zwykłą uwagą pod wierszem stanu –
  z ikoną informacji dla magazynu centralnego, ze znakiem ostrzeżenia dla pozostałych – zamiast ramki z tłem; przyciski
  **Zaktualizuj szablony…** i **Wczytaj szablony ponownie** zostały po prawej stronie uwagi.
- **Ustawienia zabezpieczeń: wiersz stanu zamiast ramek.** Ten sam wiersz: ile ustawień zdefiniowano i ile zmieniono
  („Ustawienia zabezpieczeń: zdefiniowane 14 · Zmienione ustawienia: 1 (niezapisane)”), odczyt trzech plików z
  poleceniem **Zatrzymaj** (zatrzymany odczyt nie gubi edycji; **Odśwież** czyta jeszcze raz), przygotowanie planu zapisu
  i jego niepowodzenie z poleceniem **Ponów**, a zmianę pliku przez kogoś innego od rozpoczęcia edycji – jako stan karty
  z poleceniem **Odrzuć zmiany**. Plik, którego nie da się odczytać, jest uwagą pod wierszem stanu, a ostrzeżenie o
  inspekcji (kategorie podstawowe i podkategorie bez wymuszenia podkategorii) – uwagą nad listą na stronie
  *Inspekcja*.
- **Wybór GPO (*Połącz istniejący GPO…*, *Porównaj z…*): wiersz stanu u góry okna.** Wczytywanie GPO domeny widać w
  wierszu – **Trwa** „Wczytywanie obiektów zasad grupy domeny contoso.test…” z poleceniem **Zatrzymaj** – a gdy lista
  jest gotowa, wiersz mówi, ile jest GPO („Obiekty zasad grupy: 12”, po wpisaniu filtra „Pokazano obiekty zasad grupy: 3
  z 12”). Odczyt zatrzymany albo nieudany zostawia pustą listę, więc wiersz opisuje to, dopóki GPO nie odczytasz ponownie
  poleceniem **Ponów** („Wczytywanie obiektów zasad grupy zatrzymano: lista jest pusta…”, „Nie udało się wczytać …:
  przyczyna. (kod)”). Po zakończeniu sesji okna wiersz mówi o tym. Dotąd: czerwona ramka nad listą i pasek postępu na
  środku listy. Okno jest domyślnie o tyle wyższe, ile zajmuje wiersz stanu.
- **Porównanie dwóch GPO: wiersz stanu pod tytułem okna.** **Trwa** – „Porównywanie „Stacje robocze – Zasady haseł” z
  „Stacje robocze – Pulpit zdalny”…” z poleceniem **Zatrzymaj**; gdy wiersze już widać, a szablony administracyjne jeszcze
  nazywają zasady, kolejny etap „Wczytywanie szablonów administracyjnych, aby nazwać zasady…” (zatrzymany zostawia wiersze
  z nazwami z rejestru – to wynik, który można zamknąć). W spoczynku wiersz mówi, ile jest różnic („Różnice: 12 z 140
  pozycji”) – to zdanie było dotąd w stopce okna. Porównanie zatrzymane, zanim cokolwiek pokazało (albo gdy widać jeszcze
  wiersze poprzedniego – wiersz mówi wtedy, że mogą być nieaktualne), albo nieudane jest stanem okna, dopóki nie
  powtórzysz go poleceniem **Odśwież** albo **Ponów**. Co się nie dało porównać (SYSVOL
  niedostępny, GPO za duży – „Ustawień i plików nie porównano.”) jest uwagą nad wierszami, tak długo jak je widać. Nieudany
  eksport do CSV mówi w wierszu, dlaczego; udany nadal potwierdza powiadomienie. Dotąd: czerwona i żółta ramka oraz pasek
  postępu na środku listy.
- **Kopia zapasowa, przywracanie i import GPO: wiersz stanu u góry okna.** Okno mówi w jednym wierszu: czyta folder z
  kopiami („Wczytywanie kopii zapasowych z folderu…” – zatrzymanie oddaje okno od razu także wtedy, gdy udział sieciowy nie
  odpowiada), czyta wybraną kopię („Wczytywanie kopii zapasowej „…”…”), tworzy kopie („Tworzenie kopii zapasowych GPO: 1 z
  3…” z postępem) i przygotowuje plan przywrócenia albo importu aż do otwarcia podglądu („Przywrócenie „…” z kopii
  zapasowej: przygotowywanie zmian…”), zawsze z poleceniem **Zatrzymaj**. W spoczynku wiersz podaje, ile kopii jest w
  folderze („Kopie zapasowe w folderze: 3”). Kopia, która się udała, niczego nie dodaje – okno się zamyka, a wynik mówi
  powiadomienie jak dotąd; zatrzymana mówi, ile obiektów GPO zapisano, i podaje polecenie, które rozpoczyna kopię od nowa;
  nieudana mówi, ile kopii nie powstało i dlaczego („Nie udało się utworzyć kopii zapasowych 1 z 3 obiektów GPO: …”).
  Folder albo kopię, których nie da się odczytać, wiersz
  opisuje, dopóki nie odczytasz ich ponownie poleceniem **Ponów**. Zatrzymanie planu przywrócenia albo importu mówi, że
  nic nie zmieniono, i też podaje **Ponów**. To, że kopia pochodzi z innej domeny (przywrócić ją można tylko tam, można
  zaimportować jej ustawienia), jest uwagą pod listą, dopóki ta kopia jest zaznaczona – dotąd czerwoną ramką.
- **Kopia zapasowa, przywracanie i import: przycisk główny przerywa to, co rozpoczął.** **Utwórz kopię**, **Przywróć…** i
  **Importuj…** na czas kopii albo przygotowania planu zamieniają się w **Anuluj** (potem „Zatrzymywanie…”) – ten sam
  przycisk, który to rozpoczął, jak w pozostałych oknach. Przycisk **Zamknij** jest wtedy wyłączony, jak dotąd.
- **Aktualizacja szablonów ADMX: wiersz stanu u góry okna.** Wczytywanie paczki – pliku, folderu, szablonów tego komputera
  albo pobrania („Pobieranie szablonów Microsoft Edge…”) – jest operacją okna z poleceniem **Zatrzymaj**; porównanie z
  magazynem centralnym to jej ostatni etap („Porównywanie z magazynem centralnym…”), a gdy zmienisz język albo opcję, jest
  osobną operacją. W spoczynku wiersz mówi, co porównanie znalazło („Do zapisania: 2, bez zmian: 1, pominięte: 0”) – to
  zdanie było dotąd w stopce. Paczka, której nie dało się wczytać, jest wynikiem, który można zamknąć („Nie udało się
  pobrać szablonów Microsoft Edge: … Wybierz Ponów, aby spróbować ponownie.”; przy pliku lub folderze: „… Wybierz inną
  paczkę.”); porównanie zatrzymane albo nieudane nie ma plików do pokazania, więc jest stanem okna, dopóki nie wybierzesz
  **Ponów**. **Zaktualizuj…** jest teraz przyciskiem, który przerywa to, co rozpoczął: kopia zapasowa magazynu i plan
  (zatrzymane: „Aktualizację zatrzymano: magazynu centralnego nie zmieniono…”); na czas podglądu planu wiersz wraca do
  podsumowania, a po aktualizacji i nowym porównaniu mówi, co zapisano i gdzie jest kopia zapasowa („Magazyn centralny
  zaktualizowany: zapisano plików: 2. … Kopia zapasowa zapisana w …”). Że magazyn centralny jeszcze nie istnieje, jest uwagą
  nad plikami. Dotąd: czerwona ramka, karta z wynikiem, pasek postępu i dwa teksty w stopce. W dzienniku diagnostycznym
  zostają tylko klucze komunikatów, liczby i rodzaje błędów – nigdy adres pobierania ani nazwa pliku.
- **Okna zasad grupy, które tylko zbierają dane, mają uwagi zamiast ramek.** *Edytor filtru WMI* (zapytania zapisane w
  filtrze, których nie dało się odczytać), okno *Kopiuj ustawienia* i *Scal GPO* (zbyt wiele GPO źródłowych, ustawienia,
  które blokują scalenie, i różnice, które scalenie zmieni) oraz okno nazwy nowego, kopiowanego i zmienianego GPO nie
  uruchamiają żadnej operacji – oddają to, co zebrały, poleceniu, które je otworzyło, a ono przygotowuje plan, pokazuje
  podgląd i mówi w wierszu swojej karty, jak poszło. Nie mają więc wiersza stanu; to, co mówią, jest prawdą o polach dopóty,
  dopóki je widać, i stoi obok nich jako uwaga (znak błędu albo ostrzeżenia i zdanie, bez tła).
- **Kreator zakładania wielu kont: wiersz stanu okna zamiast ramek i pasków.** Kreator jest pierwszym krokiem okna
  edytora zbiorczego i mówi w jego jednym wierszu stanu – pod paskiem kroków – to, co mówi strona kroku, który widać.
  *Dane:* w spoczynku podsumowanie tabeli („Wiersze: 120, kolumny: 7 – nagłówki rozpoznane”; to zdanie stało dotąd nad
  tabelą); wklejanie i wczytywanie pliku jest operacją – **Trwa** „Odczytywanie pliku „dane.xlsx”…” z poleceniem
  **Zatrzymaj** (po zatrzymaniu tabela się nie zmienia, a wiersz proponuje ponowienie). Gdy z wklejenia albo pliku nic nie
  wynika, wiersz mówi, co pominięto i dlaczego („Nic nie wklejono: Schowek nie zawiera tabeli …”, „Nie wczytano pliku
  „dane.xlsx”: Plik jest większy niż 50 MB. Wybierz inny plik.”), a wcześniej wczytana tabela zostaje – dotąd była to
  czerwona ramka nad tabelą i pasek postępu obok przycisków. Ostrzeżenia odczytu arkusza i pytanie o powtórne wklejenie
  (z jego trzema przyciskami) zostały nad tabelą jako uwagi. *Nazwy i opcje:* wynik poleceń profilu – zapisano, usunięto,
  ustawiono jako domyślny, albo dlaczego nie („Nie zapisano profilu: Wpisz nazwę profilu.”) – i niepowodzenie odczytu
  konta, które może dołączać komputery, są w wierszu stanu, a nie pod przyciskami; powód, dla którego *Dalej* nie jest
  jeszcze możliwe, jest uwagą nad regułami (dotąd czerwoną ramką). *Sprawdzenie:* podsumowanie („Wiersze: 120 – OK: 108,
  z ostrzeżeniami: 6 …”) w wierszu; samo sprawdzenie to **Trwa** „Sprawdzanie wierszy: 40 z 120…” z postępem i poleceniem
  **Zatrzymaj**, a **Sprawdź ponownie** i **Numeruj od nowa** zamieniają się na czas sprawdzenia, które rozpoczęły, w
  **Anuluj** – osobnego przycisku *Zatrzymaj* i paska postępu nie ma. Sprawdzenie zatrzymane, nieudane albo nieaktualne
  („Dane, reguły lub utworzone konta zmieniły się od tego sprawdzenia: wiersze mogą być nieaktualne…”) jest stanem
  strony, dopóki nie sprawdzisz ponownie – z poleceniem **Sprawdź ponownie** albo **Ponów**. Problemy, które dotyczą
  wszystkich wierszy, i uwagi nad wynikami tworzenia kont (replikacja, konta zostawione wyłączone, zatrzymanie po
  pierwszym koncie) są uwagami zamiast żółtych ramek. Gdy sesja okna się zakończy, wiersz mówi to od razu – także w
  trakcie sprawdzania.
- **Przegląd komputerów: wiersz stanu u góry okna.** Okno mówi w jednym wierszu, co robi i jak się to skończyło. W
  spoczynku: podsumowanie ostatniego sprawdzenia i liczba zaznaczonych komputerów (to zdanie stało dotąd w stopce).
  Sprawdzanie to **Trwa** „Sprawdzanie komputerów w zakresie „contoso.test”: odczytano: 1 200, z ustaleniami: 56…” z
  poleceniem **Zatrzymaj**; zatrzymane („… wyniki są niepełne (odczytano: 400). Wybierz „Sprawdź”, aby sprawdzić ponownie.”)
  i nieudane sprawdzanie są stanem okna, dopóki nie sprawdzisz ponownie. Wynik **Dokładnego ostatniego logowania**, ustalenia
  lokacji i polecenia **Ping** – dotąd powiadomienie, które znikało po kilku sekundach, i okno z błędem – jest w wierszu:
  „Odpowiedziało kontrolerów domeny: 2 z 2; aktywnych okazało się komputerów: 1.”, a gdy któryś kontroler nie odpowiedział,
  zwraca uwagę i go wymienia. Zatrzymanie któregokolwiek z nich mówi, że listy nie zmieniono. Że sesja okna się zakończyła
  albo że okno otwarto bez połączenia, wiersz mówi od razu – także w trakcie sprawdzania. Uwaga, że katalog wydań systemu
  Windows jest stary, została nad parametrami jako uwaga (dotąd żółta ramka), bo jest prawdą o wynikach, dopóki je widać.
- **Przegląd komputerów: *Sprawdź*, *Dokładne ostatnie logowanie*, *Ustal lokację* i *Ping* przerywa się tym przyciskiem,
  którym się je zaczęło.** Każdy z nich na czas operacji zamienia się w **Anuluj** (potem „Zatrzymywanie…”); osobnego
  przycisku *Zatrzymaj* obok *Sprawdź* i paska postępu nad listą nie ma. Jedna operacja trwa naraz; nowe sprawdzanie nadal
  zastępuje poprzednie. Okno jest o 36 px wyższe.
- **Zdalne akcje na komputerze: wiersz stanu pod nagłówkiem.** W spoczynku wiersz mówi, jak okno łączy się z komputerem
  („Połączenie: WinRM (HTTP 5985, Kerberos) jako CONTOSO\admin”; to zdanie stało dotąd w stopce). Odczyt albo akcja to
  **Trwa** – „Odczytywanie usług komputera KRK-PC101…” – z poleceniem **Zatrzymaj**. Odczyt kończy się bez komunikatu
  (kartę stanu i listy widać pod spodem); akcja mówi, co zrobiła („KRK-PC101: zasady grupy odświeżone”, „KRK-PC101 uruchomi
  się ponownie za 15 min”). Odczyt, który się nie udał, jest wynikiem z poleceniem **Ponów**; akcja, która zmienia komputer
  (odświeżenie zasad, ponowne uruchomienie, jego anulowanie), nie ma odnośnika do powtórzenia. Zatrzymana akcja mówi
  uczciwie: „… zatrzymano: nie wiadomo, czy komputer otrzymał żądanie.” Że komputer nie ma nazwy DNS w Active Directory albo
  że zdalne akcje nie są dostępne w tej wersji, jest stanem okna – dotąd był to dziennik i okno błędu nad oknem. Dziennik
  akcji pod listami zostaje jak był: jest historią tego, co zrobiono w oknie. Sześć przycisków, które coś zaczynają
  (*Sprawdź stan*, *Pokaż zalogowanych*, *Pokaż usługi*, *Odśwież zasady grupy*, *Uruchom ponownie…*, *Anuluj zaplanowane
  ponowne uruchomienie*), przerywa to, co rozpoczęło. Okno jest o 36 px wyższe. Okno *Uruchom ponownie* ma zdanie o
  polach jako uwagę zamiast ręcznie narysowanej ramki.
- **Dzienniki zdarzeń: wiersz stanu pod nagłówkiem każdego dokumentu.** *Dziennik zdarzeń*, *Nieudane logowania* i
  *Blokady kont* mówią w jednym wierszu to samo tymi samymi słowami. W spoczynku: podsumowanie ostatniego przebiegu z
  zaznaczonym zdarzeniem albo ustaleniem („Zdarzeń: 7 z 2 źródeł”). Przebieg to **Trwa** z etapem i licznikami – „Odczytywanie
  dzienników zdarzeń – źródła: 2 z 3, zdarzenia: 1 204…”, „Sprawdzanie nieudanych logowań – analiza zdarzeń…”, „Badanie
  blokady konta „Jan Kowalski” – odczyt kontrolerów domeny…” – z poleceniem **Zatrzymaj**. Jeśli nie odczytano któregoś ze
  źródeł, wiersz zwraca na to uwagę i wymienia najwyżej trzy („… nie odczytano źródeł: 1 z 2 (dc02.contoso.test). Przyczyna
  każdego jest w sekcji „Zasięg i dostęp”.”); przyczyna zostaje w wierszu źródła w tej sekcji, a to, jak daleko wstecz
  sięga dziennik, w uwagach analiz. Zatrzymany odczyt zostawia to, co odczytano („… zatrzymano; odczytane i pokazane
  zdarzenia: 1 204. Wybierz „Odśwież”, aby odczytać ponownie.”), zatrzymana analiza – wyniki, które były; przebieg, który
  się nie udał, jest wynikiem z poleceniem **Ponów**. Wczytanie kanałów komputera mówi w wierszu, a podsumowanie ostatniego
  odczytu zostaje. Że dzienników zdarzeń nie da się czytać w tym oknie (nie Windows albo proste wiązanie), jest stanem
  dokumentu – dotąd żółta ramka u góry, a podsumowanie było puste. Że konto z blokady nie istnieje albo że zakres jest
  nieprawidłowy, jest stanem do następnego przebiegu. Osobny przycisk *Anuluj* i pasek postępu zniknęły: **Wyszukaj**,
  **Sprawdź** i **Wczytaj kanały** same są przyciskiem, który przerywa to, co rozpoczęły.
- **Gotowość środowiska: wiersz stanu pod nagłówkiem.** W spoczynku wiersz mówi, co znalazło ostatnie sprawdzenie, a potem
  kiedy i gdzie je wykonano („OK: 20 · Ostrzeżenia: 3 · Błędy: 1 · Niesprawdzone: 9 · Ostatnie sprawdzenie: pełne,
  3.11.2026 14:32, na dc01.contoso.test”) – dotąd były to dwa osobne teksty w karcie. Sprawdzenie to **Trwa** „Pełne
  sprawdzenie: 5 z 8 – Dzienniki zdarzeń (E)…” z postępem w kółku i poleceniem **Zatrzymaj**; *Szybkie sprawdzenie* i
  *Pełne sprawdzenie* zamieniają się na jego czas w **Anuluj** – osobnego przycisku i paska postępu nie ma. Sprawdzenie
  zatrzymane („… sprawdzenia, do których nie doszło, oznaczono jako niesprawdzone. Wybierz „Sprawdź ponownie”…”) i zmiana
  połączenia po pokazanym sprawdzeniu są stanami karty z poleceniem **Sprawdź ponownie**, dopóki go nie wykonasz; brak
  połączenia – błędem stanu. „Tylko do odczytu…” i uwaga o eksporcie są jedną uwagą nad obszarami.
- **Certyfikaty (AD CS): wiersz stanu pod nagłówkiem każdego z czterech dokumentów.** *Usługi kluczy publicznych*,
  *Szablony certyfikatów*, *Przegląd bezpieczeństwa AD CS* i *Gotowość na silne mapowanie i NTAuth* mówią w jednym
  wierszu to samo tymi samymi słowami. W spoczynku: podsumowanie – co odczytano, kiedy i skąd oraz zaznaczony wiersz
  („Urzędy przedsiębiorstwa: 2 · szablony: 26 · zaufane urzędy główne: 1 · certyfikaty NTAuth: 1 · odczytano z
  dc01.contoso.test, 13.10.2026 14:00 · Kontoso Wystawiający CA”); dotąd były to dwa osobne teksty nad kartami. Odczyt to
  **Trwa** z etapem – „Wczytywanie „Usługi kluczy publicznych” – obiekty AD CS: 1 204…”, w szablonach także „odczyt
  uprawnień szablonów…” i „liczenie kont: 500…”, w przeglądzie bezpieczeństwa „Sprawdzanie bezpieczeństwa AD CS – odczyt
  uprawnień obiektów PKI…” – z poleceniem **Zatrzymaj**; przycisk *Odśwież*, *Odczytaj ponownie* albo *Sprawdź ponownie*
  zamienia się na ten czas w **Anuluj**. Osobnego przycisku *Anuluj* przeglądu bezpieczeństwa i pasków postępu już nie
  ma. Odczyt zatrzymany („… zatrzymano: karta może nie pokazywać wszystkiego. Wybierz „Odśwież”…”) i odczyt, który się
  nie udał (z poleceniem **Ponów**), zostają w wierszu, dopóki dokument nie zostanie odczytany ponownie; zatrzymane
  sprawdzenie bezpieczeństwa zostawia wyniki poprzedniego sprawdzenia i mówi o tym. Brak połączenia jest błędem stanu
  karty. Czerwona ramka z błędem odczytu zniknęła.
- **Certyfikaty: sprawdzanie adresów HTTP i certyfikatów LDAPS mówi w wierszu karty.** Na karcie *Zdrowie* sprawdzenie
  adresów to **Trwa** – „Sprawdzanie adresów AIA, CDP i OCSP – 3 z 8…” z postępem w kółku i poleceniem **Zatrzymaj**;
  w tym czasie *Sprawdź adresy HTTP* jest przyciskiem **Anuluj**. To samo robi *Sprawdź certyfikaty LDAPS* na karcie
  *Certyfikaty LDAPS* i w raporcie gotowości: „Sprawdzanie certyfikatów LDAPS kontrolerów domeny…”. Zatrzymane
  sprawdzenie nie zmienia dotychczasowych wyników i mówi o tym („… zatrzymano: wyników nie zmieniono. Wybierz „Sprawdź
  adresy HTTP”…”); sondowanie, którego nie dało się wykonać, jest błędem karty z poleceniem **Ponów**. W spoczynku wiersz
  karty podaje to, co karta znalazła: podsumowanie zdrowia (po sprawdzeniu także liczbę sprawdzonych adresów) albo
  „Sprawdzone porty: 3; OK: 2; z problemem: 1.”. To, że wiersze adresów HTTP mają stan *Nieznany*, bo sprawdzenia nie
  uruchomiono, mówi uwaga na karcie.
- **Szablony certyfikatów: liczenie kont jest etapem odczytu.** Szablony i ich prawa widać od razu, a liczenie kont
  („liczenie kont: 500…”) można zatrzymać. Gdy się nie uda, szablony zostają, a wiersz mówi, co brakuje: „Nie udało się
  policzyć kont: … Lista pokazuje, kto może się zarejestrować, ale nie liczbę kont. Wybierz „Ponów”, aby wczytać
  ponownie.” – dotąd był to tekst pod podsumowaniem.
- **Certyfikaty: uwagi zamiast ramek.** „Uwagi z odczytu”, ostrzeżenie, że urząd wystawia szablony logowania, a nie
  jest w NTAuth, „prawa szablonu nie zostały ocenione”, zakres przeglądu bezpieczeństwa wraz z tym, czego jego
  sprawdzenie nie mogło odczytać, i zdanie „tylko do odczytu” w raporcie gotowości są uwagami przy treści (znak i tekst,
  bez ramki i bez karty) – dotąd żółtymi ramkami albo kartami.
- **Watchdog: jeden wiersz stanu zakładki.** Zakładka Watchdog mówi teraz w jednym stałym miejscu – w wierszu pod kartą
  połączenia, nad podzakładkami – co robi i jak się to skończyło; wiersz mówi to, co dotyczy wybranej podzakładki. W
  spoczynku jest to podsumowanie: na liście zdarzeń „Zdarzeń: 30 · niepotwierdzonych: 28 · stan na 10:30:00” (po
  zaznaczeniu wierszy także „Zaznaczono: 3”), na kartach reguł, kanałów, raportu dziennego i źródeł wersja konfiguracji
  („Wersja 7 · 14.10.2026 12:00 · CONTOSO\jan.kowalski”), na karcie Stan – godzina odczytu stanu. Dotąd te informacje
  były w pasku stanu okna głównego, w tekstach pod przyciskami i w ramkach – pasek stanu nie mówi już nic o zakładce
  Watchdog. Komunikaty, które wcześniej były czerwoną i żółtą ramką, paskami postępu i napisami pod przyciskami, są
  teraz jednym wierszem z czterema rodzajami (**Trwa**, **Gotowe**, **Uwaga**, **Błąd**), jednym poleceniem i – gdy
  operację można zatrzymać – przyciskiem **Zatrzymaj**. To, co wciąż jest prawdziwe, zostaje w wierszu, dopóki się nie
  zmieni: błąd połączenia, brak adresu usługi, przerwane aktualizacje na żywo, konfiguracja zmieniona przez innego
  administratora.
- **Watchdog: karta połączenia mówi, z kim i jako kto.** Karta pod nagłówkiem zakładki pokazuje adres usługi (do niego
  trafiają poświadczenia) i – po połączeniu – konto, rolę i poziom szczegółowości, z przyciskiem **Zmień adres**. Stanu
  połączenia już nie mówi: łączenie to **Trwa** „Łączenie z admng-wd.contoso.com:8443…”; gdy się nie uda – **Błąd**
  „Nie udało się połączyć z admng-wd.contoso.com:8443: … Wybierz Ponów, aby spróbować ponownie. (Unreachable)” z poleceniem
  **Ponów**, a gdy usługa przedstawiła certyfikat do potwierdzenia – z poleceniem **Sprawdź certyfikat…** (dotąd
  przyciski w karcie). Błędu połączenia nie można zamknąć, dopóki połączenie nie działa; podzakładki są wtedy ukryte jak
  dotąd.
- **Watchdog: odczyt listy zdarzeń można zatrzymać.** Odczyt to **Trwa** „Wczytywanie zdarzeń…” z poleceniem
  **Zatrzymaj**, odczyt starszych stron („Wczytaj starsze”) – „Wczytywanie starszych zdarzeń (wczytano: 100)…”. Zatrzymany
  albo nieudany odczyt całej listy zostawia ją pustą, a aktualizacje na żywo wstrzymane, i mówi o tym w wierszu z
  poleceniem **Odśwież** albo **Ponów**, dopóki lista nie zostanie wczytana ponownie (dotąd czerwona ramka z przyciskiem
  *Ponów* nad listą). Zatrzymany lub nieudany odczyt starszych zdarzeń zostawia listę, jak była – wynik można zamknąć.
  Potwierdzanie wielu zdarzeń naraz pokazuje postęp („Potwierdzanie zdarzeń: 500 z 1 200…”) i nie ma przycisku
  *Zatrzymaj*, bo zmienia dane usługi. W panelu szczegółów zdarzenia odczyt szczegółów to kółko (dotąd cienki pasek), a
  to, że szczegółów nie udało się odczytać, jest uwagą pod tytułem zdarzenia; odpowiedzi na „Potwierdź”, „Pokaż w AD
  Manager” i „Odblokuj konto” mówi wiersz zakładki, a następne zaznaczone zdarzenie je zamyka.
- **Watchdog: konfiguracja usługi mówi w wierszu zakładki – na każdej karcie, która ją edytuje.** Odczyt i sprawdzanie
  konfiguracji można zatrzymać („Sprawdzanie konfiguracji…”); wynik sprawdzania to **Gotowe** „Konfiguracja jest
  poprawna.” albo **Uwaga** „Konfiguracja jest nieprawidłowa – popraw uwagi z listy.”; zapis nie ma *Zatrzymaj* i kończy
  się **Gotowe** „Zapisano konfigurację (wersja 8).”. Zapis, którego usługa nie przyjęła („Konfiguracja nie została
  zapisana – popraw zaznaczone pola.”), i błąd zapisu zostają wynikiem. Że ktoś inny zapisał konfigurację wcześniej, jest
  stanem kart, dopóki konfiguracja nie zostanie wczytana ponownie (polecenie **Wczytaj ponownie**); brak konfiguracji
  (odczyt się nie udał albo go zatrzymano) – stanem z poleceniem **Ponów** albo **Wczytaj ponownie**. Wersja
  konfiguracji jest podsumowaniem wiersza i zniknęła z paska pod przyciskami, gdzie zostały „Niezapisane zmiany”.
- **Watchdog: podgląd raportu dziennego i reguły, „Wyślij teraz”, przywracanie reguły.** Podgląd raportu i podgląd
  reguły to **Trwa** z poleceniem **Zatrzymaj** (podgląd zostaje, jak był, gdy go zatrzymano lub gdy się nie udał – z
  poleceniem **Ponów**); „Wyślij teraz” zmienia dane usługi i nie ma *Zatrzymaj*. Że wbudowanej reguły nie udało się
  przywrócić do domyślnej (usługa jej nie zna albo nie odpowiedziała), mówi wiersz, a nie okno z błędem. Zapis
  konfiguracji może się odbywać, gdy przygotowywany jest podgląd: każde z nich mówi osobno.
- **Watchdog: test dostarczenia – przycisk, który uruchamia i przerywa.** *Wyślij test* (SMTP) i *Wyślij test* kanału są
  teraz przyciskami, które na czas testu zamieniają się w **Anuluj** – osobny przycisk i pasek postępu w karcie zniknęły.
  Wiersz mówi **Trwa** „Wysyłanie testu…”, a potem **Gotowe** „Test zakończony powodzeniem (840 ms).” albo **Błąd** „Test nie
  powiódł się (1,3 s). Etapy testu pokazują, gdzie się nie powiódł.”; test, którego nie wysłano, i dlaczego („Nic nie
  zostało wysłane: …”), albo test zatrzymany („nie wiadomo, czy cokolwiek dotarło do miejsca docelowego”) też są w wierszu. Każdy
  etap zostaje w karcie ze swoim wynikiem (✓ / ✗, kod, czas) razem z podpowiedziami. Test uruchomiony w trakcie innego
  testu przerywa tamten.
- **Watchdog: karta Stan.** Odczyt stanu, dziennika dostarczania i dziennika zmian konfiguracji mówi w wierszu jeden po
  drugim („Wczytywanie stanu usługi…”) z poleceniem **Zatrzymaj**; odczyt, który się nie udał, gdy nic nie jest
  pokazane, zostaje stanem karty z poleceniem **Ponów**, a przy pokazanym stanie jest wynikiem do zamknięcia.
  „Ponów” przy martwej wysyłce jest zapisem do usługi i bez *Zatrzymaj*; błąd mówi wiersz, a wiersz listy – że wysyłka
  czeka znowu.
- **Watchdog: uwagi zamiast ramek.** Tryb wdrożenia („Tryb wdrożenia do 21.10.2026…” z przyciskiem **Zakończ tryb
  wdrożenia**), „Wymaga roli administratora” (rola Viewer), uwagi usługi do konfiguracji, podpowiedź o wygenerowanym
  kluczu podpisu („pokazany tylko raz”) i podpowiedzi do testu dostarczenia są uwagami przy treści (znak i tekst, bez
  ramki), tam gdzie były żółte i czerwone ramki.
- **Korespondencja: wiersz stanu pod górną krawędzią okna zamiast ramek, pasków postępu i tekstu w stopce.** Okno mówi
  w jednym stałym miejscu, co robi zakładka, którą widzisz, i jak się to skończyło. W spoczynku: liczby z danej zakładki –
  *Odbiorcy* („Odbiorcy: 120 • do wysłania: 95 • pominięci: 20”), *Skrzynki* („Dopasowane: 90 · do sprawdzenia: 12 · …”),
  a na zakładce *Odpowiedzi* – ile wysyłek znaleziono i podsumowanie odpowiedzi („Odpowiedzi: 40 z 95 · Tak: 30 · Nie: 10”);
  dotąd te teksty były osobnymi napisami na zakładkach, a ostatni komunikat – w stopce okna. Wynik wczytania i działań
  na liście odbiorców („Wczytano plik „kontakty.csv” (CSV): 120 nowych, 3 powtórzone, 2 błędne adresy”) jest w tym wierszu
  jako **Gotowe** albo **Uwaga** – także to, że część plików nie dała się wczytać i dlaczego, że w schowku nie ma adresów,
  że folderu *Pobrane* nie ma – i można go zamknąć krzyżykiem. Stopka zostaje przy połączeniu z Outlookiem i ma kółko, gdy
  okno się wczytuje (dotąd: pasek).
- **Korespondencja: stany, które trwają.** Że sesja okna się zakończyła, mówią zakładki, które czytają katalog – *Odbiorcy*
  i *Skrzynki* – razem z tym, że wysyłanie przez Outlooka nadal działa; zakładka *Wysyłka* niczego z tego nie pokazuje, więc
  trwająca wysyłka nie jest zasłonięta. Że Outlook jest niedostępny (nie ma klasycznego Outlooka, nie działa tu albo połączenie
  się nie udało), mówią zakładki *Wysyłka* i *Odpowiedzi* – z powodem i, gdy połączenie się nie udało, z poleceniem **Połącz
  z Outlookiem**; zakładki *Odbiorcy*, *Skrzynki* i *Wiadomość* niczego o nim nie mówią. Że dziennika „już wysłane” nie da się
  użyć (albo że założono nowy – ze ścieżką kopii poprzedniego), mówi okno na każdej zakładce; dotąd była to czerwona ramka u
  góry okna.
- **Korespondencja: wysyłkę, odczyty i szukanie przerywa się tym przyciskiem, którym się je zaczęło.** *Test do siebie*,
  *Utwórz wersje robocze*, *Wyślij* i *Ponów nieudane* na czas przebiegu zamieniają się w **Anuluj** (ten, którym zaczęto;
  pozostałe są wyłączone), a *Znajdź właścicieli*, *Szukaj w Elementach wysłanych*, *Pokaż odpowiedzi* i *Podgląd z Outlooka* –
  tak samo; osobnego przycisku *Zatrzymaj* i pasków postępu już nie ma. Wiersz mówi **Trwa** „Wysyłam wiadomości: 12 z 95 ·
  zostało ok. 4 min…” (z częścią wykonaną w kółku), „Szukam właścicieli skrzynek: skrzynek 120, osób 4 210…”,
  „Przeszukuję „Elementy wysłane”…”, a po naciśnięciu pokazuje „Zatrzymywanie…” od razu; wysyłkę przerywa się po wiadomości,
  która już idzie do Outlooka. Wynik: **Gotowe** („Wysyłka zakończona: udane 95, błędy 0. Każda wiadomość jest na liście
  wyników.”), **Uwaga** – z tym, co nacisnąć dalej („Wysyłka przerwana: udane 12, błędy 0, niewysłane 83. Naciśnij „Wyślij”,
  aby wysłać resztę…”; „… Naciśnij „Ponów nieudane”…”; zatrzymanie po pięciu błędach z rzędu), albo **Błąd** z rodzajem
  awarii Outlooka na końcu. Przerwane szukanie właścicieli, odczyt *Elementów wysłanych* i podgląd z Outlooka mówią, co
  zostało bez zmian, i oferują **Ponów**; wczytywanie plików i odczyt z katalogu (*Zaznaczone obiekty*, *Wybierz
  użytkowników…*, *Członkowie grupy…*) też można przerwać, a błąd odczytu z katalogu ma **Ponów** – dotąd: okno błędu.
  Szukanie, które się nie udało, mówi dlaczego w wierszu zakładki *Skrzynki*, dopóki nie zaczniesz następnego.
- **Korespondencja: uwagi przy treści zamiast żółtych tekstów.** Co zostało pominięte przy wklejaniu do treści, co jest nie
  tak z wybranym podpisem, ostrzeżenia podglądu (nieznane pola, puste pola, rozmiar) i „Podgląd z Outlooka” są uwagami
  przy treści (znak i tekst, bez ramki). Nieudany podgląd z Outlooka jest błędem w wierszu z poleceniem **Ponów**, a nie
  jednym z ostrzeżeń podglądu; okno jest o 36 px wyższe.
- **Co się nie zmieniło:** wiersz stanu mają teraz lista obiektów, wszystkie karty zasad grupy, kreator zakładania wielu
  kont, przegląd komputerów i zdalne akcje, dzienniki zdarzeń, gotowość środowiska, cztery dokumenty certyfikatów,
  zakładka Watchdog, okno korespondencji i wszystkie okna oraz dialogi wymienione wyżej; uwagi zamiast ramek mają też okna
  zasad grupy, które tylko zbierają dane. Eksport do pliku i kopiowanie do schowka nadal potwierdza powiadomienie, a pasek
  postępu pojedynczej operacji na liście operacji w tle zostaje w swoim wierszu. W oknie korespondencji nadal pokazują
  okna: lista powodów, dla których wiadomości nie można jeszcze wysłać („Wiadomości nie można jeszcze wysłać: …”), błędny
  adres testu, błędy zapisu raportu, szablonów i dziennika oraz pytania; powiadomienie po zakończeniu wysyłki też zostaje.
  Animacja nowego komunikatu (krótkie wsunięcie) i zamiany napisu na przycisku działa tylko przy włączonych animacjach
  (*Ustawienia → Ogólne → Wygląd → Animacje*); kółko postępu obraca się zawsze.

## Inne zmiany

- **Angielski interfejs:** 33 starsze teksty (zestawy sprawdzeń przeglądu kont, tytuły planów zasad grupy, ustalenia
  diagnostyki i porządków GPO) otwierały cytat polskim cudzysłowem „ – teraz używają angielskich, jak reszta.
- **Postęp operacji równoległych:** licznik postępu przy planowaniu i wykonywaniu zmian wsadowych, imporcie CSV,
  odczycie folderów zasad grupy i sprawdzaniu adresów AIA/CDP mógł zatrzymać się o jedną pozycję przed końcem
  (np. „11 z 12”), choć operacja była zakończona – meldunki równoległych wątków mogły się wyprzedzać. Teraz
  przychodzą po kolei.
- **Przycisk „Zasady grupy” nad listą obiektów** był pustą ramką – miał tylko podpowiedź. Teraz ma ikonę obiektu
  zasad grupy i nazwę polecenia. W oknie *Znajdź*, gdzie nic nie robił, już go nie ma.

## AdMng Watchdog {{VERSION}}

- Bez zmian w działaniu usługi i bez migracji bazy.

## Aktualizacja z {{PREVIOUS}}

- **AD Manager:** rozpakuj archiwum w miejsce poprzedniego; ustawienia w `%APPDATA%\AdMng` zostają bez zmian.
- **AdMng Watchdog:** aktualizacja nie jest potrzebna (bez zmian w działaniu); pakiet {{VERSION}} można zainstalować
  `apt install ./admng-watchdog_{{VERSION}}-1_amd64.deb`.
- **Powrót do {{PREVIOUS}}:** bez ograniczeń (starsza wersja pomija nowe ustawienia).

## Znane ograniczenia

<!-- TODO: ograniczenia nowych funkcji i to, czego nie sprawdzono na prawdziwej domenie -->
- Wszystkie ograniczenia z wersji {{PREVIOUS}} obowiązują nadal.

## Co dalej

<!-- TODO: następne kroki z ROADMAP.md (albo usuń ten rozdział) -->

## English summary

<!-- TODO: one paragraph in English: "AD Manager {{VERSION}} (preview) adds ...", links to docs/en with the tag v{{VERSION}} -->
