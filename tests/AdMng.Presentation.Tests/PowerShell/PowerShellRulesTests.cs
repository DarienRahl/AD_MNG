using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// The rules of CLAUDE.md that the PowerShell window keeps in its presentation code (track R, decisions D8 and D9), as checks
/// that fail when someone breaks them: no reference to the engine, nothing written, nothing remembered in the settings.
/// </summary>
public sealed class PowerShellRulesTests
{
    private static string[] SourceFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AdMng.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "src", "AdMng.Presentation", "PowerShell");
        var files = Directory.GetFiles(directory, "*.cs");
        Assert.True(files.Length >= 10, "the source files of the window were not found");
        return files;
    }

    [Fact]
    public void Presentation_DoesNotReferenceTheEngine_TheEngineIsReachedOnlyThroughTheFactory()
    {
        var referenced = typeof(PowerShellWindowViewModel).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToList();

        Assert.DoesNotContain("AdMng.PowerShell", referenced);
        Assert.DoesNotContain("System.Management.Automation", referenced);
        Assert.DoesNotContain("Microsoft.PowerShell.SDK", referenced);
    }

    [Fact]
    public void TheWindowsPresentationCode_WritesNoFile_AndTouchesNeitherTheRegistryNorTheSettings()
    {
        // The scrollback, the history and the results are in memory (rule 1). The only thing the code reads from the machine is
        // behind IPowerShellPolicySource, which the host implements; a file is written by the host's export, on the operator's path.
        string[] forbidden =
        [
            "File.Write", "File.Create", "File.Append", "File.Open", "File.Copy", "File.Move", "File.Delete", "StreamWriter", "FileStream",
            "Directory.Create", "Path.GetTempPath", "Path.GetTempFileName", "Microsoft.Win32", "RegistryKey", "ISettingsStore", ".Settings", "SpecialFolder", "ILogger",
            "Console.Write", "Debug.Write", "Trace.Write",
        ];

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "PowerShellGridViewModel.cs")
            {
                // The one deletion: the part of an export the operator stopped (audit TI-3) - the file the grid itself began on the
                // operator's path, never anything else.
                Assert.Equal(1, Occurrences(text, "File.Delete"));
                Assert.Contains("private static Task<bool> RemovePartAsync(string path)", text, StringComparison.Ordinal);
                text = text.Replace("File.Delete", string.Empty, StringComparison.Ordinal);
            }

            foreach (var word in forbidden)
            {
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} mentions '{word}'.");
            }
        }
    }

    private static int Occurrences(string text, string word)
    {
        var count = 0;
        for (var index = text.IndexOf(word, StringComparison.Ordinal); index >= 0; index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void TheOnlyFilesTheWindowsCodeWrites_AreTheCsvExportsOfTheGridAndOfExportAdmList_ToAPathTheOperatorGave()
    {
        // Rule 1 allows a file the operator explicitly exports: the grid writes the path of its Save As, the action of the command
        // the path of -Path or of Save As. Nothing else of the window's code may use the CSV writer, and neither of the two is
        // allowed another way of writing (the forbidden words above still hold for them).
        var writers = SourceFiles()
            .Where(file => File.ReadAllText(file).Contains("CsvFiles.WriteAsync", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file) ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["PowerShellGridViewModel.cs", "PowerShellWindowActions.cs"], writers);
    }

    [Fact]
    public void NoWriteIsSpelledInTheWindow_ItKnowsNoCommandThatChangesTheDirectory()
    {
        // R1 has no write command (decision D9). The window passes text to the engine and shows what comes back; it never builds
        // a change plan or touches the directory session's write methods.
        string[] writes = ["ModifyAsync", "AddAsync", "DeleteAsync", "MoveAsync", "IChangePlanner", "IBatchExecutor", "IChangeCoordinator", "ChangePlan"];

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var word in writes)
            {
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} mentions '{word}'.");
            }
        }
    }
}
