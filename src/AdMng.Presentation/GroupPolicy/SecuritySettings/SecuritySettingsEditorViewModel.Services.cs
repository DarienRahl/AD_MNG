using System.Globalization;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>"Usługi": [Service General Setting] of GptTmpl.inf (start modes; the security descriptor is kept as read).</summary>
public sealed partial class SecuritySettingsEditorViewModel
{
    private readonly Dictionary<string, ServiceRowViewModel> _serviceRows = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial IReadOnlyList<ServiceRowViewModel> Services { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveServiceCommand))]
    public partial ServiceRowViewModel? SelectedService { get; set; }

    /// <summary>Automatic, manual, disabled.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SecurityChoice> ServiceModes { get; private set; } = [];

    /// <summary>The short name of the service to add (<c>Spooler</c>).</summary>
    [ObservableProperty]
    public partial string NewServiceName { get; set; } = string.Empty;

    /// <summary>The start mode of the service to add (disabled unless chosen otherwise).</summary>
    [ObservableProperty]
    public partial SecurityChoice? NewServiceMode { get; set; }

    private void BuildServiceChoices()
    {
        ServiceModes =
        [
            new SecurityChoice(2, Localizer[L.SecuritySettings.ServiceAutomatic]),
            new SecurityChoice(3, Localizer[L.SecuritySettings.ServiceManual]),
            new SecurityChoice(4, Localizer[L.SecuritySettings.ServiceDisabled]),
        ];
        NewServiceMode = ServiceModes[2];
        _serviceRows.Clear();
        Services = [];
        SelectedService = null;
    }

    private void RebuildServices()
    {
        var current = _template?.Document.GetServices() ?? [];
        var original = (_template?.OriginalDocument.GetServices() ?? []).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var rows = new List<ServiceRowViewModel>();
        foreach (var service in current)
        {
            if (!_serviceRows.TryGetValue(service.Name, out var row))
            {
                row = new ServiceRowViewModel(this, service.Name, ServiceModes);
                _serviceRows[service.Name] = row;
            }

            row.Update(() =>
            {
                row.StartMode = service.StartMode;
                row.SelectedMode = ServiceModes.FirstOrDefault(m => m.Value == service.StartMode);
                row.ModeText = row.SelectedMode?.Label ?? Localizer.Format(L.SecuritySettings.ServiceModeOther, service.StartMode.ToString(CultureInfo.InvariantCulture));
                row.SecurityText = service.Security.Length > 0 ? service.Security : Localizer[L.SecuritySettings.NoSecurityDescriptor];
            });
            row.IsChanged = !original.TryGetValue(service.Name, out var before) || before != service;
            rows.Add(row);
        }

        if (!rows.SequenceEqual(Services))
        {
            var selected = SelectedService;
            Services = rows;
            SelectedService = selected is not null && rows.Contains(selected) ? selected : null;
        }
    }

    internal void OnServiceModeEdited(ServiceRowViewModel row)
    {
        if (CanEditTemplate && _template is { } template && row.SelectedMode is { } mode && row.IsModeEditable
            && template.Document.GetServices().FirstOrDefault(s => s.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)) is { } service)
        {
            template.Document.SetService(service.Name, service with { StartMode = (int)mode.Value });
        }

        AfterEdit();
    }

    /// <summary>"Dodaj usługę": the short name as typed (no quotes, commas or line breaks), without a security descriptor.</summary>
    [RelayCommand(CanExecute = nameof(CanEditTemplateNow))]
    private void AddService()
    {
        if (!CanEditTemplate || _template is not { } template)
        {
            return;
        }

        var name = NewServiceName.Trim();
        if (name.Length == 0 || name.IndexOfAny(['"', ',', '\r', '\n']) >= 0)
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer[L.SecuritySettings.ServiceNameInvalid]);
            return;
        }

        if (template.Document.GetServices().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.SecuritySettings.AlreadyListed]);
            SelectedService = Services.FirstOrDefault(s => s.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase));
            return;
        }

        template.Document.SetService(name, new ServiceStartupSetting(name, (int)(NewServiceMode ?? ServiceModes[2]).Value, string.Empty));
        NewServiceName = string.Empty;
        AfterEdit();
        SelectedService = Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Usuń usługę": the service's line (its start mode is no longer set by this GPO).</summary>
    [RelayCommand(CanExecute = nameof(CanEditService))]
    private void RemoveService()
    {
        if (SelectedService is { } row && CanEditTemplate && _template is { } template)
        {
            template.Document.SetService(row.Name, null);
            SelectedService = null;
            AfterEdit();
        }
    }

    private bool CanEditService() => CanEditTemplate && SelectedService is not null;

    /// <summary>The page commands follow the read-only state.</summary>
    private void RefreshPageCommands()
    {
        AddRightHolderCommand.NotifyCanExecuteChanged();
        AddRightHolderByNameCommand.NotifyCanExecuteChanged();
        RemoveRightHolderCommand.NotifyCanExecuteChanged();
        AddRestrictedGroupCommand.NotifyCanExecuteChanged();
        PickRestrictedGroupCommand.NotifyCanExecuteChanged();
        RemoveRestrictedGroupCommand.NotifyCanExecuteChanged();
        AddRestrictedMemberCommand.NotifyCanExecuteChanged();
        AddRestrictedMemberByNameCommand.NotifyCanExecuteChanged();
        RemoveRestrictedMemberCommand.NotifyCanExecuteChanged();
        AddRestrictedMemberOfCommand.NotifyCanExecuteChanged();
        AddRestrictedMemberOfByNameCommand.NotifyCanExecuteChanged();
        RemoveRestrictedMemberOfCommand.NotifyCanExecuteChanged();
        AddGppGroupCommand.NotifyCanExecuteChanged();
        RemoveGppGroupCommand.NotifyCanExecuteChanged();
        AddGppMemberCommand.NotifyCanExecuteChanged();
        AddGppMemberByNameCommand.NotifyCanExecuteChanged();
        RemoveGppMemberCommand.NotifyCanExecuteChanged();
        RemoveRawEntryCommand.NotifyCanExecuteChanged();
        ReplaceRawValueCommand.NotifyCanExecuteChanged();
        AddServiceCommand.NotifyCanExecuteChanged();
        RemoveServiceCommand.NotifyCanExecuteChanged();
    }
}
