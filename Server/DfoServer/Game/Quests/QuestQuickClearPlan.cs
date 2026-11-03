using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Quests;

internal readonly record struct QuestSkipAnswer(uint QuestId, uint Answer);
internal sealed class QuestTicketExpiredException(string message="任务券在提交前已到期") : InvalidOperationException(message) { }
internal sealed record QuestTicketSource(InventoryItem Item,QuestTicketDefinition Definition,InventoryPlan Inventory,long UtcSeconds)
{
    public static QuestTicketSource Create(Character expected,InventorySlot slot,QuestTicketDefinition definition,long now)
    {
        var item = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || item is null || item.Uid <= 0 || item.Core.ItemId != definition.TemplateId ||
            item.Core.Kind != ItemKind.Consumable || item.Core.Count < 1 || item.Core.IsExpired(now) || item.Core.EquipmentLockId != 0 ||
            !definition.Allows(expected.Level,expected.Job)) throw new InvalidDataException("任务券实例、资格或期限不符");
        var consumed = InventoryDeleteService.Plan(expected.Inventory,slot,item.Core.Value,1);
        if (!consumed.Accepted) throw new InvalidDataException("任务券不能扣除一张");
        return new(item,definition,new(expected.Inventory,consumed.Plan!.After,"quest_ticket"),now);
    }
}

// 主线跳过只核算特殊任务物品；槽位/觉醒由专用券授权，不拥有在线角色状态。
internal sealed partial class QuestQuickClearPlan
{
    readonly QuestConnectionUnit? unit;
    readonly ActiveMainQuest? activeQuestion;
    readonly StoryQuest? question;
    readonly StoryQuest? marker;

    QuestQuickClearPlan(Character expected, QuestConnectionUnit unit, ActiveMainQuest active,
        StoryQuest question, StoryQuest marker, ImmutableArray<StoryQuest> interval, byte answer, QuestCatalog quests,
        ImmutableArray<StoryQuest> areaQuests)
    {
        Expected = expected; EquipmentSlotFlagsAfter = expected.EquipmentSlotFlags; TargetLearning=expected.Learning;
        Inventory=new(expected.Inventory,expected.Inventory,"quest_skip");
        this.unit = unit; activeQuestion = active; this.question = question; this.marker = marker;
        OperationId = active.AcceptanceId;
        Interval = interval; Answer = answer;
        AreaQuests = areaQuests;
        // 终点marker归当前角色；显式账号同步位同时加入账号完成集，不共享分支回答。
        var scope = interval.Concat(areaQuests).Select(quest => quest.Id).Prepend(question.Id).Append(marker.Id).ToHashSet();
        ScopeQuestIds = scope.Order().ToImmutableArray();
        var completed = QuestEligibility.Completed(expected);
        NewCompletedQuestIds = scope.Where(id => !completed.Contains(id)).Order().ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Concat(NewCompletedQuestIds).Distinct().Order().ToImmutableArray();
        AnswersAfter = expected.QuestAnswers.SetItem(question.Id,answer);
        SyncedQuestIds = ScopeQuestIds.Where(id => QuestRelationProgress.IsAccountSyncMarker(quests.Get(id)))
            .Except(expected.AccountCompletedQuests).ToImmutableArray();
        AccountCompletedAfter = expected.AccountCompletedQuests.Concat(SyncedQuestIds).Distinct().Order().ToImmutableArray();
        AccountAnswersAfter = expected.AccountQuestAnswers;
        var removal = ActiveRemovalScope(quests);
        RemovedActive = expected.ActiveMainQuests.Where(row => removal.Contains(row.QuestId)).ToImmutableArray();
        var remaining = QuestRelationProgress.Refresh(expected.ActiveMainQuests.Where(row => !removal.Contains(row.QuestId)).ToArray(),
            CompletedAfter.Concat(AccountCompletedAfter).ToArray(),quests);
        ActiveAfter = QuestStateProgress.Refresh(remaining,expected with
        {
            CompletedQuests = CompletedAfter.ToArray(), AccountCompletedQuests = AccountCompletedAfter.ToArray(), QuestAnswers = AnswersAfter
        },quests);
        Snapshot = JsonSerializer.Serialize(new
        {
            Unit.ParentKey, QuestionId = question.Id, AcceptanceId, expected.Revision, Answer,
            MarkerQuestId, Interval = interval.Select(quest => quest.Id).ToArray(),
            BeforeCharacter = new
            {
                expected.Id, expected.Slot, expected.Name, expected.Job, expected.GrowType, expected.Level,
                expected.EquipmentSlotFlags, expected.AcceptedStoryLevel, expected.Position, expected.Return,
                expected.DungeonState, expected.Skills, expected.Learning, expected.Inventory, expected.QuestCycleDay,
                DailyClaims = expected.QuestDailyClaims.OrderBy(row => row.Key).ToArray(),
                AccountDailyClaims = expected.AccountQuestDailyClaims.OrderBy(row => row.Key).ToArray()
            },
            BeforeActive = expected.ActiveMainQuests, ActiveAfter, BeforeCompleted = expected.CompletedQuests, CompletedAfter,
            BeforeAnswers = expected.QuestAnswers.OrderBy(row => row.Key).ToArray(), AnswersAfter = AnswersAfter.OrderBy(row => row.Key).ToArray(),
            AccountCompletedAfter, AccountAnswersAfter = AccountAnswersAfter.OrderBy(row => row.Key).ToArray()
        });
        if (!areaQuests.IsEmpty)
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot[nameof(AreaQuests)] = JsonSerializer.SerializeToNode(areaQuests.Select(quest => quest.Id));
            Snapshot = snapshot.ToJsonString();
        }
    }

    QuestQuickClearPlan(Character expected, Guid operationId, int cutoff, ImmutableArray<StoryQuest> scope,
        ImmutableDictionary<int,byte> answers, ImmutableArray<int> questions, QuestCatalog quests,QuestTicketSource? ticket = null,
        byte? equipmentSlots = null,byte? profession = null,CharacterLearningState? learning = null,
        LevelUpTicketSource? levelUpTicket = null,bool orphanRepair = false)
    {
        IsOrphanRepair = orphanRepair;
        Expected = expected; OperationId = operationId; IsManual = ticket is null && levelUpTicket is null;
        Ticket = ticket; LevelUpTicket = levelUpTicket; CutoffLevel = cutoff;
        Inventory=ticket?.Inventory ?? levelUpTicket?.Inventory ?? new(expected.Inventory,expected.Inventory,"quest_skip");
        EquipmentSlotFlagsAfter = equipmentSlots ?? expected.EquipmentSlotFlags;
        ProfessionGrowType=profession ?? levelUpTicket?.ProfessionGrowType;
        TargetLearning=learning ?? levelUpTicket?.Learning ?? expected.Learning;
        Interval = scope; ManualAnswers = answers; PendingQuestions = questions;
        ScopeQuestIds = scope.Select(row => row.Id).Distinct().Order().ToImmutableArray();
        var completed = QuestEligibility.Completed(expected);
        NewCompletedQuestIds = ScopeQuestIds.Where(id => !completed.Contains(id)).ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Concat(NewCompletedQuestIds).Distinct().Order().ToImmutableArray();
        AnswersAfter = expected.QuestAnswers.SetItems(answers);
        SyncedQuestIds = ScopeQuestIds.Where(id => QuestRelationProgress.IsAccountSyncMarker(quests.Get(id)))
            .Except(expected.AccountCompletedQuests).ToImmutableArray();
        AccountCompletedAfter = expected.AccountCompletedQuests.Concat(SyncedQuestIds).Distinct().Order().ToImmutableArray();
        AccountAnswersAfter = expected.AccountQuestAnswers;
        var removal = ActiveRemovalScope(quests);
        RemovedActive = expected.ActiveMainQuests.Where(row => removal.Contains(row.QuestId)).ToImmutableArray();
        var retained = expected.ActiveMainQuests.Where(row => !removal.Contains(row.QuestId)).ToArray();
        if (ticket is not null) retained = QuestInventoryProgress.Refresh(retained,ticket.Inventory.After,quests,ticket.UtcSeconds).ToArray();
        if (levelUpTicket is not null) retained = QuestInventoryProgress.Refresh(retained,Inventory.After,quests,levelUpTicket.UtcSeconds).ToArray();
        var remaining = QuestRelationProgress.Refresh(retained,
            CompletedAfter.Concat(AccountCompletedAfter).ToArray(),quests);
        ActiveAfter = QuestStateProgress.Refresh(remaining,expected with
        {
            CompletedQuests = CompletedAfter.ToArray(), AccountCompletedQuests = AccountCompletedAfter.ToArray(), QuestAnswers = AnswersAfter,EquipmentSlotFlags=EquipmentSlotFlagsAfter,
            GrowType=ProfessionGrowType ?? expected.GrowType,Learning=TargetLearning,Level=TargetLevel
        },quests);
        Snapshot = JsonSerializer.Serialize(new
        {
            OperationId, Kind = orphanRepair ? "OrphanCleanup" : ticket is null ? "Manual" : "Ticket", CutoffLevel, ScopeQuestIds,
            Answers = answers.OrderBy(row => row.Key).ToArray(),
            BeforeCharacter = new
            {
                expected.Id, expected.Slot, expected.Name, expected.Job, expected.GrowType, expected.Level,
                expected.EquipmentSlotFlags, expected.AcceptedStoryLevel, expected.Position, expected.Return,
                expected.DungeonState, expected.Skills, expected.Learning, expected.Inventory, expected.QuestCycleDay,
                DailyClaims = expected.QuestDailyClaims.OrderBy(row => row.Key).ToArray(),
                AccountDailyClaims = expected.AccountQuestDailyClaims.OrderBy(row => row.Key).ToArray()
            },
            BeforeActive = expected.ActiveMainQuests, ActiveAfter, BeforeCompleted = expected.CompletedQuests, CompletedAfter,
            BeforeAnswers = expected.QuestAnswers.OrderBy(row => row.Key).ToArray(), AnswersAfter = AnswersAfter.OrderBy(row => row.Key).ToArray(),
            AccountCompletedAfter, AccountAnswersAfter = AccountAnswersAfter.OrderBy(row => row.Key).ToArray()
        });
        if (ticket is not null)
            Snapshot = JsonSerializer.Serialize(new { State = JsonSerializer.Deserialize<JsonElement>(Snapshot),
                TicketSource = ticket.Item, TicketDefinition = ticket.Definition, ticket.UtcSeconds, InventoryAfter = ticket.Inventory.After });
        if (ticket?.Definition.ActionIndex == QuestTicketDefinition.SpecifiedAction)
            Snapshot = JsonSerializer.Serialize(new { State = JsonSerializer.Deserialize<JsonElement>(Snapshot),
                Action=ticket.Definition.ActionIndex,Targets=ticket.Definition.TargetQuestIds,EquipmentSlotFlagsAfter });
        if (profession is not null)
            Snapshot=JsonSerializer.Serialize(new { State=JsonSerializer.Deserialize<JsonElement>(Snapshot),
                Action=ticket!.Definition.ActionIndex,ticket.Definition.VillageOnly,ProfessionGrowType,TargetLearning });
        if (levelUpTicket is not null)
            Snapshot=JsonSerializer.Serialize(new { State=JsonSerializer.Deserialize<JsonElement>(Snapshot),
                Kind="LevelUpTicket",Source=levelUpTicket.Item,Definition=levelUpTicket.Definition,levelUpTicket.UtcSeconds,
                levelUpTicket.ByTable,levelUpTicket.ProfessionGrowType,
                InventoryAfter=Inventory.After,TargetLevel,TargetLearning });
    }

    public Character Expected { get; }
    public byte EquipmentSlotFlagsAfter { get; private set; }
    public byte? ProfessionGrowType { get; private set; }
    public CharacterLearningState TargetLearning { get; private set; }
    public InventoryPlan Inventory { get; private set; } = null!;
    public QuestEffectPlan? Effects { get; private set; }
    // 1714保留真实question合同；手册与任务券不制造question或普通接取身份。
    public QuestConnectionUnit Unit => unit ?? throw new InvalidOperationException("手册或任务券没有旅程单元");
    public ActiveMainQuest ActiveQuestion => activeQuestion ?? throw new InvalidOperationException("手册或任务券没有普通问题接取槽");
    public Guid AcceptanceId => activeQuestion?.AcceptanceId ?? Guid.Empty;
    public StoryQuest Question => question ?? throw new InvalidOperationException("手册或任务券没有单一问题任务");
    public byte Answer { get; }
    public StoryQuest MarkerDefinition => marker ?? throw new InvalidOperationException("手册或任务券没有单一旅程标记");
    public int MarkerQuestId => MarkerDefinition.Id;
    public bool IsManual { get; }
    public bool IsOrphanRepair { get; }
    public QuestTicketSource? Ticket { get; }
    public LevelUpTicketSource? LevelUpTicket { get; }
    public byte TargetLevel => LevelUpTicket?.Growth.TargetLevel ?? Expected.Level;
    public bool IsLevelUpTicket => LevelUpTicket is not null;
    public bool IsTicket => Ticket is not null;
    public bool IsAchievementTicket => Ticket?.Definition.ActionIndex is QuestTicketDefinition.AchievementAction or QuestTicketDefinition.SideAction;
    public bool IsLevelClearTicket => Ticket?.Definition.ActionIndex == QuestTicketDefinition.LevelAction;
    public bool IsFixedLevelTicket => Ticket?.Definition.ActionIndex is QuestTicketDefinition.LevelAction or QuestTicketDefinition.EpicAction or QuestTicketDefinition.AllAction or QuestTicketDefinition.GmAction;
    public int MarkingLimit => Ticket!.Definition.ActionIndex switch
    {
        QuestTicketDefinition.GmAction => int.MaxValue,
        QuestTicketDefinition.AllAction => Expected.Level,
        _ => Ticket.Definition.CompletionLevel ?? Expected.Level
    };
    public bool HasTitleRewards => IsAchievementTicket || IsLevelClearTicket || Ticket?.Definition.ActionIndex==QuestTicketDefinition.AllAction;
    public ImmutableArray<int> GeneralTitleQuestIds { get; private set; } = [];
    public bool IsPreparedSkip => IsManual || IsTicket || IsLevelUpTicket;
    public string OperationKind => IsTicket || IsLevelUpTicket ? "Ticket" : IsManual ? "Manual" : "QuickClear";
    internal HashSet<int> ActiveRemovalScope(QuestCatalog? quests) => IsAchievementTicket
        ? ScopeQuestIds.ToHashSet() : RemovalScope(Expected,ScopeQuestIds.ToHashSet(),quests);

    internal static bool AchievementTitle(StoryQuest quest) => quest.Grade == "[achievement]" && quest.RewardType == "[title]";
    // 清理范围与完成/奖励范围分开：合法父链已结束的活动子槽不能继续弹出，也不能借修复补领奖。
    internal static HashSet<int> RemovalScope(Character expected,IReadOnlySet<int> scope,QuestCatalog? quests)
    {
        var removal=scope.ToHashSet();
        if(quests is null) return removal;
        var done=expected.CompletedQuests.ToHashSet();
        var active=expected.ActiveMainQuests.Select(row=>(int)row.QuestId).ToHashSet();
        foreach(var row in expected.ActiveMainQuests)
        {
            if(row.AcceptanceId==Guid.Empty || !quests.TryGet(row.QuestId,out var child) || child.Grade!="[sub]" ||
                child.MainQuest is not { } parentId || QuestRelationProgress.ZeroRewardChild(child) || !child.Attributes.Contains("not give exp quest") ||
                !quests.TryGet(parentId,out var parent) || parent.Grade!="[epic]" || !QuestRelationProgress.SequentialParent(parent,quests) ||
                !parent.IntData.Contains(child.Id) || !scope.Contains(parentId) && (!done.Contains(parentId) || active.Contains(parentId)) ||
                child.Definition is not {AccountQuest:false,JobChange:0,SelectRewards.Length:0,AcceptanceRewards.Length:0,RequiredAnswers.Length:0} definition ||
                definition.Has("casecade clear") || QuestCycle.Daily(child) || QuestCycle.Repeat(child) ||
                child.DeclaredGoldMultiple is not (null or 0) || child.Condition=="[question]" || child.RewardType is not (null or "[item]") ||
                !QuestCoverage.HasOrdinaryDeclarations(child)) continue;
            removal.Add(child.Id);
        }
        return removal;
    }

    internal static bool TryCreateOrphanRepair(Character expected,QuestCatalog quests,out QuestQuickClearPlan? plan)
    {
        plan=null;
        if(expected.Id==0 || expected.Level is <1 or >95 || RemovalScope(expected,new HashSet<int>(),quests).Count==0) return false;
        plan=new(expected,Guid.NewGuid(),ManualCutoff(expected.Level),[],ImmutableDictionary<int,byte>.Empty,[],quests,orphanRepair:true);
        return true;
    }

    public Guid OperationId { get; }
    public int? ParentKey => unit?.ParentKey;
    public int? QuestionQuestId => question?.Id;
    public int CutoffLevel { get; }
    public ImmutableDictionary<int,byte> ManualAnswers { get; } = ImmutableDictionary<int,byte>.Empty;
    public ImmutableArray<int> PendingQuestions { get; } = [];
    public ImmutableArray<int> ScopeQuestIds { get; }
    public ImmutableArray<StoryQuest> Interval { get; }
    public ImmutableArray<StoryQuest> AreaQuests { get; } = [];
    public ImmutableArray<int> NewCompletedQuestIds { get; }
    public ImmutableArray<int> CompletedAfter { get; }
    public ImmutableDictionary<int,byte> AnswersAfter { get; }
    public ImmutableArray<int> AccountCompletedAfter { get; }
    public ImmutableArray<int> SyncedQuestIds { get; }
    public ImmutableDictionary<int,byte> AccountAnswersAfter { get; }
    public ImmutableArray<ActiveMainQuest> RemovedActive { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; private set; }
    public string Snapshot { get; private set; }

    // 仅在锁外构建阶段附加效果；pending保存后始终复用这份冻结计划。
    public void PrepareEffects(QuestSkipEffects? effects,QuestCatalog quests)
    {
        Inventory = Ticket?.Inventory ?? LevelUpTicket?.Inventory ?? new(Expected.Inventory,Expected.Inventory,"quest_skip");
        if (effects is null)
        {
            if (HasTitleRewards) throw new NotSupportedException("任务券称号发放资源未装配");
            return;
        }
        var ordered=IsPreparedSkip ? Interval : Interval.Prepend(Question).Concat(AreaQuests).Append(quests.Get(MarkerQuestId)).ToImmutableArray();
        var newlyCompleted = ordered.Where(quest=>NewCompletedQuestIds.Contains(quest.Id));
        Effects = HasTitleRewards ? effects.PrepareAchievementTitles(Expected,newlyCompleted.Where(AchievementTitle),Inventory,
            DateTimeOffset.FromUnixTimeSeconds(Ticket!.UtcSeconds),GeneralTitleQuestIds) :
            IsFixedLevelTicket ? new(Expected,Inventory,Expected.EquipmentSlotFlags,Expected.GrowType,Expected.Learning) :
            effects.Prepare(Expected with { Level=TargetLevel },newlyCompleted,Inventory,EquipmentSlotFlagsAfter,
            ProfessionGrowType ?? Expected.GrowType,TargetLearning,
            characterState:Ticket?.Definition.ActionIndex is QuestTicketDefinition.SpecifiedAction or QuestTicketDefinition.FirstAwakeningAction or QuestTicketDefinition.SecondAwakeningAction);
        Inventory=Effects.Inventory; EquipmentSlotFlagsAfter=Effects.EquipmentSlots;
        ProfessionGrowType=Effects.GrowType==Expected.GrowType ? ProfessionGrowType : Effects.GrowType;
        TargetLearning=Effects.Learning;
        var current = Expected with { Inventory=Inventory.After,GrowType=Effects.GrowType,EquipmentSlotFlags=EquipmentSlotFlagsAfter,
            Learning=TargetLearning,Level=TargetLevel,CompletedQuests=CompletedAfter.ToArray(),AccountCompletedQuests=AccountCompletedAfter.ToArray(),QuestAnswers=AnswersAfter };
        ActiveAfter=QuestStateProgress.Refresh(QuestInventoryProgress.Refresh(ActiveAfter.ToArray(),Inventory.After,quests,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()),current,quests);
        if (Effects.Changed)
            Snapshot=JsonSerializer.Serialize(new { State=JsonSerializer.Deserialize<JsonElement>(Snapshot),
                Effects=new { InventoryAfter=Inventory.After,EquipmentSlotFlagsAfter,ProfessionGrowType,TargetLearning,
                    Titles=Effects.Titles is { } titles ? new { titles.Progress,titles.Sections,titles.Completed,titles.Postals,titles.PostalItems } : null } });
    }

    // Full03手册确认窗：Lv90..94截止89，Lv95截止94，其余截止实际等级减一。
    public static int ManualCutoff(byte level) => level < 95 && level - 1 >= 90 ? 89 : level - 1;

    public static bool TryCreateManual(Character expected, Guid operationId, QuestCatalog quests,
        ImmutableDictionary<int,byte> answers, out QuestQuickClearPlan? plan, out string reason, TownCatalog? towns = null)
        => TryCreateRange(expected,operationId,quests,answers,null,out plan,out reason,towns);

    public static bool TryCreateLevelUpTicket(Character expected, Guid operationId, QuestCatalog quests, LevelUpTicketSource source,
        ImmutableDictionary<int,byte> answers, out QuestQuickClearPlan? plan, out string reason, TownCatalog? towns = null)
        => TryCreateRange(expected,operationId,quests,answers,null,out plan,out reason,towns,source);

    public static bool TryCreateTicket(Character expected,Guid operationId,QuestCatalog quests,QuestTicketSource ticket,
        ImmutableDictionary<int,byte> answers,out QuestQuickClearPlan? plan,out string reason,SkillLearningCatalog? skills=null,TownCatalog? towns=null,
        TitleCatalog? titles=null)
    {
        if (ticket.Definition.AwakeningStage!=0)
            return TryCreateAwakeningTicket(expected,operationId,quests,ticket,answers,skills,out plan,out reason);
        if (ticket.Definition.ActionIndex==QuestTicketDefinition.SpecifiedAction)
            return TryCreateSpecifiedTicket(expected,operationId,quests,ticket,answers,out plan,out reason);
        if (ticket.Definition.ActionIndex is QuestTicketDefinition.AchievementAction or QuestTicketDefinition.SideAction)
            return TryCreateAchievementTicket(expected,operationId,quests,ticket,answers,out plan,out reason,titles);
        if (ticket.Definition.ActionIndex is QuestTicketDefinition.LevelAction or QuestTicketDefinition.EpicAction or QuestTicketDefinition.AllAction or QuestTicketDefinition.GmAction)
            return TryCreateLevelClearTicket(expected,operationId,quests,ticket,answers,out plan,out reason,titles);
        if (ticket.Definition.ActionIndex==QuestTicketDefinition.Action)
            return TryCreateRange(expected,operationId,quests,answers,ticket,out plan,out reason,towns);
        plan=null; reason="任务券动作尚未接入"; return false;
    }

    static bool TryCreateAwakeningTicket(Character expected,Guid operationId,QuestCatalog quests,QuestTicketSource ticket,
        ImmutableDictionary<int,byte> answers,SkillLearningCatalog? skills,out QuestQuickClearPlan? plan,out string reason)
    {
        plan=null;
        try
        {
            int stage=ticket.Definition.AwakeningStage;
            if (expected.Id==0 || operationId==Guid.Empty || skills is null || stage!= (expected.GrowType>>4)+1 ||
                !ReferenceEquals(ticket.Inventory.Before,expected.Inventory) || expected.Inventory.Find(ticket.Item.Position)!=ticket.Item)
                throw new InvalidDataException("觉醒券需要真实源实例、逐级阶段及匹配技能资源");
            var candidates=quests.All.Where(row=>row.Definition?.JobChange==stage+1).ToArray();
            var completed=QuestEligibility.Completed(expected); var changed=new List<StoryQuest>();
            var chosen=ImmutableDictionary<int,byte>.Empty;
            byte? target=null;
            bool progressed;
            // external按当前stage+1逐轮重算可达；本版复用OR前置，绝不补齐普通剧情前置或未知回答。
            do
            {
                progressed=false;
                foreach (var quest in candidates)
                {
                    if (completed.Contains(quest.Id)) continue;
                    var current=expected with { CompletedQuests=completed.Except(expected.AccountCompletedQuests).Order().ToArray(),
                        QuestAnswers=expected.QuestAnswers.SetItems(chosen),
                        ActiveMainQuests=expected.ActiveMainQuests.Where(row=>row.QuestId!=quest.Id && !completed.Contains(row.QuestId)).ToArray() };
                    var entry=quest;
                    if (quest.MainQuest is { } parent && completed.Contains(parent) &&
                        candidates.Any(row=>row.Id==parent && row.Condition is ("[quest clear]" or "[clear quest]" or "[clear multi quest]") && row.IntData.Contains(quest.Id)))
                        entry=quest with { MainQuest=null };
                    if (!QuestEligibility.Evaluate(current,entry,quests).Eligible) continue;
                    byte? selectedAnswer=null;
                    try
                    {
                        ValidateAwakening(quest,quests);
                        if (quest.Condition=="[question]")
                        {
                            uint answer=expected.QuestAnswers.TryGetValue(quest.Id,out byte saved) ? saved :
                                expected.ActiveMainQuests.FirstOrDefault(row=>row.QuestId==quest.Id)?.ConditionState ?? QuestQuestionProgress.Unanswered;
                            if (!QuestQuestionProgress.TryCount(quest,out int count) || answer>=count ||
                                !QuestQuestionProgress.OrdinaryChoice(quest,answer,quests)) continue;
                            selectedAnswer=checked((byte)answer);
                        }
                        if (quest.RewardType=="[awakening type]")
                        {
                            byte next=QuestProfessionReward.Target(expected,quest,skills);
                            if (target is not null && target!=next) throw new InvalidDataException("觉醒链终点职业不同");
                            target=next;
                        }
                    }
                    catch(Exception error) when(error is InvalidDataException or NotSupportedException or OverflowException or KeyNotFoundException)
                    { continue; } // 失效旧定义或无资格专用链隔离，不修补也不写完成。
                    if (selectedAnswer is { } choice) chosen=chosen.SetItem(quest.Id,choice);
                    completed.Add(quest.Id); changed.Add(quest); progressed=true;
                }
            } while(progressed);
            if (changed.Count==0 || target is null) throw new InvalidDataException("当前觉醒阶段没有可达的真实职业奖励终点");
            if (answers.Any(row=>!chosen.TryGetValue(row.Key,out byte value) || value!=row.Value))
                throw new InvalidDataException("觉醒券不能新增或覆盖未实际选择的回答");
            var learning=QuestProfessionReward.Prepare(expected,target.Value,expected.Level,expected.Learning,skills,
                DateTimeOffset.FromUnixTimeSeconds(ticket.UtcSeconds));
            plan=new(expected,operationId,expected.Level,changed.ToImmutableArray(),chosen,[],quests,ticket,
                profession:target,learning:learning);
            reason="当前职业可达觉醒链、两树来源及实际券消费已冻结"; return true;
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException or OverflowException or KeyNotFoundException)
        { reason=error.Message; return false; }
    }

    static void ValidateAwakening(StoryQuest quest,QuestCatalog quests)
    {
        QuestCoverage.RequireOrdinaryDeclarations(quest);
        if (!QuestCoverage.HasRequiredReferences(quest,quests) || QuestAccountPolicy.SharedCompletion(quest) || quest.Definition is not { AccountQuest:false } definition ||
            QuestCycle.Daily(quest) || QuestCycle.Repeat(quest) || definition.Has("casecade clear"))
            throw new NotSupportedException("觉醒券目标引用、账号、周期或级联不支持: "+quest.Id);
        if (quest.RewardType=="[awakening type]")
        {
            if (quest.Rewards is not [{Type:0,Text:null,Value:1 or 2}] || definition.SelectRewards.Length!=0)
                throw new InvalidDataException("觉醒奖励终点形状不完整: "+quest.Id);
        }
        else
        {
            if (quest.RewardType is not (null or "[item]")) throw new NotSupportedException("觉醒券不代替其它专用奖励: "+quest.Id);
            _=QuestFixedRewardDefinition.Parse(quest,allowGold:true);
        }
        _=QuestFixedRewardDefinition.Parse(quest with { Rewards=definition.SelectRewards });
        _=QuestFixedRewardDefinition.Parse(quest with { Rewards=definition.AcceptanceRewards });
        if (quest.Condition is "[seeking]" or "[seek n meet npc]" && !QuestLifecyclePlan.ValidSeeking(quest))
            throw new InvalidDataException("觉醒券寻物声明不完整: "+quest.Id);
    }

    static bool TryCreateAchievementTicket(Character expected,Guid operationId,QuestCatalog quests,QuestTicketSource ticket,
        ImmutableDictionary<int,byte> answers,out QuestQuickClearPlan? plan,out string reason,TitleCatalog? titles)
    {
        plan=null;
        try
        {
            if (expected.Id==0 || operationId==Guid.Empty || expected.Level is <1 or >95 || !answers.IsEmpty ||
                !ReferenceEquals(ticket.Inventory.Before,expected.Inventory) || expected.Inventory.Find(ticket.Item.Position)!=ticket.Item)
                throw new InvalidDataException("成就券需要原角色、真实源实例及独立操作身份");
            var completed=QuestEligibility.Completed(expected);
            var scope=new List<StoryQuest>();
            var candidates=quests.All.Where(quest=>AchievementTitle(quest) && quest.MinimumLevel<=expected.Level).ToArray();
            int snapshotCount=-1;
            int[] completedSnapshot=[];
            bool progressed;
            do
            {
                progressed=false;
                foreach (var quest in candidates)
                {
                    if (completed.Contains(quest.Id)) continue;
                    try
                    {
                        QuestCoverage.RequireOrdinaryDeclarations(quest);
                        if (!QuestCoverage.HasRequiredReferences(quest,quests) || QuestAccountPolicy.SharedCompletion(quest) ||
                            quest.Definition is not { AccountQuest:false,JobChange:0,SelectRewards.Length:0 } || QuestCycle.Daily(quest) ||
                            QuestCycle.Repeat(quest) || quest.Condition=="[question]" || quest.Definition.Has("casecade clear")) continue;
                        _=QuestFixedRewardDefinition.Parse(quest,allowGold:true);
                    }
                    catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
                    { continue; }
                    if (snapshotCount!=scope.Count)
                    {
                        completedSnapshot=expected.CompletedQuests.Concat(scope.Select(row=>row.Id)).ToArray();
                        snapshotCount=scope.Count;
                    }
                    var current=expected with
                    {
                        CompletedQuests=completedSnapshot,
                        ActiveMainQuests=expected.ActiveMainQuests.Where(row=>row.QuestId!=quest.Id && !completed.Contains(row.QuestId)).ToArray()
                    };
                    if (!QuestEligibility.Evaluate(current,quest,quests).Eligible) continue;
                    completed.Add(quest.Id); scope.Add(quest); progressed=true;
                }
            } while (progressed);
            var general = titles?.Slots.Where(row=>row.Key.Category==0 && row.Value.Kind!=-1 && row.Value.QuestId>0)
                .OrderBy(row=>row.Key.Index).Select(row=>row.Value.QuestId).Distinct()
                .Where(id=>expected.TitleProgress.GetValueOrDefault(id)?.Remaining.Complete!=true).ToImmutableArray() ?? [];
            if (scope.Count==0 && general.IsEmpty)
                throw new InvalidDataException("当前等级没有可完成的achievement/title任务，general称号簿也无待发奖励");
            plan=new(expected,operationId,expected.Level,scope.ToImmutableArray(),ImmutableDictionary<int,byte>.Empty,[],quests,ticket);
            plan.GeneralTitleQuestIds=general;
            plan.Snapshot=JsonSerializer.Serialize(new { State=JsonSerializer.Deserialize<JsonElement>(plan.Snapshot),
                Action=ticket.Definition.ActionIndex,GeneralTitleQuestIds=general,
                TitleProgressBefore=expected.TitleProgress,TitleSectionsBefore=expected.TitleSections });
            reason="成就券的achievement/title交集、general称号簿及真实源消费已冻结";
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        { reason=error.Message; return false; }
    }

    public static bool TryCreateSpecifiedTicket(Character expected,Guid operationId,QuestCatalog quests,QuestTicketSource ticket,
        ImmutableDictionary<int,byte> answers,out QuestQuickClearPlan? plan,out string reason)
    {
        plan=null;
        try
        {
            var targets=ticket.Definition.TargetQuestIds;
            if (expected.Id==0 || operationId==Guid.Empty || ticket.Definition.ActionIndex!=QuestTicketDefinition.SpecifiedAction ||
                targets.IsDefaultOrEmpty || targets.Any(id=>id is < 1 or >= 30000) || targets.Distinct().Count()!=targets.Length ||
                !ReferenceEquals(ticket.Inventory.Before,expected.Inventory) || expected.Inventory.Find(ticket.Item.Position)!=ticket.Item ||
                !answers.IsEmpty)
                throw new InvalidDataException("指定券需要实际角色、原序目标、源实例及独立操作身份");
            var completed=QuestEligibility.Completed(expected); var changed=ImmutableArray.CreateBuilder<StoryQuest>();
            byte slots=expected.EquipmentSlotFlags;
            // external AnyQuestClear只授权显式targets，逐项推进；不自动扩展前置/子链，资格复用本版OR组。
            foreach (int id in targets)
            {
                if (!quests.TryGet(id,out var quest)) throw new InvalidDataException("指定任务券引用缺失: "+id);
                if (completed.Contains(id)) continue;
                QuestCoverage.RequireOrdinaryDeclarations(quest);
                if (!QuestCoverage.HasRequiredReferences(quest,quests) || QuestAccountPolicy.SharedCompletion(quest) || quest.Definition is not { AccountQuest:false,JobChange:0 } ||
                    QuestCycle.Daily(quest) || QuestCycle.Repeat(quest) || quest.Condition=="[question]")
                    throw new NotSupportedException("指定券目标需要尚未接入的引用、账号、周期、职业或问题事务: "+id);
                if (quest.RewardType is not (null or "[item]" or "[slot expansion]" or "[hell challenge]" or "[inferno challenge]" or "[tp]") ||
                    quest.Definition.Has("casecade clear"))
                    throw new NotSupportedException("指定券目标的专用状态或级联尚未接入: "+id);
                if (quest.RewardType=="[slot expansion]")
                {
                    if (!QuestCharacterReward.TryScalar(quest,out _)) throw new InvalidDataException("指定券槽位奖励形状无效: "+id);
                }
                else if (quest.RewardType=="[tp]")
                {
                    if (!QuestCharacterReward.TryTp(quest,out _)) throw new InvalidDataException("指定券TP奖励形状无效: "+id);
                }
                else if (quest.RewardType is "[hell challenge]" or "[inferno challenge]")
                {
                    // 资格由真实完成记录驱动；1和副本号是专用参数，不按物品奖励解析或发放。
                    if (!QuestCharacterReward.TryHellChallenge(quest)) throw new InvalidDataException("指定券深渊资格奖励形状无效: "+id);
                }
                else _=QuestFixedRewardDefinition.Parse(quest,allowGold:true);
                _=QuestFixedRewardDefinition.Parse(quest with { Rewards=quest.Definition.SelectRewards });
                _=QuestFixedRewardDefinition.Parse(quest with { Rewards=quest.Definition.AcceptanceRewards });
                if (quest.Condition is "[seeking]" or "[seek n meet npc]" && !QuestLifecyclePlan.ValidSeeking(quest))
                    throw new InvalidDataException("指定券寻物声明不完整: "+id);
                var current=expected with { CompletedQuests=completed.Except(expected.AccountCompletedQuests).Order().ToArray(),
                    ActiveMainQuests=expected.ActiveMainQuests.Where(row=>row.QuestId!=id && !completed.Contains(row.QuestId)).ToArray(),
                    EquipmentSlotFlags=slots };
                if (!QuestEligibility.Evaluate(current,quest,quests).Eligible)
                    throw new InvalidDataException("指定券目标角色、前置、互斥或已有回答资格不符: "+id);
                slots=QuestCharacterReward.EquipmentSlots(current,quest); completed.Add(id); changed.Add(quest);
            }
            if (changed.Count==0) throw new InvalidDataException("指定任务券目标均已完成，不能再次消费");
            plan=new(expected,operationId,expected.Level,changed.ToImmutable(),answers,[],quests,ticket,slots);
            reason="指定券原序目标及槽位结果已冻结";
            return true;
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException or OverflowException)
        { reason=error.Message; return false; }
    }

    static bool TryCreateRange(Character expected, Guid operationId, QuestCatalog quests,
        ImmutableDictionary<int,byte> answers,QuestTicketSource? ticket,out QuestQuickClearPlan? plan,out string reason,TownCatalog? towns,
        LevelUpTicketSource? levelUpTicket = null)
    {
        plan = null;
        try
        {
            var original = expected;
            if (levelUpTicket is not null)
            {
                if (!ReferenceEquals(levelUpTicket.Inventory.Before,expected.Inventory) ||
                    expected.Inventory.Find(levelUpTicket.Item.Position) != levelUpTicket.Item ||
                    levelUpTicket.Growth.CharacterId != expected.Id || levelUpTicket.Growth.Revision != expected.Revision ||
                    !levelUpTicket.Allows(expected) || levelUpTicket.Growth.PreviousLevel != expected.Level ||
                    levelUpTicket.Growth.TargetLevel != levelUpTicket.TargetLevel(expected.Level))
                    throw new InvalidDataException("升级券需要原角色成长及源实例快照");
                expected = expected with { Level=levelUpTicket.Growth.TargetLevel,Learning=levelUpTicket.Learning,
                    GrowType=levelUpTicket.ProfessionGrowType ?? expected.GrowType,
                    Inventory=levelUpTicket.Inventory.After };
            }
            // 普通券包含当前等级的epic/normal/mission；手册仍只处理低一级主线。
            int cutoff = ticket is null ? ManualCutoff(expected.Level) : expected.Level;
            if (expected.Id == 0 || operationId == Guid.Empty || expected.Job >= 16 || expected.Level is < 1 or > 95 ||
                ticket is null && expected.Level < 2 || ticket is not null &&
                (!ReferenceEquals(ticket.Inventory.Before,expected.Inventory) || expected.Inventory.Find(ticket.Item.Position) != ticket.Item))
                throw new InvalidDataException("手册需要实际角色、操作身份及有效截止等级");
            var ordered = quests.All.ToArray();
            var access = AreaQuestScope(quests,towns);
            var scope = new List<StoryQuest>(); var scoped = new HashSet<int>();
            var completed = QuestEligibility.Completed(expected);
            var remainingAnswers = answers.Keys.ToHashSet();
            var chosen = expected.QuestAnswers;
            var questions = new List<int>();
            bool progressed;
            do
            {
                progressed = false; questions.Clear();
                foreach (var quest in ordered)
                {
                    bool areaQuest = access.Contains(quest.Id);
                    if (!(quest.Grade == "[epic]" || ticket is not null && quest.Grade is (null or "" or "[normal]" or "[mission]")) ||
                        quest.MainQuest is not null || quest.MinimumLevel is < 1 ||
                        quest.MinimumLevel > Math.Min(expected.Level,cutoff) || completed.Contains(quest.Id) ||
                        quest.Definition is not { ExposedByNpc: true }) continue;
                    var current = Current();
                    var declaration=SkipMainDeclaration(current,quest,quests,areaQuest);
                    if (!QuestEligibility.Evaluate(current with
                        { ActiveMainQuests = current.ActiveMainQuests.Where(row => row.QuestId != quest.Id).ToArray() },declaration,quests).Eligible) continue;
                    // 专用或坏定义隔离，不写其完成记录；后继仍须满足真实前置，不能阻断独立普通主线。
                    if (!CanSkipManual(declaration,quests,areaQuest)) continue;
                    if (quest.Condition == "[question]")
                    {
                        if (!QuestQuestionProgress.TryCount(quest,out int count) ||
                            !Enumerable.Range(0,count).Any(value => QuestQuestionProgress.OrdinaryChoice(quest,(uint)value,quests))) continue;
                        if (!answers.TryGetValue(quest.Id,out byte answer))
                        {
                            questions.Add(quest.Id); continue;
                        }
                        if (answer >= count || !QuestQuestionProgress.OrdinaryChoice(quest,answer,quests) ||
                            expected.QuestAnswers.TryGetValue(quest.Id,out byte previous) && previous != answer ||
                            expected.AccountQuestAnswers.TryGetValue(quest.Id,out byte shared) && shared != answer)
                            throw new InvalidDataException("手册问题回答越界、专用分支或与既有回答冲突: " + quest.Id);
                        chosen = chosen.SetItem(quest.Id,answer); remainingAnswers.Remove(quest.Id);
                    }
                    Add(quest); AddLinked(quest); progressed = true;
                }
            } while (progressed);
            if (remainingAnswers.Count != 0) throw new InvalidDataException("手册回答不属于当前可达问题");
            if (questions.Count > byte.MaxValue) throw new NotSupportedException("手册本批问题超过1381数量范围");
            if (scope.Count == 0 && questions.Count == 0 && levelUpTicket is null) throw new InvalidDataException("截止等级内没有可用手册主线");
            plan = new(original,operationId,cutoff,scope.ToImmutableArray(),answers,questions.ToImmutableArray(),quests,ticket,
                levelUpTicket:levelUpTicket);
            reason = questions.Count == 0 ? "手册范围及回答已冻结" : "手册等待真实问题回答";
            return true;

            Character Current() => expected with
            {
                CompletedQuests = expected.CompletedQuests.Concat(scoped).Distinct().Order().ToArray(), QuestAnswers = chosen,
                ActiveMainQuests = expected.ActiveMainQuests.Where(row => !scoped.Contains(row.QuestId)).ToArray()
            };
            void Add(StoryQuest quest)
            {
                if (!completed.Add(quest.Id)) return;
                scoped.Add(quest.Id); scope.Add(quest);
                if (QuestRelationProgress.CascadeMarker(quest,quests) is { } markerId && completed.Add(markerId))
                { scoped.Add(markerId); scope.Add(quests.Get(markerId)); }
            }
            void AddLinked(StoryQuest parent)
            {
                if (parent.Condition is not ("[quest clear]" or "[clear quest]")) return;
                foreach (int id in parent.IntData.Distinct())
                {
                    if (!quests.TryGet(id,out var child) || child.MainQuest != parent.Id || !QuestRelationProgress.ZeroRewardChild(child) ||
                        child.MinimumLevel is < 1 || child.MinimumLevel > Math.Min(expected.Level,cutoff) || completed.Contains(id)) continue;
                    // 显式反向父引用授权子范围；只移除已校验的父入口要求，不伪造普通接取槽。
                    var current = Current();
                    var declaration=SkipMainDeclaration(current,child,quests,access.Contains(id));
                    if (!QuestEligibility.Evaluate(current with
                        { ActiveMainQuests = current.ActiveMainQuests.Where(row => row.QuestId != id).ToArray() },declaration with { MainQuest = null },quests).Eligible) continue;
                    if (!CanSkipManual(declaration,quests,access.Contains(child.Id)) || child.Condition == "[question]") continue;
                    Add(child); AddLinked(child);
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = error.Message;
            return false;
        }
    }

    static bool CanSkipManual(StoryQuest quest,QuestCatalog quests,bool areaQuest = false)
    {
        try
        {
            if(areaQuest && (QuestCycle.Daily(quest) || QuestCycle.Repeat(quest))) return false;
            if(areaQuest) quest=SkipAreaDeclaration(quest);
            ValidateManual(quest,quests); return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException) { return false; }
    }

    static StoryQuest SkipAreaDeclaration(StoryQuest quest)
    {
        if(quest.Definition is not { } definition) return quest;
        return quest with {Definition=definition with
        {
            Fields=definition.Fields.Where(field=>!SkipAreaDecoration(field)).ToImmutableArray(),
            FirstExposedByNpc=definition.Fields.Any(field=>field.Name=="first exposed by npc" && SkipAreaDecoration(field))
                ? null : definition.FirstExposedByNpc
        }};
    }

    static StoryQuest SkipMainDeclaration(Character current,StoryQuest quest,QuestCatalog quests,bool areaQuest,
        StoryQuest? areaDeclaration = null)
    {
        var declaration=areaQuest ? areaDeclaration ?? SkipAreaDeclaration(quest) : quest;
        if(QuestEligibility.TransferredStoryAlternative(quest,quests,current.Job))
            return declaration with {Definition=declaration.Definition! with {JobChange=0}};
        if(quest.Grade!="[epic]" || quest.Rewards is not [{Type:0,Text:null,Value:>=1 and <=5} reward] ||
            quest.Definition is not {SelectRewards.IsEmpty:true,AcceptanceRewards.IsEmpty:true} definition) return declaration;
        int branch=current.GrowType&15,stage=current.GrowType>>4;
        byte previous;
        if(quest.RewardType=="[grow type]" && branch==reward.Value) previous=0;
        else if(quest.RewardType=="[awakening type]" && reward.Value<=2 && (stage==reward.Value-1 || stage>=reward.Value) &&
            (branch>0 || current.Job is 9 or 10)) previous=(byte)(branch | (reward.Value-1)<<4);
        else return declaration;
        var candidate=current with {GrowType=previous,ActiveMainQuests=current.ActiveMainQuests.Where(row=>row.QuestId!=quest.Id).ToArray()};
        if(!QuestEligibility.Evaluate(candidate,quest,quests).Eligible) return declaration;
        if(definition.Collisions.Any(id=>quests.TryGet(id,out var alternative) &&
            alternative is {Grade:"[epic]",RewardType:"[item]",Definition:{JobChange:0 or 1}} &&
            alternative.Rewards is [{Type:0,Text:null,Value:0},{Type:0,Text:null,Value:0}] &&
            QuestEligibility.Evaluate(current,alternative,quests).Eligible &&
            (alternative.Definition.JobChange==0 || QuestEligibility.TransferredStoryAlternative(alternative,quests,current.Job))))
            return declaration;
        // 主线只登记完成，不执行职业奖励；独立补充任务仍按实际职业状态接取。
        return declaration with {RewardType="[item]",Rewards=[],Definition=definition with {JobChange=0,GrowType=-1,TargetCharacters=[]}};
    }

    static bool SkipAreaDecoration(QuestField field) => field.Children.IsEmpty && field.Name switch
    {
        "mail time" => field.Values is [{ Type:0,Text:null,Value:>=0 }],
        "mail title" or "mail contents" => field.Values is [{ Type:6,Text:not null }],
        "npc visivility not revert" or "episode town use" => field.Values.IsEmpty,
        "synchronization account quest" => field.Values is [{ Type:0,Text:null,Value:0 or 1 }],
        "first exposed by npc" => field.Values is [{ Type:0,Text:null,Value:0 or 1 }],
        _ => false
    };

    static void ValidateManual(StoryQuest quest, QuestCatalog quests)
    {
        QuestCoverage.RequireOrdinaryDeclarations(quest);
        var definition = quest.Definition!;
        if (!QuestCoverage.HasRequiredReferences(quest,quests)) throw new InvalidDataException("手册任务引用不完整: " + quest.Id);
        if (QuestAccountPolicy.SharedCompletion(quest)) throw new NotSupportedException("手册账号任务事务尚未接入: " + quest.Id);
        if (definition.JobChange != 0) throw new NotSupportedException("手册不代替职业变化: " + quest.Id);
        if (quest.RewardType is not (null or "[item]")) throw new NotSupportedException("手册专用状态尚未接入: " + quest.Id + ": " + quest.RewardType);
        // 不发普通奖励或扣普通材料；特殊任务物品由冻结效果计划核算。
        _ = QuestFixedRewardDefinition.Parse(quest,allowGold:true);
        _ = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition.SelectRewards });
        _ = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition.AcceptanceRewards });
        if (quest.Condition is "[seeking]" or "[seek n meet npc]" && !QuestLifecyclePlan.ValidSeeking(quest))
            throw new InvalidDataException("手册寻物声明不完整: " + quest.Id);
    }

    public static bool TryCreate(Character expected, uint parentKey, QuestCatalog quests, QuestConnectionCatalog connections,
        out QuestQuickClearPlan? plan, out string reason,TownCatalog? towns = null)
    {
        plan = null;
        try
        {
            if (parentKey > int.MaxValue || !connections.TryGet((int)parentKey,out var unit) ||
                unit.QuestionQuestId is not { } questionId || unit.QuickClear is not { } quick)
                throw new InvalidDataException("parent key 没有完整可用的问题及 quick clear 声明");
            if (!quests.TryGet(questionId,out var question) || !QuestQuestionProgress.TryCount(question,out int count) ||
                quick.Answer < 0 || quick.Answer >= count)
                throw new InvalidDataException("question 定义或零基跳过答案不符");
            var activeRows = expected.ActiveMainQuests.Where(row => row.QuestId == questionId).Take(2).ToArray();
            if (activeRows is not [var active] || active.AcceptanceId == Guid.Empty || active.Slot >= 30 ||
                active.ConditionState != QuestQuestionProgress.Unanswered && active.ConditionState >= count ||
                QuestEligibility.Completed(expected).Contains(questionId))
                throw new InvalidDataException("真实 question 未接取、接取身份无效或已完成");
            var eligible = QuestEligibility.Evaluate(expected with
            {
                ActiveMainQuests = expected.ActiveMainQuests.Where(row => row != active).ToArray()
            },question,quests);
            if (!eligible.Eligible) throw new InvalidDataException("question 当前资格不符: " + eligible.Reason);
            if (expected.QuestAnswers.TryGetValue(questionId,out byte saved) && saved != quick.Answer ||
                expected.AccountQuestAnswers.TryGetValue(questionId,out byte accountSaved) && accountSaved != quick.Answer)
                throw new InvalidDataException("已有持久问题答案不能由跳过覆盖");
            if (unit.Sequence.IsDefaultOrEmpty || unit.Sequence.Select(row => row.QuestId).Distinct().Count() != unit.Sequence.Length ||
                unit.Sequence.Count(row => row.QuestId == questionId) != 1)
                throw new InvalidDataException("sequence 缺少唯一 question 或含重复任务引用");
            var sequence = unit.Sequence.Select(row => row.QuestId).ToArray();
            int start = Array.IndexOf(sequence,quick.StartQuestId);
            int end = Array.IndexOf(sequence,quick.EndQuestId);
            if (start < 0 || end < start) throw new InvalidDataException("quick clear 起止任务不在合法原序区间");
            // 按 sequence 的端点位置裁剪是资源推断；不是已恢复的原服遍历规则，也不按数字范围或依赖图扩展。
            var interval = unit.Sequence.Skip(start).Take(end-start+1).Select(row => quests.Get(row.QuestId)).ToImmutableArray();
            if(question.Grade!="[epic]" || interval.Any(quest=>quest.Grade!="[epic]" &&
                !(quest.Grade=="[sub]" && quest.MainQuest is { } parent && interval.Prepend(question)
                    .Any(main=>main.Id==parent && main.Grade=="[epic]" && main.IntData.Contains(quest.Id)))))
                throw new NotSupportedException("主线章节跳过不能完成独立支线、外传或普通任务");
            if (!quests.TryGet(quick.MarkerQuestId,out var marker) || marker.Definition is not { Diagnostics.Length: 0 } ||
                marker.Id == questionId || interval.Any(quest => quest.Id == marker.Id))
                throw new InvalidDataException("显式 marker 定义缺失或与实际完成范围重叠");
            if (!QuestRelationProgress.IsCascadeMarker(marker))
                throw new NotSupportedException("级联标记带有未确认的账号、职业或奖励行为");
            var markerUnknown = QuestCoverage.Unconfirmed(marker).Where(name => name != "synchronization account quest").ToArray();
            if (markerUnknown.Length != 0)
                throw new NotSupportedException("marker 声明尚未确认: " + marker.Id + ": " + string.Join(",",markerUnknown));
            Validate(question,quick.MarkerQuestId,quests);
            var completed = QuestEligibility.Completed(expected);
            var intervalCharacter=expected with {CompletedQuests=expected.CompletedQuests.Append(question.Id).Distinct().ToArray()};
            foreach (var quest in interval)
            {
                Validate(SkipMainDeclaration(intervalCharacter,quest,quests,areaQuest:false),quick.MarkerQuestId,quests);
                if (quest.Condition == "[question]" && quest.Id != questionId && !completed.Contains(quest.Id))
                    throw new NotSupportedException("区间内问题尚无持久回答，不能猜造分支: " + quest.Id);
                intervalCharacter=intervalCharacter with {CompletedQuests=intervalCharacter.CompletedQuests.Append(quest.Id).Distinct().ToArray()};
            }
            // marker按完成消费者归档；账号同步资格与普通AccountQuest领奖分别保存。
            var after = expected with
            {
                CompletedQuests = expected.CompletedQuests.Concat(interval.Select(quest => quest.Id)).Append(question.Id).Append(marker.Id).Distinct().ToArray(),
                QuestAnswers = expected.QuestAnswers.SetItem(question.Id,checked((byte)quick.Answer))
            };
            var areaQuests = CompleteAreaQuests(after,quests,towns,interval.Select(quest => quest.MinimumLevel).Append(question.MinimumLevel).Max());
            plan = new(expected,unit,active,question,marker,interval,checked((byte)quick.Answer),quests,areaQuests);
            reason = "显式原序区间可无奖励跳过";
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            reason = error.Message;
            return false;
        }
    }

    static HashSet<int> AreaQuestScope(QuestCatalog quests,TownCatalog? towns)
    {
        var scope = towns?.AccessQuestIds.Where(id => quests.TryGet(id,out _)).ToHashSet() ?? [];
        var pending = new Queue<int>(scope);
        while (pending.TryDequeue(out int id))
        {
            var quest = quests.Get(id);
            foreach (int prerequisite in (quest.Definition?.PrerequisiteGroups.SelectMany(group => group) ?? [])
                .Concat(quest.Definition?.RequiredAnswers.Select(answer => answer.QuestId) ?? []))
                if (quests.TryGet(prerequisite,out _) && scope.Add(prerequisite)) pending.Enqueue(prerequisite);
        }
        return scope;
    }

    static ImmutableArray<StoryQuest> CompleteAreaQuests(Character expected,QuestCatalog quests,TownCatalog? towns,int cutoff)
    {
        var access = AreaQuestScope(quests,towns);
        var completed = QuestEligibility.Completed(expected);
        var added = ImmutableArray.CreateBuilder<StoryQuest>();
        // 资源形状只在本次规划内复用；职业改写后的完整声明分别验证，不缓存角色资格。
        var areaDeclarations = new Dictionary<int, StoryQuest>();
        var validation = new Dictionary<StoryQuest, bool>();
        var candidates = access.Select(quests.Get).Where(quest => quest.Grade == "[epic]" && !completed.Contains(quest.Id) &&
            quest.MinimumLevel >= 1 && quest.MinimumLevel <= cutoff && quest.Definition is { ExposedByNpc: true }).ToList();
        int snapshotAdded = -1;
        int[] completedSnapshot = [];
        bool progressed;
        do
        {
            progressed = false;
            for (int at = 0; at < candidates.Count; at++)
            {
                var quest = candidates[at];
                int id = quest.Id;
                var definition = quest.Definition!;
                if (definition.PrerequisiteGroups.Length != 0 && !definition.PrerequisiteGroups.Any(group =>
                    group.Length != 0 && group.All(required => quests.TryGet(required,out _) && completed.Contains(required)))) continue;
                // 完成集合不变时共用只读数组；新增任务后立即重建，保持原合并顺序。
                if (snapshotAdded != added.Count)
                {
                    completedSnapshot = expected.CompletedQuests.Concat(added.Select(row => row.Id)).Distinct().ToArray();
                    snapshotAdded = added.Count;
                }
                var current = expected with
                {
                    CompletedQuests = completedSnapshot,
                    ActiveMainQuests = expected.ActiveMainQuests.Where(row => row.QuestId != id && !completed.Contains(row.QuestId)).ToArray()
                };
                if (!areaDeclarations.TryGetValue(id,out var areaDeclaration))
                    areaDeclarations[id] = areaDeclaration = SkipAreaDeclaration(quest);
                var declaration = SkipMainDeclaration(current,quest,quests,areaQuest:true,areaDeclaration);
                if (!validation.TryGetValue(declaration,out bool valid))
                    validation[declaration] = valid = CanSkipManual(declaration,quests,areaQuest:true);
                if (!valid) continue;
                // 章节跳过不能猜造范围外的问答；只有既有持久回答才能推进该解锁链。
                if (quest.Condition == "[question]" &&
                    (!current.QuestAnswers.TryGetValue(id,out byte answer) || !QuestQuestionProgress.TryCount(quest,out int count) ||
                        answer >= count || !QuestQuestionProgress.OrdinaryChoice(quest,answer,quests)) ||
                    !QuestEligibility.Evaluate(current,declaration,quests).Eligible) continue;
                completed.Add(id); added.Add(quest);
                candidates.RemoveAt(at--);
                progressed = true;
            }
        } while (progressed);
        return added.ToImmutable();
    }

    static void Validate(StoryQuest quest, int markerId, QuestCatalog quests)
    {
        var definition = quest.Definition ?? throw new InvalidDataException("跳过任务没有完整定义: " + quest.Id);
        if (definition.Diagnostics.Length != 0) throw new InvalidDataException("跳过任务定义不可用: " + quest.Id + ": " + string.Join(",",definition.Diagnostics));
        if (QuestAccountPolicy.SharedCompletion(quest)) throw new NotSupportedException("跳过范围包含账号任务: " + quest.Id);
        if (definition.JobChange != 0) throw new NotSupportedException("跳过范围包含职业变化: " + quest.Id);
        if (quest.Condition is "[seeking]" or "[seek n meet npc]" && !QuestLifecyclePlan.ValidSeeking(quest))
            throw new InvalidDataException("跳过寻物声明不完整: " + quest.Id);
        if (quest.RewardType is not (null or "[item]")) throw new NotSupportedException("跳过范围包含特殊奖励: " + quest.Id + ": " + quest.RewardType);
        if (definition.NpcVisibility.Any(row =>
            !(row.Condition is "[accept]" or "[clear]" && row.Visibility is "[show]" or "[hide]") &&
            !(row.Condition == "[clear]" && row.Visibility == "[delete]")))
            throw new NotSupportedException("跳过任务NPC可见性声明尚未确认: " + quest.Id);
        foreach (var field in definition.Fields.Where(field => field.Name is "quest title noti" or "solve video" or "casecade clear"))
        {
            bool valid = field.Name switch
            {
                "quest title noti" => QuestCoverage.IsTitleNotification(field),
                "solve video" => field.Children.Length == 0 && field.Values is [{ Type: 6, Text: not null }],
                "casecade clear" => field.Children.Length == 0 && field.Values is [{ Type: 0, Text: null } value] && value.Value == markerId,
                _ => false
            };
            if (!valid) throw new NotSupportedException("跳过声明不符合已普查形状: " + quest.Id + ": " + field.Name);
        }
        // 无奖励跳过保留完整显示声明，复用已确认的公共形状和引用合同。
        var unknown = QuestCoverage.Unconfirmed(quest);
        if (unknown.Length != 0) throw new NotSupportedException("跳过任务声明尚未确认: " + quest.Id + ": " + string.Join(",",unknown));
        if (!QuestCoverage.HasOrdinaryDeclarations(quest)) throw new NotSupportedException("跳过任务有未确认的分类、例外或属性: " + quest.Id);
        if (!QuestCoverage.HasRequiredReferences(quest,quests) || definition.RelationQuests.Any(id => !quests.TryGet(id,out _)))
            throw new InvalidDataException("跳过任务必需引用缺失: " + quest.Id);
        _ = QuestFixedRewardDefinition.Parse(quest,allowGold: true);
        _ = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition.SelectRewards });
        _ = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition.AcceptanceRewards });
    }
}

internal sealed record QuestQuickClearCommit(Character Character, QuestQuickClearPlan Plan, bool Duplicate, bool ProjectionCurrent);
