using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib.Scripting;

namespace PvfLib;

// 只还原交易次数与后续绑定标签，不推断实例剩余次数或仓库资格。
public sealed record StackableTradeLimitDeclaration(int Count, string Attachment)
{
    public static StackableTradeLimitDeclaration? Read(PvfDocument document)
    {
        if (document.DeclarationCount("trade limit") != 1)
            return null;
        var match = Regex.Match(document.Section("trade limit"), @"\A\s*([0-9]+)\s+`(\[[^\]\r\n`]+\])`\s*\z");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            ? new(count, match.Groups[2].Value) : null;
    }
}
