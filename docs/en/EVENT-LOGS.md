**English** · [Polski](../DZIENNIKI-ZDARZEN.md)

# Event logs in AD Manager

Starting with track **E**, AD Manager reads the event logs of domain controllers, servers and workstations, as well as
`.evtx` files – without Event Viewer and without installing anything – and **analyzes** them right away: **the lockout
source** (with a jump to the source computer) and **failed logons** (password guessing, password spraying, account
enumeration, success after a series of failures). This is a **separate feature, independent of AdMng Watchdog**: the
Watchdog continuously collects selected DC events and keeps their history, whereas the event logs in AD Manager are for ad
hoc investigation – any channel, any computer, history reaching as far back as the log itself.

Developer specification (in English): [dev/e-event-logs.md](../dev/e-event-logs.md).

---

## 1. Where it is in the program

| Place | What it gives |
|---|---|
| *Tools → Event logs…*, palette `events.open` | the **Event logs** tab (the reader, chapter 3) |
| *Tools → Failed logons…*, palette `events.failedLogons` | the **Failed logons** tab (chapter 5) |
| The user's context menu, the *Account* tab in properties, *Account review* (locked accounts), palette `events.lockout` | **Find the lockout source…** – the **Lockout source – <account>** tab (chapter 4) |
| The computer's context menu, the *Status* page in properties, palette `events.computer` | **Event log of this computer** – the reader set to this computer (launching Event Viewer remains) |

Each tab belongs to its connection window and uses its credentials.

## 2. Requirements

- **AD Manager running on Windows** and the window connected with a Windows account or domain credentials (Kerberos
  logon; a window connected with a distinguished name – a simple bind – cannot read logs). Computers are given by their
  **full DNS name** (`waw-pc001.contoso.test`) – Kerberos does not work with an IP address or a short name.
- On the computers being read (DCs too):
  - firewall rules from the **Remote Event Log Management** group (RPC, RPC endpoint mapper, named pipes) – preferably
    only from the subnet of the administrators' workstations;
  - the operator's account in the **Event Log Readers** group – sufficient for the *Security* channel, domain
    administrator permissions are not needed. On a DC it is worth using a separate domain group nested in *Event Log
    Readers* and treating it as Tier 0 (DC logs show administrators' actions).
- When something is missing, the tab says what: the computer does not respond, no RPC connection (firewall or the
  computer is off), access denied (group, channel permissions), no such channel, the computer rejected the query – always
  with a hint on what to do.
- **The analyses need auditing on the DCs**: lockouts 4740 – *User Account Management* (success); 4771 – *Kerberos
  Authentication Service* (failure); 4776 – *Credential Validation* (failure); 4625 – *Logon* (failure); 4624 – *Logon*
  (success). The tabs show whether these events are recorded at all (chapter 4). Auditing settings are changed in the GPO
  security settings editor
  ([ZASADY-GRUPY.md, chapter 10](GROUP-POLICY.md#10-security-settings-local-groups-rights-account-policies-auditing-services)).

## 3. The reader – the "Event logs" tab

- **Sources:** all domain controllers (the PDC emulator highlighted), computers entered by full DNS name or picked from AD
  (*Pick from AD…*) and `.evtx` files (*Open .evtx file…*; the file is read from the oldest events).
- **Query:** the channel (a list of common channels or *Load channels* of the computer), time (*Last hour*, *Last 24
  hours* – the default, *Last 7 days*, *Custom*), identifiers (`4625, 4740, 4768-4771`), levels, providers, auditing of
  successes or failures, and for the *Security* channel also the account, SID, IP address and workstation. The program
  builds an XPath query from these and inserts the values safely (they cannot be used to change the query). **XPath mode
  (expert)** accepts your own expression after a syntax check; the target computer verifies the rest (in this mode the
  time range from the list does not apply).
- **Search** reads at most four computers at once, with progress for each; *Stop* stops the read on the target computers.
  Results: a shared **timeline** of all sources (newest first), at most 50,000 events – with more, the tab says how many
  were skipped and asks you to narrow the query. *Ctrl+F* filters the loaded events.
- **Event details:** all fields by name with explanations of the codes in plain language (e.g. `0xC000006A` – wrong password,
  Kerberos `0x18`, logon types, encryption types with weak RC4 and DES marked), accounts and SIDs linked to AD objects,
  **Show message** – the full description of the event read from the computer.
- **Coverage and access:** for each source the oldest event, the number of events, the size, the log mode (overwriting,
  archiving, no overwriting) and the warning *"DC01 reaches back only 6 hours"* when the range starts earlier than the
  log; on an error – the cause and a hint.
- **Saved queries:** name, channel, identifiers, levels, providers, conditions and XPath as typed, and the list of
  computers (without file paths and without values read from the logs).
- **Export** to CSV or HTML only to a file chosen by the operator; the file begins with a note that it contains personal
  data (accounts, addresses, computer names).

## 4. Lockout source

**Check** does in one step what usually takes LockoutStatus and EventCombMT:

1. Reads the account and the lockout policy (the domain policy or the account's fine-grained password policy – PSO).
2. On **every domain controller** reads the account state: locked or not, the bad password counter, the time of the last
   bad password and of the last logon; points to the DC where the lockout was recorded (the `lockoutTime` replication
   metadata).
3. Reads the *Security* logs of all DCs (the PDC emulator and the DC where the lockout was recorded first): lockouts
   4740, unlocks 4767, failed attempts 4771, 4776 and 4625 of this account in the range (by default 24 hours before the
   last lockout). Then on each DC that was read it briefly checks (one event of any account) whether the required events
   that were absent from the read are in the log at all, and reads the failed attempts of all accounts from a few of the
   most frequent sources (proxy server detection).
4. Shows:
   - **Summary** – whether the account is locked now and on which DCs, the last lockout, the lockout threshold and window.
   - **Sources of the attempts** in order of importance: the calling computer from 4740, the IP address from 4771, the
     workstation from 4776 and 4625 – with the number of attempts, bad passwords, attempts before the lockout, times,
     controllers and explained codes. Hints: *probably a proxy server* (Exchange, ADFS, NPS, Remote Desktop Gateway – by
     name or by many accounts from the same source), *domain controller – only relayed the attempt*.
   - **Timeline**, the table of **domain controllers** (account state and log coverage) and **notes**: missing calling
     computer, gaps in log coverage, a full log without overwriting, the oldest events skipped (memory limit), attempts
     despite the lockout (*most likely a saved password – service, scheduled task, mapped drive, phone*), time
     difference, threshold 0. A copy of an NTLM failure that another DC retried on the PDC emulator counts once. *Locked
     now* means the state at the moment of the check, also for a custom range.
   - **Auditing on the domain controllers**: for each DC and required event – *seen*, *no event of any account although
     the log covers the window – probably not audited* (only after a check without the account filter) or *unknown*; with
     a jump to the Group Policy model of the DC (*Show in model*) and to *Group Policy diagnostics*.
5. **Check the source computer** (for a computer or an IP address – the name is resolved through DNS): through remote
   actions ([KOMPUTERY.md](COMPUTERS.md)) shows this account's **sessions**, **services** and **scheduled tasks** running as
   this account (with tasks that have a saved password marked); *Open remote actions* opens the computer's window.

A locked account can be unlocked with the ordinary command (through the change plan preview). When the window is
connected to AdMng Watchdog, *History in Watchdog* shows a longer history of account changes and lockouts.

## 5. Failed logons

For the chosen range (*Last hour*, *Last 24 hours*, *Last 7 days*) the tab reads the failures 4625, 4771, 4776 and 4768
from all DCs, and then successful logons only of the accounts that could have been guessed. Findings:

- **Password guessing** – many bad passwords of one account from one source;
- **Password spraying** – many accounts from one source in a short time, a few attempts per account (fewer than the
  password guessing threshold, so also "lockout threshold − 1"); one finding per source and continuous period;
- **Account enumeration** – many nonexistent names from one source; the names of nonexistent accounts are hidden until you
  click *Show* (they are sometimes passwords typed into the name field);
- **Success after failures** – a successful logon from the same source after a series of attempts.

Each finding has a severity, an explanation, accounts, numbers, times, codes and a list of events; the severity counters
filter the list, and there are also filters by kind and by text, *Top accounts*, *Top sources* and *Failure codes*.
**Thresholds** (expandable) change the sensitivity. The addresses of domain controllers are counted but are not findings
(DCs only relay attempts); a copy of an NTLM failure retried on the PDC emulator counts once, and a ticket denial for a
disabled, expired or locked-out account (4768 `0x12`) is counted but is not a password attempt. Auditing of successful
logons is checked separately, without the account filter (one event per DC). Caveats: NAT, VPNs and proxy servers combine
many users under one address, and the workstation name in 4776 is supplied by the client.

## 6. What AD Manager writes to disk

Nothing from the event logs (rule 1): events, coverage, analysis results and messages are only in the memory of the tab
while it is open; the program's diagnostic log does not contain event content. Only saved queries are stored (chapter 3).
Files are created only where the operator points (CSV and HTML export). The window's password is only in memory (rule 2).

## 7. Limitations and pilot

- Verified with tests on fake logs (Polish accounts and computers, lockout, spraying and password guessing scenarios) and
  with UI tests on Windows; reading from real computers over RPC and from `.evtx` files – a pilot on Windows Server AD
  (comparison with LockoutStatus, EventCombMT and Event Viewer).
- Reading only over RPC; WinRM (summaries computed on the target computer) – later.
- Further track E analyses (Kerberos and RC4, NTLM and NTLMv1, LDAP signing, logon history, account and group changes,
  Group Policy on the computer, DC health, auditing integrity) – in the following stages (README, *Track E*).
