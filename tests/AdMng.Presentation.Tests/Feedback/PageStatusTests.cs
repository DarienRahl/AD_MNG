using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Feedback;

/// <summary>
/// The value a page shows in its status line (track V, stage V2; design note docs/dev/v-interface-renewal.md 4.5): its
/// kinds, what each kind may carry, the value-free copy for the log and the wording helpers in both languages.
/// </summary>
public class PageStatusTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public void Idle_IsTheNeutralSummary_WithoutProgressActionOrClose()
    {
        var summary = PageStatus.Idle("  2 314 obiektów · Zaznaczono: 12 ");

        Assert.Equal(PageStatusKind.Idle, summary.Kind);
        Assert.True(summary.IsIdle);
        Assert.False(summary.IsWorking);
        Assert.Equal("2 314 obiektów · Zaznaczono: 12", summary.Message);
        Assert.Null(summary.Progress);
        Assert.Null(summary.Action);
        Assert.False(summary.CanClose);
        Assert.Same(PageStatusLog.None, summary.Log);

        // No summary is the one empty status.
        Assert.Same(PageStatus.Empty, PageStatus.Idle(null));
        Assert.Same(PageStatus.Empty, PageStatus.Idle("   "));
        Assert.Equal(string.Empty, PageStatus.Empty.Message);
        Assert.False(PageStatus.Empty.CanClose);
    }

    [Fact]
    public void Working_HasAProgress_AndCanNeverBeClosed()
    {
        var log = new PageStatusLog(L.ListStatus.Reading, 2314);
        var stop = new PageStatusAction("Zatrzymaj", new RelayCommand(() => { }));

        var indeterminate = PageStatus.Working("Wczytywanie „Kraków”…", log);
        var measured = PageStatus.Working("Wczytywanie „Kraków”…", log, PageProgress.Of(1, 4), stop);

        Assert.Equal(PageStatusKind.Working, indeterminate.Kind);
        Assert.True(indeterminate.IsWorking);
        Assert.False(indeterminate.CanClose);
        Assert.True(Assert.NotNull(indeterminate.Progress).IsIndeterminate);
        Assert.Null(indeterminate.Action);
        Assert.Equal(0.25, Assert.NotNull(measured.Progress).Fraction);
        Assert.Same(stop, measured.Action);
        Assert.False(measured.CanClose);
        Assert.Same(log, measured.Log);
    }

    [Fact]
    public void Results_CanBeClosed_UnlessTheyDescribeALastingState()
    {
        var log = new PageStatusLog("Some.Key");
        var retry = new PageStatusAction("Ponów", new RelayCommand(() => { }), "parametr");

        var done = PageStatus.Done("Wyłączono 12 kont w contoso.test.", log);
        var attention = PageStatus.Attention("Pominięto 3 z 15 kont: brak prawa zapisu.", log, retry);
        var error = PageStatus.Error("Nie udało się wczytać „Kraków”: kontroler domeny jest niedostępny.", log, retry);
        var lasting = PageStatus.Error("Sesja tego okna została zakończona.", log, canClose: false);
        var lastingAttention = PageStatus.Attention("Ten kontener już nie istnieje.", log, canClose: false);

        Assert.Equal(
            [PageStatusKind.Done, PageStatusKind.Attention, PageStatusKind.Error, PageStatusKind.Error, PageStatusKind.Attention],
            new[] { done, attention, error, lasting, lastingAttention }.Select(status => status.Kind));
        Assert.All(new[] { done, attention, error }, status => Assert.True(status.CanClose));
        Assert.False(lasting.CanClose);
        Assert.False(lastingAttention.CanClose);
        Assert.All(new[] { done, attention, error, lasting }, status => Assert.Null(status.Progress));
        Assert.All(new[] { done, attention, error, lasting }, status => Assert.False(status.IsIdle || status.IsWorking));
        Assert.Null(done.Action);
        Assert.Same(retry, attention.Action);
        Assert.Equal("Ponów", error.Action!.Label);
        Assert.Equal("parametr", error.Action.Parameter);
    }

    [Fact]
    public void AStatusThatIsNotTheSummary_NeedsATextAndALogCopy()
    {
        var log = new PageStatusLog("Some.Key");

        Assert.ThrowsAny<ArgumentException>(() => PageStatus.Done(" ", log));
        Assert.ThrowsAny<ArgumentException>(() => PageStatus.Error(string.Empty, log));
        Assert.Throws<ArgumentNullException>(() => PageStatus.Attention("Tekst", null!));
        Assert.Throws<ArgumentNullException>(() => PageStatus.Working("Tekst", null!));
        Assert.Throws<ArgumentNullException>(() => new PageStatusLog(null!));
    }

    [Fact]
    public void SameMessage_IsTheSameKindAndKey_WhateverTheCountsAndTheText()
    {
        var first = PageStatus.Working("Wczytywanie „Kraków”…", new PageStatusLog(L.ListStatus.Reading));
        var later = PageStatus.Working("Wczytywanie „Kraków”: na razie 1 500 obiektów…", new PageStatusLog(L.ListStatus.Reading, 1500));
        var stopping = PageStatus.Working("Zatrzymywanie…", new PageStatusLog(L.Feedback.Stopping));
        var stopped = PageStatus.Attention("Zatrzymano.", new PageStatusLog(L.ListStatus.Reading, 1500));

        Assert.True(later.IsSameMessageAs(first));
        Assert.False(stopping.IsSameMessageAs(later));
        Assert.False(stopped.IsSameMessageAs(later));
        Assert.False(first.IsSameMessageAs(null));

        // Two summaries are the same message: a selection that changes is not news.
        Assert.True(PageStatus.Idle("6 obiektów").IsSameMessageAs(PageStatus.Idle("6 obiektów · Zaznaczono: 2")));
        Assert.True(PageStatus.Empty.IsSameMessageAs(PageStatus.Idle("6 obiektów")));
        Assert.False(PageStatus.Idle("6 obiektów").IsSameMessageAs(first));
    }

    [Fact]
    public void Progress_IsIndeterminateOrAFractionBetweenZeroAndOne()
    {
        Assert.True(PageProgress.Indeterminate.IsIndeterminate);
        Assert.Null(PageProgress.Indeterminate.Fraction);
        Assert.True(default(PageProgress).IsIndeterminate);
        Assert.Equal(PageProgress.Indeterminate, default);

        Assert.Equal(0.5, PageProgress.Of(0.5).Fraction);
        Assert.False(PageProgress.Of(0.5).IsIndeterminate);
        Assert.Equal(0d, PageProgress.Of(-3).Fraction);
        Assert.Equal(1d, PageProgress.Of(7).Fraction);
        Assert.True(PageProgress.Of(double.NaN).IsIndeterminate);

        Assert.Equal(0.25, PageProgress.Of(3, 12).Fraction);
        Assert.Equal(1d, PageProgress.Of(15, 12).Fraction);
        Assert.True(PageProgress.Of(3, 0).IsIndeterminate);
        Assert.True(PageProgress.Of(3, -1).IsIndeterminate);
        Assert.Equal(PageProgress.Of(1, 4), PageProgress.Of(0.25));
        Assert.NotEqual(PageProgress.Of(0), PageProgress.Indeterminate);
    }

    [Fact]
    public void LogCopy_HoldsTheKeyCountsAndCode_AndNothingElse()
    {
        var counts = new long[] { 150, 3 };
        var log = new PageStatusLog(L.ListStatus.Stopped, counts);
        counts[0] = 999;

        Assert.Equal(L.ListStatus.Stopped, log.Key);
        Assert.Equal([150L, 3L], log.Counts);
        Assert.True(log.Code.IsNone);
        Assert.Equal("List.Status.Stopped (150, 3)", log.ToString());

        var failed = new PageStatusLog(L.ListStatus.ReadFailed).WithCode(ResultCode.Ldap(81));
        Assert.Equal("List.Status.ReadFailed [LDAP 81]", failed.ToString());
        Assert.Empty(failed.Counts);
        Assert.Equal(string.Empty, PageStatusLog.None.Key);
        Assert.Equal(string.Empty, PageStatusLog.None.ToString());

        // The public surface of the copy: a key, numbers and a code that only the program can make - no free text.
        Assert.Equal(
            ["Code", "Counts", "Key"],
            typeof(PageStatusLog).GetProperties().Where(p => p.GetMethod is { IsPublic: true, IsStatic: false }).Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(ResultCode).GetConstructors(), c => c.GetParameters().Length > 0);
    }

    [Fact]
    public void AStatusAsText_IsItsKindAndLogCopy_NeverTheTextOnScreen()
    {
        const string name = "Żaneta Śliwa";
        var status = PageStatus.Error($"Nie udało się zapisać konta „{name}”.", new PageStatusLog("Save.Failed", 1).WithCode(ResultCode.Win32(0x52D)));

        Assert.Equal("Error: Save.Failed (1) [0x52D]", status.ToString());
        Assert.DoesNotContain(name, status.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(name, status.Log.ToString(), StringComparison.Ordinal);
        Assert.Equal("Idle", PageStatus.Idle($"Zaznaczono: {name}").ToString());
    }

    [Fact]
    public void ResultCode_IsMadeOfNumbersAndNames_NeverOfAServerMessage()
    {
        const string dn = "CN=Łukasz Żak,OU=Kraków,DC=contoso,DC=test";
        const string diagnostic = "0000052D: SvcErr: DSID-031A1254, problem 5003 (WILL_NOT_PERFORM), CN=Łukasz Żak";

        var both = new DirectoryException(DirectoryErrorKind.PasswordPolicy, diagnostic) { LdapResultCode = 53, Win32Error = 0x52D, ServerMessage = diagnostic, DistinguishedName = dn };
        var ldap = new DirectoryException(DirectoryErrorKind.ServerUnavailable, diagnostic) { LdapResultCode = 81, DistinguishedName = dn };
        var win32 = new DirectoryException(DirectoryErrorKind.AccountRestriction, diagnostic) { Win32Error = 0x775 };
        var kind = new DirectoryException(DirectoryErrorKind.Timeout, diagnostic) { DistinguishedName = dn };
        var other = new InvalidOperationException(diagnostic);

        Assert.Equal("LDAP 53, 0x52D", ResultCode.Of(both).ToString());
        Assert.Equal("LDAP 81", ResultCode.Of(ldap).ToString());
        Assert.Equal("0x775", ResultCode.Of(win32).ToString());
        Assert.Equal("Timeout", ResultCode.Of(kind).ToString());
        Assert.Equal("InvalidOperationException", ResultCode.Of(other).ToString());
        Assert.Equal("LDAP 81", ResultCode.Of(new AggregateException(ldap)).ToString());
        Assert.True(ResultCode.Of(null).IsNone);
        Assert.True(ResultCode.Of(new OperationCanceledException(diagnostic)).IsNone);
        Assert.True(ResultCode.None.IsNone);
        Assert.Equal(string.Empty, ResultCode.None.ToString());
        Assert.Equal(ResultCode.Ldap(81), ResultCode.Of(ldap));
        Assert.Equal("ServerUnavailable", ResultCode.Kind(DirectoryErrorKind.ServerUnavailable).ToString());

        foreach (var exception in new Exception[] { both, ldap, win32, kind, other })
        {
            var code = ResultCode.Of(exception).ToString();
            Assert.DoesNotContain("Łukasz", code, StringComparison.Ordinal);
            Assert.DoesNotContain("DSID", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Kraków", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Titles_AreFixedPerSeverity_InBothLanguages()
    {
        var english = new ResxLocalizer(English);
        var polish = new ResxLocalizer(Polish);

        Assert.Equal(
            ["", "Working", "Done", "Attention", "Error"],
            Enum.GetValues<PageStatusKind>().Select(kind => PageStatusText.Title(english, kind)));
        Assert.Equal(
            ["", "Trwa", "Gotowe", "Uwaga", "Błąd"],
            Enum.GetValues<PageStatusKind>().Select(kind => PageStatusText.Title(polish, kind)));

        // Every severity has a key of its own; the summary has none.
        Assert.Equal(string.Empty, PageStatusText.TitleKey(PageStatusKind.Idle));
        var keys = Enum.GetValues<PageStatusKind>().Where(kind => kind != PageStatusKind.Idle).Select(PageStatusText.TitleKey).ToList();
        Assert.Equal(4, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.True(english.Contains(key), key));
    }

    [Theory]
    [InlineData(0, "0 obiektów")]
    [InlineData(1, "1 obiekt")]
    [InlineData(2, "2 obiekty")]
    [InlineData(4, "4 obiekty")]
    [InlineData(5, "5 obiektów")]
    [InlineData(11, "11 obiektów")]
    [InlineData(12, "12 obiektów")]
    [InlineData(14, "14 obiektów")]
    [InlineData(21, "21 obiektów")]
    [InlineData(22, "22 obiekty")]
    [InlineData(25, "25 obiektów")]
    [InlineData(112, "112 obiektów")]
    [InlineData(122, "122 obiekty")]
    public void Counts_UseThePolishPluralForms(int count, string expected) =>
        Assert.Equal(expected, PageStatusText.Objects(new ResxLocalizer(Polish), count));

    [Theory]
    [InlineData(0, "0 objects")]
    [InlineData(1, "1 object")]
    [InlineData(2, "2 objects")]
    [InlineData(5, "5 objects")]
    [InlineData(21, "21 objects")]
    public void Counts_UseTheEnglishPluralForms(int count, string expected) =>
        Assert.Equal(expected, PageStatusText.Objects(new ResxLocalizer(English), count));

    [Fact]
    public void Counts_GroupThousandsAsTheLanguageDoes()
    {
        Assert.Equal("2,314 objects", PageStatusText.Objects(new ResxLocalizer(English), 2314));
        Assert.Equal($"{2314.ToString("N0", Polish)} obiektów", PageStatusText.Objects(new ResxLocalizer(Polish), 2314));
        Assert.Equal($"{1002.ToString("N0", Polish)} obiekty", PageStatusText.Objects(new ResxLocalizer(Polish), 1002));

        // Any noun: the caller names the three forms (one, few, many).
        var polish = new ResxLocalizer(Polish);
        Assert.Equal("3 obiekty", PageStatusText.Count(polish, 3, L.Feedback.ObjectsOne, L.Feedback.ObjectsFew, L.Feedback.ObjectsMany));
    }

    [Fact]
    public void Failure_HasOneShape_WhatWeTriedWhyWhatNextAndTheCodeInParenthesesAtTheEnd()
    {
        Assert.Equal(
            "Could not read “Kraków”: The domain controller is not available. Press Retry to try again. (LDAP 81)",
            PageStatusText.Failure("Could not read “Kraków”", "The domain controller is not available.", "Press Retry to try again.", ResultCode.Ldap(81)));

        // Full stops are added where a part has none and never doubled; a colon or a stop after "what we tried" goes.
        Assert.Equal(
            "Nie udało się wczytać „Kraków”: Kontroler domeny jest niedostępny. Wybierz Ponów.",
            PageStatusText.Failure(" Nie udało się wczytać „Kraków”: ", "Kontroler domeny jest niedostępny", "Wybierz Ponów"));
        Assert.Equal(
            "Nie udało się zapisać: Czy konto istnieje? Sprawdź nazwę!",
            PageStatusText.Failure("Nie udało się zapisać.", "Czy konto istnieje?", "Sprawdź nazwę!"));

        // Without a reason, without advice, with a code only: the code still never stands alone.
        Assert.Equal("Could not read “IT”. Press Retry.", PageStatusText.Failure("Could not read “IT”", null, "Press Retry"));
        Assert.Equal("Could not read “IT”: Access is denied.", PageStatusText.Failure("Could not read “IT”", "Access is denied."));
        Assert.Equal("Could not read “IT”. (Timeout)", PageStatusText.Failure("Could not read “IT”", " ", null, ResultCode.Kind(DirectoryErrorKind.Timeout)));
        Assert.EndsWith("(0x52D)", PageStatusText.Failure("Could not save", "The password is too short.", null, ResultCode.Win32(0x52D)), StringComparison.Ordinal);
        Assert.ThrowsAny<ArgumentException>(() => PageStatusText.Failure(" ", "Why."));
    }

    [Fact]
    public void Name_IsWhatAScreenReaderHears_TheTitleThenTheText()
    {
        var english = new ResxLocalizer(English);
        var polish = new ResxLocalizer(Polish);
        var log = new PageStatusLog("Some.Key");

        Assert.Equal("Error: Could not read “IT”.", PageStatusText.Name(english, PageStatus.Error("Could not read “IT”.", log)));
        Assert.Equal("Błąd: Nie udało się wczytać „IT”.", PageStatusText.Name(polish, PageStatus.Error("Nie udało się wczytać „IT”.", log)));
        Assert.Equal("Trwa: Wczytywanie „IT”…", PageStatusText.Name(polish, PageStatus.Working("Wczytywanie „IT”…", log)));
        Assert.Equal("Gotowe: Wyłączono 12 kont.", PageStatusText.Name(polish, PageStatus.Done("Wyłączono 12 kont.", log)));
        Assert.Equal("Uwaga: Pominięto 3 konta.", PageStatusText.Name(polish, PageStatus.Attention("Pominięto 3 konta.", log)));

        // The summary is its own name; an empty line is still a named region.
        Assert.Equal("6 obiektów", PageStatusText.Name(polish, PageStatus.Idle("6 obiektów")));
        Assert.Equal("Status", PageStatusText.Name(english, PageStatus.Empty));
        Assert.Equal("Stan", PageStatusText.Name(polish, PageStatus.Empty));
    }
}
