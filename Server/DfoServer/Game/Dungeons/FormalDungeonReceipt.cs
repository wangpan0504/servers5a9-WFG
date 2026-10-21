using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal sealed record FormalDungeonReceipt(Character Character, long Generation, long SelectionEpoch,
    DungeonEntryPlan Entry, DungeonRunSnapshot Snapshot, DungeonMonsterPlan Monsters, uint MapSeed)
{
    public bool HellPartyRoomCleared => Snapshot.Phase == SinglePlayerDungeonPhase.Dungeon &&
        Entry.HellParty is { } hell && Snapshot.CurrentMap.Id == hell.MapId && Monsters.HellWaves.Count > 0 &&
        Monsters.Monsters.Where(monster => monster.IsBlocking).All(monster => !Snapshot.LivingMonsters.Contains(monster.ObjectKey));

    public ImmutableArray<DungeonGroundObject> PassiveItems { get; init; } = [];
    // #233 强袭入场提交时扣除材料变化的背包格，仅入场回执携带。
    public ImmutableArray<InventorySlot> AssaultConsumedSlots { get; init; } = [];
    // 实验性：深渊派对入场票在入场提交时扣掉的背包格与票据（日志 + UPDATE_ITEM_LIST 用）。
    public ImmutableArray<InventorySlot> HellConsumedSlots { get; init; } = [];
    public HellPartyTicket? HellTicket { get; init; }
    public HellPartyTicket? LegendTicket { get; init; }
    // #510/#511 普通入场票在入场提交时扣掉的背包格，与实际扣掉的入场一次性疲劳（无限疲劳时为 0）。
    public ImmutableArray<InventorySlot> EntryItemConsumedSlots { get; init; } = [];
    public ushort EntryFatigueCharged { get; init; }
    // 演出返回只确认缓存呈现，不重新执行业务装载或授权基础图演员。
    public Guid ScriptedReturnId { get; init; }
    public bool NavigationReplay { get; init; }
    public uint ClearTimeMilliseconds { get; init; }
    public bool AutomaticRevival { get; init; }
}
internal sealed record FormalDungeonDeathResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    ushort MonsterKey = 0, DungeonExperienceReward? Reward = null, DungeonClearFact? ClearFact = null,
    DungeonGroundObject? Drop = null, byte? PreviousLevel = null)
{
    public ImmutableArray<DungeonGroundObject> Drops { get; init; } = [];
    public uint GainedExperience { get; init; }
    public DungeonPlannedMonster? ReportedTarget { get; init; }
    public bool SpecialChildRetired { get; init; }
    public DungeonElevatorReceipt? Elevator { get; init; }
    public IReadOnlyList<ActiveMainQuest>? PreviousQuests { get; init; }
    public IReadOnlyList<InventorySlot> QuestDropSlots { get; init; } = [];
    public IReadOnlyList<ushort> CulledMonsterKeys { get; init; } = [];
    public DungeonRoomClearFact? RoomClearFact { get; init; }
    public DfoServer.GameWorld.Dungeons.DungeonPoint? BossPositionUpdate { get; init; }
}
internal sealed record DungeonPickupCommit(Character Character, bool Duplicate, InventorySlot Destination, long ItemUid,
    bool ProjectionCurrent, CurrencyKind? Currency = null, int Amount = 0);
internal sealed record FormalDungeonPickupResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DungeonGroundObject? Ground = null, DungeonPickupCommit? Commit = null)
{
    public bool CapacityRejected { get; init; }
}
internal sealed record DungeonSettlementPresentation(uint ClearTimeMilliseconds, byte RankPoint, byte RankGrade,
    uint MonsterExperience)
{
    public uint MonsterGrowthContractExperience { get; init; }
}
internal sealed record FormalDungeonSettlementResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DfoServer.GameWorld.Dungeons.DungeonClearExperienceReward? Reward = null, DungeonSettlementPresentation? Presentation = null,
    Character? PreviousCharacter = null)
{
    public uint GainedExperience { get; init; }
    public bool LuckyStarGained { get; init; }
    public int LuckyStarTotal { get; init; }
    public DungeonCardReward? Card { get; init; }
    public InventoryResult? Inventory { get; init; }
    public uint? MailId { get; init; }
    public DfoServer.Game.Session.AssaultClearResult? AssaultClear { get; init; }
    public DfoServer.Game.Session.CrackClearResult? CrackClear { get; init; }
    public DungeonCardOptions? Options { get; init; }
    public DungeonCardReward? BlackDiamond { get; init; }
}
