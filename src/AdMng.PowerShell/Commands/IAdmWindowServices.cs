using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// What the window's three commands call back into (docs/dev/r1-powershell-window.md 4.10 and 12.4): <c>Out-AdmGrid</c> shows its
/// objects in a grid of the program, <c>Open-AdmObject</c> opens an object's properties, <c>Export-AdmList</c> writes a CSV file.
/// The engine knows no WPF and no dialog: the program implements this over its own window services, and a test implements it with
/// a recorder. A command calls it on the pipeline thread and waits for the task there (never the UI thread), with the stop token
/// of the pipeline.
/// </summary>
/// <remarks>
/// Nothing here writes to the directory (CLAUDE.md rule 5, decision D9). The only thing that writes at all is
/// <see cref="ExportListAsync"/>, and only to a file the operator named or chose (rule 1).
/// </remarks>
public interface IAdmWindowServices
{
    /// <summary>Shows the table in a grid of the program (sorting, filter, copy and CSV export work there).</summary>
    Task ShowGridAsync(PowerShellGridTable table, CancellationToken cancellationToken);

    /// <summary>Opens the properties of the object with this distinguished name (the command has read it, so it exists); false when the program could not open it.</summary>
    Task<bool> OpenObjectAsync(string distinguishedName, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the table as CSV with formula protection. <paramref name="path"/> null asks the operator in <i>Save As</i>; returns the
    /// path written, or null when the operator cancelled (nothing was written).
    /// </summary>
    Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken);
}

/// <summary>The services of a window whose host provides none (a session without a window, a host that did not wire them): every call says so.</summary>
public sealed class UnavailableWindowServices : IAdmWindowServices
{
    public static UnavailableWindowServices Instance { get; } = new();

    public Task ShowGridAsync(PowerShellGridTable table, CancellationToken cancellationToken) => Task.FromException(Unavailable());

    public Task<bool> OpenObjectAsync(string distinguishedName, CancellationToken cancellationToken) => Task.FromException<bool>(Unavailable());

    public Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken) =>
        Task.FromException<string?>(Unavailable());

    private static NotSupportedException Unavailable() => new(Reading.AdmText.Get(System.Globalization.CultureInfo.CurrentUICulture, Reading.AdmText.Keys.ServicesUnavailable));
}
