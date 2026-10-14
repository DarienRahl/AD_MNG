namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The service's data folder (spec 19.5): <c>watchdog.db</c> (+ WAL), <c>keys\</c> (Data Protection key ring) and
/// <c>backup\</c>. The only place on disk where directory data may live (ADR-0008). Created with a protected DACL on
/// Windows (<see cref="DataDirectoryAcl"/>) and mode 0700 elsewhere; the ACL itself is verified by
/// <see cref="StartupChecks"/>.
/// </summary>
public sealed class DataDirectory
{
    public const string KeysFolderName = "keys";
    public const string BackupFolderName = "backup";

    public DataDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        KeysPath = System.IO.Path.Combine(Path, KeysFolderName);
        BackupPath = System.IO.Path.Combine(Path, BackupFolderName);
    }

    public string Path { get; }

    public string KeysPath { get; }

    public string BackupPath { get; }

    /// <summary>True when <see cref="Ensure"/> created the folder in this run.</summary>
    public bool CreatedNow { get; private set; }

    /// <summary>The configured folder with environment variables and a leading <c>~</c> (home folder) expanded.
    /// <c>%ProgramData%</c> on a host without that variable (Linux lab/dev) maps to the local application data folder of
    /// the user.</summary>
    public static string Resolve(ServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = string.IsNullOrWhiteSpace(options.DataDirectory) ? new ServiceOptions().DataDirectory : options.DataDirectory;
        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded.Length > 2 ? expanded[2..] : string.Empty);
        }

        if (expanded.Contains("%ProgramData%", StringComparison.OrdinalIgnoreCase))
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            expanded = expanded.Replace("%ProgramData%", root, StringComparison.OrdinalIgnoreCase);
        }

        if (!OperatingSystem.IsWindows())
        {
            expanded = expanded.Replace('\\', '/');
        }

        return System.IO.Path.GetFullPath(expanded);
    }

    /// <summary>Creates the folder (protected) and its <c>keys</c>/<c>backup</c> subfolders when missing.</summary>
    public void Ensure()
    {
        if (!System.IO.Directory.Exists(Path))
        {
            CreateProtected(Path);
            CreatedNow = true;
        }

        foreach (var child in new[] { KeysPath, BackupPath })
        {
            if (!System.IO.Directory.Exists(child))
            {
                CreateProtected(child);
            }
        }
    }

    /// <summary>The files whose ACL must stay inside the allow-list: the folder, the database and its WAL/SHM files.</summary>
    public IReadOnlyList<string> ProtectedPaths()
    {
        var database = System.IO.Path.Combine(Path, Storage.SqliteDatabase.FileName);
        return [Path, database, database + "-wal", database + "-shm"];
    }

    private static void CreateProtected(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            DataDirectoryAcl.CreateProtected(path);
        }
        else
        {
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
