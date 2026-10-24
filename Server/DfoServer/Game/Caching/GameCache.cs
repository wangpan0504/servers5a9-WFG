using Microsoft.Extensions.Caching.Memory;

namespace DfoServer.Game.Caching;

// 槽位由模块静态声明，不接受玩家输入作为动态缓存键。
internal sealed class GameCacheSlot<T> where T : class;

// Runtime 持有一份底座；分区只保存原业务对象，不复制在线状态或参与资产事务。
internal sealed class GameCache : IDisposable
{
    readonly MemoryCache entries = new(new MemoryCacheOptions());
    int disposed;

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
    internal int Count => IsDisposed ? 0 : entries.Count;

    public GameCacheScope CreateScope() => new(this);
    public GameCacheIndex<TKey, TValue> CreateIndex<TKey, TValue>() where TKey : notnull where TValue : class => new(this);

    internal TValue? Read<TValue>(object key) where TValue : class =>
        !IsDisposed && entries.TryGetValue(key, out TValue? value) ? value : null;

    internal void Write<TValue>(object key, TValue value) where TValue : class
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // 有效业务上下文不能因 TTL 或容量压力静默消失；生命周期由所属业务显式结束。
        entries.Set(key, value, new MemoryCacheEntryOptions { Priority = CacheItemPriority.NeverRemove });
    }

    internal void Remove(object key)
    {
        if (!IsDisposed) entries.Remove(key);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // 只在监听、管理入口和连接全部结束后释放共享底座。
        entries.Clear();
        entries.Dispose();
    }
}

internal sealed class GameCacheScope : IDisposable
{
    readonly GameCacheIndex<object, object> entries;

    internal GameCacheScope(GameCache cache) => entries = cache.CreateIndex<object, object>();
    internal int Count => entries.Count;
    public T? Get<T>(GameCacheSlot<T> slot) where T : class => entries.TryGetValue(slot, out var value) ? (T)value : null;
    public void Set<T>(GameCacheSlot<T> slot, T value) where T : class => entries.Set(slot, value);
    public void Remove<T>(GameCacheSlot<T> slot) where T : class => entries.Remove(slot);
    public void Clear() => entries.Clear();
    public void Dispose() => entries.Dispose();
}
