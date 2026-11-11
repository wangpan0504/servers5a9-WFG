using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Events;

internal sealed record ReturningAdventurerReward(int ItemId, int Count);
internal sealed record ReturningAdventurerMission(int Id, int Level, string Type, ImmutableArray<int> Conditions,
    int Count, ReturningAdventurerReward Reward);

internal sealed class ReturningAdventurerCatalog
{
    public ReturningAdventurerReward Welcome { get; }
    public ReturningAdventurerReward Daily { get; }
    public ImmutableArray<ReturningAdventurerMission> Missions { get; }

    public ReturningAdventurerCatalog(PvfArchive pvf) : this(new(pvf.ReadTokens(AdventurerBenefitCatalog.ReturningPath), pvf.ResolveString),
        pvf.ResolveString) { }

    internal ReturningAdventurerCatalog(PvfTokenDocument document, Func<int, string> resolve)
    {
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("回归任务资源存在未闭合声明");
        var welcome = Numbers(Field(document.Sections, "welcome reward"));
        if (welcome.Length != 4) throw new InvalidDataException("欢迎奖励必须包含回归及新手两组奖励");
        // 03F39440先装+78/+7C，再装+7E/+82；03F29DC0按身份1/2选择第一/第二组，不能同时发放。
        Welcome = Reward(welcome[..2]);
        _ = Reward(welcome[2..]);
        Daily = Reward(Numbers(Field(document.Sections, "return daily reward")));
        var rows = ImmutableArray.CreateBuilder<ReturningAdventurerMission>();
        foreach (var section in Field(document.Sections, "return mission list").Children)
        {
            if (section.Name != "mission" || !section.Values.IsEmpty) throw new InvalidDataException("回归任务列表结构无效");
            int id = Scalar(section.Children, "id"), level = Scalar(section.Children, "level"), count = Scalar(section.Children, "count");
            var type = Field(section.Children, "type").Values;
            var conditions = Numbers(Field(section.Children, "condition"));
            if (id is < 1 or > 100 || rows.Any(row => row.Id == id) || level is < 1 or > 255 || count <= 0 ||
                type is not [{ Type: 6 }] || conditions.Length == 0 || conditions.Any(value => value < 0))
                throw new InvalidDataException("回归任务编号、门槛或条件无效");
            string kind = resolve(type[0].Value);
            if (kind is not ("quest clear - grade" or "quest clear - index" or "skill setting" or "join guild" or
                "dungeon clear" or "buff switching setting" or "buff switching equipment" or "level up" or "achieve antievil"))
                throw new InvalidDataException("未支持的原生回归任务类型: " + kind);
            rows.Add(new(id, level, kind, conditions.ToImmutableArray(), count,
                Reward(Numbers(Field(section.Children, "reward")))));
        }
        if (rows.Count is < 1 or > 100) throw new InvalidDataException("回归任务数量超出原生100槽");
        Missions = rows.OrderBy(row => row.Id).ToImmutableArray();
    }

    static ReturningAdventurerReward Reward(int[] values) => values is [> 0, > 0 and <= ushort.MaxValue]
        ? new(values[0], values[1]) : throw new InvalidDataException("回归奖励声明无效");
    static PvfTokenSection Field(ImmutableArray<PvfTokenSection> fields, string name) =>
        fields.Where(field => field.Name == name).ToArray() is [var field] ? field :
            throw new InvalidDataException("回归任务字段缺失或重复: " + name);
    static int[] Numbers(PvfTokenSection field) => field.Values.All(token => token.Type == 0)
        ? field.Values.Select(token => token.Value).ToArray() : throw new InvalidDataException("回归任务字段必须是整数: " + field.Name);
    static int Scalar(ImmutableArray<PvfTokenSection> fields, string name) => Numbers(Field(fields, name)) is [var value]
        ? value : throw new InvalidDataException("回归任务标量无效: " + name);
}
