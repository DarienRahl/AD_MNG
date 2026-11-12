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
| 3 | Status message change | `StatusLine` | `PageStatus` changes | text and icon fade, 8 px rise | Appear in, Disappear out; Enter/Exit | "a result arrived" – without a pop-up | storyboard in the control | the line is always present and one line high while the page works, so content does not jump (a long result wraps – "V2b-1 as built") |
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
2. **Status line** – always present; one line of a fixed height, except for a result too long for it, which wraps
   to three lines at most (decided with the owner in V2b-1). Idle: a neutral summary ("2,314 objects, 12 selected") – this
   absorbs the `StatusText` bindings found in 32 XAML files. During and after an operation: the four severities.
3. **Toolbar card** – the page's commands; for lists the standard list toolbar.
4. **Content.**

The elements carry fixed names (`PageHeader`, `PageStatus`, `PageToolbar`, `PageContent`), so a source test can check
the skeleton and UI tests can find the parts the same way everywhere.

As built (V2a): `PageStatus` exists on two pages – the object list and the Find window – as a `c:StatusLine` over the
`PageStatusHolder` of the page view model; a source test keeps the list of converted pages. The status bar of the main
window no longer repeats the summary of a tab that has its own line. See *V2a as built* (section 8).
As built (V2b-1): also on the property sheet, the batch editor, the change plan preview, the AD Recycle Bin, the
account review and the comparison of objects. The main window itself has none – it is the shell, not a page; its
"busy" marks are progress rings (*V2b-1 as built*, section 8).
As built (V2b-2): also on the four dialogs that open a session or write one object – the Connect window, "Change domain
controller", "Reset Password" and "New Object" –, on the message window ("Send message to users"), on the settings
window (above its tabs), on the filter builder, the editor of a saved view, the session log, "Browse for Container" and
the object picker: nineteen views in all, and every window of `Views/` that has a status to say. The Logon Hours dialog
and the rendered preview of a message have none – they say what they say in notes – and are on the second list of the
source test, `ConvertedViewsWithoutAStatusLine` (*V2b-2 as built*, section 8).

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
  As built (V2b-2): a dialog that starts an operation of its own – or reads something that can fail, like the two
  pickers – has the status line at its top, above its fields, and its primary button is an action button when that
  operation can be stopped; a dialog with tabs has the line above them and shows there what the tab in front says; a
  dialog that only collects input has no line (*V2b-2 as built*, section 8, *Dialogs*).
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
  - `StatusLine` – shows a `PageStatus` in one line of the fixed height `Ad.Size.StatusLine`, always present (as
    built in V2a; since V2b-1 a result that does not fit wraps to three lines at most – see "V2b-1 as built"): the
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
- **What V2b has to do** (rewritten at the end of V2b-2; counted in the XAML, comments excluded). At the end of V2a
  there were 43 uses of `Ad.ErrorBar` and 58 of `Ad.WarningBar` in 51 files, and 65 progress bars. V2b-1 – seven core
  windows, *V2b-1 as built* below – took 16 + 13 of the banners and 23 of the progress bars away; V2b-2 – the twelve
  remaining windows of `Views/` and the rendered preview of a message, *V2b-2 as built* – took 10 + 9 and 10. **Left: 17
  uses of `Ad.ErrorBar` and 36 of `Ad.WarningBar` in 33 files, and 32 progress bars in 29 files** – 40 view files with
  at least one of the three, 19 views with a status line and 2 converted views without one. `Views/` itself is done:
  what is left is in the feature folders, most of it on document tabs.
  - By area (error bars + warning bars, progress bars): `GroupPolicy/` 7 + 17 and 8 in 15 files (with the shared
    `GroupPolicyBanners.xaml`); `BulkCreate/` 3 + 5 and 2 in 4 files; `Views/Watchdog/` 2 + 4 and 7 in 9 files;
    `Computers/` 2 + 3 and 3 in 2 files; `Pki/` 2 + 2 and 4 in 4 files; `EventLogs/` 0 + 3 and 3 in 3 files;
    `Correspondence/` 1 + 1 and 3 in 1 file; `Readiness/` 0 + 1 and 1; and the row of an activity in the main window
    (no banner, 1 progress bar), which stays. The HTML preview control (`Controls/HtmlPreviewView.xaml`, 0 + 1 until
    V2b-2) is done: it was converted with the message window that hosts it, and the correspondence window, which hosts
    it too, shows its one note next to its own bars until its increment.
  - Sort first (foundation 1 of *V2b-1 as built*): status goes to the line, a note stays where it is with the look of a
    note (`Ad.Note.Info`, `Ad.Note.Attention`, `Ad.Note.Error`), the progress of a row stays in its row, and a place
    that only shows "busy" gets a progress ring – unless its read can fail: then it has status to say (*V2b-2 as built*,
    Dialogs 19). Of the 48 banners sorted so far, 26 were status and 22 notes: of the 26 error bars 22 were status and 4
    notes – findings of a check that runs while the operator types (a template, "why Save is not possible", a filter
    that is not valid) –, of the 22 warning bars 4 were status and 18 notes. So most of the 36 warning bars that are
    left will stay in their places as notes, and most of the 17 error bars will go to the line. What never was a banner
    is sorted the same way: a count or "as of" text is the summary (`StatusText` is still mentioned 45 times in the XAML
    of 24 views), a result card, a toast or a dialog that says what the window just did is status.
  - Per page, the recipe is the foundations of *V2b-1 as built*: a `PageStatusHolder` per view model – and per tab or
    part that says something of its own, with the one shown in front –, the summary in `SetIdle`, every long operation
    in a scope with its token, a state that lasts in `SetLasting` (first of all `SessionEndedStatus`), errors built with
    `PageStatusText.Failure`, an execution through `ExecutionStatus`; in the view one `c:StatusLine` named `PageStatus`,
    the file on `PagesWithAStatusLine`, and a start button with a separate stop or cancel button as one
    `c:ActionButton`. For a dialog, the sub-list *Dialogs* of *V2b-2 as built* says the rest: where the line sits and
    how it is declared (tab order, default and cancel button), what a dialog that closes on success says, that a missing
    field is said when the button is pressed, how a dialog with tabs, a dialog without a session and a hosted control
    are handled, and that a view with nothing but notes gets no line and goes on the second list
    (`ConvertedViewsWithoutAStatusLine`). Candidates for the action button that are left: bulk check, computer review,
    remote computer, the event log reader and its two analyses, GPO search, cleanup and diagnostics, the PKI security
    review, readiness, the sending of correspondence, the Watchdog delivery test. (Done in V2b-2: connect and the search
    for domain controllers, the change of the domain controller, OK of "Reset Password", preview, send and paste of the
    message window, the tests of the filter builder and of the saved view editor.)
  - Document tabs drop out of `StatusBarText` as they are converted (Watchdog, event logs, Group Policy, PKI, readiness
    – neither V2b-1 nor V2b-2 had a document tab). The wizard of bulk creation (`BulkCreate/`) is hosted in step 1 of
    the batch editor: its holder becomes `SourceStatus` of the create mode. With the last converted view `Ad.ErrorBar`
    and `Ad.WarningBar` leave `Themes/Controls.xaml`, and with them the strings and view-model members that lost their
    last user (listed under *After V2b-1*).
  - Settled in V2b-1, of what was open in the controls after V2a: the accent variant of the action button
    (`Ad.AccentActionButton`, and `Ad.DialogActionButton` / `Ad.AccentDialogActionButton` with the metrics of the dialog
    buttons); the status line of a page with several operations (a state that lasts, several holders with the page in
    front, operations one after another on one holder – foundations 2 to 4, without a hub); how far an operation over
    many objects is ("12 of 300" and the part done in the ring – foundation 6, nothing new in the control); the look of
    what stays in its place (the notes); the execution of a plan as an operation (foundation 9); "busy" where there is
    no sentence to say (a `ProgressRing` in the place, as in the main window); and long results – by the owner's
    decision of 2026-11-11 a result that does not fit the line wraps, to three lines at most, while the summary and a
    running operation stay one line (built next to unit 3: *A result that does not fit the line wraps* in *V2b-1 as
    built*).
  - Settled in V2b-2 (*V2b-2 as built*, the sub-list *Dialogs*), without a change to the model or the controls: how a
    small dialog carries the line – its place above the fields, the footer declared first, `IsDefault` and `IsCancel`
    where they were, the primary button as an action button when its operation can be stopped (Dialogs 1 to 10); the
    dialog with tabs (12); the view that has no status and gets no line, with a source rule of its own (11, 16), and the
    control a converted window hosts (15); a sentence that answers a press and is still a note (13); "busy" in a dialog
    (14) and when a progress bar that looks like it is status after all (19); view models that are no window view models
    and own a holder all the same (the Connect window, the two editors – 17); a result that goes with its input (18); a
    state that follows a selection (20); and an operation over many items that is no change plan – the send of messages
    – with the progress of foundation 6. The sorting itself learned one thing: an error bar may be a note.
  - Still open – in the controls and the model:
    - *A delay before "Working"* (about 150 ms), so that an operation that ends at once does not flash – more visible
      with every window: the read of nested membership after every comparison, the reads of the tabs of the property
      sheet, "Reading the containers of …" at every opening of the container picker, a test of a filter in a small
      domain.
    - *The rest of the V2 scope:* the status pictures at 16 px for pills, `StatusPill`, `EmptyState`, `Skeleton`,
      taskbar progress and the close question tied to running activities are not started. A send of messages that takes
      minutes now shows its progress only in the 16 px ring and the numbers of the line – taskbar progress would be its
      place.
    - *A mark in the tab header* of a page that reads or failed (6.7): a failed tab says so only when it is selected.
    - *The same message on two pages:* switching between two tabs that say a message of the same key changes the text
      without the entrance motion and without a live region event (the control compares kind and key).
    - *Reports are not coalesced:* an operation that reports per object (planning thousands of objects) updates the line
      as often.
    - *Notes:* whether a note may choose its glyph (the shield of the protected account became the warning glyph).
    - *Stop for a write:* Apply of the property sheet and OK of "New Object" cannot be stopped from the line (the
      execution is an activity). Where a write can be stopped – "Reset Password" through `ExecutionStatus` – the
      executor leaves the account it was working on half done (password reset, "must change" not set) and reports it as
      failed without a message.
    - *Reads that take no token:* the containers of the tree (the main window and the container picker – the picker's
      read therefore has no Stop), the two short reads before a send, the checks of "New Object".
    - *Said twice, or said elsewhere:* the toast of the main window after an execution, a restore or a send repeats what
      the line of the window that ran it says; an export and a copy to the clipboard still answer with a toast in every
      converted window, not in its line.
    - *Buttons whose operation is stopped in the line only:* the test message, the three tests of the settings, *Find
      Now* and *Check Names* of the object picker (and its OK, the primary button of a dialog) stayed plain buttons –
      they never had a Cancel, and several action buttons over one holder each need a flag. Whether they become action
      buttons is to be decided with the owner (6.7: "cancel where you started").
    - *The empty line of a dialog:* a small dialog shows an empty band of 28 px at rest, and always on a tab that has
      nothing to say. Collapse an empty line in dialogs, or keep the one fixed place?
    - *Long results in narrow dialogs:* a failed connect with its advice does not fit three lines at 560 px – a shorter
      advice for the line, or a fourth line.
    - *A window without a session:* the lasting "session ended" takes the place of the summary and covers what the page
      in front says, so counts that are still true (the session log, the report of a send) are not shown.
    - *A failed read in the tree* is said by the line of the container picker only for the selected container; a
      container opened with its expander alone, and every container of the main window's tree, says it with a glyph in
      its row.
    - *Tab order:* in the dialog-like windows and the dialogs the status line is declared after the footer (as in the
      Find pilot), so its link and close button follow the footer buttons – to be put right with the page skeleton of V3
      (section 7).
    - *Language:* the property sheet computes its lasting state from its conditions and says it again after a language
      switch; in the other converted windows a failure that lasts keeps the language it was said in, and only "the
      session of this window ended" and "not connected" follow the switch at once. The Connect window and the two
      editors do not follow a switch at all (they are modal, and the settings cannot be opened over them).
    - *Tests:* summaries show the time of day, so README pictures of converted windows differ with every screenshot run
      – a fixed clock for the UI tests would end that.
- **Not seen, to confirm on Windows:** nothing of this was seen on a screen. Open: the look of the line on the status
  backgrounds in light and dark and in High Contrast; whether the info picture next to the summary is too loud in a
  dense window; the ring at 14 px in a button and at 16 px in the line (stroke 1.75 and 2 px); the cross-fade of the
  button contents and the entrance of a message; the width of the Polish "Zatrzymywanie…", which makes *Znajdź teraz*
  wider than the buttons next to it were; that a ring keeps its clock while the window is minimized (a minimized
  window still counts as visible).

**V2b-1 as built (the core windows).** The second increment of V2 is the sweep over the views that still had banners;
V2b-1 covers seven core windows in three units. Unit 1 built what the sweep needs beyond V2a and converted the property
sheet; unit 2 added the execution of a change plan as an operation (foundation 9) and converted the batch editor, the
change plan preview and the main window; unit 3 converted the AD Recycle Bin, the account review and the comparison of
objects. The block reads in this order: the foundations every later unit follows, the seven windows, the tests, what
is left, and what nobody has seen on a screen yet. What the sweep still has to do is counted in *What V2b has to do*
above.

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
     the second is refused by its command while the first runs, or - when the second only *follows* the first - it
     waits for it and takes the line then (the reads of nested membership in the comparison window; unit 3). What
     decides between two holders and one: do the two things belong to two places the operator can look at (a list and
     what is done with its selection, two tabs)? Then each place says its own. An operation that *contains* a step
     that is an operation elsewhere does not begin a second scope for it: the read after Apply belongs to "Saving…"
     (`PropertySheetViewModel._saving`). A "latest wins" token that other code cancels (a `SerialCancellation`) is
     linked with the token of the scope, so both the newer request and the operator's Stop end the work.
  5. *The shape of an operation.* `using var scope = Status.Begin(text, new PageStatusLog(key));` and the token from
     the scope. After every `await`: `scope.IsEnded` means replaced or disposed – say nothing, touch nothing. A
     cancellation with `scope.IsCancelling` is the operator's Stop (with the scope's token alone that is every
     cancellation of a scope that has not ended): complete as attention – what was stopped, what the page may lack,
     and the command that starts it again as the action. A cancellation that came through a linked token (the read of
     the window ended, a newer request) ends without a message. A failure that leaves the content empty or stale is
     the state of the page: `SetLasting(error with Retry)` inside the operation, which then ends without a message. A
     failure that leaves the content as it was is a result: `CompleteError`. A result that is the content itself (a
     list that was read) is no message: set the summary and let the scope end. A flag like `IsLoading` is ended only
     by the operation that owns the page (keep the scope in a field and compare), never by a replaced one. A window
     that is closed disposes what still runs (`Status.Operation?.Dispose()`), its pages too.
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
  9. *The execution of a change plan* (added by unit 2) – `Changes/ExecutionStatus`. A window that executes a plan
     through the coordinator says it with this one helper, so every such window says the same: `using var execution =
     ExecutionStatus.Begin(Status, Localizer, title, ConfirmationDomain, plan.Objects.Count);` **before** the step
     changes (the line goes from the summary straight to "working"), every activity the coordinator hands to its
     `started` callback goes to `execution.Follow(activity)`, and the end is `execution.Complete(result, next, action)`
     or `execution.Fail(reason, code)`. Working: "Executing “Disable accounts” in contoso.test: 12 of 300…" – with
     ", failed: 2" once something failed – as one message whose ring shows the part done (the total is every object of
     the plan: the executor counts also the ones it skips, and the list of background operations shows the same
     numbers). Stop works both ways: the Stop of the line and the Cancel of the action button cancel the activity, and
     an activity that is cancelled in the list of background operations makes the line say "Stopping…". An operation
     that merely ends – the window was closed, something else took the line – never cancels the activity: the
     execution goes on in the background, as before. Several activities one after another (the wizard's first object,
     then the rest) add up. Results name the domain (4.5): **done** "“…” was executed in contoso.test: 12 objects
     changed. The report is in the list of background operations."; **attention** when it was executed only in part
     or stopped (what was changed, how many failed, that the rest was not executed, then the window's own advice and
     action – `next`, `action`); **error** when nothing was changed or the plan could not be executed at all (reason
     and result code). It only *says* what happens: planning, validation, execution, audit and rollback are the
     pipeline's, untouched (rule 5 of `CLAUDE.md`).
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
  - *A result that does not fit the line wraps* (owner's decision of 2026-11-11, after the sheet showed the
    problem: in a 640 px wide window an error with its reason and advice was cut with an ellipsis, and failures that
    used to be a dialog with the whole text became one cut line). "Done", "attention" and "error" wrap to three lines
    at most; a text longer than that is cut in its third line and is whole in the tool tip and in the name a screen
    reader hears. The line grows by 20 px per extra line and what lies under it moves down by as much – once, when
    the result arrives, and back when it is closed or the next operation starts. The summary and a running operation
    stay one line whatever their length: they change while the operator works, and a page must not jump then. In
    `Ad.StatusLine`: `MinHeight` instead of `Height`; title and message with a 20 px block line height and 4 px above
    and below (one line is exactly `Ad.Size.StatusLine`, and both share a base line); the message has
    `MaxHeight` = three lines and wraps through the triggers of the three result kinds; the picture and the two
    buttons stay in the first line (each button in a box of the line's height at the top). Tests:
    `StatusLineUiTests.TheSummaryAndARunningOperation_AreOneLineOfAFixedHeight_AndWhatLiesUnderThemNeverMoves` and
    `AResultThatDoesNotFitTheLine_WrapsToThreeLinesAtMost_AndTheLineGrowsWithIt`; the window tests assert after a
    result that the content moved by as much as the line grew (`GrowthOf`), and after "working" that it did not move.
- **The batch editor and the CSV import** (`Views/BatchEditorWindow.xaml`; `BatchEditing/BatchEditorViewModel`,
  `CsvImportViewModel`).
  - *What the line says.* It sits under the step strip, 8 px in from the window edge, and follows the step shown.
    **Step 1, selected objects:** at rest "3 objects · User: 2, Group: 1"; "Reading the selected objects (3)…" with
    Stop; a read that was stopped (attention: names, types and attribute suggestions may be missing) or failed (error
    with the reason and the code) as the state of the list, with Retry, for as long as it lasts. **Step 1, CSV:** at
    rest the loaded file, "users.csv · 120 rows, delimiter: ;" (nothing before a file was chosen); "Reading
    “users.csv”…" with Stop; a file that could not be read or has no header row as an error ("Choose another file.").
    **Checking** (Preview, Retry failed, the wizard's Undo): "Checking the changes…", then "Checking the changes: 120
    of 300…" with the part done and Stop; stopped – attention "Checking the changes was stopped: nothing was changed.
    Press Retry to try again."; failed – error with the reason, the code and Retry (the command that started that
    check). **Step 2:** the summary of the preview ("300 objects: 280 will change, …"), which follows the include check
    boxes. **Step 3:** the execution (foundation 9). **Step 4:** how it ended – done; attention with "The reasons are
    in the list of results. Press “Retry failed” to plan the objects that were not changed again." and *Retry failed*
    as the action; error when nothing was changed – and behind that result, as the summary, the counts ("Succeeded:
    118, failed: 2, skipped: 5"). In every step: that the session of the window ended (lasting error).
  - *How the old places were sorted* (2 error bars, 4 warning bars, 5 progress bars, and the texts that said the
    same).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar under the steps (`Error`: a failed check or execution, the session that ended) | status | result of the operation; lasting error of the window |
    | progress bar and "Reading current values and validating... 120 / 300" in the footer; the *Stop* button of Preview | status | "Checking the changes: 120 of 300…" in the line; *Preview* is an action button |
    | progress bar over the list of selected objects; the line "User: 2, Group: 1" | status, summary | operation and summary of the list (the source in front in step 1) |
    | CSV: progress bar next to *Browse*; "120 rows, delimiter: ;" under the file name; error bar (`Error`) | status, summary | operation, summary and error of the import (the source in front in step 1) |
    | executing step: the indeterminate bar, the bar of the activity and its "12 / 300 · 1 failed" | status | the execution in the line (the card keeps the title, the object done last and the hint) |
    | the *Stop* button of the executing step | – | *Execute* is an action button and stays for that step |
    | preview: "120 objects: 100 will change…"; results: "Succeeded: 118…" next to the filter | summary | `SetIdle` of the step shown; gone from the page |
    | warning bar `OperationsError` ("Delete cannot be combined with other operations.") | note | `Ad.Note.Attention` above the operations: the rule the list breaks – why Preview is not possible yet |
    | warning bar of the delete operation ("Deleted objects cannot be restored with Undo…") | note | `Ad.Note.Attention` in the card of that operation |
    | two warning bars above the preview (the undo plan; what the wizard's undo deletes) | note | `Ad.Note.Attention` above the rows: what the plan shown is |
    | message of an operation card, `MappingError` of the CSV mapping, the field messages of the editors | note | unchanged (glyph and text, as before) |

  - *Decisions.*
    - **Two holders: the window and the source of its first step.** This closes the question left open by unit 1 –
      reading the selected objects and checking the changes *can* overlap (Preview is possible before the names
      arrived), and a second `Begin` on one holder would have taken the read off the line for good. The list of
      objects (`_targetsStatus`) and the CSV import (`CsvImportViewModel.Status`) each own a holder,
      `BatchEditorViewModel.SourceStatus` is the one of the mode, and it is `Status.Front` exactly while step 1 is
      shown (`OnStepChanged`). The check is the window's own operation and takes the line while it runs; back in step
      1 the source says again what is true of it. Nothing new in the model.
    - **Which button cancels what.** Both action buttons bind `CancelCommand` / `IsCancelling` to the window's holder
      – they start the window's operations. *Preview* is running while `Status.IsWorking` (not `IsBusy`: after Back or
      the end of the session a cancelled check may take a moment to return, and there is nothing left to cancel then);
      *Execute* is running while `IsExecutingStep` and is shown in steps 2 **and** 3 (`IsExecuteStep`), so the button
      that was pressed stays where it was and is the Cancel of the execution. Reading the objects or the file is
      stopped in the line (the Stop of the source's operation), not on a button: no button started it.
    - **A read of the selected objects that was stopped or failed is a state of the list**, not a result: the rows
      keep their DNs without names, and that stays true until the list is read again (Retry –
      `ReloadTargetsCommand`, which never throws). A check that failed or was stopped is a result that can be closed:
      step 1 is as it was.
    - **Tokens** (rule 6). The read of the selected objects ran with `CancellationToken.None`, the read of a CSV file
      could not be stopped, and the checks of *Retry failed* and of the wizard's *Undo* were started with
      `CancellationToken.None`: all of them are operations now and can be stopped. The cancel of the Preview command
      (`PreviewCancelCommand`) is the same Stop – the line says "Stopping…". Back, closing the window and the end of
      its session still cancel a check without a message. How a plan is made, checked and executed did not change:
      `BatchEditorTests` passes as it was.
    - **Back and Undo take the result off the line.** What the line said was about the step that is left; the step
      shown says its own. An execution that is still running is not affected by closing the window (it stays an
      activity), and when the session ends during an execution the execution still says how it ended – the lasting
      "session ended" is back when that result is closed.
    - **The toast after an execution stays**: it is shown in the main window, for which the execution finished
      "elsewhere" (6.7).
    - `Error`, `TargetSummary`, `IsLoadingTargets`, `PlanningCompleted` / `PlanningTotal`, `PreviewSummary`,
      `ResultSummary`, `PreviewCancelCommand`, `CancelExecutionCommand` and the import's `Error` / `IsLoading` are
      unchanged for the tests and view models that read them. The window is 36 px higher by default (760 to 796).
  - *Where this differs from the plan.* **The wizard of the create mode is not converted**: its pages
    (`BulkCreate/*.xaml`, a later unit of the sweep) are hosted in step 1 of this window and keep their banners and
    bars, so in create mode the window still shows them there, and the line is empty at rest in that step (the check
    started by Preview, the execution and the results are said in the line as in the other modes). When the wizard
    gets a holder, it becomes `SourceStatus` of the create mode – the place is prepared. The strings `Batch.Planning`
    and `PlanPreview.Cancel` have no user any more and were left in the resources.
- **The change plan preview** (`Views/ChangePlanPreviewWindow.xaml`; `Changes/ChangePlanPreviewViewModel`) – the
  window every Group Policy operation is previewed and executed in.
  - *What the line says.* Under the title: at rest the summary of the plan shown ("3 objects, 9 steps on
    dc01.contoso.test."); the execution (foundation 9) with Stop; its result – done; attention or error with "The
    reasons are in the list of results." and, where something was made, "Press Undo to revert what was made." with
    *Undo* as the action; the end of the session as the lasting error.
  - *How the old places were sorted* (1 error bar, 1 progress bar, and two places that were neither).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` (an execution that could not start, the session that ended) | status | result of the execution; lasting error of the window |
    | indeterminate progress bar in the footer | status | the execution in the line, with the part done |
    | summary text in the footer | summary | `SetIdle`; gone from the footer |
    | result card `ResultText` after an execution ("Done: every change was made." / "Not every change was made…") | status | result of the execution, with the domain and the counts |
    | the same card after Undo ("The undo plan is shown. Execute it to revert the change.") | note | `Ad.Note.Attention` above the tabs while that plan waits (`IsRollbackNoteShown`) |
    | the *Cancel* button of the executing step | – | *Execute* is an action button (the default button of the window) and stays for that step |
    | the notes of the request (cards above the rows), the warnings of the rows | note | unchanged |

  - *Decisions.* The result card was a card, not a banner – but it said what the window just did, and the line says
    that now: one place. After Undo the same property carried a sentence about the plan shown; that is a note, and it
    belongs to the plan only while it waits to be executed (its result is said by the line). `ResultText`, `Error`,
    `Summary` and `CancelExecutionCommand` are unchanged for their tests; the window still cannot be closed while it
    executes. It is 36 px higher by default (700 to 736).
- **The main window** (`Views/MainWindow.xaml`). The shell is not a page: it has **no status line of its own** – a
  document says its status in its own line (the object list since V2a; the other documents with their units). Its four
  places were sorted one by one:

  | Place | Kind | Now | Why |
  |---|---|---|---|
  | bar of one activity in the list of background operations (`ActivityTemplate`, `Fraction`) | progress of a row | unchanged | how far *one* item of a list is stays in its row |
  | 2 px bar under the quick search (`IsSearching`) | "busy" mark | `ProgressRing` in the place of the magnifier while the search runs | the result of the search is the list of suggestions and a failure is a toast – there is no sentence to say, only "busy"; the box got `AutomationProperties.ItemStatus` "Searching…" while it runs, because the ring is outside UI Automation and the bar had that name |
  | 60 px bar next to the activity button of the status bar (`Activities.HasRunning`) | "busy" mark | `ProgressRing` (14 px) | what runs and how far is the text next to it ("Running operations: 2") and the list behind the button |
  | warning bar "Protected account (adminCount=1)" in the details pane | note | `Ad.Note.Attention` | not a warning of the shell: a fact about the selected object, true for as long as the pane shows it |

  The warning glyph of the note replaced the shield of the bar (a note does not choose its glyph; the words say what
  it is about). The rule of V2a for the status bar holds unchanged – `ShellViewModel.StatusBarText` /
  `StatusBarSelectionText` are empty for a tab that is `IHasPageStatus` (`ObjectListStatusTests`,
  `StatusLinePilotTests`) – and nothing of this unit added a document to it: the batch editor and the plan preview are
  windows, not tabs.
- **The AD Recycle Bin** (`Views/RecycleBinWindow.xaml`; `Dialogs/RecycleBinViewModel`).
  - *What the line says.* It sits at the top of the window, 8 px in from its edge, above the note about the forest, the
    filters and the list. **While the list is shown** it is the line of the list: at rest "12 objects · 0.21 s · as of
    14:35:02", with " · 3 selected" once rows are selected; "Reading the deleted objects of contoso.test…", then "…:
    1,500 objects so far…" (one report per batch), with Stop; a read the operator stopped – attention "Reading the
    deleted objects was stopped before all results arrived: 150 objects loaded. Press Refresh to read the whole list."
    with Refresh; a read that failed – error with the reason, "Press Retry to try again.", the code and Retry, for as
    long as the list is empty because of it. **What the window does with the selection:** "Checking the objects to
    restore (3)…" with Stop; stopped – attention "Checking the objects to restore was stopped: nothing was restored.
    Press Retry to try again."; failed – error with the reason and the code; both with Retry, which checks the same
    restore, to the same place, again. **While the restore preview is shown:** its summary ("3 of 4 objects will be
    restored."). **The restore** is the execution of a plan (foundation 9), with Stop, and its result names the domain:
    done; attention with the reasons of the failures (each said once, three at most) and "What was not restored is still
    in the list."; error when nothing was restored. As the state that lasts: the session of the window ended, or the
    window was opened without one.
  - *How the old places were sorted* (1 error bar, 2 progress bars, and the texts and the dialog that said the same).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` over the list (could not be read, no right to list the deleted objects, not connected) | status | lasting error of the list with Retry; "not connected" and "the session ended" are the lasting error of the window |
    | 3 px progress bar over the list (`IsLoading`); the *Stop* icon button next to *Refresh* | status | the read in the line, with the objects so far; *Refresh* is an action button and the Cancel of the read |
    | progress bar in the footer (`IsBusy`: checking or restoring) | status | the check and the restore in the line; the button that restores is an action button |
    | footer texts: "12 objects · …", "3 selected", the summary of the preview | summary | `SetIdle` of the list and, in the preview, of the window; gone from the footer |
    | error dialog after a check or a restore that failed | status | error (or attention) in the line; a restore whose window was closed meanwhile still reports with a toast or the dialog |
    | banner "The AD Recycle Bin is enabled…" / "…is not enabled in this forest…" (a card of its own style, caution background without the Recycle Bin; "not connected" in the same place) | note | `Ad.Note.Info` with the Recycle Bin, `Ad.Note.Attention` without it: what can be restored here – a fact about the forest, shown while the window is connected |
    | "1 object cannot be restored – see the findings" over the preview, the findings of its rows | note | unchanged |

  - *Decisions.*
    - **Two holders: the list and the window.** The list is read while a restore is being checked or executed (objects
      deleted elsewhere arrive, the restore reads the list again), so the read (`ListStatus`) and what is done with the
      selection (`Status`) cannot share an operation. The list is in front while it is shown (`OnIsPreviewingChanged`);
      in the preview the window says its own summary.
    - **A failed read is the state of the list, a stopped one a result.** The empty list has no other explanation than
      the error, which lasts until the list is read again; what a stopped read left is there to see, and its message can
      be closed. Only a read the operator stopped says so: a newer read ("the latest wins": another filter, F5, objects
      deleted elsewhere) and the end of the session cancel without a word.
    - **Refresh is the Cancel of the read, whoever started it** – the window when it opens, a filter, the reload after a
      restore: `IsRunning` is `ListStatus.IsWorking`. Refresh was an icon button; as an action button it has its glyph
      *and* its text, because "Cancel" and "Stopping…" need the room anyway.
    - **The read after a restore belongs to the restore.** The line keeps saying "Executing…" while the list is read
      again and says how the restore ended when the list is there – "what was not restored is still in the list" is true
      when it is said.
    - **A result leaves with the operator's next step.** Refresh, another filter and Back close what the window said
      about its last check or restore (6.7: a result stays until the next operation or until it is closed); a reload the
      window starts by itself leaves it.
    - **A failed restore is said in the line, not in a dialog.** The dialog listed every failed object with its reason;
      the line says the numbers, the distinct reasons and where the objects are – which object failed why is in the
      report of the operation, in the list of background operations. A restore without failures is still a toast as
      well: of the main window, for which it finished elsewhere (6.7).
    - `Error`, `StatusText`, `SelectionText`, `PreviewSummary`, `BannerText`, `IsLoading`, `IsBusy`, `IsPlanning` and
      `StopCommand` are unchanged for the tests and the code that read them. The window is 36 px higher by default (660
      to 696).
- **The account review** (`Views/AccountReviewWindow.xaml`; `AccountReview/AccountReviewViewModel`,
  `PrivilegedAccessViewModel`).
  - *What the line says.* It sits at the top of the window, above the scope. **The check** – *Check* runs the one of the
    tab shown – is the operation of the window and is said on both tabs: "Checking the accounts in the scope
    “contoso.test”…", then "…: read: 1,200, with findings: 56…"; for the *Privileges* tab "Checking the privileges of
    the accounts in the scope “…”…", "…: read: 300, with rights beyond a domain user: 17…" and, as its last step, "…:
    comparing with similar accounts…"; with Stop. The scope is named as the operator chose it: the domain, the path of
    the organizational unit or of the group, the name of the saved view. **How a check ended is said by the tab it was
    for**, while that tab is shown: at rest its summary ("As of 12:00 · 1,234 accounts read · 56 accounts with
    findings", with " · 3 selected"); stopped – attention "Checking the accounts was stopped: the results are incomplete
    (read: 400). Press “Check” to check again." with *Check*; failed – error with the reason, the code and Retry, for as
    long as the tab shows what that check left. **The exact last logon** (*Account state* tab): "Reading the exact last
    logon of the accounts (12) on all domain controllers…" with Stop; done "The exact last logon was read on 3 of 3
    domain controllers. Accounts that are active after all: 2."; attention when a controller did not answer ("… No
    answer from: dc02.contoso.test."); stopped – attention "…: the list was not changed." with the button's own text as
    the action; failed – error with Retry. As the state that lasts: the session of the window ended.
  - *How the old places were sorted* (2 error bars, 2 warning bars, 3 progress bars, and the texts, the toast and the
    dialog that said the same).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` under the scope (a check that failed) and the same text in an error bar on the *Privileges* tab | status | lasting error of the tab the check was for, with Retry |
    | warning bar `SessionEndedMessage` | status | lasting error of the window |
    | progress bar in the footer (`IsBusy`); the 3 px bars over the two lists (`IsRunning`) | status | the check in the line, with the accounts read so far |
    | the *Stop* button next to *Check* | – | gone: *Check* is an action button, and so is *Exact last logon (all DCs)* |
    | footer texts: "As of …" / "Checking (as of …)…" of the tab shown, the selection | summary | `SetIdle` of each tab; gone from the footer |
    | toast (done, or a warning with the controllers that did not answer) and error dialog after *Exact last logon* | status | done / attention / error of the *Account state* tab |
    | warning bar `NoticeText` on the *Privileges* tab (what a grant source could not read: SYSVOL, GPO files, unknown names) | note | `Ad.Note.Attention` above the list: how complete the results below are, true for as long as they are shown |
    | the hints "not checked yet" and "no accounts", the validation of the parameters | note | unchanged |

  - *Decisions.*
    - **Three holders: the window and its two tabs.** A check runs one at a time and belongs to the window – *Check* is
      one button for both tabs, and the operator may look at the other tab while it runs – so it is the window's
      operation and is said whichever tab is shown. What a check left belongs to its tab: `AccountsStatus` and
      `Privileges.Status` hold the summary and the outcome, and the tab shown is in front (`OnSelectedTabIndexChanged`).
      A tab whose check failed says so when it is selected, not on the other tab.
    - **The *Privileges* tab reports through the window's operation.** Its view model does the work but does not own the
      operation: the window hands it the running scope (`PrivilegedAccessCheck`: the operation, the name of the scope,
      the command that checks again), the tab reports its counts through it and says itself how the check ended. One
      operation for one piece of work (foundation 4). A scope that cannot be resolved leaves that tab as it was – an
      error that can be closed – and is said with its own reason, without a code.
    - **The exact last logon is an operation of its tab** (`AccountsStatus`), with the button that started it as its
      Cancel; a check started meanwhile replaces it without a word. Its result is said in the line and no longer in a
      toast or a dialog – it is what this window just did (6.7). The domain controllers that did not answer are named on
      screen (their number is in the log copy); the reason of each, which the toast listed, is not said any more.
    - **Only a check the operator stopped says so**; a newer check and the end of the session cancel without a word.
    - `Error`, `StatusText`, `FooterText`, `SelectionText`, `IsBusy`, `IsRunning`, `IsReadingDomainControllers`,
      `StopCommand` and the tab's `Error` / `StatusText` are unchanged for the tests and the code that read them. The
      window is 36 px higher by default (840 to 876).
- **The comparison of objects** (`Views/CompareObjectsWindow.xaml`; `Comparison/ObjectComparisonViewModel`).
  - *What the line says.* It sits at the top of the window, above the connection box. **Working:** "Comparing “Łukasz
    Wiśniewski” with the connection fabrikam.test · FABRIKAM\anna.zielinska…" with Stop – whoever started the comparison
    (the window when it opens with the only other connection chosen, *Compare*, *Refresh*, a chosen candidate); then, as
    operations of their own that follow it, "Reading nested membership…" and – when asked for – "Reading nested
    members…", with Stop. **At rest:** the object of the other connection the tabs show a comparison with ("Found:
    CN=Łukasz Wiśniewski,OU=IT,…"); before the first comparison, with several other connections open, "Choose the other
    connection". **Stopped:** attention "Comparing “…” was stopped: the tabs may not show everything. Press “Compare” to
    compare again." with *Compare*; a stopped read of nested membership – "…: only direct membership is compared. Press
    “Refresh” to read everything again." with *Refresh*. **A nested read that failed:** error "Could not read nested
    membership: reason. Only direct membership is compared. (code)" with Retry – a result that can be closed, the tab
    keeps what it has. **States that last**, the most important first: the session of the window ended; the last
    comparison could not be read – error "Could not compare “…”: reason. Press Retry to try again. (code)" with Retry;
    the last comparison shows no pair of objects, and why – attention: "Found 2 objects – choose one." (the candidates
    are listed under the connection box), "Not found (rules tried: …).", "More than 50 – narrow the search.", "The
    object no longer exists in contoso.test." and the like. The last two go with the next comparison.
  - *How the old places were sorted* (2 error bars, 3 warning bars, 2 progress bars, and the text of the footer).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `SessionEndedMessage` at the top | status | lasting error of the window |
    | the same sentence as a warning bar in the card of side A | status | gone: said once, by the line (side A is still marked as ended) |
    | error bar `ErrorText` (a comparison that failed) | status | lasting error of the window with Retry |
    | progress bar and `StatusText` in the footer ("Reading…", "Found: …", "Not found…", "Stopped") | status, summary | the comparison in the line; how matching ended as the summary or as a lasting attention |
    | progress bar and "Reading nested membership…" on the *Membership* tab | status | an operation of the window, with Stop |
    | the error of a nested read, added to the footnotes under its tab | status | error in the line, with Retry |
    | the *Stop* button next to *Compare* | – | gone: *Compare* is an action button |
    | warning bar in the card of side B ("The connection … was closed – its results stay visible.", "… changed its session … – Refresh") | note | `Ad.Note.Attention` in the card: about what that card and its column show, for as long as they show it |
    | warning bar "No other connection is open. Open a second connection (Ctrl+Shift+N) to compare." | note | `Ad.Note.Attention` under the connection box: why the box is empty and *Compare* cannot be pressed |
    | warning bar "This is the same object seen by two accounts – differences come from read permissions." | note | `Ad.Note.Attention` above the tabs: how to read the differences below |
    | the footnotes under the membership tabs (truncated lists, members that were skipped) | note | unchanged |

  - *Decisions.*
    - **One holder, operations one after another.** The window has tabs but no page view models, and what runs after a
      comparison – nested membership, nested members – fills tabs of the same result. So there is one holder: the
      comparison, then the nested reads, each begin on it; the read of nested members waits for the read of nested
      membership (`WhenOverAsync`); a new comparison replaces whatever runs. The line says what the window is still
      reading whichever tab is shown, and the count in a tab header follows when its read is through. Ticking *Show
      nested members* while a comparison runs starts nothing: the comparison starts that read itself when it is through.
      (It used to start at once, from members that were not read yet; as a second operation it would also have taken the
      line, and with the line the token, from the comparison.)
    - **Every comparison can be stopped, and *Compare* is its Cancel.** The old *Stop* was the cancel of the *Compare*
      command only: for a comparison started by *Refresh*, by a chosen candidate or by the window itself it was shown
      disabled. The action button is running while `IsRunning` and cancels through the holder (`Status.CancelCommand`),
      so it stops the comparison whoever started it. The nested reads are not its operation: they are stopped in the
      line, `IsRunning` is false then and *Compare* can be pressed – it replaces them. The cancel of the command
      (`CompareCancelCommand`) is the same Stop.
    - **Why the tabs are empty is a state, not a result.** "Not found", "choose one", "could not compare" explain what
      the window shows until something else is compared; as results they could be closed, and the empty tabs would stand
      without a reason. They are kept as two statuses (`_failure`, `_outcome`) behind the end of the session
      (`UpdateLasting`). "Choose one" goes with its candidates: choosing another connection, or the other connection
      changing its session, clears both (the sentence used to stay in the footer).
    - **The summary is what the tabs show** (the object of side B that was read), not what was matched last: after a
      comparison that was stopped or failed, with the results of the one before still on screen, "Found: …" behind the
      message is still true.
    - **The end of the session ends "running".** A comparison that is cancelled by the end of the session is no longer
      the current run and never reset `IsRunning` (the bar in the footer stayed); `OnSessionEnded` resets it now –
      *Compare* would otherwise stay the Cancel of nothing.
    - **The other side is a note, this side is the line.** That the session of *this* window ended is its state: nothing
      can be compared any more. That the *other* connection was closed or changed its session is about one of the two
      columns – the results stay, *Refresh* or another connection goes on – and stays in the card of that side.
    - **A state is said in the language of the moment.** After a language switch the end of the session is said in the
      new language (in all three windows of this unit); why a read or a comparison failed stays as it was said until it
      is read or compared again, as the texts of the view models did before.
    - **The texts of matching are the ones the footer had**, said as sentences (a full stop is added) and logged with
      their keys. One was made anew: "choose one" counts the candidates in the plural form of the language
      (`Comparison.Status.Ambiguous` with `PageStatusText.Objects` – „2 obiekty”, „5 obiektów”; it read „2 obiektów”).
    - `StatusText`, `ErrorText`, `IsRunning`, `IsNestedLoading`, `SessionEndedMessage` and `CompareCancelCommand` are
      unchanged for the tests and the code that read them (`StatusText` is still the accessible name of the list of
      candidates). The window is 36 px higher by default (780 to 816).
- **Tests.** Presentation tests run on Linux and are the bulk; the UI tests run on Windows CI only and were written
  without a screen – one file per window, through the real window over the fake directory, asserting only states that
  last until the test changes them (a call the test holds in `FakeDirectorySession.BeforeOperation` and releases makes
  "1 of 2", "8 objects so far" and "Stopping…" such states).
  - *Foundations and the property sheet (unit 1).* Presentation: `Feedback/PageStatusSourcesTests` (the lasting state,
    the page in front, "12 of 300"), `Dialogs/PropertySheetStatusTests` (summary in both languages, read with stop,
    failed read, gone and deleted, out of date, Apply done / refused / failed, a tab that failed, the nested list with
    cancel and failure, a tab that reads when shown or with the sheet, the account check, the session that ended,
    closing the sheet), and the status of the certificate tabs and of the computer's page in their test classes. UI:
    `NoteUiTests`, the accent and dialog variants in `ActionButtonUiTests`, the line of a window with a page in front in
    `StatusLineUiTests`, `PropertySheetStatusUiTests` (six tests through the real window), the note in
    `CertificateTabsUiTests`.
  - *The batch editor, the change plan preview, the main window (unit 2).* Presentation: `Changes/ExecutionStatusTests`
    (working from the first moment, the progress of one and of several activities, Stop in both directions and before
    the activity is known, an operation that merely ends, a plan that could not be executed, a window without a domain);
    `Dialogs/BatchEditorStatusTests` (the summary of the objects in both languages, the read with Stop / failure /
    Retry, checking with the part done in both languages, the overlap of read and check, Stop and the command's cancel,
    a failed check, executing with the part done and the result with the domain in both languages, Stop of an execution
    and cancel from the list of background operations, executed in part and not at all, Undo, Back, the session that
    ended – also during a check –, closing the window during an execution, the CSV file, the log copies without a title,
    name or server text); `GroupPolicy/ChangePlanPreviewStatusTests` (summary in both languages, execute and undo, a
    failed execution with Undo as the action, Stop, a refused confirmation, the session that ended). `BatchEditorTests`
    and `ChangePlanPreviewTests` are unchanged. UI (Windows CI): `BatchEditorStatusUiTests` (six tests),
    `ChangePlanPreviewStatusUiTests` (four), `MainWindowFeedbackUiTests` (three); `BatchEditorWindowTests` reads the two
    summaries from the line instead of the text blocks that are gone.
  - *The Recycle Bin, the account review, the comparison (unit 3).* Presentation: `Dialogs/RecycleBinStatusTests` (16:
    the summary with the selection in both languages, the read with the objects so far and Stop, a newer read that
    replaces the running one, a failed read and "no right to list", a window without a session and the end of the
    session, checking a restore with Stop and failure, the list read while a restore is checked, the restore with the
    domain, in part and stopped, closing the window, the log copies); `AccountReview/AccountReviewStatusTests` (13: the
    summary of the tab shown in both languages, the check with its counts and the name of a saved scope, Stop, a failed
    check as the state of its tab, a scope that cannot be resolved, the check of the *Privileges* tab, "said on both
    tabs, ended on its own", the exact last logon done / not all controllers / stopped, the end of the session on both
    tabs, the log copies); `Comparison/ObjectComparisonStatusTests` (16: comparing, then nested membership, then the
    summary, in both languages; what to choose; Stop and the cancel of the command; the stopped and the failed
    comparison in Polish; a failed comparison with Retry; no match and several candidates as the state of the window,
    and another connection that takes "choose one" off the line; the nested reads as operations of their own, stopped,
    failed, one after the other, and not started inside a running comparison; the end of the session, also during a
    comparison; the other connection closing as a note; the log copies). Adapted where the place of a message changed:
    `RecycleBinViewModelTests` (a failed restore is read from the line, no dialog) and `AccountReviewViewModelTests`
    (the result of the exact last logon is read from the line, no toast). UI: `RecycleBinStatusUiTests` (three tests),
    `AccountReviewStatusUiTests` (three), `CompareObjectsStatusUiTests` (four); `RecycleBinWindowTests`,
    `AccountReviewWindowTests` and `CompareObjectsWindowTests` read the summaries, "not connected" and the notes from
    their new places.
  - *Source rules.* The eight views with a status line – the two pilots and six windows of V2b-1; the main window has
    none – are on `PagesWithAStatusLine`. The rules of `XamlSourceTests` that cover these files (`Ad.*` keys defined,
    the closed set of Fluent brush keys – unchanged at 28 –, no storyboard, duration or easing outside
    `Themes/Motion.xaml`, the status line rule) and "every `StaticResource` key resolves, before its first use" were
    proven on Linux with throwaway scripts that repeat the expressions of the tests.
- **After V2b-1** (counted in the XAML, comments excluded): 27 uses of `Ad.ErrorBar` and 45 of `Ad.WarningBar` in 44
  files and 42 progress bars in 36 files – 53 view files with at least one of the three; one progress bar is the row of
  an activity, which stays. By area, and what else the sweep has to do: *What V2b has to do*, above. Left behind on
  purpose, to be looked through when the sweep is over: the strings `Batch.Planning`, `PlanPreview.Cancel`,
  `Comparison.Stop`, `Comparison.StatusAmbiguous` and `AccountReview.ExactDone`, which have no user any more, and
  view-model members that are no longer shown but are read by tests or other code (`StatusText`, `Error` and the "busy"
  flags of the converted windows, `StopCommand` of the Recycle Bin and of the account review). Added by V2b-2, unit 1:
  `FindDomainControllersCancelCommand` of the Connect window, which has no user any more; no longer shown but read by
  tests or by the view model itself – `ErrorText` and `StatusText` of the Connect window and of "Change domain
  controller", `ListNote` of the latter, `Error` of "Reset Password" and of "New Object". No string lost its last user
  in that unit. Added by V2b-2, unit 2: the string `UserMessages.Rich.CancelPaste` and `CancelPasteCommand` of the
  message window, which have no user any more; `CancelSendCommand`, which only tests use; no longer shown but read by
  tests or by the view model itself – `Error`, `StatusText`, `TestResultText`, `RichStatusText`, `ProgressText`,
  `ProgressValue` and `ProgressMaximum` of the message window, `TestResult`, `TestSucceeded` and `OutlookAccountsText`
  of the "E-mail messages" tab of the settings. Added by V2b-2, unit 3: no longer shown but read by tests or set by the
  view model itself – `TestResult` of the filter builder and of the saved view editor (with the strings `Views.Testing`
  and `Views.TestResult`, which only feed it), `StatusText` of the object picker (with `Picker.Found` and
  `Picker.TooMany`), `IsLoading` of the container picker, and `SessionEndedMessage`, which the session log no longer
  binds. `TestCancelCommand` of the two editors still has a user: the view cancels a running test with it when the
  window closes. No string lost its last user in that unit.
- **Not seen, to confirm on Windows.** Nothing of V2b-1 was seen on a screen.
  - *The property sheet.* How a wrapped result looks in the 640 px wide sheet (two or three lines on the caution or
    critical surface, picture and buttons in the first line). The note next to the old validation texts (glyph and plain
    text against text in the critical colour). The width of the nested list's button ("Odśwież" grows to the width of
    "Zatrzymywanie…"). The
    time of day in the summary, which is now in the two README pictures of the sheet.
  - *The batch editor, the change plan preview, the main window.* The line of the batch editor between the step strip
    and the content (an empty line at rest in the CSV mode before a file was chosen, and in step 1 of the create mode).
    The footer of both windows without the texts on its left; the width of *Podgląd* / *Wykonaj* as action buttons (they
    are as wide as "Zatrzymywanie…"). The card of the executing step without its bars. The three notes next to the
    content they belong to, and the note in the narrow details pane (the shield is gone). The 14 px rings in the search
    box (in the secondary text colour, at the place of the magnifier) and in the status bar. A long result of an
    execution in the line ("…was executed only in part in contoso.test: 3 objects changed, failed: 2. The reasons are in
    the list of results. Press “Retry failed”…") wraps to two or three lines; how that looks above the results is to be
    seen.
  - *The Recycle Bin, the account review, the comparison.* The line above the note about the forest and the filters of
    the Recycle Bin, and that note without its card (the caution background of "not enabled in this forest" is gone –
    glyph and text only). *Refresh* of the Recycle Bin with glyph and text where an icon button was, next to the icon
    button that clears the filters; *Compare* and *Exact last logon (all DCs)* as wide as "Zatrzymywanie…" needs; the
    accent *Check* next to the scope. The footers of the three windows with buttons only. The notes of the comparison
    window: under the connection box, above the tabs, and in the narrow card of a side (wrapped next to its glyph).
    "Reading nested membership…" in the line right after every comparison – in a small domain it may only flash (the
    open question of a delay before "Working"). The long sentences: "Comparing “…” with the connection fabrikam.test ·
    FABRIKAM\anna.zielinska…" names the object and the whole label of the other connection and stays one line, cut with
    an ellipsis in a narrow window; a failed comparison or restore with its reasons is the kind of result the wrapping
    was decided for. The README picture of the comparison window keeps its client size (1180 × 780), so its tabs are 36
    px lower than before.

**V2b-2 as built (the remaining windows of Views).** The third increment of V2 converts the twelve windows of `Views/`
that still had banners, in three units. Unit 1 converted the four dialogs that open a session or write one object – the
Connect window, "Change domain controller", "Reset Password" and "New Object" – and, because they are the first small
dialogs of the sweep, decided how a dialog carries a status line (the sub-list *Dialogs*, which the later units follow
and extend). Unit 2 converted the window that sends messages to users, the settings window and the Logon Hours dialog –
the first window of the increment with an operation over many items and a known count, the first dialog with tabs, and
the first view that gets no status line at all – together with the rendered preview of a message, a control the message
window hosts. Unit 3 converted the filter builder, the editor of a saved view, the session log and the two pickers – two
editors that work without a session and still test something on the domain controller, a window with a state and no
operation, and two dialogs whose progress bar looked like a mere "busy" mark. With that no window of `Views/` has a
banner any more. The foundations are those of *V2b-1 as built*; nothing was added to the model or to the controls. The
block reads in this order: *Dialogs*, the twelve windows, the tests, what is left behind, and what nobody has seen on a
screen yet. What the sweep still has to do is counted in *What V2b has to do*, above.

- **Dialogs** (decided in unit 1, extended by units 2 and 3; what every later dialog of the sweep follows).
  1. *Where the line sits.* At the top of the dialog, above its fields: under the dialog's own header when it has one
     (the title of the Connect window, the domain and the current domain controller of "Change domain controller"),
     otherwise as the first thing ("Reset Password", "New Object"). It is the place of every converted window – the
     operator looks in one place – and not the place of the old bars (under the fields, next to the buttons): a line at
     the bottom would be a second convention. Its side margin is the margin of the content less 8 px (content at 24 px:
     line at 16 px), so the picture of the line stands over the left edge of the fields; 8 px above it (4 px under a
     header that ends with a gap of its own), and the content starts 8 px under it (0 where its first element has a top
     margin already).
  2. *How it is declared: tab order, default and cancel button.* In the `DockPanel` of the dialog the footer comes first
     (`DockPanel.Dock="Bottom"`, as before), then the header, then `c:StatusLine` (`DockPanel.Dock="Top"`), then the
     content. Tab order is the order of declaration, so the cycle is: fields, footer buttons, the action and the close
     button of the line, fields. From the button that started an operation the next Tab is what the line offers; nothing
     that was a tab stop moved. (Visual order and tab order differ for the line, as in the tool windows of V2b-1 – left
     for the page skeleton of V3.) `IsDefault` stays on the primary button, also when it becomes an action button (Enter
     starts and never cancels – the control sees to it). `IsCancel` stays on the dialog's own Cancel or Close: the
     Cancel of an operation never takes its place, Esc does what it did. The focus on open is untouched: the line is no
     tab stop and has no buttons at rest.
  3. *Size.* A dialog with a fixed height is 36 px higher by default (the 28 px of the line and 8 px above it), like the
     windows of V2b-1. A dialog with `SizeToContent="Height"` grows by itself – and grows and shrinks with a result that
     wraps, the fields moving down by as much. Tests assert that movement (`GrowthOf`), never a height.
  4. *A dialog that closes itself on success says nothing in its line.* The operation ends without a message
     (`scope.Complete(null)`, or the scope is disposed) before `RequestClose`. What the operator learns is what he
     learned before – the toast of the main window, a message dialog of the opener: nothing was added, nothing was taken
     away. The line of such a dialog only ever says: working, stopped, why nothing was started, why it failed, and its
     state that lasts.
  5. *What is said when the button was pressed and nothing was started* – a field is empty, two passwords do not match –
     is status: the answer to the press, an attention the operator can close, built with `PageStatusText.Failure(what
     was skipped, the reason)` – "Not connected: Enter the password.", "Nothing was created: Enter a name." – with the
     key of the reason (or of the lead) as its log copy. These dialogs check their fields on the press and have one
     sentence for the whole form; such a sentence goes stale the moment a field is corrected, which a result may and a
     note may not. A note is what the dialog computes from its fields for as long as they are shown (whether the logon
     name is free, what two options mean together): it stays next to its field.
  6. *An empty line.* A dialog has a summary only when it shows a list or a result (the number of domain controllers;
     the counts of the result list). Otherwise its line is empty at rest – present, one line high, without a picture.
  7. *No session.* A dialog that was opened without a session, or whose session ended, says so as its state that lasts
     from the first moment (`UpdateLasting`: `SessionEndedStatus`, else "Not connected to a domain."), not only when its
     button is pressed.
  8. *Action buttons in a footer.* The primary button of a dialog whose operation can be stopped is
     `Ad.AccentDialogActionButton`: `IsRunning` on the dialog's own flag for *that* operation (`IsConnecting`,
     `IsBusy`), `CancelCommand` / `IsCancelling` on the holder that owns the operation. Two action buttons over one
     holder each need their own flag – with `Status.IsWorking` both would say "Cancel". A button whose operation cannot
     be stopped stays a `Button`. The separate cancel buttons of operations (`Ad.CollapsedWhenDisabledButton` over a
     `…CancelCommand`) go; the cancel of a command stays the same Stop (`cancellationToken.Register(scope.Cancel)`).
  9. *Stop must hold.* Where a step that can be stopped is followed by a write, check `scope.IsCancelling` after the
     step returned as well as in its `catch`: a planner or a locator that finished although Stop was pressed must not
     lead to the write ("Reset Password": Stop while the accounts are checked never resets a password).
  10. *A short read after a picker* (the account that may join a computer) is no operation of its own: its failure is
      said with `Status.Show`, unless an operation runs – that one keeps the line.
  11. *When a dialog gets no line at all.* A dialog that starts no operation – it collects input and hands it to its
      opener, and everything it says is computed from its fields – has no status. It gets **no** `c:StatusLine`, its
      sentences become notes next to what they are about, and it is **not** put on `PagesWithAStatusLine`. It goes on a
      second sorted list of `XamlSourceTests` instead – to be created by the unit that converts the first such view
      (suggested: `ConvertedViewsWithoutAStatusLine`, with a test of its own: no status line, no `Ad.ErrorBar`, no
      `Ad.WarningBar`, no `<ProgressBar`). Unit 1 had no such dialog – all four start an operation – and added no empty
      list. The question to ask: does the dialog itself read or write something that takes time, or can it fail by
      itself? An error its *opener* gets back and shows is not the status of the dialog.
  12. *A dialog with tabs* (added by unit 2: the settings window). The line sits above the tab control, outside the
      scrolling of the tabs, 8 px in from the window edge like in the property sheet: a test that was started far down a
      long tab answers where the operator can see it. A tab that says something of its own gets a holder; the window's
      holder has it in front while that tab is shown (the view tells the view model through a two-way `SelectedIndex`
      binding – foundation 3). On a tab that has nothing to say the line is empty; a result of another tab is still in
      its holder and is said again when that tab is shown.
  13. *What answers a press and is still a note* (added by unit 2: the transport that does not fit, in the message
      window). "Nothing was sent: these messages have images and this AdMng Watchdog takes none" answers the press of
      *Send* – but it offers up to three ways out (a status has one action), and it is true of these messages and this
      transport for as long as they are shown. It stays a note next to the button it explains, with its buttons; the
      line keeps what it showed. The test of Dialogs 5 still holds for a sentence without a choice ("Enter the SMTP
      password"): that one is status.
  14. *"Busy" without a sentence in a dialog* (added by unit 2: the images of a template being read from the image
      store). A `c:ProgressRing` (14 px, secondary text colour) next to the label of the place that is being filled, and
      the old sentence as `AutomationProperties.ItemStatus` of that place – the ring is outside UI Automation (the
      pattern of the quick search of the main window).
  15. *A control that a converted window hosts* (added by unit 2: `Controls/HtmlPreviewView.xaml`, the rendered preview
      of a message). Its elements are in the visual tree of the window, so a banner in it is a banner of the window –
      for the operator and for the UI tests, whose "no element of the old banner styles" walks that tree. Its sentence
      follows the sorting of its host: a note (why the HTML source is shown instead of the rendered preview), and the
      control goes on `ConvertedViewsWithoutAStatusLine`. Look for hosted controls of other folders before a window is
      called converted (`grep '<c:'` and the local namespaces of the view).
  16. *The second list exists* (created by unit 2, as Dialogs 11 asked).
      `XamlSourceTests.ConvertedViewsWithoutAStatusLine` – sorted, never overlapping `PagesWithAStatusLine` – with the
      test `AConvertedViewWithoutAStatusLine_HasNoBannerAndNoProgressBar`: no `StatusLine` element, no `Ad.ErrorBar`, no
      `Ad.WarningBar`, no `<ProgressBar`. On it after unit 2: the Logon Hours dialog and the rendered preview.
  17. *A dialog that works without a session* (added by unit 3: the filter builder, the editor of a saved view). Its
      view model is no window view model – it is not pinned to a session; a filter or a view can be edited and saved
      without one – so it takes what the holder needs as constructor arguments, like the Connect window: the UI
      dispatcher (required) and the status logger (optional). It has no state that lasts: "Not connected to a domain."
      is the answer to the press of *Test* (Dialogs 5: "The filter was not tested: …"), not an error that cannot be
      closed. Dialogs 7 is for a dialog that can do nothing without its session; the question to ask is whether the
      dialog still does its main job.
  18. *A result goes with what it was about* (added by unit 3: the test of a filter or of a view). When the input the
      operation was about changes – the filter; for a view also its search base and scope – the line is cleared
      (`Status.Clear()`): the result is closed, and an operation that still runs ends without a word, because its answer
      would be about another input. What is not that input (the name, the description) leaves the result alone.
  19. *A picker gets the line, not a ring, when its place can fail* (added by unit 3: "Browse for Container"). Its 2 px
      bar looked like "busy" without a sentence (Dialogs 14) – but the read behind it can fail, a failed read is status,
      and a window says its status in one place: so the read is said by the line as well. The ring stays for a place
      whose failure is said elsewhere (the toast of the quick search, a note). The marks in the rows of the tree –
      "being read", "could not be read" – are the progress and the state of a row and stay in the row.
  20. *A state that lasts and follows the selection* (added by unit 3: "Browse for Container"). "The content of this
      container could not be read" is computed in the one `UpdateLasting` from the selected node (the view model follows
      that node's `LoadError`), behind "session ended" and "not connected": it is said while the operator looks at that
      container and gone when another one is selected – nothing is lost, the row keeps its mark. Its Retry is the node's
      own command.
  21. *An operation the dialog cannot stop is begun with `canCancel: false`* (added by unit 3: the read of the container
      tree). The tree reads its containers itself, without a token, as in the main window; a Stop in the line would only
      have stopped the waiting. The dialog's own Cancel closes it at any time, and the tree can be used while it is
      read. An operation that reads through the dialog's own calls takes the token of its scope, as everywhere.
  22. *The end of the session: the state first, then the operation* (added by unit 3). `OnSessionEnded` calls
      `UpdateLasting()` before it disposes the running operation: the line then goes from "working" straight to "the
      session of this window ended". The other way round it shows its summary for a moment in between – one change of
      the line more, found by a test that records every change. "Busy" ends in the same call, not when the domain
      controller answers (the object picker), as unit 2 found for the message window.
- **The Connect window** (`Views/ConnectWindow.xaml`; `Connection/ConnectViewModel`).
  - *What the line says.* Under the header, above the profile box. At rest nothing; once domain controllers were looked
    for, "Domain controllers found: 2" – the summary, because the result is the list of the drop-down. **Working:**
    "Connecting to contoso.test…" (the domain controller when one was chosen, else the domain – the computer's own when
    the box is empty; never the account), "Looking for domain controllers of contoso.test…"; both with Stop.
    **Stopped:** attention "Connecting to contoso.test was stopped: no session was opened. Press “Connect” to try
    again." with *Connect*; "Looking for the domain controllers of contoso.test was stopped: the list was not changed.
    Press “Find domain controllers” to look again." with that button's text. **Not started:** attention "Not connected:
    Enter the password." (the domain, the user name, the profile name, simple bind without TLS), "The domain controllers
    were not looked for: Enter the domain name…". **Failed:** error "Could not connect to contoso.test: The user name or
    password is incorrect. Check the user name (DOMAIN\user or user@domain), the password and Caps Lock. (LDAP 49,
    0x52E)" – the reason, the advice of `ErrorTranslator.ConnectHint`, the code; "Could not find the domain controllers
    of contoso.test: …" with Retry. A connection that succeeded says nothing: the window closes.
  - *How the old places were sorted* (1 error bar, 1 progress bar, and the text and the two buttons that went with
    them).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `ErrorText` under the fields (a field is missing; the bind or the search failed) | status | attention "not connected: why"; error of the operation |
    | progress bar and `StatusText` in the footer ("Connecting to …", "Looking for …", "Domain controllers found: 2", "cancelled") | status, summary | the operation in the line; the count as the summary; a stopped operation as attention |
    | the two *Cancel* buttons of the footer (shown while their command ran) | – | gone: *Connect* and *Find domain controllers* are action buttons |
    | "This profile is already open in the window …" under the profile box (a dot in the colour of that window and a sentence) | note | unchanged – never a banner |

  - *Decisions.*
    - **The view model is no window view model and gets what the holder needs from DI:** the UI dispatcher as a required
      constructor argument, the status logger (`ILogger<PageStatusHolder>`) as an optional one – the smallest honest
      way; `DialogServices` and `ObjectListServices` belong to a connected window and this one has no session yet. So
      there is also no "session ended" state.
    - **One holder, two operations that never overlap:** each command refuses while the other runs (`IsBusy`, as
      before). Each action button has its own flag (`IsConnecting`, `IsFindingDomainControllers`) and both cancel
      through the holder.
    - **A failed bind offers no Retry in the line.** Its advice is nearly always to change something in the window or on
      the account first, and a bind repeated with a wrong password counts towards the lockout of the account; *Connect*
      is where it was. A stopped attempt offers *Connect* (the operator stopped it himself), a failed search Retry (a
      read is safe to repeat).
    - **Nothing of the credential is in a text or a log copy:** the line names the domain or the domain controller, the
      log copy is a key and a result code. (An unexpected exception still shows its own message on screen, as the bar
      did; the log copy has its type name only.)
    - **Choosing a profile closes a result** – what the line said was about the connection typed before (6.7). The
      cancel of the command, which the view uses when the window closes while it connects, is the same Stop.
    - `ErrorText`, `IsBusy` and `ConnectCancelCommand` are unchanged for the tests and the code that read them;
      `StatusText` is still set, and only the view model reads it.
- **Change domain controller** (`Views/ChangeDomainControllerWindow.xaml`; `Dialogs/ChangeDomainControllerViewModel`).
  - *What the line says.* Under the header, above the list. **While nothing else is said** it is the line of the list:
    at rest "3 domain controllers" (in the plural form of the language); "Reading the domain controllers of
    contoso.test…" with Stop; as the state of the list until it is read again – attention "Reading the domain
    controllers was stopped: the list may not show all of them. Type the name of a domain controller, or press Refresh
    to read the list again." with Refresh, and attention "Could not read the list of domain controllers: Access is
    denied. Type the name of a domain controller, or press Retry to read the list again. (AccessDenied)" with Retry.
    **The switch** is the window's own: "Connecting to dc02.contoso.test…" with Stop; stopped – attention "Connecting to
    dc02.contoso.test was stopped: the session stays connected to dc01.contoso.test. Press “Change” to try again." with
    *Change*; failed – error "Could not move the session to dc02.contoso.test: The domain controller is not available.
    Check the domain controller name, … The session stays connected to dc01.contoso.test. (LDAP 81)"; an alias of the
    current one – attention "The domain controller was not changed: Already connected to dc01.contoso.test.". A switch
    that succeeded says nothing: the dialog closes and the main window shows its toast, as before. As the state that
    lasts: the session of the window ended, or it was opened without one.
  - *How the old places were sorted* (1 error bar, 1 warning bar, 2 progress bars, and the text of the footer).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `ErrorText` (why the switch failed; not connected; the session ended) | status | result of the switch; lasting error of the window |
    | warning bar `ListNote` ("The list of domain controllers could not be read. Type the name of a domain controller.") | status | lasting attention of the list, now with the reason, the code and Retry |
    | progress bar next to *Refresh* (`IsLoading`) | status | the read of the list in the line, with Stop |
    | progress bar and `StatusText` in the footer ("Connecting to dc02…", "cancelled") | status | the switch in the line; a stopped switch as attention |
    | the hint under the name box (not a host name; the current one; a read-only domain controller) | note | unchanged – computed from the box while it is shown |

  - *Decisions.*
    - **What the warning bar said decided it: a read failed.** It is the answer to what the window did when it opened
      (or to Refresh), it has a way out (read again), and it is over when the list is read – status, the state of the
      list. It stays an *attention*, as the bar was a warning: the dialog still works, a name can be typed. The reason
      and the code are new (the read of the directory used to be swallowed whole).
    - **Two holders: the list and the window.** The list can still be read when the operator already switches to a name
      he typed, so the read (`ListStatus`, the page in front – it is always shown) and the switch (`Status`) cannot
      share an operation. The window's operation takes the line while it runs; closing its result gives the line back to
      the list.
    - **The read can be stopped** (it ran with the token of the window only): a slow locator no longer has to be waited
      for. A stopped read shows what had arrived – the current domain controller at least – and is a state of the list,
      like one that failed. The end of the session and closing the dialog end the read without a word.
    - ***Change* is an action button, the dialog's *Cancel* stays what it was:** it closes the dialog and, while a
      switch runs, stops that switch first – the same Stop as the button and the line. *Refresh* stays an icon button:
      it never had a Stop of its own; the read is stopped in the line.
    - **The session log entry is unchanged** (the reason and, on a line of its own, the advice): only the line got the
      two as one flowing text, with where the session stays.
    - `ErrorText`, `StatusText`, `ListNote`, `IsLoading` and `IsBusy` are unchanged for the tests and the code that read
      them. The window is 36 px higher by default (600 to 636).
- **Reset Password** (`Views/ResetPasswordWindow.xaml`; `Dialogs/ResetPasswordViewModel`).
  - *What the line says.* At the top, above the accounts. **While nothing else is said** it is the line of the list of
    accounts: "Reading the accounts (2)…" with Stop; stopped – attention "Reading the accounts was stopped: names and
    the lockout state may be missing. Press Retry to read them again."; failed – error "Could not read the accounts:
    reason. Press Retry to try again. (code)"; both results with Retry (the list keeps the names of the distinguished
    names). **The reset** is the window's own: "Checking the accounts (2)…" with Stop, then the execution of the plan
    (foundation 9: "Executing “Reset Password” in contoso.test: 1 of 2…") with Stop; done, attention or error with the
    domain and, when the result list has them, "The reasons are in the list of results."; stopped while checking –
    attention "Checking the accounts was stopped: no password was reset. Press Retry to try again." with Retry. **Not
    started:** attention "No password was reset: The passwords do not match." (a missing password, the length of
    generated ones, no account that can be reset – with the reasons of the planner, each once, three at most).
    **Afterwards**, behind the result: the counts of the result list ("Succeeded: 2, failed: 0, skipped: 0"). As the
    state that lasts: no session.
  - *How the old places were sorted* (1 error bar, 1 progress bar, and the count line).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` (a password is missing or does not match; no account can be reset; the read or the reset failed; no session) | status | attention "no password was reset: why"; result of the operation; lasting error of the window |
    | progress bar in the footer (`IsBusy`) | status | the check and the execution in the line |
    | `ResultSummary` above the result list | summary | `SetIdle`; gone from the page (the list has the header "Results" and keeps the counts as its accessible name) |
    | "the account is locked out" under *Unlock*; the hint about generated passwords; the message of a result row | note | unchanged |

  - *Decisions.*
    - **Two holders: the accounts and the window.** The names may still be read when OK is pressed, so the read
      (`TargetsStatus`, in front) and the reset (`Status`) are apart, as in the Recycle Bin; OK is never refused because
      of the read (a disabled default button would lose the focus it gets when the dialog opens).
    - **The reset uses `ExecutionStatus`** – several accounts, a result that stays on screen – and with it the execution
      can be stopped from the window: OK is an action button (`IsRunning` is `IsBusy`), although it had no Stop before.
      The check before it got the operation's token (planning ran with `CancellationToken.None`), and a Stop during the
      check never proceeds to the execution (Dialogs 9). The line is at rest while the typed confirmation of several
      accounts is asked; for one account the execution takes the line from the check without a pause.
    - **A Stop during the execution may leave one account half done** (its password reset, "must change" not set): that
      is how the executor stops, as it did from the list of background operations. That account is in the result list
      as failed, without a message of its own, so the line says how many were changed and that the rest was not executed
      – and does not point to reasons the list does not have.
    - **A password is never said.** The line says the title of the change, the domain and counts; the generated password
      is in its result row, masked until asked for, as before.
    - `Error`, `ResultSummary`, `IsBusy` and `IsCompleted` are unchanged for the tests and the code that read them.
- **New Object** (`Views/NewObjectWindow.xaml`; `Dialogs/NewObjectViewModel`).
  - *What the line says.* At the top, above "Create in". **Working:** "Creating “Łucja Żak-Wąsowska” in contoso.test…" –
    without Stop. **Not created:** attention "Nothing was created: Enter a name." (the fields, the password, a name or a
    logon name that is taken – found by the checks of the creation –, the findings of the plan). **Failed:** error
    "Could not create “Projekty”: Access is denied. (AccessDenied)"; cancelled in the list of background operations
    before anything was written – attention "Creating “…” was stopped: the object was not created.". For a computer:
    attention "The account that may join the computer was not changed: The chosen user or group has no security
    identifier.", error "Could not read the chosen account: …". An object that was created says nothing: the dialog
    closes; the toast of the main window – or, for an account created only in part, the dialog of before – tells it. As
    the state that lasts: no session.
  - *How the old places were sorted* (1 error bar, 1 warning bar, 1 progress bar).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` (a field; the name is taken; the plan; the creation failed; no session; the joiner) | status | attention "nothing was created: why"; error of the creation; lasting error of the window |
    | progress bar in the footer (`IsBusy`) | status | "Creating …" in the line |
    | warning bar `PasswordOptionsWarning` ("The password never expires, so the user will not be required to change it at next logon.") | note | `Ad.Note.Attention` under the two options: what they mean together, for as long as both are ticked |
    | the availability of the logon name under its box (glyph and text, "Checking…" included) | note | unchanged |

  - *Decisions.*
    - **One operation without Stop, like Apply of the property sheet:** the checks of the name, the plan and its
      execution are "Creating …" (`canCancel: false`); OK stays a plain button. A creation stopped half way would leave
      an account without its password, the checks are single reads, and the execution can be cancelled in the list of
      background operations, as before. The reads of the checks still run with `CancellationToken.None` – *Stop for a
      write* stays open (see *What V2b has to do*).
    - **`ExecutionStatus` is not used:** one object, and no result that stays – the dialog closes. "Executing “New User:
      …” in contoso.test: 0 of 1…" would say less than "Creating “…” in contoso.test…".
    - **The reads when the dialog opens** (UPN suffixes; whether the window's account is a trusted owner of a computer
      account) and the check of the logon name stay as they were: silent, best effort, next to their fields.
    - **A window without a session says so from the start** (it used to say it when OK was pressed).
    - `Error`, `IsBusy` and `CreatedDn` are unchanged for the tests and the code that read them.
- **The message window** (`Views/UserMessagesWindow.xaml`, "Send message to users";
  `UserMessages/UserMessageComposerViewModel` with its part `…RichHtml.cs`).
  - *What the line says.* At the top of the window, 8 px in from its edge, above the line that says how the messages go
    out and the stage shown. **In the editor** nothing at rest; what the editor does is said there (below). **Preparing
    the preview** – *Preview*, F5, *Retry failed*, *Send without images* – is the operation of the window: "Reading the
    accounts (4) and preparing the messages…" with Stop; then, as the summary, the counts of the preview ("Ready: 3 ·
    With warnings: 1 · Errors: 0 · Skipped: 2 · Excluded: 0", which follow the check boxes). Stopped – attention
    "Preparing the messages was stopped: no message was prepared. Press Retry to try again."; could not be read – error
    "Could not read the accounts: reason. Press Retry to try again. (code)"; both with Retry, which prepares the same
    accounts again. A template that stops the plan – attention "No message was prepared: Enter the value of the field
    “termin”." (the reasons that block, each once, three at most). **The test message:** "Sending the test message to
    operator@contoso.test…" with Stop; done "The server accepted the test message for …"; error "The test message was
    not sent: reason"; stopped – attention. **The send:** "Sending the messages: 12 of 300…" – one message whose ring
    shows the part done, with ", failed: 2" once something failed and "– waiting to retry after a temporary failure"
    while it waits – with Stop. How it ended: **done** "The server accepted 300 messages for users of contoso.test. The
    report is in the list below."; **attention** when only a part was accepted ("The messages were sent only in part:
    accepted by the server: 298, failed: 2, not sent: 0. The reasons are in the report below. Press “Retry failed” to
    prepare the messages that were not sent again.") or the send was stopped ("Sending the messages was stopped:
    accepted …, failed …, not sent ….", same advice); **error** when the server accepted none ("Could not send the
    messages: …"); each with *Retry failed* as the action when something can be prepared again. Behind the result, as
    the summary: the counts of the report ("Accepted by the server: 298 · Failed: 2 · Not sent: 0 · Not in this send:
    1"). **Not started:** attention "No message was sent: Enter the SMTP password (kept in memory only until AD Manager
    closes).", "The test message was not sent: Set your address for test messages in Settings → E-mail messages.", or
    why no transport can be used. **The editor** (the page in front while it is shown): "Pasting the formatted content…"
    with Stop; done "Pasted: 2 image(s), 0 note(s) below." (attention when one of the remarks listed under the content
    warns), "Image obraz1.png inserted.", a link domain that was allowed, a link that was removed; attention "Nothing
    was pasted: The clipboard holds no image." (nothing to paste, too large, an image that was refused, the limits of
    the template); stopped – attention "Pasting was stopped: the content was not changed.". As the state that lasts: the
    session of the window ended, or it was opened without one.
  - *How the old places were sorted* (2 error bars, 2 warning bars, 3 progress bars, and the texts that said the same).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` in the header (the template stops the plan; the read failed; the SMTP password is missing; no transport; no session) | status | attention "no message was prepared / sent: why"; error of the read with Retry; lasting error of the window |
    | warning bar `SessionEndedMessage` in the header | status | lasting error of the window |
    | progress bar and `StatusText` in the footer ("Reading the accounts from the domain controller...") | status | the preparation in the line, with Stop |
    | progress bar and `ProgressText` of the sending stage ("Sent 12 of 300 · accepted 11 · failed 1"); the *Stop* button of the footer | status | the send in the line, with the part done; *Send* is an action button and stays for that stage (the stage keeps its title and says what closing the window means) |
    | row under the toolbar of the HTML section: progress bar and `RichStatusText` (pasting, pasted, an image inserted or refused, a link allowed or removed); the *Stop pasting* button | status | operation and results of the editor; *Paste formatted content* is an action button |
    | the same row saying "Loading the template's images…" | "busy" without a sentence | progress ring at the label of the image strip (Dialogs 14) |
    | the same row saying "2 image(s) of the template could not be read (file missing or changed) – remove them or paste them again.", with *Remove missing images* | note | `Ad.Note.Attention` in the image strip, with its button: true for as long as the template has such images |
    | `SummaryText` next to the filter of the preview; `ReportSummary` above the report | summary | `SetIdle` of the stage shown; gone from the page (the report has the header "Report") |
    | `TestResultText` under the selected message | status | result of the test message |
    | error bar `TemplateErrors` under the editor (syntax, fields that are never sent, HTML) | note | `Ad.Note.Error`: the findings of the template check while editing – computed from the text for as long as it is shown |
    | warning bar above the footer: why the messages do not fit the transport, with *Send directly through SMTP*, *Send without images*, *Cancel* | note | `Ad.Note.Attention` in the same place, with its buttons (Dialogs 13) |
    | bar of the rendered preview: WebView2 is missing or the preview is switched off, so the HTML source is shown (`Controls/HtmlPreviewView.xaml`, hosted twice) | note | `Ad.Note.Attention` in the control (Dialogs 15) |
    | the remarks of a paste under the content, the issues of the selected message, the hints | note | unchanged |

  - *Decisions.*
    - **Two holders: the window and the editor.** A paste reads image files and may take long, and *Preview* can be
      pressed meanwhile – on one holder the second `Begin` would have taken the paste off the line for good. The editor
      owns `EditorStatus` and is `Status.Front` exactly while the editor is shown (`OnStageShown`); the preparation of
      the preview is the window's own and takes the line while it runs; back in the editor a paste that still runs is
      said again.
    - **One holder for the preparation, the test message and the send:** their commands exclude one another (`IsBusy`,
      the stage), as before.
    - **Which button cancels what.** *Preview* is running while `IsPlanning` (not `IsBusy`, which a test message sets as
      well), *Send* while the stage is "sending" – it is shown in the preview **and** while sending (`IsSendStep`), so
      the button that was pressed stays where it was; both cancel through the window's holder. *Paste formatted content*
      is running while `IsPasting` and cancels through the editor's. The test message keeps its plain button – it never
      had a Stop – and is stopped in the line, like a preparation that no visible button started (F5 in the preview,
      *Retry failed*, Retry of the line). `CancelSendCommand`, which the old *Stop* button used, is the same Stop.
    - **A read that failed or was stopped is a result, not a state:** the window shows what it showed before – the
      editor, or the preview of before – and Retry prepares the same accounts again (`_lastPlanned`). Nothing is empty
      because of it.
    - **Stop holds.** Stop while the messages are rendered (the last, uncancellable step of the plan) leaves the window
      as it was; Stop while the import of a paste finishes all the same pastes nothing (Dialogs 9).
    - **Tokens** (rule 6). The test message ran with `CancellationToken.None`; it has the token of its operation now.
      The plan's "latest wins" token is linked with the operation's; the paste has the operation's alone. How messages
      are planned, rate-limited, sent, reported and written to the session log did not change –
      `UserMessageComposerTests` passes as it was.
    - **How a send ended is said three times over, each for its reader:** the line (what happened, the counts, what to
      do), the report (which account, why), and – unchanged – the toast of the main window, for which the send finished
      elsewhere (6.7). The sending stage lost its bar: how far the send is, is the ring and the numbers of the line and
      the Cancel of the button.
    - **The end of the session** ends a preparation without a word and "busy" with it at once; a send or a test message
      goes on – neither needs the session – and still says how it ended. Messages that are prepared can still be sent;
      *Retry failed* is not offered then (nothing can be read again).
    - **A new attempt takes what the line said about the one before with it** (`EnsureTransportAsync`): the old error
      bar was cleared when *Send* was pressed again, and "No message was sent: Enter the SMTP password…" next to a note
      about the transport would be two answers to one press. *Back* closes what the line said about the step that is
      left.
    - **What may be named.** The line names the domain of the window and the operator's own address for test messages
      (from the settings); never a recipient, a subject or the content of a message, and never the SMTP password or the
      SMTP user name. The log copy is a key with numbers (the count of accounts, done / total / failed, accepted /
      failed / not sent).
    - The keys are `MessageWindow.Status.*` (`L.MessageWindowStatus`): `UserMessages.Status.*` is the family of the
      states of a planned message. One string is no status: `MessageWindow.SendingHint`, what the sending stage says now
      that its bar is gone. The texts of the editor are the ones its row had (`UserMessages.Rich.*`), as results with
      their keys as log copies.
    - `Error`, `StatusText`, `TestResultText`, `RichStatusText`, `ProgressText`, `ProgressValue`, `ProgressMaximum`,
      `SummaryText`, `ReportSummary`, `IsBusy` and `CancelSendCommand` are unchanged for the tests and the code that
      read them. The window is 36 px higher by default (820 to 856).
  - *Where this differs from the recipe.* The rendered preview is a control of `Controls/`, outside the twelve windows
    of this increment, and is hosted by the correspondence window too, which is not converted: its one bar was converted
    here all the same (Dialogs 15) – with it the message window shows no banner in any state, and the correspondence
    window shows that one note next to its own bars until its unit. The transport that does not fit is a note although
    it answers a press (Dialogs 13). The test message got Stop without an action button. The two short reads before a
    send or a test message – which transport can be used, and what an AdMng Watchdog takes (`RefreshTransportAsync`,
    `FitsTransportAsync`) – were left as they were: silent and with `CancellationToken.None`, before the operation
    begins (Dialogs 10: such a read is no operation); with a service that does not answer the window says nothing
    until its client gives up.
- **The settings window** (`Views/SettingsWindow.xaml`; `Settings/SettingsViewModel`,
  `UserMessages/UserMessageSettingsViewModel` for the "E-mail messages" tab).
  - *What the line says.* Above the tabs (Dialogs 12). The window itself says nothing: Save writes a local file and
    closes the dialog, and the window never acts in a session, so it has no state that lasts. **While the "E-mail
    messages" tab is shown** the line is the tab's: "Testing the connection to smtp.contoso.com:587…" with Stop, then
    done "The connection to smtp.contoso.com:587 works: encrypted (Tls13), authentication: LOGIN." or error "The
    connection to smtp.contoso.com:587 does not work: Stage "TLS" failed: smtp.notSupported – The server does not offer
    STARTTLS."; "Sending the test message to …" with Stop and its result, in the words of the message window;
    "Connecting to Outlook…" with Stop, then done "The Outlook profile “Outlook” has 2 accounts. Choose the account to
    send from in the list.", attention "The Outlook profile has no accounts." or error "Could not read the accounts from
    Outlook: reason". Stopped – attention ("Testing the connection to … was stopped.", "Reading the accounts from
    Outlook was stopped: the list was not changed."). Not started – attention "The connection was not tested: Enter the
    SMTP password (kept in memory only until AD Manager closes)." or, with a value that is not valid, the sentence of
    the note below. On the other tabs the line is empty.
  - *How the old places were sorted* (1 error bar, 1 warning bar, and the two texts that said what a button did).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` above the buttons (the first value of any tab that is not valid: why Save cannot be pressed) | note | `Ad.Note.Error` in the same place – computed from the fields for as long as they are shown |
    | warning bar under the TLS choice ("Without TLS the messages travel unencrypted…") with its confirmation check box | note | `Ad.Note.Attention` next to the choice, while it is chosen; the check box under it |
    | `TestResult` under the test buttons (testing, how the test ended, what was missing) | status | operation and result of the tab; what the server showed – the mechanisms it offers, the fingerprint of its certificate, which *Pin the certificate from the test* pins – stays there as `TestDetails` |
    | `OutlookAccountsText` under the account box ("Accounts of the profile “…”: a@…, b@…", or why Outlook could not be used) | status | operation and result of the tab; the accounts are the list of the box, the line says how many |
    | the hints of the fields, the pinned certificate, the salutation check | note | unchanged |

  - *Decisions.*
    - **Both banners are notes.** "Sender address: not a valid e-mail address." is not the answer to a press: it is
      there while the value is, and goes with it – a validation text. What the insecure choice means is true while it is
      chosen. Neither is a setting "applied at the next start"; the window has no such sentence.
    - **The tab owns the holder, the window shows it** – the window has no operation of its own, so its holder only has
      the tab in front (`SelectedTabIndex`, `SettingsViewModel.MessagesTab`). A result of the tab is not shown on
      *General* or *Computers* and is back on its tab.
    - **Three operations on one holder, one after another:** the connection test, the test message and the read of the
      Outlook accounts each refuse while another runs (`IsTesting`, `IsLoadingOutlookAccounts` – the accounts could be
      read during a test before; with one line for the tab they cannot).
    - **The buttons stay plain buttons, Stop is in the line.** None of the three had a Cancel, and three action buttons
      over one holder would each need a flag; the line is in sight above the tabs wherever the tab is scrolled.
    - **Tokens** (rule 6): the three calls ran with `CancellationToken.None`; they have the token of their operation.
      Closing the window ends what runs without a word (`EndOperations`). A read of the accounts that Outlook answered
      although Stop was pressed leaves the list as it was (Dialogs 9); a connection test that the server answered all
      the same says its result – it changes no setting.
    - **No result code of ours for a failed test:** the reason of the transport carries its own code
      ("smtp.notSupported") and what it means.
    - **The SMTP password is in no text.** The line names the server and the mechanism that was used, never the user
      name or the password; the log copy is a key (and the count of accounts).
    - The three tabs scroll from 564 px instead of 600, so with a tab that scrolls the dialog is as high as it was; with
      a shorter tab it is 36 px higher (it sizes to its content, and grows with a result that wraps). `TestResult`,
      `TestSucceeded` and `OutlookAccountsText` are still set for the tests that read them; `TestDetails` is new.
- **Logon Hours** (`Views/LogonHoursWindow.xaml`; `Dialogs/LogonHoursViewModel`, untouched). **No status line**
  (Dialogs 11): the dialog is pure editing – it reads nothing, writes nothing and hands its result to its opener, which
  turns it into a change plan. Its three warning bars are true of the grid for as long as it is shown and became
  `Ad.Note.Attention` where they were: why the hours cannot be changed (the reason its opener hands in – for example no
  right to write) and a stored value the grid cannot show – above the grid; "No hour is permitted: the user will not be
  able to log on at all." – under it, while the schedule is so, still announced when it appears. The notes are left
  aligned and keep the 720 px cap of the bars (the dialog sizes to its content). The view is on
  `ConvertedViewsWithoutAStatusLine`.
- **The filter builder and the editor of a saved view** (`Views/FilterEditorWindow.xaml`,
  `Views/SavedViewEditorWindow.xaml`; `Filters/FilterEditorViewModel`, `Views/SavedViewEditorViewModel`) – two dialogs
  with the same one operation: the test of what is edited on the domain controller.
  - *What the line says.* At the top, above the name. At rest nothing. **The test:** "Counting the matching objects in
    contoso.test…" – in the editor of a view with the search base as a path, "…in “contoso.test/Firma/IT”…" – with Stop;
    **done** "Matching objects in contoso.test: 42 (0.21 s)."; stopped – attention "Counting the matching objects was
    stopped: the filter was not tested. Press “Test” to test again." with *Test*; failed – error "Could not test the
    filter in contoso.test: reason. Press Retry to try again. (code)" with Retry, a result that can be closed. **Not
    started:** attention "The filter was not tested: Not connected to a domain." ("The view was not tested: …").
  - *How the old places were sorted* (2 error bars, and the text and the button next to *Test*).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` under the preview of the filter builder (a condition without an attribute, a value that is not valid, an LDAP filter that cannot be read) | note | `Ad.Note.Error` in the same place: computed from the conditions while they are shown – why *Save* and *Test* cannot be pressed |
    | error bar `FilterError` under the filter of the saved view editor (with a glyph of its own) | note | `Ad.Note.Error` in the same place (the note brings the glyph) |
    | `TestResult` next to *Test* ("Counting matching objects…", "Matching objects: 42 (0.21 s)", "The operation was cancelled.", why the search failed, "Not connected to a domain.") | status | operation and result of the window; gone from the page |
    | the *Stop* button next to *Test* (shown while the command ran) | – | gone: *Test* is an action button |
    | the issue under a condition, the hint about constructed attributes, the hint under the filter of a view | note | unchanged |

  - *Decisions.*
    - **The count is a result, not the summary.** It answers a press, the dialog shows no list it would count, and it is
      true only of the filter that was tested: a "done" the operator can close, which goes by itself when the filter
      changes (Dialogs 18). The tests of the settings window are the same kind of answer.
    - **The number stands after a colon** – "Matching objects in contoso.test: 42" – as in the text the button had next
      to it: after a verb the Polish noun would need a case the plural helper does not have („pasuje do 42 obiektów”).
    - **No state that lasts, and a view model with two more arguments** (Dialogs 17). `SavedFiltersViewModel`,
      `ShellViewModel` and the smoke test pass the dispatcher and the logger of their services; the container resolves
      them for the editor of a new view.
    - ***Test* is the plain action button** (`Ad.ActionButton`, the style the control takes by itself), left aligned
      where the button was: it is not the primary action of the dialog – *Save* is, and stays the default button. It is
      running while `Status.IsWorking` (one operation, one holder). The cancel of the command, which the view uses when
      the window closes during a test, is the same Stop.
    - **A filter that changes ends its test.** The count of a search that was started for the filter of before used to
      appear next to the new filter. In the editor of a view the search base and the scope count as well (they did not
      clear the old text; `TestResult` keeps that rule for the tests that read it).
    - **Stop holds** (Dialogs 9): a domain controller that answers although Stop was pressed does not turn "Stopping…"
      into a count.
    - `Error`, `FilterError`, `TestResult` and `TestCancelCommand` are unchanged for the tests and the code that read
      them; the element `ErrorBar` of the filter builder is the note `ErrorNote` now. The filter builder is 36 px higher
      by default (720 to 756); the editor of a view sizes to its content.
- **The session log** (`Views/SessionLogWindow.xaml`; `Dialogs/SessionLogViewModel`).
  - *What the line says.* At the top, 8 px in from the window edge, above the filter. At rest how many entries the list
    shows – "Entries: 12 of 40", the text the footer had –, which follows new entries, the filter and the language. As
    the state that lasts: the session of the window ended. The window reads nothing from a domain controller and starts
    no operation.
  - *How the old places were sorted* (1 error bar, and the count).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `SessionEndedMessage` at the top | status | lasting error of the window |
    | `CountText` in the footer | summary | `SetIdle`; gone from the footer, which holds its two buttons |
    | the toast after an export, the dialog when the file could not be written, the toasts of the copy commands | – | unchanged, as in every converted window |
    | "No entries to show." over the empty list | – | unchanged (the hint of an empty list, never a banner) |

  - *Decisions.* What its error bar said decided it: that the session ended is the state of the window, and the count –
    a text, never a banner – is its summary; so the window has a line although it starts nothing. **A window opened
    without a session has no state that lasts:** it lists the log, which is its whole job (Dialogs 7 is for dialogs that
    act in the session). **The state covers the count** – it takes the place of the summary –, while the entries are
    still there to see, filter, copy and export: the known price of one line (*Not seen*, below). `CountText` is
    unchanged for the tests that read it. The window is 36 px higher by default (640 to 676).
- **Browse for Container** (`Views/ContainerPickerWindow.xaml`; `Dialogs/ContainerPickerViewModel`; one property added
  to `Navigation/ContainerNodeViewModel`).
  - *What the line says.* At the top, above the prompt and the tree. **Working**, when the dialog opens: "Reading the
    containers of contoso.test…" – without Stop – until the tree stands at the start container; then nothing. **States
    that last**, the most important first: the session of the dialog ended, or it was opened without one ("Not connected
    to a domain."); the content of the selected container could not be read – error "Could not read the containers in
    “Pracownicy”: Access is denied. Press Retry to try again. (AccessDenied)" with Retry, for as long as that container
    is selected.
  - *How the old places were sorted* (1 progress bar, and the marks of the tree).

    | Old place | Kind | Now |
    |---|---|---|
    | 2 px progress bar above the tree (`IsLoading`: the tree is read and opened at the start container) | status | the read in the line |
    | the glyph next to a container that is being read | progress of a row | unchanged |
    | the error glyph next to a container whose read failed, with the reason as its tool tip | state of a row | unchanged in the row; for the selected container the line says it as well, with the reason, the code and Retry |
    | the path of the selected container under the tree | content | unchanged |

  - *Decisions.*
    - **A status line, not a ring** (Dialogs 19): the bar only showed "busy", but what it stood for can fail – a domain
      controller that does not answer, a container the account may not list – and until now that was said by a 12 px
      glyph whose reason only a pointer could read. The line is the place a keyboard and a screen reader reach.
    - **No Stop** (Dialogs 21). The reads are the tree's; the dialog can be used and cancelled while they run.
    - **The failure belongs to the container the operator looks at** (Dialogs 20). When the dialog opens, the walk to
      the start container ends at the container that could not be read and selects it, so the line explains at once why
      the tree stops there. Retry is the node's own refresh: it reads that container again; the walk to the start
      container is not taken up again – the operator opens the rest himself.
    - **The node carries the code of its failure.** `ContainerNodeViewModel.LoadErrorCode` – a `ResultCode`, set next to
      the translated `LoadError` – is the one touch of a class the main window shares: the line needs the code for its
      text and its log copy, and the exception is not kept.
    - `IsLoading`, `SelectedPath` and the commands are unchanged; a container whose content could not be read can still
      be chosen. The dialog is 36 px higher by default (560 to 596).
  - *Where this differs from the recipe.* The operation takes no token (Dialogs 21). A container the operator opens with
    its expander without selecting it says a failed read only in its row, as in the main window – the line follows the
    selection, and the tree raises no event for a failed read.
- **The object picker** (`Views/ObjectPickerWindow.xaml`, "Select Users, Computers, or Groups";
  `Dialogs/ObjectPickerViewModel`).
  - *What the line says.* At the top, above the object types. **Find Now:** "Searching in “contoso.test/Firma”…" with
    Stop; then, as the summary, how many objects the list shows ("Found: 12 objects", in the plural form of the
    language); attention "More than 500 objects match: the list shows the first 500. Type a name or choose another
    location to narrow the search." when the list is cut; stopped – attention "Searching was stopped: the list was not
    changed. Press “Find Now” to search again." with *Find Now*; failed – error "Could not search in
    “contoso.test/Firma”: reason. Press Retry to try again. (code)" with Retry. **The check of the names** (*Check
    Names*, and OK with names in the box): "Checking the names (3)…" with Stop; attention "Not every name was resolved:
    Several objects match 'GG_' - select one in the list. No object found for 'nikt'." – three names at most, all of
    them stay in the box; stopped – attention "Checking the names was stopped: no object was added. Press “Check Names”
    to check again." with *Check Names*; failed – error "Could not check the names: reason. Press Retry to try again.
    (code)" with Retry. A check that resolved every name says nothing – the chosen objects appear –, and neither does OK
    that closes the dialog (Dialogs 4). As the state that lasts: the session of the dialog ended.
  - *How the old places were sorted* (1 progress bar, and the text next to it).

    | Old place | Kind | Now |
    |---|---|---|
    | 60 px progress bar under the names box (`IsSearching`) | status | the search or the check in the line, with Stop |
    | `StatusText` next to it ("Loading…", "Objects found: 12", "More than 500 objects match - narrow the search.", "No object found for 'x'.", "Several objects match 'x' - select one in the list.", why a search failed) | status, summary | the count of the list as the summary; attention for a list that was cut and for names that were not resolved; error for a search that failed; gone from the page |
    | the chosen objects, the list of results | content | unchanged |

  - *Decisions.*
    - **One holder, three commands that exclude one another** (`IsSearching`, as before): *Find Now*, *Check Names* and
      the name check of OK.
    - **The buttons stay plain buttons, Stop is in the line** – as for the tests of the settings window: none of the
      three had a Cancel, and three action buttons over one holder would each need a flag. OK is the primary button, but
      what it starts is "return the choice"; the name check it may run first is the operation of *Check Names*. (A
      departure from Dialogs 8, on purpose – see the open questions of *What V2b has to do*.)
    - **Both operations can be stopped now** (rule 6): they ran with a "latest wins" token that nothing but closing the
      dialog cancelled; it is linked with the token of the operation.
    - **Stop holds, and so does a failure: a check adds nothing until it is through.** The unique matches used to be
      chosen name by name, so a check that failed half way had already added objects; they are chosen together at the
      end now, and "no object was added" is true when it is said.
    - **A list that was cut is an attention the operator can close**, and the summary behind it counts what is listed
      ("Found: 500 objects"). The list is there to see; what is missing is said once.
    - **The sentences about single names are the ones the dialog had** (`Picker.NotFound`, `Picker.Ambiguous`, with
      their quotes), joined after "Not every name was resolved:"; the log copy has the two counts.
    - **No "not connected".** The picker is always opened with the session of its caller and reads through it, so its
      only state that lasts is "the session ended" (Dialogs 7 has nothing more to say here). The end of the session ends
      "busy" at once and takes *Find Now* and *Check Names* away; before, nothing told the dialog.
    - `StatusText` and `IsSearching` are unchanged for the tests and the code that read them. The dialog is 36 px higher
      by default (640 to 676).
- **Tests.** The Presentation tests run on Linux and are the bulk; the UI tests run on Windows CI only and were written
  without a screen – one file per window, through the real window over the fake directory, asserting only states that
  last until the test changes them (a call the test holds and releases makes "working" and "Stopping…" such states).
  - *The four dialogs (unit 1).* Presentation (run on Linux): `Session/ConnectStatusTests` (15: at rest, not started in
    both languages, connecting with Stop and "Connect", success, a failed bind in both languages without Retry, an
    exception without advice, the window closing while it connects, the search with its summary in both languages,
    stopped and failed with Retry, no domain, a profile that closes a result, the log copies);
    `Dialogs/ChangeDomainControllerStatusTests` (14: the summary in the plural forms, the read with Stop and Refresh, a
    list that could not be read in both languages, the switch with Stop, the dialog's Cancel, a failed switch in both
    languages, what the dialog explains itself, the list read while the operator switches, no session, the end of the
    session during a read and a switch, closing, the log copies); `Dialogs/ResetPasswordStatusTests` (13: the read,
    stopped and failed, not started in both languages, checking then executing in both languages, several accounts with
    the confirmation, nothing succeeded, an account that cannot be reset, Stop while checking – no password reset –,
    Stop while executing, no session and its end, the log copies – and no password in any text);
    `Dialogs/NewObjectStatusTests` (13: not started in both languages, the passwords, creating without Stop in both
    languages, a name that is taken, a failed creation in both languages, a domain controller that does not answer the
    checks, created in part, no session and its end, the joiner, the log copies). The fake connector of the Presentation
    tests can hold or fail a bind and a search. Unchanged and green: `SessionAndConnectTests` (the constructor got the
    dispatcher), `ChangeDomainControllerViewModelTests`, `ResetPasswordTests`, `NewObjectTests`. UI (Windows CI):
    `ConnectStatusUiTests`, `ChangeDomainControllerStatusUiTests`, `ResetPasswordStatusUiTests`,
    `NewObjectStatusUiTests` (three tests each, through the real window; the fake connector of the UI tests can hold or
    refuse a bind, a search and a switch); `ChangeDomainControllerWindowTests` and `ConnectWindowTests` read "not
    connected" and "what is missing" from the line instead of the error bar.
  - *The message window, the settings window, Logon Hours (unit 2).* Presentation (run on Linux):
    `UserMessages/UserMessageComposerStatusTests` (26 cases: at rest with the editor in front; preparing with Stop and
    the counts in both languages; Stop with "Stopping…" at once and Retry that prepares the same accounts; a failed read
    in both languages; a template that stops the plan in both languages; sending with "0 of 2", "1 of 2", "2 of 2" and
    the result with the domain in both languages, then the counts of the report; Stop while sending, also through the
    command of the old button; sent in part in both languages; nothing accepted, and no Retry without a session; no SMTP
    password in both languages – and the password in no text; the test message with Stop, refused, stopped, without an
    address; the paste as the editor's operation while the preview is prepared; Stop while pasting although the import
    finished; the small steps of the editor and that they go with the template; the transport that does not fit as a
    note while the line keeps its summary; the end of the session during a held read – nothing "busy" – and in Polish
    after a language switch; closing the window; the log copies without an address, a name, the content or the password)
    and `UserMessages/UserMessageSettingsStatusTests` (13 cases: the tab in front and the empty line of the other tabs;
    the connection test with Stop in both languages; a failed test with its stage; Stop; what stops a test before it
    starts, in both languages, and the password in no text; the test message; the Outlook accounts in both languages –
    how many, not which; accounts that could not be read, and a stopped read; closing the window; the log copies).
    `HtmlPasteWorkflowTests` got the note of the images that could not be read (no status, in Polish after the switch,
    gone with the images). The fake transport can hold a connect and a connection test, the fake Outlook client a
    connect. Unchanged and green: `UserMessageComposerTests`, `UserMessageSettingsTests`, the salutation, transport and
    library tests. UI (Windows CI): `UserMessagesStatusUiTests` (four tests through the real window),
    `SettingsStatusUiTests` (three), `LogonHoursNotesUiTests` (two: no status line, the three notes). Adapted where the
    place of a message changed: `UserMessagesWindowTests` (the counts of the report are read from the line behind the
    result), `UserMessagesRichHtmlWindowTests` (what became of a paste is read from the line; the transport and the
    fallback of the preview are notes) and `CorrespondenceWindowTests` (how the read of the Outlook accounts ended is
    read from the line of the settings window).
  - *The editors, the session log and the pickers (unit 3).* Presentation (run on Linux), 55 cases:
    `Filters/FilterEditorStatusTests` (12: at rest, with a filter that is not valid as no status; the test with Stop and
    the count in both languages; Stop with "Stopping…" at once, in both languages, and the action that tests again; the
    cancel of the command; a filter that changes during its test, and a result that goes with its filter; a failed test
    in both languages with Retry; no session in both languages; the log copies without the filter, the domain or a
    server text), `Views/SavedViewEditorStatusTests` (11: the same for a view, with the search base in the text, and the
    base and the scope that end a test), `Dialogs/SessionLogStatusTests` (6: the summary that follows entries, filter
    and language; the end of the session in both languages with the entries kept; a window opened without a session; the
    export that keeps its toast; the log copy), `Dialogs/ContainerPickerStatusTests` (9: the read without Stop in both
    languages; a container that could not be read as the state of the selection, in both languages, with Retry; a domain
    that could not be read; no session in both languages – and nothing is read; the end of the session during the read
    as one message; the log copies) and `Dialogs/ObjectPickerStatusTests` (17: Find Now with the summary in the three
    plural forms and both languages; Stop; a failed search; more matches than the list shows; the names that were not
    resolved, in both languages; a check that resolved everything, and three names at most; Stop during the check, which
    adds nothing; a failed check and OK that stays open; the end of the session during a search – nothing "busy"; the
    log copies without a name or a location). A slow domain controller is a search held in
    `FakeDirectorySession.BeforeOperation`. Unchanged and green: `SavedFiltersTests` and `ViewsTests` (the constructors
    got the dispatcher), `PickerTests`, `SessionLogAndHistoryTests`. UI (Windows CI): `FilterEditorStatusUiTests`,
    `SavedViewEditorStatusUiTests`, `ContainerPickerStatusUiTests`, `ObjectPickerStatusUiTests` (three tests each) and
    `SessionLogStatusUiTests` (two), through the real windows. Adapted where the place of a message changed:
    `AccountReviewWindowTests` (what is wrong with a filter is read from the note of the builder) and `ToolWindowTests`
    (the count of the session log is read from the line).
  - *Source rules.* The twelve windows are on the two lists of `XamlSourceTests`: eleven on `PagesWithAStatusLine` –
    nineteen views now, with the two pilots and the six windows of V2b-1 –, the Logon Hours dialog and the rendered
    preview on `ConvertedViewsWithoutAStatusLine`. Proven on Linux with the throwaway script of V2b-1, extended by the
    second list, and with a second script for "every `StaticResource` key of a converted view resolves, before its first
    use".
- **After V2b-2** (counted in the XAML, comments excluded): 17 uses of `Ad.ErrorBar` and 36 of `Ad.WarningBar` in 33
  files and 32 progress bars in 29 files – 40 view files with at least one of the three; 19 views with a status line and
  2 converted views without one. No window of `Views/` has a banner any more; the one progress bar left there is the row
  of an activity in the main window, which stays. V2b-2 took 10 error bars (6 status, 4 notes), 9 warning bars (2
  status, 7 notes) and 10 progress bars (all status). Unit 1: 4 error bars (all status), 2 warning bars (1 status – the
  list that could not be read –, 1 note) and 5 progress bars. Unit 2: 3 error bars (1 status, 2 notes – the first error
  bars of the sweep that are notes: both are findings of a check that runs while the operator types), 7 warning bars (1
  status, the session that ended, and 6 notes, the bar of the rendered preview among them) and 3 progress bars. Unit 3:
  3 error bars (1 status, the session that ended, and 2 notes, the filter that is not valid) and 2 progress bars. By
  area, and what else the sweep has to do: *What V2b has to do*, above. Left behind on purpose, to be looked through
  when the sweep is over – every item is on the list of *After V2b-1*: commands that lost their last user
  (`FindDomainControllersCancelCommand`, `CancelPasteCommand`) and the string `UserMessages.Rich.CancelPaste`;
  `CancelSendCommand`, which only tests use; and view-model members that are no longer shown but are read by tests or by
  the view model itself – the `Error`, `ErrorText`, `StatusText`, `TestResult` and progress members of the converted
  dialogs, with the strings that only feed them (`Views.Testing`, `Views.TestResult`, `Picker.Found`, `Picker.TooMany`).
- **Not seen, to confirm on Windows.** Nothing of V2b-2 was seen on a screen. By unit: the four dialogs of unit 1, the
  three windows of unit 2, the five of unit 3.
  - *The empty line (unit 1).* The four dialogs of unit 1 show an empty band of 28 px at rest (under the header of the
    Connect window, under the header of "Change domain controller" until the list is read, at the top of the other two).
    Whether that reads as a gap, and whether "Domain controllers found: 2" with the info picture is too loud in the
    Connect window. Of the four only the Connect window is in the screenshots of CI (`08-connect.png`, at rest: the
    empty line under the header and the footer with one button); the other three dialogs are in no picture.
  - *Long results in narrow dialogs (unit 1).* The Connect window is 560 px wide: about 65 characters of a result fit
    one line next to the title and the close button. A bind that fails with "the domain controller is not available"
    says some 250 characters with the advice about ports and the LDAPS certificate – four lines, so it is cut in the
    third and whole only in the tool tip (the old bar wrapped freely). The same advice plus "The session stays connected
    to …" in "Change domain controller" (680 px). If that proves too tight: a shorter advice for the line, or a fourth
    line – a question for the owner, not decided here.
  - *The fields move (unit 1).* In the three dialogs that size to their content (the Connect window, "Reset Password",
    "New Object") a result that wraps makes the window higher and moves every field down by 20 or 40 px, and back when
    it is closed (the old bar was under the fields and moved only the footer).
  - *Widths (unit 1).* OK of "Reset Password" and *Change* are as wide as "Stopping…" / "Zatrzymywanie…" needs; *Find
    domain controllers* is wider than that already. The footer of the Connect window holds one button now; the *Export
    results* button of "Reset Password" starts at the left edge of the footer.
  - *The note of "New Object" (unit 1)* (glyph and plain text where a caution bar was), and the header "Results" in
    "Reset Password" where the counts were.
  - *The message window (unit 2).* The line above the row that says how the messages go out – an empty band of 28 px in
    the editor at rest – and the footer with buttons only. *Preview* and *Send (300)* as wide as "Stopping…" /
    "Zatrzymywanie…" needs; *Paste formatted content* is wider than that already. The sending stage: a title and two
    sentences in the middle of the window where an 8 px bar was – whether the 16 px ring of the line and its numbers are
    enough for a send that takes minutes (taskbar progress is not built). The notes: the findings of the template check
    (several lines next to one glyph) under the editor, the transport that does not fit next to its three buttons, the
    images that could not be read with their button, the fallback of the rendered preview; the 14 px ring next to
    "Images in the content". The header "Report". The long results: sent in part with the counts, where the reasons are
    and what to press – some 200 characters, two lines at the default width and three at the minimum width of 860 px;
    "No message was prepared: …" with three reasons may be cut in its third line. Three README pictures show this window
    (the preview, the salutations, the formatted content): they change with this unit – the line at the top, and in it
    the counts that stood next to the filter.
  - *The settings window (unit 2).* The line above the tab strip is empty on every tab at rest, and always on *General*
    and *Computers*: whether that band reads as a gap in this dialog more than in the others. The answer of a test
    appears at the top of the dialog while its button is at the bottom of a long tab – in sight, but not next to the
    button, where the old text was (what the server showed is still there). The note above the buttons where the
    critical bar was (glyph and plain text for "why Save is disabled"); the note of the TLS choice in the column of the
    fields.
  - *Logon Hours (unit 2).* The three notes without their caution background – "No hour is permitted…" is the loudest
    thing the dialog says and is now a glyph and a sentence under the grid.
  - *The two editors (unit 3).* An empty band of 28 px at the top at rest, above the name. *Test* / *Testuj* alone under
    the filter, as wide as "Stopping…" / "Zatrzymywanie…" needs, where a button, a second button and a text stood. The
    note under the filter where the critical bar was – in the filter builder possibly several lines next to one glyph
    (one line per condition that is not valid). "Matching objects in contoso.test: 42 (0.21 s)." on the success
    background for what used to be a quiet text next to the button; the failed test with its reason and Retry in the 600
    px wide editor of a view (two or three lines, and the fields move down with it – the dialog sizes to its content).
  - *The session log (unit 3).* The count above the filter instead of in the footer, with the info picture; the footer
    with its two buttons only. "The session this window was opened in has ended…" is one line at the default width of
    1140 px and two at the minimum of 720 px – and while it is there the count is not (the state takes the place of the
    summary).
  - *The two pickers (unit 3).* "Reading the containers of contoso.test…" at every opening of "Browse for Container" –
    with a fast domain controller it only flashes (the open question of a delay before "Working") – and the empty band
    above the prompt afterwards. In that 460 px wide dialog an error with its reason, "Press Retry to try again." and
    the code takes two or three lines, and the tree moves down with it. In the object picker what a search or a check
    did is said at the top of the dialog, away from the names box and the buttons that started it, where the bar and the
    text were; "Not every name was resolved: …" with three names is three lines at the default width of 720 px. Whether
    "Found: 12 objects" with the info picture is too loud for a count. None of the five windows of unit 3 is in a
    picture of CI.
  - *Known and left: a window without a session.* While a window has no session the line says so and nothing else at
    rest – the state that lasts takes the place of the summary, and the window's own error that cannot be closed covers
    what the page in front says (foundation 3): in a message window whose session ended the counts of the preview and of
    the report are not shown (a result still is, until it is closed), and what the editor says about a paste is not
    shown at all (the remarks of the paste under the content are). To be decided with the rest of "what a window without
    a session still does". The session log is the plainest case: its count is covered by the state while its list still
    works.

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
