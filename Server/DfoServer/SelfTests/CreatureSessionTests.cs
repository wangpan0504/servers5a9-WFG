using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureSessionTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-creature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("creature-session", () => { });
            var candidate = CharacterCreation.Prepare("PetClock", characters.Get(0), towns.Room, towns.InitialReturn, null);
            var equipped = CreatureRules.EquippedSlot;
            InventoryItem Pet(InventorySlot slot) => new(0, slot, new(7001, ItemKind.Creature, 0))
                { Creature = new() { Satiety = 10 } };
            repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(Pet(equipped)).Add(Pet(new(InventoryListType.Pet, 0)))
                .Add(new(0, new(InventoryListType.Pet, 189), new(24, ItemKind.CreatureConsumable, 1))) } });
            var clock = new ConsumableUseSessionTests.FakeClock();
            var service = new CreatureService(CreatureSystemTests.Synthetic(), clock);
            var session = new GameSession(repository, towns, owner);
            session.Select(0); session.MarkReady();
            var ticket = session.PrepareCreatureTick(service)!;
            clock.Advance(TimeSpan.FromSeconds(60));
            var ticked = session.SettleCreatureTick(ticket, service);
            That(ticked.Accepted && ticked.Receipt!.Character.Inventory.Find(equipped)!.Creature is { Satiety: 10, SatietyFraction: 6000 } &&
                session.CanPublishInventory(ticked.Receipt), "时钟按原角色及UID提交小数，不提前投影规划快照");
            var request = new ConsumableUseRequest(new(InventoryListType.Pet, 189), 0, 24, 0);
            var fed = session.FeedCreature(request, service);
            That(fed.Outcome == ConsumableUseOutcome.Committed && session.Selected!.Inventory.Find(request.Position) is null &&
                session.RecentCreatureFeed(request, service) && session.FeedCreature(request, service).Outcome == ConsumableUseOutcome.CloseConnection,
                "最后一份饲料消费后仍识别歧义重放，不再次扣除或盲发失败ACK");
            ticket = session.PrepareCreatureTick(service)!;
            session.PlanAndCommitInventory(character =>
            {
                var current = character.Inventory.Find(equipped)!;
                var other = character.Inventory.Find(new(InventoryListType.Pet, 0))!;
                return new(true, 0, new(character.Inventory, character.Inventory with { Items = character.Inventory.Items
                    .Replace(current, other with { Position = current.Position }).Replace(other, current with { Position = other.Position }) }, "test_creature_swap"));
            });
            var afterSwap = session.Selected!;
            clock.Advance(TimeSpan.FromMinutes(1));
            That(!session.SettleCreatureTick(ticket, service, force: true).Accepted && ReferenceEquals(afterSwap, session.Selected),
                "旧宠物时钟不能向换上的另一UID写入饱食余额");
            ticket = session.PrepareCreatureTick(service)!;
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            That(!session.SettleCreatureTick(ticket, service, force: true).Accepted && !session.CanPublishInventory(ticked.Receipt!),
                "同角色重选epoch使旧时钟和旧库存发布同时失效");
            var before = session.Selected!;
            Sql("CREATE TRIGGER fail_creature_clock AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='creature_satiety' " +
                "BEGIN SELECT RAISE(ABORT,'creature rollback'); END");
            ticket = session.PrepareCreatureTick(service)!; clock.Advance(TimeSpan.FromSeconds(60));
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => session.SettleCreatureTick(ticket, service, force: true), "饱食持久化末段审计失败");
            That(ReferenceEquals(before, session.Selected) &&
                repository.LoadCharacter(owner, 0).Inventory.Find(equipped) == before.Inventory.Find(equipped),
                "时钟SQL失败整笔回滚UID详情，不安装或提前发送变化");
            Sql("DROP TRIGGER fail_creature_clock");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => session.SettleCreatureTick(ticket, service, force: true), "旧generation不能提交宠物时钟");

            void Sql(string sql)
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
