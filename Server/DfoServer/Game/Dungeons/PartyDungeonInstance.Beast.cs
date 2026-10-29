using DfoServer.Game.Session;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    internal PartyDungeonNotice[] TickBeastDeadline(GameSession reporter) => Execute(current =>
    {
        if (!current.Any(member => ReferenceEquals(member.Actor.Session, reporter)) || pendingScene is not null) return [];
        var notices = new List<PartyDungeonNotice>();
        // 先恢复已接受的共同击杀，包括某成员SQL失败后尚未完成的原阶段。
        if (pendingDeath is not null) FinishPendingDeath(current, notices);
        var receipts = current.Select(member => member.Actor.Session.CaptureBeastRun()).ToArray();
        if (receipts.Any(receipt => receipt is null) ||
            receipts.Any(receipt => !ReferenceEquals(receipt!.Progress, receipts[0]!.Progress)) ||
            current.Any(member => !member.Actor.Session.CanExpireBeastRun())) return notices.ToArray();
        if (!receipts[0]!.Progress.TryExpire()) return notices.ToArray();
        for (int index = 0; index < current.Length; index++)
        {
            var member = current[index];
            if (!member.Actor.Session.ExpireBeastRun(receipts[index]!))
                throw new InvalidOperationException("共享魔兽超时的角色Run投影不一致");
            notices.Add(new(PartyDungeonNoticeKind.BeastUpdated, member, current, member.Actor.Session.CurrentFormalDungeonScene()));
        }
        return notices.ToArray();
    });
}
