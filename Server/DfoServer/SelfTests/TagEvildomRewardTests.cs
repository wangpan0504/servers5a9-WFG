using System.Collections.Immutable;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TagEvildomRewardTests
{
    internal static void Run()
    {
        var parsed = BoosterCatalog.Parse("[stackable type] `[booster]` 0 [booster info] [etc] 1 100 1000 81 [/etc] [etc] 1 200 1000 81 [/etc] [/booster info]");
        var items = TagEvildomRewardCatalog.Expand(parsed);
        That(items.SequenceEqual(new TagEvildomRewardItem[] { new(100, 81), new(200, 81) }),
            "复用真实booster解析展开两个确定产物，不把外层结算定义当箱子");
        var table = Make([500, 9000, 501, 1000], _ => parsed);
        foreach (int roll in new[] { 0, 8999, 9000, 9999 })
        {
            int draws = 0;
            var reward = TagEvildomRewardPolicy.Prepare(table, 9608, upper =>
            { That(upper == 10000, "使用资源万分权重"); draws++; return roll; });
            That(draws == 1 && reward.DefinitionId == (roll < 9000 ? 500 : 501) && reward.Items.SequenceEqual(items),
                "一次通关只选一份，保留定义及全部实际产物");
        }
        That(TagEvildomRewardPolicy.Prepare(table, 400000095, _ => 9999).DefinitionId == 600,
            "简单模式使用独立奖池");
        Reject<InvalidDataException>(() => TagEvildomRewardPolicy.Prepare(table, 9609, _ => throw new Exception()),
            "其它副本在随机前拒绝，不能借剧情图套取奖励");
        foreach (int roll in new[] { -1, 10000 })
            Reject<InvalidDataException>(() => TagEvildomRewardPolicy.Prepare(table, 9608, _ => roll), "越界随机不回空奖成功");
        foreach (int[] malformed in new[] { Array.Empty<int>(), [500], [500, 0], [500, -1], [500, 9999], [500, 10001], [500, 5000, 500, 5000] })
            Reject<InvalidDataException>(() => Make(malformed, _ => parsed), "坏权重表及重复定义整表拒绝");
        foreach (var malformed in new[]
        {
            parsed with { PendingRule = "unknown" }, parsed with { Lottery = true },
            parsed with { FirstGrowTypes = [1] }, parsed with { UnsealsRandomOptions = true },
            parsed with { DailyRetainedLimit = 1 }, parsed with { Pools = [] },
            parsed with { Pools = [new(2, [new(100, 1000, 1)])] },
            parsed with { Pools = [new(1, [new(100, 500, 1), new(200, 500, 1)])] },
            parsed with { Pools = [new(1, [new(100, 0, 1)])] },
            parsed with { Pools = [new(1, [new(100, 1000, 1)]) { MinimumLevel = 95 }] },
            parsed with { Pools = [new(1, [new(100, 1000, 1)]), new(1, [new(100, 1000, 2)])] }
        }) Reject<InvalidDataException>(() => Make([500, 10000], _ => malformed), "未支持附加规则或随机子池不得部分展开发奖");
        Reject<InvalidDataException>(() => Make([500, 10000], _ => parsed, weekly: 4), "周限制变化不能突破现有三队保护合同");
    }

    static TagEvildomRewardCatalog Make(int[] hard, Func<int, BoosterDefinition> definition, int weekly = 3)
    {
        var strings = new List<string>();
        var tokens = new List<PvfToken>();
        void Tag(string text) { strings.Add(text); tokens.Add(new(3, strings.Count - 1)); }
        void Number(int value) => tokens.Add(new(0, value));
        Tag("[weekly reward]"); Number(weekly);
        Tag("[clear reward]"); foreach (int value in hard) Number(value); Tag("[/clear reward]");
        Tag("[clear reward easy mode]"); Number(600); Number(10000); Tag("[/clear reward easy mode]");
        return new(tokens, index => strings[index], definition);
    }
}
