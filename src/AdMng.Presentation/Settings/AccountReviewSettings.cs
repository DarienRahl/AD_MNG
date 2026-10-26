using AdMng.Presentation.AccountReview;

namespace AdMng.Presentation.Settings;

/// <summary>What the account review checks.</summary>
public enum AccountReviewScopeKind
{
    /// <summary>The whole domain (default naming context, subtree).</summary>
    Domain,

    /// <summary>An organizational unit with its subtree.</summary>
    OrganizationalUnit,

    /// <summary>A saved view: its base, scope, filter and quick filter chips.</summary>
    SavedView,

    /// <summary>Members of a group, nested (and accounts whose primary group it is).</summary>
    GroupMembers,
}

/// <summary>Scope of an account review as remembered in the settings (DNs in the portable <c>{domain}</c> form).</summary>
public sealed record AccountReviewScope
{
    public AccountReviewScopeKind Kind { get; init; } = AccountReviewScopeKind.Domain;

    /// <summary>The OU or the group (portable DN, see <see cref="Views.SavedView.ToPortableDn"/>).</summary>
    public string? Dn { get; init; }

    /// <summary>Id of the saved view.</summary>
    public string? ViewId { get; init; }
}

/// <summary>
/// A named set of checks ("Poranny przegląd helpdesku"): name, checks, N-day parameters and scope - never directory data.
/// </summary>
public sealed record AccountReviewCheckSet
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<AccountCheck> Checks { get; init; } = [];

    public AccountReviewParameters Parameters { get; init; } = new();

    public AccountReviewScope Scope { get; init; } = new();
}

/// <summary>The account review window's remembered parameters, scope and named check sets.</summary>
public sealed class AccountReviewSettings
{
    /// <summary>Maximum number of named check sets.</summary>
    public const int MaxCheckSets = 50;

    /// <summary>N days of the last run.</summary>
    public AccountReviewParameters Parameters { get; set; } = new();

    /// <summary>Checks the operator switched off in the last run (all others run).</summary>
    public List<AccountCheck> DisabledChecks { get; set; } = [];

    /// <summary>Scope of the last run.</summary>
    public AccountReviewScope Scope { get; set; } = new();

    public List<AccountReviewCheckSet> CheckSets { get; set; } = [];

    /// <summary>Repairs values of a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        Parameters = (Parameters ?? new()).Normalized();
        DisabledChecks = [.. (DisabledChecks ?? []).Where(Enum.IsDefined).Distinct()];
        Scope ??= new();
        CheckSets = [.. (CheckSets ?? [])
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s with
            {
                Checks = [.. (s.Checks ?? []).Where(Enum.IsDefined).Distinct()],
                Parameters = (s.Parameters ?? new()).Normalized(),
                Scope = s.Scope ?? new(),
            })
            .Take(MaxCheckSets)];
    }
}
