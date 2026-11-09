using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Batch.Creation;

// ComputeAsync (docs/dev/phase3-bulk-create.md 2.5): the run checks, the rows' base values, the rules in evaluation order and
// the final assembly of the check grid. Counters, uniqueness and references are in their own partials.
public sealed partial class BulkCreatePlanner
{
    /// <summary>One computation (or counter preview) of one input: all state of a run, never shared between runs.</summary>
    private sealed partial class ComputeRun
    {
        private static readonly string[] NonRoutableMailDomains = [".local", ".lan", ".internal", ".localdomain", ".corp"];

        private readonly BulkCreatePlanner _planner;
        private readonly IDirectorySession _session;
        private readonly BulkCreateInput _input;
        private readonly BulkCreateLookups _lookups;
        private readonly CancellationToken _cancellationToken;
        private readonly ObjectKind _kind;
        private readonly BulkCreateRules _rules;
        private readonly ISchemaCatalog _schema;
        private readonly InputTable _table;
        private readonly List<ValidationIssue> _runIssues = [];
        private readonly string _windowDomain;
        private readonly string _upnSuffix;
        private readonly string _mailDomain;
        private readonly DateTimeOffset _now;
        private readonly string _containerDn;
        private readonly HashSet<string> _released;
        private readonly List<RowState> _rows = [];
        private readonly ConcurrentDictionary<string, int> _columnByName = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<int>? _reachableColumns;
        private CreationPatternSet _patterns = null!;
        private ForestInfo _forest;
        private UniquenessScope _scope = UniquenessScope.Domain;
        private DomainPasswordPolicy? _policy;
        private bool? _isPdc;
        private bool _containerExists = true;

        public ComputeRun(BulkCreatePlanner planner, IDirectorySession session, BulkCreateInput input, BulkCreateLookups lookups, CancellationToken cancellationToken)
        {
            _planner = planner;
            _session = session;
            _input = input;
            _lookups = lookups;
            _cancellationToken = cancellationToken;
            _rules = input.Rules ?? throw new ArgumentException("The input has no rules.", nameof(input));
            _kind = _rules.Kind;
            _schema = session.Schema;
            _table = input.Table ?? throw new ArgumentException("The input has no table.", nameof(input));
            _windowDomain = session.Info.DomainDnsName;
            _upnSuffix = string.IsNullOrWhiteSpace(_rules.UpnSuffix) ? _windowDomain : _rules.UpnSuffix.Trim();
            _mailDomain = string.IsNullOrWhiteSpace(_rules.MailDomain) ? _upnSuffix : _rules.MailDomain.Trim();
            _now = TimeZoneInfo.ConvertTime(planner._time.GetUtcNow(), planner._time.LocalTimeZone);
            _containerDn = Ldap.DistinguishedName.TryParse(input.ContainerDn, out var container) ? container.ToString() : input.ContainerDn ?? string.Empty;
            _released = new HashSet<string>(input.ReleasedValues ?? [], StringComparer.OrdinalIgnoreCase);
            _forest = new ForestInfo(1, [_windowDomain]) { DomainNamingContexts = [session.Info.DefaultNamingContext] };
        }

        private int Parallelism => Math.Clamp(_input.MaxParallelLookups, 1, 16);

        /// <summary>A cached run read of the lookups; this run's token tells its own cancellation from another run's.</summary>
        private Task<T> ReadOnceAsync<T>(string key, Func<Task<T>> read) => _lookups.ReadOnceAsync(key, read, _cancellationToken);

        public async Task<BulkCreateComputation> ExecuteAsync(IProgress<int>? progress)
        {
            await RunChecksAsync().ConfigureAwait(false);
            CompilePatterns();
            BuildRows();
            progress?.Report(0);
            EvaluateInitial();
            _cancellationToken.ThrowIfCancellationRequested();
            await AllocateCountersAsync().ConfigureAwait(false);
            await ResolveUniquenessAsync().ConfigureAwait(false);
            await ResolveReferencesAsync().ConfigureAwait(false);
            await FindExistingAccountsAsync().ConfigureAwait(false);
            var rows = Assemble(progress);
            return new BulkCreateComputation
            {
                Input = _input,
                Rows = rows,
                Attributes = GridAttributes(rows),
                Server = _session.Info.Server,
                Scope = _scope,
                Issues = Distinct(_runIssues),
                PasswordPolicy = _policy,
                ForestDomains = _forest.DomainDnsNames,
                IsPdcEmulator = _isPdc,
                ComputedAt = _planner._time.GetUtcNow(),
            };
        }

        // ------------------------------------------------------------------ run checks (2.5 step 1)

        private async Task RunChecksAsync()
        {
            if (_containerDn.Length == 0 || !Ldap.DistinguishedName.TryParse(_containerDn, out _))
            {
                _containerExists = false;
                _runIssues.Add(Error(IssueCodes.TargetNotFound, "The target container is not a valid DN."));
            }
            else
            {
                var container = await ReadOnceAsync("container\u0001" + _containerDn,
                    () => _session.GetAsync(_containerDn, [Attr.ObjectClass], _cancellationToken)).ConfigureAwait(false);
                if (container is null)
                {
                    _containerExists = false;
                    _runIssues.Add(Error(IssueCodes.TargetNotFound, "The target container does not exist."));
                }
                else
                {
                    var permissions = await ReadPermissionsAsync(_containerDn).ConfigureAwait(false);
                    if (!permissions.CanCreate(CreationNames.StructuralClassOf(_kind)))
                    {
                        _runIssues.Add(Error(IssueCodes.CannotCreateChild, "The window's account cannot create objects of this kind in the container."));
                    }
                }
            }

            CheckOptions();
            if (_schema.Attributes.Count == 0)
            {
                _runIssues.Add(Warning(BulkCreateIssueCodes.SchemaUnavailable, "The schema is not loaded: only the common attributes are accepted."));
            }

            await ReadForestAsync().ConfigureAwait(false);
            await ReadDomainAsync().ConfigureAwait(false);
            await ReadOptionGroupsAsync().ConfigureAwait(false);
            await CheckRidPoolAsync().ConfigureAwait(false);
            await CheckJoinOwnerAsync().ConfigureAwait(false);
        }

        private void CheckOptions()
        {
            var options = _input.Options;
            if (IsUserKind(_kind) && options.Enabled && options.PasswordMode == InitialPasswordMode.None)
            {
                _runIssues.Add(Error(BulkCreateIssueCodes.EnabledWithoutPassword, "Enabled accounts need a password."));
            }

            var setsPasswords = (IsUserKind(_kind) && options.PasswordMode != InitialPasswordMode.None) || _kind == ObjectKind.Computer;
            if (setsPasswords && !_session.Info.IsChannelEncrypted)
            {
                _runIssues.Add(Error(BulkCreateIssueCodes.PasswordsNeedEncryptedChannel, "Passwords can only be set over an encrypted connection."));
            }

            if (IsUserKind(_kind) && options.PasswordMode == InitialPasswordMode.Generate
                && options.GeneratedLength is < Security.PasswordGenerator.MinimumLength or > Security.PasswordGenerator.MaximumLength)
            {
                _runIssues.Add(Error(IssueCodes.InvalidValue,
                    $"Generated passwords must be {Text(Security.PasswordGenerator.MinimumLength)} to {Text(Security.PasswordGenerator.MaximumLength)} characters long."));
            }
        }

        private async Task ReadForestAsync()
        {
            ForestInfo? forest;
            try
            {
                forest = await ReadOnceAsync("forest", () => ForestScope.ReadAsync(_session, _cancellationToken)).ConfigureAwait(false);
            }
            catch (DirectoryException)
            {
                forest = null;
            }

            if (forest is { DomainCount: > 0 })
            {
                _forest = forest;
            }

            _scope = forest switch
            {
                { DomainCount: 1 } => UniquenessScope.ForestSingleDomain,
                { DomainCount: > 1 } when _lookups.ForestReader is not null => UniquenessScope.Forest,
                _ => UniquenessScope.Domain,
            };
            if (_scope == UniquenessScope.Domain)
            {
                _runIssues.Add(Warning(BulkCreateIssueCodes.CheckedInDomainOnly, "UPN and address uniqueness is checked in the domain only."));
            }
        }

        /// <summary>The domain object: the password policy and the PDC emulator role (2.5).</summary>
        private async Task ReadDomainAsync()
        {
            var domainNc = _session.Info.DefaultNamingContext;
            DirectoryObject? domain;
            try
            {
                domain = await ReadOnceAsync("domain",
                    () => _session.GetAsync(domainNc, ["minPwdLength", "pwdProperties", "fSMORoleOwner"], _cancellationToken)).ConfigureAwait(false);
            }
            catch (DirectoryException)
            {
                domain = null;
            }

            if (domain?.GetInt32("minPwdLength") is { } minimum)
            {
                _policy = new DomainPasswordPolicy(minimum, ((domain.GetInt32("pwdProperties") ?? 0) & 1) != 0);
            }

            _isPdc = IsThisServer(domain?.GetString("fSMORoleOwner"));
            var options = _input.Options;
            if (IsUserKind(_kind) && options.PasswordMode == InitialPasswordMode.Generate && _policy is { } policy
                && options.GeneratedLength < policy.MinimumLength)
            {
                _runIssues.Add(Error(BulkCreateIssueCodes.PasswordTooShortForDomain, "The generated passwords are shorter than the domain minimum.",
                    null, Text(policy.MinimumLength)));
            }
        }

        /// <summary>Whether an NTDS Settings DN (an fSMORoleOwner value) names the window's DC; null when unknown.</summary>
        private bool? IsThisServer(string? ntdsSettingsDn)
        {
            if (!Ldap.DistinguishedName.TryParse(ntdsSettingsDn, out var settings) || settings.Parent is not { IsRoot: false } server)
            {
                return null;
            }

            var host = _session.Info.Server.Split('.')[0];
            return string.Equals(server.Name, host, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<EffectivePermissions> ReadPermissionsAsync(string dn)
        {
            try
            {
                return await ReadOnceAsync("permissions\u0001" + Key(dn),
                    () => _session.GetEffectivePermissionsAsync(dn, _cancellationToken)).ConfigureAwait(false);
            }
            catch (DirectoryException)
            {
                return EffectivePermissions.Unknown;
            }
        }

        // ------------------------------------------------------------------ patterns

        private void CompilePatterns()
        {
            var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in _input.Mappings)
            {
                switch (mapping.Kind)
                {
                    case ColumnTargetKind.Attribute when mapping.Attribute is { Length: > 0 } attribute:
                        provided.Add(Canonical(attribute));
                        break;
                    case ColumnTargetKind.FullName:
                        provided.Add(Attr.GivenName);
                        provided.Add(Attr.Sn);
                        break;
                    case ColumnTargetKind.Country:
                        provided.UnionWith([Attr.Country, Attr.CountryName, Attr.CountryCode]);
                        break;
                    case ColumnTargetKind.ExpiresAt when IsUserKind(_kind):
                        provided.Add(Attr.AccountExpires);
                        break;
                }
            }

            foreach (var edits in _input.Edits.Values)
            {
                provided.UnionWith(edits.Keys.Where(k => !IsSpecialEdit(k)).Select(Canonical));
            }

            _patterns = CreationPatternSet.Compile(_rules.Attributes, new PatternScope(_kind, _table.Columns, provided, _schema));
            foreach (var error in _patterns.Errors)
            {
                var message = $"Pattern problem {error.Code} at position {Text(error.Position)}.";
                _runIssues.Add(error.IsError
                    ? Error(IssueCodes.TemplateError, message, error.Attribute)
                    : Warning(IssueCodes.TemplateError, message, error.Attribute));
            }
        }

        private string Canonical(string attribute) =>
            _schema.FindAttribute(attribute.Trim()) is { } definition ? definition.LdapName : attribute.Trim();

        private static bool IsSpecialEdit(string key) =>
            Is(key, ManagerEditKey) || Is(key, Attr.ManagedBy) || Is(key, GroupsEditKey);

        // ------------------------------------------------------------------ rows: base values (2.5 step 2)

        private void BuildRows()
        {
            for (var index = 0; index < _table.Rows.Count; index++)
            {
                var row = _table.Rows[index];
                var state = new RowState(row, index);
                if (_input.CreatedRows.TryGetValue(row.Number, out var createdDn))
                {
                    state.Fixed = BulkRowStatus.Created;
                    state.CreatedDn = createdDn;
                }
                else if (_input.ExcludedRows.Contains(row.Number))
                {
                    state.Fixed = BulkRowStatus.Excluded;
                }

                if (_input.PinnedNumbers.TryGetValue(row.Number, out var pin))
                {
                    state.Pin = pin;
                }

                BuildBase(state);
                if (state.Fixed is null && _input.FrozenValues is { } frozen && frozen.TryGetValue(row.Number, out var values))
                {
                    state.IsFrozen = true;
                    foreach (var value in values)
                    {
                        state.Base[value.Attribute] = new Draft(value.Attribute, value.Values, BulkValueSource.Frozen)
                        {
                            Adjusted = value.Adjusted,
                            Sanitized = value.Sanitized,
                            Truncated = value.Truncated,
                        };
                    }

                    foreach (var attribute in state.Base.Keys.Where(k => !values.Any(v => Is(v.Attribute, k))).ToList())
                    {
                        state.Base.Remove(attribute);
                    }

                    state.CounterNumber = state.Pin;
                }

                _rows.Add(state);
            }
        }

        private void BuildBase(RowState state)
        {
            string? fullName = null;
            var nameOrder = NameOrder.GivenNameFirst;
            var nameCase = TextCase.AsIs;
            string? country = null;
            string? expires = null;
            foreach (var mapping in _input.Mappings)
            {
                var cell = _table.Cell(state.Index, mapping.Column);
                switch (mapping.Kind)
                {
                    case ColumnTargetKind.Attribute when mapping.Attribute is { Length: > 0 } attribute:
                        var canonical = Canonical(attribute);
                        if (!CreationValues.IsBlockedAttribute(canonical, _kind, _schema) && !state.Base.ContainsKey(canonical) && cell.Length > 0)
                        {
                            AddColumnValue(state, canonical, cell, mapping.Case, BulkValueSource.Column);
                        }

                        break;
                    case ColumnTargetKind.FullName when cell.Length > 0:
                        fullName ??= cell;
                        nameOrder = mapping.Order;
                        nameCase = mapping.Case;
                        break;
                    case ColumnTargetKind.Manager when cell.Length > 0:
                        state.ManagerText ??= cell;
                        break;
                    case ColumnTargetKind.Groups when cell.Length > 0:
                        state.GroupsText ??= cell;
                        break;
                    case ColumnTargetKind.Country when cell.Length > 0:
                        country ??= cell;
                        break;
                    case ColumnTargetKind.ExpiresAt when cell.Length > 0:
                        expires ??= cell;
                        break;
                }
            }

            if (fullName is not null)
            {
                var (givenName, surname) = CellText.SplitFullName(fullName, nameOrder);
                if (givenName.Length > 0 && !state.Base.ContainsKey(Attr.GivenName))
                {
                    AddColumnValue(state, Attr.GivenName, givenName, nameCase, BulkValueSource.Column);
                }

                if (surname.Length > 0 && !state.Base.ContainsKey(Attr.Sn))
                {
                    AddColumnValue(state, Attr.Sn, surname, nameCase, BulkValueSource.Column);
                }
            }

            if (_input.Edits.TryGetValue(state.Number, out var edits))
            {
                foreach (var (key, raw) in edits)
                {
                    var value = CellText.Normalize(raw ?? string.Empty);
                    if (Is(key, ManagerEditKey) || Is(key, Attr.ManagedBy))
                    {
                        state.ManagerText = value;
                    }
                    else if (Is(key, GroupsEditKey))
                    {
                        state.GroupsText = value;
                    }
                    else if (Is(key, Attr.Country) || Is(key, Attr.CountryName) || Is(key, Attr.CountryCode))
                    {
                        country = value;
                        state.CountryEdited = true;
                    }
                    else if (Is(key, Attr.AccountExpires))
                    {
                        expires = value;
                        state.ExpiresEdited = true;
                    }
                    else
                    {
                        var attribute = Canonical(key);
                        if (CreationValues.IsBlockedAttribute(attribute, _kind, _schema))
                        {
                            continue;
                        }

                        state.Base.Remove(attribute);
                        state.Edited.Add(attribute);
                        AddColumnValue(state, attribute, value, TextCase.AsIs, BulkValueSource.Edited);
                    }
                }
            }

            if (!string.IsNullOrEmpty(country))
            {
                AddCountry(state, country);
            }

            if (!string.IsNullOrEmpty(expires) && IsUserKind(_kind))
            {
                AddExpiry(state, expires);
            }
        }

        private void AddColumnValue(RowState state, string attribute, string cell, TextCase textCase, BulkValueSource source)
        {
            if (cell.Length == 0)
            {
                if (source == BulkValueSource.Edited)
                {
                    state.Base[attribute] = new Draft(attribute, [], source);
                }

                return;
            }

            var value = textCase == TextCase.AsIs ? cell : CellText.ApplyCase(cell, textCase);
            if (_rules.Phones == PhoneFormat.International && CreationValues.IsPhoneAttribute(attribute))
            {
                value = CellText.NormalizePhone(value, PhoneFormat.International);
            }

            // description is multi-valued in the schema, but SAM takes one value on users, groups and computers (ADUC writes one,
            // 2.2): a "|" in it is text, not a separator.
            IReadOnlyList<string> values = value.Contains('|', StringComparison.Ordinal) && !Is(attribute, Attr.Description)
                && _schema.FindAttribute(attribute) is { IsSingleValued: false }
                ? CellText.SplitValues(value)
                : [value];
            state.Base[attribute] = new Draft(attribute, [.. values.Select(v => ApplyComputerRules(attribute, v))], source)
            {
                ColumnCase = source == BulkValueSource.Column ? textCase : null,
            };
        }

        /// <summary>The <c>Country</c> target (2.5): <c>c</c>, <c>co</c> and <c>countryCode</c> from a name or a code.</summary>
        private void AddCountry(RowState state, string text)
        {
            var source = state.CountryEdited ? BulkValueSource.Edited : BulkValueSource.Column;
            var countries = _input.Countries;
            if (countries.TryGetValue(text.Trim(), out var country) || countries.TryGetValue(ColumnSynonyms.Fold(text), out country))
            {
                state.Base[Attr.Country] = new Draft(Attr.Country, [country.Code], source);
                state.Base[Attr.CountryName] = new Draft(Attr.CountryName, [country.Name], source);
                state.Base[Attr.CountryCode] = new Draft(Attr.CountryCode, [Text(country.NumericCode)], source);
                return;
            }

            state.Add(Warning(BulkCreateIssueCodes.CountryNotFound, "The country is not known; nothing is written.", Attr.Country));
        }

        /// <summary>The <c>ExpiresAt</c> target (2.5): the end of the given local day as <c>accountExpires</c>.</summary>
        private void AddExpiry(RowState state, string text)
        {
            if (ParseDate(text) is not { } date)
            {
                state.Add(Error(BulkCreateIssueCodes.InvalidDate, "The expiry date is not in a supported format.", Attr.AccountExpires));
                return;
            }

            var source = state.ExpiresEdited ? BulkValueSource.Edited : BulkValueSource.Column;
            var fileTime = AdConvert.ToFileTime(EndOfDay(date, _planner._time.LocalTimeZone));
            state.Base[Attr.AccountExpires] = new Draft(Attr.AccountExpires, [fileTime.ToString(CultureInfo.InvariantCulture)], source);
        }

        /// <summary><c>dd.MM.yyyy</c>, <c>d.M.yyyy</c>, <c>yyyy-MM-dd</c> or an Excel serial date (2.5).</summary>
        internal static DateOnly? ParseDate(string text)
        {
            var value = text.Trim();
            string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd"];
            if (DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }

            // A serial has at least five whole digits (10000 = 18.05.1927): "31.12" or "12.2026" (a day and month, a month and year)
            // is not serial 31 or 12 in January 1900 – an account that expired before it was created.
            var serialText = value.Replace(',', '.');
            var wholeDigits = serialText.IndexOf('.') is var point and >= 0 ? point : serialText.Length;
            if (wholeDigits >= 5 && serialText.All(c => char.IsAsciiDigit(c) || c == '.')
                && double.TryParse(serialText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var serial)
                && serial < 2958466)
            {
                return DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));
            }

            return null;
        }

        /// <summary>The end of <paramref name="date"/> in <paramref name="zone"/>: the next local midnight (as the batch editor's expiration editor).</summary>
        internal static DateTimeOffset EndOfDay(DateOnly date, TimeZoneInfo zone)
        {
            var midnight = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
        }

        // ------------------------------------------------------------------ rules (2.3)

        /// <summary>
        /// The first values of the rows: the rules rendered with the counter sentinel – a row whose values contain it needs a
        /// number (its prefix and suffix, 2.4) – and the reservations of the operator's values.
        /// </summary>
        private void EvaluateInitial()
        {
            foreach (var state in _rows)
            {
                if (state.IsFrozen)
                {
                    state.Values = new Dictionary<string, Draft>(state.Base, StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                if (state.Fixed is not null)
                {
                    state.CounterNumber = state.Pin;
                    state.Values = Evaluate(state, state.Pin is { } pin ? Text(pin) : null, null, null);
                    continue;
                }

                if (_patterns.Counter is null)
                {
                    state.Values = Evaluate(state, null, null, null);
                    continue;
                }

                state.Values = Evaluate(state, SentinelText, null, null);
                state.NeedsNumber = state.CounterDependent.Count > 0;
                if (state.NeedsNumber)
                {
                    ChooseSequence(state);
                }
            }

            // The operator's values of every row, and every value of the rows this window created: they hold their names even
            // when the cached lookups predate their creation.
            foreach (var state in _rows.Where(r => r.Fixed is null or BulkRowStatus.Created))
            {
                var created = state.Fixed == BulkRowStatus.Created;
                foreach (var key in Keys(state.Values, d => created || d.IsOperatorValue))
                {
                    ReserveOperator(key, state.Number);
                }
            }
        }

        /// <summary>
        /// Renders the rules of a row in evaluation order with <paramref name="number"/> (digits, the sentinel or null) and the
        /// disambiguators of its roots; an edit wins, then a non-empty mapped column (unless the rule overrides it), then the rule.
        /// With the sentinel the row's counter-dependent attributes and their prefixes and suffixes are recorded.
        /// </summary>
        private Dictionary<string, Draft> Evaluate(RowState state, string? number, IReadOnlyDictionary<string, int>? bumps, List<(string Attribute, string Token)>? emptyTokens)
        {
            var values = new Dictionary<string, Draft>(state.Base, StringComparer.OrdinalIgnoreCase);
            var sentinelPass = number == SentinelText;
            if (sentinelPass)
            {
                state.CounterDependent.Clear();
                state.Parts.Clear();
                state.Unsplittable.Clear();
            }

            foreach (var attribute in _patterns.EvaluationOrder)
            {
                if (state.Edited.Contains(attribute))
                {
                    continue;
                }

                var column = values.TryGetValue(attribute, out var existing) && existing.HasValue ? existing : null;
                if ((column is not null && !_patterns.OverridesColumn(attribute)) || !_patterns.CanRender(attribute))
                {
                    continue;
                }

                var dependent = sentinelPass
                    ? _patterns.UsesCounterDirectly(attribute) || _patterns.DirectReferences(attribute).Any(state.CounterDependent.Contains)
                    : number is not null && state.CounterDependent.Contains(attribute);
                List<string>? empty = emptyTokens is null ? null : [];
                var raw = ComposeUpn(attribute, _patterns.Render(attribute, RowFor(state, values, number), empty is null ? null : empty.Add));
                var (value, sanitized, truncated) = sentinelPass && dependent
                    ? FinishWithSentinel(state, attribute, raw)
                    : Finish(attribute, raw, dependent);
                var adjusted = false;
                if (bumps is not null && bumps.TryGetValue(attribute, out var bump) && value.Length > 0)
                {
                    value = CreationNames.AppendDisambiguator(attribute, value, bump, DisambiguatorLimit(attribute));
                    adjusted = true;
                }

                if (sentinelPass && dependent)
                {
                    state.CounterDependent.Add(attribute);
                }

                if (value.Length == 0)
                {
                    if (column is null)
                    {
                        values.Remove(attribute);
                    }
                }
                else
                {
                    values[attribute] = new Draft(attribute, [value], _patterns.IsFixed(attribute) ? BulkValueSource.Fixed : BulkValueSource.Pattern)
                    {
                        Sanitized = sanitized,
                        Truncated = truncated,
                        Adjusted = adjusted,
                        IsCounter = dependent,
                    };
                }

                if (empty is { Count: > 0 } && IsIdentifier(attribute))
                {
                    emptyTokens!.AddRange(empty.Select(t => (attribute, t)));
                }
            }

            return values;
        }

        private static bool IsIdentifier(string attribute) =>
            Is(attribute, Attr.Cn) || Is(attribute, Attr.SamAccountName) || Is(attribute, Attr.UserPrincipalName) || Is(attribute, Attr.Mail);

        private PatternRow RowFor(RowState state, Dictionary<string, Draft> values, string? number) => new()
        {
            Attribute = name => values.TryGetValue(name, out var draft) ? draft.Values : [],
            Column = name => ColumnCell(state, name),
            Number = number,
            UpnSuffix = _upnSuffix,
            MailDomain = _mailDomain,
            Domain = _windowDomain,
            ContainerDn = _containerDn,
            Now = _now,
        };

        /// <summary>The cell of a column by header or letter; a password-like column is unreachable (2.3).</summary>
        private string ColumnCell(RowState state, string name)
        {
            var index = _columnByName.GetOrAdd(name, n => _table.FindColumn(n) is { } column && ReachableColumns.Contains(column.Index) ? column.Index : -1);
            return index < 0 ? string.Empty : _table.Cell(state.Index, index);
        }

        private HashSet<int> ReachableColumns => _reachableColumns ??=
        [
            .. _table.Columns.Where(c => c.Header.Trim().Length == 0 || !ColumnSynonyms.IsPasswordLike(c.Header.Trim())).Select(c => c.Index),
        ];

        /// <summary>The UPN rule renders the prefix: <c>prefix@suffix</c>, unless the prefix already is a whole address (2.3).</summary>
        private string ComposeUpn(string attribute, string raw) =>
            Is(attribute, Attr.UserPrincipalName) && raw.Length > 0 && !raw.Contains('@', StringComparison.Ordinal) && _upnSuffix.Length > 0
                ? raw + "@" + _upnSuffix
                : raw;

        private (string Value, bool Sanitized, bool Truncated) Finish(string attribute, string raw, bool isCounter)
        {
            if (raw.Length == 0)
            {
                return (string.Empty, false, false);
            }

            var finished = CreationValues.Finish(_kind, attribute, raw, new CreationFinishOptions(_rules.SanitizeIdentifiers, _rules.TruncateNames, _rules.Phones, isCounter));
            return (ApplyComputerRules(attribute, finished.Value), finished.Sanitized, finished.Truncated);
        }

        /// <summary>
        /// A counter-dependent value rendered with the sentinel: split at it, both parts finished separately (2.4), the computer
        /// rules applied to the whole; a value with no or several sentinels (a function removed or repeated it) is unsplittable.
        /// </summary>
        private (string Value, bool Sanitized, bool Truncated) FinishWithSentinel(RowState state, string attribute, string raw)
        {
            var first = raw.IndexOf(SentinelText, StringComparison.Ordinal);
            if (first < 0 || raw.IndexOf(SentinelText, first + 1, StringComparison.Ordinal) >= 0)
            {
                state.Unsplittable.Add(attribute);
                return Finish(attribute, raw.Replace(SentinelText, string.Empty, StringComparison.Ordinal), isCounter: true);
            }

            var prefix = Finish(attribute, raw[..first], isCounter: true);
            var suffix = Finish(attribute, raw[(first + 1)..], isCounter: true);
            var joined = ApplyComputerRules(attribute, prefix.Value + SentinelText + suffix.Value);
            var at = joined.IndexOf(SentinelText, StringComparison.Ordinal);
            state.Parts[attribute] = (joined[..at], joined[(at + 1)..]);
            return (joined, prefix.Sanitized || suffix.Sanitized, false);
        }

        /// <summary>Computers (2.2): <c>cn</c> and <c>sAMAccountName</c> upper case, the account name with its <c>$</c> – applied to every source.</summary>
        private string ApplyComputerRules(string attribute, string value)
        {
            if (_kind != ObjectKind.Computer || value.Length == 0)
            {
                return value;
            }

            if (Is(attribute, Attr.Cn))
            {
                return value.ToUpperInvariant();
            }

            if (Is(attribute, Attr.SamAccountName))
            {
                var upper = value.ToUpperInvariant();
                return upper.EndsWith('$') ? upper : upper + "$";
            }

            return value;
        }

        private int DisambiguatorLimit(string attribute) => attribute switch
        {
            _ when Is(attribute, Attr.DisplayName) => (int)(_schema.FindAttribute(Attr.DisplayName)?.RangeUpper ?? 256),
            _ => LimitOf(attribute, _kind),
        };

        // ------------------------------------------------------------------ assembly (2.5 steps 2 and 5)

        private List<BulkCreateRow> Assemble(IProgress<int>? progress)
        {
            var result = new List<BulkCreateRow>(_rows.Count);
            var mandatory = MandatoryAttributes();
            var done = 0;
            foreach (var state in _rows)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                result.Add(state.Fixed is { } status ? AssembleFixed(state, status) : AssembleRow(state, mandatory));
                if (++done % 250 == 0)
                {
                    progress?.Report(done);
                }
            }

            progress?.Report(done);
            return result;
        }

        private BulkCreateRow AssembleFixed(RowState state, BulkRowStatus status) => new()
        {
            Number = state.Number,
            Status = status,
            Values = Ordered(state.Values),
            DistinguishedName = status == BulkRowStatus.Created ? state.CreatedDn : null,
            CounterNumber = state.CounterNumber,
        };

        private BulkCreateRow AssembleRow(RowState state, IReadOnlyList<string> mandatory)
        {
            if (!state.IsFrozen && _patterns.EvaluationOrder.Count > 0)
            {
                var emptyTokens = new List<(string Attribute, string Token)>();
                _ = Evaluate(state, state.CounterNumber is { } number ? Text(number) : null, state.Bumps, emptyTokens);
                foreach (var (attribute, token) in emptyTokens.Distinct())
                {
                    state.Add(Warning(BulkCreateIssueCodes.EmptyToken, "A token of an identifier rendered empty.", attribute, token));
                }
            }

            foreach (var draft in state.Values.Values.Where(d => d.HasValue))
            {
                // Wave-1 merge (2.15, B2 as built): switch to B1's overload
                // CreationValues.Validate(_kind, draft.Attribute, draft.Values, _schema, _rules.Phones, draft.ColumnCase).
                foreach (var issue in CreationValues.Validate(_kind, draft.Attribute, draft.Values, _schema))
                {
                    state.Add(issue.Attribute is null ? issue with { Attribute = draft.Attribute } : issue);
                }
            }

            CheckDomains(state);
            foreach (var attribute in mandatory)
            {
                if (!state.Values.TryGetValue(attribute, out var draft) || !draft.HasValue)
                {
                    state.Add(Error(IssueCodes.MandatoryAttribute, "A mandatory attribute has no value.", attribute));
                }
            }

            foreach (var identifier in new[] { Attr.UserPrincipalName, Attr.Mail })
            {
                if (_patterns.HasRule(identifier) && (!state.Values.TryGetValue(identifier, out var draft) || !draft.HasValue)
                    && !state.Edited.Contains(identifier))
                {
                    state.Add(Error(IssueCodes.MandatoryAttribute, "The identifier rendered empty.", identifier));
                }
            }

            string? dn = null;
            if (_containerExists && state.Values.TryGetValue(Attr.Cn, out var cn) && cn.HasValue)
            {
                try
                {
                    dn = Ldap.DistinguishedName.Parse(_containerDn).Child("CN", cn.First).ToString();
                }
                catch (FormatException)
                {
                    state.Add(Error(IssueCodes.InvalidValue, "The name cannot be an RDN.", Attr.Cn));
                }
            }

            var issues = Distinct(state.Issues);
            var status = issues.Any(i => i.Severity == IssueSeverity.Error)
                ? BulkRowStatus.Error
                : issues.Any(i => i.Severity == IssueSeverity.Warning) ? BulkRowStatus.Warning : BulkRowStatus.Ok;
            return new BulkCreateRow
            {
                Number = state.Number,
                Status = status,
                Values = Ordered(state.Values),
                DistinguishedName = dn,
                CounterNumber = state.CounterNumber,
                ManagerDn = state.ManagerDn,
                ManagerCandidates = state.ManagerCandidates,
                GroupDns = state.GroupDns,
                MatchDns = state.MatchDns,
                Issues = issues,
            };
        }

        /// <summary>The UPN suffix and the mail domain against the forest's domains and UPN suffixes (2.2, 2.4).</summary>
        private void CheckDomains(RowState state)
        {
            var known = new HashSet<string>(_forest.DomainDnsNames.Concat(_input.UpnSuffixes).Append(_windowDomain), StringComparer.OrdinalIgnoreCase);
            if (state.Values.TryGetValue(Attr.UserPrincipalName, out var upn) && upn.HasValue && DomainOf(upn.First) is { } suffix && !known.Contains(suffix))
            {
                state.Add(Warning(BulkCreateIssueCodes.UpnSuffixNotInForest, "The UPN suffix is not a domain or UPN suffix of the forest.", Attr.UserPrincipalName));
            }

            if (state.Values.TryGetValue(Attr.Mail, out var mail) && mail.HasValue && DomainOf(mail.First) is { } domain)
            {
                if (!known.Contains(domain))
                {
                    state.Add(Warning(BulkCreateIssueCodes.MailDomainNotInForest, "The mail domain is not a domain or UPN suffix of the forest.", Attr.Mail));
                }

                if (NonRoutableMailDomains.Any(d => domain.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                {
                    state.Add(Warning(BulkCreateIssueCodes.NonRoutableMailDomain, "The mail domain cannot receive mail from the internet.", Attr.Mail));
                }
            }
        }

        private static string? DomainOf(string address) =>
            address.LastIndexOf('@') is var at and >= 0 && at < address.Length - 1 ? address[(at + 1)..] : null;

        /// <summary>The schema's mandatory attributes of the class without what the DC or the plan fills in, plus F3's own (2.5).</summary>
        private List<string> MandatoryAttributes()
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Attr.Cn, Attr.Name, Attr.ObjectSid, Attr.ObjectCategory, Attr.NtSecurityDescriptor, "instanceType", Attr.ObjectClass,
                Attr.GroupType, Attr.UserAccountControl,
            };
            var mandatory = new List<string> { Attr.Cn };
            if (HasSamAccountName(_kind))
            {
                mandatory.Add(Attr.SamAccountName);
            }

            if (_schema.Attributes.Count > 0)
            {
                foreach (var attribute in _schema.GetMandatoryAttributes(CreationNames.ClassChainOf(_kind)))
                {
                    if (!excluded.Contains(attribute) && !mandatory.Contains(attribute, StringComparer.OrdinalIgnoreCase))
                    {
                        mandatory.Add(attribute);
                    }
                }
            }

            return mandatory;
        }

        private static List<BulkValue> Ordered(Dictionary<string, Draft> values) =>
        [
            .. values.Values
                .Where(d => d.HasValue)
                .OrderBy(d => GridRank(d.Attribute))
                .ThenBy(d => d.Attribute, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.ToValue()),
        ];

        private static int GridRank(string attribute)
        {
            var index = Array.FindIndex(GridOrder, a => Is(a, attribute));
            return index < 0 ? GridOrder.Length : index;
        }

        private List<string> GridAttributes(List<BulkCreateRow> rows)
        {
            var present = new HashSet<string>(rows.SelectMany(r => r.Values).Select(v => v.Attribute), StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var attribute in GridOrder)
            {
                var applicable = Is(attribute, Attr.Cn)
                    || (Is(attribute, Attr.SamAccountName) && HasSamAccountName(_kind))
                    || (IsUserKind(_kind) && !Is(attribute, Attr.SamAccountName))
                    || present.Contains(attribute)
                    || _patterns.HasRule(attribute);
                if (applicable)
                {
                    result.Add(attribute);
                }
            }

            result.AddRange(present.Where(a => !GridOrder.Contains(a, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
            return result;
        }

        private static List<ValidationIssue> Distinct(IEnumerable<ValidationIssue> issues)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ValidationIssue>();
            foreach (var issue in issues)
            {
                if (seen.Add(issue.Severity + "\u0001" + issue.Code + "\u0001" + issue.Attribute + "\u0001" + string.Join("\u0002", issue.Arguments)))
                {
                    result.Add(issue);
                }
            }

            return result;
        }

        private static string Key(string dn) => Planning.PlanningContext.Key(dn).ToUpperInvariant();
    }

    /// <summary>The state of one row while it is computed (a class: it is never printed).</summary>
    private sealed class RowState(TableRow row, int index)
    {
        public TableRow Row { get; } = row;

        /// <summary>The 0-based position in <see cref="InputTable.Rows"/>.</summary>
        public int Index { get; } = index;

        public int Number => Row.Number;

        public BulkRowStatus? Fixed { get; set; }

        public string? CreatedDn { get; set; }

        /// <summary>Values from columns and edits (and the frozen values).</summary>
        public Dictionary<string, Draft> Base { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Edited { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool CountryEdited { get; set; }

        public bool ExpiresEdited { get; set; }

        /// <summary>The current values (base plus rules).</summary>
        public Dictionary<string, Draft> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ValidationIssue> Issues { get; } = [];

        public bool IsFrozen { get; set; }

        public int? Pin { get; set; }

        public int? CounterNumber { get; set; }

        public bool NeedsNumber { get; set; }

        /// <summary>The attributes whose value depends on the counter number in this row.</summary>
        public HashSet<string> CounterDependent { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The finished prefix and suffix around the number per counter-dependent attribute.</summary>
        public Dictionary<string, (string Prefix, string Suffix)> Parts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Counter-dependent attributes a function removed or repeated the number of (windowed lookups, 2.4).</summary>
        public HashSet<string> Unsplittable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? PrimaryAttribute { get; set; }

        public string SequenceKey { get; set; } = string.Empty;

        /// <summary>The disambiguators of the bumped roots (2.4).</summary>
        public Dictionary<string, int> Bumps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string? ManagerText { get; set; }

        public string? GroupsText { get; set; }

        public string? ManagerDn { get; set; }

        public List<string> ManagerCandidates { get; } = [];

        public List<string> GroupDns { get; } = [];

        public List<string> MatchDns { get; } = [];

        public void Add(ValidationIssue issue) => Issues.Add(issue);

        public override string ToString() => $"Row state {Number}";
    }
}
