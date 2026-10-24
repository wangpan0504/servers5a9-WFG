using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonAltarPhase(int Ordinal, int Round, byte Difficulty, int MonsterId,
    ushort X, ushort Y, ushort Z, ushort Scale, int InitialDelay, int Duration, ushort Interval,
    int InitialCount, int Increment, int Repetitions, int GroupCount)
{
    public int CountAt(int repetition) => checked(InitialCount + Increment * repetition);
    public int TotalCount => checked(Repetitions * InitialCount + Increment * Repetitions * (Repetitions - 1) / 2);
}

// 字段来自本版MAP解析器；时序与数量解释对照旧端CBattle_Field的BloodPhaseSpawn系列。
// 保留原始行号，绝不把祭坛十整数拼成普通怪物的八整数掉落声明。
internal sealed record DungeonAltarDefinition(bool Ultimate, ImmutableArray<DungeonAltarPhase> Phases)
{
    public int RoundCount => Phases[^1].Round + 1;

    public static DungeonAltarDefinition Read(PvfMapAltarWaves source)
    {
        if (source.Phases.IsDefaultOrEmpty) throw new InvalidDataException("祭坛阶段为空");
        var result = ImmutableArray.CreateBuilder<DungeonAltarPhase>(source.Phases.Length);
        long actorCount = 0;
        for (int ordinal = 0; ordinal < source.Phases.Length; ordinal++)
        {
            var phase = source.Phases[ordinal];
            if (phase.MonsterOrdinal < 0 || phase.MonsterOrdinal >= source.Monsters.Length)
                throw new NotSupportedException("祭坛随机模板索引尚未支持");
            var raw = source.Monsters[phase.MonsterOrdinal].Values;
            double scale = Math.Truncate((double)phase.RawFactor * 1000);
            if (raw.Length != 10 || raw[0] <= 0 || raw[1] != 0 ||
                raw[2] is < 0 or > ushort.MaxValue || raw[3] is < 0 or > ushort.MaxValue || raw[4] is < 0 or > ushort.MaxValue ||
                raw[5] <= 0 || raw[6] is <= 0 or >= ushort.MaxValue || raw[7] <= 0 || raw[8] < 0 || raw[9] is <= 0 or > ushort.MaxValue ||
                raw[5] - (long)raw[6] * raw[9] <= 0 || phase.Time <= 0 || phase.Round is < 0 or >= byte.MaxValue ||
                !double.IsFinite(scale) || scale is <= 0 or > short.MaxValue || phase.GroupCount is < 0 or > 10 ||
                (source.Ultimate ? phase.Difficulty is < 1 or > 5 : phase.Difficulty != 0))
                throw new InvalidDataException("祭坛阶段参数超出已确认范围");
            long count = (long)raw[9] * raw[7] + (long)raw[8] * raw[9] * (raw[9] - 1) / 2;
            actorCount += count;
            if (count > ushort.MaxValue || actorCount > ushort.MaxValue)
                throw new InvalidDataException("祭坛对象数量超出对象键容量");
            result.Add(new(ordinal, phase.Round, phase.Difficulty, raw[0], (ushort)raw[2], (ushort)raw[3],
                (ushort)raw[4], (ushort)scale, phase.Time, raw[5], (ushort)raw[6], raw[7], raw[8], raw[9], phase.GroupCount));
        }
        var phases = result.MoveToImmutable();
        if (!phases.Select(row => row.Round).Distinct().SequenceEqual(Enumerable.Range(0, phases[^1].Round + 1)) ||
            phases.Zip(phases.Skip(1)).Any(pair => pair.First.Round > pair.Second.Round ||
                pair.First.Round == pair.Second.Round && pair.First.Difficulty > pair.Second.Difficulty))
            throw new InvalidDataException("祭坛回合必须从零连续排列");
        for (int at = 0; at < phases.Length;)
        {
            var first = phases[at];
            int count = Math.Max(1, first.GroupCount);
            if (at + count > phases.Length || phases.Skip(at).Take(count).Any(row => row.Round != first.Round || row.Difficulty != first.Difficulty))
                throw new InvalidDataException("祭坛并发生成组越过回合或难度边界");
            at += count;
        }
        return new(source.Ultimate, phases);
    }
}
