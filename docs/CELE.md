[English](en/GOALS.md) · **Polski**

# AD Manager – cele

Do czego służy projekt, po czym poznamy, że działa, i dokąd zmierza. To, co oznaczono jako **propozycję**, czeka na
decyzję właściciela; reszta powtarza to, co już mówią [README](../README.pl.md), opisy wydań i
[lista kontrolna pilotażu](PILOTAZ.md).

## Do czego służy i dla kogo

AD Manager to konsola Windows do Active Directory: szybki, nowoczesny następca przystawki `dsa.msc` z edycją zbiorczą,
Koszem AD i monitorem zmian w katalogu. Jest dla osób, które na co dzień opiekują się domeną ze stacji roboczej
przyłączonej do domeny – administratorów, helpdesku i wszystkich, którzy muszą odpowiedzieć na pytania „kto ma jakie
uprawnienia” i „kto to zmienił”. Wszystko czyta na żywo z kontrolera domeny i nie wymaga instalatora, .NET ani RSAT.

## Cele produktu

| # | Cel | Znak, po którym to poznamy |
|---|---|---|
| 1 | Codzienna praca bez `dsa.msc` | scenariusze codziennej pracy z pilotażu (PILOTAZ.md, 3.1) dają ten sam wynik co narzędzie Microsoftu, a administratorzy z pilotażu przez tydzień nie otwierają przystawki |
| 2 | Wiele obiektów naraz, bez obaw | każda zmiana ma podgląd, walidację, zapis w audycie i cofnięcie; nie istnieje ścieżka zapisu poza planem zmian |
| 3 | Szybko przy dużym katalogu, bez blokowania | listy dziesiątek tysięcy obiektów działają płynnie, a każdy długi odczyt da się anulować; budżety mierzymy w CI ([odnowa interfejsu, rozdział 7](dev/v-interface-renewal.md#7-accessibility-keyboard-and-performance-budgets)) |
| 4 | Pokazać to, czego przystawka nie pokazuje | stan kont, uprawnienia, komputery, zasady grupy, AD CS i dzienniki zdarzeń odpowiadają na swoje pytania z danych na żywo; części 3.2–3.5 pilotażu zgadzają się z narzędziami Microsoftu |
| 5 | Wiedzieć, kto co zmienił | w pilotażu na kontrolerach domeny Windows Server AdMng Watchdog wskazuje, kto i skąd wprowadził zmianę, a powiadomienie dociera |
| 6 | Łatwa obsługa i nauka (**propozycja**, ścieżka V) | każdą funkcję da się znaleźć po nazwie, każda strona sama się objaśnia, wynik działania widać zawsze w tym samym miejscu – miary G1–G7 [odnowy interfejsu](dev/v-interface-renewal.md#1-goals-and-measures) |
| 7 | Bezpieczne wdrożenie | nic z katalogu nie zostaje na stacji roboczej, żadne hasło nie jest zapisywane, kod jest otwarty (Apache-2.0), wydania są podpisane i mają sumy kontrolne |

## Zasady, od których nie ma wyjątków

1. **Nic z katalogu nie zostaje na stacji roboczej.** To, co widać, zostało przed chwilą odczytane z kontrolera domeny
   i żyje tylko w pamięci. Na dysku są ustawienia, diagnostyka bez wartości z katalogu i pliki, które operator sam
   eksportuje. (Usługa Watchdog przechowuje to, co monitoruje – na własnym serwerze, według własnych zasad.)
2. **Hasła nigdy nie są zapisywane ani logowane.**
3. **Wszystko, co wpisano, jest escapowane**, zanim trafi do filtra LDAP albo nazwy wyróżniającej.
4. **Jedno okno rozmawia z jednym kontrolerem domeny** – przy odczycie i przy zapisie.
5. **Każda zmiana przechodzi przez plan zmian** – podgląd, walidacja, audyt, cofnięcie – pojedyncza edycja także.
6. **Okno nigdy się nie zawiesza.** Każde wywołanie katalogu działa w tle i da się je anulować.

Dokładne brzmienie, z nielicznymi wyjątkami, jest w [notatkach dla deweloperów](../CLAUDE.md) (po angielsku).

## Czym nie jest

- Nie jest kopią, pamięcią podręczną ani archiwum katalogu i nie działa offline.
- Nie jest usługą ani harmonogramem: zamknięty program nic nie robi. Stałe monitorowanie to osobna usługa
  AdMng Watchdog.
- Nie jest aplikacją webową ani składnikiem serwerowym w domenie.
- Nie zmienia AD po cichu: nie ma zapisu bez podglądu.
- Nie jest kolektorem logów: analizy dzienników zdarzeń czytają na żądanie i niczego nie przechowują.
- Nie jest produktem Microsoftu i **nie przeszedł jeszcze pilotażu na produkcyjnym Windows AD**.

## Gdzie jesteśmy

Wersja **0.15.0**, szesnaste wydanie. Po kolei: konsola (0.1.0), AdMng Watchdog (0.2.0) i jego usługa dla Linuksa
z pakietami Debiana (0.3.0), przegląd kont i wiadomości e-mail do użytkowników (0.4.0), uprawnienia kont i kilka domen
w osobnych oknach (0.5.0), komputery i zasady grupy w drzewie (0.6.0), Korespondencja przez Outlooka (0.7.0, 0.9.1),
edytor szablonów administracyjnych (0.8.0), „co dostanie komputer” (0.9.0), ustawienia zabezpieczeń GPO (0.10.0),
czytelność i diagnostyka GPO (0.11.0), dzienniki zdarzeń (0.12.0), stabilizacja i gotowość do pilotażu (0.13.0),
wyszukiwanie, kopiowanie i porządki w GPO (0.14.0), zakładanie wielu kont z Excela i przegląd AD CS tylko do odczytu
(0.15.0).

Na [roadmapie](../README.pl.md#-roadmapa) faza 0 jest zrobiona; fazy 1 i 2 oraz ścieżki W, U, K, E i P1 są
zaimplementowane i oznaczone 🟡, bo czekają na pilotaż na Windows Server AD. Wydania nie są jeszcze podpisane.

## Cele najbliższych wydań (propozycja)

Uzgodniona kolejność funkcji to **R1** (okno PowerShell, odczyty) → **P2** (wystawianie certyfikatów) → **N** (DNS
i DHCP). Propozycja: wpleść między nie etapy odnowy interfejsu ([ścieżka V](dev/v-interface-renewal.md#8-stages)),
tak żeby każde nowe okno powstawało już w nowym stylu i niczego nie trzeba było przerabiać dwa razy:

| Kolejność | Funkcja | Odnowa interfejsu |
|---|---|---|
| 1 | – | V1 – wygląd: tokeny, nowe ikony, pierwsze animacje |
| 2 | R1 – okno PowerShell | V2 – linia stanu, przycisk akcji z anulowaniem |
| 3 | – | V3 – szkielet strony: nagłówek, karta paska narzędzi, pasek listy |
| 4 | P2 – wystawianie certyfikatów | – |
| 5 | – | V4 – powłoka: pasek tytułu z wyszukiwaniem, obszary, strona startowa |
| 6 | N – DNS i DHCP, pierwszy etap | V5 – ustawienia jako karty, jeden styl okien dialogowych |
| 7 | – | V6 – duże okna; V7 – audyt dostępności i wydajności |

To, czy rozwijamy aplikację WPF w miejscu (zalecane), czy zaczynamy powłokę w WinUI 3, jest decyzją właściciela – zob.
[decyzję technologiczną](dev/v-interface-renewal.md#3-technology-decision-the-owners-decision).

## Droga do wersji 1.0 (propozycja)

README nie definiują wersji 1.0. Wymieniają to, co jeszcze otwarte, i proponujemy nazwać wydanie 1.0 wtedy, gdy:

1. **Pilotaż jest zakończony** na Windows Server AD według [PILOTAZ.md](PILOTAZ.md): tabela wyników jest wypełniona,
   nie ma otwartej blokującej różnicy względem narzędzi Microsoftu, a fazy 1 i 2 zmieniają status z 🟡 na ✅.
2. **Pakiety są podpisane.** Podpisywanie kodu przez SignPath Foundation jest w przygotowaniu
   ([Code signing policy](../README.pl.md#-code-signing-policy)); dotychczasowe wydania nie są podpisane.
3. **Jest instalator** obok archiwum zip – podpisany MSI z
   [fazy 7](../README.pl.md#faza-7--wdrożenie-i-dodatki-).
4. **AdMng Watchdog przeszedł swój pilotaż** na kontrolerach domeny Windows Server
   ([WATCHDOG-LINUX.md](WATCHDOG-LINUX.md)).
5. **Odnowa interfejsu doszła do powłoki** (V4), żeby wersja 1.0 nie zmieniała wyglądu zaraz po wydaniu.
