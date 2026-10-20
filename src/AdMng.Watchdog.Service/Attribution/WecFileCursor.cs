using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>
/// The position of the WEF tailer in one OpenWEC client directory (stage B design 7.5), kept in <c>eventlog_cursors</c>:
/// <c>source = "wec:&lt;client dir&gt;"</c>, <c>last_record_id</c> = <see cref="Offset"/>, <c>bookmark_xml</c> =
/// <c>&lt;WecFileCursor Device Inode Offset Size FirstLineSha256 Name Attempts AttemptOffset PoisonEnd/&gt;</c>. The file is
/// identified by <c>(device, inode, first-line SHA-256)</c> – the hash protects against an inode reused after a deletion.
/// </summary>
/// <param name="Device"><c>st_dev</c> of the file.</param>
/// <param name="Inode"><c>st_ino</c> of the file.</param>
/// <param name="FirstLineSha256">SHA-256 (hex, lower case) of the first line including its line feed.</param>
/// <param name="Name">The file name when the cursor was written (diagnostics; a rotation renames the file).</param>
/// <param name="Offset">Bytes consumed and committed.</param>
/// <param name="Size">The file size when the cursor was committed (<see cref="Offset"/> = <see cref="Size"/>: the file was
/// consumed completely at that time).</param>
public sealed record WecFileCursor(ulong Device, ulong Inode, string FirstLineSha256, string Name, long Offset, long Size)
{
    /// <summary>Poison input (7.5): attempts recorded before a batch at <see cref="AttemptOffset"/> was processed.</summary>
    public int Attempts { get; init; }

    /// <summary>The offset the <see cref="Attempts"/> belong to.</summary>
    public long? AttemptOffset { get; init; }

    /// <summary>End of a batch that failed <see cref="WecFileIngestionJob.MaxAttempts"/> times and is processed line by
    /// line up to here; null otherwise.</summary>
    public long? PoisonEnd { get; init; }

    /// <summary>The <c>eventlog_cursors.source</c> of a client directory.</summary>
    public static string SourceOf(string clientDirectory) => "wec:" + clientDirectory;

    /// <summary>True when the cursor's file is <paramref name="file"/> (device, inode and first-line hash).</summary>
    public bool Matches(WecFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Device == Device && file.Inode == Inode
            && string.Equals(file.FirstLineSha256, FirstLineSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The attempts recorded at <paramref name="offset"/> (0 for another offset).</summary>
    public int AttemptsAt(long offset) => AttemptOffset == offset ? Attempts : 0;

    /// <summary>The store row of this cursor.</summary>
    public EventLogCursor ToEventLogCursor(string clientDirectory, DateTimeOffset now) => new()
    {
        Source = SourceOf(clientDirectory),
        LastRecordId = Offset,
        BookmarkXml = ToXml(),
        UpdatedAt = now,
    };

    /// <summary>The <c>bookmark_xml</c> form.</summary>
    public string ToXml()
    {
        var element = new XElement("WecFileCursor",
            new XAttribute("Device", Device.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("Inode", Inode.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("Offset", Offset.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("Size", Size.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("FirstLineSha256", FirstLineSha256),
            new XAttribute("Name", Name));
        if (Attempts > 0 && AttemptOffset is { } attemptOffset)
        {
            element.Add(new XAttribute("Attempts", Attempts.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("AttemptOffset", attemptOffset.ToString(CultureInfo.InvariantCulture)));
        }

        if (PoisonEnd is { } poisonEnd)
        {
            element.Add(new XAttribute("PoisonEnd", poisonEnd.ToString(CultureInfo.InvariantCulture)));
        }

        return element.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The cursor of a store row; null when there is none or it is of another kind.</summary>
    public static WecFileCursor? Parse(EventLogCursor? cursor)
    {
        if (cursor?.BookmarkXml is not { Length: > 0 } xml)
        {
            return null;
        }

        try
        {
            var element = XElement.Parse(xml);
            if (element.Name.LocalName != "WecFileCursor"
                || !ulong.TryParse((string?)element.Attribute("Device"), NumberStyles.None, CultureInfo.InvariantCulture, out var device)
                || !ulong.TryParse((string?)element.Attribute("Inode"), NumberStyles.None, CultureInfo.InvariantCulture, out var inode)
                || !long.TryParse((string?)element.Attribute("Offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                || (string?)element.Attribute("FirstLineSha256") is not { Length: 64 } hash)
            {
                return null;
            }

            _ = long.TryParse((string?)element.Attribute("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size);
            return new WecFileCursor(device, inode, hash, (string?)element.Attribute("Name") ?? string.Empty, offset, size)
            {
                Attempts = int.TryParse((string?)element.Attribute("Attempts"), NumberStyles.None, CultureInfo.InvariantCulture, out var attempts) ? attempts : 0,
                AttemptOffset = long.TryParse((string?)element.Attribute("AttemptOffset"), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : null,
                PoisonEnd = long.TryParse((string?)element.Attribute("PoisonEnd"), NumberStyles.None, CultureInfo.InvariantCulture, out var end) ? end : null,
            };
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>A cursor at <paramref name="offset"/> of <paramref name="file"/> (no attempts).</summary>
    public static WecFileCursor At(WecFileInfo file, long offset)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new WecFileCursor(file.Device, file.Inode, file.FirstLineSha256 ?? string.Empty, file.Name, offset, file.Size);
    }
}
