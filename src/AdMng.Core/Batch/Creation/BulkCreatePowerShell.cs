namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The semantic PowerShell of a created object (docs/dev/phase3-bulk-create.md 2.2, owner B1): <c>New-ADUser</c>,
/// <c>New-ADGroup</c> or <c>New-ADComputer</c> with the Create values, <c>-AccountPassword (Read-Host -AsSecureString …)</c>
/// for a reset (never the password), then <c>Set-AD… -Manager/-ManagedBy</c> and <c>Add-ADGroupMember</c>. The planner sets
/// <see cref="ObjectPlan.PowerShell"/> to it (2.5).
/// </summary>
public static class BulkCreatePowerShell
{
    /// <summary>
    /// The commands of <paramref name="objectPlan"/>, or null for the exporter's generic per-step lines (contacts, computers
    /// with a join right – and, in the contract commit, every object).
    /// </summary>
    public static PowerShellEquivalent? For(ObjectPlan objectPlan)
    {
        ArgumentNullException.ThrowIfNull(objectPlan);
        return null;
    }
}
