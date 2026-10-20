# AdMng Watchdog na Linuksie – wdrożenie krok po kroku (Debian + systemd)

Ta instrukcja prowadzi przez całe wdrożenie linuksowej wersji AdMng Watchdog: od domeny testowej, przez przygotowanie
Active Directory i serwera Debian, po pilotaż i wycofanie zmian. Jest pisana dla administratora AD, który najpierw
uruchamia całość w domenie **testowej**, a dopiero potem w produkcji.

- Decyzja i jej uzasadnienie: [ADR-0009](adr/0009-watchdog-na-linuksie.md). Specyfikacja wykonawcza (po angielsku,
  wiążąca dla etapu B): [`dev/watchdog-linux-design.md`](dev/watchdog-linux-design.md).
- Opis funkcji Watchdoga (wykrywane zdarzenia, reguły, kanały, zakładka w AD Managerze): [`WATCHDOG.md`](WATCHDOG.md).
  Jego rozdziały o instalacji na Windows (gMSA, skrypt PowerShell, HTTP.sys, DPAPI-NG) dotyczą wydania 0.2.0 i na
  Linuksie nie mają zastosowania.
- **Stan prac.** Pakiety `admng-watchdog` i `admng-openwec`, narzędzie `admng-watchdog-keytab` i nowe okna
  AD Managera 0.3.0 powstają w etapie B. Wszystko, co od nich zależy, ma oznaczenie **[od 0.3.0]** – na początku
  akapitu albo punktu, którego dotyczy, a w nagłówku na jego końcu (wtedy dotyczy całej sekcji). Takie miejsca
  opisują to, co ustala projekt; wykonasz je dopiero po wydaniu wersji 0.3.0. Zmianę hasła, keytab oraz testy 1 i 3
  z rozdz. 3.6 sprawdziliśmy w laboratorium (Samba AD), test 2 (LDAP z szyfrowaniem GSSAPI) – na testowym serwerze
  OpenLDAP; na kontrolerach Windows potwierdzi je pilotaż. Co możesz zrobić już teraz:

  | Rozdział | Już teraz | Czeka na 0.3.0 |
  |---|---|---|
  | 1 – domena testowa | całość | okno odcisku certyfikatu w AD Managerze (1.6) |
  | 2 – przygotowanie AD | całość | reguły Watchdoga i ostrzeżenia zakładki Stan, o których mówią kroki |
  | 3.1–3.6 – Debian, keytab | całość (keytab procedurą MIT Kerberos) | `admng-watchdog-keytab init` |
  | 3.7 – zmiana hasła | zmiana ręczna przez nowe hasło jednorazowe | timer i automatyczna zmiana co 30 dni |
  | 3.8 – certyfikat | wariant A (gdy jest urząd certyfikacji, w laboratorium – 1.7) | wariant B |
  | 3.9 – kopie | rozmiar dziennika systemowego, kopia maszyny wirtualnej | kopie bazy, scenariusze odtworzenia |
  | 4 – OpenWEC | sprawdzenie zapytania XPath i filtra 4776 na kontrolerach (4.4) | reszta |
  | 5–7 – usługa, AD Manager, pilotaż | – | całość |
  | 8–9 – wycofanie, problemy | części dotyczące rozdz. 1–3 | części dotyczące pakietów |

## Jak czytać tę instrukcję

Każdy krok ma te same części:

- **Gdzie** – na której maszynie i z jakimi uprawnieniami go wykonujesz:
  - **PAW** – stacja administracyjna Tier 0 z RSAT (moduł PowerShell ActiveDirectory, konsola GPMC, narzędzia DNS),
    zalogowane konto z grupy Domain Admins. W domenie testowej rolę PAW pełni ADM-WS01 (1.6).
  - **DC01 / DC02** – konsola kontrolera domeny, PowerShell uruchomiony jako administrator.
  - **Debian** – serwer `admng-wd`, powłoka roota (`sudo -i`) po zalogowaniu przez SSH na własne konto administratora.
  - **ADM-WS01** – stacja administratora z AD Managerem w podsieci 10.0.20.0/24.
- **Co i po co** – co krok zmienia i dlaczego jest potrzebny.
- **Sprawdzenie** – polecenie i oczekiwany wynik. Nie przechodź dalej, dopóki sprawdzenie nie daje tego wyniku.

Przykładowe nazwy i adresy są w całej instrukcji te same. Zamień je na swoje według tabeli poniżej – konsekwentnie we
wszystkich poleceniach i plikach.

Pojęcia, które wracają w całej instrukcji:

| Pojęcie | Znaczenie |
|---|---|
| **Tier 0** | kontrolery domeny i wszystko, co pozwala nimi zarządzać: konta i grupy administratorów, serwery z ich tożsamością, kopie zapasowe |
| **SPN** | nazwa usługi w Kerberosie, np. `HTTP/admng-wd.contoso.com`; kontroler wystawia bilet do usługi zaszyfrowany kluczem konta, do którego należy SPN |
| **keytab** | plik z kluczami Kerberos konta wyprowadzonymi z hasła (bez samego hasła); pozwala usłudze uwierzytelniać się bez człowieka – kto ma ten plik, ma tożsamość konta |
| **kvno** | numer wersji klucza konta – rośnie o 1 przy każdej zmianie hasła; keytab musi zawierać wersję, której używa kontroler |
| **SACL** | lista inspekcji obiektu – mówi kontrolerowi, które operacje na obiekcie zapisać w dzienniku Security |
| **WEF** | Windows Event Forwarding – wbudowany w Windows mechanizm wysyłania zdarzeń z dziennika do kolektora |
| **SSF** | siła szyfrowania połączenia LDAP w bitach (0 = bez szyfrowania) |
| **SASL GSSAPI** | logowanie do LDAP biletem Kerberos, z podpisem i szyfrowaniem połączenia |
| **PTR** | rekord odwrotnego DNS: adres IP → nazwa komputera |
| **kerberoasting** | łamanie offline haseł kont, które mają SPN – na podstawie biletów, o które może poprosić każdy użytkownik |
| **SDProp** | proces na emulatorze PDC, który co godzinę kopiuje uprawnienia z `AdminSDHolder` na konta chronione (`adminCount=1`) |
| **FSMO, emulator PDC** | role wykonywane przez jeden wybrany kontroler; emulator PDC m.in. przyjmuje zmiany haseł i jest źródłem czasu domeny |
| **UBR** | numer poprawki w numerze kompilacji Windows (np. 26100.**4946**) – pokazuje poziom aktualizacji |
| **XPath** | język filtrów, którym subskrypcja WEF wybiera zdarzenia z dziennika |
| **zakładka WEF (bookmark)** | zapamiętane przez kontroler miejsce w dzienniku, do którego wysłał już zdarzenia |
| **tmpfs** (`/dev/shm`, `/run`) | system plików w pamięci RAM – zawartość znika przy restarcie i nie trafia na dysk |
| **umask** | maska domyślnych uprawnień nowych plików (`077` = tylko właściciel) |
| **jednostka / timer systemd** | usługa systemowa Linuksa / jej harmonogram (odpowiednik zadania w Harmonogramie zadań) |
| **journald** (`journalctl`) | dziennik systemowy Linuksa i polecenie do jego przeglądania |
| **nftables** (`nft`) | zapora systemu Linux |
| **chrony** (`chronyc`) | usługa synchronizacji czasu na Debianie i polecenie do sprawdzania jej stanu |
| **conffile** | plik konfiguracji z pakietu `.deb`; gdy go zmienisz, `dpkg` przy aktualizacji pyta, którą wersję zachować |

## Zamień na swoje

| Co | Wartość w przykładach | Gdzie występuje |
|---|---|---|
| Domena DNS / nazwa NetBIOS / obszar Kerberos | `contoso.com` / `CONTOSO` / `CONTOSO.COM` | wszędzie; obszar Kerberos zawsze wielkimi literami |
| DN domeny | `DC=contoso,DC=com` | PowerShell, `ldapsearch` |
| Kontroler 1 (emulator PDC) | `DC01.contoso.com`, 10.0.1.10, Windows Server 2025 | `krb5.conf`, chrony, zapora, OpenWEC, `appsettings.json` |
| Kontroler 2 | `DC02.contoso.com`, 10.0.1.11, Windows Server 2022 | jak wyżej |
| Sieć serwerów i brama | 10.0.1.0/24, brama 10.0.1.1 | sieć Debiana, strefa odwrotna DNS |
| Strefy odwrotne DNS / nazwa PTR serwera Watchdog | `1.0.10.in-addr.arpa` (serwery), `20.0.10.in-addr.arpa` (stacje) / `25` | 1.2, 2.4, 2.13, 8.2 |
| Serwer Watchdog | `admng-wd` / `admng-wd.contoso.com` / 10.0.1.25 | DNS, SPN, certyfikat, zapora |
| Konto usługi (obiekt komputera) | `ADMNG-WD` (`sAMAccountName` `ADMNG-WD$`) | 2.5, keytab, uprawnienia |
| „Sól” kluczy w keytabie (wynika z dwóch wierszy wyżej) | `CONTOSO.COMhostadmng-wd.contoso.com` | 3.6, 3.7 |
| Istniejąca OU Tier 0 | `OU=Tier 0,OU=Admin,DC=contoso,DC=com` | 2.2, 2.9 |
| OU konta usługi | `OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com` | 2.5 |
| OU grup ról | `OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com` | 2.3 |
| Grupy ról Watchdoga | `AdMng-Watchdog-Admins`, `AdMng-Watchdog-Viewers` | 2.3, `appsettings.json` |
| Podsieć stacji administratorów | 10.0.20.0/24 (w laboratorium brama 10.0.20.1) | zapora, `appsettings.json`, 1.6 |
| Stacja administratora (testy) | `ADM-WS01`, 10.0.20.15 | pilotaż – oczekiwane „skąd” |
| Konto administratora (osoba) | `adm-jan` – konto Tier 0 w domenie i lokalne konto na Debianie | 1.5, 2.3, 3.2, pilotaż |
| Grupa uprawniona do SSH na Debianie | `ssh-admins` | 3.2 |
| Przekaźnik SMTP | `smtp.contoso.com:587` (STARTTLS), adres 10.0.1.30 (w laboratorium Mailpit – 1.1) | zapora, zakładka Kanały i SMTP |
| Nadawca powiadomień | `AD Watchdog <watchdog@contoso.com>` | 6.2 |
| Microsoft Teams | przepływ Workflows (adres URL jest sekretem) | 6.2 |
| Adres API Watchdoga | `https://admng-wd.contoso.com:8443` | AD Manager |
| Adres WEF (OpenWEC) | `http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC` | GPO na kontrolerach |
| Urząd certyfikacji (wariant A) | `CA01.contoso.com\Contoso-Issuing-CA` (w laboratorium z 1.7: `DC01.contoso.com\Contoso-Issuing-CA`) | 1.7, 2.1, 2.5, 3.8 |
| OU testowa pilotażu | `OU=Pilot,OU=Firma,DC=contoso,DC=com` | 1.5, 7 |
| Konta testowe | `jan.kowalski`, `anna.nowak`, `józef.żak`, `svc-pilot`, `hd-ewa` | 1.5, 7 |
| Obiekty tworzone w pilotażu | `test.pilot1`, `dmsa-pilot`, `dmsa-pilot2`, GPO „Pilot - test” | 7 |
| Konto gMSA wersji 0.2.0 (tylko przy migracji z usługi Windows) | `svc-admngwd$` | 0.4 (migracja), 2.12 |

Inna nazwa domeny w laboratorium (np. `lab.contoso.com`) jest w porządku, ale zmień wtedy **wszystko** razem: obszar
Kerberos (`LAB.CONTOSO.COM`), DN (`DC=lab,DC=contoso,DC=com`), pełne nazwy (`DC01.lab.contoso.com`,
`admng-wd.lab.contoso.com`), nazwę NetBIOS (np. `LAB`) i „sól” kluczy w keytabie
(`LAB.CONTOSO.COMhostadmng-wd.lab.contoso.com`, rozdz. 3.6).

---

## 0. Przegląd

Przegląd opisuje docelowe działanie wersji 0.3.0. Co możesz wykonać już teraz – tabela w „Stanie prac” na początku
instrukcji.

### 0.1 Co działa gdzie

```text
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ KONTROLERY DOMENY (Tier 0)                                                                     │
│ DC01.contoso.com  10.0.1.10  Windows Server 2025, emulator PDC                                 │
│ DC02.contoso.com  10.0.1.11  Windows Server 2022                                               │
│ dziennik Security ──► usługa WinRM wysyła zdarzenia (WEF, wbudowany w Windows, bez agentów)    │
└──────┬───────────────────────────────────────────▲─────────────────────────────────────────────┘
       │ WEF: HTTP 5985 + Kerberos                 │ LDAP 389 z podpisem i szyfrowaniem (GSSAPI)
       │ (wiadomości szyfrowane)                   │ SMB 445: odczyt GPO z SYSVOL
       │                                           │ Kerberos 88, kpasswd 464, DNS 53, NTP 123
       ▼                                           │
┌──────┴───────────────────────────────────────────┴─────────────────────────────────────────────┐
│ SERWER WATCHDOG: admng-wd.contoso.com  10.0.1.25  Debian 13, poza domeną, serwer Tier 0        │
│ openwecd (OpenWEC) ──► /var/lib/openwec/out/<DC>/security.jsonl (pliki przejściowe)            │
│ admng-watchdog: czyta pliki OpenWEC (kto, skąd), wykrywa zmiany przez LDAP, czyta treść GPO    │
│   historia zmian 3 lata: /var/lib/admng-watchdog (SQLite, tylko konto usługi)                  │
│   powiadomienia: e-mail przez smtp.contoso.com:587 (STARTTLS), Teams Workflows (HTTPS 443)     │
│ /etc/admng-watchdog/admng-wd.keytab: klucze konta ADMNG-WD$, hasło zmieniane co 30 dni         │
└────────────────────────────────────────▲───────────────────────────────────────────────────────┘
                                         │ HTTPS 8443 + Kerberos, tylko z 10.0.20.0/24
┌────────────────────────────────────────┴───────────────────────────────────────────────────────┐
│ STACJE ADMINISTRATORÓW 10.0.20.0/24                                                            │
│ AD Manager, zakładka Watchdog: zdarzenia, reguły, kanały, stan (role z grup AD)                │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Na kontrolerach domeny** niczego nie instalujesz. Zdarzenia z dziennika Security kontrolery wysyłają same,
  wbudowanym mechanizmem Windows Event Forwarding (WEF), do odbiornika OpenWEC na serwerze Watchdog.
- **Serwer Watchdog** to Debian 13 (pakiet obsłuży też Debian 12) **niedołączony do domeny**. Kerberos, DNS i czas
  konfigurujemy na nim ręcznie. Działają tam dwie usługi systemd: `openwecd` (odbiera zdarzenia z DC) i
  `admng-watchdog` (wykrywa zmiany, ustala wykonawców, powiadamia, przechowuje historię) oraz dwa timery: zmiana hasła
  konta usługi i porządkowanie plików OpenWEC.
- **AD Manager** łączy się z usługą przez HTTPS na porcie 8443 z kontem Windows operatora (Kerberos). Role w
  Watchdogu wynikają z członkostwa w grupach AD.

### 0.2 Co Watchdog widzi, a czego nigdy nie robi

Watchdog **widzi**:

- **co** się zmieniło w AD: nowe, zmienione, przeniesione i usunięte konta, grupy, komputery, OU, członkostwa, flagi
  kont, atrybuty krytyczne (delegowanie, SPN, Shadow Credentials, SID History, dMSA), zasady domeny, GPO, relacje
  zaufania, PKI, schemat – zawsze ze starą i nową wartością (pełna lista: [`WATCHDOG.md`](WATCHDOG.md), rozdz. 1);
- **kto** to zrobił i **skąd** – konto wykonawcy z dziennika Security kontrolera (zdarzenia zmian) oraz komputer
  i adres IP, z którego przyszła zmiana – ustalane ze zdarzeń 4624 (logowania sieciowe – typ 3 – i RDP – typ 10), 4625,
  4771 i nieudanych 4776;
- **kiedy** i **na którym DC**;
- **treść GPO** – ustawienia w postaci „ustawienie: było → jest” (nowa funkcja w stosunku do wersji 0.2.0);
- **stan własny**: konto usługi, keytab, odbiór zdarzeń z każdego DC, czas, certyfikat.

Historia zmian (kto, co, kiedy, skąd, było → jest) jest przechowywana **3 lata** (1095 dni). Rekordy z dzienników DC
potrzebne do dopasowania zmian – 30 dni, rekordy logowań i nieudanych logowań (4624/4625/4771/4776) – 48 godzin
(ustalone źródło jest kopiowane do zdarzenia, więc zostaje z nim na 3 lata).

Watchdog **nigdy**:

- nie zmienia niczego w AD poza hasłem własnego konta (co 30 dni) – konto usługi ma nadane wyłącznie prawa odczytu;
  usługa niczego nie blokuje ani nie cofa, tylko raportuje;
- nie odczytuje haseł, LAPS, kluczy BitLocker, haseł gMSA ani atrybutów poufnych; haseł z preferencji GPO
  (`cpassword`) nie zapisuje w żadnej postaci – odnotowuje tylko, że są;
- nie instaluje oprogramowania na kontrolerach domeny;
- nie przyjmuje logowania NTLM (tylko Kerberos) i nie wysyła danych poza skonfigurowane kanały (e-mail, Teams);
- nie zapisuje do dzienników systemowych (journald) wartości z katalogu ani sekretów.

### 0.3 Model bezpieczeństwa w prostych słowach

1. **Serwer Watchdog jest zasobem Tier 0 – jak kontroler domeny.** Kto ma na nim roota, ten ma tożsamość konta usługi
   (odczyt całego katalogu i SYSVOL), 3-letnią historię zmian i może wyciszyć alarmy. Dlatego serwerem administrują
   tylko 1–3 osoby z Tier 0, wyłącznie kluczami SSH i tylko z podsieci administratorów. Kopie zapasowe tej maszyny też
   są zasobem Tier 0.
2. **Konto usługi `ADMNG-WD$` nie jest administratorem.** To zwykły obiekt komputera z prawami odczytu: nie należy do
   żadnej grupy uprzywilejowanej (usługa odmawia startu, jeśli należy), nie może się logować interaktywnie ani przez
   RDP, nie może być delegowane, używa tylko AES. Jego hasło ma 120 losowych znaków, nikt go nie zna i zmienia się co
   30 dni. Przejęcie tego konta daje napastnikowi odczyt katalogu i SYSVOL – to samo, co ma każdy zalogowany
   użytkownik domeny, plus kilka jawnie nadanych praw odczytu. Zapis we własnym obiekcie blokuje wpis odmowy (2.5),
   tworzenie kont komputerów – limit `ms-DS-MachineAccountQuota` = 0 (2.8), wystawienie certyfikatu na to konto –
   wpisy odmowy na szablonach certyfikatów (2.5, gdy w lesie jest urząd Enterprise), a każda zmiana konta i tak jest
   alarmem krytycznym. Wpisy na szablonach są ważne, bo certyfikat pozwala logować się jako to konto bez hasła przez
   cały okres swojej ważności (zwykle rok) – zmiana hasła co 30 dni by go nie unieważniła. Jest jednak wyjątek poza AD: kto ma keytab tego konta, może podrobić bilet Kerberos do API Watchdoga jako
   dowolny użytkownik domeny (tzw. *silver ticket*), także jako administrator Watchdoga, i zmienić reguły albo wyciszyć
   alarmy. Keytab jest wspólny dla usługi i OpenWEC – to świadoma decyzja (4.3); podrobiony bilet zadziała tylko
   z podsieci administratorów, bo tylko z niej API przyjmuje połączenia. Dlatego keytab, konta systemowe, które go
   czytają (usługa i OpenWEC), kopie maszyny i root serwera są zasobami Tier 0, a każda zmiana konfiguracji Watchdoga
   trafia też do dotychczasowych odbiorców (punkt 3).
3. **Administratorzy Watchdoga to nie to samo co Domain Admins.** Grupa `AdMng-Watchdog-Admins` zarządza regułami,
   kanałami i wyjątkami w Watchdogu; jej członkowie nie potrzebują żadnych praw w AD. Członkostwo w Domain Admins nie
   daje żadnej roli w Watchdogu. Powód: osoby, których zmiany są monitorowane, nie powinny po cichu wyłączać
   monitoringu. Domain Admin technicznie może dodać się do grupy Watchdoga – ale ta zmiana sama jest zdarzeniem
   krytycznym, a każda zmiana konfiguracji Watchdoga trafia także do **dotychczasowych** odbiorców. Celem jest
   widoczność, a nie blokada.
4. **Grupy ról leżą w OU Tier 0**, bo kontrola nad nimi to kontrola nad alarmami o Tier 0. Ich członkami zarządza
   wyłącznie Tier 0; Watchdog traktuje je jak grupy uprzywilejowane.
5. **Ustawienia startowe** (kto jest administratorem Watchdoga, z jakich podsieci można się łączyć, czy wolno wyciszać
   zdarzenia krytyczne) leżą w pliku na serwerze, którego usługa nie może zmienić. Zmienia je tylko root serwera.

### 0.4 Kolejność prac

1. Domena testowa – rozdz. 1 (jeśli jej jeszcze nie masz; 1.7 – opcjonalny urząd certyfikacji).
2. AD, część pierwsza – 2.1 (czy jest CA), 2.2 (audyt OU Tier 0), 2.3 (grupy), 2.4 (DNS).
3. Debian – 3.1 do 3.5 (system, konta, zapora, DNS i czas, konfiguracja Kerberos).
4. Konto usługi – 2.5 (utworzenie konta, wpis odmowy, szablony certyfikatów) na PAW i **od razu** 3.6 na Debianie
   (hasło jednorazowe ma żyć kilka minut). GPO z końca 2.5 – dopiero po 3.6.
5. AD, część druga – GPO z końca 2.5, potem 2.6 do 2.12 (po 2.6 i 2.7 wykonaj na Debianie opisane tam testy
   odczytu), na końcu lista kontrolna 2.13. Krok 2.8 (`ms-DS-MachineAccountQuota` = 0) jest warunkiem wdrożenia
   w produkcji (usługa uruchomi się także bez niego, ale zakładka Stan ostrzega – 2.8).
6. Debian – 3.7 (na razie tylko zmiana ręczna), 3.8 (wariant A, jeśli jest urząd certyfikacji), 3.9 (dziennik
   systemowy, kopia maszyny).
7. **[od 0.3.0]** OpenWEC (4), Watchdog (5), AD Manager (6), pozostałe części 3.7–3.9.
8. **[od 0.3.0]** Pilotaż – rozdz. 7.

> **Migracja z wersji 0.2.0 (usługa Windows).** Jeśli działa już Watchdog 0.2.0, usługa Windows pracuje dalej aż do
> przełączenia (po rozdz. 5), a część kroków wykonujesz inaczej – inaczej zepsujesz działającą usługę:
>
> - **2.3** – grup nie twórz (już istnieją), przenieś je do OU Tier 0; SID-y się nie zmieniają:
>   `Move-ADObject -Identity (Get-ADGroup AdMng-Watchdog-Admins).DistinguishedName -TargetPath 'OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com'`
>   i to samo dla `AdMng-Watchdog-Viewers`.
> - **2.4 i 3.1** – Debian dostaje tymczasowy adres (np. 10.0.1.26; w 3.1 także w `/etc/hosts`); rekord A
>   `admng-wd` wskazuje stary serwer aż do przełączenia (2.4, „Rekord wskazuje inny serwer”).
> - **2.5** – utwórz konto **bez** SPN `HTTP/…`: `-ServicePrincipalNames 'HOST/admng-wd.contoso.com', 'HOST/ADMNG-WD'`
>   (kontroler odrzuciłby SPN, który ma jeszcze gMSA `svc-admngwd$`). Keytab z 3.6 i tak zawiera nazwę `HTTP/…` – to
>   ten sam klucz konta. Do przełączenia `setspn -Q HTTP/admng-wd.contoso.com` pokazuje jeszcze gMSA, a w teście 1
>   z 3.6 wiersz `HTTP/…` kończy się błędem – to oczekiwane; wiersz `host/…` musi pokazać `keytab entry valid`.
> - **2.12** – GPO WEF utwórz z wyłączonym połączeniem (`New-GPLink … -LinkEnabled No`); włączasz je przy
>   przełączeniu.
> - **Przełączenie** (po rozdz. 5): zatrzymaj usługę Windows; `setspn -D HTTP/admng-wd.contoso.com svc-admngwd$`;
>   `setspn -S HTTP/admng-wd.contoso.com ADMNG-WD$`; zmień rekord A i PTR `admng-wd` na adres Debiana (2.4) – albo,
>   po wyłączeniu starego serwera, przenieś jego adres na Debiana (`/etc/network/interfaces` i `/etc/hosts`, z konsoli
>   maszyny), wtedy rekordy DNS zostają bez zmian; włącz
>   połączenie GPO WEF (`Set-GPLink -Name 'AdMng - WEF' -Target 'OU=Domain Controllers,DC=contoso,DC=com' -LinkEnabled Yes`);
>   potem 5.3 i 6.1. Na końcu usuń pozostałości starej usługi – „Migracja z wersji 0.2.0” na końcu 2.12.

---

## 1. Domena testowa – przepis

Cel: domena, która odwzorowuje produkcję (las z kontrolerami Windows Server 2025 i 2022, poziom funkcjonalny 2016,
OU Tier 0), na której bezpiecznie przećwiczysz wszystkie kroki tej instrukcji.

### 1.1 Maszyny wirtualne i sieć

| Maszyna | System | vCPU / RAM / dysk | Adres | Rola |
|---|---|---|---|---|
| DC01 – **wymagana** | Windows Server 2025 (wersja ewaluacyjna, Desktop Experience) | 2 / 4 GB / 60 GB | 10.0.1.10 | kontroler, DNS, emulator PDC |
| admng-wd – **wymagana** | Debian 13 netinst | 2 / 4 GB / 60 GB, vTPM opcjonalnie | 10.0.1.25 | serwer Watchdog |
| ADM-WS01 – **wymagana** | Windows 11 Enterprise (ewaluacyjna) lub Windows Server 2025 | 2 / 4 GB / 60 GB | 10.0.20.15 | stacja administratora: RSAT, AD Manager; w laboratorium także PAW. AD Manager działa tylko na Windows, a zmiana zrobiona na samym DC ma jako źródło kontroler – bez tej stacji nie sprawdzisz „skąd” |
| DC02 – opcjonalna | Windows Server 2022 (ewaluacyjna) | 2 / 4 GB / 60 GB | 10.0.1.11 | drugi kontroler – replikacja hasła konta usługi, przełączenie DC, starszy klient WinRM |
| smtp – opcjonalna | Debian 13 netinst z Mailpit | 1 / 1 GB / 10 GB | 10.0.1.30 | przekaźnik SMTP laboratorium (wiadomości oglądasz w przeglądarce) – potrzebny do testów e-mail w 6.2 i 7.2 |

- Wersje ewaluacyjne pobierzesz z Microsoft Evaluation Center. Windows Server 2025 w wersji ewaluacyjnej działa 180
  dni, ale wymaga **aktywacji online w ciągu 10 dni** – bez dostępu do Internetu zacznie się okresowo wyłączać. Daj
  maszynom dostęp do Internetu przez router hiperwizora (NAT) – przykład dla Hyper-V niżej.
- **Sieć – w laboratorium najprościej jedna płaska sieć** 10.0.1.0/24: ADM-WS01 pod adresem 10.0.1.50 (brama 10.0.1.1).
  Wtedy wszędzie, gdzie instrukcja podaje podsieć 10.0.20.0/24 albo adres 10.0.20.15, wpisz `10.0.1.50/32` albo
  `10.0.1.50`: adres i brama w 1.6, zapora Debiana (`ADMIN_NET`, 3.3), `AllowedRemoteSubnets` (5.2), oczekiwane źródło
  w pilotażu (7.2); strefa odwrotna 10.0.20.0/24 z 1.2 jest wtedy zbędna. Dwie podsieci jak w przykładach (10.0.1.0/24
  dla serwerów, 10.0.20.0/24 dla stacji) wymagają routera między nimi (np. małej maszyny z Linuksem z dwiema kartami
  sieciowymi), który przekazuje ruch między podsieciami **bez NAT** (bez maskarady). Debian i kontrolery muszą widzieć
  prawdziwy adres stacji 10.0.20.15 – za NAT-em widziałyby adres routera: zapora Debiana odrzuciłaby SSH i API, a
  pilotaż pokazałby router jako źródło zmian.
- **Sieć w Hyper-V** (na hoście, PowerShell jako administrator) – przełącznik wewnętrzny z NAT dla płaskiej sieci
  10.0.1.0/24; host dostaje adres bramy 10.0.1.1:

  ```powershell
  New-VMSwitch -Name LAB -SwitchType Internal
  New-NetIPAddress -InterfaceAlias 'vEthernet (LAB)' -IPAddress 10.0.1.1 -PrefixLength 24
  New-NetNat -Name LAB -InternalIPInterfaceAddressPrefix 10.0.1.0/24
  ```

  NAT Hyper-V nie odpowiada na zapytania DNS – w 1.2 ustaw przekazywanie zapytań z DC01 na firmowy albo publiczny
  serwer DNS (np. `9.9.9.9`), a nie na 10.0.1.1. Maszyna z Debianem: **generacja 2**, Secure Boot z szablonem
  „Microsoft UEFI Certificate Authority” (domyślny szablon „Microsoft Windows” nie uruchomi Debiana):

  ```powershell
  Set-VMFirmware -VMName admng-wd -SecureBootTemplate MicrosoftUEFICertificateAuthority
  Set-VMKeyProtector -VMName admng-wd -NewLocalKeyProtector; Enable-VMTPM -VMName admng-wd     # vTPM (opcjonalnie)
  ```
- vTPM dla Debiana jest opcjonalny: z nim sprawdzisz wariant „TPM2 + klucz hosta” szyfrowania sekretów (rozdz. 3.9).
- **Przekaźnik SMTP w laboratorium.** Na maszynie `smtp` (Debian, jako root) pobierz Mailpit, sprawdź sumę SHA-256
  i zainstaluj program (wersja i suma jak w `tools/testlab/watchdog/common.sh` – `MAILPIT_VERSION` – i
  `tools/testlab/watchdog/start-mailpit.sh`; tam też znajdziesz nowsze wersje):

  ```bash
  apt install -y curl ca-certificates openssl
  cd /root && curl -fsSLO https://github.com/axllent/mailpit/releases/download/v1.31.5/mailpit-linux-amd64.tar.gz
  echo '5924ace84f38c83c29f941a644a2d7be6c11f339eb4a9a12ab526414897ceed1  mailpit-linux-amd64.tar.gz' | sha256sum -c
  tar -xzf mailpit-linux-amd64.tar.gz mailpit && install -m 0755 mailpit /usr/local/bin/
  ```

  Oczekiwane: `mailpit-linux-amd64.tar.gz: OK`. Potem utwórz stały certyfikat samopodpisany dla `smtp.contoso.com`
  (stały, bo jego odcisk przypniesz w 6.2) i uruchom Mailpit z wymaganym STARTTLS:

  ```bash
  openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj '/CN=smtp.contoso.com' \
      -addext 'subjectAltName=DNS:smtp.contoso.com' -keyout /root/smtp.key -out /root/smtp.crt
  systemd-run --unit=mailpit /usr/local/bin/mailpit --smtp 0.0.0.0:587 --listen 0.0.0.0:8025 \
      --smtp-require-starttls --smtp-auth-accept-any --smtp-tls-cert /root/smtp.crt --smtp-tls-key /root/smtp.key
  ```

  Po restarcie maszyny powtórz polecenie `systemd-run`. Po utworzeniu domeny (1.2) dodaj na DC01 rekord
  `Add-DnsServerResourceRecordA -ZoneName contoso.com -Name smtp -IPv4Address 10.0.1.30 -CreatePtr`. Wiadomości
  oglądasz na `http://10.0.1.30:8025`.
- **Microsoft Teams** jest opcjonalny – wymaga dzierżawy Microsoft 365. Bez niej pomiń kanał Teams w 6.2 i 7.2.

### 1.2 DC01 – nowy las `contoso.com`

**Gdzie:** DC01, PowerShell jako administrator lokalny. **Co i po co:** stały adres, nazwa, rola AD DS i nowy las na
poziomie funkcjonalnym Windows Server 2016 – takim, jaki wymusza w produkcji kontroler Windows Server 2022.

```powershell
# adres i nazwa (nazwę karty sprawdź: Get-NetAdapter)
New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.1.10 -PrefixLength 24 -DefaultGateway 10.0.1.1
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 127.0.0.1
Rename-Computer -NewName DC01 -Restart
```

Po restarcie:

```powershell
Install-WindowsFeature AD-Domain-Services -IncludeManagementTools
Install-ADDSForest -DomainName contoso.com -DomainNetbiosName CONTOSO `
    -ForestMode WinThreshold -DomainMode WinThreshold -InstallDns `
    -SafeModeAdministratorPassword (Read-Host -AsSecureString 'Hasło DSRM')
```

Serwer uruchomi się ponownie jako DC01.contoso.com. Następnie skonfiguruj przekazywanie zapytań DNS na zewnątrz i
strefy odwrotne dla rekordów PTR – serwerów i stacji administratorów (z rekordu PTR Watchdog bierze nazwę komputera,
z którego przyszła zmiana – 7.2). DC01 rejestruje swój rekord PTR dopiero przy następnej rejestracji w DNS – stąd
ostatnie polecenie:

```powershell
Set-DnsServerForwarder -IPAddress 10.0.1.1          # DNS routera laboratorium; przy NAT Hyper-V – np. 9.9.9.9 (1.1)
Add-DnsServerPrimaryZone -NetworkId '10.0.1.0/24'  -ReplicationScope Domain -DynamicUpdate Secure
Add-DnsServerPrimaryZone -NetworkId '10.0.20.0/24' -ReplicationScope Domain -DynamicUpdate Secure   # pomiń w płaskiej sieci (1.1)
ipconfig /registerdns
```

**Sprawdzenie:**

```powershell
Get-ADDomain | Format-List DNSRoot, NetBIOSName, DomainMode, PDCEmulator
Get-ADForest | Format-List ForestMode, SchemaMaster
Resolve-DnsName deb.debian.org
Resolve-DnsName 10.0.1.10
```

Oczekiwane: `DNSRoot : contoso.com`, `NetBIOSName : CONTOSO`, `DomainMode : Windows2016Domain`,
`PDCEmulator : DC01.contoso.com`, `ForestMode : Windows2016Forest`; `Resolve-DnsName` zwraca adresy (przekazywanie
zapytań działa – Debian pobierze przez ten DNS aktualizacje); `Resolve-DnsName 10.0.1.10` zwraca `DC01.contoso.com`
(rekord PTR – po kilku minutach od `ipconfig /registerdns`). Nowa domena na Windows Server 2025 domyślnie
**wymaga podpisywania LDAP** – Watchdog to spełnia (szyfrowanie Kerberos/GSSAPI, rozdz. 3.6).

### 1.3 DC02 – drugi kontroler (opcjonalnie)

**Gdzie:** DC02 (Windows Server 2022). **Co i po co:** drugi kontroler pozwala sprawdzić replikację haseł konta
usługi, przełączenie na inny DC i zachowanie starszego klienta WinRM (WEF).

```powershell
New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.1.11 -PrefixLength 24 -DefaultGateway 10.0.1.1
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.10
Add-Computer -DomainName contoso.com -NewName DC02 -Credential CONTOSO\Administrator -Restart
```

Po restarcie (zaloguj się jako `CONTOSO\Administrator`):

```powershell
Install-WindowsFeature AD-Domain-Services -IncludeManagementTools
Install-ADDSDomainController -DomainName contoso.com -InstallDns -Credential (Get-Credential CONTOSO\Administrator) `
    -SafeModeAdministratorPassword (Read-Host -AsSecureString 'Hasło DSRM')
```

Na koniec ustaw na obu kontrolerach DNS wzajemnie – najpierw partner, potem własny adres pętli zwrotnej:

```powershell
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.11, 127.0.0.1    # na DC01
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.10, 127.0.0.1    # na DC02
ipconfig /registerdns                                                                          # na DC02 (rekord PTR)
```

**Sprawdzenie** (na DC01): `repadmin /replsummary` – brak błędów; `Get-ADDomainController -Filter * | Format-Table
HostName, OperatingSystem, IPv4Address` – dwa kontrolery.

> **Bez DC02.** Jeśli nie budujesz drugiego kontrolera, usuń `DC02.contoso.com` z `krb5.conf` (3.5), z pliku źródeł
> czasu `ad-dcs.sources` i z `resolv.conf` (3.4), adres 10.0.1.11 z `DCS` w zaporze (3.3), `--dc 'DC02$@CONTOSO.COM'`
> przy generowaniu subskrypcji (4.4) i `DC02.contoso.com` z `Directory.Servers` (5.2). Kroki i sprawdzenia dotyczące
> DC02 (m.in. w 2.4, 2.9, 2.10, 2.12, 3.4, 3.6 i 4.6) pomiń.

### 1.4 Czas w domenie

**Gdzie:** DC01. **Co i po co:** emulator PDC jest źródłem czasu dla całej domeny i – w rozdz. 3.4 – dla serwera
Debian. Kerberos odrzuca uwierzytelnianie przy różnicy zegarów powyżej 5 minut. Jeżeli DC01 nie ma żadnego źródła,
Debian (chrony) może uznać go za niezsynchronizowany i odrzucić.

```powershell
w32tm /config /manualpeerlist:"0.pl.pool.ntp.org,0x8 1.pl.pool.ntp.org,0x8" /syncfromflags:manual /reliable:yes /update
Restart-Service w32time
w32tm /resync /rediscover
```

**Sprawdzenie:** `w32tm /query /status` – `Source:` wskazuje serwer NTP (nie `Local CMOS Clock`), `Leap Indicator: 0`.
W produkcji emulator PDC zwykle ma już ustawione źródło – nic nie zmieniaj, tylko sprawdź.

W maszynie wirtualnej Hyper-V wynik `Source: VM IC Time Synchronization Provider` oznacza, że czas narzuca host,
a ustawione serwery NTP są pomijane. Na DC01 wyłącz tego dostawcę i zsynchronizuj ponownie:

```powershell
reg add HKLM\SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\VMICTimeProvider /v Enabled /t REG_DWORD /d 0 /f
Restart-Service w32time; w32tm /resync /rediscover
```

W VMware wyłącz w ustawieniach maszyny „Synchronize guest time with host”.

### 1.5 Struktura OU, konta i grupy testowe

**Gdzie:** DC01, jako `CONTOSO\Administrator` (ADM-WS01 powstaje dopiero w 1.6). **Co i po co:** minimalna struktura z OU Tier 0
(miejsce na konto usługi i grupy ról), OU dla zwykłych kont i OU „Pilot” na testy dMSA oraz kilka kont do testów
z rozdz. 7. W produkcji OU Tier 0 już istnieje – nie twórz nowej, użyj swojej.

```powershell
$d = 'DC=contoso,DC=com'
New-ADOrganizationalUnit -Name 'Admin' -Path $d
New-ADOrganizationalUnit -Name 'Tier 0' -Path "OU=Admin,$d"
'Accounts', 'Groups', 'Service Accounts' | ForEach-Object { New-ADOrganizationalUnit -Name $_ -Path "OU=Tier 0,OU=Admin,$d" }
New-ADOrganizationalUnit -Name 'Firma' -Path $d
'Użytkownicy', 'Grupy', 'Komputery', 'Pilot' | ForEach-Object { New-ADOrganizationalUnit -Name $_ -Path "OU=Firma,$d" }

$pw = Read-Host -AsSecureString 'Hasło kont testowych'
$u  = "OU=Użytkownicy,OU=Firma,$d"
New-ADUser -Name 'Jan Kowalski' -SamAccountName 'jan.kowalski' -UserPrincipalName 'jan.kowalski@contoso.com' -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'Anna Nowak'   -SamAccountName 'anna.nowak'   -UserPrincipalName 'anna.nowak@contoso.com'   -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'Józef Żak'    -SamAccountName 'józef.żak'    -UserPrincipalName 'jozef.zak@contoso.com'    -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'Ewa Helpdesk' -SamAccountName 'hd-ewa'       -UserPrincipalName 'hd-ewa@contoso.com'       -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'svc-pilot'    -SamAccountName 'svc-pilot'    -Path "OU=Pilot,OU=Firma,$d" -AccountPassword $pw -Enabled $true

# konto administracyjne osoby (Tier 0) – w produkcji używasz istniejących kont administratorów
New-ADUser -Name 'adm-jan' -SamAccountName 'adm-jan' -UserPrincipalName 'adm-jan@contoso.com' `
    -Path "OU=Accounts,OU=Tier 0,OU=Admin,$d" -AccountPassword $pw -Enabled $true
Add-ADGroupMember -Identity "$((Get-ADDomain).DomainSID)-512" -Members 'adm-jan'     # Domain Admins (po SID – niezależnie od języka)

New-ADGroup -Name 'Księgowość' -GroupScope Global -GroupCategory Security -Path "OU=Grupy,OU=Firma,$d"
New-ADGroup -Name 'Helpdesk'   -GroupScope Global -GroupCategory Security -Path "OU=Grupy,OU=Firma,$d"
Add-ADGroupMember -Identity 'Helpdesk' -Members 'hd-ewa'
# typowa delegacja Tier 1/2: Helpdesk resetuje hasła w OU Użytkownicy (audyt z 2.2 jej nie zgłosi – jest poza Tier 0)
dsacls "OU=Użytkownicy,OU=Firma,$d" /I:S /G "CONTOSO\Helpdesk:CA;Reset Password;user"
```

Ustaw zasady blokady kont, potrzebne do testu blokady w pilotażu (próg 5 nieudanych prób, 15 minut):

```powershell
Set-ADDefaultDomainPasswordPolicy -Identity contoso.com -LockoutThreshold 5 `
    -LockoutDuration 00:15:00 -LockoutObservationWindow 00:15:00
```

**Sprawdzenie:** `Get-ADUser -Filter * -SearchBase "OU=Firma,$d" | Format-Table SamAccountName, Enabled` – pięć kont,
`Enabled = True`; `Get-ADGroupMember "$((Get-ADDomain).DomainSID)-512"` – zawiera `adm-jan`.

### 1.6 Stacja administratora ADM-WS01

**Gdzie:** ADM-WS01. **Co i po co:** z tej stacji wykonujesz zmiany testowe w pilotażu (Watchdog ma pokazać ją jako
źródło) i łączysz się AD Managerem z usługą.

```powershell
New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.20.15 -PrefixLength 24 -DefaultGateway 10.0.20.1
# w sieci płaskiej (1.1) zamiast wiersza wyżej:
# New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.1.50 -PrefixLength 24 -DefaultGateway 10.0.1.1
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.10, 10.0.1.11
Add-Computer -DomainName contoso.com -NewName ADM-WS01 -Credential CONTOSO\Administrator -Restart
```

Na Windows 11 doinstaluj RSAT (na Windows Server: `Install-WindowsFeature RSAT-AD-PowerShell, GPMC, RSAT-DNS-Server`):

```powershell
'Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0', 'Rsat.GroupPolicy.Management.Tools~~~~0.0.1.0', 'Rsat.Dns.Tools~~~~0.0.1.0' |
    ForEach-Object { Add-WindowsCapability -Online -Name $_ }
```

Zainstaluj AD Manager (wydanie 0.2.0 lub nowsze; okno potwierdzenia certyfikatu Watchdoga ma dopiero
AD Manager 0.3.0). Loguj się na stacji kontem `CONTOSO\adm-jan`.

**Sprawdzenie:** `Get-ADDomain` działa na stacji; `nltest /sc_query:contoso.com` – `Trusted DC Connection Status
Status = 0 0x0 NERR_Success`; po `ipconfig /registerdns` polecenie `Resolve-DnsName 10.0.20.15` (w sieci płaskiej:
`Resolve-DnsName 10.0.1.50`) zwraca `ADM-WS01.contoso.com` (rekord PTR – z niego Watchdog bierze nazwę stacji).

### 1.7 Urząd certyfikacji w laboratorium (opcjonalnie)

**Gdzie:** DC01, PowerShell jako `CONTOSO\Administrator`. **Co i po co:** bez urzędu certyfikacji krok 2.1 zawsze
kończy się wariantem B (certyfikat generowany przez pakiet 0.3.0). Urząd typu Enterprise w laboratorium pozwala już
teraz przećwiczyć wariant A z 3.8 i wpisy odmowy na szablonach certyfikatów z 2.5. **Tylko w laboratorium** – w
produkcji urząd certyfikacji nigdy nie działa na kontrolerze domeny.

```powershell
Install-WindowsFeature ADCS-Cert-Authority -IncludeManagementTools
Install-AdcsCertificationAuthority -CAType EnterpriseRootCA -CACommonName 'Contoso-Issuing-CA' -Force
```

**Sprawdzenie:** polecenie z 2.1 pokazuje urząd `Contoso-Issuing-CA` z `dNSHostName : DC01.contoso.com` i szablonem
`WebServer` na liście `certificateTemplates`. W laboratorium ciąg konfiguracji urzędu to
`DC01.contoso.com\Contoso-Issuing-CA` – wpisuj go wszędzie tam, gdzie instrukcja podaje
`CA01.contoso.com\Contoso-Issuing-CA` (2.1, 3.8).

---

## 2. Przygotowanie AD krok po kroku

Wszystkie polecenia tego rozdziału wykonujesz na **PAW** w PowerShell (Windows PowerShell 5.1 lub PowerShell 7) z
modułem ActiveDirectory, jako członek Domain Admins – chyba że krok mówi inaczej. Żaden krok nie wymaga restartu
kontrolerów.

- **Skrypty zapisywane do plików** (2.2, 2.9) zapisz w kodowaniu **UTF-8 z BOM** (Notatnik: Plik → Zapisz jako →
  Kodowanie „UTF-8 z BOM”; VS Code: „Save with Encoding” → „UTF-8 with BOM”) albo uruchamiaj je w PowerShell 7
  (`pwsh`). Bez BOM Windows PowerShell 5.1 odczyta polskie litery błędnie i skrypt zakończy się błędem składni.
  Polecenia wklejane wprost do okna PowerShell tego problemu nie mają.
- **Nazwy ustawień GPO** podajemy po angielsku – tak, jak w angielskiej konsoli GPMC i w dokumentacji Microsoft. Ścieżki
  podajemy w obu językach; polska konsola pokazuje te same ustawienia w tym samym miejscu drzewa pod przetłumaczonymi
  nazwami. Gdzie się da, podajemy też polecenie PowerShell, które ustawia wartość bez szukania w drzewie.

### 2.1 Czy w domenie jest urząd certyfikacji (AD CS)?

**Gdzie:** PAW. **Co i po co:** od odpowiedzi zależy, skąd usługa weźmie certyfikat TLS dla API (rozdz. 3.8).
Polecenie tylko czyta konfigurację lasu.

```powershell
$cfg = (Get-ADRootDSE).configurationNamingContext
Get-ADObject -SearchBase "CN=Enrollment Services,CN=Public Key Services,CN=Services,$cfg" `
    -LDAPFilter '(objectClass=pKIEnrollmentService)' -Properties dNSHostName, certificateTemplates |
    Format-List Name, dNSHostName, certificateTemplates
```

Jak czytać wynik:

| Wynik | Znaczenie | Dalej |
|---|---|---|
| Pusty | W lesie nie ma urzędu typu Enterprise (w laboratorium bez 1.7 – zawsze). Urzędu Standalone to zapytanie nie pokaże – zapytaj zespół PKI. | **Wariant B** w 3.8 (**[od 0.3.0]**): certyfikat generowany na serwerze, odcisk potwierdzany w AD Managerze. |
| Urząd z `WebServer` na liście `certificateTemplates` | Można wystawić certyfikat serwera WWW. | **Wariant A** w 3.8. Zanotuj ciąg konfiguracji `<dNSHostName>\<Name>`, np. `CA01.contoso.com\Contoso-Issuing-CA`. Wykonaj też wpisy odmowy na szablonach z 2.5. |
| Urząd bez szablonu `WebServer` | Urząd jest, ale nie wystawia certyfikatów serwerów. | Poproś zespół PKI o opublikowanie szablonu (albo jego kopii) z prawem Enroll dla administratorów Tier 0 – albo wariant B. Wpisy odmowy na szablonach z 2.5 wykonaj i tak. |

Gdy urząd jest, sprawdź, czy odpowiada:

```powershell
certutil -config "CA01.contoso.com\Contoso-Issuing-CA" -ping
```

Oczekiwane: `Server "Contoso-Issuing-CA" ICertRequest2 interface is alive` i `CertUtil: -ping command completed
successfully.` (Polecenie `certutil -config - -ping` robi to samo, ale otwiera okno wyboru urzędu.)

### 2.2 Audyt delegacji w OU Tier 0

> **W skrócie.** Co zmienia: nic – skrypt tylko czyta listy ACL; poprawki wprowadzasz sam, po zapisaniu kopii ACL.
> Gdzie: PAW. Ile trwa: kilka minut, plus czas na poprawki. Wynik: lista wpisów do poprawienia albo pusta lista.
> Wycofanie poprawek: z zapisanego pliku SDDL (pod tabelą w 2.13).

**Gdzie:** PAW. **Co i po co:** konto usługi i grupy ról trafią do istniejącej OU Tier 0. Jeżeli ktoś spoza Tier 0
(helpdesk, konto aplikacji, grupa Exchange) ma tam prawa zapisu – choćby odziedziczone z wyższej OU – może przejąć
konto usługi albo zmienić członków grup Watchdoga. Skrypt tylko **czyta** listy ACL: OU Tier 0, wszystkich obiektów pod
nią i `AdminSDHolder` (szablon uprawnień kont chronionych) i wypisuje wpisy, które dają prawa zapisu komuś spoza zbioru
Tier 0, oraz właścicieli spoza Tier 0 (właściciel może zawsze zmienić uprawnienia obiektu).

Zapisz jako `Test-AdMngTier0Delegation.ps1`:

```powershell
# Test-AdMngTier0Delegation.ps1 – audyt delegacji w OU Tier 0 i na AdminSDHolder (tylko odczyt)
param(
    [string]   $Tier0Ou = 'OU=Tier 0,OU=Admin,DC=contoso,DC=com',
    [string[]] $AdditionalTier0Sids = @()      # SID-y własnych grup administratorów Tier 0, jeśli macie takie
)
Import-Module ActiveDirectory
$domain  = Get-ADDomain
$dSid    = $domain.DomainSID.Value
$rootSid = (Get-ADDomain -Identity (Get-ADForest).RootDomain).DomainSID.Value
$rootDse = Get-ADRootDSE
$SidType = [System.Security.Principal.SecurityIdentifier]

# Zbiór Tier 0: SYSTEM, Administrators, Enterprise Domain Controllers, Domain Admins, Domain Controllers, Key Admins,
# Enterprise Read-only DCs, Schema Admins, Enterprise Admins, Enterprise Key Admins
$tier0 = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-9', "$dSid-512", "$dSid-516", "$dSid-526",
           "$rootSid-498", "$rootSid-518", "$rootSid-519", "$rootSid-527") + $AdditionalTier0Sids

# Wpisy zwykle domyślne – są pokazywane z komentarzem, do oceny
$notes = @{
    'S-1-5-10'     = 'SELF: obiekt sam o sobie – zwykle domyślne'
    'S-1-3-0'      = 'CREATOR OWNER: szablon dla nowych obiektów'
    'S-1-5-32-548' = 'Account Operators: domyślne na OU – grupa ma być pusta'
    'S-1-5-32-550' = 'Print Operators: domyślne na OU – grupa ma być pusta'
    'S-1-5-32-561' = 'Terminal Server License Servers: domyślne (atrybut terminalServer)'
    "$dSid-517"    = 'Cert Publishers: domyślne (userCertificate) – traktuj grupę jak Tier 0'
}
$changePassword = [guid]'ab721a53-1e2f-11d0-9819-00aa0040529b'    # User-Change-Password (wymaga starego hasła)
$sendTo         = [guid]'ab721a55-1e2f-11d0-9819-00aa0040529b'    # Send-To (domyślne na grupach)

# Nazwy klas, atrybutów i praw rozszerzonych zamiast GUID-ów
$names = @{}
Get-ADObject -SearchBase $rootDse.schemaNamingContext -LDAPFilter '(schemaIDGUID=*)' -Properties lDAPDisplayName, schemaIDGUID |
    ForEach-Object { $names[([guid]$_.schemaIDGUID).Guid] = $_.lDAPDisplayName }
Get-ADObject -SearchBase "CN=Extended-Rights,$($rootDse.configurationNamingContext)" -LDAPFilter '(objectClass=controlAccessRight)' `
    -Properties displayName, rightsGuid | ForEach-Object { $names[([guid]$_.rightsGuid).Guid] = $_.displayName }
function Get-GuidName([guid]$g, [string]$empty) {
    if ($g -eq [guid]::Empty) { return $empty }
    if ($names.ContainsKey($g.Guid)) { return $names[$g.Guid] }
    return $g.Guid
}
function Get-SidName([string]$sid) {
    try { return ([System.Security.Principal.SecurityIdentifier]$sid).Translate([System.Security.Principal.NTAccount]).Value }
    catch { return $sid }
}
$dangerous = [int][System.DirectoryServices.ActiveDirectoryRights]'CreateChild, DeleteChild, Self, WriteProperty, DeleteTree, ExtendedRight, Delete, WriteDacl, WriteOwner'

$top = (Get-ADOrganizationalUnit -Identity $Tier0Ou).DistinguishedName
$adminSdHolder = "CN=AdminSDHolder,CN=System,$($domain.DistinguishedName)"
$objects = @(Get-ADObject -SearchBase $top -SearchScope Subtree -Filter * -Properties nTSecurityDescriptor) +
           @(Get-ADObject -Identity $adminSdHolder -Properties nTSecurityDescriptor)

$findings = foreach ($o in $objects) {
    $sd = $o.nTSecurityDescriptor
    $isRoot = ($o.DistinguishedName -eq $top) -or ($o.DistinguishedName -eq $adminSdHolder)
    $owner = $sd.GetOwner($SidType).Value
    if ($tier0 -notcontains $owner) {
        [pscustomobject]@{ Obiekt = $o.DistinguishedName; Kto = Get-SidName $owner; SID = $owner
            Prawa = 'WŁAŚCICIEL (może zmienić uprawnienia)'; Dotyczy = ''; Zakres = ''; Odziedziczone = ''; Uwagi = $notes[$owner] }
    }
    # Na samej OU Tier 0 i na AdminSDHolder – wszystkie wpisy; niżej tylko jawne (odziedziczone pokazuje obiekt nadrzędny)
    foreach ($r in $sd.GetAccessRules($true, $isRoot, $SidType)) {
        $sid = $r.IdentityReference.Value
        if ($r.AccessControlType -ne 'Allow' -or $tier0 -contains $sid) { continue }
        if (([int]$r.ActiveDirectoryRights -band $dangerous) -eq 0) { continue }
        $note = $notes[$sid]
        if ($r.ObjectType -eq $changePassword) { $note = 'Zmiana własnego hasła (wymaga starego) – domyślne' }
        if ($r.ObjectType -eq $sendTo) { $note = 'Send To na grupie – domyślne, nie daje zmiany członków' }
        $scope = if (([int]$r.PropagationFlags -band 2) -ne 0) { 'tylko podrzędne: ' + (Get-GuidName $r.InheritedObjectType 'wszystkie') }
                 elseif ($r.InheritanceType -ne 'None') { 'ten obiekt i podrzędne' } else { 'ten obiekt' }
        [pscustomobject]@{ Obiekt = $o.DistinguishedName; Kto = Get-SidName $sid; SID = $sid
            Prawa = $r.ActiveDirectoryRights.ToString(); Dotyczy = Get-GuidName $r.ObjectType '(wszystko)'
            Zakres = $scope; Odziedziczone = $r.IsInherited; Uwagi = $note }
    }
}
foreach ($g in 'S-1-5-32-548', 'S-1-5-32-549', 'S-1-5-32-550', 'S-1-5-32-551') {
    Write-Host ('{0}: członków {1}' -f (Get-SidName $g), @(Get-ADGroupMember -Identity $g).Count)
}
$findings | Sort-Object @{ Expression = { [bool]$_.Uwagi } }, Obiekt
```

Uruchomienie:

```powershell
.\Test-AdMngTier0Delegation.ps1 -Tier0Ou 'OU=Tier 0,OU=Admin,DC=contoso,DC=com' | Out-GridView -Title 'Delegacje Tier 0'
```

Jeśli macie własne grupy administratorów Tier 0 (np. `T0-Admins`), dodaj ich SID-y:
`-AdditionalTier0Sids (Get-ADGroup T0-Admins).SID.Value`. Skrypt nie rozwija członkostwa grup i nie ocenia wpisów
„Odmów”.

**Jak czytać wynik.** Na górze listy są wiersze **bez komentarza** – to właściwe znaleziska:

| Kolumna | Znaczenie |
|---|---|
| `Obiekt` | gdzie jest wpis (OU Tier 0, obiekt pod nią albo AdminSDHolder) |
| `Kto` / `SID` | komu nadano prawo |
| `Prawa` | np. `GenericAll` (pełna kontrola), `WriteProperty`, `ExtendedRight` (np. reset hasła), `WriteDacl` (zmiana uprawnień), `CreateChild` |
| `Dotyczy` | atrybut, klasa lub prawo rozszerzone (np. `User-Force-Change-Password` = reset hasła, `member` = członkowie grupy, `msDS-DelegatedManagedServiceAccount` = tworzenie dMSA); `(wszystko)` = bez ograniczenia |
| `Zakres` | czy wpis działa na tym obiekcie, czy tylko na obiektach podrzędnych danej klasy |
| `Odziedziczone` | `True` = wpis przychodzi z wyższej OU albo z obiektu domeny – poprawiasz go **tam** |

Typowe znaleziska i co z nimi zrobić:

- **Helpdesk z resetem haseł** na OU Tier 0 (często odziedziczony z `OU=Admin` albo z obiektu domeny) – usuń wpis u
  źródła albo wyłącz dziedziczenie na OU Tier 0. Helpdesk nie może resetować haseł kont Tier 0.
- **Grupy Exchange** (np. `Exchange Windows Permissions` z `WriteDacl` na obiekcie domeny) – znane ryzyko; zastosuj
  zalecenia Microsoft dla Exchange (model uprawnień „split permissions” albo odebranie `WriteDacl`).
- **Konta aplikacji lub kopii zapasowych** z `GenericAll` – ogranicz do potrzebnych atrybutów albo przenieś do Tier 0.
- **Właściciel** będący kontem osoby lub grupą spoza Tier 0 – zmień właściciela na Domain Admins.
- **Account Operators / Print Operators** – wpisy są domyślne; grupy mają być **puste** (skrypt wypisuje liczbę
  członków; dotyczy też Server Operators i Backup Operators).

Zanim cokolwiek zmienisz, zapisz obecną listę ACL obiektu (to jest Twój plan wycofania):

```powershell
$dn = 'OU=Tier 0,OU=Admin,DC=contoso,DC=com'
(Get-Acl -Path "AD:\$dn").Sddl | Out-File "$env:USERPROFILE\Documents\acl-tier0-$(Get-Date -Format yyyyMMdd-HHmm).txt"
```

Poprawki: wpisy usuwasz w „Użytkownicy i komputery AD” (Widok → Funkcje zaawansowane → Właściwości → Zabezpieczenia →
Zaawansowane) albo `dsacls "<DN>" /R "CONTOSO\<grupa>"` (usuwa **wszystkie** wpisy tej grupy na tym obiekcie).
Właściciela zmienisz tak:

```powershell
$acl = Get-Acl -Path "AD:\$dn"
$acl.SetOwner([System.Security.Principal.SecurityIdentifier]"$((Get-ADDomain).DomainSID)-512")
Set-Acl -Path "AD:\$dn" -AclObject $acl
```

Konta chronione (`adminCount=1`) co godzinę dostają uprawnienia z `AdminSDHolder` – poprawka na samym koncie bez poprawki
`AdminSDHolder` zostanie nadpisana.

**Sprawdzenie:** ponowne uruchomienie skryptu nie pokazuje wierszy bez komentarza; grupy operatorów mają 0 członków.
Wiersze z komentarzem zostają – to wpisy domyślne każdego obiektu (np. SELF, CREATOR OWNER, Account Operators,
zmiana własnego hasła przez Everyone, „Send To” dla Authenticated Users na grupach). Po 2.3 i 2.5 uruchom skrypt
jeszcze raz: nowe grupy i konto usługi nie mogą dodać wierszy bez komentarza.

**Ćwiczenie w domenie testowej:** nadaj celowo złą delegację i sprawdź, że skrypt ją znajduje, a potem ją usuń:

```powershell
dsacls 'OU=Tier 0,OU=Admin,DC=contoso,DC=com' /I:S /G 'CONTOSO\Helpdesk:CA;Reset Password;user'
.\Test-AdMngTier0Delegation.ps1 | Where-Object Kto -like '*Helpdesk' | Format-List
dsacls 'OU=Tier 0,OU=Admin,DC=contoso,DC=com' /R 'CONTOSO\Helpdesk'
```

### 2.3 Grupy ról `AdMng-Watchdog-Admins` i `AdMng-Watchdog-Viewers`

**Gdzie:** PAW. **Co i po co:** dwie grupy globalne zabezpieczeń decydują o dostępie do Watchdoga w AD Managerze:

- **Admins** – zmieniają reguły, kanały, wyjątki, SMTP i Teams, ponawiają wysyłki (mają też uprawnienia Viewers);
- **Viewers** – przeglądają zdarzenia i stan, potwierdzają zdarzenia; nie widzą wyjątków, adresów odbiorców ani danych
  serwera SMTP.

Przy migracji z wersji 0.2.0 grupy już istnieją – nie twórz ich, tylko przenieś (ramka w 0.4).

```powershell
$g = 'OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com'
New-ADGroup -Name 'AdMng-Watchdog-Admins'  -GroupScope Global -GroupCategory Security -Path $g -Description 'AdMng Watchdog - administratorzy (reguły, kanały)'
New-ADGroup -Name 'AdMng-Watchdog-Viewers' -GroupScope Global -GroupCategory Security -Path $g -Description 'AdMng Watchdog - podgląd i potwierdzanie zdarzeń'
Add-ADGroupMember -Identity 'AdMng-Watchdog-Admins' -Members 'adm-jan'
(Get-ADGroup 'AdMng-Watchdog-Admins').SID.Value      # -> Authorization.AdminSids  (rozdz. 5.2)
(Get-ADGroup 'AdMng-Watchdog-Viewers').SID.Value     # -> Authorization.ViewerSids (rozdz. 5.2)
```

Zasady:

- Członkami są **konta administracyjne** osób (np. `adm-jan`), których używają na stacjach 10.0.20.0/24 – nie konta
  codzienne. Członkowie nie muszą być Domain Admins.
- Nie ustawiaj atrybutu `ManagedBy` (menedżer grupy mógłby sam zmieniać jej członków). Członkami zarządza tylko Tier 0.
- Grupy leżą w OU Tier 0, bo kto kontroluje te grupy, ten może wyciszać alarmy o Tier 0. Watchdog sam zgłasza każdą
  zmianę ich członkostwa jako zmianę dostępu uprzywilejowanego.
- **[od 0.3.0]** Usługa ustala role sama, z AD (członkostwo sprawdza po SID, z pamięcią podręczną 5 minut) – dodanie
  lub usunięcie członka działa w ciągu 5 minut, bez wylogowania. Do natychmiastowego odebrania dostępu służy lista
  `DeniedSids` w ustawieniach startowych (5.2).

**Sprawdzenie:**

```powershell
Get-ADGroup AdMng-Watchdog-Admins -Properties ManagedBy, member | Format-List DistinguishedName, GroupScope, ManagedBy, member
```

Oczekiwane: DN w `OU=Groups,OU=Tier 0,…`, `GroupScope : Global`, `ManagedBy` puste, w `member` konto `adm-jan`.
Zanotuj oba SID-y.

### 2.4 Rekord DNS A (i PTR) dla serwera Watchdog

**Gdzie:** PAW (moduł DnsServer). **Co i po co:** serwer Debian nie jest w domenie, więc sam się w DNS nie zarejestruje.
Kerberos buduje nazwę usługi (SPN) z **nazwy kanonicznej** hosta – dlatego musi to być rekord **A**, nigdy CNAME.
Rekord PTR jest zalecany (diagnostyka, logi DC). Rekord dodany poleceniem jest statyczny – nie zniknie przy
oczyszczaniu nieaktualnych rekordów. Najpierw sprawdź, czy rekord `admng-wd` już istnieje (np. z wersji 0.2.0):

```powershell
Get-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -ErrorAction SilentlyContinue
```

- **Brak wyniku** – dodaj rekordy:

  ```powershell
  # strefa odwrotna – tylko jeśli jej jeszcze nie ma (w domenie testowej powstała w 1.2)
  if (-not (Get-DnsServerZone -ComputerName DC01.contoso.com -Name '1.0.10.in-addr.arpa' -ErrorAction SilentlyContinue)) {
      Add-DnsServerPrimaryZone -ComputerName DC01.contoso.com -NetworkId '10.0.1.0/24' -ReplicationScope Domain
  }
  Add-DnsServerResourceRecordA -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -IPv4Address '10.0.1.25' -CreatePtr
  ```

- **Rekord A wskazuje już adres serwera Debian** (np. przy powtórzeniu tego kroku) – niczego nie dodawaj; jeśli
  w sprawdzeniu niżej brakuje rekordu PTR, dodaj go:
  `Add-DnsServerResourceRecordPtr -ComputerName DC01.contoso.com -ZoneName '1.0.10.in-addr.arpa' -Name '25' -PtrDomainName 'admng-wd.contoso.com'`.
- **Rekord wskazuje inny serwer albo jest aliasem CNAME** (zwykle stary serwer Watchdog 0.2.0 – w jego instrukcji
  pod tym samym adresem 10.0.1.25) – **nie** dodawaj drugiego rekordu A: klienci i kontrolery dostawaliby na przemian oba adresy i połowa połączeń trafiałaby na stary
  serwer. Przy migracji rekord zmieniasz dopiero przy przełączeniu (ramka w 0.4) – wtedy usuń stary rekord i jego PTR
  i dodaj nowe:

  ```powershell
  Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -RRType A -RecordData '<stary adres>' -Force
  Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName '<strefa odwrotna starego adresu>' -Name '<ostatni oktet>' -RRType Ptr -Force
  Add-DnsServerResourceRecordA -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -IPv4Address '<adres Debiana>' -CreatePtr
  ```

  Rekord CNAME usuwasz tak samo, z `-RRType CName` zamiast `-RRType A` (bez `-RecordData`).

**Sprawdzenie:**

```powershell
Resolve-DnsName admng-wd.contoso.com -Type A -Server DC01.contoso.com
Resolve-DnsName 10.0.1.25 -Server DC01.contoso.com
```

Oczekiwane: **dokładnie jeden** wiersz `admng-wd.contoso.com  A  … 10.0.1.25` (typ **A**, bez wiersza CNAME) i
`25.1.0.10.in-addr.arpa  PTR  admng-wd.contoso.com`. Powtórz z `-Server DC02.contoso.com` po replikacji.

W produkcji sprawdź też, że stacje administratorów mają rekordy PTR (`Resolve-DnsName <adres stacji>` zwraca jej
nazwę). Przy logowaniu Kerberosem kontroler nie zapisuje nazwy stacji – Watchdog bierze ją z rekordu PTR; bez niego
pokaże jako źródło tylko adres IP.

### 2.5 Konto usługi `ADMNG-WD$`

> **W skrócie.** Co zmienia: nowy obiekt komputera w OU Tier 0, wpis odmowy dla SELF na tym obiekcie, wpisy odmowy na
> szablonach certyfikatów (tylko gdy w lesie jest urząd Enterprise), a po 3.6 – GPO z odmową logowania. Gdzie: PAW
> (Domain Admin), zaraz potem 3.6 na Debianie. Ile trwa: ok. 15 minut bez GPO. Wynik: konto `ADMNG-WD$` z trzema
> SPN-ami, tylko AES, bez prawa zapisu we własnym obiekcie. Wycofanie: 2.13, wiersze 4, 5 i 13.

#### Czym jest i dlaczego tak

Usługa potrzebuje w domenie tożsamości Kerberos: do LDAP, do SYSVOL, do przyjmowania biletów od AD Managera
(`HTTP/admng-wd.contoso.com`) i od kontrolerów (WEF). Używamy **obiektu komputera** z keytabem:

- konto gMSA odpada – serwer spoza domeny nie pobierze hasła gMSA bez drugiej, osobnej tożsamości;
- konto użytkownika z SPN-em podlega zasadom wygasania haseł i jest typowym celem ataku „kerberoasting”; konto
  komputera z losowym 120-znakowym hasłem jest na to odporne;
- konto komputera i keytab to standardowy wzorzec dla usług Kerberos na Linuksie.

Konto tworzy **administrator Tier 0 na PAW** (nie narzędzie na Debianie – serwer nie potrzebuje żadnych praw do
tworzenia obiektów w AD) z **hasłem jednorazowym**. Na Debianie (3.6) to hasło zostaje od razu zastąpione losowym,
którego nikt nie zna, i powstaje keytab. Narzędzia `msktutil` i `adcli` nie są używane (zapisują w AD przez LDAP i
tworzą pliki tymczasowe); nie instaluj ich na serwerze.

#### Tworzenie

**Gdzie:** PAW, Domain Admin. **Co i po co:** obiekt komputera w OU Tier 0 z trzema SPN-ami, tylko AES (0x18), z flagą
„konto jest poufne i nie może być delegowane” i hasłem jednorazowym. Przed wykonaniem uzgodnij z administratorem
Debiana, że krok 3.6 wykona zaraz potem. Przy migracji z wersji 0.2.0 pomiń w poleceniu SPN `HTTP/…` (ramka w 0.4) –
inaczej kontroler odrzuci utworzenie konta, bo ten SPN ma jeszcze gMSA.

```powershell
$ou = 'OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com'
# hasło jednorazowe: 30 losowych bajtów zapisanych Base64 (40 znaków); działa w PowerShell 5.1 i 7
$bytes = New-Object byte[] 30
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()
$plain = [Convert]::ToBase64String($bytes)
$otp   = ConvertTo-SecureString $plain -AsPlainText -Force
New-ADComputer -Name 'ADMNG-WD' -SamAccountName 'ADMNG-WD$' -Path $ou `
    -DNSHostName 'admng-wd.contoso.com' `
    -ServicePrincipalNames 'HTTP/admng-wd.contoso.com', 'HOST/admng-wd.contoso.com', 'HOST/ADMNG-WD' `
    -KerberosEncryptionType AES128, AES256 `
    -AccountNotDelegated $true -PasswordNotRequired $false `
    -AccountPassword $otp -Enabled $true `
    -Description 'AdMng Watchdog (Debian admng-wd, 10.0.1.25) - konto usługi Tier 0, tylko odczyt'
$plain    # przekaż JEDEN raz administratorowi Debiana (3.6), potem zamknij okno PowerShell
```

Hasło jednorazowe przekaż kanałem, któremu ufacie obaj (ustnie, przez menedżer haseł z usunięciem wpisu) – nie
e-mailem ani komunikatorem. Jest ważne tylko do chwili wykonania 3.6.

SPN-y – po co każdy:

| SPN | Kto o niego prosi |
|---|---|
| `HTTP/admng-wd.contoso.com` | AD Manager (API 8443) i klient WinRM na Windows Server 2022 (WEF) |
| `HOST/admng-wd.contoso.com` | klient WinRM na Windows Server 2025 – według dokumentacji OpenWEC od tej wersji domyślnie prosi o `HOST/`; OpenWEC przyjmuje bilety do obu nazw (ten sam klucz konta, 4.6) |
| `HOST/ADMNG-WD` | krótka nazwa hosta – zwyczajowy SPN konta komputera |

W keytabie (3.6) nazwa `HOST/admng-wd.contoso.com` ma postać `host/admng-wd.contoso.com` – małymi literami. AD nie
rozróżnia wielkości liter w SPN, MIT Kerberos tak; to ta sama nazwa, więc `setspn -L` i `klist -k` pokazują ją różnie.

`New-ADComputer` zapisuje wyłącznie podane SPN-y. Jeśli `setspn -L` pokaże dodatkowo `RestrictedKrbHost/…`, pochodzą
one z innego narzędzia; nie szkodzą, ale zanotuj to przy pilotażu.

**Sprawdzenie:**

```powershell
Get-ADComputer ADMNG-WD -Properties Enabled, AccountNotDelegated, KerberosEncryptionType, msDS-SupportedEncryptionTypes,
    userAccountControl, servicePrincipalName, mS-DS-CreatorSID, memberOf, PrimaryGroupID, msDS-KeyVersionNumber |
    Format-List Name, DistinguishedName, Enabled, AccountNotDelegated, KerberosEncryptionType, msDS-SupportedEncryptionTypes,
        userAccountControl, servicePrincipalName, mS-DS-CreatorSID, memberOf, PrimaryGroupID, msDS-KeyVersionNumber
setspn -L ADMNG-WD
setspn -Q HTTP/admng-wd.contoso.com
setspn -X
```

Oczekiwane:

- `Enabled : True` (gdyby hasło nie spełniło zasad domeny, konto powstałoby wyłączone – usuń je i utwórz ponownie),
- `AccountNotDelegated : True`, `KerberosEncryptionType : {AES128, AES256}`, `msDS-SupportedEncryptionTypes : 24`,
- `userAccountControl : 1052672` (0x101000 = konto stacji roboczej + „nie może być delegowane”); jeśli widzisz
  `1052704` (dodatkowo 0x20 – „hasło niewymagane”), wykonaj
  `Set-ADAccountControl -Identity (Get-ADComputer ADMNG-WD) -PasswordNotRequired $false` i sprawdź ponownie,
- trzy SPN-y z tabeli, `mS-DS-CreatorSID` puste (konto utworzył administrator, nie limit `MachineAccountQuota`),
  `memberOf` puste, `PrimaryGroupID : 515` (Domain Computers),
- `setspn -Q` znajduje `HTTP/admng-wd.contoso.com` **tylko** na `CN=ADMNG-WD,…`; `setspn -X` – `found 0 group of
  duplicate SPNs`. Przy migracji z wersji 0.2.0 SPN `HTTP/…` ma do przełączenia jeszcze gMSA (ramka w 0.4).

#### Blokada zapisu we własnym obiekcie

**Gdzie:** PAW, Domain Admin, zaraz po utworzeniu konta (przed 3.6). **Co i po co:** każde konto komputera domyślnie
może samo tworzyć i usuwać obiekty podrzędne pod własnym obiektem, dopisywać sobie SPN-y i nazwy DNS oraz zmieniać kilka
własnych atrybutów (np. ponownie włączyć RC4 w `msDS-SupportedEncryptionTypes`). Usługa nic z tego nie potrzebuje,
więc odbieramy to wpisem „Odmów” dla SELF. Zmiana hasła protokołem Kerberos korzysta z osobnego prawa „Change
Password”, którego ten wpis nie obejmuje – krok 3.6 od razu to sprawdza.

```powershell
$dn   = (Get-ADComputer ADMNG-WD).DistinguishedName
$acl  = Get-Acl -Path "AD:\$dn"
$self = [System.Security.Principal.SecurityIdentifier]'S-1-5-10'      # SELF
$acl.AddAccessRule((New-Object System.DirectoryServices.ActiveDirectoryAccessRule($self, 'CreateChild, DeleteChild, Self, WriteProperty', 'Deny')))
Set-Acl -Path "AD:\$dn" -AclObject $acl
```

**Sprawdzenie:**

```powershell
(Get-Acl -Path "AD:\$dn").Access | Where-Object AccessControlType -eq 'Deny' | Format-Table IdentityReference, ActiveDirectoryRights
```

Oczekiwane: wiersz `NT AUTHORITY\SELF  CreateChild, DeleteChild, Self, WriteProperty`. Administratorów ten wpis nie
dotyczy (SELF oznacza wyłącznie samo konto `ADMNG-WD$`). **[od 0.3.0]** Usługa co godzinę sprawdza, czy wpis nadal
istnieje (5.4).

#### Szablony certyfikatów (gdy w lesie jest urząd Enterprise)

**Gdzie:** PAW, konto z Enterprise Admins (w lesie z jedną domeną wystarczy Domain Admin) – szablony leżą w partycji
konfiguracji. Tylko gdy 2.1 pokazało urząd typu Enterprise (w laboratorium – po 1.7). **Co i po co:** konto
`ADMNG-WD$` należy do Domain Computers, a ta grupa ma domyślnie prawo zapisu (Enroll) do szablonu „Computer”
(`Machine`), opublikowanego na każdym urzędzie Enterprise. Kto ukradnie keytab, może więc poprosić o certyfikat
komputera dla `admng-wd.contoso.com` i logować się nim jako `ADMNG-WD$` (PKINIT) przez cały okres ważności certyfikatu –
zwykle rok. Zmiana hasła co 30 dni tego nie unieważnia; unieważnia dopiero wyłączenie konta albo odwołanie
certyfikatu. Wpis „Odmów” dla praw Enroll i Autoenroll na każdym szablonie, do którego mogą się zapisywać Everyone,
Authenticated Users albo Domain Computers, zamyka tę drogę. Usługa nie potrzebuje certyfikatu z urzędu (certyfikat API
z 3.8 wnioskuje administrator Tier 0).

```powershell
$cfg     = (Get-ADRootDSE).configurationNamingContext
$wd      = (Get-ADComputer ADMNG-WD).SID
$dSid    = (Get-ADDomain).DomainSID.Value
$broad   = @('S-1-1-0', 'S-1-5-11', "$dSid-515")                     # Everyone, Authenticated Users, Domain Computers
$enroll  = [guid]'0e10c968-78fb-11d2-90d4-00c04f79dc55'              # Certificate-Enrollment
$auto    = [guid]'a05b8cc2-17bc-4802-a710-e7c15ab866a2'              # Certificate-AutoEnrollment
$adr     = [System.DirectoryServices.ActiveDirectoryRights]
$SidType = [System.Security.Principal.SecurityIdentifier]
Get-ADObject -SearchBase "CN=Certificate Templates,CN=Public Key Services,CN=Services,$cfg" `
    -LDAPFilter '(objectClass=pKICertificateTemplate)' | ForEach-Object {
    $p   = "AD:\$($_.DistinguishedName)"
    $acl = Get-Acl -Path $p
    $open = $acl.GetAccessRules($true, $true, $SidType) | Where-Object {
        $_.AccessControlType -eq 'Allow' -and $broad -contains $_.IdentityReference.Value -and (
            $_.ActiveDirectoryRights.HasFlag($adr::GenericAll) -or
            ($_.ActiveDirectoryRights.HasFlag($adr::ExtendedRight) -and $_.ObjectType -in [guid]::Empty, $enroll, $auto)) }
    if ($open) {
        foreach ($g in $enroll, $auto) {
            $acl.AddAccessRule((New-Object System.DirectoryServices.ActiveDirectoryAccessRule($wd, 'ExtendedRight', 'Deny', $g)))
        }
        Set-Acl -Path $p -AclObject $acl
        $_.Name                                                          # szablon, na którym dodano wpis odmowy
    }
}
```

Skrypt wypisuje nazwy szablonów, na których dodał wpis odmowy – na świeżym urzędzie co najmniej `Machine`. Ponowne
uruchomienie niczego nie dubluje; uruchom go znowu po opublikowaniu nowego szablonu, do którego zapisują się szerokie
grupy (Watchdog zgłasza zmiany szablonów – 2.11).

**Sprawdzenie:**

```powershell
Get-ADObject -SearchBase "CN=Certificate Templates,CN=Public Key Services,CN=Services,$cfg" -LDAPFilter '(objectClass=pKICertificateTemplate)' |
    Where-Object { (Get-Acl -Path "AD:\$($_.DistinguishedName)").Access |
        Where-Object { $_.AccessControlType -eq 'Deny' -and $_.IdentityReference -like '*\ADMNG-WD$' } } | Select-Object Name
```

Oczekiwane: te same nazwy, które wypisał skrypt.

**Gdy keytab mógł wyciec:** wyłącz konto (`Disable-ADAccount 'ADMNG-WD$'` – wyłączone konto nie zaloguje się ani
hasłem, ani certyfikatem) i odwołaj certyfikaty wystawione na to konto. Na PAW z narzędziami urzędu:

```powershell
certutil -config 'CA01.contoso.com\Contoso-Issuing-CA' -view -restrict 'RequesterName=CONTOSO\ADMNG-WD$' -out 'RequestID,SerialNumber,NotAfter'
certutil -config 'CA01.contoso.com\Contoso-Issuing-CA' -revoke <SerialNumber> 1      # 1 = przejęcie klucza; dla każdego numeru z listy
```

Dopiero potem nowe hasło jednorazowe i 3.7 („zmiana ręczna”) albo usunięcie konta (8.2).

#### Co to konto może, a czego nie

| Może | Nie może |
|---|---|
| uwierzytelnić się w domenie Kerberosem (tylko AES) | zalogować się interaktywnie, przez RDP, jako zadanie lub usługa (GPO) |
| czytać katalog (jak każdy uwierzytelniony podmiot + jawne prawa odczytu z 2.6) | zapisywać czegokolwiek w cudzych obiektach AD ponad to, co może każde uwierzytelnione konto (wiersz niżej); replikować katalogu (DCSync) |
| czytać SYSVOL (GPO) | czytać haseł, LAPS, kluczy BitLocker, haseł gMSA, atrybutów poufnych |
| zmienić własne hasło (protokół Kerberos, port 464) | być delegowane (usługi nie mogą działać w jego imieniu) |
| przyjmować bilety do `HTTP/` i `HOST/admng-wd.contoso.com` | zmienić własnego `userAccountControl`, a dzięki wpisowi odmowy – także innych własnych atrybutów, SPN-ów i obiektów podrzędnych |
| dopóki `ms-DS-MachineAccountQuota` > 0 – utworzyć do 10 kont komputerów, jak każde konto w domenie (2.8 ustawia 0) | uzyskać certyfikatu z urzędu Enterprise (wpisy odmowy na szablonach – wyżej) |
| jak każde uwierzytelnione konto – dodać nowy rekord w strefie DNS zintegrowanej z AD (domyślne prawo Authenticated Users) | |

Dodawanie rekordów DNS nie daje kontu nic ponad to, co ma każdy zalogowany użytkownik domeny, a Watchdog nie śledzi
partycji DNS. Jeśli chcesz odebrać to prawo także temu kontu, dodaj wpis odmowy na każdej strefie zintegrowanej z AD
(także na strefach odwrotnych i na `_msdcs.contoso.com` w `ForestDnsZones`), np.:
`dsacls "DC=contoso.com,CN=MicrosoftDNS,DC=DomainDnsZones,DC=contoso,DC=com" /D "CONTOSO\ADMNG-WD$:CC"`.

Kto ma keytab, ma tę tożsamość – i może też podrobić bilet do API Watchdoga (0.3, punkt 2). **[od 0.3.0]**
Niezależnie od wpisów odmowy Watchdog zgłasza **każdą** zmianę konta `ADMNG-WD$` jako krytyczną (reguła
`watchdog-self`) – poza jego własną, zaplanowaną zmianą hasła.

#### Jak działa zmiana hasła

- Hasło ma 120 losowych znaków, istnieje tylko w pamięci w chwili zmiany i nigdzie nie jest zapisywane – na dysku leży
  wyłącznie keytab z kluczami Kerberos wyprowadzonymi z hasła.
- **[od 0.3.0]** Codzienny timer na Debianie sprawdza wiek hasła; gdy ma ponad 30 dni, zmienia je protokołem Kerberos
  (port 464 na emulatorze PDC). Nie potrzeba do tego żadnych praw w AD ani zapisów LDAP. Do tego czasu hasło zmieniasz
  ręcznie (3.7).
- **[od 0.3.0]** Keytab zawiera klucze **bieżącej i poprzedniej** wersji (kvno) – bilety wystawione przed zmianą
  nadal działają, a DC02 może przez chwilę, do replikacji, używać starego klucza. (Keytab z ręcznej procedury z 3.6
  ma na początku jedną wersję, a po zmianie ręcznej z 3.7 – wszystkie dotychczasowe.)
- **[od 0.3.0]** Nieudana zmiana niczego nie psuje: hasło i keytab zostają, timer spróbuje następnego dnia. Usługa
  ostrzega, gdy hasło ma ponad 32 dni, i zgłasza błąd po 35 dniach.
- Kontrolery domeny nie wymuszają wygasania haseł kont komputerów – 30 dni to nasza zasada, nie limit domeny.
- Zmiana hasła unieważnia stare klucze, ale nie certyfikaty wystawione na konto – dlatego wpisy odmowy na szablonach.

#### Ograniczenie logowania konta (GPO) – po 3.6

**Kiedy:** dopiero po 3.6 – hasło jednorazowe ma żyć jak najkrócej. **Gdzie:** PAW, konsola GPMC. **Co i po co:**
konto usługi nigdy nie loguje się interaktywnie. GPO z odmową logowania lokalnego, przez RDP, wsadowego i jako usługa
sprawia, że ktoś, kto zdobyłby keytab, nie użyje tej tożsamości do logowania na serwery i stacje. Logowania
sieciowego **nie** blokujemy – usługa go potrzebuje (LDAP, SMB).

Prawa użytkownika z kilku GPO **nie łączą się**: na danym komputerze obowiązuje lista z jednego GPO – tego o
najwyższym priorytecie spośród GPO, które to prawo definiują (GPO połączone z OU wygrywa z GPO połączonym z domeną).
Lista z GPO zastępuje też w całości listę lokalną komputera. Dlatego:

1. Nowe GPO połączone z domeną definiuje **wszystkie cztery** prawa – to obejmuje komputery, na których żadne inne GPO
   tych praw nie definiuje.
2. Do **każdego** istniejącego GPO, które definiuje któreś z tych praw (np. GPO modelu warstwowego połączone z OU
   serwerów), dopisz `CONTOSO\ADMNG-WD$` w prawach, które to GPO definiuje – na komputerach w jego zasięgu wygrywa ono.
   Takie GPO znajdziesz tak:

```powershell
Get-GPO -All | ForEach-Object {
    $x = Get-GPOReport -Guid $_.Id -ReportType Xml
    if ($x -match 'SeDeny(Interactive|RemoteInteractive|Batch|Service)LogonRight') { $_.DisplayName }
}
```

Nowe GPO:

```powershell
$gpo = New-GPO -Name 'AdMng - ograniczenia konta Watchdog' -Comment 'Odmowa logowania lokalnego, RDP, wsadowego i jako usługa dla CONTOSO\ADMNG-WD$'
New-GPLink -Guid $gpo.Id -Target 'DC=contoso,DC=com' -LinkEnabled Yes
```

W GPMC edytuj GPO: Konfiguracja komputera → Zasady → Ustawienia systemu Windows → Ustawienia zabezpieczeń → Zasady
lokalne → Przypisywanie praw użytkownika (Computer Configuration → Policies → Windows Settings → Security Settings →
Local Policies → User Rights Assignment) i w czterech ustawieniach – „Deny log on locally”, „Deny log on through Remote
Desktop Services”, „Deny log on as a batch job”, „Deny log on as a service” – zaznacz „Define these policy settings” i
dodaj `CONTOSO\ADMNG-WD$`. W oknie wyboru obiektu kliknij „Object Types” i zaznacz „Computers” – inaczej konto
komputera nie zostanie znalezione. W „Deny log on locally” dodaj też wbudowaną grupę `Guests` (w polskim Windows:
Goście) – domyślna lista lokalna zawiera konto Gość, a GPO ją zastępuje. **Nie** dodawaj konta do „Deny access to this
computer from the network”.

**Sprawdzenie** (po `gpupdate /force`) na ADM-WS01 i na jednym komputerze w każdej OU, z którą połączone jest GPO
znalezione wyżej:

```powershell
$sid = (Get-ADComputer ADMNG-WD).SID.Value
secedit /export /areas USER_RIGHTS /cfg "$env:TEMP\ur.inf" | Out-Null
Select-String -Path "$env:TEMP\ur.inf" -Pattern 'SeDeny(Interactive|RemoteInteractive|Batch|Service)LogonRight' |
    ForEach-Object { '{0}: {1}' -f $_.Line.Split('=')[0].Trim(), ($_.Line -match [regex]::Escape($sid)) }
Remove-Item "$env:TEMP\ur.inf"
```

Oczekiwane: cztery wiersze zakończone `True`.

### 2.6 Prawa odczytu dla konta usługi

**Gdzie:** PAW, Domain Admin. **Co i po co:** w wielu domenach każde konto czyta obiekty dzięki grupie „Pre-Windows 2000
Compatible Access”. W domenach utwardzonych tak nie jest, a obiekty i atrybuty, których konto nie może odczytać (np.
atrybuty `uSNChanged`, `lockoutTime`), **po cichu znikają** ze śledzenia. Dlatego prawa odczytu nadajemy jawnie –
zawsze, także w domenie testowej. To te same polecenia co w wersji Windows ([`WATCHDOG.md`](WATCHDOG.md) 4.3), tylko
dla konta `ADMNG-WD$`.

```powershell
$d = 'DC=contoso,DC=com'; $a = 'CONTOSO\ADMNG-WD$'
$classes = 'user', 'inetOrgPerson', 'computer', 'group', 'organizationalUnit', 'contact',
           'msDS-ManagedServiceAccount', 'msDS-GroupManagedServiceAccount', 'msDS-DelegatedManagedServiceAccount'
$schema = (Get-ADRootDSE).schemaNamingContext
foreach ($c in $classes) {
    # klasa dMSA istnieje tylko w schemacie Windows Server 2025 – brakujące klasy pomijamy
    if (Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=$c)") { dsacls $d /I:S /G "${a}:GR;;$c" }
}
dsacls $d /G "${a}:GR"                                                    # obiekt domeny: zasady haseł i blokad, FSMO, MachineAccountQuota
dsacls "CN=AdminSDHolder,CN=System,$d" /G "${a}:GR"                       # konta chronione (adminCount=1) dziedziczą stąd ACL (SDProp, do 60 min)
dsacls "CN=Password Settings Container,CN=System,$d" /I:T /G "${a}:GR"    # szczegółowe zasady haseł (PSO) – właściwy czas blokady
```

Dlaczego osobno dla każdej klasy: wpisy dziedziczone „dla obiektów klasy X” dotyczą klasy strukturalnej obiektu, więc
konta `inetOrgPerson` i dMSA nie są objęte wpisem dla `user`.

**Usunięte obiekty** (zalecane – bez tego usunięcia są wykrywane dopiero przy dobowym uzgodnieniu jako „obiekt
zniknął”):

```powershell
dsacls "CN=Deleted Objects,$d" /takeownership      # zmienia właściciela kontenera systemowego – uzgodnij z zespołem AD
dsacls "CN=Deleted Objects,$d" /G "${a}:LCRP"
```

**`tokenGroups` (role w API).** Usługa ustala role operatorów, czytając atrybut `tokenGroups` ich kont. Prawo do
niego daje wpis `GR` dla klas `user`, `inetOrgPerson` i `computer` z tego kroku – także w domenie utwardzonej. Nie
dodawaj konta do grupy Windows Authorization Access Group: ta grupa daje odczyt innego atrybutu
(`tokenGroupsGlobalAndUniversal`), z którego usługa nie korzysta. `ADMNG-WD$` nie należy do żadnej grupy poza Domain
Computers.

Uprawnienia do **zapisu** w AD nie są potrzebne. Usługa po starcie i raz na dobę sprawdza, czy widzi wszystko, czego
potrzebuje (zakładka Stan → „Kontrola uprawnień”).

**Sprawdzenie:**

```powershell
dsacls 'DC=contoso,DC=com' | Select-String 'ADMNG-WD'
dsacls 'CN=AdminSDHolder,CN=System,DC=contoso,DC=com' | Select-String 'ADMNG-WD'
```

Oczekiwane: kilka wierszy `Allow CONTOSO\ADMNG-WD$` – na obiekcie domeny także z dopiskiem `<Inherited to user>`,
`<Inherited to computer>` itd.; na AdminSDHolder co najmniej jeden.

**Sprawdzenie z Debiana** (root; konto usługi i keytab z 3.6 już są – kolejność z 0.4). Odczyt `tokenGroups` sprawdź
na zwykłym koncie (bez `adminCount=1`), np. przyszłego członka `AdMng-Watchdog-Viewers`: konta chronione (np. Domain
Admins) dostają uprawnienia z AdminSDHolder, gdzie każdy uwierzytelniony może czytać wszystkie atrybuty, więc test na
nich przechodzi zawsze. Wiersz z `Deleted Objects` dotyczy tylko sytuacji, gdy nadałeś prawa do usuniętych obiektów.

```bash
export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=FILE:/dev/shm/admng-test-cc
export KRB5_CLIENT_KTNAME=FILE:/etc/admng-watchdog/admng-wd.keytab LDAPCONF=/etc/admng-watchdog/ldap.conf
kdestroy 2>/dev/null
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -s base \
    -b 'CN=Ewa Helpdesk,OU=Użytkownicy,OU=Firma,DC=contoso,DC=com' tokenGroups | head -3
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -s base -E '!1.2.840.113556.1.4.417' \
    -b 'CN=Deleted Objects,DC=contoso,DC=com' dn
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -s base \
    -b 'CN=Password Settings Container,CN=System,DC=contoso,DC=com' dn
kdestroy; unset KRB5_CLIENT_KTNAME LDAPCONF
```

Oczekiwane: przy `Ewa Helpdesk` wiersze `tokenGroups:: …`, w dwóch pozostałych zapytaniach wiersz `dn: …`. Brak tych
wierszy (pusty wynik albo `No such object (32)`) znaczy, że brakuje praw z tego kroku – sprawdź polecenia `dsacls`
i powtórz test po kilku minutach. W produkcji DN zwykłego konta znajdziesz poleceniem
`ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -b 'DC=contoso,DC=com' '(sAMAccountName=<konto>)' dn`
(z tymi samymi `export`).

### 2.7 Odczyt SYSVOL (treść GPO)

**Gdzie:** PAW i DC01. **Co i po co:** Watchdog czyta pliki GPO z udziału `SYSVOL` swojego kontrolera (tylko odczyt,
SMB 3 z Kerberosem i podpisywaniem), żeby pokazywać zmiany ustawień „było → jest”. Zwykle nie trzeba niczego nadawać:
odczyt SYSVOL i folderów GPO mają Authenticated Users i Domain Computers, a `ADMNG-WD$` należy do obu. Problem robią
GPO, którym ktoś odebrał odczyt tym grupom (filtrowanie zabezpieczeń) – wtedy ich folder jest dla usługi niewidoczny.

**Sprawdzenie** (PAW, moduł GroupPolicy) – GPO bez odczytu dla Authenticated Users i Domain Computers:

```powershell
$dSid = (Get-ADDomain).DomainSID.Value
Get-GPO -All | Where-Object {
    -not (Get-GPPermission -Guid $_.Id -All | Where-Object {
        $_.Trustee.Sid.Value -in @('S-1-5-11', "$dSid-515") -and $_.Permission -in 'GpoRead', 'GpoApply' })
} | Select-Object DisplayName, Id
```

Oczekiwane: brak wyników. Wymienionym GPO dodaj odczyt dla Domain Computers – nazwę grupy bierzemy po SID, bo w domenie
utworzonej na polskim Windows Server grupa nazywa się „Komputery domeny”. Dodanie samego odczytu nie zmienia, do kogo
GPO się stosuje:

```powershell
$dc = (Get-ADGroup "$((Get-ADDomain).DomainSID)-515").Name
Set-GPPermission -Guid <Id> -TargetName $dc -TargetType Group -PermissionLevel GpoRead
```

Na DC01: `Get-SmbShareAccess -Name SYSVOL` – udział musi dawać odczyt grupie Everyone albo Authenticated Users (stan
domyślny). Na Debianie powtórz Test 3 z 3.6 (odczyt SYSVOL; blok testu zawiera potrzebne polecenia `export`).

### 2.8 `ms-DS-MachineAccountQuota` = 0

**Gdzie:** PAW. **Co i po co:** domyślnie każde konto domeny – użytkownika, ale też komputera, czyli także
`ADMNG-WD$` – może utworzyć w domenie do 10 kont komputerów (dołączyć komputery), a takie konta bywają potem używane
w atakach (np. delegowanie oparte na zasobach). Wartość 0 jest **warunkiem wdrożenia** Watchdoga w produkcji: bez
niej ktoś, kto zdobędzie keytab, mógłby tworzyć konta komputerów. Komputery dołączają wtedy wyłącznie osoby z jawnie
delegowanym prawem tworzenia obiektów komputerów albo osoby, którym administrator przygotował konto komputera (niżej).
Jeśli nie możecie ustawić 0 od razu, zapiszcie to jako zaakceptowane ryzyko.

**[od 0.3.0]** Zakładka Stan ostrzega, dopóki wartość jest większa od 0, a Watchdog zgłasza **podniesienie** tej
wartości z 0 jako zdarzenie krytyczne (reguła `tier0-machine-quota`).

Najpierw sprawdź stan i kto dziś korzysta z limitu:

```powershell
$dn = (Get-ADDomain).DistinguishedName
(Get-ADObject $dn -Properties 'ms-DS-MachineAccountQuota').'ms-DS-MachineAccountQuota'      # zanotuj – to wartość do wycofania
Get-ADComputer -LDAPFilter '(mS-DS-CreatorSID=*)' -Properties mS-DS-CreatorSID, whenCreated |
    Select-Object Name, whenCreated, @{ n = 'Utworzył'; e = {
        $s = $_.'mS-DS-CreatorSID'      # w bloku catch $_ to już błąd, nie komputer – dlatego zmienna
        try { ([System.Security.Principal.SecurityIdentifier]$s).Translate([System.Security.Principal.NTAccount]).Value }
        catch { "$s" } } } | Sort-Object whenCreated
```

Komputery z listy dołączyli zwykli użytkownicy. Jeśli w ostatnich miesiącach to się zdarza (np. serwis dołącza laptopy
własnym kontem), najpierw daj tym osobom – najlepiej grupie – prawo tworzenia obiektów komputerów, a dopiero potem
ustaw 0:

```powershell
(Get-ADDomain).ComputersContainer        # tu trafiają komputery dołączane bez wskazania OU
dsacls 'OU=Komputery,OU=Firma,DC=contoso,DC=com' /I:T /G 'CONTOSO\<grupa>:CC;computer'
```

- Dołączenie bez wskazania OU (`Add-Computer` bez `-OUPath`, okno „Zmiana nazwy komputera”) tworzy obiekt w kontenerze
  z pierwszego polecenia – zwykle `CN=Computers,DC=contoso,DC=com`. Albo przekieruj to miejsce do OU komputerów
  (na DC: `redircmp "OU=Komputery,OU=Firma,DC=contoso,DC=com"`), albo nadaj to samo prawo także tam:
  `dsacls 'CN=Computers,DC=contoso,DC=com' /G 'CONTOSO\<grupa>:CC;computer'`.
- To samo prawo nada kreator „Delegate Control” w konsoli „Użytkownicy i komputery AD” (zadanie niestandardowe →
  „Computer objects” → „Create selected objects in this folder”).
- **Konta komputerów przygotowane przez administratora.** Tworząc obiekt komputera z wyprzedzeniem (ADUC: Nowy →
  Komputer), wskaż w polu „The following user or group can join this computer to a domain” osobę albo grupę, która
  będzie dołączać – dostaje wtedy prawa potrzebne do dołączenia (reset hasła, zapis nazwy DNS i SPN, ograniczenia
  konta). Od aktualizacji KB5020276 dołączenie do istniejącego konta komputera jest dozwolone tylko jego twórcy, gdy
  konto utworzył członek Domain Admins, albo gdy właściciel konta jest na liście zasady „Domain controller: Allow
  computer account re-use during domain join” – konto przygotowane przez administratora domeny spełnia ten warunek.

```powershell
Set-ADDomain -Identity contoso.com -Replace @{ 'ms-DS-MachineAccountQuota' = 0 }
```

**Sprawdzenie:** polecenie odczytu z początku tego kroku zwraca `0`. Skutek: dołączenie komputera przez zwykłego
użytkownika bez delegacji kończy się odmową dostępu. **Wycofanie:** `Set-ADDomain` z poprzednią wartością
(domyślnie `10`); delegację usuwasz `dsacls '<DN>' /R 'CONTOSO\<grupa>'`.

### 2.9 Ekspozycja na BadSuccessor (dMSA) i poziom poprawek

**Gdzie:** PAW i DC01. **Co i po co:** konta dMSA (Windows Server 2025) mogą „zastąpić” inne konto przez atrybut
`msDS-ManagedAccountPrecededByLink`. Badacze Akamai pokazali (atak „BadSuccessor”), że kto może **utworzyć dMSA** w
dowolnej OU, mógł w ten sposób uzyskać uprawnienia dowolnego konta, także Domain Admins. Microsoft zmienił działanie
KDC w aktualizacji z sierpnia 2025 (CVE-2025-53779) – ale samo prawo tworzenia dMSA w rękach osób spoza Tier 0 pozostaje
ryzykiem. Domena jest narażona, gdy ma choć jeden kontroler Windows Server 2025 (u nas DC01).

**[od 0.3.0]** Watchdog zgłasza każde utworzenie dMSA i każdą zmianę powiązania migracji jako krytyczne (reguła
`tier0-badsuccessor`).

**Gdzie może powstać dMSA i kto może je tworzyć.** Skrypt tylko czyta. Najpierw ustala ze schematu, pod obiektami
jakich klas może powstać dMSA – możliwi rodzice są dziedziczeni po klasach nadrzędnych (dMSA → `computer` → `user` →
`organizationalPerson` → `person` → `top`), więc sama definicja klasy dMSA ich nie pokazuje. Potem czyta ACL wszystkich
obiektów tych klas w domenie i pokazuje podmioty spoza Tier 0 z prawem `CreateChild` (dla wszystkich klas albo dla
klasy dMSA), `GenericAll`, `WriteDacl`, `WriteOwner` oraz właścicieli spoza Tier 0. Zapisz jako
`Test-AdMngDmsaCreators.ps1` i uruchom `.\Test-AdMngDmsaCreators.ps1` (własne grupy Tier 0 – jak w 2.2 parametrem
`-AdditionalTier0Sids`):

```powershell
# Test-AdMngDmsaCreators.ps1 – kto może tworzyć dMSA (tylko odczyt)
param([string[]] $AdditionalTier0Sids = @())
Import-Module ActiveDirectory
$dSid    = (Get-ADDomain).DomainSID.Value
$rootSid = (Get-ADDomain -Identity (Get-ADForest).RootDomain).DomainSID.Value
$tier0 = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-9', "$dSid-512", "$dSid-516", "$dSid-526",
           "$rootSid-498", "$rootSid-518", "$rootSid-519", "$rootSid-527") + $AdditionalTier0Sids
# DnsAdmins to w praktyce Tier 0 (członkowie mogą uruchomić kod na DC); ma pełną kontrolę nad CN=MicrosoftDNS,CN=System
$tier0 += @(Get-ADGroup -Filter "sAMAccountName -eq 'DnsAdmins'" | ForEach-Object { $_.SID.Value })
$SidType = [System.Security.Principal.SecurityIdentifier]
$dmsa = [guid]'0feb936f-47b3-49f2-9386-1dedc2c23765'      # klasa msDS-DelegatedManagedServiceAccount
$mask = [int][System.DirectoryServices.ActiveDirectoryRights]'CreateChild, WriteDacl, WriteOwner'
function Get-SidName([string]$sid) {
    try { ([System.Security.Principal.SecurityIdentifier]$sid).Translate([System.Security.Principal.NTAccount]).Value } catch { $sid }
}
# klasy możliwych rodziców dMSA: possSuperiors i systemPossSuperiors tej klasy i wszystkich jej klas nadrzędnych
$schema = (Get-ADRootDSE).schemaNamingContext
$c = 'msDS-DelegatedManagedServiceAccount'
if (-not (Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=$c)")) { $c = 'computer' }   # schemat bez Windows Server 2025
$parents = @()
while ($c) {
    $o = Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=$c)" -Properties subClassOf, systemPossSuperiors, possSuperiors
    $parents += @($o.systemPossSuperiors) + @($o.possSuperiors)
    $c = if ($o.subClassOf -ne $c) { $o.subClassOf } else { $null }      # klasa top jest swoją własną klasą nadrzędną
}
$parents = @($parents | Where-Object { $_ } | Sort-Object -Unique)
Write-Host ('dMSA może powstać pod obiektami klas: ' + ($parents -join ', '))
$filter = '(|' + (($parents | ForEach-Object { "(objectClass=$_)" }) -join '') + ')'
Get-ADObject -LDAPFilter $filter -Properties nTSecurityDescriptor |
ForEach-Object {
    $o = $_; $sd = $o.nTSecurityDescriptor
    $owner = $sd.GetOwner($SidType).Value
    if ($tier0 -notcontains $owner) { [pscustomobject]@{ Kontener = $o.DistinguishedName; Kto = Get-SidName $owner; Jak = 'właściciel' } }
    foreach ($r in $sd.GetAccessRules($true, $true, $SidType)) {
        if ($r.AccessControlType -ne 'Allow' -or $tier0 -contains $r.IdentityReference.Value) { continue }
        if (([int]$r.PropagationFlags -band 2) -ne 0) { continue }                  # wpis tylko dla obiektów podrzędnych
        if (([int]$r.ActiveDirectoryRights -band $mask) -eq 0) { continue }
        if ($r.ObjectType -ne [guid]::Empty -and $r.ObjectType -ne $dmsa) { continue }
        [pscustomobject]@{ Kontener = $o.DistinguishedName; Kto = Get-SidName $r.IdentityReference.Value; Jak = $r.ActiveDirectoryRights.ToString() }
    }
} | Sort-Object Kto, Kontener | Format-Table -AutoSize -Wrap
```

Jak czytać: pierwszy wiersz to klasy możliwych rodziców dMSA – w schemacie Windows Server 2025 `builtinDomain,
container, domainDNS, lostAndFound, organization, organizationalUnit` (bez kontrolera 2025 skrypt pokazuje to samo dla
klasy `computer`). Skrypt sprawdza obiekty wszystkich tych klas – także łatwy do przeoczenia `CN=Builtin`. Jeśli w
wierszu jest inna klasa (rozszerzenie schematu), skrypt uwzględnia ją sam. Każdy następny wiersz to miejsce, w którym
podany podmiot może utworzyć dMSA (albo nadać sobie takie prawo). Świeża domena testowa zwykle nie daje żadnych
wierszy. Typowe znaleziska w produkcji: zespoły z pełną kontrolą nad „swoimi” OU,
konta narzędzi do zarządzania tożsamością, właściciele OU będący kontami osób, a na samym obiekcie domeny – np.
`Exchange Windows Permissions` z `WriteDacl` (2.2). Co zrobić: zawęź `CreateChild` do potrzebnych klas (użytkownik,
grupa, komputer), odbierz `GenericAll`/`WriteDacl`/`WriteOwner`, zmień właściciela na Domain Admins. Zanim cokolwiek
zmienisz, zapisz SDDL obiektu do pliku tak jak w 2.2 – to Twój plan wycofania (polecenie przywracania – pod tabelą
w 2.13). Do porównania możesz uruchomić skrypt Akamai `Get-BadSuccessorOUPermissions.ps1`
(<https://github.com/akamai/BadSuccessor>) – sprawdza tylko OU.

**Poziom poprawek kontrolerów:**

```powershell
Invoke-Command -ComputerName DC01.contoso.com, DC02.contoso.com {
    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Select-Object ProductName, CurrentBuild, UBR
}
Invoke-Command -ComputerName DC01.contoso.com { Get-HotFix | Sort-Object InstalledOn | Select-Object -Last 3 }
```

Oczekiwane: na DC01 (Windows Server 2025, kompilacja 26100) zainstalowana co najmniej aktualizacja zbiorcza z sierpnia
2025 (KB5063878, kompilacja 26100.4946) – w praktyce bieżąca aktualizacja miesięczna. Numery porównaj z historią
aktualizacji Windows Server 2025 na stronie Microsoft. DC02 (2022) nie ma dMSA, ale też powinien mieć bieżące poprawki.

### 2.10 Zasady inspekcji na kontrolerach i rozmiar dziennika Security

**Gdzie:** PAW (GPMC), sprawdzenie na DC01 i DC02. **Co i po co:** bez zasad inspekcji kontrolery nie zapisują zdarzeń,
z których Watchdog ustala wykonawcę i źródło – wykonawca będzie „nieustalony” (`AUDIT_POLICY_MISSING`).

Ustawienia podkategorii inspekcji z kilku GPO łączą się osobno dla każdej podkategorii; przy konflikcie wygrywa GPO
o wyższym priorytecie. Dlatego nowe GPO dostaje najwyższy priorytet (`-Order 1`) i **każdą** podkategorię z tabeli
ustawia na „Sukces i niepowodzenie”. To najwyższa możliwa wartość, więc GPO niczego nie obniża – ani ustawień
domyślnych kontrolera (np. sukcesu dla Credential Validation), ani wymagań innych narzędzi (Defender for Identity
wymaga np. sukcesu i niepowodzenia dla Credential Validation). Zdarzenia sukcesu, których Watchdog nie potrzebuje
(np. 4768, udane 4776), zostają w dzienniku DC – subskrypcja WEF (4.4) ich nie przekazuje. Część podkategorii nie ma
zdarzeń niepowodzenia – tam „niepowodzenie” niczego nie dodaje. Zamiast nowego GPO możesz ustawić to samo w Waszym
istniejącym GPO inspekcji kontrolerów.

**Najpierw zapisz obecny stan każdego DC** – to Twój plan wycofania (z PAW; pliki trafiają do folderu Dokumenty).
Zachowaj **pierwsze** pliki kopii (najlepiej także poza stacją) – kopia zrobiona po 2.10 zawierałaby już nowe
ustawienia; poniższy blok sam to sprawdza:

```powershell
foreach ($dc in 'DC01.contoso.com', 'DC02.contoso.com') {
    $n = $dc.Split('.')[0]
    if (Test-Path "$env:USERPROFILE\Documents\admng-auditpol-before-$n.csv") { "Kopia dla $n już istnieje – nie tworzę nowej"; continue }
    Invoke-Command -ComputerName $dc {
        auditpol /backup /file:C:\Windows\Temp\admng-auditpol-before.csv | Out-Null
        Get-Content C:\Windows\Temp\admng-auditpol-before.csv; Remove-Item C:\Windows\Temp\admng-auditpol-before.csv
    } | Out-File "$env:USERPROFILE\Documents\admng-auditpol-before-$n.csv" -Encoding utf8
    Invoke-Command -ComputerName $dc { wevtutil gl security } | Select-String 'maxSize' |
        ForEach-Object { "$n $_" } | Tee-Object -FilePath "$env:USERPROFILE\Documents\admng-auditpol-before-maxsize.txt" -Append
}
```

Ostatnie polecenie wypisuje obecny rozmiar dziennika Security każdego DC (`maxSize`, w bajtach) – przyda się niżej.

Utwórz GPO `AdMng - inspekcja DC` połączone z OU **Domain Controllers** (albo uzupełnij Wasze istniejące GPO
inspekcji):

```powershell
$gpo = New-GPO -Name 'AdMng - inspekcja DC'
New-GPLink -Guid $gpo.Id -Target 'OU=Domain Controllers,DC=contoso,DC=com' -LinkEnabled Yes -Order 1
```

W GPMC edytuj GPO: Konfiguracja komputera → Zasady → Ustawienia systemu Windows → Ustawienia zabezpieczeń → Konfiguracja
zaawansowanych zasad inspekcji (Computer Configuration → Policies → Windows Settings → Security Settings → Advanced
Audit Policy Configuration). W każdej podkategorii z tabeli zaznacz „Configure the following audit events” oraz
„Success” i „Failure”:

| Podkategoria (nazwa angielska) | GUID (do `auditpol`) | Ustawienie | Po co |
|---|---|---|---|
| User Account Management | `{0CCE9235-69AE-11D9-BED3-505054503030}` | S, F | 4720–4726, 4738, 4740, 4767, 4781, 4794 |
| Computer Account Management | `{0CCE9236-69AE-11D9-BED3-505054503030}` | S, F | 4741–4743 |
| Security Group Management | `{0CCE9237-69AE-11D9-BED3-505054503030}` | S, F | 4727–4737, 4754–4758, 4764 |
| Distribution Group Management | `{0CCE9238-69AE-11D9-BED3-505054503030}` | S, F | 4744–4763 |
| Directory Service Changes | `{0CCE923C-69AE-11D9-BED3-505054503030}` | S, F | 5136–5141 – kto zmienił atrybut (wymaga SACL z 2.11) |
| Logon | `{0CCE9215-69AE-11D9-BED3-505054503030}` | S, F | 4624 – komputer i adres źródłowy; 4625 – źródło blokad |
| Kerberos Authentication Service | `{0CCE9242-69AE-11D9-BED3-505054503030}` | S, F | 4771 – źródło błędnych haseł (blokady, „password spraying”); Watchdog używa tylko niepowodzeń |
| Credential Validation | `{0CCE923F-69AE-11D9-BED3-505054503030}` | S, F | 4776 – źródło błędnych haseł NTLM; Watchdog używa tylko niepowodzeń |
| Audit Policy Change | `{0CCE922F-69AE-11D9-BED3-505054503030}` | S, F | 4719 – zmiana zasad inspekcji |

W tym samym GPO: Ustawienia zabezpieczeń → Zasady lokalne → Opcje zabezpieczeń (Security Settings → Local Policies →
Security Options) → „Audit: Force audit policy subcategory settings (Windows Vista or later) to override audit policy
category settings” = Enabled (zwykle już włączone).

**Rozmiar dziennika Security.** Kontrolery wysyłają teraz także zdarzenia logowań – 4624 (logowania sieciowe – typ 3 –
i RDP – typ 10), 4625, 4771 i nieudane 4776 – więc dziennik zapełnia się szybciej. Dziennik musi pomieścić zdarzenia
z najdłuższej przerwy w pracy odbiornika, jaką akceptujesz – po przerwie kontroler wysyła zaległe zdarzenia od
miejsca, w którym skończył. Na start dziennik ma mieć **co najmniej 1 GB**, a w pilotażu zmierz rzeczywisty przyrost
(7.4). Jeśli któryś zapisany wyżej wiersz `maxSize` jest mniejszy niż `1073741824`, ustaw rozmiar w tym GPO – 1 GB albo
więcej, ale nie mniej niż największa zapisana wartość. Jeśli wszystkie DC mają co najmniej 1 GB, zostaw to ustawienie
nieskonfigurowane: GPO o najwyższym priorytecie zmniejszyłoby rozmiar ustawiony gdzie indziej. Ustawienie: Szablony
administracyjne → Składniki systemu Windows → Usługa dziennika zdarzeń → Zabezpieczenia (Administrative Templates →
Windows Components → Event Log Service → Security) → „Specify the maximum log file size (KB)” = `1048576` (albo więcej).
Odpowiednik w PowerShell:

```powershell
Set-GPRegistryValue -Name 'AdMng - inspekcja DC' -Key 'HKLM\Software\Policies\Microsoft\Windows\EventLog\Security' `
    -ValueName 'MaxSize' -Type DWord -Value 1048576
```

**Sprawdzenie** (DC01 i DC02, po `gpupdate /target:computer /force`):

```powershell
auditpol /get /subcategory:"{0CCE9235-69AE-11D9-BED3-505054503030},{0CCE9236-69AE-11D9-BED3-505054503030},{0CCE9237-69AE-11D9-BED3-505054503030},{0CCE9238-69AE-11D9-BED3-505054503030},{0CCE923C-69AE-11D9-BED3-505054503030},{0CCE9215-69AE-11D9-BED3-505054503030},{0CCE9242-69AE-11D9-BED3-505054503030},{0CCE923F-69AE-11D9-BED3-505054503030},{0CCE922F-69AE-11D9-BED3-505054503030}"
wevtutil gl security
```

Oczekiwane: przy każdej podkategorii `Success and Failure` (po polsku „Sukces i niepowodzenie”); w `wevtutil` wiersz
`maxSize:` równy `1073741824` lub więcej.

### 2.11 SACL – zdarzenia 5136 i 5137 „kto zmienił, kto utworzył”

> **W skrócie.** Co zmienia: dodaje wpisy inspekcji (SACL) na obiekcie domeny, na `AdminSDHolder` i na kontenerze
> usług PKI; uprawnień (DACL) nie zmienia. Gdzie: PAW, Domain Admin (w lesie z wieloma domenami – Enterprise Admin dla
> kontenera PKI). Ile trwa: kilka minut. Wynik: kontrolery zapisują zdarzenia 5136/5137/5141 dla śledzonych obiektów.
> Wycofanie: blok „Wycofanie” na końcu kroku – usuwa tylko wpisy dodane tutaj.

**Gdzie:** PAW, Domain Admin (potrzebne prawo „Zarządzanie inspekcją i dziennikiem zabezpieczeń” na kontrolerach – Domain
Admins je mają). **Co i po co:** domyślne listy SACL w AD **nie** generują zdarzeń 5136/5137/5141 dla użytkowników,
grup i komputerów. Bez nich Watchdog wie, co się zmieniło, ale nie wie, kto to zrobił (`SACL_MISSING`). Skrypt dodaje
wpisy inspekcji „Wszyscy – sukces” na wzór Microsoft Defender for Identity: dla klas śledzonych obiektów (także dMSA i
obiektów GPO – potrzebne do reguł BadSuccessor i do wykonawcy zmian GPO), na samym obiekcie domeny (zasady haseł,
`gPLink`, `ms-DS-MachineAccountQuota`), na `AdminSDHolder` oraz – w partycji konfiguracji, która nie dziedziczy wpisów
z domeny – na kontenerze usług PKI `CN=Public Key Services` (szablony certyfikatów, urzędy, NTAuth). Bez tego ostatniego
zmiana szablonu certyfikatu zostanie wykryta, ale z wykonawcą „nieustalony” (`SACL_MISSING`). Zmiana w partycji
konfiguracji wymaga w lesie z wieloma domenami konta z grupy Enterprise Admins; w lesie z jedną domeną wystarczy Domain
Admin.

Zdarzenie 5137 („utworzono obiekt”) kontroler zapisuje według SACL obiektu **nadrzędnego** – liczy się wpis „tworzenie
obiektów klasy X” na kontenerze, w którym obiekt powstaje. Dlatego obok wpisów na samych obiektach skrypt dodaje na
obiekcie domeny wpisy „tworzenie i usuwanie obiektów klasy X – ten obiekt i wszystkie podrzędne”. Działają w każdym
miejscu domeny: w OU, w kontenerach (np. `CN=Managed Service Accounts` – tam trafia dMSA utworzone bez `-Path`,
`CN=Users`, `CN=Policies,CN=System`) i bezpośrednio pod obiektem domeny.

Najpierw zapisz obecne SACL. Zachowaj **pierwszy** plik kopii (najlepiej także poza stacją) – przy ponownym
uruchomieniu 2.11 nie twórz nowej kopii, bo zawierałaby już wpisy Watchdoga; poniższy blok sam to sprawdza:

```powershell
Import-Module ActiveDirectory
$domain = (Get-ADDomain).DistinguishedName
$cfg    = (Get-ADRootDSE).configurationNamingContext
$pki    = "AD:\CN=Public Key Services,CN=Services,$cfg"
$old    = Get-ChildItem "$env:USERPROFILE\Documents\admng-sacl-backup-*.txt" -ErrorAction SilentlyContinue
if ($old) { "Kopia już istnieje – nie tworzę nowej: $($old.FullName)" } else {
    $backup = "$env:USERPROFILE\Documents\admng-sacl-backup-$(Get-Date -Format yyyyMMdd-HHmm).txt"
    'domain=' + (Get-Acl -Path "AD:\$domain" -Audit).GetSecurityDescriptorSddlForm('Audit') | Out-File $backup
    'adminsdholder=' + (Get-Acl -Path "AD:\CN=AdminSDHolder,CN=System,$domain" -Audit).GetSecurityDescriptorSddlForm('Audit') | Out-File $backup -Append
    'pki=' + (Get-Acl -Path $pki -Audit).GetSecurityDescriptorSddlForm('Audit') | Out-File $backup -Append
    $backup
}
```

**Definicje wpisów.** Ten blok tylko przygotowuje listę wpisów (`$rules`) – niczego nie zmienia. Uruchamiasz go przed
dodaniem wpisów, przed sprawdzeniem i przed wycofaniem (także w nowej sesji PowerShell):

```powershell
Import-Module ActiveDirectory
$domain   = (Get-ADDomain).DistinguishedName
$cfg      = (Get-ADRootDSE).configurationNamingContext
$schema   = (Get-ADRootDSE).schemaNamingContext
$pki      = "AD:\CN=Public Key Services,CN=Services,$cfg"
$everyone = [System.Security.Principal.SecurityIdentifier]'S-1-1-0'
$none     = [guid]::Empty
$classes = @{
  user  = 'bf967aba-0de6-11d0-a285-00aa003049e2'; group = 'bf967a9c-0de6-11d0-a285-00aa003049e2'
  computer = 'bf967a86-0de6-11d0-a285-00aa003049e2'; organizationalUnit = 'bf967aa5-0de6-11d0-a285-00aa003049e2'
  inetOrgPerson = '4828cc14-1437-45bc-9b07-ad6f015e5f28'
  msa = 'ce206244-5827-4a86-ba1c-1c0c386c1b64'; gmsa = '7b8b558a-93a5-4af7-adca-c017e67f1057'
  groupPolicyContainer = 'f30e3bc2-9ff0-11d1-b603-0000f80367c1'; passwordSettings = '3bcd9db8-f84b-451c-952f-6c52b81f9ec6'
  trustedDomain = 'bf967ab8-0de6-11d0-a285-00aa003049e2'
}
# dMSA (Windows Server 2025) – tylko gdy klasa istnieje w schemacie
$dmsa = Get-ADObject -SearchBase $schema -LDAPFilter '(lDAPDisplayName=msDS-DelegatedManagedServiceAccount)' -Properties schemaIDGUID
if ($dmsa) { $classes['dmsa'] = ([guid]$dmsa.schemaIDGUID).Guid }
function New-AdMngAuditRule([string]$Rights, [guid]$ObjectType, [string]$Inheritance, [guid]$InheritedType) {
    New-Object System.DirectoryServices.ActiveDirectoryAuditRule($everyone, $Rights, 'Success', $ObjectType, $Inheritance, $InheritedType)
}
$write = 'WriteProperty, Delete, DeleteTree, WriteDacl, WriteOwner, CreateChild, DeleteChild, ExtendedRight, Self'
$rules = @{
    "AD:\$domain" = @(
        foreach ($g in $classes.Values) {
            New-AdMngAuditRule $write $none 'Descendents' $g               # 1) zmiany obiektów tej klasy (5136, 5139, 5141)
            New-AdMngAuditRule 'CreateChild, DeleteChild' $g 'All' $none   # 2) tworzenie i usuwanie obiektów tej klasy w całej domenie (5137, 5141)
        }
        New-AdMngAuditRule 'WriteProperty, WriteDacl, WriteOwner' $none 'None' $none    # 3) sam obiekt domeny
    )
    "AD:\CN=AdminSDHolder,CN=System,$domain" = @(                          # 4) szablon uprawnień kont chronionych
        New-AdMngAuditRule 'WriteProperty, WriteDacl, WriteOwner' $none 'None' $none)
    $pki = @(                                                              # 5) usługi PKI – ten obiekt i wszystkie podrzędne
        New-AdMngAuditRule 'WriteProperty, Delete, DeleteTree, WriteDacl, WriteOwner, CreateChild, DeleteChild' $none 'All' $none)
}
```

(Opcjonalnie możesz dopisać do `$rules` ten sam wpis co w punkcie 5 dla `"AD:\CN=Sites,$cfg"` – połączenia GPO z
lokacjami.)

**Dodanie wpisów** (w tej samej sesji, po bloku definicji):

```powershell
foreach ($p in $rules.Keys) {
    $acl = Get-Acl -Path $p -Audit
    foreach ($r in $rules[$p]) { $acl.AddAuditRule($r) }
    Set-Acl -Path $p -AclObject $acl
}
```

**Sprawdzenie** (w tej samej sesji, po bloku definicji):

```powershell
foreach ($p in $rules.Keys) {
    $have = (Get-Acl -Path $p -Audit).GetAuditRules($true, $false, [System.Security.Principal.SecurityIdentifier])
    $missing = @($rules[$p] | Where-Object {
        $r = $_
        -not ($have | Where-Object { $_.IdentityReference -eq $r.IdentityReference -and $_.AuditFlags -eq $r.AuditFlags -and
            $_.ObjectType -eq $r.ObjectType -and $_.InheritedObjectType -eq $r.InheritedObjectType -and
            $_.InheritanceType -eq $r.InheritanceType -and ($_.ActiveDirectoryRights -band $r.ActiveDirectoryRights) -eq $r.ActiveDirectoryRights })
    })
    '{0}: brakujących wpisów {1} z {2}' -f $p, $missing.Count, @($rules[$p]).Count
}
```

Oczekiwane: trzy wiersze z `brakujących wpisów 0` – dla obiektu domeny `0 z 23` na schemacie Windows Server 2025
(`0 z 21` bez klasy dMSA), dla `AdminSDHolder` i kontenera PKI `0 z 1`.

**Test działania** (po 2.10). Zmiany wykonaj na DC01, żeby wiedzieć, gdzie szukać zdarzeń:

```powershell
Set-ADUser jan.kowalski -Description 'test SACL' -Server DC01.contoso.com
New-ADUser -Name 'test-sacl' -Server DC01.contoso.com             # bez -Path: trafia do kontenera CN=Users
Remove-ADUser test-sacl -Confirm:$false -Server DC01.contoso.com
```

Na DC01 (konsola albo `Invoke-Command -ComputerName DC01.contoso.com { … }`):

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5136, 5137, 5141 } -MaxEvents 6 | Format-List TimeCreated, Id, Message
```

Oczekiwane: zdarzenie 5136 „A directory service object was modified” z kontem `CONTOSO\adm-jan`, obiektem
`CN=Jan Kowalski,…` i atrybutem `description`; 5137 „A directory service object was created” dla
`CN=test-sacl,CN=Users,…` (wpis tworzenia działa także w kontenerach) i 5141 „A directory service object was deleted”.

Watchdog nie może sam odczytać SACL (to wymaga uprawnień, których konto usługi nie ma); zamiast tego pokazuje pokrycie –
odsetek zmian przypisanych wykonawcy (Stan → Atrybucja).

**Wycofanie** – usuwa wyłącznie wpisy dodane w 2.11; działa w nowej sesji PowerShell, także po tygodniach. Najpierw
uruchom blok „Definicje wpisów”, potem (wpisz nazwę **pierwszego** pliku kopii):

```powershell
$backup = "$env:USERPROFILE\Documents\admng-sacl-backup-<data>.txt"      # pierwszy plik kopii z 2.11
$saved  = @{}
Get-Content $backup | ForEach-Object { $k, $v = $_ -split '=', 2; $saved[$k] = $v }
$names  = @{ "AD:\$domain" = 'domain'; "AD:\CN=AdminSDHolder,CN=System,$domain" = 'adminsdholder'; $pki = 'pki' }
foreach ($p in $rules.Keys) {
    $before = New-Object System.DirectoryServices.ActiveDirectorySecurity
    if ($saved[$names[$p]]) { $before.SetSecurityDescriptorSddlForm($saved[$names[$p]], 'Audit') }
    $old = $before.GetAuditRules($true, $false, [System.Security.Principal.SecurityIdentifier])
    $acl = Get-Acl -Path $p -Audit
    foreach ($r in $rules[$p]) {
        [void]$acl.RemoveAuditRule($r)                                     # odejmuje prawa dodane w 2.11
        $old | Where-Object { $_.IdentityReference -eq $r.IdentityReference -and $_.AuditFlags -eq $r.AuditFlags -and
            $_.ObjectType -eq $r.ObjectType -and $_.InheritedObjectType -eq $r.InheritedObjectType -and
            $_.InheritanceType -eq $r.InheritanceType } | ForEach-Object { $acl.AddAuditRule($_) }   # ten sam wpis sprzed 2.11 wraca
    }
    Set-Acl -Path $p -AclObject $acl
}
```

Wpisy, które ktoś dodał po 2.11 (np. Defender for Identity, Exchange, inny zespół), zostają – z jednym wyjątkiem:
wpis o tym samym koncie, klasie i dziedziczeniu co wpis z 2.11 łączy się z nim w jeden i zniknie razem z nim. Takie są
np. wymagane przez Defender for Identity wpisy „Everyone – Descendant User/Group/Computer objects”: jeśli po 2.11
wdrożono Defender for Identity, nie wycofuj SACL. Pierwszy plik kopii służy wtedy tylko do porównania – pełne
przywrócenie SACL z niego usunęłoby wszystkie wpisy dodane po 2.11.

### 2.12 Windows Event Forwarding – GPO dla kontrolerów

**Gdzie:** PAW (GPMC), sprawdzenie na DC01 i DC02. **Co i po co:** kontrolery same wysyłają wybrane zdarzenia z
dziennika Security do OpenWEC na serwerze Watchdog (subskrypcja inicjowana przez źródło). Nie potrzeba agentów ani
otwierania RPC na DC, a konto usługi nie potrzebuje żadnych praw do dzienników kontrolerów. Kontroler uwierzytelnia
się swoim kontem komputera (`DC01$`) przez Kerberos, a treść jest szyfrowana na poziomie wiadomości (HTTP 5985).

Utwórz GPO `AdMng - WEF` połączone z OU **Domain Controllers**:

1. **Menedżer subskrypcji.** Szablony administracyjne → Składniki systemu Windows → Przekazywanie zdarzeń
   (Administrative Templates → Windows Components → Event Forwarding) → „Configure target Subscription Manager” =
   Enabled → Show → dodaj:
   `Server=http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC,Refresh=60`.
   `Refresh=60` (sekundy) przyspiesza testy; po pilotażu zmień na `3600`.
2. **Dostęp do dziennika Security dla NETWORK SERVICE.** Przekazywaniem zajmuje się usługa WinRM działająca jako
   NETWORK SERVICE, która domyślnie nie czyta dziennika Security. Szablony administracyjne → Składniki systemu Windows →
   Usługa dziennika zdarzeń → Zabezpieczenia (Administrative Templates → Windows Components → Event Log Service →
   Security) → „Configure log access” = Enabled, wartość:
   `O:BAG:SYD:(A;;0xf0005;;;SY)(A;;0x5;;;BA)(A;;0x1;;;S-1-5-32-573)(A;;0x1;;;NS)`. To samo ustaw w „Configure log
   access (legacy)” (zalecenie dokumentacji OpenWEC). Zanim wpiszesz wartość, porównaj ją z obecną na DC
   (`wevtutil gl security`, wiersz `channelAccess`): jeśli są tam dodatkowe wpisy (np. konto agenta SIEM), zostaw je i
   dopisz tylko `(A;;0x1;;;NS)`. Ta metoda działa bez restartu – w odróżnieniu od dodawania NETWORK SERVICE do grupy
   Event Log Readers.
3. **Usługa WinRM.** Ustawienia zabezpieczeń → Usługi systemowe (Security Settings → System Services) → „Windows
   Remote Management (WS-Management)” = Automatic (na Windows Server zwykle już działa).
4. **Zapora wychodząca.** Domyślnie Windows Firewall nie blokuje ruchu wychodzącego. Jeśli Wasze GPO dla DC blokuje
   ruch wychodzący, dodaj regułę: TCP 5985 do 10.0.1.25.

Zamiast tworzenia GPO i punktów 1–2 w GPMC możesz użyć PowerShell (moduł GroupPolicy) – polecenia tworzą GPO, łączą je
z OU i ustawiają obie wartości; ustawienie „(legacy)” oraz punkty 3–4 dodaj potem w GPMC:

```powershell
$gpo = New-GPO -Name 'AdMng - WEF'
New-GPLink -Guid $gpo.Id -Target 'OU=Domain Controllers,DC=contoso,DC=com' -LinkEnabled Yes   # migracja z 0.2.0: -LinkEnabled No (0.4)
Set-GPRegistryValue -Guid $gpo.Id -Key 'HKLM\Software\Policies\Microsoft\Windows\EventLog\EventForwarding\SubscriptionManager' `
    -ValueName '1' -Type String -Value 'Server=http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC,Refresh=60'
Set-GPRegistryValue -Guid $gpo.Id -Key 'HKLM\Software\Policies\Microsoft\Windows\EventLog\Security' `
    -ValueName 'ChannelAccess' -Type String -Value 'O:BAG:SYD:(A;;0xf0005;;;SY)(A;;0x5;;;BA)(A;;0x1;;;S-1-5-32-573)(A;;0x1;;;NS)'
```

Zasady inspekcji i SACL (2.10, 2.11) są częścią tej samej całości – bez nich nie ma czego przekazywać. Reguły zapory
„Remote Event Log Management” i członkostwo konta usługi w Event Log Readers z wersji 0.2.0 **nie są potrzebne**
(przy migracji usuń je – na końcu tego kroku).

**Sprawdzenie** (DC01 i DC02, po `gpupdate /target:computer /force`):

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\EventLog\EventForwarding\SubscriptionManager'
wevtutil gl security | Select-String 'channelAccess'
Get-Service WinRM | Format-Table Name, Status, StartType
Get-WinEvent -LogName 'Microsoft-Windows-Forwarding/Operational' -MaxEvents 10 | Format-Table TimeCreated, Id, Message -Wrap
```

Oczekiwane:

- wartość `1 : Server=http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC,Refresh=60`,
- `channelAccess:` zawiera `(A;;0x1;;;NS)`,
- `WinRM  Running  Automatic`,
- w dzienniku Forwarding/Operational: **dopóki OpenWEC nie działa** – zdarzenia 105 („The forwarder is having a problem
  communicating with subscription manager…”) – to oczekiwane. Po uruchomieniu OpenWEC (rozdz. 4) – zdarzenie **104**
  („The forwarder has successfully connected to the subscription manager…”), a po nim **100** („The subscription
  admng-dc-security is created successfully.”).

Po uruchomieniu OpenWEC sprawdzisz też połączenie: `Test-NetConnection admng-wd.contoso.com -Port 5985` –
`TcpTestSucceeded : True`.

**Migracja z wersji 0.2.0** (tylko gdy działała usługa Windows; wykonaj po przełączeniu z ramki w 0.4 oraz
uruchomieniu i sprawdzeniu usługi na Debianie – rozdz. 5 i 6). Usuń prawa, które dostało konto gMSA wersji Windows –
inaczej zostaną po nim uprawnienia w pobliżu Tier 0:

```powershell
$o = 'CONTOSO\svc-admngwd$'; $d = 'DC=contoso,DC=com'
foreach ($p in $d, "CN=AdminSDHolder,CN=System,$d", "CN=Password Settings Container,CN=System,$d", "CN=Deleted Objects,$d") {
    dsacls $p /R $o
}
Remove-ADGroupMember -Identity 'S-1-5-32-573' -Members (Get-ADServiceAccount svc-admngwd) -Confirm:$false   # Event Log Readers
(Get-ADGroup -Identity 'S-1-5-32-573' -Properties member).member      # pozostali członkowie
```

- Jeśli ostatnie polecenie pokazuje `CN=S-1-5-20,CN=ForeignSecurityPrincipals,…` (NETWORK SERVICE – wariant B wersji
  0.2.0), usuń go: `Set-ADGroup -Identity 'S-1-5-32-573' -Remove @{ member = "CN=S-1-5-20,CN=ForeignSecurityPrincipals,$d" }`.
  Dostęp do dziennika Security daje teraz ustawienie z punktu 2.
- Reguły zapory „Remote Event Log Management” wyłącz na każdym DC (albo w GPO, które je włączało):
  `Get-NetFirewallRule -Name 'RemoteEventLogSvc*' | Disable-NetFirewallRule`.
- Jeśli w wariancie B kontrolery wysyłały zdarzenia do kolektora Windows przez inne GPO menedżera subskrypcji – odłącz je
  od OU Domain Controllers albo usuń.
- Na koniec odinstaluj usługę Windows na starym serwerze (`Uninstall-AdMngWatchdog.ps1 -RemoveData`), usuń konto gMSA
  i grupę serwerów, które mogły pobierać jego hasło: `Remove-ADServiceAccount svc-admngwd`,
  `Remove-ADGroup AdMng-Watchdog-Hosts`. SPN `HTTP/admng-wd.contoso.com` zdjąłeś z gMSA już przy przełączeniu (0.4).

**Sprawdzenie:** `dsacls 'DC=contoso,DC=com' | Select-String 'svc-admngwd'` nic nie zwraca.

### 2.13 Podsumowanie zmian w AD i ich wycofanie

| # | Zmiana | Gdzie | Sprawdzenie | Wycofanie |
|---|---|---|---|---|
| 1 | Poprawki delegacji w OU Tier 0 (2.2) | ACL OU Tier 0, AdminSDHolder | skrypt 2.2 bez znalezisk | przywróć SDDL z zapisanego pliku (polecenie pod tabelą; zwykle **nie** wycofujesz – to poprawa bezpieczeństwa) |
| 2 | Grupy `AdMng-Watchdog-Admins`, `-Viewers` (2.3) | OU Groups w Tier 0 | `Get-ADGroup` | `Remove-ADGroup AdMng-Watchdog-Admins`, `Remove-ADGroup AdMng-Watchdog-Viewers` |
| 3 | Rekord A + PTR `admng-wd` (2.4) | DNS | `Resolve-DnsName` | `Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName contoso.com -Name admng-wd -RRType A -Force` i PTR `-ZoneName 1.0.10.in-addr.arpa -Name 25 -RRType Ptr` |
| 4 | Konto `ADMNG-WD$` z wpisem odmowy dla SELF (2.5) | OU Service Accounts w Tier 0 | `Get-ADComputer`, `setspn -L`, `Get-Acl` | `Disable-ADAccount 'ADMNG-WD$'`, po potwierdzeniu `Remove-ADComputer ADMNG-WD` (keytab przestaje działać od razu; wpis odmowy znika razem z obiektem) |
| 5 | GPO „AdMng - ograniczenia konta Watchdog” i wpisy konta w istniejących GPO z prawami odmowy logowania (2.5) | domena, OU tych GPO | `secedit` na stacjach | `Remove-GPO -Name 'AdMng - ograniczenia konta Watchdog'` i usuń konto z praw w pozostałych GPO |
| 6 | Prawa odczytu `dsacls` (2.6) | domena, AdminSDHolder, Password Settings Container, Deleted Objects | `dsacls … \| Select-String ADMNG-WD`, test z Debiana | **przed** usunięciem konta: `dsacls <DN> /R 'CONTOSO\ADMNG-WD$'` na każdym z czterech obiektów (zmiany właściciela Deleted Objects nie cofamy) |
| 7 | Odczyt GPO dla Domain Computers (2.7, tylko gdy potrzebne) | wybrane GPO | skrypt 2.7 | `Set-GPPermission -Guid <Id> -TargetName $dc -TargetType Group -PermissionLevel None -Replace` (`$dc` jak w 2.7; bez `-Replace` polecenie niczego nie zmienia) |
| 8 | `ms-DS-MachineAccountQuota` = 0 (2.8) | obiekt domeny | odczyt atrybutu | `Set-ADDomain -Identity contoso.com -Replace @{ 'ms-DS-MachineAccountQuota' = <poprzednia> }` |
| 9 | Poprawki prawa tworzenia dMSA (2.9) | ACL obiektu domeny, OU i kontenerów | skrypt 2.9 | jak 1 – zwykle nie wycofujesz |
| 10 | GPO „AdMng - inspekcja DC” + rozmiar dziennika (2.10) | OU Domain Controllers | `auditpol`, `wevtutil gl security` | GPO zostaw (zalecane – inspekcja przydaje się niezależnie od Watchdoga). Samo `Remove-GPO` **nie** przywraca poprzednich zasad – kontrolery zachowują ostatnie ustawienia w lokalnej konfiguracji. Jeśli trzeba wycofać: w tym GPO ustaw każdą podkategorię na wartość z kopii z 2.10 (polecenie pod tabelą; „No Auditing” tylko tam, gdzie kopia tak mówi), rozmiar dziennika – jak w kopii albo nieskonfigurowany; na DC `gpupdate /target:computer /force`, porównaj `auditpol /get /category:*` z kopią i dopiero potem `Remove-GPO -Name 'AdMng - inspekcja DC'` |
| 11 | SACL (2.11) | obiekt domeny, AdminSDHolder, `CN=Public Key Services` | blok sprawdzenia z 2.11 | blok „Wycofanie” z 2.11 – usuwa tylko wpisy dodane w 2.11 |
| 12 | GPO „AdMng - WEF” (2.12) | OU Domain Controllers | rejestr, `wevtutil`, Forwarding/Operational | `Remove-GPO -Name 'AdMng - WEF'` i `gpupdate /force` na DC – kontrolery przestają wysyłać zdarzenia, dostęp NETWORK SERVICE do Security wraca do stanu domyślnego |
| 13 | Wpisy odmowy dla `ADMNG-WD$` na szablonach certyfikatów (2.5, tylko gdy jest urząd Enterprise) | partycja konfiguracji, `CN=Certificate Templates` | sprawdzenie z 2.5 | **przed** usunięciem konta: `dsacls "<DN szablonu>" /R 'CONTOSO\ADMNG-WD$'` na każdym szablonie z listy |

Wartości zasad inspekcji sprzed 2.10 (dla każdego DC z jego pliku kopii):

```powershell
$guids = '{0CCE9235-69AE-11D9-BED3-505054503030}', '{0CCE9236-69AE-11D9-BED3-505054503030}', '{0CCE9237-69AE-11D9-BED3-505054503030}',
         '{0CCE9238-69AE-11D9-BED3-505054503030}', '{0CCE923C-69AE-11D9-BED3-505054503030}', '{0CCE9215-69AE-11D9-BED3-505054503030}',
         '{0CCE9242-69AE-11D9-BED3-505054503030}', '{0CCE923F-69AE-11D9-BED3-505054503030}', '{0CCE922F-69AE-11D9-BED3-505054503030}'
Import-Csv "$env:USERPROFILE\Documents\admng-auditpol-before-DC01.csv" |
    Where-Object { $guids -contains $_.'Subcategory GUID' } | Format-Table Subcategory, 'Inclusion Setting'
```

Zamiast ustawiania wartości w GPO możesz po `Remove-GPO` przywrócić kopię na każdym DC: skopiuj na niego jego plik
kopii i wykonaj `auditpol /restore /file:<plik>`.

**Przywrócenie listy uprawnień (DACL) z pliku zapisanego w 2.2 lub 2.9.** Poprawki z 2.2 i 2.9 to zwykle usunięte
wpisy. Najpierw porównaj kopię z obecnym stanem i przywróć tylko to, co trzeba – pełne przywrócenie z pliku usuwa także
wszystkie zmiany wprowadzone po zapisaniu kopii (przez Ciebie, inne zespoły albo instalatory):

```powershell
$dn   = 'OU=Tier 0,OU=Admin,DC=contoso,DC=com'                 # obiekt, którego dotyczy plik
$sddl = (Get-Content '<plik acl-….txt>' -Raw).Trim()
$copy = New-Object System.DirectoryServices.ActiveDirectorySecurity
$copy.SetSecurityDescriptorSddlForm($sddl)
$fmt  = { '{0} {1} {2} {3} {4} {5}' -f $_.AccessControlType, $_.IdentityReference, $_.ActiveDirectoryRights, $_.ObjectType, $_.InheritanceType, $_.InheritedObjectType }
$then = $copy.GetAccessRules($true, $false, [System.Security.Principal.NTAccount]) | ForEach-Object $fmt
$now  = (Get-Acl -Path "AD:\$dn").GetAccessRules($true, $false, [System.Security.Principal.NTAccount]) | ForEach-Object $fmt
Compare-Object $then $now     # <= tylko w kopii (usunięte później), => dodane po zapisaniu kopii
'Właściciel w kopii: ' + $copy.GetOwner([System.Security.Principal.NTAccount]) + ', teraz: ' + (Get-Acl -Path "AD:\$dn").Owner
```

Usunięty wpis przywrócisz w „Użytkownicy i komputery AD” (Zabezpieczenia → Zaawansowane → Dodaj) albo `dsacls … /G`.
Pełne przywrócenie (właściciel i wszystkie jawne wpisy z kopii; zmiany wprowadzone po zapisaniu kopii **znikną**):

```powershell
$acl = Get-Acl -Path "AD:\$dn"
$acl.SetSecurityDescriptorSddlForm($sddl, 'Owner, Access')
Set-Acl -Path "AD:\$dn" -AclObject $acl
```

---

## 3. Serwer Debian

Wszystkie polecenia tego rozdziału wykonujesz na **Debianie** jako root: zaloguj się przez SSH na swoje konto
administratora i wpisz `sudo -i`.

### 3.1 Instalacja systemu i aktualizacje

**Gdzie:** konsola maszyny wirtualnej (instalator), potem Debian. **Co i po co:** minimalny system bez środowiska
graficznego – mniej oprogramowania to mniej podatności na serwerze Tier 0.

1. Obraz: Debian 13 „trixie” netinst amd64 (debian.org). Debian 12 „bookworm” też będzie obsługiwany przez pakiet.
2. W instalatorze:
   - nazwa hosta `admng-wd`, domena `contoso.com`;
   - sieć statyczna: 10.0.1.25/24, brama 10.0.1.1, DNS 10.0.1.10 i 10.0.1.11;
   - **hasło roota zostaw puste** – konto root zostanie zablokowane, a pierwszy użytkownik (np. `adm-jan`) dostanie
     `sudo`. Hasło tego konta (i kont dodanych w 3.2) będzie potrzebne zawsze: do `sudo` i do logowania na konsoli
     maszyny wirtualnej, także po wyłączeniu logowania hasłem przez SSH. Zapisz je w menedżerze haseł Tier 0 – bez
     niego roota odzyskasz tylko trybem ratunkowym (zmiana parametrów rozruchu w konsoli);
   - dysk co najmniej 60 GB (historia z 3 lat, kopie bazy); jeśli tworzysz osobną partycję `/var`, ma ona mieć co
     najmniej 40 GB; partycję wymiany (swap) instalator utworzy sam – wyłączysz ją po instalacji (niżej);
   - wybór oprogramowania: **tylko** „SSH server” i „standard system utilities”.
3. Szyfrowanie dysku (zalecane – na dysku będzie 3-letnia historia zmian i keytab): preferuj szyfrowanie przez
   hiperwizor. LUKS z instalatora Debiana wymaga wpisania hasła w konsoli przy **każdym** starcie – serwer po restarcie
   (np. po awarii zasilania) nie wstanie sam, a monitoring do tego czasu nie działa.

Po pierwszym uruchomieniu:

```bash
# /etc/hosts: nazwa serwera ma wskazywać prawdziwy adres, nie 127.0.1.1
sed -i 's/^127\.0\.1\.1\s.*$/10.0.1.25\tadmng-wd.contoso.com\tadmng-wd/' /etc/hosts
grep -q '^10\.0\.1\.25' /etc/hosts || printf '10.0.1.25\tadmng-wd.contoso.com\tadmng-wd\n' >> /etc/hosts
# bez pamięci wymiany – zawartość pamięci usług nie trafia na dysk
swapoff -a && sed -i '/\sswap\s/s/^/#/' /etc/fstab
echo 'RESUME=none' > /etc/initramfs-tools/conf.d/resume && update-initramfs -u
apt update && apt full-upgrade -y
DEBIAN_FRONTEND=noninteractive apt install -y krb5-user libsasl2-modules-gssapi-mit ldap-utils smbclient \
    chrony nftables openssl ca-certificates curl bind9-dnsutils unattended-upgrades apt-listchanges
```

Pakiet `krb5-user` przy instalacji interaktywnej pyta o domyślny obszar – nie ma to znaczenia: usługa i wszystkie
polecenia tej instrukcji używają własnego pliku `/etc/admng-watchdog/krb5.conf` (3.5), a `/etc/krb5.conf` zostaje bez
zmian. Nie instaluj `msktutil`, `adcli`, `realmd`, `sssd` ani `gss-ntlmssp`.

**Środowisko .NET (ASP.NET Core Runtime 10)** – wymaganie pakietu Watchdog (wersja co najmniej 10.0.13), z repozytorium
Microsoft. Dla Debiana 12 w adresie zamiast `13` wpisz `12`:

```bash
curl -fsSLo /root/packages-microsoft-prod.deb https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb
apt install -y /root/packages-microsoft-prod.deb && rm /root/packages-microsoft-prod.deb
apt update && apt install -y aspnetcore-runtime-10.0
```

**Automatyczne poprawki bezpieczeństwa:**

```bash
dpkg-reconfigure -plow unattended-upgrades          # odpowiedz „Yes”
cat > /etc/apt/apt.conf.d/51admng-unattended <<'EOF'
// środowisko .NET z repozytorium Microsoft
Unattended-Upgrade::Origins-Pattern {
        "site=packages.microsoft.com";
};
EOF
```

**[od 0.3.0]** Aktualizacja środowiska .NET usuwa z dysku pliki poprzedniej wersji – pakiet `admng-watchdog` sam
restartuje wtedy usługę (wyzwalacz dpkg). Restart trwa kilka sekund; zakładka Stan pokazuje wersję środowiska.

**Sprawdzenie:**

```bash
hostname --fqdn; hostname -I; cat /etc/debian_version
swapon --show
dotnet --list-runtimes
unattended-upgrade --dry-run --debug 2>&1 | grep -i 'allowed origins'
systemd-creds has-tpm2; echo "kod: $?"
```

Oczekiwane: `admng-wd.contoso.com`, `10.0.1.25`, `13.x`; `swapon --show` nic nie wypisuje; w `dotnet --list-runtimes`
wiersze `Microsoft.AspNetCore.App 10.0.13` (lub nowszy) i `Microsoft.NETCore.App 10.0.13`; lista dozwolonych źródeł
zawiera `Debian-Security` i `site=packages.microsoft.com`. To sprawdza tylko konfigurację – prawdziwe sprawdzenie
przychodzi po najbliższej aktualizacji .NET (zwykle w drugi wtorek miesiąca):
`grep -i aspnetcore /var/log/unattended-upgrades/unattended-upgrades.log` pokazuje jej instalację.

Wynik `systemd-creds has-tpm2` mówi, czy maszyna ma (v)TPM: `+firmware` i `+driver` – tak. Wtedy **zawsze**
doinstaluj biblioteki tpm2-tss – bez nich szyfrowanie sekretów nie użyje TPM (3.9). Na Debianie 13 wynik pokazuje też
`±libraries`; na Debianie 12 (systemd 252) tego wiersza nie ma i polecenie odpowiada `yes` także bez bibliotek, więc
sprawdź je poleceniem `dpkg -s`:

```bash
apt install -y libtss2-esys-3.0.2-0t64 libtss2-mu-4.0.1-0t64 libtss2-rc0t64 libtss2-tcti-device0t64   # Debian 13
apt install -y libtss2-esys-3.0.2-0 libtss2-mu0 libtss2-rc0 libtss2-tcti-device0                     # Debian 12
dpkg -s libtss2-esys-3.0.2-0 libtss2-mu0 libtss2-rc0 libtss2-tcti-device0 | grep -c 'Status: install ok installed'   # Debian 12
```

Oczekiwane: na Debianie 13 `systemd-creds has-tpm2` odpowiada `yes` (kod 0) z wierszem `+libraries`; na Debianie 12
ostatnie polecenie wypisuje `4`. **[od 0.3.0]** Pakiet przy instalacji użyje wariantu „TPM2 + klucz hosta”, gdy
maszyna ma TPM, a gdy to się nie uda – samego klucza hosta, i wypisze, którego wariantu użył i dlaczego.

### 3.2 Konta administratorów i SSH

**Gdzie:** Debian. **Co i po co:** serwerem administrują 1–3 osoby z Tier 0, każda z własnym kontem, wyłącznie kluczami
SSH, bez logowania roota i tylko z podsieci administratorów (zapora – 3.3). Konta są lokalne (serwer nie jest w
domenie).

Na stacji **PAW** (Tier 0 – nie na stacjach z AD Managerem, z których korzystają też osoby spoza Tier 0) utwórz klucz
chroniony hasłem, jeśli go nie masz (PowerShell, wbudowany klient OpenSSH; z kluczem sprzętowym FIDO2 użyj
`-t ed25519-sk`):

```powershell
ssh-keygen -t ed25519 -C 'adm-jan@PAW'      # podaj hasło klucza
```

Na Debianie (`groupadd` wykonujesz tylko raz; pierwsza osoba ma już konto z instalatora):

```bash
groupadd --system ssh-admins
usermod -aG sudo,ssh-admins adm-jan                     # pierwsza osoba – konto z instalatora
adduser --gecos 'Anna Nowak (Tier 0)' adm-anna          # każda kolejna osoba: nowe konto (zapyta o hasło – 3.1)…
usermod -aG sudo,ssh-admins adm-anna                    # …i te same grupy
```

Przy **pierwszym** połączeniu z PAW `ssh` pokaże odcisk klucza serwera i zapyta, czy mu ufasz. Zanim odpowiesz `yes`,
wyświetl odcisk na konsoli Debiana (w hiperwizorze) i porównaj oba:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Skopiuj klucz publiczny z PAW na serwer (logowanie hasłem jest jeszcze włączone; plik `.pub` nie jest tajny):

```powershell
Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub | ssh adm-jan@admng-wd.contoso.com 'umask 077; mkdir -p ~/.ssh; cat >> ~/.ssh/authorized_keys'
```

Osoba dodana później (gdy logowanie hasłem jest już wyłączone) przekazuje swój plik `.pub`, a root dopisuje go do
`/home/<konto>/.ssh/authorized_keys` (katalog 0700, plik 0600, właściciel – ta osoba).

Sprawdź logowanie **samym kluczem** z drugiego okna – musi się udać bez pytania o hasło konta na serwerze (pytanie
o hasło klucza jest w porządku):

```powershell
ssh -o PreferredAuthentications=publickey adm-jan@admng-wd.contoso.com
```

Dopiero wtedy wyłącz hasła i roota. **Nie zamykaj tej sesji**, dopóki sprawdzenie niżej nie przejdzie. Wycofanie
(z otwartej sesji albo z konsoli maszyny wirtualnej): `sudo rm /etc/ssh/sshd_config.d/10-admng.conf && sudo systemctl
reload ssh`.

```bash
cat > /etc/ssh/sshd_config.d/10-admng.conf <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
AuthenticationMethods publickey
AllowGroups ssh-admins
X11Forwarding no
EOF
sshd -t && systemctl reload ssh
passwd -l root
```

**Sprawdzenie:** nowe połączenie kluczem działa; `ssh -o PubkeyAuthentication=no adm-jan@admng-wd.contoso.com` kończy
się `Permission denied (publickey)`; `sshd -T | grep -E 'permitrootlogin|passwordauthentication|allowgroups'` pokazuje
`permitrootlogin no`, `passwordauthentication no`, `allowgroups ssh-admins`; `getent group sudo` – tylko osoby Tier 0.
Dopiero gdy nowe połączenie kluczem działa, zamknij starą sesję.

### 3.3 Zapora (nftables)

**Gdzie:** Debian. **Co i po co:** domyślnie wszystko jest zablokowane w obie strony; otwieramy tylko porty potrzebne
usłudze – przychodzące tylko od konkretnych nadawców. **[od 0.3.0]** Pakiet dostarczy ten sam zestaw reguł jako
przykład `/usr/share/admng-watchdog/nftables/admng-watchdog.nft`.

| Kierunek | Port | Druga strona | Po co |
|---|---|---|---|
| przychodzący | 22/tcp | 10.0.20.0/24 (w tym PAW) | SSH administratorów |
| przychodzący | 8443/tcp | 10.0.20.0/24 | API Watchdog (HTTPS + Kerberos) dla AD Manager |
| przychodzący | 5985/tcp | 10.0.1.10, 10.0.1.11 | OpenWEC – zdarzenia z kontrolerów |
| wychodzący | 53/udp+tcp | kontrolery | DNS |
| wychodzący | 88/udp+tcp | kontrolery | Kerberos |
| wychodzący | 123/udp | kontrolery | czas (chrony) |
| wychodzący | 389/tcp | kontrolery | LDAP z podpisem i szyfrowaniem |
| wychodzący | 445/tcp | kontrolery | SMB – odczyt SYSVOL |
| wychodzący | 464/udp+tcp | kontrolery | kpasswd – zmiana hasła konta usługi |
| wychodzący | 636/tcp | kontrolery | LDAPS (tylko gdy włączysz tę opcję) |
| wychodzący | 587/tcp | 10.0.1.30 (`smtp.contoso.com`) | e-mail (STARTTLS) |
| wychodzący | 80, 443/tcp | Internet | aktualizacje Debian i Microsoft, Teams Workflows |

PAW musi mieć adres w 10.0.20.0/24. Jeśli stoi w innej sieci, dopisz jej adres do reguły SSH – albo zawęź SSH do
samych adresów PAW (`ip saddr { <adresy PAW> } tcp dport 22 accept`), a port 8443 zostaw dla całej podsieci stacji.

```bash
cp -a /etc/nftables.conf /etc/nftables.conf.orig        # kopia na wypadek wycofania (8.1)
cat > /etc/nftables.conf <<'EOF'
#!/usr/sbin/nft -f
flush ruleset

define DCS        = { 10.0.1.10, 10.0.1.11 }
define ADMIN_NET  = 10.0.20.0/24
define SMTP_RELAY = 10.0.1.30

table inet admng {
    chain input {
        type filter hook input priority filter; policy drop;
        ct state established,related accept
        ct state invalid drop
        iif lo accept
        ip saddr $ADMIN_NET tcp dport { 22, 8443 } accept
        ip saddr $DCS tcp dport 5985 accept
        icmp type { echo-request, destination-unreachable, time-exceeded } accept
    }
    chain forward {
        type filter hook forward priority filter; policy drop;
    }
    chain output {
        type filter hook output priority filter; policy drop;
        ct state established,related accept
        oif lo accept
        ip daddr $DCS udp dport { 53, 88, 123, 464 } accept
        ip daddr $DCS tcp dport { 53, 88, 389, 445, 464, 636 } accept
        ip daddr $SMTP_RELAY tcp dport 587 accept
        tcp dport { 80, 443 } accept
        icmp type echo-request accept
    }
}
EOF
nft -c -f /etc/nftables.conf && echo 'składnia OK'
```

Zastosuj reguły z „siatką bezpieczeństwa”: jeśli odetniesz sobie SSH, systemd usunie reguły sam po 3 minutach. Timer
systemd działa niezależnie od Twojej sesji – także wtedy, gdy połączenie zostanie zerwane. Wpisz to jako **jedno**
polecenie (`&&` sprawia, że reguły wczytają się tylko z uzbrojonym timerem; pierwsza część usuwa timer z poprzedniej
próby):

```bash
systemctl stop admng-nft-rescue.timer 2>/dev/null; systemd-run --on-active=180 --timer-property=RemainAfterElapse=no --unit=admng-nft-rescue /usr/sbin/nft flush ruleset && nft -f /etc/nftables.conf
```

**Nie zamykaj tej sesji.** Z PAW otwórz **nowe** połączenie `ssh adm-jan@admng-wd.contoso.com`. Jeśli NIE działa – nic
nie rób: po 3 minutach reguły znikną same; popraw `ADMIN_NET` i powtórz to samo polecenie. Dopiero gdy nowe połączenie
działa, w starej sesji wykonaj:

```bash
systemctl stop admng-nft-rescue.timer
systemctl enable --now nftables
```

Gdy wszystko zawiedzie: zaloguj się na konsoli maszyny wirtualnej w hiperwizorze na swoje konto (hasło lokalne – 3.1),
wpisz `sudo nft flush ruleset`, popraw `ADMIN_NET` w `/etc/nftables.conf` i wykonaj `sudo systemctl restart nftables`
(gdy usługa `nftables` jest już włączona, sam `flush` działa tylko do restartu serwera).

Reguły przyjmują tylko IPv4. Jeżeli w sieci serwerów używacie IPv6, dopisz odpowiedniki `ip6` (i ICMPv6 dla wykrywania
sąsiadów); w przeciwnym razie ruch IPv6 jest blokowany.

**Sprawdzenie:**

- `nft list ruleset` – pokazuje tabelę `inet admng`;
- `systemctl list-timers admng-nft-rescue.timer` – brak timera (siatka bezpieczeństwa wyłączona);
- z ADM-WS01: `Test-NetConnection admng-wd.contoso.com -Port 22` – `TcpTestSucceeded : True`;
- z komputera spoza 10.0.20.0/24 to samo – `False`;
- na Debianie: `getent hosts DC01.contoso.com` i `curl -sI https://packages.microsoft.com | head -1` – odpowiadają.

### 3.4 DNS i czas

**Gdzie:** Debian. **Co i po co:**

- **DNS.** Serwer pyta wyłącznie DNS kontrolerów AD – inaczej nie znajdzie nazw DC ani własnego rekordu.
- **Czas.** Kerberos odrzuca uwierzytelnienie, gdy zegary różnią się o więcej niż **5 minut** – wtedy przestaje działać
  wszystko: LDAP, SYSVOL, API, WEF. Serwer synchronizuje czas z kontrolerami (tak jak komputery w domenie). Ten sam
  czas jest też potrzebny do dopasowania zdarzeń z dzienników DC do zmian. **[od 0.3.0]** Watchdog ostrzega już przy
  różnicy 60 s, a od 240 s zgłasza błąd.
- **Serwery czasu Windows** zgłaszają zwykle duży „rozrzut” (root dispersion – kilka sekund), a chrony domyślnie
  odrzuca źródło, którego łączny błąd przekracza 3 s. Dlatego podnosimy ten limit (`maxdistance 16`, zalecenie z FAQ
  chrony dla serwerów Windows) – w osobnym pliku w `/etc/chrony/conf.d`, bo pliki `sources.d` mogą zawierać tylko
  źródła.

```bash
ls -l /etc/resolv.conf       # ma być zwykłym plikiem; jeśli to dowiązanie (resolvconf, systemd-resolved) – ustaw DNS tam
cp -a /etc/resolv.conf /etc/resolv.conf.orig; cp -a /etc/chrony/chrony.conf /etc/chrony/chrony.conf.orig   # kopie (8.1)
cat > /etc/resolv.conf <<'EOF'
search contoso.com
nameserver 10.0.1.10
nameserver 10.0.1.11
EOF

sed -i 's/^pool /#pool /' /etc/chrony/chrony.conf          # wyłącz publiczne źródła Debiana
cat > /etc/chrony/sources.d/ad-dcs.sources <<'EOF'
server DC01.contoso.com iburst
server DC02.contoso.com iburst
EOF
printf 'maxdistance 16.0\n' > /etc/chrony/conf.d/admng-ad.conf   # kontrolery Windows mają duży rozrzut czasu
systemctl restart chrony
```

**Sprawdzenie:**

```bash
getent hosts DC01.contoso.com DC02.contoso.com
host -t SRV _kerberos._tcp.contoso.com
host admng-wd.contoso.com; host 10.0.1.25
chronyc -N sources; chronyc tracking | grep -E 'Leap status|System time'
chronyc ntpdata DC01.contoso.com | grep -E 'Root (delay|dispersion)'
```

Oczekiwane: adresy 10.0.1.10 i 10.0.1.11; rekordy SRV obu kontrolerów na porcie 88; `admng-wd.contoso.com has address
10.0.1.25` (bez „is an alias for”) i `25.1.0.10.in-addr.arpa domain name pointer admng-wd.contoso.com.`; w `chronyc -N
sources` wiersz `^* DC01.contoso.com` (gwiazdka = wybrane źródło; `-N` pokazuje nazwy z pliku źródeł, a nie z
odwrotnego DNS), `Leap status : Normal`, przesunięcie rzędu milisekund; `Root dispersion` kontrolera – zwykle od
ułamka sekundy do kilku sekund. Gdy przy kontrolerze widać `?`, `x` albo `Leap status : Not synchronised`:

- `Root dispersion` (plus połowa `Root delay`) z ostatniego polecenia bliskie 16 s lub większe albo brak pliku
  `/etc/chrony/conf.d/admng-ad.conf` – chrony odrzuca źródło jako zbyt niedokładne; sprawdź plik i
  `systemctl restart chrony`;
- emulator PDC nie ma własnego źródła czasu (duży rozrzut rośnie z czasem) – popraw je (1.4).

### 3.5 Klient Kerberos i prywatne pliki konfiguracji

**Gdzie:** Debian. **Co i po co:** usługa, OpenWEC i narzędzie keytabu używają własnych plików w `/etc/admng-watchdog`
(systemowe `/etc/krb5.conf` i `/etc/ldap/ldap.conf` zostają nietknięte) – tworząc je teraz, przygotowujesz dokładnie
tę konfigurację, której użyje usługa. **[od 0.3.0]** Pakiet dostarczy pliki o tej samej treści (z przykładowymi
nazwami); przy jego instalacji `dpkg` zapyta o te pliki („File on system created by you or by a script”) – odpowiedz
`N`, żeby zachować swoje wersje.

```bash
install -d -m 0755 /etc/admng-watchdog

cat > /etc/admng-watchdog/krb5.conf <<'EOF'
[libdefaults]
    default_realm = CONTOSO.COM
    dns_lookup_kdc = false
    dns_lookup_realm = false
    dns_canonicalize_hostname = false
    rdns = false
    forwardable = false
    udp_preference_limit = 0
    allow_weak_crypto = false
    permitted_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tgs_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tkt_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96

[realms]
    CONTOSO.COM = {
        kdc = DC01.contoso.com
        kdc = DC02.contoso.com
        primary_kdc = DC01.contoso.com
        kpasswd_server = DC01.contoso.com
        admin_server = DC01.contoso.com
    }

[domain_realm]
    .contoso.com = CONTOSO.COM
    contoso.com = CONTOSO.COM
EOF

cat > /etc/admng-watchdog/ldap.conf <<'EOF'
# AdMng Watchdog – private libldap configuration (LDAPCONF)
SASL_NOCANON   on
SASL_SECPROPS  minssf=56,noanonymous,noplain
REFERRALS      off
TLS_REQCERT    demand
EOF

cat > /etc/admng-watchdog/smb-client.conf <<'EOF'
[global]
    workgroup = CONTOSO
    realm = CONTOSO.COM
    security = ads
    client min protocol = SMB3_00
    client signing = required
    client ipc signing = required
    client smb encrypt = desired
    client use kerberos = required
    disable netbios = yes
    name resolve order = host
    lock directory = /run/admng-watchdog/samba
    state directory = /run/admng-watchdog/samba
    cache directory = /run/admng-watchdog/samba
    private dir = /run/admng-watchdog/samba
    log level = 0
EOF
chmod 0644 /etc/admng-watchdog/*.conf
```

Najważniejsze ustawienia i dlaczego:

| Ustawienie | Dlaczego |
|---|---|
| `permitted_enctypes` … tylko AES, `allow_weak_crypto = false` | RC4 i DES są wyłączone – zgodnie z kontem usługi (0x18) i z wycofywaniem RC4 na kontrolerach |
| `dns_lookup_kdc = false` + jawne `kdc` | kontrolery są wpisane na stałe – podmiana rekordów SRV w DNS nie przekieruje uwierzytelniania. Przy dodaniu lub wycofaniu DC zaktualizuj listę |
| `rdns = false`, `dns_canonicalize_hostname = false` | nazwy usług (SPN) są budowane z nazw, które podajesz, a nie z odwrotnego DNS – błędny rekord PTR niczego nie psuje |
| `primary_kdc`, `kpasswd_server` = emulator PDC | zmiana hasła trafia na PDC, a przy świeżo zmienionym haśle klient pyta PDC, zanim zmiana dojdzie do DC02. Po przeniesieniu roli PDC zmień te wiersze (usługa ostrzeże) |
| `udp_preference_limit = 0` | Kerberos zawsze przez TCP – bilety AD bywają za duże dla UDP |
| `forwardable = false` | bilety usługi nie są przekazywalne (i tak blokuje to flaga „nie może być delegowane”) |
| `SASL_SECPROPS minssf=56` | połączenie LDAP **musi** być szyfrowane – bez tego wiązanie się nie uda (zamiast cichej pracy bez szyfrowania) |
| `SASL_NOCANON on` | libldap nie zamienia nazwy DC na nazwę z odwrotnego DNS |
| `client signing = required`, `client use kerberos = required` | odczyt SYSVOL tylko z podpisywaniem SMB i tylko Kerberosem (bez NTLM) |

**Sprawdzenie** – uwierzytelnij się kontem testowym (to sprawdza DNS, port 88, czas i AES, zanim powstanie konto
usługi); bilet trafia do pamięci i znika po zakończeniu polecenia:

```bash
KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=MEMORY:test kinit -V jan.kowalski@CONTOSO.COM
```

Oczekiwane: po podaniu hasła `Authenticated to Kerberos v5`. Błędy – rozdz. 9.1.

### 3.6 Keytab konta `ADMNG-WD$` – tworzenie i testy

> **W skrócie.** Co zmienia: hasło konta `ADMNG-WD$` w AD (jednorazowe → losowe, 120 znaków, którego nikt nie zna);
> tworzy plik `/etc/admng-watchdog/admng-wd.keytab`. Gdzie: Debian, root, zaraz po 2.5. Ile trwa: kilka minut razem
> z testami. Wynik: keytab z sześcioma wpisami, testy 1–3 przechodzą. Wycofanie: usunięcie keytabu (8.1) i
> wyłączenie konta (8.2).

**Gdzie:** Debian, zaraz po 2.5 (masz hasło jednorazowe). **Co i po co:** zastępujemy hasło jednorazowe losowym hasłem
120 znaków, którego nikt nie zna, i zapisujemy keytab – plik z kluczami Kerberos konta. Hasło istnieje tylko w pamięci
powłoki i zaraz jest usuwane; na dysk trafiają wyłącznie klucze.

**[od 0.3.0]** Po instalacji pakietu ten krok wykona jedno polecenie: `sudo admng-watchdog-keytab init` (zapyta o
hasło jednorazowe i zrobi to samo co niżej, łącznie ze sprawdzeniem na każdym DC).

Do tego czasu – albo jako procedura awaryjna – użyj narzędzi MIT Kerberos. Zmianę hasła, keytab oraz testy 1 i 3
sprawdziliśmy w laboratorium (Samba AD); test 2 (LDAP z szyfrowaniem GSSAPI) – na testowym serwerze OpenLDAP, bo
laboratoryjny DC Samba nie ma SPN `ldap/`. Na kontrolerach Windows potwierdzi je pilotaż.

Keytab musi zawierać trzy nazwy (konto oraz dwa SPN-y, z których korzystają API i OpenWEC), każdą w dwóch typach
szyfrowania AES, z tą samą „solą”. Dla kont komputerów w AD sól to: `OBSZAR` + `host` + nazwa konta małymi literami
bez `$` + `.` + domena małymi literami, czyli `CONTOSO.COMhostadmng-wd.contoso.com`. Błędna sól daje keytab, który
wygląda poprawnie, ale nie działa – dlatego testy są obowiązkowe. Kolejność wpisów też ma znaczenie: pierwszy wpis to
nazwa konta `ADMNG-WD$` – z niego biblioteka Kerberos bierze tożsamość, gdy usługa loguje się kluczem z keytabu
(test 2). Blok niżej zachowuje tę kolejność.

Ten sam blok służy do zmiany ręcznej (3.7) i do obejścia, gdy `kpasswd` odmawia (niżej): jeśli keytab już istnieje,
blok najpierw wczytuje obecne klucze – zostają w nowym pliku, więc wystawione wcześniej bilety nadal działają.

Wszystkie polecenia tego rozdziału wykonujesz w jednej powłoce roota. Jeśli otworzysz nową sesję (albo połączenie
zostanie zerwane), zacznij od dwóch poleceń `export` z pierwszych wierszy bloku poniżej – inaczej narzędzia Kerberos
użyją systemowego `/etc/krb5.conf` i pokażą mylące błędy.

```bash
umask 077                                              # nowe pliki tylko dla roota (do końca tej sesji)
groupadd --system admng-keytab 2>/dev/null || true     # grupa czytelników keytabu (od 0.3.0 pakiet doda do niej usługi)
export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf
export KRB5CCNAME=FILE:/dev/shm/admng-test-cc          # bilety w pamięci (tmpfs), nie na dysku
trap 'kdestroy 2>/dev/null' EXIT                       # bilety znikają przy wyjściu z powłoki
P='ADMNG-WD$@CONTOSO.COM'
KT=/etc/admng-watchdog/admng-wd.keytab
SALT='CONTOSO.COMhostadmng-wd.contoso.com'

read -rs -p 'Hasło jednorazowe: ' OTP; echo
NEW=$(openssl rand -base64 90 | tr -d '\n')            # 120 losowych znaków – tylko w pamięci powłoki

# 1) zmiana hasła protokołem Kerberos (port 464 na DC01) – stare hasło, nowe, nowe;
#    przy odmowie keytab powstaje z hasła jednorazowego (niżej: „Gdy kpasswd odmawia”)
if ! printf '%s\n%s\n%s\n' "$OTP" "$NEW" "$NEW" | kpasswd "$P"; then
    echo 'UWAGA: kpasswd odmówił – keytab powstanie z hasła jednorazowego'; NEW="$OTP"
fi

# 2) numer wersji klucza (kvno) – z biletu do własnego SPN host/
KVNO=
if printf '%s\n' "$NEW" | kinit "$P" >/dev/null; then
    KVNO=$(kvno host/admng-wd.contoso.com | sed -n 's/.*kvno = \([0-9]*\).*/\1/p'); kdestroy
fi
echo "kvno: ${KVNO:-BRAK}"

# 3) keytab: najpierw obecne klucze (jeśli plik już jest), potem 3 nazwy × 2 typy AES – do pliku tymczasowego
# 4) uprawnienia, potem podmiana pliku jednym ruchem
if [ -n "$KVNO" ]; then
  rm -f "$KT.new"
  { [ -f "$KT" ] && printf 'rkt %s\n' "$KT"
    for pr in "$P" 'HTTP/admng-wd.contoso.com@CONTOSO.COM' 'host/admng-wd.contoso.com@CONTOSO.COM'; do
      for e in aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96; do
        printf 'addent -password -p %s -k %s -e %s -s %s\n%s\n' "$pr" "$KVNO" "$e" "$SALT" "$NEW"
      done
    done
    printf 'wkt %s.new\nquit\n' "$KT"; } | ktutil >/dev/null
  chown root:admng-keytab "$KT.new"; chmod 0640 "$KT.new"; mv "$KT.new" "$KT"
else
  echo 'Keytab NIE został zapisany – zobacz komunikat kpasswd/kinit powyżej'
fi
unset NEW OTP
```

Oczekiwane: `kpasswd` kończy się wierszem `Password changed.`, a `kvno:` pokazuje liczbę (np. `3`). `kvno: BRAK` znaczy,
że keytab nie powstał – przyczynę pokazuje komunikat `kpasswd` albo `kinit` (rozdz. 9.1). Wiersz `UWAGA: kpasswd
odmówił…` – patrz niżej.

#### Gdy `kpasswd` odmawia

Blok zapisuje wtedy keytab z **hasła jednorazowego** – usługa i testy będą działać, ale hasło zna człowiek. Co dalej,
zależy od komunikatu `kpasswd` nad wierszem `UWAGA`:

- `Password change rejected` – minimalny wiek hasła w domenie liczy się od ustawienia hasła jednorazowego.
  Administrator Tier 0 przechowuje hasło jednorazowe w menedżerze haseł do następnego dnia. Następnego dnia (gdy minie
  minimalny wiek hasła) wykonaj na Debianie cały blok jeszcze raz z **tym samym** hasłem jednorazowym – **bez** nowego
  resetu na PAW, bo reset znów uruchamia minimalny wiek hasła. Blok zachowa obecne klucze. Potem administrator usuwa
  hasło z menedżera. **[od 0.3.0]** Narzędzie `admng-watchdog-keytab` robi to samo automatycznie i pokazuje stan
  `otpActive`.
- `Access denied` – inna sytuacja, rozdz. 9.1.
- inny błąd (np. brak połączenia z portem 464 na DC01) – usuń przyczynę (9.1) i wykonaj cały blok jeszcze raz z tym
  samym hasłem jednorazowym.

#### Uprawnienia keytabu

Plik czyta tylko root i członkowie grupy `admng-keytab` (**[od 0.3.0]** po instalacji pakietów: konto usługi
`admng-watchdog` i konto `openwec`):

```bash
stat -c '%U:%G %a %n' /etc/admng-watchdog/admng-wd.keytab
```

Oczekiwane: `root:admng-keytab 640 /etc/admng-watchdog/admng-wd.keytab`.

#### Testy

**Test 1 – zawartość i uwierzytelnienie kluczem:**

```bash
klist -k -t -e /etc/admng-watchdog/admng-wd.keytab
kinit -f -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'
klist -e -f
kvno -k /etc/admng-watchdog/admng-wd.keytab HTTP/admng-wd.contoso.com host/admng-wd.contoso.com
```

Oczekiwane (kvno przykładowo 3):

```text
KVNO Timestamp         Principal
---- ----------------- --------------------------------------------------------
   3 …                 ADMNG-WD$@CONTOSO.COM (aes256-cts-hmac-sha1-96)
   3 …                 ADMNG-WD$@CONTOSO.COM (aes128-cts-hmac-sha1-96)
   3 …                 HTTP/admng-wd.contoso.com@CONTOSO.COM (aes256-cts-hmac-sha1-96)
   3 …                 HTTP/admng-wd.contoso.com@CONTOSO.COM (aes128-cts-hmac-sha1-96)
   3 …                 host/admng-wd.contoso.com@CONTOSO.COM (aes256-cts-hmac-sha1-96)
   3 …                 host/admng-wd.contoso.com@CONTOSO.COM (aes128-cts-hmac-sha1-96)
Default principal: ADMNG-WD$@CONTOSO.COM
…  krbtgt/CONTOSO.COM@CONTOSO.COM
        renew until …, Flags: RIA
        Etype (skey, tkt): aes256-cts-hmac-sha1-96, aes256-cts-hmac-sha1-96
HTTP/admng-wd.contoso.com@CONTOSO.COM: kvno = 3, keytab entry valid
host/admng-wd.contoso.com@CONTOSO.COM: kvno = 3, keytab entry valid
```

- `keytab entry valid` – bilet wystawiony przez KDC da się odszyfrować kluczem z keytabu: sól, kvno i typy szyfrowania
  się zgadzają.
- W flagach biletu **nie ma `F`** (forwardable), choć `kinit -f` o nią prosił – to dowód, że flaga „konto jest poufne i
  nie może być delegowane” działa.
- `aes256-cts-hmac-sha1-96` – konto używa AES.
- Po zmianie ręcznej (3.7) albo obejściu (wyżej) keytab ma kilka wersji kluczy – pierwszy wiersz to nadal
  `ADMNG-WD$@CONTOSO.COM`.

**Test 2 – LDAP z podpisem i szyfrowaniem.** Zmienna `KRB5_CLIENT_KTNAME` pozwala bibliotece Kerberos samej pobrać
bilet z keytabu – tak będzie działać usługa:

```bash
kdestroy
export KRB5_CLIENT_KTNAME=FILE:/etc/admng-watchdog/admng-wd.keytab
export LDAPCONF=/etc/admng-watchdog/ldap.conf
ldapsearch -Y GSSAPI -H ldap://DC01.contoso.com -b 'DC=contoso,DC=com' -s base '(objectClass=*)' distinguishedName
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -b 'DC=contoso,DC=com' '(sAMAccountName=ADMNG-WD$)' msDS-KeyVersionNumber
```

Oczekiwane:

```text
SASL/GSSAPI authentication started
SASL username: ADMNG-WD$@CONTOSO.COM
SASL SSF: 256
SASL data security layer installed.
…
result: 0 Success

dn: CN=ADMNG-WD,OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com
msDS-KeyVersionNumber: 3
```

- `SASL SSF: 256` – połączenie jest szyfrowane (spełnia wymóg podpisywania LDAP na Windows Server 2025).
- `SASL username: ADMNG-WD$@CONTOSO.COM` – tożsamość z pierwszego wpisu keytabu.
- `msDS-KeyVersionNumber` równe najnowszemu kvno w keytabie – keytab jest aktualny.
- Odczyt `tokenGroups` (role operatorów w API) i usuniętych obiektów sprawdzisz po nadaniu praw w 2.6 – tam jest test.

Powtórz pierwsze polecenie z `-H ldap://DC02.contoso.com` (po replikacji hasła).

**Test 3 – odczyt SYSVOL.** Blok jest samodzielny (ma własne `export`), więc możesz go powtórzyć w nowej sesji
(2.7, 9.4), także gdy usługa już działa: stan Samby trafia do tymczasowego katalogu w `/dev/shm`, a nie do katalogu
usługi `/run/admng-watchdog`.

```bash
export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=FILE:/dev/shm/admng-test-cc
sed 's#/run/admng-watchdog/samba#/dev/shm/admng-test-samba#' /etc/admng-watchdog/smb-client.conf > /dev/shm/admng-test-smb.conf
install -d -m 0700 /dev/shm/admng-test-samba
kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'   # smbclient sam nie pobierze biletu z keytabu
smbclient //DC01.contoso.com/SYSVOL -s /dev/shm/admng-test-smb.conf --use-kerberos=required -N -m SMB3 \
    -c 'ls contoso.com/Policies/*'
echo "kod: $?"
kdestroy; rm -rf /dev/shm/admng-test-samba /dev/shm/admng-test-smb.conf
```

Oczekiwane: lista folderów GPO, m.in. `{31B2F340-016D-11D2-945F-00C04FB984F9}` (Default Domain Policy) i
`{6AC1786C-016F-11D2-945F-00C04FB984F9}` (Default Domain Controllers Policy), oraz `kod: 0`. `-N` jest obowiązkowe
(bez niego `smbclient` czeka na hasło). Bez biletu TGT w pamięci podręcznej (polecenie `kinit` w bloku) `smbclient`
kończy się błędem `NT_STATUS_INVALID_PARAMETER` – 9.4.

**Sprzątanie po testach:**

```bash
kdestroy 2>/dev/null; unset KRB5_CLIENT_KTNAME LDAPCONF KRB5CCNAME KRB5_CONFIG; trap - EXIT
```

**Sprawdzenie po stronie AD** (PAW):

```powershell
Get-ADComputer ADMNG-WD -Properties pwdLastSet, msDS-KeyVersionNumber |
    Format-List Name, @{ n = 'pwdLastSet'; e = { [datetime]::FromFileTime($_.pwdLastSet) } }, msDS-KeyVersionNumber
```

Oczekiwane: `pwdLastSet` sprzed kilku minut (hasło jednorazowe już nie działa) i `msDS-KeyVersionNumber` równe kvno z
keytabu.

### 3.7 Zmiana hasła konta usługi co 30 dni

**Gdzie:** Debian. **Co i po co:** regularna zmiana hasła ogranicza wartość skradzionego keytabu – po zmianie stare
klucze przestają działać (najpóźniej po kolejnej zmianie). Nie unieważnia jednak certyfikatu, o który ktoś z keytabem
zdążyłby poprosić u urzędu certyfikacji – przed tym chronią wpisy odmowy na szablonach (2.5), a po wycieku keytabu
także wyłączenie konta i odwołanie certyfikatów (2.5, „Gdy keytab mógł wyciec”).

**[od 0.3.0]** Pakiet zainstaluje:

- narzędzie `admng-watchdog-keytab` z poleceniami `init`, `rotate --if-older-than 30d`, `verify`, `status`;
- jednostkę `admng-watchdog-keytab.service` (uruchamiana jako root, z ograniczeniami systemd) i timer
  `admng-watchdog-keytab.timer` – codziennie ok. 04:20 (z losowym przesunięciem do 30 minut; zaległe uruchomienie po
  starcie serwera); zmiana następuje tylko wtedy, gdy hasło ma ponad 30 dni – albo przy każdym uruchomieniu, dopóki
  keytab pochodzi z hasła jednorazowego (stan `otpActive`, 3.6);
- plik stanu `/var/lib/admng-watchdog-keytab/status.json` (bez sekretów): wersja klucza, data ostatniej zmiany i próby,
  wynik (`ok`, `otpActive`, `changeFailed`, `keytabWriteFailed`, `replicationPending`, `verifyFailed`), termin
  następnej zmiany.

Przy zmianie narzędzie: zmienia hasło na PDC, zapisuje nowy keytab obok starego i podmienia go jednym ruchem
(czytelnicy nigdy nie widzą połowy pliku), zachowuje klucze bieżącej i poprzedniej wersji, sprawdza nowe klucze na
każdym DC (czeka na replikację do 15 minut). Tak samo działa `init` przy naprawie (nowe hasło jednorazowe): klucze
dotychczasowej wersji z obecnego keytabu zostają, więc wystawione wcześniej bilety nadal działają. Usługi czytają
keytab przy każdym uwierzytelnieniu – restart nie jest potrzebny.

**[od 0.3.0]** Ręczne uruchomienie i sprawdzenie:

```bash
systemctl list-timers admng-watchdog-keytab.timer
systemctl start admng-watchdog-keytab.service       # to samo, co robi timer: zmiana tylko gdy hasło > 30 dni
journalctl -u admng-watchdog-keytab -n 20 --no-pager
admng-watchdog-keytab status
admng-watchdog-keytab verify
klist -k /etc/admng-watchdog/admng-wd.keytab | awk 'NR>3 {print $1}' | sort -u    # wersje kluczy w keytabie
```

Oczekiwane: timer z datą następnego uruchomienia; `status` z wynikiem `ok`; `verify` pokazuje nazwy, kvno i typy
szyfrowania bez błędów; w keytabie utrzymywanym przez narzędzie najwyżej dwie wersje kluczy, z których nowsza równa się
`msDS-KeyVersionNumber` (PAW: `Get-ADComputer ADMNG-WD -Properties msDS-KeyVersionNumber`, albo `ldapsearch` z 3.6).

**Zmiana ręczna przez nowe hasło jednorazowe** (do czasu pakietu – co 30 dni; tak samo naprawisz keytab, który
przestał pasować do AD, np. po odtworzeniu maszyny z kopii):

1. PAW – nowe hasło jednorazowe (wygeneruj `$plain` i `$otp` jak w 2.5):
   `Set-ADAccountPassword -Identity 'ADMNG-WD$' -Reset -NewPassword $otp`.
2. Debian – **cały** blok z 3.6 z nowym hasłem jednorazowym. Keytab już istnieje, więc blok najpierw wczytuje obecne
   klucze – poprzednia wersja zostaje. Jeśli `kpasswd` odmówi (minimalny wiek hasła), postępuj jak w 3.6, „Gdy
   `kpasswd` odmawia” – następnego dnia ten sam blok z tym samym hasłem, bez nowego resetu.
3. Sprawdzenie: `klist -k /etc/admng-watchdog/admng-wd.keytab` pokazuje starą i nową wersję. Nowa jest **o 2 wyższa** –
   reset hasła przez administratora i zmiana na losowe hasło podnoszą kvno po jednym. Testy 1–3 z 3.6 przechodzą.
   W ręcznej procedurze starszych wersji nie usuwamy (keytab może mieć więcej niż dwie wersje); narzędzie
   `admng-watchdog-keytab` zostawia tylko wersję bieżącą i poprzednią.

### 3.8 Certyfikat TLS dla API

**Gdzie:** Debian i PAW. **Co i po co:** AD Manager łączy się z usługą przez HTTPS. Certyfikat musi zawierać nazwę
`admng-wd.contoso.com` (SAN) i przeznaczenie „Uwierzytelnianie serwera”. Usługa czyta go z plików PEM:
`/etc/admng-watchdog/tls/admng-wd.crt` (certyfikat, potem certyfikaty pośrednie) i `/etc/admng-watchdog/tls/admng-wd.key`
(klucz prywatny). Usługa przyjmuje tylko TLS 1.2 i 1.3.

#### Wariant A – certyfikat z firmowego CA (gdy 2.1 znalazło urząd z szablonem WebServer)

Klucz prywatny powstaje na serwerze i nigdy go nie opuszcza; do CA trafia tylko wniosek (CSR).

Debian:

```bash
install -d -m 0750 /etc/admng-watchdog/tls
cd /etc/admng-watchdog/tls
umask 077
openssl req -new -newkey rsa:3072 -nodes -keyout admng-wd.key -out admng-wd.csr \
    -subj '/CN=admng-wd.contoso.com' -addext 'subjectAltName=DNS:admng-wd.contoso.com' -addext 'extendedKeyUsage=serverAuth'
cp admng-wd.csr /home/adm-jan/ && chown adm-jan /home/adm-jan/admng-wd.csr
```

PAW (wniosek nie jest tajny – kopiujesz go zwykłym `scp`):

```powershell
scp adm-jan@admng-wd.contoso.com:admng-wd.csr .
certreq -submit -attrib "CertificateTemplate:WebServer" -config "CA01.contoso.com\Contoso-Issuing-CA" admng-wd.csr admng-wd.cer
certutil -config "CA01.contoso.com\Contoso-Issuing-CA" -ca.chain chain.p7b
scp admng-wd.cer chain.p7b adm-jan@admng-wd.contoso.com:
```

Jeśli `certreq` zgłosi wniosek „oczekujący” (szablon wymaga zatwierdzenia), po zatwierdzeniu pobierz certyfikat:
`certreq -retrieve -config "CA01.contoso.com\Contoso-Issuing-CA" <RequestId> admng-wd.cer`.

Debian:

```bash
cd /etc/admng-watchdog/tls
openssl x509 -in /home/adm-jan/admng-wd.cer -out admng-wd.pem 2>/dev/null || openssl x509 -inform DER -in /home/adm-jan/admng-wd.cer -out admng-wd.pem
openssl pkcs7 -inform DER -in /home/adm-jan/chain.p7b -print_certs -out chain.pem 2>/dev/null || \
    openssl pkcs7 -inform PEM -in /home/adm-jan/chain.p7b -print_certs -out chain.pem
cat admng-wd.pem chain.pem > admng-wd.crt            # najpierw certyfikat serwera, potem urzędy
chmod 0644 admng-wd.crt; rm admng-wd.csr /home/adm-jan/admng-wd.c* /home/adm-jan/chain.p7b
```

**[od 0.3.0]** Po instalacji pakietu (rozdz. 5) grupa `admng-watchdog` musi móc czytać klucz – pakiet ustawia
właściciela i tryby katalogu `/etc/admng-watchdog` sam; sprawdź to ostatnim poleceniem ze „Sprawdzenia”. Gdy
certyfikat już leży w tym miejscu, pakiet nie generuje własnego.

**Sprawdzenie:**

```bash
openssl verify -CAfile chain.pem admng-wd.pem
openssl x509 -in admng-wd.pem -noout -subject -enddate -ext subjectAltName,extendedKeyUsage
[ "$(openssl x509 -in admng-wd.pem -noout -pubkey | sha256sum)" = "$(openssl pkey -in admng-wd.key -pubout | sha256sum)" ] && echo 'klucz pasuje'
stat -c '%U:%G %a %n' /etc/admng-watchdog/tls /etc/admng-watchdog/tls/*     # po instalacji pakietu
```

Oczekiwane: `admng-wd.pem: OK`; `subject=CN=admng-wd.contoso.com`, `DNS:admng-wd.contoso.com`, `TLS Web Server
Authentication`; `klucz pasuje`; po instalacji pakietu katalog `root:admng-watchdog 750`, `admng-wd.key`
`root:admng-watchdog 640`, `admng-wd.crt` `… 644`. Stacje w domenie ufają firmowemu CA, więc AD Manager połączy się bez
dodatkowych pytań. Odnowienie: przed datą `notAfter` powtórz wariant A (nowy klucz i nowy wniosek); usługa sama
wczytuje podmienione pliki.

#### Wariant B – certyfikat generowany na serwerze (gdy nie ma CA) [od 0.3.0]

W laboratorium bez urzędu certyfikacji (bez 1.7) ten wariant wykonasz dopiero z pakietem 0.3.0.

Gdy w `/etc/admng-watchdog/tls/` nie ma certyfikatu, pakiet przy instalacji
wygeneruje certyfikat z podpisem własnym (ECDSA P-256, ważny 825 dni, nazwa z `hostname --fqdn` – dlatego 3.1 ustawia
`/etc/hosts`) i wypisze jego odcisk SHA-256. Odcisk pokaże też w każdej chwili `sudo admng-watchdog cert-fingerprint`
(i wpis 1008 w dzienniku przy każdym starcie usługi); odnowienie: `sudo admng-watchdog cert-renew-self-signed`.

Takiemu certyfikatowi stacje nie ufają, dlatego AD Manager przy **pierwszym** połączeniu pokaże okno z odciskiem
certyfikatu. Porównaj go z wynikiem polecenia na serwerze (przez SSH) i dopiero wtedy kliknij „Ufam temu
certyfikatowi” – AD Manager zapamięta odcisk. Jeśli certyfikat się zmieni (odnowienie, podmiana), AD Manager **nie**
przyjmie go po cichu: pokaże stary i nowy odcisk i znów poprosi o potwierdzenie. Niezgodność nazwy nigdy nie jest
akceptowana. (6.1)

Odcisk dowolnego certyfikatu sprawdzisz też bez pakietu:
`openssl x509 -in /etc/admng-watchdog/tls/admng-wd.crt -noout -fingerprint -sha256`.

### 3.9 Kopie zapasowe

**[od 0.3.0]** **Co zrobi usługa:** codziennie wykonuje lokalną kopię bazy (spójną kopię pliku bazy SQLite) do
`/var/lib/admng-watchdog/backup/` i trzyma **7** ostatnich. Te kopie chronią przed błędem logicznym (np. uszkodzoną
migracją), ale **nie** przed utratą dysku czy maszyny – leżą na tym samym dysku.

**Zalecenie (jedno, ale ważne):** historia zmian ma obejmować 3 lata, więc zrób **kopię zapasową całej maszyny
wirtualnej** na poziomie hiperwizora – razem ze stanem vTPM, jeśli go używasz. Taka kopia zawiera katalog usługi,
keytab i dane – jest zasobem Tier 0: dostęp do systemu kopii mają tylko administratorzy Tier 0, a kopie powinny być
szyfrowane. W wariancie „klucz hosta” (bez TPM2) kopia dysku zawiera też klucz, którym odszyfrowuje się sekrety kanałów.

Co musi zawierać odtworzenie:

| Element | Gdzie | Uwagi |
|---|---|---|
| Baza i kopie | `/var/lib/admng-watchdog/` (`watchdog.db`, `backup/`) | historia 3 lat |
| Klucze sekretów kanałów | `/var/lib/admng-watchdog/keys/` | zaszyfrowane kluczem z poświadczenia systemd |
| Poświadczenie systemd z kluczem | `/etc/admng-watchdog/credentials/dp-key.cred` | odszyfrujesz je tylko z tym samym kluczem hosta (plik `/var/lib/systemd/credential.secret` – jest w kopii dysku) i – w wariancie TPM2 – tym samym vTPM; nie zależy od stanu rozruchu (Secure Boot, aktualizacje UEFI) |
| Konfiguracja, certyfikat, keytab | `/etc/admng-watchdog/` | keytab może być nieaktualny, jeśli po wykonaniu kopii nastąpiła zmiana hasła |

**[od 0.3.0]** Scenariusze:

- **Odtworzenie tej samej maszyny wirtualnej** (dysk + vTPM): wszystko działa. Sprawdź tylko keytab – zakładka Stan →
  Konto usługi albo na Debianie:

  ```bash
  export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=FILE:/dev/shm/admng-test-cc
  kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM' && kvno -k /etc/admng-watchdog/admng-wd.keytab HTTP/admng-wd.contoso.com
  kdestroy
  ```

  `Preauthentication failed` albo brak `keytab entry valid` znaczy, że kvno nie pasuje do AD – nowe hasło jednorazowe
  i `init` (3.7).
- **Odtworzenie na nowej maszynie** (inny klucz hosta lub TPM): poświadczenia nie da się odszyfrować i systemd nie
  uruchomi usługi (`systemctl status admng-watchdog` pokazuje `status=243/CREDENTIALS`).
  `sudo admng-watchdog reset-secrets-key` odkłada stare klucze, tworzy nowe poświadczenie i uruchamia usługę; historia
  zostaje, a hasło SMTP i adres Teams wpisujesz ponownie w AD Managerze. Eksportu klucza nie ma – celowo. (Kopia całego
  dysku w wariancie „klucz hosta” zawiera klucz – odtworzona na nowej VM działa jak ta sama maszyna.)

Dziennik systemowy – ogranicz jego rozmiar (usługa nie pisze własnych plików dziennika):

```bash
install -d /etc/systemd/journald.conf.d
printf '[Journal]\nSystemMaxUse=1G\n' > /etc/systemd/journald.conf.d/size.conf
systemctl restart systemd-journald
journalctl --disk-usage
```

---

## 4. OpenWEC – odbiór zdarzeń z kontrolerów [od 0.3.0]

Cały rozdział opisuje pakiet `admng-openwec` 0.3.0. Już teraz możesz wykonać tylko sprawdzenie zapytania na
kontrolerach z 4.4.

### 4.1 Co to jest i dlaczego osobna usługa

OpenWEC (projekt open source `cea-sec/openwec`, licencja GPL-3.0) to odbiornik Windows Event Forwarding dla Linuksa.
Przyjmuje od kontrolerów zdarzenia wysyłane według subskrypcji (rozdz. 2.12), sprawdza ich tożsamość Kerberosem i
zapisuje każde zdarzenie jako jeden wiersz JSON z oryginalnym XML zdarzenia – w osobnym pliku dla każdego DC. Watchdog
tylko **czyta** te pliki; niczego w nich nie zmienia.

- Działa jako osobny proces i osobne konto systemowe (`openwec`), w osobnym pakiecie `admng-openwec` budowanym przez nas
  z przypiętej wersji źródeł (kod źródłowy dołączany do wydania). Licencja GPL nie obejmuje przez to kodu AdMng.
- Pliki wyjściowe to jedyne miejsce, w którym surowe zdarzenia z DC leżą na dysku – tylko do czasu ich przetworzenia
  (szczegóły w 4.5).
- Jeśli Watchdog na chwilę stanie, zdarzenia czekają w plikach; jeśli stanie OpenWEC, kontrolery przechowują zdarzenia
  u siebie i wyślą je później (dlatego rozmiar dziennika Security z 2.10).

### 4.2 Instalacja

**Gdzie:** Debian. Pakiet `admng-openwec` (osobne kompilacje dla Debiana 12 i 13)
instaluje się razem z pakietem Watchdoga (rozdz. 5), który od niego zależy:

```bash
apt install ./admng-openwec_<wersja>~deb13_amd64.deb ./admng-watchdog_<wersja>_amd64.deb
```

Pakiet `admng-openwec` tworzy konto systemowe `openwec` (bez powłoki), dodaje je do grupy `admng-keytab`, zakłada bazę
stanu OpenWEC (`/var/lib/openwec/db/` – wyłącznie subskrypcje i stan klientów, bez zdarzeń) i jednostkę
`openwecd.service` z ograniczeniami systemd. Katalog wyjściowy `/var/lib/openwec/out/` (właściciel `openwec`, grupa
`admng-watchdog`, tryb 2750) tworzy pakiet `admng-watchdog` (rozdz. 5). Usługa nie wystartuje, dopóki nie ma keytabu
(3.6). Katalog subskrypcji `/etc/openwec/subscriptions/` pakiet zostawia pusty – plik subskrypcji tworzysz sam (4.4);
przykład leży w `/usr/share/admng-openwec/admng-dc-security.toml.example`.

### 4.3 Konfiguracja Kerberos z keytabem

OpenWEC używa **tego samego** konta i keytabu co usługa Watchdog: kontrolery łączą się z nazwą
`admng-wd.contoso.com`, a bilety do niej wystawia KDC dla konta `ADMNG-WD$`. Plik `/etc/openwec.conf.toml` (dostarcza
go pakiet; właściciel root, grupa `openwec`, tryb 0640) – zmieniasz tylko nazwy:

```toml
[server]
keytab = "/etc/admng-watchdog/admng-wd.keytab"
db_sync_interval = 5
flush_heartbeats_interval = 5

[logging]
verbosity = "warn"
server_logs = "stderr"          # do journald
# access_logs – nie ustawiaj: brak klucza = brak dziennika dostępu (adresy i konta przy każdej paczce);
# wartość "None" byłaby nazwą pliku, a nie wyłączeniem

[database]
type = "SQLite"
path = "/var/lib/openwec/db/openwec.sqlite"

[cli]
read_only_subscriptions = true  # subskrypcje tylko z plików

[[collectors]]
hostname = "admng-wd.contoso.com"
listen_address = "0.0.0.0"
listen_port = 5985
max_content_length = 512000

[collectors.authentication]
type = "Kerberos"
service_principal_name = "HTTP/admng-wd.contoso.com@CONTOSO.COM"

[outputs]
garbage_collect_interval = 600

[outputs.files]
files_descriptor_close_timeout = 600
```

**Wspólny keytab to przyjęte rozwiązanie (ADR-0009), ze świadomie zaakceptowanym ryzykiem.** Kto przejmie konto
systemowe `openwec` (np. przez błąd w OpenWEC, który przyjmuje dane z sieci), ma klucz `HTTP/admng-wd.contoso.com` –
ten sam, którym API Watchdoga sprawdza bilety. Może nim podrobić bilet do API jako dowolny użytkownik domeny, także
administrator Watchdoga, i zmienić reguły albo wyciszyć alarmy (każda zmiana konfiguracji trafia jednak do
dotychczasowych odbiorców); ma też tożsamość `ADMNG-WD$` (odczyt katalogu i SYSVOL). Nie ma natomiast dostępu do bazy
Watchdoga (katalog 0700 innego konta), sekretów kanałów ani praw zapisu w AD. Ryzyko ograniczają:

- **API tylko z podsieci administratorów** – zapora (3.3) przepuszcza port 8443 wyłącznie z 10.0.20.0/24, a usługa
  sprawdza to samo ustawieniem `AllowedRemoteSubnets` (5.2). Zapytania do API z samego serwera – także przez adres
  pętli zwrotnej – są odrzucane (poza `/healthz`), więc podrobiony bilet zadziała tylko z podsieci administratorów;
- **izolacja konta `openwec`** – osobne konto systemowe bez powłoki, usługa w piaskownicy systemd (bez uprawnień,
  zapis tylko w `/var/lib/openwec`), port 5985 przyjmuje połączenia tylko od kontrolerów domeny (zapora, 3.3); konto
  nie czyta bazy, biletów ani sekretów usługi Watchdog.

Dlatego konto `openwec` traktuj jak keytab – jako zasób Tier 0. Opcjonalnym utwardzeniem na przyszłość byłoby osobne
konto komputera z własną nazwą DNS i keytabem dla kolektora; wersja 0.3.0 go nie przewiduje i nie musisz niczego
w tym kierunku przygotowywać.

### 4.4 Subskrypcja dla kontrolerów

Subskrypcja mówi kontrolerom, **które** zdarzenia wysyłać. Obejmuje dotychczasowy zestaw zdarzeń zmian potrzebnych do
ustalania wykonawców (zmiany kont, grup, komputerów, obiektów katalogu, czyszczenie dziennika, zmiana zasad inspekcji)
oraz źródła zmian: 4624 (logowania sieciowe – typ 3 – i RDP – typ 10), 4625, 4771 i nieudane 4776. Zmiany
`dSCorePropagationData` (szum SDProp) są pomijane już na kontrolerze. Obejmuje tylko konta `DC01$` i `DC02$` – inne
komputery nie dostaną tej subskrypcji.

Plik `/etc/openwec/subscriptions/admng-dc-security.toml` wygeneruje polecenie (lista zdarzeń pochodzi z tego samego
katalogu zdarzeń co parser usługi, więc nie mogą się rozjechać). `install -m 0644` nadaje plikowi tryb czytelny dla
konta `openwec` także wtedy, gdy w powłoce działa `umask 077` z 3.6:

```bash
admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' --dc 'DC02$@CONTOSO.COM' |
    install -m 0644 /dev/stdin /etc/openwec/subscriptions/admng-dc-security.toml
```

W katalogu `/etc/openwec/subscriptions/` trzymaj **tylko** ten plik – bez kopii i plików zapasowych edytora. OpenWEC
wczytuje wskazany plik (4.6); kopia z tym samym `uuid` w tym katalogu łatwo pomyliłaby się z właściwym plikiem.

Wynik wygląda tak (zapytanie XPath – każda sekcja `Select` ma najwyżej 20 identyfikatorów zdarzeń):

```toml
uuid = "4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"
name = "admng-dc-security"
query = """
<QueryList>
  <Query Id="0" Path="Security">
    <Select Path="Security">*[System[(EventID=4720 or EventID=4722 or EventID=4723 or EventID=4724 or EventID=4725 or EventID=4726 or EventID=4738 or EventID=4740 or EventID=4767 or EventID=4781 or EventID=4765 or EventID=4766 or EventID=4794 or EventID=4741 or EventID=4742 or EventID=4743 or EventID=4719)]]</Select>
    <Select Path="Security">*[System[(EventID=4727 or EventID=4728 or EventID=4729 or EventID=4730 or EventID=4731 or EventID=4732 or EventID=4733 or EventID=4734 or EventID=4735 or EventID=4737 or EventID=4754 or EventID=4755 or EventID=4756 or EventID=4757 or EventID=4758 or EventID=4764)]]</Select>
    <Select Path="Security">*[System[(EventID=4744 or EventID=4745 or EventID=4746 or EventID=4747 or EventID=4748 or EventID=4749 or EventID=4750 or EventID=4751 or EventID=4752 or EventID=4753 or EventID=4759 or EventID=4760 or EventID=4761 or EventID=4762 or EventID=4763)]]</Select>
    <Select Path="Security">*[System[(EventID=5136 or EventID=5137 or EventID=5138 or EventID=5139 or EventID=5141 or EventID=1102 or EventID=1104 or EventID=1100)]]</Select>
    <Select Path="Security">*[System[(EventID=4624)]] and *[EventData[Data[@Name='LogonType']='3' or Data[@Name='LogonType']='10']]</Select>
    <Select Path="Security">*[System[(EventID=4625 or EventID=4771)]]</Select>
    <Select Path="Security">*[System[(EventID=4776)]] and *[EventData[Data[@Name='Status']!='0x0']]</Select>
    <Suppress Path="Security">*[System[(EventID=5136)]] and *[EventData[Data[@Name='AttributeLDAPDisplayName']='dSCorePropagationData']]</Suppress>
  </Query>
</QueryList>
"""

[options]
enabled = true
heartbeat_interval = 900
connection_retry_count = 5
connection_retry_interval = 60
max_time = 30
max_envelope_size = 512000
read_existing_events = false
content_format = "Raw"
ignore_channel_error = true

[filter]
operation = "Only"
type = "Client"
flags = "CaseInsensitive"
targets = ["DC01$@CONTOSO.COM", "DC02$@CONTOSO.COM"]

[[outputs]]
driver = "Files"
format = "RawJson"
config = { path = "/var/lib/openwec/out/{client}/security.jsonl" }
```

`max_time = 30` – kontroler wysyła paczkę najpóźniej po 30 sekundach (krótkie opóźnienie alarmów);
`heartbeat_interval = 900` – sygnał życia co 15 minut, gdy nie ma zdarzeń.

**Sprawdź zapytanie na kontrolerach, zanim je wczytasz – możesz to zrobić już teraz.** **Gdzie:** DC01 i DC02. Zapisz
część od `<QueryList>` do `</QueryList>` jako `admng-query.xml` i uruchom:

```powershell
Get-WinEvent -FilterXml ([xml](Get-Content .\admng-query.xml -Raw)) -MaxEvents 5 | Format-Table TimeCreated, Id -AutoSize
```

Oczekiwane: lista zdarzeń (identyfikatory z zapytania) albo `No events were found that match the specified selection
criteria.` – oba wyniki znaczą, że zapytanie jest poprawne. Błąd `The specified query is invalid` oznacza literówkę.
Osobno sprawdź filtr nieudanych 4776 (pytanie otwarte projektu): z ADM-WS01 wykonaj nieudane logowanie NTLM
(`net use \\10.0.1.10\IPC$ /user:CONTOSO\jan.kowalski ZleHaslo1` – adres IP zamiast nazwy wymusza NTLM), a na DC01:

```powershell
$q = '<QueryList><Query Id="0" Path="Security"><Select Path="Security">*[System[(EventID=4776)]] and *[EventData[Data[@Name=''Status'']!=''0x0'']]</Select></Query></QueryList>'
Get-WinEvent -FilterXml ([xml]$q) -MaxEvents 3 | Format-List TimeCreated, Message
```

Oczekiwane: zdarzenie 4776 z kodem błędu `0xC000006A` (złe hasło); udane 4776 nie mogą się pojawić.

Zasada na później: do wczytanej subskrypcji **nie dopisuje się** kanałów ani zdarzeń – tworzy się nową subskrypcję
(nowy `uuid`, plik w miejsce starego), inaczej kontrolery mogą zgubić zakładki WEF (bookmarks – zapamiętane miejsce
w dzienniku, do którego wysłały już zdarzenia).

### 4.5 Wyjście do Watchdoga

- Każdy DC ma własny katalog: `/var/lib/openwec/out/DC01@CONTOSO.COM/security.jsonl` (nazwa konta bez `$`), z prawem
  odczytu dla grupy `admng-watchdog`. Watchdog czyta pliki i zapamiętuje, dokąd doczytał, w tej samej transakcji co
  zapis zdarzeń – po awarii niczego nie gubi; ewentualne duplikaty odrzuca.
- Watchdog przyjmuje wiersz tylko wtedy, gdy zgadzają się: identyfikator subskrypcji, konto nadawcy (zapisywalny DC
  tej domeny), nazwa komputera w zdarzeniu i adres IP nadawcy. Rekord niezgodny jest odrzucany, a zakładka Stan pokazuje
  błąd (możliwy fałszywy nadawca).
- Logowania kont komputerów (`…$`) i anonimowe są odrzucane przy odczycie – filtr XPath Windows nie umie ich wykluczyć
  na kontrolerze.
- Timer `admng-wec-rotate.timer` (co godzinę, jako root) zmienia nazwę aktywnych
  plików na `security.jsonl.<znacznik czasu>`, każe OpenWEC otworzyć nowe pliki (sygnał przeładowania) i usuwa plik
  dopiero wtedy, gdy Watchdog potwierdzi, że przeczytał go do końca. Plik nieprzetworzony jest usuwany po **7 dniach**
  (ustawienie `MAX_UNCONSUMED_DAYS` w `/etc/default/admng-wec-rotate`, najwyżej 30), a Watchdog zgłasza lukę.

### 4.6 Uruchomienie i sprawdzenie, że zdarzenia z DC01 i DC02 docierają

**Gdzie:** Debian, potem DC01 i DC02.

```bash
sudo -u openwec openwec subscriptions load -y /etc/openwec/subscriptions/admng-dc-security.toml
systemctl start openwecd
systemctl status openwecd --no-pager
ss -tlnp 'sport = :5985'
journalctl -u openwecd -n 30 --no-pager
```

Oczekiwane: `+ Load subscription admng-dc-security`, `active (running)`, gniazdo `LISTEN … 0.0.0.0:5985` procesu
`openwecd`, brak błędów w dzienniku. Polecenie `load` z jednym plikiem usuwa z bazy OpenWEC inne subskrypcje (`-y`
potwierdza to bez pytania) – tak ma być, kontrolery mają dostać tylko tę. Subskrypcji nie trzeba wczytywać ponownie
po restarcie `openwecd` – są w bazie OpenWEC.

Na kontrolerach (najpóźniej po czasie `Refresh` z GPO, tu 60 s):

```powershell
Get-WinEvent -LogName 'Microsoft-Windows-Forwarding/Operational' -MaxEvents 5 | Format-Table TimeCreated, Id, Message -Wrap
Test-NetConnection admng-wd.contoso.com -Port 5985
klist -li 0x3e4
```

Oczekiwane: zdarzenie **104** (połączono z menedżerem subskrypcji) i **100** („The subscription admng-dc-security is
created successfully”); `TcpTestSucceeded : True`. `klist -li 0x3e4` pokazuje bilety konta NETWORK SERVICE (pod nim
działa przekazywanie) – zanotuj, czy jest tam bilet do `HTTP/admng-wd.contoso.com`, czy do `HOST/admng-wd.contoso.com`.
Według dokumentacji OpenWEC klient WinRM na Windows Server 2025 domyślnie prosi o `HOST/`, a starsze o `HTTP/`. OpenWEC
(MIT Kerberos) przyjmuje bilet do każdego SPN konta `ADMNG-WD$` – `HTTP/` i `HOST/` są szyfrowane tym samym kluczem
konta (sprawdzone w laboratorium), dlatego wynik `klist` zapisz tylko informacyjnie. Błąd Kerberos w zdarzeniu 105 –
9.2.

Na Debianie (po kilku minutach):

```bash
sudo -u openwec openwec subscriptions machines admng-dc-security
sudo -u openwec openwec stats --subscription admng-dc-security
ls -l /var/lib/openwec/out/
wc -l /var/lib/openwec/out/*/security.jsonl
```

Oczekiwane: na liście maszyn adres i nazwa każdego kontrolera (np. `10.0.1.10:DC01$@CONTOSO.COM`); w statystykach
liczba aktywnych maszyn = liczba kontrolerów; katalogi `DC01@CONTOSO.COM` i `DC02@CONTOSO.COM`; liczba wierszy rośnie
(kontrolery cały czas obsługują logowania sieciowe). Zawartości plików nie kopiuj poza serwer – to dane z katalogu.

---

## 5. Instalacja Watchdoga [od 0.3.0]

Cały rozdział opisuje pakiet, który powstaje w etapie B. Szczegóły ustala specyfikacja; tu jest to, co musisz o nim
wiedzieć przed wdrożeniem.

### 5.1 Co zrobi pakiet

Instalacja: `apt install ./admng-openwec_<wersja>~deb13_amd64.deb ./admng-watchdog_<wersja>_amd64.deb` (jeden pakiet
`amd64` dla Debiana 12 i 13; zależności: ASP.NET Core Runtime 10.0 ≥ 10.0.13, `admng-openwec`, biblioteki MIT Kerberos
i SASL GSSAPI, `smbclient`, `openssl`, systemd ≥ 252; konflikt z `gss-ntlmssp`). Pakiet:

- tworzy konto systemowe `admng-watchdog` (bez powłoki) i dodaje je do grupy `admng-keytab`;
- tworzy katalogi z właścicielami i trybami: dane `/var/lib/admng-watchdog` (0700, tylko konto usługi:
  `watchdog.db`, `backup/`, `keys/`), konfiguracja `/etc/admng-watchdog` (właściciel root; `appsettings.json`
  root:admng-watchdog 0640 – usługa może go czytać, ale nie zmienić);
- instaluje pliki: usługę w `/usr/lib/admng-watchdog/`, polecenia `admng-watchdog` i `admng-watchdog-keytab`,
  jednostki `admng-watchdog.service`, `admng-watchdog-keytab.service/.timer`, `admng-wec-rotate.service/.timer`,
  przykład zapory;
- tworzy klucz sekretów kanałów jako zaszyfrowane poświadczenie systemd
  (`/etc/admng-watchdog/credentials/dp-key.cred`) – gdy maszyna ma TPM, w wariancie „TPM2 + klucz hosta”, a gdy go nie
  ma albo ten wariant się nie uda – „klucz hosta”; wypisuje, którego wariantu użył i dlaczego (3.1);
- gdy nie ma certyfikatu w `/etc/admng-watchdog/tls/` – generuje własny i wypisuje odcisk (3.8, wariant B);
- po aktualizacji środowiska .NET (ASP.NET Core Runtime) restartuje usługę – stara wersja znika z dysku (wyzwalacz dpkg,
  3.1);
- **przy pierwszej instalacji niczego nie uruchamia** (usługa czeka na keytab i ustawienia); przy aktualizacji
  restartuje działającą usługę (migracje bazy wykonują się przy starcie, z kopią sprzed migracji);
- przy `apt purge` usuwa `/etc/admng-watchdog` (także keytab) i `/var/lib/openwec`, ale **zostawia**
  `/var/lib/admng-watchdog` – 3-letnią historię usuwasz tylko świadomie (8.1).

Usługa `admng-watchdog.service` działa jako `admng-watchdog` w piaskownicy systemd (bez uprawnień, tylko odczyt systemu
plików poza swoim katalogiem, tylko port 8443) i zgłasza systemd, że żyje, tylko wtedy, gdy jej zadania faktycznie
posuwają się naprzód – zawieszona usługa zostanie zrestartowana.

### 5.2 Ustawienia startowe (`/etc/admng-watchdog/appsettings.json`)

Plik edytuje tylko root serwera. Pakiet dostarcza go z przykładowymi nazwami; zmień:

| Ustawienie | Wartość w przykładzie | Skąd |
|---|---|---|
| `Watchdog.Hosting.Urls` | `https://admng-wd.contoso.com:8443` | nazwa z rekordu A (2.4) |
| `Watchdog.Hosting.AllowedRemoteSubnets` | `[ "10.0.20.0/24" ]` | podsieć stacji (druga linia obrony po zaporze) |
| `Watchdog.Hosting.Certificate.Path` / `KeyPath` | `/etc/admng-watchdog/tls/admng-wd.crt` / `.key` | 3.8 |
| `Watchdog.Authorization.AdminSids` | SID `AdMng-Watchdog-Admins` | 2.3 |
| `Watchdog.Authorization.ViewerSids` | SID `AdMng-Watchdog-Viewers` | 2.3 |
| `Watchdog.Authorization.DeniedSids` | `[]` | natychmiastowe odebranie dostępu (SID osoby lub grupy) |
| `Watchdog.Directory.Domain` | `contoso.com` | |
| `Watchdog.Directory.Servers` | `[ "DC01.contoso.com", "DC02.contoso.com" ]` | tylko pełne nazwy, nie adresy IP; usługa wybiera emulator PDC |
| `Watchdog.Kerberos.Realm` / `ServicePrincipal` | `CONTOSO.COM` / `ADMNG-WD$@CONTOSO.COM` | 2.5 |
| `Watchdog.Collector.SubscriptionUuid` | `4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10` | musi być równy `uuid` subskrypcji (4.4) |

Pakiet wpisuje w miejsce SID-ów grup wartość zastępczą `S-1-5-21-0-0-0-0` – dopóki jej nie zmienisz,
`--check-config` i start usługi kończą się komunikatem, że trzeba podać SID-y grup. Pozostałe ustawienia
(`AllowTier0Suppression`, `AllowPrivilegedServiceAccount`, `AllowInsecureDataDirectoryAcl`) zostaw na `false`. Zmiany `Authorization` i podsieci
działają w ciągu kilku sekund, pozostałe po restarcie usługi.

Po każdej edycji:

```bash
admng-watchdog --check-config
```

Retencję, SMTP, Teams i reguły ustawiasz w AD Managerze (rozdz. 6) – zapisują się w bazie usługi. Domyślna retencja
w 0.3.0: zdarzenia **1095 dni**, rekordy dzienników DC związane ze zmianami 30 dni, rekordy logowań 48 godzin, 7 kopii
bazy.

### 5.3 Pierwsze uruchomienie

```bash
systemctl start openwecd admng-watchdog
systemctl status openwecd admng-watchdog --no-pager
journalctl -u admng-watchdog -b --no-pager | tail -50
admng-watchdog --health; echo "kod: $?"
systemctl list-timers 'admng-*'
```

Oczekiwane: obie usługi `active (running)`; w dzienniku start bez błędów i wpis 1008 z odciskiem certyfikatu;
`--health` – kod 0; dwa timery (`admng-watchdog-keytab`, `admng-wec-rotate`) z datą następnego uruchomienia.

Po starcie usługa tworzy bazę i robi **zrzut bazowy** domeny (bez powiadomień o zmianach; dla ok. 50 000 obiektów kilka
minut) oraz ciche odczytanie wszystkich GPO. Na koniec pojawia się zdarzenie „Stan bazowy gotowy” i zaczyna się
**7-dniowy tryb wdrożenia** (natychmiast tylko zdarzenia krytyczne, reszta w raporcie dziennym).

Jeśli usługa nie startuje, kod wyjścia wskazuje przyczynę (`systemctl status admng-watchdog` → `status=…`):

| Kod | Przyczyna | Co zrobić |
|---|---|---|
| 2 | błąd w `appsettings.json` albo niespójny `ldap.conf` (np. brak `minssf`) | `admng-watchdog --check-config`; 3.5 |
| 3 | baza z nowszej wersji niż program | nie cofaj wersji pakietu |
| 4 | klucz certyfikatu dostępny dla wszystkich albo nieczytelny | 3.8 – tryby plików |
| 5 | folder danych z za szerokimi uprawnieniami, z wpisami ACL, na udziale sieciowym albo dowiązanie | `stat /var/lib/admng-watchdog` – właściciel `admng-watchdog`, tryb 700 |
| 6 | konto usługi może zapisywać plik ustawień lub jego katalog | właściciel root, tryby jak w 5.1 |
| 8 | konto `ADMNG-WD$` należy do grupy uprzywilejowanej | usuń je z grupy (2.5) |
| 9 | usługa uruchomiona bez poświadczenia z kluczem sekretów (np. poza jednostką systemd albo po zmianie jednostki) | uruchamiaj tylko przez `systemctl`; 3.9 |
| 10 | keytab: zły właściciel, zapisywalny dla grupy, nieczytelny dla usługi | 3.6 – `root:admng-keytab 640` |
| `243/CREDENTIALS` (komunikat systemd – program nawet nie wystartował) | nie da się odszyfrować `dp-key.cred`: inna maszyna, inny TPM, brak pliku | 3.9 – `sudo admng-watchdog reset-secrets-key` |

### 5.4 Kontrole stanu

- **Zakładka Stan w AD Managerze** (nowe sekcje): „Konto usługi” (DN, kvno, ostatnia i następna zmiana hasła,
  typy szyfrowania, członkostwo w grupach uprzywilejowanych, zgodność keytabu z AD), „Kolektor WEF” (dla każdego DC:
  ostatni rekord, opóźnienie, zaległości, rekordy odrzucone), „Zawartość GPO” (postęp odczytu, błędy, opóźnienie
  SYSVOL), „Host” (system, środowisko .NET, wariant klucza sekretów, wolne miejsce, różnica zegara, poziom szyfrowania
  LDAP). Każdy problem ma opis i zalecenie.
- **Usługa sama sprawdza konto `ADMNG-WD$`** przy starcie i co godzinę: brak członkostwa w grupach uprzywilejowanych,
  flaga „nie może być delegowane”, tylko AES, SPN-y, wpis odmowy dla SELF na własnym obiekcie (2.5), wiek hasła
  (ostrzeżenie > 32 dni, błąd > 35 dni), kvno keytabu równe `msDS-KeyVersionNumber`, pierwszy wpis keytabu równy
  nazwie konta (3.6), różnica zegara względem DC.
- **Dziennik systemowy:** `journalctl -u admng-watchdog -p warning --since today` – ostrzeżenia i błędy z dziś (bez
  wartości z katalogu). Numery zdarzeń są takie jak w wersji 0.2.0 ([`WATCHDOG.md`](WATCHDOG.md), rozdz. 10), nowe:
  1008 odcisk certyfikatu, 1011 problem z kontem usługi, 1012 problem z kolektorem WEF, 1013 problem z treścią GPO.
- **Raport dzienny** (7:30, także w weekendy) – jego brak oznacza problem z usługą albo z wysyłką.

Zewnętrznego monitoringu nie potrzeba: zakładka Stan, raport dzienny i automatyczny restart przez systemd wystarczą;
jeśli macie Zabbix lub SIEM, podepnijcie pod nie `journalctl` (priorytet warning i wyżej).

---

## 6. AD Manager [od 0.3.0]

### 6.1 Połączenie z usługą

**Gdzie:** ADM-WS01, zalogowany `CONTOSO\adm-jan` (członek `AdMng-Watchdog-Admins`).

1. Sprawdź, że stacja dostaje bilet Kerberos do usługi i dociera do portu:

   ```powershell
   klist get HTTP/admng-wd.contoso.com
   Test-NetConnection admng-wd.contoso.com -Port 8443
   ```

   Oczekiwane: bilet `Server: HTTP/admng-wd.contoso.com @ CONTOSO.COM` z `KerbTicket Encryption Type:
   AES-256-CTS-HMAC-SHA1-96`; `TcpTestSucceeded : True`.
2. W AD Manager: **Narzędzia → Watchdog** (Ctrl+Shift+A) → adres `https://admng-wd.contoso.com:8443` → **Połącz**.
   Zawsze pełna nazwa z rekordu A – nie adres IP, nie alias (wtedy Windows próbuje NTLM, który usługa odrzuca).
3. **Wariant A** (certyfikat z firmowego CA) – połączenie od razu.
   **Wariant B** (certyfikat własny serwera) – AD Manager pokaże okno z odciskiem
   SHA-256 (w grupach po 4 znaki), nazwą i datami ważności. Na serwerze uruchom `sudo admng-watchdog cert-fingerprint`,
   porównaj **cały** odcisk i dopiero wtedy kliknij „Ufam temu certyfikatowi”. Odcisk zostaje zapamiętany w
   ustawieniach AD Manager (Ustawienia → Watchdog, z przyciskiem „Usuń przypięcie”); potwierdzenie robisz na każdej
   stacji raz. Zmieniony certyfikat AD Manager pokaże jako zmianę (stary i nowy odcisk) – nie akceptuj go bez
   sprawdzenia na serwerze.
4. Role: członkowie `AdMng-Watchdog-Admins` widzą wszystko i mogą zmieniać konfigurację; `AdMng-Watchdog-Viewers` – tylko
   odczyt i potwierdzanie. Zmiana członkostwa działa w ciągu 5 minut, bez wylogowania.

### 6.2 Pierwsze kroki w zakładce Watchdog

Opis wszystkich opcji: [`WATCHDOG.md`](WATCHDOG.md), rozdz. 5 i 6. Minimum na start:

1. **Stan** – połączenie z DC (emulator PDC), kontrola uprawnień (bez braków), atrybucja (pokrycie rośnie po pierwszych
   zmianach), nowe sekcje z 5.4 bez błędów.
2. **Kanały i SMTP → SMTP**: serwer `smtp.contoso.com`, port `587`, **STARTTLS wymagane**, uwierzytelnianie zgodnie z
   ustawieniami przekaźnika (bez logowania – gdy przekaźnik przyjmuje pocztę z adresu 10.0.1.25; albo użytkownik i
   hasło). Hasło jest zapisywane zaszyfrowane, tylko do zapisu (nigdy nie wraca do AD Managera) i powiązane z tym
   serwerem – po zmianie serwera, portu lub użytkownika trzeba je wpisać ponownie. Nadawca np. `AD Watchdog
   <watchdog@contoso.com>`. **Wyślij wiadomość testową** – okno pokazuje każdy etap (połączenie, TLS, logowanie,
   odbiorcy).

   W laboratorium (Mailpit z 1.1) – bez logowania, a ponieważ certyfikat Mailpit jest samopodpisany, w weryfikacji
   certyfikatu serwera SMTP wybierz przypięty odcisk i wklej odcisk SHA-256 odczytany na Debianie (64 znaki
   szesnastkowe bez dwukropków – w takiej postaci przyjmuje go Watchdog):

   ```bash
   openssl s_client -starttls smtp -connect smtp.contoso.com:587 </dev/null 2>/dev/null |
       openssl x509 -noout -fingerprint -sha256 | cut -d= -f2 | tr -d :
   ```
3. **Grupy odbiorców**: „Administratorzy IT” (`it-admins`) i „Helpdesk” (`helpdesk`) – adresy e-mail (najlepiej listy
   dystrybucyjne).
4. **Kanały i SMTP → Teams** (opcjonalnie – wymaga dzierżawy Microsoft 365): w kanale Teams → … → Przepływy pracy
   (Workflows) → szablon „Send webhook alerts to a channel” / „Post to a channel when a webhook request is received”
   → skopiuj adres. W Power Automate dodaj współwłaścicieli przepływu (przepływ jednej osoby przestaje działać po jej
   odejściu) i włącz „Secure inputs/outputs”. Wklej adres (to sekret – po zapisaniu widać tylko nazwę hosta) i wyślij kartę testową; sprawdź, że karta pojawiła się
   w kanale (odpowiedź 202 oznacza tylko przyjęcie). Do reguł krytycznych (`tier0-*`) dodaj kanał Teams, jeśli alarmy
   mają tam trafiać.
5. **Reguły**: przejrzyj reguły wbudowane, także nowe w 0.3.0: `tier0-badsuccessor` (dMSA), `tier0-machine-quota`,
   `watchdog-self` (konto usługi), `tier0-gpo` (treść GPO) – powiadamiają natychmiast i całą dobę, także w trybie
   wdrożenia; ich wyłączenie przez API wymaga lokalnej zgody na serwerze (`AllowTier0Suppression`) i samo jest
   zdarzeniem krytycznym. Przy każdej regule widać, ile wiadomości by wysłała (przycisk Podgląd).
6. **Retencja** (Konfiguracja): sprawdź, że zdarzenia mają 1095 dni. Trzy lata historii to dane osobowe pracowników –
   uzgodnij cel i okres z IOD i wpisz przetwarzanie do rejestru czynności.
7. **Tryb wdrożenia** (7 dni): w produkcji przez tydzień przeglądaj Reguły i dodawaj wyjątki z podpowiedzi (np. konto
   synchronizacji Entra Connect), potem „Zakończ tryb wdrożenia”. W domenie testowej zakończ go od razu po zrzucie
   bazowym – inaczej w pilotażu (rozdz. 7) zobaczysz tylko alarmy krytyczne.
8. **Raport dzienny**: odbiorcy, godzina (domyślnie 7:30), „Wyślij teraz” jako test.

---

## 7. Pilotaż – lista kontrolna [od 0.3.0]

### 7.1 Przygotowanie

- Wszystkie kroki 2.x, 3.x, 4, 5 i 6 zakończone sprawdzeniem; Stan bez błędów; tryb wdrożenia zakończony (domena
  testowa).
- Zmiany testowe wykonuj z **ADM-WS01** jako `CONTOSO\adm-jan`. AD Manager oraz konsole „Użytkownicy i komputery AD”
  i GPMC łączą się z kontrolerem bezpośrednio (LDAP) – wtedy Watchdog pokaże jako źródło `ADM-WS01` / 10.0.20.15 (nazwa
  z rekordu PTR, z dopiskiem „(DNS)”). Polecenia modułu ActiveDirectory w PowerShell i Centrum administracyjne AD
  działają przez usługę ADWS na kontrolerze: wykonawca (`adm-jan`) jest ustalany tak samo, ale jako źródło Watchdog może
  pokazać „lokalnie na DC” – test 18 w 7.2 pokazuje różnicę. Zmiana zrobiona w konsoli kontrolera ma jako źródło sam
  DC.
- Notuj przy każdym teście: czas wykonania, czas pojawienia się zdarzenia w AD Managerze, czas e-maila/karty Teams.
  Zdarzenie pojawia się zwykle w ciągu kilkunastu–kilkudziesięciu sekund; wykonawca może mieć przez chwilę status
  „ustalanie…”.

### 7.2 Testy zmian i oczekiwany wynik

Kolumna „Kto / skąd” to oczekiwana atrybucja zmiany wykonanej w AD Managerze albo w konsoli (ADUC, GPMC):
`CONTOSO\adm-jan`, stacja `ADM-WS01 (DNS)` (10.0.20.15), DC, na którym wykonano zmianę. Polecenie PowerShell w kolumnie
„Akcja” opisuje zmianę – możesz je wykonać zamiast AD Managera, ale wtedy zmiana przechodzi przez ADWS: wykonawca jest
ten sam, a źródłem może być „lokalnie na DC” (7.1). Tę różnicę celowo sprawdza test 18. Testy dMSA (11–12) mają
`-Server DC01.contoso.com`, bo DC02 (Windows Server 2022) nie obsługuje dMSA. Powiadomienia według reguł domyślnych po
zakończeniu trybu wdrożenia (godziny pracy: pon.–pt. 7:00–17:00).

| # | Akcja (ADM-WS01, AD Manager lub PowerShell) | Oczekiwane zdarzenie w Watchdogu | Kto / skąd (AD Manager, konsola) | Powiadomienie |
|---|---|---|---|---|
| 1 | `New-ADUser -Name 'Test Pilot1' -SamAccountName test.pilot1 -Path 'OU=Użytkownicy,OU=Firma,DC=contoso,DC=com' -AccountPassword (Read-Host -AsSecureString) -Enabled $true` | „Nowe konto” (jedno zdarzenie ze stanem końcowym, mimo kilku kroków tworzenia) | adm-jan / ADM-WS01 | e-mail do „Administratorzy IT” w godzinach pracy (zbiorczo co 5 min) + raport dzienny |
| 2 | `Disable-ADAccount test.pilot1` | „Wyłączono konto” | adm-jan / ADM-WS01 | raport dzienny |
| 3 | `Set-ADAccountPassword test.pilot1 -Reset -NewPassword (Read-Host -AsSecureString)` | „Zmieniono hasło” z oznaczeniem resetu przez administratora | adm-jan / ADM-WS01 | raport dzienny (dla konta uprzywilejowanego – e-mail) |
| 4 | `Set-ADUser jan.kowalski -Department 'Księgowość' -Title 'Specjalista'` | „Zmiana parametrów konta” – `department` i `title`: było → jest | adm-jan / ADM-WS01 | raport dzienny |
| 5 | `Add-ADGroupMember -Identity "$((Get-ADDomain).DomainSID)-512" -Members test.pilot1` | „Dodano do grupy uprzywilejowanej” (krytyczne) + „Dodano członka grupy” | adm-jan / ADM-WS01 | e-mail natychmiast, całą dobę (reguła `tier0-privilege`); Teams, jeśli kanał jest w regule |
| 6 | `Remove-ADGroupMember -Identity "$((Get-ADDomain).DomainSID)-512" -Members test.pilot1 -Confirm:$false` | „Usunięto z grupy uprzywilejowanej” | adm-jan / ADM-WS01 | e-mail |
| 7 | `Remove-ADUser test.pilot1 -Confirm:$false` | „Usunięto obiekt” | adm-jan / ADM-WS01 | e-mail |
| 8 | Blokada: na ADM-WS01 sześć razy `net use \\DC01.contoso.com\IPC$ /user:CONTOSO\anna.nowak ZleHaslo<n>` (próg z 1.5 = 5) | „Zablokowano konto” ze **źródłem błędnych haseł**: adres 10.0.20.15 / ADM-WS01 i liczba prób (z 4771/4776/4625) | źródło błędnych haseł: ADM-WS01 | e-mail do helpdesku w godzinach pracy (najwyżej jeden na konto na godzinę) |
| 9 | `Unlock-ADAccount anna.nowak` | „Odblokowano konto” | adm-jan / ADM-WS01 | raport dzienny |
| 10 | GPO: w GPMC utwórz GPO „Pilot - test” połączone z `OU=Pilot,…`, zmień w nim ustawienie (np. Ustawienia zabezpieczeń → Przypisywanie praw użytkownika → „Allow log on locally” – dodaj grupę `Księgowość`) | „Zmieniono obiekt zasad grupy” (wersja GPO) oraz zdarzenie zmiany ustawień GPO z wierszem „ustawienie: było → jest” (np. `Computer/Security/Privilege Rights/SeInteractiveLogonRight`) | adm-jan / ADM-WS01 (wykonawca z 5136 na obiekcie GPO) | prawa użytkownika to kategoria wrażliwa – priorytet wysoki, e-mail natychmiast (reguła `tier0-gpo`); dla GPO połączonego z domeną lub OU kontrolerów – krytyczny |
| 11 | dMSA: na DC01 (Windows Server 2025), jeśli nie ma klucza KDS (`Get-KdsRootKey`): `Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))` (tylko laboratorium); potem z ADM-WS01 (jeśli moduł RSAT na stacji nie zna parametru `-CreateDelegatedServiceAccount`, wykonaj na DC01 – źródłem będzie wtedy DC01): `New-ADServiceAccount -Name dmsa-pilot -DNSHostName dmsa-pilot.contoso.com -CreateDelegatedServiceAccount -KerberosEncryptionType AES256 -Path 'OU=Pilot,OU=Firma,DC=contoso,DC=com' -Server DC01.contoso.com`; potem to samo bez `-Path` dla `dmsa-pilot2` (`-DNSHostName dmsa-pilot2.contoso.com`) – trafi do domyślnego kontenera `CN=Managed Service Accounts` | dwa razy „Nowe konto usługi” – **krytyczne** (reguła `tier0-badsuccessor`); także drugie z wykonawcą (wpis tworzenia obiektów z 2.11 działa w kontenerach) | adm-jan / ADM-WS01 | e-mail natychmiast, całą dobę |
| 12 | `Start-ADServiceAccountMigration -Identity dmsa-pilot -SupersededAccount (Get-ADUser svc-pilot).DistinguishedName -Server DC01.contoso.com` | „Zmieniono powiązanie migracji dMSA” – **krytyczne** (atrybuty `msDS-ManagedAccountPrecededByLink` / `msDS-SupersededManagedAccountLink`) | adm-jan / ADM-WS01 | e-mail natychmiast |
| 13 | `Set-ADDomain -Identity contoso.com -Replace @{ 'ms-DS-MachineAccountQuota' = 10 }` (z 0) | „Zmieniono zasady domeny” – `ms-DS-MachineAccountQuota: 0 → 10`, **krytyczne** (reguła `tier0-machine-quota`); powrót do 0 – wysokie | adm-jan / ADM-WS01 | e-mail natychmiast |
| 14 | `Set-ADComputer ADMNG-WD -KerberosEncryptionType RC4, AES128, AES256`, potem z powrotem `-KerberosEncryptionType AES128, AES256` | zmiana konta usługi – **krytyczne** (reguła `watchdog-self`) – oba kroki; Stan → Konto usługi chwilowo pokazuje rozbieżność typów szyfrowania | adm-jan / ADM-WS01 | e-mail natychmiast |
| 15 | `Add-ADGroupMember AdMng-Watchdog-Viewers -Members hd-ewa` | „Dodano do grupy uprzywilejowanej” (grupy ról Watchdoga są traktowane jak uprzywilejowane); po ≤ 5 min `hd-ewa` może się połączyć jako Viewer | adm-jan / ADM-WS01 | e-mail natychmiast |
| 16 | W AD Manager zmień dowolną regułę i zapisz | „Zmieniono konfigurację Watchdog” (kto, co) | adm-jan | e-mail także do dotychczasowych odbiorców |
| 17 | (opcjonalnie) na DC02: `wevtutil cl Security` | „Wyczyszczono dziennik zabezpieczeń” (krytyczne) | konto, które wyczyściło dziennik | e-mail natychmiast |
| 18 | Ta sama zmiana dwiema drogami: w AD Managerze zmień opis konta `anna.nowak` na „test LDAP”, potem w PowerShell `Set-ADUser anna.nowak -Description 'test ADWS'` | dwa zdarzenia „Zmiana parametrów konta” – `description`: było → jest | pierwsze: adm-jan / ADM-WS01 (DNS); drugie: adm-jan / ADM-WS01 albo „lokalnie na DC” – zanotuj wynik (7.4) | raport dzienny |

Po testach 11–12 posprzątaj – najpierw wycofaj migrację (dMSA musi jeszcze istnieć), dopiero potem usuń dMSA:

```powershell
Undo-ADServiceAccountMigration -Identity dmsa-pilot -SupersededAccount (Get-ADUser svc-pilot).DistinguishedName -Server DC01.contoso.com
Get-ADUser svc-pilot -Server DC01.contoso.com -Properties msDS-SupersededManagedAccountLink, msDS-SupersededServiceAccountState |
    Format-List msDS-SupersededManagedAccountLink, msDS-SupersededServiceAccountState     # oba puste
Remove-ADServiceAccount dmsa-pilot -Confirm:$false -Server DC01.contoso.com
Remove-ADServiceAccount dmsa-pilot2 -Confirm:$false -Server DC01.contoso.com
```

Sprzątanie też daje zdarzenia (wycofanie powiązania – krytyczne, usunięcie obiektu) – to oczekiwane. Po teście 13
przywróć `0`.

### 7.3 Testy działania

| # | Test | Jak | Oczekiwany wynik |
|---|---|---|---|
| 1 | Restart odbiornika pod obciążeniem | w trakcie testów z 7.2: `systemctl restart openwecd` | Stan → Kolektor WEF bez luki; zdarzenia z czasu restartu docierają (kontrolery je ponawiają) |
| 2 | Przerwa w pracy usługi | `systemctl stop admng-watchdog`, zmiana testowa, po 10 min `systemctl start admng-watchdog` | zmiana zgłoszona po starcie, z wykonawcą (pliki OpenWEC czekały) |
| 3 | Awaria DC01 | wyłącz DC01 na 30 min | usługa przechodzi na DC02 (Stan, zdarzenie „Kontroler domeny niedostępny”), Kerberos działa przez DC02; po powrocie DC01 usługa zostaje na DC02, dopóki ten działa – na PDC wraca po restarcie usługi albo awarii DC02, bez pełnego ponownego skanowania |
| 4 | Zmiana hasła konta usługi | pierwsza automatyczna zmiana po 30 dniach – albo szybciej ścieżka z 3.7 (nowe hasło jednorazowe + `admng-watchdog-keytab init`) | AD Manager, LDAP i WEF działają bez restartu; Stan → Konto usługi pokazuje nowe kvno równe AD. Automatyczna zmiana nie wywołuje alarmu `watchdog-self`; reset hasła przez administratora w ścieżce z hasłem jednorazowym – wywołuje (alarm krytyczny, oczekiwany) |
| 5 | Certyfikat (wariant B) | połączenie z drugiej stacji; potem `cert-renew-self-signed` | okno odcisku przy pierwszym połączeniu; po odnowieniu – ostrzeżenie o zmianie z dwoma odciskami |
| 6 | Odtworzenie na innej maszynie | wariant TPM2: odtwórz dysk na nowej VM bez tego samego vTPM; wariant „klucz hosta”: nowa VM z Debianem z 3.1, skopiuj tylko `/etc/admng-watchdog` i `/var/lib/admng-watchdog` (bez `/var/lib/systemd/credential.secret`) | `systemctl status admng-watchdog` pokazuje `status=243/CREDENTIALS`; po `reset-secrets-key` usługa startuje, historia jest, SMTP i Teams trzeba wpisać ponownie. (Kopia całego dysku w wariancie „klucz hosta” zawiera klucz – usługa startuje normalnie, to oczekiwane.) |
| 7 | Role | `Add-ADGroupMember AdMng-Watchdog-Viewers -Members anna.nowak`, logowanie AD Managerem z jej kontem; potem jej SID w `DeniedSids` | po ≤ 5 min dostęp tylko do odczytu; po wpisie w `DeniedSids` – odmowa w ciągu sekund |
| 8 | Zapora | z sieci spoza 10.0.20.0/24: połączenie na 8443 i 22 | brak połączenia |

### 7.4 Co pilotaż ma rozstrzygnąć

Zapisz wyniki – część decyzji etapu B zależy od nich:

- o który bilet proszą kontrolery 2025 i 2022 – `HTTP/` czy `HOST/` (`klist -li 0x3e4`, 4.6; tylko informacyjnie –
  OpenWEC przyjmuje oba);
- czy klient WinRM na kontrolerach ma ustawione `TrustedHosts` (`winrm get winrm/config/client` na obu DC) – przy
  wartości `*` (np. z firmowego GPO) kontroler może wysyłać Negotiate zamiast Kerberos;
- czy `kpasswd` przyjmuje zmianę hasła konta komputera zaraz po ustawieniu hasła jednorazowego i przy wpisie odmowy dla
  SELF z 2.5 (3.6; potem także pierwsza automatyczna zmiana po 30 dniach);
- czy filtr nieudanych 4776 działa (4.4);
- liczbę zdarzeń 4624 (typ 3 i 10) i 4776 na godzinę na każdym DC i przyrost dziennika Security – na tej podstawie
  ustal docelowy rozmiar dziennika (2.10);
- czy podpisywanie LDAP wymuszone na Windows Server 2025 działa z szyfrowaniem GSSAPI (`SASL SSF: 256`, 3.6);
- czy `tokenGroups` zwykłego konta jest czytelny dla `ADMNG-WD$` po nadaniu praw z 2.6 (test z Debiana w 2.6);
- jakie źródło Watchdog pokazuje dla zmian przez ADWS (PowerShell, Centrum administracyjne AD) – test 18 w 7.2;
- kto jest zapisany jako wykonawca automatycznej zmiany hasła `ADMNG-WD$` (zdarzenia 4742/4723 na DC);
- pokrycie atrybucji (Stan → Atrybucja) po tygodniu i rzeczywistą treść GPO (zmiany w każdej kategorii z 7.2 #10);
- czy hiperwizor kopiuje i odtwarza stan vTPM (3.9).

---

## 8. Wycofanie

### 8.1 Serwer Debian

**Gdzie:** Debian, root.

```bash
systemctl disable --now admng-watchdog admng-watchdog-keytab.timer admng-wec-rotate.timer openwecd
apt purge admng-watchdog admng-openwec        # od 0.3.0: usuwa /etc/admng-watchdog z keytabem i /var/lib/openwec
```

- Historia (`/var/lib/admng-watchdog`) **zostaje** po `purge`. Usuń ją dopiero po decyzji, co dalej z 3-letnią historią
  (archiwum, przekazanie, RODO) – usunięcie jest nieodwracalne:
  `rm -rf --one-file-system /var/lib/admng-watchdog`.
- Przed pakietem (tylko ręczny keytab z 3.6): `rm /etc/admng-watchdog/admng-wd.keytab` i pozostałe pliki z
  `/etc/admng-watchdog`. Keytab przestaje mieć wartość, gdy konto w AD zostanie wyłączone albo usunięte (8.2) – zrób to
  zawsze.
- Zapora, chrony, DNS: przywróć kopie z 3.3 i 3.4 albo usuń całą maszynę:

  ```bash
  cp -a /etc/nftables.conf.orig /etc/nftables.conf && nft -f /etc/nftables.conf
  cp -a /etc/chrony/chrony.conf.orig /etc/chrony/chrony.conf
  rm -f /etc/chrony/sources.d/ad-dcs.sources /etc/chrony/conf.d/admng-ad.conf && systemctl restart chrony
  cp -a /etc/resolv.conf.orig /etc/resolv.conf
  ```
- Jeśli maszyna była przeznaczona tylko dla Watchdoga – usuń całą VM i jej kopie zapasowe (to dane Tier 0).

### 8.2 AD i kontrolery

**Gdzie:** PAW, Domain Admin. Kolejność ma znaczenie: najpierw prawa nadane kontu, potem samo konto.

1. Wyłącz konto (od tej chwili keytab nie działa, a konto nie zaloguje się też certyfikatem):
   `Disable-ADAccount 'ADMNG-WD$'`. Gdy jest urząd certyfikacji, sprawdź, czy wystawiono certyfikaty na to konto, i je
   odwołaj (2.5, „Gdy keytab mógł wyciec”).
2. Usuń prawa odczytu i wpisy odmowy na szablonach certyfikatów (przed usunięciem konta – potem zostałyby wpisy
   z „osieroconym” SID-em):

   ```powershell
   $d = 'DC=contoso,DC=com'; $a = 'CONTOSO\ADMNG-WD$'
   dsacls $d /R $a
   dsacls "CN=AdminSDHolder,CN=System,$d" /R $a
   dsacls "CN=Password Settings Container,CN=System,$d" /R $a
   dsacls "CN=Deleted Objects,$d" /R $a
   # tylko gdy wykonano wpisy odmowy na szablonach (2.5): każdy szablon z listy
   $cfg = (Get-ADRootDSE).configurationNamingContext
   Get-ADObject -SearchBase "CN=Certificate Templates,CN=Public Key Services,CN=Services,$cfg" -LDAPFilter '(objectClass=pKICertificateTemplate)' |
       Where-Object { (Get-Acl -Path "AD:\$($_.DistinguishedName)").Access | Where-Object IdentityReference -like '*\ADMNG-WD$' } |
       ForEach-Object { dsacls $_.DistinguishedName /R $a }
   ```

3. GPO: `Remove-GPO -Name 'AdMng - WEF'`; `Remove-GPO -Name 'AdMng - ograniczenia konta Watchdog'` i usuń konto
   z praw w istniejących GPO, do których je dopisałeś (2.5). Na kontrolerach `gpupdate /target:computer /force`;
   w Forwarding/Operational pojawi się informacja o zmianie zasad, przekazywanie ustaje, a dostęp NETWORK SERVICE do
   dziennika Security wraca do ustawień domyślnych. GPO inspekcji (`AdMng - inspekcja DC`) zalecamy zostawić. Samo jego
   usunięcie **nie** przywraca poprzednich zasad inspekcji – kontrolery zachowują ostatnie ustawienia w lokalnej
   konfiguracji. Jeśli trzeba je wycofać, postępuj jak w 2.13, wiersz 10: w tym GPO ustaw każdą podkategorię na wartość
   z kopii z 2.10 (nie na „No Auditing” – chyba że kopia tak mówi), na DC `gpupdate /target:computer /force`, porównaj
   `auditpol /get /category:*` z kopią i dopiero potem `Remove-GPO -Name 'AdMng - inspekcja DC'`.
4. SACL: blok „Wycofanie” z 2.11 (usuwa tylko wpisy dodane w 2.11 – także na `CN=Public Key Services`).
5. `ms-DS-MachineAccountQuota`: przywróć poprzednią wartość tylko wtedy, gdy jest taka potrzeba biznesowa (2.8).
6. Usuń konto i grupy: `Remove-ADComputer ADMNG-WD -Confirm:$false`; `Remove-ADGroup AdMng-Watchdog-Admins -Confirm:$false`;
   `Remove-ADGroup AdMng-Watchdog-Viewers -Confirm:$false`.
7. DNS: `Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName contoso.com -Name admng-wd -RRType A -Force`
   i `Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName 1.0.10.in-addr.arpa -Name 25 -RRType Ptr -Force`.
8. Poprawek delegacji w OU Tier 0 i praw tworzenia dMSA (2.2, 2.9) **nie** cofaj – to poprawa bezpieczeństwa niezależna
   od Watchdoga. Jeśli jednak trzeba, przywróć listy ACL z zapisanych plików SDDL (polecenie pod tabelą w 2.13).

**Sprawdzenie:** po kroku 2 (konto jeszcze istnieje) `dsacls 'DC=contoso,DC=com' | Select-String 'ADMNG-WD'` nic nie
zwraca; po kroku 6 `Get-ADComputer ADMNG-WD` kończy się błędem „Cannot find an object”; po kroku 7
`Resolve-DnsName admng-wd.contoso.com` – brak rekordu; na kontrolerach brak nowych zdarzeń w Forwarding/Operational.

---

## 9. Rozwiązywanie problemów

Szczegółowy przebieg Kerberosa na Debianie pokaże zmienna `KRB5_TRACE`, np.:

```bash
KRB5_TRACE=/dev/stderr KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=MEMORY:t \
    kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'
```

`KRB5_CONFIG` wskazuje konfigurację usługi (systemowa daje mylące błędy), a `KRB5CCNAME=MEMORY:` trzyma bilet tylko
w pamięci polecenia. Ślad nie zawiera kluczy ani haseł, ale zawiera nazwy kont i adresy – nie publikuj go poza
zespołem.

### 9.1 Kerberos

| Objaw | Przyczyna | Rozwiązanie |
|---|---|---|
| `Clock skew too great` | zegar Debiana lub DC różni się o więcej niż 5 min: chrony odrzuca kontrolery (brak `maxdistance` z 3.4, duży rozrzut czasu DC) albo emulator PDC nie ma źródła czasu | `chronyc tracking`, `chronyc -N sources`, `chronyc ntpdata DC01.contoso.com` (`Root dispersion`), plik `/etc/chrony/conf.d/admng-ad.conf` (3.4); na DC01 `w32tm /query /status` (1.4) |
| `Cannot contact any KDC for realm 'CONTOSO.COM'` | DNS, zapora (88/tcp), zła nazwa KDC w `krb5.conf` | `getent hosts DC01.contoso.com`, `nft list ruleset`, `KRB5_CONFIG` ustawione? |
| `Client not found in Kerberos database` | zła nazwa konta (brak `$`, literówka, małe litery obszaru) | `ADMNG-WD$@CONTOSO.COM` – w apostrofach (znak `$`) |
| `Client's credentials have been revoked` | konto wyłączone (np. hasło jednorazowe nie spełniło zasad i konto powstało wyłączone) | `Get-ADComputer ADMNG-WD -Properties Enabled` (2.5) |
| `Preauthentication failed` przy `kinit -k` | klucze w keytabie nie pasują do hasła w AD: zła sól, keytab sprzed zmiany hasła, zmiana hasła w AD poza Debianem | 3.6 (sól), porównaj kvno: `klist -k` vs `msDS-KeyVersionNumber`; naprawa – nowe hasło jednorazowe i 3.7 |
| `kvno -k` bez `keytab entry valid` / AD Manager „Usługa odrzuciła uwierzytelnienie Kerberos” (401) przy poprawnym bilecie | keytab nie zawiera wersji klucza, którą KDC szyfruje bilety (kvno), albo brak nazwy `HTTP/…` w keytabie | `klist -k -e` – trzy nazwy, aktualne kvno; dziennik usługi, wpis 2501 z podpowiedzią |
| `KDC has no support for encryption type` | konto nie ma kluczy AES (typy szyfrowania ustawione, ale hasło nie zmienione od tego czasu) albo klient wymusza AES, a konto ma tylko RC4 | `Get-ADComputer ADMNG-WD -Properties msDS-SupportedEncryptionTypes` = 24; zmień hasło (3.7) |
| `Server not found in Kerberos database` | SPN nie istnieje albo klient używa aliasu (CNAME), adresu IP lub nazwy z innej domeny | `setspn -Q HTTP/admng-wd.contoso.com`; łącz się nazwą z rekordu A (2.4) |
| Ten sam SPN na dwóch kontach (np. stary gMSA z 0.2.0) | KDC nie wie, kluczem którego konta szyfrować; uwierzytelnianie zawodzi | `setspn -X` (duplikaty), `setspn -Q HTTP/admng-wd.contoso.com`, `setspn -D …` na starym koncie |
| `kpasswd`: `Password change rejected` | zasady haseł domeny – minimalny wiek hasła liczy się od ustawienia hasła jednorazowego | 3.6 – keytab z hasła jednorazowego, następnego dnia zmiana z **tym samym** hasłem jednorazowym, bez nowego resetu na PAW |
| `kpasswd`: `Access denied` | zmiana hasła zablokowana – np. przez wpis odmowy dla SELF z 2.5 (pytanie pilotażu) | usuń wpis (blok z 2.5 z `RemoveAccessRule` w miejscu `AddAccessRule`), powtórz 3.6 i zgłoś wynik zespołowi |
| Usługa przestała się uwierzytelniać po odtworzeniu VM | keytab starszy niż hasło w AD (zmiana nastąpiła po wykonaniu kopii) | Stan → Konto usługi (`keytabStale`); nowe hasło jednorazowe + `init` (3.7) |

### 9.2 WEF (przekazywanie zdarzeń)

Na DC: `Get-WinEvent -LogName 'Microsoft-Windows-Forwarding/Operational' -MaxEvents 20`. Zdarzenie **104** – połączono
z menedżerem subskrypcji, **100** – subskrypcja utworzona (tak ma być), **105** – problem z połączeniem; jego treść
zawiera kod błędu.

| Objaw | Przyczyna | Rozwiązanie |
|---|---|---|
| Brak jakichkolwiek wpisów Forwarding/Operational | GPO nie dotarło do DC albo usługa WinRM nie działa | `gpresult /scope computer /r` (czy jest „AdMng - WEF”), rejestr z 2.12, `Get-Service WinRM` |
| 105 z kodem `2150858770` (0x80338012, „The client cannot connect to the destination…”) | DC nie dociera do 10.0.1.25:5985: DNS, zapora (Debian – tylko adresy DC; DC – ruch wychodzący), `openwecd` nie działa | `Test-NetConnection admng-wd.contoso.com -Port 5985`; `systemctl status openwecd`; `nft list ruleset` |
| 105 z kodem `2150859027` (0x80338113, „…HTTP URL was not available”) | pod adresem odpowiada coś, co nie jest odbiornikiem WS-Management, albo zły adres w GPO | sprawdź wartość `Server=…` w GPO (2.12) i `ss -tlnp 'sport = :5985'` na Debianie |
| 105 z błędem Kerberos (np. `0x80090322` – zła nazwa usługi) | SPN / keytab: brak SPN w AD, inne kvno w keytabie niż w AD | `setspn -L ADMNG-WD` (2.5), `kvno -k` na Debianie (3.6), `klist -li 0x3e4` na DC (4.6) |
| 105 z błędem uwierzytelnienia mimo poprawnego SPN i keytabu | GPO ustawia na DC `TrustedHosts=*` klienta WinRM – kontroler wysyła Negotiate zamiast Kerberos | `winrm get winrm/config/client` na DC; zanotuj wynik i GPO (7.4) |
| 104 jest, 100 brak | DC połączył się, ale subskrypcja go nie obejmuje | `[filter] targets` w subskrypcji (nazwy `DC01$@CONTOSO.COM`), `openwec subscriptions` – czy wczytana i włączona |
| 100 jest, ale brak zdarzeń z dziennika Security / błąd z kodem 5 (odmowa dostępu) | NETWORK SERVICE nie czyta dziennika Security | `wevtutil gl security` – `channelAccess` musi zawierać `(A;;0x1;;;NS)` (2.12) |
| Zdarzenia przychodzą, ale Stan pokazuje „rekordy odrzucone” przy DC | niezgodność tożsamości: inny adres IP DC (NAT, kilka kart), nazwa komputera, konto | adresy DC w DNS; dla NAT lub kilku kart – `Watchdog.Collector.AdditionalSourceAddresses` w `appsettings.json` (5.2) |
| Stan: „DC milczy” / „DC nie przekazuje zdarzeń” | przekazywanie przerwane (GPO, WinRM, zapora 5985) – kontrolery zawsze mają logowania sieciowe, więc cisza oznacza awarię | jak wyżej, od Forwarding/Operational na tym DC |

### 9.3 LDAP

| Objaw | Przyczyna | Rozwiązanie |
|---|---|---|
| `ldap_sasl_interactive_bind: Local error (-2)` … `Server not found in Kerberos database` | nazwa DC podana jako adres IP lub alias, brak `SASL_NOCANON on` przy błędnym PTR | zawsze pełna nazwa DC (`ldap://DC01.contoso.com`), `LDAPCONF=/etc/admng-watchdog/ldap.conf` |
| `SASL(-4): no mechanism available` | brak modułu SASL GSSAPI | `apt install libsasl2-modules-gssapi-mit` |
| `Strong(er) authentication required` (`… requires binds to turn on integrity checking …`) | wiązanie bez podpisu – DC wymaga podpisywania LDAP (domyślnie na Windows Server 2025) | użyj `-Y GSSAPI` z `ldap.conf` z 3.5 (`minssf=56`); nie używaj prostego wiązania bez TLS |
| `SASL(-15): mechanism too weak for this user` (wiązanie GSSAPI odrzucone, bo serwer nie oferuje szyfrowania ≥ 56 bitów) | wymuszone szyfrowanie (`minssf=56`) – i dobrze | sprawdź klucze AES konta i `ldap.conf`; nie obniżaj `minssf` |
| `SASL SSF: 0` | `ldap.conf` z `maxssf=0` (to ustawienie wyłącznie dla opcjonalnego LDAPS na 636) | dla portu 389 przywróć `ldap.conf` z 3.5 |
| Brak `tokenGroups` w teście z 2.6 / AD Manager: „Brak uprawnień” mimo członkostwa w grupie | konto nie może czytać `tokenGroups` kont operatorów | 2.6 – wpis `GR` dla klas `user`, `inetOrgPerson`, `computer` (Windows Authorization Access Group nie pomaga) |
| Stan: braki uprawnień (`permission.*`) | brak jawnych praw odczytu | 2.6 (także AdminSDHolder; SDProp do 60 min) |

### 9.4 SYSVOL

| Objaw (`smbclient`, stan `sysvol.*`) | Przyczyna | Rozwiązanie |
|---|---|---|
| `NT_STATUS_ACCESS_DENIED` dla folderu GPO | GPO bez odczytu dla Authenticated Users i Domain Computers | skrypt z 2.7 |
| `NT_STATUS_LOGON_FAILURE` | Kerberos: bilet, keytab, czas | testy 1 i 3 z 3.6 (test 3 można powtarzać przy działającej usłudze), 9.1 |
| `Could not find a suitable mechtype` / `NT_STATUS_INVALID_PARAMETER` | brak biletu TGT w pamięci podręcznej – `smbclient` nie pobiera go sam z keytabu | `kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'` (3.6, Test 3); usługa robi to sama przed każdym odczytem |
| `NT_STATUS_BAD_NETWORK_NAME` | zła nazwa udziału lub DC | `//DC01.contoso.com/SYSVOL` |
| `NT_STATUS_IO_TIMEOUT` / `Connection … failed` | zapora 445 (Debian lub DC) | `nft list ruleset`, `Test-NetConnection DC01.contoso.com -Port 445` z innej maszyny |
| `smbclient` „wisi” | brak `-N` – czeka na hasło | dodaj `-N` (3.6) |
| Stan: opóźnienie SYSVOL dla GPO (`gpo.<GUID>.sysvolLag`) | replikacja SYSVOL (DFSR) nie nadąża za zmianą wersji GPO | na DC: `dfsrdiag backlog` lub stan DFSR; usługa ponawia odczyt do 2 godzin |
| Zmiana w GPO z wykonawcą „nieustalony – SYSVOL_NOT_AUDITED” | ktoś zmienił pliki w SYSVOL bez zmiany wersji GPO (z pominięciem GPMC) | sprawdź, kto ma zapis do SYSVOL; to sygnał do wyjaśnienia |
