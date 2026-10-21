using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed record ExperienceBonusCatalog(decimal BlackDiamondPercent, decimal InternetCafePercent)
{
    public static ExperienceBonusCatalog Load(PvfArchive pvf) => Parse(pvf.ReadText("etc/premiumlist_new.etc"));

    internal static ExperienceBonusCatalog Parse(string text)
    {
        decimal Read(int type)
        {
            var blocks = Regex.Matches(text, @"\[type\]\s*(-?\d+)(?<body>.*?)(?=\[type\]|\z)", RegexOptions.Singleline)
                .Cast<Match>().Where(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == type).ToArray();
            if (blocks.Length == 0) return 0;
            if (blocks.Length != 1) throw new InvalidDataException("经验特权类型重复");
            // 子物品的期限和属性不属于父特权。
            string parent = Regex.Replace(blocks[0].Groups["body"].Value, @"\[item\].*?\[/item\]", "", RegexOptions.Singleline);
            var fields = Regex.Matches(parent, @"\[exp\]\s*([^\[]*)");
            if (fields.Count == 0) return 0;
            if (fields.Count != 1 || !decimal.TryParse(fields[0].Groups[1].Value.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out decimal percent) || percent < 0)
                throw new InvalidDataException("经验特权exp声明无效");
            return percent;
        }
        return new(Read(56), Read(128));
    }
}
