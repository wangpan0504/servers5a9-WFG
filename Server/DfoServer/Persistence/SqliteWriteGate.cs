using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

/// <summary>
/// 进程内单写者闸门（写入准入）：SQLite 每个数据库文件同一时刻只允许一个 writer，
/// 因此进程内**所有写事务在 BEGIN 之前排队取闸门，提交或回滚后释放**。
///
/// 为什么必须有它：连接池已关闭（每次操作新开连接），多个会话各自开写事务时由 SQLite 自己裁决，
/// 竞争表现为 <c>SQLITE_BUSY</c> 后按 <c>busy_timeout=30000</c> 忙等——等成功也意味着该会话的处理循环
/// 被独占几十秒（杀怪不结算、回城无反应就是这么来的）。把竞争前移到闸门后，写事务变成严格串行，
/// 不再有第二个连接去撞写锁，忙等只剩"外部进程（GM 工具等）占用数据库"这一种来源。
///
/// 边界：
///   - 只串行化写；WAL 下读事务（<see cref="SqliteTransactions.BeginRead"/>）不取闸门，读者之间仍并发。
///   - 同一线程重入不排队：重入者通常是"外层已持有事务又调用另一个写入口"，此时再等自己的写锁
///     只会互等到超时，因此用可重入计数放行，让内层按 SQLite 自身的错误暴露问题（事务必须沿调用链传递）。
///   - 闸门等待不设上限：SQLite 的忙等上限只对 writer 竞争有意义，这里的排队由
///     <see cref="SqliteDbDiagnostics"/> 的 <c>gate_wait</c> / <c>write_transaction</c> 事件记录持有者。
/// </summary>
internal static class SqliteWriteGate
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    // 同一线程的重入深度。跨线程释放不受影响：租约自己记录是否真的取到了信号量，
    // 释放时按该标记决定 Release 还是仅递减计数（当前所有写入口都是同步代码，进入与释放同线程）。
    [ThreadStatic] static int depth;

    static int active;
    static int peak;

    /// <summary>当前正在运行的写事务数（正常情况下恒为 0 或 1）。</summary>
    internal static int ActiveWriters => Volatile.Read(ref active);

    /// <summary>自测用：自上次 <see cref="ResetPeak"/> 以来的最大并发写事务数。</summary>
    internal static int PeakWriters => Volatile.Read(ref peak);

    internal static bool HeldByCurrentThread => depth > 0;

    internal static void ResetPeak() => Volatile.Write(ref peak, Volatile.Read(ref active));

    internal static SqliteWriteLease Enter(SqliteConnection db, string action, bool deferred, string? sql)
    {
        long requested = Stopwatch.GetTimestamp();
        bool acquired = false;
        if (depth == 0)
        {
            Gate.Wait();
            acquired = true;
            depth = 1;
            int running = Interlocked.Increment(ref active);
            int high = Volatile.Read(ref peak);
            while (running > high && Interlocked.CompareExchange(ref peak, running, high) != high)
                high = Volatile.Read(ref peak);
            SqliteDbDiagnostics.Report("gate_wait", action, ElapsedMilliseconds(requested), sql);
        }
        else
        {
            depth++;
        }

        long entered = Stopwatch.GetTimestamp();
        try
        {
            return new SqliteWriteLease(db.BeginTransaction(deferred: deferred), action, sql, acquired, entered);
        }
        catch
        {
            // BEGIN 失败（外部 writer 占满忙等上限等）：闸门必须立刻归还，否则整个进程再也写不进去。
            SqliteDbDiagnostics.Report("begin_transaction", action, ElapsedMilliseconds(entered), sql);
            Release(acquired);
            throw;
        }
    }

    // 不等待。进程内已有写者时立刻失败，调用方把它当成 SQLITE_BUSY，不能在 owner 里排队。
    // 同一线程已持闸门时也失败：内层再 BEGIN 会自锁，不能借这次尝试把等待藏进去。
    internal static bool TryTake(string action, out IDisposable? held)
    {
        held = null;
        if (depth != 0 || !Gate.Wait(0)) return false;
        depth = 1;
        int running = Interlocked.Increment(ref active);
        int high = Volatile.Read(ref peak);
        while (running > high && Interlocked.CompareExchange(ref peak, running, high) != high)
            high = Volatile.Read(ref peak);
        SqliteDbDiagnostics.Report("gate_wait", action, 0, null);
        held = new GateHold();
        return true;
    }

    sealed class GateHold : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Release(true);
        }
    }

    internal static void Release(bool acquired)
    {
        if (acquired)
        {
            depth = 0;
            Interlocked.Decrement(ref active);
            Gate.Release();
            return;
        }
        depth = Math.Max(0, depth - 1);
    }

    internal static long ElapsedMilliseconds(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

/// <summary>
/// 一次写事务的准入租约：<see cref="Transaction"/> 是真正的事务，释放本对象即归还闸门并上报耗时。
/// 调用方声明顺序必须是 <c>db → write → transaction</c>，这样提交后按 transaction、write、db 逆序释放。
/// </summary>
internal sealed class SqliteWriteLease : IDisposable
{
    readonly string action;
    readonly string? sql;
    readonly bool acquired;
    readonly long entered;
    bool disposed;

    internal SqliteWriteLease(SqliteTransaction transaction, string action, string? sql, bool acquired, long entered)
    {
        Transaction = transaction;
        this.action = action;
        this.sql = sql;
        this.acquired = acquired;
        this.entered = entered;
    }

    internal SqliteTransaction Transaction { get; }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            // 先按"准入→释放"计时：这段就是本次写事务占用 writer 的完整时长，超阈值即记录持有者。
            SqliteDbDiagnostics.Report("write_transaction", action, SqliteWriteGate.ElapsedMilliseconds(entered), sql);
        }
        finally
        {
            SqliteWriteGate.Release(acquired);
        }
    }
}

/// <summary>
/// 仓储层的事务入口：写走 <see cref="BeginWrite"/>（取闸门），读走 <see cref="BeginRead"/>（不取闸门）。
/// 直接调用 <c>BeginTransaction</c> 只允许出现在本文件，便于用一条 grep 校验"没有漏网的写入口"。
/// </summary>
internal static class SqliteTransactions
{
    /// <summary>
    /// 开始一个写事务。调用方必须声明 <c>using var write = ...; using var transaction = write.Transaction;</c>，
    /// 事务由业务代码显式 <c>Commit()</c>，未提交即随 <c>using</c> 回滚。
    /// </summary>
    /// <param name="action">诊断用动作名（<c>类型.方法</c>），慢事务/排队超阈值时写入 <c>db_slow</c>。</param>
    /// <param name="deferred">true 表示先读后写（deferred），false 表示 BEGIN IMMEDIATE。</param>
    /// <param name="sql">可选的事务代表语句，仅用于把 <c>SqlHead</c> 记成真实 SQL 头部。</param>
    internal static SqliteWriteLease BeginWrite(this SqliteConnection db, string action, bool deferred = false, string? sql = null)
        => SqliteWriteGate.Enter(db, action, deferred, sql);

    /// <summary>开始一个只读事务：WAL 下读者互不阻塞，也不申请 writer，因此不取写闸门。</summary>
    internal static SqliteTransaction BeginRead(this SqliteConnection db) => db.BeginTransaction(deferred: true);
}
