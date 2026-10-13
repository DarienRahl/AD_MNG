using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>Writes - only inside the dedicated test OU.</summary>
public sealed class WriteTests(LabFixture lab)
{
    [Fact]
    public async Task Add_Read_Modify_Delete_User()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Zapis"));
        var dn = await lab.CreateUserAsync(ou, "Jan Kowalski", new Dictionary<string, string>
        {
            [Attr.GivenName] = "Jan",
            [Attr.Sn] = "Kowalski",
            [Attr.Department] = "Księgowość",
        });

        var created = await lab.Admin.GetAsync(dn, [Attr.ObjectClass, Attr.Department, Attr.UserAccountControl, Attr.ObjectSid], ct);
        Assert.NotNull(created);
        Assert.Equal(ObjectKind.User, created.Kind);
        Assert.Equal("Księgowość", created.GetString(Attr.Department));
        Assert.NotNull(created.ObjectSid);

        await lab.Admin.ModifyAsync(
            dn,
            [
                AttributeModification.Replace(Attr.Department, "Kadry i płace"),
                AttributeModification.Replace(Attr.Title, "Specjalista ds. płac"),
                AttributeModification.Add(Attr.OtherTelephone, "+48 22 111 11 11", "+48 22 222 22 22"),
            ],
            ModifyOptions.Default,
            ct);
        var modified = await lab.Admin.GetAsync(dn, [Attr.Department, Attr.Title, Attr.OtherTelephone], ct);
        Assert.Equal("Kadry i płace", modified!.GetString(Attr.Department));
        Assert.Equal("Specjalista ds. płac", modified.GetString(Attr.Title));
        Assert.Equal(2, modified.GetStrings(Attr.OtherTelephone).Count);

        await lab.Admin.ModifyAsync(
            dn,
            [AttributeModification.Clear(Attr.Title), AttributeModification.Delete(Attr.OtherTelephone, "+48 22 111 11 11")],
            null,
            ct);
        var cleared = await lab.Admin.GetAsync(dn, [Attr.Title, Attr.OtherTelephone], ct);
        Assert.False(cleared!.Has(Attr.Title));
        Assert.Equal(["+48 22 222 22 22"], cleared.GetStrings(Attr.OtherTelephone));

        await lab.Admin.DeleteAsync(dn, subtree: false, ct);
        Assert.Null(await lab.Admin.GetAsync(dn, [], ct));
    }

    [Fact]
    public async Task BinaryValues_RoundTrip()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Binarne"));
        var dn = await lab.CreateUserAsync(ou, "Zdjęcie Testowe");
        var photo = Enumerable.Range(0, 2048).Select(i => (byte)(i * 7 % 256)).ToArray();

        await lab.Admin.ModifyAsync(dn, [AttributeModification.Replace("thumbnailPhoto", photo)], null, ct);
        var read = await lab.Admin.GetAsync(dn, ["thumbnailPhoto"], ct);

        Assert.Equal(photo, read!.GetBytes("thumbnailPhoto"));
        Assert.IsType<byte[]>(read.Attributes["thumbnailPhoto"][0]);
    }

    [Fact]
    public async Task Move_And_Rename_ReturnTheNewDn()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var source = await lab.CreateOuAsync(TestNames.Unique("Źródło"));
        var target = await lab.CreateOuAsync(TestNames.Unique("Cel"));
        var dn = await lab.CreateUserAsync(source, "Anna Nowak");

        var moved = await lab.Admin.MoveAsync(dn, target, null, ct);
        Assert.True(DistinguishedName.AreEqual(DistinguishedName.Parse(target).Child("CN", "Anna Nowak").ToString(), moved));
        Assert.Null(await lab.Admin.GetAsync(dn, [], ct));
        Assert.NotNull(await lab.Admin.GetAsync(moved, [], ct));

        var renamed = await lab.Admin.MoveAsync(moved, null, "CN=Nowak\\, Anna (Łódź)", ct);
        Assert.True(DistinguishedName.AreEqual(DistinguishedName.Parse(target).Child("CN", "Nowak, Anna (Łódź)").ToString(), renamed));
        var read = await lab.Admin.GetAsync(renamed, [Attr.Cn, Attr.Name], ct);
        Assert.Equal("Nowak, Anna (Łódź)", read!.GetString(Attr.Cn));
        Assert.Equal("Nowak, Anna (Łódź)", read.Name);

        var both = await lab.Admin.MoveAsync(renamed, source, "CN=Anna Nowak-Kowalska", ct);
        Assert.True(DistinguishedName.AreEqual(DistinguishedName.Parse(source).Child("CN", "Anna Nowak-Kowalska").ToString(), both));
        Assert.NotNull(await lab.Admin.GetAsync(both, [], ct));
    }

    [Fact]
    public async Task PolishDiacritics_InDistinguishedNames()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var parent = await lab.CreateOuAsync(TestNames.Unique("Diakrytyki"));
        var accounting = await lab.CreateOuAsync("Księgowość", parent);
        var dn = await lab.CreateUserAsync(accounting, "Łukasz Żółć-Gęślą");

        var search = new DirectorySearch
        {
            BaseDn = accounting,
            Scope = SearchScope.OneLevel,
            Attributes = [Attr.Cn, Attr.DistinguishedName],
        };
        var found = new List<DirectoryObject>();
        await foreach (var entry in lab.Admin.SearchAsync(search, ct))
        {
            found.Add(entry);
        }

        var user = Assert.Single(found);
        Assert.True(DistinguishedName.AreEqual(dn, user.DistinguishedName));
        Assert.Contains("Księgowość", user.DistinguishedName, StringComparison.Ordinal);
        Assert.Equal("Łukasz Żółć-Gęślą", user.Name);
        Assert.Equal("Łukasz Żółć-Gęślą", user.Dn.Name);
        Assert.True(DistinguishedName.AreEqual(dn, user.GetString(Attr.DistinguishedName)));
    }

    [Fact]
    public async Task PermissiveModify_IsIdempotent()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Idempotent"));
        var member = await lab.CreateContactAsync(ou, "Członek Grupy");
        var group = DistinguishedName.Parse(ou).Child("CN", "GG_Testowa").ToString();
        await lab.Admin.AddAsync(
            group,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", ObjectClasses.Group],
                [Attr.SamAccountName] = [TestNames.SamAccountName()],
                [Attr.Description] = [],
            },
            ct);

        // Samba 4.x ignores the permissive modify control for linked attributes (AD honours it).
        var rootDse = await lab.Admin.GetAsync(string.Empty, ["vendorName"], ct);
        var isSamba = rootDse?.GetString("vendorName")?.Contains("Samba", StringComparison.OrdinalIgnoreCase) == true;

        await lab.Admin.ModifyAsync(group, [AttributeModification.Add(Attr.Member, member)], ModifyOptions.Idempotent, ct);
        var secondAdd = await Record.ExceptionAsync(() =>
            lab.Admin.ModifyAsync(group, [AttributeModification.Add(Attr.Member, member)], ModifyOptions.Idempotent, ct));
        AssertPermissiveOutcome(secondAdd, isSamba);

        // Without the control a linked value that exists is entryAlreadyExists (AD: 0x562 ERROR_MEMBER_IN_GROUP).
        var duplicate = await Assert.ThrowsAsync<DirectoryException>(() =>
            lab.Admin.ModifyAsync(group, [AttributeModification.Add(Attr.Member, member)], ModifyOptions.Default, ct));
        Assert.Contains(duplicate.Kind, new[] { DirectoryErrorKind.AlreadyExists, DirectoryErrorKind.AttributeOrValueExists });

        await lab.Admin.ModifyAsync(group, [AttributeModification.Delete(Attr.Member, member)], ModifyOptions.Idempotent, ct);
        var secondDelete = await Record.ExceptionAsync(() =>
            lab.Admin.ModifyAsync(group, [AttributeModification.Delete(Attr.Member, member)], ModifyOptions.Idempotent, ct));
        AssertPermissiveOutcome(secondDelete, isSamba);
        var read = await lab.Admin.GetAsync(group, [Attr.Member], ct);
        Assert.False(read!.Has(Attr.Member));
    }

    [Fact]
    public async Task PermissiveModify_OnAPlainMultiValuedAttribute()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Telefony"));
        var dn = await lab.CreateUserAsync(ou, "Wiele Numerów");
        await lab.Admin.ModifyAsync(dn, [AttributeModification.Add(Attr.OtherTelephone, "+48 22 000 00 01")], null, ct);

        var exists = await Assert.ThrowsAsync<DirectoryException>(() =>
            lab.Admin.ModifyAsync(dn, [AttributeModification.Add(Attr.OtherTelephone, "+48 22 000 00 01")], null, ct));
        Assert.Equal(DirectoryErrorKind.AttributeOrValueExists, exists.Kind);
        Assert.Equal(20, exists.LdapResultCode);

        var missing = await Assert.ThrowsAsync<DirectoryException>(() =>
            lab.Admin.ModifyAsync(dn, [AttributeModification.Delete(Attr.OtherTelephone, "+48 22 999 99 99")], null, ct));
        Assert.Equal(DirectoryErrorKind.NoSuchAttribute, missing.Kind);

        await lab.Admin.ModifyAsync(
            dn,
            [
                AttributeModification.Add(Attr.OtherTelephone, "+48 22 000 00 01", "+48 22 000 00 02"),
                AttributeModification.Delete(Attr.OtherTelephone, "+48 22 999 99 99"),
            ],
            ModifyOptions.Idempotent,
            ct);
        var read = await lab.Admin.GetAsync(dn, [Attr.OtherTelephone], ct);
        Assert.Equal(["+48 22 000 00 01", "+48 22 000 00 02"], read!.GetStrings(Attr.OtherTelephone).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DeleteOldAddNew_IsACompareAndSwap()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Wspolbieznosc"));
        var dn = await lab.CreateUserAsync(ou, "Optymistyczna Blokada", new Dictionary<string, string> { [Attr.Department] = "IT" });

        // Somebody else changed the value since we read "Marketing": the swap must fail atomically.
        var conflict = await Assert.ThrowsAsync<DirectoryException>(() => lab.Admin.ModifyAsync(
            dn,
            [AttributeModification.Delete(Attr.Department, "Marketing"), AttributeModification.Add(Attr.Department, "Logistyka")],
            null,
            ct));
        Assert.Equal(DirectoryErrorKind.NoSuchAttribute, conflict.Kind);
        Assert.Equal("IT", (await lab.Admin.GetAsync(dn, [Attr.Department], ct))!.GetString(Attr.Department));

        await lab.Admin.ModifyAsync(
            dn,
            [AttributeModification.Delete(Attr.Department, "IT"), AttributeModification.Add(Attr.Department, "Logistyka")],
            null,
            ct);
        Assert.Equal("Logistyka", (await lab.Admin.GetAsync(dn, [Attr.Department], ct))!.GetString(Attr.Department));
    }

    [Fact]
    public async Task TreeDelete_RemovesAContainerWithChildren()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Drzewo"));
        var child = await lab.CreateOuAsync("Dziecko", ou);
        await lab.CreateContactAsync(child, "Wnuk");

        var error = await Assert.ThrowsAsync<DirectoryException>(() => lab.Admin.DeleteAsync(ou, subtree: false, ct));
        Assert.Equal(DirectoryErrorKind.NotAllowedOnNonLeaf, error.Kind);
        Assert.Equal(66, error.LdapResultCode);
        Assert.NotNull(await lab.Admin.GetAsync(ou, [], ct));

        await lab.Admin.DeleteAsync(ou, subtree: true, ct);
        Assert.Null(await lab.Admin.GetAsync(ou, [], ct));
    }

    [Fact]
    public async Task ErrorMapping_AlreadyExists_And_NoSuchObject()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Bledy"));
        var contact = await lab.CreateContactAsync(ou, "Duplikat");

        var exists = await Assert.ThrowsAsync<DirectoryException>(() => lab.CreateContactAsync(ou, "Duplikat"));
        Assert.Equal(DirectoryErrorKind.AlreadyExists, exists.Kind);
        Assert.Equal(68, exists.LdapResultCode);
        Assert.True(DistinguishedName.AreEqual(contact, exists.DistinguishedName));

        var missing = DistinguishedName.Parse(ou).Child("CN", "Nie ma").ToString();
        var noSuchObject = await Assert.ThrowsAsync<DirectoryException>(() => lab.Admin.ModifyAsync(
            missing,
            [AttributeModification.Replace(Attr.Description, "x")],
            null,
            ct));
        Assert.Equal(DirectoryErrorKind.NoSuchObject, noSuchObject.Kind);
        Assert.Equal(32, noSuchObject.LdapResultCode);
        Assert.False(noSuchObject.IsTransient);

        var deleteMissing = await Assert.ThrowsAsync<DirectoryException>(() => lab.Admin.DeleteAsync(missing, subtree: false, ct));
        Assert.Equal(DirectoryErrorKind.NoSuchObject, deleteMissing.Kind);

        var moveMissing = await Assert.ThrowsAsync<DirectoryException>(() => lab.Admin.MoveAsync(missing, null, "CN=Inna", ct));
        Assert.Equal(DirectoryErrorKind.NoSuchObject, moveMissing.Kind);

        var objectClass = await Assert.ThrowsAsync<DirectoryException>(() => lab.Admin.ModifyAsync(
            contact,
            [AttributeModification.Replace(Attr.SamAccountName, "nie-dla-kontaktu")],
            null,
            ct));
        Assert.Equal(DirectoryErrorKind.ObjectClassViolation, objectClass.Kind);
    }

    /// <summary>
    /// AD: a permissive add of an existing / delete of a missing linked value succeeds. Samba 4.19 answers
    /// entryAlreadyExists / unwillingToPerform instead - accepted (as a translated error) only there.
    /// </summary>
    private static void AssertPermissiveOutcome(Exception? error, bool isSamba)
    {
        if (isSamba && error is not null)
        {
            Assert.IsType<DirectoryException>(error);
        }
        else
        {
            Assert.Null(error);
        }
    }

    [Fact]
    public async Task InvalidWriteArguments_AreRejectedBeforeSending()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var dn = $"CN=Cokolwiek,{LabSettings.TestOu}";

        await Assert.ThrowsAsync<ArgumentException>(() => lab.Admin.ModifyAsync(dn, [], null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => lab.Admin.ModifyAsync(dn, [AttributeModification.Add(Attr.Description)], null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => lab.Admin.ModifyAsync(
            dn,
            [new AttributeModification(Attr.Description, ModificationOperation.Replace, [42])],
            null,
            ct));
        await Assert.ThrowsAsync<ArgumentException>(() => lab.Admin.MoveAsync(dn, null, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => lab.Admin.MoveAsync(dn, null, "CN=a,CN=b", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>(), ct));
    }
}
