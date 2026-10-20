using System.Collections.Concurrent;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Tests.TestDoubles;

/// <summary>
/// Planner double: records requests and builds one executable object plan per target (a Modify step), unless a
/// custom <see cref="PlanFactory"/> is set (e.g. to return validation errors).
/// </summary>
internal sealed class RecordingChangePlanner : IChangePlanner
{
    private readonly ConcurrentQueue<BatchRequest> _requests = new();

    public IReadOnlyList<BatchRequest> Requests => [.. _requests];

    public Func<BatchRequest, ChangePlan>? PlanFactory { get; set; }

    public Task<ChangePlan> PlanAsync(IDirectorySession session, BatchRequest request, PlanningOptions? options, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        return Task.FromResult(PlanFactory?.Invoke(request) ?? DefaultPlan(request));
    }

    public static ChangePlan DefaultPlan(BatchRequest request) => new()
    {
        Title = request.Title,
        Objects = [.. request.TargetDns.Select(dn => new ObjectPlan
        {
            DistinguishedName = dn,
            DisplayName = DistinguishedName.Parse(dn).Name,
            Changes = [.. request.Operations.Select(ToChange)],
        })],
    };

    public static ObjectPlan WithIssue(string dn, IssueSeverity severity, string code, string? attribute = null) => new()
    {
        DistinguishedName = dn,
        DisplayName = DistinguishedName.Parse(dn).Name,
        Changes = [new PlannedChange { Action = ChangeAction.Modify, Attributes = [new AttributeChange("description", AttributeChangeKind.Replace, [], ["x"])] }],
        Issues = [new ValidationIssue(severity, code, $"English detail for {code}", attribute)],
    };

    private static PlannedChange ToChange(BatchOperation operation) => operation switch
    {
        MoveOperation move => new PlannedChange { Action = ChangeAction.Move, TargetDn = move.TargetContainerDn },
        RenameOperation rename => new PlannedChange { Action = ChangeAction.Rename, TargetDn = "CN=" + DistinguishedName.EscapeValue(rename.NameTemplate) },
        DeleteOperation delete => new PlannedChange { Action = ChangeAction.Delete, DeleteSubtree = delete.IncludeSubtree },
        AddToGroupsOperation add => new PlannedChange { Action = ChangeAction.AddMember, TargetDn = add.GroupDns[0] },
        _ => new PlannedChange
        {
            Action = ChangeAction.Modify,
            Attributes = [new AttributeChange("userAccountControl", AttributeChangeKind.Replace, ["512"], ["514"])],
        },
    };
}

/// <summary>
/// Executor double: records plans and reports every executable object as succeeded. <see cref="OnExecute"/> lets a
/// test apply the effect to the in-memory directory (so refreshed rows show the new state).
/// </summary>
internal sealed class RecordingBatchExecutor : IBatchExecutor
{
    private readonly ConcurrentQueue<ChangePlan> _plans = new();

    public IReadOnlyList<ChangePlan> Plans => [.. _plans];

    public Action<ChangePlan>? OnExecute { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public async Task<BatchResult> ExecuteAsync(IDirectorySession session, ChangePlan plan, BatchOptions? options, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
    {
        _plans.Enqueue(plan);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        OnExecute?.Invoke(plan);
        var now = DateTimeOffset.UtcNow;
        var results = new List<ObjectResult>();
        var completed = 0;
        foreach (var obj in plan.Objects)
        {
            if (obj.HasErrors || obj.IsNoOp)
            {
                results.Add(new ObjectResult(obj, ObjectResultStatus.Skipped, [], obj.DistinguishedName, now, now));
                continue;
            }

            var finalDn = obj.DistinguishedName;
            foreach (var change in obj.Changes)
            {
                if (change.Action == ChangeAction.Move && change.TargetDn is not null)
                {
                    finalDn = DistinguishedName.Parse(finalDn).WithParent(DistinguishedName.Parse(change.TargetDn)).ToString();
                }
            }

            var result = new ObjectResult(obj, ObjectResultStatus.Succeeded, [.. obj.Changes.Select(c => new ChangeResult(c, true))], finalDn, now, now);
            results.Add(result);
            completed++;
            progress?.Report(new BatchProgress(completed, plan.ExecutableCount, 0, result));
        }

        return new BatchResult(plan, results, session.Info.Server, session.Info.BoundIdentity, now, now, cancellationToken.IsCancellationRequested);
    }
}

/// <summary>Dialog double with scripted answers; records what was shown.</summary>
internal sealed class ScriptedDialogService : IDialogService
{
    public Queue<bool> ConfirmAnswers { get; } = new();

    public Queue<bool> TypedConfirmAnswers { get; } = new();

    public Queue<string?> PickedContainers { get; } = new();

    public Queue<IReadOnlyList<string>> PickedObjects { get; } = new();

    public List<string> Shown { get; } = [];

    public List<(string Title, string Message)> Errors { get; } = [];

    public List<(string Title, string Message, string Expected)> TypedConfirmations { get; } = [];

    public List<BatchEditorRequest> BatchEditorRequests { get; } = [];

    public List<string> PropertiesShown { get; } = [];

    public List<IReadOnlyList<string>> ResetPasswordRequests { get; } = [];

    public Func<SavedViewEditorViewModel, bool>? ViewEditorHandler { get; set; }

    public Func<ColumnChooserViewModel, bool>? ColumnChooserHandler { get; set; }

    /// <summary>Plays the Logon Hours dialog (null: Cancel).</summary>
    public Func<Presentation.Dialogs.LogonHoursViewModel, bool>? LogonHoursHandler { get; set; }

    public string? NewObjectResult { get; set; }

    public Task ShowInfoAsync(string title, string message)
    {
        Shown.Add($"info:{message}");
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, bool destructive)
    {
        Shown.Add($"confirm:{message}");
        return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    }

    public Task<bool> ConfirmTypedAsync(string title, string message, string expectedText)
    {
        TypedConfirmations.Add((title, message, expectedText));
        return Task.FromResult(TypedConfirmAnswers.Count > 0 && TypedConfirmAnswers.Dequeue());
    }

    public Task<string?> PickContainerAsync(IDirectorySession session, string? startDn) =>
        Task.FromResult(PickedContainers.Count > 0 ? PickedContainers.Dequeue() : null);

    public Task<IReadOnlyList<string>> PickObjectsAsync(IDirectorySession session, IReadOnlyList<Core.Model.ObjectKind> kinds, bool multiSelect) =>
        Task.FromResult(PickedObjects.Count > 0 ? PickedObjects.Dequeue() : (IReadOnlyList<string>)[]);

    public Task ShowPropertiesAsync(string distinguishedName)
    {
        PropertiesShown.Add(distinguishedName);
        return Task.CompletedTask;
    }

    public Task ShowBatchEditorAsync(BatchEditorRequest request)
    {
        BatchEditorRequests.Add(request);
        return Task.CompletedTask;
    }

    public Task ShowResetPasswordAsync(IReadOnlyList<string> distinguishedNames)
    {
        ResetPasswordRequests.Add(distinguishedNames);
        return Task.CompletedTask;
    }

    public Task<string?> ShowNewObjectAsync(Core.Model.ObjectKind kind, string parentDn)
    {
        Shown.Add($"new:{kind}:{parentDn}");
        return Task.FromResult(NewObjectResult);
    }

    public Task ShowFindAsync(string? startDn)
    {
        Shown.Add($"find:{startDn}");
        return Task.CompletedTask;
    }

    public Task ShowSessionLogAsync()
    {
        Shown.Add("sessionLog");
        return Task.CompletedTask;
    }

    public Task ShowRecycleBinAsync(Guid? select = null)
    {
        Shown.Add(select is { } guid ? $"recycleBin:{guid:D}" : "recycleBin");
        return Task.CompletedTask;
    }

    public Task ShowSettingsAsync()
    {
        Shown.Add("settings");
        return Task.CompletedTask;
    }

    public Task ShowConnectAsync()
    {
        Shown.Add("connect");
        return Task.CompletedTask;
    }

    public Task<bool> ShowChangeDomainControllerAsync()
    {
        Shown.Add("changeDc");
        return Task.FromResult(false);
    }

    public Task<bool> ShowSavedViewEditorAsync(SavedViewEditorViewModel editor) =>
        Task.FromResult(ViewEditorHandler?.Invoke(editor) ?? false);

    public Task<bool> ShowColumnChooserAsync(ColumnChooserViewModel chooser) =>
        Task.FromResult(ColumnChooserHandler?.Invoke(chooser) ?? false);

    public Task<bool> ShowLogonHoursAsync(Presentation.Dialogs.LogonHoursViewModel editor) =>
        Task.FromResult(LogonHoursHandler?.Invoke(editor) ?? false);

    /// <summary>Plays the Watchdog certificate confirmation (null: Cancel); every dialog shown is recorded.</summary>
    public Func<Presentation.Watchdog.WatchdogCertificateTrustViewModel, bool>? CertificateTrustHandler { get; set; }

    public List<Presentation.Watchdog.WatchdogCertificateTrustViewModel> CertificateDialogs { get; } = [];

    public Task<bool> ShowWatchdogCertificateTrustAsync(Presentation.Watchdog.WatchdogCertificateTrustViewModel dialog)
    {
        CertificateDialogs.Add(dialog);
        return Task.FromResult(CertificateTrustHandler?.Invoke(dialog) ?? false);
    }
}
