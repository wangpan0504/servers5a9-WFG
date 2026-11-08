using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuickSlotEquipmentTests
{
    public static void RunCore()
    {
        int before = Count;
        That(ItemCatalog.Slot("charm") == 31 && ItemCatalog.Slot("talisman") == 34,
            "快捷栏类型31不再冒充护石35，护石模板类型34保持");
        var old = QuickSlotEquipmentDefinition.Parse(new PvfDocument("[inventory limit]\n5000\n"));
        var modern = QuickSlotEquipmentDefinition.Parse(new PvfDocument("[heraldry item]\n[inventory limit]\n10000\n[inventory limit]\n10000\n"));
        That(old == new QuickSlotEquipmentDefinition(false) && modern == new QuickSlotEquipmentDefinition(true) &&
            QuickSlotEquipmentDefinition.Parse(new PvfDocument("")) == old,
            "快捷栏标记不把负重字段当能量，也不要求永久型装备声明有限上限");
        ItemTemplate Template(int id, byte type, QuickSlotEquipmentDefinition? definition = null, ushort durability = 0) =>
            new(id, ItemKind.Equipment, type, 1, durability,
                1, 1, 2, 20, "[trade]", ushort.MaxValue, 0, "fixture")
            { QuickSlotEquipment = definition, ExperiencePercent = definition is null ? 10 : 200 };
        var charm = Template(9001, 31, old);
        var crest = Template(9002, 31, modern, 100);
        var weapon = Template(9003, 12, durability: 40);
        var finite = Template(9005, 31, old, 100);
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [9001] = charm, [9002] = crest, [9003] = weapon, [9005] = finite });
        var first = new InventoryItem(101, Slot(9), InventoryRewardGrantService.Create(charm) with
            { Attr = 3, EnchantCardId = 1234, SortLockFlag = 1 });
        var second = new InventoryItem(102, Slot(10), InventoryRewardGrantService.Create(crest));
        var ordinary = new InventoryItem(103, Slot(11), InventoryRewardGrantService.Create(weapon));
        var limited = new InventoryItem(106, Slot(12), InventoryRewardGrantService.Create(finite));
        That(first.Core.Value == InventoryRewardGrantService.InitialQuality && first.Core.Durability == 0 && first.Core.Count == 1 &&
            second.Core.Durability == 100 && second.Core.Value == first.Core.Value && limited.Core.Durability == 100 &&
            limited.Core.Value == first.Core.Value && InventoryRewardGrantService.Create(charm, quality: 999999950).Value == 999999950,
            "所有快捷栏装备沿普通核心保留品级，永久型耐久0，有限旧符咒与新纹章按声明初始化耐久");
        var character = new Character(1, 0, "QuickSlot", 0, new(1, 0, 1, 1), null)
        { Level = 20, Inventory = new([first, second, ordinary]) };
        string original = JsonSerializer.Serialize(character.Inventory);
        var inserted = Move(character, 9, 9001, 3, 0);
        That(inserted.Accepted && inserted.Plan!.After.Find(Slot(3)) == first with
            { Position = Slot(3), Core = first.Core with { SortLockFlag = 0 } } && inserted.Plan.After.Find(Slot(9)) is null &&
            JsonSerializer.Serialize(character.Inventory) == original,
            "真实整件移动保留UID、能量和打造，只沿既有规则清排序锁，规划不修改源快照");
        var active = character with { Inventory = inserted.Plan!.After };
        var secondEquipped = Move(active, 8, 0, 10, 9002);
        That(secondEquipped.Accepted && secondEquipped.Plan!.After.Find(Slot(3))?.Uid == 101 &&
            secondEquipped.Plan.After.Find(Slot(8))?.Uid == 102,
            "快捷栏已有纹章时允许继续拖入第二件快捷栏装备，与客户端025D96E5/02864073一致");
        var multiRole = active with { Inventory = secondEquipped.Plan!.After with { Items = secondEquipped.Plan.After.Items.Add(limited) } };
        var clientRightClickSwap = Move(multiRole, 3, 9001, 12, 9005);
        var quickSlotSwap = Move(multiRole, 8, 9002, 3, 9001);
        That(clientRightClickSwap.Accepted && clientRightClickSwap.Plan!.After.Find(Slot(3))?.Uid == 106 &&
            clientRightClickSwap.Plan.After.Find(Slot(12))?.Uid == 101 &&
            quickSlotSwap.Accepted && quickSlotSwap.Plan!.After.Find(Slot(3))?.Uid == 102 &&
            quickSlotSwap.Plan.After.Find(Slot(8))?.Uid == 101,
            "多件并存时按客户端029E6804真实顺序右键替换及快捷栏内互换均合法，AllowsAt正确排除双方原槽位");
        var exchanged = Move(active, 10, 9002, 3, 9001);
        That(exchanged.Accepted && exchanged.Plan!.After.Find(Slot(3))?.Uid == 102 &&
            exchanged.Plan.After.Find(Slot(10))?.Uid == 101, "直接替换已装备纹章是合法原子互换，不丢旧实例");
        var expiredInQuickSlot = active with
        {
            Inventory = active.Inventory with
            {
                Items = active.Inventory.Items.Replace(active.Inventory.Find(Slot(3))!,
                    active.Inventory.Find(Slot(3))! with { Core = first.Core with { ExpireTime = 999 } })
            }
        };
        var expiredReplaced = Move(expiredInQuickSlot, 3, 9001, 10, 9002);
        var expiredUnequipped = Move(expiredInQuickSlot, 9, 0, 3, 9001);
        That(expiredReplaced.Accepted && expiredReplaced.Plan!.After.Find(Slot(3))?.Uid == 102 &&
            expiredReplaced.Plan.After.Find(Slot(10))?.Uid == 101 &&
            expiredUnequipped.Accepted && expiredUnequipped.Plan!.After.Find(Slot(9))?.Uid == 101 &&
            !Move(expiredInQuickSlot, 8, 0, 3, 9001).Accepted &&
            !Move(character with
            {
                Inventory = new([first with { Core = first.Core with { ExpireTime = 999 } }, second, ordinary])
            }, 3, 0, 9, 9001).Accepted,
            "快捷栏内已过期符咒可卸回背包或被新纹章右键替换，但不可在快捷栏内移位或从背包重新装入");
        That(!Move(character with { Level = 4 }, 9, 9001, 3, 0).Accepted &&
            Move(character with { Level = 4 }, 11, 9003, 3, 0).Accepted,
            "5级门槛只限制快捷栏装备，不收窄普通装备快捷栏移动");
        That(!Move(active, 3, 9001, 12, 0, town: false).Accepted &&
            Move(active, 3, 9001, 8, 0, town: false).Accepted && Move(active, 3, 9001, 12, 0).Accepted,
            "副本不装卸纹章，同快捷栏移位及回城卸下仍合法");
        That(!InventoryMoveService.Allows(character, new(InventoryListType.Equipment, 31), first.Core, items),
            "类型31不是可穿戴的Equipment31槽");
        That(QuickSlotEquipmentRules.IsActive(first with { Position = Slot(6) }, charm, 1000) &&
            !QuickSlotEquipmentRules.IsActive(first, charm, 1000) &&
            QuickSlotEquipmentRules.IsActive(first with { Position = Slot(6), Core = first.Core with { Value = 0 } }, charm, 1000) &&
            QuickSlotEquipmentRules.IsActive(limited with { Position = Slot(6) }, finite, 1000) &&
            !QuickSlotEquipmentRules.IsActive(limited with { Position = Slot(6), Core = limited.Core with { Durability = 0 } }, finite, 1000) &&
            !QuickSlotEquipmentRules.IsActive(second with { Position = Slot(6), Core = second.Core with { Durability = 0 } }, crest, 1000),
            "永久型不受零耐久或品级Value门禁影响；有限旧符咒与新纹章均检查当前耐久");
        var bonus = new ExperienceBonusService(items, new RecoveryConsumableCatalog([]), new ExperienceBonusCatalog(0, 0));
        uint Extra(Character role, ExperienceEvent kind = ExperienceEvent.Kill) =>
            bonus.Plan(new(role, kind, 1, 1000, false), 100, 0, null).Amount(ExperienceBonusSource.Equipment);
        That(Extra(character) == 0 && Extra(active) == 200 && Extra(active, ExperienceEvent.Clear) == 0 &&
            Extra(active, ExperienceEvent.Quest) == 0, "快捷栏真实符咒只加入击杀经验，未装备、任务和通关不冒领该来源");
        var zeroValue = active with { Inventory = active.Inventory with { Items = active.Inventory.Items.Replace(
            active.Inventory.Find(Slot(3))!, active.Inventory.Find(Slot(3))! with { Core = first.Core with { Value = 0 } }) } };
        var empty = active with { Inventory = new([limited with { Position = Slot(3), Core = limited.Core with { Durability = 0 } }]) };
        That(Extra(zeroValue) == 200 && Extra(empty) == 0 && Extra(active with { Inventory = active.Inventory with
            { Items = active.Inventory.Items.Add(new(104, Slot(8), ordinary.Core)) } }) == 200,
            "永久型品级Value为0仍有装备经验，有限旧符咒零耐久不奖励，普通装备不冒充快捷栏专用品");
        That(Extra(active with { Inventory = active.Inventory with { Items = active.Inventory.Items.Add(second with
                { Uid = 104, Position = Slot(8) }) } }) == 200 &&
            Extra(empty with { Inventory = empty.Inventory with { Items = empty.Inventory.Items.Add(second with
                { Uid = 104, Position = Slot(8) }) } }) == 0,
            "重复旧记录逐槽只认首件，既不叠加也不借第二件绕过首件空能量");
        var equipmentRange = ItemSlotBoundService.Range(ItemKind.Equipment, character.Inventory);
        var full = character with { Inventory = character.Inventory with { Items = Enumerable.Range(equipmentRange.Start,
            equipmentRange.End - equipmentRange.Start + 1)
            .Select(slot => new InventoryItem(slot, Slot((short)slot), ordinary.Core)).ToImmutableArray() } };
        var pieces = new EpicPieceCatalog("[piece list]\n9100 9101\n[/piece list]");
        That(!InventoryRewardGrantService.TryGrantCore(full, first.Core, charm, pieces, usePickupSlotPolicy: true).Accepted &&
            InventoryRewardGrantService.TryGrantCore(full, ordinary.Core, weapon, pieces, usePickupSlotPolicy: true).Accepted,
            "满装备页的纹章留在地面，不自动装备；普通装备快捷栏回退不变");
        VerifyCharging(items, limited, first, second, character);
        VerifyAutomaticRepair(items, limited, second, ordinary, character);
        VerifyFatigueEnergy(items, limited, first, second, character);
        Console.WriteLine($"PASS {Count - before} 快捷栏装备分类、能量初始化、装卸及经验来源断言");

        InventoryResult Move(Character role, short from, int id, short to, int target, bool town = true) =>
            InventoryMoveService.Plan(role, new(Slot(from), id, 0, Slot(to), target, 0, -1, 0, 0), items, 1000,
                allowQuickSlotEquipmentChange: town);
    }

    static void VerifyCharging(ItemCatalog items, InventoryItem charm, InventoryItem permanent, InventoryItem heraldry, Character character)
    {
        var tool = new ItemTemplate(9004, ItemKind.Consumable, 0, 1, 0, 10, 0, 0, 1, "[trade]", ushort.MaxValue, 0, "charge");
        RecoveryConsumableFile File(params int[] arguments) => new(tool.Id, "charge.stk",
            new(tool.Id, "charge.stk", new("[waste]", [0]), 10, 0, 0, 1, null, [])
            { ActionDeclaration = new("[charm charging]", arguments), HasActionType = true,
                DeclaredNeededMaterials = [10004008, 30] }, 0, null, null, null, [], []);
        var definition = ActionConsumableDefinition.Parse(File(100, 20, 123), tool)!;
        That(definition is { Parameters: [100, 20, 123] } &&
            ActionConsumableDefinition.Parse(File(100), tool) is null && ActionConsumableDefinition.Parse(File(0, 20), tool) is null,
            "充能读取前两项上限及恢复量，额外数值不误拒绝，缺少有效业务参数不冒充合法定义");
        var clock = new Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.FromUnixTimeSeconds(1000));
        var service = new InventoryCharmChargingService(items.Get, _ => definition, clock);
        var target = charm with { Position = Slot(3), Core = charm.Core with { Durability = 75 } };
        var source = new InventoryItem(105, Slot(65), new(tool.Id, ItemKind.Consumable, 3));
        var role = character with { Inventory = new([target, source], Gold: 1234) };
        var result = service.Plan(role, target.Position, source.Position);
        var after = result.Plan!.After;
        That(result.Accepted && after.Find(target.Position) == target with { Core = target.Core with { Durability = 95 } } &&
            after.Find(source.Position)!.Core.Count == 2 && after.Gold == 1234 && result.Plan.UsedItems.SequenceEqual([new ItemUseIntent(tool.Id, 1)]) &&
            role.Inventory.Find(target.Position)!.Core.Durability == 75 && role.Inventory.Find(source.Position)!.Core.Count == 3,
            "充能只恢复真实耐久并消费一张，完整保留品级Value、UID、打造和原快照，不扣商店need material");
        var capped = service.Plan(role with { Inventory = after }, target.Position, source.Position);
        That(capped.Accepted && capped.Plan!.After.Find(target.Position)!.Core.Durability == 100 &&
            capped.Plan.After.Find(source.Position)!.Core.Count == 1 &&
            service.Plan(role with { Inventory = capped.Plan.After }, target.Position, source.Position).Error == 4,
            "超出道具恢复上限只补足5，已达上限拒绝而不消耗下一张");
        var large = new InventoryCharmChargingService(items.Get, _ => definition with { Parameters = [500, 100] }, clock);
        That(large.Plan(role with { Inventory = capped.Plan!.After }, target.Position, source.Position)
            .Plan!.After.Find(target.Position)!.Core.Durability == 200,
            "高阶充能可越过模板初始耐久100，不把所有符咒固定在100");
        var overflow = new InventoryCharmChargingService(items.Get,
            _ => definition with { Parameters = [int.MaxValue, int.MaxValue] }, clock);
        That(overflow.Plan(role, target.Position, source.Position).Plan!.After.Find(target.Position)!.Core.Durability == ushort.MaxValue,
            "恢复量用宽整数计算并受真实u16耐久字段边界约束，不从负重推导上限");
        var modern = role with { Inventory = new([heraldry with { Position = target.Position, Core = heraldry.Core with { Durability = 0 } }, source]) };
        var unlimited = role with { Inventory = new([permanent with { Position = target.Position }, source]) };
        That(!service.Plan(modern, target.Position, source.Position).Accepted &&
            service.Plan(unlimited, target.Position, source.Position).RejectReason == "permanent_quick_slot_equipment" &&
            !service.Plan(role, source.Position, target.Position).Accepted &&
            !service.Plan(role with { Inventory = new([target, source with { Core = source.Core with { Value = 0 } }]) },
                target.Position, source.Position).Accepted,
            "永久型、新纹章、错误目标与不足的源券不扣券，不给永久纹章人工制造耐久");
        var display = new ExperienceBonusService(items, new RecoveryConsumableCatalog([]), new ExperienceBonusCatalog(0, 0))
            .Plan(new(role with { Inventory = after }, ExperienceEvent.Kill, 1, 1000, false), 100, 0, null).Display(300);
        That(display.BuffExperience == 200 && display.DeductedExperienceComponent == 200,
            "恢复后真实快捷栏装备加成与普通装备统一进入Buff明细，显示扣除不改奖励来源");
    }

    static void VerifyAutomaticRepair(ItemCatalog items, InventoryItem charm, InventoryItem heraldry,
        InventoryItem weapon, Character character)
    {
        var clock = new Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.FromUnixTimeSeconds(1000));
        var now = clock.GetUtcNow();
        var legacy = charm with { Position = Slot(3), Core = charm.Core with { Durability = 0 } };
        var worn = weapon with { Position = new(InventoryListType.Equipment, 12), Core = weapon.Core with { Durability = 0 } };
        var role = character with { Inventory = new([legacy, worn])
            { AccountWallet = character.Inventory.AccountWallet.ActivateContract(586, 1000, now) } };
        var result = InventoryRepairService.Automatic(role, items, now, clock);
        That(result.Accepted && result.Plan!.After.Find(legacy.Position) == legacy &&
            result.Plan.After.Find(worn.Position)!.Core.Durability == 40 &&
            DevilContractPolicy.Used(result.Plan.After, 6, now) == 1,
            "契约自动修理只恢复普通破损装备，不借此把旧符咒能量充满");
        That(!InventoryRepairService.Automatic(role with { Inventory = role.Inventory with { Items = [legacy] } },
                items, now, clock).Accepted && role.Inventory.DevilUses.IsEmpty,
            "只有旧符咒零能量不能触发免费自动修理或消耗契约次数");
        var modern = heraldry with { Position = Slot(3), Core = heraldry.Core with { Durability = 0 } };
        var modernRole = role with { Inventory = role.Inventory with { Items = [modern] } };
        var repaired = InventoryRepairService.Automatic(modernRole, items, now, clock);
        That(repaired.Accepted && repaired.Plan!.After.Find(modern.Position) == modern with
            { Core = modern.Core with { Durability = 100 } }, "Heraldry新纹章仍保留原有自动修理，不随旧符咒一起排除");
    }

    static void VerifyFatigueEnergy(ItemCatalog items, InventoryItem charm, InventoryItem permanent,
        InventoryItem heraldry, Character character)
    {
        var item = charm with { Position = Slot(3), Core = charm.Core with { Durability = 3 } };
        var role = character with { Inventory = new([item]) };
        var source = QuickSlotEquipmentEnergy.Capture(role, items, 1000);
        var spent = QuickSlotEquipmentEnergy.Plan(role, role.Inventory, source, 2)!;
        That(spent.After.Find(item.Position) == item with { Core = item.Core with { Durability = 1 } } &&
            role.Inventory.Find(item.Position) == item, "疲劳消耗2只扣已装备能量2，完整保留品级、UID和原快照");
        var lastPoint = CharacterFatigueState.NewOrdinary with { Used = 155, Remaining = 1 };
        int lost = lastPoint.Remaining - lastPoint.ConsumeOrdinaryRoom(2).Remaining;
        That(lost == 1 && QuickSlotEquipmentEnergy.Plan(role, role.Inventory, source, lost)!.After.Find(item.Position)!.Core.Durability == 2,
            "燃烧应耗2但真实疲劳只剩1时，能量只扣实际余额差1");
        var exhausted = QuickSlotEquipmentEnergy.Plan(role, role.Inventory, source, 8)!;
        That(exhausted.After.Find(item.Position)!.Core.Durability == 0 &&
            !QuickSlotEquipmentRules.IsActive(exhausted.After.Find(item.Position)!, items.Get(item.Core.ItemId), 1000) &&
            QuickSlotEquipmentEnergy.Plan(role, role.Inventory, source, 0) is null,
            "实际消耗超过剩余能量钳到0并失效，零疲劳消耗不扣能量");
        var unlimited = role with { Inventory = new([permanent with { Position = Slot(3) }]) };
        var modern = role with { Inventory = new([heraldry with { Position = Slot(3) }]) };
        That(QuickSlotEquipmentEnergy.Capture(unlimited, items, 1000) is null &&
            QuickSlotEquipmentEnergy.Capture(modern, items, 1000) is not null &&
            QuickSlotEquipmentEnergy.Capture(character, items, 1000) is null,
            "永久型和未装备物品不消耗，新纹章与有限旧符咒同用真实耐久字段");
        That(QuickSlotEquipmentEnergy.Plan(role, new([item with { Uid = 10000 }]), source, 1) is null &&
            QuickSlotEquipmentEnergy.ChangedSlots(role.Inventory, spent.After).SequenceEqual([item.Position]),
            "疲劳规划不会扣同槽新实例，投影只包含本次实际降低的格子");
    }

    static InventorySlot Slot(short slot) => new(InventoryListType.Main, slot);
}
