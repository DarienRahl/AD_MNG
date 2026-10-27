using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Security;

namespace AdMng.Core.Batch;

/// <summary><see cref="ChangeAction.ModifySecurity"/>: a delta on the DACL (K1 design note 3.1).</summary>
public sealed partial class BatchExecutor
{
    /// <summary>The SD flags of a DACL write: owner, group and SACL are left as they are.</summary>
    private static readonly ModifyOptions DaclWrite = new() { SecurityDescriptorFlags = SecurityDescriptorRights.Dacl };

    private sealed partial class Run
    {
        /// <summary>
        /// Reads nTSecurityDescriptor (SD flags owner, group, DACL – no SACL), applies the step's ACE delta to the DACL as it
        /// is NOW and writes the DACL-only result with the SD flags control DACL_SECURITY_INFORMATION. There is no
        /// compare-and-swap for descriptors: the delta is re-applied to the fresh DACL (also on a retry), so ACL changes
        /// made by others meanwhile are kept. A delta that changes nothing writes nothing. What actually changed the DACL is
        /// the result's <see cref="ChangeResult.AppliedAcl"/>.
        /// </summary>
        private async Task<StepOutcome> ModifySecurityAsync(PlannedChange change, string dn)
        {
            if (change.Acl is not { IsEmpty: false } acl)
            {
                return Invalid(change, "The security step has no ACE to add or remove.");
            }

            AclChange? applied = null;
            var attempted = await InvokeAsync(change.Action, async () =>
            {
                var current = await _session.GetAsync(dn, [Attr.NtSecurityDescriptor], CancellationToken.None).ConfigureAwait(false)
                    ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The object does not exist.");
                var descriptor = current.GetBytes(Attr.NtSecurityDescriptor)
                    ?? throw new DirectoryException(DirectoryErrorKind.AccessDenied, "The permissions of the object cannot be read.");
                DaclEditResult edited;
                try
                {
                    edited = DaclEditor.Apply(descriptor, acl);
                }
                catch (FormatException ex)
                {
                    throw new DirectoryException(DirectoryErrorKind.InvalidSyntax, "The security descriptor of the object cannot be edited.", ex);
                }

                if (edited.Changed)
                {
                    // Kept before the write: a write that timed out may have reached the DC, and its retry finds nothing to do.
                    applied = Union(applied, edited.Applied);
                    await _session.ModifyAsync(
                        dn,
                        [AttributeModification.Replace(Attr.NtSecurityDescriptor, edited.Descriptor)],
                        DaclWrite,
                        CancellationToken.None).ConfigureAwait(false);
                }

                return true;
            }).ConfigureAwait(false);
            var outcome = ToOutcome(change, attempted);
            return outcome.Result.Succeeded
                ? outcome with { Result = outcome.Result with { AppliedAcl = applied ?? new AclChange([], []) } }
                : outcome;
        }

        /// <summary>The ACEs applied by two attempts of the same step (each ACE once).</summary>
        private static AclChange Union(AclChange? first, AclChange second) => first is null
            ? second
            : new AclChange(
                [.. first.Added, .. second.Added.Where(a => !first.Added.Any(a.SameAce))],
                [.. first.Removed, .. second.Removed.Where(r => !first.Removed.Any(r.SameAce))]);
    }
}
