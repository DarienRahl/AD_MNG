[English](en/POWERSHELL.md) · **Polski**

# Okno PowerShell w AD Managerze (etap R1)

Ten dokument opisuje okno **PowerShell**: wiersz poleceń przy oknie połączenia z własnymi **poleceniami odczytu** AD Managera
(`Get-AdmUser`, `Search-AdmObject` i pozostałe), wynikami, które są zwykłymi obiektami PowerShell, siatką dla nich i zasadami,
które utrzymują okno w bezpiecznych granicach. Etap R1 to **tylko odczyt**: żadne polecenie nie zmienia katalogu (rozdział 9).
Projekt techniczny: [docs/dev/r1-powershell-window.md](dev/r1-powershell-window.md); decyzje właściciela i ich uzasadnienie:
[ADR-0010](adr/0010-okno-powershell-osadzony-silnik.md).

Zasady, które obowiązują wszędzie:

- **Nic z katalogu nie trafia na dysk.** Tekst w oknie, wyniki i historia poleceń istnieją tylko w pamięci okna i znikają
  razem z nim. Plik powstaje wyłącznie wtedy, gdy operator go wskaże albo wybierze w oknie *Zapisz jako* (rozdział 5). Zasady
  grupy mogą to zmienić z zewnątrz programu – rozdział 8.
- **Żadne polecenie nie zapisuje w katalogu.** Zapisy przechodzą przez plan zmian (podgląd, audyt, cofnięcie); polecenia, które
  będą do niego dodawać operacje, to etap R2.
- **Jeden kontroler domeny na okno.** Polecenia czytają przez własną sesję okna: ten sam kontroler domeny i to samo konto co
  okno, do którego należą. Nie mają parametrów `-Server` ani `-Credential`, a skrypt w trybie domyślnym nie dosięga sesji okna
  ani jego hasła (rozdział 7).
- **Wartości są zabezpieczane.** Cokolwiek wpiszesz w parametrze, jest wartością, nigdy kawałkiem filtra (rozdział 3).

## 1. Czym jest okno i jak je otworzyć

*Narzędzia → PowerShell…* w oknie połączenia, **Ctrl+Shift+P** albo paleta poleceń (`tools.powershell`). Każde okno połączenia
ma jedno okno PowerShell; należy do tego połączenia, w tytule pokazuje jego domenę, kontroler domeny i konto (*contoso.test –
dc01.contoso.test – CONTOSO\admin – PowerShell*) i zamyka się razem z nim. Okna dwóch połączeń niczego nie dzielą: każde ma
własną sesję PowerShell (*runspace*) i własne zmienne.

Okno uruchamia **PowerShell 7.6 wbudowany w AD Managera**. Nie potrzebujesz `pwsh.exe`, Windows PowerShell ani modułów RSAT i
nie logujesz się ponownie. Pierwsze okno w uruchomieniu programu startuje krócej niż sekundę (wiersz stanu mówi *Uruchamianie
PowerShell…*); następne są szybkie.

Od góry okno ma:

- **wiersz stanu**: co okno robi (*Wykonywanie polecenia…*, *Polecenie zakończyło się: 12 wierszy danych wyjściowych w oknie,
  0,8 s*, *Polecenie zatrzymano po 3 s*) z przyciskiem **Zatrzymaj**; błędy i ostrzeżenia mają ikonę, nie tylko kolor;
- **uwagi, które trwają** – co rejestrują zasady grupy (rozdział 8), *Tryb zaufany jest włączony* (rozdział 7) i *Najstarsze
  wiersze usunięto z tego okna* (okno zachowuje ostatnie 20 000 wierszy);
- **obszar wyników**: uruchomione polecenia i to, co napisały, z błędami i ostrzeżeniami w kolorach okna;
- **wiersz poleceń** z przyciskami *Uruchom* (gdy polecenie trwa, jest to *Zatrzymaj*), *Wyczyść* i przełącznikiem *Tryb zaufany
  (FullLanguage)*.

Po otwarciu okno pisze dwa wiersze: w jakim jest trybie języka i jak znaleźć polecenia (`Get-Command *-Adm*`,
`Get-Help <polecenie>`), razem z nazwą tego dokumentu. Pod wynikami zarezerwowano miejsce na oczekujący plan zmian etapu R2; w
R1 zostaje puste i ukryte.

Zamknięcie okna w trakcie polecenia zadaje pytanie *Zatrzymać trwającą operację i zamknąć okno?* – w trybie zaufanym to, co
wpiszesz, może zrobić wszystko, więc okno traktuje trwające polecenie jak każdą operację, która może zapisywać.

## 2. Polecenia

Nazwy są takie same w angielskim i polskim interfejsie. `Get-Command *-Adm*` wypisuje je, `Get-Help <polecenie> -Examples`
pokazuje pomoc z przykładami (po angielsku). Polecenia nie mają parametrów `-Server` ani `-Credential`: zawsze używają sesji
okna.

| Polecenie | Co robi | Przykład |
|---|---|---|
| `Get-AdmVersion` | Wersje AD Managera i silnika, tryb języka oraz domena, kontroler i konto okna | `Get-AdmVersion` |
| `Get-AdmObject` | Jeden obiekt według tożsamości albo obiekty znalezione gotowym filtrem LDAP | `Get-AdmObject 'OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test' -Properties description` |
| `Get-AdmUser` | Konta użytkowników według tożsamości albo filtrów | `Get-AdmUser -Department 'Księgowość' -Enabled $true -Properties mail,title` |
| `Get-AdmGroup` | Grupy według tożsamości albo filtrów | `Get-AdmGroup -Name 'GG_*' -GroupScope Global -GroupCategory Security` |
| `Get-AdmComputer` | Konta komputerów według tożsamości albo filtrów | `Get-AdmComputer -OperatingSystem 'Windows 11*' -Enabled $true` |
| `Get-AdmGroupMember` | Członkowie grupy; z `-Recursive` także członkowie grup zagnieżdżonych | `Get-AdmGroupMember GG_IT -Recursive` |
| `Search-AdmObject` | Obiekty według wartości atrybutu albo gotowym filtrem LDAP | `Search-AdmObject -Attribute description -Value 'Łukasz (Kraków)*' -Match StartsWith` |
| `Out-AdmGrid` | Obiekty potoku w oknie z siatką (rozdział 4) | `Get-AdmUser -Department IT \| Out-AdmGrid -Title 'IT staff'` |
| `Open-AdmObject` | Otwiera właściwości obiektu w programie | `Open-AdmObject jan.kowalski` |
| `Export-AdmList` | Obiekty potoku do pliku CSV (rozdział 5) | `Get-AdmUser -Department IT \| Export-AdmList C:\Temp\it.csv` |

**Tożsamość** (`-Identity`, pierwszy parametr poleceń `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer`,
`Get-AdmGroupMember` i `Open-AdmObject`) jest rozpoznawana po kształcie, nie zgadywana: nazwa wyróżniająca (czytana tam, gdzie
jest), `objectGUID` (także `<GUID=…>`), SID (`S-1-5-…`, także `<SID=…>`), w pozostałych przypadkach `sAMAccountName` (użytkownik
lub obiekt także po głównej nazwie użytkownika; komputer z `$` albo bez). Typ polecenia jest częścią wyszukiwania: `Get-AdmUser`
zwraca tylko konta użytkowników, więc komputer o tej samej nazwie nigdy przez nie nie wróci. Tożsamość, która niczego nie
znajduje, to błąd, który to mówi (*Cannot find a user with identity 'nobody' …: it does not exist, or this account cannot see
it*). Nazwę wyróżniającą z przecinkiem w wartości trzeba ująć w cudzysłów: `'CN=Kowalski\, Jan,OU=IT,DC=contoso,DC=test'`.

**Filtry** poleceń `Get-AdmUser`, `Get-AdmGroup` i `Get-AdmComputer`: każdy parametr zawęża wynik (łączą się przez *i*) i
przyjmuje `*` jako wieloznacznik.

- Użytkownicy: `-Name`, `-SamAccountName`, `-UserPrincipalName`, `-GivenName`, `-Surname`, `-DisplayName`, `-Mail`,
  `-Department`, `-Title`, `-Company`, `-Description`, `-Enabled`.
- Grupy: `-Name`, `-SamAccountName`, `-Description`, `-GroupScope` (`DomainLocal`, `Global`, `Universal`), `-GroupCategory`
  (`Security`, `Distribution`).
- Komputery: `-Name`, `-SamAccountName`, `-DnsHostName`, `-OperatingSystem`, `-Location`, `-Description`, `-Enabled`.
- Każde z tych trzech przyjmuje też `-LdapFilter`, gotowy filtr LDAP dodawany do pozostałych. Jego składnia jest sprawdzana
  najpierw (tym samym sprawdzeniem co w oknie *Znajdź*), więc pomyłka jest błędem, zanim cokolwiek zostanie odczytane.

**Wspólne parametry** poleceń odczytu:

- `-Properties nazwa1,nazwa2` – atrybuty zwracane oprócz zwykłych (nazwy ze schematu, uzupełniane klawiszem Tab; nieznana nazwa
  jest błędem, zanim cokolwiek zostanie odczytane; `*` prosi o wszystko, co konto może czytać);
- `-SearchBase 'OU=…'` i `-SearchScope Base|OneLevel|Subtree` (alias `-Scope`) – gdzie szukać; Tab uzupełnia konteksty nazw
  domeny i kontenery, które okno już zwróciło;
- `-ResultPageSize` (1–10000, domyślnie 500) i `-ResultSetSize` (0 = wszystko) – rozmiar strony i limit wyniku.

**`Get-AdmGroupMember`.** Bez `-Recursive`: bezpośredni członkowie (grupa dowolnej wielkości, także powyżej 1500 członków). Z
`-Recursive`: ta sama lista co *Wszystkie (zagnieżdżone)* na stronie *Członkowie* programu – zagnieżdżenia oblicza kontroler
domeny, cykl grup go kończy, a **same grupy zagnieżdżone są w wyniku**; `-ExcludeGroups` je pomija i zostawia tylko konta i
komputery. Członkowie, dla których ta grupa jest *grupą podstawową*, nie są tu członkami (jak na zakładce *Członkowie*).

**`Search-AdmObject`.** `Search-AdmObject <nazwa>` szuka obiektów, których `name` jest dokładnie taka; `-Attribute a,b -Value
x,y` szuka obiektów, w których którykolwiek z atrybutów ma którąkolwiek z wartości; `-Match Exact|StartsWith|Contains|EndsWith`
mówi, jak wartość jest porównywana; `-ObjectClass` zawęża typ; `-Filter '(…)'` to gotowy filtr LDAP. Polecenie odmawia
porównywania atrybutów tajnych (np. hasła LAPS): wyszukiwanie mogłoby posłużyć do ich zgadywania.

**Zwykłe cmdlety do pracy z obiektami działają jak w każdym PowerShellu.** `Where-Object`, `Select-Object`, `Sort-Object`,
`Group-Object`, `Measure-Object`, `Format-Table`, `Format-List`, `ForEach-Object`, zmienne, `if`, `foreach` i podobne – okno ma
zamkniętą listę poleceń, podaną w rozdziale 7. Blok skryptu wpisuje się wprost, np.
`Get-AdmUser -Enabled $true | Where-Object { $_.LockedOut } | Select-Object Name, sAMAccountName`.

Prawdziwa sesja (przykładowa firma z testowego katalogu):

```text
PS> Get-AdmVersion

ApplicationVersion : 1.2.3-test
EngineVersion      : 7.6.10
LanguageMode       : Constrained
Domain             : contoso.test
DomainController   : dc01.contoso.test
Account            : CONTOSO\Administrator

PS> Get-AdmUser -Department 'Księgowość' -Enabled $true

Name                   sAMAccountName       Enabled DistinguishedName
----                   --------------       ------- -----------------
Małgorzata Wójcik      malgorzata.wojcik    True    CN=Małgorzata Wójcik,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=conto…
Grażyna Dąbrowska      grazyna.dabrowska    True    CN=Grażyna Dąbrowska,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=conto…

PS> Get-AdmUser -Identity nobody
Get-AdmUser: Cannot find a user with identity 'nobody' under 'DC=contoso,DC=test': it does not exist, or this account cannot see it.
```

## 3. Znaki specjalne i wieloznaczniki

Polecenia nigdy nie wklejają Twojego tekstu do filtra. Każda wartość jest zabezpieczana tym samym kodem, którego używa
wyszukiwarka programu (RFC 4515), więc nawias, gwiazdka, ukośnik odwrotny, cudzysłów czy polska litera w wartości to po prostu
znak.

- W `Get-AdmUser`, `Get-AdmGroup` i `Get-AdmComputer` **gwiazdka jest wieloznacznikiem** (`-Name 'Jan*'` to to, o co chodzi
  administratorowi); wszystko inne w wartości jest dosłowne.
- W `Search-AdmObject` **żaden znak nie jest wieloznacznikiem**: wartość jest brana dosłownie, a `-Match` mówi, jak jest
  porównywana. `Search-AdmObject 'Łukasz (Kraków)*\'` znajduje dokładnie obiekt o nazwie `Łukasz (Kraków)*\`, nic więcej. Do
  wyszukiwania „zaczyna się od” użyj `-Match StartsWith`.
- Gotowy filtr (`-LdapFilter`, `-Filter`) jest **Twój**: sprawdzana jest jego składnia i jest wysyłany tak, jak go napisano.
  Wartości w nim zabezpiecz sam (`\28` dla `(`, `\29` dla `)`, `\2a` dla `*`, `\5c` dla `\`).

PowerShell ma własne zasady dla tego, co wpisujesz:

- Używaj **apostrofów**. W cudzysłowie `$` zaczyna zmienną: `"CN=$name"` wstawia zmienną `$name`, a `'CN=$name'` to tekst taki,
  jak go napisano. Apostrof wewnątrz apostrofów podwaja się: `'O''Brien'`.
- Wartość ze spacją, przecinkiem lub nawiasem wymaga cudzysłowu: `-Name 'Jan Kowalski'`.
- Ścieżkę z nawiasami kwadratowymi, np. `C:\Temp\[2026]\it.csv`, `Export-AdmList -Path` bierze dosłownie; inne cmdlety czytają
  nawiasy w `-Path` jako wieloznacznik (użyj `-LiteralPath`).

## 4. Wyniki i siatka

**Wyniki to obiekty.** Okno pokazuje je w formatowaniu samego silnika (tabela albo lista), ale pod spodem to prawdziwe obiekty:
działają na nich `Where-Object`, `Select-Object`, `Sort-Object` i reszta. Każdy obiekt ma `DistinguishedName`, `Name` i
`ObjectKind`; konta mają `Enabled`, użytkownicy `LockedOut` i `UserAccountControlFlags` (nazwy flag, np. `NORMAL_ACCOUNT`);
grupy mają `GroupScope`, `GroupCategory` i `GroupTypeFlags`. Dalej są atrybuty pod swoimi nazwami LDAP (`sAMAccountName`,
`mail`, `memberOf`). `Get-Member` je wypisuje.

**Wartości są dekodowane tak jak w oknach programu**, a test porównuje je dla 1000 obiektów:

- atrybut *tak/nie* to `$true` / `$false`; liczby są liczbami (`userAccountControl` jako liczba: można porównywać jego bity);
- daty to `DateTime` w czasie lokalnym; „nigdy” (zero, największa wartość) to `$null`;
- `objectGUID` to `Guid`, SID to tekst (`S-1-5-21-…`); wartości binarne to bajty;
- atrybut o wielu wartościach to tablica (pusta, gdy nie ma wartości); atrybut bez wartości to `$null`;
- **sekrety są maskowane** jak w *Edytorze atrybutów*: materiał kluczy, hasła zarządzane i szyfrowane i podobne pokazują tekst
  maski programu. Sekrety, które Edytor atrybutów pokazuje operatorowi (hasło LAPS jawnym tekstem, hasło odzyskiwania
  BitLockera), są tu pokazane także – **nie czytaj ich w oknie, gdy zasady grupy rejestrują polecenia i wyniki**
  (rozdział 8).

**Tabela i nazwa wyróżniająca.** Domyślne tabele kończą się kolumną `DistinguishedName`, która dostaje resztę wiersza. Nazwę
dłuższą niż wiersz silnik ucina znakiem `…` (*Format-List*, szersze okno albo `Select-Object -ExpandProperty
DistinguishedName` pokazują ją w całości). Nazwa, która w wynikach jest cała, jest **odnośnikiem**: kliknij ją, aby otworzyć
obiekt w programie (*Otwórz w programie*), prawym przyciskiem – *Kopiuj nazwę wyróżniającą*; **Enter** na zaznaczonym wierszu
otwiera pierwszą nazwę tego wiersza. Ucięta nazwa nie jest odnośnikiem.

**Wiele wyników.** Wyniki przychodzą w miarę czytania: 100 000 obiektów nie blokuje okna, a **Zatrzymaj** (albo **Ctrl+C**)
kończy odczyt na kontrolerze domeny. `Select-Object -First 50` kończy odczyt po 50 obiektach. Obszar wyników zachowuje ostatnie
20 000 wierszy.

**`Out-AdmGrid`** otwiera obiekty potoku w **osobnym oknie** (każde wywołanie to nowe okno, jak `Out-GridView`; należy do okna
połączenia i zamyka się razem z nim):

- kolumny to właściwości obiektów, najpierw nazwa wyróżniająca; daty są pokazane jako `2026-11-16 08:30:00`, listy złączone
  przez `; `, wartości binarne jako `(binary, 12 bytes)`;
- kliknięcie nagłówka kolumny sortuje (liczby jako liczby, puste komórki na końcu); pole filtra nad listą szuka tekstu w
  dowolnej kolumnie (wielkość liter i polskie znaki nie mają znaczenia);
- **Enter** albo dwukrotne kliknięcie otwiera obiekt w programie (najwyżej 10 okien naraz); menu prawego przycisku ma *Otwórz w
  programie*, *Kopiuj wiersze*, *Kopiuj wiersze z nazwami kolumn*, *Kopiuj nazwę wyróżniającą* i *Eksportuj listę…*;
  **Ctrl+C** kopiuje zaznaczone wiersze (rozdzielone tabulatorami, formuły chronione jak w listach programu);
- *Eksportuj listę…* zapisuje CSV do wybranego pliku – rozdział 5.

**`Open-AdmObject`** przyjmuje tożsamość albo nazwę wyróżniającą z potoku: `Get-AdmGroup GG_IT | Open-AdmObject`. Jedno
polecenie otwiera najwyżej 10 okien właściwości; nazwa, która nie istnieje, jest błędem i okno się dla niej nie otwiera.

## 5. Eksport

Plik powstaje w dokładnie trzech przypadkach i tylko wtedy, gdy o to prosisz:

- **`Export-AdmList`** zapisuje obiekty potoku jako CSV tak jak program eksportuje listę: **formuły są chronione** (wartość
  zaczynająca się od `=`, `+`, `-` lub `@` nie może uruchomić się w Excelu), separator pochodzi z ustawień regionalnych
  (`-Delimiter ';'` go zastępuje), UTF-8 ze znacznikiem kolejności bajtów. Zapisuje **tylko do pliku, który wskażesz** w
  `-Path` (pierwszy parametr) **albo wybierzesz w oknie *Zapisz jako***, gdy nie podasz ścieżki. Nigdy nie zapisuje w
  miejscu domyślnym; istniejący plik zastępuje tylko z `-Force`; bez ścieżki i z anulowanym oknem nie zapisuje niczego i
  mówi o tym. Wypisuje ścieżkę oraz liczbę wierszy i kolumn, nigdy wartości.
- ***Eksportuj listę…*** w oknie siatki `Out-AdmGrid`: ten sam CSV do wybranego pliku.
- **`Start-Transcript -Path <plik>`** (albo `-LiteralPath`) zapisuje przebieg sesji – polecenia i wszystko, co pokazały, a więc
  także dane z katalogu – do pliku, który wskażesz, aż do `Stop-Transcript` albo do zamknięcia okna. Bez ścieżki jest odrzucany:
  silnik zapisałby wtedy plik o nadanej przez siebie nazwie w Twoim folderze *Dokumenty*. Nie ma tu ochrony formuł; traktuj ten plik
  jak eksport.

Żadne inne polecenie nie zapisuje pliku. `Export-Csv`, `Export-Clixml`, `Out-File`, `Set-Content` i `Add-Content` odpowiadają
komunikatem, który to mówi, a przekierowanie do pliku (`>`, `>>`, `2>`) jest odrzucane, zanim polecenie ruszy (`> $null` nadal
odrzuca wynik, a `2>&1` nadal łączy strumienie). Do CSV użyj `Export-AdmList`.

## 6. Historia i to, co zostaje w pamięci

- **Historia poleceń jest tylko w pamięci okna** – ostatnie 500 wierszy, bez powtórzeń z rzędu. **Góra** i **Dół** chodzą po
  niej (najpierw po wierszach polecenia wielowierszowego), **Ctrl+R** ją przeszukuje (*Szukanie w historii: …*; Enter bierze
  trafienie, Esc anuluje). Nie jest zapisywana i znika, gdy okno się zamyka. *Wyczyść* (Ctrl+L) czyści wyniki, nie historię.
- **Wiersz z hasłem jawnym tekstem nie jest zapamiętywany:** z `-AsPlainText` (przy `ConvertTo-SecureString` także skróconym do
  `-As` albo `-A`), z `ConvertTo-SecureString 'tekst'`, z napisem zamienianym na `[securestring]` (`[securestring]$x = 'tekst'`)
  albo z wartością wpisaną wprost dla nazwy sekretu – parametru (`-Password 'tekst'`), klucza tablicy skrótów (`@{ Password = 'tekst' }`,
  także przekazanej przez `@h` i klucza złożonego ze zmiennej, jak `"Pass$x"`) albo zmiennej lub właściwości (`$password = 'tekst'`,
  `$cred.Password = 'tekst'`). Nazwą sekretu jest nazwa, która zawiera `Password`, `Passwd`, `Passphrase`, `Pwd`, `Secret`,
  `Token`, `ApiKey` lub `PrivateKey`, nazwa `Pw`, `Pin` albo `Key` w całości i skróty `-Pa`, `-Pas`, `-Pass`. Wartość w
  nawiasach albo złożona z samych napisów (`-Password ('tekst')`, `$password = 'te' + 'kst'`, `$password = $x = 'tekst'`) liczy
  się tak samo jak wpisana wprost. Nie jest zapamiętywany także
  wiersz z wartością wpisaną bez nazwy parametru dla polecenia, którego nazwa mówi o haśle (`Reset-AdmPassword jan 'tekst'`).
  Zmienna albo `Read-Host -AsSecureString` jako wartość jest w porządku. Filtr nie rozpozna każdego hasła: wartości bez nazwy
  parametru dla innego polecenia, hasła wewnątrz dłuższego tekstu, argumentu programu konsolowego (`net user jan tekst`) ani
  nazwy spoza listy (np. polskiej: `$haslo = 'tekst'` zostaje zapamiętany). Wpisany wiersz zostaje też widoczny w obszarze wyników, dopóki go nie wyczyścisz albo nie
  zamkniesz okna – i zasady grupy mogą go zarejestrować (rozdział 8) – więc nie wpisuj hasła do polecenia w ogóle. Hasło, o które prosi `Read-Host -AsSecureString` albo parametr obowiązkowy, wpisuje się w polu hasła i
  nigdy nie jest pokazywane.
- **Program nie zapisuje niczego więcej.** PSReadLine nie jest ładowany (nie ma pliku historii), żaden profil nie jest
  uruchamiany (`$PROFILE` nigdy nie jest ładowany), telemetria i kanał diagnostyczny silnika są wyłączone, automatyczne
  ładowanie modułów i pamięć podręczna analizy modułów są wyłączone, a test sprawdza, że silnik nie zapisuje własnych plików (na
  Windows tylko w folderach, które da się przekierować zmienną, więc dowodem są przede wszystkim powyższe ustawienia).
  Dziennik diagnostyczny programu nigdy nie zawiera polecenia ani wartości.

## 7. Tryby języka i tryb zaufany

**Okno zaczyna w trybie *ConstrainedLanguage*.** Polecenia AD Managera, zwykłe cmdlety, zmienne, pętle, warunki i bloki
skryptów działają; ograniczony jest dowolny kod .NET:

- `[pscustomobject]@{ a = 1 }` jest odrzucane (*Only core types are supported in this language mode*); użyj `New-Object PSObject
  -Property @{ a = 1 }` albo `Select-Object` z właściwościami obliczanymi (`@{ Name = 'x'; Expression = { … } }`);
- wywołanie metody typu, który nie jest typem podstawowym, np. `[System.IO.File]::ReadAllText(…)` albo
  `$PSCmdlet.ShouldProcess()`, jest odrzucane (*Method invocation is supported only on core types in this language mode*);
  definiowanie nowego typu (`Add-Type -TypeDefinition`) jest odrzucane;
- `Format-*`, `Out-String`, `Select-Object`, `Sort-Object`, `Group-Object` i `Measure-Object` działają.

**Okno ma zamkniętą listę poleceń** – tę samą w obu trybach, na każdym komputerze:

- polecenia AD Managera (rozdział 2);
- obiekty i potok: `Where-Object`, `Select-Object`, `Sort-Object`, `Group-Object`, `Measure-Object`, `Compare-Object`,
  `ForEach-Object` (bez `-Parallel`, `-AsJob` i pozostałych parametrów równoległych), `Tee-Object` (do zmiennej, `-Variable`),
  `Get-Unique`, `Join-String`, `Get-Member`, `Add-Member`, `New-Object` (bez `-ComObject`);
- wyświetlanie: `Format-Table`, `Format-List`, `Format-Wide`, `Format-Custom`, `Format-Hex`, `Out-String`, `Out-Host`, `Out-Null`,
  `Out-Default`;
- dane w pamięci: `ConvertTo-Csv`, `ConvertFrom-Csv`, `ConvertTo-Json`, `ConvertFrom-Json`, `ConvertTo-SecureString`;
- pliki i lokalizacje, tylko do odczytu: `Get-Content`, `Import-Csv`, `Select-String`, `Test-Path`, `Get-ChildItem`, `Get-Item`,
  `Resolve-Path`, `Join-Path`, `Split-Path`, `Get-Location`, `Set-Location`, `Push-Location`, `Pop-Location`, `Get-PSDrive`;
- zmienne i aliasy: `Get-Variable`, `Set-Variable`, `New-Variable`, `Remove-Variable`, `Clear-Variable`, `Get-Alias`,
  `Set-Alias`, `New-Alias`;
- pomoc i wartości: `Get-Command`, `Get-Help` (także `help`, bez stronicowania), `Get-Date`, `New-TimeSpan`, `Get-Random`,
  `New-Guid`, `Get-Culture`, `Get-UICulture`, `Get-ExecutionPolicy`;
- komunikaty i pytania: `Write-Host`, `Write-Output`, `Write-Error`, `Write-Warning`, `Write-Verbose`, `Write-Debug`,
  `Write-Information`, `Write-Progress`, `Read-Host`;
- skrypty wpisywane w oknie: `Start-Sleep`, `Measure-Command`, `Set-StrictMode`, `Clear-Host` (`cls` – czyści okno jak Ctrl+L,
  w miejscu polecenia, w którym stoi; historia zostaje); zapis przebiegu sesji: `Start-Transcript -Path` i `Stop-Transcript`
  (rozdział 5). `exit` kończy polecenie tam, gdzie stoi – także w funkcji – a okno pisze o tym z kodem wyjścia i zostaje otwarte.

Nic poza tym. Nie ma zadań w tle, wątków ani osobnych runspace'ów (`Start-Job`, `ForEach-Object -Parallel`, `&` na końcu
potoku), sesji zdalnych (`Invoke-Command`, `Enter-PSSession`), `Invoke-Expression`, `Start-Process`, ładowania modułów ani
zestawów (`Import-Module`, `using module`, `using assembly`, `Add-Type`), zdarzeń, debugera, `Update-Help`, `Set-ExecutionPolicy`
ani żadnego polecenia, które zapisuje plik (rozdział 5). Kilka nazw, które łatwo wpisać z przyzwyczajenia, odpowiada komunikatem,
dlaczego ich nie ma i czego użyć zamiast nich: `Start-Job`, `Import-Module`, `Invoke-Command`, `Invoke-Expression`,
`Get-Credential` (okno pracuje z poświadczeniami swojego połączenia), `Start-Process`, `Out-GridView` (użyj `Out-AdmGrid`),
`Export-Csv`, `Export-Clixml`, `Out-File`, `Set-Content` i `Add-Content`; pozostałe są *nierozpoznane* (*not recognized*).
`using module`, `using assembly`, `&` na końcu potoku, przekierowanie do pliku i wpisane `ForEach-Object -Parallel` są
odrzucane, zanim ruszy cokolwiek z polecenia. Tak samo w całości polecenie dłuższe niż 262 144 znaki albo zagnieżdżone zbyt
głęboko – setki nawiasów lub bloków skryptu jeden w drugim albo łańcuch setek operatorów (`1+1+1+…`): silnik PowerShella mógłby na
nim zakończyć cały program; takie polecenie nie trafia też do historii. Pliki skryptów (`.ps1`) się nie uruchamiają – na Windows okno odrzuca je
niezależnie od polityki wykonywania. Nie ma profilu ani PSReadLine. Żadne zadanie nie trwa dłużej niż polecenie, które je
uruchomiło: zadanie pozostawione w sesji jest zatrzymywane, gdy polecenie się kończy, po *Zatrzymaj* i przy zamknięciu okna.
Programy konsolowe (`whoami`, `nltest`, `repadmin`) działają jak w każdej konsoli – jako osobny proces z Twoją tożsamością
Windows, nigdy z poświadczeniami okna.

Polecenia, które o coś pytają – `Read-Host`, parametr obowiązkowy, `-Confirm` – pytają w oknie, a *Zatrzymaj* anuluje pytanie.

**Tryb zaufany (FullLanguage)** to przełącznik pod wierszem poleceń:

- jego **włączenie** najpierw pyta. Pytanie wymienia domenę i mówi, na co tryb pozwala: na dowolny kod .NET i zmiany katalogu
  wykonywane bezpośrednio, **z pominięciem planu zmian** – bez podglądu, audytu i cofania AD Managera, a także że wyłączenie
  trybu uruchamia nową sesję. Nic w R1 nie powstrzymuje skryptu przed zapisem w tym trybie (polecenia R1 tylko czytają, ale .NET
  potrafi więcej): kod .NET sięga do wszystkiego w procesie AD Managera, także do sesji katalogu okna i jej poświadczeń.
  Włączenie zachowuje sesję i jej zmienne (funkcje zdefiniowane wcześniej w ConstrainedLanguage silnik PowerShella sam usuwa);
- nie dodaje poleceń: lista wyżej jest ta sama. Zadanie, które kod .NET umieści w sesji (własne `ForEach-Object -Parallel
  -AsJob` silnika, osiągnięte przez .NET), jest zatrzymywane razem z poleceniem jak każde inne (gdy silnik otwiera akurat runspace
  dla jednego z jego zadań podrzędnych – gdy go otworzy), a jego zadania podrzędne, które czekały jeszcze na swoją kolej, już się
  nie uruchamiają (poza tym, na które kolej przyjdzie akurat w tej chwili – jest zatrzymywane razem z resztą); wątek albo runspace,
  który kod .NET utworzy sam, jest poza zasięgiem okna, jak reszta procesu – działa dalej po *Zatrzymaj* i po zamknięciu okna;
  blok skryptu, który taki licznik czasu albo wątek wywoła po zamknięciu okna, już się nie wykona (nie ma runspace'u), a jego błąd
  nie zamyka programu;
- dopóki jest włączony, tytuł okna ma dopisek *– tryb zaufany* i trwa uwaga, która mówi to samo;
- jego **wyłączenie** nie pyta, ale uruchamia w oknie **nową sesję PowerShell** w ConstrainedLanguage. Przez ułamek sekundy, gdy
  powstaje, wiersz stanu mówi *Wyłączanie trybu zaufanego: uruchamianie nowej sesji PowerShell…*, a polecenia, Tab i przełącznik
  czekają. Zmienne, funkcje, aliasy i bloki skryptu z trybu zaufanego znikają razem ze starą sesją, więc nic z nich nie działa
  dalej w FullLanguage w oknie, które mówi ConstrainedLanguage; okno pisze o tym w wynikach, a historia poleceń i zawartość okna
  zostają. Gdy nowej sesji nie da się uruchomić, okno nie zostaje w trybie zaufanym: mówi *Nie można uruchomić PowerShell* i nie
  wykonuje już poleceń (rozdział 11);
- to, co kod .NET trybu zaufanego zmienił **poza sesją**, zostaje do zamknięcia programu – także w innych oknach PowerShell i
  w nowym oknie: wątek albo licznik czasu, który uruchomił, i składowa, którą `Add-Member` dodał do typu .NET albo obiektu
  całego procesu (np. `[string] | Add-Member -MemberType ScriptMethod …` albo to samo na kulturze z `Get-Culture`) – taką
  składową okno w ConstrainedLanguage może nadal wywołać i działa ona w FullLanguage. Żadna sesja tego nie cofa; jeśli tak
  zrobiłeś, zamknij program;
- dotyczy tylko tego okna i **nigdy nie jest zapamiętywany**: nowe okno znów zaczyna w ConstrainedLanguage;
- nie można go zmienić, gdy polecenie trwa ani zanim PowerShell się uruchomi;
- gdy system wymusza ConstrainedLanguage (WDAC albo AppLocker), okno w nim zostaje, a przełącznik jest **wyłączony** – podpowiedź
  podaje powód, tak jak w każdym innym stanie, w którym przełącznik jest wyłączony.

W ConstrainedLanguage żadne polecenie okna nie uruchamia kodu w innym trybie języka, a nic w sesji – żadna zmienna, parametr ani
nic z `$Host` – nie przekazuje skryptowi sesji katalogu okna ani jej poświadczeń: polecenia AD Managera znajdują swoje okno
w tablicy silnika, której skrypt nie odczyta.

Tryb języka i lista poleceń to **zabezpieczenie przed pomyłką, a nie granica bezpieczeństwa.** Granicą są uprawnienia konta okna
w AD: okno nigdy nie daje więcej, niż konto ma, a zmiana wykonana z pominięciem planu nie ma w programie audytu ani cofnięcia
(AdMng Watchdog widzi ją jak każdą inną zmianę w AD).

## 8. Co robi z oknem rejestrowanie z zasad grupy

Zasady grupy – komputera albo użytkownika – mogą włączyć w PowerShell **transkrypcję**, **rejestrowanie bloków skryptów** i
**rejestrowanie modułów**. Dotyczą każdego programu, który hostuje silnik PowerShell, **także AD Managera**: transkrypcja
zapisuje polecenia wykonywane tutaj **i ich wyniki** – czyli dane z katalogu – w katalogu wskazanym przez zasadę, a rejestrowanie
bloków skryptów zapisuje treść poleceń w dzienniku zdarzeń systemu Windows (*PowerShellCore/Operational*). AD Manager nie zmienia
tych zasad i ich nie omija.

**Co okno robi w tej sprawie:**

- przy otwarciu odczytuje zasady – PowerShell Core (`HKLM` i `HKCU`, `Software\Policies\Microsoft\PowerShellCore`), zasady
  Windows PowerShell, gdy włączono *Use Windows PowerShell Policy setting*, i oba pliki `powershell.config.json` (silnika, obok
  `AdMng.exe`, oraz Twój, w *Dokumenty\PowerShell*) – oraz stan blokady systemowej;
- zanim uruchomisz pierwsze polecenie, mówi wprost, co jest rejestrowane i gdzie, np. *Transkrypcja jest włączona (zasady
  komputera): polecenia wykonywane w tym oknie i ich wyniki, także dane z katalogu, są zapisywane w D:\PSLogs.* Uwaga jest w
  wierszu stanu (dopóki wynik polecenia jej nie przykrywa), w układzie pod nim i w pierwszych wierszach wyników; trwa do
  zamknięcia okna;
- gdy zasad nie da się odczytać, mówi o tym: *Nie wiadomo, czy polecenia i wyniki są rejestrowane.* Brak wiedzy to też uwaga.

**Co z tym zrobić:** jeśli włączono transkrypcję albo rejestrowanie bloków skryptów, nie oglądaj w oknie wrażliwych atrybutów
(haseł LAPS, haseł odzyskiwania BitLockera) i nie wpisuj hasła do polecenia. Zapytaj zespół bezpieczeństwa, dokąd trafiają
transkrypcje i kto może je czytać – zawierają te same dane, które pokazuje okno. Plik `powershell.config.json`, który AD Manager
dostarcza obok `AdMng.exe`, wyłącza transkrypcję i rejestrowanie włączone w **Twoim** `powershell.config.json`; nie wyłączy zasad
komputera, a uwaga mówi wtedy, że zasada jest włączona. Systemy bezpieczeństwa (EDR) mogą zgłaszać *silnik PowerShell w
procesie innym niż `powershell.exe`* – tym procesem jest `AdMng.exe`; uprzedź zespół bezpieczeństwa (SOC).

## 9. Czego okno jeszcze nie robi

- **Brak zapisów (etap R2).** Nie ma jeszcze poleceń `Set-`, `New-`, `Remove-`, `Move-`, `Rename-`, `Add-`, `Enable-`,
  `Disable-`, `Unlock-` ani `Reset-` AD Managera i nie ma dysku `AD:`. Test sprawdza, że lista poleceń zawiera wyłącznie
  odczyty. Aby zmienić obiekt, użyj okien programu (właściwości, edytor zbiorczy, kreatory): działają tam podgląd, walidacja,
  audyt i cofnięcie. R2 doda polecenia, które wkładają operacje do **oczekującego planu zmian** z własnym panelem (puste miejsce
  pod wynikami), pokazywanego w tym samym podglądzie i wykonywanego dopiero po Twoim potwierdzeniu.
- **Brak modułów RSAT (etap R3).** `Import-Module ActiveDirectory` i `GroupPolicy` są odrzucane, a `Get-ADUser` i inne polecenia
  `…-AD…` w oknie nie istnieją. Używaj `Get-AdmUser` i jego krewnych; R3 doda moduły skierowane na kontroler domeny okna i
  przetłumaczy ich polecenia zapisu na plan zmian.
- **Brak modułów Microsoft 365** (ścieżka C), zdalnych sesji PowerShell do serwerów, zadań w tle, skryptów z plików (`.ps1`)
  i skryptów uruchamianych według harmonogramu.
- **Brak uzupełniania nazw plików.** Tab uzupełnia polecenia, parametry, zmienne, nazwy atrybutów i kontenery okna, nie nazwy
  plików; okno nigdy nie czyta katalogu na każdy klawisz.
- **Wiersz stanu liczy wiersze, nie obiekty.** Okno pokazuje sformatowany tekst silnika, więc mówi *12 wierszy danych
  wyjściowych*, nie *12 obiektów*.
- `LockedOut` to własna flaga kontrolera domeny, gdy ją zwraca, a w przeciwnym razie „ma czas blokady”; lista kont w programie
  stosuje dodatkowo czas blokady z zasad domeny.

## 10. Klawiatura

| Gdzie | Klawisz | Co robi |
|---|---|---|
| Wiersz poleceń | **Enter** | Uruchamia polecenie, gdy jest kompletne; dodaje wiersz, gdy nie jest (otwarty cudzysłów, nawias, blok skryptu, `\|` na końcu). Przy otwartej liście uzupełnień bierze zaznaczone dopasowanie; gdy polecenie czeka na odpowiedź, wysyła odpowiedź. |
| | **Shift+Enter** | Zawsze dodaje wiersz |
| | **Tab**, **Shift+Tab**, **Ctrl+Spacja** | Uzupełnia (jedno dopasowanie jest wstawiane, kilka otwiera listę), cofa się na liście, otwiera listę |
| | **Esc** | Zamyka listę albo wyszukiwanie w historii |
| | **Góra**, **Dół** | Wiersze polecenia wielowierszowego, potem historia, potem z powrotem do tego, co pisałeś |
| | **Ctrl+R** | Przeszukuje historię; wpisuj, aby zawężać, **Backspace** usuwa znak |
| | **Ctrl+L** | Czyści wyniki (`cls` w poleceniu robi to samo) |
| | **Ctrl+C** | Kopiuje, gdy zaznaczono tekst; w przeciwnym razie **zatrzymuje trwające polecenie** |
| Obszar wyników | **Ctrl+C** | Kopiuje zaznaczone wiersze (bez zaznaczenia: zatrzymuje polecenie) |
| | **Enter** | Otwiera pierwszą nazwę wyróżniającą zaznaczonego wiersza |
| | **Esc** | Powrót do wiersza poleceń |
| Okno siatki | **Enter**, dwukrotne kliknięcie | Otwiera obiekt |
| | **Ctrl+C** | Kopiuje zaznaczone wiersze |

Edytor koloruje polecenie w trakcie pisania: polecenia, parametry, zmienne, ciągi znaków, liczby, słowa kluczowe i komentarze
(kolory podążają za motywem jasnym, ciemnym i wysokiego kontrastu; w wysokim kontraście zlewają się z kolorem tekstu).

## 11. Gdy coś nie działa

| Co widzisz | Co sprawdzić |
|---|---|
| *Ta instalacja nie zawiera silnika PowerShell.* | To wydanie AD Managera zbudowano bez silnika. Użyj standardowego pakietu. |
| *Nie można uruchomić PowerShell* | Zamknij okno i otwórz je ponownie – także wtedy, gdy stało się to po wyłączeniu trybu zaufanego (nowa sesja nie wystartowała) albo po pozostawionym poleceniu. Powód jest w komunikacie. Jeśli się powtarza, zgłoś to ([PILOTAZ.md, rozdział 4](PILOTAZ.md#4-zgłaszanie-problemów)) z wersją programu. |
| Polecenie się nie kończy | **Zatrzymaj** (albo Ctrl+C). Jedno dopasowanie wyrażenia regularnego (`-match`, `-replace`, `-split`, `switch -regex`, `Select-String`, `[regex]`) trwa najwyżej 2 sekundy, potem kończy się błędem *The Regex engine has timed out…*. Polecenie utknięte w kodzie .NET, którego zatrzymanie nie przerywa (tryb zaufany albo `[regex]` z własnym, dłuższym limitem czasu), okno zostawia po 5 sekundach i mówi o tym na pasku stanu i w oknie wyników: następne polecenie działa od razu, w nowej sesji, ale zmiennych, funkcji i modułów z wcześniejszych poleceń już w niej nie ma. Pozostawione polecenie może zajmować rdzeń procesora, dopóki się nie skończy albo nie zamkniesz programu; zamknięciu programu nie przeszkadza. |
| *Cannot find a user with identity …* | Tożsamość nie istnieje, jest innego typu (`Get-AdmUser` nie znajdzie komputera) albo konto jej nie widzi. Nazwa wyróżniająca z przecinkiem w wartości wymaga cudzysłowu. |
| *Access denied reading …* / *The attribute '…' of '…' was not returned …* | Konto okna nie ma prawa czytać obiektu albo atrybutu; kontroler domeny odpowiada na nieczytelny atrybut tak, jakby był pusty. Komunikat podaje atrybut jeden raz. |
| *The LDAP filter is not valid* | Składnia `-LdapFilter` lub `-Filter`: zrównoważone nawiasy, `(atrybut=wartość)`. |
| Nic nie wraca | Sprawdź filtr i `-SearchBase`; przy kilku parametrach filtra muszą pasować wszystkie. Nieistniejąca baza wyszukiwania to błąd, który ją nazywa. |
| *Only core types are supported in this language mode* | Zob. rozdział 7: użyj `New-Object PSObject -Property` albo włącz tryb zaufany. |
| *… is not available in the AD Manager PowerShell window* / *The term '…' is not recognized* | Okno ma zamkniętą listę poleceń (rozdział 7). Komunikat mówi, czego użyć zamiast – do pliku `Export-AdmList` albo `Start-Transcript -Path` (rozdział 5). |
| Nazwy wyróżniające kończą się `…` | Użyj `Format-List`, szerszego okna albo `Select-Object -ExpandProperty DistinguishedName`. |
| Przełącznik trybu zaufanego jest wyłączony | Podpowiedź mówi, dlaczego: trwa polecenie, PowerShell się uruchamia, właśnie wyłącza tryb zaufany albo nie dał się uruchomić, w tej instalacji nie ma silnika, sesja połączenia się skończyła albo system wymusza ConstrainedLanguage (WDAC, AppLocker). |
| Uwaga mówi, że włączono transkrypcję lub rejestrowanie | Rozdział 8. To ustawienie zasad grupy tego komputera lub konta, nie AD Managera. |
| *There are no objects to show: no grid was opened* | Potok `Out-AdmGrid` był pusty. |
| *The file … exists. Nothing was written; use -Force* / *No file was chosen: nothing was written.* | `Export-AdmList` zastępuje plik tylko z `-Force` i nie zapisuje niczego bez ścieżki ani pliku wybranego w oknie *Zapisz jako*. |
| `Get-Help` nie pokazuje przykładów | Usunięto plik `AdMng.PowerShell.dll-Help.xml` obok `AdMng.exe`. Przywróć go z pakietu. |

## 12. Ograniczenia i pilotaż

**Nikt jeszcze nie widział okna na ekranie.** Silnik, polecenia i model okna są objęte testami automatycznymi na Linuksie i
Windows, a prawdziwe okno z prawdziwym silnikiem nad sztucznym katalogiem działa w testach na Windows, ale pilotaż na Windows
musi potwierdzić:

- wygląd kolorowanego tekstu (pole edytuje tekst przezroczysty, kolory rysuje warstwa pod nim), kursor i zaznaczenie w motywie
  jasnym, ciemnym i wysokiego kontrastu, przy skalowaniu 125 %–200 % oraz listę uzupełnień przy krawędzi ekranu;
- czytnik ekranu (Narrator, NVDA) z oknem: lista uzupełnień nie jest ogłaszana;
- zasady okna pod **prawdziwymi zasadami grupy** (transkrypcja, rejestrowanie bloków skryptów, rejestrowanie modułów; klucze
  pochodzą z kodu samego silnika i z udokumentowanych szablonów) oraz silnik pod **WDAC / AppLocker**;
- polecenia na **prawdziwym Windows Server AD** (testy przeszły nad sztucznym katalogiem i kontrolerem Samba).

Scenariusze pilotażu są w [PILOTAZ.md, rozdział 3.7](PILOTAZ.md#37-okno-powershell-r1).

Inne ograniczenia: PowerShell 7.6 powiększa `AdMng.exe` o około 38 MB, a każda poprawka bezpieczeństwa PowerShella oznacza nowe
wydanie AD Managera. Skrypt w trybie zaufanym, który używa `System.Data.SqlClient`, nie znajdzie `sni.dll` (pakiet nie jest
otwartoźródłowy, więc go pominięto). Silnika nie ma w pakiecie Watchdoga.
