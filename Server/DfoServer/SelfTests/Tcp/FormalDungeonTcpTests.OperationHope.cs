using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
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
        OperationHopeSessionTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources);
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
        // 已开放两条主线通路的独立角色；希望领奖不得清掉黑市阶段或区域准入完成位。
        done.UnionWith([5936,6045,6115,6201,6301]);
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
                Inventory = seed.Inventory with { Items = seed.Inventory.Items.Add(new(0, new(InventoryListType.Main, 3),
                    InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(400360000)) with { Durability = 25 })) },
                CompletedQuests=done.ToArray(),InitialLearning=SkillPointLedger.Synchronize(0,0,95,seed.InitialLearning with
                    {Experience=resources.Growth.GetLevelStartExperience(95)},resources.Learning) });
            var service = new InventoryService(resources.ItemResources.Items,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces);
            created = InventoryService.Commit(repository,owner,created,service.Grant(created,[new(2600014,1)]));
            potion = created.Inventory.Items.Single(row => row.Core.ItemId == 2600014).Position.Index;
        }
        // 独立夹具预置当日哈林日常已领，避免自动重新分配6115掩盖“最后活动槽”边界。
        f.Sql("UPDATE characters SET quest_cycle_day=$p0 WHERE id=$p1; " +
            "UPDATE character_completed_quests SET daily_period=$p0 WHERE character_id=$p1 AND quest_id=6115",
            DfoServer.Game.Quests.QuestCycle.Day(f.UtcNow),created.Id);
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
        That(f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0",created.Id) is [[0L]],
            "希望单任务回归在入场前没有其它活动任务");
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
        int progressTransitions = 0;
        for(int index=0;index<path.Count;index++)
        {
            Load(peer);QuestLifecycleTcpTests.DrainNotifications(peer);
            uint scalar = checked((uint)(long)f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1", created.Id, quest)[0][0]);
            var revision = f.Query("SELECT revision FROM characters WHERE id=$p0", created.Id)[0][0];
            peer.Send(33, Build(w => { w.Write((ushort)33); w.Write(quest); w.Write((byte)0x10); w.Write((ushort)0); }));
            peer.Expect(1, 33, Build(w => { w.Write((byte)1); w.Write(quest); w.Write(scalar); }));
            QuestLifecycleTcpTests.DrainNotifications(peer);
            That(Equals(f.Query("SELECT revision FROM characters WHERE id=$p0", created.Id)[0][0], revision), "希望原生33回读不会写进度或revision");
            foreach(int at in Enumerable.Range(0,map[18]).Select(i=>19+22*i).Where(at=>map[at+13]!=9 && map[at+21]==0).OrderBy(at=>map[at+13]==3))
            {
                peer.Send(39,Death(U16(map,at+6),peer.Id,map[at+12]));
                peer.Expect(0,38);
                var notices = new List<WirePacket>();
                DrainDeath(notices);
                foreach (var notice in notices.Where(row => row.Kind == 1 && row.Opcode == 33))
                {
                    Bytes(notice.Body, Build(w => { w.Write((byte)1); w.Write(quest); w.Write(0u); }), "希望目标死亡按单任务33投影完成，不重建任务槽");
                    progressTransitions++;
                    That(!notices.Any(row => row.Kind == 0 && row.Opcode == 574), "只有希望进度改变时不发整表574");
                }
            }
            if(index+1==path.Count)break;
            var next=path[index+1];var move=new byte[100];move[0]=checked((byte)next.X);move[1]=checked((byte)next.Y);
            peer.Send(45,move);peer.Expect(1,45,[1]);map=peer.Expect(0,29).Body;
        }
        That((long)f.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0",created.Id)[0][0]==fatigueBefore-16,
            "真实网络完整四区域仅扣16疲劳");
        var energy = JsonSerializer.Deserialize<ItemCore>((string)f.Query("SELECT item_core FROM character_inventory_items i " +
            "JOIN character_inventory_slots s USING(item_uid) WHERE s.character_id=$p0 AND list_type=0 AND slot_index=3", created.Id)[0][0]);
        That(energy == created.Inventory.Find(new(InventoryListType.Main, 3))!.Core with { Durability = 9 },
            "希望完整路线按四区域疲劳16同扣能量16，同区域换房不重复扣且不改品级");
        That(progressTransitions == 1, "希望完整迷宫恰有一次权威目标完成UI转换");
        peer.Send(46,DungeonSettlementProtocolTests.Report(peer.Id,99));
        QuestLifecycleTcpTests.DrainNotifications(peer);
        That(f.Query("SELECT count(*) FROM character_operation_hope_reward_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
            f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,quest) is [[1L]],
            "翻牌完成前不自动提交已达成任务，任务仍保留到牌面提交");
        peer.Send(69);
        PartyDungeonTcpTests.Drain(peer);
        peer.Send(70);
        PartyDungeonTcpTests.Drain(peer);
        peer.Send(71,[0,0]);
        var rewardNotices = PartyDungeonTcpTests.Drain(peer).ToList();
        int cardAckIndex = rewardNotices.FindIndex(packet => packet.Kind == 1 && packet.Opcode == 71);
        int finishIndex = rewardNotices.FindIndex(packet => packet.Kind == 1 && packet.Opcode == 34);
        int activeIndex = rewardNotices.FindIndex(packet => packet.Opcode == 574);
        int historyIndex = rewardNotices.FindIndex(packet => packet.Opcode == 356);
        That(cardAckIndex >= 0 && finishIndex > cardAckIndex && activeIndex > finishIndex &&
            historyIndex > activeIndex && rewardNotices[activeIndex].Body.AsSpan().SequenceEqual(new byte[4]),
            "已达成希望任务在免费牌提交后自动ACK34、清活动槽并回放完成历史；实际通知="+
            string.Join(',',rewardNotices.Select(packet => packet.Kind+":"+packet.Opcode)));
        var history = rewardNotices[historyIndex].Body;
        That(U32(history) == 30000 && Enumerable.Range(1,29999).All(id => history[4+id] ==
                (done.Contains(id) || id == 13700 ? 1 : 0)),
            "自动交付完整回放原完成位和显示兼容位，不把希望任务写成普通完成或伪造其它通路");
        That(f.Query("SELECT count(*) FROM character_operation_hope_reward_receipts WHERE character_id=$p0",created.Id) is [[1L]] &&
            f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,quest) is [[0L]] &&
            f.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,quest) is [[0L]],
            "自动交付只写实例收据、清本次接取且不污染普通完成历史");
        // 独立测试库模拟最后一次已耗尽；不改变在线角色快照或通关回执。
        f.Sql("UPDATE character_dungeon_incount SET daily_used=$p0,weekly_used=$p1 WHERE character_id=$p2 AND dungeon=$p3",
            incountRow.Limit,Math.Max(incountRow.WeeklyLimit,1),created.Id,incountRow.CountKey);
        string countsBeforeRetry = CountState();
        string attemptBeforeRetry = f.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0",created.Id);
        string rewardsBeforeRetry = f.Snapshot("SELECT * FROM character_operation_hope_reward_receipts WHERE character_id=$p0",created.Id);
        peer.Send(72,[2,0]);peer.Expect(1,72,[1,2,0]);
        peer.Send(72,[1,0]);
        var returned = PartyDungeonTcpTests.Drain(peer);
        int ack = Array.FindIndex(returned,p => p is {Kind:1,Opcode:72,Body:[1,1,2]});
        int state = Array.FindIndex(returned,p => p is {Kind:0,Opcode:3,Body:[1,_,_,0]});
        int townNotice = Array.FindIndex(returned,p => p is {Kind:0,Opcode:24});
        That(ack >= 0 && state > ack && townNotice > state && returned.All(p => p.Opcode is not (28 or 29)),
            "希望次数耗尽再次挑战必须确认回城并结束旧场景，不能静默丢弃72");
        That(CountState() == countsBeforeRetry &&
            f.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0",created.Id) == attemptBeforeRetry &&
            f.Snapshot("SELECT * FROM character_operation_hope_reward_receipts WHERE character_id=$p0",created.Id) == rewardsBeforeRetry,
            "希望拒绝重试不重扣次数、不创建尝试、不重复专属奖励");
        peer.Send(72,[1,0]);peer.Barrier();
        peer.Send(1598,[]);peer.Expect(1,1598,[1,0,0,0,0]);
        peer.Send(15,Build(w=>w.Write((uint)8523)));peer.Expect(1,15,[1]);
        QuestLifecycleTcpTests.DrainNotifications(peer);
        peer.Send(16,entry);peer.Expect(1,16,[0,9,0,0]);peer.Barrier();
        That(CountState() == countsBeforeRetry,"返回选图仍拒绝已耗尽次数，不绕过准入");
        peer.Send(132);peer.Expect(1,132,[1]);QuestLifecycleTcpTests.DrainNotifications(peer);
        f.Sql("UPDATE character_dungeon_incount SET daily_used=0,weekly_used=0 WHERE character_id=$p0 AND dungeon=$p1",
            created.Id,incountRow.CountKey);
        peer.Send(1598,[]);peer.Expect(1,1598,[1,0,0,0,0]);
        peer.Send(15,Build(w=>w.Write((uint)8523)));peer.Expect(1,15,[1]);QuestLifecycleTcpTests.DrainNotifications(peer);
        peer.Send(16,entry);That(peer.Expect(1,31).Body[0] == 1,"次数恢复后可重新选择希望并分配新任务");
        peer.Expect(0,28);peer.Expect(0,29);QuestLifecycleTcpTests.DrainNotifications(peer);
        peer.Send(42);peer.Expect(1,42,[1]);QuestLifecycleTcpTests.DrainNotifications(peer);
        var current = position;
        foreach (var (town,region) in new (byte,byte)[] { (54,1),(60,3),(60,1),(60,3),(54,1),(54,3),(63,0),(54,3),(54,1) })
        {
            var next = resources.Towns.Entrance(current,new(town,region,0,0),done) ??
                throw new InvalidDataException("主线往返缺少实际入口："+town+"/"+region);
            peer.Travel(town,region,next.X,next.Y,current.Town,current.Area);
            current = next;
        }
        That(peer.CompletedQuestHistory[4+6045] == 1 && peer.CompletedQuestHistory[4+6201] == 1 &&
            peer.CompletedQuestHistory[4+6301] == 1 &&
            f.Query("SELECT town,area FROM characters WHERE id=$p0",created.Id) is [[54L,1L]],
            "希望领奖返城后黑市→灰色墓地及黑市→中央竞技场主线通路均可往返，完成位保持");

        void DrainDeath(List<WirePacket> notices)
        {
            peer.SendBarrier();
            for (int read = 0; read < 64; read++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "希望死亡投影完成屏障"); return; }
                That(packet.Kind == 0 || packet is { Kind: 1, Opcode: 33 }, "希望死亡只允许通知及已确认的单任务进度ACK");
                notices.Add(packet);
            }
            throw new InvalidDataException("希望死亡通知超过有界数量");
        }

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
