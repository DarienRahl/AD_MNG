**English** · [Polski](../CELE.md)

# AD Manager – goals

What the project is for, how we will know that it works, and where it goes next. Whatever is marked **proposal** waits
for the owner's decision; the rest repeats what the [README](../../README.md), the release notes and the
[pilot checklist](PILOT.md) already say.

## What it is for and for whom

AD Manager is a Windows desktop console for Active Directory: a fast, modern successor to the `dsa.msc` snap-in with
bulk editing, the AD Recycle Bin and a directory change monitor. It is for the people who look after a domain every
day from a domain-joined workstation – administrators, the helpdesk, and whoever has to answer "who has which rights"
and "who changed this". It reads everything live from a domain controller and needs neither an installer, nor .NET,
nor RSAT.

## Product goals

| # | Goal | The sign by which we will know |
|---|---|---|
| 1 | Everyday work without `dsa.msc` | the everyday scenarios of the pilot (PILOT.md, 3.1) give the same result as the Microsoft tool, and the pilot's administrators get through their week without opening the snap-in |
| 2 | Many objects at once, without fear | every change is previewed, validated, recorded and can be rolled back; no write path exists outside the change plan |
| 3 | Fast with a large directory, never blocked | lists of tens of thousands of objects stay fluid and every long read can be cancelled; the budgets are measured in CI ([interface renewal, section 7](../dev/v-interface-renewal.md#7-accessibility-keyboard-and-performance-budgets)) |
| 4 | Show what the snap-in does not | account state, privileges, computers, Group Policy, AD CS and event logs answer their questions from live data; the pilot sections 3.2–3.5 agree with Microsoft's tools |
| 5 | Know who changed what | in the pilot on Windows Server domain controllers the AdMng Watchdog names who made a change and from where, and the notification arrives |
| 6 | Easy to use and to learn (**proposal**, track V) | every function is found by name, every page explains itself, the result of an action is always shown in the same place – measures G1–G7 of the [interface renewal](../dev/v-interface-renewal.md#1-goals-and-measures) |
| 7 | Safe to deploy | nothing of the directory stays on the workstation, no password is stored, the code is open (Apache-2.0), releases are signed and come with checksums |

## The rules that never bend

1. **Nothing of the directory is kept on the workstation.** What you see was read from the domain controller a moment
   ago and lives in memory only. On disk are settings, diagnostics without directory values and the files you export
   yourself. (The Watchdog service keeps what it monitors – on its own server, under its own rules.)
2. **Passwords are never saved and never logged.**
3. **Whatever is typed is escaped** before it becomes part of an LDAP filter or a distinguished name.
4. **One window talks to one domain controller** – for reading and for writing.
5. **Every change goes through the change plan** – preview, validation, audit, rollback – a single edit as well.
6. **The window never freezes.** Every directory call runs in the background and can be cancelled.

The exact wording, with the few exceptions, is in the [developer notes](../../CLAUDE.md).

## What it is not

- Not a copy, cache or archive of the directory, and not an offline tool.
- Not a service or a scheduler: the program does nothing while it is closed. Continuous monitoring is the separate
  AdMng Watchdog service.
- Not a web application and not a server component in the domain.
- Not a tool that changes AD quietly: there is no write without a preview.
- Not a log collector: the event log analyses read on demand and store nothing.
- Not a Microsoft product, and **not yet piloted on a production Windows AD**.

## Where we are

Version **0.15.0**, the sixteenth release. In order: the console (0.1.0), AdMng Watchdog (0.2.0) and its Linux
service with Debian packages (0.3.0), account review and e-mail messages to users (0.4.0), account privileges and
several domains in separate windows (0.5.0), computers and Group Policy in the tree (0.6.0), Correspondence through
Outlook (0.7.0, 0.9.1), the Administrative Templates editor (0.8.0), "what will the computer get" (0.9.0), GPO
security settings (0.10.0), GPO readability and diagnostics (0.11.0), event logs (0.12.0), stabilization and pilot
readiness (0.13.0), GPO search, copying and cleanup (0.14.0), creating multiple accounts from Excel and the read-only
AD CS overview (0.15.0).

On the [roadmap](../../README.md#-roadmap) phase 0 is done; phases 1 and 2 and the tracks W, U, K, E and P1 are
implemented and marked 🟡, because they wait for a pilot on Windows Server AD. The releases are not signed yet.

## Goals of the next releases (proposal)

The agreed feature order is **R1** (PowerShell window, reads) → **P2** (issuing certificates) → **N** (DNS and DHCP).
The proposal is to weave the stages of the interface renewal ([track V](../dev/v-interface-renewal.md#8-stages))
between them, so that every new window is built in the new style and nothing is restyled twice:

| Order | Feature | Interface renewal |
|---|---|---|
| 1 | – | V1 – look: tokens, new icons, first motion |
| 2 | R1 – PowerShell window | V2 – status line, cancellable action button |
| 3 | – | V3 – page skeleton: header, toolbar card, list toolbar |
| 4 | P2 – issuing certificates | – |
| 5 | – | V4 – shell: title bar with search, areas, home page |
| 6 | N – DNS and DHCP, first stage | V5 – settings as cards, one dialog style |
| 7 | – | V6 – the large windows; V7 – accessibility and performance audit |

Whether the WPF app is evolved in place (recommended) or a WinUI 3 shell is started is the owner's decision – see the
[technology decision](../dev/v-interface-renewal.md#3-technology-decision-the-owners-decision).

## The road to 1.0 (proposal)

The READMEs do not define version 1.0. They name what is still open, and the proposal is to call it 1.0 when:

1. **The pilot is done** on a Windows Server AD according to [PILOT.md](PILOT.md): its result table is filled in, no
   blocking difference from the Microsoft tools is open, and phases 1 and 2 change from 🟡 to ✅.
2. **The packages are signed.** Code signing through SignPath Foundation is being set up
   ([code signing policy](../../README.md#-code-signing-policy)); the releases published so far are not signed.
3. **There is an installer** next to the zip archive – the signed MSI of
   [phase 7](../../README.md#phase-7--deployment-and-extras-).
4. **AdMng Watchdog has passed its pilot** on Windows Server domain controllers
   ([WATCHDOG-LINUX.md](WATCHDOG-LINUX.md)).
5. **The interface renewal has reached the shell** (V4), so that 1.0 does not change its face right after release.
