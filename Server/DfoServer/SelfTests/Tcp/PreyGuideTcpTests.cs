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
        That(started.Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }), "TCP真实666启动普雷引导");
        That(fixture.Query("SELECT raid_type,state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[3L, 0L]],
            "TCP费用与类型3生命周期共同提交");
        peer.Send(666);
        That(Read("start replay").Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }), "TCP重复666只回放成功");
        clock.Advance(25);
        peer.Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(U32(created.Single(packet => packet is { Kind: 1, Opcode: 664 }).Body, 1)); }));
        var prepared = Read("prepared");
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
        var report = new AntonTcpProbe.AntonProbeReport();
        foreach (int dungeon in branch == 0 ? new[] { 291100391, 291100393 } : new[] { 291100390, 291100392 })
            AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true);
        Claim(0);
        AntonTcpProbe.Enter(peer, catalog, 291100394, report, clear: true);
        AntonTcpProbe.Enter(peer, catalog, 291100397, report, clear: true);
        Claim(1);
        That(fixture.Query("SELECT state,phase0_event IS NOT NULL,phase1_event IS NOT NULL FROM character_raid_runs WHERE character_id=$p0", peer.Id)
            is [[2L, 1L, 1L]], "TCP真实MAP死亡与退场生成两阶段最终通关记录");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0 AND raid_type=3", peer.Id)
            is [[4L]], "TCP两阶段两类奖励各提交一次");

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

        void Claim(int phase)
        {
            foreach (byte kind in new byte[] { 1, 0 })
            {
                peer.Send(660, [kind, 0]);
                var packets = Read("reward");
                int ack = packets.FindIndex(packet => packet is { Kind: 1, Opcode: 660, Body: [1] });
                var display = packets.LastOrDefault(packet => packet is { Kind: 0, Opcode: 600 } && packet.Body[0] == kind);
                That(ack >= 0 && display is not null && packets.IndexOf(display) < ack, "TCP600真实产物在660成功前发布：" + phase);
                var body = display!.Body;
                That(body[1] == 1 && U16(body, 2) == peer.Id && body[4] == 0 && body[5] > 0 && body.Length == 6 + 16 * body[5],
                    "TCP普雷通用600完整16B行且归本人");
                for (int index = 0; index < body[5]; index++)
                {
                    int at = 6 + index * 16;
                    That(body[at] == 0 && body[at + 7] == 0 && U32(body, at + 8) == 0 &&
                        U16(body, at + 5) > 0 && resources.ItemResources.Items.TryGet(checked((int)U32(body, at + 1)), out _),
                        "TCP600每项使用真实资源产物及完整显式字段");
                }
                if (kind == 1)
                {
                    var receipts = fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0", peer.Id);
                    peer.Send(660, [kind, 0]);
                    That(Read("reward replay").Any(packet => packet is { Kind: 1, Opcode: 660, Body: [1] }) &&
                        fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0", peer.Id)[0][0].Equals(receipts[0][0]),
                        "TCP重复660回放确认且不多写收据");
                }
            }
        }
        List<WirePacket> Read(string action) => AntonTcpProbe.Barrier(peer, "prey " + action);
    }

    static Dictionary<int, int> ReadSymbols(byte[] body)
    {
        var result = new Dictionary<int, int>();
        for (int index = 0; index < body[0]; index++) result.Add(checked((int)U32(body, 1 + index * 8)), unchecked((int)U32(body, 5 + index * 8)));
        return result;
    }
}
