using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    internal static Task RunBattlePassDeathAsync(ServerOptions source, string root, TcpTestResources resources)
        => ProductionCoreAsync(source, root, resources, false, battlePassDeath: true);

    static void VerifyBattlePassDeaths(TcpTestFixture fixture, TcpPeer peer, RaidDungeonCatalog catalog,
        RaidResources resources, LukeRaidClockTests.Clock clock, BattlePassMissionPolicy policy)
    {
        var entered = Enter([peer], peer, 5054); Load([peer]);
        byte maze = entered[0].Single(row => row is { Kind: 0, Opcode: 28 }).Body[7];
        var rules = LukeSanctumObjectRules.Create(catalog.Script);
        var counts = new Dictionary<int, int> { [117] = 0, [118] = 0, [119] = 0 };
        int deaths = 0, bosses = 0;
        clock.Advance(7);
        Clear(fixture, [peer], [], peer, entered[0], catalog,
            roomCleared: room => ProductionFacility([peer], peer, resources.LukeObjects!, rules, 5054, maze,
                room, counts, 0, catalog.Script),
            waypoints: ProductionFacilityStops(catalog, resources.LukeObjects!, 5054, maze), checkDeath: BeforeDeath,
            expectedBattlePassMissions: MissionBody);
        That(deaths > bosses && bosses > 0, "原5054生产路线覆盖真实普通怪与IsBoss领主死亡");
        var final = Missions();
        That(final.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 1).Count == Math.Min(10, deaths) &&
            final.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 2).Count == Math.Min(2, bosses),
            "原日任务1/2累计值与真实5054死亡对应");

        Action BeforeDeath(ushort key, byte level)
        {
            var monster = CurrentMonster(key);
            bool eligible = monster.MonsterId > 0 && monster.CanReportDeath && !monster.IsFriendly &&
                !monster.IsSpecialChild && monster.AiCharacter is null;
            bool boss = eligible && monster.IsBoss;
            var before = Missions(); long growth = Growth(), sources = Sources();
            return () =>
            {
                if (eligible) deaths++;
                if (boss) bosses++;
                That(Growth() == growth + (eligible ? 1 : 0) && Sources() == sources + (eligible ? 1 : 0),
                    "卢克有效死亡沿原事务各记录一次成长与战令来源，设施和非敌方不借来源");
                var after = Missions();
                foreach (var slot in before.Slots)
                {
                    bool advances = eligible && slot.Instance.Key.Category == 0 &&
                        (slot.Instance.Key.Id == 1 || boss && slot.Instance.Key.Id == 2);
                    long expected = advances ? Math.Min(slot.Instance.Definition.RequiredCount, slot.Count + 1) : slot.Count;
                    That(after.Slots.Single(row => row.Instance.Key == slot.Instance.Key).Count == expected,
                        "真实卢克日击杀按资格推进，日领主仅按当前Run的IsBoss推进");
                }
                That(after.Season.TotalExperience == before.Season.TotalExperience, "真实死亡不自动领取任务经验");
                string committed = Snapshot();
                peer.Send(39, FormalDungeonTcpTests.Death(key, peer.Id, level));
                That(!Drain(peer, expectedBattlePassMissions: MissionBody).Any(row => row is { Kind: 0, Opcode: 38 }) && Snapshot() == committed,
                    "原39重复不增加成长、来源、任务、赛季版本或重复死亡通知");
            };
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
            var observer = new CharacterRepository(fixture.Database); observer.ConfigureBattlePassMissions(policy);
            using var owner = new AccountOwners().Acquire("lkprod1_0", () => { });
            return observer.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
        }
        byte[] MissionBody()
        {
            var state = Missions();
            That(state.Active && state.Slots.Length == 26, "权威季包含完整26个原任务槽");
            using var body = new MemoryStream();
            using var writer = new BinaryWriter(body);
            foreach (var slot in state.Slots)
            {
                writer.Write(checked((ushort)slot.Instance.Key.Id));
                writer.Write(checked((ushort)Math.Min(ushort.MaxValue, Math.Min(slot.Count, slot.Instance.Definition.RequiredCount))));
                writer.Write(slot.Ready ? (byte)1 : (byte)0);
                writer.Write(slot.Completed ? (byte)1 : (byte)0);
            }
            return body.ToArray();
        }
        long Growth() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id)[0][0]);
        long Sources() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE account='lkprod1_0' AND source_kind='luke_raid_monster_death'")[0][0]);
        string Snapshot() => System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            fixture.Query("SELECT * FROM characters ORDER BY id"),
            fixture.Query("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id"),
            fixture.Query("SELECT * FROM account_battle_pass_source_events ORDER BY account,source_kind,source_id"),
            fixture.Query("SELECT * FROM account_battle_pass_mission_progress ORDER BY account,season_key,category,period_key,mission_id"),
            fixture.Query("SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key")
        });
    }
}
