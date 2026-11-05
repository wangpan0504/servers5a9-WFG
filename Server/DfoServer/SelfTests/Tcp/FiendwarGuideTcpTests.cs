using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class FiendwarGuideTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
        => await RunFixtureAsync(source, root, resources, false);

    internal static async Task RunBattlePassNoticeAsync(ServerOptions source, string root, TcpTestResources resources)
        => await RunFixtureAsync(source, root, resources, true);

    internal static async Task RunBattlePassDeathAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        foreach (int mission in new[] { 1, 2 })
            await RunFixtureAsync(source, Path.Combine(root, "mission-" + mission), resources, true, mission);
        await RunFixtureAsync(source, Path.Combine(root, "rollback"), resources, true, 1, true);
    }

    internal static async Task RunBattlePassDeathWriterAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var failures = new List<Exception>();
        foreach (int mode in new[] { 1, 2, 3 })
        {
            try
            {
                await RunFixtureAsync(source, Path.Combine(root, "writer-mode-" + mode), resources,
                    battlePass: mode == 1, deathMission: 1, writerDeadline: mode);
            }
            catch (Exception error)
            {
                Console.WriteLine($"GUIDE_WRITER_DEADLINE_CASE mode={mode} {error}");
                failures.Add(error);
            }
        }
        if (failures.Count != 0) throw new AggregateException("Guide原死亡跨writer期限回归失败", failures);
    }

    static async Task RunFixtureAsync(ServerOptions source, string root, TcpTestResources resources, bool battlePass,
        int deathMission = 0, bool failDeath = false, int writerDeadline = 0)
    {
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-11-03T00:00:00Z"));
        var startAt = DateTimeOffset.Parse("2026-10-29T06:00:00+08:00");
        if (deathMission != 0)
        {
            var policy = new BattlePassMissionPolicy("fiendwar-notice-fixture", startAt, BattlePassCatalog.Load(resources.Archive));
            clock = new LukeRaidClockTests.Clock(Enumerable.Range(0, policy.Days)
                .Select(day => startAt.AddDays(day).AddHours(2))
                .First(at => policy.Active(at).Any(row => row.Key.Category == 0 && row.Key.Id == deathMission)));
        }
        if (battlePass) source = source with { BattlePassSeasonKey = "fiendwar-notice-fixture", BattlePassSeasonStart = startAt };
        if (writerDeadline == 2)
            source = source with { BattlePassSeasonKey = null, BattlePassSeasonStart = null };
        if (writerDeadline == 3)
            source = source with { BattlePassSeasonKey = "fiendwar-notice-fixture", BattlePassSeasonStart = startAt.AddDays(70) };
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
        if (battlePass)
        {
            peer.ExpectedOpenEvents = [ServerEventService.NolanVisibilityId, ServerEventService.MileageShopId, 2520];
            peer.ExpectedBattlePassEnd = checked((uint)new BattlePassMissionPolicy("fiendwar-notice-fixture", startAt,
                BattlePassCatalog.Load(resources.Archive)).End.ToUnixTimeSeconds());
        }
        var symbols = new Dictionary<uint, uint>();
        var clears = new Dictionary<byte, (uint Daily, uint Weekly)>();
        var missionPackets = new List<byte[]>();
        byte[]? originalPartyRemoval = null;
        int dynamicSpawnRooms = 0;
        int guideDeaths = 0, guideBossDeaths = 0;
        bool rollbackChecked = false, writerDeadlineChecked = false;
        bool inDungeonScene = false, returnedToField = false, resultShown = false, victoryShown = false, cardsShown = false;
        var cardChoices = new HashSet<byte>();
        Select();
        FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 10);
        FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, true, 20, 1114);
        That(resources.ItemResources.RecoveryConsumables.TryGet(1114, out var ordinary), "引导普通药使用真实目录声明");
        clock.Advance(checked((ordinary.CoolTime + 999) / 1000));
        That(clears.Count == 4 && clears[2] == (0u, 0u), "初次登录四类团本次数初始化为真实零记录");
        for (int route = 0; route < (battlePass ? 1 : 2); route++)
        {
            resultShown = victoryShown = cardsShown = false;
            cardChoices.Clear();
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
                if (battlePass && dungeon == 5356) missionPackets.Clear();
                Clear(dungeon, route);
                if (deathMission != 0)
                {
                    if (writerDeadline != 0)
                        That(writerDeadlineChecked, "真实writer等待场景已完成");
                    else if (failDeath)
                        That(rollbackChecked, "引导真实死亡已触发战令来源晚期故障与原事务回滚");
                    else
                    {
                        var persisted = ReadMissions();
                        That(guideDeaths > 0 && guideBossDeaths > 0 &&
                            persisted.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == deathMission).Count ==
                                Math.Min(deathMission == 1 ? guideDeaths : guideBossDeaths, deathMission == 1 ? 10 : 2),
                            "原Repository重建恢复真实引导怪物与资源领主任务进度");
                    }
                    return;
                }
                if (next == 35)
                {
                    string Ledger() => fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,raid_type") +
                        fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,kind") +
                        fixture.Snapshot("SELECT * FROM guild_contribution_events ORDER BY character_id,event_id") +
                        fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id");
                    string before = Ledger();
                    peer.Send(72, [1, 2]);
                    var returned = Read();
                    int ack = returned.FindIndex(row => row is { Kind: 1, Opcode: 72, Body: [1, 1, 2] });
                    int town = returned.FindIndex(row => row is { Kind: 0, Opcode: 24, Body: [57, ..] });
                    That(ack >= 0 && town > ack && !returned.Any(row => row is { Kind: 0, Opcode: 15 or 27 or 28 or 29 }),
                        "引导真实非终局清房菜单返回先ACK72再24回原战场，不打开普通选图");
                    That(before == Ledger(), "引导清房菜单返回不重复原通关、次数、奖励或死亡账本");
                    peer.Send(72, [1, 2]);
                    That(!Read().Any(row => row is { Kind: 1, Opcode: 72, Body: [1, ..] } or
                        { Kind: 0, Opcode: 24 or 15 or 27 or 28 or 29 }) && before == Ledger(),
                        "引导旧清房菜单重放不重复退场或原账本");
                }
                else
                {
                    peer.Send(42); Read();
                }
                if (battlePass && dungeon == 5356)
                {
                    var observer = new CharacterRepository(fixture.Database);
                    observer.ConfigureBattlePassMissions(new BattlePassMissionPolicy("fiendwar-notice-fixture", startAt,
                        BattlePassCatalog.Load(resources.Archive)));
                    using var observerOwner = new AccountOwners().Acquire("fwguide", () => { });
                    var authoritative = observer.ReadBattlePassMissions(observerOwner, clock.GetUtcNow())!;
                    That(authoritative.Slots.Single(row => row.Instance.Key.Id == 58).Count == 1 &&
                        fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE account=$p0 AND source_kind='raid_completion'", "fwguide") is [[1L]],
                        "真实漩涡引导终杀同事务写入赛季58来源，尚未领取奖励");
                    That(missionPackets.Any(body => body.SequenceEqual(authoritative.ToPayload())),
                        "真实漩涡终结后由原Notice发送权威1801，不依赖领奖");
                    return;
                }
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
            That(resultShown && victoryShown, "引导真实24返城后接收601及1523成功电影，不套587");
            string beforeSkip = fixture.Snapshot("SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id);
            peer.Send(659, [0]);
            var skipped = Read();
            That(skipped.Any(row => row is { Kind: 1, Opcode: 659, Body: [1] }) &&
                skipped.Any(row => row is { Kind: 0, Opcode: 599, Body.Length: 0 }) &&
                !skipped.Any(row => row is { Kind: 0, Opcode: 600 } or { Kind: 0, Opcode: 1523, Body: [4, ..] }),
                "引导659零值只请求跳过并收到599，不能提前打开牌面");
            peer.Send(660, [1, 3]);
            That(!Read().Any(row => row is { Kind: 1, Opcode: 660, Body: [1] }) && beforeSkip ==
                fixture.Snapshot("SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id),
                "引导跳过尚未真实确认，提前660拒绝且资产收据不变");
            if (route == 0)
            {
                clock.Advance(19); Read();
                That(!cardsShown && beforeSkip == fixture.Snapshot(
                    "SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id),
                    "缺失电影确认时，演出期限之前不提前翻牌或发奖");
                clock.Advance(1); Read();
                That(cardsShown && cardChoices.SetEquals(new byte[] { 1, 0 }) && beforeSkip == fixture.Snapshot(
                    "SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id),
                    "引导缺失659一时按原20秒演出期限开放两类牌，实际资产仍等待660领取");
            }
            peer.Send(659, [1]);
            var completedMovie = Read();
            That(completedMovie.Any(row => row is { Kind: 1, Opcode: 659, Body: [1] }) && cardsShown &&
                cardChoices.SetEquals(new byte[] { 1, 0 }), "引导真实电影完成后1523四及两种600空选择表开放翻牌");
            foreach (byte kind in new byte[] { 1, 0 })
            {
                peer.Send(660, [kind, 3]);
                var claimed = Read();
                int ack = claimed.FindIndex(row => row is { Kind: 1, Opcode: 660, Body: [1] });
                var display = claimed.SingleOrDefault(row => row is { Kind: 0, Opcode: 600 });
                That(ack >= 0 && display is not null && claimed.IndexOf(display) < ack && display.Body[0] == kind &&
                    display.Body[1] == 1 && U16(display.Body, 2) == peer.Id && display.Body[4] == 3 &&
                    display.Body[5] > 0 && display.Body.Length == 6 + 16 * display.Body[5],
                    "引导独立原奖励非零卡位实际领取，原16B产物展示先于660成功");
                string receipts = fixture.Snapshot("SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id);
                peer.Send(660, [kind, 3]);
                That(Read().Any(row => row is { Kind: 1, Opcode: 660, Body: [1] }) && receipts ==
                    fixture.Snapshot("SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id),
                    "引导重复660只重放原卡位，不重复资产或收据");
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
            var entered = AntonTcpProbe.TakeFiendwarEntry(peer, dungeon);
            That(entered.Any(row => row is { Kind: 1, Opcode: 15, Body: [1] }), "引导36确认后由真实对象键握手发布选图回执");
            That(entered.Any(row => row is { Kind: 0, Opcode: 28 }), "引导真实36进入：" + dungeon);
            That(entered.Count(row => row is { Kind: 0, Opcode: 27 }) == 1 &&
                entered.FindIndex(row => row is { Kind: 0, Opcode: 27 }) < entered.FindIndex(row => row is { Kind: 0, Opcode: 28 }),
                "引导36确认后只打开一次原生选图场景，再完整加载");
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
                    var deathRequest = FormalDungeonTcpTests.Death(U16(map, offset + 6), peer.Id, map[offset + 12]);
                    var before = deathMission == 0 || writerDeadline > 1 ? null : ReadMissions();
                    long sourcesBefore = SourceCount();
                    var sourceMap = deathMission == 0 ? null : definition.Mazes.Single(row => row.SourceOrdinal == info[7])
                        .MapSpecification!.Single(row => row.Position == path[step]);
                    var actualMap = sourceMap is null ? null : sourceMap with
                    {
                        Id = checked((int)U32(map, 14)),
                        Path = SelfTestCatalogs.Dungeons(resources.Archive).MapPath(checked((int)U32(map, 14)))!
                    };
                    var room = actualMap is null ? null : SelfTestCatalogs.Rooms(resources.Archive).Get(actualMap);
                    var sourceMonster = room is null || map[offset + 13] >= 5 ? null : room.Monsters[checked((int)U32(map, offset + 2))];
                    if (sourceMonster is not null)
                        That(sourceMonster.MonsterId == U32(map, offset + 8), "原wire ordinal定位实际MAP怪物资源行");
                    bool eligible = sourceMonster is { SpecialChild: null } && sourceMonster.MonsterId > 0;
                    bool boss = eligible && sourceMonster!.RankKind == "[boss]" && room!.DeclaredType != "[dummy]";
                    long growthBefore = GrowthCount();
                    if (writerDeadline != 0 && eligible)
                    {
                        var originalWriterGate = (SemaphoreSlim)(typeof(SqliteWriteGate)
                            .GetField("Gate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                            .GetValue(null) ?? throw new InvalidOperationException("原writer gate缺失"));
                        var waiterField = typeof(SemaphoreSlim).GetField("m_waitCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            ?? typeof(SemaphoreSlim).GetField("_waitCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("原writer waiter计数不可观测");
                        var plannedBeforeWait = clock.GetUtcNow();
                        using (var holdDb = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                            { DataSource = fixture.Database, Pooling = false }.ToString()))
                        {
                            holdDb.Open();
                            using var heldWriter = holdDb.BeginWrite("FiendwarGuideTcpTests.DeathWriterHold");
                            using var heldTransaction = heldWriter.Transaction;
                            That(originalWriterGate.CurrentCount == 0 && SqliteWriteGate.ActiveWriters == 1, "Guide原writer已真实持有");
                            peer.Send(39, deathRequest);
                            bool queued = SpinWait.SpinUntil(() => Convert.ToInt32(waiterField.GetValue(originalWriterGate)) >= 1,
                                TimeSpan.FromSeconds(10));
                            That(queued, "Guide真实39已冻结原死亡并在原writer闸门排队");
                            // 只推进同一Battle时钟；不执行定时回调，以隔离writer等待跨双deadline。
                            clock.AdvanceMillisecondsWithoutCallbacks(1810 * 1000);
                            That(clock.GetUtcNow() == plannedBeforeWait.AddSeconds(1810), "真实排队后才跨过Guide战场和遭遇时限");
                        }
                        Exception? delayedDeathFailure = null;
                        List<WirePacket> delayedDeathPackets = [];
                        try { delayedDeathPackets = Read(); }
                        catch (Exception error) { delayedDeathFailure = error; }
                        long delayedGrowth = GrowthCount(), delayedSources = SourceCount();
                        Console.WriteLine($"GUIDE_WRITER_WAIT_OBS mode={writerDeadline} growth={delayedGrowth} sources={delayedSources} failure={delayedDeathFailure?.GetType().Name}");
                        That(delayedDeathFailure is null && delayedDeathPackets.Any(row => row is { Kind: 0, Opcode: 38 }) &&
                            delayedGrowth == growthBefore + 1 && delayedSources == sourcesBefore + (writerDeadline == 1 ? 1 : 0),
                            "时限内冻结的Guide死亡跨writer与双deadline后仍提交原成长");
                        if (writerDeadline == 1)
                            That(ReadMissions().Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 1).Count == before!.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 1).Count + 1,
                                "有效战令季跨Battle期限仍仅推进原击杀来源一次");
                        string committedDeath = DeathSnapshot();
                        peer.Send(39, deathRequest); Read();
                        That(DeathSnapshot() == committedDeath, "跨writer期限后原39重报仍只读原已提交死亡");
                        writerDeadlineChecked = true;
                        return;
                    }
                    if (deathMission != 0 && failDeath && eligible)
                    {
                        string saved = DeathSnapshot();
                        fixture.Sql("CREATE TRIGGER fail_guide_bp BEFORE INSERT ON account_battle_pass_source_events " +
                            "WHEN NEW.source_kind='fiendwar_guide_monster_death' BEGIN SELECT RAISE(ABORT,'guide battle pass rollback'); END");
                        try
                        {
                            That(originalPartyRemoval is not null, "故障前原自然队伍名册已绑定本场身份");
                            int faultConnection = -1;
                            That(SpinWait.SpinUntil(() =>
                            {
                                var selections = fixture.Events().Where(row => row.GetProperty("Kind").GetString() == "selected" &&
                                    row.GetProperty("Detail").GetProperty("Character").GetUInt16() == peer.Id).ToArray();
                                if (selections.Length != 0)
                                    faultConnection = selections[^1].GetProperty("Detail").GetProperty("Connection").GetInt32();
                                return faultConnection >= 0;
                            }, TimeSpan.FromSeconds(10)), "选角日志绑定本次故障的原角色连接");
                            int eventBoundary = fixture.Events().Length;
                            peer.Send(39, deathRequest);
                            // 故障连接的移除通知受原身份守卫约束，只允许零条或一条完整原名册通知后关闭。
                            peer.ClosedAfterFiendwarPartyRemoval(originalPartyRemoval!);
                            That(SpinWait.SpinUntil(() => fixture.Events().Skip(eventBoundary).Any(row =>
                                row.GetProperty("Kind").GetString() == "disconnected" &&
                                row.GetProperty("Detail").GetProperty("Connection").GetInt32() == faultConnection &&
                                row.GetProperty("Detail").GetProperty("Protocol").GetString() == "GAME"),
                                TimeSpan.FromSeconds(10)), "原故障连接断线清理完成后才核对回滚账本");
                            var failureEvents = fixture.Events().Skip(eventBoundary).ToArray();
                            var receivedDeaths = failureEvents.Select((row, index) => (row, index)).Where(entry =>
                                entry.row.GetProperty("Kind").GetString() == "recv" &&
                                entry.row.GetProperty("Detail").GetProperty("Connection").GetInt32() == faultConnection &&
                                entry.row.GetProperty("Detail").GetProperty("Kind").GetInt32() == 1 &&
                                entry.row.GetProperty("Detail").GetProperty("Opcode").GetInt32() == 39 &&
                                entry.row.GetProperty("Detail").GetProperty("Body").GetString() == Convert.ToHexString(deathRequest)).ToArray();
                            That(receivedDeaths.Length == 1, "事件边界后仅原连接接收一次本次完整死亡请求");
                            var deathEvents = failureEvents.Skip(receivedDeaths[0].index + 1).Where(row =>
                                row.GetProperty("Detail").TryGetProperty("Connection", out var connection) &&
                                connection.GetInt32() == faultConnection).ToArray();
                            var closures = deathEvents.Where(row => row.GetProperty("Kind").GetString() == "closed").ToArray();
                            That(closures.Length == 1 &&
                                closures[0].GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
                                closures[0].GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'guide battle pass rollback'.",
                                "原死亡请求仅因本次战令触发器的精确SQLite异常关闭");
                            That(deathEvents[^1].GetProperty("Kind").GetString() == "disconnected",
                                "本次故障关闭后原连接完整断线，不接受无关断开");
                            var sent = deathEvents.Where(row => row.GetProperty("Kind").GetString() == "send")
                                .Select(row => row.GetProperty("Detail")).ToArray();
                            That(sent.Length <= 1 && sent.All(row => row.GetProperty("Kind").GetInt32() == 0 &&
                                row.GetProperty("Opcode").GetInt32() == 9 &&
                                row.GetProperty("Body").GetString() == Convert.ToHexString(originalPartyRemoval!)),
                                "故障后仅允许至多一条完整原队伍移除通知，不发送死亡或其它成功帧");
                            That(DeathSnapshot() == saved,
                                "战令来源晚期失败回滚原成长回执、角色、来源、任务和赛季版本且不发死亡成功");
                            rollbackChecked = true;
                        }
                        finally { fixture.Sql("DROP TRIGGER fail_guide_bp"); }
                        return;
                    }
                    peer.Send(39, deathRequest);
                    That(Read().Any(row => row is { Kind: 0, Opcode: 38 }), "引导原房间怪物真实死亡");
                    if (deathMission != 0)
                    {
                        if (eligible)
                            That(GrowthCount() == growthBefore + 1 && fixture.Query(
                                "SELECT COALESCE(sum(amount),0) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id) is [[0L]],
                                "Guide原成长事务记录真实死亡但保持禁用杀怪经验");
                        if (eligible) guideDeaths++;
                        if (boss) guideBossDeaths++;
                        var after = ReadMissions();
                        bool hasKill = before!.Slots.Any(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 1);
                        bool hasBoss = before.Slots.Any(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 2);
                        That(SourceCount() == sourcesBefore + (eligible && (hasKill || boss && hasBoss) ? 1 : 0),
                            "真实Guide原死亡按原EventId只记一次来源，非目标演员不借来源");
                        foreach (var slot in before.Slots)
                        {
                            bool advances = eligible && slot.Instance.Key.Category == 0 &&
                                (slot.Instance.Key.Id == 1 || boss && slot.Instance.Key.Id == 2);
                            long expected = advances ? Math.Min(slot.Instance.Definition.RequiredCount, slot.Count + 1) : slot.Count;
                            That(after.Slots.Single(row => row.Instance.Key == slot.Instance.Key).Count == expected,
                                "真实死亡只推进资源击杀定义，领主取原MAP rank并排除dummy");
                        }
                        That(after.Season.TotalExperience == before.Season.TotalExperience,
                            "击杀只累计任务进度，不提前领取任务经验");
                        string saved = DeathSnapshot();
                        peer.Send(39, deathRequest); Read();
                        That(DeathSnapshot() == saved, "同一原39重报不重复成长、来源、进度或季版本");
                    }
                    That(fixture.Query("SELECT count(*) FROM character_fiendwar_title_facts WHERE character_id=$p0", peer.Id) is [[0L]],
                        "官方Guide真实击杀与清图不产生团队漩涡称号事实");
                }
            }
        }
        BattlePassMissionState ReadMissions()
        {
            var observer = new CharacterRepository(fixture.Database);
            observer.ConfigureBattlePassMissions(new BattlePassMissionPolicy("fiendwar-notice-fixture", startAt,
                BattlePassCatalog.Load(resources.Archive)));
            using var observerOwner = new AccountOwners().Acquire("fwguide", () => { });
            return observer.ReadBattlePassMissions(observerOwner, clock.GetUtcNow())!;
        }
        long SourceCount() => deathMission == 0 ? 0 : Convert.ToInt64(fixture.Query(
            "SELECT count(*) FROM account_battle_pass_source_events WHERE account='fwguide' AND source_kind='fiendwar_guide_monster_death'")[0][0]);
        long GrowthCount() => deathMission == 0 ? 0 : Convert.ToInt64(fixture.Query(
            "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id)[0][0]);
        string DeathSnapshot() => System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            fixture.Query("SELECT * FROM characters ORDER BY id"),
            fixture.Query("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id"),
            fixture.Query("SELECT * FROM account_battle_pass_source_events ORDER BY account,source_kind,source_id"),
            fixture.Query("SELECT * FROM account_battle_pass_mission_progress ORDER BY account,season_key,category,period_key,mission_id"),
            fixture.Query("SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key")
        });

        void Select(ushort slot = 0)
        {
            peer.Select(slot);
            foreach (var (type, usage) in RaidPanelTests.ReadWeeklyClears(peer.RaidWeeklyClearInfo)) clears[type] = usage;
            Read();
        }

        List<WirePacket> Read()
        {
            var rows = AntonTcpProbe.Barrier(peer, "fiendwar guide");
            foreach (var row in rows)
            {
                That(row is not { Kind: 0, Opcode: 587 }, "引导仅使用原生1523状态合同");
                if (row is { Kind: 0, Opcode: 29 }) { inDungeonScene = true; returnedToField = false; }
                if (row is { Kind: 0, Opcode: 24, Body: [57, ..] }) { inDungeonScene = false; returnedToField = true; }
                if (row is { Kind: 0, Opcode: 601, Body.Length: 10 })
                {
                    That(!inDungeonScene && returnedToField, "引导终局实际24返城先于601，避免旧副本场景清掉演出");
                    resultShown = true;
                }
                if (row is { Kind: 0, Opcode: 1523, Body: [3, 0, 0] })
                {
                    That(resultShown, "引导601结果先于1523成功电影");
                    victoryShown = true;
                }
                if (row is { Kind: 0, Opcode: 1523, Body: [4, 0, 0] }) cardsShown = true;
                if (row is { Kind: 0, Opcode: 600, Body: [byte kind, 0] })
                {
                    That(cardsShown, "引导1523四先于600空选择表");
                    cardChoices.Add(kind);
                }
            }
            foreach (var roster in rows.Where(row => row is { Kind: 0, Opcode: 9, Body.Length: > 11 } && row.Body[9] == 0))
            {
                That(U16(roster.Body) == 1 && U16(roster.Body, 2) == 9999 && U16(roster.Body, 4) != 0 &&
                    roster.Body[6] != 0 && roster.Body[7] == 39 && roster.Body[8] == 0 && roster.Body[10] == 0,
                    "故障前原自然队伍名册保留完整队伍、server与channel身份");
                // 沿现有TCP名册读宽独立消费标题和成员段，绑定本连接的原角色/槽0。
                That(roster.Body[12] == 0, "本场原自然队伍名册使用完整自定义标题段");
                int memberAt = checked(17 + (int)U32(roster.Body, 13) + 24);
                That(roster.Body[memberAt++] == 1 && roster.Body[memberAt] == 0 &&
                    U16(roster.Body, memberAt + 1) == peer.Id,
                    "故障前原名册唯一成员绑定本连接原角色及槽0");
                memberAt += 8;
                That(roster.Body.Length == memberAt + 10 && roster.Body[memberAt] == 0 && roster.Body[memberAt + 2] == 0,
                    "原名册无跨频道成员且队长仍为槽0，完整消费正文");
                originalPartyRemoval = roster.Body[..9].Concat(new byte[] { 3, 0 }).ToArray();
            }
            if (battlePass) missionPackets.AddRange(rows.Where(row => row is { Kind: 0, Opcode: 1801 }).Select(row => row.Body));
            foreach (var row in rows.Where(row => row is { Kind: 0, Opcode: 1446 }))
                foreach (var (type, usage) in RaidPanelTests.ReadWeeklyClears(row.Body)) clears[type] = usage;
            foreach (var row in rows.Where(row => row is { Kind: 0, Opcode: 1530, Body.Length: > 0 }))
                for (int index = 0; index < row.Body[0]; index++) symbols[U32(row.Body, 1 + 8 * index)] = U32(row.Body, 5 + 8 * index);
            return rows;
        }
    }
}


