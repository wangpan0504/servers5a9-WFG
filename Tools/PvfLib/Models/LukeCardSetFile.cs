using System.Collections.Immutable;

namespace PvfLib;

public sealed record PvfCardImage(string Path, ImmutableArray<int> Indices);
public readonly record struct PvfCardPoint(double X, double Y);
public sealed record PvfLukePattern(int Dungeon, int Ordinal, PvfCardImage Image,
    ImmutableArray<PvfCardPoint> Positions, string Tooltip);
public sealed record PvfLukeCard(int Template, PvfCardImage Image, string Tooltip);
public sealed record PvfLukeCardSet(ImmutableArray<PvfLukePattern> Patterns, ImmutableArray<PvfLukeCard> Cards);

// 只保存文件声明与顺序；坐标不是MAP格，pattern序号不是MazeOrdinal，图片索引不是随机权重。
public static class LukeCardSetFile
{
    public static PvfLukeCardSet Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count > 262144) throw new InvalidDataException("卢克卡牌文件超过解析预算");
        var document = new PvfTokenDocument(tokens, resolve, preserveExtraEndTags: true);
        if (!document.Diagnostics.IsEmpty || document.Sections.Length is < 1 or > 4352)
            throw new InvalidDataException("卢克卡牌声明不完整或超过有界目录");
        var patterns = ImmutableArray.CreateBuilder<PvfLukePattern>();
        var cards = ImmutableArray.CreateBuilder<PvfLukeCard>();
        var patternKeys = new HashSet<(int, int)>(); var cardKeys = new HashSet<int>();
        foreach (var section in document.Sections)
        {
            bool pattern = section.Name == "pattern";
            if (!section.IsBlock || section.Name is not ("pattern" or "card") ||
                section.Values.Length != (pattern ? 2 : 1) || section.Values.Any(token => token.Type != 0) ||
                section.Values[0].Value <= 0 || pattern && section.Values[1].Value < 0)
                throw new InvalidDataException("卢克卡牌根块或身份字段不支持");
            string[] names = pattern ? ["image", "card pos", "tooltip text"] : ["image", "tooltip text"];
            if (section.Children.Length != names.Length || section.Children.Any(row => !names.Contains(row.Name)) ||
                section.Children.Select(row => row.Name).Distinct().Count() != names.Length)
                throw new InvalidDataException("卢克卡牌子字段缺失、重复或未知");
            var image = section.Children.Single(row => row.Name == "image");
            var tooltip = section.Children.Single(row => row.Name == "tooltip text");
            if (image.IsBlock || !image.Children.IsEmpty || image.Values.Length is < 2 or > 17 || image.Values[0].Type != 6 ||
                image.Values.Skip(1).Any(token => token.Type != 0 || token.Value < 0) ||
                tooltip.IsBlock || !tooltip.Children.IsEmpty || tooltip.Values is not [{ Type: 6 }])
                throw new InvalidDataException("卢克卡牌图片或说明字段不完整");
            string path = resolve(image.Values[0].Value);
            if (string.IsNullOrWhiteSpace(path) || path.Length > 2048) throw new InvalidDataException("卢克卡牌图片路径无效");
            var picture = new PvfCardImage(path, image.Values.Skip(1).Select(token => token.Value).ToImmutableArray());
            string text = resolve(tooltip.Values[0].Value);
            if (pattern)
            {
                if (patterns.Count >= 256 || !patternKeys.Add((section.Values[0].Value, section.Values[1].Value)))
                    throw new InvalidDataException("卢克PATTERN重复或超过上限");
                var position = section.Children.Single(row => row.Name == "card pos");
                if (!position.IsBlock || !position.Children.IsEmpty || position.Values.Length is < 2 or > 256 || position.Values.Length % 2 != 0)
                    throw new InvalidDataException("卢克卡牌坐标必须为完整有界数值对");
                var points = ImmutableArray.CreateBuilder<PvfCardPoint>(position.Values.Length / 2);
                for (int at = 0; at < position.Values.Length; at += 2)
                    points.Add(new(Number(position.Values[at]), Number(position.Values[at + 1])));
                patterns.Add(new(section.Values[0].Value, section.Values[1].Value, picture, points.MoveToImmutable(), text));
            }
            else
            {
                if (cards.Count >= 4096 || !cardKeys.Add(section.Values[0].Value)) throw new InvalidDataException("卢克CARD重复或超过上限");
                cards.Add(new(section.Values[0].Value, picture, text));
            }
        }
        if (patterns.Count == 0 || cards.Count == 0) throw new InvalidDataException("卢克卡牌缺少PATTERN或CARD");
        return new(patterns.ToImmutable(), cards.ToImmutable());
    }

    static double Number(PvfToken token)
    {
        double value = token.Type == 0 ? (double)token.Value : token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : double.NaN;
        if (!double.IsFinite(value)) throw new InvalidDataException("卢克卡牌坐标不是有限数值");
        return value;
    }
}
