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
