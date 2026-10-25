using System.Collections.Immutable;
using System.Globalization;

namespace PvfLib;

// 原始资源分支编号与装备标记不在解析层换算成角色转职、穿戴状态或时间单位。
public sealed record PvpPrivateEquipmentDeclaration(string Slot, int ItemId, int Flag);
public sealed record PvpPrivateCharacterDeclaration(int ResourceGrowType,
    ImmutableArray<PvpPrivateEquipmentDeclaration> Equipment, int? RecreatePenalty);

public static class PvpPrivateCharacterFile
{
    public static ImmutableArray<PvpPrivateCharacterDeclaration> Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var result = ImmutableArray.CreateBuilder<PvpPrivateCharacterDeclaration>();
        var equipment = ImmutableArray.CreateBuilder<PvpPrivateEquipmentDeclaration>();
        var seen = new HashSet<int>();
        int grow = 0;
        int? penalty = null;
        bool declared = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != 3) continue;
            string tag = resolve(tokens[i].Value);
            if (tag.StartsWith("[growtype ", StringComparison.Ordinal) && tag.EndsWith(']') &&
                int.TryParse(tag.AsSpan(10, tag.Length - 11), NumberStyles.None, CultureInfo.InvariantCulture, out int next))
            {
                Finish();
                if (next < 1 || !seen.Add(next)) throw new InvalidDataException("决斗角色资源分支重复或非法");
                grow = next;
            }
            else if (tag == "[pvp private create equipment list]")
            {
                if (grow == 0 || declared) throw new InvalidDataException("决斗创建装备缺少分支或重复声明");
                declared = true;
                i++;
                while (i < tokens.Count && tokens[i].Type != 3)
                {
                    if (i + 2 >= tokens.Count || tokens[i].Type != 6 || tokens[i + 1].Type != 0 ||
                        tokens[i + 1].Value <= 0 || tokens[i + 2].Type != 0)
                        throw new InvalidDataException("决斗创建装备必须为部位、模板、原始标记三元组");
                    string slot = resolve(tokens[i].Value);
                    if (slot.Length < 3 || slot[0] != '[' || slot[^1] != ']')
                        throw new InvalidDataException("决斗创建装备部位格式非法");
                    equipment.Add(new(slot, tokens[i + 1].Value, tokens[i + 2].Value));
                    i += 3;
                }
                if (i >= tokens.Count || resolve(tokens[i].Value) != "[/pvp private create equipment list]" || equipment.Count == 0)
                    throw new InvalidDataException("决斗创建装备列表缺失闭合或为空");
            }
            else if (tag == "[pvp private recreate penaly period]")
            {
                if (grow == 0 || penalty is not null || i + 1 >= tokens.Count || tokens[i + 1].Type != 0 ||
                    tokens[i + 1].Value < 0 || i + 2 < tokens.Count && tokens[i + 2].Type != 3)
                    throw new InvalidDataException("决斗角色重建惩罚必须为单个非负整数");
                penalty = tokens[++i].Value;
            }
        }
        Finish();
        return result.ToImmutable();

        void Finish()
        {
            if (declared || penalty is not null) result.Add(new(grow, equipment.ToImmutable(), penalty));
            equipment.Clear();
            penalty = null;
            declared = false;
        }
    }
}
