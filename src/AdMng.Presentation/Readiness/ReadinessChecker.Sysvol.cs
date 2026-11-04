using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;
using AdMng.Core.Readiness;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// SYSVOL and Group Policy: SYSVOL of the session's DC opened as the GPO documents open it, the write permission on
/// <c>Policies</c> judged from its ACL (never by writing), the central store, the two default GPOs and DFSR against FRS.
/// </summary>
public sealed partial class ReadinessChecker
{
    private const int MaxCentralStoreEntries = 5000;
    private const long MaxGptIniBytes = 64 * 1024;
    private const string GptIni = "GPT.INI";
    private const string DefaultDomainPolicyName = "Default Domain Policy";
    private const string DefaultDomainControllersPolicyName = "Default Domain Controllers Policy";

    private async Task SysvolAsync(RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var server = session.Info.Server;
        ISysvolWriteConnection? connection = null;
        if (_services.GroupPolicy is not { } gp)
        {
            rows.Add(NotChecked(ReadinessCodes.SysvolReachable, Localizer[L.Readiness.NotAvailable], host: server));
        }
        else
        {
            var access = await gp.OpenSysvolAsync(session, cancellationToken).ConfigureAwait(false);
            connection = access.Connection;
            rows.Add(connection is not null
                ? Ok(ReadinessCodes.SysvolReachable, Localizer.Format(L.Readiness.SysvolOpen, server), server)
                : Row(ReadinessCodes.SysvolReachable, ReadinessStatus.Error, gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, server),
                    Localizer[SysvolRemedyKey(access.Reason ?? SysvolUnavailableReason.Failed)], server));
        }

        try
        {
            var skipped = Localizer[_services.GroupPolicy is null ? L.Readiness.NotAvailable : L.Readiness.SysvolSkipped];
            if (connection is null)
            {
                rows.Add(NotChecked(ReadinessCodes.SysvolPoliciesWrite, skipped, host: server));
                rows.Add(NotChecked(ReadinessCodes.SysvolCentralStore, skipped, host: server));
            }
            else
            {
                await AddAsync(rows, ReadinessCodes.SysvolPoliciesWrite, () => PoliciesWriteAsync(session, connection, cancellationToken), server).ConfigureAwait(false);
                await AddAsync(rows, ReadinessCodes.SysvolCentralStore, () => CentralStoreAsync(connection, server, cancellationToken), server).ConfigureAwait(false);
            }

            await AddAsync(rows, ReadinessCodes.SysvolDefaultGpos, () => DefaultGposAsync(session, connection, cancellationToken)).ConfigureAwait(false);
            await AddAsync(rows, ReadinessCodes.SysvolReplication, () => ReplicationAsync(session, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string SysvolRemedyKey(SysvolUnavailableReason reason) => reason switch
    {
        SysvolUnavailableReason.DistinguishedNameLogon => L.Readiness.RemedySysvolDnLogon,
        SysvolUnavailableReason.LogonFailure => L.Readiness.RemedySysvolLogonFailure,
        SysvolUnavailableReason.AccessDenied => L.Readiness.RemedySysvolAccessDenied,
        SysvolUnavailableReason.Unreachable => L.Readiness.RemedySysvolUnreachable,
        SysvolUnavailableReason.NotSupported => L.Readiness.RemedySysvolNotSupported,
        _ => L.Readiness.RemedySysvolFailed,
    };

    /// <summary>
    /// The ACL of <c>Policies</c> evaluated for the window's account: its SID, its groups (<c>tokenGroups</c>) and the
    /// well-known SIDs of a network logon. Nothing is written.
    /// </summary>
    private async Task<ReadinessResult> PoliciesWriteAsync(IDirectorySession session, ISysvolWriteConnection connection, CancellationToken cancellationToken)
    {
        var server = session.Info.Server;
        byte[]? descriptor;
        try
        {
            descriptor = await connection.GetPoliciesFolderSecurityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SysvolWriteException)
        {
            descriptor = null;
        }

        if (descriptor is null)
        {
            return NotChecked(ReadinessCodes.SysvolPoliciesWrite, Localizer[L.Readiness.PoliciesWriteUnknown], host: server);
        }

        var token = await TokenAsync(session, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return NotChecked(ReadinessCodes.SysvolPoliciesWrite, Localizer[L.Readiness.PoliciesWriteNoToken], host: server);
        }

        return FolderWriteAccess.Evaluate(descriptor, token) switch
        {
            FolderWriteVerdict.Granted => Ok(ReadinessCodes.SysvolPoliciesWrite, Localizer[L.Readiness.PoliciesWriteGranted], server),
            FolderWriteVerdict.NotGranted => Row(ReadinessCodes.SysvolPoliciesWrite, ReadinessStatus.Warning, Localizer[L.Readiness.PoliciesWriteDenied],
                Localizer[L.Readiness.RemedyPoliciesWrite], server),
            _ => NotChecked(ReadinessCodes.SysvolPoliciesWrite, Localizer[L.Readiness.PoliciesWriteUnknown], host: server),
        };
    }

    /// <summary>The SIDs of the window's account on the DC (null when its groups cannot be read).</summary>
    private static async Task<IReadOnlySet<string>?> TokenAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        if (session.Info.BoundUserDn is not { Length: > 0 } userDn)
        {
            return null;
        }

        DirectoryObject? user;
        try
        {
            user = await session.GetAsync(userDn, [Attr.TokenGroups, Attr.ObjectSid], cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            return null;
        }

        if (user is null || !user.TryGet(Attr.TokenGroups, out var groups) || groups.Count == 0)
        {
            return null;
        }

        var sids = groups.OfType<byte[]>().Where(b => b.Length >= 8).Select(b => AdConvert.SidToString(b));
        return FolderWriteAccess.NetworkToken(user.ObjectSid, sids);
    }

    /// <summary>The central store: its ADMX files (top level) and the languages of its ADML files.</summary>
    private async Task<ReadinessResult> CentralStoreAsync(ISysvolWriteConnection connection, string server, CancellationToken cancellationToken)
    {
        IReadOnlyList<SysvolEntry>? entries;
        try
        {
            entries = await connection.ListPolicyDefinitionsAsync(MaxCentralStoreEntries, cancellationToken).ConfigureAwait(false);
        }
        catch (SysvolWriteException)
        {
            return Row(ReadinessCodes.SysvolCentralStore, ReadinessStatus.Warning, Localizer[L.Readiness.CentralStoreFailed], Localizer[L.Readiness.RemedySysvolFailed], server);
        }

        if (entries is null)
        {
            return Row(ReadinessCodes.SysvolCentralStore, ReadinessStatus.Warning, Localizer[L.Readiness.CentralStoreMissing], Localizer[L.Readiness.RemedyCentralStore], server);
        }

        var admx = entries.Count(e => !e.IsFolder && !e.RelativePath.Contains('/', StringComparison.Ordinal)
            && e.RelativePath.EndsWith(".admx", StringComparison.OrdinalIgnoreCase));
        var languages = entries
            .Where(e => !e.IsFolder && e.RelativePath.EndsWith(".adml", StringComparison.OrdinalIgnoreCase) && e.RelativePath.IndexOf('/', StringComparison.Ordinal) > 0)
            .Select(e => e.RelativePath[..e.RelativePath.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (admx == 0)
        {
            return Row(ReadinessCodes.SysvolCentralStore, ReadinessStatus.Warning, Localizer[L.Readiness.CentralStoreEmpty], Localizer[L.Readiness.RemedyCentralStore], server);
        }

        return languages.Count == 0
            ? Row(ReadinessCodes.SysvolCentralStore, ReadinessStatus.Warning, Localizer.Format(L.Readiness.CentralStoreNoLanguage, Number(admx)),
                Localizer[L.Readiness.RemedyCentralStore], server)
            : Ok(ReadinessCodes.SysvolCentralStore, Localizer.Format(L.Readiness.CentralStoreOk, Number(admx), string.Join(", ", languages)), server);
    }

    /// <summary>Default Domain Policy and Default Domain Controllers Policy: the container in AD and GPT.INI in SYSVOL.</summary>
    private async Task<ReadinessResult> DefaultGposAsync(IDirectorySession session, ISysvolWriteConnection? connection, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        foreach (var (id, name) in new[]
        {
            (GpoInfo.DefaultDomainPolicyId, DefaultDomainPolicyName),
            (GpoInfo.DefaultDomainControllersPolicyId, DefaultDomainControllersPolicyName),
        })
        {
            if (await _services.Reader.ReadPolicyAsync(session, id, cancellationToken).ConfigureAwait(false) is null)
            {
                missing.Add(Localizer.Format(L.Readiness.DefaultGpoInAd, name));
            }

            if (connection is not null
                && (await connection.ReadGpoFileAsync(id, GptIni, MaxGptIniBytes, cancellationToken).ConfigureAwait(false)).Status != SysvolFileStatus.Read)
            {
                missing.Add(Localizer.Format(L.Readiness.DefaultGpoInSysvol, name));
            }
        }

        if (missing.Count > 0)
        {
            return Row(ReadinessCodes.SysvolDefaultGpos, ReadinessStatus.Error, Localizer.Format(L.Readiness.DefaultGposMissing, string.Join(", ", missing)),
                Localizer[L.Readiness.RemedyDefaultGpos]);
        }

        return connection is null
            ? NotChecked(ReadinessCodes.SysvolDefaultGpos, Localizer[L.Readiness.DefaultGposAdOnly])
            : Ok(ReadinessCodes.SysvolDefaultGpos, Localizer[L.Readiness.DefaultGposOk]);
    }

    /// <summary>DFSR or FRS for SYSVOL (<see cref="SysvolReplication"/>): three reads of the domain's CN=System.</summary>
    private async Task<ReadinessResult> ReplicationAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var domain = session.Info.DefaultNamingContext;
        var settings = await TryGetAsync(session, SysvolReplication.GlobalSettingsDn(domain), SysvolReplication.FlagsAttribute, cancellationToken).ConfigureAwait(false);
        var dfsrGroup = await TryGetAsync(session, SysvolReplication.DfsrGroupDn(domain), Attr.ObjectClass, cancellationToken).ConfigureAwait(false);
        var frsReplicaSet = await TryGetAsync(session, SysvolReplication.FrsReplicaSetDn(domain), Attr.ObjectClass, cancellationToken).ConfigureAwait(false);
        var flags = settings?.GetInt32(SysvolReplication.FlagsAttribute);
        return SysvolReplication.Classify(flags, dfsrGroup is not null, frsReplicaSet is not null) switch
        {
            SysvolReplicationState.Dfsr => Ok(ReadinessCodes.SysvolReplication, Localizer[L.Readiness.ReplicationDfsr]),
            SysvolReplicationState.MigrationRedirected => Row(ReadinessCodes.SysvolReplication, ReadinessStatus.Warning, Localizer[L.Readiness.ReplicationRedirected],
                Localizer[L.Readiness.RemedyReplicationFinish]),
            SysvolReplicationState.MigrationPrepared => Row(ReadinessCodes.SysvolReplication, ReadinessStatus.Error, Localizer.Format(L.Readiness.ReplicationMigrating, "Prepared"),
                Localizer[L.Readiness.RemedyReplicationMigrate]),
            SysvolReplicationState.MigrationStarted => Row(ReadinessCodes.SysvolReplication, ReadinessStatus.Error, Localizer.Format(L.Readiness.ReplicationMigrating, "Start"),
                Localizer[L.Readiness.RemedyReplicationMigrate]),
            SysvolReplicationState.Frs => Row(ReadinessCodes.SysvolReplication, ReadinessStatus.Error, Localizer[L.Readiness.ReplicationFrs], Localizer[L.Readiness.RemedyReplicationMigrate]),
            _ => NotChecked(ReadinessCodes.SysvolReplication, Localizer[L.Readiness.ReplicationUnknown]),
        };
    }

    /// <summary>A base read that answers null for an object that is missing or cannot be read.</summary>
    private static async Task<DirectoryObject?> TryGetAsync(IDirectorySession session, string dn, string attribute, CancellationToken cancellationToken)
    {
        try
        {
            return await session.GetAsync(dn, [attribute], cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.AccessDenied)
        {
            return null;
        }
    }
}
