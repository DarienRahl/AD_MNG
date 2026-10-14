using AdMng.Testing;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class SecurityDescriptorSummaryTests
{
    private const string DomainSid = FakeDirectorySession.DefaultDomainSid;
    private static readonly Guid GetChangesAll = new("1131f6ad-9c07-11d1-f79f-00c04fc2dcd2");

    private static readonly FakeAce Admins = FakeAce.FullControl($"{DomainSid}-512");
    private static readonly FakeAce Builtin = FakeAce.FullControl("S-1-5-32-544");
    private static readonly FakeAce Dcsync = FakeAce.ExtendedRight($"{DomainSid}-1105", GetChangesAll);

    [Fact]
    public void Parses_OwnerAndAces()
    {
        var sd = FakeSecurityDescriptor.Build($"{DomainSid}-512", $"{DomainSid}-513", Admins, Dcsync);

        Assert.True(SecurityDescriptorSummary.TryParse(sd, out var summary));
        Assert.Equal($"{DomainSid}-512", summary!.Owner);
        Assert.Equal(2, summary.Aces.Count);
        var dcsync = summary.Aces[1];
        Assert.Equal((byte)0x05, dcsync.Type);
        Assert.Equal(0x100, dcsync.Mask);
        Assert.Equal(GetChangesAll, dcsync.ObjectType);
        Assert.Equal($"{DomainSid}-1105", dcsync.Sid);
    }

    [Fact]
    public void ReorderedAces_HaveADifferentHashButTheSameComparisonSet()
    {
        var a = FakeSecurityDescriptor.Build($"{DomainSid}-512", null, Admins, Builtin, Dcsync);
        var b = FakeSecurityDescriptor.Build($"{DomainSid}-512", null, Dcsync, Admins, Builtin, Builtin);

        Assert.True(SecurityDescriptorSummary.TryParse(a, out var first));
        Assert.True(SecurityDescriptorSummary.TryParse(b, out var second));
        Assert.NotEqual(first!.Sha256, second!.Sha256);
        Assert.True(SecurityDescriptorSummary.ComparisonSet(first.ToValues()).SetEquals(SecurityDescriptorSummary.ComparisonSet(second.ToValues())));
    }

    [Fact]
    public void AddedAce_ChangesTheComparisonSet_AndIsDescribedByName()
    {
        var before = Summary(FakeSecurityDescriptor.Build($"{DomainSid}-512", null, Admins));
        var after = Summary(FakeSecurityDescriptor.Build($"{DomainSid}-512", null, Admins, Dcsync));

        var added = SecurityDescriptorSummary.ComparisonSet(after).Except(SecurityDescriptorSummary.ComparisonSet(before)).Single();

        Assert.Equal($"allow DS-Replication-Get-Changes-All {DomainSid}-1105", SecurityDescriptorSummary.Describe(added));
        Assert.Equal($"owner {DomainSid}-512", SecurityDescriptorSummary.Describe($"owner:{DomainSid}-512"));
        Assert.Equal("allow full control S-1-5-32-544", SecurityDescriptorSummary.Describe(new SecurityDescriptorAce { Type = 0, Flags = 0, Mask = 0xF01FF, Sid = "S-1-5-32-544" }));
    }

    [Fact]
    public void OwnerChange_ChangesTheComparisonSet()
    {
        var before = Summary(FakeSecurityDescriptor.Build($"{DomainSid}-512", null, Admins));
        var after = Summary(FakeSecurityDescriptor.Build($"{DomainSid}-1105", null, Admins));

        Assert.False(SecurityDescriptorSummary.ComparisonSet(before).SetEquals(SecurityDescriptorSummary.ComparisonSet(after)));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 4, 0x80, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Malformed_IsRejected(byte[] descriptor) => Assert.False(SecurityDescriptorSummary.TryParse(descriptor, out _));

    [Fact]
    public void AceSummaryValue_RoundTrips()
    {
        var ace = new SecurityDescriptorAce { Type = 5, Flags = 0x12, Mask = 0x20, Sid = "S-1-5-11", ObjectType = Guid.NewGuid(), InheritedObjectType = Guid.NewGuid() };

        var parsed = SecurityDescriptorAce.FromSummaryValue(ace.ToSummaryValue());

        Assert.Equal(ace, parsed);
        Assert.Null(SecurityDescriptorAce.FromSummaryValue("owner:S-1-5-11"));
    }

    private static IReadOnlyList<string> Summary(byte[] descriptor) =>
        SecurityDescriptorSummary.TryParse(descriptor, out var summary) ? summary!.ToValues() : throw new InvalidOperationException();
}
