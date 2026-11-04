using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;
using AdMng.Outlook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.Correspondence;

/// <summary>
/// Outlook for a host that must never start or attach to Outlook, nor read the user's signatures (the smoke test of the
/// published exe; CI agents have no Outlook): the client reports <see cref="OutlookAvailability.NotInstalled"/> and every
/// connection fails with <c>outlook.notInstalled</c>, the signature source is empty and the U1 Outlook transport runs over
/// that client.
/// </summary>
internal static class OfflineOutlook
{
    /// <summary>The failure code of a connection (design note 4.3).</summary>
    public const string NotInstalledCode = "outlook.notInstalled";

    /// <summary>Replaces the Outlook client, the signatures and the U1 Outlook transport of <paramref name="services"/>.</summary>
    public static void Use(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var client = new OfflineOutlookClient();
        services.Replace(ServiceDescriptor.Singleton<IOutlookClient>(client));
        services.Replace(ServiceDescriptor.Singleton<IOutlookSignatureSource>(new OfflineSignatureSource()));
        services.Replace(ServiceDescriptor.Singleton<IOutlookUserMessageTransportFactory>(new OutlookUserMessageTransportFactory(client)));
    }

    /// <summary>A computer without classic Outlook.</summary>
    internal sealed class OfflineOutlookClient : IOutlookClient
    {
        public OutlookAvailability Availability => OutlookAvailability.NotInstalled;

        public Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new OutlookConnectResult(null, new OutlookFailure(OutlookFailureKind.NotInstalled, NotInstalledCode, "connect", null)));
        }

        public override string ToString() => nameof(OfflineOutlookClient);
    }

    /// <summary>No signatures folder.</summary>
    internal sealed class OfflineSignatureSource : IOutlookSignatureSource
    {
        public string? Folder => null;

        public IReadOnlyList<string> List() => [];

        public Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken) =>
            Task.FromResult<SignatureParseResult?>(null);

        public override string ToString() => nameof(OfflineSignatureSource);
    }
}
