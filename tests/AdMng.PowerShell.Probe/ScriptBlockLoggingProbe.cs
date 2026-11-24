using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace AdMng.PowerShell.Probe;

/// <summary>
/// The mode <c>script-block-logging:&lt;all-users file&gt;|&lt;user's file&gt;</c>: what the engine does with script block logging
/// when its all-users <c>powershell.config.json</c> is the first file and the user's the second (decision D-B, audit PS-04). The
/// all-users file is set the way <c>pwsh -SettingsFile</c> sets it (<c>PowerShellConfig.SetSystemConfigFilePath</c>), the user's
/// file by its field (<c>perUserConfigFile</c>, so that the real user's file never counts; a path that does not exist is no file),
/// both before the engine reads any configuration of this process. The report says the setting the engine works with
/// (<c>Utils.GetPolicySetting&lt;ScriptBlockLogging&gt;</c>: Group Policy, then the all-users file, then the user's - <c>null</c>
/// when none sets it) and, for each of <see cref="Lines"/>, whether the engine wrote the compiled and run script block to its log
/// (<c>HasLogged</c>) or decided not to (<c>SkipLogging</c>). The members are internal to <c>System.Management.Automation</c> 7.6.10
/// (<c>CompiledScriptBlock.cs</c>, <c>Utils.cs</c>, <c>PSConfiguration.cs</c>); a later engine that renames one fails here, loudly,
/// and the docs that rely on it are read again.
/// </summary>
internal static class ScriptBlockLoggingProbe
{
    /// <summary>
    /// The operator's lines, by name, in the order of the report. The first eight are marked suspicious by
    /// <c>ScriptBlock.CheckSuspiciousContent</c>: a word of the engine's list inside one run of letters and '-'
    /// (<c>SuspiciousContentChecker.Match</c> while it reads: "Properties", and "Marshal" inside the account name "jmarshall"), or a
    /// mark of the parser's <c>SemanticChecks</c> (<c>CheckMemberAccess</c>: a member name that is not a constant, a static member
    /// of something that is not a type literal; <c>VisitTypeExpression</c>: the type <c>[type]</c>), or the last letters of the
    /// text, read without what lies between them, spell a word of the list (<c>Match</c> hashes only letters and '-' and tests
    /// every length once more at the end: "gracemit" ends in "Emit"). The last four are not - among them a word of the list with a
    /// digit ("FromBase64String": a digit is never hashed, so such a word never matches) and a line whose letters hold no word of
    /// the list anywhere (the pilot's PW12, "get-admuserjkowalski"). <c>Get-AdmUser</c> is a function of the probe that does
    /// nothing.
    /// </summary>
    public static IReadOnlyList<(string Name, string Line)> Lines { get; } =
    [
        ("word", "$properties = 'mail', 'department'"),
        ("member-by-variable", "$u = @{ mail = 'a' }; foreach ($a in 'mail', 'department') { $u.$a }"),
        ("member-by-expression", "$u = @{ mail = 'a' }; $u.('mail')"),
        ("member-by-expandable-string", "$u = @{ mail = 'a' }; $n = 'mail'; $u.\"$n\""),
        ("static-member-through-variable", "$t = [datetime]; $t::Now"),
        ("type-type", "[type]"),
        ("last-letters", "Get-AdmUser 'grace mit'"),
        ("word-inside-name", "Get-AdmUser jmarshall"),
        ("ordinary", "$attributes = 'mail', 'department'"),
        ("members-by-constant-name", "$u = @{ mail = 'a' }; $u.mail; $u.'mail'; [datetime]::Now"),
        ("word-with-digit", "$x = [Convert]::FromBase64String('eA==')"),
        ("account-name", "Get-AdmUser jkowalski"),
    ];

    private const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags InternalStatic = BindingFlags.NonPublic | BindingFlags.Static;

    public static string Run(string files)
    {
        var parts = files.Split('|');
        if (parts.Length != 2)
        {
            throw new ArgumentException("Expected <all-users file>|<user's file>: " + files, nameof(files));
        }

        PowerShellEnvironment.Apply();
        var engine = typeof(PSObject).Assembly;
        var config = Required(engine.GetType("System.Management.Automation.Configuration.PowerShellConfig"), "PowerShellConfig");
        var instance = Required(config.GetField("Instance", InternalStatic), "PowerShellConfig.Instance").GetValue(null);
        Required(config.GetMethod("SetSystemConfigFilePath", Internal), "PowerShellConfig.SetSystemConfigFilePath").Invoke(instance, [Path.GetFullPath(parts[0])]);
        Required(config.GetField("perUserConfigFile", Internal), "PowerShellConfig.perUserConfigFile").SetValue(instance, Path.GetFullPath(parts[1]));

        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var ps = SmaPowerShell.Create(runspace);

        // Each line is compiled as a script block of its own and run once (logging happens when a block starts), then the blocks
        // are the output.
        var script = new StringBuilder("function Get-AdmUser { param($Identity) } ");
        for (var index = 0; index < Lines.Count; index++)
        {
            script.Append(CultureInfo.InvariantCulture, $"$b{index} = [scriptblock]::Create('{Lines[index].Line.Replace("'", "''", StringComparison.Ordinal)}'); $null = & $b{index}; ");
        }

        script.Append(string.Join(", ", Enumerable.Range(0, Lines.Count).Select(index => "$b" + index.ToString(CultureInfo.InvariantCulture))));
        var blocks = ps.AddScript(script.ToString()).Invoke();
        if (ps.HadErrors || blocks.Count != Lines.Count)
        {
            throw new InvalidOperationException("The script blocks did not run: " + string.Join("; ", ps.Streams.Error.Select(error => error.ToString())));
        }

        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture, $"setting={Setting(engine)}\n");
        for (var index = 0; index < Lines.Count; index++)
        {
            report.Append(CultureInfo.InvariantCulture, $"{Lines[index].Name}={State(blocks[index].BaseObject)}\n");
        }

        return report.Append("done\n").ToString();
    }

    /// <summary><c>EnableScriptBlockLogging</c> of the setting the engine works with: <c>null</c>, <c>True</c> or <c>False</c>.</summary>
    private static string Setting(Assembly engine)
    {
        var utils = Required(engine.GetType("System.Management.Automation.Utils"), "Utils");
        var type = Required(engine.GetType("System.Management.Automation.Configuration.ScriptBlockLogging"), "ScriptBlockLogging");
        var order = Required(utils.GetField("SystemWideThenCurrentUserConfig", InternalStatic), "Utils.SystemWideThenCurrentUserConfig").GetValue(null);
        var read = Required(utils.GetMethod("GetPolicySetting", InternalStatic), "Utils.GetPolicySetting").MakeGenericMethod(type);
        var setting = read.Invoke(null, [order]);
        var enabled = setting is null ? null : Required(type.GetProperty("EnableScriptBlockLogging"), "EnableScriptBlockLogging").GetValue(setting);
        return enabled?.ToString() ?? "null";
    }

    /// <summary>"logged" when the engine wrote the script block, "skipped" when it decided not to, "undecided" otherwise.</summary>
    private static string State(object block)
    {
        var scriptBlock = (ScriptBlock)block;
        bool Flag(string name) => (bool)Required(typeof(ScriptBlock).GetProperty(name, Internal), "ScriptBlock." + name).GetValue(scriptBlock)!;
        return Flag("HasLogged") ? "logged" : Flag("SkipLogging") ? "skipped" : "undecided";
    }

    private static T Required<T>(T? member, string name)
        where T : class =>
        member ?? throw new MissingMemberException("System.Management.Automation has no " + name + " any more.");
}
