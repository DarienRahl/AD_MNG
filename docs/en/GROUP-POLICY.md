**English** · [Polski](../ZASADY-GRUPY.md)

# Group Policy (GPO) in AD Manager

From stage **K2**, AD Manager manages Group Policy Objects without the GPMC console: a GPO tree in the main window, a
*Group Policy* tab for a domain, OU and site, creating, copying, renaming and deleting GPOs, linking them to containers
(order, enforcement, disabling links, blocking inheritance), GPO status, security filtering, WMI filters, delegation,
GPMC-compatible backups (restore and import of settings) and comparison of two GPOs.

**Every change – in AD and in SYSVOL – goes through the change plan preview** (rule 5): first you see what will change
(attributes, files with differences, GPO versions, folder permissions) and the PowerShell equivalent, then *Execute*, the
result and *Undo*.

From stage **K3**, **Administrative Templates (ADMX)** settings are edited in AD Manager – like in Microsoft's *Group
Policy Management Editor* – and the domain's **central store** of templates is updated from vendor packages (Windows,
Microsoft 365 Apps, Edge, Chrome, Firefox, custom ADMX) or by downloading the latest Edge, Chrome and Firefox templates
straight from the vendor (chapters 7 and 8). From stage **K5**, the *"what will the computer get"* model shows which
GPOs and settings a computer and a user will get (chapter 9). From stage **K4**, **security settings** – local groups,
user rights, account policies and security options, auditing and services – are edited on the *Security Settings* tab
(chapter 10). From stage **K6**, settings everywhere speak the GPMC language (names, values with units, groups and
accounts instead of SIDs), editing has keyboard shortcuts, *Edit this setting* and a summary of changes before saving,
and **Group Policy diagnostics** searches all GPOs of the domain for logical errors and weak security settings
(chapter 11). From stage **K7**, the **GPO settings search** shows which GPOs of the domain set a given setting, value
or account, selected settings can be **copied to another GPO**, and **GPO cleanup** proposes deleting empty and unused
GPOs and duplicate settings, as well as merging GPOs with the same scope (chapter 12).

Developer specification (in English): [dev/k-computers-and-gpo.md](../dev/k-computers-and-gpo.md), chapters 3, 5
and 6 as well as *K2 as built* (15), *K3 as built* (17), K5 (18), K4 (19), K6 (20) and K7 (21).

---

## 1. Where it is in the program

| Place | What it gives |
|---|---|
| Tree, under the domain | the nodes **Group Policy Objects** (all GPOs of the domain, alphabetically), **WMI Filters** and **Sites** (with the sites as nodes) – always at the top, before the domain containers |
| *View → Group Policy in the tree* | hides and shows these nodes (the setting is remembered) |
| *Action → Group Policy* | *Group Policy* of the current container, *Create a GPO in this domain, and Link it here…*, *Link an Existing GPO…*, *Block Inheritance*, *Group Policy Objects*, *WMI Filters* |
| Context menu of a domain and OU in the tree | the same commands for the clicked container (site: without blocking inheritance) |
| Context menu of a GPO in the tree | *Open*, *GPO Status*, *Back Up…*, *Copy…*, *Rename*, *Delete…* (unavailable for the two default GPOs), *Compare with…*, *History in the Watchdog* |
| Object list | the **Group Policy** button on the toolbar (for a domain and OU, also for a single selected OU) and an item in the context menu |
| *Group Policy Objects* tab and GPO tab | **Edit administrative templates** (toolbar, context menu of the GPO list, header of the GPO tab and the *Settings* tab) – the ADMX settings editor, chapter 7 |
| *Group Policy Objects* tab and GPO tab | **Edit security settings** (next to *Edit administrative templates*) – local groups, rights, account policies, auditing, services, chapter 10 |
| *Group Policy Objects* tab and template editor | **Update templates…** – update of the central store of ADMX templates, chapter 8 |
| *Action*, list, *Group Policy* tab, *Status* page of a computer | **What will the computer get…**, **What will the user get…**, **Model Group Policy…** – modeling, chapter 9 |
| *Group Policy Objects* tab, *Action → Group Policy*, menu of the *Group Policy Objects* node | **Group Policy diagnostics…** – logical errors, consistency and security of all GPOs of the domain, chapter 11 |
| *Group Policy Objects* tab, *Action → Group Policy*, menu of the *Group Policy Objects* node, the GPO's *Settings* tab | **Search a setting in all GPOs…** (on the toolbar: *Search settings…*, on the tab: *Find in all GPOs*) and **GPO cleanup…** (on the toolbar: *Cleanup…*) – chapter 12 |
| Keyboard | **Ctrl+S** – save the changes of the active editor, **Ctrl+F** – tab search, **Esc** – clear the search (chapter 11) |
| Command palette (Ctrl+K) | `gpo.objects`, `gpo.wmiFilters`, `gpo.containerPolicies`, `gpo.newLinked`, `gpo.linkExisting`, `gpo.blockInheritance`, `gpo.model`, `gpo.modelUser`, `gpo.diagnose`, `gpo.searchSettings`, `gpo.cleanup`, `gpo.editTemplates`, `gpo.editSecurity`, `document.save`, `view.groupPolicyNodes` |

Every GPO view opens as a tab next to the object lists (one tab per target – a second click returns to it).

## 2. Tabs

### Group Policy Objects

All GPOs of the domain: name, status (enabled / user part disabled / computer part disabled / all disabled), WMI filter,
links, versions in AD and in `GPT.INI` (a marker on mismatch), owner, modification date. Multiple rows can be selected.
Commands: *New…*, *Copy…*, *Rename*, *Delete…*, *Back Up…* (selected GPOs), *Restore from Backup…*, *Import
Settings…*, *Compare* (exactly two selected), *History in the Watchdog*, *Edit administrative templates* (one selected
GPO), *Update templates…* and *Diagnostics…*. The search box (Ctrl+F) narrows the list by name, GUID, WMI filter and
linked container.

### A single GPO (five tabs)

- **Scope** – links (container, enforced, enabled; *Enforced*, *Link Enabled*, *Delete Link*, jump to the container),
  **Security Filtering** (*Add…* / *Delete*) and **WMI Filtering** (list of the domain's filters, *Apply*).
- **Details** – domain, owner, dates, user and computer versions in AD and in SYSVOL, GUID, **GPO Status** (enabled,
  user settings disabled, computer settings disabled, all settings disabled).
- **Settings** – settings from the GPO files (security template, `audit.csv`, `Registry.pol`, preferences, scripts,
  comment) named and grouped like in the GPMC report, with search, details, *Edit this setting*, *Copy* and *HTML
  report…* (chapter 11); read-only; preference passwords (`cpassword`) and registry values that look like passwords
  are masked. `Registry.pol` settings are changed in the administrative templates editor (the *Edit administrative
  templates* button, chapter 7), the security template, `audit.csv` and the local groups of preferences – on the
  *Security Settings* tab (chapter 10).
- **Delegation** – who has which GPMC permission level: *Read*, *Read (from Security Filtering)*, *Edit settings*,
  *Edit settings, delete, modify security*, *Custom*; *Add…*, *Change level*, *Delete*. Inherited entries are only
  shown.
- **Status** – diagnostics of this GPO (chapter 11): links, scope, filtering, AD and `GPT.INI` versions, SYSVOL folder
  and its permissions, extension names, leftovers of an interrupted save, settings and security – one row per area,
  then findings with *Fix…*. The buttons **Align versions** and **Repair folder permissions**. A GPO deleted from AD
  with its folder left behind: *Delete the remaining folder*.

### Group Policy of a container (domain, OU, site)

- **Linked Group Policy Objects** – link order (1 = highest precedence), GPO, enforced, link enabled, GPO status, WMI
  filter, modification date; *Up* / *Down* (Alt+↑ / Alt+↓), *Enforced*, *Link Enabled*, *Delete Link* (Delete), *Link an
  Existing GPO…*, *Create a GPO in this domain, and Link it here…*; a double-click opens the GPO.
- **Block Inheritance** (not for sites).
- **Group Policy Inheritance** – the GPOs applied at this place from the highest precedence, with the place of the link
  and an enforcement marker; blocking inheritance lets through only enforced GPOs from higher levels. Site links are
  shown only for sites (like in GPMC).

### WMI Filters

Domain filters with queries, author, date and the list of GPOs that use them; *New…*, *Edit…*, *Delete…* (deleting
first detaches the filter from those GPOs). The filter editor: name, description and WQL queries (namespace + query).
Trying a query on a selected computer does not exist yet – it requires extending the remote actions of stage K1 (CIM
over WinRM, [COMPUTERS.md](COMPUTERS.md)) with WQL queries.

## 3. Operations

| Operation | What it changes | PowerShell equivalent |
|---|---|---|
| New GPO (optionally linked at once) | a `groupPolicyContainer` object with `CN=Machine` and `CN=User`, a `{GUID}` folder in SYSVOL with `GPT.INI`, folder permissions, the link | `New-GPO`, `New-GPLink` |
| Copy | a new GPO with the files, status and WMI filter of the source; default permissions or – on request – exactly the explicit entries of the source (its security filtering stays) | `Copy-GPO [-CopyAcl]` |
| Rename | `displayName` | `Rename-GPO` |
| Delete | links in the domain and sites, the GPO object, the SYSVOL folder; the two default GPOs cannot be deleted; in a forest with other domains the preview warns that their links remain | `Remove-GPO`, `Remove-GPLink` (sites) |
| Link / unlink / order / enforced / enabled | `gPLink` of the container | `New-GPLink`, `Remove-GPLink`, `Set-GPLink` |
| Block Inheritance | `gPOptions` of the domain or OU | `Set-GPInheritance` |
| GPO status | `flags` | `(Get-GPO …).GpoStatus = …` |
| WMI filter of a GPO | `gPCWQLFilter` | `(Get-GPO …).WmiFilter = …` |
| Security filtering, delegation | permissions of the GPO object and, in line with them, of the SYSVOL folder | `Set-GPPermission` |
| WMI filters: new / change / delete | `msWMI-Som` objects | `New-ADObject`, `Set-ADObject`, `Remove-ADObject` |
| Align versions, repair folder permissions | `versionNumber` and `GPT.INI`; permissions of the SYSVOL folder | – |

**Deleting a GPO** requires retyping the GPO name; the message names the domain. First the window offers a backup
(*Create a backup before deleting* – on by default, the choice is remembered). When the folder could not be deleted, the
GPO tab shows *Delete the remaining folder* (it refuses when the GPO exists again). *Undo* after deleting a GPO or a WMI
filter does not restore the links or filters that pointed to the deleted object.

**Settings stored in the directory** – software installation (class store), wireless and wired network policies – live
under `CN=Machine`/`CN=User` of the GPO object, not in SYSVOL. Copying, backup, restore and import do not carry them;
the preview and the backup window say so explicitly.

**Security filtering and MS16-072.** Since June 2016, computers read the user's GPO in the context of the computer.
Removing *Authenticated Users* from filtering leaves them the *Read* permission (like GPMC), and the preview warns when
neither *Authenticated Users* nor *Domain Computers* can read the GPO.

**Operator permissions.** The program does not check up front whether the window's account can change a given GPO or
container: a change without the permission ends with an error in the preview results, and SYSVOL changes are rolled
back (the GPO stays as before).

## 4. Change plan preview and write safety

The preview window has three tabs: **Changes** (AD attributes, SYSVOL files with sizes, GPO version, folder and its
permissions; *Show differences* for a file – settings with masked secrets or lines of text), **PowerShell**
(equivalents with `GroupPolicy` module cmdlets, with the window's domain controller) and **Results**. Warnings (e.g.
MS16-072, GPP passwords, references to another domain) are next to the rows.

Saving a GPO is a transaction on one domain controller – the one the window works with (rule 4):

1. new files are written next to the target ones as temporary files and verified,
2. only then are they swapped in, and at the end the GPO version is raised in `GPT.INI` and in AD (`versionNumber`,
   compared with the value from before the plan – when someone else changed the GPO in the meantime, the change is
   rejected and the files return to the state from before the plan),
3. an error midway restores the previous files from memory; should that fail, the *Status* tab shows the inconsistency
   and *Align versions*.

Versions always increase (also on rollback), so clients always fetch the new state. *Undo* in the preview window shows
the reverse plan and executes it the same way.

## 5. Backups, restore and import

**GPMC-compatible format**: `manifest.xml` (merged with the existing one), in the `{BackupId}` folder the files
`Backup.xml`, `bkupInfo.xml`, `DomainSysvol\GPO\…` (GPO files byte for byte) and AD Manager's `admng.json` (SHA-256 sums
of the files, GPO links at the time of the backup, the WMI filter definition). Thanks to this, `Restore-GPO` or GPMC
can also restore a backup, and AD Manager restores backups made in GPMC (also Microsoft Security Compliance Toolkit
packages through *Import Settings*). Compatibility in both directions will be confirmed by a pilot on a Windows domain
controller – the backup window says so explicitly.

- **Backup** – writes only to a folder chosen by the operator (local or UNC; *Browse…*, the folder is remembered), with
  a comment. Progress and result are visible in the activity center; the window shows the `Backup-GPO` equivalent. The
  directory is not changed, and a failed backup deletes whatever it managed to write.
- **Restore from Backup** – a list of backups in the folder (name, domain, time, comment); an existing GPO gets the
  files, name, status, extension names, WMI filter (if it exists) and explicit permissions from the backup, versions
  increase; a deleted GPO is created again with the same GUID and exactly the explicit permissions from the backup, with
  a version above the last version of the deleted GPO (the AD Recycle Bin keeps it; without the Recycle Bin the version
  rises by 1001 above the version from the backup). A leftover SYSVOL folder with the same GUID has to be deleted first.
  **Also restore the links (N)** – an option, off by default (GPMC does not restore links). A backup from another
  domain is not restored – its settings can be imported.
- **Import Settings** – only the content (files, extension names, version) goes to the chosen GPO, without the name,
  status, WMI filter and permissions. Security principals and UNC paths of another domain are listed in a warning
  (K2 does not support migration tables).
- **Reading a backup is reading untrusted data**: XML without a DTD, paths only inside `DomainSysvol\GPO`, size limits,
  SHA-256 sums from `admng.json` checked when the file exists. Preference passwords (`cpassword`, MS14-025) in restored
  or imported files produce a warning in the preview (the values are not shown).

## 6. Comparing two GPOs

*Compare* (two selected GPOs) or *Compare with…* (choosing the second GPO) opens a window with the columns of both
GPOs: basic settings (status, versions, WMI filter, security filtering, delegation, links), settings from files and the
list of files (identical, different, only in A, only in B). Settings are named like in GPMC, with an *Area* column and
the technical key in the tooltip (chapter 11). By default **differences only**. Masked values (passwords) are never
shown. *Export CSV…* saves the comparison only on request. Both GPOs come from the window's domain.

## 7. Administrative Templates (ADMX)

*Edit administrative templates* opens the **Administrative templates – <GPO>** tab: settings from the GPO's
`Registry.pol` files described by ADMX templates, like in Microsoft's *Group Policy Management Editor*.

- **Where templates come from.** From the domain's central store on the window's domain controller
  (`\\<DC>\SYSVOL\<domena>\Policies\PolicyDefinitions`). When the domain does not have one – from the templates of this
  computer (`%WINDIR%\PolicyDefinitions`), just like Microsoft's editor. The bar above the tree says which templates the
  tab uses and how many files were loaded; *Reload templates* reads them anew. Names and descriptions are in the
  program's language (Polish from `pl-PL` files, missing ones from `en-US`). Template problems – an unreadable file, a
  missing language file, two files with the same namespace (e.g. an old and a new version of the Edge template; the
  first is used), an unknown category, categories nested deeper than 32 levels (shown at the top level) – are listed
  under *Template problems*; a damaged file does not block the others, and the reason is given in the program's
  language.
- **Tree and list.** *Computer configuration* and *User configuration* with template categories, *All settings* and
  *Registry settings outside the templates* – `Registry.pol` entries not described by any loaded template (written by
  another tool or by a template that is missing from the store); they can only be deleted. The settings list: name,
  state (*Not configured*, *Enabled*, *Disabled*, *Ambiguous*), category; search in names, descriptions and registry
  values; the filters *Only configured* and *Only changed*.
- **Editing a setting.** *Not configured* / *Enabled* / *Disabled* and the options from the template: a checkbox, a
  number (with range and buttons), text, multiline text, a drop-down list, a list of values or of name–value pairs.
  Beside it: the description from the template (*Help*), *Supported on*, the registry key and values, and the template
  file. An invalid value (out of range, an empty required field, repeated or empty list entries, text that is too long)
  is described at the setting and blocks saving. The *Ambiguous* state means values that match neither the *Enabled* nor
  the *Disabled* state (another tool, another template version) – choosing a state replaces them. A setting with its own
  registry value that the file does not contain is *Not configured* – also when other settings wrote values they share
  with it (e.g. the IE zone template and individual actions of that zone).
- **Saving.** *Save changes…* creates one plan for the GPO: a new `Machine\Registry.pol` or `User\Registry.pol` (only the
  entries of the edited settings are changed – the rest of the file stays unchanged and in the same order), extension
  names in AD (`gPCMachineExtensionNames` / `gPCUserExtensionNames`: the *Registry* extension and the *Administrative
  Templates* tool, as well as the client extensions the template requires) and a version increase of the changed part
  of the GPO. Values are written according to the template's rules like in Microsoft's editor: *Disabled* writes the
  template's disable values (they take precedence over deleting the values of options that share their name) or a
  delete-value command (`**del.`), lists are replaced entirely (`**delvals.`), *Not configured* deletes the setting's
  entries. A list changes only its own entries: values of other settings in the same key (and, for an appended list,
  values other than text) stay untouched. The plan preview shows the files with setting differences and the PowerShell
  equivalent (`Set-GPRegistryValue`, `Remove-GPRegistryValue`); it warns when the changed part of the GPO is disabled in
  the GPO status (clients will apply the settings only after it is enabled). During saving the editor is read-only.
  *Discard changes* returns to the saved state, *Undo* in the preview – to the state from before the save. Closing the
  tab (or the connection window) with unsaved changes requires confirmation.
- **Change by someone else.** When the GPO's `Registry.pol` changes on the controller after editing began, saving is
  rejected as a conflict (the file stays untouched), and the tab asks to discard the changes to show the current
  settings.

## 8. Updating ADMX templates (central store)

*Update templates…* opens the **Update ADMX templates** window for the central store on the window's domain controller:

1. **Templates.** *From a file…*: a vendor's ZIP archive (Chrome, Firefox, Edge), CAB (Edge), MSI (Windows templates),
   EXE (Microsoft 365 Apps) or a single ADMX file – only that template with its ADML files from the language folders
   next to it; *From a folder…* (an unpacked package, custom templates); *Templates of this computer*
   (`%WINDIR%\PolicyDefinitions` – this is how a central store is created when the domain has none); **Download
   Microsoft Edge / Google Chrome / Mozilla Firefox** – the latest templates straight from the vendor.
   Windows and Microsoft 365 Apps templates have no fixed address – you download them from Microsoft's site and point to
   the file. MSI and EXE packages are unpacked by Windows (`msiexec /a`, `/extract`), and a CAB archive – by AD Manager
   in memory, with the Windows CAB library (`cabinet.dll`), without writing its files to disk. Always from a copy in a
   private temporary folder of the profile – the copy is opened read-only, so nobody can swap it between the signature
   check and unpacking – and the folder is deleted right after the files are loaded. A CAB that would unpack to more
   than 512 MiB, is part of a set of several CAB files or contains a file from outside its folder is not unpacked; from
   a CAB only templates and ZIP archives are taken. An installer that finishes without unpacking anything is an
   unpacking error (not a package without templates). A package can have at most 512 MiB, 5000 templates and 512 MiB of
   ADMX/ADML files altogether – a larger one is rejected. Package files the window will not use (e.g. a second template
   with the same name in another folder, a file that is too large) are listed separately.
2. **Trust.** The window says where the files come from: *Signed by …* (an Authenticode signature checked by Windows,
   in the window the organization from the signing certificate; an MSI or EXE installer is run only when Microsoft
   signed it: the certificate chain ends at Microsoft's root certification authority, and the certificate's
   organization is exactly "Microsoft Corporation"), *Downloaded over HTTPS from the vendor's address* (only vendors'
   addresses, also after redirects; SHA-256 sum and size checked when the vendor publishes them – Edge always, Firefox
   when the GitHub release gives a sum, Chrome does not publish a sum; a mismatching file is rejected), *Not signed by
   its vendor* (e.g. a CAB without a valid signature or signed with a certificate that poses as Microsoft without a
   Microsoft chain) or *Files you chose* (the origin cannot be checked) – the last two with a warning.
3. **Languages.** The package's language folders to copy; by default `en-US`, the program's language and the languages
   the central store already has are selected (otherwise their files would stay at the old version). The package's `pl`
   folder goes to the store's `pl-PL` (as in Microsoft's templates).
4. **Central store files.** Each file with an assessment: *New*, *Replaced* (with the number of settings the new
   template adds and removes), *No change*, *Skipped: namespace taken* (another store file defines the same namespace –
   e.g. an old Edge template under a different name; Microsoft's editor would reject both), *Skipped: not a valid
   template* (also an archive file that cannot be unpacked – e.g. packed with LZMA or damaged), *Deleted* (an old
   collision file, when you tick *Replace old templates that declare the same namespace* – together with its language
   files), *Deleted (leftover)* of an interrupted update and *Kept at the old revision* (a store language file for a
   template being replaced, in a language the update does not write – select that language or delete the file). When the
   package itself has two templates with the same namespace (e.g. an old and a new file after a Windows update), the one
   the store already has is used, and the other is skipped. Store files that the package does not contain stay. The
   comparison refreshes after every change of languages or options, and *Update…* waits for its result.
5. **Backup.** Before saving, the whole central store is copied to the chosen folder (a subfolder
   `PolicyDefinitions-<DC>-<date-time>`; on by default, the folder is remembered jointly with the GPO backup folder).
   When the backup fails, nothing is changed.
6. **Update….** A plan through the preview: *Write a template file (central store)* / *Delete a template file (central
   store)* with sizes and differences, and when the domain has no central store yet – a note that from now on every
   administrator's editor will use it instead of the templates of their own computer. Saving is a transaction like with
   GPOs: temporary files `*.admng-*.tmp` next to the target ones, a check that nobody changed the store after the
   preview (otherwise a conflict and nothing is swapped), the swap; an error restores the previous files, and folders
   the run created disappear. *Undo* restores the previous files (the store has no version numbers), and when the update
   created the central store – deletes it together with the folders (an empty `PolicyDefinitions` would take all
   templates away from the editors). The backup, plan and preview run in the background; the window cannot be closed in
   the meantime. After the update, open editors reload the templates.

## 9. What the computer will get (Group Policy modeling)

From stage **K5**, AD Manager answers the question *"which GPOs and what settings will this computer (and this user)
get?"* – without logging on to the computer and without `gpresult`, like *Group Policy Modeling* in GPMC, but computed
in the program from what is in AD and SYSVOL of the window's controller (nothing is run on the controller or on the
computer).

**Where:** *Action* and the list's context menu – **What will the computer get…** (a selected computer) and **What will
the user get…** (a selected user); the *Group Policy* tab of a domain or OU – **Model Group Policy…** (a new computer and
a new user in this container); computer properties, the *Status* page – **What will the computer get…**; command
palette – `gpo.model`, `gpo.modelUser`. Every model opens as a tab (one tab per set of targets).

**Inputs** (a change does not recalculate the model by itself – the bar shows *click "Recalculate"*):

| Field | What it gives |
|---|---|
| *Computer*, *User* | an account (*Choose a computer…* / *Choose a user…*), a new account in a container (*Container…*) or *None* |
| *Site* | the forest's sites or *(unknown)*; for a computer account by default the site from its subnet (as in the *Computer review*), the operator's choice is not overwritten |
| *WMI Filters* | *Assume true* or *false* – the program does not run WQL queries on the computer |
| *Loopback* | *From the computer's settings* (from the computer's GPO, like the client), *Off*, *Merge*, *Replace* |

**How it computes** (like the Group Policy client, [MS-GPOL]):

- **GPO list** – links of the site, the domain and successive OUs above the account, link order, **enforced** links at
  the end (the deepest container wins among the enforced), **block inheritance** (also for site links), disabled links,
  the same GPO linked several times, GPOs that do not exist, with the computer or user part disabled and empty (version
  0);
- **security filtering** – the account must have the *Read* and *Apply Group Policy* permissions; ACL entries are
  evaluated in order, as in Windows (the first matching entry decides). The token: the account, its security groups
  from `tokenGroups` (nested and the primary group), *Authenticated Users*, *Everyone*, *This Organization*, and for a
  domain controller *Enterprise Domain Controllers*. For the user part it is also checked whether the **computer can
  read the GPO** (MS16-072);
- **WMI filter** – a GPO with a filter is *applied if the filter "…" is true* or skipped, depending on the choice;
- **loopback** – `UserPolicyMode` from the winning computer GPO: *merge* adds GPOs from the computer's chain (taking
  precedence over the user's GPOs), *replace* uses only the computer's chain;
- **settings** – for each setting the GPO with the highest precedence wins; overridden GPOs are shown in *Why*. Scripts
  are **cumulative** (all run). On a domain controller, account policies (passwords, lockout, Kerberos) count only from
  GPOs linked to the domain – a warning points to the others. Administrative templates are shown as ADMX policies (state
  and options), `Registry.pol` values without a definition – as registry values. A `Registry.pol` value that no
  configured policy of that part describes (e.g. a user policy value in the computer file or an option without the
  policy's own value) stays on the list as a registry value – like in GPMC. Preference items with item-level targeting
  carry the note *depends on the computer*.

**Result:** for the computer and for the user – a GPO list with the outcome (*Applied*, *Blocked by block inheritance
at …*, *Security filtering: no Apply Group Policy permission*, *The computer cannot read the GPO (MS16-072)*, *Link
disabled*, *Empty (version 0)* and others) and order, a settings list (area, setting, value, winning GPO, overridden,
notes) with the **Why** panel, token groups and warnings (site unknown, GPO folder unreadable in SYSVOL, no
`tokenGroups`, no ADMX templates). **Export to CSV…** writes the GPO and settings lists of both parts to a file chosen
by the operator.

**What it is not:** a model, not a result from the computer (RSoP from `gpresult`). It does not take into account WMI
filters, preference item targeting, the computer's local policies, GPOs from other domains of the forest (a site or OU
link to a GPO of another domain has the outcome *GPO of another domain (not modelled)* – it is never confused with a
GPO of this domain with the same GUID, like *Default Domain Policy*), slow links and the processing state on the client.
When SYSVOL is unavailable, the GPO lists are complete and the settings are missing.

## 10. Security settings (local groups, rights, account policies, auditing, services)

*Edit security settings* opens the **Security Settings – <GPO>** tab: the part *Computer Configuration → Windows
Settings → Security Settings* and the local groups from *Preferences*, like in Microsoft's editor. The tab reads three
GPO files: the security template `GptTmpl.inf`, the advanced audit policy `audit.csv` and the local groups of
preferences `Groups.xml`. It changes in them only the rows of the edited settings – everything else (file and registry
permissions, event logs, firewalls, local users from preferences, item-level targeting, unknown entries) stays in the
file unchanged.

- **Local groups.** Side by side *Restricted groups* (security template: *Members* – exactly that list, *Member of* – a
  group added to the indicated groups) and *Preferences* local groups (the action *Create*, *Replace*, *Update*,
  *Delete*, *Delete all member users / all member groups*, members added and removed). A group is chosen from the list
  of built-in local groups (saved with its SID) or its name is typed. A preference item with item-level targeting is
  marked – the targeting stays when saving. **Precedence:** *Members* of restricted groups replace the membership at
  every policy refresh (the GPO with the highest priority wins), preferences change the membership in the order of the
  items, and the order of the two extensions on the client is not guaranteed – the same group should not be set in both
  places. The same description is on the *Privileges* tab (U4c).
- **User rights.** All rights from *User Rights Assignment* (name and constant, e.g. `SeRemoteInteractiveLogonRight`),
  *Defined* and the list of principals; a defined empty list means "nobody". Rights from the file that are outside the
  program's list are shown after them.
- **Account policies and security options.** Password, account lockout and Kerberos policies, account options (enabling
  and the name of the Administrator and Guest accounts) and the most commonly used security options (LAN Manager and
  NTLM, SMB and LDAP signing, secure channel, interactive logon, User Account Control, auditing), each *Not defined* or
  with a value (a number with a range, enabled/disabled, a choice list, text, multiline text). Other values from the
  file are shown as raw entries (key and value) – they can be deleted or the raw value changed. Domain password and
  account lockout policies work only from a GPO linked to the domain (fine-grained password policies are a separate
  mechanism); in other GPOs they apply to the computers' local accounts.
- **Auditing.** Basic categories (`[Event Audit]`) and advanced subcategories (`audit.csv`), each *Not configured*, *No
  auditing*, *Succeeded*, *Failed* or *Success and failure*. When a GPO has both and the option *Audit: Force audit
  policy subcategory settings…* is not enabled, the tab warns that the basic categories may overwrite the subcategories.
- **Services.** The service name and startup mode (*Automatic*, *Manual*, *Disabled*); service permissions (SDDL) are
  shown and stay unchanged, a new service does not set them.

**Principals** (members, right holders) are chosen in the object picker – the SID is saved – or a name is typed
(`KONTOSO\Serwisanci`), which each computer resolves itself; the tab marks this. **Saving** – *Save changes…* creates
one plan: the computer's extension names in AD (*Security*, *Audit Policy Configuration*, *Local Users and Groups* –
only for the files being written), the changed files and the computer version. The `GroupPolicy` PowerShell module has
no commands for these files, so the script in the preview only points to the GPO. A change of a file by someone else
since editing began is a conflict (the file stays untouched), *Discard changes* returns to the saved state, *Undo* in
the preview – to the state from before the save, and closing the tab with unsaved changes requires confirmation – as in
the template editor (chapter 7). A file that cannot be read blocks only its own pages.

## 11. Readable settings, editing comfort and diagnostics (K6)

### Settings in GPMC language

Everywhere the program shows GPO settings, they speak the language of Microsoft's console, not of the keys from files:

| Was (technical key) | Is |
|---|---|
| `Computer/Security/System Access/MaximumPasswordAge = 42` | *Computer Configuration › Policies › Windows Settings › Security Settings › Account Policies › Password Policy* – **Maximum password age: 42 days** |
| `Computer/Security/System Access/LockoutBadCount = 0` | **Account lockout threshold: Account is not locked out** |
| `Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight = *S-1-5-32-544, *S-1-5-21-…-1107` | **Allow log on through Remote Desktop Services: Administrators, KONTOSO\Serwisanci** |
| `Computer/Audit/{0cce923f-…} = Success and Failure` | **Audit Credential Validation: Success and failure** |
| `Computer/Registry/HKLM\Software\Policies\…\AU!NoAutoUpdate = REG_DWORD:0` | the ADMX policy **Configure Automatic Updates: Enabled** with options, like in the template editor |
| `Computer/Preferences/Groups/{uid}@name`, `…/groupSid`, `…/Members` … (several rows) | one row in *Preferences › Control Panel Settings › Local Users and Groups*: **Administrators (built-in): Update: + KONTOSO\Serwisanci, − KONTOSO\Goście firmowi**, in the details the item type, group, options and item-level targeting |

- **Values** have units with correct pluralization (*1 day*, *2 days*, *5 days*; *3 invalid logon attempts*), explained
  special values (*Password never expires*, *Password can be changed immediately*, *Until an administrator unlocks it*),
  names of choices from the K4 catalog, registry types and actions (*Delete the value*, *Create the key*), service modes,
  script phases (*Startup*, *Logon* …) and script file sizes. SIDs are replaced with names (local groups and well-known
  principals at once, the rest with one directory query per view). Passwords and other secrets stay masked everywhere –
  in the row, details, copy, report and export.
- **The GPO's *Settings* tab** is grouped like the GPMC report (*Computer Configuration* / *User Configuration* → folder
  path, collapsible groups), has search (name, value, technical key; **Ctrl+F**), a details panel (where GPMC shows the
  setting, the full value, an explanation, the technical key), **Edit this setting** (double-click, Enter, context menu),
  **Copy** and **HTML report…** – a standalone report of the GPO's settings written to a file chosen by the operator.
  `Registry.pol` settings are first shown as registry values and, once the ADMX templates are loaded (in the
  background), as policies with names and options; when there are no templates, the tab says so.
- **The same description** is used by: the *what will the computer get* model (K5, also with *Edit this setting*), the
  comparison of two GPOs (with the *Area* and *Technical key* columns, also in CSV), the change plan preview (file
  differences as sentences, the technical line in the tooltip) and GPO changes in the Watchdog tab. Watchdog
  notifications (e-mail, Teams, webhook) still give the technical key.

### Editing comfort

- **Keyboard.** **Ctrl+S** saves the changes of the active editor tab (*Save changes…* with the plan preview),
  **Ctrl+F** moves to the tab's search (editors, the *Settings* tab, the GPO list, diagnostics; on other tabs – to the
  directory search), **Esc** clears the search, **Enter** on a setting goes to its editing. Command palette (Ctrl+K):
  *Save changes*, *Edit administrative templates*, *Edit security settings*, *Group Policy diagnostics*.
- **Edit this setting** – from the *Settings* tab, from the K5 model, from diagnostics and from the link menu on the
  container's *Group Policy* tab (*Edit…*) – opens the right editor right at that setting: administrative templates on
  the policy, security settings on the right page and row. An already open editor tab of that GPO is reused. Settings
  that no editor changes (scripts, other preferences) do not have this command.
- **Before saving**, the plan preview has a **Setting changes (N)** section: the setting, the value before and after, in
  sentences like on the *Settings* tab – above the list of files and attributes.
- **Template editor (K3):** the counter of configured settings keeps up with changes; incorrectly typed options do not
  disappear when moving to another setting – they stay in the editor with a warning at the policy and block saving until
  they are corrected or discarded; a missing central store is a warning on the templates tab; the category column
  appears only when the list spans several categories; the registry key and value are on two rows.
- **Security settings editor (K4):** one search and the filters **Only defined** / **Only changed** over all five pages
  (with the filter, options from all groups on one list with a group column); an edited row does not disappear from the
  list until the filter changes.
- **The GPO list** has search (name, GUID, WMI filter, linked container). Tab headers do not cut off the title or the
  status (full text in the tooltip), and the splits between the tree, list and details can be moved.

### Group Policy diagnostics

**Group Policy diagnostics…** (the *Diagnostics…* button on the *Group Policy Objects* tab, *Action → Group Policy*, the
menu of the *Group Policy Objects* node in the tree, the palette `gpo.diagnose`) opens a tab that looks for logical
errors, consistency problems and weak security settings in all GPOs of the window's domain.

- **Quick check** reads from AD the GPO objects, links, permissions and WMI filters as well as the `GPT.INI` of every
  GPO – usually a few seconds. **Full check** also reads the SYSVOL folders and the settings of all GPOs, counts users
  and computers under the links, checks SIDs and compares the settings of GPOs acting on the same container. Progress,
  *Cancel*, the time of the last check; when Group Policy changes in this window, the tab says the results may be
  outdated (it does not recheck by itself).
- **Results:** counters by severity (*Critical*, *High*, *Medium*, *Low*, *Information* – a click filters), filters by
  severity, area (*Links*, *Scope*, *Filtering*, *Versions*, *SYSVOL folder*, *Settings*, *Conflicts*, *Security*), GPO
  and text, the list of findings and details: *Why it matters*, *What to do*, *What it is about* and **Fix…**, *Open
  GPO*, *Edit this setting*, *Show in model*, *Copy*. The bar above the list says what was not checked and why (quick
  check, SYSVOL unavailable, a folder that could not be read).
- **Fix…** opens the regular change plan preview (rule 5) – with the PowerShell equivalent, execution and *Undo* – and
  after execution the tab checks again. Fixes: *enable the link*, *remove the link*, *keep one link (the one in
  effect)*, *set the GPO status*, *grant the "Read" permission* (MS16-072), *clear the WMI filter*, *delete the WMI
  filter*, *align the versions*, *repair the folder permissions*, *remove the account from the permissions*, *add the
  missing extension names*.
- **Export** to CSV or HTML – only to a file chosen by the operator; the results are only in the tab's memory.
- **The GPO's *Status* tab** shows the results of the same check for one GPO (the full one when SYSVOL is available):
  one row per area, then findings with *Fix…*; *Align versions* and *Repair folder permissions* stay. The check starts
  when the tab is visible (not on every opening of the GPO tab).

**What is checked** (severity in parentheses; ✚ – with a fix in the program):

- **Links:** an unlinked GPO (information); all links disabled (low ✚); a link to a nonexistent GPO (medium ✚) – and to
  a GPO this account cannot read, only information, without removing the link; unreadable or foreign link entries (low);
  a GPO linked several times to the same container (low ✚); a linked GPO with all settings disabled (medium); a linked
  empty GPO (low).
- **Scope:** a disabled part with settings (medium ✚); an enabled empty part (information ✚); user settings without
  users and computer settings without computers under the links (medium); account policies outside the domain level
  (medium); loopback in a GPO without computers (low).
- **Filtering:** nobody can apply the GPO (high); computers cannot read a GPO with user settings – MS16-072 (high ✚); a
  missing WMI filter (medium ✚); an unused WMI filter (information ✚); deny entries (information); an unknown account in
  the permissions (low ✚).
- **Versions and SYSVOL folder:** different versions in AD and `GPT.INI` (medium ✚); a missing GPO folder (high); folder
  permissions different from the GPO's permissions (medium ✚); settings files skipped by clients because extension
  names are missing (medium ✚); an unreadable settings file (medium); leftovers of an interrupted save (low); a GPO
  folder outside the domain's SYSVOL (high).
- **Settings:** the same group in *Restricted groups* and in preferences (medium); *Administrators* defined without
  administrators (high); basic and advanced auditing at the same time (medium when subcategory forcing is disabled; low
  when it is not defined – then forcing is on by default); minimum password length above 14 without the option allowing
  it where it applies (medium); a principal saved by name (information); an unknown SID (low); logon denial for a broad
  group (high).
- **Conflicts:** GPOs acting on the same container set one setting differently (the winner and the loser named) or the
  same way (a duplicate) – information.
- **Security:** a password in preferences `cpassword`, MS14-025 (critical); a broad group (*Everyone*, *Authenticated
  Users*, *Domain Users* …) can edit the GPO, has a sensitive right (debugging, backup, restore, taking ownership,
  loading drivers …) or is a member of a privileged local group (high); reversibly encrypted passwords and unencrypted
  passwords to SMB servers (high); a weak LAN Manager authentication level, stored LM hashes, SMB or LDAP signing not
  required on domain controllers, anonymous access, the Guest account enabled, User Account Control disabled, weak
  domain password policies (medium). The rules for domain controllers and domain password policies assess the setting
  that actually wins on them (GPO precedence, disabled computer part), and "the part has settings" – in the full check –
  according to the files, not the version number.

The checks work on data from one domain controller (rule 4) and change nothing; every change goes through *Fix…* and the
plan preview. Some rules assume that WMI filters are true and loopback is off (like in the K5 model) – details are in
the description of each finding.

## 12. Searching, copying settings and cleaning up GPOs (K7)

Three tools for all GPOs of the window's domain: the **GPO settings search** answers questions like "where is the
minimum password length set?", "which GPO maps drive K:?", "who grants the right to log on through Remote Desktop
Services?"; **copying settings** carries selected settings from one or more GPOs into another GPO; **GPO cleanup** points
out empty, unused and disabled GPOs, repeated settings and GPOs that can be merged. Settings are described in the GPMC
language (chapter 11). Every change – copying, removing a setting, merging, unlinking, disabling, deleting a GPO – goes
through the change plan preview (chapter 4) on the window's domain controller (chapter 13); every one of them except
deleting a GPO can be undone.

### Where it is

| Place | Search | Cleanup |
|---|---|---|
| Command palette (Ctrl+K) | `gpo.searchSettings` – *Search a setting in all GPOs…* | `gpo.cleanup` – *GPO cleanup…* |
| *Action → Group Policy* | *Search a setting in all GPOs…* | *GPO cleanup…* |
| Menu of the *Group Policy Objects* node in the tree | *Search a setting in all GPOs…* | *GPO cleanup…* |
| Toolbar of the *Group Policy Objects* tab | the **Search settings…** button | the **Cleanup…** button |
| *Settings* tab of a GPO tab | **Find in all GPOs** – searches for the text of the tab's search box (and, when it is empty, for the name of the selected setting) | – |
| *GPO cleanup* tab | *Show in the settings search* – opens the settings search with the setting name of the proposal | – |

Both tabs exist once per connection window: a second call returns to the open tab (the search gets the new text).

### Reading the settings of all GPOs

The first opening of the search or the cleanup reads from the window's domain controller the GPO objects, the links
(domain, OUs, sites), the `GPT.INI` versions and the folders of all GPOs in SYSVOL (at most four reads at a time),
resolves account names and groups the settings. The progress shows the stages *Reading GPOs and links*, *Reading GPT.INI
versions*, *Reading GPO folders*, *Resolving account names*, *Grouping settings*; **Cancel** ends this tab's wait (the
previous results stay). Both tabs use **one read** per window: opening the second one does not read the folders again.

- **Refresh** (F5) reads again only the GPOs changed since the last read (a different version in AD or in `GPT.INI`),
  deleted ones disappear, links are always fresh. **Read everything again** reads the folders of all GPOs.
- When Group Policy changes in this window (also through copying or the cleanup), the tab shows the bar *Group Policy
  changed in this window – the results may be out of date.* with a *Refresh* button – it does not read again by itself.
  After an action from these tabs, the refresh (of the changed GPOs only) starts by itself.
- Changing the domain controller, reconnecting and disconnecting discard the read (the results shown stay, with the
  out-of-date bar); changing the language only rewrites the texts (without reading the folders again).
- **Limits:** a settings file larger than 16 MiB or a GPO's settings over 64 MiB in total are not read (the GPO has
  the status *Not all settings read*, the same as with a `Registry.pol` file of more than 10,000 entries); after 1 GiB
  has been read, the remaining GPOs have the status *Not read*. Other statuses: *Folder not found on SYSVOL*, *Access denied*, *Folder
  too large*, *Could not be read*. GPOs that the window's account cannot read in AD are not visible at all – the tab
  header gives the number of GPOs searched and the time of the read (*GPOs searched: N · read …*).
- Without access to SYSVOL, the tab has only directory data (the bar *SYSVOL is not available (…): only directory data
  was read*); the settings from files then cannot be searched or assessed.

### GPO settings search

**The search box** (*Setting, value, key or account (at least 2 characters)*, Ctrl+F, Esc clears it) searches as you
type (Enter – at once). Case and Polish letters do not matter: "dlugosc" finds "długość".

| Filter | Choices |
|---|---|
| **Search in** | *Everything*, *Name* (the setting's name as in GPMC), *Value*, *Technical key* (e.g. `MinimumPasswordLength`, `HKLM\Software\Policies\…`), *Account* |
| **Configuration** | *Computer and user*, *Computer*, *User* |
| **Area** | *Every area*, *Administrative templates* (also registry values outside the templates), *Security settings*, *Advanced audit*, *Preferences – type* (each preference type that occurs in the domain separately, e.g. drive maps), *Scripts*, *Other* |
| **Only different values** | only the settings that GPOs set to different values |

*Clear filters* returns to the defaults. **Account** accepts a name (`KONTOSO\Serwisanci`, `serwis@kontoso.pl`,
*Administrators*) or a SID and finds the settings in which the account is stored as a SID or by name (user rights,
restricted groups, local groups of preferences, item-level targeting), as well as accounts whose name contains the typed
text. **Hidden values** (passwords, SNMP communities …) are never searched – only their names and keys.

**A short query** (an empty text or one character) shows only a summary: for each area the number of settings,
occurrences and GPOs, and the total at the end – never the whole list of all settings of the domain.

**Results** are grouped by setting. The group header is the setting's name, its path in the GPMC console and a summary:
*set in N GPOs – values match*, *– values differ* or *– values not comparable* (e.g. a hidden value, an item with
item-level targeting, scripts; disabled items are not counted; when the setting takes effect from one GPO only – just
*set in N GPO(s)*). Under the header is **every** GPO that sets this setting, also with a different value, with the
columns *GPO*, *Value*, *Links* (a disabled link with the note *(disabled)*) and *Notes*: *conditional (item-level
targeting)*, *item disabled*, *value hidden*, *GPO read only in part*. The line above the list: *Settings found: N
(occurrences: M)*. When there are more than **2,000** groups, the tab shows the first 2,000 and the note *Narrow the
search – 2,000 of N settings shown*.

**The notes bar** above the results says what was not searched: GPOs whose folders were not read or were read only in
part (the number, names and statuses in the tooltip), administrative templates that are not loaded (policies are then
shown as registry values and cannot be found by their names), the number of hidden values, an unavailable SYSVOL, links
from other domains of the forest (not visible from this domain controller).

**Details** of the selected row: the name, the GPO, the full value with its parts, *Where GPMC lists it*, *Links*,
*Technical key*, notes and – when the setting cannot be copied – *Cannot be copied: reason*.

**Actions** (buttons in the details and the row's context menu):

- **Open GPO** (Enter, double-click) – the GPO tab on its *Settings* tab;
- **Edit this setting** – the template editor (K3) or the security settings editor (K4) right at this setting (when the
  setting has an editor, chapter 11);
- **Compare GPOs** – two selected rows: compares their GPOs; one row: compares its GPO with one you pick (chapter 6);
- **Copy to another GPO…** – the selected rows (also from different GPOs) to a chosen GPO, see below;
- **Copy** (Ctrl+C) – the text of the selected rows to the clipboard (hidden values stay hidden);
- **Export to CSV…** and **Export to HTML…** – the current results to a file chosen by the operator (CSV protected
  against formulas; the HTML report with the domain controller, the query and the time of the read).

### Copying settings to another GPO

**Copy to another GPO…** (in the search and in the cleanup) first asks for the **target GPO** (*Copy settings – choose
the target GPO*; one GPO, the source GPOs are not offered), then opens the **Copy settings to …** window: a list of the
settings with the columns *Setting*, *Value*, *Source GPO* and *Remark*, check boxes (*Check all*, *Uncheck all*, the
counter *Checked: X of Y*) and the choice **When the target GPO already has the setting:**

| Mode | Effect |
|---|---|
| **Replace** (default) | the value in the target GPO is replaced by the copied one; when the target GPO already has the same value, nothing is written (*the target GPO already has the same value*); preference items with different item-level targeting (e.g. several mappings of drive K: for different groups) stay, and the copy is added after them |
| **Skip** | the target GPO keeps its value, the setting is not copied |
| **Merge lists** | user rights assignments and restricted groups get the accounts of both lists (the same account stored by name and by SID counts once); other settings are replaced |

One copy takes settings from **at most 10** source GPOs (the window says so when more are checked). A setting selected
from two GPOs at the same time is not copied at all – it has to be selected from one of them.

**What cannot be copied.** Refused settings are greyed out in the window, with the reason in the *Remark* column, and do
not stop the others from being copied:

| Setting | Reason in the window |
|---|---|
| passwords in preferences (`cpassword`, **MS14-025**), `DefaultPassword`, SNMP communities and other hidden values | *Holds a secret or masked value (for example a GPP password, MS14-025) – never copied.* |
| startup, shutdown, logon and logoff scripts and their files | *Scripts and script files are not copied – add them in GPMC.* |
| registry and file system permissions (*Registry*, *File System* in the security settings) | *Registry and file system permissions are not copied – set them in GPMC.* |
| audit options and global object access auditing | *Audit options and global object access auditing are not copied – set them in GPMC.* |
| comments of the GPO and of settings | *Comments are not copied.* |
| a setting from a file that could not be read; a setting with too long a name | *The file could not be read.* / *The setting's name is too long to be identified exactly.* |
| a preference item the program cannot identify unambiguously, and settings of other kinds | *AD Manager cannot copy this kind of setting – change it in GPMC.* |
| a preference item in a collection with item-level targeting or in a disabled collection (copied outside the collection, it would lose its conditions) | *The item is in a collection with item-level targeting or a disabled collection – copy it in GPMC.* |

Also refused, with a message in the plan preview: an ADMX policy when the administrative templates are not loaded; a
registry value that belongs to a configured ADMX policy in the target GPO; a preference whose client-side extension
(CSE) is in neither the source nor the target GPO (clients would skip it); all settings of a GPO read only in part.
Settings stored in the directory (software installation, wired and wireless network policies) are **never copied** – the
preview warns about it, as with a copy of a GPO (chapter 3).

**Warnings in the preview** (copying goes on): the value changed since the search (the value read now is copied); the
setting comes from a disabled part of the source GPO (it has no effect there, in the target it will take effect);
password and account lockout policies in a GPO not linked to the domain (they act only on the local accounts of
computers), Kerberos policies in such a GPO (no effect); a preference item with the option *Remove this item when it is
no longer applied* (clients first undo it – unmap the drive, delete and recreate the user or group – before the new
item takes effect).

**The plan preview** is one plan for the target GPO: extension names (only when they change), every changed file
(`Registry.pol`, `GptTmpl.inf`, `audit.csv`, preference files – with *Show differences*) and a single version increase.
The **Setting changes (N)** section shows every setting with its value before and after (group: *GPO name › area*;
hidden values stay hidden). All bytes come from the files read at planning time, not from the search results; a new
preference item gets a new identifier and goes to the end of the target GPO's list (applied last). The *PowerShell* tab
gives `Set-GPRegistryValue` for registry values and a comment that the other files are written by the plan (the
`GroupPolicy` module has no cmdlets for them). When nothing would change (every setting refused, skipped or already the
same), an error window *Nothing would change in …* with every setting and its reason appears instead of the preview – an
MS14-025 refusal never disappears silently. **Undo** in the preview window restores the files of the target GPO (with
another version increase).

### Removing a setting

**Remove the duplicate from this GPO** (cleanup, the *Duplicated setting* proposal) removes a setting from one GPO: it
sets the ADMX policy to *Not configured*, removes the `GptTmpl.inf` and `audit.csv` lines, the `Registry.pol` entries and
the preference items (an empty collection stays, as in GPMC). The same preview, the same refusals and the same warning
about the option *Remove this item when it is no longer applied*; *Undo* restores the setting.

### Merging two GPOs

**Merge with…** (cleanup, the *Merge candidate* and *GPO with one setting* proposals) opens the **Merge A into B** window
and, in one plan with a preview and *Undo*:

1. **copies** all settings of A into B (without the settings of A's disabled part – they had no effect there; comments
   and unused script files are not carried over, the preview says so);
2. **replaces the links**: in every domain container and site to which A is linked, B takes A's place with its options
   (order, enforcement, enabled); when B is already linked there, A's link is only removed (B's link stays);
3. **disables A** (status *All settings disabled*). Deleting A is a separate step (*Delete GPO…* from the proposal, with
   a backup) – a deletion cannot be undone.

**Which values win** when both GPOs set the same thing – the window suggests it from the link order (*As the link order
says: Replace* or *Skip*): when B has the higher precedence everywhere both are linked, B's values stay; when A has, A's
values replace B's; when they have no container in common, A's values replace B's. When the order differs from place to
place, you have to choose yourself (*Replace*, *Skip*, *Merge lists*).

**Check before merging** – the window and the preview warn when: the GPOs apply to different users, groups or computers
(security filtering) or have different WMI filters; they are linked in different places (*Only A is linked to …: after
the merge these places also get the other settings of B*, and the other way round); in a shared container A has the
higher precedence, enforcement or an enabled link where B does not (A's settings take B's weaker position – B is not
moved); the forest has other domains (their links to A are not replaced); a part of B is disabled where A has settings;
preference items of A have the option *Remove this item when it is no longer applied*.

**The merge is refused** when A has settings that cannot be copied (the list *These settings cannot be copied, so the
GPOs cannot be merged:* with the reasons), settings stored in the directory or files AD Manager does not read (e.g.
folder redirection, software installation), when A or B was read only in part, when A is one of the two default GPOs or
when A and B are the same GPO. **Undo** restores B's files, the links and A's status.

### GPO cleanup

The **GPO cleanup** tab analyses the read of all GPOs (the same one as the search) and shows proposals: counters by kind
above the list (a click filters), the filters *Kind:* and *GPO:*, search in the proposals (Ctrl+F), a list with the
columns *Kind*, *GPO*, *Proposal* and *Actions* and, in the details, **What was found**, **What to do**, **GPOs** (with
their links), **Setting** (in the GPMC language) and **Remarks**. *Refresh* (F5), *Read everything again*, *Cancel*, the
bar *Group Policy changed after the analysis – the proposals may be out of date.* and the export (*Export to CSV…*,
*Export to HTML…* – to a file of the operator's choice) work as in the search. GPO content is assessed only for GPOs read
in full; the others have only their links and status assessed (the bar *The settings of N GPOs were not read
completely, …*).

| Kind | When | Actions | Remarks |
|---|---|---|---|
| **Empty GPO** | the GPO has no settings at all – also none in the directory and in files the program does not read; a disabled preference item is a setting too (dormant content is not an "empty GPO") | *Delete GPO…*, *Unlink everywhere…* (when linked) | a linked GPO: deleting it removes its links too; default GPOs – no deletion |
| **GPO without links** | no link to the domain, an OU or a site of this domain – it applies to nobody | *Delete GPO…*, *Disable GPO* | a forest with other domains: their links are not visible from this controller |
| **All links disabled** | every link of the GPO is disabled | *Unlink everywhere…*, *Disable GPO*, *Delete GPO…* | |
| **GPO disabled** | both parts (computer and user) disabled | *Delete GPO…*, *Unlink everywhere…* (when linked) | |
| **GPO with one setting** | exactly one setting, without settings in the directory and files the program does not read | *Copy to another GPO…* (then *Disable GPO* separately); *Merge with…* when there is a GPO linked in the same places (container, enabled, enforced), with the same security filtering and WMI filter | the setting cannot be copied – no copying and no merging |
| **Duplicated setting** | the setting has the same value in another GPO that applies everywhere this one does (described below) | *Remove the duplicate from this GPO* | the option *Remove this item when it is no longer applied*; account policies only in GPOs linked to the domain |
| **Different values of one setting** | the same setting has different values in different GPOs that take effect | information only: *Open GPO*, *Compare*, *Show in the settings search* | |
| **Merge candidate** | two or more GPOs linked in the same places, with the same security filtering and WMI filter; each is proposed for merging into the one with the most settings | *Merge with…* | a different link order; the option *Remove this item…*; no merging when the GPO has settings that cannot be copied, settings in the directory, files the program does not read or a disabled part with settings |

**Duplicated setting** is cautious, because the same value in two GPOs does not always make one of them redundant (user
rights and restricted groups replace each other as a whole, preferences act one after another, scopes may not overlap).
Removing the setting from GPO X is proposed only when: both GPOs take effect (they have an enabled link, the part with
the setting is not disabled, the value is comparable – not hidden, not conditional, not a script); no other GPO that
takes effect sets a different value here (then it is *Different values of one setting*); the other GPO Y applies
**everywhere** X does – in every domain, OU and site (with block inheritance and enforcement), to the same users and
computers (security filtering) and with the same WMI filter or without a filter. Two GPOs with the same drive mapping on
disjoint OUs give no proposal – the search shows them. The GPOs are ranked, so carrying out all proposals never removes
the last copy of a setting. Account policies (passwords, lockout, Kerberos) are assessed only in GPOs linked to the
domain; entries that delete a whole registry key (`**delvals.`) are not proposed.

**Actions** – each one through the plan preview (with *Undo* – except deleting a GPO); after execution the tab refreshes
the read and analyses again:

- **Delete GPO…** – first the program checks whether the GPO has changed since the analysis (version and links): when it
  got settings or links in another window or from another administrator, the deletion is refused (*… changed (its
  settings, status or links) since the analysis, so it is not deleted*) and the list is read again. Then the usual
  deletion from chapter 3: the offer of a backup (*Create a backup before deleting*) and retyping the GPO name; the plan
  checks the state again right up to execution. Deleting a GPO **cannot be undone** – hence the backup.
- **Unlink everywhere…** – removes the GPO's links from all containers of the domain and all sites (with a warning when
  the forest has other domains); **Disable GPO** – the status *All settings disabled*;
- **Copy to another GPO…**, **Remove the duplicate from this GPO**, **Merge with…** – as above;
- **Open GPO**, **Compare** (with the related GPO), **Show in the settings search**, **Copy** (the text of the proposal).

The cleanup never proposes deleting or merging the default GPOs (*Default Domain Policy*, *Default Domain Controllers
Policy*).

### What stays only in memory

The read of the settings of all GPOs, account names, search results and cleanup proposals are only in the memory of the
connection window – they disappear when the domain controller changes, on disconnecting and when the window closes. The
program does not keep a search history; the diagnostic log contains only counts and times. Files are created only when
exporting to CSV or HTML – where the operator points (chapter 14).

## 13. Domain controller, PDC emulator and SYSVOL

- The window reads and writes AD and SYSVOL **only on its own domain controller**. When it is not the PDC emulator (the
  one GPMC uses by default), the GPO tabs show: *GPO changes are made on X, but the PDC emulator is Y* with the *Change
  Domain Controller…* button; the program never switches the controller by itself.
- SYSVOL is opened with the window's credentials (the password only in memory, rule 2) only for the time of reading,
  planning and execution. When it is unavailable, the bar gives the reason (a connection opened by distinguished name, an
  unsupported environment, rejected credentials, access denied, no connection to the share). Then only the changes that
  write SYSVOL are disabled (a new GPO, a GPO copy, deletion, security filtering, delegation, restore, import, aligning
  versions, repairing permissions, saving administrative templates, updating the central store, copying and removing
  settings, merging GPOs); links, GPO status, rename, WMI filter and block inheritance change only AD and keep working.
  The settings search and the cleanup (chapter 12) then have only directory data.

## 14. What AD Manager writes to disk

Nothing from the directory or SYSVOL (rule 1): GPOs, links, settings, ADMX templates and differences are only in memory
while they are visible. Only the following are remembered: the visibility of GPO nodes in the tree, the last backup
folder, the backup comment template and the choice *Create a backup before deleting*. Files are created only where the
operator points: GPO backups, the copy of the central store before an update, the export of the comparison and the model
(chapter 9) to CSV, the HTML report of GPO settings, the export of diagnostics (chapter 11), of settings search results
and of cleanup proposals (chapter 12) to CSV or HTML. The diagnostics results are only in the tab's memory, and the read
of the settings of all GPOs, the search results and the cleanup proposals only in the memory of the connection window
(without a search history).
Security settings (chapter 10) are only in the tab's memory until written to SYSVOL. Vendor template packages (vendor
files, not directory data) are kept in memory; only MSI and EXE installers and CAB archives are copied to the profile's
temporary folder (installers are also unpacked there, CAB – in memory), deleted right after loading. The audit log
contains, for SYSVOL changes, the paths of files in the GPO folder or the central store, sizes and SHA-256 sums – not
the content of the files.

## 15. Limitations and pilot

- Verified by tests on a fake directory and on a Samba 4.19 lab (SYSVOL writing through `smbclient`); SYSVOL writing on
  Windows (`WindowsSysvolFileWriter`), WPF windows, screenshot 23 and the smoke test are verified only by CI on Windows.
- Administrative templates and the central store (K3) were verified by tests on a fake directory and synthetic
  templates (Polish ADMX/ADML prepared for the tests, not vendor files); the editor and the update window – CI on
  Windows. Downloading the Edge, Chrome and Firefox templates was verified on responses prepared according to the
  vendors' formats (the test environment has no access to their servers). Unpacking MSI, EXE and CAB and checking the
  signature work only on Windows: the signature check (on a .NET library signed by Microsoft) and CAB unpacking (on test
  files from `makecab`) are verified by CI on Windows, Microsoft's MSI and EXE installers – the pilot.
- To be confirmed in the pilot (Windows Server 2025, Windows 11): GPMC restores AD Manager's backups, and AD Manager
  GPMC's backups (with and without `gpreport.xml`); GPMC does not report inconsistent permissions after AD Manager's
  changes; GPMC permission level masks and the `msWMI-Parm2` format of WMI filters; a GPO created in AD Manager works
  like one created in GPMC (`gpresult /h`); a `Registry.pol` written by the template editor is identical to the file
  from Microsoft's editor for the same settings, and the client applies them as if set in GPMC; updating the central
  store from current Windows 11 / Server 2025, Microsoft 365 Apps, Edge, Chrome and Firefox packages – including that
  Microsoft's MSI and EXE installers pass the Microsoft certificate chain check (when one does not: unpack it manually,
  e.g. `msiexec /a file.msi TARGETDIR=C:\Templates`, and point to the folder through *From a folder…*).
- Modeling (K5) was verified by tests on a fake directory and on the Samba lab (a computer model in an OU with block
  inheritance consistent with `samba-tool gpo list`); in the pilot – a comparison with GPMC's *Group Policy Modeling* and
  with `gpresult /h` on a test computer.
- Security settings (K4) were verified by tests on a fake directory (writing template rows, `audit.csv` and `Groups.xml`
  while preserving the rest of the file, the plan, a conflict); in the pilot – that GPMC shows the saved settings as its
  own, `secedit /analyze` and `gpresult /h` on the client, and that the client applies advanced auditing and
  preference groups with the extension names written by AD Manager.
- Readable settings, editing comfort and diagnostics (K6) were verified by tests on a fake directory (every diagnostics
  rule on examples with Polish data, the texts of all findings in both languages); windows – CI on Windows; in the
  pilot – a comparison of setting names with the GPMC report (`Get-GPOReport`) and of diagnostics findings with
  knowledge of the domain.
- Searching, copying settings and cleanup (K7) were verified by tests on a fake directory (the sample company and the
  *Porządki – …* GPOs with Polish data: copying and removing every kind of setting with execution and undo, refusals
  including MS14-025, merging with replacement of the links, every kind of proposal together with the cases in which
  there must be no proposal); windows and screenshots 34 and 35 – CI on Windows; **not verified on a real Windows
  domain**. In the pilot: search results against *Find* in the GPMC editor and `gpresult /h`, the copied settings in
  GPMC and in `gpresult /h` on a test computer, merging two test GPOs, the cleanup proposals against the picture in
  GPMC. Not copied or merged: scripts, file and registry permissions, audit options, comments, hidden values (GPP
  passwords – MS14-025), items of collections with targeting or of disabled collections, settings in the directory; no
  copying to a new GPO or between domains; a merge does not move B's link to A's position.
- Not available: editing firewall rules, file and registry permissions, event logs, local users and other preferences,
  local groups in the user configuration, comments on template settings, editor filters by platform and keywords, old
  ADM templates, migration tables and copying GPOs between domains, Starter GPOs, comparing GPOs from another
  connection window, trying a WMI filter on a computer (requires WQL queries in the K1 remote actions), checking operator
  permissions before a change.
