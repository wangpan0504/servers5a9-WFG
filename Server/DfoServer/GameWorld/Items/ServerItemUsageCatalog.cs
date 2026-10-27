using System.Collections.Immutable;
using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ServerItemLimit(byte Type, int ItemId, int Maximum);
internal sealed record ServerItemReset(int ItemId, DateTime Until);
internal sealed record ServerItemRefill(int ItemId, int Count, DateTime Until, int MinimumLevel = 0);
internal sealed record ServerItemMail(int ItemId, int Count, int IntervalDays, int SubjectCode, int MessageCode,
    int MinimumLevel, int MaximumLevel, DateTime Until);

// 当前格式的新增列采用明确本服政策：补给第四列为最低等级；邮件为间隔日、消息键及等级区间。
internal sealed class ServerItemUsageCatalog
{
    public const string Path = "etc/chn_server_limititemusageinfo.etc";
    public static ServerItemUsageCatalog Empty { get; } = new([], [], [], [], []);
    public ImmutableArray<ServerItemLimit> Limits { get; }
    public ImmutableArray<ServerItemReset> Resets { get; }
    public ImmutableArray<ServerItemRefill> Refills { get; }
    public ImmutableArray<ServerItemRefill> AccountRefills { get; }
    public ImmutableArray<ServerItemMail> Mails { get; }

    internal ServerItemUsageCatalog(ImmutableArray<ServerItemLimit> limits, ImmutableArray<ServerItemReset> resets,
        ImmutableArray<ServerItemRefill> refills, ImmutableArray<ServerItemRefill> accountRefills, ImmutableArray<ServerItemMail> mails)
    { Limits = limits; Resets = resets; Refills = refills; AccountRefills = accountRefills; Mails = mails; }

    public static ServerItemUsageCatalog Load(PvfArchive archive) => archive.Contains(Path)
        ? Parse(new(archive.ReadTokens(Path), archive.ResolveString), archive.ResolveString) : Empty;

    internal static ServerItemUsageCatalog Parse(PvfTokenDocument document, Func<int, string> resolve)
    {
        if (!document.Diagnostics.IsEmpty || document.Sections.Any(field => field.Children.Length != 0 ||
            field.Name is not ("limit item usage info" or "reset item" or "refill item" or "account refill item" or "charac daily item mail")) ||
            document.Sections.Select(field => field.Name).Distinct().Count() != document.Sections.Length)
            throw new InvalidDataException("服务端物品限次配置包含未知或重复声明");
        PvfToken[] Values(string name) => document.Sections.SingleOrDefault(field => field.Name == name)?.Values.ToArray() ?? [];
        static int Integer(PvfToken token, int minimum, int maximum = int.MaxValue) =>
            token.Type == 0 && token.Value >= minimum && token.Value <= maximum ? token.Value :
                throw new InvalidDataException("服务端物品限次字段范围无效");
        DateTime Time(PvfToken token) => token.Type == 6 && DateTime.TryParseExact(resolve(token.Value), "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? DateTime.SpecifyKind(date, DateTimeKind.Unspecified) :
                throw new InvalidDataException("服务端物品限次日期无效");
        static void Width(PvfToken[] rows, int count)
        { if (rows.Length % count != 0) throw new InvalidDataException("服务端物品限次行宽无效"); }
        var limits = ImmutableArray.CreateBuilder<ServerItemLimit>();
        var rows = Values("limit item usage info"); Width(rows, 3);
        for (int at = 0; at < rows.Length; at += 3)
        {
            byte type = (byte)Integer(rows[at], 0, 3);
            int id = Integer(rows[at + 1], type == 3 ? 0 : 1);
            if (type == 3 && id != 0) throw new InvalidDataException("普通邮件限次使用物品键0");
            limits.Add(new(type, id, Integer(rows[at + 2], 0)));
        }
        if (limits.Select(row => (row.Type, row.ItemId)).Distinct().Count() != limits.Count)
            throw new InvalidDataException("服务端物品限次键重复");
        var resets = ImmutableArray.CreateBuilder<ServerItemReset>();
        rows = Values("reset item"); Width(rows, 2);
        for (int at = 0; at < rows.Length; at += 2) resets.Add(new(Integer(rows[at], 1), Time(rows[at + 1])));
        ImmutableArray<ServerItemRefill> RefillsFor(string name)
        {
            var values = Values(name); var list = ImmutableArray.CreateBuilder<ServerItemRefill>();
            // 旧端三列仍兼容；本版第四列0表示不附加等级门槛。
            for (int at = 0; at < values.Length;)
            {
                if (values.Length - at < 3) throw new InvalidDataException("服务端物品补给行截断");
                int id = Integer(values[at++], 1), count = Integer(values[at++], 0);
                var until = Time(values[at++]);
                bool nextRow = at + 2 < values.Length && values[at + 2].Type == 6;
                int level = !nextRow && at < values.Length && values[at].Type == 0 && values[at].Value is >= 0 and <= 255
                    ? Integer(values[at++], 0, 255) : 0;
                list.Add(new(id, count, until, level));
            }
            if (list.Select(row => row.ItemId).Distinct().Count() != list.Count)
                throw new InvalidDataException("服务端物品补给键重复");
            return list.ToImmutable();
        }
        var mail = ImmutableArray.CreateBuilder<ServerItemMail>();
        rows = Values("charac daily item mail"); Width(rows, 8);
        for (int at = 0; at < rows.Length; at += 8)
        {
            var entry = new ServerItemMail(Integer(rows[at], 1), Integer(rows[at + 1], 1), Integer(rows[at + 2], 1, 36500),
                Integer(rows[at + 3], 0), Integer(rows[at + 4], 0), Integer(rows[at + 5], 0, 255),
                Integer(rows[at + 6], 0, 255), Time(rows[at + 7]));
            if (entry.MaximumLevel != 0 && entry.MaximumLevel < entry.MinimumLevel) throw new InvalidDataException("每日邮件等级范围无效");
            mail.Add(entry);
        }
        if (mail.Select(row => row.ItemId).Distinct().Count() != mail.Count) throw new InvalidDataException("每日邮件键重复");
        return new(limits.ToImmutable(), resets.ToImmutable(), RefillsFor("refill item"), RefillsFor("account refill item"), mail.ToImmutable());
    }
}
