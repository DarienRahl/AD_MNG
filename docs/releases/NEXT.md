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
  z listą obiektów są one w wierszu stanu listy. Połączenie, kontroler domeny, zabezpieczenia połączenia, działania,
  wersja i znaczek listy wirtualnej zostają na swoim miejscu; dla pozostałych kart (Watchdog, dzienniki zdarzeń,
  zasady grupy, certyfikaty, gotowość środowiska) pasek działa jak dotąd.
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
- **Co się nie zmieniło:** wiersz stanu mają teraz lista obiektów i wszystkie okna oraz dialogi wymienione wyżej.
  Pozostałe karty i okna – zasady grupy, Watchdog, dzienniki zdarzeń, certyfikaty, przegląd komputerów i zdalne
  akcje, korespondencja, gotowość środowiska – mają na razie dotychczasowe ramki ostrzeżeń i błędów oraz paski
  postępu; przejdą na wiersz stanu w następnych wydaniach. Dotyczy to także kreatora zakładania wielu kont, który jest
  pierwszym krokiem w oknie edytora zbiorczego. Eksport do pliku i kopiowanie do schowka nadal potwierdza
  powiadomienie, a pasek postępu pojedynczej operacji na liście operacji w tle zostaje w swoim wierszu. Animacja
  nowego komunikatu (krótkie wsunięcie) i zamiany napisu na przycisku działa tylko przy włączonych animacjach
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
