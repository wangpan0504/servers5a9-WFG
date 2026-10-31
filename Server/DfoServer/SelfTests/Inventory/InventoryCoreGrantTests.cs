using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;

namespace DfoServer.Testing.Inventory;

internal static class InventoryCoreGrantTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly EpicPieceCatalog Pieces = new("[piece list]\n50000 50001\n[/piece list]");

    public static void RunCore()
    {
        int checks = Count;
        var gmCargo = new InventoryItem(710, new(InventoryListType.AccountCargo, 1), new ItemCore(490013209, ItemKind.Consumable, 10));
        var gmBefore = new InventorySnapshot([gmCargo]);
        That(!InventoryDeleteService.Plan(gmBefore, gmCargo.Position, gmCargo.Core.ItemId, 10).Accepted,
            "普通客户端丢弃仍拒绝账号金库");
        var gmDeleted = InventoryDeleteService.Plan(gmBefore, gmCargo.Position, gmCargo.Core.ItemId, 10, includeAccountCargo: true);
        That(InventoryDeleteService.CanGmDelete(gmBefore, gmCargo) && gmDeleted.Accepted &&
            gmDeleted.Plan!.After.Items.IsEmpty && gmDeleted.Plan.AccountCargoChanged && gmBefore.Items.Length == 1,
            "GM账号金库删除规划标记共享修订且保持原快照");
        var lockedCargo = gmCargo with { Core = gmCargo.Core with { EquipmentLockId = 7 } };
        That(!InventoryDeleteService.CanGmDelete(new([lockedCargo]), lockedCargo) &&
            !InventoryDeleteService.Plan(new([lockedCargo]), lockedCargo.Position, lockedCargo.Core.ItemId, 10, includeAccountCargo: true).Accepted,
            "GM账号金库同样保留锁定物品");
        var wornCargo = gmCargo with { Position = new(InventoryListType.Equipment, 12) };
        That(!InventoryDeleteService.CanGmDelete(new([wornCargo]), wornCargo), "GM不允许删除穿戴物品");
        var stackTemplate = Template(1000, ItemKind.Consumable, 100);
        var equipmentTemplate = Template(101010438, ItemKind.Equipment, 1);
        var core = FullCore(stackTemplate, 3);
        var empty = CharacterWith([]);
        string coreBefore = JsonSerializer.Serialize(core);
        var fresh = Grant(empty, core, stackTemplate);
        That(fresh.Accepted && fresh.Plan!.After.Items.Single() is { Uid: 0, Position.Index: 65 } freshItem &&
            freshItem.Core == core, "新堆完整保留全部核心字段与原期限，仅分配临时UID0和普通类别槽");
        That(JsonSerializer.Serialize(core) == coreBefore && empty.Inventory.Items.IsEmpty,
            "规划不修改输入核心或角色快照");

        var equipment = FullCore(equipmentTemplate, 0) with { EquipmentLockId = 7 };
        var originalEquipment = new InventoryItem(900, new(InventoryListType.Main, 9), equipment);
        var equipmentCharacter = CharacterWith([originalEquipment]);
        var equipmentGrant = Grant(equipmentCharacter, equipment, equipmentTemplate);
        var addedEquipment = equipmentGrant.Plan!.After.Find(new(InventoryListType.Main, 10))!;
        That(equipmentGrant.Accepted && addedEquipment.Uid == 0 && addedEquipment.Core == equipment &&
            addedEquipment.Core.Value == 0 && addedEquipment.Core.EquipmentLockId == 7 &&
            equipmentGrant.Plan.After.Find(originalEquipment.Position) == originalEquipment,
            "零品级装备不变空堆；正锁与完整属性不清零，不复制存量UID");

        var target = new InventoryItem(10, new(InventoryListType.Main, 3), core with { Value = 5, SortLockFlag = 1 });
        var later = new InventoryItem(11, new(InventoryListType.Main, 65), core with { Value = 6 });
        var beforeMerge = CharacterWith([later, target]) with
        {
            Inventory = new([later, target], Gold: 123)
            {
                ReviveCoins = 2, WinPoints = 3,
                AccountWallet = AccountWallet.Empty.Set(CurrencyKind.WhiteCube, 4)
            }
        };
        string mergeBefore = JsonSerializer.Serialize(beforeMerge);
        var merged = Grant(beforeMerge, core, stackTemplate);
        That(merged.Accepted && merged.Plan!.After.Find(target.Position) is { Uid: 10 } mergedItem &&
            mergedItem.Core == (target.Core with { Value = 8 }) && merged.Plan.After.Find(later.Position) == later,
            "按槽顺序合入首个完整兼容快捷栏堆，保留目标UID及排序锁");
        That(merged.Plan!.After.Gold == 123 && merged.Plan.After.ReviveCoins == 2 && merged.Plan.After.WinPoints == 3 &&
            merged.Plan.After.AccountWallet.Same(beforeMerge.Inventory.AccountWallet) &&
            JsonSerializer.Serialize(beforeMerge) == mergeBefore, "实体规划保留数量资产且不修改原堆");

        foreach (var different in new[]
        {
            target with { Core = target.Core with { ExpireTime = core.ExpireTime + 1 } },
            target with { Core = target.Core with { EnchantCardId = core.EnchantCardId + 1 } }
        })
        {
            var separated = Grant(CharacterWith([different]), core, stackTemplate);
            That(separated.Accepted && separated.Plan!.After.Find(different.Position) == different &&
                separated.Plan.After.Find(new(InventoryListType.Main, 65)) is { Uid: 0 } added && added.Core == core,
                "不同期限或附魔属性不得合并，完整核心进入新槽");
        }

        var lockedTarget = target with { Core = target.Core with { EquipmentLockId = 1 } };
        var unlockedAfterLock = Grant(CharacterWith([lockedTarget, later]), core, stackTemplate);
        That(unlockedAfterLock.Accepted && unlockedAfterLock.Plan!.After.Find(lockedTarget.Position) == lockedTarget &&
            unlockedAfterLock.Plan.After.Find(later.Position)!.Core == (later.Core with { Value = 9 }),
            "安全锁目标不参与合并，继续查下一个兼容堆");
        var lockedSource = core with { EquipmentLockId = 2 };
        var keptSourceLock = Grant(CharacterWith([target]), lockedSource, stackTemplate);
        That(keptSourceLock.Accepted && keptSourceLock.Plan!.After.Find(target.Position) == target &&
            keptSourceLock.Plan.After.Find(new(InventoryListType.Main, 65))!.Core == lockedSource,
            "合法锁定来源核心不能通过清锁并入其他堆");

        var almostFull = target with { Core = target.Core with { Value = 98 } };
        Reject(CharacterWith([later, almostFull]), core, stackTemplate, 17,
            "首兼容堆容不下整条时拒绝，不溢出到后堆或新槽");
        Reject(empty, core with { Value = 101 }, stackTemplate, 17, "单条数量超过真实堆叠上限拒绝");
        Reject(empty, core with { ExpireTime = checked((int)Now.ToUnixTimeSeconds()) }, stackTemplate, 19,
            "恰好到期的完整核心拒绝，不重算新期限");
        Reject(empty, core with { Value = 0 }, stackTemplate, 1, "零堆叠量拒绝");
        Reject(empty, core with { SortLockFlag = 2 }, stackTemplate, 1, "沿用完整核心的排序锁合法域");
        Reject(empty, core with { ItemId = 1004 }, stackTemplate, 1, "模板编号不匹配拒绝");
        Reject(empty, core with { Kind = ItemKind.Material }, stackTemplate, 1, "模板类别不匹配拒绝");

        var fullStacks = Enumerable.Range(65, 56).Select(slot => new InventoryItem(slot,
            new(InventoryListType.Main, (short)slot), core with { Value = 1, EnchantCardId = slot + 1000 })).ToImmutableArray();
        Reject(CharacterWith(fullStacks), core, stackTemplate, 17, "无兼容堆且消耗品页满时拒绝");
        var fullEquipment = Enumerable.Range(9, 56).Select(slot => new InventoryItem(slot,
            new(InventoryListType.Main, (short)slot), equipment)).ToImmutableArray();
        Reject(CharacterWith(fullEquipment), equipment, equipmentTemplate, 17, "装备不堆叠，满装备页拒绝");

        var currencyTemplate = Template(3034, ItemKind.Material, int.MaxValue);
        Reject(empty, new(3034, ItemKind.Material, 1), currencyTemplate, 1, "数值钱包不能伪装物理核心");
        var pieceTemplate = Template(50001, ItemKind.Material, int.MaxValue);
        Reject(empty, new(50001, ItemKind.Material, 1), pieceTemplate, 1, "账号史诗碎片不能伪装物理核心");
        foreach (var kind in new[] { ItemKind.Avatar, ItemKind.Creature, ItemKind.CreatureEquipment })
        {
            var unsupported = Template(70000 + (int)kind, kind, 1);
            Reject(empty, new(unsupported.Id, kind, 1), unsupported, 1, "非普通装备/堆叠核心不借用此入口: " + kind);
        }
        // 拾取策略独立于普通授予，仍先合堆且不得绕过整条数量上限。
        foreach (int occupied in Enumerable.Range(0, 7))
        {
            var quickItems = Enumerable.Range(3, occupied).Select(slot => new InventoryItem(slot,
                new(InventoryListType.Main, (short)slot), equipment)).ToImmutableArray();
            var pickup = Pickup(CharacterWith(quickItems), core, stackTemplate);
            short expectedSlot = 65;
            That(pickup.Accepted && pickup.Plan!.ChangedSlots.SequenceEqual(new[] { new InventorySlot(InventoryListType.Main, expectedSlot) }) &&
                pickup.Plan.After.Find(new(InventoryListType.Main, expectedSlot)) is { Uid: 0 } added && added.Core == core &&
                quickItems.All(item => pickup.Plan.After.Find(item.Position) == item),
                "快捷栏空闲数不影响消耗品新堆落背包，保留已有物品: " + occupied);
        }
        foreach (ushort expansion in new ushort[] { 0, 8, 16, 24 })
        {
            var range = ItemSlotBoundService.Range(ItemKind.Consumable, expansion);
            var occupiedPage = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(slot,
                new(InventoryListType.Main, (short)slot), core with { ItemId = 10000 + slot })).ToImmutableArray();
            var fullPage = CharacterWith(occupiedPage);
            fullPage = fullPage with { Inventory = fullPage.Inventory with { Expansion = expansion } };
            string before = JsonSerializer.Serialize(fullPage);
            var denied = Pickup(fullPage, core, stackTemplate);
            That(!denied.Accepted && denied.Error == 17 && denied.Plan is null && JsonSerializer.Serialize(fullPage) == before,
                "各扩展档消耗品满页拒绝新堆，空快捷槽和未开放分页不能充当容量: " + expansion);
            var oneHole = fullPage with { Inventory = fullPage.Inventory with
                { Items = occupiedPage.Where(item => item.Position.Index != range.End).ToImmutableArray() } };
            var filled = Pickup(oneHole, core, stackTemplate);
            That(filled.Accepted && filled.Plan!.ChangedSlots.SequenceEqual(new[] { new InventorySlot(InventoryListType.Main, range.End) }) &&
                filled.Plan.After.Find(new(InventoryListType.Main, range.End)) is { Uid: 0 } added && added.Core == core,
                "各扩展档只使用已开放分页的最后空槽，保留完整冻结核心: " + expansion);
            var replenished = Pickup(fullPage with { Inventory = fullPage.Inventory with { Items = occupiedPage.Add(target) } }, core, stackTemplate);
            That(replenished.Accepted && replenished.Plan!.ChangedSlots.SequenceEqual(new[] { target.Position }) &&
                replenished.Plan.After.Find(target.Position) is { Uid: 10 } kept && kept.Core == (target.Core with { Value = 8 }),
                "满消耗品页仍可补已有快捷兼容堆，不创建新快捷实例: " + expansion);
        }
        var pageMerge = Pickup(CharacterWith([later]), core, stackTemplate);
        That(pageMerge.Accepted && pageMerge.Plan!.ChangedSlots.SequenceEqual(new[] { later.Position }) &&
            pageMerge.Plan.After.Find(later.Position) is { Uid: 11 } pageTarget && pageTarget.Core == (later.Core with { Value = 9 }),
            "普通页已有兼容堆优先于空快捷栏，保留原UID");
        var quickMerge = Pickup(beforeMerge, core, stackTemplate);
        That(quickMerge.Accepted && quickMerge.Plan!.After.Find(target.Position)!.Core == (target.Core with { Value = 8 }) &&
            quickMerge.Plan.After.Find(later.Position) == later, "地面策略仍按槽号合入首兼容堆并保留排序锁");
        foreach (int count in new[] { 98, 100 })
        {
            var blocked = CharacterWith([later with { Core = later.Core with { Value = count } }]);
            var refused = Pickup(blocked, core, stackTemplate);
            That(!refused.Accepted && refused.Error == 17 && refused.Plan is null && blocked.Inventory.Items.Single().Core.Count == count,
                "已有堆不足整条或满栈时不借空快捷栏溢出、拆堆: " + count);
        }
        var fullPagePickup = Pickup(CharacterWith(fullStacks), core, stackTemplate);
        That(!fullPagePickup.Accepted && fullPagePickup.Error == 17 && fullPagePickup.Plan is null,
            "普通消耗品页已满不能借空快捷栏绕过容量");
        var allFull = fullStacks.AddRange(Enumerable.Range(3, 6).Select(slot => new InventoryItem(slot,
            new(InventoryListType.Main, (short)slot), equipment)));
        That(!Pickup(CharacterWith(allFull), core, stackTemplate).Accepted, "快捷栏及普通页均满时整笔拒绝");
        var incompatible = Pickup(CharacterWith([target with { Core = target.Core with { ExpireTime = core.ExpireTime + 1 } }]),
            core, stackTemplate);
        That(incompatible.Accepted && incompatible.Plan!.After.Find(new(InventoryListType.Main, 65))!.Core == core,
            "不兼容期限不合堆，另选背包空槽并保留完整核心");
        foreach (var template in new[] { equipmentTemplate, Template(2000, ItemKind.Material, 100),
            Template(2001, ItemKind.Quest, 100), Template(2002, ItemKind.CreatureConsumable, 100),
            Template(2003, ItemKind.Profession, 100), Template(2004, ItemKind.GuardianGem, 100) })
        {
            var input = FullCore(template, template.Kind == ItemKind.Equipment ? 0 : 3);
            var pickup = Pickup(empty, input, template);
            var ordinary = Grant(empty, input, template);
            That(pickup.Accepted && ordinary.Accepted && pickup.Plan!.After.Items.SequenceEqual(ordinary.Plan!.After.Items),
                "非消耗品所属分页有空位时仍先使用原容器: " + template.Kind);
        }

        foreach (var template in new[] { equipmentTemplate, Template(2000, ItemKind.Material, 100),
            Template(2001, ItemKind.Quest, 100), Template(2003, ItemKind.Profession, 100),
            Template(2004, ItemKind.GuardianGem, 100) })
        foreach (ushort expansion in new ushort[] { 0, 24 })
        {
            var input = FullCore(template, template.Kind == ItemKind.Equipment ? 0 : 3);
            var range = ItemSlotBoundService.Range(template.Kind, expansion);
            var list = ItemSlotBoundService.List(template.Kind);
            var page = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(10000 + slot,
                new(list, (short)slot), input with { ItemId = template.Id + 1000 + slot })).ToImmutableArray();
            var fullPage = CharacterWith(page);
            fullPage = fullPage with { Inventory = fullPage.Inventory with { Expansion = expansion } };
            That(!Grant(fullPage, input, template).Accepted, "普通发放不借拾取策略占用空快捷栏: " + template.Kind);
            var quick = Enumerable.Range(3, 6).Select(slot => new InventoryItem(20000 + slot,
                new(InventoryListType.Main, (short)slot), equipment)).ToImmutableArray();
            foreach (short hole in Enumerable.Range(3, 6).Select(slot => (short)slot))
            {
                var before = fullPage with { Inventory = fullPage.Inventory with { Items = page.AddRange(quick.Where(item => item.Position.Index != hole)) } };
                string old = JsonSerializer.Serialize(before);
                var result = Pickup(before, input, template);
                var destination = new InventorySlot(InventoryListType.Main, hole);
                That(result.Accepted && result.Plan!.ChangedSlots.SequenceEqual(new[] { destination }) &&
                    result.Plan.After.Find(destination) is { Uid: 0 } added && added.Core == input &&
                    before.Inventory.Items.All(item => result.Plan.After.Find(item.Position) == item) && JsonSerializer.Serialize(before) == old,
                    $"分页满后使用实际快捷空槽，保留完整Core、原资产和开放容量: {template.Kind}/{expansion}/{hole}");
            }
            var allOccupied = fullPage with { Inventory = fullPage.Inventory with { Items = page.AddRange(quick) } };
            var rejected = Pickup(allOccupied, input, template);
            That(!rejected.Accepted && rejected.Error == 17 && rejected.Plan is null,
                "所属页及快捷栏全满时拒绝，不使用其他分类空页: " + template.Kind);
        }

        foreach (var kind in new[] { ItemKind.Emblem, ItemKind.CreatureConsumable })
        {
            var template = Template(3000 + (int)kind, kind, 100);
            var input = FullCore(template, 3);
            var range = ItemSlotBoundService.Range(kind, 24);
            var page = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(30000 + slot,
                new(ItemSlotBoundService.List(kind), (short)slot), input with { ItemId = template.Id + 1000 + slot })).ToImmutableArray();
            var rejected = Pickup(CharacterWith(page), input, template);
            That(!rejected.Accepted && rejected.Error == 17 && rejected.Plan is null &&
                !ItemSlotBoundService.Allows(24, new(InventoryListType.Main, 3), kind),
                "禁止进入快捷栏的STK满专用页时仍拒绝: " + kind);
        }

        var gemTemplate = Template(2004, ItemKind.GuardianGem, 100);
        var gemCore = FullCore(gemTemplate, 3);
        var quickGem = new InventoryItem(31, new(InventoryListType.Main, 3), gemCore with { Value = 5 });
        var pageGem = new InventoryItem(32, new(InventoryListType.GuildMedal, 49), gemCore with { Value = 6 });
        var gemMerge = Pickup(CharacterWith([pageGem, quickGem]), gemCore, gemTemplate);
        That(gemMerge.Accepted && gemMerge.Plan!.After.Find(quickGem.Position) is { Uid: 31, Core.Value: 8 } &&
            gemMerge.Plan.After.Find(pageGem.Position) == pageGem, "允许跨容器的STK仍先合快捷栏已有堆，不按专用容器数值顺序选堆");
        var gemPageMerge = Pickup(CharacterWith([pageGem]), gemCore, gemTemplate);
        That(gemPageMerge.Accepted && gemPageMerge.Plan!.ChangedSlots.SequenceEqual(new[] { pageGem.Position }) &&
            gemPageMerge.Plan.After.Find(pageGem.Position) is { Uid: 32, Core.Value: 9 }, "专用页已有兼容堆仍优先于空快捷栏");
        var blockedGem = Pickup(CharacterWith([quickGem with { Core = gemCore with { Value = 100 } }, pageGem]), gemCore, gemTemplate);
        That(!blockedGem.Accepted && blockedGem.Error == 17 && blockedGem.Plan is null,
            "快捷栏首兼容堆已满时不绕到专用页或新格突破原堆叠合同");

        InventoryResult Pickup(Character character, ItemCore input, ItemTemplate template) =>
            InventoryRewardGrantService.TryGrantCore(character, input, template, Pieces, Now, usePickupSlotPolicy: true);
        Console.WriteLine($"PASS {Count - checks} 完整核心发放纯规则断言");

        InventoryResult Grant(Character character, ItemCore input, ItemTemplate template) =>
            InventoryRewardGrantService.TryGrantCore(character, input, template, Pieces, Now);
        void Reject(Character character, ItemCore input, ItemTemplate template, ushort error, string name)
        {
            string snapshot = JsonSerializer.Serialize(character);
            var result = Grant(character, input, template);
            That(!result.Accepted && result.Error == error && result.Plan is null &&
                JsonSerializer.Serialize(character) == snapshot, name + "，无计划和输入修改");
        }
    }

    public static void Run(ItemResources resources)
    {
        int checks = Count;
        var empty = CharacterWith([]);
        foreach (int itemId in new[] { 1000, 1013, 1047, 1004, 101010438 })
        {
            var template = resources.Items.Get(itemId);
            var core = FullCore(template, template.Kind == ItemKind.Equipment ? 0 : 3);
            var result = Grant(empty, core, template);
            That(result.Accepted && result.Plan!.After.Items.Single() is { Uid: 0 } item && item.Core == core,
                "匹配PVF物品完整核心入包: " + itemId);
            if (template.Kind == ItemKind.Consumable)
            {
                var pickup = InventoryRewardGrantService.TryGrantCore(empty, core, template, resources.EpicPieces, Now, usePickupSlotPolicy: true);
                var ordinaryBatch = InventoryRewardGrantService.TryGrantBatch(empty, [new(itemId, 3)],
                    resources.Items, resources.GoldLimits, resources.EpicPieces, Now);
                That(pickup.Accepted && pickup.Plan!.After.Items.Single() is { Position.Index: 65 } picked && picked.Core == core &&
                    ordinaryBatch.Accepted && ordinaryBatch.Plan!.After.Items.Single() is { Position.Index: 65 },
                    "肉干及其他真实消耗品的新堆均进背包，默认批量发放不改变: " + itemId);
            }
        }
        var stack = resources.Items.Get(1000);
        That(stack.Kind == ItemKind.Consumable && stack.StackLimit == 1000,
            "目标普通掉落野草莓真实模板类别与堆叠上限");
        var source = FullCore(stack, 2);
        var existing = new InventoryItem(88, new(InventoryListType.Main, 3), source with { Value = 9, SortLockFlag = 1 });
        var merged = Grant(CharacterWith([existing]), source, stack);
        That(merged.Accepted && merged.Plan!.After.Find(existing.Position) is { Uid: 88 } itemAfter &&
            itemAfter.Core == (existing.Core with { Value = 11 }), "真实野草莓合并保留期限、完整属性和目标排序锁");
        var nearLimit = existing with { Core = existing.Core with { Value = stack.StackLimit - 1 } };
        var refused = Grant(CharacterWith([nearLimit]), source, stack);
        That(!refused.Accepted && refused.Error == 17 && refused.Plan is null, "真实上限不足整条时不创建第二堆");
        var oversized = Grant(empty, source with { Value = stack.StackLimit + 1 }, stack);
        That(!oversized.Accepted && oversized.Error == 17, "真实模板超上限拒绝");
        var expired = Grant(empty, source with { ExpireTime = checked((int)Now.ToUnixTimeSeconds()) }, stack);
        That(!expired.Accepted && expired.Error == 19, "真实模板的已到期实例不重新创建");

        // 共用放置实现仍须保留原批量入口的整批失败；最后装备超页不能留下先前的物品。
        string snapshot = JsonSerializer.Serialize(empty);
        var batch = InventoryRewardGrantService.TryGrantBatch(empty, [new(1000, 1), new(101010438, 57)],
            resources.Items, resources.GoldLimits, resources.EpicPieces, Now);
        That(!batch.Accepted && batch.Error == 17 && batch.Plan is null && JsonSerializer.Serialize(empty) == snapshot,
            "普通批量后续容量失败不返回部分奖励");
        Console.WriteLine($"PASS {Count - checks} 完整核心发放匹配资源断言");

        InventoryResult Grant(Character character, ItemCore core, ItemTemplate template) =>
            InventoryRewardGrantService.TryGrantCore(character, core, template, resources.EpicPieces, Now);
    }

    static Character CharacterWith(ImmutableArray<InventoryItem> items) =>
        new(1, 0, "CoreGrant", 0, new(1, 0, 10, 10), null) { Inventory = new(items) };

    static ItemTemplate Template(int id, ItemKind kind, int stackLimit) =>
        new(id, kind, kind == ItemKind.Equipment ? (byte)12 : (byte)0, 1, 30, stackLimit, 1, 0, 1,
            "[free]", ushort.MaxValue, 0, "合成核心模板");

    // 合成的各字段取非默认值，用完整record相等检查捕获重建核心或漏拷贝。
    static ItemCore FullCore(ItemTemplate template, int value) => new(template.Id, template.Kind, value)
    {
        Attr = 7, Durability = 9, SealFlag = 1, EnchantCardId = 11, EnchantUpgrade = 2,
        AmplifyType = 3, AmplifyValue = 4, Marker16 = 5, Extension26 = 6,
        Extension27 = 7, Extension31 = 8, Extension35 = 9,
        Chronicle0 = new(10, 1, 2, 3, 4), Chronicle1 = new(11, 2, 3, 4, 5),
        ExpireTime = checked((int)Now.AddHours(2).ToUnixTimeSeconds()),
        EmblemSocketCount = 2, Emblem1 = 12, Emblem2 = 13, Rune = 14,
        Random0 = new(1, 15, 16), Random1 = new(2, 17, 18), Random2 = new(3, 19, 20),
        RandomState = 21, RandomChangedIndex = 22, RandomChangeState = 23, RandomChange = new(4, 24, 25),
        GenuineUpgrade = 26, EmancipateLevel = 27, TradeRestriction = 28, Extension90 = 29,
        Extension92 = 30, Extension93 = 31, RemainUseCount = 32, SortLockFlag = 0,
        Extension97 = 33, GuardianGemKeys = 0x1122334455667788, Extension109 = 34, Extension110 = 35,
        Extension114 = new(36, 37), Extension119 = 38, Extension120 = 39, Extension121 = 40
    };
}
