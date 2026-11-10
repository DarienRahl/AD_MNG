[English](en/CREATING-ACCOUNTS.md) · **Polski**

# Zakładanie wielu kont w AD Managerze (faza 3)

Ten dokument opisuje kreator **Zakładanie wielu kont**: konta użytkowników – a także kontakty, grupy i komputery –
zakładane hurtem z arkusza wklejonego z Excela, LibreOffice Calc albo Arkuszy Google, z pliku CSV lub `.xlsx`, z nazwami
według reguł, na przykład kolejno numerowane `kd201`, `kd202`… z UPN `kd201@contoso.com`. Projekt techniczny:
[docs/dev/phase3-bulk-create.md](dev/phase3-bulk-create.md).

Zasady, które obowiązują w całym kreatorze:

- **Dane z arkusza tylko w pamięci.** Wklejona tabela, wyliczone wartości, wynik sprawdzenia, utworzone konta i ich
  hasła istnieją wyłącznie w oknie kreatora i znikają po jego zamknięciu. Plik powstaje tylko wtedy, gdy operator sam
  wybierze eksport. Profile nazewnictwa zapisują reguły i opcje – nigdy danych z arkusza ani haseł.
- **Każde konto powstaje przez plan zmian:** podgląd, odpowiednik PowerShell, potwierdzenie wpisaniem liczby obiektów,
  dziennik sesji, *Ponów nieudane* i *Cofnij* – kreator jest częścią edytora zbiorczego.
- **Jeden kontroler domeny na okno.** Wszystkie odczyty i zapisy idą do kontrolera okna. W lesie z kilkoma domenami
  kreator otwiera dodatkowo sesję tylko do odczytu do wykazu globalnego **tego samego** kontrolera – wyłącznie po to,
  żeby sprawdzić unikalność UPN i adresów e-mail w całym lesie.
- **Hasła tylko w pamięci.** Hasła początkowe nie trafiają do dziennika sesji, historii PowerShell, dzienników
  diagnostycznych ani profili. Do schowka trafiają tylko przez bezpieczne kopiowanie (czyszczone po minucie), do pliku –
  tylko w eksporcie, o który operator wyraźnie poprosi.

## 1. Do czego służy kreator

Typowy przypadek: dział kadr przysyła arkusz z nowymi pracownikami (telefon komórkowy, imię, nazwisko, dział, adres
e-mail). Operator zaznacza zakres w Excelu, kopiuje go, wkleja w kreatorze i dostaje konta w jednej jednostce
organizacyjnej – z nazwą logowania, UPN, adresem e-mail, nazwą wyświetlaną, hasłem początkowym, wymuszoną zmianą hasła i
członkostwem w grupach. Zanim cokolwiek zostanie zapisane, kreator pokazuje każdy wiersz z wyliczonymi wartościami i
uwagami: zajęte nazwy, możliwe istniejące konto tej samej osoby, za długie wartości, nieznaleziony menedżer…

Kreator zakłada cztery rodzaje obiektów (pole *Rodzaj obiektów*): **Użytkownicy**, **Kontakty**, **Grupy** i
**Komputery** – tym samym mechanizmem, z kolumnami i opcjami właściwymi dla rodzaju. Wszystkie obiekty jednego przebiegu
trafiają do **jednego** kontenera (*Kontener docelowy*).

## 2. Jak otworzyć kreator

- paleta poleceń (Ctrl+K): **Wiele kont…** (polecenie `new.bulkCreate`, grupa *Nowy*);
- *Akcja → Nowy → Wiele kont…*;
- menu kontekstowe jednostki organizacyjnej albo kontenera w drzewie: *Nowy → Wiele kont…* – obiekty powstaną w
  klikniętym kontenerze;
- menu kontekstowe listy obiektów: *Nowy → Wiele kont…*;
- przycisk **Wiele naraz…** w oknie *Nowy obiekt* (polecenia *Nowy użytkownik*, *Nowy kontakt*, *Nowa grupa*, *Nowy
  komputer*) – zamyka okno pojedynczego obiektu i otwiera kreator dla tego samego rodzaju i kontenera.

Kreator otwiera się w oknie edytora zbiorczego, np. *Zakładanie wielu kont – Nowi pracownicy* (dla innych rodzajów:
*Zakładanie wielu kontaktów*, *grup*, *komputerów*). Kontener jest wypełniony od razu: kontener, w którym kliknięto
*Nowy*, a z palety i menu *Akcja* – kontener bieżącej karty (gdy karta pokazuje domenę albo wyniki wyszukiwania – domyślny
kontener nowych obiektów domeny). Zmienia go przycisk *Przeglądaj...* obok pola *Kontener docelowy*. Polecenie jest
dostępne zawsze, gdy okno jest połączone; czy konto okna może tworzyć obiekty wybranego rodzaju w tym kontenerze,
kreator sprawdza sam w kroku *Sprawdzenie*.

Kreator ma cztery kroki (w pasku kroków edytora zbiorczego: *Dane i reguły*), po których następują zwykłe kroki edytora:

```
 Dane i reguły                                                       │ edytor zbiorczy
 1 Dane ─── 2 Kolumny ─── 3 Nazwy i opcje ─── 4 Sprawdzenie ─────────┼─ Podgląd ─── Wykonanie ─── Wyniki
```

Między krokami kreatora przechodzi się przyciskami *Dalej* i *Wstecz*; z kroku *Sprawdzenie* przycisk *Podgląd* buduje
plan zmian.

## 3. Krok 1 – Dane

**Wklejanie.** Zaznacz zakres w Excelu – z wierszem nagłówków albo bez – i naciśnij **Ctrl+V** w kreatorze albo kliknij
*Wklej*. Tak samo działa zakres z LibreOffice Calc i Arkuszy Google (tekst rozdzielany tabulatorami) oraz tabela z Worda
albo strony WWW (format HTML schowka – czytany tylko wtedy, gdy tekst nie wygląda na tabelę albo zawiera liczby w
zapisie naukowym). Kolejne wklejenie **dołącza** wiersze; *Wklej i zastąp* zastępuje całą tabelę. Gdy wszystkie (albo
ponad połowa) wklejanych wierszy są już w tabeli, kreator pyta: *Te wiersze są już wklejone … Dołączyć je mimo to,
zastąpić tabelę czy anulować?* (*Dołącz mimo to*, *Zastąp*, *Anuluj*). Przy dołączaniu kolumny są dopasowywane po
nagłówkach (nieznany nagłówek tworzy nową kolumnę), a w tabeli bez nagłówków – po kolejności.

**Plik.** *Wczytaj plik…* przyjmuje `.xlsx`, `.csv` i `.txt` do 50 MB i zastępuje wczytane dane (kreator najpierw
pyta). Z pliku `.xlsx` czytany jest pierwszy widoczny, niepusty arkusz – pozostałe niepuste arkusze i ukryte wiersze są
pomijane z ostrzeżeniem. Liczby przychodzą z formatem skoroszytu, więc telefon zapisany jako liczba nie jest skracany.
CSV i tekst: separator `;`, `,` albo tabulator (wykrywany z pierwszego wiersza), kodowanie UTF-16 (Excel: *Tekst
Unicode (\*.txt)*), UTF-8 z BOM albo bez, a w pozostałych przypadkach strona kodowa Windows (w polskim Windows – cp1250).
Pliki są czytane do pamięci, bez plików tymczasowych.

**Wpisywanie.** Komórki tabeli można poprawiać na miejscu. *Dodaj wiersz* w pustym kreatorze zaczyna tabelę z kolumnami
*Imię, Nazwisko, E-mail, Telefon komórkowy, Dział*, które następny krok przypisze sam. *Dodaj kolumnę* dodaje kolumnę o
nagłówku wpisanym w polu *Nagłówek nowej kolumny*; *Usuń zaznaczone wiersze* i *Wyczyść* usuwają dane.

**Nagłówki.** *Pierwszy wiersz zawiera nagłówki*: *Automatycznie* (domyślnie), *Tak* albo *Nie*. Automatycznie
nagłówkiem jest wiersz ze znanymi nazwami kolumn (rozdział 4), także pod tytułem arkusza (np. *Nowi pracownicy –
listopad 2026*) – wiersze nad nagłówkiem są pomijane z ostrzeżeniem, a *Wiersz nagłówków* pozwala wskazać inny wiersz
zakresu. Tabela bez nagłówków ma kolumny oznaczone literami (`A`, `B`, `C`…). *Pierwszy wiersz danych w Excelu*
(domyślnie 2 z nagłówkiem, 1 bez) służy tylko do wyświetlania: kolumna *Nr* w kroku *Sprawdzenie* i w wynikach pokazuje
numer wiersza arkusza, więc poprawki łatwo znaleźć w pliku źródłowym. Podsumowanie pod tabelą ma postać *Wiersze: 120,
kolumny: 7 – nagłówki rozpoznane*.

**Co kreator robi z komórkami.** Usuwa BOM i znaki niewidoczne (np. spacje zerowej szerokości), zamienia spacje
nierozdzielające na zwykłe, skleja powtórzone spacje i obcina spacje na początku i końcu. Puste kolumny i wiersze na
końcu zakresu znikają, puste wiersze w środku są pomijane. Komórki z tabulatorem albo podziałem wiersza (Alt+Enter) są
czytane tak, jak zapisał je Excel, a cudzysłowy w zwykłym tekście (np. komórka `"Projekt A" – zespół`) zostają
zwykłymi znakami. Ostrzeżenia podają numer wiersza i kolumny – nigdy wartość:

| Sytuacja | Co robi kreator |
|---|---|
| wartość błędu Excela (`#N/D!`, `#ARG!`, `#NAZWA?`, `#N/A`, `#VALUE!` …) | komórka zostaje pusta, ostrzeżenie |
| liczba w zapisie naukowym (`4,86E+11` – Excel skrócił 12-cyfrowy numer w kolumnie o formacie *Ogólne*) | ostrzeżenie; gdy schowek ma także format HTML z pełną wartością, kreator ją odczytuje (*pełną liczbę odczytano z formatu HTML schowka*) |
| scalone komórki (tabela HTML) | rozdzielone, dodatkowe komórki puste |
| powtórzony nagłówek | obie kolumny zostają, druga z dopiskiem *(2)* |
| wiersze dłuższe niż nagłówek | kolumny bez nagłówka na końcu tabeli |
| kolumna, która wygląda na hasła (*Hasło*, *Hasło startowe*, *password*, *PIN* …) | nie zostanie użyta: komórki są ukryte (`••••••••`), tylko do odczytu i kopiują się jako maska |
| ponad 10 000 wierszy, 256 kolumn albo 65 536 znaków w komórce | wczytane pierwsze 10 000 wierszy, nadmiarowe kolumny pominięte, komórka skrócona |

> Excel kopiuje także **ukryte kolumny**, ale **nie wiersze ukryte filtrem**. Przed kopiowaniem sprawdźcie, czy filtr
> arkusza nie ukrywa osób, które mają dostać konta.

*Dalej* jest dostępne, gdy wybrano kontener, a tabela ma co najmniej jeden wiersz.

## 4. Krok 2 – Kolumny

Każda kolumna arkusza ma tu wiersz: *Kolumna* (nagłówek albo litera), *Przykłady* (pierwsze wartości z arkusza),
*Przeznaczenie*, *Atrybut*, *Kolejność*, *Wielkość liter* i *Uwagi*. Przeznaczenia:

| Przeznaczenie | Znaczenie |
|---|---|
| *Pomiń* | kolumna nie jest używana |
| *Atrybut* | wartość trafia do wybranego atrybutu – dowolnego zapisywalnego atrybutu klasy według schematu, także `extensionAttribute1–15` i atrybutów własnych; lista pokazuje polską nazwę z nazwą LDAP, np. *Telefon komórkowy [mobile]* |
| *Tylko we wzorcach* | kolumna nie trafia do żadnego atrybutu, ale można jej użyć we wzorcu (`{$column:…}`, rozdział 5.2) – np. prywatny adres e-mail, na który operator prześle hasło |
| *Imię i nazwisko* | jedna kolumna dzielona na imię (`givenName`) i nazwisko (`sn`), kolejność *Imię Nazwisko* albo *Nazwisko Imię*; tytuły (*dr hab.*) są pomijane, a przecinek oznacza „nazwisko, imię” (*Kowalski, Jan*); użytkownicy i kontakty |
| *Menedżer (przełożony)* | `manager` (dla grup i komputerów `managedBy`), wskazany nazwą logowania, UPN, adresem e-mail, nazwą wyświetlaną albo DN |
| *Grupy* | grupy tego wiersza, rozdzielone średnikiem (także `\|` albo w osobnych liniach komórki), nazwą albo DN |
| *Kraj/region* | `c`, `co` i `countryCode` naraz – z polskiej albo angielskiej nazwy kraju lub z kodu (`PL`, `616`); użytkownicy i kontakty |
| *Konto wygasa* | data wygaśnięcia konta w tym wierszu (`dd.MM.rrrr`, `rrrr-MM-dd` albo data z pliku `.xlsx`) – koniec tego dnia; tylko użytkownicy |

Dwie kolumny nie mogą mieć tego samego przeznaczenia. Lista atrybutów nie oferuje atrybutów, których kreator nie
zapisuje: systemowych i tylko do odczytu, binarnych i poufnych, `userAccountControl`, `member`, atrybutów z DN (poza
menedżerem), `proxyAddresses` (tylko przez opcję z rozdziału 5.4), `c` / `co` / `countryCode` (tylko przez *Kraj/region*)
oraz atrybutów Exchange i Skype for Business.

**Rozpoznawanie kolumn.** Przy wczytaniu tabeli (i po *Rozpoznaj ponownie*) kreator przypisuje kolumny sam: najpierw
według przypisań zapamiętanych w profilu (rozdział 5.1), potem według polskich i angielskich nagłówków – bez względu na
wielkość liter, polskie znaki i interpunkcję (*Tel. kom.*, *Adres e-mail*) – nazw LDAP i polskich nazw atrybutów, a w
końcu według zawartości. Nagłówek musi pasować w całości: *Komórka organizacyjna* to dział, a nie telefon. Najczęstsze
nagłówki:

| Przypisanie | Przykładowe nagłówki |
|---|---|
| `givenName` | Imię, Imiona, First name |
| `sn` | Nazwisko, Last name, Surname |
| *Imię i nazwisko* | Imię i nazwisko, Pracownik, Osoba, Full name; *Nazwisko i imię* – nazwisko pierwsze |
| `sAMAccountName` | Login, Nazwa użytkownika, Nazwa logowania, Konto, Username |
| `userPrincipalName` | UPN, Login UPN |
| `mail` | E-mail, Mail, Adres e-mail, Adres mailowy, Poczta, E-mail służbowy |
| *Tylko we wzorcach* | E-mail prywatny, Mail prywatny, Adres prywatny, Private e-mail |
| `mobile` | Telefon komórkowy, Komórkowy, Tel. kom., Nr komórki, GSM, Mobile |
| `telephoneNumber` | Telefon, Tel., Nr telefonu, Telefon służbowy, Wewnętrzny, Phone |
| `title` | Stanowisko, Funkcja, Job title |
| `department` | Dział, Departament, Wydział, Komórka organizacyjna |
| `company` | Firma, Spółka, Pracodawca |
| `physicalDeliveryOfficeName` | Biuro, Pokój, Siedziba, Miejsce pracy |
| `employeeID` / `employeeNumber` | ID pracownika / Numer pracownika, Nr ewidencyjny, Nr kadrowy |
| *Menedżer (przełożony)* | Menedżer, Przełożony, Kierownik, Szef, Manager |
| *Konto wygasa* | Umowa do, Data końca umowy, Konto wygasa, Wygasa |
| *Kraj/region* | Kraj, Państwo |
| `streetAddress` / `l` / `postalCode` | Ulica, Adres / Miasto, Miejscowość / Kod pocztowy |
| *Grupy* | Grupy, Grupa, Członkostwo |

Zawartość też ma znaczenie: kolumna *Telefon*, w której co najmniej 80 % wartości to polskie numery komórkowe, trafia do
`mobile` (uwaga *rozpoznano numery komórkowe*); *Komórka* to telefon komórkowy tylko wtedy, gdy zawiera numery, a w
przeciwnym razie dział. W tabeli bez nagłówków kolumny z adresami e-mail i numerami telefonów są rozpoznawane po
zawartości (*rozpoznano po zawartości*). Kolumny z hasłami nigdy nie są przypisywane (*prawdopodobnie hasła – kolumna nie
zostanie użyta*), a identyfikatory osobowe (PESEL, NIP, numer dowodu, data urodzenia, IBAN) – tylko ręcznie (*identyfikator
osobowy – pominięto (przypisuj go tylko wtedy, gdy to konieczne)*). Druga kolumna o tym samym przeznaczeniu dostaje
*Pomiń* z uwagą *inna kolumna ma już to przeznaczenie – pominięto*.

**Wielkość liter** (przeznaczenia *Atrybut* i *Imię i nazwisko*): *Bez zmian*, *Jak W Nazwie* (każde słowo i część
nazwiska dwuczłonowego wielką literą: *NOWAK-KOWALSKA* → *Nowak-Kowalska*), *małe litery*, *WIELKIE LITERY*. Poza tym
wyborem i formatem telefonów kreator nie zmienia wartości z arkusza; imię lub nazwisko zapisane wielkimi literami daje
ostrzeżenie z podpowiedzią *Jak W Nazwie*.

**Telefony.** Gdy kolumna telefonów zawiera numery bez plusa (`48600100200`), uwaga brzmi *numery bez +48 – format
międzynarodowy je uzupełni*, a przycisk *Format międzynarodowy* włącza opcję *Numery telefonów* z sekcji *Zaawansowane*
(rozdział 5.7) dla wszystkich telefonów.

## 5. Krok 3 – Nazwy i opcje

Krok ma na górze *Profil nazewnictwa*, a pod nim sekcje **Nazwy**, **Konto**, **Grupy** i zwiniętą sekcję
**Zaawansowane** z jednowierszowym podsumowaniem.

### 5.1 Profil nazewnictwa

Profile wbudowane (nie da się ich nadpisać ani usunąć – zmienione zapisuje się pod nową nazwą):

| Rodzaj | Profil | Reguły |
|---|---|---|
| Użytkownicy | *Standardowy* | `cn` i `displayName`: `{givenName} {sn}`; nazwa logowania: `{givenName\|sam\|lower}.{sn\|sam\|lower}` (*Łucja Żak* → `lucja.zak`); prefiks UPN: `{sAMAccountName}`; licznik: *Najniższy wolny numer (uzupełnia luki)* |
| Użytkownicy | *Konta numerowane (kd201…)* | nazwa logowania: `kd{$nr:201}`; prefiks UPN: `{sAMAccountName}`; `mail`: `{sAMAccountName}@{$mailDomain}`; `cn` i `displayName`: `{givenName} {sn}` (w listach AD widać imię i nazwisko, nie numer); licznik: *Kolejny po najwyższym istniejącym*, bez numerów z Kosza AD |
| Kontakty | *Standardowy* | `cn` i `displayName`: `{givenName} {sn}` |
| Grupy, Komputery | *Standardowy* | `cn` z kolumny (np. *Nazwa grupy*, *Nazwa komputera*); nazwa logowania: `{cn}` (komputer dostaje `$` na końcu) |

**Który profil jest wybrany.** Po otwarciu kreatora: profil domyślny kontenera (*Domyślny dla tego kontenera*), w
przeciwnym razie ostatnio używany profil tego rodzaju, a w końcu *Standardowy*. Zmienione reguły lub opcje oznacza
*(zmieniony)*; przy zmianie profilu i w polu *Rodzaj obiektów* kreator pyta, czy odrzucić zmiany.

**Zapisywanie.** *Zapisz* zapisuje zmiany zapisanego profilu. Profil wbudowany zapisuje się przez *Zapisz jako…*
(nazwa podpowiadana z dopiskiem *(kopia)*): panel ma pole *Nazwa profilu*, opcję *Domyślny dla tego kontenera* i
informację, które przypisania kolumn profil zapamięta (*Profil zapamięta też przypisanie kolumn: …*). *Domyślny dla tego
kontenera* przy zapisanym profilu włącza go albo wyłącza jako domyślny; *Usuń* usuwa zapisany profil (utworzone obiekty
się nie zmieniają). Można zapisać do 50 profili; profile są wspólne dla wszystkich okien programu.

**Co zawiera profil:** reguły (atrybut, wzorzec, *Reguła zastępuje kolumnę*), wybrany sufiks UPN i domenę adresów
e-mail, opcje kont i opcje zaawansowane, grupy (z nazwą domeny – w oknie innej domeny są pomijane z ostrzeżeniem
*Grupy z profilu … wybrano w domenie … i nie zostaną tu użyte*), przypisania kolumn według nagłówków i kontenery, dla
których profil jest domyślny. Nagłówek jest zapamiętywany tylko wtedy, gdy nie wygląda na dane (bez `@`, mniej niż 7
cyfr) ani na hasło. Profil **nie zawiera** żadnej wartości z arkusza, wyliczonej nazwy, numeru licznika ani hasła.
Użytkownik lub grupa, która może dołączać komputery (rozdział 5.5), obowiązuje tylko w bieżącym przebiegu.

### 5.2 Reguły i wzorce

Sekcja *Nazwy* ma listę reguł z kolumnami *Atrybut*, *Wzorzec* i *Przykład*. Stałe wiersze: użytkownicy – `cn`,
`displayName`, `sAMAccountName`, `userPrincipalName`, `mail`, `description`; kontakty – `cn`, `displayName`, `mail`,
`description`; grupy – `cn`, `sAMAccountName`, `mail`, `description`; komputery – `cn`, `sAMAccountName`, `description`.
*Dodaj regułę* dodaje regułę dla dowolnego atrybutu klasy, *Usuń regułę* ją usuwa. Pusty wzorzec opisuje podpowiedź
*bez reguły – wartość z kolumny „…”* albo *bez reguły – pole pozostanie puste*. Gdy `cn` (albo nazwa logowania – poza
kontaktami) nie ma ani reguły, ani kolumny, krok zgłasza błąd.

**Skąd pochodzi wartość.** Dla każdego wiersza i atrybutu: z komórki poprawionej w kroku *Sprawdzenie*, w przeciwnym
razie z niepustej komórki przypisanej kolumny, a dopiero potem z reguły. Gdy atrybut ma i kolumnę, i regułę, przy
regule widać *Kolumna „E-mail” ma pierwszeństwo – reguła działa tylko dla pustych komórek*; opcja *Reguła zastępuje
kolumnę* odwraca kolejność. Wzorzec bez pól to stała wartość (np. `description` = `Konto tymczasowe`).

**Składnia.** Wzorzec to tekst z polami w nawiasach klamrowych – ta sama składnia co w szablonach edytora zbiorczego i
wiadomości do użytkowników:

| Pole | Wartość |
|---|---|
| `{givenName}`, `{sn}`, `{sAMAccountName}`, `{mail}` … | wyliczona wartość atrybutu w tym wierszu (z kolumny, poprawki albo reguły); pola mają nazwy LDAP |
| `{$column:Dział}`, `{$column:C}` | komórka kolumny o tym nagłówku albo literze |
| `{$nr:201}`, `{$nr:1:4}`, `{$nr:200:0:10}` | licznik (rozdział 5.3) |
| `{$suffix}` | wybrany sufiks UPN |
| `{$mailDomain}` | *Domena adresów e-mail* |
| `{$domain}` | nazwa DNS domeny okna |
| `{$parentName}` | nazwa kontenera docelowego |
| `{$date:yyyy-MM-dd}`, `{$date:dd.MM.yyyy}` | dzisiejsza data |

Po nazwie pola można dopisać funkcje oddzielone znakiem `|`, np. `{givenName|nodiacritics|lower}`:

| Funkcja | Działanie |
|---|---|
| `lower` / `upper` | małe litery / WIELKIE LITERY |
| `title` / `capitalize` | Każde Słowo Wielką Literą / Pierwsza litera wielka |
| `trim` | bez spacji na początku i końcu |
| `nodiacritics` | bez polskich znaków (ą→a, ł→l) |
| `nospaces` / `alnum` | bez spacji / tylko litery i cyfry |
| `sam` | jak nazwa logowania (bez polskich i niedozwolonych znaków), maks. 20 znaków w każdym polu |
| `first:N` / `last:N` | pierwsze N znaków / ostatnie N znaków |
| `replace:a:b` | zamień „a” na „b” |
| `default:tekst` | „tekst”, gdy pole jest puste |

*Wstaw pole* wstawia w miejscu kursora pola opisane polskimi nazwami atrybutów, kolumny, pola specjalne (*Kolejny
numer*, *Sufiks UPN*, *Domena adresów e-mail*, *Domena (nazwa DNS)*, *Nazwa kontenera*, *Dzisiejsza data (RRRR-MM-DD)*,
*Dzisiejsza data (DD.MM.RRRR)*) i funkcje z opisem. Kolumna przypisana do atrybutu jest wstawiana jako ten atrybut (np.
`{department}`), więc uwzględnia poprawki z kroku *Sprawdzenie*; `{$column:…}` jest oferowane dla kolumn *Tylko we
wzorcach* i pominiętych. Kolumny wyglądające na hasła są niedostępne także we wzorcach.

Zasady pisania: `{{` i `}}` dają nawias klamrowy w tekście; wewnątrz pola `\` poprzedza znak `|`, `{` albo `}`
(`{$column:Dział\|Zespół}`). Pola `{$dn}`, `{$rdn}` i `{$counter}` edytora zbiorczego nie działają przy zakładaniu
obiektów (licznik to `{$nr}`). W adresach (UPN, `mail`) zamiast `sam` używajcie `nodiacritics|nospaces|lower` – `sam`
skraca każde pole do 20 znaków, a adresów kreator nie skraca. Format daty z nazwami miesięcy lub dni (`MMMM`, `ddd`) daje
nazwy angielskie – kreator ostrzega i podpowiada format liczbowy.

**Przykłady** (sufiks UPN i domena adresów `contoso.com`, w domenie nie ma jeszcze kont `kd…`, data 2026-11-10):

| Reguła | Wzorzec | Wiersz | Wynik |
|---|---|---|---|
| nazwa logowania | `kd{$nr:201}` | wiersze 1–3 | kd201, kd202, kd203 |
| prefiks UPN | `{sAMAccountName}` | wiersz 1 | kd201@contoso.com |
| `mail` | `{sAMAccountName}@{$mailDomain}` | wiersz 1 | kd201@contoso.com |
| prefiks UPN (*UPN = adres e-mail*) | `{mail}` | `mail` = lucja.zak@contoso.com | lucja.zak@contoso.com |
| nazwa logowania | `{givenName\|sam\|lower}.{sn\|sam\|lower}` | Łucja Żak | lucja.zak |
| nazwa logowania | `{givenName\|first:1\|sam\|lower}{sn\|sam\|lower}` | Anna Konstantynopolitańczykowa | akonstantynopolitanc (skrócone do 20 znaków) |
| `mail` | `{givenName\|nodiacritics\|nospaces\|lower}.{sn\|nodiacritics\|nospaces\|lower}@{$mailDomain}` | Anna Konstantynopolitańczykowa | anna.konstantynopolitanczykowa@contoso.com (adres nie jest skracany) |
| `cn` | `{givenName} {sn}` | Łucja Żak dwa razy w arkuszu | Łucja Żak, Łucja Żak (2) |
| nazwa logowania | `u{$nr:1:4}` | wiersze 1–2 | u0001, u0002 |
| `employeeNumber` | `{$nr:200:0:10}` | wiersze 1–3 | 200, 210, 220 |
| `homeDirectory` | `\\fs01\home$\{sAMAccountName}` | kd201 | `\\fs01\home$\kd201` (folder nie jest tworzony) |
| `description` | `Konto utworzone {$date:yyyy-MM-dd} – {$column:Umowa}` | *Umowa* = zlecenie | Konto utworzone 2026-11-10 – zlecenie |
| `info` | `{{tymczasowe}} {$column:C}` | C = praktyki | {tymczasowe} praktyki |

**Przykład na żywo.** Kolumna *Przykład* pokazuje wynik dla pierwszych trzech wierszy (i *najdłuższy: …*, gdy dalej jest
dłuższa wartość), z pierwszym wolnym numerem licznika; *(zmieniono znaki)* i *(skrócono)* oznaczają wartości poprawione
przez opcje z sekcji *Zaawansowane*. Błąd wzorca podaje pozycję w tekście i pole, czasem z podpowiedzią (*Czy chodziło o
{givenName}?*). Przykład nie uwzględnia poprawek z kroku *Sprawdzenie* ani zajętych nazw – to robi dopiero krok 4.

### 5.3 Numeracja

Gdy któraś reguła używa licznika `{$nr…}`, nad regułami jest panel **Numeracja**: *Prefiks*, *Pierwszy numer*, *Liczba
cyfr (zera z przodu)* i *Krok*. Wszystkie pola `{$nr…}` reguł korzystają z jednego licznika, więc panel zmienia je
naraz. Składnia `{$nr:początek[:cyfry[:krok]]}`: początek 0–999 999 999 (domyślnie 1), liczba cyfr 0–9 (dopełnienie
zerami z przodu; 0 – bez dopełniania), krok 1–1 000 000 (domyślnie 1). `kd{$nr:201}` daje kd201, kd202…,
`kd{$nr:1:4}` – kd0001, kd0002…

**Pierwszy wolny numer.** Po wejściu w krok i po każdej zmianie licznika panel pyta katalog i pokazuje np. *Pierwszy
wolny numer: kd208 (najwyższy istniejący: kd207)*; bez połączenia – *Pierwszy numer: kd201 (bez sprawdzenia w AD)*.

**Jak numer jest przydzielany** (*Zaawansowane → Licznik*):

- *Kolejny po najwyższym istniejącym* (profil *Konta numerowane*): numeracja zaczyna się za najwyższym numerem, który
  nazwa logowania z tym prefiksem ma już **w domenie** – licząc także konta usunięte, które są w Koszu AD, gdy zaznaczono
  *Nie używaj ponownie numerów z Kosza AD* (domyślnie). Przykład: istnieją kd201, kd203 i kd205, a kd207 jest w Koszu AD
  → kd208, kd209… Numer odchodzącego pracownika nie jest wydawany ponownie.
- *Najniższy wolny numer (uzupełnia luki)*: od wartości *Pierwszy numer* w górę, z pominięciem zajętych (w tym samym
  przykładzie: kd202, kd204, kd206, kd208).

W obu trybach numer musi być wolny dla **każdego** atrybutu, który z niego korzysta: nazwy logowania w domenie, UPN i
adresu e-mail w całym lesie (adres – także wśród `proxyAddresses` innych obiektów; zakres sprawdzenia – rozdział 6) oraz
wolny od numerów zajętych przez wcześniejsze wiersze arkusza. Numery rosną w kolejności wierszy. Przydzielony numer
zostaje przy wierszu: poprawka albo wykluczenie innego wiersza nie przenumerowuje pozostałych, a *Numeruj od nowa* w
kroku *Sprawdzenie* przydziela numery od nowa. Wartość z licznikiem nigdy nie jest skracana ani uzupełniana numerem: gdy
prefiks z numerem przekracza limit (np. 20 znaków nazwy logowania), wiersz ma błąd.

**Szybka poprawka.** Gdy reguła identyfikatora jest stałym tekstem zakończonym cyframi (np. `kd201` – wszystkie konta
dostałyby tę samą nazwę), kreator proponuje *Numerować kolejno od 201? → kd{$nr:201}* z przyciskiem *Numeruj*.

### 5.4 UPN, adres e-mail, Microsoft 365 i Exchange

Reguła `userPrincipalName` to **prefiks**: kreator dopisuje `@` i *Sufiks UPN* wybrany z listy (nazwy DNS domen lasu i
dodatkowe sufiksy UPN). Domyślny sufiks to sufiks zapisany w profilu, jeśli las go zna (inaczej ostrzeżenie *Sufiksu
UPN z profilu (…) nie ma w tym lesie – użyto …*), a w pozostałych przypadkach domena okna – jak w ADUC. Gdy wzorzec
prefiksu daje już cały adres (np. `{mail}`), zamiast sufiksu widać *z wzorca*. Przycisk *UPN = adres e-mail* ustawia
regułę `{mail}`.

*Domena adresów e-mail* (`{$mailDomain}`) jest domyślnie równa sufiksowi UPN i zmienia się razem z nim, dopóki operator
nie wybierze innej. Domena kończąca się na `.local`, `.lan` albo `.internal` (a w kroku *Sprawdzenie* także
`.localdomain` i `.corp`) nie odbiera poczty z internetu – kreator ostrzega. Adres z arkusza w domenie, która nie jest
domeną lasu ani sufiksem UPN, dostaje ostrzeżenie *(adres prywatny?)* – taką kolumnę lepiej przypisać jako *Tylko we
wzorcach*.

**Microsoft 365 / Exchange.** Środowiska się różnią, więc oba ustawienia są opcjami profilu i domyślnie są wyłączone:

- *Dodaj główny adres SMTP do proxyAddresses (Microsoft 365 / Exchange)* (sekcja *Zaawansowane*) – nowe konto dostaje
  `proxyAddresses` = `SMTP:` + `mail`;
- *UPN = adres e-mail* – gdy w Waszym środowisku nazwa logowania UPN ma być równa adresowi e-mail.

Kreator **nie zakłada skrzynek pocztowych**: atrybutów Exchange (`mailNickname`, `targetAddress`, `legacyExchangeDN`,
`homeMDB`, `msExch…`) i Skype for Business (`msRTCSIP-…`) nie da się ustawić ani kolumną, ani regułą – zapisane
bezpośrednio dałyby niepełnych adresatów. Skrzynki zakłada się narzędziami Exchange albo Microsoft 365.

### 5.5 Konto

**Użytkownicy:**

- *Hasło początkowe*: *Generuj osobne hasło dla każdego konta* (domyślnie; *Długość hasła* od 12 do 256 znaków,
  domyślnie 16, obok *minimum domeny: …*), *Jedno hasło dla wszystkich kont* (pola *Hasło* i *Potwierdź hasło*) albo
  *Bez hasła – konto wyłączone*. Wygenerowane hasła są losowe, różne dla każdego konta i nie zawierają nazwy logowania
  ani części imienia i nazwiska.
- *Użytkownik musi zmienić hasło przy następnym logowaniu* – domyślnie zaznaczone.
- *Konto włączone* – domyślnie zaznaczone; bez hasła niedostępne (AD nie przyjmuje włączonego konta bez hasła).
- *Konto wygasa: koniec dnia* z datą – dla wszystkich kont; kolumna *Konto wygasa* ma pierwszeństwo w swoim wierszu.

Hasło musi spełniać zasady domeny: krótsza długość generowanych haseł niż minimum domeny to błąd kroku (*Domena wymaga
dłuższych haseł …*), a wspólne hasło za krótkie albo niezłożone blokuje plan przy jego budowaniu. Wspólne hasło
zawierające nazwę logowania albo część nazwy wyświetlanej daje ostrzeżenie (domena może je odrzucić), a przy kontach
włączonych – uwagę *Wszyscy będą znać hasło każdego konta do pierwszej zmiany hasła.* Pola wspólnego hasła są czyszczone
po zbudowaniu każdego planu – po *Wstecz* trzeba je wpisać ponownie. Gdy wybrana grupa ma własne zasady haseł (PSO),
kreator ostrzega: hasło początkowe sprawdzają zasady domeny, a zasady grupy obowiązują dopiero przy zmianie hasła.

Hasła ustawia się **tylko przez szyfrowane połączenie** (Kerberos z szyfrowaniem, LDAPS albo StartTLS). Bez niego krok
*Sprawdzenie* zgłasza problem dotyczący wszystkich wierszy (*Hasła można ustawić tylko przez szyfrowane połączenie …*) –
przy kontach użytkowników z hasłem i przy każdym zakładaniu komputerów.

**Kontakty** nie mają konta ani hasła.

**Grupy:** *Zakres grupy* (*Lokalna domeny*, *Globalna* – domyślnie, *Uniwersalna*) i *Grupa zabezpieczeń (w przeciwnym
razie dystrybucyjna)*.

**Komputery:** *Użytkownik lub grupa, która może dołączyć komputery do domeny* (*Przeglądaj...*; bez wyboru – *(nie
wybrano – Administratorzy domeny)*). Konto komputera powstaje wyłączone, dostaje losowe hasło o długości 120 znaków,
którego nikt nie widzi (nie ma go w wynikach, schowku ani eksporcie), i dopiero wtedy jest włączane. Gdy dołączać ma
ktoś inny, a konto okna nie jest jednym z właścicieli, którym ufa utwardzenie KB5020276, kreator ostrzega jak okno
*Nowy komputer* (zob. [KOMPUTERY.md](KOMPUTERY.md#przygotowanie-konta-nowy--komputer)). Nazwa komputera ma od 1 do 15
liter, cyfr i kresek (nie same cyfry, bez kreski na początku i końcu) i jest zapisywana wielkimi literami; kropka to
błąd, a `_` – ostrzeżenie (znak niedozwolony w nazwach DNS).

### 5.6 Grupy

*Dodaj grupy…* wybiera grupy, do których trafi każdy utworzony obiekt (*Każdy utworzony obiekt zostanie dodany do tych
grup.*); kolumna *Grupy* dodaje grupy pojedynczym wierszom. Grupa podstawowa konta (*Domain Users*, dla komputerów
*Domain Computers*) jest pomijana. Grupa nieznaleziona albo niejednoznaczna daje ostrzeżenie; nazwy rozdzielone
przecinkami kreator próbuje rozdzielić i prosi o średniki. Zagnieżdżenie tworzonej grupy niedozwolone przez zakres grup
oraz grupa, do której konto okna nie może dodawać członków, dają ostrzeżenie (to członkostwo zostanie pominięte albo się
nie powiedzie).

### 5.7 Zaawansowane

| Opcja | Domyślnie | Działanie |
|---|---|---|
| *Licznik* | według profilu | rozdział 5.3 |
| *Nie używaj ponownie numerów z Kosza AD* | zaznaczone | numer konta z Kosza AD liczy się jako zajęty; bez zaznaczenia może zostać użyty ponownie (z ostrzeżeniem) |
| *Gdy nazwa jest zajęta* | *Dopisz numer* | *Dopisz numer*: zajęte `jan.kowalski` → `jan.kowalski2` (UPN i adres z reguły `{sAMAccountName}…` idą za nim), *Jan Kowalski* zajęty w kontenerze → *Jan Kowalski (2)*; *Oznacz jako błąd* – wiersz z błędem. Komputery – zawsze błąd. Wartości z kolumn, poprawki i wartości z licznikiem nigdy nie są zmieniane – ich kolizja to błąd wiersza |
| *Poprawiaj niedozwolone znaki w identyfikatorach (ą→a, spacje)* | zaznaczone | dotyczy wartości z reguł: nazwy logowania, prefiksu UPN i części adresu przed `@` |
| *Skracaj za długie nazwy (limit znaków: nazwa logowania 20, cn 64)* | zaznaczone | UPN i adresy e-mail nie są skracane nigdy – za długie to błąd |
| *Dodaj główny adres SMTP do proxyAddresses (Microsoft 365 / Exchange)* | wyłączone | rozdział 5.4 |
| *Numery telefonów* | *Bez zmian* | *Format międzynarodowy (+48 600 100 200)*: usuwa spacje, kreski, kropki i nawiasy, `00` zamienia na `+`, numer 9-cyfrowy i `48` z 9 cyframi dostają `+48`; komórkowe w grupach 3-3-3, stacjonarne z dwucyfrowym numerem kierunkowym; numeru, którego nie da się tak zapisać, nie zmienia i ostrzega |
| *Szukaj istniejących kont tej osoby* | zaznaczone | rozdział 6 |

Unikalność: nazwa logowania – w domenie (razem z nazwami komputerów i grup); UPN i adres e-mail – w całym lesie; nazwa
obiektu (`cn`) – w kontenerze docelowym, bez względu na typ obiektu (grupa *Księgowość* koliduje z jednostką
*Księgowość*). Nazwa zajęta tylko przez obiekt w Koszu AD nie blokuje wiersza, ale daje ostrzeżenie: przywrócenie tamtego
obiektu będzie wymagało zmiany nazwy.

## 6. Krok 4 – Sprawdzenie

Po wejściu w krok kreator wylicza wszystkie wiersze (*Sprawdzanie wierszy: 40 z 120*, przycisk *Zatrzymaj*). Czyta
katalog przez kontroler okna – zajęte nazwy, numery licznika, menedżerów, grupy, możliwe istniejące konta, zasady haseł
domeny, uprawnienia do kontenera – i niczego nie zapisuje.

**Tabela.** Kolumny: *Utwórz* (pole wyboru), *Stan*, *Nr* (wiersz arkusza), imię i nazwisko (dla grup i komputerów –
nazwa) przypięte z lewej, *Uwagi*, potem wyliczone atrybuty (`cn`, nazwa logowania, UPN, adres e-mail, imię, nazwisko i
każdy atrybut, który ma wartość w którymś wierszu) oraz *Menedżer* i *Grupy*. *Kliknij dwukrotnie komórkę, aby ją
poprawić (Enter zapisuje); odznacz wiersz, aby go pominąć.* Podsumowanie: *Wiersze: 120 – OK: 108, z ostrzeżeniami: 6,
z błędami: 4, wykluczone: 2*; pod nim zakres sprawdzenia: *Unikalność UPN i adresów: cały las (jedna domena)*, *cały las
(wykaz globalny na DC01)* albo *sprawdzone tylko w domenie*.

| Stan | Znaczenie |
|---|---|
| *OK* | wiersz zostanie utworzony |
| *Ostrzeżenie* | wiersz zostanie utworzony; przeczytajcie uwagi |
| *Błąd* | wiersz nie zostanie utworzony, dopóki go nie poprawicie (albo nie odznaczycie) |
| *Wykluczony* | wiersz odznaczony |
| *Utworzony* | obiekt już utworzony w tym oknie |

**Co blokuje.**

- **Błąd wiersza** blokuje tylko ten wiersz: wartość zajęta w AD albo przez inny wiersz arkusza, której nie da się
  zmienić (*… ta wartość jest już używana przez „…”*, *… ta sama wartość jest w wierszu …*); nazwa logowania kolidująca
  z nazwą logowania innego konta w postaci `nazwa@domena` (Kerberos wskazałby inne konto); brak wymaganego atrybutu
  (`cn`, nazwa logowania); niedozwolone znaki, za długa wartość albo podział wiersza w atrybucie jednowierszowym
  (Alt+Enter w Excelu); licznik za długi albo wyczerpany; nieobsługiwana data w kolumnie *Konto wygasa*; `$` na końcu
  nazwy użytkownika; nieprawidłowa nazwa komputera.
- **Problemy dotyczące wszystkich wierszy** (nad tabelą) blokują cały przebieg: kontener nie istnieje; *Nie możesz
  utworzyć obiektu tego typu w tym kontenerze.*; błąd we wzorcu; włączone konta bez hasła; hasła bez szyfrowanego
  połączenia; generowane hasła krótsze niż minimum domeny. Wspólne hasło za krótkie albo niezłożone kreator zgłasza przy
  budowaniu planu – podgląd pokazuje wtedy każdy obiekt z błędem.
- **Ostrzeżenia** nie blokują: wartość zmieniona, bo była zajęta (*dopisano numer*); obiekt o tej nazwie w Koszu AD;
  menedżer nieznaleziony, niejednoznaczny albo zakładany w tej samej partii (konto powstanie bez menedżera – przy kilku
  pasujących kontach można wybrać jedno w komórce: *Pasuje kilka kont – wybierz menedżera*); grupa nieznaleziona; adres
  albo sufiks UPN spoza lasu; imię wielkimi literami; nieznany kraj; telefon w zapisie naukowym albo niedający się
  zapisać w formacie międzynarodowym; mało wolnych identyfikatorów RID na kontrolerze; unikalność sprawdzona tylko w
  domenie.

**Możliwe istniejące konta.** Licznik nigdy nie koliduje, więc arkusz wklejony drugi raz dałby tym samym osobom nowe
konta (kd221… obok kd201…). Dlatego przy zakładaniu użytkowników kreator szuka wśród użytkowników i kontaktów domeny tej
samej osoby: według pierwszej wartości z arkusza (albo poprawionej) z listy `employeeID`, `employeeNumber`, adres e-mail,
`mobile`, a bez nich – imienia i nazwiska razem. Trafienie to ostrzeżenie *Możliwe istniejące konto tej osoby: „…” (ten
sam atrybut …)*, a dwa wiersze tej samej osoby – *Wiersz … opisuje tę samą osobę …*; oba pokazuje filtr *Możliwe
duplikaty*. Wyłącza to opcja *Szukaj istniejących kont tej osoby*.

**Poprawianie.** Poprawiona komórka ma źródło *edytowano* i ma pierwszeństwo przed kolumną i regułą; po chwili wiersze
są liczone ponownie, a tabela zostaje w tym samym miejscu (przewinięcie, zaznaczenie i edytowana komórka się nie
zmieniają). Poprawka nazwy logowania zmienia też UPN i adres z reguł, które jej używają. Podpowiedź komórki podaje
źródło wartości (*Źródło: z kolumny*, *ze wzorca*, *edytowano* …) i to, co kreator zmienił (*Zmieniono, aby wartość
była unikalna.*, *Usunięto lub zamieniono niedozwolone znaki.*, *Skrócono do dopuszczalnej długości.*); menu komórki ma
*Przywróć wartość wyliczoną*. *Menedżer* i *Grupy* też można poprawiać.

**Narzędzia.** *Filtr*: *Wszystkie*, *OK*, *Ostrzeżenia*, *Błędy*, *Możliwe duplikaty*, *Wykluczone*, *Utworzone*.
*Następny błąd* (F8). *Kopiuj wiersze z błędami* – komórki arkusza z wierszy z błędami razem z uwagami, gotowe do
wklejenia do Excela i odesłania do poprawy. *Sprawdź ponownie* – czyta katalog od nowa (nazwy zajęte w międzyczasie,
menedżerów, grupy). *Numeruj od nowa* – zapomina pokazane numery i przydziela je ponownie.

Po powrocie do wcześniejszego kroku i zmianie danych, reguł albo kontenera wiersze są liczone ponownie przy następnym
wejściu w krok; poprawki komórek, wykluczenia i przydzielone numery zostają. Tracą się tylko po zmianie nagłówków,
*Wklej i zastąp*, *Wyczyść*, wczytaniu nowego pliku albo zmianie rodzaju obiektów.

*Podgląd* jest dostępny, gdy nie ma problemów dotyczących wszystkich wierszy, a co najmniej jeden wiersz ma stan *OK*
albo *Ostrzeżenie*.

## 7. Podgląd planu zmian i wykonanie

*Podgląd* liczy wiersze jeszcze raz – z wartościami z kroku *Sprawdzenie* zamrożonymi: nic nie jest przenumerowywane
ani zmieniane, a wartość, którą ktoś zajął od sprawdzenia, staje się błędem wiersza (widać go w podglądzie i po
*Wstecz* w kroku *Sprawdzenie*). Potem kreator buduje plan zmian, np. *Utworzenie kont (3) w jednostce „Nowi
pracownicy”*; wspólne hasło jest odczytywane właśnie teraz, a jego pola – czyszczone.

**Co zawiera plan.** Kolejność kroków jak w ADUC:

| Rodzaj | Kroki jednego obiektu |
|---|---|
| użytkownik | utworzenie konta (wyłączonego) z wartościami → hasło początkowe → włączenie konta i wymuszenie zmiany hasła → menedżer → członkostwo w każdej grupie |
| kontakt | utworzenie → menedżer → grupy |
| grupa | utworzenie z zakresem i typem → `managedBy` → grupy |
| komputer | utworzenie (wyłączonego) → prawo dołączenia do domeny → losowe hasło → włączenie → `managedBy` → grupy |

Hasło odrzucone przez domenę zostawia więc konto wyłączone i bez grup, a nieudane dodanie do grupy – działające konto
bez tej grupy; oba przypadki widać w wynikach i można je ponowić. Podgląd to zwykły podgląd edytora zbiorczego: obiekty z
krokami (wartość „przed” jest pusta), filtry, pola wyboru, *Eksportuj podgląd* (nigdy z hasłami) i *Kopiuj jako
PowerShell* – `New-ADUser … -AccountPassword (Read-Host -AsSecureString -Prompt '…')`, `Set-ADUser -Manager …`,
`Add-ADGroupMember …`; hasła w skrypcie nie ma.

**Wykonaj.** Okno potwierdzenia podaje tytuł zmiany, liczbę obiektów do utworzenia i domenę, przypomina, że *Hasła
początkowe będą dostępne tylko w tym oknie*, i wymaga wpisania liczby obiektów. Wykonanie działa w tle (centrum
aktywności) z postępem i przyciskiem *Zatrzymaj*.

**Najpierw jedno konto.** Kreator tworzy najpierw jeden obiekt; dopiero gdy wszystkie jego kroki się udadzą, wykonuje
resztę (w centrum aktywności i dzienniku sesji są wtedy dwa wpisy). Błąd systematyczny – brak szyfrowania, brak prawa
do resetu hasła albo zapisu `userAccountControl`, hasło odrzucone przez zasady – zatrzymuje wykonanie po jednym koncie:
*Wstrzymano po pierwszym koncie – pozostałe wiersze nie zostały wykonane. Usuń przyczynę podaną poniżej i użyj Ponów
nieudane.* Jeśli pierwszy obiekt nie powstał, bo jego wartość jest zajęta (problem tylko tego wiersza), próbę przejmuje
następny – najwyżej trzy.

## 8. Wyniki i hasła

Nad zwykłym raportem edytora zbiorczego (filtr, podsumowanie, stan i komunikat każdego obiektu, *Eksportuj wyniki*,
*Ponów nieudane*, *Cofnij*) jest lista **Utworzone obiekty**: *Nr*, *Nazwa*, *Nazwa logowania*, *UPN*, *Adres e-mail*,
*Hasło początkowe* (zamaskowane, z przyciskami *Pokaż* i *Kopiuj*), *Przekazane*, *Stan* (*Utworzono*, *Utworzono, nie
wszystkie kroki się udały*, *Usunięte (cofnięte)*) i *Komunikat*. Lista obejmuje wszystkie wykonania okna; kontakty i
grupy są na niej bez hasła, komputerów nie ma. *Hasła początkowe istnieją tylko w tym oknie: przekaż je (Kopiuj, Kopiuj
tabelę z hasłami lub Eksportuj), zanim je zamkniesz.*

Jak przekazać hasła:

- *Kopiuj* przy koncie – hasło trafia do schowka bezpieczną ścieżką (*Skopiowano hasło – schowek zostanie wyczyszczony po
  minucie.*). *Pokaż* odsłania hasło – to też liczy się jako przekazanie.
- *Kopiuj tabelę z hasłami…* – po ostrzeżeniu (*Tabela zawiera hasła początkowe jawnym tekstem. Wklej ją tylko tam,
  gdzie hasła pozostaną chronione (np. w zaszyfrowanej wiadomości) …*) kopiuje tabelę dla Excela: *Nr*, nazwę, nazwę
  logowania, UPN, adres e-mail, wybrane kolumny arkusza i hasło. *Kolumny arkusza w kopii:* domyślnie zaznacza telefony i
  kolumny *Tylko we wzorcach* – zwykle numer komórkowy i prywatny adres, przez które przekazuje się hasła; kolumn
  wyglądających na hasła nie oferuje. Komórki zaczynające się od `=`, `+`, `-` albo `@` są chronione przed formułami.
  Schowek jest czyszczony po minucie.
- *Kopiuj bez haseł* – ta sama tabela bez haseł (np. do zgłoszenia); hasła nie są wtedy oznaczane jako przekazane.
- *Eksportuj wyniki* – raport do pliku wskazanego przez operatora. Hasła trafiają do niego tylko po potwierdzeniu
  *Dołączyć hasła?* (*Wyniki zawierają nowe hasła. Czy zapisać je w pliku jawnym tekstem?*), inaczej są zamaskowane. Plik
  z hasłami chrońcie jak same hasła i usuńcie po przekazaniu.

Zamknięcie okna, gdy wygenerowane hasło nie zostało skopiowane, pokazane ani wyeksportowane, wymaga potwierdzenia
(*Wygenerowane hasła nie zostały wyeksportowane ani skopiowane …*). Wspólne hasło operator zna, więc przy nim okno o to
nie pyta.

Uwagi nad listą: kontroler, na którym utworzono konta, i czy jest emulatorem PDC – pozostałe kontrolery zobaczą konta po
replikacji, do tego czasu logowanie przez inny kontroler może się nie udać; *Konto bez hasła pozostaje wyłączone.* po
nieudanym ustawieniu hasła; *UPN może być już używany w innej domenie lasu.*, gdy unikalność sprawdzono tylko w domenie, a
kontroler odrzucił utworzenie.

## 9. Ponawianie i cofanie

**Ponów nieudane** planuje tylko kroki, które nie udały się w żadnym wykonaniu: konto nigdy nie powstaje dwa razy, a
konto, które dostało hasło, nie dostaje drugiego. Nieudane ustawienie hasła dostaje nowe wygenerowane hasło. Gdy domena
odrzuciła wspólne hasło, wyniki pokazują *Nowe hasło dla kont bez hasła* (*Nowe hasło*, *Potwierdź hasło*) – nowe hasło
otrzymają wszystkie konta, które jeszcze go nie mają. Obiekt, którego wartość zajęto od sprawdzenia, nie jest ponawiany
(*Wartość została zajęta od sprawdzenia – obiekt nie zostanie ponowiony; wróć do Sprawdzenia.*). Po zatrzymaniu po
pierwszym koncie ponowienie znów zaczyna od jednego obiektu. Można też wrócić (*Wstecz*) do kroku *Sprawdzenie*,
poprawić wiersze i zbudować nowy plan – utworzone wiersze mają stan *Utworzony* i nie wchodzą do planu, a wyniki
wszystkich wykonań są na jednej liście.

**Cofnij** usuwa **wszystko, co utworzyło to okno** – ze wszystkich wykonań, ponowień i wklejonych tabel. Podgląd
cofnięcia (np. *Cofnięcie: Utworzenie kont (3) w jednostce „Nowi pracownicy”*) mówi: *Cofnięcie usunie obiekty (3)
utworzone w tym oknie – razem z ich hasłami i członkostwem w grupach; przy włączonym Koszu AD można je z niego
przywrócić.* Przed zbudowaniem planu każdy obiekt jest odczytywany ponownie po `objectGUID`: obiekt przeniesiony albo o
zmienionej nazwie jest usuwany tam, gdzie jest teraz, a obiekt, którego już nie ma albo którego nazwę ma inny obiekt,
jest pomijany (*Utworzonego obiektu już nie ma albo jego nazwę ma inny obiekt – cofnięcie go pominie.*). Ostrzeżenia:
konto było używane od utworzenia (logowanie albo zmiana hasła), obiekt ma obiekty podrzędne (jego usunięcie się nie
powiedzie). Cofnięcie potwierdza się wpisaniem liczby obiektów.

Po cofnięciu konta mają stan *Usunięte (cofnięte)* i nie mają już haseł, wiersze wracają do stanu sprzed utworzenia, a
ich nazwy i numery są w tym oknie znowu wolne – ponowne wykonanie daje te same numery. W innym oknie (albo później)
usunięte konta, które są w Koszu AD, nadal zajmują swoje numery i nazwy (licznik je pomija, nazwy dają ostrzeżenie).
Cofnięcie działa tylko w otwartym oknie kreatora – po jego zamknięciu program nie pamięta, co utworzono.

## 10. Przykład: konta kd201, kd202… z arkusza kadr

Domena `contoso.test` ma dodatkowy sufiks UPN `contoso.com`; nowe konta mają trafić do jednostki *Nowi pracownicy* (pod
*Pracownicy*). W domenie nie ma jeszcze kont `kd…`. Dział kadr przysłał arkusz:

| | A | B | C | D | E |
|---|---|---|---|---|---|
| 1 | Telefon komórkowy | Imię | Nazwisko | Dział | E-mail |
| 2 | +48 600 100 201 | Łucja | Żak | Księgowość | lucja.zak@contoso.com |
| 3 | +48 600 100 202 | Bartłomiej | Śliwiński | Kadry | bartlomiej.sliwinski@contoso.com |
| 4 | +48 600 100 203 | Zofia | Kamińska | IT | zofia.kaminska@contoso.com |

1. W drzewie kliknij prawym przyciskiem *Nowi pracownicy* → *Nowy → Wiele kont…*. Okno *Zakładanie wielu kont – Nowi
   pracownicy* ma *Rodzaj obiektów*: *Użytkownicy* i kontener *Nowi pracownicy*.
2. **Dane:** w Excelu zaznacz `A1:E4`, Ctrl+C, w kreatorze Ctrl+V. Podsumowanie: *Wiersze: 3, kolumny: 5 – nagłówki
   rozpoznane*. *Dalej*.
3. **Kolumny:** kreator przypisał sam *Telefon komórkowy* → `mobile`, *Imię* → `givenName`, *Nazwisko* → `sn`, *Dział* →
   `department`, *E-mail* → `mail`. *Dalej*.
4. **Nazwy i opcje:** *Profil nazewnictwa* → *Konta numerowane (kd201…)*, *Sufiks UPN* → `contoso.com` (*Domena adresów
   e-mail* zmienia się razem z nim). Panel *Numeracja*: prefiks `kd`, pierwszy numer 201, *Pierwszy wolny numer: kd201*.
   Przy regule `mail` widać *Kolumna „E-mail” ma pierwszeństwo – reguła działa tylko dla pustych komórek* – adresy z
   arkusza wygrywają. *Konto*: *Generuj osobne hasło dla każdego konta*, *Użytkownik musi zmienić hasło przy następnym
   logowaniu* i *Konto włączone* zostają zaznaczone. *Grupy → Dodaj grupy…* → grupa nowych pracowników, np. `GG_Nowi`.
   *Dalej*.
5. **Sprawdzenie** (wybrane kolumny):

   | *Nr* | Imię | Nazwisko | `cn` | `sAMAccountName` | `userPrincipalName` | `mail` | *Stan* |
   |---|---|---|---|---|---|---|---|
   | 2 | Łucja | Żak | Łucja Żak | kd201 | kd201@contoso.com | lucja.zak@contoso.com | OK |
   | 3 | Bartłomiej | Śliwiński | Bartłomiej Śliwiński | kd202 | kd202@contoso.com | bartlomiej.sliwinski@contoso.com | OK |
   | 4 | Zofia | Kamińska | Zofia Kamińska | kd203 | kd203@contoso.com | zofia.kaminska@contoso.com | OK |

   *Wiersze: 3 – OK: 3, z ostrzeżeniami: 0, z błędami: 0, wykluczone: 0*, *Unikalność UPN i adresów: cały las (jedna
   domena)*.
6. *Podgląd* → plan *Utworzenie kont (3) w jednostce „Nowi pracownicy”* → *Wykonaj*, wpisz `3`. Najpierw powstaje kd201,
   potem kd202 i kd203.
7. **Wyniki:** trzy konta z zamaskowanymi hasłami. *Kopiuj tabelę z hasłami…* (kolumna *Telefon komórkowy* zaznaczona
   w *Kolumny arkusza w kopii:*) → wklej tabelę tam, gdzie hasła pozostaną chronione, i przekaż je pracownikom.
8. Na koniec *Wstecz* do kroku *Nazwy i opcje* i *Zapisz jako…*, np. „Nowi pracownicy – kd”, z zaznaczonym *Domyślny
   dla tego kontenera*. Następny kreator otwarty na *Nowi pracownicy* wybierze ten profil sam, przypisze kolumny według
   zapamiętanych nagłówków i zacznie od *Pierwszy wolny numer: kd204 (najwyższy istniejący: kd203)*.

Warianty:

- **W domenie są już konta kd…:** jeśli istnieją kd201, kd203 i kd205, a kd207 jest w Koszu AD, numeracja zacznie od
  kd208.
- **Arkusz ma prywatne adresy:** przypisz kolumnę *E-mail* jako *Tylko we wzorcach* – `mail` utworzy wtedy reguła profilu
  (`kd201@contoso.com`), a prywatny adres będzie domyślnie w *Kopiuj tabelę z hasłami…*, żeby wysłać na niego hasło.
- **Adres służbowy ma być loginem:** *UPN = adres e-mail* – UPN = `lucja.zak@contoso.com`, a nazwa logowania (sprzed
  Windows 2000) zostaje `kd201`.

## 11. Dane i prywatność

- Wklejona tabela (razem z pominiętymi wierszami tytułu), wyliczone wartości, wynik sprawdzenia, odczytane z katalogu
  nazwy, menedżerowie i grupy, plany, wyniki, identyfikatory utworzonych obiektów i hasła istnieją **tylko w pamięci
  okna** i znikają po jego zamknięciu. Kreator nie zapisuje stanu – zamknięte okno trzeba zacząć od nowa.
- Pliki `.xlsx` i CSV są czytane do pamięci, bez plików tymczasowych. Dzienniki diagnostyczne zawierają tylko liczby i
  czasy (np. liczbę wierszy i zapytań), nigdy wartości; odczyt schowka zapisuje w dzienniku najwyżej kod błędu.
- Na dysk trafiają tylko **profile nazewnictwa** w ustawieniach programu: nazwy profili, wzorce, opcje, wybrany sufiks
  UPN i domena adresów (nazwy DNS), grupy i kontenery jako DN z nazwą domeny oraz nagłówki kolumn (bez adresów, numerów
  i haseł). Nigdy wartości z arkusza, wyliczone nazwy, numery licznika ani hasła.
- Pliki powstają tylko na polecenie operatora: *Eksportuj podgląd* (bez haseł) i *Eksportuj wyniki* (hasła tylko po
  potwierdzeniu). *Kopiuj jako PowerShell* trafia do schowka.
- Hasła: wygenerowane istnieją tylko w planie i wynikach okna; wspólne jest odczytywane z pól w chwili budowania planu i
  od razu czyszczone. Dziennik sesji zapisuje ustawienie hasła bez wartości, odpowiednik PowerShell pyta o hasło
  (`Read-Host -AsSecureString`). Kopiowanie haseł idzie przez bezpieczny schowek (czyszczony po minucie). Hasła kont
  komputerów nie są nigdzie pokazywane.
- Wiersze i wyniki nie trafiają do usługi AdMng Watchdog (Watchdog widzi tylko zmiany w katalogu, jak każde inne).

## 12. Ograniczenia i pilotaż

Ograniczenia tej wersji:

- **Jeden kontener na przebieg.** Obiekty z jednego arkusza trafiają do jednego kontenera; kontenera z kolumny nie ma.
- **Excel:** kopiuje ukryte kolumny, ale nie wiersze ukryte filtrem. **Zer wiodących, które Excel usunął z liczb**
  (`00123` wpisane w kolumnie *Ogólne* to `123`), nie da się wykryć – takie kolumny formatujcie jako tekst przed
  wpisaniem danych. Liczby w zapisie naukowym kreator tylko wykrywa; pełną wartość odzyskuje z formatu HTML schowka albo
  z pliku `.xlsx`. Plików `.ods` nie czyta (z Calca – wklejanie, CSV albo `.xlsx`).
- **Limity:** 10 000 wierszy, 256 kolumn, 65 536 znaków w komórce, pliki do 50 MB, 50 zapisanych profili.
- **Hasła z arkusza nie są obsługiwane** – kolumny wyglądające na hasła są ukryte i pomijane. Kreator nie wysyła też
  haseł menedżerom ani pracownikom – przekazuje je operator (rozdział 8).
- **Nie w tej wersji:** skrzynki pocztowe, foldery macierzyste i profile (tylko atrybuty `homeDirectory`, `homeDrive`,
  `profilePath`), *Hasło nigdy nie wygasa* i inne flagi konta przy tworzeniu (ustawia je potem edytor zbiorczy), menedżer
  zakładany w tym samym arkuszu (ustawcie go potem edytorem zbiorczym), członkowie tworzonych grup z kolumny, kopiowanie
  konta wzorcowego (*Kopiuj…* z ADUC), atrybuty z DN poza menedżerem, obiekty `inetOrgPerson`, udostępnianie profili
  innym operatorom, zakładanie według harmonogramu.
- **Licznik:** prefiks krótszy niż 2 znaki jest sprawdzany numer po numerze i w trybie *Kolejny po najwyższym
  istniejącym* liczy od wartości *Pierwszy numer* (jak przy uzupełnianiu luk). Numer zawsze zależy od tego, co widzi
  kontroler okna: konto założone w tej samej chwili na innym kontrolerze i jeszcze niezreplikowane nie zostanie wykryte –
  AD sam nie rozwiązuje zduplikowanych nazw logowania.
- **Kosz AD:** usunięte obiekty widzi tylko konto, które może czytać kontener `CN=Deleted Objects` domeny; kreator
  otwarty innym kontem ich nie znajdzie (bez komunikatu) – nie ostrzeże o nazwach z Kosza AD i nie pominie ich numerów.
- **Las z wieloma domenami:** UPN i adresy są sprawdzane w całym lesie tylko wtedy, gdy kontroler okna jest gotowym
  wykazem globalnym; w przeciwnym razie *sprawdzone tylko w domenie* – kontrolery od Windows Server 2012 R2 i tak
  odrzucą zduplikowany UPN przy zapisie, chyba że wyłączono to w `dSHeuristics` (wynik: *UPN może być już używany w
  innej domenie lasu.*). Zasady haseł grup (PSO) może zwykle czytać tylko administrator domeny – bez tego wspólne hasło
  jest sprawdzane tylko z zasadami domeny.
- **Przykład w kroku 3** liczy wartości lokalnie (bez poprawek z kroku 4 i bez zajętych nazw). Numer 10-cyfrowy bez
  numeru kraju nie jest zmieniany przez *Format międzynarodowy*. `description` ma zawsze jedną wartość (`|` nie dzieli
  komórki). W kolumnie *Konto wygasa* liczba jest datą Excela tylko wtedy, gdy ma co najmniej pięć cyfr (`31.12` to
  błąd daty, a nie 31 stycznia 1900).
- **Cofnięcie** działa tylko w otwartym oknie. Między podglądem cofnięcia a jego wykonaniem kreator nie sprawdza obiektów
  ponownie – obiekt podmieniony w tym czasie zostałby usunięty pod tą samą nazwą; wykonujcie cofnięcie zaraz po
  podglądzie.

**Do potwierdzenia w pilotażu na Windows Server AD** – fałszywy katalog testów i laboratorium Samba (jedna domena w
lesie) tego nie rozstrzygną; scenariusz i wyniki: [PILOTAZ.md, rozdział 3.1](PILOTAZ.md#31-codzienna-praca-fazy-13):

- las z kilkoma domenami: sprawdzenie UPN i adresów przez wykaz globalny (*cały las (wykaz globalny na …)*), *sprawdzone
  tylko w domenie* bez wykazu globalnego oraz kontrola unikalności UPN w lesie wykonywana przez sam Windows przy zapisie;
- unikalność nazwy obiektu w kontenerze dla różnych typów obiektów (`CN=` obok `OU=` o tej samej nazwie);
- konta komputerów: KB5020276 (ponowne użycie przygotowanego konta przez osobę spoza administratorów) i KB5008102 (zasady
  znaku `$`), przebieg „wyłączone → losowe hasło → włączone” i porównanie z oknem *Nowy komputer*, które zakłada konto
  włączone bez hasła;
- zachowanie przy wyczerpaniu puli RID (ostrzeżenie o małej puli, błędy kolejnych kont, ponowienie);
- logowanie nowym kontem przez inny kontroler przed replikacją;
- administrator jednostki z delegowanymi uprawnieniami bez prawa resetu hasła – wykonanie zatrzymuje się po pierwszym
  koncie;
- wklejanie z prawdziwego Excela (polska wersja, liczby w zapisie naukowym i ich odzyskanie z formatu HTML), z
  LibreOffice Calc i z Arkuszy Google.
