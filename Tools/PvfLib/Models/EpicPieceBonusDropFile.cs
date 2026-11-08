using System.Collections.Immutable;

namespace PvfLib;

public sealed record EpicPieceBonusDropData(ImmutableDictionary<int, int> Equipment, ImmutableDictionary<int, int> Stackable);

// 两种来源均为物品编号/额外碎片数量；不在资源库决定穿戴、持有资格或随机方式。
public static class EpicPieceBonusDropFile
{
    public static EpicPieceBonusDropData Read(PvfTokenDocument document) =>
        new(ReadList(document, "equip item list"), ReadList(document, "stackable item list"));

    static ImmutableDictionary<int, int> ReadList(PvfTokenDocument document, string name)
    {
        var result = ImmutableDictionary.CreateBuilder<int, int>();
        foreach (var section in document.Sections.Where(row => row.Name == name))
        {
            if (!section.IsBlock || section.Children.Length != 0 || section.Values.Length % 2 != 0 ||
                section.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("史诗碎片加成列表须为完整的物品编号/数量对：" + name);
            for (int i = 0; i < section.Values.Length; i += 2)
            {
                int id = section.Values[i].Value, count = section.Values[i + 1].Value;
                if (id <= 0 || count < 0 || result.TryGetValue(id, out int previous) && previous != count)
                    throw new InvalidDataException("史诗碎片加成编号、数量或重复声明无效：" + name);
                result[id] = count;
            }
        }
        return result.ToImmutable();
    }
}
