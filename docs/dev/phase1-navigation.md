# Phase 1 – navigation track: well-known containers (WKGUID) and VLV lists

Design note for two roadmap items of phase 1 ("rozpoznawanie kontenerów po WKGUID", "VLV na listach bardzo dużych OU").
Binding for the implementation; user documentation is in `README.md`.

## 1. Well-known containers (wellKnownObjects / otherWellKnownObjects)

### Behaviour

- The domain head carries `wellKnownObjects` and `otherWellKnownObjects`, DN-with-binary values
  `B:32:<GUID hex>:<DN>` ([MS-ADTS] 6.1.1.4). `redirusr` / `redircmp` rewrite the Users/Computers entries, so the
  values – never the container names – say which container is the default one.
- `AdMng.Core.Directory.WellKnownObjects` is the pure parser: `TryParseValue` (format, length, hex, DN, also the
  Extended DN form of the DN part), the GUID constants of the standard containers and `WellKnownObjectMap`
  (`Find(container)`, `Identify(dn)`). Recognized: Users, Computers, Domain Controllers, System, LostAndFound,
  Deleted Objects, Infrastructure, ForeignSecurityPrincipals, Program Data, Microsoft (in Program Data),
  NTDS Quotas, Managed Service Accounts and Keys (the last two from `otherWellKnownObjects`). Unknown GUIDs and
  malformed values are ignored; a duplicate GUID keeps the first value.
- `ISessionManager.GetWellKnownObjectsAsync` reads both attributes of the domain head once per session (base read,
  in memory with the session like the domain policy, never persisted). A failed read yields an empty map – the
  application then behaves exactly as before (no badges, no default containers); nothing is guessed from names.
- Tree: `NavigationTreeViewModel.WellKnown` holds the map of the current session; every `ContainerNodeViewModel`
  (domain tree and pinned shortcuts) gets `WellKnown` (the role or null), `IsWellKnown` and `ToolTipText` (role
  description, e.g. "Domyślny kontener nowych użytkowników", plus the object's description). Nodes created before the
  map arrives are updated when it does. The tree item template shows a small badge glyph with the role as tooltip;
  the tree item carries the same text as its UI Automation help text (keyboard and screen reader users).
- New objects: the New commands with an explicit parent (tree context menu) keep it. Without one, the target is
  the current container tab, except when the tab shows the domain root or is not a container (search results, a view,
  Find results): then users, inetOrgPersons, groups and contacts go to the well-known Users container and computers to
  the well-known Computers container (`ShellViewModel.DefaultCreateContainer`). OUs have no well-known default and
  keep the old rule (container tab, selected tree container, domain root). Without a well-known entry (map empty or
  value missing) the old rule applies too. The create-permission check (`allowedChildClassesEffective`) and
  `CreateBlockedReason` use the same per-kind target, and its permissions are read like the current container's.
- `AdMng.Watchdog.Core.Detection.WellKnownContainers` reuses the Core parser (same result, one implementation).
  There is no other hard-coded `CN=Users` / `CN=Computers` in the application.

### Edge cases

Redirected Users to an OU (the OU gets the Users badge, `CN=Users` gets none); a DN in Extended DN form; lower-case
hex; a value with a wrong length prefix; no access to the attributes (empty map); a new session (map re-read,
old badges dropped); pinned shortcuts of a well-known container (badge too).

### Tests

Core: parser (all standard GUIDs, lab values, redirect, malformed values, Extended DN, reverse lookup). Presentation:
tree badges and tooltips (en/pl, redirect, late map), shell default container (domain root tab, search tab, view tab,
OU tab, explicit parent, computer vs user, no map), Watchdog parser regression (Watchdog.Core.Tests). Lab: the
lab's `wellKnownObjects` and `otherWellKnownObjects` parse into all 13 roles with the expected DNs.

## 2. VLV on very large containers

### Behaviour

- Only container listings (one level) switch; search results, views and Find results keep streaming.
- Detection without an extra request for normal containers: the load streams as before; when the streamed rows
  reach `VirtualThreshold` (5000) and the DC announces the VLV control, the list asks the DC for the first VLV window
  (the stream is paused meanwhile). `msDS-Approx-Immed-Subordinates` is deliberately not read: it would cost one more
  request on every navigation, and the streamed first pages are shown anyway. If the DC serves it, the stream is cancelled, the streamed rows are dropped and the list switches to the
  virtual mode; the location is remembered as large, so F5, sorting, chips and live updates of the same location use
  VLV right away. If the DC refuses (unsupported control, unwilling to perform, sort refused) the stream simply
  continues – today's behaviour. The locked-accounts post filter (it drops rows client side) never uses VLV.
- `VirtualRowList` (Presentation, no WPF types): a read-only `IList` + `INotifyCollectionChanged` whose `Count` is
  the server's content count. The indexer returns a cached row or a per-index placeholder ("Loading…") and asks for
  the page (200 rows). Fetches run one at a time, newest wanted page first, at most 4 pending (an enumeration of the
  whole list – UI Automation, a text search – cannot queue the whole directory). Loaded rows replace their
  placeholders with Replace notifications; a changed count raises Reset. A list control unselects an item a Replace
  swaps out, so a selected placeholder (End, PageDown onto rows not read yet) hands its selection to its row once
  the page arrived. The cache holds at most 12 pages (LRU);
  evicted pages are fetched again when shown. Placeholders are bounded as well: beyond 12 × 200 handed out
  (a fast drag of the scroll thumb over a slow DC), those of pages that are neither recently shown nor pending are
  forgotten (the view no longer shows them; asked again, the index gets a new placeholder). The server context id of the last response is sent with the next
  request (AD keeps the sorted result set); the content count estimate is not sent, so offsets are never rescaled.
- Sorting is always on the server: the current server sort, or the clicked column; without one, `name` ascending.
  Columns the server cannot sort on (Type, Location) are ignored in the virtual mode. A sort the DC refuses falls back
  to the streaming load (which itself falls back to unsorted + local sort).
- Type-to-find: in the virtual mode the view sends typed characters (1 s window) to `FindByPrefixAsync`, which seeks
  with the VLV assertion value (the sort column, like a phone book) and selects the first row whose sort value
  starts with the text (`VirtualRowList.FindPrefixAsync`). The rows just before the seek target are checked too
  (bounded walk back): in descending order the matching block lies before the target ("mark" sorts above "m"), and
  Samba compares a value that continues with a non-ASCII letter as smaller than the text ("Kontakt 2222 Żółw" <
  "Kontakt 2222"), so its seek lands one block late (verified in the lab). Only the newest typed prefix selects: the
  answer for an older, shorter prefix that arrives later is ignored. WPF's own text search stays on for loaded
  lists (now by `Name`) and is switched off in the virtual mode (it would walk every item).
- Locate / select after load (new object, "show in container"): the object's sort value (its RDN for `name`) is
  sought, the page is scanned for the DN. One attempt per request: an object that is not found (deleted, moved out)
  is not sought again on every later refresh.
- Live updates and the application's own changes in a virtual list re-read the most recently shown pages and the
  count (one request per page). When the count and the objects at those positions are unchanged (an attribute
  changed - the common live update), the rows are updated in place and the view gets no Reset (a Reset regenerates
  every row container and takes the keyboard focus out of the list). Otherwise rows keep their identity by DN
  (updated in place), the view gets one Reset and the selected DNs are re-selected after it. Rows still waiting for a
  page read the refresh dropped are asked for again (also when the refresh fails).
- The quick filter cannot filter rows that are not loaded: on a large location it becomes a server-side filter
  (`name`, `displayName`, `sAMAccountName`, `description` contain the text; debounced reload in the virtual mode).
- Status bar: "Wirtualna lista (VLV): 125 000 obiektów, sortowanie po stronie serwera: Nazwa" (the list's status
  text) and a badge in the main window status bar with a tooltip. Ctrl+A is not offered in the virtual mode (it would
  select placeholders); placeholders are never part of `Selection` and are never opened (double click / Enter). Export of a virtual list is disabled (the
  operator exports with a Find query or a saved view).
- Memory: at most 12 × 200 rows and 12 × 200 placeholders plus the selected rows; nothing is written to disk (rule 1).

### Edge cases

DC without VLV (Samba without the module, a GC), sort refused for the column, count changes (objects added/removed
while scrolling), page response with a different offset (list shrank), a new load while a fetch runs (generation
check), disposal, language switch (rows reformatted, placeholders relocalized), switching back to a small container
(normal mode), chips toggled on a large location (VLV with the combined filter).

### Files

`src/AdMng.Core/Directory/WellKnownObjects.cs`; `src/AdMng.Presentation/Session/SessionManager.cs`;
`src/AdMng.Presentation/Navigation/{NavigationTreeViewModel,TreeNodes,WellKnownRoles}.cs`;
`src/AdMng.Presentation/Shell/ShellViewModel.WellKnown.cs` (+ small edits in `ShellViewModel.Commands.cs`);
`src/AdMng.Presentation/Lists/{VirtualRowList,ObjectListViewModel.Virtual}.cs` (+ hooks in `ObjectListViewModel.cs`,
`ObjectRowViewModel.cs`); resx en/pl; `src/AdMng.App/Views/{MainWindow.xaml,ObjectListView.xaml(.cs)}`;
`src/AdMng.Watchdog.Core/Detection/WellKnownContainers.cs`; test doubles (`FakeDirectoryBuilder`, `FakeSchema`,
`InMemoryDirectorySession`, `TestDirectory`).

### Tests

Presentation (InMemoryDirectorySession with VLV, seek - optionally with Samba's comparison -, refusal, holds and a
request log): switch at the threshold, no
switch below it or for views, fallback when VLV/sort is refused, pages on demand, bounded cache and pending queue,
placeholders and Replace notifications, server sort on header click, type-to-find (ascending and descending), locate,
live update (count change → Reset, selection by DN), quick filter on the server, status text. UI tests: the tree
badge and the virtual list binding without binding errors. Lab (`AdMng.Directory.IntegrationTests`): an OU with
3000 contacts created once per run in the run OU (removed with it by `LabCleanup`), VLV windows across the whole
list, descending order, seek in both directions, and `VirtualRowList` (pages, seek, type-to-find in both directions,
refresh after a new object) over the real session - the integration test project references Presentation for that.
