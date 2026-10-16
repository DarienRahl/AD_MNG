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
