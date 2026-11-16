namespace AdMng.PowerShell.Tests;

/// <summary>ADR-0010, "Package licenses": the non-OSI sni.dll packages of the SDK's closure are excluded from every output.</summary>
public sealed class PackageLicenseTests
{
    [Fact]
    public void The_build_output_has_no_sni_dll_and_the_deps_file_does_not_name_it()
    {
        var found = Directory.EnumerateFiles(AppContext.BaseDirectory, "sni.dll", SearchOption.AllDirectories).ToList();
        var deps = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.deps.json").ToList();

        Assert.Empty(found);
        Assert.NotEmpty(deps);
        Assert.All(deps, file => Assert.DoesNotContain("sni", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_engine_assembly_is_in_the_output_so_the_check_looks_at_a_real_build()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "System.Management.Automation.dll"))
            || Directory.EnumerateFiles(AppContext.BaseDirectory, "System.Management.Automation.dll", SearchOption.AllDirectories).Any());
    }
}
