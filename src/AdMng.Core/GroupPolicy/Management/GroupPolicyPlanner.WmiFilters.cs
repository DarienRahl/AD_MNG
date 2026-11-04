using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>WMI filters (<c>msWMI-Som</c>): create, edit, delete (design note 5.3; PowerShell: New-ADObject / Set-ADObject / Remove-ADObject).</summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>A new WMI filter <c>CN={GUID},CN=SOM,CN=WMIPolicy,CN=System</c>.</summary>
    public async Task<ChangePlan> PlanCreateWmiFilterAsync(IDirectorySession session, string name, string? description, IReadOnlyList<WmiFilterQuery> queries,
        CancellationToken cancellationToken, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(queries);
        var filterId = "{" + (id ?? Guid.NewGuid()).ToString("D").ToUpperInvariant() + "}";
        var trimmed = (name ?? string.Empty).Trim();
        var issues = await ValidateWmiFilterAsync(session, trimmed, queries, exceptDn: null, cancellationToken).ConfigureAwait(false);
        var now = WmiFilterQueries.FormatDate(_time.GetUtcNow());
        var dn = DistinguishedName.Parse(GroupPolicyReader.WmiFiltersDn(session)).Child("CN", filterId).ToString();
        var attributes = new List<AttributeChange>
        {
            new(Attr.ObjectClass, AttributeChangeKind.Replace, [], [GroupPolicyAttributes.WmiFilterClass]),
            New("msWMI-ID", filterId),
            New("msWMI-Name", trimmed),
            New("msWMI-Parm2", WmiFilterQueries.Format(queries)),
            New("msWMI-Author", session.Info.BoundIdentity),
            New("msWMI-CreationDate", now),
            New("msWMI-ChangeDate", now),
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            attributes.Insert(3, New("msWMI-Parm1", description.Trim()));
        }

        return Plan(session, [new ObjectPlan
        {
            DistinguishedName = dn,
            DisplayName = trimmed,
            Kind = ObjectKind.WmiFilter,
            ObjectClasses = ["top", GroupPolicyAttributes.WmiFilterClass],
            Changes = [new PlannedChange { Action = ChangeAction.Create, Attributes = attributes, Issues = issues }],
        }], $"New WMI filter {trimmed}");
    }

    /// <summary>Changes the name, description and queries of a filter (compare-and-swap against the values read now).</summary>
    public async Task<ChangePlan> PlanEditWmiFilterAsync(IDirectorySession session, string filterDn, string name, string? description,
        IReadOnlyList<WmiFilterQuery> queries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(queries);
        var current = await session.GetAsync(filterDn, GroupPolicyReader.WmiFilterAttributes, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The WMI filter does not exist.");
        var filter = GroupPolicyReader.ToWmiFilter(current);
        var trimmed = (name ?? string.Empty).Trim();
        var issues = await ValidateWmiFilterAsync(session, trimmed, queries, exceptDn: filterDn, cancellationToken).ConfigureAwait(false);
        var newDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var newQueries = WmiFilterQueries.Format(queries);
        var changes = new List<AttributeChange>();
        if (!string.Equals(filter.Name, trimmed, StringComparison.Ordinal))
        {
            changes.Add(Replace("msWMI-Name", current.GetString("msWMI-Name"), trimmed));
        }

        if (!string.Equals(filter.Description, newDescription, StringComparison.Ordinal))
        {
            changes.Add(Replace("msWMI-Parm1", filter.Description, newDescription));
        }

        if (!string.Equals(filter.RawQueries, newQueries, StringComparison.Ordinal))
        {
            changes.Add(Replace("msWMI-Parm2", filter.RawQueries, newQueries));
        }

        if (changes.Count > 0)
        {
            changes.Add(Replace("msWMI-ChangeDate", current.GetString("msWMI-ChangeDate"), WmiFilterQueries.FormatDate(_time.GetUtcNow())));
        }

        return Plan(session, [new ObjectPlan
        {
            DistinguishedName = filterDn,
            DisplayName = filter.Name,
            Kind = ObjectKind.WmiFilter,
            Changes = [new PlannedChange { Action = ChangeAction.Modify, Attributes = changes, Issues = issues }],
        }], $"Edit WMI filter {filter.Name}");
    }

    /// <summary>Deletes a filter; the GPOs that use it lose their link first (<c>gPCWQLFilter</c> cleared).</summary>
    public async Task<ChangePlan> PlanDeleteWmiFilterAsync(IDirectorySession session, string filterDn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var current = await session.GetAsync(filterDn, GroupPolicyReader.WmiFilterAttributes, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The WMI filter does not exist.");
        var filter = GroupPolicyReader.ToWmiFilter(current);
        var objects = new List<ObjectPlan>();
        foreach (var gpo in await UsersOfWmiFilterAsync(session, filter.Id, cancellationToken).ConfigureAwait(false))
        {
            objects.Add(PolicyObject(gpo, [new PlannedChange
            {
                Action = ChangeAction.Modify,
                Attributes = [Replace(GroupPolicyAttributes.WqlFilter, gpo.WmiFilterLink, null)],
            }], null));
        }

        objects.Add(new ObjectPlan
        {
            DistinguishedName = filterDn,
            DisplayName = filter.Name,
            Kind = ObjectKind.WmiFilter,
            Changes = [new PlannedChange { Action = ChangeAction.Delete }],
        });
        return Plan(session, objects, $"Delete WMI filter {filter.Name}");
    }

    /// <summary>The GPOs whose <c>gPCWQLFilter</c> names the filter.</summary>
    public async Task<IReadOnlyList<GpoInfo>> UsersOfWmiFilterAsync(IDirectorySession session, string filterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new List<GpoInfo>();
        try
        {
            await foreach (var gpc in session.SearchAsync(new DirectorySearch
            {
                BaseDn = GroupPolicyReader.PoliciesDn(session),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, GroupPolicyAttributes.ContainerClass), LdapFilter.Contains(GroupPolicyAttributes.WqlFilter, filterId)),
                Attributes = GroupPolicyReader.PolicyAttributes,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (GroupPolicyReader.ToPolicy(gpc) is { } gpo && string.Equals(gpo.WmiFilterId, filterId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(gpo);
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No CN=Policies.
        }

        return result;
    }

    private async Task<List<ValidationIssue>> ValidateWmiFilterAsync(IDirectorySession session, string name, IReadOnlyList<WmiFilterQuery> queries, string? exceptDn,
        CancellationToken cancellationToken)
    {
        var issues = new List<ValidationIssue>();
        if (name.Length == 0)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.TooShort, "The WMI filter needs a name.", "msWMI-Name"));
        }

        if (queries.Count == 0 || queries.Any(q => string.IsNullOrWhiteSpace(q.Query) || string.IsNullOrWhiteSpace(q.Namespace)))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.MandatoryAttribute, "A WMI filter needs at least one query with its namespace.", "msWMI-Parm2"));
        }

        var existing = await _reader.ReadWmiFiltersAsync(session, cancellationToken).ConfigureAwait(false);
        if (name.Length > 0 && existing.Any(f => !DistinguishedName.AreEqual(f.Dn, exceptDn) && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.Duplicate, "A WMI filter with this name already exists.", "msWMI-Name"));
        }

        return issues;
    }
}
