using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HarlemAbyssBonusDropTests
{
    public static void RunCore(DungeonEntryPlan entry, DungeonPlannedMonster wave)
    {
        int before = Count;
        var rules = HarlemAbyssBonusRules.Parse("{}");
        That(rules.InvitationNumerator == 2 && rules.InvitationDenominator == 3 && rules.InvitationMaximumCount == 2 &&
            rules.SoulPermille == 10 && rules.CrystalPermille == 1 && rules.SkyFragmentPermille == 100 && rules.StoneBagPermille == 5 &&
            rules.SkyFragments.SequenceEqual([2, 3, 4, 4, 5]) && rules.CosmoSkyFragmentMultiplier == 2,
            "默认参数为用户批准的本服概率，不能声称为PVF原值");
        foreach (string invalid in new[] { "null", "{\"InvitationDenominator\":0}", "{\"InvitationNumerator\":4}",
            "{\"InvitationNumerator\":-1}", "{\"InvitationMaximumCount\":0}", "{\"SoulPermille\":1001}",
            "{\"CrystalPermille\":-1}", "{\"SkyFragmentPermille\":1001}", "{\"StoneBagPermille\":-1}",
            "{\"SkyFragments\":[2,3]}", "{\"CosmoSkyFragmentMultiplier\":0}" })
            Reject<InvalidDataException>(() => HarlemAbyssBonusRules.Parse(invalid), "拒绝空配置、非法概率和数量，不自动截断或回退");
        Reject<JsonException>(() => HarlemAbyssBonusRules.Parse("{\"SoulPercent\":1}"), "配置拼错须明确拒绝");
        Reject<JsonException>(() => HarlemAbyssBonusRules.Parse("{\"SkyFragments\":null}"), "数量数组显式null在JSON类型检查时拒绝");
        ItemTemplate Item(int id) => new(id, ItemKind.Material, 0, 1, 0, 100, 1, 0, 1, "[free]", ushort.MaxValue, 0, "合成补全奖励");
        var epicPool = new DungeonItemDropPool(new DungeonWeightedDropItem[] {
            new(Item(999) with { Kind = ItemKind.Equipment, Rarity = 4, MinimumLevel = 95, StackLimit = 1 }, 1) });
        var map = entry.BossMap with { Id = 92690 };
        var source = new DungeonRoomMonster(42, [42, 1, 0, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/合成终点.mob");
        var cosmo = new DungeonCosmoRewardCatalog(8, 4, [new(map, [source], [], [])]);
        var epics = new HarlemHellEpicDropPolicy([entry.DungeonId], epicPool, cosmo: cosmo);
        var policy = new HarlemAbyssBonusDropPolicy(rules, epics, Item);
        var empty = new DungeonDropGenerationPlan("合成空计划", []);
        var star = entry with { IsHellLegend = true };
        var ultra = entry with { DungeonId = 7191, HellParty = null, Difficulty = 4, BossMap = map };
        var boss = new DungeonPlannedMonster(0, 11, 42, 95, source) { SpawnType = 3 };
        var normalPlan = policy.Extend(empty, entry, wave);
        var starPlan = policy.Extend(empty, star, wave);
        var ultraPlan = policy.Extend(empty, ultra, boss);
        That(normalPlan.Trials.Length == 3 && starPlan.Trials.Length == 5 && ultraPlan.Trials.Length == 5,
            "普通只补票、灵魂、晶石，星空及超星空另补苍穹碎片与20灵石礼袋");
        foreach (var invalid in new[] { entry with { HellParty = null }, entry with { DungeonId = 530 },
            entry with { IsPractice = true }, entry with { RouteQuestId = 10 } })
            That(ReferenceEquals(policy.Extend(empty, invalid, wave), empty), "普通图、黎明、练习及任务路线不借深渊奖励");
        foreach (var invalid in new[] { wave with { HellWave = null }, wave with { HellRewardRolls = 0 },
            wave with { IsFriendly = true }, wave with { IsBlocking = false } })
            That(ReferenceEquals(policy.Extend(empty, star, invalid), empty), "非末波奖励计划、友方和不可死亡演员不补发奖励");
        That(ReferenceEquals(policy.Extend(empty, ultra, boss with { IsDummyMapBoss = true }), empty) &&
            ReferenceEquals(policy.Extend(empty, ultra with { Difficulty = 0 }, boss), empty), "超星空仅合法真实最终领主领奖");
        int[] invitationCounts = new int[3];
        var invitation = new DungeonDropGenerationPlan("合成邀请函边界", [normalPlan.Trials[0]]);
        for (int chance = 0; chance < 3; chance++)
        for (int count = 0; count < 2; count++)
        {
            var drops = invitation.Generate(bound => bound == 3 ? chance : bound == 2 ? count : 0, DateTimeOffset.UnixEpoch);
            invitationCounts[drops.Length]++;
            That(drops.All(row => row.Item is { ItemId: 3330, Count: 1 }), "邀请函按一次数量抽样逐张落地，不逐张重判概率");
        }
        That(invitationCounts.SequenceEqual([2, 2, 2]) && invitation.MaximumObjects == 2, "零、一、两张严格等概率，完整容量预留两张");
        foreach (var (trial, threshold) in starPlan.Trials.Skip(1).Zip(new[] { 10, 1, 100, 5 }))
        {
            var isolated = new DungeonDropGenerationPlan("合成独立概率边界", [trial]);
            for (int roll = 0; roll < 1000; roll++)
            {
                var drops = isolated.Generate(bound => bound == 1000 ? roll : 0, DateTimeOffset.UnixEpoch);
                That(drops.Length == (roll < threshold ? trial.MaximumCount : 0) && drops.All(row => row.Item!.Count == 1),
                    "四项概率各自按千分比边界命中，碎片整份命中后逐个落地");
            }
        }
        for (byte difficulty = 0; difficulty < 5; difficulty++)
        {
            var plan = policy.Extend(empty, star with { Difficulty = difficulty }, wave);
            That(plan.Generate(_ => 0, DateTimeOffset.UnixEpoch).Count(row => row.Item!.ItemId == HarlemAbyssBonusDropPolicy.SkyFragmentId)
                == rules.SkyFragments[difficulty], "星空五档苍穹碎片数量完整");
        }
        That(ultraPlan.Generate(_ => 0, DateTimeOffset.UnixEpoch).Count(row => row.Item!.ItemId == HarlemAbyssBonusDropPolicy.SkyFragmentId) == 10,
            "超星空固定难度苍穹碎片命中后掉10个；不把概率或其它奖励数量翻倍");
        foreach (var disabledRules in new[] { rules with { Enabled = false }, rules with { InvitationNumerator = 0, SoulPermille = 0,
            CrystalPermille = 0, SkyFragmentPermille = 0, StoneBagPermille = 0 } })
            That(ReferenceEquals(new HarlemAbyssBonusDropPolicy(disabledRules, epics, Item).Extend(empty, star, wave), empty),
                "全部关闭不添加骰、不消耗随机数");
        var tooLarge = new HarlemAbyssBonusDropPolicy(rules with { InvitationMaximumCount = 500 }, epics, Item);
        Reject<InvalidDataException>(() => tooLarge.Extend(empty, star, wave), "新奖励总对象容量超限必须在准备时拒绝");
        Reject<NotSupportedException>(() => new HarlemAbyssBonusDropPolicy(rules, epics, id => Item(id) with { Kind = ItemKind.Equipment }),
            "错误身份或非材料候选拒绝启动");
        Console.WriteLine($"PASS {Count - before} 深渊补全奖励概率、模式、逐个落地与容量断言");
    }
}
