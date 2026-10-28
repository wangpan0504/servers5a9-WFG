using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record EvildomWarHiddenEncounter(uint StageDungeon, uint Dungeon);

// 本服抽样规则：每场按万分权重决定一次，命中后等概率替换钥匙或魔法阵图；五图面板保持原合同。
internal static class EvildomWarHiddenPolicy
{
    public const string Rule = "evildom-war-local-hidden-v2";
    public const int Denominator = 10_000;

    public static EvildomWarHiddenEncounter? Select(byte mode, ImmutableArray<EvildomWarDungeonState> stages,
        ImmutableArray<EvildomWarHiddenDungeon> hidden, Func<int, int> draw)
    {
        var definition = hidden.Single(row => row.Mode == mode);
        var eligible = stages.Where(row => row.Kind == 0 && row.Attribute < 2).ToArray();
        if (definition.RawWeight is < 0 or > Denominator || eligible.Length != 2 || eligible.Select(row => row.Attribute).Distinct().Count() != 2)
            throw new InvalidDataException("魔界本服隐藏权重或前置图数量无效");
        int roll = Draw(Denominator);
        return roll < definition.RawWeight
            ? new(checked((uint)eligible[Draw(eligible.Length)].Id), checked((uint)definition.Id)) : null;

        int Draw(int maximum)
        {
            int value = draw(maximum);
            return value >= 0 && value < maximum ? value : throw new InvalidDataException("魔界隐藏随机值越界");
        }
    }
}
