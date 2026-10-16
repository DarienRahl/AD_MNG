#!/usr/bin/env python3
"""Generates deterministic test data (LDIF) for the AD_MNG test lab.

The data imitates a typical Polish company directory: OU per department for
employees, OU per location for computers, groups, contacts, service accounts.
Values with Polish diacritics are intentional - they exercise DN escaping,
UTF-8 handling and the "remove diacritics" template function.

Usage:
    python3 generate_ldif.py --base DC=contoso,DC=test --domain contoso.test \
        --users 400 --computers 250 --bulk-users 0 > seed.ldif

Only the Python standard library is used. Output is meant to be loaded over an
encrypted connection (ldaps) because user passwords are set via unicodePwd.
"""
from __future__ import annotations

import argparse
import base64
import random
import sys
import unicodedata
from datetime import datetime, timedelta, timezone

FIRST_NAMES_F = [
    "Anna", "Maria", "Katarzyna", "Małgorzata", "Agnieszka", "Barbara", "Ewa",
    "Krystyna", "Elżbieta", "Zofia", "Joanna", "Magdalena", "Monika", "Aleksandra",
    "Natalia", "Beata", "Dorota", "Jolanta", "Grażyna", "Iwona", "Łucja", "Żaneta",
]
FIRST_NAMES_M = [
    "Piotr", "Krzysztof", "Andrzej", "Tomasz", "Paweł", "Jan", "Michał", "Marcin",
    "Stanisław", "Jakub", "Adam", "Marek", "Łukasz", "Grzegorz", "Wojciech",
    "Mateusz", "Dariusz", "Rafał", "Kamil", "Sławomir", "Zbigniew", "Bartłomiej",
]
LAST_NAMES = [
    ("Nowak", "Nowak"), ("Kowalski", "Kowalska"), ("Wiśniewski", "Wiśniewska"),
    ("Wójcik", "Wójcik"), ("Kowalczyk", "Kowalczyk"), ("Kamiński", "Kamińska"),
    ("Lewandowski", "Lewandowska"), ("Zieliński", "Zielińska"), ("Szymański", "Szymańska"),
    ("Woźniak", "Woźniak"), ("Dąbrowski", "Dąbrowska"), ("Kozłowski", "Kozłowska"),
    ("Jankowski", "Jankowska"), ("Mazur", "Mazur"), ("Kwiatkowski", "Kwiatkowska"),
    ("Krawczyk", "Krawczyk"), ("Piotrowski", "Piotrowska"), ("Grabowski", "Grabowska"),
    ("Nowakowski", "Nowakowska"), ("Pawłowski", "Pawłowska"), ("Michalski", "Michalska"),
    ("Król", "Król"), ("Wieczorek", "Wieczorek"), ("Jabłoński", "Jabłońska"),
    ("Wróbel", "Wróbel"), ("Majewski", "Majewska"), ("Olszewski", "Olszewska"),
    ("Stępień", "Stępień"), ("Malinowski", "Malinowska"), ("Jaworski", "Jaworska"),
    ("Górski", "Górska"), ("Rutkowski", "Rutkowska"), ("Michalak", "Michalak"),
    ("Sikora", "Sikora"), ("Ostrowski", "Ostrowska"), ("Baran", "Baran"),
    ("Duda", "Duda"), ("Szewczyk", "Szewczyk"), ("Tomaszewski", "Tomaszewska"),
    ("Pietrzak", "Pietrzak"), ("Marciniak", "Marciniak"), ("Wróblewski", "Wróblewska"),
    ("Zalewski", "Zalewska"), ("Jakubowski", "Jakubowska"), ("Jasiński", "Jasińska"),
    ("Zawadzki", "Zawadzka"), ("Sadowski", "Sadowska"), ("Bąk", "Bąk"),
    ("Chmielewski", "Chmielewska"), ("Włodarczyk", "Włodarczyk"), ("Borkowski", "Borkowska"),
    ("Czarnecki", "Czarnecka"), ("Sawicki", "Sawicka"), ("Sokołowski", "Sokołowska"),
    ("Urbański", "Urbańska"), ("Kubiak", "Kubiak"), ("Maciejewski", "Maciejewska"),
    ("Szczepański", "Szczepańska"), ("Kucharski", "Kucharska"), ("Wilk", "Wilk"),
    ("Kalinowski", "Kalinowska"), ("Lis", "Lis"), ("Mazurek", "Mazurek"),
    ("Wysocki", "Wysocka"), ("Adamski", "Adamska"), ("Kaźmierczak", "Kaźmierczak"),
    ("Wasilewski", "Wasilewska"), ("Sobczak", "Sobczak"), ("Czerwiński", "Czerwińska"),
    ("Andrzejewski", "Andrzejewska"), ("Cieślak", "Cieślak"), ("Głowacki", "Głowacka"),
    ("Zakrzewski", "Zakrzewska"), ("Kołodziej", "Kołodziej"), ("Sikorski", "Sikorska"),
    ("Krajewski", "Krajewska"), ("Gajewski", "Gajewska"), ("Szymczak", "Szymczak"),
    ("Szulc", "Szulc"), ("Baranowski", "Baranowska"), ("Laskowski", "Laskowska"),
    ("Brzeziński", "Brzezińska"), ("Makowski", "Makowska"), ("Ziółkowski", "Ziółkowska"),
    ("Przybylski", "Przybylska"),
]

DEPARTMENTS = [
    # (OU name, department attribute, group suffix, titles)
    ("IT", "IT", "IT", ["Administrator systemów", "Programista", "Specjalista ds. wsparcia", "Kierownik IT"]),
    ("Księgowość", "Księgowość", "Ksiegowosc", ["Księgowa", "Główna księgowa", "Specjalista ds. płac"]),
    ("Sprzedaż", "Sprzedaż", "Sprzedaz", ["Handlowiec", "Key Account Manager", "Dyrektor sprzedaży"]),
    ("Kadry", "Kadry i płace", "Kadry", ["Specjalista ds. kadr", "Kierownik HR"]),
    ("Marketing", "Marketing", "Marketing", ["Specjalista ds. marketingu", "Grafik", "Kierownik marketingu"]),
    ("Logistyka", "Logistyka", "Logistyka", ["Magazynier", "Spedytor", "Kierownik logistyki"]),
    ("Zarząd", "Zarząd", "Zarzad", ["Prezes zarządu", "Członek zarządu", "Asystentka zarządu"]),
]

LOCATIONS = [
    # (OU name, city, computer name prefix, phone area code)
    ("Warszawa", "Warszawa", "WAW", "22"),
    ("Kraków", "Kraków", "KRK", "12"),
    ("Gdańsk", "Gdańsk", "GDN", "58"),
    ("Wrocław", "Wrocław", "WRO", "71"),
    ("Poznań", "Poznań", "POZ", "61"),
]

OS_VERSIONS = [
    ("Windows 11 Enterprise", "10.0 (26100)"),
    ("Windows 11 Enterprise", "10.0 (22631)"),
    ("Windows 10 Enterprise", "10.0 (19045)"),
    ("Windows 11 Pro", "10.0 (26100)"),
]

GROUP_TYPE_GLOBAL_SECURITY = -2147483646
GROUP_TYPE_DOMAIN_LOCAL_SECURITY = -2147483644
GROUP_TYPE_UNIVERSAL_SECURITY = -2147483640
GROUP_TYPE_GLOBAL_DISTRIBUTION = 2
GROUP_TYPE_UNIVERSAL_DISTRIBUTION = 8

UAC_NORMAL = 512
UAC_DISABLED = 514
UAC_NORMAL_DONT_EXPIRE = 66048
UAC_WORKSTATION = 4096

USER_PASSWORD = "Passw0rd!Lab2026"


def strip_diacritics(value: str) -> str:
    table = str.maketrans({"ł": "l", "Ł": "L"})
    normalized = unicodedata.normalize("NFKD", value.translate(table))
    return "".join(c for c in normalized if not unicodedata.combining(c))


def escape_rdn_value(value: str) -> str:
    """RFC 4514 escaping of an attribute value used in an RDN."""
    out = []
    for i, ch in enumerate(value):
        if ch in ',+"\\<>;=':
            out.append("\\" + ch)
        elif (i == 0 and ch in " #") or (i == len(value) - 1 and ch == " "):
            out.append("\\" + ch)
        else:
            out.append(ch)
    return "".join(out)


def filetime(dt: datetime) -> int:
    epoch = datetime(1601, 1, 1, tzinfo=timezone.utc)
    return int((dt - epoch).total_seconds() * 10_000_000)


def ldif_line(attr: str, value) -> str:
    text = str(value)
    needs_b64 = (
        any(ord(c) > 127 for c in text)
        or text.startswith((" ", ":", "<"))
        or text.endswith(" ")
        or "\n" in text
    )
    if needs_b64:
        return f"{attr}:: {base64.b64encode(text.encode('utf-8')).decode('ascii')}"
    return f"{attr}: {text}"


def password_line(password: str) -> str:
    raw = f'"{password}"'.encode("utf-16-le")
    return f"unicodePwd:: {base64.b64encode(raw).decode('ascii')}"


class Writer:
    def __init__(self, stream):
        self.stream = stream
        self.count = 0

    def entry(self, dn: str, attrs: list[tuple[str, object]], extra_lines: list[str] | None = None):
        lines = [ldif_line("dn", dn)]
        for attr, value in attrs:
            if value is None or value == "":
                continue
            if isinstance(value, (list, tuple)):
                lines.extend(ldif_line(attr, v) for v in value)
            else:
                lines.append(ldif_line(attr, value))
        if extra_lines:
            lines.extend(extra_lines)
        self.stream.write("\n".join(lines) + "\n\n")
        self.count += 1

    def modify_add_values(self, dn: str, attr: str, values: list[str]):
        if not values:
            return
        lines = [ldif_line("dn", dn), "changetype: modify", f"add: {attr}"]
        lines.extend(ldif_line(attr, v) for v in values)
        lines.append("-")
        self.stream.write("\n".join(lines) + "\n\n")


def ou(writer: Writer, name: str, parent: str, description: str = "") -> str:
    dn = f"OU={escape_rdn_value(name)},{parent}"
    writer.entry(dn, [("objectClass", ["top", "organizationalUnit"]), ("ou", name), ("description", description)])
    return dn


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", default="DC=contoso,DC=test")
    parser.add_argument("--domain", default="contoso.test")
    parser.add_argument("--company", default="Contoso Sp. z o.o.")
    parser.add_argument("--users", type=int, default=400, help="regular employees spread across departments")
    parser.add_argument("--computers", type=int, default=250)
    parser.add_argument("--bulk-users", type=int, default=0, help="extra users in OU=Masowe for performance tests")
    parser.add_argument("--seed", type=int, default=2026)
    parser.add_argument("--now", default="2026-10-01T00:00:00+00:00", help="reference time for lastLogonTimestamp")
    args = parser.parse_args()

    rnd = random.Random(args.seed)
    now = datetime.fromisoformat(args.now)
    w = Writer(sys.stdout)
    base = args.base

    root = ou(w, "Firma", base, "Główna struktura organizacyjna")
    employees = ou(w, "Pracownicy", root, "Konta pracowników wg działów")
    computers = ou(w, "Komputery", root, "Stacje robocze wg lokalizacji")
    servers = ou(w, "Serwery", root, "Serwery członkowskie")
    groups = ou(w, "Grupy", root, "Grupy zabezpieczeń i dystrybucyjne")
    sec_groups = ou(w, "Bezpieczeństwa", groups)
    dist_groups = ou(w, "Dystrybucyjne", groups)
    service = ou(w, "Konta serwisowe", root, "Konta usług i konta testowe aplikacji")
    disabled_ou = ou(w, "Wyłączone", root, "Konta wyłączone, do usunięcia po 90 dniach")
    contacts_ou = ou(w, "Kontakty", root, "Kontakty zewnętrzne")

    dept_ous = {d[0]: ou(w, d[0], employees, f"Dział {d[1]}") for d in DEPARTMENTS}
    loc_ous = {}
    for loc in LOCATIONS:
        loc_dn = ou(w, loc[0], computers, f"Komputery - {loc[1]}")
        loc_ous[loc[0]] = {
            "root": loc_dn,
            "Laptopy": ou(w, "Laptopy", loc_dn),
            "Stacjonarne": ou(w, "Stacjonarne", loc_dn),
        }

    used_sam: set[str] = set()
    users_by_dept: dict[str, list[str]] = {d[0]: [] for d in DEPARTMENTS}
    managers: dict[str, str] = {}
    all_user_dns: list[str] = []
    employee_counter = 1000

    def make_user(parent_dn: str, dept, title: str, uac: int, location=None, manager_dn=None,
                  description: str = "", first=None, last=None, female=None) -> str:
        nonlocal employee_counter
        if female is None:
            female = rnd.random() < 0.5
        if first is None:
            first = rnd.choice(FIRST_NAMES_F if female else FIRST_NAMES_M)
        if last is None:
            pair = rnd.choice(LAST_NAMES)
            last = pair[1] if female else pair[0]
        base_sam = strip_diacritics(f"{first}.{last}").lower()
        sam = base_sam[:20]
        n = 2
        while sam in used_sam:
            suffix = str(n)
            sam = base_sam[: 20 - len(suffix)] + suffix
            n += 1
        used_sam.add(sam)
        display = f"{first} {last}"
        cn = display if display not in cn_used.setdefault(parent_dn, set()) else f"{display} ({sam})"
        cn_used[parent_dn].add(cn)
        dn = f"CN={escape_rdn_value(cn)},{parent_dn}"
        loc = location or rnd.choice(LOCATIONS)
        employee_counter += 1
        upn = f"{sam}@{args.domain}"
        mail = f"{sam}@{args.domain}"
        phone = f"+48 {loc[3]} {rnd.randint(100, 999)} {rnd.randint(10, 99)} {rnd.randint(10, 99)}"
        mobile = f"+48 {rnd.randint(500, 799)} {rnd.randint(100, 999)} {rnd.randint(100, 999)}"
        attrs = [
            ("objectClass", ["top", "person", "organizationalPerson", "user"]),
            ("cn", cn),
            ("givenName", first),
            ("sn", last),
            ("displayName", display),
            ("sAMAccountName", sam),
            ("userPrincipalName", upn),
            ("mail", mail),
            ("proxyAddresses", [f"SMTP:{mail}", f"smtp:{sam}@contoso.mail.onmicrosoft.com"]),
            ("department", dept[1] if dept else None),
            ("title", title),
            ("company", args.company),
            ("physicalDeliveryOfficeName", loc[1]),
            ("l", loc[1]),
            ("c", "PL"),
            ("co", "Polska"),
            ("countryCode", 616),
            ("telephoneNumber", phone),
            ("mobile", mobile if rnd.random() < 0.7 else None),
            ("employeeID", str(employee_counter)),
            ("description", description),
            ("manager", manager_dn),
            ("userAccountControl", uac),
        ]
        w.entry(dn, attrs, [password_line(USER_PASSWORD)])
        all_user_dns.append(dn)
        return dn

    cn_used: dict[str, set[str]] = {}

    # Department managers first, so that other users can reference them.
    for dept in DEPARTMENTS:
        mgr_title = dept[3][-1]
        dn = make_user(dept_ous[dept[0]], dept, mgr_title, UAC_NORMAL, location=LOCATIONS[0])
        managers[dept[0]] = dn
        users_by_dept[dept[0]].append(dn)

    for _ in range(args.users):
        dept = rnd.choice(DEPARTMENTS)
        title = rnd.choice(dept[3][:-1] or dept[3])
        uac = UAC_NORMAL if rnd.random() > 0.04 else UAC_DISABLED
        if rnd.random() < 0.05:
            uac = UAC_NORMAL_DONT_EXPIRE
        dn = make_user(dept_ous[dept[0]], dept, title, uac, manager_dn=managers[dept[0]])
        users_by_dept[dept[0]].append(dn)

    disabled_users = []
    for i in range(15):
        dept = rnd.choice(DEPARTMENTS)
        disabled_users.append(make_user(disabled_ou, dept, rnd.choice(dept[3]), UAC_DISABLED,
                                        description=f"Wyłączone {(now - timedelta(days=rnd.randint(1, 120))).date()} - zwolnienie"))

    # Test accounts with known passwords used by integration tests and manual testing.
    helpdesk = make_user(service, None, "Helpdesk (konto testowe)", UAC_NORMAL_DONT_EXPIRE,
                         first="Helpdesk", last="Test", description="Konto z delegowanymi uprawnieniami do OU=Pracownicy")
    readonly = make_user(service, None, "Tylko odczyt (konto testowe)", UAC_NORMAL_DONT_EXPIRE,
                         first="Odczyt", last="Test", description="Zwykły użytkownik domeny - tylko odczyt")
    svc_names = ["svc_backup", "svc_sql", "svc_web", "svc_scan"]
    svc_dns = []
    for name in svc_names:
        dn = f"CN={name},{service}"
        used_sam.add(name)
        w.entry(dn, [
            ("objectClass", ["top", "person", "organizationalPerson", "user"]),
            ("cn", name), ("sAMAccountName", name), ("userPrincipalName", f"{name}@{args.domain}"),
            ("description", f"Konto usługi {name[4:]}"), ("userAccountControl", UAC_NORMAL_DONT_EXPIRE),
        ], [password_line(USER_PASSWORD + name)])
        svc_dns.append(dn)

    bulk_dns = []
    if args.bulk_users > 0:
        bulk = ou(w, "Masowe", root, "Duża liczba kont do testów wydajności")
        for i in range(args.bulk_users):
            dept = DEPARTMENTS[i % len(DEPARTMENTS)]
            bulk_dns.append(make_user(bulk, dept, rnd.choice(dept[3]), UAC_NORMAL))

    # Computers.
    computer_dns = []
    counters = {loc[2]: 0 for loc in LOCATIONS}
    for i in range(args.computers):
        loc = rnd.choice(LOCATIONS)
        kind = "Laptopy" if rnd.random() < 0.6 else "Stacjonarne"
        counters[loc[2]] += 1
        name = f"{'NB' if kind == 'Laptopy' else 'PC'}-{loc[2]}-{counters[loc[2]]:04d}"
        os_name, os_ver = rnd.choice(OS_VERSIONS)
        days_ago = rnd.choice([rnd.randint(0, 14)] * 6 + [rnd.randint(15, 89)] * 2 + [rnd.randint(90, 400)])
        owner = rnd.choice(all_user_dns)
        dn = f"CN={name},{loc_ous[loc[0]][kind]}"
        w.entry(dn, [
            ("objectClass", ["top", "person", "organizationalPerson", "user", "computer"]),
            ("cn", name),
            ("sAMAccountName", f"{name}$"),
            ("dNSHostName", f"{name.lower()}.{args.domain}"),
            ("userAccountControl", UAC_WORKSTATION if rnd.random() > 0.05 else UAC_WORKSTATION | 2),
            ("operatingSystem", os_name),
            ("operatingSystemVersion", os_ver),
            ("description", f"{'Laptop' if kind == 'Laptopy' else 'Komputer stacjonarny'} - {loc[1]}"),
            ("location", f"{loc[1]}, piętro {rnd.randint(0, 5)}"),
            ("managedBy", owner),
            ("lastLogonTimestamp", filetime(now - timedelta(days=days_ago, minutes=rnd.randint(0, 1440)))),
        ])
        computer_dns.append(dn)

    server_dns = []
    for name, desc in [("SRV-FILE01", "Serwer plików"), ("SRV-SQL01", "SQL Server"), ("SRV-APP01", "Serwer aplikacji"),
                       ("SRV-PRINT01", "Serwer wydruku")]:
        dn = f"CN={name},{servers}"
        w.entry(dn, [
            ("objectClass", ["top", "person", "organizationalPerson", "user", "computer"]),
            ("cn", name), ("sAMAccountName", f"{name}$"), ("dNSHostName", f"{name.lower()}.{args.domain}"),
            ("userAccountControl", UAC_WORKSTATION), ("operatingSystem", "Windows Server 2025 Standard"),
            ("operatingSystemVersion", "10.0 (26100)"), ("description", desc),
            ("lastLogonTimestamp", filetime(now - timedelta(days=rnd.randint(0, 5)))),
        ])
        server_dns.append(dn)

    # Contacts.
    for i in range(12):
        first = rnd.choice(FIRST_NAMES_F + FIRST_NAMES_M)
        last = rnd.choice(LAST_NAMES)[0]
        company = rnd.choice(["Dostawca Sp. z o.o.", "Partner S.A.", "Kancelaria Prawna", "Biuro Rachunkowe"])
        display = f"{first} {last} ({company})"
        sam_like = strip_diacritics(f"{first}.{last}").lower()
        w.entry(f"CN={escape_rdn_value(display)},{contacts_ou}", [
            ("objectClass", ["top", "person", "organizationalPerson", "contact"]),
            ("cn", display), ("givenName", first), ("sn", last), ("displayName", display),
            ("company", company), ("mail", f"{sam_like}@example.com"),
            ("telephoneNumber", f"+48 {rnd.randint(500, 799)} {rnd.randint(100, 999)} {rnd.randint(100, 999)}"),
        ])

    # Groups (created after members exist; members added in the same add operation).
    def group(name: str, parent: str, group_type: int, members: list[str], description: str = "",
              mail: str | None = None, managed_by: str | None = None) -> str:
        dn = f"CN={escape_rdn_value(name)},{parent}"
        w.entry(dn, [
            ("objectClass", ["top", "group"]), ("cn", name), ("sAMAccountName", strip_diacritics(name)[:256]),
            ("groupType", group_type), ("description", description), ("mail", mail),
            ("managedBy", managed_by), ("member", members),
        ])
        return dn

    dept_groups = []
    for dept in DEPARTMENTS:
        dept_groups.append(group(f"GG_{dept[2]}", sec_groups, GROUP_TYPE_GLOBAL_SECURITY, users_by_dept[dept[0]],
                                 f"Pracownicy działu {dept[1]}", managed_by=managers[dept[0]]))
    group("GG_Wszyscy_Pracownicy", sec_groups, GROUP_TYPE_GLOBAL_SECURITY, dept_groups,
          "Wszyscy pracownicy (zagnieżdżone grupy działów)")
    group("GG_Helpdesk", sec_groups, GROUP_TYPE_GLOBAL_SECURITY, [helpdesk], "Operatorzy helpdesk - delegacja na OU=Pracownicy")
    group("DL_Udzial_Finanse_RW", sec_groups, GROUP_TYPE_DOMAIN_LOCAL_SECURITY,
          [dept_groups[1], dept_groups[6]], "Udział \\\\srv-file01\\finanse - zapis")
    group("UG_VPN_Uzytkownicy", sec_groups, GROUP_TYPE_UNIVERSAL_SECURITY,
          rnd.sample(all_user_dns, min(60, len(all_user_dns))), "Dostęp VPN")
    group("GG_Pusta_Grupa", sec_groups, GROUP_TYPE_GLOBAL_SECURITY, [], "Pusta grupa - do raportu pustych grup")
    group("Wszyscy", dist_groups, GROUP_TYPE_UNIVERSAL_DISTRIBUTION, all_user_dns[: len(all_user_dns)],
          "Lista dystrybucyjna - wszyscy pracownicy", mail=f"wszyscy@{args.domain}")
    for loc in LOCATIONS:
        members = [dn for dn in all_user_dns if rnd.random() < 0.2]
        group(f"Biuro {loc[1]}", dist_groups, GROUP_TYPE_GLOBAL_DISTRIBUTION, members,
              f"Lista dystrybucyjna biura {loc[1]}", mail=f"biuro.{strip_diacritics(loc[1]).lower()}@{args.domain}")
    group("GG_Komputery_Warszawa", sec_groups, GROUP_TYPE_GLOBAL_SECURITY,
          [dn for dn in computer_dns if ",OU=Warszawa," in dn][:40], "Komputery w Warszawie (GPO filtering)")
    if bulk_dns:
        group("GG_Masowe", sec_groups, GROUP_TYPE_GLOBAL_SECURITY, bulk_dns,
              "Duża grupa - test ranged retrieval (member;range=...)")

    print(f"# entries: {w.count}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
