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
        return indices is [var at] && at+1 < tokens.Count && tokens[at+1] is { Type:0,Value:>0 } &&
            (at+2 == tokens.Count || tokens[at+2].Type == 3) ? tokens[at+1].Value : null;
    }

    public static int? StartCinematic(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        // 只认首个ON START MAP触发ME行为0；其它触发不授予播放或死亡资格。
        if (!Match(tokens,0,resolve,"trigger","on start map","do behavior","me",0,"/trigger")) return null;
        int first = Enumerable.Range(0,tokens.Count).FirstOrDefault(i => Tag(tokens[i],resolve) == "behavior",-1);
        return first >= 0 && first+3 < tokens.Count && Tag(tokens[first+1],resolve) == "cinematic" &&
            tokens[first+2] is { Type:0,Value:>0 } && Tag(tokens[first+3],resolve) == "/behavior"
            ? tokens[first+2].Value : null;
    }

    public static bool DeathChangesLayer(int mapId,IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        if (!Map(tokens,mapId,resolve)) return false;
        var changes = Enumerable.Range(0,tokens.Count).Where(i => Tag(tokens[i],resolve) == "change map").ToArray();
        if (changes is not [var at] || at < 10 ||
            !Match(tokens,at-10,resolve,"behavior","actor","type","character","index",0,"pos",0,0,"/actor")) return false;
        string[] fields = ["x min","x max","y min","y max","z min","z max","character view"];
        if (at+17 >= tokens.Count || !Match(tokens,at+15,resolve,"/change map","/behavior","/scene") || at+18 != tokens.Count)
            return false;
        for (int i=0;i<fields.Length;i++)
            if (Tag(tokens[at+1+2*i],resolve) != fields[i] || tokens[at+2+2*i].Type != 0) return false;
        for (int i=0;i<3;i++) if (tokens[at+2+4*i].Value > tokens[at+4+4*i].Value) return false;
        return tokens[at+14].Value is 0 or 1;
    }

    public static bool RewardedDestroy(int mapId,int ordinal,IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        if (!Map(tokens,mapId,resolve)) return false;
        var destroys = Enumerable.Range(0,tokens.Count).Where(i => Tag(tokens[i],resolve) == "destroy").ToArray();
        if (destroys is not [var at] || at < 10 || at+6 >= tokens.Count ||
            !Match(tokens,at-10,resolve,"behavior","actor","type","monster","index",ordinal,"pos") ||
            tokens[at-3].Type != 0 || tokens[at-2].Type != 0 || Tag(tokens[at-1],resolve) != "/actor") return false;
        return Match(tokens,at+1,resolve,"is show die",0,"is reward",1,"/destroy","/behavior");
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
