using System.Management.Automation;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Export-AdmList</c>: writes the objects of the pipeline as CSV, the way the program exports a list - formulas are protected
/// (a value that starts with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> cannot run in Excel), the separator is the one of the regional
/// settings, the file is UTF-8 with a signature. It writes only to the file named in <c>-Path</c> or chosen in <i>Save As</i>, and
/// never to a default place; an existing file is replaced only with <c>-Force</c>.
/// </summary>
[Cmdlet(VerbsData.Export, "AdmList")]
[OutputType(typeof(AdmExportResult))]
public sealed class ExportAdmListCommand : AdmTableCmdlet
{
    /// <summary>The file to write (taken literally: brackets are not wildcards). Without it the program asks in <i>Save As</i>.</summary>
    [Parameter(Position = 0)]
    [Alias("LiteralPath", "PSPath")]
    [ValidateNotNullOrEmpty]
    public string? Path { get; set; }

    /// <summary>The field separator (one character); the list separator of the regional settings by default.</summary>
    [Parameter]
    public char? Delimiter { get; set; }

    /// <summary>Replaces the file when it exists.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    // The file, resolved before anything of the pipeline is read.
    private string? _path;

    /// <summary>
    /// The delimiter, the path and -Force are checked here, before the commands in front of this one read anything (audit
    /// ps-commands-10: the whole pipeline was read and kept, then refused); a refusal ends the pipeline.
    /// </summary>
    protected override void BeginProcessing()
    {
        base.BeginProcessing();
        if (Delimiter is { } separator && (char.IsControl(separator) || separator is '"' or '\r' or '\n'))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(Text(AdmText.Keys.ExportInvalidDelimiter)),
                "AdmInvalidDelimiter",
                ErrorCategory.InvalidArgument,
                separator));
        }

        if (Path is not null)
        {
            _path = Resolve(Path);
            RefuseAnExistingFile(_path);
        }
    }

    protected override void EndProcessing()
    {
        var context = RequireContext();
        var table = Table.Build(null);
        if (table is null)
        {
            WriteWarning(Text(AdmText.Keys.ExportNoObjects));
            return;
        }

        // The file may have been made while the pipeline was read.
        var path = _path;
        RefuseAnExistingFile(path);

        string? written;
        try
        {
            written = Wait(context.Services.ExportListAsync(table, path, Delimiter, StopToken));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            WriteError(new ErrorRecord(error, "AdmExportFailed", ErrorCategory.WriteError, path));
            return;
        }

        if (written is null)
        {
            WriteWarning(Text(AdmText.Keys.ExportNoFile));
            return;
        }

        WriteObject(new AdmExportResult(written, table.Rows.Count, table.Columns.Count));
    }

    /// <summary>Ends the command with an error when <paramref name="path"/> exists and <c>-Force</c> was not given.</summary>
    private void RefuseAnExistingFile(string? path)
    {
        if (path is not null && File.Exists(path) && !Force)
        {
            ThrowTerminatingError(new ErrorRecord(
                new IOException(Text(AdmText.Keys.ExportFileExists, path)),
                "AdmExportFileExists",
                ErrorCategory.ResourceExists,
                path));
        }
    }

    /// <summary>The full path of <paramref name="text"/> in the file system; ends the command with an error when it is not one.</summary>
    private string Resolve(string text)
    {
        string full;
        ProviderInfo provider;
        try
        {
            full = SessionState.Path.GetUnresolvedProviderPathFromPSPath(text, out provider, out _);
        }
        catch (Exception error) when (error is ArgumentException or PSInvalidOperationException or System.Management.Automation.DriveNotFoundException or ProviderNotFoundException or NotSupportedException)
        {
            ThrowTerminatingError(new ErrorRecord(error, "AdmExportInvalidPath", ErrorCategory.InvalidArgument, text));
            throw;
        }

        if (!string.Equals(provider.Name, "FileSystem", StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(Text(AdmText.Keys.ExportNotFileSystem, text)), "AdmExportInvalidPath", ErrorCategory.InvalidArgument, text));
        }

        return full;
    }
}

/// <summary>What <c>Export-AdmList</c> wrote: the file and its size in rows and columns (the values themselves stay in the file).</summary>
public sealed record AdmExportResult(string Path, int Rows, int Columns);
