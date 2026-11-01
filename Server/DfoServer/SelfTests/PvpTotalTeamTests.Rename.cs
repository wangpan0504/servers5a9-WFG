using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class PvpTotalTeamTests
{
    static void RunRenameCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-total-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            using var owner = owners.Acquire("rename-team", () => { });
            using var other = owners.Acquire("rename-other", () => { });
            var clock = new ConsumableUseSessionTests.FakeClock();
            var point = new Location(10, 4, 10, 10);
            var map = new TownMap("rename.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(10, 4)] = new(10, 4, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var card = new ItemTemplate(PvpTotalTeamRename.CardId, ItemKind.Material, 0, 1, 0, 100, 0, 2, 1,
                "[trade]", ushort.MaxValue, 0, "更名券", "[material]");
            var teams = new PvpTotalTeamService(repository, Catalog(), card, clock);
            Character Create(AccountOwners.Lease lease, string name, byte grow) => repository.Create(lease,
                new(name, point, point) { Level = 85, GrowType = grow, Inventory = new([
                    new(0, new(InventoryListType.Main, 121), new(PvpTotalTeamRename.CardId, ItemKind.Material, 3) { SortLockFlag = 1 }),
                    new(0, new(InventoryListType.Main, 122), new(PvpTotalTeamRename.CardId, ItemKind.Material, 2) { EquipmentLockId = 1 }),
                    new(0, new(InventoryListType.Main, 123), new(PvpTotalTeamRename.CardId, ItemKind.Material, 1)
                        { ExpireTime = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 2) })]) });
            var first = Create(owner, "RenameFirst", 1);
            Create(owner, "RenameSecond", 2); Create(owner, "RenameThird", 3);
            Create(other, "RenameOtherA", 1); Create(other, "RenameOtherB", 2); Create(other, "RenameOtherC", 3);
            GameSession Session(AccountOwners.Lease lease)
            {
                var value = new GameSession(repository, towns, lease, entry: new(new(1, 47), 8, "[pvp]"));
                value.MarkRosterPublished(value.PrepareRoster()); value.SelectFromRosterIndex(0); value.MarkReady(); return value;
            }
            var session = Session(owner);
            var otherSession = Session(other);
            That(session.RenamePvpTotalTeam(teams, "新队名", 121).Failure == PvpTotalTeamRenameFailure.MissingTeam,
                "没有原队伍不能凭更名券自动建队");
            var original = teams.Register(session, "原队名", [0, 1, 2])!;
            teams.Register(otherSession, "占用名称", [0, 1, 2]);
            foreach (ushort slot in new ushort[] { 122, 124, ushort.MaxValue })
                That(session.RenamePvpTotalTeam(teams, "新队名", slot).Failure == PvpTotalTeamRenameFailure.InvalidCard,
                    "锁定、空格和查找失败索引不得替换成其他可用券");
            That(session.RenamePvpTotalTeam(teams, "新队名", 121, true).Failure == PvpTotalTeamRenameFailure.Unavailable &&
                session.RenamePvpTotalTeam(teams, "bad!", 121).Failure == PvpTotalTeamRenameFailure.InvalidName,
                "外部活动门禁和非法名称零消费");
            That(session.RenamePvpTotalTeam(teams, "占用名称", 121).Failure == PvpTotalTeamRenameFailure.NameTaken &&
                session.Selected!.Inventory.Find(new(InventoryListType.Main, 121))!.Core.Count == 3 &&
                teams.Read(owner)!.SameLineup(original), "真正更名事务再次查重且不扣券");
            Sql("CREATE TRIGGER rename_abort AFTER UPDATE ON account_pvp_total_teams BEGIN SELECT RAISE(ABORT,'rename rollback'); END");
            Reject<SqliteException>(() => session.RenamePvpTotalTeam(teams, "新队名", 121), "队名写入末段失败传播");
            That(teams.Read(owner)!.SameLineup(original) &&
                repository.LoadCharacter(owner, first.Slot).Inventory.Find(new(InventoryListType.Main, 121))!.Core.Count == 3 &&
                session.Selected!.Revision == first.Revision, "改名失败回滚库存与队名，在线快照不提前安装");
            Sql("DROP TRIGGER rename_abort");
            var renamed = session.RenamePvpTotalTeam(teams, "新队名", 121);
            That(renamed is { Failure: PvpTotalTeamRenameFailure.None, Team.Revision: 2, Inventory.Plan.Changed: true } &&
                renamed.Team.Id == original.Id && renamed.Team.Characters.SequenceEqual(original.Characters) &&
                session.Selected!.Inventory.Find(new(InventoryListType.Main, 121)) is { Core.Count: 2, Core.SortLockFlag: 1 } &&
                session.CanPublishPvpTotalTeamRename(renamed.Actor!), "指定排序锁槽允许消费一张，保留UID、队伍和阵容");
            var replay = session.RenamePvpTotalTeam(teams, "新队名", ushort.MaxValue);
            That(replay.Failure == PvpTotalTeamRenameFailure.None && replay.Inventory is null &&
                replay.Team!.SameLineup(renamed.Team!) && session.Selected!.Inventory.Find(new(InventoryListType.Main, 121))!.Core.Count == 2,
                "已提交同名重放不扣第二张、不推进版本");
            var stale = teams.PrepareRename(session.Selected!, renamed.Team!, "迟到名称", 121)!;
            var another = session.RenamePvpTotalTeam(teams, "再次改名", 121);
            Reject<InvalidOperationException>(() => repository.RenamePvpTotalTeam(owner, stale), "旧队伍版本不能改名或扣原快照");
            That(!session.CanPublishPvpTotalTeamRename(renamed.Actor!) && another.Team!.Revision == 3,
                "后续库存变更使旧成功投影失效");
            var expiring = teams.PrepareRename(session.Selected!, another.Team!, "到期名称", 123)!;
            clock.Advance(TimeSpan.FromSeconds(2));
            Reject<InventoryQualificationExpiredException>(() => repository.RenamePvpTotalTeam(owner, expiring),
                "规划后提交前恰到期拒绝，队名与库存全部回滚");
            That(session.RenamePvpTotalTeam(teams, "到期名称", 123).Failure == PvpTotalTeamRenameFailure.InvalidCard &&
                teams.Read(owner)!.SameLineup(another.Team!), "到期源不消费且不改名");
            var depleted = session.RenamePvpTotalTeam(teams, "最终名称", 121);
            That(depleted.Failure == PvpTotalTeamRenameFailure.None &&
                session.Selected!.Inventory.Find(new(InventoryListType.Main, 121)) is null &&
                new CharacterRepository(path).ReadPvpTotalTeam(owner)!.SameLineup(depleted.Team!),
                "最后一张完整删除，重开仓储恢复同一名称、身份和阵容");
            using var takeover = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => session.RenamePvpTotalTeam(teams, "接管改名", 123), "旧owner不能继续更名");
            That(teams.Read(takeover)!.SameLineup(depleted.Team!), "接管不回滚成功更名或重复消费");

            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 总力更名扣券、回滚、重放、版本与期限断言");
    }
}
