using System.Collections.ObjectModel;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

// 标签名沿用资源声明；这里不把 attack/defense 标签改称战斗攻击或四维。
internal enum CharacterAttributeField
{
    MaxHp, MaxMp, PhysicalAttack, PhysicalDefense, MagicalAttack, MagicalDefense,
    FireResistance, WaterResistance, DarkResistance, LightResistance,
    InventoryLimit, MpRecovery, MoveSpeed, AttackSpeed, CastSpeed,
    HitRecovery, JumpPower, Weight, JumpSpeed
}

internal sealed record CharacterAttributeBlock(int ResourceGrowType, int Awakening,
    IReadOnlyDictionary<CharacterAttributeField, float> Fields);

internal sealed record CharacterAttributeDefinition(string ScriptPath,
    IReadOnlyList<CharacterAttributeBlock> Blocks)
{
    internal static readonly IReadOnlyDictionary<string, CharacterAttributeField> Tags =
        new ReadOnlyDictionary<string, CharacterAttributeField>(new Dictionary<string, CharacterAttributeField>(StringComparer.Ordinal)
        {
            ["[HP MAX]"] = CharacterAttributeField.MaxHp, ["[MP MAX]"] = CharacterAttributeField.MaxMp,
            ["[physical attack]"] = CharacterAttributeField.PhysicalAttack,
            ["[physical defense]"] = CharacterAttributeField.PhysicalDefense,
            ["[magical attack]"] = CharacterAttributeField.MagicalAttack,
            ["[magical defense]"] = CharacterAttributeField.MagicalDefense,
            ["[fire resistance]"] = CharacterAttributeField.FireResistance,
            ["[water resistance]"] = CharacterAttributeField.WaterResistance,
            ["[dark resistance]"] = CharacterAttributeField.DarkResistance,
            ["[light resistance]"] = CharacterAttributeField.LightResistance,
            ["[inventory limit]"] = CharacterAttributeField.InventoryLimit,
            ["[MP regen speed]"] = CharacterAttributeField.MpRecovery,
            ["[move speed]"] = CharacterAttributeField.MoveSpeed,
            ["[attack speed]"] = CharacterAttributeField.AttackSpeed,
            ["[cast speed]"] = CharacterAttributeField.CastSpeed,
            ["[hit recovery]"] = CharacterAttributeField.HitRecovery,
            ["[jump power]"] = CharacterAttributeField.JumpPower,
            ["[weight]"] = CharacterAttributeField.Weight,
            ["[jump speed]"] = CharacterAttributeField.JumpSpeed
        });

    // 原始数值保留 float32 位精度，缺项保持缺项；此入口不评估等级或装备效果。
    public static CharacterAttributeDefinition Parse(string path, ReadOnlySpan<PvfToken> tokens, Func<int, string> resolve)
    {
        var blocks = new List<CharacterAttributeBlock>();
        Dictionary<CharacterAttributeField, float>? fields = null;
        int growType = -1, awakening = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type != 3)
                continue;
            string tag = resolve(tokens[i].Value);
            int next = i + 1;
            while (next < tokens.Length && tokens[next].Type != 3)
                next++;
            int index = 0;
            if (tag == "[initial value]" || TryIndex(tag, "growtype", out index))
            {
                Finish();
                growType = tag == "[initial value]" ? 0 : index;
                awakening = 0;
                fields = new();
            }
            else if (TryIndex(tag, "awakening", out index) && fields is not null)
            {
                if (growType == 0)
                    throw new InvalidDataException("初始属性块不能包含觉醒声明");
                Finish();
                awakening = index;
                fields = new();
            }
            else if (fields is not null && Tags.TryGetValue(tag, out var field))
            {
                if (next != i + 2 || tokens[i + 1].Type is not (0 or 2))
                    throw new InvalidDataException("角色属性声明不是单个数值: " + tag);
                float value = tokens[i + 1].Type == 2
                    ? BitConverter.Int32BitsToSingle(tokens[i + 1].Value) : tokens[i + 1].Value;
                if (!float.IsFinite(value) || !fields.TryAdd(field, value))
                    throw new InvalidDataException("角色属性声明非有限或重复: " + tag);
            }
            i = next - 1;
        }
        Finish();
        if (blocks.Count == 0 || blocks[0].ResourceGrowType != 0 ||
            blocks.Select(block => (block.ResourceGrowType, block.Awakening)).Distinct().Count() != blocks.Count)
            throw new InvalidDataException("角色属性资源块缺失或重复");
        return new(path, Array.AsReadOnly(blocks.ToArray()));

        void Finish()
        {
            if (fields is not null)
                blocks.Add(new(growType, awakening, new ReadOnlyDictionary<CharacterAttributeField, float>(fields)));
        }
    }

    private static bool TryIndex(string tag, string name, out int index)
    {
        string prefix = "[" + name + " ";
        index = 0;
        return tag.StartsWith(prefix, StringComparison.Ordinal) && tag.EndsWith(']') &&
            int.TryParse(tag.AsSpan(prefix.Length, tag.Length - prefix.Length - 1),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) && index > 0;
    }
}

internal sealed record CharacterAdditionalStatDefinition(string ScriptPath, int MaxLevel,
    IReadOnlyDictionary<CharacterAttributeField, float> InitialFields,
    IReadOnlyDictionary<CharacterAttributeField, float> GrowthFields)
{
    // 040BD87E..040BD976：解析 chn_add_stat.etc 的 [max level]、[add initial value stat] 与 [add growtype stat]。
    public static CharacterAdditionalStatDefinition Parse(string path, ReadOnlySpan<PvfToken> tokens, Func<int, string> resolve)
    {
        int? maxLevel = null;
        Dictionary<CharacterAttributeField, float>? initial = null, growth = null, current = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type != 3)
                continue;
            string tag = resolve(tokens[i].Value);
            int next = i + 1;
            while (next < tokens.Length && tokens[next].Type != 3)
                next++;
            if (tag == "[max level]")
            {
                if (maxLevel is not null || next != i + 2 || tokens[i + 1].Type != 0 ||
                    tokens[i + 1].Value is <= 0 or > CharacterGrowthCatalog.MaximumSupportedLevel)
                    throw new InvalidDataException("角色额外属性最大等级缺失或非法");
                maxLevel = tokens[i + 1].Value;
                current = null;
            }
            else if (tag == "[add initial value stat]")
            {
                if (initial is not null)
                    throw new InvalidDataException("角色额外初始属性块重复");
                initial = current = new();
            }
            else if (tag == "[/add initial value stat]")
            {
                if (!ReferenceEquals(current, initial))
                    throw new InvalidDataException("角色额外初始属性结束标签不匹配");
                current = null;
            }
            else if (tag == "[add growtype stat]")
            {
                if (growth is not null)
                    throw new InvalidDataException("角色额外成长属性块重复");
                growth = current = new();
            }
            else if (tag == "[/add growtype stat]")
            {
                if (!ReferenceEquals(current, growth))
                    throw new InvalidDataException("角色额外成长属性结束标签不匹配");
                current = null;
            }
            else if (current is not null && CharacterAttributeDefinition.Tags.TryGetValue(tag, out var field))
            {
                if (next != i + 2 || tokens[i + 1].Type is not (0 or 2))
                    throw new InvalidDataException("角色额外属性声明不是单个数值: " + tag);
                float value = tokens[i + 1].Type == 2
                    ? BitConverter.Int32BitsToSingle(tokens[i + 1].Value) : tokens[i + 1].Value;
                if (!float.IsFinite(value) || !current.TryAdd(field, value))
                    throw new InvalidDataException("角色额外属性声明非有限或重复: " + tag);
            }
            else
                throw new InvalidDataException("未识别的角色额外属性标签: " + tag);
            i = next - 1;
        }
        if (maxLevel is null || initial is null || growth is null || initial.Count == 0 || growth.Count == 0)
            throw new InvalidDataException("角色额外属性资源块缺失");
        return new(path, maxLevel.Value,
            new ReadOnlyDictionary<CharacterAttributeField, float>(initial),
            new ReadOnlyDictionary<CharacterAttributeField, float>(growth));
    }
}
