namespace AdMng.Core.GroupPolicy.Content;

/// <summary>
/// Parses one classified content file of a GPO folder into normalized settings (stage B design 8.4/8.5 of the Watchdog,
/// shared by the Watchdog's snapshot builder and the GPO views of AD Manager). Pure and in memory: the content is the
/// caller's buffer and nothing touches the disk. A file that cannot be parsed throws <see cref="GpoParseException"/>.
/// </summary>
public static class GpoContentParser
{
    /// <summary>Key of a script file: <c>Computer/Scripts/Files/&lt;path below Scripts/&gt;</c>.</summary>
    public static string ScriptKey(GpoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var parts = file.Path.Split('/');
        return GpoValues.Key((file.Scope == GpoScope.User ? "User" : "Computer") + "/Scripts/Files/" + string.Join('/', parts.Skip(2)));
    }

    /// <summary>Adds the settings of <paramref name="content"/> to <paramref name="sink"/> (GPT.INI and inventory files add none).</summary>
    public static void Parse(GpoFile file, byte[] content, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sink);
        switch (file.Role)
        {
            case GpoFileRole.SecurityTemplate:
                GptTmplParser.Parse(content, sink);
                break;
            case GpoFileRole.AuditCsv:
                AuditCsvParser.Parse(content, sink);
                break;
            case GpoFileRole.RegistryPol:
                RegistryPolParser.Parse(content, file.Scope, sink);
                break;
            case GpoFileRole.Preferences:
                GppXmlParser.Parse(content, file.Scope, file.PreferenceType ?? "Preferences", sink);
                break;
            case GpoFileRole.ScriptsIni:
                ScriptsIniParser.Parse(content, file.Scope, powerShell: false, sink);
                break;
            case GpoFileRole.PsScriptsIni:
                ScriptsIniParser.Parse(content, file.Scope, powerShell: true, sink);
                break;
            case GpoFileRole.ScriptFile:
                sink.Hash(ScriptKey(file), GpoValues.Hash(content) + " size:" + GpoValues.Number(content.LongLength), GpoSettingCategory.Scripts);
                break;
            case GpoFileRole.Comment:
                PolicyCommentParser.ParseCmtx(content, file.Scope, sink);
                break;
            case GpoFileRole.GpoComment:
                PolicyCommentParser.ParseGpoComment(content, sink);
                break;
        }
    }
}
