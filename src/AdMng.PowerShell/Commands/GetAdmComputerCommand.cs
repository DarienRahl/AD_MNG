using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Get-AdmComputer</c>: computer accounts, by identity or by the filters of its parameters (all of them must match). <c>*</c> in
/// a text parameter is a wildcard, everything else is literal. Read-only, through the window's directory only.
/// </summary>
[Cmdlet(VerbsCommon.Get, "AdmComputer", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetAdmComputerCommand : AdmSearchCmdlet
{
    private const string IdentitySet = "Identity";
    private const string FilterSet = "Filter";

    /// <summary>A distinguished name, an <c>objectGUID</c>, a SID or the name of the account (<c>PC01</c> or <c>PC01$</c>).</summary>
    [Parameter(ParameterSetName = IdentitySet, Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public string[] Identity { get; set; } = [];

    /// <summary>The name; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }

    /// <summary>The <c>sAMAccountName</c> (with the trailing <c>$</c>); <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? SamAccountName { get; set; }

    /// <summary>The DNS host name; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? DnsHostName { get; set; }

    /// <summary>The operating system; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? OperatingSystem { get; set; }

    /// <summary>The location; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Location { get; set; }

    /// <summary>The description; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Description { get; set; }

    /// <summary><c>$true</c> for enabled accounts, <c>$false</c> for disabled ones.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    public bool? Enabled { get; set; }

    /// <summary>A ready LDAP filter added to the others; its syntax is checked first.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? LdapFilter { get; set; }

    protected override void ProcessRecord()
    {
        var context = RequireContext();
        var projection = Project(context, AdmObjectType.Computer);
        if (ParameterSetName == IdentitySet)
        {
            ReadIdentities(context, Identity, ObjectKind.Computer, projection);
            return;
        }

        var searchBase = ResolveBase(context);
        var filter = AdmFilterBuilder.All(
            AdmFilterBuilder.ForKind(ObjectKind.Computer),
            AdmFilterBuilder.ForWildcard(Attr.Name, Name),
            AdmFilterBuilder.ForWildcard(Attr.SamAccountName, SamAccountName),
            AdmFilterBuilder.ForWildcard(Attr.DnsHostName, DnsHostName),
            AdmFilterBuilder.ForWildcard(Attr.OperatingSystem, OperatingSystem),
            AdmFilterBuilder.ForWildcard(Attr.Location, Location),
            AdmFilterBuilder.ForWildcard(Attr.Description, Description),
            AdmFilterBuilder.ForEnabled(Enabled),
            LdapFilter is null ? null : CheckLdapFilter(LdapFilter));
        WriteSearch(
            context,
            new DirectorySearch { BaseDn = searchBase, Scope = SearchScope, Filter = filter, Attributes = projection.Request },
            projection,
            searchBase);
    }
}
