using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct Strongway2019DungeonRow(int Key, int DungeonId, int RawValue);
internal readonly record struct Strongway2019RewardRow(int Key, int ItemId, int Count, int RawValue1, int RawValue2);
internal readonly record struct Strongway2019SkillRow(int ButtonIndex, string Type, string Symbol,
    int NeedPoint, int GlobalCooltime, int? LimitCount);

internal sealed class Strongway2019Catalog
{
    public const string ResourcePath = "etc/dpl/season/dpl.etc";

    public int DungeonStartDelay { get; }
    public int StartAreaPathgateOpenDelay { get; }
    public int ReturnVillageDelay { get; }
    public ImmutableArray<Strongway2019DungeonRow> NormalDungeons { get; } = [];
    public ImmutableArray<Strongway2019DungeonRow> MatchingDungeons { get; } = [];
    public ImmutableArray<Strongway2019RewardRow> Rewards { get; } = [];
    public int QualifyingDungeonId { get; }
    public ImmutableArray<int> RandomMapIds { get; } = [];
    public ImmutableDictionary<int, int> MapMonsters { get; } = ImmutableDictionary<int, int>.Empty;
    public ImmutableArray<int> AddPointValues { get; } = [];
    public int SkillWaitingTime { get; }
    public ImmutableArray<Strongway2019SkillRow> Skills { get; } = [];

    public Strongway2019Catalog(PvfArchive archive) : this(archive.ReadTokens(ResourcePath), archive.ResolveString) { }

    internal Strongway2019Catalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty)
            throw new InvalidDataException("首期强者之路资源块未正确闭合");
        var fields = document.Sections;
        DungeonStartDelay = Scalar(fields, "dungeon start delay");
        StartAreaPathgateOpenDelay = Scalar(fields, "start area pathgate open delay");
        ReturnVillageDelay = Scalar(fields, "return village delay");
        NormalDungeons = Dungeons("normal dungeon index");
        MatchingDungeons = Dungeons("matching dungeon index");

        var rewards = Integers(fields, "reward", true);
        if (rewards.IsEmpty || rewards.Length % 5 != 0)
            throw new InvalidDataException("首期强者之路奖励必须为完整五列记录");
        var rewardRows = ImmutableArray.CreateBuilder<Strongway2019RewardRow>(rewards.Length / 5);
        var rewardKeys = new HashSet<int>();
        for (int i = 0; i < rewards.Length; i += 5)
        {
            if (rewards[i] < 0 || rewards[i + 1] <= 0 || rewards[i + 2] <= 0 || !rewardKeys.Add(rewards[i]))
                throw new InvalidDataException("首期强者之路奖励键重复或物品数量无效");
            // 末两列尚未确定消费语义，按原整数保留，不作为额度或任务编号解释。
            rewardRows.Add(new(rewards[i], rewards[i + 1], rewards[i + 2], rewards[i + 3], rewards[i + 4]));
        }
        Rewards = rewardRows.ToImmutable();
        QualifyingDungeonId = Scalar(fields, "qualifying dungeon index", 1);
        RandomMapIds = Integers(fields, "random map index", true);
        if (RandomMapIds.IsEmpty || RandomMapIds.Any(id => id <= 0) || RandomMapIds.Distinct().Count() != RandomMapIds.Length)
            throw new InvalidDataException("首期强者之路随机地图为空、重复或无效");
        var monsterValues = Integers(fields, "map monster index", true);
        if (monsterValues.IsEmpty || monsterValues.Length % 2 != 0)
            throw new InvalidDataException("首期强者之路地图怪物映射必须为完整整数对");
        var mapMonsters = ImmutableDictionary.CreateBuilder<int, int>();
        for (int i = 0; i < monsterValues.Length; i += 2)
        {
            if (monsterValues[i] <= 0 || monsterValues[i + 1] <= 0 ||
                !mapMonsters.TryAdd(monsterValues[i], monsterValues[i + 1]))
                throw new InvalidDataException("首期强者之路地图怪物映射重复或无效");
        }
        MapMonsters = mapMonsters.ToImmutable();
        if (RandomMapIds.Any(id => !MapMonsters.ContainsKey(id)))
            throw new InvalidDataException("首期强者之路随机地图缺少怪物映射");

        var system = Single(fields, "skill system");
        if (!system.IsBlock || !system.Values.IsEmpty)
            throw new InvalidDataException("首期强者之路技能系统块结构无效");
        AddPointValues = Integers(system.Children, "add point");
        if (AddPointValues.Length != 2)
            throw new InvalidDataException("首期强者之路充能声明必须保留两个整数");
        SkillWaitingTime = Scalar(system.Children, "skill waiting time");
        var skills = ImmutableArray.CreateBuilder<Strongway2019SkillRow>();
        var buttons = new HashSet<int>();
        foreach (var skill in system.Children.Where(row => row.Name == "skill"))
        {
            if (!skill.IsBlock || !skill.Values.IsEmpty)
                throw new InvalidDataException("首期强者之路技能块结构无效");
            int button = Scalar(skill.Children, "button index");
            if (!buttons.Add(button))
                throw new InvalidDataException("首期强者之路技能按钮重复");
            var type = Values(skill.Children, "type");
            if (type is not [{ Type: 6 }, { Type: 6 }])
                throw new InvalidDataException("首期强者之路技能类型必须为两个字符串");
            string effect = resolve(type[0].Value), symbol = resolve(type[1].Value);
            if (string.IsNullOrWhiteSpace(effect) || string.IsNullOrWhiteSpace(symbol))
                throw new InvalidDataException("首期强者之路技能类型或符号为空");
            int? limit = skill.Children.Any(row => row.Name == "limit count")
                ? Scalar(skill.Children, "limit count") : null;
            skills.Add(new(button, effect, symbol, Scalar(skill.Children, "need point"),
                Scalar(skill.Children, "global cooltime"), limit));
        }
        if (skills.Count == 0)
            throw new InvalidDataException("首期强者之路技能列表为空");
        Skills = skills.ToImmutable();

        ImmutableArray<Strongway2019DungeonRow> Dungeons(string name)
        {
            var values = Integers(fields, name, true);
            if (values.IsEmpty || values.Length % 3 != 0)
                throw new InvalidDataException("首期强者之路副本必须为完整三列记录: " + name);
            var rows = ImmutableArray.CreateBuilder<Strongway2019DungeonRow>(values.Length / 3);
            var keys = new HashSet<int>();
            for (int i = 0; i < values.Length; i += 3)
            {
                if (values[i] < 0 || values[i + 1] <= 0 || !keys.Add(values[i]))
                    throw new InvalidDataException("首期强者之路副本键重复或编号无效: " + name);
                rows.Add(new(values[i], values[i + 1], values[i + 2]));
            }
            return rows.ToImmutable();
        }
    }

    static PvfTokenSection Single(ImmutableArray<PvfTokenSection> fields, string name)
    {
        var found = fields.Where(row => row.Name == name).ToArray();
        return found is [var row] ? row : throw new InvalidDataException("首期强者之路字段缺失或重复: " + name);
    }

    static ImmutableArray<PvfToken> Values(ImmutableArray<PvfTokenSection> fields, string name, bool block = false)
    {
        var row = Single(fields, name);
        if (row.IsBlock != block || !row.Children.IsEmpty)
            throw new InvalidDataException("首期强者之路字段结构无效: " + name);
        return row.Values;
    }

    static ImmutableArray<int> Integers(ImmutableArray<PvfTokenSection> fields, string name, bool block = false)
    {
        var values = Values(fields, name, block);
        if (values.Any(token => token.Type != 0))
            throw new InvalidDataException("首期强者之路字段必须为整数: " + name);
        return values.Select(token => token.Value).ToImmutableArray();
    }

    static int Scalar(ImmutableArray<PvfTokenSection> fields, string name, int minimum = 0)
    {
        var values = Integers(fields, name);
        return values is [var value] && value >= minimum ? value
            : throw new InvalidDataException("首期强者之路标量无效: " + name);
    }
}
