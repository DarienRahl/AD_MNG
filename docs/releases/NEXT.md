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
