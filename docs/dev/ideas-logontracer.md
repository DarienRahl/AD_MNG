# Ideas from LogonTracer (note for later review)

**Status:** backlog note, nothing decided or planned. Written on 2026-11-04 at the user's request so that the ideas can be
reviewed later against tracks E (event logs), U4 (privileged access) and the AdMng Watchdog.

[LogonTracer](https://github.com/JPCERTCC/LogonTracer) (JPCERT/CC) investigates malicious Windows logons by turning the
Security log into a graph of accounts ↔ hosts/IP addresses. Stack: Python, Flask, Neo4j, Cytoscape.js; inputs EVTX,
XML or Elasticsearch (winlogbeat). Events it reads: 4624, 4625, 4768, 4769, 4776 (logons), 4672 (special privileges),
4720/4726 (accounts), 4728/4732/4756 and 4729/4733/4757 (group membership), 4719 (audit policy), 4662, 5137, 5141
(directory objects). Analysis: PageRank, a hidden Markov model and ChangeFinder to rank suspicious accounts and hosts;
a timeline, a diff graph between two periods, Sigma rules, an optional OpenAI agent, per-case Neo4j databases.

Ideas only – no code is to be copied (different language and stack; check its LICENSE.txt before quoting anything).

## What AD_MNG already covers

Track E (README.pl.md, *Ścieżka E*; `docs/dev/e-event-logs.md`) already reads the same events and plans most of the
per-event analyses: lockout source, password spraying, Kerberos/RC4, NTLM, "where and when a user logged on", account and
group changes, DCSync (4662), audit policy changes (4719); Sigma rules are a "later" item. The Watchdog already ingests
4624/4625/4771/4776 from the DCs (kept 48 h) and attributes directory changes.

## Candidate features

| # | Idea (from LogonTracer) | Where in AD_MNG | Value / notes |
|---|---|---|---|
| L1 | **Logon graph**: accounts ↔ hosts/IPs as an interactive graph, edges weighted by logon count and type, privileged accounts (U4, Tier 0) highlighted, click-through to the AD object | Track E, new view next to analysis 6 ("where and when a user logged on") | Makes lateral movement and credential exposure visible at a glance. WPF graph control needed (layout library – licence must be Apache-compatible). In memory only (rule 1). |
| L2 | **Tiering violations**: Tier 0 / privileged accounts logging on to Tier 1/2 hosts or non-PAW workstations | Track E analysis + Watchdog rule | Strongest fit: AD_MNG already classifies privileged accounts (U4) and PAW computers. LogonTracer shows it only implicitly in the graph. |
| L3 | **Ranking of accounts and hosts** (PageRank on the logon graph): accounts used on unusually many hosts, hosts many accounts log on to | Track E analysis | Cheap to compute in memory; explain the score in plain words instead of a raw number. |
| L4 | **First-seen / diff between periods**: new account → host pairs compared with a baseline period ("first logon of this admin on this server") | Watchdog alert (needs a baseline) or track E diff of two time ranges | The Watchdog keeps raw logons only 48 h (L8 in `watchdog-linux-design.md`); a baseline would need aggregated, value-minimal edges with their own retention – an ADR-0008/0009 amendment. |
| L5 | **Time-series anomaly** (ChangeFinder): sudden spikes in logons or failures per account/host | Watchdog (daily report) | Needs per-hour counters only, no raw records; tune thresholds to avoid noise. |
| L6 | **Sequence anomaly** (hidden Markov model over event-ID sequences per account) | Later, research | Unclear value vs. effort and false positives; explainability is weak. |
| L7 | **Timeline / heat map** per account or host (hour × day) | Track E (analysis 6) | Simple and useful for helpdesk questions ("when was this account used?"). |
| L8 | **4672 special logons** as a separate signal (admin logons) | Track E analysis 6, Watchdog | Small addition to the event set. |
| L9 | **Elasticsearch / winlogbeat as a source** | Watchdog source alongside OpenWEC | Only if a customer already ships logs to Elastic. |

Out of scope: the OpenAI agent (sends log data to an external service – conflicts with the privacy policy) and Neo4j
case databases (rule 1 for the app; the Watchdog has its own SQLite store).

## Open questions

- Personal data: logon graphs of named employees fall under the same GDPR / Polish Labour Code (art. 22³) question as
  track E ("to be agreed with the team") – a purpose prompt and a value-free search trail may be required.
- Which graph/layout component for WPF (licence, performance with thousands of nodes)?
- L4/L5 in the Watchdog: which aggregates may be stored and for how long (ADR amendment)?
