using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool IsFormalRaidDungeon => owner.Execute(() => formalDungeon?.Entry.RaidEntry is not null);
    internal bool IsLukeEnergyDungeon => owner.Execute(() => formalDungeon is
        { Entry.RaidEntry: { Kind: RaidKind.Luke, Phase: 1 }, Entry.LukeEnergyObject.IsEnergy: true });

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
        var replacing = prepared[0].Session.PreparedParty(prepared[0]).AntonHatcherySource?.Snapshot.Identity.InstanceId;
        var actors = new List<(RaidActor Actor, DungeonRunIdentity Identity)>();
        foreach (var token in prepared)
        {
            var session = token.Session;
            var dungeon = session.PreparedParty(token).Dungeon;
            if (session.PreparedParty(token).AntonHatcherySource?.Snapshot.Identity.InstanceId != replacing ||
                dungeon.Entry.RaidEntry != identity || dungeon.Entry.DungeonId != first.Entry.DungeonId ||
                dungeon.Entry.Maze.SourceOrdinal != first.Entry.Maze.SourceOrdinal || session.CurrentDungeonSelection?.Raid?.Route != route ||
                session.raidDungeonAdmission is not { } own || own.Kind != identity.Kind || !ReferenceEquals(own.Battle, battle) ||
                !ReferenceEquals(session.CurrentRaidBattle(own.Actor), battle) || !session.CanInstallRaidDungeon(dungeon.Entry)) return null;
            actors.Add((own.Actor, dungeon.Run.Snapshot.Identity));
        }
        var objects = admission.LukeObjects is { } catalog
            ? new LukeRaidObjectRegistration(first.Run.Snapshot.Identity.InstanceId, checked((int)first.Entry.DungeonId), first.Entry.Maze, catalog) : null;
        if (battle.Kind == RaidKind.Fiendwar && (first.Entry.FiendwarScale is not { } scale ||
            (battle.FiendwarAreaForDungeon(checked((int)first.Entry.DungeonId)) is 30 or 31
                ? actors.Any(actor => !scale.Participants.Contains(actor.Actor.Character))
                : !scale.Participants.ToHashSet().SetEquals(actors.Select(actor => actor.Actor.Character))) ||
            prepared.Any(token => !ReferenceEquals(token.Session.PreparedParty(token).Dungeon.Entry.FiendwarScale, first.Entry.FiendwarScale)))) return null;
        return battle.ReserveDungeon(identity, first.Entry.DungeonId, actors.ToArray(), objects, route,
            first.Entry.Maze.SourceOrdinal, first.Entry.CoinLimit, admission.PreyLanding,
            admission.Catalog.Find(checked((int)first.Entry.DungeonId))?.LimitTimeSeconds, replacing);
    }

    // PartyDungeonInstance在全部成员SQL已确认后调用；即使上次网络前消费中断，也从原Run保留的事实恢复。
    internal static bool CompleteRaidPartyDungeon(PartyMember[] members, Guid instance, out AntonRaidBattleChange? change)
    {
        change = null;
        if (members.Length is < 1 or > 4) return false;
        var first = members[0].Actor.Session;
        if (first.raidCombat is not { Battle.Kind: RaidKind.Anton or RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar } firstRaid) return false;
        var facts = new List<(RaidActor Actor, DungeonClearFact Fact)>();
        foreach (var member in members)
        {
            var session = member.Actor.Session;
            if (session.raidCombat is not { } raid || !ReferenceEquals(raid.Battle, firstRaid.Battle) || !raid.Actor.IsCurrent ||
                session.formalDungeon is not { Run.ClearFact: { } fact } dungeon || dungeon.Run.HasPendingDeath ||
                dungeon.Entry.RaidEntry is not { } entry || entry.Kind != firstRaid.Battle.Kind || entry.InstanceId != raid.Battle.Instance ||
                fact.Identity.InstanceId != instance || fact.Identity != dungeon.Run.Snapshot.Identity ||
                !dungeon.Entry.IsTerminalMap(dungeon.Run.Snapshot.CurrentMap)) return false;
            facts.Add((raid.Actor, fact));
        }
        if (firstRaid.Battle.Kind == RaidKind.Anton)
        {
            change = firstRaid.Battle.RecordAntonPartyClears(facts.ToArray());
            return change is not null;
        }
        change = firstRaid.Battle.Kind == RaidKind.Fiendwar ? firstRaid.Battle.CompleteFiendwarDungeon(facts.ToArray()) : firstRaid.Battle.Kind == RaidKind.Prey
            ? firstRaid.Battle.CompletePreyDungeon(facts.ToArray()) : firstRaid.Battle.CompleteLukeDungeon(facts.ToArray());
        // 网络前中断后只复用同一Run的已提交事件；不重新造事实或重复登记接收者。
        if (change is null && firstRaid.Battle.CaptureLukeRunCleared(instance) is { } cleared)
        {
            var view = firstRaid.Battle.View;
            change = new(view, view) { LukeClear = cleared };
        }
        // 漩涡单侧终杀也会提交安全退场，但不授予整团/副本奖励贡献；仍须发布原Run的退场事实。
        return firstRaid.Battle.Kind == RaidKind.Fiendwar && change is not null ||
            facts.All(row => firstRaid.Battle.HasDungeonContribution(row.Actor, instance));
    }

    // 输入来自原共享实例全部已安装/加载确认的正式投影；不能用客户端地图号直接触发规则。
    internal static PartyDungeonNotice[] ObserveRaidPartyRun(PartyMember[] members, Guid instance,
        PartyDungeonNotice[] notices, bool observeMap)
    {
        if (members.Length is >= 1 and <= 4 && members[0].Actor.Session.raidCombat is { Battle.Kind: RaidKind.Fiendwar } fiendwar)
        {
            var snapshots = members.Select(member => member.Actor.Session.CurrentFormalDungeonScene()).ToArray();
            if (snapshots.Any(scene => scene is null || scene.Snapshot.Identity.InstanceId != instance) ||
                snapshots.Select(scene => scene!.Snapshot.CurrentMap).Distinct().Count() != 1) return notices;
            var fiendwarChange = fiendwar.Battle.ObserveFiendwarRun(instance,
                members.Select(member => member.Actor.Session.raidCombat!.Actor).ToArray(),
                snapshots[0]!.Snapshot.CurrentMap.Id, observeMap);
            return [.. notices, new(PartyDungeonNoticeKind.RaidBattleSync, members[0], members, snapshots[0], RaidBattleChange: fiendwarChange)];
        }
        if (members.Length is < 1 or > 4 || members[0].Actor.Session.raidCombat is not { } first ||
            first.Battle.Kind != RaidKind.Luke && (first.Battle.Kind != RaidKind.Prey || first.Battle.PreyGuide))
            return notices;
        var rows = new List<(RaidActor Actor, DungeonRunIdentity Identity)>();
        DfoServer.GameWorld.Dungeons.DungeonMap? map = null;
        foreach (var member in members)
        {
            var session = member.Actor.Session;
            if (session.raidCombat is not { } raid || !ReferenceEquals(raid.Battle, first.Battle) || !raid.Actor.IsCurrent ||
                session.formalDungeon is not { Entry.RaidEntry: { } raidEntry } dungeon || raidEntry.Kind != first.Battle.Kind ||
                dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance || dungeon.Run.Snapshot.Identity.InstanceId != instance)
                return notices;
            var snapshot = dungeon.Run.Snapshot;
            if (map is not null && map != snapshot.CurrentMap) return notices;
            map = snapshot.CurrentMap;
            rows.Add((raid.Actor, snapshot.Identity));
        }
        var change = first.Battle.Kind == RaidKind.Prey
            ? first.Battle.ObservePreyTeamRun(instance, rows.ToArray(), observeMap ? map!.Id : null)
            : first.Battle.ObserveLukeRun(instance, rows.ToArray(), observeMap ? map!.Id : null, observeMap ? map : null,
                members[0].Actor.Session.formalDungeon!.Run);
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

    GameWorld.Towns.Location? RaidDungeonExitPosition(FormalDungeon dungeon, Characters.Character character)
    {
        // 只在原末阶段资格已提交后改变本次正式退出落点；结果/CG仍等待原Run退出屏障。
        if (dungeon.Entry.RaidEntry is { Kind: RaidKind.Prey, Phase: 1 } entry &&
            raidCombat is { Battle.Kind: RaidKind.Prey } raid &&
            entry.InstanceId == raid.Battle.Instance && entry.Phase + 1 == raid.Battle.PhaseCount &&
            raid.Battle.RequiresLukeCompletedPhaseReturn(raid.Actor, entry.InstanceId, entry.Phase) &&
            (raid.Battle.PreyGuide ? raid.Battle.PreyGuideWaitingRoom : raid.Battle.LukeWaitingRoom) is { } room)
            return towns.Spawn(new(checked((byte)room.Town), checked((byte)room.Area), 0, 0), completed);
        return dungeon.OrdinaryReturnPosition ?? character.Position;
    }
}
