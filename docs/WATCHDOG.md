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
| Nowe obiekty | nowe konto użytkownika, nowy komputer, nowa grupa, nowe OU, nowe konto gMSA | niski / informacja | konta: e-mail w godzinach pracy (zbiorczo co 5 min) + raport dzienny; komputery: raport dzienny |
| Nietypowe komputery | komputer dodany do domeny przez zwykłego użytkownika (MachineAccountQuota), utworzony w `CN=Computers`, nowy serwer | średni | e-mail w godzinach pracy |
| Usunięcia | usunięte / przywrócone konto, grupa, komputer, OU; masowe usuwanie (≥ 10 obiektów w 10 min) | średni (OU – wysoki, masowe – krytyczny) | e-mail |
| Stan konta | wyłączenie, włączenie (włączenie konta wyłączonego > 30 dni – wysoki), zmiana flag (np. „hasło nigdy nie wygasa”, „hasło niewymagane”) | informacja … wysoki | flagi ryzykowne: e-mail; pozostałe: raport dzienny |
| Blokady | zablokowanie konta (z nazwą komputera, z którego szły błędne hasła), odblokowanie, podejrzenie ataku „password spraying” (≥ 5 blokad w 10 min) | niski (atak – wysoki) | blokady: e-mail do helpdesku w godzinach pracy; atak: e-mail zawsze |
| Hasła | reset hasła przez administratora, zmiana hasła, „użytkownik musi zmienić hasło” | informacja (konta uprzywilejowane i serwisowe – wysoki) | raport dzienny; uprzywilejowane: e-mail |
| Parametry kont | każda obserwowana zmiana atrybutu: stara → nowa wartość (dział, stanowisko, przełożony, telefon, UPN, skrypt logowania, profil, ograniczenia logowania …) | informacja (skrypty/profile/ograniczenia – średni) | raport dzienny; skrypty i profile: e-mail |
| Grupy | dodanie / usunięcie członka, zmiana typu grupy | informacja | raport dzienny; grupy obserwowane: e-mail |
| Dostęp uprzywilejowany | uzyskanie członkostwa (także pośredniego, przez zagnieżdżone grupy i grupę podstawową) w Domain Admins, Enterprise Admins, Administrators, Schema Admins, operatorach kont/kopii/serwerów, DnsAdmins … | krytyczny / wysoki | e-mail natychmiast, całą dobę |
| Atrybuty krytyczne | adminCount, SID History, delegowanie Kerberos, SPN na koncie użytkownika, msDS-KeyCredentialLink (Shadow Credentials), zmiana grupy podstawowej, zmiana uprawnień (ACL) obiektów krytycznych (domena, AdminSDHolder, OU kontrolerów) | wysoki / krytyczny | e-mail natychmiast, całą dobę |
| Domena | zasady haseł i blokad, zasady szczegółowe (PSO), powiązania GPO z domeną i OU kontrolerów, relacje zaufania, nowy kontroler domeny, zmiany schematu | wysoki / krytyczny | e-mail natychmiast |
| Dzienniki DC | wyczyszczenie dziennika zabezpieczeń (1102), zmiana zasad inspekcji (4719), zmiana hasła DSRM (4794) | krytyczny / wysoki | e-mail natychmiast |
| Terminy | konto wygasa za 14 / 7 / 1 dzień, konto wygasło, hasło konta uprzywilejowanego lub serwisowego wygasa | informacja (uprzywilejowane ≤ 7 dni – wysoki) | raport dzienny |
| Stan usługi | brak połączenia z DC, ponowna synchronizacja, braki uprawnień, problemy z dziennikami DC, nieudane wysyłki, mało miejsca | wysoki / średni | e-mail zawsze (reguła zablokowana – nie da się jej wyłączyć) |

Każde zdarzenie zawiera: **co** się zmieniło (stara → nowa wartość), **kiedy** i **na którym DC**, a gdy pozwalają na
to dzienniki kontrolerów – **kto** to zrobił i **z którego komputera** (z poziomem pewności). Wartości haseł, LAPS,
BitLockera i atrybutów poufnych nigdy nie są odczytywane ani wysyłane – w powiadomieniu pojawia się wyłącznie
„zmieniono (wartość ukryta)”.

Przy pierwszym uruchomieniu Watchdog robi **zrzut bazowy** domeny i nie wysyła żadnych powiadomień o zmianach –
raportuje dopiero to, co zmieni się później.

---

## 2. Jak to działa

```
            ┌──────────── serwer członkowski domeny, konto gMSA CONTOSO\svc-admngwd$ ─────────────┐
            │ AdMng.Watchdog.exe (usługa „AdMng Watchdog”)                                          │
  DC (PDC) ◄┼─ LDAP 389, Kerberos + podpis + szyfrowanie: co 15 s sprawdzenie highestCommittedUSN,     │
            │  pobranie tylko zmienionych obiektów (uSNChanged), porównanie z migawką                  │
  wszystkie ◄┼─ RPC (Remote Event Log): zdarzenia 4720…4781, 5136…5141, 4624 → kto i skąd             │
  DC        │                                                                                          │
            │  reguły → kolejka powiadomień → SMTP / Teams / webhook (ponawianie, limity)              │
            │  folder danych: %ProgramData%\AdMng\Watchdog  (SQLite, klucze, kopie zapasowe)            │
            │  API HTTPS 8443 (Kerberos, dostęp tylko dla wskazanych grup AD)                           │
            └──────────────────────────────────────▲───────────────────────────────────────────────┘
                                                   │ HTTPS + Kerberos
                                  AD_MNG → Narzędzia → Watchdog (Ctrl+Shift+A)
```

1. **Wykrywanie (LDAP).** Usługa jest „przypięta” do jednego kontrolera (domyślnie emulatora PDC – tam trafiają
   blokady kont). Co 15 sekund odczytuje z RootDSE licznik `highestCommittedUSN`. Gdy się zmienił, pobiera tylko
   obiekty o `uSNChanged` większym niż ostatnio przetworzony i porównuje obserwowane atrybuty z migawką zapisaną w
   folderze danych. Członków grup i przełożonych porównuje po GUID obiektów, więc przeniesienie lub zmiana nazwy
   konta nie wygląda jak zmiana członkostwa. Usunięte obiekty widzi w kontenerze `Deleted Objects`, a raz na dobę
   (domyślnie 02:30) wykonuje pełne uzgodnienie.
2. **Atrybucja (dzienniki DC).** Z metadanych replikacji usługa wie, na którym DC i kiedy powstała zmiana. Czyta
   dziennik Security tego kontrolera i dopasowuje zdarzenie (np. 4738 dla konta, 4728 dla grupy, 5136 dla atrybutu).
   Komputer źródłowy ustala z logowania 4624 tej samej sesji (adres IP, nazwa stacji), a dla blokad z pola „Caller
   Computer Name” zdarzenia 4740. Dziennik bywa opóźniony – wtedy zdarzenie ma status „ustalanie…”, a po
   dopasowaniu jest uzupełniane (dla zdarzeń krytycznych przychodzi dodatkowy e-mail w tym samym wątku). Jeżeli
   wykonawcy nie da się ustalić, Watchdog nie zgaduje: pokazuje „nieustalony” z przyczyną (np. brak zasad inspekcji).
3. **Powiadomienia.** Reguły decydują, które zdarzenia i komu są wysyłane: natychmiast (zbiorczo w krótkim oknie,
   z limitem wiadomości na godzinę i z uwzględnieniem godzin pracy i polskich świąt), w raporcie dziennym albo do
   Teams / webhooka. Wysyłki czekają w trwałej kolejce i są ponawiane po błędach.

---

## 3. Wymagania

- Serwer Windows Server 2019 lub nowszy, członek domeny (nie musi i nie powinien być kontrolerem domeny),
  zainstalowany **ASP.NET Core Runtime 10 (x64)** – aktualizacje przez Microsoft Update/WSUS.
- Konto usługi: **gMSA** (zalecane) z uprawnieniami tylko do odczytu, opisane niżej. Nie jest potrzebne prawo
  „Replicating Directory Changes”.
- Rekord DNS typu **A** dla usługi (np. `admng-wd.contoso.com`) – nie CNAME – i certyfikat TLS z tą nazwą
  (z wewnętrznego CA).
- Poziom funkcjonalności lasu dowolny wspierany przez Windows Server 2016+; Kerberos AES włączony (RC4 jest
  wyłączane na DC w 2026 r.).
- Dla atrybucji: zasady inspekcji na kontrolerach, SACL na obiektach i dostęp do dzienników (rozdział 4.5–4.7).
- Dla e-maili: serwer SMTP (Exchange Online, Exchange w firmie albo przekaźnik).
- Stacje administratorów z AD_MNG muszą mieć dostęp do portu usługi (domyślnie 8443/TCP).

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

# Rekord A (nie CNAME!) – Kerberos buduje SPN z nazwy kanonicznej
Add-DnsServerResourceRecordA -ZoneName contoso.com -Name admng-wd -IPv4Address 10.0.1.25
```

Rola **Admin** może zmieniać konfigurację, testować kanały i ponawiać wysyłki; rola **Viewer** tylko przegląda
zdarzenia i stan oraz potwierdza zdarzenia. Członkowie grupy Admin mają też uprawnienia Viewer. Zmiana członkostwa
działa po odnowieniu biletu Kerberos (wylogowanie albo `klist purge` na stacji).

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

- **Nie** używaj `-RestrictToOutboundAuthenticationOnly` – klienci AD_MNG uwierzytelniają się do usługi Kerberosem.
- Nie dodawaj `HTTP/SRV-WD01…` do gMSA (zepsułoby to inne usługi HTTP serwera, np. WinRM).
- Gdy zespół AD nie zgadza się na SPN na koncie usługi, użyj trybu **HTTP.sys** (rozdział 4.10) – wtedy Kerberos
  obsługuje konto komputera i SPN nie jest potrzebny.

### 4.3 Uprawnienia do odczytu katalogu

W wielu domenach zwykły użytkownik czyta konta dzięki grupie „Pre-Windows 2000 Compatible Access”. W domenach
utwardzonych tak nie jest i obiekty, których konto nie może odczytać (np. `uSNChanged`, `lockoutTime`), **po cichu
znikają** ze śledzenia. Dlatego nadaj uprawnienia jawnie:

```powershell
$d = "DC=contoso,DC=com"; $a = "CONTOSO\svc-admngwd$"
foreach ($c in "user","computer","group","organizationalUnit","msDS-GroupManagedServiceAccount","msDS-ManagedServiceAccount","contact") {
    dsacls $d /I:S /G "${a}:GR;;$c"
}
dsacls $d /G "${a}:GR"                                   # obiekt domeny: zasady haseł i blokad
dsacls "CN=AdminSDHolder,CN=System,$d" /G "${a}:GR"     # konta chronione (adminCount=1) dziedziczą ACL z AdminSDHolder (SDProp, do 60 min)
```

**Usunięte obiekty** (zalecane – bez tego usunięcia są wykrywane dopiero przy dobowym uzgodnieniu jako
„obiekt zniknął”):

```powershell
dsacls "CN=Deleted Objects,$d" /takeownership           # zmienia właściciela kontenera systemowego – wymaga zgody zespołu AD
dsacls "CN=Deleted Objects,$d" /G "${a}:LCRP"
```

Uprawnienie do zapisu w AD **nie jest** potrzebne. Usługa po starcie i raz na dobę sprawdza, czy widzi wszystko, czego
potrzebuje (zakładka Watchdog → Stan → „Kontrola uprawnień”).

### 4.4 Dostęp do dzienników kontrolerów (atrybucja)

```powershell
# Wbudowana grupa domenowa „Event Log Readers” (S-1-5-32-573) – jedna dla wszystkich DC domeny
Add-ADGroupMember -Identity (Get-ADGroup -Identity "S-1-5-32-573") -Members (Get-ADServiceAccount svc-admngwd)
```

Na kontrolerach (najlepiej GPO dla OU „Domain Controllers”) włącz reguły zapory „Remote Event Log Management”
(nazwy reguł są niezależne od języka systemu):

```powershell
Get-NetFirewallRule -Name "RemoteEventLogSvc*" | Enable-NetFirewallRule
```

Ustaw rozmiar dziennika Security na DC na co najmniej **1 GB** (GPO: Konfiguracja komputera → Zasady → Ustawienia
systemu Windows → Ustawienia zabezpieczeń → Dziennik zdarzeń) – mały dziennik nadpisuje zdarzenia, zanim usługa je
odczyta (przyczyna „LOG_OVERWRITTEN”). Po zmianie członkostwa zrestartuj usługę Watchdog.

### 4.5 Zasady inspekcji na kontrolerach domeny

GPO na OU „Domain Controllers” → Konfiguracja zaawansowanych zasad inspekcji. Włącz (S = sukces, F = niepowodzenie):

| Podkategoria (nazwa angielska) | GUID (do `auditpol`) | Ustawienie | Po co |
|---|---|---|---|
| User Account Management | `{0CCE9235-69AE-11D9-BED3-505054503030}` | S, F | 4720–4726, 4738, 4740, 4767, 4781 |
| Computer Account Management | `{0CCE9236-69AE-11D9-BED3-505054503030}` | S | 4741–4743 |
| Security Group Management | `{0CCE9237-69AE-11D9-BED3-505054503030}` | S | 4727–4737, 4754–4758, 4764 |
| Distribution Group Management | `{0CCE9238-69AE-11D9-BED3-505054503030}` | S | 4744–4763 |
| Other Account Management Events | `{0CCE923A-69AE-11D9-BED3-505054503030}` | S | m.in. 4794 |
| Directory Service Changes | `{0CCE923C-69AE-11D9-BED3-505054503030}` | S | 5136–5141 (wymaga SACL – 4.6) |
| Logon | `{0CCE9215-69AE-11D9-BED3-505054503030}` | S, F | 4624 – komputer źródłowy |
| Audit Policy Change | `{0CCE922F-69AE-11D9-BED3-505054503030}` | S | 4719 |

Zostaw włączone „Inspekcja: wymuś ustawienia podkategorii zasad inspekcji…”. Sprawdzenie na DC:
`auditpol /get /category:*` (GUID-y podkategorii pokazuje `auditpol /list /subcategory:* /v`).

### 4.6 SACL dla zdarzeń 5136 („kto zmienił atrybut”)

Domyślne SACL-e w AD **nie** generują zdarzeń 5136/5137/5141 dla użytkowników, grup i komputerów. Wzorem Microsoft
Defender for Identity dodaj wpis inspekcji „Wszyscy – sukces” na obiekcie domeny, dziedziczony przez obiekty
użytkowników, grup, komputerów, kont MSA/gMSA i OU (uruchom jako administrator domeny – wymagane prawo
„Zarządzanie inspekcją i dziennikiem zabezpieczeń”):

```powershell
Import-Module ActiveDirectory
$domain = (Get-ADDomain).DistinguishedName
$acl = Get-Acl -Path "AD:\$domain" -Audit
$everyone = [System.Security.Principal.SecurityIdentifier]'S-1-1-0'
$rights = [System.DirectoryServices.ActiveDirectoryRights]'WriteProperty, Delete, DeleteTree, WriteDacl, WriteOwner, CreateChild, DeleteChild, ExtendedRight, Self'
$classes = @{
  user  = 'bf967aba-0de6-11d0-a285-00aa003049e2'; group = 'bf967a9c-0de6-11d0-a285-00aa003049e2'
  computer = 'bf967a86-0de6-11d0-a285-00aa003049e2'; organizationalUnit = 'bf967aa5-0de6-11d0-a285-00aa003049e2'
  msa = 'ce206244-5827-4a86-ba1c-1c0c386c1b64'; gmsa = '7b8b558a-93a5-4af7-adca-c017e67f1057'
}
foreach ($guid in $classes.Values) {
  $rule = New-Object System.DirectoryServices.ActiveDirectoryAuditRule($everyone, $rights, 'Success', [guid]::Empty, 'Descendents', [guid]$guid)
  $acl.AddAuditRule($rule)
}
Set-Acl -Path "AD:\$domain" -AclObject $acl
```

Odczyt SACL wymaga uprawnień, których konto usługi nie ma, więc Watchdog nie sprawdzi SACL bezpośrednio. Zamiast tego
liczy pokrycie: jaki odsetek wykrytych zmian udało się przypisać wykonawcy (Stan → Atrybucja, przyczyna
„SACL_MISSING”, gdy brakuje zdarzeń 5136). Do audytu SACL można użyć skryptu Microsoft `Get-ADAuditSaclReport.ps1`.

### 4.7 Certyfikat TLS

Wystaw certyfikat serwera (EKU „Uwierzytelnianie serwera”) z nazwą `admng-wd.contoso.com` w magazynie
`LocalMachine\My` serwera i nadaj gMSA prawo **odczytu klucza prywatnego**: certlm.msc → Osobisty → Certyfikaty →
certyfikat → Wszystkie zadania → Zarządzaj kluczami prywatnymi… → dodaj `CONTOSO\svc-admngwd$` (Odczyt). Skrypt
instalacyjny robi to automatycznie. Po automatycznym odnowieniu certyfikatu nowy klucz dostaje domyślne ACL – w
szablonie AD CS zaznacz „Autoryzuj dodatkowe konta usług w celu uzyskania dostępu do klucza prywatnego” albo ponów
nadanie uprawnień (usługa zgłosi problem w dzienniku Application i w stanie usługi).

### 4.8 Folder danych

Domyślnie `%ProgramData%\AdMng\Watchdog` (`watchdog.db`, `keys\`, `backup\`). Skrypt instalacyjny wyłącza
dziedziczenie i zostawia dostęp tylko dla SYSTEM, Administratorów i konta usługi:

```powershell
icacls "$env:ProgramData\AdMng\Watchdog" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "CONTOSO\svc-admngwd$:(OI)(CI)M"
```

(SID-y zamiast nazw, bo polski Windows ma „Administratorzy” zamiast „Administrators”.) Folder musi leżeć na lokalnym
dysku NTFS (nie na udziale sieciowym).

### 4.9 Instalacja usługi

Rozpakuj artefakt `AdMng-Watchdog-win-x64` i uruchom jako administrator:

```powershell
.\Deploy\Install-AdMngWatchdog.ps1 `
    -ServiceAccount 'CONTOSO\svc-admngwd$' `
    -Url 'https://admng-wd.contoso.com:8443' `
    -CertificateThumbprint '<odcisk>' `
    -AdminGroupSid '<SID AdMng-Watchdog-Admins>' -ViewerGroupSid '<SID AdMng-Watchdog-Viewers>' `
    -AllowedSubnets '10.0.20.0/24'
```

Skrypt: kopiuje pliki do `C:\Program Files\AdMng\Watchdog`, zapisuje `%ProgramData%\AdMng\Watchdog\appsettings.json`,
tworzy folder danych z ACL, źródło dziennika „AdMng Watchdog”, nadaje prawo „Logowanie w trybie usługi”, uprawnienie
do klucza certyfikatu, rejestruje usługę `AdMngWatchdog` (start „automatyczny – opóźniony”, konto gMSA bez hasła,
automatyczny restart po awarii), dodaje regułę zapory dla wskazanych podsieci, sprawdza SPN i uruchamia usługę.
Odpowiednik ręczny:

```powershell
sc.exe create AdMngWatchdog binPath= "C:\Program Files\AdMng\Watchdog\AdMng.Watchdog.exe" start= delayed-auto obj= "CONTOSO\svc-admngwd$" DisplayName= "AdMng Watchdog"
sc.exe sidtype AdMngWatchdog unrestricted
sc.exe failure AdMngWatchdog reset= 86400 actions= restart/60000/restart/60000/restart/300000
sc.exe failureflag AdMngWatchdog 1
New-NetFirewallRule -Name AdMngWatchdog-HTTPS -DisplayName "AdMng Watchdog (HTTPS)" -Direction Inbound -Protocol TCP -LocalPort 8443 -Profile Domain -RemoteAddress 10.0.20.0/24 -Action Allow
```

Jeżeli prawo „Logowanie w trybie usługi” jest zdefiniowane w GPO domeny, dodaj gMSA do tej GPO (wpis w GPO zastępuje
lokalne przypisania).

### 4.10 Plik `appsettings.json` (ustawienia startowe)

Edytowany tylko przez lokalnych administratorów serwera (nie przez API – w ten sposób administrator Watchdoga nie
nada sobie innych uprawnień):

```json
{
  "Watchdog": {
    "DataDirectory": "%ProgramData%\\AdMng\\Watchdog",
    "Authentication": "Negotiate",
    "Hosting": {
      "Server": "Kestrel",
      "Urls": "https://admng-wd.contoso.com:8443",
      "Certificate": { "Thumbprint": "<odcisk>", "Store": "My", "Location": "LocalMachine" },
      "RequireKerberos": true,
      "AllowedRemoteSubnets": [ "10.0.20.0/24" ]
    },
    "Authorization": { "AdminSids": [ "S-1-5-21-…-1302" ], "ViewerSids": [ "S-1-5-21-…-1301" ] },
    "Directory": { "Domain": null, "Server": null, "PreferPdc": true }
  }
}
```

- `Server: "HttpSys"` – alternatywa bez SPN na gMSA; wymaga `netsh http add urlacl url=https://+:8443/
  user=CONTOSO\svc-admngwd$` i `netsh http add sslcert ipport=0.0.0.0:8443 certhash=<odcisk>
  appid={…}` (po każdym odnowieniu certyfikatu powiązanie trzeba odświeżyć).
- `RequireKerberos: true` odrzuca NTLM (ochrona przed przekazywaniem NTLM; .NET 10 nie ma tu powiązania kanału).
- `Directory.Server` pozwala wskazać konkretny DC; domyślnie usługa wybiera emulator PDC.

### 4.11 Pierwsze uruchomienie

1. Po starcie usługa tworzy bazę, zapisuje domyślną konfigurację i wykonuje **zrzut bazowy** (dla ~50 000 obiektów
   kilka minut). Bez powiadomień o zmianach; na koniec zdarzenie „Zakończono zrzut bazowy”.
2. W AD_MNG: **Narzędzia → Watchdog** (Ctrl+Shift+A) → podaj adres `https://admng-wd.contoso.com:8443` → Połącz.
3. Zakładka **Stan**: sprawdź połączenie z DC, kontrolę uprawnień i dostęp do dzienników każdego DC.
4. Zakładka **Kanały i SMTP**: skonfiguruj SMTP, adresy grup odbiorców („Administratorzy IT”, „Helpdesk”), wyślij
   wiadomość testową.
5. Przejrzyj **Reguły** (podgląd „ile powiadomień dałaby ta reguła w ostatnich 7 dniach”) i ustaw godzinę raportu
   dziennego.

---

## 5. Konfiguracja w zakładce Watchdog

### 5.1 Reguły

Reguła określa: rodzaje zdarzeń, rodzaje obiektów, zakres (OU – uwzględnij/wyklucz), opcjonalnie atrybuty, tagi
(np. „konto uprzywilejowane”, „konto serwisowe”), grupy (uprzywilejowane / obserwowane / wskazane), minimalny
priorytet, wykluczenia wykonawców oraz dostarczanie: natychmiast (grupy odbiorców e-mail, kanały Teams/webhook),
raport dzienny, godziny (zawsze / tylko w godzinach pracy / domyślnie: zawsze dla wysokich i krytycznych), okno
zbierania (domyślnie 5 min) i limit wiadomości na godzinę (domyślnie 10; zdarzenia krytyczne bez limitu).

Wbudowane reguły można edytować, wyłączać i przywracać do ustawień domyślnych; reguły „tier0-*” obejmują krytyczne
zmiany bezpieczeństwa. Regułę „Stan usługi Watchdog” można tylko edytować (nie wyłączyć). Przycisk **Podgląd**
pokazuje, ile zdarzeń z ostatnich dni dopasowałaby reguła i ile wiadomości by wysłała.

**Wyjątki** (np. konto synchronizacji Entra Connect, które stale zmienia `msDS-KeyCredentialLink`) wymagają podania
powodu, mogą mieć datę wygaśnięcia, są zapisywane w historii zmian konfiguracji i wymieniane w każdym raporcie
dziennym.

Godziny pracy: domyślnie pon.–pt. 7:00–17:00 (strefa Europe/Warsaw) z polskimi świętami (w tym Wigilia od 2025 r.).
Poza godzinami pracy powiadomienia o priorytecie niższym niż wysoki trafiają tylko do raportu dziennego.

### 5.2 SMTP

| Tryb | Kiedy | Ustawienia |
|---|---|---|
| Przekaźnik bez logowania | Exchange w firmie (łącznik odbioru ograniczony do IP serwera Watchdog) lub Exchange Online przez łącznik przychodzący (certyfikat/IP) na `<tenant>.mail.protection.outlook.com:25` | port 25, „STARTTLS gdy dostępny” lub „STARTTLS”, uwierzytelnianie: brak |
| Logowanie (Basic) | przekaźnik zewnętrzny, Exchange w firmie | port 587, STARTTLS (wymagane), użytkownik + hasło (zapisywane zaszyfrowane, tylko do zapisu) |
| Exchange Online OAuth2 | Exchange Online – **Basic SMTP AUTH jest domyślnie wyłączane od końca grudnia 2026** | `smtp.office365.com:587`, STARTTLS, uwierzytelnianie OAuth2, użytkownik = skrzynka nadawcy |

Przygotowanie Exchange Online OAuth2 (jednorazowo, administrator Entra/Exchange):

1. Entra ID → Rejestracje aplikacji → nowa aplikacja „AdMng Watchdog SMTP”; uprawnienie aplikacji **Office 365
   Exchange Online → SMTP.SendAsApp**, zgoda administratora; dodaj certyfikat (zalecane – klucz prywatny w
   `LocalMachine\My` serwera z odczytem dla gMSA) albo sekret klienta.
2. Exchange Online PowerShell:
   ```powershell
   New-ServicePrincipal -AppId <Application (client) ID> -ObjectId <Object ID z „Aplikacje dla przedsiębiorstw”>
   Add-MailboxPermission -Identity watchdog@contoso.com -User <ServicePrincipal Id> -AccessRights FullAccess
   Set-CASMailbox -Identity watchdog@contoso.com -SmtpClientAuthenticationDisabled $false
   ```
   (Object ID musi pochodzić z „Aplikacje dla przedsiębiorstw”, nie z „Rejestracje aplikacji”.)
3. W zakładce: identyfikator dzierżawy, identyfikator aplikacji, odcisk certyfikatu (lub sekret), nadawca
   `AD Watchdog <watchdog@contoso.com>`.

Przycisk **Wyślij wiadomość testową** pokazuje każdy etap (połączenie, TLS z wersją i szyfrem, logowanie, nadawca,
odbiorcy, treść) z kodami odpowiedzi i podpowiedziami, np. `5.7.139`/`5.7.30` – wyłączone SMTP AUTH,
`5.7.68` – odrzucone „Direct Send”. Certyfikat serwera SMTP jest zawsze weryfikowany (opcjonalnie przypięcie
odcisku SHA-256; „akceptuj każdy” istnieje tylko w trybie deweloperskim).

### 5.3 Microsoft Teams (Workflows)

Stare webhooki „Office 365 Connectors” (`*.webhook.office.com`) zostały wyłączone 18–22 maja 2026 r. – Watchdog je
odrzuca. Używaj **Workflows** (Power Automate):

1. W kanale Teams: … → Przepływy pracy (Workflows) → szablon „Post to a channel when a webhook request is
   received” / „Send webhook alerts to a channel” → skopiuj adres URL.
2. W Power Automate dodaj **współwłaścicieli** przepływu (przepływ należy do osoby, która go utworzyła – po jej
   odejściu przestaje działać) i włącz „Secure inputs/outputs” dla wyzwalacza i akcji publikowania karty (historia
   uruchomień przechowuje treść 30 dni).
3. W zakładce Watchdog → Kanały → Teams: wklej adres (jest sekretem – zawiera podpis `sig`; po zapisaniu widać tylko
   początek adresu), wybierz poziom szczegółowości (domyślnie „Standardowy”) i wyślij kartę testową.

Odpowiedź 202 oznacza tylko przyjęcie przez Workflows, nie publikację w kanale – sprawdź, czy karta testowa się
pojawiła. Przepływy właścicieli bez licencji premium są zawieszane po 90 dniach bez wywołań; codzienny raport
(domyślnie wysyłany także przy braku zmian) temu zapobiega. Tryb „Specific users in my tenant” (token Entra ID
zamiast podpisu w adresie) jest obsługiwany: aplikacja Entra z certyfikatem, zakres
`https://service.flow.microsoft.com//.default`.

### 5.4 Webhook (SIEM, systemy zgłoszeń)

Żądania `POST` JSON zgodne ze **Standard Webhooks v1**: nagłówki `webhook-id` (stały przy ponowieniach – klucz
idempotencji), `webhook-timestamp`, `webhook-signature: v1,<base64 HMAC-SHA256>`; sekret `whsec_…` generuje zakładka
(pokazany jeden raz). Opcjonalnie nagłówek `X-Hub-Signature-256` (styl GitHub). Typy zdarzeń: `ad.user.created`,
`ad.user.locked_out`, `ad.group.member_added`, `ad.privilege.granted`, `watchdog.digest` … Domyślny poziom
szczegółowości „Minimalny” (bez wartości atrybutów). Weryfikacja po stronie odbiorcy (Python):

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

Domyślnie codziennie o 7:30 (poniedziałkowy obejmuje weekend): stan usługi, liczniki, **„Wymaga uwagi”** (zdarzenia
wysokie i krytyczne z informacją o potwierdzeniu), sekcje: nowe konta, nowe komputery (wg OU), usunięcia, włączenia i
wyłączenia, przeniesienia, dostęp uprzywilejowany, członkostwo w grupach, zmiany parametrów kont (stara → nowa
wartość, kto, kiedy), blokady (liczba, komputer źródłowy), hasła, domena i GPO, nadchodzące terminy (≤ 14 dni), a w
poniedziałki także konta nieaktywne (≥ 90 dni) i liczniki higieny (hasło niewymagane, hasło nigdy nie wygasa,
delegowanie nieograniczone, wiek hasła krbtgt). Aktywne wyjątki są wymienione na końcu. Raport wysyłany jest także
przy braku zmian („Brak zmian w okresie”) – jego brak oznacza problem z usługą.

### 5.6 Poziomy szczegółowości

| Poziom | Co zawiera |
|---|---|
| Minimalny | rodzaj zdarzenia, obiekt, priorytet, wykonawca (konto), łącze – bez wartości atrybutów i adresów IP |
| Standardowy | jak wyżej + wartości atrybutów poza danymi osobowymi (telefony, numer pracownika, uwagi, biuro, katalog domowy, godziny logowania) |
| Pełny | wszystkie wartości z wyjątkiem tajnych (hasła, LAPS, BitLocker, atrybuty poufne – nigdy) |

Domyślnie: e-mail do administratorów – pełny, helpdesk – standardowy, Teams – standardowy, webhook – minimalny.

---

## 6. Zakładka Watchdog w AD_MNG

Otwieranie: menu **Narzędzia → Watchdog**, paleta poleceń (Ctrl+K → „Watchdog”) lub **Ctrl+Shift+A**. Zakładka działa
niezależnie od połączenia z AD (zostaje otwarta po zmianie DC); łączy się z usługą przez HTTPS z bieżącym kontem
Windows (działa też `runas /netonly`). AD_MNG nie zapisuje danych Watchdoga na dysku – w ustawieniach zapamiętuje
tylko adres usługi.

| Podzakładka | Zawartość |
|---|---|
| Zdarzenia | lista na żywo (nowe zdarzenia pojawiają się same), filtry (priorytet, rodzaj, obiekt, tekst, daty, niepotwierdzone), szczegóły: zmiany stara → nowa, wykonawca i komputer z poziomem pewności i dowodami (DC, numer zdarzenia), powiązane zdarzenia, wysłane powiadomienia; „Potwierdź”, „Pokaż w AD_MNG”, „Kopiuj”, eksport CSV (tylko na polecenie operatora) |
| Reguły | lista reguł, edytor, wyjątki, podgląd skutków |
| Kanały i SMTP | SMTP, grupy odbiorców, kanały Teams i webhook, wysyłki testowe z diagnostyką |
| Raport dzienny | harmonogram, odbiorcy, podgląd treści, „Wyślij teraz” |
| Stan | połączenie z DC, znacznik USN, zrzut bazowy, kontrola uprawnień, dzienniki każdego DC (pokrycie atrybucji), kanały, baza danych i kopie, dziennik wysyłek (ponawianie), historia zmian konfiguracji |

Rola Viewer widzi wszystko tylko do odczytu; edycja wymaga roli Admin.

---

## 7. Dane, retencja i kopie zapasowe

| Dane | Gdzie | Domyślna retencja |
|---|---|---|
| Migawka obserwowanych atrybutów (bez sekretów) i członkostw | `watchdog.db` | bieżący stan; obiekty usunięte 180 dni |
| Zdarzenia (zmiany, atrybucja, potwierdzenia) | `watchdog.db` | 365 dni |
| Znormalizowane zdarzenia z dzienników DC (wartości tajne zamaskowane) | `watchdog.db` | 30 dni |
| Kolejka i historia wysyłek (treść wiadomości) | `watchdog.db` | wysłane 30 dni, nieudane 90 dni |
| Konfiguracja i historia jej zmian | `watchdog.db` | konfiguracja bieżąca; historia 3 lata |
| Sekrety kanałów (hasło SMTP, sekret OAuth, adresy Teams/webhook, klucze podpisu) | `watchdog.db`, zaszyfrowane Data Protection; klucze w `keys\` chronione DPAPI-NG dla gMSA | do usunięcia |
| Kopie bazy | `backup\` (codziennie 3:15, 7 ostatnich) | 7 kopii |

Retencję ustawia się w konfiguracji. Do odtworzenia potrzebne są **baza i folder `keys\` oraz to samo konto gMSA** –
po usunięciu i ponownym utworzeniu gMSA sekrety trzeba wpisać ponownie. Kopie zapasowe folderu mają te same ACL co
oryginał. Rozmiar: ok. 0,6 KB na zdarzenie (1 000 zdarzeń dziennie ≈ 230 MB rocznie) i ok. 0,6 KB na obiekt migawki.

---

## 8. Bezpieczeństwo

- **Dane osobowe (RODO).** Folder danych i powiadomienia zawierają dane pracowników (nazwiska, działy, zmiany kont).
  Uzgodnij z IOD cele, okres retencji, krąg odbiorców i kanały (Teams i Power Automate przechowują kopie poza
  folderem – historia przepływu 30 dni). Wpisz przetwarzanie do rejestru czynności. Obowiązki z ustawy o KSC
  (NIS2) dotyczące monitorowania i dowodów incydentów mogą przemawiać za dłuższą retencją zdarzeń.
- **Zero sekretów.** Wartości haseł, LAPS, BitLockera, TPM, gMSA i atrybutów oznaczonych w schemacie jako poufne nie
  są odczytywane, zapisywane ani wysyłane.
- **Konto usługi** ma tylko odczyt AD i dzienników; nie ma uprawnień zapisu ani replikacji. Jako gMSA nie ma hasła
  do wycieku.
- **Dostęp do API**: HTTPS, wyłącznie Kerberos (NTLM odrzucany), role wg SID grup AD z lokalnego `appsettings.json`,
  dodatkowy nagłówek wymagany przy zmianach (ochrona przed CSRF z przeglądarki), zapora ograniczona do podsieci
  administratorów.
- **Sekrety kanałów** są tylko do zapisu: API i zakładka nigdy ich nie zwracają; w dziennikach usługi widać wyłącznie
  `schemat://host/ścieżka` adresów.
- **Dzienniki diagnostyczne** usługi (dziennik Application, konsola) nie zawierają wartości z katalogu.
- **Treści z AD są niezaufane**: w e-mailach kodowane HTML, w kartach Teams wyświetlane jako zwykły tekst (bez
  linków Markdown), w webhookach jako JSON.
- **Wyjątki od reguł** są audytowane i widoczne w każdym raporcie dziennym; wyjątki dla reguł krytycznych są
  oznaczane ostrzeżeniem.

---

## 9. Ograniczenia

- Jedna instancja usługi monitoruje jedną domenę (w lesie z wieloma domenami – instancja na domenę).
- Zmiany wykonane na DC w innych lokacjach są widoczne po replikacji międzylokacyjnej (domyślnie do 180 min).
- Automatyczne odblokowanie konta po upływie czasu blokady nie zapisuje niczego w AD – Watchdog może je tylko
  wyliczyć (opcjonalne zdarzenie „wygaśnięcie blokady”).
- Atrybucja wymaga zasad inspekcji, SACL i dostępu do dzienników; bez nich wykonawca jest „nieustalony” (z podaną
  przyczyną). Zmiany wykonane przez ADWS (Centrum administracyjne AD, PowerShell) lub zdalną sesję na DC mogą wskazywać
  jako źródło sam kontroler („lokalnie na DC”).
- `lastLogonTimestamp` jest aktualizowany z opóźnieniem 9–14 dni – próg nieaktywności nie powinien być krótszy niż
  30 dni.
- Bez prawa odczytu `CN=Deleted Objects` usunięcia są wykrywane dopiero przy dobowym uzgodnieniu (jako „obiekt
  zniknął”).
- Zmiany GPO są wykrywane na poziomie obiektu GPO (wersja, powiązania), bez porównywania ustawień.
- Usługa niczego nie blokuje ani nie cofa – tylko raportuje.
- Laboratorium Samba nie ma dzienników zabezpieczeń Windows: atrybucję testuje się na zarejestrowanych zdarzeniach,
  a przed wydaniem na laboratorium Windows Server.

---

## 10. Rozwiązywanie problemów

| Objaw | Przyczyna i rozwiązanie |
|---|---|
| Zakładka: „Usługa odrzuciła uwierzytelnienie Kerberos” (401) | SPN `HTTP/<nazwa>` nie jest na gMSA lub klient używa aliasu CNAME/adresu IP. Sprawdź `setspn -Q HTTP/admng-wd.contoso.com`, używaj nazwy z rekordu A; po zmianach `klist purge` na stacji. |
| „Brak uprawnień” (403) | Konto nie należy do grup z `Authorization` w `appsettings.json` albo bilet Kerberos jest sprzed dodania do grupy (wyloguj się lub `klist purge`). |
| „Usługa nieosiągalna” | Usługa zatrzymana, zapora (port 8443, `AllowedRemoteSubnets`), DNS. Na serwerze: `Invoke-WebRequest https://localhost:8443/healthz -UseBasicParsing` (zwraca „ok”). |
| Błąd certyfikatu | Certyfikat nie pochodzi z zaufanego CA lub nazwa nie zgadza się z adresem. |
| Usługa nie startuje (dziennik Application, źródło „AdMng Watchdog”) | Brak odczytu klucza prywatnego certyfikatu (kod wyjścia 4), baza w nowszej wersji niż program (kod 3), brak prawa „Logowanie w trybie usługi”. |
| Stan: „permission.uSNChanged” | Konto nie widzi części obiektów – nadaj uprawnienia z rozdziału 4.3 (także na AdminSDHolder). |
| Stan: „eventlog.<DC>” z kodem 1722 / 5 | 1722 – zapora „Remote Event Log Management” na DC; 5 – gMSA nie jest w Event Log Readers (lub usługa nie została zrestartowana po dodaniu). |
| Wykonawca „nieustalony – AUDIT_POLICY_MISSING / SACL_MISSING / LOG_OVERWRITTEN / DC_UNREACHABLE” | Odpowiednio: zasady inspekcji (4.5), SACL (4.6), rozmiar dziennika (4.4), dostęp do dziennika DC. |
| E-mail: `535 5.7.139` / `550 5.7.30` | Exchange Online: SMTP AUTH wyłączone dla dzierżawy/skrzynki – przejdź na OAuth2 lub `Set-CASMailbox -SmtpClientAuthenticationDisabled $false`. |
| E-mail: `550 5.7.68` | Dzierżawa odrzuca „Direct Send” – użyj łącznika przychodzącego lub logowania. |
| Teams: „przestarzały adres” | Adres `*.webhook.office.com` – utwórz Workflow (5.3). |
| Teams: 401/403 z tekstem o „Shared Access scheme” lub „OAuth” | Niezgodny tryb uwierzytelniania wyzwalacza (SAS vs Entra) – zmień tryb kanału albo ustawienie przepływu. |
| Brak raportu dziennego | Usługa nie działa, SMTP niesprawne (Stan → Kanały) lub raport wyłączony. |

---

## 11. Środowisko testowe

Laboratorium Samba (`tools/testlab`) i katalog `tools/testlab/watchdog`: uruchomienie usługi w trybie konsolowym
przeciw laboratorium (`run-watchdog-lab.sh`), serwer pocztowy Mailpit z podglądem wiadomości
(http://127.0.0.1:8025), odbiornik webhooków weryfikujący podpisy, skrypt scenariusza zmian w jednorazowym OU (z
automatycznym sprzątaniem) i opcjonalny test prawdziwego Kerberosa. Testy integracyjne:
`dotnet test tests/AdMng.Watchdog.IntegrationTests` (wymagają zmiennych `ADMNG_TEST_LDAP_*`). CI publikuje usługę
jako artefakt `AdMng-Watchdog-win-x64` oraz zrzuty ekranu zakładki Watchdog na gałęzi `screenshots`.
