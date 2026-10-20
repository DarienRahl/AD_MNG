# AdMng Watchdog – monitor zmian w Active Directory

AdMng Watchdog to osobna usługa Windows, która przez całą dobę obserwuje domenę Active Directory, zapisuje historię
zmian w swoim własnym folderze danych i powiadamia administratorów (e-mail, raport dzienny, Microsoft Teams,
webhook). W aplikacji AD Manager (AD_MNG) usługa ma własną zakładkę **Watchdog**: podgląd zdarzeń, reguły
powiadomień, konfigurację SMTP i kanałów oraz stan usługi.

Decyzję o przechowywaniu danych przez usługę opisuje [ADR-0008](adr/0008-watchdog-dane-w-osobnym-folderze.md).
Specyfikacja implementacyjna (dla programistów, po angielsku) powstała razem z tym dokumentem.

---

## 1. Co wykrywa Watchdog

| Kategoria | Przykłady zdarzeń | Domyślny priorytet | Domyślne powiadomienie |
|---|---|---|---|
| Nowe obiekty | nowe konto użytkownika, nowy komputer, nowa grupa, nowe OU, nowe konto gMSA (dMSA – wysoki) | niski / informacja | konta: e-mail w godzinach pracy (zbiorczo co 5 min) + raport dzienny; komputery: raport dzienny |
| Nietypowe komputery | komputer dodany do domeny przez zwykłego użytkownika (MachineAccountQuota), utworzony w `CN=Computers` mimo przekierowania kontenera domyślnego (`redircmp`), nowy serwer | średni | e-mail w godzinach pracy |
| Usunięcia | usunięte / przywrócone konto, grupa, komputer, OU; masowe usuwanie (≥ 10 kont, grup lub OU w 10 min – krytyczny; ≥ 50 komputerów w 10 min – wysoki) | średni (OU – wysoki) | e-mail |
| Stan konta | wyłączenie, włączenie (włączenie konta wyłączonego lub nieużywanego > 30 dni – wysoki), zmiana flag (np. „hasło nigdy nie wygasa”, „hasło niewymagane”) | informacja … wysoki | flagi ryzykowne: e-mail; pozostałe: raport dzienny |
| Blokady | zablokowanie konta ze **źródłem błędnych haseł** (adres IP / komputer z liczbą prób), odblokowanie przez administratora, wiele blokad w krótkim czasie (≥ 5 kont lub 0,5 % kont w 10 min), opcjonalnie wykrywanie ataku „password spraying” z dzienników DC (≥ 20 kont z jednego adresu w 10 min; nazwy kont, które nie istnieją, są maskowane do pierwszego znaku i długości, np. `L…(9)`, bo bywają hasłem wpisanym w pole nazwy użytkownika – liczba kont pozostaje dokładna) | niski (masowe blokady – średni, spraying – wysoki) | blokady: e-mail do helpdesku w godzinach pracy, najwyżej jeden na konto na godzinę; spraying: e-mail zawsze |
| Hasła | reset hasła przez administratora (reset z opcją „użytkownik musi zmienić hasło przy następnym logowaniu” daje oba zdarzenia: reset – liczony jako reset w raporcie i regułach – oraz „użytkownik musi zmienić hasło”), zmiana hasła przez użytkownika, „użytkownik musi zmienić hasło”, przedłużenie ważności hasła bez jego zmiany (sztuczka `pwdLastSet` 0 → -1) | informacja (przedłużenie – średni; reset hasła konta uprzywilejowanego lub serwisowego – wysoki) | raport dzienny; reset hasła konta uprzywilejowanego i przedłużenie ważności: e-mail |
| Parametry kont | każda obserwowana zmiana atrybutu: stara → nowa wartość (dział, stanowisko, przełożony, telefon, UPN, skrypt logowania, profil, ograniczenia logowania …) | informacja (skrypty/profile/ograniczenia – średni) | raport dzienny; skrypty i profile: e-mail |
| Grupy | dodanie / usunięcie członka (także członka z innej domeny lasu, np. z domeny podrzędnej), zmiana typu grupy | informacja | raport dzienny; grupy obserwowane: e-mail |
| Dostęp uprzywilejowany | uzyskanie członkostwa (także pośredniego, przez zagnieżdżone grupy i grupę podstawową, również po przywróceniu konta z Kosza AD) w Domain Admins, Enterprise Admins, Administrators, Schema Admins, operatorach kont/kopii/serwerów, DnsAdmins, grupach ról samego Watchdoga … | krytyczny / wysoki | e-mail natychmiast, całą dobę |
| Atrybuty krytyczne | adminCount, SID History, delegowanie Kerberos, SPN na koncie użytkownika, msDS-KeyCredentialLink (Shadow Credentials), zmiana grupy podstawowej, powiązania migracji kont dMSA (po obu stronach – BadSuccessor), jawne mapowania certyfikatów (`altSecurityIdentities`), lista odczytujących hasło gMSA, zmiana uprawnień (ACL) obiektów krytycznych (domena, AdminSDHolder, OU kontrolerów) | wysoki / krytyczny | e-mail natychmiast, całą dobę |
| Domena i las | zasady haseł i blokad, zasady szczegółowe (PSO), powiązania GPO z domeną i OU kontrolerów, relacje zaufania, nowy lub usunięty kontroler domeny (awansowanie serwera na DC w jednym kroku daje tylko „nowy kontroler domeny”, bez dodatkowego krytycznego zdarzenia o delegowaniu), przeniesienie ról FSMO, szablony certyfikatów i usługi PKI (NTAuth), włączenie Kosza AD, `dSHeuristics`, `tombstoneLifetime`, zmiany schematu (jedno zdarzenie na cykl, nigdy powtórzone po błędzie cyklu; zapisy wykonane w trakcie cyklu zgłasza następny) | wysoki / krytyczny | e-mail natychmiast |
| Dzienniki DC | wyczyszczenie dziennika zabezpieczeń (1102), zmiana zasad inspekcji (4719), zmiana hasła DSRM (4794) | krytyczny / wysoki | e-mail natychmiast |
| Terminy | konto wygasa za 14 / 7 / 1 dzień (dni kalendarzowe do ostatniego dnia ważności; gdy termin wypada w weekend lub święto – w poprzedzającym dniu roboczym), konto wygasło, hasło konta uprzywilejowanego lub serwisowego wygasa | informacja (uprzywilejowane ≤ 7 dni – wysoki) | helpdesk: e-mail w godzinach pracy; raport dzienny; opcjonalnie przełożony konta |
| Stan usługi | brak połączenia z DC, ponowna synchronizacja, braki uprawnień, problemy z dziennikami DC, nieudane wysyłki, mało miejsca, przestarzałe środowisko .NET, **zmiana konfiguracji Watchdoga** | wysoki / średni | e-mail i wszystkie kanały Teams/webhook zawsze (reguła zablokowana – nie da się jej wyłączyć ani osłabić jej doręczania) |

Każde zdarzenie zawiera: **co** się zmieniło (stara → nowa wartość), **kiedy** i **na którym DC**, a gdy pozwalają na
to dzienniki kontrolerów – **kto** to zrobił i **skąd** (adres IP, nazwa komputera z oznaczeniem pochodzenia – nazwa
podana przez klienta lub z DNS nie jest dowodem – i poziom pewności). Wartości haseł, LAPS, BitLockera, sekretów LSA i
atrybutów poufnych nigdy nie są odczytywane ani wysyłane – w powiadomieniu pojawia się wyłącznie „zmieniono (wartość
ukryta)”.

Watchdog celowo **nie hałasuje**:

- Przy pierwszym uruchomieniu robi **zrzut bazowy** domeny i nie wysyła żadnych powiadomień o zmianach.
- Przez pierwsze **7 dni** po zrzucie działa **tryb wdrożenia**: natychmiast powiadamia tylko o zdarzeniach
  krytycznych (reguły „tier0-*”, zdarzenia masowe, stan usługi), a resztę zbiera w raporcie dziennym; zakładka Reguły
  pokazuje przy każdej regule, ile wiadomości by wysłała. Administrator kończy tryb wcześniej jednym przyciskiem
  („Zakończ tryb wdrożenia” zapisuje wyłącznie tę flagę – inne niezapisane zmiany w regułach, kanałach i raporcie
  dziennym pozostają niezapisane na nowej wersji konfiguracji i trzeba je zapisać osobno).
- Konto zakładane w kilku krokach (utworzenie → hasło → włączenie, skrypty masowe) daje **jedno** zdarzenie „Nowe
  konto” ze stanem końcowym (okno 2 min).
- Własna zmiana hasła przez administratora, automatyczna rotacja haseł gMSA, wpis SDProp (adminCount) po już zgłoszonym
  nadaniu uprawnień, zmiany działu czy telefonu z nocnej synchronizacji kadrowej, rejestracja klucza urządzenia
  (Windows Hello / dołączenie hybrydowe), usuwanie obiektów podrzędnych (klucze BitLocker, rekordy DNS, urządzenia) i
  wyzerowanie blokady przy pierwszym logowaniu po jej wygaśnięciu **nie** generują pilnych powiadomień.
  „Własna” zmiana hasła to tylko zdarzenie 4723 zapisane przez to samo konto (albo przez ANONYMOUS LOGON – wygasłe
  hasło zmieniane przy logowaniu). Hasło konta uprzywilejowanego zmienione przez **inne** konto pozostaje zdarzeniem
  wysokim i wysyła alarm.
- Zmiany, które obiekt wprowadza sam w sobie, reguła „tier0-security” (`ExcludeSelfChanges`) pomija tylko wtedy, gdy są
  rutyną: rotacja klucza konta komputera oraz samodzielna rejestracja SPN przez gMSA/dMSA – i nigdy dla kontrolerów
  domeny ani obiektów uprzywilejowanych. Zmiany Tier 0 we własnym obiekcie (RBCD, SID History, własny SPN konta
  użytkownika, grupa podstawowa) zawsze alarmują – spodziewaj się alarmu np. w scenariuszu RBCD z przekazanym
  uwierzytelnieniem konta komputera.
- **Wykluczenia** (`ExcludedSubtrees`): dodanie poddrzewa do wykluczeń po cichu usuwa jego obiekty z migawki – bez
  zdarzenia „przeniesiono” dla każdego obiektu; sama zmiana konfiguracji jest zgłaszana jako „Zmiana konfiguracji
  Watchdog”. OU przeniesiona do wykluczonego poddrzewa daje **jedno** zdarzenie „przeniesiono poza zakres”
  (`outOfScope`) dla samej OU, nie dla każdego obiektu w środku. OU przeniesiona z powrotem albo usunięcie wykluczenia
  daje jedno zdarzenie „obiekt pojawił się”, a obiekty w środku są przejmowane po cichu. Obiekty utworzone w tym samym
  odpytaniu nadal są zgłaszane jako nowe (np. „Nowe konto”). Znacznik `outOfScope` przy „obiekt pojawił się” oznacza
  wyłącznie rzeczywiste przeniesienie do zakresu, nigdy samo istnienie wykluczeń.
- **Masowe usunięcia i masowe blokady** są liczone według czasu samej zmiany, nie czasu jej wykrycia. Usunięcia lub
  blokady rozłożone na wiele godzin, ale wykryte razem (nadrabianie po przerwie w pracy usługi, przełączenie na inny DC
  i ponowna synchronizacja, nocne uzgodnienie), nie są zdarzeniem masowym. Prawdziwa seria wykryta z opóźnieniem jest
  zgłaszana raz.
- Po przełączeniu na inny DC (ponowna synchronizacja) obiekty utworzone do 3 godzin przed ostatnim odpytaniem są
  zgłaszane jako nowe, a nie jako „obiekt pojawił się” (opóźnienie replikacji, różnica zegarów).
- Zamiana lub przesunięcie nazw OU w jednym odpytaniu (np. `Sprzedaż` → `Sprzedaż_stara` i `Sprzedaż_nowa` →
  `Sprzedaż`) zachowuje poprawne DN obiektów i nie daje fałszywych przeniesień; poprzednim DN obiektu, który opuścił
  przemianowaną w tym samym odpytaniu OU, jest jego rzeczywisty stary DN.
- Dobowe uzgodnienie: obiekt znaleziony pod starym DN, którego skanowanie nie widzi, to „obiekt zniknął” (niski), a nie
  „przeniesiono” z tym samym poprzednim i nowym DN.

Usunięcia a Kosz AD: bez Kosza AD (domyślne ustawienie Windows, Samba) obiekt utworzony i usunięty między dwoma
odpytaniami jest zgłaszany jako usunięty. Z włączonym Koszem AD ostateczne usunięcie (recykling) dawno usuniętego
obiektu – po ok. 180 dniach – nie jest zgłaszane jako nowe usunięcie. Przywrócenie takiego krótko żyjącego obiektu to
zdarzenie „przywrócono obiekt”.

Członkostwo w trybie `MembershipTracking: PrivilegedAndWatchListOnly` (tylko grupy uprzywilejowane i obserwowane):
zapisywane są także listy członków grup zagnieżdżonych w grupach uprzywilejowanych lub obserwowanych, więc ich
kolejne zmiany są zgłaszane jako dodanie / usunięcie członka – również zaraz po zrzucie bazowym. Pierwszy odczyt po
zmianie trybu jest cichy (bez zdarzeń dla istniejących członków), a grupa, która przestaje być zagnieżdżona, traci
zapisaną listę.

Członkowie z innej domeny lasu (także z domeny podrzędnej): ich usunięcie z grupy jest zgłaszane jako usunięcie
członka, a dla grup uprzywilejowanych także jako odebranie dostępu uprzywilejowanego – usługa nie widzi obiektów innej
domeny, więc nie traktuje ich jako usuniętych.

---

## 2. Jak to działa

```
            ┌──────────── serwer członkowski domeny (Tier 0), konto gMSA CONTOSO\svc-admngwd$ ───────┐
            │ AdMng.Watchdog.exe (usługa „AdMng Watchdog”)                                          │
  DC (PDC) ◄┼─ LDAP 389, Kerberos + podpis + szyfrowanie: co 15 s sprawdzenie highestCommittedUSN,     │
            │  pobranie tylko zmienionych obiektów (uSNChanged), porównanie z migawką                  │
  zapisywalne◄┼─ RPC (Remote Event Log): zdarzenia 4720…4781, 5136…5141; na żądanie 4624/4771/4776     │
  DC domeny │  → kto i skąd                                                                            │
            │  reguły → kolejka powiadomień → SMTP / Teams / webhook (ponawianie, limity)              │
            │  %ProgramData%\AdMng\Watchdog\data   (SQLite, klucze, kopie zapasowe)                     │
            │  %ProgramData%\AdMng\Watchdog\config (ustawienia startowe – tylko do odczytu dla gMSA)   │
            │  API HTTPS 8443 (Kerberos, dostęp tylko dla wskazanych grup AD), /healthz dla monitoringu │
            └──────────────────────────────────────▲───────────────────────────────────────────────┘
                                                   │ HTTPS + Kerberos
                              AD Manager → Narzędzia → Watchdog (Ctrl+Shift+A)
```

1. **Wykrywanie (LDAP).** Usługa jest „przypięta” do jednego kontrolera (domyślnie emulatora PDC – tam trafiają
   blokady kont). Co 15 sekund odczytuje z RootDSE licznik `highestCommittedUSN`. Gdy się zmienił, pobiera tylko
   obiekty o `uSNChanged` większym niż ostatnio przetworzony i porównuje obserwowane atrybuty z migawką zapisaną w
   folderze danych. Członków grup i przełożonych porównuje po GUID obiektów, więc przeniesienie lub zmiana nazwy
   konta nie wygląda jak zmiana członkostwa. Usunięte obiekty widzi w kontenerze `Deleted Objects`, a raz na dobę
   (domyślnie 02:30) wykonuje pełne uzgodnienie. Postęp zapamiętuje osobno dla każdego DC, więc powrót na PDC po
   awarii lub przeniesieniu roli nie wymaga ponownego pełnego skanu.
2. **Atrybucja (dzienniki DC).** Z metadanych replikacji usługa wie, na którym DC i kiedy powstała zmiana. Czyta
   dziennik Security tego kontrolera i dopasowuje zdarzenie (np. 4738 dla konta, 4728 dla grupy, 5136 dla atrybutu).
   Komputer źródłowy ustala z logowania 4624 tej samej sesji (najpierw adres IP, potem nazwa stacji), a dla blokad –
   z pola „Caller Computer Name” zdarzenia 4740 oraz zdarzeń 4771/4776/4625 z PDC (adresy, z których szły błędne
   hasła, z liczbą prób). Dziennik bywa opóźniony – wtedy zdarzenie ma status „ustalanie…”, a po dopasowaniu jest
   uzupełniane (dla zdarzeń krytycznych przychodzi dodatkowe „Uzupełnienie” w tym samym wątku – dopiero gdy pierwotny
   alarm został doręczony; czeka, dopóki alarm jest ponawiany, i nie jest wysyłane wcale, gdy alarm ostatecznie się
   nie powiódł; uzupełnienie powstaje także wtedy, gdy wykonawcę ustalono w trakcie przygotowywania alarmu). Gdy
   ustalony wykonawca **podnosi priorytet** zdarzenia do wysokiego lub krytycznego (np. Shadow Credentials zapisane
   przez inne konto: średni → wysoki), odbiorcy, którzy dostali już alarm z niższym priorytetem, dostają w tym samym
   wątku jedno „Uzupełnienie: priorytet podniesiony” (razem z wykonawcą – nigdy dwie wiadomości). Jeżeli
   wykonawcy nie da się ustalić, Watchdog nie zgaduje: pokazuje „nieustalony” z przyczyną (np. brak zasad inspekcji;
   po wyczyszczeniu dziennika Security – zdarzenia 1102/1104 albo numeracja rekordów zaczęta od nowa –
   „LOG_OVERWRITTEN” zamiast „NO_MATCH”).
   - **Blokady:** dopóki zdarzenie 4740 nie dotarło (paczki WEF, niedostępny PDC), źródło błędnych haseł ma status
     „ustalanie…” i jest szukane ponownie przez maksymalnie 30 minut. Konta z polskimi znakami lub apostrofem
     (`józef.żak`, `o'neill`) też dostają wyszukanie 4771/4776/4625.
   - **Dostęp uprzywilejowany** (uzyskanie / odebranie): wykonawcą jest autor zmiany dowolnego ogniwa ścieżki
     zagnieżdżeń albo zmiany grupy podstawowej konta, wykonanej najwyżej 15 minut wcześniej. W przeciwnym razie
     wykonawca pozostaje „nieustalony” – usługa nie przypisuje starszego, niezwiązanego dodania do grupy.
   - Usługa łączy się z DC i z kolektorem WEF **wyłącznie przez Kerberos** (bez NTLM), dlatego DC i kolektor muszą być
     podane jako nazwy DNS (rozdział 4.4).
3. **Powiadomienia.** Reguły decydują, które zdarzenia i komu są wysyłane: natychmiast (zbiorczo w krótkim oknie,
   z limitem wiadomości na godzinę i z uwzględnieniem godzin pracy i polskich świąt), w raporcie dziennym albo do
   Teams / webhooka. Wysyłki czekają w trwałej kolejce i są ponawiane po błędach.

---

## 3. Wymagania

- Serwer Windows Server 2022 lub nowszy (zalecany; 2019 jest obsługiwany), członek domeny (nie musi i nie powinien być
  kontrolerem domeny), zainstalowany **ASP.NET Core Runtime 10 (x64) w wersji co najmniej 10.0.13** (poprawki
  bezpieczeństwa z października 2026) – aktualizacje przez Microsoft Update/WSUS; usługa ostrzega przy starszej wersji.
- Serwer i konto usługi traktuj jako zasoby **Tier 0** (jak kontrolery domeny): kto administruje serwerem, może czytać
  dzienniki DC, odszyfrować sekrety kanałów i wyciszyć alarmy. Administruj nim tylko z PAW, bez agentów Tier 1
  działających jako SYSTEM.
- Konto usługi: **gMSA** (zalecane) z uprawnieniami tylko do odczytu, opisane niżej. Nie jest potrzebne prawo
  „Replicating Directory Changes”.
- Rekord DNS typu **A** dla usługi (np. `admng-wd.contoso.com`) – nie CNAME – i certyfikat TLS z tą nazwą
  (z wewnętrznego CA).
- Poziom funkcjonalności lasu dowolny wspierany przez Windows Server 2016+; Kerberos AES włączony (RC4 jest
  wyłączane na DC w 2026 r.).
- Dla atrybucji: zasady inspekcji na kontrolerach, SACL na obiektach i dostęp do dzienników (rozdział 4.4–4.6).
- Dla e-maili: serwer SMTP (Exchange Online, Exchange w firmie albo przekaźnik) z TLS.
- Stacje administratorów z AD Manager muszą mieć dostęp do portu usługi (domyślnie 8443/TCP); przed usługą nie może stać
  serwer pośredniczący ani urządzenie inspekcji TLS.

---

## 4. Wdrożenie krok po kroku

Polecenia PowerShell wymagają modułu ActiveDirectory (RSAT) i uprawnień administratora domeny tam, gdzie zmieniają
AD. Nazwy w przykładach: domena `contoso.com`, serwer `SRV-WD01`, gMSA `svc-admngwd`, adres usługi
`admng-wd.contoso.com:8443`.

### 4.1 Grupy AD i rekord DNS

```powershell
# Grupy ról (ich SID-y trafią do appsettings.json usługi)
New-ADGroup -Name "AdMng-Watchdog-Admins"  -GroupScope Global -GroupCategory Security -Path "OU=Grupy,OU=Firma,DC=contoso,DC=com"
New-ADGroup -Name "AdMng-Watchdog-Viewers" -GroupScope Global -GroupCategory Security -Path "OU=Grupy,OU=Firma,DC=contoso,DC=com"
# Grupa serwerów, które mogą pobrać hasło gMSA
New-ADGroup -Name "AdMng-Watchdog-Hosts" -GroupScope Global -GroupCategory Security -Path "OU=Grupy,OU=Firma,DC=contoso,DC=com"
Add-ADGroupMember "AdMng-Watchdog-Hosts" -Members (Get-ADComputer SRV-WD01)
(Get-ADGroup "AdMng-Watchdog-Admins").SID.Value
(Get-ADGroup "AdMng-Watchdog-Viewers").SID.Value
(Get-ADGroup "AdMng-Watchdog-Hosts").SID.Value

# Rekord A (nie CNAME!) – Kerberos buduje SPN z nazwy kanonicznej
Add-DnsServerResourceRecordA -ZoneName contoso.com -Name admng-wd -IPv4Address 10.0.1.25
```

Rola **Admin** może zmieniać konfigurację, testować kanały i ponawiać wysyłki; rola **Viewer** tylko przegląda
zdarzenia i stan (bez danych osobowych spoza poziomu „standardowego”, bez wyjątków i adresów odbiorców) oraz
potwierdza zdarzenia. Członkowie grupy Admin mają też uprawnienia Viewer. Zmiana członkostwa działa po odnowieniu
biletu Kerberos (wylogowanie albo `klist purge` na stacji); do natychmiastowego odebrania dostępu służy lista
`DeniedSids` w `appsettings.json`. Zmiany członkostwa tych trzech grup Watchdog sam zgłasza jako zmiany dostępu
uprzywilejowanego.

### 4.2 Konto gMSA ze SPN-em

```powershell
# Raz na las (w laboratorium można użyć -EffectiveTime ((Get-Date).AddHours(-10)))
Add-KdsRootKey -EffectiveImmediately

New-ADServiceAccount -Name svc-admngwd `
    -DNSHostName admng-wd.contoso.com `
    -PrincipalsAllowedToRetrieveManagedPassword "AdMng-Watchdog-Hosts" `
    -ServicePrincipalNames "HTTP/admng-wd.contoso.com" `
    -KerberosEncryptionType AES128,AES256

# Na serwerze SRV-WD01 (po restarcie lub klist -li 0x3e7 purge, aby serwer „zobaczył” nową grupę)
Install-ADServiceAccount svc-admngwd
Test-ADServiceAccount svc-admngwd        # musi zwrócić True

setspn -Q HTTP/admng-wd.contoso.com     # SPN musi należeć wyłącznie do svc-admngwd$
```

- **Nie** używaj `-RestrictToOutboundAuthenticationOnly` – klienci AD Manager uwierzytelniają się do usługi Kerberosem.
- Nie dodawaj `HTTP/SRV-WD01…` do gMSA (zepsułoby to inne usługi HTTP serwera, np. WinRM).
- Gdy zespół AD nie zgadza się na SPN na koncie usługi, użyj trybu **HTTP.sys** (rozdział 4.10) – wtedy Kerberos
  obsługuje konto komputera i SPN nie jest potrzebny.

### 4.3 Uprawnienia do odczytu katalogu

W wielu domenach zwykły użytkownik czyta konta dzięki grupie „Pre-Windows 2000 Compatible Access”. W domenach
utwardzonych tak nie jest i obiekty, których konto nie może odczytać (np. `uSNChanged`, `lockoutTime`), **po cichu
znikają** ze śledzenia. Dlatego nadaj uprawnienia jawnie:

```powershell
$d = "DC=contoso,DC=com"; $a = "CONTOSO\svc-admngwd$"
$classes = "user","inetOrgPerson","computer","group","organizationalUnit","contact",
           "msDS-ManagedServiceAccount","msDS-GroupManagedServiceAccount","msDS-DelegatedManagedServiceAccount"
$schema = (Get-ADRootDSE).schemaNamingContext
foreach ($c in $classes) {
    # klasa dMSA istnieje tylko w schemacie Windows Server 2025 – brakujące klasy pomijamy
    if (Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=$c)") { dsacls $d /I:S /G "${a}:GR;;$c" }
}
dsacls $d /G "${a}:GR"                                   # obiekt domeny: zasady haseł i blokad, role FSMO
dsacls "CN=AdminSDHolder,CN=System,$d" /G "${a}:GR"     # konta chronione (adminCount=1) dziedziczą ACL z AdminSDHolder (SDProp, do 60 min)
dsacls "CN=Password Settings Container,CN=System,$d" /I:T /G "${a}:GR"   # zalecane: PSO – właściwy czas blokady kont
```

Dlaczego jawne nadanie po klasach: wpisy dziedziczone „dla obiektów klasy X” dotyczą klasy strukturalnej obiektu, więc
konta `inetOrgPerson` i dMSA nie są objęte wpisem dla `user`.

**Usunięte obiekty** (zalecane – bez tego usunięcia są wykrywane dopiero przy dobowym uzgodnieniu jako
„obiekt zniknął”):

```powershell
dsacls "CN=Deleted Objects,$d" /takeownership           # zmienia właściciela kontenera systemowego – wymaga zgody zespołu AD
dsacls "CN=Deleted Objects,$d" /G "${a}:LCRP"
```

Uprawnienie do zapisu w AD **nie jest** potrzebne. Usługa po starcie i raz na dobę sprawdza, czy widzi wszystko, czego
potrzebuje (zakładka Watchdog → Stan → „Kontrola uprawnień”).

### 4.4 Dostęp do dzienników kontrolerów (atrybucja)

Dwie możliwości:

**A. Odczyt bezpośrednio z DC (domyślnie, najprostsze).**

```powershell
# Wbudowana grupa domenowa „Event Log Readers” (S-1-5-32-573) – jedna dla wszystkich DC domeny
Add-ADGroupMember -Identity (Get-ADGroup -Identity "S-1-5-32-573") -Members (Get-ADServiceAccount svc-admngwd)
```

Na kontrolerach (najlepiej GPO dla OU „Domain Controllers”) włącz reguły zapory „Remote Event Log Management”
(nazwy reguł są niezależne od języka systemu) i **ogranicz je do adresu serwera Watchdog**:

```powershell
Get-NetFirewallRule -Name "RemoteEventLogSvc*" | Set-NetFirewallRule -RemoteAddress 10.0.1.25 -PassThru | Enable-NetFirewallRule
```

Uwaga: członkostwo w „Event Log Readers” daje odczyt **wszystkich** dzienników na wszystkich DC domeny (także linii
poleceń z 4688, skryptów PowerShell, Sysmon). Usługa czyta tylko zdarzenia z listy (rozdział 4.5) i tylko z
zapisywalnych DC tej domeny (RODC i DC innych domen lasu są pomijane). Kontrolery wpisane ręcznie w ustawieniach
atrybucji (`attribution.domainControllers`) muszą być nazwami DNS (zalecane FQDN) – usługa łączy się z nimi wyłącznie
przez Kerberos, więc adres IP jest odrzucany przy zapisie, a `localhost` lub nazwa bez SPN kończy się błędem
połączenia. Gdy usługa nie może odczytać `CN=Sites` w partycji konfiguracji, czyta tylko te wpisane kontrolery (Stan →
„Wykrywanie kontrolerów domeny”).

**B. Windows Event Forwarding (zalecane w środowiskach utwardzonych i dużych).** Kontrolery wysyłają do kolektora
(np. sam serwer Watchdog) wyłącznie potrzebne zdarzenia, a gMSA czyta lokalny dziennik „ForwardedEvents” – bez praw
na DC:

1. Na kontrolerach: konto `NETWORK SERVICE` w grupie „Event Log Readers” (wymóg WEF dla dziennika Security) i GPO
   „Configure target Subscription Manager” wskazująca kolektor.
2. Na kolektorze: subskrypcja inicjowana przez źródła, tryb „Minimize Latency”, zapytanie XPath ograniczone do:
   4720 4722 4723 4724 4725 4726 4738 4740 4767 4781 4765 4766 4794 4741 4742 4743 4727–4737 4744–4764 4719 5136 5137
   5138 5139 5141 1102 1104 1100 oraz – jeśli ma być ustalany komputer źródłowy – 4624 (tylko LogonType 3 i 10; duży
   wolumen, zwiększ dziennik kolektora) i 4771/4776 (tylko niepowodzenia).
3. W konfiguracji Watchdog: Atrybucja → źródło „ForwardedEvents”, kolektor = nazwa DNS serwera (zalecana pełna
   nazwa FQDN; nie adres IP, nie `localhost` – usługa łączy się z kolektorem i z DC wyłącznie przez Kerberos, więc
   nazwa musi mieć SPN); gMSA w lokalnej grupie „Event Log Readers” kolektora. Adres IP jest odrzucany przy zapisie, a
   konfiguracja zapisana wcześniej z adresem IP blokuje każdy zapis, dopóki go nie poprawisz; błąd pokazuje Stan →
   „Kolektor zdarzeń (WEF)”. Bez 4624/4771/4776 w subskrypcji komputer źródłowy ma przyczynę „SOURCE_NOT_COLLECTED”.
   Zdarzenia przekazane przez WEF zachowują numery rekordów swoich DC, dlatego usługa czyta kolektor według czasu
   zdarzeń: przy każdym odczycie ponownie przegląda ostatnie 15 minut (ustawienie „replicationLatencyBound”), aby nie
   pominąć zdarzeń dostarczonych z opóźnieniem. Zdarzenia dostarczone później (np. po dłuższej przerwie łączności DC
   z kolektorem) nie zostaną uwzględnione w atrybucji – w razie potrzeby zwiększ to ustawienie. Po zalewie zdarzeń
   większym niż `maxRecordsPerPoll` (domyślnie 50 000 na odczyt; zdarzenie „Luka w dzienniku zabezpieczeń DC” z
   przyczyną „flooding”) usługa
   przez pewien czas czyta tylko zdarzenia nowsze od ostatnio odczytanego – zdarzenia dostarczone z opóźnieniem w
   trakcie zalewu mogą zostać pominięte; potem wraca 15-minutowe nakładanie odczytów.

W obu wariantach ustaw rozmiar dziennika Security na DC na co najmniej **1 GB** (GPO: Konfiguracja komputera →
Zasady → Ustawienia systemu Windows → Ustawienia zabezpieczeń → Dziennik zdarzeń) – mały dziennik nadpisuje zdarzenia,
zanim usługa je odczyta (przyczyna „LOG_OVERWRITTEN”). Po zmianie członkostwa zrestartuj usługę Watchdog.

### 4.5 Zasady inspekcji na kontrolerach domeny

GPO na OU „Domain Controllers” → Konfiguracja zaawansowanych zasad inspekcji. Tabela to **minimum** – nigdy nie obniżaj
wartości, która już obowiązuje (S = sukces, F = niepowodzenie). Ustawienia podkategorii z kilku GPO łączą się osobno
dla każdej podkategorii i wygrywa GPO o wyższym priorytecie, więc najprościej ustawić każdą podkategorię z tabeli na
„Sukces i niepowodzenie” – tak robi instrukcja wersji linuksowej ([`WATCHDOG-LINUX.md`](WATCHDOG-LINUX.md), 2.10, razem
z kopią stanu sprzed zmiany). Kontrolery mają domyślnie sukces dla Kerberos Authentication Service i Credential
Validation, a Defender for Identity wymaga dla Credential Validation sukcesu i niepowodzenia – samo „F” w GPO o wysokim
priorytecie by je wyłączyło.

| Podkategoria (nazwa angielska) | GUID (do `auditpol`) | Co najmniej | Po co |
|---|---|---|---|
| User Account Management | `{0CCE9235-69AE-11D9-BED3-505054503030}` | S, F | 4720–4726, 4738, 4740, 4767, 4781, 4794 |
| Computer Account Management | `{0CCE9236-69AE-11D9-BED3-505054503030}` | S | 4741–4743 |
| Security Group Management | `{0CCE9237-69AE-11D9-BED3-505054503030}` | S | 4727–4737, 4754–4758, 4764 |
| Distribution Group Management | `{0CCE9238-69AE-11D9-BED3-505054503030}` | S | 4744–4763 |
| Directory Service Changes | `{0CCE923C-69AE-11D9-BED3-505054503030}` | S | 5136–5141 (wymaga SACL – 4.6) |
| Logon | `{0CCE9215-69AE-11D9-BED3-505054503030}` | S, F | 4624 – komputer źródłowy; 4625 – źródło blokad |
| Kerberos Authentication Service | `{0CCE9242-69AE-11D9-BED3-505054503030}` | S, F | 4771 – źródło błędnych haseł (blokady, „password spraying”); sukces – domyślne ustawienie kontrolera |
| Credential Validation | `{0CCE923F-69AE-11D9-BED3-505054503030}` | S, F | 4776 – źródło błędnych haseł NTLM; sukces – domyślne ustawienie kontrolera |
| Audit Policy Change | `{0CCE922F-69AE-11D9-BED3-505054503030}` | S | 4719 |

Zostaw włączone „Inspekcja: wymuś ustawienia podkategorii zasad inspekcji…”. Sprawdzenie na DC:
`auditpol /get /category:*` (GUID-y podkategorii pokazuje `auditpol /list /subcategory:* /v`).

### 4.6 SACL dla zdarzeń 5136 („kto zmienił atrybut”)

Domyślne SACL-e w AD **nie** generują zdarzeń 5136/5137/5141 dla użytkowników, grup i komputerów. Wzorem Microsoft
Defender for Identity dodaj wpisy inspekcji „Wszyscy – sukces” (uruchom jako administrator domeny – wymagane prawo
„Zarządzanie inspekcją i dziennikiem zabezpieczeń”):

```powershell
Import-Module ActiveDirectory
$domain = (Get-ADDomain).DistinguishedName
$schema = (Get-ADRootDSE).schemaNamingContext
$everyone = [System.Security.Principal.SecurityIdentifier]'S-1-1-0'
$rights = [System.DirectoryServices.ActiveDirectoryRights]'WriteProperty, Delete, DeleteTree, WriteDacl, WriteOwner, CreateChild, DeleteChild, ExtendedRight, Self'
$classes = @{
  user  = 'bf967aba-0de6-11d0-a285-00aa003049e2'; group = 'bf967a9c-0de6-11d0-a285-00aa003049e2'
  computer = 'bf967a86-0de6-11d0-a285-00aa003049e2'; organizationalUnit = 'bf967aa5-0de6-11d0-a285-00aa003049e2'
  inetOrgPerson = '4828cc14-1437-45bc-9b07-ad6f015e5f28'
  msa = 'ce206244-5827-4a86-ba1c-1c0c386c1b64'; gmsa = '7b8b558a-93a5-4af7-adca-c017e67f1057'
  groupPolicyContainer = 'f30e3bc2-9ff0-11d1-b603-0000f80367c1'; passwordSettings = '3bcd9db8-f84b-451c-952f-6c52b81f9ec6'
  trustedDomain = 'bf967ab8-0de6-11d0-a285-00aa003049e2'
}
# dMSA (Windows Server 2025) – tylko gdy klasa istnieje w schemacie
$dmsa = Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=msDS-DelegatedManagedServiceAccount)" -Properties schemaIDGUID
if ($dmsa) { $classes['dmsa'] = ([guid]$dmsa.schemaIDGUID).Guid }

# 1) obiekty potomne wskazanych klas (dziedziczone z obiektu domeny)
$acl = Get-Acl -Path "AD:\$domain" -Audit
foreach ($guid in $classes.Values) {
  $acl.AddAuditRule((New-Object System.DirectoryServices.ActiveDirectoryAuditRule($everyone, $rights, 'Success', [guid]::Empty, 'Descendents', [guid]$guid)))
}
# 2) utworzenie i usunięcie obiektu tych klas w dowolnym miejscu domeny – zdarzenie 5137 kontroler zapisuje według SACL
#    obiektu nadrzędnego (OU, kontenery takie jak CN=Users, CN=Managed Service Accounts, CN=Policies, sam obiekt domeny)
foreach ($guid in $classes.Values) {
  $acl.AddAuditRule((New-Object System.DirectoryServices.ActiveDirectoryAuditRule($everyone, 'CreateChild, DeleteChild', 'Success', [guid]$guid, 'All')))
}
# 3) sam obiekt domeny (zasady haseł i blokad, gPLink, ms-DS-MachineAccountQuota) – „tylko ten obiekt”
$acl.AddAuditRule((New-Object System.DirectoryServices.ActiveDirectoryAuditRule($everyone, 'WriteProperty, WriteDacl, WriteOwner', 'Success', [guid]::Empty, 'None')))
Set-Acl -Path "AD:\$domain" -AclObject $acl

# 4) AdminSDHolder (szablon uprawnień kont chronionych)
$p = "AD:\CN=AdminSDHolder,CN=System,$domain"
$acl = Get-Acl -Path $p -Audit
$acl.AddAuditRule((New-Object System.DirectoryServices.ActiveDirectoryAuditRule($everyone, 'WriteProperty, WriteDacl, WriteOwner', 'Success', [guid]::Empty, 'None')))
Set-Acl -Path $p -AclObject $acl
```

Odczyt SACL wymaga uprawnień, których konto usługi nie ma, więc Watchdog nie sprawdzi SACL bezpośrednio. Zamiast tego
liczy pokrycie: jaki odsetek wykrytych zmian udało się przypisać wykonawcy (Stan → Atrybucja, przyczyna
„SACL_MISSING”, gdy brakuje zdarzeń 5136). Do audytu SACL można użyć skryptu Microsoft `Get-ADAuditSaclReport.ps1`.
Uwaga: reguły z warunkiem „z wyjątkiem wykonawcy X” działają niezawodnie dopiero wtedy, gdy wykonawca jest ustalany
(przy brakującym SACL usługa pokaże ostrzeżenie w zakładce Stan).

### 4.7 Certyfikat TLS

Wystaw certyfikat serwera (EKU „Uwierzytelnianie serwera”) z nazwą `admng-wd.contoso.com` w magazynie
`LocalMachine\My` serwera i nadaj gMSA prawo **odczytu klucza prywatnego**: certlm.msc → Osobisty → Certyfikaty →
certyfikat → Wszystkie zadania → Zarządzaj kluczami prywatnymi… → dodaj `CONTOSO\svc-admngwd$` (Odczyt). Skrypt
instalacyjny robi to automatycznie. Po automatycznym odnowieniu certyfikatu nowy klucz dostaje domyślne ACL – w
szablonie AD CS zaznacz „Autoryzuj dodatkowe konta usług w celu uzyskania dostępu do klucza prywatnego” albo ponów
nadanie uprawnień. Do czasu nadania uprawnień usługa używa poprzedniego, jeszcze ważnego certyfikatu i zgłasza to jako
**ostrzeżenie** 1007 w dzienniku Application. Gdy poprzedni certyfikat wygaśnie, pojawia się **błąd** 1007 (połączenia
TLS przestają działać) oraz stan „Klucz prywatny certyfikatu TLS niedostępny” (`certificate.key`) w zakładce Stan;
zbliżające się wygaśnięcie certyfikatu pokazuje stan `certificate.expiry`. Usługa przyjmuje tylko TLS 1.2 i 1.3.

### 4.8 Foldery usługi

```
%ProgramData%\AdMng\Watchdog\            SYSTEM, Administratorzy – pełna kontrola; gMSA – odczyt (tylko ten folder)
  config\appsettings.json                ustawienia startowe: gMSA tylko ODCZYT (usługa nie wystartuje, jeśli może tu pisać)
  data\                                  SYSTEM, Administratorzy – pełna kontrola; gMSA – modyfikacja
    watchdog.db (+ -wal, -shm), keys\, backup\
```

Skrypt instalacyjny tworzy foldery z wyłączonym dziedziczeniem (SID-y zamiast nazw, bo polski Windows ma
„Administratorzy” zamiast „Administrators”):

```powershell
$root = "$env:ProgramData\AdMng\Watchdog"
icacls "$root\data"   /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "CONTOSO\svc-admngwd$:(OI)(CI)M"
icacls "$root\config" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "CONTOSO\svc-admngwd$:(OI)(CI)R"
```

Skrypt instalacyjny (także przy aktualizacji) przywraca wszystkim plikom i podfolderom wewnątrz `config\` i folderu
danych uprawnienia dziedziczone – jawne wpisy ACL nadane tam ręcznie są usuwane.

Przy każdym starcie usługa sprawdza uprawnienia folderu `data\`, plików bazy oraz folderów `data\keys\` i
`data\backup\`: jeśli dostęp ma ktokolwiek poza SYSTEM, Administratorami i kontem usługi, **nie uruchomi się**
(dziennik Application, kod wyjścia 5). Folder musi
leżeć na lokalnym dysku NTFS (nie na udziale sieciowym); zalecane jest szyfrowanie BitLocker woluminu. Baza SQLite
nie tworzy plików tymczasowych poza folderem danych, a usuwane rekordy są nadpisywane.

### 4.9 Instalacja usługi

Rozpakuj archiwum `AdMng-Watchdog-<wersja>-win-x64.zip` z wydania (albo artefakt CI `AdMng-Watchdog-win-x64`). Skrypty nie
są podpisane: archiwum pobrane z Internetu ma znacznik pochodzenia, który przechodzi na rozpakowane pliki, i zasada
wykonywania RemoteSigned zablokuje wtedy `Install-AdMngWatchdog.ps1` – odblokuj pliki w folderze archiwum
(`Get-ChildItem -Recurse | Unblock-File`). Potem uruchom jako administrator:

```powershell
.\Deploy\Install-AdMngWatchdog.ps1 `
    -ServiceAccount 'CONTOSO\svc-admngwd$' `
    -Url 'https://admng-wd.contoso.com:8443' `
    -CertificateThumbprint '<odcisk>' `
    -AdminGroupSid '<SID AdMng-Watchdog-Admins>' -ViewerGroupSid '<SID AdMng-Watchdog-Viewers>' `
    -HostGroupSid '<SID AdMng-Watchdog-Hosts>' `
    -AllowedSubnets '10.0.20.0/24' -MonitoringSubnets '10.0.30.5/32'
```

Skrypt: kopiuje pliki do `C:\Program Files\AdMng\Watchdog`, tworzy foldery `config\` i `data\` z ACL (4.8), zapisuje
`config\appsettings.json`, tworzy źródło dziennika „AdMng Watchdog”, nadaje prawo „Logowanie w trybie usługi”,
uprawnienie do klucza certyfikatu, wyłącza zrzuty awaryjne WER dla `AdMng.Watchdog.exe` (mogłyby zawierać sekrety),
rejestruje usługę `AdMngWatchdog` (start „automatyczny – opóźniony”, konto gMSA bez hasła, automatyczny restart po
awarii), dodaje regułę zapory dla wskazanych podsieci, sprawdza SPN, uruchamia usługę i sprawdza ją poleceniem
`AdMng.Watchdog.exe --health`. Skrypt działa w Windows PowerShell 5.1 i PowerShell 7.

Aktualizacja to ponowne uruchomienie skryptu: istniejący `config\appsettings.json` jest **scalany** – zmieniają się
tylko ustawienia podane w parametrach, a ręczne wpisy (np. `DeniedSids`, `ViewerDetailLevel`, `Notifications`,
`Directory`, `Server: "HttpSys"`) zostają; skrypt wypisuje zmienione ustawienia. `-Url` i `-AdminGroupSid` są
wymagane i zawsze zastępują swoje wartości – **`-AdminGroupSid` zastępuje całą listę `AdminSids`**, więc druga grupa
administratorów dopisana ręcznie znika przy aktualizacji (dopisz ją ponownie). `-ViewerGroupSid`, `-HostGroupSid`,
`-AllowedSubnets` i `-MonitoringSubnets` zastępują swoje listy tylko wtedy, gdy zostały podane.

Zanim skrypt cokolwiek zmieni, sprawdza istniejący `config\appsettings.json`: jeśli właścicielem pliku lub folderu nad
nim (aż do `%ProgramData%\AdMng`) jest albo prawo zmiany ma konto inne niż SYSTEM i Administratorzy, skrypt
**zatrzymuje się** i wypisuje te konta (taki plik mógł zostać podłożony, a scalenie przeniosłoby jego ustawienia do
nowej konfiguracji). Przejrzyj plik, a potem usuń go albo uruchom skrypt ponownie z przełącznikiem
`-TrustExistingConfig`.

Bez `-CertificateThumbprint` skrypt sam znajduje certyfikat po nazwie hosta (ta sama reguła co usługa) i nadaje gMSA
odczyt jego klucza. Właścicielem folderów `%ProgramData%\AdMng\…` zostają Administratorzy, a ich ACL są zastępowane w
całości – nikt, kto utworzył te foldery lub plik przed instalacją, nie zachowuje do nich praw; wszystko wewnątrz
`config\` i folderu danych dostaje z powrotem uprawnienia dziedziczone (4.8).

**Aktualizacja z wersji, w której hasło SMTP lub sekret klienta XOAUTH2 zapisano przy niestandardowej weryfikacji
certyfikatu serwera SMTP** (przypięty odcisk `PinnedSha256`, „akceptuj każdy” albo wyłączone sprawdzanie odwołań): po
aktualizacji hasło/sekret trzeba wpisać ponownie (zakładka Kanały i SMTP). Do tego czasu e-mail nie jest wysyłany, a
wysyłki kończą się kodem `secretRebindRequired` (5.2).

Odpowiednik ręczny instalacji:

```powershell
sc.exe create AdMngWatchdog binPath= "C:\Program Files\AdMng\Watchdog\AdMng.Watchdog.exe" start= delayed-auto obj= "CONTOSO\svc-admngwd$" DisplayName= "AdMng Watchdog"
sc.exe sidtype AdMngWatchdog unrestricted
sc.exe failure AdMngWatchdog reset= 86400 actions= restart/60000/restart/60000/restart/300000
sc.exe failureflag AdMngWatchdog 1
New-NetFirewallRule -Name AdMngWatchdog-HTTPS -DisplayName "AdMng Watchdog (HTTPS)" -Direction Inbound -Protocol TCP -LocalPort 8443 -Profile Domain -RemoteAddress 10.0.20.0/24,10.0.30.5 -Action Allow
```

Jeżeli prawo „Logowanie w trybie usługi” jest zdefiniowane w GPO domeny, dodaj gMSA do tej GPO (wpis w GPO zastępuje
lokalne przypisania).

### 4.10 Plik `config\appsettings.json` (ustawienia startowe)

Edytowany tylko przez lokalnych administratorów serwera (nie przez API – w ten sposób administrator Watchdoga nie
nada sobie innych uprawnień; konto usługi ma do niego tylko odczyt):

```json
{
  "Watchdog": {
    "DataDirectory": "%ProgramData%\\AdMng\\Watchdog\\data",
    "Authentication": "Negotiate",
    "Hosting": {
      "Server": "Kestrel",
      "Urls": "https://admng-wd.contoso.com:8443",
      "Certificate": { "Thumbprint": "<odcisk>", "Store": "My", "Location": "LocalMachine" },
      "RequireKerberos": true,
      "AllowedRemoteSubnets": [ "10.0.20.0/24" ],
      "MonitoringSubnets": [ "10.0.30.5/32" ]
    },
    "Authorization": {
      "AdminSids": [ "S-1-5-21-…-1302" ], "ViewerSids": [ "S-1-5-21-…-1301" ], "DeniedSids": [],
      "WatchdogHostGroupSids": [ "S-1-5-21-…-1303" ],
      "ViewerDetailLevel": "Standard",
      "AllowTier0Suppression": false
    },
    "Notifications": { "OAuthSmtpHosts": [ "smtp.office365.com" ] },
    "Directory": { "Domain": null, "Server": null, "PreferPdc": true }
  }
}
```

- `Server: "HttpSys"` – alternatywa bez SPN na gMSA; wymaga `netsh http add urlacl url=https://+:8443/
  user=CONTOSO\svc-admngwd$` i `netsh http add sslcert ipport=0.0.0.0:8443 certhash=<odcisk>
  appid={…}` (po każdym odnowieniu certyfikatu powiązanie trzeba odświeżyć). HTTP.sys negocjuje schematem
  Negotiate (z ochroną powiązania kanału); przy `RequireKerberos` usługa odrzuca każde logowanie, które nie odbyło
  się przez Kerberos (dziennik Application, zdarzenie 2500). **Do potwierdzenia na laboratorium Windows:** czy w trybie
  HTTP.sys każde logowanie rzeczywiście dociera do usługi jako Kerberos. Jeżeli każde logowanie jest odrzucane
  zdarzeniem 2500 z pakietem „unknown (Negotiate)”, użyj Kestrel ze SPN-em na gMSA albo tymczasowo ustaw
  `RequireKerberos: false` (wtedy NTLM jest przyjmowany).
- `RequireKerberos: true` odrzuca NTLM w obu trybach (ochrona przed przekazywaniem NTLM; .NET 10 nie ma tu powiązania
  kanału).
- `DeniedSids` – natychmiastowe odebranie dostępu (SID użytkownika lub grupy), bez czekania na wygaśnięcie biletu i
  bez restartu usługi.
- Usługa obserwuje ten plik: zmiany w `Authorization`, `Notifications` oraz w listach `AllowedRemoteSubnets` i
  `MonitoringSubnets` działają w ciągu kilku sekund. Pozostałe ustawienia (`Urls`, `Server`, `Certificate`,
  `RequireKerberos`, `DataDirectory`, `Authentication`, `Directory`) wymagają restartu usługi
  (`Restart-Service AdMngWatchdog`). Wyjątki od „w ciągu kilku sekund”:
  - `AllowedRemoteSubnets` i `MonitoringSubnets` od razu zmieniają filtr samej usługi, ale **nie** regułę zapory
    Windows `AdMngWatchdog-HTTPS`, którą skrypt instalacyjny zbudował z tych list – uruchom skrypt ponownie albo
    popraw regułę ręcznie.
  - `AdminSids`, `ViewerSids` i `WatchdogHostGroupSids` od razu zmieniają dostęp do API, ale obserwacja tych grup jako
    grup uprzywilejowanych (zdarzenia wysokie) korzysta z wartości z ostatniego startu – po ich zmianie zrestartuj
    usługę.
- Gdy zapisanej zmiany pliku nie da się wczytać (błąd składni JSON) ani zastosować (błędna wartość, np. `"Minimalny"`
  zamiast `"Minimal"` albo `"tak"` zamiast `true`), usługa **zachowuje poprzednie ustawienia** i po każdym takim zapisie
  wpisuje ostrzeżenie **2002** do dziennika Application. Do czasu poprawienia pliku zmiana (np. nowy wpis w
  `DeniedSids`) **nie działa**, a ten sam plik przy następnym starcie zatrzyma usługę (kod wyjścia 2, zdarzenie 1010).
  Po każdej edycji sprawdź plik poleceniem `AdMng.Watchdog.exe --check-config` i dodaj zdarzenie 2002 do
  monitorowanych (rozdział 10).
- `ViewerDetailLevel` – ile danych widzi rola Viewer w zakładce i na stronie zdarzenia (5.6).
- `AllowTier0Suppression` – tylko lokalnie można zezwolić na wyłączenie krytycznych zdarzeń przez API (domyślnie nie).
  Bez tej zgody API odrzuca m.in. wykluczenie kontenerów `CN=Users` i `CN=Builtin` (grupy Domain Admins,
  Administrators, konto krbtgt), poddrzew z dodatkowymi grupami uprzywilejowanymi oraz pominięcie atrybutów
  `dNSHostName`, `versionNumber` i `gPCFileSysPath`. Odrzucane jest też każde nowe wykluczone poddrzewo, które według
  bieżącej migawki zawiera grupę uprzywilejowaną, grupę zagnieżdżoną na ścieżce do grupy uprzywilejowanej, konto z
  dostępem uprzywilejowanym, krbtgt albo wbudowane konto Administrator – to chroni OU warstwowego modelu
  administracji (Tier 0), nie tylko `CN=Users` i `CN=Builtin`. Wykluczenia zapisane wcześniej nie są sprawdzane
  ponownie.
- `MonitoringSubnets` – adresy systemu monitorowania, które mogą anonimowo pytać `/healthz` (zwraca tylko „ok” albo
  błąd 503).
- `Directory.Server` pozwala wskazać konkretny DC; domyślnie usługa wybiera emulator PDC.

### 4.11 Pierwsze uruchomienie

1. Po starcie usługa tworzy bazę, zapisuje domyślną konfigurację i wykonuje **zrzut bazowy** (dla ~50 000 obiektów
   kilka minut). Bez powiadomień o zmianach; na koniec zdarzenie „Zakończono zrzut bazowy”. Zaczyna się 7-dniowy
   **tryb wdrożenia** (rozdział 1).
2. W AD Manager: **Narzędzia → Watchdog** (Ctrl+Shift+A) → podaj adres `https://admng-wd.contoso.com:8443` → Połącz.
3. Zakładka **Stan**: sprawdź połączenie z DC, kontrolę uprawnień i dostęp do dzienników każdego DC.
4. Zakładka **Kanały i SMTP**: skonfiguruj SMTP, adresy grup odbiorców („Administratorzy IT”, „Helpdesk”), wyślij
   wiadomość testową.
5. Przez tydzień przeglądaj **Reguły** (przy każdej regule: „wysłałaby N wiadomości”, przycisk Podgląd), dodaj wyjątki
   z podpowiedzi (np. konto synchronizacji Entra Connect) i zakończ tryb wdrożenia.
6. Podłącz monitoring: `/healthz` z `MonitoringSubnets`, zdarzenia dziennika Application (rozdział 10, także 2002 –
   nieprzyjęta edycja `appsettings.json`) i opcjonalnie sygnał „heartbeat” webhooka do SIEM.

---

## 5. Konfiguracja w zakładce Watchdog

### 5.1 Reguły

Reguła określa: rodzaje zdarzeń, rodzaje obiektów, zakres (OU – uwzględnij/wyklucz), opcjonalnie atrybuty, tagi
(np. „konto uprzywilejowane”, „konto serwisowe”), grupy (uprzywilejowane / obserwowane / wskazane), minimalny
priorytet, wykluczenia wykonawców (po SID) oraz dostarczanie: natychmiast (grupy odbiorców e-mail, kanały
Teams/webhook), raport dzienny, godziny (zawsze / tylko w godzinach pracy / domyślnie: zawsze dla wysokich i
krytycznych), okno zbierania (domyślnie 5 min), limit wiadomości na godzinę (domyślnie 10; zdarzenia krytyczne bez
limitu) i tłumienie powtórzeń dla tego samego obiektu (blokady: 1 h). Limit wiadomości na godzinę (na regułę i kanał)
liczy wiadomości **dodane do kolejki** w ostatniej godzinie, nie tylko już wysłane – awaria kanału nie pozwala więc
nazbierać serii, która po naprawie wyszłaby naraz; po restarcie usługi liczą się tylko wiadomości już wysłane.

Wbudowane reguły można edytować, wyłączać i przywracać do ustawień domyślnych; reguły „tier0-*” obejmują krytyczne
zmiany bezpieczeństwa. Regułę „Stan usługi Watchdog” można tylko edytować (nie wyłączyć); wysyła ona także do
wszystkich włączonych kanałów Teams/webhook. Przycisk **Podgląd** pokazuje, ile zdarzeń z ostatnich dni dopasowałaby
reguła i ile wiadomości by wysłała.

- Shadow Credentials (`msDS-KeyCredentialLink`) na koncie zwykłego użytkownika zapisane przez **inne** konto wywołują
  alarm reguły „tier0-security” od razu po ustaleniu wykonawcy – także wtedy, gdy własna reguła dla tych samych
  odbiorców dopasowała zdarzenie wcześniej, ale wyklucza tego wykonawcę.
- **Kilka reguł, ci sami odbiorcy**: odbiorca (grupa e-mail, kanał) dostaje o jednym zdarzeniu jeden alarm, z
  najsilniejszym ustawieniem wszystkich reguł, które go teraz obejmują – najwyższy priorytet, najkrótsze okno zbierania,
  dostarczenie także poza godzinami pracy, jeśli choć jedna z nich tak dostarcza; w alarmie widnieje reguła najwyżej na
  liście. Reguła, która w danej chwili nie może wysyłać (godziny pracy, tryb wdrożenia, tłumienie powtórzeń), nie
  wpływa na alarm. Dotyczy to także zmian po ustaleniu wykonawcy: wbudowana reguła „tier0-security”, która dopasuje
  zdarzenie dopiero po atrybucji, przejmuje wstrzymane powiadomienie własnej reguły z niższym priorytetem (alarm ma
  priorytet i nazwę „tier0-security” i nie czeka na godziny pracy), a gdy reguła wstrzymanego powiadomienia przestaje
  pasować (np. wyklucza ustalonego wykonawcę), powiadomienie przejmuje następna reguła, która nadal pasuje.
- Własne reguły z wymaganymi tagami „reset hasła” (`passwordReset`), „zmiana własna” (`selfChange`) lub „konto
  synchronizacji” (`syncAccount`) działają – te tagi są nadawane po atrybucji, a reguły są wtedy oceniane ponownie.
- Po ponownej ocenie odbiorcy, którzy dostali już alarm o tym zdarzeniu z co najmniej tym samym priorytetem i z ustalonym
  wykonawcą (albo dostaną „Uzupełnienie”), nie dostają drugiego alarmu z nowej reguły. W alarmie zbiorczym liczy się
  priorytet, z jakim pokazano **to** zdarzenie, a nie najwyższy priorytet całej wiadomości. Usługa sprawdza to jeszcze
  raz tuż przed wysłaniem, więc alarm wysłany w tej samej chwili co ponowna ocena nie zostanie powtórzony, a przejęcie
  powiadomienia przez regułę „tier0-security” nie przepadnie, gdy wstrzymane powiadomienie jest akurat wysyłane.
  Priorytet nadany przez regułę (np. „Krytyczny”) zostaje po ustaleniu wykonawcy.
- **Podniesienie priorytetu po ustaleniu wykonawcy (eskalacja)**: gdy odbiorca dostał już alarm o zdarzeniu, a po
  ustaleniu wykonawcy reguła, która go obejmuje – ta sama, która wysłała alarm, albo nowa (np. „tier0-security”) –
  daje mu priorytet wysoki lub krytyczny, wyższy niż widział, dostaje **jedno** uzupełnienie w wątku tego alarmu, a nie
  drugi alarm: e-mail z tematem „Re: …” (nagłówki `In-Reply-To`/`References`), baner „Uzupełnienie: priorytet
  podniesiony” albo „Uzupełnienie: wykonawca ustalony, priorytet podniesiony” i wiersz „Priorytet: Średni → Wysoki”;
  w Teams karta uzupełnienia z tym samym tytułem i wierszem „Priorytet” przy każdym zdarzeniu; webhook typu
  `ad.severity.escalated` (5.4). Dotyczy to także alarmu „średniego” wysłanego z wykonawcą „ustalanie…” po upływie
  czasu oczekiwania (dla niego zwykłe uzupełnienie nie przychodzi) oraz zdarzenia pokazanego jako średnie w zbiorczym
  alarmie – uzupełnienie obejmuje tylko zdarzenia, którym podniesiono priorytet. Jeżeli uzupełnienie z wykonawcą i
  tak by przyszło (alarm wysoki, który staje się krytyczny), odbiorca dostaje jedną wiadomość z wykonawcą i nowym
  priorytetem. Eskalacja jest wysyłana tylko raz (także po restarcie usługi), dopiero po doręczeniu alarmu i z
  ustalonym wykonawcą. Godziny dostarczania reguły działają jak przy alarmie (domyślnie wysoki i krytyczny – zawsze;
  reguła „tylko w godzinach pracy” poza nimi nie eskaluje, zdarzenie pokazuje raport dzienny), tryb wdrożenia i
  tłumienie powtórzeń też: reguła, która przy tym zdarzeniu milczała jako powtórzenie, nie wysyła eskalacji ze swoim
  priorytetem.
  Eskalacja przechodzi przez limity i ponawianie kanału, nie zależy od ustawienia
  `notifications.followUpWhenAttributed` i – jak każde uzupełnienie – nie jest wstrzymywana przez limit wiadomości
  reguły na godzinę (jest najwyżej jedna na alarm i odbiorcę). Obniżenie priorytetu nigdy nie wysyła nowej wiadomości –
  zwykłe uzupełnienie z wykonawcą pokaże wtedy nowy priorytet (np. „Wysoki → Info” przy własnej zmianie hasła
  użytkownika).
- **Uzupełnienia pokazują priorytet każdego zdarzenia**: w uzupełnieniu zbiorczego alarmu każde zdarzenie ma swój
  ostateczny priorytet (kolumna „Priorytet”), a nie najwyższy priorytet całego alarmu.
- **Priorytet własny a priorytet wyświetlany**: priorytet ustawiony w regule podnosi priorytet zdarzenia widoczny na
  liście, w raporcie dziennym i w licznikach, ale nie wpływa na dopasowanie innych reguł – „minimalny priorytet” każdej
  reguły jest porównywany z własnym priorytetem zdarzenia (wykrycie, tagi, atrybucja), także przy ponownej ocenie po
  ustaleniu wykonawcy i przy zwalnianiu wstrzymanego powiadomienia. Reguła przeglądowa podnosząca zmiany haseł kont
  uprzywilejowanych do „Wysokiego” nie sprawi więc, że własna zmiana hasła użytkownika (priorytet własny: informacyjny)
  wyśle alarm reguły „Konta uprzywilejowane i serwisowe”. Zdarzenia zapisane przed aktualizacją usługi przyjmują
  dotychczasowy priorytet jako własny.
- `SuppressComputerSelfChanges` (domyślnie włączone) działa także przy włączonych `TrackComputerPasswordChanges` lub
  `TrackComputerOsChanges`: rutynowe zmiany, które komputer wprowadza sam w sobie (rotacja hasła, wersja systemu), nie
  pasują do żadnej reguły, ale pozostają na liście zdarzeń.
- Stan **„Nieistniejące DN w konfiguracji”** (`rules.configuredDns`, sprawdzany najwyżej co 10 minut) wymienia OU i
  grupy wskazane w konfiguracji (zakresy reguł, grupy uprzywilejowane i obserwowane, poddrzewa, obiekty krytyczne,
  wyjątki), które przemianowano, przeniesiono lub usunięto – reguła ze starym DN niczego nie obejmuje. Usługa nigdy
  sama nie poprawia konfiguracji; `CN=Builtin`, `CN=System` i wykluczone poddrzewa nie są sprawdzane.

**Wyjątki** (np. konto synchronizacji Entra Connect, które stale zmienia `msDS-KeyCredentialLink`, albo konto
porządkujące stare komputery) wymagają podania powodu, wskazują wykonawcę po SID (wybór z AD w AD Manager), mogą mieć
datę wygaśnięcia, są zapisywane w historii zmian konfiguracji i wymieniane w każdym raporcie dziennym. „Utworzył /
kiedy” ustala usługa (konto administratora, który zapisał wyjątek, i czas serwera) – edycja wyjątku (powód, data
wygaśnięcia, zakres) ustawia je od nowa, także wiek wyjątku pokazywany w raporcie dziennym. Wyjątki wykonawców zapisane
z symbolem `{domainSid}` lub `{rootDomainSid}` niczego nie wykluczają, dopóki usługa nie zna SID-u domeny (bezpieczna
odmowa). Watchdog sam **proponuje** takie wyjątki (zakładka Reguły → „Dodaj wyjątek”), ale nigdy nie dodaje ich
automatycznie.

**Ochrona przed wyciszeniem**: każda zapisana zmiana konfiguracji wywołuje zdarzenie „Zmiana konfiguracji Watchdog”
(kto, co), wysyłane także do **dotychczasowych** odbiorców – usunięcie siebie z listy odbiorców nie przejdzie
niezauważone. Wyłączenie przez API obserwacji atrybutów i zdarzeń krytycznych (np. `msDS-KeyCredentialLink`,
dostęp uprzywilejowany) albo wykluczenie domeny lub OU kontrolerów, a także poddrzewa z grupami i kontami
uprzywilejowanymi (4.10), jest niemożliwe bez lokalnej zgody (`AllowTier0Suppression` w `appsettings.json`).

Godziny pracy: domyślnie pon.–pt. 7:00–17:00 (strefa Europe/Warsaw) z polskimi świętami (w tym Wigilia od 2025 r.).
Poza godzinami pracy powiadomienia o priorytecie niższym niż wysoki trafiają tylko do raportu dziennego – dotyczy to
też powiadomienia zbiorczego lub wstrzymanego przez limit, którego oczekiwanie kończy się już po godzinach pracy (nie
jest wysyłane, trafia tylko do raportu).

### 5.2 SMTP

| Tryb | Kiedy | Ustawienia |
|---|---|---|
| Przekaźnik bez logowania | Exchange w firmie (łącznik odbioru ograniczony do IP serwera Watchdog) lub Exchange Online przez łącznik przychodzący (certyfikat/IP) na `<tenant>.mail.protection.outlook.com:25` | port 25, **STARTTLS (wymagane)** – wariant „gdy dostępny” jest tylko ostrzeżeniem (atakujący w sieci mógłby wyłączyć szyfrowanie i czytać alarmy); uwierzytelnianie: brak |
| Logowanie (Basic) | przekaźnik zewnętrzny, Exchange w firmie | port 587, STARTTLS (wymagane), użytkownik + hasło (zapisywane zaszyfrowane, tylko do zapisu) |
| Exchange Online OAuth2 | Exchange Online – **Basic SMTP AUTH jest domyślnie wyłączane od końca grudnia 2026** | `smtp.office365.com:587`, STARTTLS, uwierzytelnianie OAuth2, chmura Entra (globalna / US Government / Chiny), użytkownik = skrzynka nadawcy |

Przygotowanie Exchange Online OAuth2 (jednorazowo, administrator Entra/Exchange):

1. Entra ID → Rejestracje aplikacji → nowa aplikacja „AdMng Watchdog SMTP”; uprawnienie aplikacji **Office 365
   Exchange Online → SMTP.SendAsApp**, zgoda administratora; dodaj certyfikat (zalecane – klucz prywatny w
   `LocalMachine\My` serwera z odczytem dla gMSA) albo sekret klienta.
2. Nadawcą niech będzie **skrzynka współdzielona z zablokowanym logowaniem** (bez hasła, którym dałoby się zalogować).
   Exchange Online PowerShell:
   ```powershell
   New-ServicePrincipal -AppId <Application (client) ID> -ObjectId <Object ID z „Aplikacje dla przedsiębiorstw”>
   Add-MailboxPermission -Identity watchdog@contoso.com -User <ServicePrincipal Id> -AccessRights FullAccess
   Set-CASMailbox -Identity watchdog@contoso.com -SmtpClientAuthenticationDisabled $false
   ```
   (Object ID musi pochodzić z „Aplikacje dla przedsiębiorstw”, nie z „Rejestracje aplikacji”.)
3. W zakładce: chmura, identyfikator dzierżawy, identyfikator aplikacji, odcisk certyfikatu (lub sekret), nadawca
   `AD Watchdog <watchdog@contoso.com>`. Token OAuth jest wysyłany tylko do serwerów z listy `OAuthSmtpHosts`.

Hasła i sekrety są **powiązane z miejscem docelowym**: po zmianie serwera, portu, użytkownika, dzierżawy lub adresu
kanału trzeba wpisać hasło/sekret ponownie – zapisany sekret nigdy nie zostanie wysłany pod nowy adres (także przy
wiadomości testowej). Hasło SMTP i sekret klienta XOAUTH2 są powiązane także z weryfikacją certyfikatu serwera SMTP:
każda zmiana przypiętego odcisku, trybu weryfikacji lub sprawdzania odwołań wymaga ponownego wpisania hasła/sekretu,
również przed wiadomością testową (inaczej administrator Watchdoga mógłby przypiąć certyfikat własnego serwera i
przechwycić hasło). Sekrety zapisane przez starsze wersje przy niestandardowej weryfikacji trzeba po aktualizacji
wpisać ponownie (4.9).

Przycisk **Wyślij wiadomość testową** pokazuje każdy etap (połączenie, TLS z wersją i szyfrem, logowanie, nadawca,
odbiorcy, treść) z kodami odpowiedzi i podpowiedziami, np. `5.7.139`/`5.7.30` – wyłączone SMTP AUTH,
`5.7.68` – odrzucone „Direct Send”. Certyfikat serwera SMTP jest zawsze weryfikowany (opcjonalnie przypięcie
odcisku SHA-256; „akceptuj każdy” istnieje tylko w trybie deweloperskim); używane są tylko TLS 1.2 i 1.3.

**Wielu odbiorców i limity.** Lista odbiorców jest sortowana i dzielona na wiadomości po `MaxRecipientsPerMessage`
adresów (domyślnie 50). Limit `MaxMessagesPerMinute` (domyślnie 20, poniżej limitu Exchange Online 30/min) liczy
**każdą wiadomość SMTP**, czyli każdą taką część, a nie każdy wpis kolejki – duże grupy zużywają więc więcej limitu, a
wysyłka może zostać odłożona w połowie bez ponownego wysyłania części już wysłanych. Uzupełnienie (np. „ustalono
wykonawcę”, „podniesiono priorytet”) trafia do tych samych odbiorców w tym samym wątku – każda część odpowiada na
odpowiadającą jej część pierwotnego alarmu.

**Częściowe doręczenie.** Gdy serwer odrzuci tylko część odbiorców:

- odrzucenie tymczasowe (4xx, np. greylisting) – pozostali dostają wiadomość, a ponowienie idzie wyłącznie do
  odrzuconych, bez duplikatów u pozostałych (ostrzeżenie 2202 w dzienniku Application); po restarcie usługi ponowienie
  może jednak trafić ponownie do wszystkich,
- odrzucenie trwałe (5xx) – odrzucony adres jest pomijany (ostrzeżenie 2203), pozostali dostają wiadomość; wysyłka
  kończy się błędem trwałym tylko wtedy, gdy odrzucono wszystkich,
- jeśli jeden odbiorca odpowiada 4xx przez ok. 22 godziny, wiadomość kończy się stanem „Niedostarczone” z kodem
  `partial.smtp.4xx…`, a kanał e-mail ma stan ostrzeżenia – bez alarmu „kanał nie może dostarczać powiadomień”.
  Ponowienie z dziennika wysyłek (rola Admin, w ciągu 14 dni) idzie wtedy tylko do brakujących odbiorców.

### 5.3 Microsoft Teams (Workflows)

Stare webhooki „Office 365 Connectors” (`*.webhook.office.com`) zostały wyłączone 18–22 maja 2026 r. – Watchdog je
odrzuca. Używaj **Workflows** (Power Automate):

1. W kanale Teams: … → Przepływy pracy (Workflows) → szablon „Post to a channel when a webhook request is
   received” / „Send webhook alerts to a channel” → skopiuj adres URL.
2. W Power Automate dodaj **współwłaścicieli** przepływu (przepływ należy do osoby, która go utworzyła – po jej
   odejściu przestaje działać) i włącz „Secure inputs/outputs” dla wyzwalacza i akcji publikowania karty (historia
   uruchomień przechowuje treść 30 dni).
3. W zakładce Watchdog → Kanały → Teams: wklej adres (jest sekretem – zawiera podpis `sig`; po zapisaniu widać tylko
   `schemat://host/…`, bez ścieżki i parametrów – podpowiedzi zapisane przez starsze wersje są skracane
   automatycznie), wybierz poziom szczegółowości (domyślnie „Standardowy”) i wyślij kartę testową.

Odpowiedź 202 oznacza tylko przyjęcie przez Workflows, nie publikację w kanale – sprawdź, czy karta testowa się
pojawiła. Przepływy właścicieli bez licencji premium są zawieszane po 90 dniach bez wywołań; codzienny raport
(domyślnie wysyłany także przy braku zmian) temu zapobiega. Tryb „Specific users in my tenant” (token Entra ID
zamiast podpisu w adresie) jest obsługiwany: aplikacja Entra z certyfikatem, zakres
`https://service.flow.microsoft.com//.default`; token jest wysyłany wyłącznie na adresy
`*.environment.api.powerplatform.com`.

### 5.4 Webhook (SIEM, systemy zgłoszeń)

Żądania `POST` JSON zgodne ze **Standard Webhooks v1**: nagłówki `webhook-id` (stały przy ponowieniach – klucz
idempotencji), `webhook-timestamp`, `webhook-signature: v1,<base64 HMAC-SHA256>`; sekret `whsec_…` generuje zakładka
(pokazany jeden raz). Opcjonalnie nagłówek `X-Hub-Signature-256` (styl GitHub). Typy zdarzeń: `ad.user.created`,
`ad.user.locked_out`, `ad.group.member_added`, `ad.privilege.granted`, `ad.burst.mass_lockout`,
`watchdog.configuration_changed`, `watchdog.digest`, `watchdog.heartbeat` … Uzupełnienia mają ten sam `data.eventId` co
alarm: `ad.attribution.updated` (ustalony wykonawca) albo `ad.severity.escalated`, gdy priorytet zdarzenia wzrósł
(5.1; z ostatecznym wykonawcą); przy zmianie priorytetu pole `data.previousSeverity` zawiera poprzedni priorytet
(np. `"severity":"high","previousSeverity":"medium"`). Domyślny poziom szczegółowości
„Minimalny” (bez wartości atrybutów). Opcja **heartbeat** (np. co 15 min) pozwala systemowi SIEM zaalarmować, gdy
Watchdog przestanie się odzywać. Sygnały heartbeat nigdy nie są ponawiane ani wysyłane po upływie swojego interwału:
w czasie awarii SIEM pojawiają się w dzienniku wysyłek jako „Niedostarczone” z kodem `heartbeat.superseded`, a SIEM
dostaje wyłącznie świeże sygnały. Weryfikacja po stronie odbiorcy (Python):

```python
import base64, hmac, hashlib, time
def verify(secret: str, msg_id: str, ts: str, body: bytes, signature_header: str) -> bool:
    if abs(time.time() - int(ts)) > 300: return False
    key = base64.b64decode(secret.removeprefix("whsec_"))
    expected = base64.b64encode(hmac.new(key, f"{msg_id}.{ts}.".encode() + body, hashlib.sha256).digest()).decode()
    return any(hmac.compare_digest(expected, s.split(",", 1)[1]) for s in signature_header.split())
```

Przy rotacji sekretu wpisz stary w pole „Poprzedni sekret” – do czasu jego usunięcia wysyłane są oba podpisy.

### 5.5 Raport dzienny

Codziennie o 7:30 (także w weekendy – jako sygnał, że usługa działa). **Raport z pierwszego dnia roboczego po
weekendzie lub święcie obejmuje cały okres od ostatniego raportu dnia roboczego** (np. od piątku 7:30 do poniedziałku
7:30), więc nic z weekendu nie umyka (opcja „Raport po dniach wolnych obejmuje cały okres”, domyślnie włączona).
Okres takiego raportu zaczyna się tam, gdzie faktycznie skończył się ostatni raport dnia roboczego (np. o 10:00, gdy
piątkowy raport wysłano z opóźnieniem po przerwie w pracy usługi). „Wyślij teraz” w poniedziałek po porannym raporcie
nie obejmuje ponownie weekendu.

Okres raportu może kończyć się kilka sekund lub minut przed godziną wysłania, gdy jakieś zdarzenie nie przeszło
jeszcze oceny reguł albo czeka jeszcze na ustalenie wykonawcy – trafi ono do następnego raportu, już z ostatecznym
priorytetem i wykonawcą (nic nie ginie i nic nie jest liczone dwa razy). Raport
przerwany zatrzymaniem usługi jest kończony po jej starcie i nie jest wysyłany drugi raz; restart usługi krótko po
wysłaniu raportu nie powoduje ponownej wysyłki.

Zawartość: stan usługi, liczniki, **„Wymaga uwagi”** (zdarzenia wysokie i krytyczne z informacją o potwierdzeniu;
zdarzenie, któremu po wysłaniu alarmu zmieniono priorytet w wątku alarmu – podniesiono go albo obniżono w
uzupełnieniu – ma dopisek „(priorytet: Średni → Wysoki)”),
sekcje: nowe konta, nowe komputery (wg OU), usunięcia, włączenia i wyłączenia, przeniesienia, dostęp uprzywilejowany,
członkostwo w grupach, zmiany parametrów kont (stara → nowa wartość, kto, kiedy), blokady (jeden wiersz na konto i
dzień: „zablokowane 5× dziś, ostatnio 10:42” – „dziś” liczone od rzeczywistej północy czasu lokalnego, także w dniach
zmiany czasu – i źródła błędnych haseł), hasła (osobno resety przez administratorów,
zmiany przez użytkowników i przedłużenia ważności), domena, GPO i PKI, zmiany bezpieczeństwa do przeglądu,
nadchodzące terminy (≤ 14 dni), a w poniedziałki także konta nieaktywne (≥ 90 dni) i liczniki higieny (hasło
niewymagane, hasło nigdy nie wygasa, delegowanie nieograniczone, osierocone `adminCount`, wiek hasła krbtgt). Aktywne
wyjątki i zmiany konfiguracji Watchdog są wymienione na końcu. Raport wysyłany jest także przy braku zmian („Brak
zmian w okresie”) – jego brak oznacza problem z usługą.

**Podgląd** raportu w zakładce (Raport dzienny → Podgląd) w sekcji „Aktywne wyjątki” pokazuje dla każdej roli tylko
ich liczbę („Liczba aktywnych wyjątków (szczegóły tylko w wysłanym raporcie)”); wysłany raport nadal je wymienia.

### 5.6 Poziomy szczegółowości

| Poziom | Co zawiera |
|---|---|
| Minimalny | rodzaj zdarzenia, obiekt, priorytet, wykonawca (konto), łącze – bez wartości atrybutów i adresów IP (wyszukiwanie tekstu w zakładce też nie uwzględnia wtedy stacji ani IP) |
| Standardowy | jak wyżej + wartości atrybutów poza danymi osobowymi (telefony, numer pracownika, uwagi, biuro, katalog domowy, godziny logowania) |
| Pełny | wszystkie wartości z wyjątkiem tajnych (hasła, LAPS, BitLocker, atrybuty poufne – nigdy) |

Domyślnie: e-mail do administratorów – pełny, helpdesk – standardowy, Teams – standardowy, webhook – minimalny,
przełożeni (powiadomienia o wygasaniu kont) – minimalny, rola Viewer w zakładce i na stronie zdarzenia – standardowy
(`ViewerDetailLevel`).

Teksty pochodzące z AD (opisy, nazwy obiektów i grup) są w powiadomieniach „rozbrajane”: niewidoczne znaki sterujące
(np. odwrócenie kierunku tekstu) są pokazywane jawnie, a adresy w treści zamieniane na `hxxps://przyklad[.]pl`, żeby
Outlook i Teams nie robiły z nich klikalnych łączy. Znaki sterujące w wartościach zapisanych w dziennikach DC są w
dowodach atrybucji pokazywane jako „�” – rekord nie jest z tego powodu odrzucany.

### 5.7 Kolejka wysyłek, ponawianie i stan kanałów

- **Kanał niesprawny** (przepływ Teams wyłączony, webhook odpowiada 404/410, błąd uwierzytelniania): usługa ponawia
  wysyłkę co godzinę, ale te ponowienia nie zwiększają licznika „Próby” w dzienniku wysyłek. Po 24 godzinach
  wiadomość jest porzucana („Niedostarczone”), a pierwszy błąd tymczasowy po naprawie kanału jest ponawiany w ciągu
  kilku sekund.
- **Wyłączenie lub usunięcie kanału** Teams/webhook albo wyłączenie SMTP od razu porzuca wiadomości czekające w kolejce
  tego kanału („Niedostarczone”, kody `channel.disabled` / `channel.unknown` / `smtp.disabled`), bez alarmu „kanał nie
  może dostarczać powiadomień”. Ponowne włączenie kanału ich nie dostarcza; Admin może je ponowić ręcznie z dziennika
  wysyłek do upływu `OutboxDeadDays` (domyślnie 90 dni).
- **Stan kanału** (zakładka Stan): błąd utrzymuje się 2 godziny od ostatniego niepowodzenia na poziomie błędu, potem
  przechodzi w ostrzeżenie aż do następnej udanej wysyłki – także wtedy, gdy nic nie jest wysyłane. Kolejna awaria po
  tym czasie wywołuje nowy alarm „kanał nie może dostarczać powiadomień”.
- Częściowe doręczenie e-maili i limity wiadomości SMTP opisuje rozdział 5.2, sygnały heartbeat – 5.4.

---

## 6. Zakładka Watchdog w AD Manager

Otwieranie: menu **Narzędzia → Watchdog**, paleta poleceń (Ctrl+K → „Watchdog”) lub **Ctrl+Shift+A**; dla wybranego
obiektu: menu kontekstowe / panel szczegółów → **„Historia zmian (Watchdog)”** (zdarzenia tego obiektu po GUID).
Zakładka działa niezależnie od połączenia z AD (zostaje otwarta po zmianie DC); łączy się z usługą przez HTTPS (tylko
`https://`) z bieżącym kontem Windows (działa też `runas /netonly`); pasek połączenia zawsze pokazuje adres usługi.
Nagłówek zakładki pokazuje liczbę niepotwierdzonych zdarzeń wysokich i krytycznych (odświeżaną co minutę także w tle).
AD Manager nie zapisuje danych Watchdoga na dysku – w ustawieniach zapamiętuje tylko adres usługi, układ i nazwane
filtry.

- **Pasek stanu** pokazuje raz „stan + host”; drugi wiersz pojawia się tylko wtedy, gdy trzeba wyjaśnić problem.
  Gdy wybrana jest zakładka Watchdog, panel szczegółów obiektu okna głównego jest ukryty (wybór Ctrl+D zostaje
  zapamiętany dla list obiektów). Zdarzenia krytyczne mają obwódkę wokół czerwonej kropki, aby odróżnić je od
  wysokich. Panel szczegółów zdarzenia ma 400 px szerokości; rozdzielaczem można go poszerzyć albo zwęzić do 300 px.
  Lista „Zapisane filtry” pojawia się dopiero wtedy, gdy istnieje co najmniej jeden zapisany zestaw. „Zapisane
  filtry”, „Więcej filtrów” i przycisk ✕ „Wyczyść filtry” są zawsze razem na końcu pierwszego wiersza filtrów; w wąskim
  oknie pozostałe filtry przechodzą do kolejnego wiersza.
- **Kolumny list** zdarzeń, reguł i dziennika dostarczeń (podzakładka Stan) mieszczą się w szerokości listy – lista nie
  przewija się w bok, także w domyślnym rozmiarze okna AD Manager (1360 × 840, w którym program się otwiera) przy
  panelu szczegółów zdarzenia o szerokości 400 px. Na liście zdarzeń zawsze widać ważność, czas, zdarzenie i obiekt;
  tytuł zdarzenia zajmuje miejsce, które zostaje po pozostałych kolumnach. Kolumny ✓ (potwierdzone), Wykonawca, Źródło, Lokalizacja i Blokady
  pojawiają się w tej kolejności, gdy lista ma na nie miejsce (szersze okno, węższy panel szczegółów) – ich wartości są
  zawsze w panelu szczegółów zdarzenia. Na liście reguł nazwa, rodzaje zdarzeń i powiadomienia dzielą szerokość w
  proporcji 4 : 3 : 3; skróconą nazwę reguły pokazuje podpowiedź. W dzienniku dostarczeń temat i błąd dzielą miejsce po
  pozostałych kolumnach (3 : 2). Szerokość kolumny zmieniona przez operatora (przeciągnięcie krawędzi nagłówka albo
  dwuklik na niej, który dopasowuje kolumnę do zawartości) zostaje, a pozostałe kolumny od razu robią jej miejsce.
- **Problemy z połączeniem**: pojedyncze żądanie, które przekroczyło czas albo nie dotarło do usługi (np. powolny zapis
  lub wyszukiwanie tekstu), pokazuje błąd w swojej podzakładce, a zakładka zostaje otwarta. Zakładka chowa się za
  banerem „Ponów” dopiero wtedy, gdy zawodzą także aktualizacje na żywo, i wraca sama po następnym udanym odczycie stanu
  (w ciągu 30–60 s), bez klikania „Ponów”. Po nieudanym połączeniu z **innym** adresem usługi trzeba kliknąć „Ponów”.
- „Ponów” lub F5 po problemie z połączeniem **zachowują** niezapisane zmiany konfiguracji i wpisane hasła/sekrety
  administratora, jeśli adres usługi się nie zmienił; zmiana adresu usługi wczytuje konfigurację tamtej usługi.
- Zamknięcie zakładki Watchdog (Ctrl+W, przycisk zamknięcia, środkowy przycisk myszy) albo okna głównego przy
  niezapisanej konfiguracji pyta: „Konfiguracja Watchdog ma niezapisane zmiany (także wpisane hasła i sekrety).
  Odrzucić je?”. Odpowiedź „Nie” pozostawia zakładkę i okno otwarte, a zakładka zostaje wybrana.
- **Lista zdarzeń**: „ustalanie…” jest zastępowane automatycznie (w wierszu i w panelu szczegółów) około 30 s po
  ustaleniu wykonawcy, dopóki zakładka jest widoczna. Przy filtrze po tym, co usługa uzupełnia po zapisaniu zdarzenia
  (priorytet, tagi, status atrybucji, wykonawca, reguła, tekst), zdarzenie, które zaczyna pasować dopiero po atrybucji,
  pojawia się na liście na swoim miejscu w ciągu około 30 s – także wiele naraz (np. operacja masowa) i także wtedy,
  gdy usługa ustala wykonawcę dłużej niż domyślne 30 minut: lista ponownie sprawdza zdarzenia wykryte w czasie, w którym
  usługa może jeszcze ustalać wykonawcę (ostatnia próba z `attribution.pendingRetries` w konfiguracji usługi plus zapas,
  co najmniej 2 godziny wstecz, licząc od ostatniego pełnego sprawdzenia listy – zmiany z czasu, gdy zakładka była
  ukryta albo usługa była nieosiągalna, pojawiają się więc po powrocie; przez ten sam czas po ponownym uruchomieniu
  usługi lista sprawdza także zdarzenia zapisane przed jej zatrzymaniem, bo usługa dopiero wtedy kończy ich ocenę i
  atrybucję). Wiersz, który przestał pasować do
  filtru (np. obniżony priorytet albo inny wykonawca), znika z listy; zaznaczony wiersz zostaje przyciemniony z
  podpowiedzią „Nie pasuje już do filtru” i znika po odznaczeniu. Zdarzenie potwierdzone w innej konsoli pozostaje na
  liście jako potwierdzone (jak potwierdzone przez operatora). Lista na żywo trzyma w pamięci 2000 najnowszych zdarzeń (więcej,
  jeśli operator wczytał starsze strony); starsze są dostępne ponownie przyciskiem „Wczytaj starsze”. Nowe i późno
  dopasowane zdarzenia ani „Wczytaj starsze” nie przewijają listy: gdy operator czyta starsze zdarzenia (lista
  przewinięta w dół), czytane wiersze i zaznaczenie zostają na miejscu, a o nowych informuje przycisk „N nowych”.
  Po odświeżeniu (F5) albo zmianie filtru przewinięta lista pokazuje to samo zdarzenie, jeśli nadal pasuje (inaczej
  zaznaczone, a bez zaznaczenia – najnowsze).

| Podzakładka | Zawartość |
|---|---|
| Zdarzenia | lista na żywo (nowe zdarzenia pojawiają się same), filtry (priorytet, kategoria, rodzaj, obiekt, OU, tagi – np. „uprzywilejowane”, status atrybucji – np. „pokaż nieustalone”, wykonawca, tekst także po stacji/IP, daty, niepotwierdzone) z zapisywanymi zestawami, potwierdzanie wielu zdarzeń naraz; szczegóły: zmiany stara → nowa, wykonawca i źródło z poziomem pewności i dowodami (DC, numer zdarzenia), dla blokad źródła błędnych haseł, powiązane zdarzenia, wysłane powiadomienia; „Potwierdź”, „Pokaż w AD Manager” (bieżące położenie obiektu po GUID; obiekt usunięty – okno Kosza AD), „Odblokuj konto” (przez plan zmian z podglądem), „Kopiuj”, eksport CSV (tylko na polecenie operatora) |
| Reguły | lista reguł, edytor, wyjątki i propozycje wyjątków, podgląd skutków, baner trybu wdrożenia |
| Kanały i SMTP | SMTP, grupy odbiorców, kanały Teams i webhook, wysyłki testowe z diagnostyką |
| Raport dzienny | harmonogram, odbiorcy, podgląd treści, „Wyślij teraz” |
| Stan | połączenie z DC, znacznik USN, zrzut bazowy, kontrola uprawnień, dzienniki każdego DC (pokrycie atrybucji, liczba i czas zapytań o źródło), kanały, baza danych i kopie, dziennik wysyłek (ponawianie), historia zmian konfiguracji; każdy zgłaszany kod stanu ma opis i zalecenie – m.in. wygasający certyfikat TLS i jego klucz prywatny, rozmiar i konserwacja bazy, planowanie powiadomień, „Nieistniejące DN w konfiguracji”, kolektor WEF, wykrywanie kontrolerów domeny, źródło atrybucji, odmowa wyszukiwania zmian w katalogu (`permission.search`) |

Rola Viewer widzi wszystko tylko do odczytu (bez wyjątków, adresów odbiorców i danych serwera SMTP); edycja wymaga
roli Admin.

---

## 7. Dane, retencja i kopie zapasowe

| Dane | Gdzie | Domyślna retencja |
|---|---|---|
| Migawka obserwowanych atrybutów (bez sekretów) i członkostw | `data\watchdog.db` | bieżący stan; obiekty usunięte 180 dni |
| Zdarzenia (zmiany, atrybucja, potwierdzenia) | `data\watchdog.db` | 365 dni |
| Znormalizowane zdarzenia z dzienników DC (tylko pola potrzebne do dopasowania; wartości wyłącznie obserwowanych, nietajnych atrybutów) | `data\watchdog.db` | 30 dni; najstarsze usuwane wcześniej, gdy baza przekroczy limit rozmiaru (domyślnie 10 GB) |
| Kolejka wysyłek (treść wiadomości) | `data\watchdog.db` | treść usuwana zaraz po wysłaniu; nieudane 90 dni; metadane wysłanych 30 dni |
| Konfiguracja i historia jej zmian | `data\watchdog.db` | konfiguracja bieżąca; historia 3 lata |
| Sekrety kanałów (hasło SMTP, sekret OAuth, adresy Teams/webhook, klucze podpisu) | `data\watchdog.db`, zaszyfrowane Data Protection i powiązane z miejscem docelowym; klucze w `data\keys\` chronione DPAPI-NG dla gMSA | do usunięcia |
| Kopie bazy | `data\backup\` (codziennie 3:15, 7 ostatnich; kopie sprzed migracji – najnowsza + 30 dni) | 7 kopii |

Retencję i limit rozmiaru ustawia się w konfiguracji. Usuwane rekordy są nadpisywane w pliku bazy (`secure_delete`).
Do odtworzenia potrzebne są **baza i folder `keys\` oraz to samo konto gMSA** – po usunięciu i ponownym utworzeniu
gMSA sekrety trzeba wpisać ponownie. Kopie zapasowe folderu mają te same ACL co oryginał i powinny być szyfrowane
przez system kopii. Rozmiar: ok. 0,6 KB na zdarzenie (1 000 zdarzeń dziennie ≈ 230 MB rocznie) i ok. 0,6 KB na obiekt
migawki.

Codzienna kopia (3:15, `VACUUM INTO`) i kompaktowanie bazy **nie wstrzymują** wykrywania zmian ani wysyłania
alarmów. Stan bazy pokazują dwa sprawdzenia o jednej definicji (stan nie zmienia się już co godzinę):

- „Rozmiar bazy danych” (`storage.size`) – plik bazy razem z plikiem WAL względem `Retention.MaxDatabaseMb`:
  ostrzeżenie od 80 % limitu, błąd powyżej limitu,
- „Wolne miejsce na dysku” (`storage.free`) – błąd, gdy na woluminie folderu danych zostaje mniej niż 1 GiB.

Nieudana retencja lub konserwacja bazy daje stan „Konserwacja bazy danych” (`storage.maintenance`).

---

## 8. Bezpieczeństwo

- **Dane osobowe (RODO).** Folder danych i powiadomienia zawierają dane pracowników (nazwiska, działy, zmiany kont).
  Uzgodnij z IOD cele, okres retencji, krąg odbiorców i kanały (Teams i Power Automate przechowują kopie poza
  folderem – historia przepływu 30 dni). Wpisz przetwarzanie do rejestru czynności. Obowiązki z ustawy o KSC
  (NIS2) dotyczące monitorowania i dowodów incydentów mogą przemawiać za dłuższą retencją zdarzeń. Odczyt
  pojedynczych zdarzeń w API jest rejestrowany (kto, numer zdarzenia) w dzienniku Application – zdarzenia 2600
  (szczegóły w zakładce) i 2601 (strona zdarzenia z linku w powiadomieniu). Każde otwarcie szczegółów i każda
  otwarta strona zdarzenia to jeden wpis – dobierz rozmiar dziennika Application albo przekazuj go do SIEM.
- **Zero sekretów.** Wartości haseł, LAPS, BitLockera, TPM, gMSA, sekretów LSA (`currentValue`, `priorValue`) i
  atrybutów oznaczonych w schemacie jako poufne nie są odczytywane, zapisywane ani wysyłane.
- **Serwer Watchdog to zasób Tier 0** (rozdział 3). Konto usługi ma tylko odczyt AD i dzienników; nie ma uprawnień
  zapisu ani replikacji. Jako gMSA nie ma hasła do wycieku.
- **Dostęp do API**: HTTPS (TLS 1.2/1.3), wyłącznie Kerberos (NTLM odrzucany także w trybie HTTP.sys), role wg SID grup
  AD z lokalnego `config\appsettings.json` (konto usługi nie może go zmienić), lista natychmiastowych odmów
  (`DeniedSids`), dodatkowy nagłówek wymagany przy zmianach (ochrona przed CSRF z przeglądarki), zapora ograniczona do
  podsieci administratorów i monitoringu, bez serwerów pośredniczących.
- **Sekrety kanałów** są tylko do zapisu i powiązane z miejscem docelowym: API i zakładka nigdy ich nie zwracają,
  administrator Watchdoga nie może przekierować zapisanego hasła lub tokenu OAuth na inny serwer (także przez zmianę
  weryfikacji certyfikatu SMTP – przypięcie innego certyfikatu wymaga ponownego wpisania hasła); w zakładce i w
  dziennikach usługi widać z adresów kanałów wyłącznie `schemat://host/…` (bez ścieżki i parametrów).
- **Dzienniki DC** są czytane wyłącznie przez Kerberos (bez NTLM) i tylko z kontrolerów i kolektora podanych jako nazwy
  DNS (4.4).
- **Zmiany konfiguracji** są audytowane i zgłaszane (także dotychczasowym odbiorcom); krytycznych detekcji nie da się
  wyłączyć przez API bez lokalnej zgody.
- **Dzienniki diagnostyczne** usługi (dziennik Application, konsola) nie zawierają wartości z katalogu ani treści
  wyjątków – testy automatyczne to sprawdzają. Zrzuty awaryjne WER są wyłączone dla usługi.
- **Treści z AD są niezaufane**: w e-mailach kodowane HTML i „rozbrajane” (5.6), w kartach Teams wyświetlane jako
  zwykły tekst (bez linków Markdown), w webhookach jako JSON. Nazwy komputerów podane przez klienta lub z DNS są
  oznaczane i nigdy nie decydują o regułach.
- **Źródła zdarzeń** są sprawdzane: przyjmowane są tylko rekordy dostawcy „Microsoft-Windows-Security-Auditing” z
  kontrolerów znanych z AD.
- **Wyjątki od reguł** są audytowane i widoczne w każdym raporcie dziennym; wyjątki dla reguł krytycznych są
  oznaczane ostrzeżeniem.
- **Monitoring usługi**: `/healthz` dla systemu monitorowania, zdarzenia dziennika Application (rozdział 10),
  zdarzenia SCM 7036/7040 usługi `AdMngWatchdog` przekazywane do SIEM, opcjonalny heartbeat webhooka.

---

## 9. Ograniczenia

- Jedna instancja usługi monitoruje jedną domenę (w lesie z wieloma domenami – instancja na domenę); kontrolery
  innych domen pojawiają się tylko jako „nowy / usunięty kontroler” (priorytet wysoki).
- Zmiany wykonane na DC w innych lokacjach są widoczne po replikacji międzylokacyjnej (domyślnie do 180 min).
- Wygaśnięcie blokady po upływie czasu blokady niczego nie zapisuje w AD, a przy pierwszym udanym logowaniu DC zeruje
  `lockoutTime` – Watchdog nie traktuje tego jako odblokowania (opcjonalne zdarzenie „wygaśnięcie blokady” wylicza
  sam). Czas blokady uwzględnia PSO, jeśli konto usługi może je odczytać (4.3).
- Atrybucja wymaga zasad inspekcji, SACL i dostępu do dzienników; bez nich wykonawca jest „nieustalony” (z podaną
  przyczyną). Zmiany wykonane przez ADWS (Centrum administracyjne AD, PowerShell) lub zdalną sesję na DC mogą wskazywać
  jako źródło sam kontroler („lokalnie na DC”). Sesje logowania starsze niż 12 h nie są wyszukiwane (przyczyna
  „SOURCE_LOGON_NOT_FOUND”).
- `lastLogonTimestamp` jest aktualizowany z opóźnieniem 9–14 dni – próg nieaktywności nie powinien być krótszy niż
  30 dni.
- Bez prawa odczytu `CN=Deleted Objects` usunięcia są wykrywane dopiero przy dobowym uzgodnieniu (jako „obiekt
  zniknął”).
- Członkostwa czasowe (PAM, TTL) wygasają bez śladu w AD – ich koniec jest wykrywany przy dobowym uzgodnieniu.
- Zmiany GPO są wykrywane na poziomie obiektu GPO (wersja, powiązania), bez porównywania ustawień.
- Usługa niczego nie blokuje ani nie cofa – tylko raportuje.
- Laboratorium Samba nie ma dzienników zabezpieczeń Windows: atrybucję testowano dotąd na zarejestrowanych zdarzeniach;
  przebieg na laboratorium Windows Server jest jeszcze przed nami (wersja zapoznawcza 0.2.0 go nie przeszła).
- Tryb HTTP.sys nie został jeszcze sprawdzony na laboratorium Windows: nie wiadomo, czy każde logowanie dociera do
  usługi jako Kerberos (obejście – 4.10).
- W trybie WEF po zalewie zdarzeń (więcej niż `maxRecordsPerPoll` w jednym odczycie) zdarzenia dostarczone z
  opóźnieniem w trakcie zalewu mogą nie zostać uwzględnione w atrybucji (4.4).
- Zmiana `AllowedRemoteSubnets`/`MonitoringSubnets` w `appsettings.json` nie aktualizuje reguły zapory Windows, a
  zmiana grup ról działa na obserwację grup uprzywilejowanych dopiero po restarcie usługi (4.10).

---

## 10. Rozwiązywanie problemów

| Objaw | Przyczyna i rozwiązanie |
|---|---|
| Zakładka: „Usługa odrzuciła uwierzytelnienie Kerberos” (401) | SPN `HTTP/<nazwa>` nie jest na gMSA lub klient używa aliasu CNAME/adresu IP (wtedy Windows próbuje NTLM, który jest odrzucany). Sprawdź `setspn -Q HTTP/admng-wd.contoso.com`, używaj nazwy z rekordu A; po zmianach `klist purge` na stacji. |
| „Brak uprawnień” (403) | Konto nie należy do grup z `Authorization` w `config\appsettings.json`, jest na liście `DeniedSids` albo bilet Kerberos jest sprzed dodania do grupy (wyloguj się lub `klist purge`). |
| „Usługa nieosiągalna” | Usługa zatrzymana, zapora (port 8443, `AllowedRemoteSubnets`), DNS. Na serwerze: `& "C:\Program Files\AdMng\Watchdog\AdMng.Watchdog.exe" --health` (kod 0 = działa) albo `Invoke-WebRequest https://admng-wd.contoso.com:8443/healthz -UseBasicParsing` (nazwa z certyfikatu, nie `localhost`). |
| Błąd certyfikatu | Certyfikat nie pochodzi z zaufanego CA lub nazwa nie zgadza się z adresem. |
| Usługa nie startuje (dziennik Application, źródło „AdMng Watchdog”, zdarzenie 1010) | Kod wyjścia 2 – błędny `config\appsettings.json` (składnia JSON, nazwa lub wartość ustawienia; szczegóły: `AdMng.Watchdog.exe --check-config`); 3 – baza w nowszej wersji niż program; 4 – brak odczytu klucza prywatnego certyfikatu albo `http://` na adresie innym niż lokalny; 5 – zbyt szerokie uprawnienia folderu `data\`, plików bazy albo folderów `data\keys\` lub `data\backup\` (obce konto – uruchom ponownie skrypt instalacyjny, który przywraca uprawnienia, 4.8); 6 – konto usługi może zapisywać `config\`; 7 – tryb deweloperski uruchomiony jako usługa; brak prawa „Logowanie w trybie usługi”. |
| Ostrzeżenie 2002 w dzienniku Application po edycji `config\appsettings.json` | Plik ma błąd składni JSON albo niepoprawną wartość (np. `"Minimalny"` zamiast `"Minimal"`, `"tak"` zamiast `true`). Usługa działa dalej na poprzednich ustawieniach – zmiana **nie** obowiązuje, a przy następnym starcie usługa się zatrzyma (kod 2). Popraw plik i sprawdź go `AdMng.Watchdog.exe --check-config`. |
| Instalacja/aktualizacja: skrypt zatrzymuje się i wymienia konta, które mogły zmienić `config\appsettings.json` | Właścicielem pliku lub folderu nad nim albo kontem z prawem zapisu jest ktoś inny niż SYSTEM i Administratorzy. Przejrzyj plik, usuń go albo uruchom skrypt z `-TrustExistingConfig` (4.9). |
| Tryb HTTP.sys: każde logowanie odrzucone zdarzeniem 2500 „unknown (Negotiate)” | Użyj Kestrel ze SPN-em na gMSA albo tymczasowo `RequireKerberos: false` (NTLM jest wtedy przyjmowany) – 4.10. |
| Stan: „Kolektor zdarzeń (WEF)” – błąd; zapis konfiguracji odrzucany | Kolektor lub kontroler w ustawieniach atrybucji podano jako adres IP (albo `localhost`, nazwę bez SPN). Wpisz nazwę DNS (FQDN) – usługa łączy się wyłącznie przez Kerberos. Dopóki zapisany jest adres IP, każdy zapis konfiguracji jest odrzucany. |
| Stan: „Nieistniejące DN w konfiguracji” | OU lub grupę wskazaną w konfiguracji przemianowano, przeniesiono albo usunięto – popraw wymienione DN (reguły, grupy uprzywilejowane, poddrzewa, wyjątki). |
| Stan: „permission.uSNChanged” | Konto nie widzi części obiektów – nadaj uprawnienia z rozdziału 4.3 (także na AdminSDHolder). |
| Stan: „eventlog.<DC>” z kodem 1722 / 5 | 1722 – zapora „Remote Event Log Management” na DC (także ograniczenie adresu); 5 – gMSA nie jest w Event Log Readers (lub usługa nie została zrestartowana po dodaniu). |
| Wykonawca „nieustalony – AUDIT_POLICY_MISSING / SACL_MISSING / LOG_OVERWRITTEN / DC_UNREACHABLE” | Odpowiednio: zasady inspekcji (4.5), SACL (4.6), rozmiar dziennika (4.4) albo wyczyszczony dziennik Security (1102/1104 lub numeracja rekordów od nowa – zdarzenia sprzed wyczyszczenia nie są już dostępne), dostęp do dziennika DC. |
| Źródło „SOURCE_LOGON_NOT_FOUND / SOURCE_NOT_COLLECTED” | Sesja logowania starsza niż 12 h lub dziennik nadpisany; w trybie WEF brak 4624/4771/4776 w subskrypcji (4.4). |
| Zapis konfiguracji: „wpisz hasło/sekret ponownie” (`secretRebindRequired`) | Zmieniono serwer, port, użytkownika, dzierżawę, adres kanału albo weryfikację certyfikatu serwera SMTP (przypięty odcisk, tryb, sprawdzanie odwołań) – zapisany sekret jest powiązany ze starym miejscem docelowym. Po aktualizacji usługi także hasło SMTP / sekret XOAUTH2 zapisany przy niestandardowej weryfikacji certyfikatu (4.9): do jego ponownego wpisania e-mail nie jest wysyłany. |
| Dziennik wysyłek: „Niedostarczone” z kodem `partial.smtp.4xx…` | Jeden z odbiorców przez ok. 22 h odrzucał wiadomość tymczasowo (4xx); pozostali ją dostali. Ponowienie (rola Admin, do 14 dni) wyśle ją tylko do brakujących odbiorców (5.2). |
| Dziennik wysyłek: „Niedostarczone” z kodem `channel.disabled` / `channel.unknown` / `smtp.disabled` | Kanał wyłączono lub usunięto albo wyłączono SMTP, gdy wiadomość czekała w kolejce. Ponowne włączenie kanału jej nie dostarczy – ponów ręcznie (5.7). |
| E-mail: `535 5.7.139` / `550 5.7.30` | Exchange Online: SMTP AUTH wyłączone dla dzierżawy/skrzynki – przejdź na OAuth2 lub `Set-CASMailbox -SmtpClientAuthenticationDisabled $false`. |
| E-mail: `550 5.7.68` | Dzierżawa odrzuca „Direct Send” – użyj łącznika przychodzącego lub logowania. |
| Teams: „przestarzały adres” | Adres `*.webhook.office.com` – utwórz Workflow (5.3). |
| Teams: 401/403 z tekstem o „Shared Access scheme” lub „OAuth” | Niezgodny tryb uwierzytelniania wyzwalacza (SAS vs Entra) – zmień tryb kanału albo ustawienie przepływu. |
| Brak raportu dziennego | Usługa nie działa, SMTP niesprawne (Stan → Kanały) lub raport wyłączony. |

Zdarzenia dziennika Application (źródło „AdMng Watchdog”) do monitoringu (SCOM, Zabbix, SIEM): 1000 start usługi,
1001 brak połączenia z DC / przełączenie na inny DC, 1002 nieudane wysyłki, 1003 problem z magazynem (miejsce, limit,
uprawnienia folderu), 1004 braki uprawnień, 1005 problemy z atrybucją / luka w dzienniku DC, 1006 zmiana konfiguracji,
1007 przestarzałe środowisko .NET / wygasający certyfikat / niedostępny klucz odnowionego certyfikatu (ostrzeżenie,
dopóki działa poprzedni certyfikat; błąd, gdy ten wygaśnie), 1010 nieudana kontrola startowa, **2002 nieprzyjęta
edycja `config\appsettings.json`** (usługa działa na poprzednich ustawieniach), 2202/2203 odbiorcy e-maila odrzuceni
tymczasowo / trwale (częściowe doręczenie), 2300 ostrzeżenie: zadanie usługi zawodzi (wpis przy pierwszym błędzie lub
zmianie jego kodu, potem najwyżej co godzinę), 2301 zadanie znów działa; do rozliczalności (RODO) 2600/2601 odczyt
pojedynczego zdarzenia (kto, numer zdarzenia).

Identyczne ostrzeżenia i błędy są zapisywane najwyżej raz na 15 minut; następny wpis kończy się dopiskiem „(repeated N
times since the previous entry)”. Reguły SIEM, które liczą wystąpienia, muszą to uwzględniać. Ostrzeżenie 2002 nigdy
nie jest w ten sposób ograniczane.

---

## 11. Środowisko testowe

Usługę można uruchomić na Linuksie przeciw laboratorium Samba AD DC z `tools/testlab` – skrypty i opis:
[`tools/testlab/watchdog/README.md`](../tools/testlab/watchdog/README.md).

```bash
tools/testlab/watchdog/run-watchdog-lab.sh --reset --background   # usługa (profil Lab), Mailpit, odbiornik webhooków
tools/testlab/watchdog/configure-lab.sh --test                    # SMTP → Mailpit, kanały → odbiornik (przez API)
tools/testlab/watchdog/lab-scenario.sh --verify                   # zmiany w OU=WatchdogDemo i sprawdzenie powiadomień
tools/testlab/watchdog/run-watchdog-lab.sh --stop
```

- Profil `Lab` (`appsettings.Lab.json`): uwierzytelnianie `Development` (tylko pętla zwrotna, nigdy jako usługa
  Windows), API `http://127.0.0.1:5089`, odczyt katalogu jako `odczyt.test` prostym wiązaniem bez TLS
  (`AllowInsecureLab` – odrzucane poza środowiskiem Development); hasło wyłącznie w zmiennej
  `ADMNG_WATCHDOG_LDAP_PASSWORD`, dane w `~/.local/share/admng-watchdog-lab`. Na Linuksie klucze Data Protection nie są
  szyfrowane (ostrzeżenie przy starcie), a atrybucja jest wyłączona (`SOURCE_DISABLED`) – laboratorium Samba nie ma
  dzienników zabezpieczeń Windows.
- Mailpit (http://127.0.0.1:8025) zbiera e-maile (STARTTLS, certyfikat przypinany przez `configure-lab.sh`), odbiornik
  webhooków sprawdza podpisy Standard Webhooks i koperty Teams Workflows.
- Konfiguracja laboratoryjna celowo skraca okna agregacji i kończy tryb wdrożenia; ustawienia produkcyjne opisują
  rozdziały 4 i 5.
- Konto `odczyt.test` nie widzi `CN=Deleted Objects`, więc usunięcia w laboratorium pojawiają się dopiero przy
  uzgodnieniu dobowym (stan `permission.deletedObjects`) – zgodnie z rozdziałem 4.3.

Testy integracyjne (`tests/AdMng.Watchdog.IntegrationTests`) uruchamiają prawdziwe silniki i całą usługę przeciw
laboratorium:

```bash
tools/testlab/watchdog/start-mailpit.sh
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
export ADMNG_TEST_LDAP_READONLY_USER='odczyt.test@contoso.test' ADMNG_TEST_LDAP_READONLY_PASSWORD='Passw0rd!Lab2026'
export ADMNG_TEST_SMTP_HOST=127.0.0.1 ADMNG_TEST_SMTP_PORT=1025 ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025
dotnet test tests/AdMng.Watchdog.IntegrationTests
# opcjonalnie prawdziwy Kerberos (SPN HTTP/localhost, keytab): eval "$(sudo -E tools/testlab/watchdog/krb5-lab.sh)"
# i ADMNG_TEST_KERBEROS=1 – na Linuksie sprawdzane jest tylko uwierzytelnienie (role po SID-ach: testy na Windows)
```

CI (`.github/workflows/ci.yml`): na Windows wszystkie projekty testów (także testy tylko-Windows: czytnik dziennika
Security, DPAPI-NG), publikacja usługi, `AdMng.Watchdog.exe --smoke-test` i artefakt **AdMng-Watchdog-win-x64**
(14 dni); na Ubuntu z Samba DC – testy silnika i usługi, testy integracyjne z Mailpit oraz skrypty laboratorium
(`lab-scenario.sh --verify`). Zrzuty ekranu zakładki Watchdog trafiają na gałąź `screenshots`.
