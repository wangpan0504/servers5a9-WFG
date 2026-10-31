using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 商店目录只证明商品属于626；动态商人的成员、位置与期限在规划及提交前各复核一次。
    bool CanBuyFiendwarMerchant() => Ready && Phase == SessionPhase.Town && ChannelEntry?.Type == 45 &&
        Selected is { Position.Town: 57 } character &&
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Fiendwar } raid &&
        MatchesRaidCombatActor(raid.Actor) && HasCurrentRaid(raid.Actor) &&
        raid.Battle.AllowsFiendwarMerchant(raid.Actor, character.Position.Area);

    internal Town.TownTravelReceipt? ReturnFiendwarLobby(RaidActor actor) => owner.Execute<Town.TownTravelReceipt?>(() =>
    {
        if (!actor.IsCurrent || !ReferenceEquals(actor.Session, this) || HasCurrentRaid(actor) || raidCombat is not null ||
            !CanChangeTownParty || ChannelEntry?.Type != 45 || Selected is not { Position.Town: 57, Position.Area: >= 2 } character)
            return null;
        var target = towns.Spawn(new(57, 1, 0, 0), completed);
        if (target is null) return null;
        var saved = SaveTownPosition(character, target, character.Return);
        if (saved is null) return null;
        Selected = saved;
        return new(saved, owner.Generation, SelectionEpoch);
    });

    internal AntonRaidBattleChange? ReportFiendwarBoss(uint operation, uint value) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Dungeon || raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Fiendwar } raid ||
            formalDungeon is not { } dungeon || dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement ||
            dungeon.Ground.HasPendingOperation || !CanMoveWithParty()) return null;
        return raid.Battle.ReportFiendwarBoss(raid.Actor, dungeon.Run.Snapshot.Identity, dungeon.Run.Snapshot.CurrentMap.Id, operation, value);
    });
}
