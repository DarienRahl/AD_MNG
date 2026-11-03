using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using static AdMng.Core.Tests.EventLogs.Analysis.SecurityRecords;

namespace AdMng.Core.Tests.EventLogs.Analysis;

/// <summary>The internal code classification and value normalization of the analyses.</summary>
public sealed class AnalysisCodesTests
{
    [Theory]
    [InlineData("0x18", 0x18u)]
    [InlineData("0xc000006a", 0xC000006Au)]
    [InlineData(" 0XC0000234 ", 0xC0000234u)]
    [InlineData("c0000064", 0xC0000064u)]
    [InlineData("0x0", 0u)]
    public void ParseCode_ReadsHexInAnyCase(string text, uint expected) => Assert.Equal(expected, AnalysisCodes.ParseCode(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%%2313")]
    [InlineData("0xZZ")]
    public void ParseCode_NullForNoNumber(string? text) => Assert.Null(AnalysisCodes.ParseCode(text));

    [Fact]
    public void FormatCode_UpperHexWithoutLeadingZeros()
    {
        Assert.Equal("0x18", AnalysisCodes.FormatCode(0x18));
        Assert.Equal("0xC000006A", AnalysisCodes.FormatCode(0xC000006A));
        Assert.Equal("0x0", AnalysisCodes.FormatCode(0));
    }

    [Theory]
    [InlineData(0xC000006Au, LogonFailureKind.BadPassword)]
    [InlineData(0xC0000064u, LogonFailureKind.UnknownAccount)]
    [InlineData(0xC0000234u, LogonFailureKind.AccountLocked)]
    [InlineData(0xC0000072u, LogonFailureKind.AccountDisabled)]
    [InlineData(0xC0000193u, LogonFailureKind.AccountExpired)]
    [InlineData(0xC0000071u, LogonFailureKind.PasswordExpired)]
    [InlineData(0xC0000224u, LogonFailureKind.PasswordMustChange)]
    [InlineData(0xC000006Fu, LogonFailureKind.LogonHours)]
    [InlineData(0xC0000070u, LogonFailureKind.WorkstationRestriction)]
    [InlineData(0xC000015Bu, LogonFailureKind.LogonTypeNotGranted)]
    [InlineData(0xC0000133u, LogonFailureKind.ClockSkew)]
    [InlineData(0xC0000413u, LogonFailureKind.AuthenticationFirewall)]
    [InlineData(0xC000006Du, LogonFailureKind.Other)]
    [InlineData(0u, LogonFailureKind.None)]
    public void ClassifyNtStatus(uint status, LogonFailureKind expected) => Assert.Equal(expected, AnalysisCodes.ClassifyNtStatus(status));

    [Theory]
    [InlineData(0x18u, LogonFailureKind.BadPassword)]
    [InlineData(0x6u, LogonFailureKind.UnknownAccount)]
    [InlineData(0x12u, LogonFailureKind.AccountRevoked)]
    [InlineData(0x17u, LogonFailureKind.PasswordExpired)]
    [InlineData(0x25u, LogonFailureKind.ClockSkew)]
    [InlineData(0xCu, LogonFailureKind.Policy)]
    [InlineData(0x1Fu, LogonFailureKind.Other)]
    [InlineData(0u, LogonFailureKind.None)]
    public void ClassifyKerberos(uint code, LogonFailureKind expected) => Assert.Equal(expected, AnalysisCodes.ClassifyKerberos(code));

    [Fact]
    public void Classify_4625UsesTheSubStatusBehindTheGenericCodes()
    {
        var records = new SecurityRecords();
        var badPassword = records.LogonFailure(Dc2, T0, "anowak", "10.0.0.5", "WS-GDANSK-03");
        var unknown = records.LogonFailure(Dc2, T0, "zażółć", "10.0.0.5", null, subStatus: "0xc0000064");
        var disabled = records.LogonFailure(Dc2, T0, "anowak", "10.0.0.5", null, status: "0xc000006e", subStatus: "0xc0000072");
        var locked = records.LogonFailure(Dc2, T0, "anowak", "10.0.0.5", null, status: "0xc0000234", subStatus: "0x0");
        var generic = records.LogonFailure(Dc2, T0, "anowak", "10.0.0.5", null, subStatus: "0x0");

        Assert.Equal((LogonFailureKind.BadPassword, "0xC000006A"), AnalysisCodes.Classify(badPassword));
        Assert.Equal((LogonFailureKind.UnknownAccount, "0xC0000064"), AnalysisCodes.Classify(unknown));
        Assert.Equal((LogonFailureKind.AccountDisabled, "0xC0000072"), AnalysisCodes.Classify(disabled));
        Assert.Equal((LogonFailureKind.AccountLocked, "0xC0000234"), AnalysisCodes.Classify(locked));
        Assert.Equal((LogonFailureKind.Other, "0xC000006D"), AnalysisCodes.Classify(generic));
    }

    [Fact]
    public void Classify_KerberosAndNtlm()
    {
        var records = new SecurityRecords();
        Assert.Equal((LogonFailureKind.BadPassword, "0x18"), AnalysisCodes.Classify(records.Kerberos(Pdc, T0, "jkowalski", KowalskiSid, "::ffff:10.0.0.5")));
        Assert.Equal((LogonFailureKind.UnknownAccount, "0x6"), AnalysisCodes.Classify(records.Tgt(Pdc, T0, "nieistniejący", "::ffff:10.0.0.5", "0x6")));
        Assert.Equal((LogonFailureKind.BadPassword, "0xC000006A"), AnalysisCodes.Classify(records.Ntlm(Pdc, T0, "jkowalski", "WS-01")));
        Assert.Equal((LogonFailureKind.None, null), AnalysisCodes.Classify(records.Ntlm(Pdc, T0, "jkowalski", "WS-01", "0x0")));
        Assert.Equal((LogonFailureKind.None, null), AnalysisCodes.Classify(records.Tgt(Pdc, T0, "jkowalski", "::ffff:10.0.0.5")));
    }

    [Fact]
    public void IsFailure_WithoutKeywordsDecidesByStatus()
    {
        var records = new SecurityRecords();
        var failure = records.Ntlm(Pdc, T0, "jkowalski", "WS-01") with { Keywords = 0 };
        var success = records.Ntlm(Pdc, T0, "jkowalski", "WS-01", "0x0") with { Keywords = 0 };
        var tgtFailure = records.Tgt(Pdc, T0, "jkowalski", "10.0.0.5", "0x6") with { Keywords = 0 };
        var preAuth = records.Kerberos(Pdc, T0, "jkowalski", null, "10.0.0.5") with { Keywords = 0 };

        Assert.True(AnalysisCodes.IsFailure(failure));
        Assert.False(AnalysisCodes.IsFailure(success));
        Assert.True(AnalysisCodes.IsSuccess(success));
        Assert.True(AnalysisCodes.IsFailure(tgtFailure));
        Assert.True(AnalysisCodes.IsFailure(preAuth));
        Assert.False(AnalysisCodes.IsSuccess(preAuth));
        Assert.True(AnalysisCodes.IsSuccess(records.LogonSuccess(Pdc, T0, "jkowalski", "10.0.0.5", null) with { Keywords = 0 }));
    }

    [Theory]
    [InlineData("::ffff:10.20.30.40", "10.20.30.40")]
    [InlineData(" 10.20.30.40 ", "10.20.30.40")]
    [InlineData("FE80::1:2", "fe80::1:2")]
    [InlineData("::ffff:abcd", "::ffff:abcd")]
    [InlineData("-", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Address_Normalized(string? value, string? expected) => Assert.Equal(expected, AnalysisValues.Address(value));

    [Theory]
    [InlineData("::1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("10.0.0.1", false)]
    public void IsLocalAddress(string address, bool expected) => Assert.Equal(expected, AnalysisValues.IsLocalAddress(address));

    [Theory]
    [InlineData(@"\\ws-krakow-07", "WS-KRAKOW-07")]
    [InlineData("księgowość-01$", "KSIĘGOWOŚĆ-01")]
    [InlineData(" ws01.firma.test ", "WS01.FIRMA.TEST")]
    [InlineData("-", null)]
    [InlineData(@"\\", null)]
    [InlineData("$", null)]
    public void Computer_Normalized(string value, string? expected) => Assert.Equal(expected, AnalysisValues.Computer(value));

    [Theory]
    [InlineData(@"FIRMA\JKowalski", "JKowalski", "jkowalski")]
    [InlineData("Łukasz.Wiśniewski@firma.test", "Łukasz.Wiśniewski@firma.test", "łukasz.wiśniewski@firma.test")]
    [InlineData(" żaneta.ćwik ", "żaneta.ćwik", "żaneta.ćwik")]
    public void Account_DisplayAndKey(string value, string display, string key)
    {
        Assert.Equal(display, AnalysisValues.AccountDisplay(value));
        Assert.Equal(key, AnalysisValues.AccountKey(value));
    }

    [Theory]
    [InlineData("*EXCH*", "SRV-EXCH01", true)]
    [InlineData("exch??", "EXCH01", true)]
    [InlineData("EXCH?", "EXCH01", false)]
    [InlineData("*ADFS*", "WS-KRAKOW-07", false)]
    [InlineData("*", "", true)]
    [InlineData("RDG*", "rdgw01", true)]
    public void WildcardMatch(string pattern, string text, bool expected) => Assert.Equal(expected, AnalysisValues.WildcardMatch(pattern, text));

    [Fact]
    public void ParsedSource_PerEvent()
    {
        var records = new SecurityRecords();

        Assert.Equal(new AttemptSource(AttemptSourceKind.CallerComputer, "WS-KRAKOW-07"),
            ParsedSource.Of(records.Lockout(Dc2, T0, "jkowalski", KowalskiSid, "ws-krakow-07")).Source);
        Assert.Equal(AttemptSourceKind.Unknown, ParsedSource.Of(records.Lockout(Dc2, T0, "jkowalski", KowalskiSid, null)).Source.Kind);
        Assert.Equal(new AttemptSource(AttemptSourceKind.IpAddress, "10.1.2.3"),
            ParsedSource.Of(records.Kerberos(Dc2, T0, "jkowalski", KowalskiSid, "::ffff:10.1.2.3")).Source);
        Assert.Equal(new AttemptSource(AttemptSourceKind.Local, "dc02.firma.test"),
            ParsedSource.Of(records.Kerberos("DC02.firma.test.", T0, "jkowalski", KowalskiSid, "::1")).Source);
        Assert.Equal(new AttemptSource(AttemptSourceKind.Workstation, "WS-01"), ParsedSource.Of(records.Ntlm(Dc2, T0, "jkowalski", @"\\ws-01")).Source);
        Assert.Equal(AttemptSourceKind.Unknown, ParsedSource.Of(records.Ntlm(Dc2, T0, "jkowalski", null)).Source.Kind);

        var withBoth = ParsedSource.Of(records.LogonFailure(Dc2, T0, "jkowalski", "10.0.0.9", "WS-RADOM-02"));
        Assert.Equal((new AttemptSource(AttemptSourceKind.IpAddress, "10.0.0.9"), "10.0.0.9", "WS-RADOM-02"), (withBoth.Source, withBoth.Address, withBoth.Workstation));
        Assert.Equal(new AttemptSource(AttemptSourceKind.Workstation, "WS-RADOM-02"),
            ParsedSource.Of(records.LogonFailure(Dc2, T0, "jkowalski", "-", "WS-RADOM-02")).Source);
        Assert.Equal(new AttemptSource(AttemptSourceKind.Local, "dc02.firma.test"),
            ParsedSource.Of(records.LogonFailure(Dc2, T0, "jkowalski", "127.0.0.1", "DC02", logonType: 2)).Source);
        Assert.Equal(new AttemptSource(AttemptSourceKind.Local, "dc02.firma.test"),
            ParsedSource.Of(records.LogonFailure(Dc2, T0, "jkowalski", null, null, logonType: 5)).Source);
    }

    [Fact]
    public void ValuesAreQueryableUnlessBothQuotesOrControlCharacters()
    {
        Assert.True(AnalysisValues.IsQueryable("o'brien"));
        Assert.True(AnalysisValues.IsQueryable("\"cytat\""));
        Assert.False(AnalysisValues.IsQueryable("o'b\"rien"));
        Assert.False(AnalysisValues.IsQueryable("jan\nkowalski"));
        Assert.False(AnalysisValues.IsQueryable(string.Empty));
    }

    [Fact]
    public void ToStringCarriesNoValue()
    {
        var source = new AttemptSource(AttemptSourceKind.Workstation, "WS-KRAKOW-07");
        var reference = new EventRecordRef(Dc2, 41_234);
        var note = new AnalysisNote("EmptyCaller", [Dc2, "2026-11-03T08:00:00.0000000+00:00"]);
        var code = new EventCodeCount(4776, "0xC000006A", LogonFailureKind.BadPassword, 5);

        foreach (var text in new[] { source.ToString(), reference.ToString(), note.ToString(), code.ToString() })
        {
            Assert.DoesNotContain("KRAKOW", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dc02", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0xC000006A", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("firma", new EventLogRecord { Host = Dc2, Computer = Dc2, Channel = "Security", EventId = 4740, RecordId = 1, TimeCreated = T0 }
            .ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
