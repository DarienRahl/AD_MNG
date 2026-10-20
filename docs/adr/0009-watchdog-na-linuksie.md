# ADR-0009: Usługa AdMng Watchdog na Linuksie (Debian + systemd)

- Status: **proponowana – do akceptacji użytkownika**. Opiera się na ustaleniach z 2026-10-19, ale zawiera odstępstwa
  od nich, które użytkownik musi potwierdzić (sekcja „Odstępstwa od ustaleń z 2026-10-19”); szczegóły techniczne
  potwierdzi pilotaż na kontrolerach Windows Server 2025 i 2022 (`docs/dev/watchdog-linux-design.md`, rozdz. 22)
- Data: 2026-10-19 (poprawki po przeglądzie: 2026-10-20)
- Zmienia: [ADR-0008](0008-watchdog-dane-w-osobnym-folderze.md) (miejsce i ochrona danych usługi, sekrety, dzienniki –
  szczegóły w sekcji „Zmiana ADR-0008”) oraz zasady 1 i 2 w `CLAUDE.md` (pliki wyjściowe OpenWEC, keytab konta usługi)
- Specyfikacja wykonawcza: [`docs/dev/watchdog-linux-design.md`](../dev/watchdog-linux-design.md) (wiążąca dla etapu B)
- Instrukcja wdrożenia: [`docs/WATCHDOG-LINUX.md`](../WATCHDOG-LINUX.md)

## Kontekst

Wydanie 0.2.0 dostarcza AdMng Watchdog jako usługę Windows: konto gMSA, Kestrel lub HTTP.sys, klucze sekretów chronione
DPAPI-NG, odczyt dzienników zabezpieczeń DC przez RPC (Event Log Readers), instalacja skryptem PowerShell, dziennik
Application.

Użytkownik zdecydował, że usługa ma docelowo działać **wyłącznie na Linuksie z systemd** („usługa finalnie stała na
Linux – systemd”), jako rozbudowana wersja z historią zmian „kto, co, kiedy, skąd, było → jest”. Ustalenia z
2026-10-19:

- serwer Debian 13 (pakiet także dla Debiana 12) `admng-wd.contoso.com` (10.0.1.25), **niedołączony do domeny**;
  administratorzy serwera to 1–3 osoby Tier 0 (klucze SSH, bez logowania roota);
- konto usługi typu **komputer** z keytabem, w istniejącej OU Tier 0, **bez uprawnień administracyjnych**, bez
  logowania interaktywnego, tylko AES, hasło losowe zmieniane co 30 dni; grupy ról `AdMng-Watchdog-Admins` i
  `AdMng-Watchdog-Viewers`;
- historia zdarzeń przez **3 lata**, rekordy dzienników DC do dopasowania przez 30 dni; atrybucja obejmuje konto
  **i** komputer/adres źródłowy (logowania sieciowe 4624, nieudane 4771/4776);
- zdarzenia z DC przez **Windows Event Forwarding** do **OpenWEC** na tym samym serwerze (własny odbiornik – nie teraz);
- pełne śledzenie **treści GPO** (SYSVOL, „ustawienie: było → jest”);
- nowa reguła krytyczna **BadSuccessor** (dMSA);
- sekrety przez **systemd-creds** (TPM2, gdy jest, inaczej klucz hosta – bez decyzji użytkownika);
- certyfikat API z firmowego CA albo samodzielnie wygenerowany z potwierdzeniem odcisku w AD Manager;
- kopie lokalne (7 dni), zalecenie kopii na poziomie maszyny wirtualnej podane raz.

Środowisko klienta: kontrolery Windows Server 2025 i 2022 (poziom funkcjonalny najwyżej 2016), na Server 2025 domyślnie
wymagane podpisywanie LDAP.

Ograniczenia techniczne Linuksa (sprawdzone w laboratorium Samba 2026-10-19): brak gMSA bez dodatkowej tożsamości
hosta, brak odczytu dzienników zdarzeń Windows przez RPC, uwierzytelnianie Negotiate w ASP.NET Core zwraca tylko nazwę
konta (bez SID-ów grup), `LdapSessionOptions.Signing/Sealing` nie działa – poufność LDAP trzeba wymusić konfiguracją
libldap/SASL.

## Decyzja

1. **Platforma.** Usługa działa wyłącznie na Debianie 12/13 (amd64) jako jednostka systemd `admng-watchdog.service`
   (`Type=notify`, pełne utwardzenie sandboksem systemd), dostarczana pakietem `.deb` `admng-watchdog`
   (zależna od `aspnetcore-runtime-10.0` ≥ 10.0.13 z repozytorium Microsoft). Serwer nie jest dołączony do domeny:
   Kerberos, DNS i czas (chrony z DC) konfigurujemy ręcznie; usługa ma własne pliki `krb5.conf`, `ldap.conf` i
   `smb-client.conf` w `/etc/admng-watchdog`.
2. **Tożsamość: konto komputera + keytab zamiast gMSA.** Administrator Tier 0 tworzy na stacji PAW obiekt komputera
   `ADMNG-WD` (`ADMNG-WD$`) w OU Tier 0 z jednorazowym hasłem, SPN-ami `HTTP/admng-wd.contoso.com`,
   `HOST/admng-wd.contoso.com`, `HOST/ADMNG-WD`, szyfrowaniem tylko AES (0x18) i flagą „konto jest poufne i nie może być
   delegowane”. Na Debianie narzędzie `admng-watchdog-keytab` (własne, oparte na bibliotece MIT Kerberos, bez LDAP)
   zastępuje jednorazowe hasło losowym hasłem 120 znaków, zapisuje keytab `/etc/admng-watchdog/admng-wd.keytab`
   (root:admng-keytab 0640) i co 30 dni zmienia hasło (timer `admng-watchdog-keytab.timer`, sprawdzenie codzienne).
   Hasło nie jest nigdzie zapisywane – tylko klucze w keytabie (bieżąca i poprzednia wersja). Konto nie należy do
   żadnej grupy uprzywilejowanej (usługa odmawia startu, gdy należy), ma odebrane prawa logowania lokalnego, RDP,
   wsadowego i jako usługa i ma nadane w AD wyłącznie prawa odczytu. Zapisy, które każde konto komputera ma domyślnie
   we własnym obiekcie (obiekty podrzędne, SPN, nazwa DNS, kilka atrybutów), blokuje wpis „Odmów” dla SELF; tworzenie
   kont komputerów – `ms-DS-MachineAccountQuota` = 0 (warunek wdrożenia). Zostaje zmiana własnego hasła. Nie używamy
   msktutil ani adcli (zapisy LDAP do własnego obiektu, których skutków na DC Windows nie dało się zweryfikować, pliki
   tymczasowe w `/tmp`, własne timery pakietów) – odstępstwo od ustaleń, do potwierdzenia.
3. **LDAP.** Domyślnie port 389, SASL GSSAPI (Kerberos) z podpisywaniem i szyfrowaniem wymuszonym w `ldap.conf`
   (`SASL_SECPROPS minssf=56`, `SASL_NOCANON on`); usługa sprawdza wynegocjowany poziom zabezpieczeń po każdym
   powiązaniu. LDAPS (636) jest opcją. Nadal jedna sesja = jeden DC, preferowany emulator PDC.
4. **API.** Kestrel + Negotiate z keytabem, wyłącznie Kerberos, HTTPS 8443 tylko z podsieci stacji
   administratorów. Role ustala sama usługa: nazwa konta z biletu → `objectSid` i `tokenGroups` z AD (pamięć podręczna
   5 minut) → dotychczasowa obsługa ról po SID-ach z listą `DeniedSids` sprawdzaną najpierw. Funkcja `EnableLdap`
   uwierzytelniania Negotiate nie jest używana (role po nazwach grup dają się podrobić – sprawdzone w laboratorium).
5. **Zdarzenia z DC: WEF przez OpenWEC.** Kontrolery same wysyłają zdarzenia (subskrypcja inicjowana przez źródło,
   GPO na OU Domain Controllers) do OpenWEC (cea-sec/openwec, GPL-3.0) działającego jako osobna usługa `openwecd`
   z pakietu `admng-openwec` budowanego przez nas z przypiętej wersji źródeł. OpenWEC przyjmuje je przez Kerberos z tym
   samym kontem i keytabem (bilety do `HTTP/` i `HOST/admng-wd.contoso.com` – oba SPN-y mają ten sam klucz konta,
   sprawdzone w laboratorium), zapisuje w formacie RawJson do jednego pliku na DC, a Watchdog odczytuje te pliki
   z kursorem zatwierdzanym w tej samej transakcji co rekordy (dostarczenie co najmniej raz, duplikaty pomijane po
   (komputer, numer rekordu, czas)). Subskrypcja obejmuje dotychczasowy zestaw zdarzeń atrybucji oraz 4624 (logowanie
   sieciowe i RDP), 4625, 4771 i nieudane 4776 – źródło zmian (komputer/adres IP) jest ustalane z tych rekordów.
   Watchdog sprawdza, czy rekord pochodzi od uwierzytelnionego DC (konto, nazwa komputera i adres IP muszą się zgadzać).
6. **Treść GPO.** Usługa czyta foldery GPO w SYSVOL swojego DC (SMB 3, Kerberos, wymagane podpisywanie, tylko odczyt,
   dane wyłącznie w pamięci), analizuje `GptTmpl.inf`, `Registry.pol`, preferencje GPP, skrypty, `audit.csv`
   i komentarze i raportuje zmiany jako „ustawienie: było → jest”. Wykonawcę dziedziczy ze zdarzenia 5136 na obiekcie
   GPC. Ścieżka `gPCFileSysPath` wskazująca poza SYSVOL domeny jest zdarzeniem krytycznym i nie jest odczytywana.
7. **Nowe reguły.** BadSuccessor (utworzenie dMSA, zmiana `msDS-ManagedAccountPrecededByLink` /
   `msDS-DelegatedMSAState` i atrybutów strony zastępowanej) – krytyczne; zmiana `ms-DS-MachineAccountQuota` –
   krytyczna przy podniesieniu z 0; każda zmiana własnego konta `ADMNG-WD$` (poza jego rotacją hasła) – krytyczna;
   istotne zmiany treści GPO – wysokie lub krytyczne.
8. **Sekrety.** `ISecretStore` bez zmian (ASP.NET Core Data Protection); pierścień kluczy jest szyfrowany AES-256-GCM
   kluczem przechowywanym jako zaszyfrowane poświadczenie systemd (`systemd-creds --with-key=auto --tpm2-pcrs=`: TPM2
   + klucz hosta, gdy TPM2 jest dostępny, w przeciwnym razie klucz hosta; bez wiązania ze stanem rozruchu, więc
   aktualizacja UEFI nie blokuje usługi). Instalator wybiera wariant sam.
9. **Certyfikat.** Certyfikat z firmowego CA (wniosek z Debiana, `certreq` po stronie Tier 0), a gdy CA nie ma –
   certyfikat samodzielnie podpisany, wygenerowany przy instalacji; AD Manager przy pierwszym połączeniu pokazuje odcisk
   SHA-256 do porównania z `sudo admng-watchdog cert-fingerprint` i zapamiętuje go dopiero po potwierdzeniu; zmiana
   certyfikatu nigdy nie jest akceptowana po cichu.
10. **Retencja i kopie.** Zdarzenia Watchdog 1095 dni; rekordy dzienników DC związane ze zmianami 30 dni; rekordy
    logowań i błędów logowania – propozycja 48 godzin (źródło trafia do zdarzenia i jest przechowywane razem z nim; do
    potwierdzenia – odstępstwo 2); lokalne kopie zapasowe 7 dni.
11. **Wycofanie wersji Windows.** Usługa Windows pozostaje dostępna jako wydanie 0.2.0. Kod hostingu Windows (usługa
    SCM, HTTP.sys, DPAPI-NG, odczyt dzienników przez RPC, ACL NTFS, magazyn certyfikatów Windows, dziennik Application,
    skrypty PowerShell) zostaje usunięty po pierwszym wydaniu linuksowym (0.3.0). Bazę 0.2.0 można przenieść na
    Linuksa (bez kluczy – sekrety kanałów wprowadza się ponownie).

## Konsekwencje

- Na serwerze Watchdog nie ma żadnego komponentu Windows; serwer i jego administratorzy są zasobami Tier 0 (root
  serwera = tożsamość `ADMNG-WD$` i wszystkie dane usługi).
- Po stronie AD potrzebne są: konto komputera z wpisem odmowy dla SELF i dwie grupy w OU Tier 0, rekord A (i PTR),
  jawne prawa odczytu (jak dotąd, dla `ADMNG-WD$`; one dają też odczyt `tokenGroups` – grupa Windows Authorization
  Access Group nie jest potrzebna), `ms-DS-MachineAccountQuota` = 0, GPO ograniczające logowanie konta, GPO na
  kontrolerach (menedżer subskrypcji WEF, usługa WinRM, dostęp NETWORK SERVICE do dziennika Security, zasady
  inspekcji), SACL w domenie i na kontenerze PKI w partycji konfiguracji. Nie są już potrzebne: gMSA, grupa Event Log
  Readers dla konta usługi i reguły zapory „Remote Event Log Management” na DC (przy migracji usuwane).
- Kontrolery wysyłają więcej zdarzeń (4624 wszystkich logowań sieciowych, także kont komputerów – filtr XPath Windows
  nie potrafi ich wykluczyć); rozmiar dziennika Security na DC i ruch WEF trzeba dobrać na podstawie pomiaru
  w pilotażu. Watchdog odrzuca logowania kont komputerów i anonimowe przy odczycie.
- Atrybucja zależy od poprawnego GPO subskrypcji na każdym DC; usługa zgłasza w zakładce Stan DC, od którego nie
  przychodzą zdarzenia, opóźnienia i rekordy odrzucone przy sprawdzaniu tożsamości.
- Wspólny keytab dla API i OpenWEC oznacza, że przejęcie konta systemowego `openwec` pozwala podszyć się pod
  dowolnego użytkownika domeny wobec API Watchdoga (bilet usługi sfałszowany kluczem `HTTP/admng-wd`, tzw. *silver
  ticket*), także pod administratora Watchdoga – oraz daje tożsamość `ADMNG-WD$` (odczyt katalogu i SYSVOL); nie daje
  dostępu do bazy Watchdog. Usługa odrzuca zapytania do API z samego serwera (także z adresu pętli zwrotnej), więc
  sfałszowany bilet działa tylko z podsieci administratorów. Pełne rozdzielenie: drugie konto komputera `ADMNG-WEC$`
  i nazwa `wec.contoso.com` dla kolektora – odstępstwo 5, decyzja użytkownika.
- Konto komputera może domyślnie samo zmienić niektóre swoje atrybuty (np. ponownie włączyć RC4 w
  `msDS-SupportedEncryptionTypes`, dodać SPN dla własnej nazwy) – blokuje to wpis odmowy dla SELF, a każda zmiana
  własnego konta i tak jest zgłaszana jako krytyczna, mimo filtra „szumu komputerów”.
- Odtworzenie serwera na innej maszynie (inny klucz hosta lub stan TPM) nie odszyfruje klucza sekretów: usługa po
  `admng-watchdog reset-secrets-key` startuje z nowym kluczem, a hasło SMTP i adres Teams trzeba wprowadzić ponownie.
  Odtworzenie tej samej maszyny wirtualnej razem z dyskiem i stanem vTPM zachowuje wszystko.
- Nieudana rotacja hasła niczego nie psuje (hasło i keytab zostają); jedyny groźny przypadek (hasło zmienione, zapis
  keytabu nieudany) jest wykrywany natychmiast i naprawiany jednorazowym hasłem od administratora Tier 0.
- OpenWEC to mały projekt zewnętrzny przypięty do niewydanej wersji: budujemy go sami, dołączamy źródła (GPL-3.0)
  i aktualizujemy tylko po przeglądzie. OpenWEC działa jako osobny proces – licencja GPL nie obejmuje kodu AdMng.
- Zespół utrzymuje jedną platformę usługi; testy jednostkowe, testy usługi, testy integracyjne z laboratorium Samba
  i budowanie pakietów `.deb` działają w CI na Linuksie. Na Linuksie można wreszcie sprawdzić także role (dotąd tylko
  samo uwierzytelnienie). Zachowanie na prawdziwych DC Windows (bilety WinRM, podpisywanie LDAP na Server 2025,
  wolumen zdarzeń, dMSA) potwierdza pilotaż.
- Zmiany robione przez ADWS (moduł PowerShell ActiveDirectory, Centrum administracyjne AD) kontroler wykonuje sam u
  siebie – wykonawca jest ustalany, ale źródłem może być „lokalnie na DC”; pilotaż sprawdza, czy da się to poprawić.
- Do aktualizacji w etapie B: `docs/WATCHDOG-LINUX.md` (instrukcja wdrożenia – zgodnie z gotowymi pakietami),
  `docs/WATCHDOG.md` (rozdziały instalacji Windows zastąpione odesłaniem), `CLAUDE.md` (zasady 1 i 2, układ
  projektów, polecenia), wskazane rozdziały `docs/dev/watchdog-spec.md`, nota w nagłówku ADR-0008.

## Odstępstwa od ustaleń z 2026-10-19 (wymagają potwierdzenia)

Projekt i instrukcja przyjmują poniższe rozwiązania, ale użytkownik ich nie zatwierdził – przed etapem B trzeba je
potwierdzić albo zmienić.

1. **Tworzenie konta.** Ustalenie: konto tworzone z Debiana narzędziem msktutil/adcli. Projekt: konto tworzy
   administrator Tier 0 na PAW (`New-ADComputer` z hasłem jednorazowym), a hasło i keytab obsługuje własne narzędzie
   `admng-watchdog-keytab`. Powód: serwer nie potrzebuje żadnych praw do tworzenia obiektów w AD, brak zapisów LDAP do
   własnego obiektu i plików tymczasowych w `/tmp` (alternatywa 7).
2. **Retencja rekordów logowań.** Ustalenie: rekordy dzienników DC do dopasowania – 30 dni. Projekt: rekordy związane ze
   zmianami 30 dni, a nowe, masowe rekordy logowań i błędów logowania (4624/4625/4771/4776) – 48 godzin; ustalone
   źródło jest kopiowane do zdarzenia i zostaje z nim 3 lata. Powód: minimalizacja danych (RODO) – setki tysięcy
   logowań dziennie. 30 dni (720 h) mieści się w dozwolonym zakresie ustawienia, jeśli użytkownik tak zdecyduje.
3. **Zakres zdarzeń.** Ustalenie: 4624 (logowania sieciowe), 4771 i 4776. Projekt dodaje 4625 (nieudane logowania na DC
   – źródło blokad) i logowania RDP (4624 typ 10).
4. **Dodatkowe reguły i wymagania.** Poza ustaloną regułą BadSuccessor projekt dodaje reguły `tier0-machine-quota`
   (podniesienie `ms-DS-MachineAccountQuota` z 0) i `watchdog-self` (każda zmiana konta usługi) oraz wymaga
   `ms-DS-MachineAccountQuota` = 0 i wpisu odmowy dla SELF na koncie usługi – żeby konto rzeczywiście nie mogło
   niczego zmienić w AD poza własnym hasłem.
5. **Wspólny keytab API i OpenWEC.** Ustalony układ: OpenWEC używa tej samej nazwy `HTTP/admng-wd.contoso.com`.
   Skutek: przejęcie konta `openwec` pozwala sfałszować bilet do API (patrz „Konsekwencje”). Do decyzji: zostawić
   (z ograniczeniem zapytań do podsieci administratorów) albo dodać drugie konto `ADMNG-WEC$`, rekord A
   `wec.contoso.com` i osobny keytab czytany tylko przez `openwec` (zalecane dla Tier 0; projekt w
   `watchdog-linux-design.md`, 4.3).
6. **Laboratorium.** Ustalenie: 1 VM z DC Windows Server 2025 i 1 VM z Debianem 13. Do przejścia całej instrukcji
   potrzebna jest jeszcze stacja ADM-WS01 z Windows (AD Manager działa tylko na Windows, a zmiana zrobiona na DC ma
   jako źródło kontroler); opcjonalnie DC02 i mała maszyna z Mailpit do testów e-mail.

## Rozważone alternatywy

1. **gMSA na Linuksie** (np. demon pobierający hasło gMSA). Host i tak potrzebuje własnej tożsamości z keytabem, żeby
   pobrać hasło gMSA (dwa konta zamiast jednego), dochodzi zależność od KDS i dodatkowe oprogramowanie z prawami do
   sekretu gMSA. Brak zysku bezpieczeństwa wobec konta komputera z rotacją. Odrzucona.
2. **Własny odbiornik WEF w Watchdog.** Wymaga implementacji WS-Management/SOAP z szyfrowaniem wiadomości Kerberos,
   zakładek (bookmarks) i pulsu – duży, ryzykowny zakres. OpenWEC robi to już dziś. Odłożona („nie teraz” – decyzja
   użytkownika); format pośredni (pliki RawJson) pozwala później podmienić odbiornik.
3. **Dołączenie serwera do domeny (SSSD/realmd/winbind).** Daje automatyczny keytab i DNS, ale wprowadza na serwer
   Tier 0 GPO domeny, możliwość logowania kont domenowych, dodatkowe usługi i zapisy konta komputera wykonywane przez
   narzędzia dołączania. Usługa potrzebuje tylko Kerberos, LDAP i SMB z jedną tożsamością. Odrzucona.
4. **Agenci na kontrolerach domeny** (np. NXLog, Winlogbeat, własny agent). Oprogramowanie firm trzecich na DC (Tier 0)
   wymaga procesu zmian, aktualizacji i rozszerza powierzchnię ataku; WEF jest wbudowany w Windows. Odrzucona.
5. **Odczyt dzienników DC przez RPC z Linuksa** (MS-EVEN6). Brak dojrzałej biblioteki, wymaga członkostwa w Event Log
   Readers (odczyt wszystkich dzienników wszystkich DC) i otwartego RPC na DC. Odrzucona.
6. **Pozostawienie usługi Windows obok wersji linuksowej.** Dwie platformy do zabezpieczania i testowania; użytkownik
   chce wyłącznie Linuksa. Odrzucona (0.2.0 pozostaje do pobrania).
7. **msktutil lub adcli do tworzenia konta i rotacji.** Wymagają poświadczeń administratora na Debianie (tworzenie) albo
   zapisów LDAP do własnego obiektu (rotacja), tworzą tymczasowe `krb5.conf` w `/tmp`, msktutil generuje hasło
   63-znakowe i włącza własny timer. Odrzucone na rzecz własnego narzędzia na prymitywach MIT sprawdzonych
   w laboratorium; konto tworzy administrator Tier 0 na Windows.
8. **Tylko LDAPS.** Wymaga PKI na DC i (na Server 2025) wiązania kanału; GSSAPI z szyfrowaniem działa bez PKI. LDAPS
   pozostaje opcją.
9. **Inne wyjścia OpenWEC** (TCP, gniazdo Unix, Redis, Kafka). TCP i gniazdo datagramowe gubią zdarzenia przy
   niedostępności odbiorcy (dostarczenie „najwyżej raz”), Redis nie ma uwierzytelniania ani TLS, Kafka jest zbyt ciężka
   dla jednego serwera. Wybrano pliki.
10. **Biblioteka SMB w .NET zamiast `smbclient`.** Dostępna biblioteka obsługuje Kerberos tylko przez własny moduł
    uwierzytelniania, a szyfrowania SMB 3 z kluczami Kerberos nie dało się potwierdzić. Wybrano proces `smbclient`
    (sprawdzony w laboratorium); P/Invoke do libsmbclient pozostaje możliwą zmianą.

## Zmiana ADR-0008

ADR-0008 obowiązuje nadal; na Linuksie jego warunki realizujemy następująco:

1. **Jedno miejsce.** Folder danych to `/var/lib/admng-watchdog` (właściciel `admng-watchdog`, tryb **0700**, bez
   wpisów ACL POSIX, lokalny system plików): `watchdog.db` z plikami WAL, `backup/`, `keys/`. Zamiast ACL NTFS usługa
   przy każdym starcie sprawdza właściciela i tryb folderu i plików bazy; przy szerszych uprawnieniach nie startuje
   (chyba że lokalny administrator ustawi `AllowInsecureDataDirectoryAcl`). Ustawienia startowe leżą w
   `/etc/admng-watchdog` (właściciel root), których konto usługi nie może zmieniać – sprawdzane przy starcie.
   Pliki tymczasowe: `PrivateTmp=yes`, SQLite z `temp_store = MEMORY`; zrzuty pamięci wyłączone (`LimitCORE=0`,
   `DOTNET_DbgEnableMiniDump=0`), pamięć usługi nie trafia do swap (`MemorySwapMax=0`) – w miejsce wyłączenia WER.
2. **Drugie, krótkotrwałe miejsce: pliki wyjściowe OpenWEC.** Surowe zdarzenia z DC leżą w
   `/var/lib/openwec/out` (openwec:admng-watchdog, 2750, pliki 0640) tylko do czasu ich przetworzenia: pliki są
   rotowane co godzinę i usuwane po potwierdzonym odczycie przez Watchdog, a nieprzetworzone – najpóźniej po 7 dniach
   (maksymalnie 30). Baza OpenWEC zawiera wyłącznie subskrypcje i stan klientów. Ten wyjątek obejmuje tylko te dwa
   katalogi. Trzecie miejsce, wyłącznie w pamięci (tmpfs `/run/admng-watchdog`, 0700, usuwane przy zatrzymaniu
   usługi): pamięć podręczna Samby (może zawierać mapowania nazw i SID-ów, czyszczona po każdym odczycie SYSVOL)
   i plik postępu odczytu plików OpenWEC.
3. **Tylko to, co potrzebne.** Dochodzą: rekordy 4624/4625/4771/4776 ograniczone do pól potrzebnych do ustalenia
   źródła (bez logowań kont komputerów i anonimowych, retencja 48 godzin) oraz migawka ustawień GPO (tylko najnowsza dla
   każdego GPO; pliki spoza listy – wyłącznie nazwa, rozmiar i data; skrypty – wyłącznie skrót SHA-256).
4. **Nigdy sekrety.** Hasła preferencji GPP (`cpassword`) i wartości rejestru o nazwach wskazujących na hasła nie są
   zapisywane w żadnej postaci (tylko informacja o obecności, oznaczona jako krytyczna).
5. **Retencja.** Domyślna retencja zdarzeń Watchdog wynosi **1095 dni** (3 lata, decyzja użytkownika); rekordy
   dzienników DC związane ze zmianami – 30 dni; rekordy logowań – 48 godzin; kopie zapasowe – 7.
6. **Sekrety kanałów.** Klucze Data Protection są szyfrowane kluczem z poświadczenia systemd (TPM2 + klucz hosta lub
   klucz hosta) zamiast DPAPI-NG; wiązanie sekretów z miejscem docelowym bez zmian. Odtworzenie na innym hoście wymaga
   ponownego wprowadzenia sekretów (wcześniej: odtworzenie gMSA).
7. **Dzienniki diagnostyczne** trafiają do journald zamiast do dziennika Application – nadal bez wartości z katalogu,
   treści wyjątków i sekretów.
8. **Szyfrowanie dysku.** Zalecenie BitLocker zastępuje szyfrowanie dysku maszyny wirtualnej przez hiperwizor
   (preferowane) lub LUKS (wymaga hasła w konsoli przy każdym starcie); pamięć wymiany wyłączona; zalecana kopia
   zapasowa na poziomie maszyny wirtualnej (historia 3 lat).
9. **Poświadczenie konta usługi.** Keytab `/etc/admng-watchdog/admng-wd.keytab` (root:admng-keytab 0640) zawiera klucze
   Kerberos konta `ADMNG-WD$` – nigdy hasło – oraz bilet TGT tego konta w `/run/admng-watchdog/krb5cc` (tmpfs, 0600,
   tylko na czas działania usługi). To nowy, wąski wyjątek od zasady 2 w `CLAUDE.md`, obok sekretów kanałów
   w `ISecretStore`. Procedury ręczne w instrukcji trzymają bilety wyłącznie w pamięci (`/dev/shm`).
