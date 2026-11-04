using System.Globalization;
using AdMng.Core.Computers.Remote;

namespace AdMng.Remote;

/// <summary>
/// CIM results as property bags (name → value, as <c>CimInstance.CimInstanceProperties</c> gives them) → the contract's
/// records. Pure functions, so they are tested on every platform; the CIM client only copies the properties it asked for.
/// </summary>
internal static class CimMapping
{
    /// <summary>Win32_OperatingSystem (Caption, Version, BuildNumber, LastBootUpTime) plus the UBR registry value.</summary>
    public static RemoteOperatingSystem OperatingSystem(IReadOnlyDictionary<string, object?> os, object? updateBuildRevision)
    {
        ArgumentNullException.ThrowIfNull(os);
        return new RemoteOperatingSystem(
            Text(os, "Caption")?.Trim(),
            Text(os, "Version")?.Trim(),
            ToInt(Value(os, "BuildNumber")),
            ToInt(updateBuildRevision),
            ToDateTime(Value(os, "LastBootUpTime")));
    }

    /// <summary>
    /// The owners of explorer.exe (one per desktop session) with the console user of Win32_ComputerSystem.UserName; an
    /// owner that could not be read (GetOwner ReturnValue ≠ 0) is skipped. One row per account and session, the earliest
    /// start wins.
    /// </summary>
    public static IReadOnlyList<LoggedOnUser> LoggedOnUsers(
        IEnumerable<(IReadOnlyDictionary<string, object?> Process, IReadOnlyDictionary<string, object?>? Owner)> explorers,
        string? consoleUser)
    {
        ArgumentNullException.ThrowIfNull(explorers);
        var users = new Dictionary<(string Account, int? Session), LoggedOnUser>();
        foreach (var (process, owner) in explorers)
        {
            if (owner is null || ToInt(Value(owner, "ReturnValue")) is not (0 or null) || Text(owner, "User") is not { Length: > 0 } user)
            {
                continue;
            }

            var account = Text(owner, "Domain") is { Length: > 0 } domain ? $"{domain}\\{user}" : user;
            var session = ToInt(Value(process, "SessionId"));
            var since = ToDateTime(Value(process, "CreationDate"));
            var isConsole = consoleUser is not null && string.Equals(consoleUser.Trim(), account, StringComparison.OrdinalIgnoreCase);
            var key = (account.ToUpperInvariant(), session);
            if (!users.TryGetValue(key, out var existing) || (since is not null && (existing.Since is null || since < existing.Since)))
            {
                users[key] = new LoggedOnUser(account, session, since ?? existing?.Since, isConsole);
            }
        }

        // The console user without a desktop process (a locked console before explorer started) is still listed.
        if (consoleUser is { Length: > 0 } console && !users.Values.Any(u => string.Equals(u.Account, console.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            users[(console.Trim().ToUpperInvariant(), null)] = new LoggedOnUser(console.Trim(), null, null, true);
        }

        return [.. users.Values];
    }

    /// <summary>Win32_Service (Name, DisplayName, State, StartMode, DelayedAutoStart, StartName).</summary>
    public static RemoteService Service(IReadOnlyDictionary<string, object?> service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new RemoteService(
            Text(service, "Name") ?? string.Empty,
            Text(service, "DisplayName"),
            Text(service, "State"),
            Text(service, "StartMode"),
            Value(service, "DelayedAutoStart") is true,
            Text(service, "StartName"));
    }

    /// <summary>
    /// MSFT_ScheduledTask (TaskPath, TaskName, State) with its embedded <c>Principal</c> (UserId, LogonType) and
    /// <c>Settings</c> (Enabled) as nested property bags. An empty UserId (a group principal) is null; a missing
    /// <c>Settings.Enabled</c> is taken from the state (Disabled → false, a known other state → true).
    /// </summary>
    public static RemoteScheduledTask ScheduledTask(IReadOnlyDictionary<string, object?> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var principal = Value(task, "Principal") as IReadOnlyDictionary<string, object?>;
        var settings = Value(task, "Settings") as IReadOnlyDictionary<string, object?>;
        var state = EnumValue<RemoteTaskState>(Value(task, "State"));
        var enabled = settings is not null && Value(settings, "Enabled") is bool flag
            ? flag
            : state is null or RemoteTaskState.Unknown ? (bool?)null : state != RemoteTaskState.Disabled;
        var path = Text(task, "TaskPath")?.Trim();
        return new RemoteScheduledTask(
            string.IsNullOrEmpty(path) ? "\\" : path,
            Text(task, "TaskName")?.Trim() ?? string.Empty,
            principal is not null && Text(principal, "UserId")?.Trim() is { Length: > 0 } user ? user : null,
            principal is null ? null : EnumValue<RemoteTaskLogonType>(Value(principal, "LogonType")),
            state,
            enabled);
    }

    /// <summary>A CIM enumeration value (a number, or its name) → a defined member of <typeparamref name="TEnum"/>; null otherwise.</summary>
    public static TEnum? EnumValue<TEnum>(object? value)
        where TEnum : struct, Enum
    {
        if (ToInt(value) is { } number)
        {
            var member = (TEnum)Enum.ToObject(typeof(TEnum), number);
            return Enum.IsDefined(member) ? member : null;
        }

        // One member name only: Enum.TryParse would also take numbers and combine "A, B" into a different member.
        return value is string text && text.Trim() is { Length: > 0 } name && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit)
            && Enum.TryParse<TEnum>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
    }

    /// <summary>The end of the last computer policy processing: EndTimeHi/EndTimeLo of the Group Policy state (FILETIME, UTC).</summary>
    public static DateTimeOffset? GroupPolicyEnd(object? endTimeHigh, object? endTimeLow)
    {
        if (ToLong(endTimeHigh) is not { } high || ToLong(endTimeLow) is not { } low)
        {
            return null;
        }

        var fileTime = (high << 32) | (low & 0xFFFFFFFFL);
        return fileTime <= 0 || fileTime >= DateTime.MaxValue.ToFileTimeUtc() ? null : DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
    }

    /// <summary>A CIM datetime: <see cref="DateTime"/> (as MI .NET returns it), <see cref="DateTimeOffset"/> or DMTF text.</summary>
    public static DateTimeOffset? ToDateTime(object? value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime { Kind: DateTimeKind.Utc } utc => new DateTimeOffset(utc),
        DateTime { Kind: DateTimeKind.Local } local => new DateTimeOffset(local),
        DateTime unspecified => new DateTimeOffset(DateTime.SpecifyKind(unspecified, DateTimeKind.Local)),
        string text => ParseDmtf(text),
        _ => null,
    };

    /// <summary>DMTF datetime <c>yyyyMMddHHmmss.ffffff±UUU</c> (offset in minutes).</summary>
    public static DateTimeOffset? ParseDmtf(string? text)
    {
        if (text is null || text.Length != 25 || (text[21] != '+' && text[21] != '-'))
        {
            return null;
        }

        if (!DateTime.TryParseExact(text[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || !int.TryParse(text[22..], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes > 14 * 60)
        {
            return null;
        }

        return new DateTimeOffset(local, TimeSpan.FromMinutes(text[21] == '-' ? -minutes : minutes));
    }

    public static int? ToInt(object? value) => ToLong(value) is { } number && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    private static long? ToLong(object? value) => value switch
    {
        null => null,
        byte b => b,
        sbyte sb => sb,
        short s => s,
        ushort us => us,
        int i => i,
        uint ui => ui,
        long l => l,
        ulong ul when ul <= long.MaxValue => (long)ul,
        string text when long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static object? Value(IReadOnlyDictionary<string, object?> bag, string name) =>
        bag.TryGetValue(name, out var value) ? value : bag.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(IReadOnlyDictionary<string, object?> bag, string name) => Value(bag, name) switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };
}
