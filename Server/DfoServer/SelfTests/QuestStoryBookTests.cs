using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestStoryBookTests
{
    public static void Run(ItemResources items)
    {
        int checks = Count;
        var source = new InventorySlot(InventoryListType.Main, 99);
        var quest = QuestEligibilityTests.Quest(3108) with { Condition = "[play only]", IntData = [] };
        var role = new Character(1, 0, "StoryBook", 1, new(38, 0, 1, 1), null)
        {
            Level = 1,
            Inventory = new([new(77, source, InventoryRewardGrantService.Create(items.Items.Get(10149048), utcNow: DateTimeOffset.UtcNow))])
        };
        InventoryResult Use(Character character, GameWorld.Quests.StoryQuest target, ushort requestQuest = 3108) =>
            QuestStoryBookService.Plan(character, source, requestQuest, new QuestCatalog(new() { [target.Id] = target }), items.Items, "[village]");
        var used = Use(role, quest);
        That(items.Items.ActionConsumable(10149048) is { Label: "[story book]", Parameters: [3108] } && used.Accepted &&
            used.Plan!.After.Find(source) is null && used.Plan.UsedItems is [{ ItemId: 10149048, Count: 1 }] &&
            role.Inventory.Find(source)!.Core.Count == 1 && role.ActiveMainQuests.Length == 0 && role.CompletedQuests.Length == 0,
            "真实故事簿只读规划一次源消费；确认前不占任务槽、不改完成位或原快照");
        That(!Use(role, quest, 3109).Accepted && !Use(role, quest with { Condition = "[unknown]" }).Accepted,
            "故事簿拒绝客户端改目标和未接入的任务类型");
        That(!Use(role with { CompletedQuests = [3108] }, quest).Accepted &&
            !Use(role with { AccountCompletedQuests = [3108] }, quest).Accepted &&
            !Use(role with { ActiveMainQuests = [new(0, 3108, 0) { AcceptanceId = Guid.NewGuid() }] }, quest).Accepted,
            "已完成、账号已完成及进行中任务不再消耗另一张故事簿");
        That(!Use(role, quest with { MinimumLevel = 2 }).Accepted &&
            !Use(role, quest with { Definition = quest.Definition! with { PrerequisiteGroups = [[100]] } }).Accepted,
            "开启故事簿仍使用统一等级及前置资格，不伪造任务事实");
        var full = role with { ActiveMainQuests = Enumerable.Range(500, 30)
            .Select((id, slot) => new ActiveMainQuest((byte)slot, (ushort)id, 1) { AcceptanceId = Guid.NewGuid() }).ToArray() };
        That(Use(full, quest).Accepted && full.ActiveMainQuests.Length == 30,
            "开启选择框不预占任务槽；后续真正31继续执行原30槽门禁");
        Console.WriteLine($"PASS {Count - checks} 故事簿源身份、候选资格及原生延后接取断言");
    }
}
