using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BatchEditing;

/// <summary>Operation types offered by the batch editor's "Add operation" menu (one per <see cref="BatchOperation"/>).</summary>
public enum OperationType
{
    SetAttribute,
    SetValues,
    ClearAttribute,
    AddValues,
    RemoveValues,
    FindReplace,
    EnableDisable,
    AccountFlag,
    Unlock,
    RequirePasswordChange,
    AccountExpiration,
    ResetPassword,
    Move,
    Rename,
    AddToGroups,
    RemoveFromGroups,
    Delete,
}

/// <summary>An entry of the "Add operation" menu.</summary>
public sealed record OperationTypeOption(OperationType Type, string Text, string Description);

/// <summary>What operation editors need from the batch editor.</summary>
internal sealed class OperationEditorContext(
    DialogServices services,
    ITemplateEngine templates,
    Func<string, CancellationToken, Task<string?>> renderSample)
{
    public DialogServices Services { get; } = services;

    public ILocalizer Localizer => Services.Localizer;

    public ITemplateEngine Templates { get; } = templates;

    /// <summary>Renders a template for the first target (null when there is no target or it cannot be read).</summary>
    public Func<string, CancellationToken, Task<string?>> RenderSample { get; } = renderSample;
}

/// <summary>
/// Editor of one batch operation. The WPF layer picks the editor template by type. <see cref="Error"/> is the
/// localized validation message; the preview is only possible when every editor is valid.
/// </summary>
public abstract partial class OperationEditorViewModel : ObservableObject
{
    private protected OperationEditorViewModel(OperationEditorContext context, OperationType type)
    {
        Context = context;
        Type = type;
    }

    /// <summary>Raised after any parameter changed.</summary>
    public event EventHandler? Changed;

    public OperationType Type { get; }

    /// <summary>Localized name of the operation ("Set attribute", "Move to container"...).</summary>
    public string Title => Context.Localizer[DisplayNames.OperationTypeKey(Type)];

    /// <summary>Localized validation error, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string? Error { get; private set; }

    public bool IsValid => Error is null;

    internal OperationEditorContext Context { get; }

    protected ILocalizer Localizer => Context.Localizer;

    /// <summary>Creates an empty editor of the given type.</summary>
    internal static OperationEditorViewModel Create(OperationType type, OperationEditorContext context) => type switch
    {
        OperationType.SetAttribute => new SetAttributeEditorViewModel(context),
        OperationType.SetValues => new SetValuesEditorViewModel(context),
        OperationType.ClearAttribute => new ClearAttributeEditorViewModel(context),
        OperationType.AddValues => new AddValuesEditorViewModel(context),
        OperationType.RemoveValues => new RemoveValuesEditorViewModel(context),
        OperationType.FindReplace => new FindReplaceEditorViewModel(context),
        OperationType.EnableDisable => new EnableDisableEditorViewModel(context),
        OperationType.AccountFlag => new AccountFlagEditorViewModel(context),
        OperationType.Unlock => new UnlockEditorViewModel(context),
        OperationType.RequirePasswordChange => new RequirePasswordChangeEditorViewModel(context),
        OperationType.AccountExpiration => new AccountExpirationEditorViewModel(context),
        OperationType.ResetPassword => new ResetPasswordEditorViewModel(context),
        OperationType.Move => new MoveEditorViewModel(context),
        OperationType.Rename => new RenameEditorViewModel(context),
        OperationType.AddToGroups => new GroupsEditorViewModel(context, OperationType.AddToGroups),
        OperationType.RemoveFromGroups => new GroupsEditorViewModel(context, OperationType.RemoveFromGroups),
        OperationType.Delete => new DeleteEditorViewModel(context),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown operation type."),
    };

    /// <summary>Creates an editor pre-filled from an operation (batch editor opened with operations).</summary>
    internal static OperationEditorViewModel From(BatchOperation operation, OperationEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var editor = Create(TypeOf(operation), context);
        editor.Fill(operation);
        editor.Revalidate();
        return editor;
    }

    internal static OperationType TypeOf(BatchOperation operation) => operation switch
    {
        SetAttributeOperation => OperationType.SetAttribute,
        SetValuesOperation => OperationType.SetValues,
        ClearAttributeOperation => OperationType.ClearAttribute,
        AddValuesOperation => OperationType.AddValues,
        RemoveValuesOperation => OperationType.RemoveValues,
        FindReplaceOperation => OperationType.FindReplace,
        SetAccountEnabledOperation => OperationType.EnableDisable,
        SetAccountFlagOperation => OperationType.AccountFlag,
        UnlockAccountOperation => OperationType.Unlock,
        RequirePasswordChangeOperation => OperationType.RequirePasswordChange,
        SetAccountExpirationOperation => OperationType.AccountExpiration,
        ResetPasswordOperation => OperationType.ResetPassword,
        MoveOperation => OperationType.Move,
        RenameOperation => OperationType.Rename,
        AddToGroupsOperation => OperationType.AddToGroups,
        RemoveFromGroupsOperation => OperationType.RemoveFromGroups,
        DeleteOperation => OperationType.Delete,
        _ => throw new NotSupportedException($"Unsupported operation {operation.GetType().Name}."),
    };

    /// <summary>The configured operation, or null when the editor is not valid.</summary>
    internal BatchOperation? Build()
    {
        Revalidate();
        return Error is null && ValidateBuild() is null ? BuildCore() : null;
    }

    /// <summary>
    /// Last check right before the operation is built, for inputs the view model cannot observe live (password
    /// boxes). It must not affect <see cref="IsValid"/>, otherwise Preview could stay disabled after the operator
    /// corrected the input. Returns a localized error (shown by the editor) or null.
    /// </summary>
    protected virtual string? ValidateBuild() => null;

    /// <summary>Re-evaluates <see cref="Error"/>.</summary>
    internal void Revalidate() => Error = Validate();

    /// <summary>Renders the template sample again (after the first target became known).</summary>
    internal virtual void RefreshSample()
    {
    }

    protected abstract BatchOperation? BuildCore();

    protected abstract void Fill(BatchOperation operation);

    protected abstract string? Validate();

    /// <summary>To be called after a parameter changed.</summary>
    protected void OnChanged()
    {
        Revalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Base of operations on one attribute (attribute picker with schema autocomplete).</summary>
public abstract partial class AttributeOperationEditorViewModel : OperationEditorViewModel
{
    private protected AttributeOperationEditorViewModel(OperationEditorContext context, OperationType type)
        : base(context, type)
    {
    }

    /// <summary>LDAP name of the attribute (free text; the options are suggestions).</summary>
    [ObservableProperty]
    public partial string Attribute { get; set; } = string.Empty;

    /// <summary>Writable, non-binary attributes allowed for the targets (set by the batch editor).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AttributeOption> AttributeOptions { get; internal set; } = [];

    /// <summary>The option matching <see cref="Attribute"/> (two-way for combo boxes).</summary>
    public AttributeOption? SelectedAttribute
    {
        get => AttributeOptions.FirstOrDefault(o => string.Equals(o.LdapName, Attribute, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
            {
                Attribute = value.LdapName;
            }
        }
    }

    /// <summary>LDAP name with the "[ldapName]" suffix of an option text removed.</summary>
    protected string AttributeName
    {
        get
        {
            var text = (Attribute ?? string.Empty).Trim();
            var open = text.LastIndexOf('[');
            return open >= 0 && text.EndsWith(']') ? text[(open + 1)..^1].Trim() : text;
        }
    }

    partial void OnAttributeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedAttribute));
        OnChanged();
    }

    partial void OnAttributeOptionsChanged(IReadOnlyList<AttributeOption> value) => OnPropertyChanged(nameof(SelectedAttribute));

    protected override string? Validate()
    {
        var name = AttributeName;
        if (name.Length == 0)
        {
            return Localizer[L.Batch.AttributeRequired];
        }

        if (!char.IsAsciiLetter(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return Localizer[L.Batch.InvalidAttribute];
        }

        var schema = Context.Services.Sessions.Current?.Schema;
        if (schema is { Attributes.Count: > 0 } && schema.FindAttribute(name) is null)
        {
            return Localizer[L.Batch.UnknownAttribute];
        }

        return null;
    }

    /// <summary>Validates a template and renders the sample for the first target.</summary>
    protected string? ValidateTemplate(string template)
    {
        return Context.Templates.Parse(template ?? string.Empty).IsValid ? null : Localizer[L.Batch.InvalidTemplate];
    }
}

/// <summary>Shared sample rendering of template editors ("Sample for the first object: jan.kowalski@firma.pl").</summary>
internal sealed class TemplateSample(OperationEditorContext context, Action<string?> setSample)
{
    private readonly SerialCancellation _renders = new();

    public async Task UpdateAsync(string template)
    {
        var token = _renders.Next();
        if (!context.Templates.Parse(template ?? string.Empty).IsValid)
        {
            setSample(null);
            return;
        }

        try
        {
            var sample = await context.RenderSample(template ?? string.Empty, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                setSample(sample);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer template.
        }
    }
}

/// <summary>Set an attribute from a template, e.g. <c>{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}@firma.pl</c>.</summary>
public sealed partial class SetAttributeEditorViewModel : AttributeOperationEditorViewModel
{
    private readonly TemplateSample _sample;

    internal SetAttributeEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.SetAttribute)
    {
        _sample = new TemplateSample(context, s => Sample = s);
        Revalidate();
    }

    [ObservableProperty]
    public partial string ValueTemplate { get; set; } = string.Empty;

    /// <summary>An empty result clears the attribute (otherwise the object is left unchanged).</summary>
    [ObservableProperty]
    public partial bool EmptyClears { get; set; }

    /// <summary>The template rendered for the first target (null while unknown).</summary>
    [ObservableProperty]
    public partial string? Sample { get; private set; }

    partial void OnValueTemplateChanged(string value)
    {
        OnChanged();
        _sample.UpdateAsync(value).Forget();
    }

    partial void OnEmptyClearsChanged(bool value) => OnChanged();

    internal override void RefreshSample() => _sample.UpdateAsync(ValueTemplate).Forget();

    protected override string? Validate() => base.Validate() ?? ValidateTemplate(ValueTemplate);

    protected override BatchOperation BuildCore() => new SetAttributeOperation(AttributeName, ValueTemplate, EmptyClears);

    protected override void Fill(BatchOperation operation)
    {
        var set = (SetAttributeOperation)operation;
        Attribute = set.Attribute;
        ValueTemplate = set.ValueTemplate;
        EmptyClears = set.EmptyClears;
    }
}

/// <summary>Set exact values (no templating), one per line; no lines clears the attribute.</summary>
public sealed partial class SetValuesEditorViewModel : AttributeOperationEditorViewModel
{
    internal SetValuesEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.SetValues)
    {
        Revalidate();
    }

    /// <summary>One value per line.</summary>
    [ObservableProperty]
    public partial string ValuesText { get; set; } = string.Empty;

    partial void OnValuesTextChanged(string value) => OnChanged();

    protected override BatchOperation BuildCore() => new SetValuesOperation(AttributeName, Lines(ValuesText));

    protected override void Fill(BatchOperation operation)
    {
        var set = (SetValuesOperation)operation;
        Attribute = set.Attribute;
        ValuesText = string.Join(Environment.NewLine, set.Values);
    }

    internal static string[] Lines(string text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Clear an attribute.</summary>
public sealed class ClearAttributeEditorViewModel : AttributeOperationEditorViewModel
{
    internal ClearAttributeEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.ClearAttribute)
    {
        Revalidate();
    }

    protected override BatchOperation BuildCore() => new ClearAttributeOperation(AttributeName);

    protected override void Fill(BatchOperation operation) => Attribute = ((ClearAttributeOperation)operation).Attribute;
}

/// <summary>Add values (templates, one per line) to a multi-valued attribute.</summary>
public sealed partial class AddValuesEditorViewModel : AttributeOperationEditorViewModel
{
    private readonly TemplateSample _sample;

    internal AddValuesEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.AddValues)
    {
        _sample = new TemplateSample(context, s => Sample = s);
        Revalidate();
    }

    /// <summary>One value template per line.</summary>
    [ObservableProperty]
    public partial string ValuesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Sample { get; private set; }

    partial void OnValuesTextChanged(string value)
    {
        OnChanged();
        _sample.UpdateAsync(string.Join("; ", SetValuesEditorViewModel.Lines(value))).Forget();
    }

    internal override void RefreshSample() => _sample.UpdateAsync(string.Join("; ", SetValuesEditorViewModel.Lines(ValuesText))).Forget();

    protected override string? Validate()
    {
        var error = base.Validate();
        if (error is not null)
        {
            return error;
        }

        var lines = SetValuesEditorViewModel.Lines(ValuesText);
        return lines.Length == 0 ? Localizer[L.Batch.ValuesRequired] : lines.Select(ValidateTemplate).FirstOrDefault(e => e is not null);
    }

    protected override BatchOperation BuildCore() => new AddValuesOperation(AttributeName, SetValuesEditorViewModel.Lines(ValuesText));

    protected override void Fill(BatchOperation operation)
    {
        var add = (AddValuesOperation)operation;
        Attribute = add.Attribute;
        ValuesText = string.Join(Environment.NewLine, add.ValueTemplates);
    }
}

/// <summary>Remove values (exact, one per line, case-insensitive) from a multi-valued attribute.</summary>
public sealed partial class RemoveValuesEditorViewModel : AttributeOperationEditorViewModel
{
    internal RemoveValuesEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.RemoveValues)
    {
        Revalidate();
    }

    [ObservableProperty]
    public partial string ValuesText { get; set; } = string.Empty;

    partial void OnValuesTextChanged(string value) => OnChanged();

    protected override string? Validate() =>
        base.Validate() ?? (SetValuesEditorViewModel.Lines(ValuesText).Length == 0 ? Localizer[L.Batch.ValuesRequired] : null);

    protected override BatchOperation BuildCore() => new RemoveValuesOperation(AttributeName, SetValuesEditorViewModel.Lines(ValuesText));

    protected override void Fill(BatchOperation operation)
    {
        var remove = (RemoveValuesOperation)operation;
        Attribute = remove.Attribute;
        ValuesText = string.Join(Environment.NewLine, remove.Values);
    }
}

/// <summary>Find and replace in every value of an attribute (plain text or regular expression).</summary>
public sealed partial class FindReplaceEditorViewModel : AttributeOperationEditorViewModel
{
    internal FindReplaceEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.FindReplace)
    {
        Revalidate();
    }

    [ObservableProperty]
    public partial string Find { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Replace { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    partial void OnFindChanged(string value) => OnChanged();

    partial void OnReplaceChanged(string value) => OnChanged();

    partial void OnUseRegexChanged(bool value) => OnChanged();

    partial void OnMatchCaseChanged(bool value) => OnChanged();

    protected override string? Validate()
    {
        var error = base.Validate();
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrEmpty(Find))
        {
            return Localizer[L.Batch.FindRequired];
        }

        if (UseRegex)
        {
            try
            {
                _ = new Regex(Find, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return Localizer[L.Batch.InvalidRegex];
            }
        }

        return null;
    }

    protected override BatchOperation BuildCore() => new FindReplaceOperation(AttributeName, Find, Replace ?? string.Empty, UseRegex, MatchCase);

    protected override void Fill(BatchOperation operation)
    {
        var findReplace = (FindReplaceOperation)operation;
        Attribute = findReplace.Attribute;
        Find = findReplace.Find;
        Replace = findReplace.Replace;
        UseRegex = findReplace.UseRegex;
        MatchCase = findReplace.MatchCase;
    }
}

/// <summary>Enable or disable accounts.</summary>
public sealed partial class EnableDisableEditorViewModel : OperationEditorViewModel
{
    internal EnableDisableEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.EnableDisable)
    {
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Disable))]
    public partial bool Enable { get; set; } = true;

    /// <summary>Radio-button helper.</summary>
    public bool Disable
    {
        get => !Enable;
        set => Enable = !value;
    }

    partial void OnEnableChanged(bool value) => OnChanged();

    protected override string? Validate() => null;

    protected override BatchOperation BuildCore() => new SetAccountEnabledOperation(Enable);

    protected override void Fill(BatchOperation operation) => Enable = ((SetAccountEnabledOperation)operation).Enabled;
}

/// <summary>Set or clear one userAccountControl option.</summary>
public sealed partial class AccountFlagEditorViewModel : OperationEditorViewModel
{
    /// <summary>Options an operator may change in a batch (account type and computed flags are excluded).</summary>
    public static IReadOnlyList<UserAccountControl> SettableFlags { get; } =
    [
        UserAccountControl.DontExpirePassword, UserAccountControl.PasswordNotRequired, UserAccountControl.EncryptedTextPasswordAllowed,
        UserAccountControl.SmartcardRequired, UserAccountControl.NotDelegated, UserAccountControl.UseDesKeyOnly,
        UserAccountControl.DontRequirePreauth, UserAccountControl.TrustedForDelegation, UserAccountControl.TrustedToAuthForDelegation,
        UserAccountControl.HomeDirRequired, UserAccountControl.Script,
    ];

    internal AccountFlagEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.AccountFlag)
    {
        Flags = [.. SettableFlags.Select(f => new Option<UserAccountControl>(f, context.Localizer.UacFlagName(f)))];
        SelectedFlag = Flags[0];
    }

    public IReadOnlyList<Option<UserAccountControl>> Flags { get; }

    [ObservableProperty]
    public partial Option<UserAccountControl>? SelectedFlag { get; set; }

    /// <summary>True = set the option, false = clear it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Clear))]
    public partial bool Set { get; set; } = true;

    /// <summary>Radio-button helper.</summary>
    public bool Clear
    {
        get => !Set;
        set => Set = !value;
    }

    partial void OnSelectedFlagChanged(Option<UserAccountControl>? value) => OnChanged();

    partial void OnSetChanged(bool value) => OnChanged();

    protected override string? Validate() => SelectedFlag is null ? Localizer[L.Batch.OptionRequired] : null;

    protected override BatchOperation BuildCore() => new SetAccountFlagOperation(SelectedFlag!.Value, Set);

    protected override void Fill(BatchOperation operation)
    {
        var flag = (SetAccountFlagOperation)operation;
        SelectedFlag = Flags.FirstOrDefault(f => f.Value == flag.Flag) ?? new Option<UserAccountControl>(flag.Flag, Localizer.UacFlagName(flag.Flag));
        Set = flag.Value;
    }
}

/// <summary>Unlock locked-out accounts.</summary>
public sealed class UnlockEditorViewModel : OperationEditorViewModel
{
    internal UnlockEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.Unlock)
    {
    }

    protected override string? Validate() => null;

    protected override BatchOperation BuildCore() => new UnlockAccountOperation();

    protected override void Fill(BatchOperation operation)
    {
    }
}

/// <summary>"User must change password at next logon" on or off.</summary>
public sealed partial class RequirePasswordChangeEditorViewModel : OperationEditorViewModel
{
    internal RequirePasswordChangeEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.RequirePasswordChange)
    {
    }

    [ObservableProperty]
    public partial bool Required { get; set; } = true;

    partial void OnRequiredChanged(bool value) => OnChanged();

    protected override string? Validate() => null;

    protected override BatchOperation BuildCore() => new RequirePasswordChangeOperation(Required);

    protected override void Fill(BatchOperation operation) => Required = ((RequirePasswordChangeOperation)operation).Required;
}

/// <summary>Account expires: never / end of a day (stored as the following local midnight, like ADUC).</summary>
public sealed partial class AccountExpirationEditorViewModel : OperationEditorViewModel
{
    internal AccountExpirationEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.AccountExpiration)
    {
    }

    [ObservableProperty]
    public partial bool IsNever { get; set; } = true;

    [ObservableProperty]
    public partial DateTime? EndOfDate { get; set; }

    partial void OnIsNeverChanged(bool value) => OnChanged();

    partial void OnEndOfDateChanged(DateTime? value) => OnChanged();

    protected override string? Validate() => !IsNever && EndOfDate is null ? Localizer[L.Fields.DateRequired] : null;

    protected override BatchOperation BuildCore()
    {
        if (IsNever || EndOfDate is not { } date)
        {
            return new SetAccountExpirationOperation(null);
        }

        var zone = Context.Services.Time.LocalTimeZone;
        var midnight = DateTime.SpecifyKind(date.Date.AddDays(1), DateTimeKind.Unspecified);
        return new SetAccountExpirationOperation(new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)));
    }

    protected override void Fill(BatchOperation operation)
    {
        var expiration = (SetAccountExpirationOperation)operation;
        if (expiration.ExpiresAt is { } expires)
        {
            var local = TimeZoneInfo.ConvertTime(expires, Context.Services.Time.LocalTimeZone);
            EndOfDate = local.TimeOfDay == TimeSpan.Zero ? local.Date.AddDays(-1) : local.Date;
            IsNever = false;
        }
        else
        {
            IsNever = true;
        }
    }
}

/// <summary>
/// Password reset: generated unique passwords (default) or one fixed password typed by the operator. The fixed
/// password is read from <see cref="PasswordInput"/>/<see cref="ConfirmPasswordInput"/> (set by the view, PasswordBox)
/// only when the operation is built; it is never exposed as a property.
/// </summary>
public sealed partial class ResetPasswordEditorViewModel : OperationEditorViewModel
{
    internal ResetPasswordEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.ResetPassword)
    {
    }

    /// <summary>True = a unique random password per account; false = the password typed by the operator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseFixedPassword))]
    public partial bool Generate { get; set; } = true;

    public bool UseFixedPassword
    {
        get => !Generate;
        set => Generate = !value;
    }

    [ObservableProperty]
    public partial int GeneratedLength { get; set; } = PasswordGenerator.DefaultLength;

    [ObservableProperty]
    public partial bool MustChangeAtNextLogon { get; set; } = true;

    [ObservableProperty]
    public partial bool UnlockAccount { get; set; } = true;

    /// <summary>Password box of the fixed password (set by the view).</summary>
    public IPasswordInput? PasswordInput { get; set; }

    /// <summary>Confirmation password box (set by the view).</summary>
    public IPasswordInput? ConfirmPasswordInput { get; set; }

    /// <summary>
    /// Localized problem of the typed password ("required", "do not match") found by the last Preview. The password
    /// boxes cannot be observed, so they are checked when the operation is built; the error does not disable Preview.
    /// </summary>
    [ObservableProperty]
    public partial string? PasswordError { get; private set; }

    public int MinimumLength => PasswordGenerator.MinimumLength;

    public int MaximumLength => PasswordGenerator.MaximumLength;

    /// <summary>Call from the view when the operator typed in a password box (clears <see cref="PasswordError"/>).</summary>
    public void NotifyPasswordChanged() => PasswordError = null;

    partial void OnGenerateChanged(bool value)
    {
        PasswordError = null;
        OnChanged();
    }

    partial void OnGeneratedLengthChanged(int value) => OnChanged();

    partial void OnMustChangeAtNextLogonChanged(bool value) => OnChanged();

    partial void OnUnlockAccountChanged(bool value) => OnChanged();

    /// <summary>Never print the password.</summary>
    public override string ToString() => $"{nameof(ResetPasswordEditorViewModel)} {{ Generate = {Generate} }}";

    protected override string? Validate() =>
        Generate && GeneratedLength is < PasswordGenerator.MinimumLength or > PasswordGenerator.MaximumLength
            ? Localizer.Format(L.Batch.PasswordLength, PasswordGenerator.MinimumLength, PasswordGenerator.MaximumLength)
            : null;

    /// <summary>The typed password is checked only here (typing in a PasswordBox raises no change notification).</summary>
    protected override string? ValidateBuild()
    {
        PasswordError = null;
        if (Generate)
        {
            return null;
        }

        var password = PasswordInput?.GetPassword() ?? string.Empty;
        if (password.Length == 0)
        {
            PasswordError = Localizer[L.Batch.PasswordRequired];
        }
        else if (!string.Equals(password, ConfirmPasswordInput?.GetPassword() ?? string.Empty, StringComparison.Ordinal))
        {
            PasswordError = Localizer[L.Batch.PasswordMismatch];
        }

        return PasswordError;
    }

    /// <summary>The fixed password is read from the password box only here, when the operation is built.</summary>
    protected override BatchOperation BuildCore() => Generate
        ? new ResetPasswordOperation(PasswordMode.GenerateUnique, null, MustChangeAtNextLogon, UnlockAccount, GeneratedLength)
        : new ResetPasswordOperation(PasswordMode.Fixed, PasswordInput?.GetPassword(), MustChangeAtNextLogon, UnlockAccount);

    protected override void Fill(BatchOperation operation)
    {
        var reset = (ResetPasswordOperation)operation;
        Generate = reset.Mode == PasswordMode.GenerateUnique;
        GeneratedLength = reset.GeneratedLength;
        MustChangeAtNextLogon = reset.MustChangeAtNextLogon;
        UnlockAccount = reset.UnlockAccount;
    }
}

/// <summary>Move to another container (container picker).</summary>
public sealed partial class MoveEditorViewModel : OperationEditorViewModel
{
    internal MoveEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.Move)
    {
        Revalidate();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    public partial string TargetContainerDn { get; set; } = string.Empty;

    /// <summary>Canonical path of the target ("contoso.test/Firma/IT").</summary>
    public string TargetPath => TargetContainerDn.Length == 0 ? string.Empty : AddressResolver.ToDisplayPath(TargetContainerDn);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var session = Context.Services.Sessions.Current;
        if (session is null)
        {
            return;
        }

        var picked = await Context.Services.Dialogs.PickContainerAsync(session, TargetContainerDn.Length == 0 ? null : TargetContainerDn)
            .ConfigureAwait(true);
        if (picked is not null)
        {
            TargetContainerDn = picked;
        }
    }

    partial void OnTargetContainerDnChanged(string value) => OnChanged();

    protected override string? Validate() =>
        Core.Ldap.DistinguishedName.TryParse(TargetContainerDn, out var dn) && !dn.IsRoot ? null : Localizer[L.Batch.TargetRequired];

    protected override BatchOperation BuildCore() => new MoveOperation(TargetContainerDn.Trim());

    protected override void Fill(BatchOperation operation) => TargetContainerDn = ((MoveOperation)operation).TargetContainerDn;
}

/// <summary>Rename (new name from a template), optionally also setting displayName.</summary>
public sealed partial class RenameEditorViewModel : OperationEditorViewModel
{
    private readonly TemplateSample _sample;

    internal RenameEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.Rename)
    {
        _sample = new TemplateSample(context, s => Sample = s);
        Revalidate();
    }

    [ObservableProperty]
    public partial string NameTemplate { get; set; } = "{givenName} {sn}";

    [ObservableProperty]
    public partial bool AlsoSetDisplayName { get; set; } = true;

    [ObservableProperty]
    public partial string? Sample { get; private set; }

    partial void OnNameTemplateChanged(string value)
    {
        OnChanged();
        _sample.UpdateAsync(value).Forget();
    }

    partial void OnAlsoSetDisplayNameChanged(bool value) => OnChanged();

    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(NameTemplate))
        {
            return Localizer[L.Batch.NameRequired];
        }

        return Context.Templates.Parse(NameTemplate).IsValid ? null : Localizer[L.Batch.InvalidTemplate];
    }

    protected override BatchOperation BuildCore() => new RenameOperation(NameTemplate, AlsoSetDisplayName);

    protected override void Fill(BatchOperation operation)
    {
        var rename = (RenameOperation)operation;
        NameTemplate = rename.NameTemplate;
        AlsoSetDisplayName = rename.AlsoSetDisplayName;
    }

    internal override void RefreshSample() => _sample.UpdateAsync(NameTemplate).Forget();
}

/// <summary>Add to / remove from groups (object picker).</summary>
public sealed partial class GroupsEditorViewModel : OperationEditorViewModel
{
    internal GroupsEditorViewModel(OperationEditorContext context, OperationType type)
        : base(context, type)
    {
        if (type is not (OperationType.AddToGroups or OperationType.RemoveFromGroups))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only group operations are supported.");
        }

        Groups.CollectionChanged += (_, _) => OnChanged();
        Revalidate();
    }

    public ObservableCollection<DirectoryItem> Groups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveGroupCommand))]
    public partial DirectoryItem? SelectedGroup { get; set; }

    [RelayCommand]
    private async Task AddGroupsAsync()
    {
        var session = Context.Services.Sessions.Current;
        if (session is null)
        {
            return;
        }

        var picked = await Context.Services.Dialogs.PickObjectsAsync(session, [ObjectKind.Group], multiSelect: true).ConfigureAwait(true);
        var added = picked.Where(dn => !Groups.Any(g => Core.Ldap.DistinguishedName.AreEqual(g.Dn, dn))).ToList();
        foreach (var dn in added)
        {
            Groups.Add(DirectoryItem.Unresolved(dn, Localizer));
        }

        await ResolveAsync(added).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveGroup))]
    private void RemoveGroup(DirectoryItem? group)
    {
        if ((group ?? SelectedGroup) is { } target)
        {
            Groups.Remove(target);
        }
    }

    private bool CanRemoveGroup(DirectoryItem? group) => (group ?? SelectedGroup) is not null;

    protected override string? Validate() => Groups.Count == 0 ? Localizer[L.Batch.GroupsRequired] : null;

    protected override BatchOperation BuildCore() => Type == OperationType.AddToGroups
        ? new AddToGroupsOperation([.. Groups.Select(g => g.Dn)])
        : new RemoveFromGroupsOperation([.. Groups.Select(g => g.Dn)]);

    protected override void Fill(BatchOperation operation)
    {
        IReadOnlyList<string> dns = operation switch
        {
            AddToGroupsOperation add => add.GroupDns,
            RemoveFromGroupsOperation remove => remove.GroupDns,
            _ => [],
        };
        foreach (var dn in dns)
        {
            Groups.Add(DirectoryItem.Unresolved(dn, Localizer));
        }

        ResolveAsync(dns).Forget();
    }

    private async Task ResolveAsync(IReadOnlyList<string> dns)
    {
        var session = Context.Services.Sessions.Current;
        if (session is null || dns.Count == 0)
        {
            return;
        }

        try
        {
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, dns, DirectoryItem.Attributes, CancellationToken.None)).ConfigureAwait(true);
            for (var i = 0; i < Groups.Count; i++)
            {
                if (found.TryGetValue(Lists.DnKey.For(Groups[i].Dn), out var obj))
                {
                    Groups[i] = DirectoryItem.From(obj, Localizer);
                }
            }
        }
        catch (Core.Directory.DirectoryException)
        {
            // Names stay RDN values.
        }
    }
}

/// <summary>Delete objects (cannot be combined with other operations).</summary>
public sealed partial class DeleteEditorViewModel : OperationEditorViewModel
{
    internal DeleteEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.Delete)
    {
    }

    /// <summary>Delete containers together with everything they contain (tree delete).</summary>
    [ObservableProperty]
    public partial bool IncludeSubtree { get; set; }

    partial void OnIncludeSubtreeChanged(bool value) => OnChanged();

    protected override string? Validate() => null;

    protected override BatchOperation BuildCore() => new DeleteOperation(IncludeSubtree);

    protected override void Fill(BatchOperation operation) => IncludeSubtree = ((DeleteOperation)operation).IncludeSubtree;
}
