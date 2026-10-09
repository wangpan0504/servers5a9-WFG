using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Quests;

internal readonly record struct QuestSkipAnswer(uint QuestId, uint Answer);
internal sealed class QuestTicketExpiredException() : InvalidOperationException("任务券在提交前已到期") { }
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

// 无普通奖励的跳过规划；任务券附带实际消费及资源授权的槽位/逐级觉醒状态，不拥有在线角色状态。
internal sealed class QuestQuickClearPlan
{
    readonly QuestConnectionUnit? unit;
    readonly ActiveMainQuest? activeQuestion;
    readonly StoryQuest? question;
    readonly StoryQuest? marker;

    QuestQuickClearPlan(Character expected, QuestConnectionUnit unit, ActiveMainQuest active,
        StoryQuest question, StoryQuest marker, ImmutableArray<StoryQuest> interval, byte answer, QuestCatalog quests)
    {
        Expected = expected; EquipmentSlotFlagsAfter = expected.EquipmentSlotFlags; TargetLearning=expected.Learning;
        this.unit = unit; activeQuestion = active; this.question = question; this.marker = marker;
        OperationId = active.AcceptanceId;
        Interval = interval; Answer = answer;
        // V31完成消费者会把终点casecade关联marker加入当前角色完成集；与普通账号任务分开保存。
        var scope = interval.Select(quest => quest.Id).Prepend(question.Id).Append(marker.Id).ToHashSet();
        ScopeQuestIds = scope.Order().ToImmutableArray();
        var completed = QuestEligibility.Completed(expected);
        NewCompletedQuestIds = scope.Where(id => !completed.Contains(id)).Order().ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Concat(NewCompletedQuestIds).Distinct().Order().ToImmutableArray();
        AnswersAfter = expected.QuestAnswers.SetItem(question.Id,answer);
        AccountCompletedAfter = expected.AccountCompletedQuests.ToImmutableArray();
        AccountAnswersAfter = expected.AccountQuestAnswers;
        RemovedActive = expected.ActiveMainQuests.Where(row => scope.Contains(row.QuestId)).ToImmutableArray();
        var remaining = QuestRelationProgress.Refresh(expected.ActiveMainQuests.Where(row => !scope.Contains(row.QuestId)).ToArray(),
            CompletedAfter.Concat(AccountCompletedAfter).ToArray(),quests);
        ActiveAfter = QuestStateProgress.Refresh(remaining,expected with
        {
            CompletedQuests = CompletedAfter.ToArray(), QuestAnswers = AnswersAfter
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
    }

    QuestQuickClearPlan(Character expected, Guid operationId, int cutoff, ImmutableArray<StoryQuest> scope,
        ImmutableDictionary<int,byte> answers, ImmutableArray<int> questions, QuestCatalog quests,QuestTicketSource? ticket = null,
        byte? equipmentSlots = null,byte? profession = null,CharacterLearningState? learning = null)
    {
        Expected = expected; OperationId = operationId; IsManual = ticket is null; Ticket = ticket; CutoffLevel = cutoff;
        EquipmentSlotFlagsAfter = equipmentSlots ?? expected.EquipmentSlotFlags;
        ProfessionGrowType=profession; TargetLearning=learning ?? expected.Learning;
        Interval = scope; ManualAnswers = answers; PendingQuestions = questions;
        ScopeQuestIds = scope.Select(row => row.Id).Distinct().Order().ToImmutableArray();
        var completed = QuestEligibility.Completed(expected);
        NewCompletedQuestIds = ScopeQuestIds.Where(id => !completed.Contains(id)).ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Concat(NewCompletedQuestIds).Distinct().Order().ToImmutableArray();
        AnswersAfter = expected.QuestAnswers.SetItems(answers);
        AccountCompletedAfter = expected.AccountCompletedQuests.ToImmutableArray();
        AccountAnswersAfter = expected.AccountQuestAnswers;
        RemovedActive = expected.ActiveMainQuests.Where(row => ScopeQuestIds.Contains(row.QuestId)).ToImmutableArray();
        var retained = expected.ActiveMainQuests.Where(row => !ScopeQuestIds.Contains(row.QuestId)).ToArray();
        if (ticket is not null) retained = QuestInventoryProgress.Refresh(retained,ticket.Inventory.After,quests,ticket.UtcSeconds).ToArray();
        var remaining = QuestRelationProgress.Refresh(retained,
            CompletedAfter.Concat(AccountCompletedAfter).ToArray(),quests);
        ActiveAfter = QuestStateProgress.Refresh(remaining,expected with
        {
            CompletedQuests = CompletedAfter.ToArray(), QuestAnswers = AnswersAfter,EquipmentSlotFlags=EquipmentSlotFlagsAfter,
            GrowType=profession ?? expected.GrowType,Learning=TargetLearning
        },quests);
        Snapshot = JsonSerializer.Serialize(new
        {
            OperationId, Kind = ticket is null ? "Manual" : "Ticket", CutoffLevel, ScopeQuestIds,
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
    }

    public Character Expected { get; }
    public byte EquipmentSlotFlagsAfter { get; }
    public byte? ProfessionGrowType { get; }
    public CharacterLearningState TargetLearning { get; }
    // 1714保留真实question合同；手册与任务券不制造question或普通接取身份。
    public QuestConnectionUnit Unit => unit ?? throw new InvalidOperationException("手册或任务券没有旅程单元");
    public ActiveMainQuest ActiveQuestion => activeQuestion ?? throw new InvalidOperationException("手册或任务券没有普通问题接取槽");
    public Guid AcceptanceId => activeQuestion?.AcceptanceId ?? Guid.Empty;
    public StoryQuest Question => question ?? throw new InvalidOperationException("手册或任务券没有单一问题任务");
    public byte Answer { get; }
    public StoryQuest MarkerDefinition => marker ?? throw new InvalidOperationException("手册或任务券没有单一旅程标记");
    public int MarkerQuestId => MarkerDefinition.Id;
    public bool IsManual { get; }
    public QuestTicketSource? Ticket { get; }
    public bool IsTicket => Ticket is not null;
    public bool IsPreparedSkip => IsManual || IsTicket;
    public string OperationKind => IsTicket ? "Ticket" : IsManual ? "Manual" : "QuickClear";
    public Guid OperationId { get; }
    public int? ParentKey => unit?.ParentKey;
    public int? QuestionQuestId => question?.Id;
    public int CutoffLevel { get; }
    public ImmutableDictionary<int,byte> ManualAnswers { get; } = ImmutableDictionary<int,byte>.Empty;
    public ImmutableArray<int> PendingQuestions { get; } = [];
    public ImmutableArray<int> ScopeQuestIds { get; }
    public ImmutableArray<StoryQuest> Interval { get; }
    public ImmutableArray<int> NewCompletedQuestIds { get; }
    public ImmutableArray<int> CompletedAfter { get; }
    public ImmutableDictionary<int,byte> AnswersAfter { get; }
    public ImmutableArray<int> AccountCompletedAfter { get; }
    public ImmutableDictionary<int,byte> AccountAnswersAfter { get; }
    public ImmutableArray<ActiveMainQuest> RemovedActive { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }
    public string Snapshot { get; }

    // Full03手册确认窗：Lv90..94截止89，Lv95截止94，其余截止实际等级减一。
    public static int ManualCutoff(byte level) => level < 95 && level - 1 >= 90 ? 89 : level - 1;

    public static bool TryCreateManual(Character expected, Guid operationId, QuestCatalog quests,
        ImmutableDictionary<int,byte> answers, out QuestQuickClearPlan? plan, out string reason)
        => TryCreateRange(expected,operationId,quests,answers,null,out plan,out reason);

    public static bool TryCreateTicket(Character expected,Guid operationId,QuestCatalog quests,QuestTicketSource ticket,
        ImmutableDictionary<int,byte> answers,out QuestQuickClearPlan? plan,out string reason,SkillLearningCatalog? skills=null)
    {
        if (ticket.Definition.AwakeningStage!=0)
            return TryCreateAwakeningTicket(expected,operationId,quests,ticket,answers,skills,out plan,out reason);
        if (ticket.Definition.ActionIndex==QuestTicketDefinition.SpecifiedAction)
            return TryCreateSpecifiedTicket(expected,operationId,quests,ticket,answers,out plan,out reason);
        if (ticket.Definition.ActionIndex==QuestTicketDefinition.Action)
            return TryCreateRange(expected,operationId,quests,answers,ticket,out plan,out reason);
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
        if (!QuestCoverage.HasRequiredReferences(quest,quests) || quest.Definition is not { AccountQuest:false } definition ||
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
                if (!QuestCoverage.HasRequiredReferences(quest,quests) || quest.Definition is not { AccountQuest:false,JobChange:0 } ||
                    QuestCycle.Daily(quest) || QuestCycle.Repeat(quest) || quest.Condition=="[question]")
                    throw new NotSupportedException("指定券目标需要尚未接入的引用、账号、周期、职业或问题事务: "+id);
                if (quest.RewardType is not (null or "[item]" or "[slot expansion]") || quest.Definition.Has("casecade clear"))
                    throw new NotSupportedException("指定券目标的专用状态或级联尚未接入: "+id);
                if (quest.RewardType=="[slot expansion]")
                {
                    if (!QuestCharacterReward.TryScalar(quest,out _)) throw new InvalidDataException("指定券槽位奖励形状无效: "+id);
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
        ImmutableDictionary<int,byte> answers,QuestTicketSource? ticket,out QuestQuickClearPlan? plan,out string reason)
    {
        plan = null;
        try
        {
            // external consumable_clear.go:25-29明确普通券包含当前等级的epic/normal；手册只到低一级。
            int cutoff = ticket is null ? ManualCutoff(expected.Level) : expected.Level;
            if (expected.Id == 0 || operationId == Guid.Empty || expected.Job >= 16 || expected.Level is < 1 or > 95 ||
                ticket is null && expected.Level < 2 || ticket is not null &&
                (!ReferenceEquals(ticket.Inventory.Before,expected.Inventory) || expected.Inventory.Find(ticket.Item.Position) != ticket.Item))
                throw new InvalidDataException("手册需要实际角色、操作身份及有效截止等级");
            var ordered = quests.All.ToArray();
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
                    if (!(quest.Grade == "[epic]" || ticket is not null && quest.Grade is (null or "" or "[normal]")) ||
                        quest.MainQuest is not null || quest.MinimumLevel is < 1 ||
                        quest.MinimumLevel > cutoff || completed.Contains(quest.Id) || quest.Definition is not { ExposedByNpc: true }) continue;
                    var current = Current();
                    if (!QuestEligibility.Evaluate(current with
                        { ActiveMainQuests = current.ActiveMainQuests.Where(row => row.QuestId != quest.Id).ToArray() },quest,quests).Eligible) continue;
                    // 专用或坏定义隔离，不写其完成记录；后继仍须满足真实前置，不能阻断独立普通主线。
                    if (!CanSkipManual(quest,quests)) continue;
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
            if (scope.Count == 0 && questions.Count == 0) throw new InvalidDataException("截止等级内没有可用手册主线");
            plan = new(expected,operationId,cutoff,scope.ToImmutableArray(),answers,questions.ToImmutableArray(),quests,ticket);
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
                        child.MinimumLevel is < 1 || child.MinimumLevel > cutoff || completed.Contains(id)) continue;
                    // 显式反向父引用授权子范围；只移除已校验的父入口要求，不伪造普通接取槽。
                    var current = Current();
                    if (!QuestEligibility.Evaluate(current with
                        { ActiveMainQuests = current.ActiveMainQuests.Where(row => row.QuestId != id).ToArray() },child with { MainQuest = null },quests).Eligible) continue;
                    if (!CanSkipManual(child,quests) || child.Condition == "[question]") continue;
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

    static bool CanSkipManual(StoryQuest quest,QuestCatalog quests)
    {
        try { ValidateManual(quest,quests); return true; }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException) { return false; }
    }

    static void ValidateManual(StoryQuest quest, QuestCatalog quests)
    {
        QuestCoverage.RequireOrdinaryDeclarations(quest);
        var definition = quest.Definition!;
        if (!QuestCoverage.HasRequiredReferences(quest,quests)) throw new InvalidDataException("手册任务引用不完整: " + quest.Id);
        if (definition.AccountQuest) throw new NotSupportedException("手册账号任务事务尚未接入: " + quest.Id);
        if (definition.JobChange != 0) throw new NotSupportedException("手册不代替职业变化: " + quest.Id);
        if (quest.RewardType is not (null or "[item]")) throw new NotSupportedException("手册专用状态尚未接入: " + quest.Id + ": " + quest.RewardType);
        // 手册不发普通奖励/赠品，也不消耗寻物材料；仅校验声明，不套1714区间的持有域限制。
        _ = QuestFixedRewardDefinition.Parse(quest,allowGold:true);
        _ = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition.SelectRewards });
        _ = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition.AcceptanceRewards });
        if (quest.Condition is "[seeking]" or "[seek n meet npc]" && !QuestLifecyclePlan.ValidSeeking(quest))
            throw new InvalidDataException("手册寻物声明不完整: " + quest.Id);
    }

    public static bool TryCreate(Character expected, uint parentKey, QuestCatalog quests, QuestConnectionCatalog connections,
        out QuestQuickClearPlan? plan, out string reason)
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
            foreach (var quest in interval)
            {
                Validate(quest,quick.MarkerQuestId,quests);
                if (quest.Condition == "[question]" && quest.Id != questionId && !completed.Contains(quest.Id))
                    throw new NotSupportedException("区间内问题尚无持久回答，不能猜造分支: " + quest.Id);
            }
            // marker按匹配完成消费者加入本角色；其跨角色同步不套用普通AccountQuest规则。
            plan = new(expected,unit,active,question,marker,interval,checked((byte)quick.Answer),quests);
            reason = "显式原序区间可无奖励跳过";
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            reason = error.Message;
            return false;
        }
    }

    static void Validate(StoryQuest quest, int markerId, QuestCatalog quests)
    {
        var definition = quest.Definition ?? throw new InvalidDataException("跳过任务没有完整定义: " + quest.Id);
        if (definition.Diagnostics.Length != 0) throw new InvalidDataException("跳过任务定义不可用: " + quest.Id + ": " + string.Join(",",definition.Diagnostics));
        if (definition.AccountQuest) throw new NotSupportedException("跳过范围包含账号任务: " + quest.Id);
        if (definition.JobChange != 0) throw new NotSupportedException("跳过范围包含职业变化: " + quest.Id);
        if (definition.AcceptanceRewards.Length != 0) throw new NotSupportedException("跳过范围包含接取赠品: " + quest.Id);
        if (quest.Condition is "[seeking]" or "[seek n meet npc]") throw new NotSupportedException("跳过范围包含寻物消耗: " + quest.Id);
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
    }
}

internal sealed record QuestQuickClearCommit(Character Character, QuestQuickClearPlan Plan, bool Duplicate, bool ProjectionCurrent);
