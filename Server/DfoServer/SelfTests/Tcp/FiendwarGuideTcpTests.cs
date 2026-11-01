using System.Buffers.Binary;
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

internal static class FiendwarGuideTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-11-03T00:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "fiendwar-guide"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        Seed(0);
        void Seed(int index)
        {
            using var owner = new AccountOwners().Acquire("fwguide", () => { });
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("FwGuide" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            var role = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                CompletedQuests = [8672], InitialLearning = learning });
            var items = resources.ItemResources;
            var funded = InventoryService.Commit(repository, owner, role,
                InventoryRewardGrantService.TryGrantBatch(role,
                    [new(10306760, 2), new(10309054, 3), new(10312385, 2), new(1114, 3), new(7180, 2)],
                    items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()));
            repository.CommitInventory(owner, funded, new(funded.Inventory, funded.Inventory with { ReviveCoins = 20 }, "fixture_guide_coins"));
        }
        var modes = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!.Resources!;
        var guide = modes.FiendwarGuide!;
        using var peer = Open(fixture, "fwguide", gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39);
        var symbols = new Dictionary<uint, uint>();
        var clears = new Dictionary<byte, (uint Daily, uint Weekly)>();
        int dynamicSpawnRooms = 0;
        Select();
        FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 10);
        FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, true, 20, 1114);
        That(resources.ItemResources.RecoveryConsumables.TryGet(1114, out var ordinary), "引导普通药使用真实目录声明");
        clock.Advance(checked((ordinary.CoolTime + 999) / 1000));
        That(clears.Count == 4 && clears[2] == (0u, 0u), "初次登录四类团本次数初始化为真实零记录");
        for (int route = 0; route < 2; route++)
        {
            peer.Send(1426); Read();
            Create(true);
            Move(1, 2, accepted: false);
            Move(1, 33);
            peer.Send(666);
            var started = Read();
            That(started.Any(row => row is { Kind: 1, Opcode: 666, Body: [1] }) &&
                fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[57L, 33L]],
                "官方引导在57/33正式开战，不落团队基地");
            That(symbols.GetValueOrDefault(129u) == route, "引导按同一type2周通关次数选择两条原路线");
            peer.Send(15, BitConverter.GetBytes(modes.Fiendwar!.Areas[15].DungeonId));
            That(!Read().Any(row => row is { Kind: 1, Opcode: 15, Body: [1] }), "引导拒绝借用团队地下城");
            int current = 33;
            foreach (int next in route == 0 ? new[] { 34, 35, 36, 37, 38, 39, 41, 42, 43 } : new[] { 34, 35, 36, 37, 40, 39, 41, 42, 43 })
            {
                Move(current, next); current = next;
                int dungeon = guide.Fiendwar!.Areas[next].DungeonId;
                bool active = next is 38 or 40 || guide.Fiendwar.Rules.SelectMany(rule => rule.Actions)
                    .Any(action => action.Op is FiendwarOp.CreateSingleEnemy or FiendwarOp.CreateSingleMonster && action.A == next);
                if (dungeon <= 0 || !active || route == 0 && next == 41) continue;
                if (next == 35) Move(35, 36, accepted: false);
                Clear(dungeon, route);
                peer.Send(42); Read();
            }
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0 AND raid_type=2 AND state=2", peer.Id)
                is [[long completed]] && completed == route + 1, "原单世界终杀提交真实共享type2通关");
            That(clears[2] == (1u, (uint)route + 1), "真实终杀后主动刷新漩涡日周次数，无需重新登录");
            That(fixture.Query("SELECT count(*),COALESCE(sum(points),0) FROM guild_contribution_events WHERE character_id=$p0 AND source=5", peer.Id)
                is [[long contributions, 0L]] && contributions == route + 1,
                "独立原版引导真实通关使用相同贡献账本，无公会不赠分也不阻止通关");
            peer.Send(1403, [0, 0]);
            var adventure = Read().Single(row => row is { Kind: 1, Opcode: 1403 }).Body;
            That(adventure[0] == 1 && U32(adventure, 19 + 32) == (uint)route + 1,
                "引导两条原路线从同一真实终局账本投影冒险团累计，不随日周期清零");
            foreach (byte kind in new byte[] { 1, 0 })
            {
                peer.Send(660, [kind, 0]);
                That(Read().Any(row => row is { Kind: 1, Opcode: 660, Body: [1] }), "引导独立原奖励实际领取成功");
            }
            peer.Send(665, BitConverter.GetBytes(peer.Id)); Read();
            if (route == 0)
            {
                // 当天改建团队也不能重复通关；失败不扣团队门票。
                peer.Send(1426); Read(); Create(false); peer.Send(666);
                That(Read().Any(row => row is { Kind: 1, Opcode: 666, Body: [0, ..] }), "引导通关占用团队同一日资格");
                peer.Send(665, BitConverter.GetBytes(peer.Id)); Read();
                peer.Send(7); Read(); Select();
                That(clears[2] == (1u, 1u), "重登从已提交生命周期恢复日周次数，不重置为零");
                clock.Advance(86400);
                peer.Send(7); Read(); Select();
                That(clears[2] == (0u, 1u), "漩涡次日只重置日额度，保留周二周期内已通关数");
            }
        }
        That(dynamicSpawnRooms > 0, "引导原路线实际走过独立父子模板的生成和零奖励退休");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0 AND raid_type=2", peer.Id) is [[4L]],
            "两次引导各两类奖励，不新建另一种团本奖励账本");
        Create(true); Move(1, 33); peer.Send(666);
        That(Read().Any(row => row is { Kind: 1, Opcode: 666, Body: [0, ..] }), "第三次引导被共享周上限拒绝");
        peer.Send(665, BitConverter.GetBytes(peer.Id)); Read();
        Seed(1); Seed(2);
        for (ushort slot = 1; slot <= 2; slot++)
        {
            peer.Send(7); Read(); Select(slot); peer.Send(1426); Read();
            Create(true); Move(1, 33); peer.Send(666);
            bool admitted = Read().Any(row => row is { Kind: 1, Opcode: 666, Body: [1] });
            That(admitted == (slot == 1), "同账号每周只允许两名不同角色绑定引导");
            That(fixture.Query("SELECT count(*) FROM account_fiendwar_guides WHERE account='fwguide'") is [[2L]],
                "重复入场不重复占槽，第三角色失败不占槽");
            if (slot == 2)
                That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[0L]],
                    "绑定名额拒绝与入场生命周期保持原子性");
            peer.Send(665, BitConverter.GetBytes(peer.Id)); Read();
        }

        FiendwarFailureTcpTests.Run(fixture, resources, clock);

        void Create(bool single)
        {
            peer.Send(664, Build(writer => { writer.Write((byte)2); writer.Write(Name("FwGuide")); writer.Write(new byte[] { single ? (byte)1 : (byte)0, 0, 0, 7 }); }));
            var created = Read();
            That(created.Any(row => row is { Kind: 1, Opcode: 664, Body: [1, ..] }), "原生664模式字段创建对应团队或引导");
            That(RaidPanelTests.ReadGuideFlag(created.Single(row => row is { Kind: 0, Opcode: 590 }).Body, 4) == single,
                "创建面板显示实际团队或引导模式");
            uint key = U32(created.Single(row => row is { Kind: 1, Opcode: 664 }).Body, 1);
            peer.Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(key); }));
            var information = Read().Single(row => row is { Kind: 1, Opcode: 1361 }).Body;
            That(RaidPanelTests.ReadGuideFlag(information, 6) == single, "详情面板与创建记录模式一致");
            peer.Send(833, Build(writer =>
            {
                writer.Write((byte)0); writer.Write(0u); writer.Write(0x100u); writer.Write(2u); writer.Write(Name("FwGuide"));
            }));
            var search = Read().Single(row => row is { Kind: 0, Opcode: 756 }).Body;
            That(U32(search, 0) == 1 && U32(search, 8) == 1 && U32(search, 12) == key &&
                RaidPanelTests.ReadGuideFlag(search, 12) == single, "原生招募搜索返回真实团队或引导记录");
            peer.Send(833, [1]);
            That(!Read().Any(row => row is { Kind: 0, Opcode: 756 }), "原生关闭搜索不重发旧列表或建立新查询");
        }
        void Move(int from, int to, bool accepted = true)
        {
            var target = new Location(57, checked((byte)to), 0, 0);
            var arrival = resources.Towns.Entrance(new(57, checked((byte)from), 0, 0), target, new HashSet<int> { 8672 })
                ?? resources.Towns.Spawn(target, new HashSet<int> { 8672 })!;
            peer.Send(36, Area(57, checked((byte)to), checked((ushort)arrival.X), checked((ushort)arrival.Y), 57, checked((ushort)from)));
            bool moved = Read().Any(row => row is { Kind: 0, Opcode: 23 } && row.Body[3] == to);
            That(moved == accepted, "引导实际门准入及进度：" + from + "->" + to);
        }
        void Clear(int dungeon, int route)
        {
            bool direct = dungeon == 5350;
            if (!direct)
            {
                peer.Send(15, BitConverter.GetBytes(dungeon));
            }
            byte[] request = (byte[])AntonTcpProbe.LiveSelect210.Clone(); BinaryPrimitives.WriteInt32LittleEndian(request, dungeon);
            peer.Send(16, request); var entered = Read();
            if (!direct)
                That(entered.Any(row => row is { Kind: 1, Opcode: 15, Body: [1] }), "引导连续15/16在握手后接受真实选图");
            That(entered.Any(row => row is { Kind: 0, Opcode: 28 }), "引导正式选图进入：" + dungeon);
            That(entered.Count(row => row is { Kind: 0, Opcode: 27 }) == 1 &&
                entered.FindIndex(row => row is { Kind: 0, Opcode: 27 }) < entered.FindIndex(row => row is { Kind: 0, Opcode: 28 }),
                "引导连续15/16或直接16均只打开一次原生选图场景，再完整加载");
            var info = entered.Single(row => row is { Kind: 0, Opcode: 28 }).Body;
            var map = entered.Single(row => row is { Kind: 0, Opcode: 29 }).Body;
            var ai = entered.SingleOrDefault(row => row is { Kind: 0, Opcode: 32752 })?.Body;
            peer.Send(DfoServer.Network.Parsers.FiendwarCounterRequests.Opcode, Build(writer =>
            {
                writer.Write(DfoServer.Network.Parsers.FiendwarCounterRequests.Magic);
                writer.Write(ai is null ? 1u : U32(ai, 4)); writer.Write((uint)dungeon);
                writer.Write(ai is null ? 1u : U32(ai, 12)); writer.Write(ai is null ? 1u : U32(ai, 16));
                writer.Write((uint)(map[0] | map[1] << 8)); writer.Write(1u);
                writer.Write(1u); writer.Write(107013178u); writer.Write(1u);
            })); Read();
            That(fixture.Query("SELECT count(*) FROM character_fiendwar_counter_stream WHERE character_id=$p0", peer.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7006", peer.Id) is [[0L]],
                "官方引导的破招报告不进入团队成就或去重流水");
            var definition = guide.EntryCatalog!.Find(dungeon)!;
            That(info[7] == (definition.Mazes.Count == 1 ? 0 : route), "引导固定遭遇选择本路线原迷宫");
            var path = AntonTcpProbe.Route(guide.EntryCatalog, dungeon, info[7], map[0], map[1], info[8], info[9]);
            for (int step = 0; step < path.Length; step++)
            {
                if (step > 0)
                {
                    var move = new byte[100]; move[0] = checked((byte)path[step].X); move[1] = checked((byte)path[step].Y);
                    peer.Send(45, move); var moved = Read();
                    map = moved.Single(row => row is { Kind: 0, Opcode: 29 }).Body;
                    ai = moved.Single(row => row is { Kind: 0, Opcode: 32752 }).Body;
                }
                peer.Send(37, new byte[8]); Read();
                That(ai is not null, "引导房间向动态原生召唤提供当前MAI身份");
                if (FiendwarRaidTcpTests.VerifyDynamicSpawns(fixture, [peer], map, new() { [peer.Id] = ai! })) dynamicSpawnRooms++;
                if (dungeon == 5350 && route == 0 && step == 0)
                {
                    FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 21, 1114);
                    FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 22, 1114);
                    FiendwarRaidTcpTests.VerifyBlockedLegacyConsumable(fixture, peer);
                    FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, true, 11);
                    FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 12);
                }
                if (dungeon == 5350 && route == 0 && step == 0)
                    for (int revival = 0; revival < 6; revival++)
                    {
                        peer.Send(40, [0x74, 0, 4, 1, 0, 0]); Read();
                        peer.Send(41, BitConverter.GetBytes(peer.Id));
                        That(Read().Any(row => row is { Kind: 1, Opcode: 41, Body: [1, ..] }), "原版引导不套团队四币限制");
                    }
                foreach (int index in Enumerable.Range(0, map[18]).OrderBy(index => map[19 + index * 22 + 13] == 3))
                {
                    int offset = 19 + index * 22;
                    if (map[offset + 21] == 1 || map[offset + 13] == 9) continue;
                    peer.Send(39, FormalDungeonTcpTests.Death(U16(map, offset + 6), peer.Id, map[offset + 12]));
                    That(Read().Any(row => row is { Kind: 0, Opcode: 38 }), "引导原房间怪物真实死亡");
                    That(fixture.Query("SELECT count(*) FROM character_fiendwar_title_facts WHERE character_id=$p0", peer.Id) is [[0L]],
                        "官方Guide真实击杀与清图不产生团队漩涡称号事实");
                }
            }
        }
        void Select(ushort slot = 0)
        {
            peer.Select(slot);
            foreach (var (type, usage) in RaidPanelTests.ReadWeeklyClears(peer.RaidWeeklyClearInfo)) clears[type] = usage;
            Read();
        }

        List<WirePacket> Read()
        {
            var rows = AntonTcpProbe.Barrier(peer, "fiendwar guide");
            foreach (var row in rows.Where(row => row is { Kind: 0, Opcode: 1446 }))
                foreach (var (type, usage) in RaidPanelTests.ReadWeeklyClears(row.Body)) clears[type] = usage;
            foreach (var row in rows.Where(row => row is { Kind: 0, Opcode: 1530, Body.Length: > 0 }))
                for (int index = 0; index < row.Body[0]; index++) symbols[U32(row.Body, 1 + 8 * index)] = U32(row.Body, 5 + 8 * index);
            return rows;
        }
    }
}


