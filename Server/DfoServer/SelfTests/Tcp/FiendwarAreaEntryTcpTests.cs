using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using Microsoft.Data.Sqlite;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 匹配原生确认窗：只用36进入遭遇，不能靠假客户端补15/16掩盖入口缺失。
internal static partial class FiendwarAreaEntryTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        foreach (bool guide in new[] { false, true })
            await VerifyAsync(source, Path.Combine(root, guide ? "guide" : "normal"), resources, guide);
        foreach (int probe in new[] { 1, 2, 3 })
            await VerifyAsync(source, Path.Combine(root, "boundary" + probe), resources, false, probe);
    }

    static async Task VerifyAsync(ServerOptions source, string root, TcpTestResources resources, bool guide, int probe = 0)
    {
        var clock = new LukeRaidClockTests.Clock();
        await using var fixture = new TcpTestFixture(source, root, resources: resources, clock: clock);
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        int memberCount = guide ? 1 : 4;
        for (int index = 0; index < memberCount; index++)
        {
            using var owner = new AccountOwners().Acquire("fwareaentry" + index, () => { });
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("FwAreaEntry" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            var character = repository.Create(owner, candidate with
                { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8672], InitialLearning = learning });
            var items = resources.ItemResources;
            InventoryService.Commit(repository, owner, character,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(character,
                    [new(10306760, 2), new(10309054, 2)]));
        }
        var modes = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!.Resources!;
        var activeMode = guide ? modes.FiendwarGuide! : modes;
        var script = activeMode.Fiendwar!;
        var members = new List<TcpPeer>();
        var enemyAreas = new HashSet<int>();
        var monsterAreas = new HashSet<int>();
        int current = 1;
        try
        {
        for (int index = 0; index < memberCount; index++)
        {
            var member = Open(fixture, "fwareaentry" + index, gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39);
            members.Add(member);
            member.Select(); ReadWithKeys(member, "native36 selected");
        }
        var peer = members[0];
        peer.Send(1426); Read("recruitment");
        peer.Send(664, Build(writer =>
        {
            writer.Write((byte)2); writer.Write(Name("FwArea"));
            writer.Write(new byte[] { guide ? (byte)1 : (byte)0, 0, 0, 7 });
        }));
        That(Read("create").Any(row => row is { Kind: 1, Opcode: 664, Body: [1, ..] }), "真实664创建待开战名册");
        foreach (var member in members.Skip(1))
        {
            peer.Send(10, AntonRaidTcpTests.Peer(member.Id, 0, true));
            Read("invite"); ReadWithKeys(member, "native36 invitation");
            member.Send(11, AntonRaidTcpTests.Peer(peer.Id, 0, false));
            That(ReadWithKeys(member, "native36 accepted").Any(row => row is { Kind: 1, Opcode: 11, Body: [1, ..] }),
                "四名真实连接分别加入普通攻坚名册");
            foreach (var other in members.Where(other => other != member)) ReadWithKeys(other, "native36 roster");
        }
        Move(guide ? 33 : 2, expectEncounter: false);
        peer.Send(666);
        That(Read("start").Any(row => row is { Kind: 1, Opcode: 666, Body: [1] }), "真实666开战并提交费用");
        foreach (var member in members.Skip(1))
            That(ReadWithKeys(member, "native36 member started").Any(row => row is { Kind: 0, Opcode: 1522 }),
                "全部真实成员进入同一Started战场");

        // 真实1526/1527决定本轮随机兵线；只经过无遭遇的区域，到达首个有真实敌人的目的地。
        int target = -1;
        var previous = new Dictionary<int, int> { [current] = -1 };
        var queue = new Queue<int>(); queue.Enqueue(current);
        while (queue.TryDequeue(out int at) && target < 0)
            foreach (int next in script.Areas[at].Neighbours.Where(next => next is not (30 or 31)))
            {
                if (previous.ContainsKey(next)) continue;
                if (enemyAreas.Contains(next) || monsterAreas.Contains(next))
                { previous.Add(next, at); target = next; break; }
                if (script.Areas[next].Roles.Any(role => role != "[ally area]")) continue;
                previous.Add(next, at); queue.Enqueue(next);
            }
        That(target > 0 && script.Areas[target].DungeonId > 0,
            "普通随机战场与官方引导均从实际1526/1527找到首个可达的真实敌军遭遇");
        var path = new List<int>();
        for (int at = target; at != current; at = previous[at]) path.Add(at);
        path.Reverse();
        List<WirePacket> entered = [];
        foreach (int next in path) entered = Move(next, expectEncounter: next == target);
        if (probe != 0) return;
        int dungeon = script.Areas[target].DungeonId;
        That(entered.TakeWhile(row => row is not { Kind: 0, Opcode: 27 })
                .Count(row => row is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode } && row.Body[4] == (byte)FiendwarKeyLeaseKind.Offer) == 1,
            "首次27之前仅36触发一份真实FWL1准备offer，不用15/16启动准备");
        That(entered.Count(row => row is { Kind: 0, Opcode: 27 }) == 1 &&
            entered.Count(row => row is { Kind: 0, Opcode: 28 }) == 1 && entered.Count(row => row is { Kind: 0, Opcode: 29 }) == 1,
            "普通和官方引导在真实grant/ready后都恰好建立一次完整副本场景");
        That(entered.FindIndex(row => row is { Kind: 0, Opcode: 27 }) < entered.FindIndex(row => row is { Kind: 0, Opcode: 28 }) &&
            entered.FindIndex(row => row is { Kind: 0, Opcode: 28 }) < entered.FindIndex(row => row is { Kind: 0, Opcode: 29 }),
            "36入口保留场景27、地下城28及地图29顺序");
        That(U32(entered.Single(row => row is { Kind: 0, Opcode: 28 }).Body) == (uint)dungeon,
            "入场地下城取本模式真实区域目录");
        var map = entered.Single(row => row is { Kind: 0, Opcode: 29 }).Body;
        var info = entered.Single(row => row is { Kind: 0, Opcode: 28 }).Body;
        var specification = activeMode.EntryCatalog!.Find(dungeon)!.Mazes.Single(row => row.SourceOrdinal == info[7])
            .MapSpecification!.Single(row => row.Position.X == map[0] && row.Position.Y == map[1]);
        var actualMap = specification with { Id = checked((int)U32(map, 14)),
            Path = SelfTestCatalogs.Dungeons(resources.Archive).MapPath(checked((int)U32(map, 14)))! };
        var room = SelfTestCatalogs.Rooms(resources.Archive).Get(actualMap);
        That(Enumerable.Range(0, map[18]).Any(index =>
        {
            int at = 19 + index * 22;
            int ordinal = checked((int)U32(map, at + 2));
            return map[at + 13] < 5 && map[at + 21] == 0 && ordinal < room.Monsters.Count &&
                room.Monsters[ordinal] is { MonsterId: > 0, SpecialChild: null } monster && U32(map, at + 8) == monster.MonsterId;
        }), "正式29携带与候选真实MAP ordinal和怪物ID相符的敌军，不能用APC对象代替有怪证据");
        var position = peer.SavedTownPosition();
        peer.Send(36, Area(57, checked((byte)target), position.X, position.Y, 57, checked((ushort)target)));
        That(!Read("duplicate while loading").Any(row => row is { Kind: 0, Opcode: 27 or 28 or 29 or FiendwarKeyLeaseRequests.Opcode }),
            "加载期重复36不能重新准备、重发场景或另建Run");
        if (!guide)
        {
            VerifyTargetGuards(ProbeSessions(), dungeon, resources);
            VerifyCommitWindows(ProbeSessions(), fixture, clock, resources);
        }
        }
        finally { foreach (var member in members) member.Dispose(); }

        List<WirePacket> Move(int destination, bool expectEncounter)
        {
            var from = new Location(57, checked((byte)current), 0, 0);
            var to = new Location(57, checked((byte)destination), 0, 0);
            var arrival = resources.Towns.Entrance(from, to, new HashSet<int> { 8672 })
                ?? resources.Towns.Spawn(to, new HashSet<int> { 8672 })!;
            // 来源不匹配的失败36不能启动任何正式准备，也不更新已提交位置。
            var peer = members[0];
            var before = peer.SavedTownPosition();
            peer.Send(36, Area(57, checked((byte)destination), arrival.X, arrival.Y, 57, ushort.MaxValue));
            That(!Read("stale source").Any(row => row is { Kind: 0, Opcode: 23 or 27 or 28 or 29 or FiendwarKeyLeaseRequests.Opcode }) &&
                peer.SavedTownPosition() == before, "失败36不改位置、不发选图或对象键offer");
            if (expectEncounter)
            {
                string snapshot = fixture.Snapshot("SELECT id,revision,town,area,x,y,return_town,return_area FROM characters ORDER BY id");
                fixture.Sql($"CREATE TRIGGER fail_native36_last BEFORE UPDATE OF area ON characters WHEN NEW.id={members[^1].Id} AND NEW.area={destination} " +
                    "BEGIN SELECT RAISE(ABORT,'native36 final member rollback'); END");
                peer.Send(36, Area(57, checked((byte)destination), arrival.X, arrival.Y, 57, checked((ushort)current)));
                foreach (var member in members)
                    That(!ReadWithoutKeys(member).Any(row => row is { Kind: 0, Opcode: 23 or 27 or 28 or 29 or FiendwarKeyLeaseRequests.Opcode }),
                        "最后成员SQL保存失败，整队均无成功切图或对象键准备");
                That(snapshot == fixture.Snapshot("SELECT id,revision,town,area,x,y,return_town,return_area FROM characters ORDER BY id"),
                    "普通四人和引导最终UPDATE失败均完整回滚全员位置、revision与返回点");
                fixture.Sql("DROP TRIGGER fail_native36_last");
                if (probe == 1)
                {
                    var writerGate = (SemaphoreSlim)typeof(SqliteWriteGate).GetField("Gate",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var waiters = typeof(SemaphoreSlim).GetField("m_waitCount", flags) ??
                        typeof(SemaphoreSlim).GetField("_waitCount", flags)!;
                    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                        { DataSource = fixture.Database, Pooling = false }.ToString()))
                    {
                        db.Open();
                        using var held = db.BeginWrite("FiendwarAreaEntryTcpTests.DeadlineWriterHold");
                        using var transaction = held.Transaction;
                        peer.Send(36, Area(57, checked((byte)destination), arrival.X, arrival.Y, 57, checked((ushort)current)));
                        That(SpinWait.SpinUntil(() => Convert.ToInt32(waiters.GetValue(writerGate)) > 0,
                            TimeSpan.FromSeconds(10)), "真实整队36已冻结成员并进入原writer等待");
                        clock.AdvanceMillisecondsWithoutCallbacks(3610 * 1000);
                    }
                    foreach (var member in members)
                        That(!ReadWithoutKeys(member).Any(row => row is { Kind: 0, Opcode: 23 or 27 or 28 or 29 or FiendwarKeyLeaseRequests.Opcode }),
                            "writer等待跨截止整队拒绝，原连接仍可1278且没有成功入场帧");
                    That(snapshot == fixture.Snapshot("SELECT id,revision,town,area,x,y,return_town,return_area FROM characters ORDER BY id"),
                        "writer跨截止的最终复验使全员位置、revision、返回点完整回滚");
                    return [];
                }
                if (probe is 2 or 3)
                {
                    var sessions = ProbeSessions();
                    var property = typeof(GameSession).GetProperty("FiendwarAreaTravelArrived",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    var original = (Func<TownTravelReceipt, Task<bool>>)property.GetValue(sessions[^1])!;
                    property.SetValue(sessions[^1], (Func<TownTravelReceipt, Task<bool>>)(travel => probe == 2
                        ? original(travel with { SelectionEpoch = travel.SelectionEpoch + 1 })
                        : Task.FromException<bool>(new IOException("Controlled area publication failure"))));
                    peer.Send(36, Area(57, checked((byte)destination), arrival.X, arrival.Y, 57, checked((ushort)current)));
                    foreach (var member in members.Take(probe == 3 ? members.Count - 1 : members.Count))
                        That(!ReadWithoutKeys(member).Any(row => row is { Kind: 0, Opcode: 27 or 28 or 29 or FiendwarKeyLeaseRequests.Opcode }),
                            "任一23守卫失效或发送失败均不准备FWL1，也不向存活成员发布新场景");
                    That(sessions.Take(probe == 3 ? sessions.Length - 1 : sessions.Length).All(session =>
                            session.Phase == SessionPhase.Town && session.CurrentFiendwarKeyPreparation() is null) &&
                        sessions[^1].Phase is SessionPhase.Town or SessionPhase.Unselected &&
                        members.All(member => member.SavedTownPosition().Area == destination),
                        "23失败保留一次已提交全员位置，全部成员不残留MapSelect或共享pending");
                    That(fixture.Query("SELECT revision FROM characters ORDER BY id").Select(row => Convert.ToInt64(row[0])).SequenceEqual(
                        System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(snapshot).EnumerateArray().Select(row => row[1].GetInt64() + 1)),
                        "23发布失败不重跑或回滚已提交SQL，每名成员只递增一次revision");
                    return [];
                }
            }
            var early = members.Select(_ => new List<WirePacket>()).ToArray();
            TaskCompletionSource? publishing = null, release = null;
            if (expectEncounter && !guide && probe == 0)
            {
                publishing = new(TaskCreationOptions.RunContinuationsAsynchronously);
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var enteredPublish = publishing;
                var releasePublish = release;
                var property = typeof(GameSession).GetProperty("FiendwarAreaTravelArrived",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var session = ProbeSessions()[^1];
                var original = (Func<TownTravelReceipt, Task<bool>>)property.GetValue(session)!;
                property.SetValue(session, (Func<TownTravelReceipt, Task<bool>>)(async travel =>
                {
                    enteredPublish.TrySetResult();
                    await releasePublish.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    return await original(travel);
                }));
            }
            foreach (var member in expectEncounter ? members.Take(1) : members)
                member.Send(36, Area(57, checked((byte)destination), arrival.X, arrival.Y, 57, checked((ushort)current)));
            if (publishing is not null)
            {
                publishing.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                try
                {
                    for (int index = 1; index < members.Count; index++)
                    {
                        early[index].AddRange(ReadWithoutKeys(members[index]));
                        That(!early[index].Any(row => row is { Kind: 0, Opcode: 27 or 28 or 29 or FiendwarKeyLeaseRequests.Opcode }),
                            "末成员23发布暂停时其他原连接仍能完成1278，不持全组命令门禁也不提前准备副本");
                    }
                }
                finally { release!.TrySetResult(); }
            }
            var batches = members.Select(member => expectEncounter ? ReadWithoutKeys(member) :
                ReadWithKeys(member, "native36 move " + destination)).ToArray();
            for (int index = 0; index < batches.Length; index++) batches[index].InsertRange(0, early[index]);
            var rows = batches[0];
            That(rows.Any(row => row is { Kind: 0, Opcode: 23 } && row.Body[2] == 57 && row.Body[3] == destination),
                "原生36先确认真实城镇区域移动");
            if (expectEncounter)
            {
                for (int index = 0; index < members.Count; index++)
                {
                    var frames = batches[index];
                    var offers = frames.Where(row => row is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode }).ToArray();
                    That(offers is [{ Body.Length: 80 }] && offers[0].Body[4] == (byte)FiendwarKeyLeaseKind.Offer &&
                        !frames.Any(row => row is { Kind: 0, Opcode: 27 or 28 or 29 }),
                        "一人发送36后全部同区成员各收到一份offer；真实ready前均无场景");
                    int landing = frames.FindIndex(row => row is { Kind: 0, Opcode: 23 } && U16(row.Body) == members[index].Id);
                    That(landing >= 0 && landing < frames.IndexOf(offers[0]) &&
                        members[index].SavedTownPosition().Area == destination,
                        "所有成员真实位置提交与各自23均先于FWL1 offer");
                    if (index + 1 < members.Count) AntonTcpProbe.ProcessFiendwarKeyNotice(members[index], offers[0]);
                }
                if (members.Count > 1)
                {
                    for (int index = 0; index < members.Count - 1; index++)
                    {
                        batches[index].AddRange(ReadWithKeys(members[index], "native36 waiting last ready"));
                        That(!batches[index].Any(row => row is { Kind: 0, Opcode: 27 or 28 or 29 }),
                            "最后成员尚未ready时其他真实连接不能先建图或冻结成单人Run");
                    }
                }
                var lastOffer = batches[^1].Single(row => row is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode });
                AntonTcpProbe.ProcessFiendwarKeyNotice(members[^1], lastOffer);
                AntonTcpProbe.CompleteFiendwarEntry(members.ToArray(), batches,
                    member => ReadWithKeys(member, "native36 grant and ready"));
                var firstMap = rows.Single(row => row is { Kind: 0, Opcode: 29 }).Body;
                foreach (var frames in batches)
                {
                    That(frames.Count(row => row is { Kind: 0, Opcode: 27 }) == 1 && frames.Count(row => row is { Kind: 0, Opcode: 28 }) == 1,
                        "全部成员恰好一次正式27/28/29入场");
                    Bytes(frames.Single(row => row is { Kind: 0, Opcode: 29 }).Body, firstMap,
                        "同区四人共同入场共享真实地图、种子与怪物键");
                    var offers = frames.Where(row => row is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode } &&
                        row.Body[4] == (byte)FiendwarKeyLeaseKind.Offer).ToArray();
                    int mai = frames.FindIndex(row => row is { Kind: 0, Opcode: 32752 });
                    int scene = offers.Length == 2 ? frames.IndexOf(offers[1]) : -1;
                    That(offers.Length == 2 && U32(offers[0].Body, 64) == 0 && mai >= 0 && scene > mai &&
                        scene < frames.FindIndex(row => row is { Kind: 0, Opcode: 29 }) &&
                        U32(offers[1].Body, 64) == U32(frames[mai].Body, 4) && U32(offers[1].Body, 64) > 0,
                        "准备offer一次；MAI之后29之前另发一次绑定真实非零serial的scene offer，不误当重复准备");
                }
            }
            if (!expectEncounter)
            {
                // 后完成成员的同屏23可能排在先完成成员1278之后，固定全员命令完成后再收尽尾界。
                foreach (var member in members) ReadWithKeys(member, "native36 all movement completed");
                That(batches.All(frames => !frames.Any(row => row is { Kind: 0, Opcode: 27 or 28 or 29 or FiendwarKeyLeaseRequests.Opcode })),
                    "未开战和空区域36保持城镇，不生成无怪的正式场景");
            }
            current = destination;
            return rows;
        }
        GameSession[] ProbeSessions()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var host = typeof(TcpTestFixture).GetField("runtimeHost", flags)!.GetValue(fixture)!;
            var protocol = host.GetType().GetFields(flags).Select(field => field.GetValue(host)).OfType<GameProtocolHandler>().Single();
            var directory = typeof(GameProtocolHandler).GetFields(flags).Select(field => field.GetValue(protocol)).OfType<OnlineSessionDirectory>().Single();
            return members.Select((_, index) => directory.Find("fwareaentry" + index)!).ToArray();
        }
        List<WirePacket> Read(string action) => ReadWithKeys(members[0], "fiendwar native36 " + action);
        List<WirePacket> ReadWithKeys(TcpPeer member, string action)
        {
            var rows = Observe(AntonTcpProbe.Barrier(member, action));
            WaitCompleted(member, unchecked((ushort)(member.NextClientSequence - 1)));
            return rows;
        }
        void WaitCompleted(TcpPeer member, ushort sequence)
        {
            // 本夹具不换连接；character+本连接sequence唯一固定这次1278尾界，不能把ACK当命令门禁释放。
            That(SpinWait.SpinUntil(() => fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "command_completed" &&
                row.GetProperty("Detail").GetProperty("Character").GetUInt16() == member.Id &&
                row.GetProperty("Detail").GetProperty("Sequence").GetUInt16() == sequence), TimeSpan.FromSeconds(10)),
                "全部1278实际完成自动刷新并释放命令门禁，随后才尝试多人36");
        }
        List<WirePacket> Observe(List<WirePacket> rows)
        {
            foreach (var packet in rows.Where(row => row is { Kind: 0, Opcode: 1526 or 1527 }))
            {
                int width = packet.Opcode == 1526 ? 10 : 14;
                That(packet.Body.Length == 1 + packet.Body[0] * width, "真实兵线通知保留完整10/14字节行");
                var areas = packet.Opcode == 1526 ? monsterAreas : enemyAreas;
                for (int i = 0; i < packet.Body[0]; i++)
                {
                    int at = 1 + i * width;
                    if (U32(packet.Body, at + 6) == 1) areas.Add(packet.Body[at]);
                    else if (U32(packet.Body, at + 6) == 2) areas.Remove(packet.Body[at]);
                    else That(false, "真实兵线仅接受原生增删操作");
                }
            }
            return rows;
        }
        List<WirePacket> ReadWithoutKeys(TcpPeer peer)
        {
            ushort sequence = peer.NextClientSequence;
            peer.Send(1278);
            var rows = new List<WirePacket>();
            for (int budget = 0; budget < 256; budget++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1278 })
                {
                    Bytes(packet.Body, [1], "原生36准备屏障保持真实成功");
                    WaitCompleted(peer, sequence);
                    return Observe(rows);
                }
                rows.Add(packet);
            }
            throw new InvalidOperationException("Native36 preparation barrier exceeded budget");
        }
    }
}
