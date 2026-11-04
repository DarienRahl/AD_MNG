using AdMng.Core.EventLogs;
using AdMng.Testing.EventLogs;

namespace AdMng.Core.Tests.EventLogs;

public class EventCodesTests
{
    [Theory]
    [InlineData("0xC000006A", 0xC000006AL)]
    [InlineData("0xc000006a", 0xC000006AL)]
    [InlineData("0Xc000006A", 0xC000006AL)]
    [InlineData("C000006A", 0xC000006AL)]
    [InlineData(" 0x18 ", 0x18L)]
    [InlineData("0x0", 0L)]
    [InlineData("%%2313", 2313L)]
    [InlineData("10", 10L)]
    [InlineData("0xffffffff", 0xFFFFFFFFL)]
    [InlineData("0x8020000000000000", unchecked((long)0x8020000000000000UL))]
    public void TryParseCode(string value, long expected)
    {
        Assert.True(EventCodes.TryParseCode(value, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("0x")]
    [InlineData("%%")]
    [InlineData("%%23x")]
    [InlineData("0xZZ")]
    [InlineData("błąd")]
    [InlineData("0x10000000000000000")]
    [InlineData("-5")]
    public void TryParseCode_Invalid(string? value) => Assert.False(EventCodes.TryParseCode(value, out _));

    [Theory]
    [InlineData("0xC0000064", EventCodeCategory.UnknownAccount, "EventCode.NtStatus.C0000064")]
    [InlineData("0xc000006a", EventCodeCategory.BadPassword, "EventCode.NtStatus.C000006A")]
    [InlineData("0xC000006D", EventCodeCategory.Other, "EventCode.NtStatus.C000006D")]
    [InlineData("0xC000006E", EventCodeCategory.LogonRestriction, "EventCode.NtStatus.C000006E")]
    [InlineData("0xC000006F", EventCodeCategory.LogonRestriction, "EventCode.NtStatus.C000006F")]
    [InlineData("0xC0000070", EventCodeCategory.LogonRestriction, "EventCode.NtStatus.C0000070")]
    [InlineData("0xC0000071", EventCodeCategory.PasswordExpired, "EventCode.NtStatus.C0000071")]
    [InlineData("0xC0000072", EventCodeCategory.AccountDisabled, "EventCode.NtStatus.C0000072")]
    [InlineData("0xC0000133", EventCodeCategory.ClockSkew, "EventCode.NtStatus.C0000133")]
    [InlineData("0xC000015B", EventCodeCategory.LogonRestriction, "EventCode.NtStatus.C000015B")]
    [InlineData("0xC000018C", EventCodeCategory.TrustFailure, "EventCode.NtStatus.C000018C")]
    [InlineData("0xC000018D", EventCodeCategory.TrustFailure, "EventCode.NtStatus.C000018D")]
    [InlineData("0xC0000192", EventCodeCategory.TrustFailure, "EventCode.NtStatus.C0000192")]
    [InlineData("0xC0000193", EventCodeCategory.AccountExpired, "EventCode.NtStatus.C0000193")]
    [InlineData("0xC0000224", EventCodeCategory.PasswordMustChange, "EventCode.NtStatus.C0000224")]
    [InlineData("0xC0000234", EventCodeCategory.AccountLockedOut, "EventCode.NtStatus.C0000234")]
    [InlineData("0xC0000371", EventCodeCategory.Other, "EventCode.NtStatus.C0000371")]
    [InlineData("0xC0000380", EventCodeCategory.Other, "EventCode.NtStatus.C0000380")]
    [InlineData("0xC0000413", EventCodeCategory.LogonRestriction, "EventCode.NtStatus.C0000413")]
    [InlineData("0x0", EventCodeCategory.Success, "EventCode.NtStatus.00000000")]
    public void NtStatusCatalog(string value, EventCodeCategory category, string key)
    {
        var code = EventCodes.Find(EventCodeKind.NtStatus, value)!;

        Assert.Equal(category, code.Category);
        Assert.Equal(key, code.Key);
        Assert.True(code.IsKnown);
        Assert.Equal(category != EventCodeCategory.Success, code.IsFailure);
        Assert.Equal(key, code.ToString());
    }

    [Theory]
    [InlineData("0x6", EventCodeCategory.UnknownAccount, "EventCode.KerberosResult.0x6")]
    [InlineData("0x7", EventCodeCategory.Other, "EventCode.KerberosResult.0x7")]
    [InlineData("0xC", EventCodeCategory.LogonRestriction, "EventCode.KerberosResult.0xC")]
    [InlineData("0xd", EventCodeCategory.Other, "EventCode.KerberosResult.0xD")]
    [InlineData("0xE", EventCodeCategory.Other, "EventCode.KerberosResult.0xE")]
    [InlineData("0x12", EventCodeCategory.AccountRevoked, "EventCode.KerberosResult.0x12")]
    [InlineData("0x17", EventCodeCategory.PasswordExpired, "EventCode.KerberosResult.0x17")]
    [InlineData("0x18", EventCodeCategory.BadPassword, "EventCode.KerberosResult.0x18")]
    [InlineData("0x1F", EventCodeCategory.Other, "EventCode.KerberosResult.0x1F")]
    [InlineData("0x20", EventCodeCategory.Other, "EventCode.KerberosResult.0x20")]
    [InlineData("0x25", EventCodeCategory.ClockSkew, "EventCode.KerberosResult.0x25")]
    [InlineData("0x0", EventCodeCategory.Success, "EventCode.KerberosResult.0x0")]
    public void KerberosResultCatalog(string value, EventCodeCategory category, string key)
    {
        var code = EventCodes.Find(EventCodeKind.KerberosResult, value)!;

        Assert.Equal(category, code.Category);
        Assert.Equal(key, code.Key);
    }

    [Theory]
    [InlineData("0x1", EventCodeCategory.WeakEncryption, "EventCode.EncryptionType.0x1")]
    [InlineData("0x3", EventCodeCategory.WeakEncryption, "EventCode.EncryptionType.0x3")]
    [InlineData("0x11", EventCodeCategory.None, "EventCode.EncryptionType.0x11")]
    [InlineData("0x12", EventCodeCategory.None, "EventCode.EncryptionType.0x12")]
    [InlineData("0x17", EventCodeCategory.WeakEncryption, "EventCode.EncryptionType.0x17")]
    [InlineData("0x18", EventCodeCategory.WeakEncryption, "EventCode.EncryptionType.0x18")]
    [InlineData("0xffffffff", EventCodeCategory.None, "EventCode.EncryptionType.0xFFFFFFFF")]
    public void EncryptionTypeCatalog(string value, EventCodeCategory category, string key)
    {
        var code = EventCodes.Find(EventCodeKind.KerberosEncryptionType, value)!;

        Assert.Equal(category, code.Category);
        Assert.Equal(key, code.Key);
        Assert.False(code.IsFailure);
    }

    [Fact]
    public void LogonTypes_TwoToThirteenWithoutSix()
    {
        Assert.Equal([2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13], EventCodes.LogonTypes.Select(c => (int)c.Value));
        Assert.Equal("EventCode.LogonType.10", EventCodes.Find(EventCodeKind.LogonType, "10")!.Key);
        Assert.Equal("10", EventCodes.Find(EventCodeKind.LogonType, 10)!.Display);
        Assert.Null(EventCodes.Find(EventCodeKind.LogonType, "6"));
    }

    [Fact]
    public void PreAuthTypes_ZeroIsNoPreAuthentication()
    {
        Assert.Equal(EventCodeCategory.NoPreAuthentication, EventCodes.Find(EventCodeKind.KerberosPreAuthType, "0")!.Category);
        Assert.Equal("EventCode.PreAuthType.2", EventCodes.Find(EventCodeKind.KerberosPreAuthType, "2")!.Key);
        Assert.Contains(EventCodes.KerberosPreAuthTypes, c => c.Value == 138);
    }

    [Fact]
    public void FailureReasons_OfThe4625()
    {
        Assert.Equal(Enumerable.Range(2304, 10).Select(v => (long)v), EventCodes.FailureReasons.Select(c => c.Value));
        var reason = EventCodes.Find(EventCodeKind.FailureReason, "%%2307")!;
        Assert.Equal(EventCodeCategory.AccountLockedOut, reason.Category);
        Assert.Equal("EventCode.FailureReason.2307", reason.Key);
        Assert.Equal("%%2307", reason.Display);
    }

    [Fact]
    public void CredentialValidationStatuses_AreNtStatusEntries()
    {
        Assert.All(EventCodes.CredentialValidationStatuses, c => Assert.Contains(c, EventCodes.NtStatuses));
        Assert.Contains(EventCodes.CredentialValidationStatuses, c => c.Value == EventCodes.StatusWrongPassword);
        Assert.Contains(EventCodes.CredentialValidationStatuses, c => c.Value == EventCodes.StatusAccountLockedOut);
    }

    [Fact]
    public void EveryKeyIsUniqueAndStable()
    {
        var keys = EventCodes.All.Select(c => c.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.StartsWith(EventCodes.KeyPrefix, key, StringComparison.Ordinal));
        Assert.All(keys, key => Assert.Matches("^EventCode\\.[A-Za-z]+\\.[0-9A-Fx]+$", key));
        Assert.Equal(EventCodes.NtStatuses.Count + EventCodes.KerberosResults.Count + EventCodes.KerberosEncryptionTypes.Count
            + EventCodes.KerberosPreAuthTypes.Count + EventCodes.LogonTypes.Count + EventCodes.FailureReasons.Count, EventCodes.All.Count);
    }

    [Theory]
    [InlineData(EventCodeKind.NtStatus, 0xC0000064L, "0xC0000064")]
    [InlineData(EventCodeKind.KerberosResult, 0x6L, "0x6")]
    [InlineData(EventCodeKind.KerberosEncryptionType, 0x12L, "0x12")]
    [InlineData(EventCodeKind.KerberosPreAuthType, 2L, "2")]
    [InlineData(EventCodeKind.LogonType, 3L, "3")]
    [InlineData(EventCodeKind.FailureReason, 2313L, "%%2313")]
    public void Format(EventCodeKind kind, long value, string expected) => Assert.Equal(expected, EventCodes.Format(kind, value));

    [Fact]
    public void Describe_UnknownCodes()
    {
        var unknown = EventCodes.Describe(EventCodeKind.NtStatus, "0xC0000999")!;

        Assert.False(unknown.IsKnown);
        Assert.Equal(EventCodeCategory.Other, unknown.Category);
        Assert.True(unknown.IsFailure);
        Assert.Equal("EventCode.NtStatus.C0000999", unknown.Key);
        Assert.Equal(EventCodeCategory.None, EventCodes.Describe(EventCodeKind.LogonType, 99).Category);
        Assert.Null(EventCodes.Describe(EventCodeKind.NtStatus, "-"));
        Assert.Same(EventCodes.Find(EventCodeKind.NtStatus, 0xC000006A), EventCodes.Describe(EventCodeKind.NtStatus, 0xC000006A));
    }

    [Fact]
    public void ResultOf_4625UsesTheSubStatusOfTheGenericStatus()
    {
        var badPassword = EventLogSamples.LogonFailed(EventLogSamples.Dc01, 1, EventLogSamples.Morning, EventLogSamples.ZofiaZak, "10.20.5.40", "KRK-LT001");
        var unknownUser = EventLogSamples.LogonFailed(EventLogSamples.Dc01, 2, EventLogSamples.Morning, "admin", "10.10.0.15", null, subStatus: "0xc0000064");
        var locked = EventLogSamples.LogonFailed(EventLogSamples.Dc01, 3, EventLogSamples.Morning, EventLogSamples.JanKowalski, null, "WAW-PC001",
            status: "0xc0000234", subStatus: "0x0", failureReason: "%%2307");

        Assert.Equal(EventCodeCategory.BadPassword, EventCodes.ResultOf(badPassword)!.Category);
        Assert.Equal(EventCodeCategory.UnknownAccount, EventCodes.ResultOf(unknownUser)!.Category);
        Assert.Equal(EventCodes.StatusAccountLockedOut, EventCodes.ResultOf(locked)!.Value);
    }

    [Fact]
    public void ResultOf_KerberosAndNtlmRecords()
    {
        var at = EventLogSamples.Morning;
        Assert.Equal(EventCodeCategory.BadPassword,
            EventCodes.ResultOf(EventLogSamples.KerberosPreAuthFailed(EventLogSamples.Dc01, 1, at, EventLogSamples.JanKowalski, "10.10.1.21"))!.Category);
        Assert.Equal(EventCodeCategory.BadPassword,
            EventCodes.ResultOf(EventLogSamples.CredentialValidation(EventLogSamples.Dc01, 2, at, EventLogSamples.JanKowalski, "WAW-PC001"))!.Category);
        Assert.Equal(EventCodeCategory.Success,
            EventCodes.ResultOf(EventLogSamples.CredentialValidation(EventLogSamples.Dc01, 3, at, EventLogSamples.JanKowalski, "WAW-PC001", "0x0"))!.Category);
        Assert.Equal(EventCodeCategory.UnknownAccount,
            EventCodes.ResultOf(EventLogSamples.TicketGranted(EventLogSamples.Dc01, 4, at, "admin", "10.10.0.15", status: "0x6"))!.Category);
        Assert.Equal(EventCodeCategory.Success,
            EventCodes.ResultOf(EventLogSamples.TicketGranted(EventLogSamples.Dc01, 5, at, EventLogSamples.ZofiaZak, "10.20.5.40"))!.Category);
        Assert.Null(EventCodes.ResultOf(EventLogSamples.AccountLockedOut(EventLogSamples.Dc01, 6, at, EventLogSamples.JanKowalski, "WAW-PC001")));
    }

    [Theory]
    [InlineData("::ffff:10.20.5.40", "10.20.5.40")]
    [InlineData("::FFFF:10.20.5.40", "10.20.5.40")]
    [InlineData(" 10.10.1.21 ", "10.10.1.21")]
    [InlineData("fe80::1c2d:3e4f", "fe80::1c2d:3e4f")]
    [InlineData("::1", "::1")]
    [InlineData("-", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeIpAddress(string? value, string? expected) => Assert.Equal(expected, EventCodes.NormalizeIpAddress(value));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.10.1.21", false)]
    [InlineData("-", false)]
    [InlineData("WAW-PC001", false)]
    public void IsLocalAddress(string value, bool expected) => Assert.Equal(expected, EventCodes.IsLocalAddress(value));

    [Theory]
    [InlineData(@"\\WAW-PC001", "WAW-PC001")]
    [InlineData(" KRK-LT001 ", "KRK-LT001")]
    [InlineData("waw-pc001.contoso.test.", "waw-pc001.contoso.test")]
    [InlineData("-", null)]
    [InlineData(@"\\", null)]
    [InlineData(null, null)]
    public void NormalizeWorkstation(string? value, string? expected) => Assert.Equal(expected, EventCodes.NormalizeWorkstation(value));
}
