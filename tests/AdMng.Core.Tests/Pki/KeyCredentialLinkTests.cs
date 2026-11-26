using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.Pki;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <c>msDS-KeyCredentialLink</c> (docs/dev/p-certificate-services.md 2.4): DN-Binary values holding a KEYCREDENTIALLINK_BLOB
/// version 0x0200 (MS-ADTS 2.2.20) parse into the metadata only – key id, device id, source, usage, times, the KeyHash check –
/// never the key material; malformed values stay as rows.
/// </summary>
public sealed class KeyCredentialLinkTests
{
    private const string Owner = "CN=Jan Kowalski,OU=Księgowość,OU=Kontoso,DC=contoso,DC=test";

    private static readonly byte[] KeyId = [0x4A, 0x61, 0x6E, 0x2D, 0x4B, 0x6C, 0x75, 0x63, 0x7A];
    private static readonly Guid DeviceId = new("6d0f7c2e-8a41-4b4b-9c3e-1f2a3b4c5d6e");
    private static readonly DateTimeOffset Created = new(2026, 5, 4, 7, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastLogon = new(2026, 10, 12, 6, 50, 0, TimeSpan.Zero);

    [Fact]
    public void TheSampleBlob_ParsesIntoItsMetadata()
    {
        var link = Assert.Single(KeyCredentialLink.Parse([PkiSamples.KeyCredentialLinkValue(Owner, KeyId, DeviceId, Created, LastLogon)]));

        Assert.False(link.IsMalformed);
        Assert.Equal(Convert.ToBase64String(KeyId), link.KeyId);
        Assert.Equal(DeviceId, link.DeviceId);
        Assert.Equal(KeyCredentialSource.ActiveDirectory, link.Source);
        Assert.Equal(KeyCredentialUsage.Ngc, link.Usage);
        Assert.Equal(Created, link.Created);
        Assert.Equal(LastLogon, link.LastLogon);
        Assert.Equal(TimeSpan.Zero, link.Created!.Value.Offset);
        Assert.Equal(32, link.KeyMaterialLength);
        Assert.True(link.IsKeyHashValid);
        Assert.Equal(Owner, link.OwnerDn);
    }

    [Fact]
    public void WithoutALastLogon_ThatTimeIsNull()
    {
        var link = Assert.Single(KeyCredentialLink.Parse([PkiSamples.KeyCredentialLinkValue(Owner, KeyId, DeviceId, Created)]));

        Assert.Equal(Created, link.Created);
        Assert.Null(link.LastLogon);
    }

    [Theory]
    [InlineData((byte)0x00, KeyCredentialUsage.AdminKey)]
    [InlineData((byte)0x01, KeyCredentialUsage.Ngc)]
    [InlineData((byte)0x02, KeyCredentialUsage.Stk)]
    [InlineData((byte)0x03, KeyCredentialUsage.BitLockerRecovery)]
    [InlineData((byte)0x04, KeyCredentialUsage.Other)]
    [InlineData((byte)0x07, KeyCredentialUsage.Fido)]
    [InlineData((byte)0x08, KeyCredentialUsage.Fek)]
    [InlineData((byte)0x09, KeyCredentialUsage.Dpapi)]
    [InlineData((byte)0x05, KeyCredentialUsage.Unknown)]
    [InlineData((byte)0xFF, KeyCredentialUsage.Unknown)]
    public void KeyUsage_FollowsMsAdts(byte usage, KeyCredentialUsage expected)
    {
        var link = Assert.Single(KeyCredentialLink.Parse([PkiSamples.KeyCredentialLinkValue(Owner, KeyId, DeviceId, Created, usage: usage)]));

        Assert.Equal(expected, link.Usage);
    }

    [Theory]
    [InlineData((byte)0x00, KeyCredentialSource.ActiveDirectory)]
    [InlineData((byte)0x01, KeyCredentialSource.EntraId)]
    [InlineData((byte)0x02, KeyCredentialSource.Unknown)]
    public void KeySource_FollowsMsAdts(byte source, KeyCredentialSource expected)
    {
        var link = Assert.Single(KeyCredentialLink.Parse([Value(Blob(source: source))]));

        Assert.Equal(expected, link.Source);
    }

    [Fact]
    public void EntraIdKeys_StoreTheirTimesAsDateTimeBinary()
    {
        // DSInternals: KEY_SOURCE_AZURE timestamps are DateTime.ToBinary, KEY_SOURCE_AD ones FILETIME.
        var created = new DateTime(2026, 3, 2, 9, 30, 0, DateTimeKind.Utc);
        var link = Assert.Single(KeyCredentialLink.Parse([Value(Blob(source: 0x01, created: created.ToBinary()))]));

        Assert.Equal(KeyCredentialSource.EntraId, link.Source);
        Assert.Equal(new DateTimeOffset(created), link.Created);
    }

    [Fact]
    public void AChangedEntry_FailsTheKeyHash_ButStillParses()
    {
        var blob = Blob();
        blob[^1] ^= 0x01;
        var link = Assert.Single(KeyCredentialLink.Parse([Value(blob)]));

        Assert.False(link.IsMalformed);
        Assert.False(link.IsKeyHashValid);
    }

    [Fact]
    public void WithoutAKeyHash_TheCheckIsNull_AndUnknownEntriesAreSkipped()
    {
        var link = Assert.Single(KeyCredentialLink.Parse([Value(Blob(withHash: false, extra: [(0x0A, [1, 2, 3]), (0x07, [0x01, 0x00])]))]));

        Assert.False(link.IsMalformed);
        Assert.Null(link.IsKeyHashValid);
        Assert.Equal(DeviceId, link.DeviceId);
    }

    public static TheoryData<string, string> MalformedValues()
    {
        var good = Blob();
        var truncated = good[..^3];
        var version1 = (byte[])good.Clone();
        version1[1] = 0x01;
        var shortDevice = Blob(deviceId: new byte[15]);
        var shortTime = Blob(createdBytes: [1, 2, 3, 4]);
        var emptyKeyId = Blob(keyId: []);
        return new TheoryData<string, string>
        {
            { "not DN-Binary", "Żółć-Gęślą-Jaźń" },
            { "no DN", "B:8:00020000:" },
            { "odd hex length", "B:7:0002000:" + Owner },
            { "length mismatch", "B:10:00020000:" + Owner },
            { "bare version", Value([0x00, 0x02]) },
            { "truncated entry", Value(truncated) },
            { "version 0x0100", Value(version1) },
            { "device id of 15 bytes", Value(shortDevice) },
            { "creation time of 4 bytes", Value(shortTime) },
            { "empty key id", Value(emptyKeyId) },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedValues))]
    public void MalformedValues_StayAsRows_WithoutFields(string reason, string value)
    {
        var link = Assert.Single(KeyCredentialLink.Parse([value]));

        Assert.True(link.IsMalformed, reason);
        Assert.Null(link.KeyId);
        Assert.Null(link.DeviceId);
        Assert.Null(link.Created);
        Assert.Null(link.LastLogon);
        Assert.Equal(KeyCredentialSource.Unknown, link.Source);
        Assert.Equal(KeyCredentialUsage.Unknown, link.Usage);
    }

    [Fact]
    public void EveryValue_GivesOneRow_InOrder()
    {
        var links = KeyCredentialLink.Parse(
        [
            PkiSamples.KeyCredentialLinkValue(Owner, KeyId, DeviceId, Created),
            "Żółć-Gęślą-Jaźń",
            Value(Blob(source: 0x01, usage: 0x07)),
        ]);

        Assert.Equal(3, links.Count);
        Assert.False(links[0].IsMalformed);
        Assert.True(links[1].IsMalformed);
        Assert.Equal(KeyCredentialUsage.Fido, links[2].Usage);
        Assert.Empty(KeyCredentialLink.Parse([]));
    }

    [Fact]
    public void LowerCaseHex_AsSomeDirectoriesReturnIt_Parses()
    {
        var value = PkiSamples.KeyCredentialLinkValue(Owner, KeyId, DeviceId, Created);
        var parts = value.Split(':', 4);
        var lower = $"B:{parts[1]}:{parts[2].ToLowerInvariant()}:{parts[3]}";

        Assert.False(Assert.Single(KeyCredentialLink.Parse([lower])).IsMalformed);
    }

    [Fact]
    public void TheKeyMaterial_IsNeverExposed_AndToStringIsValueFree()
    {
        Assert.DoesNotContain(typeof(KeyCredentialLink).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => p.PropertyType == typeof(byte[]) || p.PropertyType == typeof(ReadOnlyMemory<byte>));

        var link = Assert.Single(KeyCredentialLink.Parse([PkiSamples.KeyCredentialLinkValue(Owner, KeyId, DeviceId, Created)]));
        var text = link.ToString();
        Assert.Equal("KeyCredentialLink(ActiveDirectory Ngc)", text);
        Assert.DoesNotContain(link.KeyId!, text, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("KeyCredentialLink(Unknown Unknown, malformed)", KeyCredentialLink.Parse(["x"])[0].ToString());
    }

    private static string Value(byte[] blob)
    {
        var hex = Convert.ToHexString(blob);
        return $"B:{hex.Length}:{hex}:{Owner}";
    }

    /// <summary>A KEYCREDENTIALLINK_BLOB v2 with the given variations (KeyID, KeyHash over the rest, entries 0x03–0x09).</summary>
    private static byte[] Blob(
        byte source = 0x00,
        byte usage = 0x01,
        long? created = null,
        byte[]? createdBytes = null,
        byte[]? deviceId = null,
        byte[]? keyId = null,
        bool withHash = true,
        IReadOnlyList<(byte Id, byte[] Value)>? extra = null)
    {
        var tail = new List<byte>();
        Entry(tail, 0x03, SHA256.HashData(Encoding.UTF8.GetBytes("klucz testowy – Łódź")));
        Entry(tail, 0x04, [usage]);
        Entry(tail, 0x05, [source]);
        Entry(tail, 0x06, deviceId ?? DeviceId.ToByteArray());
        foreach (var (id, value) in extra ?? [])
        {
            Entry(tail, id, value);
        }

        Entry(tail, 0x09, createdBytes ?? BitConverter.GetBytes(created ?? Created.ToFileTime()));
        var blob = new List<byte> { 0x00, 0x02, 0x00, 0x00 };
        Entry(blob, 0x01, keyId ?? KeyId);
        if (withHash)
        {
            Entry(blob, 0x02, SHA256.HashData([.. tail]));
        }

        blob.AddRange(tail);
        return [.. blob];

        static void Entry(List<byte> target, byte id, byte[] value)
        {
            target.AddRange(BitConverter.GetBytes((ushort)value.Length));
            target.Add(id);
            target.AddRange(value);
        }
    }
}
