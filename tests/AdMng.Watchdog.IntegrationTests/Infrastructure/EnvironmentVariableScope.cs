namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// Sets a process environment variable for the lifetime of the scope and restores the previous value.
/// </summary>
/// <remarks>
/// The variable belongs to the whole test process and the test classes run side by side, so scopes of one variable
/// overlap: the first one sets it and remembers what was there, the last one to end restores that. A scope that only
/// remembered what it found would take the variable away from a test that still runs - the service of the end-to-end
/// test then could not bind any more ("the baseline did not happen") because a shorter test that had set the same
/// password had ended. Overlapping scopes have to want the same value; tests that need different values of one variable
/// belong in one collection.
/// </remarks>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Held> Variables = new(StringComparer.Ordinal);

    private readonly string _name;
    private bool _disposed;

    public EnvironmentVariableScope(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _name = name;
        lock (Gate)
        {
            if (Variables.TryGetValue(name, out var held))
            {
                if (!string.Equals(held.Value, value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The environment variable {name} is held by another running test with a different value; tests that need different values of it must share a collection.");
                }

                held.Users++;
                return;
            }

            Variables.Add(name, new Held(Environment.GetEnvironmentVariable(name), value));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var held = Variables[_name];
            if (--held.Users > 0)
            {
                return;
            }

            Variables.Remove(_name);
            Environment.SetEnvironmentVariable(_name, held.Previous);
        }
    }

    /// <summary>What the variable was before its first scope, what the scopes set it to, and how many still run.</summary>
    private sealed class Held(string? previous, string? value)
    {
        public string? Previous { get; } = previous;

        public string? Value { get; } = value;

        public int Users { get; set; } = 1;
    }
}
