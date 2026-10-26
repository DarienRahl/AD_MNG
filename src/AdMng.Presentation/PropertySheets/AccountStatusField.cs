using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>
/// "Stan konta" on the Account tab (README U2, single account): lock state, password expiry with the days left (FGPP aware
/// through msDS-UserPasswordExpiryTimeComputed), account expiry ("end of day"), last logon (lastLogonTimestamp) and, on
/// demand, the lock state and the exact last logon on every domain controller. Informational: it never writes. The
/// "Wyślij wiadomość…" button is the U1 hook and stays hidden without an <see cref="IUserMessageLauncher"/>.
/// </summary>
public sealed partial class AccountStatusFieldViewModel : PropertyFieldViewModel
{
    private static readonly string[] DomainControllerAttributes = [Attr.LockoutTime, Attr.UserAccountControlComputed, Attr.LastLogon];

    private readonly SerialCancellation _reads = new();
    private DirectoryObject? _loaded;
    private DomainPolicy _policy = DomainPolicy.Unknown;

    internal AccountStatusFieldViewModel(FieldContext context)
        : base(context, Attr.PasswordExpiryComputed, context.Localizer[L.AccountStatus.Title])
    {
        IsInformational = true;
    }

    /// <summary>Locked, disabled or enabled.</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PasswordText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastLogonText { get; private set; } = string.Empty;

    /// <summary>The account is locked out, its password or the account itself expired (shown as a warning).</summary>
    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    /// <summary>Result of "Sprawdź na wszystkich DC" (one line per finding), empty before.</summary>
    [ObservableProperty]
    public partial string DomainControllersText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDomainControllersCommand))]
    public partial bool IsCheckingDomainControllers { get; private set; }

    /// <summary>The state as computed for the last load (null before).</summary>
    public AccountStatus? Status { get; private set; }

    /// <summary>U1 hook: "Wyślij wiadomość…" only when a message launcher is registered.</summary>
    public bool CanSendMessages => Context.Messages is not null;

    internal override IReadOnlyList<string> ReadAttributes => AccountStatus.Attributes;

    internal override IReadOnlyList<string> WrittenAttributes => [];

    internal override async Task LoadDetailsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _policy = await Context.Services.Sessions.GetDomainPolicyAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException)
        {
            _policy = DomainPolicy.Unknown;
        }

        Recompute();
    }

    /// <summary>Reads the lock state and lastLogon of the account on every domain controller of the domain.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckDomainControllers))]
    private async Task CheckDomainControllersAsync()
    {
        if (_loaded is not { } account || Context.Services.Sessions.Current is not { } session || !ReferenceEquals(session, Context.Session))
        {
            return;
        }

        var reader = Context.DomainControllers ?? new DomainControllerReader(null, Context.Services.Errors);
        var token = _reads.Next();
        IsCheckingDomainControllers = true;
        DomainControllersText = Localizer[L.AccountStatus.ReadingDomainControllers];
        try
        {
            var dn = account.DistinguishedName;
            var reads = await Task.Run(() => reader.ReadAsync(session, [dn], DomainControllerAttributes, token), token).ConfigureAwait(true);
            DomainControllersText = Describe(reads, dn);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DomainControllersText = string.Empty;
        }
        finally
        {
            IsCheckingDomainControllers = false;
        }
    }

    private bool CanCheckDomainControllers() => !IsCheckingDomainControllers && _loaded is not null;

    [RelayCommand]
    private Task SendMessageAsync() =>
        Context.Messages is { } messages && _loaded is { } account ? messages.ShowComposerAsync([account.DistinguishedName], null) : Task.CompletedTask;

    protected override void LoadCore(DirectoryObject obj)
    {
        _loaded = obj;
        _reads.Cancel();
        DomainControllersText = string.Empty;
        Recompute();
        CheckDomainControllersCommand.NotifyCanExecuteChanged();
    }

    protected override void RevertCore()
    {
    }

    protected override bool ComputeIsDirty() => false;

    protected override IEnumerable<BatchOperation> CreateOperations() => [];

    private AccountReviewFormatter Formatter => new(Localizer, Context.Services.Time.LocalTimeZone, new AccountReviewParameters());

    private void Recompute()
    {
        if (_loaded is not { } obj)
        {
            return;
        }

        var now = Context.Services.Time.GetUtcNow();
        var policy = _policy;
        var status = AccountStatus.Evaluate(
            obj, now, Context.Services.Time.LocalTimeZone, policy.MaxPasswordAge,
            lockoutTime => policy.GetLockState(lockoutTime, now) != LockState.NotLocked);
        Status = status;
        var f = Formatter;
        StateText = status.IsLocked
            ? Localizer.Format(L.AccountStatus.LockedSince, f.Moment(status.LockedSince))
            : status.IsDisabled ? Localizer[L.AccountStatus.Disabled] : Localizer[L.AccountStatus.Active];
        PasswordText = status.Password switch
        {
            PasswordState.Expires or PasswordState.Expired when status.PasswordExpiresAt is { } at => Estimate(
                Localizer.Format(
                    status.Password == PasswordState.Expires ? L.AccountStatus.PasswordExpires : L.AccountStatus.PasswordExpired,
                    f.Moment(at),
                    f.Days(status.PasswordDaysLeft)),
                status.IsPasswordExpiryEstimated),
            PasswordState.Expired => Localizer[L.AccountReview.PasswordExpiredValue],
            PasswordState.NeverExpires => Localizer[L.AccountStatus.PasswordNever],
            PasswordState.MustChange => Localizer[L.AccountStatus.PasswordMustChange],
            _ => Localizer[L.Common.Unknown],
        };
        AccountText = status.AccountLastDay is { } day
            ? Localizer.Format(status.IsAccountExpired ? L.AccountStatus.AccountExpired : L.AccountStatus.AccountExpires, f.Date(day), f.Days(status.AccountDaysLeft))
            : Localizer[L.AccountStatus.AccountNever];
        LastLogonText = status.LastLogon is { } logon
            ? Localizer.Format(L.AccountStatus.LastLogonApproximate, f.Moment(logon), f.Days(-status.DaysSinceLogon))
            : Localizer[L.AccountReview.Never];
        HasProblem = status.IsLocked || status.IsAccountExpired || status.Password is PasswordState.Expired;
    }

    /// <summary>Lock state on one DC: the computed lock bit, else lockoutTime within the lockout duration of the policy.</summary>
    private bool IsLockedOn(DirectoryObject obj)
    {
        if (obj.GetInt32(Attr.UserAccountControlComputed) is { } computed)
        {
            return (computed & (int)UserAccountControl.Lockout) != 0;
        }

        return _policy.GetLockState(obj.GetInt64(Attr.LockoutTime), Context.Services.Time.GetUtcNow()) != LockState.NotLocked;
    }

    private string Estimate(string text, bool estimated) =>
        estimated ? Localizer.Format(L.AccountStatus.PasswordEstimate, text) : text;

    private string Describe(IReadOnlyList<DomainControllerRead> reads, string dn)
    {
        var f = Formatter;
        var lines = new List<string>();
        var answered = reads.Where(r => r.Succeeded).ToList();
        var locked = answered
            .Select(r => (r.Server, Object: r.Find(dn)))
            .Where(r => r.Object is { } o && IsLockedOn(o))
            .Select(r => $"{r.Server} ({f.Moment(r.Object!.GetFileTime(Attr.LockoutTime))})")
            .ToList();
        lines.Add(locked.Count > 0
            ? Localizer.Format(L.AccountStatus.LockedOn, string.Join(", ", locked))
            : Localizer.Format(L.AccountStatus.NotLockedOn, answered.Count));
        var newest = answered
            .Select(r => (r.Server, Time: r.Find(dn)?.GetFileTime(Attr.LastLogon)))
            .Where(r => r.Time is not null)
            .OrderByDescending(r => r.Time)
            .FirstOrDefault();
        lines.Add(newest.Time is { } time
            ? Localizer.Format(L.AccountStatus.ExactLastLogon, f.Moment(time), newest.Server)
            : Localizer[L.AccountStatus.ExactNever]);
        lines.AddRange(reads.Where(r => !r.Succeeded).Select(r => Localizer.Format(L.AccountStatus.DomainControllerFailed, r.Server, r.Error)));
        return string.Join(Environment.NewLine, lines);
    }
}
