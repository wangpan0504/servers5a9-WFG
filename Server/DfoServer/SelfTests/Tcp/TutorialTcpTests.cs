using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using System.Buffers.Binary;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class TutorialTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        TutorialTests.RunGrowthBoundaries(SelfTestCatalogs.Growth(resources.Archive));
        var tutorials = resources.Tutorials;
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var scenes = resources.TutorialScenes;
        That(tutorials.Flows.Count() == 14 && tutorials.Get(0) is { DungeonId: 7115, IntroAfterTutorial: false, Destination.Town: 38, Destination.Area: 0 } &&
            tutorials.Get(15) is { DungeonId: 7145 } && tutorials.Get(9) is null && tutorials.Get(10) is null && tutorials.Rewards is [{ ItemId: 8474, Count: 1 }],
            "当前训练流程按CHR职业匹配14项资源，未声明职业不伪造流程，检查点38奖励来自当前PVF");
        foreach (var flow in tutorials.Flows)
        {
            var definition = dungeons.Get(checked((int)flow.DungeonId));
            var role = new Character(1, 0, "TutorialShape", 0, resources.Towns.Room, resources.Towns.InitialReturn)
                { Job = flow.Job, Tutorial = new(flow.DungeonId) };
            bool planned = DungeonEntryPlan.TryCreate(dungeons, role, flow.DungeonId, 0, 0, out var plan, out string reason);
            That(tutorials.Owns(flow.Job, flow.DungeonId, definition) && planned, $"训练拥有明确PVF身份和可规划迷宫: job={flow.Job},reason={reason}");
            var routeRooms = new List<DungeonMonsterPlan>();
            ushort next = 2;
            foreach (var map in definition.Mazes.Single().MapSpecification!.Concat(definition.Mazes.Single().LayeredMaps))
            {
                DungeonMonsterPlan actors;
                try { actors = DungeonMonsterPlan.FromRoom(scenes.Apply(rooms.Get(map, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: true)),
                    1, next, definition.BasisLevel, includeSpecial: false, playerTeam: plan!.PlayerTeam); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                { throw new InvalidDataException($"训练资源边界：job={flow.Job},dungeon={flow.DungeonId},map={map.Id},path={map.Path}", error); }
                That(actors.Monsters.Count + actors.SpecialObjects.Count <= 255, "训练房间Actor格式与正文上限: " + map.Id);
                routeRooms.Add(actors);
                if (actors.Monsters.Count > 0 || actors.SpecialObjects.Count > 0) next = checked((ushort)actors.NextObjectKey);
            }
            var route = new DungeonFixedRoute(routeRooms, plan!.Maze.StaticTopology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray());
            var authority = new NativeDungeonCombatAuthority();
            bool created = SinglePlayerDungeonRun.TryCreate(plan, 1, 1, route, authority, out var run, out string runReason);
            That(created && authority.BindTo(run!),
                $"全部训练职业可建立真实房间图: job={flow.Job},reason={runReason},layers={plan.Maze.LayeredMaps.Count}");
        }
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "tutorial"), resources: resources);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Create("TutorialTcp", withTutorial: true);
            That(fixture.Query("SELECT dungeon_id,completed,reward_received FROM character_tutorial_state WHERE character_id=$p0", peer.Id)
                is [[7115L, 0L, 0L]], "真实创建一次登记待训练状态，不给存量角色按等级补标记");
            var entry = peer.Select();
            That(entry[0].Body.Length == 234 && entry[0].Body[223] == 0 && entry[0].Body[224] == 0,
                "本版ACK4保持空教程完成列表，后续字段前移一字节，不改变初始城镇或其他初始化包");
            peer.Send(143, [0, 38, 0, 0, 0, 1]); peer.Expect(1, 143, [0, 19, 0]);
            peer.Send(15, [203, 27, 0, 0]); peer.Expect(1, 15, [1]); peer.Expect(0, 3);
            peer.Expect(0, 27, new byte[42]);
            peer.Send(143, [0, 30, 0, 0, 0, 1]); peer.Expect(1, 143, [1, 0]);
            peer.Send(16, Selection(7115));
            That(U32(peer.Expect(0, 28).Body) == 7115 && U32(peer.Expect(0, 29).Body, 14) == 53127,
                "教程C2S15/16从已选角色进入真实首图，不要求普通城镇wdm提供训练入口");
            FormalDungeonTcpTests.Load(peer);
            peer.Send(143, [0, 38, 0, 0, 0, 1]);
            peer.Expect(1, 143, [1, 1, 3, 0, 26, 33, 0, 0, 1, 0, 0, 0]);
            var reward = peer.Expect(0, 14).Body;
            That(U16(reward, 3) == 3 && U32(reward, 5) == 8474 && U32(reward, 9) == 1 &&
                fixture.Query("SELECT reward_received FROM character_tutorial_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "药剂与奖励标记同事务，实际UID物品位于首快捷格，不按总剩余量重复提示");
            string saved = fixture.Snapshot();
            peer.Send(143, [0, 38, 0, 0, 0, 1]); peer.Expect(1, 143, [1, 0]); peer.Barrier();
            That(fixture.Snapshot() == saved, "检查点重放不再次奖励、增加revision或抽随机结果");
            peer.Send(44, Build(writer => { writer.Write((ushort)3); writer.Write(123u); writer.Write(8474u); }));
            peer.Expect(1, 44, [1, 3, 0, 0, 123, 0, 0, 0, 26, 33, 0, 0]); peer.Expect(0, 14); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=8474", peer.Id) is [[0L]],
                "训练药剂通过本版CMD44消费，先ACK让客户端应用STK本地效果，再发绝对库存");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Barrier();
            That(fixture.Query("SELECT completed FROM character_tutorial_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "用户明确退出未完成训练后持久结束训练，与普通副本退出分开");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(entry[0].Body.Length == 235 && entry[0].Body[225] == 31,
                "退出后重选恢复普通完成标记，训练和药剂不会重复发放");
        }
        using (var peer = Open(fixture))
        {
            peer.Create("Training14", 14, withTutorial: true); peer.Select(1);
            peer.Send(15, Convert.FromHexString("D51B0000"));
            peer.Expect(1, 15, [1]); peer.Expect(0, 3); peer.Expect(0, 27, new byte[42]);
            peer.Send(16, Convert.FromHexString("D51B00000000000000FFFF0000000000000000000000000000"));
            peer.Send(143, [0, 30, 0, 0, 0, 1]);
            That(U32(peer.Expect(0, 28).Body) == 7125 && U32(peer.Expect(0, 29).Body, 14) == 57801,
                "女圣职者实收15/16进入训练首图，不能停留选图或以失败ACK代替地图初始化");
            peer.Expect(1, 143, [1, 0]);
            FormalDungeonTcpTests.Load(peer);
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Barrier();
            That(fixture.Query("SELECT completed FROM character_tutorial_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "女圣职者真实进入后明确退出仍完成训练，不通过入场失败伪造完成");
        }
        Battle(0, 2, false);
        Battle(8, 3, true);
        Battle(15, 4, false);
        await fixture.StopAsync();

        void Battle(byte job, ushort slot, bool cinematic)
        {
            var flow = tutorials.Get(job)!;
            var definition = dungeons.Get(checked((int)flow.DungeonId));
            using var peer = Open(fixture);
            peer.Create("Training" + job, job, withTutorial: true); peer.Select(slot);
            peer.Send(15, Build(writer => writer.Write(flow.DungeonId))); peer.Expect(1, 15, [1]); peer.Expect(0, 3); peer.Expect(0, 27);
            peer.Send(16, Selection(flow.DungeonId)); peer.Expect(0, 28); byte[] map = peer.Expect(0, 29).Body;
            var ordered = definition.Mazes.Single().MapSpecification!.OrderBy(row => row.Position.X).ThenBy(row => row.Position.Y).ToArray();
            bool cleared = false, cinematicCompleted = false;
            foreach (var expectedMap in ordered)
            {
                That(U32(map, 14) == expectedMap.Id, "训练真实切图顺序: job=" + job);
                FormalDungeonTcpTests.Load(peer);
                var room = scenes.Apply(rooms.Get(expectedMap, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: true));
                var actors = DungeonMonsterPlan.FromRoom(room, peer.Id, map[18] == 0 ? (ushort)1 : U16(map, 25), definition.BasisLevel,
                    includeSpecial: false, playerTeam: 0);
                if (job == 15 && expectedMap.Id == 70577)
                {
                    That(actors.Monsters.Where(actor => actor.IsBlocking).Select(actor => actor.MonsterId).Order()
                            .SequenceEqual(new[] { 70229, 70229, 70235 }) &&
                        actors.Monsters.Single(actor => actor.MonsterId == 70216) is { IsScriptedRetirement: true, IsBlocking: false },
                        "枪剑士第二图保留两只同模板怪的独立身份，脚本退场演员不充当战斗阻挡");
                    That(map[18] == 4 && actors.Monsters.All(actor =>
                    {
                        int offset = 19 + actor.SourceOrdinal * 22;
                        return U16(map, offset + 6) == actor.ObjectKey && U32(map, offset + 8) == actor.MonsterId &&
                            map[offset + 21] == (actor.MonsterId == 70216 ? 1 : 0);
                    }), "枪剑士第二图实收生成行保留四个原身份，只排除70216的客户端清房阻挡，不排除三只战斗怪");
                    MoveBeforeClear();
                }
                if (cinematic && expectedMap == ordered[^1])
                {
                    var movie = room.TutorialCinematics.Where(row => row.DestroyTargets.Count > 0).MaxBy(row => row.MonsterActors.Count)!;
                    var target = actors.Monsters.First(row => movie.DestroyTargets.Contains(row.SourceOrdinal) && row.AiCharacter is null);
                    byte[] check = new byte[39];
                    BinaryPrimitives.WriteUInt16LittleEndian(check, peer.Id); BinaryPrimitives.WriteUInt16LittleEndian(check.AsSpan(2), target.ObjectKey);
                    BinaryPrimitives.WriteUInt32LittleEndian(check.AsSpan(13), checked((uint)target.CandidateLevel));
                    BinaryPrimitives.WriteUInt64LittleEndian(check.AsSpan(17), 504);
                    var outside = actors.Monsters.Where(row => row.IsBlocking && !movie.MonsterActors.Contains(row.SourceOrdinal)).ToArray();
                    if (outside.Length > 0)
                    {
                        peer.Send(117, check); Drain();
                        That(!cleared && fixture.Query("SELECT completed FROM character_tutorial_state WHERE character_id=$p0", peer.Id) is [[0L]],
                            "CMT覆盖不完整时拒绝提前清图，不把另一电影或未死亡对象并入证据");
                        foreach (var actor in outside) Kill(actor);
                    }
                    peer.Send(117, check); Drain(); cinematicCompleted = cleared;
                }
                else foreach (var actor in actors.Monsters.Where(row => row.IsBlocking && row.AiCharacter is null))
                {
                    Kill(actor);
                    if (job == 15 && expectedMap.Id == 70577 && actor == actors.Monsters.First(row => row.IsBlocking))
                        MoveBeforeClear();
                }
                if (expectedMap != ordered[^1])
                {
                    var next = ordered[Array.IndexOf(ordered, expectedMap) + 1];
                    var move = new byte[100]; move[0] = checked((byte)next.Position.X); move[1] = checked((byte)next.Position.Y);
                    peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                }

                void MoveBeforeClear()
                {
                    // 同模板第一只死亡不能替代第二只；无效过图后仍须在原房间处理真实死亡。
                    var move = new byte[100]; move[0] = 2;
                    peer.Send(45, move); peer.Barrier();
                    That(fixture.Query("SELECT completed FROM character_tutorial_state WHERE character_id=$p0", peer.Id) is [[0L]],
                        "枪剑士第二图未清怪不提前完成训练或进入第三图");
                }
            }
            That(cleared && (!cinematic || cinematicCompleted) && fixture.Query("SELECT completed FROM character_tutorial_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "真实训练终点完成并持久标记；剧情收尾不发假死亡和经验");
            That(fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id) is [[> 0L]],
                "训练死亡与新版普通掉落账本共享原成长事实，完整外键有效且不重复增加经验");
            peer.Send(46, DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 90));
            peer.Expect(0, 34); peer.Expect(0, 37); Drain();
            That(fixture.Query("SELECT count(*) FROM character_tutorial_receipts WHERE character_id=$p0 AND kind='settlement'", peer.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0", peer.Id) is [[0L]],
                "训练结算独立原子检查点，不借用普通结算账本或付费免费翻牌奖励");
            peer.Send(42); peer.Expect(1, 42); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Barrier();

            void Kill(DungeonPlannedMonster actor)
            {
                peer.Send(39, FormalDungeonTcpTests.Death(actor.ObjectKey, peer.Id, checked((byte)actor.CandidateLevel)));
                peer.Expect(0, 38); peer.Expect(0, 37); Drain();
            }

            void Drain()
            {
                peer.SendBarrier();
                for (int count = 0; count < 16; count++)
                {
                    var packet = peer.Read();
                    if (packet.Kind == 1 && packet.Opcode == 1347) return;
                    That(packet.Kind == 0 && packet.Opcode is 2 or 19 or 21 or 35 or 38 or 115 or 31,
                        "训练成长、完整Player及清场后续包没有无关成功ACK: " + packet.Opcode);
                    if (packet.Opcode == 31) { That(packet.Body.Length == 0, "S5 NOTI31空正文不移植旧版零字节"); cleared = true; }
                }
                throw new InvalidOperationException("训练后续帧超过有界预算");
            }
        }
    }
    static byte[] Selection(uint dungeonId) => Build(writer =>
    {
        writer.Write(dungeonId); writer.Write((byte)0); writer.Write((ushort)0); writer.Write((byte)0);
        writer.Write((byte)0); writer.Write(ushort.MaxValue); writer.Write(0u); writer.Write((byte)0);
        writer.Write(0u); writer.Write((byte)0); writer.Write(0u);
    });
}
