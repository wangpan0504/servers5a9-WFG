using System.Security.Cryptography;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Protocol;

internal enum NativeObservationResult { Added, Duplicate, Conflict }

// 每条认证Native租约独占，仅诊断；不作为伤害、设施或奖励的去重凭证。
internal sealed class NativeStateObservation(TimeProvider? clock = null)
{
    public const int Capacity = 256;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly Queue<Entry> entries = new();
    readonly Dictionary<Key, Entry> index = new();
    readonly object gate = new();
    long observations, duplicates, conflicts;
    sealed record Key(DungeonRunIdentity Run, DungeonMap Room, byte Kind, uint Sequence, byte Source);
    sealed record Entry(Key Key, long Timestamp, byte[] Digest, ushort ObjectKind, ushort Instance, byte State, int ValueCount);

    public NativeObservationResult Observe(DungeonRunIdentity run, DungeonMap room, NativeGameplayRecord record,
        NativeObjectState state)
    {
        var digest = SHA256.HashData(record.Wire.Span);
        var key = new Key(run, room, record.Kind, record.Sequence, record.SourceSlot);
        lock (gate)
        {
            long now = time.GetTimestamp();
            Prune(now);
            if (index.TryGetValue(key, out var previous))
            {
                if (previous.Digest.AsSpan().SequenceEqual(digest)) { duplicates++; return NativeObservationResult.Duplicate; }
                conflicts++;
                return NativeObservationResult.Conflict;
            }
            if (entries.Count == Capacity) index.Remove(entries.Dequeue().Key);
            var entry = new Entry(key, now, digest, state.ObjectKind, state.Instance, state.State, state.Values.Length);
            entries.Enqueue(entry);
            index.Add(key, entry);
            observations++;
            return NativeObservationResult.Added;
        }
    }

    void Prune(long now)
    {
        while (entries.TryPeek(out var oldest) && time.GetElapsedTime(oldest.Timestamp, now) >= Lifetime)
            index.Remove(entries.Dequeue().Key);
    }

    public (long Added, long Duplicates, long Conflicts, int Retained) Snapshot()
    {
        lock (gate)
        {
            Prune(time.GetTimestamp());
            return (observations, duplicates, conflicts, entries.Count);
        }
    }
}
