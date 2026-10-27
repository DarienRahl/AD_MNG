using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Directory;
using AdMng.Core.Messaging;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// "Wyślij wiadomość" (README U1, design note docs/dev/u1-user-messages.md): compose (template, language, condition,
/// recipient mode, inputs, recipients) → plan (fresh read from the window's DC) → preview (every message, filter, exclude,
/// test message to me) → send in the background (rate limit, retries, cancel, typed confirmation above the threshold) →
/// report (per recipient, retry failed, CSV on demand, one session log entry without content). Rendered messages and the
/// report exist only in memory while the window is open.
/// </summary>
public sealed partial class UserMessageComposerViewModel : WindowViewModelBase
{
    /// <summary>Session log action of a send (<see cref="AuditEntry.Action"/>).</summary>
    public const string SessionLogAction = "SendUserMessages";

    private readonly UserMessageServices _messages;
    private readonly SerialCancellation _plans = new();
    private readonly List<UserMessageItemViewModel> _items = [];
    private MessageTemplate _working;
    private bool _loading;
    private CancellationTokenSource? _sending;
    private IUserMessageTransport? _transport;
    private Task _currentRun = Task.CompletedTask;

    public UserMessageComposerViewModel(DialogServices services, UserMessageServices messages, UserMessageRequest request)
        : base(services)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        ArgumentNullException.ThrowIfNull(request);
        Recipients = [.. request.DistinguishedNames
            .Where(dn => !string.IsNullOrWhiteSpace(dn))
            .DistinctBy(DnKey.For, StringComparer.Ordinal)
            .Select(dn => new UserMessageRecipientViewModel(dn))];
        Recipients.CollectionChanged += (_, _) => OnRecipientsChanged();
        Languages = [new(MessageLanguage.Polish, Localizer[L.UserMessages.LanguagePolish]), new(MessageLanguage.English, Localizer[L.UserMessages.LanguageEnglish])];
        Conditions = [.. Enum.GetValues<MessageConditionKind>().Select(k => new Option<MessageConditionKind>(k, Localizer[UserMessageText.ConditionKey(k)]))];
        RecipientModes = [.. Enum.GetValues<MessageRecipientMode>().Select(m => new Option<MessageRecipientMode>(m, Localizer[UserMessageText.ModeKey(m)]))];
        Filters = [.. Enum.GetValues<UserMessagePreviewFilter>().Select(f => new Option<UserMessagePreviewFilter>(f, Localizer[UserMessageText.FilterKey(f)]))];
        var template = _messages.Templates.Find(request.TemplateId) ?? _messages.Templates.Find(BuiltInMessageTemplates.Custom)!;
        _working = template;
        _loading = true;
        Filter = Filters[0];
        EditLanguage = Languages.First(l => l.Value == services.Settings.Current.UserMessages.Language);
        Condition = Conditions.First(c => c.Value == template.Condition);
        RecipientMode = RecipientModes.First(m => m.Value == template.Recipients);
        Templates = CreateTemplateOptions();
        SelectedTemplate = Templates.First(t => t.Value == template.Id);
        _loading = false;
        LoadTemplate(template, request.ConditionDays);
        _messages.Templates.Changed += OnTemplatesChanged;
    }

    public override string WindowTitle => Localizer.Format(L.UserMessages.WindowTitle, Recipients.Count);

    // ---- Stage ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompose), nameof(IsPreview), nameof(IsSending), nameof(IsReport))]
    [NotifyCanExecuteChangedFor(nameof(PlanCommand), nameof(SendCommand), nameof(SendTestCommand), nameof(BackCommand),
        nameof(RetryFailedCommand), nameof(ExportReportCommand), nameof(CancelSendCommand), nameof(AddRecipientsCommand), nameof(RemoveRecipientCommand))]
    public partial UserMessageStage Stage { get; private set; }

    public bool IsCompose => Stage == UserMessageStage.Compose;

    public bool IsPreview => Stage == UserMessageStage.Preview;

    public bool IsSending => Stage == UserMessageStage.Sending;

    public bool IsReport => Stage == UserMessageStage.Report;

    /// <summary>Reading or planning, or a test message is on its way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlanCommand), nameof(SendCommand), nameof(SendTestCommand), nameof(RetryFailedCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Localized error of the last action (template errors, read failure, transport problem).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Completes when the current send finished (tests).</summary>
    public Task CurrentRun => _currentRun;

    // ---- Transport -----------------------------------------------------------------------------------------------

    /// <summary>"Wysyłka bezpośrednio przez SMTP: smtp.contoso.com:587" / "przez AdMng Watchdog" / why none can be used.</summary>
    [ObservableProperty]
    public partial string? TransportText { get; private set; }

    /// <summary>Direct SMTP with user name + password and no password in this session yet: the window shows a password box.</summary>
    [ObservableProperty]
    public partial bool NeedsSmtpPassword { get; private set; }

    /// <summary>The window's password box (read only when sending; the password then lives in the session memory only).</summary>
    public IPasswordInput? SmtpPasswordInput { get; set; }

    // ---- Recipients ----------------------------------------------------------------------------------------------

    public ObservableCollection<UserMessageRecipientViewModel> Recipients { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRecipientCommand))]
    public partial UserMessageRecipientViewModel? SelectedRecipient { get; set; }

    public string RecipientsText => Localizer.Format(L.UserMessages.RecipientsCount, Recipients.Count);

    // ---- Template ------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> Templates { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateCommand))]
    public partial Option<string>? SelectedTemplate { get; set; }

    /// <summary>The selected template is a built-in one (it is saved as a copy).</summary>
    public bool IsBuiltInTemplate => UserMessageTemplateLibrary.IsBuiltIn(SelectedTemplate?.Value);

    /// <summary>Name under which "Zapisz szablon" stores the edited template.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    public partial string TemplateName { get; set; } = string.Empty;

    public IReadOnlyList<Option<MessageLanguage>> Languages { get; }

    /// <summary>The language whose texts the editor shows.</summary>
    [ObservableProperty]
    public partial Option<MessageLanguage> EditLanguage { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    /// <summary>Optional HTML body (empty = generated from the text).</summary>
    [ObservableProperty]
    public partial string Html { get; set; } = string.Empty;

    public IReadOnlyList<Option<MessageConditionKind>> Conditions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConditionDays))]
    public partial Option<MessageConditionKind> Condition { get; set; }

    public bool ShowConditionDays => MessageConditions.UsesDays(Condition.Value);

    [ObservableProperty]
    public partial int ConditionDays { get; set; }

    public IReadOnlyList<Option<MessageRecipientMode>> RecipientModes { get; }

    [ObservableProperty]
    public partial Option<MessageRecipientMode> RecipientMode { get; set; }

    /// <summary>Values of the template's <c>{$input:name}</c> fields.</summary>
    public ObservableCollection<UserMessageInputViewModel> Inputs { get; } = [];

    public bool HasInputs => Inputs.Count > 0;

    /// <summary>Field examples for the editor ("Wstaw pole").</summary>
    public IReadOnlyList<string> FieldExamples => UserMessageFields.Examples;

    /// <summary>Errors of the edited template (syntax, forbidden fields, HTML), or null.</summary>
    [ObservableProperty]
    public partial string? TemplateErrors { get; private set; }

    // ---- Preview -------------------------------------------------------------------------------------------------

    public IReadOnlyList<Option<UserMessagePreviewFilter>> Filters { get; }

    [ObservableProperty]
    public partial Option<UserMessagePreviewFilter> Filter { get; set; }

    /// <summary>The messages of the plan shown with the current filter.</summary>
    public ObservableCollection<UserMessageItemViewModel> Items { get; } = [];

    /// <summary>Every message of the plan (the report too).</summary>
    public IReadOnlyList<UserMessageItemViewModel> AllItems => _items;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestCommand))]
    public partial UserMessageItemViewModel? SelectedItem { get; set; }

    /// <summary>"Gotowe: 3 · Z ostrzeżeniami: 1 · Błędy: 1 · Pominięte: 2 · Wykluczone: 0".</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    /// <summary>Messages that will be sent (included and sendable).</summary>
    public int SendCount => _items.Count(i => i.CanInclude && i.IsIncluded);

    public string SendButtonText => Localizer.Format(L.UserMessages.SendCount, SendCount);

    /// <summary>Outcome of "Wyślij wiadomość testową do mnie".</summary>
    [ObservableProperty]
    public partial string? TestResultText { get; private set; }

    // ---- Sending and report --------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial int ProgressValue { get; private set; }

    [ObservableProperty]
    public partial int ProgressMaximum { get; private set; } = 1;

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial string? ReportSummary { get; private set; }

    // ---- Lifecycle -----------------------------------------------------------------------------------------------

    public override async Task InitializeAsync()
    {
        await RefreshTransportAsync().ConfigureAwait(true);
    }

    public override async Task<bool> CanCloseAsync()
    {
        if (Stage != UserMessageStage.Sending)
        {
            return true;
        }

        if (!await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer[L.UserMessages.StopOnClose], destructive: true).ConfigureAwait(true))
        {
            return false;
        }

        _sending?.Cancel();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        _messages.Templates.Changed -= OnTemplatesChanged;
        _plans.Cancel();
        _sending?.Cancel();
        base.Dispose(disposing);
    }

    protected override void OnSessionEnded()
    {
        _plans.Cancel();
        PlanCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        AddRecipientsCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(RecipientsText));
        OnPropertyChanged(nameof(SendButtonText));
    }

    // ---- Compose commands ----------------------------------------------------------------------------------------

    /// <summary>Adds accounts chosen in the object picker (users).</summary>
    [RelayCommand(CanExecute = nameof(CanEditRecipients))]
    private async Task AddRecipientsAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.User], multiSelect: true).ConfigureAwait(true);
        foreach (var dn in picked.Where(dn => Recipients.All(r => DnKey.For(r.DistinguishedName) != DnKey.For(dn))))
        {
            Recipients.Add(new UserMessageRecipientViewModel(dn));
        }
    }

    private bool CanEditRecipients() => Session is not null && Stage == UserMessageStage.Compose;

    [RelayCommand(CanExecute = nameof(CanRemoveRecipient))]
    private void RemoveRecipient()
    {
        if (SelectedRecipient is { } recipient)
        {
            Recipients.Remove(recipient);
        }
    }

    private bool CanRemoveRecipient() => SelectedRecipient is not null && Stage == UserMessageStage.Compose;

    /// <summary>Appends a field to the text body.</summary>
    [RelayCommand]
    private void InsertField(string? field)
    {
        if (!string.IsNullOrEmpty(field))
        {
            Body += field;
        }
    }

    /// <summary>Stores the edited template under <see cref="TemplateName"/> (a built-in one as a copy).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private async Task SaveTemplateAsync()
    {
        var name = TemplateName.Trim();
        var current = SelectedTemplate?.Value;
        var asNew = IsBuiltInTemplate || !string.Equals(_messages.Templates.Find(current)?.Name, name, StringComparison.CurrentCulture);
        var template = _working with
        {
            Id = asNew ? Guid.NewGuid().ToString("N") : current!,
            Name = name,
            ConditionDays = ConditionDays,
        };
        var stored = await _messages.Templates.SaveAsync(template, _images.Values).ConfigureAwait(true);
        SelectTemplate(stored.Id);
        Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.UserMessages.TemplateSaved, stored.Name));
        await WarnWhenImageFolderIsLargeAsync().ConfigureAwait(true);
    }

    private bool CanSaveTemplate() => !string.IsNullOrWhiteSpace(TemplateName);

    [RelayCommand(CanExecute = nameof(CanDeleteTemplate))]
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is not { } selected || IsBuiltInTemplate
            || !await Services.Dialogs.ConfirmAsync(Localizer[L.UserMessages.DeleteTemplate], Localizer.Format(L.UserMessages.DeleteTemplateConfirm, selected.Text), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await _messages.Templates.DeleteAsync(selected.Value).ConfigureAwait(true);
        SelectTemplate(BuiltInMessageTemplates.Custom);
    }

    private bool CanDeleteTemplate() => SelectedTemplate is not null && !IsBuiltInTemplate;

    [RelayCommand]
    private async Task ExportTemplatesAsync()
    {
        var title = Localizer[L.UserMessages.ExportTemplates];
        if (_messages.Templates.Custom.Count == 0)
        {
            await Services.Dialogs.ShowInfoAsync(title, Localizer[L.UserMessages.NoCustomTemplates]).ConfigureAwait(true);
            return;
        }

        var path = await Services.Files.SaveFileAsync(title, "szablony-wiadomosci.json", [new FileFilter(Localizer[L.Files.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            var json = await _messages.Templates.ExportJsonAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, _messages.Templates.Custom.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ImportTemplatesAsync()
    {
        var title = Localizer[L.UserMessages.ImportTemplates];
        var path = await Services.Files.OpenFileAsync(title, [new FileFilter(Localizer[L.Files.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            var count = await ImportTemplatesWithImagesAsync(await File.ReadAllTextAsync(path).ConfigureAwait(true)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.UserMessages.TemplatesImported, count));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer[L.UserMessages.NotATemplatesFile]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    // ---- Plan and preview ----------------------------------------------------------------------------------------

    /// <summary>"Przygotuj podgląd": fresh read of every recipient from the window's DC, render and validate.</summary>
    [RelayCommand(CanExecute = nameof(CanPlan))]
    private Task PlanAsync() => PlanCoreAsync([.. Recipients.Select(r => r.DistinguishedName)]);

    private bool CanPlan() => Session is not null && !IsBusy && Stage is UserMessageStage.Compose or UserMessageStage.Preview && Recipients.Count > 0;

    /// <summary>Back to the editor (from the preview or the report).</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        Stage = UserMessageStage.Compose;
        TestResultText = null;
    }

    private bool CanGoBack() => Stage is UserMessageStage.Preview or UserMessageStage.Report;

    /// <summary>"Wyślij wiadomość testową do mnie": the selected message to the test address of the settings (no CC).</summary>
    [RelayCommand(CanExecute = nameof(CanSendTest))]
    private async Task SendTestAsync()
    {
        if (SelectedItem?.Planned is not { Message: { } message } planned)
        {
            return;
        }

        var settings = Services.Settings.Current.UserMessages;
        if (!UserMessageRules.TryParseAddress(settings.TestAddress, out var address))
        {
            TestResultText = Localizer[L.UserMessages.TestAddressMissing];
            return;
        }

        if (await EnsureTransportAsync().ConfigureAwait(true) is not { } transport
            || !await FitsTransportAsync(transport, [planned], test: true).ConfigureAwait(true))
        {
            return;
        }

        var test = planned with
        {
            Status = PlannedMessageStatus.Ready,
            Message = message with
            {
                Id = Guid.NewGuid(),
                To = [new MessageAddress(address)],
                Cc = [],
                Subject = UserMessageRules.HeaderSafe("[TEST] " + message.Subject),
            },
        };
        IsBusy = true;
        TestResultText = Localizer[L.UserMessages.TestSending];
        try
        {
            ApplyRateSettings();
            var results = await Task.Run(() => _messages.Dispatcher.SendAsync(transport, [test], _messages.RateLimiter, _messages.DispatchOptions, null, CancellationToken.None)).ConfigureAwait(true);
            var result = results[0];
            TestResultText = result.Status == UserMessageSendStatus.Accepted
                ? Localizer.Format(L.UserMessages.TestSent, address)
                : Localizer.Format(L.UserMessages.TestFailed, UserMessageText.ResultDetail(Localizer, result));
            AfterRun(results);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSendTest() => SelectedItem?.Planned.Message is not null && !IsBusy && Stage == UserMessageStage.Preview;

    /// <summary>Sends the included messages (confirmation; typed above the threshold of the settings).</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var items = _items.Where(i => i.CanInclude && i.IsIncluded).ToList();
        if (items.Count == 0 || await EnsureTransportAsync().ConfigureAwait(true) is not { } transport
            || !await FitsTransportAsync(transport, [.. items.Select(i => i.Planned)], test: false).ConfigureAwait(true))
        {
            return;
        }

        var count = items.Count;
        var title = Localizer[L.UserMessages.Send];
        var threshold = Services.Settings.Current.UserMessages.ConfirmationThreshold;
        var confirmed = count > threshold
            ? await Services.Dialogs.ConfirmTypedAsync(title, Localizer.Format(L.UserMessages.ConfirmTyped, count, transport.Description, ConfirmationDomain), count.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true)
            : await Services.Dialogs.ConfirmAsync(title, Localizer.Format(L.UserMessages.Confirm, count, transport.Description, ConfirmationDomain), destructive: false).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        _currentRun = RunAsync(transport, items);
        await _currentRun.ConfigureAwait(true);
    }

    private bool CanSend() => Stage == UserMessageStage.Preview && !IsBusy && SendCount > 0;

    [RelayCommand(CanExecute = nameof(IsSending))]
    private void CancelSend()
    {
        _sending?.Cancel();
        ProgressText = Localizer[L.UserMessages.Stopping];
    }

    /// <summary>"Ponów nieudane": plans the failed and not sent messages again (fresh read) and shows their preview.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private Task RetryFailedAsync() =>
        PlanCoreAsync([.. _items.Where(i => i.CanRetry).Select(i => i.Planned.DistinguishedName)]);

    private bool CanRetryFailed() => Stage == UserMessageStage.Report && !IsBusy && Session is not null && _items.Any(i => i.CanRetry);

    /// <summary>Exports the report (one row per account, no content) to a CSV file the operator chooses.</summary>
    [RelayCommand(CanExecute = nameof(IsReport))]
    private async Task ExportReportAsync()
    {
        var title = Localizer[L.UserMessages.ExportReport];
        var path = await Services.Files.SaveFileAsync(
            title,
            ListExportSnapshot.SuggestFileName(Localizer[L.UserMessages.ReportFileName], Services.Time.GetLocalNow()),
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var entries = _items.Select(i => i.ReportEntry).ToList();
        var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => _messages.Reports.Write(writer, entries, delimiter)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, entries.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose(true);

    // ---- Plan ----------------------------------------------------------------------------------------------------

    private async Task PlanCoreAsync(IReadOnlyList<string> distinguishedNames)
    {
        var token = _plans.Next();
        IsBusy = true;
        Error = null;
        TestResultText = null;
        StatusText = Localizer[L.UserMessages.Reading];
        try
        {
            var session = RequireSession();
            var planner = _messages.Planner;
            var template = _working with { ConditionDays = ConditionDays };
            var inputs = Inputs.ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase);
            var templateIssues = planner.CheckTemplate(template, inputs, session.Schema);
            if (templateIssues.Any(i => i.Severity == MessageIssueSeverity.Error))
            {
                Error = string.Join(Environment.NewLine, templateIssues.Select(i => UserMessageText.Issue(Localizer, i)));
                return;
            }

            var accounts = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, distinguishedNames, planner.AccountAttributesFor(template), token), token).ConfigureAwait(true);
            var managerDns = accounts.Values.Select(a => a.GetString(Attr.Manager)).OfType<string>().Where(m => m.Length > 0).ToList();
            var managers = managerDns.Count == 0
                ? []
                : await Task.Run(() => DirectoryLookup.ReadManyAsync(session, managerDns, planner.ManagerAttributesFor(template), token), token).ConfigureAwait(true);
            var policy = await ReadPolicyAsync(token).ConfigureAwait(true);
            var now = Services.Time.GetUtcNow();
            var request = new UserMessagePlanRequest
            {
                Template = template,
                Recipients = RecipientMode.Value,
                ConditionDays = ConditionDays,
                Inputs = inputs,
                Images = await PlanImagesAsync().ConfigureAwait(true),
                OmitImages = _omitImages,
                Sender = UserMessageSenders.From(Services.Settings.Current.UserMessages, session.Info.DomainDnsName),
                Accounts = [.. distinguishedNames.Select(dn =>
                {
                    var account = accounts.GetValueOrDefault(DnKey.For(dn));
                    var manager = account?.GetString(Attr.Manager) is { Length: > 0 } managerDn ? managers.GetValueOrDefault(DnKey.For(managerDn)) : null;
                    return new UserMessageAccount(dn, account, manager);
                })],
                Now = now,
                DomainMaxPasswordAge = policy.MaxPasswordAge,
                IsLockedByTime = lockoutTime => policy.GetLockState(lockoutTime, now) != LockState.NotLocked,
                Domain = session.Info.DomainDnsName,
                Schema = session.Schema,
            };
            var plan = await Task.Run(() => planner.Plan(request), token).ConfigureAwait(true);
            if (!_plans.IsCurrent(token) || IsDisposed)
            {
                return;
            }

            if (plan.IsBlocked)
            {
                Error = string.Join(Environment.NewLine, plan.TemplateIssues.Select(i => UserMessageText.Issue(Localizer, i)));
                return;
            }

            ShowPlan(plan);
            Stage = UserMessageStage.Preview;
            await RefreshTransportAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded or the window closed.
        }
        catch (Exception ex) when (ex is DirectoryException or SessionEndedException or InvalidOperationException)
        {
            Error = ex is SessionEndedException or InvalidOperationException ? NoSessionMessage : Errors.Translate(ex);
        }
        finally
        {
            if (_plans.IsCurrent(token))
            {
                IsBusy = false;
                StatusText = null;
            }
        }
    }

    private async Task<DomainPolicy> ReadPolicyAsync(CancellationToken token)
    {
        try
        {
            return await Services.Sessions.GetDomainPolicyAsync(token).ConfigureAwait(true);
        }
        catch (DirectoryException)
        {
            return DomainPolicy.Unknown;
        }
    }

    private void ShowPlan(UserMessagePlan plan)
    {
        foreach (var item in _items)
        {
            item.PropertyChanged -= OnItemChanged;
        }

        _items.Clear();
        _items.AddRange(plan.Messages.Select(m => new UserMessageItemViewModel(m, Localizer, Services.Time.LocalTimeZone)));
        foreach (var item in _items)
        {
            item.PropertyChanged += OnItemChanged;
        }

        Filter = Filters[0];
        ApplyFilter();
        SelectedItem = Items.FirstOrDefault(i => i.CanInclude) ?? Items.FirstOrDefault();
        RefreshCounts();
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserMessageItemViewModel.IsIncluded))
        {
            RefreshCounts();
            if (Filter.Value == UserMessagePreviewFilter.Excluded)
            {
                ApplyFilter();
            }
        }
    }

    partial void OnFilterChanged(Option<UserMessagePreviewFilter> value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selected = SelectedItem;
        Items.Clear();
        foreach (var item in _items.Where(Matches))
        {
            Items.Add(item);
        }

        SelectedItem = selected is not null && Items.Contains(selected) ? selected : Items.FirstOrDefault();
    }

    private bool Matches(UserMessageItemViewModel item) => Filter.Value switch
    {
        UserMessagePreviewFilter.Ready => item.Status == PlannedMessageStatus.Ready && !item.IsExcluded,
        UserMessagePreviewFilter.Warnings => item.Status == PlannedMessageStatus.Warning,
        UserMessagePreviewFilter.Errors => item.Status == PlannedMessageStatus.Error,
        UserMessagePreviewFilter.Skipped => item.Status == PlannedMessageStatus.Skipped,
        UserMessagePreviewFilter.Excluded => item.IsExcluded,
        _ => true,
    };

    private void RefreshCounts()
    {
        SummaryText = Localizer.Format(
            L.UserMessages.Summary,
            _items.Count(i => i.Status == PlannedMessageStatus.Ready),
            _items.Count(i => i.Status == PlannedMessageStatus.Warning),
            _items.Count(i => i.Status == PlannedMessageStatus.Error),
            _items.Count(i => i.Status == PlannedMessageStatus.Skipped),
            _items.Count(i => i.IsExcluded));
        OnPropertyChanged(nameof(SendCount));
        OnPropertyChanged(nameof(SendButtonText));
        SendCommand.NotifyCanExecuteChanged();
    }

    // ---- Send ----------------------------------------------------------------------------------------------------

    private async Task RunAsync(IUserMessageTransport transport, IReadOnlyList<UserMessageItemViewModel> items)
    {
        using var cancellation = new CancellationTokenSource();
        _sending = cancellation;
        foreach (var item in _items)
        {
            item.Result = null;
        }

        Stage = UserMessageStage.Sending;
        ProgressMaximum = items.Count;
        ProgressValue = 0;
        ProgressText = Localizer.Format(L.UserMessages.Progress, 0, items.Count, 0, 0);
        var operatorName = Session?.Info.BoundIdentity ?? string.Empty;
        var domainNc = Session?.Info.DefaultNamingContext ?? string.Empty;
        var templateName = WithRunNote(SelectedTemplate?.Text ?? string.Empty);
        ApplyRateSettings();
        var progress = Services.Dispatcher.CreateProgress<UserMessageProgress>(p =>
        {
            if (Stage != UserMessageStage.Sending)
            {
                return;
            }

            ProgressValue = p.Completed;
            ProgressText = p.IsWaitingForRetry
                ? Localizer.Format(L.UserMessages.ProgressRetry, p.Completed, p.Total, p.Accepted, p.Failed)
                : Localizer.Format(L.UserMessages.Progress, p.Completed, p.Total, p.Accepted, p.Failed);
        });
        IReadOnlyList<UserMessageSendResult> results;
        try
        {
            var planned = items.Select(i => i.Planned).ToList();
            results = await Task.Run(() => _messages.Dispatcher.SendAsync(transport, planned, _messages.RateLimiter, _messages.DispatchOptions, progress, cancellation.Token)).ConfigureAwait(true);
        }
        finally
        {
            _sending = null;
        }

        var byIndex = items.Zip(results).ToList();
        foreach (var (item, result) in byIndex)
        {
            item.Result = result;
        }

        var accepted = results.Count(r => r.Status == UserMessageSendStatus.Accepted);
        var failed = results.Count(r => r.Status == UserMessageSendStatus.Failed);
        var notSent = results.Count(r => r.Status == UserMessageSendStatus.NotSent);
        ReportSummary = Localizer.Format(L.UserMessages.ReportSummary, accepted, failed, notSent, _items.Count - items.Count);
        _messages.AuditLog.Record(new AuditEntry(
            Services.Time.GetUtcNow(),
            operatorName,
            transport.Description,
            domainNc,
            SessionLogAction,
            Attribute: null,
            OldValue: templateName,
            NewValue: string.Create(CultureInfo.InvariantCulture, $"{accepted}/{items.Count}"),
            Succeeded: failed == 0 && notSent == 0,
            Error: failed + notSent > 0 ? Localizer.Format(L.UserMessages.SessionLogFailures, failed, notSent) : null));
        AfterRun(results);
        Stage = UserMessageStage.Report;
        Filter = Filters[0];
        ApplyFilter();
        Services.Notifications.Show(
            failed + notSent == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning,
            ReportSummary);
    }

    /// <summary>A rejected password is forgotten, so the next send asks again.</summary>
    private void AfterRun(IReadOnlyList<UserMessageSendResult> results)
    {
        if (results.Any(r => r.Delivery?.Outcome == UserMessageDeliveryOutcome.AuthenticationFailed)
            && Services.Settings.Current.UserMessages.Smtp.Authentication == SmtpAuthentication.Basic
            && _transport?.Kind == UserMessageTransportKind.DirectSmtp)
        {
            _messages.Transports.Credentials.Clear();
            _transport = null;
            NeedsSmtpPassword = true;
        }
    }

    private void ApplyRateSettings()
    {
        var settings = Services.Settings.Current.UserMessages;
        _messages.RateLimiter.MessagesPerMinute = settings.MessagesPerMinute;
        _messages.RateLimiter.RecipientsPerDay = settings.RecipientsPerDay;
    }

    // ---- Transport -----------------------------------------------------------------------------------------------

    private async Task RefreshTransportAsync()
    {
        try
        {
            var choice = await _messages.Transports.SelectAsync(CancellationToken.None).ConfigureAwait(true);
            _transport = choice.Transport;
            NeedsSmtpPassword = choice.NeedsPassword;
            TransportText = choice.Transport is { } transport
                ? Localizer.Format(transport.Kind == UserMessageTransportKind.Watchdog ? L.UserMessages.ViaWatchdog : L.UserMessages.ViaSmtp, transport.Description)
                : Localizer[UserMessageText.ProblemKey(choice.Problem ?? UserMessageTransportChoiceResult.SmtpNotConfigured)];
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            _transport = null;
            TransportText = Errors.Translate(ex);
        }
    }

    /// <summary>The transport for a send; asks the SMTP password from the window's box when needed (kept in memory only).</summary>
    private async Task<IUserMessageTransport?> EnsureTransportAsync()
    {
        Error = null;
        if (_transport is null || NeedsSmtpPassword)
        {
            await RefreshTransportAsync().ConfigureAwait(true);
        }

        if (NeedsSmtpPassword)
        {
            var password = SmtpPasswordInput?.GetPassword();
            if (string.IsNullOrEmpty(password))
            {
                Error = Localizer[L.UserMessages.PasswordRequired];
                return null;
            }

            var choice = _messages.Transports.Smtp(Services.Settings.Current.UserMessages.Smtp, password);
            SmtpPasswordInput?.Clear();
            _transport = choice.Transport;
            NeedsSmtpPassword = choice.NeedsPassword;
        }

        if (_transport is null)
        {
            Error = TransportText;
        }

        return _transport;
    }

    // ---- Template editing ----------------------------------------------------------------------------------------

    partial void OnSelectedTemplateChanged(Option<string>? value)
    {
        OnPropertyChanged(nameof(IsBuiltInTemplate));
        if (_loading || value is null || _messages.Templates.Find(value.Value) is not { } template)
        {
            return;
        }

        LoadTemplate(template, null);
    }

    partial void OnEditLanguageChanged(Option<MessageLanguage> value) => LoadTexts();

    partial void OnSubjectChanged(string value) => UpdateText(t => t with { Subject = value });

    partial void OnBodyChanged(string value) => UpdateText(t => t with { Body = value });

    partial void OnHtmlChanged(string value) => UpdateText(t => t with { Html = string.IsNullOrWhiteSpace(value) ? null : value });

    partial void OnConditionChanged(Option<MessageConditionKind> value)
    {
        if (!_loading)
        {
            _working = _working with { Condition = value.Value };
        }
    }

    partial void OnRecipientModeChanged(Option<MessageRecipientMode> value)
    {
        if (!_loading)
        {
            _working = _working with { Recipients = value.Value };
        }
    }

    private void LoadTemplate(MessageTemplate template, int? conditionDays)
    {
        _loading = true;
        try
        {
            _working = template;
            OnTemplateLoaded(template);
            TemplateName = UserMessageTemplateLibrary.IsBuiltIn(template.Id) ? Localizer.Format(L.UserMessages.CopyName, template.Name) : template.Name;
            Condition = Conditions.First(c => c.Value == template.Condition);
            ConditionDays = conditionDays ?? template.ConditionDays;
            RecipientMode = RecipientModes.First(m => m.Value == template.Recipients);
            LoadTexts();
        }
        finally
        {
            _loading = false;
        }

        RefreshTemplateCheck();
    }

    private void LoadTexts()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var text = EditLanguage.Value == MessageLanguage.English ? _working.English : _working.Polish;
            Subject = text.Subject;
            Body = text.Body;
            Html = text.Html ?? string.Empty;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void UpdateText(Func<MessageTemplateText, MessageTemplateText> change)
    {
        if (_loading)
        {
            return;
        }

        _working = EditLanguage.Value == MessageLanguage.English
            ? _working with { English = change(_working.English) }
            : _working with { Polish = change(_working.Polish) };
        RefreshTemplateCheck();
    }

    private void RefreshTemplateCheck()
    {
        var issues = _messages.Planner.CheckTemplate(_working, null, OpenedSessionSchema);
        TemplateErrors = issues.Count == 0 ? null : string.Join(Environment.NewLine, issues.Select(i => UserMessageText.Issue(Localizer, i)));
        var names = _messages.Planner.InputsOf(_working).Where(n => !UserMessageRules.IsForbiddenInputName(n)).ToList();
        var keep = Inputs.ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase);
        if (!names.SequenceEqual(Inputs.Select(i => i.Name), StringComparer.OrdinalIgnoreCase))
        {
            Inputs.Clear();
            foreach (var name in names)
            {
                Inputs.Add(new UserMessageInputViewModel(name) { Value = keep.GetValueOrDefault(name) ?? string.Empty });
            }

            OnPropertyChanged(nameof(HasInputs));
        }
    }

    private IReadOnlyList<Option<string>> CreateTemplateOptions() =>
        [.. _messages.Templates.All.Select(t => new Option<string>(t.Id, t.Name))];

    private void SelectTemplate(string id)
    {
        if (Templates.FirstOrDefault(t => t.Value == id) is { } option)
        {
            SelectedTemplate = option;
        }
    }

    private void OnTemplatesChanged(object? sender, EventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        var selected = SelectedTemplate?.Value;
        _loading = true;
        try
        {
            Templates = CreateTemplateOptions();
            SelectedTemplate = Templates.FirstOrDefault(t => t.Value == selected) ?? Templates.First(t => t.Value == BuiltInMessageTemplates.Custom);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(IsBuiltInTemplate));
        DeleteTemplateCommand.NotifyCanExecuteChanged();
    });

    private void OnRecipientsChanged()
    {
        OnPropertyChanged(nameof(RecipientsText));
        OnPropertyChanged(nameof(WindowTitle));
        PlanCommand.NotifyCanExecuteChanged();
    }
}
