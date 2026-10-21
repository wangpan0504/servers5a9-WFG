using System.Collections.Immutable;
using DfoServer.Game.Adventure;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureProgressionTests
{
    // 合成资源：满级3、经验倍率0.3、等级边界2/3为10/20，勇者结晶5经验1点、上限250，胶囊每格100、最多2格。
    public static void RunCore()
    {
        int before = Count;
        var catalog = AdventureCoreTests.SyntheticCatalog();
        var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var initial = AdventureState.Initial("progress", now);
        decimal rate = catalog.ExperienceRate;
        That(rate > 0.29m && rate < 0.31m, "合成资源保留冒险团经验倍率");

        var advanced = AdventureProgression.Advance(initial, 1000, catalog);
        decimal scaled = 1000 * rate;
        ulong gained = (ulong)decimal.Floor(scaled);
        That(advanced.Level == 3 && advanced.Experience == 270,
            "溢出经验按倍率转换并逐级扣除边界，Lv3上限余270");
        That(advanced.Remainder == scaled - gained && advanced.Remainder >= 0 && advanced.Remainder < 1,
            "经验倍率的小数部分留在Remainder，不按整数截断丢失");
        That(advanced.BraveExperience == 0 && advanced.Points[0] == 200 && advanced.Points.Skip(1).All(value => value == 0),
            "勇者结晶按5经验1点兑换，点数只写入类别0");
        That(advanced.CapsuleExperience == 200, "胶囊进度按容量封顶");

        var carried = AdventureProgression.Advance(initial with { Remainder = 0.9m }, 1, catalog);
        That(carried.Level == 1 && carried.Experience == 1 && carried.BraveExperience == 1 && carried.Points[0] == 0 &&
            carried.CapsuleExperience == 1 &&
            carried.Remainder == rate + 0.9m - 1m, "跨结算携带的小数与不足一点的经验都保留到下次");

        var saturated = AdventureProgression.Advance(initial with { Points = [249, 0, 0, 0, 0], BraveExperience = 4 }, 12, catalog);
        That(saturated.BraveExperience == 1 && saturated.Points[0] == 250,
            "勇者结晶先兑换再按商店上限封顶，余数继续累计");

        var capsuleFull = AdventureProgression.Advance(initial with { CapsuleExperience = 150 }, 100, catalog);
        That(capsuleFull.CapsuleExperience == 200, "胶囊已有150时再灌100只补到容量200");

        var atMaximum = AdventureProgression.Advance(initial with { Level = 3, Experience = 15 }, 100, catalog);
        That(atMaximum.Level == 3 && atMaximum.Experience == 15 + (ulong)decimal.Floor(100 * rate),
            "满级后经验不再扣边界，直接累计到账号经验");

        That(ReferenceEquals(AdventureProgression.Advance(initial, 0, catalog), initial),
            "本单没有溢出经验时不改账号快照");

        var snapshot = new AdventureSnapshot(initial, ImmutableArray<AdventureMember>.Empty);
        var plan = AdventureProgression.Plan(snapshot, 1000, catalog, now);
        That(ReferenceEquals(plan.Before, snapshot) &&
            System.Text.Json.JsonSerializer.Serialize(plan.After) == System.Text.Json.JsonSerializer.Serialize(advanced) &&
            plan.MaximumLevelExperience == 1000,
            "规划同时保留提交前快照、按同一规则计算的提交后状态与溢出额度");

        That(AdventureProgression.MaximumLevelExperience(95, 0, 1000, 95, 1_000_000) == 1000,
            "角色已满级时整单经验都溢出");
        That(AdventureProgression.MaximumLevelExperience(94, 1_000_000 - 100, 1000, 95, 1_000_000) == 900,
            "跨过满级入口线时只取入口以上的差额");
        That(AdventureProgression.MaximumLevelExperience(94, 1_000_000 - 1000, 1000, 95, 1_000_000) == 0,
            "恰好停在上限入口线不产生溢出");
        That(AdventureProgression.MaximumLevelExperience(1, 0, 100, 95, 50) == 50,
            "低等级单笔超过入口线时同样只取差额");
        That(AdventureProgression.MaximumLevelExperience(95, 0, 0, 95, 1_000_000) == 0 &&
            AdventureProgression.MaximumLevelExperience(0, 0, 1000, 95, 1_000_000) == 0 &&
            AdventureProgression.MaximumLevelExperience(96, 0, 1000, 95, 1_000_000) == 0,
            "零经验与越界等级不产生溢出");

        That(AdventurePackets.Claims(advanced, catalog) == 2, "胶囊可领取次数不超过资源最大格数");
        That(AdventurePackets.Claims(advanced with { CapsuleExperience = 15 }, catalog) == 1,
            "胶囊按每格最小经验计算可领取次数");
        That(AdventurePackets.Claims(advanced with { CapsuleExperience = 5 }, catalog) == 0,
            "不足最小经验时不可领取");

        That(advanced.Revision == initial.Revision, "规划不伪造账号revision，由事务提交递增");
        Console.WriteLine($"PASS {Count - before} 冒险团溢出经验、结晶、胶囊与领取次数断言");
    }
}
