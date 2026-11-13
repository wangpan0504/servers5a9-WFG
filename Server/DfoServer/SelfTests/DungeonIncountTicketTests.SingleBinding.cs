using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonIncountTicketTests
{
    static void RunSingleBindingAdmission(TcpTestResources resources, string root)
    {
        string path = Path.Combine(root, "ticket-single-binding.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        var authentication = new AuthenticationService(new AuthenticationRepository(path));
        authentication.Create("ticket-single-bind", "fixture-password");
        authentication.Create("ticket-other-bind", "fixture-password");
        using var owner = owners.Acquire("ticket-single-bind", () => { });
        var counts = DungeonIncountCatalog.Load(resources.Archive);
        That(counts.SingleGroups.Select(group => (group.Id, group.CharacterLimit, group.ResetDay))
            .SequenceEqual([(1, 4, 4), (2, 2, 2), (3, 2, 6), (4, 2, 4)]), "四组绑定按真实资源保留独立人数与周期");
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(DateTimeOffset.Parse("2026-11-05T05:59:59+08:00") - clock.GetUtcNow());
        var group = counts.SingleGroups.Single(group => group.Id == 1);
        var admission = SingleDungeonAdmission.At(group, clock);
        var row = counts.Find(5349)!;
        var characters = Enumerable.Range(0, group.CharacterLimit + 1).Select(index =>
        {
            var candidate = CharacterCreation.Prepare("Bind" + index, resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, null);
            return repository.Create(owner, candidate with { Level = 95, Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), new(10158964, ItemKind.Consumable, 2))) } });
        }).ToArray();
        var charge = Charge(characters[0]);
        string before = ClientSettingsSnapshots.Capture(path, true);
        Sql("CREATE TRIGGER binding_entry_abort BEFORE UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'binding entry rollback'); END");
        Reject<SqliteException>(() => repository.CommitDungeonEntry(owner, characters[0], 1, charge), "绑定后尝试号末段失败");
        Sql("DROP TRIGGER binding_entry_abort");
        That(ClientSettingsSnapshots.Capture(path, true) == before,
            "晚期失败回滚绑定、材料、日周次数、revision与尝试号");
        for (int index = 0; index < group.CharacterLimit; index++)
            characters[index] = repository.CommitDungeonEntry(owner, characters[index], 1, Charge(characters[index])).Character;
        repository = new CharacterRepository(path);
        before = ClientSettingsSnapshots.Capture(path, true);
        Reject<SingleDungeonAdmissionRejectedException>(() => repository.CommitDungeonEntry(owner, characters[^1], 1,
            Charge(characters[^1])), "重开库后第五角色仍被账号四角色上限拦截");
        That(ClientSettingsSnapshots.Capture(path, true) == before, "绑定名额不足不扣材料、次数或尝试号");
        characters[0] = repository.CommitDungeonEntry(owner, characters[0], 1, Charge(characters[0])).Character;
        That(Query("SELECT count(*) FROM account_single_dungeon_bindings") == 4,
            "已绑定角色重复入场幂等，不额外占名额");
        using (var other = owners.Acquire("ticket-other-bind", () => { }))
        {
            var candidate = CharacterCreation.Prepare("BindOther", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, null);
            var created = repository.Create(other, candidate);
            using var db = new SqliteConnection("Data Source=" + path); db.Open(); using var tx = db.BeginTransaction();
            CharacterRepository.BindSingleDungeonInTransaction(db, tx, other, created, admission); tx.Commit();
            That(Query("SELECT count(*) FROM account_single_dungeon_bindings WHERE account='ticket-other-bind'") == 1,
                "其他账号的同组绑定不被当前账号名额阻塞");
        }
        var prey = counts.SingleGroups.Single(target => target.Id == 3);
        var preyAdmission = SingleDungeonAdmission.At(prey, clock);
        foreach (var character in characters.Take(prey.CharacterLimit)) BindPrey(character, preyAdmission);
        repository.Delete(owner, repository.LoadDeletionTarget(owner, characters[1].Slot));
        Reject<SingleDungeonAdmissionRejectedException>(() => repository.CommitDungeonEntry(owner, characters[^1], 1,
            Charge(characters[^1])), "删角不返还引导本周四角色名额");
        Reject<SingleDungeonAdmissionRejectedException>(() => BindPrey(characters[^1], preyAdmission),
            "删角不返还普雷本周两角色名额");
        var oldCharge = Charge(characters[^1]);
        clock.Advance(TimeSpan.FromSeconds(1));
        That(SingleDungeonAdmission.At(prey, clock).Week == preyAdmission.Week,
            "普雷绑定不随周四挑战周期提前清空");
        Reject<InvalidOperationException>(() => repository.CommitDungeonEntry(owner, characters[^1], 1, oldCharge),
            "跨绑定周期旧入场计划失效");
        characters[^1] = repository.CommitDungeonEntry(owner, characters[^1], 1, Charge(characters[^1])).Character;
        That(Query("SELECT count(*) FROM account_single_dungeon_bindings WHERE account='ticket-single-bind' AND group_id=1") == 1,
            "周四06点新入场自然替换旧周期绑定");
        clock.Advance(TimeSpan.FromDays(2));
        That(SingleDungeonAdmission.At(prey, clock).Week != preyAdmission.Week, "普雷绑定周六06点独立切换");
        BindPrey(characters[^1], SingleDungeonAdmission.At(prey, clock));
        repository.Delete(owner, repository.LoadDeletionTarget(owner, characters[^1].Slot));
        That(Query("SELECT count(*) FROM account_single_dungeon_bindings WHERE account='ticket-single-bind'") == 2,
            "新表不阻塞删角，两个组均保留当前周期占位，新周期才恢复名额");

        void BindPrey(Character character, SingleDungeonAdmission target)
        {
            using var db = new SqliteConnection("Data Source=" + path); db.Open(); using var tx = db.BeginTransaction();
            CharacterRepository.BindSingleDungeonInTransaction(db, tx, owner, character, target); tx.Commit();
        }

        DungeonEntryCharge Charge(Character expected)
        {
            var spent = InventoryMaterialConsumptionService.Plan(expected.Inventory, [new ItemMaterialCost(10158964, 1)],
                resources.ItemResources.EpicPieces, clock.GetUtcNow());
            return new(spent.Plan!, 0, new(expected.Id, 0, expected.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1), 1)
            { DungeonId = 5349, SingleBinding = SingleDungeonAdmission.At(group, clock),
                Incount = new(row.CountKey, DungeonIncountPolicy.Period(clock.GetUtcNow()), row, [], true) };
        }
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        long Query(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!;
        }
    }
}
