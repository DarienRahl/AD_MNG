using System.Management.Automation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Get-AdmGroupMember</c>: the members of a group; with <c>-Recursive</c> also the members of the groups nested in it, as the
/// program's "All (nested)" list shows them (the domain controller evaluates the nesting, cycles included). Read-only, through the
/// window's directory only, streamed.
/// </summary>
[Cmdlet(VerbsCommon.Get, "AdmGroupMember")]
[OutputType(typeof(PSObject))]
public sealed class GetAdmGroupMemberCommand : AdmReadCmdlet
{
    /// <summary>The group: a distinguished name, an <c>objectGUID</c>, a SID or a <c>sAMAccountName</c>.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public string[] Identity { get; set; } = [];

    /// <summary>Also the members of the nested groups (and their members, to any depth).</summary>
    [Parameter]
    public SwitchParameter Recursive { get; set; }

    /// <summary>Leave the groups out of the result: only the accounts and other objects that are members.</summary>
    [Parameter]
    public SwitchParameter ExcludeGroups { get; set; }

    protected override void ProcessRecord()
    {
        var context = RequireContext();
        var projection = Project(context, AdmObjectType.Object);
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

            var groupDn = ResolveGroup(context, identity);
            if (groupDn is null)
            {
                continue;
            }

            if (Recursive)
            {
                WriteSearch(context, AdmGroupMembers.NestedSearch(context.Reader, groupDn, projection.Request, ExcludeGroups), projection, groupDn);
                continue;
            }

            var report = new AdmMemberReport();
            Stream(
                context,
                AdmGroupMembers.DirectAsync(context.Reader, groupDn, projection.Request, ExcludeGroups, ResultSetSize, report, StopToken),
                projection,
                groupDn);
            if (report.Missing > 0)
            {
                WriteWarning(
                    $"{report.Missing} member(s) of '{groupDn}' could not be read (deleted meanwhile, in another domain, or this account may not read them), for example: " +
                    string.Join("; ", report.Samples));
            }
        }
    }

    /// <summary>The distinguished name of the group named by <paramref name="identity"/>; writes the error and returns null when there is none.</summary>
    private string? ResolveGroup(IAdmWindowContext context, AdmIdentity identity)
    {
        var search = AdmSearchPlan.ForIdentity(context.Reader, identity, ObjectKind.Group, [AdMng.Core.ActiveDirectory.Attr.ObjectClass], null, SearchScope.Subtree);
        try
        {
            var group = Wait(AdmSearchPlan.FirstAsync(context.Reader.SearchAsync(search, StopToken), StopToken));
            if (group is not null)
            {
                return group.DistinguishedName;
            }
        }
        catch (DirectoryException error) when (error.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // Reported below as "not found".
        }
        catch (DirectoryException error)
        {
            WriteError(AdmErrors.FromDirectory(error, identity.Text));
            return null;
        }

        WriteError(AdmErrors.NotFound(identity.Text, "a group"));
        return null;
    }
}
