using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 安徒恩单图沿用原生46统计包，但不进入普通副本经验、翻牌或签到结算。
    // 团本副本号（例如黑雾210）不属于普通 dungeon.lst，必须在普通结算入口之前安全消费。
    internal FormalDungeonReceipt? AcceptAntonPlayResult(ushort actorId, byte rankPoint, byte maximumRankPoint,
        IReadOnlyList<ushort> reportMembers) => owner.Execute(() =>
    {
        if (Selected?.Id != actorId || RaidDungeonResultFocus() is not { Entry.RaidEntry.Kind: RaidKind.Anton } receipt ||
            formalDungeon is not { Run.ClearFact: { } fact } dungeon || !IsCurrentRaidDungeon(dungeon.Entry) ||
            raidCombat is not { } raid || !MatchesRaidCombatActor(raid.Actor) ||
            fact.Identity != receipt.Snapshot.Identity || fact.DungeonId != dungeon.Entry.DungeonId ||
            !ValidSettlementReport(dungeon, actorId, rankPoint, maximumRankPoint, reportMembers)) return null;

        if (PartyDungeon is { } party)
        {
            if (party.Id != receipt.Snapshot.Identity.InstanceId || !party.Members.Any(member =>
                member.IsCurrent() && ReferenceEquals(member.Actor.Session, this) &&
                ReferenceEquals(member.Actor.Owner, owner))) return null;
        }
        else if (!dungeon.PartyActors.IsEmpty) return null;

        // 没有资产或状态写入；重复报告只返回同一冻结回执，保持统计包幂等。
        return receipt;
    });

    // 漩涡原生31后仍上传46统计；只接纳原已清Run的报告，不进入普通经验/翻牌结算。
    internal FormalDungeonReceipt? AcceptFiendwarPlayResult(ushort actorId, byte rankPoint, byte maximumRankPoint,
        IReadOnlyList<ushort> reportMembers) => owner.Execute(() =>
    {
        if (Selected?.Id != actorId || RaidDungeonResultFocus() is not { Entry.RaidEntry.Kind: RaidKind.Fiendwar } receipt ||
            formalDungeon is not { Run.ClearFact: { } fact } dungeon || !IsCurrentRaidDungeon(dungeon.Entry) ||
            raidCombat is not { } raid || !MatchesRaidCombatActor(raid.Actor) ||
            fact.Identity != receipt.Snapshot.Identity || fact.DungeonId != dungeon.Entry.DungeonId ||
            !ValidSettlementReport(dungeon, actorId, rankPoint, maximumRankPoint, reportMembers)) return null;

        if (PartyDungeon is { } party)
        {
            if (party.Id != receipt.Snapshot.Identity.InstanceId || !party.Members.Any(member =>
                member.IsCurrent() && ReferenceEquals(member.Actor.Session, this) &&
                ReferenceEquals(member.Actor.Owner, owner))) return null;
        }
        else if (!dungeon.PartyActors.IsEmpty) return null;

        // 没有资产或状态写入，重复统计自然幂等；正文没有Run令牌，不另造历史包识别能力。
        return receipt;
    });
}
