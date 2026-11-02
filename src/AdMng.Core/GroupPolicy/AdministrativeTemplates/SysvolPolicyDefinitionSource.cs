namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// The central store of the session's DC (<c>\\&lt;DC&gt;\SYSVOL\&lt;domain&gt;\Policies\PolicyDefinitions</c>, CLAUDE.md rule 4)
/// read through an open SYSVOL connection; files stay in memory (rule 1). The connection belongs to the caller.
/// </summary>
public sealed class SysvolPolicyDefinitionSource(ISysvolConnection connection, string description) : IPolicyDefinitionSource
{
    /// <summary>Most entries of the central store listed (a Windows store has about 400 files per language).</summary>
    public const int MaxEntries = 50_000;

    private readonly ISysvolConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public string Description { get; } = description ?? string.Empty;

    public async Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken)
    {
        var entries = await _connection.ListPolicyDefinitionsAsync(MaxEntries, cancellationToken).ConfigureAwait(false);
        return entries is null ? null : [.. entries.Where(e => !e.IsFolder).Select(e => new PolicyDefinitionFile(e.RelativePath, e.Length))];
    }

    public async Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken)
    {
        var result = await _connection.ReadPolicyDefinitionsFileAsync(relativePath, maxBytes, cancellationToken).ConfigureAwait(false);
        return result.Content;
    }
}
