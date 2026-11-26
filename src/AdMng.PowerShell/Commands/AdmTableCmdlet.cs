using System.Management.Automation;
using AdMng.PowerShell.Tables;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// Base of the commands that take the objects of a pipeline and make a table of them (<c>Out-AdmGrid</c>, <c>Export-AdmList</c>):
/// every object becomes a row as it arrives (the object itself is not kept), so the table is plain text by the end of the pipeline.
/// </summary>
public abstract class AdmTableCmdlet : AdmCmdlet
{
    private readonly AdmTableBuilder _table = new();

    /// <summary>The objects to show or export; normally from the pipeline.</summary>
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [AllowNull]
    [AllowEmptyString]
    [AllowEmptyCollection]
    public PSObject? InputObject { get; set; }

    private protected AdmTableBuilder Table => _table;

    /// <summary>The cells take the window's language (the words that stand for a secure string or a binary value).</summary>
    protected override void BeginProcessing() => _table.Culture = UiCulture;

    protected override void ProcessRecord()
    {
        if (InputObject is not null)
        {
            _table.Add(InputObject);
        }
    }
}
