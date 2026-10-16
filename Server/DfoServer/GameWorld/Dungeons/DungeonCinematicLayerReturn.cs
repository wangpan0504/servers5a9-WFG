using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonCinematicLayerReturn(int MapId, string MapPath, string ActionPath, int CinematicId, string CinematicPath);

// 只解释一次无条件地图演出的导航声明，不执行CMT或据此签发死亡、清房与奖励。
internal static class DungeonCinematicLayerReturnRules
{
    public static string? BasicAction(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        string? action = null;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (Text(tokens[at],resolve) != "basic action") continue;
            // 不认识或歧义的可选声明不授予导航资格，保持原房间的清怪规则。
            if (action is not null || at + 1 >= tokens.Count || tokens[at + 1].Type != 6 ||
                at + 2 < tokens.Count && tokens[at + 2].Type != 3) return null;
            action = resolve(tokens[++at].Value);
        }
        return action;
    }

    public static int? OneShotCinematic(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count != 11 || !Tag(0,"trigger") || !Tag(1,"limit") || !Number(2,1) ||
            !Tag(3,"do behavior") || !Tag(4,"me") || !Number(5,0) || !Tag(6,"/trigger") ||
            !Tag(7,"behavior") || !Tag(8,"cinematic") || tokens[9].Type != 0 || tokens[9].Value <= 0 || !Tag(10,"/behavior"))
            return null;
        return tokens[9].Value;

        bool Tag(int at, string expected) => Text(tokens[at],resolve) == expected;
        bool Number(int at, int expected) => tokens[at].Type == 0 && tokens[at].Value == expected;
    }

    public static bool IsNonCombatMapChange(int mapId, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        bool mapSeen = false, typeSeen = false, moveSeen = false, scene = false, damageSeen = false, changeSeen = false;
        int scenes = 0, behavior = 0;
        for (int at = 0; at < tokens.Count; at++)
        {
            string tag = Text(tokens[at],resolve);
            switch (tag)
            {
                case "map" when !scene:
                    if (mapSeen || !Scalar(at,mapId)) return false;
                    mapSeen = true;
                    break;
                case "type" when !scene:
                    if (typeSeen || at + 1 >= tokens.Count || Text(tokens[at + 1],resolve) != "scenario") return false;
                    typeSeen = true;
                    break;
                case "move allow" when !scene:
                    if (moveSeen || !Scalar(at,0)) return false;
                    moveSeen = true;
                    break;
                case "scene":
                    if (scene || behavior != 0) return false;
                    scene = true; damageSeen = false; scenes++;
                    break;
                case "/scene":
                    if (!scene || behavior != 0 || !damageSeen) return false;
                    scene = false;
                    break;
                case "allow damage":
                    if (!scene || behavior != 0 || damageSeen || !Scalar(at,0)) return false;
                    damageSeen = true;
                    break;
                case "behavior":
                    if (!scene || behavior != 0) return false;
                    behavior = 1;
                    break;
                case "/behavior":
                    if (!scene || behavior != 1) return false;
                    behavior = 0;
                    break;
                case "change map":
                    if (!scene || behavior != 1 || changeSeen || at == 0 || Text(tokens[at - 1],resolve) != "behavior" ||
                        !MapChange(at) || at + 16 >= tokens.Count || Text(tokens[at + 16],resolve) != "/behavior") return false;
                    changeSeen = true;
                    at += 15;
                    break;
                case "/change map":
                    return false;
            }
        }
        return mapSeen && typeSeen && moveSeen && scenes > 0 && !scene && behavior == 0 && changeSeen;

        bool Scalar(int at, int value) => at + 1 < tokens.Count && tokens[at + 1].Type == 0 && tokens[at + 1].Value == value &&
            (at + 2 == tokens.Count || tokens[at + 2].Type == 3);

        bool MapChange(int start)
        {
            string[] fields = ["x min","x max","y min","y max","z min","z max","character view"];
            if (start + 15 >= tokens.Count || Text(tokens[start + 15],resolve) != "/change map") return false;
            for (int index = 0; index < fields.Length; index++)
                if (Text(tokens[start + 1 + 2 * index],resolve) != fields[index] || tokens[start + 2 + 2 * index].Type != 0)
                    return false;
            for (int axis = 0; axis < 3; axis++)
            {
                long width = (long)tokens[start + 4 + 4 * axis].Value - tokens[start + 2 + 4 * axis].Value;
                if (width < 0 || width > int.MaxValue) return false;
            }
            // CharacterView仅为显示布尔；两值的已证生产者都发送同一kind1。
            return tokens[start + 14].Value is 0 or 1;
        }
    }

    static string Text(PvfToken token, Func<int, string> resolve) => token.Type == 3
        ? resolve(token.Value).Trim('[',']').ToLowerInvariant() : "";
}
