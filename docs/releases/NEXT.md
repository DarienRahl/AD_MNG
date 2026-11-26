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

## Nowe: <!-- TODO: nazwa funkcji -->

<!-- TODO: **Gdzie:** wejścia w interfejsie, potem lista najważniejszych możliwości i link do instrukcji w docs/ (z tagiem v{{VERSION}}) -->

## Inne zmiany

- **Zapis i odczyt obiektu nie wychodzą już połączeniem z kontrolerem domeny, które właśnie się zerwało.** Okno
  połączenia rozkłada równoczesne operacje na kilka połączeń z tym samym kontrolerem domeny. Gdy jedno z nich zostało
  zerwane albo przestało odpowiadać pod innym żądaniem, żądanie, które w tej samej chwili miało nim wyjść, mogło jeszcze
  zostać nim wysłane i skończyć się błędem: od razu – przy zapisie, którego program nie powtarza, gdy ten mógł dotrzeć do
  serwera – albo dopiero po upływie limitu czasu żądania. Teraz pojedyncza operacja – zapis albo odczyt jednego obiektu –
  nie wychodzi połączeniem, o którym program już wie, że się zerwało, tylko innym połączeniem z tym samym kontrolerem
  domeny. Wyszukiwanie stronicowane (np. w oknie wyszukiwania) działa jak dotąd: jego strona wychodzi połączeniem, które
  wyszukiwanie dostało, także gdy to właśnie zostało zerwane, bo serwer wiąże z nim kolejne strony wyniku. Pierwsza
  strona przechodzi wtedy na inne połączenie, gdy serwer to połączenie zerwał; kolejna strona – i pierwsza, gdy
  połączenie przestało odpowiadać – może w tym samym wyścigu jeszcze zakończyć wyszukiwanie błędem. Był to rzadki wyścig,
  możliwy przy kilku operacjach naraz w jednym oknie.
- **Dokładne ostatnie logowanie znowu mówi, dlaczego kontrolera domeny nie udało się odczytać.** W *Przeglądzie kont*
  (karta *Stan kont*) i w *Przeglądzie komputerów* wynik odczytu ze wszystkich kontrolerów domeny wymieniał tylko nazwy
  kontrolerów, z których nie udało się odczytać – jako „Brak odpowiedzi: dc02.contoso.test.”, także gdy kontroler
  odpowiedział, tylko odmówił. Teraz wiersz stanu podaje każdy z nich z powodem, np. „Nie udało się odczytać z:
  dc02.contoso.test (Upłynął limit czasu operacji); dc03.contoso.test (Odmowa dostępu).”, więc widać, czy kontroler jest
  niedostępny, czy brakuje uprawnień. W *Przeglądzie komputerów* wynik podaje teraz „Dokładne ostatnie logowanie odczytano
  na 1 z 3 kontrolerów domeny”, a nie „Odpowiedziało 1 z 3 kontrolerów domeny”, bo kontroler, który odmówił, też
  odpowiedział. Dziennik diagnostyczny nadal zapisuje tylko liczby, bez nazw kontrolerów.
- **Okno *Zdalne akcje* nazywa właściwą przyczynę, gdy nie może działać.** Gdy komputera nie udało się odczytać z
  katalogu, wiersz stanu okna podawał sam powód („Kontroler domeny jest niedostępny.”); teraz mówi też, czego nie udało
  się zrobić, i podaje kod: *Nie udało się odczytać komputera „KRK-PC101”: Kontroler domeny jest niedostępny. (LDAP
  52)*. Kopia komunikatu w dzienniku diagnostycznym mówiła przy każdej przyczynie, że komputer nie ma nazwy DNS; teraz
  zapisuje właściwą: obiekt nie jest komputerem, brak nazwy DNS, nieudany odczyt (z kodem) albo brak zdalnych akcji w tym
  programie. *Ping* w *Przeglądzie komputerów*, gdy nie udało się odczytać komputera, też podaje teraz kod błędu.
- **Zatrzymane sprawdzanie w kreatorze zakładania kont nie rusza już samo od nowa.** Gdy w trakcie sprawdzania (krok
  *Sprawdzenie*) poprawiono komórkę albo odznaczono wiersz, a potem – zanim sprawdzanie, które ta zmiana przerwała,
  zdążyło się zakończyć, np. w czasie długiego odczytu z kontrolera domeny – wybrano *Zatrzymaj* w wierszu stanu (albo
  *Anuluj* przycisku *Sprawdź ponownie*), ponowne wyliczenie, które czekało na tę zmianę, zaczynało się chwilę później i
  przykrywało uwagę o zatrzymaniu. Teraz *Zatrzymaj* odwołuje je razem ze sprawdzaniem: krok mówi, że sprawdzanie
  zatrzymano, a *Podgląd* i tak wylicza wiersze jeszcze raz, z tą zmianą. Był to rzadki przypadek.

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
