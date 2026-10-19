# ADR-0005: Sesja przypięta do jednego kontrolera domeny

- Status: przyjęta
- Data: 2026-10-13

## Kontekst

AD replikuje zmiany z opóźnieniem. Jeśli zapis trafi do DC1, a odświeżenie do DC2, operator widzi stare dane i
myśli, że zmiana się nie udała (częsty problem przy narzędziach używających losowego DC).

## Decyzja

Po zalogowaniu wybierany jest jeden DC (lokator DC z uwzględnieniem lokacji lub wybór ręczny). Wszystkie odczyty i
zapisy sesji – w tym operacje zbiorcze – idą do tego DC przez pulę kilku połączeń. Nazwa DC jest widoczna na pasku
stanu; operator może zmienić DC („Zmień kontroler domeny…” jak w ADUC).

## Konsekwencje

- Spójny obraz „zapisz → odśwież”.
- Przy awarii DC sesja zgłasza błąd i proponuje ponowne połączenie (bez cichego przełączania na inny DC).
- „Zmień kontroler domeny…” (faza 1, październik 2026) nie przełącza istniejącej sesji: otwiera nową sesję na wybranym
  DC z poświadczeniem bieżącej i zastępuje ją dopiero po sprawdzeniu (ta sama domena, inny DC). Okna otwarte wcześniej
  zostają przy poprzedniej sesji i nie zapisują – reguła „jedna sesja = jeden DC” obowiązuje bez wyjątków
  ([dev/phase1-changedc.md](../dev/phase1-changedc.md)).
