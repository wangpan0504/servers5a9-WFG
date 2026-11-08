using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Channels;

internal sealed record ChannelDefinition(int Number, string NameReference, int Type, string Group);
internal readonly record struct ChannelIdentity(byte ServerIndex, byte Number);
internal sealed record ChannelEntry(ChannelIdentity Identity, int Type, string Group);
internal sealed record ChannelEndpoint(ChannelEntry Entry, int Port);

internal sealed partial class ChannelCatalog
{
    public const int PrimaryChannelNumber = 10;
    public int ServerIndex { get; }
    public ChannelIdentity GameIdentity => new((byte)ServerIndex, PrimaryChannelNumber);
    public ChannelEntry Entry(int number)
    {
        if (number is < 1 or > 255)
            throw new InvalidDataException("当前Actor/Player频道身份必须在1..255内");
        var definition = Channels.Single(row => row.Number == number);
        return new(new(checked((byte)ServerIndex), checked((byte)number)), definition.Type, definition.Group);
    }
    public string SourceText { get; }
    public IReadOnlyList<ChannelDefinition> Channels { get; }

    ChannelCatalog(int serverIndex, string source, List<ChannelDefinition> channels)
        => (ServerIndex, SourceText, Channels) = (serverIndex, source, channels.AsReadOnly());

    public static ChannelCatalog Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length is 0 or > 65520 || source.Contains('\0'))
            throw new InvalidDataException("频道脚本长度或内容无效");
        string block = PvfScript.Block(source, "server");
        string[] lines = block.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        if (lines.Length < 2 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int server) || server is < 0 or > 249)
            throw new InvalidDataException("频道脚本必须声明一个有效服务器分组");
        var channels = new List<ChannelDefinition>();
        var numbers = new HashSet<int>();
        foreach (string line in lines.Skip(1))
        {
            MatchCollection matches = TokenPattern().Matches(line);
            if (matches.Count != 16 || matches[^1].Index + matches[^1].Length != line.Length)
                throw new InvalidDataException("频道行必须包含16个完整字段");
            string[] tokens = matches.Select(match => match.Groups[1].Value).ToArray();
            if (!int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number is < 1 or > 65535 || !numbers.Add(number))
                throw new InvalidDataException("频道号无效或重复");
            // 本版名称列剥除外层反引号后仍须保留可解析的字符串引用。
            if (!NamePattern().IsMatch(tokens[1]))
                throw new InvalidDataException("频道名称须使用反引号包裹的客户端字符串引用");
            if (!int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out int type) ||
                !(type is >= 0 and <= 56 or 62 or 63 or 64 or 200))
                throw new InvalidDataException("频道类型未在匹配客户端中定义");
            if (!GroupPattern().IsMatch(tokens[3]) || !TextPattern().IsMatch(tokens[15]))
                throw new InvalidDataException("频道分组或末尾字符串无效");
            for (int i = 4; i < 15; i++)
            {
                if (!float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) ||
                    i == 4 && (value < 0 || value > 255))
                    throw new InvalidDataException("频道数值字段无效");
            }
            if (type is 4 or 5 or 8 or 13 or 24)
            {
                // 原生01705040比较Actor Data+30C与频道的决斗等级范围；与下方角色等级列分开。
                float minimumGrade = float.Parse(tokens[5], CultureInfo.InvariantCulture);
                float maximumGrade = float.Parse(tokens[6], CultureInfo.InvariantCulture);
                if (minimumGrade < 0 || maximumGrade > byte.MaxValue || minimumGrade > maximumGrade ||
                    minimumGrade != MathF.Truncate(minimumGrade) || maximumGrade != MathF.Truncate(maximumGrade))
                    throw new InvalidDataException($"频道{number}的决斗等级范围必须是0..255内且下限不大于上限的整数");
                // 客户端将这两列读为等级下限/上限；上限0不会补默认，会拒绝所有角色。
                float minimumLevel = float.Parse(tokens[9], CultureInfo.InvariantCulture);
                float maximumLevel = float.Parse(tokens[10], CultureInfo.InvariantCulture);
                if (minimumLevel < 1 || maximumLevel > 95 || minimumLevel > maximumLevel ||
                    minimumLevel != MathF.Truncate(minimumLevel) || maximumLevel != MathF.Truncate(maximumLevel))
                    throw new InvalidDataException($"频道{number}的PK等级范围必须是1..95内且下限不大于上限的整数");
            }
            channels.Add(new(number, tokens[1][1..^1], type, tokens[3][1..^1]));
            if (channels.Count > 256)
                throw new InvalidDataException("频道目录最多支持256项");
        }
        if (!numbers.Contains(PrimaryChannelNumber))
            throw new InvalidDataException("频道脚本缺少共享游戏入口10");
        return new(server, source, channels);
    }

    [GeneratedRegex(@"\G\s*(`[^`\r\n]*`|[^\s`]+)")]
    private static partial Regex TokenPattern();
    [GeneratedRegex(@"^`<(?:[0-9]+::)?[A-Za-z0-9_]+>`$")]
    private static partial Regex NamePattern();
    [GeneratedRegex(@"^`\[[^\]\r\n`]+\]`$")]
    private static partial Regex GroupPattern();
    [GeneratedRegex(@"^`[^`\r\n]*`$")]
    private static partial Regex TextPattern();
}
