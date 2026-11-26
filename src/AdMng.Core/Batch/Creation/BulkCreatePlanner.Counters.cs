using System.Globalization;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Batch.Creation;

// Counters (docs/dev/phase3-bulk-create.md 2.4): one query per (attribute, prefix, suffix), the in-memory allocation in row
// order with pins, sequences and cursors (FillGaps from the start, AfterHighest after the highest taken number), windowed
// equality lookups for short prefixes and unsplittable values, the Recycle Bin rule and the counter preview.
public sealed partial class BulkCreatePlanner
{
    private sealed partial class ComputeRun
    {
        /// <summary>Candidate numbers looked up together when a value is not covered by a prefix query (2.4 step 1).</summary>
        private const int CounterWindow = 50;

        /// <summary>The primary counter attribute of a row and its sequence key (rows with the same finished prefix and suffix).</summary>
        private void ChooseSequence(RowState state)
        {
            var direct = state.CounterDependent.Where(_patterns.UsesCounterDirectly).ToList();
            var candidates = direct.Count > 0 ? direct : [.. state.CounterDependent];
            var primary = PrimaryCounterOrder.FirstOrDefault(a => candidates.Contains(a, StringComparer.OrdinalIgnoreCase))
                ?? candidates.Order(StringComparer.OrdinalIgnoreCase).First();
            primary = candidates.First(c => Is(c, primary));
            state.PrimaryAttribute = primary;
            state.SequenceKey = state.Parts.TryGetValue(primary, out var parts) && !state.Unsplittable.Contains(primary)
                ? string.Join('\u0001', "p", primary.ToUpperInvariant(), parts.Prefix.ToUpperInvariant(), parts.Suffix.ToUpperInvariant())
                : string.Join('\u0001', "w", primary.ToUpperInvariant(), state.Values.TryGetValue(primary, out var value) ? value.First.ToUpperInvariant() : string.Empty);
        }

        private async Task AllocateCountersAsync()
        {
            if (_patterns.Counter is not { } spec)
            {
                return;
            }

            var rows = _rows.Where(r => r.Fixed is null && !r.IsFrozen && r.NeedsNumber).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            await RunPrefixQueriesAsync(rows).ConfigureAwait(false);
            await KeepPinsAsync(rows, spec).ConfigureAwait(false);
            var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var state in rows)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!cursors.TryGetValue(state.SequenceKey, out var cursor))
                {
                    cursor = InitialCursor(state, spec);
                }

                if (state.CounterNumber is { } pinned)
                {
                    cursors[state.SequenceKey] = Math.Max(cursor, (long)pinned + spec.Step);
                    continue;
                }

                var found = await FindFreeAsync(state, spec, cursor).ConfigureAwait(false);
                if (found is not { } free)
                {
                    state.Values = Evaluate(state, null, state.Bumps, null);
                    state.Add(Error(BulkCreateIssueCodes.CounterExhausted, "No free counter number was found."));
                    cursors[state.SequenceKey] = cursor;
                    continue;
                }

                Assign(state, free.Number, free.Values, free.Recycled);
                cursors[state.SequenceKey] = (long)free.Number + spec.Step;
            }

            foreach (var state in rows.Where(r => r.CounterNumber is not null))
            {
                foreach (var draft in state.Values.Values.Where(d => d.IsCounter && d.HasValue && IsUnique(d.Attribute)))
                {
                    var limit = LimitOf(draft.Attribute, _kind);
                    if (LimitedLength(draft.Attribute, draft.First) > limit)
                    {
                        state.Add(Error(BulkCreateIssueCodes.CounterTooLong, "The prefix and the number exceed the attribute's limit.",
                            draft.Attribute, Text(limit)));
                    }
                }
            }
        }

        private void Assign(RowState state, int number, Dictionary<string, Draft> values, IReadOnlyCollection<string> recycled)
        {
            state.CounterNumber = number;
            state.Values = values;
            foreach (var attribute in recycled)
            {
                state.Add(Warning(BulkCreateIssueCodes.NameInRecycleBin, "An object in the AD Recycle Bin holds this number.", attribute));
            }

            Reserve(state, values, d => d.IsCounter);
        }

        /// <summary>One search per (namespace, prefix, suffix) of the counter values with a prefix of at least 2 characters, with their implicit-UPN counterparts.</summary>
        private async Task RunPrefixQueriesAsync(IEnumerable<RowState> rows)
        {
            var queries = new Dictionary<string, (Namespace Namespace, string Prefix, string Suffix)>(StringComparer.Ordinal);
            var names = false;
            void Add(Namespace? ns, string prefix, string suffix)
            {
                if (ns is not null && prefix.Length >= 2)
                {
                    queries.TryAdd(string.Join('\u0001', ns.Key, prefix.ToUpperInvariant(), suffix.ToUpperInvariant()), (ns, prefix, suffix));
                }
            }

            foreach (var state in rows)
            {
                foreach (var (attribute, (prefix, suffix)) in state.Parts)
                {
                    if (!IsUnique(attribute) || state.Unsplittable.Contains(attribute) || prefix.Length < 2)
                    {
                        continue;
                    }

                    if (Is(attribute, Attr.Cn))
                    {
                        names = true;
                        continue;
                    }

                    Add(NamespaceOf(attribute), prefix, suffix);
                    if (!IsUserKind(_kind))
                    {
                        continue;
                    }

                    if (Is(attribute, Attr.SamAccountName))
                    {
                        Add(ForestNamespace(NamespaceKind.Upn), prefix, suffix + "@" + _windowDomain);
                    }
                    else if (Is(attribute, Attr.UserPrincipalName) && suffix.LastIndexOf('@') is var at and >= 0 && _forest.IsDomain(suffix[(at + 1)..]))
                    {
                        Add(SamNamespace(suffix[(at + 1)..]), prefix, suffix[..at]);
                    }
                }
            }

            if (names)
            {
                await ListNamesAsync(NameNamespace()).ConfigureAwait(false);
            }

            await Parallel.ForEachAsync(
                queries.Values,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = _cancellationToken },
                async (query, _) => await PrefixQueryAsync(query.Namespace, query.Prefix, query.Suffix).ConfigureAwait(false)).ConfigureAwait(false);
        }

        /// <summary>
        /// The pins of the previous computation (2.4 step 2): a row keeps its number while it is on the grid and free; kept pins
        /// are reserved before any other row is numbered, so an edit or an exclusion never renumbers other rows.
        /// </summary>
        private async Task KeepPinsAsync(List<RowState> rows, CounterSpec spec)
        {
            var pinned = rows
                .Where(r => r.Pin is { } pin && spec.IsOnGrid(pin))
                .Select(r => (State: r, Values: Evaluate(r, Text(r.Pin!.Value), r.Bumps, null)))
                .ToList();
            await EnsureKnownAsync(pinned.SelectMany(p => Keys(p.Values, d => d.IsCounter))).ConfigureAwait(false);
            foreach (var (state, values) in pinned)
            {
                if (IsCounterFree(state, values, out var recycled))
                {
                    Assign(state, state.Pin!.Value, values, recycled);
                }
            }
        }

        /// <summary>The first number a sequence tries: the start (FillGaps), or the first grid number above the highest taken one (AfterHighest).</summary>
        private long InitialCursor(RowState state, CounterSpec spec)
        {
            if (_rules.CounterMode == CounterMode.FillGaps || state.PrimaryAttribute is not { } primary
                || !state.Parts.TryGetValue(primary, out var parts) || state.Unsplittable.Contains(primary) || parts.Prefix.Length < 2
                || NamespaceOf(primary) is not { } ns)
            {
                return spec.Start;
            }

            var highest = HighestTaken(ns, parts.Prefix, parts.Suffix);
            return highest is { } taken ? spec.GridAtOrAbove(taken + 1) ?? (long)CounterSpec.MaxNumber + 1 : spec.Start;
        }

        /// <summary>
        /// The largest number between the prefix and the suffix of a value taken in the namespace (digits only: <c>kd0201</c>
        /// counts as 201): live objects, and deleted ones unless <see cref="BulkCreateRules.ReuseRecycleBinNumbers"/>.
        /// </summary>
        private long? HighestTaken(Namespace ns, string prefix, string suffix)
        {
            var held = _lookups.Index(ns.Key).ValuesUnder(prefix, suffix).AsEnumerable();
            if (ns.Kind == NamespaceKind.Name)
            {
                held = held.Concat(_lookups.Index("deleted|" + Key(_containerDn)).ValuesUnder(prefix, suffix));
            }

            long? highest = null;
            foreach (var (value, owners) in held)
            {
                var middle = value[prefix.Length..^suffix.Length];
                if (middle.Length is 0 or > 10 || !middle.All(char.IsAsciiDigit)
                    || !long.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    continue;
                }

                var counts = owners.Any(o => !o.IsRecycled && (!o.IsDeleted
                    || (!_rules.ReuseRecycleBinNumbers && !_released.Contains(value))));
                if (counts && (highest is null || number > highest))
                {
                    highest = number;
                }
            }

            return highest;
        }

        /// <summary>
        /// The smallest grid number at or above <paramref name="cursor"/> that is free for every counter value of the row;
        /// values a prefix query did not cover are looked up in windows of 50 numbers. Null: exhausted.
        /// </summary>
        private async Task<(int Number, Dictionary<string, Draft> Values, IReadOnlyCollection<string> Recycled)?> FindFreeAsync(RowState state, CounterSpec spec, long cursor)
        {
            var next = spec.GridAtOrAbove(cursor);
            var tried = 0;
            while (next is { } number && number <= CounterSpec.MaxNumber && tried < MaxCounterCandidates)
            {
                var values = Evaluate(state, Text((int)number), state.Bumps, null);
                var keys = Keys(values, d => d.IsCounter).ToList();
                if (keys.Any(k => !_lookups.Index(k.Namespace.Key).IsKnown(k.Value)))
                {
                    var window = new List<UniqueKey>(keys);
                    for (long later = number + spec.Step, k = 1; k < CounterWindow && later <= CounterSpec.MaxNumber; later += spec.Step, k++)
                    {
                        window.AddRange(Keys(Evaluate(state, Text((int)later), state.Bumps, null), d => d.IsCounter));
                    }

                    await EnsureKnownAsync(window).ConfigureAwait(false);
                }

                tried++;
                if (IsCounterFree(state, values, out var recycled))
                {
                    return ((int)number, values, recycled);
                }

                next = number + spec.Step;
            }

            return null;
        }

        /// <summary>Every counter value of the row is free (2.4): deleted holders count as taken unless numbers of the Recycle Bin are reused (then they are listed).</summary>
        private bool IsCounterFree(RowState state, Dictionary<string, Draft> values, out IReadOnlyCollection<string> recycled)
        {
            var reused = new List<string>();
            recycled = reused;
            foreach (var key in Keys(values, d => d.IsCounter))
            {
                var collision = Check(state, key, deletedIsTaken: !_rules.ReuseRecycleBinNumbers);
                if (collision is null)
                {
                    continue;
                }

                if (!collision.IsDeletedOnly)
                {
                    return false;
                }

                if (!reused.Contains(key.Attribute, StringComparer.OrdinalIgnoreCase))
                {
                    reused.Add(key.Attribute);
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ preview (2.4 step 3)

        public async Task<CounterPreview> PreviewCounterAsync()
        {
            await ReadForestAsync().ConfigureAwait(false);
            CompilePatterns();
            if (_patterns.Counter is not { } spec || !_patterns.IsValid)
            {
                return new CounterPreview(null, null, null);
            }

            var direct = _patterns.EvaluationOrder.Where(_patterns.UsesCounterDirectly).ToList();
            var primary = PrimaryCounterOrder.FirstOrDefault(a => direct.Contains(a, StringComparer.OrdinalIgnoreCase)) ?? direct.Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (primary is null || NamespaceOf(primary) is not { } ns)
            {
                return new CounterPreview(null, null, null);
            }

            primary = direct.First(a => Is(a, primary));
            var empty = Probe(primary, SentinelText, string.Empty);
            var filled = Probe(primary, SentinelText, "x");
            if (SplitAtSentinel(empty) is not { } parts || SplitAtSentinel(filled) is not { } other
                || !string.Equals(parts.Prefix, other.Prefix, StringComparison.Ordinal) || !string.Equals(parts.Suffix, other.Suffix, StringComparison.Ordinal))
            {
                return new CounterPreview(null, null, null);
            }

            long? highest = null;
            if (parts.Prefix.Length >= 2)
            {
                if (ns.Kind == NamespaceKind.Name)
                {
                    await ListNamesAsync(ns).ConfigureAwait(false);
                }
                else
                {
                    await PrefixQueryAsync(ns, parts.Prefix, parts.Suffix).ConfigureAwait(false);
                }

                highest = HighestTaken(ns, parts.Prefix, parts.Suffix);
            }

            var cursor = _rules.CounterMode == CounterMode.AfterHighest && highest is { } taken ? taken + 1 : spec.Start;
            var next = spec.GridAtOrAbove(cursor);
            int? firstFree = null;
            for (var tried = 0; next is { } number && number <= CounterSpec.MaxNumber && tried < MaxCounterCandidates; tried++, next = number + spec.Step)
            {
                var value = Probe(primary, Text((int)number), string.Empty);
                var key = new UniqueKey(ns, value, primary, isImplicit: false);
                if (!_lookups.Index(ns.Key).IsKnown(value))
                {
                    var window = Enumerable.Range(0, CounterWindow)
                        .Select(k => number + ((long)k * spec.Step))
                        .Where(n => n <= CounterSpec.MaxNumber)
                        .Select(n => new UniqueKey(ns, Probe(primary, Text((int)n), string.Empty), primary, isImplicit: false));
                    await EnsureKnownAsync(window).ConfigureAwait(false);
                }

                var owners = OwnersOf(key);
                if (!owners.Any(o => !o.IsDeleted) && (_rules.ReuseRecycleBinNumbers || !owners.Any(o => o.IsDeleted)))
                {
                    firstFree = (int)number;
                    break;
                }
            }

            return new CounterPreview(parts.Prefix, firstFree, highest is { } h && h <= int.MaxValue ? (int)h : null);
        }

        /// <summary>The finished value of <paramref name="attribute"/> for a row whose every attribute and column is <paramref name="fill"/>.</summary>
        private string Probe(string attribute, string number, string fill)
        {
            var values = new Dictionary<string, Draft>(StringComparer.OrdinalIgnoreCase);
            foreach (var current in _patterns.EvaluationOrder)
            {
                var row = new PatternRow
                {
                    Attribute = name => values.TryGetValue(name, out var draft) ? draft.Values : fill.Length > 0 ? [fill] : [],
                    Column = _ => fill,
                    Number = number,
                    UpnSuffix = _upnSuffix,
                    MailDomain = _mailDomain,
                    Domain = _windowDomain,
                    ContainerDn = _containerDn,
                    Now = _now,
                };
                var raw = ComposeUpn(current, _patterns.Render(current, row));
                var dependent = _patterns.CounterAttributes.Contains(current, StringComparer.OrdinalIgnoreCase);
                var value = number == SentinelText && dependent && raw.Split(SentinelText).Length == 2
                    ? FinishParts(current, raw)
                    : Finish(current, raw, dependent).Value;
                values[current] = new Draft(current, [value], BulkValueSource.Pattern);
                if (Is(current, attribute))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private string FinishParts(string attribute, string raw)
        {
            var at = raw.IndexOf(SentinelText, StringComparison.Ordinal);
            return ApplyComputerRules(attribute,
                Finish(attribute, raw[..at], isCounter: true).Value + SentinelText + Finish(attribute, raw[(at + 1)..], isCounter: true).Value);
        }

        private static (string Prefix, string Suffix)? SplitAtSentinel(string value)
        {
            var at = value.IndexOf(SentinelText, StringComparison.Ordinal);
            return at < 0 || value.IndexOf(SentinelText, at + 1, StringComparison.Ordinal) >= 0 ? null : (value[..at], value[(at + 1)..]);
        }
    }
}
