using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>The scope of a group, as the filter of <c>Get-AdmGroup -GroupScope</c> offers it.</summary>
public enum AdmGroupScope
{
    /// <summary>Domain local.</summary>
    DomainLocal,

    /// <summary>Global.</summary>
    Global,

    /// <summary>Universal.</summary>
    Universal,
}

/// <summary>The category of a group, as the filter of <c>Get-AdmGroup -GroupCategory</c> offers it.</summary>
public enum AdmGroupCategory
{
    /// <summary>A security group.</summary>
    Security,

    /// <summary>A distribution group.</summary>
    Distribution,
}

/// <summary>
/// <c>Get-AdmGroup</c>: groups, by identity or by the filters of its parameters (all of them must match). <c>*</c> in a text
/// parameter is a wildcard, everything else is literal. Read-only, through the window's directory only.
/// </summary>
[Cmdlet(VerbsCommon.Get, "AdmGroup", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetAdmGroupCommand : AdmSearchCmdlet
{
    private const string IdentitySet = "Identity";
    private const string FilterSet = "Filter";

    /// <summary>A distinguished name, an <c>objectGUID</c>, a SID or a <c>sAMAccountName</c>.</summary>
    [Parameter(ParameterSetName = IdentitySet, Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public string[] Identity { get; set; } = [];

    /// <summary>The name; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }

    /// <summary>The <c>sAMAccountName</c>; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? SamAccountName { get; set; }

    /// <summary>The description; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Description { get; set; }

    /// <summary>DomainLocal, Global or Universal.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    public AdmGroupScope? GroupScope { get; set; }

    /// <summary>Security or Distribution.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    public AdmGroupCategory? GroupCategory { get; set; }

    /// <summary>A ready LDAP filter added to the others; its syntax is checked first.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? LdapFilter { get; set; }

    protected override void ProcessRecord()
    {
        var context = RequireContext();
        var projection = Project(context, AdmObjectType.Group);
        if (ParameterSetName == IdentitySet)
        {
            ReadIdentities(context, Identity, ObjectKind.Group, projection, "a group");
            return;
        }

        var searchBase = ResolveBase(context);
        var filter = AdmFilterBuilder.All(
            AdmFilterBuilder.ForKind(ObjectKind.Group),
            AdmFilterBuilder.ForWildcard(Attr.Name, Name),
            AdmFilterBuilder.ForWildcard(Attr.SamAccountName, SamAccountName),
            AdmFilterBuilder.ForWildcard(Attr.Description, Description),
            AdmFilterBuilder.ForGroupScope(GroupScope switch
            {
                AdmGroupScope.DomainLocal => Core.ActiveDirectory.GroupScope.DomainLocal,
                AdmGroupScope.Global => Core.ActiveDirectory.GroupScope.Global,
                AdmGroupScope.Universal => Core.ActiveDirectory.GroupScope.Universal,
                _ => null,
            }),
            AdmFilterBuilder.ForGroupCategory(GroupCategory switch
            {
                AdmGroupCategory.Security => true,
                AdmGroupCategory.Distribution => false,
                _ => null,
            }),
            LdapFilter is null ? null : CheckLdapFilter(LdapFilter));
        WriteSearch(
            context,
            new DirectorySearch { BaseDn = searchBase, Scope = SearchScope, Filter = filter, Attributes = projection.Request },
            projection,
            searchBase);
    }
}
