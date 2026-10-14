using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Testing;

/// <summary>The AdMng Watchdog emulation of the fake (watchdog specification 4.3), mirroring the Samba lab findings.</summary>
public class FakeDirectoryWatchdogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FakeDirectoryBuilder Builder, SampleCompany Company, ManualTimeProvider Clock) Create()
    {
        var clock = new ManualTimeProvider(Start);
        var builder = new FakeDirectoryBuilder(timeProvider: clock);
        var company = builder.AddSampleCompany();
        builder.AddWatchdogInfrastructure();
        return (builder, company, clock);
    }

    private static async Task<List<DirectoryObject>> SearchAsync(IDirectorySession session, DirectorySearch search)
    {
        var result = new List<DirectoryObject>();
        await foreach (var item in session.SearchAsync(search, Ct))
        {
            result.Add(item);
        }

        return result;
    }

    private static async Task<long> UsnChangedAsync(IDirectorySession session, string dn) =>
        (await session.GetAsync(dn, [Attr.UsnChanged], Ct))!.GetInt64(Attr.UsnChanged)!.Value;

    private static async Task<long> HighestUsnAsync(IDirectorySession session) =>
        (await session.GetAsync(string.Empty, ["highestCommittedUSN"], Ct))!.GetInt64("highestCommittedUSN")!.Value;

    [Fact]
    public async Task RootDse_ReturnsTheWatchdogAttributes()
    {
        var (builder, _, clock) = Create();
        var session = builder.Session;

        var rootDse = await session.GetAsync(string.Empty,
            ["highestCommittedUSN", "dsServiceName", "serverName", "dnsHostName", "isSynchronized", "currentTime",
             "defaultNamingContext", "configurationNamingContext", "schemaNamingContext", "rootDomainNamingContext", "supportedControl"], Ct);

        Assert.NotNull(rootDse);
        Assert.Equal(session.HighestCommittedUsn, rootDse.GetInt64("highestCommittedUSN"));
        Assert.Equal(builder.PdcNtdsSettingsDn, rootDse.GetString("dsServiceName"));
        Assert.StartsWith("CN=DC01,CN=Servers,", rootDse.GetString("serverName"), StringComparison.Ordinal);
        Assert.Equal("dc01.contoso.test", rootDse.GetString("dnsHostName"));
        Assert.Equal("TRUE", rootDse.GetString("isSynchronized"));
        Assert.Equal(clock.GetUtcNow(), rootDse.GetGeneralizedTime("currentTime"));
        Assert.Equal(builder.DomainDn, rootDse.GetString("defaultNamingContext"));
        Assert.Equal(builder.ConfigurationDn, rootDse.GetString("configurationNamingContext"));
        Assert.Equal(builder.SchemaDn, rootDse.GetString("schemaNamingContext"));
        Assert.Equal(builder.DomainDn, rootDse.GetString("rootDomainNamingContext"));
        Assert.Contains(LdapControls.ExtendedDn, rootDse.GetStrings("supportedControl"));

        session.IsSynchronized = false;
        var notSynchronized = await session.GetAsync(string.Empty, ["isSynchronized"], Ct);
        Assert.Equal("FALSE", notSynchronized!.GetString("isSynchronized"));
    }

    [Fact]
    public async Task DefaultFake_IsUnchanged_NoDomainControllersAndLockoutNeverExpires()
    {
        var clock = new ManualTimeProvider(Start);
        var builder = new FakeDirectoryBuilder(timeProvider: clock);
        var company = builder.AddSampleCompany();
        var session = builder.Session;

        Assert.False(session.Exists($"CN=Sites,{session.Info.ConfigurationNamingContext}"));
        Assert.False(session.EnforceNamingContextBoundaries);
        clock.Advance(TimeSpan.FromDays(30));
        var zaneta = await session.GetAsync(company.ZanetaSzymanska, [Attr.UserAccountControl, Attr.UserAccountControlComputed, "msDS-UserPasswordExpiryTimeComputed"], Ct);
        Assert.True(zaneta!.IsLockedOut);
        Assert.False(zaneta.Has("msDS-UserPasswordExpiryTimeComputed"));

        // The seeded state has no new constructed values and RootDSE reports the fake's USN counter.
        var rootDse = await session.GetAsync(string.Empty, ["highestCommittedUSN"], Ct);
        Assert.Equal(session.HighestCommittedUsn, rootDse!.GetInt64("highestCommittedUSN"));
    }

    [Fact]
    public async Task MemberChange_BumpsOnlyTheGroup()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var groupBefore = await UsnChangedAsync(session, company.GroupIt);
        var memberBefore = await UsnChangedAsync(session, company.MalgorzataWojcik);

        await session.ModifyAsync(company.GroupIt, [AttributeModification.Add(Attr.Member, company.MalgorzataWojcik)], null, Ct);

        Assert.True(await UsnChangedAsync(session, company.GroupIt) > groupBefore);
        Assert.Equal(memberBefore, await UsnChangedAsync(session, company.MalgorzataWojcik));
    }

    [Fact]
    public async Task RenameAndMove_BumpOnlyTheMovedObject_DescendantsKeepTheirUsn()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var childBefore = await UsnChangedAsync(session, company.JanKowalski);
        var groupBefore = await UsnChangedAsync(session, company.GroupIt);

        var renamed = await session.MoveAsync(company.ItOu, null, "OU=Informatyka", Ct);
        var movedUser = await session.MoveAsync(company.MalgorzataWojcik, company.SalesOu, null, Ct);

        var janDn = $"CN=Jan Kowalski,{renamed}";
        Assert.Equal(childBefore, await UsnChangedAsync(session, janDn));
        Assert.Equal(groupBefore, await UsnChangedAsync(session, company.GroupIt));
        Assert.Contains(janDn, session.GetValues(company.GroupIt, Attr.Member));
        Assert.True(await UsnChangedAsync(session, movedUser) > childBefore);
    }

    [Fact]
    public async Task DeletingAMember_RemovesItWithoutBumpingTheGroup_RestoreReactivatesWithoutBump()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var groupBefore = await UsnChangedAsync(session, company.GroupIt);

        await session.DeleteAsync(company.LukaszWisniewski, subtree: false, Ct);
        Assert.DoesNotContain(company.LukaszWisniewski, session.GetValues(company.GroupIt, Attr.Member));
        Assert.Equal(groupBefore, await UsnChangedAsync(session, company.GroupIt));

        var deleted = Assert.Single(session.DeletedDistinguishedNames, d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal));
        await session.RestoreDeletedAsync(deleted, company.LukaszWisniewski, Ct);
        Assert.Contains(company.LukaszWisniewski, session.GetValues(company.GroupIt, Attr.Member));
        Assert.Equal(groupBefore, await UsnChangedAsync(session, company.GroupIt));
    }

    [Fact]
    public async Task Tombstoning_BumpsTheDeletedObject()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var before = await UsnChangedAsync(session, company.PawelLewandowski);

        await session.DeleteAsync(company.PawelLewandowski, subtree: false, Ct);

        var tombstone = Assert.Single(await SearchAsync(session, new DirectorySearch
        {
            BaseDn = $"CN=Deleted Objects,{builder.DomainDn}",
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.Equal(Attr.SamAccountName, "pawel.lewandowski"),
            Attributes = [Attr.UsnChanged],
            IncludeDeleted = true,
        }));
        Assert.True(tombstone.GetInt64(Attr.UsnChanged) > before);
    }

    [Fact]
    public async Task NamingContexts_DomainSubtreeSearchNeverReturnsConfigurationObjects()
    {
        var (builder, _, _) = Create();
        var session = builder.Session;

        var fromDomain = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = builder.DomainDn,
            Filter = "(objectClass=*)",
            Attributes = [Attr.ObjectClass],
        });
        var fromConfiguration = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = $"CN=Sites,{builder.ConfigurationDn}",
            Filter = LdapFilter.Equal(Attr.ObjectClass, "nTDSDSA"),
        });
        var schemaFromConfiguration = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = builder.ConfigurationDn,
            Filter = LdapFilter.Equal(Attr.ObjectClass, "dMD"),
        });

        Assert.DoesNotContain(fromDomain, o => o.Dn.IsDescendantOf(DistinguishedName.Parse(builder.ConfigurationDn)));
        Assert.DoesNotContain(fromDomain, o => DistinguishedName.AreEqual(o.DistinguishedName, builder.ConfigurationDn));
        Assert.Equal(builder.PdcNtdsSettingsDn, Assert.Single(fromConfiguration).DistinguishedName);
        Assert.Empty(schemaFromConfiguration);
    }

    [Fact]
    public async Task SeededDomainController_HasServerAndNtdsSettings()
    {
        var (builder, _, _) = Create();
        var session = builder.Session;

        var ntds = await session.GetAsync(builder.PdcNtdsSettingsDn!, ["invocationId", "msDS-HasDomainNCs", Attr.ObjectCategory, Attr.ObjectGuid], Ct);
        var server = await session.GetAsync(DistinguishedName.Parse(builder.PdcNtdsSettingsDn!).Parent!.ToString(), [Attr.DnsHostName], Ct);
        var head = await session.GetAsync(builder.DomainDn, ["fSMORoleOwner", "wellKnownObjects", "lockoutDuration"], Ct);

        Assert.Equal(FakeDirectoryBuilder.PdcInvocationId, new Guid(ntds!.GetBytes("invocationId")!));
        Assert.Equal(builder.DomainDn, ntds.GetString("msDS-HasDomainNCs"));
        Assert.StartsWith("CN=NTDS-DSA,", ntds.GetString(Attr.ObjectCategory), StringComparison.Ordinal);
        Assert.NotNull(ntds.ObjectGuid);
        Assert.Equal("dc01.contoso.test", server!.GetString(Attr.DnsHostName));
        Assert.Equal(builder.PdcNtdsSettingsDn, head!.GetString("fSMORoleOwner"));
        Assert.Equal(2, head.GetStrings("wellKnownObjects").Count);
        Assert.Equal(-FakeDirectoryBuilder.DefaultLockoutDuration.Ticks, head.GetInt64("lockoutDuration"));
    }

    [Fact]
    public async Task ReadOnlyDomainController_HasTheRodcCategory()
    {
        var (builder, _, _) = Create();

        var ntds = builder.AddDomainController("rodc01.contoso.test", Guid.NewGuid(), isReadOnly: true);

        var entry = await builder.Session.GetAsync(ntds, [Attr.ObjectCategory], Ct);
        Assert.StartsWith("CN=NTDS-DSA-RO,", entry!.GetString(Attr.ObjectCategory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchServer_ChangesDsServiceNameAndUsnSpace_FailbackSeesLaterChanges()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var dc2Invocation = new Guid("0d2c0000-0000-4000-8000-000000000002");
        var dc2Ntds = builder.AddDomainController("dc02.contoso.test", dc2Invocation);
        var highOnDc1 = await HighestUsnAsync(session);

        session.SwitchServer("dc02.contoso.test");
        var rootDse = await session.GetAsync(string.Empty, ["dsServiceName", "highestCommittedUSN", "dnsHostName"], Ct);
        Assert.Equal(dc2Ntds, rootDse!.GetString("dsServiceName"));
        Assert.Equal("dc02.contoso.test", rootDse.GetString("dnsHostName"));
        Assert.True(rootDse.GetInt64("highestCommittedUSN") >= highOnDc1 + FakeDirectorySession.DomainControllerUsnSpacing);
        Assert.Equal(dc2Invocation, session.CurrentInvocationId);

        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.Department, "Bezpieczeństwo")], null, Ct);

        session.SwitchServer("dc01.contoso.test");
        var changed = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = builder.DomainDn,
            Filter = LdapFilter.GreaterOrEqual(Attr.UsnChanged, highOnDc1 + 1),
            Attributes = [Attr.UsnChanged],
        });
        Assert.Equal(company.JanKowalski, Assert.Single(changed).DistinguishedName);
        Assert.True(await HighestUsnAsync(session) > highOnDc1);
    }

    [Fact]
    public async Task SimulateRestore_ChangesTheInvocationId_OverrideReportsAUsnRollback()
    {
        var (builder, _, _) = Create();
        var session = builder.Session;
        var real = await HighestUsnAsync(session);

        var newInvocation = session.SimulateRestore();
        session.OverrideHighestCommittedUsn(real - 100);

        var ntds = await session.GetAsync(session.DsServiceName, ["invocationId"], Ct);
        Assert.NotEqual(FakeDirectoryBuilder.PdcInvocationId, newInvocation);
        Assert.Equal(newInvocation, new Guid(ntds!.GetBytes("invocationId")!));
        Assert.Equal(real - 100, await HighestUsnAsync(session));
        session.OverrideHighestCommittedUsn(null);
        Assert.Equal(real, await HighestUsnAsync(session));
    }

    [Fact]
    public async Task Metadata_IsWrittenByEveryWritePath_AsWindowsXml()
    {
        var (builder, company, clock) = Create();
        var session = builder.Session;
        clock.Advance(TimeSpan.FromMinutes(5));

        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.Department, "Bezpieczeństwo")], null, Ct);
        await session.ModifyAsync(company.GroupIt, [AttributeModification.Add(Attr.Member, company.MalgorzataWojcik)], null, Ct);
        await session.ResetPasswordAsync(company.LukaszWisniewski, "Zażółć!Gęślą1", Ct);

        var jan = await session.GetAsync(company.JanKowalski, ["msDS-ReplAttributeMetaData", Attr.UsnChanged], Ct);
        var department = Assert.Single(jan!.GetStrings("msDS-ReplAttributeMetaData"), v => v.Contains("<pszAttributeName>department<", StringComparison.Ordinal));
        Assert.Contains("<dwVersion>2</dwVersion>", department, StringComparison.Ordinal);
        Assert.Contains($"<usnLocalChange>{jan.GetInt64(Attr.UsnChanged)}</usnLocalChange>", department, StringComparison.Ordinal);
        Assert.Contains("<ftimeLastOriginatingChange>2026-10-14T08:05:00Z</ftimeLastOriginatingChange>", department, StringComparison.Ordinal);
        Assert.Contains(FakeDirectoryBuilder.PdcInvocationId.ToString("D"), department, StringComparison.Ordinal);
        Assert.Contains(builder.PdcNtdsSettingsDn!, department, StringComparison.Ordinal);
        Assert.EndsWith("\0", department, StringComparison.Ordinal);

        var group = await session.GetAsync(company.GroupIt, ["msDS-ReplAttributeMetaData"], Ct);
        Assert.DoesNotContain(group!.GetStrings("msDS-ReplAttributeMetaData"), v => v.Contains("<pszAttributeName>member<", StringComparison.Ordinal));

        var lukasz = await session.GetAsync(company.LukaszWisniewski, ["msDS-ReplAttributeMetaData"], Ct);
        var metadata = lukasz!.GetStrings("msDS-ReplAttributeMetaData");
        Assert.Contains(metadata, v => v.Contains("<pszAttributeName>unicodePwd<", StringComparison.Ordinal) && v.Contains("<dwVersion>2<", StringComparison.Ordinal));
        Assert.Contains(metadata, v => v.Contains("<pszAttributeName>pwdLastSet<", StringComparison.Ordinal) && v.Contains("<dwVersion>2<", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PwdLastSetMinusOne_UpdatesPwdLastSetMetadataOnly()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;

        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.PwdLastSet, "0")], null, Ct);
        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.PwdLastSet, "-1")], null, Ct);

        var jan = await session.GetAsync(company.JanKowalski, ["msDS-ReplAttributeMetaData"], Ct);
        var metadata = jan!.GetStrings("msDS-ReplAttributeMetaData");
        Assert.Contains(metadata, v => v.Contains("<pszAttributeName>pwdLastSet<", StringComparison.Ordinal) && v.Contains("<dwVersion>3<", StringComparison.Ordinal));
        Assert.Contains(metadata, v => v.Contains("<pszAttributeName>unicodePwd<", StringComparison.Ordinal) && v.Contains("<dwVersion>1<", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtendedDn_SearchAndRangedReadReturnGuidAndSid()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var janGuid = session.Snapshot(company.JanKowalski)!.ObjectGuid;
        var janSid = session.Snapshot(company.JanKowalski)!.ObjectSid;

        var group = Assert.Single(await SearchAsync(session, new DirectorySearch
        {
            BaseDn = company.GroupIt,
            Scope = SearchScope.Base,
            Attributes = [Attr.Member, Attr.ObjectGuid],
            ExtendedDn = true,
        }));
        var values = new List<string>();
        await foreach (var value in session.GetAllValuesAsync(company.GroupIt, Attr.Member, new AttributeReadOptions { ExtendedDn = true }, Ct))
        {
            values.Add(value);
        }

        Assert.Equal(company.GroupIt, group.DistinguishedName);
        var parsed = group.GetStrings(Attr.Member).Select(v => Assert.IsType<ExtendedDistinguishedName>(ParseExtended(v))).ToList();
        var jan = Assert.Single(parsed, p => p.Guid == janGuid);
        Assert.Equal(janSid, jan.Sid);
        Assert.Equal(company.JanKowalski, jan.Dn);
        Assert.Equal(group.GetStrings(Attr.Member), values);
    }

    [Fact]
    public async Task ExtendedDn_DnBinaryValuesKeepTheBinaryPart()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        session.SetValues(company.JanKowalski, "msDS-KeyCredentialLink", $"B:8:0102AABB:{company.JanKowalski}");

        var jan = Assert.Single(await SearchAsync(session, new DirectorySearch
        {
            BaseDn = company.JanKowalski,
            Scope = SearchScope.Base,
            Attributes = ["msDS-KeyCredentialLink"],
            ExtendedDn = true,
        }));

        var value = jan.GetString("msDS-KeyCredentialLink");
        Assert.StartsWith("B:8:0102AABB:<GUID=", value, StringComparison.Ordinal);
        Assert.EndsWith($";{company.JanKowalski}", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lockout_ComputedBitExpires_LogonAfterExpiryResetsLockoutTime()
    {
        var (builder, company, clock) = Create();
        var session = builder.Session;
        session.LockoutDuration = TimeSpan.FromMinutes(30);
        var before = await UsnChangedAsync(session, company.JanKowalski);

        session.SimulateLockout(company.JanKowalski);
        var locked = await session.GetAsync(company.JanKowalski, [Attr.UserAccountControl, Attr.UserAccountControlComputed, Attr.LockoutTime, Attr.UsnChanged], Ct);
        Assert.True(locked!.IsLockedOut);
        Assert.True(locked.GetInt64(Attr.UsnChanged) > before);

        clock.Advance(TimeSpan.FromMinutes(31));
        var expired = await session.GetAsync(company.JanKowalski, [Attr.UserAccountControl, Attr.UserAccountControlComputed, Attr.LockoutTime], Ct);
        Assert.False(expired!.IsLockedOut);
        Assert.Equal(locked.GetInt64(Attr.LockoutTime), expired.GetInt64(Attr.LockoutTime));

        session.SimulateSuccessfulLogon(company.JanKowalski);
        var afterLogon = await session.GetAsync(company.JanKowalski, [Attr.LockoutTime, Attr.LastLogonTimestamp], Ct);
        Assert.Equal(0, afterLogon!.GetInt64(Attr.LockoutTime));
        Assert.Equal(clock.GetUtcNow(), afterLogon.GetFileTime(Attr.LastLogonTimestamp));
    }

    [Fact]
    public async Task Logon_BeforeExpiry_KeepsLockoutTime_BadPasswordDoesNotBumpUsn()
    {
        var (builder, company, clock) = Create();
        var session = builder.Session;
        session.LockoutDuration = TimeSpan.FromMinutes(30);
        session.SimulateLockout(company.JanKowalski);
        clock.Advance(TimeSpan.FromMinutes(10));

        var before = await UsnChangedAsync(session, company.JanKowalski);
        session.SimulateBadPassword(company.JanKowalski);
        Assert.Equal(before, await UsnChangedAsync(session, company.JanKowalski));
        Assert.Equal("1", session.GetValues(company.JanKowalski, Attr.BadPwdCount).Single());

        session.SimulateSuccessfulLogon(company.JanKowalski);
        var jan = await session.GetAsync(company.JanKowalski, [Attr.LockoutTime, Attr.UsnChanged], Ct);
        Assert.True(jan!.GetInt64(Attr.LockoutTime) > 0);
        Assert.True(jan.GetInt64(Attr.UsnChanged) > before);
    }

    [Fact]
    public async Task ConstructedPasswordExpiryAndResultantPso_AreOptIn()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var pso = builder.AddPasswordSettings("PSO-Admini", 10, TimeSpan.FromHours(2), [company.JanKowalski]);
        string[] attributes = ["msDS-UserPasswordExpiryTimeComputed", "msDS-ResultantPSO"];

        var before = await session.GetAsync(company.JanKowalski, attributes, Ct);
        session.EmulateConstructedPasswordExpiry = true;
        session.SetResultantPso(company.JanKowalski, pso);
        var jan = await session.GetAsync(company.JanKowalski, attributes, Ct);
        var grazyna = await session.GetAsync(company.GrazynaDabrowska, attributes, Ct);
        var computer = await session.GetAsync(company.Computer1, attributes, Ct);

        Assert.False(before!.Has("msDS-UserPasswordExpiryTimeComputed"));
        Assert.False(before.Has("msDS-ResultantPSO"));
        Assert.Equal(FakeDirectoryBuilder.DefaultPasswordLastSet + TimeSpan.FromDays(42), jan!.GetFileTime("msDS-UserPasswordExpiryTimeComputed"));
        Assert.Equal(pso, jan.GetString("msDS-ResultantPSO"));
        Assert.Equal(long.MaxValue, grazyna!.GetInt64("msDS-UserPasswordExpiryTimeComputed"));
        Assert.Equal(long.MaxValue, computer!.GetInt64("msDS-UserPasswordExpiryTimeComputed"));

        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.PwdLastSet, "0")], null, Ct);
        var mustChange = await session.GetAsync(company.JanKowalski, attributes, Ct);
        Assert.Equal(0, mustChange!.GetInt64("msDS-UserPasswordExpiryTimeComputed"));
    }

    [Fact]
    public async Task HideAttributes_HidesValuesFromReadsAndFilters()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        session.HideAttributes(company.JanKowalski, Attr.UsnChanged);

        var visible = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = builder.DomainDn,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectCategory, "person"), LdapFilter.GreaterOrEqual(Attr.UsnChanged, 1)),
        });
        var jan = await session.GetAsync(company.JanKowalski, [Attr.UsnChanged, Attr.Department], Ct);

        Assert.DoesNotContain(visible, o => o.DistinguishedName == company.JanKowalski);
        Assert.Contains(visible, o => o.DistinguishedName == company.LukaszWisniewski);
        Assert.False(jan!.Has(Attr.UsnChanged));
        Assert.Equal("IT", jan.GetString(Attr.Department));
    }

    [Fact]
    public async Task DenyDeletedObjects_ShowDeletedSearchesReturnNothing()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        await session.DeleteAsync(company.PawelLewandowski, subtree: false, Ct);
        session.DenyDeletedObjects = true;
        var deletedObjects = $"CN=Deleted Objects,{builder.DomainDn}";

        var children = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = deletedObjects,
            Scope = SearchScope.OneLevel,
            Filter = "(isDeleted=TRUE)",
            IncludeDeleted = true,
        });
        var container = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = deletedObjects,
            Scope = SearchScope.Base,
            Attributes = [Attr.ObjectGuid, Attr.IsDeleted],
            IncludeDeleted = true,
        });
        var byGuid = await SearchAsync(session, new DirectorySearch
        {
            BaseDn = builder.DomainDn,
            Filter = LdapFilter.EqualGuid(Attr.ObjectGuid, session.Snapshot(session.DeletedDistinguishedNames[0])!.ObjectGuid!.Value),
            IncludeDeleted = true,
        });

        Assert.Empty(children);
        Assert.False(Assert.Single(container).Has(Attr.ObjectGuid));
        Assert.Empty(byGuid);
    }

    [Fact]
    public async Task NewSchemaAttributes_AreAcceptedByModify()
    {
        var (builder, company, _) = Create();
        var session = builder.Session;
        var sd = builder.DefaultSecurityDescriptor();

        await session.ModifyAsync(company.JanKowalski,
        [
            AttributeModification.Replace("altSecurityIdentities", "X509:<I>DC=test,DC=contoso,CN=CA<S>CN=Jan"),
            AttributeModification.Replace("msDS-AllowedToDelegateTo", "cifs/srv01.contoso.test"),
            AttributeModification.Replace("msDS-AllowedToActOnBehalfOfOtherIdentity", sd),
            AttributeModification.Replace("logonHours", new byte[21]),
            AttributeModification.Replace("employeeType", "Pracownik etatowy"),
            AttributeModification.Replace("userCertificate", new byte[] { 0x30, 0x82 }),
        ], null, Ct);
        await session.ModifyAsync(builder.DomainDn, [AttributeModification.Replace("lockoutThreshold", "10"), AttributeModification.Replace("gPOptions", "1")], null, Ct);

        var jan = await session.GetAsync(company.JanKowalski, ["altSecurityIdentities", "employeeType", "logonHours"], Ct);
        Assert.Equal("Pracownik etatowy", jan!.GetString("employeeType"));
        Assert.Equal(21, jan.GetBytes("logonHours")!.Length);
        Assert.Equal("10", session.GetValues(builder.DomainDn, "lockoutThreshold").Single());
    }

    [Fact]
    public async Task UsnSpaces_TranslateUsnCreatedToo()
    {
        var (builder, _, _) = Create();
        var session = builder.Session;
        builder.AddDomainController("dc02.contoso.test", Guid.NewGuid());
        var ou = builder.AddOu("Łódź");
        var onDc1 = (await session.GetAsync(ou, [Attr.UsnCreated], Ct))!.GetInt64(Attr.UsnCreated);

        session.SwitchServer("dc02.contoso.test");
        var onDc2 = (await session.GetAsync(ou, [Attr.UsnCreated], Ct))!.GetInt64(Attr.UsnCreated);

        Assert.Equal(onDc1 + FakeDirectorySession.DomainControllerUsnSpacing, onDc2);
    }

    private static ExtendedDistinguishedName? ParseExtended(string value) =>
        ExtendedDistinguishedName.TryParse(value, out var parsed) ? parsed : null;
}
