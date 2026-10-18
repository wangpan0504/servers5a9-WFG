using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// #233 强袭模式资源：etc/dungeonetc/assaultmodedungeoninfo.etc + 任务副本 dgn 的入场声明。
// [mission info list] 三元组 (主副本, 任务副本, 0)：客户端 A81020 以主副本(7300/7327)判定强袭槽位，
// ACK1547 处理后 A81B50→A80D50 以任务副本 id 在同一容器的另一索引查找，故 ACK 必须回任务副本 id。
internal sealed record AssaultModeDailyOpen(int Day, ImmutableArray<int> Dungeons, int HiddenDungeon);
// etc/itemdropinfo_clearreward.etc [dungeon index privilege service reward rate] 中五元组 (副本, 难度, 物品, 概率/10000, 数量)。
// 节名含 privilege service（特权服务），无特权时是否同样发放待确认；本实现对强袭任务副本按原表发放（任务副本×15，隐藏副本×45）。
internal sealed record AssaultModeClearReward(int DungeonId, int Difficulty, int ItemId, int Rate, int Count);
internal sealed record AssaultModeMissionRequirement(int MissionDungeonId, int MinimumLevel, int ItemId, int ItemCount, int ItemFlag);

internal sealed class AssaultModeCatalog
{
    public const string ResourcePath = "etc/dungeonetc/assaultmodedungeoninfo.etc";
    // 客户端 00A7FF50 查询任务 0x17E7 是否完成（对应 dstr 60720/60727 前置任务提示）。
    public const int PrerequisiteQuestId = 6119;
    public const string MissionDungeonType = "assaultmode dungeon";
    public const string ClearRewardPath = "etc/itemdropinfo_clearreward.etc";
    // 客户端 0xA82280 以 (3-b0)/3 显示可通关次数（2C0F670 固定返回3），0xA82173 以 (1-b1)/1 显示隐藏关卡次数。
    public const int DailyClearLimit = 3;
    public const int DailyHiddenLimit = 1;

    readonly Func<int, AssaultModeMissionRequirement>? readRequirement;
    readonly ConcurrentDictionary<int, Lazy<(AssaultModeMissionRequirement? Value, string? Error)>> requirements = new();

    public ImmutableDictionary<int, ImmutableArray<int>> Missions { get; }
    public int HiddenBaseDungeon { get; }
    public ImmutableArray<AssaultModeDailyOpen> DailyOpens { get; }
    public ImmutableDictionary<int, AssaultModeClearReward> ClearRewards { get; internal set; } = ImmutableDictionary<int, AssaultModeClearReward>.Empty;

    AssaultModeCatalog(ImmutableDictionary<int, ImmutableArray<int>> missions, int hidden, ImmutableArray<AssaultModeDailyOpen> daily,
        Func<int, AssaultModeMissionRequirement>? readRequirement)
    {
        Missions = missions;
        HiddenBaseDungeon = hidden;
        DailyOpens = daily;
        this.readRequirement = readRequirement;
    }

    public static AssaultModeCatalog Load(PvfArchive pvf, DungeonCatalog dungeons)
    {
        var catalog = Parse(pvf.ReadTokens(ResourcePath), pvf.ResolveString, id => ReadRequirement(dungeons.Get(id)));
        catalog.ClearRewards = ParseClearRewards(pvf.ReadTokens(ClearRewardPath), pvf.ResolveString,
            catalog.Missions.Values.SelectMany(rows => rows).ToHashSet());
        return catalog;
    }

    // 隐藏副本(7327)可进入：当日普通通关已达3且隐藏通关未达1（客户端 A81090）。
    public bool HiddenUnlocked(int clears, int hiddenClears) => clears >= DailyClearLimit && hiddenClears < DailyHiddenLimit;

    internal static ImmutableDictionary<int, AssaultModeClearReward> ParseClearRewards(IReadOnlyList<PvfToken> tokens,
        Func<int, string> resolve, IReadOnlySet<int> missions)
    {
        var result = new Dictionary<int, AssaultModeClearReward>();
        bool inside = false;
        for (int at = 0; at < tokens.Count;)
        {
            if (tokens[at].Type != 3) { at++; continue; }
            string tag = resolve(tokens[at++].Value).Trim().ToLowerInvariant();
            if (tag == "[dungeon index privilege service reward rate]") { inside = true; continue; }
            if (tag == "[/dungeon index privilege service reward rate]") { if (inside) break; continue; }
            if (!inside || tag != "[dungeon type]") continue;
            var values = new List<int>();
            bool numeric = true;
            while (at < tokens.Count && tokens[at].Type != 3)
            {
                if (tokens[at].Type != 0) numeric = false; else values.Add(tokens[at].Value);
                at++;
            }
            if (!numeric || values.Count % 5 != 0)
            {
                if (values.Any(missions.Contains)) throw new NotSupportedException("强袭通关奖励行格式未确认");
                continue;
            }
            for (int i = 0; i < values.Count; i += 5)
            {
                if (!missions.Contains(values[i])) continue;
                if (values[i + 2] <= 0 || values[i + 3] is <= 0 or > 10000 || values[i + 4] <= 0)
                    throw new NotSupportedException("强袭通关奖励数值未确认: " + values[i]);
                if (!result.TryAdd(values[i], new(values[i], values[i + 1], values[i + 2], values[i + 3], values[i + 4])))
                    throw new InvalidDataException("强袭通关奖励重复: " + values[i]);
            }
        }
        return result.ToImmutableDictionary();
    }

    public AssaultModeDailyOpen? Day(int day) => DailyOpens.FirstOrDefault(row => row.Day == day);

    public bool IsMission(int parent, int mission) => Missions.TryGetValue(parent, out var rows) && rows.Contains(mission);

    public bool TryGetRequirement(int mission, out AssaultModeMissionRequirement? requirement, out string? error)
    {
        var lazy = requirements.GetOrAdd(mission, id => new(() =>
        {
            if (readRequirement is null) return (null, "requirement-reader-unavailable");
            try { return (readRequirement(id), null); }
            catch (Exception failure) when (failure is InvalidDataException or NotSupportedException or FileNotFoundException)
            { return (null, failure.Message); }
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        (requirement, error) = lazy.Value;
        return requirement is not null;
    }

    internal static AssaultModeCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        Func<int, AssaultModeMissionRequirement>? readRequirement = null)
    {
        var missions = new Dictionary<int, List<int>>();
        int? hidden = null;
        var daily = new List<AssaultModeDailyOpen>();
        bool missionList = false;
        for (int at = 0; at < tokens.Count;)
        {
            if (tokens[at].Type != 3) { at++; continue; }
            string tag = resolve(tokens[at++].Value).Trim().ToLowerInvariant();
            switch (tag)
            {
                case "[mission info list]":
                {
                    if (missionList) throw new InvalidDataException("强袭 mission info list 重复");
                    missionList = true;
                    var values = Ints(tokens, ref at);
                    Close(tokens, ref at, resolve, "[/mission info list]");
                    if (values.Count == 0 || values.Count % 3 != 0) throw new InvalidDataException("强袭 mission info list 不是三元组");
                    for (int i = 0; i < values.Count; i += 3)
                    {
                        // 第三列全部为0，含义待确认；非0时拒绝而非猜测。
                        if (values[i] <= 0 || values[i + 1] <= 0 || values[i + 2] != 0)
                            throw new NotSupportedException("强袭 mission info list 第三列非0，含义待确认");
                        if (!missions.TryGetValue(values[i], out var rows)) missions.Add(values[i], rows = []);
                        if (rows.Contains(values[i + 1])) throw new InvalidDataException("强袭任务副本重复");
                        rows.Add(values[i + 1]);
                    }
                    break;
                }
                case "[hidden base dungeon]":
                {
                    var values = Ints(tokens, ref at);
                    if (hidden is not null || values is not [> 0]) throw new InvalidDataException("强袭 hidden base dungeon 格式无效");
                    hidden = values[0];
                    break;
                }
                case "[daily open dungeon]":
                    daily.Add(ReadDaily(tokens, ref at, resolve));
                    break;
            }
        }
        if (!missionList || hidden is null || daily.Count == 0) throw new InvalidDataException("强袭模式资源缺少必需段");
        if (daily.Select(row => row.Day).Distinct().Count() != daily.Count || daily.Any(row => row.Day is < 0 or > 6))
            throw new InvalidDataException("强袭 daily open dungeon 的 day 重复或越界");
        return new(missions.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray()), hidden.Value,
            daily.OrderBy(row => row.Day).ToImmutableArray(), readRequirement);
    }

    static AssaultModeDailyOpen ReadDaily(IReadOnlyList<PvfToken> tokens, ref int at, Func<int, string> resolve)
    {
        int? day = null, hidden = null;
        ImmutableArray<int>? dungeons = null;
        while (at < tokens.Count)
        {
            if (tokens[at].Type != 3) throw new InvalidDataException("强袭 daily open dungeon 含游离值");
            string tag = resolve(tokens[at++].Value).Trim().ToLowerInvariant();
            if (tag == "[/daily open dungeon]")
            {
                if (day is null || hidden is null || dungeons is null) throw new InvalidDataException("强袭 daily open dungeon 字段不全");
                return new(day.Value, dungeons.Value, hidden.Value);
            }
            var values = Ints(tokens, ref at);
            switch (tag)
            {
                case "[day]" when day is null && values is [_]: day = values[0]; break;
                case "[hidden dungeon]" when hidden is null && values is [> 0]: hidden = values[0]; break;
                case "[dungeon]" when dungeons is null && values.Count > 0 && values.All(v => v > 0):
                    dungeons = values.ToImmutableArray();
                    Close(tokens, ref at, resolve, "[/dungeon]");
                    break;
                default: throw new NotSupportedException("强袭 daily open dungeon 未知字段: " + tag);
            }
        }
        throw new InvalidDataException("强袭 daily open dungeon 未闭合");
    }

    static List<int> Ints(IReadOnlyList<PvfToken> tokens, ref int at)
    {
        var values = new List<int>();
        while (at < tokens.Count && tokens[at].Type != 3)
        {
            if (tokens[at].Type != 0) throw new InvalidDataException("强袭模式资源出现非整数值");
            values.Add(tokens[at++].Value);
        }
        return values;
    }

    static void Close(IReadOnlyList<PvfToken> tokens, ref int at, Func<int, string> resolve, string expected)
    {
        if (at >= tokens.Count || tokens[at].Type != 3 || resolve(tokens[at].Value).Trim().ToLowerInvariant() != expected)
            throw new InvalidDataException("强袭模式资源缺少 " + expected);
        at++;
    }

    // 任务副本必须声明 [dungeon type] `assaultmode dungeon`、最低等级与唯一的 [required item] itemId count flag。
    // flag(第三列)PVF 均为1，含义待确认；不据此改变扣除行为。
    internal static AssaultModeMissionRequirement ReadRequirement(DungeonDefinition definition)
    {
        if (!string.Equals(definition.DungeonType?.Trim(), MissionDungeonType, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("副本不是 assaultmode dungeon: " + definition.Id);
        if (definition.MinimumLevel is not > 0)
            throw new InvalidDataException("强袭任务副本缺少最低等级: " + definition.Id);
        var items = definition.RawSections.Where(row => row.Tag == "required item").ToArray();
        if (items is not [{ Tokens: [{ Type: 0, Value: > 0 } item, { Type: 0, Value: > 0 } count, { Type: 0 } flag] }])
            throw new NotSupportedException("强袭任务副本 required item 格式未确认: " + definition.Id);
        return new(definition.Id, definition.MinimumLevel!.Value, item.Value, count.Value, flag.Value);
    }
}
