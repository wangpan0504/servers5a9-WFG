using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class FiendwarRecoveryTests
{
    static void RunDepartureFailure(string root, TcpTestResources resources)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path, resources.Quests);
        using var owner = new AccountOwners().Acquire("fwdepartfail", () => { });
        var definition = new RaidStartSettings { StartMinimumOverride = 1 }.Apply(RaidCatalog.Load(resources.Archive)).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!;
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.UtcNow);
        Seed(repository, owner, resources, "FwDepartFail", definition.Resources!.Script.EntryCost!.ItemId, clock.GetUtcNow());
        var session = Session(repository, owner, resources);
        var registry = new RaidRegistry(clock); var parties = new PartyService(repository);
        session.CurrentParty = () => parties.Current(session);
        var actor = session.CaptureRaidActor(registry)!;
        var items = resources.ItemResources;
        var rewards = new FiendwarRewards(definition.Resources.Fiendwar!, items.Items, items.Boosters, definition.Resources.FiendwarGuide!.Fiendwar);
        var start = new AntonRaidStart(registry, parties, repository, resources.Towns, items.EpicPieces, clock, fiendwarRewards: rewards);
        try
        {
            That(registry.Create(actor, 2, "Departure", "Departure"u8.ToArray(), 8) is not null,
                "离线故障用例创建实际攻坚队");
            var started = start.Start(actor, registry.Find(actor)!, definition);
            That(started.Error == 0, $"离线故障用例沿原Start取得实际Battle和持久身份：{started.Reason}");
            long epoch = session.SelectionEpoch;
            Sql("CREATE TRIGGER fiendwar_departure_failure BEFORE UPDATE ON character_fiendwar_recovery BEGIN SELECT RAISE(ABORT,'fixture departure failure'); END");
            bool rejected = false;
            try { session.ReturnToRoster(); } catch (SqliteException) { rejected = true; }
            That(rejected && session.Selected is null && !session.Ready && session.Phase == SessionPhase.Unselected &&
                session.SelectionEpoch == epoch + 1 && registry.Find(actor) is null && session.StartedRaid is null,
                "离线持久SQL失败仍完整清Selected/epoch/目录/Battle，不留半失效角色且不伪造退出成功");
            Sql("DROP TRIGGER fiendwar_departure_failure");
        }
        finally { session.End(); }

        void Sql(string sql)
        { using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }
}
