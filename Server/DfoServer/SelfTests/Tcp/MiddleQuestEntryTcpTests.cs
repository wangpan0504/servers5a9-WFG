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

internal static class MiddleQuestEntryTcpTests
{
    // 借用已启动的正式fixture，不为各条路线新建服务或另装PVF。
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth)
    {
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        // NPC两条路线只复用入场wire夹具；不制造NPC退场、dummy死亡或sub11结算。
        var scenarios = MiddleQuestEntryTests.NpcScenarios.Concat(MiddleQuestEntryTests.Scenarios);
        foreach (var scenario in scenarios)
        {
            string originalRoles = Roles();
            var quest = resources.Quests.Get(scenario.Quest);
            var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,scenario.Dungeon);
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                // 隔离准备入口资格；正式31接取，不预写任务条件、死亡或清图。
                var candidate = CharacterCreation.Prepare("Middle"+scenario.Quest,resources.Characters.Get(0),
                    entrance.Position,entrance.Position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,checked((byte)quest.MinimumLevel),candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(checked((byte)quest.MinimumLevel)) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                {
                    Level=checked((byte)quest.MinimumLevel),AcceptedStoryLevel=checked((uint)quest.MinimumLevel),
                    CompletedQuests=entrance.Completed,InitialLearning=learning
                });
            }
            using var peer = Open(fixture);
            peer.Select(RoleIndex(peer.Roster,created),prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id,"地轨入场使用本条真实角色身份");
            // Enter辅助每次都会发送31；本组只正式接取一次，两次16必须复用同一槽位和接取身份。
            peer.Send(31,[31,0,(byte)scenario.Quest,(byte)(scenario.Quest >> 8)]);
            peer.Expect(1,31,[1,(byte)scenario.Quest,(byte)(scenario.Quest >> 8),1,0,0,0,0]);
            peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            string acceptance = Active();
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                created.Id,scenario.Quest) is [[1L]],"唯一真实31接取保留未完成条件："+scenario.Quest);
            long attempt = 0;
            foreach (ushort request in new ushort[] { scenario.Quest,0 })
            {
                peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]); peer.Expect(0,27,peer.SelectionNoticeBody());
                var selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
                BinaryPrimitives.WriteUInt32LittleEndian(selection,scenario.Dungeon);
                BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),request);
                peer.Send(16,selection);
                var information = peer.Expect(0,28).Body;
                That(information.Length == 36,"正式NOTI28完整36B");
                var map = peer.Expect(0,29).Body;
                var maze = dungeons.Get(checked((int)scenario.Dungeon)).Mazes.Single(row => row.SourceOrdinal == information[7]);
                That(maze.ConnectsQuest(scenario.Quest) && U32(map,14) == scenario.StartMap &&
                    information[8] == maze.Bosses.Single().X && information[9] == maze.Bosses.Single().Y,
                    $"地轨实际TCP显式/0选择同一原任务迷宫与Boss grid：quest={scenario.Quest} request={request}");
                That(maze.MapSpecification!.Concat(maze.LayeredMaps).Concat(maze.ConditionalMaps)
                        .Select(row => row.Id).SequenceEqual(scenario.MapIds) &&
                    maze.MapSpecification!.Single(row => row.Position == maze.Bosses.Single()).Id == scenario.TargetMap,
                    "TCP选中完整显式MAP名册及真实末图，不按连续编号推算："+scenario.Quest);
                var start = maze.MapSpecification!.Single(row => row.Position == maze.Starts.Single());
                var expected = DungeonMonsterPlan.Create(rooms,start,peer.Id,100,dungeons.Get(checked((int)scenario.Dungeon)).BasisLevel,0);
                That(map[18] == expected.Monsters.Count+expected.SpecialObjects.Count,
                    "入场29完整包含资源声明的起点演员，不删友方或特殊对象："+scenario.Quest);
                for (int at = 0; at < expected.Monsters.Count; at++)
                {
                    var actor = expected.Monsters[at];
                    int offset = 19+22*at;
                    That(U32(map,offset+8) == actor.MonsterId && map[offset+12] == actor.CandidateLevel &&
                        map[offset+13] == actor.SpawnType && map[offset+21] == (actor.IsFriendly ? 1 : 0),
                        "独立按完整22B演员行核模板/等级/类型和清房排除位："+scenario.Quest);
                }
                for (int at = 0; at < expected.SpecialObjects.Count; at++)
                {
                    var actor = expected.SpecialObjects[at];
                    int offset = 19+22*(expected.Monsters.Count+at);
                    That(U32(map,offset+2) == actor.Source.SourceOrdinal && U32(map,offset+8) == actor.Source.ObjectId &&
                        map[offset+13] == 9 && map[offset+21] == 1,
                        "特殊父对象保留原ordinal/模板及独立类型，不冒充普通怪物："+scenario.Quest);
                }
                var actualKeys = Enumerable.Range(0,map[18]).Select(at => U16(map,25+22*at)).ToArray();
                That(actualKeys.All(key => key != peer.Id) && actualKeys.Distinct().Count() == actualKeys.Length,
                    "起点全部演员使用本次实际唯一key且不碰撞本人："+scenario.Quest);
                FormalDungeonTcpTests.Load(peer);
                peer.Barrier();
                That(Active() == acceptance && fixture.Query(
                    "SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[long actualAttempt]] && actualAttempt == ++attempt,
                    "两种真实请求各建立一次尝试，保留同一原接取及未完成条件："+scenario.Quest);
                peer.Send(42);
                peer.Expect(1,42,[1]);
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24);
                peer.Expect(0,21);
                peer.Expect(0,356);
                peer.Barrier();
                That(Active() == acceptance && fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,scenario.Quest) is [[0L]],
                    "入场/加载/未通关返回不伪造实际死亡、清图、结算或任务完成："+scenario.Quest);
            }
            // 仅成功路径释放本条自建角色；失败不在finally删存档，保留原接取及入场现场。
            peer.Send(7); peer.Expect(1,7,[1]);
            ushort ordinal = RoleIndex(peer.Expect(0,2).Body,created);
            peer.Send(6,Build(writer => { writer.Write(ordinal); writer.Write(Name(created.Name)); }));
            peer.Expect(1,6,Build(writer => { writer.Write((byte)1); writer.Write((byte)0); writer.Write(ordinal); }));
            peer.Expect(0,2);
            peer.Send(692,Name(created.Name)); peer.Expect(1,692,[1]);
            That(Roles() == originalRoles,"成功后只删除本条自建角色，保留既有角色身份和槽位："+scenario.Quest);

            string Active() => fixture.Snapshot(
                "SELECT slot,acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                created.Id,scenario.Quest);
        }

        string Roles() => fixture.Snapshot("SELECT id,slot,name FROM characters WHERE account=$p0 ORDER BY slot",fixture.Options.DevelopmentAccount);
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
        throw new InvalidDataException("本条测试角色不在返回名册中："+role.Name);
    }
}
