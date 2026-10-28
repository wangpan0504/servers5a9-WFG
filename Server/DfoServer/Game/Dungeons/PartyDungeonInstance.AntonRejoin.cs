using DfoServer.Game.Party;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    internal bool CanRestoreAntonMember()
    {
        lock (gate) return pendingDeath is null && pendingScene is null && members.Length is > 0 and < 4;
    }

    internal bool RestoreAntonMember(PartyMember member, Func<bool> install)
    {
        lock (gate)
        {
            if (!CanRestoreAntonMember() || members.Any(m => m.Slot == member.Slot || m.Actor.Character.Id == member.Actor.Character.Id) ||
                !member.IsCurrent() || !install()) return false;
            // 断线清理可能已向幸存者释放过加载屏障并清空loaded；Dungeon阶段就是其真实确认事实。
            // 新成员仍须自己37，不能要求已在Dungeon的幸存者重新发送只接受Loading的请求。
            if (member.Actor.Session.Phase == Game.Session.SessionPhase.Loading)
                foreach (var survivor in members.Where(m => m.Actor.Session.Phase is Game.Session.SessionPhase.Dungeon or Game.Session.SessionPhase.Settlement))
                    loaded.Add(survivor.Actor.Character.Id);
            members = [..members, member];
            loaded.Remove(member.Actor.Character.Id);
            // 保留原loaded集合及原Run；状态迁移不等于新客户端已确认加载，不自行释放加载屏障。
            return true;
        }
    }
}
