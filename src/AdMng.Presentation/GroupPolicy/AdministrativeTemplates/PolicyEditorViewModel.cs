using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// The selected policy of the editor (K3): its texts (name, help, supported on, registry key), the state ("Nie
/// skonfigurowano", "Włączone", "Wyłączone") and, for an enabled policy, its options built from the ADML presentation (a
/// control per element; elements without one get a plain editor). Every change the operator makes is validated and, when
/// valid, written at once into the GPO's in-memory Registry.pol through <see cref="PolicyEngine"/> (nothing reaches SYSVOL
/// before "Zapisz zmiany…"); invalid options are listed in <see cref="Problems"/> and leave the document as it was.
/// </summary>
public sealed partial class PolicyEditorViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly Action<PolicyEditorViewModel, PolicySetting> _apply;
    private readonly Dictionary<string, PresentationControl> _controls = new(StringComparer.OrdinalIgnoreCase);
    private bool _showing;

    /// <param name="localizer">Texts.</param>
    /// <param name="definition">The policy.</param>
    /// <param name="scope">The half (computer or user configuration).</param>
    /// <param name="current">Its setting in the GPO now.</param>
    /// <param name="canEdit">The GPO can be written (SYSVOL available, the half's Registry.pol readable).</param>
    /// <param name="apply">Writes a valid setting into the document.</param>
    public PolicyEditorViewModel(ILocalizer localizer, PolicyDefinition definition, PolicyClass scope, PolicySetting current, bool canEdit,
        Action<PolicyEditorViewModel, PolicySetting> apply)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Scope = scope;
        CanEdit = canEdit && definition.Policy.IsWritable;
        Elements = BuildElements();
        foreach (var editor in Elements)
        {
            editor.ValueChanged += OnElementChanged;
        }

        _showing = true;
        IsInconsistent = current.State == PolicyState.Inconsistent;
        State = current.State;
        foreach (var editor in Elements.Where(e => e.Element is not null))
        {
            var element = editor.Element!;
            editor.Show(current.ValueOf(element.Id) ?? PolicyEngine.DefaultValue(element, _controls.GetValueOrDefault(element.Id)));
        }

        UpdateEditable();
        _showing = false;
    }

    public PolicyDefinition Definition { get; }

    public PolicyClass Scope { get; }

    public string Name => Definition.DisplayName;

    public string ExplainText => Definition.ExplainText;

    public bool HasExplainText => Definition.ExplainText.Length > 0;

    /// <summary>"Obsługiwane w: Co najmniej Windows 10" (null when the template does not say).</summary>
    public string? SupportedOnText => Definition.SupportedOnText is { Length: > 0 } text ? _localizer.Format(L.AdministrativeTemplates.SupportedOn, text) : null;

    /// <summary>"Składniki systemu Windows / Windows Update".</summary>
    public string CategoryText => Definition.Category is { } category ? string.Join(" / ", category.PathNames()) : string.Empty;

    /// <summary>"HKLM\Software\Policies\…!NoAutoUpdate".</summary>
    public string RegistryText => (Scope == PolicyClass.User ? "HKCU\\" : "HKLM\\") + Definition.Policy.Key
        + (Definition.Policy.ValueName is { } name ? "!" + (name.Length == 0 ? "(Default)" : name) : string.Empty);

    /// <summary>"contoso.admx".</summary>
    public string FileText => Definition.FileName;

    /// <summary>The options in presentation order.</summary>
    public IReadOnlyList<PolicyElementEditorViewModel> Elements { get; }

    public bool HasElements => Elements.Count > 0;

    /// <summary>The state can be changed.</summary>
    public bool CanEdit { get; }

    /// <summary>A policy Registry.pol cannot express (no value, list or element): shown read-only.</summary>
    public bool IsNotWritable => !Definition.Policy.IsWritable;

    /// <summary>Registry.pol held values of the policy that match neither state when it was opened.</summary>
    [ObservableProperty]
    public partial bool IsInconsistent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConfigured), nameof(IsEnabled), nameof(IsDisabled))]
    public partial PolicyState State { get; private set; }

    public bool IsNotConfigured
    {
        get => State == PolicyState.NotConfigured;
        set => Choose(value, PolicyState.NotConfigured);
    }

    public bool IsEnabled
    {
        get => State == PolicyState.Enabled;
        set => Choose(value, PolicyState.Enabled);
    }

    public bool IsDisabled
    {
        get => State == PolicyState.Disabled;
        set => Choose(value, PolicyState.Disabled);
    }

    /// <summary>What keeps the options from being written (localized; empty when they are valid).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>The setting the editor shows now (null when its options are invalid).</summary>
    public PolicySetting? CurrentSetting()
    {
        if (State == PolicyState.Inconsistent)
        {
            return null;
        }

        if (State != PolicyState.Enabled)
        {
            return State == PolicyState.Disabled ? PolicySetting.Disabled : PolicySetting.NotConfigured;
        }

        var values = new Dictionary<string, PolicyElementValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in Elements.Where(e => e.Element is not null))
        {
            if (editor.Value is { } value)
            {
                values[editor.Element!.Id] = value;
            }
        }

        return PolicySetting.Enabled(values);
    }

    private void Choose(bool selected, PolicyState state)
    {
        if (!selected || State == state || !CanEdit)
        {
            return;
        }

        State = state;
        IsInconsistent = false;
        UpdateEditable();
        Commit();
    }

    private void OnElementChanged(object? sender, EventArgs e)
    {
        if (!_showing && State == PolicyState.Enabled)
        {
            Commit();
        }
    }

    /// <summary>Validates and writes the shown setting (an invalid one only lists its problems).</summary>
    private void Commit()
    {
        var problems = new List<string>();
        if (State == PolicyState.Enabled)
        {
            foreach (var editor in Elements.Where(e => e.Element is not null && e.InputError is not null))
            {
                problems.Add(_localizer.Format(L.AdministrativeTemplates.ProblemNotANumber, editor.Label.TrimEnd(':')));
            }
        }

        var setting = CurrentSetting();
        if (setting is not null && problems.Count == 0)
        {
            foreach (var problem in PolicyEngine.Validate(Definition.Policy, setting))
            {
                problems.Add(ProblemText(problem));
            }
        }

        Problems = problems;
        if (setting is not null && problems.Count == 0)
        {
            _apply(this, setting);
        }
    }

    private void UpdateEditable()
    {
        foreach (var editor in Elements)
        {
            editor.IsEditable = CanEdit && State == PolicyState.Enabled;
        }
    }

    private string ProblemText(PolicyValueProblem problem)
    {
        var label = Elements.FirstOrDefault(e => string.Equals(e.Element?.Id, problem.ElementId, StringComparison.OrdinalIgnoreCase))?.Label.TrimEnd(':')
            ?? problem.ElementId;
        var key = problem.Kind switch
        {
            PolicyValueProblemKind.Required => L.AdministrativeTemplates.ProblemRequired,
            PolicyValueProblemKind.OutOfRange => L.AdministrativeTemplates.ProblemOutOfRange,
            PolicyValueProblemKind.TooLong => L.AdministrativeTemplates.ProblemTooLong,
            PolicyValueProblemKind.TooManyLines => L.AdministrativeTemplates.ProblemTooManyLines,
            PolicyValueProblemKind.UnknownItem => L.AdministrativeTemplates.ProblemUnknownItem,
            PolicyValueProblemKind.EmptyListEntry => L.AdministrativeTemplates.ProblemEmptyListEntry,
            PolicyValueProblemKind.DuplicateListEntry => L.AdministrativeTemplates.ProblemDuplicateListEntry,
            _ => L.AdministrativeTemplates.ProblemInvalidCharacter,
        };
        return _localizer.Format(key, [label, .. problem.Arguments]);
    }

    /// <summary>A control per presentation entry (in order), then a plain editor for every element the presentation lacks.</summary>
    private List<PolicyElementEditorViewModel> BuildElements()
    {
        var policy = Definition.Policy;
        var editors = new List<PolicyElementEditorViewModel>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in Definition.Presentation?.Controls ?? [])
        {
            if (control is TextPresentation text)
            {
                editors.Add(new StaticTextEditorViewModel(text.Label));
                continue;
            }

            if (control.RefId is not { } id || policy.Elements.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } element
                || !used.Add(element.Id))
            {
                continue;
            }

            _controls[element.Id] = control;
            editors.Add(Editor(element, control));
        }

        foreach (var element in policy.Elements.Where(e => !used.Contains(e.Id)))
        {
            editors.Add(Editor(element, null));
        }

        return editors;
    }

    private PolicyElementEditorViewModel Editor(PolicyElement element, PresentationControl? control)
    {
        var label = control?.Label is { Length: > 0 } text ? text : element.Id;
        return (element, control) switch
        {
            (BooleanElement boolean, _) => new CheckBoxEditorViewModel(boolean, label),
            (DecimalElement number, NumberBoxPresentation box) => new NumberEditorViewModel(number, label, box.Spin, box.SpinStep),
            (DecimalElement number, _) => new NumberEditorViewModel(number, label, spin: true, 1),
            (TextElement textElement, ComboBoxPresentation combo) => new TextEditorViewModel(textElement, label, combo.Suggestions),
            (TextElement textElement, _) => new TextEditorViewModel(textElement, label),
            (MultiTextElement lines, MultiTextBoxPresentation box) => new MultiTextEditorViewModel(lines, label, box.DefaultHeight),
            (MultiTextElement lines, _) => new MultiTextEditorViewModel(lines, label, 3),
            (EnumElement choice, _) => new DropDownEditorViewModel(choice, label, [.. choice.Items.Select(i => Definition.Text(i.DisplayName))]),
            (ListElement list, _) => new ListEditorViewModel(list, label),
            _ => new StaticTextEditorViewModel(label),
        };
    }
}
