using System.Globalization;
using System.Text;

namespace AdMng.Presentation.Services;

/// <summary>
/// Regional settings of the operating system (Windows: Region → Formats, and the system locale for non-Unicode
/// programs) - independent of the UI language chosen in the app. Files exchanged with Excel follow them: the list
/// separator of CSV exports and the ANSI code page of "CSV (comma delimited)" files. A Polish administrator with an
/// English UI still gets ';' and Windows-1250.
/// </summary>
public interface IRegionalSettings
{
    /// <summary>Regional format culture (its list separator is what Excel expects: ';' for Polish, ',' for English).</summary>
    CultureInfo Culture { get; }

    /// <summary>ANSI code page of legacy (non-Unicode) text files, e.g. 1250 on Polish Windows.</summary>
    int AnsiCodePage { get; }
}

/// <summary>The settings of the machine the app runs on (the app never changes <see cref="CultureInfo.CurrentCulture"/>).</summary>
public sealed class SystemRegionalSettings : IRegionalSettings
{
    public SystemRegionalSettings()
        : this(CultureInfo.CurrentCulture)
    {
    }

    /// <param name="culture">The regional format culture.</param>
    /// <param name="ansiCodePage">The ANSI code page; null = the system code page (Windows), else the culture's.</param>
    public SystemRegionalSettings(CultureInfo culture, int? ansiCodePage = null)
    {
        Culture = culture ?? throw new ArgumentNullException(nameof(culture));
        AnsiCodePage = ansiCodePage ?? SystemAnsiCodePage() ?? culture.TextInfo.ANSICodePage;
    }

    public CultureInfo Culture { get; }

    public int AnsiCodePage { get; }

    /// <summary>The system ANSI code page (GetACP; what Excel writes ANSI CSV files in), null where there is none.</summary>
    private static int? SystemAnsiCodePage()
    {
        // Code page 0 = the system default; the provider resolves it on Windows and returns null elsewhere.
        var codePage = CodePagesEncodingProvider.Instance.GetEncoding(0)?.CodePage;
        return codePage is > 0 and not 65001 ? codePage : null;
    }
}
