using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentUpgradeBonusTests
{
    public static void RunCore()
    {
        int before = Count;
        That(new EquipmentUpgradeBonus(400, 0, 0, null).SuccessWeight(50000, 100000) == 52000 &&
            new EquipmentUpgradeBonus(500, 0, 0, null).SuccessWeight(40000, 100000) == 42000 &&
            new EquipmentUpgradeBonus(400, 2000, 0, null).SuccessWeight(50000, 100000) == 54000,
            "两列新词条按相对倍率，旧词条直接减少失败权重，二者不可混用单位");
        That(new EquipmentUpgradeBonus(400, 0, 0, null).SuccessWeight(0, 100000) == 0 &&
            new EquipmentUpgradeBonus(0, 2000, 0, null).SuccessWeight(0, 100000) == 2000 &&
            new EquipmentUpgradeBonus(float.MaxValue, float.MaxValue, 0, null).SuccessWeight(99999, 100000) == 100000 &&
            new EquipmentUpgradeBonus(-20000, 0, 0, null).SuccessWeight(5000, 10000) == 0,
            "相对加成不抬高零概率，旧权重沿旧端直接加值，最终结果钳在概率区间");
        That(new EquipmentUpgradeBonus(700, 0, 0, null).SuccessWeight(33333, 100000) == 35666 &&
            new EquipmentUpgradeBonus(700, 0, 0, null).SuccessWeight(3333, 10000) == 3566,
            "多装备先汇总同单位数值，维持当前入口的概率分母和单次向下取整");
        That(new EquipmentUpgradeBonus(0, 0, 4, null).GoldCost(273960) == 263002 &&
            new EquipmentUpgradeBonus(0, 0, 200, null).GoldCost(1001) == 0 &&
            new EquipmentUpgradeBonus(0, 0, 3, null).RefinementMaterials(66) == 64,
            "折扣在原费用计算后应用，金币四舍五入且不为负，锻造保留向下取整及至少一份材料");

        var templates = new[]
        {
            Template(1, ItemKind.Equipment, 13, new(100, 0, 0, 3, 0)),
            Template(2, ItemKind.Creature, 26, new(400, 500, 0, 4, 5)),
            Template(3, ItemKind.CreatureEquipment, 27, new(200, 0, 1000, 0, 0)),
            Template(4, ItemKind.Avatar, 3, new(100, 0, 0, 0, 0)),
            Template(5, ItemKind.Rune, 0, new(50, 0, 0, 0, 0))
        };
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(templates.Select(row => KeyValuePair.Create(row.Id, row)));
        InventoryItem Worn(int id) => new(id, new(InventoryListType.Equipment, templates[id - 1].EquipmentSlot),
            new(id, templates[id - 1].Kind, id))
            {
                Avatar = templates[id - 1].Kind == ItemKind.Avatar ? new() : null,
                Creature = templates[id - 1].Kind == ItemKind.Creature ? new() : null
            };
        Character Role(params InventoryItem[] rows) => new(1, 0, "UpgradeBonus", 0, new(1, 0, 0, 0), null)
            { Inventory = new([.. rows]) };
        var role = Role(Worn(1), Worn(2), Worn(3), Worn(4),
            Worn(5) with { Position = new(InventoryListType.Talisman, 210) });
        EquipmentUpgradeBonus Capture(Character value, EquipmentUpgradeKind mode = EquipmentUpgradeKind.Reinforcement,
            long now = 100, bool cost = true) => EquipmentUpgradeBonusPolicy.Capture(value, catalog, mode, now, cost);
        That(Capture(role) == new EquipmentUpgradeBonus(850, 1000, 7, null) &&
            Capture(role, EquipmentUpgradeKind.Amplification) == new EquipmentUpgradeBonus(500, 1000, 5, null) &&
            Capture(role, EquipmentUpgradeKind.Refinement) == new EquipmentUpgradeBonus(0, 0, 7, null),
            "称号、成宠、宠物装备、装扮及装载符文按真实穿戴叠加，锻造不冒用成功率列");
        var unrelated = Role(Worn(1) with { Position = new(InventoryListType.Main, 9) },
            Worn(2) with { Position = new(InventoryListType.Equipment, 33) },
            Worn(4) with { Position = new(InventoryListType.Equipment, 11) },
            Worn(3) with { Position = new(InventoryListType.PersonalCargo, 0) });
        That(Capture(unrelated) == default && Capture(role with { Id = 2, Inventory = new([]) }) == default,
            "背包、仓库、纯外观槽和其他角色不提供改造加成");
        var limited = Worn(2) with { Creature = Worn(2).Creature! with { ExpireTime = 110 } };
        That(Capture(Role(limited), now: 109).ExpiresAt == 110 && Capture(Role(limited), now: 110) == default,
            "期限到期等号停止装备加成，并将参与来源的期限交给原事务复验");
        catalog.PrefillTemplates([KeyValuePair.Create(2, templates[1] with { CreatureEgg = true })]);
        That(Capture(Role(Worn(2))) == default, "宠物蛋不能作为成宠提供加成");
        catalog.PrefillTemplates([KeyValuePair.Create(2, templates[1] with { UsableExpired = true })]);
        That(Capture(Role(limited), now: 111) is { RelativeRate: 400, ExpiresAt: null },
            "明确允许过期使用的装备仍沿原有资格生效");
        That(Capture(Role(Worn(1)), cost: false) is { RelativeRate: 100, CostDiscount: 0 },
            "便携来源不借费用折扣减免原来的源消耗");
        That(EquipmentUpgradeBonuses.Read(new PvfLib.PvfDocument("[upgrade cost discount]\n4 0 1\n")) is
            { ReinforcementDiscount: 0, AmplificationDiscount: 4 }, "费用标志只在整数1时启用对应模式");
        Console.WriteLine($"PASS {Count - before} equipment upgrade bonus assertions");
    }

    static ItemTemplate Template(int id, ItemKind kind, byte slot, EquipmentUpgradeBonuses bonus) =>
        new(id, kind, slot, 1, 0, 1, 0, 2, 1, "[free]", ushort.MaxValue, 0, "UpgradeBonus") { UpgradeBonuses = bonus };

    public static void RunResources(TcpTestResources resources)
    {
        var r = resources.ItemResources;
        foreach (var (id, reinforce, amplify) in new[] { (26698, 100, 0), (26699, 100, 0), (400330110, 400, 0),
            (400990076, 400, 0), (400990169, 400, 0), (400990006, 200, 0), (400990007, 300, 0), (400990278, 0, 500) })
            That(r.Items.Get(id).UpgradeBonuses is var bonus && bonus.ReinforcementRate == reinforce &&
                bonus.AmplificationRate == amplify, "实际EQU模板保存两列成功率而非根据编号赋值：" + id);
        That(r.Items.Get(100330566).UpgradeBonuses is { ReinforcementRate: 0, AmplificationRate: 0,
            ReinforcementDiscount: 5, AmplificationDiscount: 5 }, "黄金烈鹰的费用折扣不误作成功率加成");
        var cold = new ItemCatalog(resources.Archive,
            new Dictionary<int, string> { [26698] = "character/common/title/craftsmanship.equ" }, new Dictionary<int, string>(), 0);
        var coldRole = new Character(1, 10000, "ColdUpgrade", 0, resources.Towns.Room, null)
            { Inventory = new([new(1, new(InventoryListType.Equipment, 13), new(26698, ItemKind.Equipment, 1))]) };
        That(!cold.TryGetLoaded(26698, out _), "新目录尚未装载已穿称号");
        EquipmentUpgradeBonusPolicy.Prepare(coldRole, cold);
        That(EquipmentUpgradeBonusPolicy.Capture(coldRole, cold, EquipmentUpgradeKind.Reinforcement, 100).RelativeRate == 100,
            "首次请求的锁外准备装载真实EQU，锁内捕获不触发资源读取");
        var clock = new ConsumableUseSessionTests.FakeClock();
        var random = new AmplifyUpgradeTests.FixedRandom(0);
        var service = new InventoryAmplifyUpgradeService(r.Items, r.AmplifyUpgrades, r.Enhancements,
            r.Disjoint, r.GoldLimits, r.EpicPieces, random, clock);
        var template = r.Items.Get(31440);
        var target = new InventoryItem(1, new(InventoryListType.Main, 9),
            InventoryRewardGrantService.Create(template) with { Attr = 9, AmplifyType = 3, AmplifyValue = 6,
                EnchantCardId = 3175, GenuineUpgrade = 4, ExpireTime = 0 });
        var source = new InventoryItem(2, new(InventoryListType.Main, 121),
            InventoryRewardGrantService.Create(r.Items.Get(3242), 1000) with { ExpireTime = 0 });
        Character role = new(1, 10000, "EquippedUpgrade", 0, resources.Towns.Room, null)
            { Level = 75, Job = 2, Inventory = new([target, source], Gold: 100_000_000) };
        var request = new EnhancementRequest(EnhancementMode.Amplify, target.Position, template.Id, source.Position.Index,
            -1, ReadOnlyMemory<byte>.Empty, -1, -1);
        InventoryItem Worn(int id)
        {
            var definition = r.Items.Get(id);
            var core = InventoryRewardGrantService.Create(definition) with { ExpireTime = 0 };
            if (definition.Kind is ItemKind.Avatar or ItemKind.Creature) core = core with { Value = id };
            return new(id, new(InventoryListType.Equipment, definition.EquipmentSlot), core)
            {
                Avatar = definition.Kind == ItemKind.Avatar ? new() : null,
                Creature = definition.Kind == ItemKind.Creature ? new() : null
            };
        }
        var prey = Worn(400990278);
        var equipped = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(prey) } };
        int failure = r.AmplifyUpgrades.Rules.Steps[9].FailureWeight;
        random.Value = failure - 1;
        int draws = random.Calls;
        var plain = service.Plan(role, request, out var original);
        var improved = service.Plan(equipped, request, out var result);
        int gold = (int)Math.Floor(r.AmplifyUpgrades.Rules.Gold(template, 9) * 0.95 + 0.5);
        That(plain.Accepted && improved.Accepted && original is { Succeeded: false } && result is { Succeeded: true } &&
            result.GoldCost == gold && result.SuccessPercent == (100000 - failure) * 1.05m / 1000 && random.Calls == draws + 2 &&
            improved.Plan!.After.Gold == role.Inventory.Gold - gold &&
            improved.Plan.After.Find(target.Position) == target with { Core = target.Core with { Attr = 10 } } &&
            improved.Plan.After.Find(prey.Position) == prey,
            "实际普通增幅应用普雷5%成功率及5%金币折扣，单次抽取，保留目标完整核心与加成装备");
        var limited = equipped with { Inventory = equipped.Inventory with { Items = equipped.Inventory.Items.Replace(prey,
            prey with { Creature = prey.Creature! with { ExpireTime = (int)clock.GetUtcNow().ToUnixTimeSeconds() + 10 } }) } };
        var planned = service.Plan(limited, request, out _);
        long end = clock.GetUtcNow().ToUnixTimeSeconds() + 10;
        That(planned.Accepted && planned.Plan!.CharacterPrecondition!.Matches(limited, end - 1) &&
            !planned.Plan.CharacterPrecondition.Matches(limited, end), "普通增幅提交前复验加成装备期限，不延长旧计划资格");

        var reinforceTarget = target with { Core = target.Core with { AmplifyType = 0, AmplifyValue = 0 } };
        var reinforcing = role with { Inventory = role.Inventory with { Items = [reinforceTarget, Worn(26698), Worn(400990076)],
            AccountWallet = role.Inventory.AccountWallet.Set(DfoServer.Game.Currency.CurrencyKind.ClearCube, 5000) } };
        int reinforceFailure = r.AmplifyUpgrades.ReinforceRules.Steps[9].FailureWeight;
        random.Value = reinforceFailure - 1;
        That(service.Plan(reinforcing, request with { Mode = EnhancementMode.Reinforce, MaterialSlot = 358 }, out result).Accepted &&
            result is { Succeeded: true } && result.SuccessPercent == (100000 - reinforceFailure) * 1.05m / 1000 &&
            result.GoldCost == (int)Math.Floor(r.AmplifyUpgrades.ReinforceRules.Gold(template, 9) * 0.96 + 0.5),
            "真实称号和赫尔德按同次穿戴叠加强化成功率，费用只取赫尔德对应列");

        var luckySource = new InventoryItem(3, new(InventoryListType.Main, 65),
            InventoryRewardGrantService.Create(r.Items.Get(10007407), 2) with { ExpireTime = 0 });
        var lucky = role with { Inventory = role.Inventory with { Items = [target, luckySource] } };
        random.Value = 0;
        var luckyRequest = request with { Mode = EnhancementMode.LuckyAmplify, MaterialSlot = 65 };
        That(service.Plan(lucky, luckyRequest, out var luckyBefore).Accepted &&
            service.Plan(lucky with { Inventory = lucky.Inventory with { Items = lucky.Inventory.Items.Add(prey) } },
                luckyRequest, out var luckyAfter).Accepted && luckyBefore == luckyAfter,
            "穿戴成功率装备不改写幸运券的独立三段奖池和源消耗");

        var forge = new InventoryRefinementService(r.Items, r.Refinement, random, clock);
        var forgingTarget = target with { Core = target.Core with { GenuineUpgrade = 1 } };
        That(r.Refinement.TryCost(template, 1, out var cost), "实际武器存在锻造成本");
        var material = new InventoryItem(4, new(InventoryListType.Main, 121),
            InventoryRewardGrantService.Create(r.Items.Get(cost.ItemId), 1000) with { ExpireTime = 0 });
        var forging = role with { Inventory = new([forgingTarget, material, Worn(100330496)]) };
        var refined = forge.Refine(forging, new(target.Position, target.Core.ItemId, material.Position.Index, 0), out var receipt);
        That(refined.Accepted && receipt!.MaterialRemaining == 1000 - Math.Max(1, (int)Math.Floor(cost.Count * 0.97)) &&
            refined.Plan!.After.Find(forgingTarget.Position) == forgingTarget with
                { Core = forgingTarget.Core with { GenuineUpgrade = 2 } },
            "锻造实际扣除称号折扣后的指定格材料，不改写成功率或目标其他打造字段");
    }
}
