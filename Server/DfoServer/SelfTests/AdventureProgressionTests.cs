using DfoServer.Game.Adventure;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureProgressionTests
{
    // 合成资源：满级3、经验倍率0.3、等级边界2/3为10/20，勇者结晶5经验1点、上限250，胶囊每颗100、最多2颗，总容量200。
    public static void RunCore()
    {
        int before = Count;
        var catalog = AdventureCoreTests.SyntheticCatalog();
        var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var initial = AdventureState.Initial("progress", now);
        decimal rate = catalog.ExperienceRate;
        That(rate > 0.29m && rate < 0.31m, "合成资源保留冒险团经验倍率");

        // Advance 只接收已折算的精确增量与同段原始溢出；折算本身由 AdventureCredit 统一负责。
        var advanced = AdventureProgression.Advance(initial, 1000 * rate, 1000, catalog);
        decimal scaled = 1000 * rate;
        ulong gained = (ulong)decimal.Floor(scaled);
        That(advanced.Level == 3 && advanced.Experience == 270,
            "溢出经验按倍率转换并逐级扣除边界，Lv3上限余270");
        That(advanced.Remainder == scaled - gained && advanced.Remainder >= 0 && advanced.Remainder < 1,
            "经验倍率的小数部分留在Remainder，不按整数截断丢失");
        That(advanced.BraveExperience == 0 && advanced.Points[0] == 200 && advanced.Points.Skip(1).All(value => value == 0),
            "勇者结晶按5经验1点兑换，点数只写入类别0");
        That(advanced.CapsuleExperience == 200, "胶囊按单颗经验乘最大颗数封顶，允许累计多颗");

        var carried = AdventureProgression.Advance(initial with { Remainder = 0.9m }, rate, 1, catalog);
        That(carried.Level == 1 && carried.Experience == 1 && carried.BraveExperience == 1 && carried.Points[0] == 0 &&
            carried.CapsuleExperience == 1 &&
            carried.Remainder == rate + 0.9m - 1m, "跨结算携带的小数与不足一点的经验都保留到下次");

        var saturated = AdventureProgression.Advance(initial with { Points = [249, 0, 0, 0, 0], BraveExperience = 4 }, 12 * rate, 12, catalog);
        That(saturated.BraveExperience == 1 && saturated.Points[0] == 250,
            "勇者结晶先兑换再按商店上限封顶，余数继续累计");

        var capsuleFull = AdventureProgression.Advance(initial with { CapsuleExperience = 80 }, 100 * rate, 100, catalog);
        That(capsuleFull.CapsuleExperience == 180 && AdventurePackets.Claims(capsuleFull, catalog) == 1, "胶囊已有80再灌100，保留一整颗及80尾数");

        // 改版公告：累积的成长胶囊经验每周四06:00初始化；资源锚点为2018-02-22 06:00（周四）。
        var anchorWeek = AdventurePeriod.CapsuleWeek(catalog, new DateTimeOffset(2018, 2, 22, 6, 0, 0, TimeSpan.FromHours(8)));
        That(AdventurePeriod.CapsuleWeek(catalog, new DateTimeOffset(2018, 2, 22, 5, 59, 0, TimeSpan.FromHours(8))) == 0 &&
            anchorWeek == 20180222 &&
            AdventurePeriod.CapsuleWeek(catalog, new DateTimeOffset(2018, 2, 28, 23, 0, 0, TimeSpan.FromHours(8))) == anchorWeek &&
            AdventurePeriod.CapsuleWeek(catalog, new DateTimeOffset(2018, 3, 1, 6, 0, 0, TimeSpan.FromHours(8))) == 20180301,
            "胶囊周期以资源锚点为每周四06:00边界，边界内不变");
        var sameWeek = AdventurePeriod.Apply(initial with { CapsuleExperience = 40, CapsuleWeek = anchorWeek }, catalog,
            new DateTimeOffset(2018, 2, 28, 12, 0, 0, TimeSpan.FromHours(8)));
        var nextWeek = AdventurePeriod.Apply(initial with { CapsuleExperience = 40, CapsuleWeek = anchorWeek }, catalog,
            new DateTimeOffset(2018, 3, 1, 6, 0, 0, TimeSpan.FromHours(8)));
        That(sameWeek.CapsuleExperience == 40 && nextWeek.CapsuleExperience == 0 && nextWeek.CapsuleWeek == 20180301,
            "同周期载入保留胶囊进度，跨周四06:00载入清空并记录新周期");
        That(AdventurePeriod.Apply(initial with { CapsuleExperience = 250, CapsuleWeek = AdventurePeriod.CapsuleWeek(catalog, now) },
            catalog, now).CapsuleExperience == 200, "超容量旧存量截回两颗容量，合法多颗不截断");
        var overCap = AdventurePeriod.Apply(initial with { CapsuleWeek = AdventurePeriod.CapsuleWeek(catalog, now),
            Points = [300, 12000, 0, 0, 0] }, catalog, now);
        That(overCap.Points.SequenceEqual([250u, 9999u, 0u, 0u, 0u]), "旧版超上限结晶在载入时按各商店上限截回");
        var withinCap = initial with { CapsuleWeek = AdventurePeriod.CapsuleWeek(catalog, now), Points = [250, 9999, 0, 0, 0] };
        That(ReferenceEquals(AdventurePeriod.Apply(withinCap, catalog, now), withinCap), "已达上限的结晶载入不产生新快照");

        var atMaximum = AdventureProgression.Advance(initial with { Level = 3, Experience = 15 }, 100 * rate, 100, catalog);
        That(atMaximum.Level == 3 && atMaximum.Experience == 15 + (ulong)decimal.Floor(100 * rate),
            "满级后经验不再扣边界，直接累计到账号经验");

        That(ReferenceEquals(AdventureProgression.Advance(initial, 0, 0, catalog), initial),
            "本单没有计入经验与溢出时不改账号快照");
        That(advanced.Revision == initial.Revision, "规划不伪造账号revision，由事务提交递增");

        // 精确总量恒等式：T = 各等级边界之和 + 当前级经验 + 小数余量。
        var exactState = initial with { Level = 3, Experience = 15, Remainder = 0.25m };
        decimal exact = AdventureCredit.ExactTotal(exactState, catalog);
        That(exact == 45.25m, "精确总量由各等级边界、当前级经验与小数余量组成");
        var restored = AdventureCredit.FromExactTotal(exact, initial, catalog);
        That(restored.Level == 3 && restored.Experience == 15 && restored.Remainder == 0.25m,
            "精确总量可无损还原冒险团等级、当前级经验与余量");

        // 国服95折算：入口线以内100%，超出部分按资源倍率；原始溢出另计勇者结晶与成长胶囊。
        That(AdventureCredit.Value(900, 1000, rate) == 900 && AdventureCredit.Value(1000, 1000, rate) == 1000 &&
            AdventureCredit.Value(1500, 1000, rate) == 1000 + 500 * rate &&
            AdventureCredit.Overflow(900, 1000) == 0 && AdventureCredit.Overflow(1500, 1000) == 500,
            "精确总量按95级入口线全额、超出部分按资源倍率折算");

        var earned = AdventureCredit.Apply(initial, AdventureCreditSource.Earned, 0, 0, 0, 1000, 1000, catalog, 50, 50);
        That(earned.PositionAfter == 1000 && earned.Credit == 1000 && earned.RawOverflow == 0 && earned.Reconciliation == 0 &&
            earned.After.Level == 3 && earned.After.Experience == 970,
            "入口线以内的击杀与结算经验按100%计入冒险团");

        var baselineOverflow = initial with { Level = 3, Experience = 900 };
        var overflow = AdventureCredit.Apply(baselineOverflow, AdventureCreditSource.Earned, 1000, 1000, 1000, 500, 1000, catalog, 95, 95);
        That(overflow.PositionAfter == 1500 && overflow.Credit == 150 && overflow.RawOverflow == 500 &&
            overflow.After.Experience == 1050 && overflow.After.BraveExperience == 0 &&
            overflow.After.Points[0] == 100 && overflow.After.CapsuleExperience == 200,
            "入口线以上每500点只按倍率计入150，原始溢出照常累计结晶与胶囊");

        var grant = AdventureCredit.Apply(baselineOverflow, AdventureCreditSource.ExcludedGrant, 1000, 1000, 1000, 500, 1000, catalog, 95, 95);
        That(grant.PositionAfter == 1500 && grant.Credit == 150 && grant.After.ExcludedExperience == 150 &&
            grant.After.Experience == 900 && grant.After.Points.SequenceEqual(baselineOverflow.Points) &&
            grant.After.CapsuleExperience == 0 && grant.RawOverflow == 0,
            "经验胶囊只推进里程并记录排除额，不改变账号进度、结晶或胶囊");

        var rewrite = AdventureCredit.Apply(initial, AdventureCreditSource.ExcludedState, 0, 0, 0, 750, 1000, catalog, 50, 60);
        That(rewrite.PositionAfter == 750 && rewrite.Credit == 750 && rewrite.After.ExcludedExperience == 750 &&
            rewrite.After.Level == initial.Level && rewrite.After.Experience == initial.Experience,
            "升级券等状态来源按绝对里程改写，排除额吸收差额");

        var reset = AdventureCredit.Apply(baselineOverflow, AdventureCreditSource.ExcludedState, 5000, 5000, 5000, 0, 1000, catalog, 60, 50);
        That(reset.PositionAfter == 0 && reset.Credit == -2200 && reset.After.ExcludedExperience == -2200 &&
            reset.After.Level == 3 && reset.After.Experience == 900,
            "GM改回低等级时排除额可为负，账号总量按差额扣回");

        var reconciled = AdventureCredit.Apply(initial, AdventureCreditSource.Earned, 0, 0, 1000, 100, 1000, catalog, 50, 50);
        That(reconciled.Reconciliation == 1000 && reconciled.PositionBefore == 1000 && reconciled.PositionAfter == 1100 &&
            reconciled.After.ExcludedExperience == 1000 && reconciled.Credit == 30 && reconciled.After.Level == 3 &&
            reconciled.After.Experience == 0,
            "账本里程落后于角色有效位置时，差额按排除调整补回，再计入本次经验");

        var pinned = AdventureCredit.Apply(baselineOverflow, AdventureCreditSource.Earned, 1100, 1000, 1000, 50, 1000, catalog, 95, 95);
        That(pinned.Reconciliation == 0 && pinned.PositionBefore == 1100 && pinned.PositionAfter == 1150 &&
            pinned.Credit == 15 && pinned.RawOverflow == 50 && pinned.After.Experience == 915,
            "满级经验条钉在入口线时不触发修正，里程按账本继续推进");

        // 40级门槛：40级以下只推进里程，基准不动；自然升级跨过40级时从基准起算，把40级前的自然经验一次计入。
        var below = AdventureCredit.Apply(initial, AdventureCreditSource.Earned, 0, 0, 0, 1000, 1000, catalog, 39, 39);
        That(below.PositionAfter == 1000 && below.Credit == 0 && below.BaselineAfter == 0 &&
            ReferenceEquals(below.After, initial) && below.RawOverflow == 0,
            "40级以下角色的击杀与结算经验只推进里程，不计入冒险团");
        var crossing = AdventureCredit.Apply(initial, AdventureCreditSource.Earned, 0, 0, 0, 1000, 1000, catalog, 39, 40);
        That(crossing.PositionAfter == 1000 && crossing.BaselineAfter == 1000 && crossing.Credit == 1000 &&
            crossing.After.Level == 3 && crossing.After.Experience == 970,
            "自然升级跨过40级时把40级前的自然经验连同本笔一次计入");
        var grantBelow = AdventureCredit.Apply(initial, AdventureCreditSource.ExcludedGrant, 0, 0, 0, 400, 1000, catalog, 39, 39);
        That(grantBelow.Credit == 0 && grantBelow.PositionAfter == 400 && grantBelow.BaselineAfter == 400 &&
            grantBelow.After.ExcludedExperience == 0 && grantBelow.After.Level == initial.Level,
            "40级以下的经验道具只抬高基准，保留待追溯的自然经验");
        var grantCrossing = AdventureCredit.Apply(initial, AdventureCreditSource.ExcludedGrant, 400, 400, 400, 600, 1000, catalog, 39, 40);
        That(grantCrossing.Credit == 600 && grantCrossing.PositionAfter == 1000 && grantCrossing.BaselineAfter == 1000 &&
            grantCrossing.After.ExcludedExperience == 600 && grantCrossing.After.Level == initial.Level,
            "经验道具把角色推过40级时不追溯历史，整笔只记排除额");
        var earnedAfterGrant = AdventureCredit.Apply(initial, AdventureCreditSource.Earned, 1000, 1000, 1000, 500, 1000, catalog, 40, 40);
        That(earnedAfterGrant.Credit == 150 && earnedAfterGrant.After.Experience == 120 &&
            earnedAfterGrant.BaselineAfter == 1500, "经验道具跨过40级后只累计之后的自然经验");
        var insta = AdventureCredit.Apply(initial, AdventureCreditSource.ExcludedState, 0, 0, 0, 1000, 1000, catalog, 39, 50);
        That(insta.Credit == 0 && insta.BaselineAfter == 1000 && insta.PositionAfter == 1000 &&
            insta.After.ExcludedExperience == 0, "直升类跨越40级直接改写基准，历史经验作废");
        var shift = AdventureCredit.Apply(initial, AdventureCreditSource.ExcludedState, 500, 400, 500, 200, 1000, catalog, 39, 30);
        That(shift.Credit == 0 && shift.PositionAfter == 200 && shift.BaselineAfter == 100,
            "40级以下改写位置时基准随差额平移，待追溯自然经验保持不变");

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
        That(AdventurePackets.Claims(advanced with { CapsuleExperience = 150 }, catalog) == 1,
            "胶囊按单颗max exp计算可领取次数");
        That(AdventurePackets.Claims(advanced with { CapsuleExperience = 99 }, catalog) == 0,
            "不足完整一颗时不可领取");

        Console.WriteLine($"PASS {Count - before} 冒险团折算、排除额、里程修正、结晶、胶囊与领取次数断言");
    }
}
