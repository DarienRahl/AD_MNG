using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

public class ChangePlannerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;

    public ChangePlannerTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private ChangePlanner Planner => new(new TemplateEngine(), new PasswordGenerator(), _time);

    private Task<ChangePlan> PlanAsync(IReadOnlyList<string> targets, params BatchOperation[] operations) =>
        PlanAsync(targets, null, operations);

    private Task<ChangePlan> PlanAsync(IReadOnlyList<string> targets, PlanningOptions? options, params BatchOperation[] operations) =>
        Planner.PlanAsync(Session, new BatchRequest(targets, operations) { Title = "Test" }, options, null, Ct);

    private async Task<ObjectPlan> PlanOneAsync(string target, params BatchOperation[] operations) =>
        Assert.Single((await PlanAsync([target], operations)).Objects);

    private static PlannedChange Step(ObjectPlan plan, ChangeAction action) => Assert.Single(plan.Changes, c => c.Action == action);

    private static AttributeChange Change(ObjectPlan plan, string attribute) =>
        Assert.Single(Step(plan, ChangeAction.Modify).Attributes, a => a.Attribute == attribute);

    private static IEnumerable<ValidationIssue> AllIssues(ObjectPlan plan) => plan.Issues.Concat(plan.Changes.SelectMany(c => c.Issues));

    private static ValidationIssue Issue(ObjectPlan plan, string code) => Assert.Single(AllIssues(plan), i => i.Code == code);

    private static EffectivePermissions Permissions(params string[] writable) => new()
    {
        IsKnown = true,
        WritableAttributes = new HashSet<string>(writable, StringComparer.OrdinalIgnoreCase),
        CreatableChildClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };

    // ------------------------------------------------------------------ attribute values

    [Fact]
    public async Task SetAttribute_ComparesWithLiveValuesAndDropsNoOps()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.MalgorzataWojcik], new SetAttributeOperation(Attr.Department, "Księgowość"));

        var jan = plan.Objects[0];
        var malgorzata = plan.Objects[1];
        Assert.Equal("Test", plan.Title);
        Assert.Equal("dc01.contoso.test", plan.Server);
        Assert.Equal(_time.GetUtcNow(), plan.CreatedAt);
        Assert.Equal(["IT"], Change(jan, Attr.Department).OldValues);
        Assert.Equal(["Księgowość"], Change(jan, Attr.Department).NewValues);
        Assert.Equal("Jan Kowalski", jan.DisplayName);
        Assert.Equal(ObjectKind.User, jan.Kind);
        Assert.Contains("user", jan.ObjectClasses);
        Assert.True(malgorzata.IsNoOp);
        Assert.Equal(IssueCodes.NoChange, Assert.Single(malgorzata.Issues).Code);
        Assert.Equal(1, plan.ExecutableCount);
        Assert.Equal(1, plan.NoOpCount);
        Assert.Equal(0, plan.ErrorCount);
    }

    [Fact]
    public async Task Templates_UseObjectValuesCounterAndParent()
    {
        var plan = await PlanAsync(
            [_company.LukaszWisniewski, _company.ZanetaSzymanska],
            new SetAttributeOperation(Attr.Mail, "{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}@firma.pl"),
            new SetAttributeOperation(Attr.Description, "Pracownik {$counter:000} z {$parentName}"));

        Assert.Equal(["lukasz.wisniewski@firma.pl"], Change(plan.Objects[0], Attr.Mail).NewValues);
        Assert.Equal(["Pracownik 001 z IT"], Change(plan.Objects[0], Attr.Description).NewValues);
        Assert.Equal(["zaneta.szymanska@firma.pl"], Change(plan.Objects[1], Attr.Mail).NewValues);
        Assert.Equal(["Pracownik 002 z Sprzedaż"], Change(plan.Objects[1], Attr.Description).NewValues);
        var read = Assert.Single(Session.Operations, o => o.Kind == FakeOperationKind.Get && o.DistinguishedName == _company.ZanetaSzymanska);
        Assert.Contains(Attr.GivenName, read.Attributes);
        Assert.Contains(Attr.Sn, read.Attributes);
        Assert.Contains(Attr.Mail, read.Attributes);
        Assert.Contains(Attr.Description, read.Attributes);
        Assert.Contains(Attr.UserAccountControl, read.Attributes);
    }

    [Fact]
    public async Task Templates_SeeValuesProducedByEarlierOperations()
    {
        var jan = await PlanOneAsync(_company.JanKowalski,
            new SetAttributeOperation(Attr.GivenName, "Janusz"),
            new SetAttributeOperation(Attr.DisplayName, "{givenName} {sn}"));

        Assert.Equal(["Janusz Kowalski"], Change(jan, Attr.DisplayName).NewValues);
        Assert.Equal(["Jan Kowalski"], Change(jan, Attr.DisplayName).OldValues);
    }

    [Fact]
    public async Task CaseOnlyChange_IsAChange()
    {
        var jan = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.Department, "it"));

        Assert.Equal(["it"], Change(jan, Attr.Department).NewValues);
    }

    [Fact]
    public async Task EmptyTemplate_LeavesValueUnlessEmptyClears()
    {
        var keep = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.Info, "{missing}"));
        var clear = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.Department, "{missing}", EmptyClears: true));

        Assert.True(keep.IsNoOp);
        Assert.Contains(keep.Issues, i => i is { Code: IssueCodes.NoChange, Attribute: Attr.Info, Severity: IssueSeverity.Info });
        var change = Change(clear, Attr.Department);
        Assert.Equal(AttributeChangeKind.Clear, change.Kind);
        Assert.Equal(["IT"], change.OldValues);
        Assert.Empty(change.ResultValues);
    }

    [Fact]
    public async Task AddAndRemoveValues_AreComputedAgainstCurrentValues()
    {
        Session.SetValues(_company.JanKowalski, Attr.ProxyAddresses, "SMTP:jan.kowalski@contoso.test", "smtp:jk@contoso.test");

        var add = await PlanOneAsync(_company.JanKowalski, new AddValuesOperation(Attr.ProxyAddresses, ["smtp:JK@contoso.test", "smtp:{givenName|lower}@firma.pl", ""]));
        var remove = await PlanOneAsync(_company.JanKowalski, new RemoveValuesOperation(Attr.ProxyAddresses, ["SMTP:JK@CONTOSO.TEST", "smtp:nie@ma"]));
        var both = await PlanOneAsync(_company.JanKowalski,
            new AddValuesOperation(Attr.ProxyAddresses, ["smtp:nowy@firma.pl"]),
            new RemoveValuesOperation(Attr.ProxyAddresses, ["smtp:jk@contoso.test"]));
        var noop = await PlanOneAsync(_company.JanKowalski, new RemoveValuesOperation(Attr.ProxyAddresses, ["smtp:nie@ma"]));

        Assert.Equal(new[] { "smtp:jan@firma.pl" }, Change(add, Attr.ProxyAddresses).NewValues);
        Assert.Equal(AttributeChangeKind.AddValues, Change(add, Attr.ProxyAddresses).Kind);
        Assert.Equal(new[] { "smtp:jk@contoso.test" }, Change(remove, Attr.ProxyAddresses).NewValues);
        Assert.Equal(AttributeChangeKind.RemoveValues, Change(remove, Attr.ProxyAddresses).Kind);
        Assert.Equal(AttributeChangeKind.Replace, Change(both, Attr.ProxyAddresses).Kind);
        Assert.Equal(new[] { "SMTP:jan.kowalski@contoso.test", "smtp:nowy@firma.pl" }, Change(both, Attr.ProxyAddresses).NewValues);
        Assert.True(noop.IsNoOp);
    }

    [Fact]
    public async Task FindReplace_SupportsPlainTextAndRegex()
    {
        var plain = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new FindReplaceOperation(Attr.City, "warszawa", "Warsaw"));
        var matchCase = await PlanOneAsync(_company.JanKowalski, new FindReplaceOperation(Attr.City, "warszawa", "Warsaw", MatchCase: true));
        var regex = await PlanOneAsync(_company.JanKowalski, new FindReplaceOperation(Attr.Mail, @"@contoso\.test$", "@firma.pl", UseRegex: true));
        var groups = await PlanOneAsync(_company.JanKowalski, new FindReplaceOperation(Attr.Mail, @"^(\w+)\.(\w+)@", "$2.$1@", UseRegex: true));
        var invalid = await PlanOneAsync(_company.JanKowalski, new FindReplaceOperation(Attr.Mail, "(", "x", UseRegex: true));

        Assert.Equal(["Warsaw"], Change(plain.Objects[0], Attr.City).NewValues);
        Assert.True(plain.Objects[1].IsNoOp);
        Assert.True(matchCase.IsNoOp);
        Assert.Equal(["jan.kowalski@firma.pl"], Change(regex, Attr.Mail).NewValues);
        Assert.Equal(["kowalski.jan@contoso.test"], Change(groups, Attr.Mail).NewValues);
        Assert.Equal(IssueSeverity.Error, Issue(invalid, IssueCodes.InvalidValue).Severity);
    }

    // ------------------------------------------------------------------ account operations

    [Fact]
    public async Task EnableDisable_UsesCurrentUserAccountControl()
    {
        var contact = _builder.AddContact("Anna", "Zewnętrzna", _company.SalesOu);

        var plan = await PlanAsync([_company.JanKowalski, _company.ZbigniewKazmierczak, contact], new SetAccountEnabledOperation(false));
        var enable = await PlanOneAsync(_company.ZbigniewKazmierczak, new SetAccountEnabledOperation(true));

        var change = Change(plan.Objects[0], Attr.UserAccountControl);
        Assert.Equal(["512"], change.OldValues);
        Assert.Equal(["514"], change.NewValues);
        Assert.True(plan.Objects[1].IsNoOp);
        Assert.True(plan.Objects[2].IsNoOp);
        Assert.Equal(IssueSeverity.Warning, Issue(plan.Objects[2], IssueCodes.NotApplicable).Severity);
        Assert.False(plan.Objects[2].HasErrors);
        Assert.Equal(["512"], Change(enable, Attr.UserAccountControl).NewValues);
    }

    [Fact]
    public async Task AccountFlags_AreValidated()
    {
        var neverExpires = await PlanOneAsync(_company.JanKowalski, new SetAccountFlagOperation(UserAccountControl.DontExpirePassword, true));
        var disable = await PlanOneAsync(_company.JanKowalski, new SetAccountFlagOperation(UserAccountControl.AccountDisable, true));
        var unlock = await PlanOneAsync(_company.ZanetaSzymanska, new SetAccountFlagOperation(UserAccountControl.Lockout, false));
        var typeFlag = await PlanOneAsync(_company.JanKowalski, new SetAccountFlagOperation(UserAccountControl.WorkstationTrustAccount, true));
        var both = await PlanOneAsync(_company.JanKowalski,
            new SetAccountFlagOperation(UserAccountControl.DontExpirePassword, true),
            new SetAccountEnabledOperation(false));

        Assert.Equal(["66048"], Change(neverExpires, Attr.UserAccountControl).NewValues);
        Assert.Equal(["514"], Change(disable, Attr.UserAccountControl).NewValues);
        Assert.Equal(["0"], Change(unlock, Attr.LockoutTime).NewValues);
        Assert.True(typeFlag.HasErrors);
        Assert.Equal(IssueCodes.InvalidValue, Assert.Single(AllIssues(typeFlag)).Code);
        Assert.Equal([(66048 | 2).ToString(CultureInfo.InvariantCulture)], Change(both, Attr.UserAccountControl).NewValues);
    }

    [Fact]
    public async Task Unlock_OnlyTouchesLockedAccounts()
    {
        var plan = await PlanAsync([_company.ZanetaSzymanska, _company.JanKowalski], new UnlockAccountOperation());

        var change = Change(plan.Objects[0], Attr.LockoutTime);
        Assert.NotEqual("0", Assert.Single(change.OldValues));
        Assert.Equal(["0"], change.NewValues);
        Assert.True(plan.Objects[1].IsNoOp);
    }

    [Fact]
    public async Task RequirePasswordChange_SetsPwdLastSetAndWarnsAboutNeverExpires()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.GrazynaDabrowska], new RequirePasswordChangeOperation(true));
        Session.SetValues(_company.JanKowalski, Attr.PwdLastSet, "0");
        var undo = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new RequirePasswordChangeOperation(false));

        Assert.Equal(["0"], Change(plan.Objects[0], Attr.PwdLastSet).NewValues);
        Assert.DoesNotContain(AllIssues(plan.Objects[0]), i => i.Code == IssueCodes.PasswordNeverExpiresConflict);
        Assert.Equal(["0"], Change(plan.Objects[1], Attr.PwdLastSet).NewValues);
        var warning = Issue(plan.Objects[1], IssueCodes.PasswordNeverExpiresConflict);
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Equal(Attr.PwdLastSet, warning.Attribute);
        Assert.Equal(["-1"], Change(undo.Objects[0], Attr.PwdLastSet).NewValues);
        Assert.True(undo.Objects[1].IsNoOp);
    }

    [Fact]
    public async Task AccountExpiration_WritesFileTimeAndTreatsZeroAsNever()
    {
        var expires = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero);

        var set = await PlanOneAsync(_company.JanKowalski, new SetAccountExpirationOperation(expires));
        var never = await PlanOneAsync(_company.JanKowalski, new SetAccountExpirationOperation(null));
        Session.SetValues(_company.LukaszWisniewski, Attr.AccountExpires, "0");
        var zero = await PlanOneAsync(_company.LukaszWisniewski, new SetAccountExpirationOperation(null));
        Session.SetValues(_company.PawelLewandowski, Attr.AccountExpires, AdConvert.ToFileTime(expires).ToString(CultureInfo.InvariantCulture));
        var clear = await PlanOneAsync(_company.PawelLewandowski, new SetAccountExpirationOperation(null));

        Assert.Equal([AdConvert.ToFileTime(expires).ToString(CultureInfo.InvariantCulture)], Change(set, Attr.AccountExpires).NewValues);
        Assert.True(never.IsNoOp);
        Assert.True(zero.IsNoOp);
        Assert.Equal(["9223372036854775807"], Change(clear, Attr.AccountExpires).NewValues);
    }

    [Fact]
    public async Task AccountExpiration_BeforeFileTimeEpochIsAnErrorNotACrash()
    {
        var plan = await PlanOneAsync(_company.JanKowalski, new SetAccountExpirationOperation(new DateTimeOffset(1500, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.True(plan.HasErrors);
        Assert.Equal(Attr.AccountExpires, Issue(plan, IssueCodes.InvalidValue).Attribute);
    }

    [Fact]
    public async Task RangedValues_AreReadCompletelyBeforePlanning()
    {
        // GG_Ksiegowosc has three members; the DC returns at most two values per read (AD: 1500).
        Session.MaxValueRange = 2;

        var remove = await PlanOneAsync(_company.GroupAccounting, new RemoveValuesOperation(Attr.Member, [_company.ZbigniewKazmierczak]));
        var clear = await PlanOneAsync(_company.GroupAccounting, new ClearAttributeOperation(Attr.Member));

        Assert.Equal([_company.ZbigniewKazmierczak], Change(remove, Attr.Member).NewValues);
        Assert.Equal([_company.MalgorzataWojcik, _company.GrazynaDabrowska, _company.ZbigniewKazmierczak], Change(clear, Attr.Member).OldValues);
        Assert.Contains(Session.Operations, o => o.Kind == FakeOperationKind.GetAllValues && o.Attributes.SequenceEqual([Attr.Member]));
    }

    [Fact]
    public async Task RangedMemberOf_DoesNotHideExistingMemberships()
    {
        // Jan is in GG_IT plus four more groups; a read returns four memberOf values (and all four objectClass values).
        var extra = Enumerable.Range(1, 4).Select(i => _builder.AddGroup($"GG_Projekt{i}", _company.GroupsOu, members: [_company.JanKowalski])).ToList();
        Session.MaxValueRange = 4;

        var plan = await PlanOneAsync(_company.JanKowalski, new AddToGroupsOperation([extra[^1]]));

        Assert.Equal(ObjectKind.User, plan.Kind);
        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.Changes);
        Assert.DoesNotContain(AllIssues(plan), i => i.Severity != IssueSeverity.Info);
    }

    [Fact]
    public async Task Templates_LastZeroIsEmptyNotACrash()
    {
        var plan = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.Info, "{sn|last:0}x"));

        Assert.Equal(["x"], Change(plan, Attr.Info).NewValues);
    }

    [Fact]
    public async Task ResetPassword_GeneratesUniquePasswordsAndOrdersSteps()
    {
        Session.SetValues(_company.PawelLewandowski, Attr.PwdLastSet, "0");

        var plan = await PlanAsync([_company.JanKowalski, _company.ZanetaSzymanska, _company.PawelLewandowski],
            new ResetPasswordOperation(PasswordMode.GenerateUnique));

        var passwords = plan.Objects.Select(o => o.Changes[0].Password!).ToList();
        Assert.All(plan.Objects, o => Assert.Equal([ChangeAction.ResetPassword, ChangeAction.Modify], o.Changes.Select(c => c.Action)));
        Assert.Equal(3, passwords.Distinct(StringComparer.Ordinal).Count());
        Assert.All(passwords, p => Assert.Equal(PasswordGenerator.DefaultLength, p.Length));
        Assert.DoesNotContain("jan", passwords[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kowalski", passwords[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["0"], Change(plan.Objects[0], Attr.PwdLastSet).NewValues);
        Assert.Equal(["0"], Change(plan.Objects[1], Attr.LockoutTime).NewValues);
        Assert.DoesNotContain(Step(plan.Objects[0], ChangeAction.Modify).Attributes, a => a.Attribute == Attr.LockoutTime);
        var forced = Change(plan.Objects[2], Attr.PwdLastSet);
        Assert.Equal(["0"], forced.OldValues);
        Assert.Equal(["0"], forced.NewValues);
        Assert.True(plan.ContainsPasswords);
        Assert.DoesNotContain(passwords[0], plan.Objects[0].Changes[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPassword_ValidatesModeAndTarget()
    {
        var noPassword = await PlanOneAsync(_company.JanKowalski, new ResetPasswordOperation(PasswordMode.Fixed));
        var fixedPassword = await PlanOneAsync(_company.JanKowalski, new ResetPasswordOperation(PasswordMode.Fixed, "Wi0sna!2026xyz", MustChangeAtNextLogon: false, UnlockAccount: false));
        var tooShort = await PlanOneAsync(_company.JanKowalski, new ResetPasswordOperation(PasswordMode.GenerateUnique, GeneratedLength: 8));
        var group = await PlanOneAsync(_company.GroupIt, new ResetPasswordOperation(PasswordMode.GenerateUnique));

        Assert.True(noPassword.HasErrors);
        Assert.Equal(IssueCodes.InvalidValue, Issue(noPassword, IssueCodes.InvalidValue).Code);
        Assert.Equal("Wi0sna!2026xyz", Assert.Single(fixedPassword.Changes).Password);
        Assert.True(tooShort.HasErrors);
        Assert.True(group.IsNoOp);
        Assert.Equal(IssueSeverity.Warning, Issue(group, IssueCodes.NotApplicable).Severity);
    }

    // ------------------------------------------------------------------ membership

    [Fact]
    public async Task AddToGroups_SkipsExistingAndPrimaryMemberships()
    {
        var plan = await PlanAsync([_company.JanKowalski], new AddToGroupsOperation([_company.GroupSales.ToUpperInvariant(), _company.GroupIt, _builder.DomainUsersDn]));

        var step = Assert.Single(plan.Objects[0].Changes);
        Assert.Equal(ChangeAction.AddMember, step.Action);
        Assert.Equal(_company.GroupSales, step.TargetDn);
        Assert.Empty(step.Issues);
        Assert.Contains(Session.Operations, o => o.Kind == FakeOperationKind.Get && o.DistinguishedName == _company.JanKowalski && o.Attributes.Contains(Attr.MemberOf));
    }

    [Fact]
    public async Task RemoveFromGroups_HandlesNonMembersAndPrimaryGroup()
    {
        var plan = await PlanOneAsync(_company.JanKowalski, new RemoveFromGroupsOperation([_company.GroupIt, _company.GroupSales, _builder.DomainUsersDn]));

        var step = Assert.Single(plan.Changes);
        Assert.Equal(ChangeAction.RemoveMember, step.Action);
        Assert.Equal(_company.GroupIt, step.TargetDn);
        Assert.Equal(IssueSeverity.Warning, Issue(plan, IssueCodes.NotApplicable).Severity);
    }

    [Fact]
    public async Task Membership_ReportsInvalidGroups()
    {
        var missing = await PlanOneAsync(_company.JanKowalski, new AddToGroupsOperation(["CN=Brak," + _company.GroupsOu]));
        var notGroup = await PlanOneAsync(_company.JanKowalski, new AddToGroupsOperation([_company.ItOu]));
        var self = await PlanOneAsync(_company.GroupIt, new AddToGroupsOperation([_company.GroupIt]));
        var badDn = await PlanOneAsync(_company.JanKowalski, new AddToGroupsOperation(["nie jest DN"]));
        var ou = await PlanOneAsync(_company.ItOu, new AddToGroupsOperation([_company.GroupIt]));

        Assert.Equal(ChangeAction.AddMember, Assert.Single(missing.Changes).Action);
        Assert.Equal(IssueSeverity.Error, Issue(missing, IssueCodes.NotFound).Severity);
        Assert.True(notGroup.HasErrors);
        Assert.True(self.HasErrors);
        Assert.True(badDn.HasErrors);
        Assert.True(ou.IsNoOp);
        Assert.Equal(IssueSeverity.Warning, Issue(ou, IssueCodes.NotApplicable).Severity);
    }

    [Fact]
    public async Task AddThenRemoveSameGroup_IsANoOp()
    {
        var plan = await PlanOneAsync(_company.JanKowalski,
            new AddToGroupsOperation([_company.GroupSales]),
            new RemoveFromGroupsOperation([_company.GroupSales]));

        Assert.True(plan.IsNoOp);
    }

    [Fact]
    public async Task Membership_ChecksWritePermissionOnTheGroup()
    {
        Session.SetPermissions(_company.GroupSales, Permissions(Attr.Description));

        var plan = await PlanOneAsync(_company.JanKowalski, new AddToGroupsOperation([_company.GroupSales]));

        Assert.Equal(IssueSeverity.Error, Issue(plan, IssueCodes.NotWritable).Severity);
    }

    // ------------------------------------------------------------------ rename and move

    [Fact]
    public async Task Rename_BuildsEscapedRdnAndOptionallySetsDisplayName()
    {
        var upper = await PlanOneAsync(_company.JanKowalski, new RenameOperation("{givenName} {sn|upper}", AlsoSetDisplayName: true));
        var comma = await PlanOneAsync(_company.JanKowalski, new RenameOperation("{sn}, {givenName}"));
        var same = await PlanOneAsync(_company.JanKowalski, new RenameOperation("{givenName} {sn}"));
        var ou = await PlanOneAsync(_company.ItOu, new RenameOperation("Informatyka"));

        Assert.Equal("CN=Jan KOWALSKI", Step(upper, ChangeAction.Rename).TargetDn);
        Assert.Equal(["Jan KOWALSKI"], Change(upper, Attr.DisplayName).NewValues);
        Assert.Equal([ChangeAction.Modify, ChangeAction.Rename], upper.Changes.Select(c => c.Action));
        Assert.Equal(@"CN=Kowalski\, Jan", Step(comma, ChangeAction.Rename).TargetDn);
        Assert.True(same.IsNoOp);
        Assert.Equal("OU=Informatyka", Step(ou, ChangeAction.Rename).TargetDn);
    }

    [Fact]
    public async Task Rename_ValidatesNameLengthCollisionsAndPermissions()
    {
        Session.SetValues(_company.JanKowalski, Attr.Description, new string('x', 70));
        var tooLong = await PlanOneAsync(_company.JanKowalski, new RenameOperation("{description}"));
        var collision = await PlanOneAsync(_company.LukaszWisniewski, new RenameOperation("Jan Kowalski"));
        var empty = await PlanOneAsync(_company.JanKowalski, new RenameOperation("{missing}"));
        Session.SetPermissions(_company.PawelLewandowski, Permissions(Attr.Description));
        var denied = await PlanOneAsync(_company.PawelLewandowski, new RenameOperation("Paweł Nowak"));

        Assert.Equal(IssueSeverity.Error, Issue(tooLong, IssueCodes.TooLong).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(collision, IssueCodes.Duplicate).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(empty, IssueCodes.InvalidValue).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(denied, IssueCodes.NotWritable).Severity);
    }

    [Fact]
    public async Task Move_SkipsObjectsAlreadyInTarget()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.PawelLewandowski], new MoveOperation(_company.SalesOu));

        Assert.Equal(_company.SalesOu, Step(plan.Objects[0], ChangeAction.Move).TargetDn);
        Assert.True(plan.Objects[1].IsNoOp);
        Assert.Equal(IssueSeverity.Info, Issue(plan.Objects[1], IssueCodes.TargetIsSameContainer).Severity);
        Assert.Equal(1, plan.ExecutableCount);
    }

    [Fact]
    public async Task Move_ValidatesTarget()
    {
        var missing = await PlanOneAsync(_company.JanKowalski, new MoveOperation("OU=Brak," + _builder.DomainDn));
        var invalid = await PlanOneAsync(_company.JanKowalski, new MoveOperation("bez sensu"));
        var intoItself = await PlanOneAsync(_company.EmployeesOu, new MoveOperation(_company.ItOu));
        Session.SetPermissions(_company.GroupsOu, new EffectivePermissions
        {
            IsKnown = true,
            CreatableChildClasses = new HashSet<string>(["group"], StringComparer.OrdinalIgnoreCase),
        });
        var denied = await PlanOneAsync(_company.JanKowalski, new MoveOperation(_company.GroupsOu));
        var otherGroup = _builder.AddGroup("GG_Tymczasowa", _company.SalesOu);
        var allowedGroup = await PlanOneAsync(otherGroup, new MoveOperation(_company.GroupsOu));

        Assert.Equal(IssueSeverity.Error, Issue(missing, IssueCodes.NotFound).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(invalid, IssueCodes.InvalidValue).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(intoItself, IssueCodes.InvalidValue).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(denied, IssueCodes.CannotCreateChild).Severity);
        Assert.Equal(_company.GroupsOu, Step(allowedGroup, ChangeAction.Move).TargetDn);
        Assert.False(allowedGroup.HasErrors);
    }

    [Fact]
    public async Task Move_DetectsNameCollisions()
    {
        var namesake = _builder.AddUser("Jan", "Kowalski", _company.AccountingOu,
            new Dictionary<string, object?> { [Attr.SamAccountName] = "jan.kowalski2" });
        _builder.AddUser("Paweł", "Lewandowski", _company.ItOu, new Dictionary<string, object?> { [Attr.SamAccountName] = "pawel.l2" });

        var inBatch = await PlanAsync([_company.JanKowalski, namesake], new MoveOperation(_company.GroupsOu));
        var existing = await PlanOneAsync(_company.PawelLewandowski, new MoveOperation(_company.ItOu));

        Assert.All(inBatch.Objects, o => Assert.Equal(IssueSeverity.Error, Issue(o, IssueCodes.DuplicateInBatch).Severity));
        Assert.Equal(IssueSeverity.Error, Issue(existing, IssueCodes.Duplicate).Severity);
    }

    [Fact]
    public async Task Steps_AreOrderedResetModifyMembershipRenameMove()
    {
        var plan = await PlanOneAsync(_company.JanKowalski,
            new MoveOperation(_company.SalesOu),
            new AddToGroupsOperation([_company.GroupSales]),
            new RenameOperation("{givenName} {sn}-Nowak"),
            new SetAttributeOperation(Attr.Title, "Kierownik"),
            new RemoveFromGroupsOperation([_company.GroupIt]),
            new ResetPasswordOperation(PasswordMode.GenerateUnique));

        Assert.Equal(
            [ChangeAction.ResetPassword, ChangeAction.Modify, ChangeAction.AddMember, ChangeAction.RemoveMember, ChangeAction.Rename, ChangeAction.Move],
            plan.Changes.Select(c => c.Action));
        Assert.False(plan.HasErrors);
    }

    // ------------------------------------------------------------------ delete and protection

    [Fact]
    public async Task Delete_CannotBeCombined()
    {
        var plan = await PlanOneAsync(_company.JanKowalski, new DeleteOperation(), new SetAttributeOperation(Attr.Title, "x"));

        Assert.Equal(IssueSeverity.Error, Issue(plan, IssueCodes.InvalidCombination).Severity);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public async Task Delete_ChecksChildren()
    {
        var leaf = await PlanOneAsync(_company.JanKowalski, new DeleteOperation());
        var container = await PlanOneAsync(_company.ItOu, new DeleteOperation());
        var subtree = await PlanOneAsync(_company.ItOu, new DeleteOperation(IncludeSubtree: true));

        Assert.Equal(ChangeAction.Delete, Assert.Single(leaf.Changes).Action);
        Assert.False(leaf.HasErrors);
        Assert.Equal(IssueSeverity.Error, Issue(container, IssueCodes.HasChildren).Severity);
        Assert.True(Assert.Single(subtree.Changes).DeleteSubtree);
        Assert.False(subtree.HasErrors);
    }

    [Fact]
    public async Task ProtectedAccounts_CannotBeDeletedOrDisabled()
    {
        var administrator = await PlanOneAsync(_builder.AdministratorDn, new DeleteOperation());
        var domainController = await PlanOneAsync(_builder.DomainControllerDn, new SetAccountEnabledOperation(false));
        var krbtgt = await PlanOneAsync(_builder.KrbtgtDn, new SetAccountFlagOperation(UserAccountControl.AccountDisable, true));
        var usersContainer = await PlanOneAsync(_builder.UsersDn, new MoveOperation(_company.CompanyOu));
        var adminRename = await PlanOneAsync(_builder.AdministratorDn, new RenameOperation("Admin"));
        var adminDescription = await PlanOneAsync(_builder.AdministratorDn, new SetAttributeOperation(Attr.Description, "Konto wbudowane"));

        Assert.Equal(IssueSeverity.Error, Issue(administrator, IssueCodes.ProtectedObject).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(administrator, IssueCodes.CriticalSystemObject).Severity);
        Assert.Equal(IssueSeverity.Warning, Issue(administrator, IssueCodes.AdminSdHolder).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(domainController, IssueCodes.ProtectedObject).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(krbtgt, IssueCodes.ProtectedObject).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(usersContainer, IssueCodes.CriticalSystemObject).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(adminRename, IssueCodes.CriticalSystemObject).Severity);
        Assert.False(adminDescription.HasErrors);
        Assert.True(adminDescription.HasWarnings);
    }

    // ------------------------------------------------------------------ schema validation

    [Theory]
    [InlineData("noSuchAttribute", "x", IssueCodes.UnknownAttribute)]
    [InlineData(Attr.WhenCreated, "20260101000000.0Z", IssueCodes.ReadOnlyAttribute)]
    [InlineData(Attr.MemberOf, "CN=GG_Sprzedaz,OU=Grupy,OU=Firma,DC=contoso,DC=test", IssueCodes.ReadOnlyAttribute)]
    [InlineData(Attr.Cn, "Nowa nazwa", IssueCodes.ReadOnlyAttribute)]
    [InlineData(Attr.Name, "Nowa nazwa", IssueCodes.ReadOnlyAttribute)]
    [InlineData(Attr.CanonicalName, "x", IssueCodes.ReadOnlyAttribute)]
    [InlineData(Attr.Department, "0123456789012345678901234567890123456789012345678901234567890123456789", IssueCodes.TooLong)]
    [InlineData(Attr.CountryCode, "abc", IssueCodes.InvalidValue)]
    [InlineData(Attr.CountryCode, "70000", IssueCodes.InvalidValue)]
    [InlineData(Attr.Manager, "nie jest DN", IssueCodes.InvalidValue)]
    [InlineData(Attr.Manager, "CN=Nikt,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test", IssueCodes.NotFound)]
    [InlineData(Attr.ThumbnailPhoto, "abc", IssueCodes.BinaryNotSupported)]
    [InlineData(Attr.SamAccountName, "bardzo.dlugi.login.21", IssueCodes.TooLong)]
    [InlineData(Attr.SamAccountName, "jan|kowalski", IssueCodes.InvalidValue)]
    [InlineData(Attr.IsCriticalSystemObject, "TRUE", IssueCodes.ReadOnlyAttribute)]
    [InlineData("msExchHideFromAddressLists", "perhaps", IssueCodes.InvalidValue)]
    [InlineData("bad name!", "x", IssueCodes.InvalidValue)]
    public async Task SetAttribute_ValidatesAgainstSchema(string attribute, string value, string code)
    {
        var plan = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(attribute, value));

        Assert.True(plan.HasErrors);
        Assert.Contains(AllIssues(plan), i => i.Code == code && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task Validation_SingleValuedMandatoryAndClassRules()
    {
        var singleValued = await PlanOneAsync(_company.JanKowalski, new AddValuesOperation(Attr.Mail, ["drugi@contoso.test"]));
        var mandatory = await PlanOneAsync(_company.JanKowalski, new ClearAttributeOperation(Attr.SamAccountName));
        var notForUsers = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.Location, "Warszawa"));
        var forComputers = await PlanOneAsync(_company.Computer1, new SetAttributeOperation(Attr.Location, "Warszawa, pokój 201"));
        var boolean = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation("msExchHideFromAddressLists", "true"));
        var manager = await PlanOneAsync(_company.PawelLewandowski, new SetAttributeOperation(Attr.Manager, _company.JanKowalski.ToUpperInvariant()));

        Assert.Equal(IssueSeverity.Error, Issue(singleValued, IssueCodes.SingleValued).Severity);
        Assert.Equal(IssueSeverity.Error, Issue(mandatory, IssueCodes.MandatoryAttribute).Severity);
        Assert.Equal(IssueSeverity.Warning, Issue(notForUsers, IssueCodes.UnknownAttribute).Severity);
        Assert.False(notForUsers.HasErrors);
        Assert.Empty(AllIssues(forComputers));
        Assert.Equal(["TRUE"], Change(boolean, "msExchHideFromAddressLists").NewValues);
        Assert.False(manager.HasErrors);
    }

    [Fact]
    public async Task TemplateErrors_AreReportedOnEveryObject()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.PawelLewandowski], new SetAttributeOperation(Attr.Department, "{givenName|nope}"));

        Assert.All(plan.Objects, o => Assert.Equal(IssueSeverity.Error, Issue(o, IssueCodes.TemplateError).Severity));
        Assert.Equal(2, plan.ErrorCount);
    }

    [Fact]
    public async Task Targets_AreResolvedValidatedAndDeduplicated()
    {
        var plan = await PlanAsync(
            [_company.JanKowalski, _company.JanKowalski.ToUpperInvariant(), "CN=Nikt," + _company.ItOu, "to nie jest DN"],
            new SetAttributeOperation(Attr.Title, "Kierownik"));

        Assert.Equal(3, plan.Objects.Count);
        Assert.False(plan.Objects[0].HasErrors);
        Assert.Equal(IssueCodes.NotFound, Assert.Single(plan.Objects[1].Issues).Code);
        Assert.Equal("Nikt", plan.Objects[1].DisplayName);
        Assert.Equal(IssueCodes.InvalidValue, Assert.Single(plan.Objects[2].Issues).Code);
    }

    [Fact]
    public async Task OnlyOneMoveRenameOrResetIsAllowed()
    {
        var plan = await PlanOneAsync(_company.JanKowalski, new MoveOperation(_company.SalesOu), new MoveOperation(_company.ItOu));

        Assert.Equal(IssueSeverity.Error, Issue(plan, IssueCodes.InvalidCombination).Severity);
    }

    [Fact]
    public async Task NullOperation_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync([_company.JanKowalski], [null!]));
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Permissions_AreCheckedPerAttribute()
    {
        Session.SetPermissions(_company.JanKowalski, Permissions(Attr.Department));

        var plan = await PlanOneAsync(_company.JanKowalski,
            new SetAttributeOperation(Attr.Department, "Zarząd"),
            new SetAttributeOperation(Attr.Title, "Prezes"));
        var unknown = await PlanOneAsync(_company.PawelLewandowski, new SetAttributeOperation(Attr.Title, "Prezes"));

        var issue = Issue(plan, IssueCodes.NotWritable);
        Assert.Equal(Attr.Title, issue.Attribute);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.False(unknown.HasErrors);
    }

    [Fact]
    public async Task Permissions_CanBeSkipped()
    {
        Session.SetPermissions(_company.JanKowalski, Permissions());

        var plan = await PlanAsync([_company.JanKowalski], new PlanningOptions { CheckPermissions = false }, new SetAttributeOperation(Attr.Title, "Prezes"));

        Assert.False(plan.Objects[0].HasErrors);
        Assert.DoesNotContain(Session.Operations, o => o.Kind == FakeOperationKind.GetEffectivePermissions);
    }

    // ------------------------------------------------------------------ uniqueness

    [Fact]
    public async Task Uniqueness_DetectsDuplicatesInBatchAndDirectory()
    {
        var inBatch = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new SetAttributeOperation(Attr.Mail, "wspolny@contoso.test"));
        var existing = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.Mail, "PAWEL.lewandowski@contoso.test"));
        var ownValue = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.UserPrincipalName, "JAN.KOWALSKI@contoso.test"));
        var computerName = await PlanOneAsync(_company.JanKowalski, new SetAttributeOperation(Attr.SamAccountName, "waw-pc001$"));

        Assert.All(inBatch.Objects, o => Assert.Equal(IssueSeverity.Error, Issue(o, IssueCodes.DuplicateInBatch).Severity));
        var duplicate = Issue(existing, IssueCodes.Duplicate);
        Assert.Contains(_company.PawelLewandowski, duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(Attr.Mail, duplicate.Attribute);
        Assert.False(ownValue.HasErrors);
        Assert.Equal(IssueSeverity.Error, Issue(computerName, IssueCodes.Duplicate).Severity);
    }

    [Fact]
    public async Task Uniqueness_UsesBatchedOrFiltersAndCanBeDisabled()
    {
        var targets = new[] { _company.JanKowalski, _company.LukaszWisniewski, _company.PawelLewandowski };
        await PlanAsync(targets, new SetAttributeOperation(Attr.Mail, "{givenName|nodiacritics|lower}@firma.pl"));
        var searches = Session.Operations.Where(o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.StartsWith("(|", StringComparison.Ordinal)).ToList();
        Session.ClearOperations();
        await PlanAsync(targets, new PlanningOptions { CheckUniqueness = false }, new SetAttributeOperation(Attr.Mail, "{givenName|nodiacritics|lower}@firma.pl"));

        var search = Assert.Single(searches);
        Assert.Equal(Session.Info.DefaultNamingContext, search.Search!.BaseDn);
        Assert.Contains("(mail=pawel@firma.pl)", search.Search.Filter, StringComparison.Ordinal);
        Assert.DoesNotContain(Session.Operations, o => o.Kind == FakeOperationKind.Search);
    }

    // ------------------------------------------------------------------ live reads, parallelism and progress

    [Fact]
    public async Task Planning_ReadsInParallelWithinTheLimitAndReportsProgress()
    {
        Session.Latency = TimeSpan.FromMilliseconds(15);
        var targets = new[]
        {
            _company.JanKowalski, _company.LukaszWisniewski, _company.MalgorzataWojcik, _company.GrazynaDabrowska,
            _company.ZbigniewKazmierczak, _company.PawelLewandowski, _company.ZanetaSzymanska, _company.BartlomiejKozlowski,
        };
        var progress = new SynchronousProgress<int>();

        var plan = await Planner.PlanAsync(
            Session,
            new BatchRequest(targets, [new SetAttributeOperation(Attr.Company, "Fabrikam")]),
            new PlanningOptions { MaxDegreeOfParallelism = 2, CheckUniqueness = false },
            progress,
            Ct);

        Assert.Equal(8, plan.ExecutableCount);
        Assert.InRange(Session.MaxObservedConcurrency, 1, 2);
        Assert.Equal(Enumerable.Range(1, 8), progress.Values.Order());
        Assert.Equal(targets, plan.Objects.Select(o => o.DistinguishedName));
        Assert.Equal(8, Session.Operations.Count(o => o.Kind == FakeOperationKind.Get && targets.Contains(o.DistinguishedName)));
    }

    [Fact]
    public async Task Planning_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Planner.PlanAsync(Session, new BatchRequest([_company.JanKowalski], [new SetAttributeOperation(Attr.Title, "x")]), null, null, cts.Token));
    }
}

/// <summary>IProgress that records values synchronously (Progress&lt;T&gt; posts asynchronously).</summary>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly List<T> _values = [];

    public IReadOnlyList<T> Values
    {
        get
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }

    public void Report(T value)
    {
        lock (_values)
        {
            _values.Add(value);
        }
    }
}
