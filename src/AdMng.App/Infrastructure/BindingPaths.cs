using System.Text;

namespace AdMng.App.Infrastructure;

/// <summary>Builds WPF binding paths from untrusted names (attribute names, resource keys).</summary>
public static class BindingPaths
{
    /// <summary>
    /// Indexer path <c>[name]</c> with every character that is special inside a PropertyPath indexer
    /// (<c>^ , [ ] ( )</c> and white space) escaped with <c>^</c>, so the name reaches the indexer verbatim.
    /// </summary>
    public static string Indexer(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var path = new StringBuilder(name.Length + 4).Append('[');
        foreach (var c in name)
        {
            if (c is '^' or ',' or '[' or ']' or '(' or ')' || char.IsWhiteSpace(c))
            {
                path.Append('^');
            }

            path.Append(c);
        }

        return path.Append(']').ToString();
    }
}
