using System.Runtime.InteropServices;

namespace AdMng.Watchdog.Keytab.Native;

/// <summary>MIT <c>krb5_data</c> (LP64: 16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Krb5Data
{
    public int Magic;
    public uint Length;
    public IntPtr Data;
}

/// <summary>MIT <c>krb5_keyblock</c> (24 bytes). The contents are allocated and zeroed on free by libkrb5.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Krb5Keyblock
{
    public int Magic;
    public int Enctype;
    public uint Length;
    public IntPtr Contents;
}

/// <summary>MIT <c>krb5_keytab_entry</c> (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Krb5KeytabEntry
{
    public int Magic;
    public IntPtr Principal;
    public int Timestamp;
    public uint Vno;
    public Krb5Keyblock Key;
}

/// <summary>MIT <c>krb5_creds</c> (120 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Krb5Creds
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

/// <summary>
/// The MIT Kerberos functions the keytab tool uses (<c>libkrb5.so.3</c>, Debian <c>libkrb5-3</c>; string-to-key lives in
/// <c>libk5crypto.so.3</c>). Signatures of krb5.h / profile.h, LP64 (x86_64, aarch64). Secrets are passed as pointers to
/// pinned buffers, never as managed strings.
/// </summary>
internal static unsafe partial class Krb5Native
{
    public const string Library = "libkrb5.so.3";
    public const string CryptoLibrary = "libk5crypto.so.3";

    public const int EnctypeAes128 = 17;
    public const int EnctypeAes256 = 18;

    [LibraryImport(Library, EntryPoint = "profile_init_path", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ProfileInitPath(string path, out IntPtr profile);

    [LibraryImport(Library, EntryPoint = "profile_release")]
    public static partial void ProfileRelease(IntPtr profile);

    [LibraryImport(Library, EntryPoint = "profile_get_values")]
    public static partial nint ProfileGetValues(IntPtr profile, IntPtr* names, out IntPtr values);

    [LibraryImport(Library, EntryPoint = "profile_free_list")]
    public static partial void ProfileFreeList(IntPtr values);

    [LibraryImport(Library, EntryPoint = "krb5_init_context_profile")]
    public static partial int InitContextProfile(IntPtr profile, int flags, out IntPtr context);

    [LibraryImport(Library, EntryPoint = "krb5_free_context")]
    public static partial void FreeContext(IntPtr context);

    [LibraryImport(Library, EntryPoint = "krb5_parse_name", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ParseName(IntPtr context, string name, out IntPtr principal);

    [LibraryImport(Library, EntryPoint = "krb5_unparse_name")]
    public static partial int UnparseName(IntPtr context, IntPtr principal, out IntPtr name);

    [LibraryImport(Library, EntryPoint = "krb5_free_unparsed_name")]
    public static partial void FreeUnparsedName(IntPtr context, IntPtr name);

    [LibraryImport(Library, EntryPoint = "krb5_free_principal")]
    public static partial void FreePrincipal(IntPtr context, IntPtr principal);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_alloc")]
    public static partial int GetInitCredsOptAlloc(IntPtr context, out IntPtr options);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_free")]
    public static partial void GetInitCredsOptFree(IntPtr context, IntPtr options);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_set_forwardable")]
    public static partial void GetInitCredsOptSetForwardable(IntPtr options, int forwardable);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_set_proxiable")]
    public static partial void GetInitCredsOptSetProxiable(IntPtr options, int proxiable);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_set_tkt_life")]
    public static partial void GetInitCredsOptSetTicketLifetime(IntPtr options, int lifetime);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_set_change_password_prompt")]
    public static partial void GetInitCredsOptSetChangePasswordPrompt(IntPtr options, int prompt);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_opt_set_out_ccache")]
    public static partial int GetInitCredsOptSetOutCcache(IntPtr context, IntPtr options, IntPtr ccache);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_password", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetInitCredsPassword(
        IntPtr context, ref Krb5Creds creds, IntPtr client, byte* password, IntPtr prompter, IntPtr data, int startTime, string? service, IntPtr options);

    [LibraryImport(Library, EntryPoint = "krb5_get_init_creds_keytab", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetInitCredsKeytab(IntPtr context, ref Krb5Creds creds, IntPtr client, IntPtr keytab, int startTime, string? service, IntPtr options);

    [LibraryImport(Library, EntryPoint = "krb5_free_cred_contents")]
    public static partial void FreeCredContents(IntPtr context, ref Krb5Creds creds);

    [LibraryImport(Library, EntryPoint = "krb5_free_creds")]
    public static partial void FreeCreds(IntPtr context, IntPtr creds);

    [LibraryImport(Library, EntryPoint = "krb5_change_password")]
    public static partial int ChangePassword(
        IntPtr context, ref Krb5Creds creds, byte* newPassword, out int resultCode, out Krb5Data resultCodeString, out Krb5Data resultString);

    [LibraryImport(Library, EntryPoint = "krb5_free_data_contents")]
    public static partial void FreeDataContents(IntPtr context, ref Krb5Data data);

    [LibraryImport(Library, EntryPoint = "krb5_cc_new_unique", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int CcNewUnique(IntPtr context, string type, string? hint, out IntPtr ccache);

    [LibraryImport(Library, EntryPoint = "krb5_cc_destroy")]
    public static partial int CcDestroy(IntPtr context, IntPtr ccache);

    [LibraryImport(Library, EntryPoint = "krb5_get_credentials")]
    public static partial int GetCredentials(IntPtr context, int options, IntPtr ccache, ref Krb5Creds inCreds, out IntPtr outCreds);

    [LibraryImport(Library, EntryPoint = "krb5_decode_ticket")]
    public static partial int DecodeTicket(Krb5Data* code, out IntPtr ticket);

    [LibraryImport(Library, EntryPoint = "krb5_free_ticket")]
    public static partial void FreeTicket(IntPtr context, IntPtr ticket);

    [LibraryImport(CryptoLibrary, EntryPoint = "krb5_c_string_to_key")]
    public static partial int StringToKey(IntPtr context, int enctype, ref Krb5Data password, ref Krb5Data salt, out Krb5Keyblock key);

    [LibraryImport(Library, EntryPoint = "krb5_free_keyblock_contents")]
    public static partial void FreeKeyblockContents(IntPtr context, ref Krb5Keyblock key);

    [LibraryImport(Library, EntryPoint = "krb5_kt_resolve", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int KtResolve(IntPtr context, string name, out IntPtr keytab);

    [LibraryImport(Library, EntryPoint = "krb5_kt_close")]
    public static partial int KtClose(IntPtr context, IntPtr keytab);

    [LibraryImport(Library, EntryPoint = "krb5_kt_add_entry")]
    public static partial int KtAddEntry(IntPtr context, IntPtr keytab, ref Krb5KeytabEntry entry);

    [LibraryImport(Library, EntryPoint = "krb5_kt_start_seq_get")]
    public static partial int KtStartSeqGet(IntPtr context, IntPtr keytab, out IntPtr cursor);

    [LibraryImport(Library, EntryPoint = "krb5_kt_next_entry")]
    public static partial int KtNextEntry(IntPtr context, IntPtr keytab, out Krb5KeytabEntry entry, ref IntPtr cursor);

    [LibraryImport(Library, EntryPoint = "krb5_kt_end_seq_get")]
    public static partial int KtEndSeqGet(IntPtr context, IntPtr keytab, ref IntPtr cursor);

    [LibraryImport(Library, EntryPoint = "krb5_free_keytab_entry_contents")]
    public static partial int FreeKeytabEntryContents(IntPtr context, ref Krb5KeytabEntry entry);
}
