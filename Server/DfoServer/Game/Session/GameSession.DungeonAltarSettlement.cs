using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed record DungeonAltarResultReceipt(PreparedDungeonAltarSettlement Pending, DungeonAltarSettlementCommit Commit, int Stage);

internal sealed partial class GameSession
{
    sealed class AltarSettlementState(PreparedDungeonAltarSettlement pending)
    {
        public PreparedDungeonAltarSettlement Pending { get; } = pending;
        public DungeonAltarSettlementCommit? Commit { get; set; }
        public int Stage { get; set; }
        public long Due { get; set; }
    }

    public DungeonAltarReceipt? BeginDungeonAltarSettlement(DungeonAltarRewardPolicy policy, CharacterGrowthCatalog growth)
    {
        var capture = owner.Execute(() =>
        {
            if (!Ready || Selected is null || formalDungeon is not { SpecialEntry: { } special } dungeon ||
                special.Kind is not (DungeonSpecialEntryKind.Blood or DungeonSpecialEntryKind.Ultimate) ||
                dungeon.Run.CurrentAltar is null || dungeon.Run.HasPendingDeath ||
                dungeon.Run.ClearFact is null && (!dungeon.CharacterDead ||
                    dungeon.AltarRevivalDue is not { } due || AltarClock() < due)) return null;
            return new AltarCapture(Selected, dungeon, dungeon.Run.ClearFact is null, dungeon.CharacterDead, AltarClock(), dungeon.Run.AltarCompletedRounds,
                dungeon.Run.AltarMaximumRounds, dungeon.AltarSettlement is not null);
        });
        if (capture is null) return null;
        if (capture.Prepared) return owner.Execute(() => CommitAltarResult(capture.Dungeon, growth));
        var dungeon = capture.Dungeon;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var reward = policy.Prepare(checked((int)dungeon.Entry.DungeonId), capture.Character.Level,
            capture.CompletedRounds, capture.MaximumRounds,
            dungeon.SpecialEntry!.Expected.CharacterEntries + 1, dungeon.Draw, now);
        var adjusted = owner.Execute(() => ReferenceEquals(Selected, capture.Character) && ReferenceEquals(formalDungeon, dungeon)
            ? reward with { Experience = reward.Experience == 0 ? 0 : Math.Min(1000000,
                PlanExperience(capture.Character, ExperienceEvent.Clear, reward.Experience, 0, now.ToUnixTimeSeconds(),
                    dungeon.Entry.DungeonId, dungeon.Entry.ExperienceEquipmentModule).Total) } : null);
        if (adjusted is null) return null;
        byte level = capture.Character.Level;
        if (adjusted.Experience > 0)
        {
            var grown = CharacterGrowthPlan.AddExperience(capture.Character, adjusted.Experience, growth);
            if (!grown.Success || grown.Plan is null) throw new InvalidOperationException("祭坛结算成长不可规划");
            level = grown.Plan.TargetLevel;
        }
        var inventory = policy.Delivery(capture.Character, level, adjusted, now);
        return owner.Execute(() =>
        {
            if (!Ready || !ReferenceEquals(Selected, capture.Character) || !ReferenceEquals(formalDungeon, dungeon) ||
                dungeon.CharacterDead != capture.Dead || dungeon.AltarSettlement is not null) return null;
            var pending = dungeon.Run.PrepareAltarSettlement(capture.Character, adjusted, inventory,
                checked((uint)capture.Clock), capture.Failed);
            if (pending is null) return null;
            dungeon.AltarSettlement = new(pending);
            SetState(SessionPhase.Settlement);
            return CommitAltarResult(dungeon, growth);
        });
    }

    sealed record AltarCapture(Character Character, FormalDungeon Dungeon, bool Failed, bool Dead, long Clock,
        int CompletedRounds, int MaximumRounds, bool Prepared);

    public DungeonAltarReceipt? CurrentDungeonAltarResult() => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Settlement ||
            formalDungeon is not { AltarSettlement: { Commit: not null } result } dungeon ||
            dungeon.Run.CurrentAltar is null || result.Stage is < 0 or > 2) return null;
        return AltarResultReceipt(dungeon, result, AltarClock());
    });

    public DungeonAltarReceipt? AdvanceDungeonAltarResult(DungeonAltarReceipt expected) => owner.Execute(() =>
    {
        if (expected.Result is not { Stage: >= 0 and < 2 } || !CanPublishDungeonAltar(expected) ||
            formalDungeon?.AltarSettlement is not { Commit: not null } result) return null;
        long now = AltarClock();
        result.Due = now;
        return TickAltarSettlement(expected, result, now);
    });

    DungeonAltarReceipt? CommitAltarResult(FormalDungeon dungeon, CharacterGrowthCatalog growth)
    {
        if (!Ready || !ReferenceEquals(formalDungeon, dungeon) || dungeon.AltarSettlement is not { } result ||
            Phase != SessionPhase.Settlement || !dungeon.Run.IsPendingAltarSettlement(result.Pending)) return null;
        // 先提交资产，演出期间保持原准备以阻止资产并发；断线不会丢失已冻结奖励。
        result.Commit ??= repository.CommitDungeonAltarSettlement(owner, dungeon.Run, result.Pending, growth);
        Selected = result.Commit.Character;
        long now = AltarClock();
        if (result.Due == 0) result.Due = checked(now + 8000);
        return AltarResultReceipt(dungeon, result, now);
    }

    DungeonAltarReceipt? TickAltarSettlement(DungeonAltarReceipt scheduled, AltarSettlementState result, long now)
    {
        if (result.Commit is null || scheduled.Result is not { } previous ||
            previous.Stage != result.Stage || !ReferenceEquals(previous.Pending, result.Pending)) return null;
        if (now < result.Due) return AltarResultReceipt(formalDungeon!, result, now);
        result.Stage++;
        if (result.Stage == 1) result.Due = checked(now + 8000);
        else if (result.Stage == 2)
        {
            if (!formalDungeon!.Run.ConfirmCommittedAltarSettlement(result.Pending))
                throw new InvalidOperationException("祭坛持久奖励不能确认原实例");
            result.Due = checked(now + 60000);
        }
        else
        {
            var returned = ReturnFromFormalDungeon();
            return returned is null ? null : scheduled with { Clock = now, Due = null, Returned = returned };
        }
        return AltarResultReceipt(formalDungeon!, result, now);
    }

    DungeonAltarReceipt AltarResultReceipt(FormalDungeon dungeon, AltarSettlementState result, long now) =>
        AltarReceipt(dungeon.Run.CurrentAltar!, now) with { Due = result.Due, Completed = true,
            Result = new(result.Pending, result.Commit!, result.Stage) };
}
