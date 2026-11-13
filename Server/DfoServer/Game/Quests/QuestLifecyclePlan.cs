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
internal sealed record QuestReportedClear(Guid AcceptanceId, ushort QuestId, byte QuestSlot, Guid EventId,
    Guid InstanceId, long TaskAttempt, long Epoch, int DungeonId, int MapId);
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
    internal DfoServer.Game.Pvp.PvpRatingSnapshot? PvpRating { get; set; }
    internal InventoryEventPrecondition? RequiredEvent { get; set; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }
    public InventoryPlan Inventory { get; }
    public QuestSceneEvent? SceneEvent { get; }
    internal PreparedDungeonPassiveClear? SceneClear { get; private init; }
    internal SinglePlayerDungeonRun? SceneClearRun { get; private init; }
    internal QuestReportedClear? ReportedClear { get; private init; }
    internal SinglePlayerDungeonRun? ReportedClearRun { get; private init; }

    internal static QuestLifecyclePlan ObserveClientEvent(Character expected, StoryQuest quest, SinglePlayerDungeonRun run)
    {
        var active = expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id);
        if (active is null || !QuestHuntProgress.Bound(active, run.Entry) || !QuestClientObservation.Matches(quest, run))
            throw new InvalidDataException("客户端观察事件需要原接取及已加载正式房间上下文");
        var after = expected.ActiveMainQuests.Select(row => row.AcceptanceId == active.AcceptanceId && row.ConditionState > 0
            ? row with { ConditionState = row.ConditionState - 1 } : row).ToImmutableArray();
        return new(expected, QuestLifecycleAction.Condition, active.QuestId, active.AcceptanceId, after,
            new(expected.Inventory, expected.Inventory, "quest_client_observation"));
    }

    internal static QuestLifecyclePlan ObserveClientClear(Character expected, StoryQuest quest, SinglePlayerDungeonRun run)
    {
        var active = expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id);
        var entry = run.Entry;
        var clear = run.ClearFact;
        if (active is not { ConditionState: 1 } || !QuestHuntProgress.Bound(active, entry) ||
            !QuestDungeonClearProgress.NeedsClientCondition(quest) || !QuestDungeonClearProgress.MatchesScope(quest, entry) ||
            !QuestDungeonClearProgress.MatchServerCondition(quest, entry, run) ||
            run.Phase != SinglePlayerDungeonPhase.Settlement || clear is not { PassiveSource: null, ClearMap: not null } ||
            clear.Identity.CharacterId != expected.Id || clear.Identity.InstanceId == Guid.Empty || clear.ConfirmedDeathEvents.Count == 0 ||
            !entry.IsTerminalMap(clear.ClearMap) || entry.HasFreeCombatConsumption || entry.IsQuestReplay)
            throw new InvalidDataException("客户端统计报告需要原接取、真实终末清图和匹配副本难度");
        var after = expected.ActiveMainQuests.Select(row => row.AcceptanceId == active.AcceptanceId
            ? row with { ConditionState = row.ConditionState - 1 } : row).ToImmutableArray();
        return new(expected, QuestLifecycleAction.Condition, active.QuestId, active.AcceptanceId, after,
            new(expected.Inventory, expected.Inventory, "quest_client_clear"))
        {
            ReportedClearRun = run,
            ReportedClear = new(active.AcceptanceId, active.QuestId, active.Slot, clear.ConfirmedDeathEvents[^1],
                clear.Identity.InstanceId, clear.Identity.TaskAttempt, clear.Identity.Epoch, checked((int)entry.DungeonId), clear.ClearMap.Id)
        };
    }
    internal bool IsPartySceneOnly => QuestId == 0 && AcceptanceId == Guid.Empty && Action == QuestLifecycleAction.Condition &&
        SceneEvent is { } scene && SceneClearRun is { } run && AllowsSceneOnly(run.Entry, scene) &&
        scene.Identity.CharacterId == Expected.Id && !Inventory.Changed && ActiveAfter.SequenceEqual(Expected.ActiveMainQuests) &&
        (SceneClear is { } clear ? QuestClearEligibility.TryValidateScene(run, clear, scene) :
            run.IsPassiveClearCondition(scene.Identity, scene.TemplateId, scene.SourceOrdinal));

    static bool AllowsSceneOnly(DungeonEntryPlan entry, QuestSceneEvent scene) => entry.PartyRoute is not null ||
        entry.RaidEntry is { Kind: DfoServer.Game.Raids.RaidKind.Anton, Phase: 0 } && entry.DungeonId is 212 or 214 &&
        scene.TemplateId == 15467 && entry.BossMap.Id == scene.MapId ||
        entry.RaidEntry is { Kind: DfoServer.Game.Raids.RaidKind.Luke, Phase: 1 } && entry.LukeEnergyObject is { } binding &&
        entry.Maze.MapSpecification?.SingleOrDefault(map => map.Id == scene.MapId && binding.Room.Kind == map.Kind &&
            binding.Room.Position == map.Position && binding.Room.Path == map.Path) is { } map &&
        binding.MatchesEnergy(entry.DungeonId, entry.Maze, map, scene.TemplateId, scene.SourceOrdinal);
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
                (QuestClearEligibility.IsSingleMap(quest) && row.ConditionState == 1 &&
                    QuestClearEligibility.MatchesClearMap(quest, entry, clear) &&
                    QuestClearEligibility.HasRequiredActors(quest,run,sceneClear.Result.Snapshot) &&
                    (quest.DungeonIndex is null or < 0 || quest.DungeonIndex == entry.DungeonId) ||
                 (QuestEligibility.InspectResource(quest).Entry is QuestResourceEntry.Ordinary or QuestResourceEntry.Dialogue or QuestResourceEntry.Creature ||
                    QuestAutoDailyPlan.Supports(quest)) &&
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
        if (changed is null && AllowsSceneOnly(entry, sceneEvent) && run is not null &&
            (sceneClear is not null || run.IsPassiveClearCondition(sceneEvent.Identity,sceneEvent.TemplateId,sceneEvent.SourceOrdinal)))
            return new(expected,QuestLifecycleAction.Condition,0,Guid.Empty,after,inventory,sceneEvent)
                { SceneClear = sceneClear, SceneClearRun = run };
        return changed is null ? null : new(expected,QuestLifecycleAction.Condition,changed.QuestId,changed.AcceptanceId,
            after,inventory,sceneEvent) { SceneClear = sceneClear, SceneClearRun = run };
    }

    internal static QuestLifecyclePlan SetCinematicPassiveClear(Character expected, StoryQuest quest, QuestCatalog quests,
        DfoServer.Game.Dungeons.DungeonEntryPlan entry, QuestSceneEvent sceneEvent, PreparedDungeonPassiveClear sceneClear,
        SinglePlayerDungeonRun run)
    {
        var conditionPlan = SetCondition(expected, quest, 0, quests);
        var passivePlan = ObservePassive(conditionPlan.Expected with { ActiveMainQuests = [.. conditionPlan.ActiveAfter] },
            quests, entry, sceneEvent, drops: null, sceneClear, run)
            ?? throw new InvalidDataException("演出终末场景清图需要有效迷宫单物件目标");
        return new(expected, QuestLifecycleAction.Condition, conditionPlan.QuestId, conditionPlan.AcceptanceId,
            passivePlan.ActiveAfter, conditionPlan.Inventory, passivePlan.SceneEvent)
            { SceneClear = sceneClear, SceneClearRun = run };
    }

    public static QuestLifecyclePlan Accept(Character expected, StoryQuest quest, QuestCatalog catalog,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, DateTimeOffset now, int? pvpGrade = null,
        bool strongway2019Active = false, bool epicRoad2019Active = false)
    {
        var eligibility = QuestEligibility.Evaluate(expected, quest, catalog,
            strongway2019Active: strongway2019Active, epicRoad2019Active: epicRoad2019Active);
        if (!eligibility.Eligible || !eligibility.Visible && !QuestRelationProgress.DialogueChild(quest, catalog) &&
            !QuestRelationProgress.ThresholdParent(quest,catalog) && !QuestHiddenEntry.Direct(quest,catalog))
            throw new InvalidDataException(eligibility.Reason);
        QuestCoverage.RequireOrdinaryDeclarations(quest);
        if (QuestMailPlan.CompletionDeclaration(quest))
            _ = QuestMailPlan.Prepare(quest,items,pieces,now);
        if (QuestCycle.Daily(quest) && expected.QuestCycleDay != QuestCycle.Day(now))
            throw new InvalidDataException("每日任务需要当前游戏日快照");
        if (expected.ActiveMainQuests.Length >= 30)
            throw new InvalidDataException("30个任务槽已满");
        uint initial = QuestTargetRules.Initial(expected, quest, catalog, now, pvpGrade);
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
            QuestEventProgress.Accepted(QuestEventProgress.Inventory(QuestInventoryProgress.Refresh(expected.ActiveMainQuests.Append(activeQuest).OrderBy(active => active.Slot).ToArray(),
                grant.Plan.After, catalog, now.ToUnixTimeSeconds(),QuestEligibility.Completed(expected)), catalog, grant.Plan), catalog), new(expected.Inventory, grant.Plan.After, "quest_accept"))
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
            QuestInventoryProgress.Refresh(activeAfter, inventory, catalog, now.ToUnixTimeSeconds(),QuestEligibility.Completed(expected)), new(expected.Inventory, inventory, "quest_giveup"));
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

    public static QuestLifecyclePlan SetCondition(Character expected, StoryQuest quest, uint scalar, QuestCatalog? catalog = null, int? pvpGrade = null)
    {
        var active = expected.ActiveMainQuests.SingleOrDefault(value => value.QuestId == quest.Id)
            ?? throw new InvalidDataException("任务未接取");
        bool observedHunt = quest.Condition is "[hunt enemy]" or "[hunt monster]" && scalar == active.ConditionState && QuestHuntProgress.ValidScalar(quest,scalar);
        bool seekMeet = quest.Condition == "[seek n meet npc]" && ValidSeeking(quest) && scalar <= 1023;
        bool parent = quest.Condition == "[quest clear]" && quest.SubType == 2 && scalar <= quest.IntData.Count ||
            catalog is not null && QuestRelationProgress.ListParent(quest, catalog) && scalar == QuestRelationProgress.ListScalar(quest, QuestEligibility.Completed(expected)) ||
            QuestRelationProgress.ThresholdShape(quest) && scalar == QuestRelationProgress.ThresholdScalar(quest,QuestEligibility.Completed(expected));
        bool scene = scalar <= 1 && (QuestClientSceneProgress.TownRange(quest) || QuestClientSceneProgress.Cinematic(quest,out _,out _) ||
            QuestClientSceneProgress.DungeonCinematic(quest));
        bool storage = QuestDazzlementStorage.Matches(quest) && scalar == 0;
        bool question = QuestQuestionProgress.TryCount(quest,out int answers) && scalar < answers;
        bool state = QuestStateProgress.Type(quest) is not null && scalar == QuestStateProgress.Scalar(expected,quest,pvpGrade);
        bool observedClear = QuestDungeonClearProgress.ValidScalar(quest,scalar) && scalar == active.ConditionState;
        bool observedRental = QuestRentalProgress.Supports(quest) && scalar == 0 && active.ConditionState == 0;
        bool observedEpic = QuestEpicProductionProgress.Supports(quest) && scalar == 0 && active.ConditionState == 0;
        bool observedEvent = (QuestEventProgress.Supports(quest) || QuestPvpProgress.Supports(quest)) &&
            (scalar == active.ConditionState ||
             scalar <= active.ConditionState && quest.TargetKind is QuestTargetKind.UiClick or QuestTargetKind.SendChatting
                 or QuestTargetKind.CheckTime or QuestTargetKind.CheckLife);
        bool holding = scalar <= 1 && catalog is not null && (quest.TargetKind == QuestTargetKind.CheckItem &&
            scalar == MaterialState(expected.Inventory, quest, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), catalog.Items) ||
            quest.TargetKind == QuestTargetKind.EquippedItem && catalog.Items is { } facts &&
            scalar == (QuestItemProgress.Equipped(expected.Inventory, quest, facts, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ? 0u : 1u));
        bool observedExpert = QuestExpertJobProgress.Tutorial(quest) && scalar == 0 && active.ConditionState == 0;
        if (active.AcceptanceId == Guid.Empty || !observedHunt && !seekMeet && !parent && !scene && !storage && !question && !state && !observedClear && !observedRental && !observedExpert && !observedEpic && !observedEvent && !holding && (scalar > 1 || quest.Condition is not ("[meet npc]" or "[seeking]")))
            throw new InvalidDataException("条件类型或接取身份不符");
        var after = expected.ActiveMainQuests.Select(value => value.AcceptanceId == active.AcceptanceId ? value with { ConditionState = scalar } : value).ToImmutableArray();
        return new(expected, QuestLifecycleAction.Condition, active.QuestId, active.AcceptanceId,
            catalog is null ? after : QuestRelationProgress.Refresh(after,QuestEligibility.Completed(expected),catalog),
            new(expected.Inventory, expected.Inventory, "quest_condition"));
    }

    // 调用者必须持有当前训练实例的完整轮次胜利；不向普通33或SetCondition提供绕过入口。
    internal static QuestLifecyclePlan? ObservePvpTrainingWin(Character expected, StoryQuest quest,
        DfoServer.Game.Pvp.PvpTrainingRun run)
    {
        if (!ReferenceEquals(run.Entry.Expected,expected) || run.Entry.IsCombo || !run.Round.Complete || run.InstanceId==Guid.Empty ||
            !expected.ActiveMainQuests.Any(row=>ReferenceEquals(row,run.Entry.Quest)) ||
            run.Entry.Quest.QuestId!=quest.Id || run.Entry.Quest.AcceptanceId==Guid.Empty ||
            !DfoServer.Game.Pvp.PvpTrainingProgress.TryApply(quest,run.Entry.Quest.ConditionState,
                DfoServer.Game.Pvp.PvpTrainingFact.OpponentsDefeated,run.Entry.Mode!.Difficulty,out uint scalar)) return null;
        var after=expected.ActiveMainQuests.Select(row=>ReferenceEquals(row,run.Entry.Quest) ? row with {ConditionState=scalar} : row).ToImmutableArray();
        return new(expected,QuestLifecycleAction.Condition,run.Entry.Quest.QuestId,run.Entry.Quest.AcceptanceId,
            after,new(expected.Inventory,expected.Inventory,"quest_pvp_training_win"));
    }

    internal static QuestLifecyclePlan? ObtainPvpTrainingManual(Character expected,StoryQuest quest,
        DfoServer.Game.Pvp.PvpTrainingRun run,ItemCatalog items,GoldLimitCatalog goldLimits,EpicPieceCatalog pieces)
    {
        if(!DfoServer.Game.Pvp.PvpTrainingProgress.Manual(quest) || ObservePvpTrainingWin(expected,quest,run) is not { } progress)
            return null;
        var grant=InventoryRewardGrantService.TryGrantBatch(expected,
            [new(DfoServer.Game.Pvp.PvpTrainingProgress.ManualItemId,1)],items,goldLimits,pieces,DateTimeOffset.UtcNow);
        if(!grant.Accepted || grant.Plan is null || grant.Plan.ChangedSlots.Any(slot=>slot.List!=InventoryListType.Main)) return null;
        // 物品与已取得的原胜利通道一起保存；重发1269不能用同一胜利再次领书。
        return new(expected,QuestLifecycleAction.Condition,checked((ushort)quest.Id),run.Entry.Quest.AcceptanceId,progress.ActiveAfter,grant.Plan);
    }

    // 039C7488→015C2240的成功报告不含技能命中明细；只承认原正式连招实例和接取身份。
    internal static QuestLifecyclePlan? ObservePvpTrainingCombo(Character expected,StoryQuest quest,
        DfoServer.Game.Pvp.PvpTrainingRun run)
    {
        if(!run.Entry.IsCombo || run.Entry.Combos.IsDefaultOrEmpty || run.InstanceId==Guid.Empty ||
            !ReferenceEquals(run.Entry.Expected,expected) || !expected.ActiveMainQuests.Any(row=>ReferenceEquals(row,run.Entry.Quest)) ||
            run.Entry.Quest.QuestId!=quest.Id || run.Entry.Quest.AcceptanceId==Guid.Empty ||
            !DfoServer.Game.Pvp.PvpTrainingProgress.TryApply(quest,run.Entry.Quest.ConditionState,
                DfoServer.Game.Pvp.PvpTrainingFact.ComboCompleted,0,out uint scalar)) return null;
        var after=expected.ActiveMainQuests.Select(row=>ReferenceEquals(row,run.Entry.Quest) ? row with {ConditionState=scalar} : row).ToImmutableArray();
        return new(expected,QuestLifecycleAction.Condition,run.Entry.Quest.QuestId,run.Entry.Quest.AcceptanceId,
            after,new(expected.Inventory,expected.Inventory,"quest_pvp_training_combo"));
    }

    internal static QuestLifecyclePlan? ObserveExpertJobTutorial(Character expected, QuestCatalog quests, ushort questId)
    {
        var active = expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == questId);
        if (!quests.TryGet(questId, out var quest) || !QuestExpertJobProgress.Tutorial(quest) ||
            QuestEligibility.Completed(expected).Contains(quest.Id) ||
            expected.ExpertJobType != QuestExpertJobProgress.RequiredType(quest) ||
            active is not { ConditionState: 1 } || active.AcceptanceId == Guid.Empty)
            return null;
        var after = expected.ActiveMainQuests.Select(row => row == active ? row with { ConditionState = 0 } : row).ToArray();
        return new(expected, QuestLifecycleAction.Condition, active.QuestId, active.AcceptanceId,
            after.ToImmutableArray(), new(expected.Inventory, expected.Inventory, "quest_condition"));
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

    internal static int MaterialLength(StoryQuest quest) => QuestSeekingObjective.TryRead(quest, out var objective)
        ? objective.MaterialLength : 0;

    // 黑市EXP保留自身资源与奖励识别；普通sub0寻物已按本版默认值接入，不再以这三个ID充当通用白名单。
    internal static readonly int[] HarlemSeekingIds = [6053, 6056, 6059];

    internal static bool SeekingSubtype(StoryQuest quest) => quest.TargetKind == QuestTargetKind.CheckItem && ValidSeeking(quest) || quest.SubType is -1 or 0 ||
        quest is { TargetKind: QuestTargetKind.Seeking, SubType: 20 } && QuestSeekingObjective.TryRead(quest, out _);

    internal static bool HarlemSeeking(StoryQuest quest) => quest is { Condition: "[seeking]", SubType: 0, Grade: "[side]", RewardType: "[item]" } &&
        HarlemSeekingIds.Contains(quest.Id) && quest.MainQuest is null && quest.IntData.Count == 2 && quest.IntData[1] == 3 && ValidSeeking(quest) &&
        quest.Definition is { Diagnostics.Length: 0, Drops.Length: > 0 } definition &&
        definition.Drops.All(row => row.EnemyType == 1 && row.ItemId == quest.IntData[0] && row.DungeonId == 7322 && row.Difficulty == -1 &&
            row.Rate == 100 && row.Count > 0);

    internal static bool ValidSeeking(StoryQuest quest) => QuestSeekingObjective.TryRead(quest, out _);

    internal static bool HasMaterials(Character character, StoryQuest quest, long utcSeconds, ItemCatalog? items = null)
    {
        return ValidSeeking(quest) && MissingMaterials(character.Inventory, quest, utcSeconds, items) == 0;
    }

    // type25材料通道按完整库存谓词归零或恢复1；9位通道宽度不是材料数量上限。
    internal static uint MaterialState(InventorySnapshot inventory, StoryQuest quest, long utcSeconds, ItemCatalog? items = null) =>
        MissingMaterials(inventory, quest, utcSeconds, items) == 0 ? 0u : 1u;

    internal static long MissingMaterials(InventorySnapshot inventory, StoryQuest quest, long utcSeconds, ItemCatalog? items = null)
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
        {
            if (CurrencyWallet.RewardKind(id) is { } kind)
                counts[id] = CurrencyWallet.Get(inventory, kind);
            else if (id < 0)
                counts[id] = items is null ? throw new NotSupportedException("品级持有目标需要显式物品事实目录") :
                    QuestItemProgress.Count(inventory, quest, id, items, utcSeconds);
        }
        return wanted.Sum(pair => Math.Max(0, pair.Value - counts.GetValueOrDefault(pair.Key)));
    }
}
