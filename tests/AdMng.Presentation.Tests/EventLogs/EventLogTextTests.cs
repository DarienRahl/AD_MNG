using System.Globalization;
using System.Resources;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Localization;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

public class EventLogTextTests
{
    private static readonly ResxLocalizer Polish = new(CultureInfo.GetCultureInfo("pl-PL"));
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public void EveryDynamicKey_IsInBothLanguages()
    {
        var resources = new ResourceManager(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);
        var keys = EventLogText.AllKeys().ToList();

        Assert.True(keys.Count > 200);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in keys)
        {
            var en = resources.GetString(key, CultureInfo.InvariantCulture);
            var pl = resources.GetString(key, CultureInfo.GetCultureInfo("pl"));
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(pl), key);
        }
    }

    [Fact]
    public void EveryCatalogCode_HasAPolishMeaning()
    {
        foreach (var code in EventCodes.All)
        {
            Assert.NotEqual(Polish[L.EventLogs.UnknownCode], EventLogText.Code(Polish, code));
        }

        Assert.Equal("złe hasło", EventLogText.Code(Polish, EventCodes.Describe(EventCodeKind.NtStatus, EventCodes.StatusWrongPassword)));
        Assert.Equal("0x18 – wstępne uwierzytelnianie nieudane (złe hasło)", EventLogText.CodeWithMeaning(Polish, EventCodes.Describe(EventCodeKind.KerberosResult, 0x18)));
        Assert.Equal("0xC0000234 – the account is locked out", EventLogText.CodeWithMeaning(English, EventCodes.Describe(EventCodeKind.NtStatus, 0xC0000234)));
        Assert.Equal(Polish[L.EventLogs.UnknownCode], EventLogText.Code(Polish, EventCodes.Describe(EventCodeKind.NtStatus, 0xC0001234)));
    }

    [Fact]
    public void Explain_GivesTheMeaningOfCodeFields_AndMarksWeakEncryption()
    {
        var failure = EventLogSamples.KerberosPreAuthFailed(EventLogSamples.Dc01, 1, EventLogSamples.Morning, EventLogSamples.JanKowalski, EventLogSamples.WawPc001Ip);
        var logon = EventLogSamples.LogonFailed(EventLogSamples.Dc01, 2, EventLogSamples.Morning, "jan.kowalski", "10.10.1.21", "WAW-PC001", logonType: 10);
        var ticket = EventLogSamples.TicketGranted(EventLogSamples.Dc01, 3, EventLogSamples.Morning, "jan.kowalski", "10.10.1.21", encryptionType: "0x17");

        Assert.Equal("wstępne uwierzytelnianie nieudane (złe hasło)", EventLogText.Explain(Polish, failure, "Status", "0x18"));
        Assert.Equal("PA-ENC-TIMESTAMP (hasło)", EventLogText.Explain(Polish, failure, "PreAuthType", "2"));
        Assert.Equal("zdalne interaktywne (pulpit zdalny)", EventLogText.Explain(Polish, logon, "LogonType", "10"));
        Assert.Equal("złe hasło", EventLogText.Explain(Polish, logon, "SubStatus", "0xc000006a"));
        Assert.Equal("nieznana nazwa użytkownika lub złe hasło", EventLogText.Explain(Polish, logon, "FailureReason", "%%2313"));
        Assert.Contains("słabe", EventLogText.Explain(Polish, ticket, "TicketEncryptionType", "0x17"), StringComparison.Ordinal);
        Assert.Contains("weak", EventLogText.Explain(English, ticket, "TicketEncryptionType", "0x1"), StringComparison.Ordinal);
        Assert.Null(EventLogText.Explain(Polish, failure, "IpAddress", "::ffff:10.10.1.21"));
        Assert.Null(EventLogText.Explain(Polish, failure, "Status", null));
    }

    [Theory]
    [InlineData(EventLogAccessError.RpcUnavailable, "Zdalne zarządzanie dziennikiem zdarzeń")]
    [InlineData(EventLogAccessError.AccessDenied, "Czytelnicy dziennika zdarzeń")]
    [InlineData(EventLogAccessError.ChannelNotFound, "Wczytaj kanały")]
    [InlineData(EventLogAccessError.NotSupported, "logowaniem Windows")]
    [InlineData(EventLogAccessError.Unreachable, "pełna nazwa DNS")]
    public void AccessErrors_CarryTheirRemedy(EventLogAccessError error, string remedy)
    {
        var text = EventLogText.AccessError(Polish, error);

        Assert.StartsWith(EventLogText.AccessErrorShort(Polish, error), text, StringComparison.Ordinal);
        Assert.Contains(remedy, text, StringComparison.Ordinal);
        Assert.EndsWith("(błąd 1722)", EventLogText.AccessError(Polish, error, 1722), StringComparison.Ordinal);
    }

    [Fact]
    public void English_AccessDenied_NamesTheGroupAndTheFirewallRule()
    {
        Assert.Contains("Event Log Readers", EventLogText.AccessError(English, EventLogAccessError.AccessDenied), StringComparison.Ordinal);
        Assert.Contains("Remote Event Log Management", EventLogText.AccessError(English, EventLogAccessError.RpcUnavailable), StringComparison.Ordinal);
    }

    [Fact]
    public void RpcUnavailable_NamesAComputerThatIsOff_BeforeTheFirewall()
    {
        // E-EVTLOG-03: 1722 is also what a computer that is off (its A record still resolving) or cut off by a WAN outage gives.
        var polish = EventLogText.AccessError(Polish, EventLogAccessError.RpcUnavailable, 1722);
        var english = EventLogText.AccessError(English, EventLogAccessError.RpcUnavailable, 1722);

        var polishOn = polish.IndexOf("jest włączony", StringComparison.Ordinal);
        var englishOn = english.IndexOf("is on", StringComparison.Ordinal);

        Assert.InRange(polishOn, 0, polish.IndexOf("Zdalne zarządzanie dziennikiem zdarzeń", StringComparison.Ordinal));
        Assert.InRange(englishOn, 0, english.IndexOf("Remote Event Log Management", StringComparison.Ordinal));
        Assert.EndsWith("(błąd 1722)", polish, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageUnavailable_NamesAnEventGoneFromTheSource_NotOnlyMissingMetadata()
    {
        // E-EVTLOG-02: the re-read finds nothing when the log has overwritten the event since it was read.
        Assert.Contains("nie ma go już w źródle", Polish[L.EventLogs.MessageUnavailable], StringComparison.Ordinal);
        Assert.Contains("no longer in the source", English[L.EventLogs.MessageUnavailable], StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_FillTheirArguments_InTheUiLanguage()
    {
        var zone = TimeZoneInfo.Utc;
        var proxy = new AnalysisNote(LockoutNoteKeys.LikelyProxy, ["Workstation", "EXCH01", ProxyReasonKeys.NamePattern], AnalysisNoteSeverity.Warning);
        var caller = new AnalysisNote(LockoutNoteKeys.EmptyCaller, ["dc01.contoso.test", "2026-11-03T07:34:01.0000000+00:00"]);
        var failed = new AnalysisNote(LockoutNoteKeys.LogReadFailed, ["dc02.contoso.test", nameof(EventLogAccessError.AccessDenied)]);
        var relays = new AnalysisNote(FailedLogonNoteKeys.DomainControllerRelays, ["3"]);

        Assert.StartsWith("Stacja EXCH01 wygląda na serwer pośredniczący", EventLogText.LockoutNote(Polish, proxy, zone), StringComparison.Ordinal);
        Assert.Contains("nazwa wskazuje na serwer pośredniczący", EventLogText.LockoutNote(Polish, proxy, zone), StringComparison.Ordinal);
        Assert.Contains(new DateTime(2026, 11, 3, 7, 34, 1).ToString("G", CultureInfo.GetCultureInfo("pl-PL")), EventLogText.LockoutNote(Polish, caller, zone), StringComparison.Ordinal);
        Assert.Contains("Czytelnicy dziennika zdarzeń", EventLogText.LockoutNote(Polish, failed, zone), StringComparison.Ordinal);
        Assert.StartsWith("3 niepowodzeń", EventLogText.FailedLogonNote(Polish, relays, zone), StringComparison.Ordinal);
        foreach (var key in EventLogText.LockoutNotes)
        {
            Assert.True(Polish.Contains(EventLogText.LockoutNoteKey(key)), key);
        }
    }

    [Fact]
    public void ChannelsLevelsAndRawXPathReasons_AreLocalized()
    {
        Assert.Equal("Zabezpieczenia", EventLogText.Channel(Polish, "Security"));
        Assert.Equal("Aplikacja", EventLogText.Channel(Polish, "application"));
        Assert.Equal("Zasady grupy (operacyjny)", EventLogText.Channel(Polish, "Microsoft-Windows-GroupPolicy/Operational"));
        Assert.Equal("Custom-Channel/Admin", EventLogText.Channel(Polish, "Custom-Channel/Admin"));
        Assert.Equal("Zabezpieczenia (Security)", EventLogText.ChannelOption(Polish, "Security"));
        Assert.Equal("Ostrzeżenie", EventLogText.Level(Polish, EventLogLevels.Warning));
        Assert.Equal("Informacje", EventLogText.Level(Polish, EventLogLevels.LogAlways));
        Assert.False(EventQueryXml.IsValidRawXPath("*[System[(EventID=4740)]", out var reason));
        Assert.Equal("Nawiasy nie są sparowane.", EventLogText.RawXPathReason(Polish, reason!));
        Assert.Equal("6 godz.", EventLogText.Duration(Polish, TimeSpan.FromHours(6)));
        Assert.Equal("3 dni", EventLogText.Duration(Polish, TimeSpan.FromDays(3)));
    }

    [Fact]
    public void Summary_ShowsTheKeyFields()
    {
        var failure = EventLogSamples.LogonFailed(EventLogSamples.Dc01, 2, EventLogSamples.Morning, "jan.kowalski", "10.10.1.21", "WAW-PC001");
        var lockout = EventLogSamples.AccountLockedOut(EventLogSamples.Dc01, 3, EventLogSamples.Morning, "jan.kowalski", "WAW-PC001");

        Assert.Equal("CONTOSO\\jan.kowalski · 10.10.1.21 · WAW-PC001 · 0xC000006A – złe hasło · typ logowania 3 (sieciowe (folder udostępniony, IIS …))",
            EventLogText.Summary(Polish, failure));
        Assert.Equal("jan.kowalski · WAW-PC001", EventLogText.Summary(Polish, lockout));
    }
}
