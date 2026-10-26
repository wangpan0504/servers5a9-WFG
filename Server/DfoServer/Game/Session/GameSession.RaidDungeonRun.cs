using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool IsLukeRaidDungeon => owner.Execute(() => formalDungeon?.Entry.RaidEntry?.Kind == RaidKind.Luke);

    // 调用方已持原四人owner组；资源准备完成，SQL尚未开始。
    internal static AntonRaidBattle.DungeonReservation? ReserveRaidPartyRun(PartyDungeonPreparation[] prepared, out bool required)
    {
        required = prepared.Any(token => token.Session.PreparedParty(token).Dungeon.Entry.RaidEntry?.Kind == RaidKind.Luke);
        if (!required || prepared.Length is < 1 or > 4) return null;
        var first = prepared[0].Session.PreparedParty(prepared[0]).Dungeon;
        var admission = prepared[0].Session.raidDungeonAdmission;
        if (first.Entry.RaidEntry is not { Kind: RaidKind.Luke } identity || admission?.Battle is not { } battle) return null;
        var actors = new List<(RaidActor Actor, DungeonRunIdentity Identity)>();
        foreach (var token in prepared)
        {
            var session = token.Session;
            var dungeon = session.PreparedParty(token).Dungeon;
            if (dungeon.Entry.RaidEntry != identity || dungeon.Entry.DungeonId != first.Entry.DungeonId ||
                session.raidDungeonAdmission is not { Kind: RaidKind.Luke } own || !ReferenceEquals(own.Battle, battle) ||
                !ReferenceEquals(session.CurrentRaidBattle(own.Actor), battle) || !session.CanInstallRaidDungeon(dungeon.Entry)) return null;
            actors.Add((own.Actor, dungeon.Run.Snapshot.Identity));
        }
        return battle.ReserveDungeon(identity, first.Entry.DungeonId, actors.ToArray());
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

    AntonRaidBattleChange? ReleaseCurrentRaidDungeon()
    {
        if (formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Luke } dungeon ||
            raidCombat is not { Battle.Kind: RaidKind.Luke } raid) return null;
        return raid.Battle.ReleaseDungeon(raid.Actor, dungeon.Run.Snapshot.Identity.InstanceId);
    }
}
