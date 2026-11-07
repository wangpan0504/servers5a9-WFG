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
        var typed = File(490005584, "[item upgrad buff usual upgrade]", [1000]);
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
        foreach (int id in new[] { 2600025, 10007343, 490004594 })
        {
            var legacy = File(id, "[expert town potion]", [0]);
            That(EnhancementPotionDefinition.Read(legacy) is
                { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 10000 } &&
                EnhancementPotionDefinition.Read(legacy, 100) is
                { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 },
                "已确认炼金强化身份独立读取自身EXJ万分比，缺项兜底10%且不依赖另一件新版药剂");
        }
        var alchemist = File(2600025, "[expert town potion]", [0]);
        var large = EnhancementPotionDefinition.Read(alchemist, int.MaxValue)!;
        That(EnhancementPotionDefinition.Read(alchemist, 0) is { SuccessBonusWeight: 0 } &&
            EnhancementPotionDefinition.Read(alchemist, -1) is null && large.SuccessBonusWeight == (long)int.MaxValue * 10 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(1, large.SuccessBonusWeight) == 100000 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(0, large.SuccessBonusWeight) == 0,
            "显式0不回退，负参数拒绝，完整int参数换算不溢出且保留成功率封顶和零概率");
        That(EnhancementPotionDefinition.Read(File(10007343, "[expert town potion]", [0]) with
            { StatChangeDuration = new(30000, "myself", null) }, 1000) is { SuccessBonusWeight: 10000 } &&
            EnhancementPotionDefinition.Read(File(490004594, "[item upgrad buff usual upgrade]", [1000]), 2000)
                is { SuccessBonusWeight: 1000 } &&
            EnhancementPotionDefinition.Read(typed, 9000) is { SuccessBonusWeight: 1000 } &&
            EnhancementPotionDefinition.Read(File(490004594, "[item upgrad buff amplify upgrade]", [5000]), -1)
                is { Mode: EnhancementPotionMode.Amplify, SuccessBonusWeight: 5000 },
            "水晶之光接受自身30秒声明；编号是否登记及EXJ是否冲突都不覆盖明确的新类型、模式及参数");
        That(EnhancementPotionPolicy.AdjustedSuccessWeight(40000, EnhancementPotionDefinition.Read(alchemist)!.SuccessBonusWeight) == 44000 &&
            EnhancementPotionPolicy.AdjustedSuccessWeight(40000, EnhancementPotionDefinition.Read(alchemist, 100)!.SuccessBonusWeight) == 40400,
            "兜底10%使40%成为44%，EXJ100使40%成为40.4%，都不是直接增加概率百分点");
        That(EnhancementPotionDefinition.Read(File(999, "[expert town potion]", [0]), 1000) is null &&
            EnhancementPotionDefinition.Read(File(2600018, "[expert town potion]", [0]), 5000) is null &&
            EnhancementPotionDefinition.Read(File(10008220, "[expert town potion]", [0]), 1000) is null &&
            EnhancementPotionDefinition.Read(File(490001355, "[expert town potion]", [0]), 1000) is null &&
            EnhancementPotionDefinition.Read(File(490001358, "[expert town potion]", [0]), 1000) is null &&
            EnhancementPotionDefinition.Read(File(490012267, "[item upgrad buff usual upgrade]", [0]), 1000) is null,
            "参数表不授予效果身份，财宝药、随机活动药、孤儿编号和损坏新版类别仍不放行");

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
        That(EnhancementPotionPolicy.ActiveBonusWeight([new(ConsumableStateKind.Effect, 2600025, 2000, 7, 1)],
            new Dictionary<int, EnhancementPotionDefinition> { [2600025] = EnhancementPotionDefinition.Read(alchemist, 0)! },
            EnhancementPotionMode.Reinforce, 1000) == 0, "显式零参数的有效药效不报错，也不获得兜底加成");
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
        InventoryResult Plan(EnhancementDefinition rule, long bonus, out EnhancementReceipt? receipt) =>
            InventoryEnhancementService.Plan(role, request with { Mode = rule.Mode }, 1900000000, rule,
                sourceTemplate, gearTemplate, () => { calls++; return 6000; }, out receipt, potionSuccessBonusWeight: bonus);
        int before = calls;
        var plain = Plan(additional, 0, out var plainReceipt);
        var boosted = Plan(additional, 1000, out var boostedReceipt);
        That(plain.Accepted && plainReceipt is { Succeeded: false, NewLevel: 0 } && boosted.Accepted &&
            boostedReceipt is { Succeeded: true, NewLevel: 1, MaterialRemaining: 1 } && calls == before + 2 &&
            plainReceipt is { BaseSuccessPercent: 60m, SuccessPercent: 60m } &&
            boostedReceipt is { BaseSuccessPercent: 60m, SuccessPercent: 60.6m } &&
            boosted.Plan!.After.Find(target.Position) == target with { Core = target.Core with { Attr = 0xE1 } } &&
            boosted.Plan.After.Find(source.Position)!.Core.Count == 1 && plain.Plan!.After.Find(target.Position) == target,
            "真实逐级强化规划相同6000抽样无药失败有药成功，各抽一次扣一券，目标UID及完整核心保留");
        That(!System.Text.Json.JsonSerializer.Serialize(boostedReceipt).Contains("SuccessPercent", StringComparison.Ordinal),
            "概率诊断不进入持久事实JSON，不修改已有回执格式");
        var fixedRule = additional with { Additional = false, Level = 11 };
        That(Plan(fixedRule, int.MaxValue, out var fixedReceipt).Accepted && fixedReceipt is
            { Succeeded: false, NewLevel: 0, MaterialRemaining: 1, BaseSuccessPercent: 60m, SuccessPercent: 60m },
            "固定等级券保持原6000边界，巨大药剂加成不能变成保底成功");
        var randomRule = additional with { Mode = EnhancementMode.RandomReinforce, Additional = false,
            RandomLevels = [new(7, 6000), new(12, 4000)] };
        var randomPlain = Plan(randomRule, 0, out var randomPlainReceipt);
        var randomBoost = Plan(randomRule, int.MaxValue, out var randomBoostReceipt);
        That(randomPlain.Accepted && randomBoost.Accepted && randomPlainReceipt is { Succeeded: true, NewLevel: 12, Path: 1 } &&
            randomPlainReceipt is { BaseSuccessPercent: null, SuccessPercent: null } &&
            randomBoostReceipt == randomPlainReceipt &&
            randomBoost.Plan!.After.Find(target.Position) == randomPlain.Plan!.After.Find(target.Position),
            "随机等级券相同抽样仍落原第二档，药剂不能重权重、追加等级或改变真实装备结果");
    }

    public static void RunResources(IReadOnlyDictionary<int, EnhancementPotionDefinition> definitions)
    {
        That(definitions.TryGetValue(490004594, out var reinforce) && reinforce is
            { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 }, "正式新版强化秘药优先自身类型参数，不因缺EXJ行使用兜底");
        That(definitions.TryGetValue(490001533, out var guildReinforce) && guildReinforce is
            { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 } &&
            guildReinforce.IsFamilyItem(490004594),
            "公会商店永恒的强化的秘密按强化秘药接入同族");
        foreach (var (id, bonus) in new[] { (10004688,5000), (10004745,3000), (490001534,1000), (490010213,500), (490012609,500) })
            That(definitions.TryGetValue(id, out var amplify) && amplify is
                { Mode: EnhancementPotionMode.Amplify } && amplify.SuccessBonusWeight == bonus &&
                amplify.IsFamilyItem(10004688) && !amplify.IsFamilyItem(490004594),
                "正式LST收录增幅药保留5000、3000、1000、500不同参数并接入独立同族：" + id);
        foreach (var (id, bonus) in new[] { (490004594,1000), (490006368,1000), (490007878,1000), (490008465,1000),
            (490012267,10000), (490012268,10000), (2749959,10000), (2600025,1000), (10007343,10000),
            (10006268,1000), (490702938,1000), (490011297,1000), (490009901,1000), (490009714,1000),
            (490007876,1000), (490002394,1000), (490001497,1000) })
            That(definitions.TryGetValue(id, out var legacy) && legacy is
                { Mode: EnhancementPotionMode.Reinforce } && legacy.SuccessBonusWeight == bonus &&
                legacy.IsFamilyItem(490004594) && !legacy.IsFamilyItem(10004688),
                "17项正式强化药保留新版参数，仅旧式声明取自身EXJ或10%兜底，同族覆盖且不混入增幅：" + id);
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
