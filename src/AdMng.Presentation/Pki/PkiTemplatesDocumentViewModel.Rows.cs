using System.Globalization;
using AdMng.Core.Pki;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Pki;

/// <summary>One certificate template of <i>Szablony certyfikatów</i> with the README columns (texts built when shown).</summary>
public sealed partial class PkiTemplateRowViewModel : ObservableObject
{
    internal PkiTemplateRowViewModel(CertificateTemplate template, IReadOnlyList<string> cas)
    {
        Template = template;
        Cas = cas;
    }

    /// <summary>The template as read.</summary>
    public CertificateTemplate Template { get; }

    /// <summary>The enterprise CAs that issue it (names).</summary>
    public IReadOnlyList<string> Cas { get; }

    public bool IsPublished => Cas.Count > 0;

    public string Cn => Template.Cn;

    public string DisplayName => string.IsNullOrWhiteSpace(Template.DisplayName) ? Template.Cn : Template.DisplayName!;

    public string SchemaText => Template.SchemaVersion.ToString(CultureInfo.InvariantCulture);

    public string VersionText { get; internal init; } = string.Empty;

    public string PurposeText { get; internal init; } = string.Empty;

    public string ValidityText { get; internal init; } = string.Empty;

    public string RenewalText { get; internal init; } = string.Empty;

    public string KeyText { get; internal init; } = string.Empty;

    public string SubjectText { get; internal init; } = string.Empty;

    public string ApprovalText { get; internal init; } = string.Empty;

    public string SignaturesText => Template.RaSignatures.ToString(CultureInfo.InvariantCulture);

    public string PublishText { get; internal init; } = string.Empty;

    public string SupersededText { get; internal init; } = string.Empty;

    public string IssuancePoliciesText { get; internal init; } = string.Empty;

    /// <summary>"Kontoso Wystawiający CA" or "niewystawiany przez żaden urząd".</summary>
    public string CasText { get; internal init; } = string.Empty;

    /// <summary>Who can enroll: the trustees at once, the account count after the expansion.</summary>
    [ObservableProperty]
    public partial string EnrollText { get; internal set; } = string.Empty;

    /// <summary>The most dangerous class that can enroll (the view marks broad enrollment), null when nobody or not evaluated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBroadEnroll))]
    public partial PrincipalClass? EnrollClass { get; internal set; }

    public bool IsBroadEnroll => EnrollClass == PrincipalClass.Broad;

    /// <summary>The rights (trustee level first, replaced by the expanded rights).</summary>
    internal TemplateRights? Rights { get; set; }

    /// <summary>The text the search box matches (names, OID, purposes, CAs, trustee names).</summary>
    internal string SearchKey { get; set; } = string.Empty;

    /// <summary>Whether it is published only (never the template name).</summary>
    public override string ToString() => $"{nameof(PkiTemplateRowViewModel)}({(IsPublished ? "published" : "not published")})";
}

/// <summary>A heading of the flag pane (one per flag attribute) with its sentences.</summary>
public sealed class PkiFlagGroupViewModel
{
    internal PkiFlagGroupViewModel(string header, IReadOnlyList<PkiFlagItemViewModel> items)
    {
        Header = header;
        Items = items;
    }

    public string Header { get; }

    public IReadOnlyList<PkiFlagItemViewModel> Items { get; }

    /// <summary>The count only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiFlagGroupViewModel)}({Items.Count})");
}

/// <summary>One decoded flag as a sentence, the technical bit name in the tooltip.</summary>
public sealed class PkiFlagItemViewModel
{
    internal PkiFlagItemViewModel(string text, string technicalName, bool isUnknown = false)
    {
        Text = text;
        TechnicalName = technicalName;
        IsUnknown = isUnknown;
    }

    public string Text { get; }

    /// <summary>"CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT (0x1)".</summary>
    public string TechnicalName { get; }

    public bool IsUnknown { get; }

    /// <summary>The kind only.</summary>
    public override string ToString() => $"{nameof(PkiFlagItemViewModel)}({(IsUnknown ? "unknown" : "known")})";
}

/// <summary>An account the rights pane names (one of the first 20), opening its property sheet.</summary>
public sealed class PkiAccountLinkViewModel
{
    internal PkiAccountLinkViewModel(PkiAccountRef account, string text)
    {
        Account = account;
        Text = text;
    }

    public PkiAccountRef Account { get; }

    public string Dn => Account.Dn;

    /// <summary>The name ("… (wyłączone)" for a disabled account).</summary>
    public string Text { get; }

    public bool IsDisabled => Account.IsDisabled;

    /// <summary>Whether it is disabled only.</summary>
    public override string ToString() => $"{nameof(PkiAccountLinkViewModel)}({(IsDisabled ? "disabled" : "enabled")})";
}

/// <summary>A trustee that holds a right on the selected template (trustee level, then with the account counts).</summary>
public sealed class PkiTrusteeRowViewModel
{
    internal PkiTrusteeRowViewModel(TemplateTrustee trustee, string rightText, string name, string? dn, bool isGroup, string classText, string countText, IReadOnlyList<PkiAccountLinkViewModel> firstAccounts)
    {
        Trustee = trustee;
        RightText = rightText;
        Name = name;
        Dn = dn;
        IsGroup = isGroup;
        ClassText = classText;
        CountText = countText;
        FirstAccounts = firstAccounts;
    }

    public TemplateTrustee Trustee { get; }

    public TemplateRight Right => Trustee.Right;

    public string RightText { get; }

    /// <summary>The trustee's name (the SID when it cannot be resolved).</summary>
    public string Name { get; }

    /// <summary>The trustee's object (an account or a group of the window's domain), null for a well-known principal or another domain.</summary>
    public string? Dn { get; }

    public bool IsGroup { get; }

    public PrincipalClass Class => Trustee.Class;

    public bool IsBroad => Trustee.Class == PrincipalClass.Broad;

    public bool IsTier0 => Trustee.Class == PrincipalClass.Tier0;

    public string ClassText { get; }

    /// <summary>"kont: 54 (+ wyłączonych: 2)", "liczenie…" before the expansion, "liczba nieznana (inna domena)".</summary>
    public string CountText { get; }

    /// <summary>The first accounts (a broad trustee shows the count only).</summary>
    public IReadOnlyList<PkiAccountLinkViewModel> FirstAccounts { get; }

    public bool HasFirstAccounts => FirstAccounts.Count > 0;

    /// <summary>A group opens on its members, an account on its property sheet; a broad or unresolved trustee does not open.</summary>
    public bool CanOpen => Dn is not null && !IsBroad;

    /// <summary>Class and right only (never the SID or name).</summary>
    public override string ToString() => $"{nameof(PkiTrusteeRowViewModel)}({Class} {Right})";
}

/// <summary>The account-level total of one right (after the expansion).</summary>
public sealed class PkiRightTotalViewModel
{
    internal PkiRightTotalViewModel(TemplateRightTotal total, string rightText, string countText, string classText, IReadOnlyList<PkiAccountLinkViewModel> firstAccounts)
    {
        Total = total;
        RightText = rightText;
        CountText = countText;
        ClassText = classText;
        FirstAccounts = firstAccounts;
    }

    public TemplateRightTotal Total { get; }

    public string RightText { get; }

    public string CountText { get; }

    public string ClassText { get; }

    public bool IsBroad => Total.HighestClass == PrincipalClass.Broad;

    public IReadOnlyList<PkiAccountLinkViewModel> FirstAccounts { get; }

    public bool HasFirstAccounts => FirstAccounts.Count > 0;

    /// <summary>Right and counts only.</summary>
    public override string ToString() => $"{nameof(PkiRightTotalViewModel)}({Total.Right})";
}

/// <summary>One property of the compare pane (two templates side by side, differing rows marked).</summary>
public sealed class PkiCompareRowViewModel
{
    internal PkiCompareRowViewModel(string property, string left, string right)
    {
        Property = property;
        Left = left;
        Right = right;
    }

    public string Property { get; }

    public string Left { get; }

    public string Right { get; }

    public bool IsDifferent => !string.Equals(Left, Right, StringComparison.Ordinal);

    /// <summary>Whether the values differ only.</summary>
    public override string ToString() => $"{nameof(PkiCompareRowViewModel)}({(IsDifferent ? "different" : "same")})";
}
