using DfoServer.GameWorld.Pvp;

namespace DfoServer.Game.Pvp;

internal enum PvpRatingMode { Ordinary, Matching, Total }
internal sealed record PvpRatingState(int Experience, int Rating, int HighestRating, int HighestGrade,
    int Played, int Wins, int Losses, int Draws, int WinStreak, int LossStreak, bool Initialized)
{
    // GM指定的战斗力称谓；旧存档缺省为无称谓，结算沿用同一快照保留。
    public byte PowerTitle { get; init; }
}

internal readonly record struct PvpDisplayGrade(byte Level, byte PowerTitle = 0)
{
    public static implicit operator PvpDisplayGrade(byte level) => new(level);
}
internal sealed record PvpRatingChange(PvpRatingState Before, PvpRatingState After, int BeforeGrade, int Grade)
{
    public int ExperienceDelta => After.Experience - Before.Experience;
    public int RatingDelta => After.Rating - Before.Rating;
}

// 纯计算不改角色、不发货币；持久用例应冻结双方赛前值，并以同一回执提交实际结果。
internal sealed class PvpRatingCalculator(PvpRatingCatalog rules)
{
    public const string RuleVersion = "pvf-compatible-v1";
    internal int Grade(PvpRatingState state) { Validate(state); return rules.Grade(state.Experience); }
    public PvpRatingState Initial() => new(0, rules.InitialPoints, rules.InitialPoints, 0, 0, 0, 0, 0, 0, 0, false);

    public PvpRatingChange Calculate(PvpRatingState before, PvpRatingMode mode, PvpArenaOutcome outcome,
        PvpRatingState? opponent = null)
    {
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(mode));
        Validate(before);
        int grade = rules.Grade(before.Experience);
        if (outcome == PvpArenaOutcome.Cancelled) return new(before, before, grade, grade);
        bool win = outcome == PvpArenaOutcome.Win, draw = outcome == PvpArenaOutcome.Draw;
        var points = (mode == PvpRatingMode.Total ? rules.TotalPoints : rules.OrdinaryPoints)[grade];
        var percent = mode == PvpRatingMode.Ordinary ? rules.ManualPercent : rules.MatchingPercent;
        var experience = ApplyExperience(before, grade, points, percent, outcome);
        var after = experience.After;
        if (mode == PvpRatingMode.Ordinary) return experience;
        if (opponent is null) throw new ArgumentNullException(nameof(opponent));
        Validate(opponent);
        int self = Seed(before), other = Seed(opponent);
        int wins = win && !draw ? Increment(before.WinStreak) : 0;
        int losses = !win && !draw ? Increment(before.LossStreak) : 0;
        int weight = before.Played < rules.WarmupGames
            ? Select(rules.Warmup, before.Played, rules.WarmupDefault) : Select(rules.Rating, self, 0);
        int multiplier = draw ? 100 : win ? Select(rules.Wins, wins, 100) : Select(rules.Losses, losses, 100);
        double expected = 1 / (1 + Math.Pow(10, ((double)other - self) / 400));
        long ratingDelta = (long)Math.Round(weight * (double)multiplier / 100 * ((draw ? .5 : win ? 1 : 0) - expected),
            MidpointRounding.AwayFromZero);
        if (!draw)
        {
            foreach (var row in rules.Additional)
                if (self >= row.Threshold) { ratingDelta += win ? row.Win : row.Loss; break; }
            if (win && before.Played == rules.WarmupGames) ratingDelta += rules.AfterWarmupBonus;
            ratingDelta = win ? Math.Max(0, ratingDelta) : Math.Min(0, ratingDelta);
        }
        int rating = (int)Math.Clamp(self + ratingDelta, 0, int.MaxValue);
        after = after with
        {
            Rating = rating, HighestRating = Math.Max(before.HighestRating, rating), Initialized = true,
            Played = Increment(before.Played), Wins = win && !draw ? Increment(before.Wins) : before.Wins,
            Losses = !win && !draw ? Increment(before.Losses) : before.Losses,
            Draws = draw ? Increment(before.Draws) : before.Draws, WinStreak = wins, LossStreak = losses
        };
        return experience with { After = after };
    }

    // 总力队伍评分与三个真实角色的个人评分分开；角色只取总力经验，不改变个人排位事实。
    public PvpRatingChange CalculateTotalCharacterExperience(PvpRatingState before, PvpArenaOutcome outcome, bool manual = false)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        Validate(before);
        int grade = rules.Grade(before.Experience);
        if (outcome == PvpArenaOutcome.Cancelled) return new(before, before, grade, grade);
        return ApplyExperience(before, grade, rules.TotalPoints[grade], manual ? rules.ManualPercent : rules.MatchingPercent, outcome);
    }

    PvpRatingChange ApplyExperience(PvpRatingState before, int grade, PvpGradePoints points, PvpGradePoints percent,
        PvpArenaOutcome outcome)
    {
        bool win = outcome == PvpArenaOutcome.Win;
        long delta = outcome == PvpArenaOutcome.Draw ? 0 :
            (long)(win ? points.Win : points.Loss) * (win ? percent.Win : percent.Loss) / 100;
        int experience = (int)Math.Clamp(before.Experience + delta, 0, rules.Experience[^1]);
        int afterGrade = rules.Grade(experience);
        return new(before, before with { Experience = experience, HighestGrade = Math.Max(before.HighestGrade, afterGrade) },
            grade, afterGrade);
    }

    int Seed(PvpRatingState state) => !state.Initialized && state.Rating == rules.InitialPoints ? rules.InitialRating : state.Rating;
    internal void Validate(PvpRatingState state)
    {
        if (state.Experience < 0 || state.Experience > rules.Experience[^1] || state.Rating < 0 ||
            state.HighestRating < state.Rating || state.HighestGrade < rules.Grade(state.Experience) ||
            state.HighestGrade >= rules.OrdinaryPoints.Length || state.Played < 0 || state.Wins < 0 ||
            state.Losses < 0 || state.Draws < 0 || state.WinStreak < 0 || state.LossStreak < 0 ||
            state.WinStreak != 0 && state.LossStreak != 0 || state.PowerTitle > 9)
            throw new InvalidDataException("决斗评分赛前快照无效");
    }
    static int Select(System.Collections.Immutable.ImmutableArray<PvpRatingWeight> rows, int value, int fallback)
    {
        foreach (var row in rows) if (value >= row.Threshold) return row.Value;
        return fallback;
    }
    static int Increment(int value) => value == int.MaxValue ? value : value + 1;
}
