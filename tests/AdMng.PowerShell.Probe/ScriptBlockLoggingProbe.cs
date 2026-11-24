using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace AdMng.PowerShell.Probe;

/// <summary>
/// The mode <c>script-block-logging:&lt;file&gt;</c>: what the engine does with script block logging when its all-users
/// <c>powershell.config.json</c> is <c>&lt;file&gt;</c> (decision D-B, audit PS-04). The file is set the way <c>pwsh -SettingsFile</c>
/// sets it (<c>PowerShellConfig.SetSystemConfigFilePath</c>), before the engine reads any configuration of this process. The report
/// says the setting the engine works with (<c>Utils.GetPolicySetting&lt;ScriptBlockLogging&gt;</c>: Group Policy, then the all-users
/// file, then the user's - <c>null</c> when none sets it) and, for a script block with a word of the engine's list of suspicious
/// content and an ordinary one, whether the engine wrote it to its log (<c>HasLogged</c>) or decided not to (<c>SkipLogging</c>).
/// The members are internal to <c>System.Management.Automation</c> 7.6.10 (<c>CompiledScriptBlock.cs</c>, <c>Utils.cs</c>,
/// <c>PSConfiguration.cs</c>); a later engine that renames one fails here, loudly, and the docs that rely on it are read again.
/// </summary>
internal static class ScriptBlockLoggingProbe
{
    /// <summary>An operator's line with "Properties" in it - a word of the engine's list (<c>SuspiciousContentChecker</c>).</summary>
    public const string Suspicious = "$properties = 'mail', 'department'";

    /// <summary>The same line without a word of the list.</summary>
    public const string Ordinary = "$attributes = 'mail', 'department'";

    private const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags InternalStatic = BindingFlags.NonPublic | BindingFlags.Static;

    public static string Run(string allUsersFile)
    {
        PowerShellEnvironment.Apply();
        var engine = typeof(PSObject).Assembly;
        var config = Required(engine.GetType("System.Management.Automation.Configuration.PowerShellConfig"), "PowerShellConfig");
        var instance = Required(config.GetField("Instance", InternalStatic), "PowerShellConfig.Instance").GetValue(null);
        Required(config.GetMethod("SetSystemConfigFilePath", Internal), "PowerShellConfig.SetSystemConfigFilePath").Invoke(instance, [Path.GetFullPath(allUsersFile)]);

        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var ps = SmaPowerShell.Create(runspace);
        var blocks = ps.AddScript(
            $"$s = [scriptblock]::Create('{Suspicious.Replace("'", "''", StringComparison.Ordinal)}'); & $s; "
            + $"$o = [scriptblock]::Create('{Ordinary.Replace("'", "''", StringComparison.Ordinal)}'); & $o; $s; $o").Invoke();
        if (ps.HadErrors || blocks.Count != 2)
        {
            throw new InvalidOperationException("The script blocks did not run: " + string.Join("; ", ps.Streams.Error.Select(error => error.ToString())));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"setting={Setting(engine)}\nsuspicious={State(blocks[0].BaseObject)}\nordinary={State(blocks[1].BaseObject)}\ndone\n");
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
