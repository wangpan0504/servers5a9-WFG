using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using PvfLib;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;
namespace DfoServer;
internal static class RevisedBeastResourceTests
{
 public static void RunResources(PvfArchive pvf)
 {
  var c=DungeonCatalog.Load(pvf);
  var rooms=DungeonRoomCatalog.Load(pvf);
  var character=new Character(1,0,"BeastTest",0,new(51,1,0,0),null) { Level=95,CompletedQuests=[5927] };
  foreach(int id in Enumerable.Range(RevisedBeastSelection.FirstDungeon,5))
  {
   var d=c.Get(id);
   Console.WriteLine($"REVISED_BEAST_RESOURCE id={id} path={d.Path} mazes={d.Mazes.Count} raw={string.Join(',',d.RawSections.Select(x=>x.Tag).Distinct())}");
   Console.WriteLine("REVISED_BEAST_DIFF "+string.Join("|",d.RawSections.Where(x=>x.Tag == "difficulty level" || x.Tag == "difficulty size").Select(x=>x.Tag+":"+string.Join(",",x.Tokens.Select(t=>t.Value)))));
   if(d.Mazes.Count==0) throw new Exception($"改版魔兽副本无迷宫: {id}");
   var m=c.RevisedBeastMaze(id);
   Console.WriteLine($"REVISED_BEAST_MAZE id={id} size={m.Width}x{m.Height} greed={m.Greed} starts={string.Join(';',m.Starts)} bosses={string.Join(';',m.Bosses)} maps={m.MapSpecification?.Count ?? 0}");
   if(m.MapSpecification is { } maps) Console.WriteLine("REVISED_BEAST_MAPS "+string.Join("|",maps.OrderBy(x=>x.Position.Y).ThenBy(x=>x.Position.X).Select(x=>$"{x.Position.X},{x.Position.Y}:{x.Id}:{x.Kind}")));
   That(m.Width==2 && m.Height==1 && m.StaticTopology.Links?.Count==1,"原生改版双房有唯一连接");
   foreach(var map in m.MapSpecification!)
   {
    var room=rooms.Get(map,requirePassiveDefinitions:false);
    That(room.Map.Id==map.Id,"两张地图实体按DNG引用读取");
   }
   for(byte difficulty=0;difficulty<3;difficulty++)
   {
    That(DungeonEntryPlan.TryCreateRevisedBeast(c,character,(uint)id,difficulty,out var plan,out var reason),"改版真实资源可按三难度建立计划："+reason);
    That(plan!.IsRevisedBeast && plan.Difficulty==difficulty && plan.StartMap.Position==new DungeonPoint(0,0) &&
      plan.BossMap.Position==new DungeonPoint(1,0) && plan.Maze.MapSpecification!.Count==2,"改版保留实际难度、出生点与双房");
    var encounter=rooms.RevisedBeastEncounter(difficulty);
    That(encounter.HierroId==107005374+difficulty && encounter.AnneId==66159+difficulty &&
        encounter.AnneRewardItem==10305186+difficulty && encounter.HierroRewardItem==10305183+difficulty,
        "三档BOSS与奖励袖珍罐按ETC配对，不使用首版铜像实体");
    var bossRoom=encounter.Apply(rooms.Get(plan.BossMap,requirePassiveDefinitions:false));
    That(bossRoom.Monsters.Select(row=>row.MonsterId).Order().SequenceEqual(new[]{encounter.AnneId,encounter.HierroId}.Order()),"遭遇只含安妮与歇罗");
    var original=rooms.Get(plan.BossMap,requirePassiveDefinitions:false);
    That(bossRoom.Monsters.Zip(original.Monsters).All(pair=>pair.First.MonsterId==pair.Second.MonsterId &&
        pair.First.RawValues.SequenceEqual(pair.Second.RawValues) && pair.First.Modifiers.SequenceEqual(pair.Second.Modifiers)) &&
        bossRoom.Monsters[0].MonsterId==encounter.HierroId && bossRoom.Monsters[1].MonsterId==encounter.AnneId,
        "原生MAP演员及行序完整保留：歇罗序号0，安妮序号1");
    var hierroSource=bossRoom.Monsters[0].RawValues;
    That((hierroSource[3]>=2000 || hierroSource[4]<0) && bossRoom.PassiveObjects.Any(row=>row.ObjectId==86118),
        "原生歇罗场外待场坐标及86118召唤检查对象存在，不复制普通怪位置");
    var expectedMaps=new Dictionary<int,int[]>{[5335]=[400365,400383,400384],[5336]=[400367,400394,400395],
        [5337]=[400371,400414,400415],[5338]=[400369,400404,400405],[5339]=[400363,400373,400374]};
    That(plan.BossMap.Id==expectedMaps[id][difficulty],"五图三档匹配独立核对的原生遭遇MAP");
    That(encounter.Apply(rooms.Get(plan.StartMap,requirePassiveDefinitions:false)).Monsters.Count==0,"首房为空");
    var entry=plan with { RevisedBeastEncounter=encounter };
    ushort key=10;
    var routePlans=entry.Maze.MapSpecification!.Select(map=>
    {
        var room=encounter.Apply(rooms.Get(map,requirePassiveDefinitions:false));
        var built=DungeonMonsterPlan.FromRoom(room,character.Id,key,entry.BasisLevel);
        if(built.Monsters.Count>0) key=checked((ushort)built.NextObjectKey);
        return built;
    }).ToArray();
    That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new(routePlans,[new(new(0,0),new(1,0))]),
        new RevisedBeastEncounterTests.Authority(),out _,out var runReason),"真实五图三档完整怪物规划复验："+runReason);
    var wire=DungeonPackets.Information(plan);
    That(wire[4]==difficulty && wire[7]==0 && wire[8]==255 && wire[9]==255,"改版NOTI28保留DGN的无固定Boss哨兵");
    That(!plan.IsTerminalMap(plan.BossMap),"内部锚点不冒充连续魔兽遭遇完成");
   }
   That(!DungeonEntryPlan.TryCreateRevisedBeast(c,character,(uint)id,3,out _,out _),"拒绝第四档");
  }
  That(!DungeonEntryPlan.TryCreateRevisedBeast(c,character,310,0,out _,out _),"首版不进入改版适配器");
 }
}
