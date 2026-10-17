using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal enum QuestLifecycleAction { Accept, Giveup, Condition }
internal enum QuestLifecycleOutcome { Rejected, Committed, Unchanged }
internal readonly record struct QuestClientCondition(byte? Channel, byte Value, byte Reserved);
internal sealed record QuestLifecycleCommit(Character Character, QuestLifecyclePlan Plan, bool Duplicate, bool ProjectionCurrent);
internal sealed record QuestSceneEvent(Guid OperationId, DfoServer.Game.Dungeons.DungeonRunIdentity Identity,
    int DungeonId, int MapId, int TemplateId, int SourceOrdinal, string Fingerprint)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DropSnapshot { get; init; }
}

internal sealed class QuestLifecyclePlan
{
    private QuestLifecyclePlan(Character expected, QuestLifecycleAction action, ushort questId, Guid acceptance,
        ImmutableArray<ActiveMainQuest> active, InventoryPlan inventory, QuestSceneEvent? sceneEvent = null)
    {
        Expected = expected; Action = action; QuestId = questId; AcceptanceId = acceptance;
        ActiveAfter = active; Inventory = inventory; SceneEvent = sceneEvent; OperationId = sceneEvent?.OperationId ?? Guid.NewGuid();
        CompletedAfter = expected.CompletedQuests.ToImmutableArray();
        AccountCompletedAfter = expected.AccountCompletedQuests.ToImmutableArray();
    }

    public Character Expected { get; }
    public QuestLifecycleAction Action { get; }
    public ushort QuestId { get; }
    public Guid AcceptanceId { get; }
    public Guid OperationId { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }
    public InventoryPlan Inventory { get; }
    public QuestSceneEvent? SceneEvent { get; }
    internal PreparedDungeonPassiveClear? SceneClear { get; private init; }
    internal SinglePlayerDungeonRun? SceneClearRun { get; private init; }
    public ImmutableArray<int> CompletedAfter { get; private init; }
    public ImmutableArray<int> AccountCompletedAfter { get; private init; }

    internal static QuestLifecyclePlan PayImageCommunication(Character expected, ushort questId, Guid acceptance,
        uint charge, QuestCatalog quests, DateTimeOffset now)
    {
        if (acceptance == Guid.Empty || !expected.ActiveMainQuests.Any(row => row.QuestId == questId && row.AcceptanceId == acceptance) ||
            expected.Inventory.Gold < charge)
            throw new InvalidDataException("通讯装置需要本次活动接取和足额金币");
        var inventory = expected.Inventory with { Gold = expected.Inventory.Gold - charge };
        // 金币也是其它寻物任务的材料；沿共享库存重算，不让本次通讯费用直接完成会面。
        return new(expected, QuestLifecycleAction.Condition, questId, acceptance,
            QuestInventoryProgress.Refresh(expected.ActiveMainQuests, inventory, quests, now.ToUnixTimeSeconds()),
            new(expected.Inventory, inventory, "quest_image_communication"));
    }

    internal static QuestLifecyclePlan? ObservePassive(Character expected, QuestCatalog quests,
        DfoServer.Game.Dungeons.DungeonEntryPlan entry, QuestSceneEvent sceneEvent, QuestDropPlan? drops = null,
        PreparedDungeonPassiveClear? sceneClear = null, SinglePlayerDungeonRun? run = null)
    {
        if (sceneEvent.OperationId == Guid.Empty || sceneEvent.Identity.CharacterId != expected.Id ||
            sceneEvent.Identity.QuestId != entry.QuestId || sceneEvent.DungeonId != entry.DungeonId ||
            sceneEvent.MapId <= 0 || sceneEvent.TemplateId <= 0 || sceneEvent.SourceOrdinal < 0 || sceneEvent.Fingerprint.Length != 64)
            throw new InvalidDataException("被动物件事件需要真实实例、地图和资源行身份");
        var after = QuestHuntProgress.PassiveDeath(expected.ActiveMainQuests,entry,quests,sceneEvent.TemplateId);
        if (sceneClear is not null)
        {
            if (run is null || !run.IsPendingPassiveClear(sceneClear) || sceneClear.Source.OperationId != sceneEvent.OperationId ||
                sceneClear.Source.Identity != sceneEvent.Identity || sceneClear.Source.TemplateId != sceneEvent.TemplateId ||
                sceneClear.Source.SourceOrdinal != sceneEvent.SourceOrdinal)
                throw new InvalidDataException("场景清图必须来自原实例冻结的物件终末");
            var clear = sceneClear.Result.ClearFact!;
            after = after.Select(row => row.ConditionState > 0 && QuestHuntProgress.Bound(row,entry) &&
                quests.TryGet(row.QuestId,out var quest) && QuestCoverage.HasOrdinaryDeclarations(quest) &&
                (QuestClearEligibility.IsSingleMap(quest) && row.ConditionState == 1 && quest.IntData[0] == clear.ClearMap!.Id &&
                    (quest.DungeonIndex is null or < 0 || quest.DungeonIndex == entry.DungeonId) ||
                 QuestEligibility.InspectResource(quest).Entry is QuestResourceEntry.Ordinary or QuestResourceEntry.Dialogue or QuestResourceEntry.Creature &&
                    QuestDungeonClearProgress.Matches(quest,entry,run) && QuestDungeonClearProgress.ValidScalar(quest,row.ConditionState))
                ? row with { ConditionState = row.ConditionState - 1 } : row).ToImmutableArray();
        }
        var inventory = drops?.Inventory ?? new(expected.Inventory,expected.Inventory,"quest_passive_death");
        if (drops is not null)
        {
            if (!ReferenceEquals(drops.Expected,expected) || drops.EventId != sceneEvent.OperationId || drops.Identity != sceneEvent.Identity ||
                drops.MapId != sceneEvent.MapId) throw new InvalidDataException("被动物件掉落不是原场景事件");
            after = QuestInventoryProgress.Refresh(after,inventory.After,quests,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            sceneEvent = sceneEvent with { DropSnapshot = drops.Snapshot };
        }
        var changed = after.FirstOrDefault(row => expected.ActiveMainQuests.Any(old => old.AcceptanceId == row.AcceptanceId && old.ConditionState != row.ConditionState));
        if (changed is null && drops?.Outcomes.Length > 0)
            changed = after.First(row => row.AcceptanceId == drops.Outcomes[0].AcceptanceId);
        // 迷宫物件条件可以独立于任务目标；仍借用本实例真实接取身份保存场景事件，不伪造任务推进。
        if (changed is null && entry.Maze.ClearConditions is [{ Type: "destroy object", Count: 1 } condition] &&
            condition.TargetId == sceneEvent.TemplateId)
            changed = after.SingleOrDefault(row => row.QuestId == entry.RouteQuestId && QuestHuntProgress.Bound(row, entry));
        return changed is null ? null : new(expected,QuestLifecycleAction.Condition,changed.QuestId,changed.AcceptanceId,
            after,inventory,sceneEvent) { SceneClear = sceneClear, SceneClearRun = run };
    }

    public static QuestLifecyclePlan Accept(Character expected, StoryQuest quest, QuestCatalog catalog,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        var eligibility = QuestEligibility.Evaluate(expected, quest, catalog);
        if (!eligibility.Eligible || !eligibility.Visible && !QuestRelationProgress.DialogueChild(quest, catalog) &&
            !QuestRelationProgress.ThresholdParent(quest,catalog) && !QuestHiddenEntry.Direct(quest,catalog))
            throw new InvalidDataException(eligibility.Reason);
        QuestCoverage.RequireOrdinaryDeclarations(quest);
        if (QuestCycle.Daily(quest) && expected.QuestCycleDay != QuestCycle.Day(now))
            throw new InvalidDataException("每日任务需要当前游戏日快照");
        if (expected.ActiveMainQuests.Length >= 30)
            throw new InvalidDataException("30个任务槽已满");
        uint initial = quest.Condition switch
        {
            // 租赁成功事务才归零；这是本窄用例的未达成模型，不是任意custom的初值。
            "[custom quest]" when QuestRentalProgress.Supports(quest) => 1,
            "[meet npc]" when quest.SubType == -1 && quest.IntData.Count == 1 && quest.IntData[0] > 0 => 1,
            // Full03成功ACK31的type4/sub1立即触发event4归零，不读取实际点击NPC。
            "[meet npc]" when QuestStateProgress.Type(quest) == 4 => 0,
            "[reach the range]" when QuestClientSceneProgress.TownRange(quest) => 1,
            "[look cinematic]" when QuestClientSceneProgress.Cinematic(quest,out _,out _) || QuestClientSceneProgress.DungeonCinematic(quest) => 1,
            "[question]" when QuestQuestionProgress.TryCount(quest,out _) => QuestQuestionProgress.Unanswered,
            "[seeking]" when SeekingSubtype(quest) && ValidSeeking(quest) => HasMaterials(expected, quest, now.ToUnixTimeSeconds()) ? 0u : 1u,
            "[seek n meet npc]" when quest.SubType == -1 && ValidSeeking(quest) => 512u | MaterialState(expected.Inventory, quest, now.ToUnixTimeSeconds()),
            "[clear map]" when QuestClearEligibility.IsSingleMap(quest) => 1,
            "[condition under clear]" or "[clear a dungeon]" when QuestDungeonClearProgress.Supports(quest) => QuestDungeonClearProgress.Initial(quest),
            "[hunt enemy]" or "[hunt monster]" => QuestHuntProgress.Initial(quest),
            "[quest clear]" when QuestRelationProgress.SequentialParent(quest,catalog) => QuestRelationProgress.Initial(quest,catalog,expected.CompletedQuests),
            "[clear multi quest]" when QuestRelationProgress.ThresholdParent(quest,catalog) =>
                QuestRelationProgress.Initial(quest,catalog,QuestEligibility.Completed(expected)),
            // Full03 ACK31的type49分支在打开故事UI前即发送归零33；真实接取事务本身已满足此目标。
            "[play only]" when QuestStateProgress.Type(quest) == 49 => 0,
            _ when QuestStateProgress.Type(quest) is not null => QuestStateProgress.Scalar(expected,quest),
            _ => throw new NotSupportedException("任务条件初值尚未确认: " + quest.Condition)
        };
        var gifts = quest.Definition!.AcceptanceRewards;
        var rows = gifts.Length == 0 ? [] : QuestFixedRewardDefinition.Parse(quest with { Rewards = gifts }).ForJob(expected.Job,expected.GrowType);
        var grant = InventoryRewardGrantService.TryGrantBatch(expected,
            rows.Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray(), items, goldLimits, pieces, now);
        if (!grant.Accepted || grant.Plan is null)
            throw new InvalidDataException("接取赠品无法整批进入背包");
        if (grant.Plan.ChangedSlots.Length > byte.MaxValue || grant.Plan.ChangedSlots.Any(slot => slot.List != InventoryListType.Main) || grant.Plan.ChangedCurrencies.Length != 0 ||
            grant.Plan.ChangedEpicPieces.Length != 0)
            throw new NotSupportedException("接取赠品需要尚未确认的持有域投影");
        if (quest.Condition is "[seeking]" or "[seek n meet npc]")
            initial = quest.Condition == "[seek n meet npc]" ? 512u | MaterialState(grant.Plan.After, quest, now.ToUnixTimeSeconds())
                : HasMaterials(expected with { Inventory = grant.Plan.After }, quest, now.ToUnixTimeSeconds()) ? 0u : 1u;
        byte free = Enumerable.Range(0, 30).Select(at => (byte)at).First(slot => expected.ActiveMainQuests.All(active => active.Slot != slot));
        var activeQuest = new ActiveMainQuest(free, checked((ushort)quest.Id), initial) { AcceptanceId = Guid.NewGuid() };
        return new(expected, QuestLifecycleAction.Accept, activeQuest.QuestId, activeQuest.AcceptanceId,
            QuestInventoryProgress.Refresh(expected.ActiveMainQuests.Append(activeQuest).OrderBy(active => active.Slot).ToArray(),
                grant.Plan.After, catalog, now.ToUnixTimeSeconds()), new(expected.Inventory, grant.Plan.After, "quest_accept"))
        {
            // 两端接取重复任务时清当前完成投影；领奖历史仍按独立接取身份保留。
            CompletedAfter = expected.CompletedQuests.Where(id => !QuestCycle.Repeat(quest) || id != quest.Id).ToImmutableArray(),
            AccountCompletedAfter = expected.AccountCompletedQuests.Where(id => !QuestCycle.Repeat(quest) || !quest.Definition.AccountQuest || id != quest.Id).ToImmutableArray()
        };
    }

    public static QuestLifecyclePlan Giveup(Character expected, StoryQuest quest, QuestCatalog catalog, DateTimeOffset now)
    {
        var accepted = expected.ActiveMainQuests.SingleOrDefault(active => active.QuestId == quest.Id)
            ?? throw new InvalidDataException("任务未接取");
        var remove = new HashSet<int> { quest.Id };
        bool added;
        do
        {
            added = false;
            foreach (var active in expected.ActiveMainQuests)
                if (catalog.TryGet(active.QuestId, out var child) && child.MainQuest is { } parent && remove.Contains(parent))
                    added |= remove.Add(active.QuestId);
        } while (added);
        var definitions = remove.Select(catalog.Get).ToArray();
        if (definitions.Any(value => value.Definition is null || value.Definition.Diagnostics.Length != 0 ||
            value.Definition.CannotGiveup && !(value.Definition.GiveupRule == 2 && value.Definition.JobChange == 1 && value.RewardType == "[grow type]")))
            throw new InvalidDataException("任务或关联子任务禁止放弃");
        var activeAfter = expected.ActiveMainQuests.Where(active => !remove.Contains(active.QuestId)).ToImmutableArray();
        var inventory = ReclaimQuestItems(expected,definitions,activeAfter,catalog,now);
        return new(expected, QuestLifecycleAction.Giveup, accepted.QuestId, accepted.AcceptanceId,
            QuestInventoryProgress.Refresh(activeAfter, inventory, catalog, now.ToUnixTimeSeconds()), new(expected.Inventory, inventory, "quest_giveup"));
    }

    // 父交付关闭未选活动分支；真实完成列表和子奖励保持原义，后续32只确认已提交的关闭结果。
    public static (ImmutableArray<ActiveMainQuest> ActiveAfter, InventoryPlan Inventory, ImmutableArray<ActiveMainQuest> Removed)
        CloseThresholdChildren(Character expected, StoryQuest parent, QuestCatalog catalog, DateTimeOffset now)
    {
        if (!QuestRelationProgress.ThresholdParent(parent,catalog) ||
            QuestRelationProgress.ThresholdScalar(parent,QuestEligibility.Completed(expected)) != 0)
            throw new InvalidDataException("阈值父任务尚未达到真实子完成数量");
        var completed = QuestEligibility.Completed(expected);
        var removed = expected.ActiveMainQuests.Where(row => parent.IntData.Contains(row.QuestId) &&
            !completed.Contains(row.QuestId)).ToImmutableArray();
        var ids = removed.Select(row => (int)row.QuestId).ToHashSet();
        var activeAfter = expected.ActiveMainQuests.Where(row => !ids.Contains(row.QuestId)).ToImmutableArray();
        var inventory = ReclaimQuestItems(expected,removed.Select(row => catalog.Get(row.QuestId)),activeAfter,catalog,now);
        return (QuestInventoryProgress.Refresh(activeAfter,inventory,catalog,now.ToUnixTimeSeconds()),
            new(expected.Inventory,inventory,"quest_giveup"),removed);
    }

    static InventorySnapshot ReclaimQuestItems(Character expected, IEnumerable<StoryQuest> definitions,
        IReadOnlyList<ActiveMainQuest> activeAfter, QuestCatalog catalog, DateTimeOffset now)
    {
        var collect = new HashSet<int>();
        foreach (var value in definitions)
        {
            if (value.Condition is "[seeking]" or "[seek n meet npc]" && ValidSeeking(value))
                foreach (int id in value.IntData.Take(MaterialLength(value)).Where((_, at) => at % 2 == 0)) collect.Add(id);
            if (value.Definition!.AcceptanceRewards.Length != 0)
                foreach (var gift in QuestFixedRewardDefinition.Parse(value with { Rewards = value.Definition.AcceptanceRewards }).ForJob(expected.Job,expected.GrowType))
                    collect.Add(gift.ItemId);
        }
        var retained = new Dictionary<int, long>();
        foreach (var active in activeAfter)
        {
            var other = catalog.Get(active.QuestId);
            if (other.Condition is "[seeking]" or "[seek n meet npc]" && ValidSeeking(other))
            {
                var requirements = new Dictionary<int, long>();
                for (int at = 0; at < MaterialLength(other); at += 2)
                    requirements[other.IntData[at]] = requirements.GetValueOrDefault(other.IntData[at]) + other.IntData[at + 1];
                foreach (var requirement in requirements)
                    retained[requirement.Key] = Math.Max(retained.GetValueOrDefault(requirement.Key), requirement.Value);
            }
            else
                foreach (int id in other.IntData.Where(collect.Contains))
                    retained[id] = long.MaxValue;
            if (other.Definition is { AcceptanceRewards.Length: > 0 } definition)
                foreach (var gifts in QuestFixedRewardDefinition.Parse(other with { Rewards = definition.AcceptanceRewards })
                    .ForJob(expected.Job,expected.GrowType).Where(gift => collect.Contains(gift.ItemId)).GroupBy(gift => gift.ItemId))
                    retained[gifts.Key] = Math.Max(retained.GetValueOrDefault(gifts.Key),gifts.Sum(gift => (long)gift.Count));
        }
        var planned = expected.Inventory.Items.ToBuilder();
        foreach (int id in collect)
        {
            long total = planned.Where(item => item.Position.List == InventoryListType.Main && item.Core.Kind == ItemKind.Quest && item.Core.ItemId == id)
                .Where(item => !item.Core.IsExpired(now.ToUnixTimeSeconds())).Sum(item => (long)item.Core.Count);
            long remaining = Math.Max(0, total - retained.GetValueOrDefault(id));
            for (int at = planned.Count - 1; at >= 0; at--)
            {
                var item = planned[at];
                if (item.Position.List != InventoryListType.Main || item.Core.Kind != ItemKind.Quest || item.Core.ItemId != id)
                    continue;
                bool expired = item.Core.IsExpired(now.ToUnixTimeSeconds());
                int count = expired ? item.Core.Count : (int)Math.Min(remaining, item.Core.Count);
                if (count == 0) continue;
                if (!expired) remaining -= count;
                if (count == item.Core.Count) planned.RemoveAt(at);
                else planned[at] = item with { Core = item.Core with { Value = item.Core.Count - count } };
            }
        }
        return expected.Inventory with { Items = planned.ToImmutable() };
    }

    public static QuestLifecyclePlan SetCondition(Character expected, StoryQuest quest, uint scalar)
    {
        var active = expected.ActiveMainQuests.SingleOrDefault(value => value.QuestId == quest.Id)
            ?? throw new InvalidDataException("任务未接取");
        bool observedHunt = quest.Condition is "[hunt enemy]" or "[hunt monster]" && scalar == active.ConditionState && QuestHuntProgress.ValidScalar(quest,scalar);
        bool seekMeet = quest.Condition == "[seek n meet npc]" && ValidSeeking(quest) && scalar <= 1023;
        bool parent = quest.Condition == "[quest clear]" && quest.SubType == 2 && scalar <= quest.IntData.Count ||
            QuestRelationProgress.ThresholdShape(quest) && scalar == QuestRelationProgress.ThresholdScalar(quest,QuestEligibility.Completed(expected));
        bool scene = scalar <= 1 && (QuestClientSceneProgress.TownRange(quest) || QuestClientSceneProgress.Cinematic(quest,out _,out _) ||
            QuestClientSceneProgress.DungeonCinematic(quest));
        bool question = QuestQuestionProgress.TryCount(quest,out int answers) && scalar < answers;
        bool state = QuestStateProgress.Type(quest) is not null && scalar == QuestStateProgress.Scalar(expected,quest);
        bool observedClear = QuestDungeonClearProgress.ValidScalar(quest,scalar) && scalar == active.ConditionState;
        bool observedRental = QuestRentalProgress.Supports(quest) && scalar == 0 && active.ConditionState == 0;
        if (active.AcceptanceId == Guid.Empty || !observedHunt && !seekMeet && !parent && !scene && !question && !state && !observedClear && !observedRental && (scalar > 1 || quest.Condition is not ("[meet npc]" or "[seeking]")))
            throw new InvalidDataException("条件类型或接取身份不符");
        return new(expected, QuestLifecycleAction.Condition, active.QuestId, active.AcceptanceId,
            expected.ActiveMainQuests.Select(value => value.AcceptanceId == active.AcceptanceId ? value with { ConditionState = scalar } : value).ToImmutableArray(),
            new(expected.Inventory, expected.Inventory, "quest_condition"));
    }

    internal static QuestLifecyclePlan? ObserveRental(Character expected, InventoryPlan inventory, QuestCatalog quests, DateTimeOffset now)
    {
        var active = expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == QuestRentalProgress.QuestId);
        if (!quests.TryGet(QuestRentalProgress.QuestId, out var quest) || !QuestRentalProgress.Supports(quest) ||
            QuestEligibility.Completed(expected).Contains(quest.Id) ||
            active is not { ConditionState: 1 } || active.AcceptanceId == Guid.Empty)
            return null;
        if (!ReferenceEquals(inventory.Before, expected.Inventory) || !inventory.Changed ||
            inventory.CharacterPrecondition?.Matches(expected, now.ToUnixTimeSeconds()) != true)
            throw new InvalidDataException("租赁任务需要当前角色的成功库存规划");
        var after = expected.ActiveMainQuests.Select(row => row == active ? row with { ConditionState = 0 } : row).ToArray();
        return new(expected, QuestLifecycleAction.Condition, active.QuestId, active.AcceptanceId,
            QuestInventoryProgress.Refresh(after, inventory.After, quests, now.ToUnixTimeSeconds()), inventory);
    }

    internal static int MaterialLength(StoryQuest quest) => quest.IntData.Count - (quest.Condition == "[seek n meet npc]" ? 1 : 0);

    // #256：黑市EXP的6053/6056/6059声明`seeking / sub type 0`。服务端的寻物完全由int data物品/数量对与库存推导，
    // 参考实现(go-server finishDefinitionSeekingRules/itemPairRulesFromInts)同样不读取sub type；资源形状与已支持的sub type -1寻物一致
    // （单个物品/数量对+怪物掉落行只产出该物品）。因此只对这三个任务的精确形状按sub type -1处理，不放行其它sub type 0寻物。
    internal static readonly int[] HarlemSeekingIds = [6053, 6056, 6059];

    internal static bool SeekingSubtype(StoryQuest quest) => quest.SubType == -1 || quest.SubType == 0 && HarlemSeeking(quest);

    internal static bool HarlemSeeking(StoryQuest quest) => quest is { Condition: "[seeking]", SubType: 0, Grade: "[side]", RewardType: "[item]" } &&
        HarlemSeekingIds.Contains(quest.Id) && quest.MainQuest is null && quest.IntData.Count == 2 && quest.IntData[1] == 3 && ValidSeeking(quest) &&
        quest.Definition is { Diagnostics.Length: 0, Drops.Length: > 0 } definition &&
        definition.Drops.All(row => row.EnemyType == 1 && row.ItemId == quest.IntData[0] && row.DungeonId == 7322 && row.Difficulty == -1 &&
            row.Rate == 100 && row.Count > 0);

    internal static bool ValidSeeking(StoryQuest quest)
    {
        int length = MaterialLength(quest);
        return length > 0 && length % 2 == 0 && quest.IntData.Take(length).Where((_,at) => at % 2 == 0).All(value => value >= 0) &&
            quest.IntData.Take(length).Where((_,at) => at % 2 != 0).All(value => value > 0) &&
            (quest.Condition != "[seek n meet npc]" || quest.IntData[^1] > 0);
    }

    internal static bool HasMaterials(Character character, StoryQuest quest, long utcSeconds)
    {
        return ValidSeeking(quest) && MissingMaterials(character.Inventory, quest, utcSeconds) == 0;
    }

    // type25材料通道按完整库存谓词归零或恢复1；9位通道宽度不是材料数量上限。
    internal static uint MaterialState(InventorySnapshot inventory, StoryQuest quest, long utcSeconds) =>
        MissingMaterials(inventory, quest, utcSeconds) == 0 ? 0u : 1u;

    internal static long MissingMaterials(InventorySnapshot inventory, StoryQuest quest, long utcSeconds)
    {
        if (!ValidSeeking(quest)) throw new InvalidDataException("寻物目标不是完整物品/数量对或目标NPC无效");
        var wanted = new Dictionary<int, long>();
        for (int at = 0; at < MaterialLength(quest); at += 2)
            wanted[quest.IntData[at]] = wanted.GetValueOrDefault(quest.IntData[at]) + quest.IntData[at + 1];
        var counts = new Dictionary<int, long>();
        foreach (var item in inventory.Items)
            if (item.Position.List == InventoryListType.Main && !item.Core.IsExpired(utcSeconds) && wanted.ContainsKey(item.Core.ItemId))
                counts[item.Core.ItemId] = counts.GetValueOrDefault(item.Core.ItemId) + item.Core.Count;
        foreach (int id in wanted.Keys)
            if (CurrencyWallet.RewardKind(id) is { } kind)
                counts[id] = CurrencyWallet.Get(inventory, kind);
        return wanted.Sum(pair => Math.Max(0, pair.Value - counts.GetValueOrDefault(pair.Key)));
    }
}
