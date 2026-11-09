namespace AdMng.Core.Pki;

/// <summary>
/// The LDAP-only ESC rules (docs/dev/p-certificate-services.md 2.4: ESC1, 2, 3, 4, 5, 9, 13, 14, 15, 17), pure – split
/// like K6 into a reader and this analyzer; C4 may split it into <c>PkiEscAnalyzer.Esc*.cs</c> partials.
/// </summary>
public static partial class PkiEscAnalyzer
{
    /// <summary>Evaluates every rule on <paramref name="snapshot"/>; most severe first.</summary>
    public static PkiEscResult Analyze(PkiEscSnapshot snapshot) => throw new NotImplementedException("P1 C4: PkiEscAnalyzer.Analyze");
}
