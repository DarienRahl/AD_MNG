# Raport z badań – AD_MNG

Nowoczesny następca konsoli „Użytkownicy i komputery usługi Active Directory” (ADUC, `dsa.msc`) w WPF/.NET 10.
Dane czytamy wyłącznie na żywo z kontrolera domeny przez LDAP i nie trzymamy lokalnej kopii katalogu. Najważniejsze
wymaganie to edycja zbiorcza.

| | |
|---|---|
| Data | 13 października 2026 |
| Podstawa | 7 obszarów badawczych (ADUC; nowsze narzędzia Microsoft; produkty konkurencyjne; LDAP przez S.DS.P; interfejs; bezpieczeństwo; edycja zbiorcza) oraz synteza architektoniczna |
| Powiązane dokumenty | [`ARCHITECTURE.md`](ARCHITECTURE.md), decyzje architektoniczne w [`adr/`](adr/) |
| Decyzje klienta | technologia WPF/.NET 10; interfejs PL + EN; priorytet: Kosz AD oraz atrybuty Exchange/poczty; konfigurowalne widoki dla podziałów OU |

**Konwencje.** Nazwy atrybutów, klas, kontrolek, OID-y, nazwy API i elementów interfejsu ADUC zostawiamy w oryginale
(angielskim). Dopisek **(do weryfikacji w laboratorium)** oznacza fakt, którego badacze nie potwierdzili w źródle
pierwotnym: był podany z pamięci, pochodził z fragmentu wyników wyszukiwarki albo źródła były ze sobą sprzeczne.
Zanim oprzemy na nim implementację, trzeba go sprawdzić na kontrolerze Windows Server (a w przypadku produktów
komercyjnych – na wersji próbnej). Niepotwierdzone kwestie licencyjne i prawne mają krótszy dopisek „do weryfikacji”.

**Ograniczenia badania.** Z piaskownicy nie dało się otworzyć learn.microsoft.com ani większości witryn producentów.
Dokumentację Microsoftu czytano więc z repozytoriów źródłowych MicrosoftDocs na GitHubie (ten sam tekst co na Learn),
zachowanie S.DS.P – z kodu dotnet/runtime (gałąź `release/10.0`), a informacje o produktach komercyjnych – z wyciągów
wyszukiwarki. Pod koniec prac wyczerpał się budżet wyszukiwań. W syntezie bezpośrednio w kodzie źródłowym sprawdzono
dwie kwestie sporne: identyfikatory WKGUID (nagłówek `ntdsapi.h` z mingw-w64) oraz zachowanie
`LdapConnection.Abort()` w .NET 10.

**Sekcje:** [1 Streszczenie](#1-streszczenie) · [2 ADUC](#2-aduc-dsamsc--inwentarz-funkcji) · [3 Bolączki](#3-bolączki-aduc-i-jak-je-adresujemy) · [4 Narzędzia Microsoft](#4-adac-windows-admin-center-entra-admin-center-powershell-ad) · [5 Konkurencja](#5-produkty-konkurencyjne-i-open-source) · [6 LDAP](#6-techniki-ldap-dla-płynności-na-żywo) · [7 Interfejs](#7-interfejs) · [8 Bezpieczeństwo](#8-bezpieczeństwo-i-poświadczenia) · [9 Edycja zbiorcza](#9-edycja-zbiorcza--najlepsze-praktyki) · [10 Ryzyka](#10-ryzyka-i-jak-je-ograniczamy) · [11 Pytania](#11-pytania-otwarte-do-zespołu) · [12 Źródła](#12-źródła)

---

## 1. Streszczenie

1. ADUC (`dsadmin.dll` w MMC 3.0) nadal jest podstawowym narzędziem do pracy z AD w Windows Server 2025 i RSAT dla
   Windows 11, choć Microsoft praktycznie go nie rozwija od 2008 R2; ADAC go nie wyparł, a rozszerzenie AD w Windows
   Admin Center od 2019 r. ma status „early preview”.
2. ADUC to leniwie ładowane drzewo domeny, lista z limitem 2000 obiektów i modalne karty, których pola w większości
   odpowiadają 1:1 atrybutom LDAP – pełny parytet jest osiągalny czystym LDAP przez `System.DirectoryServices.Protocols`
   (S.DS.P), bez RSAT i ADWS.
3. Wyjątki – „User cannot change password”, „Protect object from accidental deletion”, „Manager can update membership
   list” (wpisy ACE), karty RDS (binarny `userParameters`) i kraj (`c`/`co`/`countryCode`) – wymagają osobnych koderów
   testowanych na wartościach zapisanych przez ADUC.
4. Bolączki ADUC (zamrażanie przy dużych OU, limit 2000, brak wyszukiwania w trakcie pisania, ręczne F5, edycja wielu
   obiektów bez podglądu i cofania, brak przełączania poświadczeń) wynikają z architektury MMC/ADSI, a nie z AD.
5. Płynność bez lokalnej kopii dają: anulowalny wrapper na S.DS.P oparty na `TaskCompletionSource` (nie `FromAsync` – po
   `Abort()` callback nigdy nie przychodzi), stronicowanie z małą pierwszą stroną, VLV powyżej ok. 2000 obiektów, ANR z
   opóźnieniem 250 ms, ranged retrieval/ASQ dla dużych grup i atrybuty konstruowane.
6. Prototyp na Sambie 4.19 potwierdził rząd wielkości (bind 16 ms, pierwsza strona 100 obiektów 65 ms, 428 obiektów
   174 ms, 20 równoległych zapytań na jednym połączeniu, działające VLV i ASQ); trzeba to powtórzyć na Windows Server
   2025 z Kerberos i szyfrowaniem.
7. ManageEngine ADManager Plus, Adaxes i Active Roles opierają się na serwerze pośredniczącym, koncie serwisowym i
   własnej bazie (ManageEngine kopiuje AD do PostgreSQL co 10 minut), co łamie wymaganie „brak danych lokalnych” i
   tworzy nowy zasób Tier-0.
8. Żadne przebadane narzędzie nie ma pełnego potoku (wybór → podgląd różnic → kontrola współbieżności → wykonanie z
   raportem per obiekt → cofnięcie), więc obok modelu „zero infrastruktury” jest to główny wyróżnik produktu.
9. Wszystkie zapisy, także pojedyncze, przechodzą przez jeden silnik planu zmian: atomowy `ModifyRequest` na obiekt,
   jeden przypięty zapisywalny DC, CAS do wykrywania konfliktów, `PermissiveModify` dla członkostwa, adaptacyjne
   ograniczanie obciążenia DC i dziennik cofania wyłącznie w pamięci.
10. Łączymy się Kerberosem z podpisywaniem i szyfrowaniem na 389, zawsze do FQDN kontrolera (LDAPS opcjonalnie; simple
    bind i ciche przejście na NTLM zabronione), co spełnia domyślne wymagania nowych lasów WS2025.
11. Inne poświadczenia działają jak `runas /netonly` wewnątrz procesu (`LogonUserW` z `LOGON32_LOGON_NEW_CREDENTIALS`,
    bind w `RunImpersonated`, kontrola WhoAmI), bo `NetworkCredential` w S.DS.P trzyma jawną kopię hasła.
12. Interfejs świadomy uprawnień (`allowedAttributesEffective` i pokrewne) pokazuje helpdeskowi, co może zmienić, ale
    jest tylko podpowiedzią – granicą bezpieczeństwa pozostają ACL w AD.
13. WPF na .NET 10 z wbudowanym motywem Fluent to najlepszy stos (gęste kontrolki danych, dojrzałe UI Automation), pod
    warunkiem izolacji eksperymentalnego `ThemeMode` (WPF0001), stylów kompaktowych i obejścia błędów `DataGrid`.
14. Priorytety klienta mają solidne podstawy: przywracanie z Kosza AD „rodzic przed dzieckiem” usuwa główne
    ograniczenie ADAC, a `mail`/`proxyAddresses` wymagają sprawdzania unikalności w GC, której AD sam nie wymusza.
15. Otwarte pozostają: skala i topologia środowiska, obecność Exchange i stan Kosza AD, granica „danych lokalnych”
    oraz progi potwierdzeń w operacjach zbiorczych.

---

## 2. ADUC (dsa.msc) – inwentarz funkcji

### 2.1 Stan narzędzia w 2026 r.

- ADUC to `dsadmin.dll` w MMC 3.0. W Windows 11 dostarcza go Feature on Demand
  `Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0`. Instalacja wymaga lokalnego administratora, nie działa w Windows
  Home, a natywne wsparcie ARM64 pojawiło się dopiero w aktualizacji zapoznawczej (preview) z lutego 2026.
- Ostatnie zmiany są drobne: karta Windows LAPS, osobno pakowane rozszerzenie BitLocker, Name Mappings domyślnie w
  silnym formacie `X509IssuerSerialNumber` (od lutego 2024) oraz odmowa ustawienia hasła komputera wyprowadzonego z
  nazwy na DC WS2025.
- Archiwalny blog AskDS nazywa ADUC przestarzałym na rzecz ADAC, ale nie podaje daty usunięcia. Według FAQ Windows
  Admin Center nie zastępuje RSAT w zakresie AD.
- ADUC czyta wszystko na żywo. Poza definicjami zapisanych zapytań w pliku konsoli (`%APPDATA%\Microsoft\MMC\DSA` lub
  własny `.msc`) nie przechowuje danych, więc parytet funkcji da się pogodzić z zasadą „brak danych lokalnych”.

### 2.2 Drzewo i kontenery dobrze znane (WKGUID)

Korzeń drzewa to „Active Directory Users and Computers [*FQDN związanego DC*]”, a pod nim „Saved Queries” i węzeł
domeny (`domainDNS`). Kontenerów dobrze znanych nie rozwiązujemy po nazwie (można je przenieść lub przekierować), tylko
przez `<WKGUID=hex,DC=…>` albo atrybuty `wellKnownObjects` / `otherWellKnownObjects` głowy kontekstu nazw domeny; np.
`<WKGUID=a9d1ca15768811d1aded00c04fd8d5cd,DC=contoso,DC=test>` wskazuje kontener Users niezależnie od jego nazwy.

| Węzeł | Klasa | WKGUID | Uwagi |
|---|---|---|---|
| Builtin | `builtinDomain` | — | wbudowane grupy lokalne domeny: Administrators, Account/Server/Print/Backup Operators, Remote Desktop Users, Remote Management Users |
| Computers | `container` | `aa312825768811d1aded00c04fd8d5cd` | **nie jest OU** – nie można podpiąć GPO; przekierowanie `redircmp` |
| Domain Controllers | `organizationalUnit` | `a361b2ffffd211d1aa4b00c04fd7d83a` | z Default Domain Controllers Policy |
| ForeignSecurityPrincipals | `container` | `22b70c67d56e4efb91e9300fca3dc1aa` | obiekty `foreignSecurityPrincipal` nazwane SID-em; kolumna „Readable Name” rozwiązywana przez LSA |
| Managed Service Accounts | `container` | `1EB93889E40C45DF9F0C64D23BBB6237` (w `otherWellKnownObjects`) | MSA, gMSA, w WS2025 także dMSA; ADUC nie ma kreatora tych kont; GUID potwierdzony wyszukiwaniem, brak go w nagłówku |
| Users | `container` | `a9d1ca15768811d1aded00c04fd8d5cd` | przekierowanie `redirusr` |
| System | `container` | `ab1d30f3768811d1aded00c04fd8d5cd` | widok zaawansowany |
| LostAndFound | `lostAndFound` | `ab8153b7768811d1aded00c04fd8d5cd` | widok zaawansowany |
| Infrastructure | `infrastructureUpdate` | `2fbac1870ade11d297c400c04fd8d5cd` | widok zaawansowany |
| Deleted Objects | `container` | `18e2ea80684f11d2b9aa00c04f79f805` | Kosz AD (6.13) |
| Program Data / Microsoft Program Data | `container` | `09460c08ae1e4a4ea0f64aee7daa1e5a` / `f4be92a4c777485e878e9421d53087db` | widok zaawansowany |
| NTDS Quotas | `msDS-QuotaContainer` | `6227f0af1fc2410d8e3bb10615bb5b0f` | widok zaawansowany |
| Keys | `container` | `683A24E2E8164BD3AF86AC3C2CF3F981` (w `otherWellKnownObjects`) | (do weryfikacji w laboratorium) |
| OU organizacji | `organizationalUnit` | — | struktura klienta |

GUID-y z `wellKnownObjects` zweryfikowano z nagłówkiem `ntdsapi.h` (mingw-w64); wartości dla MSA i Keys czytamy na żywo.

### 2.3 Widok zaawansowany

**View › Advanced Features** pokazuje obiekty z `showInAdvancedViewOnly=TRUE`, dodaje karty Object, Security i
Attribute Editor, pozycję menu Name Mappings oraz pole „Protect object from accidental deletion”.

| Kontener | Zawartość |
|---|---|
| LostAndFound (`lostAndFound`) | obiekty osierocone; przydatna kolumna `lastKnownParent` |
| Program Data | `CN=Microsoft`, dane aplikacji |
| System | AdminSDHolder, Policies (`groupPolicyContainer`), Password Settings Container (`msDS-PasswordSettings`, FGPP), MicrosoftDNS, RpcServices, WinsockServices, IP Security, DFSR-GlobalSettings, Domain System Volume, File Replication Service, ComPartitions/ComPartitionSets, WMIPolicy, Meetings, RID Manager$, sekrety `BCKUPKEY_*` |
| NTDS Quotas (`msDS-QuotaContainer`) | obiekty `msDS-QuotaControl` (`msDS-QuotaTrustee`, `msDS-QuotaAmount`) |
| TPM Devices (`msTPM-InformationObjectsContainer`) | `msTPM-InformationObject` z `msTPM-OwnerInformation`; komputer wskazuje go przez `msTPM-TpmInformationForComputer` (schemat WS2012+) |
| Keys | tworzony przez domainprep WS2016; pełna kontrola dla Key Admins i Enterprise Key Admins; obiekty `msDS-KeyCredential` |
| Infrastructure (`infrastructureUpdate`) | `fSMORoleOwner` wskazuje wzorzec infrastruktury |

Osobna opcja **View › Users, Contacts, Groups, and Computers as containers** pokazuje obiekty podrzędne obiektów
liściowych: `msFVE-RecoveryInformation` pod komputerami, `printQueue` pod serwerami wydruku i kontenery urządzeń
Exchange ActiveSync pod użytkownikami.

### 2.4 Menu Widok, opcje filtru i limit 2000

- **View**: Large Icons / Small Icons / List / Detail; „as containers”; Advanced Features; Filter Options; Add/Remove
  Columns; Customize. **Filter Options**: wszystkie typy, wybrane typy (Builtin domain, Computers, Contacts, Groups,
  InetOrgPerson, OUs, Printers, Shared folders, Users…) albo filtr własny (okno w stylu Find z kartą LDAP).
- W tym samym oknie jest **„Maximum number of items displayed per folder” – domyślnie 2000**. Powyżej limitu ADUC
  pokazuje pierwsze N obiektów i proponuje podniesienie limitu albo filtr (Joe Richards opisał 2000 pokazanych z 15 048).
  Sortowanie nagłówkiem działa po stronie klienta, tylko na załadowanych obiektach.
- `msDS-Approx-Immed-Subordinates` (WS2003+, tylko odczyt bazowy, błąd ok. 10%) daje przybliżoną liczbę dzieci – służy
  nam do wyboru strategii ładowania i znacznika „ok. 15 tys.”.

### 2.5 Kolumny i eksport listy

- Domyślnie Name, Type, Description. Add/Remove Columns łączy pozycje wbudowane z `extraColumns` z
  `organizationalUnit-Display` / `container-Display` (zestaw zależy od klasy węzła): m.in. First/Last/Display Name, User
  Logon Name, Pre-Windows 2000 Logon Name, E-Mail Address, Office, Business Phone, Job Title, Department, Company, City,
  State, Country/Region, Managed By, Modified, Created, Last Known Parent. Wybór zapisuje się w pliku konsoli.
- Dowolny atrybut wymaga edycji `extraColumns` w całym lesie, a wypełnione `extraColumns` **zastępuje** listę domyślną u
  wszystkich administratorów. U nas `extraColumns` jest tylko podpowiedzią katalogu kolumn (nigdy go nie zapisujemy),
  kolumną może być każdy atrybut schematu, a zestaw kolumn to preferencja użytkownika.
- **Action › Export List** zapisuje widoczne kolumny (opcjonalnie tylko zaznaczone wiersze) do pliku tekstowego z
  tabulatorami lub przecinkami, w ANSI albo Unicode.

### 2.6 Znajdź (Find), ANR i zapisane zapytania

- **Find**: Users, Contacts, and Groups; Computers; Printers; Shared Folders; Organizational Units; Custom Search;
  Common Queries. Zakres „In”: domena, OU albo **Entire Directory**, czyli Global Catalog (3268), który widzi tylko
  atrybuty z częściowego zestawu (PAS). Karta Advanced: pole wg klasy, warunek (Starts with / Ends with / Is exactly /
  Is not / Present / Not present), wartość; Custom Search przyjmuje surowy filtr LDAP.
- **Common Queries**: nazwa/opis zaczyna się od lub równa się; „Disabled accounts”; „Non expiring passwords”; „Days
  since last logon” (30–180 dni, z `lastLogonTimestamp`, który spóźnia się o 9–14 dni).
- Wyniki otwierają się w osobnym oknie. **Znany błąd:** obiekty otwarte z wyników Find nie mają karty Attribute Editor
  ani innych kart zaawansowanych; według AskDS nie zostanie to poprawione, bo ADUC jest przestarzały.
- **ANR** (MS-ADTS 3.1.1.3.1.3.4): `(anr=wartość)` obejmuje atrybuty z `fANR` (0x4) i indeksem (`fATTINDEX`, 0x1) w
  `searchFlags`, m.in. `displayName`, `givenName`, `sn`, `sAMAccountName`, `name`, `physicalDeliveryOfficeName`,
  `proxyAddresses`, `legacyExchangeDN`, `mailNickname`, `msDS-AdditionalSamAccountName`, `msDS-Phonetic*` (dokładny
  zestaw domyślny – do weryfikacji w laboratorium; aplikacja wyznacza go ze schematu). Wartość ze spacją jest dzielona
  na pierwszej spacji i sprawdzana jako `givenName`/`sn` w obu kolejnościach; `anr==wartość` to dopasowanie dokładne.
- **Saved Queries**: foldery, nazwa, opis, korzeń, „Include subcontainers”, definicja z okna Find; wykonywane na żywo,
  bez buforowania wyników; zapisane w pliku konsoli użytkownika i współdzielone przez Export/Import Query Definition
  (XML). Kreator łączy warunki tylko przez AND. Import tych plików XML (robi to też NetIQ DRA) ułatwi migrację.

### 2.7 Akcje kontekstowe według klasy

| Obiekt | Akcje w menu kontekstowym |
|---|---|
| Domena | Delegate Control, Find, Change Domain, Change Domain Controller, Raise domain functional level, Operations Masters, New, All Tasks, View, Refresh, Export List, Properties; z rozszerzeniem BitLocker także „Find BitLocker recovery password” |
| OU | Delegate Control, Move, Find, New ›, All Tasks, View, Cut, Delete, Rename, Refresh, Export List, Properties |
| User / inetOrgPerson | Copy…, Add to a group…, Name Mappings… (tylko widok zaawansowany), Disable/Enable Account, Reset Password…, Move…, Open Home Page (`wWWHomePage`), Send Mail (`mail`), All Tasks › Resultant Set of Policy (Planning/Logging), Cut, Delete, Rename, Properties |
| Computer | Add to a group…, Disable Account, Reset Account, Move…, Manage (`compmgmt.msc /computer=<dNSHostName>`), All Tasks, Cut, Delete, Properties |
| Group | Add to a group…, Move…, Send Mail, All Tasks, Cut, Delete, Rename, Properties |
| Contact | Add to a group, Move, Send Mail, Cut, Delete, Rename, Properties |
| Kilka obiektów | Add to a group, Enable/Disable, Move, Delete, Cut, Send Mail, Properties (arkusz dla wielu obiektów) |

- **Rename** zmienia RDN przez ModifyDN; okno użytkownika edytuje też `givenName`, `sn`, `displayName`, UPN i
  `sAMAccountName`.
- **Move** to ModifyDN z `newSuperior`, tylko w obrębie domeny. Przenoszenie między domenami wymaga ADMT.
- Usunięcie kontenera z dziećmi używa kontrolki Tree Delete (`1.2.840.113556.1.4.805`).
- Przeciąganie myszą jest dozwolone; od WS2008 pokazuje ostrzeżenie, które użytkownik może wyłączyć.

### 2.8 Kreatory „New”

Menu New na OU: Computer, Contact, Group, InetOrgPerson, msDS-KeyCredential, msDS-ResourcePropertyList,
msDS-ShadowPrincipalContainer, msImaging-PSPs, MSMQ Queue Alias, Printer, User, Shared Folder, Organizational Unit.
Rzadsze klasy obsługuje generyczny kreator pytający o atrybuty. Lista zależy od schematu, display specifiers i
`possSuperiors` (spisana z pamięci – do weryfikacji w laboratorium).

| Kreator | Pola i zachowanie |
|---|---|
| User | Strona 1: First name (`givenName`), Initials (`initials`), Last name (`sn`), Full name (`cn`/`name`, zasila też `displayName` wg `createDialog` z `user-Display`, np. `%<givenName> %<initials>. %<sn>`; obsługiwane są tylko tokeny `%<givenName>`, `%<initials>`, `%<sn>`), User logon name (prefiks UPN + sufiks z nazwy DNS domeny i `uPNSuffixes` na `CN=Partitions`), nazwa sprzed Windows 2000 (`NETBIOS\sAMAccountName`, wypełniana z prefiksu UPN, maks. 20 znaków). Strona 2: hasło ×2, User must change password, User cannot change password, Password never expires, Account is disabled. Strona 3: podsumowanie. Wewnętrznie obiekt powstaje wyłączony z `PASSWD_NOTREQD`, potem ustawiane jest hasło, a na końcu UAC = 512 lub 514. |
| InetOrgPerson | Ten sam kreator, klasa `inetOrgPerson`; można go ukryć w menu (KB311555) |
| Computer | Computer name (`cn`), nazwa sprzed Windows 2000 (`sAMAccountName` z `$`, maks. 15 znaków), „The following user or group can join this computer to a domain” (domyślnie Domain Admins; „Change…” nadaje temu podmiotowi ACE potrzebne do dołączenia), opcja „Assign this computer account as a pre-Windows 2000 computer” (hasło z nazwy, blokowane na DC WS2025). Domyślny UAC 4128 (0x1000 + 0x20 `PASSWD_NOTREQD`). Dawniej strona RIS/WDS (`netbootGUID`). |
| Group | nazwa (`cn`), nazwa sprzed Windows 2000 (`sAMAccountName`), zakres (Domain local / Global / Universal), typ (Security / Distribution) |
| Contact | First, Initials, Last, Full name, Display name |
| Organizational Unit | nazwa + „Protect container from accidental deletion” – zaznaczone domyślnie od WS2008 |
| Printer | ścieżka UNC udziału drukarki sprzed Windows 2000 → `printQueue` z `uNCName` |
| Shared Folder | nazwa + ścieżka sieciowa → obiekt `volume` z `uNCName`; właściwości Description, Keywords, Managed By |

### 2.9 Kopiowanie użytkownika (Copy…)

Copy otwiera kreator New User z wypełnionymi polami. Kopiowane są atrybuty z flagą `fCOPY` (0x10) w `searchFlags`; w
schemacie bazowym: `accountExpires`, `assistant`, `codePage`, `company`, `countryCode`, `c`, `co`, `department`,
`division`, `employeeType`, `homeDirectory`, `homeDrive`, `localeID`, `l`, `logonHours`, `logonWorkstation`,
`userWorkstations`, `otherLoginWorkstations`, `manager`, `maxStorage`, `msNPAllowDialin` i atrybuty RADIUS/RAS,
`postalAddress`, `postalCode`, `postOfficeBox`, `preferredOU`, `primaryGroupID`, `profilePath`, `scriptPath`,
`showInAddressBook`, `st`, `street`, `userAccountControl`. Członkostwo w grupach kopiuje logika przystawki. **Nie** są
kopiowane `title`, `description`, `physicalDeliveryOfficeName`, telefony i `streetAddress` (pole Street). Ścieżki
kończące się nazwą użytkownika źródłowego są zamieniane na `%username%`. Administrator schematu może dodać `fCOPY`
innym atrybutom (np. `extensionAttribute*`), więc zestaw czytamy ze schematu na żywo.

### 2.10 Karty właściwości użytkownika – mapowanie pól na atrybuty

| Karta | Pole | Atrybut(y) | Uwagi |
|---|---|---|---|
| General | First name / Initials / Last name | `givenName` / `initials` / `sn` | nagłówek karty pokazuje `cn` |
| | Display name | `displayName` | |
| | Description | `description` | w schemacie wielowartościowy, ale na klasach SAM (user, group, computer) tylko jedna wartość |
| | Office | `physicalDeliveryOfficeName` | |
| | Telephone number (Other…) | `telephoneNumber` (`otherTelephone`) | |
| | E-mail | `mail` | |
| | Web page (Other…) | `wWWHomePage` (`url`) | |
| Address | Street / P.O. Box / City / State / ZIP | `streetAddress` / `postOfficeBox` / `l` / `st` / `postalCode` | |
| | Country/region | `c` + `co` + `countryCode` | zawsze trzy naraz, np. `PL` / `Poland` / `616` |
| Account | User logon name | `userPrincipalName` | prefiks + sufiks z listy |
| | Pre-Windows 2000 | `sAMAccountName` | |
| | Logon Hours | `logonHours` | 21 bajtów = 168 bitów w UTC od niedzieli 00:00 UTC; wyświetlane w czasie lokalnym |
| | Log On To | `userWorkstations` | nazwy NetBIOS oddzielone przecinkami |
| | Unlock account | `lockoutTime` | zapis tylko `0` |
| | Account options | `userAccountControl`, `pwdLastSet`, ACE, `msDS-SupportedEncryptionTypes` | szczegóły w tabeli poniżej |
| | Account expires | `accountExpires` | Never / End of *data* |
| Profile | Profile path / Logon script | `profilePath` / `scriptPath` | |
| | Home folder | `homeDirectory` (+ `homeDrive`) | „Local path” albo „Connect *litera* To” |
| Telephones | Home / Pager / Mobile / Fax / IP phone | `homePhone`, `pager`, `mobile`, `facsimileTelephoneNumber`, `ipPhone` + listy `other*` | |
| | Notes | `info` | |
| Organization | Job Title / Department / Company | `title` / `department` / `company` | |
| | Manager | `manager` | DN |
| | Direct reports | `directReports` | łącze zwrotne, tylko odczyt |
| Member Of | Member of | `memberOf` | łącze zwrotne; edycja zapisuje `member` w grupie |
| | Primary group | `primaryGroupID` | RID; „Set Primary Group” wymaga wcześniejszego członkostwa |
| Dial-in | Network Access Permission | `msNPAllowDialin` | Allow / Deny / brak = „Control access through NPS Network Policy” |
| | Caller ID, Callback, Static IP, Routes | `msNPCallingStationID`, `msRADIUSServiceType`, `msRADIUSCallbackNumber`, `msRADIUSFramedIPAddress`, `msRADIUS-FramedIpv6Prefix`, `msRADIUS-FramedInterfaceId`, `msRADIUSFramedRoute` | plus kopie `msRAS-Saved*` |
| Environment, Sessions, Remote control, RDS Profile | program startowy, mapowanie urządzeń, limity sesji, zdalna kontrola, profil i katalog TS, zakaz logowania do RDSH | binarny `userParameters` (format TSUserEx, `tsuserex.dll`) | **nie** w atrybutach `msTS*` |
| COM+ | Partition set | `msCOM-UserPartitionSetLink` | |
| Published Certificates | certyfikaty | `userCertificate` | dodaj z magazynu lub pliku, usuń, zapisz do pliku |
| Object (zaawansowany) | | `canonicalName`, `objectClass`, `whenCreated`, `whenChanged`, `uSNCreated`, `uSNChanged` | plus pole „Protect…” (ACE) |
| Security (zaawansowany) | ACL | `nTSecurityDescriptor` | |
| Attribute Editor (zaawansowany) | wszystkie atrybuty | — | edytory zależne od składni; filtry „tylko z wartościami” i „tylko zapisywalne” |

**Opcje konta (karta Account):**

| Opcja | Rzeczywisty zapis |
|---|---|
| User must change password at next logon | `pwdLastSet = 0` (wyczyszczenie: `-1`); nie działa, gdy ustawione jest 0x10000 |
| User cannot change password | dwa wpisy Deny dla Everyone (S-1-1-0) i SELF (S-1-5-10) na prawie Change Password `ab721a53-1e2f-11d0-9819-00aa0040529b`; bit UAC 0x40 jest ignorowany przy zapisie i nigdy nie występuje przy odczycie |
| Password never expires | UAC 0x10000 |
| Store password using reversible encryption | UAC 0x80 |
| Account is disabled | UAC 0x2 |
| Smart card is required for interactive logon | UAC 0x40000 |
| Account is sensitive and cannot be delegated | UAC 0x100000 |
| Use only Kerberos DES encryption types | UAC 0x200000 |
| This account supports Kerberos AES 128 / 256 | `msDS-SupportedEncryptionTypes` 0x8 / 0x10 |
| Do not require Kerberos preauthentication | UAC 0x400000 |

Pułapki: `logonHours` jest w UTC (naiwne wyświetlenie przesuwa siatkę o strefę, a przy zmianie czasu mapa bitowa się
nie przesuwa); w `accountExpires` „End of 31.12.2026” zapisuje 2027-01-01 00:00 czasu lokalnego w UTC – źródło błędów o
jeden dzień. „Password Replication” z listy zadania nie jest kartą użytkownika, tylko kartą komputerów RODC (2.11).

**Name Mappings** (widok zaawansowany) ma karty „X.509 Certificates” (plik `.cer`, „Use Issuer…”/„Use Subject…”) i
„Kerberos Names” (`principal@REALM`); wartości trafiają do `altSecurityIdentities`. KB5014754 uznaje
`X509IssuerSubject`, `X509SubjectOnly` i `X509RFC822` za słabe, a `X509IssuerSerialNumber`
(`X509:<I>issuer<SR>odwrócony-numer`), `X509SKI` i `X509SHA1PublicKey` za silne; przy pełnym wymuszeniu słabe
mapowania nie pozwalają zalogować się certyfikatem.

### 2.11 Karty komputera

| Karta | Pola → atrybuty |
|---|---|
| General | Computer name (pre-2000) = `sAMAccountName`; DNS name = `dNSHostName`; rola z `userAccountControl` (0x1000 stacja/serwer, 0x2000 `SERVER_TRUST_ACCOUNT` = DC, 0x04000000 `PARTIAL_SECRETS_ACCOUNT` = RODC); `description`. Dla DC także typ (GC) i przycisk NTDS Settings (do weryfikacji w laboratorium) |
| Operating System | `operatingSystem`, `operatingSystemVersion`, `operatingSystemServicePack` – tylko odczyt |
| Member Of | `memberOf` + `primaryGroupID` (515 Domain Computers, 516 DC, 521 RODC) |
| Delegation (konta z SPN) | „Do not trust”; „Trust for delegation to any service (Kerberos only)” = UAC `TRUSTED_FOR_DELEGATION` 0x80000; „…to specified services only” z „Use Kerberos only” (tylko `msDS-AllowedToDelegateTo`) albo „Use any authentication protocol” (dodatkowo `TRUSTED_TO_AUTH_FOR_DELEGATION` 0x1000000). Delegacja ograniczona zasobowo (`msDS-AllowedToActOnBehalfOfOtherIdentity`) **nie ma interfejsu w ADUC** |
| Location | `location` |
| Managed By | `managedBy` (+ biuro, adres, telefony zarządcy tylko do odczytu) |
| Dial-in | jak u użytkownika |
| LAPS (Windows LAPS) | `msLAPS-PasswordExpirationTime` (FILETIME UTC) z „Expire now”; bieżące hasło z `msLAPS-Password` (JSON `{n, t, p}`) albo odszyfrowane `msLAPS-EncryptedPassword`. Historia (`msLAPS-EncryptedPasswordHistory`) i DSRM (`msLAPS-EncryptedDSRMPassword`) tylko przez `Get-LapsADPassword`. Odczyt wymaga praw do atrybutu poufnego i – dla haseł szyfrowanych – członkostwa w grupie uprawnionych do odszyfrowania (DPAPI-NG na komputerze w domenie). Dawny LAPS (`ms-Mcs-AdmPwd`, `ms-Mcs-AdmPwdExpirationTime`) nie ma karty |
| BitLocker Recovery | obiekty podrzędne `msFVE-RecoveryInformation` (`msFVE-RecoveryPassword`, `msFVE-RecoveryGuid`, `msFVE-VolumeGuid`); w menu domeny „Find BitLocker recovery password” po pierwszych 8 znakach Password ID (filtr – do weryfikacji w laboratorium) |
| Password Replication Policy (tylko RODC) | listy Allowed `msDS-RevealOnDemandGroup` i Denied `msDS-NeverRevealGroup`; „Advanced” pokazuje `msDS-RevealedList` i `msDS-AuthenticatedToAccountlist` oraz „Prepopulate Passwords” |
| Object / Security / Attribute Editor | jak u użytkownika |

Akcja **Reset Account** ustawiała dawniej hasło komputera na nazwę małymi literami (kanał bezpieczny zerwany do
ponownego dołączenia). DC WS2025 odmawiają hasła z nazwy (zasada „Domain controller: Refuse setting default machine
account password”, respektowana przez ADUC, ADAC, `net computer` i `dsmod`), więc tej akcji nie odtwarzamy (9.6).

### 2.12 Karty grupy

| Karta | Pola → atrybuty |
|---|---|
| General | `cn`, `sAMAccountName`, `description`, `mail`; zakres i typ w `groupType`; Notes = `info` (strona mapowania Microsoftu podaje `comment` – do weryfikacji w laboratorium) |
| Members | `member`; dla dużych grup ranged retrieval (`MaxValRange` 1500 lub 5000) |
| Member Of | `memberOf` |
| Managed By | `managedBy` + pole „Manager can update membership list” = ACE Allow WriteProperty na `member` (`bf9679c0-0de6-11d0-a285-00aa003049e2`) dla zarządcy |
| Object / Security / Attribute Editor | jak u użytkownika |

`groupType`: 0x2 globalna, 0x4 lokalna domeny, 0x8 uniwersalna, 0x1 systemowa, 0x80000000 zabezpieczeń – np. globalna
zabezpieczeń `-2147483646`, lokalna domeny zabezpieczeń `-2147483644`, uniwersalna zabezpieczeń `-2147483640`,
dystrybucyjne `2`/`4`/`8`. Zmiana zakresu: Global → Universal tylko, gdy grupa nie należy do innej grupy globalnej;
Domain local → Universal tylko bez członków domain local; Universal → Global lub Domain local tylko, gdy pozwala na to
członkostwo.

### 2.13 OU, kontakt i domena

- **OU:** General (`description`, `street`, `l`, `st`, `postalCode`, `c`/`co`/`countryCode`), Managed By
  (`managedBy`), COM+, Object (z polem Protect), Security, Attribute Editor. Karty Group Policy już nie ma (przeniesiono ją
  do GPMC).
- **Contact:** General, Address, Telephones, Organization, Member Of, Object, Security, Attribute Editor.
- **Domena:** General (nazwa NetBIOS, opis, poziomy funkcjonalne domeny i lasu – tylko odczyt), Managed By, Object,
  Security, Attribute Editor.

### 2.14 Pola, które nie są prostymi atrybutami

| Pole w ADUC | Rzeczywisty zapis | Konsekwencja dla AD_MNG |
|---|---|---|
| User cannot change password | 2 × Deny ACE (Everyone, SELF) na prawie `ab721a53-…` | odczyt DACL z kontrolką SD Flags; nie używać UAC 0x40 |
| Protect object from accidental deletion | Deny Everyone `DELETE` (0x10000) + `DELETE_TREE` (0x40) na obiekcie oraz Deny Everyone `DELETE_CHILD` (0x2) na rodzicu; SDDL `(D;;SDDT;;;WD)` i `(D;;DC;;;WD)` (zapis SDDL – do weryfikacji w laboratorium) | blokuje też przeniesienie; jawny Deny zatrzymuje nawet Domain Admins; stan odczytujemy z DACL |
| Manager can update membership list | Allow WriteProperty na `member` dla podmiotu z `managedBy` | edycja ACE grupy przy zmianie zarządcy |
| Environment / Sessions / Remote control / RDS Profile | binarny `userParameters` (TSUserEx) | własny koder formatu, karty oznaczone jako zaawansowane |
| Country/region | `c` (ISO 3166 alfa-2) + `co` (nazwa) + `countryCode` (ISO liczbowy) | zapis tylko trójką; sam `c` daje w ADUC puste lub nieaktualne pole |
| Locked out | stan z `msDS-User-Account-Control-Computed` 0x10; odblokowanie `lockoutTime = 0` | bit LOCKOUT w UAC nie jest utrzymywany; `lockoutTime > 0` nie znaczy „zablokowane” |
| Primary group | `primaryGroupID` (RID z `primaryGroupToken` grupy) | nie występuje w `member`/`memberOf`; wymaga wcześniejszego członkostwa |

### 2.15 Kreator delegowania i operacje na domenie

- **Delegate Control** (sterowany `%windir%\inf\delegwiz.inf`; Microsoft opublikował wersję z ponad 70 zadaniami):
  wybór użytkowników lub grup, potem zadania typowe albo własne (zakres i uprawnienia). Typowe zadania na OU:
  zarządzanie kontami, reset hasła z wymuszeniem zmiany, odczyt informacji o użytkowniku, zarządzanie grupami i ich
  członkostwem, łącza GPO, RSoP, odpowiedniki dla inetOrgPerson; „Join a computer to the domain” na poziomie domeny
  (pełna lista – do weryfikacji w laboratorium). Kreator dopisuje dziedziczone ACE do OU i **nie umie pokazać ani cofnąć
  delegacji** – sprzątanie odbywa się na karcie Security.
- **Change Domain / Change Domain Controller** („Any writable DC” albo DC z listy: nazwa, lokacja, typ, wersja, stan);
  wszystkie operacje idą do jednego DC, którego nazwę pokazuje korzeń drzewa.
- **Operations Masters**: RID, PDC, Infrastructure (`fSMORoleOwner` na `CN=RID Manager$,CN=System`, głowie NC domeny i
  `CN=Infrastructure`); „Change…” przenosi rolę na związany DC zapisami RootDSE `becomeRidMaster`, `becomePdc`,
  `becomeInfrastructureMaster`. Wzorzec schematu jest w `schmmgmt.msc`, wzorzec nazw domen w `domain.msc`.
- **Raise domain functional level** zapisuje `msDS-Behavior-Version` na głowie NC przez PDC; operacja jest
  nieodwracalna i wymaga wszystkich DC na danym poziomie (blokuje ją nawet nieużywane konto RODC niższej wersji). Poziom
  lasu podnosi się w `domain.msc` (`msDS-Behavior-Version` na `CN=Partitions`).

| `msDS-Behavior-Version` | 0 | 2 | 3 | 4 | 5 | 6 | 7 | 10 |
|---|---|---|---|---|---|---|---|---|
| Poziom | 2000 | 2003 | 2008 | 2008 R2 | 2012 | 2012 R2 | 2016 (także 2019 i 2022) | 2025 (strony 32k, dMSA) |

### 2.16 Display specifiers – źródło etykiet ADUC

`CN=DisplaySpecifiers,CN=Configuration,<las>` ma kontener na każdy język: `CN=409` (en-US) i `CN=415` (pl-PL); gdy brak
kontenera, ADUC używa `409`. Obiekt `<klasa>-Display` zawiera `classDisplayName`, `attributeDisplayNames` (pary
`atrybut,Etykieta`), `extraColumns`, `createDialog`, `treatAsLeaf`, `iconPath`, `adminContextMenu`,
`adminPropertyPages`, `adminMultiselectPropertyPages`, `shellPropertyPages` (CLSID pomijamy). Czytamy je na żywo do
pamięci sesji i nigdy nie zapisujemy (to konfiguracja całego lasu), więc polscy administratorzy zobaczą nazwy jak w
polskim ADUC. Laboratorium Samba nie ma `CN=415` ([ADR-0007](adr/0007-laboratorium-samba.md)) – potrzebne są też
własne tłumaczenia.

### 2.17 Edycja wielu obiektów w ADUC

Zaznaczenie N użytkowników i Properties otwiera okrojony arkusz, w którym każde pole ma pole wyboru „zastosuj”;
zapisywane są tylko zaznaczone pola. Zestaw z praktyki (Microsoft go już nie dokumentuje): **General** – Description,
Office, Telephone number, Fax, Web page, E-mail; **Address** – wszystkie pola; **Account** – sufiks UPN (bez prefiksu),
Logon hours, Log On To, Unlock, opcje konta, Account expires; **Profile** – ścieżki z `%username%` (tworzy też katalogi
domowe); **Organization** – Title, Department, Company, Manager. Brak: nazw, `displayName`, karty Telephones, Member Of
(zamiast tego „Add to a group”), Dial-in, RDS i Attribute Editor; rozszerzenia mogą dodać strony przez
`adminMultiselectPropertyPages` (np. Exchange). Dla komputerów, grup i kontaktów arkusz jest szczątkowy (`description`,
`managedBy` – do weryfikacji w laboratorium). Zapis idzie obiekt po obiekcie: bez podglądu, różnic, raportu per obiekt,
wycofania i CSV.

### 2.18 Czego ADUC nie potrafi

Kosz AD (przeglądanie i przywracanie – jest w ADAC i `Restore-ADObject`); interfejs FGPP; Authentication Policies i
Silos; Dynamic Access Control; tworzenie gMSA i dMSA; RBCD; historia haseł LAPS; członkostwo efektywne i zagnieżdżone
(`tokenGroups`); rzeczywiste ostatnie logowanie (`lastLogon` jest per DC i niereplikowany, `lastLogonTimestamp`
spóźnia się 9–14 dni); data wygaśnięcia hasła (`msDS-UserPasswordExpiryTimeComputed`); stan blokady na wszystkich DC
(dawniej `LockoutStatus.exe`); historia „kto co zmienił” (jest tylko `msDS-ReplAttributeMetaData` /
`repadmin /showobjmeta`); historia poleceń PowerShell jak w ADAC; wyszukiwanie globalne w wielu domenach.

---

## 3. Bolączki ADUC i jak je adresujemy

| # | Bolączka | Przyczyna | Jak ją adresujemy |
|---|---|---|---|
| 1 | „Not Responding” przy dużych OU | synchroniczne wyliczanie na wątku UI konsoli MMC | asynchroniczny, anulowalny dostęp do LDAP; strony dopisywane do listy z priorytetem `Background`; wątek UI tylko wiąże dane; Esc anuluje (6.2) |
| 2 | Limit 2000 obiektów i mylące liczniki (2000 z 15 048) | limit w Filter Options, liczenie tylko załadowanych | brak limitu: stronicowanie, a powyżej ok. 2000 obiektów VLV; liczba przybliżona z `msDS-Approx-Immed-Subordinates`; znacznik „stan na *godzina*” (6.4–6.5) |
| 3 | Sortowanie i filtr tylko po załadowanych obiektach | sortowanie po stronie klienta | sortowanie po stronie serwera po atrybucie indeksowanym, filtry jako LDAP; sortowanie lokalne (`pl-PL`) tylko dla pełnych, małych zbiorów (7.4) |
| 4 | Modalne okna o stałym rozmiarze, mała lista członków, jedno okno na obiekt, brak odświeżenia otwartego arkusza | model arkuszy MMC | niemodalne, skalowalne karty jako zakładki lub okna; panel szczegółów; przycisk odśwież; wykrywanie równoległej zmiany przez `whenChanged`/`uSNChanged` (7.6, 9.4) |
| 5 | Brak wyszukiwania w trakcie pisania; Find w osobnym oknie; w wynikach Find brak Attribute Editor (błąd „won't fix”); „Check Names” w selektorze obiektów | interfejs dsquery | globalne pole ANR (Ctrl+E) z opóźnieniem i anulowaniem; wyniki otwierają pełny arkusz; własny selektor obiektów oparty na ANR (6.8) |
| 6 | „Entire Directory” widzi tylko atrybuty PAS | wyszukiwanie w GC | jawny wybór zakresu (kontener / domena / GC); kolumny spoza PAS wyszarzone; zapis kierowany do DC domeny obiektu (6.12) |
| 7 | Słaba edycja zbiorcza: stały podzbiór pól użytkownika, bez podglądu, różnic, CSV i raportu | arkusz „Properties for multiple items” | jeden silnik planu zmian (sekcja 9) |
| 8 | Brak cofania, brak Kosza, przypadkowe przeciągnięcia | brak dziennika zmian; Kosz tylko w ADAC | dziennik cofania w pamięci; widok Kosza z przywracaniem drzewa (6.13); potwierdzenie przeciągania i kontrola ochrony przed usunięciem |
| 9 | Ręczne F5, brak powiadomień | brak subskrypcji zmian | powiadomienia LDAP dla widocznego kontenera (maks. 5 na połączenie), zapasowo odpytywanie `highestCommittedUSN` (6.11) |
| 10 | Praca z jednym DC: stan blokady tylko z tego DC | przypięcie przystawki | DC przypięty świadomie i widoczny; stan blokady i ostatnie logowanie ze wszystkich DC na żądanie |
| 11 | Brak przełączania poświadczeń (`runas /netonly`, osobna MMC) | konsola dziedziczy tożsamość procesu | profile połączeń w aplikacji, `NEW_CREDENTIALS` w procesie, kilka okien z różnymi tożsamościami i kolorowym paskiem (8.3) |
| 12 | Jedna domena na przystawkę | model przystawki | korzenie dla wszystkich domen z `crossRef`, osobna sesja na domenę, wyszukiwanie w GC |
| 13 | Kolumny wspólne dla lasu (`extraColumns`), zapytania w plikach `.msc` | model display specifiers i konsoli | kolumny i zapytania jako preferencje użytkownika; import XML z ADUC; konfigurowalne widoki z eksportem dla zespołu (7.6) |
| 14 | Rozmyty obraz na wysokim DPI i przez RDP, brak trybu ciemnego, słaba dostępność | skalowanie bitmap w MMC | PerMonitorV2, Fluent jasny/ciemny, UI Automation, wysoki kontrast, skala tekstu (7.8) |
| 15 | Zależność od RSAT (instalacja jako administrator, znikanie po aktualizacji 24H2, ARM64 dopiero od 2026) | dystrybucja jako funkcja Windows | samodzielna aplikacja .NET 10 w MSI; potrzebne tylko porty LDAP 389/636 i GC 3268/3269 |
| 16 | Delegacji nie da się przejrzeć ani cofnąć | kreator tylko dopisuje ACE | przegląd niestandardowych ACE per OU (w stylu adeleg) z możliwością wycofania – faza późniejsza |
| 17 | Brak odpowiedzi na „kto i kiedy zmienił” | brak historii | karta Historia z metadanych replikacji (kiedy, na którym DC, wersja – bez autora); opcjonalnie zdarzenie 5136 z DC (8.8) |

---

## 4. ADAC, Windows Admin Center, Entra admin center, PowerShell AD

### 4.1 Active Directory Administrative Center (dsac.exe)

ADAC wprowadził większość „nowoczesnych” pomysłów wartych zachowania, ale działa przez moduł PowerShell AD i usługę
AD Web Services (ADWS, TCP 9389), przez co jest odbierany jako wolniejszy od ADUC.

| Kopiujemy (i poprawiamy) | Unikamy |
|---|---|
| Nawigacja w dwóch trybach: drzewo (jak ADUC) i lista z węzłami dodanymi przez użytkownika (przypięcia: zmiana nazwy, kolejność, węzły z domen zaufanych) oraz historią 3 ostatnich kontenerów na węzeł | Zależność od ADWS: gdy usługa nie działa lub port jest zablokowany, ADAC i moduł AD przestają działać |
| Pasek adresu przyjmujący DN – u nas także dla kontekstu Configuration, gdy operator ma prawa (ADAC widzi tylko NC domen) | Limit listy 20 000 obiektów na kontener (maks. 100 000 w Manage › Management List Options) w połączeniu z filtrowaniem po stronie klienta – obiekty powyżej limitu po cichu znikają |
| Global Search z kreatorem kryteriów i „Convert to LDAP”; zapisane zapytania przechowują kryteria, sortowanie i kolumny | `MaxGroupOrMemberEntries` = 5000 w ADWS: `Get-ADGroupMember` zwraca błąd dla większych grup, a zmiana limitu wymaga edycji pliku konfiguracyjnego na każdym DC |
| Attribute Editor dostępny z wyników wyszukiwania (sekcja „Extensions”) | Inne poświadczenia tylko przez `runas /user:… dsac` |
| Panel Windows PowerShell History: domyślnie tylko polecenia zmieniające dane, „Show All” dla `Get-*`, „Start Task”/„End Task” grupujące polecenia pod notatką, wyszukiwanie i kopiowanie | Kosz AD bez przywracania poddrzewa jednym ruchem: najpierw rodzice, odświeżenie, potem dzieci poziom po poziomie |
| Widok Kosza AD (Restore, Restore To, Locate Parent; filtry „When deleted”, „Last known parent”, nazwa, typ, dział, UPN) | Kosz tylko dla partycji domeny (Configuration i DNS wymagają `Restore-ADObject`) |
| Widok FGPP (Password Settings Container, „View Resultant Password Settings”) | |
| Edycja wielu obiektów z polem „zastosuj” przy każdym polu | |

Węzły Dynamic Access Control oraz Authentication Policies/Silos są niszowe; przewidujemy je późno i najpierw tylko do
odczytu. Do diagnozy samego ADAC przydaje się `dsac.exe.config` z `DsacLogLevel` (na poziomie Info dziennik pokazuje,
z którymi DC się łączył i jakie polecenia wykonał).

### 4.2 Windows Admin Center – rozszerzenie Active Directory

Rozszerzenie pojawiło się w WAC Preview 1903 (2019) i według Microsoft Learn nadal jest „early preview” (bez
aktualizacji w latach 2024–2026). Działa tylko po połączeniu z DC, przez bramę WAC. Oferuje tworzenie użytkowników i
grup, wyszukiwanie, panel szczegółów, zbiorcze Disable/Enable/Remove, reset hasła, podstawowe właściwości i delegację
Kerberos do jednej maszyny; członków grup zmienia się pojedynczo (wczesne wersje zwracały maks. 10 wyników na typ). WAC
2410 przeszedł na .NET 8 i zepsuł starsze rozszerzenia. **Kopiujemy** panel szczegółów obok siatki i model „Manage as”
(domyślnie bieżąca tożsamość, opcjonalnie inne poświadczenia). **Unikamy** bramy (gdy przechodzą przez nią
poświadczenia administratorów domeny, staje się zasobem Tier-0), członkostwa „po jednym” i traktowania WAC jako
platformy.

### 4.3 Microsoft Entra admin center

| Kopiujemy | Unikamy |
|---|---|
| Wybór kolumn z przeciąganiem nagłówków; dowolny atrybut jako kolumna (np. `employeeId`, atrybuty rozszerzeń, `manager`) | Limitu 60 użytkowników w edycji zbiorczej i stałej listy ok. 12 pól (lista – do weryfikacji w laboratorium) |
| „Copy link to current view” – u nas definicja widoku jako JSON lub link do aplikacji (pasuje do konfigurowalnych widoków klienta) | Zadań asynchronicznych z limitem 1 godziny (duże zadania kończą się błędem) |
| Przewijanie z doładowywaniem zamiast przycisku „Load more” | Liczników „nie w czasie rzeczywistym” odświeżanych co kilka godzin |
| Osobne zakładki członków bezpośrednich i wszystkich (przechodnich), każda z własnym filtrem; obie liczby obok siebie | Domyślnego trybu „contains” – w AD oznacza to `(attr=*x*)` bez indeksu, kosztowne na DC |
| Szablon CSV do pobrania z nagłówkami w stylu `Name [displayName] Required`, walidacja pliku przed wysłaniem, strona wyników z liczbą sukcesów i błędów, wyniki per wiersz do pobrania | |
| Filtry dziennika: kategoria, aktywność, zakres dat, cel, inicjator; pobieranie danych samo jest audytowane | |

### 4.4 Moduł PowerShell ActiveDirectory

Aplikacja nie uruchamia PowerShella – generuje polecenia **równoważne** faktycznie wykonanej operacji LDAP, oznaczone
jako „równoważne”, a nie „wykonane”.

- `Get-AD*`: `-Identity` (DN, GUID, SID, `sAMAccountName`), `-Filter`, `-LDAPFilter`, `-SearchBase`, `-SearchScope`,
  `-ResultPageSize` (domyślnie 256), `-Properties`, `-Server`, `-Credential`. Operatory filtra: `-band` →
  `:1.2.840.113556.1.4.803:=`, `-bor` → `.804`, `-recursivematch` → `.1941`; jedyny symbol wieloznaczny to `*`.
- `Set-ADUser -Add/-Remove/-Replace` (tablice skrótów z nazwami LDAP) i `-Clear` wykonują się w kolejności Remove, Add,
  Replace, Clear; pusta wartość w `-Add`/`-Replace` to błąd (puste komórki CSV → `-Clear`); `-Instance` zapisuje tylko
  różnice. `Move-ADObject -TargetServer` między domenami wymaga wzorca RID w obu domenach i nie działa wobec RODC; OU
  chronionej przed usunięciem nie da się przenieść.
- Generowane skrypty: `-Server <przypięty DC>`, `-Identity <objectGUID>`, `-WhatIf`, nigdy hasła
  (`Read-Host -AsSecureString`), a dla dużych grup `(Get-ADGroup -Properties member).member` zamiast
  `Get-ADGroupMember` (limit ADWS). Mapowanie operacji na polecenia – 9.12.

### 4.5 Szybkie filtry w stylu Search-ADAccount

`Search-ADAccount` ma przełączniki `-AccountDisabled`, `-AccountExpired`, `-AccountExpiring`, `-AccountInactive`,
`-LockedOut`, `-PasswordExpired`, `-PasswordNeverExpires`. Odwzorowujemy je jako „chipy” filtrów wykonywane po stronie
serwera (FT = FILETIME, setki nanosekund od 1601-01-01 UTC):

| Chip | Filtr LDAP | Uwagi |
|---|---|---|
| Wyłączone | `(userAccountControl:1.2.840.113556.1.4.803:=2)` | |
| Hasło nigdy nie wygasa | `(userAccountControl:1.2.840.113556.1.4.803:=65536)` | |
| Konto wygasło | `(&(accountExpires>=1)(accountExpires<=FT_teraz))` | wyklucza `0` i `9223372036854775807` („nigdy”) |
| Wygasa w ciągu N dni | `(&(accountExpires>=FT_teraz)(accountExpires<=FT_teraz+N))` | |
| Nieaktywne N dni | `(\|(lastLogonTimestamp<=FT_teraz-N)(!(lastLogonTimestamp=*)))` | domyślnie 90 dni; podpowiedź o opóźnieniu 9–14 dni; dla komputerów także `pwdLastSet` |
| Zablokowane | `(lockoutTime>=1)` na serwerze + bit 0x10 w `msDS-User-Account-Control-Computed` po stronie klienta | `lockoutTime` zostaje po wygaśnięciu blokady (MS-ADTS 3.1.1.4.5.17) |
| Hasło wygasło | bit 0x800000 w `msDS-User-Account-Control-Computed` lub `msDS-UserPasswordExpiryTimeComputed` < teraz | uwzględnia FGPP; bitów nie ma w zapisanym `userAccountControl` |
| Musi zmienić hasło | `(pwdLastSet=0)` | |

Atrybutów konstruowanych nie da się użyć w filtrze, więc chipy „zablokowane” i „hasło wygasło” filtrują wstępnie na
serwerze i sprawdzają tylko bieżącą stronę po stronie klienta. Domyślne okno nieaktywności `Search-ADAccount` nie jest
udokumentowane (źródło zewnętrzne podaje 15 dni – do weryfikacji w laboratorium), dlatego w generowanym kodzie zawsze
podajemy jawnie `-TimeSpan`.

### 4.6 Historia zmian bez lokalnej bazy

Najbliższym, wolnym od pamięci podręcznej odpowiednikiem dziennika Entra są atrybuty konstruowane
`msDS-ReplAttributeMetaData` (atrybuty jednowartościowe) i `msDS-ReplValueMetaData` (wartości łączy, np. dodanie i
usunięcie członka grupy): ostatnia zmiana, jej czas, `invocationId` źródłowego DC, wersja i USN. **Nie mówią, kto
zmienił** – do tego potrzebne są zdarzenia z dziennika Security na DC (np. 4738, 5136), co jest osobną, cięższą funkcją
(8.8).

---

## 5. Produkty konkurencyjne i open source

Dane o produktach komercyjnych pochodzą z wyciągów wyszukiwarki z dokumentacji producentów (witryny były nieosiągalne).
Szczegóły oznaczone przez badaczy jako niepewne mają dopisek (do weryfikacji w laboratorium) – w tym przypadku na
wersji próbnej produktu.

### 5.1 Tabela porównawcza

| Produkt | Model | Lokalna baza / kopia AD | Edycja zbiorcza | Podgląd przed zapisem | Cofanie | Delegacja |
|---|---|---|---|---|---|---|
| ADUC (punkt odniesienia) | klient MMC (ADSI) | nie (tylko definicje zapytań w `.msc`) | arkusz wielu obiektów, stały podzbiór pól | nie | nie | ACL AD |
| ADAC | klient WPF → ADWS/PowerShell | nie | arkusz wielu obiektów z polami „zastosuj” | nie | Kosz AD (bez poddrzew) | ACL AD |
| ManageEngine ADManager Plus | aplikacja WWW + usługa Windows, zapis kontem serwisowym | **tak**: PostgreSQL, synchronizacja co 10 min, pełna ok. raz w miesiącu | CSV z kluczem dopasowania, szablony, akcje z raportów, automatyzacje | lista dopasowanych obiektów z wyborem atrybutów | brak natywnego; wycofanie atrybutów w RecoveryManager Plus (kolejna baza) | w produkcie (role techników per domena/OU), zatwierdzenia wieloetapowe |
| Softerra Adaxes | usługa-pośrednik (.NET Remoting), web UI, REST | **tak**: konfiguracja w AD LDS, dziennik w SQLite (domyślnie 30 dni) | Basket z różnych kontenerów, „Multiple Selection Properties” (tylko jeden typ), value references | brak danych | brak natywnego | Security Roles w produkcie, zapis kontem usługi, zatwierdzenia |
| One Identity Active Roles | usługa-pośrednik + web | **tak**: baza konfiguracji; Managed Units istnieją tylko w produkcie | operacje wsadowe w web (domyślny limit 1500 obiektów) | brak danych | tylko „Undo deprovisioning” | Access Templates na obiektach i Managed Units; workflow z eskalacją |
| Hyena (SystemTools) | klient desktop | nie | Attribute Manager (Replace/Append/Delete), Active Editor (arkusz), Active Task (import z kluczem) | walidacja wierszy, stan przed i po w Active Editor | brak udokumentowanego | ACL AD |
| Softerra LDAP Administrator | klient desktop LDAP | nie | kreator Multiple Modifications (`%attr%`), LDAP-SQL, import LDIF | brak danych | brak | ACL AD |
| Sysinternals AD Explorer | klient desktop | **tak**: migawki `.dat` = pełna kopia katalogu | brak | — | — (porównanie migawek) | ACL AD |
| ADModify.NET (archiwalny, Microsoft PSS) | klient desktop + CLI | dziennik XML ze starymi i nowymi wartościami | dowolny atrybut, wartości dynamiczne | brak danych | **tak**, z dziennika XML | ACL AD |
| UnifiedDirectoryManager | klient WPF, .NET 10 | nie (ulubione jako odwołania DN) | wyniki wyszukiwania, wklejana lista (≤ 100 wpisów), CSV, scenariusze | „każdy zapis potwierdzany diffem” | brak udokumentowanego cofania zbiorczego | ACL AD |
| BLAZAM | aplikacja WWW (Blazor) | brak danych | brak danych | brak danych | brak danych | „Access Levels” per atrybut i akcja, dziedziczone w dół OU (Deny wygrywa), impersonacja do testów |
| OpenADUC (beta) | aplikacja WWW (Vue 3, Fastify, ldapts) | **tak**: PostgreSQL z deltami i okresową pełną synchronizacją | w planach | — | — | ponowne podanie hasła AD przed zapisem (60 min), dziennik tylko do dopisywania |
| **AD_MNG** | klient WPF, zero infrastruktury | **nie** – tylko pamięć sesji | jeden silnik planu: zaznaczenie, zapytanie, wklejona lista, CSV, arkusz, szablony, kreatory | diff per obiekt z filtrami, walidacją i kontrolą uprawnień | dziennik w pamięci + plan kompensujący z warunkami CAS | ACL AD + interfejs świadomy uprawnień |

### 5.2 Najważniejsze obserwacje o produktach

- **ManageEngine ADManager Plus:** modyfikacja z CSV z dopasowaniem po `sAMAccountName`, `employeeID`, UPN, DN,
  `objectGUID`, `mail`, `cn` lub `displayName`; klucza nie wolno zmieniać w tym samym przebiegu; nagłówki LDAP, UTF-8,
  TRUE/FALSE; od 6.3 dopisywanie lub nadpisywanie wartości wielowartościowych i wybór, czy pusta komórka czyści.
  Producent wymienia jako przyczyny błędów m.in. kodowanie i opóźnienie replikacji. Szablony z polami obowiązkowymi,
  ukrytymi i „silently active”, kreator formatów nazw, 200+ raportów z akcjami, automatyzacje. Delegacja „działa tylko
  w produkcie”. Cennik roczny od 595 USD (1 domena, 2 techników) do 17 995 USD (500 techników). Przypisanie szablonów do
  OU – do weryfikacji w laboratorium.
- **Softerra Adaxes:** .NET Framework 4.8 + IIS; Property Patterns z odwołaniami (`%firstname:lower%%lastname:lower,2%`,
  `%adm-RandomString,12%`, `%adm-ParentDN%`), Business Rules, Custom Commands, zatwierdzenia (od 2025.1 zatwierdzający
  może poprawić wniosek), Business Units. Licencja per włączone konto (100 kont: 1600 USD + 480 USD/rok). Użytkownicy
  proszą na forum producenta o cofanie wzorowane na ADModify.
- **One Identity Active Roles:** Managed Units, Access Templates, zasady PGV (znany błąd: brak atrybutu usuwa tekst
  następujący po nim), workflow z eskalacją, członkostwo czasowe, grupy dynamiczne; „Update object attributes” kończy
  się błędem, gdy nic się nie zmienia; wersja 8.2 (X 2024) dodała federację i MFA.
- **Hyena:** Attribute Manager (Replace/Append/Delete), Active Editor (arkusz z wyników bez ponownego odczytu –
  dokumentacja zaleca odświeżenie; import po kluczu z walidacją i oznaczeniem zmienionych komórek), Active Task (plik
  zadania uruchamiany z wiersza poleceń). 150–329 USD na administratora.
- **Softerra LDAP Administrator** (250 USD): Multiple Modifications z `%uid%`, LDAP-SQL, import LDIF z aktualizacją
  istniejących obiektów i porządkiem „rodzic przed dzieckiem”; w 2026 zadania importu/eksportu w harmonogramie.
- **ADModify.NET:** dziennik XML ze starą i nową wartością pozwalał cofnąć wszystko poza „Remove Exchange Attributes” i
  „Import Mailbox Rights”; projekt zniknął razem z CodePlex (składnia wartości dynamicznych
  – do weryfikacji w laboratorium).
- **Sysinternals AD Explorer:** migawka `.dat` to pełna kopia katalogu, którą narzędzia ofensywne (ADExplorerSnapshot.py,
  ok. 1,1 tys. gwiazdek) zamieniają na dane BloodHound – mocny argument za zasadą „brak danych lokalnych”.
- **Pozostałe:** Cjwdev AD Tidy (cofanie tylko w wersji płatnej, aktualizacje ustały ok. 2016; produktu „AD Bulk
  Users” nie potwierdzono); Netwrix i Lepide (historia zmian, ale z kolektorami i własnym magazynem); Specops Secure
  Service Desk (weryfikacja rozmówcy przed resetem, opcjonalny zakaz ręcznie wpisywanych haseł).
- **Open source:** *UnifiedDirectoryManager* (WPF, .NET 10, aktywny w X 2026) to najbliższy odpowiednik: szablony,
  „Copy user”, zapisane zapytania, edycja zbiorcza wyników, wklejana lista, CSV z neutralizacją formuł, scenariusze,
  ulubione wyszarzane po zniknięciu celu, LDAPS lub Kerberos z podpisywaniem. *OpenADUC* trzyma cały AD w PostgreSQL.
  *BLAZAM* ma najlepszy model delegacji wśród projektów open source. *ADMC* (Qt6) klonuje ADUC i GPMC. *myADMonitor*
  trzyma zmiany tylko w pamięci. *adeleg*, *ADACLScanner* i *PingCastle* to wzorce raportów ACL i nieaktualnych
  obiektów; *Tulpep Object Picker* opakowuje DsObjectPicker; *lithnet access-manager* to wzorzec UX dostępu do
  LAPS/BitLocker; *LDAP Admin*, *Apache Directory Studio* (kreator Batch Operation) i *LDAP Account Manager* (walidacja
  → podgląd LDIF → zapis) to ogólne klienty LDAP.

### 5.3 Składnie szablonów – porównanie

| Narzędzie | Składnia | Wnioski |
|---|---|---|
| Adaxes | `%firstname%`, `%lastname:lower,2%`, `%adm-RandomString,12%`, `%adm-ParentDN%` | modyfikatory wielkości liter i długości |
| Active Roles (PGV) | `%<givenName>`, `%<sn>`, maski, właściwości OU nadrzędnej | brak atrybutu „zjada” następujący tekst – potrzebny jawny zapas |
| ManageEngine | kreator formatu nazw zapisywany jako `%givenName%`, formaty warunkowe | podgląd na żywo |
| Softerra | `%uid%` w Multiple Modifications | wartości per obiekt |
| ADUC | `%username%` w ścieżkach profilu/katalogu domowego przy edycji wielu obiektów | zgodność wsteczna |
| AD_MNG | `{attr\|fn…}`, `{csv:Kolumna}`; funkcje m.in. `lower`, `first(n)`, `translit`, `default(x)`, `counter(w)` | transliteracja polska, jawne wartości zapasowe, liczniki unikalności, podgląd (9.10) |

### 5.4 Wnioski i wyróżniki AD_MNG

- **Zero infrastruktury:** bez usługi, konta serwisowego i bazy; operacje na żywo z poświadczeniami operatora, więc
  granicą delegacji są natywne ACL AD i nie powstaje nowy zasób Tier-0.
- **Pełny potok zbiorczy z cofaniem** (świeży odczyt, diff, kontrola współbieżności, raport per obiekt, plan
  kompensujący), którego nie ma żaden z przebadanych produktów.
- **Interfejs świadomy uprawnień z samego AD** – „widok helpdesku” i „widok administratora” bez lokalnych ról.
- **Historia zmian na żywo z AD** (metadane replikacji + opcjonalnie zdarzenie 5136), bez kolektora i bazy.
- **Polskie realia** (transliteracja ł, CSV w cp1250 ze średnikiem, skrypty z BOM, etykiety z `CN=415`) oraz
  **parytet z ADUC** razem z nawykami klawiaturowymi.
- **Kompromis:** bez serwera nie ma wymuszanego procesu zatwierdzeń – później podpisane pliki planu („cztery oczy”).
  Role i ukryte pola w interfejsie to wygoda, nie zabezpieczenie – trzeba to jasno komunikować.

---

## 6. Techniki LDAP dla „płynności na żywo”

### 6.1 Dlaczego S.DS.P

ADSI/`DirectoryEntry` (podstawa ADUC) ma kliencką pamięć podręczną właściwości (`RefreshCache`/`CommitChanges`), co
kłóci się z zasadą „bez pamięci podręcznej”, i jest oparty na COM; `System.DirectoryServices.AccountManagement` dokłada
obiegi sieciowe i obsługuje kilka klas; ADWS (port 9389) to wolniejszy SOAP, nie na każdym DC, z limitem 5000 członków.
S.DS.P to cienka warstwa nad `wldap32`: każda kontrolka i opcja sesji, multipleksowanie żądań, pakiet NuGet 10.0.x,
działanie także na Linuksie (testy z Sambą w CI) – [ADR-0002](adr/0002-system-directoryservices-protocols.md). WS2025 i
Windows 11 24H2 mają liczniki wydajności klienta LDAP, którymi można wykazać przewagę nad ADUC.

### 6.2 Asynchroniczność: dlaczego `TaskCompletionSource`, a nie `FromAsync`

S.DS.P w .NET 10 ma tylko API w modelu APM (`BeginSendRequest`, `EndSendRequest`, `Abort`, `GetPartialResults`); ani
`release/10.0`, ani `main` (.NET 11) nie mają wersji opartej na `Task`. Z kodu .NET 6–10 wynika:

1. `BeginSendRequest` przy pierwszym użyciu **synchronicznie** wykonuje `ldap_connect` (limit = `Timeout` w pełnych
   sekundach, domyślnie 30 s) i – przy `AutoBind` – blokujące `ldap_bind_s`; ponowny bind także po zmianie `AuthType`
   lub `Credential`. Pierwszego żądania nie wolno więc wysłać z wątku UI.
2. Zakończenie wykrywa pętla `ldap_result` z `Task.Delay` podwajanym od 1 do 100 ms (narzut od kilku do ok. 100 ms, bez
   blokowania wątku); po upływie limitu żądania zgłaszany jest `LdapException` 85 i wysyłane `ldap_abandon`.
   Synchroniczne `SendRequest` obcina limit czasu do pełnych sekund.
3. Tryb wyników częściowych (powiadomienia) obsługuje **jeden globalny wątek** z `Thread.Sleep(250)`;
   `TimeSpan.MaxValue` i `Timeout.InfiniteTimeSpan` dają natychmiastowe `LDAP_TIMEOUT`.
4. **Po `Abort(ar)` callback nigdy nie jest wywoływany** (flaga `_abortCalled`; sprawdzone w `LdapConnection.cs` z
   `release/10.0`, ok. linii 354 i 424), więc `Task.Factory.FromAsync` po anulowaniu nigdy by się nie zakończył. `Abort`
   rzuca też `ArgumentException`, jeśli `EndSendRequest` już się wykonało. To rozstrzygnęło spór badaczy (badacz
   interfejsu proponował `FromAsync`, badacz S.DS.P – `TaskCompletionSource`):

```csharp
var tcs = new TaskCompletionSource<DirectoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
var ar = conn.BeginSendRequest(req, timeout, PartialResultProcessing.NoPartialResultSupport,
    a => { try { tcs.TrySetResult(conn.EndSendRequest(a)); } catch (Exception e) { tcs.TrySetException(e); } }, null);
using var reg = ct.Register(() =>
{
    if (tcs.TrySetCanceled(ct))
        try { conn.Abort(ar); } catch (ArgumentException) { } catch (ObjectDisposedException) { }
});
return await tcs.Task.ConfigureAwait(false);
```

- Wątek UI tylko wiąże dane; pierwszy connect/bind idzie przez `Task.Run`; nigdzie `.Result` ani `.Wait()`;
  `ConfigureAwait(false)` w warstwach usług. Zawsze ograniczony limit żądania (30–120 s) i `SearchRequest.TimeLimit`
  (DC tnie do `MaxQueryDuration` = 120 s) – po `Abort` wewnętrzna pętla działa aż do błędu lub upływu limitu.
- Każdy widok ma własny `CancellationTokenSource`, anulowany przy nawigacji, odświeżeniu, zmianie sortowania, filtra
  lub tożsamości; wyszukiwanie w trakcie pisania anuluje przy każdym znaku; Esc anuluje operację pierwszoplanową.
- `Abort` wysyła abandon, ale nie cofa modyfikacji już wykonanej przez serwer – przerwany zapis ma stan **Unknown** i
  rozstrzyga go odczyt weryfikujący (9.5).

### 6.3 Połączenia i ich konfiguracja

Wywołania `wldap32` są bezpieczne wątkowo, a jedna sesja multipleksuje wiele żądań; sama klasa S.DS.P nie gwarantuje
jednak bezpieczeństwa instancji – `Bind()` i zmiany `SessionOptions`, `Credential`, `AuthType` nie mogą biec równolegle
z żądaniami. Stan serwera (ciasteczka stron, `ContextId` VLV, rejestracje powiadomień) jest związany z połączeniem.

| „Pas” na parę (tożsamość, domena) | Połączenia | Przeznaczenie |
|---|---|---|
| Interactive | 1, ok. 6 równoległych żądań, kolejka priorytetowa | nawigacja > widoczna strona > karta właściwości > wstępne pobieranie |
| Batch | 1–2, za ogranicznikiem szybkości | wyłącznie zapisy |
| Notification | 1, maks. 5 rejestracji | powiadomienia o zmianach |
| GlobalCatalog | 1, port 3268 (3269 z TLS) | wyszukiwanie w lesie, tylko odczyt, nigdy zapis |

Ustawienia: `LdapDirectoryIdentifier(fqdn, 389, fullyQualifiedDnsHostName: true, connectionless: false)` (ustawia
`LDAP_OPT_AREC_EXCLUSIVE`, więc SPN `ldap/<fqdn>` daje Kerberos); `ProtocolVersion = 3`; `ReferralChasing = None`
(domyślnie w Windows faktycznie `All` – gonienie DomainDnsZones, ForestDnsZones i Configuration nowymi bindami daje
wielosekundowe przestoje); `Signing = Sealing = true` przed `Bind()`; `AutoBind = false`; `TcpKeepAlive`; `Timeout`
15–30 s. Wbudowane klasy kontrolek mają domyślnie `IsCritical = true`; przed użyciem kontrolki sprawdzamy
`supportedControl` w RootDSE.

### 6.4 Stronicowanie

- Paged Results (RFC 2696, `1.2.840.113556.1.4.319`): domyślny `PageSize` w S.DS.P to 512 – ustawiamy go jawnie; pętla
  trwa do pustego `Cookie`; DC tnie stronę do `MaxPageSize` (1000, twardo 20 000). Bez kontrolki wynik powyżej limitu
  kończy się `sizeLimitExceeded` (4), a częściowe wpisy są w `SearchResponse` wyjątku. Porzucając wyszukiwanie,
  wysyłamy `PageSize = 0` z ciasteczkiem, by zwolnić stan serwera; `DomainScopeControl` (`.1339`) wyłącza odwołania
  kontynuacyjne na każdej stronie.
- AD używa jednego indeksu na wyszukiwanie; sortowanie z paginacją zmusza DC do posortowania całego wyniku przed
  pierwszą stroną, a paginacji nie łączy się z VLV.
- Dla płynności: pierwsza strona 100–200 obiektów, kolejne 500–1000; zawsze jawna lista atrybutów (nigdy `*`); jedno
  zdarzenie `CollectionChanged` na stronę z priorytetem `DispatcherPriority.Background`.

### 6.5 VLV i sortowanie po stronie serwera

- VLV (`2.16.840.1.113730.3.4.9`, odpowiedź `.10`) wymaga kontrolki sortowania (`1.2.840.113556.1.4.473`, odpowiedź
  `.474`); AD obsługuje **jeden klucz** i nie sortuje po atrybutach konstruowanych ani po `distinguishedName`.
  `VlvRequestControl(before, after, offset)` liczy offset od 1, wariant z `target` realizuje „skok do »Kow…«”;
  odsyłamy `ContextId` i `EstimateCount`; `ContentCount` to szacunek; błąd 76 = `virtualListViewError`.
- Bez indeksu VLV (`searchFlags` 0x40, WS2008+) lub indeksu per kontener (0x2) DC buduje tabelę tymczasową ograniczoną
  `MaxTempTableSize` (10 000, twardo 100 000), co na dużych kontenerach jest wolne lub kończy się błędem (dokładny kod –
  do weryfikacji w laboratorium). ADUC nie używa VLV – to ono ma dać odczuwalną przewagę na dużych OU.
- Porządek sortowania definiuje serwer. OID-y reguł porządku dla języków istnieją (ok. `1.2.840.113556.1.4.1461`–
  `…15xx`), ale polskiego nie potwierdzono (do weryfikacji w laboratorium, razem z porządkiem nazw z ą/ł/ż).

| Sytuacja | Strategia |
|---|---|
| Rozwinięcie węzła drzewa | OneLevel ze stronicowaniem, tylko klasy kontenerowe, minimalne atrybuty |
| Lista kontenera, `msDS-Approx-Immed-Subordinates` ≤ ok. 2000 | stronicowanie, cały zbiór w pamięci widoku, sortowanie lokalne komparatorem `pl-PL` |
| Lista kontenera > ok. 2000 | VLV + jeden indeksowany klucz; przy błędzie 76/53/11 lub tabeli tymczasowej powrót do stronicowania, baner „zawęź filtr”, sortowanie tylko po kolumnach indeksowanych |
| Szybkie wyszukiwanie | `(anr=…)`, `SizeLimit` 50 |
| Wyszukiwanie zaawansowane | stronicowane Subtree, strumieniowo |
| Karta właściwości | odczyt bazowy + atrybuty konstruowane; ranged retrieval, gdy nazwa atrybutu zawiera `;range=` |
| Zapisy | wyłącznie pas Batch |

### 6.6 Limity polityki LDAP na DC

Polityki są w obiektach `queryPolicy` (atrybut `lDAPAdminLimits`) pod
`CN=Query-Policies,CN=Directory Service,CN=Windows NT,CN=Services,CN=Configuration` i można je odczytać w trakcie
działania; ich nazwy zawiera `supportedLDAPPolicies` w RootDSE.

| Polityka | Domyślnie | Twardy limit (WS2008+) | Znaczenie dla AD_MNG |
|---|---|---|---|
| `MaxPageSize` | 1000 | 20 000 | rozmiar strony ≤ 1000 |
| `MaxValRange` | 1500 (min. 30) | 5000 | ranged retrieval dla `member` |
| `MaxTempTableSize` | 10 000 | 100 000 | limit sortowania/VLV bez indeksu |
| `MaxQueryDuration` | 120 s | 1200 s | `TimeLimit` ≤ 120 s |
| `MaxResultSetSize` | 262 144 B | — | łączny stan stron/VLV na serwerze |
| `MaxResultSetsPerConn` | 10 (`MinResultSets` 3) | — | maks. ok. 8 otwartych stronicowań na połączenie |
| `MaxNotificationPerConn` | 5 | — | LRU rejestracji powiadomień |
| `MaxConnIdleTime` | 900 s | — | serwer zamyka bezczynne połączenia; rejestracje powiadomień giną |
| `MaxConnections` | 5000 na DC | — | |
| `MaxReceiveBuffer` | 10 485 760 B | 20 971 520 B | ograniczenie liczby wartości `member` w jednym zapisie |
| `MaxPoolThreads` | 4 na CPU | — | nie zalewać DC zapisami |
| `InitRecvTimeout` | 120 s | — | |
| `MaxActiveQueries` | 20 | nie egzekwowany od 2003 | |

### 6.7 Ranged retrieval i ASQ

- **Ranged retrieval:** odczyt bazowy `member;range=0-*`; DC zwraca np. `member;range=0-1499`, więc iterujemy po
  `AttributeNames` i prosimy o `member;range=1500-*`, aż nazwa skończy się na `-*`. Bez tego `member` jest **po cichu
  obcięty** do 1500 wartości (maks. 5000). ADSI zaleca prośby o najwyżej 1000; istnieje kontrolka „no-error” `.1948`.
- **ASQ** (`AsqRequestControl("member")`, `1.2.840.113556.1.4.1504`): wyszukiwanie bazowe na grupie zwraca obiekty
  członków z filtrem i wybranymi atrybutami (bez N+1 zapytań); jeden atrybut źródłowy typu DN; można nałożyć VLV i
  sortowanie. ASQ ze stronicowaniem – do weryfikacji w laboratorium.
- `member` i ASQ pomijają członków przez grupę podstawową – osobno `(primaryGroupID=<RID z primaryGroupToken>)`. Na DC,
  który nie jest GC, `memberOf` nie widzi grup uniwersalnych z innych domen.

### 6.8 Filtry, ANR i alternatywne formy DN

- Reguły bitowe przyjmują tylko liczby dziesiętne: `BIT_AND` `1.2.840.113556.1.4.803`, `BIT_OR` `.804`, np. wyłączone
  `(userAccountControl:1.2.840.113556.1.4.803:=2)`, grupy zabezpieczeń `(groupType:1.2.840.113556.1.4.803:=2147483648)`.
  `IN_CHAIN` `1.2.840.113556.1.4.1941` (np. `(memberOf:1.2.840.113556.1.4.1941:=<DN grupy>)`) daje członkostwo
  przechodnie, ale jest kosztowny przy dużym rozgałęzieniu.
- **ANR** jest prefiksowe i indeksowane; szukania w środku słowa (`*kow*`) unikamy, chyba że atrybut ma indeks krotkowy
  (0x20). Wyszukiwanie w trakcie pisania: opóźnienie 250–300 ms, min. 2 znaki, `SizeLimit` ok. 50, minimalna lista
  atrybutów, `Abort` poprzedniego żądania, odrzucanie spóźnionych wyników po numerze sekwencji, `sizeLimitExceeded`
  traktowane jako sukces.
- Wejście użytkownika escapujemy wg RFC 4515 (`\2a`, `\28`, `\29`, `\5c`, `\00`) – inaczej możliwe jest wstrzyknięcie
  do filtra LDAP.
- AD przyjmuje jako DN `<GUID=…>`, `<SID=…>` i `<WKGUID=…,DC=…>` (odporne na zmianę nazwy i przeniesienie); dopisanie
  `<SID=S-1-5-…>` do `member` automatycznie tworzy `foreignSecurityPrincipal`.

### 6.9 Kontrolki – tabela OID

OID-y kontrolek, które mają klasę w S.DS.P, sprawdzono w kodzie dotnet/runtime; pozostałe pochodzą z dokumentacji
Microsoftu i trzeba je potwierdzić w `supportedControl`.

| Kontrolka / reguła / operacja | OID | Klasa S.DS.P | Zastosowanie w AD_MNG |
|---|---|---|---|
| Paged Results | `1.2.840.113556.1.4.319` | `PageResultRequestControl` | listy, wyszukiwanie |
| Server-side sort (żądanie / odpowiedź) | `1.2.840.113556.1.4.473` / `.474` | `SortRequestControl` | sortowanie kolumn, wymagane przez VLV |
| VLV (żądanie / odpowiedź) | `2.16.840.1.113730.3.4.9` / `.10` | `VlvRequestControl` | duże kontenery, skok do litery |
| ASQ | `1.2.840.113556.1.4.1504` | `AsqRequestControl` | członkowie grupy z atrybutami |
| Domain Scope | `1.2.840.113556.1.4.1339` | `DomainScopeControl` | bez odwołań kontynuacyjnych |
| Search Options (DomainScope=1, PhantomRoot=2) | `1.2.840.113556.1.4.1340` | `SearchOptionsControl` | wyszukiwanie w całym lesie przez GC |
| Show Deleted | `1.2.840.113556.1.4.417` | `ShowDeletedControl` | Kosz AD |
| Show Recycled / Show Deactivated Link | `1.2.840.113556.1.4.2064` / `.2065` | ogólna `DirectoryControl` | Kosz AD, łącza dezaktywowane |
| Tree Delete | `1.2.840.113556.1.4.805` | `TreeDeleteControl` | usuwanie z dziećmi (zachowanie przy bardzo dużych poddrzewach – do weryfikacji w laboratorium) |
| SD Flags | `1.2.840.113556.1.4.801` | `SecurityDescriptorFlagControl` | odczyt DACL bez praw do SACL (0x7), zapis tylko DACL (0x4) |
| Permissive Modify | `1.2.840.113556.1.4.1413` | `PermissiveModifyControl` | idempotentne dodawanie/usuwanie wartości |
| Lazy Commit | `1.2.840.113556.1.4.619` | `LazyCommitControl` | opcjonalnie przy bardzo dużych importach |
| Extended DN | `1.2.840.113556.1.4.529` | `ExtendedDNControl` | `<GUID=…>;<SID=…>;DN` w wynikach |
| Verify Name | `1.2.840.113556.1.4.1338` | `VerifyNameControl` | walidacja DN z innych domen przez GC |
| Cross Domain Move | `1.2.840.113556.1.4.521` | `CrossDomainMoveControl` | poza zakresem v1 |
| Quota | `1.2.840.113556.1.4.1852` | `QuotaControl` | — |
| Policy Hints | `1.2.840.113556.1.4.2239` (przestarzałe `.2066`) | ogólna, wartość `BerConverter.Encode("{i}", 1)` | historia haseł przy resecie administracyjnym |
| Change Notification | `1.2.840.113556.1.4.528` | `DirectoryNotificationControl` | odświeżanie na żywo |
| DirSync | `1.2.840.113556.1.4.841` | `DirSyncRequestControl` | **odrzucone** (6.11) |
| Range retrieval no-error | `1.2.840.113556.1.4.1948` | ogólna | ranged retrieval |
| Stats | `1.2.840.113556.1.4.970` | ogólna | diagnostyka planów zapytań (wersje deweloperskie) |
| BIT_AND / BIT_OR / IN_CHAIN | `1.2.840.113556.1.4.803` / `.804` / `.1941` | reguły w filtrze | filtry stanu, zagnieżdżenia |
| WhoAmI (operacja rozszerzona, RFC 4532) | `1.3.6.1.4.1.4203.1.11.3` | `ExtendedRequest` | weryfikacja tożsamości po bindzie |
| StartTLS (operacja rozszerzona) | `1.3.6.1.4.1.1466.20037` | `StartTransportLayerSecurity` | tryb TLS na 389 |

### 6.10 Atrybuty konstruowane

Nie są zwracane dla `*` – trzeba o nie prosić z nazwy; część jest droga dla DC i zwracana lub sensowna tylko w
odczycie bazowym; żadnego nie da się użyć w filtrze (`inappropriateMatching`).

| Atrybut | OID | Co daje | Uwagi |
|---|---|---|---|
| `allowedAttributesEffective` | `1.2.840.113556.1.4.914` | atrybuty, które związany podmiot może zapisać | blokady pól; bez praw rozszerzonych, usuwania i przenoszenia |
| `allowedChildClassesEffective` | `1.2.840.113556.1.4.912` | klasy, które może utworzyć w kontenerze | menu New |
| `sDRightsEffective` | `1.2.840.113556.1.4.1304` | bity 0x1 właściciel, 0x4 DACL, 0x8 SACL | karta Security |
| `msDS-User-Account-Control-Computed` | `1.2.840.113556.1.4.1460` | 0x10 LOCKOUT, 0x800000 PASSWORD_EXPIRED, 0x4000000 PARTIAL_SECRETS, 0x8000000 USE_AES_KEYS | stan blokady i wygaśnięcia hasła |
| `msDS-UserPasswordExpiryTimeComputed` | `1.2.840.113556.1.4.1996` | data wygaśnięcia hasła (FILETIME) | jedyna wiarygodna przy FGPP |
| `msDS-ResultantPSO` | — | skuteczna polityka FGPP | generator haseł, podgląd polityki |
| `msDS-PrincipalName` | `1.2.840.113556.1.4.1865` | `DOMENA\nazwa` | |
| `canonicalName` | `1.2.840.113556.1.4.916` | forma kanoniczna | pasek adresu |
| `msDS-Approx-Immed-Subordinates` | `1.2.840.113556.1.4.1669` | przybliżona liczba dzieci (±10%) | ekspander, wybór paginacja/VLV |
| `primaryGroupToken` | — | RID grupy | członkowie przez grupę podstawową |
| `tokenGroups` | `1.2.840.113556.1.4.1301` | SID-y członkostwa przechodniego | tylko odczyt bazowy, wymaga GC, drogi; warianty `tokenGroupsNoGCAcceptable`, `tokenGroupsGlobalAndUniversal` |
| `msDS-ReplAttributeMetaData` / `msDS-ReplValueMetaData` | — | ostatnia zmiana atrybutu / wartości łącza | karta Historia |

Tożsamość związanego podmiotu pobieramy operacją WhoAmI. Alternatywnie `tokenGroups` z RootDSE
(do weryfikacji w laboratorium) albo odczyt bazowy własnego obiektu operatora.

### 6.11 Powiadomienia o zmianach i ich limity

- `LDAP_SERVER_NOTIFICATION_OID` `1.2.840.113556.1.4.528` (`DirectoryNotificationControl`): asynchroniczne wyszukiwanie
  z filtrem **dokładnie** `(objectClass=*)` (inny filtr daje `unwillingToPerform`), zakres Base (otwarta karta) lub
  OneLevel (widoczny kontener); Subtree jest kosztowny.
- **Maks. 5 rejestracji na połączenie** (`MaxNotificationPerConn`) – prowadzimy LRU rejestracji na osobnym połączeniu.
  Powiadomienia są lokalne dla DC (zmiany z innych DC przychodzą po replikacji); zmiana nazwy lub przeniesienie
  **przodka** ich nie generuje; po `MaxConnIdleTime` (900 s) lub restarcie DC rejestracje giną po cichu – trzeba to
  wykryć, zarejestrować ponownie i odczytać stan od nowa.
- Najpierw rejestracja, potem odczyt stanu początkowego; obiekty identyfikujemy po `objectGUID`; usunięcia przychodzą z
  `isDeleted=TRUE` i nowym DN. W S.DS.P: `ReturnPartialResultsAndNotifyCallback`, limit `TimeSpan.FromDays(7)`,
  `GetPartialResults` w callbacku; opóźnienie ok. 250 ms × liczba operacji częściowych w procesie.
- **DirSync** (`1.2.840.113556.1.4.841`) wymaga zakresu Subtree od korzenia NC, prawa DS-Replication-Get-Changes (lub
  flagi ObjectSecurity) i najpierw zwraca wszystko; ciasteczka bywają odrzucane między DC (`0000203D`) – odrzucony.
- **Zapas:** co 10–30 s tani odczyt `highestCommittedUSN` z RootDSE, a po zmianie zapytanie `(uSNChanged>=N)` w
  widocznym kontenerze (`uSNChanged` jest lokalny dla DC, więc wymaga przypięcia).

### 6.12 Przypięcie do DC, lokalizator, GC i wiele domen

- DC wybiera `DsGetDcName` (netapi32, `LibraryImport`, zwolnienie wyniku `NetApiBufferFree`) z flagami
  `DS_DIRECTORY_SERVICE_REQUIRED` 0x10, `DS_WRITABLE_REQUIRED` 0x1000 (bez RODC), `DS_IS_DNS_NAME` 0x20000,
  `DS_RETURN_DNS_NAME` 0x40000000, opcjonalnie `DS_TRY_NEXTCLOSEST_SITE` 0x40000, `DS_PDC_REQUIRED` 0x80,
  `DS_GC_SERVER_REQUIRED` 0x40, `DS_DIRECTORY_SERVICE_6/8_REQUIRED`; `DS_FORCE_REDISCOVERY` 0x1 tylko po awarii. Netlogon
  buforuje wyniki i uwzględnia lokacje, więc nie składamy rekordów SRV ręcznie.
- **Sesja przypięta do DC** ([ADR-0005](adr/0005-sesja-przypieta-do-dc.md)) daje odczyt własnych zapisów, stabilne
  `uSNChanged`, działające powiadomienia i wiarygodne sprawdzanie unikalności; bez cichego przełączenia, z dostępnym
  „Change domain controller”. Reset hasła i odblokowanie można skierować do PDC lub DC z lokacji użytkownika – okno
  pokazuje, który DC wykonał operację.
- **GC** (3268/3269) służy tylko do odczytu atrybutów PAS; wyszukiwanie w lesie z PhantomRoot lub bazą `""`; zapis
  zawsze na DC domeny obiektu (mapowanie sufiksu DN na domenę). **Domeny lasu:**
  `(&(objectClass=crossRef)(systemFlags:1.2.840.113556.1.4.803:=2))` pod `CN=Partitions` (`nCName`, `dnsRoot`,
  `nETBIOSName`), każda z własną przypiętą sesją; wynik `Referral` kierujemy jawnie do właściwej sesji, nigdy nie gonimy
  odwołań. WS2025 poprawia mapowanie NetBIOS→DNS w lokalizatorze i nie używa mailslotów.

### 6.13 Kosz AD – mechanika (priorytet klienta)

- **Wymagania:** poziom lasu 2008 R2+; włączenie (`Enable-ADOptionalFeature … -Scope ForestOrConfigurationSet`) jest
  **nieodwracalne** i powiększa NTDS.DIT na każdym DC; dokumentacja podaje raz Enterprise Admins, raz Domain Admins.
  Przywrócić można tylko obiekty usunięte po włączeniu.
- **Wykrycie:** `msDS-EnabledOptionalFeatures` z GUID funkcji `766ddcd8-acd0-445e-f3b9-a7f9b6744f2a`
  (do weryfikacji w laboratorium). Okno usuwania pokazuje „do przywrócenia przez N dni” (`msDS-deletedObjectLifetime`,
  `tombstoneLifetime`) albo „NIE do przywrócenia – tombstone traci większość atrybutów i łączy grup”.
- **Przeglądanie:** baza `<WKGUID=18e2ea80684f11d2b9aa00c04f79f805,DC=…>`, Show Deleted `.417` (i Show Recycled
  `.2064`), `(isDeleted=TRUE)` z paginacją, filtry serwerowe po `lastKnownParent`, `whenChanged` (przybliża czas
  usunięcia), `msDS-LastKnownRDN`, nazwie, typie, UPN.
- **Przywrócenie** (MS-ADTS 3.1.1.5.3.7): jedna modyfikacja z Show Deleted, która **usuwa** `isDeleted` (ustawienie
  FALSE nie działa) i zastępuje `distinguishedName`; wymaga prawa Reanimate-Tombstones (zmiana ACL usuniętego obiektu –
  także `WRITE_DAC` i `WRITE_OWNER`). „Restore To” = inny rodzic.
- **Poprawka względem ADAC:** drzewo OU przywracane jedną operacją w kolejności „rodzic przed dzieckiem” ustalonej z
  `lastKnownParent`. Obiekty po `msDS-deletedObjectLifetime` są nie do odzyskania; Configuration i strefy DNS wymagają
  osobnej obsługi.

### 6.14 RootDSE, schemat i display specifiers

- **RootDSE** (odczyt `""` z jawną listą atrybutów): konteksty nazw (`defaultNamingContext`,
  `configurationNamingContext`, `schemaNamingContext`, `rootDomainNamingContext`, `namingContexts`), `dnsHostName`,
  `serverName`, `dsServiceName`, `supportedControl`, `supportedCapabilities`, `supportedLDAPPolicies`,
  `supportedSASLMechanisms`, poziomy funkcjonalne (jak w 2.15), `isGlobalCatalogReady`, `isSynchronized`,
  `highestCommittedUSN`, `currentTime`, `subschemaSubentry`.
- **Schemat** (raz na sesję, stronicowane OneLevel): `attributeSchema` (`lDAPDisplayName`, `attributeSyntax`,
  `oMSyntax`, `isSingleValued`, `rangeLower`/`rangeUpper`, `systemOnly`, `searchFlags`, `systemFlags` – 0x4 oznacza
  atrybut konstruowany, `linkID` – parzysty to łącze w przód, nieparzysty zwrotne, `isMemberOfPartialAttributeSet`,
  `schemaIDGUID`) i `classSchema` (`mustContain`/`mayContain` z klasami pomocniczymi, `possSuperiors`, `rDNAttID`).
- Bity `searchFlags`: 0x1 indeks, 0x2 indeks per kontener, 0x4 ANR, 0x10 kopiowany, 0x20 indeks krotkowy, 0x40 indeks
  VLV, 0x80 poufny. Z nich wynikają edytory, walidacja, sortowalne kolumny, zestaw ANR, „Copy user” i dostępność w GC.
  Schemat, display specifiers i mapy `rightsGuid`/`schemaIDGUID` trzymamy wyłącznie w pamięci sesji.

### 6.15 Pomiary prototypu

Prototyp S.DS.P uruchomiono na laboratorium [`tools/testlab`](../tools/testlab/README.md): Samba 4.19 AD DC, klient na
Linuksie, połączenie w obrębie jednej maszyny, wiązanie laboratoryjne (aplikacja produkcyjnie go nie używa).

| Bind | Pierwsza strona (100 obiektów) | 428 obiektów | 20 równoległych zapytań na 1 połączeniu | VLV + sortowanie | ASQ | `allowedAttributesEffective` |
|---|---|---|---|---|---|---|
| 16 ms | 65 ms | 174 ms | działa | działa | działa | zwracany |

To rząd wielkości narzutu klienta i protokołu, a nie zachowanie Windows DC w LAN z Kerberos i szyfrowaniem. Na
Windows Server 2025 pomiary trzeba powtórzyć (do weryfikacji w laboratorium) względem celów fazy 0: pierwsza strona
< 300 ms w LAN dla OU ze 100 tys. obiektów, żadna ramka UI z pracą > 16 ms, okno widoczne < 1 s, Esc anuluje zapytanie
z wysłaniem abandon.

### 6.16 Mapowanie błędów

S.DS.P rzuca `LdapException` (klient i transport: 49, 81, 82 – często SSPI/SPN, 85, 91; `ServerErrorMessage` z
diagnostyką bindu) albo `DirectoryOperationException` (`ResultCode`, `ErrorMessage`, `MatchedDN`, `Referral`).
`ErrorMessage` z AD zaczyna się 8-cyfrowym kodem Win32; komunikat bierzemy z `new Win32Exception(kod).Message`
(lokalizowany przez system), dodajemy wskazówkę, a surowy tekst DSID pokazujemy pod „Szczegóły”. Część mapowań badacze
podali z pamięci (do weryfikacji w laboratorium).

| Kod LDAP | Kod Win32 / podkod | Znaczenie i komunikat |
|---|---|---|
| 8 strongerAuthRequired | `00002028` | brak podpisywania/szyfrowania lub TLS |
| 10 referral | `0000202B` | zapis na RODC lub zły NC – przekierować do zapisywalnego DC |
| 13 / 53 | `0000001F` | operacja wymaga szyfrowanego połączenia |
| 16 noSuchAttribute | | konflikt CAS – ktoś zmienił wartość |
| 19 / 53 | `0000052D` | polityka haseł (długość, złożoność, historia, minimalny wiek) |
| 19 / 68 | `000021C7` / `000021C8` | SPN / UPN nieunikalny w lesie (WS2012 R2+) |
| 20, 68 | `00000524`, `00002071` | wartość lub obiekt już istnieje |
| 32 noSuchObject | `0000208D` | „nie istnieje lub brak uprawnień” (tryb List Object) |
| 50 insufficientAccessRights | `00000005`, `00002098` | które prawo, na jakim obiekcie i DC; „kopiuj szczegóły do eskalacji” |
| 53 / 66 | `0000055E` / `00002015` | usunięcie z grupy podstawowej / obiekt ma dzieci (potrzebny Tree Delete) |
| 11, 51, 52, 81 | | przeciążenie lub niedostępność – ponowienie z odczekaniem |
| 49 invalidCredentials | podkody `data` | 525 brak użytkownika, 52e złe hasło, 530 godziny logowania, 531 stacja, 532 hasło wygasło, 533 konto wyłączone, 701 konto wygasło, 773 musi zmienić hasło, 775 zablokowane |

---

## 7. Interfejs

### 7.1 WPF Fluent w .NET 10 – stan i znane błędy

- Fluent pojawił się w .NET 9: `Application.ThemeMode` / `Window.ThemeMode` (`None` = Aero2, `Light`, `Dark`, `System`)
  albo ręczne dołączenie `Fluent.xaml`. `ThemeMode` steruje też tłem Mica i ciemnym paskiem tytułu; akcent pochodzi z
  `SystemColors` (`AccentColor`, Light1–3/Dark1–3) i używamy go wyłącznie przez `DynamicResource`.
- **Status eksperymentalny:** `ThemeMode` i `Application.ThemeMode` mają `[Experimental("WPF0001")]` także na `main`
  (.NET 11 RC1), z adnotacją, że API może zniknąć; kod wymaga `NoWarn`/`#pragma` (ustawienie w XAML podobno nie wywołuje
  diagnostyki). Przy `ThemeMode` aplikacji innym niż `None` pojedyncze okno nie może się wyłączyć. Całe API trzymamy za
  `IThemeService` (jedyne miejsce z wyłączonym WPF0001), a SDK przypinamy w `global.json`
  ([ADR-0001](adr/0001-wpf-net10-fluent.md)).
- .NET 10 dodał style m.in. dla DatePicker, GridSplitter, GridView, GroupBox, Label i TextBox oraz poprawił awarie w
  trybie wysokiego kontrastu; 51 plików stylów pokrywa wszystkie kontrolki potrzebne klonowi ADUC.
- **Otwarte błędy:** epik #10393, #10445 (styl DataGrid), #10911 (ListView/GridView), #10962 (DataGrid gubi pozycję
  przewinięcia po przeładowaniu), #10986 (nagłówek DataGrid traci styl przy `HorizontalContentAlignment`), #9562,
  #8550 (mechanizm wyłączenia), #8991 (tracker Win11).
- **Brakuje:** NavigationView, InfoBar, ContentDialog, AutoSuggestBox, BreadcrumbBar, NumberBox, ProgressRing,
  ToggleSwitch, TabView, TeachingTip, Snackbar, CommandBarFlyout. Budujemy mały zestaw `AdMng.Controls` (paleta poleceń,
  BreadcrumbBar, InfoBar/toast, zamykane karty, ProgressRing, style kompaktowe), w razie potrzeby z kodem MIT z WPF UI.
- Rozmiary Fluent są „dotykowe” – potrzebne style kompaktowe (wiersz 24–28 px) `BasedOn` Fluent. Segoe Fluent Icons jest
  tylko w Windows 11 (w Windows 10 zapasowo Segoe MDL2 Assets). Biblioteki **lepoco/wpfui** (4.3.0, ok. 419 otwartych
  zgłoszeń, jeden opiekun wydań) nie używamy jako motywu, bo ma równoległy system motywów.

### 7.2 Porównanie stosów

| Stos | Za | Przeciw | Werdykt |
|---|---|---|---|
| **WPF .NET 10 + wbudowany Fluent** | dojrzałe, gęste kontrolki danych z wirtualizacją; najlepsze UIA/Narrator; projektant VS; CommunityToolkit.Mvvm; łatwe MSI | WPF0001; błędy DataGrid; brak części kontrolek; brak Native AOT i trimmingu; brak automatycznej skali tekstu | **wybrany** |
| WPF + biblioteka WPF UI jako motyw | uzupełnia brakujące kontrolki | drugi system motywów, jeden opiekun | tylko wybrane kontrolki MIT |
| WinUI 3 / Windows App SDK 2.x (2.5.1 stabilne, IX 2026) | najnowszy Fluent, `x:Bind`, skala tekstu | brak stabilnego DataGrid (TableView tylko w 2.5.4-experimental z 30.09.2026); brak projektanta XAML; cięższe środowisko; mniej gęsty | odrzucony |
| Avalonia 12 | szybki rendering, dojrzały Fluent | najlepsza siatka (TreeDataGrid) od X 2025 płatna (Accelerate), darmowy DataGrid tylko z poprawkami; dostępność UIA w Windows mniej dojrzała (do weryfikacji w laboratorium); wieloplatformowość niepotrzebna | odrzucony |
| Aplikacja WWW z bramą (styl WAC) | brak instalacji, świetne siatki przeglądarkowe | brama staje się zasobem Tier-0, potrzebna delegacja Kerberos lub przekazywanie poświadczeń, dodatkowe opóźnienie | odrzucony |

Plan B: komercyjna siatka (DevExpress GridControl z `InfiniteAsyncSource`/`PagedAsyncSource`, Telerik, Syncfusion),
decyzja po teście wydajności w fazie 0 (100 tys. obiektów, 60 kl./s przez RDP, pierwsza strona < 300 ms).

### 7.3 Wirtualizacja UI

- Jedna receptura dla list i drzewa: `IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Item` dla list
  ze stałą wysokością wiersza (`Pixel` dla drzewa), `CacheLength="1,1"` w stronach, `CanContentScroll=True`; w DataGrid
  edytora zbiorczego dodatkowo `EnableColumnVirtualization=True` (domyślnie wyłączone). Główna lista to ListView +
  GridView (mniej warstw na komórkę niż DataGrid).
- **Wirtualizację po cichu wyłączają:** lista w ScrollViewer, pionowym StackPanel lub wierszu Grid z wysokością Auto;
  `CanContentScroll=False`; niewirtualizujący panel; grupowanie bez `IsVirtualizingWhenGrouping`; źródło `IEnumerable`
  zamiast `IList`; kolumny `Auto`/`SizeToCells`; `Items` zamiast `ItemsSource`. Test UI porównuje liczbę
  zrealizowanych kontenerów z liczbą widocznych wierszy.
- Lekkie wiersze: gotowe teksty, `Mode=OneTime`, bez konwerterów na ścieżce krytycznej, zamrożone wspólne ikony,
  szerokości stałe lub gwiazdkowe; przy 100 tys.+ wierszy `IsDeferredScrollingEnabled` albo opóźnianie pobierania stron
  podczas przeciągania suwaka.

### 7.4 Wirtualizacja danych, sortowanie i zaznaczanie

- `VirtualDirectoryList` (nieogólne `IList` + `INotifyCollectionChanged`) oparty na VLV: `Count` z `ContentCount`,
  indeksator zwraca wiersz-zaślepkę („Wczytywanie”) i zamawia stronę 100–250 wierszy, a po jej nadejściu wypełnia
  zaślepki. LRU 5–10 stron tylko w RAM, czyszczony przy nawigacji, F5, zmianie filtra, sortowania i tożsamości – to
  bufor widoku, a nie kopia katalogu.
- `ICollectionView` z sortowaniem lub filtrem nad listą wirtualizowaną wylicza wszystko (każda strona = zapytanie LDAP)
  i sortuje dane częściowe – zakazane. Kliknięcie nagłówka: `e.Handled = true` i nowe zapytanie z kluczem sortowania,
  tylko dla kolumn indeksowanych. Małe, kompletne zbiory: `ListCollectionView.CustomSort` z komparatorem `pl-PL`, osobny
  widok na kontrolkę, `DeferRefresh()`, bez „live shaping”; strony dopisujemy hurtem, a nie element po elemencie.
- Zaznaczenie to `HashSet<objectGUID>`, nie referencje do wierszy. Ctrl+A na 50 tys. wierszy oznacza **zaznaczenie
  zapytaniem** („Wszystkie 12 345 obiektów pasujących do filtra w OU=Pracownicy” – baner jak w Gmailu), które silnik
  zbiorczy strumieniuje ponownie w chwili wykonania.

### 7.5 Drzewo

Drzewo zawiera tylko kontenery (`domainDNS`, `organizationalUnit`, `container`, `builtinDomain`; w widoku
zaawansowanym także `lostAndFound`, `msDS-QuotaContainer` itp.). `HierarchicalDataTemplate` nad modelem węzła z jednym
dzieckiem-zaślepką, żeby pokazać strzałkę; `IsExpanded` wiązane dwukierunkowo. Pierwsze rozwinięcie = zapytanie
OneLevel o kontenery z `(!(showInAdvancedViewOnly=TRUE))`, gdy widok zaawansowany jest wyłączony; zwinięcie anuluje
ładowanie. Przy wirtualizacji `ContainerFromItem` zwraca `null` dla niezrealizowanych węzłów, więc przejście do DN
(pasek adresu, wyszukiwanie) rozwija przodków po kolei w modelach widoku, czeka na ich dzieci i używa
`BringIndexIntoViewPublic`. F5 na węźle odświeża tylko jego dzieci i zachowuje rozwinięcia po DN.

### 7.6 Wzorce UX

| Wzorzec | Opis | Inspiracja |
|---|---|---|
| Trzy panele | drzewo / lista / zwijany panel szczegółów z kluczowymi atrybutami, znacznikami stanu i szybkimi akcjami (reset hasła, odblokuj, włącz/wyłącz, dodaj do grupy, przenieś, kopiuj DN) | ADUC, WAC, Entra |
| Niemodalne karty właściwości | zakładki lub okna, kilka naraz, OK/Anuluj/Zastosuj, odświeżenie, zapis tylko różnic | ADUC (parytet kart) |
| Paleta poleceń (Ctrl+K) | rozmyte dopasowanie w rejestrze poleceń (id, tytuł PL/EN, słowa kluczowe, skrót, kontekst, `CanExecute`); prefiksy `>` polecenia, `@` obiekty (ANR), `/` przejdź do OU | VS Code |
| Wyszukiwanie globalne (Ctrl+E lub `/`) | ANR z opóźnieniem, wyniki grupowane według typu; Enter – panel szczegółów, Ctrl+Enter – nowa karta, Alt+Enter – właściwości | ADAC Global Search |
| Pasek adresu (Ctrl+L) | ścieżka kanoniczna (`contoso.test › Firma › Pracownicy › IT`), lista dzieci przy każdym segmencie; tryb edycji przyjmuje DN, `canonicalName` lub `<GUID=…>` | ADAC, Eksplorator |
| Chipy filtrów | szybkie filtry łączone przez AND, zawsze wykonywane jako filtr LDAP na serwerze (4.5) | Entra |
| Konfigurowalne widoki (wymaganie klienta) | „przestrzenie robocze” dla podziałów OU (np. Pracownicy, Komputery, Kontrahenci, Wyłączone): przypięte OU (objectGUID + DN, ostrzeżenie, gdy cel zniknie), zapisane zapytania, zestawy kolumn, chipy, szablony przypisane do OU, wersje dla ról (helpdesk/administrator); eksport/import JSON i link do widoku; zapisywane wyłącznie jako konfiguracja | ADAC (węzły), Entra („Copy link to current view”) |
| Karty (Ctrl+T/W/Tab) | kontener, wyszukiwanie lub zapytanie w karcie, każda z własną historią wstecz/dalej | przeglądarki |
| Centrum aktywności (Ctrl+Shift+A) | kolejka zapisów i zadań zbiorczych: Queued/Running/Succeeded/Partial/Failed/Cancelled, postęp, wyniki per obiekt, „Ponów nieudane”, anuluj, „Kopiuj raport”; znacznik w pasku stanu | Entra „Bulk operation results” |
| Powiadomienia w aplikacji | InfoBar/toasty w prawym dolnym rogu, historia tylko w sesji, ogłaszane przez UIA; **żadnych DN ani nazw kont w powiadomieniach systemowych** (Centrum akcji zapisuje je na dysku) | |
| Pasek tożsamości | podmiot, DC, tryb szyfrowania, kolor poziomu (tier), Protected Users, czas do wygaśnięcia sesji | WAC „Manage as” |
| „Stan na” | każda lista pokazuje czas odczytu i liczbę („428 obiektów · 0,17 s · stan na 14:35:02”), F5 odczytuje ponownie | |
| Historia PowerShell | dokowany panel z równoważnymi poleceniami, tylko w pamięci, bez sekretów | ADAC |
| Porównanie obiektów | dwa obiekty obok siebie, atrybut po atrybucie | — |

### 7.7 Skróty klawiaturowe

| Zgodne z ADUC/MMC | Dodatkowe |
|---|---|
| F5 odśwież; Del usuń (z potwierdzeniem i kontrolą ochrony); F2 zmień nazwę; Alt+Enter właściwości; Enter otwórz; Shift+F10 / klawisz menu; Tab/F6 przełączanie paneli; Backspace poziom wyżej; Alt+←/→ wstecz/dalej; Ctrl+A; `*`, `+`, `-` w drzewie; wpisywanie liter = skok w liście (VLV) | Ctrl+K paleta; Ctrl+E lub `/` wyszukiwanie globalne; Ctrl+F szukaj w kontenerze; Ctrl+Shift+F wyszukiwanie zaawansowane; Ctrl+L pasek adresu; Ctrl+T/W/Tab karty; Ctrl+C nazwa, Ctrl+Shift+C DN; Ctrl+Shift+A centrum aktywności; Esc anuluj; Ctrl+Plus/Minus/0 powiększenie; F1 pomoc |

Wszystkie skróty pochodzą z jednego rejestru poleceń, który zasila też menu, paletę i podpowiedzi; każda akcja ma
ścieżkę klawiaturową i klawisz dostępu (`_` w etykiecie). Zmiana przypisań – w późniejszej fazie.

### 7.8 Dostępność

`AutomationProperties.Name` dla elementów z samą ikoną, komórek graficznych i pola wyszukiwania; `LabeledBy`,
`HelpText`, `AcceleratorKey`; regiony na żywo (`LiveSetting=Polite`) dla komunikatów typu „Wczytano 250 z ok. 12 000”;
wiersze z czytelną nazwą, zaślepki czytają „Wczytywanie”. Tylko pędzle `DynamicResource`, ikony z fontu dziedziczące
`Foreground` (wysoki kontrast), status zawsze z ikoną i tekstem. WPF nie stosuje sam ustawienia „Rozmiar tekstu” –
czytamy `UISettings.TextScaleFactor` (TFM `net10.0-windows10.0.19041.0`) i oferujemy powiększenie; ograniczony ruch
przy wyłączonych animacjach i w RDP (`SystemParameters.IsRemoteSession`); manifest `PerMonitorV2`. Testy: Narrator,
NVDA, Accessibility Insights, FlaUI; cel WCAG 2.2 AA / EN 301 549 (kontrast 4,5:1).

### 7.9 Lokalizacja PL/EN

- `.resx`: angielski jako neutralny, polski jako satelita; `SatelliteResourceLanguages=en;pl`; przełączanie języka w
  trakcie pracy przez źródło wiązań z indeksatorem. `FrameworkElement.LanguageProperty.OverrideMetadata(...)` z bieżącą
  kulturą – inaczej wiązania formatują daty i liczby jak en-US.
- Trzy formy liczby mnogiej (1 użytkownik / 2–4 użytkownicy / 5+ użytkowników) obsługuje pomocnik, a nie
  `string.Format`; polskie etykiety są ok. 30% dłuższe i układ musi to wytrzymać.
- Etykiety klas i atrybutów z `CN=415` (jak w polskim ADUC), z zapasem w `CN=409` i we własnych zasobach (Samba nie ma
  `CN=415`). Sortowanie lokalne komparatorem `pl-PL`; porządek sortowania serwera z polskimi znakami
  – do weryfikacji w laboratorium.

### 7.10 Struktura prezentacji, diagnostyka i dystrybucja

- **MVVM:** CommunityToolkit.Mvvm 8.4.2+ (`[ObservableProperty]` na właściwościach częściowych,
  `[RelayCommand(IncludeCancelCommand = true)]`, `ObservableValidator`, `WeakReferenceMessenger` jako sygnał „odczytaj
  ponownie”, a nie pamięć podręczna); modele widoku w `net10.0` bez typów WPF; Generic Host; obsługa nieobsłużonych
  wyjątków; okno widoczne przed połączeniem z DC.
- **Diagnostyka:** `[LoggerMessage]`, EventSource/EventLog; w trwałych dziennikach żadnych DN, wartości, filtrów, tekstu
  wyszukiwania ani poświadczeń; szczegóły tylko do ETW; opcjonalna redakcja (`Microsoft.Extensions.Compliance.Redaction`).
- **Środowisko:** .NET 10 LTS (wsparcie do 14.11.2028; .NET 8 i 9 kończą je 10.11.2026). WPF nie obsługuje trimmingu ani
  Native AOT – start przyspiesza ReadyToRun.
- **Dystrybucja:** podpisany MSI instalowany per maszyna do Program Files (WiX; zasady licencji v6 – do weryfikacji),
  wdrażany przez Intune, ConfigMgr lub GPO. Single-file z `IncludeNativeLibrariesForSelfExtract` rozpakowuje natywne
  biblioteki do `%TEMP%`, co blokuje AppLocker/WDAC na stacjach PAW – wersja przenośna tylko jako dodatek. MSIX jest
  niewygodny w połączeniu z `runas /netonly`.
- **Podpisywanie:** Authenticode ze znacznikiem czasu RFC 3161, certyfikat z wewnętrznego AD CS (reguły wydawcy w
  AppLocker/WDAC) albo Azure Trusted Signing, podpis wyłącznie w CI (ważność publicznych certyfikatów ma spaść do ok. 460
  dni – do weryfikacji). Manifest `asInvoker`, bez autoaktualizacji z internetu i bez telemetrii.

---

## 8. Bezpieczeństwo i poświadczenia

### 8.1 Windows Server 2025 – domyślne ustawienia, które klient musi spełnić

- **Podpisywanie LDAP** jest wymagane domyślnie w nowych wdrożeniach (zasada „Domain controller: LDAP server signing
  requirements enforcement” ma pierwszeństwo); zaktualizowane lasy zachowują ustawienia, ale bez żadnej zasady WS2025
  wymaga podpisu. Klienci WS2025 i Windows 11 24H2+ domyślnie szyfrują LDAP.
- **Channel binding** (`LdapEnforceChannelBinding`: 0 Never / 1 When supported / 2 Always): w nowych WS2025 „When
  supported”, w 2022 i starszych „Never”; dotyczy tylko bindów SASL przez TLS.
- **Atrybuty poufne** (`searchFlags` 0x80, np. LAPS i BitLocker) tylko przez szyfrowane połączenie; LDAPS z TLS 1.3.
- KDC nie wydaje TGT z RC4 (konta potrzebują kluczy AES), zdalne zmiany haseł przez SAM-RPC są zablokowane (zostaje
  `unicodePwd`), ustawienie domyślnego hasła konta komputera jest blokowane, Credential Guard jest domyślnie włączony,
  a podpisywanie SMB wymagane dla połączeń wychodzących. Microsoft zaleca wyłączanie NTLM wszędzie, gdzie to możliwe.
- **Oś czasu:** ADV190023 (2019) → zasada CBT i zdarzenia 3039–3041 (III 2020) → audyt CBT domyślnie (III 2023) →
  zdarzenia 3074/3075 (VIII/X 2023) → domyślne ustawienia WS2025 (XI 2024). Według KB4520412 aktualizacje nie zmienią
  ustawień istniejących DC; doniesienia o fazach wymuszenia w 2026 r. są niepotwierdzone. Zdarzenia: 2886–2889
  (podpisywanie), 3039–3041 i 3074/3075 (CBT).

### 8.2 Macierz wiązania

| Wariant | Ochrona | Decyzja |
|---|---|---|
| 389 + SASL Negotiate ograniczony do Kerberos (`AuthType.Kerberos`) + `Signing` i `Sealing` | poufność, integralność, tożsamość serwera, bez PKI | **domyślny** |
| 636 LDAPS (GC 3269) lub StartTLS na 389 + SASL Kerberos; CBT dodaje klient Windows | najsilniejsza konfiguracja według Microsoftu (TLS + Kerberos + wymuszony CBT) | opcja; **bez** podpisywania/szyfrowania SASL na TLS – AD odrzuca taki bind („Cannot bind using sign/seal on a connection on which TLS or SSL is in effect”; tekst do weryfikacji w laboratorium) |
| `AuthType.Negotiate` (dopuszcza NTLM) | jak wyżej, ale możliwy cichy NTLM | wyłączony; tylko gdy pozwala zasada administracyjna |
| Simple bind (`AuthType.Basic`), anonimowy | na 389 brak ochrony; przy wymuszonym podpisywaniu błąd 8 strongerAuthRequired; na TLS hasło trafia do serwera bez CBT | **zakazany** (analizator zabronionych API) |

`AuthType.Kerberos` to w S.DS.P nadal `LDAP_AUTH_NEGOTIATE`, ale z `packageList = Kerberos`, co blokuje przejście na
NTLM (wymagane dla Protected Users). Łączymy się zawsze z FQDN kontrolera – IP, krótka nazwa lub CNAME bez SPN dają
NTLM. Certyfikat LDAPS: EKU Server Authentication (`1.3.6.1.5.5.7.3.1`), FQDN w CN lub SAN (przy wiązaniu
bezserwerowym także nazwa domeny), zaufany łańcuch; magazyn usługi NTDS ma pierwszeństwo. Callback
`VerifyServerCertificate` przejmuje pełną odpowiedzialność za weryfikację – albo go nie ustawiamy, albo budujemy w nim
`X509Chain` z odwołaniami, EKU i dopasowaniem nazwy; nigdy `(c, cert) => true`. Panel „Bezpieczeństwo połączenia”
pokazuje `SslInformation`, odcisk i ważność certyfikatu.

### 8.3 Alternatywne poświadczenia

| Sposób | Gdzie jest hasło | Ocena |
|---|---|---|
| `NetworkCredential` przekazany do `LdapConnection` | S.DS.P trzyma kopię (`_directoryCredential`) przez cały czas życia połączenia, żeby móc ponowić bind; getter `Password` zamienia `SecureString` na zwykły `string` | odrzucony (`SecureString` na .NET Core nie szyfruje – DE0001) |
| **`LogonUserW(LOGON32_LOGON_NEW_CREDENTIALS = 9, LOGON32_PROVIDER_WINNT50 = 3)` + `WindowsIdentity.RunImpersonated(token, () => conn.Bind())` z `Credential = null`** | w LSA (w LSAIso przy Credential Guard); proces trzyma tylko uchwyt tokenu, a jawne hasło istnieje w nim przez mikrosekundy | **wybrany** – „runas /netonly” w procesie |
| Ponowne uruchomienie przez `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)` | jak wyżej, ale tożsamość dziedziczy cały proces i narzędzia potomne | zapas, gdyby wariant w procesie zawiódł w teście fazy 0 |
| Zewnętrzne `runas /netonly` / „Uruchom jako inny użytkownik” | poza aplikacją | działa (bieżąca tożsamość procesu) |
| Konta `SMARTCARD_REQUIRED` / karta inteligentna | nie da się ich podać jako `NetworkCredential` | „Uruchom jako” albo LDAPS z certyfikatem klienta i SASL EXTERNAL – później |

- Hasło z `PasswordBox.SecurePassword` → `Marshal.SecureStringToGlobalAllocUnicode` → `LogonUserW` →
  `ZeroFreeGlobalAllocUnicode`. Nazwa w formacie UPN wymaga `lpszDomain = NULL`; nie działa z gMSA; nie wymaga
  `SeTcbPrivilege`; lokalnie logowane jako zdarzenie 4624 typu 9, które obserwują systemy EDR.
- `NEW_CREDENTIALS` **nie sprawdza hasła**, więc od razu bind i WhoAmI (`1.3.6.1.4.1.4203.1.11.3`) z porównaniem
  zwróconego `u:DOMENA\nazwa`. `AutoBind = false`, `ReferralChasing = None` i własny reconnect (impersonacja, bind,
  WhoAmI) – inaczej ponowny bind na wątku bez impersonacji po cichu działa jako tożsamość procesu. Ponowne połączenie po
  rozłączeniu bezczynnym (900 s) nie wymaga hasła.
- Test fazy 0 sprawdza, że DC widzi Kerberos, a nie NTLM (4624 na DC), że reconnect nie pyta o hasło i że błędne hasło
  wychodzi przy bindzie.

**Menedżera poświadczeń Windows nie używamy.** Wpis `CRED_TYPE_DOMAIN_PASSWORD` sprawia, że każdy proces sesji
(Eksplorator, przeglądarka, Office) uwierzytelnia się do wskazanych hostów jako administrator; `CRED_TYPE_GENERIC`
chroni wyłącznie DPAPI użytkownika, więc w praktyce odczyta go każdy kod tego użytkownika; przechowywanie poświadczeń
wyższego poziomu na stacji niższego poziomu łamie model warstwowy.
Zapamiętujemy tylko nazwę użytkownika, domenę i preferowany DC (ewentualny wyjątek: zasada ADMX, tylko Tier-2,
`CRED_PERSIST_SESSION`).

### 8.4 Protected Users i sesja

Członkowie Protected Users (RID 525, DFL 2012 R2+): bez NTLM, bez DES i RC4 w preautentykacji, bez delegacji, TGT
maks. 240 minut bez odnawiania, brak buforowanych poświadczeń na urządzeniu. Wiążemy się więc tylko Kerberosem do FQDN;
gdy `tokenGroups` operatora zawiera SID `-525`, sesja trwa maks. 4 h z odliczaniem w pasku tożsamości;
`KDC_ERR_ETYPE_NOSUPP` tłumaczymy jako „konto nie ma kluczy AES – zresetuj hasło”. Niezależnie od tego blokada po
bezczynności (domyślnie 15 min, ADMX) i maksymalny czas sesji zwalniają token i połączenia.

### 8.5 Model warstwowy i PAW

- Tier 0 to płaszczyzna tożsamości (DC, AD CS, AD FS, Entra Connect, ich administratorzy, Backup/Server Operators i
  systemy zarządzające), Tier 1 – serwery i aplikacje, Tier 2 – stacje i helpdesk. „Zaufanie zaczyna się od pierwszej
  fizycznej klawiatury”; każdy poziom potrzebuje PAW tego samego poziomu; cel: mniej niż 5 osób z prawami Domain Admin;
  zarządca grupy (`managedBy`) zawsze z tego samego poziomu.
- Narzędzie używane przez administratora Tier-0 samo staje się interfejsem Tier-0: podpis Authenticode, Program Files,
  bez autoaktualizacji z internetu, wtyczek z lokalizacji zapisywalnych i telemetrii, zgodność z WDAC/AppLocker,
  Credential Guard i ochroną LSA.
- Po bindzie klasyfikujemy operatora (dobrze znane RID-y w `tokenGroups`) i komputer (DN z `GetComputerObjectName` w OU
  PAW lub członkostwo w grupie PAW). Poza PAW przed polem hasła pojawia się „nie wpisuj tu poświadczeń Tier-0”; konto
  Tier-0 poza PAW to czerwony baner i wpis w dzienniku albo blokada, jeśli tak mówi zasada. Odmowy silosów
  uwierzytelniania (zdarzenia Microsoft-Windows-Authentication 101, 105, 106, 305, 306; silosy wymagają FAST) mapujemy na
  czytelne komunikaty.

### 8.6 UI świadome uprawnień i jego ograniczenia

- Przy otwarciu karty właściwości lub menu kontekstowego wysyłamy **jedno** wyszukiwanie bazowe z wyświetlanymi
  atrybutami oraz `allowedAttributesEffective`, `allowedChildClassesEffective`, `sDRightsEffective` i
  `msDS-User-Account-Control-Computed`. Pole spoza `allowedAttributesEffective` jest tylko do odczytu z kłódką i
  wyjaśnieniem; menu New pokazuje klasy z `allowedChildClassesEffective` ([ADR-0006](adr/0006-ui-swiadome-uprawnien.md)).
  W listach i wyszukiwaniach Subtree tych atrybutów nie żądamy (koszt CPU na DC) – tylko przy wyborze obiektu i w
  walidacji partii.
- **Ograniczenia:** nie obejmują praw rozszerzonych (Reset Password, Send As), zapisów walidowanych, usuwania,
  przenoszenia ani odczytu. Dla nich AuthZ: `nTSecurityDescriptor` z SD Flags 0x7 i `tokenGroups` operatora →
  `AuthzInitializeResourceManager(AUTHZ_RM_FLAG_NO_AUDIT)` → `AuthzInitializeContextFromSid(AUTHZ_SKIP_TOKEN_GROUPS)` →
  `AuthzAddSidsToContext` → `AuthzAccessCheck` z `OBJECT_TYPE_LIST` (poziom 0: klasa, poziom 1: GUID prawa lub
  atrybutu) i `PrincipalSelfSid` celu. Reguły: usunięcie = `DELETE` 0x10000 lub `DELETE_CHILD` 0x2 u rodzica; drzewo =
  `DELETE_TREE` 0x40; tworzenie = `CREATE_CHILD` 0x1; przeniesienie = usuwanie w źródle, `CREATE_CHILD` w celu i zapis
  nazwy.
- Wynik to **podpowiedź**, nie blokada: AuthZ nie widzi oświadczeń, ACE warunkowych i przypadków szczególnych, a
  autorytetem jest DC. Ścieżka optymistyczna zawsze działa – próbujemy i mapujemy błąd 50 na konkretny komunikat.
- `adminCount=1`: SDProp na PDC co 60 min (`AdminSDProtectFrequency`, 60–7200 s) nadpisuje ACL obiektów chronionych
  wzorcem z `CN=AdminSDHolder,CN=System` i wyłącza dziedziczenie, więc delegacja helpdesku na OU ich nie obejmuje, a
  ręczne zmiany ACL znikają. Pokazujemy znacznik z wyjaśnieniem przed próbą operacji i wykrywamy osierocone
  `adminCount` (pozostawanie flagi po usunięciu z grupy i wpływ znaku 16 `dSHeuristics` – do weryfikacji w laboratorium).

| Prawo / zestaw | GUID | Uwaga |
|---|---|---|
| Reset Password (User-Force-Change-Password) | `00299570-246d-11d0-a768-00aa006e0529` | user, computer, inetOrgPerson, MSA |
| Change Password | `ab721a53-1e2f-11d0-9819-00aa0040529b` | „User cannot change password” = Deny na tym prawie |
| Self-Membership / atrybut `member` | `bf9679c0-0de6-11d0-a285-00aa003049e2` | zapis walidowany |
| Validated-DNS-Host-Name / Validated-SPN | `72e39547-7b18-11d1-adef-00c04fd8d5cd` / `f3a64788-5306-11d1-a9c5-0000f80367c1` | (do weryfikacji w laboratorium) |
| Personal-Information | `77b5b886-944a-11d1-aebd-0000f80367c1` | telefony, adresy, ale też `userCert`, `msDS-SupportedEncryptionTypes` |
| Public-Information | `e48d0154-bcf8-11d1-8702-00c04fb96050` | m.in. `title`, `department`, `company`, `manager`, ale też **SPN, `altSecurityIdentities`, `msDS-AllowedToDelegateTo`, UPN** |
| General-Information | `59ba2f42-79a2-11d0-9020-00c04fc2d3cf` | `sAMAccountName`, `displayName`, `sIDHistory`… |
| User-Account-Restrictions | `4c164200-20c0-11d0-a768-00aa006e0529` | `userAccountControl`, `pwdLastSet`, `accountExpires`, od 2012 także RBCD |
| Membership | `bc0ac240-79a9-11d0-9020-00c04fc2d4cf` | `member`, `memberOf` |

**Ryzyka typowej delegacji helpdesku**, które pokazuje interfejs: prawo zapisu `userAccountControl` pozwala ustawić
`DONT_REQ_PREAUTH` 0x400000 (AS-REP roasting), `PASSWD_NOTREQD` 0x20 i `DONT_EXPIRE_PASSWD` 0x10000 (bity delegacji
wymagają dodatkowo `SeEnableDelegationPrivilege`); delegowanie całych zestawów właściwości odsłania SPN (Kerberoasting),
`altSecurityIdentities` (przejęcie przez mapowanie certyfikatu) i RBCD. W trybie helpdesku zbiorcze zmiany UAC
ograniczamy do 0x2, a zapisy tych atrybutów i dodawanie do grup zagnieżdżonych w grupach chronionych opatrujemy
ostrzeżeniem.

### 8.7 Klasyfikacja Tier-0 i podnoszenie uprawnień

Obiekt jest Tier-0, gdy: `adminCount=1`; RID lub członkostwo przechodnie trafia w 500, 502, 512, 516, 518, 519, 521,
526, 527, S-1-5-32-544/548/549/550/551/552 albo DnsAdmins/GPCO; ma `SERVER_TRUST_ACCOUNT` 0x2000 lub `primaryGroupID`
516/521; `isCriticalSystemObject=TRUE`; leży w skonfigurowanej liście OU Tier-0 (konta i serwery Entra Connect, AD CS,
AD FS); jest głową domeny, AdminSDHolder, OU Domain Controllers lub kontenerem GPO. Respektujemy też `systemFlags`
(`FLAG_DISALLOW_DELETE` 0x80000000, `FLAG_DOMAIN_DISALLOW_RENAME` 0x08000000, `FLAG_DOMAIN_DISALLOW_MOVE` 0x04000000 –
do weryfikacji w laboratorium). Sesje spoza Tier-0 widzą takie obiekty tylko do odczytu; zmiana wymaga „Podnieś
uprawnienia”: osobnego, krótkotrwałego połączenia z poświadczeniami Tier-0 (opcjonalnie tylko na PAW) i podania
powodu. Blokujemy przenoszenie obiektów Tier-0 do OU spoza Tier-0 i dodawanie członków do grup zagnieżdżonych w grupach
chronionych. W interfejsie i dokumentacji jasno: **to ochrona przed pomyłką, nie granica bezpieczeństwa.**

### 8.8 Audyt

| Strona | Źródło | Zawartość |
|---|---|---|
| DC (autorytatywne) | 5136 (zmiana obiektu: podmiot SID i Logon ID, DN/GUID/klasa, atrybut, „Value Deleted”/„Value Added” z tym samym Correlation ID), 5137 utworzenie, 5138 przywrócenie, 5139 przeniesienie, 5141 usunięcie (także drzewa), 4662 (prawo rozszerzone, odczyt poufny; wymaga SACL) | wymaga „Audit Directory Service Changes” i SACL; przepis MDI: wpis audytu Everyone/Success na korzeniu domeny dla obiektów potomnych user, group, computer, gMSA, MSA |
| DC – konta | 4720/4722/4723/4724/4725/4726/4738/4740/4767/4781 (użytkownicy), 4741–4743 (komputery), 4728/4729/4732/4733/4756/4757 (członkostwo), 4624 typ 3 (źródłowa stacja) | |
| Operator | kanał Dziennika zdarzeń rejestrowany przez MSI (manifest ETW; zwykły użytkownik nie utworzy źródła): kto, kiedy, DC, objectGUID, operacja, nazwy atrybutów, wynik, ID korelacji | **wartości tylko, gdy pozwala zasada; sekretów nigdy**; zbierany przez WEF/SIEM |
| Operator (opcja) | JSONL w `%LOCALAPPDATA%` z łańcuchem SHA-256, ACL tylko dla właściciela, retencja wg zasady, tryb wartości pełne/skrót/brak | **szara strefa** – wyłączony do decyzji klienta |

Korelacja: wpis w dzienniku aplikacji (DC, objectGUID, czas) odpowiada zdarzeniu 5136 na tym DC; jego Logon ID wskazuje
zdarzenie 4624 typu 3 ze stacją źródłową. Precedens formatu: skrypty Microsoft AD Tier Model (bloki po 100 ID, wzorzec
START / COMPLETE / ERROR, pola `klucz=wartość`, redakcja kluczy zawierających Password, Secret, Token, Key, Credential).
Opcjonalny test gotowości audytu odczytuje zasady i obecność SACL (SACL tylko z `SeSecurityPrivilege`).

### 8.9 Polityka „brak danych lokalnych”

Decyzja: [ADR-0003](adr/0003-brak-lokalnych-danych.md). Każde zapytanie idzie do DC, a dane żyją w pamięci tak długo,
jak są wyświetlane.

| Kategoria | Przykłady | Zasada |
|---|---|---|
| **Nigdy na dysku** | wyniki wyszukiwania, atrybuty obiektów, listy członków, schemat i display specifiers, bufory stron VLV, dzienniki cofania, wygenerowane hasła, wartości LAPS/BitLocker, poświadczenia; DN i wartości w dziennikach diagnostycznych i powiadomieniach systemowych | tylko pamięć sesji (zakres DI per sesja, czyszczony przy wylogowaniu, zmianie tożsamości i blokadzie) |
| **Dozwolone jako konfiguracja** | preferencje UI i zestawy kolumn; profile połączeń (użytkownik, domena, preferowany DC – bez haseł); zapisane widoki i zapytania (nazwy, DN przypiętych OU, filtry LDAP, kolumny, sortowanie); szablony, profile mapowania CSV, receptury; zasady ADMX | JSON w `%APPDATA%`; tryb „bez zapisu” dla ostrożnych |
| **Szara strefa – decyzja klienta** | automatyczna historia ostatnio odwiedzanych kontenerów (MRU); dziennik operatora z wartościami przed/po; eksporty CSV, plan, dziennik cofania, PS1, LDIF; zrzuty awaryjne | domyślnie wyłączone lub tylko na wyraźne polecenie operatora, z potwierdzeniem i zdarzeniem audytu |
| **Egzekwowanie** | brak API plikowego w warstwach Directory i Batch (reguła zabronionych API); eksport tylko przez `ExportService`; wykluczenie buforów sekretów ze zrzutów, brak pełnych zrzutów; wykluczenie sekretów z historii schowka; redakcja dzienników | **test CI z kanarkami:** scenariusz na obiektach-kanarkach, potem przeszukanie `%APPDATA%`, `%LOCALAPPDATA%`, `%TEMP%` i Dziennika zdarzeń |
| **Odrzucone podejścia** | DirSync (pełna synchronizacja od korzenia NC), migawki w stylu AD Explorer, pamięć właściwości ADSI, jakakolwiek usługa lub baza (wzorzec ManageEngine/Adaxes/OpenADUC) | — |

Szara strefa wynika z tego, że dziennik cofania, eksport czy plik raportu z wartościami atrybutów **są** lokalnymi
danymi katalogu, a jednocześnie bywają potrzebne operacyjnie. Domyślnie wszystko zostaje w pamięci, eksport jest
jawną czynnością operatora (opcjonalnie szyfrowany DPAPI `CurrentUser`), a nigdy nie zapisujemy automatycznie dziennika,
pamięci podręcznej ani migawki.

### 8.10 Higiena sekretów, RODO i zabezpieczenia w kodzie

- **Sekrety w interfejsie:** tylko `PasswordBox.SecurePassword` i `Clear()` po użyciu; skopiowane hasło z formatami
  schowka wykluczającymi historię, chmurę i monitory (`ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory=0`, `CanUploadToCloudClipboard=0` – do weryfikacji w laboratorium) i czyszczeniem po
  30–60 s; okno odsłonięcia hasła opcjonalnie z `WDA_EXCLUDEFROMCAPTURE`; bufory sekretów wykluczone ze zrzutów
  (`WerRegisterExcludedMemoryBlock`); `unicodePwd` w przypiętej tablicy zerowanej `CryptographicOperations.ZeroMemory`.
- **RODO:** eksport danych z AD to przetwarzanie danych osobowych, a dziennik operatora to dane o operatorach. Domyślnie
  tylko widoczne kolumny, kolumny wrażliwe z dodatkowym potwierdzeniem, nigdy sekrety (LAPS, BitLocker, hasła), zdarzenie
  eksportu bez danych (kto, kiedy, filtr, kolumny, liczba wierszy, typ celu), ostrzeżenie przy zapisie na dysk
  synchronizowany z chmurą, sieciowy lub wymienny, duża kopia siatki do schowka liczy się jako eksport, opcjonalne
  przełączniki ADMX (zakaz, limit wierszy, wymagany powód). Artykuły 5(1)(c), 5(1)(e), 25, 30, 32 i 33 podano z pamięci –
  do potwierdzenia z inspektorem ochrony danych.
- **Kod i build:** `BannedApiAnalyzers` (zakaz `AuthType.Basic`/`Anonymous`, `PasswordBox.Password`, gettera
  `NetworkCredential.Password`, `VerifyServerCertificate` poza zaufanym walidatorem); CA5392 na P/Invoke do `wldap32`,
  `authz`, `advapi32`, `credui`; manifest `asInvoker`; testy na DC WS2025 z wymuszonym podpisywaniem i
  `LdapEnforceChannelBinding=2`. Bind z poświadczeniem `NULL` pod tokenem ograniczonym przez UAC może blokować zapisy,
  jeśli aplikacja działa na samym DC.

---

## 9. Edycja zbiorcza – najlepsze praktyki

### 9.1 Potok i model planu

Każdy punkt wejścia – zaznaczenie, arkusz wielu obiektów, siatka, CSV, szablon, kreator, akcja z menu – tworzy **plan
zmian** dla jednego silnika ([ADR-0004](adr/0004-jeden-potok-zmian.md)). Edycja pojedynczego obiektu korzysta z niego z
automatycznie zatwierdzanym podglądem, więc walidacja, dziennik i cofanie działają wszędzie. Etapy: **Zbierz → Rozwiąż
tożsamości → Zaplanuj → Waliduj na żywo → Podgląd → Potwierdź → Wykonaj → Zweryfikuj → Raport → (Cofnij)**.

- `BatchJob`: tożsamość operatora (z WhoAmI), domena, przypięty DC (`dnsHostName`, `dsServiceName`, `invocationId`),
  źródło, opcje (współbieżność, `PermissiveModify`, progi błędów, tryb próbny). `ObjectPlan`: `objectGUID`, DN i
  `uSNChanged` z chwili planowania, klasa, odwołanie do źródła (np. wiersz CSV), kroki, stan (Pending, Validated,
  Conflict, Applying, Applied, PartiallyApplied, Failed, Skipped, Unknown). `AttributeChange`: atrybut, operacja
  (Add/Delete/Replace/Clear), stare i nowe wartości, składnia, krotność, pochodzenie, problemy (Blocker/Warning/Info).
- Kroki w stałej kolejności: utworzenie → hasło → modyfikacja (**jeden `ModifyRequest` na obiekt** – atomowy według RFC
  4511) → członkostwo (zapis na grupie) → zmiana nazwy → przeniesienie → usunięcie. Obiekty adresujemy przez
  `<GUID=…>`, więc przeniesienie w tej samej partii nie psuje kolejnych kroków. Zmiany bez efektu (równe według reguły
  dopasowania atrybutu) i obiekty bez zmian znikają z planu.
- AD nie ma transakcji wieloobiektowych (RFC 5805 nie jest obsługiwane): partia to N niezależnych atomowych operacji z
  wynikiem per obiekt.

### 9.2 Źródła wyboru i sposoby edycji

- **Źródła:** wielokrotne zaznaczenie, zaznaczenie zapytaniem (7.4), zapisane zapytanie lub wynik raportu („działaj na
  wynikach”), koszyk z obiektami z różnych OU (wzorzec Adaxes), wklejona lista rozwiązywana po `sAMAccountName`, UPN,
  `mail`, `employeeID`, DN lub GUID ze statusem każdej linii (0 lub > 1 dopasowań oznaczone), CSV z kolumną-kluczem.
- **Arkusz wielu obiektów:** parytet z ADUC, ale z trzema stanami pola (bez zmian / ustaw / wyczyść), znacznikiem
  „wartości mieszane” i dowolnym atrybutem schematu dla dowolnej klasy; tokeny `%username%` i `{sAMAccountName}`.
- **Siatka:** `DataGrid` z wirtualizacją kolumn i edycjami „na brudno” (zmiany wyróżnione, błędy na czerwono z
  podpowiedzią), wklejanie TSV z Excela, Ctrl+D wypełnij w dół, Ctrl+Enter zastosuj do zaznaczenia, Delete = oznacz do
  wyczyszczenia, Ctrl+Z. **Zatwierdzenie komórki niczego nie zapisuje w AD** – zapis dopiero po podglądzie.

| Operacja | Realizacja LDAP | Uwagi |
|---|---|---|
| Ustaw / zastąp | Replace | idempotentne |
| Wyczyść | Replace bez wartości | idempotentne; ignorowane, gdy atrybutu brak |
| Dopisz / usuń wartość | Add / Delete z `PermissiveModify` | `member`, `proxyAddresses`, `otherTelephone`, `servicePrincipalName`, `url` |
| Znajdź i zamień | Replace wyliczony w planie | tekst lub regex z `RegexOptions.NonBacktracking` albo limitem czasu, poza wątkiem UI (ReDoS); liczba trafień przed zastosowaniem |
| Mapa wartości / wyrażenie z tokenami | Replace | np. `title` → `department`; szablony – 9.10 |
| Flagi UAC | odczyt-modyfikacja-zapis | w trybie helpdesku tylko 0x2 |
| Włącz / wyłącz / odblokuj | UAC 0x2 / `lockoutTime = 0` | |
| Wygaśnięcie konta | `accountExpires` | „koniec dnia D” = (D+1) 00:00 lokalnie → UTC; „nigdy” = `0x7FFFFFFFFFFFFFFF` (lub 0) |
| Kraj / przełożony | `c` + `co` + `countryCode` razem / `manager` (DN z selektora) | `directReports` tylko do odczytu |
| Przenieś / zmień nazwę | ModifyDN | kontrola ochrony przed usunięciem; komputerów nie przemianowujemy (robi się to na maszynie) |
| Członkostwo | Add/Delete `member` na grupie | 9.5 |
| Ochrona przed usunięciem | edycja DACL (obiekt + rodzic) | jawna opcja „zdejmij ochronę → działaj → przywróć”, logowana |
| Znacznik w opisie | Replace `description` | np. „Wyłączono 2026-10-13 przez …: nieaktywne 90 dni” |
| Usuń | Delete, przy dzieciach Tree Delete | potwierdzenie wpisywane, status Kosza AD |

### 9.3 Walidacja

- **Schemat** (z pamięci sesji): składnia → typ (2.5.5.12 Unicode, .9 Int32, .16 Int64/FILETIME, .1 DN, .10 oktety,
  .8 Boolean, .11 GeneralizedTime); `isSingleValued` (`description` jednowartościowy na klasach SAM);
  `rangeLower`/`rangeUpper`; odrzucanie `systemOnly`, łączy zwrotnych (zapisujemy łącze w przód) i atrybutów
  konstruowanych; atrybut musi być dozwolony dla klas obiektu.
- **Reguły domeny:** `sAMAccountName` maks. 20 znaków, bez `" / \ [ ] : ; | = , + * ? < >` i kropki na końcu, unikalny w
  domenie; sufiks UPN z domen lasu lub `uPNSuffixes`; UPN unikalny w lesie (GC + partia); DN w `manager`, `managedBy`,
  `member` muszą się rozwiązywać (`VerifyNameControl` dla innych domen); spójne `c`/`co`/`countryCode`;
  `accountExpires` w przyszłości (ostrzeżenie).
- **Uprawnienia:** `allowedAttributesEffective` per obiekt planu, `allowedChildClassesEffective` dla OU docelowych,
  `sDRightsEffective` dla zmian DACL; prawa rozszerzone i usuwanie przez AuthZ (8.6) albo „sprawdzane przy wykonaniu”.
- **Flagi ryzyka:** Tier-0, `adminCount=1`, `isCriticalSystemObject`, ochrona przed usunięciem, obiekty z dziećmi,
  grupy wbudowane.

| Atrybut | Składnia, krotność | Limit | Uwagi |
|---|---|---|---|
| `department`, `company` | 2.5.5.12, jedna | 64 | `fCOPY` |
| `title` / `physicalDeliveryOfficeName` | jedna | 128 | bez `fCOPY`; Office jest w ANR i indeksowany |
| `description` | wiele w schemacie, jedna na SAM | 1024 | dwie wartości = błąd |
| `telephoneNumber`, `mobile`, `facsimileTelephoneNumber`, `ipPhone` / `otherTelephone` | jedna / wiele | 64 | |
| `employeeID` / `employeeNumber` | jedna | 16 / 512 | **bez indeksu i bez gwarancji unikalności** |
| `employeeType`, `division` | jedna | 256 | `fCOPY` |
| `manager` / `managedBy` | DN, jedna | — | `linkID` 42 / 72; `manager` z `fCOPY` |
| `mail` / `proxyAddresses` | jedna / wiele | 256 / 1123 | indeks; `proxyAddresses` w ANR, porównanie bez wielkości liter; unikalność niewymuszana |
| `userPrincipalName` | jedna | 1024 | indeks, unikalny w lesie (WS2012 R2+) |
| `sAMAccountName` / `cn` | jedna / RDN | 20 w praktyce / 64 | zakazane znaki / tylko przez ModifyDN |
| `givenName`, `sn` / `initials` / `displayName` | jedna | 64 / 6 / 256 | |
| `info` / `wWWHomePage` / `location` | jedna | 1024 / 2048 / 1024 | |
| `c` / `co` / `countryCode` | jedna | 3 / 128 / int | `fCOPY`, zawsze razem |
| `extensionAttribute1–15` / `msDS-cloudExtensionAttribute1–20` | jedna | — | schemat Exchange (wykrywany) / WS2012+ |

Wartości pochodzą ze zrzutu schematu 2008 R2; w działaniu czytamy je ze schematu klienta, który mógł zostać rozszerzony.

### 9.4 Współbieżność optymistyczna

- `uSNChanged` odczytany na przypiętym DC przy planowaniu (jest lokalny dla DC, więc porównanie ma sens tylko dzięki
  przypięciu). Przed wykonaniem ponownie czytamy `uSNChanged` i zaplanowane atrybuty; przy zmianie USN porównujemy tylko
  zaplanowane atrybuty (inaczej fałszywe konflikty od np. `lastLogonTimestamp`). Zmieniony atrybut oznacza **Conflict**
  z wyborem: pomiń, nadpisz, zaplanuj ponownie.
- **CAS** dla atrybutów jednowartościowych: Delete(stara) + Add(nowa) w jednym `ModifyRequest` – atomowo, a gdy ktoś
  zmienił wartość, DC odpowiada błędem 16. **Nigdy razem z `PermissiveModify`**, który ten błąd tłumi. Dla atrybutów
  zarządzanych przez SAM (np. `userAccountControl`) bezpieczniej: ponowny odczyt + Replace
  (do weryfikacji w laboratorium).
- Idempotencja: Replace i Clear są idempotentne, Add/Delete wartości – z `PermissiveModify`; ponowione utworzenie z
  błędem 68 kończy się odczytem i porównaniem (zgodny obiekt = Applied, inaczej Conflict).

### 9.5 Wykonanie, obciążenie DC i członkostwo

- **Jeden zapisywalny DC** dla odczytu, walidacji, zapisu i weryfikacji („zastosowano na DC01, pozostałe DC otrzymają
  zmiany przez replikację”). Bez cichego przełączenia: gdy DC jest niedostępny, zadanie się wstrzymuje i pyta
  operatora, a po zmianie DC oczekujące obiekty są walidowane ponownie.
- **Współbieżność:** kroki jednego obiektu sekwencyjnie, obiekty równolegle – domyślnie 4 (zakres 1–16), adaptacyjnie
  (AIMD: połowa przy zajętości lub p95 > 500 ms, +1 przy stabilnym sukcesie); kubełek tokenów ok. 20 zapisów/s; porcje
  po 50–100 obiektów z pauzą i wznowieniem; limity w ADMX; dla tysięcy obiektów tryb „dławiony / poza godzinami”.
  `LazyCommit` tylko jako opcja, domyślnie wyłączona.
- **Ponowienia** tylko dla 51, 52, 81, 85, 91 (odczekanie także przy 11 i 3): maks. 3 próby z opóźnieniem 0,5/1,5/4 s i
  losowym rozrzutem. Bez ponowień: 16, 17, 19, 20, 21, 32, 34, 50, 53, 64–68; **odmowy dostępu nigdy nie ponawiamy**.
  Automatyczna pauza przy ponad 10% błędów lub 20 błędach z rzędu.
- **Anulowanie:** nowe obiekty nie są wysyłane, trwające żądania się kończą; przerwane po wysłaniu mają stan
  **Unknown** i rozstrzyga je odczyt. Opcjonalnie każdy zapis jest weryfikowany odczytem („Applied – zweryfikowane”).
  Każdy zapis zwiększa USN i się replikuje; atrybuty z GC trafiają do całego lasu, a `member` replikuje się per wartość.
- **Członkostwo:** zapisujemy łącze w przód `member` na grupie, wiele wartości w jednym żądaniu (ok. 500, w granicach
  `MaxReceiveBuffer`) z `PermissiveModify`; przy błędzie dzielimy partię na połowy, by znaleźć wadliwy DN; zapisy do
  jednej grupy są serializowane. „Zastąp członkostwo” = ranged retrieval, różnica, przyrostowe Add/Delete – **nigdy
  masowe Replace `member`**. „Kopiuj członkostwa A → B” pomija grupę podstawową. Reguły zakresów sprawdzamy przed
  zapisem: globalna – członkowie tylko z tej samej domeny; lokalna domeny – konta i grupy globalne/uniwersalne z lasu i
  zaufań; uniwersalna – bez grup lokalnych domeny. Podmioty obce przez `<SID=…>`; grupa w innej domenie = zapis na
  przypiętym DC tamtej domeny.

### 9.6 Hasła, konta i tworzenie obiektów

- **Reset hasła:** Replace `unicodePwd` = hasło w cudzysłowach w UTF-16LE jako `byte[]`; wymaga szyfrowania 128-bit
  (sealing albo TLS – sam podpis nie wystarczy) i prawa Reset Password. Hasło, `pwdLastSet = 0` i `lockoutTime = 0` idą
  w **jednym** `ModifyRequest`; Policy Hints (`.2239`) wymusza historię haseł. Zmiana przez samego użytkownika = Delete
  starego + Add nowego (prawo Change Password, minimalny wiek hasła). `pwdLastSet = 0` nie działa przy
  `DONT_EXPIRE_PASSWD` – ostrzegamy jak ADUC. Naruszenie polityki zwraca 19 z `0000052D`; komunikat wymienia zasady
  domeny lub PSO.
- **Generator haseł** zgodny z polityką domeny lub `msDS-ResultantPSO`; hasło pokazywane raz, nigdy w dziennikach ani
  skryptach; dla helpdesku opcjonalnie tylko hasła generowane i lista kontrolna weryfikacji rozmówcy (wzorzec Specops).
- **Nowy użytkownik:** (1) Add z `sAMAccountName` (zawsze jawnie, inaczej AD nada losową nazwę), UPN, nazwami i
  `userAccountControl = 514`; (2) `unicodePwd`; (3) UAC 512 i `pwdLastSet = 0`; (4) członkostwa; (5) opcjonalnie kroki
  poza LDAP (katalog domowy). Stan per krok pozwala wznowić częściowe utworzenie lub usunąć obiekt po GUID. Bez hasła i
  jawnego UAC konto ma 546 (`PASSWD_NOTREQD`), a 512 przed zgodnym hasłem daje 53 z `0000052D`. KB podaje, że
  `unicodePwd` można tylko modyfikować; czy Add z hasłem działa przez szyfrowane połączenie
  – do weryfikacji w laboratorium.
- **Nowy komputer:** `sAMAccountName = NAZWA$` (15 + `$`), UAC 4096 (nie 4128 jak w ADUC), ACE dla podmiotu
  uprawnionego do dołączenia, ograniczenia ponownego użycia kont z KB5020276. Opcji „pre-Windows 2000” i akcji „Reset
  Account” nie odtwarzamy (WS2025 blokuje hasło z nazwy) – zamiast tego wyjaśniamy, że maszynę trzeba dołączyć ponownie.

### 9.7 Podgląd, potwierdzenie i raport

- **Podgląd** w dwóch widokach: „według obiektu” (atrybut, stara → nowa wartość, ikony kroków) i „według zmiany” (np.
  „`department`: IT → Dział IT (120 obiektów)”, „Przenieś → OU=Wyłączone (37)”); filtry: zmienione, bez zmian, blokady,
  ostrzeżenia, konflikty, brak uprawnień; liczniki i szacowany czas. Blokady wstrzymują tylko dotknięte obiekty –
  „zastosuj poprawny podzbiór” z jawną liczbą.
- **Potwierdzenie wpisywane** (liczba obiektów albo słowo USUŃ) dla usuwania, wyłączania, resetu haseł, przeniesienia
  ponad N obiektów i zmian w grupach uprzywilejowanych; próg konfigurowalny (punkt odniesienia: Active Roles domyślnie
  ogranicza operację do 1500 obiektów); tryb próbny i „zatrzymaj po N błędach”.
- **W trakcie:** postęp, stany na żywo, pauza/anuluj, przepustowość i licznik błędów, eksport w każdej chwili. **Po:**
  wynik per obiekt z kodem LDAP, kodem Win32 i polskim komunikatem; „Ponów tylko nieudane”; „Utwórz plan cofnięcia”;
  eksport CSV/JSON/HTML/PS1/LDIF. Kolory stanów zawsze z ikoną i tekstem.

### 9.8 Cofanie

| Zmiana | Odwracalność |
|---|---|
| Zastąpienie / wyczyszczenie atrybutu | powrót do starej wartości albo wyczyszczenie, jeśli atrybutu wcześniej nie było |
| Dodanie / usunięcie wartości, członkostwo | operacja odwrotna |
| Przeniesienie / zmiana nazwy | powrót do starego rodzica / RDN (o ile obiekt nie został w międzyczasie przeniesiony) |
| Włączenie / wyłączenie, wygaśnięcie | przywrócenie poprzedniego stanu |
| Utworzenie | usunięcie po GUID |
| Usunięcie | **warunkowo** – tylko przez Kosz AD (6.13) |
| Reset hasła | **nieodwracalny** – zaznaczony na czerwono w podglądzie |

Plan cofnięcia jest generowany ze starych wartości zapisanych przy wykonaniu i przechodzi przez ten sam podgląd i
walidację z warunkiem CAS: cofamy tylko tam, gdzie bieżąca wartość nadal równa się tej, którą zapisaliśmy; w przeciwnym
razie Conflict do decyzji operatora. Dziennik żyje wyłącznie w pamięci procesu; eksport (LDIF/JSON, opcjonalnie DPAPI)
to jawna czynność operatora z ostrzeżeniem – wzorzec ADModify dostosowany do zasady „brak danych lokalnych”.

### 9.9 CSV: import i eksport

- **Kodowanie i separator:** BOM → ścisłe UTF-8 → Windows-1250 (`CodePagesEncodingProvider`); separator `;`, `,` lub
  Tab wykrywany automatycznie. Polski Excel zapisuje CSV ze **średnikiem** i często w **cp1250** – importer „tylko UTF-8
  z przecinkiem” zniszczy „Łukasz Żółć”.
- **Nagłówki** mapowane po nazwie LDAP, potem po angielskiej etykiecie ADUC, potem po polskiej; profile mapowania jako
  konfiguracja; szablon z nagłówkami `Etykieta [ldapName]` i walidacja przed wysłaniem (wzorzec Entra/LAM).
- **Klucz:** `objectGUID` (najlepszy), `sAMAccountName`, UPN, DN (kruchy po przeniesieniu), `employeeID` lub `mail` (bez
  gwarancji unikalności, `employeeID` bez indeksu – ostrzeżenie i zawężenie bazy). Atrybutu-klucza nie zmieniamy w tym
  samym przebiegu. Klucze rozwiązujemy partiami filtrów OR po 50–100 wartości z escapowaniem; 0 lub > 1 dopasowań
  raportujemy per wiersz.
- **Komórki:** pusta = ignoruj, `<clear>` = wyczyść (nigdy nie czyścimy po cichu); separator wielu wartości `|`;
  przycinanie spacji; daty `dd.MM.yyyy` lub ISO; TRUE/FALSE.
- **Eksport:** UTF-8 z BOM, `;` lub `,`, wszystkie pola w cudzysłowach; neutralizacja formuł (OWASP) – apostrof przed
  komórkami zaczynającymi się od `=`, `+`, `-`, `@`, Tab, CR, LF i pełnoszerokich `＝＋－＠`. Prefiks trafia też w
  numery „+48 …”, więc przy ponownym imporcie go zdejmujemy; Excel po ponownym zapisie może reaktywować formuły –
  ostrzegamy. Wyniki: ObjectGuid, DN, `sAMAccountName`, krok, atrybut, stara, nowa, stan, kod LDAP, kod Win32,
  komunikat, DC, czas, liczba prób; także JSON i LDIF.

### 9.10 Szablony, transliteracja polska i unikalność

Składnia: `{atrybut}`, `{atrybut|fn|fn(arg)}`, `{csv:Kolumna}`; funkcje `lower`, `upper`, `title` (pl-PL), `trim`,
`collapse`, `ascii`/`translit`, `first(n)`, `left(n)`, `right(n)`, `substr(i,n)`, `replace(a,b)`,
`regex(wzorzec,zamiana)`, `nospace`, `nohyphen`, `default(x)`, `truncate(n)`, `date(format)`, `counter(szer.)`.

```text
sAMAccountName = {givenName|first(1)|translit|lower}{sn|translit|lower|nospace}
userPrincipalName = {givenName|translit|lower}.{sn|translit|lower}@firma.pl
displayName = {sn} {givenName}
homeDirectory = \\fs01\home$\{sAMAccountName}
```

| Znak | ą | ć | ę | ł | ń | ó | ś | ź | ż | Ą | Ć | Ę | Ł | Ń | Ó | Ś | Ź | Ż |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Transliteracja | a | c | e | l | n | o | s | z | z | A | C | E | L | N | O | S | Z | Z |

- **Ł i ł nie mają rozkładu Unicode** – sprawdzono, że NFD z usunięciem znaków łączących zamienia „ąćęłńóśźż” na
  „acełnoszz”. Dlatego najpierw jawna tabela, potem NFD jako zapas dla innych języków plus tabela liter bez rozkładu
  (đ, ø, æ, œ, ß → ss, ı). Transliterujemy tylko `sAMAccountName`, UPN i adresy; `displayName` i `givenName` zachowują
  polskie znaki. Konfigurowalne: nazwiska dwuczłonowe (Nowak-Kowalska: zachowaj, bez łącznika, pierwszy człon),
  apostrofy, spacje; pusty token ma jawną wartość zapasową (unikamy błędu Active Roles).
- **Unikalność:** wzorce po kolei (jkowalski, jan.kowalski, jkowalski2…); bazę **obcinamy przed dodaniem licznika**
  (inaczej przy limicie 20 znaków licznik zostanie obcięty i kolizje wrócą); kandydat jest sprawdzany na przypiętym DC,
  w GC (UPN, `mail`/`proxyAddresses`) i wśród nazw zarezerwowanych w partii. Unikalność UPN wymuszają tylko DC WS2012
  R2+, a GC ma opóźnienie replikacji, więc przy błędzie 68, 19 lub 0x21C8 w trakcie wykonania generujemy kolejny
  wariant (jeśli zasada pozwala) i odnotowujemy różnicę względem podglądu.

### 9.11 Atrybuty Exchange i poczty (priorytet klienta)

- **Zakres:** `mail`, `mailNickname`, `proxyAddresses`, `targetAddress` (kontakty), `extensionAttribute1–15` (obecność
  schematu Exchange wykrywamy na żywo), `msDS-cloudExtensionAttribute1–20` (WS2012+). `legacyExchangeDN`,
  `proxyAddresses` i `mailNickname` są w zestawie ANR, więc wyszukiwanie po adresie działa od razu.
- **`proxyAddresses`:** dokładnie jeden adres podstawowy `SMTP:`, pozostałe `smtp:`; porównanie bez wielkości liter,
  więc zamiana podstawowego na dodatkowy wymaga Delete + Add w jednym żądaniu; dopisywanie i usuwanie adresów idzie przez
  `PermissiveModify`.
- **Unikalność:** AD jej nie wymusza, ale duplikaty psują Exchange i Entra Connect – każdą nową wartość sprawdzamy w GC
  (`(|(mail=x)(proxyAddresses=smtp:x))`) oraz w obrębie partii.
- **Granice:** `msExch*` domyślnie tylko do odczytu; tworzenie skrzynek i zmiany wymagające logiki Exchange zostają w
  narzędziach Exchange; stron `adminMultiselectPropertyPages` z rozszerzeń Exchange nie przenosimy. W hybrydzie trzeba
  pamiętać o kotwicach synchronizacji (np. `mS-DS-ConsistencyGuid`) – obsługę hybrydy przewidziano na później.
- **Scenariusze:** zmiana nazwiska (`sn`, `displayName`, `cn` przez ModifyDN, UPN, `mail`, nowy podstawowy adres w
  `proxyAddresses` z zachowaniem starego jako dodatkowego), migracja domeny pocztowej (dopisanie adresów `smtp:` dla
  całego OU), uzupełnianie `extensionAttribute*` z CSV kadr. Atrybuty z `fCOPY` (np. niektóre `extensionAttribute*`)
  kopiuje „Copy user”.

### 9.12 Kreatory, scenariusze i skrypty równoważne

- **Nieaktywne konta i komputery:** `lastLogonTimestamp` aktualizuje się dopiero, gdy jest starszy niż
  `msDS-LogonTimeSyncInterval` (14 dni minus losowo do 5 dni), więc próg to co najmniej 14 dni plus margines (typowo
  60/90/180); dodatkowo `(!(lastLogonTimestamp=*))` ze starym `whenCreated` oraz `pwdLastSet` (komputery zmieniają hasło
  co ok. 30 dni). Wykluczenia: DC (`(!(userAccountControl:1.2.840.113556.1.4.803:=8192))`), CNO/VCO klastrów (SPN z
  `MSClusterVirtualServer`), `isCriticalSystemObject`, wyjątki operatora. Przebieg: podgląd → wyłącz → znacznik w opisie
  → przenieś do OU „Wyłączone”; usunięcie później z Tree Delete (komputery mają dzieci, zwykłe usunięcie kończy się 66).
- **Inne kreatory:** zmiana nazwiska (9.11), migracja sufiksu UPN (walidacja z `uPNSuffixes`, unikalność w GC,
  ostrzeżenie o wpływie na Entra Connect), kopiowanie członkostw A → B. **Scenariusze** (np. odejście pracownika:
  wyłącz, usuń z grup, przenieś, wyczyść przełożonego, znacznik) zapisane jako konfiguracja.
- **Równoważny PowerShell:** `-Server $dc` i `-Identity '<objectGUID>'` w każdym poleceniu; Modify → `Set-ADObject` /
  `Set-ADUser -Replace/-Add/-Remove/-Clear`; `Move-ADObject -TargetPath`; `Rename-ADObject`;
  `Add-/Remove-ADGroupMember -Confirm:$false`; `Enable-/Disable-ADAccount`; `Unlock-ADAccount`;
  `Set-ADAccountPassword -Reset -NewPassword (Read-Host -AsSecureString)`; `New-ADUser -Instance`;
  `Remove-ADObject -Recursive`; `-WhatIf` przez `$WhatIfPreference`; `try/catch` per obiekt z zapisem wyników. Plik w
  **UTF-8 z BOM** – bez BOM Windows PowerShell 5.1 czyta skrypt w cp1250 i psuje „Łódź”.
- **LDIF** (RFC 2849, `changetype: modify`) dla `ldifde -i -f plan.ldf -s <dc>`. Podpisane pliki planu (eksport przez
  operatora A, import, ponowna walidacja i wykonanie przez operatora B) dadzą później „zasadę czterech oczu” bez
  serwera.

---

## 10. Ryzyka i jak je ograniczamy

| # | Ryzyko | Ograniczenie |
|---|---|---|
| R1 | Fluent w WPF jest eksperymentalny (WPF0001), ma otwarte błędy DataGrid/ListView, rozmiary „dotykowe” | `IThemeService`, SDK przypięty w `global.json`, style kompaktowe `BasedOn`, ListView+GridView dla głównej listy, komercyjna siatka jako plan B decydowany w fazie 0 |
| R2 | Duże kontenery nie osiągną celu „szybciej niż ADUC”: sortowanie bez indeksu, `MaxTempTableSize`, jeden klucz sortowania | test na 100 tys. obiektów w fazie 0; strategia wg `msDS-Approx-Immed-Subordinates`; sortowanie tylko po kolumnach indeksowanych; automatyczny powrót do stronicowania z banerem; pomiary i kontrolka Stats |
| R3 | Pułapki S.DS.P: synchroniczny connect/bind w `BeginSendRequest`, brak callbacku po `Abort`, wątek 250 ms, przepełnienie limitów czasu, obcinanie do sekund | własny wykonawca (6.2) z testem jednostkowym na każdą pułapkę; pierwszy bind przez `Task.Run`; `FromDays(7)` dla powiadomień; zero synchronicznych `SendRequest` na ścieżkach UI |
| R4 | Cicha zmiana tożsamości: ponowny bind na wątku bez impersonacji działa jako tożsamość procesu | `AutoBind=false`, `ReferralChasing=None`, własny reconnect z WhoAmI, pasek tożsamości, test sprawdzający SID z WhoAmI przed każdą partią zapisów |
| R5 | Różne utwardzenia domen: podpisywanie, CBT=2, zablokowany NTLM, Protected Users, połączenie po IP = NTLM, atrybuty poufne tylko szyfrowane | Kerberos z sign+seal domyślnie, tylko FQDN, bez sign/seal na TLS, macierz testów WS2022/WS2025 i CBT 0/1/2, komunikaty dla błędów 8, 13 i podkodów bindu |
| R6 | Masowe pomyłki (masowe wyłączenie, usunięcie, wyczyszczenie członkostwa) | obowiązkowy podgląd, potwierdzenie wpisywane, tryb próbny, ostrzeżenia Tier-0/`adminCount`/ochrona, świadomość Kosza, zakaz Replace `member`, automatyczna pauza, cofanie w pamięci, limity trybu helpdesku |
| R7 | Brak transakcji wieloobiektowych, opóźnienie replikacji, awaria DC – częściowe lub „niewidoczne” wyniki | atomowe zapisy per obiekt, jeden DC na partię, wykrywanie zmian przez `uSNChanged` + CAS, stan Unknown weryfikowany odczytem, pauza zamiast przełączenia, ponowna walidacja po zmianie DC |
| R8 | Wyciek danych katalogu na dysk przez dzienniki, zrzuty, powiadomienia systemowe, historię schowka, MRU, eksporty | tabela zasad (8.9), redakcja, ogólne powiadomienia systemowe, wykluczenia schowka, test CI z kanarkami, decyzje klienta w szarej strefie, przełączniki ADMX |
| R9 | Obciążenie DC atrybutami konstruowanymi i zapisami masowymi | atrybuty konstruowane tylko w odczytach bazowych lub dławionej walidacji; domyślnie 4 zapisy naraz i ok. 20/s z AIMD; łączenie zapisów do grup; telemetria z progami |
| R10 | Błędne podpowiedzi uprawnień: brak praw rozszerzonych w `allowedAttributesEffective`, `adminCount=1`, SDProp | podpowiedź zamiast blokady, AuthZ dla praw rozszerzonych, ścieżka optymistyczna zawsze działa, znacznik AdminSDHolder z wyjaśnieniem, testy na prawdziwych kontach delegowanych |
| R11 | Błędne kodowanie starych formatów: `logonHours` (UTC/DST), `accountExpires` (koniec dnia), `c`/`co`/`countryCode`, `userParameters`, `groupType`, ACE „cannot change password” | koderzy w `AdMng.Core` z testami na wartościach zapisanych przez ADUC w laboratorium; `userParameters` późno i tylko, jeśli jest używany |
| R12 | Polskie realia: ł poza NFD, CSV w cp1250 ze średnikiem, PS 5.1 bez BOM, trzy formy liczby mnogiej, porządek sortowania serwera | jawna tabela transliteracji, wykrywanie kodowania i separatora, BOM we wszystkich skryptach, pomocnik liczby mnogiej, test porządku sortowania z polskimi nazwiskami |
| R13 | Rozrost zakresu przez „długi ogon” ADUC (Dial-in, RDS, COM+, drukarki, kreator delegacji) | priorytety wg faktycznego użycia, Attribute Editor jako uniwersalny zapas, lista parytetu uzgodniona z zespołem przed pełną edycją |
| R14 | Utwardzone stacje PAW: AppLocker/WDAC, single-file rozpakowujący biblioteki do `%TEMP%` | podpisany MSI per maszyna do Program Files, reguły wydawcy na wewnętrznym certyfikacie, bez samorozpakowania, test na prawdziwym obrazie PAW |
| R15 | Limity powiadomień: 5 na połączenie, utrata po bezczynności lub restarcie DC, tylko lokalne dla DC | LRU rejestracji na osobnym połączeniu, wykrywanie i ponowna rejestracja, zapasowe odpytywanie USN, widoczny czas ostatniego odświeżenia |
| R16 | Niezweryfikowane szczegóły z badań | lista do sprawdzenia w laboratorium na początku prac (wszystkie miejsca oznaczone „do weryfikacji w laboratorium”, m.in. ASQ ze stronicowaniem, `unicodePwd` w Add, `tokenGroups` w RootDSE, Tree Delete na dużych poddrzewach, `info` vs `comment`, zestaw ANR, OID polskiego sortowania, filtr BitLocker po ID) |
| R17 | Zależność od bibliotek z jednym opiekunem (lepoco/wpfui) | brak zależności; kilka kontrolek MIT skopiowanych z atrybucją do `AdMng.Controls` |
| R18 | Laboratorium Samba różni się od Windows AD (brak display specifiers 415, brak schematu Exchange, inne zachowanie części kontrolek) | testy integracyjne na Sambie w CI, a przed wydaniem obowiązkowa weryfikacja na Windows Server 2022/2025 ([ADR-0007](adr/0007-laboratorium-samba.md)) |
| R19 | Zapisy atrybutów pocztowych z pominięciem Exchange tworzą niespójności lub duplikaty adresów | `msExch*` tylko do odczytu, sprawdzanie unikalności w GC i w partii, jeden adres `SMTP:`, provisioning skrzynek w narzędziach Exchange, pytania o topologię Exchange (sekcja 11) |
| R20 | Kosz AD wyłączony w lesie klienta – usunięcia są praktycznie nieodwracalne | wykrywanie stanu Kosza, mocniejsze ostrzeżenia w oknie usuwania, brak automatycznego włączania (włączenie tylko jako jawna, podniesiona operacja w późnej fazie) |
| R21 | Widoki i przypięcia z DN psują się po reorganizacji OU | przechowywanie `objectGUID` obok DN, oznaczanie celów, które zniknęły, ponowne rozwiązanie po GUID |

---

## 11. Pytania otwarte do zespołu

Pominięto sprawy już rozstrzygnięte: technologia (WPF/.NET 10), język interfejsu (PL + EN), priorytet Kosza AD i
atrybutów Exchange oraz potrzeba konfigurowalnych widoków.

**Środowisko**

1. Topologia: ile domen, jakie zaufania, poziomy funkcjonalne, wersje DC (2016–2025), czy są RODC, ile lokacji?
2. Skala: ile użytkowników, komputerów i grup; jak duże są największe OU i największa grupa? (decyduje o priorytecie VLV
   i obsługi dużych grup)
3. Widoki: czy udostępnicie drzewo OU (np. Pracownicy / Komputery / Serwery / Kontrahenci / Wyłączone)? Które widoki
   predefiniować i dla jakich ról? Czy współdzielić je centralnie (np. z udziału sieciowego), czy wystarczy
   eksport/import?
4. Exchange: lokalny, hybryda czy Exchange Online z Entra Connect? Czy Exchange jest źródłem prawdy dla
   `mail`/`proxyAddresses`? Do czego służą `extensionAttribute1–15`? Czy schemat ma własne rozszerzenia?
5. Kosz AD: czy jest już włączony, jaki jest `msDS-deletedObjectLifetime` i kto ma mieć prawo przywracania?
6. Czy jest laboratorium Windows Server lub kopia produkcyjnego AD do testów (Samba nie odwzorowuje wszystkiego)?

**Bezpieczeństwo**

7. Granica „braku danych lokalnych”: czy dopuszczalne są (a) automatyczna historia ostatnio odwiedzanych OU z DN,
   (b) dziennik operatora z wartościami przed i po (plik, tylko Dziennik zdarzeń/SIEM czy same metadane), (c) eksporty
   CSV zlecone przez operatora, (d) eksport dziennika cofania, (e) zrzuty awaryjne?
8. Czy LDAPS działa na wszystkich DC? Czy wymuszono podpisywanie LDAP i channel binding? Czy NTLM jest ograniczony?
9. Czy jest model warstwowy lub PAW i skąd czytać definicje Tier-0? Czy administratorzy są w Protected Users, w silosach
   lub używają kont tylko z kartą inteligentną?
10. Jak operatorzy logują się dziś (konta `adm-*` z `runas`, serwer przesiadkowy)? Czy potrzebne są dwie tożsamości
    (helpdesk + administrator) jednocześnie w jednej instancji?
11. Jakie prawa ma helpdesk (reset, odblokowanie, włączanie, które atrybuty, które grupy)? Czy potrzebny jest tryb
    „tylko podgląd”?
12. Czy helpdesk może eksportować do CSV? Czy reset ma wymagać weryfikacji rozmówcy albo wyłącznie haseł generowanych?
13. Czy na DC działa audyt zmian katalogu (5136) i czy jest SIEM, który może być ścieżką audytu?

**Edycja zbiorcza**

14. Które operacje zbiorcze są najczęstsze i na ilu obiektach (typowo i maksymalnie)?
15. Progi potwierdzeń: usunięcie, wyłączenie, reset hasła, modyfikacje powyżej N obiektów? Czy coś wymaga drugiej
    osoby (zasada czterech oczu)?
16. Źródła CSV: eksport z systemu kadrowego – format, kodowanie, klucz (`employeeID`?) i czy klucz jest naprawdę
    unikalny?
17. Konwencje nazw: wzorzec `sAMAccountName`, sufiksy UPN, format adresu e-mail, polskie znaki, nazwiska dwuczłonowe,
    duplikaty (numery?); czy reguły różnią się między OU?
18. Czy przy tworzeniu kont tworzyć też katalogi domowe lub ACL udziałów (SMB, poza LDAP)?
19. Które atrybuty pocztowe zmieniacie masowo (aliasy po zmianie domeny, adres podstawowy po zmianie nazwiska,
    `extensionAttribute*` z kadr)?

**Zakres**

20. Które karty ADUC są używane: Dial-in, RDS (`userParameters`), COM+, Name Mappings, Published Certificates,
    drukarki, foldery udostępnione?
21. LAPS: Windows LAPS czy dawny, z szyfrowaniem haseł czy bez? Czy klucze odzyskiwania BitLocker są w AD?
22. Czy narzędzie ma objąć pozostałe obszary ADAC (FGPP, zasady i silosy uwierzytelniania, DAC)?
23. Czy potrzebny jest widok „równoważny PowerShell” (nauka, przegląd zmian, harmonogramy)?
24. Etykiety atrybutów: polskie z display specifiers (jak w polskim ADUC), nazwy LDAP czy obie naraz?

**Wdrożenie**

25. Dystrybucja (Intune, ConfigMgr, GPO)? AppLocker/WDAC? Certyfikat podpisu kodu z wewnętrznego AD CS? Wsparcie
    Windows 10 lub ARM64?
26. Kto rozwija i utrzymuje narzędzie, jaki jest termin pierwszej użytecznej wersji (odczyt + helpdesk) i czy ma być
    tylko wewnętrzne, czy open source?
27. Kto weźmie udział w pilotażu (propozycja: ok. 5 osób z helpdesku przez 2 tygodnie równolegle z ADUC)?

---

## 12. Źródła

Większość stron Microsoft Learn czytano z ich źródeł w repozytoriach MicrosoftDocs (ten sam tekst); poniżej podajemy
adresy w formie, w jakiej cytowali je badacze. Dane producentów pochodzą z wyciągów wyszukiwarki.

**ADUC – mapowanie interfejsu, karty, kreatory**

- [User Object UI Mapping](https://learn.microsoft.com/en-us/windows/win32/ad/user-object-user-interface-mapping) · [Group Object UI Mapping](https://learn.microsoft.com/en-ie/Windows/Win32/ad/group-object-user-interface-mapping) · [Computer UI Mapping](https://msdn.microsoft.com/en-us/ms675736) · [Mappings for the ADUC snap-in](https://learn.microsoft.com/en-us/windows/win32/ad/mappings-for-the-active-directory-users-and-computers-snap-in) · [Modifying User Cannot Change Password](https://learn.microsoft.com/en-us/windows/win32/adsi/modifying-user-cannot-change-password-ldap-provider) · [ADS_GROUP_TYPE_ENUM](https://learn.microsoft.com/en-us/windows/win32/api/iads/ne-iads-ads_group_type_enum) · [ManagedBy i ACE](https://learn.microsoft.com/en-us/archive/blogs/dsadsi/issues-while-setting-a-manageby-properties-of-a-group-object-in-active-directory)
- [KB250455 (createDialog)](https://support.microsoft.com/kb/250455/) · [Zmiana nazw wyświetlanych](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/change-display-names-ad-users) · [Ukrywanie inetOrgPerson](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/hide-display-inetorgperson-object-class) · [Copy user (cc771231)](https://technet.microsoft.com/en-us/library/cc771231.aspx) · [ee198800](https://technet.microsoft.com/en-us/library/ee198800.aspx) · [KB305144 (userAccountControl)](https://support.microsoft.com/kb/305144) · [KB5014754 (mapowania certyfikatów)](https://support.microsoft.com/kb/5014754) · [AskDS – Name Mappings](https://techcommunity.microsoft.com/blog/askds/-/4451386)
- [RODC PRP (dd145483)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-r2-and-2008/dd145483(v=ws.11)) · [Delegacja Kerberos gMSA](https://learn.microsoft.com/en-us/windows-server/security/group-managed-service-accounts/configure-kerberos-delegation-group-managed-service-accounts) · [Pre-Windows 2000 computers](https://thehacker.recipes/ad/movement/builtins/pre-windows-2000-computers)
- Edycja wielu obiektów: [AD Cookbook ch06s05](https://www.oreilly.com/library/view/active-directory-cookbook/0596004648/ch06s05.html) · [Windows Server 2008 ch11s06](https://oreilly.com/library/view/windows-server-2008/9780735624375/ch11s06.html) · [rebeladmin](https://www.rebeladmin.com/?p=6905)

**ADUC – kontenery, widoki, wyszukiwanie, operacje**

- [cc773295](https://technet.microsoft.com/en-us/library/cc773295(v=ws.10)) · [Binding with WKGUID](https://learn.microsoft.com/en-us/windows/desktop/ad/binding-to-well-known-objects-using-wkguid) · [ntdsapi.h (mingw-w64, weryfikacja GUID)](https://raw.githubusercontent.com/mingw-w64/mingw-w64/master/mingw-w64-headers/include/ntdsapi.h) · [no-such-object (hybryda)](https://learn.microsoft.com/ar-sa/troubleshoot/azure/entra/entra-id/ad-dmn-services/azure-ad-hybrid-sync-no-such-object-on-server) · [gg456494](https://technet.microsoft.com/en-us/library/gg456494.aspx) · [Domain-wide updates](https://learn.microsoft.com/en-in/WINDOWS-SERVER/identity/ad-ds/deploy/domain-wide-updates) · [jj635854](https://technet.microsoft.com/en-us/jj635854)
- [Filter Options (cc773054)](https://technet.microsoft.com/en-us/library/cc773054(v=ws.10)) · [Q&A 920235](https://learn.microsoft.com/en-us/answers/a/920235) · [joeware – limit 2000](https://blog.joeware.net/?p=2124) · [extraColumns](https://www.undocumented-features.com/?p=95) · [Modifying existing UIs](https://learn.microsoft.com/sv-se/windows/win32/ad/modifying-existing-user-interfaces) · [Export List (cc758335)](https://technet.microsoft.com/en-us/library/cc758335(v=ws.10))
- [Find (cc778937)](https://technet.microsoft.com/en-us/library/cc778937(v=ws.10)) · [Redmond – Searching AD](https://redmondmag.com/articles/2003/07/01/searching-active-directory.aspx) · [MS-ADTS ANR](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/1a9177f4-0272-4ab8-aa22-3c3eafd39e4b) · [Saved Queries (cc783268)](https://technet.microsoft.com/library/cc783268(v=ws.10)) · [cc757566](https://technet.microsoft.com/library/cc757566(v=ws.10)) · [woshub – saved queries](https://woshub.com/using-ldap-saved-queries-in-active-directory/) · [Petri – saved queries](https://petri.com/saved_queries_in_windows_2003_dsa/) · [cc775386](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc775386(v=ws.10)) · [NetIQ DRA – import zapytań](https://www.netiq.com/documentation/directory-and-resource-administrator-92/drauserguide/data/t43avmuy8d8l.html)
- [Ochrona przed usunięciem (cc739350)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc739350(v=ws.10)) · [Petri](https://petri.com/secure-active-directory-objects-windows-server-2008r2/) · [AdminDroid](https://blog.admindroid.com/how-to-delete-protection-enabled-organizational-units-in-active-directory/) · [Delegate Control (cc772784)](https://technet.microsoft.com/en-us/library/cc772784(v=ws.10)) · [AdminDroid – kreator delegacji](https://blog.admindroid.com/apply-least-privilege-in-active-directory-with-delegation-wizard/) · [Functional levels](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/active-directory-functional-levels) · [Raise levels](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/plan/raise-domain-forest-functional-levels) · [KB322692](https://support.microsoft.com/kb/322692)
- Bolączki i luki: [ADAC vs ADUC (jj574144)](https://technet.microsoft.com/library/jj574144.aspx) · [Petri – ADAC](https://petri.com/active-directory-administrative-center/) · [Q&A 476055](https://learn.microsoft.com/en-us/answers/questions/476055/aduc-disabled-users-query) · [One Identity – duże OU](https://support.oneidentity.com/kb/4333860/what-are-the-known-limitations-when-dealing-with-organizational-units-or-groups-with-many-thousands-of-objects-) · [Błąd Find bez zaawansowanych kart](https://learn.microsoft.com/en-us/archive/blogs/389thoughts/workaround-for-the-aduc-search-bug-with-advanced-tabs-missing) · [RSAT na ARM64](https://www.elevenforum.com/t/rsat-capabilities-arrive-on-arm-based-windows-11-pcs.45196/latest) · [windows-active-directory.com](https://www.windows-active-directory.com/active-directory-users-and-computers.html)

**ADAC, Kosz AD, FGPP, WAC, Entra, PowerShell**

- ADAC: [dd560631](https://technet.microsoft.com/en-us/library/dd560631) · [Navigation pane](https://learn.microsoft.com/windows-server/security/group-managed-service-accounts/customize-the-active-directory-administrative-center-navigation-pane) · [ITPro Today](https://itprotoday.com/windows-8/using-active-directory-administrative-center-in-windows-server-2008-r2) · [Global Search (dd861328)](https://technet.microsoft.com/en-us/library/dd861328.aspx) · [Petri – Global Search](https://petri.com/use-global-search-in-active-directory-administrative-center-adac/) · [Q&A 562406](https://learn.microsoft.com/en-us/answers/questions/562406/why-we-are-not-able-to-see-attibute-editor-in-user) · [Level 200](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/advanced-ad-ds-management-using-active-directory-administrative-center--level-200-) ([źródło](https://github.com/MicrosoftDocs/windowsserverdocs/blob/main/WindowsServerDocs/identity/ad-ds/get-started/adac/Advanced-AD-DS-Management-Using-Active-Directory-Administrative-Center--Level-200-.md)) · [ADWS (ff755968)](https://technet.microsoft.com/en-us/library/ff755968(v=technet.10)) · [Duże grupy](https://activedirectorypro.com/get-members-of-large-ad-groups) · [PowerShell History](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/use-active-directory-administrative-center-powershell-history) · [TechTarget](https://www.techtarget.com/searchWindowsServer/tip/Improve-automation-skills-with-the-AD-Administrative-Center)
- Kosz AD i FGPP: [AD Recycle Bin](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/active-directory-recycle-bin) · [MS-ADTS undelete](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/4302a2eb-55c8-426c-b310-6791c6ae8307) · [Retrieving deleted objects](https://learn.microsoft.com/en-us/windows/win32/ad/retrieving-deleted-objects) · [FGPP](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/fine-grained-password-policies) · [msDS-UserPasswordExpiryTimeComputed](https://www.manageengine.com/products/ad-manager/ad-schema-attributes/ms-ds-user-password-expiry-time-computed.html) · [DAC](https://learn.microsoft.com/en-us/windows-server/identity/solution-guides/deploy-a-central-access-policy--demonstration-steps-) · [Authentication policies and silos](https://learn.microsoft.com/en-us/windows-server/security/credentials-protection-and-management/authentication-policies-and-authentication-policy-silos)
- WAC: [Manage servers](https://learn.microsoft.com/en-US/windows-server/manage/windows-admin-center/use/manage-servers) · [Overview](https://learn.microsoft.com/windows-server/manage/windows-admin-center/overview) · [Preview 1903](https://blogs.windows.com/windows-insider/2019/03/26/announcing-windows-admin-center-preview-1903/) · [Neowin](https://www.neowin.net/amp/microsoft-releases-windows-admin-center-preview-1903---heres-whats-new/)
- Entra: [Users search](https://learn.microsoft.com/en-us/entra/identity/users/users-search-enhanced) · [Group members](https://learn.microsoft.com/en-us/entra/identity/users/groups-members-owners-search) · [Devices](https://learn.microsoft.com/en-us/entra/identity/devices/manage-device-identities) · [Bulk add](https://learn.microsoft.com/en-us/entra/identity/users/users-bulk-add) · [Bulk delete](https://learn.microsoft.com/en-us/entra/identity/users/users-bulk-delete) · [Bulk download](https://learn.microsoft.com/en-us/entra/identity/users/users-bulk-download) · [Bulk operations](https://learn.microsoft.com/en-us/entra/fundamentals/bulk-operations) · [Limitations](https://learn.microsoft.com/en-us/entra/fundamentals/bulk-operations-service-limitations)
- PowerShell i historia zmian: [Get-ADUser](https://learn.microsoft.com/en-us/powershell/module/activedirectory/get-aduser) · [Set-ADUser](https://learn.microsoft.com/en-us/powershell/module/activedirectory/set-aduser) · [New-ADUser](https://learn.microsoft.com/en-us/powershell/module/activedirectory/new-aduser) · [Move-ADObject](https://learn.microsoft.com/en-us/powershell/module/activedirectory/move-adobject) · [Search-ADAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/search-adaccount) ([źródło](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/ActiveDirectory/Search-ADAccount.md)) · [MS-ADTS lockout](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/b80798ae-1f8c-4d30-9d80-a1f3281e96e2) · [ANR (TechNet Wiki)](https://learn.microsoft.com/en-us/archive/technet-wiki/22653.active-directory-ambiguous-name-resolution) · [cc220851](https://msdn.microsoft.com/en-us/library/cc220851) · [Get-ADReplicationAttributeMetadata](https://technet.microsoft.com/en-us/library/hh852209(v=wps.620)) · [SpecterOps – metadane replikacji](https://specterops.io/blog/2017/09/06/hunting-with-active-directory-replication-metadata/) · [DS_REPL_ATTR_META_DATA](https://learn.microsoft.com/en-us/windows/win32/api/ntdsapi/ns-ntdsapi-ds_repl_attr_meta_data) · [about_Character_Encoding (5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding?view=powershell-5.1)

**Produkty konkurencyjne i open source**

- ManageEngine: [częstotliwość synchronizacji](https://manageengine.com/products/ad-manager/admanager-kb/how-often-admanager-plus-sync-with-active-directory.html) · [synchronizacja danych](https://www.manageengine.com/products/ad-manager/admanager-kb/active-directory-and-admanager-plus-data-synchronization.html) · [PostgreSQL](https://www.manageengine.com/products/ad-manager/admanager-kb/how-to-update-admanager-plus-built-in-postgresql-database.html) · [CSV modify](https://manageengine.com/products/ad-manager/help/user_management/active-directory-modify-users-csv.html) · [błędy CSV](https://www.manageengine.com/au/products/ad-manager/admanager-kb/csv-bulk-modification-failure-reasons.html) · [manager z CSV](https://www.manageengine.com/products/ad-manager/admanager-kb/fix-csv-manager-update-failure.html)
- Adaxes: [Installation guide](https://www.adaxes.com/help/InstallationGuide) · [Technical overview](https://passwords.devfactory.com/AdaxesSelfService/Help/GettingStartedWithAdaxes.TechnicalOverview.html) · [Szablon pełnej nazwy](https://adaxes.com/help/ChangeTemplateForAutoGeneratingUsersFullName/) · [UPN](https://www.adaxes.com/questions/726/userprincipalname-attribute?show=4872) · Active Roles: [Admin guide 7.6 – 26](https://support.oneidentity.com/technical-documents/active-roles/7.6/administration-guide/26) · [7.6.3 – 5](https://support.oneidentity.com/technical-documents/active-roles/7.6.3/administration-guide/5) · [doc2114472](https://support.oneidentity.com/technical-documents/doc2114472) · [KB PGV](https://support.oneidentity.com/kb/4334147/text-entry-in-a-property-generation-and-validation-policy-following-a-missing-attribute-is-removed)
- Softerra LDAP Administrator: [2025 ch08s08](https://www.ldapadministrator.com/resources/english/help/la2025/ch08s08.html) · [2011.1](https://www.ldapadministrator.com/resources/english/2011.1/whatsnew.html) · [2012.1](https://ldapadministrator.com/resources/english/2012.1/whatsnew.html) · Hyena: [attribute management](https://www.systemtools.com/HyenaHelp/about_attribute_management.htm) · [attributemgt](https://systemtools.com/HyenaHelp/attributemgt.htm) · [Active Editor](https://www.systemtools.com/HyenaHelp/active_editor.htm) · ADModify: [dwinter](https://learn.microsoft.com/en-us/archive/blogs/dwinter/admodify-net-released) · [Exchange blog](https://techcommunity.microsoft.com/blog/exchange/admodify-net-is-here/610119) · [computerperformance](https://www.computerperformance.co.uk/utilities/admodify/)
- Inne: [Cjwdev](https://cjwdev.com/Software.html) · [AD Tidy](http://www.cjwdev.co.uk/Software/ADTidy/Info.html) · [albusbit](https://albusbit.com/blog/cjwdev-alternatives/) · [AD Explorer](https://learn.microsoft.com/en-us/sysinternals/downloads/adexplorer) · [Sysinternals 2020](https://www.neowin.net/news/sysinternals-suite-20201104/) · [Snapshots](https://documentation.help/AD-Explorer/Snapshots.htm) · [ADExplorerSnapshot](https://github.com/c3c/ADExplorerSnapshot) · [Netwrix](https://www.netwrix.com/active_directory_change_control.html) · [Netwrix restore](https://docs.netwrix.com/docs/auditor/10_7/tools/objectrestoread) · [Lepide](https://lepide.com/lepideauditor/active-directory-auditing.html) · [Specops datasheet](https://specopssoft.com/our-resources/secure-service-desk-datasheet/) · [Specops admin](https://specopssoft.com/support/secure-service-desk/administration/authentication-web/)
- Open source: [UnifiedDirectoryManager](https://github.com/Armadillon44/UnifiedDirectoryManager) · [OpenADUC](https://github.com/OpenADUC/openaduc) · [ADMC](https://github.com/altlinux/admc) · [BLAZAM](https://github.com/Blazam-App/BLAZAM) · [LDAP Admin (Wikipedia)](https://en.wikipedia.org/wiki/LDAP_Admin) · [LDAP Admin (SourceForge)](https://www.sourceforge.net/projects/ldapadmin/) · [Apache Directory Studio](https://directory.apache.org/studio/)

**LDAP i System.DirectoryServices.Protocols**

- Kod .NET (`release/10.0`): [ref API](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/ref/System.DirectoryServices.Protocols.cs) · [LdapConnection.cs (Abort)](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapConnection.cs) · [LdapConnection.Windows.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapConnection.Windows.cs) · [DirectoryConnection.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/common/DirectoryConnection.cs) · [LdapPartialResultsProcessor.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapPartialResultsProcessor.cs) · [LdapAsyncResult.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapAsyncResult.cs) · [LdapSessionOptions.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapSessionOptions.cs) · [LdapSessionOptions.Windows.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapSessionOptions.Windows.cs) · [DirectoryControl.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/common/DirectoryControl.cs) · [ref API (main)](https://github.com/dotnet/runtime/blob/main/src/libraries/System.DirectoryServices.Protocols/ref/System.DirectoryServices.Protocols.cs) · [NetworkCredential.cs](https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Net.Primitives/src/System/Net/NetworkCredential.cs) · [NuGet](https://www.nuget.org/packages/system.directoryservices.protocols) · [BeginSendRequest](https://learn.microsoft.com/dotnet/api/system.directoryservices.protocols.ldapconnection.beginsendrequest)
- Polityki i limity: [ntdsutil – LDAP policy](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/view-set-ldap-policy-using-ntdsutil) · [MS-ADTS – LDAP policies](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/3f0137a1-63df-400c-bf97-e1040f055a99) · [500 wartości w odpowiedzi](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/domain-controller-returns-500-values-ldap-response) · [ldap_search_ext](https://learn.microsoft.com/en-us/windows/win32/api/winldap/nf-winldap-ldap_search_ext) · [LDAP considerations](https://learn.microsoft.com/en-us/windows-server/administration/performance-tuning/role/active-directory-server/ldap-considerations) · [Referrals](https://learn.microsoft.com/en-us/windows/win32/ad/referrals)
- Stronicowanie, VLV, zakresy, ASQ, filtry: [Sorting](https://learn.microsoft.com/en-us/windows/win32/adsi/sorting) · [Search using VLV](https://learn.microsoft.com/en-us/windows/win32/adsi/how-to-search-using-vlv) · [LDAPSortKey](https://learn.microsoft.com/en-us/windows/win32/api/winldap/ns-winldap-ldapsortkeya) · [draft VLV-09](https://datatracker.ietf.org/doc/html/draft-ietf-ldapext-ldapv3-vlv-09) · [RFC 2891](https://www.rfc-editor.org/rfc/rfc2891) · [Range retrieval](https://learn.microsoft.com/en-us/windows/win32/adsi/attribute-range-retrieval) · [ASQ](https://learn.microsoft.com/en-us/windows/win32/adsi/performing-an-attribute-scoped-query) · [ASQ OID](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ldap/ldap-server-asq-oid) · [Search filter syntax](https://learn.microsoft.com/en-us/windows/win32/adsi/search-filter-syntax) · [RFC 4515](https://www.rfc-editor.org/rfc/rfc4515) · [searchFlags](https://learn.microsoft.com/en-us/windows/win32/adschema/a-searchflags)
- Atrybuty konstruowane, powiadomienia, RootDSE, lokalizator: [allowedAttributesEffective](https://learn.microsoft.com/en-us/windows/win32/adschema/a-allowedattributeseffective) · [allowedChildClassesEffective](https://learn.microsoft.com/en-us/windows/win32/adschema/a-allowedchildclasseseffective) · [sDRightsEffective](https://learn.microsoft.com/en-us/windows/win32/adschema/a-sdrightseffective) · [msDS-User-Account-Control-Computed](https://learn.microsoft.com/en-us/windows/win32/adschema/a-msds-user-account-control-computed) · [dirteam – czy mogę zapisać](https://dirteam.com/tomek/2006/12/13/how-to-check-if-i-can-update-attribute_3f00_/) · [Change notifications](https://learn.microsoft.com/en-us/windows/win32/ad/change-notifications-in-active-directory-domain-services) · [Notification OID](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ldap/ldap-server-notification-oid) · [DirSync](https://learn.microsoft.com/en-us/windows/win32/ad/polling-for-changes-using-the-dirsync-control) · [uSNChanged polling](https://learn.microsoft.com/en-us/windows/win32/ad/polling-for-changes-using-usnchanged) · [RootDSE](https://learn.microsoft.com/en-us/windows/win32/adschema/rootdse) · [Serverless binding](https://learn.microsoft.com/en-us/windows/win32/ad/serverless-binding-and-rootdse) · [Display specifiers](https://learn.microsoft.com/windows/win32/ad/display-specifiers) · [DsGetDcName](https://learn.microsoft.com/en-us/windows/win32/api/dsgetdc/nf-dsgetdc-dsgetdcnamew) · [LDAP client performance counters](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/ldap-client-performance-counters)
- Konta, hasła, grupy, błędy: [Zmiana hasła przez LDAP](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/change-windows-active-directory-user-password) · [unicodePwd](https://learn.microsoft.com/en-us/windows/win32/adschema/a-unicodepwd) · [Setting user passwords](https://learn.microsoft.com/en-us/windows/win32/adsi/setting-user-passwords-for-ldap-providers) · [userAccountControl](https://learn.microsoft.com/en-us/windows/win32/adschema/a-useraccountcontrol) · [pwdLastSet](https://learn.microsoft.com/en-us/windows/win32/adschema/a-pwdlastset) · [User must change password](https://learn.microsoft.com/en-us/windows/win32/adsi/user-must-change-password-at-next-logon) · [accountExpires](https://learn.microsoft.com/en-us/windows/win32/adschema/a-accountexpires) · [groupType](https://learn.microsoft.com/en-us/windows/win32/adschema/a-grouptype) · [primaryGroupID](https://learn.microsoft.com/en-us/windows/win32/adschema/a-primarygroupid) · [adminCount](https://learn.microsoft.com/en-us/windows/win32/adschema/a-admincount) · [Creating a user](https://learn.microsoft.com/en-us/windows/win32/ad/creating-a-user) · [Moving objects](https://learn.microsoft.com/en-us/windows/win32/ad/moving-objects) · [Naming properties](https://learn.microsoft.com/en-us/windows/win32/ad/naming-properties) · [description](https://learn.microsoft.com/en-us/windows/win32/adschema/a-description) · [sAMAccountName](https://learn.microsoft.com/en-us/windows/win32/adschema/a-samaccountname) · [SPN/UPN uniqueness](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/component-updates/spn-and-upn-uniqueness) · [lastLogonTimestamp](https://learn.microsoft.com/en-us/windows/win32/adschema/a-lastlogontimestamp) · [lastLogonTimestamp w ConfigMgr](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/discovery/lastlogontimestamp-not-accurate) · [Dell – kody LDAP](https://www.dell.com/support/kbdoc/en-ph/000165267/networker-ad-ldap-integration-failing-with-common-ldap-error-codes-ldap-error-code-49) · [Lenovo – błędy bindu](https://helpdesk.lenovosoftware.com/portal/en/kb/articles/common-microsoft-ad-ldap-bind-errors-22-8-2017) · [RFC 4511 §4.6](https://www.rfc-editor.org/rfc/rfc4511#section-4.6) · [Schemat AD 2008 R2 (Samba)](https://github.com/samba-team/samba/blob/master/source4/setup/ad-schema/MS-AD_Schema_2K8_R2_Attributes.txt) · [ldb.h (Samba)](https://github.com/samba-team/samba/blob/master/lib/ldb/include/ldb.h)

**Bezpieczeństwo**

- WS2025 i LDAP: [What's new in WS2025](https://learn.microsoft.com/en-us/windows-server/get-started/whats-new-windows-server-2025) · [LDAP signing](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/ldap-signing) · [LDAP channel binding](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/ldap-channel-binding) · [Enable LDAP signing](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/enable-ldap-signing-in-windows-server) · [LDAPS z CA zewnętrznym](https://raw.githubusercontent.com/MicrosoftDocs/SupportArticles-docs/main/support/windows-server/active-directory/enable-ldap-over-ssl-3rd-certification-authority.md) · [VERIFYSERVERCERT](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winldap/nc-winldap-verifyservercert.md) · [ldap_bind_s](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winldap/nf-winldap-ldap_bind_s.md)
- Poświadczenia i model warstwowy: [LogonUserW](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winbase/nf-winbase-logonuserw.md) · [CreateProcessWithLogonW](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winbase/nf-winbase-createprocesswithlogonw.md) · [WindowsIdentity](https://raw.githubusercontent.com/dotnet/dotnet-api-docs/main/xml/System.Security.Principal/WindowsIdentity.xml) · [DE0001 SecureString](https://raw.githubusercontent.com/dotnet/platform-compat/master/docs/DE0001.md) · [CREDENTIALW](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/wincred/ns-wincred-credentialw.md) · [Tier model](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/tier-model.md) · [AD Tier Model – best practices](https://raw.githubusercontent.com/microsoft/ActiveDirectoryTierModel/main/docs/best-practices.md) · [Protected Users](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/security/credentials-protection-and-management/protected-users-security-group.md) · [Protected Accounts](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/manage/How-to-Configure-Protected-Accounts.md) · [Authentication silos (źródło)](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/security/credentials-protection-and-management/authentication-policies-and-authentication-policy-silos.md)
- Prawa i delegacja: [Control access rights](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/AD/control-access-rights.md) · [User-Force-Change-Password](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/r-user-force-change-password.md) · [User-Change-Password](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/r-user-change-password.md) · [Checking a control access right](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/AD/checking-a-control-access-right-in-an-objectampaposs-acl.md) · [nTSecurityDescriptor](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/a-ntsecuritydescriptor.md) · [Delegation wizard](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/manage/delegation-control-wizard.md) · [Public-Information](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/r-public-information.md) · [lockoutTime](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/a-lockouttime.md) · [Protected Accounts and Groups](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/plan/security-best-practices/Appendix-C--Protected-Accounts-and-Groups-in-Active-Directory.md)
- Audyt i dzienniki: [Audit policy recommendations](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/plan/security-best-practices/Audit-Policy-Recommendations.md) · [MDI – zbieranie zdarzeń](https://raw.githubusercontent.com/MicrosoftDocs/ATADocs/master/ATPDocs/deploy/configure-windows-event-collection.md) · [Tier Model – logowanie](https://raw.githubusercontent.com/microsoft/ActiveDirectoryTierModel/main/docs/tiermodel-logging.md) · [Tier Model – schemat ID zdarzeń](https://raw.githubusercontent.com/microsoft/ActiveDirectoryTierModel/main/docs/event-id-schema.md) · [OWASP CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection) · [LoggerMessage](https://learn.microsoft.com/dotnet/core/extensions/logger-message-generator) · [Redakcja danych](https://learn.microsoft.com/dotnet/core/extensions/data-redaction)

**Interfejs i dystrybucja**

- WPF/Fluent i alternatywy: [What's new WPF .NET 10](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/whats-new/net100.md) · [.NET 9](https://learn.microsoft.com/dotnet/desktop/wpf/whats-new/net90) · [ThemeMode.cs](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/ThemeMode.cs) · [#10393](https://github.com/dotnet/wpf/issues/10393) · [#10445](https://github.com/dotnet/wpf/issues/10445) · [#10911](https://github.com/dotnet/wpf/issues/10911) · [WPF UI – wydania](https://github.com/lepoco/wpfui/releases) · [WPF UI – NuGet](https://www.nuget.org/packages/WPF-UI) · [WPF UI – kontrolki](https://github.com/lepoco/wpfui/tree/main/src/Wpf.Ui/Controls) · [WinUI – wydania](https://github.com/microsoft/microsoft-ui-xaml/releases) · [Windows App SDK 2.0](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0) · [Kanały wydań](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels) · [Avalonia – dyskusja 18388](https://github.com/AvaloniaUI/Avalonia/discussions/18388) · [Avalonia – blog](https://avaloniaui.net/blog/building-a-sustainable-future-for-avalonia) · [TreeDataGrid](https://avaloniaui.net/tree-data-grid)
- Wydajność, dostępność, lokalizacja, MVVM: [TreeView performance](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/controls/how-to-improve-the-performance-of-a-treeview.md) · [ListBox scrolling](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/controls/how-to-improve-the-scrolling-performance-of-a-listbox.md) · [Optimizing controls](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/optimizing-performance-controls) · [DataGrid](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/controls/datagrid.md) · [DataGrid – klawiatura](https://learn.microsoft.com/dotnet/desktop/wpf/controls/default-keyboard-and-mouse-behavior-in-the-datagrid-control) · [TreeView o nieznanej głębokości](https://learn.microsoft.com/dotnet/desktop/wpf/controls/how-to-bind-a-treeview-to-data-that-has-an-indeterminable-depth) · [EnableCollectionSynchronization](https://learn.microsoft.com/dotnet/api/system.windows.data.bindingoperations.enablecollectionsynchronization) · [CustomSort](https://learn.microsoft.com/dotnet/api/system.windows.data.listcollectionview.customsort) · [PerMonitorDPI](https://github.com/microsoft/WPF-Samples/tree/main/PerMonitorDPI) · [UI Automation](https://learn.microsoft.com/dotnet/framework/ui-automation/ui-automation-overview) · [LiveSetting](https://learn.microsoft.com/dotnet/api/system.windows.automation.automationproperties.livesetting) · [Globalizacja WPF](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/wpf-globalization-and-localization-overview) · [CommunityToolkit](https://github.com/CommunityToolkit/dotnet/releases) · [ObservableProperty](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/observableproperty) · [Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host)
- Dystrybucja: [.NET releases](https://github.com/dotnet/core/blob/main/releases.md) · [Single-file](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview) ([źródło](https://raw.githubusercontent.com/dotnet/docs/main/docs/core/deploying/single-file/overview.md)) · [MSIX](https://learn.microsoft.com/windows/msix/overview) · [SignTool](https://learn.microsoft.com/windows/win32/seccrypto/signtool) · [Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/overview) · [LAPS – technical reference](https://learn.microsoft.com/en-us/windows-server/identity/laps/laps-technical-reference)
