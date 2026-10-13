# ADR-0006: Interfejs świadomy uprawnień operatora

- Status: przyjęta
- Data: 2026-10-13

## Kontekst

Aplikacja jest używana z poświadczeniami o różnym poziomie uprawnień (helpdesk z delegacją na wybrane OU,
administratorzy). ADUC pokazuje wszystkie pola jako edytowalne i dopiero przy zapisie zgłasza „Odmowa dostępu”.

## Decyzja

Przy otwarciu obiektu pobierane są atrybuty konstruowane `allowedAttributesEffective`,
`allowedChildClassesEffective` i `sDRightsEffective`. Pola, których operator nie może zmienić, są tylko do odczytu
(z podpowiedzią dlaczego), a akcje niedostępne (np. tworzenie obiektów w OU) są wyłączone. Planer zmian zbiorczych
odrzuca zmiany niedozwolone jeszcze przed wykonaniem. Ostateczną decyzję zawsze podejmuje serwer.

## Konsekwencje

- Jeden dodatkowy odczyt bazowy na otwierany obiekt (kilka ms).
- Prawa rozszerzone (np. reset hasła) nie są widoczne w tych atrybutach – dla nich UI pozwala spróbować i czytelnie
  tłumaczy odmowę.
