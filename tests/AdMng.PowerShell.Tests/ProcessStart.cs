using System.Runtime.CompilerServices;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// The test process starts the way the program does (src/AdMng.App/ProcessStart.cs): the time limit of regular expressions is set
/// before any of them is made, which only a module initializer of the entry assembly can promise (<see cref="RegexLimitTests"/>), and
/// a script block that trusted-mode code left behind cannot end the process (<see cref="EscapeTests"/>).
/// </summary>
internal static class ProcessStart
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        PowerShellEnvironment.LimitRegularExpressions();
        PowerShellEnvironment.ContainLeftoverScriptBlocks();
    }
}
