using System.Globalization;
using System.Xml;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Narrow reader of 4771 / 4776 event XML (<c>EventRecord.ToXml()</c>): EventID and the EventData fields
/// TargetUserName, IpAddress, Workstation and Status. DTDs are prohibited; malformed records are skipped.
/// </summary>
internal static class FailedLogonEventReader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    /// <summary>Reads one record; false for other event ids or malformed XML.</summary>
    public static bool TryRead(string xml, out FailedLogon? failure)
    {
        failure = null;
        if (string.IsNullOrEmpty(xml))
        {
            return false;
        }

        try
        {
            return TryReadCore(xml, out failure);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool TryReadCore(string xml, out FailedLogon? failure)
    {
        failure = null;
        int? eventId = null;
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = XmlReader.Create(new StringReader(xml), Settings))
        {
            // ReadElementContentAsString already moves to the next node – only call Read() otherwise, or every other
            // sibling <Data> element would be skipped.
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "EventID")
                {
                    eventId = int.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Data" && reader.GetAttribute("Name") is { } name)
                {
                    data[name] = reader.ReadElementContentAsString();
                }
                else
                {
                    reader.Read();
                }
            }
        }

        return FromData(eventId, name => data.GetValueOrDefault(name), out failure);
    }

    /// <summary>Reads a stored record (collector mode, stage B design 7.8); false for other event ids.</summary>
    public static bool TryRead(AdMng.Watchdog.Core.Model.SecurityEvent record, out FailedLogon? failure)
    {
        ArgumentNullException.ThrowIfNull(record);
        return FromData(record.EventCode, record.Get, out failure);
    }

    private static bool FromData(int? eventId, Func<string, string?> get, out FailedLogon? failure)
    {
        failure = null;
        if (eventId is not (4771 or 4776) || get("TargetUserName") is not { } user || NormalizeUser(user) is not { } account
            || FailedLogon.ParseStatus(get("Status")) is not { } status)
        {
            return false;
        }

        failure = eventId == 4771
            ? new FailedLogon(4771, account, NormalizeAddress(get("IpAddress")), null, status)
            : new FailedLogon(4776, account, null, NormalizeName(get("Workstation")), status);
        return true;
    }

    /// <summary>"CONTOSO\jan" / "jan@contoso.test" / "Jan" → "jan"; "-" and empty → null.</summary>
    private static string? NormalizeUser(string value)
    {
        var name = value.Trim();
        var slash = name.LastIndexOf('\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var at = name.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            name = name[..at];
        }

        return name.Length == 0 || name == "-" ? null : name.ToLowerInvariant();
    }

    private static string? NormalizeAddress(string? value)
    {
        var address = value?.Trim();
        if (string.IsNullOrEmpty(address) || address == "-" || address == "::1" || address == "127.0.0.1")
        {
            return null;
        }

        return address.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? address[7..] : address;
    }

    private static string? NormalizeName(string? value)
    {
        var name = value?.Trim().TrimStart('\\');
        return string.IsNullOrEmpty(name) || name == "-" ? null : name;
    }
}
