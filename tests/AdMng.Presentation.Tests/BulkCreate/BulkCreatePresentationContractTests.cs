using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Csv;
using AdMng.Core.Model;
using AdMng.Core.Templates;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The F3 contract commit (docs/dev/phase3-bulk-create.md 2.11, 2.12) on the Presentation side: the records of
/// <c>AdMng.Presentation.BulkCreate</c> and the F3 settings types print no value, the batch editor's create mode opens the
/// complete wizard host (steps, kind, container, title, input, readiness) while the other modes are unchanged, the entry
/// point is wired but not offered until wave 2, the settings normalize, and <c>IssueText</c> renders the F3 codes.
/// </summary>
public sealed class BulkCreatePresentationContractTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static TheoryData<string> RecordNames()
    {
        var data = new TheoryData<string>();
        foreach (var type in BulkCreateRecordSamples.Records(typeof(BulkCreateViewModel).Assembly, "AdMng.Presentation.BulkCreate"))
        {
            data.Add(type.FullName!);
        }

        data.Add(typeof(BulkCreateRequest).FullName!);
        data.Add(typeof(BulkCreateRuleSetting).FullName!);
        data.Add(typeof(BulkCreateHeaderMapping).FullName!);
        return data;
    }

    [Theory]
    [MemberData(nameof(RecordNames))]
    public void EveryRecord_DeclaresAValueFreeToString(string typeName)
    {
        var type = typeof(BulkCreateViewModel).Assembly.GetType(typeName, throwOnError: true)!;

        Assert.True(BulkCreateRecordSamples.DeclaresOwnToString(type), $"{type.Name} uses the compiler-generated ToString (it prints every member).");
        Assert.Equal([], BulkCreateRecordSamples.LeakedValues(type));
    }

    [Fact]
    public void ViewModels_PrintNoValueAndNoPassword()
    {
        var account = new CreatedAccountViewModel(3, BulkCreateRecordSamples.Sentinels[5])
        {
            Name = BulkCreateRecordSamples.Sentinels[0],
            Login = BulkCreateRecordSamples.Sentinels[6],
            Mail = BulkCreateRecordSamples.Sentinels[1],
        };
        var profile = new BulkCreateProfile { Name = BulkCreateRecordSamples.Sentinels[4], UpnSuffix = "contoso.com" };

        Assert.Equal([], BulkCreateRecordSamples.LeakedValues(account));
        Assert.Equal([], BulkCreateRecordSamples.LeakedValues(profile));
        Assert.Equal(CreatedAccountViewModel.Mask, account.PasswordText);
        account.IsPasswordVisible = true;
        Assert.Equal(BulkCreateRecordSamples.Sentinels[5], account.PasswordText);
        account.ClearPassword();
        Assert.False(account.HasPassword);
        Assert.Equal(string.Empty, account.PasswordText);
    }

    [Fact]
    public async Task CreateMode_OpensTheWizardHost()
    {
        var harness = await B2Harness.CreateAsync(Polish, bulkCreateSchema: true);
        var sample = harness.Builder.AddBulkCreateSample(harness.Company);
        using var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User, sample.NewEmployeesOu) });

        await editor.InitializeAsync();

        Assert.Equal(BatchSourceMode.Create, editor.Mode);
        Assert.True(editor.IsCreateMode);
        Assert.False(editor.IsObjectsMode);
        Assert.False(editor.IsCsvMode);
        Assert.Null(editor.CsvImport);
        Assert.Null(editor.Error);
        Assert.Null(editor.UndoNote);
        Assert.Empty(editor.CreatedAccounts);
        Assert.False(editor.PreviewCommand.CanExecute(null));
        var wizard = Assert.IsType<BulkCreateViewModel>(editor.BulkCreate);
        Assert.Equal(ObjectKind.User, wizard.ObjectKind);
        Assert.Equal(["Użytkownicy", "Kontakty", "Grupy", "Komputery"], wizard.Kinds.Select(k => k.Text));
        Assert.Equal(sample.NewEmployeesOu, wizard.ContainerDn);
        Assert.Equal("Nowi pracownicy", wizard.ContainerName);
        Assert.Equal("Zakładanie wielu kont – Nowi pracownicy", wizard.Title);
        Assert.Same(harness.Connector, wizard.Check.Connector);
        Assert.Same(harness.Engines.BulkCreate, wizard.Check.Planner);
    }

    [Fact]
    public async Task Host_WithoutRows_CannotGoOn_AndHasNoInput()
    {
        var harness = await B2Harness.CreateAsync(Polish);
        using var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User, harness.Company.EmployeesOu) });
        var wizard = editor.BulkCreate!;

        Assert.Equal(BulkCreateStep.Source, wizard.Step);
        Assert.True(wizard.IsSourceStep);
        Assert.False(wizard.CanGoNext);
        Assert.False(wizard.NextStepCommand.CanExecute(null));
        Assert.False(wizard.PreviousStepCommand.CanExecute(null));
        Assert.Null(wizard.BuildInput());
        Assert.False(wizard.IsReadyToPlan);
        Assert.Equal(0, wizard.IncludedRowCount);
        Assert.Same(InputTable.Empty, wizard.Source.Table);
        Assert.Equal(ObjectKind.User, wizard.Columns.Kind);
        Assert.Equal(ObjectKind.User, wizard.Rules.Kind);
        Assert.Equal(Enum.GetValues<ColumnTargetKind>().Length, wizard.Columns.Targets.Count);
        Assert.Equal(Enum.GetValues<BulkCheckFilter>().Length, wizard.Check.Filters.Count);
        Assert.Equal("Pomiń", wizard.Columns.Targets[0].Text);
        Assert.Equal("Generuj osobne hasło dla każdego konta", wizard.Rules.PasswordModes[0].Text);
    }

    [Fact]
    public async Task Host_PicksTheContainer_AndChangesTheKind()
    {
        var harness = await B2Harness.CreateAsync(Polish);
        using var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User, null) });
        var wizard = editor.BulkCreate!;
        var changes = 0;
        wizard.Changed += (_, _) => changes++;

        Assert.Null(wizard.ContainerDn);
        Assert.False(wizard.HasContainer);
        Assert.Equal("Nie wybrano kontenera", wizard.ContainerName);
        Assert.Equal("Zakładanie wielu kont", wizard.Title);

        harness.Dialogs.PickedContainers.Enqueue(harness.Company.AccountingOu);
        await wizard.PickContainerCommand.ExecuteAsync(null);

        Assert.Equal(harness.Company.AccountingOu, wizard.ContainerDn);
        Assert.Equal("Księgowość", wizard.ContainerName);
        Assert.True(changes > 0);

        wizard.Kind = wizard.Kinds.Single(k => k.Value == ObjectKind.Group);
        await B2Harness.WaitUntilAsync(() => wizard.Rules.Kind == ObjectKind.Group);

        Assert.Equal(ObjectKind.Group, wizard.Columns.Kind);
        Assert.Equal("Zakładanie wielu grup – Księgowość", wizard.Title);
        Assert.Empty(harness.Dialogs.Shown); // the rules were not changed: no question
    }

    [Fact]
    public async Task OtherModes_AreUnchanged()
    {
        var harness = await B2Harness.CreateAsync();
        using var objects = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski]));
        using var csv = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]));

        Assert.Equal(BatchSourceMode.Objects, objects.Mode);
        Assert.True(objects.IsObjectsMode);
        Assert.False(objects.IsCreateMode);
        Assert.Null(objects.BulkCreate);
        Assert.Equal(BatchSourceMode.Csv, csv.Mode);
        Assert.False(csv.IsObjectsMode);
        Assert.Null(csv.BulkCreate);
        Assert.NotNull(csv.CsvImport);
    }

    [Fact]
    public async Task Shell_RegistersNewBulkCreate_OfferedOnlyWhenAvailableAndConnected()
    {
        // Not pinned to the contract's false: B4 switches IsBulkCreateAvailable on (2.15) without editing this test.
        var harness = await B2Harness.CreateAsync();
        using var shell = harness.CreateShell();

        var descriptor = Assert.Single(shell.CommandDescriptors, d => d.Id == "new.bulkCreate");
        Assert.Equal(L.BulkCreate.Command, descriptor.TitleKey);
        Assert.Equal(L.CommandCategories.New, descriptor.CategoryKey);
        Assert.True(shell.IsConnected);
        Assert.Equal(Presentation.Shell.ShellViewModel.IsBulkCreateAvailable, descriptor.Command.CanExecute(null));

        await harness.Sessions.DisconnectAsync();

        Assert.False(shell.IsConnected);
        Assert.False(descriptor.Command.CanExecute(null));
    }

    [Fact]
    public async Task BatchEditor_InCreateMode_IgnoresTheRequestOperations()
    {
        var harness = await B2Harness.CreateAsync(bulkCreateSchema: true);
        using var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([])
        {
            Operations = [new SetAttributeOperation("department", "Księgowość")],
            OpenPreview = true,
            Create = new BulkCreateRequest(ObjectKind.User, null),
        });

        await editor.InitializeAsync();

        Assert.True(editor.IsCreateMode);
        Assert.Empty(editor.Operations);
        Assert.True(editor.IsEditStep);
    }

    [Fact]
    public void Engines_DefaultToTheCorePlanner()
    {
        var csv = new CsvCodec();
        var engines = new BatchEngines(new RecordingChangePlanner(), new CsvImportPlanner(TimeProvider.System), new RollbackPlanner(), new PowerShellExporter(), csv,
            new TemplateEngine(), new BatchReportExporter(csv));

        Assert.IsType<BulkCreatePlanner>(engines.BulkCreate);
    }

    [Fact]
    public void ClipboardReadTable_DefaultsToTheFormattedContent()
    {
        var clipboard = new FakeClipboard { Formatted = new ClipboardContent(null, null, null, ExcelSamples.PolishTsv) };

        Assert.Same(clipboard.Formatted, ((IClipboardService)clipboard).ReadTable());
    }

    [Fact]
    public void Settings_KeepNamesPatternsAndOptions_AndDropWhatBreaksTheRules()
    {
        var settings = new BulkCreateSettings
        {
            Profiles =
            [
                new BulkCreateProfile
                {
                    Id = "p1",
                    Name = "Konta numerowane (kd201…)",
                    Rules =
                    [
                        new BulkCreateRuleSetting("sAMAccountName", "kd{$nr:201}", false),
                        new BulkCreateRuleSetting("unicodePwd", "x", false),
                        new BulkCreateRuleSetting(" ", "y", false),
                        new BulkCreateRuleSetting("msExchHideFromAddressLists", "TRUE", false),
                    ],
                    InitialLength = 3,
                    UpnSuffix = " Contoso.COM. ",
                    HeaderMappings =
                    [
                        new BulkCreateHeaderMapping("telefonkomorkowy", ColumnTargetKind.Attribute, "mobile", NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("#C", ColumnTargetKind.PatternOnly, null, NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("jan.kowalski@contoso.com", ColumnTargetKind.Attribute, "mail", NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("48600100200", ColumnTargetKind.Attribute, "mobile", NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("haslostartowe", ColumnTargetKind.Ignore, null, NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("pin", ColumnTargetKind.Ignore, null, NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("dzial", ColumnTargetKind.Attribute, "proxyAddresses", NameOrder.GivenNameFirst, TextCase.AsIs),
                    ],
                },
                new BulkCreateProfile { Id = "p1", Name = "Kopia" },
                new BulkCreateProfile { Name = "  " },
                new BulkCreateProfile { Name = "Jednostki", Kind = ObjectKind.OrganizationalUnit },
                .. Enumerable.Range(0, 60).Select(i => new BulkCreateProfile { Name = $"Profil {i}" }),
            ],
            LastProfileIds = new Dictionary<string, string> { ["User"] = "p1", ["OrganizationalUnit"] = "x", ["Nonsense"] = "y" },
        };

        settings.Normalize();

        Assert.Equal(BulkCreateSettings.MaxProfiles, settings.Profiles.Count);
        var first = settings.Profiles[0];
        Assert.Equal(["sAMAccountName"], first.Rules.Select(r => r.Attribute));
        Assert.Equal(12, first.InitialLength);
        Assert.Equal("contoso.com", first.UpnSuffix);
        Assert.Equal(["telefonkomorkowy", "#C"], first.HeaderMappings.Select(m => m.Header));
        Assert.NotEqual("p1", settings.Profiles[1].Id);
        Assert.Equal(settings.Profiles.Count, settings.Profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(settings.Profiles, p => p.Kind == ObjectKind.OrganizationalUnit);
        Assert.Equal(new Dictionary<string, string> { ["User"] = "p1" }, settings.LastProfileIds);

        var old = new AppSettings { BulkCreate = null! };
        old.Normalize();
        Assert.NotNull(old.BulkCreate);
    }

    [Fact]
    public void SerializedProfiles_HoldNoPasswordWordAndNoSheetValue()
    {
        var settings = new AppSettings();
        settings.BulkCreate.Profiles.Add(new BulkCreateProfile
        {
            Name = "Standardowy (kopia)",
            Rules = [new BulkCreateRuleSetting("sAMAccountName", "{givenName|sam|lower}.{sn|sam|lower}", false)],
            InitialMode = InitialPasswordMode.SameForAll,
            GroupDns = ["CN=GG_Nowi,OU=Grupy,OU=Firma,DC=@"],
            HeaderMappings = [new BulkCreateHeaderMapping("telefonkomorkowy", ColumnTargetKind.Attribute, "mobile", NameOrder.GivenNameFirst, TextCase.AsIs)],
        });

        var json = JsonSettingsStore.Serialize(settings);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwd", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BulkCreateRecordSamples.Sentinels, s => json.Contains(s, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("telefonkomorkowy", json, StringComparison.Ordinal);
        Assert.Contains("sameForAll", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("telefonkomorkowy", true)]
    [InlineData("#C", true)]
    [InlineData("#AB", true)]
    [InlineData("#c", false)]
    [InlineData("TelefonKomorkowy", false)]
    [InlineData("adres e-mail", false)]
    [InlineData("jan.kowalski@contoso.com", false)]
    [InlineData("600100200", false)]
    [InlineData("nrpokoju12", true)]
    [InlineData("nowehaslo", false)]
    [InlineData("temppassword", false)]
    [InlineData("kodpin", false)]
    [InlineData("", false)]
    public void HeaderRule(string header, bool storable) => Assert.Equal(storable, BulkCreateSettings.IsStorableHeader(header));

    [Fact]
    public void IssueText_GivesTheAttributeAndTheArguments_ToTheF3Codes()
    {
        var localizer = new ResxLocalizer(Polish);
        var schema = FakeSchema.Create();

        var duplicate = localizer.IssueText(new ValidationIssue(IssueSeverity.Error, BulkCreateIssueCodes.DuplicateInDirectory, "taken", "sAMAccountName")
        {
            Arguments = ["Jan Kowalski"],
        }, schema);
        var row = localizer.IssueText(new ValidationIssue(IssueSeverity.Error, BulkCreateIssueCodes.DuplicateRow, "row", "mail") { Arguments = ["12"] }, schema);
        var ambiguous = localizer.IssueText(new ValidationIssue(IssueSeverity.Warning, BulkCreateIssueCodes.ManagerAmbiguous, "two") { Arguments = ["2"] });

        Assert.Equal($"{localizer.AttributeName("sAMAccountName", schema)}: ta wartość jest już używana przez „Jan Kowalski”.", duplicate);
        Assert.Equal("Adres e-mail: ta sama wartość jest w wierszu 12.", row);
        Assert.Equal("Niejednoznaczny menedżer – pasujące konta: 2; wybierz jedno w komórce.", ambiguous);
    }
}
