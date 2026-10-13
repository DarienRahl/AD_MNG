# ADR-0001: WPF na .NET 10 z motywem Fluent

- Status: przyjęta
- Data: 2026-10-13

## Kontekst

Aplikacja ma zastąpić przystawkę `dsa.msc` na stacjach administratorów podłączonych do domeny. Musi wyglądać
nowocześnie (Windows 11, tryb jasny/ciemny), być płynna przy dziesiątkach tysięcy obiektów i dać się łatwo
wdrożyć (jeden plik `.exe`). Rozważane opcje: WPF (.NET 10), WinUI 3 (Windows App SDK), Avalonia, aplikacja webowa
(model Windows Admin Center).

## Decyzja

WPF na .NET 10 (LTS) z wbudowanym motywem Fluent (`Application.ThemeMode`), MVVM (CommunityToolkit.Mvvm),
Generic Host + DI. Warstwa ViewModeli w osobnym projekcie bez zależności od WPF.

## Uzasadnienie

- Dojrzałe, wirtualizowane `DataGrid`/`TreeView` – kluczowe dla list tysięcy obiektów; WinUI 3 nie ma dojrzałego
  DataGrid, a wdrożenie wymaga runtime Windows App SDK/MSIX.
- Motyw Fluent w WPF (.NET 9+) daje wygląd Windows 11, tryb ciemny i akcent systemowy bez bibliotek firm trzecich.
- Pełny dostęp do Win32 (DsGetDcName, Kerberos/SSPI przez wldap32) – wymagane dla komputera w domenie.
- Publikacja self-contained single-file, bez instalatora runtime.
- Aplikacja webowa wymagałaby serwera, delegacji Kerberos (double hop) i dodatkowej powierzchni ataku – sprzeczne z
  wymaganiem „uruchamiane z komputera w domenie”.

## Konsekwencje

- Aplikacja działa tylko na Windows 10/11/Server; logika (Core, Directory, Presentation) jest wieloplatformowa i
  testowana na Linuksie, a WPF kompilowany wszędzie (`EnableWindowsTargeting`) i testowany dymnie na Windows CI.
- Kontrolki nieobjęte motywem Fluent wymagają własnych stylów.
