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
  komunikat zostaje, dopóki lista nie zostanie wczytana. Wiersz ma stałą wysokość, więc wiersze listy nie przesuwają
  się już, gdy wczytywanie się zaczyna i kończy – dotąd nad listą pojawiał się na ten czas pasek postępu
  z przyciskiem, a błąd był w ramce na liście. Rodzaj komunikatu widać po ikonie i po słowie na początku, nie tylko
  po kolorze; czytnik ekranu sam odczytuje nowy komunikat. Komunikat, który można zamknąć, ma przycisk zamknięcia –
  po zamknięciu wraca podsumowanie.
- **Pasek stanu okna głównego** nie powtarza już podsumowania listy i liczby zaznaczonych obiektów – dla karty
  z listą obiektów są one w wierszu stanu listy. Połączenie, kontroler domeny, zabezpieczenia połączenia, działania,
  wersja i znaczek listy wirtualnej zostają na swoim miejscu; dla pozostałych kart (Watchdog, dzienniki zdarzeń,
  zasady grupy, certyfikaty, gotowość środowiska) pasek działa jak dotąd.
- **Okno *Znajdź* (Ctrl+F): jeden przycisk do szukania i przerywania.** Przycisk **Znajdź teraz** na czas
  wyszukiwania zamienia się w **Anuluj** z kółkiem postępu – wyszukiwanie przerywa się tam, gdzie się je zaczęło; po
  naciśnięciu pokazuje „Zatrzymywanie…”, dopóki wyszukiwanie się nie skończy. Osobnego przycisku *Zatrzymaj* już nie
  ma, a przycisk nie zmienia szerokości między stanami. Enter w polu nazwy nadal zaczyna wyszukiwanie; naciśnięty
  jeszcze raz w trakcie niczego nie przerywa. U góry okna jest wiersz stanu: przed pierwszym wyszukiwaniem
  podpowiedź, potem przebieg wyszukiwania z poleceniem **Zatrzymaj**, podsumowanie wyników, informacja o zatrzymaniu
  (z **Odśwież**) albo błąd (z **Ponów**), a także to, że sesja okna się zakończyła – dotąd: ramka u góry okna
  i tekst w stopce.
- **Co się nie zmieniło:** pozostałe okna i karty mają na razie dotychczasowe ramki ostrzeżeń i błędów oraz paski
  postępu – przejdą na wiersz stanu w następnych wydaniach. Animacja nowego komunikatu (krótkie wsunięcie) i zamiany
  napisu na przycisku działa tylko przy włączonych animacjach (*Ustawienia → Ogólne → Wygląd → Animacje*); kółko
  postępu obraca się zawsze.

## Inne zmiany

- **Angielski interfejs:** 33 starsze teksty (zestawy sprawdzeń przeglądu kont, tytuły planów zasad grupy, ustalenia
  diagnostyki i porządków GPO) otwierały cytat polskim cudzysłowem „ – teraz używają angielskich, jak reszta.
- **Postęp operacji równoległych:** licznik postępu przy planowaniu i wykonywaniu zmian wsadowych, imporcie CSV,
  odczycie folderów zasad grupy i sprawdzaniu adresów AIA/CDP mógł zatrzymać się o jedną pozycję przed końcem
  (np. „11 z 12”), choć operacja była zakończona – meldunki równoległych wątków mogły się wyprzedzać. Teraz
  przychodzą po kolei.

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
