using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Events;

internal sealed record EventResourceEntry(string Path, string? Name, string? NameToken, string Kind,
    string Description, bool SupportsControl, string ControlReason, ImmutableArray<int> DeclaredEventIds, string? ReadError);

// 只读运营目录；仅管理页面首次访问时扫描并缓存，不在启动、战斗、心跳或玩家锁内扫描。
internal sealed class EventResourceCatalog(PvfArchive archive)
{
    readonly Lazy<ImmutableArray<EventResourceEntry>> entries = new(() => Scan(archive));

    public object Page(int offset, int limit, string? query, string kind)
    {
        var all = entries.Value;
        var matched = all.Where(entry => (kind == "all" || entry.Kind == kind) &&
            (string.IsNullOrWhiteSpace(query) || entry.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new
        {
            schemaVersion = 1, source = "当前服务端已加载的PVF", resourceCount = all.Length,
            definitionCount = all.Count(entry => entry.Kind == "definition"),
            componentCount = all.Count(entry => entry.Kind == "component"),
            directoryCount = all.Count(entry => entry.Kind == "directory"), listCount = all.Count(entry => entry.Kind == "list"),
            readFailureCount = all.Count(entry => entry.ReadError is not null), total = matched.Length, offset, limit,
            hasMore = (long)offset + limit < matched.Length, items = matched.Skip(offset).Take(limit).ToArray()
        };
    }

    static ImmutableArray<EventResourceEntry> Scan(PvfArchive archive)
    {
        var rows = ImmutableArray.CreateBuilder<EventResourceEntry>();
        foreach (var file in archive.Files.Where(file =>
            file.Path.StartsWith("event/", StringComparison.Ordinal) &&
                (file.Path.EndsWith(".evt", StringComparison.Ordinal) || file.Path.EndsWith(".etc", StringComparison.Ordinal) ||
                 file.Path.EndsWith(".lst", StringComparison.Ordinal)) || file.Path.EndsWith(".evt", StringComparison.Ordinal))
            .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<int>();
            string? name = null, nameToken = null, error = null;
            try
            {
                var tokens = archive.ReadTokens(file.Path);
                string tag = "";
                foreach (var token in tokens)
                {
                    if (token.Type == 3)
                    {
                        tag = archive.ResolveString(token.Value);
                        tags.Add(tag);
                    }
                    else if (token.Type == 0 && tag == "[event index]" && token.Value is > 0 and <= ushort.MaxValue)
                        ids.Add(token.Value);
                    else if (token.Type == 6 && tag is "[title]" or "[name]" or "[event name]" or "[window title]" or "[event title]")
                    {
                        string text = archive.ResolveString(token.Value).Trim();
                        if (text.Length is < 1 or > 120 || text.Contains('\n') || text.Contains('\r')) continue;
                        if (text.Any(value => value is >= '\u4e00' and <= '\u9fff')) name ??= text;
                        else nameToken ??= text;
                    }
                }
            }
            catch (Exception failure) when (failure is InvalidDataException or IOException or NotSupportedException)
            {
                error = failure.Message;
            }
            string kind = Kind(file.Path, tags);
            rows.Add(new(file.Path, name, nameToken, kind, Description(kind, tags), false,
                "资源条目没有可直接使用的开关，已有业务请查看活动能力目录。", ids.Order().ToImmutableArray(), error));
        }
        return rows.ToImmutable();
    }

    static string Kind(string path, IReadOnlySet<string> tags)
    {
        if (path is "event/eventlist.etc" or "event/eventlistwindow.evt" or "event/eventnotice.evt" or "event/ingameeventlist.evt") return "directory";
        if (path.EndsWith(".lst", StringComparison.Ordinal)) return "list";
        if (new[] { "/activeobject/", "/player/", "/monster/", "/bullet/", "/action/", "/skill/" }.Any(path.Contains) ||
            path.Contains("/escaperoomevent/", StringComparison.Ordinal) && !path.Contains("/clue.", StringComparison.Ordinal) ||
            path.StartsWith("worldmap/", StringComparison.Ordinal) || tags.Contains("[max hp]") && tags.Contains("[move animation path]")) return "component";
        return "definition";
    }

    static string Description(string kind, IReadOnlySet<string> tags)
    {
        if (kind == "component") return "活动使用的角色、怪物、场景或表现组件，不能作为独立活动开启。";
        if (kind is "directory" or "list") return "活动展示或资源索引目录，条目编号不能直接作为可用活动开关。";
        var parts = new List<string>();
        if (tags.Any(tag => tag.Contains("reward", StringComparison.Ordinal) || tag.Contains("basic items", StringComparison.Ordinal))) parts.Add("奖励配置");
        if (tags.Any(tag => tag.Contains("mail", StringComparison.Ordinal) || tag.Contains("postal", StringComparison.Ordinal))) parts.Add("奖励邮件");
        if (tags.Any(tag => tag.Contains("level", StringComparison.Ordinal))) parts.Add("参加等级");
        if (tags.Any(tag => tag.Contains("time", StringComparison.Ordinal) || tag.Contains("period", StringComparison.Ordinal))) parts.Add("时间条件");
        if (tags.Any(tag => tag.Contains("dungeon", StringComparison.Ordinal) || tag.Contains("dgn", StringComparison.Ordinal))) parts.Add("地下城条件");
        if (tags.Any(tag => tag.Contains("buff", StringComparison.Ordinal))) parts.Add("增益效果");
        if (tags.Any(tag => tag.Contains("ui", StringComparison.Ordinal) || tag.Contains("window", StringComparison.Ordinal))) parts.Add("活动界面");
        return (parts.Count == 0 ? "保存活动参数" : "包含" + string.Join("、", parts)) + "；仅有资源不能证明玩法和领奖已接通。";
    }
}
