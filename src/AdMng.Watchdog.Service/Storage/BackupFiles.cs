using System.Globalization;
using System.Text.RegularExpressions;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// The <c>&lt;data&gt;/backup</c> folder (spec 16.1/16.3): names of retention backups (<c>watchdog-yyyyMMdd-HHmm[-n].db</c>)
/// and pre-migration backups (<c>pre-migration-&lt;old&gt;-yyyyMMddHHmmss.db</c>) and their rotation. Times come from the
/// names (written with the service's <see cref="TimeProvider"/>), never from file system timestamps.
/// </summary>
internal sealed partial class BackupFiles(string backupDirectory)
{
    private const string RetentionPrefix = "watchdog-";
    private const string PreMigrationPrefix = "pre-migration-";

    public string Directory { get; } = backupDirectory;

    /// <summary>Creates the folder when missing (Windows: inherits the protected DACL of the data folder; Unix: 0700).</summary>
    public void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        else
        {
            System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A free path for a retention backup taken at <paramref name="at"/>.</summary>
    public string NewRetentionBackupPath(DateTimeOffset at)
    {
        var stamp = at.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var path = Path.Combine(Directory, $"{RetentionPrefix}{stamp}.db");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(Directory, $"{RetentionPrefix}{stamp}-{n.ToString(CultureInfo.InvariantCulture)}.db");
        }

        return path;
    }

    /// <summary>The path of the backup taken before migrating from <paramref name="oldVersion"/>.</summary>
    public string NewPreMigrationBackupPath(int oldVersion, DateTimeOffset at) => Path.Combine(
        Directory,
        string.Create(CultureInfo.InvariantCulture, $"{PreMigrationPrefix}{oldVersion}-{at.UtcDateTime:yyyyMMddHHmmss}.db"));

    /// <summary>Retention backups, newest first.</summary>
    public IReadOnlyList<(string Path, DateTimeOffset TakenAt)> RetentionBackups() => List(RetentionPattern());

    /// <summary>Pre-migration backups, newest first.</summary>
    public IReadOnlyList<(string Path, DateTimeOffset TakenAt)> PreMigrationBackups() => List(PreMigrationPattern());

    /// <summary>Deletes retention backups beyond the newest <paramref name="keep"/>; returns the number deleted.</summary>
    public int RotateRetentionBackups(int keep)
    {
        var deleted = 0;
        foreach (var (path, _) in RetentionBackups().Skip(Math.Max(1, keep)))
        {
            File.Delete(path);
            deleted++;
        }

        return deleted;
    }

    /// <summary>Keeps the newest pre-migration backup and deletes the others older than <paramref name="maxAge"/>.</summary>
    public int RotatePreMigrationBackups(TimeSpan maxAge, DateTimeOffset now)
    {
        var deleted = 0;
        foreach (var (path, takenAt) in PreMigrationBackups().Skip(1))
        {
            if (takenAt < now - maxAge)
            {
                File.Delete(path);
                deleted++;
            }
        }

        return deleted;
    }

    private List<(string Path, DateTimeOffset TakenAt)> List(Regex pattern)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var result = new List<(string Path, DateTimeOffset TakenAt, int Sequence)>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.db"))
        {
            var match = pattern.Match(Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            var format = match.Groups["stamp"].Value.Length == 13 ? "yyyyMMdd-HHmm" : "yyyyMMddHHmmss";
            if (DateTime.TryParseExact(match.Groups["stamp"].Value, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var takenAt))
            {
                var sequence = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
                result.Add((path, new DateTimeOffset(takenAt, TimeSpan.Zero), sequence));
            }
        }

        return [.. result.OrderByDescending(r => r.TakenAt).ThenByDescending(r => r.Sequence).Select(r => (r.Path, r.TakenAt))];
    }

    [GeneratedRegex(@"^watchdog-(?<stamp>\d{8}-\d{4})(-(?<n>\d+))?\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex RetentionPattern();

    [GeneratedRegex(@"^pre-migration-\d+-(?<stamp>\d{14})\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex PreMigrationPattern();
}
