using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    sealed class GriefClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // 使用正式Handler和原生格式的合成网络报告；不替代Windows客户端的自然战斗验收。
    public static async Task RunGriefAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        int checks = Count;
        var clock = new GriefClock { Now = new(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(8)) };
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"grief-tower"),resources:resources,clock:clock);
        await fixture.StartAsync();
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        var quest = resources.Quests.Get(2929);
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 5115));
        var done = quest.Definition!.PrerequisiteGroups.Take(1).SelectMany(group => group)
            .Concat(area.Quests).Concat(area.AnyQuests.Take(1)).Append(3145).ToHashSet();
        var option = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(row => row.DungeonId == 5115);
        if (option.QuestId > 0 && !option.InProgress) done.Add(option.QuestId);
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        That(!done.Contains(2929),"塔准入准备不预填目标任务完成位");
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        var repository = new CharacterRepository(fixture.Database,resources.Quests);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var candidate = CharacterCreation.Prepare("GriefFormal",resources.Characters.Get(0),position,position,resources.Learning.Get);
            created = repository.Create(owner,candidate with
            {
                Level=90,AcceptedStoryLevel=90,CompletedQuests=done.ToArray(),
                InitialLearning=SkillPointLedger.Synchronize(0,0,90,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(90) },resources.Learning)
            });
            var items = resources.ItemResources;
            var grants = new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
            created = InventoryService.Commit(repository,owner,created,grants.Grant(created,[new(10161065,2)]));
        }
        long tickets = Tickets();
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:resources.Towns.IsRoom(position,done));
            That(peer.DungeonEnterCounts[5115] == (1, 0), "首次选角初始化悲叹基础日剩余1次");
            peer.Send(31,[31,0,0x71,0x0B]);
            That(peer.Expect(1,31).Body is [1,0x71,0x0B,1,0,0,0,..],"2929经真实31接取pending1");
            QuestLifecycleTcpTests.DrainNotifications(peer);
            var first = EnterGrief(peer,1,questId:2929);
            That(Tickets()==tickets && fixture.Query("SELECT used FROM account_grief_tower") is [[1L]],
                "正式基础入场与尝试号提交同一事务，不消耗重试票或疲劳");
            ReturnGrief(peer);
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2929",created.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM character_grief_tower_clears") is [[0L]],"未死亡清图的退出不生成任务或塔通关回执");
            OpenGriefSelection(peer,1);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:resources.Towns.IsRoom(position,done));
            That(peer.DungeonEnterCounts[5115] == (0, 0), "未通关即退本后重登仍恢复已用状态");
            var retry = EnterGrief(peer,1,invitation:true);
            That(Tickets()==tickets-1 && fixture.Query("SELECT used FROM account_grief_tower") is [[1L]] &&
                fixture.Query("SELECT invitation FROM character_grief_tower_entries ORDER BY task_attempt") is [[0L],[1L]],
                "同角色失败重试只扣一张悲叹票，不再次扣账号额度");
            ClearGrief(peer,retry,1);
            That(fixture.Query("SELECT cleared,layer FROM character_grief_tower WHERE character_id=$p0",created.Id) is [[1L,2L]] &&
                fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2929",created.Id) is [[0L]] &&
                fixture.Query("SELECT quest_id FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id) is [[2929L]],
                "APC死亡报告和原实例通关驱动2929到0，并持久下一层及当天已通关");
            ReturnGrief(peer);
            peer.Send(33,[33,0,0x71,0x0B,0,0]); peer.Expect(1,33,[1,0x71,0x0B,0,0,0,0]); peer.Barrier();
            peer.Send(34,[34,0,0x71,0x0B,255,255,1,0,255,255]);
            That(peer.Expect(1,34).Body is [1,0x71,0x0B,..],"真实34完成2929交付");
            QuestLifecycleTcpTests.DrainNotifications(peer);
            That(fixture.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id) is [[2929L]] &&
                fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2929",created.Id) is [[0L]],
                "任务交付奖励、清槽与完成回执真实保存");
            OpenGriefSelection(peer,2,true);
            peer.Send(16,Request()); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            That(Tickets()==tickets-1 && fixture.Query("SELECT count(*) FROM character_grief_tower_entries") is [[2L]],
                "当天已通关再入拒绝，不扣票或创建尝试");
        }
        clock.Now = new(2026, 10, 27, 5, 59, 59, TimeSpan.FromHours(8));
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:resources.Towns.IsRoom(position,done));
            That(peer.DungeonEnterCounts[5115] == (0, 0) && peer.DungeonEnterCounts[5116] == (0, 0),
                "次日05:59:59仍使用前一游戏日，首层入口和下一层详情均显示已用");
            OpenGriefSelection(peer,2,true);
        }
        clock.Now = clock.Now.AddSeconds(1);
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:resources.Towns.IsRoom(position,done));
            That(peer.DungeonEnterCounts[5115] == (1, 0) && peer.DungeonEnterCounts[5116] == (1, 0),
                "06:00精确重置首层入口与当前第二层的基础剩余，未把楼层当作独立日额度");
            var second = EnterGrief(peer,2);
            That(U32(second,14)==dungeons.ResolveOrdinaryMaze(dungeons.Get(5116),dungeons.Get(5116).Mazes.Single()).GriefRoom!.Id &&
                fixture.Query("SELECT used FROM account_grief_tower") is [[1L]],"跨游戏日重置账号额度，首层入口映射持久第二层及真实MAP");
            ClearGrief(peer,second,2);
            That(fixture.Query("SELECT cleared,layer FROM character_grief_tower") is [[1L,3L]],"重连及跨日后继续实际层，不恢复首层");
        }
        Console.WriteLine($"PASS {Count-checks} 悲叹正式TCP入场、失败扣票重试、APC报告清图、1267、2929交付及跨日续层（合成报告，非实机验收）");

        long Tickets()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{});
            return repository.LoadCharacter(owner,created.Slot).Inventory.Items.Where(row=>row.Core.ItemId==10161065).Sum(row=>(long)row.Core.Count);
        }
        byte[] Request(ushort questId=0)
        {
            var request=Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(request,5115);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),questId);
            return request;
        }
        void ReturnGrief(TcpPeer peer)
        {
            peer.Send(42,[]); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
        }
        void OpenGriefSelection(TcpPeer peer,int layer,bool cleared=false,byte remaining=0)
        {
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]); peer.Expect(0,3);
            peer.Expect(0,1536);
            peer.Expect(0,1303,[0x34,cleared ? (byte)1 : (byte)0]);
            peer.ExpectAltarEnterCounts();
            That(peer.DungeonEnterCounts[5115] == (remaining, 0) && peer.DungeonEnterCounts[5114 + layer] == (remaining, 0),
                "选图前悲叹入口和实际楼层的550剩余与挑战日一致，两祭坛仍各3次");
            byte[] notice=peer.Expect(0,27).Body;
            That(notice.Length==42 && U16(notice,39)==layer,"真实27下发持久悲叹楼层");
        }
        byte[] EnterGrief(TcpPeer peer,int layer,bool invitation=false,ushort questId=0)
        {
            OpenGriefSelection(peer,layer,remaining:invitation ? (byte)0 : (byte)1);
            var wrong=Request(questId);
            BinaryPrimitives.WriteUInt32LittleEndian(wrong,checked((uint)dungeons.GriefTower.FloorsByDungeon.Values.Single(row=>row.Layer==layer+1).DungeonId));
            peer.Send(16,wrong); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            peer.Send(16,Request(questId));
            var info=peer.Expect(0,28).Body;
            var map=peer.Expect(0,29).Body;
            That(U32(info,0)==dungeons.GriefTower.FloorsByDungeon.Values.Single(row=>row.Layer==layer).DungeonId && map[18]>0,
                "正式28/29携带专表对应副本与原生演员，不走诊断实例");
            int itemNotices=0, countNotices=0;
            QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
            {
                if (packet.Opcode == 550)
                {
                    That(packet.Body.Length == 6 && packet.Body[4] == 0 && packet.Body[5] == 0 &&
                        U32(packet.Body,0) == (countNotices == 0 ? 5115 : 5114 + layer),
                        "成功入场事务后立即同步入口及当前层零剩余，不冒充通关或扣减其它副本");
                    countNotices++;
                }
                else { That(packet.Opcode==14,"入场提交后仅补投实际扣票库存差量"); itemNotices++; }
            });
            That(itemNotices==(invitation ? 1 : 0) && countNotices==(layer == 1 ? 1 : 2),
                "基础与邀请函重试均同步已用状态，重试按提交结果发一次扣票差量");
            Load(peer); peer.Barrier();
            string before=fixture.Snapshot("SELECT * FROM character_grief_tower_entries ORDER BY task_attempt");
            peer.Send(16,Request(questId)); peer.Barrier();
            peer.Send(46,global::DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,90)); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_grief_tower_entries ORDER BY task_attempt")==before &&
                fixture.Query("SELECT count(*) FROM character_grief_tower_clears WHERE layer=$p0",layer) is [[0L]],
                "进图重复请求不再扣费；无死亡清图事实的46不发奖");
            return map;
        }
        void ClearGrief(TcpPeer peer,byte[] map,int layer)
        {
            var rows=Enumerable.Range(0,map[18]).Select(index=>19+index*22).ToArray();
            That(rows.Any(at=>map[at+13] is >=5 and <=8),"塔房间包含资源声明的APC演员");
            int enabled=0, deaths=0;
            byte[] invalid=Death(U16(map,rows[0]+6),peer.Id,map[rows[0]+12]); invalid[^1]^=1;
            peer.Send(39,invalid); peer.Barrier();
            foreach (int at in rows)
            {
                peer.Send(39,Death(U16(map,at+6),peer.Id,map[at+12]));
                QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
                {
                    if (packet.Opcode==38) deaths++;
                    if (packet.Opcode==31) { Bytes(packet.Body,[],"自然死亡后发布空31许可客户端上报结算"); enabled++; }
                    That(packet.Opcode!=1267,"死亡通知不提前绕过46结算事务发奖励");
                });
            }
            That(deaths==rows.Length && enabled==1,"当前实例演员死亡后只许可一次结算");
            var report=global::DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,90);
            peer.Send(46,report);
            byte[]? reward=null;
            QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
            {
                That(packet.Opcode is not (34 or 35),"悲叹不发布普通结果页或翻牌");
                if (packet.Opcode==1267) { That(reward is null,"一次结算只发布一份悲叹奖励"); reward=packet.Body; }
            });
            That(reward is not null,"真实46成功发布1267");
            That(U16(reward!,4)==layer && reward!.Length==7+8*reward[6],"真实通关事务后发布1267楼层与冻结奖励");
            string committed=fixture.Snapshot("SELECT * FROM character_grief_tower_clears ORDER BY task_attempt");
            string inventory=fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
            peer.Send(46,report);
            int repeated=0;
            QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
            {
                if (packet.Opcode==1267) { Bytes(packet.Body,reward!,"46重放奖励逐字节一致"); repeated++; }
            });
            That(repeated==1,"46重放投影只含一份原奖励");
            That(fixture.Snapshot("SELECT * FROM character_grief_tower_clears ORDER BY task_attempt")==committed &&
                fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")==inventory,
                "精确46重放复用奖励回执，不重复抽奖或推进楼层");
        }
    }
}
