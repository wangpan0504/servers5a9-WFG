using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonLostLandWarp(int ObjectOrdinal, int ObjectId, string ObjectPath, string ActionPath,
    DungeonPoint Destination, int PlayerX, int PlayerY)
{
    // MOVE MAP的第五个原值不解释为wire kind；本地动作链只提供导航来源，不签发战斗事实。
    public int MoveParameter { get; init; }
    public IReadOnlyList<string> SourceChain { get; init; } = [];
}

internal static class DungeonLostLandWarpRules
{
    // 只接受玩家接近物件即执行的一条MOVE MAP；附加机关、时段或自定义动作不授予资格。
    public static (DungeonPoint Destination, int PlayerX, int PlayerY)? UnconditionalMove(
        IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count != 34 || !Tag(0,"motion") || !Tag(1,"base ani") ||
            tokens[2].Type != 6 || string.IsNullOrWhiteSpace(resolve(tokens[2].Value)) || !Tag(3,"/motion") ||
            !Tag(4,"trigger") || !Tag(5,"which") || !Tag(6,"player") || !Tag(7,"checkup") ||
            !Tag(8,"is in area") || !Tag(9,"pos") || !Number(10,0) || !Number(11,0) || !Number(12,0) ||
            !Tag(13,"range") || !Number(14,40) || !Number(15,40) || !Number(16,0) ||
            !Tag(17,"coordinate") || tokens[18].Type != 6 || resolve(tokens[18].Value) != "RELATIVE" ||
            !Tag(19,"/is in area") || !Tag(20,"/checkup") || !Tag(21,"do behavior") || !Tag(22,"me") ||
            !Number(23,0) || !Tag(24,"/trigger") || !Tag(25,"behavior") || !Tag(26,"move map") ||
            tokens[27] is not { Type: 0, Value: >= 0 and <= byte.MaxValue } ||
            tokens[28] is not { Type: 0, Value: >= 0 and <= byte.MaxValue } ||
            tokens[29] is not { Type: 0, Value: >= 0 and <= ushort.MaxValue } ||
            tokens[30] is not { Type: 0, Value: >= 0 and <= ushort.MaxValue } ||
            !Number(31,0) || !Tag(32,"/move map") || !Tag(33,"/behavior")) return null;
        return (new(tokens[27].Value,tokens[28].Value),tokens[29].Value,tokens[30].Value);

        bool Tag(int at, string value) => tokens[at].Type == 3 &&
            resolve(tokens[at].Value).Trim('[',']').Equals(value,StringComparison.OrdinalIgnoreCase);
        bool Number(int at, int value) => tokens[at].Type == 0 && tokens[at].Value == value;
    }

    internal static string? Action(string source, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        string path = PvfArchive.NormalizePath(source[..(source.LastIndexOf('/') + 1)] + relative);
        return path.StartsWith("passiveobject/", StringComparison.Ordinal) && path.EndsWith(".act", StringComparison.Ordinal) ? path : null;
    }

    // 固定形状逐token核对；null只略过展示字符串，不略过触发器、条件、索引或数值。
    internal static bool Match(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, params object?[] shape)
    {
        if (tokens.Count != shape.Length) return false;
        for (int at = 0; at < shape.Length; at++)
        {
            var token = tokens[at];
            bool matches = shape[at] switch
            {
                null => token.Type == 6 && !string.IsNullOrWhiteSpace(resolve(token.Value)),
                int value => token.Type == 0 && token.Value == value,
                string tag when tag.StartsWith('[') => token.Type == 3 && resolve(token.Value).Equals(tag, StringComparison.OrdinalIgnoreCase),
                string text => token.Type == 6 && resolve(token.Value) == text,
                _ => false
            };
            if (!matches) return false;
        }
        return true;
    }
}
