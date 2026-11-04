using DfoServer.Game.Party;
using DfoServer.Game.Session;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    static PartyDungeonNotice AntonRejoinLoadedNotice(PartyMember member, PartyMember[] current, FormalDungeonReceipt scene) =>
        new(PartyDungeonNoticeKind.Loaded, member, current, scene)
        {
            AntonRejoinDeaths = [.. current.Select(m => m.Actor.Session.CaptureAntonRejoinDeadMember(scene)).OfType<ushort>()]
        };

    // null继续原全员屏障；空集合代表重入呈现身份已失效，不能误入普通业务加载。
    PartyDungeonNotice[]? TryCompleteAntonRejoinLoading(GameSession reporter, PartyMember[] current)
    {
        var member = current.SingleOrDefault(m => ReferenceEquals(m.Actor.Session, reporter));
        if (member is null) return null;
        if (reporter.HasAntonRejoinPresentation)
        {
            if (!reporter.AntonRejoinSceneWritten) return [];
            if (reporter.Phase == SessionPhase.Loading && current.Any(m => !ReferenceEquals(m.Actor.Session, reporter) &&
                m.Actor.Session.Phase == SessionPhase.Loading)) return null;
            bool businessLoading = reporter.Phase == SessionPhase.Loading;
            var scene = reporter.CompleteAntonRejoinPresentation();
            if (scene is null) return [];
            PartyDungeonNotice[] notices = [AntonRejoinLoadedNotice(member, current, scene)];
            if (!businessLoading) return notices;
            loaded.Clear();
            return GameSession.ObserveRaidPartyRun(current, Id, notices, observeMap: true);
        }
        return null;
    }

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
            {
                foreach (var survivor in members.Where(m => m.Actor.Session.Phase is Game.Session.SessionPhase.Dungeon or Game.Session.SessionPhase.Settlement))
                    loaded.Add(survivor.Actor.Character.Id);
            }
            members = [..members, member];
            loaded.Remove(member.Actor.Character.Id);
            // 保留原loaded集合及原Run；状态迁移不等于新客户端已确认加载，不自行释放加载屏障。
            return true;
        }
    }
}
