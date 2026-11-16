using System.Management.Automation;

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

    protected override void EndProcessing()
    {
        var context = RequireContext();
        var table = Table.Build(null);
        if (table is null)
        {
            WriteWarning("There are no objects to export: nothing was written.");
            return;
        }

        if (Delimiter is { } separator && (char.IsControl(separator) || separator is '"' or '\r' or '\n'))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException("The delimiter must be one visible character, not a quote or a line break."),
                "AdmInvalidDelimiter",
                ErrorCategory.InvalidArgument,
                separator));
        }

        string? path = null;
        if (Path is not null)
        {
            path = Resolve(Path);
            if (path is null)
            {
                return;
            }

            if (File.Exists(path) && !Force)
            {
                WriteError(new ErrorRecord(
                    new IOException($"The file '{path}' exists. Nothing was written; use -Force to replace it."),
                    "AdmExportFileExists",
                    ErrorCategory.ResourceExists,
                    path));
                return;
            }
        }

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
            WriteWarning("No file was chosen: nothing was written.");
            return;
        }

        WriteObject(new AdmExportResult(written, table.Rows.Count, table.Columns.Count));
    }

    /// <summary>The full path of <paramref name="text"/> in the file system, or null (after an error) when it is not one.</summary>
    private string? Resolve(string text)
    {
        try
        {
            var full = SessionState.Path.GetUnresolvedProviderPathFromPSPath(text, out var provider, out _);
            if (!string.Equals(provider.Name, "FileSystem", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"'{text}' is not a path in the file system."), "AdmExportInvalidPath", ErrorCategory.InvalidArgument, text));
                return null;
            }

            return full;
        }
        catch (Exception error) when (error is ArgumentException or PSInvalidOperationException or System.Management.Automation.DriveNotFoundException or ProviderNotFoundException or NotSupportedException)
        {
            WriteError(new ErrorRecord(error, "AdmExportInvalidPath", ErrorCategory.InvalidArgument, text));
            return null;
        }
    }
}

/// <summary>What <c>Export-AdmList</c> wrote: the file and its size in rows and columns (the values themselves stay in the file).</summary>
public sealed record AdmExportResult(string Path, int Rows, int Columns);
