using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestPassiveClearTcpTests
{
    // 借用quests组已启动的隔离服务和通知消费者；不另建资源、监听或冷启动。
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        foreach (bool ordinary in new[] { false, true }) VerifyCase(fixture,resources,growth,drain,dungeons,ordinary);
        VerifyZeroKill(fixture,resources,growth,drain);
    }

    static void VerifyCase(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain, DungeonCatalog dungeons, bool ordinary)
    {
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 85 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 191));
        var done = new HashSet<int>(area.Quests) { 3841 };
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            // 只准备入口等级和真实前置；接取、击杀、清图及交付均走TCP，不改条件或造怪。
            var candidate = CharacterCreation.Prepare(ordinary ? "PassiveZero" : "QuestPassive",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,85,candidate.InitialLearning with
                { Experience=growth.GetLevelStartExperience(85) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level=85,AcceptedStoryLevel=85,CompletedQuests=done.ToArray(),InitialLearning=learning });
        }
        byte[] passive = PassiveDeath(69291);
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3842,191,row => information=row,requestQuestId:ordinary ? (ushort)0 : (ushort)3842);
            var maze = dungeons.Get(191).Mazes.Single(row => row.SourceOrdinal == information[7]);
            That(maze.ClearConditions is [{ Type:"destroy object",TargetId:69291,Count:1 }],
                "3842 TCP按实际单物件清图条件选择D191路线");
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(information[8],information[9]));
            var visited = new List<uint>();
            var killed = new Dictionary<uint,int>();
            int enabled = 0, bossChecks = 0;
            for (int room = 0; room < path.Length; room++)
            {
                visited.Add(U32(map,14));
                FormalDungeonTcpTests.Load(peer);
                Drain();
                That(enabled == 0,"3842加载或前序死亡不提前发布物件终点31");
                bool terminal = U32(map,14) == 70407;
                if (terminal)
                {
                    That(map[18] == 1 && U32(map,27) == 808 && map[40] == 1,
                        "70407保留真实友方APC808和清房排除位，不伪造Boss或杀友方");
                    break;
                }
                if (room == 0) RejectPassive(passive,"前房合成52B物件报告不能提前清图");
                for (int at = 0; at < map[18]; at++)
                {
                    int offset = 19+22*at;
                    uint template = U32(map,offset+8);
                    if (map[offset+21] != 0) continue;
                    // 两个猎杀模板之外的真实敌人同样完整击杀，不以任务目标过滤房间演员。
                    peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12]));
                    Drain();
                    killed[template] = killed.GetValueOrDefault(template)+1;
                }
                That(enabled == 0,"完整前房击杀仍须等待69291物件来源提交");
                That(room+1 < path.Length,"3842物件终点必须位于实际路线末端");
                var next = path[room+1];
                var request = new byte[100];
                request[0] = checked((byte)next.X);
                request[1] = checked((byte)next.Y);
                peer.Send(45,request);
                peer.Expect(1,45,[1]);
                map = peer.Expect(0,29).Body;
            }
            That(visited.SequenceEqual(Enumerable.Range(70400,8).Select(id => (uint)id)) &&
                killed.GetValueOrDefault(69722u) == 22 && killed.GetValueOrDefault(69723u) == 21 &&
                fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3842",peer.Id) is [[0L]],
                "TCP完整走过70400..70407，完整击杀两路22/21及其他真实阻挡敌人而未提前清图");
            int deathCount = killed.Values.Sum();
            var wrongChecksum = passive.ToArray();
            wrongChecksum[^1] ^= 1;
            RejectPassive(wrongChecksum,"终点错误摘要不保存场景清图或许可结算");
            RejectPassive(PassiveDeath(69292),"终点错误模板不能借用本房69291资格");
            peer.Send(39,passive);
            var conditions = peer.Expect(0,574).Body;
            That(conditions.Length >= 4 && conditions.Length == 4+6*U32(conditions,0) &&
                Enumerable.Range(0,checked((int)U32(conditions,0))).Any(at => U16(conditions,4+6*at) == 3842 && U32(conditions,6+6*at) == 0),
                "场景事务提交后先发布完整574，保留猎杀任务实际归零状态");
            peer.Expect(0,31,[]);
            enabled++;
            Drain();
            That(enabled == 1 && bossChecks == 0 && fixture.Query(
                "SELECT source_type,dungeon_id,map_id,template_id,length(death_events)/16 FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                is [["PassiveObject",191L,70407L,69291L,long sceneDeaths]] && sceneDeaths == deathCount && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[long growthDeaths]] && growthDeaths == deathCount,
                "合成52B39沿独立物件来源提交完整实际死亡集合，并发送空31而无115");
            That(fixture.Query("SELECT quest_id FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                    is [[long instanceQuest]] && instanceQuest == (ordinary ? 0 : 3842) &&
                fixture.Query("SELECT quest_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0 AND scene_event IS NOT NULL",peer.Id)
                    is [[3842L]],
                "普通0保留实例请求号0，生命周期仍绑定真实3842，不伪造请求或接取身份");
            string committed = State();
            peer.Send(39,passive);
            Drain();
            That(State() == committed && enabled == 1,"重复物件39不重写凭据、资产或重复发送31");
            foreach (byte channel in new byte[] { 0x10,0x20 })
            {
                peer.Send(33,[33,0,2,15,channel,0,0]);
                peer.Expect(1,33,[1,2,15,0,0,0,0]);
                Drain();
                That(State() == committed,"猎杀33只读取已持久归零结果，不把普通怪物条件当物件事件");
            }
            var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
            peer.Send(46,report);
            Drain();
            That(fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                    is [[long settledQuest,70407L]] && settledQuest == (ordinary ? 0 : 3842),
                "真实46按物件终末70407提交正式通关奖励");
            committed = State();
            peer.Send(46,report);
            Drain();
            That(State() == committed,"重复46不重复发放物件终末通关奖励");
            peer.Send(34,[34,0,2,15,255,255,1,0,255,255]);
            var finished = peer.Expect(1,34).Body;
            That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == 3842 && finished[3] == 6,
                "3842沿真实34猎杀分类交付，不将物件来源伪造为怪物击杀");
            Drain();
            committed = State();
            peer.Send(34,[34,0,2,15,255,255,1,0,255,255]);
            peer.Expect(1,34,[0,19,0]);
            Drain();
            That(State() == committed,"重复34不增加完成回执或奖励");
            peer.Send(42);
            peer.Expect(1,42,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24);
            peer.Expect(0,21);
            peer.Expect(0,356);
            peer.Barrier();

            void Drain() => drain(peer,packet =>
            {
                if (packet.Opcode == 31) enabled++;
                if (packet.Opcode == 115) bossChecks++;
            });
            void RejectPassive(byte[] body, string label)
            {
                string before = State();
                peer.Send(39,body);
                Drain();
                That(State() == before && enabled == 0 && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],label);
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true);
            peer.Barrier();
            That(peer.CompletedQuestHistory[4+3842] == 1 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3842",peer.Id) is [[1L]] &&
                fixture.Query("SELECT map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[70407L]],
                "回城重连恢复3842完成位、唯一交付及物件终末结算");
            string before = State();
            peer.Send(39,passive);
            drain(peer,null);
            That(State() == before,"重选后的迟到52B物件报告不能复活旧实例");
        }

        string State() => CharacterState(fixture,created.Id);
    }

    static void VerifyZeroKill(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var area = resources.Towns.Areas.Values.First(row=>row.Level<=54 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option=>option.DungeonId==7156));
        var done = new HashSet<int>(area.Quests) { 3446 };
        if (area.AnyQuests.Length>0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var candidate = CharacterCreation.Prepare("ZeroKillTCP",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,54,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(54) },resources.Learning);
            var repository = new CharacterRepository(fixture.Database);
            created = repository.Create(owner,candidate with
                { Level=54,AcceptedStoryLevel=54,CompletedQuests=done.ToArray(),InitialLearning=learning });
            created = repository.CommitInventory(owner,created,new(created.Inventory,created.Inventory with { Gold=10000 },"test_zero_card"));
        }
        byte[] passive = PassiveDeath(69292,74);
        int enabled=0,bossChecks=0;
        byte[]? clearReward=null;
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            var map = FormalDungeonTcpTests.Enter(peer,3447,7156);
            That(U32(map,14)==70184 && map[18]==1 && U32(map,27)==60409 && map[40]==1,
                "3447真实70184保留唯一友方APC60409，不造敌怪");
            FormalDungeonTcpTests.Load(peer); Drain();
            string before = State();
            peer.Send(39,passive); Drain();
            That(State()==before && enabled==0,"未到70185的74B物件报告不能提前清图");
            var request = new byte[100]; request[0]=1;
            peer.Send(45,request); peer.Expect(1,45,[1]); map=peer.Expect(0,29).Body;
            That(U32(map,14)==70185 && map[18]==0,"真实终点70185没有怪物行，不能填充假Boss或死亡");
            FormalDungeonTcpTests.Load(peer); Drain();
            before=State();
            var corrupted = passive.ToArray(); corrupted[^1]^=1;
            peer.Send(39,corrupted); Drain();
            That(State()==before && enabled==0,"终点错误摘要不能保存空死亡scene或发布31");
            peer.Send(39,passive); peer.Expect(0,574,[1,0,0,0,119,13,0,0,0,0]); peer.Expect(0,31,[]); enabled++;
            Drain();
            That(enabled==1 && bossChecks==0 && fixture.Query(
                "SELECT source_type,dungeon_id,map_id,template_id,length(death_events),typeof(death_events) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                is [["PassiveObject",7156L,70185L,69292L,0L,"blob"]] && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "74B真实形状按原scene保存empty BLOB与精确零growth，574后只发空31而无115");
            before=State(); peer.Send(39,passive); Drain();
            That(State()==before && enabled==1,"零击杀物件精确重放不再次保存scene或推进任务");
            var report = DungeonSettlementProtocolTests.Report(peer.Id,99);
            peer.Send(46,report); Drain();
            That(clearReward is { Length:>=218 } && U32(clearReward,85)==0 && fixture.Query(
                "SELECT t.map_id,length(t.death_events),s.operation_id=t.scene_operation_id,t.total_experience,t.base_experience+t.rank_experience FROM character_dungeon_settlement_receipts t JOIN character_dungeon_scene_clear_receipts s ON s.character_id=t.character_id AND s.operation_id=t.scene_operation_id WHERE t.character_id=$p0",peer.Id)
                is [[70185L,0L,1L,long total,long parts]] && total>0 && total==parts+U32(clearReward,49),
                "真实46保留完整非零通关经验、零怪物契约经验及原scene来源，不用kill1进入奖励");
            var frozen = JsonSerializer.Deserialize<DungeonCardReward>((string)fixture.Query(
                "SELECT reward_json FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id)[0][0])!;
            That(frozen.Gold==1 && fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[0L]],
                "零击杀通关冻结最低金币免费牌，实际选牌前不发资产");
            before=State(); peer.Send(46,report); Drain();
            That(State()==before,"重复零击杀46只回放原结算，不重抽或二次发经验");
            peer.Send(69); peer.Expect(1,69,[1]); peer.Send(70); peer.Expect(1,70); peer.Send(71,[0,0]);
            var selected = peer.Expect(1,71).Body; Drain();
            That(selected[0]==1 && fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[1L]],
                "零击杀免费牌实际提交领取及库存差量");
            peer.Send(71,[1,0]); var paid=peer.Expect(1,71).Body; peer.ReadCardMembershipState(); Drain();
            That(paid[0]==1 && fixture.Query("SELECT paid_cost,paid_reward_json IS NOT NULL FROM character_dungeon_card_options WHERE character_id=$p0",peer.Id)
                is [[long cost,1L]] && cost==SelfTestCatalogs.CardPrices(resources.Archive).Price(57),
                "零击杀原价付费牌沿真实71合法领取并保存费用，不按kill0拒绝");
            peer.Send(34,[34,0,119,13,255,255,1,0,255,255]); var finished=peer.Expect(1,34).Body; Drain();
            That(finished.Length>=4 && finished[0]==1 && U16(finished,1)==3447 && finished[3]==1,
                "真实3447经34按condition-under-clear分类和独立物件来源交付");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]); peer.Expect(0,24);
            peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

            void Drain()=>drain(peer,packet=>
            {
                if (packet.Opcode==31) enabled++;
                if (packet.Opcode==115) bossChecks++;
                if (packet.Opcode==35) clearReward=packet.Body;
            });
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(peer.CompletedQuestHistory[4+3447]==1 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3447",peer.Id) is [[1L]],
                "回城重连恢复3447完成位和一次34，不依赖在线Run");
            string before=State(); peer.Send(39,passive); drain(peer,null);
            That(State()==before,"重连后迟到74B物件报告不能复活旧零击杀实例");
        }
        string State()=>CharacterState(fixture,created.Id);
    }

    static string CharacterState(TcpTestFixture fixture,ushort id)=>fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_card_rewards WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_card_options WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",id)+
        fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",id)+
        fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",id)+
        fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",fixture.Options.DevelopmentAccount);

    internal static byte[] PassiveDeath(uint template,int length = 52)
    {
        var body = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,template);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),ushort.MaxValue);
        body[37] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(42),1);
        FormalDungeonTcpTests.SealDeath(body);
        return body;
    }
}
