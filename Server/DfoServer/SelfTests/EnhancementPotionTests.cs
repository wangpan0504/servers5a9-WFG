using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EnhancementPotionTests
{
    public static void RunCore()
    {
        RunTicketPlans();
        var typed = File(490004594, "[item upgrad buff usual upgrade]", [1000]);
        That(EnhancementPotionDefinition.Read(typed) is { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 } &&
            EnhancementPotionDefinition.Read(File(490001356, "[item upgrad buff amplify upgrade]", [5000])) is
                { Mode: EnhancementPotionMode.Amplify, SuccessBonusWeight: 5000 },
            "强化与增幅保留各自资源整数和模式，不根据名称推断或共用一个加成");
        foreach (var bad in new[]
        {
            File(1, "[item upgrad buff usual upgrade]", []), File(1, "[item upgrad buff usual upgrade]", [0]),
            File(1, "[item upgrad buff usual upgrade]", [-1]), File(1, "[item upgrad buff usual upgrade]", [1000, 2]),
            typed with { Diagnostics = ["duplicate stackable type"] }, typed with { EffectMaintenance = 0 },
            typed with { StatChangeDuration = new(1800000, "party", null) },
            typed with { Stackable = typed.Stackable with { HasActionType = true } },
            typed with { Stackable = typed.Stackable with { HasUseActionPacket = true, DeclaredUseActionPacket = 1 } }
        }) That(EnhancementPotionDefinition.Read(bad) is null, "非法或异形强化声明不能创建持久概率加成");
        That(EnhancementPotionDefinition.Read(typed with { Stackable = typed.Stackable with
            { HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[dungeon]"] } }) is not null,
            "地点不用于判断强化药身份");
        foreach (int id in EnhancementPotionDefinition.LegacyReinforcementItems)
        {
            var legacy = File(id, "[expert town potion]", [0]);
            That(EnhancementPotionDefinition.Read(legacy) is null && EnhancementPotionDefinition.Read(legacy, true) is
                { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 } &&
                EnhancementPotionDefinition.Read(legacy with { StatChangeDuration = new(600000, "myself", null) }, true) is null,
                "旧四种已收录强化仅在新版等价合同存在时按明确本服策略补1000，不扩到别的时长");
        }
        That(EnhancementPotionDefinition.Read(File(999, "[expert town potion]", [0]), true) is null &&
            EnhancementPotionDefinition.Read(File(490001355, "[expert town potion]", [0]), true) is null &&
            EnhancementPotionDefinition.Read(File(490001358, "[expert town potion]", [0]), true) is null &&
            EnhancementPotionDefinition.Read(File(490012267, "[item upgrad buff usual upgrade]", [0]), true) is null,
            "兼容不放行任意expert0或孤儿旧编号，也不覆盖已收录编号上显式损坏的新版类别");

        IReadOnlyDictionary<int, EnhancementPotionDefinition> definitions = new Dictionary<int, EnhancementPotionDefinition>
        {
            [1] = new(EnhancementPotionMode.Reinforce, 1000), [2] = new(EnhancementPotionMode.Reinforce, 3000),
            [3] = new(EnhancementPotionMode.Amplify, 5000)
        };
        ConsumableState[] states = [new(ConsumableStateKind.Effect, 1, 2000, 7, 1),
            new(ConsumableStateKind.Effect, 2, 3000, 7, 2), new(ConsumableStateKind.Effect, 3, 4000, 7, 3),
            new(ConsumableStateKind.Cooldown, 2, 999999, 7, 2), new(ConsumableStateKind.Effect, 999, 999999, 7, 4)];
        That(EnhancementPotionPolicy.ActiveBonusWeight(states, definitions, EnhancementPotionMode.Reinforce, 1999) == 3000 &&
            EnhancementPotionPolicy.ActiveBonusWeight(states, definitions, EnhancementPotionMode.Amplify, 1999) == 5000 &&
            EnhancementPotionPolicy.ActiveBonusWeight(states, definitions, EnhancementPotionMode.Reinforce, 2999) == 3000 &&
            EnhancementPotionPolicy.ActiveBonusWeight(states, definitions, EnhancementPotionMode.Reinforce, 3000) == 0 &&
            EnhancementPotionPolicy.ActiveBonusWeight(states, definitions, EnhancementPotionMode.Amplify, 4000) == 0,
            "同模式旧多份取最大而不累加，强化增幅隔离，恰到期失效且冷却和未知道具不能冒充效果");
        That(EnhancementPotionPolicy.AdjustedSuccessWeight(60000, 1000) == 60600 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(60000, 5000) == 63000 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(33333, 1000) == 33666 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(60000, int.MaxValue) == 100000 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(0, int.MaxValue) == 0 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(60000, 0) == 60000,
            "本服十万分母相对加成保留整数边界并封顶，不把1000误当直接增加1000成功权重");
        Reject<ArgumentOutOfRangeException>(() => EnhancementPotionPolicy.AdjustedSuccessWeight(-1, 1000), "拒绝负成功权重");
        Reject<ArgumentOutOfRangeException>(() => EnhancementPotionPolicy.AdjustedSuccessWeight(100001, 0), "拒绝超过分母的原始权重");
        Reject<ArgumentOutOfRangeException>(() => EnhancementPotionPolicy.AdjustedSuccessWeight(60000, -1), "拒绝负药剂加成");
    }

    static void RunTicketPlans()
    {
        const int sourceId = 990291, gearId = 990292;
        var sourceTemplate = new ItemTemplate(sourceId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1,
            "[free]", ushort.MaxValue, 0, "药剂边界券");
        var gearTemplate = new ItemTemplate(gearId, ItemKind.Equipment, 12, 10, 45, 1, 20, 1, 10,
            "[free]", ushort.MaxValue, 0, "药剂边界装备");
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), new(sourceId, ItemKind.Consumable, 2));
        var target = new InventoryItem(2, new(InventoryListType.Main, 9), new(gearId, ItemKind.Equipment, 123)
            { Attr = 0xE0, Durability = 45, GenuineUpgrade = 4 });
        Character role = new(1, 0, "PotionTicket", 0, new(0, 0, 0, 0), null)
            { Level = 20, Inventory = new([source, target]) };
        var request = new EnhancementRequest(EnhancementMode.Reinforce, target.Position, gearId, 65, -1,
            ReadOnlyMemory<byte>.Empty, -1, -1);
        var additional = EnhancementCatalog.Parse(sourceId, "[equipment reinforcement ticket]\n1 60 `additional` -1\n")!;
        int calls = 0;
        InventoryResult Plan(EnhancementDefinition rule, int bonus, out EnhancementReceipt? receipt) =>
            InventoryEnhancementService.Plan(role, request with { Mode = rule.Mode }, 1900000000, rule,
                sourceTemplate, gearTemplate, () => { calls++; return 6000; }, out receipt, potionSuccessBonusWeight: bonus);
        int before = calls;
        var plain = Plan(additional, 0, out var plainReceipt);
        var boosted = Plan(additional, 1000, out var boostedReceipt);
        That(plain.Accepted && plainReceipt is { Succeeded: false, NewLevel: 0 } && boosted.Accepted &&
            boostedReceipt is { Succeeded: true, NewLevel: 1, MaterialRemaining: 1 } && calls == before + 2 &&
            boosted.Plan!.After.Find(target.Position) == target with { Core = target.Core with { Attr = 0xE1 } } &&
            boosted.Plan.After.Find(source.Position)!.Core.Count == 1 && plain.Plan!.After.Find(target.Position) == target,
            "真实逐级强化规划相同6000抽样无药失败有药成功，各抽一次扣一券，目标UID及完整核心保留");
        var fixedRule = additional with { Additional = false, Level = 11 };
        That(Plan(fixedRule, int.MaxValue, out var fixedReceipt).Accepted && fixedReceipt is
            { Succeeded: false, NewLevel: 0, MaterialRemaining: 1 },
            "固定等级券保持原6000边界，巨大药剂加成不能变成保底成功");
        var randomRule = additional with { Mode = EnhancementMode.RandomReinforce, Additional = false,
            RandomLevels = [new(7, 6000), new(12, 4000)] };
        var randomPlain = Plan(randomRule, 0, out var randomPlainReceipt);
        var randomBoost = Plan(randomRule, int.MaxValue, out var randomBoostReceipt);
        That(randomPlain.Accepted && randomBoost.Accepted && randomPlainReceipt is { Succeeded: true, NewLevel: 12, Path: 1 } &&
            randomBoostReceipt == randomPlainReceipt &&
            randomBoost.Plan!.After.Find(target.Position) == randomPlain.Plan!.After.Find(target.Position),
            "随机等级券相同抽样仍落原第二档，药剂不能重权重、追加等级或改变真实装备结果");
    }

    public static void RunResources(IReadOnlyDictionary<int, EnhancementPotionDefinition> definitions)
    {
        That(definitions.TryGetValue(490004594, out var reinforce) && reinforce is
            { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 }, "正式资源强化秘药明确保留1000参数");
        foreach (var (id, bonus) in new[] { (10004688,5000), (10004745,3000), (490001534,1000), (490010213,500), (490012609,500) })
            That(definitions.TryGetValue(id, out var amplify) && amplify is
                { Mode: EnhancementPotionMode.Amplify } && amplify.SuccessBonusWeight == bonus &&
                amplify.IsFamilyItem(10004688) && !amplify.IsFamilyItem(490004594),
                "正式LST收录增幅药保留5000、3000、1000、500不同参数并接入独立同族：" + id);
        foreach (int id in EnhancementPotionDefinition.LegacyReinforcementItems)
            That(definitions.TryGetValue(id, out var legacy) && legacy is
                { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 } &&
                legacy.IsFamilyItem(490004594) && !legacy.IsFamilyItem(10004688),
                "正式旧版强化在等价新版存在时沿明确本服策略接入同族：" + id);
        That(!definitions.ContainsKey(490000413) && !definitions.ContainsKey(10008220) &&
            !definitions.ContainsKey(490001356) && !definitions.ContainsKey(490001359) &&
            !definitions.ContainsKey(490001355) && !definitions.ContainsKey(490001358),
            "史诗、随机强化宝盒和未被正式LST收录的孤儿STK不冒充可用固定升级药剂");
    }

    static RecoveryConsumableFile File(int id, string category, IReadOnlyList<int> values)
    {
        var raw = new StackableFile(id, "stackable/synthetic.stk", new(category, values), 100, null, null, 1, "potion", []);
        return new(id, raw.ScriptPath, raw, 1800000, 351, null, null, ["stackable type", "stat change duration", "effect maintenance"], [])
            { StatChangeDuration = new(1800000, "myself", null), EffectMaintenance = 7, CooltimeMaintenance = 7 };
    }
}
