using DfoServer.Game.Session;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    // 86普通结算：完整名单的选图保留option1；名单不全或重试准入失败才降为option2。
    // 95沿现有分队退场事务提交，保留原Battle、阶段资格和未领奖计划。
    internal PartyDungeonNotice[] AntonResultCommand(GameSession leader, byte option) => Execute(current =>
    {
        if (option > 1 || !current.Any(m => m.Slot == Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            current.Any(m => m.Actor.Session.AntonDungeonResultFocus() is null) ||
            pendingDeath is not null || pendingScene is not null) return [];
        bool complete = current.Length == party.Members.Length &&
            current.All(m => party.Members.Any(original => original.Actor == m.Actor));
        // 开放图的直接重试仍缺95新Run/场景衔接；不能以实现缺口冒充准入失败。
        if (option == 0 && complete && leader.CanRetryAntonResultDungeon()) return [];
        bool select = option == 1 && complete;
        var result = PartyDungeonService.Exit(store, current,
            select ? PartyDungeonExitKind.ResultSelection : PartyDungeonExitKind.ResultReturn);
        if (result.Length == 0) return [];
        foreach (var member in current) member.Actor.Session.RemovePartyDungeon(this);
        members = [];
        ClearCards();
        return select ? result.Select(notice => notice with { SceneTransition = true, Option = 1 }).ToArray() : result;
    });
}
