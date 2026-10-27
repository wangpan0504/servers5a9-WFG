using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AlbertHuntTcpTests
{
    // 真实MAP的完整静态演员；25+1是任务门槛，不能把56只实例怪物截成26只。
    static readonly (DungeonPoint Position,int[] Monsters,int Objects)[] Route =
    [
        (new(0,0),[65027,65027,65028,65027],13),
        (new(1,0),[65031,65031,65031,65031,65031],19),
        (new(1,1),[65031,65031,65031,64039,64039,64039,64039],12),
        (new(1,2),[65031,65031,65031,65031,65031,65031],6),
        (new(1,3),[65028,65027,65028,65027,65027,65028],8),
        (new(2,3),[63512,63512,63512,65031,65031,65031,65031,65031],11),
        (new(3,3),[65027,65028,65028,65027,65031,65031,65031,65031,65031],26),
        (new(4,3),[65031,65031,65031,65031,65031],15),
        (new(5,3),[65031,65031,65031,65031,65031,70176],11)
    ];

    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        const string account = "albert-hunt", name = "AlbertHunt";
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var definition = dungeons.Get(158);
        var maze = definition.Mazes.Single(row => row.ConnectsQuest(3472));
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        That(definition.MinimumLevel == 56 && definition.BasisLevel == 59 && maze.SourceOrdinal == 2 &&
            maze.BossEntrance is null && maze.LayeredMaps.Count == 0 && maze.StoryStages.Count == 0 && maze.WarpMaps.Count == 0 &&
            maze.MapSpecification!.Select(map => (map.Id,map.Position)).SequenceEqual(
                Route.Select((row,index) => (70248+index,row.Position))) &&
            QuestStoryRouteTests.Path(maze.StaticTopology,maze.Starts.Single(),maze.Bosses.Single()).SequenceEqual(Route.Select(row => row.Position)),
            "3472资源保持唯一九图蛇形路线，不借普通图或跳过房间");
        var done = new HashSet<int> { 3457 };
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 58 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 158));
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        done.ExceptWith([3458,3472,3473]);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
        Character created;
        ushort rewardSlot;
        string parentAcceptance, huntAcceptance;
        byte[] lastDeath = [];
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            // 登录前只准备等级、入口前置和已有2碎片；父子活动槽、猎杀计数、清图及领奖都从真实请求建立。
            var candidate = CharacterCreation.Prepare(name,resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,58,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(58) },resources.Learning);
            var range = ItemSlotBoundService.Range(resources.ItemResources.Items.Get(10164800).Kind,candidate.Inventory.Expansion);
            rewardSlot = checked((ushort)Enumerable.Range(range.Start,range.End-range.Start+1)
                .First(index => candidate.Inventory.Find(new(InventoryListType.Main,checked((short)index))) is null));
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level = 58,AcceptedStoryLevel = 1,CompletedQuests = done.ToArray(),InitialLearning = learning,
                    Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(new(0,
                        new(InventoryListType.Main,checked((short)rewardSlot)),
                        InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(10164800),2))) } });
        }
        QuestDailyCoexistence? daily = null;
        using (var peer = Open(fixture,account))
        {
            Roster(peer.Roster,true,created);
            peer.Select(0,prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id && Materials() == 2,"独立账号唯一名册行0对应自建角色，初始已有2个任务碎片");
            daily = new(fixture,created.Id,13040,1636);
            That(fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=13040",created.Id) is [[0L]],
                "58级每日占原始0槽，后续父子只能使用1及2槽");
            peer.Send(31,[31,0,130,13]); peer.Expect(1,31,[1,130,13,2,0,0,0,0]); drain(peer,null);
            peer.Send(31,[31,0,144,13]); peer.Expect(1,31,[1,144,13,25,2,0,0,0]); drain(peer,null);
            string identities = Identities();
            parentAcceptance = fixture.Snapshot("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3458",created.Id);
            huntAcceptance = (string)fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3472",created.Id)[0][0];
            Progress(537);
            peer.Send(34,[34,0,144,13,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); peer.Barrier();
            That(Identities() == identities && Materials() == 2 && Completions() is [[0L]],
                "未猎杀前真实34拒绝，不把等级或父接取变成已达成子任务");
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]); peer.Expect(0,27,peer.SelectionNoticeBody());
            var selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(selection,158);
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),3458);
            peer.Send(16,selection);
            var information = peer.Expect(0,28).Body;
            That(information.Length == 36 && U32(information) == 158 && information[7] == 2 && information[8] == 5 && information[9] == 3,
                "真实父3458请求借当前子3472的maze2，保持正式Boss grid");
            var map = peer.Expect(0,29).Body;
            var keys = new HashSet<ushort>();
            int targetDeaths = 0, deaths = 0, passiveObjects = 0;
            uint scalar = 537;
            for (int roomIndex = 0; roomIndex < Route.Length; roomIndex++)
            {
                var expected = Route[roomIndex];
                var source = rooms.Get(maze.MapSpecification![roomIndex]);
                passiveObjects += source.PassiveObjects.Count;
                That(source.Monsters.Select(actor => actor.MonsterId).SequenceEqual(expected.Monsters) &&
                    source.PassiveObjects.Count == expected.Objects && source.AiCharacters.All(row => DungeonCompanion.IsCompanionRow(row.Id)) &&
                    source.SpecialObjects.Count == 0 &&
                    source.MonsterTeams is null or { Count: 0 } && source.Monsters.All(actor => actor.Modifiers.Count == 0 &&
                        actor.RankKind == (actor.MonsterId == 70176 ? "[boss]" : "[normal]")),
                    "每图完整演员及普通物件保留，无友方/APC/dummy或特殊父子："+source.Map.Id);
                That(U32(map,14) == 70248+roomIndex && map[0] == expected.Position.X && map[1] == expected.Position.Y &&
                    map[18] == expected.Monsters.Length && map.Length == 23+22*expected.Monsters.Length,
                    "真实29完整演员数与九图原序一致，不删非任务怪："+source.Map.Id);
                var actors = FormalDungeonTcpTests.ObserveChampionTypes(map,
                    DungeonMonsterPlan.FromRoom(source, peer.Id, 100, definition.BasisLevel!.Value, playerTeam: 0), definition);
                FormalDungeonTcpTests.Load(peer); drain(peer,null);
                for (int actorIndex = 0; actorIndex < expected.Monsters.Length; actorIndex++)
                {
                    int offset = 19+22*actorIndex, template = expected.Monsters[actorIndex];
                    ushort key = U16(map,offset+6);
                    That(U32(map,offset+2) == actorIndex && U32(map,offset+8) == template && map[offset+12] == 59 &&
                        map[offset+13] == actors.Monsters[actorIndex].SpawnType && map[offset+14] == 0 && map[offset+21] == 0 &&
                        key != peer.Id && keys.Add(key),"独立22B演员合同与本次实际唯一key一致："+source.Map.Id);
                    uint previous = scalar;
                    if (template == 65031) { targetDeaths++; if (targetDeaths <= 25) scalar--; }
                    if (template == 70176) scalar -= 512;
                    lastDeath = FormalDungeonTcpTests.Death(key,peer.Id,map[offset+12]);
                    peer.Send(39,lastDeath);
                    bool notified = false;
                    drain(peer,packet =>
                    {
                        if (packet.Kind != 0 || packet.Opcode != 574) return;
                        Bytes(packet.Body,Build(writer => { writer.Write(3u); writer.Write((ushort)13040); writer.Write(1636u); writer.Write((ushort)3458); writer.Write(2u);
                            writer.Write((ushort)3472); writer.Write(scalar); }),"每次实际目标死亡574只推进原子任务通道，父仍等待正式交付");
                        notified = true;
                    });
                    deaths++;
                    That(notified == (scalar != previous) && Identities() == identities,
                        "目标到25后饱和、其余怪不减通道，死亡不换原父子接取身份");
                    Progress(scalar);
                    if (roomIndex == 1 && actorIndex == 0) Duplicate(peer,lastDeath,"同房重复65031死亡");
                }
                if (roomIndex+1 < Route.Length)
                {
                    var next = Route[roomIndex+1].Position;
                    var move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                }
            }
            That(deaths == 56 && targetDeaths == 34 && passiveObjects == 121 && scalar == 0 && Materials() == 2 &&
                fixture.Query("SELECT count(*),count(DISTINCT event_id),count(DISTINCT monster_key),count(DISTINCT instance_id) FROM character_dungeon_growth_receipts WHERE character_id=$p0",created.Id)
                    is [[56L,56L,56L,1L]] &&
                fixture.Query("SELECT monster_id,count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 GROUP BY monster_id ORDER BY monster_id",created.Id)
                    is [[63512L,3L],[64039L,4L],[65027L,8L],[65028L,6L],[65031L,34L],[70176L,1L]],
                "全部56真实死亡各提交一次，34只目标不下溢，121普通物件不伪造怪物死亡或提前发碎片");
            Duplicate(peer,lastDeath,"Boss完成后迟到死亡");
            peer.Send(34,[34,0,144,13,255,255,1,0,255,255]);
            peer.Expect(1,34,Build(writer => { writer.Write((byte)1); writer.Write((ushort)3472); writer.Write((byte)2);
                writer.Write(0u); writer.Write((byte)0); writer.Write((byte)1); writer.Write(rewardSlot);
                writer.Write(10164800u); writer.Write(2u); writer.Write(new byte[9]); }));
            var balance = peer.Expect(0,14).Body;
            That(balance.Length == 126 && balance[0] == 0 && U16(balance,1) == 1 && U16(balance,3) == rewardSlot &&
                U32(balance,5) == 10164800 && U32(balance,9) == 4,"猎杀子34之后以完整Main行投影已有2加奖励2的真实碎片4");
            peer.Expect(0,574,Convert.FromHexString("02000000F03264060000820D01000000")); peer.Expect(0,21);
            That(peer.Expect(0,356).Body[4+3472] == 1,"正式交付后才发布3472完成位"); peer.Barrier();
            Delivered();
            string delivered = State();
            peer.Send(34,[34,0,144,13,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); peer.Barrier();
            That(State() == delivered,"重复猎杀子34不重复发碎片或关闭父任务");
            peer.Send(31,[31,0,145,13]); peer.Expect(1,31,[1,145,13,0,0,0,0,0]);
            peer.Expect(0,574,Convert.FromHexString("03000000F03264060000820D00000000910D00000000"));
            peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(Materials() == 4 && Completions() is [[1L]],"Boss及猎杀奖励后立即接真实末子，4/3只归零父子条件、不自动消费或领奖");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); drain(peer,null);
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }
        using (var peer = Open(fixture,account))
        {
            Roster(peer.Roster,true,created);
            var selected = peer.Select(0,prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id && U16(selected[0].Body,22) == 13040 && U32(selected[0].Body,24) == 1636 &&
                U16(selected[0].Body,28) == 3458 && U32(selected[0].Body,30) == 0 &&
                U16(selected[0].Body,34) == 3473 && U32(selected[0].Body,36) == 0 &&
                peer.CompletedQuestHistory[4+3472] == 1 && Materials() == 4 && Completions() is [[1L]],
                "自然接取后重连选角回放父0末子0和真实猎杀完成，不预置重连结果或重发奖励");
            Duplicate(peer,lastDeath,"重连后的旧实例Boss迟到死亡");
            string ready = State();
            for (int repeat = 0; repeat < 3; repeat++)
            {
                peer.Send(33,[33,0,145,13,0,0]); peer.Expect(1,33,[1,145,13,0,0,0,0]); peer.Barrier();
                That(State() == ready && Materials() == 4,"真实死亡与子奖励后4/3正常寻物重复33仅回权威0，不发奖或刷新循环");
            }
            // 所有断言成功后才删本条自建角色；选角阶段以真实名册/692确认，不发送1347。
            peer.Send(7); peer.Expect(1,7,[1]); Roster(peer.Expect(0,2).Body,true,created);
            peer.Send(6,Build(writer => { writer.Write((ushort)0); writer.Write(Name(name)); }));
            peer.Expect(1,6,[1,0,0,0]); Roster(peer.Expect(0,2).Body,false);
            peer.Send(692,Name(name)); peer.Expect(1,692,[1]);
            That(fixture.Query("SELECT count(*) FROM characters WHERE account=$p0",account) is [[0L]],
                "成功后仅清理独立账号自建角色，名册行0不误当持久slot");
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }

        void Progress(uint expected) => That(daily!.OtherRows("quest_id,condition_state","ORDER BY slot")
            is [[3458L,2L],[3472L,long actual]] && actual == expected,"实际持久父子条件与本次完整死亡顺序一致");
        string Identities() => fixture.Snapshot("SELECT slot,quest_id,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id);
        long Materials() => (long)fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10164800",created.Id)[0][0];
        object[][] Completions() => fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id);
        void Delivered() => That(Materials() == 4 && Completions() is [[1L]] &&
            fixture.Query("SELECT quest_id,condition_type,experience_amount,acceptance_id FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id)
                is [[3472L,2L,0L,string saved]] && saved == huntAcceptance &&
            fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",created.Id) is [[56L]] &&
            daily!.OtherRows("quest_id,condition_state") is [[3458L,1L]] &&
            fixture.Snapshot("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3458",created.Id) == parentAcceptance &&
            fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id IN(3458,3472,3473)",created.Id) is [[3472L]],
            "真实猎杀34仅父2→1、子完成和碎片4，原父接取不变，不自动接3473或交父奖励");
        void Duplicate(TcpPeer peer,byte[] death,string label)
        {
            string before = State(); peer.Send(39,death); drain(peer,null);
            That(State() == before,label+"不再提交死亡、改条件或重复奖励");
        }
        string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY combat_sequence",created.Id)+
            fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id)+
            fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",created.Id);
    }

    static void Roster(byte[] body,bool present,Character? role = null)
    {
        That(body[0] == 2 && body[1] == (present ? 1 : 0) && U16(body,7) == body[1] && U16(body,13) == body[1],
            "独立猎杀账号名册只有本条角色或清理后的空名册");
        if (present) That(role is not null && U16(body,15) == role.Slot &&
            DecodeName(body,21,checked((int)U32(body,17))) == role.Name,"唯一名册行0绑定真实slot和确认名");
    }
}
