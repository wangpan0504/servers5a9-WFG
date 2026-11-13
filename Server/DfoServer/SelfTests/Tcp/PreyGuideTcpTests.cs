using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PreyGuideTcpTests
{
    internal static Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
        => RunFixtureAsync(source, root, resources, false);

    internal static Task RunBattlePassDeathAsync(ServerOptions source, string root, TcpTestResources resources)
        => RunFixtureAsync(source, root, resources, true);

    static async Task RunFixtureAsync(ServerOptions source, string root, TcpTestResources resources, bool death)
    {
        var clock = new LukeRaidClockTests.Clock();
        BattlePassMissionPolicy? deathPolicy = null;
        if (death)
        {
            var start = DateTimeOffset.Parse("2026-09-10T06:00:00+08:00");
            deathPolicy = new("prey-death-fixture", start, BattlePassCatalog.Load(resources.Archive));
            clock = new LukeRaidClockTests.Clock(start.AddDays(44).AddHours(2));
            That(new[] { 1, 2 }.All(id => deathPolicy.Active(clock.GetUtcNow())
                .Any(row => row.Key.Category == 0 && row.Key.Id == id)), "原调度同日激活日击杀与领主任务");
            source = source with { BattlePassSeasonKey = "prey-death-fixture", BattlePassSeasonStart = start };
        }
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, death ? "prey-guide-battlepass-death" : "prey-guide-flow"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire("preyflow", () => { }))
        {
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("PreyFlow", resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            var role = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                CompletedQuests = [8935], InitialLearning = learning });
            var items = resources.ItemResources;
            InventoryService.Commit(repository, owner, role,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(role, [new(10309342, 2)]));
        }
        var catalog = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(3)!.Resources!.EntryCatalog!;
        using var peer = Open(fixture, "preyflow", gamePort: ChannelTcpTests.Endpoints(fixture)[42], channelNumber: 42);
        var observed = new List<WirePacket>();
        if (deathPolicy is not null)
        {
            peer.ExpectedOpenEvents = [282, 2449, 414, 181, 2532, 266, 2520];
            peer.ExpectedBattlePassEnd = checked((uint)deathPolicy.End.ToUnixTimeSeconds());
        }
        peer.Select();
        Read("selected");
        if (!death)
        {
            peer.Send(664, Build(writer => { writer.Write((byte)3); writer.Write(6u); writer.Write("normal"u8); writer.Write(new byte[] { 0, 0, 0, 7 }); }));
            That(Read("normal created").Any(row => row is { Kind: 1, Opcode: 664, Body: [1, ..] }), "普通普雷创建不转换为引导");
            foreach (byte slot in new byte[] { 11, 21, 34, 34 })
            {
                peer.Send(669, Build(writer => { writer.Write(2u); writer.Write((uint)peer.Id); writer.Write((uint)slot); }));
                var assigned = Read("prey formation");
                That(assigned.Any(row => row is { Kind: 1, Opcode: 669, Body: [1] }), "普雷真实op2反复编制均有成功ACK，解除客户端忙标记");
                var roster = assigned.Last(row => row is { Kind: 0, Opcode: 591 } && U32(row.Body, 4) == 3).Body;
                int length = checked((int)U32(roster, 12));
                That(roster[18 + length] == slot / 10 && roster[42 + length] == slot,
                    "591完整成员同时携带真实分队与独立普雷槽位");
            }
            peer.Send(666);
            That(Read("normal unsupported").Any(row => row is { Kind: 1, Opcode: 666, Body: [0, 19, 0] }) &&
                fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[0L]],
                "普通攻坚战仍明确拒绝，不借引导资源扣票或伪造开战");
            peer.Send(665, BitConverter.GetBytes(peer.Id));
            That(Read("normal left").Any(row => row is { Kind: 1, Opcode: 665, Body: [1, 0] }), "退出普通团后可继续创建引导");
        }
        peer.Send(664, Build(writer => { writer.Write((byte)3); writer.Write(5u); writer.Write("guide"u8); writer.Write(new byte[] { 1, 0, 0, 7 }); }));
        var created = Read("created");
        That(created.Any(packet => packet is { Kind: 1, Opcode: 664, Body: [1, ..] }), "TCP真实664创建单人引导团");
        var guideRoster = created.Single(row => row is { Kind: 0, Opcode: 591 } && U32(row.Body, 4) == 3).Body;
        That(guideRoster[42 + checked((int)U32(guideRoster, 12))] == 11, "引导首次名册已落红队首槽");
        peer.Send(666);
        var started = Read("started");
        That(started.Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }) &&
            started.Any(packet => packet is { Kind: 0, Opcode: 587, Body: [1, 0] }) &&
            started.Any(packet => packet is { Kind: 0, Opcode: 594 }), "TCP真实666启动普雷引导并发布登船state1与594");
        That(fixture.Query("SELECT raid_type,state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[3L, 0L]],
            "TCP费用与类型3生命周期共同提交");
        peer.Send(666);
        That(Read("start replay").Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }), "TCP重复666只回放成功");
        List<WirePacket> prepared;
        if (!death)
        {
            peer.Send(36, Convert.FromHexString("3B038C04B000053B0002000000000000"));
            prepared = Read("boarded");
            That(prepared.Any(packet => packet is { Kind: 0, Opcode: 24, Body: [59, 3, ..] }) &&
                prepared.Any(packet => packet is { Kind: 0, Opcode: 587, Body: [2, 0] }),
                "普雷引导发送原36登船后进入59/3并切换state2开放战斗");
        }
        else
        {
            clock.Advance(25);
            peer.Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(U32(created.Single(packet => packet is { Kind: 1, Opcode: 664 }).Body, 1)); }));
            prepared = Read("prepared");
        }
        var symbolPacket = prepared.LastOrDefault(packet => packet is { Kind: 0, Opcode: 583 }) ??
            started.Last(packet => packet is { Kind: 0, Opcode: 583 });
        var symbols = ReadSymbols(symbolPacket.Body);
        That(!symbols.ContainsKey(102), "普雷初始击杀全员零值不作为583执行事件发送");
        int branch = symbols[129];
        if (deathPolicy is not null)
        {
            VerifyDeaths(branch == 0 ? 291100391 : 291100390);
            return;
        }
        var gate4 = resources.Towns.Entrance(new(59, 3, 1164, 176), new(59, 4, 0, 0), new HashSet<int> { 8935 })!;
        peer.Send(36, Area(59, 4, gate4.X, gate4.Y, 59, 3));
        That(Read("board left").Any(packet => packet is { Kind: 0, Opcode: 24, Body: [59, 4, ..] }),
            "普雷引导登船后可切入59/4左侧甲板待机");
        var landed = new List<WirePacket>();
        for (int stop = 0; stop < 16 && !landed.Any(packet => packet is { Kind: 0, Opcode: 29 }); stop++)
        {
            clock.Advance(15);
            for (int attempt = 0; attempt < 20 && !landed.Any(packet => packet is { Kind: 0, Opcode: 29 }); attempt++)
            {
                landed.AddRange(Read("guide landing " + stop));
                if (!landed.Any(packet => packet is { Kind: 0, Opcode: 29 }) &&
                    landed.Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }))
                    await Task.Delay(10);
                else break;
            }
        }
        That(landed.Any(packet => packet is { Kind: 0, Opcode: 28 }) &&
            landed.Any(packet => packet is { Kind: 0, Opcode: 29 }),
            "普雷引导在59/4待机随斯雷克坤移动自动降落进入开放副本");
        peer.Send(37, new byte[8]);
        That(Read("guide landing loaded").Any(packet => packet is { Kind: 1, Opcode: 37, Body: [1] }), "普雷引导降落副本完成加载");
        peer.Send(40, [0x74, 0, 4, 1, 0, 0]);
        That(Read("guide player died").Any(packet => packet is { Kind: 0, Opcode: 32 }), "普雷引导副本内角色死亡");
        clock.Advance(10);
        var autoReturned = new List<WirePacket>();
        for (int attempt = 0; attempt < 50 && !autoReturned.Any(packet => packet is { Kind: 0, Opcode: 24, Body: [59, 3, ..] }); attempt++)
        {
            await Task.Delay(10);
            autoReturned.AddRange(Read("guide death auto return"));
        }
        That(autoReturned.Any(packet => packet is { Kind: 0, Opcode: 24, Body: [59, 3, ..] }) &&
            fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[59L, 3L]],
            "普雷副本内全员死亡倒计时归0自动退场回到鸟背59/3");
        var guideScript = PreyGuideScript.Parse(resources.Archive.ReadTokens(PreyGuideScript.Path),
            resources.Archive.ReadTokens(PreyGuideScript.Path + ".symbol"), resources.Archive.ResolveString);
        var waiting = guideScript.WaitingRoom ?? throw new InvalidOperationException("引导候战区缺失");
        That(guideScript.Issues.IsEmpty && !(waiting.Town == 59 && waiting.Area == 3),
            "引导候战区可读，且不是鸟背59/3");
        var report = new AntonTcpProbe.AntonProbeReport { Observe = observed.Add };
        int phaseStart = observed.Count;
        foreach (int dungeon in branch == 0 ? new[] { 291100391, 291100393 } : new[] { 291100390, 291100392 })
            AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true);
        await Reveal(0, phaseStart);
        phaseStart = observed.Count;
        AntonTcpProbe.Enter(peer, catalog, 291100394, report, clear: true);
        AntonTcpProbe.Enter(peer, catalog, 291100397, report, clear: true, leave: false, allowPhaseReward: true);
        await Reveal(1, phaseStart);
        That(fixture.Query("SELECT state,phase0_event IS NOT NULL,phase1_event IS NOT NULL FROM character_raid_runs WHERE character_id=$p0", peer.Id)
            is [[2L, 1L, 1L]], "TCP真实MAP死亡与退场生成两阶段最终通关记录");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0 AND raid_type=3", peer.Id)
            is [[4L]], "TCP两阶段两类奖励各提交一次");
        That(observed.Count(row => row is { Kind: 0, Opcode: 587, Body: [4, 1] }) == 2 &&
            observed.All(row => row.Opcode != 660), "真实两阶段各揭示一次，单人引导全链不发送或依赖660");

        void VerifyDeaths(int dungeon)
        {
            peer.Send(15, BitConverter.GetBytes(dungeon));
            That(Read("death selection").Any(row => row is { Kind: 1, Opcode: 15, Body: [1] }), "原15打开普雷引导选图");
            var request = (byte[])AntonTcpProbe.LiveSelect210.Clone();
            BitConverter.GetBytes(dungeon).CopyTo(request, 0);
            peer.Send(16, request);
            var entered = Read("death entry");
            var info = entered.Single(row => row is { Kind: 0, Opcode: 28 }).Body;
            var map = entered.Single(row => row is { Kind: 0, Opcode: 29 }).Body;
            That(U32(info) == dungeon, "原16进入本分支首圣地");
            var path = AntonTcpProbe.Route(catalog, dungeon, info[7], map[0], map[1], info[8], info[9]);
            int deaths = 0, bosses = 0;
            for (int step = 0; step < path.Length; step++)
            {
                if (step != 0)
                {
                    var move = new byte[100]; move[0] = checked((byte)path[step].X); move[1] = checked((byte)path[step].Y);
                    peer.Send(45, move);
                    map = Read("death move").Single(row => row is { Kind: 0, Opcode: 29 }).Body;
                }
                peer.Send(37, new byte[8]);
                That(Read("death load").Any(row => row is { Kind: 1, Opcode: 37, Body: [1] }), "原37加载真实房间");
                foreach (int index in Enumerable.Range(0, map[18]).OrderBy(index => map[19 + index * 22 + 13] == 3))
                {
                    int at = 19 + index * 22;
                    if (map[at + 21] == 1 || map[at + 13] == 9) continue;
                    var monster = CurrentMonster(U16(map, at + 6));
                    That(monster.MonsterId == U32(map, at + 8) && monster.SourceOrdinal == U32(map, at + 2),
                        "原当前Run存活怪物与真实29对象键、资源身份一致");
                    bool eligible = monster.MonsterId > 0 && monster.CanReportDeath && !monster.IsFriendly &&
                        !monster.IsSpecialChild && monster.AiCharacter is null;
                    bool boss = eligible && monster.IsBoss;
                    var before = Missions(); long growthBefore = Growth(), sourcesBefore = Sources();
                    var deathRequest = FormalDungeonTcpTests.Death(U16(map, at + 6), peer.Id, map[at + 12]);
                    peer.Send(39, deathRequest);
                    That(Read("death").Any(row => row is { Kind: 0, Opcode: 38 }), "原39接受本图真实死亡");
                    if (eligible) deaths++;
                    if (boss) bosses++;
                    That(Growth() == growthBefore + (eligible ? 1 : 0) && Sources() == sourcesBefore + (eligible ? 1 : 0),
                        "有效死亡沿原成长事务各记一次成长与普雷Guide战令来源");
                    var after = Missions();
                    foreach (var slot in before.Slots)
                    {
                        bool advances = eligible && slot.Instance.Key.Category == 0 &&
                            (slot.Instance.Key.Id == 1 || boss && slot.Instance.Key.Id == 2);
                        long expected = advances ? Math.Min(slot.Instance.Definition.RequiredCount, slot.Count + 1) : slot.Count;
                        That(after.Slots.Single(row => row.Instance.Key == slot.Instance.Key).Count == expected,
                            "日任务1按有效死亡、日任务2按原IsBoss推进，其余任务不变");
                    }
                    That(after.Season.TotalExperience == before.Season.TotalExperience, "死亡不自动领取任务经验");
                    string committed = Snapshot();
                    peer.Send(39, deathRequest); Read("death replay");
                    That(Snapshot() == committed, "原39重报不增加成长、来源、进度或赛季版本");
                }
            }
            That(deaths > 0 && bosses > 0, "首真实圣地覆盖有效死亡与原IsBoss领主死亡");
            var final = Missions();
            That(final.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 1).Count == Math.Min(10, deaths) &&
                final.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 2).Count == Math.Min(2, bosses),
                "两项原任务累计值与本场真实死亡一一对应");
        }
        DungeonPlannedMonster CurrentMonster(ushort key)
        {
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            static object Field(object target, string name) => target.GetType().GetField(name, fields)!.GetValue(target)!;
            static object Property(object target, string name) => target.GetType().GetProperty(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;
            var timers = Field(Field(fixture, "runtimeHost"), "<gameplayTimers>P");
            lock (Field(timers, "gate"))
            {
                var leases = (System.Collections.IDictionary)Field(timers, "leases");
                var battle = Property(leases.Values.Cast<object>().Single(), "Battle");
                var members = (System.Collections.IDictionary)Field(battle, "members");
                var session = (GameSession)Property(members.Keys.Cast<object>().Single(), "Session");
                var formal = typeof(GameSession).GetProperty("formalDungeon", fields)!.GetValue(session)!;
                var run = (SinglePlayerDungeonRun)Property(formal, "Run");
                return run.CurrentLivingMonster(key) ?? throw new InvalidOperationException("原Run未签发当前存活怪物");
            }
        }
        BattlePassMissionState Missions()
        {
            var observer = new CharacterRepository(fixture.Database); observer.ConfigureBattlePassMissions(deathPolicy!);
            using var owner = new AccountOwners().Acquire("preyflow", () => { });
            return observer.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
        }
        long Growth() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id)[0][0]);
        long Sources() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE account='preyflow' AND source_kind='prey_guide_monster_death'")[0][0]);
        string Snapshot() => System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            fixture.Query("SELECT * FROM characters ORDER BY id"),
            fixture.Query("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id"),
            fixture.Query("SELECT * FROM account_battle_pass_source_events ORDER BY account,source_kind,source_id"),
            fixture.Query("SELECT * FROM account_battle_pass_mission_progress ORDER BY account,season_key,category,period_key,mission_id"),
            fixture.Query("SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key")
        });

        async Task Reveal(int phase, int start)
        {
            // 终杀后不发42，以真实退场及演出包为完成条件；屏障只负责收取当前已写帧。
            if (phase == 1)
                for (int tick = 0; tick < 50 && !observed.Skip(start).Any(row =>
                    row is { Kind: 0, Opcode: 587, Body: [3, 1] }); tick++)
                {
                    await Task.Delay(10);
                    Read("guide final automatic return");
                }
            var initial = observed.Skip(start).ToList();
            int returned = initial.FindLastIndex(row => row is { Kind: 0, Opcode: 24, Body: [var town, var area, ..] } &&
                town == (phase == 1 ? waiting.Town : 59) && area == (phase == 1 ? waiting.Area : 3));
            int result = initial.FindIndex(row => row is { Kind: 0, Opcode: 601, Body.Length: 10 } && row.Body[1] == phase);
            int movie = initial.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [3, 1] });
            That(returned >= 0 && result > returned && movie > result && initial.All(row => row.Opcode != 600 &&
                row is not { Kind: 0, Opcode: 587, Body: [4, 1] }),
                "引导实际退场后按显示阶段1播放演出，不提前发本人牌面或开窗：真实阶段" + phase);
            That(ReceiptCount(phase) == 0 && fixture.Query("SELECT source FROM character_raid_runs WHERE character_id=$p0", peer.Id)
                is [[PreyGuideRewards.Source]], "演出前无该阶段资产收据且保留真实single来源");
            if (phase == 0)
                That(fixture.Query("SELECT phase0_event IS NOT NULL,phase1_event IS NULL,state FROM character_raid_runs WHERE character_id=$p0", peer.Id)
                    is [[1L, 1L, 0L]], "显示阶段1不改写真实phase0资格，不提前生成phase1事件或最终完成");
            string before = RewardAssets();
            peer.Send(659, [0]);
            var skipped = Read("guide movie skip " + phase);
            int ack = skipped.FindIndex(row => row is { Kind: 1, Opcode: 659, Body: [1] });
            That(ack >= 0 && skipped.FindIndex(row => row is { Kind: 0, Opcode: 599 }) > ack &&
                skipped.All(row => row.Opcode != 600 && row is not { Kind: 0, Opcode: 587, Body: [4, 1] }) && RewardAssets() == before,
                "真实6590只确认及通知跳过，不完成演出、发奖或开窗");
            peer.Send(659, [1]);
            That(Read("guide movie finished " + phase).Any(row => row is { Kind: 1, Opcode: 659, Body: [1] }),
                "真实6591确认当前引导阶段演出完成");
            long began = clock.GetTimestamp();
            // 最终退场已在演出前完成；奖励仍须等真实659完成事实与原自动领奖tick。
            bool Settled() => Displayed() && (phase != 1 || HomeReady());
            for (int tick = 0; tick < 10 && !Settled(); tick++)
            {
                clock.Advance(1);
                await Task.Delay(25);
                Read("guide automatic reward " + phase);
            }
            That(Settled() && clock.GetElapsedTime(began).TotalSeconds <= 10,
                phase == 1
                    ? "二阶段候战落点与翻牌均完成，超时仍失败，不等待资源电影加120秒兜底"
                    : "引导演出完成后由原秒tick交付，不等待资源电影加120秒兜底");
            var shown = observed.Skip(start).ToList();
            int opened = shown.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [4, 1] });
            var personal = shown.Single(row => row is { Kind: 0, Opcode: 600, Body.Length: >= 6 } && row.Body[0] == 1);
            var team = shown.Single(row => row is { Kind: 0, Opcode: 600, Body.Length: >= 6 } && row.Body[0] == 3);
            That(shown.Count(row => row is { Kind: 0, Opcode: 587, Body: [4, 1] }) == 1 &&
                shown.Where(row => row.Opcode == 600).All(row => shown.IndexOf(row) < opened) &&
                shown.Where(row => row.Opcode == 600).Count() == 2 && team.Body[1] == 1,
                "已提交个人合计及完整单人team表全部写出后才唯一开窗，不用kind0逐包覆盖");
            Bytes(personal.Body, Build(writer => { writer.Write((byte)1); writer.Write((byte)1); writer.Write(ExpectedRow(phase, true)); }),
                "个人wire1逐字段匹配原single冻结全部offers合计，保留完整16B行");
            Bytes(team.Body, Build(writer => { writer.Write((byte)3); writer.Write((byte)1); writer.Write(ExpectedRow(phase, false)); }),
                "完整wire3只展示同一真实阶段Kind0原始完整产物");
            if (phase == 0)
            {
                int next = shown.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [2, 1] });
                That(next > opened && ReceiptCount(1) == 0 && fixture.Query(
                    "SELECT phase1_event IS NULL,state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[1L, 0L]],
                    "真实phase0结果实际写完后才开放phase1，显示1不污染资格与业务身份");
                That(!shown.Any(row => row is { Kind: 0, Opcode: 24, Body: [var town, var area, ..] } &&
                    town == waiting.Town && area == waiting.Area) &&
                    fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[59L, 3L]],
                    "一阶段开窗后仍停在鸟背59/3，不进入脚本候战区");
            }
            if (phase == 1)
            {
                int home = shown.FindIndex(row => row is { Kind: 0, Opcode: 24, Body: [var town, var area, ..] } &&
                    town == waiting.Town && area == waiting.Area);
                var saved = fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id);
                That(shown.Count(row => row is { Kind: 0, Opcode: 24, Body: [var town, var area, ..] } &&
                    town == waiting.Town && area == waiting.Area) == 1 && home >= 0 && home < result && home < movie && home < opened &&
                    saved is [[var dbTown, var dbArea]] &&
                    Convert.ToInt64(dbTown) == waiting.Town && Convert.ToInt64(dbArea) == waiting.Area,
                    "二阶段终杀无需42自动回脚本候战区，只迁一次且早于演出和翻牌");
            }
            string committed = RewardAssets();
            int opens = observed.Count(row => row is { Kind: 0, Opcode: 587, Body: [4, 1] });
            peer.Send(659, [1]);
            var replay = Read("guide movie completion replay " + phase);
            That(replay.All(row => row.Opcode != 600 && row is not { Kind: 0, Opcode: 587, Body: [4, 1] }) &&
                observed.Count(row => row is { Kind: 0, Opcode: 587, Body: [4, 1] }) == opens && RewardAssets() == committed,
                "重复659不重复资产、收据或旧阶段结果窗");

            bool Displayed() => ReceiptCount(phase) == 2 && observed.Skip(start).Any(row =>
                row is { Kind: 0, Opcode: 587, Body: [4, 1] }) && (phase != 0 || observed.Skip(start).Any(row =>
                row is { Kind: 0, Opcode: 587, Body: [2, 1] }));
            bool HomeReady()
            {
                bool packet = observed.Skip(start).Any(row => row is { Kind: 0, Opcode: 24, Body: [var town, var area, ..] } &&
                    town == waiting.Town && area == waiting.Area);
                var saved = fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id);
                return packet && saved is [[var dbTown, var dbArea]] &&
                    Convert.ToInt64(dbTown) == waiting.Town && Convert.ToInt64(dbArea) == waiting.Area;
            }
        }
        long ReceiptCount(int phase) => Convert.ToInt64(fixture.Query(
            "SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0 AND raid_type=3 AND phase=$p1", peer.Id, phase)[0][0]);
        string RewardAssets() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind");
        byte[] ExpectedRow(int phase, bool personal)
        {
            string json = (string)fixture.Query($"SELECT phase{phase}_receipt FROM character_raid_runs WHERE character_id=$p0", peer.Id)[0][0]!;
            using var document = JsonDocument.Parse(json);
            That(document.RootElement.GetProperty("Phase").GetInt32() == phase &&
                document.RootElement.GetProperty("Source").GetString() == PreyGuideRewards.Source,
                "牌面来自真实业务阶段及single来源，不能将显示阶段1写入冻结资格");
            var offers = document.RootElement.GetProperty("Offers").EnumerateArray().ToArray();
            var selected = personal ? offers : offers.Where(row => row.GetProperty("Kind").GetByte() == 0).ToArray();
            var rows = new List<byte[]>();
            var positions = new Dictionary<uint, int>();
            foreach (var item in selected.SelectMany(offer => offer.GetProperty("PreyDisplay").EnumerateArray()))
            {
                uint id = item.GetProperty("ItemId").GetUInt32();
                var encoded = Build(writer =>
                {
                    writer.Write(item.GetProperty("RawCode").GetByte()); writer.Write(id);
                    writer.Write(item.GetProperty("DisplayNumber").GetUInt16()); writer.Write(item.GetProperty("RawFlag").GetByte());
                    writer.Write(item.GetProperty("RawTail1").GetUInt32()); writer.Write(item.GetProperty("EnchantUpgrade").GetUInt32());
                });
                if (personal && positions.TryGetValue(id, out int position))
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(5), checked((ushort)(U16(rows[position], 5) + U16(encoded, 5))));
                    rows[position] = encoded;
                }
                else { positions[id] = rows.Count; rows.Add(encoded); }
            }
            return Build(writer =>
            {
                writer.Write(peer.Id); writer.Write(personal ? (byte)0 : selected.Single().GetProperty("Card").GetByte());
                writer.Write(checked((byte)rows.Count)); foreach (var row in rows) writer.Write(row);
            });
        }
        List<WirePacket> Read(string action)
        {
            var rows = AntonTcpProbe.Barrier(peer, "prey " + action);
            observed.AddRange(rows);
            return rows;
        }
    }

    static Dictionary<int, int> ReadSymbols(byte[] body)
    {
        var result = new Dictionary<int, int>();
        for (int index = 0; index < body[0]; index++) result.Add(checked((int)U32(body, 1 + index * 8)), unchecked((int)U32(body, 5 + index * 8)));
        return result;
    }
}
