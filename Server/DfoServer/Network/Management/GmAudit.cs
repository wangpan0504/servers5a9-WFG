using System.Text;
using System.Text.Json;

namespace DfoServer.Network.Management;

// GM 网页写操作的审计记录：每次写操作（含失败）追加一行 JSON 到数据目录的 gm-audit.jsonl，不写 game.db。
internal sealed record GmAuditEntry(DateTimeOffset Time, string Operator, string Action, string Target, string Command,
    bool Ok, string Before, string After, string Result);

internal sealed class GmAudit(string path)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly object gate = new();

    public string Path => path;

    public void Append(GmAuditEntry entry)
    {
        string line = JsonSerializer.Serialize(entry with
        {
            Before = Cut(entry.Before), After = Cut(entry.After), Result = Cut(entry.Result), Command = Cut(entry.Command)
        }, Json);
        lock (gate)
        {
            string? directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
        }
    }

    // 最近的记录在前；损坏的行跳过。
    public GmAuditEntry[] Recent(int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        string[] lines;
        lock (gate)
        {
            if (!File.Exists(path)) return [];
            // 只读文件尾部（约 2MB），长期运行后审计文件很大时也不会整份读入内存；尾部开头可能是半行，解析失败会被跳过。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const long Tail = 2 * 1024 * 1024;
            if (stream.Length > Tail) stream.Seek(-Tail, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        var rows = new List<GmAuditEntry>();
        for (int i = lines.Length - 1; i >= 0 && rows.Count < limit; i--)
        {
            try { if (JsonSerializer.Deserialize<GmAuditEntry>(lines[i], Json) is { } row && Complete(row)) rows.Add(row); }
            catch (JsonException) { }
        }
        return [.. rows];
    }

    // 反序列化会把 JSON 的显式 null 写进非空字段（外部工具或手改文件留下的占位行），
    // 这类行不是本类写入的记录，按损坏行跳过，不能让筛选和导出在空引用上失败。
    static bool Complete(GmAuditEntry row) =>
        row.Operator is not null && row.Action is not null && row.Target is not null && row.Command is not null &&
        row.Before is not null && row.After is not null && row.Result is not null;

    // 按账号（对象以“账号/角色”开头）、动作、时间范围、结果过滤；最多 5000 条，供网页筛选与导出使用。
    public GmAuditEntry[] Query(int limit, string? account, string? action, DateTimeOffset? from, DateTimeOffset? to, bool? ok)
    {
        limit = Math.Clamp(limit, 1, 5000);
        string[] lines;
        lock (gate)
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const long Tail = 16 * 1024 * 1024;
            if (stream.Length > Tail) stream.Seek(-Tail, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        var rows = new List<GmAuditEntry>();
        for (int i = lines.Length - 1; i >= 0 && rows.Count < limit; i--)
        {
            try
            {
                if (JsonSerializer.Deserialize<GmAuditEntry>(lines[i], Json) is not { } row || !Complete(row)) continue;
                if (!string.IsNullOrEmpty(account) && !row.Target.StartsWith(account + "/", StringComparison.Ordinal) && row.Target != account) continue;
                if (!string.IsNullOrEmpty(action) && !row.Action.Equals(action, StringComparison.Ordinal)) continue;
                if (from is { } f && row.Time < f) continue;
                if (to is { } t && row.Time > t) continue;
                if (ok is { } o && row.Ok != o) continue;
                rows.Add(row);
            }
            catch (JsonException) { }
        }
        return [.. rows];
    }

    public static string ToCsv(IEnumerable<GmAuditEntry> rows)
    {
        static string Q(string v)
        {
            // 防 CSV 公式注入：以 = + - @ 开头的单元格前加单引号。
            if (v.Length > 0 && "=+-@\t\r".Contains(v.TrimStart()is{Length:>0}t?t[0]:v[0])) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        var sb = new StringBuilder("\uFEFF时间,操作者,操作,对象,结果,命令,修改前,修改后,返回\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(",", Q(r.Time.ToString("yyyy-MM-dd HH:mm:ss zzz")), Q(r.Operator), Q(r.Action), Q(r.Target), Q(r.Ok ? "成功" : "失败"), Q(r.Command), Q(r.Before), Q(r.After), Q(r.Result))).Append("\r\n");
        return sb.ToString();
    }

    static string Cut(string value) => value.Length <= 400 ? value : value[..400] + "…";
}