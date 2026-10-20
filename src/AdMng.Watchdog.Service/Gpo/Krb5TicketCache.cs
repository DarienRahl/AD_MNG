using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Service.Gpo;

/// <summary>Provides the TGT smbclient needs in its credential cache (stage B design 8.3).</summary>
public interface ISmbTicketSource
{
    /// <summary>Makes sure the ccache smbclient is started with holds a TGT of the service account with at least
    /// <see cref="Krb5TicketCache.MinimumLifetime"/> left.</summary>
    /// <exception cref="SysvolReadException">No TGT could be obtained (<see cref="SysvolErrorKind.Authentication"/>).</exception>
    Task EnsureAsync(CancellationToken cancellationToken);
}

/// <summary>Where the TGT comes from and where it goes.</summary>
public sealed record Krb5TicketCacheOptions
{
    /// <summary>The service account principal, e.g. <c>ADMNG-WD$@CONTOSO.COM</c> (bootstrap <c>Watchdog:Kerberos:ServicePrincipal</c>).</summary>
    public required string Principal { get; init; }

    /// <summary>The client keytab (<c>KRB5_CLIENT_KTNAME</c> of the unit), e.g. <c>FILE:/etc/admng-watchdog/admng-wd.keytab</c>.</summary>
    public required string ClientKeytab { get; init; }

    /// <summary>A <c>FILE:</c> ccache (<c>KRB5CCNAME</c> of the unit: <c>FILE:/run/admng-watchdog/krb5cc</c>).</summary>
    public required string CredentialCache { get; init; }

    /// <summary>krb5.conf to use; null = libkrb5's default (<c>KRB5_CONFIG</c> of the process environment).</summary>
    public string? Krb5Config { get; init; }

    /// <summary>Requested ticket lifetime; null = the krb5.conf/KDC default. Tests request 1 minute.</summary>
    public TimeSpan? TicketLifetime { get; init; }
}

/// <summary>
/// Stage B design 8.3, "TGT before every invocation": smbclient never obtains a TGT from the client keytab, it only uses
/// a valid TGT in <c>KRB5CCNAME</c> (empty ccache → <c>NT_STATUS_INVALID_PARAMETER</c>, expired → <c>NT_STATUS_LOGON_FAILURE</c>).
/// Before every run the cache is checked: when its TGT for the service account has less than <see cref="MinimumLifetime"/>
/// left (or there is none, or another principal's), a new one is obtained with <c>krb5_get_init_creds_keytab</c>, stored
/// with <c>krb5_cc_initialize</c>/<c>krb5_cc_store_cred</c> into a temporary ccache next to the target and renamed over
/// it atomically – smbclient and GSSAPI never see a half-written cache. MIT libkrb5 through P/Invoke (Linux only).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class Krb5TicketCache : ISmbTicketSource
{
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(15);

    private const string Library = "libkrb5.so.3";

    private readonly Krb5TicketCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cachePath;

    public Krb5TicketCache(Krb5TicketCacheOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientKeytab);
        if (!options.CredentialCache.StartsWith("FILE:", StringComparison.Ordinal) || options.CredentialCache.Length <= 5)
        {
            throw new ArgumentException("The credential cache for smbclient must be a FILE: ccache.", nameof(options));
        }

        _options = options;
        _time = time;
        _cachePath = options.CredentialCache[5..];
    }

    /// <summary>TGTs obtained so far (tests, diagnostics).</summary>
    public int Acquisitions { get; private set; }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = OpenContext();
            try
            {
                if (RemainingLifetime(context) is { } left && left >= MinimumLifetime)
                {
                    return;
                }

                Acquire(context);
                Acquisitions++;
            }
            finally
            {
                FreeContext(context);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lifetime left of the cached TGT of the service account; null when there is none.</summary>
    private TimeSpan? RemainingLifetime(IntPtr context)
    {
        if (!File.Exists(_cachePath) || CcResolve(context, _options.CredentialCache, out var cache) != 0)
        {
            return null;
        }

        IntPtr client = IntPtr.Zero, cachePrincipal = IntPtr.Zero, server = IntPtr.Zero;
        try
        {
            if (ParseName(context, _options.Principal, out client) != 0
                || CcGetPrincipal(context, cache, out cachePrincipal) != 0
                || PrincipalCompare(context, client, cachePrincipal) == 0
                || ParseName(context, "krbtgt/" + Realm(_options.Principal) + "@" + Realm(_options.Principal), out server) != 0)
            {
                return null;
            }

            var match = new Krb5Creds { Client = cachePrincipal, Server = server };
            if (CcRetrieveCred(context, cache, 0, ref match, out var creds) != 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds((uint)creds.EndTime) - _time.GetUtcNow();
            }
            finally
            {
                FreeCredContents(context, ref creds);
            }
        }
        finally
        {
            FreeIfSet(context, client);
            FreeIfSet(context, cachePrincipal);
            FreeIfSet(context, server);
            _ = CcClose(context, cache);
        }
    }

    private void Acquire(IntPtr context)
    {
        IntPtr client = IntPtr.Zero, keytab = IntPtr.Zero, options = IntPtr.Zero, temporary = IntPtr.Zero;
        var creds = default(Krb5Creds);
        var haveCreds = false;
        var directory = Path.GetDirectoryName(_cachePath) ?? throw Failure("The credential cache path has no folder.", 0);
        var temporaryPath = Path.Combine(directory, "krb5cc.tmp." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        try
        {
            Check(ParseName(context, _options.Principal, out client), "parsing the service principal");
            Check(KtResolve(context, _options.ClientKeytab, out keytab), "opening the client keytab");
            Check(GetInitCredsOptAlloc(context, out options), "allocating the request options");
            GetInitCredsOptSetForwardable(options, 0);
            if (_options.TicketLifetime is { } lifetime)
            {
                GetInitCredsOptSetTicketLifetime(options, (int)Math.Max(60, lifetime.TotalSeconds));
            }

            Check(GetInitCredsKeytab(context, ref creds, client, keytab, 0, null, options), "obtaining a TGT with the client keytab");
            haveCreds = true;
            Check(CcResolve(context, "FILE:" + temporaryPath, out temporary), "creating the temporary credential cache");
            Check(CcInitialize(context, temporary, client), "initializing the temporary credential cache");
            Check(CcStoreCred(context, temporary, ref creds), "storing the TGT");
            _ = CcClose(context, temporary);
            temporary = IntPtr.Zero;
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new SysvolReadException("The credential cache for smbclient could not be replaced.", ex);
        }
        finally
        {
            if (temporary != IntPtr.Zero)
            {
                _ = CcClose(context, temporary);
            }

            TryDelete(temporaryPath);
            if (haveCreds)
            {
                FreeCredContents(context, ref creds);
            }

            if (options != IntPtr.Zero)
            {
                GetInitCredsOptFree(context, options);
            }

            if (keytab != IntPtr.Zero)
            {
                _ = KtClose(context, keytab);
            }

            FreeIfSet(context, client);
        }
    }

    private IntPtr OpenContext()
    {
        int code;
        IntPtr context;
        if (_options.Krb5Config is { Length: > 0 } path)
        {
            code = (int)ProfileInitPath(path, out var profile);
            if (code != 0)
            {
                throw Failure("The Kerberos configuration could not be read", code);
            }

            try
            {
                code = InitContextProfile(profile, 0, out context);
            }
            finally
            {
                ProfileRelease(profile);
            }
        }
        else
        {
            code = InitContext(out context);
        }

        return code == 0 ? context : throw Failure("The Kerberos context could not be created", code);
    }

    private static string Realm(string principal)
    {
        var at = principal.LastIndexOf('@');
        return at >= 0 && at < principal.Length - 1 ? principal[(at + 1)..] : throw new ArgumentException("The principal has no realm.", nameof(principal));
    }

    private static void Check(int code, string step)
    {
        if (code != 0)
        {
            throw Failure("Kerberos failed " + step, code);
        }
    }

    private static SysvolReadException Failure(string message, int code) =>
        new(SysvolErrorKind.Authentication, null, code == 0 ? message + "." : message + " (krb5 error " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");

    private static void FreeIfSet(IntPtr context, IntPtr principal)
    {
        if (principal != IntPtr.Zero)
        {
            FreePrincipal(context, principal);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- MIT libkrb5 (krb5.h, LP64) -------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Krb5Data
    {
        public int Magic;
        public uint Length;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Krb5Keyblock
    {
        public int Magic;
        public int Enctype;
        public uint Length;
        public IntPtr Contents;
    }

    /// <summary><c>krb5_creds</c> (120 bytes on LP64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Krb5Creds
    {
        public int Magic;
        public IntPtr Client;
        public IntPtr Server;
        public Krb5Keyblock Keyblock;
        public int AuthTime;
        public int StartTime;
        public int EndTime;
        public int RenewTill;
        public int IsSkey;
        public int TicketFlags;
        public IntPtr Addresses;
        public Krb5Data Ticket;
        public Krb5Data SecondTicket;
        public IntPtr AuthData;
    }

    [LibraryImport(Library, EntryPoint = "krb5_init_context")]
    private static partial int InitContext(out IntPtr context);

    [LibraryImport(Library, EntryPoint = "profile_init_path", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ProfileInitPath(string path, out IntPtr profile);

    [LibraryImport(Library, EntryPoint = "profile_release")]
    private static partial void ProfileRelease(IntPtr profile);

    [LibraryImport(Library, EntryPoint = "krb5_init_context_profile")]
    private static partial int InitContextProfile(IntPtr profile, int flags, out IntPtr context);

    [LibraryImport(Library, EntryPoint = "krb5_free_context")]
    private static partial void FreeContext(IntPtr context);

    [LibraryImport(Library, EntryPoint = "krb5_parse_name", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ParseName(IntPtr context, string name, out IntPtr principal);

    [LibraryImport(Library, EntryPoint = "krb5_free_principal")]
    private static partial void FreePrincipal(IntPtr context, IntPtr principal);

    [LibraryImport(Library, EntryPoint = "krb5_principal_compare")]
    private static partial int PrincipalCompare(IntPtr context, IntPtr first, IntPtr second);

    [LibraryImport(Library, EntryPoint = "krb5_kt_resolve", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int KtResolve(IntPtr context, string name, out IntPtr keytab);

    [LibraryImport(Library, EntryPoint = "krb5_kt_close")]
    private static partial int KtClose(IntPtr context, IntPtr keytab);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_alloc")]
    private static partial int GetInitCredsOptAlloc(IntPtr context, out IntPtr options);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_free")]
    private static partial void GetInitCredsOptFree(IntPtr context, IntPtr options);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_set_forwardable")]
    private static partial void GetInitCredsOptSetForwardable(IntPtr options, int forwardable);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_set_tkt_life")]
    private static partial void GetInitCredsOptSetTicketLifetime(IntPtr options, int lifetime);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_keytab", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetInitCredsKeytab(IntPtr context, ref Krb5Creds creds, IntPtr client, IntPtr keytab, int startTime, string? service, IntPtr options);

    [LibraryImport(Library, EntryPoint = "krb5_free_cred_contents")]
    private static partial void FreeCredContents(IntPtr context, ref Krb5Creds creds);

    [LibraryImport(Library, EntryPoint = "krb5_cc_resolve", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CcResolve(IntPtr context, string name, out IntPtr cache);

    [LibraryImport(Library, EntryPoint = "krb5_cc_initialize")]
    private static partial int CcInitialize(IntPtr context, IntPtr cache, IntPtr principal);

    [LibraryImport(Library, EntryPoint = "krb5_cc_store_cred")]
    private static partial int CcStoreCred(IntPtr context, IntPtr cache, ref Krb5Creds creds);

    [LibraryImport(Library, EntryPoint = "krb5_cc_get_principal")]
    private static partial int CcGetPrincipal(IntPtr context, IntPtr cache, out IntPtr principal);

    [LibraryImport(Library, EntryPoint = "krb5_cc_retrieve_cred")]
    private static partial int CcRetrieveCred(IntPtr context, IntPtr cache, int flags, ref Krb5Creds match, out Krb5Creds creds);

    [LibraryImport(Library, EntryPoint = "krb5_cc_close")]
    private static partial int CcClose(IntPtr context, IntPtr cache);
}
