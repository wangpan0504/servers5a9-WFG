using System.Diagnostics.CodeAnalysis;

namespace DfoServer.Game.Caching;

// 分区保留的只是业务键到缓存槽的定位信息；实际值只存在共享 MemoryCache 中。
internal sealed class GameCacheIndex<TKey, TValue> : IDisposable where TKey : notnull where TValue : class
{
    readonly GameCache cache;
    readonly object gate = new();
    readonly Dictionary<TKey, object> slots = new();
    bool disposed;

    internal GameCacheIndex(GameCache cache)
    {
        ObjectDisposedException.ThrowIf(cache.IsDisposed, cache);
        this.cache = cache;
    }

    internal int Count { get { lock (gate) return disposed || cache.IsDisposed ? 0 : slots.Count; } }

    public bool TryGetValue(TKey key, [NotNullWhen(true)] out TValue? value)
    {
        lock (gate)
        {
            value = !disposed && slots.TryGetValue(key, out var slot) ? cache.Read<TValue>(slot) : null;
            return value is not null;
        }
    }

    public void Set(TKey key, TValue value) => Store(key, value, false);
    public void Add(TKey key, TValue value) => Store(key, value, true);

    void Store(TKey key, TValue value, bool onlyNew)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || cache.IsDisposed, this);
            bool present = slots.TryGetValue(key, out var slot);
            if (onlyNew && present) throw new ArgumentException("缓存分区已有相同键", nameof(key));
            slot ??= new object();
            cache.Write(slot, value);
            if (!present) slots.Add(key, slot);
        }
    }

    public bool Remove(TKey key)
    {
        lock (gate)
        {
            if (disposed || !slots.Remove(key, out var slot)) return false;
            cache.Remove(slot);
            return true;
        }
    }

    // 比较和移除在同一分区锁内完成，旧连接不能移除新连接安装的值。
    public bool Remove(TKey key, TValue expected)
    {
        lock (gate)
        {
            if (disposed || !slots.TryGetValue(key, out var slot) || !ReferenceEquals(cache.Read<TValue>(slot), expected)) return false;
            slots.Remove(key);
            cache.Remove(slot);
            return true;
        }
    }

    public TValue[] SnapshotValues()
    {
        lock (gate)
        {
            if (disposed || cache.IsDisposed) return [];
            return slots.Values.Select(cache.Read<TValue>).OfType<TValue>().ToArray();
        }
    }

    public void Clear()
    {
        lock (gate) ClearLocked();
    }

    void ClearLocked()
    {
        foreach (var slot in slots.Values) cache.Remove(slot);
        slots.Clear();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            ClearLocked();
            disposed = true;
        }
    }
}
