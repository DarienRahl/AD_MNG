using System.Management.Automation;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Out-AdmGrid</c>: shows the objects of the pipeline in a grid of the program - the properties are the columns, a column of
/// distinguished names makes a row openable, sorting, a filter, copying and the CSV export of the grid work. The objects are turned
/// into plain text on the pipeline thread; nothing is written anywhere unless the operator exports from the grid.
/// </summary>
[Cmdlet(VerbsData.Out, "AdmGrid")]
public sealed class OutAdmGridCommand : AdmTableCmdlet
{
    /// <summary>The title of the grid window; "Results" by default.</summary>
    [Parameter]
    [ValidateLength(1, 200)]
    public string? Title { get; set; }

    protected override void EndProcessing()
    {
        var context = RequireContext();
        var table = Table.Build(Title);
        if (table is null)
        {
            WriteWarning(Text(AdmText.Keys.GridNoObjects));
            return;
        }

        try
        {
            Wait(context.Services.ShowGridAsync(table, StopToken));
        }
        catch (NotSupportedException error)
        {
            WriteError(new ErrorRecord(error, "AdmGridUnavailable", ErrorCategory.NotImplemented, null));
        }
    }
}
