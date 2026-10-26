using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// The test run (process) that created a run OU, kept in the OU's description as
/// <c>[run &lt;host&gt; pid &lt;id&gt; started &lt;UTC time&gt;]</c>. The host is the machine name plus, on Linux, the
/// process namespace: containers on one machine may share a name but never process ids.
/// </summary>
internal sealed partial record LabRunOwner(string Host, int ProcessId, DateTimeOffset StartedAt)
{
    /// <summary>This test process.</summary>
    public static LabRunOwner Current { get; } = Create();

    /// <summary>The text appended to the run OU's description.</summary>
    public string Format() =>
        string.Create(CultureInfo.InvariantCulture, $"[run {Host} pid {ProcessId} started {StartedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}]");

    /// <summary>The owner named in <paramref name="description"/>, or null (no owner: an OU of an earlier fixture version).</summary>
    public static LabRunOwner? Find(string? description)
    {
        if (description is null || OwnerPattern().Match(description) is not { Success: true } match)
        {
            return null;
        }

        return int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            && DateTimeOffset.TryParse(match.Groups["started"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)
                ? new LabRunOwner(match.Groups["host"].Value, pid, started)
                : null;
    }

    /// <summary>
    /// True when <paramref name="owner"/> is a process of this host (and process namespace) that still runs – the same
    /// process, not a later one that got its id. False for owners elsewhere: whether they still run is unknown here.
    /// </summary>
    public static bool IsRunning(LabRunOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!string.Equals(owner.Host, Current.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return Math.Abs((process.StartTime.ToUniversalTime() - owner.StartedAt.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Not running (or not observable).
            return false;
        }
    }

    private static LabRunOwner Create()
    {
        using var self = Process.GetCurrentProcess();
        var started = self.StartTime.ToUniversalTime();
        return new LabRunOwner(HostId(), Environment.ProcessId, new DateTimeOffset(started.AddTicks(-(started.Ticks % TimeSpan.TicksPerMillisecond)), TimeSpan.Zero));
    }

    private static string HostId()
    {
        var name = Environment.MachineName;
        if (!OperatingSystem.IsLinux())
        {
            return name;
        }

        try
        {
            // "pid:[4026531836]"
            var target = new FileInfo("/proc/self/ns/pid").LinkTarget;
            var digits = new string([.. (target ?? string.Empty).Where(char.IsAsciiDigit)]);
            return digits.Length > 0 ? $"{name}:{digits}" : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return name;
        }
    }

    [GeneratedRegex(@"\[run (?<host>\S+) pid (?<pid>\d+) started (?<started>\S+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();
}

/// <summary>
/// The run OUs of one integration test project directly below the lab domain: <c>OU=&lt;prefix&gt;&lt;UTC
/// time&gt;-&lt;random&gt;</c>, one per test run, its description naming the owner (<see cref="LabRunOwner"/>), so
/// concurrent runs on the shared lab (worktrees, CI jobs) never share or delete each other's OU. At start-up a run
/// removes the OUs of runs that are over (<see cref="FindStaleAsync"/>) client-side (<see cref="LabCleanup"/>);
/// failures there are reported, not thrown – the run has its own OU. Errors of a busy or missing lab are explained
/// (<see cref="Explain"/>). Also used by <c>AdMng.Watchdog.IntegrationTests</c> (linked file).
/// </summary>
internal sealed class LabRuns(string domainDn, string prefix, string purpose, TimeSpan staleAfter, params string[] legacyNames)
{
    /// <summary>The owner written into new run OUs; never stale.</summary>
    public LabRunOwner Owner { get; init; } = LabRunOwner.Current;

    /// <summary>Whether an owner of this host still runs (<see cref="LabRunOwner.IsRunning"/>).</summary>
    public Func<LabRunOwner, bool> IsAlive { get; init; } = LabRunOwner.IsRunning;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The lab DC as named in messages.</summary>
    public string Server { get; init; } = "the lab DC";

    /// <summary>Progress of the start-up cleanup (the fixtures send it as an xUnit diagnostic message).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Description of a new run OU.</summary>
    public string Description => $"{purpose} – safe to delete {Owner.Format()}";

    /// <summary>A new, unique run OU name: <c>&lt;prefix&gt;yyyyMMdd-HHmmss-&lt;6 hex&gt;</c> (UTC).</summary>
    public string NewRunName() =>
        prefix + Time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// The run OUs that are over: the owner is a process of this host that ended, or – owner elsewhere or unknown (OUs
    /// of earlier fixture versions, including <c>legacyNames</c>) – created more than <c>staleAfter</c> ago. OUs of runs
    /// still in progress on this host are kept however old; this run's own OU is never stale.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindStaleAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var names = legacyNames.Select(n => LdapFilter.Equal("ou", n)).Prepend(LdapFilter.StartsWith("ou", prefix));
        var now = Time.GetUtcNow();
        var stale = new List<string>();
        await foreach (var ou in session.SearchAsync(new DirectorySearch
        {
            BaseDn = domainDn,
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.OrganizationalUnit), LdapFilter.Or(names)),
            Attributes = [Attr.WhenCreated, Attr.Description],
        }, cancellationToken))
        {
            var name = DistinguishedName.Parse(ou.DistinguishedName).Name;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !legacyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var owner = LabRunOwner.Find(ou.GetString(Attr.Description));
            if (owner == Owner)
            {
                continue;
            }

            var over = owner is not null && string.Equals(owner.Host, Owner.Host, StringComparison.OrdinalIgnoreCase)
                ? !IsAlive(owner)
                : ou.GetGeneralizedTime(Attr.WhenCreated) is { } created && now - created > staleAfter;
            if (over)
            {
                stale.Add(ou.DistinguishedName);
            }
        }

        return stale;
    }

    /// <summary>Deletes the stale run OUs client-side; returns those that could not be deleted (explained), never throws for them.</summary>
    public async Task<IReadOnlyList<(string Dn, Exception Error)>> RemoveStaleAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var failures = new List<(string Dn, Exception Error)>();
        foreach (var dn in await FindStaleAsync(session, cancellationToken))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var deleted = await LabCleanup.DeleteTreeAsync(session, dn, cancellationToken);
                Log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Removed the leftover {dn}: {deleted} objects in {stopwatch.Elapsed.TotalSeconds:F0} s."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var explained = Explain(ex, $"deleting the leftover {dn}");
                Log?.Invoke($"Could not remove the leftover {dn}: {explained.Message}");
                failures.Add((dn, explained));
            }
        }

        return failures;
    }

    /// <summary>Creates the run OU <paramref name="name"/> with <see cref="Description"/>; a busy or missing lab is explained.</summary>
    public async Task<string> CreateRunOuAsync(IDirectorySession session, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var dn = DistinguishedName.Parse(domainDn).Child("OU", name).ToString();
        try
        {
            await session.AddAsync(
                dn,
                new Dictionary<string, IReadOnlyList<object>>
                {
                    [Attr.ObjectClass] = ["top", ObjectClasses.OrganizationalUnit],
                    [Attr.Description] = [Description],
                },
                cancellationToken);
        }
        catch (DirectoryException ex) when (Explain(ex, "creating the run OU") is var explained && !ReferenceEquals(explained, ex))
        {
            throw explained;
        }

        return dn;
    }

    /// <summary>
    /// An exception that says what to do when the lab is busy (a timeout or LDAP busy: another run holds the DC) or not
    /// reachable; any other <paramref name="error"/> is returned unchanged.
    /// </summary>
    public Exception Explain(Exception error, string action)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            DirectoryException { Kind: DirectoryErrorKind.Timeout or DirectoryErrorKind.Busy } => new InvalidOperationException(
                $"The lab DC {Server} is busy: {action} did not finish in time. Another test run is probably deleting a large "
                + "test tree or writing many objects (Samba runs one write transaction at a time and ends a request after 300 s). "
                + "Wait until it is done and run the tests again; leftovers of aborted runs are removed at the next start "
                + "(tools/testlab/README.md).",
                error),
            DirectoryException { Kind: DirectoryErrorKind.ServerUnavailable } => new InvalidOperationException(
                $"The lab DC {Server} is not reachable ({action}). Start it with 'samba -D', wait for port 389 and run the "
                + "tests again (tools/testlab/README.md).",
                error),
            _ => error,
        };
    }
}
