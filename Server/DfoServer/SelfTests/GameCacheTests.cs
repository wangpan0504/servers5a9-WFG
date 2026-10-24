using DfoServer.Game.Caching;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GameCacheTests
{
    sealed record Context(int DungeonIndex, uint NextKey);

    public static void RunCore()
    {
        int checks = Count;
        var slot = new GameCacheSlot<Context>();
        var otherSlot = new GameCacheSlot<Context>();
        using var memory = new GameCache();
        using var first = memory.CreateScope();
        using var second = memory.CreateScope();
        var context = new Context(3, 300);
        That(first.Count == 0 && first.Get(slot) is null, "缓存未命中不创建业务上下文");
        first.Set(slot, context);
        That(ReferenceEquals(first.Get(slot), context) && first.Count == 1,
            "缓存保留原上下文引用，不克隆角色或副本状态");
        That(second.Get(slot) is null && first.Get(otherSlot) is null,
            "同类型槽位彼此隔离，不同会话缓存不共享运行状态");
        first.Set(otherSlot, new(5, 400));
        var replacement = new Context(5, 256);
        first.Set(slot, replacement);
        That(first.Count == 2 && ReferenceEquals(first.Get(slot), replacement) && first.Get(otherSlot) is not null,
            "替换当前实例不累计旧条目，不影响其它槽位");
        first.Remove(slot);
        first.Remove(slot);
        That(first.Get(slot) is null && first.Count == 1, "按槽位失效可重复调用，不误删其它功能");
        first.Clear();
        That(first.Count == 0 && first.Get(otherSlot) is null, "角色上下文清理释放本会话所有槽位");
        second.Set(slot, context);
        first.Dispose();
        first.Dispose();
        first.Clear();
        first.Remove(slot);
        That(first.Count == 0 && first.Get(slot) is null && ReferenceEquals(second.Get(slot), context),
            "旧会话重复收尾不影响其它会话且不能读出已释放缓存");
        Reject<ObjectDisposedException>(() => first.Set(slot, context), "已结束的会话不能复活缓存");
        SharedIndices();
        OwnersAndScopes();
        Console.WriteLine($"Game cache: {Count - checks} assertions passed");
    }

    static void SharedIndices()
    {
        using var memory = new GameCache();
        using var first = memory.CreateIndex<string, Context>();
        using var second = memory.CreateIndex<string, Context>();
        var original = new Context(3, 300);
        var replacement = new Context(3, 301);
        first.Add("account", original);
        second.Add("account", replacement);
        That(memory.Count == 2 && first.TryGetValue("account", out var found) && ReferenceEquals(found, original),
            "共享MemoryCache中的同名业务键按索引分区隔离，不复制值");
        Reject<ArgumentException>(() => first.Add("account", replacement), "Add仍拒绝覆盖已有账号注册");
        first.Set("account", replacement);
        That(!first.Remove("account", original) && !first.Remove("account", new Context(3, 301)) &&
            first.TryGetValue("account", out found) && ReferenceEquals(found, replacement),
            "旧引用或字段相同的复制对象不能移除已接管的索引值");
        That(first.SnapshotValues() is [var current] && ReferenceEquals(current, replacement) &&
            first.Remove("account", replacement) && first.Count == 0 && memory.Count == 1,
            "索引快照保留原对象，准确移除仅释放自身分区");
        using var concurrent = memory.CreateIndex<int, Context>();
        Parallel.For(0, 32, key => concurrent.Add(key, new(5, checked((uint)key))));
        That(concurrent.Count == 32 && concurrent.SnapshotValues().Length == 32 && memory.Count == 33,
            "并发索引注册保持条目和槽位数量一致");
        concurrent.Clear();
        That(memory.Count == 1 && second.Count == 1, "清空一个索引不删除另一索引的同名键");
        memory.Dispose();
        That(second.Count == 0 && !second.TryGetValue("account", out _) && second.SnapshotValues().Length == 0,
            "服务退出后所有分区不可读，不能保留第二份在线状态");
        Reject<ObjectDisposedException>(() => second.Set("account", original), "共享底座结束后不能恢复索引条目");
        Reject<ObjectDisposedException>(() => memory.CreateScope(), "已关闭的底座不能创建新会话分区");
    }

    static void OwnersAndScopes()
    {
        using var memory = new GameCache();
        var owners = new AccountOwners(memory);
        var slot = new GameCacheSlot<Context>();
        using var old = owners.Acquire("shared", () => { });
        using var oldScope = old.CreateCacheScope();
        oldScope.Set(slot, new(3, 300));
        That(owners.TrackedAccounts == 1 && memory.Count == 2, "账号注册和会话上下文进入同一缓存底座的不同分区");
        using var current = owners.Acquire("shared", () => { });
        using var currentScope = current.CreateCacheScope();
        var value = new Context(5, 500);
        currentScope.Set(slot, value);
        oldScope.Dispose();
        old.Dispose();
        That(!old.IsCurrent && current.IsCurrent && current.Generation > old.Generation && owners.TrackedAccounts == 1 &&
            ReferenceEquals(currentScope.Get(slot), value) && memory.Count == 2,
            "共享底座保留原generation规则，旧账号和会话释放不误删新owner或角色上下文");
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        var receipts = DungeonTransientReceipts.Get(current, identity);
        receipts.Fatigue[42] = new(CharacterFatigueState.NormalRoomRuleId, 1,
            CharacterFatigueState.NewOrdinary, CharacterFatigueState.NewOrdinary.ConsumeOrdinaryRoom());
        That(ReferenceEquals(DungeonTransientReceipts.Find(current, identity), receipts) &&
            !receipts.Fatigue[42].Committed && DungeonTransientReceipts.Find(old, identity) is null,
            "副本临时回执进入当前租约缓存，尚未提交的余额候选不标成功且不跨generation");
        DungeonTransientReceipts.Forget(current, identity);
        That(DungeonTransientReceipts.Find(current, identity) is null, "结束实例只释放本实例临时回执");
        currentScope.Dispose();
        current.Dispose();
        That(memory.Count == 0 && owners.TrackedAccounts == 0, "会话和账号各自结束后回收全部值与索引槽位");
        using var last = owners.Acquire("shutdown", () => { });
        memory.Dispose();
        That(!last.IsCurrent, "服务底座退出使剩余租约失效");
        Reject<InvalidOperationException>(() => last.Execute(() => true), "关闭后的租约不能继续业务写入");
    }
}
