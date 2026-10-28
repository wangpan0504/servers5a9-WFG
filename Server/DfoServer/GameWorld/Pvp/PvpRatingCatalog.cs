using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Pvp;

internal readonly record struct PvpGradePoints(int Win, int Loss);
internal readonly record struct PvpRatingWeight(int Threshold, int Value);
internal readonly record struct PvpRatingAdjustment(int Threshold, int Win, int Loss);

// PVF仅提供参数；如何组合、定位及取整属于明确采用的本服兼容规则。
internal sealed class PvpRatingCatalog
{
    public ImmutableArray<int> Experience { get; }
    public ImmutableArray<PvpGradePoints> OrdinaryPoints { get; }
    public ImmutableArray<PvpGradePoints> TotalPoints { get; }
    public PvpGradePoints ManualPercent { get; }
    public PvpGradePoints MatchingPercent { get; }
    public int InitialRating { get; }
    public int InitialPoints { get; }
    public int WarmupGames { get; }
    public int WarmupDefault { get; }
    public int AfterWarmupBonus { get; }
    public ImmutableArray<PvpRatingWeight> Warmup { get; }
    public ImmutableArray<PvpRatingWeight> Rating { get; }
    public ImmutableArray<PvpRatingWeight> Wins { get; }
    public ImmutableArray<PvpRatingWeight> Losses { get; }
    public ImmutableArray<PvpRatingAdjustment> Additional { get; }

    public PvpRatingCatalog(PvfArchive pvf) : this(
        new(pvf.ReadTokens("etc/pvp_ref.etc"), pvf.ResolveString),
        new(pvf.ReadTokens("etc/matchingmode.etc"), pvf.ResolveString)) { }

    internal PvpRatingCatalog(PvfTokenDocument reference, PvfTokenDocument matching)
    {
        if (!reference.Diagnostics.IsEmpty || !matching.Diagnostics.IsEmpty) throw Invalid("声明未闭合");
        var source = Flatten(reference.Sections).ToArray();
        var elo = Flatten(matching.Sections).ToArray();
        int grades = Scalar(source, "pvp experience max grade", 1, 255);
        int[] experience = Numbers(Single(source, "pvp experience"));
        if (experience.Length != grades * 2) throw Invalid("段位经验表宽度");
        var thresholds = ImmutableArray.CreateBuilder<int>(grades + 1);
        thresholds.Add(0);
        for (int index = 0; index < grades; index++)
        {
            if (experience[index * 2] != index + 1 || experience[index * 2 + 1] <= thresholds[^1])
                throw Invalid("段位经验表顺序");
            thresholds.Add(experience[index * 2 + 1]);
        }
        Experience = thresholds.MoveToImmutable();
        OrdinaryPoints = Points("pvp grade point");
        TotalPoints = Points("pvp grade point total match");
        ManualPercent = Percent("pvp manual match etc set");
        MatchingPercent = Percent("pvp match exp set");
        InitialRating = Scalar(elo, "initial rating point", 0, int.MaxValue);
        InitialPoints = Scalar(elo, "pvp initial rating point", 0, int.MaxValue);
        WarmupGames = Scalar(elo, "warm up games", 0, 10000);
        WarmupDefault = Scalar(elo, "warm up default weight", 1, 10000);
        AfterWarmupBonus = Scalar(elo, "first after warm up bonus", 0, 10000);
        Warmup = Weights("warm up weight");
        Rating = Weights("rating");
        Wins = Weights("streak win");
        Losses = Weights("streak lose");
        if (Rating[^1].Threshold != 0) throw Invalid("评分权重缺少零下限");
        var additional = elo.Where(row => row.Name == "addtional weight").ToArray();
        // 本版原表用重复开标签代替闭标签，仅接受这一份有值行和一份空行。
        if (additional.Length is < 1 or > 2 || additional.Length == 2 &&
            (!additional[1].Values.IsEmpty || !additional[1].Children.IsEmpty)) throw Invalid("附加权重重复声明");
        int[] values = Numbers(additional[0]);
        if (values.Length == 0 || values.Length % 3 != 0) throw Invalid("附加权重宽度");
        var adjustments = ImmutableArray.CreateBuilder<PvpRatingAdjustment>(values.Length / 3);
        for (int at = 0; at < values.Length; at += 3)
        {
            if (values[at] < 0 || at > 0 && values[at] >= values[at - 3] ||
                values[at + 1] is < -10000 or > 10000 || values[at + 2] is < -10000 or > 10000)
                throw Invalid("附加权重范围或顺序");
            adjustments.Add(new(values[at], values[at + 1], values[at + 2]));
        }
        Additional = adjustments.MoveToImmutable();

        ImmutableArray<PvpGradePoints> Points(string name)
        {
            int[] values = Numbers(Single(source, name));
            if (values.Length != grades * 3) throw Invalid(name);
            var points = ImmutableArray.CreateBuilder<PvpGradePoints>(grades);
            for (int index = 0; index < grades; index++)
            {
                int at = index * 3;
                if (values[at] != index || values[at + 1] <= 0 || values[at + 2] > 0) throw Invalid(name);
                points.Add(new(values[at + 1], values[at + 2]));
            }
            return points.MoveToImmutable();
        }
        PvpGradePoints Percent(string name)
        {
            int[] values = Numbers(Single(source, name));
            if (values.Length != 6 || values[0] is < 0 or > 10000 || values[3] is < 0 or > 10000)
                throw Invalid(name);
            return new(values[0], values[3]);
        }
        ImmutableArray<PvpRatingWeight> Weights(string name)
        {
            int[] values = Numbers(Single(elo, name));
            if (values.Length == 0 || values.Length % 2 != 0) throw Invalid(name);
            var weights = ImmutableArray.CreateBuilder<PvpRatingWeight>(values.Length / 2);
            for (int at = 0; at < values.Length; at += 2)
            {
                if (values[at] < 0 || values[at + 1] is <= 0 or > 10000 || at > 0 && values[at] >= values[at - 2])
                    throw Invalid(name);
                weights.Add(new(values[at], values[at + 1]));
            }
            return weights.MoveToImmutable();
        }
    }

    public int Grade(int experience)
    {
        if (experience < 0 || experience > Experience[^1]) throw Invalid("经验范围");
        int grade = 0;
        while (grade + 1 < Experience.Length - 1 && experience >= Experience[grade + 1]) grade++;
        return grade;
    }

    static IEnumerable<PvfTokenSection> Flatten(ImmutableArray<PvfTokenSection> sections)
    {
        foreach (var section in sections)
        {
            yield return section;
            foreach (var nested in Flatten(section.Children)) yield return nested;
        }
    }
    static PvfTokenSection Single(PvfTokenSection[] sections, string name)
    {
        var found = sections.Where(row => row.Name == name).ToArray();
        return found.Length == 1 ? found[0] : throw Invalid(name);
    }
    static int Scalar(PvfTokenSection[] sections, string name, int minimum, int maximum)
    {
        int[] values = Numbers(Single(sections, name));
        return values.Length == 1 && values[0] >= minimum && values[0] <= maximum ? values[0] : throw Invalid(name);
    }
    static int[] Numbers(PvfTokenSection section)
    {
        if (!section.Children.IsEmpty || section.Values.Any(value => value.Type != 0)) throw Invalid(section.Name);
        return section.Values.Select(value => value.Value).ToArray();
    }
    static InvalidDataException Invalid(string field) => new("决斗评分资源无效：" + field);
}
