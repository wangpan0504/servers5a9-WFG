using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network.Projection;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassProgressTests
{
    public static void RunCore()
    {
        int before = Count;
        var dynamic = Catalog([10, 30, 0]);
        Check(dynamic, 0, 1, 0);
        Check(dynamic, 9, 1, 9);
        Check(dynamic, 10, 2, 0);
        Check(dynamic, 39, 2, 29);
        Check(dynamic, 40, 3, 0);
        Check(dynamic, 41, 3, 0);
        Check(dynamic, long.MaxValue, 3, 0);
        Reject<ArgumentOutOfRangeException>(() => BattlePassProgress.FromTotalExperience(dynamic, -1),
            "战令显示拒绝负累计经验，不经无符号转换变成终级");
        var matchedCurve = Catalog(Enumerable.Repeat(1300, 79).Append(0).ToArray());
        Check(matchedCurve, 1299, 1, 1299);
        Check(matchedCurve, 1300, 2, 0);
        Check(matchedCurve, 102699, 79, 1299);
        Check(matchedCurve, 102700, 80, 0);
        Check(matchedCurve, 102701, 80, 0);
        Check(matchedCurve, long.MaxValue, 80, 0);

        // 合成三等级目录验证级内字段、原始type与本服保留策略；不是归档或实机通过。
        var state = new BattlePassSeasonState("projection-fixture", 39, 2,
            ImmutableHashSet<BattlePassRewardKey>.Empty.Add(new(0, 1)), 7);
        byte[] claims = state.ToClaimBytes();
        byte[] first = BattlePassProjection.PassInfo(dynamic, state);
        That(first.SequenceEqual(Convert.FromHexString("02000000020000001D000000")),
            "1800保留原始type2、显式000000、等级2及级内经验29");
        first[0] = 0xFF;
        That(BattlePassProjection.PassInfo(dynamic, state).SequenceEqual(Convert.FromHexString("02000000020000001D000000")) &&
            state.TotalExperience == 39 && state.PassType == 2 && state.Revision == 7 &&
            state.ToClaimBytes().SequenceEqual(claims), "编码拥有独立快照，不改总经验、资格、revision或领取事实");
        var terminal = state with { TotalExperience = long.MaxValue, PassType = 1 };
        That(BattlePassProjection.PassInfo(matchedCurve, terminal).SequenceEqual(Convert.FromHexString("010000005000000000000000")) &&
            terminal.TotalExperience == long.MaxValue, "终级80/0是本服显示策略，累计总经验不截断");
        Console.WriteLine($"PASS BattlePassProgressTests core ({Count - before} checks)");
    }

    static void Check(BattlePassCatalog catalog, long total, uint level, uint experience) =>
        That(BattlePassProgress.FromTotalExperience(catalog, total) == new BattlePassProgress(level, experience),
            $"累计经验{total}投影为{level}级内{experience}，使用目录动态阈值");

    static BattlePassCatalog Catalog(int[] requirements)
    {
        var source = new List<object>
        {
            "[ui path]", "UI/ProgressFixture.xui",
            "[mission]", 1, 1, 1, 0, "进度夹具", -1, "[/mission]",
            "[daily mission]", 1, "[/daily mission]", "[weekly mission]", 1, "[/weekly mission]",
            "[season mission]", 1, "[/season mission]"
        };
        for (int level = 1; level <= requirements.Length; level++)
            source.AddRange(new object[] { "[level reward]", level, 15, 1, 0, "[/level reward]" });
        source.Add("[exp table]");
        for (int level = 1; level <= requirements.Length; level++)
            source.AddRange(new object[] { level, requirements[level - 1] });
        source.AddRange(new object[] { "[/exp table]", "[tier table]", 1, requirements.Length, "[/tier table]",
            "[postal]", 1, 2, 3, 4, "[/postal]", "[package index]", 10, "[/package index]",
            "[certificate index]", 11, "[/certificate index]", "[buy restrict code]", 12, "[/buy restrict code]",
            "[golden mission exp ratio]", 3, "[cera consumption exp percentage]", 50 });
        var strings = new List<string>();
        PvfToken Token(object value)
        {
            if (value is int integer) return new(0, integer);
            string text = (string)value;
            strings.Add(text);
            return new(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }
        return BattlePassCatalog.Parse(source.Select(Token).ToArray(), index => strings[index]);
    }
}
