using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Backup;

/// <summary>
/// The GPMC backup layout (design note docs/dev/k-computers-and-gpo.md 5.5), so that operators' existing GPMC backups,
/// Microsoft's security baselines and AD Manager's backups are interchangeable:
/// <code>
/// &lt;folder&gt;\manifest.xml                  one BackupInst per backup in the folder (merged, not replaced; hidden)
/// &lt;folder&gt;\{BackupId}\Backup.xml         GroupPolicyBackupScheme: core settings, security groups, extensions with their files
/// &lt;folder&gt;\{BackupId}\bkupInfo.xml       the BackupInst of this backup
/// &lt;folder&gt;\{BackupId}\DomainSysvol\GPO\… Machine\…, User\…, root files except GPT.INI - byte for byte
/// &lt;folder&gt;\{BackupId}\admng.json         AD Manager's sidecar: version, SHA-256 of every file, links, WMI filter, flags
/// </code>
/// <c>gpreport.xml</c> is not written. Reading treats the folder as untrusted input: XML without DTDs or resolvers, every
/// path normalized and confined to <c>DomainSysvol\GPO</c>, the in-memory limits of <see cref="GpoTransaction"/>, the
/// sidecar's hashes checked. Writes go only to the folder the operator chose; a failed write removes what it wrote.
/// </summary>
public static class GpmcBackupFormat
{
    public const string ManifestFile = "manifest.xml";
    public const string BackupFile = "Backup.xml";
    public const string InfoFile = "bkupInfo.xml";
    public const string SidecarFile = "admng.json";
    public const string SysvolFolder = "DomainSysvol";
    public const string GpoFolder = "GPO";

    private const string ManifestNamespace = "http://www.microsoft.com/GroupPolicy/GPOOperations/Manifest";
    private const string BackupNamespace = "http://www.microsoft.com/GroupPolicy/GPOOperations";
    private const long MaxXmlBytes = 16L * 1024 * 1024;

    /// <summary>The CSE of a file of the GPO folder for <c>Backup.xml</c> (the Watchdog's file classifier decides).</summary>
    public static (Guid Id, string Name) ExtensionOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var file = GpoFileClassifier.Classify(relativePath);
        return file.Role switch
        {
            GpoFileRole.SecurityTemplate => (new Guid("827D319E-6EAC-11D2-A4EA-00C04F79F83A"), "Security"),
            GpoFileRole.AuditCsv => (new Guid("F3CCC681-B74C-4060-9F26-CD84525DCA2A"), "Audit Policy Configuration"),
            GpoFileRole.ScriptsIni or GpoFileRole.PsScriptsIni or GpoFileRole.ScriptFile => (new Guid("42B5FAAE-6536-11D2-AE5A-0000F87571E3"), "Scripts"),
            GpoFileRole.Preferences => PreferenceExtension(file.PreferenceType),
            _ => (new Guid("35378EAC-683F-11D2-A89A-00C04FBBCFA2"), "Registry"),
        };
    }

    // ---- Writing ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="backup"/> into <paramref name="folder"/> (created when missing) and merges its entry into
    /// <c>manifest.xml</c>. A failure removes the <c>{BackupId}</c> folder again; nothing is written anywhere else.
    /// </summary>
    public static async Task WriteAsync(string folder, GpoBackup backup, string productVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(backup);
        var root = Path.GetFullPath(folder);
        System.IO.Directory.CreateDirectory(root);
        var backupDir = Path.Combine(root, Braced(backup.BackupId));
        if (System.IO.Directory.Exists(backupDir))
        {
            throw new GpoBackupException("A backup with this id already exists in the folder.");
        }

        System.IO.Directory.CreateDirectory(backupDir);
        try
        {
            var gpoDir = Path.Combine(backupDir, SysvolFolder, GpoFolder);
            System.IO.Directory.CreateDirectory(gpoDir);
            foreach (var relative in backup.Folders)
            {
                System.IO.Directory.CreateDirectory(Confine(gpoDir, relative));
            }

            foreach (var (relative, content) in backup.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Confine(gpoDir, relative);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(Path.Combine(backupDir, BackupFile), BackupXml(backup), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(backupDir, InfoFile), InfoXml(backup), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(backupDir, SidecarFile), Sidecar(backup, productVersion), cancellationToken).ConfigureAwait(false);
            await MergeManifestAsync(root, backup, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                System.IO.Directory.Delete(backupDir, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The partial backup stays; the operator sees the failure.
            }

            throw;
        }
    }

    private static string BackupXml(GpoBackup backup)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        var sb = new StringBuilder();
        using (var xml = XmlWriter.Create(new StringWriterUtf8(sb), settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("GroupPolicyBackupScheme", BackupNamespace);
            xml.WriteAttributeString("xmlns", "bkp", null, BackupNamespace);
            xml.WriteAttributeString("version", BackupNamespace, "2.0");
            xml.WriteAttributeString("type", BackupNamespace, "GroupPolicyBackupTemplate");
            xml.WriteStartElement("GroupPolicyObject", BackupNamespace);
            xml.WriteStartElement("SecurityGroups", BackupNamespace);
            foreach (var trustee in backup.Trustees)
            {
                xml.WriteStartElement("Group", BackupNamespace);
                xml.WriteAttributeString("Source", BackupNamespace, "FromDACL");
                Cdata(xml, "Sid", trustee.Sid);
                if (trustee.Name is { Length: > 0 } name)
                {
                    Cdata(xml, "SamAccountName", name);
                }

                if (trustee.Type is { Length: > 0 } type)
                {
                    Cdata(xml, "Type", type);
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteElementString("FilePaths", BackupNamespace, string.Empty);
            xml.WriteStartElement("GroupPolicyCoreSettings", BackupNamespace);
            Cdata(xml, "ID", Braced(backup.GpoId));
            Cdata(xml, "Domain", backup.Domain);
            xml.WriteElementString("SecurityDescriptor", BackupNamespace,
                backup.SecurityDescriptor is null ? string.Empty : string.Join(' ', backup.SecurityDescriptor.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))));
            Cdata(xml, "DisplayName", backup.DisplayName);
            Cdata(xml, "Options", backup.Options.ToString(CultureInfo.InvariantCulture));
            Cdata(xml, "UserVersionNumber", backup.Version.User.ToString(CultureInfo.InvariantCulture));
            Cdata(xml, "MachineVersionNumber", backup.Version.Computer.ToString(CultureInfo.InvariantCulture));
            Cdata(xml, "MachineExtensionGuids", backup.MachineExtensionNames ?? string.Empty);
            Cdata(xml, "UserExtensionGuids", backup.UserExtensionNames ?? string.Empty);
            Cdata(xml, "WMIFilter", backup.WmiFilterLink ?? string.Empty);
            xml.WriteEndElement();
            foreach (var extension in backup.Files.Keys.GroupBy(ExtensionOf).OrderBy(g => g.Key.Name, StringComparer.Ordinal))
            {
                xml.WriteStartElement("GroupPolicyExtension", BackupNamespace);
                xml.WriteAttributeString("ID", BackupNamespace, Braced(extension.Key.Id));
                xml.WriteAttributeString("DescName", BackupNamespace, extension.Key.Name);
                var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in extension.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    xml.WriteStartElement("FSObjectFile", BackupNamespace);
                    xml.WriteAttributeString("Path", BackupNamespace, TokenPath(path));
                    xml.WriteAttributeString("Location", BackupNamespace, SysvolFolder + "\\" + GpoFolder + "\\" + path.Replace('/', '\\'));
                    xml.WriteEndElement();
                    var segments = path.Split('/');
                    for (var i = 2; i < segments.Length; i++)
                    {
                        folders.Add(string.Join('/', segments[..i]));
                    }
                }

                foreach (var folder in folders)
                {
                    xml.WriteStartElement("FSObjectDir", BackupNamespace);
                    xml.WriteAttributeString("Path", BackupNamespace, TokenPath(folder));
                    xml.WriteAttributeString("Location", BackupNamespace, SysvolFolder + "\\" + GpoFolder + "\\" + folder.Replace('/', '\\'));
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return sb.ToString();
    }

    private static string InfoXml(GpoBackup backup)
    {
        var sb = new StringBuilder();
        using (var xml = XmlWriter.Create(new StringWriterUtf8(sb), new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            xml.WriteStartDocument();
            WriteInstance(xml, backup);
            xml.WriteEndDocument();
        }

        return sb.ToString();
    }

    private static void WriteInstance(XmlWriter xml, GpoBackup backup)
    {
        xml.WriteStartElement("BackupInst", ManifestNamespace);
        Cdata(xml, "GPOGuid", Braced(backup.GpoId), ManifestNamespace);
        Cdata(xml, "GPODomain", backup.Domain, ManifestNamespace);
        Cdata(xml, "GPODomainGuid", backup.DomainGuid is { } domainGuid ? Braced(domainGuid) : string.Empty, ManifestNamespace);
        Cdata(xml, "GPODomainController", backup.DomainController ?? string.Empty, ManifestNamespace);
        Cdata(xml, "BackupTime", backup.BackupTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ManifestNamespace);
        Cdata(xml, "ID", Braced(backup.BackupId), ManifestNamespace);
        Cdata(xml, "Comment", backup.Comment, ManifestNamespace);
        Cdata(xml, "GPODisplayName", backup.DisplayName, ManifestNamespace);
        xml.WriteEndElement();
    }

    /// <summary>Adds the backup to <c>manifest.xml</c>, keeping the entries of other backups (GPMC's or ours).</summary>
    private static async Task MergeManifestAsync(string root, GpoBackup backup, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ManifestFile);
        var entries = new List<string>();
        if (File.Exists(path))
        {
            var existing = LoadXml(path);
            foreach (var instance in existing.DocumentElement?.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "BackupInst") ?? [])
            {
                entries.Add(instance.OuterXml);
            }

            // Hidden like GPMC's: clear the attribute while rewriting.
            File.SetAttributes(path, FileAttributes.Normal);
        }

        var sb = new StringBuilder();
        using (var xml = XmlWriter.Create(new StringWriterUtf8(sb), new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("Backups", ManifestNamespace);
            xml.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            xml.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            xml.WriteAttributeString("xmlns", "mfst", null, ManifestNamespace);
            xml.WriteAttributeString("version", ManifestNamespace, "1.0");
            foreach (var entry in entries)
            {
                xml.WriteRaw(entry);
            }

            WriteInstance(xml, backup);
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.Hidden);
        }
    }

    private static byte[] Sidecar(GpoBackup backup, string productVersion)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("admng", productVersion);
            json.WriteNumber("flags", backup.Options);
            json.WriteStartObject("files");
            foreach (var (path, content) in backup.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                json.WriteString(path, Convert.ToHexStringLower(SHA256.HashData(content)));
            }

            json.WriteEndObject();
            json.WriteStartArray("folders");
            foreach (var folder in backup.Folders.Order(StringComparer.OrdinalIgnoreCase))
            {
                json.WriteStringValue(folder);
            }

            json.WriteEndArray();
            json.WriteStartArray("links");
            foreach (var link in backup.Links)
            {
                json.WriteStartObject();
                json.WriteString("container", link.ContainerDn);
                json.WriteNumber("order", link.Order);
                json.WriteNumber("options", link.Options);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            if (backup.WmiFilter is { } filter)
            {
                json.WriteStartObject("wmiFilter");
                json.WriteString("id", filter.Id);
                json.WriteString("name", filter.Name);
                json.WriteString("description", filter.Description);
                json.WriteString("queries", filter.RawQueries ?? WmiFilterQueries.Format(filter.Queries));
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    // ---- Reading ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The backups of a folder: the entries of <c>manifest.xml</c> whose folder exists, else every <c>{GUID}</c> subfolder
    /// with a readable <c>bkupInfo.xml</c>. Newest first.
    /// </summary>
    public static IReadOnlyList<GpoBackupEntry> List(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.GetFullPath(folder);
        var result = new Dictionary<Guid, GpoBackupEntry>();
        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }

        var manifest = Path.Combine(root, ManifestFile);
        if (File.Exists(manifest))
        {
            try
            {
                foreach (var instance in LoadXml(manifest).DocumentElement?.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "BackupInst") ?? [])
                {
                    if (ToEntry(instance) is { } entry && System.IO.Directory.Exists(Path.Combine(root, Braced(entry.BackupId))))
                    {
                        result[entry.BackupId] = entry;
                    }
                }
            }
            catch (GpoBackupException)
            {
                // A broken manifest: fall back to the folders.
            }
        }

        foreach (var directory in System.IO.Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "B", out var id) || result.ContainsKey(id) || !File.Exists(Path.Combine(directory, InfoFile)))
            {
                continue;
            }

            try
            {
                if (ToEntry(LoadXml(Path.Combine(directory, InfoFile)).DocumentElement) is { } entry && entry.BackupId == id)
                {
                    result[id] = entry;
                }
            }
            catch (GpoBackupException)
            {
                // Not a backup we can read.
            }
        }

        return [.. result.Values.OrderByDescending(e => e.BackupTime)];
    }

    /// <summary>Reads one backup completely into memory (see the class remarks for the checks).</summary>
    /// <exception cref="GpoBackupException">The backup is missing, malformed or fails a check.</exception>
    /// <exception cref="GpoTooLargeException">The backup exceeds the in-memory limits.</exception>
    public static async Task<GpoBackup> ReadAsync(string folder, Guid backupId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.GetFullPath(folder);
        var backupDir = Path.Combine(root, Braced(backupId));
        var backupXml = Path.Combine(backupDir, BackupFile);
        if (!File.Exists(backupXml))
        {
            throw new GpoBackupException("The backup has no Backup.xml.");
        }

        var document = LoadXml(backupXml);
        var core = Descendant(document.DocumentElement, "GroupPolicyCoreSettings") ?? throw new GpoBackupException("Backup.xml has no core settings.");
        if (!Guid.TryParse(Text(core, "ID"), out var gpoId))
        {
            throw new GpoBackupException("Backup.xml has no GPO id.");
        }

        var info = File.Exists(Path.Combine(backupDir, InfoFile)) ? ToEntry(LoadXml(Path.Combine(backupDir, InfoFile)).DocumentElement) : null;
        var gpoDir = Path.Combine(backupDir, SysvolFolder, GpoFolder);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        long total = 0;
        if (System.IO.Directory.Exists(gpoDir))
        {
            var count = 0;
            foreach (var entry in new DirectoryInfo(gpoDir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > GpoTransaction.MaxEntriesPerGpo)
                {
                    throw new GpoTooLargeException();
                }

                var relative = Path.GetRelativePath(gpoDir, entry.FullName);
                if (!SysvolPaths.TryNormalize(relative, out var normalized) || Path.IsPathRooted(relative))
                {
                    throw new GpoBackupException("The backup holds a path outside its GPO folder.");
                }

                if (entry is DirectoryInfo)
                {
                    folders.Add(normalized);
                    continue;
                }

                if (string.Equals(normalized, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase) || GpoTransaction.IsTempName(normalized))
                {
                    continue;
                }

                var length = ((FileInfo)entry).Length;
                total += length;
                if (length > GpoTransaction.MaxFileBytes || total > GpoTransaction.MaxPlanBytes)
                {
                    throw new GpoTooLargeException();
                }

                files[normalized] = await File.ReadAllBytesAsync(Confine(gpoDir, normalized), cancellationToken).ConfigureAwait(false);
            }
        }

        var backup = new GpoBackup
        {
            BackupId = backupId,
            GpoId = gpoId,
            DisplayName = Text(core, "DisplayName") ?? info?.DisplayName ?? Braced(gpoId),
            Domain = Text(core, "Domain") ?? info?.Domain ?? string.Empty,
            DomainController = null,
            BackupTime = info?.BackupTime ?? File.GetLastWriteTimeUtc(backupXml),
            Comment = info?.Comment ?? string.Empty,
            Options = int.TryParse(Text(core, "Options"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var options) ? options & 3 : 0,
            Version = new GpoVersion(
                Math.Clamp(int.TryParse(Text(core, "UserVersionNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var user) ? user : 0, 0, GpoVersion.MaxHalf),
                Math.Clamp(int.TryParse(Text(core, "MachineVersionNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var machine) ? machine : 0, 0, GpoVersion.MaxHalf)),
            MachineExtensionNames = Blank(Text(core, "MachineExtensionGuids")),
            UserExtensionNames = Blank(Text(core, "UserExtensionGuids")),
            WmiFilterLink = Blank(Text(core, "WMIFilter")),
            SecurityDescriptor = ParseHex(Text(core, "SecurityDescriptor")),
            Trustees = Trustees(document.DocumentElement),
            Files = files,
            Folders = folders,
        };
        return await WithSidecarAsync(backupDir, backup, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GpoBackup> WithSidecarAsync(string backupDir, GpoBackup backup, CancellationToken cancellationToken)
    {
        var path = Path.Combine(backupDir, SidecarFile);
        if (!File.Exists(path))
        {
            return backup;
        }

        if (new FileInfo(path).Length > MaxXmlBytes)
        {
            throw new GpoTooLargeException();
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new GpoBackupException("admng.json cannot be read.", ex);
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.TryGetProperty("files", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
            {
                var expected = hashes.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                if (expected.Count != backup.Files.Count
                    || backup.Files.Any(f => !expected.TryGetValue(f.Key, out var hash)
                        || !string.Equals(hash, Convert.ToHexStringLower(SHA256.HashData(f.Value)), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new GpoBackupException("A file of the backup does not match its recorded SHA-256 (missing, extra or changed).");
                }
            }

            var links = new List<BackupLink>();
            if (root.TryGetProperty("links", out var linkArray) && linkArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in linkArray.EnumerateArray())
                {
                    if (link.TryGetProperty("container", out var container) && container.GetString() is { Length: > 0 } dn
                        && Ldap.DistinguishedName.TryParse(dn, out _))
                    {
                        links.Add(new BackupLink(dn,
                            link.TryGetProperty("order", out var order) && order.TryGetInt32(out var o) ? o : 1,
                            link.TryGetProperty("options", out var opts) && opts.TryGetInt32(out var p) ? p & 3 : 0));
                    }
                }
            }

            WmiFilterInfo? filter = null;
            if (root.TryGetProperty("wmiFilter", out var wmi) && wmi.ValueKind == JsonValueKind.Object
                && wmi.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } filterId)
            {
                var raw = wmi.TryGetProperty("queries", out var q) ? q.GetString() : null;
                filter = new WmiFilterInfo
                {
                    Dn = string.Empty,
                    Id = filterId,
                    Name = wmi.TryGetProperty("name", out var n) ? n.GetString() ?? filterId : filterId,
                    Description = wmi.TryGetProperty("description", out var d) ? d.GetString() : null,
                    RawQueries = raw,
                    Queries = WmiFilterQueries.Parse(raw) ?? [],
                };
            }

            return backup with { Links = links, WmiFilter = filter, HasSidecar = true };
        }
    }

    // ---- XML helpers -------------------------------------------------------------------------------------------------

    /// <summary>Untrusted XML: no DTD, no resolver, a size limit.</summary>
    private static XmlDocument LoadXml(string path)
    {
        if (new FileInfo(path).Length > MaxXmlBytes)
        {
            throw new GpoBackupException("An XML file of the backup is too large.");
        }

        try
        {
            var document = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlBytes,
                IgnoreComments = true,
            });
            document.Load(reader);
            return document;
        }
        catch (XmlException ex)
        {
            throw new GpoBackupException("An XML file of the backup cannot be read.", ex);
        }
    }

    private static GpoBackupEntry? ToEntry(XmlElement? instance)
    {
        if (instance is null || !Guid.TryParse(Text(instance, "ID"), out var backupId) || !Guid.TryParse(Text(instance, "GPOGuid"), out var gpoId))
        {
            return null;
        }

        var time = DateTimeOffset.TryParse(Text(instance, "BackupTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : DateTimeOffset.MinValue;
        return new GpoBackupEntry(backupId, gpoId, Text(instance, "GPODisplayName") ?? Braced(gpoId), Text(instance, "GPODomain") ?? string.Empty, time,
            Text(instance, "Comment") ?? string.Empty);
    }

    private static IReadOnlyList<BackupTrustee> Trustees(XmlElement? root) =>
        [.. (Descendant(root, "SecurityGroups")?.ChildNodes.OfType<XmlElement>() ?? [])
            .Where(e => e.LocalName == "Group" && Text(e, "Sid") is { Length: > 0 })
            .Select(e => new BackupTrustee(Text(e, "Sid")!, Text(e, "SamAccountName"), Text(e, "Type")))];

    private static XmlElement? Descendant(XmlElement? element, string localName) =>
        element?.GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(e => e.LocalName == localName);

    private static string? Text(XmlElement element, string localName) =>
        element.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == localName)?.InnerText.Trim();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static byte[]? ParseHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(string.Concat(value.Where(c => !char.IsWhiteSpace(c))));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Cdata(XmlWriter xml, string name, string value, string ns = BackupNamespace)
    {
        xml.WriteStartElement(name, ns);
        if (value.Length > 0)
        {
            xml.WriteCData(value.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal));
        }

        xml.WriteEndElement();
    }

    /// <summary>A path below the GPO folder of a backup: normalized and confined to it.</summary>
    private static string Confine(string gpoDir, string relative)
    {
        if (!SysvolPaths.TryNormalize(relative, out var normalized))
        {
            throw new GpoBackupException("The backup holds an invalid path.");
        }

        var full = Path.GetFullPath(Path.Combine(gpoDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.GetFullPath(gpoDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new GpoBackupException("The backup holds a path outside its GPO folder.");
        }

        return full;
    }

    /// <summary><c>%GPO_MACH_FSPATH%\…</c>, <c>%GPO_USER_FSPATH%\…</c> or <c>%GPO_FSPATH%\…</c> as GPMC writes them.</summary>
    private static string TokenPath(string path)
    {
        var segments = path.Split('/');
        var (token, rest) = segments[0].ToUpperInvariant() switch
        {
            "MACHINE" => ("%GPO_MACH_FSPATH%", segments[1..]),
            "USER" => ("%GPO_USER_FSPATH%", segments[1..]),
            _ => ("%GPO_FSPATH%", segments),
        };
        return rest.Length == 0 ? token : token + "\\" + string.Join('\\', rest);
    }

    private static (Guid, string) PreferenceExtension(string? type) => (type ?? string.Empty).ToUpperInvariant() switch
    {
        "GROUPS" => (new Guid("17D89FEC-5C44-4972-B12D-241CAEF74509"), "Local Users and Groups"),
        "DRIVES" => (new Guid("5794DAFD-BE60-433F-88A2-1A31939AC01F"), "Drive Maps"),
        "REGISTRY" => (new Guid("B087BE9D-ED37-454F-AF9C-04291E351182"), "Group Policy Registry"),
        "SCHEDULEDTASKS" => (new Guid("AADCED64-746C-4633-A97C-D61349046527"), "Scheduled Tasks"),
        "SERVICES" => (new Guid("91FBB303-0CD5-4055-BF42-E512A681B325"), "Services"),
        "FILES" => (new Guid("7150F9BF-48AD-4DA4-A49C-29EF4A8369BA"), "Files"),
        "FOLDERS" => (new Guid("6232C319-91AC-4931-9385-E70C2B099F0E"), "Folders"),
        "SHORTCUTS" => (new Guid("C418DD9D-0D14-4EFB-8FBF-CFE535C8FAC7"), "Shortcuts"),
        "PRINTERS" => (new Guid("BC75B1ED-5833-4858-9BB8-CBF0B166DF9D"), "Printers"),
        "ENVIRONMENTVARIABLES" => (new Guid("0E28E245-9368-4853-AD84-6DA3BA35BB75"), "Environment"),
        "INIFILES" => (new Guid("74EE6C03-5363-4554-B161-627540339CAB"), "Ini Files"),
        "NETWORKSHARES" => (new Guid("6A4C88C6-C502-4F74-8F60-2CB23EDC24E2"), "Network Shares"),
        "DATASOURCES" => (new Guid("728EE579-943C-4519-9EF7-AB56765798ED"), "Data Sources"),
        "DEVICES" => (new Guid("1A6364EB-776B-4120-ADE1-B63A406A76B5"), "Devices"),
        "POWEROPTIONS" => (new Guid("E62688F0-25FD-4C90-BFF5-F508B9D2E31F"), "Power Options"),
        "REGIONALOPTIONS" => (new Guid("E5094040-C46C-4115-B030-04FB2E545B00"), "Regional Options"),
        "STARTMENUTASKBAR" => (new Guid("E4F48E54-F38D-4884-BFB9-D4D2E5729C18"), "Start Menu"),
        "INTERNETSETTINGS" => (new Guid("E47248BA-94CC-49C4-BBB5-9EB7F05183D0"), "Internet Settings"),
        "NETWORKOPTIONS" => (new Guid("3A0DBA37-F8B2-4356-83DE-3E90BD5C261F"), "Network Options"),
        "FOLDEROPTIONS" => (new Guid("A3F3E39B-5D83-4940-B954-28315B82F0A8"), "Folder Options"),
        _ => (new Guid("35378EAC-683F-11D2-A89A-00C04FBBCFA2"), "Registry"),
    };

    private static string Braced(Guid id) => SysvolPaths.FolderName(id);

    /// <summary>A StringWriter that reports UTF-8 (the XML declaration says utf-8).</summary>
    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
