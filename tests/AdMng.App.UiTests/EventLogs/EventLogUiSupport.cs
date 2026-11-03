using System.Globalization;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.Ldap;
using AdMng.Testing.EventLogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.EventLogs;

/// <summary>
/// Helpers of the event log UI tests (track E): the sample company with two DCs (dc01 = PDC emulator and the window's DC,
/// dc02), the lockout policy of the Watchdog infrastructure (5 bad passwords, 30 min) and a
/// <see cref="FakeEventLogConnector"/> with the Security logs of both DCs. The sample records are moved to the last
/// minutes before now: the documents ask for windows relative to the system clock the application keeps.
/// </summary>
internal static class EventLogUiSupport
{
    public const string Dc01 = "dc01.contoso.test";
    public const string Dc02 = "dc02.contoso.test";

    /// <summary>An empty in-memory reader of both DCs' Security logs (dc01 also System).</summary>
    public static FakeEventLogConnector CreateConnector() =>
        new FakeEventLogConnector().AddHost(Dc01, EventLogSamples.SecurityChannel, EventLogSamples.SystemChannel).AddHost(Dc02, EventLogSamples.SecurityChannel);

    /// <summary>Registers <paramref name="connector"/> as the application's reader (instead of the empty default fake).</summary>
    public static Action<IServiceCollection> Use(FakeEventLogConnector connector) =>
        services => services.Replace(ServiceDescriptor.Singleton<IEventLogConnector>(connector));

    /// <summary><paramref name="records"/> moved in time so that the newest one is at <paramref name="newest"/>.</summary>
    public static IReadOnlyList<EventLogRecord> MovedTo(IReadOnlyList<EventLogRecord> records, DateTimeOffset newest)
    {
        var shift = newest - records.Max(r => r.TimeCreated);
        return [.. records.Select(r => r with { TimeCreated = r.TimeCreated + shift })];
    }

    /// <summary>
    /// Adds the two DCs and the lockout policy to the directory and gives Jan Kowalski the SID the samples log for
    /// <see cref="EventLogSamples.JanKowalski"/> (call before connecting).
    /// </summary>
    public static async Task AddDomainControllersAsync(UiTestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Directory.AddWatchdogInfrastructure();
        context.Directory.AddDomainController(Dc02, new Guid("2f0f0f0f-1111-2222-3333-444455556666"));
        await UseSampleSidAsync(context, context.Company.JanKowalski, EventLogSamples.JanKowalski);
    }

    /// <summary>
    /// Jan Kowalski locked out two minutes ago by an old password on WAW-PC001 (the samples' lockout scenario moved to now):
    /// lockoutTime and five bad passwords, the records in both DCs' logs (dc01 reaching back nine days, dc02 two). Returns the
    /// moment of the lockout.
    /// </summary>
    public static DateTimeOffset LockOutJan(UiTestContext context, FakeEventLogConnector connector)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connector);
        var records = MovedTo(EventLogSamples.LockoutScenario(), DateTimeOffset.UtcNow.AddMinutes(-1));
        var lockout = records.Single(r => r.EventId == 4740).TimeCreated;
        var session = context.Directory.Session;
        var jan = context.Company.JanKowalski;
        session.SetValues(jan, Attr.BadPwdCount, "5");
        session.SetValues(jan, Attr.BadPasswordTime, FileTime(lockout));
        session.SetValues(jan, Attr.LockoutTime, FileTime(lockout));
        connector.Add(records);

        // Coverage as on real DCs: the PDC emulator's 128 MB log reaches back nine days, dc02's two.
        var now = DateTimeOffset.UtcNow;
        connector.SetCoverage(Dc01, EventLogSamples.SecurityChannel, new EventLogCoverage
        {
            Host = Dc01,
            Channel = EventLogSamples.SecurityChannel,
            Oldest = now.AddDays(-9),
            Newest = now,
            RecordCount = 412_877,
            FileSizeBytes = 128L * 1024 * 1024,
            MaximumSizeBytes = 128L * 1024 * 1024,
            Mode = EventLogMode.Circular,
            IsFull = true,
        });
        connector.SetCoverage(Dc02, EventLogSamples.SecurityChannel, new EventLogCoverage
        {
            Host = Dc02,
            Channel = EventLogSamples.SecurityChannel,
            Oldest = now.AddDays(-2),
            Newest = now,
            RecordCount = 98_310,
            FileSizeBytes = 20L * 1024 * 1024,
            MaximumSizeBytes = 20L * 1024 * 1024,
            Mode = EventLogMode.Circular,
            IsFull = true,
        });
        return lockout;
    }

    /// <summary>The samples' failed logons (spraying, guessing, enumeration) moved to end five minutes ago.</summary>
    public static void AddFailedLogons(FakeEventLogConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        connector.Add(MovedTo(EventLogSamples.FailedLogonScenario(), DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    private static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>Gives <paramref name="dn"/> the SID the samples log for <paramref name="account"/> (the object holding it gets a fresh one).</summary>
    private static async Task UseSampleSidAsync(UiTestContext context, string dn, string account)
    {
        var session = context.Directory.Session;
        var sid = EventLogSamples.Sid(account);
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.Equal(Attr.ObjectSid, sid),
            Attributes = [Attr.Name],
        };
        var holders = new List<string>();
        await foreach (var holder in session.SearchAsync(search, CancellationToken.None))
        {
            holders.Add(holder.DistinguishedName);
        }

        foreach (var holder in holders)
        {
            session.SetValues(holder, Attr.ObjectSid, context.Directory.NextSid());
        }

        session.SetValues(dn, Attr.ObjectSid, sid);
    }
}
