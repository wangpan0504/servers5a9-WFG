using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Characters;

internal readonly record struct ClassChangeRequest(short Slot, byte Target, bool Confirmed, bool HasConfirmation = true);
internal sealed record ClassChangePlan(Character Expected, ClassChangeRequest Request, byte GrowType,
    InventoryPlan Inventory, CharacterLearningState Learning, ImmutableArray<int> Completed, long SourceUid);
internal sealed record ClassChangePreparation(ushort Error, string Reason, ClassChangePlan? Plan = null);

internal sealed class ClassChangeService(ItemCatalog items, CharacterCatalog characters, SkillLearningCatalog skills,
    QuestCatalog quests, EpicPieceCatalog pieces)
{
    public ClassChangePreparation Prepare(Character before, ClassChangeRequest request, DateTimeOffset now)
    {
        ClassChangePreparation Reject(ushort error, string reason) => new(error, reason);
        if (request.Target is < 1 or > 5) return Reject(1, "invalid-target");
        if (before.Job is 9 or 10) return Reject(235, "job-without-class-change");
        var source = before.Inventory.Find(new(InventoryListType.Main, request.Slot));
        if (source is null) return Reject(17, "missing-source");
        var ticket = items.ClassChangeTicket(source.Core.ItemId);
        if (ticket is null || source.Core.EquipmentLockId != 0 || source.IsExpired(now.ToUnixTimeSeconds()) ||
            !source.Core.IsStackable || source.Core.Count < 1 || source.Uid <= 0 ||
            !ItemSlotBoundService.Allows(before.Inventory, source.Position, ItemKind.Consumable))
            return Reject(23, "invalid-ticket");
        if (ticket.FixedTarget is null && !request.HasConfirmation) return Reject(1, "missing-confirmation-field");
        int first = before.GrowType & 15, stage = before.GrowType >> 4;
        if (first == request.Target) return Reject(3, "same-class");
        if (first == 0 && !ticket.AllowUntransferred) return Reject(19, "untransferred");
        if (stage > 2 || before.Level < ticket.MinimumLevel || before.Level > ticket.MaximumLevel ||
            (ticket.Jobs & 1 << before.Job) == 0 || ticket.FixedTarget is { } fixedTarget && fixedTarget != request.Target ||
            ticket.Forbidden.Contains((before.Job, request.Target)) ||
            ticket.SourceGrowTypes.Count != 0 && !ticket.SourceGrowTypes.Contains((byte)first))
            return Reject(23, "ticket-qualification");
        foreach (var active in before.ActiveMainQuests)
        {
            if (!quests.TryGet(active.QuestId, out var quest)) return Reject(23, "unknown-active-quest");
            if (quest.Definition?.JobChange == 1) return Reject(22, "active-class-quest");
            if (quest.Definition?.JobChange is 2 or 3) return Reject(23, "active-awakening-quest");
        }
        var option = characters.Get(before.Job).GrowthOptions.GrowTypes.SingleOrDefault(value => value.Value == request.Target);
        if (option is null || option.Awakenings.Count < stage) return Reject(235, "unavailable-target");
        byte target = checked((byte)(request.Target | stage << 4));
        var learning = QuestProfessionReward.Prepare(before, target, before.Level, before.Learning, skills, now);
        var candidate = before with { GrowType = target, Learning = learning,
            Skills = learning.Trees.Single(tree => tree.Context == 0).Skills.Select(value => new CharacterSkill(value.Id, value.Level)).ToArray() };
        // 副手武器必须先卸下；只核验转职相关的技能穿戴资格，不借此追查历史等级或契约穿戴。
        foreach (var equipped in before.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment))
            if (equipped.Position.Index == 24 ||
                !InventoryMoveService.RequiredSkillSatisfied(candidate, items.Get(equipped.Core.ItemId)))
                return Reject(23, "class-dependent-equipment");
        if (!InventoryDeleteService.TryDelete(source, source.Core.Value, 1, out var remaining))
            return Reject(23, "source-consumption");
        var afterBook = before.Inventory with { Items = before.Inventory.Items
            .Select(item => item.Position == source.Position ? remaining : item).OfType<InventoryItem>().ToImmutableArray() };
        var materials = InventoryMaterialConsumptionService.Plan(afterBook, ticket.Materials, pieces, now);
        if (!materials.Accepted) return Reject(23, "insufficient-materials");
        var inventory = new InventoryPlan(before.Inventory, materials.Plan!.After, "class_change");
        return new(0, "prepared", new(before, request, target, inventory, learning, CompletionHistory(candidate), source.Uid));
    }

    ImmutableArray<int> CompletionHistory(Character target)
    {
        int branch = target.GrowType & 15, stage = target.GrowType >> 4;
        var completed = QuestEligibility.Completed(target);
        var additions = new HashSet<int>();
        var visiting = new HashSet<int>();
        bool MatchesJob(QuestDefinition definition) => QuestEligibility.MatchesJob(target.Job, definition.Job) &&
            (definition.TargetCharacters.Length == 0 || definition.TargetCharacters.Any(value => QuestEligibility.MatchesJob(target.Job, value.Job)));
        foreach (var quest in quests.All)
        {
            if (quest.Definition is not { AccountQuest: false } definition || !MatchesJob(definition)) continue;
            bool first = quest.RewardType == "[grow type]" && definition.JobChange == 1 &&
                quest.Rewards is [{ Type: 0, Text: null, Value: var grow }] && grow == branch;
            bool awakened = stage != 0 && quest.RewardType == "[awakening type]" && definition.JobChange == stage + 1 &&
                (definition.GrowType == -1 || definition.GrowType == branch) &&
                quest.Rewards is [{ Type: 0, Text: null, Value: var awake }] && awake == stage;
            if (first || awakened) Visit(quest);
        }
        return additions.Order().ToImmutableArray();

        void Visit(StoryQuest quest)
        {
            if (completed.Contains(quest.Id) || additions.Contains(quest.Id)) return;
            if (quest.Definition is { } scoped && !MatchesJob(scoped)) return;
            if (quest.Id is <= 0 or >= 30000 || quest.Definition is not { AccountQuest: false, Diagnostics.Length: 0 } definition ||
                !visiting.Add(quest.Id)) throw new InvalidDataException("转职前置来源缺失或存在循环");
            // 转职书沿已确认的职业迁移规则补齐各前置组的完成位，不走领奖、不发资产或猜测回答。
            // 与普通接取的OR选择分开，保留既有回答及历史；工作集按实际任务ID去重，循环仍拒绝。
            foreach (int id in definition.PrerequisiteGroups.SelectMany(group => group).Distinct())
                if (quests.TryGet(id, out var prerequisite)) Visit(prerequisite);
            visiting.Remove(quest.Id);
            additions.Add(quest.Id);
        }
    }
}
