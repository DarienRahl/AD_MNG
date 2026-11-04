using System.Buffers.Binary;
using AdMng.Core.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.Security;

public class DaclEditorTests
{
    private const string DomainSid = FakeDirectorySession.DefaultDomainSid;
    private static readonly string DomainAdmins = $"{DomainSid}-512";
    private static readonly string Helpdesk = $"{DomainSid}-1201";
    private static readonly string Owner = $"{DomainSid}-1105";

    private static readonly FakeAce AdminsFull = FakeAce.FullControl(DomainAdmins);
    private static readonly FakeAce DenyEveryoneDelete = new(FakeAce.AccessDenied, 0, 0x10000, "S-1-1-0");
    private static readonly FakeAce InheritedRead = new(FakeAce.AccessAllowed, SecurityDescriptorAce.InheritedFlag | SecurityDescriptorAce.ContainerInheritFlag, 0x20094, "S-1-5-11");

    private static readonly AceSpec ResetPassword = AceSpec.Allow(Helpdesk, 0x100, ExtendedRightIds.ResetPassword, "CONTOSO\\Pomoc techniczna");
    private static readonly AceSpec WriteRestrictions = AceSpec.Allow(Helpdesk, 0x30, ExtendedRightIds.AccountRestrictions);

    [Fact]
    public void Writer_RoundTripsWithTheParser()
    {
        var aces = new[] { ResetPassword, WriteRestrictions, new AceSpec { Qualifier = AceQualifier.Deny, Mask = 0x10000, TrusteeSid = "S-1-1-0" } };
        var descriptor = SecurityDescriptorWriter.Build(
            SecurityDescriptorWriter.DaclProtected,
            Owner,
            DomainAdmins,
            [.. aces.Select(a => (ReadOnlyMemory<byte>)SecurityDescriptorWriter.EncodeAce(a))]);

        Assert.True(SecurityDescriptorSummary.TryParse(descriptor, out var summary));
        Assert.Equal(Owner, summary!.Owner);
        Assert.Equal(3, summary.Aces.Count);
        Assert.Equal(aces.Select(a => a.ToAce().ToSummaryValue()), summary.Aces.Select(a => a.ToSummaryValue()));
        Assert.True(DaclEditor.IsProtected(descriptor));
        Assert.Equal(SecurityDescriptorWriter.AclRevisionDs, descriptor[BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(16))]);
    }

    [Fact]
    public void Apply_AddsMissingAces_AndWritesOnlyTheDacl()
    {
        var descriptor = FakeSecurityDescriptor.Build(Owner, DomainAdmins, AdminsFull, InheritedRead);

        var result = DaclEditor.Apply(descriptor, new AclChange([ResetPassword, WriteRestrictions], []));

        Assert.True(result.Changed);
        Assert.Equal(2, result.Applied.Added.Count);
        Assert.True(SecurityDescriptorSummary.TryParse(result.Descriptor, out var summary));
        Assert.Null(summary!.Owner);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.Descriptor.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.Descriptor.AsSpan(12)));
        Assert.Equal(
            [$"allow full control {DomainAdmins}", $"allow User-Force-Change-Password {Helpdesk}", $"allow write property User-Account-Restrictions {Helpdesk}", "allow 0x00020094 S-1-5-11"],
            summary.Aces.Select(SecurityDescriptorSummary.Describe));
        var control = BinaryPrimitives.ReadUInt16LittleEndian(result.Descriptor.AsSpan(2));
        Assert.Equal(SecurityDescriptorWriter.SelfRelative | SecurityDescriptorWriter.DaclPresent, control);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, AdminsFull);
        var change = new AclChange([ResetPassword, WriteRestrictions], []);

        var first = DaclEditor.Apply(descriptor, change);
        var second = DaclEditor.Apply(first.Descriptor, change);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Descriptor, second.Descriptor);
    }

    [Fact]
    public void Apply_AnAceCoveredByABroaderMaskIsANoOp()
    {
        var broader = new FakeAce(FakeAce.AccessAllowedObject, 0, 0x130, Helpdesk, ExtendedRightIds.AccountRestrictions);
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, AdminsFull, broader);

        var result = DaclEditor.Apply(descriptor, new AclChange([WriteRestrictions], []));

        Assert.False(result.Changed);
        Assert.Empty(result.Applied.Added);
    }

    [Fact]
    public void Apply_RemovesEveryEqualExplicitAce_AndNeverAnInheritedOne()
    {
        var inheritedReset = new FakeAce(FakeAce.AccessAllowedObject, SecurityDescriptorAce.InheritedFlag, 0x100, Helpdesk, ExtendedRightIds.ResetPassword);
        var explicitReset = new FakeAce(FakeAce.AccessAllowedObject, 0, 0x100, Helpdesk, ExtendedRightIds.ResetPassword);
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, explicitReset, AdminsFull, explicitReset, inheritedReset);

        var result = DaclEditor.Apply(descriptor, new AclChange([], [ResetPassword]));

        Assert.True(result.Changed);
        Assert.Single(result.Applied.Removed);
        Assert.True(SecurityDescriptorSummary.TryParse(result.Descriptor, out var summary));
        Assert.Equal(2, summary!.Aces.Count);
        Assert.True(summary.Aces[1].IsInherited);
    }

    [Fact]
    public void Apply_RemovingAnAbsentAceIsANoOp()
    {
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, AdminsFull);

        var result = DaclEditor.Apply(descriptor, new AclChange([], [ResetPassword]));

        Assert.False(result.Changed);
        Assert.Empty(result.Applied.Removed);
    }

    [Fact]
    public void Apply_PutsTheDaclInCanonicalOrder()
    {
        // Non-canonical input: allow, inherited, deny.
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, AdminsFull, InheritedRead, DenyEveryoneDelete);
        var deny = new AceSpec { Qualifier = AceQualifier.Deny, Mask = 0x40000, TrusteeSid = Helpdesk };

        var result = DaclEditor.Apply(descriptor, new AclChange([ResetPassword, deny], []));

        Assert.True(SecurityDescriptorSummary.TryParse(result.Descriptor, out var summary));
        Assert.Equal(
            [
                "deny delete S-1-1-0",
                $"deny write DACL {Helpdesk}",
                $"allow full control {DomainAdmins}",
                $"allow User-Force-Change-Password {Helpdesk}",
                "allow 0x00020094 S-1-5-11",
            ],
            summary!.Aces.Select(SecurityDescriptorSummary.Describe));
        Assert.True(summary.Aces[^1].IsInherited);
    }

    [Fact]
    public void Apply_KeepsTheProtectionBit()
    {
        var unprotected = FakeSecurityDescriptor.Build(Owner, null, AdminsFull);
        var protectedDescriptor = SecurityDescriptorWriter.Build(
            SecurityDescriptorWriter.DaclProtected, Owner, null, [SecurityDescriptorWriter.EncodeAce(0, 0, 0xF01FF, DomainAdmins, null, null)]);

        Assert.False(DaclEditor.IsProtected(DaclEditor.Apply(unprotected, new AclChange([ResetPassword], [])).Descriptor));
        Assert.True(DaclEditor.IsProtected(DaclEditor.Apply(protectedDescriptor, new AclChange([ResetPassword], [])).Descriptor));
    }

    [Fact]
    public void Inverse_RestoresTheAceSet()
    {
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, AdminsFull, InheritedRead);
        var applied = DaclEditor.Apply(descriptor, new AclChange([ResetPassword, WriteRestrictions], []));

        var undone = DaclEditor.Apply(applied.Descriptor, applied.Applied.Inverse());

        Assert.True(SecurityDescriptorSummary.TryParse(descriptor, out var before));
        Assert.True(SecurityDescriptorSummary.TryParse(undone.Descriptor, out var after));
        Assert.Equal(before!.Aces.Select(a => a.ToSummaryValue()), after!.Aces.Select(a => a.ToSummaryValue()));
        Assert.Equal(2, undone.Applied.Removed.Count);
    }

    [Fact]
    public void Apply_KeepsUnknownAceTypesByteForByte()
    {
        // A callback allow ACE (0x09) cannot be expressed as a spec: it stays as it is.
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, AdminsFull);
        var callback = SecurityDescriptorWriter.EncodeAce(0x09, 0, 0x20094, Helpdesk, null, null);
        var withCallback = SecurityDescriptorWriter.Build(0, Owner, null, [SecurityDescriptorWriter.EncodeAce(0, 0, 0xF01FF, DomainAdmins, null, null), callback]);

        var result = DaclEditor.Apply(withCallback, new AclChange([ResetPassword], []));

        Assert.True(SecurityDescriptorSummary.TryParse(result.Descriptor, out var summary));
        Assert.Contains(summary!.Aces, a => a.Type == 0x09 && a.Sid == Helpdesk && a.Mask == 0x20094);
        Assert.True(DaclEditor.TryReadExplicit(descriptor, out var explicitAces));
        Assert.Single(explicitAces);
    }

    [Fact]
    public void Apply_RefusesADescriptorWithoutDacl_AndMalformedOnes()
    {
        var withoutDacl = SecurityDescriptorWriter.Build(0, Owner, null, null);

        Assert.Throws<FormatException>(() => DaclEditor.Apply(withoutDacl, new AclChange([ResetPassword], [])));
        Assert.Throws<FormatException>(() => DaclEditor.Apply(new byte[] { 1, 0, 4, 0x80 }, new AclChange([ResetPassword], [])));
        Assert.False(DaclEditor.TryReadExplicit(withoutDacl, out _));
    }

    [Theory]
    [InlineData("not-a-sid", 0x100, 0)]
    [InlineData("S-1-5-21-1-2-3-1105", 0, 0)]
    [InlineData("S-1-5-21-1-2-3-1105", 0x100, SecurityDescriptorAce.InheritedFlag)]
    public void Apply_RefusesAcesThatCannotBeWritten(string sid, int mask, byte flags)
    {
        var descriptor = FakeSecurityDescriptor.Build(Owner, null, AdminsFull);
        var ace = new AceSpec { Qualifier = AceQualifier.Allow, Mask = mask, TrusteeSid = sid, Flags = flags };

        Assert.Throws<ArgumentException>(() => DaclEditor.Apply(descriptor, new AclChange([ace], [])));
    }

    [Fact]
    public void AclChange_ToStringHasCountsOnly()
    {
        var change = new AclChange([ResetPassword], []);

        Assert.Equal("AclChange { Added = 1, Removed = 0 }", change.ToString());
        Assert.DoesNotContain(Helpdesk, change.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AceSpec_FromAceSkipsInheritedAndOtherTypes()
    {
        Assert.Null(AceSpec.FromAce(new SecurityDescriptorAce { Type = 0, Flags = SecurityDescriptorAce.InheritedFlag, Mask = 1, Sid = Helpdesk }));
        Assert.Null(AceSpec.FromAce(new SecurityDescriptorAce { Type = 0x09, Flags = 0, Mask = 1, Sid = Helpdesk }));
        var spec = AceSpec.FromAce(new SecurityDescriptorAce { Type = 0x05, Flags = 0, Mask = 0x100, Sid = Helpdesk, ObjectType = ExtendedRightIds.ResetPassword });
        Assert.NotNull(spec);
        Assert.True(spec!.SameAce(ResetPassword));
    }
}
