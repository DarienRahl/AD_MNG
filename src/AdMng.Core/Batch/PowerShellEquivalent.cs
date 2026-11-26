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

    /// <summary>An array of literals <c>@('a', 'b')</c> (<see cref="PowerShellParameter.Values"/>).</summary>
    LiteralList,

    /// <summary>
    /// A secure string read at run time: <c>-Name (Read-Host -AsSecureString -Prompt '&lt;prompt&gt;')</c>
    /// (<see cref="PowerShellParameter.Value"/> = the prompt; docs/dev/phase3-bulk-create.md 2.2). The secret itself is
    /// never part of the script.
    /// </summary>
    SecurePrompt,

    /// <summary>
    /// A hashtable of attributes <c>-Name @{ key = value; … }</c> (<see cref="PowerShellParameter.Attributes"/>), written
    /// through the exporter's attribute entries: keys quoted when they contain <c>-</c>, secret attributes prompted
    /// (2.2, <c>-OtherAttributes</c>).
    /// </summary>
    AttributeTable,
}

/// <summary>One parameter of a <see cref="PowerShellCommand"/>.</summary>
public sealed record PowerShellParameter(string Name, string? Value, PowerShellValueKind Kind = PowerShellValueKind.Literal)
{
    public static PowerShellParameter Literal(string name, string value) => new(name, value ?? throw new ArgumentNullException(nameof(value)));

    public static PowerShellParameter Switch(string name) => new(name, null, PowerShellValueKind.Switch);

    public static PowerShellParameter Number(string name, long value) =>
        new(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), PowerShellValueKind.Number);

    public static PowerShellParameter Boolean(string name, bool value) => new(name, value ? "true" : "false", PowerShellValueKind.Boolean);

    /// <summary><c>-Name @('a', 'b')</c>.</summary>
    public static PowerShellParameter Literals(string name, IReadOnlyList<string> values) =>
        new(name, null, PowerShellValueKind.LiteralList) { Values = values ?? throw new ArgumentNullException(nameof(values)) };

    /// <summary><c>-Name (Read-Host -AsSecureString -Prompt '<paramref name="prompt"/>')</c> – the value is typed when the script runs.</summary>
    public static PowerShellParameter SecurePrompt(string name, string prompt) =>
        new(name, prompt ?? throw new ArgumentNullException(nameof(prompt)), PowerShellValueKind.SecurePrompt);

    /// <summary><c>-Name @{ attribute = value; … }</c> (e.g. <c>-OtherAttributes</c> of <c>New-ADUser</c>).</summary>
    public static PowerShellParameter Table(string name, IReadOnlyList<AttributeChange> attributes) =>
        new(name, null, PowerShellValueKind.AttributeTable) { Attributes = attributes ?? throw new ArgumentNullException(nameof(attributes)) };

    /// <summary>The literals of a <see cref="PowerShellValueKind.LiteralList"/> parameter.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The attributes of a <see cref="PowerShellValueKind.AttributeTable"/> parameter.</summary>
    public IReadOnlyList<AttributeChange> Attributes { get; init; } = [];

    /// <summary><c>-Server $server</c> (omitted when the script has no server).</summary>
    public static PowerShellParameter Server { get; } = new("Server", null, PowerShellValueKind.Server);

    /// <summary>The name and the kind only: values are names, addresses and other personal data (docs/dev/phase3-bulk-create.md 2.8).</summary>
    public override string ToString() => $"-{Name} ({Kind})";
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

    /// <summary>The cmdlet and its parameter names and kinds only – never a value (docs/dev/phase3-bulk-create.md 2.8).</summary>
    public override string ToString() =>
        $"{Cmdlet}{(Property is null ? string.Empty : "." + Property)} [{string.Join(", ", Parameters.Select(p => p.ToString()))}]";
}
