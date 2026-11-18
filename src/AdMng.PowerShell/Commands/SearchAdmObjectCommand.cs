using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.PowerShell.Completion;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// <c>Search-AdmObject</c>: finds objects of the domain whose attribute matches a value, or by a ready LDAP filter. The program
/// builds the filter from the parameters and escapes every value (RFC 4515): <c>Łukasz (Kraków)*\</c> matches only itself, not
/// every name, and a value that looks like a filter is only a value. Read-only, through the window's directory only, streamed.
/// </summary>
[Cmdlet(VerbsCommon.Search, "AdmObject", DefaultParameterSetName = NameSet)]
[OutputType(typeof(PSObject))]
public sealed class SearchAdmObjectCommand : AdmSearchCmdlet
{
    private const string NameSet = "Name";
    private const string AttributeSet = "Attribute";
    private const string FilterSet = "Filter";

    /// <summary>The name (<c>name</c>) to look for; several names find objects with any of them.</summary>
    [Parameter(ParameterSetName = NameSet, Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string[] Name { get; set; } = [];

    /// <summary>The attribute(s) to compare (LDAP names); an object matches when any of them matches any value.</summary>
    [Parameter(ParameterSetName = AttributeSet, Mandatory = true)]
    [ValidateNotNullOrEmpty]
    [ArgumentCompleter(typeof(AdmAttributeCompleter))]
    public string[] Attribute { get; set; } = [];

    /// <summary>The value(s) to look for. A <c>*</c> or a bracket in a value is part of the value; <c>-Match</c> says how it is compared.</summary>
    [Parameter(ParameterSetName = AttributeSet, Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    public string[] Value { get; set; } = [];

    /// <summary>How a value is compared with the attribute: Exact (the default), StartsWith, Contains or EndsWith.</summary>
    [Parameter(ParameterSetName = NameSet)]
    [Parameter(ParameterSetName = AttributeSet)]
    public AdmMatch Match { get; set; } = AdmMatch.Exact;

    /// <summary>A ready LDAP filter, e.g. <c>(&amp;(objectCategory=person)(department=IT))</c>; its syntax is checked first.</summary>
    [Parameter(ParameterSetName = FilterSet, Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string Filter { get; set; } = string.Empty;

    /// <summary>Only objects of this class (<c>user</c>, <c>group</c>, <c>organizationalUnit</c>, ...).</summary>
    [Parameter]
    [ValidatePattern("^[A-Za-z][A-Za-z0-9-]*$")]
    public string? ObjectClass { get; set; }

    protected override void ProcessRecord()
    {
        var context = RequireContext();
        var schema = context.Reader.Schema;
        var projection = Project(context, AdmObjectType.Object);
        var searchBase = ResolveBase(context);
        var filter = ParameterSetName switch
        {
            FilterSet => CheckLdapFilter(Filter),
            AttributeSet => AdmFilterBuilder.Match(Attributes(schema), Values(), Match),
            _ => AdmFilterBuilder.Match([Attr.Name], Name, Match),
        };
        WriteSearch(
            context,
            new DirectorySearch
            {
                BaseDn = searchBase,
                Scope = SearchScope,
                Filter = AdmFilterBuilder.WithObjectClass(filter, ObjectClass),
                Attributes = projection.Request,
            },
            projection,
            searchBase);
    }

    /// <summary>The attributes to compare, in the schema's spelling; ends the command when one is no attribute or is a secret.</summary>
    private List<string> Attributes(ISchemaCatalog schema)
    {
        var names = new List<string>();
        foreach (var raw in Attribute)
        {
            var name = raw.Trim();
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                ThrowTerminatingError(AdmErrors.InvalidArgument($"'{raw}' is not an attribute name.", "AdmInvalidAttribute", raw));
            }

            if (schema.Attributes.Count > 0 && schema.FindAttribute(name) is null)
            {
                ThrowTerminatingError(AdmErrors.InvalidArgument($"The schema of this domain has no attribute '{name}'. Press Tab after -Attribute to see the attribute names.", "AdmInvalidAttribute", raw));
            }

            if (SecretAttributes.IsSecret(name, schema))
            {
                ThrowTerminatingError(AdmErrors.InvalidArgument($"'{name}' holds secrets: AD Manager does not search by the value of a secret attribute.", "AdmSecretAttribute", raw));
            }

            names.Add(schema.FindAttribute(name)?.LdapName ?? name);
        }

        return names;
    }

    private string[] Values()
    {
        if (Value.Any(string.IsNullOrEmpty))
        {
            ThrowTerminatingError(AdmErrors.InvalidArgument("A value to look for cannot be empty.", "AdmInvalidValue", Value));
        }

        return Value;
    }
}
