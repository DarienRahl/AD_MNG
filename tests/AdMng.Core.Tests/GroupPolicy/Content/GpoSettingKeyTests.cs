using System.Text;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Content;

/// <summary>
/// <see cref="GpoSettingKey.Parse"/> against the keys the real parsers make of a whole sample GPO (every file kind), the
/// K4 catalog links, cut keys and keys of no known shape.
/// </summary>
public sealed class GpoSettingKeyTests
{
    private const string Sa = "Computer/Security/System Access/";
    private const string Rv = "Computer/Security/Registry Values/";
    private const string MachinePol = "Computer/Registry/HKLM\\";
    private const string GroupsItem = "Computer/Preferences/Groups/{A1B2C3D4-0000-4000-8000-000000000002}";

    private static readonly IReadOnlyList<GpoSetting> Sample = GpoContentSamples.Settings();

    [Fact]
    public void EveryKeyOfTheSampleGpo_HasAKnownArea_AndEveryAreaOccurs()
    {
        var parsed = Sample.Select(s => GpoSettingKey.Parse(s.Key)).ToList();

        Assert.All(parsed, p => Assert.NotEqual(GpoSettingArea.Other, p.Area));
        Assert.All(parsed, p => Assert.False(p.Truncated));
        Assert.Equal(Sample.Select(s => s.Key), parsed.Select(p => p.Key));
        Assert.Equal(
            Enum.GetValues<GpoSettingArea>().Where(a => a != GpoSettingArea.Other).Order(),
            parsed.Select(p => p.Area).Distinct().Order());
    }

    [Fact]
    public void SystemAccessAndKerberosPolicy_AreLinkedToTheCatalog_WithTheirUnits()
    {
        var length = Parse(Sa + "MinimumPasswordLength");
        Assert.Equal((GpoScope.Computer, GpoSettingArea.SystemAccess, SecurityTemplateDocument.SystemAccess, "MinimumPasswordLength"),
            (length.Scope, length.Area, length.Section, length.Name));
        Assert.Equal("MinimumPasswordLength", length.Option!.Id);
        Assert.Equal(SecurityValueUnit.Characters, length.Option.Unit);

        Assert.Equal(SecurityValueUnit.Days, Parse(Sa + "MaximumPasswordAge").Option!.Unit);
        Assert.Equal(SecurityValueUnit.Passwords, Parse(Sa + "PasswordHistorySize").Option!.Unit);
        Assert.Equal(SecurityValueUnit.Attempts, Parse(Sa + "LockoutBadCount").Option!.Unit);
        Assert.Equal(SecurityValueUnit.Minutes, Parse(Sa + "LockoutDuration").Option!.Unit);
        Assert.Equal(SecurityValueUnit.None, Parse(Sa + "PasswordComplexity").Option!.Unit);
        Assert.Equal("NewAdministratorName", Parse(Sa + "NewAdministratorName").Option!.Id);
        // Not in the catalog: named, but not linked.
        var notListed = Parse(Sa + "RequireLogonToChangePassword");
        Assert.Equal((GpoSettingArea.SystemAccess, "RequireLogonToChangePassword"), (notListed.Area, notListed.Name));
        Assert.Null(notListed.Option);

        var ticket = Parse("Computer/Security/Kerberos Policy/MaxTicketAge");
        Assert.Equal((GpoSettingArea.KerberosPolicy, SecurityTemplateDocument.KerberosPolicy, "MaxTicketAge", SecurityValueUnit.Hours),
            (ticket.Area, ticket.Section, ticket.Name, ticket.Option!.Unit));
        Assert.Equal(SecurityValueUnit.Days, Parse("Computer/Security/Kerberos Policy/MaxRenewAge").Option!.Unit);
        Assert.Equal(SecurityValueUnit.Minutes, Parse("Computer/Security/Kerberos Policy/MaxServiceAge").Option!.Unit);
        Assert.Equal(SecurityValueUnit.Minutes, Parse("Computer/Security/Kerberos Policy/MaxClockSkew").Option!.Unit);
    }

    [Fact]
    public void ToString_ShowsNoPartOfTheKey()
    {
        var parsed = Parse(@"Computer/Security/Group Membership/CONTOSO\Pomoc Techniczna/Members");

        Assert.Equal("Computer/RestrictedGroup", parsed.ToString());
    }

    [Fact]
    public void EventAudit_UserRights_AndRestrictedGroups()
    {
        var audit = Parse("Computer/Security/Event Audit/AuditLogonEvents");
        Assert.Equal((GpoSettingArea.EventAudit, SecurityTemplateDocument.EventAudit, "AuditLogonEvents"), (audit.Area, audit.Section, audit.Name));

        var right = Parse("Computer/Security/Privilege Rights/SeDenyNetworkLogonRight");
        Assert.Equal((GpoSettingArea.UserRight, SecurityTemplateDocument.PrivilegeRights, "SeDenyNetworkLogonRight"), (right.Area, right.Section, right.Name));
        Assert.Equal(GpoSettingArea.UserRight, Parse("Computer/Security/Privilege Rights/SeServiceLogonRight").Area);

        var members = Parse("Computer/Security/Group Membership/S-1-5-32-544/Members");
        Assert.Equal((GpoSettingArea.RestrictedGroup, SecurityTemplateDocument.GroupMembership, "S-1-5-32-544", "Members"),
            (members.Area, members.Section, members.Name, members.Field));
        Assert.Equal("MemberOf", Parse("Computer/Security/Group Membership/S-1-5-32-544/MemberOf").Field);
        var byName = Parse(@"Computer/Security/Group Membership/CONTOSO\Pomoc Techniczna/Members");
        Assert.Equal((@"CONTOSO\Pomoc Techniczna", "Members"), (byName.Name, byName.Field));
        // A line without "__Members"/"__Memberof" is kept under its raw key.
        var raw = Parse("Computer/Security/Group Membership/Administratorzy bez separatora");
        Assert.Equal((GpoSettingArea.RestrictedGroup, "Administratorzy bez separatora", (string?)null), (raw.Area, raw.Name, raw.Field));
    }

    [Fact]
    public void SecurityRegistryValues_MapToTheCatalogsMachinePaths()
    {
        var noLmHash = Parse(Rv + @"HKLM\System\CurrentControlSet\Control\Lsa!NoLMHash");
        Assert.Equal((GpoSettingArea.SecurityRegistryValue, SecurityTemplateDocument.RegistryValues, @"HKLM\System\CurrentControlSet\Control\Lsa", "NoLMHash"),
            (noLmHash.Area, noLmHash.Section, noLmHash.RegistryKey, noLmHash.ValueName));
        Assert.Equal("NoLMHash", noLmHash.Option!.Id);
        Assert.Equal(@"MACHINE\System\CurrentControlSet\Control\Lsa\NoLMHash", noLmHash.Option.Key);

        Assert.Equal("ServerRequireSecuritySignature", Parse(Rv + @"HKLM\System\CurrentControlSet\Services\LanManServer\Parameters!RequireSecuritySignature").Option!.Id);
        var timeout = Parse(Rv + @"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System!InactivityTimeoutSecs").Option!;
        Assert.Equal(("InactivityTimeoutSecs", SecurityValueUnit.Seconds), (timeout.Id, timeout.Unit));
        Assert.Equal("LegalNoticeText", Parse(Rv + @"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System!LegalNoticeText").Option!.Id);

        // Secrets: the masked value keeps its name; hidden value names (SNMP communities) link nothing.
        var password = Parse(Rv + @"HKLM\Software\Microsoft\Windows NT\CurrentVersion\Winlogon!DefaultPassword");
        Assert.Equal(("DefaultPassword", (SecurityOptionDefinition?)null), (password.ValueName, password.Option));
        var hidden = Parse(Rv + @"HKLM\System\CurrentControlSet\Services\SNMP\Parameters\ValidCommunities!*");
        Assert.Equal(("*", (SecurityOptionDefinition?)null), (hidden.ValueName, hidden.Option));
        Assert.Null(Parse(Rv + @"HKLM\Software\Contoso\Łódź!Ustawienie").Option);
    }

    [Fact]
    public void Services_ObjectSecurity_EventLogs_AndOtherSections()
    {
        var spooler = Parse("Computer/Security/Services/Spooler/StartupMode");
        Assert.Equal((GpoSettingArea.Service, SecurityTemplateDocument.ServiceGeneralSetting, "Spooler", "StartupMode"),
            (spooler.Area, spooler.Section, spooler.Name, spooler.Field));
        Assert.Equal("Security", Parse("Computer/Security/Services/RemoteRegistry/Security").Field);

        var registryKey = Parse(@"Computer/Security/Registry Keys/HKLM\SOFTWARE\Contoso/Mode");
        Assert.Equal((GpoSettingArea.RegistryKeySecurity, @"HKLM\SOFTWARE\Contoso", @"HKLM\SOFTWARE\Contoso", "Mode"),
            (registryKey.Area, registryKey.Name, registryKey.RegistryKey, registryKey.Field));

        var file = Parse(@"Computer/Security/File Security/%SystemRoot%\System32\drivers\etc\hosts/Security");
        Assert.Equal((GpoSettingArea.FileSecurity, "File Security", @"%SystemRoot%\System32\drivers\etc\hosts", "Security"),
            (file.Area, file.Section, file.Name, file.Field));

        var log = Parse("Computer/Security/Security Log/MaximumLogSize");
        Assert.Equal((GpoSettingArea.EventLog, "Security Log", "MaximumLogSize"), (log.Area, log.Section, log.Name));
        Assert.Equal("Application Log", Parse("Computer/Security/Application Log/RetentionDays").Section);

        var other = Parse("Computer/Security/Profil Łódzki/Klucz");
        Assert.Equal((GpoSettingArea.SecuritySection, "Profil Łódzki", "Klucz"), (other.Area, other.Section, other.Name));
    }

    [Fact]
    public void AdvancedAudit_Options_AndGlobalSacls()
    {
        var credentials = Parse("Computer/Audit/{0cce923f-69ae-11d9-bed3-505054503030}");
        Assert.Equal((GpoSettingArea.AdvancedAudit, "{0cce923f-69ae-11d9-bed3-505054503030}"), (credentials.Area, credentials.Name));
        Assert.Equal("CredentialValidation", credentials.Subcategory!.Id);
        // The parser writes GUIDs in lower case whatever the file holds.
        Assert.Equal("SecurityGroupManagement", Parse("Computer/Audit/{0cce9237-69ae-11d9-bed3-505054503030}").Subcategory!.Id);

        // Subcategory rows without a GUID: by the Subcategory column.
        var logon = Parse("Computer/Audit/System/Audit Logon");
        Assert.Equal((GpoSettingArea.AdvancedAudit, "System", "Audit Logon", "Logon"), (logon.Area, logon.Section, logon.Name, logon.Subcategory!.Id));
        var unknown = Parse("Computer/Audit/System/Audit Nieznana Podkategoria");
        Assert.Equal((GpoSettingArea.AdvancedAudit, (AuditSubcategoryDefinition?)null), (unknown.Area, unknown.Subcategory));
        // A subcategory whose name holds '/': the Policy Target ends at the first one.
        var slash = GpoSettingKey.Parse("Computer/Audit/System/Audit Other Logon/Logoff Events");
        Assert.Equal(("Audit Other Logon/Logoff Events", "OtherLogonLogoffEvents"), (slash.Name, slash.Subcategory!.Id));

        var option = Parse("Computer/Audit/Option/CrashOnAuditFail");
        Assert.Equal((GpoSettingArea.AuditOption, "CrashOnAuditFail"), (option.Area, option.Name));

        var sacl = Parse("Computer/Audit/File System/Global SACL");
        Assert.Equal((GpoSettingArea.GlobalSacl, "File System", "Global SACL"), (sacl.Area, sacl.Section, sacl.Name));
        Assert.Equal(("Registry", GpoSettingArea.GlobalSacl), (Parse("Computer/Audit/Registry/Global SACL").Section, Parse("Computer/Audit/Registry/Global SACL").Area));
    }

    [Fact]
    public void RegistryPol_ValuesKeysAndDirectives()
    {
        var value = Parse(MachinePol + @"Software\Policies\Microsoft\Windows\WindowsUpdate\AU!NoAutoUpdate");
        Assert.Equal((GpoScope.Computer, GpoSettingArea.PolicyRegistry, @"HKLM\Software\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate"),
            (value.Scope, value.Area, value.RegistryKey, value.ValueName));
        Assert.Null(value.Option); // Registry.pol is not the security template

        Assert.Equal(string.Empty, Parse(MachinePol + @"Software\Policies\Contoso\Pulpit!(Default)").ValueName);
        Assert.Equal("Usunięta", Parse(MachinePol + @"Software\Policies\Contoso\Stare!Usunięta").ValueName);
        Assert.Equal("Pierwsza", Parse(MachinePol + @"Software\Policies\Contoso\Stare!Pierwsza").ValueName);
        Assert.Equal("**delvals.", Parse(MachinePol + @"Software\Policies\Contoso\Lista!**delvals.").ValueName);
        Assert.Equal("Opóźnienie", Parse(MachinePol + @"Software\Policies\Contoso\Miękkie!Opóźnienie").ValueName);
        Assert.Equal("*", Parse(MachinePol + @"Software\Policies\SNMP\Parameters\ValidCommunities!*").ValueName);
        Assert.Equal("DefaultPassword", Parse(MachinePol + @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon!DefaultPassword").ValueName);

        // Keys without a value: a created and a deleted key.
        var created = Parse(MachinePol + @"Software\Policies\Contoso\Pusty");
        Assert.Equal((GpoSettingArea.PolicyRegistry, @"HKLM\Software\Policies\Contoso\Pusty", (string?)null), (created.Area, created.RegistryKey, created.ValueName));
        Assert.Null(Parse(MachinePol + @"Software\Policies\Contoso\Przestarzałe").ValueName);

        // The key path ends at the first '!': a value name may hold one.
        var bang = Parse(MachinePol + @"Software\Policies\Contoso\Przegladarka\ZaufaneWitryny!" + GpoContentSamples.BangValueName);
        Assert.Equal((@"HKLM\Software\Policies\Contoso\Przegladarka\ZaufaneWitryny", GpoContentSamples.BangValueName), (bang.RegistryKey, bang.ValueName));

        var user = Parse(@"User/Registry/HKCU\Software\Policies\Microsoft\Windows\Control Panel\Desktop!ScreenSaveTimeOut");
        Assert.Equal((GpoScope.User, GpoSettingArea.PolicyRegistry, @"HKCU\Software\Policies\Microsoft\Windows\Control Panel\Desktop"),
            (user.Scope, user.Area, user.RegistryKey));
    }

    [Fact]
    public void Preferences_ItemsTheirAttributesPropertiesChildrenAndFilters()
    {
        var name = Parse(GroupsItem + "@name");
        Assert.Equal((GpoScope.Computer, GpoSettingArea.Preference, "Groups", "{A1B2C3D4-0000-4000-8000-000000000002}", "@name", GroupsItem),
            (name.Scope, name.Area, name.PreferenceType, name.ItemId, name.Property, name.ItemKey));
        Assert.True(name.IsItemAttribute);
        Assert.Equal("@element", Parse(GroupsItem + "@element").Property);

        var groupSid = Parse(GroupsItem + "/groupSid");
        Assert.Equal(("groupSid", false), (groupSid.Property, groupSid.IsItemAttribute));
        Assert.Equal("Members", Parse(GroupsItem + "/Members").Property);
        Assert.Equal("Filters", Parse(GroupsItem + "/Filters").Property);

        // Every setting of one item shares its item key.
        var item = Sample.Where(s => s.Key.StartsWith(GroupsItem, StringComparison.Ordinal)).Select(s => GpoSettingKey.Parse(s.Key)).ToList();
        Assert.True(item.Count > 5);
        Assert.All(item, p => Assert.Equal(GroupsItem, p.ItemKey));

        // A secret: the masked password and the item's change time.
        var user = "Computer/Preferences/Groups/{A1B2C3D4-0000-4000-8000-000000000001}";
        Assert.Equal("cpassword", Parse(user + "/cpassword").Property);
        Assert.Equal("@changed", Parse(user + "@changed").Property);

        // Flattened children of a scheduled task.
        var task = "Computer/Preferences/ScheduledTasks/{B1B2C3D4-0000-4000-8000-000000000001}";
        Assert.Equal("Task/Settings/Enabled", Parse(task + "/Task/Settings/Enabled").Property);
        Assert.Equal("Task/Actions@Context", Parse(task + "/Task/Actions@Context").Property);
        Assert.Equal("Task@version", Parse(task + "/Task@version").Property);
        Assert.Equal("Triggers", Parse("Computer/Preferences/ScheduledTasks/{B1B2C3D4-0000-4000-8000-000000000002}/Triggers").Property);

        // An item without a uid, and a collection with an item inside.
        var service = Parse("Computer/Preferences/Services/NTService#1/startupType");
        Assert.Equal(("Services", "NTService#1", "startupType", "Computer/Preferences/Services/NTService#1"),
            (service.PreferenceType, service.ItemId, service.Property, service.ItemKey));
        Assert.Equal("@disabled", Parse("Computer/Preferences/Services/NTService#1@disabled").Property);
        Assert.Equal("@element", Parse("Computer/Preferences/Registry/{C1B2C3D4-0000-4000-8000-000000000009}@element").Property);
        Assert.Equal("value", Parse("Computer/Preferences/Registry/{C1B2C3D4-0000-4000-8000-000000000002}/value").Property);

        var shortcut = Parse("User/Preferences/Shortcuts/{D1B2C3D4-0000-4000-8000-000000000002}@desc");
        Assert.Equal((GpoScope.User, "Shortcuts", "@desc"), (shortcut.Scope, shortcut.PreferenceType, shortcut.Property));
    }

    [Fact]
    public void Scripts_ConfigAndScriptFiles()
    {
        var startup = Parse("Computer/Scripts/Startup/0");
        Assert.Equal((GpoSettingArea.Script, "Startup", 0, (string?)null, false), (startup.Area, startup.ScriptPhase, startup.Index, startup.Field, startup.PowerShell));
        var parameters = Parse("Computer/Scripts/Startup/0/Parameters");
        Assert.Equal((0, "Parameters"), (parameters.Index, parameters.Field));
        Assert.Equal(1, Parse("Computer/Scripts/Startup/1").Index);
        Assert.Equal(("Shutdown", 0), (Parse("Computer/Scripts/Shutdown/0").ScriptPhase, Parse("Computer/Scripts/Shutdown/0").Index));
        var other = Parse("Computer/Scripts/Shutdown/CosInnego");
        Assert.Equal((GpoSettingArea.Script, "Shutdown", "CosInnego", (int?)null), (other.Area, other.ScriptPhase, other.Name, other.Index));

        var powerShell = Parse("Computer/Scripts/PowerShell/Startup/0/Parameters");
        Assert.Equal((GpoSettingArea.Script, "Startup", 0, "Parameters", true),
            (powerShell.Area, powerShell.ScriptPhase, powerShell.Index, powerShell.Field, powerShell.PowerShell));
        var config = Parse("Computer/Scripts/PowerShell/Config/StartExecutePSFirst");
        Assert.Equal((GpoSettingArea.ScriptConfig, "StartExecutePSFirst", true), (config.Area, config.Name, config.PowerShell));

        var file = Parse("Computer/Scripts/Files/Startup/mapuj-dyski.cmd");
        Assert.Equal((GpoSettingArea.ScriptFile, "Startup/mapuj-dyski.cmd", "Startup"), (file.Area, file.Name, file.ScriptPhase));

        var logon = Parse("User/Scripts/Logon/0");
        Assert.Equal((GpoScope.User, GpoSettingArea.Script, "Logon", 0), (logon.Scope, logon.Area, logon.ScriptPhase, logon.Index));
    }

    [Fact]
    public void Comments_AndUnreadableFiles()
    {
        var comment = Parse("Computer/Comment/Microsoft.Policies.WindowsUpdate:AutoUpdateCfg");
        Assert.Equal((GpoScope.Computer, GpoSettingArea.Comment, "Microsoft.Policies.WindowsUpdate:AutoUpdateCfg"), (comment.Scope, comment.Area, comment.Name));
        var gpo = Parse("Gpo/Comment");
        Assert.Equal((GpoScope.Gpo, GpoSettingArea.Comment, (string?)null), (gpo.Scope, gpo.Area, gpo.Name));

        var unreadable = Parse("File/User/Preferences/Drives/Drives.xml");
        Assert.Equal((GpoScope.User, GpoSettingArea.UnreadableFile, "User/Preferences/Drives/Drives.xml"), (unreadable.Scope, unreadable.Area, unreadable.Name));
        Assert.Equal(GpoScope.Computer, GpoSettingKey.Parse("File/Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf").Scope);
        Assert.Equal((GpoScope.Gpo, GpoSettingArea.UnreadableFile), (GpoSettingKey.Parse("File/GPO.cmt").Scope, GpoSettingKey.Parse("File/GPO.cmt").Area));
    }

    [Fact]
    public void EveryCatalogOption_IsFoundFromTheKeyItsTemplateLineMakes()
    {
        var sink = new GpoSettingSink(GpoContentSamples.GptTmplPath);
        GptTmplParser.Parse(GpoContentSamples.CatalogTemplate(), sink);

        var linked = sink.Settings.Select(s => GpoSettingKey.Parse(s.Key)).Where(p => p.Option is not null).ToList();

        Assert.Equal(SecuritySettingsCatalog.Options.Count, linked.Count);
        Assert.Equal(SecuritySettingsCatalog.Options.Select(o => o.Id).Order(), linked.Select(p => p.Option!.Id).Order());
        Assert.All(linked, p => Assert.Same(SecuritySettingsCatalog.FindOption(p.Option!.Section, p.Option.Key), p.Option));
    }

    [Fact]
    public void EveryCatalogSubcategory_IsFoundFromItsKey()
    {
        var sink = new GpoSettingSink(GpoContentSamples.AuditPath);
        AuditCsvParser.Parse(GpoContentSamples.CatalogAuditCsv(), sink);

        var parsed = sink.Settings.Select(s => GpoSettingKey.Parse(s.Key)).ToList();

        Assert.Equal(SecuritySettingsCatalog.AuditSubcategories.Count, parsed.Count);
        Assert.All(parsed, p => Assert.Equal(GpoSettingArea.AdvancedAudit, p.Area));
        Assert.Equal(SecuritySettingsCatalog.AuditSubcategories, parsed.Select(p => p.Subcategory));
        Assert.All(SecuritySettingsCatalog.AuditSubcategories, s => Assert.Same(s, SecuritySettingsCatalog.FindSubcategory(s.CsvName)));
        Assert.All(SecuritySettingsCatalog.AuditSubcategories, s => Assert.Same(s, SecuritySettingsCatalog.FindSubcategory(s.Name.ToUpperInvariant())));
    }

    [Fact]
    public void CatalogUnits_AreSetForEveryNumberWindowsShowsWithAUnit()
    {
        var units = SecuritySettingsCatalog.Options.Where(o => o.Unit != SecurityValueUnit.None).ToDictionary(o => o.Id, o => o.Unit);

        Assert.Equal(new Dictionary<string, SecurityValueUnit>
        {
            ["PasswordHistorySize"] = SecurityValueUnit.Passwords,
            ["MaximumPasswordAge"] = SecurityValueUnit.Days,
            ["MinimumPasswordAge"] = SecurityValueUnit.Days,
            ["MinimumPasswordLength"] = SecurityValueUnit.Characters,
            ["LockoutBadCount"] = SecurityValueUnit.Attempts,
            ["LockoutDuration"] = SecurityValueUnit.Minutes,
            ["ResetLockoutCount"] = SecurityValueUnit.Minutes,
            ["MaxTicketAge"] = SecurityValueUnit.Hours,
            ["MaxRenewAge"] = SecurityValueUnit.Days,
            ["MaxServiceAge"] = SecurityValueUnit.Minutes,
            ["MaxClockSkew"] = SecurityValueUnit.Minutes,
            ["MaximumPasswordAgeMachine"] = SecurityValueUnit.Days,
            ["InactivityTimeoutSecs"] = SecurityValueUnit.Seconds,
        }, units);
        Assert.All(SecuritySettingsCatalog.Options.Where(o => o.Unit != SecurityValueUnit.None), o => Assert.Equal(SecurityValueKind.Number, o.Kind));
    }

    [Fact]
    public void CutKeys_AreParsedFromTheirKeptStart_AndLinkNothing()
    {
        // A Registry.pol key longer than the key limit: the value name falls into the cut part.
        var longKey = @"Software\Policies\Contoso\" + string.Concat(Enumerable.Repeat(@"Gałąź\", 80)) + "Koniec";
        var sink = new GpoSettingSink("Machine/Registry.pol");
        RegistryPolParser.Parse(RegistryPolFile.Write([new(longKey, "Wartość", RegistryFormat.RegDword, RegistryPolFile.DwordData(1))]), GpoScope.Computer, sink);
        var key = Assert.Single(sink.Settings).Key;
        Assert.Equal(GpoValues.MaxKeyLength, key.Length);

        var parsed = GpoSettingKey.Parse(key);

        Assert.True(parsed.Truncated);
        Assert.True(GpoSettingKey.IsTruncated(key));
        Assert.Equal((GpoScope.Computer, GpoSettingArea.PolicyRegistry), (parsed.Scope, parsed.Area));
        Assert.StartsWith(@"HKLM\Software\Policies\Contoso\Gałąź\", parsed.RegistryKey, StringComparison.Ordinal);
        Assert.EndsWith("…", parsed.RegistryKey, StringComparison.Ordinal);
        Assert.DoesNotContain("#", parsed.RegistryKey, StringComparison.Ordinal);

        // A preference attribute with a very long name: the item is still found.
        var attribute = new string('a', 600);
        var xml = $"<?xml version=\"1.0\"?><Files clsid=\"{{1}}\"><File clsid=\"{{2}}\" name=\"plik\" uid=\"{{E1B2C3D4-0000-4000-8000-000000000001}}\"><Properties action=\"C\" {attribute}=\"1\"/></File></Files>";
        var files = new GpoSettingSink("Machine/Preferences/Files/Files.xml");
        GppXmlParser.Parse(GpoContentSamples.Utf8Bom(xml), GpoScope.Computer, "Files", files);
        var cut = GpoSettingKey.Parse(Assert.Single(files.Settings, s => s.Key.Length == GpoValues.MaxKeyLength).Key);
        Assert.Equal((true, GpoSettingArea.Preference, "{E1B2C3D4-0000-4000-8000-000000000001}"), (cut.Truncated, cut.Area, cut.ItemId));
        Assert.StartsWith("aaaa", cut.Property, StringComparison.Ordinal);
        Assert.EndsWith("…", cut.Property, StringComparison.Ordinal);

        // A security option key that is cut is never linked to the catalog.
        var option = GpoSettingKey.Parse((Sa + "MinimumPasswordLength" + new string('x', 600))[..(GpoValues.MaxKeyLength - 18)] + "…#0123456789abcdef");
        Assert.True(option.Truncated);
        Assert.Null(option.Option);

        // Exactly the limit without the suffix shape is a normal key.
        Assert.False(GpoSettingKey.Parse(Sa + new string('y', GpoValues.MaxKeyLength - Sa.Length)).Truncated);
        Assert.False(GpoSettingKey.IsTruncated((Sa + new string('z', 600))[..(GpoValues.MaxKeyLength - 18)] + "…#0123456789ABCDEF"));
    }

    [Fact]
    public void Keys_AreReadCaseInsensitively()
    {
        var length = GpoSettingKey.Parse("computer/SECURITY/system access/minimumpasswordlength");
        Assert.Equal((GpoScope.Computer, GpoSettingArea.SystemAccess, "MinimumPasswordLength"), (length.Scope, length.Area, length.Option!.Id));
        Assert.Equal(GpoSettingArea.PolicyRegistry, GpoSettingKey.Parse(@"USER/registry/HKCU\Software!x").Area);
        Assert.Equal(GpoSettingArea.UnreadableFile, GpoSettingKey.Parse("file/machine/Registry.pol").Area);
    }

    [Theory]
    [InlineData("", GpoScope.Gpo, GpoSettingArea.Other)]
    [InlineData("/", GpoScope.Gpo, GpoSettingArea.Other)]
    [InlineData("//", GpoScope.Gpo, GpoSettingArea.Other)]
    [InlineData("Computer", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("Computer/", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("Computer/Security", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("Computer/Security/", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("Computer/Security/System Access", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("Computer/Security/System Access/", GpoScope.Computer, GpoSettingArea.SystemAccess)]
    [InlineData("Computer/Security/Registry Values/", GpoScope.Computer, GpoSettingArea.SecurityRegistryValue)]
    [InlineData("Computer/Audit/", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("Computer/Audit/{nie-guid}", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("Computer/Registry/", GpoScope.Computer, GpoSettingArea.PolicyRegistry)]
    [InlineData("Computer/Preferences/", GpoScope.Computer, GpoSettingArea.Preference)]
    [InlineData("Computer/Preferences/Groups/", GpoScope.Computer, GpoSettingArea.Preference)]
    [InlineData("Computer/Preferences/Groups/{niedomknięty/x", GpoScope.Computer, GpoSettingArea.Preference)]
    [InlineData("Computer/Scripts/", GpoScope.Computer, GpoSettingArea.Script)]
    [InlineData("Computer/Scripts/PowerShell/", GpoScope.Computer, GpoSettingArea.Script)]
    [InlineData("Computer/Scripts/Startup/99999999999999999999", GpoScope.Computer, GpoSettingArea.Script)]
    [InlineData("Computer/Comment", GpoScope.Computer, GpoSettingArea.Comment)]
    [InlineData("Computer/Coś/innego", GpoScope.Computer, GpoSettingArea.Other)]
    [InlineData("User/Nieznane", GpoScope.User, GpoSettingArea.Other)]
    [InlineData("Gpo", GpoScope.Gpo, GpoSettingArea.Other)]
    [InlineData("File/", GpoScope.Gpo, GpoSettingArea.UnreadableFile)]
    [InlineData("Nieznany/klucz/ąę", GpoScope.Gpo, GpoSettingArea.Other)]
    [InlineData("core:status", GpoScope.Gpo, GpoSettingArea.Other)]
    public void KeysOfNoKnownShape_NeverThrow(string key, GpoScope scope, GpoSettingArea area)
    {
        var parsed = GpoSettingKey.Parse(key);

        Assert.Equal((key, scope, area), (parsed.Key, parsed.Scope, parsed.Area));
    }

    [Fact]
    public void ScriptIndexes_OutOfRange_StayNames()
    {
        var parsed = GpoSettingKey.Parse("Computer/Scripts/Startup/99999999999999999999");

        Assert.Equal(("99999999999999999999", (int?)null), (parsed.Name, parsed.Index));
    }

    [Fact]
    public void RandomKeys_NeverThrow()
    {
        var random = new Random(20261103);
        const string Alphabet = "/!@{}#…\\*:ąęŁ CmputerUsSecurityAuditRegistryPreferencesScriptsFile0123456789";
        var prefixes = new[] { string.Empty, "Computer/", "User/", "Gpo/", "File/", "Computer/Security/", "Computer/Audit/", "Computer/Registry/", "User/Preferences/", "Computer/Scripts/" };
        for (var i = 0; i < 2000; i++)
        {
            var text = new StringBuilder(prefixes[random.Next(prefixes.Length)]);
            var length = random.Next(0, i % 50 == 0 ? 600 : 40);
            for (var j = 0; j < length; j++)
            {
                text.Append(Alphabet[random.Next(Alphabet.Length)]);
            }

            var key = text.ToString();
            Assert.Equal(key, GpoSettingKey.Parse(key).Key);
        }
    }

    /// <summary>The parts of a key the sample's parsers really made.</summary>
    private static GpoSettingKeyInfo Parse(string key)
    {
        Assert.Contains(Sample, s => s.Key == key);
        return GpoSettingKey.Parse(key);
    }
}
