using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Readiness;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Readiness;

/// <summary>Connection (logon, channel, clock, PDC emulator, RODC, global catalog) and directory (functional levels, Recycle Bin, PSOs, Windows LAPS).</summary>
public sealed partial class ReadinessChecker
{
    private const string BehaviorVersion = "msDS-Behavior-Version";
    private const string EnabledFeature = "msDS-EnabledFeature";
    private const string RecycleBinFeature = "Recycle Bin Feature";
    private const string GlobalCatalogReady = "isGlobalCatalogReady";
    private const string LdapDisplayName = "lDAPDisplayName";
    private const string WindowsLapsPassword = "msLAPS-Password";
    private const string LegacyLapsPassword = "ms-Mcs-AdmPwd";
    private const string PasswordSettingsClass = "msDS-PasswordSettings";

    private async Task ConnectionAsync(RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        var info = context.Session.Info;
        rows.Add(Logon(info));
        rows.Add(Transport(info));
        await AddAsync(rows, ReadinessCodes.ConnectionClockSkew, () => ClockSkewAsync(context.Session, cancellationToken)).ConfigureAwait(false);
        await AddAsync(rows, ReadinessCodes.ConnectionPdcEmulator, () => PdcEmulatorAsync(context.Session, cancellationToken)).ConfigureAwait(false);
        var current = await CurrentControllerAsync(context, cancellationToken).ConfigureAwait(false);
        rows.Add(current is null
            ? NotChecked(ReadinessCodes.ConnectionReadOnlyDc, Localizer.Format(L.Readiness.DcNotListed, info.Server), host: info.Server)
            : current.IsReadOnly
                ? Row(ReadinessCodes.ConnectionReadOnlyDc, ReadinessStatus.Error, Localizer.Format(L.Readiness.RodcError, info.Server), Localizer[L.Readiness.RemedyRodc], info.Server)
                : Ok(ReadinessCodes.ConnectionReadOnlyDc, Localizer.Format(L.Readiness.RodcOk, info.Server), info.Server));
        await AddAsync(rows, ReadinessCodes.ConnectionGlobalCatalog, () => GlobalCatalogAsync(context.Session, current, cancellationToken), info.Server).ConfigureAwait(false);
    }

    private ReadinessResult Logon(SessionInfo info) => info.Authentication switch
    {
        AuthenticationMode.CurrentUser => Ok(ReadinessCodes.ConnectionLogon, Localizer[L.Readiness.LogonCurrentUser]),
        AuthenticationMode.Credentials => Ok(ReadinessCodes.ConnectionLogon, Localizer[L.Readiness.LogonCredentials]),
        _ => Row(ReadinessCodes.ConnectionLogon, ReadinessStatus.Warning, Localizer[L.Readiness.LogonSimpleBind], Localizer[L.Readiness.RemedyWindowsLogon]),
    };

    private ReadinessResult Transport(SessionInfo info)
    {
        var port = Number(info.Port);
        return info.Security switch
        {
            TransportSecurity.SignAndSeal when info.SecurityStrengthFactor is { } ssf and < 56 =>
                Row(ReadinessCodes.ConnectionTransport, ReadinessStatus.Warning, Localizer.Format(L.Readiness.TransportNotSealed, Number(ssf)), Localizer[L.Readiness.RemedyTransport]),
            TransportSecurity.SignAndSeal => Ok(ReadinessCodes.ConnectionTransport, Localizer.Format(L.Readiness.TransportSealed, port)),
            TransportSecurity.Tls => Ok(ReadinessCodes.ConnectionTransport, Localizer.Format(L.Readiness.TransportTls, port)),
            TransportSecurity.StartTls => Ok(ReadinessCodes.ConnectionTransport, Localizer.Format(L.Readiness.TransportStartTls, port)),
            _ => Row(ReadinessCodes.ConnectionTransport, ReadinessStatus.Error, Localizer[L.Readiness.TransportNone], Localizer[L.Readiness.RemedyTransport]),
        };
    }

    /// <summary>RootDSE <c>currentTime</c> of the session's DC against this computer's clock.</summary>
    private async Task<ReadinessResult> ClockSkewAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var server = session.Info.Server;
        var rootDse = await session.GetAsync(string.Empty, [Attr.CurrentTime], cancellationToken).ConfigureAwait(false);
        if (rootDse?.GetGeneralizedTime(Attr.CurrentTime) is not { } dcTime)
        {
            return NotChecked(ReadinessCodes.ConnectionClockSkew, Localizer[L.Readiness.ClockUnknown], host: server);
        }

        var skew = dcTime - _services.Time.GetUtcNow();
        var seconds = Number((long)Math.Round(skew.Duration().TotalSeconds));
        return ReadinessRules.ClassifySkew(skew) switch
        {
            ClockSkewLevel.Ok => Ok(ReadinessCodes.ConnectionClockSkew, Localizer.Format(L.Readiness.ClockOk, server, seconds), server),
            ClockSkewLevel.Warning => Row(ReadinessCodes.ConnectionClockSkew, ReadinessStatus.Warning, Localizer.Format(L.Readiness.ClockWarning, server, seconds),
                Localizer[L.Readiness.RemedyClock], server),
            _ => Row(ReadinessCodes.ConnectionClockSkew, ReadinessStatus.Error, Localizer.Format(L.Readiness.ClockError, server, seconds),
                Localizer[L.Readiness.RemedyClock], server),
        };
    }

    /// <summary>The PDC emulator (fSMORoleOwner of the domain head, as the GPO documents read it) against the session's DC.</summary>
    private async Task<ReadinessResult> PdcEmulatorAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var server = session.Info.Server;
        var pdc = await _services.Reader.ReadPdcEmulatorAsync(session, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pdc))
        {
            return NotChecked(ReadinessCodes.ConnectionPdcEmulator, Localizer[L.Readiness.PdcUnknown], host: server);
        }

        return DomainControllerCatalog.IsSameHost(pdc, server)
            ? Ok(ReadinessCodes.ConnectionPdcEmulator, Localizer.Format(L.Readiness.PdcOk, server), server)
            : Row(ReadinessCodes.ConnectionPdcEmulator, ReadinessStatus.Warning, Localizer.Format(L.Readiness.PdcOther, server, pdc),
                Localizer.Format(L.Readiness.RemedyPdc, pdc), server);
    }

    /// <summary>The session's DC as the configuration partition lists it (null when it is not listed or the list cannot be read).</summary>
    private async Task<DirectoryDomainController?> CurrentControllerAsync(RunContext context, CancellationToken cancellationToken)
    {
        if (context.Controllers is null)
        {
            try
            {
                context.Controllers = await DomainControllerCatalog.ReadAsync(context.Session, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryException)
            {
                context.Controllers = [];
            }
        }

        return context.Controllers.FirstOrDefault(c => DomainControllerCatalog.IsSameHost(c.HostName, context.Session.Info.Server));
    }

    /// <summary>The GC flag of the DC's NTDS Settings, else RootDSE <c>isGlobalCatalogReady</c>.</summary>
    private async Task<ReadinessResult> GlobalCatalogAsync(IDirectorySession session, DirectoryDomainController? current, CancellationToken cancellationToken)
    {
        var server = session.Info.Server;
        var isGc = current?.IsGlobalCatalog;
        if (isGc is null)
        {
            var rootDse = await session.GetAsync(string.Empty, [GlobalCatalogReady], cancellationToken).ConfigureAwait(false);
            isGc = rootDse?.GetString(GlobalCatalogReady) is { } ready ? string.Equals(ready, "TRUE", StringComparison.OrdinalIgnoreCase) : null;
        }

        return isGc switch
        {
            true => Ok(ReadinessCodes.ConnectionGlobalCatalog, Localizer.Format(L.Readiness.GcOk, server), server),
            false => Row(ReadinessCodes.ConnectionGlobalCatalog, ReadinessStatus.Warning, Localizer.Format(L.Readiness.GcNo, server), Localizer[L.Readiness.RemedyGc], server),
            _ => NotChecked(ReadinessCodes.ConnectionGlobalCatalog, Localizer.Format(L.Readiness.DcNotListed, server), host: server),
        };
    }

    // ---- Directory ---------------------------------------------------------------------------------------------------------------

    private async Task DirectoryAsync(RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var info = session.Info;
        await AddAsync(rows, ReadinessCodes.DirectoryDomainLevel, async () =>
        {
            var head = await session.GetAsync(info.DefaultNamingContext, [BehaviorVersion], cancellationToken).ConfigureAwait(false);
            return Level(ReadinessCodes.DirectoryDomainLevel, head?.GetInt32(BehaviorVersion) ?? info.DomainFunctionality);
        }).ConfigureAwait(false);

        // CN=Partitions carries the forest level and the optional features of the forest (the Recycle Bin).
        DirectoryObject? partitions = null;
        Exception? partitionsFailure = null;
        try
        {
            partitions = await session.GetAsync(PartitionsDn(info), [BehaviorVersion, EnabledFeature], cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            partitionsFailure = ex;
        }

        rows.Add(Level(ReadinessCodes.DirectoryForestLevel, partitions?.GetInt32(BehaviorVersion) ?? info.ForestFunctionality));
        var recycleBin = partitions is null
            ? info.RecycleBinEnabled
            : partitions.GetStrings(EnabledFeature).Any(dn => DistinguishedName.TryParse(dn, out var feature) && !feature.IsRoot
                && string.Equals(feature.Name, RecycleBinFeature, StringComparison.OrdinalIgnoreCase));
        rows.Add(partitionsFailure is not null && !info.RecycleBinEnabled
            ? Failed(ReadinessCodes.DirectoryRecycleBin, partitionsFailure)
            : recycleBin
                ? Ok(ReadinessCodes.DirectoryRecycleBin, Localizer[L.Readiness.RecycleBinOn])
                : Row(ReadinessCodes.DirectoryRecycleBin, ReadinessStatus.Warning, Localizer[L.Readiness.RecycleBinOff], Localizer[L.Readiness.RemedyRecycleBin]));
        await AddAsync(rows, ReadinessCodes.DirectoryPasswordSettings, () => PasswordSettingsAsync(session, cancellationToken)).ConfigureAwait(false);
        await AddAsync(rows, ReadinessCodes.DirectoryWindowsLaps, () => WindowsLapsAsync(session, cancellationToken)).ConfigureAwait(false);
    }

    private static string PartitionsDn(SessionInfo info) => DistinguishedName.Parse(info.ConfigurationNamingContext).Child("CN", "Partitions").ToString();

    private ReadinessResult Level(string code, int level)
    {
        var name = ReadinessRules.LevelName(level) ?? Localizer.Format(L.Readiness.LevelNumber, Number(level));
        return ReadinessRules.ClassifyLevel(level) switch
        {
            FunctionalLevelVerdict.Ok => Ok(code, Localizer.Format(L.Readiness.LevelOk, name, Number(level))),
            FunctionalLevelVerdict.Old => Row(code, ReadinessStatus.Warning, Localizer.Format(L.Readiness.LevelOld, name, Number(level)), Localizer[L.Readiness.RemedyLevel]),
            _ => Row(code, ReadinessStatus.Error, Localizer.Format(L.Readiness.LevelTooOld, name, Number(level)), Localizer[L.Readiness.RemedyLevel]),
        };
    }

    /// <summary>The PSO container readable, and how many PSOs the account sees (by default only Domain Admins see them).</summary>
    private async Task<ReadinessResult> PasswordSettingsAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var container = DistinguishedName.Parse(session.Info.DefaultNamingContext).Child("CN", "System").Child("CN", "Password Settings Container").ToString();
        DirectoryObject? found;
        try
        {
            found = await session.GetAsync(container, [Attr.ObjectClass], cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.AccessDenied)
        {
            found = null;
        }

        if (found is null)
        {
            return Row(ReadinessCodes.DirectoryPasswordSettings, ReadinessStatus.Warning, Localizer[L.Readiness.PsoNotReadable], Localizer[L.Readiness.RemedyPso]);
        }

        var count = 0;
        await foreach (var _ in session.SearchAsync(new DirectorySearch
        {
            BaseDn = container,
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.Equal(Attr.ObjectClass, PasswordSettingsClass),
            Attributes = [Attr.Name],
            SizeLimit = 1000,
        }, cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return Ok(ReadinessCodes.DirectoryPasswordSettings, count == 0
            ? Localizer[L.Readiness.PsoNoneVisible]
            : Localizer.Format(L.Readiness.PsoVisible, Number(count)));
    }

    /// <summary>Windows LAPS (msLAPS-Password) or legacy LAPS (ms-Mcs-AdmPwd) in the schema: read live, the session's schema as a fallback.</summary>
    private async Task<ReadinessResult> WindowsLapsAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var attribute in session.SearchAsync(new DirectorySearch
            {
                BaseDn = session.Info.SchemaNamingContext,
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Or([LdapFilter.Equal(LdapDisplayName, WindowsLapsPassword), LdapFilter.Equal(LdapDisplayName, LegacyLapsPassword)]),
                Attributes = [LdapDisplayName],
                SizeLimit = 10,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (attribute.GetString(LdapDisplayName) is { } name)
                {
                    names.Add(name);
                }
            }
        }
        catch (DirectoryException)
        {
            // The schema the session loaded when it connected answers instead.
        }

        var windows = names.Contains(WindowsLapsPassword) || session.Schema.FindAttribute(WindowsLapsPassword) is not null;
        var legacy = names.Contains(LegacyLapsPassword) || session.Schema.FindAttribute(LegacyLapsPassword) is not null;
        return windows
            ? Ok(ReadinessCodes.DirectoryWindowsLaps, Localizer[L.Readiness.LapsWindows])
            : Row(ReadinessCodes.DirectoryWindowsLaps, ReadinessStatus.Warning, Localizer[legacy ? L.Readiness.LapsLegacy : L.Readiness.LapsNone], Localizer[L.Readiness.RemedyLaps]);
    }
}
