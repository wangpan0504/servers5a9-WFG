using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal Game.Town.TownTravelReceipt? ReturnLukeRejoinToWaiting(RaidActor actor) => owner.Execute(() =>
    {
        if (!actor.IsCurrent || !CanChangeTownParty || raidCombat is not { Battle.Kind: RaidKind.Luke or RaidKind.Fiendwar } combat || combat.Actor != actor ||
            Selected is not { } character || combat.Battle.LukeWaitingRoom is not { } room ||
            !towns.Areas.TryGetValue(((byte)room.Town, (byte)room.Area), out var area)) return null;
        if (combat.Battle.Kind == RaidKind.Fiendwar) room = room with { Area = combat.Battle.FiendwarPosition(actor.Character) };
        if (!towns.Areas.TryGetValue(((byte)room.Town, (byte)room.Area), out area)) return null;
        if (character.Position.Town == room.Town && character.Position.Area == room.Area)
            return new Game.Town.TownTravelReceipt(character, owner.Generation, SelectionEpoch);
        var target = new GameWorld.Towns.Location(area.Town, area.Area, 0, 0);
        if (towns.DestinationAdmission(target, character.Level, completed) != "ready") return null;
        var landing = towns.Entrance(character.Position, target, completed) ?? area.Map(completed).Links
            .Where(link => link.Gate.Door).OrderBy(link => link.Town).ThenBy(link => link.Area)
                .Select(link => towns.Entrance(new(link.Town, link.Area, 0, 0), target, completed)).FirstOrDefault(position => position is not null);
        if (landing is null && combat.Battle.Kind == RaidKind.Fiendwar) landing = towns.Spawn(target, completed);
        return landing is null ? null : CommitTravel(new(landing, null, "ready"));
    });

    // 仅取已发布绑定中的不可变身份提示，不读Selected或授权；调用者必须零等待取得全团owner后复验。
    internal RaidActor? ReadLukeDisconnectActorHint() =>
        Volatile.Read(ref raidCombat) is { Battle.Kind: RaidKind.Luke or RaidKind.Fiendwar } combat ? combat.Actor : null;

    // 调用方已持本owner及全团门禁；只装原Battle身份，不改位置/资产/加载屏障。
    internal bool InstallLukeRejoin(RaidActor actor, AntonRaidBattle battle)
    {
        if (!MatchesRaidCombatActor(actor) || !CanChangeTownParty || raidCombat is not null ||
            raidRegistry is null || battle.Kind is not (RaidKind.Luke or RaidKind.Fiendwar)) return false;
        raidCombat = new(actor, battle);
        return true;
    }
}
