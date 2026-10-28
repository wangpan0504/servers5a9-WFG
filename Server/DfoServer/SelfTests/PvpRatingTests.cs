using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Pvp;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpRatingTests
{
    public static void RunCore()
    {
        int beforeCount = Count;
        var rules = Fixture();
        var calculator = new PvpRatingCalculator(rules);
        var initial = calculator.Initial();
        That(initial.Rating == 900 && rules.InitialRating == 1000, "显示初始分900与计算初始Elo1000分别读取");
        var firstWin = calculator.Calculate(initial, PvpRatingMode.Matching, PvpArenaOutcome.Win, initial);
        var firstLoss = calculator.Calculate(initial, PvpRatingMode.Matching, PvpArenaOutcome.Lose, initial);
        That(firstWin.After.Rating == 1050 && firstWin.After.Experience == 1000 && firstWin.After.Played == 1 &&
            firstWin.After.Wins == 1 && firstWin.After.Initialized, "首次匹配按双方冻结初始Elo计算定位积分与真实经验");
        That(firstLoss.After.Rating == 950 && firstLoss.After.Experience == 0 && firstLoss.After.Losses == 1,
            "初始显示值与Elo不同，首次失败仍按已选兼容规则以1000为计算基准");
        That(initial.Rating == 900 && !initial.Initialized && initial.Played == 0,
            "计算不修改任一赛前快照或先用胜方新分计算败方");
        var manual = calculator.Calculate(State(1234), PvpRatingMode.Ordinary, PvpArenaOutcome.Win);
        That(manual.ExperienceDelta == 450 && manual.After.Rating == 1234 && manual.After.Played == 11 &&
            manual.After.WinStreak == 0 && manual.After.Wins == 0, "普通胜利只按45%改变决斗经验，保留排位事实");
        var manualLoss = calculator.Calculate(State(1234, 12500), PvpRatingMode.Ordinary, PvpArenaOutcome.Lose);
        That(manualLoss.ExperienceDelta == -193 && manualLoss.After.Experience == 12307 && manualLoss.Grade == 1,
            "普通负经验-429乘45%向零取整，不按向下取整丢额外一点");
        That(calculator.Calculate(State(1000, 100), PvpRatingMode.Ordinary, PvpArenaOutcome.Lose).After.Experience == 100,
            "零段位失败按资源零扣经验");
        var opponent = State();
        var up = calculator.Calculate(State(1000, 11950), PvpRatingMode.Matching, PvpArenaOutcome.Win, opponent);
        var down = calculator.Calculate(State(1000, 12010), PvpRatingMode.Matching, PvpArenaOutcome.Lose, opponent);
        That(up.After.Experience == 12950 && up.Grade == 1 && up.After.HighestGrade == 1 && up.After.Rating == 1011,
            "匹配胜利跨段位，历史最高段位与积分随实际结果更新");
        That(down.After.Experience == 11796 && down.Grade == 0 && down.After.HighestGrade == 1 && down.After.Rating == 989,
            "匹配失败50%负点并降段，保留历史最高段位");
        var cap = calculator.Calculate(State(1000, 26990), PvpRatingMode.Matching, PvpArenaOutcome.Win, opponent);
        That(cap.After.Experience == 27000 && cap.ExperienceDelta == 10 && cap.Grade == 1,
            "经验封顶取真实到账差，不生成不存在的最高哨兵段位");
        That(calculator.Calculate(State(), PvpRatingMode.Total, PvpArenaOutcome.Win, opponent).ExperienceDelta == 1500,
            "总力队伍经验使用独立1500点表，不能混用普通1000点");
        var totalCharacter = calculator.CalculateTotalCharacterExperience(State(1234), PvpArenaOutcome.Win);
        That(totalCharacter.ExperienceDelta == 1500 && totalCharacter.After.Rating == 1234 &&
            totalCharacter.After.Played == 11 && totalCharacter.After.Wins == 0 && totalCharacter.After.WinStreak == 0,
            "总力三个真实角色只增加总力经验，不能同步改写其个人排位分和场次");
        That(calculator.CalculateTotalCharacterExperience(State(1234), PvpArenaOutcome.Win, manual: true).ExperienceDelta == 675,
            "手动总力角色经验按独立1500点和手动45%组合");
        That(calculator.Calculate(State(1000, played: 9), PvpRatingMode.Matching, PvpArenaOutcome.Win, opponent).After.Rating == 1050 &&
            calculator.Calculate(State(1000, played: 10), PvpRatingMode.Matching, PvpArenaOutcome.Win, opponent).After.Rating == 1051,
            "定位末局与定位结束后第一胜分别选择权重及40附加奖励");
        That(calculator.Calculate(State() with { WinStreak = 3 }, PvpRatingMode.Matching, PvpArenaOutcome.Win, opponent).After.Rating == 1013 &&
            calculator.Calculate(State() with { LossStreak = 1 }, PvpRatingMode.Matching, PvpArenaOutcome.Lose, opponent).After.Rating == 987,
            "连胜及连败按本局推进后的4连胜和2连败阈值选择权重");
        var draw = calculator.Calculate(State(1500) with { WinStreak = 4 }, PvpRatingMode.Matching, PvpArenaOutcome.Draw, opponent);
        That(draw.After.Rating == 1494 && draw.ExperienceDelta == 0 && draw.After.Draws == 1 && draw.After.WinStreak == 0 &&
            draw.After.LossStreak == 0, "不同积分平局按0.5预期分调整，经验不变且双方连胜清零");
        That(calculator.Calculate(State(), PvpRatingMode.Matching, PvpArenaOutcome.Draw, opponent).RatingDelta == 0,
            "相同积分平局不改变评分");
        var halves = new PvpRatingCalculator(Fixture("warm up weight", [0,1]));
        That(halves.Calculate(State(played: 0), PvpRatingMode.Matching, PvpArenaOutcome.Win, opponent).After.Rating == 1001 &&
            halves.Calculate(State(played: 0), PvpRatingMode.Matching, PvpArenaOutcome.Lose, opponent).After.Rating == 999,
            "正负半点均远离零四舍五入，不能使用默认银行家舍入");
        var maximum = calculator.Calculate(State(int.MaxValue, played: int.MaxValue) with
            { Wins = int.MaxValue, WinStreak = int.MaxValue }, PvpRatingMode.Matching, PvpArenaOutcome.Win, opponent);
        That(maximum.After is { Rating: int.MaxValue, Played: int.MaxValue, Wins: int.MaxValue, WinStreak: int.MaxValue },
            "评分与场次达到int32上限后不溢出成负数");
        var zero = calculator.Calculate(State(0), PvpRatingMode.Matching, PvpArenaOutcome.Lose, opponent);
        That(zero.After.Rating == 0 && zero.After.Initialized, "已初始化的真实零分不能重置成初始Elo");
        var cancelled = calculator.Calculate(initial, PvpRatingMode.Total, PvpArenaOutcome.Cancelled);
        That(ReferenceEquals(cancelled.Before, cancelled.After) && cancelled.ExperienceDelta == 0 && cancelled.RatingDelta == 0,
            "取消局不增加经验、积分或场次，也不要求虚假的对手分");
        Reject<ArgumentNullException>(() => calculator.Calculate(State(), PvpRatingMode.Matching, PvpArenaOutcome.Win),
            "正式评分缺少冻结对手快照拒绝");
        foreach (var invalid in new[] { State() with { Experience = -1 }, State() with { Rating = -1 },
            State() with { HighestRating = 999 }, State() with { Experience = 27001 },
            State() with { HighestGrade = 2 }, State() with { Played = -1 }, State() with { WinStreak = 1, LossStreak = 1 } })
            Reject<InvalidDataException>(() => calculator.Calculate(invalid, PvpRatingMode.Ordinary, PvpArenaOutcome.Win),
                "无效赛前评分快照拒绝，不静默修正损坏数据");
        foreach (var (tag, values) in new (string, int[])[] {
            ("pvp grade point", [0,1000]), ("pvp grade point total match", [0,1500,0]),
            ("pvp experience", [1,12000,2,12000]), ("rating", [1000,22]),
            ("streak win", [4,0]), ("warm up default weight", [0]),
            ("addtional weight", [0,0]), ("pvp match exp set", [10001,-1,-1,50,-1,50]) })
            Reject<InvalidDataException>(() => Fixture(tag, values), "残缺或越界PVF评分参数拒绝，不套用默认公式");
        Console.WriteLine($"PASS {Count - beforeCount} PVF参数及决斗评分兼容规则断言");

        PvpRatingState State(int rating = 1000, int experience = 0, int played = 11) =>
            new(experience, rating, rating, rules.Grade(experience), played, 0, 0, 0, 0, 0, true);
    }

    public static void RunResources(PvfArchive pvf)
    {
        var rules = new PvpRatingCatalog(pvf);
        That(rules.Experience.Length == 22 && rules.Experience[^1] == 9999999 && rules.InitialPoints == 900 &&
            rules.InitialRating == 1000 && rules.OrdinaryPoints[0].Win == 1000 && rules.TotalPoints[0].Win == 1500 &&
            rules.ManualPercent == new PvpGradePoints(45,45) && rules.MatchingPercent == new PvpGradePoints(100,50),
            "真实PVF独立普通/总力点表、经验上限、两份初始分及兼容比例装配");
        var calculator = new PvpRatingCalculator(rules);
        var state = calculator.Initial();
        That(calculator.Calculate(state, PvpRatingMode.Total, PvpArenaOutcome.Win, state).After is
            { Experience: 1500, Rating: 1050, Played: 1 }, "真实PVF重复开标签附加权重及总力首胜完整计算");
    }

    internal static PvpRatingCatalog Fixture(string? change = null, int[]? replacement = null)
    {
        return new(Document([
            ("pvp experience max grade",[2]), ("pvp experience",[1,12000,2,27000]),
            ("pvp grade point",[0,1000,0,1,1000,-429]), ("pvp grade point total match",[0,1500,0,1,1500,-429]),
            ("pvp match exp set",[100,-1,-1,50,-1,50]), ("pvp manual match etc set",[45,-1,1,45,-1,0])]),
            Document([
            ("pvp initial rating point",[900]), ("initial rating point",[1000]), ("warm up games",[10]),
            ("warm up default weight",[120]), ("warm up weight",[0,100]), ("first after warm up bonus",[40]),
            ("rating",[2000,8,1700,10,1500,14,1400,16,1100,20,700,22,0,24]),
            ("streak win",[8,180,6,140,4,120]), ("streak lose",[6,200,4,140,2,120]),
            ("addtional weight",[2000,0,-1,1700,0,0,800,0,0,500,0,1,0,0,2]), ("addtional weight",[])]));

        PvfTokenDocument Document((string Tag, int[] Values)[] rows)
        {
            var tokens = new List<PvfToken>();
            var names = new List<string>();
            foreach (var row in rows)
            {
                names.Add("[" + row.Tag + "]");
                tokens.Add(new(3,names.Count - 1));
                tokens.AddRange((row.Tag == change && row.Values.Length != 0 ? replacement! : row.Values)
                    .Select(value => new PvfToken(0,value)));
            }
            return new(tokens, id => names[id]);
        }
    }
}
