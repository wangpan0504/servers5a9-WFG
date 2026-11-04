using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Dungeons;

internal enum AntonConstructorEffect : byte { Energy = 1, InfectionEnergy = 2, VolcanoSignal = 3 }
internal readonly record struct AntonConstructorLast(AntonConstructorEffect Effect, int Dungeon = 0);
internal sealed record AntonConstructorDeath(Guid Battle, Guid Run, long Generation, long RoomEpoch,
    AntonConstructorDefinition Definition, ushort MonsterKey, bool Infected);
internal sealed record AntonConstructorEffectFact(AntonConstructorDeath Death, AntonDungeonObjectScope Scope,
    AntonConstructorEffect Effect, long EventId);

// 每共享Run最多一个正式Constructor、三个Last等待和三个消费标记。死亡不自动执行Last。
internal sealed class AntonConstructorLedger(TimeProvider? timeProvider = null)
{
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    readonly Dictionary<AntonConstructorEffect, long> pending = [];
    readonly HashSet<AntonConstructorEffect> committed = [];
    AntonDungeonObjectScope? scope;
    AntonConstructorDefinition? definition;
    AntonConstructorDeath? death;
    internal static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(30);
    internal int PendingCount => pending.Count;

    internal void Open(AntonDungeonObjectScope next, AntonConstructorDefinition resource)
    {
        if (scope == next && definition == resource) return;
        bool sameRoom = scope is { } old && old.Battle == next.Battle && old.Run == next.Run &&
            old.Generation == next.Generation && old.RoomEpoch == next.RoomEpoch && definition == resource;
        scope = next; definition = resource; pending.Clear();
        if (!sameRoom) { death = null; committed.Clear(); }
    }

    internal bool Record(AntonConstructorDeath fact)
    {
        if (scope is not { } current || fact.Battle != current.Battle || fact.Run != current.Run ||
            fact.Generation != current.Generation || fact.RoomEpoch != current.RoomEpoch ||
            fact.Definition != definition || fact.MonsterKey == 0 || death is not null && death != fact) return false;
        death = fact;
        return true;
    }

    internal bool Report(AntonDungeonObjectScope current, AntonConstructorLast report)
    {
        if (scope != current || definition is null || !Enum.IsDefined(report.Effect) ||
            report.Effect == AntonConstructorEffect.VolcanoSignal && report.Dungeon != definition.Dungeon ||
            report.Effect != AntonConstructorEffect.VolcanoSignal && report.Dungeon != 0 ||
            report.Effect == AntonConstructorEffect.InfectionEnergy && death is { Infected: false }) return false;
        Prune();
        if (committed.Contains(report.Effect) || pending.ContainsKey(report.Effect)) return false;
        pending.Add(report.Effect, clock.GetTimestamp());
        return true;
    }

    internal int Apply(AntonDungeonObjectScope current, Func<AntonConstructorEffectFact, bool> commit)
    {
        if (scope != current || death is null) return 0;
        Prune();
        int applied = 0;
        foreach (var effect in pending.Keys.ToArray())
        {
            if (effect == AntonConstructorEffect.InfectionEnergy && !death.Infected) { pending.Remove(effect); continue; }
            var fact = new AntonConstructorEffectFact(death, current, effect, EventId(death.MonsterKey, effect));
            if (!commit(fact)) continue;
            committed.Add(effect); pending.Remove(effect); applied++;
        }
        return applied;
    }

    internal static long EventId(ushort key, AntonConstructorEffect effect) => ((long)key << 32) | (byte)effect;
    void Prune()
    {
        foreach (var key in pending.Where(row => clock.GetElapsedTime(row.Value) > PairWindow).Select(row => row.Key).ToArray())
            pending.Remove(key);
    }
}
