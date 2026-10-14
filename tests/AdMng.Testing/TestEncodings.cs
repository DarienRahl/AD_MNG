using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace AdMng.Testing;

/// <summary>
/// Registers the Windows ANSI code pages (Windows-1250 of Polish Excel files) in every test process that loads the shared
/// test library. Tests write legacy-encoded files that the application reads; the application registers the provider
/// itself where it decodes them (<c>CsvFiles</c>), so without this a test's own <c>Encoding.GetEncoding(1250)</c> worked
/// only when an earlier test had already touched the application's CSV code (an order-dependent failure).
/// </summary>
internal static class TestEncodings
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Test-only library: the code pages must be available before any test of any project that uses it writes a legacy-encoded file.")]
    internal static void RegisterCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
