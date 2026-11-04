using AdMng.Core.Security;

namespace AdMng.Core.Readiness;

/// <summary>What the DACL of a folder says about a token's right to create files and folders in it.</summary>
public enum FolderWriteVerdict
{
    /// <summary>Every required right is granted (and none of them denied first).</summary>
    Granted,

    /// <summary>At least one required right is not granted (denied, or no ACE of the token grants it).</summary>
    NotGranted,

    /// <summary>The descriptor is missing, malformed or has no DACL entries: nothing can be said.</summary>
    Unknown,
}

/// <summary>
/// Judges from a folder's security descriptor whether a token may create files and subfolders in it (the readiness check
/// of <c>\\DC\SYSVOL\&lt;domain&gt;\Policies</c>, design note docs/dev/s-pilot-readiness.md 2) – nothing is written to find
/// out. The ACEs are evaluated as AccessCheck does: in the DACL's order, inherit-only and audit ACEs skipped, generic
/// rights mapped to the file rights, a right denied before it is granted stays denied. Pure and in memory.
/// </summary>
public static class FolderWriteAccess
{
    /// <summary>FILE_ADD_FILE (FILE_WRITE_DATA on a folder).</summary>
    public const int AddFile = 0x0002;

    /// <summary>FILE_ADD_SUBDIRECTORY (FILE_APPEND_DATA on a folder).</summary>
    public const int AddSubdirectory = 0x0004;

    /// <summary>What a new GPO folder and a central store file need.</summary>
    public const int CreateFilesAndFolders = AddFile | AddSubdirectory;

    /// <summary>GENERIC_ALL.</summary>
    private const int GenericAll = 0x10000000;

    /// <summary>GENERIC_WRITE.</summary>
    private const int GenericWrite = 0x40000000;

    /// <summary>FILE_ALL_ACCESS.</summary>
    private const int FileAllAccess = 0x001F01FF;

    /// <summary>FILE_GENERIC_WRITE (STANDARD_RIGHTS_WRITE | FILE_WRITE_DATA | FILE_WRITE_ATTRIBUTES | FILE_WRITE_EA | FILE_APPEND_DATA | SYNCHRONIZE).</summary>
    private const int FileGenericWrite = 0x00120116;

    /// <summary>Everyone.</summary>
    public const string EveryoneSid = "S-1-1-0";

    /// <summary>NETWORK: in the token of every logon over SMB.</summary>
    public const string NetworkSid = "S-1-5-2";

    /// <summary>Authenticated Users.</summary>
    public const string AuthenticatedUsersSid = "S-1-5-11";

    /// <summary>This Organization.</summary>
    public const string ThisOrganizationSid = "S-1-5-15";

    /// <summary>
    /// The SIDs of a domain account's network logon to a DC: the account's own SID, its groups (<c>tokenGroups</c>, which
    /// include BUILTIN groups of the DC's domain) and the well-known SIDs every such token holds (Everyone, NETWORK,
    /// Authenticated Users, This Organization).
    /// </summary>
    public static IReadOnlySet<string> NetworkToken(string? accountSid, IEnumerable<string> groupSids)
    {
        ArgumentNullException.ThrowIfNull(groupSids);
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EveryoneSid, NetworkSid, AuthenticatedUsersSid, ThisOrganizationSid };
        if (!string.IsNullOrWhiteSpace(accountSid))
        {
            sids.Add(accountSid);
        }

        foreach (var sid in groupSids)
        {
            if (!string.IsNullOrWhiteSpace(sid))
            {
                sids.Add(sid);
            }
        }

        return sids;
    }

    /// <summary>Whether <paramref name="tokenSids"/> holds every right of <paramref name="required"/> on the folder of <paramref name="descriptor"/>.</summary>
    /// <param name="descriptor">The folder's self-relative security descriptor (binary form), or null.</param>
    /// <param name="tokenSids">The SIDs of the token (<see cref="NetworkToken"/>).</param>
    /// <param name="required">The file rights needed (default <see cref="CreateFilesAndFolders"/>).</param>
    public static FolderWriteVerdict Evaluate(byte[]? descriptor, IReadOnlySet<string> tokenSids, int required = CreateFilesAndFolders)
    {
        ArgumentNullException.ThrowIfNull(tokenSids);
        if (descriptor is null || !SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary is null || summary.Aces.Count == 0)
        {
            return FolderWriteVerdict.Unknown;
        }

        var granted = 0;
        var denied = 0;
        foreach (var ace in summary.Aces)
        {
            if (ace.IsInheritOnly || !(ace.IsAllow || ace.IsDeny) || ace.ObjectType is not null || !tokenSids.Contains(ace.Sid))
            {
                continue;
            }

            var mask = Map(ace.Mask);
            if (ace.IsDeny)
            {
                denied |= mask & ~granted;
            }
            else
            {
                granted |= mask & ~denied;
            }
        }

        return (granted & required) == required ? FolderWriteVerdict.Granted : FolderWriteVerdict.NotGranted;
    }

    /// <summary>The file rights of an ACE mask: GENERIC_ALL and GENERIC_WRITE mapped as the file system maps them.</summary>
    private static int Map(int mask)
    {
        var mapped = mask & ~(GenericAll | GenericWrite | unchecked((int)0x80000000) | 0x20000000);
        if ((mask & GenericAll) != 0)
        {
            mapped |= FileAllAccess;
        }

        if ((mask & GenericWrite) != 0)
        {
            mapped |= FileGenericWrite;
        }

        return mapped;
    }
}
