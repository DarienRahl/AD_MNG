using System.IO.Compression;
using System.Xml;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// A zip package (OOXML, OpenDocument) read in memory with the import limits (design note 3.1): an entry above
/// <see cref="MaxEntryBytes"/> uncompressed, or above 1 MiB with a compression ratio above <see cref="MaxCompressionRatio"/>,
/// is refused – the declared sizes are checked before reading and the bytes actually inflated while reading (a forged
/// header cannot hide a zip bomb); all entries read together stay under <see cref="MaxTotalBytes"/>. XML is read without
/// DTDs or external resolution.
/// </summary>
internal sealed class SafeZip : IDisposable
{
    /// <summary>Largest uncompressed entry (200 MiB).</summary>
    public const long MaxEntryBytes = 200L * 1024 * 1024;

    /// <summary>Largest ratio of uncompressed to compressed size of an entry above <see cref="RatioCheckBytes"/>.</summary>
    public const long MaxCompressionRatio = 100;

    /// <summary>Entries up to this size are not ratio-checked (small XML parts compress very well and are harmless).</summary>
    public const long RatioCheckBytes = 1024 * 1024;

    /// <summary>Largest sum of the entries read from one package (512 MiB).</summary>
    public const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>Most entries of a package.</summary>
    public const int MaxEntries = 20_000;

    private readonly ZipArchive _archive;
    private long _total;

    private SafeZip(ZipArchive archive) => _archive = archive;

    public static SafeZip Open(ReadOnlySpan<byte> content)
    {
        var stream = new MemoryStream(content.ToArray(), writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            _ = archive.Entries.Count;
        }
        catch (InvalidDataException ex)
        {
            stream.Dispose();
            throw new InvalidDataException("The file is not a valid zip package.", ex);
        }

        if (archive.Entries.Count > MaxEntries)
        {
            archive.Dispose();
            throw new InvalidDataException("The package has too many entries.");
        }

        return new SafeZip(archive);
    }

    /// <summary>The entry with the name (case-insensitive, as OPC part names are), or null.</summary>
    public ZipArchiveEntry? Entry(string name)
    {
        var normalized = name.TrimStart('/');
        return _archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The names of the entries in the package.</summary>
    public IEnumerable<string> Names => _archive.Entries.Select(e => e.FullName);

    /// <summary>A reader of the entry's bytes within the limits.</summary>
    public Stream OpenEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException("A part of the package is too large.");
        }

        if (entry.Length > RatioCheckBytes && (entry.CompressedLength <= 0 || entry.Length / entry.CompressedLength > MaxCompressionRatio))
        {
            throw new InvalidDataException("A part of the package is compressed suspiciously well (zip bomb).");
        }

        var limit = Math.Min(MaxEntryBytes, Math.Max(RatioCheckBytes, entry.CompressedLength * MaxCompressionRatio));
        limit = Math.Min(limit, entry.Length);
        return new LimitedStream(entry.Open(), limit, this);
    }

    /// <summary>An XML reader of the entry: no DTD, no resolver, comments and processing instructions skipped.</summary>
    public XmlReader OpenXml(ZipArchiveEntry entry) => XmlReader.Create(OpenEntry(entry), XmlSettings());

    /// <summary>The XML of an entry with the given name, or null when there is none.</summary>
    public XmlReader? OpenXml(string name) => Entry(name) is { } entry ? OpenXml(entry) : null;

    public void Dispose() => _archive.Dispose();

    internal static XmlReaderSettings XmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        MaxCharactersFromEntities = 1024,
        CloseInput = true,
    };

    private void Count(int bytes)
    {
        _total += bytes;
        if (_total > MaxTotalBytes)
        {
            throw new InvalidDataException("The package is too large when unpacked.");
        }
    }

    /// <summary>Throws once more than <c>limit</c> bytes come out of the inflater (the declared size may be forged).</summary>
    private sealed class LimitedStream(Stream inner, long limit, SafeZip owner) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _read += read;
            if (_read > limit)
            {
                throw new InvalidDataException("A part of the package is larger than declared (zip bomb).");
            }

            owner.Count(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
