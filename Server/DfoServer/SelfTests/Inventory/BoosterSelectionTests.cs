using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class BoosterSelectionTests
{
    public static void RunCore()
    {
        BoosterSelectionResourceTests.RunCore();
        BoosterCooldownTests.RunCore();
        BoosterEpicExchangeTests.RunCore();
        BoosterDisjointInstanceTests.RunCore();
        BoosterUsePlacesTests.RunCore();
        int checks = Count;
        RunRepeatedDisjointDeclarations();
        RunCustomAbilityGradeDeclarations();
        RunServerTypeDeclarations();
        var graded = BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n[booster selection num]\n0\n" +
            "[booster select category]\n0 0\n[equipment]\n16000 1\n[/equipment]\n[booster equipment grade]\n6\n" +
            "[booster equipment upgrade]\n10\n[booster equipment separate]\n3\n[equipment]\n16400 1\n[/equipment]\n" +
            "[stackable]\n1106 2\n[/stackable]\n[equipment]\n16401 1\n[/equipment]\n[/booster select category]\n" +
            "[booster select category]\n1 0\n[equipment]\n16402 1\n[/equipment]\n[/booster select category]\n");
        That(graded.Available && graded.Selection is { Categories.Length: 2 } gradedSelection &&
            gradedSelection.Categories.All(category => category.PendingRule is null) &&
            gradedSelection.Categories[0].Rewards.SequenceEqual(new BoosterChoice[] { new(16000, 1),
                new(16400, 1, 10, 3) { Quality = 999_999_998 }, new(1106, 2),
                new(16401, 1, 10, 3) { Quality = 999_999_998 } }) &&
            gradedSelection.Categories[1].Rewards.Single() == new BoosterChoice(16402, 1),
            "品级6沿最高品质策略只冻结后续装备行，不回填前行、不污染消耗品或下一分类");
        const string empty = "[default select]\n0\n[booster equipment upgrade]\n10\n[stackable]\n[/stackable]";
        const string reward = "[etc]\n1106 1\n[/etc]";
        string Text(string first, string second, string dimensions = "[booster category num]\n2 2 2\n", int count = 1,
            string type = "[stackable type]\n`[booster selection]` 0\n", string extra = "") =>
            type + dimensions + "[booster selection num]\n" + count + "\n" +
            "[booster select category]\n0 0\n" + first + "\n[/booster select category]\n" +
            "[booster select category]\n0 0\n" + second + "\n[/booster select category]\n" +
            "[booster select category]\n1 0\n" + reward + "\n[/booster select category]\n" + extra;
        var definition = BoosterCatalog.Parse(Text(empty, empty.Replace("\n", "\r\n  ")));
        That(definition.Available && definition.Selection is { Categories.Length: 3 } selection &&
            selection.Categories[0].Rewards.IsEmpty && selection.Categories[1].Rewards.IsEmpty &&
            selection.Categories[2].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 1) }),
            "等价空重复保留两个原块及其它奖励分类，块间空白不改变token");
        That(BoosterCatalog.Parse(Text("", "", "[booster category num]\n1 2\n")).Available,
            "单维分类可保留等价空重复");
        var repeatedRewards = BoosterCatalog.Parse(Text(reward, reward));
        That(repeatedRewards.Available && repeatedRewards.Selection!.Categories.Length == 3 &&
            repeatedRewards.Selection.Categories.All(category => category.Rewards.Length == 1),
            "等价非空重复保留原块，每个分类只保留一份奖励声明");
        foreach (string text in new[]
        {
            Text(reward, reward.Replace("1106", "1112")), Text(reward, reward.Replace("1106 1", "1106 01")), Text(empty, ""), Text(empty, empty.Replace("10", "11")),
            Text(empty, empty.Replace("0\n[booster equipment", "1 1106\n[booster equipment")),
            Text("[unknown]\n1", "[unknown]\n1"), Text("[stackable]", "[stackable]"),
            Text(empty, empty, extra: "[/booster select category]"),
            "[outer]\n" + Text(empty, empty) + "\n[/outer]",
            Text(empty, empty).Replace("[booster selection num]\n1\n", "[booster selection num]\n1\n[wrapper]\n") + "\n[/wrapper]",
            Text(empty, empty, dimensions: "[booster category num]\n2 256 256\n").Replace("1 0\n[etc]", "128 0\n[etc]"),
            Text(empty, empty, dimensions: "[booster category num]\n2 256 256\n").Replace("0 0\n", "0 128\n"),
            Text(empty, empty, dimensions: "[outer]\n[booster category num]\n2 2 2\n[/outer]\n"),
            Text(empty, empty).Replace("[booster selection num]\n1\n", "[outer]\n[booster selection num]\n1\n[/outer]\n"),
            Text(empty, empty, count: 0), Text(empty, empty, dimensions: ""),
            Text(empty, empty, dimensions: "[booster category num]\n0\n"),
            Text(empty, empty, dimensions: "[booster category num]\n2 1 1\n"),
            Text(empty, empty, dimensions: "[booster category num]\n2 2 0\n"),
            Text(empty, empty, dimensions: "[booster category num]\n3 2 2 2\n"),
            Text(empty, empty, dimensions: "[booster category num]\n2 2 x\n"),
            Text(empty, empty, type: "[stackable type]\n`[booster selection]` 0\n[stackable type]\n`[booster selection]` 0\n"),
            Text(empty, empty, extra: "[booster select category]\n1 1\n[unknown]\n[/booster select category]"),
            Text(empty, empty).Replace("1 0\n[etc]", "2 0\n[etc]")
        })
            That(!BoosterCatalog.Parse(text).Available, "冲突、未知、残缺或根资格不完整的重复分类仍整盒拒绝");
        const string equipment = "[equipment]\n16400 1 16401 2\n[/equipment]";
        const string growing = "[booster category num]\n2 2 2\n[booster category num]\n2 3 2\n";
        var updated = BoosterCatalog.Parse(Text("", equipment, growing));
        That(updated.Available && updated.Selection is { Categories.Length: 3 } changed &&
            changed.Categories[0].Rewards.IsEmpty && changed.Categories[1].Rewards.SequenceEqual(new BoosterChoice[] { new(16400, 1), new(16401, 2) }),
            "完整范围扩展及空分类补装备保留原块、奖励顺序与数量");
        That(BoosterCatalog.Parse(Text(reward, reward, growing)).Available &&
            BoosterCatalog.Parse(Text("", equipment)).Available &&
            BoosterCatalog.Parse(Text(reward, reward, "[booster category num]\n2 2 2\n[booster category num]\n2 2 2\n")).Available,
            "同维范围逐项不减，不要求等价分类必须变更奖励");
        That(BoosterCatalog.Parse(Text("", equipment, growing).Replace("1 0\n[etc]", "2 0\n[etc]")).Available,
            "最终完整维数决定坐标界限，不沿用先前较小维数");
        foreach (string text in new[]
        {
            Text(equipment, "", growing), Text(equipment, equipment.Replace("16400 1", "16400 2"), growing),
            Text("", reward, growing), Text(empty, equipment, growing), Text("[unknown]\n1", equipment, growing),
            Text("", "[equipment]\n16400 1", growing),
            Text("", equipment, "[booster category num]\n2 3 2\n[booster category num]\n2 2 2\n"),
            Text("", equipment, "[booster category num]\n1 2\n[booster category num]\n2 3 2\n"),
            Text("", equipment, "[booster category num]\n2 2 2\n[booster category num]\n2 3\n"),
            Text("", equipment, "[booster category num]\n2 2 2\n[booster category num]\n2 129 2\n"),
            Text("", equipment, growing).Replace("1 0\n[etc]", "3 0\n[etc]"),
            Text("", equipment, growing, count: 0), Text("", equipment, ""),
            Text("", equipment, growing, extra: "[booster select category]\n0 0\n[/booster select category]"),
            Text("", equipment, growing, extra: "[booster select category]\n0 0\n" + equipment.Replace("16401", "16402") + "\n[/booster select category]")
        })
            That(!BoosterCatalog.Parse(text).Available, "更新声明不允许非空覆盖、缩小异维、残缺、未知、超界或全领重复");
        Console.WriteLine($"PASS {Count - checks} equivalent duplicate selection parser assertions");
    }

    internal static void RunServerTypeDeclarations()
    {
        const string source = "\n[name]\n`name_10305490` \n[explain]\n`explain_10305490` \n[grade]\n1 \n[rarity]\n3 \n" +
            "[usable job]\n`[all]` \n[/usable job]\n\n[attach type]\n`[account]` \n[minimum level]\n1 \n" +
            "[cool time]\n1000 \n[icon]\n`Item/stackable/consumption.img` 1695 \n[icon mark]\n" +
            "`item/IconMark.img` 150 \n[stackable type]\n`[booster selection]` 0 \n[move wav]\n" +
            "`BONE_TOUCH` \n[booster category num]\n0 \n[booster selection num]\n1 \n" +
            "[booster select category]\n0 0 \n[etc]\n10305489 20 10305487 20 \n[/etc]\n\n" +
            "[/booster select category]\n\n[booster category name]\n`请选择奖励` \n[/booster category name]\n" +
            "\n[item category]\n`event` \n[/item category]\n\n[action usable server type]\n" +
            "`[general server]` \n[/action usable server type]\n";
        void Accept(string text, string message)
        {
            var definition = BoosterCatalog.Parse(text);
            That(definition.Available && definition.Selection is { SelectionCount: 1, Categories.Length: 1 } selection &&
                selection.Categories[0] is { Category: 0, Subcategory: 0, PendingRule: null } category &&
                category.Rewards.SequenceEqual(new BoosterChoice[] { new(10305489, 20), new(10305487, 20) }), message);
        }
        Accept(source, "忽略服务器声明，真实选择礼盒仍保留单选、两个奖励及各20数量");
        string box = source[..source.IndexOf("[action usable server type]", StringComparison.Ordinal)];
        Accept(box, "缺席服务器声明不改变原奖励");
        foreach (string field in new[] { "action usable server type", "usable game server type", "usable server type" })
        {
            string Block(string values) => "[" + field + "]\n" + values + "\n[/" + field + "]\n";
            foreach (string values in new[] { "", "`[general server]`", "`[event server]`", "`[starter server]`",
                "`[unknown server]`", "`[general server]` `[event server]`", "`[general server]` `[general server]`", "1" })
                Accept(box + Block(values), "服务器标签和值不成为礼盒开启或奖励限制：" + field + ":" + values);
            Accept(box + Block("`[general server]`") + Block("`[event server]`"),
                "重复服务器声明不再次拒绝礼盒：" + field);
        }
        That(!BoosterCatalog.Parse(box + "[total weekly usable count]\n1\n").Available,
            "仅忽略服务器限制，不放开尚未实现的每周次数账本");
    }

    static void RunCustomAbilityGradeDeclarations()
    {
        const string box = "[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[equipment]\n16400 1\n[/equipment]\n[/booster select category]\n";
        const string grade = "[custom ability grade]\n3\n";
        foreach (string text in new[]
        {
            box, grade + box, "[custom ability grade]\n0\n" + box,
            "[custom ability grade]\n2147483647\n" + box,
            grade.ToUpperInvariant().Replace("\n", "\r\n") + box,
            "[name]\n`[custom ability grade] [/custom ability grade]`\n" + box,
            "[outer]\n[/outer]\n" + grade + "[outer]\n[/outer]\n" + box,
            box + grade, "[grade]\n1\n" + grade + "[rarity]\n2\n" + box
        })
        {
            var definition = BoosterCatalog.Parse(text);
            That(definition.Available && definition.Selection!.Categories.Single().Rewards.Single() == new BoosterChoice(16400, 1),
                "单个根级品级声明不改变合法奖励，引用中的标签文字不算声明");
        }
        foreach (string declaration in new[]
        {
            "[custom ability grade]\n-1\n", "[custom ability grade]\nabc\n", "[custom ability grade]\n",
            "[custom ability grade]\n3 4\n", "[custom ability grade]\n2147483648\n", "[custom ability grade]\n`3`\n",
            grade + grade, grade + "[custom ability grade]\n-1\n", grade + "[custom ability grade]\nabc\n",
            grade + "[custom ability grade]\n", grade + "[custom ability grade\n-1\n",
            "[/custom ability grade]\n" + grade, grade + "[/custom ability grade]\n",
            "[custom ability grade\n3\n", "[custom ability grade extra]\n3\n", "[ custom ability grade]\n3\n",
            "[outer]\n" + grade + "[/outer]\n",
            "[outer]\n[outer]\n[/outer]\n" + grade + "[/outer]\n",
            "[outer]\n[inner]\n[/outer]\n" + grade + "[/inner]\n"
        })
        {
            var definition = BoosterCatalog.Parse(declaration + box);
            That(!definition.Available && definition.PendingRule == "custom ability grade",
                "重复、冲突、残缺、嵌套及多余闭标签的品级声明整盒拒绝: " + declaration);
        }
        That(!BoosterCatalog.Parse(box.Replace("0 0\n[equipment]", "0 0\n" + grade + "[equipment]")).Available,
            "分类内的品级声明不能冒充根级声明");
    }

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        BoosterCooldownTests.RunResources(towns, itemResources);
        RunEquivalentDuplicateResources(towns, itemResources);
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
            That(failed, name);
        }
        var catalog = itemResources.Items;
        LegacyExpiryFixture.Restore(catalog, (10002064, "2013-09-24 06:00:00"));
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces);
        var now = DateTimeOffset.UtcNow;
        var serverDefinition = itemResources.Boosters.Get(10305490);
        That(serverDefinition is { Available: true, CoolTime: 1000, CoolGroup: null, CooltimeMaintenance: null } &&
            serverDefinition.Selection is { SelectionCount: 1, Categories.Length: 1 } serverSelection &&
            serverSelection.Categories[0] is { Category: 0, Subcategory: 0, PendingRule: null } serverCategory &&
            serverCategory.Rewards.SequenceEqual(new BoosterChoice[] { new(10305489, 20), new(10305487, 20) }),
            "真实普通服礼盒保留单选及两个各20数量的原始候选");
        var serverRole = BoosterNativeSelectionTests.ResourceRoles(towns, characters, catalog.Get(10305490), now).First();
        var serverSource = new InventoryItem(1000, Bag(3), Core(10305490, 2));
        serverRole = serverRole with { Inventory = new([serverSource]) };
        string serverBefore = JsonSerializer.Serialize(serverRole);
        foreach (var bad in new[] { Select(3, []), Select(3, [1106]), Select(3, [10305489, 10305489]),
            Select(3, [10305489, 10305487]), Select(3, [10305489], 1), Select(3, [10305489], 0, 1) })
        {
            var rejected = service.Open(serverRole, bad, out var rejectedReceipt, now);
            That(!rejected.Accepted && rejected.Plan is null && rejectedReceipt is null &&
                JsonSerializer.Serialize(serverRole) == serverBefore,
                "普通服礼盒仍拒绝空、外来、重复、超额及错误分类选择且不扣源");
        }
        foreach (int rewardId in new[] { 10305489, 10305487 })
        {
            var opened = service.Open(serverRole, Select(3, [rewardId]), out var serverReceipt, now);
            That(opened.Accepted && opened.Plan!.After.Items.Length == 2 &&
                opened.Plan.After.Find(Bag(3)) is { Uid: 1000, Core.Count: 1 } &&
                opened.Plan.After.Items.Single(item => item.Core.ItemId == rewardId).Core.Count == 20 &&
                serverReceipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(rewardId, 20) }) &&
                JsonSerializer.Serialize(serverRole) == serverBefore,
                "普通服礼盒两项分别按资源发20份，规划保留原角色且仅预扣一只源 " + rewardId);
            var template = catalog.Get(rewardId);
            var range = ItemSlotBoundService.Range(template.Kind, serverRole.Inventory);
            var occupied = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                InventoryRewardGrantService.CreateItem(template, new(ItemSlotBoundService.List(template.Kind), (short)slot),
                    Core(rewardId) with { EquipmentLockId = 1 }) with { Uid = 20000 + slot });
            var fullRole = serverRole with { Inventory = new([serverSource, .. occupied]) };
            string fullBefore = JsonSerializer.Serialize(fullRole);
            var rejected = service.Open(fullRole, Select(3, [rewardId]), out var rejectedReceipt, now);
            That(!rejected.Accepted && rejected.Error == 4 && rejected.Plan is null && rejectedReceipt is null &&
                JsonSerializer.Serialize(fullRole) == fullBefore,
                "普通服礼盒奖励实际所属页满且没有可合堆位置时拒绝，保留两只源与全部实例 " + rewardId);
        }
        RunRealTemplateSelection(towns, itemResources, now);
        BoosterNativeSelectionTests.RunResources(towns, characters, itemResources, now);
        RunDisjointInstanceSelection(towns, itemResources, now);
        RunRepeatedDisjointSelection(towns, characters, itemResources, now);
        RunHarlemAccessorySelection(towns, itemResources, now);
        BoosterEpicExchangeSkinPropagationTests.RunResources(towns, characters, itemResources);
        BoosterEpicExchangeTests.RunResources(towns, itemResources, now);
        var creatureCategory = Parse("0 0\n[creature]\n400990444 1 400990445 1\n[/creature]").Selection!.Categories.Single();
        That(creatureCategory.PendingRule is null && creatureCategory.Rewards.All(row =>
                row.NativeCreatureSelectionRow && row.RequiredKind is null),
            "选择段保留原生来源和完整候选，由真实模板决定产物类型");
        foreach (var rewardId in new[] { 400990444, 400990445 })
        {
            var petService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
                debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true });
            var source = new InventoryItem(1000, Bag(70), InventoryRewardGrantService.Create(catalog.Get(490705390), shopExpiration: true));
            Character petCharacter = new(1, 0, "PetBox", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
            var petResult = petService.Open(petCharacter, Select(70, [rewardId]), out var petReceipt, now);
            That(petResult.Accepted && petResult.Plan!.After.Items.Single() is { Core.Kind: ItemKind.Creature, Creature: not null, Uid: 0 } pet &&
                pet.Core.ItemId == rewardId && petReceipt!.Rewards.Single().Count == 1 && petCharacter.Inventory.Items.Single() == source,
                "真实宠物礼盒两项选择分别生成完整宠物详情，不提前消耗原背包");
            That(!petService.Open(petCharacter, Select(70, [400990444, 400990445]), out _).Accepted,
                "宠物礼盒仍拒绝超额选择");
            var range = ItemSlotBoundService.Range(ItemKind.Creature, petCharacter.Inventory);
            var pets = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                InventoryRewardGrantService.CreateItem(catalog.Get(rewardId), new(InventoryListType.Pet, (short)slot),
                    InventoryRewardGrantService.Create(catalog.Get(rewardId)))).ToArray();
            var fullPetCharacter = petCharacter with { Inventory = new([source, .. pets]) };
            That(!petService.Open(fullPetCharacter, Select(70, [rewardId]), out _, now).Accepted &&
                fullPetCharacter.Inventory.Find(Bag(70)) == source, "宠物页满时拒绝且保留礼包，不能借用Main空格");
            var lastSlotCharacter = petCharacter with { Inventory = new([source, .. pets[..^1]]) };
            var lastSlot = petService.Open(lastSlotCharacter, Select(70, [rewardId]), out _, now);
            That(lastSlot.Accepted && lastSlot.Plan!.After.Items.Length == pets.Length &&
                lastSlot.Plan.After.Find(new(InventoryListType.Pet, range.End)) is { Creature: not null },
                "宠物页仅剩一个槽时完整发放，边界槽不能误拒绝");
        }
        var current = new Character(1, 0, "Selection", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        var grouped = itemResources.Boosters.Get(8403).Selection!;
        That(grouped.SelectionCount == 1 && grouped.Categories.Length == 28 &&
            grouped.Categories.Single(row => row.Category == 0 && row.Subcategory == 1).Rewards[0] == new BoosterChoice(100332, 1),
            "two PVF category coordinates remain distinct, not flattened or inferred from character job");
        var parsed = Parse("0 0\n[stackable]\n1106 7 1112 9\n[/stackable]\n[etc]\n1106 3\n[/etc]", 0);
        That(parsed.Selection!.Categories[0].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1112, 9), new(1106, 3) }),
            "selection rewards are item/count pairs and repeated resource rows remain intact");
        var hints = Parse("0 0\n[default select]\n2 1106 999999999\n[recommend]\n1 1112\n" +
            "[stackable]\n1106 7 1112 9\n[/stackable]\n[default select]\n1 1106\n[recommend]\n0", 2).Selection!.Categories[0];
        That(hints.PendingRule is null && hints.DefaultItemIds.SequenceEqual(new[] { 1106, 999999999, 1106 }) &&
            hints.RecommendedItemIds.SequenceEqual(new[] { 1112 }) &&
            hints.Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1112, 9) }),
            "unclosed hint fields preserve IDs without adding rewards, replacing quantities or requiring current candidates");
        That(Parse("0 0\n[default select]\n1 1106\n[etc]\n1106 7 1106 3\n[/etc]", 0)
            .Selection!.Categories[0].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1106, 3) }),
            "full-group hint does not append another reward to repeated resource rows");
        foreach (string bad in new[] { "0 0\n[etc]\n1106\n[/etc]", "0 0\n[etc]\n1106 0\n[/etc]",
            "0 0\n[etc]\n-1 2\n[/etc]", "0 0\n[equipment]\n16400 1\n[/equipment]\n[avatar]\n1 1 1 1\n[/avatar]",
            "0 0\n[etc]\n1106 2\n[/etc]\n99", "0 0\n[default select]\n2 1106\n[etc]\n1106 2\n[/etc]",
            "0 0\n[recommend]\n1 1106 1112\n[etc]\n1106 2\n[/etc]", "0 0\n[default select]\n-1\n[etc]\n1106 2\n[/etc]",
            "0 0\n[recommend]\n1 x\n[etc]\n1106 2\n[/etc]", "0 0\n[default select]\n1 1106\n[/default select]",
            "0 0\n[stackable]\n1106 2\n[etc]\n1112 1\n[/etc]", "0 0\n[etc]\n1106 2\n[/stackable]",
            "0 0\n[default select]\n1 1106\n[booster equipment grade]\n7\n[equipment]\n16400 1\n[/equipment]" })
            That(Parse(bad).Selection!.Categories[0].PendingRule is not null, "unknown or malformed category never grants a truncated subset");
        That(!Parse("256 0\n[etc]\n1106 1\n[/etc]").Available && !Parse("0 0", -1).Available,
            "invalid category key and selection count reject definition");
        That(!BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[/booster select category]\n[booster select category]\n0 0\n[/booster select category]").Available,
            "duplicate category cannot replace an existing reward group");
        byte[] body = [3, 0, 1, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1106);
        var request = BoosterRequests.Read(body);
        That(request.Slot == 3 && request.Count == 1 && request.Selection is { Category: 2, Subcategory: 1 } &&
            request.Selection.ItemIds.SequenceEqual(new[] { 1106 }), "native 6-byte prefix, two category bytes, i32 ids and two zero option counts");
        foreach (int width in new[] { 7, 8, 9, 11, 12, 13, 15 })
            Reject(() => BoosterRequests.Read(new byte[width]), "truncated selection width " + width);
        body[^1] = 1;
        That(!BoosterRequests.IsItemSelection(body), "extra detail options are not parsed as ordinary item selection");
        Reject(() => BoosterRequests.Read(body), "nonzero detail tail is not discarded");

        current = current with { Inventory = new([new(1, Bag(3), Core(10000743, 2) with { SortLockFlag = 1 }),
            new(2, Bag(65), Core(10000871, 5) with { SortLockFlag = 1 })], Gold: 12345) };
        var before = current.Inventory;
        var result = service.Open(current, Select(3, [10000871], count: 2), out var receipt, now);
        That(result.Accepted && current.Inventory == before && result.Plan!.After.Find(Bag(3)) is null &&
            result.Plan.After.Find(Bag(65)) is { Uid: 2, Core.Value: 7, Core.SortLockFlag: 1 },
            "quickbar and sort lock permit use, identical reward merges without changing the existing UID");
        That(receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(10000871, 2) }) && result.Plan!.After.Gold == 12345,
            "selected reward count comes from PVF times opened count, without extra price or material costs");
        foreach (var bad in new[] { Select(3, []), Select(3, [1106]), Select(3, [10000871, 10000871]),
            Select(3, [10000871, 10000872]), Select(3, [10000871], 1), Select(3, [10000871], 0, 1),
            Select(3, [10000871], count: 0), Select(3, [10000871], count: 3), new BoosterOpenRequest(3, 1) })
            That(!service.Open(current, bad, out _).Accepted && current.Inventory == before, "invalid selection keeps all source items and assets");
        foreach (var core in new[] { Core(10000743) with { EquipmentLockId = 1 },
            Core(10000743) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) }, Core(7542) })
            That(!service.Open(current with { Inventory = new([new(1, Bag(3), core)]) }, Select(3, [10000871]), out _, now).Accepted,
                "safety lock, expiry and cross-kind request reject before consumption");

        var filled = Enumerable.Range(65, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(1108, 1000))).ToImmutableArray();
        filled = filled.SetItem(0, new(65, Bag(65), Core(10000743)));
        current = current with { Inventory = new(filled) };
        result = service.Open(current, Select(65, [10000871]), out _);
        That(result.Accepted && result.Plan!.After.Find(Bag(65)) is { Uid: 0, Core.ItemId: 10000871 } && result.Plan.After.Items.Length == 56,
            "full page uses the exhausted source slot rather than requiring an extra empty slot");
        current = current with { Inventory = new(filled.SetItem(0, filled[0] with { Core = Core(10000743, 2) })) };
        That(!service.Open(current, Select(65, [10000871]), out _).Accepted && current.Inventory.Find(Bag(65))!.Core.Count == 2,
            "capacity failure retains source when its slot cannot be freed");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012332))]) };
        result = service.Open(current, Select(3, [109000071]), out _);
        That(result.Accepted && result.Plan!.After.Items.Single().Core == Core(109000071) && current.Level == 1 && current.Job == 15,
            "equipment keeps full seal, durability, quality and expiry without an output wear-level or job restriction");
        current = current with { Inventory = new([new(1, Bag(3), Core(8403))]) };
        That(service.Open(current, Select(3, [100332], 0, 1), out _).Accepted &&
            !service.Open(current, Select(3, [100332], 0, 0), out _).Accepted &&
            !service.Open(current, Select(3, [100332], 3, 2), out _).Accepted, "correct second category succeeds; cross-category and empty groups reject");

        current = current with { Inventory = new([new(1, Bag(3), Core(10153913))]) };
        result = service.Open(current, Select(3, [100050655, 100100567, 100150535]), out receipt);
        That(result.Accepted && receipt!.Rewards.Length == 3 && result.Plan!.After.Items.Length == 3, "multiple selections grant every chosen equipment");
        That(service.Open(current, Select(3, [100050655]), out _).Accepted, "selection count is the resource maximum, not an added minimum");
        That(!service.Open(current, Select(3, [100050655, 100100567, 100150535, 100200510]), out _).Accepted,
            "selection cannot exceed the resource quota");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012510))]) };
        That(service.Open(current, Select(3, [10012190]), out _).Accepted &&
            !service.Open(current, Select(3, [100350302]), out _).Accepted,
            "an unchosen missing template does not block a valid independent choice");
        result = service.Open(current, Select(3, [63220]), out _);
        That(result.Accepted && result.Plan!.After.Items.Single() is { Core.ItemId: 63220, Core.Kind: ItemKind.Creature, Creature: not null },
            "a selected supported creature is granted with its independent detail and complete core");
        int[] all = [100300855, 100312548, 100322438, 100344654, 100352921];
        current = current with { Inventory = new([new(1, Bag(3), Core(10161354))]) };
        result = service.Open(current, Select(3, all.Reverse().ToArray()), out receipt);
        That(result.Accepted && receipt!.Rewards.Select(row => row.ItemId).SequenceEqual(all) && result.Plan!.After.Items.Length == 5,
            "selection count zero grants all rewards in the selected group, independent of submitted order");
        That(!service.Open(current, Select(3, all[..4]), out _).Accepted && !service.Open(current, Select(3, [.. all, all[0]]), out _).Accepted,
            "full-group echo cannot omit or duplicate rewards");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012104))]) };
        result = service.Open(current, Select(3, [101000007]), out receipt);
        That(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(101000007, 1) }) &&
            result.Plan!.After.Items.Single().Core == Core(101000007),
            "stale default ID outside the candidate list neither blocks a valid choice nor creates an extra item");
        That(!service.Open(current, Select(3, [10000219]), out _).Accepted &&
            !service.Open(current, Select(3, [101000007, 10000219]), out _).Accepted,
            "default hint never authorizes an item outside the real reward list or an extra selection");
        current = current with { Inventory = new([new(1, Bag(3), Core(10002056))]) };
        That(!service.Open(current, Select(3, [3290, 3311]), out _).Accepted, "觉醒之息源盒最低75级在完整合法选择时仍生效");
        current = current with { Level = 75 };
        result = service.Open(current, Select(3, [3290, 3311]), out receipt);
        That(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(3290, 88), new(3311, 88) }) &&
            result.Plan!.After.Items.Length == 2 && result.Plan.After.Items.All(item => item.Core.Count == 88),
            "multiple defaults use actual row quantities, allow fewer than the selection maximum and do not auto-grant unchosen expired defaults");
        That(!service.Open(current, Select(3, [10002064]), out _).Accepted && current.Inventory.Items.Length == 1,
            "a default that is actually selected still needs a usable complete reward template");
        current = current with { Level = 16, Inventory = new([new(1, Bag(3), Core(2749783))]) };
        var historical = new DateTimeOffset(2013, 3, 1, 0, 0, 0, TimeSpan.Zero);
        result = service.Open(current, Select(3, [2749770]), out receipt, historical);
        That(result.Accepted && receipt!.Rewards.Single() == new BoosterGrantedItem(2749770, 5) &&
            result.Plan!.After.Items.Single().Core.Count == 5,
            "default list leading count and repeated ID do not replace or add to the configured five-item reward");

        current = current with { Level = 75, Inventory = new(filled.SetItem(0, new(65, Bag(65), Core(10002056)))) };
        var materialPage = Enumerable.Range(121, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(3290, int.MaxValue)));
        current = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.AddRange(materialPage) } };
        before = current.Inventory;
        That(!service.Open(current, Select(65, [3311]), out _).Accepted && current.Inventory == before,
            "default selection still checks real output capacity and keeps the source and every existing instance on failure");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("selection-test", () => { });
            repo.Create(owner, new("SelectOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), Core(10161354)), new(0, Bag(4), Core(10000743, 2))])
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var saved = session.Selected!;
            string snapshot = Snapshot();
            Query("CREATE TRIGGER fail_selection BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=10 BEGIN SELECT RAISE(ABORT,'selection rollback'); END");
            Reject(() => session.MutateInventory(role => service.Open(role, Select(3, all), out _)), "later reward insert failure propagates");
            That(session.Selected == saved && Snapshot() == snapshot, "source, already-written rewards, revisions, audit and UID sequence roll back together");
            Query("DROP TRIGGER fail_selection");
            var stalePlan = service.Open(saved, Select(4, [10000871]), out _).Plan!;
            That(session.MutateInventory(role => service.Open(role, Select(3, all), out _)).Accepted, "complete all-group plan commits once");
            Reject(() => repo.CommitInventory(owner, saved, stalePlan), "stale revision cannot commit a selection computed before another asset change");
            That(session.MutateInventory(role => service.Open(role, Select(4, [10000871], count: 2), out _)).Accepted, "batch selection commits through the shared transaction");
            snapshot = Snapshot();
            That(!session.MutateInventory(role => service.Open(role, Select(3, all), out _)).Accepted && Snapshot() == snapshot,
                "exhausted request replay creates no extra reward or audit");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "repository reopen retains complete equipment and consumable rewards");
            That(Query("SELECT COUNT(DISTINCT item_uid) FROM character_inventory_items") is 6L, "every final physical instance has a unique persisted UID");
            repo.Create(owner, new("RookieSet", towns.Room, towns.InitialReturn)
            {
                Job = 15, Inventory = new([new(0, Bag(3), Core(490704519))])
            });
            var rookieSession = new GameSession(repo, towns, owner);
            rookieSession.Select(1);
            rookieSession.MarkReady();
            int[] rookieSet = [400100084, 400150076, 400200076, 400250082, 400050092];
            var rookieSaved = rookieSession.Selected;
            snapshot = Snapshot();
            Query("CREATE TRIGGER fail_rookie_set BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=10 BEGIN SELECT RAISE(ABORT,'rookie set rollback'); END");
            Reject(() => rookieSession.MutateInventory(role => service.Open(role, Select(3, rookieSet), out _, now)),
                "新秀整套奖励后续写入失败传播");
            That(rookieSession.Selected == rookieSaved && Snapshot() == snapshot,
                "限定实例分解兼容仍保留源、已写产物、审计与UID序列的整组回滚");
            Query("DROP TRIGGER fail_rookie_set");
            That(rookieSession.MutateInventory(role => service.Open(role, Select(3, rookieSet), out _, now)).Accepted,
                "新秀五件套沿真实Session事务完整提交");
            var rookieReopened = new CharacterRepository(path).List(owner).Single(role => role.Name == "RookieSet");
            That(JsonSerializer.Serialize(rookieReopened) == JsonSerializer.Serialize(rookieSession.Selected) &&
                rookieReopened.Inventory.Items.Length == 5 && rookieReopened.Inventory.Items.All(item => item.Uid > 0 &&
                    item.Core.ExpireTime == now.ToUnixTimeSeconds() + 30 * 86400) &&
                rookieReopened.Inventory.Items.Select(item => item.Uid).Distinct().Count() == 5,
                "重开库保留新秀整套独立UID、完整核心及30天期限");
            snapshot = Snapshot();
            That(!rookieSession.MutateInventory(role => service.Open(role, Select(3, rookieSet), out _, now)).Accepted && Snapshot() == snapshot,
                "新秀整套成功后的耗尽重放不再发奖或记审计");
            repo.Create(owner, new("NativeAura", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(70), InventoryRewardGrantService.Create(catalog.Get(10007996), 2, now, shopExpiration: true))])
            });
            var auraSaved = repo.List(owner).Single(role => role.Name == "NativeAura");
            var nativeService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits,
                itemResources.EpicPieces, debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true }, avatars: itemResources.AvatarPackages);
            var auraPlan = nativeService.Open(auraSaved, Select(70, [101590027]), out _, now).Plan!;
            snapshot = Snapshot();
            Query("CREATE TRIGGER fail_native_aura BEFORE INSERT ON inventory_audit_log WHEN NEW.item_kind_after=8 BEGIN SELECT RAISE(ABORT,'native aura rollback'); END");
            Reject(() => repo.CommitInventory(owner, auraSaved, auraPlan), "光环详情及槽位已写入后，末段审计故障仍传播失败");
            That(Snapshot() == snapshot && Query("SELECT count(*) FROM inventory_avatar_details") is 0L,
                "光环末段故障完整回滚源、核心、槽、详情、UID、revision及审计");
            Query("DROP TRIGGER fail_native_aura");
            var auraCommitted = repo.CommitInventory(owner, auraSaved, auraPlan);
            That(JsonSerializer.Serialize(repo.List(owner).Single(role => role.Name == "NativeAura")) == JsonSerializer.Serialize(auraCommitted) &&
                auraCommitted.Inventory.Find(Bag(70)) is { Core.Count: 1 } &&
                auraCommitted.Inventory.Items.Single(item => item.Core.Kind == ItemKind.Avatar) is { Uid: > 0, Avatar: not null },
                "确定回滚后原合法光环计划可完整提交一次，保存重读保留详情与剩余源");
            snapshot = Snapshot();
            Reject(() => repo.CommitInventory(owner, auraSaved, auraPlan), "已提交的旧光环计划不可再次提交");
            That(Snapshot() == snapshot, "光环旧revision拒绝不修改详情、UID或资产");
            var serverCreated = repo.Create(owner, new("ServerType", towns.Room, towns.InitialReturn)
            {
                Job = serverRole.Job, Level = serverRole.Level, Skills = serverRole.Skills,
                Inventory = new([serverSource with { Uid = 0 }])
            });
            var serverSession = new GameSession(repo, towns, owner);
            serverSession.Select(serverCreated.Slot);
            serverSession.MarkReady();
            var serverSaved = serverSession.Selected!;
            long serverSourceUid = serverSaved.Inventory.Find(Bag(3))!.Uid;
            snapshot = Snapshot();
            Query("CREATE TRIGGER fail_server_selection BEFORE INSERT ON inventory_audit_log WHEN NEW.item_id_after=10305489 BEGIN SELECT RAISE(ABORT,'server selection rollback'); END");
            Reject(() => serverSession.PlanAndCommitBooster(role => service.Open(role, Select(3, [10305489]), out _, now),
                service.UsePlaces(10305490), 10305490), "普通服礼盒源及奖励已写入后的晚审计故障传播失败");
            That(serverSession.Selected == serverSaved && Snapshot() == snapshot,
                "普通服礼盒晚故障完整回滚源、奖励、revision、审计与UID序列");
            Query("DROP TRIGGER fail_server_selection");
            var serverRewardIds = new List<int>();
            foreach (int rewardId in new[] { 10305489, 10305487 })
            {
                if (serverRewardIds.Count == 1)
                {
                    var beforeCooldown = serverSession.Selected;
                    string cooldownSnapshot = Snapshot();
                    BoosterReceipt? cooldownReceipt = null;
                    var deniedCooldown = serverSession.PlanAndCommitBooster(role =>
                        service.Open(role, Select(3, [rewardId]), out cooldownReceipt, now),
                        service.UsePlaces(10305490), 10305490);
                    That(!deniedCooldown.Accepted && deniedCooldown.Error == 19 && deniedCooldown.Plan is null &&
                        cooldownReceipt is null && ReferenceEquals(serverSession.Selected, beforeCooldown) && Snapshot() == cooldownSnapshot,
                        "普通服礼盒同一时刻第二次使用按真实1000毫秒冷却拒绝，角色、审计及UID序列全不变");
                }
                var openAt = now.AddMilliseconds(serverRewardIds.Count * 1000L);
                BoosterReceipt? serverReceipt = null;
                var committed = serverSession.PlanAndCommitBooster(role => service.Open(role, Select(3, [rewardId]), out serverReceipt, openAt),
                    service.UsePlaces(10305490), 10305490);
                serverRewardIds.Add(rewardId);
                var inventory = serverSession.Selected!.Inventory;
                That(committed.Accepted && serverReceipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(rewardId, 20) }) &&
                    (serverRewardIds.Count == 1 ? inventory.Find(Bag(3)) is { Core.Count: 1 } source && source.Uid == serverSourceUid :
                        inventory.Find(Bag(3)) is null) &&
                    inventory.Items.Where(item => item.Core.ItemId != 10305490).Select(item => item.Core.ItemId).Order().SequenceEqual(serverRewardIds.Order()) &&
                    inventory.Items.Where(item => item.Core.ItemId != 10305490).All(item => item.Core.Count == 20 && item.Uid > 0) &&
                    inventory.Items.Select(item => item.Uid).Distinct().Count() == inventory.Items.Length &&
                    inventory.ConsumableStates.SequenceEqual(new[]
                    { new ConsumableState(ConsumableStateKind.Cooldown, 10305490, openAt.ToUnixTimeMilliseconds() + 1000, 0, -10305490) }) &&
                    serverSession.Selected.Revision == serverSaved.Revision + serverRewardIds.Count,
                    "普通服礼盒经真实Session每次只扣一只源并持久化所选20份，不提前发未选奖励 " + rewardId);
            }
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single(role => role.Name == "ServerType")) ==
                JsonSerializer.Serialize(serverSession.Selected), "重开库保留普通服礼盒两项各20份、完整实例及耗尽源状态");
            snapshot = Snapshot();
            That(!serverSession.PlanAndCommitBooster(role => service.Open(role, Select(3, [10305489]), out _, now),
                service.UsePlaces(10305490), 10305490).Accepted && Snapshot() == snapshot,
                "普通服礼盒耗尽后重放不重复发奖、分配UID或推进revision与审计");
            var nextAuraResult = nativeService.Open(auraCommitted, Select(70, [101590028]), out var nextAuraReceipt, now);
            That(nextAuraResult.Accepted && nextAuraResult.Plan is not null && nextAuraReceipt is not null,
                "旧owner拒绝前先确认无冷却光环盒的后续请求确有完整合法计划和回执");
            var nextAuraPlan = nextAuraResult.Plan!;
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => service.Open(role, Select(4, [10000871]), out _)), "old generation cannot open or replay a selection");
            Reject(() => repo.CommitInventory(owner, auraCommitted, nextAuraPlan), "旧owner不能提交带光环详情的新合法计划");
            That(Query("PRAGMA integrity_check") is "ok", "selection database integrity");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        RunSpring2020NestedBoxLinks(towns, characters, itemResources);
        RunSpring2020ChildBoxOpenings(towns, characters, itemResources);
        RunSpring2020AvatarLegalOptions(towns, characters, itemResources);
        Console.WriteLine($"PASS {Count - checks} booster selection assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count, now);
    }

    static void RunEquivalentDuplicateResources(TownCatalog towns, ItemResources resources)
    {
        var failures = new List<string>();
        // 本场景验证分类与完整奖励，不依赖资源是否已移除历史活动截止；仍验证实际实例过期拒绝。
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true });
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, categories, empty, rows) in new[]
        {
            (2749832, 36, 8, 93), (10007852, 56, 15, 658),
            (10008885, 56, 14, 675), (490700910, 61, 17, 718), (2749978, 9, 0, 70), (490001689, 65, 9, 513)
        })
        {
            var definition = resources.Boosters.Get(id);
            if (!definition.Available || definition.Selection is not { } selection ||
                selection.Categories.Length != categories || selection.Categories.Any(group => group.PendingRule is not null) ||
                selection.Categories.Count(group => group.Rewards.IsEmpty) != empty ||
                selection.Categories.Sum(group => group.Rewards.Length) != rows)
                failures.Add(id + ":" + (definition.PendingRule ?? "分类或奖励声明被删改"));
            if (definition.Selection is not { } parsed)
                continue;
            var source = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(resources.Items.Get(id), utcNow: now, shopExpiration: true));
            Character role = new(1, 0, "EmptyCategory", 0, towns.Room, towns.InitialReturn)
            {
                Job = 0, Level = 95, Inventory = new([source], Gold: 12345)
            };
            That(!source.Core.IsExpired(now.ToUnixTimeSeconds()) &&
                InventoryAvatarPackageService.SourceEligible(role, source, resources.Items.Get(id)),
                "空分类拒绝用例使用真实未过期且满足资源资格的源 " + id);
            var firstValid = parsed.Categories.First(group => !group.Rewards.IsEmpty);
            var expired = source with { Core = source.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } };
            var expiredRole = role with { Inventory = role.Inventory with { Items = [expired] } };
            var denied = service.Open(expiredRole, Select(65, [firstValid.Rewards[0].ItemId], firstValid.Category, firstValid.Subcategory),
                out var deniedReceipt, now);
            That(!denied.Accepted && denied.Plan is null && deniedReceipt is null &&
                expiredRole.Inventory.Items.Single() == expired && expiredRole.Inventory.Gold == role.Inventory.Gold,
                "分类fixture的历史截止调试不忽略真实实例到期，等于截止即拒绝 " + id);
            int accepted = 0, attempted = 0;
            foreach (var group in parsed.Categories)
                for (int row = 0; row < group.Rewards.Length; row++)
                {
                    var choice = group.Rewards[row]; attempted++;
                    var effective = parsed.Categories.Last(candidate => candidate.Category == group.Category && candidate.Subcategory == group.Subcategory);
                    That(effective.Rewards.Count(candidate => JsonSerializer.Serialize(candidate) == JsonSerializer.Serialize(choice)) == 1,
                        "每条原始奖励行映射至同坐标后定义的唯一同内容行，不丢失或拼接 " + id + ":" + attempted);
                    var template = resources.Items.Get(choice.ItemId);
                    var opened = service.Open(role, Select(65, [choice.ItemId], group.Category, group.Subcategory), out var receipt, now);
                    That(opened.Accepted && role.Inventory.Items.Single() == source && role.Inventory.Gold == 12345,
                        "每条真实单选成功规划且不改输入源和钱包 " + id + ":" + choice.ItemId);
                    That(template.Kind is not (ItemKind.Avatar or ItemKind.Creature) && choice.UpgradeChances.IsEmpty,
                        "真实重复分类矩阵成功项是确定的普通实体 " + choice.ItemId);
                    var expected = ExpectedItems(role, [choice]);
                    That(receipt!.ItemId == id && receipt.Slot == 65 && receipt.Rewards is [{ } reward] &&
                        reward.ItemId == choice.ItemId && reward.Count == choice.Count &&
                        JsonSerializer.Serialize(opened.Plan!.Before) == JsonSerializer.Serialize(role.Inventory) &&
                        JsonSerializer.Serialize(opened.Plan.After) == JsonSerializer.Serialize(role.Inventory with { Items = expected }),
                        "真实单选完整核心、数量、目标槽、钱包与源一次扣量符合预期 " + id + ":" + choice.ItemId);
                    accepted++;
                }
            That(attempted == rows && accepted > 0, "每源全部原奖励行实际Open且至少一项合法非空成功 " + id);
            if (id == 490001689)
            {
                var effective = parsed.Categories.GroupBy(group => (group.Category, group.Subcategory)).Select(groups => groups.Last()).ToArray();
                var added = effective.Single(group => group.Category == 6 && group.Subcategory == 0);
                That(parsed.SelectionCount == 1 && effective.Length == 40 && effective.Sum(group => group.Rewards.Length) == 312 &&
                    parsed.Categories.First(group => group.Category == 6 && group.Subcategory == 0).Rewards.IsEmpty &&
                    added.Rewards.Select(choice => (choice.ItemId, choice.Count)).SequenceEqual(new[] { 405044, 405042, 405043, 425044, 425042, 425043, 415044, 415042, 415043 }.Select(item => (item, 1))),
                    "逐项扩展真实源保留65原块/513原行，40坐标后定义312行；裸空(6,0)补入九件各1且不拼接");
            }
            if (id == 2749978)
            {
                That(parsed.SelectionCount == 4 && parsed.Categories.Select(group => (group.Category, group.Subcategory)).Distinct().Count() == 7,
                    "真实非空重复源保留selection4、九原块和七个唯一坐标");
                foreach (var group in parsed.Categories.GroupBy(group => (group.Category, group.Subcategory)).Where(groups => groups.Count() > 1).Select(groups => groups.First()))
                {
                    var selected = group.Rewards.Take(4).ToArray();
                    var opened = service.Open(role, Select(65, selected.Select(choice => choice.ItemId).ToArray(), group.Category, group.Subcategory), out var receipt, now);
                    That(opened.Accepted && receipt!.Rewards.Select(reward => (reward.ItemId, reward.Count)).SequenceEqual(selected.Select(choice => (choice.ItemId, choice.Count))) &&
                        JsonSerializer.Serialize(opened.Plan!.After) == JsonSerializer.Serialize(role.Inventory with { Items = ExpectedItems(role, selected) }),
                        "同键非空重复四选按每项原数量发一份，不拼接或翻倍 " + group.Category);
                    foreach (int[] choices in new[] { group.Rewards.Take(5).Select(choice => choice.ItemId).ToArray(), new[] { selected[0].ItemId, selected[0].ItemId } })
                    {
                        var rejected = service.Open(role, Select(65, choices, group.Category, group.Subcategory), out var rejectedReceipt, now);
                        That(!rejected.Accepted && rejected.Plan is null && rejectedReceipt is null && role.Inventory.Items.Single() == source,
                            "非空重复分类仍拒绝五选超限及重复ID，保留源 " + group.Category);
                    }
                }
            }
            Console.WriteLine("Equivalent duplicate selection coverage: " + JsonSerializer.Serialize(new { Source = id, Attempted = attempted, Accepted = accepted, Rejected = attempted - accepted }));
            foreach (var group in parsed.Categories.GroupBy(group => (group.Category, group.Subcategory)).Select(groups => groups.Last())
                .Where(group => group.Rewards.IsEmpty))
                foreach (int[] choice in new[] { Array.Empty<int>(), new[] { 1106 } })
                {
                    var result = service.Open(role, Select(65, choice, group.Category, group.Subcategory), out var receipt, now);
                    That(!result.Accepted && result.Plan is null && receipt is null && role.Inventory.Items.Single() == source &&
                        role.Inventory.Gold == 12345, "空分类不因兼容重复而发奖、扣源或改钱包 " + id);
                }
        }
        That(failures.Count == 0, "六个真实兼容重复分类源保留全部原块、有效空分类和奖励行：" + string.Join("; ", failures));

        ImmutableArray<InventoryItem> ExpectedItems(Character role, IEnumerable<BoosterChoice> choices)
        {
            var result = new List<InventoryItem>();
            foreach (var choice in choices)
            {
                var template = resources.Items.Get(choice.ItemId);
                // 预期不调用生产Create/Grant：独立派生普通实体完整核心，未声明字段保持零。
                var core = new ItemCore(template.Id, template.Kind,
                    template.Kind is ItemKind.Equipment or ItemKind.CreatureEquipment ? 999_999_998 : choice.Count)
                {
                    Attr = choice.Upgrade, GenuineUpgrade = choice.Refinement, Durability = template.Durability,
                    SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0,
                    ExpireTime = template.Expiration.Resolve(now, ignoreAbsoluteExpiration: true)
                };
                var range = ItemSlotBoundService.Range(template.Kind, role.Inventory);
                var list = ItemSlotBoundService.List(template.Kind);
                short slot = range.Start;
                for (int index = 0; index < (core.IsStackable ? 1 : choice.Count); index++)
                {
                    while (result.Any(item => item.Position == new InventorySlot(list, slot))) slot++;
                    That(slot <= range.End, "独立预期未越过真实奖励页边界");
                    result.Add(new(0, new(list, slot), core));
                }
            }
            return result.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray();
        }
    }

    static void RunRealTemplateSelection(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        int[] sourceIds = [10007539, 10007726, 10007992, 10007993, 10007996, 490700181,
            490700184, 490700980, 490701127, 490701129, 490702047];
        var catalog = resources.Items;
        var service = new InventoryBoosterService(catalog, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true }, avatars: resources.AvatarPackages);
        var failures = new List<string>();
        int rows = 0, titles = 0, auras = 0;
        foreach (int sourceId in sourceIds)
        {
            var definition = resources.Boosters.Get(sourceId);
            That(definition.Available && definition.Selection is { SelectionCount: 1, Categories.Length: 1 },
                "真实称号和光环盒保持单分类单选 " + sourceId);
            foreach (var category in definition.Selection!.Categories)
                foreach (var choice in category.Rewards)
                {
                    rows++;
                    var template = catalog.Get(choice.ItemId);
                    if (template.Kind == ItemKind.Equipment) titles++;
                    if (template.Kind == ItemKind.Avatar) auras++;
                    var source = new InventoryItem(1000, Bag(70),
                        InventoryRewardGrantService.Create(catalog.Get(sourceId), 2, shopExpiration: true));
                    Character role = new(1, 0, "RealSelection", 0, towns.Room, towns.InitialReturn)
                    {
                        Inventory = new([source])
                    };
                    var opened = service.Open(role, Select(70, [choice.ItemId], category.Category, category.Subcategory),
                        out var receipt, now);
                    if (!opened.Accepted)
                    {
                        failures.Add(sourceId + ":" + choice.ItemId + ":" + opened.Error + ":" + opened.RejectReason);
                        continue;
                    }
                    var reward = opened.Plan!.After.Items.Single(item => item.Core.ItemId == choice.ItemId);
                    That(receipt!.Rewards.Single() == new BoosterGrantedItem(choice.ItemId, choice.Count) &&
                        reward.Core.ItemId == choice.ItemId && reward.Core.Count == choice.Count &&
                        reward.Core.Kind == template.Kind && role.Inventory.Items.Single() == source &&
                        opened.Plan.After.Find(source.Position) is { Core.Count: 1 },
                        "真实选择按模板完整发奖且规划不提前扣源 " + sourceId + ":" + choice.ItemId);
                    var expected = InventoryRewardGrantService.CreateItem(template, reward.Position,
                        InventoryRewardGrantService.Create(template, choice.Count, now, shopExpiration: true));
                    That(reward == expected, "真实候选完整核心、封装、耐久、品质、期限及详情沿统一创建规则 " + choice.ItemId);
                    That(template.Kind switch
                    {
                        ItemKind.Equipment => reward.Position.List == InventoryListType.Main && reward.Avatar is null && reward.Creature is null,
                        ItemKind.Avatar => reward.Position.List == InventoryListType.Avatar && reward.Avatar is not null &&
                            reward.Creature is null && reward.Core.Durability == 0,
                        _ => false
                    }, "真实称号与光环进入模板所属容器并带完整详情 " + sourceId + ":" + choice.ItemId);
                }
        }
        That(rows == 33 && titles == 21 && auras == 12, "正式Catalog中11盒仍声明33个真实称号与光环候选");
        That(failures.Count == 0, "真实选择发奖失败：" + string.Join(", ", failures));
        RunRealTemplateRejections(towns, resources, now);
    }

    static void RunRealTemplateRejections(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        const int sourceId = 10007539;
        var catalog = resources.Items;
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolated)!;
        var service = new InventoryBoosterService(catalog, isolated, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true },
            contracts: resources.Contracts, avatars: resources.AvatarPackages);
        var source = new InventoryItem(1000, Bag(70), InventoryRewardGrantService.Create(catalog.Get(sourceId), shopExpiration: true));
        Character role = new(1, 0, "RealReject", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
        InventoryResult Open(int id, string body, int selectionCount = 1)
        {
            definitions[sourceId] = BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n" +
                "[booster selection num]\n" + selectionCount + "\n[booster select category]\n0 0\n" + body + "\n[/booster select category]");
            int[] ids = selectionCount == 0 ? definitions[sourceId].Selection!.Categories[0].Rewards.Select(row => row.ItemId).ToArray() : [id];
            return service.Open(role, Select(70, ids), out _, now);
        }
        int optionAvatar = resources.AvatarPackages.Get(490705387).Categories.SelectMany(category => category.Rows)
            .Select(row => row.ItemId).First(id => resources.AvatarPackages.GetAvatar(id).RequiresAbilityChoice);
        That(catalog.Get(optionAvatar).Kind == ItemKind.Avatar &&
            resources.AvatarPackages.GetAvatar(optionAvatar).RequiresAbilityChoice,
            "额外属性装扮反例使用正式资源声明");
        foreach (int unsafeId in new[] { optionAvatar, 1, resources.EpicPieces.ByPiece.Keys.First(),
            resources.Boosters.DirectActivationItems.First(), int.MaxValue })
        {
            var result = Open(unsafeId, "[creature]\n" + unsafeId + " 1\n[/creature]");
            That(!result.Accepted && role.Inventory.Items.Single() == source,
                "选择段不能把需额外选项、特殊owner或失效模板当默认实体发奖 " + unsafeId);
        }
        That(!Open(400330019, "[equipment]\n400330019 1\n[/equipment]\n[creature]\n400330019 1\n[/creature]").Accepted,
            "同ID不同选择来源不能靠第一行覆盖分组语义");
        const string repeatedTitle = "[creature]\n400330019 1 400330019 1\n[/creature]";
        var repeatedChoice = Open(400330019, repeatedTitle);
        var repeatedAll = Open(400330019, repeatedTitle, 0);
        That(repeatedChoice.Accepted && repeatedChoice.Plan!.After.Items.Length == 1 &&
            repeatedAll.Accepted && repeatedAll.Plan!.After.Items.Length == 2,
            "相同来源等价行在选择模式只发一行，全领模式保留每行独立实例");
        That(!Open(1, "[etc]\n1 1\n[/etc]\n[creature]\n1 1\n[/creature]", 0).Accepted,
            "全领逐来源保留门槛，钱包行不能通过同ID普通行绕过creature来源实体限制");
        That(Open(63220, "[creature]\n63220 1\n[/creature]").Accepted,
            "真实宠物模板仍按原生选择来源生成宠物实体");
        const int auraId = 101590027;
        Open(auraId, "[creature]\n" + auraId + " 1\n[/creature]");
        foreach (string? avatarText in new string?[] { null, "[import script]\n`equipment/base.equ`",
            "[avatar type select]\n0 0 0 6000 0 0 0\n[/avatar type select]",
            "[avatar select ability]\n0 `[HP MAX]` `+` 100\n[/avatar select ability]",
            "[avatar select ability]\n1 `[HP MAX]` `+` 100\n[/avatar select ability]" })
        {
            var avatarCatalog = new AvatarPackageCatalog(_ => avatarText ?? "",
                avatarText is null ? new Dictionary<int, string>() : new() { [auraId] = "aura.equ" }, new Dictionary<int, string>());
            var guarded = new InventoryBoosterService(catalog, isolated, resources.GoldLimits, resources.EpicPieces,
                debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true }, avatars: avatarCatalog);
            var rejected = guarded.Open(role, Select(70, [auraId]), out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && role.Inventory.Items.Single() == source,
                "缺失、挂起、期限类型表及显式能力选择（含0）不能猜默认光环实例");
        }
        var aura = catalog.Get(auraId);
        var range = ItemSlotBoundService.Range(ItemKind.Avatar, role.Inventory);
        var full = role with { Inventory = new([source, .. Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Select(slot => InventoryRewardGrantService.CreateItem(aura, new(InventoryListType.Avatar, (short)slot),
                InventoryRewardGrantService.Create(aura, utcNow: now, shopExpiration: true)))]) };
        That(!service.Open(full, Select(70, [auraId]), out _, now).Accepted && full.Inventory.Find(source.Position) == source,
            "光环页满不能借用Main源槽或丢弃奖励，整次规划保留源");
        definitions[sourceId] = BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n" +
            "[creature]\n1 400330019 1 1\n[/creature]\n[/booster info]");
        That(!service.Open(role, new(70, 1), out _, now).Accepted,
            "随机宠物池继续拒绝真实称号模板，不受选择来源修复影响");
    }

    static void RunDisjointInstanceSelection(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        BoosterDisjointInstanceTests.Run(towns, resources);
        var catalog = resources.Items;
        var service = new InventoryBoosterService(catalog, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        Character Role(int sourceId) => new(1, 0, "RookieBox", 0, towns.Room, towns.InitialReturn)
        {
            Job = 15, Inventory = new([new(1, Bag(3), InventoryRewardGrantService.Create(catalog.Get(sourceId), utcNow: now))], Gold: 12345)
        };
        var first = Role(490704518);
        That(service.Open(first, Select(3, [401030066]), out _, now).Accepted,
            "模板已禁分解的新秀武器盒能沿真实开箱入口发放，不能因空实例分解声明整盒拒绝");
        for (byte job = 0; job < 16; job++)
            foreach (byte level in new byte[] { 1, 95 })
                That(service.Open(first with { Job = job, Level = level }, Select(3, [401030066]), out _, now).Accepted,
                    "各职业及领取等级都可选择异职业武器，不将穿戴资格套用于领盒 " + job + ":" + level);
        var unique = new HashSet<int>();
        foreach (var (id, categories, lines, groupSize) in new[]
        {
            (490704518, 16, 76, 1), (490704519, 35, 175, 5), (490704520, 7, 21, 3), (490704521, 7, 14, 2)
        })
        {
            var definition = resources.Boosters.Get(id);
            That(definition.Available && definition.Selection!.Categories.Length == categories &&
                definition.Selection.Categories.Sum(group => group.Rewards.Length) == lines,
                "新秀四盒保留全部分类及声明行 " + id);
            foreach (var category in definition.Selection!.Categories)
            {
                var selections = id == 490704518 ? category.Rewards.Select(row => new[] { row.ItemId }) :
                    new[] { category.Rewards.Select(row => row.ItemId).ToArray() };
                foreach (int[] ids in selections)
                {
                    var role = Role(id);
                    var opened = service.Open(role, Select(3, ids, category.Category, category.Subcategory), out var receipt, now);
                    That(opened.Accepted && receipt!.Rewards.Length == groupSize && opened.Plan!.After.Items.Length == groupSize &&
                        opened.Plan.After.Gold == 12345 && opened.Plan.After.Find(Bag(3)) is null && role.Inventory.Items.Length == 1,
                        "各分类完整发奖且只规划一次源消费，不额外限制领取职业/等级 " + id);
                    foreach (var reward in opened.Plan!.After.Items)
                    {
                        unique.Add(reward.Core.ItemId);
                        var template = catalog.Get(reward.Core.ItemId);
                        That(ids.Contains(reward.Core.ItemId) && template is { Kind: ItemKind.Equipment,
                            DisjointForbidden: true, PersonalCargoForbidden: true, Expiration.Days: 30 } &&
                            reward.Core == InventoryRewardGrantService.Create(template, utcNow: now) with
                            { Attr = (byte)(id == 490704518 ? 10 : 0), GenuineUpgrade = (byte)(id == 490704518 ? 5 : 0),
                                DisjointForbidden = true },
                            "实际装备完整核心、30天期限和模板限制保留，武器强化10锻造5 " + reward.Core.ItemId);
                    }
                }
            }
        }
        That(unique.Count == 247, "四盒全部286声明行覆盖247个独立装备模板");
        foreach (var request in new[] { Select(3, [401030066, 401030066]), Select(3, [400100084]),
            Select(3, [401030066], 255), Select(3, []) })
            That(!service.Open(first, request, out _, now).Accepted && first.Inventory.Items.Single().Core.ItemId == 490704518,
                "新兼容盒仍拒绝重复、外来、错误分类和空选择，保留源");
        foreach (var source in new[] { first.Inventory.Items.Single().Core with { EquipmentLockId = 1 },
            first.Inventory.Items.Single().Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } })
            That(!service.Open(first with { Inventory = new([new(1, Bag(3), source)]) }, Select(3, [401030066]), out _, now).Accepted,
                "兼容分解声明不绕过源安全锁和过期限制");
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, first.Inventory);
        var full = first with { Inventory = first.Inventory with { Items = first.Inventory.Items.AddRange(
            Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(slot, Bag((short)slot),
                InventoryRewardGrantService.Create(catalog.Get(16400), utcNow: now)))) } };
        That(!service.Open(full, Select(3, [401030066]), out _, now).Accepted && full.Inventory.Items.Length == range.End - range.Start + 2,
            "装备页满时整盒拒绝，不能丢奖励或改投其他容器");

        // 每个负例用私有目录，不能改共享资源缓存；仍调用真实Open而非只检查Parse。
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolated)!;
        var guarded = new InventoryBoosterService(catalog, isolated, resources.GoldLimits, resources.EpicPieces);
        const string marker = "[set disjoint instance type]\n";
        string Text(string body, string declaration = marker) => declaration + "[stackable type]\n`[booster selection]` 0\n" +
            "[booster selection num]\n0\n[booster select category]\n0 0\n" + body + "\n[/booster select category]\n";
        const string safe = "[equipment]\n401030066 1\n[/equipment]";
        definitions[10000743] = BoosterCatalog.Parse(Text(safe));
        var syntheticRole = Role(10000743);
        That(guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
            "支持范围由声明及实际模板驱动，不按四个新秀盒ID放行");
        // 同模板普通装备可分解；只限制本次礼包创建的新实例。
        definitions[10000743] = BoosterCatalog.Parse(Text("[equipment]\n16400 1\n[/equipment]"));
        var restrictedSelection = guarded.Open(syntheticRole, Select(3, [16400]), out _, now);
        That(!catalog.Get(16400).DisjointForbidden &&
            restrictedSelection.Accepted && restrictedSelection.Plan!.After.Items.Single().Core is
                { DisjointForbidden: true, DestructionSoulForbidden: false },
            "同模板礼包奖励写入不可分解实例限制，不擅自追加未知的毁装限制");
        definitions[10000743] = BoosterCatalog.Parse(Text(safe,
            "[outer]\n[inner]\n[/outer]\n" + marker + "[/inner]\n"));
        string beforeCrossedDeclaration = JsonSerializer.Serialize(syntheticRole);
        var crossed = guarded.Open(syntheticRole, Select(3, [401030066]), out var crossedReceipt, now);
        That(!crossed.Accepted && crossed.Plan is null && crossedReceipt is null &&
            JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
            "交叉闭合内层中的实例分解声明不是根级，必须整包拒绝并保留源与全部资产");
        foreach (string nested in new[]
        {
            "[outer]\n[outer]\n[/outer]\n" + marker + "[/outer]\n",
            "[outer]\n[outer]\n" + marker + "[/outer]\n[/outer]\n",
            "[outer]\n[inner]\n" + marker + "[/inner]\n[/outer]\n",
            "[outer]\n" + marker + "[/outer]\n"
        })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text(safe, nested));
            var nestedResult = guarded.Open(syntheticRole, Select(3, [401030066]), out var nestedReceipt, now);
            That(!nestedResult.Accepted && nestedResult.Plan is null && nestedReceipt is null &&
                JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
                "同名或异名嵌套外层仍开放时，实例分解声明不是根级，拒绝且资产不变");
        }
        definitions[10000743] = BoosterCatalog.Parse(Text(safe,
            "[outer]\n[/outer]\n" + marker + "[outer]\n[/outer]\n"));
        That(guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
            "两个独立且完整闭合的同名区间之间仍是根级，不因保守配对扩大拒绝范围");
        definitions[10000743] = BoosterCatalog.Parse(Text(safe, marker + marker));
        That(guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
            "完整根级空实例分解声明重复时仍沿真实开箱入口发放");
        foreach (string declaration in new[] { marker + "1\n", marker + "`value`\n", marker + "[/set disjoint instance type]\n",
            marker + marker + "[/set disjoint instance type]\n", "[/set disjoint instance type]\n",
            "[set disjoint instance type\n", "[set disjoint instance type extra]\n", "[ set disjoint instance type]\n" })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text(safe, declaration));
            That(!guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
                "有值、嵌套或闭合冲突的实例分解声明保持拒绝");
        }
        definitions[10000743] = BoosterCatalog.Parse(Text(safe + "\n" + marker, ""));
        That(!guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
            "分类内嵌实例分解声明不能按根级空声明放行");
        That(!catalog.Get(16400).DisjointForbidden, "负例普通装备确实允许分解");
        // 可分解装备已按声明放行（见上），非装备产物、钱包、碎片与契约仍整包拒绝。
        foreach (int unsafeId in new[] { 1106, 1, resources.EpicPieces.ByPiece.Keys.First(), resources.Boosters.DirectActivationItems.First() })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text(safe + "\n[etc]\n" + unsafeId + " 1\n[/etc]"));
            var rejected = guarded.Open(syntheticRole, Select(3, [401030066, unsafeId]), out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && syntheticRole.Inventory.Gold == 12345 &&
                syntheticRole.Inventory.Items.Single().Core.ItemId == 10000743,
                "实例分解声明的实际所选奖励必须为装备，非装备产物与快捷发放不能绕过 " + unsafeId);
        }
        definitions[10000743] = BoosterCatalog.Parse(Text(safe + "\n[etc]\n1106 1\n[/etc]")
            .Replace("[booster selection num]\n0", "[booster selection num]\n1", StringComparison.Ordinal));
        That(guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted &&
            !guarded.Open(syntheticRole, Select(3, [1106]), out _, now).Accepted,
            "限定兼容按实际选择预检，不因同分类内未选中的不安全候选额外拒绝");
        definitions[10000743] = BoosterCatalog.Parse(marker + "[stackable type]\n`[booster]` 0\n" +
            "[booster info]\n[equipment]\n1 401030066 1 1\n[/equipment]\n[/booster info]");
        var randomEquipment = guarded.Open(syntheticRole, new(3, 1), out var randomReceipt, now);
        var randomTemplate = catalog.Get(401030066);
        That(randomTemplate is { Kind: ItemKind.Equipment, Echon: null,
                Expiration: { Days: 30, AbsoluteTime: 0, DailyDelete: 0, WeeklyDeleteDay: null } },
            "普通限制装备池字面期望绑定完整30天装备模板，不借实际计划生成oracle");
        var randomExpected = syntheticRole.Inventory with { Items = [new(0, Bag(9),
            new ItemCore(401030066, ItemKind.Equipment, 999_999_998)
            {
                Durability = randomTemplate.Durability,
                SealFlag = randomTemplate.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0,
                ExpireTime = checked((int)Math.Min(now.ToUnixTimeSeconds() + 30 * 86400L, 2_145_823_200)),
                DisjointForbidden = true, DestructionSoulForbidden = false
            })] };
        That(randomEquipment.Accepted && randomEquipment.Plan is not null &&
            JsonSerializer.Serialize(randomEquipment.Plan.After) == JsonSerializer.Serialize(randomExpected) &&
            randomReceipt is not null && JsonSerializer.Serialize(randomReceipt) ==
                JsonSerializer.Serialize(new BoosterReceipt(10000743, 3, [new BoosterGrantedItem(401030066, 1)])) &&
            randomEquipment.Plan.ChangedCurrencies.IsEmpty && randomEquipment.Plan.OverflowItems.IsEmpty &&
            JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
            "普通装备奖池沿已确认合同保存分解限制而不增加毁装灵魂限制，完整库存及回执一致且不就地扣源");
        int unsafePiece = resources.EpicPieces.ByPiece.Keys.First();
        int unsafeContract = resources.Boosters.DirectActivationItems.First();
        That(catalog.Get(1106).Kind == ItemKind.Consumable && CurrencyWallet.RewardKind(1106) is null &&
            !resources.EpicPieces.ByPiece.ContainsKey(1106) && !resources.Boosters.DirectActivationItems.Contains(1106) &&
            CurrencyWallet.RewardKind(1) is not null && resources.EpicPieces.ByPiece.ContainsKey(unsafePiece) &&
            resources.Boosters.DirectActivationItems.Contains(unsafeContract) && !catalog.Contains(int.MaxValue),
            "普通限制奖池五类负例分别绑定普通消耗品、钱包、史诗碎片、直接激活契约与缺失模板");
        foreach (int unsafeId in new[] { 1106, 1, unsafePiece, unsafeContract, int.MaxValue })
        {
            // 前池合法也不能先发奖；第二池所有正权重候选必须预检，不能只检查随机命中项。
            definitions[10000743] = BoosterCatalog.Parse(marker + "[stackable type]\n`[booster]` 0\n" +
                "[booster info]\n[equipment]\n1 401030066 1 1\n[/equipment]\n" +
                "[etc]\n1 401030066 1 1 " + unsafeId + " 1 1\n[/etc]\n[/booster info]");
            var unsafeDefinition = definitions[10000743];
            That(unsafeDefinition.Available && unsafeDefinition.SetsDisjointInstanceType &&
                unsafeDefinition.Pools.Length == 2 && unsafeDefinition.Pools.All(pool => pool.Draws == 1 &&
                    pool.Rewards.All(reward => reward.Weight > 0)) &&
                unsafeDefinition.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(401030066, 1, 1) }) &&
                unsafeDefinition.Pools[1].Rewards.SequenceEqual(new BoosterReward[] { new(401030066, 1, 1), new(unsafeId, 1, 1) }),
                "不安全双池已完整解析且全部候选正权重，拒绝必须来自服务预检而非解析器 " + unsafeId);
            var denied = guarded.Open(syntheticRole, new(3, 1), out var deniedReceipt, now);
            That(!denied.Accepted && denied.Plan is null && deniedReceipt is null &&
                JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
                "普通限制奖池含非装备、钱包、碎片、契约或缺失模板时整包拒绝，不发前池且全部输入资产不变 " + unsafeId);
        }

        foreach (string value in new[] { "0", "3" })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text("[equipment]\n16400 1\n[/equipment]",
                "[custom ability grade]\n" + value + "\n"));
            var opened = guarded.Open(syntheticRole, Select(3, [16400]), out var receipt, now);
            That(opened.Accepted && receipt!.Rewards.Single() == new BoosterGrantedItem(16400, 1) &&
                opened.Plan!.After.Items.Single().Core == InventoryRewardGrantService.Create(catalog.Get(16400), utcNow: now) &&
                JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
                "合法根级品级声明沿真实开箱发完整模板实例，数值不改奖励或输入资产");
        }
        foreach (string declaration in new[]
        {
            "[custom ability grade]\n3\n[custom ability grade]\n-1\n",
            "[outer]\n[custom ability grade]\n3\n[/outer]\n"
        })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text(safe, declaration));
            var rejected = guarded.Open(syntheticRole, Select(3, [401030066]), out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null &&
                JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
                "畸形品级声明经真实开箱整盒拒绝，无计划、无回执且全部输入资产不变");
        }
    }

    internal static void RunRepeatedDisjointDeclarations()
    {
        const string marker = "[set disjoint instance type]\n";
        const string box = "[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[equipment]\n16400 1\n[/equipment]\n[/booster select category]\n";
        foreach (string text in new[] { marker + box + marker, marker + marker + box + marker })
        {
            var definition = BoosterCatalog.Parse(text);
            That(definition.Available && definition.SetsDisjointInstanceType && !definition.SetsEpicExchangeInstanceType &&
                definition.Selection is { SelectionCount: 1, Categories.Length: 1 } selection &&
                selection.Categories[0].Rewards is [{ ItemId: 16400, Count: 1, Upgrade: 0, Refinement: 0 }],
                "根级完整空实例分解声明幂等重复，不撤回或增添原奖励");
        }
        foreach (string declaration in new[]
        {
            marker + "1\n" + marker, marker + marker + "1\n", marker + "`value`\n" + marker,
            marker + "[outer]\n" + marker + "[/outer]\n", "[outer]\n" + marker + "[/outer]\n" + marker,
            marker + "[set disjoint instance type\n", marker + "[set disjoint instance type extra]\n",
            marker + "[ set disjoint instance type]\n", marker + marker + "[/set disjoint instance type]\n"
        })
            That(!BoosterCatalog.Parse(declaration + box).Available,
                "重复声明的每一项都必须完整、根级且无值，不能只检查第一项");
    }

    internal static void RunRepeatedDisjointSelection(TownCatalog towns, CharacterCatalog characters, ItemResources resources, DateTimeOffset now)
    {
        var catalog = resources.Items;
        var service = new InventoryBoosterService(catalog, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        // 原资源三盒全部15奖励行；首盒选3，另两盒各在两类中单选，不按产物穿戴资格收窄领取。
        foreach (var (sourceId, count, minimumLevel, epic, expectedIds) in new (int, int, int, bool, int[][])[]
        {
            (490010294, 3, 85, false, [[100300221, 100312103, 100321962]]),
            (490013512, 1, 1, true, [[100300946, 100312640, 100322533], [100322534, 100300947, 100312641]]),
            (490013513, 1, 1, true, [[100344897, 100390020, 100353080], [100390021, 100353081, 100344898]])
        })
        {
            var source = catalog.Get(sourceId);
            var definition = resources.Boosters.Get(sourceId);
            That(source is { Kind: ItemKind.Consumable, Jobs: ushort.MaxValue, RequiredSkill: 0, UseForbidden: false } &&
                source.MinimumLevel == minimumLevel && definition.Available && definition.SetsDisjointInstanceType &&
                definition.SetsEpicExchangeInstanceType == epic && definition.GrantsEpicExchangeOrigin == epic &&
                definition.Selection is { } selection && selection.SelectionCount == count && selection.Categories.Length == expectedIds.Length &&
                service.UsePlaces(sourceId) == new BoosterUsePlacesDefinition(epic, epic),
                "重复空声明三盒保留原源职业/等级、分类选数、交换来源及使用地点 " + sourceId);
            for (byte category = 0; category < expectedIds.Length; category++)
            {
                int[] ids = expectedIds[category];
                var group = definition.Selection!.Categories.Single(row => row.Category == category && row.Subcategory == 0);
                That(group.PendingRule is null && group.Rewards.Select(row => row.ItemId).SequenceEqual(ids) &&
                    group.Rewards.All(row => row.Count == 1 && row.Upgrade == 0 && row.Refinement == 0),
                    "三盒完整原奖励顺序及一件数量不变 " + sourceId + ":" + category);
                IEnumerable<int[]> vectors = count == 3 ? new[] { ids } : ids.Select(id => new[] { id });
                foreach (int[] selected in vectors)
                    foreach (var legal in BoosterNativeSelectionTests.ResourceRoles(towns, characters, source, now))
                        foreach (byte level in new[] { legal.Level, (byte)95 }.Distinct())
                        {
                            var role = legal with { Level = level };
                            string before = JsonSerializer.Serialize(role);
                            var opened = service.Open(role, Select(65, selected, category), out var receipt, now);
                            That(opened.Accepted && opened.Plan is not null && receipt is not null &&
                                receipt.Rewards.SequenceEqual(selected.Select(id => new BoosterGrantedItem(id, 1))) &&
                                opened.Plan.After.Find(Bag(65)) is null && opened.Plan.After.Items.Length == selected.Length &&
                                opened.Plan.After.Gold == role.Inventory.Gold && JsonSerializer.Serialize(role) == before,
                                "三盒每个合法职业及原最低等级完整领取且只在计划中消费源 " + sourceId + ":" + category + ":" + role.Job + ":" + level);
                            foreach (var reward in opened.Plan!.After.Items)
                            {
                                var template = catalog.Get(reward.Core.ItemId);
                                var expected = InventoryRewardGrantService.Create(template, utcNow: now) with { DisjointForbidden = true };
                                if (epic) expected = expected with { EpicExchangeOrigin = new(1, sourceId) };
                                That(selected.Contains(reward.Core.ItemId) && template.Kind == ItemKind.Equipment && reward.Uid == 0 &&
                                    reward.Core == expected && reward.Avatar is null && reward.Creature is null,
                                    "三盒完整装备核心保留，原epic两盒带不可交换来源且不授予119资格位 " + sourceId + ":" + reward.Core.ItemId);
                            }
                        }
            }
            var first = BoosterNativeSelectionTests.ResourceRoles(towns, characters, source, now).First();
            int[] valid = count == 3 ? expectedIds[0] : [expectedIds[0][0]];
            foreach (var bad in new[] { Select(65, []), Select(65, [valid[0], valid[0]]), Select(65, [1106]),
                Select(65, valid, 255), Select(65, valid, 0, 1) })
            {
                string before = JsonSerializer.Serialize(first);
                var rejected = service.Open(first, bad, out var receipt, now);
                That(!rejected.Accepted && rejected.Plan is null && receipt is null && JsonSerializer.Serialize(first) == before,
                    "三盒仍拒绝空、重复、外来及错误分类选择且全部资产不变 " + sourceId);
            }
            foreach (var role in new[] { first with { Job = 255 }, first with { Level = (byte)(minimumLevel - 1) } })
            {
                string before = JsonSerializer.Serialize(role);
                var rejected = service.Open(role, Select(65, valid), out var receipt, now);
                That(!rejected.Accepted && rejected.Plan is null && receipt is null && JsonSerializer.Serialize(role) == before,
                    "三盒不能因重复声明绕过源职业或最低等级资格 " + sourceId);
            }
        }
    }

    // 使用真实资源验证哈林首饰自选盒的奖励、领取资格、原子提交与持久化。
    internal static void RunHarlemAccessorySelection(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        const int sourceId = 490013750;
        int[][] expectedIds =
        [
            [100322540, 100300953, 100312647], [100322541, 100300954, 100312648],
            [100322542, 100300955, 100312649], [400310135, 400300134, 400320139]
        ];
        var catalog = resources.Items;
        var sourceTemplate = catalog.Get(sourceId);
        That(sourceTemplate is { Kind: ItemKind.Consumable, StackLimit: >= 2 },
            "564 source is a real consumable supporting the two-item source fixture");
        var service = new InventoryBoosterService(catalog, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var definition = resources.Boosters.Get(sourceId);
        That(definition.Available && definition.SetsDisjointInstanceType &&
            definition.Selection is { SelectionCount: 1, Categories.Length: 4 }, "564 real source shape");
        for (byte category = 0; category < 4; category++)
        {
            var group = definition.Selection!.Categories.Single(row => row.Category == category && row.Subcategory == 0);
            That(group.Rewards.Select(row => row.ItemId).SequenceEqual(expectedIds[category]) &&
                group.Rewards.All(row => row.Count == 1 && row.Upgrade == 0 && row.Refinement == 0),
                "564 exact saved 4x3 IDs, original order and one-item quantities " + category);
        }
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count, now);
        Character Role() => new(1, 0, "Harlem", 0, towns.Room, towns.InitialReturn)
        {
            Inventory = new([new(1, Bag(3), Core(sourceId, 2)), new(2, Bag(65), Core(sourceId))], Gold: 12345)
        };
        var sourceRange = ItemSlotBoundService.Range(sourceTemplate.Kind, Role().Inventory);
        That(sourceRange.Start <= 65 && sourceRange.End >= 65,
            "564 independent source-ID sentinel fits the actual consumable page at slot 65");
        for (byte category = 0; category < 4; category++)
            foreach (int id in expectedIds[category])
            {
                var template = catalog.Get(id);
                That(template.Kind == ItemKind.Equipment, "564 selected real template is equipment " + id);
                for (byte job = 0; job < 16; job++)
                    foreach (byte level in new byte[] { 1, 95 })
                    {
                        var role = Role() with { Job = job, Level = level };
                        string before = JsonSerializer.Serialize(role);
                        var opened = service.Open(role, Select(3, [id], category), out var receipt, now);
                        That(opened.Accepted && opened.Plan is not null && receipt is not null,
                            "564 real candidate must succeed, no wear-job/level grant gate " + id + ":" + job + ":" + level);
                        var after = opened.Plan!.After;
                        var reward = after.Items.Single(item => item.Core.ItemId == id);
                        var expected = InventoryRewardGrantService.CreateItem(template, reward.Position, Core(id) with { DisjointForbidden = true });
                        That(reward == expected && reward.Uid == 0 && reward.Core.Value == InventoryRewardGrantService.InitialQuality &&
                            reward.Core.Durability == template.Durability && reward.Avatar is null && reward.Creature is null &&
                            after.Find(Bag(3)) is { Uid: 1, Core.Count: 1 } && after.Find(Bag(65)) == role.Inventory.Find(Bag(65)) &&
                            after.Items.Length == 3 && after.Gold == 12345 &&
                            receipt!.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(id, 1) }) &&
                            JsonSerializer.Serialize(role) == before,
                            "564 one selected complete core only; source decremented in plan, sentinel/input unchanged " + id);
                    }
                var unchanged = Role();
                string snapshot = JsonSerializer.Serialize(unchanged);
                foreach (var bad in new[] { Select(3, []), Select(3, [id, id]), Select(3, [1106]),
                    Select(3, [id], (byte)((category + 1) % 4)), Select(3, [id], category, 1) })
                {
                    var rejected = service.Open(unchanged, bad, out var receipt, now);
                    That(!rejected.Accepted && rejected.Plan is null && receipt is null &&
                        JsonSerializer.Serialize(unchanged) == snapshot, "564 bad choice retains all assets " + id);
                }
            }
        var first = Role();
        foreach (var core in new[] { Core(sourceId, 2) with { EquipmentLockId = 1 },
            Core(sourceId, 2) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } })
        {
            var role = first with { Inventory = first.Inventory with { Items = first.Inventory.Items.SetItem(0, new(1, Bag(3), core)) } };
            string snapshot = JsonSerializer.Serialize(role);
            var rejected = service.Open(role, Select(3, [expectedIds[0][0]]), out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && JsonSerializer.Serialize(role) == snapshot,
                "564 source safety lock / instance expiration rejects without consumption");
        }
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, first.Inventory);
        var full = first with { Inventory = first.Inventory with { Items = first.Inventory.Items.AddRange(
            Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                InventoryRewardGrantService.CreateItem(catalog.Get(16400), Bag((short)slot), Core(16400)))) } };
        string fullBefore = JsonSerializer.Serialize(full);
        That(!service.Open(full, Select(3, [expectedIds[0][0]]), out var fullReceipt, now).Accepted && fullReceipt is null &&
            JsonSerializer.Serialize(full) == fullBefore, "564 equipment page full retains source and all existing instances");
        var oneFree = full with { Inventory = full.Inventory with { Items = full.Inventory.Items.RemoveAt(full.Inventory.Items.Length - 1) } };
        That(service.Open(oneFree, Select(3, [expectedIds[0][0]]), out _, now).Accepted, "564 one free equipment slot is sufficient");

        // 同既有BoosterSelectionTests使用临时SQLite，不连接玩家库。
        string root = Path.Combine(Path.GetTempPath(), "s5a9-harlem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            int ordinal = 0;
            for (byte category = 0; category < 4; category++)
                foreach (int id in expectedIds[category])
                {
                    using var owner = owners.Acquire("harlem-" + ordinal++, () => { });
                    repo.Create(owner, new("Harlem" + ordinal, towns.Room, towns.InitialReturn)
                    {
                        Job = 15, Inventory = new([new(0, Bag(3), Core(sourceId)), new(0, Bag(65), Core(sourceId))], Gold: 12345)
                    });
                    var session = new GameSession(repo, towns, owner);
                    session.Select(0);
                    session.MarkReady();
                    var saved = session.Selected!;
                    var sentinel = saved.Inventory.Find(Bag(65));
                    string before = Snapshot();
                    // Repository先写Core、槽与适用详情，再对变化槽写审计。
                    Query("CREATE TRIGGER fail_harlem BEFORE INSERT ON inventory_audit_log WHEN NEW.item_id_after=" + id +
                        " BEGIN SELECT RAISE(ABORT,'harlem late audit rollback'); END");
                    bool failed = false;
                    try { Open(); }
                    catch (SqliteException) { failed = true; }
                    That(failed && ReferenceEquals(session.Selected, saved) && Snapshot() == before,
                        "564 late SQL abort rolls back source, reward, revision, audit and UID allocation " + id);
                    Query("DROP TRIGGER fail_harlem");
                    var committed = Open();
                    That(committed.Accepted && committed.Receipt is not null &&
                        ReferenceEquals(committed.Receipt.Character, session.Selected), "564 real Session committed receipt " + id);
                    var current = session.Selected!;
                    var reward = current.Inventory.Items.Single(item => item.Core.ItemId == id);
                    var expected = InventoryRewardGrantService.CreateItem(catalog.Get(id), reward.Position, Core(id) with { DisjointForbidden = true });
                    That(reward.Uid > 0 && reward == (expected with { Uid = reward.Uid }) &&
                        current.Inventory.Find(Bag(3)) is null && current.Inventory.Find(Bag(65)) == sentinel &&
                        current.Inventory.Items.Length == 2 && current.Inventory.Items.Select(item => item.Uid).Distinct().Count() == 2 &&
                        JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(current),
                        "564 cold repository reopen keeps complete core, committed unique UID and sentinel " + id);
                    before = Snapshot();
                    var replay = Open();
                    That(!replay.Accepted && replay.Receipt is null && Snapshot() == before, "564 exhausted source cannot replay " + id);
                    That(Query("PRAGMA integrity_check") is "ok", "564 temporary DB integrity " + id);

                    InventoryResult Open() => session.PlanAndCommitBooster(role => service.Open(role,
                        Select(3, [id], category), out _, now), service.UsePlaces(sourceId), sourceId);
                    string Snapshot() => JsonSerializer.Serialize(new CharacterRepository(path).List(owner)) + ":" +
                        Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                        Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
                }
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    // 外盒只生成完整子盒实例，子盒仍等待玩家下一次真实选择。
    public static void RunSpring2020NestedBoxLinks(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        var fixedBox = resources.Boosters.Get(490705424);
        int[] fixedIds = [490705422, 490705423];
        That(fixedBox.Available && fixedBox.Selection is null && fixedBox.Pools.Length == 2 &&
            Enumerable.Range(0, 2).All(index => fixedBox.Pools[index].Draws == 1 &&
                fixedBox.Pools[index].Rewards.SequenceEqual(new BoosterReward[] { new(fixedIds[index], 1000, 1) })),
            "克隆武器套装外盒保留两个独立必得池，各发一个完整子盒");
        var selectionBox = resources.Boosters.Get(490705437);
        That(selectionBox.Available && selectionBox.Selection is { SelectionCount: 1, Categories.Length: 1 } selection &&
            selection.Categories[0] is { Category: 0, Subcategory: 0, PendingRule: null } category &&
            category.Rewards.SequenceEqual(new BoosterChoice[] { new(490705435, 1), new(490705479, 1), new(490705436, 1) }),
            "绝版稀有装扮外盒保留第2期、第5期和第9期三个子盒的一项选择");
        var cases = new[]
        {
            (SourceId: 490705424, SelectedId: 0, RewardIds: new[] { 490705422, 490705423 }),
            (SourceId: 490705437, SelectedId: 490705435, RewardIds: new[] { 490705435 }),
            (SourceId: 490705437, SelectedId: 490705479, RewardIds: new[] { 490705479 }),
            (SourceId: 490705437, SelectedId: 490705436, RewardIds: new[] { 490705436 })
        };
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        string root = Path.Combine(Path.GetTempPath(), "s5a9-spring2020-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool completed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            for (int index = 0; index < cases.Length; index++)
            {
                var entry = cases[index];
                var template = resources.Items.Get(entry.SourceId);
                var role = BoosterNativeSelectionTests.ResourceRoles(towns, characters, template, now).First();
                using var owner = owners.Acquire("spring-nested-" + index, () => { });
                var created = repo.Create(owner, new("SpringNest" + index, towns.Room, towns.InitialReturn)
                {
                    Job = role.Job, GrowType = role.GrowType, Level = role.Level, Skills = role.Skills,
                    Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(template, utcNow: now))])
                });
                var session = new GameSession(repo, towns, owner);
                session.Select(created.Slot);
                session.MarkReady();
                var saved = session.Selected!;
                string savedBefore = JsonSerializer.Serialize(saved);
                string before = Snapshot();
                BoosterOpenRequest request = entry.SelectedId == 0 ? new(3, 1) : Select(3, [entry.SelectedId]);
                var planned = service.Open(saved, request, out var receipt, now);
                That(planned.Accepted && planned.Plan is { OverflowItems.Length: 0 } && receipt is not null &&
                    receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(entry.RewardIds.Order().Select(id => new BoosterGrantedItem(id, 1))) &&
                    JsonSerializer.Serialize(saved) == savedBefore && ReferenceEquals(session.Selected, saved),
                    "春节嵌套外盒合法请求只规划原始子盒，不修改在线快照 " + entry.SourceId + ":" + entry.SelectedId);
                CheckChildren(planned.Plan!.After, entry.RewardIds, persisted: false);
                string frozenAfter = JsonSerializer.Serialize(planned.Plan.After);
                string frozenReceipt = JsonSerializer.Serialize(receipt);
                Query("CREATE TRIGGER fail_spring_2020_nested BEFORE INSERT ON inventory_audit_log WHEN NEW.item_id_after=" + entry.RewardIds[^1] +
                    " BEGIN SELECT RAISE(ABORT,'春节嵌套外盒晚期审计回滚'); END");
                Reject(() => session.PlanAndCommitBooster(_ => planned, service.UsePlaces(entry.SourceId), entry.SourceId),
                    "春节嵌套外盒全部实体写入后的晚期审计失败正确传播");
                That(ReferenceEquals(session.Selected, saved) && Snapshot() == before &&
                    JsonSerializer.Serialize(planned.Plan.After) == frozenAfter && JsonSerializer.Serialize(receipt) == frozenReceipt,
                    "春节嵌套外盒晚期失败整笔回滚源、全部子盒、详情、版本、审计及UID序列");
                Query("DROP TRIGGER fail_spring_2020_nested");
                var committed = session.PlanAndCommitBooster(_ => planned, service.UsePlaces(entry.SourceId), entry.SourceId);
                That(committed.Accepted && session.Selected!.Revision == saved.Revision + 1,
                    "春节嵌套外盒确认回滚后同一完整计划只提交一次");
                CheckChildren(session.Selected!.Inventory, entry.RewardIds, persisted: true);
                That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                    "春节嵌套外盒重开库后保留全部独立子盒及完整实例");
                before = Snapshot();
                Reject(() => repo.CommitInventory(owner, saved, planned.Plan), "春节嵌套外盒已提交的旧版本计划不能再次提交");
                BoosterReceipt? replayReceipt = null;
                var replay = session.PlanAndCommitBooster(character => service.Open(character, request, out replayReceipt, now),
                    service.UsePlaces(entry.SourceId), entry.SourceId);
                That(!replay.Accepted && replay.Plan is null && replayReceipt is null && Snapshot() == before,
                    "春节嵌套外盒耗尽重放不再发奖、增加审计或分配UID");

                string Snapshot() => JsonSerializer.Serialize(new CharacterRepository(path).List(owner)) + ":" +
                    Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                    Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
            }
            That(Query("PRAGMA integrity_check") is "ok", "春节嵌套外盒临时数据库完整");
            completed = true;
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }
        finally
        {
            if (completed) TestFiles.DeleteDirectory(root, true);
            else Console.Error.WriteLine("SPRING2020_NESTED_FIXTURE_RETAINED " + root);
        }

        void CheckChildren(InventorySnapshot inventory, int[] ids, bool persisted)
        {
            That(inventory.Items.Length == ids.Length && inventory.Items.Select(item => item.Core.ItemId).Order().SequenceEqual(ids.Order()) &&
                inventory.Items.All(item => item.Core.Count == 1 && item.Core.Kind == ItemKind.Consumable && item.ExpireTime == 0) &&
                (persisted ? inventory.Items.All(item => item.Uid > 0) && inventory.Items.Select(item => item.Uid).Distinct().Count() == ids.Length
                    : inventory.Items.All(item => item.Uid == 0)),
                "春节嵌套外盒消耗一个源，只保留所需完整子盒及正确独立UID");
            foreach (var item in inventory.Items)
            {
                var template = resources.Items.Get(item.Core.ItemId);
                var expected = InventoryRewardGrantService.CreateItem(template, item.Position,
                    InventoryRewardGrantService.Create(template, utcNow: now));
                That(template.AttachType.Trim() == "`[account]`" &&
                    JsonSerializer.Serialize(item with { Uid = 0 }) == JsonSerializer.Serialize(expected),
                    "春节嵌套子盒保留账号绑定、实际期限及全部核心详情，不替玩家选择或递归开启 " + item.Core.ItemId);
            }
        }

        static void Reject(Action action, string message)
        {
            bool failed = false;
            try { action(); }
            catch (Exception error) when (error is SqliteException or InvalidOperationException or InvalidDataException) { failed = true; }
            That(failed, message);
        }
    }

    // 保存原文只证明下列精确开启边；装扮效果及徽章镶嵌仍须各自真实消费者证明。
    public static void RunSpring2020ChildBoxOpenings(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        int[] emblemIds = [2500043, 2500042, 2500036, 2500039, 2500033,
            2510043, 2510049, 2510046, 2510037, 2510040, 2520040, 2520046, 2520043, 2520037, 2520049,
            2530049, 2530043, 2530040, 2530037, 2530046];
        var emblemBox = resources.Boosters.Get(490705423);
        That(emblemBox.Available && emblemBox.Selection is { SelectionCount: 1, Categories.Length: 1 } emblemSelection &&
            emblemSelection.Categories[0] is { Category: 0, Subcategory: 0, PendingRule: null } emblemCategory &&
            emblemCategory.Rewards.SequenceEqual(emblemIds.Select(id => new BoosterChoice(id, 2))),
            "灿烂徽章子盒保留原文二十种候选，每次一种且所选数量为两个");
        var avatarCases = new[]
        {
            (SourceId: 490705422, RewardId: 401500152, Type: 4, Selections: 0, Categories: 1),
            (SourceId: 490705435, RewardId: 39173, Type: 3, Selections: 1, Categories: 48),
            (SourceId: 490705479, RewardId: 101550535, Type: 3, Selections: 1, Categories: 24),
            (SourceId: 490705436, RewardId: 101550645, Type: 3, Selections: 1, Categories: 30)
        };
        foreach (var entry in avatarCases)
        {
            var definition = resources.AvatarPackages.Get(entry.SourceId);
            var category = definition.Categories.Single(row => row.Category == 0 && row.Subcategory == 0);
            That(definition.Available && definition.SelectionCount == entry.Selections && definition.Categories.Length == entry.Categories &&
                category.PendingRule is null && category.Rows.Contains(new AvatarPackageRow(entry.RewardId, 1, (ushort)entry.Type, 0)) &&
                (entry.Selections != 0 || category.Rows.Length == 1),
                "装扮子盒保留保存原文的分类数量与本次精确候选，不跨职业颜色拼接 " + entry.SourceId);
        }
        var cases = emblemIds.Select(id => (SourceId: 490705423, RewardId: id, Count: 2, Type: -1))
            .Concat(avatarCases.Select(entry => (entry.SourceId, entry.RewardId, Count: 1, entry.Type))).ToArray();
        var now = DateTimeOffset.UtcNow;
        var boosters = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var avatars = new InventoryAvatarPackageService(resources.Items, resources.AvatarPackages, resources.GoldLimits, resources.EpicPieces);
        string root = Path.Combine(Path.GetTempPath(), "s5a9-spring2020-child-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool completed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            for (int index = 0; index < cases.Length; index++)
            {
                var entry = cases[index];
                bool avatarPath = entry.Type >= 0;
                var sourceTemplate = resources.Items.Get(entry.SourceId);
                var rewardTemplate = resources.Items.Get(entry.RewardId);
                var role = BoosterNativeSelectionTests.ResourceRoles(towns, characters, sourceTemplate, now).First();
                AvatarPackageChoice[] choices = [];
                AvatarPackageType? avatarType = null;
                if (avatarPath)
                {
                    var definition = resources.AvatarPackages.GetAvatar(entry.RewardId);
                    That(rewardTemplate.Kind == ItemKind.Avatar && rewardTemplate.Expiration.SupportsCreation && definition.PendingRule is null &&
                        (definition.Types.IsEmpty || entry.Type < definition.Types.Length) && definition.Abilities.Contains(0),
                        "本次保存默认属性零须被真实叶目录承认，未知属性或期限类型不能被测试补造 " + entry.RewardId);
                    if (definition.RequiresAbilityChoice) choices = [new(entry.RewardId, 0)];
                    avatarType = definition.Types.IsEmpty ? new(rewardTemplate.Expiration.Days, rewardTemplate.AvatarSockets) : definition.Types[entry.Type];
                }
                else
                    That(rewardTemplate.Kind == ItemKind.Emblem, "徽章子盒实际奖励仍为徽章Kind而非替代物 " + entry.RewardId);
                byte[] body = new byte[14 + choices.Length * 5];
                BinaryPrimitives.WriteInt16LittleEndian(body, 3);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), 1);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), entry.RewardId);
                body[12] = (byte)choices.Length;
                if (choices.Length != 0)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(13), entry.RewardId);
                    body[17] = 0;
                }
                var ordinaryRequest = avatarPath ? null : BoosterRequests.Read(body);
                var avatarRequest = avatarPath ? AvatarPackageRequests.Read(body, 1, 1) : null;
                That(avatarPath ? avatarRequest!.ItemIds.SequenceEqual(new[] { entry.RewardId }) && avatarRequest.Choices.SequenceEqual(choices) :
                    ordinaryRequest!.Selection!.ItemIds.SequenceEqual(new[] { entry.RewardId }),
                    "当前请求解析器完整保留一个真实选择及必要属性选项，未声称存在该源原生抓包 " + entry.SourceId);
                using var owner = owners.Acquire("spring-child-" + index, () => { });
                var created = repo.Create(owner, new("SpringKid" + index, towns.Room, towns.InitialReturn)
                {
                    Job = role.Job, GrowType = role.GrowType, Level = role.Level, Skills = role.Skills,
                    Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(sourceTemplate, utcNow: now))])
                });
                var session = new GameSession(repo, towns, owner);
                session.Select(created.Slot);
                session.MarkReady();
                var saved = session.Selected!;
                string savedBefore = JsonSerializer.Serialize(saved);
                string before = Snapshot();
                var planned = Open(saved, out var receipt);
                That(planned.Accepted && planned.Plan is { OverflowItems.Length: 0 } && receipt is not null &&
                    receipt.ItemId == entry.SourceId && receipt.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(entry.RewardId, entry.Count) }) &&
                    JsonSerializer.Serialize(saved) == savedBefore && ReferenceEquals(session.Selected, saved),
                    "子盒合法请求只规划本次精确奖励，未改在线快照 " + entry.SourceId + ":" + entry.RewardId);
                CheckReward(planned.Plan!.After, persisted: false);
                string frozenAfter = JsonSerializer.Serialize(planned.Plan.After);
                string frozenReceipt = JsonSerializer.Serialize(receipt);
                Query("CREATE TRIGGER fail_spring_2020_child BEFORE INSERT ON inventory_audit_log WHEN NEW.item_id_after=" + entry.RewardId +
                    " BEGIN SELECT RAISE(ABORT,'春节子盒晚期审计回滚'); END");
                Reject(() => session.PlanAndCommitBooster(_ => planned, boosters.UsePlaces(entry.SourceId), entry.SourceId),
                    "子盒真实叶写入后的晚期审计失败正确传播");
                That(ReferenceEquals(session.Selected, saved) && Snapshot() == before &&
                    JsonSerializer.Serialize(planned.Plan.After) == frozenAfter && JsonSerializer.Serialize(receipt) == frozenReceipt,
                    "子盒晚期失败整笔回滚源、叶完整详情、版本、审计及UID序列，冻结计划保持不变");
                Query("DROP TRIGGER fail_spring_2020_child");
                var committed = session.PlanAndCommitBooster(_ => planned, boosters.UsePlaces(entry.SourceId), entry.SourceId);
                That(committed.Accepted && session.Selected!.Revision == saved.Revision + 1,
                    "子盒确认回滚后同一完整计划只提交一次");
                CheckReward(session.Selected!.Inventory, persisted: true);
                That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                    "子盒重开库后保留所选全部叶数量、容器及完整详情");
                before = Snapshot();
                Reject(() => repo.CommitInventory(owner, saved, planned.Plan), "子盒已提交的旧版本计划不能再次提交");
                BoosterReceipt? replayReceipt = null;
                var replay = session.PlanAndCommitBooster(character => Open(character, out replayReceipt), boosters.UsePlaces(entry.SourceId), entry.SourceId);
                That(!replay.Accepted && replay.Plan is null && replayReceipt is null && Snapshot() == before,
                    "子盒耗尽重放不发叶、不推进版本、不增加审计或分配UID");

                InventoryResult Open(Character character, out BoosterReceipt? resultReceipt) => avatarPath ?
                    avatars.Open(character, avatarRequest!, out resultReceipt, now) : boosters.Open(character, ordinaryRequest!, out resultReceipt, now);
                string Snapshot() => JsonSerializer.Serialize(new CharacterRepository(path).List(owner)) + ":" +
                    Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                    Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
                void CheckReward(InventorySnapshot inventory, bool persisted)
                {
                    That(inventory.Items.Length == 1 && inventory.Items.Single() is var item && item.Core.ItemId == entry.RewardId &&
                        item.Core.Count == entry.Count && item.Position.List == (avatarPath ? InventoryListType.Avatar : InventoryListType.Main) &&
                        (persisted ? item.Uid > 0 && (avatarPath ? item.Core.Value == item.Uid : item.Core.Value == 2) : item.Uid == 0),
                        "子盒恰好消耗一个源，只留下所选一件装扮或一栈两个徽章及正确持久UID");
                    var reward = inventory.Items.Single();
                    var core = InventoryRewardGrantService.Create(rewardTemplate, entry.Count, now);
                    if (avatarType is not null)
                    {
                        int days = rewardTemplate.Expiration.Days > 0 ? rewardTemplate.Expiration.Days : avatarType.Days;
                        core = core with { Durability = 0, ExpireTime = (rewardTemplate.Expiration with { Days = days }).Resolve(now, false, false) };
                    }
                    var expected = InventoryRewardGrantService.CreateItem(rewardTemplate, reward.Position, core);
                    if (avatarType is not null)
                    {
                        var sockets = avatarType.Sockets;
                        expected = expected with { Avatar = expected.Avatar! with
                        {
                            Socket0 = AvatarSocket.Empty(sockets.First), Socket1 = AvatarSocket.Empty(sockets.Second),
                            Socket2 = AvatarSocket.Empty(sockets.Third), Socket3 = AvatarSocket.Empty(sockets.Fourth), Socket4 = AvatarSocket.Empty(sockets.Fifth)
                        } };
                    }
                    var normalized = reward with { Uid = 0, Core = avatarPath ? reward.Core with { Value = 0 } : reward.Core };
                    That(JsonSerializer.Serialize(normalized) == JsonSerializer.Serialize(expected),
                        "子盒集成保留真实模板与期限类型生成的全部核心详情，不替代工厂或叶属性原始字段审计 " + entry.RewardId);
                }
            }
            That(Query("PRAGMA integrity_check") is "ok", "春节子盒合并增量临时数据库完整");
            completed = true;
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }
        finally
        {
            if (completed) TestFiles.DeleteDirectory(root, true);
            else Console.Error.WriteLine("SPRING2020_CHILD_FIXTURE_RETAINED " + root);
        }

        static void Reject(Action action, string message)
        {
            bool failed = false;
            try { action(); }
            catch (Exception error) when (error is SqliteException or InvalidOperationException or InvalidDataException) { failed = true; }
            That(failed, message);
        }
    }

    public static void RunSpring2020AvatarLegalOptions(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        // 分母来自已完成的四源真实元数据捕获，旧测试每行仅选一种属性。
        const string expectedKeyHash = "2F40D1F5C001B0934ACA1115D869715D8B14F72ACAD40135397EA3312409AF1F";
        int[] sourceIds = [490705422, 490705435, 490705479, 490705436];
        int[] expectedOptions = [1, 7194, 3754, 4516];
        int[] expectedRows = [1, 384, 192, 240];
        int[] expectedMasks = [65535, 65535, 4095, 32767];
        int[][] expectedDimensions = [[1], [16, 3], [12, 2], [15, 2]];
        var cases = new List<(int SourceId, byte Category, byte Subcategory, int RowIndex, AvatarPackageRow Row, byte? Ability)>();
        var keys = new System.Text.StringBuilder();
        var leaves = new HashSet<int>();
        That(characters.Jobs.Select(job => (int)job).SequenceEqual(Enumerable.Range(0, 16)), "四源消费使用实际捕获的16职业目录");
        for (int sourceIndex = 0; sourceIndex < sourceIds.Length; sourceIndex++)
        {
            int sourceId = sourceIds[sourceIndex], before = cases.Count, rows = 0;
            var source = resources.Items.Get(sourceId);
            var definition = resources.AvatarPackages.Get(sourceId);
            That(definition.Available && definition.SelectionCount == (sourceIndex == 0 ? 0 : 1) &&
                definition.Dimensions.SequenceEqual(expectedDimensions[sourceIndex]) && definition.FirstGrowTypes.IsEmpty &&
                source.Jobs == expectedMasks[sourceIndex] && source.MinimumLevel == 1 && source.MaximumLevel == int.MaxValue &&
                source.RequiredSkill == 0 && !source.UseForbidden && Permanent(source.Expiration),
                "四源精确形状、职业声明、等级技能与期限保持metadata合同 " + sourceId);
            That(!source.AllowsUse(0, 0), "0级不满足源门；不伪造NewCharacter不支持的等级 " + sourceId);
            for (int grow = 0; grow <= byte.MaxValue; grow++)
                That(definition.AllowsFirstGrowType((byte)grow), "四源未限制FirstGrowType，字节门不推断职业实现 " + sourceId + ":" + grow);
            foreach (var category in definition.Categories.OrderBy(row => row.Category).ThenBy(row => row.Subcategory))
            {
                That(category.PendingRule is null && (definition.SelectionCount != 0 || category.Rows.Length == 1) &&
                    category.Rows.Select(row => row.ItemId).Distinct().Count() == category.Rows.Length,
                    "分类不存在Pending或同ID歧义，count0为完整单行向量 " + sourceId);
                for (int rowIndex = 0; rowIndex < category.Rows.Length; rowIndex++)
                {
                    var row = category.Rows[rowIndex];
                    var leaf = resources.Items.Get(row.ItemId);
                    var avatar = resources.AvatarPackages.GetAvatar(row.ItemId);
                    bool weapon = sourceId == 490705422;
                    That(row.Count == 1 && row.Type == (weapon ? 4 : 3) && row.DefaultAbility == 0 && row.ForcedAbility is null &&
                        leaf.Kind == ItemKind.Avatar && Permanent(leaf.Expiration) && avatar.PendingRule is null &&
                        avatar.RequiresAbilityChoice == !weapon && (weapon ? avatar.Types.IsEmpty : avatar.Types.Length == 5) &&
                        (avatar.Types.IsEmpty || row.Type < avatar.Types.Length),
                        "精确坐标的类型、创建、能力选择合同 " + sourceId + ":" + row.ItemId);
                    var selectedType = avatar.Types.IsEmpty ? new AvatarPackageType(leaf.Expiration.Days, leaf.AvatarSockets) : avatar.Types[row.Type];
                    ushort socket = weapon ? (ushort)239 : (ushort)0;
                    That(selectedType.Days == 0 && leaf.AvatarSockets == new AvatarSocketTypes(socket, socket, 0, 0, 0) &&
                        selectedType.Sockets == new AvatarSocketTypes(socket, socket, 0, 0, 0),
                        "本批选中类型均永久且孔位按捕获字面合同保留 " + row.ItemId);
                    byte?[] abilities = avatar.RequiresAbilityChoice ? avatar.Abilities.Order().Select(value => (byte?)value).ToArray() : [null];
                    That(abilities.Length > 0 && (weapon ? avatar.Abilities.SetEquals(new byte[] { 0 }) : true), "不把DefaultAbility当合法属性 " + row.ItemId);
                    foreach (byte? ability in abilities)
                    {
                        cases.Add((sourceId, category.Category, category.Subcategory, rowIndex, row, ability));
                        string wire = ability?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                        keys.Append(FormattableString.Invariant($"{sourceId}|{category.Category}|{category.Subcategory}|{rowIndex}|{row.ItemId}|{row.Count}|{row.Type}|{row.DefaultAbility}|-|{(avatar.RequiresAbilityChoice ? 1 : 0)}|{wire}|{ability ?? 0}\n"));
                    }
                    rows++;
                    leaves.Add(row.ItemId);
                }
            }
            That(rows == expectedRows[sourceIndex] && cases.Count - before == expectedOptions[sourceIndex], "逐源真实选项分母 " + sourceId);
        }
        string keyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keys.ToString())));
        That(cases.Count == 15465 && leaves.Count == 705 && keyHash == expectedKeyHash,
            "15465精确坐标属性选项与独立冻结有序hash相等，不按重复叶删坐标");
        var now = DateTimeOffset.UtcNow;
        var boosters = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var avatars = new InventoryAvatarPackageService(resources.Items, resources.AvatarPackages, resources.GoldLimits, resources.EpicPieces);
        string root = Path.Combine(Path.GetTempPath(), "s5a9-spring-avatar-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool completed = false;
        int requests = 0, accepted = 0, rejected = 0;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            foreach (var entry in cases) Consume(entry, 0, 1, 0, expectedAccepted: true);
            foreach (int sourceId in sourceIds)
            {
                var entry = cases.First(row => row.SourceId == sourceId);
                foreach (byte job in characters.Jobs)
                    Consume(entry, job, 1, 0, expectedAccepted: true);
                // 建角仅允许1..95级，正式消费检查两个合法端点；0级只核纯门。
                Consume(entry, 0, 1, 0, expectedAccepted: true);
                Consume(entry, 0, 95, 0, expectedAccepted: true);
                // 低四位0已由合法职业激活覆盖；16另经会话核对高四位编码。
                for (byte grow = 1; grow <= 16; grow++) Consume(entry, 0, 1, grow, expectedAccepted: true);
            }
            var noChoice = cases.Single(row => row.SourceId == 490705422);
            Consume((noChoice.SourceId, noChoice.Category, noChoice.Subcategory, noChoice.RowIndex, noChoice.Row, (byte?)0), 0, 1, 0, expectedAccepted: true);
            Consume((noChoice.SourceId, noChoice.Category, noChoice.Subcategory, noChoice.RowIndex, noChoice.Row, (byte?)byte.MaxValue), 0, 1, 0, expectedAccepted: true);
            That(requests == 15603 && accepted == 15603 && rejected == 0 && Query("PRAGMA integrity_check") is "ok",
                "15465选项+64合法职业激活+8级别+64Grow+2哨兵；保留源职业声明，单库完整");
            Console.WriteLine($"SPRING_AVATAR_OPTIONS_COMPLETE semantic=15465 requests={requests} accepted={accepted} rejected={rejected} keyHash={keyHash}");
            completed = true;

            void Consume((int SourceId, byte Category, byte Subcategory, int RowIndex, AvatarPackageRow Row, byte? Ability) entry,
                byte job, byte level, byte grow, bool expectedAccepted)
            {
                int index = requests;
                var source = resources.Items.Get(entry.SourceId);
                var leaf = resources.Items.Get(entry.Row.ItemId);
                var profile = characters.Get(job);
                AvatarPackageChoice[] choices = entry.Ability is byte choice ? [new(entry.Row.ItemId, choice)] : [];
                byte[] body = new byte[14 + choices.Length * 5];
                BinaryPrimitives.WriteInt16LittleEndian(body, 3);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), 1);
                body[6] = entry.Category;
                body[7] = entry.Subcategory;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), entry.Row.ItemId);
                body[12] = (byte)choices.Length;
                if (entry.Ability is byte wireAbility)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(13), entry.Row.ItemId);
                    body[17] = wireAbility;
                }
                var request = AvatarPackageRequests.Read(body, 1, 1);
                That(request.Slot == 3 && request.Count == 1 && request.Category == entry.Category && request.Subcategory == entry.Subcategory &&
                    request.ItemIds.SequenceEqual(new[] { entry.Row.ItemId }) && request.Choices.SequenceEqual(choices), "真实CMD160解析保留精确坐标与属性 " + index);
                using var owner = owners.Acquire("spring-avatar-" + index, () => { });
                var created = repository.Create(owner, new("SprOpt" + index, towns.Room, towns.InitialReturn)
                {
                    Job = job, Level = level, GrowType = grow, Skills = profile.Skills,
                    Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(source, utcNow: now))])
                });
                var session = new GameSession(repository, towns, owner);
                try
                {
                    session.Select(created.Slot);
                    session.MarkReady();
                    var before = session.Selected!;
                    string beforeJson = JsonSerializer.Serialize(before);
                    string persistedBefore = expectedAccepted ? "" : JsonSerializer.Serialize(repository.List(owner));
                    string countersBefore = expectedAccepted ? "" : Counters();
                    BoosterReceipt? receipt = null;
                    InventorySnapshot? plannedAfter = null;
                    var result = session.PlanAndCommitBooster(current =>
                    {
                        string immutableBefore = JsonSerializer.Serialize(current);
                        var planned = avatars.Open(current, request, out receipt, now);
                        That(immutableBefore == JsonSerializer.Serialize(current), "每例规划不就地修改角色 " + index);
                        if (expectedAccepted)
                        {
                            That(planned.Accepted && planned.Plan is { OverflowItems.Length: 0 } && planned.Plan.ChangedCurrencies.IsEmpty &&
                                receipt is not null && receipt.ItemId == entry.SourceId && receipt.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(entry.Row.ItemId, 1) }),
                                "每例仅规划精确一件且无溢出、额外货币或奖励 " + index);
                            plannedAfter = planned.Plan!.After;
                            CheckReward(plannedAfter, persisted: false);
                        }
                        return planned;
                    }, boosters.UsePlaces(entry.SourceId), entry.SourceId);
                    That(JsonSerializer.Serialize(before) == beforeJson, "提交不修改原角色快照 " + index);
                    if (expectedAccepted)
                    {
                        var after = session.Selected!;
                        That(result.Accepted && result.Plan is not null && plannedAfter is not null && after.Revision == before.Revision + 1 &&
                            JsonSerializer.Serialize(after with { Inventory = before.Inventory, Revision = before.Revision }) == beforeJson,
                            "只推进一次库存版本，保持角色其他字段 " + index);
                        CheckReward(after.Inventory, persisted: true);
                        var reward = after.Inventory.Items.Single();
                        var normalized = after.Inventory with { Items = [reward with { Uid = 0, Core = reward.Core with { Value = 0 } }] };
                        That(JsonSerializer.Serialize(normalized) == JsonSerializer.Serialize(plannedAfter), "完整提交库存等于原计划，仅生成真实UID " + index);
                        accepted++;
                    }
                    else
                    {
                        That(!result.Accepted && result.Plan is null && receipt is null && ReferenceEquals(before, session.Selected) &&
                            JsonSerializer.Serialize(repository.List(owner)) == persistedBefore && Counters() == countersBefore,
                            "源资格拒绝保持整份库存、角色、版本、审计及UID序列 " + index);
                        rejected++;
                    }
                    That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                        "每例新Repository重开同库保留精确UID/Core/能力/期限/孔位及扣源 " + index);
                }
                finally { session.End(); }
                requests++;
                if (requests % 1000 == 0) Console.WriteLine($"SPRING_AVATAR_OPTIONS_PROGRESS requests={requests}/15603 accepted={accepted} rejected={rejected}");

                void CheckReward(InventorySnapshot inventory, bool persisted)
                {
                    That(inventory.Items.Length == 1, "扣尽唯一源且只留一个目标装扮 " + index);
                    var reward = inventory.Items.Single();
                    byte ability = entry.SourceId == 490705422 ? (byte)0 : entry.Ability!.Value;
                    ushort socket = entry.SourceId == 490705422 ? (ushort)239 : (ushort)0;
                    That(reward.Core.ItemId == entry.Row.ItemId && reward.Core.Kind == ItemKind.Avatar && reward.Core.Count == 1 &&
                        reward.Position == new InventorySlot(InventoryListType.Avatar, 0) && reward.Core.Durability == ability && reward.Core.ExpireTime == 0 &&
                        (persisted ? reward.Uid > 0 && reward.Core.Value == reward.Uid : reward.Uid == 0 && reward.Core.Value == 0),
                        "精确Item/Kind/容器首格/能力/永久期限/UID-Core身份 " + index);
                    var core = InventoryRewardGrantService.Create(leaf, utcNow: now) with { Durability = ability, ExpireTime = 0 };
                    var expected = InventoryRewardGrantService.CreateItem(leaf, new(InventoryListType.Avatar, 0), core);
                    expected = expected with { Avatar = expected.Avatar! with
                    {
                        Socket0 = AvatarSocket.Empty(socket), Socket1 = AvatarSocket.Empty(socket),
                        Socket2 = AvatarSocket.Empty(0), Socket3 = AvatarSocket.Empty(0), Socket4 = AvatarSocket.Empty(0)
                    } };
                    var normalized = reward with { Uid = 0, Core = reward.Core with { Value = 0 } };
                    That(JsonSerializer.Serialize(normalized) == JsonSerializer.Serialize(expected), "逐例完整核心、详情及五孔相等，保留模板其他字段 " + index);
                }
            }

            string Counters() => Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }
        finally
        {
            if (completed) TestFiles.DeleteDirectory(root, true);
            else Console.Error.WriteLine($"SPRING_AVATAR_OPTIONS_FIXTURE_RETAINED {root} completedRequests={requests} accepted={accepted} rejected={rejected}");
        }

        static bool Permanent(ItemExpiration expiration) => expiration.SupportsCreation && expiration.Days == 0 && expiration.AbsoluteTime == 0 &&
            expiration.DailyDelete == 0 && expiration.WeeklyDeleteDay is null;
    }

    static BoosterDefinition Parse(string category, int count = 1) => BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n" +
        "[booster selection num]\n" + count + "\n[booster select category]\n" + category + "\n[/booster select category]");
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static BoosterOpenRequest Select(short slot, int[] ids, byte category = 0, byte subcategory = 0, int count = 1) =>
        new(slot, count, new(category, subcategory, ids));
}
