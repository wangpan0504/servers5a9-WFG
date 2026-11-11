using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace DfoServer.Persistence;

// 引导候战这一次提交的数据库预算，和普通存档的 30 秒命令超时分开：
// - 写闸门只尝试 0 毫秒，进程内已有写者就立刻 SQLITE_BUSY。
// - sqlite busy_timeout 固定 0。BEGIN IMMEDIATE 被外部写锁挡住就立刻返回，不进入 RetryWhileBusy 的 150 毫秒睡眠。
// - 一次尝试最多：一次闸门尝试、一次 BEGIN、一次 UPDATE、一次 COMMIT 或 ROLLBACK。
// - 重试次数和退避在连接外的 PreyGuideWaitingRetry，这里不睡觉。
// 调用方仍要在 owner 内做身份复核，并且只有 COMMIT 成功才改内存、才允许发布 24。
internal static class SqliteImmediate
{
    internal const int BusyBudgetMilliseconds = 0;

    internal static void BeginImmediate(SqliteConnection connection)
    {
        Step(connection, "BEGIN IMMEDIATE", static _ => { }, raw.SQLITE_DONE);
    }

    internal static int UpdateTown(SqliteConnection connection, AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint)
    {
        int changes = 0;
        Step(connection, """
            UPDATE characters SET revision=revision+1,town=$town,area=$area,x=$x,y=$y,direction=$direction,
              return_town=$rtown,return_area=$rarea,return_x=$rx,return_y=$ry,return_direction=$rdirection
            WHERE id=$id AND account=$account AND revision=$revision
            """, stmt =>
        {
            Bind(stmt, "$town", position.Town);
            Bind(stmt, "$area", position.Area);
            Bind(stmt, "$x", position.X);
            Bind(stmt, "$y", position.Y);
            Bind(stmt, "$direction", position.Direction);
            Bind(stmt, "$rtown", returnPoint?.Town);
            Bind(stmt, "$rarea", returnPoint?.Area);
            Bind(stmt, "$rx", returnPoint?.X);
            Bind(stmt, "$ry", returnPoint?.Y);
            Bind(stmt, "$rdirection", returnPoint?.Direction);
            Bind(stmt, "$id", expected.Id);
            Bind(stmt, "$account", owner.Account);
            Bind(stmt, "$revision", expected.Revision);
        }, raw.SQLITE_DONE);
        changes = raw.sqlite3_changes(connection.Handle);
        return changes;
    }

    internal static void Commit(SqliteConnection connection) =>
        Step(connection, "COMMIT", static _ => { }, raw.SQLITE_DONE);

    internal static void Rollback(SqliteConnection connection)
    {
        try { Step(connection, "ROLLBACK", static _ => { }, raw.SQLITE_DONE); }
        catch (SqliteException) { }
    }

    // 读锁也用同一笔 0 毫秒预算。探到忙就不再进入会按 30 秒重试的 LoadCharacter。
    internal static void ProbeRead(SqliteConnection connection) =>
        Step(connection, "SELECT 1", static _ => { }, raw.SQLITE_ROW);

    static void Bind(sqlite3_stmt stmt, string name, int value) =>
        raw.sqlite3_bind_int(stmt, Index(stmt, name), value).Throw(stmt);

    static void Bind(sqlite3_stmt stmt, string name, int? value)
    {
        int index = Index(stmt, name);
        int rc = value is { } found ? raw.sqlite3_bind_int(stmt, index, found) : raw.sqlite3_bind_null(stmt, index);
        rc.Throw(stmt);
    }

    static void Bind(sqlite3_stmt stmt, string name, long value) =>
        raw.sqlite3_bind_int64(stmt, Index(stmt, name), value).Throw(stmt);

    static void Bind(sqlite3_stmt stmt, string name, string value) =>
        raw.sqlite3_bind_text(stmt, Index(stmt, name), value).Throw(stmt);

    static int Index(sqlite3_stmt stmt, string name)
    {
        int index = raw.sqlite3_bind_parameter_index(stmt, name);
        if (index == 0) throw new InvalidOperationException("缺少参数 " + name);
        return index;
    }

    static void Step(SqliteConnection connection, string sql, Action<sqlite3_stmt> bind, int expected)
    {
        int rc = raw.sqlite3_prepare_v2(connection.Handle, sql, out sqlite3_stmt stmt, out _);
        if (rc != raw.SQLITE_OK) Throw(connection, rc);
        try
        {
            bind(stmt);
            rc = raw.sqlite3_step(stmt);
            if (rc == expected) return;
            Throw(connection, rc);
        }
        finally
        {
            raw.sqlite3_finalize(stmt);
        }
    }

    static void Throw(this int rc, sqlite3_stmt stmt)
    {
        if (rc == raw.SQLITE_OK) return;
        throw new SqliteException("SQLite Error " + rc + ": '" + raw.sqlite3_errmsg(raw.sqlite3_db_handle(stmt)).utf8_to_string() + "'.", rc);
    }

    static void Throw(SqliteConnection connection, int rc) =>
        throw new SqliteException("SQLite Error " + rc + ": '" + raw.sqlite3_errmsg(connection.Handle).utf8_to_string() + "'.", rc);
}
