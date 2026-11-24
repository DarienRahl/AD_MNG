using System.Runtime.CompilerServices;
using AdMng.PowerShell;

namespace AdMng.App;

/// <summary>
/// What the process sets before any of its code runs: the module initializer of the program runs before <c>Main</c>. The time limit of
/// a regular expression match (<see cref="PowerShellEnvironment.LimitRegularExpressions"/>) - .NET reads it when the first regular
/// expression of the process is made, so later is too late - keeps a pattern typed in the PowerShell window from holding the window and
/// a processor core (docs/dev/r1-powershell-window.md 4.2). It applies to every regular expression of the program that has no limit of
/// its own. Only <see cref="PowerShellEnvironment"/> is loaded here, not the engine.
/// </summary>
internal static class ProcessStart
{
    [ModuleInitializer]
    internal static void Initialize() => PowerShellEnvironment.LimitRegularExpressions();
}
