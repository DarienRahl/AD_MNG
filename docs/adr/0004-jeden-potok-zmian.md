# ADR-0004: Jeden potok zmian dla edycji pojedynczej i zbiorczej

- Status: przyjęta
- Data: 2026-10-13

## Kontekst

Edycja zbiorcza jest kluczowym wymaganiem. ADUC pozwala edytować wiele obiektów tylko dla kilku pól, bez podglądu,
bez raportu i bez możliwości cofnięcia. Narzędzia komercyjne (ManageEngine ADManager Plus, Adaxes) stosują model
„wybierz → skonfiguruj → podgląd → wykonaj → raport”.

## Decyzja

Każda zmiana – także zmiana jednego pola w oknie właściwości – przechodzi przez ten sam potok:

```
BatchOperation[]  →  IChangePlanner  →  ChangePlan (diff: stara → nowa wartość, walidacja)
                  →  podgląd / potwierdzenie  →  IBatchExecutor  →  BatchResult  →  dziennik sesji
                  →  (opcjonalnie) IRollbackPlanner → plan cofnięcia
```

- Planer czyta **aktualne** wartości z DC tuż przed podglądem, pomija zmiany bez efektu, waliduje wg schematu
  i efektywnych uprawnień operatora (`allowedAttributesEffective`), sprawdza unikalność (sAMAccountName, UPN, mail).
- Wykonawca stosuje optymistyczną współbieżność (delete starej + add nowej wartości w jednym atomowym modify), więc
  zmiana wprowadzona przez kogoś innego po podglądzie nie zostanie nadpisana.
- Wyniki per obiekt, ponawianie błędów przejściowych, anulowanie, eksport CSV, odpowiednik w PowerShell, cofnięcie.

## Konsekwencje

- Jedna implementacja walidacji, audytu i obsługi błędów; edycja pojedyncza „za darmo” zyskuje podgląd i cofnięcie.
- Nieco większy koszt prostych zmian (odczyt przed zapisem) – pomijalny przy jednym obiekcie.
