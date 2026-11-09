using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static readonly IReadOnlySet<int> EnterQuests = new HashSet<int> { 8935 };
    static readonly byte[] BoardingRequest = Convert.FromHexString("3B038C04B000053B0002000000000000");

    internal static Task RunAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false);

    internal static Task RunPotionLimitAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false, potionLimit: true);

    internal static Task RunTeamAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, true);

    internal static Task RunPhasesAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false, true);

    internal static Task RunPhaseOneAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false, phases: true, phaseOneOnly: true);

    internal static Task RunLobbyAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false, lobbyOnly: true);

    internal static Task RunTeamLobbyAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, true, lobbyOnly: true);

    internal static Task RunLandingAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false, landingOnly: true);

    internal static Task RunTeamLandingAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false, landingOnly: true, landingTeam: true);

    internal static Task RunDefenceLandingAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, false, landingOnly: true, landingTeam: true, defenceLanding: true);

    internal static Task RunExitAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, true, exitOnly: true);

    internal static Task RunRejoinPartyAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, true, rejoinParty: true);

    internal static Task RunSideCapacityAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunFixtureAsync(source, root, resources, true, sideCapacity: true);

    static async Task RunFixtureAsync(ServerOptions source, string root, TcpTestResources resources, bool team, bool phases = false,
        bool lobbyOnly = false, bool landingOnly = false, bool landingTeam = false, bool phaseOneOnly = false, bool exitOnly = false,
        bool rejoinParty = false, bool sideCapacity = false, bool defenceLanding = false, bool potionLimit = false)
    {
        var clock = new LukeRaidClockTests.Clock();
        string name = potionLimit ? "prey-potion-limit" : defenceLanding ? "prey-team-defence-landing" : sideCapacity ? "prey-team-side-capacity" : rejoinParty ? "prey-team-rejoin-party" : exitOnly ? "prey-team-exit" : landingOnly ? landingTeam ? "prey-team-landing-squads" : "prey-team-landing" : lobbyOnly ? team ? "prey-team-lobby-squads" : "prey-team-lobby" :
            phases ? phaseOneOnly ? "prey-team-phase1" : "prey-team-phases" : team ? "prey-team-squads" : "prey-team-flow";
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, name),
            resources: resources, clock: clock);
        await fixture.StartAsync();
        var definition = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(3)!.Resources!.PreyTeam
            ?? throw new InvalidOperationException("普通普雷资源未装配");
        var catalog = definition.EntryCatalog ?? throw new InvalidOperationException("普通普雷正式目录未装配");
        var cost = definition.Script.EntryCost ?? throw new InvalidOperationException("普通普雷门票声明缺失");
        int[] defences = definition.Script.Phases[0].Dungeons.Where(row => row.Declarations.Any(field =>
            field.Tag == "[type]" && field.Values is [{ Text: "slenikon defence" }])).Select(row => row.Id).ToArray();
        That(defences.Length >= 2 && defences.All(id => catalog.Find(id) is not null), "普通首阶段登记可由魔塔生成的真实防御原型");
        int count = sideCapacity ? 5 : rejoinParty ? 4 : phases || landingTeam ? 2 : team ? 3 : 1;
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var peers = new List<TcpPeer>();
        try
        {
            for (int index = 0; index < count; index++)
            {
                string account = "preyteam" + index;
                using (var owner = owners.Acquire(account, () => { }))
                {
                    var point = new Location(38, 0, 629, 196);
                    var candidate = CharacterCreation.Prepare("PreyTeam" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
                    if (landingTeam || rejoinParty) candidate = candidate with { Inventory = candidate.Inventory with { ReviveCoins = 10 } };
                    var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                    var character = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                        CompletedQuests = [8935], InitialLearning = learning });
                    var items = resources.ItemResources;
                    InventoryService.Commit(repository, owner, character,
                        new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(character,
                            potionLimit ? [new(cost.ItemId, checked(cost.Count * 2)), new(7740, 20), new(8, 20)] :
                                [new(cost.ItemId, checked(cost.Count * 2))]));
                }
                var peer = Open(fixture, account, gamePort: ChannelTcpTests.Endpoints(fixture)[42], channelNumber: 42);
                peers.Add(peer);
                peer.Select();
                Read(peer, "selected");
                Travel(fixture, peer, new(59, 1, 1217, 98), bottom: true);
                RejectTravel(fixture, peer, Area(59, 2, 27, 378, 59, 1), 2,
                    "无当前攻坚队不能从真实街区门进入候战室");
            }
            That(Enumerable.Range(3, 3).All(area =>
                !ChannelAreaLanding.ClientKeepsTownSnapshot(resources.Towns, null, new(59, (byte)area, 0, 0)) &&
                ChannelAreaLanding.OrdinaryRoomReturn(new(59, (byte)area, 0, 0)) is null),
                "鸟背及头尾区域断线后不能作为普通选角快照或房间返回点恢复");
            var leader = peers[0];
            leader.Send(664, Build(writer => { writer.Write((byte)3); writer.Write(Name("prey team")); writer.Write(new byte[] { 0, 0, 0, 7 }); }));
            var created = Read(leader, "created");
            var createAck = created.Single(row => row is { Kind: 1, Opcode: 664 });
            That(createAck.Body is [1, _, _, _, _] && created.FindIndex(row => row is { Kind: 0, Opcode: 590 }) < created.IndexOf(createAck),
                "原664普通标志创建攻坚队，团记录先于成功key发布");
            That(created.All(row => row is not { Kind: 0, Opcode: 9 }), "建团不按成员标记自动创建普通队伍");
            uint key = U32(createAck.Body, 1);
            var initialRoster = ReadRoster(leader, key);
            That(initialRoster[leader.Id] is { Squad: 1, Position: 11 }, "普通普雷创建者默认第1队第1格，无需先发送669");
            if (team || phases || landingTeam)
            {
                for (int index = 1; index < peers.Count; index++)
                {
                    var entrant = peers[index];
                    entrant.Send(667, [0, 3]);
                    Ack(Read(entrant, "waiting"), 667, [1], "普通普雷成员登记候战");
                    entrant.Send(10, AntonRaidTcpTests.Peer(leader.Id, 123, true));
                    Ack(Read(entrant, "join request"), 10, [1], "成员沿原10申请入团");
                    That(Read(leader, "join invitation").Any(row => row is { Kind: 0, Opcode: 7 } && U16(row.Body) == entrant.Id && row.Body[2] == 10),
                        "原队长收到准确申请者的mode10邀请");
                    leader.Send(11, AntonRaidTcpTests.Peer(entrant.Id, 123, false));
                    Ack(Read(leader, "join accepted"), 11,
                        Build(writer => { writer.Write((byte)1); writer.Write(entrant.Id); writer.Write((byte)10); }), "原11同意并提交成员关系");
                    foreach (var peer in peers.Skip(1)) Read(peer, "joined roster");
                }
            }
            foreach (var peer in peers)
                Travel(fixture, peer, new(59, 2, 27, 378));
            RejectTravel(fixture, leader, BoardingRequest, 3, "尚未开战的当前攻坚队不能提前登上鸟背", reply: false);
            if (peers.Count > 1)
            {
                var joinedRoster = ReadRoster(leader, key);
                That(peers.Skip(1).All(peer => joinedRoster[peer.Id] is { Squad: 0, Position: 0 }),
                    "新增成员仍未编队，创建者的默认位置不能替其他成员满足开团条件");
                RejectUnassignedStart();
            }
            int[] positions = peers.Select((_, index) => index switch { 0 => 11, 1 => 21, 2 => 31, 3 => 12, _ => 22 }).ToArray();
            for (int index = 1; index < peers.Count; index++)
            {
                if (index == peers.Count - 1 && peers.Count > 1) RejectUnassignedStart();
                int position = positions[index];
                leader.Send(669, Build(writer => { writer.Write(2u); writer.Write((uint)peers[index].Id); writer.Write((uint)position); }));
                var marked = Read(leader, "formation");
                Ack(marked, 669, [1], "原669 operation2保留普雷分队和座次编码");
                That(marked.All(row => row is not { Kind: 0, Opcode: 9 }), "分配普雷标记不自动创建普通队伍");
                foreach (var peer in peers.Skip(1)) Read(peer, "formation roster");
            }
            var roster = ReadRoster(leader, key);
            That(roster.Count == peers.Count && peers.Select((peer, index) =>
                roster[peer.Id].Position == positions[index] && roster[peer.Id].Squad == positions[index] / 10).All(value => value),
                "原1361返回全员已分配且互不冲突的攻坚标记和座次");
            long startStamp = clock.GetTimestamp();
            leader.Send(666);
            var started = peers.Select(peer => Read(peer, "started")).ToArray();
            Ack(started[0], 666, [1], "普通666在所有成员已有合法攻坚标记后启动真实实例");
            That(started.All(rows => rows.All(row => row is not { Kind: 0, Opcode: 9 })),
                "普通普雷开团只冻结攻坚成员与标记，不按标记创建普通队伍");
            var startedRoster = ReadRoster(leader, key);
            That(peers.Select((peer, index) => startedRoster[peer.Id].Position == positions[index] &&
                startedRoster[peer.Id].Squad == positions[index] / 10).All(value => value),
                "开团保留创建者默认及团长明确分配的标记，不自动建立实际小队");
            That(started.All(rows => rows.Any(row => row is { Kind: 0, Opcode: 1595, Body: [1, 0, 0, 0] }) &&
                rows.Any(row => row is { Kind: 0, Opcode: 587, Body: [1, 0] }) &&
                rows.Any(row => row is { Kind: 0, Opcode: 597, Body.Length: 5 } && row.Body[0] == 0 && U32(row.Body, 1) is > 0 and <= 15) &&
                rows.Count(row => row is { Kind: 0, Opcode: 594, Body.Length: 0 }) == 1 &&
                rows.FindIndex(row => row is { Kind: 0, Opcode: 594 }) >
                    rows.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [1, 0] }) &&
                rows.All(row => row is not { Kind: 0, Opcode: 587, Body: [2, 0] })),
                "开团立即向全团发布state1及一次原生594可视倒数，不提前安装正式开战态");
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND state=0") is [[long runs]] && runs == count &&
                fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=$p0", cost.ItemId)
                    is [[long remaining]] && remaining == count * cost.Count, "真实开战每人扣一次资源门票并提交类型3生命周期");
            string committed = fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id") +
                fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid");
            int delay = definition.Script.Headers.Single(row => row.Tag == "[start delay time]").Values.Single().Number;
            int elapsed = Math.Min(1, delay - 1);
            clock.Advance(elapsed);
            foreach (var peer in peers)
                That(Read(peer, "preparation tick").All(row => row.Opcode != 594 && row is not { Kind: 0, Opcode: 587, Body: [2, 0] }),
                    "原生演出期间自然tick不重播594或在本人36前进入state2");
            leader.Send(666);
            var replayed = peers.Select(peer => Read(peer, "start replay")).ToArray();
            Ack(replayed[0], 666, [1], "重复666回放原启动结果");
            That(replayed.All(rows => rows.All(row => row.Opcode != 594 && row is not { Kind: 0, Opcode: 587, Body: [2, 0] })),
                "原生演出期间重复666不重播594或在本人36前进入state2");
            That(committed == fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id") +
                fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid"), "重复开战不重扣或重建持久事实");

            var prepared = peers.Select(_ => new List<WirePacket>()).ToArray();
            bool fallback = lobbyOnly && team;
            if (fallback)
            {
                clock.Advance(delay - elapsed);
                for (int index = 0; index < peers.Count; index++)
                {
                    prepared[index].AddRange(Read(peers[index], "preparation fallback"));
                    That(prepared[index].All(row => row.Opcode != 594 && row is not { Kind: 0, Opcode: 587, Body: [2, 0] }) &&
                        prepared[index].Where(row => row is { Kind: 1, Opcode: 1361, Body: [1, 0 or 2, ..] })
                            .All(row => RaidInformationState(row.Body, key) == 1),
                        "没有原36时资源15秒fallback只启动规则，不重播594或将未登船成员改为state2");
                }
            }
            else
                That(clock.GetTimestamp() - startStamp < delay * clock.TimestampFrequency,
                    "原36闭环在资源15秒之前发起，不以服务端等待时间代替客户端演出完成");
            for (int index = 0; index < peers.Count; index++)
            {
                var peer = peers[index];
                That(ReadDisplayedState(peer, key) == 1, "每名成员独立登船前的真实1361记录仍为state1");
                prepared[index].AddRange(Travel(fixture, peer, new(59, 3, 1164, 176), request: BoardingRequest));
                That(ReadDisplayedState(peer, key) == 2, "本人原36及24完成后真实1361记录才转state2");
            }
            leader.Send(666);
            var boardedReplay = peers.Select(peer => Read(peer, "boarded start replay")).ToArray();
            Ack(boardedReplay[0], 666, [1], "登船后重复666回放原开团成功");
            That(boardedReplay.All(rows => rows.All(row => row.Opcode != 594 && row is not { Kind: 0, Opcode: 587, Body: [1, 0] })),
                "已登船成员重复666不重播594或回退到准备态");
            if (sideCapacity)
            {
                SideCapacity(fixture, peers.ToArray(), resources.Towns, key, positions);
                return;
            }
            if (rejoinParty)
            {
                var symbols = Symbols(started[0].Concat(prepared[0]).Last(row => row is { Kind: 0, Opcode: 583 }).Body);
                await RejoinLandingAsync(fixture, peers, resources.Towns, definition, clock, symbols, key, positions);
                return;
            }
            if (exitOnly)
            {
                await ExitAsync(fixture, peers.ToArray(), resources.Towns, definition, clock, key, positions);
                return;
            }
            if (landingOnly)
            {
                var symbols = Symbols(started[0].Concat(prepared[0]).Last(row => row is { Kind: 0, Opcode: 583 }).Body);
                if (defenceLanding)
                    await LandDefenceTeamAsync(fixture, peers[1], leader, resources.Towns, definition, clock, symbols, key);
                else if (landingTeam)
                {
                    peers[1].Send(12, Convert.FromHexString("000304000000000500000000000007"));
                    var manualParty = Read(peers[1], "manual solo party rejected");
                    Ack(manualParty, 12, [0, 21, 0], "普通普雷已开战时不能用原12创建单人普通队伍");
                    That(manualParty.All(row => row is not { Kind: 0, Opcode: 9 }),
                        "拒绝手动单人组队不发送普通队伍名册，后续按侧区实际到达组队");
                    await LandTeamAsync(fixture, peers[1], leader, resources.Towns, definition, clock, symbols, key);
                }
                else await LandAsync(fixture, leader, resources.Towns, definition, clock, symbols);
                return;
            }
            if (lobbyOnly && team)
                BoardingParties(fixture, peers.ToArray(), resources.Towns, key, positions);
            else
            {
                Cross(fixture, leader, resources.Towns, 4);
                Cross(fixture, leader, resources.Towns, 3);
                Cross(fixture, leader, resources.Towns, 5);
                Cross(fixture, leader, resources.Towns, 3);
            }
            if (lobbyOnly)
            {
                That(prepared.All(rows => rows.Any(row => row is { Kind: 0, Opcode: 583 } &&
                    Symbols(row.Body)[definition.Script.Symbols["[prepare raid]"]] == 1)),
                    "全团在原36或资源fallback启动规则后收到正式开放状态");
                That(peers.All(peer => fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[59L, 3L]]) &&
                    fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[0L]],
                    "前台验收完成真实登船及头尾门往返，全员在鸟背且没有副本或阶段奖励");
                return;
            }
            if (phases)
            {
                await CompletePhases(fixture, peers, definition, resources.Towns, key, clock, phaseOneOnly);
                return;
            }
            var initialSymbols = Symbols(started[0].Concat(prepared[0]).Last(row => row is { Kind: 0, Opcode: 583 }).Body);
            int[] initialDefences = Enumerable.Range(1, 16).Where(slot =>
                initialSymbols[definition.Script.Symbols[$"[area_{slot}_dgn_type]"]] == 5).ToArray();
            That(initialDefences.Length == 2 && initialDefences.Select(slot => initialSymbols[definition.Script.Symbols[$"[area_{slot}]"]]).Distinct().Count() == 2 &&
                initialDefences.All(slot => defences.Contains(initialSymbols[definition.Script.Symbols[$"[area_{slot}]"]])),
                "开团首份真实583同步两个不同的有效阻击，空区域不冒充初始怪物");
            foreach (var symbols in prepared.Select((rows, index) => Symbols(started[index].Concat(rows).Last(row => row is { Kind: 0, Opcode: 583 }).Body)))
                That(initialDefences.All(slot => symbols[definition.Script.Symbols[$"[area_{slot}]"]] == initialSymbols[definition.Script.Symbols[$"[area_{slot}]"]] &&
                    symbols[definition.Script.Symbols[$"[area_{slot}_dgn_type]"]] == 5), "全员初始阻击位置、模板和真实类型一致");
            defences = defences.Where(id => Enumerable.Range(1, 16).Any(slot =>
                initialSymbols[definition.Script.Symbols[$"[area_{slot}]"]] == id)).Take(team ? 2 : 1).ToArray();
            That(defences.Length == (team ? 2 : 1), "初始阻击直接供本次单人或双队正式入场，不等180秒生成周期");
            foreach (var peer in peers.Take(team ? 2 : peers.Count)) Board(fixture, peer, resources.Towns, 5);
            int[] areaKeys = defences.Select(dungeon => Enumerable.Range(1, 16).Select(slot => definition.Script.Symbols[$"[area_{slot}]"])
                .Single(symbol => initialSymbols[symbol] == dungeon)).ToArray();
            int[] areaTypeKeys = defences.Select(dungeon => Enumerable.Range(1, 16)
                .Single(slot => initialSymbols[definition.Script.Symbols[$"[area_{slot}]"]] == dungeon))
                .Select(slot => definition.Script.Symbols[$"[area_{slot}_dgn_type]"]).ToArray();
            var packets = peers.Select(_ => new List<WirePacket>()).ToArray();
            var reports = peers.Select((_, index) => new AntonTcpProbe.AntonProbeReport { Observe = packets[index].Add }).ToArray();
            if (team)
                AntonTcpProbe.EnterSquad(peers[0], peers[1], catalog, defences[0], reports[0], reports[1], loaded: () =>
                {
                    Board(fixture, peers[2], resources.Towns, 5);
                    AntonTcpProbe.Enter(peers[2], catalog, defences[1], reports[2], clear: true);
                });
            else
            {
                bool potionsChecked = false;
                AntonTcpProbe.Enter(leader, catalog, defences[0], reports[0], clear: true, beforeRoomClear: potionLimit ? _ =>
                {
                    if (!potionsChecked)
                    {
                        VerifyPotionLimit(fixture, leader, resources, clock, packets[0]);
                        potionsChecked = true;
                    }
                    else That(packets[0].Last(row => row is { Kind: 0, Opcode: 1596 }).Body is [0, 0, 0, 0],
                        "过房继续同步零剩余额度，不恢复已用完的十次药品资格");
                } : null);
                That(!potionLimit || potionsChecked, "限药回归必须实际进入并加载普雷场景");
            }
            for (int index = 0; index < peers.Count; index++)
            {
                AntonTcpProbe.Note(reports[index], Read(peers[index], "final shared state"));
                That(reports[index].Opcode115 == 1 && reports[index].Opcode31 == 1 &&
                    packets[index].Any(row => row is { Kind: 0, Opcode: 38 }) && packets[index].Any(row => row is { Kind: 0, Opcode: 30 }),
                    "实际加载、39死亡与单次115/31许可完成本角色正式Run");
                var finalSymbols = Symbols(reports[index].Symbols.Last());
                That(finalSymbols[areaKeys[0]] == 0 && finalSymbols[areaTypeKeys[0]] == 7 &&
                    (!team || finalSymbols[areaKeys[1]] == 0 && finalSymbols[areaTypeKeys[1]] == 7),
                    "真实防御清图后各队收到空AREA及TYPE7，原生卡片不再以TYPE0普通图重显示");
                var finalRoster = ReadRoster(peers[index], key);
                That(finalRoster.Values.All(member => member.Clears == 1), "每个实际清图成员只累计一次参与次数");
                That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peers[index].Id) is [[59L, 3L]],
                    "原42正式退场回到鸟背中间");
            }
            That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND phase0_event IS NOT NULL") is [[0L]],
                "防御单图不伪造阶段成功或提前发放奖励");

            void RejectUnassignedStart()
            {
                string before = fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
                    fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
                leader.Send(666);
                var rejected = peers.Select(peer => Read(peer, "unassigned start rejected")).ToArray();
                Ack(rejected[0], 666, [0, 19, 0], "仍有未分配标记的成员时666明确拒绝");
                That(fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
                    fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") == before &&
                    fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3") is [[0L]] &&
                    rejected.All(rows => rows.All(row => row.Opcode is not (9 or 587 or 594 or 1595))),
                    "新增队员均未标记或仅末位未标记时，不扣门票、不创建生命周期或发布开战及实际Party");
            }
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }

    static List<WirePacket> Read(TcpPeer peer, string action) => AntonTcpProbe.Barrier(peer, "prey team " + action);

    static Location Position(TcpTestFixture fixture, TcpPeer peer)
    {
        var row = fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id).Single();
        return new((byte)(long)row[0]!, (byte)(long)row[1]!, (ushort)(long)row[2]!, (ushort)(long)row[3]!);
    }

    static void RejectTravel(TcpTestFixture fixture, TcpPeer peer, byte[] request, byte area, string detail, bool reply = true)
    {
        string before = fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", peer.Id);
        peer.Send(36, request);
        var rejected = Read(peer, "rejected town travel");
        if (reply) Ack(rejected, 36, [0, 1, 0, 59, area], detail);
        That(rejected.All(row => row.Opcode != (ushort)DfoServer.Network.NotiPacketType.AAS_TIPS), "普雷候战门禁不弹公共提示");
        That(rejected.All(row => row is not { Kind: 0, Opcode: 24 } &&
            (row is not { Kind: 0, Opcode: 23 } || U16(row.Body) != peer.Id)) &&
            before == fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", peer.Id),
            "拒绝不发布切图，位置、revision和其他角色持久字段保持");
    }

    static List<WirePacket> Travel(TcpTestFixture fixture, TcpPeer peer, Location target, bool bottom = false,
        byte[]? request = null, byte boardingPhase = 0)
    {
        var from = Position(fixture, peer);
        string back = fixture.Snapshot("SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", peer.Id);
        long revision = (long)fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id).Single()[0]!;
        peer.Send(bottom ? (ushort)1426 : (ushort)36,
            bottom ? [] : request ?? Area(target.Town, target.Area, target.X, target.Y, from.Town, from.Area));
        var travelled = Read(peer, "town travel " + target.Area);
        var location = travelled.Single(row => row is { Kind: 0, Opcode: 23 } && U16(row.Body) == peer.Id).Body;
        Bytes(location.AsSpan(0, 8), Build(writer =>
        { writer.Write(peer.Id); writer.Write(target.Town); writer.Write(target.Area); writer.Write(target.X); writer.Write(target.Y); }),
            "原23保留真实目标区域和客户端请求落点");
        var users = travelled.Single(row => row is { Kind: 0, Opcode: 24 }).Body;
        That(users[0] == target.Town && users[1] == target.Area && U16(users, 4) == peer.Id &&
            U16(users, 6) == target.X && U16(users, 8) == target.Y,
            "原24保留本角色真实目标区域和坐标");
        var position = Position(fixture, peer);
        That(position == target, $"原24目标与存档位置相同：expected={target}, actual={position}");
        long saved = (long)fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id).Single()[0]!;
        That(saved == revision + 1, $"切图revision只提交一次：before={revision}, after={saved}");
        string returned = fixture.Snapshot("SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", peer.Id);
        That(back == returned, $"切图保留完整普通返回点：before={back}, after={returned}");
        if (from is { Town: 59, Area: 2 } && target is { Town: 59, Area: 3 })
        {
            int arrival = travelled.FindIndex(row => row is { Kind: 0, Opcode: 24 });
            int started = travelled.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [2, var phase] } && phase == boardingPhase);
            That(arrival >= 0 && started > arrival && travelled.All(row => row.Opcode != 594),
                "原36登船先完成本人24，再发布首个state2，不重播594");
        }
        if (target is { Town: 59, Area: >= 3 and <= 5 })
            That(travelled.Any(row => row is { Kind: 1, Opcode: 1361 } && row.Body[1] == 2 &&
                RosterLocations(row.Body).GetValueOrDefault(peer.Id) == target.Area),
                "原1361 mode2同步玩家当前鸟背或头尾位置，不混用编队座次");
        return travelled;
    }

    static byte ReadDisplayedState(TcpPeer peer, uint key)
    {
        peer.Send(1361, Build(writer => { writer.Write((byte)0); writer.Write(key); }));
        var rows = Read(peer, "current displayed state");
        byte state = RaidInformationState(rows.Single(row => row is { Kind: 1, Opcode: 1361, Body: [1, 0, ..] }).Body, key);
        That(rows.All(row => row.Opcode != 594), "查询当前记录不重播已发送的594");
        if (state == 1)
            That(rows.All(row => row is not { Kind: 0, Opcode: 587, Body: [2, _] }) &&
                rows.Where(row => row is { Kind: 1, Opcode: 1361, Body: [1, 0 or 2, ..] }).All(row => RaidInformationState(row.Body, key) == 1),
                "等待本人原36时，通知与完整1361记录均保持state1");
        return state;
    }

    static byte RaidInformationState(byte[] body, uint key)
    {
        That(body.Length >= 83 && body[0] == 1 && body[1] is 0 or 2 && U32(body, 2) == key && U32(body, 6) == key,
            "1361完整团记录保留成功前缀、查询key和原记录key");
        int title = checked((int)U32(body, 10));
        That(title <= body.Length - 83 && body[14 + title] == 3, "1361标题及普通普雷类型后存在完整状态字段");
        return body[15 + title];
    }

    static Dictionary<ushort, ushort> RosterLocations(byte[] body)
    {
        That(body.Length >= 7 && body[0] == 1 && body[1] is 1 or 2, "位置快照使用原1361完整成员模式");
        int at = 6;
        if (body[1] == 2)
        {
            at += 4;
            at += 4 + checked((int)U32(body, at)) + 16;
            at += RaidMemberWidth(body, at);
        }
        int count = body[at++];
        var locations = new Dictionary<ushort, ushort>();
        for (int index = 0; index < count; index++)
        {
            int size = RaidMemberWidth(body, at);
            int name = checked((int)U32(body, at + 3));
            locations.Add(U16(body, at), U16(body, at + 34 + name));
            at += size;
        }
        That(at == body.Length, "位置快照完整消费u16当前地点及全部成员尾部");
        return locations;
    }

    static List<WirePacket> Cross(TcpTestFixture fixture, TcpPeer peer, TownCatalog towns, byte area)
    {
        var from = Position(fixture, peer);
        That(towns.Areas[(from.Town, from.Area)].Map(EnterQuests).Links.Any(link =>
            link.Gate.Door && link.Town == 59 && link.Area == area && link.Conditions.Length == 0 && link.Quests.All(EnterQuests.Contains)),
            "鸟背或候战切图使用原PVF真实有向门");
        var target = new Location(59, area, 0, 0);
        return Travel(fixture, peer, towns.Entrance(from, target, EnterQuests) ?? towns.Spawn(target, EnterQuests)
            ?? throw new InvalidOperationException("普雷城镇真实门缺少可站落点"));
    }

    static void Board(TcpTestFixture fixture, TcpPeer peer, TownCatalog towns, byte area, byte boardingPhase = 0)
    {
        if (Position(fixture, peer).Area == 2)
            Travel(fixture, peer, new(59, 3, 1164, 176), request: BoardingRequest, boardingPhase: boardingPhase);
        if (Position(fixture, peer).Area == area) return;
        if (Position(fixture, peer).Area != 3) Cross(fixture, peer, towns, 3);
        if (area != 3) Cross(fixture, peer, towns, area);
    }

    static void Ack(List<WirePacket> rows, ushort opcode, byte[] expected, string detail) =>
        Bytes(rows.Single(row => row.Kind == 1 && row.Opcode == opcode).Body, expected, detail);

    static Dictionary<int, int> Symbols(byte[] body)
    {
        That(body.Length == 1 + body[0] * 8, "普通普雷583完整消费符号记录");
        return Enumerable.Range(0, body[0]).ToDictionary(index => checked((int)U32(body, 1 + index * 8)),
            index => unchecked((int)U32(body, 5 + index * 8)));
    }

    static Dictionary<ushort, (byte Squad, byte Clears, byte Position)> ReadRoster(TcpPeer peer, uint key)
    {
        peer.Send(1361, Build(writer => { writer.Write((byte)1); writer.Write(key); }));
        byte[] body = Read(peer, "member information").Single(row => row is { Kind: 1, Opcode: 1361, Body: [1, 1, ..] }).Body;
        That(body.Length >= 7 && body[0] == 1 && body[1] == 1 && U32(body, 2) == key, "1361 mode1成功前缀及原团key");
        var members = new Dictionary<ushort, (byte, byte, byte)>();
        int at = 7;
        for (int index = 0; index < body[6]; index++)
        {
            int size = RaidMemberWidth(body, at);
            int width = checked((int)U32(body, at + 3));
            members.Add(U16(body, at), (body[at + 9 + width], body[at + 17 + width], body[at + 33 + width]));
            at += size;
        }
        That(at == body.Length, "1361 mode1完整消费所有成员及普雷座次");
        return members;
    }

    static int RaidMemberWidth(byte[] body, int at)
    {
        That(at + 7 <= body.Length, "1361成员名字长度字段完整");
        int name = checked((int)U32(body, at + 3));
        That(name <= body.Length - at - 53, "1361成员保留固定字段和完整名字");
        int retryCount = checked((int)U32(body, at + 40 + name));
        That(retryCount <= (body.Length - at - 53 - name) / 8, "1361成员重试表及后续尾部完整");
        return 53 + name + retryCount * 8;
    }
}
