using System.Collections.Immutable;

namespace PvfLib;

public readonly record struct EmotionExpressionDefinition(int Id, int Kind, bool Visible,
    ImmutableArray<int> RequiredQuests = default, bool Special = false,
    string EmbossedAnimation = "", string NormalAnimation = "");

public static class EmotionExpressionFile
{
    public static ImmutableArray<EmotionExpressionDefinition> Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var blocks = document.Sections.Where(s => s.Name == "emotion info").ToArray();
        if (blocks.Length != 1 || blocks[0].Children.Length != 0)
            throw new InvalidDataException("心情普通目录缺失或重复");
        var result = ImmutableArray.CreateBuilder<EmotionExpressionDefinition>();
        var ids = new HashSet<int>();
        Parse(blocks[0], special: false);
        var specials = document.Sections.Where(s => s.Name == "special emotion info").ToArray();
        if (specials.Length > 1) throw new InvalidDataException("心情特殊目录重复");
        if (specials.Length == 1) Parse(specials[0], special: true);
        return result.ToImmutable();

        void Parse(PvfTokenSection block, bool special)
        {
            if (block.Children.Length != 0) throw new InvalidDataException("心情目录存在嵌套声明");
            var values = block.Values;
            int at = 0;
            while (at < values.Length)
            {
                int id = Integer(), kind = Integer(), visible = special ? 1 : Integer();
                if (id is < 0 or > short.MaxValue || special && id < 1000 || !special && id >= 1000 ||
                    kind is < 0 or > 7 && !(special && kind == 300) || visible is not (0 or 1) || !ids.Add(id))
                    throw new InvalidDataException("心情编号、类型或显示声明无效");
                if (kind is 4 or 300) _ = Integer();
                var quests = ImmutableArray.CreateBuilder<int>();
                if (kind == 5)
                {
                    int count = Integer();
                    if (count is < 0 or > 30000 || count > values.Length - at)
                        throw new InvalidDataException("心情任务声明数量越界");
                    for (int i = 0; i < count; i++)
                    {
                        int quest = Integer();
                        if (quest <= 0) throw new InvalidDataException("心情解锁任务编号无效");
                        quests.Add(quest);
                    }
                }
                string embossed = Text(), normal = Text();
                for (int i = 0; i < (special ? 2 : 3); i++)
                    if (Integer() is not (0 or 1)) throw new InvalidDataException("心情开关不是布尔值");
                _ = Text();
                int shortcuts = 0;
                while (Text() != "$END") shortcuts++;
                if (shortcuts == 0) throw new InvalidDataException("心情快捷指令缺失");
                result.Add(new(id, kind, visible == 1, quests.ToImmutable(), special, embossed, normal));
            }

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
}
