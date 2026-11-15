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

- **Zatrzymanie wykonania kończy obiekt, który trwa.** *Zatrzymaj* w wierszu stanu (albo *Anuluj* na przycisku *Wykonaj*)
  w oknach, które wykonują zmiany na wielu obiektach – reset hasła, edytor zbiorczy, kreator zakładania kont, przywracanie
  z Kosza AD oraz podgląd planu zasad grupy i jego cofnięcie – nie zaczyna już żadnego nowego obiektu, ale obiekt, którego
  zmiany już ruszyły, dostaje wszystkie swoje zmiany. Konto nie zostaje więc z nowym hasłem i bez pozostałych ustawień,
  a konto z kreatora – wyłączone, bez hasła i bez grup. Wiersz stanu mówi, ile obiektów jest gotowych i że ten, który
  trwał, został dokończony, np. *Wykonywanie „Resetuj hasło” w domenie contoso.test zatrzymano: wykonano 1 z 2 obiektów,
  niepowodzenia: 0, obiekt, który był w toku, został dokończony.* Zatrzymanie nie przerywa też ponawiania żądania, które
  kontroler odrzucił jako chwilowo zajęty – przy awarii kontrolera czeka więc na te próby (kilka sekund) i dopiero wtedy
  kończy obiekt błędem. Plan cofnięcia i dziennik sesji obejmują każdą wykonaną zmianę.

## AdMng Watchdog {{VERSION}}

- Bez zmian w działaniu usługi i bez migracji bazy.

## Aktualizacja z {{PREVIOUS}}

- **AD Manager:** rozpakuj archiwum w miejsce poprzedniego; ustawienia w `%APPDATA%\AdMng` zostają bez zmian.
- **AdMng Watchdog:** aktualizacja nie jest potrzebna (bez zmian w działaniu); pakiet {{VERSION}} można zainstalować
  `apt install ./admng-watchdog_{{VERSION}}-1_amd64.deb`.
- **Powrót do {{PREVIOUS}}:** bez ograniczeń (starsza wersja pomija nowe ustawienia).

## Znane ograniczenia

<!-- TODO: ograniczenia nowych funkcji i to, czego nie sprawdzono na prawdziwej domenie -->
- Wszystkie ograniczenia z wersji {{PREVIOUS}} obowiązują nadal, z jednym wyjątkiem: zatrzymanie wykonania nie zostawia już
  obiektu w połowie (zob. „Inne zmiany”).

## Co dalej

<!-- TODO: następne kroki z ROADMAP.md (albo usuń ten rozdział) -->

## English summary

<!-- TODO: one paragraph in English: "AD Manager {{VERSION}} (preview) adds ...", links to docs/en with the tag v{{VERSION}} -->
