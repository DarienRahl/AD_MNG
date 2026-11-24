[English](../en/adr/0010-powershell-window-embedded-engine.md) · **Polski**

# ADR-0010: Okno PowerShell – osadzony silnik PowerShell 7 (ścieżka R)

- Status: **przyjęta** (2026-11-16). Decyzje właściciela projektu, któremu zespół przekazał sprawy z sekcji „Do ustalenia
  z zespołem” ścieżki R oraz nazwy poleceń. Dotyczą etapu R1 i wiążą etapy R2 i R3; w README ścieżki R rozstrzygnięte
  wpisy przeszły do sekcji „Ustalone”
- Data: 2026-11-16
- Dotyczy: ścieżka R w [README](../../README.pl.md#ścieżka-r--okno-powershell--r1-); zasady 1–6 z `CLAUDE.md` pozostają bez
  zmian (okno PowerShell działa w ich granicach, nie jest wyjątkiem od żadnej z nich)
- Notatka wykonawcza: [`docs/dev/r1-powershell-window.md`](../dev/r1-powershell-window.md) (wiążąca dla etapu R1; część
  „R1 as built” opisuje, co faktycznie zbudowano i czego nie udało się sprawdzić)

## Kontekst

Ścieżka R dodaje okno PowerShell 7 przy każdym oknie połączenia: doraźne zapytania, zrzuty danych z AD i skrypty bez
osobnej konsoli, bez RSAT i bez ponownego logowania. Polecenia AD Managera (`Get-Adm…`) czytają przez `IDirectorySession`
okna – ten sam DC, te same poświadczenia, filtry zbudowane przez `LdapFilter` – a wyniki są zwykłymi obiektami PowerShell,
które można otworzyć w siatce programu. Zapisy nigdy nie idą wprost do AD: etap R2 doda je do planu zmian okna (zasada 5),
etap R3 doda moduły RSAT.

README zostawiało sześć spraw do rozstrzygnięcia „z zespołem”: rozmiar pakietu i wybór osadzonego silnika, tryb języka bez
zasad WDAC/AppLocker, to, czy okno może ładować dowolne moduły i skrypty z dysku, listę tłumaczonych poleceń RSAT,
poświadczenia okna dla modułu `ActiveDirectory` oraz nazwy poleceń i skrót klawiszowy. Właściciel rozstrzygnął pierwsze
trzy, nazwy i skrót oraz wszystkie szczegóły etapu R1 (wybór silnika, zasady hosta, edytor wiersza, zasady PowerShell z
GPO, pliki na dysku). Dwie sprawy dotyczące R3 – lista tłumaczonych poleceń RSAT i poświadczenia dla modułu
`ActiveDirectory` – zostają otwarte.

Ograniczenia: AD Manager jest aplikacją WPF na .NET 10 (jedna aplikacja, bez usługi i bez zainstalowanego PowerShell
wymaganego do działania), obowiązują zasady 1–6 (zwłaszcza zasada 1 – żadnych lokalnych danych katalogu, zasada 2 – żadnych
zapisanych haseł, zasada 4 – jeden DC na sesję i zasada 6 – interfejs nigdy nie blokuje), a pakiet z silnikiem trafia
wyłącznie do aplikacji, nigdy do pakietu Watchdoga.

## Decyzja

1. **Silnik (D1): osadzony PowerShell 7 z pakietu `Microsoft.PowerShell.SDK`** (linia LTS 7.6.x; zbudowano na 7.6.10,
   `net10.0`) w nowym projekcie `src/AdMng.PowerShell` (`net10.0`), używanym wyłącznie przez `AdMng.App`, nigdy
   dostarczanym w pakiecie Watchdoga – jak `AdMng.Remote`. Wersja 7.7 celuje w .NET 11, więc przejdziemy na nią razem z
   aktualizacją platformy. Rozmiar pakietu (+19…54 MB) i wydanie AD Managera przy każdej poprawce bezpieczeństwa
   PowerShell są **zaakceptowane**. Jeśli silnik nie działa w jednoplikowym `AdMng.exe` (SDK potrzebuje manifestów
   modułów obok hosta), pliki silnika zostaną opublikowane w folderze obok `AdMng.exe`; rozstrzyga test dymny w CI na
   Windows (część 4 R1) – **rozstrzygnął: silnik działa w jednoplikowym `AdMng.exe`, folder nie jest potrzebny** (rozdział
   „Układ publikacji”). Powód: tylko silnik w procesie pozwala poleceniom AD Managera korzystać z własnej sesji okna (ten
   sam DC i to samo konto, bez ponownego logowania), przekazywać obiekty do siatki programu i pracować z własnym edytorem
   wiersza; zewnętrzne `pwsh.exe` nie daje żadnej z tych rzeczy (rozdział „Rozważone alternatywy”). Cena: kod działający
   w FullLanguage działa w procesie AD Managera i może sięgnąć do jego pamięci, także do sesji okna i jej poświadczeń (punkt
   3); nie mógłby tego tylko silnik poza procesem z mostem do odczytu – audyt R1 zaleca ponownie to rozważyć, zanim R3
   umieści poświadczenia w sesji.
2. **Jeden runspace na okno (D2).** Runspace nigdy nie jest współdzielony między oknami połączeń; powstaje i jest
   zwalniany razem z zakresem DI okna (`docs/dev/multi-window.md`). Własny `PSHost` / `PSHostUserInterface` kieruje tekst,
   pytania i potwierdzenia do okna. **Polecenia sesji to lista dozwolonych** – jedna lista w kodzie
   (`src/AdMng.PowerShell/Guards/CommandGuards.cs`), porównywana testem z żywą sesją: polecenia AD Managera, cmdlety
   silnika do pracy z obiektami, formatowania, zmiennych, pomocy i odczytu plików, cztery zamienniki z mniejszą liczbą
   parametrów (`ForEach-Object` bez `-Parallel` i `-AsJob`, `Tee-Object` tylko do zmiennej, `New-Object` bez COM,
   `Start-Transcript` tylko do pliku wskazanego w `-Path`) oraz – dla nazw wpisywanych z przyzwyczajenia – odmowy z
   komunikatem (`Start-Job`, `Import-Module`, `Invoke-Command`, `Invoke-Expression`, `Get-Credential`, `Start-Process`,
   `Out-GridView`, `Export-Csv`, `Export-Clixml`, `Out-File`, `Set-Content`, `Add-Content`). Nie ma zadań w tle,
   równoległych runspace'ów, sesji zdalnych, zdarzeń, debugera, aktualizacji pomocy, zmian polityki ani poleceń
   zapisujących pliki (plik zapisują tylko `Export-AdmList` i `Start-Transcript -Path`). To, co trafia do silnika bez
   nazwy polecenia, jest odrzucane z tekstu, zanim ruszy: `using module` / `using assembly`, operator tła `&` i
   przekierowanie do pliku; tak samo w całości tekst za długi albo zagnieżdżony zbyt głęboko, by przejść go bezpiecznie
   (przepełnienie stosu .NET w parserze albo w kompilatorze silnika zakończyłoby cały program). Pliki skryptów się nie uruchamiają: sesja nie zna żadnego, hak wyszukiwania odrzuca plik
   znaleziony z bloku skryptu, a na Windows własny zarządca autoryzacji okna odrzuca każdy `.ps1` i `.psm1` niezależnie od
   polityki wykonywania (ta pozostaje `Restricted`; na Linuksie, gdzie okna się nie dostarcza, silnik nie pyta zarządcy
   autoryzacji). Żadne zadanie nie trwa dłużej niż polecenie, które je uruchomiło: zadanie pozostawione w sesji jest
   zatrzymywane, gdy polecenie się kończy, po Zatrzymaj i przy zwolnieniu sesji. Nie ładujemy profili (`$PROFILE` nie jest
   nigdy wczytywany), nie ma PSReadLine, automatyczne ładowanie modułów jest **wyłączone**, a `Import-Module` i `using
   module` działają tylko dla modułów z listy, która w R1 jest **pusta** (RSAT przychodzą w R3, moduły Microsoft 365 ze
   ścieżką C). Powód: okno ma robić to, co zna program, a nie to, co przyniósł profil, moduł w `PSModulePath` albo skrypt
   na dysku, i nie może dawać sposobu na uruchomienie kodu poza swoim trybem języka ani poza własnym czasem życia – audyt
   R1 znalazł `ForEach-Object -Parallel` działające w FullLanguage w oknie ograniczonym i zadania `-AsJob` przeżywające
   okno, gdy sesją była domyślna sesja silnika bez dwóch nazw.
3. **Tryb języka (D3): ConstrainedLanguage domyślnie.** Polecenia AD Managera działają w nim, bo to skompilowany kod
   programu. Przełącznik okna „Tryb zaufany (FullLanguage)” operator włącza po potwierdzeniu, które mówi, na co pozwala
   (dowolny kod .NET, zapisy z pominięciem planu zmian); tryb widać w tytule okna i w trwałej notatce, dopóki jest
   włączony; nie jest zapamiętywany między oknami ani sesjami. Przy blokadzie systemu (WDAC / AppLocker,
   `SystemPolicy.GetSystemLockdownPolicy`) okno zostaje w ConstrainedLanguage, a przełącznik jest wyłączony z podaniem
   powodu. W ConstrainedLanguage żadne polecenie okna nie uruchamia kodu w innym runspace ani w innym trybie języka (każdą
   znaną drogę – `-Parallel`, `[powershell]`, `[runspacefactory]`, `[scriptblock]::Create`, zadania i drogi audytu – próbują
   `LanguageModeTests` i `EscapeTests`).
   Tryb zaufany zmienia tylko tryb języka: nie dodaje poleceń, a jego kod .NET działa w procesie AD Managera, gdzie może
   sięgnąć do wszystkiego – także do sesji katalogu okna i jej poświadczeń. **To ochrona przed pomyłką, nie granica
   bezpieczeństwa** (README, „Bezpieczeństwo i dane”): w FullLanguage dowolny kod .NET omija plan zmian, a operator może
   i tak uruchomić `pwsh.exe`. **Wyłączenie trybu zaufanego daje oknu nowy runspace** w ConstrainedLanguage (z nowym
   hostem; stary jest zamykany): funkcja, alias, blok skryptu w zmiennej albo składowa `$Host` zdefiniowane w FullLanguage
   działały wcześniej dalej w FullLanguage w oknie, które mówiło ConstrainedLanguage (audyt R1, PS-02). Zmienne okna
   znikają razem ze starym runspace'em; mówi o tym pytanie przy włączaniu i okno przy wyłączaniu. Gdy nowego runspace'u
   nie da się utworzyć, okno nie zostaje w trybie zaufanym, tylko mówi, że PowerShella nie można uruchomić. Wątek, runspace
   albo licznik czasu, które taki kod zostawi, działają dalej po wyłączeniu trybu i po zamknięciu okna, a składowa, którą
   dodał do typu .NET albo obiektu całego procesu (`Add-Member` na `[string]`), daje się wywołać w FullLanguage także
   z okna ograniczonego – do zamknięcia programu; blok skryptu wywołany przez taki licznik po zamknięciu okna nie ma już
   runspace'u i kończy się błędem, który program traktuje jako obsłużony (obsługa nieobsłużonych wyjątków procesu, ustawiana
   przy starcie programu), więc nie zamyka programu ani pozostałych okien.
4. **Nazwy i skrót (D4): przedrostek `Adm`.** R1 buduje `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`,
   `Get-AdmComputer`, `Get-AdmGroupMember` (`-Recursive` dla członkostwa zagnieżdżonego), `Search-AdmObject`,
   `Out-AdmGrid`, `Open-AdmObject` (otwiera właściwości obiektu w programie według DN lub `objectGUID`) i
   `Export-AdmList` (CSV z ochroną przed formułami jak w eksporcie list, tylko do jawnej ścieżki albo okna *Zapisz jako*).
   Nazwy poleceń zapisu z README pozostają zarezerwowane dla R2. Skrót okna: **Ctrl+Shift+P** (żaden istniejący skrót go
   nie używa); pozycja menu to *Narzędzia → PowerShell…*, a paleta poleceń dostaje wpis.
5. **Edytor wiersza (D5): własna kontrolka WPF bez nowej zależności zewnętrznej.** Uzupełnianie przez
   `CommandCompletion.CompleteInput` silnika w runspace okna (polecenia, parametry, zmienne; ścieżek i plików **nie**
   uzupełniamy w R1) oraz uzupełnianie parametrów poleceń AD Managera (nazwy atrybutów ze schematu sesji w pamięci, DN z
   tego, co sesja już zna – nigdy nowy odczyt katalogu na każdy klawisz); kolorowanie z tokenów parsera
   (`Parser.ParseInput`); wiele wierszy (Shift+Enter wstawia wiersz, Enter wykonuje kompletne polecenie, Ctrl+C i
   przycisk Zatrzymaj przerywają potok); historia poleceń **tylko w pamięci okna** (strzałki, Ctrl+R); wiersz z hasłem
   zwykłym tekstem (`-AsPlainText`, `ConvertTo-SecureString '…' -AsPlainText`, parametr z *Password* w nazwie z literałem)
   nie trafia do historii.
6. **Wyniki (D6).** Wyniki są zwykłymi obiektami PowerShell; okno pokazuje je silnikiem formatowania PowerShell
   (`Out-String`) jako tekst z rozpoznanymi DN, które są klikalne (*Otwórz w programie*). Błędy, ostrzeżenia i komunikaty
   szczegółowe mają kolory okna i znaczki linii stanu. `Out-AdmGrid` wysyła obiekty do siatki list programu: obiekty stają
   się wierszami, kolumny wynikają z ich właściwości, a sortowanie, filtry, kopiowanie i eksport CSV siatki działają.
   Nic nie jest nigdzie zapisywane, dopóki operator nie wyeksportuje.
7. **Zasady PowerShell z GPO (D7).** Przy otwarciu okno odczytuje zasady PowerShell według reguł samego silnika:
   PowerShell Core (`HKLM`/`HKCU\Software\Policies\Microsoft\PowerShellCore`) oraz, dla zasady, w której własnym kluczu
   włączono `UseWindowsPowerShellPolicySetting`, Windows PowerShell (`Software\Policies\Microsoft\Windows\PowerShell`) –
   transkrypcję (`EnableTranscripting`, `OutputDirectory`), rejestrowanie bloków skryptów, rejestrowanie modułów
   i `ExecutionPolicy`, każdą z pierwszej gałęzi rejestru, która ją ustawia, potem z plików `powershell.config.json` –
   oraz stan blokady systemu. Trwała notatka (Uwaga linii stanu) przed pierwszym poleceniem mówi wprost, co i gdzie jest
   zapisywane („transkrypcja jest włączona zasadą: polecenia i wyniki zapisuje Windows w <katalog>”). Program nie próbuje
   obejść żadnej zasady i sam nie ustawia rejestrowania bloków skryptów (D-B po audycie, PS-04): gdy nie ustawia go żadna zasada
   ani plik, silnik zapisuje w swoim dzienniku zdarzeń bloki skryptów, które uznaje za podejrzane, jako ostrzeżenie (zdarzenie
   4104), jak w każdym PowerShell 7 – notatka mówi, co rejestrują zasady, a o tym domyślnym zachowaniu silnika nie wspomina;
   opisuje je przewodnik. Czytnik rejestru stoi za interfejsem (implementacja Windows w `AdMng.App`, atrapa w testach).
8. **Nic na dysku, hasła, jeden DC, interfejs nie blokuje (D8).** Zawartość okna, wyniki i historia żyją tylko w pamięci
   (zasada 1). Środowisko silnika ustawiamy **przed** jego pierwszym użyciem: `POWERSHELL_TELEMETRY_OPTOUT=1`,
   `POWERSHELL_DIAGNOSTICS_OPTOUT=1`, `POWERSHELL_UPDATECHECK=Off`, wyłączone automatyczne ładowanie modułów i pamięć
   podręczna analizy modułów skierowana na urządzenie puste; test „żadnych plików” uruchamia polecenia w procesie z
   `HOME`, `USERPROFILE`, `LOCALAPPDATA`, `APPDATA`, `TEMP` i `XDG_*` wskazującymi pusty folder, proces potomny podaje
   foldery, w których jego silnik trzyma pliki (odczytane z samego silnika), a test sprawdza, że obserwuje każdy z nich i
   że w żadnym nie zostaje plik – na Linuksie wszystkie leżą w pustym folderze (zaobserwowane); na Windows silnik znajduje
   swoje foldery przez interfejs folderów znanych, którego zmienne nie muszą przesuwać, więc tam test porównuje też
   foldery PowerShella w profilu użytkownika przed i po uruchomieniu (ta część jeszcze nie przeszła na Windows). Na
   Windows silnik sam z siebie zapisuje dwa pliki, których nie da się wyłączyć: jego sprawdzenie AppLockera zapisuje w
   `%TEMP%` pliki `__PSScriptPolicyTest_<losowo>.ps1` i `.psm1` (wiersz komentarza, żadnych danych z katalogu) i od razu
   je usuwa, raz na proces (odczytane z kodu silnika); test dopuszcza dokładnie te pliki. W ConstrainedLanguage nie jest
   znany żaden sposób, w jaki skrypt dosięgnąłby sesji okna albo jej poświadczeń: nie przenosi ich żadna zmienna, parametr
   ani składowa `$Host` (polecenia AD Managera znajdują swoje okno w rejestrze silnika, według runspace'u), żadne polecenie
   okna nie uruchamia kodu w innym trybie języka (punkt 3), droga audytu przez `ForEach-Object -Parallel` i drogi trzech
   późniejszych prób obejścia okna (red team, 12.7–12.9 notatki wykonawczej) są testami, które muszą pozostać odrzucone, a w R1 nie powstaje żaden `PSCredential` (zasada 2); w
   trybie zaufanym może to zrobić odbicie .NET (punkt 3). Filtry budujemy
   przez `AdMng.Core.Ldap.LdapFilter`, a DN przez `DistinguishedName` (zasada 3). Polecenia czytają wyłącznie z
   `IDirectorySession` okna – jeden DC, bez `-Server` i `-Credential` (zasada 4). Każde wywołanie katalogu jest
   asynchroniczne z tokenem anulowania potoku (`StopProcessing` go anuluje); wyniki są strumieniowane i stronicowane, 100
   000 wyników nie blokuje okna (zasada 6). Dopasowania wyrażenia regularnego Zatrzymaj nie przerywa, a `-match`,
   `-replace`, `-split`, `switch -regex`, `Select-String` i `[regex]` działają w ConstrainedLanguage, więc program, zanim
   powstanie pierwsze wyrażenie regularne procesu (inicjator modułu programu), daje każdemu wyrażeniu bez własnego limitu
   limit czasu 2 s: wzorzec z katastrofalnym nawracaniem kończy się błędem polecenia, zamiast trzymać okno i rdzeń procesora.
   Wyrażenie, któremu skrypt daje własny, dłuższy limit (`[regex]::new('…', 'None', [timespan]::FromHours(1))`, dozwolone w
   ConstrainedLanguage), i kod .NET trybu zaufanego mogą nadal utknąć: polecenie, które nie skończy się w ciągu 5 s od
   Zatrzymaj, sesja zostawia jego wątkowi (wątkowi w tle, który nie wstrzymuje zamknięcia programu) i następne polecenie
   uruchamia od razu w nowym runspace, bez zmiennych i funkcji starego, a okno to mówi.
9. **Zapisy (D9): w R1 nie ma żadnych.** Nie istnieje żadne polecenie `Set-`/`New-`/`Remove-`/`Move-`/`Rename-`/`Add-`/
   `Enable-`/`Disable-`/`Unlock-`/`Reset-Adm*`, nie ma dysku `AD:` ani RSAT; test sprawdza, że lista zarejestrowanych
   poleceń AD Managera zawiera tylko polecenia odczytu z punktu 4 (i `Get-AdmVersion`). Okno ma miejsce na oczekujący plan,
   który doda R2.

## Konsekwencje

- **Rozmiar.** Pakiet silnika z zależnościami powiększa program o ok. 19 MB (pojedynczy plik z kompresją) albo ok. 54 MB
  (bez kompresji); pomiar publikacji jednoplikowej win-x64 wykonanej na Linuksie (czyli bez uruchomienia) dał +17,0 MB i
  +48,4 MB (puste programy: 37,6 MB i 73,5 MB). Pierwszy runspace startuje w ok. 0,4 s, a każdy następny w ok. 10 ms;
  pamięć zarządzana rośnie o ok. 2 MB, a zestaw roboczy procesu o kilkadziesiąt MB (ładowane zestawy silnika) – pomiary w
  notatce wykonawczej. To koszt zaakceptowany przez właściciela.
- **Wydanie przy każdej poprawce bezpieczeństwa.** Każda poprawka PowerShell (mniej więcej co miesiąc) oznacza nową wersję
  `Microsoft.PowerShell.SDK` w `Directory.Packages.props` i nowe wydanie AD Managera. Audyt NuGet (`NuGetAudit`, tryb
  `all`, ostrzeżenia jako błędy) i tak zatrzyma kompilację przy znanej luce w pakiecie. LTS 7.6 jest wspierany do listopada
  2028; 7.7 wymaga .NET 11, więc zmiana linii idzie razem ze zmianą platformy.
- **EDR i SOC.** Systemy EDR mogą zgłaszać „silnik PowerShell w procesie innym niż `powershell.exe`” (np. reguła Sigma dla
  `System.Management.Automation.dll`). Odpowiedzią są podpisany `AdMng.exe` (faza 7, certyfikat z P2) i informacja dla
  zespołu bezpieczeństwa; dokumentacja dla nich jest częścią R1. AMSI sprawdza każdy blok skryptu, a rejestrowanie bloków
  skryptów i transkrypcja z GPO obejmują okno – dlatego notatka przed pierwszym poleceniem (punkt 7). O rejestrowaniu bloków
  skryptów decydują Windows i zasady grupy, nie program (D-B): zespół bezpieczeństwa widzi w *PowerShellCore/Operational* to
  samo co przy zwykłym pwsh 7 – każdy blok skryptu przy włączonej zasadzie, żaden przy wyłączonej, a bez zasady te, które silnik
  uznaje za podejrzane (ostrzeżenie 4104; `-Properties` poleceń odczytu okna zawiera jedno ze słów silnika, a odczyt
  właściwości po nazwie ze zmiennej, np. `$u.$a`, silnik oznacza już przy analizie polecenia), o ile zarejestrowano dostawcę
  zdarzeń PowerShell 7. Jedyna różnica: ustawienie rejestrowania w pliku `powershell.config.json` w folderze zainstalowanego
  PowerShell 7 obowiązuje tylko `pwsh.exe`, bo silnik okna czyta plik obok `AdMng.exe`.
- **Granica bezpieczeństwa.** ConstrainedLanguage, lista dozwolonych poleceń, pusta lista modułów i odrzucanie plików skryptów
  chronią przed pomyłką, a nie przed złośliwym operatorem; jedyną granicą są prawa konta w AD. Zmiany wykonane z pominięciem planu nie
  mają audytu ani cofnięcia w programie, ale Watchdog widzi je jak każdą zmianę w AD.
- **Stan procesu.** Środowisko silnika i polityka wykonywania są ustawieniami procesu: `ISS.ExecutionPolicy` w Windows
  ustawia politykę zakresu *Process*, która według dokumentacji PowerShell jest przechowywana w zmiennej środowiskowej
  `PSExecutionPolicyPreference` procesu AD Managera (nie sprawdzone na Windows w pierwszej części R1), więc dziedziczą ją
  uruchamiane później procesy potomne; AD Manager nie uruchamia żadnego procesu PowerShell. Na Linuksie
  `ISS.ExecutionPolicy` nie jest obsługiwane i nie jest ustawiane (okno jest tam tylko środowiskiem testowym).
- **Silnik działa na Linuksie.** Projekt silnika i jego testy działają i są testowane na Linuksie i Windows; okno WPF –
  tylko na Windows. Dzięki temu cała logika (host, tryb języka, historia, uzupełnianie, polecenia) ma testy jednostkowe
  poza interfejsem.
- **Dalsze etapy.** R2 doda polecenia zapisu przez plan zmian (okno ma na to miejsce) bez zmiany tej decyzji; R3 doda moduły
  RSAT przez listę modułów z punktu 2. Plik `powershell.config.json` obok `AdMng.exe` (wyłączenie transkrypcji i
  rejestrowania modułów ustawionych w plikach użytkownika) jest częścią układu publikacji (rozdział „Układ publikacji”).

## Układ publikacji

Wynik części 4 R1, sprawdzony w CI na Windows (krok „Smoke test published app” publikowanego jednoplikowego `AdMng.exe`):

- **Silnik działa w jednoplikowym, samodzielnym `AdMng.exe`.** Test dymny tworzy runspace, wykonuje `1 + 1`, `Get-AdmVersion`, sprawdza
  tryb języka (ConstrainedLanguage), politykę wykonywania (`Restricted`), listę dziesięciu poleceń, odrzucenie `Import-Module` i
  pomoc polecenia (od poprawek po audycie R1 także liczbę poleceń sesji względem listy dozwolonych i `ForEach-Object` okna); pierwszy runspace procesu trwa ok. 0,5 s (523 ms), drugi ok. 10 ms (9 ms). Zestaw `System.Management.Automation`
  jest w pliku; silnik, nie mając jego lokalizacji (`Assembly.Location` jest pusty), przyjmuje za `$PSHOME` folder programu
  (zaobserwowane: `$PSHOME` = folder `AdMng.exe`). **Folder z plikami silnika obok programu nie jest więc potrzebny** i nie
  jest stosowany; zapasowe rozwiązanie z punktu 1 pozostaje opisane na wypadek zmiany silnika.
- **Obok `AdMng.exe` trafiają tylko pliki, których .NET nie umieszcza w pliku:** natywne biblioteki silnika (`pwrshplugin.dll`,
  `PowerShell.Core.Instrumentation.dll`, `getfilesiginforedist.dll`, `microsoft.management.infrastructure.native.unmanaged.dll`),
  `build.manifest(.sig)`, manifesty modułów (`runtimes/*/lib/net10.0/Modules`; nic ich nie ładuje – domyślny stan sesji wiąże polecenia
  bezpośrednio; archiwum wydania bierze tylko pliki z głównego folderu publikacji, więc nie ma ich wcale, a przepływ wydania
  uruchamia test dymny na rozpakowanym archiwum), pomoc poleceń (`AdMng.PowerShell.dll-Help.xml`, wskazywana przez `AppContext.BaseDirectory`, bo w jednym pliku
  `Assembly.Location` jest pusty) i **`powershell.config.json`**: wyłącza transkrypcję, rejestrowanie modułów oraz warstwę
  zgodności z Windows PowerShell 5.1. Silnik czyta go z `$PSHOME`, a ustawienie w nim wygrywa z tym samym ustawieniem w pliku
  użytkownika (sprawdzone na Linuksie w części 1 i testem dymnym opublikowanego pliku, który sprawdza też, że `$PSHOME` to folder
  `AdMng.exe`); zasady komputera (GPO) mają pierwszeństwo przed oboma, a okno mówi o nich przed pierwszym poleceniem.
  Rejestrowania bloków skryptów plik nie ustawia (D-B po audycie, PS-04; wcześniej je wyłączał, co wyciszało też własne
  rejestrowanie podejrzanych bloków skryptów przez silnik): decydują o nim zasady grupy, potem własny plik użytkownika, jak
  w zwykłym pwsh (`EngineBoundaryTests`, `ScriptBlockLoggingTests`).
- **Rozmiar** (MB = 10^6 bajtów; to samo polecenie publikacji co w CI, `-r win-x64 --self-contained -p:PublishSingleFile=true`,
  uruchomione na Linuksie dla commitu 0.16.0 i dla czoła R1; CI wypisuje te same 173,5 jako „MB”, a to MiB). `AdMng.exe` (jeden
  plik, bez kompresji): 143,5 MB bez silnika, 181,9 MB z silnikiem, czyli **+38,4 MB**; cały folder publikacji 164,7 MB →
  204,3 MB (**+39,6 MB**); archiwum artefaktu CI 68,8 MB → 82,4 MB (**+13,6 MB**, pomiar CI). Pomiar części 1 (puste programy, +48,4 MB bez kompresji) był
  wyższy, prawdopodobnie dlatego, że program ma już część tego, co przynosi silnik (biblioteki .NET są w pliku raz).
- **Pakiet Watchdoga nie zawiera silnika.** Żaden projekt Watchdoga nie odwołuje się do `AdMng.PowerShell` ani do
  `Microsoft.PowerShell.SDK` (test `EngineBoundaryTests`), skrypty pakietu Debian go nie znają, a CI sprawdza, że wynik publikacji
  Watchdoga nie ma `System.Management.Automation*.dll`. Wynik publikacji AD Managera nie ma `sni.dll`.

## Licencje pakietów

Pakiet `Microsoft.PowerShell.SDK` 7.6.10 jest na licencji **MIT**. Domknięcie jego zależności (85 pakietów według
`project.assets.json` projektu silnika, odtworzone 2026-11-16) sprawdzono pod kątem wymogu `CLAUDE.md` (licencja
zatwierdzona przez OSI i zgodna z Apache-2.0):

- **MIT:** 79 pakietów (rodzina `System.Management.Automation` i `Microsoft.PowerShell.*`, `System.*`,
  `Microsoft.Extensions.*`, `Newtonsoft.Json`, `Humanizer.Core`, `JsonSchema.Net` i inne).
- **BSD-2-Clause:** `Markdig.Signed` 0.44.0 (renderowanie Markdown w `Show-Markdown`) – zatwierdzona przez OSI, zgodna.
- **MIT (adres licencji corefx):** `runtime.native.System.Data.SqlClient.sni` 4.4.0 – pakiet bez plików binarnych.
- **Licencja Microsoft .NET Library (niezatwierdzona przez OSI, ale z prawem redystrybucji):**
  `runtime.win-x64`, `runtime.win-x86` i `runtime.win-arm64.runtime.native.System.Data.SqlClient.sni` 4.4.0 – natywny
  `sni.dll`, potrzebny tylko `System.Data.SqlClient`, który ciągnie pakiet SDK. Żadne polecenie AD Managera ani żadne z
  poleceń PowerShell, które okno udostępnia, go nie używa. **Decyzja:** te pakiety są wyłączone z wyniku kompilacji i
  publikacji (`ExcludeAssets="all"` w pliku `.csproj` każdego programu, który używa silnika; wspólny plik
  `src/AdMng.PowerShell/PowerShellEngineConsumer.props`), a test w `AdMng.PowerShell.Tests` sprawdza, że w wyniku nie ma
  `sni.dll`. Skutek: skrypt w trybie zaufanym, który połączy się z SQL Server przez `System.Data.SqlClient`, nie
  znajdzie biblioteki natywnej. Jest to odstępstwo od założenia „wszystko MIT”, zgłoszone właścicielowi.
- **Licencja Microsoft (plik):** `Microsoft.Management.Infrastructure.Runtime.Win` 3.0.0 jest już w programie od K1
  (`AdMng.Remote`); ta decyzja nie zmienia sytuacji.

## Rozważone alternatywy

1. **`pwsh.exe` poza procesem w terminalu (ConPTY), z PSReadLine.** Mniejszy program, pełna wygoda konsoli i brak własnego
   edytora wiersza, ale bez dostępu do sesji i poświadczeń okna (potrzebne byłoby drugie logowanie), bez obiektów w siatce
   programu i bez kontroli nad tym, co trafia do historii PSReadLine i na dysk. Odrzucona.
2. **Windows PowerShell 5.1.** Nie działa na .NET 10, wymaga zainstalowanego komponentu systemu i nie daje się osadzić w
   procesie na .NET 10. Odrzucona.
3. **FullLanguage domyślnie.** Dowolny kod .NET, w tym zapisy z pominięciem planu zmian, byłby dostępny od pierwszego
   polecenia; ConstrainedLanguage z przełącznikiem zaufanego trybu daje te same możliwości na wyraźne życzenie operatora.
   Odrzucona.
4. **Jeden runspace dla wszystkich okien.** Stan jednego okna (zmienne, `$PSDefaultParameterValues`, sesja) przeciekałby do
   innego połączenia, a runspace nie dałoby się zwolnić razem z oknem. Odrzucona; jedno okno = jeden runspace.
5. **Dowolne moduły i skrypty z dysku** (`Import-Module`, pliki `.ps1`, folder modułów użytkownika). Odrzucona na rzecz
   listy modułów (pusta w R1) i odrzucania plików skryptów przez samo okno (punkt 2), niezależnie od polityki wykonywania:
   okno ma zachowywać się tak samo na każdej stacji.
6. **Własny język poleceń zamiast PowerShell.** Pozwalałby uniknąć wielu zagadnień, ale nie dawałby potoku obiektów,
   `Where-Object`/`Select-Object`/`Group-Object` ani skryptów, których administratorzy już używają. Odrzucona.
7. **Nowy pakiet zewnętrzny na edytor wiersza** (np. kontrolka edytora kodu). Nowa zależność z licencją do sprawdzenia i
   utrzymania, a potrzebne funkcje (kolorowanie z tokenów, uzupełnianie, wiele wierszy) da się zbudować na tym, co WPF i
   silnik już mają. Odrzucona; pakiet tylko wtedy, gdy etap udowodni, że jest niezbędny, z licencją w ADR.
8. **Domyślna sesja silnika bez kilku nazw** (lista zakazów: `CreateDefault()` bez `Start-Job` i `Import-Module`). Tak
   zbudowano R1 na początku. Audyt R1 znalazł wyjścia wśród tego, czego taka lista nie nazywa – `ForEach-Object -Parallel`
   uruchamia runspace'y w FullLanguage, zadania `-AsJob` przeżywają okno, `using module`, operator tła i przekierowanie
   w ogóle nie szukają nazwy, a wśród jej 233 cmdletów są takie, które zapisują pliki, uruchamiają procesy i zmieniają
   sesję. Odrzucona na rzecz listy dozwolonych z punktu 2.
9. **Silnik poza procesem z mostem do odczytu** (polecenia okna proszą AD Managera o odczyty). Jedyny układ, w którym kod
   trybu zaufanego nie sięga do pamięci AD Managera, sesji okna i jej poświadczeń; tygodnie pracy, nowy ADR i brak obiektów
   wspólnych z siatką programu. Niewybrany dla R1 (tryb zaufany włącza tylko operator, po pytaniu); do ponownego
   rozważenia, zanim R3 przekaże modułowi `ActiveDirectory` obiekt `PSCredential` (punkt 1).
