using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Get-AdmUser</c>: user accounts, by identity or by the filters of its parameters (all of them must match). <c>*</c> in a text
/// parameter is a wildcard, everything else is literal. Read-only, through the window's directory only.
/// </summary>
[Cmdlet(VerbsCommon.Get, "AdmUser", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetAdmUserCommand : AdmSearchCmdlet
{
    private const string IdentitySet = "Identity";
    private const string FilterSet = "Filter";

    /// <summary>A distinguished name, an <c>objectGUID</c>, a SID, a <c>sAMAccountName</c> or a user principal name.</summary>
    [Parameter(ParameterSetName = IdentitySet, Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public string[] Identity { get; set; } = [];

    /// <summary>The name (<c>name</c> / <c>cn</c>); <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }

    /// <summary>The <c>sAMAccountName</c>; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? SamAccountName { get; set; }

    /// <summary>The user principal name; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? UserPrincipalName { get; set; }

    /// <summary>The first name (<c>givenName</c>); <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? GivenName { get; set; }

    /// <summary>The surname (<c>sn</c>); <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Surname { get; set; }

    /// <summary>The display name; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? DisplayName { get; set; }

    /// <summary>The e-mail address (<c>mail</c>); <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [Alias("EmailAddress")]
    [ValidateNotNullOrEmpty]
    public string? Mail { get; set; }

    /// <summary>The department; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Department { get; set; }

    /// <summary>The job title; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Title { get; set; }

    /// <summary>The company; <c>*</c> is a wildcard.</summary>
    [Parameter(ParameterSetName = FilterSet)]
    [ValidateNotNullOrEmpty]
    public string? Company { get; set; }

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
        var projection = Project(context, AdmObjectType.User);
        if (ParameterSetName == IdentitySet)
        {
            ReadIdentities(context, Identity, ObjectKind.User, projection, "a user");
            return;
        }

        var searchBase = ResolveBase(context);
        var filter = AdmFilterBuilder.All(
            AdmFilterBuilder.ForKind(ObjectKind.User),
            AdmFilterBuilder.ForWildcard(Attr.Name, Name),
            AdmFilterBuilder.ForWildcard(Attr.SamAccountName, SamAccountName),
            AdmFilterBuilder.ForWildcard(Attr.UserPrincipalName, UserPrincipalName),
            AdmFilterBuilder.ForWildcard(Attr.GivenName, GivenName),
            AdmFilterBuilder.ForWildcard(Attr.Sn, Surname),
            AdmFilterBuilder.ForWildcard(Attr.DisplayName, DisplayName),
            AdmFilterBuilder.ForWildcard(Attr.Mail, Mail),
            AdmFilterBuilder.ForWildcard(Attr.Department, Department),
            AdmFilterBuilder.ForWildcard(Attr.Title, Title),
            AdmFilterBuilder.ForWildcard(Attr.Company, Company),
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
