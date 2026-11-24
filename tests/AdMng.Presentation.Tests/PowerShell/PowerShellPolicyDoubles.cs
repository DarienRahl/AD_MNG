using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>The registry and the config files of a workstation, in memory: what <see cref="PowerShellPolicyReader"/> reads.</summary>
internal sealed class FakePowerShellPolicySource : IPowerShellPolicySource
{
    private readonly Dictionary<(PowerShellPolicyScope, string, string), object> _values = new(new KeyComparer());
    private readonly Dictionary<(PowerShellPolicyScope, string), List<string>> _names = new(new NameComparer());
    private readonly Dictionary<PowerShellPolicyScope, string> _files = [];

    public PowerShellLockdownState Lockdown { get; set; }

    /// <summary>Every call the reader made, in order ("value Computer Software\...\Transcription EnableTranscripting").</summary>
    public List<string> Calls { get; } = [];

    /// <summary>A failure of the next read (the registry cannot be opened).</summary>
    public Exception? Failure { get; set; }

    /// <summary>A value as the registry holds it: an int is a REG_DWORD, a long a REG_QWORD, a string a REG_SZ, a string array a REG_MULTI_SZ.</summary>
    public FakePowerShellPolicySource Set(PowerShellPolicyScope scope, string subKey, string name, object value)
    {
        _values[(scope, subKey, name)] = value;
        return this;
    }

    public FakePowerShellPolicySource SetDword(PowerShellPolicyScope scope, string subKey, string name, int value) => Set(scope, subKey, name, value);

    public FakePowerShellPolicySource SetString(PowerShellPolicyScope scope, string subKey, string name, string value) => Set(scope, subKey, name, value);

    /// <summary>A key whose value names are <paramref name="names"/> (also none: the key is there and empty).</summary>
    public FakePowerShellPolicySource SetNames(PowerShellPolicyScope scope, string subKey, params string[] names)
    {
        _names[(scope, subKey)] = [.. names];
        return this;
    }

    public FakePowerShellPolicySource SetFile(PowerShellPolicyScope scope, string json)
    {
        _files[scope] = json;
        return this;
    }

    /// <summary>The PowerShell 7 policy of a hive: <c>PowerShellCore\&lt;section&gt;</c>.</summary>
    public FakePowerShellPolicySource Core(PowerShellPolicyScope scope, string section, string name, object value) =>
        Set(scope, PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, section), name, value);

    /// <summary>The Windows PowerShell policy of a hive.</summary>
    public FakePowerShellPolicySource Windows(PowerShellPolicyScope scope, string section, string name, object value) =>
        Set(scope, PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.WindowsRoot, section), name, value);

    public object? GetValue(PowerShellPolicyScope scope, string subKey, string valueName)
    {
        Calls.Add($"value {scope} {subKey} {valueName}");
        Fail();
        return _values.TryGetValue((scope, subKey, valueName), out var value) ? value : null;
    }

    public IReadOnlyList<string>? GetValueNames(PowerShellPolicyScope scope, string subKey)
    {
        Calls.Add($"names {scope} {subKey}");
        Fail();
        return _names.TryGetValue((scope, subKey), out var names) ? names : null;
    }

    public string? ReadConfigFile(PowerShellPolicyScope scope)
    {
        Calls.Add($"file {scope}");
        Fail();
        return _files.TryGetValue(scope, out var text) ? text : null;
    }

    public PowerShellLockdownState GetLockdownState()
    {
        Calls.Add("lockdown");
        Fail();
        return Lockdown;
    }

    private void Fail()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }

    // The registry ignores the case of key and value names.
    private sealed class KeyComparer : IEqualityComparer<(PowerShellPolicyScope, string, string)>
    {
        public bool Equals((PowerShellPolicyScope, string, string) x, (PowerShellPolicyScope, string, string) y) =>
            x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Item3, y.Item3, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((PowerShellPolicyScope, string, string) obj) =>
            HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item3));
    }

    private sealed class NameComparer : IEqualityComparer<(PowerShellPolicyScope, string)>
    {
        public bool Equals((PowerShellPolicyScope, string) x, (PowerShellPolicyScope, string) y) =>
            x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((PowerShellPolicyScope, string) obj) =>
            HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}

/// <summary>A policy reader that returns what the test says (or fails, or waits), and counts its calls.</summary>
internal sealed class FakePowerShellPolicyReader(PowerShellPolicies? policies = null) : IPowerShellPolicyReader
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _held;

    public PowerShellPolicies Policies { get; set; } = policies ?? PowerShellPolicies.None;

    public Exception? Failure { get; set; }

    public int Reads { get; private set; }

    /// <summary>The next read waits until <see cref="Release"/>.</summary>
    public void Hold() => _held = true;

    public void Release() => _gate.TrySetResult();

    public async Task<PowerShellPolicies> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        if (_held)
        {
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Failure is { } failure ? throw failure : Policies;
    }
}
