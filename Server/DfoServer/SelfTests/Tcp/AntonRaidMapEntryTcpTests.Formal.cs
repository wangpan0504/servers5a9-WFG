using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
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
    // 使用应用同一R2有证目录验证协议链；不替代客户端资源装配和自然画面验收。
    static async Task VerifyFormalSquadsAsync(ServerOptions source, string root, TcpTestResources resources, RaidDefinition definition)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-formal-entry"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var raids = new RaidRegistry();
        var parties = new PartyService(repository);
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var original = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items).Find(0)!;
        var missing = new AntonRaidDungeonService(raids, parties, original.Resources!.EntryCatalog!, true);
        var catalog = definition.Resources!.EntryCatalog!;
        var entry = new AntonRaidDungeonService(raids, parties, catalog, true);
        var service = new PartyDungeonService(parties, repository);
        var stats = new CharacterStatComputer(resources.Characters);
        using var log = new ServerLog(fixture.Options.LogDirectory);
        var town = new TownProjection(resources.Towns, stats, null, resources.ItemResources.EpicPieces);
        var handler = new DungeonHandler(log, main, SelfTestCatalogs.Rooms(resources.Archive), resources.Growth,
            SelfTestCatalogs.KillExperience(resources.Archive), town, new CharacterLevelProjection(stats),
            partyDungeons: service, partyStats: stats, parties: parties, antonRaid: entry);
        var commands = new GameCommandRegistry();
        new DungeonSelectionHandler(town, log, dungeons: main, partyDungeons: service, antonRaid: entry).Register(commands);
        handler.Register(commands); new KeepaliveHandler(log).Register(commands); commands.Seal();
        var sessions = new List<GameSession>();
        var leases = new List<AccountOwners.Lease>();
        var peers = new List<TcpPeer>();
        var workers = new List<Task>();
        using var stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var owner = owners.Acquire("antonformal" + i, () => { }); leases.Add(owner);
                var origin = new Location(38, 0, 629, 196);
                var candidate = CharacterCreation.Prepare("AntFormal" + i, resources.Characters.Get(0), origin, origin, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
                var character = repository.Create(owner, candidate with { Level = 85, AcceptedStoryLevel = 85,
                    CompletedQuests = [12829, 3779], InitialLearning = learning });
                var items = resources.ItemResources;
                InventoryService.Commit(repository, owner, character,
                    new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(character, [new(10096296, 1)]));
                var session = new GameSession(repository, resources.Towns, owner, entry: new(new(2, 19), 23, "[raid]"));
                session.Select(0); session.MarkReady();
                session.CurrentParty = () => parties.Current(session);
                session.HasMultiplePartyMembers = () => parties.HasMultipleMembers(session);
                sessions.Add(session);
            }
            var actors = sessions.Select(session => session.CaptureRaidActor(raids)!).ToArray();
            raids.Create(actors[0], 0, "formal", "formal"u8.ToArray(), 20, 1);
            foreach (var actor in actors.Skip(1)) { raids.Invite(actors[0], actor, 0); That(raids.Respond(actor, actors[0].Character, true).Accepted, "正式入口夹具原邀请加入"); }
            for (int i = 0; i < actors.Length; i++) raids.Assign(actors[0], actors[i].Character, (byte)(i < 2 ? 1 : 2));
            var start = new AntonRaidStart(raids, parties, repository, resources.Towns, resources.ItemResources.EpicPieces);
            That(start.Start(actors[0], raids.Find(actors[0])!, definition with { StartMinimum = 1 }).Receipt is not null,
                "真实开战事务建立同Battle及2人/1人分队，不手工授权或Attach");
            That(missing.Select(sessions[0], 210, parties.Current(sessions[0]), out var rejection).Length == 0 &&
                rejection == "anton-entry-missing-maps" && sessions.All(session => session.DungeonSnapshot is null),
                "真实开战与开关开启后原目录仍按MissingMaps安全拒绝");
            for (int i = 0; i < sessions.Count; i++)
            {
                var accept = listener.AcceptTcpClientAsync(stop.Token);
                var peer = new TcpPeer(((IPEndPoint)listener.LocalEndpoint).Port, channelNumber: 19); peers.Add(peer);
                workers.Add(ServeAsync(await accept, leases[i], sessions[i]));
            }
            listener.Stop();
            byte[] request = Convert.FromHexString("D20000000200000000FFFF0000000000000000000000000000");
            string initial = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
            peers[1].Send(16, request);
            Bytes(Read(peers[1]).Single(p => p is { Kind: 1, Opcode: 16 }).Body, [0, 9, 0, 0], "非分队长不能代替队长授权");
            var hidden = (byte[])request.Clone(); hidden[0] = 211;
            peers[0].Send(16, hidden);
            That(Read(peers[0]).All(p => p.Opcode is not (28 or 29)) && sessions.All(session => session.DungeonSnapshot is null),
                "隐藏图请求拒绝且无部分Run");
            peers[0].Send(15, new byte[4]);
            That(Read(peers[0]).Any(p => p.Opcode == 27), "原15零号打开安徒恩战况表场景");
            Read(peers[1]);
            // 已由27进入选图的分队再发非零15只确认选择，不重复打开scene2。
            peers[0].Send(15, BitConverter.GetBytes(210u));
            var chosen = Read(peers[0]);
            That(chosen.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }) && chosen.All(p => p.Opcode != 27) &&
                Read(peers[1]).All(p => p.Opcode != 27), "选图内非零15不补27");
            // 末成员SQL失败不能留下首成员尝试号；另一分队随后可独立进入。
            fixture.Sql($"CREATE TRIGGER fail_formal_anton BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={actors[1].Character} BEGIN SELECT RAISE(ABORT,'formal Anton rollback'); END");
            peers[0].Send(16, request); peers[0].Closed(); await workers[0].WaitAsync(TimeSpan.FromSeconds(5));
            That(fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == initial &&
                sessions.Take(2).All(session => session.PartyDungeon is null && session.DungeonSnapshot is null),
                "全员SQL回滚、Run预留撤销，无部分28/29");
            fixture.Sql("DROP TRIGGER fail_formal_anton");
            // 在同一角色状态重连测试传输，不清除原Battle；验证失败预留没有残留。
            listener.Start();
            var reconnect = listener.AcceptTcpClientAsync(stop.Token);
            peers[0].Dispose(); peers[0] = new TcpPeer(((IPEndPoint)listener.LocalEndpoint).Port, channelNumber: 19);
            workers.Add(ServeAsync(await reconnect, leases[0], sessions[0])); listener.Stop();
            peers[0].Send(16, request);
            var first = Read(peers[0]); var other = Read(peers[1]);
            var scene = sessions[0].CurrentFormalDungeonScene()!;
            foreach (var rows in new[] { first, other })
            {
                var info = rows.Single(p => p is { Kind: 0, Opcode: 28 }).Body;
                That(info.Length == 36 && U32(info) == 210 && info[4] == 2 && info[7] == 0 && info[8] == 3 && info[9] == 0,
                    "全分队从正式Builder取得真实36B28与DGN迷宫/Boss坐标");
                var map = rows.Single(p => p is { Kind: 0, Opcode: 29 }).Body;
                That(map[13] == 1 && U32(map, 14) == 32200 && map.Length == 23 + map[18] * 22,
                    "全分队29保留真实起点与完整22B演员列表");
                Bytes(map, first.Single(p => p.Opcode == 29).Body, "共享Run同房间、seed、演员key");
            }
            That(sessions[0].PartyDungeon == sessions[1].PartyDungeon && scene.Entry.RaidEntry ==
                new RaidDungeonIdentity(sessions[0].StartedRaid!.InstanceId, 0, 1) && scene.Entry.DisableClearReward &&
                scene.Entry.DisableMonsterAndClearExperience && scene.Entry.CoinLimit == 0 && scene.Entry.AllowsCompanionAi,
                "实际Run绑定原Battle/阶段/分队且不授予普通经济与复活，保留GM同伴资源准入");
            string loadingCommitted = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
            var loadingInstance = sessions[0].PartyDungeon;
            // 现场25B请求末字段FFFFFFFF；覆盖连续两轮15/16重试及拒绝后的合法请求。
            var liveRequest = (byte[])request.Clone(); Array.Fill(liveRequest, (byte)255, 21, 4);
            peers[0].Send(16, hidden);
            Bytes(Read(peers[0]).Single(p => p is { Kind: 1, Opcode: 16 }).Body, [0, 9, 0, 0], "Loading不能借重试切换地下城");
            peers[1].Send(16, request);
            Bytes(Read(peers[1]).Single(p => p is { Kind: 1, Opcode: 16 }).Body, [0, 9, 0, 0], "Loading非队长仍拒绝");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                peers[0].Send(15, BitConverter.GetBytes(210u));
                var confirm = Read(peers[0]);
                That(confirm.Count == 1 && confirm[0] is { Kind: 1, Opcode: 15, Body: [1] },
                    "Loading重复15仅回原成功ACK，不重新发布选图场景或失败弹窗");
                peers[0].Send(16, liveRequest);
                var replay = Read(peers[0]);
                That(replay.Count == 2 && replay[0] is { Kind: 0, Opcode: 28 } && replay[1] is { Kind: 0, Opcode: 29 },
                    "Loading重复16只按原序重放28/29，无拒绝或重建名册");
                Bytes(replay[0].Body, first.Single(p => p.Opcode == 28).Body, "重放28逐字节一致");
                Bytes(replay[1].Body, first.Single(p => p.Opcode == 29).Body, "重放29的地图种子和演员逐字节一致");
                That(ReferenceEquals(sessions[0].PartyDungeon, loadingInstance) &&
                    sessions[0].CurrentFormalDungeonScene()!.Snapshot.Identity == scene.Snapshot.Identity &&
                    fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == loadingCommitted,
                    "重试保留原Run及尝试号，不新增数据库提交");
            }
            peers[2].Send(15, BitConverter.GetBytes(210u));
            var direct = Read(peers[2]);
            That(direct.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }), "原单人第二分队可沿非零15建立选择");
            // 现场09:02城镇直发15(210)后无27，客户端停在TOWN；须先发与15零号现场相同的42B空27再接28/29。
            int directState = direct.FindIndex(p => p is { Kind: 0, Opcode: 3 });
            int directScene = direct.FindIndex(p => p is { Kind: 0, Opcode: 27 });
            int directBoard = direct.FindIndex(p => p is { Kind: 0, Opcode: 585 });
            That(direct.Count(p => p is { Kind: 0, Opcode: 27 }) == 1 && directState >= 0 && directState < directScene &&
                (directBoard < 0 || directBoard < directScene), "城镇直入只补一次27，位于USER_STATE及585之后");
            Bytes(direct[directScene].Body, new byte[42], "城镇直入27与15零号现场正文逐字节一致");
            peers[2].Send(16, request);
            That(Read(peers[2]).Any(p => p.Opcode == 29) && sessions[2].PartyDungeon != sessions[0].PartyDungeon &&
                sessions[2].CurrentFormalDungeonScene()!.Entry.RaidEntry?.Squad == 2, "不同分队持独立Run，同属原Battle");
            var soloScene = sessions[2].CurrentFormalDungeonScene()!;
            peers[2].Send(15, BitConverter.GetBytes(210u));
            Bytes(Read(peers[2]).Single(p => p.Opcode == 15).Body, [1], "单人分队Loading重复15成功");
            peers[2].Send(16, liveRequest);
            That(Read(peers[2]).Select(p => p.Opcode).SequenceEqual(new ushort[] { 28, 29 }) &&
                sessions[2].CurrentFormalDungeonScene()!.Snapshot.Identity == soloScene.Snapshot.Identity,
                "单人分队重试保持独立原Run");
            peers[0].Send(37, new byte[8]);
            That(Read(peers[0]).All(p => p.Opcode != 30), "首成员加载不能提前释放共享屏障");
            peers[1].Send(37, new byte[8]);
            // 先等最后加载者处理完37；另一连接的1278不能证明该请求已经完成广播。
            foreach (var peer in new[] { peers[1], peers[0] })
            {
                var loaded = Read(peer);
                int acknowledgment = loaded.FindIndex(p => p is { Kind: 1, Opcode: 37 });
                That(acknowledgment >= 0 && acknowledgment < loaded.FindIndex(p => p is { Kind: 0, Opcode: 30 }) &&
                    loaded.Any(p => p is { Kind: 0, Opcode: 30, Body.Length: 5 }), "全员37后才发布ACK37及30");
            }
            string committed = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
            peers[0].Send(16, request);
            That(Read(peers[0]).All(p => p.Opcode is not (28 or 29)) &&
                fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == committed,
                "重复16不重复尝试、分配或28/29");
            // 原分队身份失效后，即使本地仍保留Loading Run，也不能沿幂等入口获得成功。
            raids.RemoveSession(sessions[2], sessions[2].SelectionEpoch);
            peers[2].Send(15, BitConverter.GetBytes(210u));
            Bytes(Read(peers[2]).Single(p => p is { Kind: 1, Opcode: 15 }).Body, [0, 1, 0], "Loading原团授权失效仍拒绝15");
            peers[2].Send(16, liveRequest);
            Bytes(Read(peers[2]).Single(p => p is { Kind: 1, Opcode: 16 }).Body, [0, 9, 0, 0], "Loading原团授权失效仍拒绝16");
            That(fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == committed,
                "授权失效拒绝不修改原Run账本");
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            foreach (var peer in peers) peer.Dispose();
            await Task.WhenAll(workers);
            foreach (var session in sessions) session.End();
            foreach (var owner in leases) owner.Dispose();
        }

        async Task ServeAsync(TcpClient socket, AccountOwners.Lease owner, GameSession session)
        {
            using (socket)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                var stream = socket.GetStream();
                var connection = new ConnectionContext(200 + sessions.IndexOf(session), IPAddress.Loopback, stream, lifetime);
                await using var sender = new GameConnectionSender(owner, stream, lifetime, 0, (_, _, _, _, _) => { });
                var context = new GameConnectionContext(connection, owner, session, sender); handler.PreparePartyContext(context);
                try
                {
                    while (!stop.IsCancellationRequested)
                        await commands.DispatchAsync(context, GameFrame.ParseClient(await connection.ReadAsync()));
                }
                catch (SqliteException error) when (error.Message.Contains("formal Anton rollback", StringComparison.Ordinal)) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) when (stop.IsCancellationRequested) { }
            }
        }
    }

    static List<WirePacket> Read(TcpPeer peer)
    {
        peer.Send(1278); List<WirePacket> rows = [];
        for (int i = 0; i < 256; i++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1278 }) { Bytes(packet.Body, [1], "正式入口后连接仍可处理屏障"); return rows; }
            rows.Add(packet);
        }
        throw new InvalidOperationException("安徒恩正式入口通知超过读取上限");
    }
}
