# AdMng Watchdog – monitor zmian w Active Directory

AdMng Watchdog to osobna usługa, która przez całą dobę obserwuje domenę Active Directory, zapisuje historię zmian
(kto, co, kiedy, skąd, było → jest – domyślnie 3 lata) w swoim własnym folderze danych i powiadamia administratorów
(e-mail, raport dzienny, Microsoft Teams, webhook). Od wersji **0.3.0** działa wyłącznie na Linuksie – jako usługa
systemd `admng-watchdog` na serwerze **Debian 12 lub 13** poza domeną, z kontem komputera `ADMNG-WD$` i keytabem,
z odbiorem zdarzeń kontrolerów przez Windows Event Forwarding i OpenWEC. W aplikacji AD Manager (AD_MNG) usługa ma
własną zakładkę **Watchdog**: podgląd zdarzeń, reguły powiadomień, konfigurację SMTP i kanałów oraz stan usługi.

Ten dokument opisuje funkcje. Wdrożenie krok po kroku (AD, kontrolery, serwer Debian, pakiety, pilotaż, wycofanie):
[WATCHDOG-LINUX.md](WATCHDOG-LINUX.md). Usługa Windows (gMSA, skrypt PowerShell) była dostępna w wersji 0.2.0 – jej
instrukcja jest w [WATCHDOG.md z wersji 0.2.0](https://github.com/DarienRahl/AD_MNG/blob/v0.2.0/docs/WATCHDOG.md).

Decyzje o przechowywaniu danych przez usługę i o przejściu na Linuksa opisują
[ADR-0008](adr/0008-watchdog-dane-w-osobnym-folderze.md) i [ADR-0009](adr/0009-watchdog-na-linuksie.md). Specyfikacje
implementacyjne (dla programistów, po angielsku): [dev/watchdog-spec.md](dev/watchdog-spec.md) i
[dev/watchdog-linux-design.md](dev/watchdog-linux-design.md).

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
| Treść GPO (od 0.3.0) | zmiany ustawień GPO jako **„ustawienie: było → jest”** (szablon zabezpieczeń – prawa użytkowników, grupy z ograniczeniami, zasady kont; `Registry.pol`; preferencje GPP – np. zaplanowane zadania i konta lokalne; skrypty z ich skrótem SHA-256; zasady inspekcji; komentarze), przekierowanie folderu GPO (`gPCFileSysPath`) na inny serwer, zmiana plików w SYSVOL z pominięciem GPMC (dobowe uzgodnienie), obecność haseł GPP (`cpassword`, MS14-025 – wartość nigdy nie jest zapisywana) | ustawienia wrażliwe – wysoki (GPO połączone z domeną lub OU kontrolerów – krytyczny), pozostałe – niski; przekierowanie i `cpassword` – krytyczny | e-mail natychmiast (reguła `tier0-gpo`) |
| Nowe reguły Tier 0 (od 0.3.0) | utworzenie konta dMSA i każda zmiana powiązania migracji (**BadSuccessor**, Windows Server 2025), podniesienie `ms-DS-MachineAccountQuota` z 0, każda zmiana konta usługi `ADMNG-WD$` poza jego zaplanowaną zmianą hasła (`watchdog-self`), zmiana samej listy SACL (inspekcji) domeny, AdminSDHolder, OU kontrolerów, `CN=Public Key Services` i `CN=Sites` | krytyczny (inna zmiana limitu kont komputerów – wysoki) | e-mail natychmiast, całą dobę (reguły `tier0-badsuccessor`, `tier0-machine-quota`, `watchdog-self`, `tier0-security`) |
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

```text
serwer Debian 12/13 poza domeną (Tier 0), konto komputera CONTOSO\ADMNG-WD$ z keytabem (hasło zmieniane co 30 dni)
├─ admng-watchdog.service (systemd, konto admng-watchdog)
│   ├─► DC (PDC): LDAP 389, Kerberos + podpis + szyfrowanie (SASL GSSAPI) – co 15 s highestCommittedUSN, tylko zmienione
│   │   obiekty (uSNChanged), porównanie z migawką
│   ├─► DC (PDC): SMB 3 z podpisywaniem i Kerberosem – odczyt GPO z SYSVOL (tylko odczyt)
│   ├── czyta pliki OpenWEC: 47xx, 5136–5141, 4624/4625/4771/4776 → kto i skąd
│   ├── reguły → kolejka powiadomień → SMTP / Teams / webhook (ponawianie, limity)
│   ├── /var/lib/admng-watchdog (SQLite, klucze sekretów, kopie) · /etc/admng-watchdog (ustawienia – tylko root)
│   └── API HTTPS 8443 (Kerberos, role z grup AD), /healthz dla monitoringu
├─ openwecd.service (OpenWEC, konto openwec) ◄── WEF z każdego DC: HTTP 5985 + Kerberos (wiadomości szyfrowane)
│   └── /var/lib/openwec/out/<DC>/security.jsonl (pliki przejściowe, usuwane po odczycie)
└─ timery: zmiana hasła konta usługi (codziennie, gdy hasło ma > 30 dni), porządkowanie plików OpenWEC (co godzinę)
                                   ▲ HTTPS + Kerberos
              AD Manager → Narzędzia → Watchdog (Ctrl+Shift+A)
```

1. **Wykrywanie (LDAP).** Usługa jest „przypięta” do jednego kontrolera (domyślnie emulatora PDC – tam trafiają
   blokady kont). Co 15 sekund odczytuje z RootDSE licznik `highestCommittedUSN`. Gdy się zmienił, pobiera tylko
   obiekty o `uSNChanged` większym niż ostatnio przetworzony i porównuje obserwowane atrybuty z migawką zapisaną w
   folderze danych. Członków grup i przełożonych porównuje po GUID obiektów, więc przeniesienie lub zmiana nazwy
   konta nie wygląda jak zmiana członkostwa. Usunięte obiekty widzi w kontenerze `Deleted Objects`, a raz na dobę
   (domyślnie 02:30) wykonuje pełne uzgodnienie. Postęp zapamiętuje osobno dla każdego DC, więc powrót na PDC po
   awarii lub przeniesieniu roli nie wymaga ponownego pełnego skanu.
2. **Atrybucja (dzienniki DC).** Kontrolery same wysyłają wybrane zdarzenia dziennika Security mechanizmem Windows
   Event Forwarding (bez agentów) do odbiornika OpenWEC na serwerze Watchdog; usługa czyta jego pliki na bieżąco,
   sprawdza nadawcę (zapisywalny DC tej domeny, subskrypcja, adres) i przechowuje znormalizowane rekordy (związane ze
   zmianami – 30 dni, logowania – 48 godzin). Z metadanych replikacji usługa wie, na którym DC i kiedy powstała
   zmiana, i dopasowuje zdarzenie tego kontrolera (np. 4738 dla konta, 4728 dla grupy, 5136 dla atrybutu). Komputer
   źródłowy ustala z logowania 4624 tej samej sesji (logowania sieciowe – typ 3 – i RDP – typ 10; najpierw adres IP,
   potem nazwa stacji), a dla blokad – z pola „Caller Computer Name” zdarzenia 4740 oraz zdarzeń 4771/4776/4625
   (adresy, z których szły błędne hasła, z liczbą prób). Ustalone źródło jest kopiowane do zdarzenia i zostaje z nim
   na cały okres przechowywania historii. Dziennik bywa opóźniony – wtedy zdarzenie ma status „ustalanie…”, a po dopasowaniu jest
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
   - Usługa łączy się z DC **wyłącznie przez Kerberos** (bez NTLM), dlatego kontrolery są podane jako nazwy DNS
     (`Directory.Servers`); OpenWEC przyjmuje od kontrolerów tylko uwierzytelnienie Kerberos.
3. **Treść GPO (SYSVOL).** Po zmianie wersji GPO (`versionNumber`) albo jego ścieżki usługa czyta folder GPO z SYSVOL
   swojego kontrolera (SMB 3, tylko odczyt), rozbiera pliki ustawień i porównuje je z poprzednią migawką – zdarzenie
   pokazuje „ustawienie: było → jest”, a wykonawcę dziedziczy ze zmiany obiektu GPO (5136). Dobowe uzgodnienie
   znajduje zmiany plików wykonane z pominięciem GPMC (bez zmiany wersji; wykonawca „nieustalony –
   SYSVOL_NOT_AUDITED”). Folder GPO wskazujący inny serwer nie jest w ogóle czytany – to zdarzenie krytyczne.
4. **Powiadomienia.** Reguły decydują, które zdarzenia i komu są wysyłane: natychmiast (zbiorczo w krótkim oknie,
   z limitem wiadomości na godzinę i z uwzględnieniem godzin pracy i polskich świąt), w raporcie dziennym albo do
   Teams / webhooka. Wysyłki czekają w trwałej kolejce i są ponawiane po błędach.

---

## 3. Wymagania

- **Serwer:** maszyna wirtualna z **Debianem 13** (zalecany) albo 12, amd64, 2 vCPU, 4 GB RAM, dysk 60 GB (co najmniej
  40 GB na `/var` – historia z 3 lat i kopie bazy), opcjonalnie vTPM; **niedołączona do domeny**, na platformie
  wirtualizacji Tier 0. **ASP.NET Core Runtime 10 w wersji co najmniej 10.0.13** z repozytorium packages.microsoft.com
  (aktualizacje automatyczne). Pakiety `admng-watchdog` i `admng-openwec` z wydania.
- Serwer, jego kopie zapasowe, keytab i konto usługi traktuj jako zasoby **Tier 0** (jak kontrolery domeny): kto ma
  roota na serwerze, ma tożsamość konta usługi, historię zmian i może wyciszyć alarmy. Administruj nim tylko kluczami
  SSH ze stacji PAW.
- **Konto usługi:** obiekt komputera `ADMNG-WD$` utworzony przez administratora Tier 0 (tylko AES, „nie może być
  delegowane”, w OU Tier 0), z prawami tylko do odczytu; hasło zmienia narzędzie `admng-watchdog-keytab` na serwerze.
  Warunki wdrożenia w produkcji: `ms-DS-MachineAccountQuota` = 0 i wpis odmowy zapisu dla SELF na obiekcie konta. Nie
  jest potrzebne prawo „Replicating Directory Changes”.
- Rekord DNS typu **A** dla usługi (np. `admng-wd.contoso.com`) – nie CNAME – i PTR; czas serwera z kontrolerów (chrony).
- **Certyfikat TLS:** z firmowego CA albo generowany przez pakiet (z podpisem własnym – AD Manager 0.3.0 prosi wtedy
  o potwierdzenie odcisku przy pierwszym połączeniu).
- **Kontrolery domeny:** Windows Server 2016 lub nowszy (pilotaż – 2025 i 2022), Kerberos AES (RC4 jest wyłączane na DC
  w 2026 r.); dla atrybucji – zasady inspekcji, SACL i GPO Windows Event Forwarding (bez agentów na DC).
- Dla e-maili: serwer SMTP (Exchange Online, Exchange w firmie albo przekaźnik) z TLS; Teams – opcjonalnie.
- Stacje administratorów z **AD Manager 0.3.0** w podsieci, z której zapora serwera przepuszcza port 8443/TCP; przed
  usługą nie może stać serwer pośredniczący ani urządzenie inspekcji TLS.

Szczegóły (porty, uprawnienia, inspekcja, kolejność kroków): [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md), rozdziały 0–3.

---

## 4. Wdrożenie krok po kroku

Wdrożenie wersji 0.3.0 opisuje [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md):

| Etap | Rozdział |
|---|---|
| Domena testowa (laboratorium z kontrolerami Windows Server 2025 i 2022) | 1 |
| Przygotowanie AD: grupy ról, DNS, konto `ADMNG-WD$`, prawa odczytu, SYSVOL, `ms-DS-MachineAccountQuota`, BadSuccessor, inspekcja, SACL, GPO WEF | 2 |
| Serwer Debian: system, .NET, pakiety, SSH, zapora, DNS i czas, Kerberos, keytab, zmiana hasła co 30 dni, certyfikat, kopie | 3 |
| OpenWEC – odbiór zdarzeń z kontrolerów | 4 |
| Usługa: ustawienia startowe (`/etc/admng-watchdog/appsettings.json`), pierwsze uruchomienie, kody wyjścia, kontrole stanu | 5 |
| AD Manager: połączenie, potwierdzenie certyfikatu, pierwsze kroki | 6 |
| Pilotaż – lista kontrolna | 7 |
| Wycofanie, rozwiązywanie problemów | 8, 9 |

Usługa Windows (gMSA, `Install-AdMngWatchdog.ps1`, HTTP.sys, DPAPI-NG) jest dostępna tylko jako wydanie 0.2.0 – jej
instrukcja: [WATCHDOG.md, rozdział 4 w wersji 0.2.0](https://github.com/DarienRahl/AD_MNG/blob/v0.2.0/docs/WATCHDOG.md#4-wdrożenie-krok-po-kroku).
Po wydaniu 0.3.0 jej kod zostanie usunięty. Przejście z 0.2.0 na Linuksa z zachowaniem historii (kopia bazy
`watchdog.db`, bez folderu `keys\` – hasło SMTP i adres Teams wpisuje się ponownie): WATCHDOG-LINUX.md, ramka
„Migracja z wersji 0.2.0” w rozdziale 0.4.

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
uprzywilejowanymi, a także wyłączenie albo zawężenie reguł `tier0-badsuccessor`, `tier0-machine-quota`,
`watchdog-self` i `tier0-gpo` jest niemożliwe bez lokalnej zgody (`AllowTier0Suppression` w ustawieniach startowych
`/etc/admng-watchdog/appsettings.json`, które zmienia tylko root serwera – [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 5.2).
Tak samo retencji zdarzeń nie da się skrócić poniżej progu `MinEventsDays` (domyślnie 1095 dni), a każde skrócenie
retencji jest zdarzeniem krytycznym.

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
   Exchange Online → SMTP.SendAsApp**, zgoda administratora; dodaj **sekret klienta** (usługa na Linuksie w wersji
   0.3.0 obsługuje tylko sekret – certyfikat z magazynu `LocalMachine\My` działał wyłącznie w usłudze Windows 0.2.0;
   sekret ma ograniczoną ważność, zapisz w kalendarzu termin jego wymiany).
2. Nadawcą niech będzie **skrzynka współdzielona z zablokowanym logowaniem** (bez hasła, którym dałoby się zalogować).
   Exchange Online PowerShell:
   ```powershell
   New-ServicePrincipal -AppId <Application (client) ID> -ObjectId <Object ID z „Aplikacje dla przedsiębiorstw”>
   Add-MailboxPermission -Identity watchdog@contoso.com -User <ServicePrincipal Id> -AccessRights FullAccess
   Set-CASMailbox -Identity watchdog@contoso.com -SmtpClientAuthenticationDisabled $false
   ```
   (Object ID musi pochodzić z „Aplikacje dla przedsiębiorstw”, nie z „Rejestracje aplikacji”.)
3. W zakładce: chmura, identyfikator dzierżawy, identyfikator aplikacji, sekret klienta, nadawca
   `AD Watchdog <watchdog@contoso.com>`. Token OAuth jest wysyłany tylko do serwerów z listy `OAuthSmtpHosts`.

Hasła i sekrety są **powiązane z miejscem docelowym**: po zmianie serwera, portu, użytkownika, dzierżawy lub adresu
kanału trzeba wpisać hasło/sekret ponownie – zapisany sekret nigdy nie zostanie wysłany pod nowy adres (także przy
wiadomości testowej). Hasło SMTP i sekret klienta XOAUTH2 są powiązane także z weryfikacją certyfikatu serwera SMTP:
każda zmiana przypiętego odcisku, trybu weryfikacji lub sprawdzania odwołań wymaga ponownego wpisania hasła/sekretu,
również przed wiadomością testową (inaczej administrator Watchdoga mógłby przypiąć certyfikat własnego serwera i
przechwycić hasło). Sekrety zapisane przez starsze wersje przy niestandardowej weryfikacji trzeba po aktualizacji
wpisać ponownie.

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
  odrzuconych, bez duplikatów u pozostałych (ostrzeżenie 2202 w dzienniku usługi); po restarcie usługi ponowienie
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
- **Wiadomości do użytkowników** (rozdział 5.8) idą tą samą kolejką, ale **po** czekających alarmach swojego kanału, a
  przy niesprawnym kanale lub błędzie uwierzytelnienia są porzucane od razu (AD Manager dostał już informację o błędzie).

### 5.8 Wiadomości do użytkowników (z AD Managera)

AD Manager może wysyłać wiadomości do użytkowników z szablonów („hasło wygasa 5 listopada”, „konto zostanie
wyłączone” …, [README, U1](../README.md#u1--wiadomości-e-mail-do-użytkowników-smtp-)) bezpośrednio przez SMTP albo
**przez tę usługę** – wtedy korzysta z jej kanału SMTP (także Exchange Online z OAuth2), sekretów, kolejki z
ponawianiem i dziennika wysyłek. Funkcja jest **domyślnie wyłączona**.

**Włączenie:** zakładka Watchdog → *Kanały i SMTP* → karta *Wiadomości do użytkowników (z AD Managera)* (Admin):

| Ustawienie | Domyślnie | Znaczenie |
|---|---|---|
| `userMessages.enabled` | wyłączone | pozwala AD Managerowi wysyłać wiadomości do użytkowników (wymaga włączonego SMTP z nadawcą) |
| `userMessages.maxMessagesPerHour` | 300 | ile wiadomości przyjmie usługa w ostatnich 60 minutach – od wszystkich operatorów razem |
| `userMessages.maxMessagesPerDay` | 2000 | to samo dla ostatnich 24 godzin |
| `userMessages.allowedSenders` | pusta | dodatkowi dozwoleni nadawcy (*Od*) i adresy odpowiedzi: cały adres albo `@domena`; nadawca i adres odpowiedzi kanału SMTP są dozwolone zawsze |
| `retention.userMessageAuditDays` | 365 | jak długo przechowywany jest audyt wysłanych wiadomości |

Zmiana przełącznika i listy nadawców ma w audycie konfiguracji ważność „wysoka”. W AD Managerze ustawienie *Sposób
wysyłki* = *Automatycznie* (domyślne) wybiera Watchdoga, gdy połączenie z usługą jest skonfigurowane, a funkcja włączona
– w przeciwnym razie wysyła bezpośrednio przez SMTP; *Przez AdMng Watchdog* pokazuje powód, gdy usługa nie może
przyjąć wiadomości (wyłączone, brak uprawnień, brak połączenia, starsza wersja usługi).

**Kto może wysyłać:** administratorzy **i obserwatorzy** Watchdoga (helpdesk to zwykle obserwatorzy); `DeniedSids`
wygrywa jak wszędzie. Konfigurację zmienia tylko administrator.

**Co sprawdza usługa** (niezależnie od AD Managera, przy każdej wiadomości – `POST /api/v1/user-messages`):

- jedna wiadomość na konto: 1–2 adresy *Do*, razem z *DW* najwyżej 10; każdy adres to dokładnie jedna skrzynka,
- żadnych znaków CR/LF ani innych sterujących w temacie, adresach i nazwach (wiadomość jest odrzucana, nie
  „naprawiana”), temat do 250 znaków, treść tekstowa do 50 000 znaków, HTML do 100 000,
- HTML bez skryptów, ramek, formularzy, `on…=`, `javascript:`/`data:`/`vbscript:`/`file:` (te same reguły co w AD
  Managerze), język `pl` lub `en`,
- **brak załączników** – żądanie z nieznanym polem (np. `attachments`, `bcc`) jest odrzucane,
- *Od* i *Odpowiedz do* tylko z konfiguracji kanału SMTP lub listy dozwolonych nadawców; **nadawcą koperty** (MAIL
  FROM) jest zawsze `smtp.from`. Usługa nie zna listy dozwolonych domen łączy AD Managera – tę sprawdza AD Manager.

**Wynik dla AD Managera.** Wiadomość trafia do kolejki wysyłek (rodzaj „Wiadomość do użytkownika”), a żądanie czeka do
20 sekund na pierwszą próbę SMTP:

| Odpowiedź | Kiedy | Raport w AD Managerze |
|---|---|---|
| `Delivered` (200) | serwer SMTP przyjął wiadomość dla adresata *Do* | przyjęta (odrzucone kopie *DW* jako ostrzeżenie) |
| `Rejected` (200) | odrzucenie stałe 5xx, np. `smtp.550.5.1.1` | nieudana, kod i podpowiedź; w dzienniku wysyłek „Niedostarczone” |
| `Queued` (202) | limit tempa kanału, chwilowy błąd 4xx/przekroczony czas albo brak próby w 20 s | **przyjęta** z dopiskiem „w kolejce AdMng Watchdog” – usługa ponawia jak alarmy, wynik w dzienniku wysyłek |
| `Failed` (200) | błąd uwierzytelnienia, TLS, konfiguracji kanału | nieudana; przebieg zatrzymany (pozostałe „niewysłane”), wiadomość porzucona od razu |
| 409 / 429 | funkcja wyłączona / limit godzinowy lub dobowy (z `Retry-After`) | przebieg zatrzymany, pozostałe „niewysłane” – *Ponów nieudane* później |

Każde żądanie jest **idempotentne** po identyfikatorze wiadomości: ponowienie po przekroczeniu czasu albo zerwanym
połączeniu zwraca stan pierwszego zgłoszenia i nie wysyła wiadomości drugi raz; ten sam identyfikator z inną treścią
lub od innej osoby to 409. `GET /api/v1/user-messages/status` (administratorzy i obserwatorzy) pokazuje, czy funkcja
działa, kanał (`host:port`), nadawcę, dozwolonych nadawców, limity i liczbę wiadomości w ostatniej godzinie i dobie.

**Dane i audyt.** Temat, treść i adresy są tylko w treści wiadomości w kolejce – usuwane zaraz po wysłaniu, nieudane
po `OutboxDeadDays` (90 dni, do ręcznego ponowienia). Dziennik wysyłek pokazuje dla wiadomości do użytkowników tylko
rodzaj, stan, liczbę odbiorców i kody – **bez tematu i adresów**. Audyt (`user_messages` w `watchdog.db`): kto, kiedy,
liczba odbiorców, skrót SHA-256 posortowanych adresów (pozwala sprawdzić „czy ten adres dostał wiadomość”, nie
przechowując go) i skrót treści (kontrola idempotencji) – przez `retention.userMessageAuditDays`. Dziennik diagnostyczny
zawiera jedną linię na wiadomość (identyfikator, konto operatora, liczba odbiorców) – bez adresów, skrótów i treści.

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
| Stan | połączenie z DC, znacznik USN, zrzut bazowy, kontrola uprawnień, dzienniki każdego DC (pokrycie atrybucji, liczba i czas zapytań o źródło), kanały, baza danych i kopie, dziennik wysyłek (ponawianie), historia zmian konfiguracji; od 0.3.0 także sekcje **Konto usługi** (kvno, ostatnia i następna zmiana hasła, typy szyfrowania, zgodność keytabu z AD, członkostwo w grupach uprzywilejowanych), **Kolektor WEF** (dla każdego DC: ostatni rekord, opóźnienie, zaległości, rekordy zapisane i odrzucone), **Zawartość GPO** (postęp odczytu, błędy, opóźnienie SYSVOL) i **Host** (system, środowisko .NET, wariant klucza sekretów, wolne miejsce, różnica zegara, szyfrowanie LDAP); każdy zgłaszany kod stanu ma opis i zalecenie – m.in. wygasający certyfikat TLS i jego klucz prywatny, rozmiar i konserwacja bazy, planowanie powiadomień, „Nieistniejące DN w konfiguracji”, wykrywanie kontrolerów domeny, źródło atrybucji, odmowa wyszukiwania zmian w katalogu (`permission.search`) |
| Źródła i retencja (od 0.3.0) | atrybucja (włączona, źródło „OpenWEC (WEF)”, odrzucanie logowań kont komputerów i anonimowych, progi ciszy kolektora), treść GPO (włączona, tolerancja opóźnienia SYSVOL, limity plików) i czas przechowywania rekordów logowań |

**Certyfikat usługi (od 0.3.0).** Gdy certyfikat usługi nie pochodzi z zaufanego urzędu (wariant z podpisem własnym),
AD Manager przy pierwszym połączeniu pokazuje okno z odciskiem SHA-256, nazwą i datami ważności; operator porównuje
odcisk z wynikiem `admng-watchdog cert-fingerprint` na serwerze i klika „Ufam temu certyfikatowi” – odcisk zostaje
przypięty w ustawieniach (Ustawienia → Watchdog, „Usuń przypięcie”). Zmieniony certyfikat nigdy nie jest przyjmowany
po cichu (okno pokazuje stary i nowy odcisk), a niezgodność nazwy – nigdy. Zmiana adresu usługi usuwa przypięcie.

Rola Viewer widzi wszystko tylko do odczytu (bez wyjątków, adresów odbiorców i danych serwera SMTP); edycja wymaga
roli Admin.

---

## 7. Dane, retencja i kopie zapasowe

Folder danych usługi to `/var/lib/admng-watchdog` (właściciel `admng-watchdog`, tryb 0700, sprawdzany przy każdym
starcie – przy szerszych uprawnieniach usługa nie startuje).

| Dane | Gdzie | Domyślna retencja |
|---|---|---|
| Migawka obserwowanych atrybutów (bez sekretów) i członkostw | `watchdog.db` | bieżący stan; obiekty usunięte 180 dni |
| Zdarzenia (zmiany, atrybucja – kto i skąd, potwierdzenia) | `watchdog.db` | **1095 dni** (3 lata; próg `MinEventsDays` w ustawieniach startowych – krócej tylko po zmianie przez roota) |
| Znormalizowane zdarzenia z dzienników DC związane ze zmianami (tylko pola potrzebne do dopasowania; wartości wyłącznie obserwowanych, nietajnych atrybutów) | `watchdog.db` | 30 dni; najstarsze usuwane wcześniej, gdy baza przekroczy limit rozmiaru (domyślnie 4096 MB) |
| Rekordy logowań i nieudanych logowań (4624 typ 3 i 10, 4625, 4771, nieudane 4776 – bez logowań kont komputerów i anonimowych) | `watchdog.db` | 48 godzin (ustalone źródło jest kopiowane do zdarzenia) |
| Migawka ustawień GPO (tylko najnowsza dla każdego GPO; hasła GPP i wartości wyglądające na hasła – wyłącznie informacja o obecności; skrypty – tylko skrót SHA-256) | `watchdog.db` | bieżący stan; usunięte GPO – do przywrócenia |
| Kolejka wysyłek (treść wiadomości – także wiadomości do użytkowników z AD Managera, rozdz. 5.8) | `watchdog.db` | treść usuwana zaraz po wysłaniu; nieudane 90 dni; metadane wysłanych 30 dni (dla wiadomości do użytkowników bez tematu i adresów) |
| Audyt wiadomości do użytkowników (kto, kiedy, liczba odbiorców, skróty SHA-256 adresów i treści) | `watchdog.db` | 365 dni (`retention.userMessageAuditDays`) |
| Konfiguracja i historia jej zmian | `watchdog.db` | konfiguracja bieżąca; historia 3 lata |
| Sekrety kanałów (hasło SMTP, sekret OAuth, adresy Teams/webhook, klucze podpisu) | `watchdog.db`, zaszyfrowane Data Protection i powiązane z miejscem docelowym; klucze w `keys/` zaszyfrowane kluczem z poświadczenia systemd (`/etc/admng-watchdog/credentials/dp-key.cred`, TPM2 + klucz hosta albo klucz hosta) | do usunięcia |
| Kopie bazy | `backup/` (codziennie 3:15, 7 ostatnich; kopie sprzed migracji – najnowsza + 30 dni); gdy na `/var` brakuje miejsca, kopia jest pomijana (`storage.backupSkipped`) | 7 kopii |
| Surowe zdarzenia z kontrolerów (pliki OpenWEC) | `/var/lib/openwec/out/<DC>/` | do odczytu przez usługę (pliki rotowane co godzinę); nieprzeczytane – najwyżej 7 dni lub 8 GB razem |

Retencję i limit rozmiaru zapisuje konfiguracja usługi (AD Manager 0.3.0 edytuje z nich tylko czas przechowywania
rekordów logowań). Usuwane rekordy są nadpisywane w pliku bazy (`secure_delete`). Do odtworzenia potrzebne są **baza,
folder `keys/` i poświadczenie systemd z tego samego hosta** (przy wariancie TPM2 – także ten sam vTPM); na innej
maszynie `admng-watchdog reset-secrets-key` tworzy nowy klucz, a sekrety kanałów wpisuje się ponownie
([WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 3.9). Zalecana jest kopia całej maszyny wirtualnej (zasób Tier 0). Rozmiar:
ok. 0,6 KB na zdarzenie (1 000 zdarzeń dziennie ≈ 0,7 GB w 3 lata), ok. 0,6 KB na obiekt migawki, rekordy logowań
ok. 120 MB.

Codzienna kopia (3:15, `VACUUM INTO`) i kompaktowanie bazy **nie wstrzymują** wykrywania zmian ani wysyłania
alarmów. Stan bazy pokazują dwa sprawdzenia o jednej definicji (stan nie zmienia się już co godzinę):

- „Rozmiar bazy danych” (`storage.size`) – plik bazy razem z plikiem WAL względem `Retention.MaxDatabaseMb`:
  ostrzeżenie od 80 % limitu, błąd powyżej limitu,
- „Wolne miejsce na dysku” (`storage.free`) – błąd, gdy na woluminie folderu danych zostaje mniej niż 1 GiB.

Nieudana retencja lub konserwacja bazy daje stan „Konserwacja bazy danych” (`storage.maintenance`).

---

## 8. Bezpieczeństwo

- **Dane osobowe (RODO).** Folder danych i powiadomienia zawierają dane pracowników (nazwiska, działy, zmiany kont,
  adresy i nazwy stacji). Uzgodnij z IOD cele, okres retencji (domyślnie 3 lata), krąg odbiorców i kanały (Teams i
  Power Automate przechowują kopie poza folderem – historia przepływu 30 dni). Wpisz przetwarzanie do rejestru
  czynności. Obowiązki z ustawy o KSC (NIS2) dotyczące monitorowania i dowodów incydentów przemawiają za dłuższą
  retencją zdarzeń. Odczyt pojedynczych zdarzeń w API jest rejestrowany (kto, numer zdarzenia) w dzienniku usługi
  (journald) – zdarzenia 2600 (szczegóły w zakładce) i 2601 (strona zdarzenia z linku w powiadomieniu). Każde otwarcie
  szczegółów i każda otwarta strona zdarzenia to jeden wpis – dobierz rozmiar dziennika systemowego albo przekazuj go do
  SIEM.
- **Zero sekretów.** Wartości haseł, LAPS, BitLockera, TPM, gMSA, sekretów LSA (`currentValue`, `priorValue`),
  atrybutów oznaczonych w schemacie jako poufne i haseł z preferencji GPO (`cpassword`) nie są zapisywane ani wysyłane.
- **Serwer Watchdog to zasób Tier 0** (rozdział 3): poza domeną, administrowany tylko kluczami SSH ze stacji PAW, zapora
  nftables (przychodzące tylko z kontrolerów i podsieci administratorów, połączenia wychodzące tylko do kontrolerów,
  serwera SMTP i – dla usługi – Teams), usługi w piaskownicy systemd, bez pamięci wymiany i zrzutów pamięci.
- **Konto usługi** `ADMNG-WD$` ma tylko odczyt AD i SYSVOL; nie ma uprawnień zapisu ani replikacji, nie należy do grup
  uprzywilejowanych (usługa nie startuje, gdy należy), nie może być delegowane, używa tylko AES. Jego losowe hasło (120
  znaków) nikt nie zna; zmienia się co 30 dni, a na dysku leżą tylko klucze w keytabie (`root:admng-keytab 0640`).
  Każda zmiana konta poza zaplanowaną zmianą hasła jest alarmem krytycznym. Ryzyka przyjęte świadomie (ADR-0009):
  certyfikat wystawiony na to konto po kradzieży keytabu przeżywa zmianę hasła, a keytab wspólny z OpenWEC pozwala
  podrobić bilet do API z podsieci administratorów – szczegóły i ograniczenia w
  [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 0.3 i 4.3.
- **Dostęp do API**: HTTPS (TLS 1.2/1.3), wyłącznie Kerberos (NTLM odrzucany), role wg SID grup AD z lokalnego
  `/etc/admng-watchdog/appsettings.json` (konto usługi nie może go zmienić) – usługa ustala członkostwo sama z
  `tokenGroups` (pamięć podręczna 5 minut), lista natychmiastowych odmów (`DeniedSids`), dodatkowy nagłówek wymagany
  przy zmianach (ochrona przed CSRF z przeglądarki), dostęp tylko z podsieci administratorów i monitoringu (także dla
  połączeń z samego serwera), bez serwerów pośredniczących.
- **Sekrety kanałów** są tylko do zapisu i powiązane z miejscem docelowym: API i zakładka nigdy ich nie zwracają,
  administrator Watchdoga nie może przekierować zapisanego hasła lub tokenu OAuth na inny serwer (także przez zmianę
  weryfikacji certyfikatu SMTP – przypięcie innego certyfikatu wymaga ponownego wpisania hasła); w zakładce i w
  dziennikach usługi widać z adresów kanałów wyłącznie `schemat://host/…` (bez ścieżki i parametrów).
- **Połączenia z kontrolerami** wyłącznie przez Kerberos: LDAP z podpisem i szyfrowaniem (wymagana siła szyfrowania
  ≥ 56 bitów – bez niej połączenie jest odrzucane), SYSVOL przez SMB 3 z podpisywaniem; WEF od kontrolerów tylko z ich
  adresów i tylko z Kerberosem.
- **Zmiany konfiguracji** są audytowane i zgłaszane (także dotychczasowym odbiorcom); krytycznych detekcji nie da się
  wyłączyć przez API bez lokalnej zgody, a retencji zdarzeń – skrócić poniżej progu z ustawień startowych.
- **Dzienniki diagnostyczne** usługi (journald) nie zawierają wartości z katalogu, treści wyjątków ani treści plików GPO
  – testy automatyczne to sprawdzają.
- **Treści z AD są niezaufane**: w e-mailach kodowane HTML i „rozbrajane” (5.6), w kartach Teams wyświetlane jako
  zwykły tekst (bez linków Markdown), w webhookach jako JSON. Nazwy komputerów podane przez klient lub z DNS są
  oznaczane i nigdy nie decydują o regułach. Pliki GPO są czytane z limitami rozmiaru i bez rozwijania encji XML.
- **Źródła zdarzeń** są sprawdzane: przyjmowane są tylko rekordy dostawcy „Microsoft-Windows-Security-Auditing”
  z zapisywalnych kontrolerów tej domeny, ze zgodną subskrypcją, nazwą komputera i adresem nadawcy. Kto przejmie konto
  `openwec` na serwerze, może jednak rekordy podrobić lub pominąć – dlatego to konto jest izolowane (bez połączeń
  wychodzących) i traktowane jak zasób Tier 0.
- **Wyjątki od reguł** są audytowane i widoczne w każdym raporcie dziennym; wyjątki dla reguł krytycznych są
  oznaczane ostrzeżeniem.
- **Monitoring usługi**: `/healthz` dla systemu monitorowania, dziennik usługi (`journalctl -u admng-watchdog -p
  warning`, rozdział 10), stan jednostek (`systemctl is-failed admng-watchdog openwecd`), opcjonalny heartbeat
  webhooka, brak raportu dziennego jako sygnał awarii.

---

## 9. Ograniczenia

- **Wersja zapoznawcza 0.3.0:** usługa na Linuksie nie przeszła jeszcze pilotażu na kontrolerach Windows Server 2025 i
  2022 – atrybucję przez WEF i OpenWEC sprawdzono na zarejestrowanych zdarzeniach Windows (te same wyniki co w wersji
  0.2.0) i na laboratorium Samba, nie na żywych kontrolerach. Listę spraw do rozstrzygnięcia w pilotażu podaje
  [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 7.4.
- Jedna instancja usługi monitoruje jedną domenę (w lesie z wieloma domenami – instancja na domenę); kontrolery
  innych domen pojawiają się tylko jako „nowy / usunięty kontroler” (priorytet wysoki).
- Zmiany wykonane na DC w innych lokacjach są widoczne po replikacji międzylokacyjnej (domyślnie do 180 min).
- Wygaśnięcie blokady po upływie czasu blokady niczego nie zapisuje w AD, a przy pierwszym udanym logowaniu DC zeruje
  `lockoutTime` – Watchdog nie traktuje tego jako odblokowania (opcjonalne zdarzenie „wygaśnięcie blokady” wylicza
  sam). Czas blokady uwzględnia PSO, jeśli konto usługi może je odczytać (WATCHDOG-LINUX.md 2.6).
- Atrybucja wymaga zasad inspekcji, SACL i działającego przekazywania zdarzeń (WEF); bez nich wykonawca jest
  „nieustalony” (z podaną przyczyną). Zmiany wykonane przez ADWS (Centrum administracyjne AD, PowerShell) lub zdalną
  sesję na DC mogą wskazywać jako źródło sam kontroler („lokalnie na DC”). Rekordy logowań są przechowywane 48 godzin –
  sesji starszych źródło nie zostanie ustalone („SOURCE_LOGON_NOT_FOUND”).
- Zdarzenia zapisane przez OpenWEC w ostatnich sekundach przed nagłą utratą zasilania serwera mogą przepaść (OpenWEC
  nie wymusza zapisu na dysk); pliki OpenWEC utworzone i usunięte w całości, gdy usługa była zatrzymana (limit 7 dni lub
  8 GB), nie są zgłaszane jako luka – nazywa je tylko dziennik jednostki porządkującej.
- `lastLogonTimestamp` jest aktualizowany z opóźnieniem 9–14 dni – próg nieaktywności nie powinien być krótszy niż
  30 dni.
- Bez prawa odczytu `CN=Deleted Objects` usunięcia są wykrywane dopiero przy dobowym uzgodnieniu (jako „obiekt
  zniknął”).
- Członkostwa czasowe (PAM, TTL) wygasają bez śladu w AD – ich koniec jest wykrywany przy dobowym uzgodnieniu.
- Treść GPO: zmiana plików w SYSVOL bez zmiany wersji GPO jest wykrywana dopiero przy dobowym uzgodnieniu, bez
  wykonawcy (`SYSVOL_NOT_AUDITED` – inspekcja udziałów plików nie jest zbierana); zmianę wartości już istniejącego
  sekretu (np. hasła w ustawieniach rejestru) widać tylko po dacie zmiany elementu GPP, nie po wartości; plik, którego
  nie da się odczytać lub rozebrać, jest raportowany jako „nie udało się odczytać”.
- Usługa niczego nie blokuje ani nie cofa – tylko raportuje.
- AD Manager 0.3.0 nie ma edytora retencji zdarzeń ani limitu rozmiaru bazy (zmiana tylko przez API usługi);
  konfiguracja przeniesiona z wersji 0.2.0 zachowuje limit 10 240 MB.
- Exchange Online OAuth2 na Linuksie – tylko z sekretem klienta (bez certyfikatu, 5.2).
- Zmiana grup ról działa na obserwację grup uprzywilejowanych dopiero po restarcie usługi; zmiana podsieci w
  `appsettings.json` nie zmienia zapory nftables – popraw ją osobno (WATCHDOG-LINUX.md 3.3).

---

## 10. Rozwiązywanie problemów

| Objaw | Przyczyna i rozwiązanie |
|---|---|
| Zakładka: „Usługa odrzuciła uwierzytelnienie Kerberos” (401) | SPN `HTTP/<nazwa>` nie jest na koncie `ADMNG-WD$` (albo jest na dwóch kontach, np. jeszcze na gMSA z 0.2.0), keytab nie ma wersji klucza, której używa KDC, albo klient używa aliasu CNAME/adresu IP (wtedy Windows próbuje NTLM, który jest odrzucany). Sprawdź `setspn -Q HTTP/admng-wd.contoso.com`, `admng-watchdog-keytab verify` na serwerze (dziennik usługi: wpis 2501 z podpowiedzią), używaj nazwy z rekordu A; po zmianach `klist purge` na stacji. |
| „Brak uprawnień” (403) | Konto nie należy do grup z `Authorization` w `/etc/admng-watchdog/appsettings.json` (członkostwo jest sprawdzane co 5 minut), jest na liście `DeniedSids` albo stacja jest spoza `AllowedRemoteSubnets`. |
| Odpowiedź 503 `role-lookup-unavailable` (żądania odrzucane mimo poprawnego logowania) | Usługa nie może odczytać członkostwa operatora z AD (brak połączenia z DC) – spróbuj ponownie po chwili; na serwerze `journalctl -u admng-watchdog` (wpis 2506) i stan połączenia z DC. |
| „Usługa nieosiągalna” | Usługa zatrzymana, zapora (port 8443 w nftables i `AllowedRemoteSubnets`), DNS. Na serwerze: `systemctl status admng-watchdog` i `sudo admng-watchdog --health` (kod 0 = działa). |
| Błąd certyfikatu / okno „Potwierdź certyfikat” | Certyfikat nie pochodzi z zaufanego CA – porównaj odcisk z `admng-watchdog cert-fingerprint` na serwerze (rozdział 6); nazwa niezgodna z adresem nie jest nigdy przyjmowana. |
| Usługa nie startuje (`systemctl status admng-watchdog` – `failed`, kod wyjścia) | Kody 2–10 i `243/CREDENTIALS` z przyczynami: [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 5.3; błędy ustawień pokazuje `sudo admng-watchdog --check-config`. |
| Ostrzeżenie 2002 w dzienniku usługi po edycji `appsettings.json` | Plik ma błąd składni JSON albo niepoprawną wartość (np. `"Minimalny"` zamiast `"Minimal"`, `"tak"` zamiast `true`). Usługa działa dalej na poprzednich ustawieniach – zmiana **nie** obowiązuje, a przy następnym starcie usługa się zatrzyma (kod 2). Popraw plik i sprawdź go `sudo admng-watchdog --check-config`. |
| Stan: „Nieistniejące DN w konfiguracji” | OU lub grupę wskazaną w konfiguracji przemianowano, przeniesiono albo usunięto – popraw wymienione DN (reguły, grupy uprzywilejowane, poddrzewa, wyjątki). |
| Stan: „permission.*” | Konto nie widzi części obiektów lub atrybutów – nadaj uprawnienia z [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 2.6 (także na AdminSDHolder; SDProp do 60 min). |
| Stan: „Konto usługi” – `serviceAccount.*` | Keytab niezgodny z AD, hasło starsze niż 32/35 dni, brak wpisu odmowy dla SELF, konto w grupie uprzywilejowanej – opis i zalecenie przy każdym kodzie; WATCHDOG-LINUX.md 3.6, 3.7, 9.1. |
| Stan: „Kolektor WEF” – `wec.<DC>.*` | DC milczy, rekordy odrzucone (niezgodna tożsamość nadawcy) albo utracony plik OpenWEC – WATCHDOG-LINUX.md 9.2. |
| Stan: „Zawartość GPO” – `gpo.<GUID>.*`, `sysvol.*` | Brak odczytu SYSVOL, opóźnienie replikacji SYSVOL, plik zbyt duży lub nieczytelny – WATCHDOG-LINUX.md 2.7 i 9.4. |
| Wykonawca „nieustalony – AUDIT_POLICY_MISSING / SACL_MISSING / LOG_OVERWRITTEN / DC_UNREACHABLE” | Odpowiednio: zasady inspekcji (WATCHDOG-LINUX.md 2.10), SACL (2.11), rozmiar dziennika (2.10) albo wyczyszczony dziennik Security (1102/1104 lub numeracja rekordów od nowa – zdarzenia sprzed wyczyszczenia nie są już dostępne), DC, który przestał przekazywać zdarzenia (2.12, 9.2). |
| Źródło „SOURCE_LOGON_NOT_FOUND / SOURCE_NOT_COLLECTED” | Logowanie starsze niż czas przechowywania rekordów logowań (48 h) albo kontroler nie przysłał żadnego zdarzenia 4624 w oknie wyszukiwania – subskrypcja i GPO WEF (WATCHDOG-LINUX.md 2.12, 4.4). |
| Zapis konfiguracji: „wpisz hasło/sekret ponownie” (`secretRebindRequired`) | Zmieniono serwer, port, użytkownika, dzierżawę, adres kanału albo weryfikację certyfikatu serwera SMTP (przypięty odcisk, tryb, sprawdzanie odwołań) – zapisany sekret jest powiązany ze starym miejscem docelowym. Po aktualizacji usługi także hasło SMTP / sekret XOAUTH2 zapisany przy niestandardowej weryfikacji certyfikatu (4.9): do jego ponownego wpisania e-mail nie jest wysyłany. |
| Dziennik wysyłek: „Niedostarczone” z kodem `partial.smtp.4xx…` | Jeden z odbiorców przez ok. 22 h odrzucał wiadomość tymczasowo (4xx); pozostali ją dostali. Ponowienie (rola Admin, do 14 dni) wyśle ją tylko do brakujących odbiorców (5.2). |
| Dziennik wysyłek: „Niedostarczone” z kodem `channel.disabled` / `channel.unknown` / `smtp.disabled` | Kanał wyłączono lub usunięto albo wyłączono SMTP, gdy wiadomość czekała w kolejce. Ponowne włączenie kanału jej nie dostarczy – ponów ręcznie (5.7). |
| E-mail: `535 5.7.139` / `550 5.7.30` | Exchange Online: SMTP AUTH wyłączone dla dzierżawy/skrzynki – przejdź na OAuth2 lub `Set-CASMailbox -SmtpClientAuthenticationDisabled $false`. |
| E-mail: `550 5.7.68` | Dzierżawa odrzuca „Direct Send” – użyj łącznika przychodzącego lub logowania. |
| Teams: „przestarzały adres” | Adres `*.webhook.office.com` – utwórz Workflow (5.3). |
| Teams: 401/403 z tekstem o „Shared Access scheme” lub „OAuth” | Niezgodny tryb uwierzytelniania wyzwalacza (SAS vs Entra) – zmień tryb kanału albo ustawienie przepływu. |
| Brak raportu dziennego | Usługa nie działa, SMTP niesprawne (Stan → Kanały) lub raport wyłączony. |

Numery zdarzeń w dzienniku usługi (journald – `journalctl -u admng-watchdog`, w wierszu jako `Category[1003]`;
w wersji 0.2.0 – dziennik Application, źródło „AdMng Watchdog”) do monitoringu (Zabbix, SIEM): 1000 start usługi,
1001 brak połączenia z DC / przełączenie na inny DC, 1002 nieudane wysyłki, 1003 problem z magazynem (miejsce, limit,
uprawnienia folderu), 1004 braki uprawnień, 1005 problemy z atrybucją / luka w dzienniku DC, 1006 zmiana konfiguracji,
1007 przestarzałe środowisko .NET / wygasający certyfikat / niedostępny klucz odnowionego certyfikatu (ostrzeżenie,
dopóki działa poprzedni certyfikat; błąd, gdy ten wygaśnie), 1008 odcisk certyfikatu TLS (przy każdym starcie),
1010 nieudana kontrola startowa albo zatrzymane zadanie, 1011 problem z kontem usługi, 1012 problem z kolektorem WEF,
1013 problem z treścią GPO, **2002 nieprzyjęta edycja `appsettings.json`** (usługa działa na poprzednich ustawieniach),
2501 odrzucone uwierzytelnienie Kerberos (z podpowiedzią), 2202/2203 odbiorcy e-maila odrzuceni
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
  systemd), API `http://127.0.0.1:5089`, odczyt katalogu jako `odczyt.test` prostym wiązaniem bez TLS
  (`AllowInsecureLab` – odrzucane poza środowiskiem Development); hasło wyłącznie w zmiennej
  `ADMNG_WATCHDOG_LDAP_PASSWORD`, dane w `~/.local/share/admng-watchdog-lab`. W tym profilu klucze Data Protection nie
  są szyfrowane (ostrzeżenie przy starcie), a atrybucja jest wyłączona (`SOURCE_DISABLED`) – laboratorium Samba nie ma
  dzienników zabezpieczeń Windows.
- Mailpit (http://127.0.0.1:8025) zbiera e-maile (STARTTLS, certyfikat przypinany przez `configure-lab.sh`), odbiornik
  webhooków sprawdza podpisy Standard Webhooks i koperty Teams Workflows.
- Konfiguracja laboratoryjna celowo skraca okna agregacji i kończy tryb wdrożenia; ustawienia produkcyjne opisują
  rozdział 5 i [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md).
- Konto `odczyt.test` nie widzi `CN=Deleted Objects`, więc usunięcia w laboratorium pojawiają się dopiero przy
  uzgodnieniu dobowym (stan `permission.deletedObjects`).

Testy integracyjne (`tests/AdMng.Watchdog.IntegrationTests`) uruchamiają prawdziwe silniki i całą usługę przeciw
laboratorium:

```bash
tools/testlab/watchdog/start-mailpit.sh
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
export ADMNG_TEST_LDAP_READONLY_USER='odczyt.test@contoso.test' ADMNG_TEST_LDAP_READONLY_PASSWORD='Passw0rd!Lab2026'
export ADMNG_TEST_SMTP_HOST=127.0.0.1 ADMNG_TEST_SMTP_PORT=1025 ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025
dotnet test tests/AdMng.Watchdog.IntegrationTests
# konto komputera z keytabem (narzędzie admng-watchdog-keytab), LDAP przez SASL GSSAPI, API z Negotiate i rolami
# po SID-ach (KeytabLabTests, KerberosNegotiateTests), OpenWEC i SYSVOL: eval "$(tools/testlab/watchdog/keytab-lab.sh env)"
```

Pakiety Debiana buduje `tools/packaging/build-deb.sh` (`admng-watchdog`) i `tools/packaging/build-openwec-deb.sh`
(`admng-openwec`, w obrazie Rust danej wersji Debiana); `tools/packaging/test-deb-install.sh` sprawdza instalację w
czystym kontenerze Debian 12 lub 13.

CI (`.github/workflows/ci.yml`): na Windows wszystkie projekty testów (także testy tylko-Windows), aplikacja AD Manager
i zrzuty ekranu (także zakładki Watchdog – gałąź `screenshots`); na Ubuntu z Samba DC – testy silnika i usługi, testy
integracyjne z Mailpit i skrypty laboratorium (`lab-scenario.sh --verify`); zadania `openwec-package` (Debian 12 i 13,
źródła GPL-3.0) i `linux-package` (pakiet `admng-watchdog`, test instalacji na Debianie 12 i 13, lintian). Wydanie
(`.github/workflows/release.yml`) publikuje te pakiety razem z AD Managerem.
