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
- **Okna *Przywróć z kopii zapasowej* i *Importuj ustawienia* nie mówią już „Brak kopii zapasowych w folderze” o
  folderze, którego nie odczytały.** Gdy folderu z kopiami nie udało się odczytać, a potem zatrzymano (*Zatrzymaj*)
  odczyt kolejnego folderu – np. na udziale, który nie odpowiada – wiersz stanu po zamknięciu komunikatu o zatrzymaniu
  podawał, że w folderze nie ma kopii, a powód pustej listy znikał. Teraz zatrzymany odczyt zostawia listę i jej powód
  tak, jak były: wiersz znowu mówi, dlaczego listy nie wczytano, z przyciskiem *Ponów*, a liczbę kopii w folderze
  podaje dopiero po odczycie, który się udał.
- **Okno *Aktualizacja szablonów ADMX* nie zostaje już z paczką bez plików i bez *Ponów*.** Gdy po zmianie wybranych
  języków paczki albo opcji *Zastąp stare szablony z tą samą przestrzenią nazw* trwało porównanie z magazynem
  centralnym, a operator w tym czasie wczytał inną paczkę (plik, folder, szablony tego komputera, pobranie), wczytywanie
  przerywało porównanie bez słowa. Jeśli nowej paczki nie wczytano – błąd albo *Zatrzymaj* – okno pokazywało poprzednią
  paczkę bez listy plików, bez podsumowania i bez niczego, co porównałoby ją ponownie. Teraz po zamknięciu komunikatu
  wczytywania wiersz stanu mówi, że porównanie przerwano, aby wczytać inną paczkę, i że pliki nie są pokazane, z
  przyciskiem *Ponów*. Porównanie po aktualizacji magazynu jest jej ostatnim etapem: źródła paczek, języki i opcje
  czekają na nie tak jak przy wczytywaniu (zmiana wybranych języków w tym czasie przerywała je i wiersz nie mówił już, co
  aktualizacja zapisała), a wynik aktualizacji – *Magazyn centralny zaktualizowany: …* – wiersz podaje także wtedy, gdy
  to porównanie zatrzymano albo się nie udało.
- **Porównanie dwóch GPO: nieudany eksport w trakcie porównania i uwaga o SYSVOL po zatrzymanym odświeżeniu.** Wiersze
  porównania są widoczne, zanim skończy się wczytywanie szablonów administracyjnych, więc *Eksportuj CSV…* można wybrać
  już wtedy; eksport, który się w tym czasie nie udał (np. plik jest otwarty w innym programie albo brak uprawnień do
  folderu), nie był nigdzie zgłaszany. Teraz mówi o nim powiadomienie, a wiersz stanu zostaje przy trwającym porównaniu
  i jego *Zatrzymaj*. Uwaga nad wierszami o tym, czego nie porównano (SYSVOL niedostępny), znikała, gdy *Odśwież*
  zatrzymano albo się nie udało, choć wiersze poprzedniego porównania zostawały – teraz zostaje razem z nimi.
- **Lista obiektów: pusty kontener z tekstem filtra, ponowiony pierwszy odczyt i *Wyczyść filtr* dużego kontenera.**
  Gdy w kontenerze bez obiektów wpisano tekst szybkiego filtra, lista mówiła *Żaden obiekt nie pasuje do filtra.* z
  działaniem *Wyczyść filtr*, choć tekst niczego nie ukrywał – a *Wyczyść filtr* prowadził tylko do drugiego zdania;
  teraz mówi *Brak elementów do pokazania w tym widoku.* z *Odśwież* (gotowy szybki filtr, np. *Włączone*, nadal liczy
  się jako filtr, bo filtruje na serwerze). Gdy pierwszy odczyt kontenera nie udał się albo zatrzymano go przed pierwszym
  obiektem, *Ponów* lub *Odśwież* w wierszu stanu – albo ponowne otwarcie tego samego kontenera – pokazywały pustą
  listę; teraz pokazują szare wiersze zastępcze pierwszego odczytu, dopóki nie przyjdą obiekty. *Wyczyść filtr* na
  bardzo dużym kontenerze (lista wirtualna VLV, gdzie tekst filtruje na serwerze) z włączonym gotowym szybkim filtrem i
  tekstem czytał kontener dwa razy – drugi raz po chwili, gdy minęło opóźnienie wpisywanego tekstu; teraz czyta go raz,
  od razu, także gdy włączony był sam tekst.
- **Okno *Wyślij wiadomość do użytkowników*: wybór z paska transportu czeka, aż okno skończy pracę.** Gdy wiadomości nie
  pasowały do transportu (np. AdMng Watchdog nie przyjmuje obrazów), pasek nad przyciskiem *Wyślij* oferuje *Wyślij
  bezpośrednio przez SMTP* i *Wyślij bez obrazów*. Gdy po tym ponownie wybrano *Przygotuj podgląd*, pasek – dotyczący
  jeszcze poprzedniego podglądu – był widoczny przez cały odczyt kont, a jego przyciski działały: *Wyślij bezpośrednio
  przez SMTP* przerywał bez słowa przygotowywanie nowego podglądu i wysyłał wiadomości poprzedniego. Teraz oba przyciski
  znikają z paska, dopóki okno przygotowuje podgląd; nowy podgląd zamyka pasek, a przy ponownym *Wyślij* pasek wraca,
  jeśli wiadomości nadal nie pasują.
- **Kopia zapasowa GPO odmienia liczbę GPO.** Zatrzymana albo nieudana kopia jednego GPO – w oknie *Kopia zapasowa
  obiektów zasad grupy* i w kopii przed usunięciem GPO (*Utwórz kopię zapasową przed usunięciem*) – mówiła „zapisano 0 z
  1 obiektów GPO” i „Nie udało się utworzyć kopii zapasowych 1 z 1 obiektów GPO” – teraz „z 1 obiektu GPO” (po angielsku
  „0 of 1 GPO was written”, „1 of 1 GPO”).
- **Korespondencja: plik listy *Nie wysyłać*, którego nie udało się wczytać, to jedna odpowiedź.** Gdy wśród plików
  wybranych w *Nie wysyłać → Z pliku…* był taki, którego nie udało się wczytać, karta *Odbiorcy* mówiła najpierw, że
  adresy dodano, a zaraz potem to samo z nazwą pliku, którego nie wczytano; w czasie odczytu z katalogu były to dwa
  powiadomienia, pierwsze jako sukces. Teraz wiersz stanu podaje jedną uwagę – ile adresów dodano i którego pliku nie
  wczytano – a w czasie odczytu z katalogu jest to jedno powiadomienie z ostrzeżeniem.

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
