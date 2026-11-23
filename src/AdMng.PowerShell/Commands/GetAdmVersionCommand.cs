using System.Management.Automation;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Get-AdmVersion</c>: the program's and the engine's version, the language mode and the window's domain, DC and account.
/// It is the one command of R1 part 1 and it proves the mechanism end to end: the window's services reach a plain command
/// class through the window registry of its runspace, and a fake session in a test reaches it the same way.
/// </summary>
[Cmdlet(VerbsCommon.Get, "AdmVersion")]
[OutputType(typeof(AdmVersionInfo))]
public sealed class GetAdmVersionCommand : AdmCmdlet
{
    protected override void ProcessRecord()
    {
        var context = ContextOrNull;
        var info = context?.Reader.Info;
        WriteObject(new AdmVersionInfo(
            context?.ApplicationVersion ?? string.Empty,
            PSVersionInfo.PSVersion.ToString(),
            SessionState.LanguageMode == PSLanguageMode.FullLanguage ? PowerShellLanguageMode.Full : PowerShellLanguageMode.Constrained,
            info?.DomainDnsName,
            info?.Server,
            info?.BoundIdentity));
    }
}

/// <summary>The result of <c>Get-AdmVersion</c>. Values of the window's own connection, shown in the window and kept in memory only.</summary>
public sealed record AdmVersionInfo(
    string ApplicationVersion,
    string EngineVersion,
    PowerShellLanguageMode LanguageMode,
    string? Domain,
    string? DomainController,
    string? Account);
