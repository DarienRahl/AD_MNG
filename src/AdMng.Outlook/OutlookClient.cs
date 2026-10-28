using System.Runtime.Versioning;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// Classic Outlook through COM late binding (design note docs/dev/u5-outlook-correspondence.md 4.1):
/// <see cref="Availability"/> is the cached ProgID lookup (no start, no attach); <see cref="ConnectAsync"/> attaches to the
/// running Outlook or starts it on a new STA worker (3 attempts, 700 ms apart) and loads the profile and its accounts. The
/// constructor never touches COM.
/// </summary>
public sealed class OutlookClient : IOutlookClient
{
    /// <summary>The ProgID of classic Outlook ("new Outlook" does not register it).</summary>
    public const string ProgId = "Outlook.Application";

    private readonly Lazy<OutlookAvailability> _availability = new(Probe);

    /// <param name="time">The clock of the attempts (null: the system clock).</param>
    public OutlookClient(TimeProvider? time = null)
        : this(null, time)
    {
    }

    /// <param name="provider">The source of the Outlook application object (tests pass CLR fakes); null: COM.</param>
    /// <param name="time">The clock of the attempts (null: the system clock).</param>
    internal OutlookClient(IOutlookApplicationProvider? provider, TimeProvider? time = null)
    {
        Provider = provider;
        Time = time ?? TimeProvider.System;
    }

    public OutlookAvailability Availability => _availability.Value;

    /// <summary>The application object source of the tests, or null (COM).</summary>
    internal IOutlookApplicationProvider? Provider { get; }

    internal TimeProvider Time { get; }

    public Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken) => throw new NotImplementedException("U5 T3 outlook");

    public override string ToString() => nameof(OutlookClient);

    private static OutlookAvailability Probe() => OperatingSystem.IsWindows() ? ProbeOnWindows() : OutlookAvailability.NotSupported;

    [SupportedOSPlatform("windows")]
    private static OutlookAvailability ProbeOnWindows() =>
        Type.GetTypeFromProgID(ProgId) is null ? OutlookAvailability.NotInstalled : OutlookAvailability.Installed;
}

/// <summary>
/// The only place that creates COM objects (design note 4.1): attach to the running Outlook (oleaut32 <c>GetActiveObject</c>
/// via <c>CLSIDFromProgID</c>) or start it (<c>Activator.CreateInstance</c>). Tests pass CLR fakes of the object model.
/// </summary>
internal interface IOutlookApplicationProvider
{
    /// <summary>The running Outlook's application object.</summary>
    object Attach();

    /// <summary>A started Outlook's application object.</summary>
    object Start();
}
