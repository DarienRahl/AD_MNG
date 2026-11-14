# Developer notes – index

Design notes and working documents for people (and agents) who change AD Manager. They are in English; the product
documentation is in [`docs/`](../) (Polish) and [`docs/en/`](../en/) (English). The rules every change follows are in
[`CLAUDE.md`](../../CLAUDE.md); the project goals are in [GOALS.md](../en/GOALS.md).

A design note is written before a stage is built and gets an "as built" part when it is done – where the two disagree,
"as built" wins.

## Working on the project

| Document | What it is for |
|---|---|
| [releasing.md](releasing.md) | How a release is made: `tools/release/release.sh`, the draft `docs/releases/NEXT.md`, the checks, retrying |
| [multi-window.md](multi-window.md) | One connection window = one DI scope; what must be scoped, how windows are created |
| [s-pilot-readiness.md](s-pilot-readiness.md) | Stage S: test reliability, the readiness check, the problem report, the pilot checklist |

## Planned

| Document | Track | State |
|---|---|---|
| [v-interface-renewal.md](v-interface-renewal.md) | V – interface renewal (look, motion, icons) | V1 built (as-built notes V1a–V1c in section 8), V2–V7 planned; decisions for the owner in section 9; clickable prototype: [prototypes/interface-renewal.html](prototypes/interface-renewal.html) |
| [ideas-logontracer.md](ideas-logontracer.md) | E – event logs | ideas for later review |

## Design notes of what is built

| Document | Track or phase |
|---|---|
| [phase1-navigation.md](phase1-navigation.md) | Phase 1 – well-known containers (WKGUID) and VLV lists |
| [phase1-copy.md](phase1-copy.md) | Phase 1 – copying values |
| [phase1-changedc.md](phase1-changedc.md) | Phase 1 – change the domain controller during a session |
| [phase1-logonhours.md](phase1-logonhours.md) | Phase 1 – logon hours |
| [phase3-bulk-create.md](phase3-bulk-create.md) | Phase 3 – creating multiple accounts (F3) |
| [u1-user-messages.md](u1-user-messages.md) | U1 – e-mail messages to users |
| [u1c-u1d-rich-html-and-salutations.md](u1c-u1d-rich-html-and-salutations.md) | U1c, U1d – formatted messages and salutations |
| [u2-account-review.md](u2-account-review.md) | U2 – account review |
| [u4-privileged-access.md](u4-privileged-access.md) | U4 – privileged access and saved filters |
| [u5-outlook-correspondence.md](u5-outlook-correspondence.md) | U5 – correspondence through Outlook |
| [k-computers-and-gpo.md](k-computers-and-gpo.md) | K – computers and Group Policy (K1–K7) |
| [e-event-logs.md](e-event-logs.md) | E – event log reader and analyses |
| [p-certificate-services.md](p-certificate-services.md) | P – AD Certificate Services (P1 built, P2–P5 planned) |
| [watchdog-spec.md](watchdog-spec.md) | W – AdMng Watchdog, specification v1 |
| [watchdog-linux-design.md](watchdog-linux-design.md) | W – the service on Debian (stage B; wins over the specification where they disagree) |

## Related

- Icon set: [`docs/assets/icons`](../assets/icons/README.md) – sources, palette, gallery, how to add an icon.
- Test lab (Samba AD DC, Mailpit): [`tools/testlab`](../../tools/testlab/README.md).
- Architecture decisions: [`docs/en/adr`](../en/adr/).
