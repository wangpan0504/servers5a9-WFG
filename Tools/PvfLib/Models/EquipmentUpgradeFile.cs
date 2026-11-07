using System.Globalization;

namespace PvfLib;

// 保留装备脚本的原值；成功率单位、适用玩法和当前穿戴资格由服务端解释。
public readonly record struct EquipmentUpgradeFile(float ReinforcementRate, float AmplificationRate,
    float ProbabilityIncrease, float CostDiscount, int DiscountReinforcement, int DiscountAmplification)
{
    public static EquipmentUpgradeFile Read(PvfDocument document)
    {
        if (!document.Has("increase upgrade compound rate") && !document.Has("upgrade prob increase") &&
            !document.Has("upgrade cost discount")) return default;
        var rate = ReadNumbers("increase upgrade compound rate", 2);
        var increase = ReadNumbers("upgrade prob increase", 1);
        float discount = 0;
        int reinforce = 0, amplify = 0;
        foreach (string declaration in document.Sections("upgrade cost discount"))
        {
            string[] tokens = declaration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 3 || !ReadNumber(tokens[0], out discount) ||
                !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out reinforce) ||
                !int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out amplify))
                throw new InvalidDataException("装备改造费用必须声明数值及两个整数适用标志");
        }
        return new(rate[0], rate[1], increase[0], discount, reinforce, amplify);

        float[] ReadNumbers(string field, int count)
        {
            var declarations = document.Sections(field);
            if (declarations.Count == 0) return new float[count];
            float[]? result = null;
            foreach (string declaration in declarations)
            {
                string[] tokens = declaration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != count) throw new InvalidDataException("装备改造声明宽度无效: " + field);
                result = new float[count];
                for (int i = 0; i < count; i++)
                    if (!ReadNumber(tokens[i], out result[i]))
                        throw new InvalidDataException("装备改造声明包含无效数值: " + field);
            }
            return result!;
        }
    }

    static bool ReadNumber(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
}
