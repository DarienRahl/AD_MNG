using AdMng.Core.Schema;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Fixtures.Metadata;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class ReplicationMetadataDecoderTests
{
    private static readonly SchemaCatalog Schema = new(
    [
        new AttributeDefinition { LdapName = "objectClass", AttributeId = "2.5.4.0" },
        new AttributeDefinition { LdapName = "title", AttributeId = "2.5.4.12" },
        new AttributeDefinition { LdapName = "department", AttributeId = "1.2.840.113556.1.2.141" },
        new AttributeDefinition { LdapName = "sAMAccountName", AttributeId = "1.2.840.113556.1.4.221" },
        new AttributeDefinition { LdapName = "userAccountControl", AttributeId = "1.2.840.113556.1.4.8" },
    ], []);

    [Fact]
    public void DecodesTheCapturedLabBlob()
    {
        Assert.True(ReplicationMetadataDecoder.TryDecode(ReplicationMetadataFixtures.LabUserBlob(), out var entries));

        Assert.Equal(ReplicationMetadataFixtures.LabUserEntryCount, entries.Count);
        Assert.All(entries, e => Assert.Equal(ReplicationMetadataFixtures.LabDcInvocationId, e.InvocationId));
        Assert.All(entries, e => Assert.Equal(ReplicationMetadataFixtures.LabUserCreated, e.OriginatingTime));
        var objectClass = Assert.Single(entries, e => e.AttrTyp == 0);
        Assert.Equal(1, objectClass.Version);
        Assert.Equal(ReplicationMetadataFixtures.LabUserUsnCreated, objectClass.UsnLocal);
        var department = Assert.Single(entries, e => e.AttrTyp == ReplicationMetadataFixtures.DepartmentAttrTyp);
        Assert.Equal(2, department.Version);
        Assert.Equal(ReplicationMetadataFixtures.LabUserUsnChanged, department.UsnLocal);
        var title = Assert.Single(entries, e => e.AttrTyp == ReplicationMetadataFixtures.TitleAttrTyp);
        Assert.Equal(1, title.Version);
        Assert.Equal(ReplicationMetadataFixtures.LabUserUsnChanged, title.UsnOriginating);
    }

    [Fact]
    public void ResolvesNamesThroughTheSchema_IgnoresUnknownAttrTyps()
    {
        Assert.True(ReplicationMetadataDecoder.TryDecode(ReplicationMetadataFixtures.LabUserBlob(), new AttrTypResolver(Schema), out var metadata));

        Assert.Equal(2, metadata.Get("department")!.Version);
        Assert.Equal(ReplicationMetadataFixtures.LabUserUsnCreated, metadata.Get("sAMAccountName")!.UsnLocal);
        Assert.Equal(4, metadata.Entries.Count(e => e.Attribute is "objectClass" or "title" or "department" or "sAMAccountName"));
        Assert.Null(metadata.Get("whenCreated"));
    }

    [Fact]
    public void WrongLength_IsRejected()
    {
        var blob = ReplicationMetadataFixtures.LabUserBlob();

        Assert.False(ReplicationMetadataDecoder.TryDecode(blob.AsSpan(0, blob.Length - 1), out _));
        Assert.False(ReplicationMetadataDecoder.TryDecode([.. blob, 0], out _));
        Assert.False(ReplicationMetadataDecoder.TryDecode(blob.AsSpan(0, 10), out _));
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        var blob = ReplicationMetadataFixtures.LabUserBlob();
        blob[0] = 2;

        Assert.False(ReplicationMetadataDecoder.TryDecode(blob, new AttrTypResolver(Schema), out var metadata));
        Assert.True(metadata.IsEmpty);
    }

    [Fact]
    public void EmptyBlob_WithZeroEntries_IsValid()
    {
        var blob = new byte[16];
        blob[0] = 1;

        Assert.True(ReplicationMetadataDecoder.TryDecode(blob, out var entries));
        Assert.Empty(entries);
    }
}
