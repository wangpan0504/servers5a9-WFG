using System.Buffers.Binary;
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

internal static class QuestCollaboratorTcpTests
{
    // 真实3236完整资源入场；不把合成TCP输入当作客户端原网络问题复现。
    // 末图ACT/CMT304只证明入场演出声明，本组不把合成输入当作剧情播放或通关。
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        int checks = Count;
        var quest = resources.Quests.Get(3236);
        That(quest.MinimumLevel == 31 && quest.MaximumLevel == 99 && quest.Prerequisites.SequenceEqual(new[] { 3235 }) &&
            quest.MainQuest is null && quest.Condition == "[clear map]" && quest.SubType == -1 &&
            quest.IntData.SequenceEqual(new[] { 91357 }) && quest.Definition!.Dungeons.Any(row => row.DungeonId == 32) &&
            quest.Definition.AcceptanceRewards.IsEmpty, "3236保持真实等级、前置、D32和91357清图合同");
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var definition = dungeons.Get(32);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var maze = definition.Mazes.Single(row => row.ConnectsQuest(3236));
        (int Id, DungeonPoint Position)[] route =
        [
            (91351,new(0,0)), (91352,new(1,0)), (91353,new(2,0)), (91354,new(2,1)),
            (91355,new(3,1)), (91356,new(4,1)), (91357,new(5,1))
        ];
        That(definition.MinimumLevel == 31 && definition.BasisLevel == 33 && maze.Starts.Single() == new DungeonPoint(0,0) &&
            maze.Bosses.Single() == new DungeonPoint(5,1) && maze.MapSpecification!.Select(row => (row.Id,row.Position)).SequenceEqual(route) &&
            maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 && maze.StoryStages.Count == 0 && maze.WarpMaps.Count == 0,
            "D32选中3236完整七图及真实起点/Boss坐标，不借3232/3233或普通迷宫");
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,32);
        That(entrance.Completed.Contains(3235) && !entrance.Completed.Contains(3236) &&
            resources.Towns.IsPosition(entrance.Position,entrance.Completed.ToHashSet()),
            "复用资源几何准备合法31级入口，保留3236未完成");
        const string account = "collaborator133", name = "Collab133";
        That(NameWidth(name) <= 12, "隔离昵称符合CP936的12字节上限");
        string originalRoles = fixture.Snapshot("SELECT id,slot,name FROM characters WHERE account=$p0 ORDER BY slot",account);
        Character created;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            // 只准备入口等级与原前置；接取、实例尝试和条件均经正式网络入口产生。
            var candidate = CharacterCreation.Prepare(name,resources.Characters.Get(0),entrance.Position,entrance.Position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,31,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(31) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level = 31,AcceptedStoryLevel = 31,CompletedQuests = entrance.Completed,InitialLearning = learning });
        }
        That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0",created.Id) is [[0L]],
            "建角不预填3236活动槽、条件或AcceptanceId");
        string accepted = "", acceptance = "";
        long attempt = 0;
        using (var peer = Open(fixture,account))
        {
            Select(peer);
            That(QuestLifecycleTcpTests.List(peer.AvailableQuests,31).Contains(3236), "真实登录21下发合格3236");
            peer.Send(31,[31,0,164,12]); peer.Expect(1,31,[1,164,12,1,0,0,0,0]); Drain(peer);
            accepted = Active();
            acceptance = (string)fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3236",created.Id)[0][0];
            That(Guid.TryParseExact(acceptance,"N",out var identity) && identity != Guid.Empty &&
                fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",created.Id) is [[3236L,1L]] &&
                fixture.Query("SELECT quest_id,acceptance_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0",created.Id)
                    is [[3236L,string receiptIdentity]] && receiptIdentity == acceptance,
                "唯一真实31提交未完成scalar及同一活动槽/回执接取身份");
            EntryRound(peer,3236);
            EntryRound(peer,0);
            That(attempt == 2, "同一连接未清42返回后可再次以0请求合法入场，不重复接取");
            // Dispose实际关闭TCP，后面Open创建新连接；不以同Session重选代替断线恢复。
        }
        using (var peer = Open(fixture,account))
        {
            var packets = Select(peer);
            byte[] active = packets.Single(packet => packet.Kind == 1 && packet.Opcode == 4).Body;
            That(active.Length == 235 && active[0] == 1 && U16(active,9) == created.Id &&
                Enumerable.Range(0,30).All(slot => U16(active,22+6*slot) == (slot == 0 ? 3236 : ushort.MaxValue) &&
                    U32(active,24+6*slot) == (slot == 0 ? 1u : 0u)) &&
                QuestLifecycleTcpTests.List(peer.AvailableQuests,31).Contains(3236) && Active() == accepted &&
                fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3236",created.Id)
                    is [[string restored]] && restored == acceptance,
                "实际断线重连21/ACK4三十槽与数据库恢复原3236接取身份及未完成状态");
            EntryRound(peer,0);
            That(attempt == 3 && fixture.Query("SELECT count(*) FROM character_quest_lifecycle_receipts WHERE character_id=$p0",created.Id) is [[1L]],
                "重连后0请求仍能合法入场，三次尝试只保留一次原接取");
            // 仅全部成功后正式删除本次角色；失败不在finally清数据库或覆盖现场。
            peer.Send(7); peer.Expect(1,7,[1]);
            ushort ordinal = RoleIndex(peer.Expect(0,2).Body,created);
            peer.Send(6,Build(writer => { writer.Write(ordinal); writer.Write(Name(created.Name)); }));
            peer.Expect(1,6,Build(writer => { writer.Write((byte)1); writer.Write((byte)0); writer.Write(ordinal); }));
            peer.Expect(0,2); peer.Send(692,Name(created.Name)); peer.Expect(1,692,[1]);
            That(fixture.Snapshot("SELECT id,slot,name FROM characters WHERE account=$p0 ORDER BY slot",account) == originalRoles,
                "成功后只经正式删除流程清本次角色，保留原名册");
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }
        Console.WriteLine($"PASS {Count-checks} 3236真实七图准备、显式/0入场及退出/重连恢复（未验完整剧情和客户端）");

        WirePacket[] Select(TcpPeer peer)
        {
            var packets = peer.Select(RoleIndex(peer.Roster,created),prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id, "3236只绑定本次隔离角色");
            return packets;
        }
        void EntryRound(TcpPeer peer, ushort request)
        {
            uint next = checked((uint)(long)fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0",created.Id)[0][0]);
            var plans = Plans(next);
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]); peer.Expect(0,27,new byte[42]);
            var selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(selection,32);
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),request);
            peer.Send(16,selection);
            var information = peer.Expect(0,28).Body;
            That(information.Length == 36 && U32(information,0) == 32 && information[7] == maze.SourceOrdinal &&
                information[8] == 5 && information[9] == 1, "显式3236/0选择真实任务迷宫及原Boss坐标：" + request);
            byte[] map = peer.Expect(0,29).Body;
            Actors(map,plans[0]);
            FormalDungeonTcpTests.Load(peer); Drain(peer);
            That(Active() == accepted && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",created.Id)
                    is [[long actualAttempt]] && actualAttempt == ++attempt,
                "正式加载保持原Acceptance及scalar，并保存独立尝试：" + request);
            peer.Send(42); peer.Expect(1,42,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24);
            That(QuestLifecycleTcpTests.List(peer.Expect(0,21).Body,31).Contains(3236), "未清42回城后21仍含原任务");
            peer.Expect(0,356); peer.Barrier();
            That(Active() == accepted && NoClear(), "入场/加载/未清42不生成死亡、清图、结算或完成事实：" + request);
        }
        DungeonMonsterPlan[] Plans(uint next)
        {
            var result = new List<DungeonMonsterPlan>();
            foreach (var map in maze.MapSpecification!)
            {
                var room = rooms.Get(map,requirePassiveDefinitions:false);
                var plan = DungeonMonsterPlan.FromRoom(room,created.Id,checked((ushort)next),definition.BasisLevel,playerTeam:0);
                That(plan.Monsters.Count(actor => actor.Source is { SpecialChild:null }) == room.Monsters.Count &&
                    plan.Monsters.Count(actor => actor.AiCharacter is not null) == room.AiCharacters.Count &&
                    plan.SpecialObjects.Count == room.SpecialObjects.Count, "完整准备七图普通/APC/特殊身份，不删后续演员：" + map.Id);
                result.Add(plan);
                if (plan.Monsters.Count != 0 || plan.SpecialObjects.Count != 0) next = plan.NextObjectKey;
            }
            var terminal = result.Single(plan => plan.Room.Map.Id == 91357);
            That(terminal.Monsters is [var boss,var friend,var apc] &&
                boss is { MonsterId:107000163, SpawnType:3, Team:100, IsBoss:true, IsBlocking:true, IsFriendly:false } &&
                friend is { MonsterId:107000152, SpawnType:0, Team:0, IsBoss:false, IsBlocking:false, IsFriendly:true, AiCharacter:null } &&
                apc is { MonsterId:6516, SpawnType:5, IsBlocking:false, IsFriendly:true, AiCharacter.Faction:"[character]" },
                "91357保留107000163真实Boss、107000152友方普通monster及6516角色APC，不互换品级/阵营");
            return result.ToArray();
        }
        void Actors(byte[] map, DungeonMonsterPlan plan)
        {
            That(map.Length >= 19 && map[0] == 0 && map[1] == 0 && U32(map,14) == 91351 &&
                map[18] == plan.Monsters.Count + plan.SpecialObjects.Count && map.Length >= 19 + 22*map[18],
                "真实29完整起点地图、坐标与演员数量");
            for (int index = 0; index < plan.Monsters.Count; index++)
            {
                int at = 19 + 22*index;
                var actor = plan.Monsters[index];
                That(U32(map,at+2) == actor.SourceOrdinal && U16(map,at+6) == actor.ObjectKey &&
                    U32(map,at+8) == actor.MonsterId && map[at+12] == actor.CandidateLevel &&
                    map[at+13] == actor.SpawnType && map[at+21] == (actor.IsFriendly ? 1 : 0),
                    "29保留每个实际演员的ordinal/key/模板/等级/品级/排除位");
            }
            for (int index = 0; index < plan.SpecialObjects.Count; index++)
            {
                int at = 19 + 22*(plan.Monsters.Count+index);
                var actor = plan.SpecialObjects[index];
                That(U32(map,at+2) == actor.Source.SourceOrdinal && U16(map,at+6) == actor.ObjectKey &&
                    U32(map,at+8) == actor.Source.ObjectId && map[at+13] == 9 && map[at+21] == 1,
                    "29完整保留特殊父对象身份，不冒充普通怪物");
            }
            var keys = Enumerable.Range(0,map[18]).Select(index => U16(map,25+22*index)).ToArray();
            That(keys.Distinct().Count() == keys.Length && keys.All(key => key != created.Id), "当前实际演员key唯一且不碰撞本人");
        }
        void Drain(TcpPeer peer) => drain(peer,packet =>
            That(packet.Opcode is not (31 or 115), "未发生真实死亡/清图时不得发布115或通关31"));
        string Active() => fixture.Snapshot("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id);
        bool NoClear() => fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3236",created.Id) is [[0L]];
    }

    static ushort RoleIndex(byte[] roster, Character role)
    {
        int at = 15;
        for (ushort ordinal = 0; ordinal < U16(roster,13); ordinal++)
        {
            int width = checked((int)U32(roster,at+2));
            if (U16(roster,at) == role.Slot && DecodeName(roster,at+6,width) == role.Name) return ordinal;
            int equipment = at + 21 + width;
            at = equipment + AssetWireChecks.ActorEntries(roster,equipment).ConsumedBytes + 48;
        }
        throw new InvalidDataException("3236隔离角色不在实际名册中：" + role.Name);
    }
}
