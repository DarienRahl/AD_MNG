namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Creation settle window (section 10.6.3): tools create accounts in several LDAP steps that straddle polls, so a new
/// object is stored with <c>CreationPendingUntil</c> and changes inside the window update the snapshot silently. Every
/// cycle (also NoChange) the settled objects emit one change set – the Created event with the final values, risky UAC
/// bits, members – and the row is upserted with <c>CreationPendingUntil = null</c>.
/// </summary>
internal sealed class CreationSettler
{
    private readonly DetectionServices _services;

    public CreationSettler(DetectionServices services)
    {
        _services = services;
    }

    /// <summary>Emits the due Created change sets into the cycle's current chunk.</summary>
    public async Task<int> SettleDueAsync(ObjectProcessor processor, DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var due = await _services.Store.GetPendingCreationsAsync(cycle.Now, cancellationToken).ConfigureAwait(false);
        var settled = 0;
        foreach (var created in due)
        {
            if (cycle.DeletedGuids.Contains(created.ObjectGuid) || created.IsDeleted)
            {
                continue;
            }

            var current = cycle.Chunk.PendingUpsert(created.ObjectGuid) ?? created;
            await processor.EmitCreatedAsync(current, cancellationToken).ConfigureAwait(false);
            cycle.Chunk.Upsert(current with { CreationPendingUntil = null });
            settled++;
        }

        return settled;
    }
}
