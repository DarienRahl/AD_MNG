using AdMng.Core.PowerShell;

namespace AdMng.Presentation.PowerShell;

/// <summary>What <see cref="IPowerShellWindowActions.OpenObjectAsync"/> found.</summary>
public enum PowerShellOpenOutcome
{
    /// <summary>The object's properties were opened in the program.</summary>
    Opened = 0,

    /// <summary>No object has that distinguished name or <c>objectGUID</c> (or this account cannot see it).</summary>
    NotFound = 1,
}

/// <summary>
/// What the three window commands of AD Manager call back into (track R, decision D6 and D4): <c>Out-AdmGrid</c> shows its
/// objects in the program's grid, <c>Open-AdmObject</c> opens an object in the program, <c>Export-AdmList</c> writes a CSV
/// file. The contract is in Presentation; the engine's commands reach it through a thin adapter in AdMng.App (unit 4), which
/// is also where the host that shows the grid and the dialogs implements it.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> A command calls these on the pipeline thread of the runspace and waits for the task there (never the UI
/// thread); an implementation marshals onto the UI thread where it needs it. The token is the pipeline's: Stop cancels it.</para>
/// <para><b>Nothing on disk unless the operator says so</b> (CLAUDE.md rule 1): <see cref="ExportListAsync"/> writes the path it
/// is given or the one the operator chooses in <i>Save As</i> - never a default path, never a temporary file. A table is in
/// memory for as long as the grid shows it.</para>
/// <para><b>Reads and writes.</b> None of the three writes to the directory (rule 5); <see cref="OpenObjectAsync"/> may read one
/// object through the window's own session (rule 4).</para>
/// </remarks>
public interface IPowerShellWindowActions
{
    /// <summary><c>Out-AdmGrid</c>: shows the objects in the program's object list (sorting, filters, columns, copy and CSV export work as for any list).</summary>
    Task ShowInGridAsync(PowerShellGridTable table, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Open-AdmObject</c>: opens the properties of an object. <paramref name="identity"/> is the distinguished name of the object:
    /// the command resolves a name, a SID or an <c>objectGUID</c> and reads the object first, through the window's own session (one
    /// DC, escaped filters), so the host opens what exists and never reads the directory itself. A text that is no distinguished
    /// name is <see cref="PowerShellOpenOutcome.NotFound"/>.
    /// </summary>
    Task<PowerShellOpenOutcome> OpenObjectAsync(string identity, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Export-AdmList</c>: writes <paramref name="table"/> as CSV with formula protection, as the list export does.
    /// </summary>
    /// <param name="table">The objects.</param>
    /// <param name="path">The file to write; null asks the operator in <i>Save As</i>.</param>
    /// <param name="delimiter">The field separator; null is the list separator of the system's regional settings.</param>
    /// <param name="cancellationToken">Stop.</param>
    /// <returns>The path that was written, or null when the operator cancelled <i>Save As</i> (nothing was written).</returns>
    Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken);
}

/// <summary>
/// The actions of a window whose host provides none (tests, a host without the window): every call says so, in a way a
/// command shows as an error. R1's window registers the real ones.
/// </summary>
public sealed class UnavailablePowerShellWindowActions : IPowerShellWindowActions
{
    public static UnavailablePowerShellWindowActions Instance { get; } = new();

    public Task ShowInGridAsync(PowerShellGridTable table, CancellationToken cancellationToken) => Task.FromException(Unavailable());

    public Task<PowerShellOpenOutcome> OpenObjectAsync(string identity, CancellationToken cancellationToken) => Task.FromException<PowerShellOpenOutcome>(Unavailable());

    public Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken) => Task.FromException<string?>(Unavailable());

    private static NotSupportedException Unavailable() => new("This window cannot do that: the program did not give it the means.");
}
