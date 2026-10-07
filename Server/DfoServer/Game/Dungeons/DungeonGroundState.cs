using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonGroundReservation(ushort MonsterKey, uint GroundKey, DungeonMap Map,
    ushort X, ushort Y, short RecordSlot);
internal sealed record DungeonGroundObject(Guid DeathEventId, DungeonRunIdentity DeathIdentity,
    DungeonGroundReservation Reservation, ItemCore Core)
{
    public string ResourceIdentity { get; init; } = "";
}
internal enum DungeonGroundStatus { Frozen, Available, PickupPending, Picked, Expired }
internal sealed record DungeonGroundItemSnapshot(DungeonGroundObject Ground, DungeonGroundStatus Status);
internal sealed record DungeonGroundSnapshot(DungeonRunIdentity Identity, DungeonMap Map,
    ImmutableArray<DungeonGroundItemSnapshot> Items);

internal sealed class FrozenDungeonGroundDeath(ConfirmedDungeonMonsterDeath death, DungeonRunIdentity committedIdentity,
    DungeonMap map, DungeonGroundObject? ground, string resourceIdentity = "")
{
    public ConfirmedDungeonMonsterDeath Death { get; } = death;
    public DungeonRunIdentity Identity => Death.Identity;
    public DungeonRunIdentity CommittedIdentity { get; } = committedIdentity;
    public DungeonMap Map { get; } = map;
    public DungeonGroundObject? Ground { get; } = ground;
    public string ResourceIdentity { get; } = resourceIdentity;
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
        public DungeonGroundStatus Status { get; set; } = DungeonGroundStatus.Frozen;
        public bool Committed { get; set; }
    }

    readonly SinglePlayerDungeonRun run;
    readonly string resourceIdentity;
    readonly Dictionary<ushort, DungeonGroundReservation> reservations;
    readonly Dictionary<ushort, DungeonPlannedMonster> monsters;
    readonly Dictionary<ushort, DeathState> frozen = [];
    readonly HashSet<ushort> failedOrCreating = [];
    DungeonRunSnapshot observed;
    DungeonGroundPickupPreparation? pendingPickup;

    // 预留范围是否已持久化、资源准入和RecordSlot合法常量由主调用方证明；这里只检查完整对象身份与碰撞。
    public DungeonGroundState(SinglePlayerDungeonRun run, IReadOnlyList<DungeonMonsterPlan> routePlans,
        IReadOnlyList<DungeonGroundReservation> reservations, string resourceIdentity = "")
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(routePlans);
        ArgumentNullException.ThrowIfNull(reservations);
        this.run = run;
        this.resourceIdentity = resourceIdentity;
        observed = run.Snapshot;
        monsters = [];
        var maps = new Dictionary<ushort, DungeonMap>();
        var occupied = new HashSet<uint> { observed.Identity.CharacterId };
        foreach (var plan in routePlans)
        {
            foreach (var monster in plan.Monsters)
            {
                if (!occupied.Add(monster.ObjectKey) || !monsters.TryAdd(monster.ObjectKey, monster))
                    throw new InvalidDataException("地面预规划的全路线怪物key重复或碰撞角色");
                maps.Add(monster.ObjectKey, plan.Room.Map);
            }
        }
        this.reservations = [];
        foreach (var reservation in reservations)
        {
            if (!maps.TryGetValue(reservation.MonsterKey, out var map) || map != reservation.Map ||
                reservation.GroundKey == 0 || !occupied.Add(reservation.GroundKey) ||
                !this.reservations.TryAdd(reservation.MonsterKey, reservation))
                throw new InvalidDataException("地面预留key重复、碰撞或不属于该怪物房间");
        }
    }

    public bool HasReservation(ushort monsterKey) => reservations.ContainsKey(monsterKey);
    public bool HasPendingPickup => pendingPickup is not null;

    public DungeonGroundSnapshot Snapshot
    {
        get
        {
            var current = SynchronizeRoom();
            return new(current.Identity, current.CurrentMap, frozen.Values
                .Where(state => state.Frozen.Map == current.CurrentMap && state.Frozen.Ground is not null)
                .OrderBy(state => state.Frozen.Ground!.Reservation.GroundKey)
                .Select(state => new DungeonGroundItemSnapshot(state.Frozen.Ground!, state.Status)).ToImmutableArray());
        }
    }

    // 命中、未命中及失败尝试均有界绑定预留怪物；明确回滚后同一死亡事实重试不会再次调用factory。
    public FrozenDungeonGroundDeath GetOrFreeze(PreparedDungeonDeath prepared, Func<ItemCore?> factory)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(factory);
        var current = SynchronizeRoom();
        ushort key = prepared.Death.MonsterKey;
        if (!run.IsPendingDeath(prepared) || !reservations.TryGetValue(key, out var reservation) ||
            !ReferenceEquals(monsters[key], prepared.Monster) || reservation.Map != current.CurrentMap ||
            prepared.Death.Identity != current.Identity || prepared.Result.Snapshot.CurrentMap != current.CurrentMap)
            throw new InvalidOperationException("地面冻结需要本Run当前精确死亡准备及已预留对象");
        if (frozen.TryGetValue(key, out var existing))
        {
            if (!ReferenceEquals(existing.Frozen.Death, prepared.Death) || existing.Status == DungeonGroundStatus.Expired)
                throw new InvalidOperationException("同一怪物不能改换死亡事实或复活过期掉落");
            return existing.Frozen;
        }
        if (!failedOrCreating.Add(key))
            throw new InvalidOperationException("地面冻结曾失败或仍在生成，不能重抽");
        ItemCore? core = factory();
        if (core is not null)
        {
            core.Validate();
            if (core.Kind != ItemKind.Consumable || core.Count != 1 || core.ExpireTime != 0)
                throw new InvalidDataException("本服地面冻结仅接已创建的单件永久消耗品");
        }
        if (!run.IsPendingDeath(prepared) || run.Snapshot.Identity != current.Identity)
            throw new InvalidOperationException("冻结期间原死亡准备已失效");
        var ground = core is null ? null : new DungeonGroundObject(prepared.Death.EventId, prepared.Death.Identity, reservation, core) { ResourceIdentity = resourceIdentity };
        var result = new FrozenDungeonGroundDeath(prepared.Death, prepared.Result.Snapshot.Identity, current.CurrentMap, ground, resourceIdentity);
        frozen.Add(key, new(result));
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
            state.Committed || state.Status != DungeonGroundStatus.Frozen || current.CurrentMap != result.Map ||
            current.Identity != result.CommittedIdentity || current.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement))
            return false;
        state.Committed = true;
        state.Status = DungeonGroundStatus.Available;
        return true;
    }

    public DungeonGroundObject? FindAvailable(DungeonRunIdentity expected, uint groundKey)
    {
        var current = SynchronizeRoom();
        if (!CanPickup(current, expected))
            return null;
        return frozen.Values.FirstOrDefault(state => state.Committed && state.Status == DungeonGroundStatus.Available &&
            state.Frozen.Map == current.CurrentMap && state.Frozen.Ground?.Reservation.GroundKey == groundKey)?.Frozen.Ground;
    }

    public DungeonGroundPickupPreparation? PreparePickup(DungeonRunIdentity expected, DungeonGroundObject original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var current = SynchronizeRoom();
        if (!CanPickup(current, expected))
            return null;
        if (pendingPickup is not null)
            return pendingPickup.Identity == expected && ReferenceEquals(pendingPickup.Ground, original) ? pendingPickup : null;
        if (!TryOriginal(original, out var state) || !state.Committed || state.Status != DungeonGroundStatus.Available ||
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

    // HasPendingPickup在Run.Move/Return前核对；仅在确认退出本房后显式过期，拒绝过房不得调用此方法。
    // Run已经切房/退出时也会自动过期；本对象不能拦截绕过Session的直接Run调用。
    public bool ExpireRoom()
    {
        if (HasPendingPickup)
            return false;
        ExpireUnpicked();
        return true;
    }

    bool MatchesPickup(DungeonGroundPickupPreparation prepared, out DeathState state)
    {
        state = null!;
        var current = SynchronizeRoom();
        return prepared is not null && ReferenceEquals(pendingPickup, prepared) && CanPickup(current, prepared.Identity) &&
            TryOriginal(prepared.Ground, out state) && state.Status == DungeonGroundStatus.PickupPending &&
            prepared.Ground.Reservation.Map == current.CurrentMap;
    }

    bool TryOriginal(DungeonGroundObject ground, out DeathState state)
    {
        state = null!;
        return frozen.TryGetValue(ground.Reservation.MonsterKey, out state!) && ReferenceEquals(state.Frozen.Ground, ground);
    }

    static bool CanPickup(DungeonRunSnapshot current, DungeonRunIdentity expected) => current.Identity == expected &&
        current.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement;

    DungeonRunSnapshot SynchronizeRoom()
    {
        var current = run.Snapshot;
        bool sameRoomSettlement = observed.Phase == SinglePlayerDungeonPhase.Dungeon && current.Phase == SinglePlayerDungeonPhase.Settlement &&
            current.CurrentMap == observed.CurrentMap && current.Identity == (observed.Identity with { Epoch = observed.Identity.Epoch + 1 });
        if (current.CurrentMap != observed.CurrentMap || current.Phase is SinglePlayerDungeonPhase.Loading or SinglePlayerDungeonPhase.Returned ||
            current.Identity != observed.Identity && !sameRoomSettlement)
            ExpireUnpicked();
        observed = current;
        return current;
    }

    void ExpireUnpicked()
    {
        foreach (var state in frozen.Values)
        {
            if (state.Status is DungeonGroundStatus.Frozen or DungeonGroundStatus.Available or DungeonGroundStatus.PickupPending)
                state.Status = DungeonGroundStatus.Expired;
        }
    }
}
