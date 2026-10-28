using System.Reflection;
using System.Runtime.InteropServices;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook.Tests;

/// <summary>The classification table of design note docs/dev/u5-outlook-correspondence.md 4.3.</summary>
public class OutlookErrorsTests
{
    private static COMException Com(uint hresult) => new("Nie można wysłać do jan.kowalski@contoso.test", unchecked((int)hresult));

    [Theory]
    [InlineData(0x8001010Au, "send", OutlookFailureKind.Busy, "outlook.busy")]
    [InlineData(0x80010001u, "subject", OutlookFailureKind.Busy, "outlook.busy")]
    [InlineData(0x800706BEu, "save", OutlookFailureKind.NotResponding, "outlook.notResponding")]
    [InlineData(0x80010105u, "body", OutlookFailureKind.NotResponding, "outlook.notResponding")]
    [InlineData(0x80004004u, "send", OutlookFailureKind.SecurityDenied, "outlook.denied")]
    [InlineData(0x80004004u, "recipients", OutlookFailureKind.SecurityDenied, "outlook.denied")]
    [InlineData(0x80004004u, "replyTo", OutlookFailureKind.SecurityDenied, "outlook.denied")]
    [InlineData(0x80004004u, "subject", OutlookFailureKind.Unknown, "outlook.error")]
    [InlineData(0x80010108u, "send", OutlookFailureKind.Disconnected, "outlook.disconnected")]
    [InlineData(0x800706BAu, "create", OutlookFailureKind.Disconnected, "outlook.disconnected")]
    [InlineData(0x800706BFu, "save", OutlookFailureKind.Disconnected, "outlook.disconnected")]
    [InlineData(0x8004060Cu, "send", OutlookFailureKind.ItemTooLarge, "outlook.tooLarge")]
    [InlineData(0x80040116u, "save", OutlookFailureKind.ItemTooLarge, "outlook.tooLarge")]
    [InlineData(0x80040305u, "attachments", OutlookFailureKind.ItemTooLarge, "outlook.tooLarge")]
    [InlineData(0x80040154u, "connect", OutlookFailureKind.NotInstalled, "outlook.notInstalled")]
    [InlineData(0x800401F3u, "connect", OutlookFailureKind.NotInstalled, "outlook.notInstalled")]
    [InlineData(0x80004005u, "send", OutlookFailureKind.Unknown, "outlook.error")]
    public void Classify_MapsTheHResultOfTheStep(uint hresult, string step, OutlookFailureKind kind, string code)
    {
        var failure = OutlookErrors.Classify(Com(hresult), step, isElevated: false);

        Assert.Equal(kind, failure.Kind);
        Assert.Equal(code, failure.Code);
        Assert.Equal(step, failure.Step);
        Assert.Equal(unchecked((int)hresult), failure.HResult);
    }

    [Theory]
    [InlineData(0x80080005u, true, "connect", OutlookFailureKind.Elevated)]
    [InlineData(0x800401E3u, true, "connect", OutlookFailureKind.Elevated)]
    [InlineData(0x80080005u, false, "connect", OutlookFailureKind.Unknown)]
    [InlineData(0x800401E3u, false, "connect", OutlookFailureKind.Unknown)]
    [InlineData(0x80080005u, true, "send", OutlookFailureKind.Unknown)]
    public void Classify_ElevatedOnlyWhileConnectingFromAnElevatedProcess(uint hresult, bool elevated, string step, OutlookFailureKind kind)
    {
        var failure = OutlookErrors.Classify(Com(hresult), step, elevated);

        Assert.Equal(kind, failure.Kind);
        Assert.Equal(kind == OutlookFailureKind.Elevated ? "outlook.elevated" : "outlook.error", failure.Code);
    }

    [Fact]
    public void Classify_Cancellation_IsCancelled()
    {
        var failure = OutlookErrors.Classify(new OperationCanceledException(), "send", isElevated: false);

        Assert.Equal(OutlookFailureKind.Cancelled, failure.Kind);
        Assert.Equal("outlook.cancelled", failure.Code);
        Assert.False(failure.StopsRun);
        Assert.False(failure.IsTransient);
    }

    [Fact]
    public void Classify_LooksThroughReflectionAndTaskWrappers()
    {
        var wrapped = new TargetInvocationException(new AggregateException(new TargetInvocationException(Com(0x80010108))));

        var failure = OutlookErrors.Classify(wrapped, "account", isElevated: false);

        Assert.Equal(OutlookFailureKind.Disconnected, failure.Kind);
        Assert.Equal("account", failure.Step);
    }

    [Fact]
    public void Classify_MissingFileAtTheAttachmentStep_IsAttachmentMissing_ElsewhereUnknown()
    {
        Assert.Equal(OutlookFailureKind.AttachmentMissing, OutlookErrors.Classify(new FileNotFoundException("C:\\Dane\\Łukasz.pdf"), "attachments", false).Kind);
        Assert.Equal(OutlookFailureKind.Unknown, OutlookErrors.Classify(new FileNotFoundException("x"), "images", false).Kind);
    }

    [Fact]
    public void Classify_PlatformNotSupported_IsNotSupported()
    {
        var failure = OutlookErrors.Classify(new PlatformNotSupportedException(), "connect", false);

        Assert.Equal(OutlookFailureKind.NotSupported, failure.Kind);
        Assert.Equal("outlook.notSupported", failure.Code);
        Assert.True(failure.StopsRun);
    }

    [Theory]
    [InlineData(OutlookFailureKind.RecipientUnresolved, "recipients", "outlook.recipient")]
    [InlineData(OutlookFailureKind.AttachmentMissing, "attachments", "outlook.attachment")]
    [InlineData(OutlookFailureKind.InvalidAccount, "account", "outlook.account")]
    public void Classify_StepFailuresOfTheBuilder_KeepTheirKindAndStep(OutlookFailureKind kind, string step, string code)
    {
        var failure = OutlookErrors.Classify(new OutlookErrors.OutlookStepException(kind, step), "other", false);

        Assert.Equal(kind, failure.Kind);
        Assert.Equal(code, failure.Code);
        Assert.Equal(step, failure.Step);
        Assert.Null(failure.HResult);
    }

    [Fact]
    public void Classify_AnOutlookException_KeepsItsFailure()
    {
        var inner = OutlookErrors.Failure(OutlookFailureKind.Busy, "render", unchecked((int)0x8001010A));

        Assert.Same(inner, OutlookErrors.Classify(new OutlookException(inner), "diagnose", false));
        Assert.Equal("diagnose", OutlookErrors.Classify(new OutlookException(inner with { Step = null }), "diagnose", false).Step);
    }

    [Fact]
    public void Classify_UnknownClrException_KeepsTheHResultForTheReport()
    {
        var failure = OutlookErrors.Classify(new InvalidOperationException("Brak dostępu do skrzynki anna.nowak@contoso.test"), "save", false);

        Assert.Equal(OutlookFailureKind.Unknown, failure.Kind);
        Assert.Equal("outlook.error", failure.Code);
        Assert.Equal(new InvalidOperationException().HResult, failure.HResult);
    }

    [Fact]
    public void Failure_NeverCarriesTheExceptionMessage()
    {
        var failure = OutlookErrors.Classify(Com(0x80004005), "send", false);

        Assert.DoesNotContain("contoso", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("jan.kowalski", new OutlookException(failure).ToString(), StringComparison.Ordinal);
        Assert.Null(new OutlookException(failure).InnerException);
        Assert.Equal(failure.HResult, new OutlookException(failure).HResult);
    }

    [Fact]
    public void Codes_AreTheTableOfTheDesignNote_AndDistinct()
    {
        Assert.Equal(
            ["outlook.notSupported", "outlook.notInstalled", "outlook.elevated", "outlook.busy", "outlook.notResponding", "outlook.denied",
             "outlook.disconnected", "outlook.recipient", "outlook.attachment", "outlook.account", "outlook.tooLarge", "outlook.cancelled",
             "outlook.error"],
            OutlookErrors.AllCodes);
        foreach (var kind in Enum.GetValues<OutlookFailureKind>())
        {
            Assert.Equal(Testing.FakeOutlookClient.Code(kind), OutlookErrors.Code(kind));
        }
    }

    [Theory]
    [InlineData(OutlookFailureKind.NotInstalled, true, false)]
    [InlineData(OutlookFailureKind.Elevated, true, false)]
    [InlineData(OutlookFailureKind.SecurityDenied, true, false)]
    [InlineData(OutlookFailureKind.Disconnected, true, false)]
    [InlineData(OutlookFailureKind.NotSupported, true, false)]
    [InlineData(OutlookFailureKind.Busy, false, true)]
    [InlineData(OutlookFailureKind.NotResponding, false, true)]
    [InlineData(OutlookFailureKind.RecipientUnresolved, false, false)]
    [InlineData(OutlookFailureKind.Unknown, false, false)]
    public void Failure_StopsRunAndTransient(OutlookFailureKind kind, bool stopsRun, bool transient)
    {
        var failure = OutlookErrors.Failure(kind);

        Assert.Equal(stopsRun, failure.StopsRun);
        Assert.Equal(transient, failure.IsTransient);
    }
}
