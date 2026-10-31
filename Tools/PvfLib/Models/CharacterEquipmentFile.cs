using System.Collections.Immutable;

namespace PvfLib;

public sealed record CharacterAwakeningEquipment(int Job, int GrowType, ImmutableArray<int> Equipment);

// 只保留默认装备声明；部位及职业资格由服务端目录依据 EQU 解释。
public static class CharacterEquipmentFile
{
    public static ImmutableArray<CharacterAwakeningEquipment> ReadAwakeningEquipment(IReadOnlyList<PvfToken> tokens)
    {
        // 本版先声明13个显示槽；每行跳过武器装扮槽10，实际携带12个编号。
        if (tokens.Count < 2 || tokens.Any(token => token.Type != 0) || tokens[0].Value != 13 ||
            tokens[1].Value < 0 || 2L + tokens[1].Value * 14L != tokens.Count)
            throw new InvalidDataException("觉醒默认装扮表头或行宽无效");
        var result = ImmutableArray.CreateBuilder<CharacterAwakeningEquipment>(tokens[1].Value);
        var keys = new HashSet<(int, int)>();
        for (int at = 2; at < tokens.Count; at += 14)
        {
            int job = tokens[at].Value, grow = tokens[at + 1].Value;
            if (job < 0 || grow < 0 || !keys.Add((job, grow)))
                throw new InvalidDataException("觉醒默认装扮职业或分支重复、非法");
            int[] equipment = Enumerable.Repeat(-1, 13).ToArray();
            int offset = at + 2;
            for (int slot = 0; slot < equipment.Length; slot++)
            {
                if (slot == 10) continue;
                int item = tokens[offset++].Value;
                if (item < -1) throw new InvalidDataException("觉醒默认装扮编号无效");
                equipment[slot] = item;
            }
            result.Add(new(job, grow, [.. equipment]));
        }
        return result.MoveToImmutable();
    }

    public static ImmutableArray<int> ReadDefaultEquipment(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var result = ImmutableArray.CreateBuilder<int>();
        foreach (var section in new PvfTokenDocument(tokens, resolve).Sections)
        {
            if (section.Name is not ("equipment index" or "key value index") || section.Values.Length == 0 ||
                section.Values[0].Type != 6 || resolve(section.Values[0].Value) != "defaultEquipment")
                continue;
            if (section.Children.Length != 0 || section.Values.Skip(1).Any(token => token.Type != 0 || token.Value < -1))
                throw new InvalidDataException("默认装扮装备声明不是整数表");
            var values = section.Values;
            if (section.Name == "equipment index")
                result.AddRange(values.Skip(1).Select(token => token.Value));
            else
            {
                if (values.Length < 2 || (values.Length - 2) % 2 != 0)
                    throw new InvalidDataException("默认装扮键值声明缺少缺省值或索引配对");
                var keys = new HashSet<int>();
                for (int at = 2; at < values.Length; at += 2)
                {
                    if (values[at].Value < 0 || !keys.Add(values[at].Value))
                        throw new InvalidDataException("默认装扮键值索引重复或非法");
                    result.Add(values[at + 1].Value);
                }
            }
        }
        return result.ToImmutable();
    }
}
