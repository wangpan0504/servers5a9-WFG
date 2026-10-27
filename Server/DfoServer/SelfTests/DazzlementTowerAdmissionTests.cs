using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DazzlementTowerAdmissionTests
{
    internal static void RunCore()
    {
        int before = Count;
        var boundary = DateTimeOffset.Parse("2026-10-29T06:00:00+08:00");
        var previous = DazzlementTowerAdmission.At(boundary.AddTicks(-1));
        var next = DazzlementTowerAdmission.At(boundary);
        That(previous.Week == 20261022 && next.Week == 20261029 && next.Day == previous.Day + 1,
            "眩惑周四06:00同时切换日资格和周绑定，不用UTC零点");
        var ordinaryDay = DazzlementTowerAdmission.At(boundary.AddDays(-1));
        That(ordinaryDay.Week == previous.Week && ordinaryDay.Day == previous.Day,
            "周三06:00切日不换周");
        previous = DazzlementTowerAdmission.At(boundary.AddDays(-3));
        Bytes(DfoServer.Network.Builders.DazzlementTowerPackets.State(0), new byte[11], "1605 type0未入场完整11B正文");
        Bytes(DfoServer.Network.Builders.DazzlementTowerPackets.State(1), [0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0],
            "1605已用资格只写offset6，不把日用量写到层进度或领奖标志");
        Reject<InvalidDataException>(() => DfoServer.Network.Builders.DazzlementTowerPackets.State(2), "每日上限1不截断非法计数");
        Bytes(DfoServer.Network.Builders.DazzlementTowerPackets.Bindings(new(previous,
                [new(1, 1, previous.Day, "眩惑", 0)])),
            [1, 4, 0, 0, 0, 0xD1, 0xA3, 0xBB, 0xF3, 0],
            "1608按CP936名称字节长度和职业构包，不把角色ID当名称写入");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-dazzlement-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var repo = new CharacterRepository(path);
            new AuthenticationService(new AuthenticationRepository(path)).Create("dazzlement-a", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("dazzlement-a", () => { });
            var characters = Enumerable.Range(0, 6).Select(i => repo.Create(owner,
                new NewCharacter("Dazzle" + i, new(38, 5, 90, 355), null)
                { DungeonState = CharacterDungeonState.NewOrdinary })).ToArray();
            var character = characters[0];
            DungeonEntryCharge Charge(Character c, DazzlementTowerAdmission period) =>
                new(null, 0, new(c.Id, 0, c.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1), 450001)
                { DungeonId = 7601, Dazzlement = period };
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            Sql("CREATE TRIGGER fail_dazzlement_entry BEFORE UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'dazzlement-entry-failure'); END");
            Reject<SqliteException>(() => repo.CommitDungeonEntry(owner, character, 500, Charge(character, previous)),
                "后续尝试号预留失败使眩惑准入事务回滚");
            That(repo.LoadDazzlementTower(owner).Bindings.IsEmpty && repo.LoadCharacter(owner, character.Slot).Revision == character.Revision,
                "入场失败不留绑定、不消耗当日资格且不推进角色revision");
            Sql("DROP TRIGGER fail_dazzlement_entry");
            character = repo.CommitDungeonEntry(owner, character, 500, Charge(character, previous)).Character;
            var saved = repo.LoadDazzlementTower(owner);
            That(saved.UsedToday(character.Id, previous) == 1 && saved.UsedToday(characters[1].Id, previous) == 0 &&
                saved.UsedToday(character.Id, previous with { Day = previous.Day + 1 }) == 0 && saved.UsedToday(character.Id, next) == 0,
                "1605日状态按角色及日周账本恢复，日切周切不造新计数");
            That(saved.Week == previous.Week && saved.Bindings is [{ Slot: 1, CharacterId: { } id }] && id == character.Id &&
                character.DungeonState!.LastAttempt == 1 && saved.Rejection(character.Id, previous) == "dazzlement-daily-entry-exhausted",
                "首次绑定、日资格与新尝试号同事务提交，退出或通关均不返还当日入场");
            Reject<InvalidOperationException>(() => repo.CommitDungeonEntry(owner, character, 501, Charge(character, previous)),
                "重复入场即使绕过Session仍由事务内准入拒绝");
            That(repo.LoadCharacter(owner, character.Slot).DungeonState!.LastAttempt == 1,
                "拒绝重入不预留第二个尝试号");
            for (int i = 1; i < 5; i++)
                characters[i] = repo.CommitDungeonEntry(owner, characters[i], 500, Charge(characters[i], previous)).Character;
            var full = repo.LoadDazzlementTower(owner);
            That(full.Bindings.Length == 5 && full.Rejection(characters[5].Id, previous) == "dazzlement-weekly-bindings-exhausted",
                "账号本周可绑定五个角色，第六个未绑定角色不能顶替名单");
            Reject<InvalidOperationException>(() => repo.CommitDungeonEntry(owner, characters[5], 500, Charge(characters[5], previous)),
                "第六个角色在实际写事务内仍拒绝");
            var later = previous with { Day = previous.Day + 1 };
            character = repo.CommitDungeonEntry(owner, character, 501, Charge(character, later)).Character;
            That(repo.LoadDazzlementTower(owner).Bindings.Length == 5 && character.DungeonState!.LastAttempt == 2,
                "跨日已绑定角色复用原名额，只消耗新一天资格");
            // 重建仓储验证真实持久化；不以旧会话内存冒充重登恢复。
            repo = new CharacterRepository(path);
            That(repo.LoadDazzlementTower(owner).Rejection(character.Id, later) == "dazzlement-daily-entry-exhausted",
                "仓储重建仍恢复绑定和当日已用状态");
            Reject<InvalidOperationException>(() => repo.CommitDungeonEntry(owner, character, 502, Charge(character, previous)),
                "旧日计划不能覆盖新日资格");
            repo.Delete(owner, repo.LoadDeletionTarget(owner, characters[4].Slot));
            That(repo.LoadDazzlementTower(owner).Rejection(characters[5].Id, later) == "dazzlement-weekly-bindings-exhausted",
                "删角遗留空槽本周继续占名额，不能删建角色绕过周绑定");
            characters[5] = repo.CommitDungeonEntry(owner, characters[5], 500, Charge(characters[5], next)).Character;
            That(repo.LoadDazzlementTower(owner).Bindings is [{ Slot: 1, CharacterId: { } newId }] && newId == characters[5].Id,
                "周四边界回收旧名单后允许第六个角色新周绑定");
            var stale = Charge(character, later);
            Reject<InvalidOperationException>(() => repo.CommitDungeonEntry(owner, character, 502, stale),
                "旧周准备结果不能复活旧绑定或清理新周名单");
            Sql("DROP TABLE account_dazzlement_tower_bindings; PRAGMA user_version=106");
            repo = new CharacterRepository(path);
            That(repo.LoadDazzlementTower(owner).Bindings.IsEmpty && repo.LoadCharacter(owner, character.Slot).Revision == character.Revision,
                "v106升级建立空眩惑账本，保留旧角色资产与尝试号，不伪造历史绑定");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} 眩惑日周边界、账号绑定、事务回滚和持久化断言");
    }
}
