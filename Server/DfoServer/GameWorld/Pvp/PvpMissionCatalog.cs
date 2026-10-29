using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Pvp;

internal sealed record PvpMissionDefinition(ushort Id, byte Grade, byte Category, int MinimumRank, int MaximumRank,
    int Parent, string Condition, bool TotalOnly, byte? Job, int Parameter, int Target)
{
    public ImmutableArray<PvpMissionReward> Rewards { get; init; } = [];
    public int Group { get; init; } = -1;
    public ImmutableArray<byte> Jobs { get; init; } = [];
}
internal readonly record struct PvpMissionReward(int ItemId, int Count);

// MSN任务书独立于风云QST；定义来自客户端同一索引，不能把训练任务编号当作目标任务。
internal sealed class PvpMissionCatalog
{
    public ImmutableArray<PvpMissionDefinition> Definitions { get; }
    public ImmutableArray<PvpMissionDefinition> DailyDefinitions { get; }

    public PvpMissionCatalog(PvfArchive pvf)
    {
        var files = Dungeons.DungeonCatalog.ParseDirectory(pvf.ReadTokens("pvp_mission/mission.lst"), pvf.ResolveString, "pvp_mission");
        var definitions = ImmutableArray.CreateBuilder<PvpMissionDefinition>();
        foreach (var (id, path) in files.OrderBy(row => row.Key))
            if (Read(id, new(pvf.ReadTokens(path), pvf.ResolveString), pvf.ResolveString) is { } definition)
                definitions.Add(definition);
        if (definitions.Count == 0 || definitions.Count > byte.MaxValue) throw Invalid("任务书条目数量");
        DailyDefinitions = definitions.Where(row => row.Grade is 2 or 4).ToImmutableArray();
        Definitions = definitions.Where(row => row.Grade is 0 or 3).ToImmutableArray();
        foreach (byte grade in new byte[] { 2, 4 })
            foreach (int group in new[] { 0, 1, 2 })
                if (!DailyDefinitions.Any(row => row.Grade == grade && row.Group == group)) throw Invalid("日常候选组缺失");
        var ids = Definitions.Select(row => (int)row.Id).ToHashSet();
        if (Definitions.Any(row => row.Parent != -1 && !ids.Contains(row.Parent))) throw Invalid("前置目标缺失");
    }

    internal static PvpMissionDefinition? Read(int id, PvfTokenDocument document, Func<int, string> resolve)
    {
        if (!document.Diagnostics.IsEmpty || id is <= 0 or > short.MaxValue) throw Invalid("索引或块结构");
        var gradeTokens = Single("grade").Values;
        if (gradeTokens.IsEmpty || gradeTokens[0].Type != 6) throw Invalid("任务级别字段");
        string gradeName = Normalize(resolve(gradeTokens[0].Value));
        byte grade = gradeName switch
        {
            "normal" => 0, "repeatable" => 1, "daily" => 2, "total match normal" => 3,
            "total match daily" => 4, "arad royal rumble daily" => 5, _ => throw Invalid("任务级别")
        };
        if (grade is not (0 or 2 or 3 or 4)) return null;
        bool daily = grade is 2 or 4;
        int group = -1;
        if (daily)
        {
            if (gradeTokens is not [{ Type: 6 }, { Type: 0, Value: >= 0 and <= 3 } groupValue]) throw Invalid("日常分组");
            group = groupValue.Value;
        }
        else if (gradeTokens.Length != 1) throw Invalid("任务书级别尾值");
        byte category = daily ? (byte)0 : (byte)(Label(Single("normal category").Values) switch
        { "training" => 0, "battle" => 2, "victory" => 3, "rating" => 4, _ => throw Invalid("分类") });
        var rank = Single("rank range").Values;
        if (rank is not [{ Type: 0 } minimum, { Type: 0 } maximum] || minimum.Value < 0 || maximum.Value < minimum.Value)
            throw Invalid("段位范围");
        var parentRows = document.Sections.Where(row => row.Name == "pre required mission").ToArray();
        int parent = parentRows.Length == 0 ? -1 : parentRows is [{ Values: [{ Type: 0 } value] }] ? value.Value :
            throw Invalid("前置目标");
        if (parent != -1 && (parent is <= 0 or > short.MaxValue || parent == id)) throw Invalid("前置目标身份");
        var condition = Single("condition").Values;
        if (condition.Length < 4 || condition[0].Type != 6 || condition[1].Type != 6 || condition.Skip(2).Any(row => row.Type != 0))
            throw Invalid("条件参数");
        var first = condition[2]; var second = condition[3];
        string name = Normalize(resolve(condition[0].Value)), mode = Normalize(resolve(condition[1].Value));
        // 只分配有服务端权威事实的日常；HP、浮空、姿势和斗兽场不借其它事实填充。
        if (daily && (group == 3 || name is not ("hereafter join" or "hereafter winning count" or "winning job" or "winning streak" or "kill count"))) return null;
        var jobs = ImmutableArray<byte>.Empty;
        if (name == "winning job")
        {
            if (!daily || second.Value <= 0 || condition.Length != 4 + second.Value || condition.Skip(4).Any(row => row.Value is < 0 or > 15))
                throw Invalid("指定职业列表");
            jobs = condition.Skip(4).Select(row => (byte)row.Value).ToImmutableArray();
        }
        else if (condition.Length != 4) throw Invalid("条件尾部");
        byte? job = null;
        bool total = false;
        int target;
        if (name == "combo clear")
        {
            job = mode switch
            {
                "swordman" => 0, "fighter" => 1, "gunner" => 2, "mage" => 3, "priest" => 4,
                "at gunner" => 5, "thief" => 6, "at fighter" => 7, "at mage" => 8,
                "demonic swordman" => 9, "at swordman" => 11, "knight" => 12, "demonic lancer" => 13,
                _ => throw Invalid("连招职业")
            };
            if (first.Value != 1 || second.Value is not (0 or 4 or 8)) throw Invalid("连招难度列表");
            target = 1;
        }
        else
        {
            if (mode is not ("all" or "total match mode")) throw Invalid("比赛范围");
            total = mode == "total match mode";
            target = name switch
            {
                "hereafter join" or "hereafter winning count" or "rank" or "colosseum win" => first.Value,
                "arcade mode" when first.Value is -1 or 2 => second.Value,
                "winning job" when daily => first.Value,
                "winning streak" when daily && first.Value > 0 => second.Value,
                "kill count" when daily && second.Value == 1 => first.Value,
                _ => throw Invalid("目标条件")
            };
            if (name is not ("arcade mode" or "winning job" or "winning streak" or "kill count") && second.Value != -1) throw Invalid("条件尾值");
        }
        if (target <= 0) throw Invalid("非正目标会被客户端误判完成");
        var rewards = ImmutableArray.CreateBuilder<PvpMissionReward>();
        if (document.Sections.Where(row => row.Name.StartsWith("reward ", StringComparison.Ordinal)).Any(row =>
            row.Name is not ("reward victory emblem" or "reward item")) || document.Sections
            .Where(row => row.Name is "reward victory emblem" or "reward item").GroupBy(row => row.Name).Any(group => group.Count() > 1))
            throw Invalid("未知或重复奖励字段");
        foreach (var section in document.Sections.Where(row => row.Name is "reward victory emblem" or "reward item"))
        {
            if (!section.Children.IsEmpty) throw Invalid("奖励嵌套");
            if (section.Name == "reward victory emblem")
            {
                // 015D71F4/015D721C固定物品10092640；胜利的证明不是胜点钱包。
                if (section.Values is not [{ Type: 0, Value: > 0 } count]) throw Invalid("胜利证明奖励");
                rewards.Add(new(10092640, count.Value));
            }
            else
            {
                if (section.Values.IsEmpty || section.Values.Length % 2 != 0) throw Invalid("物品奖励数量");
                for (int at = 0; at < section.Values.Length; at += 2)
                {
                    var item = section.Values[at]; var count = section.Values[at + 1];
                    if (item.Type != 0 || count.Type != 0 || item.Value <= 0 || count.Value <= 0) throw Invalid("物品奖励");
                    rewards.Add(new(item.Value, count.Value));
                }
            }
        }
        return new((ushort)id, grade, category, minimum.Value, maximum.Value, parent, name, total, job,
            name == "combo clear" ? second.Value : first.Value, target) { Rewards = rewards.ToImmutable(), Group = group, Jobs = jobs };

        PvfTokenSection Single(string field) => document.Sections.Where(row => row.Name == field).ToArray() is [var row] &&
            row.Children.IsEmpty ? row : throw Invalid("缺少或重复字段: " + field);
        string Label(ImmutableArray<PvfToken> tokens) => tokens is [{ Type: 6 } token]
            ? Normalize(resolve(token.Value)) : throw Invalid($"{id}文本字段({string.Join(',', tokens.Select(row => row.Type))})");
    }

    static string Normalize(string value) => value.Trim().Trim('[', ']').Trim().ToLowerInvariant();
    static InvalidDataException Invalid(string field) => new("决斗目标任务资源无效: " + field);
}
