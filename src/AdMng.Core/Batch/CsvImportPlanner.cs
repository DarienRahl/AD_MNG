using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Batch;

/// <summary>
/// Default <see cref="ICsvImportPlanner"/>. Keys are resolved with batched OR-filter searches (at most
/// <see cref="LookupBatchSize"/> keys per request; objectGUID keys as binary filters, distinguishedName keys by
/// direct reads), then every resolved row goes through the same validation as <see cref="ChangePlanner"/>.
/// </summary>
/// <remarks>
/// Cell values are trimmed, and the apostrophe that CSV formula protection puts in front of values such as
/// <c>+48 22 123</c> is removed (<see cref="CsvCodec.RemoveFormulaProtection"/>). Cells of multi-valued attributes are split on <see cref="CsvImportMapping.MultiValueSeparator"/>
/// and replace all values. Two rows resolving to the same object are reported as <see cref="IssueCodes.DuplicateInBatch"/>
/// on the later row. The progress callback of <see cref="PlanAsync"/> receives the number of rows planned so far.
/// </remarks>
public sealed class CsvImportPlanner : ICsvImportPlanner
{
    /// <summary>Maximum number of keys per lookup request.</summary>
    public const int LookupBatchSize = PlanValidation.LookupBatchSize;

    private readonly TimeProvider _timeProvider;

    public CsvImportPlanner(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<ChangePlan> PlanAsync(
        IDirectorySession session,
        CsvTable table,
        CsvImportMapping mapping,
        PlanningOptions? options,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(mapping);
        var columns = ResolveColumns(table, mapping, out var keyIndex);
        var context = new PlanningContext(session, options ?? new PlanningOptions(), cancellationToken);
        var createdAt = _timeProvider.GetUtcNow();
        var attributes = ChangePlanner.BaseAttributes
            .Concat(columns.Select(c => c.Attribute))
            .Append(mapping.KeyAttribute)
            .Append(Attr.ObjectGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var keys = new string[table.Rows.Count];
        for (var row = 0; row < keys.Length; row++)
        {
            keys[row] = CellValue(table, row, keyIndex);
        }

        var resolver = new KeyResolver(context, mapping, attributes);
        await resolver.ResolveAsync(keys.Where(k => k.Length > 0)).ConfigureAwait(false);

        var drafts = new ObjectPlanDraft[keys.Length];
        var resolved = new List<(int Row, DirectoryObject Object)>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var row = 0; row < keys.Length; row++)
        {
            var draft = CreateDraft(row, keys[row], mapping, resolver, seen, out var current);
            drafts[row] = draft;
            if (current is not null && !draft.HasErrors)
            {
                resolved.Add((row, current));
            }
        }

        var completed = keys.Length - resolved.Count;
        if (completed > 0)
        {
            progress?.Report(completed);
        }

        await Parallel.ForEachAsync(
            resolved,
            new ParallelOptions { MaxDegreeOfParallelism = context.MaxDegreeOfParallelism, CancellationToken = cancellationToken },
            async (item, _) =>
            {
                await PlanRowAsync(context, table, mapping, columns, drafts[item.Row], item.Row).ConfigureAwait(false);
                progress?.Report(Interlocked.Increment(ref completed));
            }).ConfigureAwait(false);

        if (context.Options.CheckUniqueness)
        {
            await PlanValidation.ValidateUniquenessAsync(context, drafts).ConfigureAwait(false);
        }

        return new ChangePlan
        {
            Objects = drafts.Select(d => d.Build()).ToArray(),
            Title = "CSV import",
            CreatedAt = createdAt,
            Server = session.Info.Server,
        };
    }

    private static List<ColumnMapping> ResolveColumns(CsvTable table, CsvImportMapping mapping, out int keyIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.KeyColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.KeyAttribute);
        ArgumentNullException.ThrowIfNull(mapping.Columns);
        keyIndex = table.IndexOf(mapping.KeyColumn);
        if (keyIndex < 0)
        {
            throw new ArgumentException($"The key column '{mapping.KeyColumn}' is not present in the CSV file.", nameof(mapping));
        }

        if (!IsAttributeName(mapping.KeyAttribute))
        {
            throw new ArgumentException($"'{mapping.KeyAttribute}' is not a valid attribute name.", nameof(mapping));
        }

        var columns = new List<ColumnMapping>();
        foreach (var (column, attribute) in mapping.Columns)
        {
            var index = table.IndexOf(column);
            if (index < 0)
            {
                throw new ArgumentException($"The column '{column}' is not present in the CSV file.", nameof(mapping));
            }

            if (!IsAttributeName(attribute))
            {
                throw new ArgumentException($"'{attribute}' (column '{column}') is not a valid attribute name.", nameof(mapping));
            }

            if (columns.Any(c => string.Equals(c.Attribute, attribute, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"The attribute '{attribute}' is mapped from more than one column.", nameof(mapping));
            }

            columns.Add(new ColumnMapping(index, column, attribute));
        }

        return columns;
    }

    private static bool IsAttributeName(string attribute) =>
        attribute.Length > 0 && char.IsAsciiLetter(attribute[0]) && attribute.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static ObjectPlanDraft CreateDraft(
        int row,
        string key,
        CsvImportMapping mapping,
        KeyResolver resolver,
        Dictionary<string, int> seen,
        out DirectoryObject? current)
    {
        current = null;
        var sourceRow = row + 1;
        if (key.Length == 0)
        {
            var empty = new ObjectPlanDraft(string.Empty, $"#{sourceRow}", null, sourceRow);
            empty.Issues.Add(PlanValidation.Error(IssueCodes.InvalidValue, $"The key column '{mapping.KeyColumn}' is empty."));
            return empty;
        }

        if (resolver.GetError(key) is { } keyError)
        {
            var invalid = new ObjectPlanDraft(string.Empty, key, null, sourceRow);
            invalid.Issues.Add(keyError);
            return invalid;
        }

        var matches = resolver.GetMatches(key);
        if (matches.Count == 0)
        {
            var missing = new ObjectPlanDraft(string.Empty, key, null, sourceRow);
            missing.Issues.Add(PlanValidation.Error(IssueCodes.NotFound, $"No object with {mapping.KeyAttribute} '{key}' was found."));
            return missing;
        }

        if (matches.Count > 1)
        {
            var ambiguous = new ObjectPlanDraft(string.Empty, key, null, sourceRow);
            var sample = string.Join("; ", matches.Take(5).Select(m => m.DistinguishedName));
            ambiguous.Issues.Add(PlanValidation.Error(IssueCodes.Ambiguous,
                $"{matches.Count} objects have {mapping.KeyAttribute} '{key}': {sample}{(matches.Count > 5 ? "; ..." : string.Empty)}"));
            return ambiguous;
        }

        current = matches[0];
        var draft = new ObjectPlanDraft(current.DistinguishedName, ObjectPlanDraft.DisplayNameOf(current), current, sourceRow);
        var dnKey = PlanningContext.Key(current.DistinguishedName);
        if (seen.TryGetValue(dnKey, out var firstRow))
        {
            draft.Issues.Add(PlanValidation.Error(IssueCodes.DuplicateInBatch, $"Row {firstRow} already refers to this object."));
        }
        else
        {
            seen[dnKey] = sourceRow;
        }

        return draft;
    }

    private static async Task PlanRowAsync(
        PlanningContext context,
        CsvTable table,
        CsvImportMapping mapping,
        IReadOnlyList<ColumnMapping> columns,
        ObjectPlanDraft draft,
        int row)
    {
        draft.Current = await context.CompleteRangedValuesAsync(draft.Current!).ConfigureAwait(false);
        if (draft.Current.IsProtectedByAdminSdHolder)
        {
            draft.Issues.Add(PlanValidation.Warning(IssueCodes.AdminSdHolder,
                "The object is protected by AdminSDHolder (adminCount=1); changes may require Domain Admin rights."));
        }

        foreach (var column in columns)
        {
            var cell = CellValue(table, row, column.Index);
            var edit = draft.GetEdit(column.Attribute, context.Schema);
            if (cell.Length == 0)
            {
                if (mapping.EmptyCells == EmptyCellBehavior.Clear)
                {
                    edit.Replace([]);
                }

                continue;
            }

            var definition = context.Schema.FindAttribute(column.Attribute);
            IEnumerable<string> values = definition is { IsSingleValued: false } && mapping.MultiValueSeparator.Length > 0
                ? cell.Split(mapping.MultiValueSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : [cell];
            edit.Replace(values.Select(v => PlanValidation.NormalizeValue(context.Schema, column.Attribute, v)));
        }

        draft.FinalizeAttributes();
        var permissions = await context.GetPermissionsAsync(draft.DistinguishedName).ConfigureAwait(false);
        await PlanValidation.ValidateAttributeChangesAsync(context, draft, permissions).ConfigureAwait(false);
    }

    private static string CellValue(CsvTable table, int row, int column) =>
        CsvCodec.RemoveFormulaProtection(table.Cell(row, column).Trim());

    private sealed record ColumnMapping(int Index, string Column, string Attribute);

    /// <summary>Resolves key values to directory objects with as few requests as possible.</summary>
    private sealed class KeyResolver
    {
        private readonly PlanningContext _context;
        private readonly CsvImportMapping _mapping;
        private readonly IReadOnlyList<string> _attributes;
        private readonly Dictionary<string, List<DirectoryObject>> _matches = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ValidationIssue> _errors = new(StringComparer.OrdinalIgnoreCase);

        public KeyResolver(PlanningContext context, CsvImportMapping mapping, IReadOnlyList<string> attributes)
        {
            _context = context;
            _mapping = mapping;
            _attributes = attributes;
        }

        private bool IsDnKey => string.Equals(_mapping.KeyAttribute, Attr.DistinguishedName, StringComparison.OrdinalIgnoreCase);

        private bool IsGuidKey => string.Equals(_mapping.KeyAttribute, Attr.ObjectGuid, StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<DirectoryObject> GetMatches(string key) => _matches.TryGetValue(Normalize(key), out var list) ? list : [];

        public ValidationIssue? GetError(string key) => _errors.GetValueOrDefault(Normalize(key));

        public async Task ResolveAsync(IEnumerable<string> rawKeys)
        {
            var keys = new List<string>();
            foreach (var raw in rawKeys)
            {
                if (IsGuidKey && !Guid.TryParse(raw, out _))
                {
                    _errors[raw] = PlanValidation.Error(IssueCodes.InvalidValue, $"'{raw}' is not a valid GUID.");
                    continue;
                }

                if (IsDnKey && (!DistinguishedName.TryParse(raw, out var dn) || dn.IsRoot))
                {
                    _errors[raw] = PlanValidation.Error(IssueCodes.InvalidValue, $"'{raw}' is not a valid distinguished name.");
                    continue;
                }

                var key = Normalize(raw);
                if (!_matches.ContainsKey(key))
                {
                    _matches[key] = [];
                    keys.Add(key);
                }
            }

            if (IsDnKey)
            {
                await ResolveByDnAsync(keys).ConfigureAwait(false);
                return;
            }

            foreach (var chunk in keys.Chunk(LookupBatchSize))
            {
                await SearchChunkAsync(chunk).ConfigureAwait(false);
            }
        }

        /// <summary>GUID keys are normalized to the "D" format; DN keys to their RFC 4514 form; others are kept.</summary>
        private string Normalize(string key)
        {
            if (IsGuidKey && Guid.TryParse(key, out var guid))
            {
                return guid.ToString("D");
            }

            return IsDnKey ? PlanningContext.Key(key) : key;
        }

        private async Task ResolveByDnAsync(IReadOnlyList<string> keys)
        {
            var results = new DirectoryObject?[keys.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, keys.Count),
                new ParallelOptions { MaxDegreeOfParallelism = _context.MaxDegreeOfParallelism, CancellationToken = _context.CancellationToken },
                async (index, token) =>
                {
                    results[index] = await _context.Session.GetAsync(keys[index], _attributes, token).ConfigureAwait(false);
                }).ConfigureAwait(false);

            for (var i = 0; i < keys.Count; i++)
            {
                if (results[i] is { } found && MatchesKind(found))
                {
                    _matches[keys[i]].Add(found);
                }
            }
        }

        private async Task SearchChunkAsync(IReadOnlyList<string> chunk)
        {
            var keyFilter = IsGuidKey
                ? LdapFilter.Or(chunk.Select(k => LdapFilter.EqualGuid(Attr.ObjectGuid, Guid.Parse(k))))
                : LdapFilter.Or(chunk.Select(k => LdapFilter.Equal(_mapping.KeyAttribute, k)));
            var search = new DirectorySearch
            {
                BaseDn = _mapping.SearchBaseDn ?? _context.Session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = _mapping.Kind is { } kind ? LdapFilter.And(kind.ToLdapFilter(), keyFilter) : keyFilter,
                Attributes = _attributes,
            };

            await foreach (var found in _context.Session.SearchAsync(search, _context.CancellationToken).ConfigureAwait(false))
            {
                foreach (var key in KeysOf(found))
                {
                    if (_matches.TryGetValue(key, out var list) && !list.Any(o => DistinguishedName.AreEqual(o.DistinguishedName, found.DistinguishedName)))
                    {
                        list.Add(found);
                    }
                }
            }
        }

        private IEnumerable<string> KeysOf(DirectoryObject found)
        {
            if (IsGuidKey)
            {
                return found.ObjectGuid is { } guid ? [guid.ToString("D")] : [];
            }

            return found.GetStrings(_mapping.KeyAttribute);
        }

        private bool MatchesKind(DirectoryObject found) => _mapping.Kind switch
        {
            null => true,
            ObjectKind.User => found.Kind is ObjectKind.User or ObjectKind.InetOrgPerson,
            var kind => found.Kind == kind,
        };
    }
}
