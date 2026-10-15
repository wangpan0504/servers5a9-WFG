using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
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
        VerifySequential();
        VerifyMovement(7958, 311, 95, new(2, 0), 655666, 655667);
        VerifyMovement(3969, 311, 88, new(3, 0), 76886, null);
        Console.WriteLine($"PASS {Count - checks} 地轨代表TCP：真实OR回答、3971父子阶段、7958叠层和3969移动（不代表59条剧情通关）");

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

        void VerifySequential()
        {
            var parent = resources.Quests.Get(3971);
            That(QuestRelationProgress.SequentialParent(parent, resources.Quests) && parent.IntData.SequenceEqual([3972, 3973]) &&
                resources.Quests.Get(3972).IntData is [455], "真实3971顺序资源与NPC455会面不改定义");
            var done = parent.Definition!.PrerequisiteGroups.Single().ToHashSet();
            var entrance = Entrance(3973, 310, done);
            var meeting = Npc(455, 88, done);
            That(!done.Overlaps([3971, 3972, 3973]), "父子准备只包含3970和入口资格");
            const string account = "metro-sequence";
            var created = Create(account, "MetroSeq", 0, 88, done, meeting);
            using var peer = Open(fixture, account);
            Select(peer, created);
            Accept(peer, 3971, 2);
            string parentIdentity = Identity(peer.Id, 3971);
            RejectAccept(peer, 3973);
            TravelTo(peer, entrance);
            SelectMap(peer);
            string blocked = State(peer.Id) + DungeonState(peer.Id);
            peer.Send(16, Selection(310, 3971)); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(State(peer.Id) + DungeonState(peer.Id) == blocked,
                "当前3972会面阶段拒绝父请求借后继迷宫，不预留key或attempt");
            peer.Send(132); peer.Expect(1, 132, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
            TravelTo(peer, meeting);
            Accept(peer, 3972, 1);
            Condition(peer, 3972, 0, 0);
            That(Scalar(peer.Id, 3971) == 2 && Identity(peer.Id, 3971) == parentIdentity,
                "3972真实NPC33先保存会面，父身份及顺序仍等子34");
            Finish(peer, 3972, 4);
            That(Scalar(peer.Id, 3971) == 1 && Identity(peer.Id, 3971) == parentIdentity &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3972", peer.Id) is [[3972L]],
                "实际3972交付将原父scalar2推进为1，不换接取UUID");
            TravelTo(peer, entrance);
            EntryRound(peer, 3973, 310, [3971, 0]);
            That(fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3973", peer.Id).Length == 0,
                "父请求及普通0只借当前子路线，不伪造3973接取");
            Accept(peer, 3973, 1);
            EntryRound(peer, 3973, 310, [3973, 0]);
            That(Scalar(peer.Id, 3971) == 1 && Scalar(peer.Id, 3973) == 1 && Identity(peer.Id, 3971) == parentIdentity,
                "两次子路线入场未清图，不归零父子或发父奖励");
            Cleanup(peer, created, account);
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

        void Finish(TcpPeer peer, ushort id, byte type)
        {
            peer.Send(34, [34, 0, (byte)id, (byte)(id >> 8), 255, 255, 1, 0, 255, 255]);
            var ack = peer.Expect(1, 34).Body;
            That(ack.Length >= 10 && ack[0] == 1 && U16(ack, 1) == id && ack[3] == type,
                "真实34按问题/会面类型交付原任务：" + id);
            drain(peer, null);
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
