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

internal static class QuestCounterattackEntryTcpTests
{
    // 复用quests热服务；本版同格普通map末行覆盖后，真实接取及显式/默认选图均须可入场。
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        int checks = Count;
        var quest = resources.Quests.Get(3553);
        That(quest.MinimumLevel == 70 && quest.MaximumLevel == 99 && quest.Prerequisites.SequenceEqual(new[] { 3552 }) &&
            quest.Condition == "[condition under clear]" && quest.SubType == 11 &&
            quest.IntData.SequenceEqual(new[] { 85,-1,-1 }) && quest.Definition!.Dungeons.Any(row => row.DungeonId == 85),
            "大决战3553保持真实等级、前置及D85通关条件，不改成单图目标");
        var definition = DungeonCatalog.Load(resources.Archive).Get(85);
        var rooms = DungeonRoomCatalog.Load(resources.Archive);
        var maze = definition.Mazes.Single(row => row.ConnectsQuest(3553));
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,85);
        const string account = "counterattack173", name = "Battle173";
        string originalRoles = Roles();
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            // 仅隔离准备等级及入口前置；活动任务和尝试均由下面的真实请求建立。
            var candidate = CharacterCreation.Prepare(name,resources.Characters.Get(0),entrance.Position,entrance.Position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,70,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(70) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level = 70,AcceptedStoryLevel = 70,CompletedQuests = entrance.Completed,InitialLearning = learning });
        }
        using var peer = Open(fixture,account);
        peer.Select(RoleIndex(peer.Roster,created),prepared:true,resumeTown:true,room:false);
        That(peer.Id == created.Id && QuestLifecycleTcpTests.List(peer.AvailableQuests,70).Contains(3553),
            "真实登录下发合格大决战，绑定本次隔离角色");
        peer.Send(31,[31,0,225,13]); peer.Expect(1,31,[1,225,13,1,0,0,0,0]); Drain();
        string accepted = Active();
        That(fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",created.Id)
                is [[3553L,1L]] &&
            fixture.Query("SELECT count(*) FROM character_quest_lifecycle_receipts WHERE character_id=$p0",created.Id) is [[1L]],
            "唯一正式31建立未完成条件和原接取回执");
        long attempt = 0;
        foreach (ushort request in new ushort[] { 3553,0 })
        {
            uint next = checked((uint)(long)fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0",created.Id)[0][0]);
            var plans = new List<DungeonMonsterPlan>();
            foreach (var source in maze.MapSpecification!)
            {
                var room = rooms.Get(source,requirePassiveDefinitions:false);
                var plan = DungeonMonsterPlan.FromRoom(room,created.Id,checked((ushort)next),definition.BasisLevel,playerTeam:0);
                That(plan.Monsters.Count(actor => actor.Source is { SpecialChild:null }) == room.Monsters.Count &&
                    plan.Monsters.Count(actor => actor.AiCharacter is not null) == room.AiCharacters.Count &&
                    plan.SpecialObjects.Count == room.SpecialObjects.Count,"3553完整准备有效房间所有演员："+source.Id);
                plans.Add(plan);
                if (plan.Monsters.Count != 0 || plan.SpecialObjects.Count != 0) next = plan.NextObjectKey;
            }
            var replacement = plans.Single(plan => plan.Room.Map.Id == 92470);
            That(replacement.Monsters.Count == 7 && replacement.Monsters.Count(actor => actor.MonsterId == 61005) == 3 &&
                replacement.Monsters.Count(actor => actor.MonsterId == 61495) == 4 && replacement.SpecialObjects.Count == 0 &&
                plans.All(plan => plan.Room.Map.Id != 92467),"替换格保留92470的七个真实普通怪，不准备被覆盖92467的怪物及10137父对象");
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]); peer.Expect(0,27,new byte[42]);
            var selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(selection,85);
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),request);
            peer.Send(16,selection);
            var information = peer.Expect(0,28).Body;
            That(maze is { ResourceError: null, MapSpecification.Count: 10, LayeredMaps.Count: 0, StoryStages.Count: 0 } &&
                maze.MapSpecification.Single(row => row.Position == new DungeonPoint(5,1)) is { Id: 92470, Kind: "map" } &&
                !maze.MapSpecification.Any(row => row.Id == 92467),"正式3553保留本版有效十房，不将92467混入候选或演出层");
            var start = maze.MapSpecification!.Single(row => row.Position == maze.Starts.Single());
            var expected = plans.Single(plan => plan.Room.Map == start);
            That(information.Length == 36 && U32(information,0) == 85 && information[7] == maze.SourceOrdinal &&
                information[8] == 6 && information[9] == 1,"显式3553及普通0选择原任务迷宫和真实Boss坐标");
            byte[] map = peer.Expect(0,29).Body;
            That(map.Length >= 19 && map[0] == 0 && map[1] == 0 && U32(map,14) == 92463 &&
                map[18] == expected.Monsters.Count + expected.SpecialObjects.Count && map.Length >= 19+22*map[18],
                "NOTI29从92463入场且完整保留起点演员");
            for (int index = 0; index < expected.Monsters.Count; index++)
            {
                var actor = expected.Monsters[index];
                int at = 19+22*index;
                That(U32(map,at+2) == actor.SourceOrdinal && U16(map,at+6) == actor.ObjectKey && U32(map,at+8) == actor.MonsterId &&
                    map[at+12] == actor.CandidateLevel && map[at+13] == actor.SpawnType &&
                    map[at+21] == (actor.IsFriendly ? 1 : 0),"起点29保留原ordinal、模板、等级、品级及清房排除位");
            }
            for (int index = 0; index < expected.SpecialObjects.Count; index++)
            {
                var actor = expected.SpecialObjects[index];
                int at = 19+22*(expected.Monsters.Count+index);
                That(U32(map,at+2) == actor.Source.SourceOrdinal && U16(map,at+6) == actor.ObjectKey && U32(map,at+8) == actor.Source.ObjectId &&
                    map[at+13] == 9 && map[at+21] == 1,"起点特殊父对象不冒充普通怪物");
            }
            var keys = Enumerable.Range(0,map[18]).Select(index => U16(map,25+22*index)).ToArray();
            That(keys.All(key => key != created.Id) && keys.Distinct().Count() == keys.Length,"实际演员key唯一且不碰撞本人");
            FormalDungeonTcpTests.Load(peer); Drain();
            That(Active() == accepted && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",created.Id)
                is [[long actualAttempt]] && actualAttempt == ++attempt,"两次正式加载保留原Acceptance及未完成scalar，各建独立尝试");
            peer.Send(42); peer.Expect(1,42,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24);
            That(QuestLifecycleTcpTests.List(peer.Expect(0,21).Body,70).Contains(3553),"未通关回城仍下发原任务");
            peer.Expect(0,356); peer.Barrier();
            That(Active() == accepted &&
                fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",created.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3553",created.Id) is [[0L]],
                "入场、加载、未清42不伪造死亡、清图、结算或任务完成");
        }
        That(fixture.Query("SELECT count(*) FROM character_quest_lifecycle_receipts WHERE character_id=$p0",created.Id) is [[1L]],
            "两次入场只使用同一原接取，不重复31");
        // 只有成功后经原删除入口清本次角色；失败留存完整现场。
        peer.Send(7); peer.Expect(1,7,[1]);
        ushort ordinal = RoleIndex(peer.Expect(0,2).Body,created);
        peer.Send(6,Build(writer => { writer.Write(ordinal); writer.Write(Name(created.Name)); }));
        peer.Expect(1,6,Build(writer => { writer.Write((byte)1); writer.Write((byte)0); writer.Write(ordinal); }));
        peer.Expect(0,2); peer.Send(692,Name(created.Name)); peer.Expect(1,692,[1]);
        That(Roles() == originalRoles,"成功后保留原名册，只删除本次自建角色");
        Console.WriteLine($"PASS {Count-checks} 大决战3553真实接取及显式/0入场（未验剧情通关与客户端）");

        void Drain() => drain(peer,null);
        string Active() => fixture.Snapshot("SELECT slot,quest_id,acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",created.Id);
        string Roles() => fixture.Snapshot("SELECT id,slot,name FROM characters WHERE account=$p0 ORDER BY slot",account);
    }

    static ushort RoleIndex(byte[] roster,Character role)
    {
        int at = 15;
        for (ushort ordinal = 0; ordinal < U16(roster,13); ordinal++)
        {
            int width = checked((int)U32(roster,at+2));
            if (U16(roster,at) == role.Slot && DecodeName(roster,at+6,width) == role.Name) return ordinal;
            int equipment = at+21+width;
            at = equipment+AssetWireChecks.ActorEntries(roster,equipment).ConsumedBytes+48;
        }
        throw new InvalidDataException("大决战测试角色不在返回名册中");
    }
}
