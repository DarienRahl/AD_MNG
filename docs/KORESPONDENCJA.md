# Korespondencja (Outlook) w AD Managerze (etap U5)

Okno **Korespondencja** to program *Korespondencja WPiA* (`MailerWPiA.exe`) wbudowany w AD Managera: ta sama wiadomość
do wielu odbiorców wysyłana przez **klasycznego Outlooka** zainstalowanego na komputerze operatora. Domyślnie każdy
odbiorca dostaje **osobną wiadomość**, więc adresaci nie widzą się nawzajem; kopie lądują w *Elementach wysłanych*
Twojej skrzynki, wysyłka idzie z Twojego konta i z Twoim podpisem. Wszystkie funkcje programu zostały zachowane (pliki z
adresami, pola `{{…}}`, edytor z wklejaniem z Worda, podpisy, załączniki, podgląd, test, wersje robocze, dziennik „już
wysłane”), a doszły nowe: **odbiorcy prosto z AD** z polami z katalogu (także zwroty grzecznościowe z U1d), pola
własne, szablony, grupowanie po adresie, przyciski głosowania z odczytem odpowiedzi oraz **zapytania do właścicieli
skrzynek** – zakładka *Skrzynki* znajduje osobę, do której przypisano skrzynkę współdzieloną, i pyta ją, czy skrzynka
jest jeszcze potrzebna. Projekt techniczny (po angielsku): [dev/u5-outlook-correspondence.md](dev/u5-outlook-correspondence.md).

Zasady, które obowiązują wszędzie:

- **Wysyła tylko Outlook i tylko po Twoim kliknięciu.** AD Manager przygotowuje każdą wiadomość i przekazuje ją
  Outlookowi; nic nie jest wysyłane w tle ani automatycznie.
- **Nic z katalogu nie trafia na dysk.** Lista odbiorców, wartości pól z AD, podgląd, raport wysyłki, dopasowania
  skrzynek i odpowiedzi istnieją tylko w pamięci okna. Na dysku są wyłącznie ustawienia, szablony, ostatni szkic i
  dziennik „już wysłane”, który przechowuje **skróty** adresów, a nie adresy (rozdział 11). Plik powstaje poza tym
  tylko wtedy, gdy operator sam go zapisze (*Eksport CSV…*, *Zapisz raport…*, *Zapisz jako .html…*).
- **Katalog tylko do odczytu.** Okno niczego w AD nie zmienia (wiadomość nie jest zmianą katalogu). Odbiorców i
  skrzynki czyta przez sesję swojego okna połączenia – z jego kontrolerem domeny i kontem.
- **Żadnych haseł.** Outlook wysyła z własnym profilem i własnym logowaniem; AD Manager nie widzi ani nie zapisuje
  żadnego hasła pocztowego.

---

## 1. Wymagania

- **Windows i klasyczny Outlook** (Microsoft 365 Apps albo Office 2016 lub nowszy, wersja 32- lub 64-bitowa) z
  profilem pocztowym na tym samym komputerze co AD Manager. **„Nowy Outlook” i Outlook w przeglądarce nie mają
  automatyzacji** i nie zadziałają – okno pokaże wtedy *Outlook: brak klasycznego Outlooka*.
- **Ten sam poziom uprawnień.** AD Manager i Outlook muszą działać tak samo: oba zwyczajnie albo oba przez *Uruchom jako
  administrator*. Windows nie pozwala programowi uruchomionemu jako administrator połączyć się z Outlookiem uruchomionym
  zwyczajnie (komunikat *AD Manager działa jako administrator, a Outlook nie…*). Najprościej uruchamiać AD Managera bez
  podnoszenia uprawnień. Jeśli musi działać jako administrator, zamknij najpierw Outlooka – AD Manager uruchomi własnego,
  który do zamknięcia też będzie działał jako administrator.
- **Zabezpieczenie Outlooka przed programami** (*Centrum zaufania → Dostęp programowy*). Gdy program antywirusowy jest
  nieaktywny lub nieaktualny, Outlook przy pierwszej wysyłce pyta, czy zezwolić programowi na wysyłanie wiadomości w
  Twoim imieniu. To jego zabezpieczenie – zezwól na czas wysyłki. Odmowa kończy się komunikatem *Outlook nie zgodził się
  na wysłanie wiadomości przez program*. W firmie zachowanie tego okna ustawia się zasadą grupy Outlooka.
- **Konta w profilu.** *Wyślij jako* oferuje konta z profilu Outlooka; inne konto trzeba najpierw dodać do profilu.
  *W imieniu* (skrzynka współdzielona) wymaga uprawnienia *Wyślij w imieniu* (albo *Wyślij jako*) do tej skrzynki w
  Exchange.
- **Połączenie z AD** jest potrzebne tylko dla odbiorców z katalogu i zakładki *Skrzynki*. Pliki, schowek i wysyłka
  działają także po rozłączeniu okna połączenia.
- **Podgląd** w oknie korzysta z Microsoft Edge WebView2 Runtime (jak podgląd wiadomości U1c); bez niego widać źródło
  HTML, a *Otwórz w przeglądarce* działa zawsze.

## 2. Otwieranie okna

| Skąd | Co się otwiera |
|---|---|
| *Narzędzia → Korespondencja (Outlook)…* (paleta poleceń: `tools.correspondence`) | jedno okno na okno połączenia, z ostatnim szkicem wiadomości |
| *Akcja → Wyślij korespondencję…*, menu kontekstowe listy (`object.correspondence`) | nowe okno z zaznaczonymi użytkownikami i kontaktami jako odbiorcami z AD; zaznaczona grupa daje swoich członków |
| *Akcja → Zapytaj właścicieli skrzynek…*, menu kontekstowe listy (`object.mailboxOwners`) | nowe okno na zakładce *Skrzynki*, z zaznaczonymi kontami jako zakresem |
| przycisk *Właściciele skrzynek…* na zakładce *Odbiorcy* | pokazuje zakładkę *Skrzynki* w bieżącym oknie |

Okno należy do okna połączenia: ma jego kolorowy pasek, a tytuł *Korespondencja – contoso.test* podaje domenę. Zakładki
odpowiadają krokom pracy: *Skrzynki* (tylko gdy potrzebna), **1 · Odbiorcy**, **2 · Wiadomość**, **3 · Podgląd**,
**4 · Wysyłka**, **5 · Odpowiedzi**. Pasek stanu pokazuje połączenie z Outlookiem (*Outlook: połączono (2 konta)*) i
ostatni komunikat; *Pomoc* wyświetla skróconą instrukcję.

Przy otwarciu okno łączy się z Outlookiem w tle: dołącza do uruchomionego Outlooka albo go uruchamia (trzy próby).
Brak Outlooka nie jest błędem okna – widać go tylko na pasku stanu, a *Połącz z Outlookiem* na zakładce *Wiadomość*
próbuje ponownie. Zamknięcie okna w trakcie wysyłki pyta, czy ją przerwać; przy zamknięciu zapisywany jest ostatni
szkic i opcje.

## 3. Krok 1 – Odbiorcy

### Źródła odbiorców

| Przycisk | Co robi |
|---|---|
| *Wczytaj CSV z Pobranych* | najnowszy plik pasujący do wzorca `*kontakty*.csv` w folderze *Pobrane* (także przekierowanym, np. do OneDrive; obejmuje pliki `prawo-pl_kontakty_*.csv`) |
| *Z pliku (CSV, Excel, Word…)* | jeden lub wiele plików: CSV, TSV, TXT, XLSX/XLSM/XLTX, DOCX/DOTX/DOCM, ODT/ODS, RTF, HTM/HTML (do 50 MB każdy) |
| *Wklej ze schowka* | tabela skopiowana z Excela albo dowolny tekst z adresami (z Worda, wiadomości, strony); rozpoznaje zapis „Jan Kowalski &lt;jan@x.pl&gt;” i kolumny *Nazwa* + *E-mail* |
| *Dodaj ręcznie…* | okno do wpisania lub wklejenia adresów (każdy tekst – adresy są z niego wyłuskiwane) |
| przeciągnięcie plików na listę | jak *Z pliku* |
| *Z katalogu: Zaznaczone obiekty* | użytkownicy, kontakty i grupy (ich członkowie) zaznaczeni w oknie połączenia |
| *Wybierz użytkowników…* | okno wyboru obiektów: użytkownicy i kontakty |
| *Członkowie grupy…* | członkowie wybranej grupy, także zagnieżdżeni (użytkownicy i kontakty, do 10 000) |
| *Właściciele skrzynek…* | zakładka *Skrzynki* (rozdział 8) |

Powtórzone adresy są usuwane – także względem odbiorców wczytanych wcześniej. Pasek stanu podsumowuje każde
wczytanie, np. *Wczytano plik: 120 nowych, 3 powtórzone, 2 błędne adresy*.

**Jak czytane są pliki i tabele:**

- Nagłówek tabeli to pierwszy z pierwszych 8 niepustych wierszy; w tekście separatorem jest `;`, tabulator albo `,`
  (w tej kolejności).
- **Kolumna z adresem:** nagłówek *E-mail*, *Email*, *Mail*, *Adres e-mail* albo *E-mail address*, potem *Poczta*, a
  na końcu każdy nagłówek zawierający „mail” – wygrywa pierwsza taka kolumna, w której naprawdę są adresy (*Inne
  e-maile* przegrywa z *E-mail*). Komórka z kilkoma adresami („a@x.pl; b@y.pl”) daje pierwszy poprawny.
- **Kolumna z nazwą:** *Nazwa*, *Name*, *Instytucja*, *Urząd*, *Firma*, *Odbiorca*, *Adresat*, *Imię i nazwisko*,
  *Pełna nazwa* (także jako część nagłówka); gdy jej nie ma, a są kolumny *Imię* i *Nazwisko* (*First name*, *Last
  name*), nazwą jest „Imię Nazwisko”.
- **Każda kolumna staje się polem** `{{Nagłówek}}` (rozdział 4). Pusty nagłówek daje *Kolumna N*, powtórzony –
  *Nagłówek (2)*.
- Wiersze bez poprawnego adresu są liczone jako błędne. Tekst bez tabeli: najpierw pary „Nazwa &lt;adres&gt;” i
  „Nazwa (adres)”, potem pojedyncze adresy.
- **Excel:** każdy arkusz ma własny nagłówek, a arkusze z kolumną adresów są łączone; komórki trafiają do kolumn według
  ich adresu (A1, C1 …), więc puste komórki nie przesuwają kolumn. **Word, OpenDocument, HTML:** tabele zachowują
  kolumny, łącza `mailto:` dają „tekst &lt;adres&gt;”, a tekst poza tabelami jest czytany jak zwykły tekst.
- Kodowanie plików tekstowych: znacznik BOM, UTF-8, a w pozostałych przypadkach strona kodowa Windows (na polskim
  Windows – Windows-1250, jak w CSV z polskiego Excela).
- Adresy są sprawdzane ostrzej niż w Korespondencji WPiA (bez „..”, kropek i łączników na brzegach, z limitami długości),
  a adresy-śmieci ze stron i dokumentów (`example.*`, `*.sentry.io`, `*.w3.org`, `schemas.*`) są pomijane.
- Uszkodzony plik, plik powyżej 50 MB albo podejrzane archiwum (tzw. bomba zip – XLSX, DOCX i ODF to archiwa ZIP)
  są odrzucane z komunikatem; treść pliku nigdy nie trafia do komunikatu ani dziennika.

### Odbiorcy z AD

Odbiorca z katalogu ma nazwę z `displayName` (gdy jej brak – z nazwy obiektu), adres z `mail`, a gdy go brak – główny
adres SMTP z `proxyAddresses`; kolumna *Źródło* pokazuje „AD”. Obiekt bez adresu jest na liście jako błędny, z opisem w
kolumnie *Status*. Wartości są czytane na żywo przez sesję okna połączenia.

**Pola odbiorców z katalogu** (sekcja pod listą) określają, jakie kolumny – a więc pola wiadomości – dostaje każdy
odbiorca z AD. Pole to *Nazwa pola* i *Wyrażenie* w składni **wiadomości do użytkowników (U1)** – z pojedynczymi
nawiasami klamrowymi, ze wszystkimi atrybutami, funkcjami i polami wyliczanymi U1 i U1d:

| Nazwa pola (domyślnie) | Wyrażenie | Przykładowa wartość |
|---|---|---|
| Imię | `{givenName}` | Łukasz |
| Nazwisko | `{sn}` | Wiśniewski |
| Nazwa wyświetlana | `{displayName}` | dr Łukasz Wiśniewski |
| Stanowisko | `{title}` | Kierownik |
| Dział | `{department}` | Księgowość |
| Login | `{sAMAccountName}` | lukasz.wisniewski |
| Zwrot osobowy | `{$salutation}` | Szanowny Panie Doktorze |
| Tytuł | `{$title}` | dr |
| Pan/Pani | `{$honorific}` | Pan |
| Pana/Panią | `{$honorific:biernik}` | Pana |
| Panu/Pani | `{$honorific:celownik}` | Panu |
| Szanowny/a | `{$byGender:Szanowny/Szanowna}` | Szanowny |

*Dodaj pole* dopisuje własne, np. *Ostatnie logowanie* = `{$lastLogon:d MMMM yyyy}`, *Konto utworzone* =
`{$created:d MMMM yyyy}`, *Hasło wygasa* = `{$passwordExpires:d MMMM yyyy}`, *Jednostka* = `{$parentName}` albo *Login
wielkimi* = `{sAMAccountName|upper}`. *Przywróć domyślne* wraca do tabeli powyżej. Wyrażenia z atrybutami poufnymi
(hasła, wszystko z `password`/`pwd`) są odrzucane, jak w U1. Zwroty grzecznościowe korzystają z ustawień *Ustawienia →
Wiadomości e-mail → Zwroty grzecznościowe* (tabela tytułów, lista imion, zachowanie przy nieznanej płci). Zapisywane są
tylko definicje pól (nazwa i wyrażenie), nigdy wartości.

### Lista odbiorców

- Kolumny: *Wyślij* (pole wyboru – odznacz tych, których chcesz pominąć), *E-mail*, *Nazwa*, *Źródło*, *Status*,
  *Wysłano wcześniej*, a za nimi kolumny pól (przewijanie w poziomie).
- Licznik nad listą, np. *Odbiorcy: 120 • do wysłania: 95 • pominięci: 20 • błędne: 5*.
- Pole wyszukiwania filtruje listę po adresie, nazwie, źródle i polach (bez względu na polskie znaki).
- *Zaznacz wszystkie*, *Odznacz wszystkie*, *Usuń zaznaczone*, *Wyczyść listę* (z potwierdzeniem) i *Eksport CSV…* –
  plik we wskazanym miejscu z kolumnami *Nazwa*, *E-mail*, *Źródło*, *Status* i polami, z ochroną przed formułami Excela.

### Nie wysyłać

Własna lista adresów do pominięcia: *Z pliku…* (z każdego obsługiwanego pliku), *Wklej* (ze schowka albo wpisane) i
*Wyczyść*. Lista żyje tylko w pamięci okna – jak w Korespondencji WPiA po zamknięciu jest pusta. Licznik obok pokazuje
obie przyczyny pominięcia, np. *Pominięcia: 12 własnych + 340 z dziennika*.

### Dziennik „już wysłane”

Każdy adres, do którego **wysłano** wiadomość, trafia do dziennika (test do siebie i wersje robocze – nie). Przy
włączonym *Pomijaj już wysłane* (domyślnie) odbiorcy z adresem z dziennika są pomijani, a kolumna *Wysłano wcześniej*
pokazuje datę i temat, np. *28.10.2026 · Czy skrzynka pocztowa jest nadal potrzebna?*. Żeby wysłać ponownie do tych
samych osób (np. przypomnienie), wyłącz *Pomijaj już wysłane*.

*Dziennik „już wysłane”…* pokazuje liczbę wpisów, najstarszy i najnowszy wpis oraz okres przechowywania (365 dni) i
pozwala usunąć wpisy starsze niż ten okres albo wyczyścić cały dziennik. Dziennik **nie zna adresów** – trzyma tylko ich
skróty (rozdział 11), więc nie da się z niego odczytać, do kogo wysłano; służą do tego *Elementy wysłane* Outlooka i raport
wysyłki. Dziennik jest wspólny dla wszystkich okien AD Managera tego użytkownika Windows i należy do niego: po
przeniesieniu profilu na inny komputer albo zalogowaniu innym kontem klucza nie da się odczytać – okno zakłada nowy
dziennik, a poprzedni zachowuje jako `journal-<data>.old` i mówi o tym.

## 4. Pola w temacie i treści

W temacie, treści, elemencie listy i polach własnych wpisuje się pola w podwójnych nawiasach klamrowych:

| Pole | Wartość |
|---|---|
| `{{zwrot}}` | zwrot grzecznościowy z pola *Zwrot grzecznościowy* na zakładce *Wiadomość*, np. „Szanowni Państwo” – ten sam dla wszystkich |
| `{{nazwa}}` | nazwa odbiorcy, np. „Urząd Gminy Andrespol”; gdy odbiorca nie ma nazwy (sam adres) – zwrot grzecznościowy, żeby powitanie nie było puste |
| `{{email}}` | adres odbiorcy |
| `{{lista}}`, `{{liczba}}` | przy grupowaniu po adresie: lista wierszy odbiorcy i ich liczba (rozdział 5) |
| `{{Dowolna kolumna}}` | wartość kolumny wczytanej tabeli, np. `{{Miejscowość}}`, `{{Kod pocztowy}}`, `{{Telefon}}`, pola z AD (`{{Imię}}`, `{{Pana/Panią}}`, `{{Zwrot osobowy}}`) i pola skrzynek |
| `{{Pole własne}}` | wartość pola własnego (niżej) |
| `{{Pole\|tekst}}` | „tekst”, gdy pole jest puste, np. `{{Ostatnie logowanie\|brak danych}}` |

- Nazwę pola pisze się jak nagłówek kolumny – ze spacjami i polskimi znakami. Wielkość liter, spacje, „-”, „_” i polskie
  znaki nie mają znaczenia: `{{Kod pocztowy}}`, `{{kodpocztowy}}` i `{{KOD-POCZTOWY}}` to to samo pole.
- *Wstaw pole* (przy temacie i na pasku edytora) podpowiada wszystkie dostępne pola i wstawia wybrane w miejscu kursora.
  Kolumny powtarzające pola wbudowane (*E-mail*, *Nazwa*) nie są podpowiadane drugi raz.
- Najpierw sprawdzane są pola wbudowane, potem pola własne, a na końcu kolumny – pole własne o nazwie kolumny wygrywa.
- W treści wartości są kodowane jak tekst (znaki `<` czy `&` z pliku nie zmienią wyglądu wiadomości), nowe wiersze w
  komórce dają nowe wiersze w treści; w temacie znaki sterujące zamieniane są na spacje.
- Pole rozcięte formatowaniem (np. Word pogrubił połowę nazwy: `{{Miej`**`scowość`**`}}`) jest sklejane przed wysyłką –
  w obrębie jednego akapitu.
- Zakładka *Podgląd* ostrzega o nazwie pola, której nie ma w danych (podaje listę dostępnych), i o polu pustym u części
  odbiorców (*Pole {{Miejscowość}} jest puste u 12 z 95 odbiorców*). Nieznane pole daje w wiadomości pusty tekst.

Typowy początek listu:

```text
{{zwrot}},
w nawiązaniu do działalności {{nazwa}} w {{Miejscowość}}…
```

**Pola własne** (zakładka *Wiadomość*): nazwa i wartość, ta sama dla wszystkich odbiorców, np. *Termin* = „15 listopada
2026” albo *Telefon działu* = „22 555 01 00”. Wartość może zawierać inne pola (`{{nazwa}}`, kolumny), ale nie inne pola
własne. Pola własne zapisują się w szablonach i w ostatnim szkicu – zamiast pól `{$input:…}` z wiadomości do
użytkowników.

**Dwie składnie pól.** Wiadomość Korespondencji używa `{{Pole}}` – tak jak Korespondencja WPiA, bo kolumny w rodzaju
„Kod pocztowy” nie są nazwami atrybutów. Wyrażenia pól z AD (rozdziały 3 i 8) mają składnię wiadomości do użytkowników
z pojedynczymi nawiasami (`{givenName}`, `{$salutation}`), a w treści używa się ich przez nazwę pola: `{{Imię}}`,
`{{Zwrot osobowy}}`.

## 5. Krok 2 – Wiadomość

### Nadawca

- **Wyślij jako** – konto z Twojego profilu Outlooka; puste pole oznacza konto domyślne profilu. Wybór jest zapamiętywany.
- **W imieniu (skrzynka współdzielona)** – opcjonalnie adres skrzynki, w imieniu której idą wiadomości (wymaga prawa
  *Wyślij w imieniu*). Gdy Outlook nie przyjmie tego ustawienia, wiadomość kończy się błędem – nigdy nie wychodzi po
  cichu z innego nadawcy.
- *Połącz z Outlookiem* i *Sprawdź Outlooka* (rozdział 13).

### Temat, zwrot i treść

- **Temat** – zwykły tekst z polami (*Wstaw pole* obok).
- **Zwrot grzecznościowy – pole {{zwrot}}** – domyślnie „Szanowni Państwo”.
- **Treść** – edytor z białym tłem i czcionką Calibri 11 pt (jak wiadomość w Outlooku), ze sprawdzaniem pisowni po
  polsku. Pasek: pogrubienie (Ctrl+B), kursywa (Ctrl+I), podkreślenie (Ctrl+U), lista punktowana i numerowana,
  *Wstaw odsyłacz…* (zaznacz najpierw tekst w jednym akapicie; adres `https://…`, `mailto:`, `tel:` albo pole, np.
  `{{Link}}`; sama domena dostaje `https://`, adres e-mail – `mailto:`; puste pole usuwa odsyłacz), *Wyczyść
  formatowanie zaznaczenia*, *Wklej bez formatowania* (Ctrl+Shift+V) oraz lista pól z *Wstaw pole*.
- **Wklejanie z Worda, Outlooka lub przeglądarki (Ctrl+V)** zachowuje formatowanie, tabele, listy i obrazki. Treść jest
  oczyszczana tak samo jak w wiadomościach do użytkowników (U1c): style Office wpisane w tekst, komentarze warunkowe i
  rysunki VML usunięte, listy Worda zamienione na listy, łącza Safe Links rozwinięte. Obrazek ze schowka (np. zrzut
  ekranu) wkleja się jako obrazek. Uwagi do wklejenia pokazują się nad edytorem.
- **Obrazki są osadzane w treści** (`cid:`), nie jako załączniki: PNG, JPEG albo GIF sprawdzane po zawartości i
  oczyszczane z metadanych (EXIF z lokalizacją GPS), do 50 obrazków i 10 MB na obrazek, razem do 25 MB na wiadomość.
  Obrazki z internetu nigdy nie są pobierane – wklej obraz razem z treścią.

### Podpis

Lista *Podpis* to podpisy z Twojego Outlooka (pliki z `%APPDATA%\Microsoft\Signatures`), na początku *(bez podpisu)*;
*Odśwież* czyta je ponownie. Podpis jest dopisywany pod treścią, z obrazkami osadzonymi w wiadomości. Jego style są
wpisywane w tekst (poczta w przeglądarce, np. Gmail, pomija bloki `<style>`), a obrazki z internetu i elementy, których
poczta nie pokaże, są usuwane – okno wymienia takie problemy pod listą. Pól `{{…}}` w podpisie się nie wypełnia.

### Załączniki

*Dodaj…* albo przeciągnięcie plików na listę; *Usuń* i *Wyczyść*. **Załączniki idą z KAŻDĄ wiadomością wysyłki** i
zostają na liście do zamknięcia okna (lista nie jest zapamiętywana). Nagłówek pokazuje liczbę i rozmiar plików, np.
*Załączniki (2, 1,4 MB)*, i wyróżnia się, gdy lista nie jest pusta. Powyżej 15 MB załączników okno ostrzega, a powyżej
20 MB na wiadomość (obrazki + załączniki) ostrzega także podgląd. Pamiętaj: 2 MB × 300 wiadomości = 600 MB w *Elementach
wysłanych* – przy dużych plikach lepiej wysłać łącze.

### Wysyłka

- **Sposób wysyłki:** *osobna wiadomość do każdego* (zalecane – adresaci się nie widzą) albo *jedna wiadomość, odbiorcy
  w UDW* – w polu *Do* jest Twój adres, a odbiorcy w ukrytej kopii, najwyżej *Odbiorców w UDW na wiadomość* (1–500,
  domyślnie 500 – limit Exchange Online). Wiadomość w UDW jest jedna dla wszystkich, więc pola kolumn są w niej puste
  (`{{nazwa}}` daje zwrot grzecznościowy); taka wiadomość częściej trafia do spamu.
- **Odstęp między wiadomościami (s)** – 0–300, domyślnie 3 s (rozdział 7).
- **Poproś o potwierdzenie przeczytania.**
- **Dostarcz dopiero o wskazanej porze** – dzień i godzina (np. `8:00`, `08:00` albo `8`); co najmniej minutę od teraz
  i najwyżej rok naprzód. Outlook przekazuje wiadomości od razu, a doręczone zostaną o tej porze – AD Managera można
  zamknąć. Przy koncie Exchange lub Microsoft 365 wiadomość czeka na serwerze; przy kontach IMAP/POP czeka w *Skrzynce
  nadawczej*, więc Outlook musi być wtedy uruchomiony.
- **Przyciski głosowania (Outlook)** i *Odpowiedzi do wyboru, rozdzielone średnikiem* (domyślnie „Tak, nadal
  potrzebna;Nie, można usunąć”). Przyciski widzą odbiorcy w Outlooku i w Outlooku w przeglądarce w organizacji Exchange;
  inne programy pocztowe pokazują zwykłą wiadomość, a odbiorca po prostu odpowiada. Głosy odczytuje zakładka *Odpowiedzi*.

### Pola własne i grupowanie

- **Pola własne** – rozdział 4; *Dodaj pole*, *Usuń*. Pole z błędem (np. pusta nazwa) blokuje wysyłkę.
- **Jedna wiadomość na adres – wiersze jako {{lista}}, ich liczba jako {{liczba}}.** Wiersze z tym samym adresem dają
  jedną wiadomość. `{{lista}}` staje się listą punktowaną: jeden punkt na wiersz, według wzoru *Element listy {{lista}}*
  (np. `{{Skrzynka}} ({{Adres skrzynki}})`); `{{liczba}}` to liczba wierszy, a pozostałe pola pochodzą z pierwszego
  wiersza. Wpisz `{{lista}}` w osobnym akapicie – cały akapit zostanie zastąpiony listą. W temacie `{{lista}}` daje punkty
  rozdzielone przecinkami. Bez grupowania każda wiadomość ma listę z jednym punktem.

### Szablony

- **Szablon** – na początku listy wbudowany szablon *Czy skrzynka pocztowa jest nadal potrzebna?* (rozdział 8), dalej
  własne. Wybranie szablonu przy niepustej treści pyta, czy ją zastąpić.
- **Nazwa szablonu** + *Zapisz* zapisuje bieżącą wiadomość; *Usuń szablon*; *Nowa wiadomość* (po potwierdzeniu czyści
  temat, treść i pola własne).
- Szablon zawiera temat, treść z obrazkami, pola własne, element listy, zwrot grzecznościowy, przyciski głosowania i
  grupowanie – nie zawiera odbiorców, załączników ani nadawcy. Wbudowanego szablonu nie da się zmienić; *Zapisz* pod
  nową nazwą tworzy kopię.
- *Eksportuj…* / *Importuj…* – plik JSON z szablonami i ich obrazkami (do 20 MB) dla zespołu; import dodaje kopie i
  ponownie sprawdza treść oraz obrazki.
- **Ostatni szkic** (temat, treść, obrazki, pola własne, element listy, zwrot, głosowanie, grupowanie) zapisuje się przy
  zamknięciu okna i przed każdą wysyłką, a przy następnym otwarciu wraca do pustej wiadomości.

## 6. Krok 3 – Podgląd

Podgląd pokazuje wiadomość dokładnie tak, jak zobaczy ją wybrany odbiorca: kartę z polami *Od*, *Do*, *Temat* i
*Załączniki*, a pod nią treść ze scalonymi polami, obrazkami i podpisem.

- **Pokaż tak, jak zobaczy:** wybór odbiorcy (pierwszych 500 wiadomości). **Szerokość:** komputer (700 px), telefon
  (380 px) albo pełna – tak jak w skrzynce. Podgląd odświeża się przy wejściu na zakładkę i po każdej zmianie wyboru;
  *Odśwież* robi to na żądanie.
- Pod paskiem: liczba osadzonych obrazków i użyte pola, a potem **ostrzeżenia** – nieznane pola (z listą dostępnych),
  pola puste u części odbiorców, rzeczy, które psują się w poczcie (skrypty, obrazki linkowane z internetu albo zapisane
  jako `data:`, pozycjonowanie bezwzględne, obrazy tła, bloki `<style>`, elementy szersze niż 700 px, odsyłacz, którego
  tekst pokazuje inną domenę niż adres docelowy – typowy znak phishingu) i zbyt duża wiadomość. Bez ostrzeżeń widać
  *Wygląda dobrze…*.
- **Podgląd z Outlooka** buduje wiadomość w Outlooku (jak do wysyłki), odczytuje z niej gotową treść z obrazkami i kasuje
  wersję roboczą – trafia ona do *Elementów usuniętych*. To dowód „jeden do jednego”: widzisz HTML, który dostanie
  odbiorca. Outlook zmienia zwykle tylko zapis cudzysłowów i myślników na encje (`&#8222;`, `&#8212;`).
- **Otwórz w przeglądarce** – najwierniejszy podgląd silnikiem przeglądarki (Edge, Chrome), bliski temu, co zobaczy
  ktoś czytający pocztę w Gmailu, Interii czy WP. AD Manager podaje wiadomość **prosto z pamięci** pod adresem
  `http://127.0.0.1:<port>/<losowy token>/`, dostępnym tylko z tego komputera, przez 15 minut (najwyżej 20 podglądów
  naraz) – nic nie jest zapisywane na dysku.
- **Zapisz jako .html…** – wybierasz folder; powstaje `podglad_wiadomosci.html` z obrazkami obok, np. do akceptacji przez
  przełożonego. To eksport operatora: plik zawiera dane wybranego odbiorcy.

## 7. Krok 4 – Wysyłka

**Podsumowanie** (*Odśwież*): liczba wiadomości z pominiętymi i błędnymi adresami, nadawca, *w imieniu*, sposób
wysyłki, temat, podpis, załączniki, dostarczenie później, głosowanie i szacowany czas.

| Przycisk | Co robi |
|---|---|
| *Test do siebie* | jedna wiadomość na Twój adres (wybrane konto, a bez wyboru – bieżąca skrzynka Outlooka; gdy i jej nie ma, okno pyta o adres), z tematem „[TEST] …” i polami odbiorcy wybranego w podglądzie (albo pierwszego); nie trafia do dziennika |
| *Utwórz wersje robocze* | zapisuje wiadomości w *Wersjach roboczych* Outlooka – nic nie jest wysyłane ani wpisywane do dziennika; wysłane później ręcznie z Outlooka też nie trafią do dziennika |
| *Wyślij* | wysyłka po potwierdzeniu |
| *Zatrzymaj* | przerywa po bieżącej wiadomości |
| *Ponów nieudane* | powtarza wysyłkę tylko do odbiorców z błędem |
| *Zapisz raport…* | raport tekstowy (data, temat, wynik każdego odbiorcy) we wskazanym pliku |
| *Dziennik „już wysłane”…* | jak na zakładce *Odbiorcy* |

**Potwierdzenie** wymienia liczbę wiadomości, nadawcę, *w imieniu*, sposób wysyłki, załączniki, pominiętych i błędne
adresy oraz porę dostarczenia, ostrzega przy odstępie poniżej 2 s i ponad 50 wiadomościach i przypomina, że wysyłki nie
da się cofnąć. Powyżej 20 wiadomości trzeba wpisać ich liczbę.

**W trakcie** pasek pokazuje postęp i czas do końca (*Wysyłam 12 z 95 · zostało ok. 4 min*), liczniki *wysłane*,
*błędy* i *pominięte*, a lista *Do kogo poszło* – wynik każdej wiadomości; kolumna *Status* listy odbiorców pokazuje
*wysłano*, *wersja robocza*, *test wysłany* albo *błąd – …*. Okno przez cały czas odpowiada: rozmowa z Outlookiem idzie
osobnym wątkiem, więc *Zatrzymaj*, przewijanie i zakładki działają normalnie.

- **Każda wiadomość jest najpierw zapisywana, a dopiero potem wysyłana.** Gdy wysyłka się nie powiedzie, wiadomość
  zostaje w *Wersjach roboczych* (status mówi „zapisana w Wersjach roboczych”) – nic nie ginie, a raport podaje, na
  którym kroku był błąd. Gdy błąd wystąpi przed zapisem, niedokończona wiadomość jest porzucana.
- Adres, którego Outlook nie rozpozna, kończy wiadomość błędem zamiast wysyłki.
- **Po 5 błędach z rzędu wysyłka zatrzymuje się sama** – zwykle znaczy to, że Outlook został zamknięty albo padło
  połączenie; po naprawieniu użyj *Ponów nieudane*. Błędy, po których dalsza wysyłka nie ma sensu (Outlook zamknięty,
  odmowa zabezpieczenia Outlooka, różne poziomy uprawnień), zatrzymują ją od razu.
- Dziennik sesji AD Managera dostaje jeden wpis na wysyłkę („Korespondencja (Outlook)”, liczba udanych z wszystkich) –
  bez adresów i bez tematu.

### Limity Microsoft 365 i duże wysyłki (200–500 wiadomości)

- Exchange Online przyjmuje ok. **30 wiadomości na minutę**, **10 000 odbiorców na dobę** i **500 odbiorców w jednej
  wiadomości** (dotyczy trybu UDW; każdy adresat w UDW liczy się do dobowego limitu). Exchange w firmie może mieć inne
  limity – zapytaj administratora poczty.
- Zostaw odstęp 3 s – to ok. 20 wiadomości na minutę, poniżej limitu. Jedna wiadomość zajmuje Outlookowi ok. 1–3 s,
  więc 300 wiadomości to ok. 15–17 minut; pasek pokazuje czas do końca.
- Nie zamykaj Outlooka i nie usypiaj komputera w trakcie wysyłki.
- Załącznik dolicza się do **każdej** wiadomości (rozdział 5).
- **Lista odbiorców nie jest zapisywana** (inaczej niż w Korespondencji WPiA, rozdział 11). Po przerwaniu – także po
  zamknięciu okna – wczytaj ten sam plik (albo dodaj tych samych odbiorców z AD) ponownie: adresy, do których już
  wysłano, zostaną pominięte dzięki dziennikowi. Zachowaj więc plik źródłowy do końca wysyłki.

## 8. Skrzynki – zapytanie właścicieli skrzynek

W wielu firmach konto skrzynki współdzielonej (np. *Sekretariat IT*) ma w polach *Imię* i *Nazwisko* (`givenName`, `sn`)
dane osoby, do której skrzynkę przypisano. Zakładka *Skrzynki* czyta te dane, znajduje **własne konto tej osoby** i
dodaje ją do odbiorców z polami skrzynki i właściciela – a wbudowany szablon pyta, czy skrzynka jest nadal potrzebna.

### Gdzie i jakie skrzynki

- **Gdzie szukać skrzynek:** *Zaznaczone obiekty* (tylko po *Zapytaj właścicieli skrzynek…*), *Cała domena* albo
  *Jednostka organizacyjna* (*Wybierz jednostkę…*, razem z poddrzewem).
- **Rodzaj skrzynek** (według `msExchRecipientTypeDetails`): skrzynki współdzielone (domyślnie); współdzielone razem
  z salami i sprzętem; wszystkie skrzynki; wszystkie konta użytkowników w zakresie. Skrzynki zdalne środowiska
  hybrydowego (Exchange Online) liczą się do swojego rodzaju.
- **Dodatkowy filtr LDAP** – np. `(department=IT)`, łączony z filtrem skrzynek przez AND; składnia jest sprawdzana przed
  wyszukiwaniem.
- **Katalog bez schematu Exchange** (brak atrybutu `msExchRecipientTypeDetails`): okno mówi o tym i oferuje tylko
  wszystkie konta użytkowników w zakresie – zawęź je jednostką organizacyjną skrzynek albo dodatkowym filtrem, np.
  `(description=Skrzynka*)`.

### Skąd imię i nazwisko właściciela

**Imię i nazwisko właściciela odczytuj z:** `givenName` + `sn` skrzynki (domyślnie; jeden z atrybutów może zawierać oba
słowa), nazwy wyświetlanej, opisu albo wskazanego atrybutu (*Atrybut*; atrybuty poufne są odrzucane). Z tekstu usuwane
są tytuły („dr”, „prof.”, „mgr inż.” – ta sama tabela co w zwrotach grzecznościowych), uwagi w nawiasach i przedrostek
przed „:” albo „ - ” („Skrzynka: Jan Kowalski” → Jan Kowalski). Ostatnie słowo to nazwisko, wcześniejsze – imiona;
jedno słowo to samo nazwisko.

### Gdzie szukać osób

- **Szukaj właścicieli w:** cała domena (domyślnie) albo wybrana jednostka (*Wybierz jednostkę…*; *Cała domena*
  przywraca domenę). Osoby to konta użytkowników z imieniem, nazwiskiem lub nazwą wyświetlaną.
- AD Manager czyta osoby **jeden raz** (kilka atrybutów, stronami) i dopasowuje je w pamięci, bo kontroler domeny
  porównuje z polskimi znakami („Lukasz” ≠ „Łukasz”), a zapytanie o każde nazwisko z osobna by tego nie obeszło. Limity:
  20 000 skrzynek i 300 000 osób – powyżej okno prosi o zawężenie zakresu.
- **Właścicielem może być tylko włączone konto** (domyślnie): osoby z wyłączonym kontem zostają kandydatami, ale nigdy
  nie są wybierane automatycznie.
- Sama skrzynka i inne konta skrzynek (współdzielonych, sal, sprzętu) nigdy nie są kandydatami.

*Znajdź właścicieli* uruchamia wyszukiwanie (postęp: *Skrzynki: 120 · osoby: 4 210*), *Zatrzymaj* je przerywa.

### Jak AD Manager znajduje właściciela

Każdy kandydat dostaje poziom dopasowania – najlepszy, jaki do niego pasuje:

| Poziom | Przykład (skrzynka → osoba) | Punkty |
|---|---|---|
| dokładnie | Jan Kowalski → Jan Kowalski (bez względu na wielkość liter) | 50 |
| bez polskich znaków | Lukasz Wisniewski → Łukasz Wiśniewski | 45 |
| zamieniona kolejność | imię „Wójcik”, nazwisko „Małgorzata” → Małgorzata Wójcik | 35 |
| nazwa wyświetlana | Anna Nowak → osoba z nazwą „dr Anna Nowak”, „Nowak, Anna” albo „Anna Nowak (IT)” | 30 |
| częściowo | nazwisko dwuczłonowe (Anna Kowalska → Anna Kowalska-Nowak – to samo pierwsze imię), jedno z kilku imion, samo nazwisko | 15 |

Do punktów dochodzi: **pełnomocnictwo** +40 (skrzynka wskazuje osobę w `msExchDelegateListLink` albo `publicDelegates` –
to potwierdzenie, że osoba naprawdę korzysta ze skrzynki), konto włączone +10, osoba ma adres +5. Liczy się najlepszy
poziom, jaki wystąpił: jeden kandydat na nim to dopasowanie (dokładne albo bez polskich znaków) lub dopasowanie
prawdopodobne (pozostałe poziomy – do sprawdzenia); kilku kandydatów – przy *Właścicielem może być tylko włączone konto*
wygrywa jedyne włączone konto, a inaczej wynik jest niejednoznaczny. Jeden kandydat, który jest pełnomocnikiem
skrzynki, wygrywa zawsze (co najmniej na poziomie *częściowo*).

| Wynik | Znaczenie | Co zrobić |
|---|---|---|
| dopasowano | jedna osoba, dokładnie lub bez polskich znaków | nic |
| dopasowano przez pełnomocnictwo | jedyny kandydat-pełnomocnik | nic |
| prawdopodobne | jedna osoba na niższym poziomie | sprawdź, w razie potrzeby wybierz inną |
| niejednoznaczne | kilka osób na tym samym poziomie | wybierz kandydata z listy |
| nie znaleziono | nikt nie pasuje | wybierz osobę, wpisz adres albo wyklucz skrzynkę |
| brak imienia i nazwiska | skrzynka nie ma danych osoby | wybierz osobę albo zmień źródło imienia i nazwiska |
| właściciel wyłączony | wybrana osoba ma wyłączone konto | wybierz inną osobę albo wyklucz skrzynkę |
| właściciel bez adresu | osoba nie ma `mail` ani adresu SMTP | wpisz adres albo wybierz inną osobę |
| wybrano ręcznie, wykluczono | decyzja operatora | – |

### Przegląd i ręczny wybór

Kolumny listy: *Wyślij*, *Skrzynka*, *Adres skrzynki*, *Osoba wg skrzynki*, *Dopasowanie* (kolor: zielony – gotowe,
żółty – do sprawdzenia, czerwony – brak właściciela), *Właściciel* (lista kandydatów, np. „Jan Kowalski – IT,
jan.kowalski@…”, pełnomocnicy oznaczeni), *Adres właściciela*, *Dział*, *Działania*, *Lokalizacja* i *Uzasadnienie*
(poziom, pełnomocnictwo, wyłączone konto). Filtr nad listą pokazuje wszystkie wiersze, gotowe, do sprawdzenia,
nieznalezione albo wykluczone, a podsumowanie – np. *Dopasowane: 90 · do sprawdzenia: 12 · nieznalezione: 8 ·
wykluczone: 2*.

- Wybór innego kandydata z listy w kolumnie *Właściciel* zmienia wiersz na wybrany ręcznie.
- *Wybierz właściciela…* – dowolny użytkownik z okna wyboru obiektów.
- *Wpisz adres właściciela…* – adres wpisany ręcznie (sprawdzany).
- *Wyklucz* / *Przywróć* – skrzynka nie trafi do odbiorców.

### Pola skrzynek i właścicieli

Sekcja *Pola skrzynek i właścicieli* działa jak pola odbiorców z katalogu (rozdział 3), z dodatkowym wyborem: *pole
skrzynki* albo *pole właściciela*. Domyślnie:

| Pole | Wyrażenie | Czyje |
|---|---|---|
| Skrzynka | `{displayName}` | skrzynki |
| Adres skrzynki | `{mail}` | skrzynki |
| Opis skrzynki | `{description}` | skrzynki |
| Jednostka | `{$parentName}` | skrzynki |
| Ostatnie logowanie | `{$lastLogon:d MMMM yyyy}` | skrzynki |
| Utworzona | `{$created:d MMMM yyyy}` | skrzynki |
| Login skrzynki | `{sAMAccountName}` | skrzynki |
| Imię, Nazwisko, Nazwa wyświetlana, Stanowisko, Dział | `{givenName}`, `{sn}`, `{displayName}`, `{title}`, `{department}` | właściciela |
| Zwrot osobowy, Pan/Pani, Pana/Panią, Panu/Pani, Szanowny/a | `{$salutation}`, `{$honorific}`, `{$honorific:biernik}`, `{$honorific:celownik}`, `{$byGender:Szanowny/Szanowna}` | właściciela |

*Ostatnie logowanie* skrzynki to logowanie na **konto** skrzynki – konto skrzynki współdzielonej jest zwykle wyłączone,
a ludzie otwierają skrzynkę własnym kontem, więc pole bywa puste i nie mówi, czy skrzynka jest używana (aktywność
skrzynek zna tylko Exchange). Dlatego szablon wbudowany pisze w takim przypadku „brak danych”.

### Dodaj właścicieli do odbiorców

*Dodaj właścicieli do odbiorców* przenosi zaznaczone wiersze z adresem właściciela na zakładkę *Odbiorcy*: adresem
odbiorcy jest adres właściciela, nazwą – jego nazwa wyświetlana, polami – pola skrzynki i właściciela, źródłem –
„Skrzynki”. Wcześniejsze wiersze ze źródła „Skrzynki” są zastępowane. Przy włączonym *Jedna wiadomość do właściciela
(wszystkie jego skrzynki)* (domyślnie) włącza się grupowanie po adresie, więc osoba z trzema skrzynkami dostaje jedną
wiadomość z listą trzech skrzynek; bez niego każda skrzynka to osobna wiadomość. Gdy wiadomość jest pusta, wczytuje się
szablon wbudowany.

**Szablon wbudowany *Czy skrzynka pocztowa jest nadal potrzebna?*** (przyciski głosowania i grupowanie włączone):

> {{Zwrot osobowy}},
>
> w ewidencji poczty jako osobę, do której przypisano skrzynkę, wskazano {{Pana/Panią}}:
>
> {{lista}}
>
> Prosimy o informację, czy skrzynka jest nadal potrzebna. W Outlooku wystarczy kliknąć przycisk nad treścią tej
> wiadomości („Tak, nadal potrzebna” albo „Nie, można usunąć”); można też po prostu odpowiedzieć na tę wiadomość.
> Jeżeli do {{Termin}} nie otrzymamy odpowiedzi, skrzynka zostanie zablokowana.
>
> Z poważaniem

Element listy: `{{Skrzynka}} ({{Adres skrzynki}}) – ostatnie logowanie: {{Ostatnie logowanie|brak danych}}`. Przed
wysyłką **wpisz datę w polu własnym *Termin*** – podgląd ostrzega, dopóki jest puste. Szablon można zapisać pod inną
nazwą i zmienić.

## 9. Krok 5 – Odpowiedzi

Zakładka czyta głosy z *Elementów wysłanych* Outlooka – nic nie jest zapisywane na tym komputerze.

1. *Wysłane od* (domyślnie 90 dni wstecz) i *Szukaj w Elementach wysłanych* – lista wysyłek z okna *Korespondencja*, np.
   *28.10.2026 · Czy skrzynka pocztowa jest nadal potrzebna? · 95 wiad.*
2. *Wysyłka* i *Pokaż odpowiedzi* – tabela: *Odbiorca*, *Skrzynki* (których skrzynek dotyczy odpowiedź), *Odpowiedź*
   (tekst przycisku albo „brak odpowiedzi”) i *Kiedy*; podsumowanie, np. *Odpowiedzi: 40 z 95 · Tak: 30 · Nie: 10*.
3. *Eksport CSV…* – tabela do pliku (z ochroną przed formułami).

Jak to działa: każda wiadomość wysłana z okna *Korespondencja* niesie w Outlooku ukryte właściwości – identyfikator wysyłki i adresy
skrzynek, których dotyczy. Głosy zlicza Outlook nadawcy na wysłanym elemencie, gdy odpowiedzi trafią do jego *Skrzynki
odbiorczej* (domyślnie od razu po nadejściu). Przeszukiwane są *Elementy wysłane* domyślnej skrzynki profilu. Kto
odpisał zwykłą wiadomością zamiast kliknąć przycisk, ma „brak odpowiedzi” – sprawdź *Skrzynkę odbiorczą*. W trybie UDW
Twój własny adres z pola *Do* nie jest liczony.

## 10. Wiadomości do użytkowników (U1) przez Outlooka

Wiadomości do użytkowników z szablonów (*Wyślij wiadomość…*, U1) mają trzeci sposób wysyłki: *Ustawienia → Wiadomości
e-mail → Sposób wysyłki → Przez Outlooka* (obok *Bezpośrednio przez SMTP* i *Przez AdMng Watchdog*; *Automatycznie* nadal
wybiera Watchdoga albo SMTP, nigdy Outlooka). Sekcja *Outlook* ustawień:

- **Wysyłaj z konta** – adres SMTP konta z profilu Outlooka (puste – konto domyślne); *Pobierz konta z Outlooka*
  podpowiada konta profilu.
- **Wysyłaj w imieniu adresu nadawcy podanego wyżej** – adres nadawcy z ustawień trafia do pola *W imieniu* (konto
  potrzebuje uprawnienia *Wyślij w imieniu* do tej skrzynki).

Szablony, stopka, ograniczenia U1 (bez załączników, lista dozwolonych domen łączy), podgląd, test, raport i „Ponów
nieudane” działają bez zmian; okno wysyłki pokazuje *Wysyłka przez Outlooka: …* z kontem. Obowiązują wymagania z
rozdziału 1 (klasyczny Outlook, ten sam poziom uprawnień, zabezpieczenie Outlooka), a wynik w raporcie to *Wysłano przez
Outlooka* albo opis błędu z rozdziału 13.

## 11. Co jest zapisywane na dysku, a co nie

| Co | Gdzie | Zawartość |
|---|---|---|
| ustawienia okna | `%APPDATA%\AdMng\settings.json`, sekcja `correspondence` | temat, zwrot, konto, *w imieniu*, nazwa podpisu, odstęp, sposób wysyłki, potwierdzenie przeczytania, pomijanie wysłanych, grupowanie, głosowanie, godzina dostarczenia, szerokość podglądu, wzorzec pliku z *Pobranych* (`downloadsPattern`), próg potwierdzenia (`confirmationThreshold`, 20), okres przechowywania dziennika (`journalRetentionDays`, 365 dni); definicje pól z AD i pól skrzynek (nazwa + wyrażenie); opcje *Skrzynek* (rodzaj, filtr LDAP, źródło imienia i nazwiska, atrybut, miejsce osób jako DN przenośny `{domain}`) |
| szablony i ostatni szkic | `settings.json` i obrazki w `%APPDATA%\AdMng\message-images` (wspólne z szablonami U1) | treść operatora: temat, treść z polami, obrazki, pola własne, element listy, opcje |
| dziennik „już wysłane” | `%APPDATA%\AdMng\correspondence\journal.txt` | jeden wiersz na wysłany adres: **skrót adresu** (HMAC-SHA-256 z kluczem, pierwsze 16 bajtów), czas wysyłki, temat tak, jak go wpisano (bez wypełnionych pól) |
| klucz dziennika | `…\correspondence\journal.key` | 32 losowe bajty zaszyfrowane DPAPI dla bieżącego użytkownika Windows |
| blokada dziennika | `…\correspondence\journal.lock` | pusty plik – kilka okien i uruchomień AD Managera zapisuje dziennik po kolei |
| obrazki dla Outlooka | `%TEMP%\AdMng-Outlook-<proces>-<losowe>` | obrazki treści i podpisu (Outlook przyjmuje obrazki tylko z pliku); usuwane zaraz po zapisaniu wiadomości, pozostałości po awarii – przy następnym starcie |
| eksporty na żądanie | miejsce wskazane przez operatora | lista odbiorców (CSV), raport, podgląd `.html` z obrazkami, odpowiedzi (CSV), szablony (JSON) |

**Nie są zapisywane:** lista odbiorców i ich pola, wartości z AD, lista *Nie wysyłać*, załączniki, podgląd, wyniki
wysyłki (bez *Zapisz raport…*), wiersze i dopasowania *Skrzynek* oraz odpowiedzi. Dzienniki diagnostyczne nie zawierają
adresów, nazw, tematów ani treści; dziennik sesji – tylko wpis o wysyłce z liczbą wiadomości. Wiadomości same w sobie są
w skrzynce Outlooka (*Elementy wysłane*, *Wersje robocze*, a po *Podglądzie z Outlooka* – *Elementy usunięte*), tak jak
każda poczta wysłana z Outlooka.

**Dlaczego lista odbiorców nie zapisuje się sama.** Korespondencja WPiA co 25 wiadomości zapisywała `odbiorcy.csv` ze
statusami, żeby po przerwaniu dokończyć wysyłkę. AD Manager nie zapisuje na dysku danych katalogu (zasada 1): odbiorcy z
AD i ich pola to dane katalogu, a pliki z adresami to dane osobowe. Dokończenie zapewnia dziennik: wczytaj plik
ponownie, a adresy, do których już wysłano, zostaną pominięte. Sam dziennik nie zawiera adresów – nawet z kluczem da się
tylko sprawdzić, czy do **znanego** adresu już wysłano, a nie odczytać listę adresów. To jedyny wyjątek etapu U5 od
zasady „żadnych danych katalogu na dysku” ([ADR-0003](adr/0003-brak-lokalnych-danych.md)).

## 12. Różnice względem Korespondencji WPiA

- **Jedno okno w AD Managerze** zamiast osobnego programu – dla każdego okna połączenia. Ustawienia są w
  `settings.json` AD Managera; pliki `%APPDATA%\MailerWPiA` (`ustawienia.txt`, `tresc.xamlpkg`, `wyslane.csv`) nie są
  czytane. Dziennik `wyslane.csv` zawiera adresy jawnym tekstem i nie jest przenoszony – jeśli trzeba pominąć adresy
  wysłane starym programem, wczytaj go przez *Nie wysyłać → Z pliku…*.
- **Nowe:** odbiorcy z AD (zaznaczenie, okno wyboru, członkowie grupy) z polami z katalogu i zwrotami grzecznościowymi,
  pola własne, `{{Pole|tekst}}`, szablony z eksportem i importem, grupowanie po adresie (`{{lista}}`, `{{liczba}}`),
  przyciski głosowania i zakładka *Odpowiedzi*, zakładka *Skrzynki*, okres przechowywania dziennika, potwierdzenie
  wpisaniem liczby powyżej 20 wiadomości, konfigurowalna liczba odbiorców w UDW (1–500).
- **Dziennik** trzyma skróty adresów zamiast adresów; *Dziennik „już wysłane”…* pokazuje statystykę zamiast otwierać plik
  CSV z adresami. Lista odbiorców nie jest zapisywana co 25 wiadomości (rozdział 11).
- **Pliki:** XLSX czytany według adresów komórek (stary program przesuwał kolumny po pustej komórce), każdy arkusz z
  własnym nagłówkiem; tabele DOCX i ODT zachowują kolumny; z komórki z kilkoma adresami brany jest pierwszy poprawny;
  kolumny *Imię* + *Nazwisko* dają nazwę; ostrzejsze sprawdzanie adresów; limity rozmiaru i ochrona przed bombami zip.
- **Treść i podpis:** HTML treści powstaje z ograniczonego, bezpiecznego zestawu znaczników – skrypty i niebezpieczne
  odsyłacze nie przejdą; style podpisu są wpisywane w tekst, a obrazki osadzane przez `cid:`; obrazki sprawdzane i
  oczyszczane z metadanych, z limitami. Pola rozcięte formatowaniem są sklejane tylko w obrębie akapitu, a `&nbsp;` staje
  się spacją.
- **Wysyłka ostrożniejsza:** adres nierozpoznany przez Outlooka, nieudane ustawienie *w imieniu*, typu adresata (UDW)
  albo konta nadawcy kończą wiadomość błędem – stary program w takich przypadkach wysyłał dalej (np. z konta
  domyślnego). Wiadomość niedokończona przed zapisem jest porzucana.
- **Podgląd w przeglądarce** prosto z pamięci (bez pliku tymczasowego); *Podgląd z Outlooka* bez zmian.
- **Diagnostyka:** *Sprawdź Outlooka* zamiast `MailerWPiA.exe --diag`, z dodatkowymi informacjami o dzienniku, folderze
  *Pobrane* i folderze podpisów; czytelny komunikat przy różnych poziomach uprawnień. Nie ma parametrów `--selftest` i
  `--shot`.

## 13. Gdy coś nie działa

**Sprawdź Outlooka** (zakładka *Wiadomość*) pokazuje raport środowiska: użytkownika, komputer i bitowość procesu, wersję
Outlooka, profil, konta, bieżącą skrzynkę, podpisy, wynik próby utworzenia wiadomości (bez wysyłania), plik dziennika i
liczbę wpisów, folder *Pobrane* i folder podpisów. *Kopiuj* przenosi raport do zgłoszenia (zawiera adresy Twoich kont).

| Komunikat (kod) | Co zrobić |
|---|---|
| *Klasyczny Outlook działa tylko w systemie Windows.* (`outlook.notSupported`) | – (dotyczy tylko testów i kompilacji na Linuksie) |
| *Nie znaleziono klasycznego Outlooka…* (`outlook.notInstalled`) | zainstaluj klasycznego Outlooka (Microsoft 365 Apps); „nowy Outlook” i Outlook w przeglądarce nie mają automatyzacji |
| *AD Manager działa jako administrator, a Outlook nie…* (`outlook.elevated`) | uruchom AD Managera bez *Uruchom jako administrator* albo zamknij Outlooka przed połączeniem |
| *Outlook jest zajęty (otwarte okno dialogowe?)…* (`outlook.busy`) | zamknij okna dialogowe Outlooka (np. wybór profilu, hasło, przypomnienie) i spróbuj ponownie |
| *Outlook nie odpowiada…* (`outlook.notResponding`) | sprawdź, czy Outlook nie czeka na hasło albo wybór profilu; ustaw profil domyślny |
| *Outlook nie zgodził się na wysłanie wiadomości przez program…* (`outlook.denied`) | zezwól w oknie Outlooka; sprawdź program antywirusowy (aktualny wyłącza pytania) albo zasady grupy *Dostęp programowy* |
| *Połączenie z Outlookiem zostało przerwane…* (`outlook.disconnected`) | uruchom Outlooka, *Połącz z Outlookiem*, potem *Ponów nieudane* |
| *Outlook nie rozpoznał adresata.* (`outlook.recipient`) | popraw adres (literówka, nieistniejący adres w organizacji) |
| *Nie znaleziono pliku załącznika.* (`outlook.attachment`) | plik przeniesiono lub usunięto po dodaniu – dodaj go ponownie |
| *Wybranego konta nie ma w profilu Outlooka.* (`outlook.account`) | wybierz konto z listy *Wyślij jako* albo dodaj je do profilu |
| *Wiadomość jest za duża dla skrzynki lub serwera.* (`outlook.tooLarge`) | zmniejsz załączniki lub obrazki, wyślij łącze |
| *Przerwano.* (`outlook.cancelled`) | wysyłkę zatrzymano – *Ponów nieudane* dokończy |
| *Nieznany błąd Outlooka.* (`outlook.error`, z kodem `0x…`) | podaj kod i krok z raportu w zgłoszeniu; treść komunikatu Outlooka nie jest zapisywana, bo może zawierać adresy |

Inne sytuacje:

- *Klucza dziennika „już wysłane” nie udało się odczytać…* – inny użytkownik Windows albo przeniesiony profil: okno
  założyło nowy dziennik, a stary zachowało (`journal-<data>.old`). Pominięcia z poprzedniego dziennika nie działają –
  przed powtórną wysyłką użyj *Nie wysyłać* albo raportu.
- *Dziennika „już wysłane” nie można użyć…* – wysłane adresy nie będą pomijane; sprawdź uprawnienia do folderu
  `%APPDATA%\AdMng\correspondence`.
- *Wczytaj CSV z Pobranych* nic nie znajduje – plik musi pasować do wzorca `*kontakty*.csv`; folder *Pobrane* pokazuje
  *Sprawdź Outlooka*.
- Wiadomości czekają w *Skrzynce nadawczej* – Outlook pracuje w trybie offline albo czeka na wysłanie/odebranie.
- Podgląd pokazuje źródło HTML zamiast wiadomości – brak środowiska WebView2; użyj *Otwórz w przeglądarce*.
- Lista podpisów jest pusta – Outlook nie zapisał podpisów w `%APPDATA%\Microsoft\Signatures` (podpisy przechowywane
  tylko w chmurze nie są dostępne).

## 14. Lista kontrolna pilotażu

Nie da się tego sprawdzić w CI (bez Outlooka i Exchange). Do potwierdzenia na prawdziwym klasycznym Outlooku:

- [ ] Microsoft 365 Apps w wersji 32- i 64-bitowej; Exchange Online i Exchange w firmie.
- [ ] Dołączenie do uruchomionego Outlooka i uruchomienie Outlooka, gdy nie działa.
- [ ] Konta profilu i *Wyślij jako* z innego konta.
- [ ] *W imieniu* skrzynki współdzielonej (wiadomość, *Elementy wysłane*, odpowiedzi).
- [ ] Obrazki osadzone (treść i podpis) widoczne w treści w Outlooku, Outlooku w przeglądarce i Gmailu.
- [ ] Załączniki.
- [ ] Dostarczenie o wskazanej porze (Exchange i ewentualnie IMAP).
- [ ] Potwierdzenie przeczytania.
- [ ] Przyciski głosowania i odczyt głosów na zakładce *Odpowiedzi* (filtr daty wysyłki w Outlooku).
- [ ] Pytanie zabezpieczenia Outlooka na komputerze bez aktualnego antywirusa (zgoda i odmowa).
- [ ] AD Manager uruchomiony jako administrator przy zwykłym Outlooku – komunikat `outlook.elevated`.
- [ ] Tylko „nowy Outlook” – komunikat `outlook.notInstalled`.
- [ ] 300 wiadomości z odstępem 3 s: czas, okno odpowiada, *Zatrzymaj*, brak odrzuceń przez limity.
- [ ] Prawdziwe wartości `msExchRecipientTypeDetails` w środowisku hybrydowym (skrzynki zdalne) i pełnomocnictwa
  `msExchDelegateListLink`.
- [ ] Wiadomości do użytkowników (U1) przez Outlooka.
- [ ] Edytor: wklejanie z prawdziwego schowka Worda i Outlooka, motyw ciemny, zgodność treści po zapisie szablonu.
- [ ] Pliki z polskiego Excela (CSV w Windows-1250, XLSX), folder *Pobrane* przekierowany do OneDrive.
- [ ] *Otwórz w przeglądarce* w Edge i Chrome; *Zapisz jako .html* otwierany na innym komputerze.
- [ ] Dziennik przy dwóch oknach AD Managera wysyłających jednocześnie.
- [ ] Test „żadnych danych katalogu na dysku” po wysyłce i po zapytaniu właścicieli skrzynek (poza dziennikiem skrótów).
