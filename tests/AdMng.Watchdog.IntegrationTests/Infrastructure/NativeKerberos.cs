using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// MIT Kerberos helpers of the keytab lab tests (Linux): empty the process's default credential cache (the MEMORY ccache
/// of <c>KRB5CCNAME</c>, so the next GSSAPI initiator takes its TGT from <c>KRB5_CLIENT_KTNAME</c> again), point the
/// calling thread's GSSAPI at another ccache (<c>gss_krb5_ccache_name</c>, thread-local), and a complete in-process
/// Negotiate exchange (the API's acceptor uses <c>KRB5_KTNAME</c>, exactly as Kestrel's Negotiate handler does).
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class NativeKerberos
{
    /// <summary>krb5_cc_destroy of the default ccache (a fresh context reads <c>KRB5CCNAME</c>).</summary>
    public static void DestroyDefaultCcache()
    {
        if (InitContext(out var context) != 0)
        {
            return;
        }

        try
        {
            if (CcDefault(context, out var ccache) == 0)
            {
                _ = CcDestroy(context, ccache);
            }
        }
        finally
        {
            FreeContext(context);
        }
    }

    /// <summary>Runs <paramref name="action"/> with the calling thread's GSSAPI using <paramref name="ccacheName"/>.</summary>
    public static T WithThreadCcache<T>(string ccacheName, Func<T> action)
    {
        if (GssKrb5CcacheName(out _, ccacheName, IntPtr.Zero) != 0)
        {
            throw new InvalidOperationException("gss_krb5_ccache_name failed.");
        }

        try
        {
            return action();
        }
        finally
        {
            _ = GssKrb5CcacheName(out _, null, IntPtr.Zero);
        }
    }

    /// <summary>A Negotiate (SPNEGO/Kerberos) exchange of default client credentials with the default acceptor for
    /// <paramref name="spn"/>; returns the client name the acceptor authenticated, or throws.</summary>
    public static string Negotiate(string spn)
    {
        using var client = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "Negotiate",
            TargetName = spn,
            Credential = CredentialCache.DefaultNetworkCredentials,
        });
        using var server = new NegotiateAuthentication(new NegotiateAuthenticationServerOptions
        {
            Package = "Negotiate",
            Credential = CredentialCache.DefaultNetworkCredentials,
        });
        var token = client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out var clientStatus);
        for (var round = 0; round < 5 && token is not null; round++)
        {
            Check(clientStatus, "client");
            var reply = server.GetOutgoingBlob(token, out var serverStatus);
            Check(serverStatus, "acceptor");
            if (reply is null || client.IsAuthenticated)
            {
                break;
            }

            token = client.GetOutgoingBlob(reply, out clientStatus);
            if (server.IsAuthenticated && client.IsAuthenticated)
            {
                break;
            }
        }

        return server.IsAuthenticated
            ? server.RemoteIdentity.Name ?? string.Empty
            : throw new InvalidOperationException("The Negotiate exchange did not complete.");
    }

    private static void Check(NegotiateAuthenticationStatusCode status, string side)
    {
        if (status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
        {
            throw new InvalidOperationException($"Negotiate {side}: {status}.");
        }
    }

    [LibraryImport("libkrb5.so.3", EntryPoint = "krb5_init_context")]
    private static partial int InitContext(out IntPtr context);

    [LibraryImport("libkrb5.so.3", EntryPoint = "krb5_free_context")]
    private static partial void FreeContext(IntPtr context);

    [LibraryImport("libkrb5.so.3", EntryPoint = "krb5_cc_default")]
    private static partial int CcDefault(IntPtr context, out IntPtr ccache);

    [LibraryImport("libkrb5.so.3", EntryPoint = "krb5_cc_destroy")]
    private static partial int CcDestroy(IntPtr context, IntPtr ccache);

    [LibraryImport("libgssapi_krb5.so.2", EntryPoint = "gss_krb5_ccache_name", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint GssKrb5CcacheName(out uint minor, string? name, IntPtr outName);
}
