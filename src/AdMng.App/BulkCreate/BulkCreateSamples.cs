using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;

namespace AdMng.App.BulkCreate;

/// <summary>
/// Synthetic data of the bulk creation wizard for the smoke test (<c>AdMng.exe --smoke-test</c>), the UI tests and screenshot
/// 43 (docs/dev/phase3-bulk-create.md 2.7): the operator's sheet as Excel copies it, and a hand-built check computation and
/// change plan of the same rows – an OK, a warning (an ambiguous manager), an error (a login already taken) and a possible
/// existing account – rendered without a planner, a session or a domain controller. Never written anywhere; no passwords.
/// </summary>
internal static class BulkCreateSamples
{
    public const string DomainDn = "DC=contoso,DC=test";

    /// <summary>The target container <em>Nowi pracownicy</em>.</summary>
    public const string ContainerDn = "OU=Nowi pracownicy,OU=Pracownicy,OU=Firma," + DomainDn;

    public const string GroupDn = "CN=GG_Nowi,OU=Grupy,OU=Firma," + DomainDn;

    /// <summary>The DC the sample computation was "read" from.</summary>
    public const string Server = "dc01.contoso.test";

    public static readonly string[] Headers = ["Telefon komórkowy", "Imię", "Nazwisko", "Dział", "E-mail", "Przełożony"];

    public static readonly string[][] Rows =
    [
        ["+48 600 100 201", "Łukasz", "Wójcik", "Księgowość", "lukasz.wojcik@firma.pl", ""],
        ["+48 600 100 202", "Zofia", "Kamińska", "Kadry", "zofia.kaminska@firma.pl", "Piotr Zieliński"],
        ["+48 600 100 203", "Michał", "Lewandowski", "IT", "michal.lewandowski@firma.pl", ""],
        ["+48 600 100 204", "Małgorzata", "Dąbrowska", "Sprzedaż", "malgorzata.dabrowska@firma.pl", ""],
        ["+48 600 100 205", "Ewa", "Wiśniewska", "Księgowość", "ewa.wisniewska@firma.pl", ""],
    ];

    /// <summary>The sheet as Excel puts it on the clipboard (Unicode text with tabs and CRLF).</summary>
    public static string Tsv => string.Join("\r\n", new[] { Headers }.Concat(Rows).Select(r => string.Join('\t', r))) + "\r\n";

    public static InputTable Table()
    {
        var columns = Headers.Select((h, i) => new TableColumn(i, TableTextReader.ColumnLetter(i), h)).ToList();
        return new InputTable(columns, [.. Rows.Select((r, i) => new TableRow(i + 1, r))], HasHeader: true, TableSource.ClipboardText, []);
    }

    /// <summary>The input of the sample: the automatic mapping and the <em>Konta numerowane</em> profile.</summary>
    /// <param name="containerDn">The target container (default <see cref="ContainerDn"/>; UI tests pass the fake's).</param>
    /// <param name="groupDn">The option group (default <see cref="GroupDn"/>).</param>
    public static BulkCreateInput Input(string containerDn = ContainerDn, string groupDn = GroupDn) => new()
    {
        ContainerDn = containerDn,
        Table = Table(),
        Mappings =
        [
            new(0, ColumnTargetKind.Attribute, Attr.Mobile),
            new(1, ColumnTargetKind.Attribute, Attr.GivenName),
            new(2, ColumnTargetKind.Attribute, Attr.Sn),
            new(3, ColumnTargetKind.Attribute, Attr.Department),
            new(4, ColumnTargetKind.Attribute, Attr.Mail),
            new(5, ColumnTargetKind.Manager),
        ],
        Rules = new BulkCreateRules
        {
            Kind = ObjectKind.User,
            Attributes =
            [
                new(Attr.SamAccountName, "kd{$nr:201}"),
                new(Attr.UserPrincipalName, "{sAMAccountName}"),
                new(Attr.Mail, "{sAMAccountName}@{$mailDomain}"),
                new(Attr.Cn, "{givenName} {sn}"),
                new(Attr.DisplayName, "{givenName} {sn}"),
            ],
            UpnSuffix = "contoso.com",
            MailDomain = "contoso.com",
            CounterMode = CounterMode.AfterHighest,
        },
        Options = new BulkAccountOptions { GroupDns = [groupDn] },
        UpnSuffixes = ["contoso.com", "firma.pl"],
    };

    /// <summary>The check grid of the sample (kd208 … kd212): OK, a warning, an error, OK and a possible existing account.</summary>
    public static BulkCreateComputation Computation(string containerDn = ContainerDn, string groupDn = GroupDn, int extraAttributes = 0)
    {
        var input = Input(containerDn, groupDn);
        var extra = Enumerable.Range(1, extraAttributes).Select(i => "contosoExtra" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        BulkCreateRow Row(int number, BulkRowStatus status, string login, IReadOnlyList<ValidationIssue>? issues = null, string? manager = null, IReadOnlyList<string>? candidates = null)
        {
            var cells = Rows[number - 1];
            var name = $"{cells[1]} {cells[2]}";
            return new BulkCreateRow
            {
                Number = number,
                Status = status,
                Values =
                [
                    new(Attr.Cn, [name], BulkValueSource.Pattern),
                    new(Attr.SamAccountName, [login], BulkValueSource.Pattern) { Adjusted = status == BulkRowStatus.Error },
                    new(Attr.UserPrincipalName, [login + "@contoso.com"], BulkValueSource.Pattern),
                    new(Attr.Mail, [cells[4]], BulkValueSource.Column),
                    new(Attr.DisplayName, [name], BulkValueSource.Pattern),
                    new(Attr.GivenName, [cells[1]], BulkValueSource.Column),
                    new(Attr.Sn, [cells[2]], BulkValueSource.Column),
                    new(Attr.Department, [cells[3]], BulkValueSource.Column),
                    new(Attr.Mobile, [cells[0]], BulkValueSource.Column),
                    .. extra.Select(a => new BulkValue(a, [a + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)], BulkValueSource.Fixed)),
                ],
                DistinguishedName = $"CN={name},{containerDn}",
                CounterNumber = int.Parse(login[2..], System.Globalization.CultureInfo.InvariantCulture),
                ManagerDn = manager,
                ManagerCandidates = candidates ?? [],
                GroupDns = [groupDn],
                Issues = issues ?? [],
            };
        }

        string[] piotr = ["CN=Piotr Zieliński,OU=IT,OU=Pracownicy,OU=Firma," + DomainDn, "CN=Piotr Zieliński,OU=Sprzedaż,OU=Pracownicy,OU=Firma," + DomainDn];
        return new BulkCreateComputation
        {
            Input = input,
            Rows =
            [
                Row(1, BulkRowStatus.Ok, "kd208"),
                Row(2, BulkRowStatus.Warning, "kd209",
                    [new ValidationIssue(IssueSeverity.Warning, BulkCreateIssueCodes.ManagerAmbiguous, "Two accounts match the manager.") { Arguments = ["2"] }],
                    candidates: piotr),
                Row(3, BulkRowStatus.Error, "kd210",
                    [new ValidationIssue(IssueSeverity.Error, BulkCreateIssueCodes.DuplicateInDirectory, "The value is taken.", Attr.SamAccountName) { Arguments = ["Michał Lewandowski"] }]),
                Row(4, BulkRowStatus.Ok, "kd211"),
                Row(5, BulkRowStatus.Warning, "kd212",
                    [new ValidationIssue(IssueSeverity.Warning, BulkCreateIssueCodes.PossibleExistingAccount, "The same person may exist.", Attr.Cn) { Arguments = ["Ewa Wiśniewska"] }]),
            ],
            Attributes = [Attr.Cn, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.DisplayName, Attr.GivenName, Attr.Sn, Attr.Department, Attr.Mobile, .. extra],
            Server = Server,
            Scope = UniquenessScope.ForestSingleDomain,
            IsPdcEmulator = true,
            ForestDomains = ["contoso.test"],
        };
    }

    /// <summary>
    /// The change plan of <see cref="Computation"/>: a Create (disabled), the enable and must-change step and the group per OK
    /// and warning row, the error row's Create not executable. No password steps (the sample hands out no password).
    /// </summary>
    public static ChangePlan Plan()
    {
        var computation = Computation();
        var objects = computation.Rows.Select(row => new ObjectPlan
        {
            DistinguishedName = row.DistinguishedName!,
            DisplayName = row.Value(Attr.Cn)!,
            Kind = ObjectKind.User,
            ObjectClasses = CreationNames.ClassChainOf(ObjectKind.User),
            SourceRow = row.Number,
            Changes = row.Status == BulkRowStatus.Error
                ? [Create(row)]
                :
                [
                    Create(row),
                    new PlannedChange
                    {
                        Action = ChangeAction.Modify,
                        Attributes = [new AttributeChange(Attr.PwdLastSet, AttributeChangeKind.Replace, [], ["0"])],
                    },
                    new PlannedChange { Action = ChangeAction.AddMember, TargetDn = GroupDn },
                ],
            Issues = row.Issues,
        }).ToList();
        return new ChangePlan
        {
            Objects = objects,
            Title = "Utworzenie kont (4) w jednostce „Nowi pracownicy”",
            CreatedAt = DateTimeOffset.UtcNow,
            Server = Server,
        };

        static PlannedChange Create(BulkCreateRow row) => new()
        {
            Action = ChangeAction.Create,
            Attributes =
            [
                new(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["user"]),
                .. row.Values.Where(v => v.Attribute != Attr.Cn).Select(v => new AttributeChange(v.Attribute, AttributeChangeKind.Replace, [], v.Values)),
                new(Attr.UserAccountControl, AttributeChangeKind.Replace, [], [BulkCreatePlanner.CreatedUserAccountControl.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            ],
        };
    }
}
