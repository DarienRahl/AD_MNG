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

As built (V1b): the aliases of this table were not introduced – views and styles name the Fluent keys with
`DynamicResource` and a source test keeps the set closed; `Themes/Tokens.xaml` holds only what the theme does not
have. The reason is in *V1b as built* (section 8).

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
| `Motion.Ring` | 1 s per turn | **ours** – the progress ring (catalogue row 11); progress, not motion: not behind the switch |
| `Motion.Offset.Page` / `Motion.Offset.Panel` | 24 px / 8 px | reference uses 50 px for pages and 24 px for panels; **ours** are smaller for a dense tool |
| `Motion.Ease.Enter` | cubic ease-out; key spline `0,0,0,1` | reference (`InfoBarV2.cs:189`, `SettingsExpander.xaml:166-227`) |
| `Motion.Ease.Exit` | cubic ease-in; key spline `1,1,0,1` | reference (`InfoBarV2.cs:1438`, `SettingsExpander.xaml`) |
| `Motion.Ease.Change` | quadratic ease in-out | reference: hover and content swap |

### 5.2 Catalogue

"Behaviour" means an attached property in `src/AdMng.App/Behaviors/Motion.cs` (new) that starts a storyboard; "VSM"
means visual states in a control template of `Themes/`.

As built (V1c): rows 1 (the icon button only), 4, 5 and 7 exist, and none of them is a storyboard or a visual state –
every motion is started from code by a behaviour, for the reason given in *V1c as built* (section 8). The column
*Built with* is the plan, not the code.

As built (V2a): rows 2 and 3 and the ring of row 11 exist, built the same way (`Motion.Swap`, `Motion.Play`,
`Motion.Turn`); of row 3 only the entrance of the new message. See *V2a as built* (section 8).

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

As built (V1c): the tests of the motion switch the static flag `Motion.IsEnabled` on in a scope and assert that an
animation was started on the element (`HasAnimatedProperties`); the screenshot writer asserts that the flag is off.

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

As built (V2a): `PageStatus` exists on two pages – the object list and the Find window – as a `c:StatusLine` over the
`PageStatusHolder` of the page view model; a source test keeps the list of converted pages. The status bar of the main
window no longer repeats the summary of a tab that has its own line. See *V2a as built* (section 8).

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
  As built (V2b-1): the status line is there, between today's header and the pages, for the window and for the page
  in front (*V2b-1 as built*, section 8); the header card is not.
- **Multi-window connections** keep their identity (`docs/dev/multi-window.md` 4): the custom title bar carries the
  connection colour and the domain-and-account title; each window remains one DI scope. New per-connection services
  (page status hub, search index, home document) are scoped and added to `ConnectionLifetimes.cs`; `MotionService`,
  icon style and theme are application-wide.
  As built (V2a): there is no page status hub – a status belongs to its page view model (*V2a as built*, section 8).

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

- The status line is a live region: polite for working and done, assertive for errors. As built (V2a): attention is
  assertive too, the summary is polite.
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
  without the value or with a value this version does not know loads as, `TolerantEnumJsonConverter<TEnum>`
  (called `IconStylePreferenceJsonConverter` until V1b made it generic), so such a file is not taken for corrupt – or
  `Monochrome`), chosen in *Settings › General › Appearance › Icons* next to the theme. The
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

**V1b as built (tokens and surfaces).** The second increment of V1; motion (`Themes/Motion.xaml`, behaviours, the
*Motion* setting) follows in the next one.

- **Tokens.** `Themes/Tokens.xaml`, merged into the application resources before `Controls.xaml`, holds what the
  Fluent theme does not have: spacing (`Ad.Space.Page` 16,12 · `Card` 12 · `Toolbar` 4,2 · `Panel` 4), density
  (`Ad.Size.Control` 28 · `Row` 26 · `ColumnHeader` 28 · `PointerTarget` 24 · `PanelGap` 8), the type ramp
  (`Ad.FontSize.Caption` 12 · `Compact` 12 · `Body` 14 · `Subtitle` 20 · `Title` 28), radii (`Ad.Radius.Control` 4 ·
  `Card` 8 · `Pill` 12) and two `Border` styles, `Ad.Surface.Panel` and `Ad.Surface.Toolbar`. They are read with
  `StaticResource` (a token does not change while the application runs). `Controls.xaml` reads them where the value
  was the same (`Ad.Card`, `Ad.ErrorBar`, `Ad.Overlay`, `Ad.Title`, `Ad.DialogFooter`). No duration is a token here.
- **Colours are not aliased** (decided while building this increment, not one of the owner's decisions of
  section 9; it replaces the alias table of 4.1). A XAML alias – a
  `StaticResource` element as a dictionary entry with `x:Key` and `ResourceKey` – is resolved once and would keep the
  brush of the old theme after a live switch of light, dark or High Contrast. So styles and views keep
  `{DynamicResource <Fluent key>}`, which is what 3.4 asks for anyway (names equal to the Fluent keys), and a source
  test keeps the set closed: `XamlSourceTests.FluentBrushKeysInUse_AreTheDocumentedSet` lists the 24 Fluent brush
  keys in use and fails on a new one and on a brush read with `StaticResource`. Found on the way: the markup compiler
  of SDK 10.0 does not compile such an alias entry correctly at all (as the first entry of an implicit dictionary it
  is taken for the dictionary itself; as a later entry the key table of the BAML loses a key – checked by compiling a
  throwaway project and reading its BAML), so `XamlSourceTests.NoXamlFile_AliasesAResourceWithAnElement` forbids it
  for every kind of resource.
- **Surfaces in the main window.** The toolbar row (toolbar, address bar, quick search) is a card, `ToolbarCard`
  (`Ad.Surface.Toolbar`: card fill, 1 px card stroke, radius 8, padding 4,2; margin 8,0,8,8). The workspace
  (margin 8,0,8,8) is three panels of `Ad.Surface.Panel` (layer fill, 1 px card stroke, radius 8, padding 4) on the
  window background: `TreePanel` around the tree and its drop-refused note, `DocumentPanel` around the tab strip
  and the document host, and `DetailsPane`, which lost its own background and left border. The two splitters are
  the gaps: transparent, `Ad.Size.PanelGap` wide (they were 8 px wide before as well – the theme's minimum width
  beat the declared 5). No clip, opacity mask or effect: children are rectangles and WPF does not clip them to a
  rounded border, but the corner of a rectangular child is inside an outline of radius 8 once the child is inset
  by 8 × (1 − 1/√2) ≈ 2.4 px, so 4 px of padding keep every child inside (and nothing is drawn through an
  intermediate surface, 4.3). Menu, connection strip, status bar, toasts and palette are untouched; no element
  was renamed, the first splitter got a name (`TreeSplitter`).
- **Widths.** The panels take 26 px from a row (the two margins of 8, and border 2 + padding 8 of the document
  panel). The document views are fitted to the old document width – the Watchdog feed is laid out so that its four
  required columns (600 px) just fit a feed of about 634 px at the default 1360 × 840 (comment in
  `WatchdogEventsView.xaml`), and its test wants 260 px for the event title at 1440 × 900 – so the tree column is
  254 instead of 280 by default: a document is exactly as wide as before at every window size
  (`DesignTokenTests.MainWindow_ADocumentIsAsWideAsBeforeThePanels`), and the tree's content is 244 px (36 less). The minimums (tree 160, documents 320, details 200), the details width the
  code-behind remembers (300) and `UpdateDetailsPane` are unchanged; no width is stored in the settings. The height
  is not compensated: a document is 26 px lower (toolbar card 6 + 2, bottom margin 8, panel 10).
- **Literal sizes of the V1 views.** `MainWindow`, `ConnectWindow` and `SettingsWindow` read `Ad.FontSize.*` and
  `Ad.Radius.*`. Values that changed: the object name in the details pane and the section titles of the settings
  15 → 14; the DN in the details pane, the number on the Watchdog tab badge and the "never stored" note of the
  connect window 11 → 12; the corner of the virtual list badge 3 → 4. Left literal on purpose: every glyph size of
  the icon font, and the radius of the two pills (connection badge 11, tab badge 9; a pill's radius is half its
  height, the same as the chip toggle 13 at height 26).
- **Column header height.** A Fluent key cannot be given a token in XAML (a number resource cannot refer to another
  resource, and the alias is out, see above), so the attached property `b:Density.ColumnHeaderHeight` on the list
  writes `Ad.Size.ColumnHeader` under the theme's key `GridViewColumnHeaderMinHeight` into the list's own
  resources. Used by `ObjectListView`; the other 35 XAML files with the literal override follow with their stages.
- **Row heights are untouched.** The object list rows are `ObjectRowStyle` (based on the theme's grid view item
  style): `Height` 26, `MinHeight` 0 (the theme's 40 switched off), margin 0, padding 2,0 – equal to `Ad.Size.Row`
  but still literal; the Watchdog lists use `Wd.ListRow` with 28. Tree rows have no height rule (padding 2).
- **Setting *Background*.** `AppSettings.WindowBackground` (`WindowBackgroundPreference`: `Theme` – the default and
  what a missing or unknown value loads as – or `Solid`), chosen in *Settings › General › Appearance › Background*
  under *Icons*. `Services/WindowBackdrop.Apply` sets the application context switch
  `Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop` for `Solid` and leaves it alone otherwise.
  It runs in `AppHost.InitializeAsync` – after the settings were loaded, before `AppearanceService` applies the
  theme mode of the settings and before any window exists – the same in the application, the smoke test and the UI
  tests. The theme reads the switch when the first window gets its style (a data trigger of the window style) and
  its backdrop (`WindowBackdropManager`, cached), not when `ThemeMode` is set in `App.xaml`, so this is early
  enough; a later change of the setting waits for the next start, which the hint under the box says. The JSON
  converter of the icon style became the generic `TolerantEnumJsonConverter<TEnum>` and serves both settings.
- **Settings window.** With the two appearance choices of V1 the content of the *General* tab is about 900 px high
  (added up from its XAML: six sections, not measured), more than a small or scaled screen shows, and a window
  sized to its content cannot be scrolled; the tab now scrolls at 600 px like the *Messages* and *Computers* tabs.
- **Tokens without a user yet:** `Ad.Size.Control`, `Ad.Size.Row`, `Ad.Size.PointerTarget`, `Ad.FontSize.Compact`,
  `Ad.FontSize.Title` and `Ad.Radius.Pill` are defined for the next stages (density of controls and rows, page
  header, pills).
- **Not done here:** `Themes/Watchdog.xaml` and the views outside the four of V1 keep their literals (source test
  G6 comes when they are converted); `Ad.Text.Compact` as a text style; the page margin `Ad.Space.Page` in the
  views; the double inset where a document view has its own 8 px margin inside the panel (page skeleton, V3).
  Nothing of this increment was seen on a screen: whether Mica shows through the panels as intended, the look of
  the 8 px gaps on hover, and the settings window height need the CI screenshots and a Windows 11 machine.

**V1c as built (first motion).** The third increment of V1: the motion tokens, the one switch, the setting and
the first four places that move. No structure change, no element renamed.

- **Decision: motion is started from code, not written in XAML.** There is no `Storyboard`, no animation element
  and no visual state of ours in any XAML file. A storyboard inside a template is frozen with the template and
  cannot take a duration that is read when the motion starts, and reduced motion must set the final values directly
  (5.4), not run a zero-length storyboard; code does both in one place. So `Behaviors/Motion.cs` holds attached
  properties that start `DoubleAnimation`s with the durations, offsets and easings of `Themes/Motion.xaml`, read by
  key each time a motion starts, behind one switch (`Motion.IsEnabled`). The views only say *what* enters
  (`b:Motion.Entrance="Page"`), never how long or along which curve.
- **Tokens.** `Themes/Motion.xaml`, merged after `Tokens.xaml`: the table of 5.1 as resources – twelve `Duration`
  entries (`Motion.Hover` 83 ms · `Press` 120 · `Swap` 150 · `Fast` 167 · `Appear` 250 · `Disappear` 167 ·
  `Entrance` 200 · `Expand` 333 · `Collapse` 167 · `Highlight` 2 s · `Shimmer` 1.6 s · `HoverIntent` 500 ms),
  three offsets (`Motion.Offset.Page` 24 · `Panel` 8 · `Toast` 16 – the toast offset is the 16 px of catalogue
  row 7, a token now) and three easings (`Motion.Ease.Enter` cubic ease-out · `Exit` cubic ease-in · `Change`
  quadratic ease in-out; easing functions, not key splines). Used so far: `Hover`, `Fast`, `Appear`, `Disappear`,
  `Entrance`, the three offsets and the three easings; the other durations wait for their stages.
- **The one switch.** `MotionPreference` (`FollowWindows` – the default and what a missing or unknown value loads
  as – `On`, `Off`) in `AppSettings`; the pure rule `MotionRules.Effective(preference, windowsAnimations,
  hardwareAccelerated, forcedOff)` in `AdMng.Presentation/Motion`; `Services/MotionService` (application-wide
  singleton like `IconStyleService`, fed by `AppearanceService`) evaluates it with
  `SystemParameters.ClientAreaAnimation` and the rendering tier (`RenderCapability.Tier >> 16` above 0), follows
  both while the application runs (`SystemParameters.StaticPropertyChanged`, `RenderCapability.TierChanged`) and
  publishes the answer as the static `Motion.IsEnabled`. The flag is false while no service exists, so an element
  created without the host never animates. Forced off for the whole run: the environment variable
  `ADMNG_UI_TESTS=1`, `--smoke-test`, and `MotionService.ForceOff()`, which the smoke test and the UI test host
  call before the first window. The switch is read when a motion starts: switching it off lets a running motion
  (at most a quarter of a second) end and applies from the next one.
- **Reduced motion.** With the switch off every behaviour sets the natural final state directly – no clock is
  created at all; a dismissed toast is removed at once, as before V1c. Nothing is ever started on an element that
  is not visible. Progress bars are untouched. The theme's own storyboards (expander, combo box, scroll bar) are
  not gated, as 5.4 says.
- **Behaviours** (`Behaviors/Motion.cs`). They animate only `Opacity` and a `TranslateTransform` the behaviour
  puts on the element (an element that has a render transform of its own is not moved), never a size or a margin,
  and never touch `IsHitTestVisible` or `IsEnabled`. When a motion ends its clocks are removed
  (`BeginAnimation(property, null)`) and the transform is taken off: the element has its base values again and
  `HasAnimatedProperties` is false – nothing is left for scrolling or a screenshot to meet. A new motion replaces a
  running one where it stands.
  - `Motion.Shown` (bool): the hover layer – opacity to 1 or 0 over `Motion.Hover` with `Motion.Ease.Change`. At
    the end the value is written as a plain local value and the clock removed (the clock does not hold it).
  - `Motion.Entrance` (`MotionEntrance`: `None`, `Page`, `Panel`, `Toast`) with `Motion.EntranceTrigger`: plays when
    the element becomes visible (also when it is loaded visible) and when the trigger changes to a value that is
    not null. *Page*: rises by `Offset.Page` over `Motion.Entrance`, moved and never faded (a fade draws the subtree
    through an intermediate surface and the text of a list loses ClearType meanwhile). *Panel*: fades in and rises
    by `Offset.Panel` over `Motion.Fast`. *Toast*: fades in and slides in from the right by `Offset.Toast` over
    `Motion.Appear`. All with `Motion.Ease.Enter`.
  - `Motion.Leaving` (bool): fades out and slides right by `Offset.Toast` over `Motion.Disappear` with
    `Motion.Ease.Exit` and holds that end until the owner removes the element – the one place that keeps a clock,
    on an element that is about to go. An element on its way out does not enter again.
- **Where it moves.**
  1. *Icon buttons* (catalogue row 1). `Ad.IconButton` – and with it `Ad.SmallIconButton` and `Ad.CopyButton` – has
     the one control template of ours: a transparent border, inside it the border `HoverLayer`
     (`SubtleFillColorSecondaryBrush` with a 1 px outline of `ControlStrokeColorDefaultBrush`, opacity 0,
     `b:Motion.Shown` template-bound to `IsMouseOver`) and the content. The outline is for High Contrast: there the
     subtle fills are the button face colour, usually equal to the window colour, and the control stroke is the
     button text colour; in light and dark it is 6–7 % of black or white.
     Pressed switches the layer's fill to `SubtleFillColorTertiaryBrush`, disabled sets the content to
     `TextFillColorDisabledBrush`; neither is animated and no theme brush is ever animated. The style is still
     based on the theme's button (focus visual, sizes, tool tip on a disabled button). Seen at rest: a disabled icon
     button lost the theme's faint fill and outline and shows only its dimmed glyph; on hover the fill is the
     subtle one instead of the button fill, 1 px inside the button's edge (inside the transparent border).
  2. *Document* (row 4). The container of a document in `DocumentHost` has the page entrance (a style setter after
     the visibility setter). It becomes visible when its document is opened or its tab is chosen – and when the
     window is shown; nothing inside a document changes it, so a refresh, paging or a selection never replays the
     entrance (rule 3 of 5.3). `DocumentHost` clips to its bounds (`ClipToBounds`), because the document starts
     24 px below its place. The header and toolbar fade of row 4 waits for the page header of V3.
  3. *Details pane* (row 5). The pane's content (`DetailsPaneContent`, a name added for it) has the panel entrance:
     it plays when the pane is opened (Ctrl+D, or a tab with objects after the Watchdog tab), not when the selected
     object changes.
  4. *Toasts* (row 7). The root of the toast template has the toast entrance and `b:Motion.Leaving` bound to the
     new `ToastViewModel.IsLeaving`. `NotificationService.Dismiss` marks the toast as leaving and removes it after
     `Motion.Disappear` (a dispatcher timer; the same token the animation reads); with motion off, after the window
     closed, and when room is made for a new toast (the limit of four holds at every moment) it removes at once.
     Dismissing twice or while leaving does nothing more.
- **Setting *Motion*.** *Settings › General › Appearance › Motion* under *Background* (`MotionBox`, `MotionHint`):
  *Follow Windows*, *On*, *Off*; applied as soon as the settings are saved.
- **Where this differs from the plan of section 5.**
  - *Pressed goes through `Motion.Shown`.* The behaviour sets the layer's opacity as a local value, and a local
    value outranks a template trigger; a trigger that set the opacity itself would stop working on a button the
    pointer had visited. So the pressed trigger sets `b:Motion.Shown`, and a press from the keyboard (no pointer
    over the button) fades the layer in over `Motion.Hover` instead of showing it at once; the pressed fill
    switches at once. `Motion.Press` (the small scale of the reference) is not used.
  - *No visual states* – see the decision above; `CommonStates` of row 1 are a template binding and two triggers.
  - *Only the icon button.* `Ad.IconToggleButton`, `Ad.ChipToggleButton` and the dialog buttons keep the theme's
    templates in this increment: their checked and accent states are the theme's business, and a template of ours
    would have to redraw them.
- **Tests.** Presentation (run on Linux): the rule table, the settings round trip and unknown values, the view
  model choice, the texts. UI tests (`MotionUiTests`, Windows CI only, written without a screen): the tokens, the
  service, each behaviour with motion off (final values, `HasAnimatedProperties` false) and on (a test switches the
  flag on in a scope and asserts that an animation was started, never a frame), the template, the main window
  wiring and the toasts; the end of a motion is asserted against a clock of the test's own and skipped where
  animation clocks do not advance. `ScreenshotWriter` asserts that the switch is off when a picture is taken.
  Source tests: rule 6 of 5.3 (`XamlSourceTests.NoViewWritesAMotion`) and
  `MotionDictionary_IsTheOnlyPlaceOfDurationsAndEasings`, which also forbids storyboards in the dictionaries; the
  documented Fluent brush set has 27 keys (`ControlStrokeColorDefaultBrush`, `SubtleFillColorTertiaryBrush` and
  `TextFillColorDisabledBrush` added).
- **Left for V2 and later:** the action button and its content swap (row 2), the status line (3), transient
  surfaces (6), the "look here" highlight (9), the skeleton (10), the ring (11), the area indicator (12), the
  refused drop (13); hover feedback in the toggle and chip templates, the dialog buttons and cards; the header and
  toolbar fade of the page entrance; whether a remote session should default to off (open question 6).
- **Not seen, to confirm on Windows:** nothing of this was seen moving. Open: how sharp the text of a list stays
  while its document is moved (row 4's "to confirm in V1" stands); how much of the entrance of a *new* document is
  seen – its clock starts when the container becomes visible, so a first layout that takes as long as the entrance
  shows only its end (it never delays input, but may show no movement); the look of the subtle hover and pressed
  fills on the toolbar card, and of the outline alone in High Contrast.

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

**V2a as built (status line and action button).** The first increment of V2: the model of "what a page says", the two
controls that show it, and two pilot pages. Everything else keeps its banners until V2b; `StatusPill`, `EmptyState`,
`Skeleton`, taskbar progress and the close question of the scope above are not started.

- **Model** (`AdMng.Presentation/Feedback`, no WPF type). `PageStatus` is the immutable value: a kind (`Idle` – the
  neutral summary – or one of the four severities `Working`, `Done`, `Attention`, `Error`), the text for the screen, a
  `PageProgress` (indeterminate or a fraction, only for `Working`), at most one `PageStatusAction` (label and
  command), `CanClose`, and the copy for the diagnostic log (`PageStatusLog`: message key, counts, `ResultCode`). The
  fixed title is not part of the value: the view takes it from the kind (`PageStatusText.Title`), so no page can give
  a severity another name. `PageStatusHolder` is the observable holder a page view model owns
  (`IHasPageStatus.Status`): `SetIdle` (the summary, shown whenever nothing else is), `Begin` (an operation), `Show`
  (a result), `Clear`, and the commands `CloseCommand` and `CancelCommand`. `OperationScope` is one running operation:
  `Token`, `Report`, `Cancel`, `Complete…`, `Dispose`.
- **Decisions of the model.**
  - *No hub and no service.* 6.8 planned a per-connection "page status hub". A status never leaves its page (what
    finished elsewhere is a toast, what runs in the connection is the activity list), so the holder is created by the
    page view model and dies with it; nothing was added to `ConnectionLifetimes.cs`. The only thing that comes from
    the connection scope is the logger, through the scoped `ObjectListServices.StatusLog`.
  - *A second operation replaces the running one* (its token is cancelled, what it says later is ignored). The first
    page is a list that loads: a new location, F5 or a sort must take effect at once and the old load is of no
    interest. Refusing would have made navigation wait for a 50,000 object container. A page whose operation must not
    be interrupted keeps its command disabled while it runs – the command's business, not the holder's.
  - *"Working" exists only through a scope.* `Show` refuses a `Working` status; `Begin` is the only way in, and a
    scope that is disposed without a completion ends as the summary. With `using var scope = Status.Begin(…)` no path
    out of a method can leave the page in "working".
  - *Cancel answers first.* `Cancel` puts "Stopping…" on the page and only then cancels the token; the operation still
    ends itself (normally as `Attention`: what was stopped and what to do). The first completion stands; a second one,
    a report after the end and everything a replaced operation says are ignored.
  - *Threads.* The holder is a UI-thread object like every view model; a scope may be used from any thread and hands
    what changes the page to the UI dispatcher.
  - *The log copy cannot carry a value* (rule 1 of `CLAUDE.md`). `PageStatusLog` holds a resource key and numbers,
    `ResultCode` can only be made from numbers and names the program produced (LDAP result code, Win32 error, error
    kind, exception type – never a server message). Every new message that is not the summary is logged once with the
    key as the event name (event ids 5200 + kind; error, warning, information, debug by kind); a new count of the same
    message is not logged again. `PageStatus.ToString()` is the kind and the log copy.
  - *One shape of a text.* `PageStatusText.Failure(tried, reason, next, code)` builds "what we tried: why it failed.
    What to do next. (code)" and is used for errors; `PageStatusText.Objects` and `Count` form counts with the plural
    rule the program already had (`CorrespondenceText.Plural`: one, few, many – 1 obiekt, 2 obiekty, 5 obiektów);
    `PageStatusText.Name` is what a screen reader hears ("Error: …").
- **Strings.** The block *V interface renewal (V2)* of `Strings.resx` / `Strings.pl.resx`: the four titles (Working /
  Trwa, Done / Gotowe, Attention / Uwaga, Error / Błąd), "Stopping…", the name of an empty line, the three plural
  forms of "object", and the texts of the two pilots (`List.Status.*`, `Find.Status.Hint`). "Stop", "Retry",
  "Refresh", "Cancel" and "Close" are the existing keys.
- **Motion and tokens.** `Behaviors/Motion.cs` got `Motion.Swap` (catalogue row 2: one content of a swap, shown or
  hidden over `Motion.Swap`), `Motion.Play` with `MotionEntrance.Status` (row 3: fade and rise by
  `Motion.Offset.Panel` over `Motion.Appear`, played once from code) and `Motion.Turn` (row 11: the ring, once per the
  new token `Motion.Ring`, 1 s). `Turn` is the one thing that is not behind the switch – progress is information (5.4)
  – so its caller turns only while visible. `Themes/Tokens.xaml` got `Ad.Size.StatusLine` (28).
- **Controls** (`Controls/`, styles in `Themes/Controls.xaml`). The rule "only string keys in `Controls.xaml`" holds
  for controls of our own as well: `StatusLine` and `ActionButton` take their styles `Ad.StatusLine` and
  `Ad.ActionButton` by a resource reference set in their constructors.
  - `ProgressRing` – the theme has none. A light element like the icon elements (no template, outside UI Automation):
    a faint full circle and an arc in the inherited text colour – a quarter that turns while the progress is not
    known, or the part done, standing still. It turns also with motion off and has a clock only while it is active
    *and* visible (`IsTurning`): a ring in a hidden tab, a collapsed row or a content that is not shown costs nothing.
  - `StatusLine` – shows a `PageStatus` in one line of the fixed height `Ad.Size.StatusLine`, always present: the
    picture of the severity (`Icon.StatusInfo` for the summary, `StatusOk`, `StatusWarning`, `StatusError`, through
    `AreaIconView` at 20 px with the glyphs Info, Completed, Warning, Error as fallback; the ring instead of a picture
    while an operation runs, as 4.4 says), the fixed title in semibold, the message on one line with an ellipsis and
    the whole text as its tool tip, the action as a link (`Ad.LinkButton`: accent text colour *and* an underline, the
    hover layer of the icon button), and a close button (`Ad.SmallIconButton`, 24 × 24) only for what may be closed.
    The summary and a running operation have no fill and the summary is in the secondary text colour; done, attention
    and error have the theme's status backgrounds. The control computes read-only properties (`Kind`, `Title`,
    `Message`, `IconKey`, `IsWorking`, `HasAction`, `CanClose` …) and the template only lays them out. It is a live
    region (`AutomationControlType.StatusBar`, name "Error: …", "Status" when empty): polite for the summary, working
    and done, assertive for attention and error; a new message raises the live region event, a new count of the same
    message and a changed summary do not. Its own words (title, name) follow a language switch at once.
  - `ActionButton` – a `Button` with the theme's own template and a content template with three contents in one cell:
    its own content (with an optional `Glyph`) at rest, ring + "Cancel" while the operation runs, ring + "Stopping…"
    after that. All three stay in the layout and differ only in opacity (`Motion.Swap`), so the button has the width
    of its widest state in every state and nothing animates a size. A press at rest runs `Command`, a press while
    running runs `CancelCommand`, a press while stopping does nothing.
- **Decisions of the action button.**
  - *Bound through plain properties* – `Command`, `CancelCommand`, `IsRunning`, optional `IsCancelling` – not through
    an `OperationScope`-producing command. The existing view models already have a start command, a stop or cancel
    command and a "busy" flag; with plain properties a page adopts the button by changing its XAML only, with or
    without a `PageStatusHolder`. With a holder the two flags are bound to `Status.IsWorking` and
    `Status.IsCancelling`. Without one the button itself remembers that Cancel was pressed – until the operation
    ended, or until the view model reports that the cancellation is over while something still runs (the cancelled
    operation was replaced).
  - *It keeps the keyboard focus by staying enabled.* A start command reports "cannot execute" while it runs, and a
    disabled element loses the focus. While the operation runs the button is enabled whatever the start command says
    (`IsEnabledCore`); it also holds "enabled" for one dispatcher turn after the press and after the end of the
    operation, because the operation and its command do not change in the same moment (WPF looks for a new focus at
    input priority; the hold ends at background priority). "Stopping…" is an enabled button that ignores the press and
    is not hit-test visible; UI Automation is told that it is not enabled.
  - *Enter starts, it never cancels.* As the default button it gets Enter from anywhere in the window as its access
    key; while the operation runs that is ignored unless the button itself has the focus – Enter typed twice into the
    name box must not stop the search it started.
  - *UI Automation:* a normal button and a leaf, named by what a press does now – "Find Now", "Cancel: Find Now",
    "Stopping…".
  - *One trigger per property in the content template.* Two data triggers that set the same property fall back to the
    template's value in the moment between "the first no longer applies" and "the second applies"; with behaviours
    that start a motion on every change that moment is not harmless. So each busy content and each ring is switched by
    exactly one trigger, and the content at rest by the one property `IsBusy`.
- **The two pilots.**
  1. *Object list* (`Views/ObjectListView.xaml`, `Lists/ObjectListViewModel.cs`). The element `PageStatus` sits
     between the header and the filter chips, full width, and shows `Status.Current`: the summary (what `StatusText`
     says, with the selection joined by " · "), a load as the operation of the page ("Reading “Kraków”…", then with
     the number of objects read so far; with Stop – the load's cancellation token is the operation's), a stopped load
     as attention with Refresh, a failed load as an error with Retry that cannot be closed (it is the only explanation
     of the empty list), a deleted container as a lasting attention. The loading row (progress bar and Stop button)
     and the `Ad.ErrorBar` over the list are gone, so the rows no longer move when a load starts or ends; they start
     30 px lower than before. The quick filter status in the header, the "no items" hint and the virtual list are
     untouched; a virtual list's page error goes to the line as well. `StatusText`, `SelectionText` and
     `CancelLoadCommand` are unchanged for their other users.
  2. *Status bar of the main window.* It no longer says the summary and the selection of a tab that has a line of its
     own: `ShellViewModel.StatusBarText` / `StatusBarSelectionText` are empty for a tab that is `IHasPageStatus` and
     equal to `StatusText` / `SelectionText` for the others (Watchdog, event logs, Group Policy, PKI, readiness –
     until their pages are converted). Connection, domain controller, security, activities, version and the virtual
     list badge stay.
  3. *Find window* (`Views/FindWindow.xaml`, `Dialogs/FindViewModel.cs`) – the tool window with a long, cancellable
     operation started by a button. One status line at the top, over the status of the results list
     (`FindViewModel.Status` is `Results.Status`): what to do before the first search, the running search, the
     summary, a stopped or failed search, and – as a lasting error – that the session of the window ended. It replaces
     the error bar at the top and the status text in the footer; the results list inside shows no line of its own
     (`ObjectListView.ShowStatusLine="False"`). *Find Now* is an action button over the two commands the window had
     (`FindNowCommand`, `StopCommand`); the separate *Stop* button is gone.
- **Where this differs from the plan.**
  - *Catalogue row 2:* no visual states – one attached property per content and data triggers (the decision of V1c
    stands). *Row 3:* only the entrance of the new message is built (`Motion.Appear`); the old message does not fade
    out first (`Motion.Disappear`), because the line would have to keep showing a status the page no longer has.
  - *Section 7* said "polite for working and done, assertive for errors". Attention is assertive too (something was
    skipped or stopped and the operator has to act); the summary is polite and is never announced when it only
    changes.
  - *6.7, progress:* in the action button and in the status line. The tab header keeps its loading mark of today; the
    taskbar is not done.
  - *4.1:* one more Fluent brush in the closed set, `SystemFillColorSuccessBackgroundBrush` (28 keys). In High
    Contrast the three status backgrounds are the window colour and the status colours the window text colour: there
    the severity is told by the glyph and the title alone, which is the rule anyway.
- **Tests.** Presentation (run on Linux; the bulk): `Feedback/PageStatusTests`, `PageStatusHolderTests` (every
  transition, cancellation, double completion, dispose without completion, replacement, use from worker threads, the
  log copy and the problem report never containing a text argument, both languages), `Lists/ObjectListStatusTests`,
  `Dialogs/FindStatusTests`. UI tests (Windows CI only, written without a screen; properties, resources and view model
  state, never pixels): `ProgressRingUiTests`, `StatusLineUiTests`, `ActionButtonUiTests` – with `LanguageScope`,
  `MotionScope` and `ControlHost` in the test infrastructure – and `StatusLinePilotTests`, the two pilots through
  their real windows over the fake directory (a slow domain controller is the fake session's cancellable latency, a
  failing one an injected search failure): working with Stop, "cancel returns control", attention with Refresh, error
  with Retry. Source tests: `XamlSourceTests.APageWithAStatusLine_SaysItsStatusThere_AndNowhereElse` – a view on the
  list `PagesWithAStatusLine` has exactly one status line named `PageStatus` over `Status.Current`, no `Ad.ErrorBar`,
  no `Ad.WarningBar` and no `ProgressBar`, and a view with a status line must be on the list; the 81 views that are
  not converted yet are not touched by it.
- **What V2b has to do** (counted in the XAML of this commit, comments excluded): 43 uses of `Ad.ErrorBar` and 58 of
  `Ad.WarningBar` in 51 files, and 65 progress bars.
  - By area (error bars + warning bars, progress bars): windows of `Views/` – property sheet 8 + 3 and 8, batch editor
    2 + 4 and 5, compare objects 2 + 3 and 2, account review 2 + 2 and 3, user messages 2 + 2 and 3, logon hours 0 +
    3, change domain controller, connect, new object, reset password, recycle bin, change plan preview, settings,
    session log, the filter and saved view editors, the pickers and the main window (its toolbar search and activity
    popup) – together 26 + 21 and 34; `GroupPolicy/` 7 + 17 and 8 in 15 files (with the shared
    `GroupPolicyBanners.xaml`); `BulkCreate/` 3 + 5 and 2; `Views/Watchdog/` 2 + 4 and 7; `Computers/` 2 + 3 and 3;
    `Pki/` 2 + 2 and 4; `EventLogs/` 0 + 3 and 3; `Correspondence/` 1 + 1 and 3; `Readiness/` 0 + 1 and 1;
    `Controls/HtmlPreviewView.xaml` 0 + 1.
  - Not every banner is a status: a warning that belongs to one field or one section (a validation text, a "this is
    read only" note) stays where it is and gets the status colours; only what says what the page did moves to the
    line. V2b has to sort the 101 uses into these two kinds first.
  - Per page: give the view model a `PageStatusHolder`, put the summary into `SetIdle` (`StatusText` is mentioned 76
    times in XAML), run the long operation in a scope, build its error with `PageStatusText.Failure`, replace banner
    and progress bar by `PageStatus`, put the file on `PagesWithAStatusLine`, and turn a start button with a separate
    stop or cancel button into an `ActionButton` (candidates: bulk check, computer review, remote computer, event log
    reader and its two analyses, GPO search, cleanup and diagnostics, PKI security review, readiness, account review,
    recycle bin, compare objects, the tests of the filter and saved view editors, the sending of correspondence and
    user messages, Watchdog delivery test, connect). Document tabs then drop out of `StatusBarText` one by one.
  - Still open in the controls: an accent variant of the action button for the primary action of a dialog; the status
    line of a page that has several operations at once (the batch editor); whether "Working" should wait about 150 ms
    before it is shown, so that a load that ends at once does not flash; the status pictures at 16 px for pills.
- **Not seen, to confirm on Windows:** nothing of this was seen on a screen. Open: the look of the line on the status
  backgrounds in light and dark and in High Contrast; whether the info picture next to the summary is too loud in a
  dense window; the ring at 14 px in a button and at 16 px in the line (stroke 1.75 and 2 px); the cross-fade of the
  button contents and the entrance of a message; the width of the Polish "Zatrzymywanie…", which makes *Znajdź teraz*
  wider than the buttons next to it were; that a ring keeps its clock while the window is minimized (a minimized
  window still counts as visible).

**V2b-1 as built (the core windows).** The second increment of V2 is the sweep over the views that still had banners;
V2b-1 covers seven core windows in three units. Unit 1 built what the sweep needs beyond V2a and converted the property
sheet; units 2 and 3 add their windows to this block.

- **Foundations** (what every later unit follows).
  1. *Sort first.* Every `Ad.ErrorBar`, `Ad.WarningBar` and `ProgressBar` of a view is one of three things. **Status** –
     what the window did or is doing (reading, a read that failed, an operation that was stopped or finished, "the
     session of this window ended", "this was changed elsewhere"): it goes to the status line. **Note** – a sentence
     that belongs to one field or one section and is true for as long as that content is shown (a validation text, a
     finding of the planner, "these mappings are weak"): it stays where it is. **Progress of a row** – how far one item
     of a list is: it stays in its row. The test for the first two: *is this the answer to something the operator just
     did, or to something the window is doing?* Then it is status. The same holds for what never was a banner: a count
     line of a page is its summary (`SetIdle`) and leaves the page, an error text that reports a failed read is status.
  2. *A state that lasts* – `PageStatusHolder.SetLasting(PageStatus?)`. "Could not be read", "was deleted", "is out
     of date" are true for as long as the page shows what it shows; with a second operation on the same page a plain
     `Show(…, canClose: false)` would be wiped by that operation and gone for good when its result is closed. The
     lasting state takes the place of the summary: it is said whenever nothing else is, it cannot be closed (whatever
     the status handed in says), an operation and its result cover it for their time, and it is back when the result
     is closed or the operation ended without a message. A new state is said at once, also in place of a result; while
     an operation runs it is kept and said when the operation is over. Setting an *equal* state again (kind, text,
     action) changes nothing, so a view model has **one** method that computes the most important of its conditions
     and calls it whenever one of them changes (`PropertySheetViewModel.UpdateLasting` / `LastingState`, called from the
     `On…Changed` hooks of the properties). Only an attention or an error can last. `Show`/`Complete…` with
     `canClose: false` (the way of the two pilots) stays right for a page with one operation. Every window has one
     such condition from its base class: `WindowViewModelBase.SessionEndedStatus` is "the session of this window
     ended" as a lasting error – the first condition a window checks.
  3. *Several sources in one window: the page in front* – `PageStatusHolder.Front`. A window whose tabs say something
     of their own gives **every page view model a holder** (created like the window's: the localizer, the dispatcher
     and the logger of the scoped services – `Services.Lists.StatusLog`) and tells **its own holder** which page is
     shown: `Status.Front = selectedPage?.Status`, before the page is activated. The one status line stays bound to the
     window's holder (`Status.Current`, `Status.CloseCommand` – the source test wants exactly that); `Current` is then,
     in this order: (1) the window's own running operation, or its own error that cannot be closed – then nothing a
     page says matters; (2) otherwise whatever was said last, by the window or by the page in front, that is not a
     summary – a page says its status again when it comes to the front, a new count of the same message is not "saying
     it again"; (3) otherwise the summary of the page in front or, when it has none, of the window. Nothing is lost by
     that – a status that is not shown stays in its holder: a tab that failed while it was not selected says so when
     it is selected, a result of the window that was covered by a page is back when the page has nothing more to say,
     and a page that finishes loading does not take a result of the window off the line. `CloseCommand` closes what is
     shown in the holder that said it; a "Stop" in the line is the command of the holder whose operation it is.
     Everything else of a holder – `IsWorking`, `IsCancelling`, `Operation`, `CancelCommand` – is about **its own**
     operation, so an action button binds to the holder whose operation it starts. A page in front may have a part in
     front of its own (the "All (nested)" list of the membership pages: the view model learns which inner tab is shown
     through a two-way `SelectedIndex` binding). No hub and no service came back: the holders still belong to their
     view models, and nothing was added to `ConnectionLifetimes.cs`.
  4. *Operations that must not replace one another.* `Begin` on a holder replaces the operation that runs there. Two
     operations that can overlap therefore live in two holders (the window and a page; a page and a part of it), or
     the second is refused by its command while the first runs. An operation that *contains* a step that is an
     operation elsewhere does not begin a second scope for it: the read after Apply belongs to "Saving…"
     (`PropertySheetViewModel._saving`). A "latest wins" token that other code cancels (a `SerialCancellation`) is
     linked with the token of the scope, so both the newer request and the operator's Stop end the work.
  5. *The shape of an operation.* `using var scope = Status.Begin(text, new PageStatusLog(key));` and the token from
     the scope. After every `await`: `scope.IsEnded` means replaced or disposed – say nothing, touch nothing. `catch
     (OperationCanceledException) when (!scope.IsEnded)` is the operator's Stop: complete as attention – what was
     stopped, what the page may lack, and the command that starts it again as the action. A failure that leaves the
     content empty or stale is the state of the page: `SetLasting(error with Retry)` inside the operation, which then
     ends without a message. A failure that leaves the content as it was is a result: `CompleteError`. A result that
     is the content itself (a list that was read) is no message: set the summary and let the scope end. A flag like
     `IsLoading` is ended only by the operation that owns the page (keep the scope in a field and compare), never by a
     replaced one. A window that is closed disposes what still runs (`Status.Operation?.Dispose()`), its pages too.
  6. *Words.* `PageStatusText.Failure(tried, reason, next, code)` for errors and for "what was skipped: why" of an
     attention; `PageStatusText.Progress(localizer, done, total)` – "12 of 300" / "12 z 300" – for an operation over
     many objects, written after what it does ("Saving the accounts: 12 of 300…": no noun has to follow the numbers in
     a plural form) and reported together with `PageProgress.Of(done, total)` and a log copy with the two numbers. That
     is the whole of determinate progress: the ring of the line shows the part done and stands still (checked in
     `StatusLineUiTests`), it stays one message (nothing is announced or enters again), and neither the model nor the
     control needed more. Reports are not coalesced: an operation that reports from a worker thread more often than a
     few times a second should report per batch. New strings go to the block *V interface renewal (V2)*, grouped by
     window as `<Window>.Status.*` with one class of `L` per window (`L.PropertySheetStatus`); "Press Retry to try
     again." is the shared `Feedback.RetryNext`. The log copy is the key, counts and a `ResultCode` – the names of
     servers and objects are on screen only.
  7. *Looks* (`Themes/Controls.xaml`; its header now says where each kind of feedback goes). **Notes**:
     `Ad.Note.Info`, `Ad.Note.Attention`, `Ad.Note.Error` on a `ContentControl` whose content is the text (a wrapping
     `TextBlock`, which keeps its name and its live setting) – the glyph of the severity in its status colour, then
     the content; no fill, no border. Not told by colour alone: each severity has its own glyph and the sentence says
     what it is about. **Banners**: `Ad.ErrorBar` and `Ad.WarningBar` are unchanged for the views that are not
     converted yet and go with the last of them; a converted view uses neither (the source test). **Action buttons**:
     `Ad.AccentActionButton` for the primary action of a window (the theme's accent button with the three contents),
     `Ad.DialogActionButton` and `Ad.AccentDialogActionButton` with the metrics of the dialog buttons; a window that
     needs another size bases a local style on `Ad.ActionButton` (`SmallActionButton` of the property sheet). The
     inline validation texts in the critical colour and the field findings of the property sheet (glyph and text) were
     notes before and were not touched.
  8. *Tests.* Presentation: record every change of `Status.Current` (kind and text) and assert the sequence; a slow
     domain controller is `FakeDirectorySession.Latency` (cancellable), a failing one `FailNext` or an exception thrown
     from `BeforeOperation`. Never lower the latency while a call may still be on its way to it – the call then
     finishes normally and the test is right only most of the time; hold the call in `BeforeOperation` instead. UI
     (Windows CI): one file per window in the style of `PropertySheetStatusUiTests`, asserting only states that last
     until the test changes them. The view goes on `PagesWithAStatusLine`; the source tests are proven here with a
     throwaway script that repeats their expressions.
- **The property sheet** (`Views/PropertiesWindow.xaml`; `PropertySheets/`, the pages of `Computers/` and `Pki/`).
  - *What the line says.* At rest where and when the object was read ("Read from dc01.contoso.test · as of
    12:03:05") or the summary of the page in front ("Groups: 12", "Published certificates: 2", "No key
    credentials.", "5 objects" for the nested list). **Working**: "Reading “Jan Kowalski”…" with Stop; "Saving the
    changes to “Jan Kowalski”…" without Stop; the read of a page ("Reading the “Attribute Editor” tab…", "Reading the
    “All (nested)” list…", the two reads of a computer's *Stan* page, the check of the account on all domain
    controllers) with Stop. **Done**: "The changes to “Jan Kowalski” were saved in contoso.test. The report is in the
    list of background operations."; that nothing had to be saved. **Attention**: nothing was saved and why (values
    that are not valid, a plan the planner refused); a save that was cancelled in the activity list; a read that was
    stopped, with Refresh; not every domain controller answered. **Error**: could not save, with the reasons and what
    to do; could not read a page or a list, with Retry. States that last, the most important first: the session of
    the window ended; the object could not be read (Retry) or no longer exists; it was deleted while the window was
    open; its read was stopped (Refresh); it was changed elsewhere while the sheet has pending changes (Refresh).
  - *How the old places were sorted* (8 error bars, 3 warning bars, 8 progress bars).

    | Old place | Kind | Now |
    |---|---|---|
    | progress bar under the header (`IsLoading`) | status | the read of the window, with Stop |
    | error bar `LoadError` with Retry | status | lasting error of the window, Retry in the line |
    | "deleted" bar (a style based on `Ad.ErrorBar`) | status | lasting error of the window |
    | warning bar "changed elsewhere" with Refresh | status | lasting attention of the window, Refresh in the line |
    | error bar `ApplyError` | status | result of Apply: attention or error |
    | progress bar in the footer (`IsApplying`) | status | "Saving…" in the line |
    | membership pages: progress bar, error bar | status | read with the object; lasting error of the page, Retry reads the object |
    | nested list: progress bar, error bar, *Stop* button | status | operation of the list; *Refresh* is an action button |
    | *Stan* of a computer: progress bar (and an error text) | status | operations of the page, attention or error |
    | published certificates, key credentials, attribute editor: progress bar, error bar each | status | operation of the page, lasting error with Retry |
    | name mappings: two warning bars (weak, invalid) | note | `Ad.Note.Attention` above the lists |
    | count lines of the membership and certificate pages | summary | `SetIdle` of the page; gone from the page |
    | findings of the planner, validation texts, read-only reasons | note | unchanged |

  - *Decisions.*
    - **One computed lasting state** for the window (`LastingState`), not one banner per condition: the old sheet could
      show "could not be read", "deleted", "out of date" and "Apply failed" at once; the line says the most important
      one, and a result of Apply covers it only until it is closed.
    - **A read can be stopped, Apply cannot** – from the line. A read was "latest wins" only; its token is now linked
      with the operation's, and a stopped read is a state of the window ("may not show everything", Refresh). Apply
      keeps its way: planning and execution are not given the operation's token, the execution is an activity of the
      connection and can be cancelled there, as before (rule 5: only how the result is said changed).
    - **Declining to overwrite** a value that was changed on the server ends "Saving…" without a message: the window
      is out of date and says so as its state; the same sentence is not said twice. `ApplyError` still carries it.
    - **A page that is read with the object** (the membership lists) starts no operation of its own – the window is
      working then – but keeps its failure as its state; its Retry is the sheet's Refresh (`FieldContext.Reload`). A
      page that reads when it is shown reads as an operation of its own, also when the sheet reads it again; while
      the window works the line is the window's. The token of the sheet's read is linked into such a read, so Stop in
      the line does not leave the window "stopping" until a tab has finished. **Who was stopped says so:** a page says
      "stopped" only when the operator stopped *its* operation; a read that ended with the read of the sheet, or was
      replaced, ends without a message – the window says that it may not show everything.
    - **The nested list is a part of its page** with a holder of its own, in front while its inner tab is shown.
      Otherwise the line would explain an empty list the operator is not looking at, or lose that explanation with the
      next thing the page says. The inner tab is now remembered per page while the sheet is open (it follows the view
      model); it opened on the direct list every time before.
    - **Reads on demand got a token**: the two reads of the computer's *Stan* page and the account check ran with
      `CancellationToken.None` or could only be superseded; as operations they can be stopped (rule 6).
      `LoadNestedCancelCommand` is gone – cancel is the operation's. A closed sheet ends the reads of its pages.
    - **The line sits 8 px in from the window edge**, like in the Find window, so its picture stands over the icon of
      the header; the window is 34 px higher by default (720 to 754), as much as the line takes. The summary names the
      domain controller and the time of the read: a sheet is modeless and may stay open for long, and what it shows is
      what that one controller answered then (rule 4 of `CLAUDE.md`).
    - `LoadError`, `ApplyError`, `Error`, `IsLoading`, `IsNestedLoading`, `NestedError`, `IsOutdated`, `IsDeleted` and
      `CountText` are unchanged for the tests and view models that read them.
  - *Where this differs from the plan.* 6.8 describes the sheet with a header card (48 px icon with badge, state
    pills, copy buttons): only the status line was placed; the header is the old one until V3/V6. 6.7 puts progress
    "in the action button, the tab header and the taskbar": the tab headers of the sheet carry no mark for a page that
    reads or failed – a failed tab says so when it is selected. The two items left open by V2a – the accent variant of
    the action button and the status line of a window with several sources – are closed by the foundations above;
    "Working" still appears without a delay, and the status pictures at 16 px are still not drawn (the notes use the
    glyphs of the icon font).
  - *Tests.* Presentation: `Feedback/PageStatusSourcesTests` (the lasting state, the page in front, "12 of 300"),
    `Dialogs/PropertySheetStatusTests` (summary in both languages, read with stop, failed read, gone and deleted, out
    of date, Apply done / refused / failed, a tab that failed, the nested list with cancel and failure, a tab that
    reads when shown or with the sheet, the account check, the session that ended, closing the sheet), and the status
    of the certificate tabs and of the computer's page in their test classes. UI: `NoteUiTests`, the accent and dialog variants in
    `ActionButtonUiTests`, the line of a window with a page in front in `StatusLineUiTests`,
    `PropertySheetStatusUiTests` (six tests through the real window), the note in `CertificateTabsUiTests`.
  - *Not seen, to confirm on Windows.* The line in a 640 px wide window: an error with its reason and advice is
    longer than the line and is cut with an ellipsis (the whole text is the tool tip and the name a screen reader
    hears) – whether a narrow window needs a second line is open. The note next to the old validation texts (glyph and
    plain text against text in the critical colour). The width of the nested list's button ("Odśwież" grows to the
    width of "Zatrzymywanie…"). The time of day in the summary, which is now in the two README pictures of the sheet.

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
