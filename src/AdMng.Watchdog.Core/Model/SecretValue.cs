namespace AdMng.Watchdog.Core.Model;

/// <summary>A secret in memory only. ToString never reveals it.</summary>
public sealed class SecretValue
{
    private readonly string _value;

    public SecretValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>The secret itself – call only at the point of use (transport, data protection).</summary>
    public string Reveal() => _value;

    public override string ToString() => "SecretValue(***)";
}

/// <summary>A stored secret with the destination binding it was entered for (SecretBindings, section 18).</summary>
public sealed record StoredSecret(SecretValue Value, string Binding)
{
    public override string ToString() => $"StoredSecret(binding {Binding})";
}

/// <summary>One secret write applied in the same transaction as a configuration save. Value null = remove.</summary>
public sealed record SecretChange(string Name, SecretValue? Value, string? Binding)
{
    public override string ToString() => $"SecretChange({Name}, {(Value is null ? "remove" : "set")})";
}
