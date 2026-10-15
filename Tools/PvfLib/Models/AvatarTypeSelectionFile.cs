using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PvfLib;

public sealed record AvatarTypeSelectionRow(int Days, int Gold, int WinPoints, int Cera,
    int Reserved4, int Reserved5, ImmutableArray<string> Sockets);

public static class AvatarTypeSelectionFile
{
    // 当前格式为七整数，再按第七项读取孔位标签；不采用旧版本的固定行宽。
    public static ImmutableArray<AvatarTypeSelectionRow> Parse(PvfDocument document)
    {
        if (!document.Has("avatar type select")) return [];
        if (document.DeclarationCount("avatar type select") > 1)
            throw new InvalidDataException("重复装扮期限类型声明");
        string body = document.Block("avatar type select");
        var tokens = Regex.Matches(body, "`[^`]*`|\\S+").Select(match => match.Value).ToArray();
        if (tokens.Length == 0) throw new InvalidDataException("装扮期限类型为空或未闭合");
        var rows = ImmutableArray.CreateBuilder<AvatarTypeSelectionRow>();
        int at = 0;
        while (at < tokens.Length)
        {
            var values = new int[7];
            for (int i = 0; i < values.Length; i++)
                if (at == tokens.Length || !int.TryParse(tokens[at++], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                    throw new InvalidDataException("装扮期限类型整数行不完整");
            if (values[0] < 0 || values[6] is < 0 or > 5 || tokens.Length - at < values[6])
                throw new InvalidDataException("装扮期限或孔位数量无效");
            var sockets = ImmutableArray.CreateBuilder<string>(values[6]);
            for (int i = 0; i < values[6]; i++)
            {
                string token = tokens[at++];
                if (token.Length < 4 || token[0] != '`' || token[^1] != '`')
                    throw new InvalidDataException("装扮孔位标签无效");
                sockets.Add(token[1..^1]);
            }
            rows.Add(new(values[0], values[1], values[2], values[3], values[4], values[5], sockets.MoveToImmutable()));
        }
        return rows.ToImmutable();
    }

    public static int? GiftType(PvfDocument document)
    {
        if (!Regex.IsMatch(document.Section("stackable type"), @"^\s*`\[avatar gift\]`\s+0\s*$")) return null;
        if (!document.TryGetInt32("avatar gift type", out int type) || type < 0)
            throw new InvalidDataException("装扮兑换券类型缺失或无效");
        return type;
    }
}
