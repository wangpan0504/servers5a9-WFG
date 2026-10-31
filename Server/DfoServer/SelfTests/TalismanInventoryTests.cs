using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TalismanInventoryTests
{
    const int StoneId = 100400002, Stone2Id = 100400003, RuneId = 10605203, OtherRuneId = 10605204,
        ForeignRuneId = 10605205, MaterialId = 490014022;
    const long Now = 1_900_000_000;
    static readonly ItemTemplate Stone = new(StoneId, ItemKind.Talisman, 34, 95, 0, 1, 0, 3, 97, "[trade]", 1, 0, "stone");
    static readonly ItemTemplate Stone2 = new(Stone2Id, ItemKind.Talisman, 34, 95, 0, 1, 0, 3, 97, "[trade]", 1, 0, "stone2");
    static readonly ItemTemplate Rune = new(RuneId, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[trade]", 1, 0, "rune");
    // 与本角色首转(1)不符的符文，用于当前转职合成的材料资格用例。
    static readonly ItemTemplate ForeignRune = new(ForeignRuneId, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[trade]", 1, 0, "rune3");
    static readonly ItemTemplate Material = new(MaterialId, ItemKind.Material, 0, 0, 0, 9999, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "material");
    static readonly GoldLimitCatalog Limits = new("[gold limit from level]\n0 1000000000\n[/gold limit from level]");
    static readonly EpicPieceCatalog Pieces = new("[piece list]\n50000 50001\n[/piece list]");

    public static void RunCore()
    {
        int start = Count;
        var catalog = Catalog();
        var empty = Character();
        var granted = InventoryRewardGrantService.TryGrantBatch(empty, [new(StoneId, 1), new(RuneId, 12)], catalog, Limits, Pieces,
            DateTimeOffset.FromUnixTimeSeconds(Now));
        That(granted.Accepted && granted.Plan!.After.Find(new(InventoryListType.Talisman, 0))?.Core.Kind == ItemKind.Talisman &&
            granted.Plan.After.Find(new(InventoryListType.Talisman, 70))?.Core.Count == 12 &&
            granted.Plan.After.Items.All(item => item.Position.List == InventoryListType.Talisman), "护石与符文奖励自动进入各自专用分页");
        var newStone = granted.Plan!.After.Find(new(InventoryListType.Talisman, 0))!.Core;
        var colors = TalismanSocketColors.Read(newStone);
        That(colors.IsComplete && new[] { colors.First, colors.Second, colors.Third }.Distinct().Count() == 3,
            "新护石一次生成三种有效孔色");
        That(ItemSlotBoundService.Range(ItemKind.Talisman, 0) == ((short)0, (short)69) &&
            ItemSlotBoundService.Range(ItemKind.Rune, 0) == ((short)70, (short)209) &&
            ItemSlotBoundService.Allows(0, new(InventoryListType.Equipment, 35), ItemKind.Talisman) &&
            !ItemSlotBoundService.Allows(0, new(InventoryListType.Main, 65), ItemKind.Rune), "专用分页容量与第二护石槽不受普通背包扩容影响");

        var originalCore = new ItemCore(StoneId, ItemKind.Equipment, 999999998)
        { Rune = 0x4321, SortLockFlag = 1, EquipmentLockId = 3, Extension114 = new(0xAA, 0xFE030201), Extension119 = 9 };
        var legacyStone = new InventoryItem(10, new(InventoryListType.Main, 9), originalCore);
        var legacyRune = new InventoryItem(20, new(InventoryListType.Main, 65), new(RuneId, ItemKind.Consumable, 2001)
        { SortLockFlag = 1, EquipmentLockId = 4, Extension97 = 123 });
        var old = Character(legacyStone, legacyRune);
        var migrated = InventoryKindCompatibility.Plan(old, catalog);
        var migratedRunes = migrated.Plan!.After.Items.Where(item => item.Core.Kind == ItemKind.Rune).ToArray();
        That(migrated.Accepted && migrated.Plan.Before == old.Inventory && old.Inventory.Items[0] == legacyStone &&
            old.Inventory.Items[1] == legacyRune && migrated.Plan.After.Find(new(InventoryListType.Talisman, 0)) is { Uid: 10 } migratedStone &&
            migratedStone.Core == originalCore with { Kind = ItemKind.Talisman }, "历史护石迁移保留UID、孔色、未知位与锁，不改原快照");
        That(migratedRunes.Select(item => item.Core.Count).SequenceEqual([999, 999, 3]) &&
            migratedRunes.Count(item => item.Uid == 20) == 1 && migratedRunes.Count(item => item.Uid == 0) == 2 &&
            migratedRunes.All(item => item.Core.EquipmentLockId == 4 && item.Core.Extension97 == 123), "历史超限符文守恒拆分，额外堆叠不复用原UID");
        var cargoRune = legacyRune with { Position = new(InventoryListType.AccountCargo, 2) };
        var cargo = Character(cargoRune) with { Inventory = new([cargoRune]) { AccountCargo = new(Capacity: 4) } };
        var cargoResult = InventoryKindCompatibility.Plan(cargo, catalog);
        That(cargoResult.Accepted && cargoResult.Plan!.After.Items.All(item => item.Position.List == InventoryListType.AccountCargo) &&
            cargoResult.Plan.After.Find(cargoRune.Position)?.Uid == cargoRune.Uid && cargoResult.Plan.AccountCargoChanged,
            "账号仓库旧符文保持账号所有权及原格，拆分参与共享版本提交");
        var fullStones = Enumerable.Range(0, 70).Select(slot => StoneItem(slot + 100, (short)slot)).Append(legacyStone).ToArray();
        var noSpace = InventoryKindCompatibility.Plan(Character(fullStones), catalog);
        That(!noSpace.Accepted && noSpace.Plan is null && fullStones[^1] == legacyStone, "专用背包不足时迁移整批拒绝，不丢旧资产");
        var incoming = InventoryRewardGrantService.TryGrantItems(empty, [legacyStone with { Core = originalCore with { EquipmentLockId = 0 } },
            legacyRune with { Core = legacyRune.Core with { EquipmentLockId = 0 } }], catalog, Limits, Pieces, DateTimeOffset.FromUnixTimeSeconds(Now));
        That(incoming.Accepted && incoming.Plan!.After.Items.Count(item => item.Uid == 20) == 1 &&
            incoming.Plan.After.Items.Where(item => item.Core.Kind == ItemKind.Rune).Sum(item => item.Core.Count) == 2001,
            "旧邮件附件在领取输出归一分类并守恒拆分，不重写原附件身份");

        var service = new InventoryTalismanService(catalog, _ => 0);
        var worn = StoneItem(1, 34, InventoryListType.Equipment);
        var source = new InventoryItem(2, new(InventoryListType.Talisman, 70), new(RuneId, ItemKind.Rune, 3));
        var wearer = Character(worn, source);
        var equipped = service.Equip(wearer, new(210, 70, RuneId), Now);
        That(equipped.Accepted && equipped.Plan!.After.Find(source.Position)?.Core.Count == 2 &&
            equipped.Plan.After.Find(new(InventoryListType.Talisman, 210)) is { Uid: 0, Core.Count: 1 } &&
            TalismanSocketColors.Read(worn.Core).First == catalog.Rune(RuneId)!.SocketColor,
            "装载从堆叠分出一枚，且只接受与孔色一致的符文");
        That(!service.Equip(wearer, new(212, 70, RuneId), Now).Accepted, "孔色与符文阵营不一致时拒绝装载");
        var otherSource = new InventoryItem(5, new(InventoryListType.Talisman, 70), new(OtherRuneId, ItemKind.Rune, 3));
        That(service.Equip(Character(worn, otherSource), new(210, 70, OtherRuneId), Now).Accepted,
            "孔色一致即可装入，符文技能与护石技能不同也接受");
        var twinStone = new InventoryItem(21, new(InventoryListType.Equipment, 35),
            new TalismanSocketColors(5, 5, 3).Apply(new(Stone2Id, ItemKind.Talisman, 999999998)));
        That(InventoryMoveService.Allows(Character(), new(InventoryListType.Equipment, 34), worn.Core, catalog),
            "空护石槽允许装备本职业技能护石");
        That(!InventoryMoveService.Allows(Character(worn, twinStone), new(InventoryListType.Equipment, 35), worn.Core, catalog),
            "两个护石槽不能同时装备同一技能的两块护石");
        var actual = equipped.Plan!.After.Find(new(InventoryListType.Talisman, 210))! with { Uid = 3 };
        var afterEquip = wearer with { Inventory = equipped.Plan.After with { Items = equipped.Plan.After.Items.Replace(equipped.Plan.After.Find(actual.Position)!, actual) } };
        var removed = service.Remove(afterEquip, 210, Now);
        That(removed.Accepted && removed.Plan!.After.Find(source.Position)?.Core.Count == 3 && removed.Plan.After.Find(actual.Position) is null,
            "卸除符文完整合回背包，已装载位置清空");
        var onlyLoaded = Character(worn, actual);
        var whileLoaded = InventoryRewardGrantService.TryGrantBatch(onlyLoaded, [new(RuneId, 2)], catalog, Limits, Pieces,
            DateTimeOffset.FromUnixTimeSeconds(Now));
        That(whileLoaded.Accepted && whileLoaded.Plan!.After.Find(actual.Position)?.Core.Count == 1 &&
            whileLoaded.Plan.After.Find(source.Position)?.Core.Count == 2,
            "发放同名符文只能合入背包，不能把装载槽增加为多枚");
        That(service.Equip(afterEquip, new(211, 70, RuneId), Now).Accepted, "同一护石孔色相同的两孔可重复装入同阵营同技能符文");
        var secondStone = StoneItem(4, 35, InventoryListType.Equipment);
        That(service.Equip(afterEquip with { Inventory = afterEquip.Inventory with { Items = afterEquip.Inventory.Items.Add(secondStone) } },
            new(213, 70, RuneId), Now).Accepted, "两块护石各自独立，可各装同色符文");
        That(!service.Equip(wearer with { Inventory = new([worn with { Core = worn.Core with { EquipmentLockId = 1 } }, source]) },
            new(210, 70, RuneId), Now).Accepted, "锁定护石拒绝装载，材料不变");
        // 孔位绑定装备槽，护石离开 34 会把符文留给下一枚护石；卸石前必须先卸符文。
        var unequip = new InventoryMoveRequest(new(InventoryListType.Equipment, 34), StoneId, 1,
            new(InventoryListType.Talisman, 0), 0, 0, 0, 0, 0);
        That(!InventoryMoveService.Plan(afterEquip, unequip, catalog, Now).Accepted, "带已装符文的护石拒绝卸下");
        That(!InventoryMoveService.Plan(Character(worn, actual, StoneItem(6, 0)),
            new(new(InventoryListType.Talisman, 0), StoneId, 1, new(InventoryListType.Equipment, 34), StoneId, 0, 0, 0, 0),
            catalog, Now).Accepted, "带已装符文的护石不能与背包护石互换，符文不会错挂到另一枚");
        That(InventoryMoveService.Plan(afterEquip with { Inventory = removed.Plan!.After }, unequip, catalog, Now).Accepted,
            "符文全部卸除后护石可以正常卸下");
        var allSlots = Enumerable.Range(70, 140).Select(slot => new InventoryItem(slot + 100,
            new(InventoryListType.Talisman, (short)slot), new(OtherRuneId, ItemKind.Rune, 999))).Append(actual).ToArray();
        var refused = service.Remove(Character(allSlots), 210, Now);
        That(!refused.Accepted && refused.Plan is null, "满符文分页卸除失败，不删除已装载实例");
        That(!InventoryDeleteService.TryDelete(actual, actual.Core.Value, 1, out var deleted) && deleted is null,
            "已装载符文拒绝通用删除与出售，资产只能经卸除命令归还");
        That(!InventoryLockService.Plan(Character(worn, actual).Inventory, actual.Position,
            InventoryLockOperation.EquipmentLock, catalog, Now).Accepted, "已装载符文不可加锁，避免卸除死锁");

        var target = StoneItem(11, 0) with { Core = TalismanSocketColors.Read(originalCore).Apply(originalCore with { Kind = ItemKind.Talisman, EquipmentLockId = 0 }) };
        var material = new InventoryItem(12, new(InventoryListType.Main, 121), new(MaterialId, ItemKind.Material, 5));
        var costs = new TalismanRefineCost(MaterialId, new Dictionary<int, int> { [3] = 2 }.ToFrozenDictionary());
        var refined = service.Refine(Character(target, material), new(121, 0), costs, Now);
        var refinedCore = refined.Plan!.After.Find(target.Position)!.Core;
        That(refined.Accepted && refined.Plan.After.Find(material.Position)?.Core.Count == 3 &&
            refinedCore.Rune == originalCore.Rune && refinedCore.Extension119 == originalCore.Extension119 &&
            refinedCore.Extension114.Flag == originalCore.Extension114.Flag && (refinedCore.Extension114.Value >> 24) == 0xFE,
            "重锻按品级足额扣料，仅替换三孔颜色，其他完整实例字段保留");
        That(refinedCore == target.Core && refined.Plan.ChangedSlots.SequenceEqual(new[] { material.Position }),
            "重锻允许抽回原孔色并照常扣料，值未变的目标不进入通用库存差量，回执后仍须恢复目标显示");
        That(!service.Refine(Character(target, material with { Core = material.Core with { Value = 1 } }), new(121, 0), costs, Now).Accepted,
            "重锻材料不足不返回扣料计划");

        var compoundCosts = new TalismanCompoundCost(new Dictionary<(int, int, int), (int, int, int, int)>
        {
            [(0, 1, 2)] = (3, 64_000, 0, 10_000)
        }.ToFrozenDictionary());
        var compoundService = new InventoryTalismanCompoundService(catalog, Limits, Pieces, _ => 0);
        var runeStack = new InventoryItem(30, new(InventoryListType.Talisman, 70), new(RuneId, ItemKind.Rune, 3));
        var payer = Character(runeStack);
        payer = payer with { Inventory = CurrencyWallet.Set(payer.Inventory, CurrencyKind.Gold, 100_000) };
        var compounded = compoundService.Compound(payer, new TalismanCompoundRequest(0, 1, 2, [70, 70, 70]),
            compoundCosts, Now, out var compoundReceipt);
        // 回执必须回报被扣减的材料格：回执前的预载只发产物槽时，客户端重画会把材料画回背包再删除。
        That(compounded.Accepted && compoundReceipt is { ProductSlot: 70, ItemId: RuneId, Upgraded: false, SourceSlots: [70] } &&
            compounded.Plan!.After.Find(new(InventoryListType.Talisman, 70))?.Core.Count == 1 &&
            CurrencyWallet.Get(compounded.Plan.After, CurrencyKind.Gold) == 36_000,
            "符文合成按同一格三次引用扣三件、扣足金币并只产出同品级一件，回执按格合并材料引用");
        var broke = payer with { Inventory = CurrencyWallet.Set(payer.Inventory, CurrencyKind.Gold, 100) };
        var unpaid = compoundService.Compound(broke, new TalismanCompoundRequest(0, 1, 2, [70, 70, 70]),
            compoundCosts, Now, out var unpaidReceipt);
        That(!unpaid.Accepted && unpaid.RejectReason == "compound-gold" && unpaidReceipt is null,
            "金币不足时合成拒绝且不返回扣料计划");
        var foreignSource = new InventoryItem(31, new(InventoryListType.Talisman, 71), new(ForeignRuneId, ItemKind.Rune, 3));
        var foreign = Character(foreignSource) with { Inventory = CurrencyWallet.Set(Character(foreignSource).Inventory, CurrencyKind.Gold, 100_000) };
        var foreignResult = compoundService.Compound(foreign, new TalismanCompoundRequest(0, 1, 2, [71, 71, 71]),
            compoundCosts, Now, out var foreignReceipt);
        That(!foreignResult.Accepted && foreignResult.RejectReason == "compound-class" && foreignReceipt is null,
            "当前转职合成拒绝其他转职的符文材料");

        // 升档权重来自 [compound info] 末尾两列：普通合成按权重抽升档，当前转职合成与末档恒为同档。
        var upgradeCatalog = UpgradeCatalog();
        var upgradeCosts = new TalismanCompoundCost(new Dictionary<(int, int, int), (int, int, int, int)>
        {
            [(0, 0, 0)] = (3, 12_000, 7_500, 6_000),
            [(0, 0, 1)] = (3, 24_000, 5_000, 6_000),
            [(0, 0, 2)] = (3, 32_000, 0, 6_000),
            [(1, 0, 2)] = (3, 38_000, 2_500, 3_000),
            [(1, 0, 3)] = (3, 64_000, 0, 3_000),
            [(0, 1, 0)] = (3, 64_000, 0, 10_000),
            [(1, 1, 2)] = (3, 128_000, 0, 10_000)
        }.ToFrozenDictionary());
        var commonRune = new InventoryItem(41, new(InventoryListType.Talisman, 70), new(10605311, ItemKind.Rune, 3));
        var commonRole = Character(commonRune);
        commonRole = commonRole with { Inventory = CurrencyWallet.Set(commonRole.Inventory, CurrencyKind.Gold, 100_000) };
        // 护石不堆叠，三次引用必须来自三个不同格。
        var rareStones = new[]
        {
            new InventoryItem(42, new(InventoryListType.Talisman, 0), new(100400021, ItemKind.Talisman, 1)),
            new InventoryItem(44, new(InventoryListType.Talisman, 1), new(100400021, ItemKind.Talisman, 1)),
            new InventoryItem(45, new(InventoryListType.Talisman, 2), new(100400021, ItemKind.Talisman, 1))
        };
        var rareRole = Character(rareStones);
        rareRole = rareRole with { Inventory = CurrencyWallet.Set(rareRole.Inventory, CurrencyKind.Gold, 100_000) };

        var upgradedService = new InventoryTalismanCompoundService(upgradeCatalog, Limits, Pieces, Rolls(0, 0));
        var upgradedRune = upgradedService.Compound(commonRole, new TalismanCompoundRequest(0, 0, 0, [70, 70, 70]),
            upgradeCosts, Now, out var upgradedRuneReceipt);
        That(upgradedRune.Accepted && upgradedRuneReceipt is { Upgraded: true, ItemId: 10605312 } &&
            upgradedRune.Plan!.After.Items.Any(item => item.Core.ItemId == 10605312) &&
            CurrencyWallet.Get(upgradedRune.Plan.After, CurrencyKind.Gold) == 88_000,
            "普通合成抽中升档时产出高一级符文并按行扣料扣费");

        var boundaryService = new InventoryTalismanCompoundService(upgradeCatalog, Limits, Pieces, Rolls(7_500, 0));
        var boundaryRune = boundaryService.Compound(commonRole, new TalismanCompoundRequest(0, 0, 0, [70, 70, 70]),
            upgradeCosts, Now, out var boundaryReceipt);
        That(boundaryRune.Accepted && boundaryReceipt is { Upgraded: false, ItemId: 10605311 } &&
            boundaryRune.Plan!.After.Items.Any(item => item.Core.ItemId == 10605311),
            "升档权重边界上（骰值等于升档权重）按同档产出");

        var currentClassService = new InventoryTalismanCompoundService(upgradeCatalog, Limits, Pieces, Rolls(0));
        var currentClassRune = currentClassService.Compound(commonRole, new TalismanCompoundRequest(0, 1, 0, [70, 70, 70]),
            upgradeCosts, Now, out var currentClassReceipt);
        That(currentClassRune.Accepted && currentClassReceipt is { Upgraded: false, ItemId: 10605311 } &&
            currentClassRune.Plan!.After.Items.Any(item => item.Core.ItemId == 10605311) &&
            CurrencyWallet.Get(currentClassRune.Plan.After, CurrencyKind.Gold) == 36_000,
            "当前转职合成即使骰值为零也保持同品级");

        var rareRune = new InventoryItem(43, new(InventoryListType.Talisman, 70), new(10605313, ItemKind.Rune, 3));
        var rareRuneRole = Character(rareRune);
        rareRuneRole = rareRuneRole with { Inventory = CurrencyWallet.Set(rareRuneRole.Inventory, CurrencyKind.Gold, 100_000) };
        var terminalService = new InventoryTalismanCompoundService(upgradeCatalog, Limits, Pieces, Rolls(0));
        var terminalRune = terminalService.Compound(rareRuneRole, new TalismanCompoundRequest(0, 0, 2, [70, 70, 70]),
            upgradeCosts, Now, out var terminalReceipt);
        That(terminalRune.Accepted && terminalReceipt is { Upgraded: false, ItemId: 10605313 },
            "稀有符文已是末档，升档权重为零时只做同档重抽");

        var stoneService = new InventoryTalismanCompoundService(upgradeCatalog, Limits, Pieces, Rolls(0, 0));
        var upgradedStone = stoneService.Compound(rareRole, new TalismanCompoundRequest(1, 0, 2, [0, 1, 2]),
            upgradeCosts, Now, out var upgradedStoneReceipt);
        That(upgradedStone.Accepted && upgradedStoneReceipt is { Upgraded: true, ItemId: 100400022, SourceSlots: [0, 1, 2] } &&
            upgradedStone.Plan!.After.Items.Any(item => item.Core.ItemId == 100400022) &&
            CurrencyWallet.Get(upgradedStone.Plan.After, CurrencyKind.Gold) == 62_000,
            "稀有护石普通合成抽中升档后产出神器护石，回执列出三个被扣减的材料格");

        // 护石窗口整理：请求里的分类是客户端页面号 18=符文页、19=护石页，不是物品类别；
        // 已装符文 210–215 绑定装备槽，任何一页整理都不得移动。
        var sortCatalog = SortCatalog();
        var sortStones = new[]
        {
            new InventoryItem(101, new(InventoryListType.Talisman, 5), new(100400011, ItemKind.Talisman, 1)),
            new InventoryItem(102, new(InventoryListType.Talisman, 12), new(100400012, ItemKind.Talisman, 1)),
            new InventoryItem(103, new(InventoryListType.Talisman, 30), new(100400013, ItemKind.Talisman, 1))
        };
        var sortRunes = new[]
        {
            new InventoryItem(111, new(InventoryListType.Talisman, 71), new(10605302, ItemKind.Rune, 2)),
            new InventoryItem(112, new(InventoryListType.Talisman, 80), new(10605304, ItemKind.Rune, 2)),
            new InventoryItem(113, new(InventoryListType.Talisman, 100), new(10605303, ItemKind.Rune, 2) { SortLockFlag = 1 }),
            new InventoryItem(114, new(InventoryListType.Talisman, 150), new(10605301, ItemKind.Rune, 2))
        };
        var equippedRune = new InventoryItem(115, new(InventoryListType.Talisman, 210), new(10605301, ItemKind.Rune, 1));
        var wornStone = new InventoryItem(116, new(InventoryListType.Equipment, 34), new(100400011, ItemKind.Talisman, 1));
        var sortable = Character([.. sortStones, .. sortRunes, equippedRune, wornStone]);
        var byLevel = InventorySortService.Plan(sortable.Inventory, (ItemKind)19, 0, sortCatalog, Now, InventoryListType.Talisman);
        That(byLevel.Accepted && byLevel.Plan!.Before == sortable.Inventory &&
            byLevel.Plan.After.Find(new(InventoryListType.Talisman, 0))?.Uid == 101 &&
            byLevel.Plan.After.Find(new(InventoryListType.Talisman, 1))?.Uid == 103 &&
            byLevel.Plan.After.Find(new(InventoryListType.Talisman, 2))?.Uid == 102, "护石页默认整理按等级从高到低补齐槽0–2");
        var levelPlan = byLevel.Plan!;
        That(levelPlan.After.Find(new(InventoryListType.Talisman, 5)) is null &&
            levelPlan.After.Find(new(InventoryListType.Talisman, 100))?.Uid == 113 &&
            levelPlan.After.Find(new(InventoryListType.Talisman, 71))?.Uid == 111 &&
            levelPlan.After.Find(new(InventoryListType.Talisman, 210))?.Uid == 115 &&
            levelPlan.After.Find(new(InventoryListType.Equipment, 34))?.Uid == 116,
            "护石页整理不移动符文页、整理锁、已装符文与穿戴槽");
        var byGrade = InventorySortService.Plan(sortable.Inventory, (ItemKind)19, 1, sortCatalog, Now, InventoryListType.Talisman);
        That(byGrade.Accepted && byGrade.Plan!.After.Find(new(InventoryListType.Talisman, 0))?.Uid == 102 &&
            byGrade.Plan.After.Find(new(InventoryListType.Talisman, 1))?.Uid == 101 &&
            byGrade.Plan.After.Find(new(InventoryListType.Talisman, 2))?.Uid == 103, "按品级整理把高品质护石排前");
        var byOrganization = InventorySortService.Plan(sortable.Inventory, (ItemKind)19, 2, sortCatalog, Now, InventoryListType.Talisman);
        That(byOrganization.Accepted && byOrganization.Plan!.After.Items.SequenceEqual(levelPlan.After.Items),
            "护石页按组织整理没有阵营可依，回退页面默认的等级顺序");
        var runePage = InventorySortService.Plan(sortable.Inventory, (ItemKind)18, 0, sortCatalog, Now, InventoryListType.Talisman);
        That(runePage.Accepted && runePage.Plan!.After.Find(new(InventoryListType.Talisman, 70))?.Uid == 114 &&
            runePage.Plan.After.Find(new(InventoryListType.Talisman, 71))?.Uid == 111 &&
            runePage.Plan.After.Find(new(InventoryListType.Talisman, 72))?.Uid == 112 &&
            runePage.Plan.After.Find(new(InventoryListType.Talisman, 100))?.Uid == 113 &&
            runePage.Plan.After.Find(new(InventoryListType.Talisman, 150)) is null &&
            runePage.Plan.After.Find(new(InventoryListType.Talisman, 5))?.Uid == 101 &&
            runePage.Plan.After.Find(new(InventoryListType.Talisman, 210))?.Uid == 115,
            "符文页按阵营归组并保留整理锁原位，护石页与已装符文不受影响");
        var runePagePlan = runePage.Plan!;
        var runeByOrganization = InventorySortService.Plan(sortable.Inventory, (ItemKind)18, 2, sortCatalog, Now, InventoryListType.Talisman);
        That(runeByOrganization.Accepted && runeByOrganization.Plan!.After.Items.SequenceEqual(runePagePlan.After.Items),
            "符文页按组织整理与页面默认同为阵营归组");
        var runeByGrade = InventorySortService.Plan(sortable.Inventory, (ItemKind)18, 1, sortCatalog, Now, InventoryListType.Talisman);
        That(runeByGrade.Accepted && runeByGrade.Plan!.After.Find(new(InventoryListType.Talisman, 70))?.Uid == 112 &&
            runeByGrade.Plan.After.Find(new(InventoryListType.Talisman, 71))?.Uid == 111 &&
            runeByGrade.Plan.After.Find(new(InventoryListType.Talisman, 72))?.Uid == 114 &&
            runeByGrade.Plan.After.Find(new(InventoryListType.Talisman, 100))?.Uid == 113,
            "符文页按品级整理读符文定义品级而不是模板稀有度，整理锁仍留原位");
        var legacyPage = InventorySortService.Plan(sortable.Inventory, ItemKind.Talisman, 0, sortCatalog, Now, InventoryListType.Talisman);
        That(legacyPage.Accepted && legacyPage.Plan!.After.Items.SequenceEqual(levelPlan.After.Items),
            "护石页保留物品类别11入口相同行为");
        var legacyRuneKind = InventorySortService.Plan(sortable.Inventory, ItemKind.Rune, 0, sortCatalog, Now, InventoryListType.Talisman);
        That(legacyRuneKind.Accepted && legacyRuneKind.Plan!.After.Items.SequenceEqual(runePagePlan.After.Items),
            "符文页保留物品类别14入口相同行为");
        var repeatedRune = InventorySortService.Plan(runePagePlan.After, (ItemKind)18, 0, sortCatalog, Now, InventoryListType.Talisman);
        That(repeatedRune.Accepted && repeatedRune.Plan!.After.Items.SequenceEqual(runePagePlan.After.Items),
            "重复整理符文页不改变已排好顺序");
        That(!InventorySortService.Plan(sortable.Inventory, (ItemKind)17, 0, sortCatalog, Now, InventoryListType.Talisman).Accepted &&
            !InventorySortService.Plan(sortable.Inventory, (ItemKind)18, 0, sortCatalog, Now, InventoryListType.Main).Accepted,
            "护石容器只接受护石页与符文页分类，其他页面号或容器拒绝");

        var list = InventoryPackets.SpecialFull(afterEquip.Inventory, InventoryListType.Talisman, Now);
        That(list[0] == 46 && BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(1)) == 2 && list.Length == 3 + 2 * 123,
            "list46全量直接使用123B普通物品行，并包含装载符文真实槽");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        EquipmentProtocolWriter.Runes(writer, afterEquip.Inventory);
        var vector = stream.ToArray();
        That(vector.Length == 25 && vector[0] == 6 && BinaryPrimitives.ReadInt32LittleEndian(vector.AsSpan(1)) == RuneId &&
            vector.AsSpan(5).IndexOfAnyExcept((byte)0) < 0, "本人装备尾向量固定六位置，未装孔不压缩错位");
        Console.WriteLine($"PASS {Count - start} talisman inventory checks");
    }

    static ItemCatalog Catalog()
    {
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var other = Rune with { Id = OtherRuneId };
        catalog.PrefillTemplates(new[] { Stone, Stone2, Rune, other, ForeignRune, Material }.ToDictionary(item => item.Id));
        catalog.PrefillTalismans([new(Stone, [1], 9), new(Stone2, [1], 9)]);
        catalog.PrefillRunes([new(Rune, [1], 9, RuneCamp.SecondPact, 2), new(other, [1], 10, RuneCamp.SecondPact, 2),
            new(ForeignRune, [2], 11, RuneCamp.SecondPact, 2)]);
        return catalog;
    }

    // 整理用例独立目录：三枚等级/品级各不相同的护石与四枚不同阵营、不同品级符文，避免与装载用例互相牵制。
    static ItemCatalog SortCatalog()
    {
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var stones = new[]
        {
            new ItemTemplate(100400011, ItemKind.Talisman, 34, 95, 0, 1, 0, 3, 97, "[trade]", 1, 0, "sort-stone-95"),
            new ItemTemplate(100400012, ItemKind.Talisman, 34, 75, 0, 1, 0, 4, 97, "[trade]", 1, 0, "sort-stone-75"),
            new ItemTemplate(100400013, ItemKind.Talisman, 34, 85, 0, 1, 0, 2, 97, "[trade]", 1, 0, "sort-stone-85")
        };
        var runes = new[]
        {
            new ItemTemplate(10605301, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[trade]", 1, 0, "sort-rune-terracotta-1"),
            new ItemTemplate(10605302, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 2, "[trade]", 1, 0, "sort-rune-guardians-2"),
            new ItemTemplate(10605303, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 3, "[trade]", 1, 0, "sort-rune-terracotta-3"),
            new ItemTemplate(10605304, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 3, "[trade]", 1, 0, "sort-rune-secondpact-3")
        };
        catalog.PrefillTemplates(stones.Concat(runes).ToDictionary(item => item.Id));
        catalog.PrefillTalismans([new(stones[0], [1], 9), new(stones[1], [1], 9), new(stones[2], [1], 9)]);
        catalog.PrefillRunes([new(runes[0], [1], 9, RuneCamp.Terracotta, 1), new(runes[1], [1], 9, RuneCamp.Guardians, 2),
            new(runes[2], [1], 9, RuneCamp.Terracotta, 3), new(runes[3], [1], 9, RuneCamp.SecondPact, 3)]);
        return catalog;
    }

    // 升档用例目录：普通/高级/稀有符文各一枚，稀有/神器护石各一枚，覆盖资源表里所有非零升档行。
    static ItemCatalog UpgradeCatalog()
    {
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var stones = new[]
        {
            new ItemTemplate(100400021, ItemKind.Talisman, 34, 95, 0, 1, 0, 2, 97, "[trade]", 1, 0, "upgrade-stone-rare"),
            new ItemTemplate(100400022, ItemKind.Talisman, 34, 95, 0, 1, 0, 3, 97, "[trade]", 1, 0, "upgrade-stone-artifact")
        };
        var runes = new[]
        {
            new ItemTemplate(10605311, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[trade]", 1, 0, "upgrade-rune-common"),
            new ItemTemplate(10605312, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[trade]", 1, 0, "upgrade-rune-advanced"),
            new ItemTemplate(10605313, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[trade]", 1, 0, "upgrade-rune-rare")
        };
        catalog.PrefillTemplates(stones.Concat(runes).ToDictionary(item => item.Id));
        catalog.PrefillTalismans([new(stones[0], [1], 9), new(stones[1], [1], 9)]);
        catalog.PrefillRunes([new(runes[0], [1], 9, RuneCamp.Terracotta, 0), new(runes[1], [1], 9, RuneCamp.Guardians, 1),
            new(runes[2], [1], 9, RuneCamp.SecondPact, 2)]);
        // 产物池只收录已解析过的定义，预置后必须先取一遍，否则高一档候选为空。
        foreach (var stone in stones) catalog.Talisman(stone.Id);
        foreach (var rune in runes) catalog.Rune(rune.Id);
        return catalog;
    }

    // 按调用顺序返回预定骰值；次数与业务路径不符时直接抛越界，避免静默改用默认随机。
    static Func<long, long> Rolls(params long[] values)
    {
        int index = 0;
        return _ => values[index++];
    }

    static InventoryItem StoneItem(long uid, short slot, InventoryListType list = InventoryListType.Talisman) =>
        // 夹具孔色与 SecondPact 符文同色：装载用例只验证堆叠拆分，颜色规则由专门用例覆盖。
        new(uid, new(list, slot), new TalismanSocketColors(5, 5, 3).Apply(new(StoneId, ItemKind.Talisman, 999999998)));
    static Character Character(params InventoryItem[] items) => new(1, 0, "Talisman", 0, new(1, 0, 10, 10), null)
    { Job = 0, GrowType = 1, Level = 95, Inventory = new(items.ToImmutableArray()) };
}
