using AdMng.Core.Batch;
using AdMng.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.PropertySheets;

/// <summary>
/// One tab of a property sheet. The WPF layer picks the tab content by page type:
/// <see cref="FieldsPageViewModel"/> (sections of fields, templated by field type), <see cref="MemberOfPageViewModel"/>,
/// <see cref="MembersPageViewModel"/> and <see cref="AttributeEditorPageViewModel"/>.
/// </summary>
public abstract partial class PropertyPageViewModel : ObservableObject
{
    private protected PropertyPageViewModel(FieldContext context, string key, string title)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Title = title ?? throw new ArgumentNullException(nameof(title));
    }

    /// <summary>Raised when <see cref="HasChanges"/> or <see cref="HasErrors"/> may have changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Stable identifier of the page (the resource key of its title, e.g. "Pages.General").</summary>
    public string Key { get; }

    /// <summary>Localized tab title.</summary>
    public string Title { get; }

    /// <summary>The page loads additional data (members, names, all attributes).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; protected set; }

    /// <summary>Localized error of the page's own (additional) load.</summary>
    [ObservableProperty]
    public partial string? Error { get; protected set; }

    /// <summary>Pending changes on this page.</summary>
    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    /// <summary>Validation or planning errors on this page.</summary>
    [ObservableProperty]
    public partial bool HasErrors { get; private set; }

    internal FieldContext Context { get; }

    /// <summary>Attributes the page needs from the sheet's main read.</summary>
    internal abstract IEnumerable<string> ReadAttributes { get; }

    /// <summary>Called when the tab becomes visible; pages with expensive data load it lazily here.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    /// <summary>Discards the pending changes of the page.</summary>
    public abstract void Revert();

    /// <summary>Takes the values of a fresh read (synchronous part).</summary>
    internal abstract void Load(DirectoryObject obj);

    /// <summary>Asynchronous part of loading (resolving names, members). Never throws for directory errors.</summary>
    internal virtual Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Operations applying the page's pending changes.</summary>
    internal abstract IEnumerable<BatchOperation> GetOperations();

    /// <summary>Shows a planner finding on the page. Returns false when the page has nothing to attach it to.</summary>
    internal virtual bool ShowIssue(ValidationIssue issue, string text) => false;

    internal virtual void ClearIssues()
    {
    }

    /// <summary>Recomputes <see cref="HasChanges"/> / <see cref="HasErrors"/> and notifies the sheet.</summary>
    protected void UpdateState()
    {
        HasChanges = ComputeHasChanges();
        HasErrors = ComputeHasErrors();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected abstract bool ComputeHasChanges();

    protected abstract bool ComputeHasErrors();
}

/// <summary>A group box of fields ("Account options", "Home folder"...). <see cref="Header"/> is null for the first block.</summary>
public sealed class FieldSectionViewModel(string? header, IReadOnlyList<PropertyFieldViewModel> fields)
{
    public string? Header { get; } = header;

    public IReadOnlyList<PropertyFieldViewModel> Fields { get; } = fields ?? throw new ArgumentNullException(nameof(fields));
}

/// <summary>A page made of fields (General, Address, Account, Profile, Telephones, Organization, E-mail Addresses, Object...).</summary>
public sealed class FieldsPageViewModel : PropertyPageViewModel
{
    internal FieldsPageViewModel(FieldContext context, string key, string title, IReadOnlyList<FieldSectionViewModel> sections)
        : base(context, key, title)
    {
        Sections = sections ?? throw new ArgumentNullException(nameof(sections));
        var all = new List<PropertyFieldViewModel>();
        foreach (var field in sections.SelectMany(s => s.Fields))
        {
            all.Add(field);
            if (field is FieldViewModel { Others: { } others })
            {
                all.Add(others);
            }
        }

        Fields = all;
        foreach (var field in Fields)
        {
            field.StateChanged += (_, _) => UpdateState();
        }
    }

    public IReadOnlyList<FieldSectionViewModel> Sections { get; }

    /// <summary>All fields of the page including nested "Other..." lists.</summary>
    public IReadOnlyList<PropertyFieldViewModel> Fields { get; }

    internal override IEnumerable<string> ReadAttributes => Fields.SelectMany(f => f.ReadAttributes);

    public override void Revert()
    {
        foreach (var field in Fields)
        {
            field.Revert();
        }
    }

    internal override void Load(DirectoryObject obj)
    {
        foreach (var field in Fields)
        {
            field.Load(obj);
        }

        UpdateState();
    }

    internal override async Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken)
    {
        foreach (var field in Fields)
        {
            await field.LoadDetailsAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    internal override IEnumerable<BatchOperation> GetOperations() => Fields.SelectMany(f => f.GetOperations()).ToList();

    internal override bool ShowIssue(ValidationIssue issue, string text)
    {
        if (issue.Attribute is null)
        {
            return false;
        }

        var shown = false;
        foreach (var field in Fields.Where(f => f.IsDirty
            && f.WrittenAttributes.Contains(issue.Attribute, StringComparer.OrdinalIgnoreCase)))
        {
            field.SetIssue(text, issue.Severity == IssueSeverity.Error);
            shown = true;
        }

        return shown;
    }

    internal override void ClearIssues()
    {
        foreach (var field in Fields)
        {
            field.ClearIssue();
        }

        UpdateState();
    }

    protected override bool ComputeHasChanges() => Fields.Any(f => f.IsDirty);

    protected override bool ComputeHasErrors() => Fields.Any(f => f.HasError);
}
