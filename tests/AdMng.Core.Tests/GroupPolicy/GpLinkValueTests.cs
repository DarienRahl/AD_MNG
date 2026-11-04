using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy;

public class GpLinkValueTests
{
    private const string Domain = "DC=contoso,DC=test";
    private static readonly Guid A = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = new("33333333-3333-3333-3333-333333333333");

    private static string Entry(Guid id, int options) =>
        $"[LDAP://cn={{{id.ToString().ToUpperInvariant()}}},cn=policies,cn=system,DC=contoso,DC=test;{options}]";

    [Fact]
    public void Parse_GivesLinkOrderOneToTheLastEntry()
    {
        var value = GpLinkValue.Parse(Entry(A, 0) + Entry(B, 2), Domain);

        Assert.Equal([(A, 2), (B, 1)], value.Links.Select(l => (l.GpoId, l.Order)));
        Assert.True(value.Find(B)!.Enforced);
        Assert.True(value.Find(A)!.Enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Parse_EmptyOrASingleSpace_IsNoLinks(string? raw)
    {
        var value = GpLinkValue.Parse(raw, Domain);

        Assert.Empty(value.Links);
        Assert.Null(value.ToAttributeValue());
    }

    [Fact]
    public void Insert_WithoutOrder_GivesTheLowestPrecedence_AsGpmc()
    {
        var value = GpLinkValue.Parse(Entry(A, 0), Domain).Insert(B, 0);

        Assert.Equal(Entry(B, 0) + Entry(A, 0), value.ToAttributeValue());
        Assert.Equal(2, value.Find(B)!.Order);
    }

    [Fact]
    public void Insert_AtOrderOne_PutsTheLinkLast()
    {
        var value = GpLinkValue.Parse(Entry(A, 0), Domain).Insert(B, GroupPolicyLink.EnforcedOption, order: 1);

        Assert.Equal(Entry(A, 0) + Entry(B, 2), value.ToAttributeValue());
    }

    [Fact]
    public void Insert_WritesTheGpmcForm()
    {
        var value = GpLinkValue.Parse(null, Domain).Insert(C, 0);

        Assert.Equal("[LDAP://cn={33333333-3333-3333-3333-333333333333},cn=policies,cn=system,DC=contoso,DC=test;0]", value.ToAttributeValue());
        Assert.Throws<InvalidOperationException>(() => value.Insert(C, 0));
    }

    [Fact]
    public void Remove_LastLink_ClearsTheAttribute()
    {
        var value = GpLinkValue.Parse(Entry(A, 1), Domain).Remove(A);

        Assert.Null(value.ToAttributeValue());
    }

    [Fact]
    public void MoveTo_ReordersAndKeepsOptions()
    {
        var value = GpLinkValue.Parse(Entry(A, 0) + Entry(B, 1) + Entry(C, 2), Domain);

        var moved = value.MoveTo(A, 1);

        Assert.Equal(Entry(B, 1) + Entry(C, 2) + Entry(A, 0), moved.ToAttributeValue());
        Assert.Equal(Entry(C, 2) + Entry(A, 0) + Entry(B, 1), value.MoveTo(C, 3).ToAttributeValue());
    }

    [Fact]
    public void SetOptions_TogglesEnforcedAndEnabled_KeepingOtherBits()
    {
        var value = GpLinkValue.Parse(Entry(A, 4), Domain);

        var changed = value.SetOptions(A, enforced: true, enabled: false);

        Assert.Equal(Entry(A, 7), changed.ToAttributeValue());
        Assert.Equal(Entry(A, 4), changed.SetOptions(A, enforced: false, enabled: true).ToAttributeValue());
    }

    [Fact]
    public void ForeignAndMalformedEntries_AreKeptByteForByte()
    {
        const string foreign = "[LDAP://cn={44444444-4444-4444-4444-444444444444},cn=policies,cn=system,DC=fabrikam,DC=test;0]";
        const string malformed = "[LDAP://nonsense]";
        var raw = foreign + Entry(A, 0) + malformed + "  garbage ";

        var value = GpLinkValue.Parse(raw, Domain);
        var changed = value.Insert(B, 0).Remove(B).SetOptions(A, enforced: true);

        Assert.Equal([A], value.Links.Select(l => l.GpoId));
        Assert.Equal(4, value.Count);
        Assert.Equal(foreign + Entry(A, 2) + malformed + "  garbage ", changed.ToAttributeValue());
        Assert.Equal(3, value.Find(A)!.Order);
    }

    [Fact]
    public void Parse_AcceptsUpperCaseAndDomainDnSpelling()
    {
        var raw = $"[LDAP://CN={{{A.ToString().ToUpperInvariant()}}},CN=Policies,CN=System,DC=Contoso,DC=Test;0]";

        Assert.True(GpLinkValue.Parse(raw, Domain).Contains(A));
    }
}
