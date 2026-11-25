<!-- DRAFT of the next release notes (docs/dev/releasing.md). Every user-visible change adds its paragraph here while it
     is being developed. `tools/release/release.sh` turns this file into docs/releases/v<version>.md: it drops this
     comment, fills {{VERSION}}, {{PREVIOUS}} and {{DOWNLOADS}}, and refuses to release while a TODO comment or an
     empty section is left. Sections "Nowe: ...", "Inne zmiany" and "Co dalej" may be removed or repeated; the others
     are required. Notes are in Polish with an English summary at the end. -->
<!-- TODO: wstęp – jeden akapit: która to wersja zapoznawcza i co w niej najważniejsze (pogrubione nazwy funkcji) -->

> **Wersja zapoznawcza ({{VERSION}}).** <!-- TODO: czym zostało sprawdzone i czego nie uruchomiono na prawdziwej domenie Windows Server -->
> Lista kontrolna pilotażu: [PILOTAZ.md](https://github.com/DarienRahl/AD_MNG/blob/v{{VERSION}}/docs/PILOTAZ.md).

## Pobieranie

{{DOWNLOADS}}

## Nowe: <!-- TODO: nazwa funkcji -->

<!-- TODO: **Gdzie:** wejścia w interfejsie, potem lista najważniejszych możliwości i link do instrukcji w docs/ (z tagiem v{{VERSION}}) -->

## Inne zmiany

<!-- TODO: lista mniejszych zmian widocznych dla użytkownika (albo usuń ten rozdział) -->

## AdMng Watchdog {{VERSION}}

- Usługa, która zamykała sesję LDAP (przy zatrzymaniu, restarcie lub aktualizacji pakietu albo po chwilowym błędzie
  kontrolera domeny) w chwili, gdy inne zadanie odbierało wynik wyszukiwania, mogła się zakończyć sygnałem SIGABRT lub
  SIGSEGV (kod wyjścia 134 lub 139; biblioteka .NET zwalniała wtedy dwa razy tę samą pamięć) albo zawiesić – teraz
  połączenie jest zamykane dopiero po zakończeniu żądania, które na nim trwa; bez migracji bazy.

## Aktualizacja z {{PREVIOUS}}

- **AD Manager:** rozpakuj archiwum w miejsce poprzedniego; ustawienia w `%APPDATA%\AdMng` zostają bez zmian.
- **AdMng Watchdog:** aktualizacja zalecana (usuwa awaryjne zakończenie usługi opisane wyżej); na serwerze, jako root:
  `apt install ./admng-watchdog_{{VERSION}}-1_amd64.deb` (pakiet OpenWEC nie wymaga aktualizacji). Usługa działa dalej
  z dotychczasową konfiguracją i bazą.
- **Powrót do {{PREVIOUS}}:** bez ograniczeń (starsza wersja pomija nowe ustawienia).

## Znane ograniczenia

<!-- TODO: ograniczenia nowych funkcji i to, czego nie sprawdzono na prawdziwej domenie -->
- Wszystkie ograniczenia z wersji {{PREVIOUS}} obowiązują nadal.

## Co dalej

<!-- TODO: następne kroki z ROADMAP.md (albo usuń ten rozdział) -->

## English summary

<!-- TODO: one paragraph in English: "AD Manager {{VERSION}} (preview) adds ...", links to docs/en with the tag v{{VERSION}} -->
