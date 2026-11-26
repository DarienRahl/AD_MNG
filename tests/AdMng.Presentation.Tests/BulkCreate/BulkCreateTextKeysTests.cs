using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Localization;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The key families of the F3 contract block (docs/dev/phase3-bulk-create.md 2.9): every key of
/// <see cref="BulkCreateTextKeys.AllKeys"/> exists in both languages, and every issue code of <see cref="BulkCreateIssueCodes"/>
/// renders through <c>DisplayNames.IssueText</c> in Polish and English – with the attribute and arguments the code is raised
/// with (the XML docs of <see cref="BulkCreateIssueCodes"/>) – without an English fallback or an unfilled placeholder.
/// </summary>
public sealed partial class BulkCreateTextKeysTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    /// <summary>The codes whose text takes the attribute as <c>{0}</c> (raised with the attribute, no arguments).</summary>
    private static readonly HashSet<string> AttributeCodes = new(StringComparer.Ordinal)
    {
        BulkCreateIssueCodes.Disambiguated, BulkCreateIssueCodes.NameInRecycleBin, BulkCreateIssueCodes.ScientificNotation,
        BulkCreateIssueCodes.InvalidPhone, BulkCreateIssueCodes.LineBreakInValue, BulkCreateIssueCodes.AllCapsName,
    };

    /// <summary>The arguments each code is raised with (AA codes: <c>{1}</c>…; the others: <c>{0}</c>).</summary>
    private static readonly Dictionary<string, string[]> Arguments = new(StringComparer.Ordinal)
    {
        [BulkCreateIssueCodes.CounterTooLong] = ["20"],
        [BulkCreateIssueCodes.DuplicateInDirectory] = ["Jan Kowalski"],
        [BulkCreateIssueCodes.DuplicateRow] = ["12"],
        [BulkCreateIssueCodes.ImplicitUpnClash] = ["Łucja Żak"],
        [BulkCreateIssueCodes.EmptyToken] = ["{givenName|first:1}"],
        [BulkCreateIssueCodes.PossibleExistingAccount] = ["Ewa Wiśniewska"],
        [BulkCreateIssueCodes.DuplicateSourceRow] = ["7"],
        [BulkCreateIssueCodes.ManagerAmbiguous] = ["2"],
        [BulkCreateIssueCodes.GroupNotFound] = ["GG_Księgowość"],
        [BulkCreateIssueCodes.GroupAmbiguous] = ["GG_Nowi"],
        [BulkCreateIssueCodes.GroupScopeNotAllowed] = ["DL_Drukarki"],
        [BulkCreateIssueCodes.PrimaryGroupSkipped] = ["Domain Users"],
        [BulkCreateIssueCodes.GroupPasswordPolicy] = ["GG_Uprzywilejowani"],
        [BulkCreateIssueCodes.PasswordTooShortForDomain] = ["7"],
        [BulkCreateIssueCodes.JoinOwnerNotTrusted] = [@"CONTOSO\jan.kowalski"],
    };

    [Fact]
    public void EveryKeyOfTheFamilies_ExistsInBothLanguages()
    {
        var keys = BulkCreateTextKeys.AllKeys().ToList();
        var en = Load(CultureInfo.InvariantCulture);
        var pl = Load(CultureInfo.GetCultureInfo("pl"));

        Assert.True(keys.Count >= 139, $"{keys.Count} keys");
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([], keys.Where(k => !en.ContainsKey(k)));
        Assert.Equal([], keys.Where(k => !pl.ContainsKey(k)));
    }

    [Fact]
    public void KeyHelpers_RefuseValuesWithoutAText()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BulkCreateTextKeys.SuggestionNoteKey(SuggestionNote.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => BulkCreateTextKeys.FunctionKey("rot13"));
        Assert.Throws<ArgumentOutOfRangeException>(() => BulkCreateTextKeys.TitleKey(ObjectKind.OrganizationalUnit));
        Assert.Equal("BulkCreate.Title.Computer", BulkCreateTextKeys.TitleKey(ObjectKind.Computer));
        Assert.Equal("Issue.DuplicateRow", BulkCreateTextKeys.IssueKey(BulkCreateIssueCodes.DuplicateRow));
    }

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("en-US")]
    public void EveryIssueCode_RendersWithoutFallbackOrOpenPlaceholder(string culture)
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
        var schema = FakeSchema.Create(bulkCreate: true);
        foreach (var code in BulkCreateIssueCodes.All)
        {
            var attribute = BulkCreateIssueCodes.AttributeAndArguments.Contains(code) || AttributeCodes.Contains(code) ? "mobile" : null;
            var issue = new ValidationIssue(IssueSeverity.Warning, code, "english planner fallback", attribute)
            {
                Arguments = Arguments.TryGetValue(code, out var arguments) ? arguments : [],
            };

            var text = localizer.IssueText(issue, schema);

            Assert.False(string.IsNullOrWhiteSpace(text), code);
            Assert.NotEqual(issue.Message, text);
            Assert.DoesNotMatch(Placeholder(), text);
            if (attribute is not null)
            {
                Assert.Contains(localizer.AttributeName(attribute, schema), text, StringComparison.Ordinal);
            }

            foreach (var argument in issue.Arguments)
            {
                Assert.Contains(argument, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void OnlyTheDocumentedCodes_HaveAPlaceholder()
    {
        var pl = Load(CultureInfo.GetCultureInfo("pl"));
        var withPlaceholder = BulkCreateIssueCodes.All.Where(c => Placeholder().IsMatch(pl[BulkCreateTextKeys.IssueKey(c)])).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            [.. BulkCreateIssueCodes.AttributeAndArguments.Concat(AttributeCodes).Concat(Arguments.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            withPlaceholder.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FunctionDescriptions_AreThoseOf2_9()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("bez polskich znaków (ą→a, ł→l)", localizer[BulkCreateTextKeys.FunctionKey("nodiacritics")]);
        Assert.Equal("Najniższy wolny numer (uzupełnia luki)", localizer[BulkCreateTextKeys.CounterModeKey(CounterMode.FillGaps)]);
        Assert.Equal("Tylko we wzorcach", localizer[BulkCreateTextKeys.TargetKey(ColumnTargetKind.PatternOnly)]);
        Assert.Equal("Możliwe duplikaty", localizer[BulkCreateTextKeys.FilterKey(BulkCheckFilter.PossibleDuplicates)]);
        Assert.Equal("cały las (wykaz globalny na dc01.contoso.test)", localizer.Format(BulkCreateTextKeys.ScopeKey(UniquenessScope.Forest), "dc01.contoso.test"));
        Assert.Equal("Wiele kont…", localizer[L.BulkCreate.Command]);
    }

    [GeneratedRegex(@"\{\d+\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for '{culture.Name}'.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }
}
