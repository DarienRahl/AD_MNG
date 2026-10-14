# Roadmapa AD Manager

Plan prac podzielony na fazy. Każda faza kończy się wersją, której da się realnie używać, i ma mierzalne kryteria
ukończenia. Kolejność wynika z ustaleń z zespołem:

- **technologia:** WPF .NET 10 + Fluent ([ADR-0001](adr/0001-wpf-net10-fluent.md));
- **interfejs:** PL + EN przełączany w locie;
- **priorytety spoza `dsa.msc`:** Kosz AD (przywracanie) oraz atrybuty poczty/Exchange;
- **podziały AD** (pracownicy, komputery, …) obsługiwane przez **konfigurowalne widoki**, a nie zaszyte na sztywno;
- **edycja zbiorcza** jest kluczowa, dlatego silnik zmian powstaje już w fazie 1, a pełne UI w fazie 2.

Szczegóły techniczne i uzasadnienia: [ARCHITECTURE.md](ARCHITECTURE.md), [RESEARCH.md](RESEARCH.md).

Legenda statusu: ✅ zrobione · 🟡 pierwsza wersja gotowa, wymaga dopracowania/weryfikacji na Windows AD · ⬜ planowane

---

## Przegląd

| Faza | Cel | Najważniejsze rezultaty | Status |
|---|---|---|---|
| 0 | Fundamenty | Rozwiązanie, CI, laboratorium Samba AD, rdzeń LDAP, kontrakty, prototyp wydajności | ✅ |
| 1 | MVP – codzienna praca zamiast ADUC | Logowanie (różne poświadczenia), drzewo, szybkie listy, wyszukiwanie, właściwości, akcje helpdesk, tworzenie/przenoszenie/usuwanie | 🟡 zaimplementowane, czeka na pilotaż na Windows AD |
| 2 | Edycja zbiorcza v1 (kluczowa) | Operacje zbiorcze z podglądem różnic, walidacją, raportem, eksportem PowerShell, cofaniem; import CSV (aktualizacja) | 🟡 zaimplementowane, czeka na testy obciążeniowe |
| 3 | Edycja zbiorcza v2 i zakładanie kont | Import CSV (tworzenie), szablony per OU, edytor arkuszowy, koszyk, kreatory (nieaktywne konta, zmiana nazwiska), pełna obsługa poczty | ⬜ (karta Adresy e-mail: ✅) |
| 4 | Kosz AD, widoki i raporty | Przeglądanie i przywracanie usuniętych obiektów, kreator zapytań, przestrzenie robocze dla działów, raporty, wiele domen | ⬜ (Kosz AD: 🟡, członkostwo zagnieżdżone i eksport widoków: ✅) |
| 5 | Helpdesk+ i bezpieczeństwo | Poświadczenia bez hasła w pamięci, ochrona przed usunięciem, karta Zabezpieczenia, stan blokady na wszystkich DC, historia zmian, LAPS, BitLocker, odświeżanie na żywo | ⬜ (odświeżanie list na żywo: ✅) |
| 6 | Delegacja i zarządzanie | Edytor ACL, kreator delegowania i przegląd delegacji, polityki ADMX, dziennik zdarzeń operatora, rzadkie karty ADUC | ⬜ |
| 7 | Wdrożenie i dodatki | Podpisany MSI, WDAC/AppLocker, zatwierdzanie „cztery oczy”, członkostwo czasowe (PAM) | ⬜ |

---

## Faza 0 – Fundamenty ✅

**Cel:** podjąć decyzje technologiczne, zweryfikować ryzyka LDAP i przygotować bazę inżynierską.

- ✅ Rozwiązanie `.slnx`: `AdMng.Core`, `AdMng.Directory`, `AdMng.Presentation` (ViewModele bez WPF), `AdMng.App` (WPF),
  projekty testów; centralne wersje pakietów; ostrzeżenia traktowane jak błędy.
- ✅ Rdzeń: DN (RFC 4514, polskie znaki), filtry LDAP z escapingiem (RFC 4515) i regułami AD (bit AND/OR, IN_CHAIN, ANR),
  flagi `userAccountControl`/`groupType`, konwersje FILETIME/GeneralizedTime/SID/GUID, model obiektu i schematu, mapowanie
  błędów AD (`0000052D`, `data 775`…).
- ✅ Kontrakty: sesja katalogu przypięta do DC, silnik zmian (plan → wykonanie → rollback), szablony, CSV, dziennik sesji.
- ✅ Laboratorium Samba AD DC z danymi „polskiej firmy” (OU wg działów i lokalizacji, konta zablokowane, delegacja helpdesk),
  uruchamiane lokalnie i w CI.
- ✅ Prototyp wydajności S.DS.P: bind 16 ms, pierwsza strona 100 obiektów 65 ms, VLV/ASQ/`allowedAttributesEffective` działają.
- ✅ CI (GitHub Actions): build i testy na Windows, publikacja `AdMng.exe`, testy integracyjne na Ubuntu z Samba DC.
- ✅ Dokumentacja: architektura, ADR, research.

**Kryteria ukończenia:** build bez ostrzeżeń na Windows i Linux; testy jednostkowe i integracyjne zielone w CI. ✅

**Do zrobienia na laboratorium Windows Server 2025** (przeniesione do fazy 1): pomiar listy 100 000 obiektów (paging vs VLV),
logowanie Kerberos z wymuszonym podpisywaniem i channel binding, test „żadnych danych na dysku” (skan profilu po sesji).

---

## Faza 1 – MVP: codzienna praca zamiast ADUC 🟡

**Cel:** helpdesk i administratorzy mogą wykonywać codzienne czynności szybciej niż w `dsa.msc`.

### Stan realizacji

- ✅ **Połączenie:** bieżące konto Windows lub inne poświadczenia, Kerberos z podpisywaniem i szyfrowaniem, LDAPS,
  StartTLS; wykrywanie domeny i kontrolerów (DsGetDcName, lokacja), wybór DC przy łączeniu, profile połączeń bez haseł,
  pasek tożsamości z ostrzeżeniem dla kont uprzywilejowanych, komunikaty błędów AD po polsku i angielsku.
- ✅ **Nawigacja i listy:** drzewo ładowane leniwie, „Funkcje zaawansowane”, listy bez limitu 2000 (strony
  strumieniowane, wirtualizacja), sortowanie, wybór kolumn z atrybutów schematu, nazwy z display specifiers (`CN=415`),
  znaczniki stanu, pasek adresu (DN lub ścieżka), Wstecz/Dalej/W górę, karty (Ctrl+T, Ctrl+Enter), panel szczegółów,
  „stan na godz.”, szybkie filtry po stronie serwera, **odświeżanie list na żywo** (powiadomienia LDAP o zmianach).
- ✅ **Wyszukiwanie:** ANR w trakcie pisania (Ctrl+E), Znajdź (Ctrl+F) z kategoriami i własnym filtrem LDAP, okno wyboru
  obiektów, paleta poleceń (Ctrl+K).
- ✅ **Właściwości:** wszystkie karty z tabeli poniżej, okna niemodalne, pola niedozwolone dla operatora tylko do odczytu
  (`allowedAttributesEffective`), członkostwo bezpośrednie **i pełne (zagnieżdżone)**, grupy z > 1500 członkami.
- ✅ **Akcje:** wszystkie z listy poniżej, a także nowy `inetOrgPerson`, historia poleceń PowerShell i dziennik sesji.
  Pojedyncze zmiany przechodzą przez ten sam silnik zmian (walidacja, dziennik, PowerShell), ale **cofanie jest na razie
  dostępne tylko w edytorze zbiorczym** – cofanie pojedynczej zmiany z dziennika sesji jest w planach.
- ✅ **Jakość:** PL/EN bez restartu, motyw jasny/ciemny/systemowy (Fluent), skróty jak w ADUC, testy UI (UI Automation)
  i test dymny opublikowanego `AdMng.exe` w CI.
- ⬜ **Brakuje:** rozpoznawania kontenerów po WKGUID, „Zmień kontroler domeny” w trakcie sesji, „Godzin logowania”,
  „Kopiuj użytkownika” (faza 3), VLV na listach bardzo dużych OU (sesja już go obsługuje), pól opartych o ACL (na razie
  wyświetlane jako tylko do odczytu – faza 5).
- 🟡 **Do weryfikacji na Windows Server:** pilotaż, pomiar 100 000 obiektów, Kerberos z wymuszonym channel binding,
  test „żadnych danych na dysku”, wysoki kontrast i czytnik ekranu.

### Połączenie i sesja
- Logowanie bieżącym kontem Windows lub innymi poświadczeniami; Kerberos z podpisywaniem i szyfrowaniem, LDAPS, StartTLS.
- Wykrywanie domeny komputera i kontrolerów (DsGetDcName, lokacja), wybór DC, „Zmień kontroler domeny”.
- Profile połączeń (bez haseł), pasek tożsamości z wyróżnieniem kont uprzywilejowanych, zrozumiałe komunikaty błędów PL/EN.

### Nawigacja i listy
- Drzewo domeny/OU ładowane leniwie, kontenery dobrze znane rozpoznawane po WKGUID, przełącznik „Funkcje zaawansowane”.
- Listy bez limitu 2000: wyniki strumieniowane stronami, wirtualizacja, sortowanie, wybór kolumn z atrybutów schematu,
  nazwy kolumn z polskich display specifiers (jak w polskim ADUC), znaczniki stanu (wyłączone, zablokowane, chronione).
- Pasek adresu (DN lub ścieżka), historia Wstecz/Dalej, karty, panel szczegółów z szybkimi akcjami, „stan na godz.”.
- Szybkie filtry (chipy) wykonywane po stronie serwera: wyłączone, zablokowane, nieaktywne > N dni, hasło nigdy nie wygasa,
  wymagana zmiana hasła, wygasłe/wygasające konta, puste grupy.

### Wyszukiwanie
- Globalne wyszukiwanie w trakcie pisania (ANR, Ctrl+E), Znajdź (Ctrl+F) z kategoriami jak w ADUC i własnym filtrem LDAP,
  okno wyboru obiektów (zamiennik „Wybierz użytkowników, komputery lub grupy”).

### Właściwości (okna niemodalne, pola niedozwolone dla operatora tylko do odczytu)

| Typ obiektu | Karty |
|---|---|
| Użytkownik | Ogólne, Adres (c + co + countryCode), Konto (flagi, wygasanie „koniec dnia”, UPN z sufiksami), Profil, Telefony, Organizacja, Członek grup (z grupą podstawową), Adresy e-mail, Obiekt, Edytor atrybutów |
| Komputer | Ogólne (rola), System operacyjny, Członek grup, Delegowanie (podgląd), Lokalizacja, Zarządzany przez, Obiekt, Edytor atrybutów |
| Grupa | Ogólne (zakres/typ z regułami konwersji), Członkowie (> 1500 przez ranged retrieval), Członek grup, Zarządzany przez, Obiekt, Edytor atrybutów |
| Kontakt | Ogólne, Adres, Telefony, Organizacja, Członek grup, Adresy e-mail, Obiekt, Edytor atrybutów |
| Jednostka organizacyjna | Ogólne, Zarządzany przez, Obiekt, Edytor atrybutów |

### Akcje
- Reset hasła (wpisane lub wygenerowane, wymuszenie zmiany, odblokowanie), włącz/wyłącz, odblokuj, dodaj do grupy,
  przenieś (okno i przeciągnij-upuść), zmień nazwę (F2), usuń (ostrzeżenia, usuwanie poddrzewa za potwierdzeniem),
  nowy użytkownik/grupa/OU/komputer/kontakt, eksport listy do CSV, kopiuj DN.
- Każda zmiana (także pojedyncza) przechodzi przez silnik zmian: walidacja, dziennik sesji, możliwość cofnięcia.

### Jakość
- PL/EN bez restartu, motyw jasny/ciemny, skróty jak w ADUC (F5, Del, F2, Alt+Enter, Ctrl+F) + Ctrl+K, dostępność
  (UI Automation, klawiatura, wysoki kontrast).

**Kryteria ukończenia**
- Pilotaż: ~5 osób z helpdesku pracuje równolegle z ADUC przez 2 tygodnie.
- Wartości wyświetlane identycznie jak w ADUC na laboratorium Windows (kraj, godziny logowania, wygasanie konta, zakres grupy).
- Konto helpdesk widzi poprawnie zablokowane pola i dostaje zrozumiałe komunikaty (odmowa dostępu, polityka haseł).
- Pierwsza strona dużego OU < 300 ms w sieci LAN, brak „zamrożeń” UI.
- Test „żadnych danych katalogu na dysku” zaliczony.

---

## Faza 2 – Edycja zbiorcza v1 (kluczowa) 🟡

**Cel:** bezpieczne, przewidywalne i odwracalne zmiany na setkach i tysiącach obiektów.

### Stan realizacji

- ✅ Edytor zbiorczy (Ctrl+Shift+B) ze wszystkimi operacjami z tabeli poniżej, cały potok 1–6, „ponów nieudane”,
  cofanie, eksport podglądu i raportu do CSV, skrypt PowerShell, import CSV aktualizujący obiekty (z wykrywaniem
  separatora i kodowania, także cp1250 z polskiego Excela, i ochroną przed formułami w eksporcie).
- ✅ Wykonanie z ograniczoną równoległością, ponawianiem błędów przejściowych i współbieżnością optymistyczną (atomowe
  „usuń starą wartość + dodaj nową” – zmiana wprowadzona przez kogoś innego po podglądzie kończy się konfliktem, a nie
  nadpisaniem). Paczki ponad próg z ustawień albo z usuwaniem, przenoszeniem lub resetem haseł wymagają wpisania liczby
  obiektów.
- ✅ Hasła wygenerowane przy resecie istnieją tylko w pamięci; eksport wyników z hasłami wyłącznie na wyraźne polecenie,
  z ostrzeżeniem przed nadpisaniem niezapisanych haseł.
- 🟡 Kryteria ukończenia: konflikty, cofanie i plan PowerShell są pokryte testami automatycznymi (także na laboratorium
  Samba); paczka 5000 obiektów i porównanie z `-WhatIf` na Windows Server – do zrobienia.

### Źródła zaznaczenia
- Zaznaczenie wielu obiektów na liście, wynik wyszukiwania, szybki filtr lub zapisany widok, plik CSV.

### Operacje (użytkownicy, komputery, grupy, kontakty)

| Operacja | Szczegóły |
|---|---|
| Ustaw atrybut | Wartość stała lub szablon: `{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}@firma.pl` |
| Wyczyść atrybut | Z kontrolą atrybutów obowiązkowych |
| Dodaj / usuń wartości | Atrybuty wielowartościowe (np. `proxyAddresses`, `otherTelephone`) – idempotentnie |
| Znajdź i zamień | Tekst lub wyrażenie regularne |
| Konto | Włącz/wyłącz, odblokuj, flagi (hasło nigdy nie wygasa, karta inteligentna…), wymuszenie zmiany hasła, data wygaśnięcia |
| Reset haseł | Unikalne hasła generowane kryptograficznie lub jedno hasło; eksport wyników tylko za wyraźną zgodą |
| Przenieś / zmień nazwę | Do wskazanego OU; nowa nazwa z szablonu |
| Grupy | Dodaj do grup / usuń z grup (wiele-do-wielu) |
| Usuń | Osobno, z potwierdzeniem przez wpisanie liczby obiektów |

### Potok
1. **Plan** – świeży odczyt bieżących wartości z przypiętego DC, renderowanie szablonów, pominięcie zmian bez efektu.
2. **Walidacja** – schemat (typ, jedno-/wielowartościowość, długość), uprawnienia operatora (`allowedAttributesEffective`),
   unikalność (`sAMAccountName`, UPN, mail – w AD i w obrębie paczki), obiekty chronione (adminCount=1, krytyczne systemowe,
   konta wbudowane).
3. **Podgląd różnic** – „przed → po” dla każdego obiektu i atrybutu, filtry (zmiany / ostrzeżenia / błędy / bez zmian),
   wykluczanie pojedynczych obiektów, eksport podglądu do CSV, **równoważny skrypt PowerShell**.
4. **Wykonanie** – w tle, z postępem i anulowaniem; ograniczona równoległość; ponawianie błędów przejściowych;
   **współbieżność optymistyczna** (zmiana wprowadzona przez kogoś innego po podglądzie nie zostanie nadpisana).
5. **Raport** – status każdego obiektu, kody błędów AD po polsku, „ponów nieudane”, eksport CSV.
6. **Cofnięcie** – plan odwrotny z zapamiętanych (w pamięci) starych wartości, przez ten sam podgląd.

### Import CSV (aktualizacja istniejących obiektów)
- Automatyczne wykrycie separatora (`;` z polskiego Excela, `,`, tab), UTF-8/BOM, klucz: `sAMAccountName`, UPN, mail,
  `employeeID`, DN lub `objectGUID`; mapowanie kolumn na atrybuty (automatyczne po nazwie LDAP lub polskiej etykiecie);
  puste komórki: pomiń lub wyczyść; wiersze nieznalezione/niejednoznaczne jako błędy w podglądzie.

**Kryteria ukończenia**
- Paczka 5000 obiektów na laboratorium bez przeciążenia DC (limit współbieżności, brak alarmów opóźnień LDAP).
- Wstrzyknięte równoległe zmiany wykrywane jako konflikty.
- Cofnięcie przywraca 100% odwracalnych zmian (testy automatyczne).
- Wygenerowany PowerShell z `-WhatIf` daje te same zmiany co plan.

---

## Faza 3 – Edycja zbiorcza v2 i zakładanie kont ⬜

- Import CSV **tworzący** obiekty (użytkownicy, kontakty, grupy, komputery) z szablonem pliku do pobrania; obsługa cp1250.
- Szablony nowych obiektów przypisane do OU (np. *Pracownicy/IT*: UPN, mail, grupy startowe, atrybuty działu), polityka
  nazewnictwa z transliteracją polskich znaków i licznikami unikalności (`jan.kowalski2`).
- „Kopiuj użytkownika” (atrybuty kopiowalne wg schematu + członkostwa).
- Edytor arkuszowy (jak Excel) nad wynikami listy: wklejanie z Excela, wypełnianie w dół, zmiany trafiają do podglądu.
- Koszyk obiektów z różnych OU, „wklej listę” (rozpoznawanie po sAMAccountName/UPN/mail/employeeID).
- Kreatory: nieaktywne konta i komputery (wyłącz → opisz → przenieś → usuń później), zmiana nazwiska (sn, displayName, cn,
  UPN, mail, główny adres SMTP), migracja sufiksu UPN, kopiowanie członkostw A → B.
- **Poczta / Exchange (priorytet):** pełna obsługa `proxyAddresses` (dokładnie jeden główny `SMTP:`, unikalność w całym lesie
  przez GC), `mailNickname`, `targetAddress`, `extensionAttribute1–15`, ukrycie w GAL; operacje zbiorcze na adresach
  (np. dodanie aliasu w nowej domenie dla całego działu).
- Synchronizacja członkostwa grupy z zapytania lub CSV (różnicowo, nigdy przez nadpisanie `member`).

## Faza 4 – Kosz AD, widoki i raporty ⬜ (Kosz AD: 🟡)

- 🟡 **Kosz AD (priorytet) – pierwsza wersja gotowa.** Okno *Kosz Active Directory* (menu Narzędzia, paleta poleceń,
  Ctrl+Shift+R): lista usuniętych obiektów czytana na żywo z `CN=Deleted Objects` (kontrolka Show Deleted, wyniki
  strumieniowane, z możliwością przerwania), filtry po stronie serwera (nazwa, typ obiektu, data usunięcia, kontener,
  z którego usunięto obiekt), kolumny: nazwa, typ, pierwotna lokalizacja (ścieżka kanoniczna – także przez usunięte
  kontenery nadrzędne), czas usunięcia. Przywracanie do pierwotnego lub wskazanego miejsca przez silnik zmian: podgląd
  planu z uwagami i potwierdzeniem, dziennik sesji, historia PowerShell (`Restore-ADObject`), odświeżenie list i drzewa.
  **Całe poddrzewo OU wraca jednym planem w kolejności rodzic → dzieci** (czego nie potrafi ADAC): dzieci trafiają do
  przywróconego rodzica, a wykonanie dziecka czeka na rodzica. Plan wykrywa konflikty nazw w miejscu docelowym (odczyt na
  żywo), obiekty, których kontener pozostaje usunięty, i obiekty już ostatecznie usunięte. Okno pokazuje stan Kosza i czas
  przechowywania usuniętych obiektów (`msDS-deletedObjectLifetime`/`tombstoneLifetime`); gdy Kosz jest wyłączony,
  ostrzega i pozwala odtworzyć obiekt z tombstone (bez większości atrybutów) po dodatkowym potwierdzeniu. Sprawdzone
  testami jednostkowymi, testami UI i na laboratorium Samba (odtworzenie poddrzewa z tombstone).
  Do zrobienia: weryfikacja na Windows Server z włączonym Koszem i z uprawnieniami delegowanymi do `CN=Deleted Objects`,
  ostrzeżenie w oknie usuwania, gdy Kosz jest wyłączony, przywracanie w partycjach Configuration i stref DNS.
- 🟡 **Widoki dla działów:** ✅ przypinanie OU, zapisane widoki (zapytanie LDAP + kontener + kolumny), gotowe widoki
  wbudowane i eksport/import widoków dla zespołu; ⬜ przestrzenie robocze (np. *Pracownicy*, *Komputery*,
  *Kontrahenci*) łączące widoki z szablonami, osobne dla helpdesku i administratorów.
- Kreator zapytań (warunki ↔ LDAP), zapisane zapytania jako foldery w drzewie, import zapytań zapisanych w ADUC (XML).
- Raporty na żywo: bezpieczeństwo (PASSWD_NOTREQD, brak preautentykacji Kerberos, delegowanie nieograniczone, adminCount,
  konta z SPN), inwentaryzacja (systemy operacyjne, puste i duże grupy, liczności OU); każdy wynik można przekazać do edycji zbiorczej.
- ✅ Członkostwo bezpośrednie vs pełne (zagnieżdżone) na kartach *Członek grup* i *Członkowie*
  (`LDAP_MATCHING_RULE_IN_CHAIN`); ⬜ raport efektywnego członkostwa wielu użytkowników.
- Wiele domen i lasów, wyszukiwanie w katalogu globalnym, paleta poleceń z prefiksami, porównanie dwóch obiektów.

## Faza 5 – Helpdesk+ i bezpieczeństwo ⬜

- Alternatywne poświadczenia bez trzymania hasła w pamięci (token `LogonUser` NEW_CREDENTIALS + impersonacja, weryfikacja
  WhoAmI), Kerberos-only z opcjonalnym NTLM, obsługa Protected Users, blokada sesji po bezczynności.
- Pola oparte o ACL: „Chroń obiekt przed przypadkowym usunięciem”, „Użytkownik nie może zmienić hasła”, „Menedżer może
  aktualizować listę członków”; karta Zabezpieczenia (podgląd), znaczniki Tier 0.
- Ostatnie logowanie i stan blokady na **wszystkich** DC (zamiennik LockoutStatus.exe).
- Karta Historia: kiedy i na którym DC zmieniono każdy atrybut (`msDS-ReplAttributeMetaData`) i członka grupy.
- ✅ Odświeżanie list na żywo przez powiadomienia LDAP o zmianach (osobne połączenie do tego samego DC, odświeżane są
  tylko zmienione obiekty); ⬜ zapasowe odpytywanie USN, gdy DC odmawia powiadomień.
- Windows LAPS (odczyt/odszyfrowanie dla uprawnionych, „wygaś teraz”), klucze odzyskiwania BitLocker, TPM,
  polityki haseł szczegółowych (FGPP) i wynikowa polityka użytkownika.

## Faza 6 – Delegacja i zarządzanie ⬜

- Edytor ACL, kreator „Deleguj kontrolę” i **przegląd delegacji** (niestandardowe ACE per OU) z cofaniem.
- Wstępna kontrola uprawnień rozszerzonych (AuthZ) przed operacją.
- Szablony ADMX (progi potwierdzeń, limity eksportu, blokada bezczynności, wymaganie PAW), kanał Dziennika zdarzeń
  operatora (metadane zmian, bez wartości), wsparcie korelacji z audytem DC (5136/5137/5141).
- Rzadkie karty ADUC: Dial-in, Środowisko/Sesje/Zdalne sterowanie (RDS), Mapowania nazw, Opublikowane certyfikaty,
  zasady replikacji haseł RODC; wzorce operacji (FSMO), konta gMSA/dMSA.

## Faza 7 – Wdrożenie i dodatki ⬜

- Podpisany instalator MSI (certyfikat z firmowego AD CS), reguły WDAC/AppLocker, dystrybucja przez Intune/ConfigMgr/GPO.
- Zatwierdzanie „cztery oczy”: operator A eksportuje podpisany plan zmian, operator B weryfikuje go na żywo i wykonuje.
- Członkostwo czasowe (PAM/TTL), polityki i silosy uwierzytelniania (podgląd), logowanie kartą inteligentną.

---

## Znane ograniczenia

Stan na październik 2026. Funkcje z kolejnych faz opisano wyżej; tu są braki i uproszczenia w tym, co już działa.

- **Nie sprawdzone na Windows Server AD.** Testy integracyjne działają na kontrolerze Samba 4.19; pilotaż, pomiary na
  dużych katalogach, Kerberos z wymuszonym channel binding, Kosz AD z delegowanymi uprawnieniami i test „żadnych danych
  na dysku” są przed nami (fazy 1–2, 4).
- **Jeden kontroler przez całą sesję** – zmiana DC wymaga rozłączenia i ponownego połączenia (brak okna „Zmień
  kontroler domeny”).
- **Kontenery dobrze znane** (`Users`, `Computers`, …) nie są rozpoznawane po WKGUID – drzewo pokazuje je jak zwykłe
  kontenery, a obiekty są zawsze tworzone w bieżącym kontenerze, nie w domyślnym z `wellKnownObjects`.
- **Bardzo duże OU** są wczytywane strumieniowo w całości (bez VLV) – lista działa płynnie, ale zajmuje pamięć
  proporcjonalną do liczby obiektów.
- **Pola oparte o ACL** („Chroń obiekt przed przypadkowym usunięciem”, „Użytkownik nie może zmienić hasła”, „Menedżer
  może aktualizować listę członków”) są pokazywane tylko do odczytu; brak karty Zabezpieczenia i „Godzin logowania”.
- **Cofanie** działa w edytorze zbiorczym (cała paczka); pojedynczych zmian z kart właściwości i menu nie da się jeszcze
  cofnąć z dziennika sesji – można je odwrócić ręcznie, a dziennik pokazuje stare wartości.
- **LAPS i BitLocker** – brak kart; hasła LAPS są traktowane jako atrybuty poufne (maskowane w dzienniku).
- **Zakładanie kont** tylko formularzem (bez szablonów per OU, CSV i „Kopiuj użytkownika”) – faza 3.
- **Filtr „Puste grupy”** sprawdza atrybut `member` i pomija domyślne grupy podstawowe domeny (Domain Users, Domain
  Guests, Domain Computers, Domain Controllers, Read-only Domain Controllers – po SID domeny); własna grupa, która jest
  grupą podstawową (`primaryGroupID`) części kont i nie ma innych członków, nadal może być pokazywana jako pusta.

---

## Pokrycie według typów obiektów

| Funkcja | Użytkownicy | Komputery | Grupy | Kontakty | OU |
|---|---|---|---|---|---|
| Lista, wyszukiwanie, właściwości | F1 | F1 | F1 | F1 | F1 |
| Edycja pól, Edytor atrybutów | F1 | F1 | F1 | F1 | F1 |
| Tworzenie | F1 (formularz), F3 (szablony, CSV) | F1, F3 | F1, F3 | F1, F3 | F1 |
| Akcje konta (reset, odblokuj, włącz/wyłącz) | F1 | F1 (włącz/wyłącz) | – | – | – |
| Edycja zbiorcza atrybutów | F2 | F2 | F2 | F2 | F2 |
| Członkostwo zbiorczo | F2 | F2 | F2 (zagnieżdżanie) | F2 | – |
| Import CSV (aktualizacja / tworzenie) | F2 / F3 | F2 / F3 | F2 / F3 | F2 / F3 | – |
| Poczta / Exchange | F1 (karta), F3 (pełna) | – | F3 | F1, F3 | – |
| Kosz AD | F4 | F4 | F4 | F4 | F4 (poddrzewo) |
| LAPS, BitLocker | – | F5 | – | – | – |
| Delegacja, ACL | F5–F6 | F5–F6 | F5–F6 | F5–F6 | F6 |

---

## Decyzje do podjęcia z zespołem

Pełna lista pytań: [RESEARCH.md](RESEARCH.md#11-pytania-otwarte-do-zespołu). Najważniejsze dla kolejnych faz:

1. **Skala i topologia:** liczba domen/lasów i zaufań, liczba obiektów, największe OU i grupa (wpływa na priorytet VLV i
   wielu domen).
2. **Granica „brak danych lokalnych”:** czy dopuszczalne są: zapisane widoki z DN (zakładamy, że tak), eksporty CSV na
   żądanie (zakładamy, że tak), dziennik zmian ze starymi/nowymi wartościami w pliku lub w Dzienniku zdarzeń (domyślnie
   tylko w pamięci), eksport dziennika cofania.
3. **Konfiguracja bezpieczeństwa domen:** LDAPS na DC, wymuszone podpisywanie/channel binding, ograniczenia NTLM, model
   warstwowy/PAW, Protected Users.
4. **Edycja zbiorcza:** najczęstsze operacje i typowy rozmiar paczki, progi potwierdzeń, potrzeba zatwierdzania przez drugą osobę.
5. **Konwencje nazewnicze:** wzorzec `sAMAccountName`/UPN/mail, sufiksy UPN, zasady dla nazwisk dwuczłonowych i duplikatów,
   różnice między OU.
6. **Źródło danych CSV** (system kadrowy?): format, kodowanie, unikalność `employeeID`.
7. **Exchange:** lokalny, hybrydowy czy tylko Entra/Exchange Online; czy zmiany adresów mają iść przez AD.
8. **Używane rzadkie karty ADUC** (RDS, Dial-in, certyfikaty) – decyduje o zakresie fazy 6.
