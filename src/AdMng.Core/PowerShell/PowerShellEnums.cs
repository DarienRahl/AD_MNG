namespace AdMng.Core.PowerShell;

/// <summary>The language mode of a window's runspace (docs/dev/r1-powershell-window.md 4.7).</summary>
public enum PowerShellLanguageMode
{
    /// <summary>ConstrainedLanguage: the start mode of every window; AD Manager's commands work in it.</summary>
    Constrained = 0,

    /// <summary>FullLanguage ("trusted mode"): any .NET code. Only on the operator's request, never remembered.</summary>
    Full = 1,
}

/// <summary>Why the window may not switch to the trusted mode right now.</summary>
public enum PowerShellTrustedModeBlock
{
    /// <summary>Nothing blocks it.</summary>
    None = 0,

    /// <summary>A system lockdown (WDAC / AppLocker) enforces ConstrainedLanguage.</summary>
    SystemLockdown = 1,
}

/// <summary>The system lockdown state a probe reports (<c>SystemPolicy.GetSystemLockdownPolicy</c>).</summary>
public enum PowerShellLockdownState
{
    None = 0,

    /// <summary>Violations are only audited; the window is not forced into ConstrainedLanguage.</summary>
    Audit = 1,

    /// <summary>The policy is enforced: the window stays in ConstrainedLanguage and the switch is disabled.</summary>
    Enforce = 2,
}

/// <summary>The stream a piece of text belongs to (the information stream arrives as Output: the engine writes it through the host's Write).</summary>
public enum PowerShellStream
{
    Output = 0,
    Error = 1,
    Warning = 2,
    Verbose = 3,
    Debug = 4,
}

/// <summary>How a run ended.</summary>
public enum PowerShellRunState
{
    Completed = 0,

    /// <summary>The operator (or the token) stopped it.</summary>
    Stopped = 1,

    /// <summary>A terminating error ended it.</summary>
    Failed = 2,

    /// <summary>
    /// It was stopped and did not end within the grace period (it is stuck in .NET code that Stop cannot interrupt): the session left
    /// it to its thread and runs the next command in a new runspace, without the variables, functions and modules of the old one.
    /// </summary>
    Abandoned = 3,
}

/// <summary>What a prompt item asks for.</summary>
public enum PowerShellPromptKind
{
    /// <summary>A line of text (<c>Read-Host</c>, a missing mandatory parameter).</summary>
    Text = 0,

    /// <summary>A secret line (<c>Read-Host -AsSecureString</c>); answered with a <see cref="System.Security.SecureString"/>.</summary>
    Secret = 1,

    /// <summary>One of several choices (<c>-Confirm</c>, <c>ShouldContinue</c>).</summary>
    Choice = 2,
}

/// <summary>The kind of a completion match (file system kinds are never offered in R1).</summary>
public enum PowerShellCompletionKind
{
    Other = 0,
    Command = 1,
    Parameter = 2,
    ParameterValue = 3,
    Variable = 4,
    Property = 5,
    Method = 6,
    Type = 7,
    Keyword = 8,
    History = 9,
}

/// <summary>What a span of the input is, for highlighting.</summary>
public enum PowerShellSyntaxKind
{
    Other = 0,
    Command = 1,
    Parameter = 2,
    Variable = 3,
    String = 4,
    Number = 5,
    Operator = 6,
    Comment = 7,
    Keyword = 8,
    Type = 9,
    Member = 10,
}
