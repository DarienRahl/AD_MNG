using System.Collections.Specialized;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Session;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Infrastructure;

public class InfrastructureTests
{
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public void BulkCollection_RaisesOneNotificationPerBatch()
    {
        var collection = new BulkObservableCollection<int>([1]);
        var events = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => events.Add(e.Action);

        collection.AddRange(Enumerable.Range(2, 500));
        collection.AddRange([]);
        collection.RemoveWhere(i => i % 2 == 0);
        collection.ReplaceAll([7, 8]);

        Assert.Equal([NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset], events);
        Assert.Equal([7, 8], collection);
    }

    [Theory]
    [InlineData("Łukasz Żółć", "lukasz zolc", true)]
    [InlineData("ŁUKASZ", "łuk", true)]
    [InlineData("Księgowość", "ksiegow", true)]
    [InlineData("Ewa", "łukasz", false)]
    public void TextFolding_IsCaseAndDiacriticsInsensitive(string text, string query, bool expected) =>
        Assert.Equal(expected, TextFolding.Contains(text, query));

    [Fact]
    public void SubsequenceScore_PrefersWordStartsAndContiguousMatches()
    {
        Assert.Null(TextFolding.SubsequenceScore("Refresh", "xyz"));
        Assert.True(TextFolding.SubsequenceScore("Reset Password...", "rp") > TextFolding.SubsequenceScore("Properties", "rp"));
        Assert.True(TextFolding.SubsequenceScore("Wyłącz konto", "wylacz") > 0);
    }

    [Fact]
    public async Task Debouncer_RunsOnlyTheLastRequest()
    {
        var time = new ManualTimeProvider(TestHarness.Now);
        var debouncer = new Debouncer(time, TimeSpan.FromMilliseconds(300));
        var executed = new List<string>();

        var first = debouncer.RunAsync(_ => { executed.Add("a"); return Task.CompletedTask; });
        time.Advance(TimeSpan.FromMilliseconds(100));
        var second = debouncer.RunAsync(_ => { executed.Add("ab"); return Task.CompletedTask; });
        time.Advance(TimeSpan.FromMilliseconds(300));
        await Task.WhenAll(first, second);

        Assert.Equal(["ab"], executed);
    }

    [Fact]
    public void ObjectIcons_ReflectAccountStateAndGroupType()
    {
        var now = TestHarness.Now;
        Assert.Equal(ObjectIcon.UserDisabled, ObjectIcons.For(TestDirectory.User("CN=a,DC=x", "a", disabled: true)));
        Assert.Equal(ObjectIcon.UserLocked, ObjectIcons.For(TestDirectory.User("CN=a,DC=x", "a", lockoutTime: now)));
        Assert.Equal(ObjectIcon.User, ObjectIcons.For(TestDirectory.User("CN=a,DC=x", "a")));
        Assert.Equal(ObjectIcon.Computer, ObjectIcons.For(TestDirectory.Computer("CN=c,DC=x", "c$")));
        Assert.Equal(ObjectIcon.OrganizationalUnit, ObjectIcons.For(TestDirectory.Ou("OU=o,DC=x")));
        var distribution = Obj.Create("CN=g,DC=x", Obj.A(Attr.ObjectClass, "top", "group"),
            Obj.A(Attr.GroupType, GroupTypes.Compose(GroupScope.Universal, security: false).ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(ObjectIcon.GroupDistribution, ObjectIcons.For(distribution));
        Assert.Equal(ObjectIcon.Builtin, ObjectIcons.For(ObjectKind.BuiltinDomain, false, false));
    }

    [Fact]
    public void ErrorTranslator_ExplainsBindFailuresWithHints()
    {
        var errors = new ErrorTranslator(English);
        var locked = new DirectoryException(DirectoryErrorKind.AccountRestriction, "bind failed") { Win32Error = 0x775 };
        var badPassword = new DirectoryException(DirectoryErrorKind.InvalidCredentials, "bind failed") { Win32Error = 0x52E };
        var signing = new DirectoryException(DirectoryErrorKind.StrongAuthRequired, "strong auth");
        var unreachable = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");

        Assert.StartsWith("The account is locked out.", errors.TranslateConnectError(locked), StringComparison.Ordinal);
        Assert.Contains("Caps Lock", errors.TranslateConnectError(badPassword), StringComparison.Ordinal);
        Assert.Contains("LDAPS", errors.TranslateConnectError(signing), StringComparison.Ordinal);
        Assert.Contains("firewall", errors.TranslateConnectError(unreachable), StringComparison.Ordinal);
        Assert.Equal("The operation was cancelled.", errors.Translate(new OperationCanceledException()));
    }

    [Fact]
    public void ErrorTranslator_TranslatesBatchFailuresFromServerDiagnostics()
    {
        var errors = new ErrorTranslator(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")));

        Assert.Equal(
            "Hasło nie spełnia wymagań zasad haseł (długość, złożoność, historia).",
            errors.TranslateFailure(DirectoryErrorKind.ConstraintViolation, "0000052D: SvcErr: DSID-031A1254, problem 5003 (WILL_NOT_PERFORM)"));
        Assert.Equal("Odmowa dostępu.", errors.TranslateFailure(DirectoryErrorKind.AccessDenied, null));
        Assert.Equal("Nieoczekiwany błąd: boom", errors.TranslateFailure(null, "boom"));
    }

    [Fact]
    public void LocalizedFormatter_ShowsNeverForFileTimeSentinels()
    {
        var formatter = new LocalizedAttributeFormatter(new PassThroughAttributeFormatter(), new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")));
        var culture = CultureInfo.GetCultureInfo("pl-PL");

        Assert.Equal("(nigdy)", formatter.Format(Attr.AccountExpires, new AttributeValues(Attr.AccountExpires, ["9223372036854775807"]), culture));
        Assert.Equal("(nigdy)", formatter.Format(Attr.PwdLastSet, new AttributeValues(Attr.PwdLastSet, ["0"]), culture));
        Assert.Equal("Dział IT", formatter.Format(Attr.Description, new AttributeValues(Attr.Description, ["Dział IT"]), culture));
    }

    [Fact]
    public void DomainPolicy_EvaluatesLockoutAgainstDuration()
    {
        var now = TestHarness.Now;
        var policy = new DomainPolicy { IsKnown = true, LockoutDuration = TimeSpan.FromMinutes(30) };
        var forever = new DomainPolicy { IsKnown = true, LockoutDuration = null };

        Assert.Equal(LockState.Locked, policy.GetLockState(AdConvert.ToFileTime(now.AddMinutes(-10)), now));
        Assert.Equal(LockState.NotLocked, policy.GetLockState(AdConvert.ToFileTime(now.AddHours(-1)), now));
        Assert.Equal(LockState.NotLocked, policy.GetLockState(0, now));
        Assert.Equal(LockState.Locked, forever.GetLockState(AdConvert.ToFileTime(now.AddDays(-30)), now));
        Assert.Equal(LockState.PossiblyLocked, DomainPolicy.Unknown.GetLockState(AdConvert.ToFileTime(now.AddDays(-30)), now));
    }

    [Theory]
    [InlineData("contoso.test/Firma/IT", new[] { "contoso.test", "Firma", "IT" })]
    [InlineData("contoso.test/Dział A\\/B/", new[] { "contoso.test", "Dział A/B" })]
    [InlineData(" / ", new string[0])]
    public void AddressResolver_SplitsCanonicalPaths(string path, string[] expected) =>
        Assert.Equal(expected, AddressResolver.SplitPath(path));

    [Fact]
    public async Task AddressResolver_ResolvesCanonicalPathsAndDns()
    {
        var session = TestDirectory.Build(TestHarness.Now);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(TestDirectory.It, await AddressResolver.ResolveAsync(session, "contoso.test/Firma/IT", ct), ignoreCase: true);
        Assert.Equal(TestDirectory.Ksiegowosc, await AddressResolver.ResolveAsync(session, "Firma/Księgowość", ct), ignoreCase: true);
        Assert.Null(await AddressResolver.ResolveAsync(session, "contoso.test/Firma/Nieistniejące", ct));
        Assert.Equal(TestDirectory.It, await AddressResolver.ResolveAsync(session, TestDirectory.It, ct));
        Assert.Equal("contoso.test/Firma/IT", AddressResolver.ToDisplayPath(TestDirectory.It));
        Assert.Equal("contoso.test", AddressResolver.ToDisplayPath(TestDirectory.Domain));
    }
}
