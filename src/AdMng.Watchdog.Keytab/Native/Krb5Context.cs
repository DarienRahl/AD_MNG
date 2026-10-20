using System.Runtime.InteropServices;
using System.Text;

namespace AdMng.Watchdog.Keytab.Native;

/// <summary>A krb5 call failed; <see cref="Name"/> is the symbolic error name (never the library's message).</summary>
internal sealed class Krb5Exception(int code, string operation)
    : Exception($"{operation} failed ({Krb5ErrorNames.Of(code)}).")
{
    public int Code { get; } = code;

    public string Name => Krb5ErrorNames.Of(Code);
}

/// <summary>One entry of a keytab as listed (no key material).</summary>
internal sealed record KeytabEntryInfo(string Principal, uint Kvno, int Enctype, DateTimeOffset Timestamp)
{
    public string EnctypeName => Krb5Context.EnctypeName(Enctype);
}

/// <summary>A parsed principal; freed with its context.</summary>
internal sealed class Krb5Principal : IDisposable
{
    private readonly Krb5Context _context;

    internal Krb5Principal(Krb5Context context, IntPtr handle, string name)
    {
        _context = context;
        Handle = handle;
        Name = name;
    }

    public IntPtr Handle { get; private set; }

    public string Name { get; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Krb5Native.FreePrincipal(_context.Handle, Handle);
            Handle = IntPtr.Zero;
        }
    }
}

/// <summary>Two derived keys (aes256, aes128) of one password and salt in libkrb5 memory, zeroed on dispose.</summary>
internal sealed class DerivedKeys : IDisposable
{
    private readonly Krb5Context _context;
    private Krb5Keyblock[] _keys;

    internal DerivedKeys(Krb5Context context, Krb5Keyblock[] keys)
    {
        _context = context;
        _keys = keys;
    }

    public int Count => _keys.Length;

    public ref Krb5Keyblock this[int index] => ref _keys[index];

    public void Dispose()
    {
        for (var i = 0; i < _keys.Length; i++)
        {
            if (_keys[i].Contents != IntPtr.Zero)
            {
                Krb5Native.FreeKeyblockContents(_context.Handle, ref _keys[i]);
                _keys[i] = default;
            }
        }

        _keys = [];
    }
}

/// <summary>
/// A krb5 context built from an explicit profile (never <c>KRB5_CONFIG</c> of the process, which .NET cannot change for
/// native code): every operation the keytab tool needs – initial credentials from a password or a keytab, password
/// change, kvno of a fresh service ticket, string-to-key with an explicit salt, keytab listing and writing. Not thread-safe.
/// </summary>
internal sealed unsafe class Krb5Context : IDisposable
{
    /// <summary>Enctypes the tool writes, strongest first (design 4.3).</summary>
    public static readonly int[] Enctypes = [Krb5Native.EnctypeAes256, Krb5Native.EnctypeAes128];

    private Krb5Context(IntPtr handle) => Handle = handle;

    public IntPtr Handle { get; private set; }

    /// <summary>A context from a colon-separated profile path list (first file wins).</summary>
    /// <exception cref="Krb5Exception">The profile or the context could not be created.</exception>
    public static Krb5Context Open(string profilePath)
    {
        var code = (int)Krb5Native.ProfileInitPath(profilePath, out var profile);
        if (code != 0)
        {
            throw new Krb5Exception(code, "Reading the Kerberos configuration");
        }

        try
        {
            code = Krb5Native.InitContextProfile(profile, 0, out var context);
            return code == 0 ? new Krb5Context(context) : throw new Krb5Exception(code, "Creating the Kerberos context");
        }
        finally
        {
            Krb5Native.ProfileRelease(profile);
        }
    }

    /// <summary>The values of one relation of a profile file (e.g. <c>realms</c>, <c>CONTOSO.COM</c>, <c>kdc</c>).</summary>
    public static IReadOnlyList<string> ProfileValues(string profilePath, params string[] names)
    {
        var code = (int)Krb5Native.ProfileInitPath(profilePath, out var profile);
        if (code != 0)
        {
            throw new Krb5Exception(code, "Reading the Kerberos configuration");
        }

        var native = new IntPtr[names.Length + 1];
        try
        {
            for (var i = 0; i < names.Length; i++)
            {
                native[i] = Marshal.StringToCoTaskMemUTF8(names[i]);
            }

            fixed (IntPtr* list = native)
            {
                if (Krb5Native.ProfileGetValues(profile, list, out var values) != 0 || values == IntPtr.Zero)
                {
                    return [];
                }

                try
                {
                    var result = new List<string>();
                    for (var p = (IntPtr*)values; *p != IntPtr.Zero; p++)
                    {
                        result.Add(Marshal.PtrToStringUTF8(*p) ?? string.Empty);
                    }

                    return result;
                }
                finally
                {
                    Krb5Native.ProfileFreeList(values);
                }
            }
        }
        finally
        {
            foreach (var p in native)
            {
                Marshal.FreeCoTaskMem(p);
            }

            Krb5Native.ProfileRelease(profile);
        }
    }

    public static string EnctypeName(int enctype) => enctype switch
    {
        Krb5Native.EnctypeAes256 => "aes256-cts-hmac-sha1-96",
        Krb5Native.EnctypeAes128 => "aes128-cts-hmac-sha1-96",
        19 => "aes128-cts-hmac-sha256-128",
        20 => "aes256-cts-hmac-sha384-192",
        23 => "arcfour-hmac",
        _ => "enctype-" + enctype.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public Krb5Principal ParseName(string name)
    {
        var code = Krb5Native.ParseName(Handle, name, out var principal);
        if (code != 0)
        {
            throw new Krb5Exception(code, "Parsing a principal name");
        }

        return new Krb5Principal(this, principal, UnparseName(principal));
    }

    /// <summary>Initial credentials from a password (<paramref name="service"/> null = a TGT), stored in
    /// <paramref name="ccache"/> when given. Returns the krb5 error code (0 = success).</summary>
    public int GetInitialCredentials(Krb5Principal client, SecretBuffer password, string? service, IntPtr ccache, out Krb5Creds creds)
    {
        creds = default;
        var options = AllocOptions(ccache);
        try
        {
            return Krb5Native.GetInitCredsPassword(Handle, ref creds, client.Handle, password.Pointer, IntPtr.Zero, IntPtr.Zero, 0, service, options);
        }
        finally
        {
            Krb5Native.GetInitCredsOptFree(Handle, options);
        }
    }

    /// <summary>Initial credentials from a keytab file (<c>kinit -k</c> semantics). Returns the krb5 error code.</summary>
    public int GetInitialCredentials(Krb5Principal client, string keytabPath, string? service, IntPtr ccache, out Krb5Creds creds)
    {
        creds = default;
        var keytab = ResolveKeytab(keytabPath);
        var options = AllocOptions(ccache);
        try
        {
            return Krb5Native.GetInitCredsKeytab(Handle, ref creds, client.Handle, keytab, 0, service, options);
        }
        finally
        {
            Krb5Native.GetInitCredsOptFree(Handle, options);
            _ = Krb5Native.KtClose(Handle, keytab);
        }
    }

    public void FreeCredentials(ref Krb5Creds creds)
    {
        Krb5Native.FreeCredContents(Handle, ref creds);
        creds = default;
    }

    /// <summary>Changes the password with <c>kadmin/changepw</c> credentials. Returns the krb5 error code; the kpasswd
    /// result code (0 = accepted) is in <paramref name="resultCode"/>. The result strings are discarded unread.</summary>
    public int ChangePassword(ref Krb5Creds creds, SecretBuffer newPassword, out int resultCode)
    {
        var code = Krb5Native.ChangePassword(Handle, ref creds, newPassword.Pointer, out resultCode, out var codeString, out var resultString);
        if (codeString.Data != IntPtr.Zero)
        {
            Krb5Native.FreeDataContents(Handle, ref codeString);
        }

        if (resultString.Data != IntPtr.Zero)
        {
            Krb5Native.FreeDataContents(Handle, ref resultString);
        }

        return code;
    }

    /// <summary>The kvno of a fresh service ticket for <paramref name="service"/>, with a TGT from a password or a keytab
    /// (exactly one of them) in a private memory ccache. Returns the krb5 error code.</summary>
    public int GetServiceKvno(Krb5Principal client, SecretBuffer? password, string? keytabPath, string service, out uint kvno)
    {
        kvno = 0;
        var code = Krb5Native.CcNewUnique(Handle, "MEMORY", null, out var ccache);
        if (code != 0)
        {
            return code;
        }

        try
        {
            Krb5Creds tgt;
            code = password is not null
                ? GetInitialCredentials(client, password, null, ccache, out tgt)
                : GetInitialCredentials(client, keytabPath!, null, ccache, out tgt);
            if (code != 0)
            {
                return code;
            }

            FreeCredentials(ref tgt);
            using var server = ParseName(service);
            var request = default(Krb5Creds);
            request.Client = client.Handle;
            request.Server = server.Handle;
            code = Krb5Native.GetCredentials(Handle, 0, ccache, ref request, out var reply);
            if (code != 0)
            {
                return code;
            }

            try
            {
                var ticketData = &((Krb5Creds*)reply)->Ticket;
                code = Krb5Native.DecodeTicket(ticketData, out var ticket);
                if (code != 0)
                {
                    return code;
                }

                // krb5_ticket: magic, server, enc_part { magic, enctype, kvno, ciphertext } → kvno at offset 24 (LP64).
                kvno = *(uint*)((byte*)ticket + 24);
                Krb5Native.FreeTicket(Handle, ticket);
                return 0;
            }
            finally
            {
                Krb5Native.FreeCreds(Handle, reply);
            }
        }
        finally
        {
            _ = Krb5Native.CcDestroy(Handle, ccache);
        }
    }

    /// <summary>The aes256 and aes128 keys of a password with an explicit salt (design 4.3).</summary>
    public DerivedKeys DeriveKeys(SecretBuffer password, string salt)
    {
        var keys = new Krb5Keyblock[Enctypes.Length];
        var derived = new DerivedKeys(this, keys);
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        try
        {
            fixed (byte* saltPointer = saltBytes)
            {
                var passwordData = new Krb5Data { Length = (uint)password.Length, Data = (IntPtr)password.Pointer };
                var saltData = new Krb5Data { Length = (uint)saltBytes.Length, Data = (IntPtr)saltPointer };
                for (var i = 0; i < Enctypes.Length; i++)
                {
                    var code = Krb5Native.StringToKey(Handle, Enctypes[i], ref passwordData, ref saltData, out derived[i]);
                    if (code != 0)
                    {
                        throw new Krb5Exception(code, "Deriving a key");
                    }
                }
            }

            return derived;
        }
        catch
        {
            derived.Dispose();
            throw;
        }
    }

    /// <summary>The entries of a keytab file in file order (no key material); empty when the file does not exist.</summary>
    public List<KeytabEntryInfo> ListKeytab(string path)
    {
        var result = new List<KeytabEntryInfo>();
        if (!File.Exists(path))
        {
            return result;
        }

        ForEachEntry(path, (ref Krb5KeytabEntry entry) =>
        {
            result.Add(new KeytabEntryInfo(UnparseName(entry.Principal), entry.Vno, entry.Key.Enctype, DateTimeOffset.FromUnixTimeSeconds((uint)entry.Timestamp)));
        });
        return result;
    }

    /// <summary>Appends the entries of <paramref name="sourcePath"/> that match <paramref name="filter"/> to an open keytab.</summary>
    public int CopyEntries(string sourcePath, IntPtr target, Func<KeytabEntryInfo, bool> filter)
    {
        var copied = 0;
        if (!File.Exists(sourcePath))
        {
            return 0;
        }

        ForEachEntry(sourcePath, (ref Krb5KeytabEntry entry) =>
        {
            var info = new KeytabEntryInfo(UnparseName(entry.Principal), entry.Vno, entry.Key.Enctype, DateTimeOffset.FromUnixTimeSeconds((uint)entry.Timestamp));
            if (filter(info))
            {
                var code = Krb5Native.KtAddEntry(Handle, target, ref entry);
                if (code != 0)
                {
                    throw new Krb5Exception(code, "Copying a keytab entry");
                }

                copied++;
            }
        });
        return copied;
    }

    /// <summary>Adds one entry with a derived key to an open keytab.</summary>
    public void AddEntry(IntPtr keytab, Krb5Principal principal, uint kvno, ref Krb5Keyblock key, DateTimeOffset timestamp)
    {
        var entry = new Krb5KeytabEntry
        {
            Principal = principal.Handle,
            Timestamp = (int)timestamp.ToUnixTimeSeconds(),
            Vno = kvno,
            Key = key,
        };
        var code = Krb5Native.KtAddEntry(Handle, keytab, ref entry);
        if (code != 0)
        {
            throw new Krb5Exception(code, "Writing a keytab entry");
        }
    }

    public IntPtr ResolveKeytab(string path)
    {
        var code = Krb5Native.KtResolve(Handle, "FILE:" + path, out var keytab);
        return code == 0 ? keytab : throw new Krb5Exception(code, "Opening the keytab");
    }

    public void CloseKeytab(IntPtr keytab) => _ = Krb5Native.KtClose(Handle, keytab);

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Krb5Native.FreeContext(Handle);
            Handle = IntPtr.Zero;
        }
    }

    private delegate void EntryAction(ref Krb5KeytabEntry entry);

    private void ForEachEntry(string path, EntryAction action)
    {
        var keytab = ResolveKeytab(path);
        try
        {
            var code = Krb5Native.KtStartSeqGet(Handle, keytab, out var cursor);
            if (code != 0)
            {
                throw new Krb5Exception(code, "Reading the keytab");
            }

            try
            {
                while (true)
                {
                    code = Krb5Native.KtNextEntry(Handle, keytab, out var entry, ref cursor);
                    if (code == Krb5ErrorNames.KeytabEnd)
                    {
                        break;
                    }

                    if (code != 0)
                    {
                        throw new Krb5Exception(code, "Reading the keytab");
                    }

                    try
                    {
                        action(ref entry);
                    }
                    finally
                    {
                        _ = Krb5Native.FreeKeytabEntryContents(Handle, ref entry);
                    }
                }
            }
            finally
            {
                _ = Krb5Native.KtEndSeqGet(Handle, keytab, ref cursor);
            }
        }
        finally
        {
            _ = Krb5Native.KtClose(Handle, keytab);
        }
    }

    private IntPtr AllocOptions(IntPtr ccache)
    {
        var code = Krb5Native.GetInitCredsOptAlloc(Handle, out var options);
        if (code != 0)
        {
            throw new Krb5Exception(code, "Allocating credential options");
        }

        Krb5Native.GetInitCredsOptSetForwardable(options, 0);
        Krb5Native.GetInitCredsOptSetProxiable(options, 0);
        Krb5Native.GetInitCredsOptSetTicketLifetime(options, 600);
        Krb5Native.GetInitCredsOptSetChangePasswordPrompt(options, 0);
        if (ccache != IntPtr.Zero)
        {
            code = Krb5Native.GetInitCredsOptSetOutCcache(Handle, options, ccache);
            if (code != 0)
            {
                Krb5Native.GetInitCredsOptFree(Handle, options);
                throw new Krb5Exception(code, "Setting the credential cache");
            }
        }

        return options;
    }

    private string UnparseName(IntPtr principal)
    {
        var code = Krb5Native.UnparseName(Handle, principal, out var name);
        if (code != 0)
        {
            throw new Krb5Exception(code, "Formatting a principal name");
        }

        try
        {
            return Marshal.PtrToStringUTF8(name) ?? string.Empty;
        }
        finally
        {
            Krb5Native.FreeUnparsedName(Handle, name);
        }
    }
}
