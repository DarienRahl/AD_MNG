using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Testing.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>
/// The hand-written GPO fixtures of <c>Gpo/Fixtures</c> (sources in UTF-8 with LF) and the encodings SYSVOL uses:
/// UTF-16LE with BOM and CRLF for the security template and the scripts files, UTF-8 with BOM for GPP XML, the binary
/// Registry.pol from its annotated hex form or built entry by entry ([MS-GPREG] 2.2.1).
/// </summary>
internal static class GpoFixtures
{
    public static readonly Guid Gpo = Guid.Parse("6AC1786C-016F-11D2-945F-00C04FB984F9");
    public const string Dc = "dc01.contoso.com";
    public const string Domain = "contoso.com";
    public const string GptTmplPath = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";
    public const string AuditPath = "Machine/Microsoft/Windows NT/Audit/audit.csv";

    public static SysvolGpoFolder Folder => SysvolGpoFolder.Create(Dc, Domain, Gpo);

    /// <summary>The fixture text (UTF-8 source).</summary>
    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gpo", "Fixtures", name), Encoding.UTF8);

    /// <summary>Text as Windows writes it: CRLF line ends.</summary>
    public static string Crlf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>UTF-16LE with BOM and CRLF (GptTmpl.inf, scripts.ini, psscripts.ini, GPO.cmt).</summary>
    public static byte[] Utf16(string text) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Crlf(text))];

    /// <summary>UTF-8 with BOM (GPP XML, comment.cmtx, audit.csv).</summary>
    public static byte[] Utf8Bom(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Crlf(text))];

    /// <summary>A fixture file encoded as in SYSVOL.</summary>
    public static byte[] Sysvol(string name) => Path.GetExtension(name).ToUpperInvariant() switch
    {
        ".INF" or ".INI" when !name.Equals("GPT.INI", StringComparison.OrdinalIgnoreCase) => Utf16(Text(name)),
        ".HEX" => Hex(Text(name)),
        ".INI" => Encoding.UTF8.GetBytes(Crlf(Text(name))), // GPT.INI: ANSI/UTF-8 without BOM
        _ => Utf8Bom(Text(name)),
    };

    /// <summary>Bytes of an annotated hex dump ('#' comments, white space ignored).</summary>
    public static byte[] Hex(string dump)
    {
        var hex = new StringBuilder();
        foreach (var line in dump.Split('\n'))
        {
            var comment = line.IndexOf('#', StringComparison.Ordinal);
            hex.Append((comment < 0 ? line : line[..comment]).Replace(" ", string.Empty, StringComparison.Ordinal).Trim());
        }

        return Convert.FromHexString(hex.ToString());
    }

    /// <summary>A whole sample GPO as GPMC lays it out (computer and user part, every parsed file kind).</summary>
    public static InMemorySysvolReader SampleGpo(InMemorySysvolReader? reader = null)
    {
        reader ??= new InMemorySysvolReader();
        return reader
            .SetFile(Gpo, "GPT.INI", Sysvol("GPT.INI"))
            .SetFile(Gpo, "GPO.cmt", Utf16("Zasada dla kontrolerów domeny – właściciel: zespół Tier 0."))
            .SetFile(Gpo, GptTmplPath, Sysvol("GptTmpl.inf"))
            .SetFile(Gpo, AuditPath, Sysvol("audit.csv"))
            .SetFile(Gpo, "Machine/Registry.pol", Sysvol("Registry.pol.hex"))
            .SetFile(Gpo, "User/Registry.pol", Pol(("Software\\Policies\\Microsoft\\Windows\\Control Panel\\Desktop", "ScreenSaveTimeOut", 1, Sz("900"))))
            .SetFile(Gpo, "Machine/Preferences/Groups/Groups.xml", Sysvol("Groups.xml"))
            .SetFile(Gpo, "Machine/Preferences/ScheduledTasks/ScheduledTasks.xml", Sysvol("ScheduledTasks.xml"))
            .SetFile(Gpo, "Machine/Preferences/Registry/Registry.xml", Sysvol("Registry.xml"))
            .SetFile(Gpo, "User/Preferences/Shortcuts/Shortcuts.xml", Sysvol("Shortcuts.xml"))
            .SetFile(Gpo, "Machine/Scripts/scripts.ini", Sysvol("scripts.ini"))
            .SetFile(Gpo, "Machine/Scripts/psscripts.ini", Sysvol("psscripts.ini"))
            .SetFile(Gpo, "Machine/Scripts/Startup/mapuj-dyski.cmd", Encoding.ASCII.GetBytes("@echo off\r\nnet use Z: \\\\fs01\\dane\r\n"))
            .SetFile(Gpo, "Machine/comment.cmtx", Sysvol("comment.cmtx"))
            .SetFile(Gpo, "Machine/Microsoft/Windows NT/SecEdit/edb.log", new byte[64])
            .SetFile(Gpo, "Adm/system.adm", new byte[128]);
    }

    /// <summary>Registry.pol bytes built from entries ([MS-GPREG] 2.2.1).</summary>
    public static byte[] Pol(params (string Key, string Value, uint Type, byte[] Data)[] entries)
    {
        var bytes = new List<byte>();
        bytes.AddRange("PReg"u8.ToArray());
        bytes.AddRange(UInt32(1));
        foreach (var (key, value, type, data) in entries)
        {
            bytes.AddRange(Encoding.Unicode.GetBytes("["));
            bytes.AddRange(Encoding.Unicode.GetBytes(key + "\0"));
            bytes.AddRange(Encoding.Unicode.GetBytes(";"));
            bytes.AddRange(Encoding.Unicode.GetBytes(value + "\0"));
            bytes.AddRange(Encoding.Unicode.GetBytes(";"));
            bytes.AddRange(UInt32(type));
            bytes.AddRange(Encoding.Unicode.GetBytes(";"));
            bytes.AddRange(UInt32((uint)data.Length));
            bytes.AddRange(Encoding.Unicode.GetBytes(";"));
            bytes.AddRange(data);
            bytes.AddRange(Encoding.Unicode.GetBytes("]"));
        }

        return [.. bytes];
    }

    /// <summary>REG_SZ data: UTF-16LE with the terminating NUL.</summary>
    public static byte[] Sz(string value) => Encoding.Unicode.GetBytes(value + "\0");

    /// <summary>REG_MULTI_SZ data.</summary>
    public static byte[] MultiSz(params string[] values) => Encoding.Unicode.GetBytes(string.Concat(values.Select(v => v + "\0")) + "\0");

    public static byte[] Dword(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return data;
    }

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static byte[] UInt32(uint value) => Dword(value);
}
