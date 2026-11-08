using PvfLib;
namespace DfoServer.GameWorld.Dungeons;

internal sealed record RevisedBeastEncounter(int AnneId, string AnnePath, int HierroId, string HierroPath,
    int AnneRewardItem, int HierroRewardItem, int CoinLimit, int CoinTimeoutMilliseconds, int DailyRewardLimit, int WeeklyRewardLimit,
    int DailyEntryLimit, int WeeklyEntryLimit)
{
    public DungeonIncountRow EntryCounts => new(0, [5335,5336,5337,5338,5339], DailyEntryLimit, false, WeeklyEntryLimit);
    internal bool UsesOrdinaryBossDeath(int monsterId) => monsterId == HierroId;

    internal static RevisedBeastEncounter Read(PvfArchive archive, IReadOnlyDictionary<int,string> monsters, byte difficulty)
    {
        if (difficulty > 2) throw new InvalidDataException("改版魔兽难度越界");
        var tokens = archive.ReadTokens("etc/beastdungeon/beastdungeon.etc");
        int[] Field(string tag)
        {
            int start = -1;
            for (int i=0; i<tokens.Length; i++)
                if (tokens[i].Type == 3 && archive.ResolveString(tokens[i].Value) == "["+tag+"]")
                { if (start >= 0) throw new InvalidDataException("改版魔兽配置重复："+tag); start=i+1; }
            if (start < 0) throw new InvalidDataException("改版魔兽配置缺失："+tag);
            var values = tokens.Skip(start).TakeWhile(t=>t.Type != 3).ToArray();
            if (values.Any(t=>t.Type != 0)) throw new InvalidDataException("改版魔兽配置不是整数："+tag);
            return values.Select(t=>t.Value).ToArray();
        }
        var rewards=Field("beast dungeon reward");
        if (rewards.Length != 12) throw new InvalidDataException("改版魔兽三档奖励配对不完整");
        int at=difficulty*4, hierro=rewards[at], anne=rewards[at+2];
        string Path(int id) => monsters.TryGetValue(id,out var path) && archive.Contains(path)
            ? path : throw new InvalidDataException("改版魔兽怪物未注册："+id);
        var limit=Field("character coin limit"); var timeout=Field("coin limit time");
        if (limit is not [>=0 and <=255] || timeout is not [>0 and <=60000])
            throw new InvalidDataException("改版魔兽复活配置非法");
        var daily=Field("limit reward count"); var weekly=Field("limit weekly reward count");
        var dailyEntry=Field("limit enter count"); var weeklyEntry=Field("limit weekly enter count");
        if (daily is not [>0 and <=255] || weekly is not [>0 and <=255] || dailyEntry is not [>0] || weeklyEntry is not [>0])
            throw new InvalidDataException("改版魔兽入场或领奖上限缺失或越界");
        return new(anne,Path(anne),hierro,Path(hierro),rewards[at+3],rewards[at+1],limit[0],timeout[0],daily[0],weekly[0],dailyEntry[0],weeklyEntry[0]);
    }

    // 校验原生遭遇房，保留全部演员初始化资源；铜像由安妮入场动作呈现。
    public DungeonRoomDefinition Apply(DungeonRoomDefinition room)
    {
        if (room.Map.Position != new DungeonPoint(1,0))
            return room with { Monsters=[], AiCharacters=[], SpecialObjects=[], MonsterTeams=null, AltarWaves=null };
        if (room.Monsters.Count!=2 || !room.Monsters.Select(actor=>actor.MonsterId).Order().SequenceEqual(new[]{AnneId,HierroId}.Order()) ||
            room.Monsters.Any(actor=>actor.PlacementKind!="[fixed]" || actor.RankKind!="[boss]" || actor.RawValues.Count!=8) ||
            room.Monsters.Single(actor=>actor.MonsterId==AnneId).Path!=AnnePath ||
            room.Monsters.Single(actor=>actor.MonsterId==HierroId).Path!=HierroPath)
            throw new InvalidDataException("改版魔兽遭遇房演员与难度配置不匹配");
        // 保留MAP原始顺序、场外落点、dummy标记、召唤检查对象和其他初始化资源。
        return room;
    }
}
