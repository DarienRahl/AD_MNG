using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// Step 3: the compiled rules (errors, quick fixes, the column-precedence notes), the <em>Numeracja</em> panel, the first
/// free number, <em>Wstaw pole</em> and the live samples (docs/dev/phase3-bulk-create.md 2.6).
/// </summary>
public sealed partial class BulkRulesViewModel
{
    /// <summary>Sample rows shown per rule (the longest value is added when it is not among them).</summary>
    internal const int SampleRows = 3;

    private readonly SerialCancellation _preview = new();
    private readonly SerialCancellation _samples = new();
    private CreationPatternSet? _compiled;
    private string? _selectedFor;
    private int? _firstFree;
    private int? _highest;
    private string? _previewPrefix;
    private string? _previewKey;
    private bool _previewChecked;
    private int _applying;
    private bool _syncingCounter;
    private bool _rewriting;

    /// <summary>The live samples being rendered (tests await it).</summary>
    internal Task SampleWork { get; private set; } = Task.CompletedTask;

    /// <summary>The counter preview being read (tests await it).</summary>
    internal Task PreviewWork { get; private set; } = Task.CompletedTask;

    /// <summary>The compiled rule set of the last change (null before the first compile).</summary>
    internal CreationPatternSet? Compiled => _compiled;

    /// <summary>Users have the UPN row (prefix pattern and suffix).</summary>
    public bool HasUpnRule => IsUserKind;

    /// <summary>The note above the rules: home folders are not created (only <c>homeDirectory</c> / <c>homeDrive</c> as attributes).</summary>
    public string HomeFoldersNote => Services.Localizer[L.BulkCreate.HomeFoldersNote];

    /// <summary>The hint of the <em>Numeracja</em> panel while no rule uses the counter (a pattern example, so read through <c>Format</c>).</summary>
    public string NoCounterHint => Services.Localizer.Format(L.BulkCreate.NoCounterHint);

    /// <summary>The UPN suffix of the profile is not in the forest any more (the next choice was taken).</summary>
    [ObservableProperty]
    public partial string? UpnSuffixWarning { get; private set; }

    private bool IsApplying => _applying > 0;

    private ISchemaCatalog Schema => Session()?.Schema ?? _openedSchema ?? SchemaCatalog.Empty;

    /// <summary>The attributes every kind gets a rule row for; an empty pattern takes the column (or leaves the value empty).</summary>
    internal static IReadOnlyList<string> CoreAttributes(ObjectKind kind) => kind switch
    {
        ObjectKind.User or ObjectKind.InetOrgPerson =>
            [Attr.Cn, Attr.DisplayName, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.Description],
        ObjectKind.Contact => [Attr.Cn, Attr.DisplayName, Attr.Mail, Attr.Description],
        ObjectKind.Group => [Attr.Cn, Attr.SamAccountName, Attr.Mail, Attr.Description],
        _ => [Attr.Cn, Attr.SamAccountName, Attr.Description],
    };

    /// <summary>The label of a rule's attribute (<em>Nazwa logowania (sprzed systemu Windows 2000) [sAMAccountName]</em>).</summary>
    internal string RuleLabel(string attribute) => attribute.Trim().Length == 0
        ? string.Empty
        : AttributeOptions.FirstOrDefault(o => string.Equals(o.LdapName, attribute.Trim(), StringComparison.OrdinalIgnoreCase))?.Text
            ?? new AttributeOption(attribute.Trim(), Services.Localizer.AttributeName(attribute.Trim(), Schema)).Text;

    /// <summary>The LDAP name of an attribute typed or picked in a rule's combo.</summary>
    internal string? ResolveAttribute(string? text) => BulkColumnsViewModel.ResolveAttribute(text, AttributeOptions, Schema);

    /// <summary>A rule's pattern, attribute or precedence changed (called by the rule).</summary>
    internal void OnRuleEdited(AttributeRuleViewModel rule)
    {
        if (IsApplying || _rewriting || _disposed)
        {
            return;
        }

        OnRulesEdited();
    }

    /// <summary>Something the rules depend on changed: compile, the counter panel, the fields, validation, samples and the preview.</summary>
    private void OnRulesEdited()
    {
        if (IsApplying || _disposed)
        {
            return;
        }

        Recompile();
        RefreshFields();
        Revalidate(raise: true);
        RefreshSamples();
        RefreshPreviewIfCounterChanged();
    }

    /// <summary>Compiles the rules against the table and updates every rule's error, quick fix, note and the counter panel.</summary>
    internal void Recompile()
    {
        if (_disposed)
        {
            return;
        }

        var schema = Schema;
        var table = Columns.Source.Table;
        var provided = ProvidedAttributes(schema);
        var rules = Rules
            .Where(r => r.Attribute.Trim().Length > 0 && r.Pattern.Trim().Length > 0)
            .Select(r => new AttributeRule(r.Attribute.Trim(), r.Pattern, r.OverridesColumn))
            .ToList();
        _compiled = CreationPatternSet.Compile(rules, new PatternScope(Kind, table.Columns, provided, schema));
        foreach (var rule in Rules)
        {
            UpdateRule(rule, schema, provided);
        }

        UpdateCounterPanel();
        var upn = Rules.FirstOrDefault(r => r.IsUpn);
        IsUpnFromPattern = upn is not null && (upn.Pattern.Contains('@', StringComparison.Ordinal)
            || _compiled.DirectReferences(Attr.UserPrincipalName).Any(a => string.Equals(a, Attr.Mail, StringComparison.OrdinalIgnoreCase)));
        Revalidate(raise: false);
    }

    /// <summary>Attributes fed by mapped columns (2.5: the references a pattern may use besides rules).</summary>
    private HashSet<string> ProvidedAttributes(ISchemaCatalog schema)
    {
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in Columns.Mappings)
        {
            switch (mapping.Target)
            {
                case ColumnTargetKind.Attribute when mapping.Attribute is { Length: > 0 } attribute:
                    provided.Add(schema.FindAttribute(attribute.Trim())?.LdapName ?? attribute.Trim());
                    break;
                case ColumnTargetKind.FullName:
                    provided.Add(Attr.GivenName);
                    provided.Add(Attr.Sn);
                    break;
                case ColumnTargetKind.Country:
                    provided.UnionWith([Attr.Country, Attr.CountryName, Attr.CountryCode]);
                    break;
                case ColumnTargetKind.ExpiresAt when IsUserKind:
                    provided.Add(Attr.AccountExpires);
                    break;
            }
        }

        return provided;
    }

    /// <summary>The column that feeds <paramref name="attribute"/> (its header or letter), or null.</summary>
    private string? FeedingColumn(string attribute) =>
        Columns.Mappings.FirstOrDefault(m =>
            (m.Target == ColumnTargetKind.Attribute && string.Equals(m.Attribute?.Trim(), attribute, StringComparison.OrdinalIgnoreCase))
            || (m.Target == ColumnTargetKind.FullName && (string.Equals(attribute, Attr.GivenName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(attribute, Attr.Sn, StringComparison.OrdinalIgnoreCase))))?.Name;

    private void UpdateRule(AttributeRuleViewModel rule, ISchemaCatalog schema, HashSet<string> provided)
    {
        var localizer = Services.Localizer;
        var attribute = rule.Attribute.Trim();
        var canonical = attribute.Length == 0 ? string.Empty : schema.FindAttribute(attribute)?.LdapName ?? attribute;
        var pattern = rule.Pattern;
        var hasPattern = pattern.Trim().Length > 0;
        var error = hasPattern && attribute.Length > 0 && _compiled is { } compiled
            ? compiled.Errors.Where(e => string.Equals(e.Attribute, canonical, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.IsError ? 0 : 1).FirstOrDefault()
            : null;
        if (hasPattern && attribute.Length == 0)
        {
            rule.Error = localizer[L.BulkCreate.ChooseRuleAttribute];
            rule.IsError = true;
            rule.Suggestion = null;
        }
        else if (error is not null)
        {
            rule.Error = localizer.Format(BulkCreateTextKeys.PatternErrorKey(error.Code), error.Position, error.Token ?? string.Empty);
            rule.IsError = error.IsError;
            rule.Suggestion = error.Suggestion is { Length: > 0 } suggestion ? localizer.Format(L.BulkCreate.PatternSuggestion, suggestion) : null;
        }
        else
        {
            rule.Error = null;
            rule.IsError = false;
            rule.Suggestion = null;
        }

        // A literal ending in digits (kd201) is never disambiguated: offer the counter instead.
        var fix = hasPattern && IsIdentifier(canonical) && error is null ? CreationPatternSet.SuggestCounter(pattern.Trim()) : null;
        rule.CounterFix = fix;
        if (fix is not null && CounterSpec.TryParse(CounterArgument(fix), out var spec, out _))
        {
            rule.Suggestion = localizer.Format(L.BulkCreate.CounterFixQuestion, spec.Start, fix);
        }

        rule.IsCounter = _compiled?.CounterAttributes.Contains(canonical, StringComparer.OrdinalIgnoreCase) == true;
        var column = canonical.Length == 0 ? null : FeedingColumn(canonical);
        rule.ColumnNote = column is null || !hasPattern
            ? null
            : localizer.Format(rule.OverridesColumn ? L.BulkCreate.RuleOverridesColumnNote : L.BulkCreate.ColumnPrecedenceNote, column);
        rule.EmptyHint = hasPattern
            ? null
            : column is not null
                ? localizer.Format(L.BulkCreate.ValueFromColumnHint, column)
                : provided.Contains(canonical) ? null : localizer[L.BulkCreate.NoRuleHint];
    }

    private static bool IsIdentifier(string attribute) =>
        attribute.Equals(Attr.SamAccountName, StringComparison.OrdinalIgnoreCase)
        || attribute.Equals(Attr.Cn, StringComparison.OrdinalIgnoreCase)
        || attribute.Equals(Attr.UserPrincipalName, StringComparison.OrdinalIgnoreCase)
        || attribute.Equals(Attr.Mail, StringComparison.OrdinalIgnoreCase);

    /// <summary>The argument of the first <c>{$nr…}</c> token of a pattern (<c>201</c> of <c>kd{$nr:201}</c>).</summary>
    private static string? CounterArgument(string pattern) =>
        PatternEditing.CounterTokens(pattern).Select(t => t.Argument).FirstOrDefault();

    // ---- Problems ---------------------------------------------------------------------------------------------

    /// <summary>Re-evaluates <see cref="Error"/>, <em>(zmieniony)</em> and the advanced summary; <paramref name="raise"/> tells the wizard.</summary>
    private void Revalidate(bool raise)
    {
        if (_disposed)
        {
            return;
        }

        Error = RulesProblem() ?? OptionsProblem() ?? PasswordError;
        MailDomainWarning = MailDomain is { Length: > 0 } domain && IsNonRoutable(domain)
            ? Services.Localizer.Format(L.BulkCreate.MailDomainNotRoutable, domain.Trim())
            : null;
        AdvancedSummary = BuildAdvancedSummary();
        RefreshModified();
        if (raise && !IsApplying)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The first rule problem that stops the step: an error-level pattern problem, a rule without an attribute, a name with no source.</summary>
    private string? RulesProblem()
    {
        var localizer = Services.Localizer;
        foreach (var rule in Rules)
        {
            if (rule.IsError && rule.Error is { } error)
            {
                return rule.Attribute.Trim().Length == 0 ? error : localizer.Format(L.BulkCreate.RuleProblem, rule.AttributeText, error);
            }
        }

        if (Columns.Source.Table.Columns.Count == 0)
        {
            return null;
        }

        var schema = Schema;
        var provided = ProvidedAttributes(schema);
        foreach (var required in RequiredAttributes())
        {
            var hasRule = Rules.Any(r => string.Equals(r.Attribute.Trim(), required, StringComparison.OrdinalIgnoreCase) && r.Pattern.Trim().Length > 0);
            if (!hasRule && !provided.Contains(required))
            {
                return localizer.Format(L.BulkCreate.NameSourceMissing, RuleLabel(required));
            }
        }

        return null;
    }

    /// <summary>The names every object needs a source for: the RDN, and the logon name of accounts and groups.</summary>
    private IEnumerable<string> RequiredAttributes()
    {
        yield return Attr.Cn;
        if (Kind != ObjectKind.Contact)
        {
            yield return Attr.SamAccountName;
        }
    }

    private static bool IsNonRoutable(string domain)
    {
        var trimmed = domain.Trim().TrimEnd('.');
        return trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildAdvancedSummary()
    {
        var localizer = Services.Localizer;
        var parts = new List<string> { localizer[BulkCreateTextKeys.CounterModeKey(CounterMode)] };
        if (AvoidRecycleBinNumbers)
        {
            parts.Add(localizer[L.BulkCreate.SummaryNoRecycleBin]);
        }

        parts.Add(localizer.Format(L.BulkCreate.SummaryCollisions, localizer[BulkCreateTextKeys.CollisionKey(Kind == ObjectKind.Computer ? CollisionStrategy.MarkError : Collisions)]));
        if (!SanitizeIdentifiers)
        {
            parts.Add(localizer[L.BulkCreate.SummaryNoSanitize]);
        }

        if (!TruncateNames)
        {
            parts.Add(localizer[L.BulkCreate.SummaryNoTruncate]);
        }

        if (AddPrimarySmtp)
        {
            parts.Add(localizer[L.BulkCreate.SummaryPrimarySmtp]);
        }

        if (Phones == PhoneFormat.International)
        {
            parts.Add(localizer[L.BulkCreate.SummaryInternationalPhones]);
        }

        if (!MatchExisting)
        {
            parts.Add(localizer[L.BulkCreate.SummaryNoMatching]);
        }

        return string.Join(" · ", parts);
    }

    // ---- Numeracja ----------------------------------------------------------------------------------------------

    /// <summary>The rule whose counter the panel shows: <c>sAMAccountName</c>, else <c>cn</c>, else the first rule with <c>{$nr}</c>.</summary>
    private AttributeRuleViewModel? PrimaryCounterRule()
    {
        var withCounter = Rules.Where(r => PatternEditing.CounterTokens(r.Pattern).Any()).ToList();
        return withCounter.FirstOrDefault(r => string.Equals(r.Attribute, Attr.SamAccountName, StringComparison.OrdinalIgnoreCase))
            ?? withCounter.FirstOrDefault(r => string.Equals(r.Attribute, Attr.Cn, StringComparison.OrdinalIgnoreCase))
            ?? withCounter.FirstOrDefault();
    }

    private void UpdateCounterPanel()
    {
        var spec = _compiled?.Counter;
        HasCounter = spec is not null;
        var prefix = PrimaryCounterRule() is { } rule ? PatternEditing.LiteralPrefix(rule.Pattern) : null;
        _syncingCounter = true;
        try
        {
            if (spec is { } counter)
            {
                CounterStart = counter.Start;
                CounterWidth = counter.Width;
                CounterStep = counter.Step;
            }

            CanEditPrefix = HasCounter && prefix is not null;
            CounterPrefix = prefix ?? string.Empty;
        }
        finally
        {
            _syncingCounter = false;
        }

        if (!HasCounter)
        {
            FirstFreeText = null;
        }
    }

    partial void OnCounterStartChanged(int value) => ApplyCounterPanel();

    partial void OnCounterWidthChanged(int value) => ApplyCounterPanel();

    partial void OnCounterStepChanged(int value) => ApplyCounterPanel();

    partial void OnCounterPrefixChanged(string value)
    {
        if (_syncingCounter || IsApplying || _disposed || !CanEditPrefix || PrimaryCounterRule() is not { } rule)
        {
            return;
        }

        rule.Pattern = PatternEditing.ReplacePrefix(rule.Pattern, value ?? string.Empty);
    }

    /// <summary>The panel rewrites every <c>{$nr…}</c> of the rules with the one counter (so they never disagree).</summary>
    private void ApplyCounterPanel()
    {
        if (_syncingCounter || IsApplying || _disposed || !HasCounter)
        {
            return;
        }

        var spec = new CounterSpec(
            Math.Clamp(CounterStart, 0, CounterSpec.MaxNumber),
            Math.Clamp(CounterWidth, 0, CounterSpec.MaxWidth),
            Math.Clamp(CounterStep, 1, CounterSpec.MaxStep));
        _rewriting = true;
        try
        {
            foreach (var rule in Rules)
            {
                rule.Pattern = PatternEditing.RewriteCounters(rule.Pattern, spec);
            }
        }
        finally
        {
            _rewriting = false;
        }

        OnRulesEdited();
    }

    // ---- First free number --------------------------------------------------------------------------------------

    /// <summary>What decides the first free number (the counter rules and the counter options).</summary>
    private string PreviewKey() => string.Join(
        "\u0001",
        Rules.Where(r => r.IsCounter).Select(r => r.Attribute + "\u0002" + r.Pattern)
            .Append(CounterMode.ToString()).Append(AvoidRecycleBinNumbers.ToString()).Append(UpnSuffix ?? string.Empty).Append(MailDomain ?? string.Empty));

    private void RefreshPreviewIfCounterChanged()
    {
        if (!string.Equals(_previewKey, PreviewKey(), StringComparison.Ordinal))
        {
            PreviewWork = RefreshPreviewAsync();
        }
    }

    /// <summary>
    /// <em>Pierwszy wolny numer: kd208 (najwyższy istniejący: kd207)</em> through <see cref="IBulkCreatePlanner.PreviewCounterAsync"/>
    /// on the window's session (cached in the wizard's lookups); <em>bez sprawdzenia w AD</em> while disconnected.
    /// </summary>
    private async Task RefreshPreviewAsync()
    {
        var token = _preview.Next();
        _previewKey = PreviewKey();
        if (!HasCounter || Session() is not { } session || Input() is not { } input)
        {
            _firstFree = null;
            _highest = null;
            _previewPrefix = null;
            _previewChecked = false;
            RefreshFirstFreeText(checkedInDirectory: false);
            RefreshSamples();
            return;
        }

        try
        {
            var lookups = Lookups();
            var preview = await Task.Run(() => Planner.PreviewCounterAsync(session, input, lookups, token), token).ConfigureAwait(true);
            if (!_preview.IsCurrent(token) || _disposed)
            {
                return;
            }

            _firstFree = preview.FirstFree;
            _highest = preview.Highest;
            _previewPrefix = preview.Prefix;
            _previewChecked = preview.FirstFree is not null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_preview.IsCurrent(token) || _disposed)
            {
                return;
            }

            _firstFree = null;
            _highest = null;
            _previewChecked = false;
        }

        RefreshFirstFreeText(_previewChecked);
        RefreshSamples();
    }

    private void RefreshFirstFreeText(bool checkedInDirectory)
    {
        if (_disposed)
        {
            return;
        }

        if (!HasCounter || _compiled?.Counter is not { } spec)
        {
            FirstFreeText = null;
            return;
        }

        var localizer = Services.Localizer;
        var prefix = _previewPrefix ?? CounterPrefix;
        if (!checkedInDirectory || _firstFree is not { } first)
        {
            FirstFreeText = localizer.Format(L.BulkCreate.FirstNumberUnchecked, prefix + spec.Format(spec.Start));
            return;
        }

        FirstFreeText = _highest is { } highest && highest >= 0 && highest <= int.MaxValue
            ? localizer.Format(L.BulkCreate.FirstFreeWithHighest, prefix + spec.Format(first), prefix + spec.Format((int)highest))
            : localizer.Format(L.BulkCreate.FirstFree, prefix + spec.Format(first));
    }

    // ---- Wstaw pole -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The entries of <em>Wstaw pole</em> by their Polish labels: mapped columns as their attribute (so they follow the check
    /// grid's edits), the rules' attributes, <c>{$column:…}</c> for <em>Tylko we wzorcach</em> and ignored columns (never a
    /// password-like one), the special tokens and the functions with their descriptions.
    /// </summary>
    private void RefreshFields()
    {
        if (_disposed)
        {
            return;
        }

        var localizer = Services.Localizer;
        var schema = Schema;
        var fields = new List<BulkTokenOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fieldGroup = localizer[L.BulkCreate.TokenGroupFields];
        void AddField(string attribute)
        {
            var name = schema.FindAttribute(attribute)?.LdapName ?? attribute;
            if (name.Length > 0 && seen.Add(name))
            {
                fields.Add(new BulkTokenOption(fieldGroup, localizer.AttributeName(name, schema), "{" + name + "}"));
            }
        }

        foreach (var mapping in Columns.Mappings)
        {
            if (mapping.Target == ColumnTargetKind.Attribute && mapping.Attribute is { Length: > 0 } attribute)
            {
                AddField(attribute.Trim());
            }
            else if (mapping.Target == ColumnTargetKind.FullName)
            {
                AddField(Attr.GivenName);
                AddField(Attr.Sn);
            }
        }

        foreach (var rule in Rules.Where(r => r.Attribute.Trim().Length > 0 && !r.IsUpn))
        {
            AddField(rule.Attribute.Trim());
        }

        var columnGroup = localizer[L.BulkCreate.TokenGroupColumns];
        foreach (var mapping in Columns.Mappings.Where(m => m.Target is ColumnTargetKind.PatternOnly or ColumnTargetKind.Ignore && !m.IsPasswordLike))
        {
            var header = mapping.Column.Header.Trim();
            var name = header.Length > 0 && header.IndexOfAny(['{', '}', '|', ':', '\\']) < 0 ? header : mapping.Column.Letter;
            fields.Add(new BulkTokenOption(columnGroup, localizer.Format(L.BulkCreate.TokenColumn, mapping.Name), "{$" + CreationPatternTokens.Column + ":" + name + "}"));
        }

        var specialGroup = localizer[L.BulkCreate.TokenGroupSpecial];
        var counter = _compiled?.Counter is { } spec ? "{$nr:" + PatternEditing.CounterArgumentText(spec) + "}" : "{$nr:1}";
        fields.Add(new BulkTokenOption(specialGroup, localizer[L.BulkCreate.TokenCounter], counter));
        if (IsUserKind)
        {
            fields.Add(new BulkTokenOption(specialGroup, localizer[L.BulkCreate.TokenSuffix], "{$" + CreationPatternTokens.Suffix + "}"));
        }

        fields.Add(new BulkTokenOption(specialGroup, localizer[L.BulkCreate.TokenMailDomain], "{$" + CreationPatternTokens.MailDomain + "}"));
        fields.Add(new BulkTokenOption(specialGroup, localizer[L.BulkCreate.TokenDomain], "{$domain}"));
        fields.Add(new BulkTokenOption(specialGroup, localizer[L.BulkCreate.TokenParentName], "{$parentName}"));
        fields.Add(new BulkTokenOption(specialGroup, localizer[L.BulkCreate.TokenDateIso], "{$date:yyyy-MM-dd}"));
        fields.Add(new BulkTokenOption(specialGroup, localizer[L.BulkCreate.TokenDatePolish], "{$date:dd.MM.yyyy}"));

        var functionGroup = localizer[L.BulkCreate.TokenGroupFunctions];
        foreach (var function in BulkCreateTextKeys.Functions)
        {
            var token = function switch
            {
                "first" or "last" => "|" + function + ":1",
                "replace" => "|replace:a:b",
                "default" => "|default:" + localizer[L.BulkCreate.TokenDefaultText],
                _ => "|" + function,
            };
            fields.Add(new BulkTokenOption(functionGroup, function, token, localizer[BulkCreateTextKeys.FunctionKey(function)]));
        }

        InsertableFields = fields;
    }

    // ---- Samples ------------------------------------------------------------------------------------------------

    /// <summary>Renders the live samples on the thread pool (the newest change wins); the texts are localized back on the UI thread.</summary>
    private void RefreshSamples()
    {
        if (_disposed)
        {
            return;
        }

        var token = _samples.Next();
        var job = CreateSampleJob();
        if (job is null)
        {
            foreach (var rule in Rules)
            {
                rule.Sample = string.Empty;
            }

            SampleWork = Task.CompletedTask;
            return;
        }

        SampleWork = RunSamplesAsync(job, token);
    }

    private async Task RunSamplesAsync(SampleJob job, CancellationToken token)
    {
        Dictionary<string, AttributeSamples> result;
        try
        {
            result = await Task.Run(() => job.Render(token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_samples.IsCurrent(token) || _disposed)
        {
            return;
        }

        foreach (var rule in Rules)
        {
            var attribute = Schema.FindAttribute(rule.Attribute.Trim())?.LdapName ?? rule.Attribute.Trim();
            rule.Sample = attribute.Length > 0 && result.TryGetValue(attribute, out var samples) ? SampleText(samples) : string.Empty;
        }
    }

    private string SampleText(AttributeSamples samples)
    {
        var localizer = Services.Localizer;
        string Text(SampleValue value)
        {
            var text = value.Value;
            if (value.Sanitized)
            {
                text += " " + localizer[L.BulkCreate.SampleSanitized];
            }

            if (value.Truncated)
            {
                text += " " + localizer[L.BulkCreate.SampleTruncated];
            }

            return text;
        }

        var parts = samples.First.Select(Text).ToList();
        if (samples.Longest is { } longest && !samples.LongestIsShown)
        {
            parts.Add(localizer.Format(L.BulkCreate.SampleLongest, Text(longest)));
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>A snapshot of everything the samples need (taken on the UI thread); null when nothing can be rendered.</summary>
    private SampleJob? CreateSampleJob()
    {
        var table = Columns.Source.Table;
        if (_compiled is not { } compiled || table.Rows.Count == 0)
        {
            return null;
        }

        var session = Session();
        var domain = session?.Info.DomainDnsName ?? UpnSuffixes.FirstOrDefault() ?? string.Empty;
        var upnSuffix = string.IsNullOrWhiteSpace(UpnSuffix) ? domain : UpnSuffix.Trim();
        var spec = compiled.Counter;
        var excluded = new HashSet<int>(Input()?.ExcludedRows ?? []);
        return new SampleJob
        {
            Kind = Kind,
            Table = table,
            Mappings = Columns.ToMappings(),
            Compiled = compiled,
            Schema = Schema,
            Options = new CreationFinishOptions(SanitizeIdentifiers, TruncateNames, Phones, IsCounterValue: false),
            UpnSuffix = upnSuffix,
            MailDomain = string.IsNullOrWhiteSpace(MailDomain) ? upnSuffix : MailDomain.Trim(),
            Domain = domain,
            ContainerDn = ContainerDn() ?? string.Empty,
            Now = TimeZoneInfo.ConvertTime(Services.Time.GetUtcNow(), Services.Time.LocalTimeZone),
            Excluded = excluded,
            FirstNumber = spec is { } counter ? _previewChecked && _firstFree is { } free ? free : counter.Start : null,
            Step = spec?.Step ?? 1,
        };
    }

    /// <summary>One rendered sample value with its marks.</summary>
    private sealed record SampleValue(string Value, bool Sanitized, bool Truncated)
    {
        public override string ToString() => $"Sample value: {Value.Length} characters";
    }

    /// <summary>The samples of one attribute: the first rows and the longest value.</summary>
    private sealed class AttributeSamples
    {
        public List<SampleValue> First { get; } = [];

        public SampleValue? Longest { get; set; }

        public bool LongestIsShown { get; set; }
    }

    /// <summary>
    /// The pure rendering of the samples: the rows' column values (case, phone format, the full name split), then the rules
    /// in evaluation order as the planner does (a non-empty column wins unless the rule overrides it), finished by the value
    /// rules; rows get the counter's numbers in order from the first free number. No directory access.
    /// </summary>
    private sealed class SampleJob
    {
        public required ObjectKind Kind { get; init; }

        public required InputTable Table { get; init; }

        public required IReadOnlyList<ColumnMapping> Mappings { get; init; }

        public required CreationPatternSet Compiled { get; init; }

        public required ISchemaCatalog Schema { get; init; }

        public required CreationFinishOptions Options { get; init; }

        public required string UpnSuffix { get; init; }

        public required string MailDomain { get; init; }

        public required string Domain { get; init; }

        public required string ContainerDn { get; init; }

        public required DateTimeOffset Now { get; init; }

        public required IReadOnlySet<int> Excluded { get; init; }

        public required int? FirstNumber { get; init; }

        public required int Step { get; init; }

        public Dictionary<string, AttributeSamples> Render(CancellationToken token)
        {
            var result = new Dictionary<string, AttributeSamples>(StringComparer.OrdinalIgnoreCase);
            var shown = 0;
            var position = 0;
            for (var index = 0; index < Table.Rows.Count; index++)
            {
                if ((index & 63) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                var row = Table.Rows[index];
                if (Excluded.Contains(row.Number))
                {
                    continue;
                }

                long? number = FirstNumber is { } first ? first + ((long)position * Step) : null;
                position++;
                var values = RenderRow(index, number is { } n && n <= CounterSpec.MaxNumber ? (int)n : null);
                var isShown = shown < SampleRows;
                foreach (var (attribute, value) in values)
                {
                    if (value.Value.Length == 0)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(attribute, out var samples))
                    {
                        samples = new AttributeSamples();
                        result[attribute] = samples;
                    }

                    if (isShown)
                    {
                        samples.First.Add(value);
                    }

                    if (samples.Longest is null || value.Value.Length > samples.Longest.Value.Length)
                    {
                        samples.Longest = value;
                        samples.LongestIsShown = isShown;
                    }
                }

                shown++;
            }

            return result;
        }

        private Dictionary<string, SampleValue> RenderRow(int index, int? number)
        {
            var values = new Dictionary<string, SampleValue>(StringComparer.OrdinalIgnoreCase);
            string? fullName = null;
            var order = NameOrder.GivenNameFirst;
            var nameCase = TextCase.AsIs;
            foreach (var mapping in Mappings)
            {
                var cell = Table.Cell(index, mapping.Column);
                if (cell.Length == 0)
                {
                    continue;
                }

                if (mapping.Kind == ColumnTargetKind.Attribute && mapping.Attribute is { Length: > 0 } attribute)
                {
                    var name = Schema.FindAttribute(attribute.Trim())?.LdapName ?? attribute.Trim();
                    if (!values.ContainsKey(name))
                    {
                        values[name] = new SampleValue(ColumnValue(name, cell, mapping.Case), false, false);
                    }
                }
                else if (mapping.Kind == ColumnTargetKind.FullName)
                {
                    fullName ??= cell;
                    order = mapping.Order;
                    nameCase = mapping.Case;
                }
            }

            if (fullName is not null)
            {
                var (givenName, surname) = CellText.SplitFullName(fullName, order);
                if (givenName.Length > 0)
                {
                    values.TryAdd(Attr.GivenName, new SampleValue(ColumnValue(Attr.GivenName, givenName, nameCase), false, false));
                }

                if (surname.Length > 0)
                {
                    values.TryAdd(Attr.Sn, new SampleValue(ColumnValue(Attr.Sn, surname, nameCase), false, false));
                }
            }

            var numberText = number?.ToString(CultureInfo.InvariantCulture);
            var row = new PatternRow
            {
                Attribute = name => values.TryGetValue(name, out var value) && value.Value.Length > 0 ? [value.Value] : [],
                Column = name => Table.FindColumn(name) is { } column && IsReachable(column) ? Table.Cell(index, column.Index) : string.Empty,
                Number = numberText,
                UpnSuffix = UpnSuffix,
                MailDomain = MailDomain,
                Domain = Domain,
                ContainerDn = ContainerDn,
                Now = Now,
            };
            foreach (var attribute in Compiled.EvaluationOrder)
            {
                if ((values.TryGetValue(attribute, out var existing) && existing.Value.Length > 0 && !Compiled.OverridesColumn(attribute))
                    || !Compiled.CanRender(attribute))
                {
                    continue;
                }

                var raw = Compiled.Render(attribute, row);
                if (attribute.Equals(Attr.UserPrincipalName, StringComparison.OrdinalIgnoreCase) && raw.Length > 0
                    && !raw.Contains('@', StringComparison.Ordinal) && UpnSuffix.Length > 0)
                {
                    raw += "@" + UpnSuffix;
                }

                if (raw.Length == 0)
                {
                    continue;
                }

                var isCounter = numberText is not null && Compiled.CounterAttributes.Contains(attribute, StringComparer.OrdinalIgnoreCase);
                var finished = CreationValues.Finish(Kind, attribute, raw, Options with { IsCounterValue = isCounter });
                var value = finished.Value;
                if (Kind == ObjectKind.Computer && attribute.Equals(Attr.SamAccountName, StringComparison.OrdinalIgnoreCase)
                    && value.Length > 0 && !value.EndsWith('$'))
                {
                    value += "$";
                }

                values[attribute] = new SampleValue(value, finished.Sanitized, finished.Truncated);
            }

            return values;
        }

        private string ColumnValue(string attribute, string cell, TextCase textCase)
        {
            var value = textCase == TextCase.AsIs ? cell : CellText.ApplyCase(cell, textCase);
            return Options.Phones == PhoneFormat.International && CreationValues.IsPhoneAttribute(attribute)
                ? CellText.NormalizePhone(value, PhoneFormat.International)
                : value;
        }

        private static bool IsReachable(TableColumn column) => column.Header.Trim().Length == 0 || !ColumnSynonyms.IsPasswordLike(column.Header.Trim());
    }
}

/// <summary>
/// Text edits of patterns for step 3 (2.6): token positions as the template engine splits them (<c>{{</c> / <c>}}</c> are
/// literal braces outside a token, a backslash escapes inside one), insertion at the caret, the counter rewrite of the
/// <em>Numeracja</em> panel and the literal prefix before the counter.
/// </summary>
internal static class PatternEditing
{
    /// <summary>A token of a pattern: the index of its <c>{</c> and of its <c>}</c> (−1 when unclosed).</summary>
    internal readonly record struct TokenSpan(int Start, int End)
    {
        public override string ToString() => $"Token {Start.ToString(CultureInfo.InvariantCulture)}–{End.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>A counter token: its span, its argument (<c>201:3</c>, or null) and its functions (<c>|lower</c>, or empty).</summary>
    internal readonly record struct CounterToken(TokenSpan Span, string? Argument, string Functions)
    {
        public override string ToString() => $"Counter token at {Span.Start.ToString(CultureInfo.InvariantCulture)}";
    }

    public static IReadOnlyList<TokenSpan> Tokens(string pattern)
    {
        var tokens = new List<TokenSpan>();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if ((c == '{' || c == '}') && i + 1 < pattern.Length && pattern[i + 1] == c)
            {
                i += 2;
                continue;
            }

            if (c != '{')
            {
                i++;
                continue;
            }

            var start = i++;
            var end = -1;
            while (i < pattern.Length)
            {
                if (pattern[i] == '\\' && i + 1 < pattern.Length)
                {
                    i += 2;
                    continue;
                }

                if (pattern[i] == '}')
                {
                    end = i;
                    i++;
                    break;
                }

                i++;
            }

            tokens.Add(new TokenSpan(start, end));
            if (end < 0)
            {
                break;
            }
        }

        return tokens;
    }

    /// <summary>The <c>{$nr…}</c> tokens of a pattern.</summary>
    public static IEnumerable<CounterToken> CounterTokens(string pattern)
    {
        foreach (var span in Tokens(pattern ?? string.Empty))
        {
            if (span.End < 0)
            {
                continue;
            }

            var body = pattern![(span.Start + 1)..span.End];
            var head = "$" + CreationPatternTokens.Number;
            if (!body.StartsWith(head, StringComparison.Ordinal) || (body.Length > head.Length && body[head.Length] is not (':' or '|')))
            {
                continue;
            }

            var rest = body[head.Length..];
            var bar = rest.IndexOf('|', StringComparison.Ordinal);
            var argumentPart = bar < 0 ? rest : rest[..bar];
            var functions = bar < 0 ? string.Empty : rest[bar..];
            yield return new CounterToken(span, argumentPart.StartsWith(':') ? argumentPart[1..] : null, functions);
        }
    }

    /// <summary>The argument text of a counter: <c>201</c>, <c>201:3</c>, <c>201:0:2</c>.</summary>
    public static string CounterArgumentText(CounterSpec spec)
    {
        var text = spec.Start.ToString(CultureInfo.InvariantCulture);
        if (spec.Width > 0 || spec.Step != 1)
        {
            text += ":" + spec.Width.ToString(CultureInfo.InvariantCulture);
        }

        if (spec.Step != 1)
        {
            text += ":" + spec.Step.ToString(CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>Every <c>{$nr…}</c> of <paramref name="pattern"/> written with <paramref name="spec"/> (functions kept).</summary>
    public static string RewriteCounters(string pattern, CounterSpec spec)
    {
        var tokens = CounterTokens(pattern).ToList();
        if (tokens.Count == 0)
        {
            return pattern;
        }

        var builder = new StringBuilder(pattern);
        foreach (var token in tokens.OrderByDescending(t => t.Span.Start))
        {
            var replacement = "{$" + CreationPatternTokens.Number + ":" + CounterArgumentText(spec) + token.Functions + "}";
            builder.Remove(token.Span.Start, token.Span.End - token.Span.Start + 1).Insert(token.Span.Start, replacement);
        }

        return builder.ToString();
    }

    /// <summary>The literal text before the first counter token (<c>kd</c> of <c>kd{$nr:201}</c>); null when a token comes first.</summary>
    public static string? LiteralPrefix(string pattern)
    {
        var counter = CounterTokens(pattern).Select(t => (TokenSpan?)t.Span).FirstOrDefault();
        if (counter is not { } span || Tokens(pattern).Any(t => t.Start < span.Start))
        {
            return null;
        }

        return pattern[..span.Start].Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
    }

    /// <summary>Replaces the literal text before the first counter token (braces doubled).</summary>
    public static string ReplacePrefix(string pattern, string prefix)
    {
        var counter = CounterTokens(pattern).Select(t => (TokenSpan?)t.Span).FirstOrDefault();
        if (counter is not { } span || Tokens(pattern).Any(t => t.Start < span.Start))
        {
            return pattern;
        }

        var escaped = prefix.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
        return escaped + pattern[span.Start..];
    }

    /// <summary>
    /// Inserts <paramref name="token"/> at <paramref name="caret"/>: a field or special token as it is; a function
    /// (<c>|lower</c>) into the token around the caret or right before it, before its closing brace. Returns the new
    /// pattern and the caret after the insertion.
    /// </summary>
    public static (string Pattern, int Caret) Insert(string pattern, int caret, string token)
    {
        pattern ??= string.Empty;
        caret = Math.Clamp(caret, 0, pattern.Length);
        if (token.StartsWith('|'))
        {
            foreach (var span in Tokens(pattern))
            {
                var end = span.End < 0 ? pattern.Length : span.End;
                if (caret > span.Start && caret <= end + 1)
                {
                    var at = span.End < 0 ? pattern.Length : span.End;
                    return (pattern.Insert(at, token), at + token.Length + (span.End < 0 ? 0 : 1));
                }
            }
        }

        return (pattern.Insert(caret, token), caret + token.Length);
    }
}
