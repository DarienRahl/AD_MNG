namespace AdMng.Core.GroupPolicy.Content;

/// <summary>
/// <c>Machine|User/Scripts/scripts.ini</c> and <c>psscripts.ini</c> ([MS-GPSCR] 2.2.2, UTF-16LE): sections
/// <c>[Startup]</c>, <c>[Shutdown]</c> (computer), <c>[Logon]</c>, <c>[Logoff]</c> (user) with <c>&lt;n&gt;CmdLine</c> and
/// <c>&lt;n&gt;Parameters</c>; psscripts.ini also <c>[ScriptsConfig]</c> (<c>StartExecutePSFirst</c>, <c>EndExecutePSFirst</c>).
/// Keys: <c>Computer/Scripts/Startup/0</c> (command line), <c>Computer/Scripts/Startup/0/Parameters</c>; PowerShell scripts
/// under <c>…/Scripts/PowerShell/…</c>. Everything is the sensitive category Scripts.
/// </summary>
public static class ScriptsIniParser
{
    public static void Parse(ReadOnlySpan<byte> content, GpoScope scope, bool powerShell, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var text = GpoText.DecodeUnicodeText(content);
        var prefix = (scope == GpoScope.User ? "User" : "Computer") + "/Scripts/" + (powerShell ? "PowerShell/" : string.Empty);
        foreach (var line in IniReader.Read(text))
        {
            var section = line.Section;
            var value = line.Value ?? string.Empty;
            if (section.Equals("ScriptsConfig", StringComparison.OrdinalIgnoreCase))
            {
                sink.Text(prefix + "Config/" + line.Key, value, GpoSettingCategory.Scripts);
                continue;
            }

            var digits = 0;
            while (digits < line.Key.Length && char.IsAsciiDigit(line.Key[digits]))
            {
                digits++;
            }

            var number = digits > 0 ? line.Key[..digits].TrimStart('0') : string.Empty;
            number = digits > 0 && number.Length == 0 ? "0" : number;
            var field = line.Key[digits..];
            if (digits > 0 && field.Equals("CmdLine", StringComparison.OrdinalIgnoreCase))
            {
                sink.Text(prefix + section + "/" + number, value, GpoSettingCategory.Scripts);
            }
            else if (digits > 0 && field.Equals("Parameters", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0)
                {
                    sink.Text(prefix + section + "/" + number + "/Parameters", value, GpoSettingCategory.Scripts);
                }
            }
            else
            {
                sink.Text(prefix + section + "/" + line.Key, value, GpoSettingCategory.Scripts);
            }
        }
    }
}
