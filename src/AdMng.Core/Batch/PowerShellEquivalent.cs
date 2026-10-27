namespace AdMng.Core.Batch;

/// <summary>
/// The semantic PowerShell of one object of a plan (<see cref="ObjectPlan.PowerShell"/>; design note
/// docs/dev/k-computers-and-gpo.md 3.3): structured commands that <see cref="PowerShellExporter"/> renders with quoted
/// literals, so planners never build script text. An empty list: covered by the command of an earlier object.
/// </summary>
public sealed record PowerShellEquivalent(IReadOnlyList<PowerShellCommand> Commands)
{
    /// <summary>Covered by the command of an earlier object.</summary>
    public static PowerShellEquivalent Covered { get; } = new([]);

    public static PowerShellEquivalent Of(params PowerShellCommand[] commands) => new(commands);
}

/// <summary>What a parameter value is.</summary>
public enum PowerShellValueKind
{
    /// <summary>A literal, written as a single-quoted string.</summary>
    Literal,

    /// <summary>A switch: <c>-Name</c> without a value.</summary>
    Switch,

    /// <summary>A whole number, written as it is.</summary>
    Number,

    /// <summary>The domain controller of the script: <c>$server</c>.</summary>
    Server,

    /// <summary><c>$true</c> / <c>$false</c> (Value "true" / "false").</summary>
    Boolean,
}

/// <summary>One parameter of a <see cref="PowerShellCommand"/>.</summary>
public sealed record PowerShellParameter(string Name, string? Value, PowerShellValueKind Kind = PowerShellValueKind.Literal)
{
    public static PowerShellParameter Literal(string name, string value) => new(name, value ?? throw new ArgumentNullException(nameof(value)));

    public static PowerShellParameter Switch(string name) => new(name, null, PowerShellValueKind.Switch);

    public static PowerShellParameter Number(string name, long value) =>
        new(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), PowerShellValueKind.Number);

    public static PowerShellParameter Boolean(string name, bool value) => new(name, value ? "true" : "false", PowerShellValueKind.Boolean);

    /// <summary><c>-Server $server</c> (omitted when the script has no server).</summary>
    public static PowerShellParameter Server { get; } = new("Server", null, PowerShellValueKind.Server);
}

/// <summary>
/// One command: <c>Cmdlet -Name 'value' …</c>, or with <see cref="Property"/> an assignment
/// <c>(Cmdlet …).Property = 'value'</c> (e.g. <c>(Get-GPO -Guid …).GpoStatus = 'UserSettingsDisabled'</c>).
/// </summary>
/// <param name="Cmdlet">Name of the cmdlet (a fixed identifier, never user input).</param>
/// <param name="Parameters">Parameters in order.</param>
public sealed record PowerShellCommand(string Cmdlet, IReadOnlyList<PowerShellParameter> Parameters)
{
    /// <summary>Module the cmdlet comes from (the script header adds <c>#Requires -Modules</c> for it).</summary>
    public string Module { get; init; } = "GroupPolicy";

    /// <summary>Property assigned on the command's output.</summary>
    public string? Property { get; init; }

    /// <summary>Literal value assigned to <see cref="Property"/>.</summary>
    public string? PropertyValue { get; init; }

    /// <summary>A comment line written before the command (English, no directory values beyond names already in the command).</summary>
    public string? Comment { get; init; }

    public static PowerShellCommand Of(string cmdlet, params PowerShellParameter[] parameters) => new(cmdlet, parameters);
}
