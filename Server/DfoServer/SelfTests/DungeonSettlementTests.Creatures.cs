using DfoServer.Game.Accounts;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunCreatures(PvfArchive archive, ItemResources items)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pet-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("pet-clear", () => { });
            var candidate = Fixture("PetClear");
            var pet = new InventoryItem(0, CreatureRules.EquippedSlot, new(63032, ItemKind.Creature, 0)
                { Attr = 7, Marker16 = 88, Extension27 = 44 })
                { Creature = new() { Name = "进化", Experience = 314, Level = 19, Satiety = 75, ExpireTime = int.MaxValue } };
            var initial = repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(pet) } });
            var service = new CreatureService(items.Creatures);
            var growth = SelfTestCatalogs.Growth(archive);
            var (run, authority) = CreateRun(initial);
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var inventory = service.Experience(initial, 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Plan!;
            var prepared = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward, creatureInventory: inventory).Pending!;
            Sql("CREATE TRIGGER pet_clear_abort AFTER INSERT ON character_dungeon_settlement_receipts BEGIN SELECT RAISE(ABORT,'pet clear rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => repository.CommitDungeonSettlement(owner, initial, run, prepared, growth),
                "宠物进化写入后的结算凭据失败");
            That(QuestCompletionRepositoryTests.Same(initial, repository.LoadCharacter(owner, 0)),
                "宠物经验、模板、完整详情与角色经验在原结算事务一起回滚");
            Sql("DROP TRIGGER pet_clear_abort");
            var committed = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth);
            var result = committed.Character.Inventory.Find(CreatureRules.EquippedSlot)!;
            That(result.Uid == initial.Inventory.Find(CreatureRules.EquippedSlot)!.Uid && result.Core.ItemId == 63033 &&
                result.Core.Marker16 == 88 && result.Creature is { Experience: 315, Level: 20, Name: "进化", Satiety: 75, ExpireTime: int.MaxValue } &&
                committed.Character.Revision == initial.Revision + 1 && committed.Inventory is not null,
                "结算只推进一次revision，宠物达到20级原UID自动进化并保留扩展和期限");
            var duplicate = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth);
            That(duplicate.Duplicate && duplicate.Inventory is null &&
                QuestCompletionRepositoryTests.Same(committed.Character, duplicate.Character), "同结算回执恢复不再增加宠物经验或转换模板");

            void Sql(string sql)
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
