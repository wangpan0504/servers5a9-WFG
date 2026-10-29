using System.Buffers.Binary;
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
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidMapEntryTcpTests
{
    // 两个开关都打开时，沿真实 117 和 586 核对阶段 0 里不依赖计时到期的战况。不发送 596/597，也不进阶段 1。
    static async Task VerifyPhaseProgressAsync(ServerOptions source, string root, TcpTestResources resources, RaidDefinition definition)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-phase-progress"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var raids = new RaidRegistry();
        var parties = new PartyService(repository);
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var catalog = definition.Resources!.EntryCatalog!;
        var entry = new AntonRaidDungeonService(raids, parties, catalog, true);
        var service = new PartyDungeonService(parties, repository);
        var stats = new CharacterStatComputer(resources.Characters);
        using var log = new ServerLog(fixture.Options.LogDirectory);
        var town = new TownProjection(resources.Towns, stats, null, resources.ItemResources.EpicPieces);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var levels = new CharacterLevelProjection(stats);
        var handler = new DungeonHandler(log, main, rooms, resources.Growth, kills, town, levels,
            partyDungeons: service, partyStats: stats, parties: parties, antonRaid: entry);
        var commands = new GameCommandRegistry();
        new DungeonSelectionHandler(town, log, dungeons: main, partyDungeons: service, antonRaid: entry).Register(commands);
        handler.Register(commands);
        new DungeonSettlementHandler(log, main, SelfTestCatalogs.ClearExperience(resources.Archive), resources.Growth, town, rooms,
            SelfTestCatalogs.MonsterDrops(resources.Archive), resources.ItemResources.Items, levels).Register(commands);
        new KeepaliveHandler(log).Register(commands);
        commands.Seal();
        var sessions = new List<GameSession>();
        var leases = new List<AccountOwners.Lease>();
        var peers = new List<TcpPeer>();
        var workers = new List<Task>();
        using var stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var owner = owners.Acquire("antonphase" + i, () => { });
                leases.Add(owner);
                var origin = new Location(38, 0, 629, 196);
                var candidate = CharacterCreation.Prepare("AntPhase" + i, resources.Characters.Get(0), origin, origin, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
                var character = repository.Create(owner, candidate with { Level = 85, AcceptedStoryLevel = 85,
                    CompletedQuests = [12829, 3779], InitialLearning = learning });
                InventoryService.Commit(repository, owner, character,
                    new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces)
                        .Grant(character, [new(10096296, 1)]));
                var session = new GameSession(repository, resources.Towns, owner, entry: new(new(2, 19), 23, "[raid]"));
                session.Select(0); session.MarkReady();
                session.CurrentParty = () => parties.Current(session);
                session.HasMultiplePartyMembers = () => parties.HasMultipleMembers(session);
                sessions.Add(session);
            }
            var actors = sessions.Select(session => session.CaptureRaidActor(raids)!).ToArray();
            raids.Create(actors[0], 0, "phase", "phase"u8.ToArray(), 20, 1);
            raids.Invite(actors[0], actors[1], 0);
            That(raids.Respond(actors[1], actors[0].Character, true).Accepted, "阶段推进夹具加入原团");
            raids.Assign(actors[0], actors[0].Character, 1);
            raids.Assign(actors[0], actors[1].Character, 1);
            var start = new AntonRaidStart(raids, parties, repository, resources.Towns, resources.ItemResources.EpicPieces, phaseProgress: true);
            That(start.Start(actors[0], raids.Find(actors[0])!, definition with { StartMinimum = 1 }).Receipt is not null &&
                actors[0].Session.CurrentRaidBattle(actors[0]) is { PhaseProgress: true },
                "真实脚本开战打开阶段推进，仍然没有奖励策略");
            for (int i = 0; i < sessions.Count; i++)
            {
                var accept = listener.AcceptTcpClientAsync(stop.Token);
                peers.Add(new TcpPeer(((IPEndPoint)listener.LocalEndpoint).Port, channelNumber: 19));
                workers.Add(ServeAsync(await accept, leases[i], sessions[i]));
            }
            listener.Stop();

            for (int remaining = 3; remaining > 0; remaining--)
            {
                var partial = ClearDungeon(210);
                That(StateOf(partial, 210) == 0 && StateOf(partial, 212) == 2 &&
                    sessions[0].RaidCombatView()!.Symbols["[210 dgn hp]"] == remaining,
                    "双人Run仅扣一次HP，前三轮不开放后续图");
                var symbols = partial.Last(packet => packet is { Kind: 0, Opcode: 583 }).Body;
                That(Enumerable.Range(0, symbols[0]).Any(index =>
                    BinaryPrimitives.ReadInt32LittleEndian(symbols.AsSpan(1 + 8 * index)) == 50 &&
                    BinaryPrimitives.ReadInt32LittleEndian(symbols.AsSpan(5 + 8 * index)) == remaining),
                    "583真实symbol50按3/2/1同步HP，不以包存在代替字段验证");
                if (remaining == 3)
                    Bytes(partial.Last(packet => packet is { Kind: 0, Opcode: 587 }).Body, [2, 0], "首个Run只发布Fighting/0");
                LeaveBoth();
            }
            var afterBlack = ClearDungeon(210);
            AssertStates(afterBlack, new()
            {
                [210] = 2, [211] = 1, [212] = 0, [213] = 0, [214] = 0, [215] = 0, [216] = 2
            }, fighting: true);
            LeaveBoth();
            peers[0].Send(15, new byte[4]);
            Read(peers[0]); Read(peers[1]);
            peers[0].Send(16, Request(211));
            That(Read(peers[0]).Any(packet => packet is { Kind: 1, Opcode: 16 }) && sessions[0].DungeonSnapshot is null,
                "211 仍是 close，16 拒绝且没有 Run");

            var afterBlue = ClearDungeon(212);
            That(StateOf(afterBlue, 212) == 1 && StateOf(afterBlue, 213) == 0, "212 clear 的 586 末字节是 close");
            LeaveBoth();
            var afterRed = ClearDungeon(214);
            That(StateOf(afterRed, 214) == 1 && StateOf(afterRed, 215) == 0, "214 clear 的 586 末字节是 close");
            LeaveBoth();
            ClearDungeon(213); LeaveBoth();
            var afterLeg = ClearDungeon(213);
            That(StateOf(afterLeg, 213) == 3 && sessions[0].RaidCombatView() is { State: AntonRaidBattleState.Fighting, Phase: 0 } view &&
                view.Symbols["[1phase success]"] == 0, "单腿 clear 发布 Clear，阶段成功仍是 0");
            LeaveBoth();
            ClearDungeon(215); LeaveBoth();
            var afterSuccess = ClearDungeon(215);
            AssertStates(afterSuccess, new()
            {
                [210] = 2, [211] = 2, [212] = 2, [213] = 2, [214] = 2, [215] = 2, [216] = 2
            }, fighting: false);
            var done = actors[0].Session.CurrentRaidBattle(actors[0])!;
            That(done.View is { State: AntonRaidBattleState.Reward, Phase: 0 } && done.View.Symbols["[1phase success]"] == 1 &&
                done.AdvanceAfterRewards(done.Instance, done.View.Revision) is null && done.View.Phase == 0,
                "双腿 clear 后 587 进入 Reward，没有奖励策略就不能进阶段 1");
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            foreach (var peer in peers) peer.Dispose();
            await Task.WhenAll(workers);
            foreach (var session in sessions) session.End();
            foreach (var owner in leases) owner.Dispose();
        }

        List<WirePacket> ClearDungeon(int dungeon)
        {
            if (sessions[0].Phase != SessionPhase.MapSelect)
            {
                peers[0].Send(15, new byte[4]);
                Read(peers[0]); Read(peers[1]);
            }
            peers[0].Send(16, Request(dungeon));
            var opened = Read(peers[0]);
            Read(peers[1]);
            That(opened.Any(packet => packet is { Kind: 0, Opcode: 29 }), dungeon + " 的 16 发布真实 29");
            peers[0].Send(37, new byte[8]); peers[1].Send(37, new byte[8]);
            Read(peers[0]); Read(peers[1]);
            var scene = sessions[0].CurrentFormalDungeonScene()!;
            var route = QuestStoryRouteTests.Path(scene.Entry.Maze.StaticTopology, scene.Entry.StartMap.Position, scene.Entry.BossMap.Position);
            var party = sessions[0].PartyDungeon!;
            DungeonPlannedMonster? boss = null;
            for (int index = 0; index < route.Length; index++)
            {
                var current = sessions[0].CurrentFormalDungeonScene()!;
                foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly).ToArray())
                {
                    if (monster.IsBoss) boss = monster;
                    var observation = FormalDungeonSessionTests.Observation(current, monster);
                    var notices = party.ObserveDeath(sessions[0], monster.ObjectKey,
                        member => member.ObserveFormalDungeonDeath(observation, kills, resources.Growth));
                    That(notices.Length == 2, "阶段图的阻挡怪由两名队员各自提交");
                }
                if (index + 1 == route.Length) break;
                var next = route[index + 1];
                That(party.Move(sessions[0], (byte)next.X, (byte)next.Y, 0).Length == 2, "清房后两名队员进入下一房");
                That(party.CompleteLoading(sessions[0]).Length == 0 && party.CompleteLoading(sessions[1]).Length == 2, "下一房等两名队员加载");
            }
            That(boss is not null, dungeon + " 的路线上有真实 Boss");
            byte[] body = new byte[39];
            BinaryPrimitives.WriteUInt16LittleEndian(body, sessions[0].Selected!.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), boss!.ObjectKey);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), checked((uint)boss.CandidateLevel));
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(17), 504);
            peers[0].Send(117, body);
            var result = Until(peers[0], 115);
            Read(peers[1]);
            That(result.Any(packet => packet is { Kind: 0, Opcode: 586 }),
                "117 后发布 586，实际 " + string.Join(',', result.Select(packet => packet.Kind + ":" + packet.Opcode)));
            That(!result.Any(packet => packet.Opcode is 596 or 597 or 601), "阶段迁移不发送 596、597 或没有奖励策略时的 601");
            return result;
        }

        static List<WirePacket> Until(TcpPeer peer, ushort opcode)
        {
            var rows = new List<WirePacket>();
            for (int i = 0; i < 80; i++)
            {
                var packet = peer.Read();
                rows.Add(packet);
                if (packet is { Kind: 0, Opcode: var id } && id == opcode) return rows;
            }
            throw new InvalidOperationException("没有等到 NOTI " + opcode);
        }

        void LeaveBoth()
        {
            peers[0].Send(42); Read(peers[0]); Read(peers[1]);
            peers[1].Send(42); Read(peers[1]);
            That(sessions.All(session => session.Phase == SessionPhase.Town && session.DungeonSnapshot is null), "42 后两人都回到候战室");
        }

        async Task ServeAsync(TcpClient socket, AccountOwners.Lease owner, GameSession session)
        {
            using (socket)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                var stream = socket.GetStream();
                var connection = new ConnectionContext(240 + sessions.IndexOf(session), IPAddress.Loopback, stream, lifetime);
                await using var sender = new GameConnectionSender(owner, stream, lifetime, 0, (_, _, _, _, _) => { });
                var context = new GameConnectionContext(connection, owner, session, sender);
                session.RaidBattleNoticeArrived = (view, actor) => RaidCombatProjection.Notice(context, view, actor);
                handler.PreparePartyContext(context);
                try
                {
                    while (!stop.IsCancellationRequested)
                        await commands.DispatchAsync(context, GameFrame.ParseClient(await connection.ReadAsync()));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) when (stop.IsCancellationRequested) { }
            }
        }

        static byte[] Request(int dungeon)
        {
            byte[] request = Convert.FromHexString("D20000000200000000FFFF0000000000000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(request, checked((uint)dungeon));
            return request;
        }

        static void AssertStates(List<WirePacket> packets, Dictionary<int, byte> expected, bool fighting)
        {
            foreach (var (id, state) in expected)
                That(StateOf(packets, id) == state, $"586 里 {id} 的状态字节为 {state}");
            if (fighting)
                That(!packets.Any(packet => packet is { Kind: 0, Opcode: 587 }), "同phase Fighting不重复发布587演出");
            else
                Bytes(packets.Last(packet => packet is { Kind: 0, Opcode: 587 }).Body, [3, 0], "双腿成功后587是Reward/0");
        }

        static byte StateOf(List<WirePacket> packets, int dungeon)
        {
            var row = packets.LastOrDefault(packet => packet is { Kind: 0, Opcode: 586, Body.Length: >= 6 } &&
                BinaryPrimitives.ReadInt32LittleEndian(packet.Body) == dungeon);
            That(row is not null, dungeon + " 缺少 586，已有 " + string.Join(',', packets.Where(packet => packet.Opcode == 586)
                .Select(packet => BinaryPrimitives.ReadInt32LittleEndian(packet.Body) + ":" + packet.Body[5])));
            return row!.Body[5];
        }
    }
}
