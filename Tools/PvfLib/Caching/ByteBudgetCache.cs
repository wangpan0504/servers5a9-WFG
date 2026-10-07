namespace PvfLib.Caching;

// 解码在锁外执行；同键并发共享一次加载，淘汰只影响缓存，不撤销调用方正在使用的值。
internal sealed class ByteBudgetCache<TKey, TValue>(long capacity, Func<TValue, long> measure) where TKey : notnull
{
    private readonly object gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Size)>> values = new();
    private readonly LinkedList<(TKey Key, TValue Value, long Size)> recent = new();
    private readonly Dictionary<TKey, Lazy<TValue>> pending = new();
    private long bytes;
    private int generation;

    internal int Count { get { lock (gate) return values.Count; } }
    internal long Bytes { get { lock (gate) return bytes; } }

    internal bool TryGet(TKey key, out TValue value)
    {
        lock (gate)
        {
            if (values.TryGetValue(key, out var node))
            {
                recent.Remove(node);
                recent.AddLast(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    internal TValue GetOrAdd(TKey key, Func<TKey, TValue> load)
    {
        Lazy<TValue> loading;
        int version;
        lock (gate)
        {
            if (TryGet(key, out var cached))
                return cached;
            if (!pending.TryGetValue(key, out loading!))
                pending.Add(key, loading = new(() => load(key)));
            version = generation;
        }

        try
        {
            TValue value = loading.Value;
            long size = Math.Max(64, measure(value));
            lock (gate)
            {
                if (version != generation)
                    return value;
                if (!values.ContainsKey(key) && size <= capacity)
                {
                    while (size > capacity - bytes)
                    {
                        var oldest = recent.First!;
                        bytes -= oldest.Value.Size;
                        values.Remove(oldest.Value.Key);
                        recent.RemoveFirst();
                    }
                    values.Add(key, recent.AddLast((key, value, size)));
                    bytes += size;
                }
            }
            return value;
        }
        finally
        {
            lock (gate)
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, loading))
                    pending.Remove(key);
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            generation++;
            values.Clear();
            recent.Clear();
            pending.Clear();
            bytes = 0;
        }
    }
}
