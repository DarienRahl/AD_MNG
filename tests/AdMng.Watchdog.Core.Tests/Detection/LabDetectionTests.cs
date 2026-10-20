using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>
/// The detection algorithms' assumptions checked against the Samba lab (skipped without <c>ADMNG_TEST_LDAP_*</c>):
/// USN semantics of the change searches, the binary replPropertyMetaData layout and the tombstone / linked-value
/// behaviour the Show Deleted pass relies on. Every test works in its own throwaway OU and deletes it.
/// </summary>
public class LabDetectionTests
{
    private const string Polish = "Zażółć gęślą jaźń – Łódź";

    [Fact]
    public async Task ChangeSearch_FindsWhatChangedAfterTheWatermarkReadFirst()
    {
        var lab = Lab();
        var token = TestContext.Current.CancellationToken;
        var before = await HighestUsnAsync(lab, token);
        var ou = NewOu("usn");
        try
        {
            await lab.ModifyAsync(AddOu(ou), token);
            var created = await UsnChangedAsync(lab, ou, token);
            var after = await HighestUsnAsync(lab, token);

            Assert.InRange(created, before + 1, after);
            Assert.Contains(await ChangedContainersAsync(lab, before + 1, token), dn => DistinguishedName.AreEqual(dn, ou));
            Assert.DoesNotContain(await ChangedContainersAsync(lab, after + 1, token), dn => DistinguishedName.AreEqual(dn, ou));

            await lab.ModifyAsync(Replace(ou, Attr.Description, "Łódź"), token);
            var changed = await UsnChangedAsync(lab, ou, token);

            Assert.True(changed > created);
            Assert.True(await HighestUsnAsync(lab, token) >= changed);
            Assert.Contains(await ChangedContainersAsync(lab, created + 1, token), dn => DistinguishedName.AreEqual(dn, ou));
        }
        finally
        {
            await lab.DeleteAsync(ou, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReplPropertyMetaData_DecodesVersionLocalUsnAndInvocationId()
    {
        var lab = Lab();
        var token = TestContext.Current.CancellationToken;
        var ou = NewOu("meta");
        try
        {
            await lab.ModifyAsync(AddOu(ou), token);
            await lab.ModifyAsync(Replace(ou, Attr.Description, "druga wersja"), token);
            var entry = Assert.Single(await lab.SearchAsync(ou, "base", "(objectClass=*)", [WatchdogAttributes.ReplPropertyMetaData, Attr.UsnChanged], false, token));
            var dsServiceName = await lab.ReadAsync(string.Empty, WatchdogAttributes.DsServiceName, token);
            var ntds = Assert.Single(await lab.SearchAsync(dsServiceName!, "base", "(objectClass=*)", [WatchdogAttributes.InvocationId], false, token));

            Assert.True(ReplicationMetadataDecoder.TryDecode(entry.Attributes[WatchdogAttributes.ReplPropertyMetaData][0], out var entries));
            var description = Assert.Single(entries, e => e.AttrTyp == AttrTypResolver.AttidFromOid("2.5.4.13"));
            Assert.Equal(2, description.Version);
            Assert.Equal(long.Parse(Encoding.UTF8.GetString(entry.Attributes[Attr.UsnChanged][0]), CultureInfo.InvariantCulture), description.UsnLocal);
            Assert.Equal(new Guid(ntds.Attributes[WatchdogAttributes.InvocationId][0]), description.InvocationId);
            Assert.InRange(description.OriginatingTime, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        }
        finally
        {
            await lab.DeleteAsync(ou, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeletedMember_LeavesTheGroupUnbumpedAndATombstoneWithItsLastKnownParent()
    {
        var lab = Lab();
        var token = TestContext.Current.CancellationToken;
        var ou = NewOu("del");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var group = DistinguishedName.Parse(ou).Child("CN", "GG_Watchdog_" + suffix).ToString();
        var user = DistinguishedName.Parse(ou).Child("CN", "Użytkownik " + suffix).ToString();
        try
        {
            await lab.ModifyAsync(AddOu(ou), token);
            await lab.ModifyAsync(Add(group, ("objectClass", "group"), ("sAMAccountName", "wdg" + suffix), ("groupType", "-2147483646")), token);
            await lab.ModifyAsync(Add(user, ("objectClass", "user"), ("sAMAccountName", "wdu" + suffix), ("userAccountControl", "514")), token);
            await lab.ModifyAsync(AddValue(group, Attr.Member, user), token);
            var userGuid = new Guid(Assert.Single(await lab.SearchAsync(user, "base", "(objectClass=*)", [Attr.ObjectGuid], false, token)).Attributes[Attr.ObjectGuid][0]);
            var groupUsn = await UsnChangedAsync(lab, group, token);

            await lab.DeleteAsync(user, token);

            Assert.Equal(groupUsn, await UsnChangedAsync(lab, group, token));
            Assert.Null(await lab.ReadAsync(group, Attr.Member, token));
            var filter = LdapFilter.And(LdapFilter.Equal(Attr.IsDeleted, "TRUE"), LdapFilter.EqualGuid(Attr.ObjectGuid, userGuid));
            var tombstone = Assert.Single(await lab.SearchAsync($"CN=Deleted Objects,{LabLdap.DomainDn}", "one", filter,
                [Attr.ObjectClass, "lastKnownParent", Attr.UsnChanged], showDeleted: true, token));
            Assert.True(DistinguishedName.AreEqual(Encoding.UTF8.GetString(tombstone.Attributes["lastKnownParent"][0]), ou));
            Assert.Contains(tombstone.Attributes[Attr.ObjectClass], v => Encoding.UTF8.GetString(v) == "user");
            Assert.True(long.Parse(Encoding.UTF8.GetString(tombstone.Attributes[Attr.UsnChanged][0]), CultureInfo.InvariantCulture) > groupUsn);
        }
        finally
        {
            await lab.DeleteAsync(user, CancellationToken.None);
            await lab.DeleteAsync(group, CancellationToken.None);
            await lab.DeleteAsync(ou, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Tombstone_ItsMangledNameGivesTheOriginalRdn()
    {
        var lab = Lab();
        var token = TestContext.Current.CancellationToken;
        var ou = NewOu("rdn");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "Żaneta Ślęzak " + suffix;
        var user = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        try
        {
            await lab.ModifyAsync(AddOu(ou), token);
            await lab.ModifyAsync(Add(user, ("objectClass", "user"), ("sAMAccountName", "wdr" + suffix), ("userAccountControl", "514")), token);
            var userGuid = new Guid(Assert.Single(await lab.SearchAsync(user, "base", "(objectClass=*)", [Attr.ObjectGuid], false, token)).Attributes[Attr.ObjectGuid][0]);

            await lab.DeleteAsync(user, token);

            var filter = LdapFilter.And(LdapFilter.Equal(Attr.IsDeleted, "TRUE"), LdapFilter.EqualGuid(Attr.ObjectGuid, userGuid));
            var tombstone = Assert.Single(await lab.SearchAsync($"CN=Deleted Objects,{LabLdap.DomainDn}", "one", filter,
                [Attr.MsDsLastKnownRdn, Attr.IsRecycled], showDeleted: true, token));
            // Without the Recycle Bin (the Samba lab) there is no msDS-LastKnownRDN: the scanner falls back to the DN.
            Assert.Equal(name, DeletedObjectsScanner.UnmangledName(tombstone.Dn));
            if (tombstone.Attributes.TryGetValue(Attr.MsDsLastKnownRdn, out var lastKnownRdn))
            {
                Assert.Equal(name, Encoding.UTF8.GetString(lastKnownRdn[0]));
            }
        }
        finally
        {
            await lab.DeleteAsync(user, CancellationToken.None);
            await lab.DeleteAsync(ou, CancellationToken.None);
        }
    }

    private static LabLdap Lab()
    {
        var lab = LabLdap.FromEnvironment();
        Assert.SkipWhen(lab is null, "Lab DC not configured (ADMNG_TEST_LDAP_SERVER / _USER / _PASSWORD) or ldapsearch missing.");
        return lab!;
    }

    private static string NewOu(string purpose) =>
        DistinguishedName.Parse(LabLdap.DomainDn).Child("OU", $"WD-Detect-{purpose}-{Guid.NewGuid().ToString("N")[..8]}").ToString();

    private static async Task<long> HighestUsnAsync(LabLdap lab, CancellationToken token) =>
        long.Parse((await lab.ReadAsync(string.Empty, WatchdogAttributes.HighestCommittedUsn, token))!, CultureInfo.InvariantCulture);

    private static async Task<long> UsnChangedAsync(LabLdap lab, string dn, CancellationToken token) =>
        long.Parse((await lab.ReadAsync(dn, Attr.UsnChanged, token))!, CultureInfo.InvariantCulture);

    private static async Task<List<string>> ChangedContainersAsync(LabLdap lab, long searchFrom, CancellationToken token) =>
        [.. (await lab.SearchAsync(LabLdap.DomainDn, "sub", DetectionFilters.ChangedContainers(searchFrom), [Attr.ObjectGuid], false, token)).Select(e => e.Dn)];

    private static string AddOu(string dn) => Add(dn, ("objectClass", "organizationalUnit"), (Attr.Description, Polish));

    private static string Add(string dn, params (string Name, string Value)[] attributes) =>
        $"dn:: {Base64(dn)}\nchangetype: add\n" + string.Concat(attributes.Select(a => $"{a.Name}:: {Base64(a.Value)}\n"));

    private static string Replace(string dn, string attribute, string value) =>
        $"dn:: {Base64(dn)}\nchangetype: modify\nreplace: {attribute}\n{attribute}:: {Base64(value)}\n-\n";

    private static string AddValue(string dn, string attribute, string value) =>
        $"dn:: {Base64(dn)}\nchangetype: modify\nadd: {attribute}\n{attribute}:: {Base64(value)}\n-\n";

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
