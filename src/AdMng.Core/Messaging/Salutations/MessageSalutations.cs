using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Core.Templates;

namespace AdMng.Core.Messaging.Salutations;

/// <summary>
/// A value the planner derived for a message (U1d): the title, the gender and the salutation of the account (and of the
/// manager when the template uses the manager fields) with where it came from – the preview shows it and marks guesses.
/// Memory only, like the rendered message.
/// </summary>
/// <param name="Field"><see cref="MessageSalutations.TitleField"/>, <see cref="MessageSalutations.GenderField"/> … (manager: <c>manager…</c>).</param>
/// <param name="Value">The title text, the <see cref="Gender"/> name, or the rendered salutation.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="IsGuess">Not certain (gender from a name, title from the display name, a default gender).</param>
/// <param name="Detail">The word or value that decided (given name, surname, unrecognized title words), or null.</param>
public sealed record DerivedFieldValue(string Field, string Value, DerivedValueSource Source, bool IsGuess, string? Detail = null)
{
    /// <summary>The finding behind a fallback (<see cref="SalutationIssueCodes"/>), or null.</summary>
    public string? Issue { get; init; }

    /// <summary>A fallback was used (neutral text, conflict, default gender).</summary>
    public bool IsFallback => Source == DerivedValueSource.Fallback;

    public override string ToString() => $"{nameof(DerivedFieldValue)} {Field} {Source}";
}

/// <summary>
/// The salutation fields of one message (U1d): the account and its manager as <see cref="SalutationPerson"/> (computed on
/// first use), the evaluation of <c>{$title}</c>, <c>{$titledName}</c>, <c>{$salutation}</c>, <c>{$honorific}</c>,
/// <c>{$byGender}</c> and their manager twins, the findings of the fields actually used (added to the plan's issue list
/// while rendering) and the derived values of the preview. Not thread-safe (one message, one rendering).
/// </summary>
public sealed class MessageSalutations
{
    public const string TitleField = "title";
    public const string GenderField = "gender";
    public const string SalutationField = "salutation";
    public const string ManagerPrefix = "manager";

    private readonly DirectoryObject _account;
    private readonly DirectoryObject? _manager;
    private readonly Gender? _override;
    private readonly ISchemaCatalog? _schema;
    private readonly ICollection<MessageIssue>? _issues;
    private readonly Usage _accountUsage = new();
    private readonly Usage _managerUsage = new();
    private SalutationPerson? _accountPerson;
    private SalutationPerson? _managerPerson;

    /// <param name="account">The account.</param>
    /// <param name="manager">Its manager, or null.</param>
    /// <param name="options">Settings merged with the template's options.</param>
    /// <param name="accountOverride">Gender chosen in the preview for this run (<see cref="Gender.Unknown"/> = neutral), or null.</param>
    /// <param name="schema">Schema of the session (confidential attributes are never read).</param>
    /// <param name="issues">The message's issue list the findings are added to, or null.</param>
    public MessageSalutations(
        DirectoryObject account,
        DirectoryObject? manager,
        SalutationOptions options,
        Gender? accountOverride = null,
        ISchemaCatalog? schema = null,
        ICollection<MessageIssue>? issues = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _manager = manager;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _override = accountOverride;
        _schema = schema;
        _issues = issues;
    }

    public SalutationOptions Options { get; }

    public SalutationPerson Account => _accountPerson ??= SalutationPerson.From(_account, Options, _override, _schema);

    public SalutationPerson? Manager => _manager is null ? null : _managerPerson ??= SalutationPerson.From(_manager, Options, null, _schema);

    /// <summary>The salutation fields of a planned message: the settings of the sender merged with the template, the override of the request.</summary>
    public static MessageSalutations For(UserMessagePlanRequest request, DirectoryObject account, DirectoryObject? manager, ICollection<MessageIssue>? issues)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(account);
        var options = (request.Sender.Salutations ?? SalutationOptions.Default).With(request.Template.Salutation);
        return new MessageSalutations(account, manager, options, OverrideFor(request.GenderOverrides, account.DistinguishedName), request.Schema, issues);
    }

    /// <summary>The salutation fields of field data built without the planner (default options, no findings).</summary>
    public static MessageSalutations For(UserMessageFieldData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new MessageSalutations(data.Account, data.Manager, SalutationOptions.Default);
    }

    /// <summary>Evaluates a salutation field (<see cref="SalutationFields.IsSalutationField"/>) and records what it needed.</summary>
    public string Evaluate(string field, string? argument, MessageLanguage language)
    {
        ArgumentNullException.ThrowIfNull(field);
        var isManager = SalutationFields.IsManagerField(field);
        var person = isManager ? Manager : Account;
        if (person is null)
        {
            return string.Empty;
        }

        var baseField = SalutationFields.BaseField(field);
        var variant = SalutationResolver.ParseVariant(argument);
        var result = baseField switch
        {
            UserMessageFields.Title => SalutationResolver.Title(person, string.Equals(SalutationKey.Of(argument), "highest", StringComparison.Ordinal), Options),
            UserMessageFields.TitledName => SalutationResolver.TitledName(person, Options),
            UserMessageFields.Salutation => SalutationResolver.Salutation(person, language, variant, Options),
            UserMessageFields.Honorific => SalutationResolver.Honorific(person, language, SalutationResolver.ParseCase(argument) ?? GrammaticalCase.Nominative, Options),
            UserMessageFields.ByGender => SalutationResolver.ByGender(person, argument ?? string.Empty, Options),
            _ => new SalutationResult(string.Empty),
        };
        var usage = isManager ? _managerUsage : _accountUsage;
        usage.Used = true;
        if (baseField == UserMessageFields.Salutation && !usage.Salutations.Any(s => s.Variant == variant))
        {
            usage.Salutations.Add((variant, result));
        }

        Record(person, result);
        return result.Text;
    }

    /// <summary>
    /// The derived values of the persons whose fields were used (account first), for the preview: title and gender, and the
    /// salutation when a <c>{$salutation}</c> field of that person was used – the text of the fullest variant rendered
    /// (<see cref="SalutationVariant.Full"/> first), marked by the worst of the variants rendered.
    /// </summary>
    public IReadOnlyList<DerivedFieldValue> Derived()
    {
        var values = new List<DerivedFieldValue>();
        if (_accountUsage.Used)
        {
            values.AddRange(Derive(Account, string.Empty, _accountUsage));
        }

        if (_managerUsage.Used && Manager is { } manager)
        {
            values.AddRange(Derive(manager, ManagerPrefix, _managerUsage));
        }

        return values;
    }

    /// <summary>The override of <paramref name="distinguishedName"/> (exact key first, then case-insensitive), or null.</summary>
    public static Gender? OverrideFor(IReadOnlyDictionary<string, Gender>? overrides, string distinguishedName)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        if (overrides.TryGetValue(distinguishedName, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in overrides)
        {
            if (string.Equals(key, distinguishedName, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private IEnumerable<DerivedFieldValue> Derive(SalutationPerson person, string prefix, Usage usage)
    {
        string Name(string field) => prefix.Length == 0 ? field : prefix + char.ToUpperInvariant(field[0]) + field[1..];
        var title = person.Title;
        var titleText = Options.UseTitles ? title.Text : string.Empty;
        yield return new DerivedFieldValue(
            Name(TitleField),
            titleText,
            Options.UseTitles ? title.Source : DerivedValueSource.None,
            Options.UseTitles && title.Source == DerivedValueSource.DisplayName,
            title.Unrecognized.Count > 0 ? string.Join(' ', title.Unrecognized) : null)
        {
            Issue = title.Unrecognized.Count > 0 ? SalutationIssueCodes.TitleUnrecognized : null,
        };

        var gender = person.Gender;
        var (effective, isDefault) = SalutationResolver.EffectiveGender(gender, Options);
        yield return new DerivedFieldValue(
            Name(GenderField),
            effective.ToString(),
            isDefault || gender.IsConflict ? DerivedValueSource.Fallback : gender.Source,
            gender.IsGuess || isDefault,
            gender.Detail)
        {
            Issue = GenderIssue(gender),
        };

        if (usage.Salutations.Count == 0)
        {
            yield break;
        }

        var rendered = usage.Salutations.OrderBy(s => s.Variant).Select(s => SalutationValue(Name(SalutationField), person, s.Result, isDefault)).ToList();
        var shown = rendered[0];
        yield return shown with
        {
            Source = rendered.Any(r => r.IsFallback) ? DerivedValueSource.Fallback : shown.Source,
            IsGuess = rendered.Any(r => r.IsGuess),
            Issue = rendered.Select(r => r.Issue).OfType<string>().OrderByDescending(SalutationIssueCodes.Severity).FirstOrDefault(),
        };
    }

    /// <summary>The derived value of one rendered <c>{$salutation}</c> variant.</summary>
    private DerivedFieldValue SalutationValue(string field, SalutationPerson person, SalutationResult salutation, bool isDefault)
    {
        var title = person.Title;
        var gender = person.Gender;
        // A neutral text the operator chose (override) is no fallback.
        var fallback = salutation.SurnameMissing
            || (salutation.IsNeutral && gender.Source != DerivedValueSource.Override)
            || (salutation.UsedGender && (isDefault || gender.IsConflict));
        var source = fallback ? DerivedValueSource.Fallback
            : salutation.UsedTitle && !salutation.UsedGender ? title.Source
            : salutation.UsedGender ? gender.Source
            : title.Source;
        var guessed = (salutation.UsedGender && (gender.IsGuess || isDefault)) || (salutation.UsedTitle && title.Source == DerivedValueSource.DisplayName);
        return new DerivedFieldValue(field, salutation.Text, source, guessed)
        {
            Issue = salutation.SurnameMissing ? SalutationIssueCodes.SurnameMissing : salutation.UsedGender ? GenderIssue(gender) : null,
        };
    }

    /// <summary>The finding of a gender that a field needed, or null.</summary>
    private string? GenderIssue(GenderInfo gender)
    {
        if (gender.Source == DerivedValueSource.Override)
        {
            return null;
        }

        if (gender.IsConflict)
        {
            return SalutationIssueCodes.GenderConflict;
        }

        if (gender.Gender == Gender.Unknown)
        {
            return Options.UnknownGender == UnknownGenderMode.Neutral ? SalutationIssueCodes.GenderUnknown : SalutationIssueCodes.GenderGuessed;
        }

        return gender.IsGuess ? SalutationIssueCodes.GenderGuessed : null;
    }

    private void Record(SalutationPerson person, SalutationResult result)
    {
        if (_issues is null)
        {
            return;
        }

        var name = person.Name;
        if (result.UsedGender)
        {
            var gender = person.Gender;
            if (GenderIssue(gender) is { } code)
            {
                var argument = code == SalutationIssueCodes.GenderGuessed && gender.Detail is { Length: > 0 } detail ? detail : name;
                Add(code, argument);
            }

            if (gender.UnknownValue is { } value && gender.Source != DerivedValueSource.Override)
            {
                Add(SalutationIssueCodes.GenderValueUnknown, value);
            }
        }

        if (result.ConsultedTitle && Options.UseTitles)
        {
            var title = person.Title;
            if (title.Source == DerivedValueSource.DisplayName && title.HasTitle)
            {
                Add(SalutationIssueCodes.TitleFromDisplayName, title.Text);
            }

            if (title.Unrecognized.Count > 0)
            {
                Add(SalutationIssueCodes.TitleUnrecognized, string.Join(' ', title.Unrecognized));
            }
        }

        if (result.SurnameMissing)
        {
            Add(SalutationIssueCodes.SurnameMissing, name);
        }
    }

    private void Add(string code, string argument)
    {
        var issue = new MessageIssue(code, SalutationIssueCodes.Severity(code), argument);
        if (!_issues!.Contains(issue))
        {
            _issues.Add(issue);
        }
    }

    /// <summary>What the rendering of one person's fields used.</summary>
    private sealed class Usage
    {
        public bool Used { get; set; }

        /// <summary>The <c>{$salutation}</c> variants rendered with their results (each variant once).</summary>
        public List<(SalutationVariant Variant, SalutationResult Result)> Salutations { get; } = [];
    }
}

/// <summary>The salutation field names (<see cref="UserMessageFields"/>) and the attributes they read.</summary>
public static class SalutationFields
{
    /// <summary>The fields of the account.</summary>
    public static IReadOnlyList<string> AccountFields { get; } =
        [UserMessageFields.Title, UserMessageFields.TitledName, UserMessageFields.Salutation, UserMessageFields.Honorific, UserMessageFields.ByGender];

    /// <summary>The fields of the manager.</summary>
    public static IReadOnlyList<string> ManagerFields { get; } =
        [UserMessageFields.ManagerTitle, UserMessageFields.ManagerTitledName, UserMessageFields.ManagerSalutation, UserMessageFields.ManagerHonorific, UserMessageFields.ManagerByGender];

    public static bool IsSalutationField(string name) =>
        AccountFields.Contains(name, StringComparer.OrdinalIgnoreCase) || IsManagerField(name);

    public static bool IsManagerField(string name) => ManagerFields.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>"managerSalutation" → "salutation"; account fields unchanged.</summary>
    public static string BaseField(string name)
    {
        var index = ManagerFields.ToList().FindIndex(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            return AccountFields[index];
        }

        return AccountFields.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>The title and gender attributes to read for the accounts when <paramref name="tokens"/> use an account field.</summary>
    public static IReadOnlyList<string> AccountAttributes(IEnumerable<TemplateTokenUse> tokens, SalutationOptions? options) =>
        tokens.Any(t => AccountFields.Contains(t.Name, StringComparer.OrdinalIgnoreCase)) ? Attributes(options) : [];

    /// <summary>The title and gender attributes to read for the managers when <paramref name="tokens"/> use a manager field.</summary>
    public static IReadOnlyList<string> ManagerAttributes(IEnumerable<TemplateTokenUse> tokens, SalutationOptions? options) =>
        tokens.Any(t => IsManagerField(t.Name)) ? Attributes(options) : [];

    private static IReadOnlyList<string> Attributes(SalutationOptions? options)
    {
        options ??= SalutationOptions.Default;
        return [.. new[] { options.UsableTitleAttribute, options.UsableGenderAttribute }.OfType<string>()];
    }
}
