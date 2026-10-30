namespace DfoServer.Network.Parsers;

internal enum PromotionQueryRoute { Ordinary, Invalid, Query }

internal static class MachinePromotionQuery
{
    const string Command = "#cp-promo/1";

    internal static PromotionQueryRoute Parse(byte kind, string? target, string text, out string? nonce)
    {
        nonce = null;
        if (kind != 3 || target is not null || string.IsNullOrEmpty(text) ||
            !text.StartsWith(Command, StringComparison.Ordinal) ||
            (text.Length != Command.Length && !char.IsWhiteSpace(text[Command.Length])))
            return PromotionQueryRoute.Ordinary;

        // 已占命名空间的坏语法必须吞掉；只允许一个空格和32位小写hex。
        if (text.Length != 44 || text[Command.Length] != ' ')
            return PromotionQueryRoute.Invalid;
        string candidate = text[(Command.Length + 1)..];
        if (!candidate.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return PromotionQueryRoute.Invalid;
        nonce = candidate;
        return PromotionQueryRoute.Query;
    }
}