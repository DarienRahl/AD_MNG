using System.Text.Json;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Identity;

/// <summary>
/// The service's own computer account for the detection (stage B design 9.3), next to its own-account monitor (4.7):
/// the sAMAccountName comes from <c>Watchdog:Kerberos:ServicePrincipal</c> at once, objectGUID and SID from the
/// <see cref="ServiceAccountMonitor"/> once it resolved the account; the rotation record is the keytab tool's
/// <c>status.json</c> (<c>Watchdog:Kerberos:KeytabStatusFile</c>), read when the detection sees a password change of the
/// account. The tool writes the file seconds after the change, so a record that still names an older key version is
/// re-read every <see cref="PollInterval"/> for up to <see cref="MaxWait"/>.
/// </summary>
public sealed class WatchdogAccountSource : IWatchdogAccountSource
{
    /// <summary>How long a stale <c>status.json</c> is waited for.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>Re-read interval while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private (Guid? Guid, string? Sid) _resolved;

    public WatchdogAccountSource(IOptionsMonitor<ServiceOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
    }

    /// <inheritdoc/>
    public WatchdogAccountIdentity? Identity
    {
        get
        {
            if (WatchdogAccountIdentity.SamAccountNameOf(_options.CurrentValue.Kerberos.ServicePrincipal) is not { } name)
            {
                return null;
            }

            lock (_gate)
            {
                return new WatchdogAccountIdentity { SamAccountName = name, ObjectGuid = _resolved.Guid, Sid = _resolved.Sid };
            }
        }
    }

    /// <summary>The account as the own-account monitor found it (objectGUID and SID).</summary>
    public void Update(Guid? objectGuid, string? sid)
    {
        lock (_gate)
        {
            _resolved = (objectGuid ?? _resolved.Guid, sid ?? _resolved.Sid);
        }
    }

    /// <inheritdoc/>
    public async Task<KeytabRotationRecord?> GetRotationAsync(int keyVersionNumber, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + MaxWait;
        while (true)
        {
            var record = Read();
            if (record is not { Kvno: { } kvno } || kvno >= keyVersionNumber || _time.GetUtcNow() >= deadline)
            {
                return record;
            }

            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private KeytabRotationRecord? Read()
    {
        try
        {
            return KeytabToolStatus.Read(_options.CurrentValue.Kerberos.KeytabStatusFile) is { } status
                ? new KeytabRotationRecord(status.Kvno, status.LastChange, status.Result)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;    // the own-account monitor reports an unreadable status file
        }
    }
}
