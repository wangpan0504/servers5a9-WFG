using System.Collections.Immutable;

namespace PvfLib;

// 怪物十整数与阶段六字段保持原序；不把未恢复的时序字段解释成掉落或普通MAP怪物行。
public sealed record PvfAltarMonster(ImmutableArray<int> Values);
public sealed record PvfAltarPhase(int Round, int MonsterOrdinal, int Time, float RawFactor,
    int SpawnOrder, int GroupCount, byte Difficulty);
public sealed record PvfMapAltarWaves(bool Ultimate, ImmutableArray<PvfAltarMonster> Monsters,
    ImmutableArray<PvfAltarPhase> Phases);

public static class MapAltarWaves
{
    public static PvfMapAltarWaves? Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        string[] names = ["blood monster", "blood phase time", "ultimate monster", "ultimate phase time"];
        var counts = new int[4];
        var ends = new int[4];
        foreach (var token in tokens)
        {
            if (token.Type != 3) continue;
            string tag = resolve(token.Value).ToLowerInvariant();
            int index = tag switch
            {
                "[blood monster]" => 0, "[blood phase time]" => 1,
                "[ultimate monster]" => 2, "[ultimate phase time]" => 3,
                "[/blood monster]" => 4, "[/blood phase time]" => 5,
                "[/ultimate monster]" => 6, "[/ultimate phase time]" => 7, _ => -1
            };
            if (index >= 4) ends[index - 4]++;
            else if (index >= 0) counts[index]++;
        }
        if (counts.All(value => value == 0) && ends.All(value => value == 0)) return null;
        bool ultimate = counts[2] != 0 || counts[3] != 0;
        int first = ultimate ? 2 : 0;
        for (int i = 0; i < names.Length; i++)
        {
            int expected = i == first || i == first + 1 ? 1 : 0;
            if (counts[i] != expected || ends[i] != expected)
                throw new InvalidDataException("祭坛怪物与阶段块必须成对、唯一且完整闭合");
        }
        var sections = new PvfTokenDocument(tokens, resolve, preserveExtraEndTags: true).Sections;
        var monsters = Block(names[first]);
        var phases = Block(names[first + 1]);
        if (monsters.Length == 0 || monsters.Length % 10 != 0 || monsters.Any(token => token.Type != 0))
            throw new InvalidDataException("祭坛怪物行必须为完整十整数");
        var monsterRows = ImmutableArray.CreateBuilder<PvfAltarMonster>(monsters.Length / 10);
        for (int at = 0; at < monsters.Length; at += 10)
            monsterRows.Add(new(monsters.Skip(at).Take(10).Select(token => token.Value).ToImmutableArray()));

        // Full03 04277964/04277B50读取十整数与 int/int/int/float/int/int；极限另读A..E并映射1..5。
        int width = ultimate ? 7 : 6;
        if (phases.Length == 0 || phases.Length % width != 0)
            throw new InvalidDataException("祭坛阶段行被截断或为空");
        var phaseRows = ImmutableArray.CreateBuilder<PvfAltarPhase>(phases.Length / width);
        for (int at = 0; at < phases.Length; at += width)
        {
            for (int column = 0; column < 6; column++)
                if (column == 3 ? phases[at + column].Type is not (0 or 2) : phases[at + column].Type != 0)
                    throw new InvalidDataException("祭坛阶段字段类型错误");
            float factor = phases[at + 3].Type == 2
                ? BitConverter.Int32BitsToSingle(phases[at + 3].Value) : phases[at + 3].Value;
            if (!float.IsFinite(factor) || phases[at + 1].Value < 0 || phases[at + 1].Value >= monsterRows.Count)
                throw new InvalidDataException("祭坛阶段浮点数或怪物索引无效");
            byte difficulty = 0;
            if (ultimate)
            {
                if (phases[at + 6].Type != 6 || resolve(phases[at + 6].Value) is not [>= 'A' and <= 'E'] letter)
                    throw new InvalidDataException("极限祭坛难度标签必须为A至E");
                difficulty = (byte)(letter[0] - 'A' + 1);
            }
            phaseRows.Add(new(phases[at].Value, phases[at + 1].Value, phases[at + 2].Value, factor,
                phases[at + 4].Value, phases[at + 5].Value, difficulty));
        }
        return new(ultimate, monsterRows.MoveToImmutable(), phaseRows.MoveToImmutable());

        ImmutableArray<PvfToken> Block(string name)
        {
            var matched = sections.Where(row => row.Name == name).ToArray();
            if (matched is not [{ IsBlock: true, Children.Length: 0 } section])
                throw new InvalidDataException("祭坛声明不在地图顶层或包含未解析条件");
            return section.Values;
        }
    }
}
