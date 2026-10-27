using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Computers;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Computers;

/// <summary>
/// "Stan" of a computer's property sheet (K1 design note 4.5): type, system, release and support, the replicated last logon
/// (approximate) and – on demand – the exact one from every DC, the site (DNS → subnet), the creator, the object owner,
/// LAPS, and the buttons of the remote actions and tools. Read only; everything stays in memory.
/// </summary>
public sealed partial class ComputerStatusPageViewModel : PropertyPageViewModel
{
    public const string PageKey = L.Computers.StatusPage;

    private static readonly WindowsReleaseCatalog Catalog = new();

    private ComputerReviewFacts? _row;
    private string? _creatorName;
    private string? _objectOwner;

    internal ComputerStatusPageViewModel(FieldContext context)
        : base(context, PageKey, context.Localizer[L.Computers.StatusPage])
    {
    }

    /// <summary>The rows of the page (label, value).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ComputerDetailViewModel> Details { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadExactLastLogonCommand), nameof(ResolveSiteCommand))]
    public partial bool IsReading { get; private set; }

    /// <summary>The remote actions and tools are offered (the Windows host registered them).</summary>
    public bool CanUseRemote => Dialogs is not null;

    public bool CanReadExactLastLogon => Context.DomainControllers is not null;

    public bool CanResolveSite => Dialogs?.Actions.CanResolveSites == true;

    private IComputerDialogs? Dialogs => Context.Services.Dialogs as IComputerDialogs;

    internal override IEnumerable<string> ReadAttributes => ComputerFacts.Attributes(Context.Schema);

    public override void Revert()
    {
    }

    /// <summary>The type of the computer (the General page).</summary>
    internal static FieldViewModel TypeField(FieldContext context) =>
        new(context, Attr.OperatingSystem, context.Localizer[L.ComputerReview.DetailType], o => context.Localizer[ComputerReviewFormatter.TypeKey(ComputerFacts.From(o).Type)]);

    /// <summary>The release of the catalogue (the Operating System page).</summary>
    internal static FieldViewModel ReleaseField(FieldContext context) =>
        new(context, Attr.OperatingSystemVersion, context.Localizer[L.ComputerReview.DetailRelease], o => Evaluate(context, ComputerFacts.From(o)).Release.Release?.Release ?? string.Empty);

    /// <summary>The support state with its date (the Operating System page).</summary>
    internal static FieldViewModel SupportField(FieldContext context) =>
        new(context, Attr.OperatingSystem, context.Localizer[L.ComputerReview.DetailSupport], o =>
            new ComputerReviewFormatter(context.Localizer, context.Services.Time.LocalTimeZone).Support(Evaluate(context, ComputerFacts.From(o)).Release));

    private static ComputerStatus Evaluate(FieldContext context, ComputerFacts facts)
    {
        var settings = context.Services.Settings.Current.Computers;
        var options = new ComputerInventoryOptions
        {
            Catalog = Catalog,
            EndingSoonDays = settings.Parameters.Normalized().EndingSoonDays,
            Overrides = settings.CatalogOverrides(),
        };
        return ComputerInventory.Evaluate(facts, context.Services.Time.GetUtcNow(), context.Services.Time.LocalTimeZone, options);
    }

    internal override void Load(DirectoryObject obj)
    {
        var facts = ComputerFacts.From(obj);
        var previous = _row;
        var row = new ComputerReviewFacts
        {
            Facts = facts,
            Status = Evaluate(Context, facts),
            Owner = facts.ManagedBy is null ? OwnerState.Missing : OwnerState.Unknown,
        };

        // A reload keeps what was read on demand for the same computer.
        if (previous is not null && string.Equals(previous.DistinguishedName, facts.DistinguishedName, StringComparison.OrdinalIgnoreCase))
        {
            row = row with
            {
                Site = previous.Site,
                HasExactLastLogon = previous.HasExactLastLogon,
                ExactLastLogon = previous.ExactLastLogon,
                ExactLastLogonServer = previous.ExactLastLogonServer,
            };
        }

        _row = row;
        RefreshDetails();
        UpdateState();
    }

    internal override async Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken)
    {
        var session = Context.Session;
        try
        {
            _creatorName = await CreatorAsync(session, ComputerFacts.From(obj).CreatorSidValue, cancellationToken).ConfigureAwait(true);
            _objectOwner = await ObjectOwnerAsync(session, obj.DistinguishedName, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            // Best effort: the rows say "not read yet".
        }

        RefreshDetails();
    }

    internal override IEnumerable<BatchOperation> GetOperations() => [];

    protected override bool ComputeHasChanges() => false;

    protected override bool ComputeHasErrors() => false;

    /// <summary>"Sprawdź na wszystkich DC": lastLogon of this computer on every DC (the newest wins).</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadExactLastLogonAsync()
    {
        if (_row is not { } row || Context.DomainControllers is not { } reader)
        {
            return;
        }

        IsReading = true;
        Error = null;
        try
        {
            var reads = await Task.Run(() => reader.ReadAsync(Context.Session, [row.DistinguishedName], [Attr.LastLogon], CancellationToken.None)).ConfigureAwait(true);
            var best = reads.Where(r => r.Succeeded)
                .Select(r => (r.Server, Time: r.Find(row.DistinguishedName)?.GetFileTime(Attr.LastLogon)))
                .Where(v => v.Time is not null)
                .OrderByDescending(v => v.Time)
                .FirstOrDefault();
            _row = row with { HasExactLastLogon = true, ExactLastLogon = best.Time, ExactLastLogonServer = best.Time is null ? null : best.Server };
            var failed = reads.Where(r => !r.Succeeded).Select(r => r.Server).ToList();
            if (failed.Count > 0)
            {
                Error = Context.Localizer.Format(L.Computers.ExactNotAnswered, string.Join(", ", failed));
            }
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            Error = Context.Services.Errors.Translate(ex);
        }
        finally
        {
            IsReading = false;
            RefreshDetails();
        }
    }

    /// <summary>"Ustal": the site through the workstation's resolver and the subnets of the forest.</summary>
    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task ResolveSiteAsync()
    {
        if (_row is not { } row || Dialogs?.Actions is not { } actions)
        {
            return;
        }

        IsReading = true;
        Error = null;
        try
        {
            if (await actions.ResolveSiteAsync(Context.Session, row.Facts).ConfigureAwait(true) is { } site)
            {
                _row = row with { Site = site };
            }
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            Error = Context.Services.Errors.Translate(ex);
        }
        finally
        {
            IsReading = false;
            RefreshDetails();
        }
    }

    [RelayCommand]
    private Task RemoteActionsAsync() => _row is { } row && Dialogs is { } dialogs ? dialogs.ShowRemoteActionsAsync(row.DistinguishedName) : Task.CompletedTask;

    [RelayCommand]
    private Task RemoteDesktopAsync() => RunAsync(ComputerAction.RemoteDesktop);

    [RelayCommand]
    private Task ComputerManagementAsync() => RunAsync(ComputerAction.ComputerManagement);

    [RelayCommand]
    private Task EventViewerAsync() => RunAsync(ComputerAction.EventViewer);

    [RelayCommand]
    private Task PingAsync() => RunAsync(ComputerAction.Ping);

    private bool CanRead() => !IsReading && Context.DomainControllers is not null;

    private bool CanResolve() => !IsReading && CanResolveSite;

    private Task RunAsync(ComputerAction action) =>
        _row is { } row && Dialogs is { } dialogs ? dialogs.RunComputerActionAsync(action, [row.DistinguishedName]) : Task.CompletedTask;

    private void RefreshDetails()
    {
        if (_row is not { } row)
        {
            Details = [];
            return;
        }

        var localizer = Context.Localizer;
        var formatter = new ComputerReviewFormatter(localizer, Context.Services.Time.LocalTimeZone);
        var notRead = localizer[L.Computers.NotRead];
        var facts = row.Facts;
        var details = new List<ComputerDetailViewModel>();
        void Add(string key, string? value, bool always = false)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                details.Add(new ComputerDetailViewModel(localizer[key], value));
            }
            else if (always)
            {
                details.Add(new ComputerDetailViewModel(localizer[key], notRead));
            }
        }

        Add(L.ComputerReview.DetailType, localizer[ComputerReviewFormatter.TypeKey(facts.Type)]);
        Add(L.ComputerReview.DetailSystem, formatter.System(facts));
        Add(L.ComputerReview.DetailRelease, row.Status.Release.Release?.Release);
        Add(L.ComputerReview.DetailSupport, formatter.Support(row.Status.Release));
        Add(L.ComputerReview.DetailLastLogon, formatter.Text(ComputerReviewColumns.LastLogon, row));
        Add(L.ComputerReview.DetailExactLastLogon, formatter.Text(ComputerReviewColumns.ExactLastLogon, row), always: true);
        Add(L.ComputerReview.DetailSite, formatter.Site(row.Site), always: true);
        Add(L.ComputerReview.DetailCreated, formatter.Moment(facts.Created));
        Add(L.ComputerReview.DetailCreator, _creatorName ?? facts.CreatorSidValue);
        Add(L.Computers.ObjectOwner, _objectOwner, always: true);
        Add(L.ComputerReview.DetailAccountRenewed, formatter.Moment(facts.PasswordLastSet));
        Add(L.ComputerReview.DetailLaps, facts.LapsKind is null ? localizer[L.ComputerReview.LapsNone] : formatter.Text(ComputerReviewColumns.Laps, row));
        Details = details;
    }

    /// <summary>DOMAIN\login of the account behind mS-DS-CreatorSID (prestaged by a non-admin).</summary>
    private static async Task<string?> CreatorAsync(IDirectorySession session, string? sid, CancellationToken cancellationToken) =>
        sid is null ? null : await NameOfSidAsync(session, sid, cancellationToken).ConfigureAwait(false);

    /// <summary>The owner of the object's security descriptor (who created or took the account).</summary>
    private static async Task<string?> ObjectOwnerAsync(IDirectorySession session, string distinguishedName, CancellationToken cancellationToken)
    {
        var obj = await session.GetAsync(distinguishedName, [Attr.NtSecurityDescriptor], cancellationToken).ConfigureAwait(false);
        if (obj?.GetBytes(Attr.NtSecurityDescriptor) is not { } descriptor
            || !SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary?.Owner is not { } owner)
        {
            return null;
        }

        return await NameOfSidAsync(session, owner, cancellationToken).ConfigureAwait(false) ?? owner;
    }

    private static async Task<string?> NameOfSidAsync(IDirectorySession session, string sid, CancellationToken cancellationToken)
    {
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.Equal(Attr.ObjectSid, sid),
            Attributes = [Attr.SamAccountName, Attr.Name],
            SizeLimit = 1,
        };
        await foreach (var principal in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            return principal.GetString(Attr.SamAccountName) is { Length: > 0 } login
                ? $"{session.Info.DomainNetBiosName ?? session.Info.DomainDnsName}\\{login}"
                : principal.Name;
        }

        return null;
    }
}
