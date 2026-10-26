using System.Text.Json;
using AdMng.Core.Messaging;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// The message templates: the built-ins (names in the UI language, read-only) and the operator's templates stored in the
/// settings (text with fields only – like saved views, never directory data). Export / import as JSON for the team.
/// </summary>
public sealed class UserMessageTemplateLibrary
{
    /// <summary>Format marker of exported files.</summary>
    public const string ExportFormat = "AdMng.MessageTemplates";

    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;

    public UserMessageTemplateLibrary(ISettingsStore settings, ILocalizer localizer)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>Raised after a template was saved, deleted or imported.</summary>
    public event EventHandler? Changed;

    /// <summary>Built-ins first (in their fixed order), then the operator's templates by name.</summary>
    public IReadOnlyList<MessageTemplate> All =>
        [.. BuiltInMessageTemplates.All(UiLanguage), .. Custom.OrderBy(t => t.Name, StringComparer.Create(_localizer.Culture, true))];

    /// <summary>The operator's templates.</summary>
    public IReadOnlyList<MessageTemplate> Custom => _settings.Current.UserMessages.Templates;

    public static bool IsBuiltIn(string? id) => BuiltInMessageTemplates.IsBuiltIn(id);

    public MessageTemplate? Find(string? id) =>
        id is null ? null : BuiltInMessageTemplates.Get(id, UiLanguage) ?? Custom.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Saves an operator template: a built-in or an unknown id is stored as a new template (new id), an existing operator
    /// template is replaced. Returns the stored template.
    /// </summary>
    public async Task<MessageTemplate> SaveAsync(MessageTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Name);
        var stored = IsBuiltIn(template.Id) || Custom.All(t => t.Id != template.Id)
            ? template with { Id = Guid.NewGuid().ToString("N"), Name = template.Name.Trim() }
            : template with { Name = template.Name.Trim() };
        await _settings.UpdateAsync(s =>
        {
            var templates = s.UserMessages.Templates;
            var index = templates.FindIndex(t => t.Id == stored.Id);
            if (index >= 0)
            {
                templates[index] = stored;
            }
            else
            {
                templates.Add(stored);
            }
        }).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
        return stored;
    }

    /// <summary>Deletes an operator template (built-ins cannot be deleted).</summary>
    public async Task DeleteAsync(string id)
    {
        if (IsBuiltIn(id))
        {
            return;
        }

        await _settings.UpdateAsync(s => s.UserMessages.Templates.RemoveAll(t => t.Id == id)).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>JSON of the operator's templates (or the given ones).</summary>
    public string ExportJson(IEnumerable<MessageTemplate>? templates = null)
    {
        var file = new TemplatesFile { Format = ExportFormat, Version = 1, Templates = [.. templates ?? Custom] };
        return JsonSerializer.Serialize(file, JsonSettingsStore.SerializerOptions);
    }

    /// <summary>
    /// Imports templates from JSON: valid ones are added as copies with new ids (imports never overwrite). Returns the
    /// number added.
    /// </summary>
    /// <exception cref="FormatException">Not a templates file.</exception>
    /// <exception cref="JsonException">Unreadable JSON.</exception>
    public async Task<int> ImportAsync(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var file = JsonSerializer.Deserialize<TemplatesFile>(json, JsonSettingsStore.SerializerOptions);
        if (file is null || !string.Equals(file.Format, ExportFormat, StringComparison.Ordinal) || file.Templates is null)
        {
            throw new FormatException("Not an AD Manager message templates file.");
        }

        var valid = file.Templates
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => t with
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = t.Name.Trim(),
                Polish = t.Polish ?? new(),
                English = t.English ?? new(),
                Condition = Enum.IsDefined(t.Condition) ? t.Condition : MessageConditionKind.None,
                Recipients = Enum.IsDefined(t.Recipients) ? t.Recipients : MessageRecipientMode.User,
                ConditionDays = Math.Clamp(t.ConditionDays, 0, 3650),
            })
            .ToList();
        if (valid.Count == 0)
        {
            return 0;
        }

        await _settings.UpdateAsync(s =>
        {
            s.UserMessages.Templates.AddRange(valid);
            s.UserMessages.Normalize();
        }).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
        return valid.Count;
    }

    private MessageLanguage UiLanguage => _localizer.Culture.TwoLetterISOLanguageName == "pl" ? MessageLanguage.Polish : MessageLanguage.English;

    private sealed class TemplatesFile
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public List<MessageTemplate>? Templates { get; set; }
    }
}
