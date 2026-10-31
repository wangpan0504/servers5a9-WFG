using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    internal static async Task RunQuestAndHellAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        HellApcFactionTests.RunResources(resources);
        LegacyHellDropTests.RunResources(resources.Archive, resources.Towns, resources.ItemResources);
        RiftEpicDropTests.RunResources(resources.Archive, resources.ItemResources);
        RiftMaterialDropTests.RunResources(resources);
        HarlemMaterialDropTests.RunResources(resources.Towns, resources.Archive, resources.Characters,
            resources.ItemResources, resources.Quests, resources.Learning);
        DungeonSpecificCardRewardsTests.RunResources(resources.Towns, resources.Archive, resources.Characters,
            resources.ItemResources, resources.Quests, resources.Learning);
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"party-modes"),resources:resources);
        await fixture.StartAsync();
        VerifyHellRoomMoveRecovery(fixture, resources);
        foreach (var (questId,dungeonId) in new (ushort,uint)[] { (5934,291100318),(6032,291100319),(3842,191) })
        {
            var quest = resources.Quests.Get(questId);
            var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,dungeonId);
            using var group = new Coop(fixture,resources,"q"+questId,entrance.Position,entrance.Completed,95,4);
            // 95级选角会真实分配6115；独立核对初值、原槽与接取身份，不把每日视作队长任务。
            var dailies = group.Peers.Select(peer => new QuestDailyCoexistence(fixture,peer.Id,6115,1686,progress:true)).ToArray();
            foreach (var peer in group.Peers.Take(3))
            {
                peer.Send(31,[31,0,(byte)questId,(byte)(questId>>8)]);
                That(Drain(peer).Any(p => p is { Kind:1,Opcode:31 } && p.Body[0]==1),"三名成员分别接取真实任务");
            }
            group.Join();
            group.Enter(dungeonId,questId);
            That(group.Information[7] == SelfTestCatalogs.Dungeons(resources.Archive).Get((int)dungeonId).Mazes
                .Single(m => m.ConnectsQuest(questId)).SourceOrdinal,"全员使用队长任务迷宫，助战者不随机选择另一条路线");
            group.Load();
            if (questId == 5934)
            {
                var invalid = group.Move(2,0,expect:false);
                That(invalid.All(p => !p.Any(row => row.Opcode==29)),"无骑乘许可时全队不能跳跃warp");
                var riding = new byte[7]; riding[0]=2;
                BinaryPrimitives.WriteUInt32LittleEndian(riding.AsSpan(1),107010392);
                group.Peers[0].Send(316,riding); group.All();
                group.Move(2,0); group.Load();
                That(U32(group.Map,14)==292104869,"骑乘warp把任务持有者和无任务助战者同步传至终末");
                group.Clear();
            }
            else if (questId == 6032)
            {
                group.Clear(scripted:true); group.Move(1,0); group.Load();
                group.Clear(scripted:true); group.Move(2,0); group.Load();
                group.Move(2,0,kind:1); group.Load();
                That(U32(group.Map,14)==59914 && group.Map[0]==2,"全员推进真实异坐标叠层，助战者保持相同物理终点");
                ushort layerKey = U16(group.Map,25);
                string beforeReturn = fixture.Snapshot();
                group.Move(2,0);
                That(group.Map.Length==16 && group.Map[2]==2 && fixture.Snapshot()==beforeReturn,
                    "全员按ACT真实返回基础房，不提交演出层死亡或提前清图");
                group.Load();
                var baseAck = new byte[100]; baseAck[0]=2; baseAck[10]=1;
                foreach (var peer in group.Peers) { peer.Send(45,baseAck); group.All(peer); }
                group.Move(3,0); group.Load();
                That(U32(group.Map,14)==59914 && U16(group.Map,25)!=layerKey,
                    "任务持有者和助战者均可继续基础Boss房，演员与演出层独立");
                var stale = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(stale,layerKey);
                BinaryPrimitives.WriteUInt16LittleEndian(stale.AsSpan(4),group.Peers[0].Id);
                stale[31]=0x22; FormalDungeonTcpTests.SealDeath(stale);
                string beforeStale = fixture.Snapshot();
                group.Peers[0].Send(39,stale); group.All();
                That(fixture.Snapshot()==beforeStale,"旧演出层演员不能代替任一成员的真实Boss死亡");
                group.Clear(scripted:true);
            }
            else
            {
                var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(191).Mazes.Single(m => m.SourceOrdinal==group.Information[7]);
                var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(group.Map[0],group.Map[1]),new(group.Information[8],group.Information[9]));
                foreach (var next in path.Skip(1)) { group.Clear(); group.Move((byte)next.X,(byte)next.Y); group.Load(); }
                That(U32(group.Map,14)==70407,"四人到达真实物件终点，不杀友方APC替代清图");
                string before = fixture.Snapshot();
                var bad = QuestPassiveClearTcpTests.PassiveDeath(69291); bad[^1]^=1;
                group.Peers[0].Send(39,bad); group.All();
                That(fixture.Snapshot()==before,"错误物件校验不推进任一队员");
                group.Peers[0].Send(39,QuestPassiveClearTcpTests.PassiveDeath(69291));
                That(group.All().All(rows => rows.Any(p => p is { Kind:0,Opcode:31 })),"物件死亡为含无任务助战者的全队开启结算");
                That(fixture.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE dungeon_id=191") is [[4L]] &&
                    fixture.Query("SELECT count(*) FROM character_quest_lifecycle_receipts WHERE character_id=$p0 AND quest_id=0",group.Peers[3].Id) is [[1L]],
                    "助战者保存真实无任务场景回执，不借用队长接取身份");
                before=fixture.Snapshot();
                group.Peers[1].Send(39,QuestPassiveClearTcpTests.PassiveDeath(69291)); group.All(group.Peers[1]);
                That(fixture.Snapshot()==before,"另一队员重复物件报告不重复提交");
            }
            That((questId==3842 || fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE quest_id=$p0",questId) is [[3L]]) &&
                fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE quest_id=$p0 AND condition_state=0",questId) is [[3L]] &&
                dailies.Take(3).All(daily => daily.OtherRows("quest_id,condition_state") is [[long id,0L]] && id == questId) &&
                dailies[3].OtherRows("quest_id") is [],
                "只有各自原接取的三人提交清图；助战者不新增任务或完成位");
            group.Settle();
            group.Peers[0].Send(72,[1,0]);
            That(group.All().All(rows => rows.Any(p => p is { Kind:0,Opcode:29 })),"任务组队可从结算再挑战，不沿用旧地面或骑乘许可");
        }

        VerifyOrdinaryMaterialRejection();
        VerifyOrdinaryMaterialRejection(continueToNext: true);
        VerifyCinematic();
        VerifyPassiveSequence();

        var area = resources.Towns.Areas.Values.First(row => row.Level<=95 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId==291100308));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).ToHashSet(); done.Add(6080);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,(ushort)(bounds.X+bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2));
        foreach (var scenario in new[] { "quest","short","rollback","success" })
        {
            using var group = new Coop(fixture,resources,"h"+scenario,position,done.ToArray(),95,2,
                tickets:scenario=="short" ? 29 : 90,withoutHellQuest:scenario=="quest");
            group.Join(); group.Open(scenario == "quest" ? [1] : []);
            string before=fixture.Snapshot();
            if (scenario=="rollback") fixture.Sql("CREATE TRIGGER reject_coop_hell BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id="+
                group.Peers[1].Id+" BEGIN SELECT RAISE(ABORT,'hell party rollback'); END");
            group.Peers[0].Send(16,Convert.FromHexString("94D659110400000100FFFF0000000000E21700000000000000"));
            if (scenario=="rollback")
            {
                group.Peers[0].Expect(0,9); group.Peers[0].Closed();
                That(fixture.Snapshot()==before,"深渊第二名队员SQL失败回滚全员门票、尝试号与revision");
                fixture.Sql("DROP TRIGGER reject_coop_hell"); continue;
            }
            var rows=group.All();
            if (scenario!="success")
            {
                That(rows[0].Any(p => p is {Kind:1,Opcode:16} && p.Body.SequenceEqual(new byte[] {0,(byte)(scenario=="quest" ? 9 : 0x11),0,0})) &&
                    rows.All(r => !r.Any(p => p.Opcode is 28 or 29)) && fixture.Snapshot()==before,
                    "队员未开启深渊或少一张票时全队拒绝，队长也不扣材料");
                continue;
            }
            group.ReadEntry(rows);
            That(rows.All(r => r.Any(p => p is { Kind:0,Opcode:14 })) && group.Information[14]==1 && group.Map[7]==1,
                "深渊全员扣票、安装相同模式和路线并各自同步背包");
            That(group.Peers.All(p => fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=3330",p.Id) is [[60L]]),
                "每人各扣30张邀请函");
            group.Load();
            var definition=SelfTestCatalogs.Dungeons(resources.Archive).Get(291100308);
            var maze=definition.Mazes.Single(m => m.SourceOrdinal==group.Information[7]);
            var path=QuestStoryRouteTests.Path(maze.StaticTopology,new(group.Map[0],group.Map[1]),maze.HellParty!.Room);
            WirePacket[][] latest=[];
            foreach (var next in path.Skip(1)) { group.Clear(); latest=group.Move((byte)next.X,(byte)next.Y); group.Load(); }
            That(latest.All(r => r.Any(p => p is {Kind:0,Opcode:679})),"双方到达深渊房均收到完整波次模板");
            group.Clear();
            That(group.LastPackets.All(rows => rows.Any(p => p is { Kind:0,Opcode:38 })) &&
                fixture.Query("SELECT count(DISTINCT character_id) FROM character_dungeon_drop_batches WHERE freeze_format=1 AND character_id IN ($p0,$p1)",
                group.Peers[0].Id,group.Peers[1].Id) is [[2L]],"深渊波次为双方发布独立掉落，并按各自真实死亡事务持久完整冻结");
            group.Peers[1].Send(15,BitConverter.GetBytes(291100308u));
            That(group.All(group.Peers[1]).All(r => !r.Any(p => p.Opcode==27)),"队员不能单独消费共享深渊出口");
            group.Peers[0].Send(15,BitConverter.GetBytes(291100308u));
            That(group.All().All(r => r.Any(p => p is {Kind:0,Opcode:27})),"深渊房打完后队长祭坛操作带全队返回选图");
        }

        foreach (bool cosmo in new[] { false,true })
        foreach (bool shortfall in new[] { true,false })
        {
            var completed=done.Concat(cosmo ? [10112] : new[] {6081}).ToArray();
            using var group=new Coop(fixture,resources,(cosmo ? "cosmo" : "legend")+(shortfall ? "short" : "ok"),
                position,completed,95,2,tickets:90,stones:shortfall ? 1 : 8);
            group.Join(); group.Open();
            uint dungeon=cosmo ? 7191u : group.LegendTarget;
            That(cosmo || dungeon>0,"组队选图保留队长已批准的传奇深渊目标");
            string before=fixture.Snapshot();
            group.Peers[0].Send(16,Coop.Selection(dungeon,0,4,true,legend:!cosmo));
            var rows=group.All();
            if (shortfall)
            {
                That(rows[0].Any(p => p is {Kind:1,Opcode:16} && p.Body.SequenceEqual(new byte[] {0,0x11,0,0})) &&
                    rows.All(r => !r.Any(p => p.Opcode is 28 or 29)) && fixture.Snapshot()==before,
                    "传奇或超星空队员少灵石时全队不扣任何票据");
                continue;
            }
            group.ReadEntry(rows); group.Load();
            int invitation=cosmo ? 40 : 30, stone=cosmo ? 4 : 2;
            That(group.Peers.All(p => fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=3330",p.Id) is [[long count]] && count==90-invitation &&
                fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10305821",p.Id) is [[long stones]] && stones==8-stone),
                "传奇和超星空分别向每人原子扣除真实邀请函及灵石数量");
            var maze=SelfTestCatalogs.Dungeons(resources.Archive).Get((int)dungeon).Mazes.Single(m => m.SourceOrdinal==group.Information[7]);
            var target=cosmo ? new DfoServer.GameWorld.Dungeons.DungeonPoint(group.Information[8],group.Information[9]) : maze.HellParty!.Room;
            var path=QuestStoryRouteTests.Path(maze.StaticTopology,new(group.Map[0],group.Map[1]),target);
            foreach (var next in path.Skip(1)) { group.Clear(); group.Move((byte)next.X,(byte)next.Y); group.Load(); }
            group.Clear();
            if (cosmo)
            {
                That(group.LastPackets.All(r => r.Any(p => p is {Kind:0,Opcode:261} && p.Body.SequenceEqual(new byte[] {9})) &&
                    !r.Any(p => p.Opcode is 34 or 35)),"超星空终点不弹翻牌，全员直接开放再次挑战");
                That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id IN ($p0,$p1)",group.Peers[0].Id,group.Peers[1].Id) is [[0L]],"未发送46也可沿真实清图许可再次挑战");
                group.Peers[0].Send(72,[1,0]); group.ReadEntry(group.All()); group.Load();
                That(group.Peers.All(p => fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=3330",p.Id) is [[10L]]),"超星空再挑战全员重新扣除40张邀请函");
                maze=SelfTestCatalogs.Dungeons(resources.Archive).Get((int)dungeon).Mazes.Single(m => m.SourceOrdinal==group.Information[7]);
                path=QuestStoryRouteTests.Path(maze.StaticTopology,new(group.Map[0],group.Map[1]),new(group.Information[8],group.Information[9]));
                foreach (var next in path.Skip(1)) { group.Clear(); group.Move((byte)next.X,(byte)next.Y); group.Load(); }
                group.Clear();
                That(group.LastPackets.All(r => r.Any(p => p is {Kind:0,Opcode:261} && p.Body.SequenceEqual(new byte[] {0}))),"任何成员不足下次票据时全队再挑战按钮禁用");
                before=fixture.Snapshot(); group.Peers[0].Send(72,[1,0]);
                That(group.All().All(r => !r.Any(p => p.Opcode==29)) && fixture.Snapshot()==before,"强发禁用再挑战也不部分扣票或替换实例");
            }
            group.Peers[0].Send(15,BitConverter.GetBytes(dungeon));
            That(group.All().All(r => r.Any(p => p is {Kind:0,Opcode:27})),"传奇深渊和超星空终点保留全队祭坛选图出口");
        }

        void VerifyOrdinaryMaterialRejection(bool continueToNext = false)
        {
            const int dungeon = 9595;
            const ushort quest = 2683;
            var definition = SelfTestCatalogs.Dungeons(resources.Archive).Get(dungeon);
            var ticket = definition.OrdinaryEntryTicket ?? throw new InvalidDataException("普通副本缺少已验证入场票");
            var entrance = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
            var completed = entrance.Quests.Concat(entrance.AnyQuests.Take(1)).Concat([3778, 4872, 8682]).ToHashSet();
            var options = resources.Towns.DungeonMaps[entrance.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == dungeon).ToArray();
            if (!options.Any(option => option.QuestId == -1)) completed.Add(options.First(option => !option.InProgress).QuestId);
            completed.UnionWith(entrance.Map(completed).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var bounds = entrance.Map(completed).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
            using var group = new Coop(fixture, resources, continueToNext ? "matnext" : "material", new(entrance.Town, entrance.Area,
                (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2)), completed.ToArray(), 95, 2,
                rewards: continueToNext ? [new(ticket.ItemId, ticket.Count * 2)] : null,
                leaderRewards: continueToNext ? null : [new(ticket.ItemId, ticket.Count)]);
            foreach (var peer in group.Peers)
            {
                peer.Send(31, [31, 0, (byte)(quest & 0xff), (byte)(quest >> 8)]);
                That(Drain(peer).Any(packet => packet is { Kind: 1, Opcode: 31 } && packet.Body[0] == 1),
                    "组队材料测试分别接取真实任务");
            }
            group.Join(); group.Open();
            string before = fixture.Snapshot();
            group.Peers[0].Send(16, Coop.Selection(dungeon, quest, 2));
            var rejected = group.All();
            if (continueToNext)
            {
                group.ReadEntry(rejected); group.Load();
                var maze = definition.Mazes.Single();
                var route = QuestStoryRouteTests.Path(maze.StaticTopology,new(group.Map[0],group.Map[1]),
                    new(group.Information[8],group.Information[9]));
                foreach (var point in route.Skip(1))
                { group.Clear(); group.Move((byte)point.X,(byte)point.Y); group.Load(); }
                group.Clear(); group.Settle();
                foreach (var peer in group.Peers)
                { peer.Send(69); group.All(peer); peer.Send(70); group.All(peer); peer.Send(71,[0,0]); group.All(peer); }
                group.Peers[0].Send(72,[1,0]);
                That(group.All().All(rows => rows.Any(packet => packet is {Kind:1,Opcode:72,Body:[1,1,0]}) &&
                    U32(rows.Single(packet => packet is {Kind:0,Opcode:28}).Body) == 9596),
                    "普通安徒恩组队从黑雾之源续到震颤的大地，不继承上一图显式任务号");
                return;
            }
            That(rejected[0].Any(packet => packet is { Kind: 1, Opcode: 16 } && packet.Body.SequenceEqual(new byte[] { 0, 27, 0, 1 })) &&
                rejected.All(rows => rows.All(packet => packet.Opcode is not (28 or 29))) && fixture.Snapshot() == before,
                "普通副本队员缺入场材料回复队伍提示，队长持有的材料和全队状态不变");
        }

        void VerifyCinematic()
        {
            var entrance=MiddleQuestEntryTests.Entrance(resources.Towns,resources.Quests.Get(3599),96);
            using var group=new Coop(fixture,resources,"movie",entrance.Position,entrance.Completed,95,4);
            foreach (var peer in group.Peers.Take(3)) { peer.Send(31,[31,0,15,14]); Drain(peer); }
            group.Join(); group.Enter(96,3599); group.Load();
            group.Move(1,0); group.Load(); group.Move(1,0,1); group.Load(); group.Clear(scripted:true);
            group.Move(1,0,1); group.Load();
            string before=fixture.Snapshot();
            group.Peers[3].Send(46,DungeonSettlementProtocolTests.Report(group.Peers.Select(p => (p.Id,(byte)99)).ToArray()));
            That(!group.All(group.Peers[3])[3].Any(p => p.Opcode==34) && fixture.Snapshot()==before,"无任务助战者也必须等待真实演出许可");
            foreach (var peer in group.Peers.Take(3))
            {
                peer.Send(33,[33,0,15,14,0,0]); var rows=group.All(peer);
                if (ReferenceEquals(peer,group.Peers[0])) That(rows[3].Any(p => p is {Kind:0,Opcode:31}),"持有者真实演出完成为助战者开启结算");
            }
            That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE quest_id=3599 AND condition_state=0") is [[3L]],"演出任务分别更新各自接取，不给助战者伪造任务");
            group.Settle();
        }

        void VerifyPassiveSequence()
        {
            var entrance=MiddleQuestEntryTests.Entrance(resources.Towns,resources.Quests.Get(3738),102);
            using var group=new Coop(fixture,resources,"passive",entrance.Position,entrance.Completed,95,2);
            foreach (var peer in group.Peers) { peer.Send(31,[31,0,154,14]); Drain(peer); }
            group.Join(); group.Enter(102,3738); group.Load();
            var maze=SelfTestCatalogs.Dungeons(resources.Archive).Get(102).Mazes.Single(m => m.SourceOrdinal==group.Information[7]);
            var target=maze.MapSpecification!.Single(m => m.Id==76533);
            var path=QuestStoryRouteTests.Path(maze.StaticTopology,new(group.Map[0],group.Map[1]),target.Position);
            foreach (var next in path.Skip(1)) { group.Clear(); group.Move((byte)next.X,(byte)next.Y); group.Load(); }
            foreach (int _ in Enumerable.Range(0,3)) { group.Peers[0].Send(33,[33,0,154,14,16,0,0]); group.All(); }
            That(group.Peers.All(p => fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3738",p.Id) is [[12L]]),"同模板的三份真实放置资源可连续33，各自任务同步推进三次");
            string before=fixture.Snapshot();
            foreach (int _ in Enumerable.Range(0,3)) { group.Peers[1].Send(33,[33,0,154,14,16,0,0]); group.All(group.Peers[1]); }
            That(fixture.Snapshot()==before,"另一成员重报相同三份资源不会重复推进任何人");
        }
    }

    sealed class Coop : IDisposable
    {
        readonly TcpTestFixture fixture;
        public TcpPeer[] Peers { get; }
        public byte[] Information { get; private set; }=[];
        public byte[] Map { get; private set; }=[];
        public uint LegendTarget { get; private set; }
        public WirePacket[][] LastPackets { get; private set; }=[];
        byte[] physicalMap=[];
        readonly ushort[] expectedIncompleteHellQuestSlots;
        public Coop(TcpTestFixture fixture,TcpTestResources resources,string prefix,Location position,int[] done,byte level,int count,
            int tickets=0,bool withoutHellQuest=false,int stones=0,
            IReadOnlyList<DfoServer.Game.Inventory.Services.InventoryReward>? rewards=null,
            IReadOnlyList<DfoServer.Game.Inventory.Services.InventoryReward>? leaderRewards=null, byte[]? memberJobs=null)
        {
            this.fixture=fixture;
            Peers=new TcpPeer[count];
            var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
            var blocked = new List<ushort>();
            for (int i=0;i<count;i++)
            {
                string account=prefix+i;
                using var owner=new AccountOwners().Acquire(account,() => { });
                byte job = memberJobs?[i] ?? 0;
                var candidate=CharacterCreation.Prepare("C"+prefix[..Math.Min(prefix.Length,8)]+i,resources.Characters.Get(job),position,position,resources.Learning.Get);
                var learning=SkillPointLedger.Synchronize(job, memberJobs is null ? (byte)0 : (byte)1,level,candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(level) },resources.Learning);
                var repo=new CharacterRepository(fixture.Database);
                var role=repo.Create(owner,candidate with { GrowType = memberJobs is null ? (byte)0 : (byte)1, Level=level,AcceptedStoryLevel=level,InitialLearning=learning,
                    CompletedQuests=withoutHellQuest && i==1 ? done.Except([6080]).ToArray() : done });
                if (FormalDungeonTcpTests.HasIncompleteHellQuest(resources, role)) blocked.Add((ushort)i);
                if (tickets>0) role=repo.CommitInventory(owner,role,new(role.Inventory,role.Inventory with { Items=role.Inventory.Items.Add(
                    new(0,new(InventoryListType.Main,121),new(3330,ItemKind.Material,i==0 ? 90 : tickets))) },"party_hell_ticket"));
                if (stones>0)
                {
                    repo.CommitInventory(owner,role,new(role.Inventory,role.Inventory with { Items=role.Inventory.Items.Add(
                        new(0,new(InventoryListType.Main,122),new(10305821,ItemKind.Material,i==0 ? 8 : stones))) },"party_hell_stone"));
                }
                var memberRewards = i == 0 && leaderRewards is not null ? leaderRewards : rewards;
                if (memberRewards is not null)
                {
                    var items = resources.ItemResources;
                    var grants = new DfoServer.Game.Inventory.Services.InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                    role = DfoServer.Game.Inventory.Services.InventoryService.Commit(repo, owner, role, grants.Grant(role, memberRewards));
                }
                Peers[i]=TcpPeer.Open(fixture,account); Peers[i].Select(role.Slot,prepared:true,resumeTown:true,room:false);
            }
            expectedIncompleteHellQuestSlots = blocked.ToArray();
            All();
        }
        public WirePacket[][] All(TcpPeer? requester=null)
        {
            requester ??= Peers[0];
            var result=new WirePacket[Peers.Length][];
            result[Array.IndexOf(Peers,requester)]=Drain(requester);
            foreach (var peer in Peers.Where(p => !ReferenceEquals(p,requester))) result[Array.IndexOf(Peers,peer)]=Drain(peer);
            return LastPackets=result;
        }
        public void Join()
        {
            for (int i=0;i<Peers.Length;i++)
            {
                Peers[i].Send(2,Build(w => { w.Write((byte)5); w.Write(0x0100007fu); w.Write(0x0100007fu); w.Write((ushort)(31000+i)); w.Write(512u); }));
                Drain(Peers[i]);
            }
            foreach (var peer in Peers.Skip(1))
            {
                Peers[0].Send(10,Build(w => { w.Write(peer.Id); w.Write((byte)0); w.Write(0u); w.Write(new byte[6]); })); All();
                peer.Send(11,Build(w => { w.Write(Peers[0].Id); w.Write((byte)0); w.Write(0u); }));
                That(All(peer).Take(Array.IndexOf(Peers,peer)+1).All(r => r.Any(p => p is {Kind:0,Opcode:9})),"扩展玩法通过真实邀请加入队伍");
            }
        }
        public void Open(ushort[]? incompleteHellQuestSlots = null)
        {
            Peers[0].Send(15,new byte[4]); var rows=All();
            That(rows.All(r => r.Any(p => p is {Kind:0,Opcode:27})),"扩展玩法全员打开选图");
            incompleteHellQuestSlots ??= expectedIncompleteHellQuestSlots;
            int tailOffset = incompleteHellQuestSlots.Length * 2;
            byte[] notice = rows[0].Single(p => p is {Kind:0,Opcode:27}).Body;
            That(notice[8] == incompleteHellQuestSlots.Length &&
                incompleteHellQuestSlots.Select((slot, i) => U16(notice, 9 + i * 2) == slot).All(matches => matches) &&
                rows.All(r => r.Single(p => p is {Kind:0,Opcode:27}).Body.SequenceEqual(notice)),
                "全队收到相同未完成区域深渊任务的队伍槽位名单，不能写角色ID或任务号");
            LegendTarget=U32(notice,9 + tailOffset);
        }
        public void Enter(uint dungeon,uint quest)
        {
            Open(); Peers[0].Send(16,Selection(dungeon,quest)); ReadEntry(All());
        }
        public static byte[] Selection(uint dungeon,uint quest,byte difficulty=0,bool hell=false,bool legend=false)
        {
            var body=Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(body,dungeon); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16),quest);
            body[4]=difficulty; body[7]=hell ? (byte)1 : (byte)0; body[20]=legend ? (byte)1 : (byte)0; return body;
        }
        public void ReadEntry(WirePacket[][] rows)
        {
            Information=rows[0].Single(p => p is {Kind:0,Opcode:28}).Body;
            ReadMap(rows); That(rows.All(r => r.Single(p => p is {Kind:0,Opcode:28}).Body.SequenceEqual(Information)),"所有成员共享完整入场信息");
        }
        void ReadMap(WirePacket[][] rows)
        {
            Map=rows[0].Single(p => p is {Kind:0,Opcode:29}).Body;
            That(rows.All(r => r.Single(p => p is {Kind:0,Opcode:29}).Body.SequenceEqual(Map)),"全员收到相同物理地图及演员key");
            if (Map.Length>18) physicalMap=Map;
        }
        public void Load()
        {
            foreach (var peer in Peers.Take(Peers.Length-1)) { peer.Send(37,new byte[8]); All(peer); }
            Peers[^1].Send(37,new byte[8]);
            That(All(Peers[^1]).All(r => r.Any(p => p is {Kind:0,Opcode:30})),"任务或深渊仍须全员加载后释放屏障");
        }
        public WirePacket[][] Move(byte x,byte y,byte kind=0,bool expect=true)
        {
            var body=new byte[100]; body[0]=x; body[1]=y; body[10]=kind;
            Peers[0].Send(45,body); var rows=All(); if (expect) ReadMap(rows); return rows;
        }
        public void Clear(bool scripted=false,bool usePhysicalMap=false)
        {
            var map=usePhysicalMap ? physicalMap : Map;
            foreach (int at in Enumerable.Range(0,map[18]).Select(i => 19+22*i).Where(at => map[at+21]==0 && map[at+13]!=9))
            {
                ushort key=U16(map,at+6);
                byte[] report;
                if (scripted) { report=new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(report,key); BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4),Peers[0].Id); report[31]=0x22; FormalDungeonTcpTests.SealDeath(report); }
                else report=FormalDungeonTcpTests.Death(key,Peers[0].Id,map[at+12]);
                Peers[0].Send(39,report);
                That(All().All(r => r.Any(p => p is {Kind:0,Opcode:38} && U16(p.Body)==key)),"共享任务或深渊死亡同步到全部成员");
            }
        }
        public void Settle()
        {
            var report=DungeonSettlementProtocolTests.Report(Peers.Select(p => (p.Id,(byte)99)).ToArray());
            foreach (var peer in Peers)
            {
                peer.Send(46,report); var rows=Drain(peer);
                That(rows.Any(p => p is {Kind:0,Opcode:34}),"普通任务地图各人正常提交经验和结算");
            }
            That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id IN ("+
                string.Join(',',Peers.Select((_,i)=>"$p"+i))+")",Peers.Select(p => (object)p.Id).ToArray()) is [[long count]] && count==Peers.Length,"每人的真实结算分别落库");
        }
        public void Dispose() { foreach (var peer in Peers) peer?.Dispose(); }
    }
}
