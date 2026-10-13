using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.PropertySheets;

/// <summary>Builds the pages of a property sheet per object kind, in ADUC's order and with ADUC's names.</summary>
internal static class PropertyPageFactory
{
    /// <summary>Attributes always read by a sheet (header, computed fields, Object page, permissions of pages).</summary>
    public static IReadOnlyList<string> BaseAttributes { get; } =
    [
        Attr.ObjectClass, Attr.Name, Attr.DisplayName, Attr.DistinguishedName, Attr.CanonicalName, Attr.ObjectGuid,
        Attr.ObjectSid, Attr.WhenCreated, Attr.WhenChanged, Attr.UsnCreated, Attr.UsnChanged, Attr.UserAccountControl,
        Attr.UserAccountControlComputed, Attr.LockoutTime, Attr.PwdLastSet, Attr.GroupType, Attr.SamAccountName,
        Attr.DnsHostName, Attr.OperatingSystem, Attr.OperatingSystemVersion, Attr.OperatingSystemServicePack,
        Attr.AllowedToDelegateTo, Attr.ProxyAddresses, Attr.IsCriticalSystemObject, Attr.AdminCount,
    ];

    private static readonly ObjectKind[] PersonKinds = [ObjectKind.User, ObjectKind.Contact];
    private static readonly ObjectKind[] ManagerKinds = [ObjectKind.User, ObjectKind.Contact, ObjectKind.Group];

    public static List<PropertyPageViewModel> Create(FieldContext context, DirectoryObject obj)
    {
        var builder = new Builder(context);
        return obj.Kind switch
        {
            ObjectKind.User or ObjectKind.InetOrgPerson => builder.User(obj),
            ObjectKind.Computer => builder.Computer(),
            ObjectKind.Group => builder.Group(),
            ObjectKind.Contact => builder.Contact(obj),
            ObjectKind.OrganizationalUnit => builder.OrganizationalUnit(),
            _ => builder.Other(),
        };
    }

    private sealed class Builder(FieldContext context)
    {
        private ILocalizer Localizer => context.Localizer;

        public List<PropertyPageViewModel> User(DirectoryObject obj)
        {
            var pages = new List<PropertyPageViewModel>
            {
                Page(L.Pages.General, Section(null,
                    Text(Attr.GivenName), Text(Attr.Initials), Text(Attr.Sn), Text(Attr.DisplayName), Text(Attr.Description),
                    Text(Attr.Office), Text(Attr.TelephoneNumber, others: Attr.OtherTelephone), Text(Attr.Mail),
                    Text(Attr.WwwHomePage, others: Attr.Url))),
                AddressPage(),
                AccountPage(),
                Page(L.Pages.Profile,
                    Section(Localizer[L.Labels.UserProfile], Text(Attr.ProfilePath), Text(Attr.ScriptPath)),
                    Section(Localizer[L.Labels.HomeFolder], context.IsKnown(Attr.HomeDirectory) ? new HomeFolderFieldViewModel(context) : null)),
                TelephonesPage(),
                OrganizationPage(),
                new MemberOfPageViewModel(context),
            };
            AddEmailPage(pages, obj);
            pages.Add(ObjectPage());
            pages.Add(new AttributeEditorPageViewModel(context));
            return pages;
        }

        public List<PropertyPageViewModel> Contact(DirectoryObject obj)
        {
            var pages = new List<PropertyPageViewModel>
            {
                Page(L.Pages.General, Section(null,
                    Text(Attr.GivenName), Text(Attr.Initials), Text(Attr.Sn), Text(Attr.DisplayName), Text(Attr.Description),
                    Text(Attr.Office), Text(Attr.TelephoneNumber, others: Attr.OtherTelephone), Text(Attr.Mail),
                    Text(Attr.WwwHomePage, others: Attr.Url))),
                AddressPage(),
                TelephonesPage(),
                OrganizationPage(),
                new MemberOfPageViewModel(context),
            };
            AddEmailPage(pages, obj);
            pages.Add(ObjectPage());
            pages.Add(new AttributeEditorPageViewModel(context));
            return pages;
        }

        public List<PropertyPageViewModel> Computer() =>
        [
            Page(L.Pages.General, Section(null,
                Info(Attr.Name, Localizer[L.Labels.ComputerName], o => o.Name),
                Info(Attr.SamAccountName, Localizer[L.Labels.ComputerNamePre2000], o => (o.GetString(Attr.SamAccountName) ?? string.Empty).TrimEnd('$')),
                Info(Attr.DnsHostName, context.Label(Attr.DnsHostName), o => o.GetString(Attr.DnsHostName) ?? string.Empty),
                Info(Attr.UserAccountControl, Localizer[L.Labels.ComputerRole], RoleText),
                Text(Attr.Description))),
            Page(L.Pages.OperatingSystem, Section(null,
                Info(Attr.OperatingSystem, context.Label(Attr.OperatingSystem), o => o.GetString(Attr.OperatingSystem) ?? string.Empty),
                Info(Attr.OperatingSystemVersion, context.Label(Attr.OperatingSystemVersion), o => o.GetString(Attr.OperatingSystemVersion) ?? string.Empty),
                Info(Attr.OperatingSystemServicePack, context.Label(Attr.OperatingSystemServicePack), o => o.GetString(Attr.OperatingSystemServicePack) ?? string.Empty))),
            new MemberOfPageViewModel(context),
            Page(L.Pages.Delegation, Section(null,
                Info(Attr.UserAccountControl, Localizer[L.Labels.Delegation], DelegationText),
                Info(Attr.AllowedToDelegateTo, context.Label(Attr.AllowedToDelegateTo), o => string.Join(Environment.NewLine, o.GetStrings(Attr.AllowedToDelegateTo)), multiline: true))),
            Page(L.Pages.Location, Section(null, Text(Attr.Location))),
            ManagedByPage(),
            ObjectPage(),
            new AttributeEditorPageViewModel(context),
        ];

        public List<PropertyPageViewModel> Group() =>
        [
            Page(L.Pages.General, Section(null,
                Info(Attr.Name, Localizer[L.Labels.GroupName], o => o.Name),
                Text(Attr.SamAccountName, Localizer[L.Labels.GroupNamePre2000]),
                Text(Attr.Description),
                Text(Attr.Mail),
                context.IsKnown(Attr.GroupType) ? new GroupTypeFieldViewModel(context) : null,
                Text(Attr.Info, multiline: true))),
            new MembersPageViewModel(context),
            new MemberOfPageViewModel(context),
            ManagedByPage(),
            ObjectPage(),
            new AttributeEditorPageViewModel(context),
        ];

        public List<PropertyPageViewModel> OrganizationalUnit() =>
        [
            Page(L.Pages.General, Section(null,
                Text(Attr.Description), Text(Attr.StreetAddress, multiline: true), Text(Attr.City), Text(Attr.State),
                Text(Attr.PostalCode), Country())),
            ManagedByPage(),
            ObjectPage(),
            new AttributeEditorPageViewModel(context),
        ];

        public List<PropertyPageViewModel> Other() =>
        [
            Page(L.Pages.General, Section(null, Text(Attr.Description))),
            ObjectPage(),
            new AttributeEditorPageViewModel(context),
        ];

        private FieldsPageViewModel AddressPage() =>
            Page(L.Pages.Address, Section(null,
                Text(Attr.StreetAddress, multiline: true), Text(Attr.PostOfficeBox), Text(Attr.City), Text(Attr.State),
                Text(Attr.PostalCode), Country()));

        private FieldsPageViewModel AccountPage()
        {
            var unlock = Known(Attr.LockoutTime, () => new CheckFieldViewModel(
                context,
                Attr.LockoutTime,
                Localizer[L.Labels.UnlockAccount],
                _ => false,
                value => value ? [new UnlockAccountOperation()] : [],
                visible: o => o.IsLockedOut));
            var mustChange = Known(Attr.PwdLastSet, () => new CheckFieldViewModel(
                context,
                Attr.PwdLastSet,
                Localizer[L.Labels.MustChangePassword],
                o => o.GetString(Attr.PwdLastSet) == "0",
                value => [new RequirePasswordChangeOperation(value)]));
            return Page(L.Pages.Account,
                Section(null,
                    Known(Attr.UserPrincipalName, () => new UpnFieldViewModel(context)),
                    Text(Attr.SamAccountName),
                    unlock),
                Section(Localizer[L.Labels.AccountOptions],
                    mustChange,
                    Flag(UserAccountControl.DontExpirePassword),
                    Flag(UserAccountControl.EncryptedTextPasswordAllowed),
                    Flag(UserAccountControl.AccountDisable),
                    Flag(UserAccountControl.SmartcardRequired),
                    Flag(UserAccountControl.NotDelegated),
                    Flag(UserAccountControl.UseDesKeyOnly),
                    Known(Attr.SupportedEncryptionTypes, () => new EncryptionTypesFieldViewModel(context)),
                    Flag(UserAccountControl.DontRequirePreauth)),
                Section(Localizer[L.Labels.AccountExpires], Known(Attr.AccountExpires, () => new ExpiryFieldViewModel(context))),
                Section(Localizer[L.Labels.LogOnTo], Known(Attr.UserWorkstations, () => new WorkstationsFieldViewModel(context))));
        }

        private FieldsPageViewModel TelephonesPage() =>
            Page(L.Pages.Telephones,
                Section(null,
                    Text(Attr.HomePhone, others: Attr.OtherHomePhone),
                    Text(Attr.Pager, others: Attr.OtherPager),
                    Text(Attr.Mobile, others: Attr.OtherMobile),
                    Text(Attr.FacsimileTelephoneNumber, others: Attr.OtherFacsimileTelephoneNumber),
                    Text(Attr.IpPhone, others: Attr.OtherIpPhone)),
                Section(context.Label(Attr.Info), Text(Attr.Info, multiline: true)));

        private FieldsPageViewModel OrganizationPage() =>
            Page(L.Pages.Organization, Section(null,
                Text(Attr.Title), Text(Attr.Department), Text(Attr.Company),
                Known(Attr.Manager, () => new DnFieldViewModel(context, Attr.Manager, PersonKinds)),
                Known(Attr.DirectReports, () => new ReferenceListFieldViewModel(context, Attr.DirectReports))));

        private FieldsPageViewModel ManagedByPage() =>
            Page(L.Pages.ManagedBy, Section(null,
                Known(Attr.ManagedBy, () => new DnFieldViewModel(context, Attr.ManagedBy, ManagerKinds, showDetails: true))));

        private void AddEmailPage(List<PropertyPageViewModel> pages, DirectoryObject obj)
        {
            if (!context.Session.Info.HasExchangeSchema && !obj.Has(Attr.ProxyAddresses))
            {
                return;
            }

            var mail = Text(Attr.Mail);
            var proxies = Known(Attr.ProxyAddresses, () => new ProxyAddressesFieldViewModel(context));
            if (proxies is not null && mail is not null)
            {
                // Exchange keeps mail equal to the primary SMTP address.
                proxies.PrimarySmtpChanged += (_, address) =>
                {
                    if (address is not null && mail.IsEditable)
                    {
                        mail.Value = address;
                    }
                };
            }

            pages.Add(Page(L.Pages.EmailAddresses, Section(null,
                mail,
                Text(Attr.MailNickname),
                proxies,
                Text(Attr.TargetAddress),
                Known(Attr.HideFromAddressLists, () => new CheckFieldViewModel(
                    context,
                    Attr.HideFromAddressLists,
                    context.Label(Attr.HideFromAddressLists),
                    o => o.GetBoolean(Attr.HideFromAddressLists) == true,
                    value => [new SetValuesOperation(Attr.HideFromAddressLists, value ? ["TRUE"] : [])])))));
        }

        private FieldsPageViewModel ObjectPage() =>
            Page(L.Pages.Object, Section(null,
                Info(Attr.CanonicalName, Localizer[L.Labels.CanonicalName], o => o.GetString(Attr.CanonicalName) ?? o.Dn.ToCanonicalName(), copy: true),
                Info(Attr.ObjectClass, context.Label(Attr.ObjectClass), o => o.GetStrings(Attr.ObjectClass).LastOrDefault() ?? string.Empty, copy: true),
                Info(Attr.WhenCreated, context.Label(Attr.WhenCreated), o => Formatted(o, Attr.WhenCreated), copy: true),
                Info(Attr.WhenChanged, context.Label(Attr.WhenChanged), o => Formatted(o, Attr.WhenChanged), copy: true),
                Info(Attr.UsnCreated, Localizer[L.Labels.UsnOriginal], o => o.GetString(Attr.UsnCreated) ?? string.Empty, copy: true),
                Info(Attr.UsnChanged, Localizer[L.Labels.UsnCurrent], o => o.GetString(Attr.UsnChanged) ?? string.Empty, copy: true),
                Info(Attr.ObjectGuid, context.Label(Attr.ObjectGuid), o => o.ObjectGuid?.ToString("D") ?? string.Empty, copy: true),
                Info(Attr.ObjectSid, context.Label(Attr.ObjectSid), o => o.ObjectSid ?? string.Empty, copy: true),
                Info(Attr.DistinguishedName, context.Label(Attr.DistinguishedName), o => o.DistinguishedName, copy: true)));

        private string Formatted(DirectoryObject obj, string attribute) =>
            obj.TryGet(attribute, out var values) ? context.Formatter.Format(attribute, values, context.Culture) : string.Empty;

        private string RoleText(DirectoryObject obj)
        {
            var uac = (UserAccountControl)(obj.GetInt32(Attr.UserAccountControl) ?? 0);
            return uac.HasFlag(UserAccountControl.PartialSecretsAccount) ? Localizer[L.Labels.RoleReadOnlyDomainController]
                : uac.HasFlag(UserAccountControl.ServerTrustAccount) ? Localizer[L.Labels.RoleDomainController]
                : Localizer[L.Labels.RoleWorkstation];
        }

        private string DelegationText(DirectoryObject obj)
        {
            var uac = (UserAccountControl)(obj.GetInt32(Attr.UserAccountControl) ?? 0);
            if (uac.HasFlag(UserAccountControl.TrustedForDelegation))
            {
                return Localizer[L.Labels.DelegationUnconstrained];
            }

            if (obj.GetStrings(Attr.AllowedToDelegateTo).Count > 0)
            {
                return uac.HasFlag(UserAccountControl.TrustedToAuthForDelegation)
                    ? Localizer[L.Labels.DelegationConstrainedAnyProtocol]
                    : Localizer[L.Labels.DelegationConstrainedKerberos];
            }

            return Localizer[L.Labels.DelegationNone];
        }

        private FieldsPageViewModel Page(string key, params FieldSectionViewModel?[] sections) =>
            new(context, key, Localizer[key], [.. sections.OfType<FieldSectionViewModel>()]);

        private static FieldSectionViewModel? Section(string? header, params PropertyFieldViewModel?[] fields)
        {
            var present = fields.OfType<PropertyFieldViewModel>().ToList();
            return present.Count == 0 ? null : new FieldSectionViewModel(header, present);
        }

        private FieldViewModel? Text(string attribute, string? label = null, string? others = null, bool multiline = false)
        {
            if (!context.IsKnown(attribute))
            {
                return null;
            }

            return new FieldViewModel(context, attribute, label)
            {
                IsMultiline = multiline,
                Others = others is not null && context.IsKnown(others) ? new MultiValueFieldViewModel(context, others) : null,
            };
        }

        private FieldViewModel Info(string attribute, string label, Func<DirectoryObject, string> reader, bool copy = false, bool multiline = false) =>
            new(context, attribute, label, reader) { CanCopy = copy, IsMultiline = multiline };

        private FlagFieldViewModel? Flag(UserAccountControl flag) =>
            context.IsKnown(Attr.UserAccountControl) ? new FlagFieldViewModel(context, flag) : null;

        private CountryFieldViewModel? Country() => context.IsKnown(Attr.Country) ? new CountryFieldViewModel(context) : null;

        private T? Known<T>(string attribute, Func<T> create)
            where T : PropertyFieldViewModel =>
            context.IsKnown(attribute) ? create() : null;
    }
}
