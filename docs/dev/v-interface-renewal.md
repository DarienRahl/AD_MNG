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
| `Motion.WorkingDelay` | 150 ms delay | **ours** (V2b-5b) – the status line shows "Working" only after it, so an operation that ends at once never flashes; a delay, not a movement: not behind the switch |
| `Motion.SummaryThrottle` | 800 ms delay | **ours** (V2b-5b) – a screen reader is told a changed summary this long after its last change; a delay, not a movement: not behind the switch |
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
| 10 | Skeleton | list host while the first page loads | loading and no rows yet | gradient sweep over at most 12 placeholder rows (as built, V2b-5b: the rows breathe - an opacity pulse over `Motion.Shimmer` - after the 150 ms of the status line; *V2b-5b as built*) | Shimmer | "rows are coming" – not "empty" | opacity-mask animation (as built: opacity of the host of the rows, from `Motion.Pulse`) | removed from the tree when rows arrive or the window is minimized; static bars under reduced motion |
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
As built (V2b-3, unit 1): the first document tabs after the object list – the list of GPOs, the Group Policy of a
container, one GPO and the WMI filters: twenty-three views. The base class of the Group Policy documents owns their
status, so the other six Group Policy tabs have the line as well, above the bars they keep until their units; the status
bar of the main window says nothing about a Group Policy tab any more (*V2b-3 as built*, section 8).
As built (V2b-3, unit 2): also the four documents with operations of their own – the settings search, the cleanup
advisor, the diagnostics and the model of a computer or a user: twenty-seven views, and the two editors are the only
Group Policy views that keep bars of their own (*V2b-3 as built*, section 8).
As built (V2b-3, unit 3): also the two editors of a GPO – the administrative templates and the security settings:
twenty-nine views. Every Group Policy document has its line and no banner now; what is left of the area are the windows
of unit 4 (*V2b-3 as built*, section 8).
As built (V2b-3, unit 4): also the four windows of the area that read or write something of their own – the GPO picker, the
comparison of two GPOs, the window that backs up, restores and imports GPOs, and the window that updates the ADMX templates
of the central store: thirty-three views. The WMI filter editor, the window that copies settings or merges two GPOs and the
name of a GPO start no operation and are on the second list of the source test, `ConvertedViewsWithoutAStatusLine`. With
that the Group Policy area has no banner and no progress bar left (*V2b-3 as built*, section 8).
As built (V2b-4, unit 1): the bulk creation wizard says its status in the line of the batch editor, in whose first step it
is hosted - no new line: its six views are on the second list of the source test, `ConvertedViewsWithoutAStatusLine`
(*V2b-4 as built*, section 8).
As built (V2b-4, unit 2): also the computer review and the remote actions window, the three event log documents and the
readiness tab - six more views with a line, thirty-nine in all; the restart dialog is on the second list (a note and no
line): fourteen converted views without one (*V2b-4 as built*, section 8).
As built (V2b-4, unit 3): also the four PKI documents - the overview, the templates, the readiness report and the security
review: forty-three views with a line, fourteen converted views without one. The base class of the PKI documents owns their
status, so the status bar of the main window says nothing about a PKI tab any more (*V2b-4 as built*, section 8).
As built (V2b-4, unit 4): also the Watchdog tab - one line for the tab, whose pages (the feed with its details pane, the rules,
the channels, the daily report, the status of the service, the sources and the save bar of the configuration) say their status
there: forty-four views with a line, twenty-seven converted views without one (*V2b-4 as built*, section 8).
As built (V2b-4, unit 5): also the Korespondencja window - one line above its tabs that says what the tab in front says (the
recipients, mailbox owners, preview, send and responses pages each own a holder, the window puts the one of the tab that is
shown in front of its own): forty-five views with a line, twenty-nine converted views without one - the report and the text
prompt of that folder start no operation and are on the second list. With that no view of the application has an
`Ad.ErrorBar`, an `Ad.WarningBar` or a progress bar that says status, and the sweep of V2b is over (*V2b-4 as built*,
section 8).

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
  As built (V2b-3): the GPO picker, the comparison, the backup window and the central store window follow it; the WMI
  filter editor, the copy and merge window and the name of a GPO only collect input and have notes, no line (*V2b-3 as
  built*, section 8, *The windows*).
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
  assertive too, the summary is polite. As built (V2b-5b): a summary whose text changes while the page stays as it is
  (the count of a search) is announced once, politely, 800 ms after the last change, when its page is in front and its
  window is active; "Working" is announced when it is shown, not when the page says it (*V2b-5b as built*).
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

**Built.** Stage V2 is built and closed: V2a (the model and the controls, two pilot pages), V2b-1 to V2b-4 (the sweep over every
view, by area) and V2b-5a and V2b-5b (the old banners removed, then the owner's decisions built: the Stop that finishes the object in
progress, the delay before "Working", the throttled announcement of a summary, the glyph of a note, the status pill, the empty state and
the skeleton, taskbar progress and the close question). Every item of the scope below exists and has a use. The figures are in *Counts*
of *V2b-5b as built*; what is still open - the owner's questions and one thing to do - is *What is still open after V2* in the block of
V2a. The exit criteria are met as the as-built blocks define them (every page that has something to say has exactly one line, counted by
a source test; an operation shows its state within the 150 ms of the line and the button that started it at once), and **by tests, not by
looking: nothing of the stage was seen on a screen** (*Not seen on a screen yet* in *V2b-5b as built*).

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
    message does not (a changed summary does since V2b-5b - once, after a quiet time: *V2b-5b as built*). Its own words
    (title, name) follow a language switch at once.
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
    skipped or stopped and the operator has to act); the summary is polite and was never announced when it only
    changed (since V2b-5b it is, once, after a quiet time - the owner's decision 2, *V2b-5b as built*).
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
- **What V2b has to do** (rewritten at the end of V2b-4, when the sweep was over; counted in the XAML, comments excluded).
  **V2b is done.** At the end of V2a there were 43 uses of `Ad.ErrorBar` and 58 of `Ad.WarningBar` in 51 files, and 65
  progress bars. V2b-1 - seven core windows, *V2b-1 as built* below - took 16 + 13 of the banners and 23 of the progress bars
  away; V2b-2 - the twelve remaining windows of `Views/` and the rendered preview of a message, *V2b-2 as built* - took
  10 + 9 and 10; V2b-3 - the Group Policy area, ten documents and six windows, *V2b-3 as built* - took 7 + 17 and 8; V2b-4 -
  the other seven areas in five units, *V2b-4 as built* - took the last 10 + 19 and 23 (by unit: 3 + 5 and 2, 2 + 7 and 7,
  2 + 2 and 4, 2 + 4 and 7, 1 + 1 and 3). **Left: no `Ad.ErrorBar`, no `Ad.WarningBar` and no status progress bar in any
  view.** What the XAML still had of them at that point (counted by a script, comments excluded) was the definitions of the two styles in
  `Themes/Controls.xaml` (`Ad.ErrorBar`, and `Ad.WarningBar` based on it), which V2b-5a removed (*V2b-5a as built*), and the one
  progress bar of the row of an activity in `MainWindow.xaml`, which stays. **Forty-five views have a status line and twenty-nine converted views have none**: the
  two lists of `XamlSourceTests` (`PagesWithAStatusLine`, `ConvertedViewsWithoutAStatusLine`) hold each other out, and a view
  with a line that is not on the first list fails the rule of the other way round. The figures per increment and unit are
  in *After V2b-1* and *What is left* of *V2b-4 as built*; the by-area list that follows is what was done, in the order it
  was done.
  - By area (error bars + warning bars, progress bars, as they were when the area was taken): `Views/` and `GroupPolicy/` -
    V2b-1 to V2b-3; V2b-4: `BulkCreate/` 3 + 5 and 2 in 4 files (unit 1, hosted in the first step of the batch editor);
    `Computers/` 2 + 3 and 3 in 2 files, `EventLogs/` 0 + 3 and 3 in 3 files and `Readiness/` 0 + 1 and 1 in 1 file (unit 2);
    `Pki/` 2 + 2 and 4 in 4 files (unit 3); `Views/Watchdog/` 2 + 4 and 7 in 9 files (unit 4); `Correspondence/` 1 + 1 and 3 in
    1 file (unit 5); and the row of an activity in the main window (no banner, 1 progress bar), which stays. The HTML preview
    control (`Controls/HtmlPreviewView.xaml`) was converted with the message window that hosts it, and is hosted by the
    correspondence window as well.
  - Sort first (foundation 1 of *V2b-1 as built*): status goes to the line, a note stays where it is with the look of a
    note (`Ad.Note.Info`, `Ad.Note.Attention`, `Ad.Note.Error`), the progress of a row stays in its row, and a place
    that only shows "busy" gets a progress ring – unless its read can fail: then it has status to say (*V2b-2 as built*,
    Dialogs 19). Of the 72 banners sorted so far, 37 were status, 34 notes and one both: of the 33 error bars 27 were status and 6
    notes – findings of a check that runs while the operator types (a template, "why Save is not possible", a filter
    that is not valid, the settings that block a merge) –, of the 39 warning bars 10 were status, 28 notes and one both
    (a failed check is status, an incomplete one a note). So most of the 19 warning bars that are left will stay in their
    places as notes, and most of the 10 error bars will go to the line. What never was a banner is sorted the same way: a
    count or "as of" text is the summary, a result card, a toast or a dialog that says what the window just did is
    status.
  - Per page, the recipe is the foundations of *V2b-1 as built*: a `PageStatusHolder` per view model – and per tab or
    part that says something of its own, with the one shown in front –, the summary in `SetIdle`, every long operation
    in a scope with its token, a state that lasts in `SetLasting` (first of all `SessionEndedStatus`), errors built with
    `PageStatusText.Failure`, an execution through `ExecutionStatus`; in the view one `c:StatusLine` named `PageStatus`,
    the file on `PagesWithAStatusLine`, and a start button with a separate stop or cancel button as one
    `c:ActionButton`. For a dialog, the sub-list *Dialogs* of *V2b-2 as built* says the rest: where the line sits and
    how it is declared (tab order, default and cancel button), what a dialog that closes on success says, that a missing
    field is said when the button is pressed, how a dialog with tabs, a dialog without a session and a hosted control
    are handled, and that a view with nothing but notes gets no line and goes on the second list
    (`ConvertedViewsWithoutAStatusLine`). No candidate for the action button is left. (Done in V2b-2: connect and the search
    for domain controllers, the change of the domain controller, OK of "Reset Password", preview, send and paste of the
    message window, the tests of the filter builder and of the saved view editor. Done in V2b-3: the two start buttons
    of the settings search, the cleanup advisor and the diagnostics, the main button of the backup window and *Update…*
    of the central store. Done in V2b-4: the check and the two start buttons of the bulk wizard's check, the check, the exact
    last logon, the sites and the ping of the computer review, the six actions of the remote computer window, the run of the
    event log reader, the loading of its channels and the runs of the two analyses, the two checks of readiness; in unit 3 the refresh
    of the four PKI documents, the check of the security review, the check of the HTTP addresses and the two probes of the domain
    controllers; in unit 4 the test buttons of the SMTP card and of the channels; in unit 5 the four buttons that start a run of the
    send page, the search for the mailbox owners, the two reads of Sent Items and the preview from Outlook.)
  - Document tabs drop out of `StatusBarText` as they are converted: the ten Group Policy documents did in V2b-3, with
    the base class they share, and the three event log documents and readiness in V2b-4, unit 2 and the four PKI documents in unit 3 and the Watchdog tab in unit 4 (neither V2b-1 nor V2b-2 had a document tab). The wizard of bulk creation (`BulkCreate/`) is hosted in step 1 of
    the batch editor: its holder becomes `SourceStatus` of the create mode (done in V2b-4, unit 1). The Korespondencja window is a
    window of its own and has no tab of the main window. Now that the last view is converted, `Ad.ErrorBar` and `Ad.WarningBar`
    leave `Themes/Controls.xaml`, and with them the strings and view-model members that lost their last user (listed under
    *After V2b-1*) - the first two items of the list that became *What is still open after V2*.
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
  - Settled in V2b-3 (*V2b-3 as built*), again without a change to the model or to the controls: a family of documents
    that share one base class shares one holder, and a part of a document that says something gets a holder in front (the
    sub-list *Group Policy documents*, 1); a lasting state that a kind of document adds to the common ones, through one
    hook (5, 14, 18); an "as of" text as the detail of the summary (15); what an operation started from a document says -
    the plan up to the preview with Stop, why nothing came of it with Retry, and the refusals of the helpers (7, 17); a
    read that was stopped or failed is repeated by the action of the line and by nothing else (13); the stage of an
    operation under the key of the operation, so that it is no new message (11, and the comparison of the central store);
    the line handed back before a window that says its own status opens, and the result said after the read that belongs
    to the operation (*The windows*, 4 and 5); a read that no token can stop and that still gives control back (*The
    windows*, 6); a sentence about the selected item that is a note, not a state (*The windows*, 8); a dialog that only
    collects input gets no line even when it belongs to a flow that has one (*The windows*, 9); and what a document that
    edits what it shows does with a conflict (18, 19).
  - Settled in V2b-4 (*V2b-4 as built*), without a change to the controls and with three factories of the model
    (`ResultCode.Remote`, `ResultCode.Watchdog`, `ResultCode.Outlook`): a flow that another window hosts has the line of its
    host (*Areas* 1); a window that does several things one after another has one holder and a flag per button (2), a family of
    documents shares the holder of its base class (3, 5) and a tab with operations of its own has a holder in front (5, 6); what
    lasts is computed from the facts in one method, in the language of the moment where it is a state of the window and in the
    language it was said in where it is a failure (3, 4, 5, 6); the states of a window of tabs are said by the pages they are
    true for, and a state that a window sets after a page began an operation is shown in its place (7); a stop that cannot give
    control back - the messages already handed to Outlook - ends when the call returns, and one that can - synchronous IO, a
    call nothing can interrupt - returns at once with `Task.Run(…).WaitAsync(token)` and drops the late answer; a read is
    repeated by a link, a change never (2, 6); a quick action never ends an operation of its page (7); the coverage of a read is
    said in the line for what the run could not read and stays in rows and notes for the rest (3); the working text carries the
    key and the numbers, the stages are one message (3, 5); a start button with a separate Stop or Cancel button became an action
    button, with its own flag where several buttons share one holder, and the buttons that never had a Stop of their own and share
    a holder with others keep the Stop of the line - the practice that answers the question of V2b-3 about *Recalculate*, the
    sources of the central store and the three tests of the settings.
  - *What is still open after V2* (until the end of V2b-5b this was *What V2b-5 has to do*, the list of what was left of the V2 scope when the
    sweep of V2b was over: items 1, 2 and 8 were done in V2b-5a and items 3 to 7 and 9 in V2b-5b, after the owner's decisions - *V2b-5a as
    built* and *V2b-5b as built*; what was done is not repeated here).
    - **Taken, no longer open.** The status line keeps its band of about 28 px also when it has nothing to say: it does not collapse,
      because the owner's priority is that content does not move when an operation starts or ends, and a list without rows is answered by
      `EmptyState` in the list (decision 3 of V2b-5b; the question was open since V2b-1 in every view with a line). The Stop of an execution
      finishes the object in progress (1); a changed summary is announced politely and throttled (2); a note may choose its glyph from the
      closed set (4); the delay of about 150 ms before "Working" (5); `StatusPill`, `EmptyState` and `Skeleton`, each with a first use,
      taskbar progress and the close question of a running write (6).
    - **Still to do** (decided, not built): a mark in the tab header of a page that failed while it was not shown (6.7).
    - **Still the owner's - from V2b-5b.** Questions the four units left, to be answered after the screens were seen; none of them blocks
      R1 or V3:
      1. *Sequential plans.* "Finish the object" is not "finish the action": a new Group Policy object is the container with its files, the
         Machine and User containers and the link, so a Stop between two of them leaves a GPO that the undo has to remove (as before
         V2b-5b). Should a sequential plan finish as a whole? (*The Stop of an execution*.)
      2. *The list of background operations* says only "Cancelled" for a stopped execution, with its counters; should its row say "stopped:
         1 of 2" as the line does?
      3. *Stop in the line for a write.* What held Apply of the property sheet and OK of "New Object" back - the half-done account - is gone.
         Should they offer Stop in the line now? (Closing them during their write says "still working", which is true.)
      4. *The selection in the announced summary.* The summary of the object list and of the search tab carries "· N selected", so a
         selection that changes the text is announced after the quiet time (with the arrow keys in a list the selected row is read by the
         list anyway). Should only the count of the list be told? To be decided after use with a screen reader.
      5. *The replication note* of the results of the bulk wizard wears the caution colour with the information glyph, as the owner asked;
         the quieter alternative is the style `Ad.Note.Info`, which needs no property.
      6. *Other empty lists.* The event logs, the failed logons, the PKI security review, the Group Policy diagnostics and the clean-up keep
         the `ListNote` that names the filters that hide the rows: should each become an `EmptyState` with an action of its own? And is the
         sentence of the `EmptyState` enough in the Find window, where the list has no line of its own and the line of the window says
         what the search did?
      7. *More pills.* Should the findings of the PKI security review and of the Group Policy diagnostics, and the channel and domain
         controller rows of the Watchdog status (narrow columns, the state in a tool tip), get a `StatusPill`?
      8. *A write on a page that is not in front* (the Korespondencja send on the *Sending* tab while another tab is shown) is asked about
         when the window is closed but is on neither the line nor the taskbar button. Should the button follow the busiest holder of the
         window instead of the line?
      9. *The main window asks twice in one case:* first about the writes of its tabs and of the windows it owns (the new question), then, as
         before, about the background operations of the connection, generated passwords and unsaved tabs. Should these be one dialog? And
         should a write that cannot be stopped in an owned window (a property sheet that is saving) keep the main window open, as it does
         now?
      10. *The wait after Yes* in the close question has no time limit: an operation that never ends keeps the window open and the line says
          "Stopping…". Should there be a limit, or a way to give up?
    - **Still the owner's - from V2b-3 and V2b-4** (the questions the nine units left, sorted at the end of V2b-4; the settled ones are in
      *Open for the owner*, below):
      - whether the caution glyph of the notes that state how complete results are is too loud, and whether the information note of the
        templates in the secondary text colour is enough;
      - whether the press-time answers of the editors ("Type a name or choose one from the list.") and the answers to the profile commands of
        the wizard belong in the line, cleared by the next edit, or next to their buttons;
      - whether a domain without a central store offers *Update templates…* as the action of a state that lasts;
      - whether the accent "Changed settings: N (not saved)" under the title of an editor stays now that the summary says it;
      - whether a stopped remote action offers a link that reads the status of the computer;
      - whether the line names the sources a run could not read when the coverage panel shows the same in rows;
      - whether *Check again* of readiness is two actions for its two causes;
      - whether the failure of the templates' account count is an attention or an error; whether the HTTP check and the LDAPS probe also say
        "done" in the line; whether *Refresh* is the Cancel of a read the operator did not start; whether the sentence about HTTP addresses
        belongs in the line; whether a stopped security check keeps the count of its findings;
      - whether the Watchdog's stopped read of the feed keeps the old rows; whether the toast after a save, an acknowledgement and "Wyślij
        teraz" is still needed; whether the rollout mode is a state of the line; whether a connected card says "Połączono"; whether a test may
        replace the running test of another editor;
      - from the Korespondencja window: whether the reasons before a send, the address of a test and the errors of saving files leave their
        dialogs for the line; whether the footer keeps the state of Outlook next to the pages that say it; whether the message page needs a
        holder; whether the preview from Outlook says "done"; whether the journal that cannot be used belongs in a note of two pages instead
        of the line of every tab.
  - Still open – in the controls and the model:
    - *A mark in the tab header* of a page that reads or failed (6.7): a failed tab says so only when it is selected.
    - *The same message on two pages:* switching between two tabs that say a message of the same key changes the text
      without the entrance motion and without a live region event (the control compares kind and key).
    - *Reports are not coalesced:* an operation that reports per object (planning thousands of objects) updates the line
      as often.
    - *Stop for a write:* Apply of the property sheet and OK of "New Object" cannot be stopped from the line (the
      execution is an activity). **The half-done account is gone (V2b-5b, decision 1):** the executor no longer leaves the
      account it was working on with its password reset and "must change" not set - Stop finishes the object in progress,
      and the line says "the one that ran was finished" (*V2b-5b as built*, *The Stop of an execution*). Whether Apply
      and "New Object" now offer Stop in the line - the reason that held them back is gone - is still to be decided.
    - *Reads that take no token:* the containers of the tree (the main window and the container picker – the picker's
      read therefore has no Stop), the two short reads before a send, the checks of "New Object".
    - *Said twice, or said elsewhere:* the toast of the main window after an execution, a restore or a send repeats what
      the line of the window that ran it says; an export and a copy to the clipboard still answer with a toast in every
      converted window, not in its line (in V2b-3 also the toast after a backup, which is all the operator learns when the
      backup window closes itself, and the eleven press-time answers of the two editors, which were sorted and left).
    - *Buttons whose operation is stopped in the line only:* the test message, the three tests of the settings, *Find
      Now* and *Check Names* of the object picker (and its OK, the primary button of a dialog) stayed plain buttons –
      they never had a Cancel, and several action buttons over one holder each need a flag. So did, in V2b-3, *Save
      changes…* of the two editors, *Recalculate* of the model, *Browse…* of the backup window and the six sources of the
      central store window (a file, a folder, this computer, three downloads – only one of them can run, and the Stop is in
      the line). Whether they become action buttons is to be decided with the owner (6.7: "cancel where you started").
    - *The empty line of a dialog* (settled, see *What is still open after V2*): a small dialog shows an empty band of 28 px at rest, and always on a tab that has
      nothing to say. **Settled in V2b-5b (decision 3): the one fixed place stays** - the owner's priority is that content does
      not move when an operation starts or ends; `EmptyState` is for lists without rows.
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
      switch; the Group Policy windows do the same for the states they compute (a conflict under the edits, "changed
      elsewhere", the end of the session, the summary); in the other converted windows a failure that lasts keeps the
      language it was said in, and only "the session of this window ended" and "not connected" follow the switch at once. A
      result - "Central store updated…", a stopped backup - always stays in the language it was said in. The Connect window and the two
      editors do not follow a switch at all (they are modal, and the settings cannot be opened over them).
    - *Open for the owner, from V2b-3 and V2b-4* (the questions the nine units left, sorted at the end of V2b-4):
      *Settled.* The "as of" text belongs to the summary as its detail, counts first and the time and the place last (V2b-3,
      Group Policy documents 15, followed by readiness and the PKI documents in V2b-4); a hosted flow has the line of its host and
      a view with nothing but notes none; *Recalculate*, the sources of the central store, the three tests of the settings, the
      source buttons of the wizard, *Validate*, *Reload* and the preview buttons of the Watchdog stay plain buttons with the Stop
      in the line - they never had a Stop of their own and share a holder with other buttons; after a stopped
      recompute of the wizard *Podgląd* plans from the latest computation (a fix after unit 1: the plan step computes again);
      the results of the exact last logon, the sites and the ping of the computer review are results of the line (a toast was
      gone after a few seconds and the line stays until the next operation - the operator closes it); a window whose pages differ
      in what they need says each state on the pages it is true for.
      *Still the owner's:* the questions of the nine units are listed one by one, with those of V2b-5b, in *What is still open after V2*,
      above. (Settled in V2b-5b, decision 3: the empty 28 px band of a line with nothing to say - open since V2b-1, and in every view with
      a line now - stays.)
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
     what it is about. **Banners**: `Ad.ErrorBar` and `Ad.WarningBar` were unchanged for the views that were not
     converted yet and went with the last of them (V2b-5a); a converted view used neither (the source test). **Action buttons**:
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
     or stopped (how many objects are done of how many, how many failed, that the one that ran was finished – V2b-5b; before it:
     what was changed and that the rest was not executed –, then the window's own advice and action – `next`, `action`); **error** when nothing was changed or the plan could not be executed at all (reason
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
  window closes. No string lost its last user in that unit. Added by V2b-3, unit 1: no longer shown but read by tests
  or by the view models themselves – `HasError` of the Group Policy documents, `HealthStatusText` and
  `IsCheckingHealth` of a GPO –, and `StatusText` / `SelectionText` of those documents, which only the shell reads now.
  No string and no command lost its last user in that unit. Added by V2b-3, unit 2: no longer shown but read by tests or
  set by the view models themselves – `ProgressValue` and `IsProgressIndeterminate` of the settings search, the cleanup
  advisor and the diagnostics, `SummaryText` of the model; `Cancel()` and `CancelCommand` of the three documents have no
  view any more (the Stop of the line is theirs), and tests and code without a line still use them. No string lost its
  last user in that unit. Added by V2b-3, unit 3: no longer shown but read by tests and by the view models themselves –
  `ConflictNote` of the two editors (the text of the state in the line). No string and no command lost its last user in
  that unit. Added by V2b-3, unit 4: no longer shown but read by tests or by the view models themselves –
  `Error` of the picker, the comparison, the backup window and the central store window, `IsLoading` of the picker and the
  comparison, `SummaryText` of the comparison and the central store window, `StatusText` of the backup window (with the
  strings `GroupPolicy.BackupsFound`, `GroupPolicy.NoBackups` and `GroupPolicy.BackupRunning`), `ProgressText`, `BusyText`,
  `IsWorking` and `ResultText` of the central store window; `CancelWorkCommand` of the backup window never had a view. No
  string lost its last user in that unit. Added by V2b-4, unit 1: the strings `BulkCreate.Reading`, `BulkCheck.Computing` and
  `BulkCheck.StaleNote` have no user any more; no longer shown but read by tests or by the view models themselves - `Error` of
  the source and of the check step of the bulk creation wizard, `ProfileStatus`, `OptionsError`, `ComputedRows` and
  `TotalRows`; `StopCommand` of the check step has no view. Added by V2b-4, unit 2: the strings `Readiness.Stale`,
  `EvtLog.Cancelled`, `EvtLog.StatusReading`, `EvtLog.LoadingChannels`, `EvtFail.StageFailures`, `EvtFail.StageSuccesses`,
  `EvtFail.StageAnalysis` and `EvtLock.StageAccount`, `.StageDomainControllers`, `.StageLogs` and `.StageAnalysis` have no
  user any more (the stages are `EventLogsStatus.*` now); no longer shown but read by tests or by the view models
  themselves - `ProgressText` and `ProgressValue` of readiness, `LastRunText`, `WasCancelled` and `IsStale` of readiness
  (the state of the line is computed from them), `Error`, `StatusText`, `SelectionText`, `IsReading`, `AsOf` and
  `StopCommand` of the computer review, `ConnectionText`, `Error` and `StopCommand` of the remote window,
  `LoadedText`, `ProgressText`, `StatusText`, `IsSupported`, `NotSupportedText` and `Error` of the event log documents;
  `Cancel()` and `CancelCommand` of the documents and of readiness are the Stop of the line. Added by V2b-4, unit 3: the strings `Pki.ReadProgress`, `PkiEsc.StageSnapshot`, `PkiEsc.StageAccounts`,
  `PkiEsc.StageAccessControl`, `PkiEsc.StagePrincipals`, `PkiEsc.StageAnalysis`, `PkiReady.StageSnapshot`,
  `PkiReady.StageDomainControllers`, `PkiReady.StageAccounts` and `PkiReady.StageLdaps` have no user any more (the stages are
  `PkiDoc.Status.Stage*` now); no longer shown but read by tests or by the view models themselves - `ProgressText`, `StatusText`,
  `SelectionText`, `Error`, `SummaryText`, `ReadAtText`, `CountStatusText`, `LastRunText`, `HealthSummaryText` and `LoadedText` of the
  four PKI documents; `Cancel()` and `CancelCommand` of the security review are the Stop of the line. Added by V2b-4, unit 4: no string and no command lost
  its last user; no longer shown but read by tests or by the view models themselves - `Error`, `Message`, `PreviewError`,
  `ActionMessage` of the pages of the Watchdog tab, `StatusText`, `SelectionText` and `LoadedText` of the tab and of the feed,
  `BannerMessage`, `StateText` and `ConnectionMessage` of the tab, `SummaryText` and `Error` of a delivery test, `Error` and
  `HasConflict` of the configuration. Added by V2b-4, unit 5: the strings `Correspondence.Preview.OutlookFailed`,
  `Correspondence.Recipients.ImportTitle`, `Correspondence.Recipients.DirectoryTitle` and `Correspondence.App.Send.Progress` have
  no user any more; no longer shown but read by tests or by the view models themselves - `StatusText` and `Error` of the
  Korespondencja window, `StatusText` of its recipients and mailboxes pages, `ProgressText`, `SummaryText` and `Error` of the
  mailboxes page, `ProgressText`, `ProgressValue` and `ProgressMaximum` of the send page, `SummaryText` of the responses page;
  `CancelCommand` of the mailboxes page and `StopCommand` of the send page are the Stop of the line. V2b-5a went through this whole list: what it removed, and what it kept and why, is in *V2b-5a as built*.
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
      test `AConvertedViewWithoutAStatusLine_HasNoBannerAndNoProgressBar` (V2b-5a: now `AConvertedViewWithoutAStatusLine_HasNoStatusLine`;
      the banner and progress rules moved into one rule for every view): no `StatusLine` element, no `Ad.ErrorBar`, no
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
    - **A Stop during the execution finishes the account it is working on** (V2b-5b; until then it "may leave one account
      half done": its password reset, "must change" not set, listed as failed without a message of its own). The account
      has all its changes, the one that was not started is in the result list as not done, and the line says "1 of 2 objects
      done, failed: 0, the one that ran was finished" - and does not point to reasons the list does not have.
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
      an account without its password (V2b-5b: it no longer would - Stop finishes the object in progress; offering Stop
      here is still open), the checks are single reads, and the execution can be cancelled in the list of
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

**V2b-3 as built (Group Policy).** The fourth increment of V2 converts the Group Policy area – the views of
`GroupPolicy/` with their view models in `Presentation/GroupPolicy` – in four units. Unit 1 built what the area shares
and converted its four plain documents: the list of GPOs, the Group Policy of a domain, organizational unit or site, one
GPO with its tabs (and the health panel its *Status* tab hosts), and the WMI filters. Unit 2 converted the four
documents that read or compute something of their own and had kept their bars: the settings search, the cleanup advisor,
the diagnostics and the model of a computer or a user. Unit 3 converted the two editors of a GPO, which edit what they
show and save it through the change plan preview: the administrative templates and the security settings. Unit 4
converted the six windows of the area – the GPO picker, the comparison of two GPOs, the window that backs up, restores and
imports, the window that updates the ADMX templates of the central store, and three dialogs that only collect input – and
closed the increment: the area has no banner and no progress bar left. Every Group Policy document is a tab of the main
window and derives from one base class, so the decisions of that class hold for all ten documents: they are written down
first, as the sub-list *Group Policy documents*; the windows are `WindowViewModelBase` view models over the same services,
and what is common to them is the sub-list *The windows*. The foundations are those of *V2b-1 as built* and the *Dialogs* of
*V2b-2 as built*; nothing was added to the model or to the controls. The block reads in this order: *Group Policy
documents*, the shared bars, the four views of unit 1, the four of unit 2, the two editors of unit 3, *The windows* and the
six of unit 4, where this departs from the recipe, the tests, what is left (the counts, what was left behind on purpose),
and what nobody has seen on a screen yet.

- **Group Policy documents** (decided in unit 1; what the later units of the area follow).
  1. *The holder is the base class's.* `GroupPolicyDocumentBase` is `IHasPageStatus`: `Status` is created there – the
     localizer and the dispatcher of `GroupPolicyServices`, the logger of the window's list services
     (`Gp.Ui.Lists.StatusLog`) – and every document view binds its one status line to it. A document creates no second
     holder for what the base does. A part of a document that says something of its own – a tab, a pane – gets a holder
     and is put in front while it is shown (`Status.Front`, foundation 3), like the *Status* tab of a GPO
     (`GroupPolicyObjectDocumentViewModel.HealthStatus`).
  2. *The status bar of the main window says nothing about a Group Policy tab* – since unit 1, for all ten documents:
     the rule of V2a (`ShellViewModel.StatusBarText` is empty for a tab that is `IHasPageStatus`) needed no change.
     `StatusText` and `SelectionText` of the documents are unchanged for the shell and the tests that read them.
  3. *The summary* is `LoadedText`, with `SelectionText` joined by " · " ("7 group policy objects, 1 with different
     versions in AD and SYSVOL · 2 selected"). The base follows both properties: a document sets `LoadedText` and raises
     `SelectionText` as it always did, and calls nothing.
  4. *The load is the operation of the document.* `LoadAsync` begins "Reading “Group Policy Objects”…"
     (`ReadingText`, virtual: the title of the document) with Stop; its token is the serial token of the base, the
     lifetime of the window and the token of the operation. A newer load replaces the running one without a word, as it
     did in the document. A load with steps reports them through `LoadOperation` – one operation for one piece of
     work (foundation 4): the check of a GPO that opens on its *Status* tab says its stages in the line of the load.
     The three documents whose `LoadCoreAsync` *is* their long operation (the settings search, the cleanup, the
     diagnostics) have it in the line already – as "Reading “…”…" – next to their own progress bar and text; their unit
     reports the stages through `LoadOperation`, and their *Cancel* becomes the Stop of the line. `IsLoading` is
     unchanged: the commands and the mark in the tab header follow it. *Refresh* stays the icon button it was: it
     never had a Stop of its own – the read is stopped in the line.
  5. *What lasts is computed in one method of the base*, `UpdateLasting`, the most important first: **not connected**
     ("Not connected to a domain." – an error without an action); **the content cannot be shown** – the load failed
     ("Could not read “…”: reason. Press Retry to try again. (code)", with Retry = `RefreshCommand`), or the load found
     that the GPO or the container is gone: a document says that with `ShowUnavailable(key, action)` from its
     `LoadCoreAsync` – the key is the resource of the sentence and its log copy, the action the one thing to do
     ("Delete the remaining folder"); **the load was stopped** ("Reading “…” was stopped: the tab may not show
     everything. Press Refresh to read it again.", an attention with Refresh) – a state, not a result, because a
     stopped load leaves the document as it was before, or empty; then **what a kind of document adds**: override
     `DocumentLastingState` ("Group Policy changed after these results were made") and call `UpdateLasting()` whenever
     its condition changes. `Error` is still what the document cannot show, as a text; its setter is private now – a
     document cannot say such a state without its key.
  6. *SYSVOL and the PDC emulator are notes, not status* – see *The shared bars* below for the reasons. They are one
     control, `GroupPolicy/GroupPolicyNotes.xaml` (`SysvolNote`, `PdcPanel` with `PdcNote` and the link
     `ChangeDcButton`), hosted under the status line of every document view.
  7. *An operation the document starts speaks in the line of the document.* `GroupPolicyDocumentBase.Actions` is the
     window's `GroupPolicyActions` bound to the holder of the document (`GroupPolicyActions.For(status)`; the scoped
     instance the tree and the shell use has `Status == null` and keeps the dialog and the toast). `RunAsync` – the
     one path of every operation: plan on the session's domain controller, then the change plan preview – says, when
     it has a holder: **working** "Rename GPO “…”: preparing the changes…" with Stop (planning took the lifetime of the
     window only; it can be stopped now, rule 6), and then gives the line back to the document before the preview
     window opens – that window says its own status. When nothing comes of it: **attention** "… – nothing was changed:
     SYSVOL unavailable on dc01: …" (the plan needs SYSVOL), **error** "… – nothing was changed: reason. Press Retry to
     try again. (code)" (the plan could not be made), **attention** "… – nothing was changed: Preparing the changes was
     stopped. …" (the operator's Stop; a plan that was made although Stop was pressed is not previewed – Dialogs 9),
     each with **Retry**, which makes the plan of the *same* request again (the name that was typed, the trustee that
     was picked) and goes on to the preview; **done** "…: nothing to change – the objects already have the requested
     values." where the toast was. What a helper of the operations says before a plan exists – a trustee without a
     SID, a read after the object picker that failed, the default GPOs that cannot be deleted – goes through
     `GroupPolicyActions.RefuseAsync(dialogTitle, operation, reason, logKey, code)`: the line of the document when
     there is one and it is not working, else the dialog. It is there for the extension methods of the later units
     (`CopySettingsAsync`, `DeleteCheckedAsync` …), whose own "why nothing was done" dialogs are still dialogs. Only
     what is *said* changed: plan, preview, confirmation and execution are the pipeline's (rule 5).
  8. *The load and the planning share the holder, and replace one another.* The commands that plan are disabled while
     the document loads (as before), so planning never takes the line from a load; a load that starts while a plan is
     being made (F5, another operation was executed, the session changed, the language) ends that planning without a
     word – the plan would be of a state that is no longer shown. A load that loses the line to another operation
     that is no load says "stopped" as its state: a document is never silently stale. So a long operation of a
     document that may run *while* the document reloads or plans does not go on `Status`: it is the load itself (4),
     or it gets a holder of its own in front (1).
  9. *The view.* Where the shared bars were: `<c:StatusLine x:Name="PageStatus" Margin="0,0,0,6"
     DockPanel.Dock="Top" …/>` over `Status.Current` / `Status.CloseCommand`, then `<gp:GroupPolicyNotes
     x:Name="DocumentNotes" DockPanel.Dock="Top" />`. Declared in place: visual order is tab order (header, line, notes,
     content). All ten views have both since unit 1. The six that still had bars of their own – the two editors,
     modeling, diagnostics, search, cleanup – were on a third, temporary list of `XamlSourceTests`,
     `ViewsWithAStatusLineNotConvertedYet` (one line named `PageStatus`, and at least one banner or progress bar left):
     the unit that converts a view moves it to `PagesWithAStatusLine`, and the test fails for a view that stays on the
     list with nothing left to convert. Such a view said some things twice until its unit came – "Reading “…”…" in the
     line and its own progress text under its toolbar. After unit 2 the two editors were the only ones left on it; unit 3
     moved them to `PagesWithAStatusLine`, and the list – which was to end empty – went with its test (the rule that
     a view with a line is on `PagesWithAStatusLine` needs no exception any more).
  10. *Tests.* Presentation: `GroupPolicyHarness.CreateAsync(culture, statusLog)` for the log copies; a slow domain
      controller is a call held in `FakeDirectorySession.BeforeOperation` (the reads of the documents run on pool
      threads; the planner's first read runs on the caller's thread – start such a command with `Task.Run`). UI:
      `GroupPolicy/GroupPolicyStatusUiSupport.cs` – the main window over the GPO sample (`OpenMainAsync`), a document
      with its view (`OpenAsync<TDocument, TView>`), `AssertTheLineOf`, `AssertNoOldFeedback`, `AssertNoNotes`,
      `GrowthOf`, and `HeldRead`, which holds a read only on a pool thread (a call held on the UI thread holds the
      test: do not hold a planner's read in a UI test – fail it instead).
  11. *A document whose `LoadCoreAsync` is a long read of its own* (added by unit 2: the settings search, the cleanup
      advisor, the diagnostics; the model of a computer, whose read has no stages, needed nothing of it). The base begins
      the operation, so the document takes it from `LoadOperation` at the start of its read and reports its stages to it
      – "Reading GPO folders: 12 of 300" with the part done in the ring, one log key (`GroupPolicy.Status.Reading`) with
      the two numbers, so a stage change is not a new message and is not announced again. The read that is stopped
      ends in the base's state (5): *Stop* of the line, the *Cancel* of the button that started the read and the
      `Cancel()` of the document (kept for code without the line) are the same operation. A stopped read leaves the shown
      results; the status bar of a host without a line still says "cancelled" (`LoadedText`).
  12. *The buttons that start such a read are action buttons* – one per kind of read, because a holder has one
      operation and two buttons over it would both say *Cancel* (*Dialogs* 8 of V2b-2): "Odśwież" / "Odczytaj wszystko
      ponownie", "Szybkie sprawdzenie" / "Pełne sprawdzenie"; the primary one of the cleanup and the diagnostics is
      `Ad.AccentActionButton`. Which of them runs follows the load (`IsLoading` and the kind of read that was asked
      for: `IsRefreshing` / `IsReadingAll`, `IsAnalyzing` / `IsReadingAll`, `IsQuickChecking` / `IsFullChecking`), from
      the first moment – the state of the line and of the button agree while the SYSVOL is opened, before the read
      begins. A reload that reads nothing (a change elsewhere marks the results stale) shows the ring on "Odśwież" for
      the moment it takes.
  13. *A read that was stopped or failed is repeated by the action of the line, and by nothing else.* The state of
      the base (5) offers *Refresh* or *Retry*, both `RefreshCommand`; a plain reload of these documents only checks
      whether the results are stale and reads nothing. `RefreshAsync` is virtual now, and these three documents
      remember the interrupted request (`_interrupted`, apart from the pending one `_requested`) and ask for it in
      their override. Keeping it pending would have started a long check again at the next change in the window or
      switch of the language – behind the operator's back, which the design of these tabs rules out.
  14. *"Group Policy changed in this window" is the state of the tab that lasts* (`DocumentLastingState`, 5):
      an attention with the one action the old bar had – *Refresh* (the search), *Odśwież* (the cleanup), *Sprawdź
      ponownie* (the diagnostics). It follows the language (`UpdateLasting()` at the start of `OnCultureChanged`) and
      stands behind the states every document has.
  15. *The summary may say when the content was read* (`SummaryDetail`, the only thing added to the base class beyond
      the hooks of unit 1): "Found settings: 1 (occurrences: 2) · GPOs searched: 7 · read 14.11.2026 10:44 ·
      *selected occurrence*". The old header line ("GPOs searched … · read …", "Last check: full, …", "Analysed GPOs:
      …") was the "as of" text of the page – the summary by the rule of foundation 1 – and the property sheet and the
      health tab fold it into the summary the same way. The parts are joined by " · " in the order count, as of,
      selection, so that a narrow tab cuts the selection (the details pane shows it) and the tool tip has all of it; a
      part equal to the one before it is said once (the short query, the document that was never read).
  16. *What was not read or evaluated is a note, with the caution look.* The cards "what the index could not read",
      "what the analysis could not see", "what the check could not evaluate" and the former warning bar of the model say
      how complete the results below them are and are true for as long as they are shown – the health note of unit 1,
      for all GPOs. They are `Ad.Note.Attention` (`NotesBar`, a list of sentences in one note) above the results; they
      were cards with the information glyph. The note stands under the notes of the base (`GroupPolicyNotes`), so the
      operator reads: the line, why SYSVOL is not there, what the results lack, the results.
  17. *The refusals of the actions of the settings tabs are said where the operator started them.* `GpoSettingsActions`
      (copy, remove, merge, unlink, the guarded delete) says "not connected" and "the GPO could not be read to compare
      it" through `GroupPolicyActions.RefuseAsync` – in the line of the cleanup or the search that started the action,
      in a dialog for the tree and the shell, as before. Two stay dialogs on purpose: the list of the units that would
      change nothing (a list of reasons per setting, not a sentence) and "the GPO changed since the analysis" (the
      advisor reads the GPOs again at once, which takes the line and would wipe the sentence). The repair runner of the
      diagnostics ("Napraw…") had the actions of the tree: it has the actions of the document now, so its plan is
      "…: preparing the changes…" with Stop in the line of the tab.
  18. *A document that edits what it shows* (added by unit 3: the two editors of a GPO). Its changes wait in memory
      until a save, so besides the states every document has it can be *wrong about the world*: a file it read has
      changed on SYSVOL under the edits. That is **the state of the document that lasts** (`DocumentLastingState`, 5):
      an attention that cannot be closed, the sentence the bar had, and the one way out as its action – **Discard
      changes**. It is not Refresh: a reload keeps the edits and the conflict (the editors reconcile a read with the
      edits and mark the file), so Refresh would change nothing; discarding is the way out, and it asks first (the
      destructive confirmation of the editor) – the line offers a command, it does not destroy by itself. The sentence is
      kept as `ConflictNote` (tests and the state read it) with the server it names, so it follows a language switch at
      once (`UpdateLasting()` through the property hook, as the stale state of unit 2). It stands behind what every
      document has (not connected, a read that failed or was stopped): the conflict is still true then and is back when
      those are gone.
  19. *What an editor says about the content is a note, and an editor's refusals stay toasts.* That a Registry.pol or a
      security file cannot be read (that part of the GPO is read only), where the templates come from or that there are
      none, and what the audit pages leave open are sentences about the content, true for as long as it is shown, and
      the line has no one action to offer for them – notes, with the look of a note (`Ad.Note.Attention`; the
      templates of a central store `Ad.Note.Info`), and for the same reason as SYSVOL (*The shared bars*): a state
      that lasts takes the place of the summary, and a domain without a central store is the common case – every
      session of the editor would say nothing else. The inline answers to a press in a row – "Type a name or choose one
      from the list.", "Already listed.", "The value cannot be written to the file …" –, the options that were kept
      invalid and "This editor does not list the setting …" are toasts as before (the sorting and the reasons: *The two
      editors*).

- **The shared bars** (`GroupPolicy/GroupPolicyBanners.xaml`, deleted; hosted by all ten document views).

  | Old place | Kind | Now |
  |---|---|---|
  | error bar `Error` (not connected, a failed load, "the GPO / the container no longer exists") | status | the state of the document that lasts (5): error, with Retry for a failed load |
  | warning bar `SysvolWarning` ("SYSVOL unavailable on dc01: …") | note | `Ad.Note.Attention` under the line (`GroupPolicyNotes.SysvolNote`) |
  | card `PdcNote` with the button *Change Domain Controller…* (never a banner style) | note | `Ad.Note.Info` under the line, the button as a link (`Ad.LinkButton`) right after the sentence |
  | "Loading…" / the loaded text / the error in the status bar of the main window (`StatusText`), the selection next to it | status, summary | the load in the line; the summary with the selection; gone from the status bar |

  - **Why "SYSVOL unavailable" is a note and not a lasting attention of the line.** It looks like the answer to
    something the document did – it tried to open SYSVOL – and the list of domain controllers that could not be read
    became status in V2b-2. Three things decided the other way. (a) *What it is about:* how complete the content is
    (versions and settings from SYSVOL are missing) and why the commands that write SYSVOL are disabled – a sentence
    about the content, true for as long as it is shown, like "No other connection is open" of the comparison window.
    (b) *It must stand next to the summary.* A state that lasts takes the place of the summary, and it cannot be
    closed. For a connection opened with a distinguished name, or in a host without a SYSVOL writer, SYSVOL is never
    available: every Group Policy tab would then say nothing but that, for the whole session – and the less important
    states of the later units ("the results are out of date") would never be seen behind it. (c) *The line has nothing
    to offer for it:* the cause is rarely over with a second try, Refresh is in the toolbar, and the way out – another
    domain controller, another logon – is not one action of the line. What the document *did* about SYSVOL is still
    status: an operation that needs SYSVOL and finds it gone says so in the line (7).
  - **Why the PDC note is a note with a link.** It is information – where the changes of this tab are made – not an
    attention, and only an attention or an error can last in the line (foundation 2). It is true while the document is
    shown on this session, and it has one optional way to act on it, which opens a dialog: a note with a link in
    place. The link is the first use of `Ad.LinkButton` outside the status line; the style and the note styles were
    not changed.
- **Group Policy Objects** (`GroupPolicy/GroupPolicyObjectsView.xaml`; `GroupPolicyObjectsDocumentViewModel` – the
  document itself; its entries to search, cleanup and diagnostics belong to unit 2).
  - *What the line says.* Under the toolbar, above the notes and the list. At rest "7 group policy objects, 1 with
    different versions in AD and SYSVOL", "· 2 selected" with a selection, "1 of 7 group policy objects shown" while the
    search narrows the list. **Working:** "Reading “Group Policy Objects”…" with Stop; "New GPO “…”: preparing the
    changes…" (Copy, Rename, Delete, Import – every operation that goes to the preview) with Stop. **Results** of an
    operation as in (7). **States:** as in (5).
  - *How the old places were sorted.* The view had no bar of its own: the three shared ones, and the status bar. The
    hint in the middle of an empty list ("No GPO matches the search.") is a hint about the list and unchanged.
- **Group Policy of a domain, organizational unit or site** (`GroupPolicy/GroupPolicyScopeView.xaml`;
  `GroupPolicyScopeDocumentViewModel`).
  - *What the line says.* Under the header, above the notes and the inheritance box. At rest "2 linked GPOs, 5
    applied". **Working:** "Reading “Group Policy – Computers”…"; "Link of “…”: preparing the changes…" (Up, Down,
    Enforced, Link Enabled, Delete Link, Link an Existing GPO, Create and link, Block Inheritance). **State:** "The
    container no longer exists." – an error without an action (`ShowUnavailable(L.GroupPolicy.ContainerMissing)`).
  - *How the old places were sorted.* No bar of its own. Its writes are unchanged (rule 5): the check box of *Block
    inheritance* still shows the state as read after a preview that was closed without executing.
- **One GPO** (`GroupPolicy/GroupPolicyObjectView.xaml` with `GroupPolicyHealthPanel.xaml`;
  `GroupPolicyObjectDocumentViewModel` and its partial files).
  - *What the line says.* Under the header, above the notes and the five tabs. At rest "1 links, 14 settings" – and on
    the *Status* tab what that tab says: "Last check: full, 12.11.2026 14:32 – 2 findings". **Working:** "Reading
    “…”…" (with the stages of the check when the document opens on *Status*); "Checking the GPO…", then its stages
    ("Reading SYSVOL folders: 1 of 1"), with Stop – the check the tab starts when it is selected, or *Check again*;
    the planning of an operation as in (7). **Attention:** "Checking the GPO was stopped: the findings were not
    updated. Press “Check again” to check it again." with *Check again*. **States:** of the document – "The GPO no
    longer exists.", or "The GPO no longer exists in Active Directory, but its SYSVOL folder is still there." with
    *Delete the remaining folder* as the action of the line; of the tab – "Could not check the GPO: reason. Press Retry
    to try again. (code)" with Retry.
  - *How the old places were sorted* (2 warning bars, 1 progress bar, and the text next to it).

    | Old place | Kind | Now |
    |---|---|---|
    | warning bar that held only the button *Delete the remaining folder* (`HasOrphanedFolder`) | status | the action of the lasting error "… its SYSVOL folder is still there." |
    | health panel: progress bar (`IsCheckingHealth`) and the text next to it (`HealthStatusText`: the stage, then "Last check: …") | status, summary | the operation and the summary of the *Status* tab, in the line of the document |
    | health panel: warning bar `HealthNotes` after a check that **failed** (the reason) | status | lasting error of the tab, with Retry |
    | health panel: warning bar `HealthNotes` after a check that could not evaluate everything (SYSVOL unavailable, the folder was not read) | note | `Ad.Note.Attention` above the results (`HealthNote`): how complete they are |
    | *Settings* tab: `SettingsNote` (why the settings are not shown), the note about the templates | note | unchanged – never banners |
    | *Check again* | – | stays a button: it never had a Stop of its own; a running check is stopped in the line |

  - *Decisions.*
    - **The *Status* tab has a holder of its own, in front while it is shown.** The check of the tab and the load of
      the document can overlap – F5 while the tab checks, a check that starts when the load is past its own – so they
      cannot share an operation (foundation 4), and what a check left belongs to its tab: on the other tabs the line
      says the summary of the GPO, and a tab that failed says so when it is selected. The document's own operation
      (its load, a plan that is being made) takes the line whichever tab is shown.
    - **A check that is a step of the load starts no operation of the tab.** Opened on *Status*, or reloaded there, the
      document checks the GPO inside its load on the load's SYSVOL connection: the stages are reported by the load
      (`LoadOperation`), the tab only gets its summary or its failure.
    - **A failed check is the state of the tab, a stopped one a result.** After a failure the tab is empty and has no
      other explanation; after a Stop it shows what the check before found, with its time in the summary.
    - **The button that had a bar of its own is the action of the line.** The bar had no sentence – that was in the
      error bar above it. One state, one sentence, one action: the lasting error carries the command
      (`DeleteOrphanedFolderCommand`, which plans and previews like every change).
    - **The health panel has no line of its own, and neither has the notes control** (Dialogs 15):
      `GroupPolicyHealthPanel.xaml` and `GroupPolicyNotes.xaml` are controls a document view hosts – what the panel
      did or is doing is said by the line of that view, and what is left in them are notes. Both are on
      `ConvertedViewsWithoutAStatusLine`.
    - `HealthStatusText`, `HealthNotes`, `IsCheckingHealth`, `Error` and `LoadedText` are unchanged for the tests and
      the code that read them, except that a failed check no longer puts its reason into `HealthNotes`.
- **WMI Filters** (`GroupPolicy/WmiFiltersView.xaml`; `WmiFiltersDocumentViewModel`). Under the header, above the notes
  and the list: "1 WMI filters" at rest; "Reading “WMI Filters”…"; "New WMI filter “…”: preparing the changes…" for
  New, Edit and Delete; the states of (5). No bar of its own.
- **GPO settings search** (`GroupPolicy/GpoSettingsSearchView.xaml`; `GpoSettingsSearchDocumentViewModel` and its
  partial files).
  - *What the line says.* Under the header and the two read buttons, above the notes and the query. At rest the
    result of the query and when the GPOs were read, then the selected occurrence: "Settings found: 1 (occurrences:
    2) · GPOs searched: 7 · read 14.11.2026 10:44 · Default Domain Policy – Minimum password length"; with a short query
    only "GPOs searched: 7 · read …". **Working:** "Reading “GPO settings search”…", then the stages of the index
    ("Reading GPOs and links…", "Reading GPT.INI versions: 3 of 7", "Reading GPO folders: 12 of 300", "Preparing the
    search…") with Stop and the ring showing the part done; "Copy settings to “…”: preparing the changes…" for the copy
    to another GPO (7 of unit 1). **States:** a read that was stopped ("Reading “GPO settings search” was stopped: the tab
    may not show everything. Press Refresh to read it again.", the results of before stay), a read that failed ("Could
    not read …: reason. Press Retry to try again. (code)"), "Group Policy changed in this window – the results may be
    out of date." with *Refresh*, and the states of (5).
  - *How the old places were sorted* (2 warning bars, 1 progress bar, and what never was a banner).

    | Old place | Kind | Now |
    |---|---|---|
    | progress bar `ReadProgress` and the text next to it (`ProgressText`, "Reading GPO folders: 12 of 300") | status | the operation of the document, its stages in the line |
    | `IndexInfoText` under the toolbar ("GPOs searched: 16 · read …") | summary (as of) | the detail of the summary (15) |
    | the *Cancel* button | – | gone: *Refresh* and *Read everything again* are action buttons (12) |
    | warning bar `StaleBar` with the button *Refresh* | status | the lasting attention of the tab with *Refresh* (14) |
    | `NotesBar` (a card with the information glyph: GPOs not read, no SYSVOL, no templates, hidden values, other domains) | note | `Ad.Note.Attention` (16); the names of the GPOs stay in the tool tips |
    | `ResultText` above the list ("Settings found: N (occurrences: M)") | summary | the line (`LoadedText`); the TextBlock is gone |
    | warning bar `CapBar` ("Narrow the search – 2,000 of 2,345 settings shown") | note | `Ad.Note.Attention` above the results: it is about the list below it, true for as long as it is shown |
    | exports, row actions, details | – | unchanged |

  - *Decisions.* The count of results is no longer a live region of its own: the summary of the line is polite and was
    never announced when it only changed (V2a), so a screen reader no longer heard "Settings found: 3" after each
    query - since V2b-5b it hears the count once, after the operator stops typing (*V2b-5b as built*). The stale state is behind the failed read and the stopped read: a reload that finds the results stale
    replaces the stopped state – the read is still undone, and *Refresh* of the stale state reads it (13).
- **GPO cleanup** (`GroupPolicy/GpoCleanupView.xaml`; `GpoCleanupDocumentViewModel` and its partial files).
  - *What the line says.* At rest the number of proposals, when the GPOs were read and the selected proposal: "16
    proposals · Analysed GPOs: 16, settings read 14.11.2026 10:44 · Empty GPO – Cleanup – Empty"; with a filter
    "Proposals shown: 3 of 16 · …"; before the first analysis "Not analysed yet." **Working:** the same stages as the
    search, and "Analysing the GPOs…". **States** as for the search; the stale one reads "Group Policy changed after the
    analysis – the proposals may be out of date." with *Refresh*. The actions of a proposal (*Delete GPO…*,
    *Unlink everywhere…*, *Disable GPO*, *Copy to another GPO…*, *Remove the duplicate from this GPO*, *Merge with…*)
    are plans with the preview, as before (rule 5): their plan is "…: preparing the changes…" with Stop in this line,
    their refusals are in the line (17), and the read after an executed action is the operation of the document.
  - *How the old places were sorted* (1 warning bar, 1 progress bar).

    | Old place | Kind | Now |
    |---|---|---|
    | progress bar `AnalysisProgress` and `ProgressText` | status | the operation of the document |
    | `LastRunText` under the toolbar ("Analysed GPOs: 16, settings read …") | summary (as of) | the detail of the summary (15) |
    | the *Cancel* button | – | gone: *Refresh* (accent) and *Read everything again* are action buttons (12) |
    | warning bar `StaleBar` with the button *Refresh* | status | the lasting attention with *Refresh* (14) |
    | `NotesBar` (GPOs whose settings were not read completely, no SYSVOL, no templates, other domains) | note | `Ad.Note.Attention` (16) |
    | chips, filters, the list, the details and the actions of a proposal | – | unchanged |

- **Group Policy diagnostics** (`GroupPolicy/GroupPolicyDiagnosticsView.xaml`; `GroupPolicyDiagnosticsDocumentViewModel`
  and its partial files).
  - *What the line says.* At rest the number of findings, when and how deep the GPOs were checked and the selected
    finding: "12 findings · Last check: full, 14.11.2026 10:44 · Versions in AD and SYSVOL differ"; with a filter "3 of
    12 findings shown"; before the first check "Not checked yet." **Working:** "Reading the GPOs, links and WMI
    filters…", "Reading the GPT.INI versions…", "Reading SYSVOL folders: 3 of 8", "Counting users and computers: 15",
    "Checking SIDs: 4 of 9", "Analysing the results…", "Reading the names of the principals…", with Stop; a quick check is
    the first one when the tab opens. **States** as for the search; the stale one reads "Group Policy has changed since
    the last check – the results may be out of date." with *Check again* (at the depth of the findings). *Fix…* is a plan
    with the preview as before; while its plan is made the line says "…: preparing the changes…" with Stop, and when the
    plan cannot be made why, with Retry.
  - *How the old places were sorted* (1 warning bar, 1 progress bar).

    | Old place | Kind | Now |
    |---|---|---|
    | progress bar `CheckProgress` and `ProgressText` | status | the operation of the document |
    | `LastRunText` under the toolbar ("Last check: full, …") | summary (as of) | the detail of the summary (15) |
    | the *Cancel* button | – | gone: *Quick check* (accent) and *Full check* are action buttons (12) |
    | warning bar `StaleBar` with the button *Check again* | status | the lasting attention with *Check again* (14) |
    | `NotesBar` (the quick check does not cover …, SYSVOL not available, folders not read) | note | `Ad.Note.Attention` (16) |
    | chips, filters, the list, the details, *Fix…* and the other actions | – | unchanged |

- **What the computer gets** (`GroupPolicy/GroupPolicyModelingView.xaml`; `GroupPolicyModelingDocumentViewModel`).
  - *What the line says.* Under the header, above the inputs. At rest what the computer and the user get, in numbers:
    "Computer: 3 GPO, 12 settings; User: 1 GPO, 2 settings"; before there is a model "No result – choose a computer or a
    user and click “Recalculate”." **Working:** "Reading “What the computer gets – WAW-PC001”…" with Stop – the model is
    computed in one go, so there are no stages. **States** as for the search: stopped (the model of before stays, the
    export waits for a new one), failed (the tab is empty, with Retry).
  - *How the old places were sorted* (1 warning bar).

    | Old place | Kind | Now |
    |---|---|---|
    | warning bar `NotesBar` (a target that does not exist, SYSVOL not available, nothing chosen) | note | `Ad.Note.Attention` above the loopback line (16) |
    | `SummaryText`, bold above the tabs | summary | the line (`LoadedText` already said it); the TextBlock is gone |
    | `StaleText` under the inputs ("The inputs changed – click “Recalculate”.") | note, never a banner | unchanged: it belongs to the inputs, and the line is not the place to say what the operator changed a moment ago |
    | `LoopbackText`, the toolbar's refresh, *Recalculate* | – | unchanged; *Recalculate* stays a plain button (it never had a *Cancel*; the line stops the calculation) |

- **The two editors** (`GroupPolicy/AdministrativeTemplatesEditorView.xaml` and `SecuritySettingsEditorView.xaml`;
  `AdministrativeTemplates/AdministrativeTemplatesEditorViewModel`, `SecuritySettings/SecuritySettingsEditorViewModel`
  and its partial files). Both are documents that edit what they show and save through the change plan preview – plan,
  preview, execution, audit and undo are the pipeline's and did not change (rule 5); only what is *said* did.
  - *What the line says.* Under the header (the title with the number of changed settings, *Save changes…*, *Discard
    changes*, Refresh), above the notes of the base and the content. **At rest** the numbers of the settings – "12
    settings, 3 configured" / "Security settings: 14 defined" – and, once something is edited, " · Changed settings: 1
    (not saved)". **Working:** "Reading “Administrative templates – GPO name”…" / "Reading “Security settings – GPO
    name”…" with Stop – the read of the templates and of the two Registry.pol files, of the three security files;
    the templates editor reports the stage that takes the time, "Reading the ADMX and ADML files…", under the key of the
    load (one message, announced once); "Administrative templates of GPO name: preparing the changes…" /
    "Security settings of GPO name: preparing the changes…" with Stop – the plan of a save, in the line of the editor
    (unit 1, 7) until the preview opens. **When nothing comes of a save:** the generic "… – nothing was changed: why.
    Press Retry to try again. (code)" with Retry, which makes the plan of the same save again; the edits and the editor
    stay as they were (`IsSaving` ends). **States that last:** those of every document (5) and – behind them – that a
    file changed on SYSVOL under the edits (attention: "The settings of this GPO changed on dc01.contoso.test since you
    started editing. Saving will be refused as a conflict: discard your changes to see the current settings.") with
    *Discard changes*.
  - *How the old places were sorted* (5 warning bars, 1 card that looked like one).

    | Old place | Kind | Now |
    |---|---|---|
    | warning bar `ConflictBar` of the templates editor, `ConflictBar` of the security editor ("the files of this GPO changed on … since you started editing; saving will be refused as a conflict") | status: "this was changed elsewhere" while edits are pending (foundation 1, as the property sheet) | the state of the document that lasts, an attention with *Discard changes* (18) |
    | warning bar `FileBar` of the templates editor ("`User/Registry.pol` cannot be read: this part of the GPO cannot be edited"), `FileBar` of the security editor ("… cannot be read: its settings cannot be edited") | note: it says how complete the content is and why that part is read only, true for as long as it is shown | `Ad.Note.Attention` (`FileNote`, text `FileText`) under the notes of the base (19) |
    | card `DefinitionsBar` of the templates editor (not a banner style: a card with the information glyph and, without a central store, the caution background; the sentence "Templates: central store on dc01 (213 files)" / "dc01 has no central store: templates of this computer …" / "No ADMX templates …", the two buttons, the problems of the templates) | note | `Ad.Note.Info` with a central store (`DefinitionsNote`, text `DefinitionsText`), `Ad.Note.Attention` without one (`DefinitionsMissingNote`) – two siblings, as the Recycle Bin does –; the buttons stay at the right of the sentence, the expander of the problems under it (19) |
    | warning bar `AuditBar` on the *Audit* page of the security editor ("both the categories and the subcategories are configured, but the subcategories are not forced …") | note: about those pages, true while they are shown | `Ad.Note.Attention` (`AuditNote`, text `AuditNoteText`) above the rows of that page |
    | policy panel of the templates editor (`InconsistentNote`, `NotWritable`, the problems of the typed options), the information card of the *Groups* page, the notes of the option groups and of the services page, the hints of empty lists | notes that never were banners; descriptions of settings | unchanged |

  - *Decisions.*
    - **A conflict under the edits is the state of the tab, the rest are notes.** The conflict is the "changed
      elsewhere while there are pending changes" of the property sheet (foundation 2): it is the answer to a reload the
      editor did, it concerns the whole document – the edits of both halves, of all pages –, it makes the main action of
      the document (Save) fail, and it has one way out. It is rare and temporary, so it does not hide the summary for a
      session; the number of changed settings is also in the header. A file that cannot be read, the origin of the
      templates and the audit sentence are different: they are true of one part of the content for as long as it is
      shown, and the line has no one action for them – the update of the templates is a window of its own, and the way
      out of a damaged file is outside the program. A *lasting* attention for "no central store" would also have taken
      the place of the summary in every editor of a domain that has none (19).
    - **The action is *Discard changes*, not Refresh.** Reading the files again keeps the edits and the conflict, so
      Refresh would leave the line as it was. Discarding is destructive, so the command is the editor's own
      `DiscardCommand`, which asks for a confirmation first; the line does not do anything by itself that the toolbar
      button would not.
    - **The stage of the templates, and no stage for the security files.** The ADMX and ADML files of a central store are
      hundreds of small reads over SMB, and a cold read takes seconds – the line names it. The security editor reads
      three small files and, at most, one lookup of names; a stage there would flash. The stage of the templates is
      reported under the log key of the load, so it is not a new message (11).
    - **The conflict is said in the language of the moment.** The sentence is made from the server of the conflict
      (`_conflictServer`), not stored as a text, and rebuilt at the start of the language switch, so the line is Polish
      or English while the editor reads itself again, not when it is through.
    - **No `ExecutionStatus` and no `Status.Front`.** The preview executes the plan and says it in its own line; no
      page of either editor says something of its own that is status (the security pages have one note, the audit one),
      so there is one holder, the document's.
    - ***Save changes…* stays a plain button.** It never had a Stop of its own; the plan can be stopped in the line (the
      button is disabled while the editor is saving anyway). The header keeps the accent text with the number of changed
      settings under the title: it is the marker of unsaved edits, which this increment leaves as it is (the close
      question and the unsaved changes are not part of it) – the summary of the line repeats the number.
    - **The toasts stay.** The editors say eleven things with a toast: in both, "This editor does not list the setting
      …" and the refusal of a save while an option is not valid ("Correct the options of … first" / "… has a value that
      is not valid: correct it or undo it before saving"); in the templates editor "…: the options typed are not valid,
      so they were not put into the GPO"; in the security editor the answers to a press in a row – "Type a name or
      choose one from the list.", "… cannot be written to the file: a name must not contain …", "Already listed."
      (twice), "Type the service's short name …" and "The value cannot be written to the file …" (from six rows). By the
      rule of this increment they say what the editor just did. They were left on purpose. (a) A result in the line
      stays until it is closed or the next operation, and editing is not an operation: "Type a name or choose one from
      the list." would stand over a corrected field for the rest of the session – the dialogs of V2b-2 accept that,
      because they close after the next press; an editor that is open for an hour cannot. (b) "This editor does not
      list the setting …" answers a press made in *another* tab ("Edit this setting") – a toast by 6.7 – and is raised
      inside the load of the editor, where a `Show` would cancel the load. (c) The options that were kept invalid belong
      to the handling of unsaved edits, which this increment leaves; the mark at the row is the lasting cue. The
      refusal of the save cannot be reached (the command is disabled while an option is invalid). Whether the
      press-time answers should move to the line, cleared by the next edit, is left to the owner.
    - `ConflictNote`, `FileNote`, `DefinitionsNote`, `IsCentralStoreMissing`, `AuditNote`, `LoadedText`, `SelectionText`,
      `IsSaving` and the commands are unchanged for the tests and the code that read them; the views no longer bind
      `ConflictNote`.
- **The windows** (decided in unit 4; what the later work on windows of any area follows, next to the *Dialogs* of
  V2b-2). The six windows of the area are `WindowViewModelBase` view models: the picker, the comparison, the backup window
  and the central store window have a status line; the WMI filter editor, the copy and merge window and the name of a GPO
  have none.
  1. *Who gets a line.* A window that reads or writes something of its own, or can fail by itself, gets one – the picker
     (reads the GPOs), the comparison (reads two GPOs and the templates), the backup window (reads a folder and a backup,
     writes backups, plans a restore or an import), the central store window (reads a package, compares, writes a backup,
     plans). A window that only collects input and hands it to its opener gets none (the question of *Dialogs* 11): what
     the opener does with it – the plan, its refusals, the preview, the result – is said by the line of the *document* that
     opened the window (7 of *Group Policy documents*, `GroupPolicyActions.For`), and a window that is opened from the tree
     or the shell has no line to say it in and uses a dialog or a toast, as before. So the WMI filter editor, the copy and
     merge window and the name of a GPO are on `ConvertedViewsWithoutAStatusLine`; the name of a GPO had no banner and is on
     the list as a guard (its validation text stays under the box: it never was a banner).
  2. *Where the line sits.* As in *Dialogs* 1 and 2: at the top, 8 px from the top and (content margin − 8) from the sides,
     declared after the footer so that Tab goes from the buttons to what the line offers and then to the fields; the footer
     holds buttons only now (a progress bar, a summary text and a status text went from three footers). Two windows are
     tool windows with a header row of their own: the comparison puts the line under that row (title, *only
     differences*, refresh), the other three at the very top. The line is above the title in the backup window – its title is
     the title of the window repeated, not a header the line should follow. The windows are 36 px higher by default (picker
     520 to 556, backup window 600 to 636, comparison and central store 680 to 716), and a result that wraps moves the fields
     under it down by as much (`GrowthOf`).
  3. *Holders.* Every window with a line owns one `Status` made from the services it already gets (`DialogServices`: the
     localizer, the dispatcher and the status log of the list services). The backup window has a second, `DetailsStatus`,
     in front of the first while backups are listed (foundation 3): the read of the selected backup starts with every
     selection – also while the window plans, because the list is not disabled then – so it cannot share an operation with
     the window; what it says (working, the state of that selection) is said by the one line of the window whenever the
     window has nothing more important.
  4. *The plan up to the preview, and the line handed back.* A window that plans a change says it like the documents do
     (*Group Policy documents* 7) and ends its operation before the change plan preview opens - that window says its own
     status: "Restore “…” from a backup: preparing the changes…" with Stop, then what nothing came of - SYSVOL not available
     (attention), the plan could not be made (error), the operator's Stop (attention: "Preparing the changes was stopped.") –
     each "– nothing was changed: why" with Retry, which makes the plan again, and Stop holds: a plan that was made
     although Stop was pressed is not previewed (*Dialogs* 9). The backup window does this with the texts of the documents
     (`GroupPolicyStatus.Preparing` and `NotChanged`); the central store window has its own, because its operation begins
     with the backup ("Preparing the update of the central store…", "Backing up the central store…", "The update was
     stopped: the central store was not changed. Press “Update…” to try again.", with the update button as the action).
  5. *The read that belongs to the operation says the result.* After an update the central store window compares again; that
     comparison is the last stage of the update ("after" scope), and the result - "Central store updated: written files: 2.
     Open editors read the templates again. Backup written to …" - is said when the new files are shown, as the restore of
     the Recycle Bin does. When that comparison cannot be made, its state is the more important thing and the result is not
     said. When the preview was closed without executing (or nothing had to change) and a backup was made, the line says that
     the backup was written. A backup that ends well says nothing in the backup window: the window closes, and the toast of
     the main window is what the operator learns (*Dialogs* 4).
  6. *Stop returns control for a read that no token can stop.* The listing of a folder of backups and the read of a backup
     – a sync call that may sit on a share that does not answer – run on pool threads and are waited for with
     `WaitAsync(token)`: Stop gives the window back at once, and what the abandoned call finds later is of no interest. The
     thread itself is not stopped (the API takes no token); that is the price, and a Linux test holds a read with a FIFO to
     see it (`GpoBackupStatusTests`). The read of the picker (no token before), the plan of the backup window and the
     backup and the comparison of the central store window (the lifetime of the window only) take the token of their scope
     now: they can be stopped (rule 6). The planner of the central store takes none; Stop holds after it (above).
  7. *A stage is not a message.* The comparison as the last stage of a load or an update, and the templates in the
     comparison window, are reported under the key of the operation (`Report(text)` without a log copy keeps the key): one
     log copy, nothing announced twice, no entrance motion. A comparison that an option starts is an operation of its own;
     started by a load it is its stage (a parent scope is passed down: one holder never has two operations that overlap).
  8. *A sentence about the selected item is a note, not a state.* "This backup comes from another domain: restore it there,
     or import its settings into a GPO of this domain" is true for as long as that backup is selected, and restore is refused
     while import is possible: a note under the list (`OtherDomainText`), not the `Error` of the window it was.
  9. *A dialog that only collects input has notes.* The stored WMI queries that could not be read, more source GPOs than one
     copy takes, the settings that block a merge and the differences a merge changes are sentences about the fields, true
     while they are shown: `Ad.Note.Attention` or `Ad.Note.Error` where a bar was, glyph and words without a fill. The
     error notes are findings of a check that runs while the operator chooses ("why OK is disabled"), as in V2b-2.
  10. *"Not connected" from the start* (*Dialogs* 7): the four windows with a line say it at once when they were opened
      without a session, and the end of the session first (`SessionEndedStatus`), as in every window. A package can still
      be read without a session, and the line keeps saying why nothing can be compared or written.
  11. *Log copies hold keys, counts and the kind of a failure.* Nothing of a GPO name, a folder, a domain, a server text, a
      download address or a package name reaches them (CLAUDE.md rule 1) - the tests read the log of every window and
      look for exactly those. The exceptions of the package loader have no result code of their own; the reason is on
      screen and the log copy has the key of what was tried.
- **The GPO picker** (`GroupPolicy/GpoPickerWindow.xaml`; `GpoPickerViewModel`).
  - *What the line says.* At the top, above the filter. **Working:** "Reading the group policy objects of contoso.test…"
    with Stop. At rest the count ("Group policy objects: 12"; with the filter "3 of 12 group policy objects shown", the
    text of the GPO list). **States:** a read that was stopped ("…was stopped: the list is empty. Press “Retry” to read
    them again.", attention) or failed ("Could not read the group policy objects of contoso.test: reason. Press Retry to try
    again. (code)"), both with Retry; the end of the session, or no session.
  - *How the old places were sorted* (1 error bar, 1 progress bar).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` over the list (a failed read, "not connected", the session that ended) | status | the state of the dialog that lasts, with Retry for a read |
    | progress bar in the middle of the list (`IsLoading`) | status | the read of the dialog, with Stop |

  - *Decisions.* A read that was stopped or failed leaves an empty list and is the state of the dialog until it is read again
    (*Dialogs* 19 and 20) - not a result: the list has no other explanation. The count is the summary and follows the
    filter. `IsLoading`, `Error` and `LoadCommand` are unchanged for the tests that read them.
- **The comparison of two GPOs** (`GroupPolicy/GpoComparisonWindow.xaml`; `GpoComparisonViewModel` and its partial file).
  - *What the line says.* Under the title and its options, above the note and the rows. **Working:** "Comparing “A” with
    “B”…" with Stop, then - once the rows are shown with the registry names of the settings - the stage "Reading the
    administrative templates to name the policies…" (the same message, the key of the comparison). At rest the count of
    differences ("12 differences in 140 items"; it moved from the footer). **Results and states:** a comparison stopped
    before it showed anything ("Comparing was stopped: nothing is shown. Press “Refresh” to compare again.", attention that
    lasts; "…the rows may be out of date…" when the rows of the comparison before are still shown) or failed (an error with the reason, the code and Retry that lasts); a comparison stopped while the templates
    were read is a result that can be closed ("…the policies are shown under their registry names"), because the rows are
    all there; a failed export ("The comparison was not exported: …") is a result that can be closed, a good one keeps its
    toast; the end of the session, or no session.
  - *How the old places were sorted* (1 error bar, 1 warning bar, 1 progress bar, the summary text).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` (a failed comparison or export, "not connected", the session that ended) | status | the state of the window with Retry; a failed export a result; "not connected" and the end of the session the state of the window |
    | warning bar `Warning` ("SYSVOL unavailable… Settings and files were not compared.", a GPO too large) | note | `Ad.Note.Attention` above the rows: how complete the rows are, true while they are shown |
    | progress bar in the middle of the list (`IsLoading`) | status | the operation of the window, with Stop |
    | `SummaryText` in the footer | summary | `SetIdle` of the window; gone from the footer |

  - *Decisions.* One scope runs through the whole of `LoadAsync`, the stage of the templates included: ending it when the rows
    appeared (as `IsLoading` does) would have left the names arriving a moment later unannounced and unstoppable. The
    summary is computed from the rows again after a language switch (it was left in the old language). The Refresh button
    stays an icon button that never had a Stop of its own (*Group Policy documents* 4).
- **The backup, restore and import window** (`GroupPolicy/GpoBackupWindow.xaml`; `GpoBackupViewModel`).
  - *What the line says.* At the very top. **Working:** "Reading the backups in the folder…", "Reading the backup of “…”…"
    (holder of the selection), "Backing up the GPOs: 1 of 3…" with the part done in the ring, "Restore “…” from a backup:
    preparing the changes…" – each with Stop. At rest the count of backups in the folder ("Backups in the folder: 3", "No
    backups in the folder"); nothing in a mode that lists none. **Results:** a backup that ended well says nothing (the
    window closes); stopped, "The backup was stopped: 1 of 3 GPOs were written to the folder. Press “Back Up” to try again."
    with the button that backs up; failed, "Could not back up 1 of 3 GPOs: reason. (code)" (the reason of the first
    failure; it was only a count); SYSVOL not available before anything was written, "The backup was not made: …" with
    Retry; a stopped read of the folder, with Retry (a result: the list is as it was); the plan as in 4 above. **States:** a
    folder that could not be read ("Could not list the backups: The folder could not be read or written. Press Retry to try
    again. (ArgumentException)") and a backup that could not be read or was stopped ("Could not read the backup of “…”: …
    (GpoBackupException)", "Reading the backup was stopped: it cannot be used yet. Press “Retry” to read it again.") –
    the second is the state of that selection and goes with it; the end of the session, or no session.
  - *How the old places were sorted* (1 error bar, 1 progress bar, a status text, and a sentence that was in the error bar).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` (a failed read of a folder or a backup, a failed backup or plan, "not connected", the session that ended) | status | results and states as above |
    | the same bar, "this backup comes from another domain" | note | `Ad.Note.Attention` under the list, while that backup is selected (8) |
    | progress bar and `StatusText` in the footer ("Backing up…", "N backups in this folder.", "Cancelled") | status, summary | the operations in the line; the count as the summary; the stopped backup as attention |
    | *Create backup* / *Restore…* / *Import…* | – | an action button (`Ad.AccentDialogActionButton`): the Cancel of the backup or the plan it started; *Close* stays the dialog's cancel button and is disabled meanwhile, as before |

  - *Decisions.* *Browse…* stays a button (the picker is a dialog of its own and the listing after it is stopped in the line).
    The folder is remembered after a listing as before (`RememberFolderAsync`, also after a failing folder; unchanged).
    `Error` and `StatusText` keep their sentences for the tests; `Error` no longer holds the other-domain sentence.
    `CancelWorkCommand` has no view and never had one; it is left.
- **The central store window** (`GroupPolicy/CentralStoreUpdateWindow.xaml`; `AdministrativeTemplates/CentralStoreUpdateViewModel`).
  - *What the line says.* At the top, above the target and the sources. **Working:** "Reading the templates…" or "Downloading
    the templates of Microsoft Edge…" with Stop, then "Comparing with the central store…" (a stage; of its own when a
    language or an option changes); the update: "Preparing the update of the central store…", "Backing up the central
    store…" with Stop, and the button that started it is its Cancel. At rest what the comparison found ("To write: 2,
    unchanged: 1, skipped: 0"; it moved from the footer). **Results:** a package that could not be read ("Could not read the
    templates: The file or folder does not exist. Choose another package."; a download or the templates of this computer
    with Retry), a load that was stopped ("Loading the templates was stopped: the package shown was not changed."), a
    backup that could not be written ("The central store was not updated: The backup could not be written to the chosen
    folder; nothing was changed. (IOException)"), an update that was stopped ("The update was stopped: the central store
    was not changed. Press “Update…” to try again."), a backup written when the preview was closed without executing, and
    the end of an update ("Central store updated: written files: 2. … Backup written to …"). **States:** a comparison
    that was stopped or failed leaves no files to show ("Could not compare the templates with the central store: reason.
    Press Retry to try again.") with Retry; the end of the session, or no session ("Not connected to a domain." from the
    start, also while a package is read).
  - *How the old places were sorted* (1 error bar, 1 warning bar, 1 progress bar, and three texts that never were banners).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` (a package that could not be read, a failed comparison, a failed backup) | status | results and states as above |
    | the text `ProgressText` ("Downloading…", "Backing up…", "Comparing…") | status | the operation and its stages |
    | card `ResultBar` (`ResultText`: the backup written, the store updated) | status | the result of the update (5) |
    | progress bar and `SummaryText` in the footer | status, summary | the operation; the summary |
    | warning bar `StoreNote` ("The domain has no central store yet: the update creates it.") | note | `Ad.Note.Attention` over the files, while they are shown |
    | the trust of the package and the skipped files (a glyph and a text in the package card; an expander) | note, never a banner | unchanged |

  - *Decisions.* One holder: loading, comparing and updating never overlap (the sources, the options and the cultures are
    disabled while the window is busy, and a comparison started by an option takes the line by replacing the one before).
    *Update…* is an action button over `IsUpdating` (the plan of the update runs while the window cannot be closed, as before);
    the six sources stay plain buttons - only one can run and the Stop is in the line. "Busy" ends with the generation of the
    load that started it: the old code asked whether its token was cancelled to tell "a newer load took over", which a
    Stop or the end of the session would have answered the same way and left the window busy. `WriteBackup` – the seam that
    replaces the write of the backup in the tests – is public now, because the UI tests hold the backup to see the update
    stopped. The log copies are the keys of the texts (`CentralStore.Loading`, `CentralStore.Downloading`, …) and never an
    address, a file or a package (11).
- **Three dialogs that only collect input** (`GroupPolicy/WmiFilterEditorWindow.xaml`, `GpoCopySettingsWindow.xaml`,
  `NewGpoWindow.xaml`; their view models are unchanged). No line (1). Sorted:

  | Old place | Kind | Now |
  |---|---|---|
  | WMI filter editor: warning bar "The stored queries could not be read; saving replaces them with the queries shown here." | note | `Ad.Note.Attention` above the queries (`UnparsedNote`) |
  | copy window: error bar "More than N source GPOs are checked…" (`TooManySourcesBar`) | note | `Ad.Note.Error` above the list: why OK is disabled |
  | merge window: error bar "These settings block the merge" with their list (`BlockingBar`) | note | `Ad.Note.Error` with the list inside it |
  | merge window: warning bar "Check before OK" with the differences of filtering, WMI filter and links (`WarningsBar`) | note | `Ad.Note.Attention` with the list inside it |
  | the validation texts (the footer of the WMI editor, under the name box of the name dialog), the count in the footer of the copy window | never banners | unchanged |

- **Where this departs from the recipe.**
  - *A third list in the source test.* The recipe knows converted views with a line and converted views without one.
    The base class gave all ten documents their holder at once – with it the status bar stopped speaking for them – so
    the six views of the later units needed the line in this unit, while they still have bars of their own:
    `ViewsWithAStatusLineNotConvertedYet`, with its own rule, exists until the units that convert those views have
    emptied it.
  - *Two view models of a later unit were touched* – one line each: the two editors set `Error` themselves for a GPO
    that is gone; with the private setter they call `ShowUnavailable(L.GroupPolicy.Missing)`.
  - *No `ExecutionStatus` in the documents.* A Group Policy document never executes a plan itself: the change plan
    preview does, and says it in its own line (converted in V2b-1). The document says the step before – the planning
    – and the read after.
  - *A behaviour that was repaired on the way:* a document that lost its session while it was loading stayed
    "loading" until the next connection (the mark in the tab header kept turning). `IsLoading` ends with the session
    now, in the same call that says "not connected" (Dialogs 22).
  - *Left as they were:* the error dialog of *History in Watchdog* (the read before another window opens), the
    toasts of the HTML report and of the copy to the clipboard on the *Settings* tab (an export still answers with a
    toast everywhere), and the dialogs of the tree and of the shell.
  - *Unit 2: the base class got two more touches* – `SummaryDetail` (15) and `RefreshAsync` made virtual (13); nothing
    else of the base, the model or the controls changed.
  - *Unit 2: the cancel of the three tabs moved from the document to the line.* `Cancel()` and `CancelCommand` stay
    (tests and code without a line use them) but they are the Stop of the line now; the separate flag
    (`_cancelRequested`) and the linked source that only it cancelled are gone from the documents. The wording of a
    stopped read is the generic one of the base ("Reading “GPO settings search” was stopped …"), not a sentence per
    document: the title of the tab says what was read.
  - *Unit 2: no `ExecutionStatus`.* As in unit 1, none of the four documents executes a plan; the preview does.
  - *Unit 2: the reads of the three tabs begin before their own code.* The base opens SYSVOL before
    `LoadCoreAsync`; "Reading “…”…" is therefore what the line says for the first moments of every read, until the first
    stage is reported.
  - *Unit 3: the temporary third list is gone.* Both editors moved to `PagesWithAStatusLine` and the list – which was to
    end empty – was removed with its test (`AViewWithAStatusLineThatIsNotConvertedYet_StillHasSomethingToConvert`) and
    the exception in the rule "a view with a line is on the list". The source test has the two lists of the recipe again.
  - *Unit 3: nothing of the base, the model or the controls changed.* The editors use the hooks unit 1 and 2 built
    (`DocumentLastingState`, `UpdateLasting`, `LoadOperation`, `GroupPolicyActions` bound to the holder). New: one L class
    and one pair of strings (the stage of the templates).
  - *Unit 3: the toasts of the editors were sorted and left* (the reasons are in *The two editors*, Decisions) – the one
    place where the brief's rule "a toast that says what the view just did is status" was not followed to the end.
  - *Unit 3: a card became a note.* `DefinitionsBar` was never a banner style, but with a caution background it looked
    like one; it is a note now, as the cards of unit 2 (16). The information note is in the secondary text colour where
    the old information glyph was in the accent colour.
  - *Unit 4: nothing of the base, the model or the controls changed, and no document was touched.* The windows use the
    foundations and the sub-list *The windows*; new are four L classes (`GpoPickerStatus`, `GpoComparisonStatus`,
    `GpoBackupStatus`, `CentralStoreStatus`) and 32 pairs of strings. The things of other units that were touched are
    their windows only: `WriteBackup` of the central store view model became public (the UI tests hold the backup); the
    comparison computes its summary again after a language switch.
  - *Unit 4: a property changed its meaning.* `Error` of the backup window no longer holds "this backup comes from another
    domain" (it is `OtherDomainText`, a note); nothing read it for that.
  - *Unit 4: the line of the backup window is above its title*, not under it (*The windows* 2), and the three windows with
    a plain footer have a footer of buttons only.
  - *Unit 4: "Reading the folder" is waited for, not stopped* (*The windows* 6): the thread of a call that takes no token
    runs on until the share answers.
  - *Unit 4: three views were put on the second list without much to convert* - the name of a GPO had no banner - so that
    the source rule keeps one out of it; the validation texts of the three dialogs were not made notes (they never were
    banners).
- **Tests.** Presentation (run on Linux): `GroupPolicy/GroupPolicyDocumentStatusTests` (26 cases: the summary with the
  selection and the search in both languages, and of every document; the status bar that says nothing about a Group
  Policy tab; the read with Stop – "Stopping…" at once, the stopped read as a state with Refresh – in both languages;
  a failed read with Retry in both languages; a newer read that replaces the running one; not connected as the state,
  "busy" ending with the session, said again after a language switch; a container that is gone in both languages; a
  GPO that is gone with its folder, deleted from the line; SYSVOL and the PDC emulator as notes; a kind of document
  that adds its own state; an operation with the preview and the read after it, in both languages; Stop while the plan
  is made and Retry, in both languages; a plan that cannot be made, in both languages, no dialog; SYSVOL gone when an
  operation needs it; nothing to change, in the line and – for the tree – as a toast; the dialog the tree keeps; the
  log copies without a name, a title, a domain or a server text) and `GroupPolicy/GroupPolicyHealthStatusTests` (9:
  the tab in front and its summary in both languages; its check with Stop in both languages; a failed check with
  Retry in both languages, not the document's error; a failure that stays with its tab and goes with the GPO; the
  note; a check inside the load; the log copies). Unchanged and green: the 401 tests of `GroupPolicy/` that were
  there, among them `GroupPolicyDocumentTests`, `GroupPolicyOperationTests` and `GroupPolicyHealthTabTests`. UI
  (Windows CI only, written without a screen): `GroupPolicyObjectsStatusUiTests` (five tests: the line with the
  summary and the selection and the empty status bar; a held read with Stop; a failed read; the two notes with the
  link; every Group Policy tab with its line and notes), `GroupPolicyScopeStatusUiTests` (three: the summary; a plan
  that cannot be made, then Retry into the real preview window; a container that is gone),
  `GroupPolicyObjectStatusUiTests` (four: the *Status* tab in front; its check with Stop; a GPO that is gone with the
  action of the line through the real preview window; the notes without SYSVOL), `WmiFiltersStatusUiTests` (two).
  Adapted where the place of a message changed: `GroupPolicyWindowTests` (the button of the orphaned folder is read
  from the line). The four views are on `PagesWithAStatusLine`, the notes control and the health panel on
  `ConvertedViewsWithoutAStatusLine`. The source rules – the three lists, the closed set of Fluent brush keys (28,
  unchanged), "every `StaticResource` key resolves, before its first use" – were proven on Linux with a throwaway
  script that repeats the expressions of the tests.
- **Tests of unit 2.** Presentation (run on Linux): `GroupPolicy/Search/GpoSettingsSearchStatusTests` (17 cases),
  `GroupPolicy/Cleanup/GpoCleanupStatusTests` (20), `GroupPolicy/Diagnostics/GroupPolicyDiagnosticsStatusTests` (18) and
  `GroupPolicy/GroupPolicyModelingStatusTests` (11): the summary with the as-of and the selection, the filters, before
  the first read – in both languages and after a language switch without a read; the read with its stages and the
  part done, the buttons that know which read runs (the first, the other kind, the one after it); Stop – "Stopping…"
  observed in the recorded sequence for the two tabs whose wait ends at once (the search, the cleanup) and held for the
  two whose call cannot be cancelled (the diagnostics, the model) –, the stopped read as the state with Refresh, the
  results of before that stay, and that only the action of the line repeats it (a change in the window after a stop
  reads nothing and marks the tab); a failed read with Retry that reads the same thing again, in both languages;
  "Group Policy changed …" with its action in both languages, said again after a language switch, behind "not
  connected"; the notes (a folder that was not read, a quick check, a target that does not exist, SYSVOL not available)
  that say nothing to the line; the refusals of the guarded delete in the line of the cleanup and in a dialog for the
  tree; a repair of the diagnostics that cannot be planned, with Retry; the inputs that changed, which the line does
  not say; the log copies without a name, a title, a domain, a stage or a server text. The tests record every change of
  `Status.Current` (`StatusRecorder`, with the part done of an operation), hold a call of the domain controller
  (`HeldDirectoryCall`) and script the stages and the end of an index read (`ScriptedIndexService`, over the real
  service); they are in `GroupPolicy/GroupPolicyStatusTestSupport.cs`, and `K7Harness.CreateAsync` takes a culture and a
  status log now. Unchanged and green: the 431 tests of `GroupPolicy/` that were there – among them the cancel tests of
  the three tabs, whose "cancelled" sentence is still said to a host without a line. UI (Windows CI only, written
  without a screen): `GpoSettingsSearchStatusUiTests` (5), `GpoCleanupStatusUiTests` (5),
  `GroupPolicyDiagnosticsStatusUiTests` (4) and `GroupPolicyModelingStatusUiTests` (4): the line under the toolbar and
  its summary, no element of the old bars, the action buttons at rest and while a held read runs, Stop on the button and
  in the line, the stopped state with its action and the growth of the line, a failed read with Retry, the state of
  "Group Policy changed" with its action, the notes with the note style and their place. Adapted where the place of a
  message changed: `GpoSettingsSearchUiTests` (the result is read from the line), `GpoCleanupUiTests` and
  `GroupPolicyDiagnosticsUiTests` (no stale bar and no progress panel any more). The four views moved from the
  temporary list to `PagesWithAStatusLine`; only the two editors are left on `ViewsWithAStatusLineNotConvertedYet`.
  The source rules were proven on Linux with a throwaway script that repeats the expressions of the tests (the three
  lists, "every `StaticResource` key resolves, before its first use" for the four files). The first Windows run found
  one wrong assumption of a test, none of a view: a check that ends and takes the note of the quick check with it moves
  the content under the line up by the height of that note, so "the chips did not move" holds only while the notes do
  not change (fixed in `GroupPolicyDiagnosticsStatusUiTests`); the other seventeen UI tests were green unseen.
- **Tests of unit 3.** Presentation (run on Linux): `GroupPolicy/AdministrativeTemplatesEditorStatusTests` (14 cases) and
  `GroupPolicy/SecuritySettingsEditorStatusTests` (14): the summary with the changed settings in both languages; the read
  as the operation of the document with Stop, "Stopping…" at once, the held call (a listing of the central store, a read
  of the first security file) and the recorded sequence, the stage of the templates, the stopped state with Refresh in
  both languages and that a stopped read does not lose the edits; a file that changed under the edits as the state of
  the document, in both languages, with Discard as its action – pressed in the line it asks, shows the current file
  and gives the summary back –, that it follows the language at once while the read still runs, and that "not
  connected" comes before it; the notes that say nothing to the line (no central store, an unreadable file, the audit
  sentence); the plan of a save in the line, the summary while the preview is open and the read after it, in both
  languages; a save whose plan cannot be made, with the edits kept, `IsSaving` ended and Retry that previews and writes;
  the log copies without a name, a title, a server or a message. Two of the hooks were mutated to see the tests fail
  (the stage; the lasting state). Unchanged and green: the 497 tests of `GroupPolicy/` that were there – among them
  `AdministrativeTemplatesEditorTests`, `SecuritySettingsEditorTests` and `GroupPolicyEditingComfortTests`; 3,301 in the
  project with these. UI (Windows CI only, written without a screen): `AdministrativeTemplatesStatusUiTests` (4) and
  `SecuritySettingsStatusUiTests` (4): the line under the header with the summary, the changed count and no old
  feedback; the notes with the note style and their places (the templates note with its buttons, the file note, the
  audit note on its page, only while that page is shown); the conflict with its action, the growth of the line and that
  the content under it moved by as much, and Discard through the real confirmation window; a save whose plan cannot
  be made, with Retry into the real change plan preview, executed. In the templates test "no templates at all" does
  not depend on the runner: the local templates are replaced by none. `GroupPolicyObjectsStatusUiTests` (every Group
  Policy tab has its line and notes and the status bar says nothing) has the security editor now. The two views are on
  `PagesWithAStatusLine`; the source rules – the two lists, the closed set of Fluent brush keys (28, unchanged although
  the card gave up three uses), "every `StaticResource` key resolves, before its first use" – were proven on Linux with
  a throwaway script that repeats the expressions of the tests (and the script was shown to catch an old bar and a key
  that does not exist). The first Windows run found one wrong assumption of a test, none of a view: a test that ends
  with unsaved edits makes the main window ask before it closes, and that question (a message window) outlived the test
  – the next test of the shared application found a window it had not opened (the failure showed in the test that
  asserts "no dialog"). The first test of each editor now discards its edit through the real confirmation, as its last
  step, and asserts that the changed count goes with it; a UI test of an editor must not end with unsaved edits. The
  other seven UI tests of the unit were green unseen.
- **Tests of unit 4.** Presentation (run on Linux): `GroupPolicy/GpoPickerStatusTests` (12 cases: the read with Stop and the
  count in both languages, the filter and the language switch, "Stopping…" at once with a held call, the stopped read as
  the state with Retry, a failed read with the reason, the code and Retry in both languages, no session from the first
  moment, the end of the session during the read, the log copies), `GroupPolicy/GpoComparisonStatusTests` (17: the
  comparison with the templates as its stage, no stage without registry settings, Stop before the rows and during the
  templates and during a refresh over earlier rows in both languages, a failure with Retry, SYSVOL as a note and no status, an export that failed and one that
  worked, no session, the end of the session during the read, the language switch, the log copies),
  `GroupPolicy/GpoBackupStatusTests` (31: the backup with its part done and nothing said when it ended well, Stop with the
  count written and the action, a failure of one GPO, SYSVOL not available, the read of the folder with its count in both
  languages, an empty folder, a folder that hangs - a FIFO - stopped and read again, a folder that cannot be read, the
  selected backup in front of the window and stopped on a FIFO and failed on a damaged file with Retry, the note of
  another domain, the plan of a restore with Stop that holds and Retry into the preview, a plan that could not be made,
  no session, the end of the session during the backup, the log copies) and `GroupPolicy/CentralStoreUpdateStatusTests`
  (23: a load with the comparison as its stage, a download held and stopped and offered again, a failed download, a file that
  does not exist, a comparison that failed and one that was stopped with Retry, the update with the line handed back for
  the preview and the result after the new comparison, an update stopped and offered again, a backup that could not be
  written, a preview closed without executing, no session, the end of the session during a download, the language switch
  and the log copies). Each hook was mutated once to see the tests fail (the stage, the lasting state, the part done, the
  holder in front, Stop that holds, the handing back of the line, the result after the comparison); unchanged and green: the
  530 tests of `GroupPolicy/` that were there - among them the 22 of `CentralStoreUpdateTests` and the ones of
  `GroupPolicyBackupAndCompareTests`; 3,384 in the project with these. UI
  (Windows CI only, written without a screen): `GpoPickerStatusUiTests` (3), `GpoComparisonStatusUiTests` (4),
  `GpoBackupStatusUiTests` (3), `CentralStoreUpdateStatusUiTests` (3) and `GroupPolicyDialogNotesUiTests` (3): the line at
  the top or under the header, its summary, the action button that is also the Cancel (`ActionButtonState`), a held read
  or backup with Stop in the line and in the button, the stopped state with its action and the growth of the line (the
  content under it moved by as much), a failure with Retry, the notes with the note style and their places, no element of
  the old banners and no progress bar, and for the three dialogs without a line: none of it, and the notes. Adapted where
  the place of a message changed: `GroupPolicyWindowTests` (the picker's count in the line, the summary of the comparison
  in the line after the templates were read, the central store window without a connection - an error in the line -, its
  summary and the result of the update in the line). The four windows are on `PagesWithAStatusLine`, the three dialogs on
  `ConvertedViewsWithoutAStatusLine`. The source rules – the two lists, the closed set of Fluent brush keys (28,
  unchanged), "every `StaticResource` key resolves, before its first use", the keys of the localization – were proven on
  Linux with a throwaway script that repeats the expressions of the tests (and the script was shown to catch an old bar).
  The first Windows run found one wrong assumption of the tests, none of a view: GPMC's `manifest.xml` is written with the
  hidden attribute, and Windows refuses to overwrite a hidden file, so the tests that make a backup come from another
  domain (one Presentation test, one UI test) clear the attribute first. The other fifteen UI tests of the unit and the
  adapted ones were green unseen.
- **After unit 1 of V2b-3** (counted in the XAML, comments excluded): 16 uses of `Ad.ErrorBar` and 33 of
  `Ad.WarningBar` in 30 files and 31 progress bars in 28 files – 37 view files with at least one of the three; in
  `GroupPolicy/` 6 + 14 and 7 in 12 files. 23 views with a status line, 6 with a line and bars of their own, 4 converted
  views without a line. Unit 1 took 1 error bar (status), 3 warning bars (1 note – SYSVOL –, 1 status – the action of a
  GPO that is gone –, and one that was both: a failed check is status, an incomplete one a note) and 1 progress bar
  (status). Left behind on purpose, for the list of *After V2b-1*: no longer shown but read by tests or by the view
  models themselves – `HasError` of the documents, `HealthStatusText` and `IsCheckingHealth` of a GPO –, and
  `StatusText` / `SelectionText` of the documents, which only the shell reads now (for hosts without a line); no string
  and no command lost its last user.
- **After unit 2 of V2b-3** (counted in the XAML, comments excluded): 16 uses of `Ad.ErrorBar` and 28 of
  `Ad.WarningBar` in 26 files and 28 progress bars in 25 files – 33 view files with at least one of the three; in
  `GroupPolicy/` 6 + 9 and 4 in 8 files. 27 views with a status line, 2 with a line and bars of their own (the two
  editors), 4 converted views without a line. Unit 2 took 5 warning bars (3 status – "Group Policy changed" in each of the
  search, the cleanup and the diagnostics –, and 2 notes – the cap of the search, what the model could not use) and 3
  progress bars (status); the three cards of the same kind that never were a banner (what the index, the analysis and
  the check could not see) got the note look as well. Left behind on
  purpose, for the list of *After V2b-1*: no longer shown but read by tests or set by the view models themselves –
  `ProgressValue` and `IsProgressIndeterminate` of the search, the cleanup and the diagnostics, `Cancel()` and
  `CancelCommand` of the three (kept, 11), and `SummaryText` of the model (the line says it; the property feeds the
  summary and the tests). No string lost its last user in that unit. The error bars and warning bars that are left in
  the area belong to the windows of unit 4 and the two editors of unit 3.
- **After unit 3 of V2b-3** (counted in the XAML, comments excluded): 16 uses of `Ad.ErrorBar` and 23 of
  `Ad.WarningBar` in 24 files and 28 progress bars in 25 files – 31 view files with at least one of the three; in
  `GroupPolicy/` 6 + 4 and 4 in 6 files, all of them in the six windows of unit 4. 29 views with a status line, none with
  a line and bars of its own, 4 converted views without a line. Unit 3 took 5 warning bars (2 status – the conflict
  under the edits in each editor –, and 3 notes – the unreadable file in each editor, the audit sentence) and no
  progress bar; the card of the templates, which never was a banner style, got the note look as well. Left behind on
  purpose, for the list of *After V2b-1*: `ConflictNote` of the two editors, which the views do not bind any more (it is
  the text of the state in the line). No string and no command lost its last user. The source test lost its temporary
  list.
- **After unit 4 of V2b-3 - and so after the increment** (counted in the XAML, comments excluded): 10 uses of
  `Ad.ErrorBar` and 19 of `Ad.WarningBar` in 18 files and 24 progress bars in 21 files – 25 view files with at least one
  of the three (one of the progress bars is the row of an activity in the main window, which stays); `GroupPolicy/` has
  none. 33 views with a status line, none with a line and bars of its own, 7 converted views without a line. Unit 4 took
  6 error bars (4 status – the picker, the comparison, the backup window and the central store window –, and 2 notes –
  too many source GPOs and what blocks a merge), 4 warning bars (all notes – SYSVOL in the comparison, the store that does
  not exist yet, the differences of a merge, the WMI queries that could not be read) and 4 progress bars (status); the
  cards and texts that never were banners went the same way (the result card, the progress text, the summary and status
  texts of three footers), and the sentence "from another domain" became a note. The whole of V2b-3 took 7 error bars (5
  status, 2 notes), 17 warning bars (6 status, 10 notes, 1 both) and 8 progress bars (all status): 24 banners. Left behind
  on purpose, for the list of *After V2b-1*: no longer shown but read by tests or by the view models themselves – `Error`
  of the four windows with a line (the sentence each window always had; the comparison also sets it for a failed export),
  `IsLoading` of the picker and the comparison, `SummaryText` of the comparison and of the central store window,
  `StatusText` of the backup window (with the strings `GroupPolicy.BackupsFound`, `GroupPolicy.NoBackups`,
  `GroupPolicy.BackupRunning` that feed it), `ProgressText`, `BusyText`, `IsWorking` and `ResultText` of the central store
  window, and `CancelWorkCommand` of the backup window, which never had a view. No string lost its last user.
- **Not seen, to confirm on Windows.** Nothing of this unit was seen on a screen.
  - *Every Group Policy tab is 34 px lower* under its header or toolbar – the line and its 6 px – where the shared bars
    took no room at rest. On the tabs that are not converted yet the line stands above their own bars.
  - *The notes.* "SYSVOL unavailable on dc01: … – changes that write SYSVOL are disabled." as a glyph and a sentence
    where a caution bar was: it is the reason why half the toolbar is disabled, and quieter than before. The PDC note in
    the secondary text colour with *Change Domain Controller…* as a link after it (underlined, accent colour): the
    first link outside the status line – its base line against the sentence (the sentence starts 3 px lower than the
    24 px high link), and how it wraps in a narrow window (the sentence wraps, the link stays at the right).
  - *"Reading “…”…" at every reload* – after each executed operation, a change of the domain controller, a language
    switch – flashes with a fast domain controller (the open question of a delay before "Working"); "…: preparing the
    changes…" flashes before every preview.
  - *Long results.* "Copy GPO “…” – nothing was changed: SYSVOL unavailable on dc01.contoso.test: the share cannot be
    reached – changes that write SYSVOL are disabled. Press Retry to try again." is some 170 characters: it wraps to
    two or three lines, depending on the width the tree and the details pane leave to the tab, and the content of the
    tab moves down with it.
  - *The* Status *tab of a GPO* without its progress bar and text: the hint and *Check again* in one row, the note
    above the results; the stages of the check in the line change quickly.
  - *Delete the remaining folder* as a link in an error line, where a button in a caution bar was.
  - Two README pictures show a Group Policy tab and change with this unit (the line under the header):
    `23-group-policy.png` – the Group Policy of a unit – and `29-administrative-templates.png`, an editor of a later
    unit, which has the line already.

- **Not seen, to confirm on Windows (unit 2).** Nothing of unit 2 was seen on a screen either.
  - *The four tabs are about 26 px higher than after unit 1* (the line of the as-of text or of the progress under the
    toolbar is gone; the bold summary of the model, about 24 px) – so about 8 px lower than before V2b-3 (the 34 px of the
    line, less the row). The README pictures `31-group-policy-model.png` and `35-gpo-cleanup.png` are made by the
    screenshot tests but are not committed or shown in the READMEs.
  - *The header of the search, the cleanup and the diagnostics.* Two action buttons and two export buttons next to the
    title in a narrow tab (the Polish "Odczytaj wszystko ponownie" grows to the width of "Zatrzymywanie…"; the title is
    trimmed first); the ring in the button of the read that runs.
  - *The summary line is long.* "Znalezione ustawienia: 1 (wystąpienia: 2) · Przeszukiwane obiekty GPO: 7 · odczyt
    14.11.2026 10:44 · Stacje robocze – Zasady haseł – Minimalna długość hasła" does not fit a tab of 900 px: the
    selection is cut with an ellipsis (the tool tip has it all). Whether the as-of text belongs there, or the line should
    keep the count and the selection only.
  - *The notes.* A list of sentences in one caution note (the glyph once, the sentences under one another, with the tool
    tips of the search) where a card with the information glyph stood: whether the caution glyph is too loud for "the
    quick check does not cover …", which is a statement of the depth the operator chose.
  - *The ring on "Odśwież"* at every reload that reads nothing (a change in the window marks the tab stale) – with a fast
    domain controller it flashes, with a slow SYSVOL logon it is on for the time of the logon.
  - *Long results.* "Delete GPO “…” – nothing was changed: the GPO could not be read to compare it with the analysis: …
    (ServerUnavailable)" in the line of the cleanup wraps to two or three lines; the chips move down with it.

- **Not seen, to confirm on Windows (unit 3).** Nothing of unit 3 was seen on a screen either.
  - *The templates note.* A glyph and a sentence in the secondary text colour where a card stood (with a central store
    the note is information, without one attention – on a domain without a central store the attention note is there
    in every editor, for the whole session, and quieter than the caution card it replaces); the two buttons at its
    right, in line with the middle of the sentence, and a sentence that wraps in a narrow tab next to them; the
    expander of the problems under the note, now flush with the left edge instead of inside a card. The README picture
    `29-administrative-templates.png` changes (the line and the note).
  - *The conflict in the line.* A sentence of about 170 characters wraps to two or three lines in a tab of 900 px, and
    the tree and the lists move down with it; *Discard changes* as the link of the line next to the same words as the
    toolbar button, and the confirmation window it opens.
  - *"Reading the ADMX and ADML files…"* is a stage after the GPO was read; with the templates already in memory it is
    on the line for the moment the next await takes (it flashes – the open question of a delay before "Working").
  - *The header* still has the accent text with the number of changed settings under the title, and the line says it
    too; whether one of the two should go is for the owner (the unsaved changes are not part of this increment).
  - *Open for the owner:* whether the answers to a press in a row (the toasts of *The two editors*) should be said in
    the line, cleared by the next edit; whether "no central store" should offer *Update templates…* as an action of a
    state that lasts, at the price of the summary.

- **Not seen, to confirm on Windows (unit 4).** Nothing of unit 4 was seen on a screen either – nor of the four units
  together, in which the area has one look for the first time.
  - *The windows are 36 px higher by default* (picker 556, backup window 636, comparison and central store 716 – still
    below the 728 px a 768 px display leaves above the task bar); their minimum heights are the old ones, so at the minimum
    the list of the comparison, of the backups and of the files has 34 px less, as in every window of the sweep.
  - *The footers.* The backup window, the comparison and the central store window have buttons only, right-aligned, where a
    progress bar and a text stood on the left; the width of *Utwórz kopię* / *Przywróć…* / *Importuj…* / *Dalej* and of
    *Zaktualizuj…* (as wide as "Zatrzymywanie…" needs) next to *Zamknij*; whether an empty left half of a footer reads as
    a gap.
  - *The line of the backup window above its title* (the title repeats the window's own): whether it reads better under
    the title, as in the comparison, where it follows the header row.
  - *The backup that runs.* "Backing up the GPOs: 1 of 3…" with the part done in a 16 px ring is the only progress of a
    backup of many GPOs, next to the activity in the list of background operations; the button that started it saying
    "Cancel" while the window cannot be closed; a stopped backup's sentence with the label of the button that backs up
    ("Press “Back Up” to try again.", in the Polish "Wybierz „Utwórz kopię”…").
  - *Long results.* "Central store updated: written files: 2. Open editors read the templates again. Backup written to
    \\pliki\Kopie\Magazyn centralny\PolicyDefinitions-dc01-…" is some 200 characters and wraps to three lines at 900 px
    (the path is cut in the third); "Could not back up 1 of 3 GPOs: reason. (code)" and a failed plan in the backup window
    wrap likewise, and the fields move down with them.
  - *The notes of the three dialogs.* The merge window's two notes - a list of settings under one error glyph, a list of
    differences under one caution glyph - where two coloured bars stood; the single sentence of the copy window and of the
    WMI editor; whether a note without a fill is enough for "why OK is disabled" there.
  - *The central store window.* "Not connected to a domain." as an error from the first moment when the window is opened
    without a connection (it used to open quietly, and its sources still work); the store note over the files without a
    fill; the summary in the line where the footer said it, with the info glyph.
  - *"Reading …" at every opening.* The picker, the comparison and the backup window flash their working state with a fast
    domain controller (the open question of a delay before "Working"); the comparison's stage of the templates flashes
    when the definitions are cached.
  - *The README picture* `30-central-store-update.png` is made by the screenshot test but is not committed or shown in the
    READMEs; with the line it has 34 px less for the list in the fixed client size of 900 × 680.

**V2b-4 as built (the remaining areas).** The fifth increment of V2 converts the areas that are left after Group Policy
- bulk creation, computers, certificates, event logs, readiness, correspondence and the Watchdog views - in five units,
done one after another. Unit 1 converted the bulk creation wizard (`BulkCreate/` with its view models in
`Presentation/BulkCreate`). Unit 2 converted the computer windows (`Computers/`: the review, the remote actions window and
the restart dialog), the three event log documents (`EventLogs/`) and the readiness tab (`Readiness/`). Unit 3 converted the
four PKI documents (`Pki/` with their view models in `Presentation/Pki`); the certificate tabs of the property sheet were
converted with it in V2b-1. Unit 4 converted the Watchdog tab (`Views/Watchdog/` with its view models in
`Presentation/Watchdog`). Unit 5 converted the Korespondencja window (`Correspondence/` with its view models in
`Presentation/Correspondence`) and closed the sweep. The foundations are
those of *V2b-1 as built*, the *Dialogs* of *V2b-2 as built* and the decisions of *V2b-3 as built*; nothing was added to the
controls, and the model got three factories (`ResultCode.Remote` in unit 2, the kind of a failed remote action,
`ResultCode.Watchdog` in unit 4, the kind of a failed call of the service, and `ResultCode.Outlook` in unit 5, the kind of a
failed call of classic Outlook). The block reads
in this order: *Areas* (what each area decided about its line, its notes and its states that last - the instructions for
whoever converts a view next), the wizard, the unit-2 sections (the computer review, the remote actions window and the
restart dialog, the event log documents, the readiness tab), the unit-3 section (the PKI documents), the unit-4 section (the
Watchdog tab), the unit-5 section (the Korespondencja window), the tests (by unit), what is left (the final count, then
what each unit left behind on purpose), and what nobody has seen on a screen yet (by unit). What V2b-5 still has to do is in
*What V2b has to do* in the block of V2a.

- **Areas** (what each area decided; the next units follow it and add their own).
  1. *Bulk creation (unit 1) - a flow that another window hosts has the line of its host.* The wizard is the first step
     of the batch editor in create mode, so it has **no `StatusLine`**: the one line of that window says what the page of
     the step shown says (`BatchEditorViewModel.SourceStatus` is the wizard's holder, the place V2b-1 prepared), and none
     of the six views of the folder is on `PagesWithAStatusLine` - they are on `ConvertedViewsWithoutAStatusLine`, and the
     UI tests look through the whole window for the old banners. Before a line goes into a view, ask who hosts the view:
     a view that is only a part of a converted window says its status through that window. *Notes:* what belongs to the
     table or to the fields stays next to them with the look of a note - the reading warnings, the question of a repeated
     paste (with its three buttons: a status has one action), why *Dalej* is not possible, the problems that concern every
     row, the notes under the results. *States that last:* only the check page has them (why there are no rows, stopped,
     out of date), computed in one method from the facts; the end of the session is the window's (`SessionEndedStatus`),
     the wizard adds none.
  2. *Computers (unit 2) - a window that does several things one after another has one holder and a flag per button.* The
     review's check, its read of the exact last logon, its finding of the sites and its ping are operations of one holder
     that replace one another (the commands of the second kind refuse while one runs, and a new check replaces a read);
     each ends as a result of its own and the four buttons that start them are action buttons, each with its own flag over
     the one holder (`IsRunning`, `IsReadingExact`, `IsResolvingSites`, `IsPinging`) - the rule of V2b-4 unit 1 for two
     buttons over one operation, with four. The remote actions window is the same with six (`IsReadingStatus` ...
     `IsAbortingRestart`). *States that last:* the end of the session, a window opened without one, a check that failed (the
     list is empty because of it), and in the remote window that the computer has no DNS name or that remote actions are not
     available - states of the window, not dialogs on top of it. An action that changes something (a policy refresh, a
     restart) is never repeated by a link in the line; a read is (Retry). *Notes:* the age of the release catalogue, which
     is true for as long as the end-of-support results are shown and would otherwise hide the summary for months. A window
     that only asks (the restart dialog) has a note and no line (*Dialogs* 11).
  3. *Event logs (unit 2) - a family of documents shares the holder of its base class, as the Group Policy documents do.*
     `EventLogDocumentBase` owns `Status`, so the reader, the lockout investigation and the failed logons say the same
     things in the same words. The run is the operation of the tab, its stages are one message of it (the key of the
     operation stays; "sources: 2 of 3, events: 1,204" is a new count, not a new message), and what it leaves is said by
     the same four results in all three documents (the summary, an attention that names the sources that were not read,
     an attention with Refresh for a run that was stopped, an error with Retry for one that failed). *Coverage* was sorted
     once for the three: the state of each source stays in its row of the panel "Coverage and access" (the progress and the
     state of a row), *which sources the run could not read* is the answer of the run and is said by the line, and the
     findings about how far back a log reaches stay in the row and in the notes of the analyses. *States that last:* not
     connected, the window cannot read event logs (nothing could be read, and its summary was empty anyway), and - in the
     lockout investigation - that the object is no account.
  4. *Readiness (unit 2) - what lasts is computed from the facts, and the rows keep their results.* The checks are the
     operation of the tab with the area they are at and how far (`PageProgress.Of(done, total)`), the result of each check
     stays in its row, the summary is what the last check found, then when and where it was made. States that last: not
     connected, the connection changed after the shown check, the shown check was stopped - the same action, "Check
     again", for the last two.
  5. *Certificates (unit 3) - a family of documents shares the holder of its base class, and a tab with an operation of its
     own has a holder in front.* `PkiDocumentBase` owns `Status` for the overview, the templates, the readiness report and
     the security review, as `GroupPolicyDocumentBase` and `EventLogDocumentBase` do: the load is the operation of the tab,
     its stages are one message (the key of the operation stays, the count is new), and what lasts is computed in one
     method - not connected, the read failed (Retry), the read was stopped (the action is the text of the document's own
     button: *Odśwież*, *Sprawdź ponownie*, *Odczytaj ponownie*) and what a kind of document adds. The two checks that touch
     the network - the HTTP addresses and the probe of the domain controllers - are operations of the *tab* that starts them:
     they can run while the document is read, so the overview gives its health tab and its LDAPS tab a holder each, in
     front while the tab is shown (foundations 3 and 4); the readiness report has one holder and refuses the probe while it
     reads. *Notes:* everything that is true of the content while it is shown - what the read observed, a CA outside NTAuth,
     rights that were not evaluated, the scope of the review, what its check could not read, what the report is, why the
     HTTP rows are unknown. *Summary detail:* when and from where the content was read, and how many accounts were counted,
     are the detail of the summary (V2b-3 *Group Policy documents* 15), not a text of their own.
  6. *Watchdog (unit 4) - one document that is a tab of pages: one line for the tab, a holder for every page that has an
     operation of its own, the page in front, and a card that says which service and as whom.* The tab owns the holder the one
     `c:StatusLine` is bound to; the connection attempt is its operation and what lasts while the connection does not work (an
     error with Retry or "Sprawdź certyfikat…", no address, interrupted live updates) is its state, computed in one method from
     the facts. Every page that reads or writes the service owns a holder and the tab puts the page that is shown in front
     (foundation 3): the feed (with the details pane in front of it), the configuration (in front of the four tabs that edit it -
     and the digest, the rules and the channels, which have operations of their own that can overlap a save, each in front of the
     configuration's), and the status tab. The service is the thing a Watchdog page talks to: a write to it (a save, an
     acknowledgement, "Wyślij teraz", the retry of a dead delivery) has no Stop and is repeated by no link, a read has Stop and
     Retry. *Notes:* what is true of a part of the content for as long as it is shown - the mode of rollout, why the editors are
     read only, the findings of the service, the hint of a signing secret that is shown once, the hints of a delivery test, why
     the details of an event could not be read.
  7. *Correspondence (unit 5) - a window of tabs of which only some talk to the directory: one line for the window, a holder
     for every page that says something of its own, and a state that lasts only on the pages it is true for.* The window is
     tabs (the mailbox owners, the recipients, the message, the preview, the send and the responses), so it has one line and
     the page of the tab that is shown is in front (foundation 3). The recipients, mailbox owners, preview, send and responses
     pages own a holder; the message page has none - what it says about a field is a note, and its templates, exports and
     imports answer with a toast or a dialog as before. *What lasts is told by who it is true for.* That the session of the
     window ended is said by the two pages that read the directory, not by the window: sending through Outlook goes on, and a
     window's own error that cannot be closed - `SessionEndedStatus`, the first condition of every other window - makes
     "nothing a page says matters" (foundation 3), so it would have covered a send that runs. That Outlook is not available
     is said by the send and the responses pages, with *Connect to Outlook* as the action where connecting again can help;
     the footer keeps the state of the connection as an indicator, like the connection in the main window's status bar. That
     the journal of the addresses sent to cannot be used is the window's own - set at its first moment and never later: a
     state of the window that is set after a page began an operation would be what was said last, and be shown in its place.
     *Operations:* the send run, the two reads of Sent Items, the search for the owners and the read of the owners that are
     added, the preview from Outlook, the read of the directory and the reading of files - each with Stop, and the buttons
     that start the run, the search, the two reads and the preview are action buttons with a flag of their own over the holder
     of their page. *Notes:* what pasting left out, what is wrong with the chosen signature, the findings of the preview and
     where it came from.

- **The bulk creation wizard** (`BulkCreate/BulkCreateView.xaml` hosting `BulkSourceView`, `BulkColumnsView`,
  `BulkRulesView` and `BulkCheckView`, and `BulkResultsView` in the results step; `BulkCreateViewModel` with `Source`,
  `Columns`, `Rules`, `Check`).
  - *What the line says.* One line - under the step strip of the batch editor, 8 px in from the window edge, over the wizard
    header - and it follows the step of the wizard that is shown (two `Front`s: the window, the wizard, the page).
    **Dane:** at rest the summary of the table ("Wiersze: 120, kolumny: 7 - nagłówki rozpoznane"); **working** "Reading
    the pasted table…" and "Reading “users.xlsx”…" with Stop - the thread that parses goes on to its end, the page gets
    control back at once and drops what it read -, "Updating the table…" (a header choice, repeated rows appended or
    replacing the table) without Stop; **attention** for an answer that came to nothing - "Nothing was pasted: The
    clipboard holds no table…", "The header row was not changed: Row 99 is not in the pasted range…", "The column was not
    added: …", a file without data rows - and **error** for a file that could not be used ("“users.xlsx” was not loaded:
    The file is larger than 50 MB. Choose another file.", the reason of the system with its kind as the code); a stopped
    read - "Reading the pasted table was stopped: the table was not changed. Press “Paste” to try again." / "…Press “Load
    file…” to load a file again." - with the button that started it as the action. The table that was loaded before always
    stays. **Kolumny:** nothing (the page has a note only). **Nazwy i opcje:** the answers to the profile commands as results -
    done: "Profile “Staff” saved. Remembered columns: …", deleted, default of the container or not; attention: "The profile was
    not saved: Type a name for the profile.", "The default profile was not changed: …"; the account that can join computers
    - attention "…was not changed: The chosen user or group could not be read." or error with the reason and the code
    (V2b-2 Dialogs 10). **Sprawdzenie:** at rest the summary of the check; **working** "Checking the rows…" and then "Checking
    the rows: 40 of 120…" with the part done in the ring and Stop; as the state of the page - **error** "Could not check the
    rows: reason. Press Retry to try again. (code)" with Retry, "The rows were not checked: …" (the earlier steps are
    incomplete, no session) without an action, **attention** "Checking the rows was stopped: the results may not match the
    data and your changes. Press “Check again” to check again." and "The data, the rules or the created accounts changed
    since this check: the rows may be out of date. Press “Check again” to check again." with *Check again*. After the
    wizard - preview, execution, results - the line is the window's own (V2b-1), and when the operator goes *Back* the page
    of the step it left is in front again and says what it said.
  - *How the old places were sorted* (3 error bars, 5 warning bars, 2 progress bars, and the sentences that said the same).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` of *Dane* (a paste or a file that failed, a header row out of range, too many columns) | status | what was skipped or could not be read, in the line; the property keeps its sentence |
    | progress bar next to the buttons of *Dane* (`IsBusy`) | status | the read as an operation with Stop; the rebuild of the table as one without |
    | `Summary` right of the header choice; `Summary` of *Sprawdzenie* | summary | `SetIdle` of the page; the text blocks are gone |
    | warning bar "these rows are already pasted" with *Dołącz mimo to / Zastąp / Anuluj* | note | `Ad.Note.Attention` with its three buttons beside it (V2b-2 Dialogs 13) |
    | warning bar of the reading warnings (rows and columns of the sheet) | note | `Ad.Note.Attention` above the table: true for as long as that table is shown |
    | `ProfileStatus` under the profile card; `OptionsError` under the joiner | status | the answer to a press, in the line; the properties keep the sentences |
    | error bar `Error` of *Nazwy i opcje* ("why *Dalej* is not possible") | note | `Ad.Note.Error` above the sections |
    | `MappingError` of *Kolumny* (a glyph and a text by hand) | note | `Ad.Note.Attention`: the same look, one style |
    | error bar `Error` of *Sprawdzenie* (failed, incomplete input, no session) | status | the state of the check page, with Retry where a retry can help |
    | warning bar of the stale grid (no action) | status | the state of the check page, with *Check again* |
    | `ComputingPanel`: progress bar with the known count, "Checking rows: 12 of 120", *Zatrzymaj* | status | the check in the line with the part done and Stop; the button is gone |
    | *Sprawdź ponownie*, *Numeruj od nowa* | - | action buttons, each the Cancel of the check it started |
    | warning bar of the problems that concern every row | note | `Ad.Note.Attention` above the grid (the findings of a check) |
    | one warning bar per note of the results (canary, replication, accounts left disabled, the forest UPN hint) | note | one `Ad.Note.Attention` per note, above the accounts: how the results are to be read |
    | the scope of the check, the hints, the first free number, pattern errors, the warnings about suffixes and groups | note / hint | unchanged |

  - *Decisions.*
    - **The line of the host, through two levels.** `BulkCreateViewModel` is `IHasPageStatus` with a holder that says
      nothing of its own and has the page of the step in front (`Step` → `Front`); the batch editor's `SourceStatus` is
      that holder (`??` after the targets and the CSV import), and the window already put its source in front while the
      first step is shown. The pages that have something to say - source, rules, check - own a holder, like the CSV
      import; *Kolumny* has none (a note is not a status, V2b-1 foundation 1). Nothing new in the model: `Front` already
      chained.
    - **The end of the session is the window's.** Its own error that cannot be closed covers every page, so the wizard
      adds no state of its own; the wizard ends what runs *after* the window said it - `BatchEditorViewModel.OnSessionEnded`
      calls `BulkCreate.OnSessionEnded()` after `Status.SetLasting(SessionEndedStatus)` (V2b-2 Dialogs 22), and the check
      disposes its operation there - so the line goes from "working" straight to the end of the session. The second change
      the shared view model needed besides `SourceStatus`; the order of the other lines is unchanged.
    - **Stop on a read that nothing can interrupt.** Pasting and loading a file parse on the thread pool; Stop is
      `Task.Run(…).WaitAsync(token)`: the line says "Stopping…" and the page is free at once (`IsBusy` ends), the thread runs
      to its end and its result is dropped - there is no state to undo, the table is applied only after the wait. A
      small test seam, `BulkSourceViewModel.ReadGate`, holds the parse on its thread so a test can stop and release it.
      The rebuild after a header choice, the appending of repeated rows and replacing the table with them are begun with
      `canCancel: false`: they are in memory and quick, a stopped half would leave the header choice out of step with the
      table. The three buttons that start a read (*Wklej*, *Wklej i zastąp*, *Wczytaj plik…*) stay buttons: the Stop of
      the line is theirs (the open question of V2b-3 about buttons whose operation is stopped in the line only).
    - **A press that came to nothing is an attention built from "what was skipped: why"** (V2b-2 Dialogs 5), with the key
      of what was skipped as its log copy and the old sentence of the view model as the reason; the file name, the
      header, a cell and a profile name are on screen only - a log copy is the key, a count (the columns remembered by a
      profile, the rows checked) and a result code. A file that could not be read is an error with "Choose another file.";
      a file or a paste that was read and held nothing is an attention.
    - **The check is one operation, whoever asked for it.** Entering the step, *Sprawdź ponownie*, an edit, an exclusion
      and the arrival of the global-catalog session all begin it on the page's holder, a newer one replaces the running one
      without a word, and Stop - the line's, the button's and `StopCommand`, the same Stop - says "Stopping…" and ends as
      the state of the page. Both buttons are action buttons with one holder, so each has its own flag: *Sprawdź ponownie*
      is the Cancel of every check except the one *Numeruj od nowa* started (`CheckRunKind`), as *Refresh* of the Recycle
      Bin is the Cancel of the read whoever started it. The planner reports at the start, every 250 rows and at the end:
      "Checking the rows: 0 of 120…" and "120 of 120…" are what a small sheet shows.
    - **Stopped, failed and out of date are states, not results.** The grid then stands without an explanation of its
      own - empty after a first check that was stopped, older after a recompute - and the sentence is true until the next
      check applies. They are computed from the facts in one method (`UpdateLasting`: the failure, the stop, `IsStale`),
      so "stopped" and "out of date" follow a language switch at once; a failure keeps the language it was said in.
      `IsStale` and `Error` are unchanged for the tests and the plan. *Noticed and fixed after the unit:* a recompute
      that was stopped after an edit left `HasPendingRecompute` false, so *Podgląd* planned from the older computation
      without that edit although the line said "the results may not match … your changes". The plan step
      (`EnsureCurrentAsync`) now also computes again when the last computation was stopped (`_stopped`); the preview
      does not refuse, it has the edit. A test holds the recompute of a manager choice at the directory call, stops it and
      proves that the plan step brings a new computation with the choice. *Corrected after the audit of this increment:*
      that fix closed one of three routes. A recompute that **failed** (the DC did not answer once), and one that the plan
      step itself had started and the Stop of the **preview** cancelled, left the old grid with no flag set - and the next
      *Podgląd* planned from it, without the operator's manager choice or exclusion. One flag per cause could not hold:
      the check now counts every change of its input (an edit, an exclusion, a revert, new pins, an earlier step, a new
      table, *Sprawdź ponownie*) and notes the count a computation was applied for (`IsGridCurrent`); the plan step
      computes whenever the two differ and **refuses** - with the check's own sentence - when the grid is still not
      current afterwards. Tests: the failed recompute end to end (the manager is in the plan once the DC answers), a
      preview whose own recompute fails (refused, nothing planned), and the plan's Stop.
    - **A result goes with what it was about.** Choosing another profile closes what the line said about the last profile
      command (V2b-2 Dialogs 18); the next action in the table (a row, a column, *Wyczyść*) closes the last read's answer
      (6.7: a result stays until the next operation or until it is closed).
    - **The creation is the window's execution** (V2b-1 foundation 9): `ExecuteCreateModeAsync` runs the canary object and
      the rest as two activities of one `ExecutionStatus`, so the line says "Executing “Create accounts (5) in the
      organizational unit …” in contoso.test: 1 of 5…" without a change of this unit; only the proof was added.
    - **The notes of the results do not repeat the window's result.** How the execution ended is said by the line of the
      window (foundation 9); the notes say how the list below them is to be read, for as long as it is shown.
    - `Error` of *Dane* and of *Sprawdzenie*, `ProfileStatus`, `OptionsError`, `Summary`, `IsBusy`, `IsComputing`,
      `ComputedRows`, `TotalRows` and `StopCommand` are unchanged for the tests and the code that read them.
  - *Where this differs from the plan.* The recipe wants one line per converted view; the wizard has none, because the
    window that hosts it has one and a second would say its status in two places (the second list of the source test
    exists for exactly this, Dialogs 15). The three source buttons did not become action buttons (above). The profile
    result is said in the line, which is at the top of the window, while *Zapisz* is at the bottom of a long step.

- **The computer review** (`Computers/ComputerReviewWindow.xaml`; `ComputerReviewViewModel`, `ComputerActions`, `ComputerSites`).
  - *What the line says.* At the top of the window, 8 px in from its edge, above the scope. **At rest** the summary of the
    last check ("As of 9:00 AM - 23 computers read - 23 computers in the reports"), with " - 3 selected" once rows are
    selected; nothing before the first check. **The check:** "Checking the computers in the scope “contoso.test”…", then
    "…: read: 1,200, with findings: 56…" (one report per batch), with Stop; the scope is named as the operator chose it (the
    domain, the path of the organizational unit or of the group, the name of the saved view). Stopped - attention "Checking
    the computers was stopped: the results are incomplete (read: 400). Press “Check” to check again." with *Check*. Failed -
    an error that lasts: "Could not check the computers: reason. Press Retry to try again. (code)" with Retry; a scope that
    cannot be resolved - "The computers were not checked: The saved view no longer exists." without an action and a code.
    **The exact last logon:** "Reading the exact last logon of the computers (12) on all domain controllers…" with Stop;
    done "2 of 2 domain controllers answered; 1 computers were active after all." (with "Checked 500 of 800 computers." in
    front when the list was cut), an attention when a controller did not answer ("… No answer from: dc02.contoso.test."),
    stopped - the list was not changed, with the button as the action; failed - an error with Retry. **The sites:** "Finding
    the sites of the computers (300)…", then "…: 120 of 300…" with the part done in the ring; done "Sites of the computers
    determined: 280 of 300.", or - once per check - an attention that adds the subnets that were skipped because of their
    names; stopped - nothing is applied (the sites are applied after the last computer). **A ping:** "Pinging SRV-APP02…" with
    Stop, then done with the replies ("Ping srv-app02.contoso.test (10.10.1.15): 4 of 4 replies, average 3 ms."), an attention
    when there was no reply or it was stopped, an error when there was nothing to ping ("Could not ping PREST-PC201: PREST-PC201
    has no DNS host name in Active Directory…"). **What lasts:** the session of the window ended; the window was opened without
    one ("Not connected to a domain."); the check failed.
  - *How the old places were sorted* (1 error bar, 2 warning bars, 2 progress bars, and the texts and toasts that said the
    same).

    | Old place | Kind | Now |
    |---|---|---|
    | warning bar `SessionEndedMessage` | status | lasting error of the window |
    | error bar `Error` (a check that failed, a scope that cannot be resolved, no session) | status | the state of the window: an error with Retry, or without one for the scope |
    | warning bar "the release catalogue is old" | note | `Ad.Note.Attention` between the scope and the parameters: what the end-of-support results rest on, true while they are shown |
    | progress bar in the footer (`IsBusy`) and the 3 px bar over the list (`IsRunning`) | status | the operation of the window, with the computers read so far |
    | footer texts "As of …" and the selection | summary | `SetIdle`; gone from the footer |
    | the *Stop* button next to *Check* (`Ad.CollapsedWhenDisabledButton`) | - | gone: *Check* is an action button and the Cancel of the check |
    | the result of the exact last logon (a toast with the failed controllers and their reasons), of the sites and of the invalid subnets (toasts) | status | done / attention / error in the line |
    | the answer of *Ping* (a toast, a dialog for what could not be pinged) | status | the operation "Pinging …" and its result in the line |

  - *Decisions.*
    - **One holder, one operation at a time, a flag per button.** Unlike the account review the window has no tabs, so the
      check, the two reads and the ping share `Status`; they cannot overlap because a second `Begin` would replace the
      first. *Check* may replace a read (a new check always won, and still does, without a word); a ping, a read and a
      second read refuse while something runs (`CanPing`). The four buttons are action buttons with `IsRunning`,
      `IsReadingExact`, `IsResolvingSites` and `IsPinging` - the last follows the operation of the holder, so a ping that
      was replaced is not "Cancel" any more.
    - **The check is begun before its first await** and `IsRunning` is true from that moment (the old code set it after the
      settings were saved and the tiles built), so the button and the line agree; the work between is inside the `try`, a
      failure there is the state of the window like any other.
    - **The controllers that did not answer are named, their reasons are not** (as in the account review): the toast used to
      list "dc02: reason" per controller; the line has the names and the counts, and what a reason says is one more thing to
      read in a place that is gone after a few seconds. The log copy has the counts only.
    - **The sites report how far they are.** `ComputerSites.ResolveAsync` got an optional `IProgress<int>` that reports about
      fifty times (one report per batch of computers, never one per computer) and once at the end, in order. The exact last
      logon has no count of its own (one read per controller, the same call for all computers): its line is indeterminate.
    - **A ping says its result in the line.** `ComputerActions.PingAsync` (shell, property sheet) keeps its toast; the review
      uses the new `TryPingAsync`, which says nothing itself and hands back the result, or why there is none with the kind of
      the failure. The same split made `FindTargetAsync` (the remote target, or why there is none, without a dialog); the old
      `ResolveTargetAsync` is built on it and behaves as before.
    - **The silent refresh after a change keeps its toast** on failure: it never said "working", it re-reads the rows that a
      change touched and finished elsewhere (6.7), as in the account review.
    - `Error`, `StatusText`, `SelectionText`, `IsRunning`, `IsReading`, `IsBusy` and `StopCommand` are unchanged for the tests
      and the code that read them (`StopCommand` is the same Stop as the line's). The window is 36 px higher by default (860
      to 896).
- **The remote actions window and the restart dialog** (`Computers/RemoteComputerWindow.xaml`, `RestartComputerWindow.xaml`;
  `RemoteComputerViewModel`, `RestartComputerViewModel`).
  - *What the line says.* Under the header (the computer and its DNS name), above the actions. **At rest** the connection:
    "Connection: WinRM (HTTP 5985, Kerberos) as CONTOSO\admin". **An action** is the operation of the window, one at a time:
    "Reading the status of KRK-PC101…", "Reading the logged-on users of …", "Reading the services of …", "Refreshing Group
    Policy on …", "Scheduling the restart of …", "Cancelling the scheduled restart of …" - with Stop. A read ends without a
    message (the card and the lists are its result); an action ends as done ("KRK-PC101: policy refreshed (computer policy
    processed 12:04)", "KRK-PC101 restarts in 15 min"); **stopped** - a read: attention "Reading the logged-on users of
    KRK-PC101 was stopped. Press “Show logged-on users” to read again." with that button; an action: "… was stopped: it is not
    known whether the computer received the request." without an action; **failed** - an error "Could not read the services
    of KRK-PC101: The computer does not answer. Check … (Unreachable)" with Retry for a read and without it for an action.
    **What lasts:** the session of the window ended, no session, the computer has no DNS name ("PREST-PC201 has no DNS host
    name in Active Directory…"), remote actions are not available in this host.
  - *How the old places were sorted* (1 error bar, 1 warning bar, 1 progress bar).

    | Old place | Kind | Now |
    |---|---|---|
    | warning bar `SessionEndedMessage` | status | lasting error of the window |
    | error bar `Error` (a failed call, "Cancelled.", no DNS name, no remote client, no session) | status | the result of the action; the state of the window for what cannot be done at all |
    | progress bar in the footer (`IsBusy`) and the *Stop* button | status | the action in the line; the six buttons that start one are action buttons |
    | `ConnectionText` in the footer | summary | `SetIdle`; gone from the footer |
    | the error dialog of `ResolveTargetAsync` when the window opens on a computer that cannot be acted on | status | the state of the window (no dialog on top of it) |
    | the validation sentence of the restart dialog (a caution glyph and secondary text by hand) | note | `Ad.Note.Attention`: why *Restart* is not possible yet - a sentence about the fields |

  - *Decisions.*
    - **Reads end quietly, actions say what was done.** The old window said both in the error bar (failures) and in its
      action log (successes); the log stays, because it is the history of what was done in this window (it is the twin of the
      audit and of the PowerShell history, and it survives the next action), while the line says the last answer. A restart
      says its own sentence, which names the computer; the others get the computer in front.
    - **A stopped action says that nobody knows.** A call that was cancelled while it was on its way to the computer may or
      may not have been received; the line says that and offers no link - the operator checks and presses the button. The log
      and the audit still say "Cancelled." as before (rule 5: only how a result is said changed).
    - **One more result code** - `ResultCode.Remote(RemoteErrorKind)` ("WinRmNotAvailable", "AccessDenied") - the only
      change to the model of the increment so far; a kind of failure the program names is allowed in a log copy.
    - **The dialog on opening is gone.** `FindTargetAsync` is quiet; `InitializeAsync` puts why there is no computer into the
      state of the window. `ResolveTargetAsync` (tools, ping from the shell) still opens its dialog.
    - **The restart dialog starts no operation**: it collects a request and hands it to this window. It is on the second list
      of the source test with the validation sentence as a note (the window kept `ErrorText` for its tests).
    - The window is 36 px higher by default (780 to 816); `Error`, `IsBusy`, `ConnectionText` and `StopCommand` are unchanged.
- **The event log documents** (`EventLogs/EventLogReaderView.xaml`, `FailedLogonsView.xaml`, `LockoutView.xaml`;
  `EventLogDocumentBase` and the three view models).
  - *What the line says.* Under the header of each document. **At rest** the summary of the last run with the selected
    record or finding joined by " - " ("7 events from 2 sources", "Choose the sources and the query, then click Search." before
    the first run). **A run** is the operation of the tab, with its stage as part of one message: the reader "Reading the event
    logs - sources: 2 of 3, events: 1,204…" (sources as the part done), the failed logons "Checking the failed logons - reading
    the domain controllers…", "… - reading the failed logons, sources: 2 of 3, events: 1,204…", "… - reading the successful
    logons, accounts: 12…", "… - analysing the events…", the lockout "Investigating the lockout of “Jan Kowalski” - reading the
    account…", "… - reading the domain controllers…", "… - reading the logs, sources: …", "… - analysing the events…"; the
    channels "Loading the channels of dc01…". All with Stop. **What a run leaves:** nothing to say when every source was read;
    an attention when some were not ("Reading the event logs: sources not read: 1 of 2 (dc02.contoso.test). The reason of each
    is in “Coverage and access”.", up to three names); a stopped run - an attention with *Refresh* ("Reading the event logs was
    stopped; events read and shown: 1,204. Press “Refresh” to read again." for the reader, which keeps what it read; "Checking
    the failed logons was stopped: the results shown were not changed. Press “Refresh” to read again." for the two analyses); a failed run - an error with
    *Retry* ("Could not investigate the lockout of “Jan Kowalski”: reason. Press Retry to try again. (code)"); the channels -
    done "dc01.contoso.test: 2 channels loaded." (it no longer overwrites the summary of the last run). **What lasts:** not
    connected; "Event logs can be read only in AD Manager running on Windows." or the simple-bind sentence (an attention - the
    banner of before, whose place the summary never had: it was empty in such a window); what the run found it cannot show
    (the account is not found or no account, the range is not valid) - an error without an action, until the next run.
  - *How the old places were sorted* (0 error bars, 3 warning bars, 3 progress bars, and the status texts and buttons).

    | Old place | Kind | Now |
    |---|---|---|
    | warning bar "not supported" (not Windows, a simple bind), one per document | status | the state of the tab that lasts (an attention, without an action) |
    | progress bar and the text beside it (`StatusText`: the progress, then the summary, or the error) | status, summary | the operation with its stage and the part done; the summary; the error as a result |
    | `LoadedText` "Cancelled after N events - …" / "Cancelled." | status | an attention with Refresh; the reader keeps its text as the summary of what is shown |
    | the *Cancel* button next to the run button | - | gone: the run buttons are action buttons (`RunButton` of the three, *Load channels* of the reader) |
    | `Error` assigned in the middle of a run (the account is not found, no account, an invalid range) | status | the state of the tab (`ShowUnavailable`) |
    | `Error` after "Show in the model" failed | status | an error that can be closed - never over a run that is on the line |
    | the state, the coverage and the access of each source (`Hosts`: panel "Coverage and access") | state of a row | unchanged |
    | the notes of the analyses (`Notes`), the validation texts of the query | note | unchanged |

  - *Decisions.*
    - **The holder is the base class's** (as in the Group Policy documents, *Group Policy documents* 1): `Status` and
      `RefreshCommand` (the "Refresh" of a stopped run and the "Retry" of a failed one - F5 is `RefreshAsync`), one `RunText`
      per run (its key is the log key of the operation and what is said when it fails), `ReportStage` and `ReportSources`
      for the stages, `ShowUnavailable` and `SayFailure` for what a run or an action of the tab has to say.
    - **A stage is not a message.** The title of the operation is the key; the stage and its counts are new texts of the same
      message (the log copy is the key with "done, total, events"). `ProgressText` is the same text for what still reads it.
    - **Coverage is said in two places that are not the same sentence.** *Which sources a run could not read* is what the run
      did and is said once by the line (names and counts); *why*, for each, is its row in the panel, which opens by itself;
      *how far back a log reaches* is a finding about the content and stays in its row and in the notes of the analyses. The
      first is closable, so the second never depends on it.
    - **A stopped reader keeps what it read** (`MarkPartial`), so the line says how many events are shown and the summary
      keeps "Cancelled after N events - …"; the two analyses throw when stopped between their steps and leave what they showed.
    - **A failed run that leaves the tab as it was is a result** (an error with Retry); what the run found the tab cannot
      show is a state (`ShowUnavailable`) - nothing in the tab explains the empty report. The not connected state is the one of
      the Group Policy documents.
    - **The reader's two start buttons** are action buttons with a flag each (`IsSearching`, `IsLoadingChannels`), over one
      holder - the second is a small button beside the channel box and is as wide as "Zatrzymywanie…" needs.
    - `Error`, `StatusText`, `SelectionText`, `IsLoading`, `CancelCommand` and `Cancel()` are unchanged for the shell and the
      tests (`Cancel()` is the same Stop as the line's); the status bar of the main window says nothing about these tabs now.
- **The readiness tab** (`Readiness/ReadinessView.xaml`; `ReadinessDocumentViewModel`).
  - *What the line says.* Under the header. **At rest** what the last check found, then when and where it was made ("OK: 20 -
    Warnings: 3 - Errors: 1 - Not checked: 9 - Last check: full, 3.11.2026 14:32, on dc01.contoso.test"; "Not checked yet. Start
    with the quick check." before the first one). **A check:** "Full check: 5 of 8 - Event logs (E)…" with the part done in the
    ring and Stop. **What lasts**, the most important first: not connected; the connection changed after the shown check ("The
    connection of this window changed after the shown check: the results may no longer be true. Press “Check again” to check
    again."); the shown check was stopped ("… the checks it did not reach are shown as not checked. Press “Check again”…"),
    both with *Check again*.
  - *How the old places were sorted* (0 error bars, 1 warning bar, 1 progress bar, and the texts around them).

    | Old place | Kind | Now |
    |---|---|---|
    | warning bar "the connection changed after this check" with *Check again* | status | the state of the tab that lasts, with *Check again* as its action |
    | progress bar and "Checking: Event logs (E) (5 of 8)" | status | the check in the line, with how far it is |
    | "Last check: …" (a text of its own) and the bold "OK: 20 - …" | summary | `SetIdle`; both gone from the tab |
    | "The check was cancelled; …" in the card | status | the state of the tab that lasts |
    | "Read only: …" and "The export names …" (a card with an information glyph) | note | one `Ad.Note.Info` above the areas |
    | the *Cancel* button | - | gone: *Quick check* and *Full check* are action buttons, one flag each |
    | what each check found (the rows of the areas) | the result of a row | unchanged |

  - *Decisions.*
    - **A cancelled check is a state, not a result.** The rows of the checks it did not reach say "not checked" until the next
      check, and the sentence is true for as long as they are shown; "stopped" and "the connection changed" are told apart by
      the cause (the operator's Stop, or the session of the window), and the connection wins - the rows are of another
      connection and incomplete, with the same action.
    - **Two buttons, one holder, one flag each** (`IsQuickChecking`, `IsFullChecking`), as the two buttons of the bulk check.
    - **The summary has the counts first, the time and the place last**, so a narrow tab cuts the part that is in the tool tip
      and the list under it anyway (the order of the Group Policy documents).
    - **Stop gives control back when the check does.** The checker honours the token between the checks and inside the
      long ones, but a check that sits in a call that takes no token (the SYSVOL folder, a blocking library call) ends when
      that call does; the line says "Stopping…" at once. The thread of a check is not abandoned: what it found before
      the stop is the report that is shown.
    - `StatusText`, `ProgressText`, `ProgressValue`, `SummaryText`, `LastRunText`, `IsStale`, `WasCancelled`, `CancelCommand`
      and `Cancel()` are unchanged for the shell and the tests.

- **The PKI documents** (`Pki/PkiOverviewView.xaml`, `PkiTemplatesView.xaml`, `PkiReadinessView.xaml`,
  `PkiSecurityReviewView.xaml`; `PkiDocumentBase` and the four view models).
  - *What the line says.* Under the header of each document, above the notes and the content. **At rest** the summary:
    the overview "Urzędy przedsiębiorstwa: 2 · szablony: 26 · zaufane urzędy główne: 1 · certyfikaty NTAuth: 1 · odczytano z
    dc01.contoso.test, 13.10.2026 14:00 · Kontoso Wystawiający CA" (what was read, when and from where, the selected row; the
    selected certificate on the trust tab); the templates "Szablony: 26, niewystawiane przez żaden urząd: 2 · policzone konta:
    1 234 · <template>"; the readiness report "OK: 4 · Ostrzeżenia: 3 · Problemy: 3 · Nie sprawdzono: 2 · Odczytano: … na
    dc01.contoso.test"; the security review "15 uwag · Ostatnie sprawdzenie: … na dc01.contoso.test · <finding>", with "Pokazano
    uwagi: 2 z 15" under a filter. **The read** is **working** "Wczytywanie „Usługi kluczy publicznych” – obiekty AD CS: 1 204…"
    with Stop; the stages are the objects, the rights of the templates, the accounts ("konta: 500", the templates also
    "liczenie kont: 500"), the domain controllers, the probe of the LDAPS certificates when the tab was seen, and for the
    review "Sprawdzanie bezpieczeństwa AD CS – odczyt uprawnień obiektów PKI…", "rozpoznawanie kont i grup…", "ocena reguł
    ESC…". **What lasts**, the most important first: not connected (an error without an action); the read failed - "Nie udało
    się wczytać „…”: reason. Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)" with Retry; the read was stopped -
    attention with the refresh action ("…zatrzymano: karta może nie pokazywać wszystkiego. Wybierz „Odśwież”…"; for the review
    "…karta pokazuje wyniki wcześniejszego sprawdzenia, jeśli było…"); and in the templates that the accounts could not be
    counted - attention with Retry, the rights stay on screen. **The overview's two tabs** say their own: the health tab the
    counts of its states and what the HTTP check found, and while it runs "Sprawdzanie adresów AIA, CDP i OCSP – 3 z 8…"
    with the part done in the ring; the LDAPS tab the count of what it found ("Sprawdzone porty: 3; OK: 2; z problemem: 1.")
    and "Sprawdzanie certyfikatów LDAPS kontrolerów domeny…". A stopped check is an attention that can be closed - "…zatrzymano:
    wyników nie zmieniono. Wybierz „Sprawdź adresy HTTP”…" with its own button as the action -, a check that could not be made
    an error with Retry (the HTTP check: a result that can be closed; the probe: the state of the LDAPS tab, until the next
    probe).
  - *How the old places were sorted* (2 error bars, 2 warning bars, 4 progress bars, and the texts and cards that said the same).

    | Old place | Kind | Now |
    |---|---|---|
    | error bar `Error` of the overview and of the templates (a failed read, not connected) | status | the state of the document that lasts: an error with Retry, or without an action for "not connected" |
    | progress bar and text in the place of the summary (`IsLoading`, `StatusText`) of the overview and the templates; progress bar and "Odczyt…" next to *Odczytaj ponownie* / *Sprawdź ponownie* of the report and the review | status | the read in the line, with the stage and Stop |
    | `SummaryText`, `ReadAtText`, `CountStatusText`; the bold summary and `LastRunText` of the report; `LastRunText` of the review | summary | `SetIdle` of the document, the "as of" text and the count of accounts as its detail; the text blocks are gone |
    | `HealthSummaryText`, `HttpStatusText` (checked: n), `LdapsStatusText`, the progress of the two checks | summary, status | the summary and the operation of the tab in front (health, LDAPS); "not checked" and "not available" are a note |
    | the *Anuluj* button of the review | - | gone: *Sprawdź ponownie* is an action button and the Cancel of the check |
    | warning bar "the CA issues logon certificates but is not in NTAuth" (`NtAuthWarning`) | note | `Ad.Note.Attention` in the CA's details: true for as long as that CA is shown |
    | warning bar "the template's rights were not evaluated" | note | `Ad.Note.Attention` in the template's details |
    | the card "what the read observed" (overview), the card of the scope and notes (review), the card "read only" (report) | note | `Ad.Note.Attention` / `Ad.Note.Info` / `Ad.Note.Info`: how complete the content is and what it is, true while it is shown (the review has two: the scope - information - and what the check could not read - attention) |
    | `HttpStatusText` "not checked: until then their rows are Unknown" and "not available in this window" | note | `Ad.Note.Info` under the thresholds of the health tab (`HttpNote`) |
    | `FilterText` ("Pokazane szablony: 3 z 26"), `CompareSummary`, the thresholds, the hints of empty lists | hint of a control | unchanged - a count that belongs to the control beside it |
    | the certificate tabs of the property sheet (published certificates, key credentials, name mappings) | status, note | unchanged - converted in V2b-1 (their holders, the lasting error with Retry, the weak and invalid mappings as notes) |

  - *Decisions.*
    - **The base class owns the holder** (*Areas* 5). The base creates it from the services the window already has
      (`Pki.Localizer`, `Pki.Dispatcher`, `Pki.Ui.Lists.StatusLog`), begins the load before its first await, links the token of
      the operation with the serial token and the lifetime, ends the operation after the state (the line goes from "working"
      straight to what the load left) and says "stopped" for any cancellation that came neither from a newer load nor from
      the closing of the window - so Stop and the replacement of a read by an operation of the same holder (the readiness
      probe) are told apart from F5. A document supplies the key of its operation (`ReadingKey`: "Wczytywanie „…”", the review
      "Sprawdzanie bezpieczeństwa AD CS"), what was tried when it fails, the sentence of a stopped read, the text of its refresh
      button and the detail of its summary; `ReportStage(key, count, progress)` says a stage as a new text of the same message
      (the log copy is the key and the count). `ProgressText` moved into the base and carries the same text as the line.
    - **A stage is not a message** (as in the event logs), and **the log copy is a key, a count and a code** - never the title
      of a document, a CA, a template, a server, an account or the reason of the system (`PkiDoc.Status.Reading (500)`,
      `PkiDoc.Status.ReadFailed [ServerUnavailable]`, `PkiOverview.Status.HttpChecking (3, 8)`).
    - **The health and LDAPS tabs have a holder each, and a read that probes is one operation.** The HTTP check and the probe can
      be started while the document is read (their buttons did not depend on `IsLoading`), so they cannot share its operation
      (foundation 4); each is in front while its tab is shown, and the document's own working or error that cannot be closed
      covers them. The probe that a read makes itself when the LDAPS tab was seen (F5, another domain controller) is a *stage of
      the read* and begins no operation of the tab; the probe the operator starts - the button, or the first time the tab is
      shown - does. A failed probe is the state of the LDAPS tab (`SetLasting`, Retry) until the next probe or read; a failed or
      stopped HTTP check is a result of the health tab. The stopped checks say that the results were *not changed* - the
      window's cache stores a check only when it ends.
    - **The readiness probe is an operation of the one holder and the read refuses it** (`CanCheckLdaps` now also needs
      `!IsLoading`; a read that starts meanwhile - F5, another session - replaces the probe without a word). The areas the read did
      not reach say "not read" (`OnLoadInterrupted` builds the rows again), not "reading…". The areas keep what each found, also
      the reason of a read that failed (the state of a row).
    - **The templates' account pass is a stage of the load, and a pass that cannot count is the state of the tab** (an
      attention, not an error: the rights are shown). `DocumentLastingState` computes it from the facts, so it follows a language
      switch; the old `CountStatusText` keeps its sentence for the tests.
    - **Stopping the security check keeps the findings of the check before it.** `LoadedText` says "Sprawdzanie anulowano." as
      before, and the line says it as the state of the tab. *Zatrzymaj* of the line, the cancel of `CancelCommand` and the button
      that started the check are one Stop.
    - **`Refresh` is the Cancel of the read whoever started it** (`IsRunning = IsLoading`): the operator's press, F5, a session
      change, the reload that follows another document's F5. As in the Recycle Bin and the computer review.
    - **No "changed elsewhere / older than" state was made up.** The PKI documents follow the window's cache - a read made by
      another PKI document is shown by every open one at once - and nothing in them measures how old a read is, so there is no
      lasting state of that kind; when and from where a read was made is the detail of the summary.
    - `Error`, `StatusText`, `SelectionText`, `IsLoading`, `ProgressText`, `LoadedText`, `SummaryText`, `ReadAtText`,
      `CountStatusText`, `HttpStatusText`, `LdapsStatusText`, `LastRunText`, `CancelCommand` and `Cancel()` are unchanged for the
      shell and the tests (`Cancel()` is the same Stop as the line's); the status bar of the main window says nothing about the
      four tabs now.
  - *Where this differs from the recipe.* Three small changes beyond how a result is said (rule 5 holds - nothing is written):
    the probe button of the readiness report is refused while the report is read (`CanCheckLdaps`), the report's read passes a
    progress to the snapshot service so that its first stage has a count (as the other three documents do), and the health tab
    got `HttpNote`, the sentence that was a text under the summary. The certificate tabs of the property sheet needed nothing
    (V2b-1). Strings: `PkiDoc.Status.*`, `PkiOverview.Status.*`, `PkiTemplates.Status.*` and `PkiSecurity.Status.*` in the V2
    block, with `L.PkiDocumentStatus`, `L.PkiOverviewStatus`, `L.PkiTemplatesStatus` and `L.PkiSecurityStatus`. Nothing was
    added to the model or the controls.

- **The Watchdog tab** (`Views/Watchdog/WatchdogView.xaml` hosting `WatchdogConfigBar` and the pages `WatchdogEventsView` with
  `WatchdogEventDetailsView`, `WatchdogRulesView` with `WatchdogRuleEditorView`, `WatchdogChannelsView` with
  `WatchdogChannelEditorView`, `WatchdogDeliveryTestView` and `WatchdogSecretFieldView`, `WatchdogDigestView`,
  `WatchdogStatusView` and `WatchdogSourcesView`; `WatchdogDocumentViewModel` with `Events` (and `Events.Details`), `Config`,
  `Rules`, `Channels`, `Digest`, `ServiceStatus` and `Sources`; `WatchdogCertificateTrustWindow` is a dialog that only asks and
  has none of it).
  - *What the line says.* One line under the connection card, 8 px in from the tab's edge, over the sub-tabs. **At rest** the
    summary of the page that is shown: the feed "Zdarzeń: 30 · niepotwierdzonych: 28 · stan na 10:30:00" with " · Zaznaczono: 3";
    the Rules, Channels, Digest and Sources tabs the version of the configuration ("Wersja 7 · 14.10.2026 12:00 · CONTOSO\jan.kowalski",
    `VersionText`); the status tab "Stan odczytano 10:30:00". **The connection:** "Łączenie z admng-wd.contoso.com:8443…" (no Stop -
    it never had one); a problem is an error that cannot be closed, "Nie udało się połączyć z admng-wd.contoso.com:8443: <the
    explanation of the kind of failure, with its remedy> Wybierz Ponów, aby spróbować ponownie. (Unreachable)" with Retry, or -
    when the service presented a certificate to confirm - with the one action "Sprawdź certyfikat…"; no address is an attention
    without an action (the address editor is the way out); interrupted live updates an attention without an action (the tab
    retries by itself). **The feed:** "Wczytywanie zdarzeń…" with Stop; stopped - attention "…zatrzymano: lista jest pusta, a
    aktualizacje na żywo wstrzymane. Wybierz „Odśwież”…" with Refresh; failed - error with Retry; both are the state of the page,
    which cannot be closed, until the feed is read again. "Wczytywanie starszych zdarzeń (wczytano: 100)…" with Stop; stopped or
    failed it leaves the list as it was - a result that can be closed, with "Wczytaj starsze" or Retry. "Potwierdzanie zdarzeń:
    500 z 1 200…" with the part done, without Stop. **The details pane:** "Potwierdzanie zdarzenia…" without Stop, an error with
    the reason; "Pokaż w AD Manager" says that the object no longer exists or why it could not be opened, "Odblokuj konto" that
    the account was unlocked; the next selected event closes what the line said. **The configuration:** "Wczytywanie konfiguracji
    usługi…" and "Sprawdzanie konfiguracji…" with Stop, "Zapisywanie konfiguracji…" without; done "Zapisano konfigurację (wersja
    8)."; a save the service refused - attention "Konfiguracja nie została zapisana – popraw zaznaczone pola." with the findings
    in the list of the bar; the state of the tabs that lasts: nothing could be read or the read was stopped before a
    configuration was loaded (Retry or "Wczytaj ponownie"), and someone else saved first (attention, "Wczytaj ponownie"). **The
    digest and the rules:** the preview of the digest and of a rule with Stop (the result is the content: the line returns to the
    summary; a stopped or failed one leaves the preview as it was and offers the button again or Retry), "Wyślij teraz" without
    Stop, the read behind "reset to default" with Stop and "Reguła nie została przywrócona: …". **A delivery test:** "Wysyłanie
    testu…" with Stop; done "Test zakończony powodzeniem (840 ms)."; error "Test nie powiódł się (1,3 s). Etapy testu pokazują,
    gdzie się nie powiódł."; "Nic nie zostało wysłane: …" (attention for what the operator can put right, error for a failed
    call); stopped - "nie wiadomo, czy cokolwiek dotarło do miejsca docelowego". **The status tab:** "Wczytywanie stanu usługi…",
    then "Wczytywanie dziennika dostarczania…" and "Wczytywanie dziennika zmian konfiguracji…", each with Stop; a status read that
    failed or was stopped while none is shown is the state of the tab, one with a status on screen a result that can be closed;
    "Ponowne kolejkowanie dostarczenia…" without Stop.
  - *How the old places were sorted* (2 error bars, 4 warning bars, 7 progress bars, and the texts that said the same).

    | Old place | Kind | Now |
    |---|---|---|
    | the connection card - state and host in warning colours for every problem, `BannerMessage` (why), `LiveError`, the buttons *Ponów* and *Sprawdź certyfikat…* | status (and identity) | the card keeps the host, the account and *Zmień adres*; the state is the connection attempt and the lasting error / attention of the line, Retry or "Sprawdź certyfikat…" its one action |
    | warning bar `RolloutBanner` (rollout mode, *Zakończ tryb wdrożenia*) | note | `Ad.Note.Attention` with its button, in the same place: a fact about the service that lasts for days - as a state of the line it would hide the summary of every page |
    | warning bar `ReadOnlyBar` of the configuration bar (the Viewer, a redacted copy) | note | `Ad.Note.Attention` (the lock glyph is the glyph of a note now), and a second note, `Ad.Note.Info`, for "the copy is redacted" |
    | error bar of the findings of the service (configuration bar) | note | the list of findings without the bar: each row has its glyph and its sentence, true for as long as the working copy has them |
    | warning bar of the generated signing secret ("shown once - copy it now") | note | `Ad.Note.Attention` with the text box and the copy button |
    | the hints of a delivery test (one warning bar each) | note | `Ad.Note.Attention` each |
    | `ErrorBar` of the feed (`Error`, *Ponów*) and its 3 px progress bar | status | the read of the feed in the line, the state of the page with Retry |
    | progress bar, `MessageText` and `ErrorText` of the configuration bar | status | the read, the validation and the save of the configuration in the line; the result, the findings and the conflict as said above |
    | `VersionText` of the configuration bar | summary | `SetIdle` of the configuration's holder; the marker "Niezapisane zmiany" stays next to the buttons |
    | progress bar, `ErrorText` and `MessageText` under the digest preview | status | the preview and "Wyślij teraz" in the line |
    | progress bar next to *Uruchom podgląd* of the rule editor, `PreviewError` | status | the preview of a rule in the line (`PreviewText` - "12 zdarzeń, ok. 4 wiadomości…" - is the caption of the list below it and stays) |
    | progress bar, *Anuluj*, `SummaryText` and `ErrorText` of the delivery test card | status | the test in the line; *Wyślij test* of the SMTP card and of a channel are action buttons; the stages stay in their rows |
    | progress bar of the details pane, `Error`, `ActionMessage` | busy / note / status | a `ProgressRing` in the header of the pane (no sentence: a read at every selection would announce itself at every key), why the details could not be read as `Ad.Note.Error` under the title, the answers of the pane's actions in the line |
    | progress bar and `ErrorText` of the status tab, `Error` of the delivery log, `AuditError` | status | the three reads and the retry of a dead delivery in the line |
    | the status bar of the main window for this tab (`StatusText` of the feed) | summary | gone: the document is `IHasPageStatus` |
    | the state and the explanation of the connection in the card (`StateText`, `ConnectionMessage`) | status | gone from the card |
    | the validation texts of the fields, the hints, `AddressError`, the card of the address editor | note | unchanged |

  - *Decisions.*
    - **One line for the tab, one holder per page that has an operation of its own, the page that is shown in front.** The
      sub-tabs are pages of one document, so there is one `c:StatusLine`, and the tab's holder gets
      `Front = FrontOf(SelectedSubTab)` before the page is activated (foundation 3). The holders are the page's: the feed's
      (with the details pane's in front of it - the pane's actions can run while the list is read), the configuration's
      (shared by the four tabs that edit it, in front of each of them), and the digest's, the rules' and the channels', each in
      front of the configuration's (a preview or a test can be started while a save runs, and a second `Begin` would have taken
      the first off the line - foundation 4), and the status tab's. Nothing is lost while a page is not shown: a feed that failed
      while the Rules tab was shown says so when the feed is shown again. The holders are made by
      `WatchdogServices.CreateStatus()` from what the tab already gets (the localizer, the dispatcher, the status log of the
      window's list services) and exist in memory only (rule 1: nothing about the service is stored by the WPF process).
    - **The card says which service and as whom; the state is the line's.** The old card showed the state word and the host in
      colours of the state, the explanation under it and the buttons - the state said twice. The card keeps what is always true:
      the host (credentials go only there - one test asserts that it stands once on the tab), the account, the role and the
      level of detail, and *Zmień adres* (an input, not a status; the line has room for one action). It is collapsed while no
      address is known.
    - **`Status` is the holder; the status tab is `ServiceStatus`.** `IHasPageStatus.Status` and the binding `Status.Current`
      that the source test wants are the holder, so the property that was the "Stan" page's view model got a new name (the tests
      and the view that read it were changed; the page's own model, `Status` of type `StatusDto`, keeps its name).
    - **The connection attempt is an operation without Stop; the pages read after it.** It ends right after the reads of the
      pages have begun (each on its own holder), so the line goes from "Łączenie…" straight to what the page that is shown says;
      a failure sets the state first and ends the operation after (before the certificate dialog opens, so the line never says
      "Łączenie…" over it). `LiveError` is made from the failure when it is read - the line follows a language switch at once -
      and the one state method recomputes after every change of a fact.
    - **A stopped read of the feed leaves it empty and paused.** The read replaces the rows when it answers, but it has already
      cleared the poller's cursor and taken the new filter; going on after a Stop would show the rows of one filter next to the
      filter of another and a feed that never gets a live update. The stopped read is therefore the same state as a failed one -
      the list is empty, the live updates wait for the next read - and says so, with Refresh.
    - **Acknowledging and the other writes to the service have no Stop and no link.** The part done is the ring ("500 z 1 200…",
      one request of 500 events at a time); the result is the rows (the tick, the counts of the summary) and the toast of the
      main window, as before, so the line goes back to the summary; a failure says how many were done and what to do. A save of
      the configuration, "Wyślij teraz" and the retry of a dead delivery are the same. Where the operation had no way to be
      repeated safely from a link, the error has no action. A write whose line was taken by a read (a filter changed while it
      ran) says its failure with a toast: it finished elsewhere (6.7).
    - **The details pane's read is a ring and a note, its actions are the line's.** The pane reads at every selection; as an
      operation each key press would put "working" in a live region. Why the read failed is true for as long as that event is
      shown (a note under its title, cleared by the next selection); what the operator pressed in the pane - acknowledge, open,
      unlock - is the answer of the window and goes to the line, closed by the next selection.
    - **The configuration says its version; a conflict and a missing configuration are states.** The version, who and when is the
      "as of" text of the four pages (the summary, no longer a text in the bar); the marker of unsaved edits stays at *Zapisz*
      (as in the editors of V2b-3). That someone saved first is true until the configuration is read again (or the next edit,
      which clears the flag as before) and has the one way out - *Wczytaj ponownie*, the command of the bar - so it is the state
      of the tabs; a read that failed or was stopped before anything was loaded explains empty tabs and is a state as well, a
      reload that fails with a configuration on screen is a result. A save the service refused is an attention - nothing was
      saved and why - and the findings stay in the list of the bar; the toast after a save stays, as after an execution.
    - **A delivery test is one operation of the channels tab; the stages are rows.** The SMTP test and every channel's test
      begin on the tab's holder, so a test started while another runs replaces it (a second test of one editor always did); the
      button of each is an action button over its own start command, flag (`IsRunning`) and cancel (the test's own, which says
      "Zatrzymywanie…" in the line first). The line says whether it ran, how it ended and what to do; the rows keep what each
      stage did (the mark, the detail, the code, the time) and the hints are notes in the card. A test that was stopped does not
      know whether the destination got anything, and says so.
    - **The status tab reads one thing after another.** The status, the delivery log and the audit are three operations of one
      holder, one after another (they were read in parallel - the only visible difference is that the line shows each in turn);
      each can be stopped; a stopped or failed read of the status when none is shown is the state of the tab. The summary is
      when the status shown was received (the poller applies a newer one every half minute and changes only the time).
    - **The dialogs of two helpers became the line.** "Reset to default" says why nothing was reset ("Reguła nie została
      przywrócona: …") and a failure with Retry in the line of the rules tab - it was an error dialog, and its read has a token
      and a Stop now (rule 6); the other answers of the Watchdog pages that were dialogs (a confirmation, the certificate) stay.
    - **Log copies.** Every message is a key with counts and a result code: `ResultCode.Watchdog(kind)` ("Unreachable",
      "Forbidden", "Timeout", "ServerError"...) is the one thing added to the model - the kind of failure the client classifies,
      never what the service said. No host, account, event, recipient, address, rule or value of the configuration is in a log
      copy, and no secret is in a text or a copy (rule 2: the tests type a password into the configuration, save it and read the
      line, the recorder and the log); the tests of every holder read the log for exactly those.
    - `StatusText`, `SelectionText`, `IsLoading`, `Error`, `Message`, `PreviewError`, `ActionMessage`, `LoadedText`... of the view
      models, `HasConflict`, `IsBusy` and the commands are unchanged for the shell and the tests that read them; `Cancel` of a
      delivery test is the same Stop as the line's.
  - *Where this differs from the recipe.* *Validate*, *Reload*, the preview buttons, *Odśwież* of the feed and of the status
    tab, and the two buttons of the save bar stay plain buttons (or icon buttons): they never had a Stop of their own, the Stop
    is in the line (the open question of V2b-3: several action buttons over one holder each need a flag). The tab has **one**
    line where the brief of the increment would have had one per page, because the pages are one document; the card and the
    sub-tab strip are not part of it. Behaviour that changed beyond how a result is said (nothing about how a request is made,
    validated or audited - rule 5 holds): a stopped read of the feed clears it (above), the reads of the status tab are
    sequential, "reset to default" uses the line instead of dialogs, and every read that ran with `CancellationToken.None`
    (the configuration, its validation, the status, the delivery log, the audit, the previews, the defaults) takes the token of
    its operation, so it can be stopped (rule 6) - the writes still pass none: a write is not cancelled by a scope that was
    replaced -, a test replaces a running test of another editor. The constructors of
    `ChannelEditorViewModel`, `WatchdogRuleEditorViewModel`, `DeliveryTestViewModel` and `WatchdogDeliveryLogViewModel` got an
    optional holder (the tab's); standalone they make their own, so the existing tests build them as before.

- **The Korespondencja window** (`Correspondence/CorrespondenceWindow.xaml`; `CorrespondenceViewModel` with the pages
  `RecipientsPageViewModel`, `MailboxOwnersPageViewModel`, `PreviewPageViewModel`, `SendPageViewModel` and
  `ResponsesPageViewModel`, and `OutlookConnectionViewModel`; `ReportWindow` and `TextPromptWindow` of the folder start no
  operation and are on `ConvertedViewsWithoutAStatusLine`).
  - *What the line says.* One line above the tabs, 8 px in from the edge, bound to the holder of the window, which has the page
    of the tab in front. **Odbiorcy:** at rest the counts of the list ("Odbiorcy: 120 • do wysłania: 95 • pominięci: 20"),
    where the old text block under the grid was; **working** "Reading “contacts.csv”…", "Reading the files: 2 of 5…" (the
    part done in the ring) and "Reading 12 objects from the directory…", all with Stop - the thread that parses runs to its
    end, the page has control back at once and the file it was reading is dropped; **done** or **attention** is what the
    action came to, in the sentence the page already had ("Read the file “contacts.csv” (CSV): 120 new, 3 duplicates, 2
    invalid addresses", a paste that found nothing, "Nothing was loaded: The Downloads folder could not be found."), with the
    files that were not read and why in the same sentence ("… Not loaded: Could not read “broken.xlsx”: …"); a stopped load
    ("Loading was stopped after 2 of 5 files: the rows of the files read so far are on the list.") and a stopped directory read
    ("Nothing was added from the directory: Reading was stopped." with *Retry*); **error** for a failed directory read, with the
    LDAP code and *Retry*. **Skrzynki:** at rest the counts of the matches; **working** "Searching for mailbox owners: 120
    mailboxes, 4,210 persons read…" and "Reading the owners (9)…" with Stop; **attention** for a search that was stopped ("The
    list may be incomplete. Press “Retry” to search again."), that found no mailbox, that did not start ("Nothing was
    searched: No objects were selected.") and for nobody to add; **done** "Added to the recipients: mailboxes - 9, owners - 9.",
    said by the recipients page as well, which is the one in front after the switch; **error that lasts** - the search failed
    and the list is empty because of it - with *Retry*, until the next search starts. **Podgląd:** "Building the preview in
    Outlook…" with Stop; "The preview could not be read from Outlook: reason. Press Retry to try again. (NotResponding)"; the
    card, its findings and its information line are the content. **Wysyłka:** **working** "Sending messages: 12 of 95 · about
    4 min left…" ("Saving drafts: …", "Sending the test: …") with the part done in the ring and Stop; **done** "Sending finished:
    95 succeeded, 0 failed. Each message is listed under Results."; **attention** "… 2 failed. Press “Retry failed” to try them
    again.", "Sending stopped: 12 succeeded, 0 failed, 83 not sent. Press “Send” to send the rest: …" and "… Stopped after 5
    failures in a row: check Outlook, then press “Retry failed”."; **error** when Outlook stopped the run ("…: reason. Check
    Outlook, then press “Retry failed”. (Disconnected)"); a press that came to nothing - "Nothing was sent: There is nobody to
    send to…", "Nothing was sent: Outlook cannot be used: … (NotResponding)"; and the state that lasts - Outlook is not
    available. **Odpowiedzi:** at rest "Runs found: 1 - choose one and read its responses." or the summary of the votes; **working**
    "Searching “Sent Items”…" and "Reading the responses…" with Stop; **error** "The responses could not be read: reason (code)"
    with *Retry*; **attention** for a read that was stopped, with *Retry*; the state that lasts - Outlook is not available.
    **Wiadomość:** nothing - the line is empty, the page has notes only. **The window itself:** that the journal "already
    sent" was started again (a result that can be closed, the path of the old one on screen only), that it cannot be used (the
    state that lasts) and that the signatures could not be read.
  - *How the old places were sorted* (1 error bar, 1 warning bar, 3 progress bars, and the sentences that said the same).

    | Old place | Kind | Now |
    |---|---|---|
    | progress bar in the footer (`IsBusy`: the window reads its settings, the journal, the last draft, the signatures) | busy, no sentence | a ring in the footer (14 px), the same flag |
    | text of the footer (`StatusText`: the last sentence of the window and of the recipients and mailboxes pages) | status | the results of the pages and of the window, in the line; the property keeps the sentence |
    | warning bar "the session ended" | status | the state that lasts of the recipients and the mailboxes pages |
    | error bar `Error` (the journal cannot be used; the objects the window was opened with could not be read) | status | the journal: the state that lasts of the window; the objects: an error of the recipients page, with *Retry*; the property keeps the sentence |
    | progress bar and text of the owner search, and its *Zatrzymaj* | status | the search in the line with Stop; *Znajdź właścicieli* is the action button |
    | progress bar and text of the send page, and its *Zatrzymaj* | status | the run in the line with the part done and Stop; the four buttons that start a run are action buttons; the tally (sent, failed, skipped) stays - it belongs to the list of results |
    | counts of the recipients, the matches and the responses | summary | `SetIdle` of the pages; the text blocks are gone |
    | `SummaryText` of the responses while it searched, read or failed | status | the operation and the error of the page |
    | what pasting left out, what is wrong with the signature, the findings of the preview, "from Outlook" | note | `Ad.Note.Attention` and `Ad.Note.Info`, in their places |
    | dialogs: the directory read failed, files that could not be read, no Downloads folder or file, nothing to send, Outlook cannot be used (send), no owner to add, the preview from Outlook failed | status | results in the line |
    | validation errors before a send ("The message cannot be sent yet: • …"), the address of a test that is not valid, errors of saving files and templates, the journal report, the questions | dialog | unchanged |

  - *Decisions.*
    - **One line, the page in front; the message page has none.** The window owns a holder of its own (the journal, the
      signatures) and `Status.Front` follows `SelectedTab`, also for the tab the window starts on (the property raises no
      change for its default). Every page that has an operation, a result or a state of its own owns a holder created from
      the services it already gets (`services.Lists.StatusLog`); the message page says nothing that is not a note, and the
      templates, imports and exports keep their toasts and dialogs (a left-behind item below).
    - **The end of the session is the pages', and the line goes straight to it.** `CorrespondenceViewModel.OnSessionEnded`
      hands the state to the recipients and the mailboxes pages first (the sentence of the window and "Sending through Outlook
      still works."), then ends what runs: the reads of the directory end through the token that was already there, and the
      search through `MailboxOwnersPageViewModel.OnSessionEnded`, which disposes its operation - so the line goes from "working"
      to the state with no "Stopping…" and no message of a stop that nobody asked for (the old `CancelCommand.Execute` would have
      said it). A test asserts that the line changes exactly once.
    - **The state of Outlook is computed from the connection and said where it is needed.** `OutlookConnectionViewModel.UnavailableStatus`
      is an attention that cannot be closed - not installed, not supported here, the connection failed or was lost - with the
      reason of the failure and *Connect to Outlook* as the action unless connecting again cannot help; the send and the
      responses pages put it in their `UpdateLasting` when the connection changes, and the window repeats it after a language
      switch. It is null while Outlook is being connected. The preview does not need Outlook, so it says nothing until its own
      button is pressed.
    - **A state of the window is set at its first moment.** The journal is loaded once, when the window opens. A state that a
      window sets later - while a page runs something - is what was said last and takes the line from the page (the rule of
      foundation 3), so such a state belongs to the page it is true for.
    - **The send run is one operation however it was started, and Stop does not give control back before the message that is
      in flight.** `RunAsync` begins it after the confirmation, with "0 of N" and the ring at zero; each report of the sender
      moves the text and the ring ("Sending messages: 12 of 95 · about 4 min left…", the estimate from the sender, the key of the
      sentence and the two numbers in the log copy). Its token is the one of the scope, linked to the source the run used;
      `StopCommand`, the line and the pressed button are the same Stop (`Status.Operation.Cancel()`), which says "Stopping…" at
      once. The run is not abandoned with a `WaitAsync`: the operator wants the count of what went, so it ends when the message
      that Outlook is handed returns, and a test holds that message to look at "Stopping…". How it ended is `CompleteRun`: no
      failure and nothing left - done; failures, a stop, five failures in a row - attention with the button to press next
      (never a link: a link that repeats a send would skip the confirmation); Outlook stopped it - error, with the kind of the
      failure as its code. The four buttons are action buttons with their own flag over the one holder (`IsTesting`,
      `IsCreatingDrafts`, `IsSendingMessages`, `IsRetrying`), so each shows *Cancel* only while its own run goes on; the others
      are disabled by `CanRun`. The toast after the run, the results list, the audit entry and the journal notes are as they
      were; the sentence of the line is the closing line of the results plus where the results are.
    - **The reads of Sent Items and the preview return control at once and drop a late answer.** The call is not told to stop
      (the preview deletes the item it builds, as it always did): the page waits with `WaitAsync(token)`, and an answer that
      arrives in the moment of Stop is not shown - `ThrowIfCancellationRequested` after the await - so "the list was not
      changed" is true.
    - **Quick actions never end a read.** `ReportStatus` of the recipients page sets the old `StatusText` always and shows
      the result in the line only while the page is not working; `Status.Show` replaces a running operation, and pressing
      *Clear exclusions* must not cancel an import.
    - **Reading files is one operation with Stop.** `Task.Run(…).WaitAsync(token)` per file, "Reading the files: 2 of 5…" with
      `PageProgress.Of`; the rows of the files read before a stop stay on the list and the line says how far it got; the thread
      that parses runs to its end and its result is dropped. A small test seam, `RecipientsPageViewModel.ReadGate`, holds the
      read on its thread (the unit-1 pattern).
    - **The search for the owners lasts when it failed and is quiet when it was refused.** Facts - the reason, the number of the
      limit, the code - are kept and the sentence is built in `UpdateLasting`, behind "the session ended"; a refused press (no
      objects selected, an attribute that cannot hold a name) is an attention that can be closed, in the sentence "Nothing was
      searched: reason". `Stop` of the search and of the read of the owners say "stopped" only for the operator's Stop.
    - **What was a dialog about an operation is its result.** The directory read says its own failure and rethrows for the
      window, which keeps the old `Error` for the request it was opened with; the files that were not read are one sentence
      with what was read (a dialog is a place for a decision, and a list of reasons that must be acknowledged is read in the
      line as well, whose whole text is the tool tip); "no Downloads folder", "nothing to send", "Outlook cannot be used" and
      "no owner to add" are the answer to a press, built with `PageStatusText.Failure(what was not done, why)` and the key of
      the reason as the log copy.
    - **The preview's failure is no finding.** `Warnings` held the failure of "Podgląd z Outlooka" in place of the findings of
      the card, and `OkText` was emptied; now the failure is an error of the line with *Retry* and the card, its findings and its
      "looks good" text stay as they were.
    - **The model.** `ResultCode.Outlook(OutlookFailureKind)` and the mapping of `OutlookException` in `ResultCode.Of`: the
      kind of a failure the program names is allowed in a log copy, the code text, the step and the HRESULT of the failure are
      not read. Log copies are keys, counts and that code: no address, name, subject, path, file or mailbox.
  - *Where this differs from the recipe.* The brief asked for `SessionEndedStatus` as the window's first condition; it is not
    used, for the reason above (the end of the session is a state of two pages), and Outlook is a state of two pages, not of the
    window. The window is 36 px higher by default (900 to 936, minimum 680 to 716). The message page has no holder. Behaviour
    that changed beyond how a result is said: the failure of the preview is no longer in `Warnings`; the directory read can
    be stopped and says its failure itself; importing files can be stopped; the end of the session ends the owner search
    without a "stopped" message; seven Presentation tests and three UI assertions were adapted (below).

- **Tests.** Presentation tests run on Linux and are the bulk; UI tests run on Windows CI only and were written without a
  screen (they assert states that last until the test changes them, and look at "Stopping…" only while a held call is on its way).
  - *Unit 1.* Presentation (35, on Linux): `BulkCreate/BulkSourceStatusTests` (12: the paste and the file with the sequence
    of the line in both languages, the name of a file never in a log copy, Stop of a paste and of a file with a parse held on
    its thread and released, a clipboard without a table, files that cannot be used - too large, not a sheet, not there -
    with the table of before staying, a header row out of range, a column over the limit closed by the next action, the
    repeated-paste question as a note, the summary after a language switch, closing the window during a read),
    `BulkRulesStatusTests` (7: nothing at rest, why *Dalej* is not possible is not status, a profile that cannot be saved, the
    results of save / default / delete with no name in the log copy, a built-in profile as the default, the limit of
    profiles, the account that can join computers - not found, a failing read, closed by the next pick -, the sentence
    back when the step is shown again), `BulkCheckStatusTests` (11: the part done from the first row with its fractions in
    both languages, no cell in a log copy, Stop with a read held on its way to the domain controller - "Stopping…" at once,
    the stopped state and its action -, `StopCommand` as the same Stop, the two buttons' flags and their Cancel, a failed
    check with Retry, incomplete input, the stale state in both languages and its action, an edit that replaces a running
    check without a word, the end of the session during a check - straight to the end, nothing from the call that returns
    later -, closing the window), `BulkCreateStatusTests` (5: the chain window → wizard → page through the steps, the
    preview and *Back*, closing, the wizard's own holder empty, and the creation as the execution of the window - the canary
    and the rest add up from "0 z 5" to "5 z 5" and the result names the domain). `BulkCreateBatchEditorTests`, `BulkCheckTests`,
    `BulkSourceTests`, `BulkRulesTests` and `BulkCreateProfileTests` are unchanged. UI (Windows CI, written without a screen):
    `BulkCreateStatusUiTests` (5: one line in the whole window and the summary in it, the notes of the table, the answer to a
    profile command and the note of the rules - with `GrowthOf` -, the check in the line with Stop and the button as its
    Cancel - the grid moves by as much as the line grew -, the notes of the check and of the results). Adapted where the
    place of a sentence changed: the two UI tests that read the summary of a step from a text block read it from the
    line (`BulkCreateInputUiTests`, `BulkCreateWindowTests`), and `BatchEditorStatusUiTests.AssertNoOldFeedback` no longer
    exempts the views of the wizard. Source rules: the six views on `ConvertedViewsWithoutAStatusLine`; the expressions of
    `XamlSourceTests` for both lists, the `Ad.*` keys, the closed set of Fluent brushes, the motion rule and "every
    `StaticResource` key is defined before its first use" were proven on Linux with a throwaway script.
  - *Unit 2.* Presentation (53, run on Linux): `Readiness/ReadinessStatusTests` (9: the first check with every area
    and how far, the full check in both languages, Stop with control back at once and the stopped state with its action,
    `CancelCommand` as the same Stop, a reconnect that ends the run without a word and the connection-changed state - in both
    languages -, a tab without a session, the window that disconnects after a check, closing the tab during a check with no
    area in a log copy), `EventLogs/EventLogStatusTests` (16: the reader's sequence from "what to do first" to the summary, in
    both languages and with the selection joined, Stop with what was read kept and its Refresh, the sources that were not
    read named by the line and their reasons left in the rows, the channels as an operation that leaves the summary, the
    flags of the two start buttons, the window that cannot read event logs, not connected, the failed logons and the lockout
    with their stages, a stopped analysis, an action that fails never over a run, an account that is no account, a failed read
    with Retry, closing a tab during a run), `Computers/ComputerReviewStatusTests` (15: the summary with the selection, the
    check with the scope and the computers read so far and no name in a log copy, the scope as the operator chose it, Stop and
    the same Stop from `StopCommand`, a failed check and a scope that cannot be resolved, the exact last logon with the
    controllers that did not answer, its Stop with the list unchanged, the sites stopped and how far they are, the ping as an
    operation with its result in the line and a computer without a DNS name, the end of the session over what runs, a window
    without a session, closing the window during a check), `Computers/RemoteComputerStatusTests` (12: the connection as the
    summary, a read that ends as the summary, Stop of a read and its button as the action, `StopCommand` and a Stop during the
    ping, an action that ends as done and a stopped one that says it is not known whether the computer got the request, a read
    that fails with Retry and an action that fails without one, Polish, no DNS name, no remote client, the end of the session,
    closing the window, the language switch) and `ComputerActionsTests` (+1: `FindTargetAsync` says why there is no target with
    no dialog, `TryPingAsync` hands back the outcome with no toast). UI (Windows CI, written without a screen, 15):
    `ReadinessStatusUiTests` (3: one line under the header with the summary and the notes as a note, the area of a full
    check in the line with the button as its Cancel and the stopped state with *Check again*, the end of the connection),
    `EventLogStatusUiTests` (5: one line under the header of every document with its run button as an action button, a run
    with its sources and events and a stopped run with *Refresh*, a source that was not read named by the line and its
    reason in its row, the window that cannot read event logs in the three documents, the channels button), `ComputerStatusUiTests`
    (7: the review's one line at the top with four action buttons, a check with the scope and Stop as the button, a failed
    check and the end of the session, the remote window's one line and its six action buttons, a read with its Stop, an
    action that fails without a link, the window without a DNS name and the restart dialog with a note and no banner); every
    place that grows with a result is measured with `GrowthOf`. Adapted where the place of a sentence changed:
    `ComputerReviewViewModelTests` (3 tests read the result of the exact last logon, the sites and the subnets from the line
    instead of the toasts), `ReadinessUiTests`, `EventLogDocumentsUiTests` (the not-supported banner is the state of the line),
    `ComputerWindowsTests` (the status and the connection are read from the line), `PageStatusTests` (the code of
    `ResultCode.Remote`). Source rules: the six views on `PagesWithAStatusLine` and `RestartComputerWindow.xaml` on
    `ConvertedViewsWithoutAStatusLine` (both lists stay sorted ordinal); the expressions of `XamlSourceTests` for both lists,
    the `Ad.*` keys, the closed set of Fluent brushes, the motion rule and "every `StaticResource` key is defined before its
    first use" were proven on Linux with a throwaway script, and the `App.UiTests` project builds without a warning.
  - *Unit 3.* Presentation (33, on Linux; `Pki/PkiStatusTestSupport.cs` has the harness - the window's PKI services over
    the fake directory with a cache whose calls the test holds, fails or lets report - and a held call that does not hear the
    token until the test releases it, so "Zatrzymywanie…" stays on the page until the call returns): `Pki/PkiOverviewStatusTests`
    (13: the summary in both languages with the selection and the tab, the read as an operation with its stage and counts and no
    name in a log copy, Stop with control back at once and the stopped read that lasts with its action, a failed read with Retry
    and its code, not connected and the language switch, a newer read that replaces the running one without a word, the health
    tab in front with the HTTP check - the part done, Stop, the stopped result with its action, then the rows and the summary -,
    a failed HTTP check that can be closed, the LDAPS probe as the operation of its tab, a probe that cannot be made as the state
    of the tab until the next probe, the probe a read makes as a stage of the read, every log copy of the three holders, and the
    status bar of the main window saying nothing of a PKI tab), `PkiTemplatesStatusTests` (6: the summary with the counted accounts
    in both languages, the stages including the account pass, Stop during the account pass with the templates kept, accounts that
    could not be counted as a state with Retry in both languages, a failed read, the log copies), `PkiReadinessStatusTests` (7: the
    summary in both languages, the stages with the areas that wait for the accounts saying "reading…", Stop and the areas the read
    did not reach saying "not read", the probe with Stop and the area keeping what it showed, a probe that failed with its row and
    Retry and a read that replaces a probe silently, not connected, the log copies), `PkiSecurityReviewStatusTests` (7: the summary
    with the selection and the filter in both languages, the check with its stages in order, Stop through the line and through
    `CancelCommand` with the findings of before kept and the sentence in both languages, a first check that was stopped, a failed
    check with Retry and the findings kept, not connected, the log copies). UI (Windows CI, written without a screen, 7):
    `PkiStatusUiTests` (one line under the header of each document with the summary said once and no old banner, the three notes of
    the overview with their styles, the action buttons over the right commands and holders, a held read of the overview with
    Stop - "Stopping…" observed through the change of the status - and the stopped read with *Refresh* and the growth of the
    line, the HTTP check with a gated probe: Stop, the stopped result, the rows and the summary and the note gone, the templates'
    line and note, the readiness probe with a gated TLS probe, the security review with one button and no *Anuluj*, and the end of
    the connection). Adapted where the place of a sentence changed: `PkiSecurityReviewTests` (the stage texts of the check),
    `PkiOverviewUiTests` and `PkiSecurityUiTests` (the summary is read from the line). Source rules: the four views on
    `PagesWithAStatusLine` (the list stays sorted ordinal); the expressions of `XamlSourceTests` for both lists, the `Ad.*` keys, the
    closed set of Fluent brushes, the motion rule, "every `StaticResource` key is defined before its first use" and the
    localization keys were proven on Linux with a throwaway script, and the `App.UiTests` project builds without a warning.
  - *Unit 4.* Presentation (48, on Linux; `WatchdogHarness` takes an optional `ListLogger<PageStatusHolder>` for the log
    copies, and the fake client's `Hold`, `HoldEvents` and `FailNext` hold, delay and fail the calls - a held call hears the Stop, so
    "Zatrzymywanie…" is asserted on the same thread before the call returns): `Watchdog/WatchdogDocumentStatusTests` (13: the
    summary of the page shown in both languages with the selection, the connection attempt as an operation without Stop and the line
    going straight to the page - the feed read is held to see it -, five kinds of connection problem as the state that lasts with
    Retry, its code and a log copy with no host, an untrusted certificate with "Sprawdź certyfikat…" as the one action, no address,
    interrupted live updates until they recover and in Polish, the problem in the language of the moment, a page that waits in its
    holder until it is shown, closing the tab with a read that still runs), `WatchdogEventsStatusTests` (11: the read with Stop and the
    summary, Stop with the feed empty and paused and Refresh, a failed read in both languages with Retry and a log copy with no event,
    a newer read that replaces the running one without a word, older events with their count / Stop / a failure that can be repeated,
    the acknowledgement of 1,200 events with the part done and no Stop and its log copy, a failed acknowledgement - also one that fails
    after a read took the line, which is a toast -, the answers of the
    details pane in the line and the next selection closing them, the read of the details as a ring and a note),
    `WatchdogConfigStatusTests` (9: the version as the summary in both languages, the read with Stop and a stopped read as the state of
    the tabs with *Wczytaj ponownie*, a failed read as a state and then as a result, the validation with its three results and Stop,
    a save that is a write without Stop - with a typed password never in a text, the recorder or the log -, a save the service
    refused, the conflict as the state of the tabs in both languages, another failure of a save without an action, the status of the
    configuration in front of every tab that edits it), `WatchdogPagesStatusTests` (7: the digest preview with Stop / a failure /
    the old preview kept, "Wyślij teraz" in Polish, a preview prepared while the configuration is saved, the preview of a rule with
    Stop and a failure and its end when another rule is selected, "reset to default" saying why instead of a dialog, a delivery
    test through its five endings with no address and no host in the log, a test that replaces another),
    `WatchdogServiceStatusTests` (8: the three reads one after another, a viewer reading the status only, Stop of the status read,
    a failed read as a state and then as a result, the delivery log with Stop / a failure / the retry of a dead delivery - and one that
    fails after a read took the line, a toast -, the audit, the status page in front). Adapted where the place of a sentence changed: `WatchdogRulesTests` (the reset of a rule the service
    no longer ships is read from the line, no dialog), and the tests that read the "Stan" tab through the document (`ServiceStatus`).
    `PageStatusTests` got the assertions of the new factory. UI (Windows CI, written without a screen, 7):
    `WatchdogStatusUiTests` (one line under the card with the summary said once and no old banner on any page, a held read of the
    feed with Stop and the stopped read with Refresh and the growth of the line, a connection problem with Retry and the card keeping
    the host, the save with the content moving by as much as the line grew, the test button as an action button with the stages in
    their rows, the notes of the save bar, the ring and the note of the details pane); `WatchdogViewTests` reads the certificate
    button, the card and the rollout note from their new places. Source rules: `WatchdogView` on `PagesWithAStatusLine` and the
    twelve pages and the trust window on `ConvertedViewsWithoutAStatusLine` (both lists stay sorted ordinal); the expressions of
    `XamlSourceTests` for both lists and "every `StaticResource` key is defined before its first use" were proven on Linux with a
    throwaway script, and the `App.UiTests` project builds without a warning.
  - *Unit 5.* Presentation (49 new, on Linux): `Correspondence/CorrespondenceStatusTests` (8: the tab in front and what each one
    says - also for the tab the window starts on, which raises no change -, a tab that said something while it was not shown
    says it again, the journal that cannot be used as the state of the window in both languages and not covering what a
    page says, the restarted journal as a result with the path only on the screen, the end of the session not said by the
    window while a send goes on, the window that is closed, and no page that does not need Outlook saying it is not there),
    `SendStatusTests` (15: a run - "0 of 3" first, then the part done in the ring and the estimate, a done that says where the
    results are, the line never going through the summary, the result closed -, Stop with a message that is held ("Stopping…"
    at once, then the attention with what to press), `StopCommand` as the same Stop, a failed message, Outlook that stops the
    run with the kind of the failure as the code and the state that lasts under it, five failures in a row, drafts in both
    languages, the flags of a test and of a retry, the presses that came to nothing, the log copies with no address, subject
    or name, closing the window during a run, the state of Outlook following the connection and the language, and without
    classic Outlook), `MailboxesStatusTests` (10: the search with its counts and the summary after it, Stop with a held read -
    "Stopping…" at once, the attention with *Retry* -, a failed search as the state with its code, the two limits, the presses
    that came to nothing, the owners that are added - the result on both pages -, Stop of that read, the end of the session
    during a search - exactly one change of the line -, the log copies), `ResponsesStatusTests` (5: the two reads and their
    summaries, the flag of each, Stop with a held read and an answer that arrives anyway, a failed read with its code, a press
    without Outlook), `RecipientsStatusTests` (7: the counts and the language, a file with its working and done and no name in
    the log copy, several files with how far they are and the files that were not read as one sentence, Stop of a load with
    the thread held and released, the directory read with Stop and *Retry*, the end of the session during a read, a quick
    action that does not end a read) and `PreviewStatusTests` (4: the preview as an operation, Stop with an answer that arrives
    anyway, a failed preview as an error with *Retry* and not as a finding, a press without Outlook); one block of assertions
    in `PageStatusTests` (`ResultCode.Outlook` and the mapping of the exception, never the code text or the step). Held calls:
    a held Outlook call is `BeforeSend` or `BeforeRead` blocking a pool thread (a read of Sent Items is started with
    `Task.Run`, because the fake calls its hook on the caller's thread), a held directory call `HeldDirectoryCall`. Adapted
    where behaviour or the place of a sentence changed (7) - each reads the line where it read a dialog or `Warnings`:
    `SendPageTests.Validation_NoRecipients_AndOutlookUnavailable_StopBeforeSending`,
    `MailboxOwnersReviewTests.AddToRecipients_WithoutAReadyOwner_SaysSo`, two of `RecipientsImportTests` (the files that cannot
    be read, the Downloads folder), `RecipientsDirectoryTests.DirectoryErrors_AreSaidInTheLine_WithRetry` and two of
    `PreviewPageTests`. UI (Windows CI, written without a screen, 7): `Correspondence/CorrespondenceStatusUiTests` (one line
    above the tabs with the counts of the page in front and no second place for them, the notes with their styles and the
    line empty for them, a run of the send page - four action buttons over one holder, the button that started it as the
    Cancel, "Stopping…" while the message is held, the attention and the content moving by as much as the line grew,
    the result closed -, the owner search stopped by its button with *Retry* in the line, the two reads of Sent Items, Outlook
    that cannot be used on the send and the responses pages with its way out, the end of the session on the recipients tab and
    not on the send tab). Adapted: three assertions of `CorrespondenceWindowTests` that read the footer text, the progress text
    of the send page and the summary of the mailboxes read the line. Source rules: the window on `PagesWithAStatusLine`, the
    report and the prompt on `ConvertedViewsWithoutAStatusLine`; the expressions of `XamlSourceTests` for both lists and "every
    `StaticResource` key is defined before its first use" were proven with a throwaway script on Linux, and the `App.UiTests`
    project builds on Linux without a warning.
- **What is left** (counted in the XAML, comments excluded; the final state of V2b-4, after unit 5): **no `Ad.ErrorBar`, no
  `Ad.WarningBar` and no progress bar that says status in any view.** The XAML has them only as the definitions of the two styles in
  `Themes/Controls.xaml` (they leave with V2b-5) and as the one progress bar of the row of an activity in `MainWindow.xaml`,
  which stays. **Forty-five views have a status line and twenty-nine converted views none** - the two lists of `XamlSourceTests`
  hold each other out and no view with a line is missing from the first. V2b-4 took 10 error bars, 19 warning bars and 23 progress bars
  (by unit: 3 + 5 and 2; 2 + 7 and 7; 2 + 2 and 4; 2 + 4 and 7; 1 + 1 and 3). The counts at the end of each unit, and what each unit left
  behind on purpose (the strings and members were added to *After V2b-1*, nothing was removed):
  - *Unit 1, at the end of that unit* (counted in the XAML, comments excluded): **7 uses of `Ad.ErrorBar` and 14 of `Ad.WarningBar` in
    21 view files, 22 progress bars** (one is the row of an activity, which stays). By area: `Views/Watchdog/` 2 + 4 and 7 in
    9 files; `Computers/` 2 + 3 and 3 in 2 files; `Pki/` 2 + 2 and 4 in 4 files; `EventLogs/` 0 + 3 and 3 in 3 files;
    `Correspondence/` 1 + 1 and 3 in 1 file; `Readiness/` 0 + 1 and 1 in 1 file. Thirty-three views have a status line and
    thirteen converted views none. Left behind on purpose (added to *After V2b-1*): the strings `BulkCreate.Reading`,
    `BulkCheck.Computing` and `BulkCheck.StaleNote`, which have no user any more; no longer shown but read by tests or by the
    view models themselves - `Error` of the source and of the check, `ProfileStatus`, `OptionsError`, `ComputedRows` and
    `TotalRows`, and `StopCommand` of the check, which has no view. The three views that were notes already (`MappingError`,
    the scope of the check, the first free number) were not renamed.
  - *Unit 2, at the end of that unit* (counted in the XAML, comments excluded): **5 uses of `Ad.ErrorBar` and 7 of `Ad.WarningBar` in
    14 view files, 15 progress bars in 15 view files** (one is the row of an activity in the main window, which stays).
    By area: `Views/Watchdog/` 2 + 4 and 7 in 9 files; `Pki/` 2 + 2 and 4 in 4 files; `Correspondence/` 1 + 1 and 3 in 1 file -
    the three areas that are left. **Thirty-nine views have a status line and fourteen converted views none.** Unit 2 took 9
    banners and 7 progress bars: of the 2 error bars both were status (the failed call of the review and of the remote window),
    of the 7 warning bars 6 were status (the end of the session in the two windows, the three "not supported" bars of the
    event logs, the changed connection of the readiness tab) and 1 a note (the age of the release catalogue); all 7 progress
    bars were status. What never was a banner: the cancelled-check sentence and the stale hint of the readiness tab (states
    that last), the card "Read only…" (a note), the validation sentence of the restart dialog (a note), the exact last
    logon, the sites and the ping (toasts and a dialog, now results of an operation). Left behind on purpose (added to *After
    V2b-1*): the strings `Readiness.Stale`, `EvtLog.Cancelled`, `EvtLog.StatusReading`, `EvtLog.LoadingChannels`,
    `EvtFail.StageFailures`, `EvtFail.StageSuccesses`, `EvtFail.StageAnalysis` and the four `EvtLock.Stage*`, which have no
    user any more (the stages are `EventLogsStatus.*` now); no longer shown but read by tests or by the view models
    themselves - `ProgressText` and `ProgressValue` of readiness and `ProgressText` of the three documents (fed by the same
    stage), `WasCancelled` and `IsStale` of readiness (the state is computed from them), `Error`, `StatusText`, `SelectionText`,
    `IsReading`, `AsOf` and `StopCommand` of the review, `ConnectionText` and `Error` of the remote window, `LoadedText`,
    `IsSupported` and `NotSupportedText` of the event log documents (the last is the sentence of the line). No command lost
    its last user: `Cancel()` and `CancelCommand` of the documents are the same Stop as the line's.
  - *Unit 3, at the end of that unit* (counted in the XAML, comments excluded): **3 uses of `Ad.ErrorBar` and 5 of `Ad.WarningBar` in 6 view
    files, 11 progress bars in 9 view files** (one is the row of an activity in the main window, which stays); **43 views have a
    status line and 14 converted views none.** By area: `Views/Watchdog/` 2 + 4 and 7 in 9 files and `Correspondence/` 1 + 1 and 3
    in 1 file - the two areas that are left. Unit 3 took 2 error bars and 2 warning bars and 4 progress bars: both error bars were
    status (a failed read), both warning bars notes (a CA outside NTAuth, rights that were not evaluated), all four progress bars
    status. What never was a banner: the summaries and "as of" texts (status line), the text of the HTTP check and of the LDAPS probe
    (the line of their tab), the failure of the account count (a state), the cards of the notes (notes). Left behind on purpose (added
    to *After V2b-1*): the strings `Pki.ReadProgress`, `PkiEsc.StageSnapshot`, `PkiEsc.StageAccounts`, `PkiEsc.StageAccessControl`,
    `PkiEsc.StagePrincipals`, `PkiEsc.StageAnalysis`, `PkiReady.StageSnapshot`, `PkiReady.StageDomainControllers`,
    `PkiReady.StageAccounts` and `PkiReady.StageLdaps` have no user any more (the stages are `PkiDoc.Status.Stage*` now); no longer
    shown but read by tests or by the view models themselves - `ProgressText`, `StatusText`, `SelectionText`, `Error`, `SummaryText`,
    `ReadAtText`, `CountStatusText`, `LastRunText`, `HealthSummaryText` and `LoadedText` of the four documents (the texts of the line
    are computed from them) and `Cancel()` / `CancelCommand` of the security review, which are the Stop of the line.
  - *Unit 4, at the end of that unit* (counted in the XAML, comments excluded): **1 use of `Ad.ErrorBar` and 1 of `Ad.WarningBar` in 1 view
    file, 4 progress bars in 2 view files** (one is the row of an activity in the main window, which stays); **44 views have a
    status line and 27 converted views none.** The one area that is left is `Correspondence/` (1 + 1 and 3 in 1 file). Unit 4 took 2
    error bars, 4 warning bars and 7 progress bars: of the 13, 7 were status (the failed read of the feed and the progress of the
    feed, of the save bar, of the digest, of the rule preview, of the delivery test and of the status tab), 5 notes (the findings of
    the service, why the editors are read only, the signing secret that is shown once, the hints of a delivery test and the rollout
    mode) and one a busy mark (the read of the details pane). What never was a banner: the result and error texts of the save bar, the
    digest, the delivery test, the status tab, its delivery log and audit, and the details pane (status), the version of the
    configuration (summary), the status bar's text of the feed (summary), the card's state and explanation (status). Left behind on
    purpose (added to *After V2b-1*): no string lost its last user in this unit; no longer shown but read by tests or by the view
    models themselves - `Error`, `Message`, `PreviewError`, `ActionMessage` of the pages, `LoadedText` / `StatusText` / `SelectionText`
    of the tab and of the feed, `BannerMessage`, `StateText` and `ConnectionMessage` of the tab, `SummaryText` and `Error` of a delivery
    test (and `CancelCommand`, which is the Stop of the line), `HasConflict` and `Error` of the configuration; `ReviewCertificateButton`
    and `RetryButton` of the card went with their view elements.
  - *Unit 5, at the end of that unit* (counted in the XAML, comments excluded): **no use of `Ad.ErrorBar` and `Ad.WarningBar` in
    any view and one progress bar in the whole application - the row of an activity in `MainWindow.xaml`**; **forty-five views have
    a status line and twenty-nine converted views none.** Unit 5 took 1 error bar, 1 warning bar and 3 progress bars: both bars were
    status (the journal that cannot be used; the end of the session), of the three progress bars one was busy without a sentence
    (the window loads: a ring) and two were status (the owner search, the run). What never was a banner: the counts of the
    recipients and of the matches, the summary of the votes (status line), the text of the footer and of the progress texts (the
    results and operations of the pages), the dialogs of the operations (results), the sentences about a paste, a signature and
    the findings of the preview (notes). Left behind on purpose (added to *After V2b-1*): the strings
    `Correspondence.Preview.OutlookFailed`, `Correspondence.Recipients.ImportTitle`, `Correspondence.Recipients.DirectoryTitle` and
    `Correspondence.App.Send.Progress` have no user any more; no longer shown but read by tests or by the view models themselves -
    `StatusText` and `Error` of the window, `StatusText` of the recipients and mailboxes pages, `ProgressText`, `SummaryText` and
    `Error` of the mailboxes page and `ProgressText`, `ProgressValue` and `ProgressMaximum` of the send page (the text of the line is
    built from the same facts), `SummaryText` of the responses page (it still holds the sentences of the reads), and
    `CancelCommand` of the mailboxes page and `StopCommand` of the send page, which are the Stop of the line. Unit 5 changed what
    the pages said in a few places beyond how a result is said (above), and the controls and the model by nothing but
    `ResultCode.Outlook`.
- **Not seen, to confirm on Windows.** Nothing of V2b-4 was seen on a screen. By unit:
  - *Unit 1.* Nothing of this unit was seen on a screen.
    - *The line over the wizard.* It is the line of the batch editor, so it is there at rest in every step, empty on
      *Kolumny* (an empty band of 28 px, the open question of the empty line) - it was there since V2b-1, empty in this mode.
    - *The toolbars.* *Dane* without the bar and the bold summary at its right; *Sprawdzenie* with two action buttons next to
      the plain *Następny błąd* and *Kopiuj wiersze z błędami* (as wide as "Zatrzymywanie…" needs: "Sprawdź ponownie" is
      wider, "Numeruj od nowa" is not), the scope of the check right under the filter row where the bold summary stood.
    - *The notes.* The reading warnings in one note with their own scroll (88 px), glyph in the first line; the repeated-paste
      note with three buttons at its right, which wraps in a narrow window; the note of *Nazwy i opcje* above the sections.
    - *Long results.* "Nie wczytano pliku „…”: Plik jest większy niż 50 MB. Wybierz inny plik." with a long file name, the
      stopped-check sentence (about 150 characters) and the stale sentence wrap to two or three lines and move the wizard
      down with them.
    - *Coming back from the preview.* The stale state of the check shows for the moment between *Back* and the recompute that
      the execution scheduled (300 ms); with a result of the line closed or not.
    - *The profile commands* answer in the line at the top of the window while the buttons are at the bottom of a long step.
    - *Open for the owner:* whether the three source buttons become action buttons (Stop is in the line only); whether
      *Podgląd* should refuse, or compute again, after a check that was stopped (rule 5 says how a plan is made is not this
      increment's business); whether the profile commands' answers belong next to the buttons as notes.
    - The pictures `40-bulk-create-data.png` to `43-bulk-create-check.png` are made by the screenshot tests but are not
      committed or shown in the READMEs; they change with this unit (the summary in the line, the notes, the two action
      buttons) - the line itself was there since V2b-1, empty in this mode.
  - *Unit 2.* Nothing of unit 2 was seen on a screen either.
    - *The computer review.* The line sits at the top of a window that is 36 px higher by default; it is an empty band before
      the first check (the open question of the empty line). The four buttons are action buttons - "Zatrzymywanie…" is wider
      than their labels, so the row may wrap in a narrow window; the long results (the exact last logon with the names of
      the controllers that did not answer, the subnets that were skipped) wrap to three lines and push the list down; the
      note about the release catalogue keeps its place between the scope and the parameters.
    - *The remote window.* The six start buttons wrap onto a second row (their margin is 6 px at the right and at the bottom,
      as in the old toolbar); "not known whether the computer got the request" is the longest sentence of the window; the
      action log under it is unchanged and says "Cancelled." as before. The restart dialog has a note where a hand-made
      caution glyph and a secondary-colour text were.
    - *The event log documents.* The line is under the header and above the query; the run button is next to the query and
      is as wide as "Zatrzymywanie…" needs, the "Load channels" button beside the channel box is small and may be cut in
      Polish; a run with sources that were not read names three of them and the panel "Coverage and access" opens beside it,
      so the same fact is on the screen twice, once as a count and once as rows.
    - *The readiness tab.* The ring in the line shows how many areas are done; "Check again" is one link for two different
      causes (stopped, changed connection); the summary is long ("OK: 20 · Warnings: 3 · … · Last check: full, … on dc01…")
      and is cut in a narrow tab with the rest in the tool tip.
    - *Open for the owner:* whether the review's exact last logon, sites and ping belong in the line as operations at all (a
      toast was gone after a few seconds, the line stays until the next thing - a result that nobody closes may be read as
      "still true" long after); whether a stopped remote action should offer a link that reads the status of the computer;
      whether the sources that a run could not read should be named by the line when their rows say so already.

  - *Unit 3.* Nothing of unit 3 was seen on a screen either.
    - *The four documents.* The line sits between the header and the notes; the overview's summary is long ("Urzędy
      przedsiębiorstwa: 2 · szablony: 26 · … · odczytano z dc01.contoso.test, 13.10.2026 14:00 · <CA>") and is cut in a narrow tab,
      with all of it in the tool tip; the same for the readiness report. The refresh buttons and the two probe buttons are action
      buttons as wide as "Zatrzymywanie…" needs (the report's header has four buttons in a wrapping row; the review's *Sprawdź
      ponownie* is the accent button and is wider); *Sprawdź ponownie* of the review has no *Anuluj* beside it any more.
    - *The overview's tabs.* The health tab loses its bold summary and the grey sentence about the HTTP check from the area under
      the header of the tab - they are in the line now - and keeps the thresholds and, until the check, the note about the unknown
      rows; the LDAPS tab keeps only the note about the KDC column under its button. Switching tabs changes the text of the line
      (the tab in front says its own) without the entrance motion when the message has the same key.
    - *Notes.* The card "what the read observed" is a note with its heading and no card; the scope of the review and what its check
      could not read are two notes where one card was; the note of the readiness report is one sentence.
    - *Long results.* The failed read of the overview and the stopped one of the review (about 150 characters) wrap to two or three
      lines and move the tabs and the list down with them; the failure of the account count in the templates is the longest
      sentence of the unit.
    - *Open for the owner:* whether a failure of the account count is an attention (the list is useful) or an error; whether the HTTP
      check and the probe, whose results are rows, should say "done" with their counts in the line besides the summary of the tab; whether
      *Odśwież* should be the Cancel of a reload that the operator did not start (another document's F5, a language switch); whether
      the sentence about the unknown HTTP rows belongs in the line as the summary of the health tab instead of a note.

  - *Unit 4.* Nothing of unit 4 was seen on a screen either.
    - *The connection card and the line.* The card without the state word and its colours (host in semibold, the account under
      it, *Zmień adres* at the right), the line under it at 8 px from the edge, and the rollout note with *Zakończ tryb wdrożenia*
      at its right in the narrow tab. A tab that is connected says nothing of "Połączono" any more: the page's summary is under the
      card. Whether the summary of a page that has none (nothing is read yet) leaves an empty 28 px band under the card.
    - *Long results.* A connection error - "Nie udało się połączyć z host: <explanation with its remedy> Wybierz Ponów… (kod)" - is
      the longest sentence of the unit (about 200 characters) and wraps to three lines at the width of the tab; the failed test and the
      refused save are shorter. The summary of the feed with a selection fits one line at 1360 px.
    - *The notes.* The mode of rollout with a button inside the note (a `DockPanel` as the content), the generated signing secret with
      its text box and copy button, the findings of the service as a list without a bar (glyph and sentence per row, scrolling
      from 140 px), and the note of the details pane under the title; the lock glyph of the read-only bar became the warning glyph.
    - *The action buttons.* *Wyślij test* with its glyph and "Anuluj" / "Zatrzymywanie…" (the Polish word is wider than the label) in
      the SMTP card (to the right of the recipients box) and next to the hint of a channel; the ring in the details pane's header, 14
      px at the top right of the title.
    - *Open for the owner:* whether *Validate*, *Reload*, the preview buttons and *Odśwież* become action buttons (Stop is in the line);
      whether a stopped read of the feed should keep the rows of before (it would show a list that does not match the filter, and the
      live updates would have to be given back); whether the toast after a save, an acknowledgement and "Wyślij teraz" is still needed
      now that the page says the same in its line; whether the rollout mode should be a state of the line (with its button as the
      action) instead of a note, at the price of the summary for as long as it lasts; whether the card should say "Połączono" once more
      when nothing else says it.
  - *Unit 5.* Nothing of the Korespondencja window was seen on a screen either.
    - *The line and the window.* The line sits under the top edge of the window, 8 px from the sides, and above the tabs: the
      window is 36 px higher by default (936 px, minimum 716), which may not fit a laptop screen of 768 px; the line is an
      empty 28 px band on the message tab and before anything was done (the open question of the empty line). The footer has
      lost its left half - the ring shows only while the window loads - and keeps the Outlook state, *Pomoc* and *Zamknij*.
    - *The counts moved.* The counts of the recipients were under the grid next to its buttons and are above the tabs now; the
      summary of the matches was next to the filter and the summary of the votes above the grid. The counts of the recipients and
      the answer to the last action are one line, so a result covers the counts until it is closed.
    - *The buttons.* The four buttons that start a run, the search, the two reads and the preview are action buttons in bars of
      buttons of the old size (padding 10,4, margin 0,0,6,6), each as wide as the widest of its three contents (the Polish
      "Zatrzymywanie…" is wider than most of the labels), so a narrow window may wrap a bar one button earlier; *Wyślij* keeps
      its width of 140 px and its accent.
    - *The notes.* The note of the paste under the editor (its own row above the editor), the note of the signature under its
      hint in the narrow right column, and one note per finding of the preview: the glyph is 12 px where it was 14 px.
    - *Long results.* "Wysyłka przerwana: udane 12, błędy 0, niewysłane 83. Naciśnij „Wyślij”, aby wysłać resztę: adresy, do
      których już wysłano, są pomijane, dopóki włączone jest „Pomijaj już wysłane”." (about 190 characters) and an error with
      the cause of Outlook, the advice and the code wrap to two or three lines at the width of the window and move the tabs
      down with them; a send of 300 messages ends with such a sentence under the line for as long as it is not closed.
    - *The state of the journal* covers the counts and the summaries of every tab for as long as it lasts (a host without a key
      protector; on Windows with DPAPI it should never appear).
    - *Open for the owner:* whether the list of reasons before a send ("The message cannot be sent yet: • …"), the address of a
      test that is not valid and the errors of saving files should leave their dialogs for the line (they were left because a
      list of reasons is read better in a dialog, and the dialog is one answer); whether the footer should keep the state of
      Outlook as well as the pages that need it saying it (they say the same fact); whether the message page needs a holder
      for its templates, imports and exports (they answer with toasts and dialogs as before); whether *Podgląd z Outlooka*,
      whose answer is the card, should say "done" in the line; whether the state of the journal belongs in a note of the
      recipients and send pages instead of the line of every tab.

**V2b-5a as built (the old banners go, what lost its last user goes).** The sixth increment of V2 closes the sweep of V2b: the
items 1, 2 and 8 of the list *What V2b-5 has to do* (now *What is still open after V2*) - the ones that need no decision of the owner. Nothing a user can see changed (no
release note), and nothing that is on the screen: a removed style that a view still used would have been a crash at run time, so
the proof is by script and by the Windows run, not by looking. The other items of the list wait for the owner. The block reads in
this order: the banners (item 1), what lost its last user (item 2: how it was proven, what was removed, what was kept and why),
the developer page (item 8), the checks, and what nobody has seen on a screen.

- **The banners (item 1).** `Ad.ErrorBar` and `Ad.WarningBar` are gone from `Themes/Controls.xaml`, with the lines of the header
  comment that said where each kind of feedback goes: the header now says status line, note, action button, and the bar of a row and
  the ring, and points at the developer page of item 8 (`docs/dev/v2-status-controls.md`).
  - *Proof that nothing names them.* A script over all 3,346 tracked files (release notes of earlier versions and the prose of this
    note excluded) looked for the two keys and for every way a key is read or defined: `StaticResource`, `DynamicResource`,
    `FindResource`, `TryFindResource`, `Resources[...]`, `x:Key` and `BasedOn`; for resource lookups whose key is not a literal (29 in
    code and tests - each takes a token, a motion key, a style key of a constant or a list of literals, none can build the name); for
    merged dictionaries (13, each a file under `src/AdMng.App`). Before the change it found 65 mentions (the two styles and a comment
    in `Controls.xaml`, 55 look-ups in 27 UI test files, the `DesignTokenTests` and `NoteUiTests` assertions, the four asserts of
    `XamlSourceTests`); after it, none outside the rule that has to name them. The closed set of Fluent brush keys is unchanged at 28:
    the two status backgrounds are still read by the template of the status line. The expressions of the source rules that the
    removal could break (every `Ad.*` key that is used is defined - 1,854 references now, 1,856 before; the brush set; the new rule)
    were repeated on Linux with a throwaway script, as in the units before.
  - *The tests.* The 27 UI test files that found the two styles with `FindResource` and walked every `Border` of a window to see that
    none had one of them as its style lose the look-up and the walk (a look-up of a key that is not there throws; a `Border` cannot
    have the style of a style that does not exist). Their helpers keep what was not about the banners (no `ProgressBar`, one status
    line) and say so in their comments; `DesignTokenTests` no longer reads the padding of the error bar, `NoteUiTests` no longer
    asserts that the two banner styles are still there. What they guarded is guarded by **one source rule for every view**,
    `XamlSourceTests.NoXamlFile_MentionsABannerOfBefore_OrHasAProgressBarThatSaysStatus`: no XAML file of `src/AdMng.App` -
    `Themes/` and `App.xaml` included, comments included - names `Ad.ErrorBar` or `Ad.WarningBar`, and the only `ProgressBar` is the row
    of an activity in `Views/MainWindow.xaml` (the list `ProgressBarsOfARow`, "file: count"; its own UI rule in
    `MainWindowFeedbackUiTests` stays as it was). The comment of the test says what else holds: a view that tried to use a style that is
    gone fails at run time as a missing `StaticResource` when it is loaded, and the rule shows the same mistake on the source first.
    The two lists (`PagesWithAStatusLine`, `ConvertedViewsWithoutAStatusLine`) and their other rules stay (one status line named
    `PageStatus` over `Status.Current` and `Status.CloseCommand`; a view with a line must be on the first list; never on both); only
    the banner and progress-bar asserts they repeated moved into the new rule, and the test of the second list is renamed
    `AConvertedViewWithoutAStatusLine_HasNoStatusLine`, which is all it checks now.
- **What lost its last user (item 2).**
  - *How each candidate was proven.* The candidates are the list *After V2b-1* with everything the units added to it. **A string** is
    removed only when a script over every tracked `.cs` and `.xaml` file finds no `L.<Class>.<Name>` reference, no use of its literal
    outside its constant, no XAML mention and no dynamic key family that could build it - and the pair of keys leaves both resx files
    with the constant (the localization tests and `tools/release/release.sh check` compare them). **A member** is removed only when
    the declaring class (and the classes that derive from it) assigns it and never reads it - a script lists the reads of a member
    inside the files that declare it - no XAML binds it for that class (a binding by name was looked for in every view, and the
    bindings that remained belong to other classes: rows, tree nodes), no string literal names it, and the whole solution compiles
    without it (the compiler finds the readers in `src/AdMng.App`, the tests of **all** projects and the UI tests). A string that lost its
    last user with a member went with it.
  - *Removed.* **39 strings** (both resx files, 117 lines each, with their `L` constants): core windows 5 (`Batch.Planning`,
    `PlanPreview.Cancel`, `Comparison.Stop`, `Comparison.StatusAmbiguous`, `AccountReview.ExactDone`), bulk creation 3, event logs and
    readiness 11, PKI documents 10, Korespondencja and the paste of the message window 5 (`UserMessages.Rich.CancelPaste` among them),
    and five that lost their last user with the members below (`UserMessages.Stopping`, `.Reading`, `.Progress`, `.ProgressRetry`,
    `ChangeDc.ListUnavailable`). **3 commands** that no view and no test used: `CancelPasteCommand` of the message window,
    `FindDomainControllersCancelCommand` of the Connect window (the generated cancel of the search; `ConnectCancelCommand`, which the
    window's `Closing` handler calls, stays) and `CancelWorkCommand` of the backup window. **22 properties**: of the message window
    `StatusText`, `ProgressText`, `ProgressValue`, `ProgressMaximum`; `ListNote` of "Change domain controller"; `IsLoading` of the
    container picker; `ProgressValue` and `ProgressMaximum` of the send page; `ProgressValue` and `IsProgressIndeterminate` of the
    settings search, the cleanup advisor and the diagnostics; `HasError` of `GroupPolicyDocumentBase`; `ComputedRows` of the wizard's
    check; `ProfileStatus` of the wizard's rules step; `ProgressValue` of readiness; `PlanningCompleted` and `PlanningTotal` of the
    batch editor and `IsLoading` of its CSV import; `IsBusy` of the account review.
  - *Tests that read a removed member, and what they read now.* `ChangeDomainControllerViewModelTests` and
    `ChangeDomainControllerStatusTests` (`ListNote` was the sentence "the list could not be read" that the line says with the reason, the
    code and Retry: the first now assert the kind of `ListStatus.Lasting` and that it is gone after a good read, the second already
    asserted everything on the line, so their three repeats went); `ContainerPickerStatusTests` (`IsLoading` beside `Status.IsWorking`:
    the second is asserted now); `SendPageTests` (the pair of numbers; the fractions of the line are asserted by `SendStatusTests`);
    `BulkRulesStatusTests` (five lines that repeated the adjacent assertions on the line) and `BulkCreateProfileTests` (six assertions of
    the profile sentence now read `rules.Status.Current.Message`, `EndsWith` for what was skipped) and `BatchEditorStatusTests` (the part
    done and the CSV flag sat next to assertions of the same fact on the line: the fraction, the log copy, `Status.IsWorking`). A test
    that proves behaviour stays and reads the line.
  - *Kept, and why.* Nothing was kept for want of looking: each item of the lists is in one of these groups.
    **The view model reads it itself** - as the text it hands to the line, or as logic: `ErrorText` and `StatusText` of the Connect
    window and "Change domain controller" (`StatusText` begins the operations, `ErrorText` is the reason of a refused press), `Error` of
    "Reset Password", "New Object" and the wizard's steps (`Failure(...)` and the checks read it), `TestResultText`,
    `RichStatusText` and `Error` of the message window, `TestResult`, `TestSucceeded` and `OutlookAccountsText` of the settings,
    `TestResult` of the filter builder and the view editor (with `Views.Testing` and `Views.TestResult`), `StatusText` of the object picker
    (with `Picker.Found` and `Picker.TooMany`), `StatusText` of the backup window (with its three strings: the toast reads it),
    `ConflictNote` of the two editors (the state that lasts is computed from it), `HealthStatusText` and `IsCheckingHealth` of a GPO,
    `SummaryText`, `BusyText`, `ResultText` and `Error` of the central store window and `SummaryText` of the comparison, `OptionsError`
    of the wizard's rules step, `TotalRows` and `IsStale` of its check, `ProgressText`, `LastRunText`, `WasCancelled` and `IsStale` of readiness, `Error`,
    `StatusText`, `SelectionText`, `IsReading` and `AsOf` of the computer review, `ConnectionText` (it *is* the summary: its change hook
    calls `SetIdle`) and `Error` of the remote window, `LoadedText`, `ProgressText`, `IsSupported`, `NotSupportedText` and `Error` of the
    event log documents, `ProgressText`, `SummaryText`, `LastRunText`, `HealthSummaryText`, `LoadedText` and `Error` of the PKI documents,
    `Error`, `Message`, `PreviewError`, `ActionMessage`, `StateText`, `ConnectionMessage` and `HasConflict` of the Watchdog pages and
    tab, and `StatusText`, `Error`, `ProgressText` and `SummaryText` of the Korespondencja window and its pages. Making them locals is a
    refactoring of what the page says, with its tests, and not "lost its last user". The windows of V2b-1 keep what their view models read: `Error`, `StatusText`, `SelectionText`,
    `IsLoading`, `IsBusy` and `PreviewSummary` of the Recycle Bin, `Error`, `StatusText`, `SelectionText`, `IsRunning` and
    `IsReadingDomainControllers` of the account review, `ErrorText` and `IsRunning` of the comparison, `LoadError`, `ApplyError`, `Error`,
    `IsLoading`, `IsDeleted` and `IsOutdated` of the property sheet and `IsNestedLoading` of its membership pages (the code-behind of the
    window reads it), and `Error`, `TargetSummary`, `IsLoadingTargets`, `PreviewSummary` and `ResultSummary` of the batch editor.
    **The contract of the shell:** `StatusText` and
    `SelectionText` of the documents (the shell reads them for a tab that has no line; `ShellViewModel.StatusBarText` is tested).
    **Tests need them as their oracle and the line is another holder or a hop away:** `IsLoading` of the Group Policy comparison and the
    GPO picker (22 test sites, among them the wait predicates of Windows UI tests that cannot be run here), `FooterText` of the account
    review and `Summary` and `ResultText` of the plan preview (the tests assert that the line says exactly that), `StatusText` and
    `IsNestedLoading` of the comparison (`StatusText` is also the accessible name of the list of candidates), `IsWorking` and
    `ProgressText` of the central store window, `ReadAtText` and `CountStatusText` of the PKI documents (`AssertNotSaidTwice` looks for that text on the
    screen), `BannerMessage` of the Watchdog tab (its change notification is tested), `SummaryText` of the delivery test (five assertions
    of what the test says), `SessionEndedMessage` (the base class builds `SessionEndedStatus` from it). **Commands that are the Stop of a
    line** stay as the commands they are: `Cancel()` and `CancelCommand` of the documents, readiness, the mailboxes page and the security
    review, `StopCommand` of the check step, the computer review, the remote window, the send page, the Recycle Bin and the account review
    (the tests and the window's `Closing` handler call them), `CancelSendCommand` of the message window (three test sites), `TestCancelCommand`
    of the two editors, `ConnectCancelCommand`, and `PreviewCancelCommand` and `CancelExecutionCommand` of the batch editor.
- **The developer page (item 8).** `docs/dev/v2-status-controls.md`, listed in `docs/dev/README.md`: what each piece is for, the sorting of
  a message, the recipe for a view model and a view, the tests to copy and the Windows lessons, the two source-rule lists, what is settled
  and what is the owner's. The header comment of `Themes/Controls.xaml` points at it. `tools/docs/check_docs.py` passes (every
  relative link).
- **The checks.** `dotnet build AdMng.slnx -c Release`: 0 warnings, 0 errors (the whole solution after every removal commit - the Windows-only
  projects compile on Linux and show a removed member); `AdMng.Presentation.Tests`: 3,603 passed; `tools/release/release.sh check`,
  `tools/icon/build-icon-set.py --check` and `tools/docs/check_docs.py` pass; the proof scripts above.
- **Not seen, to confirm on Windows.** Nothing of this changes a screen, and the UI tests that changed (27 files) only lost look-ups of
  styles that no longer exist; the Windows run of the last push is their proof. The new source rule and the two renamed or reworded
  tests run there as well.

**V2b-5b as built (the decisions of the owner, and what they changed).** The seventh and last increment of V2 builds what V2b-5a left
for the owner and closes the stage. The block reads as one text, in this order: the **Decisions** (the six answers of the owner, each with
its reason); one section for every decision as it was built - *The Stop of an execution* (decision 1, unit 1); *The delay before "Working"*,
*The announcement of a summary*, *The glyph of a note* and *The band stays* (decisions 5, 2, 4 and 3, unit 2); *The status pill, the empty
state and the skeleton* (decision 6, unit 3); *Taskbar progress and the close question* (decision 6, unit 4); then **Counts** (what the stage
came to, recounted by a script in unit 5), **Tests** (what the increment added or changed, and what the Windows runs taught) and **Not seen
on a screen yet**. What is still open - the owner's questions that remain and one thing to do - is *What is still open after V2* in the block
of V2a, which replaced *What V2b-5 has to do*. Not in this increment: the PowerShell window (R1), the page skeleton (V3) and anything about
releases.

- **Decisions** (the owner's, given before V2b-5b; every later unit builds them as they stand; the reasons are those of the questions
  in the list *What V2b-5 has to do*, which this block worked off, and of the owner's answers).
  1. **The Stop of an execution finishes the object in progress.** Nothing new is started, but the object whose changes already began
     gets all its changes: no account with a new password and without its other settings. *Reason:* the operator presses Stop to
     keep what has not started from happening, not to leave one object broken - an account without "must change", a created
     account disabled and without its groups, a GPO container without its files is worse than a few more requests after Stop. The
     first change of an object is still the operator's to stop (nothing of it has changed then), and a failed change still ends its
     object.
  2. **A result that only changes the summary** (the count of a search, the number of rows) **is announced to a screen reader
     politely and throttled** - not on every keystroke. *Reason:* a user of a screen reader should hear that the search found 12
     settings without hunting for the line, but a count that changes with every key would drown the speech.
  3. **The status line keeps its fixed band (about 28 px) also when it has nothing to say; it does not collapse.** *Reason:* the owner's
     priority is that content does not move when an operation starts or ends. `EmptyState` is the answer for lists without rows, not
     a collapsing line. This closes the question "collapse an empty line or keep the one fixed place?" of V2b-1, V2b-3 and V2b-4
     (the lists of open questions of those blocks), in the controls, the dialogs and every view with a line.
  4. **A note may choose its glyph from the closed icon set.** *Reason:* the severity decides the colour and the role, the subject
     decides the picture; one glyph per severity made the replication note of the bulk wizard (information) show the caution glyph, and
     the protected account is a warning. The set stays closed - no note takes a picture that is not in `Icons.xaml`.
  5. **A delay of about 150 ms before "Working" is shown.** *Reason:* an operation that ends at once - the read of nested membership
     after a comparison, the reads of the tabs of the property sheet, "preparing the changes…" before a preview - must not flash a
     status line and a ring. 150 ms keeps goal G3 (an operation shows "in progress" within 200 ms).
  6. **`StatusPill`, `EmptyState` and `Skeleton` controls, each with a first use; taskbar progress; the close question of running
     operations.** *Reason:* the V2 control set of 6.7 is finished, but no control is built without the place that uses it (so none
     stays unused), a send or a long read shows its progress on the taskbar where the operator looks while he works in another window,
     and a window closed during an operation asks - as the Korespondencja window does for a send - instead of ending it silently.
     (The controls: unit 3. Taskbar progress and the close question: unit 4.)
- **The Stop of an execution (decision 1, unit 1).** The rule lives in `BatchExecutor` (`ExecuteObjectAsync`); the windows only say it.
  - *The rule.* `ExecuteObjectAsync` looks at the cancellation token only before the first change of an object that has not made one:
    `Cancelled` then means "nothing of it was changed". Once the first change has succeeded the rest is executed, whatever happens to the
    token (no check between the changes); a failure still ends the object (`PartiallySucceeded`, `Failed`), `BatchOptions.StopOnFirstError`
    and `plan.Sequential` are as before, no new object is started (`TryEnterAsync` and the delay between objects still end with the
    token), and the objects that run in parallel each finish. `ExecuteStepAsync` takes a *stop token* - the run's token for the first
    change, `CancellationToken.None` for every later one.
  - *Which steps could be cancelled in the middle by their own token, and what happens to each.* A step is cancelled only in these
    places; each was checked so that none can leave half an object. **(1) The back-off between the retries of a directory request**
    (`InvokeAsync`, used by Modify - which sends two requests when it mixes replace and add/remove and could end after the first -,
    AddMember, RemoveMember, ResetPassword, Rename, Move, Delete, Create, Restore, ModifySecurity and the version write inside a GPO
    transaction): the back-off after a *busy* answer is no longer interrupted, so an object that sent its first request is carried
    through a blip; the request itself was never cancelled (`CancellationToken.None`). *Corrected after the audit of this
    increment:* as first built, no retry was interruptible, and the cost was given here as "3.5 s with the defaults". That
    figure held only when every attempt failed at once; an attempt that gets no answer runs for the request time-out (60 s by
    default), so a Stop during an outage held the window - and the close question behind it - for up to
    (`MaxRetries` + 1) x 60 s per object in progress. Now a request that got **no answer** (`Timeout`,
    `ServerUnavailable`) is not sent again once a Stop was asked for, and a Stop ends the wait before its next attempt; the
    step ends with that error kind (not `Cancelled`: the DC may have applied the request, exactly as when the retries run
    out). A Stop therefore waits for the request in flight and for the retries of a busy DC (about 3.5 s with the defaults)
    and no longer. The same audit separated two questions that `BatchResult.WasCancelled` had answered at once: *was a Stop
    asked for* (still `WasCancelled`; the bulk wizard's canary rule needs it) and *did the Stop leave anything undone*
    (`WasCutShort`: an object not started or a cancelled step). The status line, the toasts, the activity row and
    `ChangeOutcome` follow the second, so a one-object plan whose Stop came while it ran reads "executed", not
    "stopped: 1 of 1 object done", and its caller's follow-up (the re-read after an unlock) happens. **(2) Opening SYSVOL** (`OpenSysvolAsync`, before the
    first SYSVOL step of a run) **and (3) the stage and verify phases of `GpoTransaction` and `PolicyDefinitionsTransaction`** (steps
    WriteGpoFile, DeleteGpoFile, SetGpoVersion, WritePolicyDefinitionsFile, DeletePolicyDefinitionsFile): they take the stop token, so
    they are cancelled only when they are the first change of their object - and then nothing was written (the transaction deletes
    its temporary files); after the first change they run with no token. The switch of the transactions still ignores cancellation (the
    SYSVOL rule), CreateGpoFolder, SyncGpoFolderSecurity, SetGpoFolderSecurity and DeleteGpoFolder never took a token. An
    administrative templates plan starts with a Modify of the extension names on the container, so a Stop that came while its files
    were staged used to leave the extensions named without files; it now commits the files and the version.
  - *The result.* `BatchResult.FinishedAfterStopCount`: the objects that a Stop found running and that were carried through to the end
    (`ObjectResult`s of status `Succeeded` whose run was crossed by the cancellation); `BulkCreateResults.Merge` adds the counts. It is the
    only addition to the result model, and the texts need it: without it the line could not say "the one that ran was finished" and be
    right when the Stop came between two objects. `WasCancelled` keeps its meaning (the token was cancelled), because the wizard's canary
    reads it to start no other row.
  - *The line.* The one sentence of a stopped execution, `Changes.Status.CountsStopped`, was changed - not joined by a second one - and
    now has the three plural forms of the total: "Executing “Reset Password” in contoso.test was stopped: 1 of 2 objects done, failed: 0,
    the one that ran was finished." (Polish: "… zatrzymano: wykonano 1 z 2 obiektów, niepowodzenia: 0, obiekt, który był w toku, został
    dokończony." - the genitive after "z"). The clause about the objects that ran is `Changes.Status.Finished.One/Few/Many` ("the one
    that ran was finished", "the 3 that ran were finished") and is left out when the Stop came between two objects; the total is
    `Plan.ExecutableCount`, "done" are the objects that succeeded. The log copy is `Changes.Status.Stopped (done, failed, finished)` -
    counts only. `CorrespondenceText.PluralKey` is the plural rule of `Plural` for a sentence that has more to fill in than the count.
    Every window that executes a plan says it through `ExecutionStatus` - Reset Password, the batch editor and with it the bulk creation,
    the Recycle Bin restore, the change plan preview of the Group Policy actions and its undo - so none of them changed. The property
    sheet and "New Object" cannot be stopped from the line; stopped in the list of background operations they now end as saved or created
    (`Succeeded`) whenever the first change began, and "was stopped: nothing saved" is left for a Stop that came before it.
  - *Tests that changed, and why.* `BatchExecutorTests.Cancellation_…` (Jan Kowalski gets his move too); the stopped line of
    `BatchEditorStatusTests`, `RecycleBinStatusTests`, `ChangePlanPreviewStatusTests` and the two Windows UI tests (`BatchEditorStatusUiTests`,
    `ChangePlanPreviewStatusUiTests`): the new sentence and the log copy with the third number; `ResetPasswordStatusTests`: the account is
    complete (password and must-change). **New tests that fail without the change** (proved by restoring the check between the changes and
    by restoring the interruptible back-off): `BatchExecutorStopTests` - three changes each and a Stop after the first change of the second
    object, the audit, the undo plan (and that it works), three objects in parallel with the Stop while all three run (the new
    `FakeDirectorySession.BeforeOperationAsync` holds the three first changes without a blocked thread), a failed first change, a failure
    after the Stop, a Stop between two objects, a Stop during a back-off; `SysvolPipelineTests` and `TransactionCancellationTests` - a new
    GPO stopped after its first change commits its file and version, a templates plan stopped after the extensions were set commits, and a
    transaction that is the first change and is cancelled while staging changes nothing; `ExecutionStatusTests` - the sentence in both
    languages with every form; `BulkCreateBatchEditorTests` - a Stop during the canary finishes the account.
  - *Limits.* **A sequential plan is several objects:** a new GPO is the container with its files, the Machine and User containers and the
    link, so a Stop between two of them leaves a GPO that the undo has to remove - as before; "finish the object" is not "finish the
    action". **The row of the list of background operations** still says only "Cancelled" for a stopped execution, with its counters; the
    line is where the new sentence is. **Apply and "New Object"** offer no Stop in the line (see *Stop for a write*). The owner's questions
    about the three are in *What is still open after V2*.

- **The delay before "Working" (decision 5, unit 2).** The line shows a running operation only after `Motion.WorkingDelay`.
  - *Where it lives.* The logic is `StatusFlow` (`Controls/StatusFlow.cs`): no WPF type, a clock behind the one-method interface
    `IStatusScheduler` (`DispatcherStatusScheduler` in the application - a `DispatcherTimer` of the line's dispatcher at normal
    priority, so a busy thread does not postpone it; `ManualStatusScheduler` in the tests). `StatusLine` owns one flow and only puts what
    it decides on the screen. The token is `Motion.WorkingDelay`, 150 ms, in `Themes/Motion.xaml` - the one owner of durations
    (`MotionDictionary_IsTheOnlyPlaceOfDurationsAndEasings`), next to `Motion.HoverIntent`, which is also a delay and not a movement - read
    by key with `Motion.TimeOf` at the moment an operation arrives. **It is not motion:** `Motion.IsEnabled` is not consulted, so the delay is
    there with animations off (and in every UI test); a missing or non-positive token means no delay.
  - *The rule.* A status of the kind Working that arrives while the line shows anything else - the summary, nothing, or a result - is
    held for the delay. The line keeps showing what it showed; if the operation ends first (a result, the summary, nothing), the line goes
    straight to that and the working look is never shown: not the kind, not `IsWorking`, not the ring, no announcement (asserted by
    recording every change of `Kind`, `IsWorking` and the ring). A second Working status during the delay (a new count, a new text) only
    replaces the first; the delay runs on from the first moment, it is not restarted. A Working status that is already shown is not
    delayed again by a new message, another stage or "Stopping…" (they are shown at once); the next operation, after anything that is
    not Working, is delayed again.
  - *What stays as it was.* **The model:** `PageStatusHolder` says Working at once, `StatusLine.Status` is what the page says and is never
    late, `Kind`, `Title`, `Message`, `IsWorking` ... are what the line shows (`StatusLine.Shown` for a test); no Presentation test changed.
    **The automation events:** the name, the live setting and the live region event of Working are set and raised when it is shown. **The
    action button** (`ActionButton`) is not delayed: it goes to "Cancel" and, after a press, to "Stopping…" at once - it is what the operator
    pressed and it must answer; only the line waits, so for 150 ms the button says "Cancel" while the line still says the summary. Goal G3
    holds (the button at once, the line within 150 ms, both within 200 ms).
  - *Decision 3 holds for the delay:* what the line keeps showing may be nothing. A page whose summary is empty at rest shows its empty
    band of 28 px for those 150 ms (`Working_IsShownOnlyWhenTheDelayHasRunOut_AndTheEmptyBandKeepsItsHeightAndWhatLiesUnderIt`: the height of
    the line and the top of what lies under it are asserted before the Working look, in the middle of the delay, and after it).
  - *Which tests had to change, and how they were found.* Every assertion of the working look in the UI tests (the title "Working", the kind,
    `IsWorking`, the ring, the Stop link, the ring's turning) was listed with the `await` before it by a throwaway script. Almost every window
    test already waited with `UiWait` for `line.Kind == Working` or `line.IsWorking` (a held call lasts until the test lets it go, so the
    wait always ends); four did not and wait for the line now: `BulkCreateStatusUiTests` (the check of the wizard waited for idle),
    `StatusLinePilotTests` (Find Now) and the two stop tests of an execution, `BatchEditorStatusUiTests` and
    `ChangePlanPreviewStatusUiTests`, which waited for the model (the running activity, the button) and then asserted "Stopping…" in a line
    that had never shown "Working". **The first scan missed the last two** - it read the assertion lines and accepted a wait for
    `IsCancelling` - and the first Windows run of this unit failed on them (`Expected: Working, Actual: Idle`); the second scan reads
    every test method and asks that a wait for the line precede its first assertion of the working look, and finds nothing else. The
    other assertions of "Stopping…" follow a Working assertion of the same test, so they are immediate. `StatusLineUiTests` drives
    its lines with a `ManualStatusScheduler` and moves it by the delay after a status that is Working (`ShowWorking`). `MotionUiTests`
    has the two new tokens.
  - *A press is not a call.* `ControlHost.Press` goes through the Invoke pattern of a button, and WPF does not click at once: it posts the
    click to the dispatcher at `Input` priority, below the `Normal` priority of the continuation of a finished call. A test that presses
    Stop and releases the held call in the same turn of the dispatcher (a `finally` right after the press) therefore races: when the
    read's continuation is posted first, the read ends with its own result, the line shows the summary, and the click finds nothing to
    stop - `WatchdogStatusUiTests.TheRead_OfTheFeed_SaysWorkingWithStop_…` timed out on "the read was stopped" on one Windows run of two.
    The test now holds the read until it has **seen** the Stop take effect (the held request honours the cancellation, as the real
    client does, so it ends without a release) and releases it afterwards in the `finally`; every other test that presses Stop or Cancel
    of a held call already waits for "Stopping…" before it releases (checked by listing every release that follows a press without an
    `await` in between: this was the only one).
  - *New tests.* `StatusFlowTests` (17, pure logic, they run on Linux too - proved through a scratch project that compiled the same files):
    the delay at the millisecond (149 ms not yet, 150 ms shown), an operation that ends within it as a result or as the summary, two
    Working statuses, the stages of a shown operation, the operation after a result, no token, `Cancel`. `StatusLineTimingUiTests`:
    the same through the real control and its parts, the empty band and the content under it at each step, the entrance of the working
    look (motion on) played when it is shown and not before, and one test over the **real clock** with a held operation: the model is
    Working and the line is not at the same turn of the dispatcher, the line is Working a moment later and stays so, and an operation that
    ends in the same turn never shows it.
- **The announcement of a summary (decision 2, unit 2).** A summary whose text changes while the page stays as it is - the count of a
  search, a number of rows, a selection - is announced to a screen reader politely, once, after a quiet time.
  - *The rule* (`StatusFlow`, token `Motion.SummaryThrottle`, 800 ms, also in `Motion.xaml`). When the line shows a summary and the page says
    another text (the same message: summaries always are), the text on the screen follows at once and the announcement is scheduled
    `SummaryThrottle` after this change; every further change restarts it, so a search box that counts after every letter is announced once,
    for the last text. Nothing is announced for **the very first summary of a page** (the first text the line shows for a page that opens: it
    is what the page says, not a change), for **an empty summary**, for **a text the operator has heard already** (typing and deleting a
    letter), when the line is **not visible** (its page is not in front) or its **window is not active** (`MayAnnounceSummary`: `IsVisible`
    and `Window.IsActive`, asked at the moment of the announcement; the text is then consumed and does not come back as news), and not
    while a result is shown or "Working" is shown - such a message cancels a summary that waits (it would say what the line says anyway).
  - *What is unchanged.* A **new message** - a result, "Working" once it is shown, the summary that follows an operation the operator
    watched - is announced at once, as before: Done and Working politely, Attention and Error assertively; a new count of the same message
    is not announced. The summary that follows a Working that was *not* shown is a change of the summary (the operation never existed for
    the operator): the quick read of a search that changes the count is announced after the quiet time, and the first summary of a page that
    reads quickly is not announced.
  - *The name follows the announcement.* While a changed summary waits, the text on the screen is the new one but the name a screen reader
    reads (`AutomationProperties.Name`) is still the old one, and becomes the new one together with the announcement - so the throttle does
    not depend on whether a screen reader speaks a changed name of a live region by itself, which nobody could check on Linux (see *Blind spots*).
    Everything else sets the name at once. A language switch sets it at once as well.
  - *How it is tested.* `StatusFlowTests` (the quiet time at the millisecond, restarted by every change, the first summary, an empty one,
    back to what was heard, a result that arrives, a gate that says no, no token, a typed search that never shows "Working") and
    `StatusLineTimingUiTests` (the real control with a manual clock: `Announced` and `Announcements` are raised at the moment the line decides
    to tell a screen reader, whether or not one listens - `ListenerExists` is false in a test - the name before and after, the live setting
    and the peer, the gate in both states, an assertive result at once, a result that drops a waiting summary).
  - *The case that motivated it:* the settings search of the Group Policy area. "N settings found" is part of the summary of its tab; the
    operator types, the count changes with every query, and a screen reader says it once after the typing stops. The selection of a row adds
    "· 1 selected" to the same summary, so a change of the selection that changes that text is announced after the quiet time as well (whether the
    selection belongs in it is a question of *What is still open after V2*).
- **The glyph of a note (decision 4, unit 2).** `c:AdNote.Icon` (`Controls/AdNote.cs`), an attached property on a note, names one icon
  of the closed set of the four status icons - `AppIcons.StatusInfo`, `StatusOk`, `StatusWarning`, `StatusError`, the ones the status line
  shows (`StatusLine.IconOf`; a test asserts that the two agree) - and puts its glyph in place of the glyph of the style. The colour, the
  size, the text and the template stay those of the style: the severity chooses the colour and the weight of the sentence, the icon its shape.
  The style carries its glyph in `Tag`; the property sets `Tag` as a local value and clears it again when it is set to null or to a key outside
  the set, so the default is the glyph of the style and no existing note changed. The set is closed: `EveryNoteIconUsedInXaml_IsAnIconOfTheClosedSet`
  reads every value written in XAML (the attribute and the setter form; a mention it cannot read fails the count) and fails a key that is
  not in `AppIcons.All` and in the set - proved with a throwaway script over the real XAML files.
  - *The two cases of the owner.* **The replication note of the bulk creation results** is information: `BulkCreateViewModel.ResultNotes` is
    now a list of `BulkResultNote(Text, IsInformation)` (strings before), the replication note is the one with `IsInformation`, and the item
    template of `BulkResultsView` sets `c:AdNote.Icon` to the information icon with a data trigger on it. It keeps the style of an attention
    - its caution colour - and wears the information glyph; the other three notes of the list (canary stop, accounts left disabled, the forest
    UPN hint) keep the caution glyph. `BulkResultNote.ToString()` names the kind and never the sentence, which may name a domain controller (the
    records of that namespace print no value - a contract test). **The protected account** of the details pane (`adminCount=1`,
    `ProtectedNote`) is a warning and stays one: `Ad.Note.Attention`, the warning glyph, untouched. No other note was changed.
  - *Tests.* `NoteUiTests` - each style with each icon of the set (glyph, `Tag`, colour, size and room unchanged), the information glyph on a
    note of attention, a key outside the set and `null` back to the glyph of the style, and the set against the status line;
    `BulkCreateStatusUiTests` - every note of the results has the glyph its `IsInformation` says and the caution colour;
    `BulkCreateBatchEditorTests` (Presentation) - the replication note is information and the others are not.
- **The band stays (decision 3, unit 2).** No code was needed: the status line is, and stays, a band of `Ad.Size.StatusLine` (28 px) whatever
  it has to say, also when it has nothing to say and in the 150 ms of the delay; nothing built in this increment collapses it, and the
  tests of the delay assert its height and the top of what lies under it at every step. `EmptyState` (unit 3, below) is the answer for a
  list without rows.
- **The status pill, the empty state and the skeleton (decision 6, unit 3).** Three controls of `App/Controls`, each with a style in
  `Themes/Controls.xaml` that the control takes by itself, each with a first use, and no colour, duration or animation of its own: the
  closed set of Fluent brush keys as dynamic resources, `Ad.Radius.Pill`, `Ad.FontSize.Caption`, and the one clock of the application,
  `Behaviors/Motion.cs`. How to choose among them, and the wiring, is *Pill, empty state or skeleton?* of `docs/dev/v2-status-controls.md`.
  - *`StatusPill`* (`Kind`: `Presentation/Feedback/StatusPillKind` - `Unknown`, `Ok`, `Info`, `Attention`, `Error`; `Text`). The picture of
    the kind at 16 px - `AreaIconView`: the coloured picture of the icon set in the colour style, the glyph of the icon font in the
    monochrome style and in High Contrast - and the word, 22 px high, the radius of the pill token. Each kind has its own picture and its own
    glyph (the four of the status line and the notes, and `StatusUnknown` with the question mark), so the meaning is told by shape and word and
    never by colour; the surface is the tint of the kind (the success, attention, caution and critical backgrounds; "unknown" has none), and
    the 1 px stroke (`CardStrokeColorDefaultBrush`) is what outlines it where High Contrast makes the backgrounds the window colour. The word is
    the `AutomationProperties.Name`, the pill is one `Text` element for a screen reader (its template parts are no children of their own),
    and it takes no focus and no pointer. *Uses:* the result of every check and the worst result of every area in the readiness report
    (`ReadinessRowViewModel.PillKind`, `ReadinessGroupViewModel.PillKind`; `ReadinessText.PillKind`: ok, warning to attention, error, "not
    checked" to unknown) and the state of every health check on the status tab of the Watchdog (`WatchdogHealthCheckRowViewModel.PillKind`;
    "disabled" and "unknown" are both the unknown kind and their word tells them apart). The pills of a list are one column - `Grid.IsSharedSizeScope`
    with a `SharedSizeGroup` - so the titles start at the same place whatever the word; the pair of a glyph and a word that followed the title
    ("title host · status") is gone, and so is the glyph style of the readiness view. *Not changed, on purpose:* the channel and the domain
    controller rows of the Watchdog status (a glyph in a narrow column with the state in the tool tip: a pill would cut the names) and the
    glyph-and-word headers of the cards of that tab (not rows of a list). Other views that show a state with a glyph were not looked at in
    this unit; a pill there is a later, separate decision.
  - *`EmptyState`* (`Icon` - an `ObjectIcon`, drawn by `ObjectIconView` at 48 px -, `Message`, `ActionText`, `ActionCommand`,
    `ActionParameter`). Collapsed while `Message` is empty (a style trigger on `HasMessage`), centred in its host, at most 420 px wide, the
    sentence wrapped in the secondary text colour, the action the `Ad.LinkButton` of the status line (a real button in the tab order, shown only
    with a label *and* a command). For a screen reader a group named by its sentence (control and content element only while it has one). The state
    of a list is one value, `Presentation/Feedback/EmptyListState` (`Message`, `Icon`, at most one `PageStatusAction`; `EmptyListState.None`), that
    the view binds one way - the view needs no visibility and no trigger of its own. *The rule of when a list is empty* (a view model computes
    it, tested on Linux): the read of the shown list **ended well** (a flag set when the load completes and cleared when a load begins, a
    stop or a failure leaves it off), nothing is loading, there is no error and there are no rows - so "could not be read" is the status
    line's error, a stopped read is the line's attention, and the first read is the skeleton's. *Uses:* the object list of the main window and of the Find
    window (`ObjectListViewModel.EmptyState`: "There are no items to show in this view." with Refresh - the text this list had for it moved
    into the control; "No object matches the filter." with "Clear the filter", the new `ClearFiltersCommand`, which puts the quick filter
    text and every quick and saved chip out and reads once when a chip was on), the Group Policy objects
    (`GroupPolicyObjectsDocumentViewModel.EmptyState`: "No GPO matches the search." with "Clear the search" - this replaced `ListNote` and the
    text under it -, "There are no Group Policy objects in this domain." with Refresh) and the PKI templates
    (`PkiTemplatesDocumentViewModel.EmptyState`: "No template matches the filters." with "Clear the filters", the new `ClearFiltersCommand`;
    "No certificate templates were found." with Refresh). *Not changed:* the other lists with a `ListNote` of their own (the event logs, the
    failed logons, the PKI security review, the Group Policy diagnostics and the clean-up) - their notes say which filters hide the rows and
    stay a note until they are converted; no list was given an action it did not have a command for. *The band stays (decision 3):* the 28 px
    band of the status line is still there when it has nothing to say - an empty list is answered by this control, in the list, and not by
    a line that collapses, so nothing moves when a list becomes empty or gets rows.
  - *`Skeleton`* (`RowCount` 1 to 12, 8 by default; `RowHeight`, 26 by default; `IsActive`; read-only `IsShown`). The style holds only the host
    panel `PART_Rows`; the control builds the rows (a 16 px picture and three bars of lengths that differ from row to row, the same ones every
    time) as `Border`s with the brush `CardStrokeColorDefaultBrush` as a dynamic resource, and rebuilds them when the count or the height change.
    Collapsed while it is not active; **the bars appear after `Motion.WorkingDelay`** (150 ms, the delay of decision 5, on the same
    `IStatusScheduler` seam as the status line, so a test moves it with a `ManualStatusScheduler`): a read that ends at once never flashes
    placeholder rows, as it never flashes "Working". Without motion the rows stand still; with motion `Motion.Pulse` runs one opacity animation
    on the host of the rows (down to 0.55 and back over `Motion.Shimmer`, 1.6 s each way), only while the control is shown *and* visible, and
    takes it off when it is hidden - no clock for a list that has its rows. It is decorative: no peer, no focus, no pointer. *Use:* the object
    list puts it in the grid cell of the list under the column headers (a top margin of 29 px: the 28 px header and the 1 px border), 12
    rows of the list's own height (26 px), `IsActive` bound to `ObjectListViewModel.ShowsSkeleton` - loading, the **first read of a location
    that is not the one shown** (a different kind, base, scope, filter, view or search text; set when a location is shown, cleared when its read
    ends, however it ends), no error and no row yet. A reload (F5, a chip, a sort), `ReplaceLocationAsync` of the same list, a stopped read
    and a failed read never show it. *The opacity pulse instead of the sweep* of 5.2 (row 10) is the owner's instruction for this unit: no
    gradient, no opacity mask, one animation on one element.
  - *Decisions of the unit.* **(1)** The pill takes its kind from a view model property and never from a converter in XAML: the mapping is a
    value-free function of an enum, tested on Linux (`StatusPillKindTests`). **(2)** The empty state is a value of the view model, not a set of
    triggers in the view: it is the same three bindings in every view and the rule above is a test of the view model. **(3)** The delay of the
    skeleton is the control's, not the view model's: the model says "the first read runs" the moment it does, as the holder says "Working". **(4)**
    The bars use the card stroke brush because the closed set has no other grey that reads on both the light and the dark layer: the
    subtle fills are nearly transparent; a new key would need all three theme dictionaries. **(5)** Both pill words and empty sentences are
    strings of both languages (`List.EmptyFiltered`, `List.ClearFilter`, `GpEditing.GpoNone`, `GpEditing.ClearSearch`, `PkiTpl.EmptyNone`,
    `PkiTpl.EmptyFiltered`, `PkiTpl.ClearFilters`; the first sentence and "Refresh" are `List.Empty` and `Commands.Refresh` of before).
  - *Tests.* `StatusPillUiTests`, `EmptyStateUiTests`, `SkeletonUiTests` (parts, properties, the automation name and peer, the manual clock,
    `MotionScope.On()` for the pulse, nothing left running); `ObjectListEmptyStateUiTests` (an empty container with its Refresh, a filter and
    its action, the held first read with the placeholder rows after the manual delay and a reload without them, a read that ends before the
    delay, a failed read), the Group Policy list in `GroupPolicyEditingUiTests`, the PKI templates in `PkiOverviewUiTests`, the pills in
    `ReadinessStatusUiTests` (one pill per area and per check, the words, the kinds, one column, no old glyph, no word said twice) and
    `WatchdogViewTests`; Presentation: `ObjectListEmptyStateTests`, `StatusPillKindTests`, the empty states in `GroupPolicyEditingComfortTests` and
    `PkiTemplatesTests`; and two source rules in `XamlSourceTests` (the three styles have no colour of their own; every pill, empty state and
    skeleton in the views is bound to what makes it complete). The keys of the new styles were proved with a script over the real XAML
    files (every `Ad.*` key defined, the closed set of Fluent brushes unchanged and all dynamic, the `StaticResource` order of
    `Controls.xaml`).
- **Taskbar progress and the close question (decision 6, unit 4).** Two answers to "this window works", both made of what the holder
  already says: the taskbar button of every window shows the progress of its line, and a window that is closed while it writes asks one
  question. Neither adds a status: they read the holder (`Current`, `Operation`) and the flag of the operation.
  - *Taskbar progress - what it shows.* `TaskbarProgressTracker` (`Presentation/Feedback`, no WPF type, tested on Linux with a fake clock)
    follows one holder and gives `State` (`TaskbarProgressState`: a `TaskbarProgressKind` - `None`, `Indeterminate`, `Normal`, `Paused`,
    `Error` - and a fraction). **Working** with a part done is `Normal` with that fraction (the execution of a plan: "12 of 300" is 4 %);
    without a count - and while the line says "Stopping…" - it is `Indeterminate`. **An operation that ended with a state that lasts**
    - an attention or an error that cannot be closed, as "could not be read" is - keeps the button `Paused` (yellow) or `Error` (red)
    at the fraction where it stopped (a full bar when it had none) for as long as the page shows that state; a new operation that is
    on the button, a result the operator may close, the summary - anything the page says instead - ends it. **Everything else is
    `None`:** the summary, a closable result, a state that lasts with no operation behind it (the session ended, the object was
    deleted: the line says it, the button is not the place), no holder.
  - *Which holder, and when.* The holder in front is the one that counts: a window with a status line of its own follows that holder
    (`PageStatusHolder.Current` is already what the page in front says, `Front` included); the main window, whose lines are the lines of
    its tabs, follows the selected tab through `IHasFrontStatus.FrontStatus` (`ShellViewModel` raises it with `SelectedTab`); a window
    whose view model has no holder (the message dialogs) is never touched - not even given a `TaskbarItemInfo`. Switching to another
    holder starts from nothing: what was on the button for the first one goes, and a holder that is working when it is handed over shows
    its progress after the delay, like the line of its new view. **The delay is the line's** (`Motion.WorkingDelay`, handed in as a
    function, read when an operation arrives): the button follows the line *as shown*, so a read that ends at once never flashes and a
    button that shows progress always has a line that says what; a second status of the same operation during the delay only replaces
    what will be shown, an operation that ends first leaves the button alone. A state that lasts is latched **only when the progress had
    been on the button** (a quick read that fails is the line's error and nothing more); while a new operation waits for its delay the
    button keeps what it showed, as the line does.
  - *Where it is wired.* `Behaviors/TaskbarProgress.cs`: `TaskbarProgress.Register()` adds one class handler for the `Loaded` event of every
    `Window`, called from `AppHost.InitializeAsync` - the one place the application, the smoke test and every UI test run before the first
    window exists. No XAML file and no window class names it, so every present and future window follows its status without being told.
    Per window one `Follower` (an attached property) owns a tracker, hands it the holder of the window's view model (`IHasFrontStatus`,
    else `IHasPageStatus`, else none; again when the `DataContext` changes) and puts `State` on `Window.TaskbarItemInfo` (the main window's,
    which carries the connection's dot, or one it creates when there is something to show; a button with nothing to show that never had
    anything is left alone). The window's `Closed` ends it. **Not motion:** `Motion.IsEnabled` is not consulted. **No taskbar:** the known
    exceptions of the taskbar calls (`COMException`, `InvalidOperationException`, `Win32Exception`, `NotSupportedException`) are
    swallowed - a session without a taskbar works as before. `TaskbarProgress.Time` is the clock (`TimeProvider`), the seam of the UI tests.
  - *The close question - what is a write.* `OperationScope.Writes` (`OperationWrites`: `None` - the default, a read -, `Changes`, `Messages`,
    `Other`) is set by the code that starts the operation: `PageStatusHolder.Begin(..., canCancel, writes)`; `ExecutionStatus` sets
    `Changes` itself, so every window that executes a plan through it is a writing window without saying it. Nothing is guessed from a name:
    every `Begin` of Presentation was read. **Writes:** the execution of a plan (batch editor and the bulk creation wizard, Reset
    Password, Recycle Bin restore, the change plan preview of the Group Policy and its undo); **`Changes` that cannot be stopped:** the save
    of the property sheet and "New Object" (their execution is an activity of the connection); **`Messages`:** the send of Korespondencja
    (also drafts and the test message), of the e-mail messages window (also its test message) and the test of a Watchdog channel;
    **`Other`:** the update of the central store, the backup of GPOs, the remote commands that do something to a computer (refresh the
    policy, restart, abort the restart; not status, users, services), and the Watchdog's writes - acknowledging one event or many, saving
    the configuration, queuing a digest, retrying a delivery - all of which cannot be stopped. **Reads, by decision not writes:** every
    list, check, preview, search, planning and connection attempt, the test of an SMTP connection, the read of the files of the bulk wizard
    and its "Updating" (a rebuild of the wizard's table, not a change of the directory).
  - *The close question - what it does.* `RunningWork` (`Presentation/Feedback`, no WPF type: it takes a localizer and an `IDialogService`)
    is the one mechanism. It looks at the holders of the window's view models - the own holder, those a view model lists through
    `IHasPageStatusParts` (the Korespondencja pages, the sub-tabs of the Watchdog tab, the e-mail messages tab of the settings, every tab of
    the shell) and everything in front of them - and only at operations whose `Writes` is not `None`. **A read** is not a question: the
    window closes and its read is cancelled when the view model is disposed, as before. **A write that can be stopped** is asked about with
    the title of the window and the text of the line ("what is running"), then one sentence of what the stop does
    (`CloseWork.StopChanges`: nothing new is started, the object in progress is finished first - the Stop of V2b-5b unit 1 -, the objects
    not reached stay unchanged; `StopMessages`: what was not sent is not sent, what was accepted cannot be recalled; `StopOther`: what was
    written stays, the rest is not done; several writes are one question with the strongest sentence). **Yes** is the holder's own Stop -
    the line says "Stopping…", the token is cancelled, `ExecutionStatus` stops the activity - and the window closes **after the operation
    ended** (`OperationScope.Ended`), never in the middle of a step; **No** keeps the window and the operation. A write that was stopped
    already is not asked about again: the window waits for its end. **A write that cannot be stopped** is not a question: the window says
    it is still working (`CloseWork.Wait`, a notice) and stays open - the "wait until it ends" of what a window that cannot be closed had
    to say; the windows that refused silently until now (New Object, the property sheet while it applies, the store update) say why.
  - *Where it is asked.* `WindowViewModelBase.CanCloseAsync` is no longer overridable: it asks `RunningWork` first and then the window's own
    questions, `CanCloseCoreAsync` (unsaved changes, generated passwords, "not while it is busy") - the eleven windows that overrode
    `CanCloseAsync` override that. So every window the lifecycle (`WindowViewModelLifecycle`, `WindowViewModelHost`) closes - the title
    bar, Alt+F4, a Close or Cancel button that asks `CanCloseAsync` - asks it, and the Korespondencja and e-mail messages questions
    (`Correspondence.StopOnClose`, `UserMessages.StopOnClose`) are this one. `ShellViewModel.CloseTabAsync` asks it of a tab (a Watchdog
    tab that is saving stays and says so). The main window asks it **first** of its tabs and of the windows it owns - they are closed
    without asking when it closes - then the question about the background operations of the connection, the generated passwords and the
    unsaved tabs, as before; an execution that Yes stopped is no longer running when the second question is looked at. A batch editor
    that is closed during its execution no longer leaves the execution running in the background: it asks, and Yes stops it.
  - *Decisions of the unit.* **(1)** The taskbar follows the line, not the model: the same delay, so the two never disagree and a quick
    read never flashes the button. **(2)** A state that lasts is latched only when the progress was on the button; "could not be read"
    after a quick read is the line's. **(3)** `Writes` is a flag with a kind, not a boolean: the kind chooses the sentence of what the stop
    does. **(4)** The question lives in one static class over holders, so the main window, a tab and a window view model use the same
    code; a view model that has more holders than it shows says so with `IHasPageStatusParts`. **(5)** `CanCloseAsync` became a template
    method so that no window can forget the question (the other way was a call in each override). **(6)** Closing waits for the end of the
    operation with no time limit: "never kill a write mid-step" is the owner's rule; the line says "Stopping…" for as long as it lasts.
  - *Tests.* Linux: `RunningWorkTests` (a read, a write that can be stopped with Yes and No, one that was stopped already, one that cannot
    be, the chain of `Front`, parts, several writes, a replaced operation, both languages), `TaskbarProgressTrackerTests` (the delay at the
    millisecond, a count, no count, an operation that ends before the delay, every ending, a state that lasts in red and yellow, Stopping,
    the holder in front, no delay, disposal), `ShellRunningWorkTests` (the front holder, the parts, closing a tab that reads, writes and
    cannot be stopped) and the windows through their real view models: Reset Password (No, Yes, the window's own question after the
    end), New Object and the Watchdog save (the notice), the e-mail composer and Korespondencja (No, Yes, after the end). Windows CI:
    `TaskbarProgressUiTests` (a held execution without and with a count, the main window and its tabs, a read that failed, a window
    without a status line) and `CloseWhileWorkingUiTests` (an execution: No, Yes, after the end, the account that ran was finished; a
    read: no question; a creation that cannot be stopped: the notice and the window stays), through the real lifecycle with a scripted
    dialog service and calls held without blocking a thread.
  - *Limits.* **A write on a page that is not in front** is asked about but is not on the button, as it is not in the line: the
    Korespondencja send on the *Sending* tab shows its progress only while that tab is shown - following the busiest holder of the window is
    the alternative, and it would put on the button what the line does not say. **The main window asks twice in one case:** a background
    activity that no window of this one runs (an action of the list) is still the old question about the operations of the connection; a
    window of this one that runs an execution is the new one, and Yes there stops it first. **A write that cannot be stopped keeps the main
    window open too** (a property sheet that is saving): it is short, and the activity of the connection is the only thing that survives a
    window. **No time limit** on the wait after Yes: an operation that never ends keeps the window open (the line says "Stopping…"); a
    second close is ignored while one is waiting. **Apply and "New Object"** still offer no Stop in the line - closing them during their
    write says "still working", which is true. The settings window saving its own settings is not a write in this sense (no operation, a
    file). **Not marked:** an operation that writes without a holder (the activity center's own - the main window's question; the export
    and the clipboard). The owner's questions about these are in *What is still open after V2*.

- **Counts (unit 5).** The stage recounted by a script over the tracked files, comments excluded (`XamlSourceTests` counts the same with
  its own expressions; the script is the proof that its two lists and its rules say what is there).
  - *Banners: none.* No XAML file of the application names `Ad.ErrorBar` or `Ad.WarningBar` (89 XAML files: `App.xaml`, 5 dictionaries of
    `Themes/` and 83 views), and no file outside `docs/` does but the rule that has to name them (5 mentions in `XamlSourceTests`: the
    expression and its comments). They were 106 uses in 54 files when the stage was planned, 43 + 58 in 51 files at the end of V2a, and
    none after V2b-4; the two styles went in V2b-5a. There is **one** `ProgressBar` in the application, the row of an activity in
    `Views/MainWindow.xaml` (65 at the end of V2a); what says "busy" is a `ProgressRing` (5 elements in 4 views, 2 of them in the main
    window) or the ring and the part done of a line.
  - *Views with a line, views without.* Of the 83 views, **45 have a status line** - exactly one `c:StatusLine` named `PageStatus` each, 45
    elements: 20 in `Views/` (the Watchdog tab among them), 14 in `GroupPolicy/`, 4 in `Pki/`, 3 in `EventLogs/`, 2 in `Computers/`, one each in
    `Correspondence/` and `Readiness/` - and **29 converted views have none** (the six views of the bulk wizard, which the batch editor
    hosts; the pages and small windows of the Watchdog tab; the dialogs that only collect input; the two small windows of the Korespondencja
    window; a few small views that hold notes; the rendered preview of a message). **Nine never said a status and were never part of the sweep:** the main window (its own
    status bar of the connection; the lines are the lines of its tabs), `EventLogs/EventLogResources.xaml`,
    `GroupPolicy/GroupPolicyDiagnosticsResources.xaml` and `Controls/RichMessageEditor.xaml` (two dictionaries and an editor, no page),
    and five small windows with no operation and no message - `ColumnChooserWindow`, `MessageDialogWindow`, `PowerShellHistoryWindow`,
    `ProblemReportWindow` and `SavedFiltersWindow`. 45 + 29 + 9 = 83. The two lists of `XamlSourceTests` (`PagesWithAStatusLine`,
    `ConvertedViewsWithoutAStatusLine`) were checked against the files: sorted, disjoint, every entry a view, and the views with a line are
    exactly the first list. The nine are on no list on purpose: the source test asks that a view with a line be on the first list and that
    a view on the second have none, not that every view be on one of them (a question for V3, which adds a header to every page).
  - *The controls, and where they are used* (elements in the markup of the views; each item template counts once, a pill in a row appears
    once per row at run time): `StatusLine` 45 in 45 views; `ActionButton` 62 in 32 views (the most in `CorrespondenceWindow` 8,
    `RemoteComputerWindow` 6, `ComputerReviewWindow` 4); `ProgressRing` 5 in 4 views; `StatusPill` 3 in 2 views (`ReadinessView` - areas and
    checks - and `WatchdogStatusView`); `EmptyState` 3 in 3 views (`ObjectListView`, which is also the list of the Find window,
    `GroupPolicyObjectsView`, `PkiTemplatesView`); `Skeleton` 1 in 1 view (`ObjectListView`); notes: `Ad.Note.Info` 9 uses in 9 views,
    `Ad.Note.Attention` 55 in 39 and `Ad.Note.Error` 8 in 7 - 72 in all; `c:AdNote.Icon` 1 (the replication note of the bulk results). Not
    in any view, and so for every window: taskbar progress (`TaskbarProgress.Register()`, called once by `AppHost.InitializeAsync`) and the
    close question (`WindowViewModelBase.CanCloseAsync`).
  - *Strings and tests.* 8,462 strings in each resx file (8,448 at the notes of 0.16.0: 17 keys added - the stopped sentence in three plural
    forms and its finished clause in three, the four texts of the close question and the seven sentences and actions of the empty lists -
    and three removed: the old stopped sentence and the two old close questions of Korespondencja and of the e-mail messages window). `AdMng.Core.Tests` 5,526 passed (2,705 test methods before the increment, 2,717 now);
    `AdMng.Presentation.Tests` 3,666 passed (3,603 at the end of V2b-5a; 2,738 methods before, 2,793 now); `AdMng.App.UiTests`, Windows
    only: 613 test methods (546 before the increment).
- **Tests (V2b-5b).** What the increment added, by unit (the names are in the sections above), and what the Windows runs taught.
  - *Added.* Unit 1: `BatchExecutorStopTests` (9: a Stop after the first change of the second object, the audit, the undo plan and that it works,
    three objects in parallel, a failed first change, a failure after the Stop, a Stop between two objects, a Stop during a back-off), the SYSVOL cases of
    `SysvolPipelineTests` and `TransactionCancellationTests`, the sentence in `ExecutionStatusTests`, the canary in
    `BulkCreateBatchEditorTests`; `FakeDirectorySession.BeforeOperationAsync` holds calls without blocking a thread. Unit 2: `StatusFlowTests`
    (17, pure logic), `StatusLineTimingUiTests` (12, the real control on a manual clock and one test on the real clock), the glyph cases of
    `NoteUiTests` and `BulkCreateStatusUiTests`. Unit 3: `StatusPillUiTests` (6), `EmptyStateUiTests` (6), `SkeletonUiTests` (7),
    `ObjectListEmptyStateUiTests` (5), `ObjectListEmptyStateTests` (10, Presentation), `StatusPillKindTests` (3) and two source rules. Unit 4:
    `RunningWorkTests` (14), `TaskbarProgressTrackerTests` (13), `ShellRunningWorkTests` (5), `TaskbarProgressUiTests` (3) and
    `CloseWhileWorkingUiTests` (3). Unit 5: `XamlSourceTests.TheHeaderOfControlsXaml_NamesEveryControlOfOurs` (the header comment of
    `Controls.xaml` names every control that has a style, the ring, and the glyph property of a note).
  - *The lessons of the Windows runs*, each of which cost a run and is written next to the test that learned it. A press is not a call - the
    Invoke pattern posts the click at `Input` priority, so a test that presses Stop waits for the effect before it releases the held call
    (*The delay before "Working"*). A transient state is looked for only while the call is held, and the working look is 150 ms late, so a
    test waits for the line to say "Working" before it asserts "Stopping…". A collapsed control has no template until it has been laid
    out once, so a test lays the view out before it looks for a part of an empty state that has just become visible. A test that moves a
    fake clock right after a recorded call waits until the code under test has created its timer (`TimerWatchingTime` in
    `WatchdogPollerTests`), and the waits of the Watchdog presentation tests are on events with a bound of 60 s - the runner has stood still
    for 8 to 20 seconds per process. A Watchdog test that queued a refusal waits until the poller has parked on its long-poll. A style key
    that is wrong compiles and fails at run time: every key of the new styles was proved by a script over the real XAML files (every
    `Ad.*` key defined, the closed set of Fluent brushes unchanged and dynamic, the `StaticResource` order of `Controls.xaml`).
  - *Left alone, for whoever shortens the Windows job.* The tests of this increment hold their calls and wait for events: no real sleep,
    no wait that always runs to its timeout. The biggest outlier of the *Slowest tests* step is `TheDetailsPane_ShowsARing_…` (it waits for
    idle while a ring turns) - start there; and the other 10 s event waits of `AdMng.Presentation.Tests` (the PKI, event log, readiness
    and property sheet tests) could take the 60 s of `WatchdogTestWait` - they wait on events, so a long bound costs nothing.
- **Not seen on a screen yet (V2b-5b).** Nothing of this increment was seen on a screen or on a taskbar (Linux, no screen); the Windows CI
  tests assert properties, template parts, geometry and the model, not pixels, and a screen reader was never heard. To confirm on Windows, in
  light, dark and High Contrast:
  - *The stopped line:* the new sentence of a stopped execution wraps to two or three lines in the dialogs (three at most, then the tool
    tip); the two UI tests that changed with it ran on Windows CI only.
  - *The delay and the announcement:* the 150 ms before "Working" and the empty band for that time; whether Narrator and NVDA treat a
    `LiveRegionChanged` of a polite status bar as speech, and whether they would also speak a changed name by itself - the announcement was
    only decided and counted (no screen reader on Linux, and the CI windows are not activated).
  - *The three controls:* the tint of the pill in the dark theme and its outline in High Contrast; the 48 px picture and the 420 px sentence
    of the empty state; the grey of the placeholder bars on the layer colour, the 29 px offset under the column headers at other densities
    and the breathing of the rows with motion on; the width of the pill column in a narrow window; the sentence of the Find window's empty
    list, whose own line is switched off (the line of the window says what the search did).
  - *The taskbar and the question:* the button itself (also in a remote session and with the taskbar hidden - the tracker and the wiring are
    asserted through `TaskbarItemInfo`), the yellow and the red of a state that lasts, the wording and the wrapping of the close question
    with what is running listed one per line, and the order of the two questions of the main window.
  - What V2a left unseen (the look of the line on the status backgrounds, the ring at 14 and 16 px, the cross-fade of the button, the
    width of "Zatrzymywanie…") stays on its own list in the block of V2a.

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

**As built:** V1 and V2 are built; R1 and V3 are next in the table. The PowerShell window is the first window written against the status
line, the action button and the close question of V2 (the recipe is `docs/dev/v2-status-controls.md`).

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
