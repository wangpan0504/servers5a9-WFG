using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunOperationHopeAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        OperationHopeSessionTests.Run(resources.Archive,resources.ItemResources);
        await using var f = new TcpTestFixture(source,Path.Combine(root,"operation-hope"),gm:true,resources:resources);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var hope = OperationHopeCatalog.Load(resources.Archive,dungeons,resources.Quests,rooms);
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 8523));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).ToHashSet();
        done.UnionWith(resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options
            .Where(option => option.DungeonId == 8523 && option.QuestId > 0 && !option.InProgress).Select(option => option.QuestId));
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var bounds = area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && gate.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        const string account = "hope-tcp";
        Character created;
        short potion;
        using (var owner = new AccountOwners().Acquire(account,() => {}))
        {
            var seed = CharacterCreation.Prepare("HopeTcp",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var repository = new CharacterRepository(f.Database,resources.Quests);
            created = repository.Create(owner,seed with { Level=95,AcceptedStoryLevel=95,
                CompletedQuests=done.ToArray(),InitialLearning=SkillPointLedger.Synchronize(0,0,95,seed.InitialLearning with
                    {Experience=resources.Growth.GetLevelStartExperience(95)},resources.Learning) });
            var service = new InventoryService(resources.ItemResources.Items,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces);
            created = InventoryService.Commit(repository,owner,created,service.Grant(created,[new(2600014,1)]));
            potion = created.Inventory.Items.Single(row => row.Core.ItemId == 2600014).Position.Index;
        }
        await f.StartAsync();
        That(DfoServer.Network.Management.DevelopmentGmClient.TryExecute(f.Options.GmPort,"infinite-fatigue off",out var mode) &&
            mode == "OK {\"infiniteFatigue\":false}","显式关闭默认开发无限疲劳，验证真实扣费");
        using var failedPeer = Open(f,account);
        var peer = failedPeer;
        peer.Select(prepared:true);peer.Barrier();
        peer.Send(237,Build(w=>{w.Write(potion);w.Write(2600014);w.Write((byte)0);w.Write(area.Town);w.Write(area.Area);
            w.Write(position.X);w.Write(position.Y);w.Write((byte)5);}));
        That(peer.Expect(1,237).Body[0]==1,"真实传送道具进入满足6170的希望城镇");
        for(int read=0;read<16 && peer.Read() is not {Kind:0,Opcode:24};read++) {}
        QuestLifecycleTcpTests.DrainNotifications(peer);
        long fatigueBefore=(long)f.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0",created.Id)[0][0];
        long activeBefore=(long)f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0",created.Id)[0][0];
        peer.Send(1598,[]);peer.Expect(1,1598,[1,0,0,0,0]);
        peer.Send(1598,[]);peer.Expect(1,1598,[1,0,0,0,0]);
        That((long)f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0",created.Id)[0][0]==activeBefore,
            "选择阶段仅准备实例，尚未新增希望任务");
        peer.Send(15,Build(w=>w.Write((uint)8523)));peer.Expect(1,15,[1]);
        peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]);QuestLifecycleTcpTests.DrainNotifications(peer);
        var entry = new byte[25];BinaryPrimitives.WriteUInt32LittleEndian(entry,8523);entry[4]=hope.Difficulty;entry[9]=255;entry[10]=255;
        string CountState() => f.Snapshot("SELECT * FROM character_dungeon_incount WHERE character_id=$p0 ORDER BY dungeon",created.Id);
        string initialCounts = CountState();
        string initialAttempt = f.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0",created.Id);
        f.Sql("CREATE TRIGGER hope_tcp_entry_fail AFTER INSERT ON character_active_main_quests WHEN NEW.quest_id BETWEEN 6171 AND 6179 BEGIN SELECT RAISE(ABORT,'hope entry rollback'); END");
        peer.Send(16,entry); peer.Closed();
        That(CountState() == initialCounts && f.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0",created.Id) == initialAttempt &&
            (long)f.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0",created.Id)[0][0] == fatigueBefore &&
            (long)f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0",created.Id)[0][0] == activeBefore,
            "真实ServerApplication装配的希望SQL失败无成功帧，次数/疲劳/尝试号/任务整笔回滚");
        f.Sql("DROP TRIGGER hope_tcp_entry_fail");
        using var retryPeer = Open(f,account); peer = retryPeer;
        // 换频道式重连保留已成功传送的真实城镇快照；普通名册选角会回个人房间。
        peer.Select(prepared:true,resumeTown:true,room:false); peer.Barrier();
        peer.Send(1598,[]);peer.Expect(1,1598,[1,0,0,0,0]);
        peer.Send(15,Build(w=>w.Write((uint)8523)));peer.Expect(1,15,[1]);
        peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]);QuestLifecycleTcpTests.DrainNotifications(peer);
        peer.Send(16,entry);
        var assigned=peer.Expect(1,31).Body;
        ushort quest=U16(assigned,1);
        That(assigned[0]==1 && hope.Missions.ContainsKey(quest),"1598后16提交原生31希望任务");
        var info=peer.Expect(0,28).Body;int ordinal=info[7];var maze=hope.Mazes[ordinal];
        byte[] map=peer.Expect(0,29).Body;QuestLifecycleTcpTests.DrainNotifications(peer);
        var incountRow = DungeonIncountCatalog.Load(resources.Archive).Find(8523) ?? throw new InvalidDataException("希望缺少次数行");
        That(f.Query("SELECT daily_used,weekly_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=$p1",created.Id,incountRow.CountKey) is [[1L,1L]],
            "真实socket失败后重连重试只扣一次希望日周次数");
        var path=Route(new(map[0],map[1]),false);
        for(int index=0;index<path.Count;index++)
        {
            Load(peer);QuestLifecycleTcpTests.DrainNotifications(peer);
            foreach(int at in Enumerable.Range(0,map[18]).Select(i=>19+22*i).Where(at=>map[at+13]!=9 && map[at+21]==0).OrderBy(at=>map[at+13]==3))
            {
                peer.Send(39,Death(U16(map,at+6),peer.Id,map[at+12]));
                peer.Expect(0,38);QuestLifecycleTcpTests.DrainNotifications(peer);
            }
            if(index+1==path.Count)break;
            var next=path[index+1];var move=new byte[100];move[0]=checked((byte)next.X);move[1]=checked((byte)next.Y);
            peer.Send(45,move);peer.Expect(1,45,[1]);map=peer.Expect(0,29).Body;
        }
        That((long)f.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0",created.Id)[0][0]==fatigueBefore-16,
            "真实网络完整四区域仅扣16疲劳");
        peer.Send(34,Build(w=>{w.Write((ushort)34);w.Write(quest);w.Write(ushort.MaxValue);w.Write((ushort)1);w.Write(ushort.MaxValue);}));
        That(peer.Expect(1,34).Body[0]==1,"原目标达成并真实终点死亡后34发奖");
        QuestLifecycleTcpTests.DrainNotifications(peer);
        That(f.Query("SELECT count(*) FROM character_operation_hope_reward_receipts WHERE character_id=$p0",created.Id) is [[1L]] &&
            f.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,quest) is [[0L]],
            "同实例唯一收据且不污染普通完成历史");

        List<DungeonPoint> Route(DungeonPoint start,bool met)
        {
            var queue=new Queue<(DungeonPoint Point,bool Met,List<DungeonPoint> Path)>();
            var seen=new HashSet<(DungeonPoint,bool)>();queue.Enqueue((start,met,[]));
            while(queue.TryDequeue(out var node))
            {
                var current=maze.Layout.MapSpecification!.Single(row=>row.Position==node.Point);
                bool hit=node.Met || maze.MissionMaps[quest].Contains(current.Id);
                if(!seen.Add((node.Point,hit)))continue;
                var path=node.Path.Append(node.Point).ToList();
                if(hit && OperationHopePlan.IsDeclaredTerminal(hope,ordinal,current.Id))return path;
                foreach(var next in maze.Layout.MapSpecification!)
                    if(OperationHopePlan.IsDeclaredTransition(hope,ordinal,node.Point,next.Position))queue.Enqueue((next.Position,hit,path));
            }
            throw new InvalidDataException("真实希望迷宫没有经过本次任务目标的终点路线");
        }
    }
}
