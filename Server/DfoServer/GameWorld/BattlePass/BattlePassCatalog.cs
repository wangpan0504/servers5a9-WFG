using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.BattlePass;

internal sealed record BattlePassMission(int Id, int RequiredCount, int Experience, int RawTypeCode, string Text, int RawParameter);
internal sealed record BattlePassReward(int ItemId, int Count, int RawTrackCode);
internal sealed record BattlePassLevelReward(int Level, ImmutableArray<BattlePassReward> Rewards);
internal readonly record struct BattlePassExperienceRequirement(int Level, ulong Experience);
internal readonly record struct BattlePassTier(int FirstLevel, int LastLevel);

// 只解释同源定义；原始条件码、轨道码及调度顺序不在目录层赋予未核实的业务语义。
internal sealed class BattlePassCatalog
{
    public const string ResourcePath = "etc/chn_battlepass.etc";
    readonly ImmutableArray<ulong> thresholds;
    public string UiPath { get; }
    public ImmutableArray<BattlePassMission> Missions { get; }
    public FrozenDictionary<int, BattlePassMission> MissionsById { get; }
    public ImmutableArray<int> DailyMissionIds { get; }
    public ImmutableArray<int> WeeklyMissionIds { get; }
    public ImmutableArray<int> SeasonMissionIds { get; }
    public ImmutableArray<BattlePassLevelReward> LevelRewards { get; }
    public FrozenDictionary<int, BattlePassLevelReward> RewardsByLevel { get; }
    public ImmutableArray<int> Postal { get; }
    public ImmutableArray<BattlePassExperienceRequirement> ExperienceTable { get; }
    public ImmutableArray<BattlePassTier> Tiers { get; }
    public int CertificateIndex { get; }
    public int PackageIndex { get; }
    public int BuyRestrictCode { get; }
    public int RawGoldenMissionExperienceRatio { get; }
    public int RawCeraConsumptionExperiencePercentage { get; }
    public int MaximumLevel => ExperienceTable.Length;

    BattlePassCatalog(ImmutableArray<PvfTokenSection> sections, Func<int, string> resolve)
    {
        PvfTokenSection Required(string name)
        {
            var matches = sections.Where(row => row.Name == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("战令唯一资源段缺失或重复: " + name);
            return matches[0];
        }
        ImmutableArray<PvfToken> Values(PvfTokenSection row)
        {
            if (!row.Children.IsEmpty) throw new InvalidDataException("战令资源段包含未解释的嵌套: " + row.Name);
            return row.Values;
        }
        int Integer(PvfToken token) => token.Type == 0 ? token.Value :
            throw new InvalidDataException("战令字段不是整数");
        string Text(PvfToken token) => token.Type is 6 or 7 ? resolve(token.Value) :
            throw new InvalidDataException("战令字段不是字符串");
        PvfToken Scalar(string name)
        {
            var values = Values(Required(name));
            if (values.Length != 1) throw new InvalidDataException("战令标量字段数量无效: " + name);
            return values[0];
        }
        ImmutableArray<int> Integers(string name) => Values(Required(name)).Select(Integer).ToImmutableArray();

        UiPath = Text(Scalar("ui path"));
        if (string.IsNullOrWhiteSpace(UiPath)) throw new InvalidDataException("战令界面路径为空");
        var missions = ImmutableArray.CreateBuilder<BattlePassMission>();
        var missionIndex = new Dictionary<int, BattlePassMission>();
        foreach (var row in sections.Where(row => row.Name == "mission"))
        {
            var values = Values(row);
            if (values.Length != 6) throw new InvalidDataException("战令任务字段不完整");
            var mission = new BattlePassMission(Integer(values[0]), Integer(values[1]), Integer(values[2]),
                Integer(values[3]), Text(values[4]), Integer(values[5]));
            if (mission.Id <= 0 || mission.RequiredCount <= 0 || mission.Experience <= 0 ||
                !missionIndex.TryAdd(mission.Id, mission))
                throw new InvalidDataException("战令任务ID、目标或经验无效，或ID重复");
            missions.Add(mission);
        }
        if (missions.Count == 0) throw new InvalidDataException("战令任务定义缺失");
        Missions = missions.ToImmutable();
        MissionsById = missionIndex.ToFrozenDictionary();
        DailyMissionIds = Schedule("daily mission");
        WeeklyMissionIds = Schedule("weekly mission");
        SeasonMissionIds = Schedule("season mission");
        ImmutableArray<int> Schedule(string name)
        {
            var ids = Integers(name);
            if (ids.IsEmpty || ids.Any(id => !missionIndex.ContainsKey(id)))
                throw new InvalidDataException("战令调度为空或引用缺失任务: " + name);
            return ids;
        }

        var exp = Integers("exp table");
        if (exp.IsEmpty || exp.Length % 2 != 0) throw new InvalidDataException("战令经验表不是等级经验对");
        var requirements = ImmutableArray.CreateBuilder<BattlePassExperienceRequirement>();
        var totals = ImmutableArray.CreateBuilder<ulong>();
        ulong total = 0;
        for (int index = 0; index < exp.Length; index += 2)
        {
            int level = index / 2 + 1;
            int experience = exp[index + 1];
            bool terminal = index == exp.Length - 2;
            if (exp[index] != level || (terminal ? experience != 0 : experience <= 0))
                throw new InvalidDataException("战令经验表等级不连续或升级需求无效");
            totals.Add(total);
            requirements.Add(new(level, (ulong)experience));
            total = checked(total + (ulong)experience);
        }
        ExperienceTable = requirements.ToImmutable();
        thresholds = totals.ToImmutable();

        var rewards = ImmutableArray.CreateBuilder<BattlePassLevelReward>();
        var rewardIndex = new Dictionary<int, BattlePassLevelReward>();
        foreach (var row in sections.Where(row => row.Name == "level reward"))
        {
            var values = Values(row);
            if (values.Length < 4 || (values.Length - 1) % 3 != 0)
                throw new InvalidDataException("战令等级奖励不是完整物品数量轨道三元组");
            int level = Integer(values[0]);
            var items = ImmutableArray.CreateBuilder<BattlePassReward>();
            for (int at = 1; at < values.Length; at += 3)
            {
                var reward = new BattlePassReward(Integer(values[at]), Integer(values[at + 1]), Integer(values[at + 2]));
                if (reward.ItemId <= 0 || reward.Count <= 0) throw new InvalidDataException("战令奖励物品或数量无效");
                items.Add(reward);
            }
            var definition = new BattlePassLevelReward(level, items.ToImmutable());
            if (level < 1 || level > MaximumLevel || !rewardIndex.TryAdd(level, definition))
                throw new InvalidDataException("战令奖励等级越界或重复");
            rewards.Add(definition);
        }
        if (rewardIndex.Count != MaximumLevel) throw new InvalidDataException("战令等级奖励缺失");
        LevelRewards = rewards.ToImmutable();
        RewardsByLevel = rewardIndex.ToFrozenDictionary();

        var tiers = Integers("tier table");
        if (tiers.IsEmpty || tiers.Length % 2 != 0) throw new InvalidDataException("战令页阶表不是区间对");
        var ranges = ImmutableArray.CreateBuilder<BattlePassTier>();
        for (int at = 0; at < tiers.Length; at += 2)
        {
            if (tiers[at] < 1 || tiers[at + 1] < tiers[at] || tiers[at + 1] > MaximumLevel)
                throw new InvalidDataException("战令页阶区间越界");
            ranges.Add(new(tiers[at], tiers[at + 1]));
        }
        Tiers = ranges.ToImmutable();
        Postal = Integers("postal");
        CertificateIndex = Integer(Scalar("certificate index"));
        PackageIndex = Integer(Scalar("package index"));
        BuyRestrictCode = Integer(Scalar("buy restrict code"));
        RawGoldenMissionExperienceRatio = Integer(Scalar("golden mission exp ratio"));
        RawCeraConsumptionExperiencePercentage = Integer(Scalar("cera consumption exp percentage"));
    }

    public static BattlePassCatalog Load(PvfArchive pvf) => Parse(pvf.ReadTokens(ResourcePath), pvf.ResolveString);

    internal static BattlePassCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("战令资源包含不完整声明");
        return new(document.Sections, resolve);
    }

    public ulong Requirement(int level) => ExperienceTable[LevelIndex(level)].Experience;
    public ulong TotalExperienceToReach(int level) => thresholds[LevelIndex(level)];
    int LevelIndex(int level) => level >= 1 && level <= MaximumLevel ? level - 1 :
        throw new ArgumentOutOfRangeException(nameof(level), "战令等级超出目录");

    // 纯查询在终级封顶，不决定任务领取、消费经验或满级溢出的业务处理。
    public int LevelAtTotalExperience(ulong experience)
    {
        int low = 0, high = thresholds.Length - 1;
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (thresholds[middle] <= experience) low = middle;
            else high = middle - 1;
        }
        return low + 1;
    }
}
