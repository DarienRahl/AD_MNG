using System.ComponentModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Formatting;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;

namespace AdMng.Presentation.Lists;

/// <summary>Shared, immutable inputs of the rows of one list load (formatter, culture, lockout policy, clock).</summary>
public sealed class RowContext(ILocalizer localizer, IAttributeFormatter formatter, DomainPolicy policy, DateTimeOffset now)
{
    public ILocalizer Localizer { get; } = localizer;

    public IAttributeFormatter Formatter { get; } = formatter;

    public DomainPolicy Policy { get; } = policy;

    /// <summary>Reference time for lockout evaluation.</summary>
    public DateTimeOffset Now { get; } = now;
}

/// <summary>
/// One object in a list. Holds the <see cref="DirectoryObject"/> snapshot only while the list is open (in memory,
/// CLAUDE.md rule 1). Formatted values are computed lazily on first access and cached until <see cref="Update"/>.
/// Rows can be created on a background thread; the indexer and <see cref="Update"/> are UI-thread members.
/// </summary>
public sealed class ObjectRowViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> _formatted = new(StringComparer.OrdinalIgnoreCase);
    private RowContext _context;
    private string? _parentPath;

    public ObjectRowViewModel(DirectoryObject obj, RowContext context)
    {
        Object = obj ?? throw new ArgumentNullException(nameof(obj));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Evaluate();
    }

    /// <summary>A "Loading…" row of a virtual list whose page is being read (no DN, no commands).</summary>
    private ObjectRowViewModel(RowContext context)
    {
        _context = context;
        IsPlaceholder = true;
        Object = PlaceholderObject(context);
        Icon = ObjectIcon.Unknown;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// True for the stand-in of a row a virtual list is still reading: it shows "Loading…", is never part of a
    /// selection the commands act on and is replaced by the real row when its page arrives.
    /// </summary>
    public bool IsPlaceholder { get; }

    public DirectoryObject Object { get; private set; }

    public string Dn => Object.DistinguishedName;

    public string Name => Object.Name;

    public ObjectKind Kind => Object.Kind;

    /// <summary>ADUC "Type" text ("User", "Security Group - Global"...), localized.</summary>
    public string TypeText { get; private set; } = string.Empty;

    public ObjectIcon Icon { get; private set; }

    public bool IsDisabled => Object.IsDisabled;

    public LockState LockState { get; private set; }

    /// <summary>Locked or possibly locked (lockoutTime set and lockout not known to be expired).</summary>
    public bool IsLocked => LockState != LockState.NotLocked;

    /// <summary>lockoutTime is set but the lockout duration could not be evaluated.</summary>
    public bool IsPossiblyLocked => LockState == LockState.PossiblyLocked;

    public bool IsContainer => Kind.IsContainer();

    public bool IsAccount => Kind.IsAccount();

    /// <summary>Canonical path of the parent container, e.g. <c>contoso.test/Firma/IT</c> (Location column).</summary>
    public string ParentPath => _parentPath ??= Object.Dn.Parent is { IsRoot: false } parent ? parent.ToCanonicalName().TrimEnd('/') : string.Empty;

    /// <summary>Folded name/displayName/sAMAccountName/description for the quick filter.</summary>
    internal string SearchKey { get; private set; } = string.Empty;

    /// <summary>Normalized DN key (computed off the UI thread when the row is created).</summary>
    internal string Key { get; private set; } = string.Empty;

    /// <summary>Formatted value of an attribute or pseudo column (empty when the object has no value).</summary>
    public string this[string attribute]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(attribute);
            if (_formatted.TryGetValue(attribute, out var cached))
            {
                return cached;
            }

            var value = FormatUncached(attribute);
            _formatted[attribute] = value;
            return value;
        }
    }

    /// <summary>Thread-safe formatting without touching the cache (CSV export on a background thread).</summary>
    public string FormatUncached(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (IsPlaceholder)
        {
            return string.Equals(attribute, Attr.Name, StringComparison.OrdinalIgnoreCase) ? Name : string.Empty;
        }

        if (string.Equals(attribute, ColumnSets.TypeColumn, StringComparison.OrdinalIgnoreCase))
        {
            return TypeText;
        }

        if (string.Equals(attribute, ColumnSets.LocationColumn, StringComparison.OrdinalIgnoreCase))
        {
            return ParentPath;
        }

        if (string.Equals(attribute, Attr.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Name;
        }

        if (string.Equals(attribute, Attr.GroupType, StringComparison.OrdinalIgnoreCase) && Object.GetInt32(Attr.GroupType) is { } groupType)
        {
            return _context.Localizer.GroupTypeName(groupType);
        }

        if (!Object.TryGet(attribute, out var values) || values.Count == 0)
        {
            return string.Empty;
        }

        return _context.Formatter.Format(attribute, values, _context.Localizer.Culture);
    }

    /// <summary>Replaces the snapshot (after a refresh) and drops cached formatted values.</summary>
    public void Update(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        Object = obj;
        _parentPath = null;
        Evaluate();
        RaiseAllChanged();
    }

    /// <summary>Re-formats with a new context (culture change, new lockout policy).</summary>
    public void Reformat(RowContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (IsPlaceholder)
        {
            // "Loading…" in the new language (a page that is slow or failing keeps its placeholders on screen).
            Object = PlaceholderObject(context);
            _formatted.Clear();
            RaiseAllChanged();
            return;
        }

        Evaluate();
        RaiseAllChanged();
    }

    /// <summary>A "Loading…" row (see <see cref="IsPlaceholder"/>).</summary>
    public static ObjectRowViewModel CreatePlaceholder(RowContext context) =>
        new(context ?? throw new ArgumentNullException(nameof(context)));

    private static DirectoryObject PlaceholderObject(RowContext context) =>
        new(string.Empty, [new AttributeValues(Attr.Name, [context.Localizer[L.Common.Loading]])]);

    public override string ToString() => Dn;

    private void Evaluate()
    {
        _formatted.Clear();
        Key = DnKey.For(Object.DistinguishedName);
        LockState = Kind.IsAccount() ? _context.Policy.GetLockState(Object.GetInt64(Attr.LockoutTime), _context.Now) : LockState.NotLocked;
        var groupType = Object.GetInt32(Attr.GroupType);
        Icon = ObjectIcons.For(Kind, IsDisabled, IsLocked, groupType is null || GroupTypes.IsSecurity(groupType.Value));
        TypeText = _context.Localizer.TypeName(Object);
        SearchKey = TextFolding.Fold(string.Join(
            '\n',
            Object.Name,
            Object.GetString(Attr.DisplayName),
            Object.GetString(Attr.SamAccountName),
            Object.GetString(Attr.Description)));
    }

    private void RaiseAllChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Helpers for DN keyed row lookups.</summary>
internal static class DnKey
{
    /// <summary>Normalized dictionary key for a DN (parsed form, upper case) so differently escaped DNs match.</summary>
    public static string For(string dn) =>
        (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn).ToUpperInvariant();
}
