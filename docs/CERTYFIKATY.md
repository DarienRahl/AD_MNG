[English](en/CERTIFICATES.md) · **Polski**

# Usługi certyfikatów (AD CS) w AD Managerze (etap P1)

Ten dokument opisuje etap **P1** ścieżki P: **przegląd infrastruktury kluczy publicznych lasu tylko do odczytu**,
czytany na żywo z partycji konfiguracji przez LDAP. AD Manager pokazuje urzędy certyfikacji przedsiębiorstwa, zaufane
urzędy główne i pośrednie, magazyn NTAuth, listy CRL, agentów odzyskiwania kluczy, identyfikatory OID i szablony
certyfikatów – z odczytanymi flagami i z tym, **kto naprawdę może się na nie zarejestrować**. Ocenia zdrowie w stylu
*PKI przedsiębiorstwa* (`pkiview.msc`), bezpieczeństwo według katalogu ESC (numeracja Certipy) i gotowość na silne
mapowanie certyfikatów (KB5014754), a na kontach dodaje trzy karty właściwości. Plan całej ścieżki (P2–P5): [README,
ścieżka P](../README.pl.md#ścieżka-p--urząd-certyfikacji-ad-cs-i-certyfikaty--p1-); projekt techniczny:
[docs/dev/p-certificate-services.md](dev/p-certificate-services.md).

Zasady, które obowiązują wszędzie:

- **Tylko odczyt.** P1 niczego nie zmienia w AD – ani w szablonach, ani w uprawnieniach, ani na kontach. Przegląd
  bezpieczeństwa nie ma przycisku *Napraw…*: naprawy przez plan zmian przyjdą w etapie P3, wystawianie certyfikatów w
  P2, a praca z samym urzędem w P4.
- **Tylko LDAP, jeden kontroler domeny.** Wszystko, co pokazują dokumenty i karty, pochodzi z kontrolera domeny okna
  (partycja konfiguracji jest replikowana w całym lesie, więc jeden kontroler zna PKI całego lasu). AD Manager **nie
  łączy się z urzędem certyfikacji** – ani przez DCOM, ani przez rejestr. Jedyne połączenia poza sesją LDAP to
  uzgadnianie TLS z portami LDAPS kontrolerów domeny i – tylko na przycisk – sprawdzenie adresów HTTP z certyfikatów
  (rozdział 10).
- **Nic z katalogu na dysku.** Odczytane obiekty PKI, wyniki sprawdzeń i ustalenia istnieją tylko w pamięci okna
  połączenia. Plik powstaje wyłącznie wtedy, gdy operator wybierze eksport i wskaże plik.
- **Materiał klucza – nigdy.** `userPKCS12`, poświadczenia mobilne i inne klucze nie są pokazywane, kopiowane ani
  eksportowane – także w Edytorze atrybutów i w kolumnach list (rozdział 8).

Przykłady pochodzą z domeny testowej `contoso.test` z wymyślonymi urzędami „Kontoso Główny CA” (urząd główny) i „Kontoso
Wystawiający CA” (urząd wystawiający).

## 1. Co obejmuje etap P1

| Dokument lub karta | Co pokazuje | Zamiast |
|---|---|---|
| *Usługi kluczy publicznych* (przegląd) | urzędy przedsiębiorstwa z certyfikatem, ścieżką i szablonami, zaufane urzędy i NTAuth, listy CRL, agenci odzyskiwania kluczy, identyfikatory OID, zdrowie, certyfikaty LDAPS kontrolerów domeny | `pkiview.msc`, *Edytor ADSI*, `certutil` |
| *Szablony certyfikatów* | wszystkie szablony lasu z odczytanymi flagami i okresami, urzędy, które je wystawiają, kto może się zarejestrować i kto może zmienić szablon, porównanie dwóch szablonów | `certtmpl.msc` (także karta *Zabezpieczenia*) |
| *Przegląd bezpieczeństwa AD CS* | ustalenia ESC1–5, 9, 13, 14, 15 i 17 z poziomem, warunkiem, ścieżką ataku, kontami i grupami | `certipy find -vulnerable`, Locksmith |
| *Gotowość na silne mapowanie i NTAuth* | konta ze słabymi mapowaniami, szablony uwierzytelniania bez rozszerzenia SID, urzędy spoza NTAuth, certyfikaty LDAPS i KDC kontrolerów domeny | ręczne sprawdzenia według KB5014754 |
| Karty *Opublikowane certyfikaty*, *Mapowania nazw*, *Poświadczenia kluczy* | certyfikaty, jawne mapowania certyfikatów i klucze Windows Hello / FIDO zapisane na koncie | karty ADUC, *Edytor atrybutów* |

Przy okazji P1 rozszerza zakładkę *Uprawnienia* w oknie *Przegląd kont* (U4b): delegowania czytane są teraz także z
kontenerów PKI w partycji konfiguracji (lokacje nadal nie), z dwoma nowymi prawami – *Zapis właściwości szablonu
certyfikatu istotnej dla bezpieczeństwa* i *Zapis altSecurityIdentities (jawne mapowania certyfikatów)*. Domyślne
uprawnienia PKI Windows (administratorzy domeny głównej lasu, *Wydawcy certyfikatów*, komputer urzędu na własnych
obiektach) nie są zgłaszane.

**Nie w P1:** wystawianie certyfikatów (P2), naprawy, edycja szablonów, uprawnień, NTAuth, mapowań i kluczy (P3), odczyt
konfiguracji, ról i bazy samego urzędu – a z nimi ESC6, ESC7, ESC8, ESC10, ESC11 i ESC16 (P4), analiza zdarzeń KDC,
reguły Watchdoga i historia kluczy z Watchdoga (P5). ESC12 jest poza zakresem programu.

## 2. Gdzie to znaleźć

- **Drzewo:** węzeł **Usługi kluczy publicznych** pod węzłem domeny (za węzłami zasad grupy). Zaznaczenie węzła otwiera
  przegląd, a jego trzy węzły podrzędne – *Szablony certyfikatów*, *Przegląd bezpieczeństwa AD CS* i *Gotowość na silne
  mapowanie i NTAuth* – swoje dokumenty. Menu kontekstowe: *Otwórz*, a na węźle głównym także cztery polecenia z menu.
- **Widok → Usługi kluczy publicznych w drzewie** pokazuje albo ukrywa węzeł (domyślnie jest widoczny; ustawienie jest
  wspólne dla wszystkich okien).
- **Narzędzia → Usługi certyfikatów (AD CS)**: *Usługi kluczy publicznych…*, *Szablony certyfikatów…*, *Przegląd
  bezpieczeństwa AD CS…*, *Gotowość na silne mapowanie i NTAuth…*.
- **Paleta poleceń** (Ctrl+K):

| Polecenie | Identyfikator | Grupa |
|---|---|---|
| *Usługi kluczy publicznych…* | `pki.overview` | *Narzędzia* |
| *Szablony certyfikatów…* | `pki.templates` | *Narzędzia* |
| *Przegląd bezpieczeństwa AD CS…* | `pki.security` | *Narzędzia* |
| *Gotowość na silne mapowanie i NTAuth…* | `pki.readiness` | *Narzędzia* |
| *Usługi kluczy publicznych w drzewie* | `view.pkiNodes` | *Widok* |

Każdy dokument otwiera się jako karta okna połączenia, jedna każdego rodzaju; polecenie dla otwartej karty tylko ją
wybiera. Dokumenty prowadzą do siebie nawzajem: *Otwórz szablon* przechodzi do dokumentu *Szablony certyfikatów* z
zaznaczonym szablonem, *Otwórz urząd* – do przeglądu z zaznaczonym urzędem, a *Otwórz konto*, *Otwórz grupę*, *Otwórz
komputer* i *Otwórz obiekt* otwierają kartę właściwości obiektu.

**Jeden odczyt na okno.** Cztery dokumenty korzystają z tego samego odczytu PKI okna połączenia (i z jednego liczenia
kont – rozdział 4.2): drugi dokument nie czyta katalogu ponownie. *Odśwież*, *Sprawdź ponownie*, *Odczytaj ponownie*
albo F5 w dowolnym z nich czyta wszystko od nowa, a pozostałe otwarte dokumenty PKI pokazują nowy odczyt. *Zmień
kontroler domeny…*, rozłączenie i zamknięcie okna usuwają odczyt z pamięci.

## 3. Przegląd „Usługi kluczy publicznych”

Nagłówek podaje podsumowanie (np. *Urzędy przedsiębiorstwa: 2 · szablony: 26 · zaufane urzędy główne: 1 · certyfikaty
NTAuth: 1*) i skąd oraz kiedy odczytano dane (*Odczytano z dc01.contoso.test, 13.10.2026 14:00.*). Przegląd czyta jednym
stronicowanym wyszukiwaniem `CN=Public Key Services,CN=Services,CN=Configuration,…` z deskryptorami zabezpieczeń.
*Odśwież* (F5) czyta ponownie.

Panel **Uwagi z odczytu** mówi, czego odczyt nie mógł zobaczyć – AD po cichu pomija obiekty, których konto nie może
odczytać:

| Uwaga | Znaczenie |
|---|---|
| *Nie znaleziono kontenera Usługi kluczy publicznych – w tym lesie nie ma AD CS* | las bez AD CS (np. laboratorium Samba); przegląd jest pusty |
| *Odmowa dostępu do kontenera Usługi kluczy publicznych – PKI nie zostało ocenione* | konto okna nie może czytać kontenera; nic nie jest oceniane jako „bezpieczne” |
| *Obiekt PKI jest niewidoczny – brak obiektu albo brak prawa odczytu* | brakuje dobrze znanego kontenera (`CN=NTAuthCertificates`, `CN=Enrollment Services`, `CN=Certificate Templates`, `CN=Certification Authorities`, `CN=AIA`, `CN=CDP`, `CN=OID`, `CN=KRA`) |
| *Nie można odczytać deskryptora zabezpieczeń szablonu – jego prawa nie zostały ocenione* | brak prawa odczytu uprawnień szablonu; taki szablon nigdy nie jest traktowany jako bezpieczny |
| *Wartość certyfikatu nie jest poprawnym kodowaniem DER* (i to samo dla listy CRL) | wiersz zostaje, ale bez szczegółów |
| *Nie znaleziono obiektu komputera serwera urzędu certyfikacji* | przy urzędzie nie ma *Otwórz komputer* i *Zdalne akcje…* |
| *Serwer urzędu certyfikacji należy do innej domeny – jego SID jest nieznany, więc liczy się jako konto spoza Tier 0* | okno domeny podrzędnej, urząd w domenie głównej (rozdział 11) |
| *Nie można rozpoznać w tym oknie podmiotu zabezpieczeń lub połączonej grupy (inna domena lasu)* | uprawnienie albo grupa z innej domeny lasu – liczba kont nieznana |

Przejściowy błąd kontrolera (serwer niedostępny, zajęty) jest błędem odczytu, a nie komunikatem „w tym lesie nie ma AD
CS”.

### 3.1 Urzędy przedsiębiorstwa

Obiekty `CN=Enrollment Services` – kolumny *Nazwa*, *Host*, *Ważny do*, *Stan*, *NTAuth* i *Szablony*. Panel szczegółów
zaznaczonego urzędu:

- **Certyfikat urzędu** – podmiot, wystawca, ważność, klucz publiczny, algorytm podpisu, numer seryjny, odcisk palca
  (SHA-1) i SHA-256, alternatywna nazwa podmiotu, przeznaczenie, punkty dystrybucji listy CRL i dostęp do informacji o
  urzędzie; liczba certyfikatów w `cACertificate` (z odnowieniami – pokazywany jest najnowszy).
- **Ścieżka certyfikacji** zbudowana wyłącznie z certyfikatów urzędów zapisanych w AD (wystawca = podmiot, identyfikator
  klucza wystawcy = identyfikator klucza podmiotu): *(zaufany urząd główny w AD)* na końcu albo *(certyfikatu wystawcy
  nie ma w AD)*. Nic nie jest pobierane z sieci.
- **Szablony certyfikatów do wystawienia** (`certificateTemplates`) z przyciskiem *Otwórz szablon*.
- **Usługi rejestracji certyfikatów w sieci Web (CES)** z `msPKI-Enrollment-Servers`.
- **Otwórz komputer** i **Zdalne akcje…** (K1, [KOMPUTERY.md](KOMPUTERY.md)) dla serwera urzędu – po obiekcie komputera
  znalezionym w domenie okna (po `dNSHostName`). Serwer z innej domeny lasu: *Serwer urzędu należy do innej domeny lasu
  – otwórz go z okna tamtej domeny.*

### 3.2 Zaufane urzędy i NTAuth

Certyfikaty z `CN=Certification Authorities` (*Zaufany urząd główny*), `CN=AIA` (*Urząd pośredni (AIA)*, *Certyfikat
krzyżowy (AIA)*) i `CN=NTAuthCertificates` (*NTAuth*) – kolumny *Magazyn*, *Podmiot*, *Wystawca*, *Ważny do*, *Stan*,
*Urząd przedsiębiorstwa* (dopasowanie po skrócie certyfikatu, nie po nazwie) i *Odcisk palca*; szczegóły jak w 3.1.

Podsumowanie NTAuth: *Certyfikaty NTAuth: 1 · urzędy przedsiębiorstwa w NTAuth: 1 z 2*. Urząd przedsiębiorstwa jest w
NTAuth, gdy magazyn zawiera którykolwiek z jego certyfikatów. Gdy urząd wystawia szablony logowania (uwierzytelnianie
klienta), a nie ma go w NTAuth, przegląd ostrzega: *Kontoso Główny CA wystawia certyfikaty logowania (Logowanie-Kartą),
ale nie ma go w magazynie NTAuth – logowanie kartą i certyfikatem z jego certyfikatami się nie powiedzie.*

### 3.3 Listy CRL

Listy z `CN=CDP` – kolumny *Urząd*, *Host*, *Rodzaj* (*Podstawowa lista CRL*, *Różnicowa lista CRL* z numerem listy
podstawowej, *Lista odwołań urzędów (ARL)*), *Numer*, *Data wejścia w życie*, *Następna aktualizacja*, *Odwołane* i
*Stan*. Lista jest przypisana do urzędu po wystawcy i identyfikatorze klucza, nigdy po nazwie kontenera (`certutil`
zmienia nazwy urzędów w `CN=CDP` i `CN=AIA`). Odwołane certyfikaty są tylko liczone – ich numerów seryjnych P1 nie
pokazuje.

### 3.4 Agenci odzyskiwania kluczy i identyfikatory OID

- **Agenci odzyskiwania kluczy** (`CN=KRA`): *Agent*, *Podmiot*, *Wystawca*, *Ważny do*, *Stan*. Odzyskiwania kluczy nie
  ma w zakresie programu.
- **Identyfikatory OID** (`CN=OID`): *Nazwa wyświetlana*, *Rodzaj* (*Szablon certyfikatu*, *Zasady wystawiania*, *Zasady
  aplikacji*, *Inny*), *OID* i *Połączona grupa* (`msDS-OIDToGroupLink`) z przyciskiem *Otwórz grupę*. Zasada
  wystawiania połączona z grupą dodaje tę grupę do biletu Kerberos każdego, kto loguje się takim certyfikatem – zob.
  ESC13 w rozdziale 5.

### 3.5 Zdrowie

Karta **Zdrowie** ocenia PKI tak jak *PKI przedsiębiorstwa* (`pkiview.msc`), dla wszystkich urzędów naraz. Kolumny
*Urząd*, *Element*, *Stan*, *Ważny do*, *Lokalizacja* i *Wynik* (wynik sprawdzenia HTTP). Wiersze dla każdego urzędu
przedsiębiorstwa: *Certyfikat urzędu* (najnowszy z `cACertificate`), *Lokalizacja AIA* i *Lokalizacja CDP* z jego
certyfikatu (gdzie są certyfikat i lista CRL jego wystawcy), jego *Podstawowa lista CRL* i *Różnicowa lista CRL* z
`CN=CDP` oraz *Lokalizacja różnicowej listy CRL*; do tego certyfikaty agentów odzyskiwania kluczy i certyfikaty NTAuth.

| Stan | Certyfikat | Lista CRL |
|---|---|---|
| *OK* | ważny i do końca zostało więcej niż próg | do następnej aktualizacji zostało co najmniej `max(1 dzień, 20 % okresu ważności listy)` |
| *Wygasa* | do końca zostało najwyżej tyle dni, ile wynosi próg: 180 dni dla certyfikatów urzędów, 30 dni dla pozostałych (rozdział 9) | zostało mniej niż `max(1 dzień, 20 %)` |
| *Wygasła* | po dacie `NotAfter` | po dacie `nextUpdate` |
| *Niedostępna* | brak obiektu, nieczytelna wartość, lokalizacja bez certyfikatu wystawcy albo nieudane sprawdzenie HTTP | brak listy, lokalizacja bez listy tego urzędu albo nieudane sprawdzenie HTTP |
| *Nieznany* | przed datą `NotBefore` albo lokalizacja, której nie sprawdzono | lista bez `nextUpdate` (nigdy *Wygasła*) albo przed datą wejścia w życie |

Lokalizacje:

- `ldap:///CN=…` (bez hosta) – sprawdzane w tym, co odczytano: znaleziony obiekt ma swój stan, brakujący obiekt albo
  obiekt bez certyfikatu lub listy tego urzędu jest *Niedostępna*; lokalizacja spoza `CN=Public Key Services` jest
  *Nieznany*.
- `ldap://host/…`, `file:` i ścieżki UNC – *Nieznany* (nie sprawdzono): program nie łączy się z innym serwerem.
- `http://` i `https://` – *Nieznany*, dopóki operator nie naciśnie *Sprawdź adresy HTTP* (3.6).
- OCSP nie ma wiersza zdrowia (jak w `pkiview.msc`) – jest tylko na liście *Adresy HTTP*.

Nad listą: podsumowanie (*OK: 9 · wygasa: 1 · wygasła: 0 · niedostępna: 1 · nieznany: 4*) i użyte progi. Stany są
liczone **w chwili pokazania** z odczytu, bieżących progów i zegara: przy każdym przejściu na kartę, po F5 i po zmianie
progów w ustawieniach – bez ponownego czytania katalogu.

### 3.6 Sprawdzanie adresów HTTP

Adresy `http` i `https` z certyfikatów (AIA – wystawcy urzędu, CDP – podstawowa lista CRL, różnicowa lista CRL, OCSP)
program sprawdza **tylko po naciśnięciu *Sprawdź adresy HTTP*** na karcie *Zdrowie*. Bez tego nic nie jest pobierane.

- Do serwera docelowego nie trafiają żadne poświadczenia, ciasteczka ani wstępne uwierzytelnianie. Gdy stacja używa
  serwera proxy, żądania idą przez niego, a poświadczenia konta Windows dostaje tylko proxy.
- Najwyżej 3 przekierowania – każdy cel sprawdzany według tych samych zasad; 15 s na adres razem z treścią; treść do 20
  MB; 4 adresy naraz.
- `file:`, ścieżki UNC (wysłałyby NTLM do wskazanego hosta), `ldap://` (inny kontroler) i adresy z nazwą użytkownika i
  hasłem nie są kontaktowane: *Nie sprawdzono (to nie jest adres http/https)*.
- Treść AIA musi być certyfikatem (DER, PEM albo paczka PKCS #7 – liczy się pierwszy certyfikat urzędu), treść CDP –
  listą CRL; przy OCSP liczy się tylko osiągalność: każda odpowiedź HTTP to *OK* z kodem stanu (P1 nie wysyła zapytań
  OCSP). Budowanie łańcucha HTTPS niczego nie pobiera.
- Wyniki: lista **Adresy HTTP** z kolumnami *Rodzaj*, *Lokalizacja* i *Wynik* (*OK*, *Nie znaleziono (404)*, *Błąd
  HTTP*, *Brak odpowiedzi w ciągu 15 s*, *Większe niż 20 MB*, *To nie jest certyfikat ani lista CRL*, *Uzgadnianie HTTPS
  nie powiodło się*, *Nie można rozpoznać nazwy hosta*, *Nie można nawiązać połączenia*, *Nie sprawdzono (to nie jest
  adres http/https)*), a wiersze zdrowia dostają swój stan. Pobrana treść jest odrzucana zaraz po odczytaniu; wyniki są
  w pamięci okna do F5.

### 3.7 Certyfikaty LDAPS kontrolerów domeny

Karta **Certyfikaty LDAPS** sprawdza certyfikat, który każdy kontroler domeny okna przedstawia na porcie LDAPS:

- **Kiedy:** przy pierwszym otwarciu karty, po naciśnięciu *Sprawdź certyfikaty LDAPS* oraz – gdy karta była już
  otwarta, a przegląd nadal jest otwarty – po F5 (w dowolnym dokumencie PKI okna: F5 odrzuca wyniki sondy razem z
  odczytem) i po zmianie kontrolera domeny. Nigdy w tle.
- **Co:** połączenie TCP z portem 636 każdego kontrolera domeny (i 3269 na wykazach globalnych) i **samo uzgadnianie
  TLS** – bez powiązania LDAP, bez certyfikatu klienta (kontroler o niego prosi – program żadnego nie oferuje) i bez
  danych aplikacji. Najwyżej 4 kontrolery naraz, po 5 s; kontroler okna pierwszy. Cel to nazwa z `dNSHostName`, nigdy
  adres IP.
- **Kolumny:** *Kontroler domeny*, *Port* (*636* albo *3269 (wykaz globalny)*), *Wynik* (*Przedstawiony certyfikat
  LDAPS*, *Nie można rozpoznać nazwy hosta*, *Port zamknięty*, *DC bez certyfikatu LDAPS (uzgadnianie TLS nie powiodło
  się)*, *Brak odpowiedzi w ciągu 5 s*, *Nieprawidłowa nazwa hosta*), *Sprawdzenia*, *Ważny do* i *Certyfikat KDC*.
- **Sprawdzenia** przedstawionego certyfikatu: nazwa pasuje do kontrolera (wpis DNS w alternatywnej nazwie podmiotu, bez
  niego nazwa pospolita; symbol wieloznaczny tylko jako cała pierwsza etykieta), nazwa domeny w SAN (tylko ostrzeżenie:
  *nazwy domeny nie ma w SAN (ldaps://contoso.test nie zadziała)*), EKU uwierzytelniania serwera (albo brak EKU),
  ważność (próg dla certyfikatów, nie urzędów), łańcuch do urzędu głównego opublikowanego w AD i wystawca w NTAuth.
  Łańcuch jest budowany tylko z certyfikatów odczytanych z AD – bez pobierania i bez magazynu certyfikatów Windows, więc
  automatyczna aktualizacja certyfikatów głównych Windows też niczego nie pobiera.
- **Certyfikat KDC** to wskaźnik z certyfikatów opublikowanych w `userCertificate` obiektu kontrolera domeny (nie z
  certyfikatu LDAPS i nie z magazynu samego KDC – ten odczyta etap P4): *Gotowy (z certyfikatów opublikowanych w AD)*,
  *Brak EKU uwierzytelniania KDC* (np. certyfikat z szablonu *Kontroler domeny* w wersji 1: PKINIT działa, ale nie przy
  ścisłej weryfikacji KDC), *Wystawca spoza magazynu NTAuth*, *Wygasły*, *Nieznany – brak certyfikatu opublikowanego w
  AD*.

## 4. Szablony certyfikatów

Lista wszystkich obiektów `CN=Certificate Templates` z kolumnami:

| Kolumna | Skąd |
|---|---|
| *Nazwa wyświetlana*, *Nazwa szablonu (cn)* | `displayName`, `cn` |
| *Schemat*, *Wersja* | `msPKI-Template-Schema-Version` (1–4; brak = 1), `revision` i `msPKI-Template-Minor-Revision` |
| *Przeznaczenie* | zasady aplikacji szablonów w wersji 2 i nowszych (według nich urząd wystawia certyfikat), inaczej `pKIExtendedKeyUsage`; brak = *Dowolny cel (brak EKU)* |
| *Okres ważności*, *Okres odnawiania* | `pKIExpirationPeriod`, `pKIOverlapPeriod` – w największej jednostce, która dzieli okres bez reszty: lata (365 dni), miesiące (30 dni), tygodnie, dni, godziny – jak w `certtmpl.msc` |
| *Minimalny rozmiar klucza, dostawca* | `msPKI-Minimal-Key-Size`, `pKIDefaultCSPs` (w kolejności pierwszeństwa) |
| *Nazwa podmiotu* | *Dostarcz w żądaniu* albo *Kompiluj na podstawie informacji usługi Active Directory* |
| *Zatwierdzenie menedżera certyfikatów*, *Podpisy autoryzowane* | flaga `CT_FLAG_PEND_ALL_REQUESTS`, `msPKI-RA-Signature` (brak = 0) |
| *Publikuj w AD*, *Zastępowane szablony*, *Zasady wystawiania* | `CT_FLAG_PUBLISH_TO_DS`, `msPKI-Supersede-Templates`, `msPKI-Certificate-Policy` |
| *Wystawiany przez* | urzędy przedsiębiorstwa, które mają szablon w `certificateTemplates`; inaczej *niewystawiany przez żaden urząd* |
| *Kto może się zarejestrować* | podsumowanie z 4.2 |

Nad listą: wyszukiwanie (Ctrl+F; nazwa, OID, przeznaczenie, urząd, podmiot zabezpieczeń), pole *Tylko szablony
niewystawiane przez żaden urząd* i podsumowanie *Szablony: 26, niewystawiane przez żaden urząd: 2*. Panel szczegółów
zaznaczonego szablonu ma części *Ogólne* (OID szablonu, zasady aplikacji, rozszerzone użycie klucza
(`pKIExtendedKeyUsage`), zasady wymagane w podpisie autoryzowanym, dostawcy usług kryptograficznych), *Flagi*, *Kto może
się zarejestrować* i *Kto może zmienić szablon*. *Kopiuj szczegóły* kopiuje opis szablonu do schowka, *Odśwież* (F5)
czyta ponownie.

### 4.1 Flagi

Cztery grupy: *Nazwa podmiotu (msPKI-Certificate-Name-Flag)*, *Rejestracja (msPKI-Enrollment-Flag)*, *Klucz prywatny
(msPKI-Private-Key-Flag)* i *Ogólne (flags)*. Każdy ustawiony bit to zdanie w języku interfejsu, np. *Wnioskujący podaje
nazwę podmiotu w żądaniu* albo *Rozszerzenie zabezpieczeń z identyfikatorem SID (szOID_NTDS_CA_SECURITY_EXT) nie jest
dodawane*; nazwa techniczna bitu (`CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT`, `CT_FLAG_NO_SECURITY_EXTENSION` …) jest w
podpowiedzi. Bity ogólne, które protokoły ignorują, mają dopisek *(przestarzała, ignorowana)*. Bity, których nie opisuje
specyfikacja MS-CRTD, są pokazane jako *Nieznane bity: 0x…* (dotyczy to też `0x80` i `0x200` flag rejestracji, które
podaje Certipy). Pola klucza prywatnego *Minimalna wersja urzędu* i *Minimalna wersja klienta* są liczbami, nie flagami.

### 4.2 Kto może się zarejestrować i kto może zmienić szablon

Prawa są odczytane z listy DACL szablonu **tak, jak ocenia je Windows** (AccessCheck): o każdym prawie decyduje pierwszy
wpis ACE w kolejności listy, który dotyczy danego konta i obejmuje to prawo – także wpis odmowy; wpisy tylko do
dziedziczenia i wpisy dla innych klas obiektów są pomijane, prawa ogólne (GENERIC_*) rozwijane.

- **Kto może się zarejestrować:** *Rejestruj* i *Autorejestracja* (także przez *Pełną kontrolę*; *Autorejestracja* sama
  nie wystarcza do rejestracji – potrzebne jest też *Rejestruj*).
- **Kto może zmienić szablon:** *Pełna kontrola*, *Modyfikowanie uprawnień*, *Modyfikowanie właściciela*, *Zapis
  wszystkich właściwości*, *Zapis właściwości istotnej dla bezpieczeństwa* (dla każdego z dziewięciu atrybutów
  `msPKI-Certificate-Name-Flag`, `msPKI-Enrollment-Flag`, `pKIExtendedKeyUsage`, `msPKI-Certificate-Application-Policy`,
  `msPKI-RA-Signature`, `msPKI-RA-Application-Policies`, `msPKI-RA-Policies`, `msPKI-Certificate-Policy`,
  `msPKI-Template-Schema-Version` osobno), *Zapis innej właściwości* i *Właściciel*. Prawa, których nikt nie ma, są
  pomijane.

Najpierw – od razu – widać podmioty zabezpieczeń z listy ACL (nazwy z AD). Potem w tle **jeden przebieg po kontach
domeny** (*Liczenie kont…*) rozwija członkostwo w grupach, także zagnieżdżone i podstawowe: przy każdym prawie i
podmiocie pojawia się liczba kont (*kont: 412 (+ wyłączonych: 9)*), pierwsze 20 kont i wiersz *Razem*. Przebieg jest
jeden na okno – korzystają z niego też przegląd bezpieczeństwa i raport gotowości – i kończy się razem z zamknięciem
okna albo zmianą kontrolera. Gdy liczenie się nie uda, zostaje widok podmiotów z przyczyną.

Każdy podmiot ma **klasę**, od której zależy ocena w przeglądzie bezpieczeństwa:

| Klasa | Kto |
|---|---|
| *Tier 0 / infrastruktura* | grupy Tier 0 katalogu programu i każde konto lub grupa, które do nich należą (także zagnieżdżone); SYSTEM, KONTROLERY DOMENY PRZEDSIĘBIORSTWA, SELF, TWÓRCA-WŁAŚCICIEL, Administratorzy; w każdej domenie lasu (po RID): konto Administrator (500), Domain Admins (512), Domain Controllers (516), Cert Publishers (517), Schema Admins (518), Enterprise Admins (519), Read-only Domain Controllers (521), Enterprise Read-only Domain Controllers (498), Key Admins (526), Enterprise Key Admins (527); konto komputera urzędu |
| *Szeroka grupa* | Wszyscy, Użytkownicy uwierzytelnieni, LOGOWANIE ANONIMOWE, Ta organizacja, INTERAKTYWNA, SIEĆ, wbudowane grupy Użytkownicy i Goście oraz Domain Users (513), Domain Guests (514) i Domain Computers (515) każdej domeny lasu |
| *Konto lub grupa spoza Tier 0* | każdy inny podmiot |

Nazwa konta albo grupy z przyciskiem *Otwórz* otwiera jej kartę właściwości (wszyscy członkowie grupy są na jej karcie
*Członkowie*). Przy szerokiej grupie widać tylko liczbę (*Szeroka grupa: ma je praktycznie każde konto.*). Podmiot z
innej domeny lasu ma *liczba nieznana (inna domena)*. Szablon, którego deskryptora nie dało się odczytać, ma komunikat
*Nie można odczytać deskryptora zabezpieczeń szablonu – jego prawa nie zostały ocenione (nigdy nie są traktowane jako
bezpieczne).*

### 4.3 Porównanie i eksport

- **Porównaj…** otwiera w dokumencie panel *Porównanie szablonów*: dwa szablony zaznaczone na liście (Ctrl+kliknięcie)
  albo zaznaczony i drugi wybrany w panelu (*z*). Wiersze, którymi się różnią, są wyróżnione; *Tylko różnice* ukrywa
  pozostałe, *Różnice: N* podaje ich liczbę, *Zamknij porównanie* zamyka panel.
- **Eksportuj do CSV…** i **Eksportuj do HTML…** zapisują pokazane (przefiltrowane) szablony do pliku wskazanego przez
  operatora. CSV ma ochronę przed formułami Excela. Raport HTML jest samodzielną stroną (bez skryptów i zasobów z sieci)
  ze stopką: *Raport zawiera nazwy szablonów, identyfikatory OID, przeznaczenie oraz nazwy i liczby kont z katalogu.
  Przechowuj go z taką samą ostrożnością jak sam katalog.*

## 5. Przegląd bezpieczeństwa AD CS

Dokument **Przegląd bezpieczeństwa AD CS** szuka konfiguracji podatnych na znane ataki na AD CS. Po otwarciu korzysta z
odczytu PKI i liczenia kont okna, czyta uprawnienia obiektów PKI (kontenery, NTAuth, obiekty OID, usługi rejestracji,
obiekty urzędów, obiekty komputerów urzędów i konta Tier 0), rozpoznaje konta i grupy, a potem ocenia reguły – dokument
pokazuje, który etap trwa. *Anuluj* przerywa (pokazane wiersze zostają), *Sprawdź ponownie* (F5) czyta wszystko od nowa.
Nad listą zawsze jest zakres:

> Odczytuje przez LDAP szablony, obiekty PKI i konta (ESC1–5, 9, 13, 14, 15, 17) i niczego nie zmienia. Zakłada, że
> każdy urząd nadaje Użytkownikom uwierzytelnionym prawo „Rejestruj”; ustawienia samego urzędu (ESC6, 7, 8, 10, 11, 16)
> sprawdzi etap P4.

Domyślne PKI systemu Windows nie daje żadnej uwagi (*Nie znaleziono podatnej konfiguracji – domyślne PKI systemu Windows
nie daje żadnej uwagi.*). Domyślny, zaktualizowany urząd dodaje rozszerzenie SID do certyfikatów, których podmiot
pochodzi z AD, więc domyślny las nie daje ani ESC9 (flaga szablonu wyczyszczona), ani ESC16 (P4). W lesie bez AD CS
lista mówi *W tym lesie nie ma AD CS – nie ma czego sprawdzać.*

### 5.1 Poziomy, filtry i szczegóły

- **Poziomy:** *Krytyczna*, *Wysoka*, *Średnia*, *Niska*, *Informacja*; kafelki z liczbami (*Krytyczne: 2*, *Wysokie: 1*
  …) filtrują po poziomie.
- **Filtry:** *Ważność*, *Technika* (numer ESC), *Obszar* (*Szablon certyfikatu*, *Urząd certyfikacji*, *Obiekt PKI*,
  *Konto*), *Szablon* (także szablony powiązane – ustalenie agenta rejestracji widać przy jego szablonach docelowych) i
  wyszukiwanie (*Szukaj w uwagach (tytuł, szablon, konto, ESC)*, bez względu na polskie znaki); *Wyczyść filtry*.
- **Lista** jest pogrupowana według obiektu (*Szablon certyfikatu: Użytkownik-VPN-Podatny*), grupy od najpoważniejszej
  uwagi. Kolumny: *Ważność*, *ESC*, *Obiekt*, *Konta i grupy*, *Uwaga*.
- **Szczegóły** zaznaczonej uwagi: *Warunek*, *Ścieżka ataku*, *Co zrobić*, *Czego dotyczy*, *Konta i grupy* (z klasą,
  prawem i liczbą kont; *inna domena – liczba kont nieznana*), *Zastrzeżenia*, *Powiązane obiekty* i *Identyfikatory*
  (*Certipy: ESC1 · Locksmith: ESC1 · MDI: –*).

### 5.2 Sprawdzenia i kody ustaleń

Reguły rejestracji (ESC1, 2, 3, 9, 13, 15, 17) dotyczą szablonu, na który **może się zarejestrować** podmiot spoza Tier
0 – ma skuteczne prawo *Rejestruj* (także przez *Pełną kontrolę* albo wszystkie prawa rozszerzone; sama
*Autorejestracja* nie wystarcza). Poziom zależy od najgroźniejszej klasy: szeroka grupa daje najwyższy poziom reguły,
inne konto lub grupa – *Średnia*; ESC13 i ESC15 mają poziom z tabeli bez względu na klasę. Podmioty Tier 0 nie dają
uwag. Kolumna „Zatwierdzenie” mówi, co robi zatwierdzenie menedżera certyfikatów albo wymagany podpis autoryzowany:
wyłącza uwagę, obniża ją o poziom (z zastrzeżeniem) albo nie chroni (atakujący zmienia szablon albo konto).

| ESC | Kod ustalenia | Locksmith | Kiedy jest uwaga | Poziom | Zatwierdzenie |
|---|---|---|---|---|---|
| ESC1 | `EnrolleeSuppliesSubjectClientAuth` | ESC1 | podmiot dostarcza wnioskujący (*Dostarcz w żądaniu*), a certyfikat pozwala na uwierzytelnianie klienta (także logowanie kartą, PKINIT, dowolny cel) | szeroka grupa: Krytyczna, inne: Średnia | wyłącza |
| ESC2 | `AnyPurposeOrNoEku` | ESC2 | certyfikat jest do dowolnego celu (*Dowolny cel* albo brak EKU) | Krytyczna / Średnia | wyłącza |
| ESC3 | `EnrollmentAgentTemplate` | ESC3 | szablon wystawia certyfikat agenta żądania certyfikatu (*Certificate Request Agent*); *Powiązane obiekty* obejmują – obok urzędów, które go wystawiają – szablony, które przyjmują podpis agenta | Krytyczna / Średnia | wyłącza |
| ESC3 | `EnrollmentAgentTargetTemplate` | ESC3 | szablon w wersji schematu 2 lub nowszej wymaga podpisu agenta rejestracji i pozwala na uwierzytelnianie klienta | Wysoka przy szablonie agenta dostępnym dla szerokiej grupy, Średnia przy innym, Niska bez szablonu agenta | wyłącza tylko zatwierdzenie menedżera |
| ESC4 | `TemplateWriteable` | ESC4 | podmiot spoza Tier 0 ma *Pełną kontrolę*, *Modyfikowanie uprawnień*, *Modyfikowanie właściciela*, *Zapis wszystkich właściwości* albo *Zapis właściwości istotnej dla bezpieczeństwa*, albo jest właścicielem szablonu; sam *Zapis innej właściwości* – Niska | Krytyczna / Średnia | nie chroni |
| ESC5 | `PkiObjectWriteable` | ESC5 | podmiot spoza Tier 0 może zmienić kontener PKI, magazyn NTAuth, obiekt OID, usługę rejestracji, obiekt urzędu w `CN=AIA` / `CN=CDP` / `CN=Certification Authorities` albo obiekt komputera urzędu (pełna kontrola, uprawnienia, właściciel, zapis atrybutów, tworzenie obiektów, reset hasła, odczyt hasła LAPS, delegowanie ograniczone na podstawie zasobów …) | Krytyczna / Średnia | nie chroni |
| ESC9 | `NoSecurityExtensionClientAuth` | ESC9 | szablon z `CT_FLAG_NO_SECURITY_EXTENSION` (`0x80000`) pozwala na uwierzytelnianie klienta | Wysoka / Średnia | obniża |
| ESC13 | `IssuancePolicyLinkedToGroup` | ESC13 | zasada wystawiania szablonu jest połączona z grupą (`msDS-OIDToGroupLink`), a certyfikat pozwala na uwierzytelnianie klienta | grupa Tier 0: Krytyczna, inna: Wysoka | obniża |
| ESC14 | `WeakExplicitMapping` | – | konto Tier 0 ma słabe jawne mapowanie certyfikatu (Wysoka); słabe mapowania pozostałych kont – jedna uwaga zbiorcza (Niska); podmiot spoza Tier 0 może zapisać `altSecurityIdentities` konta Tier 0 (Krytyczna / Średnia) | do Krytycznej | nie chroni |
| ESC15 | `SchemaV1EnrolleeSuppliesSubject` | ESC15/EKUwu | szablon w wersji schematu 1 z podmiotem dostarczanym przez wnioskującego (CVE-2024-49019) | Średnia | obniża |
| ESC17 | `EnrolleeSuppliesSubjectServerAuth` | ESC17 | podmiot dostarcza wnioskujący, a certyfikat pozwala na uwierzytelnianie serwera (także dowolny cel) | Wysoka / Średnia | wyłącza |

Kod ustalenia jest stały i nietłumaczony – trafia do eksportu CSV (kolumna `Code`) i pozwala porównywać wyniki między
przebiegami. Kolumna *MDI* (identyfikator oceny Microsoft Defender for Identity) zostaje pusta, dopóki nie potwierdzimy
prawdziwego identyfikatora.

### 5.3 Zastrzeżenia

| Zastrzeżenie | Znaczenie |
|---|---|
| *Szablon nie jest wystawiany (do wykorzystania dopiero po opublikowaniu)* | żaden urząd przedsiębiorstwa nie wystawia szablonu – uwaga o poziom niżej (nigdy poniżej poziomu *Informacja*) |
| *Zakłada, że urząd nadaje Użytkownikom uwierzytelnionym prawo „Rejestruj” (zabezpieczenia urzędu – etap P4)* | przy każdej uwadze rejestracji: P1 nie czyta zabezpieczeń samego urzędu |
| *Zależne od poprawki urzędu z listopada 2024 (sprawdzane w etapie P4)* | ESC15 – na zaktualizowanym urzędzie atak nie działa |
| *Wymagane zatwierdzenie menedżera certyfikatów lub podpis autoryzowany – poziom niżej* | ESC9, ESC13 i ESC15 przy szablonie z zatwierdzeniem |
| *pKIExtendedKeyUsage i zasady aplikacji różnią się* | reguła jest spełniona tylko na jednym z dwóch zestawów EKU (5.4) |
| *Prawo zapisu nierozpoznanego typu obiektu (liczone jako istotne dla bezpieczeństwa)* | ESC4: wpis ACE z typem obiektu, którego nie zna ani schemat, ani program – liczony ostrożnie jako groźny |
| *Brak szablonu agenta rejestracji dostępnego dla innych* | ESC3: szablon docelowy bez dostępnego szablonu agenta – poziom *Niska* |

Najpierw działa zatwierdzenie, potem szablon niewystawiany.

### 5.4 Różnice względem `certipy find -vulnerable` i Locksmith

Reguły porównano z Certipy (commit 3ae3442) i Locksmith (commit 8b555bd). Przegląd ma znaleźć wszystko, co oba narzędzia
znajdują dla ESC z listy LDAP; różnice są celowe:

- **Oba zestawy EKU.** Certipy i Locksmith czytają tylko `pKIExtendedKeyUsage`, a urząd wystawia szablony w wersji 2 i
  nowszych według zasad aplikacji (`msPKI-Certificate-Application-Policy`). Przegląd sprawdza oba zestawy i zgłasza
  uwagę, gdy reguła jest spełniona na którymkolwiek; gdy wynik zależy od zestawu, dodaje zastrzeżenie
  *pKIExtendedKeyUsage i zasady aplikacji różnią się*.
- **Zatwierdzenie przy ESC9, ESC13 i ESC15.** Certipy pomija te uwagi, gdy szablon wymaga zatwierdzenia menedżera
  certyfikatów albo podpisu autoryzowanego; Locksmith zgłasza je zawsze. Przegląd zgłasza je o poziom niżej z
  zastrzeżeniem. Przy ESC1, ESC2, ESC3 i ESC17 zatwierdzenie wyłącza uwagę – jak w obu narzędziach.
- **ESC4 stopniowane.** Certipy zgłasza tylko zapis wszystkich właściwości, Locksmith – każde prawo zapisu poza
  *Rejestruj* i *Autorejestracja*. Przegląd zgłasza oba przypadki: zapis wszystkich właściwości, właściwości istotnej
  dla bezpieczeństwa albo nierozpoznanego typu obiektu na pełnym poziomie, zapis innej właściwości jako *Niska*.
- **ESC15** według warunku Certipy (wersja schematu 1 i podmiot z żądania). Locksmith zgłasza każdy szablon w wersji 1,
  na który ktoś spoza Tier 0 może się zarejestrować – na urzędzie bez poprawki wnioskujący o dowolny taki szablon może
  też dopisać zasadę aplikacji *Certificate Request Agent* (EKUwu → ESC3). Przegląd zostaje przy warunku Certipy, żeby
  domyślny szablon *Użytkownik* nie dawał uwagi.
- **ESC17** według definicji Certipy: uwierzytelnianie serwera albo dowolny cel. Locksmith sprawdza tylko OID
  uwierzytelniania serwera (`1.3.6.1.5.5.7.3.1`).
- **ESC1 i ESC2 razem.** Szablon z podmiotem z żądania i dowolnym celem (albo bez EKU) daje obie uwagi (oraz ESC3 i
  ESC17) – jak w Certipy; lista grupuje je pod jednym szablonem.
- **Cele ESC3.** Szablon w wersji schematu 1 z uwierzytelnianiem klienta (np. domyślny *Użytkownik*) jest tylko
  powiązanym celem uwagi o szablonie agenta („ESC3 Target Template” w Certipy), nigdy osobną uwagą.
- **Bezpieczne podmioty** to zbiór `SafeUsers` z Locksmith uzupełniony o katalog grup Tier 0 programu i konta, które do
  nich należą – dlatego domyślne PKI Windows nie daje uwag.
- **Zabezpieczenia urzędu.** P1 nie czyta prawa *Rejestruj* na samym urzędzie (P4) i zakłada domyślne *Użytkownicy
  uwierzytelnieni*; mówi to zastrzeżenie przy każdej uwadze rejestracji.
- **Szablony niewystawiane** są zgłaszane o poziom niżej z zastrzeżeniem – stają się groźne dopiero po opublikowaniu.
- **Sama flaga `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME` (`0x10000`)** nie daje ESC1 – jak w Certipy i Locksmith.
- **ESC6, ESC7, ESC8, ESC10, ESC11 i ESC16** wymagają odczytu urzędu albo kontrolerów domeny – sprawdzi je etap P4;
  ESC12 jest poza zakresem programu. Tych uwag przegląd P1 nie pokazuje.
- **Podmioty z innej domeny lasu** są pokazywane bez liczby kont (*inna domena – liczba kont nieznana*), a liczba kont w
  komunikacie ma wtedy postać `N+`.

### 5.5 Nawigacja, kopiowanie i eksport

- *Otwórz szablon* przechodzi do dokumentu *Szablony certyfikatów*, *Otwórz urząd* – do przeglądu z zaznaczonym urzędem,
  a *Otwórz konto*, *Otwórz komputer* i *Otwórz obiekt* otwierają kartę właściwości (konta z uwag ESC14, obiekty i
  komputery urzędów z ESC5).
- *Kopiuj* kopiuje zaznaczoną uwagę do schowka.
- *Eksportuj do CSV…* i *Eksportuj do HTML…* zapisują **przefiltrowane** uwagi do pliku wskazanego przez operatora. CSV
  (z ochroną przed formułami) ma kolumny poziomu, ESC, obszaru, obiektu, tytułu, warunku, ścieżki ataku, zaleceń, kont i
  grup, zastrzeżeń, powiązanych obiektów, identyfikatorów Certipy, Locksmith i MDI oraz kodu ustalenia. Raport HTML jest
  samodzielną stroną z linią filtrów i stopką: *Raport zawiera nazwy obiektów Twojego katalogu. Przechowuj i udostępniaj
  go jak każdy inny dokument administracyjny.*
- **Nic nie jest naprawiane:** *Przegląd niczego nie zmienia: wprowadź zmianę w konsoli Szablony certyfikatów
  (certtmpl.msc) lub w uprawnieniach obiektu. Naprawy przez plan zmian pojawią się w etapie P3.*

## 6. Gotowość na silne mapowanie i NTAuth

Osobny dokument PKI (to nie jest *Sprawdź gotowość środowiska…* z etapu S). Odpowiada na pytanie, **kto nie zaloguje się
certyfikatem** przy pełnym wymuszaniu silnego mapowania certyfikatów (KB5014754) i bez urzędu w NTAuth. Wiersze mają
stan *OK*, *Ostrzeżenie*, *Problem* albo *Nie sprawdzono*, opis i *Co zrobić*; nad listą są liczniki stanów.

| Obszar | Co sprawdza | Stany |
|---|---|---|
| *Jawne mapowania certyfikatów (altSecurityIdentities)* | konta z mapowaniami z przebiegu liczenia kont (konta Tier 0 pierwsze) | słabe mapowanie włączonego konta Tier 0 – *Problem*; innego konta – *Ostrzeżenie*; tylko nieprawidłowe wartości – *Ostrzeżenie*; konta tylko z silnymi mapowaniami – jeden wiersz *OK*; nazwy Kerberos są pomijane |
| *Szablony uwierzytelniania a rozszerzenie SID* | wystawiane szablony z uwierzytelnianiem klienta w zasadach aplikacji albo w `pKIExtendedKeyUsage` | `CT_FLAG_NO_SECURITY_EXTENSION` – *Problem* (ESC9); podmiot dostarcza wnioskujący – *Ostrzeżenie* (urząd nie dodaje SID, logowanie wymaga silnego jawnego mapowania); pozostałe – jeden wiersz *OK* |
| *Urzędy przedsiębiorstwa a magazyn NTAuth* | każdy urząd przedsiębiorstwa | w NTAuth – *OK*; poza NTAuth, a wystawia szablony logowania – *Problem* (z listą szablonów); poza NTAuth bez szablonów logowania – *OK* |
| *Certyfikaty LDAPS kontrolerów domeny* | wynik sprawdzenia z rozdziału 3.7 | zła nazwa, brak uwierzytelniania serwera, nieważny, port 636 zamknięty albo nieudane uzgadnianie – *Problem*; wygasa w progu, brak łańcucha do urzędu w AD, brak nazwy domeny, port 3269 zamknięty, brak odpowiedzi, nierozpoznana nazwa – *Ostrzeżenie*; bez sprawdzenia – *Nie sprawdzono* |
| *Certyfikaty KDC (opublikowane na obiektach kontrolerów domeny)* | certyfikaty z `userCertificate` każdego kontrolera domeny | gotowy – *OK* (wygasa w progu – *Ostrzeżenie*); brak EKU uwierzytelniania KDC – *Ostrzeżenie*; wystawca spoza NTAuth albo wygasły – *Problem*; brak opublikowanego certyfikatu – *Nie sprawdzono*; kontrolery tylko do odczytu są oznaczone |
| *Zdarzenia KDC* | zdarzenia 39, 40, 41, 45 i 21 kontrolerów domeny | zawsze *Nie sprawdzono* – analiza zdarzeń przyjdzie w etapie P5; do tego czasu przefiltruj dziennik *System* każdego kontrolera domeny w czytniku dzienników zdarzeń ([DZIENNIKI-ZDARZEN.md](DZIENNIKI-ZDARZEN.md)) |

- **Sprawdź certyfikaty LDAPS** – raport łączy się z kontrolerami domeny **tylko po naciśnięciu tego przycisku** (to
  samo uzgadnianie TLS co w 3.7). Bez tego pokazuje wynik wcześniejszego sprawdzenia z przeglądu albo *Nie sprawdzono*.
- *Odczytaj ponownie* (F5) czyta obiekty PKI, kontrolery domeny i konta od nowa.
- Wiersze mają przyciski *Otwórz konto*, *Otwórz szablon* i *Otwórz urząd*. Każdy obszar pokazuje do 100 wierszy (*… i
  jeszcze N*); *Kopiuj* i *Eksportuj do CSV…* obejmują wszystkie.
- Progi i zegar działają w chwili pokazania – zmiana progów w ustawieniach przebudowuje raport bez czytania.

## 7. Karty właściwości kont

Trzy karty tylko do odczytu – nic na nich nie da się zmienić (edycja przyjdzie w P3). Karta jest widoczna tylko wtedy,
gdy schemat zna jej atrybut.

| Karta | Użytkownik (i `inetOrgPerson`) | Komputer | Kontakt |
|---|---|---|---|
| *Opublikowane certyfikaty* | po *Organizacja* | po *Stan* | tak |
| *Mapowania nazw* | tak | tak | – |
| *Poświadczenia kluczy* | tak | tak | – |

Karty są w kolejności z tabeli, przed kartą *Członek grupy*.

### 7.1 Opublikowane certyfikaty

Certyfikaty z `userCertificate`, potem z `userSMIMECertificate` (paczki PKCS #7, które zapisuje Outlook, są rozpakowane
do certyfikatów użytkownika). Karta czyta je dopiero po otwarciu; *Odśwież* czyta ponownie.

- Kolumny: *Wystawiony dla* (nazwa pospolita, bez niej UPN, adres e-mail albo pierwsza nazwa DNS z SAN), *Wystawiony
  przez*, *Przeznaczenie* (nazwy znanych EKU, inaczej OID; brak EKU = *Wszystkie (brak EKU)*), *Ważny do*, *Stan*
  (*Ważny*, *Wygasa (pozostało dni: N)* – według progu dla certyfikatów –, *Wygasł*, *Jeszcze nieważny*), *Szablon*,
  *Odcisk palca* (SHA-1) i *Atrybut*.
- *Szczegóły certyfikatu*: podmiot, wystawca, numer seryjny, ważny od i do, przeznaczenie z OID, szablon, główna nazwa
  użytkownika, adres e-mail i nazwy DNS z SAN, SID z adresu URL w SAN (silne mapowanie) i z rozszerzenia zabezpieczeń,
  klucz publiczny, algorytm podpisu, SHA-1 i SHA-256.
- Nazwę szablonu daje rozszerzenie szablonu w certyfikacie; nazwę wyświetlaną program odczytuje z
  `CN=Certificate Templates` na kontrolerze okna (jedno wyszukiwanie).
- **Kopiuj do pliku…** zapisuje certyfikat (DER, `.cer`) do pliku wskazanego przez operatora; **Kopiuj odcisk palca**.
- Wartość, która nie jest certyfikatem, ma wiersz *Nieczytelna wartość (to nie jest certyfikat)*.

### 7.2 Mapowania nazw

Jawne mapowania certyfikatów z `altSecurityIdentities` z oceną siły według KB5014754:

| Forma | Rodzaj | Siła |
|---|---|---|
| `X509:<I>…<SR>…` | *Wystawca i numer seryjny* (numer seryjny w odwróconej kolejności bajtów) | *Silne* |
| `X509:<SKI>…` | *Identyfikator klucza podmiotu* | *Silne* |
| `X509:<SHA1-PUKEY>…` | *Skrót SHA1 klucza publicznego* | *Silne* |
| `X509:<I>…<S>…` | *Wystawca i podmiot* | *Słabe* |
| `X509:<S>…` | *Sam podmiot* | *Słabe* |
| `X509:<RFC822>…` | *Adres e-mail (RFC822)* | *Słabe* |
| inne (np. `<SR>` bez `<I>`) | *Nieprawidłowe mapowanie* | *Nieprawidłowe* |

Lista *Certyfikaty X.509* ma kolumny *Mapowanie*, *Siła (KB5014754)*, *Wystawca* i *Podmiot lub identyfikator* (przy
formie z numerem seryjnym: *Numer seryjny …* w kolejności bajtów certyfikatu); osobna lista *Nazwy Kerberos* pokazuje
wartości `Kerberos:`. Nad listami ostrzeżenia: *Słabe mapowania: N. Przy pełnym wymuszaniu silnego powiązania
certyfikatów (KB5014754) kontroler domeny je odrzuca…* i *Nieprawidłowe wartości: N (KDC je pomija).* *Kopiuj* kopiuje
zaznaczoną wartość w zapisanej postaci. Karta ADUC *Mapowania nazw* tworzy tylko formy słabe.

### 7.3 Poświadczenia kluczy

Metadane kluczy z `msDS-KeyCredentialLink` (Windows Hello dla firm, klucze FIDO, klucze urządzeń): *Identyfikator
klucza* (Base64), *Identyfikator urządzenia*, *Źródło* (*Active Directory*, *Microsoft Entra ID*), *Zastosowanie* (np.
*Windows Hello dla firm (NGC)*, *Klucz zabezpieczeń FIDO*), *Utworzono*, *Ostatnie logowanie (przybliżone)* i *Uwagi*
(*Nieczytelny wpis*, *Skrót klucza się nie zgadza (wpis został zmieniony)*). Samego klucza karta nigdy nie pokazuje.
Ostrzeżenie: *Wpis, którego nikt nie rejestrował, może oznaczać atak Shadow Credentials: ten, kto go zapisał, może
zalogować się jako to konto.* Przyciski *Kopiuj identyfikator klucza* i *Kopiuj identyfikator urządzenia*. Historia
kluczy z usługi AdMng Watchdog przyjdzie w etapie P5.

## 8. Materiał klucza: Edytor atrybutów i listy

Te atrybuty **nigdy** nie są pokazywane, kopiowane, edytowane ani eksportowane: `userPKCS12`, `msPKIAccountCredentials`,
`msPKIDPAPIMasterKeys`, `msFVE-KeyPackage`, `msDS-ManagedPassword`, `supplementalCredentials`,
`msLAPS-EncryptedPassword`, `msLAPS-EncryptedPasswordHistory`, `msLAPS-EncryptedDSRMPassword`,
`msLAPS-EncryptedDSRMPasswordHistory` oraz każdy inny atrybut tajny (znany z nazwy albo poufny w schemacie), któremu
schemat nadaje składnię binarną.

- **Edytor atrybutów:** wartość to `••••••••` (albo *<nie ustawiono>*), wiersz jest tylko do odczytu z kłódką i powodem
  *Wartość poufna (materiał klucza): nie jest wyświetlana, kopiowana ani edytowana*; przyciski *Edytuj* i *Kopiuj* są
  nieaktywne, edytor wartości się nie otwiera i żadnej zmiany tego atrybutu nie da się przygotować.
- **Listy:** okno wyboru kolumn tych atrybutów nie proponuje, a kolumna z zapisanego widoku pokazuje maskę i tylko maskę
  zapisuje eksport CSV; Ctrl+C pomija taką kolumnę, jak każdą kolumnę z sekretem. W komórce listy maskę wyznaczają nazwy
  z listy powyżej (atrybutów binarnych okno wyboru kolumn i tak nie proponuje).
- **Sekrety czytelne dla operatora** – hasło LAPS w postaci jawnej, hasło odzyskiwania funkcji BitLocker i atrybuty
  poufne z tekstem – działają jak dotąd: są maskowane, odsłaniane świadomie i kopiowane bezpiecznym schowkiem. Czy
  maskować je tak samo jak klucze, jest pytaniem do zespołu.
- `userPKCS12` jest też atrybutem tajnym dla usługi AdMng Watchdog: konfiguracji, która chciałaby go obserwować, usługa
  nie przyjmie.

## 9. Ustawienia

*Narzędzia → Ustawienia → Ogólne*, sekcja **Certyfikaty**:

- **Próg ostrzeżenia o wygasaniu certyfikatów (dni)** – domyślnie 30: certyfikaty LDAPS i KDC kontrolerów domeny,
  agentów odzyskiwania kluczy i opublikowane na kontach.
- **Próg ostrzeżenia o wygasaniu certyfikatów urzędów (dni)** – domyślnie 180 (odnowienie urzędu trzeba zaplanować).

Obie wartości: 1–3650 dni. Lista CRL ma stałą regułę: *wygasa*, gdy zostało mniej niż 20 % jej ważności, co najmniej
jeden dzień. Progi działają w chwili pokazania: zmiana od razu przelicza stany w otwartych dokumentach, bez czytania
katalogu. Ustawienia nie zawierają żadnych danych PKI – tylko te dwa progi i przełącznik węzła w drzewie (*Widok*).

## 10. Prywatność i sieć

| Połączenie | Dokąd | Kiedy | Co jest przesyłane |
|---|---|---|---|
| LDAP (Kerberos, podpisywanie i szyfrowanie) | kontroler domeny okna | otwarcie dokumentu PKI, F5, otwarcie karty właściwości | zapytania o obiekty PKI, szablony, konta, grupy i kontrolery domeny |
| Uzgadnianie TLS, port 636 i 3269 | każdy kontroler domeny domeny okna | karta *Certyfikaty LDAPS* (3.7): pierwsze otwarcie, przycisk, F5 (w dowolnym dokumencie PKI okna) i zmiana kontrolera, gdy karta była otwarta; raport gotowości – tylko przycisk | tylko rekordy uzgadniania TLS: bez powiązania LDAP, bez certyfikatu klienta, bez danych aplikacji |
| HTTP i HTTPS | adresy AIA, CDP i OCSP z certyfikatów w AD | tylko po *Sprawdź adresy HTTP* (3.6) | żądanie GET bez poświadczeń i ciasteczek (proxy dostaje poświadczenia Windows) |
| Zdalne akcje (WinRM) | serwer urzędu | tylko po *Zdalne akcje…* (K1) | jak w [KOMPUTERY.md, rozdział 4](KOMPUTERY.md#4-zdalne-akcje-winrm-kerberos) |
| Urząd certyfikacji (DCOM, rejestr) | – | **nigdy w P1** (od P4) | – |

Żadne z tych połączeń nie pobiera niczego samo: budowanie łańcuchów (przegląd, sonda TLS, HTTPS) korzysta tylko z
certyfikatów urzędów odczytanych z AD i nie sięga do adresów AIA ani do magazynu certyfikatów Windows.

**Dane:**

- Odczyt PKI, liczenie kont (liczby, pierwsze 20 nazw, konta z mapowaniami), ustalenia, wyniki sondy TLS i wyniki HTTP
  są **tylko w pamięci okna połączenia** – wspólne dla jego dokumentów PKI, usuwane przy zmianie kontrolera domeny,
  rozłączeniu i zamknięciu okna; F5 czyta je od nowa. Karty właściwości trzymają swoje wartości tylko, dopóki okno
  właściwości jest otwarte.
- Na dysk trafiają wyłącznie **eksporty, o które poprosi operator**, do wskazanego przez niego pliku: CSV i HTML
  szablonów oraz przeglądu bezpieczeństwa, CSV raportu gotowości, `.cer` z karty *Opublikowane certyfikaty*. Raporty
  HTML mają stopkę o danych z katalogu.
- W ustawieniach są tylko dwa progi i przełącznik węzła. Dokumenty PKI nie zapisują nic własnego w dziennikach
  diagnostycznych, a typy P1 wypisują (`ToString()`) tylko rodzaje i liczby – nigdy podmiotu, odcisku palca, DN, adresu
  URL ani numeru seryjnego.
- P1 niczego nie wysyła do usługi AdMng Watchdog.

## 11. Ograniczenia i pilotaż

Ograniczenia tej wersji:

- **Bez połączenia z urzędem.** Prawo *Rejestruj* na samym urzędzie jest zakładane (*Użytkownicy uwierzytelnieni*), stan
  poprawki z listopada 2024 (ESC15) i konfiguracja urzędu (ESC6, ESC7, ESC8, ESC11, ESC16) są nieznane – P4. ESC12 poza
  zakresem.
- **Bez odczytu kontrolerów domeny.** Wymuszanie silnego mapowania na DC (`StrongCertificateBindingEnforcement`,
  `CertificateMappingMethods` Schannel, ESC10) i magazyn certyfikatów KDC – P4/K1; wskaźnik KDC opiera się tylko na
  certyfikatach opublikowanych w AD. Analiza zdarzeń KDC 39, 40, 41, 45 i 21 – P5.
- **OCSP** – tylko osiągalność, bez zapytania OCSP; z paczki PKCS #7 pod adresem AIA liczy się pierwszy certyfikat
  urzędu. Wiersz zdrowia urzędu dotyczy najnowszego certyfikatu z `cACertificate`.
- **Okno domeny podrzędnej:** komputery urzędów z domeny głównej lasu nie są rozpoznawane (*Serwer urzędu certyfikacji
  należy do innej domeny…*), więc ich uprawnienia na obiektach urzędu liczą się jako konto spoza Tier 0; podmioty z
  innych domen lasu nie mają liczby kont; sonda TLS sprawdza tylko kontrolery domeny okna.
- **Nic nie jest naprawiane ani wystawiane** – P3 (*Napraw…*, edycja szablonów, uprawnień, NTAuth, mapowań, kluczy) i P2
  (wystawianie). Historia kluczy z Watchdoga na karcie *Poświadczenia kluczy* – P5.
- Bity `0x80` i `0x200` flag rejestracji są pokazywane jako nieznane (nie ma ich w MS-CRTD), a sama flaga
  `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME` nie daje ESC1 – do ponownej oceny w P3/P4.
- *Otwórz* przy grupie otwiera jej kartę właściwości, a nie od razu kartę *Członkowie*. Raport gotowości nie ma eksportu
  HTML i pokazuje do 100 wierszy na obszar.
- Okna WPF i zrzuty ekranu 36–39 sprawdza tylko CI na Windows.

**Do potwierdzenia w pilotażu na Windows Server AD z AD CS** – fałszywy katalog testów (urzędy, szablony z polskimi
nazwami, celowo podatny szablon dla każdej reguły ESC i bezpieczny odpowiednik) i laboratorium Samba (bez AD CS: tylko
puste kontenery PKI i sonda LDAPS kontrolera `localhost`) tego nie rozstrzygną; scenariusze i wyniki: [PILOTAZ.md,
rozdział 3.5](PILOTAZ.md#35-usługi-certyfikatów-p1):

- stan zdrowia zgodny z `pkiview.msc` w domenie z co najmniej dwoma urzędami (urząd główny i wystawiający, listy
  podstawowe i różnicowe, lokalizacje `ldap:///` i `http://`);
- właściwości i uprawnienia każdego szablonu zgodne z `certtmpl.msc` (zdania flag, okresy, *Rejestruj* i
  *Autorejestracja* z odmowami i grupami zagnieżdżonymi), także na prawdziwych wartościach AD (ujemne maski `msPKI-*`,
  `msPKI-RA-Application-Policies` szablonów w wersji 4, `CN=OID` jako obiekt `msPKI-Enterprise-Oid`, wartości-zaślepki
  `0x00`, `crossCertificatePair`, nazwy urzędów zmienione przez `certutil`);
- na celowo podatnych szablonach przegląd znajduje wszystko, co `certipy find -vulnerable` i Locksmith (różnice jak w
  5.4), a domyślne PKI Windows – także z okna domeny podrzędnej – nie daje uwag ani delegowań w zakładce *Uprawnienia*;
- sonda TLS na kontrolerach Windows: prośba o certyfikat klienta w uzgadnianiu SChannel, kontroler bez certyfikatu LDAPS
  (*DC bez certyfikatu LDAPS*), port 3269 wykazu globalnego, RODC, łańcuch do urzędu z AD bez dostępu do internetu;
- wskaźnik KDC wobec certyfikatu, którego naprawdę używa KDC (szablony *Uwierzytelnianie Kerberos*, *Uwierzytelnianie
  kontrolera domeny*, *Kontroler domeny* w wersji 1);
- sprawdzenie adresów HTTP przez serwer proxy i z prawdziwymi adresami AIA, CDP i OCSP;
- karty kont wobec ADUC i `Get-ADUser`: certyfikaty S/MIME zapisane przez Outlooka, mapowania z karty ADUC (słabe),
  klucze Windows Hello dla firm i Entra ID (identyfikatory i daty);
- maskowanie `userPKCS12`, `msPKIAccountCredentials` i `msPKIDPAPIMasterKeys` w Edytorze atrybutów i w kolumnie
  zapisanego widoku na schemacie Windows Server;
- czas liczenia kont w dużej domenie (testy: 100 000 wygenerowanych kont w około sekundę).
