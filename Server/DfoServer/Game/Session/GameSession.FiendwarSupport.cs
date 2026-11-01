using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal AntonRaidBattleChange? RequestFiendwarSupport(byte lane) => owner.Execute(() =>
    {
        if (!Ready || raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Fiendwar } raid ||
            raidRegistry?.Find(raid.Actor) is not { } party) return null;
        // 原owner同时保护团长委任及离队；战斗聚合再次核对原实例、成员和三秒门限。
        return raid.Battle.RequestFiendwarSupport(raid.Actor, party, lane);
    });

    internal bool CanPublishFiendwarSupport(FiendwarSupportNotice notice) => owner.Execute(() => Ready &&
        raidCombat is { Actor.IsCurrent: true } raid && raidRegistry?.Find(raid.Actor)?.Started?.InstanceId == notice.Instance &&
        raid.Battle.CanPublishFiendwarSupport(raid.Actor, notice));
}
