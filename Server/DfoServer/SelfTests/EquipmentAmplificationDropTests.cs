using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentAmplificationDropTests
{
    public static void RunCore()
    {
        int before = Count;
        var rules = AmplificationTests.Rules() with
        {
            GenerationRates = [0, 0, 12, 2061, 2000, 2000, 2000],
            GenerationOptions = [new(1, 2500), new(2, 2500), new(3, 2500), new(4, 2500)]
        };
        var policy = new EquipmentAmplificationDropPolicy(rules);
        var template = new ItemTemplate(990201, ItemKind.Equipment, 12, 55, 40, 1, 20, 2, 57, "[sealing]", ushort.MaxValue, 0, "Drop");
        var core = InventoryRewardGrantService.Create(template) with { Value = 77, Attr = 0xE0, EnchantCardId = 3175,
            EnchantUpgrade = 2, GenuineUpgrade = 4, Rune = 8, Random0 = new(1, 2, 3), Extension114 = new(1, 333) };
        foreach (var invalid in new[] { template with { MinimumLevel = 54, Grade = 100 }, template with { Grade = 56 },
            template with { Rarity = 0 }, template with { Rarity = 1 }, template with { Rarity = 7 },
            template with { EquipmentSlot = 13 }, template with { EquipmentSlot = 24 }, template with { Kind = ItemKind.Avatar } })
            That(!policy.Eligible(invalid) && policy.Apply(invalid, core, _ => throw new InvalidOperationException("不可用模板不抽气息")) == core,
                "最低55级、修正grade和rarity/部位同时成立，不符合时不影响其他随机序列");
        That(policy.Eligible(template) && policy.Eligible(template with { MinimumLevel = 60 }) &&
            policy.Eligible(template with { Rarity = 3, Grade = 59 }), "55级达到修正grade时即可参与，不把60级当固定下限");
        foreach (var (roll, type) in new (int, byte)[] { (0, 1), (2499, 1), (2500, 2), (4999, 2), (5000, 3), (7499, 3), (7500, 4), (9999, 4) })
        {
            var draws = new Queue<int>([11, roll]);
            var result = policy.Apply(template, core, bound => { That(bound == 10000, "概率及属性均使用独立万分域"); return draws.Dequeue(); });
            That(draws.Count == 0 && result == core with { AmplifyType = (byte)(0x80 | type), AmplifyValue = 5 },
                "稀有12/10000末端命中、四维权重区间及完整核心保留");
        }
        int calls = 0;
        That(policy.Apply(template, core, _ => { calls++; return 12; }) == core && calls == 1,
            "稀有概率等号12不命中，未命中不再抽属性，不将12误读成12百分比");
        var unique = template with { Rarity = 3, Grade = 59 };
        var selected = new Queue<int>([2060, 5000]);
        That(policy.Apply(unique, core, _ => selected.Dequeue()) is { AmplifyType: 0x83, AmplifyValue: 6 } &&
            policy.Apply(unique, core, _ => 2061) == core, "神器2061/10000边界及原float32基础值截断保留");
        selected = new([0, 5000]);
        That(policy.Apply(unique with { AmplifyPollutionForbidden = true }, core, _ => selected.Dequeue()) is { AmplifyType: 3, AmplifyValue: 6 },
            "禁止pollution只免除未净化位，仍保留生成的已识别异次元属性");
        var partial = new EquipmentAmplificationDropPolicy(rules with { GenerationOptions = [new(1, 2500)] });
        selected = new([0, 2500]);
        That(partial.Apply(template, core, _ => selected.Dequeue()) == core, "属性权重不足万分时保留空区间，不归一化或补抽");
        var existing = core with { AmplifyType = 3, AmplifyValue = 51 };
        That(policy.Apply(template, existing, _ => throw new InvalidOperationException()) == existing, "已有属性不被生成规则覆盖或重抽");
        Reject<InvalidDataException>(() => policy.Apply(template, core, _ => -1));
        Reject<InvalidDataException>(() => policy.Apply(template, core, _ => 10000));

        var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(template, 1) });
        var plan = new DungeonDropGenerationPlan("合成多件气息掉落", [new(10000, pool, 2, 2, GrowthContractEligible: true)],
            referenceQuality: true, amplification: policy);
        var steps = new Queue<(int Bound, int Value)>([(10000, 0), (1, 0), (999999997, 0), (10000, 11), (10000, 5000),
            (999999997, 1), (10000, 12)]);
        var generated = plan.WithGrowthContract(10).Generate(bound =>
        { var sample = steps.Dequeue(); That(sample.Bound == bound, "多件装备依次独立创建种子、气息概率和隐藏属性"); return sample.Value; },
            DateTimeOffset.FromUnixTimeSeconds(1800000000));
        That(steps.Count == 0 && generated[0].Item is { Value: 1, AmplifyType: 0x83, AmplifyValue: 5 } &&
            generated[1].Item is { Value: 2, AmplifyType: 0 } && ReferenceEquals(plan.WithGrowthContract(10).Amplification, policy),
            "成长契约计划复制保留气息策略，两件同模板装备不共享一次生成结果");
        That(InventoryRewardGrantService.Create(template).AmplifyType == 0, "仅掉落创建启用，不改变GM、商城或普通发奖的创建政策");
        Console.WriteLine($"PASS {Count - before} 掉落异界气息资格、PVF概率、隐藏属性及独立冻结断言");
    }

    public static void RunResources(ItemResources resources)
    {
        var rules = resources.Amplifications.Rules;
        That(rules.GenerationRates.SequenceEqual(new[] { 0, 0, 12, 2061, 2000, 2000, 2000 }) &&
            rules.GenerationOptions.Select(row => row.Weight).SequenceEqual(new[] { 2500, 2500, 2500, 2500 }),
            "当前PVF直接提供七种稀有度生成概率及四维权重，不另加服务端固定概率");
        var template = resources.Items.Get(31440);
        var policy = new EquipmentAmplificationDropPolicy(rules);
        That(policy.Eligible(template) && resources.Items.Get(400200058).AmplifyPollutionForbidden,
            "真实GT9600满足掉落资格，真实诅咒装备的pollution限制从有效脚本读取");
        var result = DungeonDropCreation.Create(template, 1, bound => bound == int.MaxValue ? 77 : 0,
            DateTimeOffset.UtcNow, amplification: policy);
        That(result is { Value: 77, AmplifyType: 0x81, AmplifyValue: > 0 } && result.Durability == template.Durability,
            "真实装备按共享创建器生成未净化隐藏基础值与满耐久");
    }
}
