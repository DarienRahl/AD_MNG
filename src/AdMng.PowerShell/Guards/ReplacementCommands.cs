using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Internal;
using AdMng.PowerShell.Hosting;
using Microsoft.PowerShell.Commands;

namespace AdMng.PowerShell.Guards;

/// <summary>
/// A command of the window that runs one engine cmdlet with fewer parameters (<see cref="CommandGuards.Replacements"/>). The engine's
/// cmdlet is not in the session under any name; this command declares only the parameters the window allows and passes what it was
/// given to the cmdlet through a steppable pipeline built from the window's bridge (<see cref="WindowEntry.Bridge"/>). The cmdlet runs
/// in the caller's language mode (a FullLanguage proxy function would not: <c>ForEach-Object -MemberName</c> could then call any .NET
/// method from ConstrainedLanguage), in the caller's scope (a script block of ForEach-Object assigns the caller's variables), and a
/// stop of the window's pipeline stops it.
/// </summary>
internal abstract class ReplacementCommand : PSCmdlet, IDisposable
{
    /// <summary>Common parameters the outer command handles itself (its own output and error variables).</summary>
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "InputObject", "OutVariable", "OutBuffer", "ErrorVariable", "WarningVariable", "InformationVariable", "PipelineVariable",
    };

    private SteppablePipeline? _pipeline;

    /// <summary>The engine's cmdlet this command runs.</summary>
    protected abstract CmdletInfo Engine { get; }

    /// <summary>Whether the engine's cmdlet takes pipeline input; the object to pass is then <see cref="Input"/>.</summary>
    protected virtual bool TakesInput => false;

    /// <summary>The object of the current record for a cmdlet that takes pipeline input.</summary>
    protected virtual object? Input => null;

    /// <summary>A refusal of what was asked, written as an error before the engine's cmdlet starts; null when the call is allowed.</summary>
    protected virtual string? Refusal() => null;

    public void Dispose()
    {
        _pipeline?.Dispose();
        _pipeline = null;
    }

    protected override void BeginProcessing()
    {
        if (Refusal() is { } message)
        {
            WriteError(new ErrorRecord(new PSNotSupportedException(message), "AdmCommandNotAvailable", ErrorCategory.PermissionDenied, null)
            {
                CategoryInfo = { Activity = MyInvocation.MyCommand.Name },
            });
            return;
        }

        var window = WindowRegistry.Current()
            ?? throw new PSInvalidOperationException(MyInvocation.MyCommand.Name + " of the AD Manager PowerShell window runs only in a session the window created.");
        var parameters = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in MyInvocation.BoundParameters)
        {
            if (!NotForwarded.Contains(name))
            {
                parameters[name] = value;
            }
        }

        _pipeline = window.Bridge.GetSteppablePipeline(CommandOrigin.Internal, [Engine, parameters]);
        _pipeline.Begin(this);
    }

    protected override void ProcessRecord()
    {
        if (_pipeline is null)
        {
            return;
        }

        if (TakesInput)
        {
            _pipeline.Process(Input);
        }
        else
        {
            _pipeline.Process();
        }
    }

    protected override void EndProcessing() => _pipeline?.End();
}

/// <summary>
/// <c>ForEach-Object</c> without <c>-Parallel</c> (its script blocks would run in runspaces of their own, in FullLanguage whatever the
/// window's mode, and reach the process - audit PS-01), <c>-AsJob</c> (a job outlives its command), <c>-ThrottleLimit</c>,
/// <c>-TimeoutSeconds</c> and <c>-UseNewRunspace</c>. A typed <c>-Parallel</c> is refused with a message by <see cref="InputGuard"/>.
/// </summary>
[Cmdlet("ForEach", "Object", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Low, DefaultParameterSetName = ScriptBlockSet)]
internal sealed class WindowForEachObjectCommand : ReplacementCommand
{
    private const string ScriptBlockSet = "ScriptBlockSet";
    private const string MemberSet = "PropertyAndMethodSet";
    private static readonly CmdletInfo EngineCmdlet = new("ForEach-Object", typeof(ForEachObjectCommand));

    [Parameter(ValueFromPipeline = true, ParameterSetName = ScriptBlockSet)]
    [Parameter(ValueFromPipeline = true, ParameterSetName = MemberSet)]
    public PSObject InputObject { get; set; } = AutomationNull.Value;

    [Parameter(ParameterSetName = ScriptBlockSet)]
    public ScriptBlock? Begin { get; set; }

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = ScriptBlockSet)]
    [AllowNull]
    [AllowEmptyCollection]
    public ScriptBlock[]? Process { get; set; }

    [Parameter(ParameterSetName = ScriptBlockSet)]
    public ScriptBlock? End { get; set; }

    [Parameter(ParameterSetName = ScriptBlockSet, ValueFromRemainingArguments = true)]
    [AllowNull]
    [AllowEmptyCollection]
    public ScriptBlock[]? RemainingScripts { get; set; }

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = MemberSet)]
    [ValidateTrustedData]
    [ValidateNotNullOrEmpty]
    public string? MemberName { get; set; }

    [Parameter(ParameterSetName = MemberSet, ValueFromRemainingArguments = true)]
    [ValidateTrustedData]
    [Alias("Args")]
    public object[]? ArgumentList { get; set; }

    protected override CmdletInfo Engine => EngineCmdlet;

    protected override bool TakesInput => true;

    protected override object? Input => InputObject;
}

/// <summary><c>Tee-Object</c> into a variable only: no <c>-FilePath</c>, <c>-LiteralPath</c>, <c>-Append</c> or <c>-Encoding</c>.</summary>
[Cmdlet("Tee", "Object", DefaultParameterSetName = "Variable")]
internal sealed class WindowTeeObjectCommand : ReplacementCommand
{
    private static readonly CmdletInfo EngineCmdlet = new("Tee-Object", typeof(TeeObjectCommand));

    [Parameter(ValueFromPipeline = true)]
    public PSObject InputObject { get; set; } = AutomationNull.Value;

    [Parameter(Mandatory = true, ParameterSetName = "Variable")]
    public string? Variable { get; set; }

    protected override CmdletInfo Engine => EngineCmdlet;

    protected override bool TakesInput => true;

    protected override object? Input => InputObject;
}

/// <summary><c>New-Object</c> of a .NET type only: no <c>-ComObject</c> (a COM object such as Scripting.FileSystemObject writes files).</summary>
[Cmdlet(VerbsCommon.New, "Object", DefaultParameterSetName = "Net")]
internal sealed class WindowNewObjectCommand : ReplacementCommand
{
    private static readonly CmdletInfo EngineCmdlet = new("New-Object", typeof(NewObjectCommand));

    [Parameter(ParameterSetName = "Net", Mandatory = true, Position = 0)]
    [ValidateTrustedData]
    public string? TypeName { get; set; }

    [Parameter(ParameterSetName = "Net", Position = 1)]
    [ValidateTrustedData]
    [Alias("Args")]
    public object[]? ArgumentList { get; set; }

    [Parameter]
    [ValidateTrustedData]
    public IDictionary? Property { get; set; }

    protected override CmdletInfo Engine => EngineCmdlet;
}

/// <summary>
/// <c>Start-Transcript</c> only to a file the operator names with <c>-Path</c> or <c>-LiteralPath</c> (the README's promise, CLAUDE.md
/// rule 1): without one the engine writes the commands and the directory values they showed to a default file in the home folder, so
/// that call is refused; <c>-OutputDirectory</c> (a file name the engine makes up) is not a parameter.
/// </summary>
[Cmdlet(VerbsLifecycle.Start, "Transcript", SupportsShouldProcess = true, DefaultParameterSetName = "ByPath")]
internal sealed class WindowStartTranscriptCommand : ReplacementCommand
{
    internal const string NoPathMessage =
        "Start-Transcript writes only to a file you name in the AD Manager PowerShell window: use Start-Transcript -Path <file>. Without it the engine would write the commands and what they showed to a default file. Nothing is recorded.";

    private static readonly CmdletInfo EngineCmdlet = new("Start-Transcript", typeof(StartTranscriptCommand));

    [Parameter(Position = 0, ParameterSetName = "ByPath")]
    [ValidateNotNullOrEmpty]
    public string? Path { get; set; }

    [Parameter(Position = 0, ParameterSetName = "ByLiteralPath")]
    [Alias("PSPath", "LP")]
    [ValidateNotNullOrEmpty]
    public string? LiteralPath { get; set; }

    [Parameter]
    public SwitchParameter Append { get; set; }

    [Parameter]
    public SwitchParameter Force { get; set; }

    [Parameter]
    [Alias("NoOverwrite")]
    public SwitchParameter NoClobber { get; set; }

    [Parameter]
    public SwitchParameter IncludeInvocationHeader { get; set; }

    [Parameter]
    public SwitchParameter UseMinimalHeader { get; set; }

    protected override CmdletInfo Engine => EngineCmdlet;

    protected override string? Refusal() => string.IsNullOrEmpty(Path) && string.IsNullOrEmpty(LiteralPath) ? NoPathMessage : null;
}
