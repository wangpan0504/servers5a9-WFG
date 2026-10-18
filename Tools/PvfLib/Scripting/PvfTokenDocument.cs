using System.Collections.Immutable;

namespace PvfLib;

// 保留声明顺序、重复字段和嵌套块；不决定游戏字段的默认值或覆盖关系。
public sealed record PvfTokenSection(string Name, ImmutableArray<PvfToken> Values,
    ImmutableArray<PvfTokenSection> Children, bool IsBlock = false);

public sealed class PvfTokenDocument
{
    public ImmutableArray<PvfTokenSection> Sections { get; }
    public ImmutableArray<string> Diagnostics { get; }

    public PvfTokenDocument(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        IReadOnlyDictionary<string, string>? blockEndAliases = null, bool preserveExtraEndTags = false)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resolve);
        var blocks = tokens.Where(token => token.Type == 3).Select(token => resolve(token.Value))
            .Where(tag => tag.StartsWith("[/", StringComparison.Ordinal) && tag.EndsWith(']'))
            .Select(tag => tag[2..^1].Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        int at = 0;
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var closedBlocks = new HashSet<string>(StringComparer.Ordinal);
        Sections = Read(null, 0);
        Diagnostics = diagnostics.ToImmutable();

        ImmutableArray<PvfTokenSection> Read(string? end, int depth)
        {
            if (depth > 32)
                throw new InvalidDataException("PVF声明嵌套超过32层");
            var rows = ImmutableArray.CreateBuilder<PvfTokenSection>();
            while (at < tokens.Count)
            {
                if (tokens[at].Type != 3)
                    throw new InvalidDataException("PVF声明存在无标签token");
                string tag = resolve(tokens[at++].Value);
                if (tag.Length < 3 || tag[0] != '[' || tag[^1] != ']')
                    throw new InvalidDataException("PVF声明标签无效");
                string name = tag[1..^1].Trim().ToLowerInvariant();
                if (name.StartsWith('/'))
                {
                    string closed = name[1..];
                    bool matches = end is not null && (closed == end ||
                        blockEndAliases?.TryGetValue(end, out var aliasEnd) == true && closed == aliasEnd);
                    if (matches)
                    {
                        closedBlocks.Add(end!);
                        return rows.ToImmutable();
                    }
                    // 内层块漏了闭合（如 skill node 里的 attack info），把这个闭合交还给外层。
                    if (end is not null)
                    {
                        at--;
                        diagnostics.Add("缺少闭合标签: " + end);
                        return rows.ToImmutable();
                    }
                    // 同一块已经正常闭合过，文件末尾再多一个闭合（礼盒脚本常见）只记诊断。
                    // preserveExtraEndTags 的调用方（任务）仍要把多余闭合留成独立段。
                    if (!preserveExtraEndTags && closedBlocks.Contains(closed))
                    {
                        diagnostics.Add("多余闭合标签: " + name);
                        continue;
                    }
                    if (preserveExtraEndTags)
                    {
                        diagnostics.Add("多余闭合标签: " + name);
                        rows.Add(new(name, [], []));
                        continue;
                    }
                    throw new InvalidDataException("PVF声明闭合标签不匹配: " + name);
                }
                int start = at;
                while (at < tokens.Count && tokens[at].Type != 3)
                    at++;
                var values = tokens.Skip(start).Take(at - start).ToImmutableArray();
                string? blockEnd = blocks.Contains(name) || blockEndAliases?.ContainsKey(name) == true ? name : null;
                var children = blockEnd is not null ? Read(blockEnd, depth + 1) : [];
                rows.Add(new(name, values, children, blockEnd is not null));
            }
            if (end is not null)
                throw new InvalidDataException("PVF声明块未闭合: " + end);
            return rows.ToImmutable();
        }
    }
}
