using DfoServer.Game.Session;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    // 团本清图返回：完整名单的选图保留option1；名单不全时降为option2。
    // 95沿现有分队退场事务提交，保留原Battle、阶段资格和未领奖计划。
    internal PartyDungeonNotice[] RaidResultCommand(GameSession leader, byte option) => Execute(current =>
    {
        if (option > 1 || !current.Any(m => m.Slot == Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            current.Any(m => m.Actor.Session.RaidDungeonResultFocus() is null) ||
            pendingDeath is not null || pendingScene is not null) return [];
        bool complete = current.Length == party.Members.Length &&
            current.All(m => party.Members.Any(original => original.Actor == m.Actor));
        // 再挑战先结束全分队原Run；调用方沿正式Select/Enter建立新Run，成功后才回复option0。
        bool select = complete && (option == 1 || option == 0 && current.All(m => m.Actor.Session.CanRetryRaidResultDungeon()));
        var result = PartyDungeonService.Exit(store, current,
            select ? PartyDungeonExitKind.ResultSelection : PartyDungeonExitKind.ResultReturn);
        if (result.Length == 0) return [];
        foreach (var member in current) member.Actor.Session.RemovePartyDungeon(this);
        members = [];
        ClearCards();
        return select ? result.Select(notice => notice with { SceneTransition = true, Option = 1 }).ToArray() : result;
    });
}
