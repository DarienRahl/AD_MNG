using System.Reflection;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// The stable step ids of <see cref="OutlookFailure.Step"/> (design note docs/dev/u5-outlook-correspondence.md 4.2): the
/// part of building, sending or reading an item that failed. Never a value (no address, subject or file name).
/// </summary>
internal static class OutlookSteps
{
    public const string Connect = "connect";
    public const string Create = "create";
    public const string Account = "account";
    public const string OnBehalf = "onBehalf";
    public const string Recipients = "recipients";
    public const string ReplyTo = "replyTo";
    public const string Subject = "subject";
    public const string Body = "body";
    public const string Receipt = "receipt";
    public const string Defer = "defer";
    public const string Voting = "voting";
    public const string Importance = "importance";
    public const string Properties = "properties";
    public const string Images = "images";
    public const string Attachments = "attachments";
    public const string Save = "save";
    public const string Inline = "inline";
    public const string Send = "send";
    public const string Render = "render";
    public const string Delete = "delete";
    public const string Diagnose = "diagnose";
    public const string TestItem = "testItem";
    public const string Campaigns = "campaigns";
    public const string Responses = "responses";
}

/// <summary>
/// HRESULT / exception → <see cref="OutlookFailure"/> (design note 4.3). Pure, tested on every platform. Exception messages
/// are never kept (they may contain addresses): only the kind, the code, the step and the HRESULT.
/// </summary>
internal static class OutlookErrors
{
    // HRESULTs of the table of design note 4.3 (and the registration errors of a missing classic Outlook).
    public const int CoEServerExecFailure = unchecked((int)0x80080005);
    public const int MkEUnavailable = unchecked((int)0x800401E3);
    public const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);
    public const int RpcECallRejected = unchecked((int)0x80010001);
    public const int RpcSCallFailed = unchecked((int)0x800706BE);
    public const int RpcEServerFault = unchecked((int)0x80010105);
    public const int EAbort = unchecked((int)0x80004004);
    public const int RpcEDisconnected = unchecked((int)0x80010108);
    public const int RpcSServerUnavailable = unchecked((int)0x800706BA);
    public const int RpcSCallFailedDne = unchecked((int)0x800706BF);
    public const int MapiEStoreFull = unchecked((int)0x8004060C);
    public const int MapiETooBig = unchecked((int)0x80040305);
    public const int MapiEMessageTooBig = unchecked((int)0x80040116);
    public const int RegDbEClassNotReg = unchecked((int)0x80040154);
    public const int CoEClassString = unchecked((int)0x800401F3);

    /// <summary>The code of a kind: <c>outlook.&lt;kind&gt;</c> (design note 4.3, <see cref="OutlookFailureCodes"/>).</summary>
    public static string Code(OutlookFailureKind kind) => OutlookFailureCodes.Of(kind);

    /// <summary>Every failure code (localization of the failure texts): <see cref="OutlookFailureCodes.Failures"/>.</summary>
    public static IReadOnlyList<string> AllCodes => OutlookFailureCodes.Failures;

    /// <summary>A failure of <paramref name="kind"/> at <paramref name="step"/>.</summary>
    public static OutlookFailure Failure(OutlookFailureKind kind, string? step = null, int? hresult = null) =>
        new(kind, Code(kind), step, hresult);

    /// <summary>Classifies <paramref name="exception"/> thrown at <paramref name="step"/>; the process elevation is read
    /// from <see cref="Environment.IsPrivilegedProcess"/>.</summary>
    public static OutlookFailure Classify(Exception exception, string? step) => Classify(exception, step, Environment.IsPrivilegedProcess);

    /// <summary>Classifies <paramref name="exception"/> thrown at <paramref name="step"/> (design note 4.3).</summary>
    /// <param name="exception">The exception (wrappers of reflection and tasks are looked through).</param>
    /// <param name="step">The step id (<see cref="OutlookSteps"/>), or null.</param>
    /// <param name="isElevated">The process runs elevated (decides <see cref="OutlookFailureKind.Elevated"/> while connecting).</param>
    public static OutlookFailure Classify(Exception exception, string? step, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inner = Unwrap(exception);
        switch (inner)
        {
            case OutlookStepException stepFailure:
                return Failure(stepFailure.Kind, stepFailure.Step);
            case OutlookException outlook:
                return outlook.Failure.Step is null && step is not null ? outlook.Failure with { Step = step } : outlook.Failure;
            case OperationCanceledException:
                return Failure(OutlookFailureKind.Cancelled, step);
            case PlatformNotSupportedException:
                return Failure(OutlookFailureKind.NotSupported, step);
            case FileNotFoundException or DirectoryNotFoundException when step == OutlookSteps.Attachments:
                return Failure(OutlookFailureKind.AttachmentMissing, step);
        }

        var hresult = inner.HResult;
        var kind = hresult switch
        {
            RegDbEClassNotReg or CoEClassString => OutlookFailureKind.NotInstalled,
            CoEServerExecFailure or MkEUnavailable when isElevated && step == OutlookSteps.Connect => OutlookFailureKind.Elevated,
            RpcEServerCallRetryLater or RpcECallRejected => OutlookFailureKind.Busy,
            RpcSCallFailed or RpcEServerFault => OutlookFailureKind.NotResponding,
            EAbort when step is OutlookSteps.Send or OutlookSteps.Recipients or OutlookSteps.ReplyTo => OutlookFailureKind.SecurityDenied,
            RpcEDisconnected or RpcSServerUnavailable or RpcSCallFailedDne => OutlookFailureKind.Disconnected,
            MapiEStoreFull or MapiETooBig or MapiEMessageTooBig => OutlookFailureKind.ItemTooLarge,
            _ => OutlookFailureKind.Unknown,
        };

        return Failure(kind, step, hresult == 0 ? null : hresult);
    }

    /// <summary>The exception behind reflection (<c>InvokeMember</c>) and task wrappers.</summary>
    internal static Exception Unwrap(Exception exception)
    {
        var current = exception;
        while (true)
        {
            switch (current)
            {
                case TargetInvocationException { InnerException: { } invoked }:
                    current = invoked;
                    continue;
                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    current = aggregate.InnerExceptions[0];
                    continue;
                default:
                    return current;
            }
        }
    }

    /// <summary>A failure the item builder decides itself (unresolved recipient, missing attachment, unknown account).</summary>
    internal sealed class OutlookStepException(OutlookFailureKind kind, string step) : Exception($"Outlook step {step} failed ({kind}).")
    {
        public OutlookFailureKind Kind { get; } = kind;

        public string Step { get; } = step;
    }
}
