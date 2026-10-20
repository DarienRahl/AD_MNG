namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>Files of the repository checkout the tests run from (the shipped packaging tree, docs).</summary>
public static class RepositoryFiles
{
    /// <summary>The checkout root: the nearest ancestor of the test binaries that holds <c>AdMng.slnx</c>.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>The absolute path of <paramref name="relativePath"/> (forward slashes) below <see cref="Root"/>.</summary>
    public static string Path(string relativePath) =>
        System.IO.Path.Combine([Root, .. relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    public static string ReadAllText(string relativePath) => File.ReadAllText(Path(relativePath));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "AdMng.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (AdMng.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
