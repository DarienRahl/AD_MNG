using System.Buffers.Binary;
using System.Globalization;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// Maps an ADMX policy to Registry.pol entries and back (the ADMX schema semantics the Microsoft editor follows):
/// <list type="bullet">
/// <item><b>Enabled</b>: the policy value (<c>enabledValue</c>, REG_DWORD 1 without one), the <c>enabledList</c>, each
/// element's value (check box: <c>trueValue</c>/<c>falseValue</c>, REG_DWORD 1/0 without them, and their lists; number:
/// REG_DWORD, REG_QWORD or text; text: REG_SZ/REG_EXPAND_SZ; lines: REG_MULTI_SZ; enum: the item's value and value list;
/// list: <c>**delvals.</c> unless additive, then the entries named by <c>valuePrefix</c> + 1, 2 …, by the operator or by
/// their data); <c>soft</c> elements as <c>**soft.</c>.</item>
/// <item><b>Disabled</b>: the policy value <c>disabledValue</c> (<c>**del.</c> without one), the <c>disabledList</c>, a
/// <c>**del.</c> for every <c>enabledList</c> value the disabled list does not cover and for every element value,
/// <c>**delvals.</c> for list keys.</item>
/// <item><b>Not configured</b>: no entry about any of these values.</item>
/// </list>
/// Reading weighs what the entries say (the policy value and its lists count twice as much as element values; deleted
/// element values count only when no element value is set, since an enabled policy's chosen item or cleared check box
/// may itself be a deletion): more evidence for enabled is Enabled, for disabled Disabled; entries that match neither are
/// <see cref="PolicyState.Inconsistent"/>. A policy with a value name whose value the file does not mention is not
/// configured (both states write it), whatever other policies wrote into the values it shares.
/// A list's entries are the string values of its key that no loaded policy names (<c>ownedByAny</c>): other policies'
/// values are neither listed nor touched, and an additive list (which shares its key) leaves values of other types alone.
/// The byte-exact output of the Microsoft editor is confirmed by fixtures from the pilot (design note 6, K3).
/// </summary>
public static class PolicyEngine
{
    /// <summary>The state and element values of <paramref name="policy"/> in a Registry.pol.</summary>
    /// <param name="policy">The policy.</param>
    /// <param name="view">The net state of the file.</param>
    /// <param name="ownedByAny">The value names every loaded policy owns per normalized key (<see cref="OwnValueNames"/> of
    /// all of them): values a list must not count as its entries. Null: only the policy's own names are excluded.</param>
    public static PolicySetting Read(AdmxPolicy policy, RegistryPolicyView view, IReadOnlyDictionary<string, HashSet<string>>? ownedByAny = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(view);
        var enabled = 0;
        var disabled = 0;
        var any = false;

        if (policy.ValueName is { } valueName)
        {
            var state = view.Get(policy.Key, valueName);
            if (state.Kind == RegistryValueStateKind.Missing)
            {
                // Enabled and Disabled both write the policy's own value (or its **del.): without it the policy is not
                // configured, whatever other policies wrote into the values it shares (an IE zone template and the zone's
                // single URL actions).
                return PolicySetting.NotConfigured;
            }

            any = true;
            if (Matches(state, policy.EnabledValue ?? PolicyValue.One))
            {
                enabled += 2;
            }
            else if (policy.DisabledValue is null ? state.IsDeleted : Matches(state, policy.DisabledValue))
            {
                disabled += 2;
            }
        }

        if (policy.EnabledList is { } enabledList)
        {
            foreach (var item in enabledList.Items)
            {
                var key = enabledList.KeyOf(item, policy.Key);
                var state = view.Get(key, item.ValueName);
                any |= state.Kind != RegistryValueStateKind.Missing;
                if (Matches(state, item.Value))
                {
                    enabled += 2;
                }
                else if (state.IsDeleted && !Covers(policy.DisabledList, policy.Key, key, item.ValueName))
                {
                    disabled += 2;
                }
            }
        }

        if (policy.DisabledList is { } disabledList)
        {
            foreach (var item in disabledList.Items)
            {
                var state = view.Get(disabledList.KeyOf(item, policy.Key), item.ValueName);
                any |= state.Kind != RegistryValueStateKind.Missing;
                if (Matches(state, item.Value))
                {
                    disabled += 2;
                }
            }
        }

        var ownNames = OwnValueNames(policy);
        var present = 0;
        var deleted = 0;
        foreach (var element in policy.Elements)
        {
            var key = element.KeyIn(policy);
            if (element is ListElement)
            {
                var values = ListValues(view, key, ownNames, ownedByAny);
                any |= values.Count > 0 || view.ClearsValues(key);
                if (values.Count > 0)
                {
                    present++;
                }
                else if (view.ClearsValues(key))
                {
                    deleted++;
                }

                continue;
            }

            var state = view.Get(key, element.ValueName!);
            any |= state.Kind != RegistryValueStateKind.Missing;
            if (state.IsSet)
            {
                present++;
            }
            else if (state.IsDeleted)
            {
                deleted++;
            }
        }

        // An enabled policy may write deletions itself (the chosen item's <delete/>, a cleared check box's <delete/>
        // falseValue): deletions are evidence for Disabled only when no element value is set.
        if (present > 0)
        {
            enabled += present;
        }
        else
        {
            disabled += deleted;
        }

        var policyState = !any ? PolicyState.NotConfigured
            : enabled > disabled ? PolicyState.Enabled
            : disabled > enabled ? PolicyState.Disabled
            : PolicyState.Inconsistent;
        if (policyState is PolicyState.NotConfigured or PolicyState.Disabled)
        {
            return policyState == PolicyState.Disabled ? PolicySetting.Disabled : PolicySetting.NotConfigured;
        }

        var result = new Dictionary<string, PolicyElementValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in policy.Elements)
        {
            if (ElementValue(policy, element, view, ownNames, ownedByAny) is { } value)
            {
                result[element.Id] = value;
            }
        }

        return new PolicySetting(policyState, result);
    }

    /// <summary>
    /// Writes <paramref name="setting"/> of <paramref name="policy"/> into the document: the entries of the new state replace
    /// those of the old one; entries of other policies are not touched. Validate first (<see cref="Validate"/>); an invalid
    /// value is written as far as it can be (a missing required value is skipped).
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="setting">The state and element values to write.</param>
    /// <param name="document">The Registry.pol of the policy's half.</param>
    /// <param name="ownedByAny">The value names every loaded policy owns per normalized key: a list keeps them (and every
    /// value that is not a string) when it rewrites its key. Null: only the policy's own names are kept.</param>
    public static void Write(AdmxPolicy policy, PolicySetting setting, RegistryPolicyDocument document,
        IReadOnlyDictionary<string, HashSet<string>>? ownedByAny = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(document);
        if (setting.State == PolicyState.Inconsistent)
        {
            throw new ArgumentException("An inconsistent state cannot be written; choose a state.", nameof(setting));
        }

        var state = setting.State;
        var ownNames = OwnValueNames(policy);

        // Lists first: their **delvals. leads the key, the values the policy writes follow. A list rewrites its entries and
        // keeps the values other loaded policies own in its key; an additive list (no **delvals.: it shares its key) also
        // keeps every value that is not a string, which cannot be one of its entries.
        foreach (var list in policy.Elements.OfType<ListElement>())
        {
            var key = list.KeyIn(policy);
            var normalized = RegistryPolicyDocument.NormalizeKey(key);
            var own = ownNames.TryGetValue(normalized, out var names) ? names : null;
            var others = ownedByAny is not null && ownedByAny.TryGetValue(normalized, out var foreign) ? foreign : null;
            document.ForgetKey(key, e => (list.Additive && !IsListEntry(e))
                || own?.Contains(RegistryPolicyDocument.BaseName(e.ValueName)) == true
                || others?.Contains(RegistryPolicyDocument.BaseName(e.ValueName)) == true);
            if (state == PolicyState.Disabled || (state == PolicyState.Enabled && !list.Additive))
            {
                document.ClearValues(key);
            }

            if (state == PolicyState.Enabled && setting.ValueOf(list.Id) is ListElementValue items)
            {
                var type = list.Expandable ? RegistryFormat.RegExpandSz : RegistryFormat.RegSz;
                var index = 0;
                foreach (var item in items.Items)
                {
                    index++;
                    var name = list.ExplicitValue ? item.Name : list.ValuePrefix is { } prefix ? prefix + index.ToString(CultureInfo.InvariantCulture) : item.Value;
                    if (name.Length > 0 || list.ExplicitValue)
                    {
                        document.Set(key, name, type, RegistryPolFile.StringData(item.Value));
                    }
                }
            }
        }

        // Disabled: the elements' values (and the lists of check boxes and enum items) are deleted first, so the policy's own
        // disabledValue, enabledList and disabledList written below win where they name the same values (Citrix's
        // "false" lockdowns, the IE zone templates' <delete/> lists).
        if (state == PolicyState.Disabled)
        {
            foreach (var element in policy.Elements.Where(e => e is not ListElement))
            {
                var key = element.KeyIn(policy);
                ForgetExtraLists(document, element, key);
                document.MarkDeleted(key, element.ValueName!);
            }
        }

        // The policy value.
        if (policy.ValueName is { } valueName)
        {
            switch (state)
            {
                case PolicyState.Enabled:
                    WriteValue(document, policy.Key, valueName, policy.EnabledValue ?? PolicyValue.One, soft: false);
                    break;
                case PolicyState.Disabled:
                    WriteValue(document, policy.Key, valueName, policy.DisabledValue ?? PolicyValue.Delete, soft: false);
                    break;
                default:
                    document.Forget(policy.Key, valueName);
                    break;
            }
        }

        // The enabled and disabled lists.
        if (policy.EnabledList is { } enabledList)
        {
            foreach (var item in enabledList.Items)
            {
                var key = enabledList.KeyOf(item, policy.Key);
                var covered = Covers(policy.DisabledList, policy.Key, key, item.ValueName);
                switch (state)
                {
                    case PolicyState.Enabled:
                        WriteValue(document, key, item.ValueName, item.Value, soft: false);
                        break;
                    case PolicyState.Disabled when !covered:
                        document.MarkDeleted(key, item.ValueName);
                        break;
                    case PolicyState.Disabled:
                        break; // the disabled list writes it
                    default:
                        document.Forget(key, item.ValueName);
                        break;
                }
            }
        }

        if (policy.DisabledList is { } disabledList)
        {
            foreach (var item in disabledList.Items)
            {
                var key = disabledList.KeyOf(item, policy.Key);
                if (state == PolicyState.Disabled)
                {
                    WriteValue(document, key, item.ValueName, item.Value, soft: false);
                }
                else if (state == PolicyState.NotConfigured || !Covers(policy.EnabledList, policy.Key, key, item.ValueName))
                {
                    document.Forget(key, item.ValueName);
                }
            }
        }

        // The other elements (Disabled: done above).
        foreach (var element in policy.Elements.Where(e => e is not ListElement))
        {
            var key = element.KeyIn(policy);
            var name = element.ValueName!;
            if (state == PolicyState.NotConfigured)
            {
                ForgetExtraLists(document, element, key);
                document.Forget(key, name);
            }
            else if (state == PolicyState.Enabled)
            {
                WriteElement(document, element, key, name, setting.ValueOf(element.Id));
            }
        }
    }

    /// <summary>The problems of an enabled setting's values (nothing to check for the other states).</summary>
    public static IReadOnlyList<PolicyValueProblem> Validate(AdmxPolicy policy, PolicySetting setting)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(setting);
        var problems = new List<PolicyValueProblem>();
        if (setting.State != PolicyState.Enabled)
        {
            return problems;
        }

        foreach (var element in policy.Elements)
        {
            var value = setting.ValueOf(element.Id);
            switch (element)
            {
                case DecimalElement number:
                    if (value is not NumberElementValue n)
                    {
                        if (number.Required)
                        {
                            problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.Required));
                        }
                    }
                    else if (n.Value < number.MinValue || n.Value > number.MaxValue)
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.OutOfRange,
                            [number.MinValue.ToString(CultureInfo.InvariantCulture), number.MaxValue.ToString(CultureInfo.InvariantCulture)]));
                    }

                    break;
                case TextElement text:
                    var textValue = (value as TextElementValue)?.Value ?? string.Empty;
                    if (text.Required && textValue.Length == 0)
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.Required));
                    }
                    else if (text.MaxLength > 0 && textValue.Length > text.MaxLength)
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.TooLong, [text.MaxLength.ToString(CultureInfo.InvariantCulture)]));
                    }
                    else if (textValue.Contains('\0', StringComparison.Ordinal))
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.InvalidCharacter));
                    }

                    break;
                case MultiTextElement multi:
                    var lines = (value as MultiTextElementValue)?.Lines ?? [];
                    if (multi.Required && lines.Count == 0)
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.Required));
                    }
                    else if (multi.MaxStrings > 0 && lines.Count > multi.MaxStrings)
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.TooManyLines, [multi.MaxStrings.ToString(CultureInfo.InvariantCulture)]));
                    }
                    else if (multi.MaxLength > 0 && lines.Any(l => l.Length > multi.MaxLength))
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.TooLong, [multi.MaxLength.ToString(CultureInfo.InvariantCulture)]));
                    }
                    else if (lines.Any(l => l.Contains('\0', StringComparison.Ordinal)))
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.InvalidCharacter));
                    }

                    break;
                case EnumElement choice:
                    if (value is not EnumElementValue chosen)
                    {
                        if (choice.Required)
                        {
                            problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.Required));
                        }
                    }
                    else if (chosen.Index < 0 || chosen.Index >= choice.Items.Count)
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.UnknownItem));
                    }

                    break;
                case ListElement list:
                    var items = (value as ListElementValue)?.Items ?? [];
                    if (items.Any(i => i.Value.Contains('\0', StringComparison.Ordinal) || i.Name.Contains('\0', StringComparison.Ordinal)))
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.InvalidCharacter));
                    }
                    else if (items.Any(i => (list.ExplicitValue && i.Name.Length == 0) || (!list.ExplicitValue && list.ValuePrefix is null && i.Value.Length == 0)))
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.EmptyListEntry));
                    }
                    else if (list.ExplicitValue ? HasDuplicates(items.Select(i => i.Name)) : list.ValuePrefix is null && HasDuplicates(items.Select(i => i.Value)))
                    {
                        problems.Add(new PolicyValueProblem(element.Id, PolicyValueProblemKind.DuplicateListEntry));
                    }

                    break;
            }
        }

        return problems;
    }

    /// <summary>
    /// The registry values a policy owns, per key (normalized): its value, list values and element values - with
    /// <see cref="ListKeys"/> everything the editor counts as the policy's (an entry outside every policy is an extra
    /// registry setting).
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<string>> OwnValueNames(AdmxPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string name)
        {
            var normalized = RegistryPolicyDocument.NormalizeKey(key);
            if (!result.TryGetValue(normalized, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[normalized] = names;
            }

            names.Add(name);
        }

        void AddList(PolicyValueList? list, string ownerKey)
        {
            foreach (var item in list?.Items ?? [])
            {
                Add(list!.KeyOf(item, ownerKey), item.ValueName);
            }
        }

        if (policy.ValueName is { } valueName)
        {
            Add(policy.Key, valueName);
        }

        AddList(policy.EnabledList, policy.Key);
        AddList(policy.DisabledList, policy.Key);
        foreach (var element in policy.Elements)
        {
            var key = element.KeyIn(policy);
            if (element.ValueName is { } name)
            {
                Add(key, name);
            }

            switch (element)
            {
                case BooleanElement boolean:
                    AddList(boolean.TrueList, key);
                    AddList(boolean.FalseList, key);
                    break;
                case EnumElement choice:
                    foreach (var item in choice.Items)
                    {
                        AddList(item.ValueList, key);
                    }

                    break;
            }
        }

        return result;
    }

    /// <summary>The keys whose values a policy's list elements own (normalized).</summary>
    public static IEnumerable<string> ListKeys(AdmxPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Elements.OfType<ListElement>().Select(l => RegistryPolicyDocument.NormalizeKey(l.KeyIn(policy)));
    }

    /// <summary>The value an element shows for a newly enabled policy: the presentation's default, else empty/first/off.</summary>
    public static PolicyElementValue? DefaultValue(PolicyElement element, PresentationControl? control)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element switch
        {
            BooleanElement => new BooleanElementValue(control is CheckBoxPresentation { DefaultChecked: true }),
            DecimalElement number => control is NumberBoxPresentation { DefaultValue: { } d }
                ? new NumberElementValue(Math.Clamp(d, number.MinValue, number.MaxValue))
                : number.Required ? new NumberElementValue(number.MinValue) : null,
            TextElement => new TextElementValue(control switch
            {
                TextBoxPresentation { DefaultValue: { } text } => text,
                ComboBoxPresentation { DefaultValue: { } text } => text,
                _ => string.Empty,
            }),
            MultiTextElement => new MultiTextElementValue([]),
            EnumElement choice => control is DropDownListPresentation { DefaultItem: { } index } && index >= 0 && index < choice.Items.Count
                ? new EnumElementValue(index)
                : choice.Items.Count > 0 ? new EnumElementValue(0) : null,
            ListElement => new ListElementValue([]),
            _ => null,
        };
    }

    private static PolicyElementValue? ElementValue(AdmxPolicy policy, PolicyElement element, RegistryPolicyView view,
        IReadOnlyDictionary<string, HashSet<string>> ownNames, IReadOnlyDictionary<string, HashSet<string>>? ownedByAny)
    {
        var key = element.KeyIn(policy);
        if (element is ListElement list)
        {
            var values = ListValues(view, key, ownNames, ownedByAny);
            return new ListElementValue([.. values.Select(v => new ListElementItem(
                list.ExplicitValue ? StripSoft(v.ValueName) : string.Empty, RegistryPolFile.ReadString(v.Data)))]);
        }

        var state = view.Get(key, element.ValueName!);
        switch (element)
        {
            case BooleanElement boolean:
                if (boolean.TrueValue is not null || boolean.TrueList is null)
                {
                    return new BooleanElementValue(Matches(state, boolean.TrueValue ?? PolicyValue.One));
                }

                return new BooleanElementValue(boolean.TrueList.Items.All(i => Matches(view.Get(boolean.TrueList.KeyOf(i, key), i.ValueName), i.Value)));
            case DecimalElement when state.Entry is { } entry:
                return Number(entry) is { } number ? new NumberElementValue(number) : null;
            case TextElement when state.Entry is { } entry && entry.Type is RegistryFormat.RegSz or RegistryFormat.RegExpandSz:
                return new TextElementValue(RegistryPolFile.ReadString(entry.Data));
            case MultiTextElement when state.Entry is { } entry && entry.Type == RegistryFormat.RegMultiSz:
                return new MultiTextElementValue(RegistryPolFile.ReadMultiString(entry.Data));
            case EnumElement choice:
                // The item's value list is written after its value: where the list names the enum's own value (BITS
                // "Custom" deletes ForegroundTransferPolicy), the list's value is what the file holds.
                PolicyValue Expected(EnumItem item) =>
                    item.ValueList?.Items.LastOrDefault(v => SameValue(item.ValueList.KeyOf(v, key), v.ValueName, key, element.ValueName!))?.Value ?? item.Value;
                for (var i = 0; i < choice.Items.Count; i++)
                {
                    var item = choice.Items[i];
                    if (Matches(state, Expected(item))
                        && (item.ValueList is null || item.ValueList.Items.All(v => Matches(view.Get(item.ValueList.KeyOf(v, key), v.ValueName), v.Value))))
                    {
                        return new EnumElementValue(i);
                    }
                }

                for (var i = 0; i < choice.Items.Count; i++)
                {
                    if (Matches(state, Expected(choice.Items[i])))
                    {
                        return new EnumElementValue(i);
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static void WriteElement(RegistryPolicyDocument document, PolicyElement element, string key, string name, PolicyElementValue? value)
    {
        switch (element)
        {
            case BooleanElement boolean:
                var on = value is BooleanElementValue { Value: true };
                WriteValue(document, key, name, on ? boolean.TrueValue ?? PolicyValue.One : boolean.FalseValue ?? PolicyValue.Zero, soft: false);
                var (write, other) = on ? (boolean.TrueList, boolean.FalseList) : (boolean.FalseList, boolean.TrueList);
                foreach (var item in other?.Items ?? [])
                {
                    var itemKey = other!.KeyOf(item, key);
                    if (!Covers(write, key, itemKey, item.ValueName))
                    {
                        document.Forget(itemKey, item.ValueName);
                    }
                }

                foreach (var item in write?.Items ?? [])
                {
                    WriteValue(document, write!.KeyOf(item, key), item.ValueName, item.Value, soft: false);
                }

                break;
            case DecimalElement number:
                if (value is not NumberElementValue n)
                {
                    document.Forget(key, name);
                    break;
                }

                if (number.StoreAsText)
                {
                    document.Set(key, name, RegistryFormat.RegSz, RegistryPolFile.StringData(n.Value.ToString(CultureInfo.InvariantCulture)), number.Soft);
                }
                else if (number.IsLong)
                {
                    document.Set(key, name, RegistryFormat.RegQword, RegistryPolFile.QwordData(n.Value), number.Soft);
                }
                else
                {
                    document.Set(key, name, RegistryFormat.RegDword, RegistryPolFile.DwordData((uint)Math.Min(n.Value, uint.MaxValue)), number.Soft);
                }

                break;
            case TextElement text:
                document.Set(key, name, text.Expandable ? RegistryFormat.RegExpandSz : RegistryFormat.RegSz,
                    RegistryPolFile.StringData((value as TextElementValue)?.Value ?? string.Empty), text.Soft);
                break;
            case MultiTextElement multi:
                document.Set(key, name, RegistryFormat.RegMultiSz, RegistryPolFile.MultiStringData((value as MultiTextElementValue)?.Lines ?? []), multi.Soft);
                break;
            case EnumElement choice:
                var chosen = value is EnumElementValue e && e.Index >= 0 && e.Index < choice.Items.Count ? choice.Items[e.Index] : null;
                foreach (var item in choice.Items.Where(i => !ReferenceEquals(i, chosen)))
                {
                    foreach (var listItem in item.ValueList?.Items ?? [])
                    {
                        var itemKey = item.ValueList!.KeyOf(listItem, key);
                        if (!Covers(chosen?.ValueList, key, itemKey, listItem.ValueName))
                        {
                            document.Forget(itemKey, listItem.ValueName);
                        }
                    }
                }

                if (chosen is null)
                {
                    document.Forget(key, name);
                    break;
                }

                WriteValue(document, key, name, chosen.Value, choice.Soft);
                foreach (var listItem in chosen.ValueList?.Items ?? [])
                {
                    WriteValue(document, chosen.ValueList!.KeyOf(listItem, key), listItem.ValueName, listItem.Value, soft: false);
                }

                break;
        }
    }

    /// <summary>The lists a check box or enum writes besides its value, forgotten when the policy is not enabled.</summary>
    private static void ForgetExtraLists(RegistryPolicyDocument document, PolicyElement element, string key)
    {
        IEnumerable<PolicyValueList?> lists = element switch
        {
            BooleanElement boolean => [boolean.TrueList, boolean.FalseList],
            EnumElement choice => choice.Items.Select(i => i.ValueList),
            _ => [],
        };
        foreach (var list in lists.OfType<PolicyValueList>())
        {
            foreach (var item in list.Items)
            {
                document.Forget(list.KeyOf(item, key), item.ValueName);
            }
        }
    }

    private static void WriteValue(RegistryPolicyDocument document, string key, string name, PolicyValue value, bool soft)
    {
        switch (value.Kind)
        {
            case PolicyValueKind.Decimal:
                document.Set(key, name, RegistryFormat.RegDword, RegistryPolFile.DwordData((uint)value.Number), soft);
                break;
            case PolicyValueKind.LongDecimal:
                document.Set(key, name, RegistryFormat.RegQword, RegistryPolFile.QwordData(value.Number), soft);
                break;
            case PolicyValueKind.String:
                document.Set(key, name, RegistryFormat.RegSz, RegistryPolFile.StringData(value.Text ?? string.Empty), soft);
                break;
            default:
                document.MarkDeleted(key, name);
                break;
        }
    }

    /// <summary>The net state is the ADMX value: a number of the same width, a string (without case), a deletion.</summary>
    internal static bool Matches(RegistryValueState state, PolicyValue value)
    {
        if (value.Kind == PolicyValueKind.Delete)
        {
            return state.IsDeleted;
        }

        if (state.Entry is not { } entry)
        {
            return false;
        }

        return value.Kind switch
        {
            PolicyValueKind.Decimal => entry.Type == RegistryFormat.RegDword && entry.Data.Length == 4
                && BinaryPrimitives.ReadUInt32LittleEndian(entry.Data) == (uint)value.Number,
            PolicyValueKind.LongDecimal => entry.Type == RegistryFormat.RegQword && entry.Data.Length == 8
                && BinaryPrimitives.ReadUInt64LittleEndian(entry.Data) == value.Number,
            PolicyValueKind.String => entry.Type is RegistryFormat.RegSz or RegistryFormat.RegExpandSz
                && string.Equals(RegistryPolFile.ReadString(entry.Data), value.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>A number from REG_DWORD, REG_QWORD or decimal text.</summary>
    private static ulong? Number(RegistryPolEntry entry) => entry.Type switch
    {
        RegistryFormat.RegDword when entry.Data.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(entry.Data),
        RegistryFormat.RegQword when entry.Data.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(entry.Data),
        RegistryFormat.RegSz or RegistryFormat.RegExpandSz when ulong.TryParse(RegistryPolFile.ReadString(entry.Data).Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// The list values of a key: the string values set there except the policy's other values and the values other loaded
    /// policies own.
    /// </summary>
    private static List<RegistryPolEntry> ListValues(RegistryPolicyView view, string key, IReadOnlyDictionary<string, HashSet<string>> ownNames,
        IReadOnlyDictionary<string, HashSet<string>>? ownedByAny)
    {
        var normalized = RegistryPolicyDocument.NormalizeKey(key);
        var own = ownNames.TryGetValue(normalized, out var names) ? names : null;
        var others = ownedByAny is not null && ownedByAny.TryGetValue(normalized, out var foreign) ? foreign : null;
        return [.. view.ValuesOf(key).Where(v => IsListEntry(v)
            && own?.Contains(StripSoft(v.ValueName)) != true
            && others?.Contains(StripSoft(v.ValueName)) != true)];
    }

    /// <summary>A value a list can own: a string value (lists write REG_SZ or REG_EXPAND_SZ), not a directive.</summary>
    private static bool IsListEntry(RegistryPolEntry entry) =>
        entry.Type is RegistryFormat.RegSz or RegistryFormat.RegExpandSz
        && (!entry.ValueName.StartsWith("**", StringComparison.Ordinal) || entry.ValueName.StartsWith(RegistryPolicyDocument.SoftPrefix, StringComparison.OrdinalIgnoreCase)
            || entry.ValueName.StartsWith(RegistryPolicyDocument.DeletePrefix, StringComparison.OrdinalIgnoreCase)
            || entry.ValueName.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase));

    private static bool SameValue(string key, string valueName, string otherKey, string otherName) =>
        valueName.Equals(otherName, StringComparison.OrdinalIgnoreCase)
        && RegistryPolicyDocument.NormalizeKey(key).Equals(RegistryPolicyDocument.NormalizeKey(otherKey), StringComparison.OrdinalIgnoreCase);

    private static string StripSoft(string valueName) =>
        valueName.StartsWith(RegistryPolicyDocument.SoftPrefix, StringComparison.OrdinalIgnoreCase) ? valueName[RegistryPolicyDocument.SoftPrefix.Length..] : valueName;

    /// <summary>The list has an item for this key and value name.</summary>
    private static bool Covers(PolicyValueList? list, string ownerKey, string key, string valueName) =>
        list is not null && list.Items.Any(i => valueName.Equals(i.ValueName, StringComparison.OrdinalIgnoreCase)
            && RegistryPolicyDocument.NormalizeKey(list.KeyOf(i, ownerKey)).Equals(RegistryPolicyDocument.NormalizeKey(key), StringComparison.OrdinalIgnoreCase));

    private static bool HasDuplicates(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values.Any(v => !seen.Add(v));
    }
}
