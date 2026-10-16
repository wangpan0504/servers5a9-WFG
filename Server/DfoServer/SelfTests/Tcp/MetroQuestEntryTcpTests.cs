using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class MetroQuestEntryTcpTests
{
    // 共用quests组的隔离服务；入口等级与前置可准备，目标接取、回答、会面和移动全部走wire。
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        int checks = Count;
        var dungeons = DungeonCatalog.Load(resources.Archive);
        var rooms = DungeonRoomCatalog.Load(resources.Archive);
        VerifyFaction(0, 3868, 3871, 3873);
        VerifyFaction(11, 3869, 3872, 3874);
        VerifySequential(3971, [(3972, 455)], 3973, 310, 88);
        VerifySequential(3547, [(3566, 59)], 3567, 84, 69);
        VerifySequential(3540, [(3564, 57)], 3565, 83, 68);
        VerifySequential(3521, [(3555, 59), (3556, 58)], 3557, 81, 64);
        VerifyYoungGizelEntry();
        VerifyMovement(7958, 311, 95, new(2, 0), 655666, 655667);
        VerifyMovement(3969, 311, 88, new(3, 0), 76886, null);
        VerifyGustDropChain();
        VerifyGustClearChain();
        VerifyBellFeathers();
        Console.WriteLine($"PASS {Count - checks} 任务代表TCP：真实OR回答、父子阶段、3640首子入场、层/warp、3988→3990清图交付、3987→3992材料及3993→3995→3998掉落（不代表完整剧情通关）");

        void VerifyGustClearChain()
        {
            // 只准备3987及合法入口；三个清图任务依次真实交付。74670不报告死亡，不把ACT计时当作通关事实。
            const string account = "metro-gust-clear";
            var done = new HashSet<int> { 3987 };
            var entrance = Entrance(3988, 313, done);
            That(!done.Overlaps([3988, 3989, 3990, 3991]), "连续清图链不预填目标或后继完成位");
            var created = Create(account, "MetroClear", 0, 89, done, entrance);
            var oldDeaths = new List<byte[]>();
            using (var peer = Open(fixture, account))
            {
                Select(peer, created);
                foreach (var scenario in new (ushort Quest, int FirstMap, int Boss, int[] PassiveCounts, string[] Groups)[]
                {
                    (3988, 77000, 74655, [37, 5, 28, 10, 14, 19, 9, 30, 26], ["65723:5,65729:3", "65730:3,65734:2", "65710:6,65712:2", "65710:6",
                        "65723:4,65730:3", "65710:4,65729:3", "65711:3,65712:2,65735:6", "65710:4,65712:3", "65735:5,74655:1,74670:1"]),
                    (3989, 77010, 74655, [44, 19, 19, 10, 17, 24, 39, 11, 20], ["65712:3,65729:4", "65710:4,65712:3", "65708:3,65710:4", "65708:4,65730:3",
                        "65708:3,65712:4", "65710:9", "65710:4,65712:2", "65730:5", "65735:5,74655:1"]),
                    (3990, 77020, 74657, [16, 20, 11, 11, 26, 17, 33, 14, 31], ["65710:3,65723:5", "65710:3,65712:3", "65708:3,65711:3", "65730:6",
                        "65729:3,65735:4", "65723:4,65735:3", "65710:3,65735:5", "65731:1,65732:1,65733:2,65734:1", "74657:1"])
                })
                {
                    var quest = resources.Quests.Get(scenario.Quest);
                    That(quest.MinimumLevel == 89 && quest.Prerequisites.SequenceEqual([scenario.Quest - 1]) &&
                        quest.Condition == "[clear map]" && quest.SubType == -1 && quest.IntData.SequenceEqual([scenario.FirstMap + 8]) &&
                        quest.RewardType == "[item]" && quest.Rewards is [{ Type: 0, Value: 0 }, { Type: 0, Value: 0 }],
                        "三个目标沿真实前置、末图和零物品奖励声明，不借寻物条件：" + scenario.Quest);
                    RejectAccept(peer, checked((ushort)(scenario.Quest + 1)));
                    Accept(peer, scenario.Quest, 1);
                    string acceptance = Identity(peer.Id, scenario.Quest);
                    long slot = (long)fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest).Single()[0];
                    var maze = dungeons.Get(313).Mazes.Single(row => row.ConnectsQuest(scenario.Quest));
                    That(maze.MapSpecification?.Count == 9 && maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 &&
                        maze.StoryStages.Count == 0 && maze.ClearConditions.Count == 0, "三条原九房路线没有额外层或物件清图条件");
                    uint next = checked((uint)(long)fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]);
                    long previousAttempt = (long)fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0];
                    var plans = Plans(maze, peer.Id, next, dungeons.Get(313).BasisLevel);
                    var map = Enter(peer, scenario.Quest, 313, scenario.Quest, maze);
                    long attempt = (long)fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0];
                    var path = QuestStoryRouteTests.Path(maze.StaticTopology, maze.Starts.Single(), maze.Bosses.Single());
                    That(attempt == previousAttempt + 1 && path.Length == 9 && path.Select(point =>
                        maze.MapSpecification!.Single(row => row.Position == point).Id).SequenceEqual(Enumerable.Range(scenario.FirstMap, 9)),
                        "实际16只建一次尝试，按原门墙逐房经过九图，不直接跳末房");
                    int enabled = 0, bossChecks = 0, killed = 0;
                    ushort? confirmedBoss = null;
                    var emptyDeaths = new HashSet<ushort>();
                    var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99);
                    for (int at = 0; at < path.Length; at++)
                    {
                        var plan = plans.Single(row => row.Room.Map.Position == path[at]);
                        Actors(peer, map, plan, scenario.Quest);
                        That(string.Join(",", plan.Monsters.Where(row => row.AiCharacter is null).GroupBy(row => row.MonsterId).OrderBy(group => group.Key)
                                .Select(group => group.Key + ":" + group.Count())) == scenario.Groups[at] &&
                            plan.Monsters.Where(row => row.AiCharacter is null).All(row => row.Source is { SpecialChild: null, ScriptedRetirement: false } &&
                                row.CandidateLevel == 91 && row.CanReportDeath && row.IsBlocking && !row.IsFriendly && !row.IsScriptedBoss) &&
                            plan.Monsters.Where(row => row.AiCharacter is not null).All(row => row.MonsterId == 16802 &&
                                row.CandidateLevel == 85 && row.IsFriendly && !row.CanReportDeath) &&
                            plan.Monsters.Count(row => row.AiCharacter is not null) ==
                                (scenario.Quest == 3989 && at == 8 || scenario.Quest == 3990 && at == 0 ? 1 : 0) &&
                            plan.Room.PassiveObjects.Count == scenario.PassiveCounts[at] &&
                            plan.SpecialObjects.Count == (at == 8 ? 3 : 0) && plan.SpecialObjects.All(row => row.Source.ObjectId == 109000302),
                            "逐图核对原怪物分组、等级阵营及末房三父对象，不漏掉高坐标演员");
                        FormalDungeonTcpTests.Load(peer); Drain();
                        if (at == 0)
                        {
                            string before = Assets();
                            foreach (var old in oldDeaths) { peer.Send(39, old); Drain(); }
                            That(Assets() == before && Identity(peer.Id, scenario.Quest) == acceptance,
                                "新实例拒绝前一通关Boss旧key，保留原接取和全部资产");
                            RejectFinish(); peer.Send(46, report); Drain();
                            That(Assets() == before && enabled == 0, "加载及提前46不能生成清图、奖励或结算许可");
                        }
                        if (at < 8)
                        {
                            foreach (var actor in plan.Monsters.Where(row => row.CanReportDeath)) Kill(actor);
                            That(enabled == 0 && bossChecks == 0 && Scalar(peer.Id, scenario.Quest) == 1 &&
                                Identity(peer.Id, scenario.Quest) == acceptance, "中途真实普通死亡不提前达成末图任务");
                            map = MoveMap(peer, path[at + 1], 0);
                        }
                    }
                    var terminal = plans.Single(row => row.Room.Map.Id == scenario.FirstMap + 8);
                    var boss = terminal.Monsters.Single(row => row.IsBoss);
                    var culled = terminal.Monsters.Where(row => row.IsBlocking && !row.IsBoss).ToArray();
                    That(boss.MonsterId == scenario.Boss && boss.SpawnType == 3 &&
                        (scenario.Quest != 3988 || culled.Single(row => row.MonsterId == 74670).Source!.RawValues
                            .SequenceEqual([74670, 1, 0, 1183, 10319, 10000, 1, 1])), "末击只选真实Boss，74670保留原生成行并始终不报死亡");
                    RejectFinish();
                    That(enabled == 0 && bossChecks == 0 && Scalar(peer.Id, scenario.Quest) == 1 &&
                        Identity(peer.Id, scenario.Quest) == acceptance, "终房加载仍未许可结算或推进原接取");
                    string beforeBoss = Assets(); peer.Send(46, report); Drain();
                    That(Assets() == beforeBoss && enabled == 0 && bossChecks == 0, "终房Boss存活时46仍不能提前结算");
                    emptyDeaths.Clear();
                    byte[] bossDeath = Kill(boss); oldDeaths.Add(bossDeath);
                    That(enabled == 1 && bossChecks == 1 && confirmedBoss == boss.ObjectKey && Scalar(peer.Id, scenario.Quest) == 0 &&
                        Identity(peer.Id, scenario.Quest) == acceptance && culled.All(row => emptyDeaths.Contains(row.ObjectKey)) &&
                        terminal.Monsters.Where(row => row.IsFriendly).All(row => !emptyDeaths.Contains(row.ObjectKey)),
                        "Boss真实死亡生成唯一结算许可和原接取进度，余演员逐个收到空掉落38清场");
                    That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND task_attempt=$p1", peer.Id, attempt)
                            is [[long deaths]] && deaths == killed &&
                        culled.All(row => fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND task_attempt=$p1 AND monster_key=$p2",
                            peer.Id, attempt, row.ObjectKey) is [[0L]]), "无奖励清场不制造74670或其它余怪的死亡、经验及掉落源事件");
                    var clear = fixture.Query("""
                        SELECT c.acceptance_id,c.quest_slot,c.epoch,g.epoch,c.instance_id,g.instance_id,c.task_attempt,g.task_attempt,c.revision,g.revision
                        FROM character_quest_clear_receipts c JOIN character_dungeon_growth_receipts g
                          ON g.character_id=c.character_id AND g.event_id=c.growth_event_id
                        WHERE c.character_id=$p0 AND c.quest_id=$p1 AND c.map_id=$p2 AND g.monster_id=$p3 AND g.quest_id=$p1
                        """, peer.Id, scenario.Quest, scenario.FirstMap + 8, scenario.Boss);
                    That(clear is [[string original, long savedSlot, long epoch, long growthEpoch, string instance, string growthInstance,
                            long savedAttempt, long growthAttempt, long revision, long growthRevision]] && original == acceptance && savedSlot == slot &&
                        epoch == growthEpoch + 1 && instance == growthInstance && savedAttempt == attempt && savedAttempt == growthAttempt && revision == growthRevision,
                        "清图与真实Boss成长共事务，绑定原Acceptance、槽、实例、attempt与下一epoch");
                    string unchanged = Assets(); peer.Send(39, bossDeath); Drain();
                    That(Assets() == unchanged && enabled == 1 && bossChecks == 1, "重复末击不再产生死亡、掉落、清图或许可");
                    if (culled.FirstOrDefault(row => row.MonsterId == 65735) is { } late)
                    {
                        peer.Send(39, FormalDungeonTcpTests.Death(late.ObjectKey, peer.Id, checked((byte)late.CandidateLevel))); Drain();
                        That(Assets() == unchanged, "已清场普通怪的迟到39拒绝新增成长或掉落，不向74670构造报告");
                    }
                    peer.Send(46, report); Drain();
                    That(fixture.Query("SELECT map_id,instance_id,task_attempt,epoch FROM character_dungeon_settlement_receipts WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest) is [[long settledMap, string settledInstance, long settledAttempt, long settledEpoch]] &&
                        settledMap == scenario.FirstMap + 8 && settledInstance == (string)clear[0][4] && settledAttempt == attempt && settledEpoch == (long)clear[0][2],
                        "实际46结算同一末图、实例、尝试和清图epoch");
                    bool nextCandidate = false;
                    Finish(peer, scenario.Quest, 3, packet =>
                    {
                        if (packet.Opcode == 21) nextCandidate = QuestLifecycleTcpTests.List(packet.Body, checked((byte)(long)fixture.Query(
                            "SELECT level FROM characters WHERE id=$p0", peer.Id).Single()[0])).Contains(scenario.Quest + 1);
                    });
                    That(nextCandidate && fixture.Query("SELECT acceptance_id,consumed FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest) is [[string completedAcceptance, "[]"]] && completedAcceptance == acceptance,
                        "真实34关闭原接取且不虚耗物品，提交后的21立即开放下一项");
                    RejectFinish(); unchanged = Assets(); peer.Send(39, bossDeath); Drain();
                    That(Assets() == unchanged, "34之后迟到Boss报告不恢复任务或重复奖励");
                    Return(peer);

                    byte[] Kill(DungeonPlannedMonster actor)
                    {
                        That(actor.MonsterId != 74670, "74670不作为真实击杀输入");
                        var death = FormalDungeonTcpTests.Death(actor.ObjectKey, peer.Id, checked((byte)actor.CandidateLevel));
                        peer.Send(39, death); Drain(); killed++; return death;
                    }
                    void Drain() => drain(peer, packet =>
                    {
                        if (packet.Opcode == 31) { Bytes(packet.Body, [], "清图链许可使用完整空31"); enabled++; }
                        if (packet.Opcode == 115)
                        {
                            That(packet.Body.Length == 4 && packet.Body[0] == 1 && packet.Body[1] == 1, "115保持实际Boss确认格式");
                            confirmedBoss = U16(packet.Body, 2); bossChecks++;
                        }
                        if (packet.Opcode == 38 && U16(packet.Body, 2) == 0)
                        {
                            Bytes(packet.Body, [(byte)U16(packet.Body), (byte)(U16(packet.Body) >> 8), 0, 0, 0, 0, 255, 0, 0],
                                "空掉落38保留完整九字节正文及死亡尾部");
                            emptyDeaths.Add(U16(packet.Body));
                        }
                    });
                    void RejectFinish()
                    {
                        string before = Assets(); peer.Send(34, [34, 0, (byte)scenario.Quest, (byte)(scenario.Quest >> 8), 255, 255, 1, 0, 255, 255]);
                        peer.Expect(1, 34, [0, 19, 0]); Drain();
                        That(Assets() == before, "提前或重复34拒绝且全部资产及凭据保持");
                    }
                }
            }
            using (var peer = Open(fixture, account))
            {
                Select(peer, created); peer.Barrier();
                That(Active(peer.Id) == "[]" && new[] { 3988, 3989, 3990 }.All(id => peer.CompletedQuestHistory[4 + id] == 1) &&
                    peer.CompletedQuestHistory[4 + 3991] == 0 && fixture.Query(
                        "SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id", peer.Id) is [[3988L], [3989L], [3990L]],
                    "重连恢复三次真实交付，3991仅开放候选，不冒充完成演出");
                Cleanup(peer, created, account);
            }
            string Assets() => State(created.Id) + DungeonState(created.Id) +
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", created.Id) +
                fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_quest_drop_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0 ORDER BY quest_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0 ORDER BY instance_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_loot_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_drop_batches WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_drop_objects WHERE character_id=$p0 ORDER BY ground_key", created.Id) +
                fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind", account);
        }

        void VerifyBellFeathers()
        {
            // 3991仅作为隔离前置，不代表连续游玩3988..3991；钟必须由本角色真实3987领奖取得，不预填材料。
            const string account = "metro-bell-feather";
            var done = new HashSet<int> { 3986, 3991 };
            var fearEntrance = Entrance(3987, 312, done);
            var gustEntrance = Entrance(3992, 313, done);
            That(!done.Overlaps([3987, 3992]), "钟与羽毛链不准备两个目标完成位");
            var source = resources.Quests.Get(3987); var target = resources.Quests.Get(3992);
            var drops = target.Definition!.Drops;
            That(source.MinimumLevel == 89 && source.Prerequisites.SequenceEqual([3986]) &&
                source.Condition == "[look cinematic]" && source.IntData.SequenceEqual([2845]) && source.CinematicMapId == 76997 &&
                source.RewardType == "[item]" && source.Rewards is [{ Type: 0, Value: 10164808 }, { Type: 0, Value: 1 }] &&
                target.MinimumLevel == 89 && target.Prerequisites.SequenceEqual([3991]) && target.Condition == "[seeking]" &&
                target.IntData.SequenceEqual([10164808, 1, 10164806, 15]) && target.RewardType == "[item]" &&
                target.Rewards is [{ Type: 0, Value: 10164803 }, { Type: 0, Value: 1 }] && drops.Length == 2 &&
                drops.All(row => row.EnemyType == 1 && row.DungeonId == 313 && row.Difficulty == -1 &&
                    row.ItemId == 10164806 && row.Rate == 100 && row.MaximumHeld == 15) &&
                drops.Any(row => row.TemplateId == 65729 && row.Count == 1) &&
                drops.Any(row => row.TemplateId == 74656 && row.Count == 2),
                "真实3987奖励钟，3992消耗钟及十五羽毛后奖励服从之钟，display send不是发放声明");
            var created = Create(account, "MetroBell", 0, 89, done, fearEntrance);
            That(Materials(10164808).Length == 0 && Materials(10164806).Length == 0 && Materials(10164803).Length == 0,
                "新角色没有钟、羽毛或目标奖励");
            var oldDeaths = new List<byte[]>();
            long bellUid = 0, rewardUid = 0;
            using (var peer = Open(fixture, account))
            {
                Select(peer, created);
                foreach (var scenario in new (ushort Quest, uint Dungeon, int Rooms, int FirstMap)[]
                    { (3987, 312, 8, 76990), (3992, 313, 10, 77040) })
                {
                    bool bell = scenario.Quest == 3987;
                    int enabled = 0, bossChecks = 0, killed = 0, feathers = 0;
                    TravelTo(peer, bell ? fearEntrance : gustEntrance);
                    Accept(peer, scenario.Quest, 1);
                    string acceptance = Identity(peer.Id, scenario.Quest);
                    long slot = (long)fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest).Single()[0];
                    RejectFinish();
                    var maze = dungeons.Get(checked((int)scenario.Dungeon)).Mazes.Single(row => row.ConnectsQuest(scenario.Quest));
                    That(maze.MapSpecification?.Count == scenario.Rooms && maze.LayeredMaps.Count == 0 &&
                        maze.StoryStages.Count == 0 && (bell
                            ? maze.ClearConditions is [{ Type: "destroy object", TargetId: 85194, Count: 1 }]
                            : maze.ClearConditions.Count == 0), "两条真实路线保留各自房数和物件终末规则");
                    uint next = checked((uint)(long)fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]);
                    long previousAttempt = (long)fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0];
                    var plans = Plans(maze, peer.Id, next, dungeons.Get(checked((int)scenario.Dungeon)).BasisLevel);
                    var map = Enter(peer, scenario.Quest, scenario.Dungeon, bell ? (ushort)0 : scenario.Quest, maze);
                    long attempt = (long)fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0];
                    That(attempt == previousAttempt + 1, "每条16只建立一次独立尝试");
                    var path = QuestStoryRouteTests.Path(maze.StaticTopology, maze.Starts.Single(), maze.Bosses.Single());
                    That(path.Length == scenario.Rooms && path.Select(point => maze.MapSpecification!.Single(row => row.Position == point).Id)
                        .SequenceEqual(Enumerable.Range(scenario.FirstMap, scenario.Rooms)), "真实门墙路径完整经过八房或十房，不跳图到终点");
                    string[] normalGroups = ["65712:3,65729:4", "65710:3,65712:4", "65708:3,65712:4", "65708:4,65731:3",
                        "65712:4,65729:3", "65731:1,65732:1,65733:2,65734:1", "65729:6", "65731:3,65735:3",
                        "65729:3,65735:4", "65729:3,65735:4"];
                    int[] passiveCounts = [27, 17, 15, 11, 16, 24, 31, 20, 18, 35];
                    for (int at = 0; at < path.Length; at++)
                    {
                        var plan = plans.Single(row => row.Room.Map.Position == path[at]);
                        Actors(peer, map, plan, scenario.Quest);
                        if (!bell)
                            That(string.Join(",", plan.Monsters.Where(row => row.AiCharacter is null && !row.IsBoss)
                                    .GroupBy(row => row.MonsterId).OrderBy(group => group.Key).Select(group => group.Key + ":" + group.Count())) == normalGroups[at] &&
                                plan.Monsters.Where(row => row.AiCharacter is null && !row.IsBoss).All(row => row is
                                    { CandidateLevel: 91, SpawnType: 0, IsBlocking: true, IsFriendly: false, CanReportDeath: true, IsScriptedBoss: false }) &&
                                plan.Room.PassiveObjects.Count == passiveCounts[at] && plan.SpecialObjects.Count == (at == 9 ? 3 : 0) &&
                                plan.SpecialObjects.All(row => row.Source.ObjectId == 109000302) &&
                                plan.Monsters.Where(row => row.AiCharacter is not null).All(row => at == 0 && row.MonsterId == 16802 &&
                                    row.CandidateLevel == 85 && row.IsFriendly && !row.CanReportDeath) &&
                                plan.Monsters.Count(row => row.AiCharacter is not null) == (at == 0 ? 1 : 0),
                                "3992十房保留普通怪、原passive、三泰拉石父对象及85级友方16802");
                        FormalDungeonTcpTests.Load(peer); Drain();
                        if (!bell && at == 0)
                        {
                            string before = Assets();
                            foreach (var old in oldDeaths) { peer.Send(39, old); Drain(); }
                            That(Assets() == before && Identity(peer.Id, 3992) == acceptance,
                                "3992新实例拒绝前次3987的物件及Boss39，不改新接取或原钟库存");
                        }
                        foreach (var actor in plan.Monsters.Where(row => row.CanReportDeath && !row.IsBoss))
                        {
                            var body = Kill(actor);
                            if (!bell)
                            {
                                if (actor.MonsterId == 65729) feathers = Math.Min(15, feathers + 1);
                                That(Materials(10164806).Sum(row => (long)row[1]) == feathers &&
                                    Materials(10164808) is [[long uid, 1L]] && uid == bellUid &&
                                    Scalar(peer.Id, 3992) == (feathers == 15 ? 0u : 1u) && Identity(peer.Id, 3992) == acceptance && enabled == 0,
                                    "只有真实65729逐个产生羽毛，持有上限十五且原钟UID、接取身份不变");
                                if (feathers == 1 && actor.MonsterId == 65729)
                                {
                                    oldDeaths.Add(body); string before = Assets();
                                    peer.Send(39, body); Drain();
                                    That(Assets() == before, "重复首只羽毛来源39不再增长材料、经验或掉落账本");
                                    RejectFinish();
                                }
                            }
                        }
                        That(enabled == 0 && Identity(peer.Id, scenario.Quest) == acceptance,
                            "加载和全部普通怪死亡不提前许可终点结算或改变原接取");
                        if (at + 1 < path.Length) map = MoveMap(peer, path[at + 1], 0);
                    }
                    var terminal = plans.Single(row => row.Room.Map.Id == (bell ? 76997 : 77049));
                    var boss = terminal.Monsters.Single(row => row.MonsterId == (bell ? 75099 : 74656));
                    That(boss is { IsBoss: true, CanReportDeath: true, IsBlocking: true, IsFriendly: false } && boss.IsScriptedBoss == bell,
                        "3987只报告真实可报告75099，3992只报告普通Boss74656，不构造额外演员");
                    if (!bell) That(boss is { CandidateLevel: 91, SpawnType: 3 }, "3992 Boss沿D313 basis91创建，不误用MOB自身89级");
                    byte[] bossDeath;
                    if (bell)
                    {
                        That(terminal.Room.PassiveObjects.Count(row => row.ObjectId == 85194) == 1 &&
                            terminal.Room.PassiveObjects[15].ObjectId == 85194 &&
                            terminal.Monsters.Single(row => row.AiCharacter?.Id == 17002) is { IsFriendly: true, CanReportDeath: false },
                            "3987保留唯一85194原ordinal15与友方17002");
                        byte[] passive = QuestPassiveClearTcpTests.PassiveDeath(85194);
                        string before = Assets(); peer.Send(39, passive); Drain();
                        That(Assets() == before && enabled == 0, "75099未退场时85194不能提前提交场景清图");
                        bossDeath = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(bossDeath, boss.ObjectKey);
                        BinaryPrimitives.WriteUInt16LittleEndian(bossDeath.AsSpan(4), peer.Id); bossDeath[31] = 0x22;
                        FormalDungeonTcpTests.SealDeath(bossDeath);
                        peer.Send(39, bossDeath); Drain(); killed++;
                        That(enabled == 0 && Scalar(peer.Id, 3987) == 1 && fixture.Query(
                            "SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0", peer.Id) is [[0L]],
                            "明确合成的75099脚本39仍不能替代85194或2845终末33");
                        before = Assets(); peer.Send(39, bossDeath); Drain();
                        That(Assets() == before && enabled == 0, "重复75099脚本39不代替物件清图或重发成长");
                        peer.Send(39, passive); Drain(); oldDeaths.Add(passive);
                        var clear = fixture.Query("""
                            SELECT c.source_type,c.map_id,c.template_id,c.source_ordinal,c.quest_id,c.before_epoch,c.epoch,
                              c.instance_id,c.task_attempt,length(c.death_events)/16,l.acceptance_id,l.revision,c.revision
                            FROM character_dungeon_scene_clear_receipts c JOIN character_quest_lifecycle_receipts l
                              ON l.character_id=c.character_id AND l.operation_id=c.operation_id WHERE c.character_id=$p0
                            """, peer.Id);
                        That(clear is [["PassiveObject", 76997L, 85194L, 15L, 0L, long beforeEpoch, long epoch, string instance,
                            long savedAttempt, long deaths, string original, long lifecycleRevision, long sceneRevision]] &&
                            beforeEpoch + 1 == epoch && Guid.TryParseExact(instance, "N", out _) && savedAttempt == attempt && deaths == killed &&
                            original == acceptance && lifecycleRevision == sceneRevision && enabled == 1 && bossChecks == 1 && Scalar(peer.Id, 3987) == 1,
                            "52B合成物件39保留普通0实例、原Acceptance、attempt及完整死亡集合的独立PassiveSource");
                        before = Assets(); peer.Send(39, passive); Drain();
                        That(Assets() == before && enabled == 1, "重复85194报告不再提交场景清图或发布31");
                        RejectFinish(); Condition(peer, 3987, 0, 0);
                        before = Assets(); Condition(peer, 3987, 0, 0);
                        That(Assets() == before && Identity(peer.Id, 3987) == acceptance, "重复2845终末33只恢复原接取已存进度");
                        peer.Send(46, DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99)); Drain();
                        That(fixture.Query("""
                            SELECT quest_id,map_id,instance_id,task_attempt,epoch FROM character_dungeon_settlement_receipts
                            WHERE character_id=$p0
                            """, peer.Id) is [[0L, 76997L, string settledInstance, long settledAttempt, long settledEpoch]] &&
                            settledInstance == (string)clear[0][7] && settledAttempt == attempt && settledEpoch == (long)clear[0][6],
                            "3987真实46沿同一普通0场景清图实例、attempt及epoch提交结算");
                        Finish(peer, 3987, 65);
                        bellUid = Reward(3987, 10164808, acceptance, 65);
                        That(Materials(10164806).Length == 0 && Materials(10164803).Length == 0 && fixture.Query(
                            "SELECT consumed FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3987", peer.Id) is [["[]"]],
                            "真实3987的34仅发原钟，不预先发羽毛、服从之钟或消费材料");
                    }
                    else
                    {
                        That(feathers == 15 && Materials(10164806).Sum(row => (long)row[1]) == 15 && Scalar(peer.Id, 3992) == 0,
                            "3992普通路线在Boss前已经足额，不假定必须先清图才能交付寻物任务");
                        bossDeath = Kill(boss);
                        That(enabled == 1 && bossChecks == 1 && Materials(10164806).Sum(row => (long)row[1]) == 15,
                            "74656合法末击可清图，已满羽毛不因Boss两件声明超过十五；2867自然发包仍未验");
                        var grants = fixture.Query("""
                            SELECT sum(json_extract(o.value,'$.Granted')),count(*),min(d.revision=g.revision),
                              min(g.quest_id=3992),min(g.task_attempt=$p1),min(json_extract(o.value,'$.AcceptanceId')=$p2),
                              min(json_extract(d.plan_snapshot,'$.EnemyType')=1),count(DISTINCT g.instance_id),
                              min(g.monster_id=65729),min(json_extract(o.value,'$.Rolled')=1),min(json_extract(o.value,'$.Error')=0),
                              min(json_extract(d.changed_items,'$[0].Core.ItemId')=10164806),
                              min(json_extract(d.changed_items,'$[0].Uid') IN (SELECT i.item_uid FROM character_inventory_slots s
                                JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 AND s.list_type=0
                                  AND json_extract(i.item_core,'$.ItemId')=10164806))
                            FROM character_quest_drop_receipts d JOIN character_dungeon_growth_receipts g
                              ON g.character_id=d.character_id AND g.event_id=d.event_id,
                              json_each(d.plan_snapshot,'$.Outcomes') o
                            WHERE d.character_id=$p0 AND json_extract(o.value,'$.QuestId')=3992
                            """, peer.Id, attempt, Guid.ParseExact(acceptance, "N").ToString());
                        That(grants.Length == 1 && grants[0][0] is 15L && grants[0][1] is 15L && grants[0].Skip(2).All(value => value is 1L),
                            "十五次普通死亡掉落与同一实例成长共事务revision，逐次绑定原Acceptance、attempt及真实羽毛UID");
                        var feathersBefore = Materials(10164806);
                        Finish(peer, 3992, 0);
                        rewardUid = Reward(3992, 10164803, acceptance, 0);
                        using var consumed = JsonDocument.Parse((string)fixture.Query(
                            "SELECT consumed FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3992", peer.Id).Single()[0]);
                        var rows = consumed.RootElement.EnumerateArray().ToArray();
                        That(rows.Length == feathersBefore.Length + 1 && rows.Count(row => row.GetProperty("Uid").GetInt64() == bellUid &&
                                row.GetProperty("ItemId").GetInt32() == 10164808 && row.GetProperty("Count").GetInt32() == 1) == 1 &&
                            feathersBefore.All(item => rows.Count(row => row.GetProperty("Uid").GetInt64() == (long)item[0] &&
                                row.GetProperty("ItemId").GetInt32() == 10164806 && row.GetProperty("Count").GetInt64() == (long)item[1]) == 1) &&
                            Materials(10164808).Length == 0 && Materials(10164806).Length == 0,
                            "3992真实34一次消费3987原钟UID及全部十五羽毛实际UID，不留下旧材料复用");
                    }
                    oldDeaths.Add(bossDeath); RejectFinish();
                    string completed = Assets(); peer.Send(39, bossDeath); Drain();
                    That(Assets() == completed && IdentityClosed(), "34后迟到Boss39不恢复材料、活动槽或重复资产");
                    Return(peer);

                    byte[] Kill(DungeonPlannedMonster actor)
                    {
                        That(actor.AiCharacter is null && actor.Source is { SpecialChild: null, ScriptedRetirement: false } &&
                            actor.CanReportDeath && !actor.IsScriptedBoss, "普通合成39只使用当前真实可报告敌人的对象key");
                        var body = FormalDungeonTcpTests.Death(actor.ObjectKey, peer.Id, checked((byte)actor.CandidateLevel));
                        peer.Send(39, body); Drain(); killed++; return body;
                    }
                    void Drain() => drain(peer, packet =>
                    {
                        if (packet.Opcode == 31) { Bytes(packet.Body, [], "钟与羽毛链结算许可为完整空31"); enabled++; }
                        if (packet.Opcode == 115) bossChecks++;
                    });
                    void RejectFinish()
                    {
                        string before = Assets(); peer.Send(34, [34, 0, (byte)scenario.Quest, (byte)(scenario.Quest >> 8), 255, 255, 1, 0, 255, 255]);
                        peer.Expect(1, 34, [0, 19, 0]); Drain();
                        That(Assets() == before, "未足额或已经交付的34拒绝且整笔资产不变：" + scenario.Quest);
                    }
                    bool IdentityClosed() => fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest) is [[0L]] && fixture.Query(
                        "SELECT acceptance_id,json_extract(plan_snapshot,'$.QuestSlot') FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest) is [[string original, long savedSlot]] && original == acceptance && savedSlot == slot;
                }
            }
            using (var peer = Open(fixture, account))
            {
                Select(peer, created); peer.Barrier();
                That(Active(peer.Id) == "[]" && peer.CompletedQuestHistory[4 + 3987] == 1 && peer.CompletedQuestHistory[4 + 3992] == 1 &&
                    Materials(10164808).Length == 0 && Materials(10164806).Length == 0 &&
                    Materials(10164803) is [[long uid, 1L]] && uid == rewardUid && bellUid != rewardUid &&
                    fixture.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id", peer.Id) is [[3987L], [3992L]],
                    "重选恢复两次真实唯一交付及服从之钟原UID，不伪造中间四任务领奖回执");
                string before = Assets();
                foreach (var body in oldDeaths) { peer.Send(39, body); drain(peer, null); }
                foreach (ushort id in new ushort[] { 3987, 3992 })
                {
                    peer.Send(34, [34, 0, (byte)id, (byte)(id >> 8), 255, 255, 1, 0, 255, 255]);
                    peer.Expect(1, 34, [0, 19, 0]); drain(peer, null);
                }
                That(Assets() == before, "重选后重放旧普通、脚本和物件39及34不重发钟、羽毛或奖励");
                Cleanup(peer, created, account);
            }

            object[][] Materials(int item) => fixture.Query("""
                SELECT i.item_uid,json_extract(i.item_core,'$.Value') FROM character_inventory_slots s
                JOIN character_inventory_items i ON i.item_uid=s.item_uid
                WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p1 ORDER BY i.item_uid
                """, created.Id, item);
            long Reward(ushort id, int item, string acceptance, byte type)
            {
                var actual = Materials(item);
                That(actual is [[long uid, 1L]] && uid > 0 && fixture.Query("""
                    SELECT acceptance_id,condition_type,json_array_length(rewards),json_extract(rewards,'$[0].Item.Uid'),
                      json_extract(rewards,'$[0].Item.Core.ItemId'),json_extract(rewards,'$[0].GrantedCount')
                    FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1
                    """, created.Id, id) is [[string original, long savedType, 1L, long savedUid, long savedItem, 1L]] &&
                    original == acceptance && savedType == type && savedUid == uid && savedItem == item,
                    "真实34奖励回执引用同一实际提交UID和一件增量：" + id);
                return (long)actual.Single()[0];
            }
            string Assets() => State(created.Id) + DungeonState(created.Id) +
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", created.Id) +
                fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", created.Id) +
                fixture.Snapshot("SELECT * FROM character_quest_drop_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0 ORDER BY quest_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0 ORDER BY operation_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0 ORDER BY instance_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_loot_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_drop_batches WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_drop_objects WHERE character_id=$p0 ORDER BY ground_key", created.Id) +
                fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind", account);
        }

        void VerifyGustDropChain()
        {
            // 只准备入口和一个原OR臂；三个目标经同一角色实际wire提交。合成39不证明2865自然演出或客户端死亡顺序。
            const string account = "metro-gust-chain";
            var done = new HashSet<int> { 3992, 3871 };
            var entrance = Entrance(3993, 313, done);
            That(!done.Overlaps([3993, 3995, 3998, 3872]), "阵风链不准备目标完成或另一个阵营臂");
            var created = Create(account, "MetroGust", 0, 89, done, entrance);
            var deaths = new List<(ushort Quest, byte[] Body)>();
            using (var peer = Open(fixture, account))
            {
                Select(peer, created);
                foreach (var scenario in new (ushort Quest, int Boss, int Map, int Item, int Rooms)[]
                    { (3993, 74658, 77058, 10164785, 9), (3995, 74659, 77079, 10164814, 10), (3998, 74657, 77109, 0, 10) })
                {
                    var quest = resources.Quests.Get(scenario.Quest);
                    That(quest.MinimumLevel == 89 && quest.MainQuest is null && quest.RewardType == "[item]" &&
                        quest.Rewards is [{ Type: 0, Value: 0 }, { Type: 0, Value: 0 }] && (scenario.Item == 0
                            ? quest.Condition == "[clear map]" && quest.IntData.SequenceEqual([scenario.Map])
                            : quest.Condition == "[seeking]" && quest.IntData.SequenceEqual([scenario.Item, 1])),
                        "连续链保留真实等级、条件及零物品奖励声明：" + scenario.Quest);
                    if (scenario.Item != 0)
                        That(quest.Definition!.Drops is [{ TemplateId: var template, EnemyType: 1, DungeonId: 313,
                            Difficulty: -1, ItemId: var item, Count: 1, Rate: 100, MaximumHeld: 1 }] &&
                            template == scenario.Boss && item == scenario.Item, "真实Boss掉落一件材料且携带上限一件");
                    That(Materials(10164785).Length == 0 && Materials(10164814).Length == 0,
                        "前一交付已消费原材料，下一目标不预填或沿用库存");
                    Accept(peer, scenario.Quest, 1);
                    string acceptance = Identity(peer.Id, scenario.Quest);
                    long slot = (long)fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest).Single()[0];
                    var maze = dungeons.Get(313).Mazes.Single(row => row.ConnectsQuest(scenario.Quest));
                    That(maze.MapSpecification!.Count == scenario.Rooms && maze.LayeredMaps.Count == 0 &&
                        maze.ConditionalMaps.Count == 0 && maze.WarpMaps.Count == 0 &&
                        maze.MapSpecification!.Single(row => row.Position == maze.Bosses.Single()).Id == scenario.Map,
                        "连续链使用原九/十房迷宫及真实终点，不借其他任务路线");
                    uint next = checked((uint)(long)fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]);
                    long previousAttempt = (long)fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0];
                    var plans = Plans(maze, peer.Id, next, dungeons.Get(313).BasisLevel);
                    var map = Enter(peer, scenario.Quest, 313, scenario.Quest, maze);
                    long attempt = (long)fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0];
                    That(attempt == previousAttempt + 1 && Identity(peer.Id, scenario.Quest) == acceptance,
                        "每条真实16只建立一次独立attempt并保留原接取");
                    var path = QuestStoryRouteTests.Path(maze.StaticTopology, maze.Starts.Single(), maze.Bosses.Single());
                    int enabled = 0, bossChecks = 0, inventoryNotices = 0, conditionNotices = 0;
                    var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99);
                    for (int at = 0; at < path.Length; at++)
                    {
                        var plan = plans.Single(row => row.Room.Map.Position == path[at]);
                        Actors(peer, map, plan, scenario.Quest);
                        FormalDungeonTcpTests.Load(peer); Drain();
                        foreach (var actor in plan.Monsters.Where(row => row.CanReportDeath && !row.IsBoss)) Kill(actor);
                        That(Materials(10164785).Length == 0 && Materials(10164814).Length == 0 &&
                            Scalar(peer.Id, scenario.Quest) == 1 && Identity(peer.Id, scenario.Quest) == acceptance && enabled == 0 && bossChecks == 0,
                            "普通怪及加载不能掉目标Boss材料、推进原接取或许可结算：" + plan.Room.Map.Id);
                        if (at + 1 < path.Length) map = MoveMap(peer, path[at + 1], 0);
                    }
                    var terminal = plans.Single(row => row.Room.Map.Id == scenario.Map);
                    var boss = terminal.Monsters.Single(row => row.MonsterId == scenario.Boss);
                    That(boss.IsBoss && boss.CanReportDeath && !boss.IsScriptedBoss && !boss.IsFriendly &&
                        terminal.Monsters.Count(row => row.IsBoss) == 1 &&
                        (scenario.Quest != 3998 || terminal.Monsters.Count(row => row.AiCharacter is not null) == 1) &&
                        terminal.Monsters.Where(row => row.AiCharacter is not null)
                            .All(row => row.MonsterId == 16802 && row.IsFriendly && !row.CanReportDeath),
                        "末图只报告真实普通Boss，保留友方16802，不将2865销毁85197当作Boss死亡");
                    RejectFinish();
                    string before = ChainState(); peer.Send(46, report); Drain();
                    That(ChainState() == before && enabled == 0, "Boss末击前46不能将演出、加载或普通击杀变成通关奖励");
                    int itemsBeforeBoss = inventoryNotices, conditionsBeforeBoss = conditionNotices;
                    byte[] death = FormalDungeonTcpTests.Death(boss.ObjectKey, peer.Id, checked((byte)boss.CandidateLevel));
                    Kill(boss); deaths.Add((scenario.Quest, death));
                    That(enabled == 1 && bossChecks == 1 && Scalar(peer.Id, scenario.Quest) == 0 &&
                        Identity(peer.Id, scenario.Quest) == acceptance, "真实末击才达成本次接取并发唯一115/空31");
                    long materialUid = 0;
                    if (scenario.Item != 0)
                    {
                        var material = Materials(scenario.Item);
                        That(material is [[long uid, 1L]] && uid > 0 && inventoryNotices > itemsBeforeBoss && conditionNotices > conditionsBeforeBoss,
                            "任务材料直接入包并发布14/574，拥有唯一真实UID及一件数量，不经过43");
                        materialUid = (long)material.Single()[0];
                        var source = fixture.Query("""
                            SELECT d.plan_snapshot,d.changed_items,d.revision,g.revision,g.instance_id,g.task_attempt,g.epoch
                            FROM character_quest_drop_receipts d JOIN character_dungeon_growth_receipts g
                              ON g.character_id=d.character_id AND g.event_id=d.event_id
                            WHERE g.character_id=$p0 AND g.quest_id=$p1 AND g.monster_id=$p2 AND g.map_id=$p3
                            """, peer.Id, scenario.Quest, scenario.Boss, scenario.Map);
                        That(source is [[string, string, long revision, long growthRevision, string, long savedAttempt, long]] &&
                            revision == growthRevision && savedAttempt == attempt, "材料与目标Boss成长沿同一event/事务revision提交一次");
                        using var snapshot = JsonDocument.Parse((string)source.Single()[0]);
                        var frozen = snapshot.RootElement;
                        var outcome = frozen.GetProperty("Outcomes").EnumerateArray().Single();
                        That(frozen.GetProperty("MapId").GetInt32() == scenario.Map && frozen.GetProperty("TemplateId").GetInt32() == scenario.Boss &&
                            frozen.GetProperty("EnemyType").GetInt32() == 1 && frozen.GetProperty("Identity").GetProperty("QuestId").GetInt32() == scenario.Quest &&
                            outcome.GetProperty("QuestId").GetInt32() == scenario.Quest &&
                            outcome.GetProperty("AcceptanceId").GetGuid().ToString("N") == acceptance &&
                            outcome.GetProperty("Rolled").GetInt32() == 1 && outcome.GetProperty("Granted").GetInt32() == 1 &&
                            outcome.GetProperty("Error").GetInt32() == 0, "掉落冻结结果绑定原Acceptance、任务实例、Boss及实际发放数量");
                        using var changed = JsonDocument.Parse((string)source[0][1]);
                        var committedItem = changed.RootElement.EnumerateArray().Single();
                        That(committedItem.GetProperty("Uid").GetInt64() == materialUid &&
                            committedItem.GetProperty("Core").GetProperty("ItemId").GetInt32() == scenario.Item,
                            "掉落回执记录实际提交UID而非规划临时UID");
                    }
                    else
                    {
                        var clear = fixture.Query("""
                            SELECT c.acceptance_id,c.quest_slot,c.epoch,g.epoch,c.instance_id,g.instance_id,c.task_attempt,g.task_attempt
                            FROM character_quest_clear_receipts c JOIN character_dungeon_growth_receipts g
                              ON g.character_id=c.character_id AND g.event_id=c.growth_event_id
                            WHERE c.character_id=$p0 AND c.quest_id=3998 AND c.map_id=77109 AND g.monster_id=74657 AND g.quest_id=3998
                            """, peer.Id);
                        That(clear is [[string original, long savedSlot, long epoch, long growthEpoch, string instance, string growthInstance,
                            long savedAttempt, long growthAttempt]] && original == acceptance && savedSlot == slot && epoch == growthEpoch + 1 &&
                            instance == growthInstance && savedAttempt == attempt && savedAttempt == growthAttempt,
                            "3998清图只来自74657末击，绑定原槽、Acceptance、instance、attempt及growth下一epoch");
                        peer.Send(46, report); Drain();
                        That(fixture.Query("SELECT quest_id,map_id,instance_id,task_attempt,epoch FROM character_dungeon_settlement_receipts WHERE character_id=$p0", peer.Id)
                            is [[3998L, 77109L, string instanceId, long settledAttempt, long settledEpoch]] &&
                            instanceId == (string)clear[0][4] && settledAttempt == attempt && settledEpoch == (long)clear[0][2],
                            "3998真实46结算同一77109通关实例及epoch");
                        before = ChainState(); peer.Send(46, report); Drain();
                        That(ChainState() == before, "重复3998结算不增经验、金币或卡牌奖励");
                    }
                    before = ChainState(); peer.Send(39, death); Drain();
                    That(ChainState() == before && enabled == 1 && bossChecks == 1, "重复Boss39不二次发材料、成长、进度或结算许可");
                    Finish(peer, scenario.Quest, scenario.Item == 0 ? (byte)3 : (byte)0);
                    var completed = fixture.Query("SELECT acceptance_id,consumed FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",
                        peer.Id, scenario.Quest).Single();
                    That((string)completed[0] == acceptance && Materials(10164785).Length == 0 && Materials(10164814).Length == 0,
                        "34关闭原接取并实际消费Boss掉落材料，不留给后继任务复用");
                    using (var consumed = JsonDocument.Parse((string)completed[1]))
                    {
                        var rows = consumed.RootElement.EnumerateArray().ToArray();
                        That(scenario.Item == 0 ? rows.Length == 0 : rows.Length == 1 && rows[0].GetProperty("Uid").GetInt64() == materialUid &&
                            rows[0].GetProperty("ItemId").GetInt32() == scenario.Item && rows[0].GetProperty("Count").GetInt32() == 1,
                            "交付回执消费同一真实UID的一件材料；清图任务不凭空消费物品");
                    }
                    RejectFinish();
                    before = ChainState(); peer.Send(39, death); Drain();
                    That(ChainState() == before, "34后迟到Boss39不能再次发材料或增长资产");
                    Return(peer);

                    void Kill(DungeonPlannedMonster actor)
                    {
                        That(actor.AiCharacter is null && actor.Source is { SpecialChild: null, ScriptedRetirement: false } &&
                            actor.CanReportDeath && !actor.IsScriptedBoss, "只向当前真实可报告普通怪物发送明确合成39");
                        peer.Send(39, FormalDungeonTcpTests.Death(actor.ObjectKey, peer.Id, checked((byte)actor.CandidateLevel))); Drain();
                    }
                    void Drain() => drain(peer, packet =>
                    {
                        if (packet.Opcode == 31) { Bytes(packet.Body, [], "阵风链结算许可保持空31"); enabled++; }
                        if (packet.Opcode == 115) bossChecks++;
                        if (packet.Opcode == 14) inventoryNotices++;
                        if (packet.Opcode == 574) conditionNotices++;
                    });
                    void RejectFinish()
                    {
                        string unchanged = ChainState();
                        peer.Send(34, [34, 0, (byte)scenario.Quest, (byte)(scenario.Quest >> 8), 255, 255, 1, 0, 255, 255]);
                        peer.Expect(1, 34, [0, 19, 0]); Drain();
                        That(ChainState() == unchanged, "未达成或已交付34拒绝且不改变资产或凭据：" + scenario.Quest);
                    }
                }
            }
            using (var peer = Open(fixture, account))
            {
                Select(peer, created); peer.Barrier();
                That(Active(peer.Id) == "[]" && new[] { 3993, 3995, 3998 }.All(id => peer.CompletedQuestHistory[4 + id] == 1) &&
                    fixture.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id", peer.Id)
                        is [[3993L], [3995L], [3998L]], "重选恢复连续三任务唯一交付历史，没有遗留活动槽");
                string before = ChainState();
                foreach (var old in deaths)
                {
                    peer.Send(39, old.Body); drain(peer, null);
                    peer.Send(34, [34, 0, (byte)old.Quest, (byte)(old.Quest >> 8), 255, 255, 1, 0, 255, 255]);
                    peer.Expect(1, 34, [0, 19, 0]); drain(peer, null);
                }
                That(ChainState() == before, "重选后的三个旧实例39及重复34不能恢复旧掉落或重复领奖");
                Cleanup(peer, created, account);
            }

            object[][] Materials(int item) => fixture.Query("""
                SELECT i.item_uid,json_extract(i.item_core,'$.Value') FROM character_inventory_slots s
                JOIN character_inventory_items i ON i.item_uid=s.item_uid
                WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p1 ORDER BY i.item_uid
                """, created.Id, item);
            string ChainState() => State(created.Id) + DungeonState(created.Id) +
                fixture.Snapshot("SELECT i.* FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 ORDER BY i.item_uid", created.Id) +
                fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", created.Id) +
                fixture.Snapshot("SELECT * FROM character_quest_drop_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0 ORDER BY quest_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0 ORDER BY instance_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_loot_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_drop_batches WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_drop_objects WHERE character_id=$p0 ORDER BY ground_key", created.Id) +
                fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind", account);
        }

        void VerifyFaction(byte job, ushort questionId, ushort branchId, ushort otherBranch)
        {
            var target = resources.Quests.Get(3954);
            var question = resources.Quests.Get(questionId);
            var branch = resources.Quests.Get(branchId);
            var group = target.Definition!.PrerequisiteGroups.Single(arm => arm.Contains(branchId));
            var done = group.Where(id => id != branchId).Concat(question.Definition!.PrerequisiteGroups.Single()).ToHashSet();
            var entrance = Entrance(3954, 310, done);
            var position = Npc(question.IntData.Single(), 88, done);
            That(branch.IntData.SequenceEqual(question.IntData) && question.IntData is [331] &&
                branch.Definition!.RequiredAnswers is [{ QuestId: var requiredQuestion }] && requiredQuestion == questionId,
                "3954两臂使用原NPC331和真实问题回答条件：" + branchId);
            byte choice = checked((byte)branch.Definition!.RequiredAnswers.Single().Answer);
            That(!done.Overlaps(Enumerable.Range(3868, 7)) && !done.Contains(3954), "准备不填问题、阵营或目标完成位");
            string account = "metro-or-" + job;
            var created = Create(account, "MetroOr" + job, job, 88, done, position);
            using var peer = Open(fixture, account);
            Select(peer, created);
            RejectAccept(peer, branchId);
            RejectAccept(peer, job == 0 ? (ushort)3869 : (ushort)3868);
            Accept(peer, questionId, QuestQuestionProgress.Unanswered);
            string unanswered = State(peer.Id);
            peer.Send(33, [33, 0, (byte)questionId, (byte)(questionId >> 8), 255, 0]);
            peer.Expect(1, 33, [0, 19, 0]); peer.Barrier();
            That(State(peer.Id) == unanswered, "越界回答不改问题哨兵或提前保存答案");
            Condition(peer, questionId, choice, choice);
            That(fixture.Query("SELECT answer FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1", peer.Id, questionId).Length == 0,
                "33只保存原活动选择，回答完成记录必须等34");
            Finish(peer, questionId, 42);
            That(fixture.Query("SELECT answer FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1", peer.Id, questionId)
                    is [[long saved]] && saved == choice && peer.CompletedQuestHistory[4 + questionId] == choice + 1,
                "真实问题34与356持久化同一零基回答：" + questionId);
            RejectAccept(peer, otherBranch);
            Accept(peer, branchId, 1);
            Condition(peer, branchId, 0, 0);
            Finish(peer, branchId, 4);
            That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id BETWEEN 3870 AND 3874", peer.Id)
                    is [[long faction]] && faction == branchId,
                "只完成实际回答选择的一个阵营，不将全部OR条件平铺完成");
            TravelTo(peer, entrance);
            Accept(peer, 3954, 1);
            EntryRound(peer, 3954, 310, [3954, 0]);
            Cleanup(peer, created, account);
        }

        void VerifyYoungGizelEntry()
        {
            var parent = resources.Quests.Get(3640);
            var first = resources.Quests.Get(3686);
            var hunt = resources.Quests.Get(3687);
            var delivery = resources.Quests.Get(3688);
            That(QuestRelationProgress.SequentialParent(parent, resources.Quests) &&
                parent.Definition!.PrerequisiteGroups is [{ } prerequisite] && prerequisite.SequenceEqual([3639]) &&
                parent.IntData.SequenceEqual([3686,3687,3688]) && parent.MinimumLevel == 77 &&
                first.MainQuest == 3640 && first.Condition == "[clear map]" && first.IntData.SequenceEqual([91845]) &&
                first.Definition!.Dungeons.Select(row => row.DungeonId).SequenceEqual([517]) &&
                hunt.MainQuest == 3640 && hunt.Condition == "[hunt monster]" && hunt.IntData.SequenceEqual([72,-1,56158,1]) &&
                hunt.Rewards.Select(row => row.Value).SequenceEqual([10164766,1]) &&
                delivery.MainQuest == 3640 && delivery.Condition == "[seek n meet npc]" &&
                delivery.IntData.SequenceEqual([10164766,1,1013]) &&
                delivery.Definition!.Dungeons.Select(row => row.DungeonId).SequenceEqual([517]),
                "3640保留清图、猎杀奖励与寻物会面的三个真实阶段，不改成会面链");
            var dungeon = dungeons.Get(517);
            DungeonPoint[] coordinates = [new(0,0),new(1,0),new(1,1),new(2,1),new(3,1)];
            int[][] monsterIds = [[61274,61243,61243,61243,61274,61243,61274],
                [61276,61245,61276,61276,61276,61276,61245,61245],
                [61245,61243,61245,61245,61243,61243,61243],
                [61244,61274,61244,61274,61244,61244,61274,61244,61244,61244,61274,61274], [70216]];
            foreach (var (id,ordinal,ids,passives) in new[]
            {
                (3686,2,new[] {91841,91842,91843,91844,91845},new[] {14,11,12,13,5}),
                (3688,3,new[] {91861,91862,91863,91864,91865},new[] {14,11,12,13,3})
            })
            {
                var maze = dungeon.Mazes.Single(row => row.ConnectsQuest(checked((uint)id)));
                That(dungeon.MinimumLevel == 70 && dungeon.BasisLevel == 73 && maze.SourceOrdinal == ordinal &&
                    maze.Width == 4 && maze.Height == 2 && maze.Starts.SequenceEqual([coordinates[0]]) &&
                    maze.Bosses.SequenceEqual([coordinates[^1]]) && maze.MapSpecification!.Select(row => row.Id).SequenceEqual(ids) &&
                    maze.MapSpecification!.Select(row => row.Position).SequenceEqual(coordinates) &&
                    maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 && maze.StoryStages.Count == 0 && maze.WarpMaps.Count == 0,
                    "D517两条连接保留各自完整五房与原坐标，不靠连续编号推断演员：" + id);
                var plans = Plans(maze, 99, 100, dungeon.BasisLevel);
                for (int index = 0; index < plans.Length; index++)
                {
                    var plan = plans[index];
                    That(plan.Room.Map.Path == "map/cataclysm/act13/517_lawlessdistrict/" + ids[index] + ".map" &&
                        plan.Monsters.Select(actor => actor.MonsterId).SequenceEqual(monsterIds[index]) &&
                        plan.Room.PassiveObjects.Count == passives[index] && plan.SpecialObjects.Count == 0 &&
                        plan.Room.AiCharacters.Count == 0 && plan.Monsters.All(actor => actor.CandidateLevel == 73 &&
                            actor.Team == 100 && !actor.IsFriendly && actor.CanReportDeath && actor.Source is
                                { PlacementKind: "[fixed]", NpcParameter: null }) &&
                        (index == 4 ? plan.Monsters.Single() is { SpawnType: 3, IsBoss: true, IsScriptedBoss: true } :
                            plan.Monsters.All(actor => actor.SpawnType == 0 && !actor.IsBoss && !actor.IsScriptedBoss)),
                        "D517逐房保留原普通怪、装饰与唯一dummy，不制造战斗或剧情退场：" + ids[index]);
                }
                That(plans[^1].Room.NpcMetadataError is null && plans[^1].Room.Npcs.Select(row => row.Id)
                    .SequenceEqual(id == 3686 ? new[] {1000} : new[] {1013,1000}),
                    "末房NPC保持独立声明，不由dummy推断吉赛尔身份：" + id);
            }
            That(dungeons.Get(72).Mazes.Single(row => row.ConnectsQuest(3687)).MapSpecification!.Select(row => row.Id)
                .SequenceEqual([91851,91852,91858,91859,91853,91854,91855,91856,91857]),
                "中间猎杀属于D72九房，不误入D517的首子或交付路线");
            // 只准备父前置3639及真实入口资格；本轮不完成三个子任务，不预填目标材料。
            var done = new HashSet<int> {3639};
            var entrance = Entrance(3686, 517, done);
            That(!done.Overlaps([3640,3686,3687,3688]), "入口准备不包含目标父子完成位");
            const string account = "young-gizel-180";
            var created = Create(account, "YoungGizel", 0, 77, done, entrance);
            That(created.Inventory.Items.All(row => row.Core.ItemId != 10164766), "首阶段不预填猎杀交付奖励材料");
            string parentIdentity, childIdentity, accepted;
            using (var peer = Open(fixture, account))
            {
                Select(peer, created);
                Accept(peer, 3640, 3);
                parentIdentity = Identity(peer.Id, 3640);
                RejectAccept(peer, 3687); RejectAccept(peer, 3688);
                EntryRound(peer, 3686, 517, [3640,0]);
                That(fixture.Query("SELECT quest_id FROM character_active_main_quests WHERE character_id=$p0", peer.Id) is [[3640L]],
                    "仅父活动时父/0请求只借首子布局，不替客户端接取3686");
                Accept(peer, 3686, 1);
                childIdentity = Identity(peer.Id, 3686);
                RejectAccept(peer, 3687); RejectAccept(peer, 3688);
                // 请求0的RunQuestId保持0；不假定子已活动时的RouteQuestId仍是父3640。
                EntryRound(peer, 3686, 517, [3640,3686,0]);
                That(Scalar(peer.Id, 3640) == 3 && Scalar(peer.Id, 3686) == 1 && Identity(peer.Id, 3640) == parentIdentity,
                    "首子未清入场不推进3640三阶段或更换接取身份");
                accepted = QuestState(peer.Id);
            }
            using (var peer = Open(fixture, account))
            {
                var packets = peer.Select(RoleIndex(peer.Roster, created), prepared: true, resumeTown: true, room: false);
                peer.Barrier();
                var active = fixture.Query("SELECT slot,quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", peer.Id);
                var ack = packets.Single(packet => packet.Kind == 1 && packet.Opcode == 4).Body;
                That(peer.Id == created.Id && ack.Length == 235 && ack[0] == 1 && active.Length == 2 &&
                    QuestState(peer.Id) == accepted && Identity(peer.Id, 3640) == parentIdentity && Identity(peer.Id, 3686) == childIdentity &&
                    Enumerable.Range(0,30).All(slot =>
                    {
                        var row = active.SingleOrDefault(row => (long)row[0] == slot);
                        return U16(ack,22+6*slot) == (row is null ? ushort.MaxValue : (long)row[1]) &&
                            U32(ack,24+6*slot) == (row is null ? 0 : (long)row[2]);
                    }) && new[] {3640,3686,3687,3688}.All(id => peer.CompletedQuestHistory[4+id] == 0) && NoClear(peer.Id),
                    "实际断线ACK4完整恢复原父子三十槽，目标历史全未完成且无清图凭据");
                EntryRound(peer, 3686, 517, [0]);
                That(QuestState(peer.Id) == accepted, "断线后普通0仍进入首子原路线，不补写后续任务或进度");
                Cleanup(peer, created, account);
            }
        }

        void VerifySequential(ushort parentId, (ushort Id, int Npc)[] meetings, ushort childId, uint dungeon, byte level)
        {
            var parent = resources.Quests.Get(parentId);
            var child = resources.Quests.Get(childId);
            That(QuestRelationProgress.SequentialParent(parent, resources.Quests) &&
                meetings.Length > 0 && parent.IntData.SequenceEqual(meetings.Select(row => (int)row.Id).Append(childId)) &&
                meetings.All(row => resources.Quests.Get(row.Id).IntData.SequenceEqual([row.Npc])),
                "真实父子顺序与会面NPC不改定义：" + parentId);
            var done = parent.Definition!.PrerequisiteGroups.Single().ToHashSet();
            var entrance = Entrance(childId, dungeon, done);
            var positions = meetings.Select(row => Npc(row.Npc, level, done)).ToArray();
            That(!done.Overlaps(parent.IntData.Append(parentId)), "父子准备只包含原前置和入口资格：" + parentId);
            string account = "metro-sequence-" + parentId;
            var created = Create(account, "MetroSeq" + parentId, 0, level, done, positions[0]);
            string parentIdentity, childIdentity, accepted;
            using (var peer = Open(fixture, account))
            {
                Select(peer, created);
                uint remaining = checked((uint)meetings.Length + 1);
                Accept(peer, parentId, remaining);
                parentIdentity = Identity(peer.Id, parentId);
                RejectAccept(peer, childId);
                for (int index = 0; index < meetings.Length; index++)
                {
                    var meeting = meetings[index];
                    foreach (var later in meetings.Skip(index + 1)) RejectAccept(peer, later.Id);
                    TravelTo(peer, entrance);
                    SelectMap(peer);
                    string blocked = State(peer.Id) + DungeonState(peer.Id);
                    peer.Send(16, Selection(dungeon, parentId)); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
                    That(State(peer.Id) + DungeonState(peer.Id) == blocked,
                        "当前会面阶段拒绝父请求借后继迷宫，不预留key或attempt：" + parentId + "/" + meeting.Id);
                    // 普通0此时可能选择普通迷宫，不将会面阶段误解为整个副本封禁。
                    peer.Send(132); peer.Expect(1, 132, [1]);
                    peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
                    peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
                    TravelTo(peer, positions[index]);
                    Accept(peer, meeting.Id, 1);
                    Condition(peer, meeting.Id, 0, 0);
                    That(Scalar(peer.Id, parentId) == remaining && Identity(peer.Id, parentId) == parentIdentity,
                        "真实NPC33先保存会面，父身份及顺序仍等子34：" + meeting.Id);
                    Finish(peer, meeting.Id, 4);
                    remaining--;
                    That(Scalar(peer.Id, parentId) == remaining && Identity(peer.Id, parentId) == parentIdentity &&
                        fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1", peer.Id, meeting.Id)
                            is [[long completed]] && completed == meeting.Id,
                        "实际会面交付逐阶段推进原父，不换接取UUID：" + parentId + "/" + remaining);
                    if (index + 1 < meetings.Length) RejectAccept(peer, childId);
                }
                TravelTo(peer, entrance);
                if (parentId is 3547 or 3540) AntberRoute(peer);
                if (parentId == 3521) GuardianRoute(peer);
                EntryRound(peer, childId, dungeon, [parentId, 0]);
                That(fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1", peer.Id, childId).Length == 0,
                    "父请求及普通0只借当前子路线，不伪造子任务接取：" + childId);
                Accept(peer, childId, 1);
                childIdentity = Identity(peer.Id, childId);
                EntryRound(peer, childId, dungeon, [childId, 0]);
                That(Scalar(peer.Id, parentId) == 1 && Scalar(peer.Id, childId) == 1 && Identity(peer.Id, parentId) == parentIdentity,
                    "子路线入场未清图，不归零父子或发父奖励：" + parentId);
                accepted = QuestState(peer.Id);
                if (parentId is not (3547 or 3540 or 3521)) Cleanup(peer, created, account);
            }
            if (parentId is not (3547 or 3540 or 3521)) return;
            using (var peer = Open(fixture, account))
            {
                var entry = peer.Select(RoleIndex(peer.Roster, created), prepared: true, resumeTown: true, room: false);
                peer.Barrier();
                var active = fixture.Query("SELECT slot,quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", peer.Id);
                That(peer.Id == created.Id && QuestState(peer.Id) == accepted && Identity(peer.Id, parentId) == parentIdentity &&
                    Identity(peer.Id, childId) == childIdentity && entry[0] is { Kind: 1, Opcode: 4, Body.Length: 235 } &&
                    active.Length == 2 && Enumerable.Range(0, 30).All(slot =>
                    {
                        var row = active.SingleOrDefault(row => (long)row[0] == slot);
                        return U16(entry[0].Body, 22 + 6 * slot) == (row is null ? ushort.MaxValue : (long)row[1]) &&
                            U32(entry[0].Body, 24 + 6 * slot) == (row is null ? 0 : (long)row[2]);
                    }) && meetings.All(row => peer.CompletedQuestHistory[4 + row.Id] == 1) && peer.CompletedQuestHistory[4 + parentId] == 0 &&
                    peer.CompletedQuestHistory[4 + childId] == 0 && NoClear(peer.Id),
                    "断线重连ACK4完整恢复三十槽、原父子身份及全部真实会面完成历史：" + parentId);
                if (parentId == 3521)
                    That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id IN (3521,3555,3556,3557) ORDER BY quest_id",peer.Id)
                        is [[3555L],[3556L]] && fixture.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",peer.Id)
                        is [[3555L],[3556L]], "3521只持久完成两次真实会面，没有清图子或父任务奖励");
                EntryRound(peer, childId, dungeon, [0]);
                That(QuestState(peer.Id) == accepted && NoClear(peer.Id), "重连普通0仍可进入原子任务路线且未清退出不完成父子：" + childId);
                Cleanup(peer, created, account);
            }

            void GuardianRoute(TcpPeer peer)
            {
                var definition = dungeons.Get(81);
                var maze = definition.Mazes.Single(row => row.ConnectsQuest(3557));
                DungeonPoint[] positions = [new(0,1),new(1,1),new(1,2),new(2,0),new(2,1),new(2,2),new(3,0),new(3,1),new(4,1)];
                int[][] monsters = [[61494,61494,61801,61801,61801],
                    [61113,61113,61494,61494,61800,61800,61800,61800],
                    [61113,61113,61801,61801,61801,61801,56106],
                    [56501,61801,61801,61801,61801,61801,61801],
                    [61494,61494,61494,61803,61803,61800,61800,61800],
                    [61800,61494,61800,61800,61494,61494,56501],
                    [61800,61800,61800,61800,61800,61800,61800,61800],
                    [61113,61113,61113,61801,61801,61801,61801],
                    [61800,61800,61800,61800,61800,61494,61494,61800,61800,61800]];
                int[] passives = [16,19,18,24,31,15,15,13,46];
                That(parent.Definition!.PrerequisiteGroups.Single().SequenceEqual([3520]) &&
                    parent.IntData.SequenceEqual([3555,3556,3557]) && child.IntData.SequenceEqual([92217]) &&
                    meetings.Select(row => row.Id).SequenceEqual(new ushort[] {3555,3556}) &&
                    meetings.Select(row => row.Npc).SequenceEqual([59,58]) &&
                    definition.MinimumLevel == 64 && definition.BasisLevel == 67 && maze.SourceOrdinal == 1 &&
                    maze.Width == 5 && maze.Height == 3 && maze.ResourceError is null && maze.UnsupportedReason is null &&
                    maze.StaticTopology.Supported && maze.MapSpecification!.Count == 9 && maze.Starts.Single() == positions[0] &&
                    maze.Bosses.Single() == positions[^1] && maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 &&
                    maze.StoryStages.Count == 0 && maze.WarpMaps.Count == 0,
                    "3521保持前置3520、两次真实会面及D81九房3557连接，不借后继3522迷宫");
                uint next = checked((uint)Convert.ToInt64(fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0",peer.Id).Single()[0]));
                var plans = Plans(maze,peer.Id,next,definition.BasisLevel);
                That(plans.Length == 9 && plans.Sum(plan => plan.Room.Monsters.Count) == 67 &&
                    plans.Sum(plan => plan.Room.PassiveObjects.Count) == 197 && plans.Sum(plan => plan.Room.AiCharacters.Count) == 1,
                    "3557准备全部67普通怪、197装饰物和唯一敌方Boss APC，不只验证起点");
                for (int index = 0; index < plans.Length; index++)
                {
                    var plan = plans[index];
                    That(plan.Room.Map.Id == 92209+index && plan.Room.Map.Position == positions[index] &&
                        plan.Room.Map.Path == "map/cataclysm/antberr/81_genteastgate/3521_"+(92209+index)+".map" &&
                        plan.Room.Monsters.Select(actor => actor.MonsterId).SequenceEqual(monsters[index]) &&
                        plan.Room.Monsters.All(actor => actor.RawValues[1] == 1 && actor.RawValues[2] == 0 &&
                            actor.PlacementKind == "[fixed]" && actor.RankKind == "[normal]" && actor.Modifiers.Count == 0) &&
                        plan.Room.PassiveObjects.Count == passives[index] && plan.SpecialObjects.Count == 0 &&
                        plan.Room.AiCharacters.Count == (index == 8 ? 1 : 0) &&
                        plan.Monsters.Count == monsters[index].Length+(index == 8 ? 1 : 0) &&
                        plan.Monsters.Where(actor => actor.AiCharacter is null).All(actor => actor is
                            { CandidateLevel:67, SpawnType:0, Team:100, IsFriendly:false, IsBlocking:true, IsBoss:false }),
                        "D81逐房独立核对原坐标、MAP路径、普通演员等级及装饰物数量："+plan.Room.Map.Id);
                }
                That(plans[^1].Monsters[^1] is { MonsterId:14002, CandidateLevel:67, SpawnType:8, Source:null,
                    IsFriendly:false, IsBlocking:false, CanReportDeath:true, IsBoss:true,
                    AiCharacter: { Id:14002, Faction:"[monster]", RankKind:"[boss]",
                        Path:"aicharacter/gunner/tmh_signalman/tmhsignalman_q1.aic", Level:67 } },
                    "92217保留AIC最低信息真实Lv67的敌方Boss14002，不改阵营、不转普通monster或删演员");
                // 九图没有直接ACT/ME；本回归不从NPC、装饰物或APC存在推导死亡、演出或清图事实。
            }

            void AntberRoute(TcpPeer peer)
            {
                bool midnight = parentId == 3540;
                var definition = dungeons.Get(checked((int)dungeon));
                var maze = definition.Mazes.Single(row => row.ConnectsQuest(childId));
                int firstMap = midnight ? 92352 : 92407, target = midnight ? 92360 : 92415;
                DungeonPoint[] positions = midnight
                    ? [new(0,2),new(1,2),new(2,0),new(2,1),new(2,2),new(3,1),new(3,0),new(4,1),new(4,2)]
                    : [new(0,0),new(0,1),new(0,2),new(1,2),new(1,1),new(2,1),new(3,1),new(3,2),new(4,2)];
                // 午夜行动的DGN按资源行列出地图，真实连接顺序并不按地图ID递增。
                DungeonPoint[] route = midnight
                    ? [new(0,2),new(1,2),new(2,2),new(2,1),new(2,0),new(3,0),new(3,1),new(4,1),new(4,2)] : positions;
                int[][] monsters = midnight
                    ? [[61441,61443,61443,61441], [61437,61437,61437,61482,61482,61482],
                        [61437,61437,61437,61437,61437], [61437,61437,61437,61437,61437],
                        [61442,61442,61442,61442,61482,61482,61482], [61441,61443,61443,61441,61482,61482,61482],
                        [61436,61436,61439,61436,61439,61439,61436], [61442,61442,61442,61441,61441,61443,61441,61443,61443],
                        [61442,61442,61442,61442,61441,61441,61441,56001]]
                    : [[61216,61216,61216,61009,61009,61009],
                    [61006,61006,61006,61009,61009,61009,61009,61009],
                    [61006,61006,61006,61009,61009,61009,61009], [61009,61009,61009,61009,61009,61009,61009],
                    [61005,61005,61005,61005,61005,61005], [56006,61216,61216,56006,61216,61216],
                    [61006,61005,61006,61005,61006,61005,61006,61005], [61014,61014,61014,61014,61014,61014], [63821]];
                int[] passives = midnight ? [12,8,34,61,39,29,29,20,32] : [17,13,21,22,23,14,16,17,15];
                int apcRoom = midnight ? 0 : 8, apc = midnight ? 10451 : 812, basis = midnight ? 71 : 72;
                That(parent.Definition!.PrerequisiteGroups.Single().SequenceEqual([midnight ? 3539 : 3546]) && child.IntData.SequenceEqual([target]) &&
                    definition.MinimumLevel == level && definition.BasisLevel == basis && maze.SourceOrdinal == (midnight ? 2 : 4) && maze.Width == 5 && maze.Height == 3 &&
                    maze.ResourceError is null && maze.MapSpecification!.Count == 9 && maze.LayeredMaps.Count == 0 &&
                    maze.ConditionalMaps.Count == 0 && maze.StoryStages.Count == 0 && maze.StaticTopology.Supported &&
                    QuestStoryRouteTests.Path(maze.StaticTopology, maze.Starts.Single(), maze.Bosses.Single()).SequenceEqual(route),
                    "父子保持真实前置、目标与九房折线路线：" + parentId);
                uint next = checked((uint)Convert.ToInt64(fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id).Single()[0]));
                var plans = Plans(maze, peer.Id, next, definition.BasisLevel);
                That(plans.Length == 9, "入场须准备全部九房，不能只准备起点掩盖末房资源失败：" + childId);
                for (int index = 0; index < positions.Length; index++)
                {
                    var plan = plans.Single(plan => plan.Room.Map.Position == positions[index]);
                    That(plan.Room.Map.Id == firstMap + index && plan.Room.Monsters.Select(actor => actor.MonsterId).SequenceEqual(monsters[index]) &&
                        plan.Room.PassiveObjects.Count == passives[index] && plan.SpecialObjects.Count == 0 &&
                        plan.Room.AiCharacters.Count == (index == apcRoom ? 1 : 0) && plan.Monsters.Count == monsters[index].Length + (index == apcRoom ? 1 : 0) &&
                        plan.Monsters.Where(actor => actor.AiCharacter is null).All(actor => actor.CandidateLevel == basis &&
                            actor.Source is { RawValues: var raw } && raw[1] == 1 && raw[2] == 0),
                        "逐房保留真实普通怪、APC、装饰物及完整演员计划：" + plan.Room.Map.Id);
                    if (index == apcRoom)
                        That(plan.Monsters[^1] is { CandidateLevel: 70, SpawnType: 5, IsFriendly: true, IsBlocking: false, CanReportDeath: false } &&
                            plan.Monsters[^1].MonsterId == apc, "友方APC使用原AIC70级，不套副本基准或伪造死亡：" + apc);
                    if (!midnight && index == 8)
                        That(plan.Monsters[0] is { MonsterId: 63821, IsScriptedBoss: true } &&
                            plan.Monsters[1] is { MonsterId: 812, SpawnType: 5, IsFriendly: true, IsBlocking: false, CanReportDeath: false },
                            "92415保留原dummy Boss与友方APC812，本例不伪造其死亡或演出完成");
                    if (midnight)
                        That(plan.Monsters.Where(actor => actor.AiCharacter is null).All(actor => actor.CanReportDeath && actor.IsBlocking &&
                            !actor.IsFriendly && !actor.IsScriptedBoss && actor.IsBoss == (actor.MonsterId == 56001) &&
                            actor.SpawnType == (actor.MonsterId == 56001 ? 3 : 0)), "午夜行动保留真实敌人与56001普通Boss，不制造战斗事件");
                }
                if (midnight)
                {
                    const string mapPath = "map/cataclysm/antberr/83_nightassault/3540_92352.map";
                    const string actionPath = "map/cataclysm/antberr/83_nightassault/action/3540_92352.act";
                    That(maze.MapSpecification!.Single(map => map.Id == 92352).Path == mapPath &&
                        TutorialSceneFile.BasicAction(resources.Archive.ReadTokens(mapPath), resources.Archive.ResolveString) == "Action/3540_92352.act",
                        "午夜行动起点保留准确相对ACT引用");
                    var action = resources.Archive.ReadTokens(actionPath);
                    That(action.All(token => token.Type is 0 or 3) && action.Where(token => token.Type == 0).Select(token => token.Value).SequenceEqual([1,0,2221]) &&
                        action.Where(token => token.Type == 3).Select(token => resources.Archive.ResolveString(token.Value).ToUpperInvariant()).SequenceEqual(
                            ["[TRIGGER]","[ON START MAP]","[LIMIT]","[WHICH]","[CHARACTER]","[DO BEHAVIOR]","[ME]","[/TRIGGER]","[BEHAVIOR]","[CINEMATIC]","[/BEHAVIOR]"]),
                        "只核ON START MAP一次性触发2221原声明；入场与37不证明自然播放、死亡或清图");
                }
            }
        }

        void VerifyMovement(ushort id, uint dungeon, byte level, DungeonPoint destination, int baseMap, int? layerMap)
        {
            var quest = resources.Quests.Get(id);
            var done = quest.Definition!.PrerequisiteGroups.Single().ToHashSet();
            var entrance = Entrance(id, dungeon, done);
            That(!done.Contains(id) && QuestClearEligibility.IsSingleMap(quest), "移动代表保留真实清图任务与未完成位：" + id);
            string account = "metro-move-" + id;
            var created = Create(account, "MetroMv" + id, 0, level, done, entrance);
            using var peer = Open(fixture, account);
            Select(peer, created);
            Accept(peer, id, 1);
            string accepted = Active(peer.Id);
            var maze = dungeons.Get(checked((int)dungeon)).Mazes.Single(row => row.ConnectsQuest(id));
            if (layerMap is { } terminal)
                That(quest.IntData.SequenceEqual([terminal]) && maze.Bosses.Single() == destination &&
                    maze.MapSpecification!.Single(map => map.Position == destination).Id == baseMap &&
                    maze.LayeredMaps is [{ Id: 655667, Position: { X: 2, Y: 0 }, CandidateIds: [655667] }],
                    "7958只将原655667层作为清图目标，基础655666与Boss格不改写");
            else That(maze.WarpMaps is [{ Source: { X: 2, Y: 0 }, Destination: { X: 3, Y: 0 } }],
                "3969只验证原声明的相邻warp，不凭地图编号猜连接");
            foreach (ushort request in new ushort[] { id, 0 })
            {
                uint nextKey = checked((uint)Convert.ToInt64(fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id).Single()[0]));
                var plans = Plans(maze, peer.Id, nextKey, dungeons.Get(checked((int)dungeon)).BasisLevel);
                var map = Enter(peer, id, dungeon, request, maze);
                var current = maze.MapSpecification!.Single(row => row.Position == maze.Starts.Single());
                var path = QuestStoryRouteTests.Path(maze.StaticTopology, current.Position, destination);
                if (layerMap is null) That(path.Length > 1 && path[^2] == new DungeonPoint(2, 0) && path[^1] == destination,
                    "3969实际45路径包含原warp来源到目标的准确一步");
                int confirmed = 0, enabled = 0;
                for (int index = 0; index < path.Length; index++)
                {
                    current = maze.MapSpecification!.Single(row => row.Position == path[index]);
                    var actors = plans.Single(plan => plan.Room.Map == current);
                    Actors(peer, map, actors, id);
                    FormalDungeonTcpTests.Load(peer); Drain();
                    if (index == path.Length - 1) break;
                    foreach (var actor in actors.Monsters.Where(actor => actor.IsBlocking))
                    {
                        That(actor.AiCharacter is null && actor.Source is { SpecialChild: null, ScriptedRetirement: false } && !actor.IsScriptedBoss,
                            "前房仅按真实普通阻挡怪物发送已确认88B死亡合同：" + id);
                        peer.Send(39, FormalDungeonTcpTests.Death(actor.ObjectKey, peer.Id, checked((byte)actor.CandidateLevel)));
                        Drain(); confirmed++;
                    }
                    map = MoveMap(peer, path[index + 1], 0);
                }
                That(U32(map, 14) == baseMap, "原45路线抵达准确基础/warp目标图：" + id);
                if (layerMap is { } layer)
                {
                    // 首次kind1沿当前基础房原声明进入叠层，不需要先杀基础dummy，也不提前杀末层Boss。
                    map = MoveMap(peer, destination, 1);
                    var expectedLayer = plans.Single(plan => plan.Room.Map.Id == layer && plan.Room.Map.Position == destination);
                    Actors(peer, map, expectedLayer, id);
                    That(map[2] == 1 && U32(map, 14) == layer, "7958真实kind1下发655667及完整原演员");
                    FormalDungeonTcpTests.Load(peer); Drain();
                }
                That(enabled == 0 && Active(peer.Id) == accepted && NoClear(peer.Id) &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND task_attempt=$p1", peer.Id,
                        Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id).Single()[0])) is [[long count]] && count == confirmed,
                    "只记录实际前房死亡；到达层/warp不制造任务清图或结算：" + id);
                Return(peer);
                That(Active(peer.Id) == accepted && NoClear(peer.Id), "未击杀目标的45代表退出不完成任务：" + id);

                void Drain() => drain(peer, packet => { if (packet.Opcode == 31) enabled++; });
            }
            Cleanup(peer, created, account);
        }

        Character Create(string account, string name, byte job, byte level, HashSet<int> done, Location position)
        {
            That(resources.Towns.IsPosition(position, done) && resources.Towns.DestinationAdmission(position, level, done) == "ready",
                "隔离入口满足原城镇等级与前置：" + name);
            using var owner = new AccountOwners().Acquire(account, () => { });
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(job), position, position, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(job, 0, level, candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(level) }, resources.Learning);
            var created = new CharacterRepository(fixture.Database).Create(owner, candidate with
                { Level = level, AcceptedStoryLevel = level, CompletedQuests = done.Order().ToArray(), InitialLearning = learning });
            That(Active(created.Id) == "[]", "新角色不预填活动槽、目标条件或接取UUID：" + name);
            return created;
        }

        void Select(TcpPeer peer, Character created)
        {
            peer.Select(RoleIndex(peer.Roster, created), prepared: true, resumeTown: true, room: false);
            That(peer.Id == created.Id, "选角只绑定本条隔离角色");
        }

        void Accept(TcpPeer peer, ushort id, uint scalar)
        {
            That(resources.Quests.Get(id).Definition!.AcceptanceRewards.Length == 0, "代表接取不含未消费的赠品正文：" + id);
            peer.Send(31, [31, 0, (byte)id, (byte)(id >> 8)]);
            var ack = peer.Expect(1, 31).Body;
            That(ack.Length == 8 && ack[0] == 1 && U16(ack, 1) == id &&
                U32(ack, 3) == (scalar == uint.MaxValue ? 1 : scalar) && ack[7] == 0,
                "真实31完整ACK绑定原任务与其类型scalar：" + id);
            drain(peer, null);
            That(Scalar(peer.Id, id) == scalar && Guid.TryParseExact(Identity(peer.Id, id), "N", out var acceptance) && acceptance != Guid.Empty,
                "31实际提交非空原接取UUID与内部条件：" + id);
        }

        void RejectAccept(TcpPeer peer, ushort id)
        {
            string before = State(peer.Id);
            peer.Send(31, [31, 0, (byte)id, (byte)(id >> 8)]); peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
            That(State(peer.Id) == before, "拒绝无回答、错误职业或未到阶段的31，不改变角色：" + id);
        }

        void Condition(TcpPeer peer, ushort id, byte value, uint scalar)
        {
            peer.Send(33, [33, 0, (byte)id, (byte)(id >> 8), value, 0]);
            var ack = peer.Expect(1, 33).Body;
            That(ack.Length == 7 && ack[0] == 1 && U16(ack, 1) == id && U32(ack, 3) == scalar,
                "当前真实NPC33回同一任务与实际scalar：" + id);
            drain(peer, null);
        }

        void Finish(TcpPeer peer, ushort id, byte type, Action<WirePacket>? observe = null)
        {
            peer.Send(34, [34, 0, (byte)id, (byte)(id >> 8), 255, 255, 1, 0, 255, 255]);
            var ack = peer.Expect(1, 34).Body;
            That(ack.Length >= 10 && ack[0] == 1 && U16(ack, 1) == id && ack[3] == type,
                "真实34按问题/会面类型交付原任务：" + id);
            drain(peer, observe);
            That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1", peer.Id, id) is [[long doneId]] && doneId == id &&
                fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1", peer.Id, id) is [[1L]],
                "34实际提交唯一完成与领奖回执：" + id);
        }

        void EntryRound(TcpPeer peer, ushort connection, uint dungeon, ushort[] requests)
        {
            var maze = dungeons.Get(checked((int)dungeon)).Mazes.Single(row => row.ConnectsQuest(connection));
            string accepted = QuestState(peer.Id);
            long attempt = Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id).Single()[0]);
            foreach (ushort request in requests)
            {
                uint next = checked((uint)Convert.ToInt64(fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id).Single()[0]));
                var expected = Plans(maze, peer.Id, next, dungeons.Get(checked((int)dungeon)).BasisLevel)
                    .Single(plan => plan.Room.Map.Position == maze.Starts.Single());
                var map = Enter(peer, connection, dungeon, request, maze);
                Actors(peer, map, expected, connection);
                FormalDungeonTcpTests.Load(peer); peer.Barrier();
                That(QuestState(peer.Id) == accepted && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)
                        is [[long saved]] && saved == ++attempt, "每次16建立独立尝试且保留全部原任务身份：" + connection);
                Return(peer);
                That(QuestState(peer.Id) == accepted && NoClear(peer.Id) &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id) is [[0L]],
                    "入场加载退出不造任务进度、清图、战斗经验或结算：" + connection);
            }
        }

        byte[] Enter(TcpPeer peer, ushort connection, uint dungeon, ushort request, DungeonMaze maze)
        {
            SelectMap(peer); peer.Send(16, Selection(dungeon, request));
            var information = peer.Expect(0, 28).Body;
            That(information.Length == 36 && U32(information) == dungeon && information[4] == 0 &&
                information[7] == maze.SourceOrdinal && maze.ConnectsQuest(connection) &&
                information[8] == maze.Bosses.Single().X && information[9] == maze.Bosses.Single().Y,
                $"真实28保留任务迷宫与Boss坐标：QID={connection} request={request}");
            var map = peer.Expect(0, 29).Body;
            That(map.Length >= 23 && U32(map, 14) == maze.MapSpecification!.Single(row => row.Position == maze.Starts.Single()).Id,
                $"真实29从本任务原起点加载：QID={connection} request={request}");
            return map;
        }

        void Return(TcpPeer peer)
        {
            peer.Send(42); peer.Expect(1, 42, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]); peer.Expect(0, 24);
            peer.Expect(0, 21); peer.Expect(0, 356); peer.Barrier();
        }

        DungeonMonsterPlan[] Plans(DungeonMaze maze, ushort character, uint next, int? basis)
        {
            var layers = new Dictionary<DungeonPoint, int>();
            var normalized = maze.LayeredMaps.Select(map =>
            {
                That(map.CandidateIds.Count <= 1, "代表层只使用已证单候选，不随机改图");
                int index = layers.GetValueOrDefault(map.Position); layers[map.Position] = index + 1;
                return map with { LayerIndex = index };
            });
            var plans = new List<DungeonMonsterPlan>();
            foreach (var map in maze.MapSpecification!.Concat(normalized).Concat(maze.ConditionalMaps))
            {
                var plan = DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), character,
                    checked((ushort)next), basis, playerTeam: 0);
                plans.Add(plan);
                if (plan.Monsters.Count != 0 || plan.SpecialObjects.Count != 0) next = plan.NextObjectKey;
            }
            return plans.ToArray();
        }

        Location Entrance(ushort id, uint dungeon, HashSet<int> done)
        {
            int level = resources.Quests.Get(id).MinimumLevel;
            var area = resources.Towns.Areas.Values.First(row => row.Level <= level && row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
            OpenArea(area, done);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            return Interior(area, done);
        }

        Location Npc(int id, byte level, HashSet<int> done)
        {
            foreach (var area in resources.Towns.Areas.Values.Where(row => row.Level <= level))
            {
                var candidate = done.ToHashSet(); OpenArea(area, candidate);
                if (resources.Towns.DestinationAdmission(new(area.Town, area.Area, 0, 0), level, candidate) != "ready" ||
                    !area.Map(candidate).Npcs.Any(npc => npc.Id == id)) continue;
                done.UnionWith(candidate); return Interior(area, done);
            }
            throw new InvalidDataException("代表没有真实可达NPC场景：" + id);
        }

        void TravelTo(TcpPeer peer, Location target)
        {
            var done = fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0", peer.Id)
                .Select(row => checked((int)(long)row[0])).ToHashSet();
            byte level = checked((byte)(long)fixture.Query("SELECT level FROM characters WHERE id=$p0", peer.Id).Single()[0]);
            var source = Position(peer.Id);
            var start = (source.Town, source.Area); var destination = (target.Town, target.Area);
            var queue = new Queue<(byte, byte)>(); queue.Enqueue(start);
            var previous = new Dictionary<(byte, byte), (byte, byte)> { [start] = start };
            while (queue.TryDequeue(out var current))
                foreach (var link in resources.Towns.Areas[current].Map(done).Links.Where(link => link.Gate.Door && link.Town != 255))
                {
                    var next = (link.Town, link.Area);
                    if (previous.ContainsKey(next) || resources.Towns.Admission(new(current.Item1, current.Item2, 0, 0),
                        new(link.Town, link.Area, 0, 0), level, done) != "ready") continue;
                    previous.Add(next, current); queue.Enqueue(next);
                }
            That(previous.ContainsKey(destination), "NPC与地轨入口之间存在真实城镇连接");
            var steps = new List<(byte, byte)>();
            for (var next = destination; next != start; next = previous[next]) steps.Add(next);
            steps.Reverse();
            foreach (var next in steps)
            {
                var landing = Interior(resources.Towns.Areas[next], done);
                peer.Travel(landing.Town, landing.Area, landing.X, landing.Y, source.Town, source.Area,
                    returnTown: resources.Towns.IsRoom(landing, done) ? (uint?)source.Town : null);
                source = Position(peer.Id);
            }
            peer.Send(35, Move(target.X, target.Y)); peer.Barrier();
            That(Position(peer.Id) == target, "真实35落点符合任务NPC/入口可行走区域");
        }

        void Cleanup(TcpPeer peer, Character created, string account)
        {
            // 只在本条成功后经正式删除流程清理自建角色；失败保留现场，不在finally删库。
            peer.Send(7); peer.Expect(1, 7, [1]);
            ushort ordinal = RoleIndex(peer.Expect(0, 2).Body, created);
            peer.Send(6, Build(writer => { writer.Write(ordinal); writer.Write(Name(created.Name)); }));
            peer.Expect(1, 6, Build(writer => { writer.Write((byte)1); writer.Write((byte)0); writer.Write(ordinal); }));
            peer.Expect(0, 2); peer.Send(692, Name(created.Name)); peer.Expect(1, 692, [1]);
            That(fixture.Query("SELECT count(*) FROM characters WHERE account=$p0", account) is [[0L]], "成功后只清理本代表隔离角色");
            peer.Send(3, [0]); peer.Expect(1, 3, [1]); peer.Closed();
        }

        Location Position(ushort id)
        {
            var row = fixture.Query("SELECT town,area,x,y,direction FROM characters WHERE id=$p0", id).Single();
            return new(checked((byte)(long)row[0]), checked((byte)(long)row[1]), checked((ushort)(long)row[2]),
                checked((ushort)(long)row[3]), checked((byte)(long)row[4]));
        }
        uint Scalar(ushort character, ushort quest) => checked((uint)(long)fixture.Query(
            "SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1", character, quest).Single()[0]);
        string Identity(ushort character, ushort quest) => (string)fixture.Query(
            "SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1", character, quest).Single()[0];
        string Active(ushort id) => fixture.Snapshot("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", id);
        string QuestState(ushort id) => Active(id) + fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id", id) +
            fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY revision", id);
        string State(ushort id) => QuestState(id) + fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", id) +
            fixture.Snapshot("SELECT * FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY revision", id);
        string DungeonState(ushort id) => fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", id);
        bool NoClear(ushort id) => fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0", id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0", id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0", id) is [[0L]];
    }

    static void OpenArea(TownArea area, HashSet<int> done)
    {
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0 && !area.AnyQuests.Any(done.Contains)) done.Add(area.AnyQuests[0]);
    }

    static Location Interior(TownArea area, HashSet<int> done)
    {
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        return new(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
    }

    static void SelectMap(TcpPeer peer)
    {
        peer.Send(15, new byte[4]); peer.Expect(1, 15, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]); peer.Expect(0, 27, new byte[42]);
    }

    static byte[] Selection(uint dungeon, ushort request)
    {
        var body = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
        BinaryPrimitives.WriteUInt32LittleEndian(body, dungeon);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), request);
        return body;
    }

    static byte[] MoveMap(TcpPeer peer, DungeonPoint point, byte kind)
    {
        var body = new byte[100]; body[0] = checked((byte)point.X); body[1] = checked((byte)point.Y); body[10] = kind;
        peer.Send(45, body); peer.Expect(1, 45, [1]);
        return peer.Expect(0, 29).Body;
    }

    static void Actors(TcpPeer peer, byte[] map, DungeonMonsterPlan expected, ushort quest)
    {
        That(map.Length >= 23 && map[0] == expected.Room.Map.Position.X && map[1] == expected.Room.Map.Position.Y &&
            U32(map, 14) == expected.Room.Map.Id && map[18] == expected.Monsters.Count + expected.SpecialObjects.Count,
            "真实29保留完整原房间与全部演员数量：" + quest);
        for (int at = 0; at < expected.Monsters.Count; at++)
        {
            int offset = 19 + 22 * at; var actor = expected.Monsters[at];
            That(U32(map, offset + 2) == (actor.Source?.SpecialChild?.SourceOrdinal ?? actor.SourceOrdinal) &&
                U16(map, offset + 6) == actor.ObjectKey && U32(map, offset + 8) == actor.MonsterId &&
                map[offset + 12] == actor.CandidateLevel && map[offset + 13] == actor.SpawnType &&
                map[offset + 21] == (actor.IsSpecialChild || actor.IsFriendly ? 1 : 0),
                "29逐行核原ordinal、独立key、模板、等级、类型与清房排除位：" + quest);
        }
        for (int at = 0; at < expected.SpecialObjects.Count; at++)
        {
            int offset = 19 + 22 * (expected.Monsters.Count + at); var actor = expected.SpecialObjects[at];
            That(U32(map, offset + 2) == actor.Source.SourceOrdinal && U16(map, offset + 6) == actor.ObjectKey &&
                U32(map, offset + 8) == actor.Source.ObjectId && map[offset + 13] == 9 && map[offset + 21] == 1,
                "29特殊父对象保留原资源和独立类型：" + quest);
        }
        var keys = Enumerable.Range(0, map[18]).Select(at => U16(map, 25 + 22 * at)).ToArray();
        That(keys.All(key => key != peer.Id) && keys.Distinct().Count() == keys.Length, "29全部对象key唯一且不碰撞本人");
    }

    static ushort RoleIndex(byte[] roster, Character role)
    {
        int at = 15;
        for (ushort ordinal = 0; ordinal < U16(roster, 13); ordinal++)
        {
            int width = checked((int)U32(roster, at + 2));
            if (U16(roster, at) == role.Slot && DecodeName(roster, at + 6, width) == role.Name) return ordinal;
            int equipment = at + 21 + width;
            at = equipment + AssetWireChecks.ActorEntries(roster, equipment).ConsumedBytes + 48;
        }
        throw new InvalidDataException("本代表自建角色不在实际名册中：" + role.Name);
    }
}
