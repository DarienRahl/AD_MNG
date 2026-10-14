namespace AdMng.Core.Directory;

/// <summary>Options of <see cref="IDirectorySession.GetAllValuesAsync(string,string,AttributeReadOptions,CancellationToken)"/>.</summary>
public sealed record AttributeReadOptions
{
    /// <summary>Return DN values in extended form (see <see cref="DirectorySearch.ExtendedDn"/>).</summary>
    public bool ExtendedDn { get; init; }

    /// <summary>Read a deleted object (Show Deleted control).</summary>
    public bool IncludeDeleted { get; init; }

    public static AttributeReadOptions Default { get; } = new();
}
