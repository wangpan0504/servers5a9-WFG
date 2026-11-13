using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 改版实体的专用资源适配；保留原DGN，普通规划器只借用可行走房间和内部锚点。
internal static class RevisedBeastLayout
{
    internal static IReadOnlyDictionary<int, string> ReadMapPaths(PvfArchive archive, IReadOnlyDictionary<int, string> registered)
    {
        var result = registered.ToDictionary(row => row.Key, row => row.Value);
        var native = new Dictionary<int, string>();
        foreach (var file in archive.Files.Where(file => file.Path.StartsWith("map/devildom_beast/", StringComparison.Ordinal) &&
            !file.Path.Contains("/legacy_mirror/", StringComparison.OrdinalIgnoreCase) && file.Path.EndsWith(".map", StringComparison.Ordinal)))
            if (int.TryParse(Path.GetFileNameWithoutExtension(file.Path).Split('_')[0], out int id) && !native.TryAdd(id, file.Path))
                throw new InvalidDataException("改版魔兽地图编号重复: " + id);
        foreach (var row in native) result[row.Key] = row.Value;
        return result;
    }

    public static DungeonMaze Read(DungeonDefinition definition, Func<int,string> resolve,
        IReadOnlyDictionary<int,string> paths, Func<string,bool> exists,
        IReadOnlyDictionary<(int Dungeon, byte Difficulty), DungeonMap> bossMaps, byte difficulty)
    {
        if (definition.Id is < 5335 or > 5339 ||
            !definition.RawSections.Any(row => row.Tag == "beast dungeon" && row.Tokens is [{ Type: 0, Value: 1 }]))
            throw new InvalidDataException("改版魔兽实体标记不匹配");
        var sections = definition.RawSections;
        int begin = sections.ToList().FindIndex(row => row.Tag == "maze info");
        if (begin < 0 || sections.Skip(begin+1).Any(row => row.Tag == "maze info"))
            throw new InvalidDataException("改版魔兽迷宫数量不匹配");
        var block = sections.Skip(begin+1).ToArray();
        IReadOnlyList<PvfToken> Field(string tag)
        {
            var rows = block.Where(row => row.Tag == tag).ToArray();
            return rows.Length == 1 ? rows[0].Tokens : throw new InvalidDataException("改版魔兽字段缺失或重复: " + tag);
        }
        int[] Numbers(string tag) => Field(tag).Select(token => token.Type == 0 ? token.Value :
            throw new InvalidDataException("改版魔兽整数类型错误: " + tag)).ToArray();
        if (Numbers("size") is not [2,1] || Numbers("start map") is not [0,0] || Numbers("boss map") is not [-1,-1] ||
            Field("greed") is not [{ Type: 6 } greed] || resolve(greed.Value) != "BBEE")
            throw new InvalidDataException("改版魔兽双房拓扑不匹配");
        var values = Field("map specification");
        if (values.Count != 8) throw new InvalidDataException("改版魔兽房间数量不匹配");
        var maps = new List<DungeonMap>();
        for (int at = 0; at < values.Count; at += 4)
        {
            if (values[at].Type != 6 || values.Skip(at+1).Take(3).Any(token => token.Type != 0))
                throw new InvalidDataException("改版魔兽地图行类型错误");
            string kind = resolve(values[at].Value);
            int x = values[at+1].Value, y = values[at+2].Value, id = values[at+3].Value;
            if (y != 0 || x is < 0 or > 1 || kind != (x == 0 ? "map" : "Beast") ||
                maps.Any(map => map.Position.X == x) || !paths.TryGetValue(id,out var path) || !exists(path))
                throw new InvalidDataException("改版魔兽房间身份或引用无效: " + id);
            // boss仅为服务端房间容器的内部锚点，不改变原生Beast类型或下发的-1,-1。
            if (x == 1)
            {
                if (!bossMaps.TryGetValue((definition.Id,difficulty),out var encounter))
                    throw new InvalidDataException("改版魔兽缺少唯一原生遭遇房: " + definition.Id + "/" + difficulty);
                maps.Add(encounter with { Kind="boss", Position=new(x,y) });
            }
            else maps.Add(new("map",new(x,y),id,path));
        }
        var maze = new DungeonMaze(0,2,1,resolve(greed.Value),[new(0,0)],[new(1,0)],maps.ToArray(),[]);
        if (!maze.StaticTopology.Supported) throw new InvalidDataException("改版魔兽房间连接无效");
        return maze;
    }

    // 实验适配：Beast占位房不能作为普通怪物房直接装载。以MAP自身归属及ETC三档演员配对
    // 恢复实际遭遇房；ETC已给出的地图行必须与反向索引一致，不按相似文件名或编号差值猜测。
    internal static IReadOnlyDictionary<(int Dungeon, byte Difficulty), DungeonMap> ReadBossMaps(
        PvfArchive archive, IReadOnlyDictionary<int,string> paths)
    {
        PvfToken[] Field(IReadOnlyList<PvfToken> tokens,string tag)
        {
            var starts=Enumerable.Range(0,tokens.Count).Where(i=>tokens[i].Type==3 && archive.ResolveString(tokens[i].Value)==tag).ToArray();
            return starts.Length==1 ? tokens.Skip(starts[0]+1).TakeWhile(t=>t.Type!=3).ToArray() : [];
        }
        var etc=archive.ReadTokens("etc/beastdungeon/beastdungeon.etc");
        var rewards=Field(etc,"[beast dungeon reward]");
        if(rewards.Length!=12 || rewards.Any(t=>t.Type!=0)) throw new InvalidDataException("改版魔兽三档演员配置不完整");
        var result=new Dictionary<(int Dungeon, byte Difficulty), DungeonMap>();
        foreach(var pair in paths.Where(p=>p.Value.StartsWith("map/devildom_beast/",StringComparison.Ordinal) && !p.Value.Contains("/legacy_mirror/", StringComparison.OrdinalIgnoreCase)))
        {
            var tokens=archive.ReadTokens(pair.Value);
            var owned=Field(tokens,"[dungeon]");
            if(owned is not [{Type:0,Value:>=5335 and <=5339} dungeon]) continue;
            var actors=Field(tokens,"[monster]");
            var ids=new List<int>();
            int at=0;
            while(at<actors.Length)
            {
                if(at+8>actors.Length || actors.Skip(at).Take(8).Any(t=>t.Type!=0)) break;
                ids.Add(actors[at].Value); at+=8;
                int first=at;
                while(at<actors.Length && actors[at].Type==6) at++;
                if(at-first<2) break;
            }
            if(at!=actors.Length || ids.Count!=2) continue;
            for(byte diff=0;diff<3;diff++)
            {
                if(!ids.Order().SequenceEqual(new[]{rewards[diff*4].Value,rewards[diff*4+2].Value}.Order())) continue;
                if(!result.TryAdd((dungeon.Value,diff),new("boss",new(1,0),pair.Key,pair.Value)))
                    throw new InvalidDataException("改版魔兽原生遭遇房不唯一: "+dungeon.Value+"/"+diff);
            }
        }
        var configured=Field(etc,"[beast dungeon]");
        if(configured.Length==0 || configured.Length%4!=0 || configured.Any(t=>t.Type!=0))
            throw new InvalidDataException("改版魔兽显式遭遇地图表不完整");
        for(int at=0;at<configured.Length;at+=4)
            for(byte diff=0;diff<3;diff++)
                if(!result.TryGetValue((configured[at].Value,diff),out var map) || map.Id!=configured[at+1+diff].Value)
                    throw new InvalidDataException("改版魔兽ETC与MAP归属或三档演员不一致");
        return result;
    }
}
