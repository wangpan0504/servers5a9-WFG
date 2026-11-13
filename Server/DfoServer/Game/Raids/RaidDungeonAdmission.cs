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
    // 旧调用默认保持单人引导；普通模式由原开战聚合明确签发并随准入冻结。
    public bool PreyGuide { get; init; } = true;
    public ushort? PreyPartyId { get; init; }
    public ushort? PreySoloCharacter { get; init; }
    // 仅供SQL前原Run预留核验；后续已安装Run不随飞鸟离站失效。
    public PreyLandingCandidate? PreyLanding { get; init; }
    public bool Current => Enum.IsDefined(Kind) && Catalog.Type == (byte)Kind && InstanceId != Guid.Empty &&
        (Kind == RaidKind.Prey && !PreyGuide ? Squad == 0 &&
            (PreyPartyId is < ushort.MaxValue && PreySoloCharacter is null || PreyPartyId is null && PreySoloCharacter == Actor.Character) :
            Squad > 0 && PreyPartyId is null && PreySoloCharacter is null) && Actor.IsCurrent &&
        (Kind is RaidKind.Anton or RaidKind.Prey or RaidKind.Fiendwar ? Catalog.Script.Issues.IsEmpty &&
            (Kind != RaidKind.Prey || Battle is not null && Battle.PreyGuide == PreyGuide && (!PreyGuide || Squad == 1)) &&
            (Battle is null || Battle.Kind == Kind && Battle.Instance == InstanceId &&
            Battle.View.Phase == Phase && ReferenceEquals(Actor.Session.CurrentRaidBattle(Actor), Battle) &&
            Actor.Session.StartedRaid?.InstanceId == InstanceId) :
            LukePolicy is not null && Battle is { Kind: RaidKind.Luke } && Battle.Instance == InstanceId) &&
        Phase >= 0 && Phase < Catalog.Script.Phases.Length && IsCurrent();

    public bool Allows(int dungeon) => Current &&
        Catalog.Script.Phases[Phase].Dungeons.Any(row => row.Id == dungeon) && CanEnterDungeon(dungeon);
}

// 仅保存本次实际准入身份；COMBAT可据此关联原攻坚实例，不能用普通副本回执代替团本奖励资格。
internal sealed record RaidDungeonIdentity(Guid InstanceId, int Phase, byte Squad, RaidKind Kind = RaidKind.Anton)
{
    public bool PreyGuide { get; init; } = true;
    // 普通普雷的战斗队伍来自左右空间，不能用名册上的分队标记作为实例身份。
    public ushort? PreyPartyId { get; init; }
    public ushort? PreySoloCharacter { get; init; }
    public bool HasValidGroup => Kind == RaidKind.Prey && !PreyGuide
        ? Squad == 0 && (PreyPartyId is < ushort.MaxValue && PreySoloCharacter is null ||
            PreyPartyId is null && PreySoloCharacter is > 0)
        : PreyPartyId is null && PreySoloCharacter is null && Squad >= 1 && Squad <= (Kind == RaidKind.Fiendwar ? 8 : Kind == RaidKind.Luke ? 2 :
            Kind == RaidKind.Prey ? 1 : 5);
}
