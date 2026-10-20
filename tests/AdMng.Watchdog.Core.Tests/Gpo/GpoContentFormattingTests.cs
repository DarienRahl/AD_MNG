using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>The formatter texts of the GPO content rows (stage B design 8.4/8.6): an unreadable file reads „nie udało się
/// odczytać” on either side, the summary row of a truncated event gives the counts – e-mail, Teams and digest share them
/// through <see cref="AlertContentBuilder.ChangeRow"/>.</summary>
public sealed class GpoContentFormattingTests
{
    [Theory]
    [InlineData("pl", "nie udało się odczytać")]
    [InlineData("en", "could not be read")]
    public void UnreadableFileMarker_ShowsValueUnreadable(string language, string expected)
    {
        var context = NotificationContext.Create(new WatchdogConfiguration { General = new GeneralSettings { Language = language } }, DetailLevel.Full);
        var diff = GpoSettingsDiffer.Diff(
            [new GpoSetting { Key = "Computer/Security/System Access/MinimumPasswordLength", Value = "14", Source = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf" }],
            [
                new GpoSetting { Key = "Computer/Security/System Access/MinimumPasswordLength", Value = "14", Source = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf" },
                new GpoSetting { Key = GpoSnapshotBuilder.FileMarkerPrefix + "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", Kind = GpoSettingKind.Unreadable },
            ]);

        var row = AlertContentBuilder.ChangeRow(Assert.Single(diff.Changes), context);
        var back = AlertContentBuilder.ChangeRow(Assert.Single(GpoSettingsDiffer.Diff(
            [new GpoSetting { Key = GpoSnapshotBuilder.FileMarkerPrefix + "Machine/Registry.pol", Kind = GpoSettingKind.Unreadable }], []).Changes), context);

        Assert.Equal("File/Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", row.Attribute);
        Assert.Equal(("—", expected), (row.Before, row.After));
        Assert.Equal((expected, "—"), (back.Before, back.After));
        Assert.Equal("unreadable", AlertContentBuilder.ChangeRow(new AttributeChange
        {
            Attribute = "Computer/Registry/HKLM\\Software\\X!Note",
            NewValues = ["unreadable"],
            NewCount = 1,
        }, context).After); // only the File/ marker is translated
    }

    [Theory]
    [InlineData("pl", "Pozostałe zmienione ustawienia", "nie pokazano kolejnych: 37 (zmienionych ustawień łącznie: 236)")]
    [InlineData("en", "More changed settings", "37 more not shown (236 changed settings in total)")]
    public void SummaryRowOfATruncatedEvent_GivesTheCounts(string language, string name, string text)
    {
        var context = NotificationContext.Create(new WatchdogConfiguration { General = new GeneralSettings { Language = language } }, DetailLevel.Full);
        var settings = Enumerable.Range(0, 236).Select(i => new GpoSetting { Key = $"Computer/Registry/HKLM\\Software\\Policies\\X!V{i:D3}", Value = "REG_DWORD:1" }).ToList();
        var diff = GpoSettingsDiffer.Diff([], settings);

        var row = AlertContentBuilder.ChangeRow(diff.Changes[^1], context);

        Assert.True(diff.Truncated);
        Assert.Equal((name, (string?)null, text), (row.Attribute, row.Before, row.After));
    }
}
