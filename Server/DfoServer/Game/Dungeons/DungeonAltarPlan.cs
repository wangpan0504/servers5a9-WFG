using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonAltarActor(ushort ObjectKey, DungeonAltarPhase Phase, int Repetition,
    byte Kind, byte Rank, byte Level);
internal sealed record DungeonAltarBatch(long DelayMilliseconds, ushort Interval, ImmutableArray<DungeonAltarActor> Actors);
internal sealed record DungeonAltarRound(int Round, byte Difficulty, ImmutableArray<DungeonAltarBatch> Batches);

// 入场锁外冻结全部键和各难度时间线；选择难度不重抽对象，也不为未选分支产生死亡或奖励。
internal sealed record DungeonAltarPlan(DungeonAltarDefinition Definition, ImmutableArray<DungeonAltarRound> Rounds,
    ImmutableArray<DungeonAltarActor> Actors, uint NextObjectKey)
{
    public static DungeonAltarPlan Create(DungeonAltarDefinition definition, ushort characterId, uint firstKey, int level,
        bool bossRoom, Func<int, int> draw)
    {
        if (level is < 1 or > byte.MaxValue || firstKey is < 1 or > ushort.MaxValue)
            throw new InvalidDataException("祭坛等级或对象键无效");
        uint next = firstKey;
        var actors = ImmutableArray.CreateBuilder<DungeonAltarActor>();
        var rounds = ImmutableArray.CreateBuilder<DungeonAltarRound>();
        int specialRound = definition.Ultimate ? -1 : Draw(bossRoom ? definition.RoundCount - 1 : definition.RoundCount);
        bool specialAssigned = false;
        foreach (var round in definition.Phases.GroupBy(row => (row.Round, row.Difficulty)))
        {
            var rows = round.ToArray();
            var batches = new List<DungeonAltarBatch>();
            long groupStart = 0;
            for (int at = 0; at < rows.Length;)
            {
                int groupCount = Math.Max(1, rows[at].GroupCount);
                long lastSpawn = -1;
                DungeonAltarPhase? lastPhase = null;
                foreach (var phase in rows.AsSpan(at, groupCount))
                {
                    for (int repeat = 0; repeat < phase.Repetitions; repeat++)
                    {
                        long delay = checked(groupStart + phase.InitialDelay + (long)repeat * phase.Interval);
                        var batch = ImmutableArray.CreateBuilder<DungeonAltarActor>(phase.CountAt(repeat));
                        bool finalRow = phase.Ordinal == definition.Phases.Length - 1;
                        ushort interval = finalRow ? bossRoom ? ushort.MaxValue : (ushort)0 : phase.Interval;
                        for (int i = 0; i < phase.CountAt(repeat); i++)
                        {
                            if (next == characterId) next++;
                            if (next > ushort.MaxValue) throw new InvalidDataException("祭坛对象键耗尽");
                            byte kind = 19;
                            if (!specialAssigned && phase.Round == specialRound)
                            {
                                int roll = Draw(100);
                                kind = (byte)(roll < 40 ? Draw(5) : roll < 70 ? Draw(6) + 5 : roll < 85 ? Draw(4) + 11 :
                                    roll < 95 ? Draw(3) + 15 : 18);
                                specialAssigned = true;
                            }
                            var actor = new DungeonAltarActor((ushort)next++, phase, repeat, kind,
                                interval == ushort.MaxValue ? (byte)3 : (byte)0, (byte)level);
                            batch.Add(actor);
                            actors.Add(actor);
                        }
                        batches.Add(new(delay, interval, batch.MoveToImmutable()));
                        if (delay >= lastSpawn) { lastSpawn = delay; lastPhase = phase; }
                    }
                }
                // 最后完成的并发行决定下一组间隔；所有数量与时序都来自MAP，不采用固定波数或固定刷怪秒数。
                groupStart = checked(lastSpawn + lastPhase!.Duration - (long)lastPhase.Repetitions * lastPhase.Interval);
                at += groupCount;
            }
            rounds.Add(new(round.Key.Round, round.Key.Difficulty, batches.OrderBy(batch => batch.DelayMilliseconds).ToImmutableArray()));
        }
        return new(definition, rounds.ToImmutable(), actors.ToImmutable(), next);

        int Draw(int bound)
        {
            if (bound <= 0) return -1;
            int value = draw(bound);
            return value >= 0 && value < bound ? value : throw new InvalidDataException("祭坛随机源越界");
        }
    }
}
