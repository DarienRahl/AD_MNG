# ADR-0008: Watchdog przechowuje dane w osobnym folderze (wyjątek od ADR-0003)

- Status: przyjęta
- Data: 2026-10-14 (uzupełniona tego samego dnia po przeglądach: operacyjnym, bezpieczeństwa i implementacyjnym)
- Zmienia: [ADR-0003](0003-brak-lokalnych-danych.md) (tylko w zakresie usługi AdMng Watchdog) oraz zasadę 2 w
  `CLAUDE.md` (tylko sekrety kanałów Watchdoga)
- **Zmieniona przez [ADR-0009](0009-watchdog-na-linuksie.md)** (2026-10-20; obowiązuje od wydania 0.3.0, w którym
  usługa działa wyłącznie na Linuksie): warunki tej decyzji obowiązują nadal, ale realizujemy je inaczej – folder
  danych `/var/lib/admng-watchdog` (0700, sprawdzany przy każdym starcie) zamiast ACL NTFS, drugie krótkotrwałe miejsce
  na pliki wyjściowe OpenWEC (`/var/lib/openwec/out`), migawka ustawień GPO bez sekretów, rekordy logowań 48 godzin,
  retencja zdarzeń 1095 dni z progiem `MinEventsDays`, klucze sekretów chronione poświadczeniem systemd (TPM2 + klucz
  hosta albo klucz hosta) zamiast DPAPI-NG, keytab konta usługi jako nowy wyjątek od zasady 2, dzienniki w journald.
  Szczegóły: ADR-0009, sekcja „Zmiana ADR-0008”. Opis poniżej dotyczy usługi Windows z wydania 0.2.0.
- **Uzupełniona (2026-10-26, U1b):** kolejka wysyłek usługi przenosi także wiadomości do użytkowników zgłoszone przez
  AD Manager, a usługa prowadzi ich audyt bez treści – sekcja „Uzupełnienie: wiadomości do użytkowników (U1b)” na końcu.

## Kontekst

[ADR-0003](0003-brak-lokalnych-danych.md) zakazuje przechowywania lokalnych kopii danych katalogu: AD Manager czyta
wszystko z DC na żądanie i trzyma dane tylko w pamięci, dopóki są wyświetlane.

Użytkownik zamówił watchdog, który całą dobę raportuje zmiany w AD (nowe konta i komputery, zmiany parametrów kont,
blokady, zbliżające się terminy ważności, zmiany uprawnień), z historią, konfigurowalnymi regułami, wysyłką SMTP,
raportem dziennym i kanałami Teams / webhook. Taka funkcja nie może działać bez stanu:

- zmianę „stara → nowa wartość” da się wyliczyć tylko wtedy, gdy poprzednia wartość jest gdzieś zapisana – AD
  przechowuje wyłącznie stan bieżący (a `whenChanged`, metadane replikacji i DirSync nie podają poprzednich wartości);
- znacznik postępu (`highestCommittedUSN` dla każdego DC), identyfikator DC (`invocationId`), kolejka powiadomień z
  ponawianiem, deduplikacja ostrzeżeń o wygasaniu kont i raport dzienny muszą przetrwać restart;
- przypisanie wykonawcy z dzienników zabezpieczeń DC często przychodzi później niż wykrycie zmiany, więc zdarzenia
  muszą czekać na uzupełnienie;
- operator chce przeglądać historię zdarzeń, a nie tylko otrzymywać powiadomienia;
- hasła i adresy kanałów powiadomień (SMTP, Teams, webhook) muszą przetrwać restart usługi działającej bez operatora.

Użytkownik świadomie zmienił decyzję: „Zmieniamy jednak podjęcie odnośnie gromadzenia danych, niech będzie do tego
osobny folder z tym”, oraz zgodził się na osobne konto usługi działające 24/7.

Rozważone opcje:

1. **Brak stanu** (tylko porównanie z poprzednim odczytem w pamięci) – utrata historii i wykrywania po każdym
   restarcie, brak wartości „przed” po awarii, brak ponawiania wysyłek. Odrzucona.
2. **Przechowywanie w aplikacji AD_MNG na stacjach administratorów** – dane osobowe na wielu stacjach, brak pracy
   24/7. Odrzucona; sprzeczna z ADR-0003 bez korzyści.
3. **Wyłącznie zewnętrzny SIEM** – wymaga systemu, którego klient może nie mieć, i nie daje zakładki w AD_MNG.
   Pozostaje możliwa jako kanał webhook.
4. **Osobna usługa z własnym folderem danych na serwerze** – wybrana.

## Decyzja

Rozluźniamy ADR-0003 **wyłącznie** dla hosta usługi AdMng Watchdog (projekt `src/AdMng.Watchdog.Service` i
implementacje magazynów, które on uruchamia). Biblioteki `AdMng.Watchdog.Client`, `AdMng.Watchdog.Contracts`,
`AdMng.Watchdog.Core` (poza hostem usługi) oraz AD_MNG (Presentation, App) pozostają pod pełnym zakazem ADR-0003.
Usługa może przechowywać dane katalogu w swoim dedykowanym folderze danych na następujących warunkach:

1. **Jedno miejsce.** Dane katalogu są zapisywane tylko w folderze danych usługi (domyślnie
   `%ProgramData%\AdMng\Watchdog\data`, konfigurowalny): baza SQLite `watchdog.db` (z plikami WAL), kopie zapasowe w
   `backup\`, pierścień kluczy w `keys\`. Nigdzie indziej: nie w plikach tymczasowych (SQLite działa z
   `temp_store = MEMORY`, więc sortowania i kopie nie trafiają do `%TEMP%` profilu gMSA), rejestrze, zrzutach awaryjnych
   (WER wyłączony dla usługi), dzienniku zdarzeń Windows, konsoli ani w dziennikach diagnostycznych – te nadal nie
   zawierają wartości z katalogu ani treści wyjątków.
2. **Tylko to, co potrzebne.** Migawka zawiera wyłącznie obserwowane atrybuty (lista w specyfikacji; dodatkowe
   atrybuty administrator może dopisać tylko pojedynczo, po sprawdzeniu w schemacie – nigdy `*`, atrybuty poufne,
   tajne ani konstruowane), członkostwa grup i identyfikatory (GUID, SID, DN); zdarzenia zawierają zmiany
   „przed → po”, atrybucję i stan powiadomień; dzienniki DC – tylko znormalizowane pola potrzebne do dopasowania
   (wartości atrybutów wyłącznie dla obserwowanych, nietajnych atrybutów; rekordy spoza zakresu są odrzucane).
3. **Nigdy sekrety katalogu.** Hasła i ich skróty, LAPS, BitLocker, TPM, hasła gMSA, sekrety LSA (`currentValue`,
   `priorValue`), atrybuty zaufania oraz wszystkie atrybuty oznaczone w schemacie jako poufne
   (`SecretAttributes.IsSecret`) nie są odczytywane ani zapisywane; zmiana takiego atrybutu jest odnotowywana jako
   „zmieniono (wartość ukryta)”, a wartości w zdarzeniach 5136 są maskowane przed zapisem.
4. **Retencja i usuwanie.** Każdy rodzaj danych ma okres przechowywania z wartością domyślną (zdarzenia 365 dni,
   zdarzenia z dzienników DC 30 dni, migawki obiektów usuniętych 180 dni, kopie zapasowe 7 dni, kopie sprzed
   migracji – najnowsza + 30 dni) oraz limit rozmiaru bazy; treść wysłanego powiadomienia jest usuwana zaraz po
   wysłaniu (zostają metadane). Retencja jest egzekwowana codziennie, a usuwane rekordy są nadpisywane
   (`secure_delete`).
5. **Dostęp.** Folder danych ma wyłączone dziedziczenie i uprawnienia wyłącznie dla SYSTEM, lokalnych
   Administratorów i konta usługi (gMSA); usługa **wymusza** to przy każdym starcie (przy szerszych uprawnieniach nie
   uruchamia się). Ustawienia startowe (role, uwierzytelnianie) leżą w osobnym folderze `config\`, którego konto usługi
   nie może zmieniać. Dane są udostępniane wyłącznie przez API usługi po HTTPS (TLS 1.2/1.3) z uwierzytelnianiem
   **wyłącznie Kerberos** (w obu trybach serwera), dla członków skonfigurowanych grup AD; rola Viewer dostaje dane na
   poziomie „standardowym”, a odczyt pojedynczych zdarzeń jest rejestrowany. Serwer usługi i jej konto są
   administrowane jako zasoby Tier 0.
6. **Sekrety kanałów** (hasło SMTP, sekret OAuth, adresy Teams Workflows i webhooków, klucze podpisów) są szyfrowane
   (ASP.NET Core Data Protection, klucze chronione DPAPI-NG dla SID gMSA), **powiązane z miejscem docelowym** (serwer,
   konto, dzierżawa – zmiana miejsca wymaga ponownego wpisania sekretu), zapisywane w jednej transakcji z konfiguracją
   i jej audytem, i nigdy nie są zwracane przez API. To jedyny wyjątek od zasady 2 w `CLAUDE.md`.
7. **Dane wychodzące** (e-mail, Teams, webhook) są kopią poza folderem – dlatego każdy kanał ma poziom szczegółowości
   (minimalny / standardowy / pełny), a sekrety nie są wysyłane nigdy.
8. **AD_MNG bez zmian.** Aplikacja WPF nadal niczego nie zapisuje lokalnie: zakładka Watchdog pobiera dane z usługi
   na żądanie i trzyma je w pamięci; na dysku zapisuje tylko adres usługi, układ i nazwane filtry (jak zapisane
   widoki). Eksport CSV zdarzeń odbywa się wyłącznie na wyraźne polecenie operatora (jak w ADR-0003).
9. **Ochrona przed wyciszeniem.** Każda zmiana konfiguracji jest audytowana i zgłaszana zdarzeniem (także dotychczasowym
   odbiorcom); wyłączenie krytycznych detekcji przez API wymaga lokalnej zgody w ustawieniach startowych.

`CLAUDE.md` (zasady 1 i 2), ADR-0003 (status) i `docs/WATCHDOG.md` opisują ten wyjątek; przeglądy kodu sprawdzają, że
dane katalogu nie wychodzą poza folder usługi i poza jawnie skonfigurowane kanały.

## Konsekwencje

- Watchdog może pokazywać historię i wartości „przed → po”, odtwarza stan po restarcie i nie gubi powiadomień.
- Folder danych zawiera dane osobowe pracowników: klient musi uwzględnić go w rejestrze czynności przetwarzania
  (RODO), polityce kopii zapasowych (kopie mają te same ograniczenia dostępu i powinny być szyfrowane; zalecany
  BitLocker na woluminie) i procedurze likwidacji serwera.
- Odtworzenie usługi wymaga bazy, folderu `keys\` i tego samego konta gMSA; po ponownym utworzeniu gMSA sekrety
  kanałów trzeba wprowadzić od nowa.
- Kod usługi ma testy pilnujące, że dzienniki diagnostyczne nie zawierają wartości atrybutów ani adresów (także przy
  wstrzykniętych błędach LDAP, SMTP i parsowania), że odpowiedzi API nie zawierają sekretów, że zapisany sekret nie
  zostanie wysłany pod zmieniony adres i że po usunięciu rekordów nie ma ich w pliku bazy.
- Kto ma prawa administratora serwera usługi lub może uruchomić kod jako gMSA, ma dostęp do wszystkich danych i może
  wyłączyć monitorowanie – stąd wymóg Tier 0, zewnętrzny monitoring (`/healthz`, dziennik Application, heartbeat).
- Zakres wyjątku jest wąski: każda inna część rozwiązania, która chciałaby przechowywać dane katalogu, wymaga osobnej
  decyzji.

## Uzupełnienie: wiadomości do użytkowników (U1b)

Od U1b AD Manager może wysyłać wiadomości do użytkowników (szablony z danymi konta, README, ścieżka U) przez usługę
(`POST /api/v1/user-messages`, docs/WATCHDOG.md rozdz. 5.8). Warunki tej decyzji obowiązują bez zmian, a nowe dane
mieszczą się w nich tak:

- **Kolejka wysyłek** (`outbox`, punkt 4) przechowuje gotową wiadomość (temat, tekst, HTML, adresy *Od*, *Do*, *DW*,
  *Odpowiedz do*) wyłącznie w treści wiadomości w kolejce (kolumna `payload`, jak treść alarmów), która jest usuwana
  zaraz po wysłaniu; nieudane – po `OutboxDeadDays` (do ręcznego ponowienia), jak każda inna wiadomość. Metadane
  w dzienniku wysyłek nie zawierają tematu ani adresów (tylko rodzaj, stan, liczbę odbiorców i kody).
- **Audyt** (`user_messages`, schemat 6): identyfikator wiadomości, czas, SID i konto operatora, liczba odbiorców, skrót
  SHA-256 posortowanych adresów i skrót SHA-256 żądania (kontrola idempotencji) – nigdy adres, temat ani treść; retencja
  `retention.userMessageAuditDays` (domyślnie 365 dni).
- **Dzienniki diagnostyczne** – jedna linia na wiadomość: identyfikator, konto operatora, liczba odbiorców; bez adresów,
  skrótów i treści.
- Funkcja jest domyślnie wyłączona (`userMessages.enabled`), a strona AD Managera pozostaje pod pełnym zakazem ADR-0003:
  wiadomości i raport istnieją w procesie WPF tylko w pamięci.
