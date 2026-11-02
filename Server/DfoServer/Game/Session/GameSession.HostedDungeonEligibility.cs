using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    HostedDungeonEligibilityCoordinator? hostedEligibility;
    CancellationTokenSource? hostedEligibilityCts;
    Guid hostedEligibilityProbeId;
    HostedDungeonEligibilityResult? hostedEligibilityLatest;
    TaskCompletionSource<HostedDungeonEligibilityResult?>? hostedEligibilityApplied;

    internal void AttachHostedDungeonEligibility(HostedDungeonEligibilityCoordinator? coordinator) =>
        hostedEligibility = coordinator;

    internal HostedDungeonEligibilityResult? LastHostedDungeonEligibility => hostedEligibilityLatest;

    internal Task<HostedDungeonEligibilityResult?> HostedDungeonEligibilityTask =>
        hostedEligibilityApplied?.Task ?? Task.FromResult(hostedEligibilityLatest);

    void CancelHostedDungeonEligibility()
    {
        hostedEligibilityProbeId = Guid.Empty;
        hostedEligibilityApplied?.TrySetResult(null);
        var cts = Volatile.Read(ref hostedEligibilityCts);
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    void ScheduleHostedDungeonEligibility(FormalDungeonReceipt? receipt)
    {
        if (hostedEligibility is null || receipt is null || !owner.IsCurrent) return;
        CancelHostedDungeonEligibility();
        var capture = CaptureHostedDungeonEligibility(receipt);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref hostedEligibilityCts, cts)?.Dispose();
        hostedEligibilityProbeId = capture.ProbeId;
        var applied = new TaskCompletionSource<HostedDungeonEligibilityResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostedEligibilityApplied = applied;
        var coordinator = hostedEligibility;
        _ = ProbeHostedDungeonEligibilityAsync(coordinator, capture, receipt.Character, cts, applied);
    }

    HostedDungeonEligibilityCapture CaptureHostedDungeonEligibility(FormalDungeonReceipt receipt)
    {
        var snapshot = receipt.Snapshot;
        return new(Guid.NewGuid(), receipt.Generation, receipt.SelectionEpoch, receipt.Character.Id, receipt.Character.Revision,
            snapshot.Identity, receipt.Entry.DungeonId, receipt.Entry.QuestId, snapshot.CurrentMap.Id, snapshot.CurrentMap.Position,
            snapshot.LivingMonsters.ToArray(), HostedDungeonEligibilityRules.Spawns(receipt.Monsters.Monsters),
            HostedDungeonEligibilityRules.RejectReason(receipt.Entry, PartyDungeon is not null,
                receipt.ScriptedReturnId != Guid.Empty, receipt.NavigationReplay));
    }

    async Task ProbeHostedDungeonEligibilityAsync(
        HostedDungeonEligibilityCoordinator coordinator, HostedDungeonEligibilityCapture capture, Character expected,
        CancellationTokenSource cts, TaskCompletionSource<HostedDungeonEligibilityResult?> applied)
    {
        HostedDungeonEligibilityResult result;
        string kind = "hosted_dungeon_eligibility";
        try
        {
            result = await coordinator.RunAsync(capture, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = HostedDungeonEligibilityRules.Refuse(
                cts.IsCancellationRequested
                    ? HostedDungeonEligibilityRules.ReasonCancelled
                    : HostedDungeonEligibilityRules.ReasonTimeout, capture.ProbeId);
        }
        catch (Exception error)
        {
            kind = "hosted_dungeon_eligibility_failed";
            result = HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonEof, capture.ProbeId);
            try { coordinator.Write(kind, new { capture.ProbeId, Reason = error.GetType().Name, error.Message }); }
            catch (Exception) { }
        }
        bool accepted = false;
        try
        {
            if (owner.IsCurrent)
            {
                accepted = owner.Execute(() =>
                {
                    if (hostedEligibilityProbeId != capture.ProbeId || !Ready || SelectionEpoch != capture.SelectionEpoch ||
                        !ReferenceEquals(Selected, expected) || Selected is not { } current ||
                        current.Id != capture.CharacterId || current.Revision != capture.CharacterRevision ||
                        formalDungeon is not { } dungeon || dungeon.Run.Snapshot.Identity != capture.Identity ||
                        dungeon.Run.Snapshot.CurrentMap.Id != capture.MapId ||
                        dungeon.Run.Snapshot.CurrentMap.Position != capture.MapPosition ||
                        !dungeon.Run.Snapshot.LivingMonsters.SequenceEqual(capture.LivingKeys))
                        return false;
                    hostedEligibilityLatest = result;
                    return true;
                });
            }
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation") { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (kind == "hosted_dungeon_eligibility")
            {
                try
                {
                    coordinator.Write(kind, new
                    {
                        capture.ProbeId,
                        result.CanFight,
                        result.Reason,
                        Applied = accepted,
                        capture.CharacterId,
                        capture.DungeonId,
                        InstanceId = capture.Identity.InstanceId,
                        capture.MapId,
                        result.ProfileSchema
                    });
                }
                catch (Exception) { }
            }
            applied.TrySetResult(accepted ? result : null);
            if (ReferenceEquals(Volatile.Read(ref hostedEligibilityCts), cts))
                Interlocked.CompareExchange(ref hostedEligibilityCts, null, cts);
            cts.Dispose();
        }
    }
}
