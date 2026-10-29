using System.Collections.ObjectModel;
using System.ComponentModel;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Correspondence;

/// <summary>An editable <see cref="DirectoryFieldDefinition"/>: name, U1 expression, target and its validation error.</summary>
public sealed partial class DirectoryFieldViewModel : ObservableObject
{
    public DirectoryFieldViewModel(DirectoryFieldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Name = definition.Name;
        Expression = definition.Expression;
        Target = definition.Target;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>A U1 template expression (<c>{givenName}</c>, <c>{$honorific:biernik}</c> …).</summary>
    [ObservableProperty]
    public partial string Expression { get; set; }

    [ObservableProperty]
    public partial DirectoryFieldTarget Target { get; set; }

    /// <summary>Why the definition cannot be used (invalid expression, forbidden attribute), or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The definition as edited.</summary>
    public DirectoryFieldDefinition ToDefinition() => new(Name, Expression, Target);

    public override string ToString() => $"{nameof(DirectoryFieldViewModel)} {Name}";
}

/// <summary>
/// The field definitions of a page (the recipients' AD fields, the mailbox and owner fields): loading from the settings
/// (empty → the defaults), add, remove, reset, validation of every definition (<see cref="DirectoryRowBuilder.Check"/>
/// plus repeated names of a target) with localized errors, and the list written back to the settings (empty while it
/// equals the defaults, so later defaults apply).
/// </summary>
internal sealed class DirectoryFieldEditor
{
    private readonly ObservableCollection<DirectoryFieldViewModel> _fields;
    private readonly UserMessageFields _engine;
    private readonly ILocalizer _localizer;
    private readonly Func<ISchemaCatalog?> _schema;
    private readonly IReadOnlyList<DirectoryFieldDefinition> _defaults;
    private readonly DirectoryFieldTarget _newTarget;
    private readonly string _newNameKey;

    /// <param name="fields">The page's collection (bound by the view).</param>
    /// <param name="engine">The U1 field engine.</param>
    /// <param name="localizer">The window's localizer.</param>
    /// <param name="schema">The session's schema (null when there is no session).</param>
    /// <param name="defaults">The definitions of an empty settings list.</param>
    /// <param name="newTarget">The target of added definitions.</param>
    /// <param name="newNameKey">The localized name of added definitions (numbered when taken).</param>
    public DirectoryFieldEditor(ObservableCollection<DirectoryFieldViewModel> fields, UserMessageFields engine, ILocalizer localizer,
        Func<ISchemaCatalog?> schema, IReadOnlyList<DirectoryFieldDefinition> defaults, DirectoryFieldTarget newTarget, string newNameKey)
    {
        _fields = fields ?? throw new ArgumentNullException(nameof(fields));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _newTarget = newTarget;
        _newNameKey = newNameKey ?? throw new ArgumentNullException(nameof(newNameKey));
    }

    /// <summary>Raised after a definition was added, removed or edited (the page refreshes what depends on it).</summary>
    public event EventHandler? Changed;

    /// <summary>The definitions as edited (invalid ones included).</summary>
    public IReadOnlyList<DirectoryFieldDefinition> Definitions => [.. _fields.Select(f => f.ToDefinition())];

    /// <summary>Replaces the list with <paramref name="definitions"/> (empty: the defaults).</summary>
    public void Load(IReadOnlyList<DirectoryFieldDefinition>? definitions) =>
        Replace(definitions is { Count: > 0 } ? definitions : _defaults);

    /// <summary>Back to the defaults.</summary>
    public void Reset() => Replace(_defaults);

    /// <summary>Adds a definition "Nowe pole" (numbered when taken) reading <c>{displayName}</c>.</summary>
    public DirectoryFieldViewModel Add()
    {
        var baseName = _localizer[_newNameKey];
        var name = baseName;
        for (var i = 2; _fields.Any(f => f.Target == _newTarget && FieldKey.Same(f.Name, name)); i++)
        {
            name = $"{baseName} {i}";
        }

        var field = new DirectoryFieldViewModel(new DirectoryFieldDefinition(name, "{displayName}", _newTarget));
        Attach(field);
        _fields.Add(field);
        Validate();
        Changed?.Invoke(this, EventArgs.Empty);
        return field;
    }

    public void Remove(DirectoryFieldViewModel? field)
    {
        if (field is null || !_fields.Remove(field))
        {
            return;
        }

        field.PropertyChanged -= OnFieldChanged;
        Validate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets <see cref="DirectoryFieldViewModel.Error"/> of every definition (also after a language change).</summary>
    public void Validate()
    {
        var schema = _schema();
        for (var i = 0; i < _fields.Count; i++)
        {
            var field = _fields[i];
            var (problem, detail) = DirectoryRowBuilder.Check(_engine, field.ToDefinition(), schema);
            if (problem == DirectoryFieldProblem.None
                && _fields.Take(i).Any(f => f.Target == field.Target && FieldKey.Same(f.Name, field.Name)))
            {
                problem = DirectoryFieldProblem.DuplicateName;
            }

            field.Error = ErrorText(problem, detail);
        }
    }

    /// <summary>The list for the settings: empty while it equals the defaults.</summary>
    public List<DirectoryFieldDefinition> ToSettings()
    {
        var definitions = _fields.Select(f => f.ToDefinition() with { Name = f.Name?.Trim() ?? string.Empty, Expression = f.Expression?.Trim() ?? string.Empty }).ToList();
        return definitions.SequenceEqual(_defaults) ? [] : definitions;
    }

    private void Replace(IEnumerable<DirectoryFieldDefinition> definitions)
    {
        foreach (var field in _fields)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        _fields.Clear();
        foreach (var definition in definitions)
        {
            var field = new DirectoryFieldViewModel(definition);
            Attach(field);
            _fields.Add(field);
        }

        Validate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Attach(DirectoryFieldViewModel field) => field.PropertyChanged += OnFieldChanged;

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DirectoryFieldViewModel.Name) or nameof(DirectoryFieldViewModel.Expression) or nameof(DirectoryFieldViewModel.Target))
        {
            Validate();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string? ErrorText(DirectoryFieldProblem problem, string? detail) => problem switch
    {
        DirectoryFieldProblem.None => null,
        DirectoryFieldProblem.EmptyName => _localizer[L.CorrespondenceRecipients.FieldEmptyName],
        DirectoryFieldProblem.InvalidName => _localizer.Format(L.CorrespondenceRecipients.FieldInvalidName, FieldTemplate.MaxFieldNameLength),
        DirectoryFieldProblem.DuplicateName => _localizer[L.CorrespondenceRecipients.FieldDuplicate],
        DirectoryFieldProblem.EmptyExpression => _localizer.Format(L.CorrespondenceRecipients.FieldEmptyExpression),
        DirectoryFieldProblem.Syntax => _localizer.Format(L.CorrespondenceRecipients.FieldSyntax, detail ?? string.Empty),
        DirectoryFieldProblem.Forbidden => _localizer.Format(L.CorrespondenceRecipients.FieldForbidden, detail ?? string.Empty),
        DirectoryFieldProblem.Unsupported => _localizer.Format(L.CorrespondenceRecipients.FieldUnsupported, detail ?? string.Empty),
        _ => null,
    };
}
