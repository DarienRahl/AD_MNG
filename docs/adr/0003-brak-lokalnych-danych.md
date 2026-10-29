# ADR-0003: Żadnych lokalnych kopii danych katalogu

- Status: przyjęta; częściowo zmieniona przez [ADR-0008](0008-watchdog-dane-w-osobnym-folderze.md) (tylko usługa AdMng
  Watchdog)
- Data: 2026-10-13

## Kontekst

Wymaganie użytkownika: aplikacja nie może zbierać danych lokalnie – dane mają być pobierane z AD w momencie
odpytywania. Część narzędzi (np. AD Explorer – snapshoty, narzędzia raportowe z lokalną bazą) synchronizuje katalog
do lokalnej bazy, co tworzy kopię danych osobowych i ryzyko wycieku.

## Decyzja

1. Wszystkie dane obiektów są czytane z DC na żądanie i przechowywane wyłącznie w pamięci, tylko dopóki są wyświetlane.
2. Na dysku mogą być wyłącznie: ustawienia UI, profile połączeń **bez haseł**, zdefiniowane widoki (nazwa, DN, filtr LDAP,
   kolumny) oraz dzienniki diagnostyczne **bez wartości atrybutów**.
3. Eksport (CSV, skrypt PowerShell, dziennik sesji) odbywa się wyłącznie na wyraźne polecenie operatora.
4. Metadane schematu są ładowane do pamięci przy połączeniu i porzucane po rozłączeniu.
5. Hasła (logowania i resetowane) nigdy nie są zapisywane ani logowane.

## Konsekwencje

- Płynność osiągamy technikami „na żywo”: pobieranie tylko potrzebnych atrybutów, stronicowanie strumieniowe,
  VLV dla ogromnych kontenerów, równoległe zapytania, odświeżanie pojedynczego obiektu po zmianie, powiadomienia o zmianach.
- Raporty (np. nieaktywne konta) są zapytaniami LDAP wykonywanymi na bieżąco, a nie analizą lokalnej bazy.
- Testy i code review pilnują, by żadne logowanie nie zawierało wartości atrybutów.

## Uzupełnienie: szablony wiadomości do użytkowników (U1a, U1c)

Szablony wiadomości do użytkowników – temat, tekst, HTML (także wklejony z Outlooka) i **ich obrazy** (logo, zrzuty
ekranu z instrukcji) – są **ustawieniami operatora**, jak zapisane widoki, a nie danymi katalogu: mogą być zapisane w
folderze ustawień (`settings.json`, obrazy w `message-images\{sha256}.png|jpg|gif` obok) i w pliku eksportu szablonów,
który operator zapisuje sam. Warunek: nic, co pochodzi z katalogu, nie trafia do szablonu ani do ustawień – pola
`{givenName}` itp. są wypełniane dopiero w pamięci przy planie, żaden atrybut (np. `thumbnailPhoto`) nie jest źródłem
obrazu, a wybór płci w podglądzie (U1d) działa tylko na bieżącą wysyłkę. Wygenerowane wiadomości, podgląd (także w
WebView2 – treść i obrazy podawane z pamięci, folder tymczasowy przeglądarki usuwany przy zamknięciu) i raport istnieją
tylko w pamięci.

## Uzupełnienie: dziennik „już wysłane” Korespondencji (U5)

Okno *Korespondencja* (wysyłka przez Outlooka, [KORESPONDENCJA.md](../KORESPONDENCJA.md)) pomija adresy, do których już
wysłano, także po przerwaniu wysyłki i ponownym uruchomieniu programu. Dlatego w folderze ustawień
(`correspondence\journal.txt`) może leżeć dziennik wysyłek – ale **bez adresów**: każdy wiersz to skrót adresu
(HMAC-SHA-256 z losowym kluczem użytkownika, pierwsze 16 bajtów), czas wysyłki i temat tak, jak go wpisał operator (bez
wypełnionych pól). Klucz (`journal.key`) jest zaszyfrowany DPAPI dla bieżącego użytkownika Windows, `journal.lock` to
pusty plik blokady. Z dziennika nie da się odczytać listy adresów, a jedynie sprawdzić, czy do znanego adresu już
wysłano. Lista odbiorców, wartości pól z AD, podgląd, raport i dopasowania właścicieli skrzynek istnieją tylko w
pamięci; szablony Korespondencji i ostatni szkic wiadomości to ustawienia operatora, jak szablony U1.
