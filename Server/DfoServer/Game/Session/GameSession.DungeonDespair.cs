using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record DungeonDespairClearReceipt(FormalDungeonReceipt Dungeon, PreparedDungeonDespairClear Pending,
    DungeonDespairClearCommit Commit, TitleAchievementResult Title);

internal sealed partial class GameSession
{
    // NOTI27的TOD层字段直接经01E0DD30限制在1..100，再在03A32557展示；零也会归一为1。
    public ushort DespairSelectionLayer() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { Level: >= 70 } character || Phase != SessionPhase.MapSelect ||
            !towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) ||
            area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var map) ||
            !map.Options.Any(option => option.DungeonId == 11008)) return (ushort)0;
        var progress = repository.LoadDespairProgress(owner, character);
        return progress == new DungeonDespairProgress() ? (ushort)0 : checked((ushort)progress.NextLayer);
    });

    sealed class DespairSettlementState(PreparedDungeonDespairClear pending, TitleAchievementResult title)
    {
        public PreparedDungeonDespairClear Pending { get; } = pending;
        public TitleAchievementResult Title { get; } = title;
        public DungeonDespairClearCommit? Commit { get; set; }
        public bool Published { get; set; }
    }

    public DungeonDespairClearReceipt? CompleteDungeonDespair(DungeonDespairRewardPolicy policy,
        TowerOfDespairCatalog tower, TitleAchievementService titles)
    {
        var capture = owner.Execute(() => Ready && Selected is not null && Phase == SessionPhase.Settlement &&
            formalDungeon is { Entry.IsDespair: true, SpecialEntry.Kind: DungeonSpecialEntryKind.Despair } dungeon &&
            dungeon.Run.ClearFact is not null && !dungeon.Run.HasPendingDeath ? (Selected, dungeon) : default);
        if (capture.dungeon is not { } current || capture.Selected is not { } character) return null;
        if (current.DespairSettlement is not null) return owner.Execute(() => CommitDespairResult(current));
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var floor = tower.Floors[current.SpecialEntry!.Layer - 1];
        var title = titles.DespairClear(character, current.Run.ClearFact!, floor, now);
        if (!title.Accepted) throw new InvalidDataException("绝望通关称号不满足当前层资源合同");
        var reward = policy.Prepare(floor.Layer, current.Draw, now);
        var given = policy.Delivery(character with { Inventory = title.Plan?.Inventory.After ?? character.Inventory }, reward, now);
        var inventory = new InventoryPlan(character.Inventory, given.After, "despair_clear_reward",
            overflowItems: (title.Plan?.Inventory.OverflowItems ?? []).AddRange(given.OverflowItems));
        uint elapsed = checked((uint)System.Diagnostics.Stopwatch.GetElapsedTime(current.StartedTimestamp).TotalMilliseconds);
        return owner.Execute(() =>
        {
            if (!Ready || !ReferenceEquals(Selected, character) || !ReferenceEquals(formalDungeon, current) ||
                current.DespairSettlement is not null || Phase != SessionPhase.Settlement) return null;
            var pending = current.Run.PrepareDespairClear(character, current.SpecialEntry,
                ShopPurchaseCount.Today(now), elapsed, inventory, title.Plan, reward);
            if (pending is null) return null;
            current.DespairSettlement = new(pending, title);
            return CommitDespairResult(current);
        });
    }

    DungeonDespairClearReceipt? CommitDespairResult(FormalDungeon dungeon)
    {
        if (!Ready || Phase != SessionPhase.Settlement || !ReferenceEquals(formalDungeon, dungeon) ||
            dungeon.DespairSettlement is not { Published: false } state) return null;
        if (state.Commit is null)
        {
            state.Commit = repository.CommitDungeonDespairClear(owner, dungeon.Run, state.Pending);
            Selected = state.Commit.Character;
            if (!dungeon.Run.ConfirmCommittedDespairClear(state.Pending))
                throw new InvalidOperationException("绝望已持久通关不能确认原实例");
            dungeon.SettlementEnabled = true;
        }
        var receipt = new InventoryCommitReceipt(Selected!, owner.Generation, SelectionEpoch)
        {
            Phase = SessionPhase.Settlement, DungeonIdentity = state.Pending.Fact.Identity,
            PreviousQuests = state.Pending.Expected.ActiveMainQuests,
            PreviousSkillSwitchInventory = state.Pending.Expected.SkillSwitchInventory
        };
        return new(FormalReceipt(), state.Pending, state.Commit,
            state.Title with { Plan = state.Title.Plan is { } plan ? plan with { Inventory = state.Commit.Inventory } : null, Receipt = receipt });
    }

    public bool CanPublishDespairClear(DungeonDespairClearReceipt receipt) => owner.Execute(() =>
        CanPublishFormalDungeon(receipt.Dungeon) && Phase == SessionPhase.Settlement &&
        formalDungeon?.DespairSettlement is { Published: false } state && ReferenceEquals(state.Pending, receipt.Pending) &&
        ReferenceEquals(state.Commit, receipt.Commit));

    public void ConfirmDespairClearPublished(DungeonDespairClearReceipt receipt) => owner.Execute(() =>
    {
        if (CanPublishDespairClear(receipt)) formalDungeon!.DespairSettlement!.Published = true;
        return true;
    });
}
