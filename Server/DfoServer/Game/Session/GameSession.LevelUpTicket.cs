using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    readonly Dictionary<int,(long Timestamp,int Duration,TimeProvider Clock)> levelUpCooldowns = new();

    public QuestQuickClearReceipt? UseLevelUpTicket(InventorySlot slot, LevelUpTicketService service, QuestCatalog quests)
    {
        var capture = CaptureManualSkip();
        if (capture.Character is not { } expected) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"升级券资格不符");
        if (capture.Pending is { Plan.LevelUpTicket: { } prior } previous && prior.Item.Position==slot &&
            (previous.CommitStarted || previous.Committed is not null && !previous.TicketAcknowledged))
            return owner.Execute(() =>
            {
                if (!CanPublishQuestQuickClear(receipt) || !ReferenceEquals(pendingQuestQuickClear,previous))
                    throw new InvalidOperationException("升级券恢复的原上下文已变化");
                return CommitManualQuestSkip(previous,receipt,retainAnswered:true);
            });
        if (capture.Pending is { Committed:null }) return receipt with { Reason="原任务跳过尚未完成" };
        QuestQuickClearPlan? plan;
        try
        {
            var source = service.Prepare(expected,slot);
            if (source.Definition.VillageOnly && capture.Phase!=SessionPhase.Town) return receipt;
            if (!QuestQuickClearPlan.TryCreateLevelUpTicket(expected,Guid.NewGuid(),quests,source,
                ImmutableDictionary<int,byte>.Empty,out plan,out string reason,towns)) return receipt with { Reason=reason };
            if (!FreezeQuestEffects(plan!,quests,out string effectsReason)) return receipt with { Reason=effectsReason };
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return receipt with { Reason=error.Message }; }
        return owner.Execute(() =>
        {
            if (!CanPublishQuestQuickClear(receipt) || !ReferenceEquals(pendingQuestQuickClear,capture.Pending) ||
                pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null)
                throw new InvalidOperationException("升级券规划的原角色或场景已变化");
            if (levelUpCooldowns.TryGetValue(plan!.LevelUpTicket!.Item.Core.ItemId,out var previous) &&
                previous.Clock.GetElapsedTime(previous.Timestamp).TotalMilliseconds<previous.Duration)
                return receipt with { Reason="升级券冷却尚未结束" };
            var pending = new PendingQuestQuickClear(plan!,capture.SelectionEpoch,capture.Phase,capture.Identity);
            pendingQuestQuickClear = pending;
            // 1381会建立原生mode2问题上下文；答案返回前不升级、不扣券，也不替玩家选择。
            return plan!.PendingQuestions.IsEmpty ? CommitManualQuestSkip(pending,receipt,retainAnswered:true) :
                receipt with { Prepared=plan,Reason="升级券等待主线分支回答" };
        });
    }

    void RecordLevelUpCooldown(LevelUpTicketSource source)
    {
        foreach(var id in levelUpCooldowns.Where(pair=>pair.Value.Clock.GetElapsedTime(pair.Value.Timestamp).TotalMilliseconds>=pair.Value.Duration)
            .Select(pair=>pair.Key).ToArray()) levelUpCooldowns.Remove(id);
        if (source.Definition.CoolTime>0)
            levelUpCooldowns[source.Item.Core.ItemId]=(source.Clock.GetTimestamp(),source.Definition.CoolTime,source.Clock);
    }
}
