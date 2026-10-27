using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Security;

namespace AdMng.Core.Batch.Planning;

/// <summary>
/// Plans <see cref="ChangeAction.ModifySecurity"/> steps (K1 design note 3.1): for an existing object the wanted delta is
/// reduced to its effective part against the DACL read now (ACEs already present are not added again, absent ones are
/// not removed), so that the inverse delta is an exact rollback; the explicit ACEs read are kept for the preview
/// (<see cref="AclChange.PlannedFrom"/>). For an object the same plan creates, the delta is taken as it is.
/// </summary>
public static class AclPlanning
{
    /// <summary>
    /// The step for an existing object, or null when the DACL already is as wanted. When the descriptor cannot be read
    /// (no right to read permissions, malformed) the step carries the wanted delta and a
    /// <see cref="IssueCodes.SecurityNotReadable"/> error.
    /// </summary>
    /// <exception cref="ArgumentException">An ACE of <paramref name="wanted"/> cannot be written.</exception>
    public static async Task<PlannedChange?> PlanAsync(IDirectoryReader session, string distinguishedName, AclChange wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        ArgumentNullException.ThrowIfNull(wanted);
        foreach (var ace in wanted.Added.Concat(wanted.Removed))
        {
            ace.Validate();
        }

        var current = await session.GetAsync(distinguishedName, [Attr.NtSecurityDescriptor], cancellationToken).ConfigureAwait(false);
        var descriptor = current?.GetBytes(Attr.NtSecurityDescriptor);
        if (descriptor is null || !DaclEditor.TryReadExplicit(descriptor, out var explicitAces))
        {
            return new PlannedChange
            {
                Action = ChangeAction.ModifySecurity,
                Acl = wanted,
                Issues = [new ValidationIssue(IssueSeverity.Error, IssueCodes.SecurityNotReadable, "The permissions of the object cannot be read.")],
            };
        }

        var applied = DaclEditor.Apply(descriptor, wanted).Applied;
        if (applied.IsEmpty)
        {
            return null;
        }

        // Keep the display names of the wanted ACEs (the editor returns the specs it was given).
        return new PlannedChange
        {
            Action = ChangeAction.ModifySecurity,
            Acl = applied with { PlannedFrom = [.. explicitAces.Select(a => a.ToAce().ToSummaryValue())] },
        };
    }

    /// <summary>The step for an object created earlier in the same object plan (its DACL does not exist yet).</summary>
    /// <exception cref="ArgumentException">An ACE of <paramref name="wanted"/> cannot be written.</exception>
    public static PlannedChange ForNewObject(AclChange wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        foreach (var ace in wanted.Added.Concat(wanted.Removed))
        {
            ace.Validate();
        }

        return new PlannedChange { Action = ChangeAction.ModifySecurity, Acl = wanted };
    }
}
