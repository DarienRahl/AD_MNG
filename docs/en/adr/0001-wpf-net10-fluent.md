**English** · [Polski](../../adr/0001-wpf-net10-fluent.md)

# ADR-0001: WPF on .NET 10 with the Fluent theme

- Status: Accepted
- Date: 2026-10-13

## Context

The application is to replace the `dsa.msc` snap-in on the workstations of administrators connected to the domain. It
must look modern (Windows 11, light/dark mode), stay fluid with tens of thousands of objects, and be easy to deploy (a
single `.exe` file). Options considered: WPF (.NET 10), WinUI 3 (Windows App SDK), Avalonia, a web application (the
Windows Admin Center model).

## Decision

WPF on .NET 10 (LTS) with the built-in Fluent theme (`Application.ThemeMode`), MVVM (CommunityToolkit.Mvvm),
Generic Host + DI. The view model layer lives in a separate project with no dependency on WPF.

## Rationale

- Mature, virtualized `DataGrid`/`TreeView` – crucial for lists of thousands of objects; WinUI 3 has no mature
  DataGrid, and deployment requires the Windows App SDK runtime/MSIX.
- The Fluent theme in WPF (.NET 9+) provides the Windows 11 look, dark mode and the system accent color without
  third-party libraries.
- Full access to Win32 (DsGetDcName, Kerberos/SSPI through wldap32) – required for a domain-joined computer.
- Self-contained single-file publishing, with no runtime installer.
- A web application would require a server, Kerberos delegation (double hop) and an additional attack surface –
  contrary to the requirement "run from a domain-joined computer".

## Consequences

- The application runs only on Windows 10/11/Server; the logic (Core, Directory, Presentation) is cross-platform and
  tested on Linux, while WPF is compiled everywhere (`EnableWindowsTargeting`) and smoke-tested on Windows CI.
- Controls not covered by the Fluent theme require custom styles.
