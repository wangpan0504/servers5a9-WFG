using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentQuestDropBonusTests
{
    static ItemTemplate Template(int id, ItemKind kind, byte slot, int percent) =>
        new(id, kind, slot, 1, 0, 1, 0, 2, 1, "[free]", ushort.MaxValue, 0, "QuestBonus")
        { QuestItemDropRateBonus = percent };

    public static void RunCore()
    {
        int before = Count;
        var missingSet = new EquipmentQuestDropSetCatalog(_ => new([], _ => ""), _ => "");
        That(missingSet.Get(90000).IsEmpty, "装备保留未注册套装号时只忽略缺席套装，不阻止模板初始化");
        SetAliases();
        var templates = new[] { Template(1, ItemKind.Equipment, 20, 50), Template(2, ItemKind.Equipment, 13, 20),
            Template(3, ItemKind.Avatar, 3, 7), Template(4, ItemKind.Creature, 26, 5),
            Template(5, ItemKind.CreatureEquipment, 27, 6), Template(6, ItemKind.CreatureEquipment, 28, 8),
            Template(7, ItemKind.CreatureEquipment, 29, 9), Template(8, ItemKind.GuildMedal, 32, 4),
            Template(9, ItemKind.Talisman, 34, 3), Template(10, ItemKind.Rune, 0, 2) }.ToDictionary(row => row.Id);
        InventoryItem Worn(int id) => new(id, new(InventoryListType.Equipment, templates[id].EquipmentSlot), new(id, templates[id].Kind, 0));
        Character Role(params InventoryItem[] rows) => new(1, 0, "QuestEquipment", 0, new(1, 0, 0, 0), null)
            { Inventory = new([.. rows]) };
        ImmutableArray<int> Capture(Character character, long now = 100) =>
            EquipmentQuestDropBonusPolicy.Capture(character, id => templates.GetValueOrDefault(id), now);
        var equipped = templates.Keys.Where(id => id != 10).Select(Worn).Append(
            new(10, new(InventoryListType.Talisman, 210), new(10, ItemKind.Rune, 1))).ToArray();
        That(Capture(Role(equipped)).SequenceEqual(new[] { 50, 20, 7, 5, 6, 8, 9, 4, 3, 2 }),
            "普通装备、称号、装扮、成宠、三色宠物装备、勋章、护石及已装载符文均按真实属性槽参与");
        var absent = new InventoryItem(999, new(InventoryListType.Equipment, 3), new(int.MaxValue, ItemKind.Avatar, 999)) { Avatar = new() };
        That(Capture(Role(Worn(1), absent)).SequenceEqual(new[] { 50 }),
            "未登记的旧装扮模板不提供任务掉落加成，也不阻断合法装备效果");
        Reject<InvalidDataException>(() => EquipmentQuestDropBonusPolicy.Capture(Role(Worn(1)),
            _ => throw new InvalidDataException("invalid equipment resource"), 100),
            "真实装备资源解析失败必须上抛，不能作为缺席模板忽略");
        That(Capture(Role(Worn(4), Worn(4) with { Uid = 11, Position = new(InventoryListType.Equipment, 33) },
            Worn(3) with { Position = new(InventoryListType.Equipment, 11) },
            Worn(1) with { Position = new(InventoryListType.Main, 9) },
            Worn(2) with { Position = new(InventoryListType.PersonalCargo, 0) })).SequenceEqual(new[] { 5 }),
            "宠物和光环皮肤槽只供外观，背包与仓库不提供穿戴加成");
        var expiring = Worn(1) with { Core = Worn(1).Core with { ExpireTime = 100 } };
        That(Capture(Role(expiring), 99).SequenceEqual(new[] { 50 }) && Capture(Role(expiring), 100).IsEmpty,
            "每次冻结任务掉落使用本次时刻，穿戴期限到期即停止加成");
        templates[1] = templates[1] with { UsableExpired = true };
        That(Capture(Role(expiring), 101).SequenceEqual(new[] { 50 }), "保留资源明确允许过期使用的装备效果");
        templates[4] = templates[4] with { CreatureEgg = true };
        That(Capture(Role(Worn(4))).IsEmpty, "宠物蛋不被当作已穿成宠");
        templates[4] = templates[4] with { CreatureEgg = false };

        var set = new[] { new EquipmentQuestDropSetBonus(17, 2, 9), new EquipmentQuestDropSetBonus(17, 3, 8) }.ToImmutableArray();
        foreach (int id in new[] { 1, 2, 3 }) templates[id] = templates[id] with { PartSetIds = [17, 17], QuestItemDropSetBonuses = set };
        That(Capture(Role(Worn(1))).SequenceEqual(new[] { 50 }) &&
            Capture(Role(Worn(1), Worn(2))).SequenceEqual(new[] { 50, 20, 9 }) &&
            Capture(Role(Worn(1), Worn(2), Worn(3))).SequenceEqual(new[] { 50, 20, 7, 9, 8 }),
            "套装按唯一成员计件，各已达阶梯仅叠加一次，卸下一件立即移除失效阶梯");
        That(EquipmentQuestDropBonusPolicy.AdditionalRate(40, [50, 20, 5]) == 30 &&
            EquipmentQuestDropBonusPolicy.AdditionalRate(1, [50, 50]) == 0 &&
            EquipmentQuestDropBonusPolicy.AdditionalRate(30, [-10, 30]) == 6 &&
            EquipmentQuestDropBonusPolicy.AdditionalRate(0, [int.MaxValue]) == 0 &&
            EquipmentQuestDropBonusPolicy.AdditionalRate(100, [int.MaxValue, int.MaxValue]) == 2L * int.MaxValue,
            "每个来源独立截断后相加，保留有符号值与零概率，不复乘且不发生32位溢出");
        DropPlans();
        Console.WriteLine($"PASS {Count - before} 穿戴装备任务取物加成断言");
    }

    static void SetAliases()
    {
        string[] names = ["[equipment part set]", "[/equipment part set]", "first.equ", "second.equ", "Set",
            "[piece set ability]", "[/piece set ability]", "[quest item drop rate bonus]"];
        PvfLib.PvfTokenDocument Document(PvfLib.PvfToken[] tokens) => new(tokens, id => names[id]);
        var registry = Document([new(3, 0), new(0, 17), new(6, 2), new(6, 4), new(3, 1),
            new(3, 0), new(0, 17), new(6, 3), new(6, 4), new(3, 1)]);
        EquipmentQuestDropSetCatalog Catalog(int? first, int? second) => new(path =>
            path == "etc/equipmentpartset.etc" ? registry :
                (path.EndsWith("first.equ", StringComparison.Ordinal) ? first : second) is { } percent
                    ? Document([new(3, 5), new(0, 3), new(3, 7), new(0, percent), new(3, 6)]) : Document([]), id => names[id]);
        That(Catalog(null, null).Get(17).IsEmpty && Catalog(9, 9).Get(17).SequenceEqual(new[] { new EquipmentQuestDropSetBonus(17, 3, 9) }),
            "重复目录的无加成或等价加成只保留一份，不影响旧套装注册");
        Reject<InvalidDataException>(() => Catalog(9, 8).Get(17), "同一套装的实际加成冲突不能随意选择或叠发");
    }

    static void DropPlans()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var active = new ActiveMainQuest(0, 29990, 1) { AcceptanceId = Guid.NewGuid() };
        var quest = QuestEligibilityTests.Quest(active.QuestId) with { Condition = "[seeking]", IntData = [9001, 5],
            Definition = new([]) { Job = "[all]", Drops = [new(1, 1, 3, -1, 9001, 1, 40, 5)] } };
        var reward = new ItemTemplate(9001, ItemKind.Quest, 0, 1, 0, 100, 0, 0, 1, "[free]", ushort.MaxValue, 0, "QuestItem");
        const int coldItem = 1234567;
        var items = new ItemCatalog(null!, new Dictionary<int, string> { [coldItem] = "cold.equ" }, new Dictionary<int, string>(), 0);
        items.PrefillTemplates(new[] { reward, Template(1, ItemKind.Equipment, 20, 50), Template(2, ItemKind.Equipment, 13, 20) }
            .Select(row => KeyValuePair.Create(row.Id, row)));
        var resources = new QuestDropResources(new Dictionary<int, StoryQuest> { [quest.Id] = quest },
            new Dictionary<int, ItemTemplate> { [reward.Id] = reward }, new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"))
            { EquipmentItems = items };
        var character = new Character(1, 0, "DropEquipment", 0, new(1, 0, 0, 0), null)
        { ActiveMainQuests = [active], Inventory = new([new(1, new(InventoryListType.Equipment, 20), new(1, ItemKind.Equipment, 0)),
            new(2, new(InventoryListType.Equipment, 13), new(2, ItemKind.Equipment, 0)),
            new(3, new(InventoryListType.Equipment, 3), new(int.MaxValue, ItemKind.Avatar, 3)) { Avatar = new() }]) };
        var entry = new DungeonEntryPlan(1, 1, 3, 0, byte.MaxValue, 0, null!, null!, null!) { QuestAcceptances = [active] };
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        QuestDropPlan Roll(Character actor, int value, QuestDropResources? facts = null, int enemy = 1, bool clear = false) =>
            QuestDropPlan.Create(actor, entry, facts ?? resources, Guid.NewGuid(), identity with { CharacterId = actor.Id }, 1, 1, enemy, clear, _ => value, now,
                new Dictionary<int, TreasureHunterPotionDefinition> { [2600018] = new(5000) });
        That(Roll(character, 67).Outcomes is [{ Granted: 1 }] && Roll(character, 68).Outcomes is [{ Granted: 0 }],
            "40%原概率加两件装备20及8点得到68%，不是乘1.5再乘1.2");
        var cold = character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Add(
            new(4, new(InventoryListType.Equipment, 14), new(coldItem, ItemKind.Equipment, 0))) } };
        Reject<InvalidOperationException>(() => Roll(cold, 67),
            "已登记但未装载的穿戴模板仍拒绝死亡规划，不在owner锁内解析资源");
        var unequipped = character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Select(item =>
            item with { Position = new(InventoryListType.Main, (short)(item.Uid + 8)) }).ToImmutableArray() } };
        That(Roll(unequipped, 40).Outcomes is [{ Granted: 0 }] &&
            Roll(unequipped with { Id = 2, Inventory = new([]) }, 39).Outcomes is [{ Granted: 1 }],
            "脱下或替换只依据新快照，另一角色没有相同加成");
        var boosted = character with { Inventory = character.Inventory with
        {
            AccountWallet = character.Inventory.AccountWallet.ActivateContract(84, 86400, now),
            ConsumableStates = [new(ConsumableStateKind.Effect, 2600018, now.ToUnixTimeMilliseconds() + 1000, 7, 770)]
        } };
        var contract = resources with { Growth = new(20, 20, [20, 23, 27, 30]) };
        That(Roll(boosted, 95, contract).Outcomes is [{ Granted: 1 }] && Roll(boosted, 96, contract).Outcomes is [{ Granted: 0 }],
            "装备28点、成长8点和药剂20点均基于原40%，最终96%，保留各来源原有算法");
        That(TreasureHunterPotionPolicy.AdjustedRate(90, 90, 5000, 45) == 100 &&
            TreasureHunterPotionPolicy.AdjustedRate(40, 40, 0, -50) == 0,
            "完整概率只在最后钳到0至100，不把极大或负加成转换成额外奖励");
        foreach (int enemy in new[] { 1, 2, 3 })
        {
            var source = quest with { Definition = quest.Definition! with { Drops = [new(1, enemy, 3, -1, 9001, 1, 40, 5)] } };
            That(Roll(character, 67, resources with { Quests = new Dictionary<int, StoryQuest> { [source.Id] = source } }, enemy).Outcomes is [{ Granted: 1 }],
                "怪物、APC和物件任务取物复用同一穿戴加成");
        }
        var terminal = quest with { Definition = quest.Definition! with { Drops = [new(0, 0, 3, -1, 9001, 1, 40, 5)] } };
        That(Roll(character, 67, resources with { Quests = new Dictionary<int, StoryQuest> { [terminal.Id] = terminal } }, clear: true)
            .Outcomes is [{ Granted: 1 }], "通关取物也使用相同装备加成，不另发地面奖励");
    }

    public static void RunResources(ItemCatalog items)
    {
        int before = Count;
        foreach (var (id, percent) in new[] { (100310690, 50), (400330027, 20), (400360011, 30), (100330566, 5),
            (20182, 7), (22174, 7), (24203, 7) })
            That(items.Get(id).QuestItemDropRateBonus == percent, "实际装备模板按词条初始化任务掉落属性：" + id);
        foreach (int id in new[] { 20182, 22174, 24203 })
            That(items.Get(id).QuestItemDropSetBonuses.Contains(new(11162, 3, 9)), "宝藏猎人首饰共享真实三件套9%声明：" + id);
        Console.WriteLine($"PASS {Count - before} 装备任务掉落实际资源断言");
    }
}
