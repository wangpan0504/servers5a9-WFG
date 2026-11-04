namespace DfoServer.Game.Raids;

// 安徒恩对象事件只在服务端确认身份后进入账本。
// EventId 的具体来源由匹配版本事件合同决定；本模型不把客户端 662 的任意字段解释成对象身份。
internal enum AntonRaidEventKind : byte
{
    ObjectDestroyed = 1,
    HatcheryOpened = 2,
    EnergySettled = 3
}

internal readonly record struct AntonRaidEventKey(
    Guid InstanceId, Guid RunId, long RunGeneration, int Phase, int Dungeon, long EventId)
{
    internal bool Valid => InstanceId != Guid.Empty && RunId != Guid.Empty && RunGeneration >= 0 &&
        Phase >= 0 && Dungeon is 218 or 221 or 222 or 223 or 224 && EventId > 0;
}

// 有界、幂等的正式事件账本。旧 Run、旧 generation 和重复事件都不能影响新状态。
internal sealed class AntonRaidEventLedger(int capacity = 1024)
{
    readonly object gate = new();
    readonly HashSet<(AntonRaidEventKey Key, AntonRaidEventKind Kind)> committed = [];
    readonly Dictionary<Guid, AntonRaidEventKey> activeRuns = [];

    internal int Count { get { lock (gate) return committed.Count; } }

    internal bool Contains(AntonRaidEventKey key, AntonRaidEventKind kind)
    {
        lock (gate) return committed.Contains((key, kind));
    }

    internal bool Activate(AntonRaidEventKey scope)
    {
        if (!scope.Valid) return false;
        lock (gate)
        {
            var normalized = scope with { EventId = 0 };
            if (activeRuns.TryGetValue(scope.RunId, out var current)) return current == normalized;
            if (activeRuns.Count >= capacity) return false;
            activeRuns.Add(scope.RunId, normalized);
            return true;
        }
    }

    internal bool TryCommit(AntonRaidEventKey key, AntonRaidEventKind kind)
    {
        if (!key.Valid || !Enum.IsDefined(kind)) return false;
        lock (gate)
        {
            if (!activeRuns.TryGetValue(key.RunId, out var scope) ||
                scope.InstanceId != key.InstanceId || scope.RunGeneration != key.RunGeneration ||
                scope.Phase != key.Phase || scope.Dungeon != key.Dungeon) return false;
            if (committed.Contains((key, kind))) return false;
            if (committed.Count >= capacity) return false;
            committed.Add((key, kind));
            return true;
        }
    }

    internal int RemoveRun(Guid runId)
    {
        if (runId == Guid.Empty) return 0;
        lock (gate)
        {
            int removed = committed.RemoveWhere(row => row.Key.RunId == runId);
            activeRuns.Remove(runId);
            return removed;
        }
    }
}
