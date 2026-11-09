using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// The pattern language of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.3): grammar, functions, escapes, the
/// creation tokens, the counter spec, errors with positions and tokens (never the engine's texts), cycles and the evaluation
/// order. Tests that need the header folding or the synonym table of B1 are skipped until the wave-1 merge.
/// </summary>
public sealed class CreationPatternTests
{
    private const string Container = "OU=Nowi pracownicy,OU=Pracownicy,OU=Firma,DC=contoso,DC=test";

    private static readonly DateTimeOffset Clock = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static CreationPatternSet Compile(
        IReadOnlyList<AttributeRule> rules,
        IReadOnlyList<TableColumn>? columns = null,
        IReadOnlyCollection<string>? provided = null,
        ObjectKind kind = ObjectKind.User) =>
        CreationPatternSet.Compile(rules, new PatternScope(kind, columns ?? [], provided ?? [Attr.GivenName, Attr.Sn], FakeSchema.Create(bulkCreate: true)));

    private static CreationPatternSet Compile(string attribute, string pattern, IReadOnlyList<TableColumn>? columns = null) =>
        Compile([new AttributeRule(attribute, pattern)], columns);

    private static PatternRow Row(
        IReadOnlyDictionary<string, string>? values = null,
        IReadOnlyDictionary<string, string>? cells = null,
        string? number = null,
        string? suffix = "contoso.com",
        string? mailDomain = null) => new()
    {
        Attribute = name => values is not null && values.TryGetValue(name, out var value) ? [value] : [],
        Column = name => cells is not null && cells.TryGetValue(name, out var cell) ? cell : string.Empty,
        Number = number,
        UpnSuffix = suffix,
        MailDomain = mailDomain,
        Domain = "contoso.test",
        ContainerDn = Container,
        Now = Clock,
    };

    private static Dictionary<string, string> Person(string givenName, string surname) =>
        new(StringComparer.OrdinalIgnoreCase) { [Attr.GivenName] = givenName, [Attr.Sn] = surname };

    private static IReadOnlyList<TableColumn> Headerless(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new TableColumn(i, TableTextReader.ColumnLetter(i), string.Empty))];

    // ------------------------------------------------------------------ the counter spec

    [Theory]
    [InlineData(201, 0, 201, "201")]
    [InlineData(1, 4, 2, "0002")]
    [InlineData(1, 4, 12345, "12345")]
    [InlineData(0, 3, 0, "000")]
    public void CounterSpec_Format_PadsToTheWidth_AndWritesLongerNumbersInFull(int start, int width, int number, string expected) =>
        Assert.Equal(expected, new CounterSpec(start, width, 1).Format(number));

    [Theory]
    [InlineData(null, 1, 0, 1)]
    [InlineData("201", 201, 0, 1)]
    [InlineData("1:4", 1, 4, 1)]
    [InlineData("200:0:10", 200, 0, 10)]
    [InlineData(" 7 ", 7, 0, 1)]
    [InlineData("0:9:1000000", 0, 9, 1_000_000)]
    [InlineData("999999999", 999_999_999, 0, 1)]
    public void CounterSpec_TryParse_AcceptsTheForms(string? argument, int start, int width, int step)
    {
        Assert.True(CounterSpec.TryParse(argument, out var spec, out var error));
        Assert.Null(error);
        Assert.Equal(new CounterSpec(start, width, step), spec);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1000000000")]
    [InlineData("1:10")]
    [InlineData("1:2:0")]
    [InlineData("1:2:1000001")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5")]
    [InlineData("２０１")]
    public void CounterSpec_TryParse_RefusesWhatIsNotDecimalOrOutOfRange(string argument)
    {
        Assert.False(CounterSpec.TryParse(argument, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void CounterSpec_Grid_StartsAtTheStart_AndStepsByTheStep()
    {
        var spec = new CounterSpec(200, 0, 10);

        Assert.Equal(200, spec.GridAtOrAbove(5));
        Assert.Equal(210, spec.GridAtOrAbove(201));
        Assert.Equal(210, spec.GridAtOrAbove(210));
        Assert.True(spec.IsOnGrid(220));
        Assert.False(spec.IsOnGrid(215));
        Assert.False(spec.IsOnGrid(190));
        Assert.Null(new CounterSpec(999_999_990, 0, 10).GridAtOrAbove(999_999_991));
        Assert.Equal("Counter start=200 width=0 step=10", spec.ToString());
    }

    [Theory]
    [InlineData("kd201", "kd{$nr:201}")]
    [InlineData("kd0201", "kd{$nr:201:4}")]
    [InlineData("201", "{$nr:201}")]
    [InlineData("pracownik.00", "pracownik.{$nr:0:2}")]
    [InlineData("kd", null)]
    [InlineData("{givenName}1", null)]
    [InlineData("kd1234567890", null)]
    [InlineData("", null)]
    public void SuggestCounter_TurnsALiteralEndingInDigitsIntoACounter(string pattern, string? expected) =>
        Assert.Equal(expected, CreationPatternSet.SuggestCounter(pattern));

    [Fact]
    public void SuggestCounter_TheSuggestionCompilesAndRendersTheLiteral()
    {
        var suggestion = CreationPatternSet.SuggestCounter("kd0201")!;
        var set = Compile(Attr.SamAccountName, suggestion);

        Assert.True(set.IsValid);
        Assert.Equal(new CounterSpec(201, 4, 1), set.Counter);
        Assert.Equal("kd0201", set.Render(Attr.SamAccountName, Row(number: "201")));
    }

    // ------------------------------------------------------------------ grammar and functions

    [Theory]
    [InlineData("{givenName|lower}", "Łucja", "łucja")]
    [InlineData("{givenName|upper}", "Łucja", "ŁUCJA")]
    [InlineData("{givenName|title}", "ANNA-MARIA o'neill", "Anna-Maria O'Neill")]
    [InlineData("{givenName|capitalize}", "żaneta", "Żaneta")]
    [InlineData("{givenName|trim}", "  Ala  ", "Ala")]
    [InlineData("{givenName|nodiacritics}", "Łucja Żółć", "Lucja Zolc")]
    [InlineData("{givenName|nospaces}", "Jan Maria", "JanMaria")]
    [InlineData("{givenName|alnum}", "O'Neill-Żak 2", "ONeillŻak2")]
    [InlineData("{givenName|sam}", "Łucja Żak", "LucjaZak")]
    [InlineData("{givenName|first:1}", "Łucja", "Ł")]
    [InlineData("{givenName|last:3}", "Grzegorz", "orz")]
    [InlineData("{givenName|replace:ł:l}", "Michał", "Michal")]
    [InlineData("{givenName|default:brak}", "", "brak")]
    [InlineData("{ givenName | lower }", "Ewa", "ewa")]
    [InlineData("{givenName|first:1|sam|lower}", "Łucja", "l")]
    public void EveryFunction_RendersAsInTheBatchEditor(string pattern, string givenName, string expected)
    {
        var set = Compile(Attr.DisplayName, pattern);

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal(expected, set.Render(Attr.DisplayName, Row(Person(givenName, "Żak"))));
    }

    [Theory]
    [InlineData("{{tymczasowe}} {givenName}", "{tymczasowe} Łucja")]
    [InlineData("a}}b{{c", "a}b{c")]
    [InlineData("\\\\fs01\\home$\\{givenName}", "\\\\fs01\\home$\\Łucja")]
    [InlineData("{givenName|replace:\\|:-}", "Łu-cja")]
    [InlineData("{sn|default:a\\|b}", "a|b")]
    [InlineData("{givenName|replace:\\::.}", "Łu.cja")]
    public void BracesAndBackslashes_AreLiteralOutsideTokens_AndEscapesInside(string pattern, string expected)
    {
        var set = Compile(Attr.Description, pattern);
        var givenName = pattern.Contains("\\|:-", StringComparison.Ordinal) ? "Łu|cja" : pattern.Contains("\\::", StringComparison.Ordinal) ? "Łu:cja" : "Łucja";

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal(expected, set.Render(Attr.Description, Row(Person(givenName, string.Empty))));
    }

    [Theory]
    [InlineData("{givenName", 1, "{givenName")]
    [InlineData("ab}", 3, "}")]
    [InlineData("x{givenName|foo}", 2, "{givenName|foo}")]
    [InlineData("{a{b}", 1, "{a{b}")]
    [InlineData("{}", 1, "{}")]
    [InlineData("{giv en}", 1, "{giv en}")]
    [InlineData("{givenName|first:x}", 1, "{givenName|first:x}")]
    [InlineData("{$foo}", 1, "{$foo}")]
    [InlineData("{$suffix:x}", 1, "{$suffix:x}")]
    public void SyntaxErrors_HaveThePositionAndTheToken(string pattern, int position, string token)
    {
        var set = Compile(Attr.Description, pattern);

        var error = Assert.Single(set.Errors);
        Assert.Equal(PatternErrorCode.Syntax, error.Code);
        Assert.Equal(Attr.Description, error.Attribute);
        Assert.Equal(position, error.Position);
        Assert.Equal(token, error.Token);
        Assert.False(set.IsValid);
        Assert.False(set.CanRender(Attr.Description));
        Assert.Equal(string.Empty, set.Render(Attr.Description, Row(Person("Łucja", "Żak"))));
    }

    [Fact]
    public void PatternErrors_KeepNoEngineText_AndPrintCodeAttributeAndPositionOnly()
    {
        var set = Compile([new AttributeRule(Attr.Description, "x {$column:Nieznana}"), new AttributeRule(Attr.Info, "{$nr:abc}")], Headerless(3));

        Assert.Equal([PatternErrorCode.UnknownColumn, PatternErrorCode.InvalidCounter], set.Errors.Select(e => e.Code));
        Assert.Equal(["{$column:Nieznana}", "{$nr:abc}"], set.Errors.Select(e => e.Token));
        Assert.Equal([3, 1], set.Errors.Select(e => e.Position));
        Assert.All(set.Errors, e => Assert.Null(e.Suggestion));
        Assert.Equal("UnknownColumn in description at 3", set.Errors[0].ToString());
        Assert.DoesNotContain("Allowed", string.Concat(set.Errors.Select(e => e.Token)), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the creation tokens

    [Theory]
    [InlineData("kd{$nr:201}", "201", "kd201")]
    [InlineData("u{$nr:1:4}", "2", "u0002")]
    [InlineData("{$nr:200:0:10}", "210", "210")]
    [InlineData("kd{$nr:201}", "0207", "kd207")]
    [InlineData("kd{$nr:201}", CreationPatternTokens.NumberSentinel, "kd")]
    [InlineData("kd{$nr:201}", null, "kd")]
    [InlineData("{$nr|lower}-{$nr:1:3}", "7", "007-007")]
    public void Counter_WritesTheNumberWithEachTokensWidth(string pattern, string? number, string expected)
    {
        var set = Compile(Attr.SamAccountName, pattern);

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal(expected, set.Render(Attr.SamAccountName, Row(number: number)));
    }

    [Fact]
    public void Counter_OneSpecPerRuleSet_WidthsMayDiffer_TokensWithoutArgumentTakeTheSpec()
    {
        var set = Compile(
        [
            new(Attr.SamAccountName, "kd{$nr:201:4}"),
            new(Attr.EmployeeNumber, "{$nr:201}"),
            new(Attr.Description, "Konto nr {$nr}"),
        ]);

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal(new CounterSpec(201, 4, 1), set.Counter);
        Assert.Equal("kd0207", set.Render(Attr.SamAccountName, Row(number: "207")));
        Assert.Equal("207", set.Render(Attr.EmployeeNumber, Row(number: "207")));
        Assert.Equal("Konto nr 0207", set.Render(Attr.Description, Row(number: "207")));
    }

    [Fact]
    public void Counter_DefaultSpec_WithoutAnyArgument_AndNoCounterWithoutToken()
    {
        Assert.Equal(CounterSpec.Default, Compile(Attr.SamAccountName, "u{$nr}").Counter);
        Assert.Null(Compile(Attr.SamAccountName, "{givenName}").Counter);
    }

    [Fact]
    public void CounterMismatch_ATokenWithAnotherStartOrStep()
    {
        var set = Compile(
        [
            new(Attr.SamAccountName, "kd{$nr:201}"),
            new(Attr.Mail, "nr{$nr:1}@firma.pl"),
            new(Attr.EmployeeNumber, "{$nr:201:0:2}"),
        ]);

        Assert.Equal(
            [(PatternErrorCode.CounterMismatch, Attr.Mail, 3, "{$nr:1}"), (PatternErrorCode.CounterMismatch, Attr.EmployeeNumber, 1, "{$nr:201:0:2}")],
            set.Errors.Select(e => (e.Code, e.Attribute, e.Position, e.Token!)));
        Assert.False(set.IsValid);
        Assert.Equal(new CounterSpec(201, 0, 1), set.Counter);
    }

    [Theory]
    [InlineData("{$nr:abc}")]
    [InlineData("{$nr:1:10}")]
    [InlineData("{$nr:1:2:0}")]
    [InlineData("{$nr:}")]
    public void InvalidCounter_HasThePositionAndTheToken(string token)
    {
        var set = Compile(Attr.SamAccountName, "kd" + token);

        var error = Assert.Single(set.Errors);
        Assert.Equal((PatternErrorCode.InvalidCounter, 3, token), (error.Code, error.Position, error.Token));
    }

    [Theory]
    [InlineData("{$dn}")]
    [InlineData("{$rdn}")]
    [InlineData("{$counter}")]
    [InlineData("{$counter:000}")]
    [InlineData("{$DN|lower}")]
    public void ForbiddenTokens_DnRdnAndTheBatchCounter(string token)
    {
        var error = Assert.Single(Compile(Attr.Description, "x" + token).Errors);

        Assert.Equal((PatternErrorCode.ForbiddenToken, 2, token), (error.Code, error.Position, error.Token));
        Assert.True(error.IsError);
    }

    [Fact]
    public void SuffixMailDomainDomainAndParent()
    {
        var set = Compile(
        [
            new(Attr.Description, "{$suffix}|{$mailDomain}|{$domain}|{$parentName}"),
            new(Attr.Info, "{$parent}"),
        ]);

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal("contoso.com|firma.pl|contoso.test|Nowi pracownicy", set.Render(Attr.Description, Row(mailDomain: "firma.pl")));
        Assert.Equal("contoso.com|contoso.com|contoso.test|Nowi pracownicy", set.Render(Attr.Description, Row()));
        Assert.Equal(Container, set.Render(Attr.Info, Row()));
    }

    [Theory]
    [InlineData("{$date:yyyy-MM-dd}", "2026-10-13", false)]
    [InlineData("{$date:dd.MM.yyyy}", "13.10.2026", false)]
    [InlineData("{$date}", "2026-10-13", false)]
    [InlineData("{$date:d MMMM yyyy}", "13 October 2026", true)]
    [InlineData("{$date:ddd}", "Tue", true)]
    public void Date_OnTheRunClock_InvariantNamesWarn(string pattern, string expected, bool warns)
    {
        var set = Compile(Attr.Description, pattern);

        Assert.True(set.IsValid);
        Assert.Equal(expected, set.Render(Attr.Description, Row()));
        Assert.Equal(warns, set.Errors.Any(e => e.Code == PatternErrorCode.InvariantDateNames && !e.IsError && e.Position == 1));
    }

    [Theory]
    [InlineData(Attr.UserPrincipalName, true)]
    [InlineData(Attr.Mail, true)]
    [InlineData(Attr.SamAccountName, false)]
    public void Sam_InAnAddressRule_Warns(string attribute, bool warns)
    {
        var set = Compile(attribute, "{givenName|sam|lower}.{sn|nodiacritics|lower}");

        Assert.True(set.IsValid);
        Assert.Equal(warns, set.Errors.Any(e => e.Code == PatternErrorCode.SamInAddress && e.Position == 1 && e.Token == "{givenName|sam|lower}"));
    }

    [Fact]
    public void Column_ByExcelLetter_InAHeaderlessTable()
    {
        var set = Compile([new(Attr.Info, "{{tymczasowe}} {$column:C}"), new(Attr.Description, "{$column:c|upper}")], Headerless(3));

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        var row = Row(cells: new Dictionary<string, string> { ["C"] = "praktyki", ["c"] = "praktyki" });
        Assert.Equal("{tymczasowe} praktyki", set.Render(Attr.Info, row));
        Assert.Equal("PRAKTYKI", set.Render(Attr.Description, row));
    }

    [Theory]
    [InlineData("{$column:D}")]
    [InlineData("{$column}")]
    [InlineData("{$column: }")]
    public void UnknownColumn_ALetterOrNameTheTableDoesNotHave(string token)
    {
        var error = Assert.Single(Compile([new(Attr.Info, token)], Headerless(3)).Errors);

        Assert.Equal((PatternErrorCode.UnknownColumn, 1, token), (error.Code, error.Position, error.Token));
    }

    [Fact]
    public void Column_ByHeader_WithColonsDiacriticsAndEscapes()
    {
        var columns = new[]
        {
            new TableColumn(0, "A", "Godz.: od"),
            new TableColumn(1, "B", "Dział|Zespół"),
            new TableColumn(2, "C", "Umowa"),
        };
        var set = Compile(
        [
            new(Attr.Description, "{$column:Godz.: od}-{$column:Dział\\|Zespół}-{$column:dzial\\|zespol}"),
            new(Attr.Info, "Konto utworzone {$date:yyyy-MM-dd} – {$column:Umowa}"),
        ], columns);

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Godz.: od"] = "8:00", ["Dział|Zespół"] = "Kadry", ["dzial|zespol"] = "Kadry", ["Umowa"] = "zlecenie",
        };
        Assert.Equal("8:00-Kadry-Kadry", set.Render(Attr.Description, Row(cells: cells)));
        Assert.Equal("Konto utworzone 2026-10-13 – zlecenie", set.Render(Attr.Info, Row(cells: cells)));
    }

    [Fact]
    public void Column_PasswordLike_IsUnreachable_ByHeaderAndByLetter()
    {
        var columns = new[] { new TableColumn(0, "A", "Imię"), new TableColumn(1, "B", "Hasło startowe") };

        var byHeader = Compile([new(Attr.Info, "{$column:Hasło startowe}")], columns);
        var byLetter = Compile([new(Attr.Info, "{$column:B}")], columns);

        Assert.Equal(PatternErrorCode.UnknownColumn, Assert.Single(byHeader.Errors).Code);
        Assert.Equal(PatternErrorCode.UnknownColumn, Assert.Single(byLetter.Errors).Code);
        Assert.Equal(["Imię", "A"], CreationPatternTokens.ReachableColumnNames(columns));
    }

    [Fact]
    public void Extensions_AreTheFourCreationTokens()
    {
        var extensions = CreationPatternTokens.Extensions(Headerless(2));

        Assert.Equal(["nr", "column", "suffix", "mailDomain"], extensions.Select(e => e.Name));
        Assert.Equal(["A", "B"], extensions[1].AllowedArguments);
        Assert.Null(extensions[0].ValidateArgument!("201:4"));
        Assert.NotNull(extensions[0].ValidateArgument!("x"));
    }

    // ------------------------------------------------------------------ rules: duplicates, blocked, references, cycles, order

    [Fact]
    public void DuplicateRule_TheSecondRuleOfAnAttribute_IsRefused()
    {
        var set = Compile([new(Attr.Cn, "{givenName} {sn}"), new("CN", "{sn}")]);

        var error = Assert.Single(set.Errors);
        Assert.Equal((PatternErrorCode.DuplicateRule, Attr.Cn, 0), (error.Code, error.Attribute, error.Position));
        Assert.Equal("Łucja Żak", set.Render(Attr.Cn, Row(Person("Łucja", "Żak"))));
    }

    [Theory]
    [InlineData(Attr.UserAccountControl)]
    [InlineData(Attr.ProxyAddresses)]
    [InlineData(Attr.Manager)]
    [InlineData(Attr.UnicodePwd)]
    [InlineData("msExchHideFromAddressLists")]
    [InlineData("")]
    public void BlockedAttribute_IsNeverARuleTarget(string attribute)
    {
        var set = Compile([new(attribute, "x")]);

        var error = Assert.Single(set.Errors);
        Assert.Equal((PatternErrorCode.BlockedAttribute, 0), (error.Code, error.Position));
        Assert.False(set.HasRule(attribute));
    }

    [Fact]
    public void Cycle_NamesTheAttributes_AndBlocksThem()
    {
        var set = Compile(
        [
            new(Attr.Cn, "{displayName}"),
            new(Attr.DisplayName, "{cn}"),
            new(Attr.SamAccountName, "{givenName|sam|lower}"),
        ]);

        Assert.Equal(
            [(PatternErrorCode.Cycle, Attr.Cn, "cn → displayName → cn"), (PatternErrorCode.Cycle, Attr.DisplayName, "cn → displayName → cn")],
            set.Errors.Select(e => (e.Code, e.Attribute, e.Token!)));
        Assert.False(set.IsValid);
        Assert.False(set.CanRender(Attr.Cn));
        Assert.True(set.CanRender(Attr.SamAccountName));
        Assert.Equal([Attr.SamAccountName, Attr.Cn, Attr.DisplayName], set.EvaluationOrder);
    }

    [Fact]
    public void Cycle_ARuleThatReferencesItself()
    {
        var error = Assert.Single(Compile(Attr.Mail, "{mail}@firma.pl").Errors);

        Assert.Equal((PatternErrorCode.Cycle, "mail → mail"), (error.Code, error.Token));
    }

    [Fact]
    public void EvaluationOrder_TopologicalWithTheFixedTies()
    {
        var set = Compile(
        [
            new(Attr.Mail, "{sAMAccountName}@{$mailDomain}"),
            new(Attr.UserPrincipalName, "{sAMAccountName}"),
            new(Attr.Description, "{cn}"),
            new(Attr.SamAccountName, "kd{$nr:201}"),
            new(Attr.Cn, "{displayName}"),
            new(Attr.DisplayName, "{givenName} {sn}"),
            new(Attr.Department, "Kadry"),
        ]);

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal(
            [Attr.DisplayName, Attr.Cn, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.Department, Attr.Description],
            set.EvaluationOrder);
        Assert.Equal(new HashSet<string> { Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail }, set.CounterAttributes.ToHashSet());
        Assert.Equal([Attr.DisplayName, Attr.GivenName, Attr.Sn], set.DependsOn(Attr.Cn));
        Assert.Equal([Attr.DisplayName], set.DirectReferences(Attr.Cn));
        Assert.True(set.UsesCounterDirectly(Attr.SamAccountName));
        Assert.False(set.UsesCounterDirectly(Attr.Mail));
        Assert.True(set.IsFixed(Attr.Department));
        Assert.False(set.IsFixed(Attr.Cn));
    }

    [Fact]
    public void References_AreCaseInsensitive_AndCanonical()
    {
        var set = Compile([new("samaccountname", "{GIVENNAME|sam|lower}"), new(Attr.UserPrincipalName, "{SAMAccountName}")]);

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal([Attr.SamAccountName, Attr.UserPrincipalName], set.EvaluationOrder);
        Assert.Equal([Attr.SamAccountName], set.DirectReferences(Attr.UserPrincipalName));
        Assert.Equal("lucja", set.Render(Attr.SamAccountName, Row(Person("Łucja", "Żak"))));
    }

    [Fact]
    public void OverridesColumn_IsKeptPerRule()
    {
        var set = Compile([new(Attr.Mail, "{sAMAccountName}@firma.pl", OverridesColumn: true), new(Attr.Cn, "{sn}")]);

        Assert.True(set.OverridesColumn(Attr.Mail));
        Assert.False(set.OverridesColumn(Attr.Cn));
        Assert.False(set.OverridesColumn(Attr.Department));
    }

    [Fact]
    public void Render_WithoutARule_IsEmpty_AndReportsEmptyTokens()
    {
        var set = Compile(Attr.Cn, "{givenName} {initials} {sn}");
        var empty = new List<string>();

        Assert.Equal(string.Empty, set.Render(Attr.Mail, Row()));
        Assert.Equal("Łucja  Żak", set.Render(Attr.Cn, Row(Person("Łucja", "Żak")), empty.Add));
        Assert.Equal(["{initials}"], empty);
    }

    [Fact]
    public void UnknownAttribute_RendersEmpty_WithADidYouMeanFromTheSynonyms()
    {
        var set = Compile([new(Attr.Cn, "{imie} {sn}"), new(Attr.Description, "{dzial}")], [new TableColumn(0, "A", "Imię")]);

        Assert.True(set.IsValid);
        Assert.Equal(
            [(PatternErrorCode.UnknownAttribute, Attr.Cn, 1, "{imie}", "{givenName}"), (PatternErrorCode.UnknownAttribute, Attr.Description, 1, "{dzial}", "{department}")],
            set.Errors.Select(e => (e.Code, e.Attribute, e.Position, e.Token!, e.Suggestion!)));
        Assert.Equal(" Żak", set.Render(Attr.Cn, Row(Person("Łucja", "Żak"))));
    }

    [Fact]
    public void AllowedButUnprovidedAttribute_IsNoError_AndRendersEmpty()
    {
        var set = Compile(Attr.Description, "{department}/{title}");

        Assert.True(set.IsValid);
        Assert.Empty(set.Errors);
        Assert.Equal("/", set.Render(Attr.Description, Row()));
    }

    [Fact]
    public void Rendering_IsThreadSafe()
    {
        var set = Compile([.. BulkCreateTestBed.StandardRules]);
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 2000, i => results.Add(set.Render(Attr.SamAccountName, Row(Person("Łucja", i % 2 == 0 ? "Żak" : "Bąk")))));

        Assert.Equal(1000, results.Count(r => r == "lucja.zak"));
        Assert.Equal(1000, results.Count(r => r == "lucja.bak"));
    }

    // ------------------------------------------------------------------ the examples of 2.3 (render level)

    public static TheoryData<string, string, string, string, string?> Examples() => new()
    {
        { Attr.SamAccountName, "{givenName|sam|lower}.{sn|sam|lower}", "Łucja", "Żak", "lucja.zak" },
        { Attr.Mail, "{givenName|nodiacritics|nospaces|lower}.{sn|nodiacritics|nospaces|lower}@firma.pl", "Grzegorz", "Brzęczyszczykiewicz", "grzegorz.brzeczyszczykiewicz@firma.pl" },
        { Attr.Mail, "{givenName|nodiacritics|nospaces|lower}.{sn|nodiacritics|nospaces|lower}@firma.pl", "Anna", "Konstantynopolitańczykowa", "anna.konstantynopolitanczykowa@firma.pl" },
        { Attr.SamAccountName, "u{$nr:1:4}", "Łucja", "Żak", "u0001" },
        { Attr.EmployeeNumber, "{$nr:200:0:10}", "Łucja", "Żak", "200" },
        { Attr.SamAccountName, "kd{$nr:201}", "Łucja", "Żak", "kd201" },
        { Attr.Cn, "{givenName} {sn}", "Łucja", "Żak", "Łucja Żak" },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void Examples_OfTheNote(string attribute, string pattern, string givenName, string surname, string? expected)
    {
        var set = Compile(attribute, pattern);
        var number = set.Counter is { } counter ? counter.Start.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        Assert.Equal(expected, set.Render(attribute, Row(Person(givenName, surname), number: number)));
    }

    [Fact]
    public void Examples_HomeDirectoryAndPatternOnlyColumn()
    {
        var set = Compile(
        [
            new(Attr.SamAccountName, "kd{$nr:201}"),
            new(Attr.HomeDirectory, "\\\\fs01\\home$\\{sAMAccountName}"),
            new(Attr.Info, "{{tymczasowe}} {$column:C}"),
        ], Headerless(3));
        var values = new Dictionary<string, string> { [Attr.SamAccountName] = "kd201" };

        Assert.True(set.IsValid, string.Join(", ", set.Errors));
        Assert.Equal("\\\\fs01\\home$\\kd201", set.Render(Attr.HomeDirectory, Row(values)));
        Assert.Equal("{tymczasowe} praktyki", set.Render(Attr.Info, Row(cells: new Dictionary<string, string> { ["C"] = "praktyki" })));
    }

    [Fact]
    public void Records_PrintNoValue()
    {
        var row = Row(Person("Łucja", "Żak"), number: "201");
        var scope = new PatternScope(ObjectKind.User, Headerless(2), [Attr.GivenName], FakeSchema.Create());

        Assert.Equal("Pattern row (number=True, suffix=True, mail domain=False)", row.ToString());
        Assert.Equal("Pattern scope: User, 2 columns, 1 provided attributes", scope.ToString());
        Assert.Equal("Pattern set: 1 rules, 0 problems, counter=True", Compile(Attr.SamAccountName, "kd{$nr:201}").ToString());
    }
}
