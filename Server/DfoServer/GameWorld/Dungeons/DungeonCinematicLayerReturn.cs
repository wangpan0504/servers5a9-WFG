using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonCinematicLayerReturn(int MapId, string MapPath, string ActionPath, int CinematicId, string CinematicPath)
{
    // 仅指无APC绑定的10 token进图动作；APC动作沿ActorId的独立资格处理。
    public bool IsOnStartMap { get; init; }
    // 原ACT选择的APC模板，与网络对象key不同。
    public int? ActorId { get; init; }
}

// 只解释已支持的地图演出导航声明，不执行CMT或据此签发死亡、清房与奖励。
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
        // LIMIT 1 是末层返回用的一次性动作；ON START MAP 是进图即播、随后 CHANGE MAP 换下层。
        if (tokens.Count == 11 && Tag(0,"trigger") && Tag(1,"limit") && Number(2,1) &&
            Tag(3,"do behavior") && Tag(4,"me") && Number(5,0) && Tag(6,"/trigger") &&
            Tag(7,"behavior") && Tag(8,"cinematic") && tokens[9].Type == 0 && tokens[9].Value > 0 && Tag(10,"/behavior"))
            return tokens[9].Value;
        if (tokens.Count == 10 && Tag(0,"trigger") && Tag(1,"on start map") &&
            Tag(2,"do behavior") && Tag(3,"me") && Number(4,0) && Tag(5,"/trigger") &&
            Tag(6,"behavior") && Tag(7,"cinematic") && tokens[8].Type == 0 && tokens[8].Value > 0 && Tag(9,"/behavior"))
            return tokens[8].Value;
        return null;

        bool Tag(int at, string expected) => Text(tokens[at],resolve) == expected;
        bool Number(int at, int expected) => tokens[at].Type == 0 && tokens[at].Value == expected;
    }

    // APC进图演出只提供导航来源；不执行CHECKUP OBJECT、CHANGE AI或演出中的死亡。
    public static (int ActorId, int CinematicId)? ActorCinematic(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count != 36 || !Tag(0,"trigger") || !Tag(1,"on start map") || !Tag(2,"which") ||
            !Tag(3,"ai character") || !Tag(4,"is index") || tokens[5] is not { Type:0,Value:>0 } ||
            !Tag(6,"/is index") || !Tag(7,"do behavior") || !Tag(8,"checkup object") || !Number(9,1) ||
            !Tag(10,"do behavior") || !Tag(11,"me") || !Number(12,0) || !Tag(13,"/trigger") ||
            !Tag(14,"behavior") || !Tag(15,"cinematic") || tokens[16] is not { Type:0,Value:>0 } ||
            !Tag(17,"/behavior") || !Tag(18,"behavior") || !Tag(35,"/behavior")) return null;
        for (int index = 0; index < 4; index++)
        {
            int at = 19 + 4 * index;
            if (!Tag(at,"change ai") || !Tag(at+1,"normal") || !Number(at+2,index) ||
                tokens[at+3].Type != 6 || resolve(tokens[at+3].Value) != "none.ai") return null;
        }
        return (tokens[5].Value,tokens[16].Value);

        bool Tag(int at,string value) => Text(tokens[at],resolve) == value;
        bool Number(int at,int value) => tokens[at] is { Type:0 } && tokens[at].Value == value;
    }

    public static bool IsNonCombatMapChange(int mapId, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
        => IsMapChange(mapId,tokens,resolve,actorBound:false);

    public static bool IsActorMapChange(int mapId, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
        => IsMapChange(mapId,tokens,resolve,actorBound:true);

    private static bool IsMapChange(int mapId, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, bool actorBound)
    {
        bool mapSeen = false, typeSeen = false, moveSeen = false, scene = false, damageSeen = false, changeSeen = false;
        bool combatSeen = false;
        int scenes = 0, behavior = 0, behaviorAt = -1;
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
                    if (!scene || behavior != 0 || damageSeen || !Scalar(at,0) && !(actorBound && Scalar(at,1))) return false;
                    combatSeen |= Scalar(at,1);
                    damageSeen = true;
                    break;
                case "behavior":
                    if (!scene || behavior != 0) return false;
                    behavior = 1;
                    behaviorAt = at;
                    break;
                case "/behavior":
                    if (!scene || behavior != 1) return false;
                    behavior = 0;
                    break;
                case "change map":
                    bool prefix = actorBound ? at == behaviorAt+10 && PositionActor(behaviorAt+1) :
                        at > 0 && Text(tokens[at-1],resolve) == "behavior";
                    if (!scene || behavior != 1 || changeSeen || !prefix || !MapChange(at) ||
                        at + 16 >= tokens.Count || Text(tokens[at + 16],resolve) != "/behavior" ||
                        actorBound && (at+18 != tokens.Count || Text(tokens[at+17],resolve) != "/scene")) return false;
                    changeSeen = true;
                    at += 15;
                    break;
                case "/change map":
                    return false;
            }
        }
        return mapSeen && typeSeen && moveSeen && scenes > 0 && !scene && behavior == 0 && changeSeen && (!actorBound || combatSeen);

        bool PositionActor(int at) => at+8 < tokens.Count && Text(tokens[at],resolve) == "actor" &&
            Text(tokens[at+1],resolve) == "type" && Text(tokens[at+2],resolve) == "pos" &&
            Text(tokens[at+3],resolve) == "index" && tokens[at+4] is { Type:0,Value:0 } &&
            Text(tokens[at+5],resolve) == "pos" && tokens[at+6] is { Type:0,Value:0 } &&
            tokens[at+7] is { Type:0,Value:0 } && Text(tokens[at+8],resolve) == "/actor";

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
