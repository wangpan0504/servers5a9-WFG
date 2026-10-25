using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonGroundReservation(ushort MonsterKey, uint GroundKey, DungeonMap Map,
    ushort X, ushort Y, short RecordSlot, int Ordinal = 0);
internal sealed record DungeonGroundContent(ItemCore? Item, int GoldAmount = 0)
{
    public bool IsGold => Item is null;
    public void Validate()
    {
        if (Item is null)
        {
            if (GoldAmount <= 0)
                throw new InvalidDataException("地面金币必须是正增量，不能冒充空物品");
            return;
        }
        if (GoldAmount != 0)
            throw new InvalidDataException("地面对象不能同时保存物品和金币");
        Item.Validate();
        if (Item.Kind is ItemKind.Avatar or ItemKind.Creature)
            throw new InvalidDataException("地面普通集合尚不携带特殊物品detail");
    }
}
internal sealed record DungeonGroundObject(Guid DeathEventId, DungeonRunIdentity DeathIdentity,
    DungeonGroundReservation Reservation, DungeonGroundContent Content)
{
    public string ResourceIdentity { get; init; } = "";
    public bool UsesCollectionLedger { get; init; }
    public DungeonInventoryDiscardOrigin? InventoryOrigin { get; init; }
    public DungeonPassiveDropOrigin? PassiveOrigin { get; init; }
    public bool IsGold => Content.IsGold;
    public int GoldAmount => Content.GoldAmount;
    public ItemCore Core => Content.Item ?? throw new InvalidOperationException("地面金币使用数量模型，不提供虚假物品Core");
    public DungeonGroundObject(Guid eventId, DungeonRunIdentity identity, DungeonGroundReservation reservation, ItemCore core)
        : this(eventId, identity, reservation, new DungeonGroundContent(core)) { }
}
internal enum DungeonGroundStatus { Frozen, Available, PickupPending, Picked, Expired }
internal sealed record DungeonGroundItemSnapshot(DungeonGroundObject Ground, DungeonGroundStatus Status);
internal sealed record DungeonGroundSnapshot(DungeonRunIdentity Identity, DungeonMap Map,
    ImmutableArray<DungeonGroundItemSnapshot> Items);

internal sealed class FrozenDungeonGroundDeath
{
    public ConfirmedDungeonMonsterDeath Death { get; }
    public DungeonRunIdentity Identity => Death.Identity;
    public DungeonRunIdentity CommittedIdentity { get; }
    public DungeonMap Map { get; }
    public ImmutableArray<DungeonGroundObject> Grounds { get; }
    // 单件结果调用方不能悄悄截掉集合；集合调用方必须处理每个独立key。
    public DungeonGroundObject? Ground => Grounds.Length > 1 || Grounds.Any(item => item.IsGold)
        ? throw new InvalidOperationException("多件冻结掉落必须使用完整集合提交") : Grounds.FirstOrDefault();
    public string ResourceIdentity { get; }
    public bool UsesCollectionLedger { get; }

    public FrozenDungeonGroundDeath(ConfirmedDungeonMonsterDeath death, DungeonRunIdentity committedIdentity,
        DungeonMap map, DungeonGroundObject? ground, string resourceIdentity = "")
        : this(death, committedIdentity, map, ground is null ? [] : [ground], resourceIdentity, false) { }

    public FrozenDungeonGroundDeath(ConfirmedDungeonMonsterDeath death, DungeonRunIdentity committedIdentity,
        DungeonMap map, ImmutableArray<DungeonGroundObject> grounds, string resourceIdentity, bool usesCollectionLedger = true)
    {
        if (grounds.IsDefault)
            throw new InvalidDataException("冻结掉落集合未初始化");
        Death = death;
        CommittedIdentity = committedIdentity;
        Map = map;
        Grounds = grounds;
        ResourceIdentity = resourceIdentity;
        UsesCollectionLedger = usesCollectionLedger;
    }
}

internal sealed class DungeonGroundPickupPreparation(DungeonRunIdentity identity, DungeonGroundObject ground)
{
    public DungeonRunIdentity Identity { get; } = identity;
    public DungeonGroundObject Ground { get; } = ground;
}

// 精确绑定一个Run，所有方法由同一Session owner串行调用；不持有角色、数据库或网络状态。
internal sealed class DungeonGroundState
{
    sealed class DeathState(FrozenDungeonGroundDeath frozen)
    {
        public FrozenDungeonGroundDeath Frozen { get; } = frozen;
        public bool Committed { get; set; }
    }

    sealed class ItemState(DungeonGroundObject ground, DeathState? death)
    {
        public DungeonGroundObject Ground { get; } = ground;
        public DeathState? Death { get; } = death;
        public bool Committed => Death?.Committed ?? ((Ground.InventoryOrigin is not null || Ground.PassiveOrigin is not null) &&
            Status is DungeonGroundStatus.Available or DungeonGroundStatus.PickupPending or DungeonGroundStatus.Picked);
        public DungeonGroundStatus Status { get; set; } = DungeonGroundStatus.Frozen;
    }

    readonly SinglePlayerDungeonRun run;
    readonly string resourceIdentity;
    readonly Dictionary<ushort, ImmutableArray<DungeonGroundReservation>> reservations;
    readonly Dictionary<ushort, DungeonPlannedMonster> monsters;
    readonly Dictionary<ushort, DeathState> frozen = [];
    readonly Dictionary<uint, ItemState> groundItems = [];
    readonly HashSet<ushort> failedOrCreating = [];
    readonly HashSet<uint> occupiedKeys = [];
    readonly Dictionary<DungeonMap, ImmutableArray<DungeonGroundObject>> passiveByMap;
    DungeonGroundPickupPreparation? pendingPickup;
    DungeonGroundObject? pendingInventoryDrop;
    ImmutableArray<DungeonGroundObject> PassiveGrounds { get; }

    // 预留范围是否已持久化、资源准入和RecordSlot合法常量由主调用方证明；这里只检查完整对象身份与碰撞。
    public DungeonGroundState(SinglePlayerDungeonRun run, IReadOnlyList<DungeonMonsterPlan> routePlans,
        IReadOnlyList<DungeonGroundReservation> reservations, string resourceIdentity = "",
        IReadOnlyList<DungeonGroundObject>? passiveGrounds = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(routePlans);
        ArgumentNullException.ThrowIfNull(reservations);
        this.run = run;
        this.resourceIdentity = resourceIdentity;
        var initial = run.Snapshot;
        monsters = [];
        var maps = new Dictionary<ushort, DungeonMap>();
        var occupied = occupiedKeys;
        occupied.Add(initial.Identity.CharacterId);
        foreach (var plan in routePlans)
        {
            foreach (var actor in plan.SpecialObjects)
                if (!occupied.Add(actor.ObjectKey)) throw new InvalidDataException("特殊父对象key重复或碰撞角色");
            foreach (var monster in plan.Monsters)
            {
                if (!occupied.Add(monster.ObjectKey) || !monsters.TryAdd(monster.ObjectKey, monster))
                    throw new InvalidDataException("地面预规划的全路线怪物key重复或碰撞角色");
                maps.Add(monster.ObjectKey, plan.Room.Map);
            }
        }
        var grouped = new Dictionary<ushort, List<DungeonGroundReservation>>();
        foreach (var reservation in reservations)
        {
            if (!maps.TryGetValue(reservation.MonsterKey, out var map) || map != reservation.Map ||
                reservation.GroundKey == 0 || !occupied.Add(reservation.GroundKey) || reservation.Ordinal < 0)
                throw new InvalidDataException("地面预留key重复、碰撞或不属于该怪物房间");
            if (!grouped.TryGetValue(reservation.MonsterKey, out var group))
                grouped.Add(reservation.MonsterKey, group = []);
            group.Add(reservation);
        }
        this.reservations = [];
        foreach (var (monster, group) in grouped)
        {
            var ordered = group.OrderBy(row => row.Ordinal).ToImmutableArray();
            if (ordered.Length > 500 || ordered.Where((row, index) => row.Ordinal != index).Any())
                throw new InvalidDataException("单怪地面预留序号必须连续且不超过完整通知容量");
            this.reservations.Add(monster, ordered);
        }
        PassiveGrounds = (passiveGrounds ?? []).ToImmutableArray();
        var parents = new Dictionary<ushort, (DungeonPlannedSpecialObject Parent, DungeonMap Map, int Association)>();
        foreach (var plan in routePlans)
        {
            int association = 0;
            foreach (var parent in plan.SpecialObjects)
                if (parent.Source.Children.Any(child => child.Kind == "[item]"))
                    parents.Add(parent.ObjectKey, (parent, plan.Room.Map, association++));
        }
        foreach (var ground in PassiveGrounds)
        {
            ground.Content.Validate();
            if (ground.PassiveOrigin is not { } origin || ground.InventoryOrigin is not null || !ground.UsesCollectionLedger ||
                ground.DeathIdentity != initial.Identity || ground.DeathEventId == Guid.Empty ||
                !parents.TryGetValue(ground.Reservation.MonsterKey, out var parent) || parent.Map != ground.Reservation.Map ||
                parent.Parent.Source.SourceOrdinal != origin.ParentOrdinal || parent.Association != origin.AssociationSlot ||
                !parent.Parent.Source.Children.Any(child => child.SourceOrdinal == origin.ActionOrdinal && child.Kind == "[item]" &&
                    child.RawValues.Count == 5 && child.RawValues[0] == origin.GroupIndex) ||
                ground.Reservation.GroundKey is 0 or > ushort.MaxValue || !occupied.Add(ground.Reservation.GroundKey))
                throw new InvalidDataException("特殊物品必须属于原Run父对象、子规则和独立预留key");
            groundItems.Add(ground.Reservation.GroundKey, new(ground, null));
        }
        passiveByMap = PassiveGrounds.GroupBy(ground => ground.Reservation.Map)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray());
    }

    public ImmutableArray<DungeonGroundObject> CurrentPassiveItems()
    {
        var current = SynchronizeRoom();
        return passiveByMap.TryGetValue(current.CurrentMap, out var items)
            ? items.Where(ground => groundItems[ground.Reservation.GroundKey].Status == DungeonGroundStatus.Available).ToImmutableArray()
            : [];
    }

    public void PublishPassiveEntry()
    {
        if (PassiveGrounds.Length != 0 && run.Snapshot.Identity != PassiveGrounds[0].DeathIdentity ||
            run.Phase != SinglePlayerDungeonPhase.Loading || PassiveGrounds.Any(ground =>
                groundItems[ground.Reservation.GroundKey].Status != DungeonGroundStatus.Frozen))
            throw new InvalidOperationException("特殊物品入场结果已失效或重复发布");
        foreach (var ground in PassiveGrounds)
            groundItems[ground.Reservation.GroundKey].Status = DungeonGroundStatus.Available;
    }

    public bool HasReservation(ushort monsterKey) => reservations.ContainsKey(monsterKey);
    internal DungeonGroundState ForPartyParticipant(SinglePlayerDungeonRun participant, IReadOnlyList<DungeonMonsterPlan> route) =>
        new(participant, route, reservations.Values.SelectMany(rows => rows).ToArray(), resourceIdentity,
            PassiveGrounds.Select(ground => ground with { DeathIdentity = participant.Snapshot.Identity }).ToArray());

    public bool HasPendingPickup => pendingPickup is not null;
    public bool HasPendingOperation => HasPendingPickup || pendingInventoryDrop is not null;

    public bool PrepareInventoryDrop(DungeonGroundObject ground)
    {
        var current = SynchronizeRoom();
        if (!CanPickup(current, ground.DeathIdentity) || HasPendingPickup || ground.InventoryOrigin is null || ground.IsGold ||
            ground.DeathEventId == Guid.Empty || ground.Reservation.Map != current.CurrentMap ||
            ground.Reservation.MonsterKey != current.Identity.CharacterId || ground.Reservation.GroundKey is 0 or > ushort.MaxValue)
            return false;
        if (pendingInventoryDrop is not null)
            return ReferenceEquals(pendingInventoryDrop, ground);
        ground.Content.Validate();
        if (!occupiedKeys.Add(ground.Reservation.GroundKey)) return false;
        groundItems.Add(ground.Reservation.GroundKey, new(ground, null));
        pendingInventoryDrop = ground;
        return true;
    }

    public bool ConfirmInventoryDrop(DungeonGroundObject ground)
    {
        var current = SynchronizeRoom();
        if (!ReferenceEquals(pendingInventoryDrop, ground) || !CanPickup(current, ground.DeathIdentity) ||
            !TryOriginal(ground, out var state) || state.Status != DungeonGroundStatus.Frozen ||
            ground.Reservation.Map != current.CurrentMap) return false;
        state.Status = DungeonGroundStatus.Available;
        pendingInventoryDrop = null;
        return true;
    }

    public DungeonGroundSnapshot Snapshot
    {
        get
        {
            var current = SynchronizeRoom();
            return new(current.Identity, current.CurrentMap, groundItems.Values
                .Where(state => state.Ground.Reservation.Map == current.CurrentMap)
                .OrderBy(state => state.Ground.Reservation.GroundKey)
                .Select(state => new DungeonGroundItemSnapshot(state.Ground, state.Status)).ToImmutableArray());
        }
    }

    // 命中、未命中及失败尝试均有界绑定预留怪物；明确回滚后同一死亡事实重试不会再次调用factory。
    public FrozenDungeonGroundDeath GetOrFreeze(PreparedDungeonDeath prepared, Func<ItemCore?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Freeze(prepared, () =>
        {
            ItemCore? core = factory();
            if (core is not null && (core.Kind != ItemKind.Consumable || core.Count != 1 || core.ExpireTime != 0))
                throw new InvalidDataException("本服单件显式池仅接已创建的永久消耗品");
            return core is null ? [] : [new DungeonGroundContent(core)];
        }, false);
    }

    public FrozenDungeonGroundDeath GetOrFreezeMany(PreparedDungeonDeath prepared, Func<IReadOnlyList<ItemCore>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return GetOrFreezeAssets(prepared, () => factory().Select(core => new DungeonGroundContent(core)).ToArray());
    }

    public FrozenDungeonGroundDeath GetOrFreezeAssets(PreparedDungeonDeath prepared, Func<IReadOnlyList<DungeonGroundContent>> factory,
        string? resourceSource = null) => Freeze(prepared, factory, true, resourceSource);

    FrozenDungeonGroundDeath Freeze(PreparedDungeonDeath prepared, Func<IReadOnlyList<DungeonGroundContent>> factory,
        bool usesCollectionLedger, string? resourceSource = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(factory);
        string source = resourceSource ?? resourceIdentity;
        var current = SynchronizeRoom();
        ushort key = prepared.Death.MonsterKey;
        if (!run.IsPendingDeath(prepared) || !reservations.TryGetValue(key, out var reserved) ||
            !ReferenceEquals(monsters[key], prepared.Monster) || reserved[0].Map != current.CurrentMap ||
            prepared.Death.Identity != current.Identity || prepared.Result.Snapshot.CurrentMap != current.CurrentMap)
            throw new InvalidOperationException("地面冻结需要本Run当前精确死亡准备及已预留对象");
        if (frozen.TryGetValue(key, out var existing))
        {
            if (!ReferenceEquals(existing.Frozen.Death, prepared.Death) || current.Phase == SinglePlayerDungeonPhase.Returned ||
                existing.Frozen.UsesCollectionLedger != usesCollectionLedger || existing.Frozen.ResourceIdentity != source)
                throw new InvalidOperationException("同一怪物不能改换死亡事实或复活过期掉落");
            return existing.Frozen;
        }
        if (!failedOrCreating.Add(key))
            throw new InvalidOperationException("地面冻结曾失败或仍在生成，不能重抽");
        var contents = factory()?.ToArray() ?? throw new InvalidDataException("冻结掉落集合为空引用");
        if (contents.Length > reserved.Length)
            throw new InvalidDataException("冻结掉落超出本怪物已持久预留容量，不能截断");
        foreach (var content in contents)
        {
            ArgumentNullException.ThrowIfNull(content);
            content.Validate();
        }
        if (!run.IsPendingDeath(prepared) || run.Snapshot.Identity != current.Identity)
            throw new InvalidOperationException("冻结期间原死亡准备已失效");
        var grounds = contents.Select((content, index) => new DungeonGroundObject(prepared.Death.EventId, prepared.Death.Identity,
            reserved[index], content) { ResourceIdentity = source, UsesCollectionLedger = usesCollectionLedger }).ToImmutableArray();
        var result = new FrozenDungeonGroundDeath(prepared.Death, prepared.Result.Snapshot.Identity, current.CurrentMap, grounds,
            source, usesCollectionLedger);
        var state = new DeathState(result);
        frozen.Add(key, state);
        foreach (var ground in grounds)
            groundItems.Add(ground.Reservation.GroundKey, new(ground, state));
        failedOrCreating.Remove(key);
        return result;
    }

    // 由调用方在明确SQL提交并Run确认后调用；精确候选查询不能被复制Prepared绕过。
    public bool PublishCommitted(PreparedDungeonDeath prepared, FrozenDungeonGroundDeath result)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(result);
        var current = SynchronizeRoom();
        if (!run.IsConfirmedDeath(prepared) || !frozen.TryGetValue(prepared.Death.MonsterKey, out var state) ||
            !ReferenceEquals(state.Frozen, result) || !ReferenceEquals(result.Death, prepared.Death) ||
            state.Committed || result.Grounds.Any(ground => groundItems[ground.Reservation.GroundKey].Status != DungeonGroundStatus.Frozen) || current.CurrentMap != result.Map ||
            current.Identity != result.CommittedIdentity || current.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement))
            return false;
        state.Committed = true;
        foreach (var ground in result.Grounds)
            groundItems[ground.Reservation.GroundKey].Status = DungeonGroundStatus.Available;
        return true;
    }

    public DungeonGroundObject? FindAvailable(DungeonRunIdentity expected, uint groundKey)
    {
        var current = SynchronizeRoom();
        if (!CanPickup(current, expected))
            return null;
        return groundItems.TryGetValue(groundKey, out var state) && state.Committed &&
            state.Status == DungeonGroundStatus.Available && state.Ground.Reservation.Map == current.CurrentMap
            ? state.Ground : null;
    }

    public DungeonGroundPickupPreparation? PreparePickup(DungeonRunIdentity expected, DungeonGroundObject original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var current = SynchronizeRoom();
        if (!CanPickup(current, expected))
            return null;
        if (pendingPickup is not null)
            return pendingPickup.Identity == expected && ReferenceEquals(pendingPickup.Ground, original) ? pendingPickup : null;
        if (pendingInventoryDrop is not null || !TryOriginal(original, out var state) || !state.Committed || state.Status != DungeonGroundStatus.Available ||
            original.Reservation.Map != current.CurrentMap)
            return null;
        pendingPickup = new(expected, original);
        state.Status = DungeonGroundStatus.PickupPending;
        return pendingPickup;
    }

    public bool ConfirmPickup(DungeonGroundPickupPreparation prepared)
    {
        if (!MatchesPickup(prepared, out var state))
            return false;
        state.Status = DungeonGroundStatus.Picked;
        pendingPickup = null;
        return true;
    }

    // 仅供主owner在SQL明确未提交/回滚时撤销；结果不明时保留原准备，不能猜测失败或重发资产。
    public bool CancelPickup(DungeonGroundPickupPreparation prepared)
    {
        if (!MatchesPickup(prepared, out var state))
            return false;
        state.Status = DungeonGroundStatus.Available;
        pendingPickup = null;
        return true;
    }

    // 过房保留原房间的冻结物品，匹配客户端房间缓存；只有整个Run确认退出后才过期。
    // HasPendingPickup仍由Session在Move/Return前核对，不能取消结果不明的资产提交。
    public bool ExpireRun()
    {
        if (HasPendingOperation || run.Phase != SinglePlayerDungeonPhase.Returned)
            return false;
        ExpireUnpicked();
        return true;
    }

    bool MatchesPickup(DungeonGroundPickupPreparation prepared, out ItemState state)
    {
        state = null!;
        var current = SynchronizeRoom();
        return prepared is not null && ReferenceEquals(pendingPickup, prepared) && CanPickup(current, prepared.Identity) &&
            TryOriginal(prepared.Ground, out state) && state.Status == DungeonGroundStatus.PickupPending &&
            prepared.Ground.Reservation.Map == current.CurrentMap;
    }

    bool TryOriginal(DungeonGroundObject ground, out ItemState state)
    {
        state = null!;
        return groundItems.TryGetValue(ground.Reservation.GroundKey, out state!) && ReferenceEquals(state.Ground, ground);
    }

    static bool CanPickup(DungeonRunSnapshot current, DungeonRunIdentity expected) => current.Identity == expected &&
        current.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement;

    DungeonRunSnapshot SynchronizeRoom()
    {
        var current = run.Snapshot;
        if (current.Phase == SinglePlayerDungeonPhase.Returned)
            ExpireUnpicked();
        return current;
    }

    void ExpireUnpicked()
    {
        foreach (var state in groundItems.Values)
        {
            if (state.Status is DungeonGroundStatus.Frozen or DungeonGroundStatus.Available or DungeonGroundStatus.PickupPending)
                state.Status = DungeonGroundStatus.Expired;
        }
    }
}
