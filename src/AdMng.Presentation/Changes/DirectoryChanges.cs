using AdMng.Core.Batch;
using AdMng.Core.Model;
using Dn = AdMng.Core.Ldap.DistinguishedName;

namespace AdMng.Presentation.Changes;

/// <summary>What happened to an object (as far as the client knows - it made the change itself).</summary>
public enum DirectoryChangeKind
{
    Modified,
    Created,
    Deleted,

    /// <summary>Moved and/or renamed: <see cref="DirectoryChange.NewDistinguishedName"/> holds the new DN.</summary>
    Moved,
}

/// <summary>One change made by this application, published so open lists and trees refresh only affected rows.</summary>
public sealed record DirectoryChange(
    DirectoryChangeKind Kind,
    string DistinguishedName,
    string? NewDistinguishedName = null,
    ObjectKind ObjectKind = ObjectKind.Unknown)
{
    /// <summary>The DN after the change (new DN for moves, otherwise the DN).</summary>
    public string CurrentDistinguishedName => NewDistinguishedName ?? DistinguishedName;

    /// <summary>Parent DN before the change (null for unparsable DNs).</summary>
    public string? OldParentDn => ParentOf(DistinguishedName);

    /// <summary>Parent DN after the change.</summary>
    public string? NewParentDn => ParentOf(CurrentDistinguishedName);

    private static string? ParentOf(string dn) =>
        Dn.TryParse(dn, out var parsed) && parsed.Parent is { IsRoot: false } parent ? parent.ToString() : null;
}

/// <summary>Event data of <see cref="IDirectoryChangeNotifier.Changed"/>.</summary>
public sealed class DirectoryChangesEventArgs(IReadOnlyList<DirectoryChange> changes) : EventArgs
{
    public IReadOnlyList<DirectoryChange> Changes { get; } = changes;
}

/// <summary>
/// Application wide hub for changes made through the change pipeline (shell actions, property sheets, batch
/// editor). Subscribers (lists, navigation tree, open property sheets) refresh the affected objects only.
/// </summary>
public interface IDirectoryChangeNotifier
{
    /// <summary>Raised synchronously on the publishing thread - subscribers marshal to the UI thread themselves.</summary>
    event EventHandler<DirectoryChangesEventArgs>? Changed;

    void Publish(IReadOnlyList<DirectoryChange> changes);
}

/// <summary>Default <see cref="IDirectoryChangeNotifier"/>.</summary>
public sealed class DirectoryChangeNotifier : IDirectoryChangeNotifier
{
    public event EventHandler<DirectoryChangesEventArgs>? Changed;

    public void Publish(IReadOnlyList<DirectoryChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count > 0)
        {
            Changed?.Invoke(this, new DirectoryChangesEventArgs(changes));
        }
    }
}

/// <summary>Derives <see cref="DirectoryChange"/>s from an executed batch.</summary>
public static class DirectoryChanges
{
    /// <summary>
    /// One change per affected object: Created (also for objects restored from the Recycle Bin, at their final DN) /
    /// Deleted / Moved (old -> final DN) or Modified; groups whose membership changed are reported as Modified too.
    /// </summary>
    public static IReadOnlyList<DirectoryChange> FromResult(BatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var changes = new List<DirectoryChange>();
        var seen = new HashSet<(DirectoryChangeKind, string)>();

        void Add(DirectoryChange change)
        {
            if (seen.Add((change.Kind, change.DistinguishedName.ToUpperInvariant())))
            {
                changes.Add(change);
            }
        }

        foreach (var obj in result.Objects)
        {
            var succeeded = obj.Changes.Where(c => c.Succeeded).Select(c => c.Change).ToList();
            if (succeeded.Count == 0)
            {
                continue;
            }

            var dn = obj.Plan.DistinguishedName;
            var kind = obj.Plan.Kind;
            if (succeeded.Any(c => c.Action == ChangeAction.Delete))
            {
                Add(new DirectoryChange(DirectoryChangeKind.Deleted, dn, ObjectKind: kind));
                continue;
            }

            if (succeeded.Any(c => c.Action is ChangeAction.Create or ChangeAction.Restore))
            {
                // A restored object appears at its final DN like a new one (its DN in CN=Deleted Objects was never listed).
                Add(new DirectoryChange(DirectoryChangeKind.Created, obj.FinalDn, ObjectKind: kind));
            }
            else if (!Dn.AreEqual(dn, obj.FinalDn))
            {
                Add(new DirectoryChange(DirectoryChangeKind.Moved, dn, obj.FinalDn, kind));
            }
            else
            {
                Add(new DirectoryChange(DirectoryChangeKind.Modified, dn, ObjectKind: kind));
            }

            foreach (var group in succeeded.Where(c => c.Action is ChangeAction.AddMember or ChangeAction.RemoveMember))
            {
                if (!string.IsNullOrEmpty(group.TargetDn))
                {
                    Add(new DirectoryChange(DirectoryChangeKind.Modified, group.TargetDn, ObjectKind: ObjectKind.Group));
                }
            }
        }

        return changes;
    }
}
