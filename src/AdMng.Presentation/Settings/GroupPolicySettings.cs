namespace AdMng.Presentation.Settings;

/// <summary>
/// What the Group Policy views remember (design note docs/dev/k-computers-and-gpo.md 8): the tree visibility and the backup
/// defaults. A folder path and a comment template only - never GPO names, settings or backup contents (rule 1).
/// </summary>
public sealed class GroupPolicySettings
{
    /// <summary>Longest remembered comment template.</summary>
    public const int MaxCommentLength = 500;

    /// <summary>"Widok → Zasady grupy w drzewie": the Group Policy nodes under the domain (default on).</summary>
    public bool ShowInTree { get; set; } = true;

    /// <summary>The folder of the last backup, restore or import (a path the operator chose).</summary>
    public string? LastBackupFolder { get; set; }

    /// <summary>The comment proposed for a new backup ({0}: the date).</summary>
    public string? BackupCommentTemplate { get; set; }

    /// <summary>"Utwórz kopię zapasową przed usunięciem" (default on).</summary>
    public bool BackupBeforeDelete { get; set; } = true;

    /// <summary>Repairs a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        LastBackupFolder = string.IsNullOrWhiteSpace(LastBackupFolder) ? null : LastBackupFolder.Trim();
        BackupCommentTemplate = string.IsNullOrWhiteSpace(BackupCommentTemplate)
            ? null
            : BackupCommentTemplate.Length > MaxCommentLength ? BackupCommentTemplate[..MaxCommentLength] : BackupCommentTemplate;
    }
}
