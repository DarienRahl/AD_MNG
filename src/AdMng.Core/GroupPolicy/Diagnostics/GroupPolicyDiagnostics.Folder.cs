using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>The rules of a GPO's SYSVOL folder (Full).</summary>
public static partial class GroupPolicyDiagnostics
{
    private static void FolderRules(Context c, GpoInfo gpo, GpoFolderView view)
    {
        var state = c.Snapshot.Folders[gpo.Id];
        if (view.Content.Leftovers.Count > 0)
        {
            c.Add(GpoFindingCodes.Leftovers, gpo.Id, arguments: [Number(view.Content.Leftovers.Count)]);
        }

        if (view.File(GptIni.RelativePath) is { } gptIni)
        {
            try
            {
                GptIni.ReadVersion(gptIni);
            }
            catch (GpoParseException)
            {
                c.Add(GpoFindingCodes.UnreadableFile, gpo.Id, settingKey: "File/" + GptIni.RelativePath, arguments: [GptIni.RelativePath]);
            }
        }

        foreach (var marker in view.Settings.Where(s => s.Kind == GpoSettingKind.Unreadable))
        {
            var path = marker.Source ?? marker.Key["File/".Length..];
            c.Add(GpoFindingCodes.UnreadableFile, gpo.Id, settingKey: marker.Key, arguments: [path]);
        }

        var missing = GroupPolicyPlanner.MissingExtensions(gpo.MachineExtensionNames, gpo.UserExtensionNames, view.Content.Files);
        if (missing.Count > 0)
        {
            c.Add(GpoFindingCodes.ExtensionNamesMissing, gpo.Id,
                arguments: [string.Join(", ", missing.Select(m => m.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase))],
                fix: new GpoQuickFix.RepairExtensionNames(gpo.Id));
        }

        if (state.SecurityRead && state.Security is { } folderSecurity && gpo.SecurityDescriptor is { } containerSecurity
            && !GpoFolderAcl.IsConsistent(folderSecurity, containerSecurity))
        {
            c.Add(GpoFindingCodes.FolderSecurityMismatch, gpo.Id, fix: new GpoQuickFix.RepairFolderSecurity(gpo.Id));
        }

        // MS14-025: the parsers mask a non-empty cpassword (GpoSecretSettings) - only its presence is known here.
        foreach (var secret in view.Settings.Where(s => s.Kind == GpoSettingKind.Masked
            && s.Key.EndsWith("/" + GpoSecretSettings.CPassword, StringComparison.OrdinalIgnoreCase)))
        {
            c.Add(GpoFindingCodes.GppPassword, gpo.Id, settingKey: secret.Key, arguments: [secret.Source ?? string.Empty]);
        }
    }
}
