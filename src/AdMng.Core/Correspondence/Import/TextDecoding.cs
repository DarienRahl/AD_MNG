namespace AdMng.Core.Correspondence.Import;

/// <summary>Text of imported files: BOM, strict UTF-8, else the ANSI code page (registers <c>CodePagesEncodingProvider</c>).</summary>
public static class TextDecoding
{
    /// <summary>Decodes <paramref name="bytes"/>: BOM first, then strict UTF-8, else <paramref name="ansiCodePage"/>.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, int ansiCodePage) => throw new NotImplementedException("U5 T1 core");
}
