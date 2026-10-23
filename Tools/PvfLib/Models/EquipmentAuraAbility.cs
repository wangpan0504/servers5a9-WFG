using System.Globalization;
using System.Text.RegularExpressions;

namespace PvfLib;

// 保留能力标签及可选整数，具体玩法和冷却单位由GameWorld解释。
public sealed record EquipmentAuraAbility(string Label, int? Parameter)
{
    public static EquipmentAuraAbility? Read(PvfDocument document)
    {
        if (!document.Has("aura ability")) return null;
        string section = document.Section("aura ability"), block = document.Block("aura ability");
        if (string.IsNullOrWhiteSpace(block))
        {
            if (!string.IsNullOrWhiteSpace(section)) throw new InvalidDataException("光环能力块未闭合");
            return new("[none]", null);
        }
        var match = Regex.Match(block, @"\A\s*`([^`]+)`(?:\s+([+-]?\d+))?\s*\z", RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups[2].Success &&
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("光环能力声明必须是标签及可选整数");
        return new(match.Groups[1].Value, match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null);
    }
}
