using DfoServer.Game.Characters;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 原生46只收尾本人的当前已清图Run；资格仍由正式死亡事实与Battle确认，不由评分请求签发。
    internal Guid? ClearedPreyTeamResultRun() => owner.Execute<Guid?>(() =>
    {
        if (!Ready || raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Prey, Run.ClearFact: { } fact } dungeon ||
            dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance ||
            dungeon.Run.Snapshot.Phase != Dungeons.SinglePlayerDungeonPhase.Settlement ||
            fact.Identity != dungeon.Run.Snapshot.Identity ||
            !raid.Battle.HasDungeonContribution(raid.Actor, fact.Identity.InstanceId)) return null;
        return fact.Identity.InstanceId;
    });

    // 失败保留原攻坚与标记，只将已经退出原Run/选图的本人送回资源声明的备战区。
    internal Town.TownTravelReceipt? ReturnFailedPreyWaiting(RaidActor actor, Guid instance) => owner.Execute<Town.TownTravelReceipt?>(() =>
    {
        if (!actor.IsCurrent || !ReferenceEquals(actor.Session, this) || !ReferenceEquals(actor.Owner, owner) ||
            !CanChangeTownParty || formalDungeon is not null || PartyDungeon is not null || ChannelEntry?.Type != 50 ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            raid.Actor != actor || raid.Battle.Instance != instance || !raid.Battle.View.PhaseExpired ||
            raidRegistry?.Find(actor) is not { Type: 3, Guide: false, Started: { } started } || started.InstanceId != instance ||
            raid.Battle.LukeWaitingRoom is not { } room || Selected is not { } character) return null;
        var target = towns.Spawn(new(checked((byte)room.Town), checked((byte)room.Area), 0, 0), completed);
        if (target is null) return null;
        var saved = character.Position.Town == room.Town && character.Position.Area == room.Area
            ? character : SaveTownPosition(character, target, character.Return);
        if (saved is null) return null;
        Selected = saved;
        return new Town.TownTravelReceipt(saved, owner.Generation, SelectionEpoch) { RaidBattleChange = RecordPreyTownLocation() };
    });

    // 离团先提交公共落点；调用方随后才解除原攻坚与队伍绑定，SQL失败仍可重试原请求。
    internal Town.TownTravelReceipt? ReturnPreyLobby(RaidActor actor, RaidParty expected) => owner.Execute<Town.TownTravelReceipt?>(() =>
    {
        if (!actor.IsCurrent || !ReferenceEquals(actor.Session, this) || !ReferenceEquals(actor.Owner, owner) ||
            !CanChangeTownParty || ChannelEntry?.Type != 50 || expected is not { Type: 3, Guide: false } ||
            raidRegistry?.Find(actor) is not { Type: 3, Guide: false } current || current.Key != expected.Key ||
            current.Started?.InstanceId != expected.Started?.InstanceId ||
            Selected is not { Position.Town: 59, Position.Area: >= 2 and <= 5 } character) return null;
        if (expected.Started is { } started && (raidCombat is not
            { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            raid.Battle.Instance != started.InstanceId)) return null;
        var target = ChannelAreaLanding.RoomExit(towns, ChannelEntry, towns.Room, character.Level, completed);
        if (target is null) return null;
        var saved = SaveTownPosition(character, target, character.Return);
        if (saved is null) return null;
        Selected = saved;
        RecordPreyBoardingArrival();
        return new(saved, owner.Generation, SelectionEpoch);
    });

    internal AntonRaidBattleChange? SetPreyMarker(byte group, int target) => owner.Execute(() =>
    {
        if (!Ready || ChannelEntry?.Type != 50 || raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey } raid ||
            raid.Battle.PreyGuide || raidRegistry?.FindCurrent(this, SelectionEpoch) is not { Type: 3, Guide: false, Leader: var leader } party ||
            Selected?.Id != leader || party.Started?.InstanceId != raid.Battle.Instance) return null;
        return raid.Battle.SetPreyMarker(raid.Actor, group, target);
    });
    bool HasPreyTeamTownAccess => ChannelEntry?.Type == 50 &&
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid &&
        raid.Battle.View is { PhaseExpired: false, ClockFaulted: false } view &&
        view.Symbols.GetValueOrDefault("[prepare raid]") == 1 &&
        raidRegistry?.FindCurrent(this, SelectionEpoch) is { Type: 3, Guide: false, Started: { } started } &&
        started.InstanceId == raid.Battle.Instance;

    // 1614只设置本人当前攻坚的报告阈值，不构造伤害、死亡或通关事实。
    internal bool SetPreyBossHpAlarm(int percent) => owner.Execute(() =>
    {
        if (percent is < -1 or > 100 || !Ready || raidCombat is not
            { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            raid.Battle.View is not { State: AntonRaidBattleState.Fighting, PhaseExpired: false, ClockFaulted: false }) return false;
        var view = raid.Battle.View;
        return view.Symbols.ContainsKey("[hp alarm]") &&
            raid.Battle.SetMechanism(view.Instance, view.Revision, "[hp alarm]", percent) is not null;
    });

    internal (RaidActor Actor, AntonRaidBattle Battle, AntonRaidBattleChange Change)? ReportPreyBossHp(int percent) =>
        owner.Execute<(RaidActor, AntonRaidBattle, AntonRaidBattleChange)?>(() =>
        {
            if (percent is < 0 or > 100 || !Ready || Phase != SessionPhase.Dungeon || raidCombat is not
                { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
                formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Prey } dungeon ||
                dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance || !CanMoveWithParty()) return null;
            var change = raid.Battle.ReportPreyTeamBossPercent(raid.Actor, dungeon.Run.Snapshot, percent);
            return change is null ? null : (raid.Actor, raid.Battle, change);
        });

    // 原本体每两秒上报当前HP/最大HP的一万分比，原Run仍负责最终死亡与清图。
    internal (RaidActor Actor, AntonRaidBattle Battle, AntonRaidBattleChange Change)? ReportPreyBossHpUnits(int value) =>
        owner.Execute<(RaidActor, AntonRaidBattle, AntonRaidBattleChange)?>(() =>
        {
            if (value is < 0 or > 10000 || !Ready || Phase != SessionPhase.Dungeon || raidCombat is not
                { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
                formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Prey } dungeon ||
                dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance || !CanMoveWithParty()) return null;
            var change = raid.Battle.ReportPreyTeamBossValue(raid.Actor, dungeon.Run.Snapshot, value);
            return change is null ? null : (raid.Actor, raid.Battle, change);
        });
}
