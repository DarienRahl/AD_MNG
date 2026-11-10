using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Pki;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>
/// <i>Poświadczenia kluczy</i> (P1, docs/dev/p-certificate-services.md 2.6) of a user or computer: the entries of
/// <c>msDS-KeyCredentialLink</c> (<see cref="KeyCredentialLink"/>) – key id, device id, source, usage, created and the
/// approximate last logon, malformed entries and a key hash that does not match marked – never the key material (rule 2).
/// Read when the tab is first shown (the Attribute Editor pattern), parsed on <see cref="Task.Run(Action)"/>, read only
/// (rule 5). The Watchdog history of the attribute is P5.
/// </summary>
public sealed partial class KeyCredentialsPageViewModel : PropertyPageViewModel
{
    public const string PageKey = L.Certificates.KeyCredentialsPage;

    private readonly SerialCancellation _loads = new();
    private string _dn = string.Empty;
    private bool _activated;
    private bool _readOnce;

    internal KeyCredentialsPageViewModel(FieldContext context)
        : base(context, PageKey, context.Localizer[PageKey])
    {
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CountText))]
    public partial IReadOnlyList<KeyCredentialRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyKeyIdCommand), nameof(CopyDeviceIdCommand))]
    public partial KeyCredentialRowViewModel? SelectedRow { get; set; }

    /// <summary>The page was read and holds no key credential.</summary>
    public bool IsEmpty => _readOnce && Rows.Count == 0;

    /// <summary><i>Poświadczenia kluczy: 1</i>, or the empty text.</summary>
    public string CountText => !_readOnce ? string.Empty
        : Rows.Count == 0 ? Context.Localizer[L.Certificates.KeysEmpty]
        : Context.Localizer.Format(L.Certificates.KeysCount, Rows.Count);

    internal override IEnumerable<string> ReadAttributes => [];

    public override async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await ReloadAsync().ConfigureAwait(true);
    }

    public override void Revert()
    {
    }

    internal override void Load(DirectoryObject obj) => _dn = obj.DistinguishedName;

    internal override Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken) =>
        _activated ? ReloadAsync() : Task.CompletedTask;

    internal override IEnumerable<BatchOperation> GetOperations() => [];

    protected override bool ComputeHasChanges() => false;

    protected override bool ComputeHasErrors() => false;

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand(CanExecute = nameof(CanCopyKeyId))]
    private void CopyKeyId()
    {
        if (SelectedRow is { KeyId.Length: > 0 } row)
        {
            ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Context.Localizer, row.KeyId, Context.Localizer[L.Certificates.ColumnKeyId]);
        }
    }

    private bool CanCopyKeyId() => SelectedRow is { KeyId.Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanCopyDeviceId))]
    private void CopyDeviceId()
    {
        if (SelectedRow is { DeviceId.Length: > 0 } row)
        {
            ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Context.Localizer, row.DeviceId, Context.Localizer[L.Certificates.ColumnDeviceId]);
        }
    }

    private bool CanCopyDeviceId() => SelectedRow is { DeviceId.Length: > 0 };

    private async Task ReloadAsync()
    {
        if (_dn.Length == 0)
        {
            return;
        }

        var token = _loads.Next();
        IsLoading = true;
        Error = null;
        try
        {
            var session = Context.Session;
            var dn = _dn;
            var links = await Task.Run(
                async () =>
                {
                    var read = await session.GetAsync(dn, [PkiAttributes.KeyCredentialLink], token).ConfigureAwait(false);
                    if (read is null)
                    {
                        return null;
                    }

                    read = await RangedValues.CompleteAsync(session, read, token).ConfigureAwait(false);
                    return KeyCredentialLink.Parse(read.GetStrings(PkiAttributes.KeyCredentialLink));
                },
                token).ConfigureAwait(true);
            if (links is null)
            {
                Error = Context.Localizer[L.PropertySheet.NotFound];
                return;
            }

            var selected = SelectedRow?.KeyId;
            _readOnce = true;
            Rows = [.. links.Select(l => new KeyCredentialRowViewModel(Context, l))];
            SelectedRow = Rows.FirstOrDefault(r => selected is not null && string.Equals(r.KeyId, selected, StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer load.
        }
        catch (DirectoryException ex)
        {
            Error = Context.Services.Errors.Translate(ex);
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;
            }
        }
    }
}

/// <summary>One <c>msDS-KeyCredentialLink</c> entry of <see cref="KeyCredentialsPageViewModel"/> (metadata only).</summary>
public sealed class KeyCredentialRowViewModel
{
    internal KeyCredentialRowViewModel(FieldContext context, KeyCredentialLink link)
    {
        var localizer = context.Localizer;
        var zone = context.Services.Time.LocalTimeZone;
        var culture = localizer.Culture;
        IsMalformed = link.IsMalformed;
        KeyId = link.KeyId ?? string.Empty;
        DeviceId = link.DeviceId?.ToString("D") ?? string.Empty;
        Source = link.Source;
        Usage = link.Usage;
        SourceText = link.IsMalformed ? string.Empty : localizer[L.Certificates.KeySourceKey(link.Source)];
        UsageText = link.IsMalformed ? string.Empty : localizer[L.Certificates.KeyUsageKey(link.Usage)];
        Created = Moment(link.Created, zone, culture);
        LastLogon = Moment(link.LastLogon, zone, culture);
        IsHashMismatch = link.IsKeyHashValid == false;
        Remarks = link.IsMalformed ? localizer[L.Certificates.KeyMalformed]
            : IsHashMismatch ? localizer[L.Certificates.KeyHashMismatch]
            : string.Empty;
    }

    /// <summary>The KeyID (Base64, as DSInternals and <c>Get-ADUser</c> tools show it), empty when unreadable.</summary>
    public string KeyId { get; }

    public string DeviceId { get; }

    public KeyCredentialSource Source { get; }

    public KeyCredentialUsage Usage { get; }

    public string SourceText { get; }

    public string UsageText { get; }

    public string Created { get; }

    /// <summary>KeyApproximateLastLogonTimeStamp (approximate, like <c>lastLogonTimestamp</c>), empty when absent.</summary>
    public string LastLogon { get; }

    public bool IsMalformed { get; }

    /// <summary>The KeyHash entry does not match the entries after it (they were changed after the hash was computed).</summary>
    public bool IsHashMismatch { get; }

    /// <summary>The malformed / hash notes, else empty.</summary>
    public string Remarks { get; }

    public bool HasRemarks => Remarks.Length > 0;

    /// <summary>Source and usage only (never the key or device id).</summary>
    public override string ToString() => $"{nameof(KeyCredentialRowViewModel)}({Source} {Usage}{(IsMalformed ? ", malformed" : string.Empty)})";

    private static string Moment(DateTimeOffset? value, TimeZoneInfo zone, CultureInfo culture) =>
        value is { } moment ? TimeZoneInfo.ConvertTime(moment, zone).ToString("g", culture) : string.Empty;
}
