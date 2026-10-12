using System.Collections.Immutable;

namespace PvfLib;

public readonly record struct EmotionExpressionDefinition(int Id, int Kind, bool Visible);

public static class EmotionExpressionFile
{
    public static ImmutableArray<EmotionExpressionDefinition> Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var blocks = new PvfTokenDocument(tokens, resolve).Sections.Where(s => s.Name == "emotion info").ToArray();
        if (blocks.Length != 1 || blocks[0].Children.Length != 0)
            throw new InvalidDataException("心情普通目录缺失或重复");
        var values = blocks[0].Values;
        var result = ImmutableArray.CreateBuilder<EmotionExpressionDefinition>();
        var ids = new HashSet<int>();
        int at = 0;
        while (at < values.Length)
        {
            int id = Integer(), kind = Integer(), visible = Integer();
            if (id is < 0 or > short.MaxValue || kind is < 0 or > 7 || visible is not (0 or 1) || !ids.Add(id))
                throw new InvalidDataException("心情编号、类型或显示声明无效");
            if (kind == 4) _ = Integer();
            if (kind == 5)
            {
                int count = Integer();
                if (count is < 0 or > 30000 || count > values.Length - at)
                    throw new InvalidDataException("心情任务声明数量越界");
                for (int i = 0; i < count; i++) _ = Integer();
            }
            _ = Text();
            _ = Text();
            for (int i = 0; i < 3; i++)
                if (Integer() is not (0 or 1)) throw new InvalidDataException("心情开关不是布尔值");
            _ = Text();
            int shortcuts = 0;
            while (Text() != "$END") shortcuts++;
            if (shortcuts == 0) throw new InvalidDataException("心情快捷指令缺失");
            result.Add(new(id, kind, visible == 1));
        }
        return result.ToImmutable();

        int Integer()
        {
            if (at >= values.Length || values[at].Type != 0) throw new InvalidDataException("心情整数声明缺失");
            return values[at++].Value;
        }
        string Text()
        {
            if (at >= values.Length || values[at].Type != 6) throw new InvalidDataException("心情字符串或行终止标记缺失");
            return resolve(values[at++].Value);
        }
    }
}
