using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool IsFormalRaidDungeon => owner.Execute(() => formalDungeon?.Entry.RaidEntry is not null);

    // 调用方已持原四人owner组；资源准备完成，SQL尚未开始。
    internal static AntonRaidBattle.DungeonReservation? ReserveRaidPartyRun(PartyDungeonPreparation[] prepared, out bool required)
    {
        required = prepared.Any(token => token.Session.PreparedParty(token).Dungeon.Entry.RaidEntry?.Kind == RaidKind.Luke ||
            token.Session.raidDungeonAdmission?.Battle is not null);
        if (!required || prepared.Length is < 1 or > 4) return null;
        var first = prepared[0].Session.PreparedParty(prepared[0]).Dungeon;
        var admission = prepared[0].Session.raidDungeonAdmission;
        if (first.Entry.RaidEntry is not { } identity || admission?.Battle is not { } battle) return null;
        var route = prepared[0].Session.CurrentDungeonSelection?.Raid?.Route;
        var actors = new List<(RaidActor Actor, DungeonRunIdentity Identity)>();
        foreach (var token in prepared)
        {
            var session = token.Session;
            var dungeon = session.PreparedParty(token).Dungeon;
            if (dungeon.Entry.RaidEntry != identity || dungeon.Entry.DungeonId != first.Entry.DungeonId ||
                dungeon.Entry.Maze.SourceOrdinal != first.Entry.Maze.SourceOrdinal || session.CurrentDungeonSelection?.Raid?.Route != route ||
                session.raidDungeonAdmission is not { } own || own.Kind != identity.Kind || !ReferenceEquals(own.Battle, battle) ||
                !ReferenceEquals(session.CurrentRaidBattle(own.Actor), battle) || !session.CanInstallRaidDungeon(dungeon.Entry)) return null;
            actors.Add((own.Actor, dungeon.Run.Snapshot.Identity));
        }
        var objects = admission.LukeObjects is { } catalog
            ? new LukeRaidObjectRegistration(first.Run.Snapshot.Identity.InstanceId, checked((int)first.Entry.DungeonId), first.Entry.Maze, catalog) : null;
        return battle.ReserveDungeon(identity, first.Entry.DungeonId, actors.ToArray(), objects, route, first.Entry.Maze.SourceOrdinal);
    }

    // PartyDungeonInstance在全部成员SQL已确认后调用；即使上次网络前消费中断，也从原Run保留的事实恢复。
    internal static bool CompleteRaidPartyDungeon(PartyMember[] members, Guid instance, out AntonRaidBattleChange? change)
    {
        change = null;
        if (members.Length is < 1 or > 4) return false;
        var first = members[0].Actor.Session;
        if (first.raidCombat is not { Battle.Kind: RaidKind.Luke } firstRaid) return false;
        var facts = new List<(RaidActor Actor, DungeonClearFact Fact)>();
        foreach (var member in members)
        {
            var session = member.Actor.Session;
            if (session.raidCombat is not { } raid || !ReferenceEquals(raid.Battle, firstRaid.Battle) || !raid.Actor.IsCurrent ||
                session.formalDungeon is not { Run.ClearFact: { } fact } dungeon || dungeon.Run.HasPendingDeath ||
                dungeon.Entry.RaidEntry is not { Kind: RaidKind.Luke } entry || entry.InstanceId != raid.Battle.Instance ||
                fact.Identity.InstanceId != instance || fact.Identity != dungeon.Run.Snapshot.Identity ||
                !dungeon.Entry.IsTerminalMap(dungeon.Run.Snapshot.CurrentMap)) return false;
            facts.Add((raid.Actor, fact));
        }
        change = firstRaid.Battle.CompleteLukeDungeon(facts.ToArray());
        return facts.All(row => firstRaid.Battle.HasDungeonContribution(row.Actor, instance));
    }

    // 输入来自原共享实例全部已安装/加载确认的正式投影；不能用客户端地图号直接触发规则。
    internal static PartyDungeonNotice[] ObserveRaidPartyRun(PartyMember[] members, Guid instance,
        PartyDungeonNotice[] notices, bool observeMap)
    {
        if (members.Length is < 1 or > 4 || members[0].Actor.Session.raidCombat is not { Battle.Kind: RaidKind.Luke } first)
            return notices;
        var rows = new List<(RaidActor Actor, DungeonRunIdentity Identity)>();
        DfoServer.GameWorld.Dungeons.DungeonMap? map = null;
        foreach (var member in members)
        {
            var session = member.Actor.Session;
            if (session.raidCombat is not { } raid || !ReferenceEquals(raid.Battle, first.Battle) || !raid.Actor.IsCurrent ||
                session.formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Luke } dungeon ||
                dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance || dungeon.Run.Snapshot.Identity.InstanceId != instance)
                return notices;
            var snapshot = dungeon.Run.Snapshot;
            if (map is not null && map != snapshot.CurrentMap) return notices;
            map = snapshot.CurrentMap;
            rows.Add((raid.Actor, snapshot.Identity));
        }
        var change = first.Battle.ObserveLukeRun(instance, rows.ToArray(), observeMap ? map!.Id : null, observeMap ? map : null);
        // 通知仍由原发布队列在owner锁外分发；重放时允许以当前view恢复丢失投影。
        return [.. notices, new(PartyDungeonNoticeKind.RaidBattleSync, members[0], members,
            members[0].Actor.Session.CurrentFormalDungeonScene(), RaidBattleChange: change,
            RaidEvent: observeMap ? RaidStageFactKind.EnterMap : RaidStageFactKind.EnterDungeon)];
    }

    AntonRaidBattleChange? ReleaseCurrentRaidDungeon()
    {
        if (formalDungeon is not { Entry.RaidEntry: not null } dungeon || raidCombat is not { } raid) return null;
        return raid.Battle.ReleaseDungeon(raid.Actor, dungeon.Run.Snapshot.Identity.InstanceId);
    }
}
