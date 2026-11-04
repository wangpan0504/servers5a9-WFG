using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AccountMaterialDeclarationTests
{
    const int Material = 490011853;
    const int Source = 490011854;
    const int Companion = 490011863;

    public static void Run(ItemResources resources, TownCatalog towns)
    {
        int checks = Count;
        foreach (int id in new[] { Material, 490011834, 490011993, 490012052, 490012480,
            490012652, 490012801, 490013085, 490013307, 490013308 })
        {
            var template = resources.Items.Get(id);
            That(template.Kind == ItemKind.Material && template.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" &&
                template.AccountMaterial is { CommonInventory: true } account && account.HoldingLimit == template.StackLimit,
                "真实尾随值共通材料可加载且保留自身持有上限 " + id);
        }
        That(resources.Items.Get(490011740).AccountMaterial is { CommonInventory: true },
            "原有无尾随值共通材料仍使用共通材料容器");
        That(resources.Items.Get(10308807).AccountMaterial is
            { CommonInventory: false, HoldingLimit: 4000, WeeklyAccountLimit: 4000, WeeklyCharacterLimit: 1300 },
            "普通账号材料保留原容器及两级攻坚额度");
        RunBooster(resources, towns);
        Console.WriteLine($"PASS account material declaration resources/persistence: {Count - checks} checks");
    }

    static void RunBooster(ItemResources resources, TownCatalog towns)
    {
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var companion = resources.Items.Get(Companion);
        That(companion.Kind == ItemKind.Consumable && companion.AccountMaterial is null && companion.StackLimit >= 2,
            "第二奖保持真实消耗品模板，未代开活动动作或转成账号材料");
        var source = InventoryRewardGrantService.Create(resources.Items.Get(Source), 2, now);
        var companionCore = InventoryRewardGrantService.Create(companion, companion.StackLimit - 1, now);
        var filler = InventoryRewardGrantService.Create(resources.Items.Get(1106), 1, now);
        var materialFiller = InventoryRewardGrantService.Create(resources.Items.Get(3167), 1, now);
        var inventory = InventorySnapshot.Empty with
        {
            Items = [new(0, Bag(65), source), new(0, Bag(66), companionCore),
                .. Enumerable.Range(67, 54).Select(slot => new InventoryItem(0, Bag((short)slot), filler)),
                .. Enumerable.Range(121, 56).Select(slot => new InventoryItem(0, Bag((short)slot), materialFiller))]
        };
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, clock: new DailyBoosterTestClock(now));
        string root = Path.Combine(Path.GetTempPath(), "s5a9-common-material-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            Character committed;
            using (var owner = owners.Acquire("common-material", () => { }))
            {
                var character = repository.Create(owner, new("CommonBox", towns.Room, towns.InitialReturn)
                    { Level = 95, Inventory = inventory });
                string before = JsonSerializer.Serialize(character.Inventory);
                var opened = service.Open(character, new(65, 1), out var receipt);
                That(opened.Accepted && opened.Plan is not null && receipt is not null &&
                    receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new BoosterGrantedItem[]
                        { new(Material, 1), new(Companion, 1) }),
                    "真实礼盒完整发放共通材料与活动道具各一件 " + opened.Error + "/" + opened.RejectReason);
                var after = opened.Plan!.After;
                var expectedSource = character.Inventory.Find(Bag(65))!;
                expectedSource = expectedSource with { Core = expectedSource.Core with { Value = 1 } };
                var expectedCompanion = character.Inventory.Find(Bag(66))!;
                expectedCompanion = expectedCompanion with { Core = expectedCompanion.Core with { Value = companion.StackLimit } };
                That(ReferenceEquals(opened.Plan.Before, character.Inventory) && JsonSerializer.Serialize(character.Inventory) == before &&
                    after.AccountWallet.Material(Material) is { Balance: 1, RaidEarned: 0 } &&
                    after.AccountWallet.Materials.Count == 1 && after.Items.Length == 112 &&
                    after.Items.All(item => item == (item.Position == Bag(65) ? expectedSource :
                        item.Position == Bag(66) ? expectedCompanion : character.Inventory.Find(item.Position))) &&
                    after.Items.All(item => item.Core.ItemId != Material) && opened.Plan.OverflowItems.IsEmpty,
                    "满消耗品页和材料页仍只扣一盒、第二奖堆至上限，共通材料入余额而不新增实体或溢出邮件");
                using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
                {
                    db.Open();
                    using var command = db.CreateCommand();
                    command.CommandText = "CREATE TRIGGER reject_common_material_audit BEFORE INSERT ON account_material_audit BEGIN SELECT RAISE(ABORT,'fixture'); END";
                    command.ExecuteNonQuery();
                    bool rolledBack = false;
                    try { InventoryService.Commit(repository, owner, character, opened); }
                    catch (SqliteException) { rolledBack = true; }
                    var reloaded = repository.LoadCharacter(owner, character.Slot);
                    That(rolledBack && reloaded.Revision == character.Revision &&
                        reloaded.Inventory.AccountWallet.Same(character.Inventory.AccountWallet) &&
                        reloaded.Inventory.Items.SequenceEqual(character.Inventory.Items),
                        "共通材料后段审计失败同时回滚源、两奖、账号余额及角色和账号版本");
                    command.CommandText = "DROP TRIGGER reject_common_material_audit";
                    command.ExecuteNonQuery();
                }
                committed = InventoryService.Commit(repository, owner, character, opened);
                That(committed.Inventory.AccountWallet.Material(Material) is { Balance: 1, RaidEarned: 0 } &&
                    committed.Inventory.AccountWallet.Revision == character.Inventory.AccountWallet.Revision + 1 &&
                    committed.Revision == character.Revision + 1 && committed.Inventory.Items.SequenceEqual(after.Items),
                    "移除审计故障后原计划整笔提交，源与两奖不重复扣发");
            }
            using (var reconnected = owners.Acquire("common-material", () => { }))
            {
                var reloaded = new CharacterRepository(path).LoadCharacter(reconnected, committed.Slot);
                That(reloaded.Revision == committed.Revision &&
                    reloaded.Inventory.AccountWallet.Same(committed.Inventory.AccountWallet) &&
                    reloaded.Inventory.Items.SequenceEqual(committed.Inventory.Items),
                    "重新获取账号owner并重建仓储后完整恢复源、实体奖励和共通材料余额");
            }
            using var other = owners.Acquire("other-common-material", () => { });
            That(repository.LoadAccountWallet(other).Materials.IsEmpty, "共通材料余额不串入其他账号");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
