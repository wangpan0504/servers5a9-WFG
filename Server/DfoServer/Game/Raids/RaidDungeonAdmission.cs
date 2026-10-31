using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// START在开战提交后签发；回调只读取原实例，不复制名单、在线状态或阶段状态机。
internal sealed record RaidDungeonAdmission(Guid InstanceId, RaidActor Actor, int Phase, byte Squad,
    RaidDungeonCatalog Catalog, Func<bool> IsCurrent, Func<int, bool> CanEnterDungeon, RaidKind Kind = RaidKind.Anton)
{
    public LukeRaidRunPolicy? LukePolicy { get; init; }
    public LukeRaidObjectCatalog? LukeObjects { get; init; }
    public AntonRaidBattle? Battle { get; init; }
    public FiendwarCombatScale? FiendwarScale { get; init; }
    public bool Current => Enum.IsDefined(Kind) && Catalog.Type == (byte)Kind && InstanceId != Guid.Empty && Squad > 0 && Actor.IsCurrent &&
        (Kind is RaidKind.Anton or RaidKind.Prey or RaidKind.Fiendwar ? Catalog.Script.Issues.IsEmpty && (Kind != RaidKind.Prey || Squad == 1 && Battle is not null) &&
            (Battle is null || Battle.Kind == Kind && Battle.Instance == InstanceId &&
            Battle.View.Phase == Phase && ReferenceEquals(Actor.Session.CurrentRaidBattle(Actor), Battle) &&
            Actor.Session.StartedRaid?.InstanceId == InstanceId) :
            LukePolicy is not null && Battle is { Kind: RaidKind.Luke } && Battle.Instance == InstanceId) &&
        Phase >= 0 && Phase < Catalog.Script.Phases.Length && IsCurrent();

    public bool Allows(int dungeon) => Current &&
        Catalog.Script.Phases[Phase].Dungeons.Any(row => row.Id == dungeon) && CanEnterDungeon(dungeon);
}

// 仅保存本次实际准入身份；COMBAT可据此关联原攻坚实例，不能用普通副本回执代替团本奖励资格。
internal sealed record RaidDungeonIdentity(Guid InstanceId, int Phase, byte Squad, RaidKind Kind = RaidKind.Anton);
