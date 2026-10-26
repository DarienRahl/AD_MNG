using AdMng.Core.Directory;
using AdMng.Core.Model;

namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// Completes multi-valued attributes that a DC returned only partially (more than 1500 values, a ranged description such
/// as <c>member;range=0-1499</c>) with ranged retrieval. Used by the attribute editor and the comparison of two objects.
/// </summary>
public static class RangedValues
{
    /// <summary>
    /// The object with every partially returned text attribute completed (binary attributes stay as returned: ranged
    /// retrieval returns text). The object itself when nothing was ranged.
    /// </summary>
    public static async Task<DirectoryObject> CompleteAsync(IDirectoryReader reader, DirectoryObject obj, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(obj);
        List<AttributeValues>? completed = null;
        foreach (var values in obj.Attributes.Values)
        {
            if (!IsPartial(values))
            {
                continue;
            }

            var baseName = values.Name[..values.Name.IndexOf(';', StringComparison.Ordinal)];
            if (reader.Schema.IsBinary(baseName))
            {
                continue;
            }

            var all = new List<object>();
            await foreach (var value in reader.GetAllValuesAsync(obj.DistinguishedName, baseName, cancellationToken).ConfigureAwait(false))
            {
                all.Add(value);
            }

            (completed ??= []).Add(new AttributeValues(baseName, all));
        }

        return completed is null ? obj : obj.With(completed);
    }

    /// <summary>True for a ranged description that is not the last range (<c>member;range=0-1499</c>, not <c>…-*</c>).</summary>
    public static bool IsPartial(AttributeValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase) && !values.Name.EndsWith("-*", StringComparison.Ordinal);
    }
}
