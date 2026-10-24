using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Items;

// 根首转资格独立于奖励奖池；普通、装扮和商城礼包共用同一严格声明合同。
internal static class BoosterFirstGrowTypes
{
    public static bool TryParse(string text, out ImmutableArray<byte> branches)
    {
        branches = [];
        string tags = Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
        var declarations = Regex.Matches(tags, @"\[\s*/?\s*usable\s+first\s+growtype[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (declarations.Count == 0) return true;
        if (text.Count(value => value == '`') % 2 != 0 || declarations.Count != 2 ||
            declarations[0].Value != "[usable first growtype]" || declarations[1].Value != "[/usable first growtype]" ||
            !BoosterCatalog.IsRootDeclaration(tags, declarations[0].Index)) return false;
        int start = declarations[0].Index + declarations[0].Length;
        string[] values = text[start..declarations[1].Index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = ImmutableArray.CreateBuilder<byte>();
        foreach (string value in values)
        {
            if (!byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out byte branch) || branch > 5 || result.Contains(branch))
                return false;
            result.Add(branch);
        }
        branches = result.ToImmutable();
        return !branches.IsEmpty;
    }
}
