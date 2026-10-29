using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class PvpTotalTeamTests
{
    public static void RunCore()
    {
        RunRenameCore();
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-total-team-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            using var owner = owners.Acquire("total-team", () => { });
            using var other = owners.Acquire("total-other", () => { });
            var point = new Location(10, 4, 10, 10);
            var map = new TownMap("total-team-fixture.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(10, 4)] = new(10, 4, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var service = new PvpTotalTeamService(repository, Catalog());
            var first = Create(owner, "TeamFirst", 1);
            var hole = Create(owner, "TeamHole", 2);
            var second = Create(owner, "TeamSecond", 2);
            var third = Create(owner, "TeamThird", 3);
            var extra = Create(owner, "TeamExtra", 1);
            repository.Delete(owner, repository.LoadDeletionTarget(owner, hole.Slot));
            var session = Session(owner);
            That(service.Read(owner) is null && service.Register(session, "队伍甲", [2, 0, 1]) is null,
                "总力空账本不自动建队，未选角不登记");
            Select(session);
            That(service.CheckName(owner, "队伍甲") == PvpTotalTeamFailure.None &&
                service.CheckName(other, "队伍甲") == PvpTotalTeamFailure.None && service.Read(owner) is null &&
                service.CheckName(owner, "") == PvpTotalTeamFailure.MissingName &&
                service.CheckName(owner, "bad!") == PvpTotalTeamFailure.InvalidName,
                "名称预检不占用名称，明确区分缺名和非法名");
            foreach (string invalid in new[] { "", "甲", " team", "Team!", "队伍\n甲", "abcdefghijklm", "😀甲" })
                That(service.Register(session, invalid, [2, 0, 1]) is null, "总力队名拒绝非法字符和边界：" + invalid);
            That(service.Register(session, "队伍甲", [2, 0, 0]) is null &&
                service.Register(session, "队伍甲", [2, 0, 1], true) is null &&
                service.Register(session, "队伍甲", [3, 0, 1]) is null,
                "重复角色、外部门禁及同基础职业重复不能登记");
            Sql("CREATE TRIGGER team_insert_abort AFTER INSERT ON account_pvp_total_teams BEGIN SELECT RAISE(ABORT,'team insert rollback'); END");
            Reject<SqliteException>(() => service.Register(session, "队伍甲", [2, 0, 1]), "总力新队伍晚期失败传播");
            That(service.Read(owner) is null, "新队伍失败不留下名称、角色或版本");
            Sql("DROP TRIGGER team_insert_abort");
            var original = service.Register(session, "队伍甲", [2, 0, 1])!;
            That(original is { Revision: 1, Complete: true } && original.Characters.SequenceEqual(new ushort?[] { third.Id, first.Id, second.Id }),
                "总力按发布索引保存稳定ID及请求顺序，有空槽时不误当持久槽");
            That(service.Register(session, "队伍甲", [2, 0, 1])!.SameLineup(original), "重复注册原阵容不增加版本");
            var initialization = service.CaptureInitialization(session)!;
            That(initialization.Team.SameLineup(original) &&
                initialization.Selection.Members.Select(row => row.RosterIndex).SequenceEqual(new byte[] { 2, 0, 1 }) &&
                session.CanPublishPvpTotalRoster(initialization.Selection),
                "完整编队初始化按实际发布名册恢复原顺序，有空槽时不把持久槽当索引");
            var publication = service.RegisterWithReceipt(session, "队伍甲", [2, 0, 1]);
            That(publication is { Failure: PvpTotalTeamFailure.None, Selection: not null } &&
                publication.Team!.SameLineup(original) && session.CanPublishPvpTotalRoster(publication.Selection!) &&
                publication.Selection!.Members.Select(row => row.RosterIndex).SequenceEqual(new byte[] { 2, 0, 1 }) &&
                service.CheckName(owner, "队伍甲") == PvpTotalTeamFailure.None &&
                service.CheckName(other, "队伍甲") == PvpTotalTeamFailure.NameTaken,
                "成功发布凭据保留原列表索引与原选角，已存在队名仍由真正注册原子占用");
            Create(other, "OtherFirst", 1); Create(other, "OtherSecond", 2); Create(other, "OtherThird", 3);
            var otherSession = Session(other); Select(otherSession);
            var arena = new PvpArenaService(memory, Catalog(), repository);
            var room = arena.Create(session, "总力门禁", PvpArenaMode.Individual, 0)!.Member.Room;
            try
            {
                var changed = service.RegisterWithReceipt(session, "队伍甲", [0, 1, 2]);
                That(changed is { Failure: PvpTotalTeamFailure.None, Team: not null } &&
                    changed.Team.Characters.SequenceEqual(new ushort?[] { first.Id, second.Id, third.Id }) &&
                    new CharacterRepository(path).ReadPvpTotalTeam(owner)!.SameLineup(changed.Team),
                    "普通等候房实际换代表立即持久化，无需小退或换频道触发保存");
                var waiting = service.RegisterWithReceipt(session, "队伍甲", [2, 0, 1]);
                That(waiting is { Failure: PvpTotalTeamFailure.None, Selection: not null } &&
                    waiting.Team!.Revision == original.Revision + 2 && waiting.Team.Characters.SequenceEqual(original.Characters) &&
                    session.CanPublishPvpTotalRoster(waiting.Selection) && service.Read(owner)!.SameLineup(waiting.Team) &&
                    !session.CanPublishPvpTotalRoster(publication.Selection!),
                    "普通等候房允许编辑代表阵容，回执绑定本房间而非旧城镇凭据");
                original = waiting.Team!;
                That(arena.Join(otherSession, room, false) is not null && arena.SetReady(otherSession, true).Length == 2 &&
                    arena.Start(session, DateTimeOffset.UtcNow).Length == 2 &&
                    service.RegisterWithReceipt(session, "队伍甲", [2, 0, 1]).Failure == PvpTotalTeamFailure.ArenaActive &&
                    !session.CanPublishPvpTotalRoster(waiting.Selection!) && service.Read(owner)!.SameLineup(original),
                    "开战后禁止改阵容，等候房旧回执也立即失效");
                arena.Leave(session);
                That(!session.CanPublishPvpTotalRoster(waiting.Selection!), "离房后不能重发原等候房编队回执");
                arena.Leave(otherSession);
            }
            finally { room.Commands.Release(); }
            That(new CharacterRepository(path).ReadPvpTotalTeam(owner)!.SameLineup(original), "重开库恢复同一队伍身份、名称与三角色");
            That(service.Register(session, "队伍乙", [2, 0, 1]) is null && service.Read(owner)!.SameLineup(original),
                "注册阵容不能绕过改名费用事务");

            That(service.Register(otherSession, "队伍甲", [0, 1, 2]) is null && service.Read(other) is null,
                "不同账号不能占用同一队名，冲突不留下半份编队");
            var otherTeam = service.Register(otherSession, "队伍乙", [0, 1, 2])!;
            That(otherTeam.Id != original.Id && otherTeam.Complete, "每账号独立队伍及不可复用身份");

            var selection = session.CapturePvpTotalRoster([1, 2, 0])!;
            var reordered = original with { Characters = [second.Id, third.Id, first.Id] };
            Sql("CREATE TRIGGER team_update_abort AFTER UPDATE ON account_pvp_total_teams BEGIN SELECT RAISE(ABORT,'team update rollback'); END");
            Reject<SqliteException>(() => service.Register(session, "队伍甲", [1, 2, 0]), "总力阵容更新末段失败传播");
            That(service.Read(owner)!.SameLineup(original), "更新失败保留原阵容顺序和版本");
            Sql("DROP TRIGGER team_update_abort");
            var updated = service.Register(session, "队伍甲", [1, 2, 0])!;
            That(updated.Id == original.Id && updated.Revision == original.Revision + 1 && updated.Characters.SequenceEqual(reordered.Characters),
                "同名阵容调整只推进队伍版本，保留队伍身份");
            Reject<InvalidOperationException>(() => repository.SavePvpTotalTeam(owner, original, reordered,
                selection.Members.Select(row => row.Character).ToImmutableArray()), "旧队伍版本不能覆盖新阵容");
            var observed = repository.ListRoster(owner).Where(row => updated.Characters.Contains(row.Id)).ToDictionary(row => row.Id);
            Reject<InvalidOperationException>(() => repository.SavePvpTotalTeam(other, updated, updated,
                updated.Characters.Select(id => observed[id!.Value]).ToImmutableArray()), "别的账号不能保存本队成员");

            Create(owner, "TeamFill", 1);
            repository.SwapSlots(owner, first.Slot, extra.Slot);
            That(service.CaptureInitialization(session) is null, "旧名册换槽后不发布错误编队索引");
            That(service.Read(owner)!.SameLineup(updated) && service.Register(session, "队伍甲", [1, 2, 0]) is null,
                "换槽保持已存稳定ID，旧发布索引不能再次登记");
            var refreshed = repository.ListRoster(owner).ToDictionary(row => row.Id);
            Sql("CREATE TRIGGER team_delete_abort AFTER DELETE ON characters BEGIN SELECT RAISE(ABORT,'team delete rollback'); END");
            Reject<SqliteException>(() => repository.Delete(owner, repository.LoadDeletionTarget(owner, refreshed[first.Id].Slot)),
                "删角末段失败传播且队伍置空同事务");
            That(service.Read(owner)!.SameLineup(updated), "删角失败回滚队伍阵容及版本");
            Sql("DROP TRIGGER team_delete_abort");
            repository.Delete(owner, repository.LoadDeletionTarget(owner, refreshed[first.Id].Slot));
            var incomplete = service.Read(owner)!;
            That(incomplete.Id == original.Id && incomplete.Name == original.Name && incomplete.Revision == updated.Revision + 1 &&
                !incomplete.Complete && incomplete.Characters.SequenceEqual(new ushort?[] { second.Id, third.Id, null }),
                "删角只清原稳定身份对应位置并使阵容不可用，队名和队伍身份保留");
            var replacementCharacter = Create(owner, "TeamNew", 1);
            That(service.CaptureInitialization(session) is null, "缺员及同槽替身不伪造完整初始化");
            That(replacementCharacter.Slot == refreshed[first.Id].Slot && replacementCharacter.Id != first.Id &&
                !service.Read(owner)!.Complete, "同槽新角色不继承总力成员资格");
            session.ReturnToRoster(); Select(session);
            That(!session.CanPublishPvpTotalRoster(initialization.Selection), "重新选角后旧初始化凭据失效");
            That(!session.CanPublishPvpTotalRoster(publication.Selection!), "换角后旧编队确认不能按新名册再次发送");
            var newRoster = repository.ListRoster(owner);
            byte Index(ushort id) => checked((byte)Array.FindIndex(newRoster.ToArray(), row => row.Id == id));
            var repaired = service.Register(session, "队伍甲", [Index(second.Id), Index(third.Id), Index(replacementCharacter.Id)])!;
            That(repaired.Id == original.Id && repaired.Revision == incomplete.Revision + 1 && repaired.Complete && repaired.Characters[2] == replacementCharacter.Id,
                "重新发布后显式补齐阵容，保留队伍及名称并推进新版本");
            var restored = service.CaptureInitialization(session)!;
            That(restored.Team.SameLineup(repaired) && restored.Selection.Members.Select(row => row.RosterIndex)
                .SequenceEqual(new byte[] { Index(second.Id), Index(third.Id), Index(replacementCharacter.Id) }),
                "新名册按稳定ID重新映射已存阵容，不沿用旧行号或自动登记");
            That(repository.LoadCharacter(owner, second.Slot).Revision == second.Revision &&
                repository.LoadCharacter(owner, third.Slot).Inventory.Gold == third.Inventory.Gold,
                "编队不改角色经验、资产或角色revision");
            using var takeover = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => service.Register(session, "队伍甲", [0, 1, 2]), "旧owner不能修改接管者总力队伍");
            That(service.Read(takeover)!.SameLineup(repaired), "接管者恢复原队伍配置，旧owner未写入");

            Character Create(AccountOwners.Lease lease, string name, byte grow) =>
                repository.Create(lease, new(name, point, point) { Level = 85, GrowType = grow });
            GameSession Session(AccountOwners.Lease lease) => new(repository, towns, lease, entry: new(new(1, 40), 24, "[pvp]"));
            void Select(GameSession current)
            {
                current.MarkRosterPublished(current.PrepareRoster());
                current.SelectFromRosterIndex(0); current.MarkReady();
            }
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} 总力稳定阵容、归属、版本、重开及删角事务断言");
    }

    static PvpArenaCatalog Catalog() => new(Doc(
        "[pvp enterable character]", 0, 1, 0, 2, 0, 3, "[/pvp enterable character]",
        "[pvp matching possible character]", 0, 1, 0, 2, 0, 3, "[/pvp matching possible character]",
        "[free pvp enterable character]", 0, 1, "[/free pvp enterable character]",
        "[tournament enterable character]", 0, 1, "[/tournament enterable character]"),
        Doc("[pvp map order]", 50000, "[/pvp map order]", "[pvp map basic data]", 50000, 1, 1, 1, 1, 1, 1, "[/pvp map basic data]"),
        Doc("[pvp normal battle time]", 180000, "[pvp normal battle time for tournament]", 240000,
            "[pvp relay battle time]", 120000, "[pvp relay battle time for tournament]", 180000));

    static PvfTokenDocument Doc(params object[] values)
    {
        var strings = new List<string>();
        var tokens = values.Select(value => value is int number ? new PvfToken(0, number) : Tag((string)value)).ToArray();
        return new(tokens, id => strings[id]);
        PvfToken Tag(string text) { strings.Add(text); return new(3, strings.Count - 1); }
    }
}
