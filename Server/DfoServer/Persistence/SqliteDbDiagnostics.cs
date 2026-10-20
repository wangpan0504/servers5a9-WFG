using System.Text;

namespace DfoServer.Persistence;

/// <summary>
/// 数据库慢操作诊断出口：一次数据库操作超过 1000ms 就写一条 <c>db_slow</c> 事件，控制台按 WARN 输出
/// （事件筛选与等级在 <see cref="DfoServer.Infrastructure.ServerLog"/>）。
///
/// 为什么需要它：忙等（<c>busy_timeout=30000</c>）等成功时不留任何记录，事后只能看到"某连接停了 30 秒"，
/// 无法回答是谁占着 writer。这里按事务/准入排队两个阶段计时，直接把 Action 和耗时写进结构化日志。
///
/// 阈值与本文件的关系见[数据库](../../../../Docs/开发/数据库.md#写入串行化与慢-sql-诊断)与
/// [并发、事务与响应延迟](../../../../Docs/规范/并发事务与响应延迟.md#事务与数据库等待)。
/// </summary>
internal static class SqliteDbDiagnostics
{
    /// <summary>慢操作阈值：正常单次业务读写在个位数毫秒，超过该值即有人在写事务里做了重活或扫了大表。</summary>
    internal const long SlowThresholdMilliseconds = 1000;

    /// <summary>日志只保留 SQL 头部：定位到语句即可，不把整条 SQL 与绑定值写进日志。</summary>
    internal const int SqlHeadLength = 80;

    static Action<string, object>? sink;

    /// <summary>
    /// 装配入口（应用入口/Runtime）把 <c>ServerLog.Write</c> 挂上来；自测可临时替换成收集器，用完还原。
    /// 未装配时不产生任何输出，仓储在纯离线场景（导入、测试夹具）保持静默。
    /// </summary>
    internal static Action<string, object>? Sink
    {
        get => sink;
        set => sink = value;
    }

    /// <summary>把一条 SQL 压成单行并截断到 <see cref="SqlHeadLength"/> 个字符；空串记为 <c>none</c>。</summary>
    internal static string Head(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "none";
        var builder = new StringBuilder(SqlHeadLength + 1);
        foreach (string line in sql.Split('\n'))
        {
            string text = line.Trim();
            if (text.Length == 0)
                continue;
            if (builder.Length != 0 && builder.Length < SqlHeadLength)
                builder.Append(' ');
            int room = SqlHeadLength - builder.Length;
            builder.Append(room >= text.Length ? text : text[..room]);
            if (builder.Length >= SqlHeadLength)
                break;
        }
        return builder.Length == 0 ? "none" : builder.ToString();
    }

    /// <summary>
    /// 上报一次数据库操作耗时；未达阈值直接返回，调用方可以在热路径上无条件调用（只付一次单调时钟读数）。
    /// <paramref name="connection"/> 只在调用方已有连接编号时填写，缺失记 <c>null</c>（控制台显示 none）。
    /// </summary>
    internal static void Report(string stage, string action, long elapsedMilliseconds, string? sql = null, int? connection = null)
    {
        if (elapsedMilliseconds < SlowThresholdMilliseconds || sink is null)
            return;
        sink("db_slow", new
        {
            Stage = stage,
            Action = action,
            ElapsedMs = elapsedMilliseconds,
            SqlHead = Head(sql),
            Connection = connection
        });
    }
}
