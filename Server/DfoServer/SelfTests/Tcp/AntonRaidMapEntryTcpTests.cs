using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Raids;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidMapEntryTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        // 与Runtime使用同一R2有证增量；普通目录仍保持原值，不替换DGN/MAP内容。
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var raid = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items).Find(0)!.Resources!;
        var original = RaidDungeonCatalog.Load(resources.Archive, raid, main);
        That(original.MissingMaps.Length == 72 && original.Find(210) is null && !main.ContainsDungeon(210),
            "原始主表仍缺72项MAP；不能靠备用DGN或测试显式登记污染生产目录");
        var registeredDefinition = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items,
            antonMapPath: AntonRaidMapPaths.Bind(main.MapPath)).Find(0)!;
        var registered = registeredDefinition.Resources!;
        var catalog = registered.EntryCatalog!;
        That(catalog.Find(210) is { Mazes.Count: 1 } && catalog.MissingMaps.IsEmpty && catalog.Ready && registered.MissingFormalMaps.IsEmpty,
            "同应用有证登记使14DGN/72MAP可准备，黑雾真实28/29不依赖测试造表");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-map-entry"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var stats = new CharacterStatComputer(resources.Characters);
        using var log = new ServerLog(fixture.Options.LogDirectory);
        var town = new TownProjection(resources.Towns, stats, null, resources.ItemResources.EpicPieces);
        var commands = new GameCommandRegistry();
        new DungeonSelectionHandler(town, log, dungeons: main).Register(commands);
        new DungeonHandler(log, main, SelfTestCatalogs.Rooms(resources.Archive), resources.Growth,
            SelfTestCatalogs.KillExperience(resources.Archive), town, new CharacterLevelProjection(stats)).Register(commands);
        new KeepaliveHandler(log).Register(commands);
        commands.Seal();
        var room = resources.Towns.Areas[(19, 2)].Map(new HashSet<int> { 12829, 3779 });
        var bounds = room.Bounds.First().Bounds;
        var waiting = new Location(19, 2, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        bool started = true;
        Guid instance = Guid.NewGuid();
        var sessions = new List<GameSession>();
        var leases = new List<AccountOwners.Lease>();
        var clients = new List<TcpPeer>();
        var workers = new List<Task>();
        using var stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var owner = owners.Acquire("antonmap" + i, () => { });
                leases.Add(owner);
                var candidate = CharacterCreation.Prepare("AntonMap" + i, resources.Characters.Get(0), waiting, resources.Towns.InitialReturn, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
                var character = repository.Create(owner, candidate with { Level = 85, AcceptedStoryLevel = 85,
                    InitialLearning = learning, CompletedQuests = [12829, 3779] });
                var history = new ChannelHistory();
                history.Record(owner.Account, 23);
                var session = new GameSession(repository, resources.Towns, owner,
                    entry: new(new(2, 19), 23, "[raid]"), channelHistory: history);
                session.Select(character.Slot, resumeTown: true);
                session.MarkReady();
                sessions.Add(session);
                var actor = new RaidActor(session, owner, session.SelectionEpoch, character.Id, character.Name, new(2, 19));
                // 重登先清理实例地区；此夹具显式安装已提交开战位置，不借存档恢复候战资格。
                var beforeStart = session.Selected!;
                var committedStart = repository.Save(owner, beforeStart, waiting, beforeStart.Return);
                session.InstallRaidStart(new(actor, beforeStart, waiting,
                    new(beforeStart.Inventory, beforeStart.Inventory, "anton_map_fixture_start")), committedStart);
                That(session.Selected!.Position == waiting && session.AuthorizeRaidDungeon(new(instance, actor, 0, (byte)(i + 1),
                    catalog, () => started, id => id == 210)), "夹具从原owner、候战室及已开战实例绑定分队入口");
                var accept = listener.AcceptTcpClientAsync(stop.Token);
                var client = new TcpPeer(((IPEndPoint)listener.LocalEndpoint).Port, channelNumber: 19);
                clients.Add(client);
                var connection = await accept;
                workers.Add(ServeAsync(connection, owner, session));
            }
            listener.Stop();
            byte[] selection = Convert.FromHexString("D20000000200000000FFFF0000000000000000000000000000");
            foreach (var peer in clients)
            {
                peer.Send(15, new byte[4]);
                peer.Expect(1, 15, [1]);
                ushort actorId = sessions[clients.IndexOf(peer)].Selected!.Id;
                peer.Expect(0, 3, [1, (byte)actorId, (byte)(actorId >> 8), 1]);
                peer.Expect(0, 27, new byte[42]);
                peer.Send(15, BitConverter.GetBytes(210u));
                peer.Expect(1, 15, [1]);
                peer.Expect(0, 3, [1, (byte)actorId, (byte)(actorId >> 8), 1]);
            }
            That(sessions.All(session => session.Phase == SessionPhase.MapSelect), "真实15应答后全员已建立选图和候战室返回锚点");
            string before = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
            fixture.Sql($"CREATE TRIGGER anton_map_rollback BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={sessions[1].Selected!.Id} BEGIN SELECT RAISE(ABORT,'anton map rollback'); END");
            // 第一分队与第二分队同时请求；第二笔注入SQL失败不得回滚已成功的另一分队或发布假成功。
            clients[0].Send(16, selection);
            clients[1].Send(16, selection);
            var information = clients[0].Expect(0, 28).Body;
            byte[] expectedInfo = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(expectedInfo, 210);
            expectedInfo[4] = 2; expectedInfo[8] = 3; expectedInfo[10] = expectedInfo[11] = 255;
            BinaryPrimitives.WriteUInt32LittleEndian(expectedInfo.AsSpan(20), uint.MaxValue);
            Bytes(information, expectedInfo, "正式黑雾沿同一36B NOTI28布局，难度2和Boss坐标来自实际DGN");
            byte[] map = clients[0].Expect(0, 29).Body;
            That(map[0] == 0 && map[1] == 0 && map[2] == 0 && map[13] == 1 && U32(map, 14) == 32200,
                "完整NOTI29采用已登记32200起点、原生地图操作及随机种子");
            var receipt = sessions[0].CurrentFormalDungeonScene()!;
            That(receipt.Entry.RaidEntry == new RaidDungeonIdentity(instance, 0, 1) &&
                receipt.Monsters.Monsters.Count == map[18] && map.Length == 23 + map[18] * 22,
                "已提交Run保留攻坚实例/阶段/分队，起点所有演员按原22B逐项发送");
            for (int i = 0; i < receipt.Monsters.Monsters.Count; i++)
            {
                var monster = receipt.Monsters.Monsters[i]; int at = 19 + i * 22;
                That(U16(map, at + 6) == monster.ObjectKey && U32(map, at + 8) == monster.MonsterId &&
                    map[at + 12] == monster.CandidateLevel && map[at + 13] == monster.SpawnType,
                    "每只演员key/模板/等级/类型来自本次实际资源准备");
            }
            clients[1].Closed();
            await workers[1].WaitAsync(TimeSpan.FromSeconds(5));
            That(sessions[1].Phase == SessionPhase.MapSelect && sessions[1].DungeonSnapshot is null &&
                repository.LoadCharacter(leases[1], sessions[1].Selected!.Slot).Revision == sessions[1].Selected!.Revision,
                "SQL失败保持原选图、revision及无实例，不向第二分队发布28/29");
            string committed = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
            That(committed != before && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", sessions[0].Selected!.Id) is [[1L]] &&
                fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", sessions[1].Selected!.Id) is [[0L]],
                "两分队入场事务隔离，失败分队不写尝试号，成功分队仅预留一次");
            clients[0].Send(16, selection);
            Barrier(clients[0]);
            That(fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == committed,
                "加载中的重复16不重新分配对象、重复预留或重复发送初始化");
            clients[0].Send(37, new byte[8]);
            clients[0].Expect(1, 37, [1]); clients[0].Expect(0, 30, new byte[5]);
            ushort id0 = sessions[0].Selected!.Id;
            clients[0].Expect(0, 3, [1, (byte)id0, (byte)(id0 >> 8), 1]);
            That(sessions[0].Phase == SessionPhase.Dungeon && sessions[0].DungeonSnapshot!.CurrentMap.Id == 32200,
                "真实37完成加载后按ACK37→NOTI30→USER_STATE进入正式黑雾房间");
            clients[0].Send(37, new byte[8]); Barrier(clients[0]);
            That(fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == committed &&
                fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts") is [[0L]],
                "重复37不重复初始化；DGN的no fatigue语义保留且进图不扣开战门票");
            started = false;
            That(!sessions[0].CanPublishFormalDungeon(receipt) && !sessions[0].AuthorizeRaidDungeon(new(instance, new(sessions[0], leases[0], sessions[0].SelectionEpoch,
                id0, "AntonMap0", new(2, 19)), 0, 1, catalog, () => started, _ => true)),
                "原实例撤销后旧入场回执不能发布，旧授权不能重绑正在运行的会话");
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            foreach (var peer in clients) peer.Dispose();
            await Task.WhenAll(workers);
            foreach (var session in sessions) session.End();
            foreach (var owner in leases) owner.Dispose();
        }
        await VerifyFormalSquadsAsync(source, root, resources, registeredDefinition);
        await VerifyPhaseProgressAsync(source, root, resources, registeredDefinition);

        async Task ServeAsync(TcpClient socket, AccountOwners.Lease owner, GameSession session)
        {
            using (socket)
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var stream = socket.GetStream();
                var connection = new ConnectionContext(100 + sessions.IndexOf(session), IPAddress.Loopback, stream, lifetime);
                await using var sender = new GameConnectionSender(owner, stream, lifetime, 0, (_, _, _, _, _) => { });
                var context = new GameConnectionContext(connection, owner, session, sender);
                try
                {
                    while (!stop.IsCancellationRequested)
                        await commands.DispatchAsync(context, GameFrame.ParseClient(await connection.ReadAsync()));
                }
                catch (SqliteException error) when (error.Message.Contains("anton map rollback", StringComparison.Ordinal)) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) when (stop.IsCancellationRequested) { }
            }
        }
    }

    static void Barrier(TcpPeer peer)
    {
        peer.Send(1278);
        peer.Expect(1, 1278, [1]);
    }
}
