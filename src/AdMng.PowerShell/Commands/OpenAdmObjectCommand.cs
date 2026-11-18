using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Open-AdmObject</c>: opens the properties of an object in the program, as a double-click in the list does. The identity is a
/// distinguished name, an <c>objectGUID</c>, a SID, a <c>sAMAccountName</c> or a user principal name; the object is read first
/// (through the window's directory, like the read commands), so a name that does not exist is an error and no window opens for it.
/// </summary>
[Cmdlet(VerbsCommon.Open, "AdmObject")]
public sealed class OpenAdmObjectCommand : AdmCmdlet
{
    /// <summary>The most property windows one command opens: a pipeline of a thousand objects is not a thousand windows.</summary>
    public const int MaxWindows = 10;

    private int _opened;
    private bool _refusedMore;

    /// <summary>A distinguished name, an <c>objectGUID</c>, a SID or an account name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public string[] Identity { get; set; } = [];

    protected override void ProcessRecord()
    {
        var context = RequireContext();
        foreach (var text in Identity)
        {
            if (StopToken.IsCancellationRequested)
            {
                throw new PipelineStoppedException();
            }

            var identity = AdmIdentity.Parse(text);
            if (identity is null)
            {
                WriteError(AdmErrors.InvalidIdentity());
                continue;
            }

            if (_opened >= MaxWindows)
            {
                if (!_refusedMore)
                {
                    _refusedMore = true;
                    WriteError(new ErrorRecord(
                        new InvalidOperationException($"Open-AdmObject opens at most {MaxWindows} windows in one command; the rest were not opened."),
                        "AdmTooManyWindows",
                        ErrorCategory.LimitsExceeded,
                        identity.Text));
                }

                continue;
            }

            var dn = Find(context, identity);
            if (dn is null)
            {
                continue;
            }

            if (Wait(context.Services.OpenObjectAsync(dn, StopToken)))
            {
                _opened++;
            }
            else
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException($"The program could not open '{dn}'."), "AdmObjectNotOpened", ErrorCategory.OpenError, dn));
            }
        }
    }

    /// <summary>The distinguished name of the object, or null (after an error record) when it cannot be found or read.</summary>
    private string? Find(IAdmWindowContext context, AdmIdentity identity)
    {
        try
        {
            var search = AdmSearchPlan.ForIdentity(context.Reader, identity, ObjectKind.Unknown, [Attr.Name], searchBase: null, SearchScope.Subtree);
            var found = Wait(AdmSearchPlan.FirstAsync(context.Reader.SearchAsync(search, StopToken), StopToken));
            if (found is null)
            {
                WriteError(AdmErrors.NotFound(identity.Text, "an object"));
                return null;
            }

            return found.DistinguishedName;
        }
        catch (DirectoryException error) when (error.Kind == DirectoryErrorKind.NoSuchObject)
        {
            WriteError(AdmErrors.NotFound(identity.Text, "an object"));
            return null;
        }
        catch (DirectoryException error)
        {
            if (StopToken.IsCancellationRequested)
            {
                throw new PipelineStoppedException();
            }

            WriteError(AdmErrors.FromDirectory(error, identity.Text));
            return null;
        }
    }
}
