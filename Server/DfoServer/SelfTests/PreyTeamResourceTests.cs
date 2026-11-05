using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using System.Collections.Immutable;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyTeamResourceTests
{
    internal static void RunCore()
    {
        int before = Count;
        var strings = new List<string>();
        PvfToken[] Tokens(object[] values) => values.Select(value =>
        {
            if (value is PvfToken token) return token;
            if (value is int number) return new PvfToken(0, number);
            string text = (string)value;
            strings.Add(text);
            return new PvfToken(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }).ToArray();
        object[] symbols = [1, "[HP]", 2, "[FLAG]"];
        object[] phase =
        [
            "[PHASE]", "[PHASE NAME]", "普通阶段", "[MINIMAL DUNGEON CLEAR COUNT]", 2,
            "[CLEAR REWARD ITEM]", "[STATE REWARD]", "squad_item", -1, 4500, 100, 1, 7, "[/CLEAR REWARD ITEM]",
            "[DUNGEON INFO]", "[INDEX]", 42, "[TYPE]", "slenikon defence", "[HP INFO]", "[HP]", 9999,
            "[INIT STATE]", "open", "[PARTY MAX]", 1, "[FUTURE DISPLAY]", "retained", "[/DUNGEON INFO]",
            "[TRIGGER]", "[ON CLEAR DUNGEON]", 42, "[/TRIGGER]",
            "[BEHAVIOR]", "[SET TIMER]", 500, 42, 180, "[SET]", "[FLAG]", 1, "[/BEHAVIOR]",
            "[TRIGGER]", "[CHECK RAID TIMER END]", 1, 150, "[/TRIGGER]",
            "[BEHAVIOR]", "[CLEAR PHASE]", "[/BEHAVIOR]", "[/PHASE]"
        ];
        object[] header =
        [
            "[PHASE MAX]", 1, "[RAID MEMBER MAX]", 12, "[RAID START MINIMUM MEMBER]", 1,
            "[WAITING ROOM]", 59, 2, "[ENTRY COST]", 100, 1, "[/ENTRY COST]", "[RETRY ITEM]", 100, 1,
            "[SLENIKON]", "[MAX HP]", 10000, "[CHANGE ACTION LIST]",
            "[PHASE]", "[PHASE NAME]", "显示内层", "[/PHASE]", "[/CHANGE ACTION LIST]", "[/SLENIKON]",
            "[FUTURE DISPLAY]", new PvfToken(4, 123)
        ];
        object[] input = [.. header, .. phase, "[SINGLE RAID PHASE]", "[INDEX]", 777, "[/SINGLE RAID PHASE]"];
        RaidScript Parse(object[] values) => PreyTeamScript.Parse(Tokens(values), Tokens(symbols), id => strings[id]);
        var script = Parse(input);
        That(script.Issues.IsEmpty && script.Phases is [{ Name: "普通阶段" }], "普通阶段只取外层，忽略动画PHASE和single块");
        That(script.EntryCost == new RaidCost(100, 1) && script.RetryItem == new RaidCost(100, 1), "普通费用取普通字段且保持正数量");
        That(script.Phases[0].Dungeons is [{ Id: 42, PartyMax: 1, HpRaw: 9999 }], "普通地图声明按字段读取，不绑定已知ID清单");
        That(script.Phases[0].Rewards is [{ RawA: -1, RawB: 4500, ItemId: 100, RawD: 1, RawE: 7 }], "五整数奖励完整保留未知尾列");
        That(script.Phases[0].Rules[0].Behavior[0] is { Timer.Kind: 500, RawIntegers: [500, 42, 180] }, "计时器500组原值与三列保持");
        That(script.Phases[0].Rules[1].Trigger[0] is { Tag: "[check raid timer end]", Timer.Kind: 1, Timer.DungeonId: 150 }, "普通与raid计时命名由标签区分，不重编号原资源");
        That(script.Headers.Any(row => row.Tag == "[max hp]" && row.Values is [{ Number: 10000 }]), "船体块业务header仍可消费");

        var fourColumns = phase.ToList();
        fourColumns.RemoveAt(fourColumns.IndexOf(7));
        That(Parse([.. header, .. fourColumns]).Phases[0].Rewards[0].RawE is null, "四整数旧式奖励不补造第五整数");
        var missingCost = input.ToArray();
        missingCost[Array.IndexOf(missingCost, "[ENTRY COST]") + 1] = "bad";
        That(Parse(missingCost).Issues.Any(row => row.Code == "prey-header-invalid" && row.Detail == "[entry cost]"), "必需费用字段类型异常可定位");
        var wrongCount = input.ToArray();
        wrongCount[1] = 2;
        That(Parse(wrongCount).Issues.Any(row => row.Code == "prey-phase-count-invalid"), "按本资源阶段声明核对完整性，不误计显示内层");
        var unknownAction = input.ToArray();
        unknownAction[Array.IndexOf(unknownAction, "[CLEAR PHASE]")] = "[UNKNOWN GAMEPLAY ACTION]";
        That(Parse(unknownAction).Issues.Any(row => row.Code == "prey-clause-unsupported"), "未知业务行为不能默认为执行成功");
        var badReference = input.ToArray();
        badReference[Array.IndexOf(badReference, "[ON CLEAR DUNGEON]") + 1] = 43;
        That(Parse(badReference).Issues.Any(row => row.Code == "prey-dungeon-reference-missing"), "事件地图必须属于本阶段实际声明");
        That(Parse([.. header, .. phase.Take(phase.Length - 1)]).Issues.Any(row => row.Code == "prey-block-incomplete"), "截断阶段有界结束并报告缺失闭合");
        var first = script.Phases[0] with { Rewards = [script.Phases[0].Rewards[0] with { ItemId = 200 }] };
        var later = first with { Name = "后续阶段", Dungeons = [first.Dungeons[0] with { Id = 43 }] };
        var partialScript = script with { Phases = [first, later] };
        var available = new DungeonDefinition(42, "dungeon/prey/available.dgn", 1, 1, [], []);
        var entries = new RaidDungeonCatalog(partialScript,
            ImmutableDictionary<int, DungeonDefinition>.Empty.Add(42, available), [123], new Dictionary<int, string>(), _ => false, 3);
        var partial = new RaidResources(partialScript, [], [43], [123], [],
            [new("formal-map-file-missing", "map/prey/later.map", 0, "123")]) { EntryCatalog = entries };
        That(!partial.ResourcesReady && !entries.Ready && partial.PreyTeamStartReady,
            "后续阶段缺图保留诊断，不禁止普通普雷基础开战");
        That(entries.Find(42) == available && entries.Find(43) is null,
            "缺图只使对应副本不可入，已有目标定义保持可用");
        foreach (int missing in new[] { script.EntryCost!.ItemId, 200 })
            That(!(partial with { MissingItems = [missing] }).PreyTeamStartReady,
                "计费或奖励物品缺失仍不能启用普通开战");
        That(!(partial with { Script = Parse(unknownAction) }).PreyTeamStartReady &&
            !(partial with { Script = Parse(missingCost) }).PreyTeamStartReady &&
            !(partial with { EntryCatalog = null }).PreyTeamStartReady,
            "未知业务、必需费用类型或入图目录缺失仍阻止普通开战");
        Console.WriteLine($"PASS {Count - before} 普雷普通资源合成断言");
    }

    internal static void Run(PvfArchive archive)
    {
        int before = Count;
        var tokens = archive.ReadTokens(PreyTeamScript.Path);
        var symbols = archive.ReadTokens(PreyTeamScript.Path + ".symbol");
        var team = PreyTeamScript.Parse(tokens, symbols, archive.ResolveString);
        var guide = PreyGuideScript.Parse(tokens, symbols, archive.ResolveString);
        That(team.Issues.IsEmpty, "原版普通普雷脚本可读取：" + string.Join(';', team.Issues));
        That(guide.Issues.IsEmpty && team.EntryCost != guide.EntryCost, "普通与引导各自使用原费用声明");
        That(team.Phases.SelectMany(row => row.Dungeons).All(dungeon =>
            !guide.Phases.SelectMany(row => row.Dungeons).Any(other => other.Id == dungeon.Id)), "普通与引导地图保持独立");
        That(team.Phases.Length == team.Headers.Single(row => row.Tag == "[phase max]").Values[0].Number, "原版阶段数与自身声明一致");
        That(team.Phases.SelectMany(row => row.Rules).SelectMany(row => row.Behavior).Any(row => row.Timer?.Kind == 500), "原版500计时器可直接消费");
        That(team.Phases.SelectMany(row => row.Rewards).Any(row => row.RawE is not null), "原版奖励尾字段保留");
        PreyTeamBattleTests.RunSpawnReservations(team);
        Console.WriteLine($"PASS {Count - before} 普雷普通原版资源断言");
    }

    internal static void RunRewards(PvfArchive archive, ItemResources items)
    {
        int before = Count;
        var tokens = archive.ReadTokens(PreyTeamScript.Path);
        var symbols = archive.ReadTokens(PreyTeamScript.Path + ".symbol");
        var script = PreyTeamScript.Parse(tokens, symbols, archive.ResolveString);
        var rewards = new PreyGuideRewards(script, items.Items, items.Boosters);
        var plain = rewards.CreateTeam(false, false, _ => 0);
        var entitled = rewards.CreateTeam(true, true, maximum => maximum - 1);
        That(plain.Source == PreyGuideRewards.TeamSource && plain.Source != PreyGuideRewards.Source &&
            plain.Phases.All(phase => phase.Rewards.Source == plain.Source), "普通奖励来源贯穿阶段且与引导隔离");
        That(plain.WeeklyClearLimit == script.Headers.Single(row => row.Tag == "[weekly clear count]").Values[0].Number &&
            plain.Phases.Select(phase => phase.MinimumCommittedRuns).SequenceEqual(script.Phases.Select(phase =>
                phase.Declarations.Single(row => row.Tag == "[minimal dungeon clear count]").Values[0].Number)), "周上限和各阶段最低参与次数取实际资源");
        var containers = new RaidRewardContainers(script, items.Items, items.Boosters);
        for (int index = 0; index < script.Phases.Length; index++)
        {
            var phase = script.Phases[index];
            var first = phase.Rewards.First(row => row.Kind == "squad_item" && row.RawB > 0);
            var last = phase.Rewards.Last(row => row.Kind == "squad_item" && row.RawB > 0);
            That(plain.Phases[index].Rewards.Offers.Single(row => row.Kind == 0).Grants.SequenceEqual(containers.ResolveAll(first.ItemId, _ => 0)) &&
                entitled.Phases[index].Rewards.Offers.Single(row => row.Kind == 0).Grants.SequenceEqual(containers.ResolveAll(last.ItemId, maximum => maximum - 1)),
                "外层权重首尾边界决定本成员冻结产物，箱内沿原解释器解析");
            That(plain.Phases[index].Rewards.Offers.Single(row => row.Kind == 1).Grants.Length == phase.Rewards.Count(row => row.Kind == "party_card") &&
                entitled.Phases[index].Rewards.Offers.Single(row => row.Kind == 1).Grants.Length == phase.Rewards.Count(row => row.Kind != "squad_item"),
                "网吧与礼遇奖励只进入有资格成员的固定牌");
            That(plain.Phases[index].Rewards.Offers.All(PreyGuideRewards.ValidDisplay) && entitled.Phases[index].Rewards.Offers.All(PreyGuideRewards.ValidDisplay),
                "普通600展示逐条匹配已冻结实际产物");
        }
        That(plain.Phases[0].Rewards.Offers.Single(row => row.Kind == 1).Grants.SequenceEqual([new InventoryReward(10308807, 50)]) &&
            plain.Phases[1].Rewards.Offers.Single(row => row.Kind == 1).Grants.SequenceEqual([new InventoryReward(10308807, 150)]),
            "普通两阶段不渝之咏按实际配方50/150发放，不把名称中的200当数量");
        foreach (var (container, count) in new[] { (10308706, 11), (10308707, 12), (10308708, 13), (10308709, 24) })
            That(containers.ResolveAll(container, _ => 0).SequenceEqual([new InventoryReward(10308559, count)]),
                "黑瞳石数量取实际配方而非名称或STATE REWARD尾列：" + container);
        That(containers.ResolveAll(10308710, _ => 0).SequenceEqual(
            [new InventoryReward(10308559, 14), new InventoryReward(10308690, 20)]),
            "多池礼包全部展开，黑瞳石14与闪耀20独立保留");
        That(containers.ResolveAll(10308713, _ => 0).SequenceEqual(
            [new InventoryReward(10308559, 11), new InventoryReward(10308715, 1)]),
            "奖励根盒只展开一次，子级武器礼盒作为真实产物保留");
        foreach (var (roll, count) in new[] { (0, 2), (8599, 2), (8600, 3), (9399, 3), (9400, 4), (9999, 4) })
            That(containers.ResolveAll(10309048, _ => roll).SequenceEqual([new InventoryReward(10308559, count)]),
                "网吧袖珍罐保留同ID不同数量的原权重边界：" + roll);
        That(containers.ResolveAll(490705074, _ => 0).SequenceEqual([new InventoryReward(10308559, 2)]),
            "礼遇legacy仍取原数量2，与booster展开共用最终产物合同");
        Reject<InvalidDataException>(() => containers.ResolveAll(10309048, bound => bound), "箱内随机上界拒绝冻结");
        var changed = script with
        {
            Phases = script.Phases.Select(phase => phase with
            {
                Rewards = phase.Rewards.Select(row => row with { RawA = 0, RawD = 0, RawE = 0 }).ToImmutableArray()
            }).ToImmutableArray()
        };
        That(new PreyGuideRewards(changed, items.Items, items.Boosters).CreateTeam(false, false, _ => 0).Phases.Length == plain.Phases.Length,
            "未消费的原始业务列为零不阻止已有权重及产物合同");
        var zeroWeight = script with
        {
            Phases = script.Phases.Select(phase => phase with
            {
                Rewards = phase.Rewards.Select(row => row == phase.Rewards.First(item => item.Kind == "squad_item") ? row with { RawB = 0 } : row).ToImmutableArray()
            }).ToImmutableArray()
        };
        var zero = new PreyGuideRewards(zeroWeight, items.Items, items.Boosters).CreateTeam(false, false, _ => 0);
        That(zero.Phases.Select((phase, index) => phase.Rewards.Offers.Single(row => row.Kind == 0).Grants.SequenceEqual(
            containers.ResolveAll(zeroWeight.Phases[index].Rewards.First(row => row.Kind == "squad_item" && row.RawB > 0).ItemId, _ => 0))).All(value => value),
            "零权重条目保留但不参与抽中，不因其存在硬拒整个奖励配置");
        var guide = PreyGuideScript.Parse(tokens, symbols, archive.ResolveString);
        That(new PreyGuideRewards(guide, items.Items, items.Boosters).Create(false, _ => 0).Source == PreyGuideRewards.Source,
            "原引导入口保留原来源与奖励解释");
        Console.WriteLine($"PASS {Count - before} 普雷普通奖励资源断言");
    }
}
