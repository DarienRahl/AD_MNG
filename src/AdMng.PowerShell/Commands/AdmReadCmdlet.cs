using System.Management.Automation;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.PowerShell.Completion;
using AdMng.PowerShell.Reading;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// Base of AD Manager's read commands (docs/dev/r1-powershell-window.md 12.2): reads only through the window's directory
/// (<see cref="IAdmWindowContext.Reader"/>, one DC - there is no <c>-Server</c> and no <c>-Credential</c>), on the pipeline thread
/// with the stop token of the pipeline, one object at a time (nothing is collected: a consumer that stops after 50 results stops
/// the read), every filter built by <see cref="AdmFilterBuilder"/> from <see cref="LdapFilter"/>.
/// </summary>
public abstract class AdmReadCmdlet : AdmCmdlet
{
    /// <summary>Attributes to return besides the defaults of the command; <c>*</c> returns everything the account may read.</summary>
    [Parameter]
    [ArgumentCompleter(typeof(AdmAttributeCompleter))]
    public string[]? Properties { get; set; }

    /// <summary>How many objects the domain controller returns per page (paged results); results are passed on as each page arrives.</summary>
    [Parameter]
    [ValidateRange(1, 10000)]
    public int ResultPageSize { get; set; } = 500;

    /// <summary>The most objects to return; 0 (the default) returns all.</summary>
    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int ResultSetSize { get; set; }

    /// <summary>Reads the objects of one search and writes them to the pipeline; returns how many were written.</summary>
    /// <param name="context">The window's services.</param>
    /// <param name="search">The search; <see cref="DirectorySearch.PageSize"/> and <see cref="DirectorySearch.SizeLimit"/> are filled in here.</param>
    /// <param name="projection">The attributes of the result objects.</param>
    /// <param name="target">What a directory error names (the identity or the search base).</param>
    /// <param name="missingBaseIsAnError">
    /// False when the caller reads an object by its distinguished name and reports "not found" itself: the directory's "no such
    /// object" then reaches it as an exception instead of an error record.
    /// </param>
    private protected int WriteSearch(IAdmWindowContext context, DirectorySearch search, AdmProjection projection, string? target, bool missingBaseIsAnError = true)
    {
        var withLimits = search with { PageSize = ResultPageSize, SizeLimit = ResultSetSize };
        return Stream(context, context.Reader.SearchAsync(withLimits, StopToken), projection, target, missingBaseIsAnError);
    }

    /// <summary>Writes the objects of <paramref name="source"/> projected as script objects; a directory error becomes an error record.</summary>
    private protected int Stream(IAdmWindowContext context, IAsyncEnumerable<DirectoryObject> source, AdmProjection projection, string? target, bool missingBaseIsAnError = true)
    {
        var projector = new AdmObjectProjector(context.Reader, context.KnownContainers);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        try
        {
            WriteAll(
                ProjectAsync(source, projector, projection, StopToken),
                projected =>
                {
                    foreach (var hidden in projected.HiddenAttributes)
                    {
                        if (reported.Add(hidden))
                        {
                            WriteError(AdmErrors.HiddenAttribute(hidden, (string)projected.Object.Properties["DistinguishedName"].Value));
                        }
                    }

                    written++;
                    return projected.Object;
                });
        }
        catch (DirectoryException error) when (error.Kind != DirectoryErrorKind.NoSuchObject || missingBaseIsAnError)
        {
            WriteError(AdmErrors.FromDirectory(error, target));
        }

        return written;
    }

    /// <summary>The attributes and type names of the command's objects; ends the command with an error when a name is refused.</summary>
    private protected AdmProjection Project(IAdmWindowContext context, AdmObjectType type)
    {
        var projection = AdmProjection.Create(type, Properties, context.Reader.Schema, out var problem);
        if (projection is null)
        {
            ThrowTerminatingError(AdmErrors.InvalidArgument(problem!, "AdmInvalidProperty", Properties));
        }

        return projection!;
    }

    /// <summary>The filter text of a ready LDAP filter; ends the command with an error when its syntax is wrong.</summary>
    private protected string CheckLdapFilter(string filter)
    {
        var problem = AdmFilterBuilder.CheckRaw(filter, out var wrapped);
        if (problem is not null)
        {
            ThrowTerminatingError(AdmErrors.InvalidArgument($"The LDAP filter is not valid: {problem}", "AdmInvalidLdapFilter", filter));
        }

        return wrapped;
    }

    private static async IAsyncEnumerable<AdmProjected> ProjectAsync(
        IAsyncEnumerable<DirectoryObject> source,
        AdmObjectProjector projector,
        AdmProjection projection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var obj in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return await projector.ProjectAsync(obj, projection, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A read command that searches below a base: <c>-SearchBase</c> and <c>-SearchScope</c>.</summary>
public abstract class AdmSearchCmdlet : AdmReadCmdlet
{
    /// <summary>The container to search in (a distinguished name); the domain by default.</summary>
    [Parameter]
    [ArgumentCompleter(typeof(AdmContainerCompleter))]
    public string? SearchBase { get; set; }

    /// <summary>How deep to search below <c>-SearchBase</c>: Base, OneLevel or Subtree (the default).</summary>
    [Parameter]
    [Alias("Scope")]
    public SearchScope SearchScope { get; set; } = SearchScope.Subtree;

    /// <summary>The validated search base, or the domain; ends the command with an error for a text that is no distinguished name.</summary>
    private protected string ResolveBase(IAdmWindowContext context)
    {
        if (SearchBase is null)
        {
            return context.Reader.Info.DefaultNamingContext;
        }

        if (!DistinguishedName.TryParse(SearchBase, out var parsed) || parsed.IsRoot)
        {
            ThrowTerminatingError(AdmErrors.InvalidArgument($"'{SearchBase}' is not a distinguished name (-SearchBase takes the DN of a container).", "AdmInvalidSearchBase", SearchBase));
        }

        return SearchBase;
    }

    /// <summary>
    /// Reads the objects named by <paramref name="identities"/> (a DN, an <c>objectGUID</c>, a SID or an account name): an object
    /// that does not exist, or is not of <paramref name="kind"/>, is a non-terminating error that names it.
    /// </summary>
    private protected void ReadIdentities(IAdmWindowContext context, IEnumerable<string?> identities, ObjectKind kind, AdmProjection projection, string description)
    {
        foreach (var text in identities)
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

            var searchBase = identity.Kind == AdmIdentityKind.DistinguishedName ? identity.Value : ResolveBase(context);
            var search = AdmSearchPlan.ForIdentity(context.Reader, identity, kind, projection.Request, SearchBase, SearchScope);

            int found;
            try
            {
                found = WriteSearch(context, search, projection, identity.Text, missingBaseIsAnError: false);
            }
            catch (DirectoryException error) when (error.Kind == DirectoryErrorKind.NoSuchObject)
            {
                found = 0;
            }

            if (found == 0 && !StopToken.IsCancellationRequested)
            {
                WriteError(AdmErrors.NotFound(identity.Text, description, identity.Kind == AdmIdentityKind.DistinguishedName ? null : searchBase));
            }
        }
    }
}
