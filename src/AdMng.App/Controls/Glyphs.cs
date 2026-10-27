using AdMng.Presentation.Icons;

namespace AdMng.App.Controls;

/// <summary>Overlay badge of an object icon (ADUC conventions).</summary>
public enum IconOverlay
{
    None,

    /// <summary>Disabled account (down arrow).</summary>
    Disabled,

    /// <summary>Locked-out account (padlock).</summary>
    Locked,
}

/// <summary>Colour family of an object icon.</summary>
public enum IconTone
{
    /// <summary>Leaf objects (users, computers, contacts...): primary text colour.</summary>
    Neutral,

    /// <summary>Containers, OUs and the domain: accent colour.</summary>
    Container,

    /// <summary>Views and filters of the navigation tree: secondary text colour.</summary>
    Muted,
}

/// <summary>
/// Glyphs of the icon font (<see cref="FontFamilyName"/>: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on
/// Windows 10 - both share these code points) and the mapping of <see cref="ObjectIcon"/>.
/// </summary>
public static class Glyphs
{
    /// <summary>Icon font with fallback.</summary>
    public const string FontFamilyName = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public const string Back = "";
    public const string Forward = "";
    public const string Up = "";
    public const string Refresh = "";
    public const string Add = "";
    public const string Cancel = "";
    public const string Delete = "";
    public const string Edit = "";
    public const string Rename = "";
    public const string Search = "";
    public const string Filter = "";
    public const string Export = "";
    public const string Copy = "";
    public const string Pin = "";
    public const string Settings = "";
    public const string More = "";
    public const string ChevronDown = "";
    public const string ChevronUp = "";
    public const string ChevronRight = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Completed = "";
    public const string Question = "";
    public const string Shield = "";
    public const string Lock = "";
    public const string DownArrow = "";
    public const string Activity = "";
    public const string Columns = "";
    public const string Properties = "";
    public const string NewUser = "";
    public const string NewFolder = "";
    public const string Batch = "";
    public const string Close = "";

    public const string User = "";
    public const string Computer = "";
    public const string Group = "";
    public const string Mail = "";
    public const string Contact = "";
    public const string Folder = "";
    public const string FolderOpen = "";
    public const string Container = "";
    public const string Domain = "";
    public const string Network = "";
    public const string Service = "";
    public const string Printer = "";
    public const string Unknown = "";

    /// <summary>The AdMng Watchdog tab (an eye: the directory is being watched).</summary>
    public const string Watchdog = "\uE890";
    public const string History = "\uE81C";
    public const string Send = "\uE724";
    public const string Unlock = "\uE785";
    public const string Accept = "\uE8FB";
    public const string Health = "\uE95E";

    /// <summary>A group policy object (a document: GPMC's scroll).</summary>
    public const string GroupPolicy = "\uE8A5";

    /// <summary>A site of the forest (a globe).</summary>
    public const string Site = "\uE909";

    /// <summary>Badge of the well-known containers in the navigation tree (a house: the default container).</summary>
    public const string WellKnown = "\uE80F";

    /// <summary>Base glyph of an object icon.</summary>
    public static string For(ObjectIcon icon) => icon switch
    {
        ObjectIcon.User or ObjectIcon.UserDisabled or ObjectIcon.UserLocked => User,
        ObjectIcon.Computer or ObjectIcon.ComputerDisabled => Computer,
        ObjectIcon.Server => Computer,
        ObjectIcon.DomainController => Shield,
        ObjectIcon.Group => Group,
        ObjectIcon.GroupDistribution => Mail,
        ObjectIcon.Contact => Contact,
        ObjectIcon.OrganizationalUnit => Folder,
        ObjectIcon.Container or ObjectIcon.Builtin => Container,
        ObjectIcon.Domain => Domain,
        ObjectIcon.ForeignPrincipal => Network,
        ObjectIcon.ServiceAccount => Service,
        ObjectIcon.Printer => Printer,
        ObjectIcon.SharedFolder => FolderOpen,
        ObjectIcon.Query => Search,
        ObjectIcon.QuickFilter => Filter,
        ObjectIcon.Pinned => Pin,
        ObjectIcon.Folder => Folder,
        ObjectIcon.Watchdog => Watchdog,
        ObjectIcon.GroupPolicy => GroupPolicy,
        ObjectIcon.WmiFilter => Filter,
        ObjectIcon.Site => Site,
        _ => Unknown,
    };

    /// <summary>Overlay badge of an object icon.</summary>
    public static IconOverlay OverlayFor(ObjectIcon icon) => icon switch
    {
        ObjectIcon.UserDisabled or ObjectIcon.ComputerDisabled => IconOverlay.Disabled,
        ObjectIcon.UserLocked => IconOverlay.Locked,
        _ => IconOverlay.None,
    };

    /// <summary>Colour family of an object icon.</summary>
    public static IconTone ToneFor(ObjectIcon icon) => icon switch
    {
        ObjectIcon.OrganizationalUnit or ObjectIcon.Container or ObjectIcon.Builtin or ObjectIcon.Domain
            or ObjectIcon.Folder or ObjectIcon.SharedFolder => IconTone.Container,
        ObjectIcon.Query or ObjectIcon.QuickFilter or ObjectIcon.Pinned => IconTone.Muted,
        ObjectIcon.Watchdog => IconTone.Container,
        ObjectIcon.GroupPolicy or ObjectIcon.Site => IconTone.Container,
        ObjectIcon.WmiFilter => IconTone.Muted,
        _ => IconTone.Neutral,
    };
}
