using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 只描述资源绑定，不执行演出，也不把DESTROY声明当作已发生的死亡。
internal sealed record DungeonCinematicBossEvidence(int MapId, string MapPath, int MonsterOrdinal,
    int MonsterId, string MonsterPath, int CinematicId, string CinematicPath);

internal static class DungeonCinematicBossTerminalRules
{
    public static int? DieCinematic(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        var indices = Enumerable.Range(0,tokens.Count).Where(i => Tag(tokens[i],resolve) == "die cinematic").ToArray();
        if (indices is not [var at] || at+1 >= tokens.Count || tokens[at+1] is not { Type:0,Value:>0 }) return null;
        int end = at+2;
        // 当前MOB也可声明死亡后向下换层；其它方向及多余参数不能借用这条路线。
        if (end < tokens.Count && tokens[end].Type != 3)
        {
            if (tokens[end].Type != 6 || resolve(tokens[end].Value) != "down") return null;
            end++;
        }
        return end == tokens.Count || tokens[end].Type == 3 ? tokens[at+1].Value : null;
    }

    public static bool DeathChangesLayer(int mapId,IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        if (!Map(tokens,mapId,resolve)) return false;
        var changes = Enumerable.Range(0,tokens.Count).Where(i => Tag(tokens[i],resolve) == "change map").ToArray();
        if (changes is not [var at] || at < 1 ||
            !Match(tokens,at-1,resolve,"behavior") &&
            !Match(tokens,at-10,resolve,"behavior","actor","type","character","index",0,"pos",0,0,"/actor")) return false;
        string[] fields = ["x min","x max","y min","y max","z min","z max","character view"];
        if (at+17 >= tokens.Count || !Match(tokens,at+15,resolve,"/change map","/behavior","/scene") || at+18 != tokens.Count)
            return false;
        for (int i=0;i<fields.Length;i++)
            if (Tag(tokens[at+1+2*i],resolve) != fields[i] || tokens[at+2+2*i].Type != 0) return false;
        for (int i=0;i<3;i++) if (tokens[at+2+4*i].Value > tokens[at+4+4*i].Value) return false;
        return tokens[at+14].Value is 0 or 1;
    }

    static bool Map(IReadOnlyList<PvfToken> tokens,int id,Func<int,string> resolve) =>
        Enumerable.Range(0,tokens.Count).Count(i => Tag(tokens[i],resolve) == "map") == 1 &&
        Match(tokens,0,resolve,"map",id,"type","scenario") && TutorialSceneFile.Cinematic(tokens,resolve).MapId == id;

    static bool Match(IReadOnlyList<PvfToken> tokens,int at,Func<int,string> resolve,params object[] pattern)
    {
        if (at < 0 || at+pattern.Length > tokens.Count) return false;
        for (int i=0;i<pattern.Length;i++)
            if (pattern[i] is int number ? tokens[at+i].Type != 0 || tokens[at+i].Value != number :
                Tag(tokens[at+i],resolve) != (string)pattern[i]) return false;
        return true;
    }
    static string Tag(PvfToken token,Func<int,string> resolve) => token.Type == 3 ? resolve(token.Value).Trim('[',']').ToLowerInvariant() : "";
}
