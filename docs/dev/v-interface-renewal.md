# Track V – Interface renewal (look, motion, icons) – design note

Status: **proposal for the owner** (2026-11-10). Nothing in this note is implemented. It plans a renewal of the AD
Manager interface so that the look partly, and the ease of use strongly, match the reference the owner named: *Harden
System Security* and *AppControl Manager* (WinUI 3) – "it is about the animations and so on" – with a new icon set.

Sources and how to read the numbers:

- **The study** = the static reading of the reference repository done for this plan (about 640 lines, kept outside the
  repository; cited here as "study §n" with the reference's own file paths: `ACM/` = AppControl Manager, `HSS/` =
  Harden System Security, `CC/` = their shared code). Nothing of the reference was built or run, so what is said about
  how it *feels* is inferred from its XAML and C#.
- **Our tree** = this repository at commit `9f74ca3` (release 0.15.0 plus tidy-ups), counted with `wc`/`grep` on the
  sources (build output excluded).
- **The SDK** = the WPF runtime pack 10.0.12 in the NuGet cache of the build machine (`PresentationFramework.dll`,
  `PresentationFramework.Fluent.dll`), inspected by decompiling and by reading its theme dictionaries.
- Sizes and durations marked **(ours)** are proposals of this note; everything else carries its source.
- No Windows machine was available: nothing below was seen on a screen. What that leaves open is listed in
  [Not established](#not-established).

## Baseline: what we have today

| Fact | Value | Where |
|---|---|---|
| XAML of the WPF app | 86 files, 31,607 lines (39 windows, 42 user controls, 4 dictionaries, `App.xaml`) | `src/AdMng.App` |
| Code-behind | 82 files, 6,631 lines | `src/AdMng.App/**/*.xaml.cs` |
| Other App code (services, behaviours, converters, smoke test) | 81 files, 11,751 lines | `src/AdMng.App` |
| Largest views | `PropertiesWindow.xaml` 2,340 · `CorrespondenceWindow.xaml` 1,742 · `MainWindow.xaml` 1,506 (+744 code-behind) · `BatchEditorWindow.xaml` 1,505 · `AccountReviewWindow.xaml` 1,116 · `SettingsWindow.xaml` 1,037 | `src/AdMng.App/Views`, `Correspondence` |
| Presentation layer (no WPF reference, `net10.0`) | 498 files, 122,036 lines; of these 208 `*ViewModel*` files with 72,197 lines | `src/AdMng.Presentation` |
| View-model tests | 2,077 test methods | `tests/AdMng.Presentation.Tests` |
| UI tests | 70 files, 16,068 lines, 312 test methods; 43 of them in 10 `ScreenshotTests*` files; 12 README pictures | `tests/AdMng.App.UiTests`, `docs/assets/screenshots` |
| How UI tests find elements | in process on one STA thread (`Infrastructure/WpfTestHost.cs`); generated `x:Name` fields (`InternalsVisibleTo`), `Descendants<T>()` (189 uses) with `.Name ==` (107 uses); one `AutomationId` in the whole app | same |
| Names and descriptions in XAML | 1,954 `x:Name`, 1,006 `AutomationProperties.Name`, 491 `ToolTip` attributes | `src/AdMng.App` |
| UI strings | 8,042 keys, English and Polish | `src/AdMng.Presentation/Resources/Strings*.resx` |
| Styles | 39 resource keys `Ad.*` (styles, converters, templates) in `Themes/Controls.xaml`, 12 keys in `Themes/Watchdog.xaml`; banners `Ad.ErrorBar` 47 uses in 34 files, `Ad.WarningBar` 59 in 41; `Ad.Card` 66 in 38 | `src/AdMng.App/Themes` |
| Colours | 0 literal hex colours in XAML – all brushes come from Fluent keys | `src/AdMng.App` |
| Literal sizes | over 100 literal `FontSize` attributes (10, 11, 12, 13, 15 …), 16 literal `CornerRadius` (2, 3, 4, 6, 9) | `src/AdMng.App` |
| Lists | 91 `GridView` lists in 43 files, 2 `DataGrid`; `ProgressBar` 66 uses in 43 files | `src/AdMng.App` |
| Motion | no `Storyboard`, animation or `VisualStateManager` of our own anywhere; no read of `SystemParameters.ClientAreaAnimation` or `HighContrast` | `src/AdMng.App` |
| Icons | Segoe Fluent Icons glyphs (`Controls/Glyphs.cs`, about 60 glyphs), drawn for objects by `Controls/ObjectIconView.cs` with disabled/locked badges | `src/AdMng.App/Controls` |
| Shell | menu bar (5 menus, 131 `MenuItem`s), toolbar with address and search box, tree, document tabs, details pane, status bar with activity popup, toasts, Ctrl+K palette (about 60 registered commands, 93 command ids in XAML) | `Views/MainWindow.xaml`, `Presentation/Shell` |
| Packages of the App | `Microsoft.Extensions.Hosting`, `CommunityToolkit.Mvvm`, `Microsoft.Web.WebView2` | `src/AdMng.App/AdMng.App.csproj` |
| Shipping form | `AdMng-<version>-win-x64.zip`: one self-contained single-file `AdMng.exe` plus a few native libraries; not signed yet | `README.md`, *Download and run* |
| Supported systems | Windows 10 22H2, Windows 11, Windows Server 2019 and newer | `README.md`, *Download and run* |

What the installed WPF Fluent theme offers (SDK, verified):

- `ThemeMode` (`None`/`Light`/`Dark`/`System`) is still marked `[Experimental("WPF0001")]`; only
  `Services/ThemeService.cs` uses it.
- **Backdrop.** The internal `WindowBackdropManager` gives every themed window the Mica backdrop
  (`DWMSBT_MAINWINDOW`) when the OS is Windows 11 22H2 or newer, the window does not use `AllowsTransparency` and the
  app-context switch `DisableFluentThemeWindowBackdrop` is off; under High Contrast it removes the backdrop. There is
  **no public API** to choose another kind. Windows 10 22H2 and Windows Server 2019/2022 are below that build, so
  they get the plain theme background (`ApplicationBackgroundBrush`, `#FAFAFA` light / `#202020` dark).
- **High Contrast** has its own dictionary (`Themes/Fluent.HC.xaml`) with the same 616 colour keys as light and dark,
  loaded whenever `SystemParameters.HighContrast` is true.
- **Resources we can build on:** 20 `Card*`, 16 `Layer*`, 24 `Accent*`, 25 `SystemFill*` (success, caution, critical,
  attention with background variants), 45 `Text*`, 55 `Control*` keys; `ControlCornerRadius` 4, `OverlayCornerRadius`
  and `PopupCornerRadius` 8; text styles Caption 12, Body 14, BodyStrong 14, Subtitle 20, Title 28, TitleLarge 40,
  Display 68; `AccentButtonStyle`; icon font `Segoe Fluent Icons, Segoe MDL2 Assets`; text box minimum height 32,
  list row minimum height 40, button padding `11,5,11,6`.
- **Styled controls (59 dictionaries):** Button, Calendar, CheckBox, ComboBox, ContextMenu, DataGrid, DatePicker,
  Expander, Frame, GridSplitter, GridView, GroupBox, Hyperlink, Label, ListBox, ListView, Menu, PasswordBox,
  ProgressBar, RadioButton, RichTextBox, ScrollBar, Slider, StatusBar, TabControl, TextBox, ToggleButton, ToolBar,
  ToolTip, TreeView, Window and their parts.
- **Not there** (no control class and no style): NavigationView, InfoBar, InfoBadge, TeachingTip, BreadcrumbBar,
  AnimatedIcon, ContentDialog, AutoSuggestBox, ToggleSwitch, ProgressRing, a title-bar control, settings cards, and
  the WinUI theme transitions (entrance, reposition, add/delete).
- **Motion already in the theme:** storyboards in 14 control styles (Expander, ComboBox, ScrollBar, Calendar,
  TreeViewItem, ProgressBar and others); the usual durations are 167 ms, 160 ms, 100 ms and 250 ms; the Expander
  uses the key splines `0,0,0,1` and `1,1,0,1` – the same curves the reference uses for its expander. Button,
  list item, text box, check box and window styles contain no storyboard.
- **System settings WPF exposes:** `SystemParameters.ClientAreaAnimation`, `MinimizeAnimation`, `MenuAnimation`,
  `UIEffects`, `HighContrast`, `IsRemoteSession`, `IsSlowMachine`, `PowerLineStatus`.

## 1. Goals and measures

What gets easier for an administrator, each with the sign by which we will know:

| # | Goal | Measurable sign |
|---|---|---|
| G1 | Find any function by name instead of by menu | Ctrl+K, a few letters, Enter reaches every command, page and settings section: a test enumerates the command registry, the document types and the settings sections and finds each in the search index |
| G2 | Always know whether it worked | every document page and tool window has exactly one status line, in the same place (a XAML source test counts them); no operation started through the common helper ends without a status (view-model test) |
| G3 | Nothing looks frozen, everything long can be stopped | an operation shows "in progress" within 200 ms and, when it lasts longer than 1 s, can be cancelled from the button that started it; every such operation has a UI test "cancel returns control" |
| G4 | Recognise kind and state at a glance | every `ObjectIcon` value has a colour icon and a glyph fallback (test over the enum); a state is never carried by colour alone: badge plus text |
| G5 | Every page explains itself | every document type has a one-sentence description and a guide link in both languages; the link checker resolves each target in `docs/` and `docs/en/` |
| G6 | One look | no literal colour (already 0), `FontSize`, `CornerRadius` or `Duration` outside `Themes/` (today over 100 + 16 + 0); a XAML source test enforces it |
| G7 | Motion that explains and never delays | every animation is in the catalogue of section 5, takes its timing from tokens, and is off under the system setting; routine motion is at most about 300 ms |

What must not get worse:

- **Speed with thousands of rows.** Budgets in section 7; the baseline is measured before V1 changes anything.
- **Keyboard work.** Every gesture of today keeps working (a test compares the gesture list of the command
  registry before and after each stage); the menu access keys stay until the owner decides otherwise (question 3).
- **Tests.** The 312 UI tests and 2,077 view-model tests stay green; `x:Name`s are kept; a test is changed only
  when the element it looked at was replaced, never deleted without a successor; binding errors stay at zero.
- **The rules of `CLAUDE.md`.** In particular rule 1: the search index, the home page and "recent" lists hold
  nothing but what the settings may hold today (names, DNs and filters of saved views); status texts go to the
  diagnostic log only as value-free templates.
- **Delivery.** Still one self-contained `AdMng.exe` in a zip, no installer needed, no new native dependency.
- **Accessibility.** The count of automation names does not drop; High Contrast keeps working.

## 2. What we take from the reference

The study's main finding: the good feel comes mostly from **consistency** – every page has the same skeleton, every
command the same shape, every list the same model – and only then from motion, which is mostly stock WinUI (study,
summary 1–6). That half is independent of the UI technology and is where most of this plan's effort goes.

| Pattern (evidence in the reference) | What it gives the user | Our equivalent | Stage |
|---|---|---|---|
| Page skeleton: status line, toolbar card, content (`HSS/Pages/Protects/UAC.xaml:9-26`; `InfoBarV2` 221 mentions) | after two pages you know where to look | `StatusLine`, `ToolbarCard`, content – in this order on every `IShellDocument` page and tool window | V2, V3 |
| One status line with four severities; "in progress" not closable; every message also logged (`CC/Others/InfoBarSettings.cs:33-131`) | "did it work" is answered in one place and can be found again | `PageStatus` in Presentation; the control replaces `Ad.ErrorBar`/`Ad.WarningBar` (106 uses in 54 files); copy to the in-memory session log, value-free template to the diagnostic log | V2 |
| One command shape: disable, say what happens, run, result, re-enable (`HSS/ViewModels/MainWindowVM.cs:1103-1131`; one flag bound 392 times) | nothing can be double-clicked, nothing looks frozen | `OperationScope` helper in Presentation on top of today's activities | V2 |
| Cancellable button: Start, Cancel, Cancelling (`ACM/CustomUIElements/AnimatedCancellableButton.cs:895-916`); closing asks only while work is registered (`CC/TaskTracking.cs:27-50`) | stop where you started; no accidental exit | `ActionButton` with three states and a 150 ms content swap, no glow; the close question uses the existing activity list | V2 |
| Standard list toolbar: filter while typing, Total/Displayed, column chooser, export, selection menu (`HSS/CustomUIElements/MUnitListViewControl.xaml:198-512`; `ACM/CustomUIElements/ColumnSelector.xaml`) | every list behaves the same | `ListToolbar` over our `GridView` lists (91 in 43 files); reuses `ColumnChooserWindow` and the existing export | V3 |
| Copy everywhere (`ACM/Pages/Simulation.xaml:423-470`; Ctrl+C declared 22 times) | DNs and names go into tickets | already built (`docs/dev/phase1-copy.md`); V3 only audits new pages | V3 |
| Self-describing pages: description and "Guide" link (`CC/UI/IPageHeaderProvider.cs`; `ACM/MainWindow.xaml:1259-1319`) | the manual is one click away, at the right chapter | `PageHeader` with a description and a guide link per document, links per language like `Readiness.Doc.*` | V3 |
| Title-bar search with key caps, at most 8 results, navigate and highlight for 2 s (`ACM/MainWindow.xaml:244-343`; `ACM/WindowComponents/SearchPageCatalog.cs`; `MUnitListViewControl.xaml.cs:1100-1210`) | functions are found by name | the Ctrl+K palette and the object search merged into one box in the title bar; 2 s highlight of the target | V4 |
| Tall title bar, 48 px, with back, icon, title, search, actions (`ACM/MainWindow.xaml:185-211`; `ACM/MainWindow.xaml.cs:169-179`) | global actions always in the same place; one row saved | title bar on `WindowChrome`, with a plain row under the system title bar as fallback | V4 |
| Navigation pane with groups, footer and compact mode (`ACM/MainWindow.xaml:138-170`, `:492-553`; `HSS/MainWindow.xaml:459-648`) | the areas of the program are visible | an area strip next to the tree, or a pane – see 6.2, open question 2 | V4 |
| Home page that answers "what is the state" without a click (`HSS/Pages/Home.xaml:18-77`, `:278-1210`) | orientation right after start | a Home document per connection window | V4 |
| Settings as cards and expanders, the whole card is the target (`ACM/CustomUIElements/SettingsControl/SettingsCard.xaml`; `SettingsCardV2.cs:30-80`) | settings can be scanned; large targets | card style on the Fluent `Expander` plus click forwarding | V5 |
| One dialog style: safe default button, text states the consequence (`ACM/CustomUIElements/ContentDialogV2.cs:46-103`; `ACM/MainWindow.xaml.cs:2363-2375`) | fewer destructive mistakes | one style for `MessageDialogWindow` and the confirmations of `DialogService` | V5 |
| Teaching tips, rare (5), next to the control (`ACM/MainWindow.xaml:413-422`) | a control is explained where it is | `Popup`-based tip, each shown once | V5 |
| Status pills: colour, word, tooltip (`HSS/CustomUIElements/StatusIndicatorV2.cs:31-60`) | state readable without colour vision | `StatusPill` with the word always visible; badges on object icons | V2 |
| Entrance motion and 83 ms hover feedback (`HSS/App.xaml:87-103`; `SettingsCard.xaml`) | changes read as movement, controls feel alive | attached `Motion.*` behaviours and visual states in the `Ad.*` styles | V1 |
| Mica, light/dark/system, cards with 8 px corners and a 1 px stroke (`ACM/ViewModels/MainWindowVM-Shared.cs:108`; `HSS/App.xaml:55-62`) | the Windows 11 look | the Fluent theme's own backdrop, card tokens over `Ad.Card` | V1 |
| Icon system with a style setting; the reference's default is monochrome (`CC/AppSettings/Main.cs:268-278`; `HSS/ViewModels/MainWindowVM.cs:604-912`) | consistent icons, decoration is a choice | our own colour set (`Icon.*`) with glyph fallback and a setting | V1 |
| Shimmer placeholders and taskbar progress (`CC/ToolKits/Shimmer.cs:51`; `CC/Taskbar/TaskBarProgress.cs:28`) | "loading" differs from "empty" | `Skeleton` rows, `TaskbarItemInfo` | V2 |
| Tooltips as sentences, accessible names, access keys (434 tooltip and 422 automation strings in the HSS resources) | faster by keyboard, usable with a screen reader | a sweep over our 491 tooltips and 1,006 automation names | V3, V7 |
| Window remembers size and panes; breakpoints at 400/950/1300 px (`ACM/WindowComponents/NavigationService.cs:430-465`; `ACM/MainWindow.xaml:138-170`) | the program opens the way it was left | breakpoints 1000 and 1300 px (ours), layout kept in the settings as today | V4 |
| First-run "what is new" sheet (`ACM/CustomUIElements/FeatureHighlightsCarouselDialog.xaml.cs`) | new functions get noticed | a plain "What's new" card on Home that opens the release notes | V5 |

What we deliberately do not take, and why (study, *Things not to adopt*):

- **Any artwork** – icons, animation files, generated icon classes, pictures. They are Icons8 material licensed to that
  repository only (its `README.md:655-656`). Everything we draw is our own (`docs/assets/icons/`).
- **Decoration with per-frame rendering** – rainbow window border, title-bar animals, shader, glitch title, animated
  background, pointer spotlight. An admin tool runs over RDP and on jump hosts; this costs CPU and says nothing.
- **Six backdrop kinds with sliders and a background picture.** Three of them force the dark theme because light
  breaks. We offer the theme and at most "Mica where available / solid".
- **Animated icons as a default, and Lottie at all.** Even the reference defaults to monochrome; WPF has no
  `AnimatedIcon`, and a Lottie player would bring SkiaSharp as a new native dependency (see 3.5).
- **Card lists for object data.** A card takes the room of four or five table rows. Cards are for settings, tasks and
  overviews; directory objects stay in dense virtualized tables.
- **The hand-built table** (header buttons and 307 splitters) – it exists because WinUI 3 has no DataGrid; WPF lists
  already sort, resize and reorder.
- **The size of the reference's custom controls** (1,891 + 1,233 + 1,533 lines for status line, button and dialog).
  Most of it handles WinUI page unloading. We port the behaviour, not the code.
- **Showy routine motion** – 600–1000 ms tile animations, 800 ms "fly to" effects, staggered list entrances on every
  refresh, 400–450 ms disappearing. Fine on a page seen once, tiring on a screen used a hundred times a day.
- **Navigation sounds, web previews on hover, minimize to tray, a re-implemented caption menu.** No value for our
  users; the hover preview would load external pages from an admin workstation.
- **Hard-coded colours and unlocalized strings** – the reference has both; our conventions already forbid them.
- **The platform choices** – Windows 11 24H2 minimum, MSIX only, Native AOT, a preview SDK. They are the author's
  preferences, not needed for the look, and too narrow for admin workstations and servers.
- **An update check.** A program that phones home from an admin workstation needs its own decision; not in this track.

## 3. Technology decision (the owner's decision)

**Decided by the owner on 2026-11-10: option A1** – the WPF app is evolved in place with our own small controls. The
comparison below is kept as the record of why.

ADR-0001 chose WPF on .NET 10 with the built-in Fluent theme and rejected WinUI 3 for the lack of a mature DataGrid and
for its deployment. The reference is a WinUI 3 application, so the question is asked again here, fairly.

### 3.1 The options

- **A – evolve the WPF app in place.** Keep the views; add tokens, motion and the missing controls step by step.
  - **A1 – our own small controls** for what WPF lacks (status line, action button, page header, title bar, area
    strip, teaching tip, settings card).
  - **A2 – a vetted library.** The candidate is **WPF-UI** (NuGet `WPF-UI` 4.3.0, published 2026-05-04, licence
    expression MIT, targets `net10.0-windows7.0`, depends only on `WPF-UI.Abstractions`, MIT; `Wpf.Ui.dll` is
    6.5 MB). Its public API has NavigationView, InfoBar, BreadcrumbBar, TitleBar, Snackbar, ContentDialog,
    CardControl, CardExpander, AutoSuggestBox, ProgressRing, ToggleSwitch, FluentWindow and backdrop kinds
    None/Auto/Mica/Acrylic/Tabbed; it has **no** TeachingTip, InfoBadge or AnimatedIcon. It is a second theme system:
    its own theme manager (`ApplicationThemeManager`), its own window class, its own subclasses of `ListView`,
    `GridView`, `DataGrid`, `Button`, `MenuItem`, and embedded icon fonts.
- **B – a new WinUI 3 shell over the existing Presentation layer.** The view models (122,036 lines, no WPF types)
  stay; all views, App services and UI tests are written again on the Windows App SDK.
- **C – others worth naming.** *C1, hybrid:* host single WinUI controls in the WPF window (XAML Islands). It brings
  the Windows App SDK runtime and licence of B and the airspace limits of hosted content, for a few controls; not
  evaluated further. *C2, Avalonia* with a Fluent control library: a full rewrite of the views like B, on a
  framework ADR-0001 already set aside, to gain cross-platform support we do not need; not evaluated further.

### 3.2 Comparison

| Criterion | A1 – WPF, own controls | A2 – WPF with WPF-UI | B – WinUI 3 shell |
|---|---|---|---|
| Fidelity of the look | high for surfaces and type: the Fluent theme uses the same resource names (cards, layers, accent, text ramp); Mica on Windows 11 22H2+ comes from the theme | higher out of the box for navigation pane, title bar and dialogs | full – the same framework as the reference |
| Fidelity of the motion | medium: about a dozen animations built by hand (section 5); no reflow transitions, no connected animation, no animated icons | medium: the same, minus what its controls animate themselves (not inspected) | full: entrance and reposition transitions are one line each (study §3) |
| Ease of use (the larger half) | the same in all three: status line, command shape, list toolbar, search, page headers are design and view-model work | the same | the same |
| Effort | sum of the stages of section 8: about 15–26 weeks of one developer, in seven shippable steps; first visible gain after 1–2 weeks | saves building pane, title bar, status line and dialog (roughly 3–5 weeks), costs a migration: either all 86 XAML files move to its theme and controls, or two theme systems coexist | about 66,000 lines to write again before the first release – 31,607 XAML, 6,631 code-behind, 11,751 App code, 16,068 UI tests – plus the same design work as A; no shippable intermediate step |
| Risk | low to medium; highest in the title bar (`WindowChrome`, snap layouts, RDP) and in list performance with colour icons | medium to high: coexistence with `Application.ThemeMode` and with our 39 `Ad.*` styles based on Fluent styles is unverified; one maintainer organisation | high: no in-box DataGrid (the reference hand-builds its tables), implicit data templates and style triggers, which our XAML relies on, are not part of WinUI 3 (general knowledge, not checked here), the rich message editor rests on WPF `FlowDocument`; features stop or are built twice meanwhile |
| Tests and screenshots | kept: same harness, same names; screenshots re-rendered per stage | kept in principle; every test that names a replaced control type changes | all 312 UI tests and 43 screenshot tests rewritten on a different harness; the 2,077 view-model tests stay |
| Distribution | unchanged: one self-contained exe in a zip; Windows 10 22H2, 11, Server 2019+ | unchanged, about 6.5 MB larger | Windows App SDK inside the package (`Microsoft.WindowsAppSDK.Runtime` 2.6.5 is a 172 MB NuGet package; how much of it an x64 self-contained app carries was not measured); single-file publishing and Windows Server support not established |
| Accessibility | WPF UI Automation as today; our controls need peers and High Contrast themselves | depends on the library's peers (not inspected) | mature in-box controls |
| Licences | nothing new | MIT, compatible; the licence of the embedded icon fonts must be checked | `Microsoft.WindowsAppSDK` 2.6.5 ships under "Microsoft Software License Terms – Microsoft Windows App SDK" (licence file, not an OSI expression): this conflicts with our rule that new dependencies need an OSI-approved licence, and SignPath Foundation requires every component to be open source |

### 3.3 Recommendation

**A1 – evolve the WPF app in place with our own small controls.** Reasons:

1. Most of what makes the reference pleasant is consistency (study, summary), and that half costs the same in every
   technology; A delivers it in steps, the first after one or two weeks, while features continue.
2. The Fluent theme already gives the reference's surfaces: the same card, layer and accent resources, the same
   expander curves, Mica on Windows 11 22H2 and newer. What is missing is a handful of controls of modest size.
3. B would rewrite about 66,000 lines and all UI tests before the first user-visible gain, would lose the list
   controls ADR-0001 chose WPF for, and brings a dependency whose licence our own rules do not admit as written.
4. A2 would save a few weeks of control building and pay for them with a second theme system across 31,607 lines of
   XAML. It stays an option for **one** control: if the owner chooses the full navigation pane (6.2, N1), a
   two-day spike decides between building it and taking WPF-UI's.

What we give up with A1, plainly: the motion will be close, not identical. Reflow transitions of toolbars, connected
animations and animated icons are not planned.

**Evidence that would change the recommendation:**

- the owner requires the reference's motion exactly (reflow, connected animations, animated icons), not "close";
- the V1 spike shows that WPF cannot hold the budgets of section 7 with colour icons and entrance motion, or that
  the custom title bar cannot be made reliable on the supported systems;
- Microsoft withdraws or freezes the WPF Fluent theme (`ThemeMode` is still experimental in 10.0.12);
- the supported systems shrink to Windows 11 and the licence question of the Windows App SDK is settled with SignPath.

### 3.4 What option A must avoid so that B stays possible

- **No WPF types in Presentation** (already the rule). Every new concept – page status, operation scope, page
  header, navigation areas, search index, teaching tips, motion and icon preferences, home page – is a plain view
  model or service there, with tests on Linux. Under B only its view would be new.
- **No logic in code-behind, converters or behaviours** beyond presentation. `MainWindow.xaml.cs` (744 lines) should
  shrink, not grow.
- **Tokens named after the Fluent resource keys** WinUI also has (`CardBackgroundFillColorDefault…`,
  `ControlCornerRadius`), so a WinUI dictionary could supply them.
- **Icons keep SVG as the source of truth** (`docs/assets/icons/`); the XAML is generated and could be generated for
  another framework.
- **Navigation stays a model**: `IShellDocument`, the command registry and the area list live in Presentation; the
  shell only draws them.
- **No dependency of view models on WPF-only services** (`ICollectionView`, `CommandManager`, routed commands).

### 3.5 Other packages considered

- `Microsoft.Xaml.Behaviors.Wpf` 1.1.161 (MIT, targets `net10.0-windows7.0`): not needed – the app already has its
  own attached behaviours (`src/AdMng.App/Behaviors`).
- `LottieSharp` 2.4.3 (MIT) for animated icons: depends on `SkiaSharp.Skottie` and `SkiaSharp.Views.WPF` 2.88.9,
  targets `net8.0-windows7.0`; a native rendering library for decoration. Not planned.
- `iNKORE.UI.WPF.Modern` 0.11.0 (no licence expression in the package, only a licence URL; targets .NET 6) and
  `ModernWpfUI` 0.9.6 (MIT, newest target .NET 5): not considered further.

## 4. Design language

### 4.1 Colour and surface tokens

One dictionary, `Themes/Tokens.xaml` (new), holds semantic aliases; views use only the aliases. Each alias points at a
Fluent key, so light, dark and High Contrast follow from the theme's three dictionaries without code of ours.

| Token (ours) | Fluent key behind it | Use |
|---|---|---|
| `Ad.Surface.Window` | window backdrop, else `ApplicationBackgroundBrush` | the window behind everything |
| `Ad.Surface.Layer` | `LayerFillColorDefaultBrush` | tree column, details pane, document area |
| `Ad.Surface.Card` / `Ad.Stroke.Card` | `CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush` | toolbar card, settings card, home card |
| `Ad.Stroke.Divider` | `DividerStrokeColorDefaultBrush` | separators |
| `Ad.Text.Primary` / `Secondary` / `Tertiary` | `TextFillColor…Brush` | text |
| `Ad.Accent` / `Ad.Accent.Text` / `Ad.Text.OnAccent` | `AccentFillColorDefaultBrush` / `AccentTextFillColorPrimaryBrush` / `TextOnAccentFillColorPrimaryBrush` | selection, primary button, links – the system accent, no palette of our own |
| `Ad.Status.Success` / `Caution` / `Critical` / `Attention` (+ `.Background`) | `SystemFillColor…Brush` and `…BackgroundBrush` | status line, pills, validation |
| `Ad.Focus` | `FocusStrokeColorOuterBrush` | focus rectangle |
| `Ad.Connection` | existing `Ad.ConnectionBrush` | identity colour of a connection window (`docs/dev/multi-window.md` 4.3) |

Rules: no literal colour anywhere (true today); a state is never told by colour alone; the icon palette
(`docs/assets/icons/README.md`) is for icons only and never used for text or surfaces.

Backdrop per system (the theme decides, we only add the setting):

| System | Backdrop | Note |
|---|---|---|
| Windows 11 22H2 and newer | Mica, from the Fluent theme | already applied by the theme to themed windows; whether our windows show it or cover it was not checked on a screen |
| Windows 10 22H2, Server 2019, Server 2022 | solid `ApplicationBackgroundBrush` | below the build the theme requires |
| Any system with High Contrast | none – system colours | the theme removes the backdrop |
| Setting *Background: solid* (ours) | solid | through the app-context switch behind `DisableFluentThemeWindowBackdrop`; the theme reads it once, so the setting applies at the next start; for RDP and slow machines |

### 4.2 Type ramp

The theme's ramp, named once: Caption 12, Body 14, Body strong 14 semibold, Subtitle 20, Title 28. Larger sizes are
used only on the home page. An administrator's tool also needs a **compact** size: `Ad.Text.Compact` 12 for list rows,
tree rows and details values **(ours)**. Monospace (`Consolas`) only for DNs, GUIDs, SIDs, filters and code. The
literal sizes 10, 11, 13 and 15 found today are mapped to the nearest ramp step.

### 4.3 Spacing, density, radii, elevation

- **Spacing** on a 4 px grid: 2, 4, 8, 12, 16, 24, 32. Page margin `16,12` (the reference uses `15,10,15,10`; ours is
  its neighbour on the grid). Button icon to text 8, glyph 16, toolbar spacing 8 (reference values).
- **Density.** The theme's defaults are roomy: control height 32, list row 40. `ObjectListView.xaml` already overrides
  them by hand (row minimum 0, header 28) and `MainWindow.xaml` overrides the text box padding. Tokens replace these
  local overrides:

  | Token | Compact (default, ours) | Comfortable (theme) |
  |---|---|---|
  | control height | 28 | 32 |
  | list and tree row | 26 | 40 |
  | column header | 28 | 32 |
  | card padding | 12 | 16 |
  | settings card minimum height | 56 | 68 (reference) |
  | pointer target minimum | 24 × 24 | 32 × 32 |

- **Radii**: control 4 (`ControlCornerRadius`), card, popup and toast 8 (`OverlayCornerRadius`), pill 12 at height
  24 (reference). The 14 px dialog corners with a glow of the reference are not taken; real windows keep the system
  corners. The literal radii 2, 3, 6 and 9 found today map to 4 or 8.
- **Elevation** – three levels, no fourth: 0 = page (no stroke); 1 = card (1 px stroke, no shadow); 2 = transient
  surface – popup, toast, teaching tip, palette (the theme's popup look). Dialogs and property sheets are real
  windows and get the system shadow. No `DropShadowEffect` on anything that contains a list: the subtree is then drawn
  through an intermediate surface, text in it loses ClearType and every change redraws the whole surface.

### 4.4 Icon system

The set is drawn in parallel (`docs/assets/icons/`, generated into `Themes/Icons.xaml` with keys `Icon.<Name>` and
`Controls/AppIcons.cs`). At the time of writing `icons.json` lists four groups: 28 object icons (user, computer,
server, domain controller, group, contact, organizational unit, container, domain, service account, printer, shared
folder, Group Policy, WMI filter, site, certificate and others, with *disabled* and *locked* variants), 25 area icons
(home, directory, search, recycle bin, bulk edit, account review, privileges, messages, correspondence, computers,
event logs, certificate authority, readiness, settings, activity, connection, PowerShell, DNS, DHCP and others),
5 statuses (ok, warning, error, info, unknown) and 6 overlays (disabled, locked, mail, service, add, review). That
covers the area strip, the home page and the status line of this plan. What V1 still has to check:

- every value of `ObjectIcon` maps to a key (`AppIcons.KeyFor`) – a test over the enum;
- "in progress" has no icon on purpose: it is shown by the progress ring, not by a picture;
- a monochrome glyph exists for every colour icon, for the *monochrome* style and for High Contrast.

Where which kind of icon:

| Kind | Where | Why |
|---|---|---|
| Colour icons (`Icon.*`) | things: object kinds in tree, lists, tabs, details and property sheet headers; areas in the strip and on Home; statuses in status line, pills and readiness lists; empty states | recognised by shape and colour before reading |
| Monochrome glyphs (`Glyphs.cs`) | actions: toolbar buttons, menu items, chevrons, sort arrows, close, search | take the text colour, stay quiet next to the data |

| Size | Use |
|---|---|
| 16 | list, tree, tab, menu row, pill |
| 20 | area strip, status line, settings card header |
| 24 | home card header, primary toolbar action |
| 32 | details pane header, search suggestion, dialog |
| 48 | property sheet header, empty state, connect window |

- **State badges** as in the set's rules: the base drawing plus an overlay in the bottom-right quadrant covering at
  least 40 % – a slate disc with a down arrow for *disabled*, an amber padlock for *locked* – and always the word in
  the row or tooltip.
- **Style setting** *Icons: colour / monochrome*. Monochrome is today's look. High Contrast always uses glyphs, so
  the system colours apply. Which one is the default is open question 4.
- **Drawing cost.** `ObjectIconView` stays a light element without template or bindings; it draws one shared, frozen
  drawing per kind and badge. At 16 px a 48-unit drawing is scaled to a third, so thin details blur: the set's rule
  "nothing thinner than 2.5 units" exists for this, and V1 checks 100, 125 and 150 % scaling on screenshots.
- **No animated icons.** At most, later, a refresh glyph that turns while a refresh runs (question 14).

### 4.5 Wording of status messages

- **Four severities with fixed titles**: *Working* (in progress, cannot be closed), *Done*, *Attention*, *Error*.
- **In progress** names the action and the scope: "Reading 2,314 accounts from Kraków…".
- **Done** says what happened, how many, and where the result is: "12 accounts disabled in contoso.test. The report
  is in the activity list." No "successfully".
- **Attention** says what was skipped, why, and what to do: "3 of 15 accounts skipped: no right to write. Sign in
  with another account or ask the owner of the OU."
- **Error** is built by one helper as "what we tried: why it failed. What to do next." A result code goes in
  parentheses at the end, never alone.
- Destructive results and confirmations name the domain (`docs/dev/multi-window.md` 4.5). Buttons are verbs with the
  count ("Disable 12 accounts"), the safe button is the default.
- Every text exists in English and Polish, with correct Polish plural forms (1 konto, 2 konta, 5 kont).
- The on-screen text may name objects; the copy for the diagnostic log is the message key with counts only (rule 1).

## 5. Motion

### 5.1 Tokens

All in `Themes/Motion.xaml` (new); no duration or easing is written anywhere else.

| Token | Value | Source |
|---|---|---|
| `Motion.Hover` | 83 ms | reference: brush change on hover (`SettingsCard.xaml`, `SegmentedStyles.xaml`) |
| `Motion.Press` | 120 ms | reference: small scale (`ACM/CustomUIElements/ScanLevelSelector.xaml.cs:318-319`) |
| `Motion.Swap` | 150 ms out, 150 ms in | reference: button content (`AnimatedCancellableButton.cs:895-916`) |
| `Motion.Fast` | 167 ms | reference: expander close; also the most common duration in the WPF Fluent theme |
| `Motion.Appear` | 250 ms | reference: status message fade in (`ACM/CustomUIElements/InfoBarV2.cs:229`) |
| `Motion.Disappear` | 167 ms | **ours** – the reference uses 400–450 ms, above our limit for routine motion |
| `Motion.Entrance` | 200 ms | **ours** – the reference uses the WinUI default, which its code does not state |
| `Motion.Expand` / `Motion.Collapse` | 333 ms / 167 ms | reference and the WPF Fluent `Expander` (same key splines) |
| `Motion.Highlight` | 2 s | reference: "look here" fade (`MUnitListViewControl.xaml.cs:1185-1191`) |
| `Motion.Shimmer` | 1600 ms loop | reference (`CC/ToolKits/Shimmer.cs:51`) |
| `Motion.HoverIntent` | 500 ms delay | reference (`HSS/CustomUIElements/LinkButtonV2.cs:44`) |
| `Motion.Offset.Page` / `Motion.Offset.Panel` | 24 px / 8 px | reference uses 50 px for pages and 24 px for panels; **ours** are smaller for a dense tool |
| `Motion.Ease.Enter` | cubic ease-out; key spline `0,0,0,1` | reference (`InfoBarV2.cs:189`, `SettingsExpander.xaml:166-227`) |
| `Motion.Ease.Exit` | cubic ease-in; key spline `1,1,0,1` | reference (`InfoBarV2.cs:1438`, `SettingsExpander.xaml`) |
| `Motion.Ease.Change` | quadratic ease in-out | reference: hover and content swap |

### 5.2 Catalogue

"Behaviour" means an attached property in `src/AdMng.App/Behaviors/Motion.cs` (new) that starts a storyboard; "VSM"
means visual states in a control template of `Themes/`.

| # | Animation | Where | Trigger | What moves | Time, easing | What it tells | Built with | Performance rule |
|---|---|---|---|---|---|---|---|---|
| 1 | Hover and press feedback | `Ad.*` buttons, chips, cards, area strip | pointer over, pressed | opacity of a hover layer inside the template | Hover 83 ms, press at once; Change | "this can be clicked" | VSM (`CommonStates`) | opacity of one border only; the theme brush is never animated, so theme and High Contrast switches stay correct |
| 2 | Content swap of the action button | `ActionButton` | operation starts, cancel requested, ends | two contents cross-fade | Swap; Change | "running – press again to stop", then "stopping" | VSM states `Idle`/`Running`/`Cancelling` | no size animation: the button keeps the width of its widest state |
| 3 | Status message change | `StatusLine` | `PageStatus` changes | text and icon fade, 8 px rise | Appear in, Disappear out; Enter/Exit | "a result arrived" – without a pop-up | storyboard in the control | the line has a fixed height and is always present, so content never jumps |
| 4 | Page entrance | document host, home page, settings page | a document opens or its tab is chosen | header and toolbar fade; content rises by Offset.Page | Entrance; Enter | "a new page replaced the old one" | behaviour `Motion.Entrance` on the host | one container, `RenderTransform` only; list content is moved, not faded (a fade renders the subtree off-screen and text loses ClearType meanwhile – to confirm in V1) |
| 5 | Panel entrance | details pane content, filter panel, expander body of ours | panel becomes visible | fade and rise by Offset.Panel | Fast; Enter | "this belongs to what you just chose" | behaviour `Motion.Entrance="Panel"` | not on selection change in a list (that is live data, rule 3 of 5.3) |
| 6 | Transient surface in and out | palette and search popup, teaching tip, in-window overlay | opens, closes | fade, 4 px drop **(ours)** | 120 ms in, 100 ms out **(ours)**; Enter/Exit | "this lies on top and will go away" | storyboard on `Popup` child | closes at once on Esc – the exit never delays focus return |
| 7 | Toast in and out | `ToastHost` | toast added, removed | slide 16 px from the right, fade | Appear, Disappear | "something happened elsewhere" | behaviour on the item container | at most three toasts on screen |
| 8 | Expander | Fluent `Expander` (31 uses) | expand, collapse | content slides | Expand, Collapse (theme) | "the group opens downwards" | the theme, unchanged | nothing of ours |
| 9 | "Look here" highlight | list row, settings card, tree node | navigation from search or from a result link | background tint fades to normal | Highlight | "this is what you searched for" | behaviour `Motion.Highlight` on the realized container after `ScrollIntoView` | one element; cleared when the container is recycled |
| 10 | Skeleton | list host while the first page loads | loading and no rows yet | gradient sweep over at most 12 placeholder rows | Shimmer | "rows are coming" – not "empty" | opacity-mask animation | removed from the tree when rows arrive or the window is minimized; static bars under reduced motion |
| 11 | Progress | `ProgressBar` (theme), ring in `ActionButton` and tab header | operation runs | indeterminate or bound value | theme; ring 1 s per turn **(ours)** | "busy", or "how far" | theme storyboard; small ring control | the only endless animations allowed are 10 and 11 |
| 12 | Area indicator | area strip | area changes | accent bar slides to the new item | Fast; Change | "you are now here" | `RenderTransform` animation | – |
| 13 | Refused drop | tree (`DropRefusedText`) | drop not allowed | text fades in and out | Fast | "not here, and why" | storyboard | – |

Not animated, on purpose: rows of a list (add, remove, reorder), toolbar reflow, window open and close (the system
does it), selection change, sorting, tab reordering, theme switch.

### 5.3 Rules

1. **Never delay input.** The logical state changes first (enabled, focus, selection, navigation); the animation
   only follows. Nothing is hit-test-disabled while it animates. A new interaction replaces a running animation.
2. **Nothing animates beyond the first screen of a virtualized list** – and nothing per row at all, except the one
   highlighted row. No staggering.
3. **No animation on live refresh.** List refresh, the Watchdog feed, VLV paging and selection-driven updates of the
   details pane apply at once. Only what the user just did animates.
4. **Routine motion is at most about 300 ms.** Exceptions: the theme's expander (333 ms), the highlight (2 s), the
   skeleton loop.
5. **Only opacity, `RenderTransform` and hover-layer opacity** are animated – never width, height or margin.
6. **One source.** A XAML source test (like today's `XamlSourceTests`) rejects `Duration=`, `KeySpline=` and easing
   elements outside `Themes/`.

### 5.4 Reduced motion

- Setting *Motion: follow Windows / on / off* (`MotionPreference` in `AppSettings`, default *follow Windows*).
- *Follow Windows* means on only while `SystemParameters.ClientAreaAnimation` is true (the Windows setting
  "Animation effects") and rendering is hardware accelerated (rendering tier above 0, `RenderCapability.Tier`).
  Changes are picked up live through `SystemParameters.StaticPropertyChanged`. Whether a remote session should
  default to off even when Windows reports animations on is open question 6.
- When off: behaviours set the final values directly (no zero-length storyboards), the skeleton shows static bars,
  the highlight is a static tint removed after 2 s, hover feedback switches at once. Progress stays – it is
  information.
- The theme's own storyboards (expander, combo box) are not ours to gate; whether they follow the Windows setting
  was not established. V7 decides whether the expander needs a template of ours.

### 5.5 Deterministic tests and screenshots

- A single `MotionService` (App, singleton) answers "is motion on". `WpfTestHost` and `--smoke-test` switch it off
  before the first window, so every test and every screenshot sees final values. The variable `ADMNG_UI_TESTS=1` is
  set by CI today but no code reads it; the service honours it as a third switch, so a manually started test run
  behaves the same.
- Tests of the motion itself turn it on explicitly and assert through the storyboard clock, not by waiting.
- Screenshot tests additionally assert that no animation clock is active when the picture is taken.

## 6. The renewed shell

### 6.1 Page skeleton

Every document page (the 6 template groups in `MainWindow.xaml`: object list, Watchdog, event logs, readiness, PKI,
Group Policy) and every tool window gets the same four parts, top to bottom:

1. **Page header** – title, one sentence, guide link (from 1300 px inline, below that behind a "?" button).
2. **Status line** – always present, fixed height. Idle: a neutral summary ("2,314 objects, 12 selected") – this
   absorbs the `StatusText` bindings found in 32 XAML files. During and after an operation: the four severities.
3. **Toolbar card** – the page's commands; for lists the standard list toolbar.
4. **Content.**

The elements carry fixed names (`PageHeader`, `PageStatus`, `PageToolbar`, `PageContent`), so a source test can check
the skeleton and UI tests can find the parts the same way everywhere.

### 6.2 Navigation – open question 2

**Decided by the owner on 2026-11-10: N2** – the tree stays primary and a 48 px area strip is added.

AD Manager is tree-centred: the directory tree is the first column, and Group Policy and PKI already live in it as
nodes (`GroupPolicyNodeViewModel`, `PkiNodeViewModel` in the tree templates of `MainWindow.xaml`). Documents are tabs,
several open at once. The reference shows one page at a time, chosen in a navigation pane. Two ways to bring them
together:

- **N1 – the pane is the navigation.** A pane like the reference's (240 px open with labels and groups, 48 px
  compact) lists the areas; *Directory* is one of them and shows the object tree as the left column of its page.
  For: closest to the reference; areas are always visible with names; room for future tracks (DNS and DHCP,
  Microsoft 365, PowerShell). Against: four columns on a wide window (pane, tree, list, details) and too little
  width at 1366 px; Group Policy and PKI nodes must move out of the tree (a change of the navigation model in
  Presentation); "one page at a time" collides with document tabs; in WPF the pane is the largest single control
  to build (study: L–XL).
- **N2 – the tree stays primary, areas are a switcher.** A 48 px strip of area icons at the window's left edge (it
  looks like a compact navigation pane) with tooltips and a footer for activity and settings. Choosing an area
  scopes the tree to that area's roots (Directory: domain and saved views; Group Policy; Certificates) or, for
  areas without a tree (Watchdog, event logs, reviews, messages), activates or opens their tab. Tabs stay as they
  are. For: keeps the tree-centred work, the keyboard paths and most tests; costs 48 px; the tree model changes
  little. Against: looks less like the reference; names only as tooltips (and in search); at most about ten areas.

**Recommendation: N2**, with N1 reconsidered when tracks C or N add areas of their own. Under 1000 px the tree becomes
a flyout opened from a button; the strip stays.

### 6.3 What moves out of today's menus

The five menus hold 131 items. Proposal (what stays reachable where):

| Today | Goes to |
|---|---|
| *File*: connect, new window, close, exit all | menu of the connection badge in the title bar; search |
| *Action*: object commands, *New*, *Copy as*, computer, Group Policy | toolbar card (*New*, *Actions*) and the context menu – they depend on the selection; search |
| *View*: filters, theme, language | filter toggle in the list toolbar; theme and language in *Settings – Appearance* |
| *Tools*: Watchdog, event logs, readiness, PKI, reviews, PowerShell history | area strip, Home, search |
| *Help*: guide, report a problem, about | "?" button in the title bar; Home card *This version* |

Whether the classic menu bar stays visible, appears on Alt, or goes, is open question 3. Until decided it stays: its
access keys are part of keyboard work, and tests address its items by name (`MainMenu`, `ConnectMenuItem`,
`ThemeMenu` …).

### 6.4 Home page after connecting

A document (`HomeDocumentViewModel`, one per connection window) that answers "where am I and what needs attention":
connection (domain, DC, account, security mode), the readiness summary (from the quick check that exists), quick
actions, pinned views, Watchdog status when configured, this version. It runs nothing heavy by itself: counters that
need a directory query have a *Run* button (rule 6). It stores nothing new (rule 1).

### 6.5 Title-bar search and the Ctrl+K palette

Today there are three inputs: the address box, the object search box with suggestions, and the Ctrl+K palette. The
renewal makes **one search box in the title bar**; Ctrl+K focuses it, so the shortcut and
`CommandPaletteViewModel` (fuzzy, diacritics-insensitive) stay. Results come in groups of at most 8 (reference):
*Commands*, *Pages and views*, *Settings*, *Objects*. Objects are a live, cancellable query on the window's DC with a
filter built by `LdapFilter` (rules 3, 4, 6), started after a short pause in typing. Choosing a result navigates and
highlights the target for 2 s. The address box stays in the page as the path of the tree location. While the box is
empty it shows the key caps "Ctrl" "K" (reference: `ACM/MainWindow.xaml:296-343`).

### 6.6 Settings as cards

`SettingsWindow.xaml` (1,037 lines, tabs *General*, *Messages*, *Computers*) becomes a page with a category list on
the left and cards on the right: icon, title, one line of description, the control; related options inside an
expander. The whole card operates its control. Settings sections are in the search index. The section names kept by
tests (`GeneralTab`, `MessagesTab`, `ComputersTab`, `PkiSection`, `OutlookSection`, `SalutationsSection`) stay as
names of the new sections.

### 6.7 Feedback, one place each

| Means | For | Behaviour |
|---|---|---|
| Status line | the result of what this page just did | stays until the next operation or until closed; "working" cannot be closed |
| Toast | something that finished elsewhere (another tab or window, a background job) | leaves after 5 s **(ours)**; errors stay until closed |
| Activity list | everything running or finished in this connection | as today |
| Dialog | a decision is needed | one style, see 6.8 |
| Teaching tip | a control that needs one explanation | shown once, light dismiss, can be shown again from settings |
| Progress | how far | in the action button, the tab header and the taskbar |
| Action button | start, cancel, cancelling | cancel where you started |
| Empty state | a list with no rows | 48 px icon, one sentence why, one action ("Clear filter") |
| Skeleton | the first page of a list is loading | at most 12 placeholder rows |

### 6.8 One dialog style, property sheet, multi-window

- **Dialogs** stay real windows (owner chain and modality per connection window are worked out in
  `docs/dev/multi-window.md` 3.6); one layout: title, text that states the consequence and names the domain,
  optional single check box, buttons right-aligned with the safe one as default. The existing `Ad.DialogButton`,
  `Ad.AccentDialogButton` and `Ad.DangerDialogButton` become the only button styles of dialogs.
- **Property sheet** (`PropertiesWindow.xaml`, 2,340 lines) stays modeless and per connection: a header card (48 px
  icon with badge, name, state pills, copy buttons), the status line, then the pages. Whether the pages stay tabs
  across the top or become a list on the left is open question 11.
- **Multi-window connections** keep their identity (`docs/dev/multi-window.md` 4): the custom title bar carries the
  connection colour and the domain-and-account title; each window remains one DI scope. New per-connection services
  (page status hub, search index, home document) are scoped and added to `ConnectionLifetimes.cs`; `MotionService`,
  icon style and theme are application-wide.

### 6.9 Wireframes

Legend: `[H]` home, `[D]` directory, `[G]` Group Policy, `[C]` certificates, `[E]` event logs, `[W]` Watchdog, `[R]`
reviews, `[M]` messages, `[A]` activity, `[S]` settings; `<` marks the selected item; `(u)`, `(g)` stand for object
icons, `(i)` for the status icon.

Main window, wide (from 1300 px), option N2:

```text
+--------------------------------------------------------------------------------------------------------------+
| [<] [>] (AD) contoso.test - CONTOSO\anna.zielinska   [ Search commands, objects  Ctrl+K ]    (2) [?]  _ [] X |
+--------------------------------------------------------------------------------------------------------------+
| [H]  | Directory                | [Kraków/Users x] [Account review x]                | (u) Łukasz Żak        |
| [D]< | v contoso.test           | Users                                  [?] Guide   |     User, enabled     |
| [G]  |   v Kraków               | Accounts in OU=Users,OU=Kraków.                    | [Copy v] [Reset pwd]  |
| [C]  |     > Users        <     | (i) 2 314 objects, 12 selected                     |                       |
| [E]  |     > Computers          | .------------------------------------------------. | Login    l.zak        |
| [W]  |   > Warszawa             | | [New v] [Actions v] Filter [_______] 2314/2314 | | Mail     l.zak@       |
| [R]  |   > Builtin              | | [Columns] [Export] [Select v]        [Refresh] | |          contoso.test |
| [M]  | Saved views              | '------------------------------------------------' | Office   Kraków       |
|      |   Locked accounts        | Name               Login       State     Changed   | Manager  Ż. Śliwa     |
|      |   Disabled computers     | (u) Łukasz Żak     l.zak       enabled   03.11     |                       |
|      |                          | (u) Żaneta Śliwa   z.sliwa     locked    28.10     | Member of (4)         |
|      |                          | (u) Paweł Wójcik   p.wojcik    disabled  12.09     |   Księgowość          |
|      |                          | (g) Księgowość     ksiegowosc  -         01.06     |   VPN                 |
|      |                          | ...                                                |                       |
|      |                          |                                                    |                       |
| [A]  |                          |                                                    |                       |
| [S]  |                          |                                                    |                       |
+--------------------------------------------------------------------------------------------------------------+
| contoso.test | DC01.contoso.test | Kerberos, signed and sealed                 2 activities [Activity]  v0.x |
+--------------------------------------------------------------------------------------------------------------+
```

Main window, narrow (under 1000 px): the tree is a flyout, the details pane a bottom drawer, the page description
hides behind "?":

```text
+----------------------------------------------------------------+
| [<] [>] (AD) contoso.test      [ Search  Ctrl+K ]       _ [] X |
+----------------------------------------------------------------+
| [H]  | [Tree v] contoso.test > Kraków > Users                  |
| [D]< | [Kraków/Users x] [Account review x]                     |
| [G]  | Users                                         [?]       |
| [C]  | (i) 2 314 objects, 12 selected                          |
| [E]  | [New v] [Actions v] Filter [______] [...]               |
| [W]  | Name               Login       State                    |
| [R]  | (u) Łukasz Żak     l.zak       enabled                  |
| [M]  | (u) Żaneta Śliwa   z.sliwa     locked                   |
|      | (u) Paweł Wójcik   p.wojcik    disabled                 |
| [A]  | ...                                                     |
| [S]  | [Details ^]  (u) Łukasz Żak - User, enabled             |
+----------------------------------------------------------------+
```

Home page (content area of the main window):

```text
+--------------------------------------------------------------------------------------------------------------+
| Home - contoso.test                                                                                [?] Guide |
| What this window is connected to and what needs attention. Nothing here is stored on disk.                   |
| (i) Connected 09:14. Quick readiness check: 1 warning.                                                       |
|                                                                                                              |
| .--------------------------------.   .--------------------------------.   .--------------------------------. |
| | Connection                     |   | Environment readiness          |   | Start here                     | |
| | contoso.test                   |   | OK    connection, directory    |   | [New user] [Multiple accounts] | |
| | DC01.contoso.test, site Kraków |   | Warn  WebView2 runtime missing |   | [Find]      [Reset password]   | |
| | CONTOSO\anna.zielinska         |   | --    SYSVOL, logs: not run    |   | [Account review]  [Computers]  | |
| | Kerberos, signed and sealed    |   |                                |   | [Recycle Bin]  [Group Policy]  | |
| | [Change DC] [New window]       |   | [Quick check] [Full check]     |   |                                | |
| '--------------------------------'   '--------------------------------'   '--------------------------------' |
|                                                                                                              |
| .--------------------------------.   .--------------------------------.   .--------------------------------. |
| | Pinned views                   |   | Watchdog                       |   | This version                   | |
| | Locked accounts                |   | Service reachable              |   | AD Manager 0.x                 | |
| | Disabled computers             |   | Last event 08:57               |   | What's new in this release     | |
| | Kraków / Users                 |   | 3 alerts since yesterday       |   | Keyboard shortcuts             | |
| |                                |   |                                |   | Report a problem               | |
| | [Manage views]                 |   | [Open Watchdog]                |   |                                | |
| '--------------------------------'   '--------------------------------'   '--------------------------------' |
+--------------------------------------------------------------------------------------------------------------+
```

Settings, page *Appearance*:

```text
+--------------------------------------------------------------------------------------------------------------+
| [<] Settings                               [ Search settings  Ctrl+K ]                                _ [] X |
+--------------------------------------------------------------------------------------------------------------+
| Appearance      <    | Appearance                                                              [?] Guide     |
| Lists                | How AD Manager looks on this computer. Applies to all windows at once.                |
| Connections          | (i) Saved.                                                                            |
| Messages             | .-----------------------------------------------------------------------------------. |
| Computers            | | (T) Theme                                              [ Follow Windows       v ] | |
| Certificates         | |     Light, dark or the Windows setting.                                           | |
| Watchdog             | '-----------------------------------------------------------------------------------' |
| About                | .-----------------------------------------------------------------------------------. |
|                      | | (I) Icons                                              [ Colour               v ] | |
|                      | |     Colour pictures or monochrome glyphs. High contrast always uses glyphs.       | |
|                      | '-----------------------------------------------------------------------------------' |
|                      | .-----------------------------------------------------------------------------------. |
|                      | | (M) Motion                                             [ Follow Windows       v ] | |
|                      | |     Follow Windows, always on or off. Off also stops the loading shimmer.         | |
|                      | '-----------------------------------------------------------------------------------' |
|                      | .-----------------------------------------------------------------------------------. |
|                      | | (D) Density                                            [ Compact              v ] | |
|                      | | v   Row height, paddings and the background (Mica where Windows offers it).       | |
|                      | '-----------------------------------------------------------------------------------' |
+--------------------------------------------------------------------------------------------------------------+
```

## 7. Accessibility, keyboard and performance budgets

**Keyboard**

- Everything reachable without a pointer; tab order equals visual order; F6 cycles the regions (title bar, area
  strip, tree, page, details, status bar).
- Ctrl+K search; Esc closes transient surfaces and returns focus where it was; access keys on toolbar actions; the
  shortcut of a command is shown in its tooltip and in search results.
- One page *Keyboard shortcuts* generated from the command registry (the reference has none – study §5.7).

**Screen reader and vision**

- The status line is a live region: polite for working and done, assertive for errors.
- Page titles are headings (`AutomationProperties.HeadingLevel`); every new control has an automation peer and a
  name; decorative icons stay out of the automation tree as `ObjectIconView` does today.
- Text contrast at least 4.5:1, icons and control borders at least 3:1, in light and dark; High Contrast uses only
  system colours and glyph icons.
- Pointer targets at least 24 × 24 px in compact density.
- Motion follows section 5.4.

**Performance budgets** – all measured on Windows CI against a baseline recorded before V1 (not available yet):

| What | Budget |
|---|---|
| Time to first rows when opening a container with 10,000 objects | not above the baseline |
| Elements per realized list row | glyph icons: unchanged; colour icons: at most the same count (one icon element per row) |
| Scrolling and live refresh of a virtualized list | no animation clock active |
| Time from start to a usable main window | at most baseline + 10 % |
| Entrance motion | never extends time to interactive: the page takes input at once |
| Size of `AdMng.exe` | at most + 1 MB for the vector icons; no new native library |
| Duration of the UI test run | at most baseline + 10 % |
| Binding errors | zero, as today |

## 8. Stages

Sizes are for one developer including tests, both languages and docs: **S** up to one week, **M** one to two weeks,
**L** three to five weeks. They add up the study's per-item estimates (its "Top 20" table); they are judgement, not
measurement. Each stage ships in one release and leaves the program consistent.

### V1 – Look: tokens, icons, surfaces, first motion (M)

The most visible gain for the least risk: no structure changes, no element is renamed.

- **Scope:** `Themes/Tokens.xaml` and `Themes/Motion.xaml`; `Behaviors/Motion.cs`; `MotionService`; settings
  *Icons*, *Motion*, *Background* (`AppSettings`, both languages); `ObjectIconView` draws `Icon.*` with glyph
  fallback; colour icons in tree, lists, tabs, details header, connect window; card surfaces for toolbar, details
  pane and document area; hover and press feedback in the `Ad.*` button styles; page and panel entrance; toast in
  and out; literal sizes replaced by tokens in the views touched. First: record the performance baseline.
- **Files:** `Themes/*`, `Controls/ObjectIconView.cs`, `Behaviors/`, `Services/`, `Views/MainWindow.xaml`,
  `ObjectListView.xaml`, `ConnectWindow.xaml`, `SettingsWindow.xaml`; `Presentation/Settings`.
- **Tests:** icon coverage over `ObjectIcon`; motion switch (off in the test host, final values); reduced motion and
  High Contrast fallbacks; source test for tokens; list render benchmark.
- **Screenshots and docs:** all 12 README pictures change (icons, surfaces); `ARCHITECTURE.md` UI chapter (both
  languages).
- **Exit criteria:** budgets of section 7 hold; every `ObjectIcon` has both icon kinds; no animation clock during
  scrolling; screenshots identical in two consecutive CI runs.
- **Risks:** drawing cost of colour icons in long lists; blur at 16 px on 125 % scaling; noise in screenshot
  reviews.

**V1a as built (colour icons for things).** The first increment of V1; tokens, surfaces and motion follow in the
next ones.

- **Setting.** `AppSettings.IconStyle` (`IconStylePreference`: `Colour` – the default and what a settings file
  without the value loads as – or `Monochrome`), chosen in *Settings › General › Appearance › Icons* next to the theme. The
  rule is the pure function `IconStyleRules.Effective(preference, highContrast)` in `AdMng.Presentation/Icons`:
  monochrome when chosen or when Windows High Contrast is on.
- **Switch.** `Services/IconStyleService` (application-wide singleton, like `ThemeService`; `AppearanceService`
  feeds it from the settings) writes the effective style to the application resource `Ad.IconStyle` and follows
  `SystemParameters.HighContrast` at run time. The icon elements read the resource through a private dependency
  property set with `SetResourceReference` (`AffectsRender`), so every icon of every open window of every connection
  is redrawn at once and no element subscribes to an event. Without the resource an element draws colour.
- **Drawing.** `ObjectIconView` draws `AppIcons.KeyFor(icon)` with `DrawingContext.DrawImage` into the `Size` square:
  the frozen `DrawingImage` of the resources, the same instance for every element (a static table by enum value
  saves the resource lookup per render), nothing cloned, measure and arrange unchanged, the square snapped to a
  device pixel of the element. *Disabled* and *locked* are their own pictures (`UserDisabled`, `UserLocked`,
  `ComputerDisabled`) with the overlay drawn in, so the glyph badges are drawn only in the monochrome style, which
  is today's code unchanged. `AreaIconView` (same shape: `IconKey` + fallback `Glyph`) is for pictures without an
  `ObjectIcon` value.
- **Dimmed rows.** A glyph is dimmed on a greyed row because it is drawn in the row's text colour (the object list
  shows disabled accounts in the secondary text colour; the theme greys disabled and pressed rows). A picture has
  its own colours, so it follows through its opacity (`IconDrawing.PictureOpacity`): next to full-strength text
  (alpha of the inherited `Foreground` at least 0.85 – the primary text colour is 0.89 on the light and 1 on the
  dark theme) it is drawn as it is, otherwise with the alpha of the text colour (secondary 0.62 / 0.77, tertiary
  0.45 / 0.53, disabled 0.36), never below 0.3. No property is added and nothing is pushed for full-strength rows.
- **Where.** Every existing use of `ObjectIconView` (tree, lists, tabs, details pane header, property sheet,
  pickers, document headers) shows the colour icon without a change in the views. Changed views: the document tabs
  use 16 px instead of 14 px, as do the pills of the chosen objects in the object picker (table in 4.4), the connect
  window has the *connection* picture at 48 px in its header, the group list of the bulk creation rules shows each
  group's object icon instead of one glyph.
- **Left as glyphs on purpose:** actions (toolbar, menus, chevrons, copy and close buttons), status and severity
  glyphs (error bars, the Watchdog connection state, list severities – they become status icons with the status line
  in V2), the well-known container badge and the loading mark in the tree, the mail glyph of the transport lines.
- **Not done here:** document headers keep 18 px (20 px comes with the page header of V3) and the property sheet
  header its present size; the domain controller rows of the connect window's server list have no icon yet; the
  check of 125 % and 150 % scaling on screenshots and the list render benchmark need Windows CI.

### V2 – Feedback: status line, command shape, action button (L)

- **Scope:** `PageStatus` and `OperationScope` in Presentation (with the session-log copy and the value-free log
  template); controls `StatusLine`, `ActionButton`, `StatusPill`, `EmptyState`, `Skeleton`; taskbar progress; the
  close question tied to running activities; replace `Ad.ErrorBar`/`Ad.WarningBar` (106 uses in 54 files),
  first in the shell, object lists and property sheet, then area by area; wording rules of 4.5 applied to the
  status strings.
- **Files:** `Presentation/Infrastructure` or `Services`, `Presentation/Changes`; `Controls/`, `Themes/`; most
  feature folders; `Strings*.resx`.
- **Tests:** view-model tests of status and operation scope (the bulk); one UI test per control; "cancel returns
  control" per long operation; tests that looked for the old banner elements follow.
- **Screenshots and docs:** pictures that show banners; a short "How AD Manager tells you what happened" section in
  the guides.
- **Exit criteria:** G2 and G3 of section 1; no window shows two banner styles.
- **Risks:** width of the sweep; many strings in two languages.

### V3 – Page skeleton: header, toolbar card, list toolbar (L)

- **Scope:** `PageHeader` with description and guide link for every document type; `ToolbarCard`; `ListToolbar`
  (filter, Total/Displayed, columns, export, selection menu, selected count) on the document lists; the fixed part
  names of 6.1; compact density tokens everywhere; tooltip and access-key sweep on the pages touched; copy audit.
- **Files:** the document templates of `MainWindow.xaml` and their views (`EventLogs/`, `GroupPolicy/`, `Pki/`,
  `Readiness/`, `Views/Watchdog/`, `ObjectListView.xaml`); `Presentation` document view models; `Strings*.resx`.
- **Tests:** skeleton source test; description and guide link per document in both languages with link check; list
  toolbar tests on one list of each kind.
- **Screenshots and docs:** every document picture; guides get the same page names as the program.
- **Exit criteria:** G5; each page shows header, status, toolbar, content in this order.
- **Risks:** per-list differences (virtual lists, grouped lists) that resist one toolbar.

### V4 – Shell: title bar, search, areas, Home (L; the riskiest stage)

- **Scope:** custom title bar with the connection identity, back and forward, search, activity, help; unified
  search (6.5) with highlight; area strip (N2) or pane (N1) per the owner's decision; Home document; breakpoints
  and persisted layout; menus per 6.3.
- **Fallback:** if `WindowChrome` cannot be made reliable (snap layouts, RDP, High Contrast, Windows 10), the same
  content goes into a 40 px row under the system title bar. Nothing else depends on which.
- **Files:** `Views/MainWindow.xaml` and code-behind, `Presentation/Shell`, `Presentation/Navigation`,
  `Services/WindowService.cs`, `SmokeTest.cs`.
- **Tests:** `MainWindowTests`, `MultiWindowTests`, `NavigationTrackTests` updated; G1 index test; title bar tests
  (drag area, caption buttons, identity colour); narrow-width layout test.
- **Screenshots and docs:** the main-window pictures; README feature section and both READMEs' screenshots; the
  guides' "where is what" passages.
- **Exit criteria:** G1; every menu command of 0.15.0 is reachable by search and by at least one visible path; two
  connection windows remain distinguishable at a glance.
- **Risks:** `WindowChrome` on the supported systems; tests bound to menu items; habits of ADUC users.

### V5 – Settings as cards, one dialog style, teaching tips (M)

- **Scope:** settings page with categories and cards (6.6); one dialog layout; teaching tips for at most eight
  places; *What's new* card.
- **Files:** `Views/SettingsWindow.xaml`, `Views/MessageDialogWindow.xaml`, `Services/DialogService*.cs`,
  `Presentation/Settings`, `Presentation/Dialogs`.
- **Tests:** `DialogWindowTests` and settings tests follow; safe default button test over all confirmations.
- **Screenshots and docs:** settings and dialog pictures; guides' settings chapters.
- **Exit criteria:** every destructive confirmation defaults to the safe button and names the domain; every setting
  is found by search.
- **Risks:** the Messages settings (salutations, Outlook) are large and specific.

### V6 – The large windows (L)

- **Scope:** skeleton, cards, tokens and density in `PropertiesWindow` (2,340 lines), `CorrespondenceWindow`
  (1,742), `BatchEditorWindow` (1,505), `AccountReviewWindow` (1,116), `UserMessagesWindow` (905),
  `CompareObjectsWindow`, `RecycleBinWindow`, `ComputerReviewWindow` and the bulk-create wizard; property sheet
  header card.
- **Tests and screenshots:** the window tests and pictures of each.
- **Exit criteria:** no window of the program is left in the old look; G6 holds for the whole app.
- **Risks:** sheer volume (about 11,000 lines of XAML); the rich message editor.

### V7 – Audit: accessibility, keyboard, performance, documentation (M)

- **Scope:** High Contrast and screen reader pass over every window; *Keyboard shortcuts* page; reduced-motion check
  on a remote session; budgets re-measured; final screenshot refresh; an ADR for the design language; guides in
  both languages brought in line.
- **Exit criteria:** all goals of section 1 hold with their tests in CI; the open points of *Not established* that
  concern shipped behaviour are closed.

### How the stages could interleave with the features (proposal)

**Decided by the owner on 2026-11-10:** V1 comes first, before R1. The rest of the table stays a proposal.

The agreed feature order is R1 (PowerShell window), then P2 (certificate issuance), then N (DNS and DHCP). A renewal
stage never blocks a feature, and a new feature window is written against the newest renewed parts, so nothing is
restyled twice:

| Release (in order) | Feature | Renewal | Why together |
|---|---|---|---|
| next | – | V1 | small, visible, sets tokens and icons before new windows appear |
| then | R1 | V2 | the PowerShell window is the first user of the status line and of "cancel where you started" |
| then | – | V3 | page skeleton before the next large feature |
| then | P2 | – | P2 pages are born with the skeleton |
| then | – | V4 | the shell change gets a release of its own |
| then | N (first stage) | V5 | new area in the strip; settings as cards |
| then | – | V6, V7 | the large windows, then the audit |

## 9. Open questions for the owner

**Decided on 2026-11-10:** question 1 – A1 (evolve the WPF app in place with our own controls); question 2 – N2 (the
tree stays primary, with the area strip); question 9 – V1 goes before R1. Until the owner says otherwise, V1 is built
with the recommended answers to the questions it touches: 4 (colour icons by default, monochrome as a setting),
6 (motion follows Windows) and 10 (Windows 10 and the server systems stay supported, with a solid background). The
other questions stay open until their stage.

1. **Technology:** do we evolve the WPF app in place with our own controls (A1, recommended), adopt WPF-UI (A2), or
   start a WinUI 3 shell (B)?
2. **Navigation:** does the tree stay primary with an area strip (N2, recommended), or do we build a navigation pane
   with the tree inside *Directory* (N1)?
3. **Menu bar:** keep it visible, show it only on Alt, or remove it once search and the area strip exist?
4. **Icons:** colour icons by default with monochrome as a setting (recommended), or the reverse as in the reference?
5. **Title bar:** a custom 48 px title bar with the search box (recommended), or the search in a row under the
   system title bar on every system?
6. **Motion in remote sessions:** follow Windows only (recommended), or default to off in every remote session?
7. **Density:** compact as the default with *comfortable* as an option (recommended), or compact only?
8. **After connecting:** open Home (recommended) or return to the place in the tree where the operator left off,
   with Home as a tab?
9. **Order:** may V1 go before R1 (recommended), or do the agreed features come first?
10. **Supported systems:** do Windows 10 22H2 and Server 2019/2022 stay supported through the renewal, accepting a
    solid background there instead of Mica?
11. **Property sheet:** pages as tabs across the top as today, or as a list on the left like the settings?
12. **Guide links:** open the guide on GitHub (needs internet access from the admin workstation) or ship the guides
    with the program?
13. **What's new:** the release notes in `docs/releases` are Polish only – do they become bilingual for the in-app
    card?
14. **Animated icons:** none (recommended), or a few hand-made ones later (refresh, done)?
15. **Dialogs:** real windows with one style (recommended), or in-window overlays like the reference?

## Not established

- How the reference and our renewed screens look and feel when running – nothing was run or seen.
- Whether our windows show the theme's Mica backdrop today or cover it with an opaque background.
- Whether Windows Server 2025 and remote sessions draw the backdrop, and whether a remote session reports
  `ClientAreaAnimation` as false by default.
- Whether the storyboards inside the WPF Fluent theme follow the Windows "Animation effects" setting.
- `WindowChrome` with the theme's backdrop: snap layouts, caption buttons, RDP, High Contrast, Windows 10.
- The drawing cost of colour vector icons in long virtualized lists, and the ClearType effect of opacity
  animations – both to be measured in V1.
- Today's baselines: time to first rows for 10,000 objects, start-up time, UI test run time, whether screenshots
  are identical between two runs, the main window's minimum size.
- WPF-UI beyond its NuGet metadata and public API: coexistence with `Application.ThemeMode`, behaviour of its list
  controls with our lists, accessibility of its controls, the licence of its embedded icon fonts, maintenance
  outlook.
- Windows App SDK: self-contained size for x64, single-file publishing of an unpackaged WinUI 3 app, WinUI 3 on
  Windows Server 2019/2022, UI test tooling, and SignPath Foundation's view of its licence.
- Whether our localization already has a helper for Polish plural forms.
