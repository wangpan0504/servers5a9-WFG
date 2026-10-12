using System.Collections.Immutable;

namespace PvfLib;

public sealed record PvfTutorialFlow(string Job, int DungeonId, int AfterTutorial, string IntroPath,
    int Town, int Area, int X, int Y);
public readonly record struct PvfTutorialReward(int Progress, int ItemId, int Count);

public static class TutorialFile
{
    public static ImmutableArray<PvfTutorialReward> Rewards(PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfLib.Scripting.PvfTokenFields(tokens, resolve, ["escalade tutorial reward"]);
        if (!fields.TryGet("escalade tutorial reward", out var values) || values.Length == 0 || values.Length % 3 != 0)
            throw new InvalidDataException("训练奖励字段缺失、重复或不成三元组");
        var result = ImmutableArray.CreateBuilder<PvfTutorialReward>();
        for (int at = 0; at < values.Length; at += 3)
        {
            if (values[at].Type != 0 || values[at + 1].Type != 0 || values[at + 2].Type != 0)
                throw new InvalidDataException("训练奖励不是完整整数声明");
            result.Add(new(values[at].Value, values[at + 1].Value, values[at + 2].Value));
        }
        return result.ToImmutable();
    }

    public static ImmutableArray<PvfTutorialFlow> Flows(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenDocument(tokens, resolve).Sections;
        var result = ImmutableArray.CreateBuilder<PvfTutorialFlow>();
        foreach (var flow in fields)
        {
            if (flow.Name != "tutorial flow" || flow.Values is not [{ Type: 6 }] || flow.Children.Length != 3)
                throw new InvalidDataException("训练流程声明或职业字段无效");
            var dungeon = Field(flow, "dungeon index");
            var intro = Field(flow, "intro");
            var town = Field(flow, "go town");
            if (intro.Values.Length != 0 || intro.Children.Length != 2 || town.Values.Length != 0 || town.Children.Length != 2)
                throw new InvalidDataException("训练影片或回城声明无效");
            int[] destination = Integers(Field(town, "town"), 2), position = Integers(Field(town, "startpos"), 2);
            var path = Field(intro, "intro path");
            if (path.Values is not [{ Type: 6 }] || path.Children.Length != 0)
                throw new InvalidDataException("训练影片路径不是单字符串");
            result.Add(new(resolve(flow.Values[0].Value), Integers(dungeon, 1)[0], Integers(Field(intro, "after tutorial"), 1)[0],
                resolve(path.Values[0].Value), destination[0], destination[1], position[0], position[1]));
        }
        if (result.Count == 0 || result.Select(row => row.Job).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidDataException("训练流程为空或职业重复");
        return result.ToImmutable();
    }

    static PvfTokenSection Field(PvfTokenSection parent, string name)
    {
        var matches = parent.Children.Where(row => row.Name == name).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("训练字段缺失或重复: " + name);
    }
    static int[] Integers(PvfTokenSection field, int count)
    {
        if (field.Children.Length != 0 || field.Values.Length != count || field.Values.Any(token => token.Type != 0))
            throw new InvalidDataException("训练字段整数数量或类型无效: " + field.Name);
        return field.Values.Select(token => token.Value).ToArray();
    }
}
