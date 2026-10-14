# ADR-0008: Watchdog przechowuje dane w osobnym folderze (wyjątek od ADR-0003)

- Status: przyjęta
- Data: 2026-10-14
- Zmienia: [ADR-0003](0003-brak-lokalnych-danych.md) (tylko w zakresie usługi AdMng Watchdog)

## Kontekst

[ADR-0003](0003-brak-lokalnych-danych.md) zakazuje przechowywania lokalnych kopii danych katalogu: AD Manager czyta
wszystko z DC na żądanie i trzyma dane tylko w pamięci, dopóki są wyświetlane.

Użytkownik zamówił watchdog, który całą dobę raportuje zmiany w AD (nowe konta i komputery, zmiany parametrów kont,
blokady, zbliżające się terminy ważności, zmiany uprawnień), z historią, konfigurowalnymi regułami, wysyłką SMTP,
raportem dziennym i kanałami Teams / webhook. Taka funkcja nie może działać bez stanu:

- zmianę „stara → nowa wartość” da się wyliczyć tylko wtedy, gdy poprzednia wartość jest gdzieś zapisana – AD
  przechowuje wyłącznie stan bieżący (a `whenChanged`, metadane replikacji i DirSync nie podają poprzednich wartości);
- znacznik postępu (`highestCommittedUSN`), identyfikator DC (`invocationId`), kolejka powiadomień z ponawianiem,
  deduplikacja ostrzeżeń o wygasaniu kont i raport dzienny muszą przetrwać restart;
- przypisanie wykonawcy z dzienników zabezpieczeń DC często przychodzi później niż wykrycie zmiany, więc zdarzenia
  muszą czekać na uzupełnienie;
- operator chce przeglądać historię zdarzeń, a nie tylko otrzymywać powiadomienia.

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

Rozluźniamy ADR-0003 **wyłącznie** dla usługi AdMng Watchdog (projekty `src/AdMng.Watchdog.*`). Usługa może
przechowywać dane katalogu w swoim dedykowanym folderze danych na następujących warunkach:

1. **Jedno miejsce.** Dane katalogu są zapisywane tylko w folderze danych usługi (domyślnie
   `%ProgramData%\AdMng\Watchdog`, konfigurowalny): baza SQLite `watchdog.db` (z plikami WAL), kopie zapasowe w
   `backup\`, pierścień kluczy w `keys\`. Nigdzie indziej: nie w plikach tymczasowych, rejestrze, dzienniku zdarzeń
   Windows, konsoli ani w dziennikach diagnostycznych – te nadal nie zawierają wartości z katalogu.
2. **Tylko to, co potrzebne.** Migawka zawiera wyłącznie obserwowane atrybuty (lista w specyfikacji), członkostwa
   grup i identyfikatory (GUID, SID, DN); zdarzenia zawierają zmiany „przed → po”, atrybucję i stan powiadomień;
   dzienniki DC – tylko znormalizowane pola potrzebne do dopasowania.
3. **Nigdy sekrety.** Hasła i ich skróty, LAPS, BitLocker, TPM, hasła gMSA, atrybuty zaufania oraz wszystkie atrybuty
   oznaczone w schemacie jako poufne (`SecretAttributes.IsSecret`) nie są odczytywane ani zapisywane; zmiana takiego
   atrybutu jest odnotowywana jako „zmieniono (wartość ukryta)”, a wartości w zdarzeniach 5136 są maskowane przed
   zapisem.
4. **Retencja.** Każdy rodzaj danych ma okres przechowywania z wartością domyślną (zdarzenia 365 dni, zdarzenia
   z dzienników DC 30 dni, historia wysyłek 30/90 dni, migawki obiektów usuniętych 180 dni, kopie zapasowe 7 dni),
   egzekwowany codziennie przez usługę i konfigurowalny przez administratora.
5. **Dostęp.** Folder ma wyłączone dziedziczenie i uprawnienia wyłącznie dla SYSTEM, lokalnych Administratorów
   i konta usługi (gMSA); usługa sprawdza ACL przy starcie i zgłasza odstępstwa. Dane są udostępniane wyłącznie przez
   API usługi po HTTPS z uwierzytelnianiem Kerberos, dla członków skonfigurowanych grup AD.
6. **Sekrety kanałów** (hasło SMTP, sekret OAuth, adresy Teams Workflows i webhooków, klucze podpisów) są szyfrowane
   (ASP.NET Core Data Protection, klucze chronione DPAPI-NG dla SID gMSA) i nigdy nie są zwracane przez API.
7. **Dane wychodzące** (e-mail, Teams, webhook) są kopią poza folderem – dlatego każdy kanał ma poziom szczegółowości
   (minimalny / standardowy / pełny), a sekrety nie są wysyłane nigdy.
8. **AD_MNG bez zmian.** Aplikacja WPF nadal niczego nie zapisuje lokalnie: zakładka Watchdog pobiera dane z usługi
   na żądanie i trzyma je w pamięci; na dysku zapisuje tylko adres usługi (ustawienie). Eksport CSV zdarzeń odbywa się
   wyłącznie na wyraźne polecenie operatora (jak w ADR-0003).

`CLAUDE.md` (zasada 1) i `docs/WATCHDOG.md` opisują ten wyjątek; przeglądy kodu sprawdzają, że dane katalogu nie
wychodzą poza folder usługi i poza jawnie skonfigurowane kanały.

## Konsekwencje

- Watchdog może pokazywać historię i wartości „przed → po”, odtwarza stan po restarcie i nie gubi powiadomień.
- Folder danych zawiera dane osobowe pracowników: klient musi uwzględnić go w rejestrze czynności przetwarzania
  (RODO), polityce kopii zapasowych (kopie mają te same ograniczenia dostępu) i procedurze likwidacji serwera.
- Odtworzenie usługi wymaga bazy, folderu `keys\` i tego samego konta gMSA; po ponownym utworzeniu gMSA sekrety
  kanałów trzeba wprowadzić od nowa.
- Kod usługi ma testy pilnujące, że dzienniki diagnostyczne nie zawierają wartości atrybutów, a odpowiedzi API –
  sekretów.
- Zakres wyjątku jest wąski: każda inna część rozwiązania, która chciałaby przechowywać dane katalogu, wymaga osobnej
  decyzji.
