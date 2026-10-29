using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class GuildNpcEmblemPoolTests
{
    public static void RunCore()
    {
        int before = Count;
        const string mapping = """
            [guild npc emblem compound info]
            1 1 3 9002 9003
            2 4 2 9001
            [/guild npc emblem compound info]
            """;
        var mappings = EmblemCompoundCatalog.ParseGuildMappings(mapping);
        That(mappings.Count == 3 && mappings[(1, 1, 2)] == 9002 && mappings[(1, 1, 3)] == 9003 &&
            mappings[(2, 4, 2)] == 9001,
            "公会徽章池按NPC等级、材料grade和实际件数展开变长行");
        foreach (string invalid in new[]
        {
            "", "1 1", "1 1 3 9001", "0 1 2 9001", "1 0 2 9001", "1 1 6 9001 9002 9003 9004 9005",
            "1 1 2 0", "1 1 2 9001 1 1 2 9002"
        })
            Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParseGuildMappings(
                "[guild npc emblem compound info]\n" + invalid + "\n[/guild npc emblem compound info]"),
                "公会徽章映射拒绝空表、截断、越界、无效booster和重复键");
        Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParseGuildMappings(
            "[guild npc emblem compound info]\n1 1 2 9001"), "公会徽章池段必须闭合");
        Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParseGuildMappings(mapping + mapping),
            "公会徽章池拒绝重复整段");

        var stackable = new Dictionary<int, string>
        {
            [9001] = "normal.stk", [9002] = "guild.stk", [9003] = "guild-three.stk",
            [2001] = "reward-normal.stk", [2002] = "reward-guild.stk"
        };
        var texts = new Dictionary<string, string>
        {
            [EmblemCompoundCatalog.RootPath] = "[emblem compound info]\n1 2 9001\n[/emblem compound info]",
            ["stackable/normal.stk"] = Pool("2001 2 1"),
            ["stackable/guild.stk"] = Pool("2002 0.25 1 2001 0.75 1"),
            ["stackable/guild-three.stk"] = Pool("2002 1 1"),
            ["stackable/reward-normal.stk"] = "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n2",
            ["stackable/reward-guild.stk"] = "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n3"
        };
        var reads = new Dictionary<string, int>();
        string Read(string path)
        {
            reads[path] = reads.GetValueOrDefault(path) + 1;
            return texts[path];
        }
        var catalog = new EmblemCompoundCatalog(Read, stackable, mapping);
        That(catalog.For(1, 2).BoosterItemId == 9001 && catalog.For(1, 2).Rewards is
            [{ ItemId: 2001, Weight: 2, Rarity: 2 }], "加载公会专用池保持普通池及其原始权重");
        That(catalog.ForGuild(1, 1, 2) is { BoosterItemId: 9002, Rewards.Length: 2 } guild &&
            guild.Rewards[0] is { ItemId: 2002, Weight: 25, Count: 1, Rarity: 3 } &&
            guild.Rewards[1] is { ItemId: 2001, Weight: 75, Count: 1, Rarity: 2 } &&
            catalog.ForGuild(1, 1, 3)?.BoosterItemId == 9003,
            "公会专用池复用实际booster解析与小数权重，不把奖励概率当成功率相加");
        That(ReferenceEquals(catalog.ForGuild(2, 4, 2), catalog.For(1, 2)) &&
            reads["stackable/normal.stk"] == 1 && reads["stackable/reward-normal.stk"] == 1,
            "公会与普通池引用相同booster时共享池和奖励元数据读取");
        That(catalog.ForGuild(3, 1, 2) is null && catalog.ForGuild(1, 2, 2) is null &&
            catalog.ForGuild(1, 1, 4) is null && catalog.ForGuild(1, 1, 1) is null,
            "缺少精确公会等级、grade或件数映射时返回空，不回退到其他公会池");
        var legacy = new EmblemCompoundCatalog(path => texts[path], stackable);
        That(legacy.For(1, 2).BoosterItemId == 9001 && legacy.ForGuild(1, 1, 2) is null,
            "旧合成夹具没有公会段时仍可构造，不额外读取不存在的guildreform");
        stackable.Remove(9002);
        Reject<InvalidDataException>(() => new EmblemCompoundCatalog(path => texts[path], stackable, mapping),
            "公会专用池的booster缺失明确拒绝，不默用普通池");
        stackable[9002] = "guild.stk";
        stackable[9004] = "nested.stk";
        texts["stackable/nested.stk"] = Nested("9001 1 1 9002 1 1");
        var nested = EmblemCompoundCatalog.ParsePool(texts["stackable/nested.stk"], 9004, path => texts[path], stackable);
        That(nested.Length == 3 && nested[0] is { ItemId: 2001, Weight: 4 } &&
            nested[1] is { ItemId: 2002, Weight: 1 } && nested[2] is { ItemId: 2001, Weight: 3 },
            "嵌套子池总权重2与100时仍按父池各一半展开，精确整数权重为4比1比3");
        stackable[9005] = "cycle.stk";
        texts["stackable/nested.stk"] = Nested("9005 1 1");
        texts["stackable/cycle.stk"] = Nested("9004 1 1");
        Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParsePool(texts["stackable/nested.stk"], 9004,
            path => texts[path], stackable), "嵌套booster循环明确拒绝，不无限递归或回退普通池");
        texts["stackable/nested.stk"] = Nested("9001 1 2");
        Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParsePool(texts["stackable/nested.stk"], 9004,
            path => texts[path], stackable), "单结果徽章合成不能打开两份子booster");

        stackable[9006] = "jobs.stk"; stackable[9007] = "job-root.stk";
        stackable[2003] = "thief.stk"; stackable[2004] = "fighter.stk"; stackable[2005] = "fighter-other.stk";
        texts["stackable/thief.stk"] = Platinum("thief");
        texts["stackable/fighter.stk"] = texts["stackable/fighter-other.stk"] = Platinum("fighter");
        texts["stackable/jobs.stk"] = """
            [stackable type]
            `[booster]` 0
            [booster info]
            [charactor]
            `[thief]` 1 99 1 2003 2 1
            [/charactor]
            [charactor]
            `[fighter]` 1 99 1 2004 3 1 2005 3 1
            [/charactor]
            [/booster info]
            """;
        texts["stackable/job-root.stk"] = Nested("9001 4 1 9006 6 1");
        var jobPool = EmblemCompoundCatalog.ParsePool(texts["stackable/job-root.stk"], 9007, path => texts[path], stackable);
        That(jobPool.Where(row => row.Rarity == 6).Sum(row => row.Weight) * 5 == jobPool.Sum(row => row.Weight) * 3 &&
            jobPool.Count(row => row.PlatinumJobs.Contains(6)) == 1 && jobPool.Count(row => row.PlatinumJobs.Contains(1)) == 2 &&
            jobPool.Where(row => row.PlatinumJobs.Contains(1)).Select(row => row.Weight).Distinct().Count() == 1,
            "白金父分支保持六成概率，职业子表数量不同时仍保留各职业自己的候选及条件权重");
        Console.WriteLine($"PASS {Count - before} guild NPC emblem pool assertions");
    }

    public static void RunResources(ItemResources resources)
    {
        var catalog = resources.EmblemCompounds;
        var pools = new List<EmblemCompoundPool>();
        for (int level = 1; level <= 4; level++)
            for (int grade = 1; grade <= 4; grade++)
                for (int count = 2; count <= (grade == 4 ? 2 : 5); count++)
                {
                    var pool = catalog.ForGuild(level, grade, count);
                    That(pool is not null && !pool.Rewards.IsDefaultOrEmpty,
                        "正式公会徽章资源提供四级NPC、四档材料及其全部合法件数奖池");
                    pools.Add(pool!);
                }
        That(pools.Count == 52 && catalog.ForGuild(1, 1, 2)?.BoosterItemId == 490704924 &&
            catalog.ForGuild(4, 2, 5)?.BoosterItemId == 490704955 &&
            Enumerable.Range(1, 4).All(level => catalog.ForGuild(level, 3, 2)?.BoosterItemId == 490701286 &&
                catalog.ForGuild(level, 4, 2)?.BoosterItemId == 490701290) &&
            catalog.ForGuild(5, 1, 2) is null && catalog.ForGuild(1, 4, 3) is null,
            "正式公会池精确保留52映射及高档共享booster，不造第五级或额外白金件数");
        var expanded = catalog.ForGuild(2, 1, 3)!.Rewards;
        That(expanded.Length == 72 && expanded.Take(23).All(row => row.Weight == 55056) &&
            expanded.Skip(23).Take(12).All(row => row.Weight == 33189) && expanded.Skip(35).All(row => row.Weight == 1012) &&
            expanded.Sum(row => row.Weight) == 1702000,
            "正式490704933的三个不同总权重子池精确展开为74.4%、23.4%、2.2%，不按子池候选数量改变概率");
        foreach (var reward in pools.DistinctBy(pool => pool.BoosterItemId).SelectMany(pool => pool.Rewards).DistinctBy(row => row.ItemId))
        {
            var item = resources.Items.Get(reward.ItemId);
            That(item.Kind == ItemKind.Emblem && item.Rarity == reward.Rarity && reward.Weight > 0 && reward.Count > 0,
                "正式公会池的实际候选全部是有效正权重徽章，沿用实际稀有度");
        }
    }

    static string Pool(string rewards) => "[stackable type]\n`[booster]` 0\n[booster info]\n[stackable]\n1 " + rewards + "\n[/stackable]\n[/booster info]";
    static string Nested(string rewards) => "[stackable type]\n`[booster]` 0\n[booster info]\n[booster]\n1 " + rewards + "\n[/booster]\n[/booster info]";
    static string Platinum(string job) => "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n6\n[enchant]\n[skill levelup]\n`[" + job + "]` 1 1\n[/skill levelup]\n[/enchant]";
}
