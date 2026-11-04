# Dzienniki zdarzeń w AD Managerze

Od ścieżki **E** AD Manager czyta dzienniki zdarzeń kontrolerów domeny, serwerów i stacji oraz pliki `.evtx` – bez
Podglądu zdarzeń i bez instalowania czegokolwiek – i od razu je **analizuje**: **skąd blokada konta** (z przejściem
do komputera źródłowego) i **nieudane logowania** (zgadywanie haseł, rozpylanie haseł, sprawdzanie istnienia kont,
sukces po serii niepowodzeń). To **osobna funkcja, niezależna od AdMng Watchdog**: Watchdog stale zbiera wybrane
zdarzenia DC i przechowuje ich historię, a dzienniki zdarzeń w AD Managerze służą do doraźnego dochodzenia – dowolny
kanał, dowolny komputer, historia sięgająca tak daleko, jak sam dziennik.

Specyfikacja dla programistów (po angielsku): [dev/e-event-logs.md](dev/e-event-logs.md).

---

## 1. Gdzie to jest w programie

| Miejsce | Co daje |
|---|---|
| *Narzędzia → Dzienniki zdarzeń…*, paleta `events.open` | karta **Dzienniki zdarzeń** (czytnik, rozdział 3) |
| *Narzędzia → Nieudane logowania…*, paleta `events.failedLogons` | karta **Nieudane logowania** (rozdział 5) |
| Menu kontekstowe użytkownika, zakładka *Konto* we właściwościach, *Przegląd kont* (konta zablokowane), paleta `events.lockout` | **Skąd blokada konta…** – karta **Skąd blokada konta – <konto>** (rozdział 4) |
| Menu kontekstowe komputera, strona *Stan* we właściwościach, paleta `events.computer` | **Dziennik zdarzeń tego komputera** – czytnik ustawiony na ten komputer (uruchamianie Podglądu zdarzeń zostaje) |

Każda karta należy do swojego okna połączenia i korzysta z jego poświadczeń.

## 2. Wymagania

- **AD Manager uruchomiony w Windows** i okno połączone kontem Windows albo poświadczeniami domeny (logowanie Kerberos;
  okno połączone nazwą wyróżniającą – prostym powiązaniem – nie może czytać dzienników). Komputery podaje się **pełną
  nazwą DNS** (`waw-pc001.contoso.test`) – Kerberos nie działa z adresem IP ani krótką nazwą.
- Na czytanych komputerach (także DC):
  - reguły zapory z grupy **Zdalne zarządzanie dziennikiem zdarzeń** (*Remote Event Log Management*: RPC, mapowanie
    punktów końcowych RPC, nazwane potoki) – najlepiej tylko z podsieci stacji administratorów;
  - konto operatora w grupie **Czytelnicy dziennika zdarzeń** (*Event Log Readers*) – wystarcza do kanału
    *Zabezpieczenia*, uprawnienia administratora domeny nie są potrzebne. Na DC warto użyć osobnej grupy domenowej
    zagnieżdżonej w *Czytelnicy dziennika zdarzeń* i traktować ją jak Tier 0 (dzienniki DC pokazują działania
    administratorów).
- Gdy czegoś brakuje, karta mówi, czego: komputer nie odpowiada, brak połączenia RPC (zapora albo wyłączony komputer),
  odmowa dostępu (grupa, uprawnienia kanału), nie ma takiego kanału, komputer odrzucił zapytanie – zawsze z podpowiedzią,
  co zrobić.
- **Analizy potrzebują inspekcji na DC**: blokady 4740 – *Zarządzanie kontami użytkowników* (sukces); 4771 – *Usługa
  uwierzytelniania Kerberos* (niepowodzenie); 4776 – *Weryfikacja poświadczeń* (niepowodzenie); 4625 – *Logowanie*
  (niepowodzenie); 4624 – *Logowanie* (sukces). Karty pokazują, czy te zdarzenia w ogóle są rejestrowane (rozdział 4).
  Ustawienia inspekcji zmienia się w edytorze ustawień zabezpieczeń GPO
  ([ZASADY-GRUPY.md, rozdział 10](ZASADY-GRUPY.md#10-ustawienia-zabezpieczeń-grupy-lokalne-prawa-zasady-kont-inspekcja-usługi)).

## 3. Czytnik – karta „Dzienniki zdarzeń”

- **Źródła:** wszystkie kontrolery domeny (emulator PDC wyróżniony), komputery wpisane pełną nazwą DNS albo wybrane z
  AD (*Wybierz z AD…*) i pliki `.evtx` (*Otwórz plik .evtx…*; plik czytany jest od najstarszych zdarzeń).
- **Zapytanie:** kanał (lista typowych kanałów albo *Wczytaj kanały* komputera), czas (*Ostatnia godzina*, *Ostatnie 24
  godziny* – domyślnie, *Ostatnie 7 dni*, *Własny*), identyfikatory (`4625, 4740, 4768-4771`), poziomy, dostawcy,
  inspekcja sukcesów lub niepowodzeń, a dla kanału *Zabezpieczenia* także konto, SID, adres IP i stacja. Program buduje
  z tego zapytanie XPath i bezpiecznie wstawia wartości (nie da się nimi zmienić zapytania). **Tryb XPath (ekspert)**
  przyjmuje własne wyrażenie po kontroli składni; resztę sprawdza komputer docelowy (w tym trybie przedział czasu z
  listy nie obowiązuje).
- **Wyszukaj** czyta najwyżej cztery komputery naraz, z postępem dla każdego; *Przerwij* zatrzymuje odczyt na
  komputerach docelowych. Wyniki: wspólna **oś czasu** wszystkich źródeł (od najnowszych), najwyżej 50 000 zdarzeń –
  przy większej liczbie karta mówi, ile pominięto, i prosi o zawężenie zapytania. *Ctrl+F* filtruje wczytane zdarzenia.
- **Szczegóły zdarzenia:** wszystkie pola po nazwach z objaśnieniami kodów po polsku (np. `0xC000006A` – złe hasło,
  Kerberos `0x18`, typy logowania, typy szyfrowania z oznaczeniem słabych RC4 i DES), konta i SID-y połączone z
  obiektami AD, **Pokaż komunikat** – pełny opis zdarzenia odczytany z komputera.
- **Zasięg i dostęp:** dla każdego źródła najstarsze zdarzenie, liczba zdarzeń, rozmiar, tryb dziennika (nadpisywanie,
  archiwizacja, bez nadpisywania) i ostrzeżenie *„DC01 sięga tylko 6 godz. wstecz”*, gdy przedział zaczyna się
  wcześniej niż dziennik; przy błędzie – przyczyna i podpowiedź.
- **Zapisane zapytania:** nazwa, kanał, identyfikatory, poziomy, dostawcy, warunki i XPath tak, jak je wpisano, oraz
  lista komputerów (bez ścieżek plików i bez wartości odczytanych z dzienników).
- **Eksport** do CSV albo HTML wyłącznie do pliku wskazanego przez operatora; plik zaczyna się uwagą, że zawiera dane
  osobowe (konta, adresy, nazwy komputerów).

## 4. Skąd blokada konta

**Sprawdź** robi w jednym kroku to, do czego zwykle potrzeba LockoutStatus i EventCombMT:

1. Odczytuje konto i zasady blokady (zasady domeny albo zasady haseł szczegółowych konta – PSO).
2. Na **każdym kontrolerze domeny** odczytuje stan konta: zablokowane czy nie, licznik złych haseł, czas ostatniego
   złego hasła i ostatniego logowania; wskazuje DC, na którym zapisano blokadę (metadane replikacji `lockoutTime`).
3. Czyta dzienniki *Zabezpieczenia* wszystkich DC (najpierw emulator PDC i DC, na którym zapisano blokadę): blokady
   4740, odblokowania 4767, nieudane próby 4771, 4776 i 4625 tego konta w przedziale (domyślnie 24 godziny przed
   ostatnią blokadą). Potem na każdym odczytanym DC sprawdza krótko (jedno zdarzenie dowolnego konta), czy wymagane
   zdarzenia, których nie było w odczycie, w ogóle są w dzienniku, i czyta nieudane próby wszystkich kont z kilku
   najczęstszych źródeł (rozpoznanie serwera pośredniczącego).
4. Pokazuje:
   - **Podsumowanie** – czy konto jest teraz zablokowane i na których DC, ostatnia blokada, próg i okno blokady.
   - **Źródła prób** w kolejności od najważniejszego: komputer wywołujący z 4740, adres IP z 4771, stacja z 4776 i
     4625 – z liczbą prób, złych haseł, prób przed blokadą, czasami, kontrolerami i objaśnionymi kodami. Wskazówki:
     *prawdopodobnie serwer pośredniczący* (Exchange, ADFS, NPS, brama usług pulpitu zdalnego – po nazwie albo po
     wielu kontach z tego samego źródła), *kontroler domeny – tylko przekazał próbę*.
   - **Oś czasu**, tabelę **kontrolerów domeny** (stan konta i zasięg dziennika) i **uwagi**: brak komputera
     wywołującego, luki w zasięgu dzienników, pełny dziennik bez nadpisywania, pominięte najstarsze zdarzenia (limit
     pamięci), próby mimo blokady (*najpewniej zapisane hasło – usługa, zadanie zaplanowane, zamapowany dysk,
     telefon*), różnica czasu, próg 0. Kopia niepowodzenia NTLM, które inny DC ponowił na emulatorze PDC, liczy się
     raz. *Teraz zablokowane* oznacza stan w chwili sprawdzenia, także dla przedziału własnego.
   - **Inspekcję na kontrolerach domeny**: dla każdego DC i wymaganego zdarzenia – *zarejestrowane*, *brak zdarzeń
     żadnego konta, choć dziennik obejmuje przedział – prawdopodobnie nie jest inspekcjonowane* (tylko po sprawdzeniu
     bez filtra konta) albo *nie wiadomo*; z przejściem do modelu zasad grupy DC (*Pokaż w modelu*) i do *Diagnostyki
     zasad grupy*.
5. **Sprawdź komputer źródłowy** (dla komputera albo adresu IP – nazwa ustalana przez DNS): przez zdalne akcje
   ([KOMPUTERY.md](KOMPUTERY.md)) pokazuje **sesje** tego konta, **usługi** i **zadania zaplanowane** działające jako to
   konto (z oznaczeniem zadań z zapisanym hasłem); *Otwórz zdalne akcje* otwiera okno komputera.

Konto zablokowane można odblokować zwykłym poleceniem (przez podgląd planu zmian). Gdy okno jest połączone z AdMng
Watchdog, *Historia w Watchdogu* pokazuje dłuższą historię zmian i blokad konta.

## 5. Nieudane logowania

Dla wybranego przedziału (*Ostatnia godzina*, *Ostatnie 24 godziny*, *Ostatnie 7 dni*) karta czyta niepowodzenia
4625, 4771, 4776 i 4768 ze wszystkich DC, a potem udane logowania tylko kont, które mogły zostać odgadnięte. Ustalenia:

- **Zgadywanie hasła** – wiele złych haseł jednego konta z jednego źródła;
- **Rozpylanie haseł** – wiele kont z jednego źródła w krótkim czasie, po kilka prób na konto (mniej niż próg
  zgadywania hasła, więc także „próg blokady − 1”); jedno ustalenie na źródło i ciągły okres;
- **Sprawdzanie istnienia kont** – wiele nieistniejących nazw z jednego źródła; nazwy nieistniejących kont są ukryte,
  dopóki nie klikniesz *Pokaż* (bywają hasłami wpisanymi w pole nazwy);
- **Sukces po serii niepowodzeń** – udane logowanie z tego samego źródła po serii prób.

Każde ustalenie ma ważność, objaśnienie, konta, liczby, czasy, kody i listę zdarzeń; liczniki ważności filtrują listę,
są też filtry rodzaju i tekstu, *Najczęstsze konta*, *Najczęstsze źródła* i *Kody niepowodzeń*. **Progi** (rozwijane)
zmieniają czułość. Adresy kontrolerów domeny są liczone, ale nie są ustaleniami (DC tylko przekazują próby); kopia
niepowodzenia NTLM ponowionego na emulatorze PDC liczy się raz, a odmowa biletu dla konta wyłączonego, wygasłego albo
zablokowanego (4768 `0x12`) jest liczona, ale nie jest próbą hasła. Inspekcję udanych logowań sprawdza się osobno, bez
filtra konta (jedno zdarzenie na DC). Zastrzeżenia:
NAT, VPN i serwery pośredniczące łączą wielu użytkowników pod jednym adresem, a nazwę stacji w 4776 podaje klient.

## 6. Co AD Manager zapisuje na dysku

Nic z dzienników zdarzeń (zasada 1): zdarzenia, zasięgi, wyniki analiz i komunikaty są tylko w pamięci karty, dopóki
jest otwarta; dziennik diagnostyczny programu nie zawiera treści zdarzeń. Zapisywane są tylko zapisane zapytania
(rozdział 3). Pliki powstają wyłącznie tam, gdzie wskaże je operator (eksport CSV i HTML). Hasło okna jest tylko w
pamięci (zasada 2).

## 7. Ograniczenia i pilotaż

- Sprawdzone testami na fałszywych dziennikach (polskie konta i komputery, scenariusze blokad, rozpylania i
  zgadywania haseł) i testach interfejsu na Windows; odczyt z prawdziwych komputerów przez RPC i z plików `.evtx` –
  pilotaż na Windows Server AD (porównanie z LockoutStatus, EventCombMT i Podglądem zdarzeń).
- Odczyt tylko przez RPC; WinRM (zestawienia liczone na komputerze docelowym) – później.
- Kolejne analizy ścieżki E (Kerberos i RC4, NTLM i NTLMv1, podpisywanie LDAP, historia logowań, zmiany kont i grup,
  zasady grupy na komputerze, zdrowie DC, integralność inspekcji) – w następnych etapach (README, *Ścieżka E*).
