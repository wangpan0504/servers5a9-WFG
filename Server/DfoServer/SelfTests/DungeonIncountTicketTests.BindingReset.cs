using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonIncountTicketTests
{
    static void RunSingleBindingReset(TcpTestResources resources, string root)
    {
        string path = Path.Combine(root, "ticket-binding-reset.db");
        var repository = new CharacterRepository(path);
        var authentication = new AuthenticationService(new AuthenticationRepository(path));
        var owners = new AccountOwners();
        var counts = DungeonIncountCatalog.Load(resources.Archive);
        var clock = new ConsumableUseSessionTests.FakeClock();
        var service = new DungeonIncountTicketService(resources.ItemResources.Items, counts, resources.Towns, clock);
        var slot = new InventorySlot(InventoryListType.Main, 65);
        var spare = new InventorySlot(InventoryListType.Main, 66);
        string[] tables = ["account_single_dungeon_bindings", "account_fiendwar_guides", "account_evildom_war_guides"];
        foreach (var (id, groupId) in new[] { (10308863, 1), (10309169, 2), (10309601, 3), (10312212, 4) })
        {
            string account = "ticket-reset-" + groupId, other = account + "-other";
            authentication.Create(account, "fixture-password");
            authentication.Create(other, "fixture-password");
            using var owner = owners.Acquire(account, () => { });
            var seed = CharacterCreation.Prepare("ResetBind" + groupId, resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, null);
            var created = repository.Create(owner, seed with { Level = 95, Inventory = seed.Inventory with
            { Items = seed.Inventory.Items.Add(new(0, slot, new(id, ItemKind.Consumable, 1)))
                .Add(new(0, spare, new(id, ItemKind.Consumable, 1))) } });
            var sibling = repository.Create(owner, seed with { Name = "ResetAlt" + groupId });
            var session = new GameSession(repository, resources.Towns, owner, incounts: counts, timeProvider: clock);
            try
            {
                session.Select(created.Slot);
                session.MarkReady();
                string before = ClientSettingsSnapshots.Capture(path, true);
                That(session.UseDungeonIncountTicket(slot, service, repository, requestedAction: 222).Outcome == ConsumableUseOutcome.Rejected &&
                    ClientSettingsSnapshots.Capture(path, true) == before, "无当前绑定时拒绝且不浪费初始化券：" + id);

                // 全部组和另一个账号同时有绑定，验证只清本账号本组当前周期；历史世代和删角占位也保留。
                foreach (string target in new[] { account, other })
                {
                    Sql("INSERT INTO account_raid_quota_epochs(account,raid_type,entry_epoch,reward_epoch) VALUES($p0,2,7,9)", target);
                    foreach (var group in counts.SingleGroups)
                    {
                        int week = DungeonIncountPolicy.Period(clock.GetUtcNow(), group.ResetDay).Week;
                        for (int index = 0; index < 2; index++)
                        {
                            int character = target == account ? index == 0 ? created.Id : sibling.Id : 65530 + index;
                            if (group.Id is 1 or 3)
                                Sql("INSERT INTO account_single_dungeon_bindings VALUES($p0,$p1,$p2,$p3)", target, group.Id, week, character);
                            else if (group.Id == 2)
                                Sql("INSERT INTO account_fiendwar_guides(account,slot,week,entry_epoch,character_id) VALUES($p0,$p1,$p2,7,$p3)", target, index, week, character);
                            else Sql("INSERT INTO account_evildom_war_guides VALUES($p0,$p1,$p2,$p3)", target, index, week, character);
                        }
                    }
                }
                var selectedGroup = counts.SingleGroups.Single(group => group.Id == groupId);
                int selectedWeek = DungeonIncountPolicy.Period(clock.GetUtcNow(), selectedGroup.ResetDay).Week;
                string table = groupId is 1 or 3 ? tables[0] : groupId == 2 ? tables[1] : tables[2];
                string predicate = groupId is 1 or 3 ? $"group_id={groupId} AND period={selectedWeek}"
                    : groupId == 2 ? $"week={selectedWeek} AND entry_epoch=7" : $"week={selectedWeek}";
                if (groupId is 1 or 3)
                    Sql("INSERT INTO account_single_dungeon_bindings VALUES($p0,$p1,$p2,65533)", account, groupId, 20000101);
                if (groupId == 2)
                    Sql("INSERT INTO account_fiendwar_guides(account,slot,week,entry_epoch,character_id) VALUES($p0,0,$p1,6,65533)", account, selectedWeek);
                string unaffected = BindingSnapshot();
                Sql($"CREATE TRIGGER binding_ticket_abort BEFORE DELETE ON {table} WHEN OLD.account='{account}' AND OLD.character_id={sibling.Id} BEGIN SELECT RAISE(ABORT,'binding reset rollback'); END");
                before = ClientSettingsSnapshots.Capture(path, true);
                Reject<SqliteException>(() => repository.CommitDungeonIncountTicket(owner, service.Prepare(session.Selected!, slot, 222),
                    clock.GetUtcNow()), "解绑中途SQL失败整笔回滚：" + id);
                That(ClientSettingsSnapshots.Capture(path, true) == before, "解绑失败保留全部绑定、券、冷却、日资格与revision：" + id);
                Sql("DROP TRIGGER binding_ticket_abort");
                That(session.UseDungeonIncountTicket(slot, service, repository, requestedAction: 222).Outcome == ConsumableUseOutcome.Committed &&
                    session.Selected!.Inventory.Find(slot) is null && session.Selected.Inventory.Find(spare)?.Core.ItemId == id,
                    "整组初始化只消费一张，另一张保持：" + id);
                using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
                {
                    db.Open(); using var query = db.CreateCommand();
                    query.CommandText = $"SELECT count(*) FROM {table} WHERE account=$account AND {predicate}";
                    query.Parameters.AddWithValue("$account", account);
                    That(Convert.ToInt32(query.ExecuteScalar()) == 0 && BindingSnapshot() == unaffected,
                        "只清同账号本组两名角色的当前周期绑定，其他账号、组和历史不变：" + id);
                }
                // 绕过成功使用后的冷却，确保重试真正验证空组拒绝，而非被冷却提前拦下。
                clock.Advance(TimeSpan.FromSeconds(11));
                before = ClientSettingsSnapshots.Capture(path, true);
                That(session.UseDungeonIncountTicket(spare, service, repository, requestedAction: 222).Outcome == ConsumableUseOutcome.Rejected &&
                    ClientSettingsSnapshots.Capture(path, true) == before, "整组已空时重试不扣第二张、不残留提交门闩：" + id);
                repository = new CharacterRepository(path);
                That(repository.LoadCharacter(owner, created.Slot).Inventory.Find(slot) is null &&
                    repository.LoadCharacter(owner, created.Slot).Inventory.Find(spare)?.Core.ItemId == id && BindingSnapshot() == unaffected,
                    "重开库保持实际扣券、整组解绑与无关绑定：" + id);

                string BindingSnapshot()
                {
                    using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                    var rows = new List<object[]>();
                    foreach (string name in tables)
                    {
                        using var query = db.CreateCommand();
                        query.CommandText = $"SELECT * FROM {name}" + (name == table ? $" WHERE NOT(account=$account AND {predicate})" : "") + " ORDER BY rowid";
                        if (name == table) query.Parameters.AddWithValue("$account", account);
                        using var reader = query.ExecuteReader();
                        while (reader.Read())
                        {
                            var values = new object[reader.FieldCount + 1];
                            values[0] = name;
                            for (int index = 0; index < reader.FieldCount; index++)
                                values[index + 1] = reader.GetValue(index);
                            rows.Add(values);
                        }
                    }
                    return JsonSerializer.Serialize(rows);
                }
            }
            finally { session.End(); }
        }

        void Sql(string sql, params object[] values)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            for (int index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$p" + index, values[index]);
            command.ExecuteNonQuery();
        }
    }
}
