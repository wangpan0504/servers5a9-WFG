using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TreasureHunterPotionTests
{
    public static void RunCore()
    {
        int before = Count;
        foreach (int id in TreasureHunterPotionDefinition.FamilyItems)
            That(TreasureHunterPotionDefinition.Read(File(id), 9999) is { BonusRate: 9999, FromExj: true },
                "财宝变体共用已确认身份及EXJ原始系数，不从名称或变体编号补倍率");
        var file = File(2600018);
        That(TreasureHunterPotionDefinition.Read(file, 0) is { BonusRate: 0 } &&
            TreasureHunterPotionDefinition.Read(file, -1) is null &&
            TreasureHunterPotionDefinition.Read(File(2600025), 5000) is null &&
            TreasureHunterPotionDefinition.Read(File(10007343), 5000) is null &&
            TreasureHunterPotionDefinition.Read(File(999), 5000) is null,
            "显式0合法，负参数及其它效果身份不被解释为财宝加成");
        That(TreasureHunterPotionDefinition.Read(file, null) is { BonusRate: 5000, FromExj: false } &&
            TreasureHunterPotionDefinition.Read(File(10008143), null) is { BonusRate: 5000, FromExj: false },
            "所有未配置变体共用一个兜底值，不能按超级药水名称补100%，显式0仍优先于兜底");
        foreach (var bad in new[]
        {
            file with { StatChangeDuration = null }, file with { EffectMaintenance = 0 },
            file with { StatChangeDuration = new(1000, "party", null) },
            file with { Diagnostics = ["incomplete"] },
            file with { Stackable = file.Stackable with { HasActionType = true } }
        }) That(TreasureHunterPotionDefinition.Read(bad, 5000) is null, "不完整财宝资源不能按空效果药消费");
        IReadOnlyDictionary<int, TreasureHunterPotionDefinition> definitions = new Dictionary<int, TreasureHunterPotionDefinition>
        {
            [2600018] = new(5000), [10004241] = new(10000)
        };
        ConsumableState[] states = [new(ConsumableStateKind.Effect, 2600018, 2000, 7, 770),
            new(ConsumableStateKind.Effect, 10004241, 3000, 7, 770),
            new(ConsumableStateKind.Cooldown, 2600018, 999999, 7, 770),
            new(ConsumableStateKind.Effect, 10008143, 999999, 7, 770)];
        int Bonus(long now) => TreasureHunterPotionPolicy.ActiveBonus(states, definitions, now);
        That(Bonus(1999) == 10000 && Bonus(2000) == 10000 && Bonus(2999) == 10000 && Bonus(3000) == 0 &&
            TreasureHunterPotionPolicy.ActiveBonus(states, null, 0) == 0,
            "旧多份效果取有效最大值，使用白名单不被擅自当成持续效果门禁；不读取冷却或未知定义，到期等号失效");
        var inventory = new InventorySnapshot([]) { ConsumableStates = [..states,
            new(ConsumableStateKind.Effect, 9000, 9000, 7, 9000)] };
        var potion = new RecoveryConsumableDefinition(2600018, 0, 1000, 770)
        { EffectMilliseconds = 1800000, EffectMaintenance = 7, CooltimeMaintenance = 7, TreasureHunterPotion = new(5000) };
        var refreshed = ConsumableStatePolicy.Activate(inventory, potion, 1000)!;
        That(refreshed.ConsumableStates.Count(row => row.Kind == ConsumableStateKind.Effect &&
                TreasureHunterPotionDefinition.FamilyItems.Contains(row.ItemId)) == 1 &&
            refreshed.ConsumableStates.Any(row => row.ItemId == 9000) &&
            refreshed.ConsumableStates.Single(row => row.ItemId == 2600018 && row.Kind == ConsumableStateKind.Effect).UntilMilliseconds == 1801000,
            "重新使用覆盖同族旧效果，保留无关药效及真实截止");
        That(TreasureHunterPotionPolicy.AdjustedRate(30, 30, 5000) == 45 &&
            TreasureHunterPotionPolicy.AdjustedRate(30, 36, 5000) == 51 &&
            TreasureHunterPotionPolicy.AdjustedRate(50, 50, 9999) == 99 &&
            TreasureHunterPotionPolicy.AdjustedRate(50, 50, 10000) == 100 &&
            TreasureHunterPotionPolicy.AdjustedRate(1, 1, int.MaxValue) == 100 &&
            TreasureHunterPotionPolicy.AdjustedRate(0, 0, int.MaxValue) == 0 &&
            TreasureHunterPotionPolicy.AdjustedRate(30, 36, 0) == 36,
            "万分比参数沿百分制整数抽样截断，增量以原概率计算，保留0、9999、上限和原契约加成");
        Reject<InvalidDataException>(() => TreasureHunterPotionPolicy.AdjustedRate(101, 100, 0), "拒绝非法原概率");
        Reject<InvalidDataException>(() => TreasureHunterPotionPolicy.AdjustedRate(30, 30, -1), "拒绝负药剂参数");
        DropPlans();
        Console.WriteLine($"PASS {Count - before} 财宝药剂身份、状态、概率及任务实际发放断言");
    }

    static void DropPlans()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1900000000);
        var active = new ActiveMainQuest(0, 29990, 1) { AcceptanceId = Guid.NewGuid() };
        var quest = QuestEligibilityTests.Quest(active.QuestId) with { Condition = "[seeking]", IntData = [9001, 5],
            Definition = new([]) { Job = "[all]", Drops = [new(1, 1, 3, -1, 9001, 2, 50, 5)] } };
        var template = new ItemTemplate(9001, ItemKind.Quest, 0, 1, 0, 100, 0, 0, 1, "[free]", ushort.MaxValue, 0, "财宝任务物品");
        var resources = new QuestDropResources(new Dictionary<int, StoryQuest> { [quest.Id] = quest },
            new Dictionary<int, ItemTemplate> { [9001] = template }, new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"));
        var entry = new DungeonEntryPlan(1, 1, 3, 0, byte.MaxValue, 0, null!, null!, null!) { QuestAcceptances = [active] };
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        var role = new Character(1, 0, "TreasureQuest", 0, new(38, 2, 450, 240), null)
        { ActiveMainQuests = [active], Inventory = new([]) { ConsumableStates =
            [new(ConsumableStateKind.Effect, 2600018, now.ToUnixTimeMilliseconds() + 1000, 7, 770)] } };
        var definitions = new Dictionary<int, TreasureHunterPotionDefinition> { [2600018] = new(5000) };
        int calls = 0;
        QuestDropPlan Roll(Character actor, int value, DateTimeOffset? time = null, QuestDropResources? facts = null) =>
            QuestDropPlan.Create(actor, entry, facts ?? resources, Guid.NewGuid(), identity, 1, 1, 1, false,
                bound => { calls++; That(bound == 100, "财宝不改变任务抽样分母或额外抽奖"); return value; }, time ?? now, definitions);
        var gained = Roll(role, 60);
        That(gained.Outcomes is [{ Rolled: 2, Granted: 2 }] && gained.Inventory.After.Items.Single().Core.Count == 2 && calls == 2,
            "实际任务发放由原50%提升到75%，两次真实命中正常入包而非生成地面物品");
        var other = role with { Id = 2, Inventory = new([]) };
        That(TreasureHunterPotionPolicy.ActiveBonus(other.Inventory.ConsumableStates, definitions, now.ToUnixTimeMilliseconds()) == 0 &&
            Roll(role, 60, now.AddSeconds(1)).Outcomes is [{ Granted: 0 }], "另一角色无药效，恰到期后原60抽样不再命中");
        var contract = role with { Inventory = role.Inventory with { AccountWallet = role.Inventory.AccountWallet.ActivateContract(84, 86400, now) } };
        var withContract = resources with { Growth = new(20, 20, [20, 23, 27, 30]) };
        That(Roll(contract, 84, facts: withContract).Outcomes is [{ Granted: 2 }] &&
            Roll(contract, 85, facts: withContract).Outcomes is [{ Granted: 0 }],
            "成长契约加10点与药剂加25点共同得到85%，不错误复乘为90%");
        var limited = quest with { Definition = quest.Definition! with { Drops = [new(1, 1, 3, -1, 9001, 2, 50, 1)] } };
        That(Roll(role, 60, facts: resources with { Quests = new Dictionary<int, StoryQuest> { [quest.Id] = limited } }).Outcomes is
            [{ Rolled: 2, Granted: 1 }], "药剂不能越过任务物品持有上限");
    }

    public static void RunResources(RecoveryConsumableCatalog catalog)
    {
        int before = Count;
        foreach (var pair in catalog.TreasureHunterPotions)
            That((catalog.AlchemistPotionInfo.TryGetValue(pair.Key, out int value)
                    ? pair.Value.FromExj && value == pair.Value.BonusRate
                    : !pair.Value.FromExj && pair.Value.BonusRate == TreasureHunterPotionDefinition.DefaultBonusRate) &&
                TreasureHunterPotionDefinition.FamilyItems.Contains(pair.Key) && catalog.TryGet(pair.Key, out var definition) &&
                definition.TreasureHunterPotion == pair.Value && definition.EffectMilliseconds > 0,
                "正式财宝目录逐项区分EXJ或统一兜底参数，时长仍来自原STK：" + pair.Key);
        That(!catalog.TreasureHunterPotions.ContainsKey(2600025) && !catalog.TreasureHunterPotions.ContainsKey(10007343),
            "EXJ中的强化药剂不参与任务掉落");
        Console.WriteLine($"PASS {Count - before} 财宝药剂真实资源断言; configured={catalog.TreasureHunterPotions.Count}");
    }

    static RecoveryConsumableFile File(int id)
    {
        var raw = new StackableFile(id, "synthetic.stk", new("[waste]", [0]), 1000, null, null, 1, "potion", []);
        return new(id, raw.ScriptPath, raw, 1800000, 770, null, null, ["stackable type", "stat change duration", "effect maintenance"], [])
        { StatChangeDuration = new(1800000, "myself", null), EffectMaintenance = 7, CooltimeMaintenance = 7 };
    }
}
