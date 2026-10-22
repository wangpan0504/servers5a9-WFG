using DfoServer.Game.Characters;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed record QuestQuickClearReceipt(Character Character, QuestQuickClearCommit? Commit, long Generation,
    long SelectionEpoch, SessionPhase Phase, DungeonRunIdentity? Identity, string Reason)
{
    public QuestQuickClearPlan? Prepared { get; init; }
    public bool TicketAcknowledged { get; init; }
}

internal sealed partial class GameSession
{
    sealed record PendingQuestQuickClear(QuestQuickClearPlan Plan, long SelectionEpoch, SessionPhase Phase, DungeonRunIdentity? Identity,
        ImmutableArray<QuestSkipAnswer> AnsweredBatch = default, bool CommitStarted = false, QuestQuickClearCommit? Committed = null,
        bool TicketAcknowledged = false);
    PendingQuestQuickClear? pendingQuestQuickClear;
    // 券回执已提交后只用于重复请求重放与ACK，不再排斥其它业务；只有提交进行中或尚未提交的规划独占当前快照。
    bool HasUncommittedQuestQuickClear => pendingQuestQuickClear is { Committed: null };

    public QuestQuickClearReceipt? QuickClearQuest(uint parentKey, QuestCatalog quests, QuestConnectionCatalog connections)
    {
        var capture = owner.Execute(() =>
        {
            Character? current = Ready && Selected is not null && Phase is SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement &&
                pendingRental is null && pendingCommunication is null && pendingQuestCompletion is null && pendingQuestLifecycle is null && !HasPendingQuestScene &&
                !HasPendingFormalCard && formalDungeon?.Ground.HasPendingOperation != true && formalDungeon?.Run.HasPendingDeath != true &&
                formalDungeon?.Run.HasPendingSettlement != true ? Selected : null;
            // 新的明确1714可替换尚未提交的手册或已安装缓存；仅提交结果待恢复时保留互斥。
            if (current is not null && pendingQuestQuickClear is { Plan.IsPreparedSkip: true, CommitStarted: false })
                pendingQuestQuickClear = null;
            return (Character: current, SelectionEpoch, Phase, Identity: formalDungeon?.Run.Snapshot.Identity, Pending: pendingQuestQuickClear);
        });
        if (capture.Character is not { } expected) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"问题及跳过资格不符");
        QuestQuickClearPlan? plan;
        if (capture.Pending is { } pending)
        {
            if (pending.Plan.IsPreparedSkip || pending.Plan.Unit.ParentKey != parentKey || !ReferenceEquals(pending.Plan.Expected,expected) ||
                pending.SelectionEpoch != capture.SelectionEpoch || pending.Phase != capture.Phase || pending.Identity != capture.Identity)
                return receipt with { Reason = "前一跳过事务尚待恢复" };
            plan = pending.Plan;
        }
        else if (!QuestQuickClearPlan.TryCreate(expected,parentKey,quests,connections,out plan,out string reason,towns))
            return receipt with { Reason = reason };
        if(capture.Pending is null && !FreezeQuestEffects(plan!,quests,out string effectReason)) return receipt with { Reason=effectReason };
        return owner.Execute(() =>
        {
            if (pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null || !CanPublishQuestQuickClear(receipt) ||
                !ReferenceEquals(pendingQuestQuickClear,capture.Pending)) throw new InvalidOperationException("跳过规划的原选角或场景已失效");
            pendingQuestQuickClear = new(plan!,capture.SelectionEpoch,capture.Phase,capture.Identity,CommitStarted:true);
            var commit = repository.CommitQuestQuickClear(owner,plan!);
            if (!commit.ProjectionCurrent) throw new InvalidOperationException("跳过已提交，但原角色状态已变化，需要重连恢复");
            Selected = commit.Character;
            completed = QuestEligibility.Completed(Selected);
            pendingQuestQuickClear = null;
            return receipt with { Character = commit.Character, Commit = commit, Reason = "无奖励跳过已提交" };
        });
    }

    public QuestQuickClearReceipt? PrepareManualQuestSkip(QuestCatalog quests)
    {
        var capture = CaptureManualSkip();
        if (capture.Character is not { } expected) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"手册资格不符");
        QuestQuickClearPlan? plan;
        if (capture.Pending is { } pending)
        {
            if (!pending.Plan.IsManual || !SameManualPending(pending,expected,capture.SelectionEpoch,capture.Phase,capture.Identity))
                return receipt with { Reason = "前一跳过事务尚待恢复" };
            plan = pending.Plan;
        }
        else if (!QuestQuickClearPlan.TryCreateManual(expected,Guid.NewGuid(),quests,ImmutableDictionary<int,byte>.Empty,out plan,out string reason,towns))
            return receipt with { Reason = reason };
        if(capture.Pending is null && !FreezeQuestEffects(plan!,quests,out string effectReason)) return receipt with { Reason=effectReason };
        return owner.Execute(() =>
        {
            if (pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null || !CanPublishQuestQuickClear(receipt) ||
                !ReferenceEquals(pendingQuestQuickClear,capture.Pending)) throw new InvalidOperationException("手册规划的原选角或场景已失效");
            pendingQuestQuickClear = capture.Pending ?? new(plan!,capture.SelectionEpoch,capture.Phase,capture.Identity);
            return receipt with { Prepared = plan, Reason = capture.Pending?.Committed is not null ? "手册已提交，保留同一回答恢复上下文" :
                plan!.PendingQuestions.Length == 0 ? "等待手册1457最终动作" : "等待手册问题回答" };
        });
    }

    public QuestQuickClearReceipt? PrepareQuestTicket(uint templateId,QuestCatalog quests,ItemCatalog items,SkillLearningCatalog? skills=null)
    {
        var capture = CaptureManualSkip();
        if (capture.Character is not { } expected) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"任务券资格不符");
        QuestQuickClearPlan? plan = null;
        var pending = capture.Pending;
        if (pending is not null && (pending.CommitStarted || pending.Plan.IsTicket && pending.Committed is null &&
            pending.Plan.Ticket!.Item.Core.ItemId == templateId))
        {
            if (!pending.Plan.IsTicket || pending.Plan.Ticket!.Item.Core.ItemId != templateId ||
                !SameManualPending(pending,expected,capture.SelectionEpoch,capture.Phase,capture.Identity))
                return receipt with { Reason = "前一跳过事务尚待恢复" };
            plan = pending.Plan;
        }
        else
        {
            try
            {
                if (templateId > int.MaxValue) return receipt;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var item = expected.Inventory.Items.Where(row => row.Position.List == InventoryListType.Main && row.Core.ItemId == templateId)
                    .OrderBy(row => row.Position.Index).FirstOrDefault(row => row.Uid > 0 && row.Core.Kind == ItemKind.Consumable &&
                        row.Core.Count > 0 && row.Core.EquipmentLockId == 0 && !row.Core.IsExpired(now));
                if (item is null) return receipt;
                var source = QuestTicketSource.Create(expected,item.Position,items.QuestTicket((int)templateId),now);
                if (source.Definition.VillageOnly && capture.Phase!=SessionPhase.Town)
                    return receipt with {Reason="实际觉醒券声明仅在城镇使用"};
                if (!QuestQuickClearPlan.TryCreateTicket(expected,Guid.NewGuid(),quests,source,ImmutableDictionary<int,byte>.Empty,out plan,out string reason,skills,towns))
                    return receipt with { Reason = reason };
                if(!FreezeQuestEffects(plan!,quests,out string effectReason)) return receipt with { Reason=effectReason };
                pending = null;
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
            { return receipt with { Reason = error.Message }; }
        }
        return owner.Execute(() =>
        {
            if (pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null || !CanPublishQuestQuickClear(receipt) || !ReferenceEquals(pendingQuestQuickClear,capture.Pending))
                throw new InvalidOperationException("任务券准备的原快照已失效");
            pendingQuestQuickClear = pending ?? new(plan!,capture.SelectionEpoch,capture.Phase,capture.Identity);
            return receipt with { Prepared = plan, Reason = plan!.PendingQuestions.Length == 0 ? "任务券等待真实515消费" : "任务券等待真实问题回答" };
        });
    }

    public QuestQuickClearReceipt? UseQuestTicket(ushort slot,byte list,QuestCatalog quests,ItemCatalog items,
        uint action=QuestTicketDefinition.Action,SkillLearningCatalog? skills=null)
    {
        var capture = CaptureManualSkip();
        if (capture.Character is not { } expected) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"没有可用任务券");
        var pending = capture.Pending;
        // 已确认消费的回执只恢复同一源槽/动作；下一张合法券可替换它，规划失败仍保留原回执。
        if (pending is { Committed: not null, TicketAcknowledged: true } &&
            (pending.Plan.Ticket is not { } prior || prior.Item.Position.List != (InventoryListType)list ||
                prior.Item.Position.Index != slot || prior.Definition.ActionIndex != action))
            pending = null;
        if (pending is not null)
        {
            if (!pending.Plan.IsTicket || !SameManualPending(pending,expected,capture.SelectionEpoch,capture.Phase,capture.Identity) ||
                pending.Plan.PendingQuestions.Length != 0 || pending.Plan.Ticket!.Definition.ActionIndex != action) return receipt;
            if (pending.Plan.Ticket.Definition.VillageOnly && capture.Phase!=SessionPhase.Town)
                return receipt with {Reason="实际觉醒券声明仅在城镇使用"};
            if (pending.CommitStarted || pending.Committed is not null)
            {
                if (pending.Plan.Ticket!.Item.Position.List != (InventoryListType)list || pending.Plan.Ticket.Item.Position.Index != slot) return receipt;
            }
            else
            {
                try
                {
                    if (slot > short.MaxValue || list != (byte)InventoryListType.Main) return receipt;
                    var position = new InventorySlot((InventoryListType)list,(short)slot);
                    // 1430只带template；首次真实515才冻结所报slot的实例，不消费准备时借用的另一张。
                    if (expected.Inventory.Find(position) is not { } item || item.Core.ItemId != pending.Plan.Ticket!.Definition.TemplateId) return receipt;
                    var source = QuestTicketSource.Create(expected,position,pending.Plan.Ticket.Definition,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (!QuestQuickClearPlan.TryCreateTicket(expected,pending.Plan.OperationId,quests,source,pending.Plan.ManualAnswers,out var plan,out string reason,skills,towns))
                        return receipt with { Reason = reason };
                    if(!FreezeQuestEffects(plan!,quests,out string effectReason)) return receipt with { Reason=effectReason };
                    if (!plan!.ScopeQuestIds.SequenceEqual(pending.Plan.ScopeQuestIds) || !plan.PendingQuestions.IsEmpty ||
                        !QuestCycle.SameAnswers(plan.AnswersAfter,pending.Plan.AnswersAfter) ||
                        plan.EquipmentSlotFlagsAfter!=pending.Plan.EquipmentSlotFlagsAfter ||
                        plan.ProfessionGrowType!=pending.Plan.ProfessionGrowType)
                        return receipt with { Reason = "任务券原准备范围或答案已变化" };
                    pending = pending with { Plan = plan };
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
                { return receipt with { Reason = error.Message }; }
            }
        }
        else
        {
            try
            {
                if (slot > short.MaxValue || list != (byte)InventoryListType.Main) return receipt;
                var position = new InventorySlot((InventoryListType)list,(short)slot);
                var item = expected.Inventory.Find(position);
                if (item is null) return receipt;
                var source = QuestTicketSource.Create(expected,position,items.QuestTicket(item.Core.ItemId),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (source.Definition.ActionIndex!=action) return receipt with { Reason="515动作与实际券声明不符" };
                if (source.Definition.VillageOnly && capture.Phase!=SessionPhase.Town)
                    return receipt with {Reason="实际觉醒券声明仅在城镇使用"};
                if (!QuestQuickClearPlan.TryCreateTicket(expected,Guid.NewGuid(),quests,source,ImmutableDictionary<int,byte>.Empty,out var plan,out string reason,skills,towns))
                    return receipt with { Reason = reason };
                if(!FreezeQuestEffects(plan!,quests,out string effectReason)) return receipt with { Reason=effectReason };
                if (plan!.PendingQuestions.Length != 0) return receipt with { Reason = "任务券问题尚未准备回答" };
                pending = new(plan,capture.SelectionEpoch,capture.Phase,capture.Identity);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
            { return receipt with { Reason = error.Message }; }
        }
        return owner.Execute(() =>
        {
            if (pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null || !CanPublishQuestQuickClear(receipt) || !ReferenceEquals(pendingQuestQuickClear,capture.Pending))
                throw new InvalidOperationException("任务券消费的原快照已失效");
            if (pending.Committed is { } installed)
                return receipt with { Commit = installed with { Duplicate = true }, TicketAcknowledged = pending.TicketAcknowledged };
            pendingQuestQuickClear = pending with { CommitStarted = true };
            QuestQuickClearCommit commit;
            try { commit = repository.CommitQuestQuickClear(owner,pending.Plan); }
            catch (QuestTicketExpiredException error)
            {
                // 仓储仅在查无已提交回执且尚未写入时报告到期；SQL/未知返回故障仍保留CommitStarted恢复。
                pendingQuestQuickClear = pending with { CommitStarted = false };
                return receipt with { Reason = error.Message };
            }
            if (!commit.ProjectionCurrent) throw new InvalidOperationException("任务券已提交但原角色已变化，需要重连恢复");
            Selected = commit.Character; completed = QuestEligibility.Completed(Selected);
            pendingQuestQuickClear = pending with { Committed = commit, CommitStarted = false };
            return receipt with { Character = commit.Character, Commit = commit, Reason = "任务券无普通奖励完成及消费已提交" };
        });
    }

    public void MarkQuestTicketAcknowledged(QuestQuickClearReceipt receipt) => owner.Execute(() =>
    {
        if (CanPublishQuestQuickClear(receipt) && pendingQuestQuickClear is { Committed: not null } pending &&
            (pending.Plan.IsTicket || pending.Plan.IsLevelUpTicket) &&
            pending.Plan.OperationId == receipt.Commit?.Plan.OperationId)
            pendingQuestQuickClear = pending with { TicketAcknowledged = true };
        return true;
    });

    public QuestQuickClearReceipt? AnswerManualQuestSkip(IReadOnlyList<QuestSkipAnswer> answers, QuestCatalog quests)
    {
        var capture = CaptureManualSkip();
        if (capture.Character is not { } expected) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"没有可恢复的手册问题上下文");
        if (capture.Pending is not { Plan.IsPreparedSkip: true } pending ||
            !SameManualPending(pending,expected,capture.SelectionEpoch,capture.Phase,capture.Identity)) return receipt;
        if (!pending.AnsweredBatch.IsDefaultOrEmpty && pending.AnsweredBatch.SequenceEqual(answers))
            return owner.Execute(() =>
            {
                if (pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null || !CanPublishQuestQuickClear(receipt) || !ReferenceEquals(pendingQuestQuickClear,pending))
                    throw new InvalidOperationException("重复手册回答的原上下文已失效");
                return pending.Plan.PendingQuestions.Length == 0 && !pending.Plan.IsTicket
                    ? CommitManualQuestSkip(pending,receipt,retainAnswered:true)
                    : receipt with { Prepared = pending.Plan, Reason = "重复回答沿同一冻结手册操作" };
            });
        if (pending.Plan.PendingQuestions.Length == 0 || answers.Count != pending.Plan.PendingQuestions.Length ||
            !answers.Select(row => row.QuestId).SequenceEqual(pending.Plan.PendingQuestions.Select(id => checked((uint)id)))) return receipt;
        var choices = pending.Plan.ManualAnswers;
        foreach (var row in answers)
        {
            if (row.Answer > byte.MaxValue || !quests.TryGet(checked((int)row.QuestId),out var question) ||
                !QuestQuestionProgress.TryCount(question,out int count) || row.Answer >= count)
                return receipt with { Reason = "本次手册零基回答越界" };
            choices = choices.SetItem(checked((int)row.QuestId),checked((byte)row.Answer));
        }
        QuestQuickClearPlan? plan;
        string reason;
        bool created = pending.Plan.LevelUpTicket is { } levelUp
            ? QuestQuickClearPlan.TryCreateLevelUpTicket(expected,pending.Plan.OperationId,quests,levelUp,choices,out plan,out reason,towns)
            : pending.Plan.Ticket is { } ticket
                ? QuestQuickClearPlan.TryCreateTicket(expected,pending.Plan.OperationId,quests,ticket,choices,out plan,out reason,towns:towns)
                : QuestQuickClearPlan.TryCreateManual(expected,pending.Plan.OperationId,quests,choices,out plan,out reason,towns);
        if (!created)
            return receipt with { Reason = reason };
        if(!FreezeQuestEffects(plan!,quests,out string effectReason)) return receipt with { Reason=effectReason };
        return owner.Execute(() =>
        {
            if (pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null || !CanPublishQuestQuickClear(receipt) || !ReferenceEquals(pendingQuestQuickClear,pending))
                throw new InvalidOperationException("手册回答的原上下文已失效");
            var answered = new PendingQuestQuickClear(plan!,capture.SelectionEpoch,capture.Phase,capture.Identity,answers.ToImmutableArray());
            pendingQuestQuickClear = answered;
            // 最后一批答案先原子提交再ACK1431，不能依赖独立mode2客户端再发送1457。
            return plan!.PendingQuestions.Length == 0 && !plan.IsTicket ? CommitManualQuestSkip(answered,receipt,retainAnswered:true)
                : receipt with { Prepared = plan, Reason = reason };
        });
    }

    public QuestQuickClearReceipt? FinishManualQuestSkip()
    {
        var capture = CaptureManualSkip();
        if (capture.Character is not { } expected || capture.Pending is not { Plan.IsManual: true } pending ||
            !SameManualPending(pending,expected,capture.SelectionEpoch,capture.Phase,capture.Identity) || pending.Plan.PendingQuestions.Length != 0) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"手册最终动作待提交");
        return owner.Execute(() =>
        {
            if (pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestCompletion is not null || !CanPublishQuestQuickClear(receipt) || !ReferenceEquals(pendingQuestQuickClear,pending))
                throw new InvalidOperationException("手册最终动作的原选角或场景已失效");
            // 1430的mode1在最终1431 ACK后仍可发送1457；已答流程只消费上下文，不能二次提交/投影。
            if (pending.Committed is not null) { pendingQuestQuickClear = null; return null; }
            return CommitManualQuestSkip(pending,receipt,retainAnswered:false);
        });
    }

    QuestQuickClearReceipt CommitManualQuestSkip(PendingQuestQuickClear pending,QuestQuickClearReceipt receipt,bool retainAnswered)
    {
        if (pending.Committed is { } installed)
            return receipt with { Character = installed.Character, Commit = installed with { Duplicate = true },
                TicketAcknowledged=pending.TicketAcknowledged, Reason = "重复最终回答恢复同一手册提交" };
        // 只有进入仓储提交才占用恢复状态；异常保留最终答案和原操作身份，不能重抽范围。
        pendingQuestQuickClear = pending with { CommitStarted = true };
        QuestQuickClearCommit commit;
        try { commit = repository.CommitQuestQuickClear(owner,pending.Plan); }
        catch (QuestTicketExpiredException error)
        {
            pendingQuestQuickClear = null;
            return receipt with { Reason=error.Message,Prepared=pending.Plan };
        }
        if (!commit.ProjectionCurrent) throw new InvalidOperationException("手册已提交但原角色已变化，需要重连恢复");
        Selected = commit.Character; completed = QuestEligibility.Completed(Selected);
        if (pending.Plan.LevelUpTicket is { } levelUpSource) RecordLevelUpCooldown(levelUpSource);
        // ACK1431发送丢失后可恢复同一结果；已安装提交不再阻止切日。
        pendingQuestQuickClear = retainAnswered ? pending with { Committed = commit, CommitStarted = false } : null;
        return receipt with { Character = commit.Character, Commit = commit, Reason = "手册无奖励完成已提交" };
    }

    (Character? Character,long SelectionEpoch,SessionPhase Phase,DungeonRunIdentity? Identity,PendingQuestQuickClear? Pending) CaptureManualSkip() => owner.Execute(() =>
    {
        Character? current = Ready && Selected is not null && Phase is SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement &&
            pendingRental is null && pendingCommunication is null && pendingQuestCompletion is null && pendingQuestLifecycle is null && !HasPendingQuestScene &&
            !HasPendingFormalCard && formalDungeon?.Ground.HasPendingOperation != true && formalDungeon?.Run.HasPendingDeath != true &&
            formalDungeon?.Run.HasPendingSettlement != true ? Selected : null;
        var identity = formalDungeon?.Run.Snapshot.Identity;
        // 普通操作可继续更新Selected；未提交手册随原快照失效，不阻止无关玩法。
        if (current is not null && pendingQuestQuickClear is { Plan.IsPreparedSkip: true, CommitStarted: false } pending &&
            !SameManualPending(pending,current,SelectionEpoch,Phase,identity)) pendingQuestQuickClear = null;
        return (current,SelectionEpoch,Phase,identity,pendingQuestQuickClear);
    });

    static bool SameManualPending(PendingQuestQuickClear pending,Character expected,long epoch,SessionPhase phase,DungeonRunIdentity? identity) =>
        ReferenceEquals(pending.Committed?.Character ?? pending.Plan.Expected,expected) && pending.SelectionEpoch == epoch && pending.Phase == phase && pending.Identity == identity;

    public bool CanPublishQuestQuickClear(QuestQuickClearReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase &&
        ReferenceEquals(Selected,receipt.Character) && (receipt.Commit?.ProjectionCurrent ?? true) &&
        (receipt.Identity is null || formalDungeon?.Run.Snapshot.Identity == receipt.Identity));
}
