using System.Management.Automation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Get-AdmObject</c>: one object of the domain by its identity, or the objects a ready LDAP filter finds. Read-only, through
/// the window's directory only. The objects are typed <c>AdMng.Object</c> first (one table for every kind), then
/// <c>AdMng.User</c>, <c>AdMng.Group</c> or <c>AdMng.Computer</c> by what they are, each with <c>DistinguishedName</c>.
/// </summary>
[Cmdlet(VerbsCommon.Get, "AdmObject", DefaultParameterSetName = IdentitySet)]
[OutputType(typeof(PSObject))]
public sealed class GetAdmObjectCommand : AdmSearchCmdlet
{
    private const string IdentitySet = "Identity";
    private const string FilterSet = "LdapFilter";

    /// <summary>A distinguished name, an <c>objectGUID</c>, a SID or a <c>sAMAccountName</c> / user principal name.</summary>
    [Parameter(ParameterSetName = IdentitySet, Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public string[] Identity { get; set; } = [];

    /// <summary>A ready LDAP filter, e.g. <c>(&amp;(objectCategory=person)(department=IT))</c>; its syntax is checked first.</summary>
    [Parameter(ParameterSetName = FilterSet, Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string LdapFilter { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var context = RequireContext();
        var projection = Project(context, AdmObjectType.Object);
        if (ParameterSetName == IdentitySet)
        {
            ReadIdentities(context, Identity, ObjectKind.Unknown, projection);
            return;
        }

        var filter = CheckLdapFilter(LdapFilter);
        var searchBase = ResolveBase(context);
        WriteSearch(
            context,
            new DirectorySearch { BaseDn = searchBase, Scope = SearchScope, Filter = filter, Attributes = projection.Request },
            projection,
            searchBase);
    }
}
