using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>What a node of the editor tree shows.</summary>
public enum PolicyTreeNodeKind
{
    /// <summary>"Konfiguracja komputera" / "Konfiguracja użytkownika".</summary>
    Scope,

    /// <summary>A category of the templates.</summary>
    Category,

    /// <summary>"Wszystkie ustawienia" of the scope.</summary>
    AllSettings,

    /// <summary>Policies whose category is unknown.</summary>
    Uncategorized,

    /// <summary>"Ustawienia rejestru spoza szablonów": Registry.pol entries no template describes.</summary>
    Extra,
}

/// <summary>A node of the editor tree.</summary>
public sealed partial class PolicyTreeNodeViewModel : ObservableObject
{
    public PolicyTreeNodeViewModel(PolicyTreeNodeKind kind, PolicyClass scope, string name, PolicyCategoryNode? category = null,
        IReadOnlyList<PolicyTreeNodeViewModel>? children = null)
    {
        Kind = kind;
        Scope = scope;
        Name = name;
        Category = category;
        Children = children ?? [];
    }

    public PolicyTreeNodeKind Kind { get; }

    /// <summary>Computer or user configuration.</summary>
    public PolicyClass Scope { get; }

    public PolicyCategoryNode? Category { get; }

    public IReadOnlyList<PolicyTreeNodeViewModel> Children { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override string ToString() => Name;
}

/// <summary>A policy in the editor's list.</summary>
public sealed partial class PolicyRowViewModel : ObservableObject
{
    public PolicyRowViewModel(PolicyDefinition definition, PolicyClass scope, string categoryPath)
    {
        Definition = definition;
        Scope = scope;
        CategoryPath = categoryPath;
    }

    public PolicyDefinition Definition { get; }

    public PolicyClass Scope { get; }

    public string Name => Definition.DisplayName;

    /// <summary>"Składniki systemu Windows / Windows Update" (shown when the list spans categories).</summary>
    public string CategoryPath { get; }

    [ObservableProperty]
    public partial PolicyState State { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    /// <summary>The state differs from the one read from SYSVOL (not saved yet).</summary>
    [ObservableProperty]
    public partial bool IsChanged { get; set; }

    public override string ToString() => Name;
}

/// <summary>A Registry.pol entry no loaded template describes ("Ustawienia rejestru spoza szablonów").</summary>
public sealed class ExtraRegistryEntryViewModel(RegistryPolEntry entry, PolicyClass scope)
{
    public RegistryPolEntry Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));

    public PolicyClass Scope { get; } = scope;

    /// <summary>"HKLM\Software\Policies\…".</summary>
    public string KeyText => (Scope == PolicyClass.User ? "HKCU\\" : "HKLM\\") + RegistryPolicyDocument.NormalizeKey(Entry.Key);

    public string ValueName => Entry.ValueName;

    public string TypeText => RegistryFormat.TypeName(Entry.Type);

    /// <summary>The data as text; values the shared parsers treat as secrets are masked.</summary>
    public string DataText
    {
        get
        {
            if (GpoSecretSettings.IsSecretRegistryValue("HKLM\\" + RegistryPolicyDocument.NormalizeKey(Entry.Key), Entry.ValueName)
                || GpoSecretSettings.HidesValueNames("HKLM\\" + RegistryPolicyDocument.NormalizeKey(Entry.Key)))
            {
                return "••••••";
            }

            return Entry.Type switch
            {
                RegistryFormat.RegSz or RegistryFormat.RegExpandSz => RegistryPolFile.ReadString(Entry.Data),
                RegistryFormat.RegMultiSz => string.Join("; ", RegistryPolFile.ReadMultiString(Entry.Data)),
                RegistryFormat.RegDword when Entry.Data.Length == 4 => BitConverter.ToUInt32(Entry.Data).ToString(CultureInfo.CurrentCulture),
                RegistryFormat.RegQword when Entry.Data.Length == 8 => BitConverter.ToUInt64(Entry.Data).ToString(CultureInfo.CurrentCulture),
                _ => Convert.ToHexStringLower(Entry.Data),
            };
        }
    }
}
