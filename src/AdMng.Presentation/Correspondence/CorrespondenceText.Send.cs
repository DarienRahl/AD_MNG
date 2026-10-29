namespace AdMng.Presentation.Correspondence;

public static partial class CorrespondenceText
{
    /// <summary><c>Correspondence.Responses.Kind.{kind}</c>.</summary>
    public static string ResponseKindKey(ResponseKind kind) => "Correspondence.Responses.Kind." + kind;

    /// <summary>Dynamic keys of the send and responses pages (track send).</summary>
    private static partial IEnumerable<string> SendKeys() => [.. Enum.GetValues<ResponseKind>().Select(ResponseKindKey)];
}
