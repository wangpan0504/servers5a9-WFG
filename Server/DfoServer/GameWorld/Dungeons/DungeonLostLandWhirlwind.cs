using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 只识别美人鱼水旋风的完整本地导航链；距离、救援符号及抬升仍由客户端ACT执行。
internal static class DungeonLostLandWhirlwind
{
    public static DungeonLostLandWarp? Read(int ordinal, DungeonRoomPassiveObject source,
        IReadOnlyDictionary<int,string> objects, Func<string,IReadOnlyList<PvfToken>> read, Func<int,string> resolve)
    {
        if (source.ObjectId is not (37074 or 37077) || source.RawValues.Count != 4 ||
            source.Path is not { Length: > 0 } root || objects.GetValueOrDefault(source.ObjectId) != root) return null;
        bool returning = source.ObjectId == 37077;
        int generator = returning ? 37078 : 37075, whirlwind = returning ? 37079 : 37076;
        var chain = new List<string>();
        var npc = Load(root);
        var actions = DungeonScriptedLayerReturnRules.OtherActions(npc,resolve);
        string? basic = Action(root,DungeonCinematicLayerReturnRules.BasicAction(npc,resolve));
        if (actions is not [var castAction,var idleAction] || basic is null) return null;
        string? idle = Action(root,idleAction), cast = Action(root,castAction);
        if (idle is null || cast is null || idle == cast || returning && basic != idle) return null;
        if (!returning && !Match(Load(basic),resolve,
            "[motion]","[base ani]",null,"[/motion]","[trigger]","[frame]",0,0,
            "[dungeon symbol compare]","Event","[=]",1,"[do behavior]","[me]",0,"[/trigger]",
            "[behavior]","[set action]","[custom]",1,"[now]","[/behavior]")) return null;
        if (!Idle(Load(idle),resolve,returning) || !Match(Load(cast),resolve,
            "[motion]","[base ani]",null,"[sub ani]",null,0,1,"[/sub ani]","[/motion]",
            "[trigger]","[frame]",12,12,"[do behavior]","[me]",0,"[/trigger]",
            "[behavior]","[create passiveobject]","[index]",generator,"[particle filename]","","[level]",1,
            "[pos]",0,0,0,"[/create passiveobject]","[/behavior]",
            "[trigger]","[frame]",15,15,"[do behavior]","[me]",1,"[/trigger]",
            "[behavior]","[set action]","[custom]",1,"[now]","[/behavior]",
            "[trigger]","[frame]",1,1,"[do behavior]","[me]",2,"[/trigger]",
            "[behavior]","[say speech]",null,0,50,"[/behavior]")) return null;
        if (!objects.TryGetValue(generator,out string? generatorPath) || !objects.TryGetValue(whirlwind,out string? whirlwindPath)) return null;
        var generatorObject = Load(generatorPath);
        string? generate = Action(generatorPath,DungeonCinematicLayerReturnRules.BasicAction(generatorObject,resolve));
        if (generate is null || DungeonScriptedLayerReturnRules.OtherActions(generatorObject,resolve) is not null ||
            !Match(Load(generate),resolve,"[motion]","[base ani]",null,"[/motion]",
                "[trigger]","[frame]",3,3,"[do behavior]","[me]",0,"[/trigger]",
                "[behavior]","[create passiveobject]","[index]",whirlwind,"[particle filename]","","[level]",1,
                "[pos]",0,0,0,"[/create passiveobject]","[/behavior]",
                "[trigger]","[frame]",6,6,"[do behavior]","[me]",1,"[/trigger]",
                "[behavior]","[destroy]","[/behavior]")) return null;
        var whirlwindObject = Load(whirlwindPath);
        var whirlwindActions = DungeonScriptedLayerReturnRules.OtherActions(whirlwindObject,resolve);
        string? start = Action(whirlwindPath,DungeonCinematicLayerReturnRules.BasicAction(whirlwindObject,resolve));
        if (whirlwindActions is not [var moveAction,var endAction] || start is null) return null;
        string? move = Action(whirlwindPath,moveAction), end = Action(whirlwindPath,endAction);
        if (move is null || end is null || move == end || start == move || start == end ||
            !Match(Load(start),resolve,"[motion]","[base ani]",null,"[sound]",null,0,"[/sound]","[/motion]",
                "[trigger]","[frame]",8,8,"[do behavior]","[me]",0,"[/trigger]",
                "[behavior]","[set action]","[custom]",0,"[now]","[/behavior]") ||
            !Match(Load(end),resolve,"[motion]","[base ani]",null,"[/motion]",
                "[trigger]","[frame]",9,9,"[do behavior]","[me]",0,"[/trigger]",
                "[behavior]","[destroy]","[/behavior]")) return null;
        if (Move(Load(move),resolve) is not { } destination) return null;
        return new(ordinal,source.ObjectId,root,move,destination.Destination,destination.PlayerX,destination.PlayerY)
            { MoveParameter = 550, SourceChain = Array.AsReadOnly(chain.Distinct(StringComparer.Ordinal).ToArray()) };

        IReadOnlyList<PvfToken> Load(string path) { chain.Add(path); return read(path); }
    }

    internal static (DungeonPoint Destination,int PlayerX,int PlayerY)? Move(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        if (tokens.Count != 80 || tokens[57] is not { Type:0,Value:>=0 and <=byte.MaxValue } ||
            tokens[58] is not { Type:0,Value:>=0 and <=byte.MaxValue } ||
            tokens[59] is not { Type:0,Value:>=0 and <=ushort.MaxValue } ||
            tokens[60] is not { Type:0,Value:>=0 and <=ushort.MaxValue } ||
            !Match(tokens,resolve,"[motion]","[base ani]",null,"[sound]",null,2,null,13,null,25,null,36,null,48,null,59,"[/sound]","[/motion]",
                "[trigger]","[which]","[player]","[checkup]","[distance]","[<=]",50,"[/checkup]",
                "[do behavior]","[checkup object]",0,"[/trigger]",
                "[behavior]","[set speed]","[z axis]",2000,"[use my direction]","[/set speed]","[/behavior]",
                "[trigger]","[which]","[player]","[checkup]","[zpos]","[>=]",650,"[/checkup]","[checked no]","[>=]",1,
                "[do behavior]","[me]",1,"[do behavior]","[me]",2,"[/trigger]",
                "[behavior]","[move map]",tokens[57].Value,tokens[58].Value,tokens[59].Value,tokens[60].Value,550,
                "[is state check]",0,"[/move map]","[/behavior]",
                "[trigger]","[frame]",47,47,"[do behavior]","[me]",2,"[/trigger]",
                "[behavior]","[set action]","[custom]",1,"[now]","[/behavior]")) return null;
        return (new(tokens[57].Value,tokens[58].Value),tokens[59].Value,tokens[60].Value);
    }

    static bool Idle(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve,bool returning)
    {
        object?[] motion = ["[motion]","[base ani]",null,"[speech]",null,0,returning ? 100 : 50,"[/speech]","[/motion]"];
        object?[] dialog = ["[trigger]","[limit]",1,"[frame]",0,0,"[do behavior]","[me]",2,"[/trigger]"];
        object?[] casting = ["[trigger]","[frame]",0,0,"[which]","[passive]","[checkup]","[is index]",37075,37076,"[/is index]",
            "[distance]","[<=]",2000,"[/checkup]","[checked no]","[<=]",0,"[which]","[player]","[checkup]","[distance]","[<=]",80,
            "[/checkup]","[checked no]","[>=]",1,"[do behavior]","[me]",0,"[/trigger]",
            "[behavior]","[casting]",1000,1,"[normal]","[display target]","[do me]","[/casting]","[/behavior]",
            "[behavior]","[set action]","[custom]",0,"[now]","[/behavior]"];
        object?[] speech = ["[behavior]","[dialog]",null,2000,"[/behavior]",
            "[trigger]","[frame]",1,1,"[do behavior]","[me]",3,"[/trigger]",
            "[behavior]","[say speech]",null,0,50,"[/behavior]"];
        return Match(tokens,resolve,returning ? [..motion,..casting] : [..motion,..dialog,..casting,..speech]);
    }

    static string? Action(string source,string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        string path = PvfArchive.NormalizePath(source[..(source.LastIndexOf('/')+1)]+relative);
        return path.StartsWith("passiveobject/",StringComparison.Ordinal) && path.EndsWith(".act",StringComparison.Ordinal) ? path : null;
    }

    // 固定形状逐token核对；null只略过展示字符串，不略过触发器、条件、索引或数值。
    static bool Match(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve,params object?[] shape)
    {
        if (tokens.Count != shape.Length) return false;
        for (int at = 0; at < shape.Length; at++)
        {
            var token = tokens[at];
            bool matches = shape[at] switch
            {
                null => token.Type == 6 && !string.IsNullOrWhiteSpace(resolve(token.Value)),
                int value => token.Type == 0 && token.Value == value,
                string tag when tag.StartsWith('[') => token.Type == 3 && resolve(token.Value).Equals(tag,StringComparison.OrdinalIgnoreCase),
                string text => token.Type == 6 && resolve(token.Value) == text,
                _ => false
            };
            if (!matches) return false;
        }
        return true;
    }
}
