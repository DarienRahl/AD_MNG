using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Core.Pki;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>
/// <i>Mapowania nazw</i> (P1, docs/dev/p-certificate-services.md 2.6) of a user or computer: the explicit certificate
/// mappings of <c>altSecurityIdentities</c> with their KB5014754 strength (<see cref="AltSecurityIdentity"/>: issuer + serial
/// number, SKI and SHA1 public key are strong; issuer + subject, subject and e-mail are weak), and the <c>Kerberos:</c> names
/// apart (ADUC's two tabs). The attribute is small, so it comes with the sheet's main read. Read only (rule 5).
/// </summary>
public sealed partial class NameMappingsPageViewModel : PropertyPageViewModel
{
    public const string PageKey = L.Certificates.NameMappingsPage;

    internal NameMappingsPageViewModel(FieldContext context)
        : base(context, PageKey, context.Localizer[PageKey])
    {
    }

    /// <summary>Every value that is not a <c>Kerberos:</c> name, in server order (invalid values included).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasX509Mappings), nameof(WeakCount), nameof(WeakWarning), nameof(InvalidWarning))]
    public partial IReadOnlyList<NameMappingRowViewModel> X509Mappings { get; private set; } = [];

    /// <summary>The <c>Kerberos:</c> names.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKerberosNames))]
    public partial IReadOnlyList<NameMappingRowViewModel> KerberosNames { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyValueCommand))]
    public partial NameMappingRowViewModel? SelectedMapping { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyValueCommand))]
    public partial NameMappingRowViewModel? SelectedKerberosName { get; set; }

    public bool HasX509Mappings => X509Mappings.Count > 0;

    public bool HasKerberosNames => KerberosNames.Count > 0;

    /// <summary>The weak mappings (refused under full enforcement of strong certificate binding).</summary>
    public int WeakCount => X509Mappings.Count(m => m.Strength == MappingStrength.Weak);

    /// <summary>The KB5014754 warning when weak mappings exist, else empty.</summary>
    public string WeakWarning => WeakCount > 0 ? Context.Localizer.Format(L.Certificates.WeakWarning, WeakCount) : string.Empty;

    /// <summary>The note on values the KDC ignores, else empty.</summary>
    public string InvalidWarning => X509Mappings.Count(m => m.Strength == MappingStrength.Invalid) is var invalid and > 0
        ? Context.Localizer.Format(L.Certificates.InvalidWarning, invalid)
        : string.Empty;

    internal override IEnumerable<string> ReadAttributes => [PkiAttributes.AltSecurityIdentities];

    public override void Revert()
    {
    }

    internal override void Load(DirectoryObject obj)
    {
        var parsed = obj.GetStrings(PkiAttributes.AltSecurityIdentities).Select(AltSecurityIdentity.Parse).ToList();
        var mapping = SelectedMapping?.Value;
        var name = SelectedKerberosName?.Value;
        X509Mappings = [.. parsed.Where(p => p.Kind != AltSecurityIdentityKind.Kerberos).Select(p => new NameMappingRowViewModel(Context, p))];
        KerberosNames = [.. parsed.Where(p => p.Kind == AltSecurityIdentityKind.Kerberos).Select(p => new NameMappingRowViewModel(Context, p))];
        SelectedMapping = X509Mappings.FirstOrDefault(m => string.Equals(m.Value, mapping, StringComparison.Ordinal));
        SelectedKerberosName = KerberosNames.FirstOrDefault(m => string.Equals(m.Value, name, StringComparison.Ordinal));
    }

    internal override IEnumerable<BatchOperation> GetOperations() => [];

    protected override bool ComputeHasChanges() => false;

    protected override bool ComputeHasErrors() => false;

    /// <summary>"Kopiuj": the value as stored of the given row (or the selected mapping, else the selected Kerberos name).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyValue))]
    private void CopyValue(NameMappingRowViewModel? row)
    {
        if ((row ?? SelectedMapping ?? SelectedKerberosName) is { } target)
        {
            ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Context.Localizer, target.Value, Context.Localizer[L.Certificates.ColumnValue]);
        }
    }

    private bool CanCopyValue(NameMappingRowViewModel? row) => (row ?? SelectedMapping ?? SelectedKerberosName) is not null;
}

/// <summary>One <c>altSecurityIdentities</c> value of <see cref="NameMappingsPageViewModel"/>.</summary>
public sealed class NameMappingRowViewModel
{
    internal NameMappingRowViewModel(FieldContext context, AltSecurityIdentity mapping)
    {
        var localizer = context.Localizer;
        Value = mapping.Value;
        Kind = mapping.Kind;
        Strength = mapping.Strength;
        KindText = localizer[PkiTextKeys.MappingKindKey(mapping.Kind)];
        StrengthText = localizer[PkiTextKeys.MappingKey(mapping.Strength)];
        Issuer = mapping.Issuer ?? string.Empty;
        SubjectOrIdentifier = mapping.Kind switch
        {
            AltSecurityIdentityKind.X509IssuerSerial when mapping.SerialNumber is { } serial => localizer.Format(L.Certificates.SerialNumber, serial),
            AltSecurityIdentityKind.X509IssuerSubject or AltSecurityIdentityKind.X509SubjectOnly => mapping.Subject ?? string.Empty,
            _ => mapping.Identifier ?? string.Empty,
        };
    }

    /// <summary>The value as stored.</summary>
    public string Value { get; }

    public AltSecurityIdentityKind Kind { get; }

    public MappingStrength Strength { get; }

    /// <summary><i>Wystawca i numer seryjny</i>, <i>Sam podmiot</i>…</summary>
    public string KindText { get; }

    /// <summary><i>Silne</i>, <i>Słabe</i>, <i>Nieprawidłowe</i>…</summary>
    public string StrengthText { get; }

    public bool IsStrong => Strength == MappingStrength.Strong;

    public bool IsWeak => Strength == MappingStrength.Weak;

    public bool IsInvalid => Strength == MappingStrength.Invalid;

    /// <summary>The issuer of <c>&lt;I&gt;</c> as stored (reversed RDN order), else empty.</summary>
    public string Issuer { get; }

    /// <summary>The subject, the serial number, the key identifier, the e-mail address or the Kerberos principal.</summary>
    public string SubjectOrIdentifier { get; }

    /// <summary>Kind and strength only (never the value).</summary>
    public override string ToString() => $"{nameof(NameMappingRowViewModel)}({Kind} {Strength})";
}
