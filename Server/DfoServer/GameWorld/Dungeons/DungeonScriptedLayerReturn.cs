using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 保留MAP、物件及ACT的完整导航声明；它不代表触发条件已成立或怪物已经死亡。
internal sealed record DungeonScriptedLayerReturn(int MapId, string MapPath, int ObjectOrdinal, int ObjectId,
    string ObjectPath, int ActionIndex, string ActionPath, int X, int Y, int PlayerX, int PlayerY, int SceneFlag);

internal static class DungeonScriptedLayerReturnRules
{
    public static IReadOnlyList<string>? OtherActions(IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        List<string>? actions = null;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (Tag(tokens[at],resolve) != "etc action") continue;
            if (actions is not null) return null;
            actions = [];
            while (++at < tokens.Count && tokens[at].Type == 6) actions.Add(resolve(tokens[at].Value));
            if (at >= tokens.Count || Tag(tokens[at],resolve) != "/etc action") return null;
        }
        return actions;
    }

    public static (int X,int Y,int PlayerX,int PlayerY,int SceneFlag)? ReturnMove(
        IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        (int,int,int,int,int)? move = null;
        string section = "";
        for (int at = 0; at < tokens.Count; at++)
        {
            string tag = Tag(tokens[at],resolve);
            if (section.Length == 0)
            {
                if (tag is not ("motion" or "trigger" or "behavior")) return null;
                section = tag;
                continue;
            }
            if (tag == "/"+section) { section = ""; continue; }
            if (tag is "motion" or "trigger" or "behavior") return null;
            if (tag != "move map") continue;
            // 只接受独立BEHAVIOR里的单条五整数指令，嵌套或歧义声明不授予返回资格。
            if (section != "behavior" || move is not null || at == 0 || Tag(tokens[at - 1],resolve) != "behavior" || at + 7 >= tokens.Count ||
                Tag(tokens[at + 6],resolve) != "/move map" || Tag(tokens[at + 7],resolve) != "/behavior" ||
                Enumerable.Range(1,5).Any(index => tokens[at + index].Type != 0)) return null;
            int x = tokens[at + 1].Value, y = tokens[at + 2].Value;
            if (x is < 0 or > byte.MaxValue || y is < 0 or > byte.MaxValue || tokens[at + 5].Value != 1) return null;
            move = (x,y,tokens[at + 3].Value,tokens[at + 4].Value,tokens[at + 5].Value);
        }
        return section.Length == 0 ? move : null;
    }

    static string Tag(PvfToken token, Func<int,string> resolve) => token.Type == 3
        ? resolve(token.Value).Trim('[',']').ToLowerInvariant() : "";
}
