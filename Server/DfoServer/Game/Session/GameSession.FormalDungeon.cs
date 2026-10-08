using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Services;
using System.Security.Cryptography;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record FormalDungeon(DungeonEntryPlan Entry, DungeonFixedRoute Route,
        SinglePlayerDungeonRun Run, NativeDungeonCombatAuthority Authority, uint MapSeed,
        DungeonGroundState Ground, IReadOnlyDictionary<int, DungeonExplicitDropPool> Pools, Func<int, int> Draw)
    {
        public long StartedTimestamp { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public uint KillExperienceTotal { get; set; }
        public uint ClearTimeMilliseconds { get; set; }
        public bool SettlementEnabled { get; set; }
        public FormalSettlement? Settlement { get; set; }
    }
    FormalDungeon? formalDungeon;
    public DungeonRunSnapshot? DungeonSnapshot => owner.Execute(() => formalDungeon?.Run.Snapshot);

    public FormalDungeonReceipt? EnterFormalDungeon(uint dungeonId, uint questId, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops = null, ItemCatalog? items = null,
        Func<int, int>? draw = null)
    {
        var (expected, epoch) = owner.Execute(() => (Ready && Phase is SessionPhase.Town or SessionPhase.MapSelect && formalDungeon is null ? Selected : null, SelectionEpoch));
        if (expected is null ||
            expected.DungeonState is not { } state)
            return null;
        bool allowClearedQuest = questId == 3145 &&
            repository.LoadQuestClear(owner, expected, 3145) is not null;
        if (!DungeonEntryPlan.TryCreate(catalog, expected, dungeonId, questId, difficulty, out var entry, out _, allowClearedQuest))
            return null;
        state.Validate();
        if (entry!.QuestAcceptanceId == Guid.Empty)
            return null;
        if (state.NextObjectKey > ushort.MaxValue || state.LastAttempt == long.MaxValue)
            return null;
        var topology = entry!.Maze.StaticTopology;
        if (!topology.Supported) return null;
        var plans = new List<DungeonMonsterPlan>();
        uint next = state.NextObjectKey;
        foreach (var map in entry.Maze.MapSpecification!)
        {
            if (next > ushort.MaxValue) return null;
            var plan = DungeonMonsterPlan.Create(rooms, map, expected.Id, checked((ushort)next));
            plans.Add(plan);
            if (plan.Monsters.Count > 0) next = checked((uint)plan.Monsters.Max(monster => monster.ObjectKey) + 1);
        }
        var pools = new Dictionary<int, DungeonExplicitDropPool>();
        var groundReservations = new List<DungeonGroundReservation>();
        if ((drops is null) != (items is null)) throw new InvalidDataException("掉落资源依赖必须同时提供");
        if (drops is not null && items is not null)
        {
            foreach (var plan in plans)
            foreach (var monster in plan.Monsters.Where(monster => monster.IsBlocking && monster.MonsterId is 1 or 2 && monster.SpawnType == 0))
            {
                if (!pools.ContainsKey(monster.MonsterId))
                    pools.Add(monster.MonsterId, DungeonExplicitDropPolicy.CreatePool("monster/monster.lst", drops.Get(monster.MonsterId), items.Get));
                if (next == expected.Id) next++;
                if (next > ushort.MaxValue) return null;
                // 创建位置是已证资源位置；不将首次受击坐标冒充死亡瞬间位置。
                // 地面RAW不携带最终库存目的槽，独立对象使用显式临时record slot0。
                groundReservations.Add(new(monster.ObjectKey, next++, plan.Room.Map,
                    checked((ushort)monster.Source.RawValues[3]), checked((ushort)monster.Source.RawValues[4]), 0));
            }
        }
        var route = new DungeonFixedRoute(Array.AsReadOnly(plans.ToArray()),
            Array.AsReadOnly(topology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray()));
        var authority = new NativeDungeonCombatAuthority();
        var committedEntry = entry with { CharacterRevision = checked(expected.Revision + 1) };
        if (!SinglePlayerDungeonRun.TryCreate(committedEntry, state.LastAttempt + 1, epoch, route, authority, out var run, out _) ||
            !authority.BindTo(run!)) return null;
        var ground = new DungeonGroundState(run!, route.Rooms, groundReservations, drops is null ? "" : "monster/monster.lst");
        return owner.Execute(() =>
        {
            if (!Ready || Phase is not (SessionPhase.Town or SessionPhase.MapSelect) || formalDungeon is not null || SelectionEpoch != epoch || !ReferenceEquals(Selected, expected))
                return null;
            // 先持久预留key/attempt；通知失败也不回绕key或复用旧身份。
            Selected = repository.ReserveDungeonAttempt(owner, expected, next);
            formalDungeon = new(committedEntry, route, run!, authority, checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)),
                ground, pools, draw ?? RandomNumberGenerator.GetInt32);
            CurrentDungeonSelection = null;
            Phase = SessionPhase.Loading;
            return FormalReceipt();
        });
    }

    public FormalDungeonReceipt? CompleteFormalDungeonLoading() => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Loading || formalDungeon is not { } dungeon ||
            dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement || dungeon.Run.Snapshot.Identity.Epoch == long.MaxValue) return null;
        var result = dungeon.Run.CompleteLoading(dungeon.Run.Snapshot.Identity, Guid.NewGuid());
        if (result.Outcome != DungeonRunOutcome.Accepted) return null;
        Phase = dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon;
        return FormalReceipt();
    });

    public FormalDungeonReceipt? MoveFormalDungeon(byte x, byte y) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Dungeon || formalDungeon is not { } dungeon || dungeon.Ground.HasPendingPickup) return null;
        var result = dungeon.Run.Move(dungeon.Run.Snapshot.Identity, Guid.NewGuid(), new(x, y));
        if (result.Outcome != DungeonRunOutcome.Accepted) return null;
        dungeon.Ground.ExpireRoom();
        Phase = SessionPhase.Loading;
        return FormalReceipt();
    });

    public FormalDungeonDeathResult ObserveFormalDungeonDeath(NativeDungeonDeathObservation observation,
        DungeonKillExperienceCatalog rewards, CharacterGrowthCatalog growth) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) || formalDungeon is not { } dungeon || dungeon.Ground.HasPendingPickup)
            return new FormalDungeonDeathResult(DungeonRunOutcome.Rejected);
        var issued = dungeon.Authority.ValidateAndIssue(dungeon.Run, dungeon.Run.Snapshot.Identity, observation);
        if (issued.Death is null) return new FormalDungeonDeathResult(issued.Outcome);
        var preparation = dungeon.Run.PrepareConfirmedDeath(issued.Death);
        if (preparation.Outcome != DungeonRunOutcome.Accepted)
            return new FormalDungeonDeathResult(preparation.Outcome);
        var pending = preparation.Pending!;
        var reward = DungeonKillExperiencePolicy.Plan(dungeon.Run, pending, rewards);
        FrozenDungeonGroundDeath? loot = null;
        if (dungeon.Ground.HasReservation(pending.Death.MonsterKey))
            loot = dungeon.Ground.GetOrFreeze(pending, () =>
            {
                var choice = DungeonExplicitDropPolicy.Select(dungeon.Pools[pending.Monster.MonsterId],
                    dungeon.Draw(DungeonExplicitDropPolicy.TriggerDenominator), dungeon.Draw(DungeonExplicitDropPolicy.SelectionDenominator));
                return choice is null ? null : InventoryRewardGrantService.Create(choice.Template, choice.Count);
            });
        byte previousLevel = Selected.Level;
        var committed = CommitDungeonGrowth(SelectionEpoch, Selected, dungeon.Run, pending, reward, growth, loot);
        if (loot is not null && !dungeon.Ground.PublishCommitted(pending, loot))
            throw new InvalidOperationException("已提交掉落的原准备不能发布");
        Phase = dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon;
        dungeon.KillExperienceTotal = checked(dungeon.KillExperienceTotal + reward.Amount);
        if (committed.Death.ClearFact is not null)
            dungeon.ClearTimeMilliseconds = checked((uint)System.Diagnostics.Stopwatch.GetElapsedTime(dungeon.StartedTimestamp).TotalMilliseconds);
        return new FormalDungeonDeathResult(DungeonRunOutcome.Accepted, FormalReceipt(), pending.Death.MonsterKey, reward, committed.Death.ClearFact, loot?.Ground, previousLevel);
    });

    public FormalDungeonPickupResult PickFormalDungeonItem(uint groundKey, ItemCatalog items, EpicPieceCatalog pieces) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) || formalDungeon is not { } dungeon || dungeon.Run.HasPendingSettlement)
            return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected);
        var identity = dungeon.Run.Snapshot.Identity;
        var original = dungeon.Ground.FindAvailable(identity, groundKey) ?? dungeon.Ground.Snapshot.Items
            .Where(item => item.Status == DungeonGroundStatus.PickupPending && item.Ground.Reservation.GroundKey == groundKey)
            .Select(item => item.Ground).SingleOrDefault();
        if (original is null) return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected);
        var prepared = dungeon.Ground.PreparePickup(identity, original);
        if (prepared is null) return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected);
        var plan = InventoryRewardGrantService.TryGrantCore(Selected, original.Core, items.Get(original.Core.ItemId), pieces);
        if (!plan.Accepted)
        {
            dungeon.Ground.CancelPickup(prepared);
            return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected);
        }
        // 存储异常/结果不明保留同一准备；原key重试只读已提交receipt，不重发资产。
        var commit = repository.CommitDungeonPickup(owner, Selected, prepared, plan.Plan!);
        if (!dungeon.Ground.ConfirmPickup(prepared)) throw new InvalidOperationException("已提交拾取不能确认原准备");
        Selected = commit.Character;
        return new FormalDungeonPickupResult(DungeonRunOutcome.Accepted, FormalReceipt(), original, commit);
    });

    public TownTravelReceipt? ReturnFromFormalDungeon() => owner.Execute(() =>
    {
        if (!Ready || Selected is null || formalDungeon is not { } dungeon || dungeon.Ground.HasPendingPickup || pendingQuestCompletion is not null) return null;
        if (dungeon.Run.Return(dungeon.Run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted) return null;
        dungeon.Ground.ExpireRoom();
        formalDungeon = null;
        Phase = SessionPhase.Town;
        return new TownTravelReceipt(Selected, owner.Generation, SelectionEpoch);
    });

    public bool CanPublishFormalDungeon(FormalDungeonReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) && formalDungeon?.Run.Snapshot.Identity == receipt.Snapshot.Identity);

    public bool CanPublishFormalDungeonRejection(Character character, long epoch, SessionPhase phase,
        DungeonSelectionContext? selection) => owner.Execute(() =>
    {
        if (!Ready || SelectionEpoch != epoch || Phase != phase || formalDungeon is not null ||
            !ReferenceEquals(CurrentDungeonSelection, selection))
            return false;
        if (!ReferenceEquals(Selected, character))
            throw new InvalidOperationException("副本拒绝发布前角色快照已变化");
        return true;
    });

    FormalDungeonReceipt FormalReceipt()
    {
        var dungeon = formalDungeon ?? throw new InvalidOperationException("无正式副本实例");
        var snapshot = dungeon.Run.Snapshot;
        var original = dungeon.Route.Rooms.Single(room => room.Room.Map == snapshot.CurrentMap);
        var living = snapshot.LivingMonsters.ToHashSet();
        var monsters = original with { Monsters = Array.AsReadOnly(original.Monsters.Where(monster => living.Contains(monster.ObjectKey)).ToArray()) };
        return new(Selected!, owner.Generation, SelectionEpoch, dungeon.Entry with { StartMap = snapshot.CurrentMap }, snapshot, monsters, dungeon.MapSeed);
    }
}
