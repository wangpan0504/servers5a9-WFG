using System.Collections.Immutable;

namespace PvfLib;

// 保留声明顺序、重复字段和嵌套块；不决定游戏字段的默认值或覆盖关系。
public sealed record PvfTokenSection(string Name, ImmutableArray<PvfToken> Values, ImmutableArray<PvfTokenSection> Children);

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
                    if (end is null && preserveExtraEndTags)
                    {
                        diagnostics.Add("多余闭合标签: " + name);
                        rows.Add(new(name, [], []));
                        continue;
                    }
                    if (name[1..] != end && !(end is not null && blockEndAliases?.TryGetValue(end, out var aliasEnd) == true && name[1..] == aliasEnd))
                        throw new InvalidDataException("PVF声明闭合标签不匹配: " + name);
                    return rows.ToImmutable();
                }
                int start = at;
                while (at < tokens.Count && tokens[at].Type != 3)
                    at++;
                var values = tokens.Skip(start).Take(at - start).ToImmutableArray();
                string? blockEnd = blocks.Contains(name) || blockEndAliases?.ContainsKey(name) == true ? name : null;
                var children = blockEnd is not null ? Read(blockEnd, depth + 1) : [];
                rows.Add(new(name, values, children));
            }
            if (end is not null)
                throw new InvalidDataException("PVF声明块未闭合: " + end);
            return rows.ToImmutable();
        }
    }
}
