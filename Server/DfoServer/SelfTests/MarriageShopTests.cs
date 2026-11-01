using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Marriage;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageShopTests
{
    public static void Run(PvfArchive pvf, ItemResources resources)
    {
        int checks = Count;
        var catalog = MarriageCatalog.Load(pvf);
        var service = new MarriageShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, catalog);
        int shopId = resources.Shops.Shops.Single(row => row.Value.NpcId == 20011 && row.Value.Items.Contains(10002797)).Key;
        var request = new BuyItemRequest(10002797, 1, shopId, 20011);
        var first = new Character(1, 0, "Buyer", 0, new(1, 0, 50, 50), null)
            { Level = 30, Inventory = new([], Gold: 1000) };
        var relation = Relation(first.Id, 2);
        int cost = resources.Items.Get(request.ItemId).ShopPrice.Materials.Single(row => row.ItemId == MarriageShopService.LoveMaterialId).Count;
        That(cost == 100 && service.Handles(request.ItemId), "真实马戈商店家具按心意点材料定价");
        var buy = service.Plan(first, relation, request, out var after, out var receipt);
        That(buy.Accepted && after!.Members[first.Id].LovePoints == 999 - cost && after.Storage[0] == new MarriageFurnitureStack(request.ItemId, 1) &&
            buy.Plan!.After.Items.Length == 0 && receipt is { Core.ItemId: 10002797, Position.Index: 0, Materials.Length: 0 },
            "canonical心意点扣款并直接入共享家具库，不生成主背包心意或家具实例");
        byte[] ack = ShopPackets.Bought(buy, receipt);
        That(ack.Length == 141 && ack[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(9)) == 0 &&
            BinaryPrimitives.ReadInt16LittleEndian(ack.AsSpan(17)) == 0 && BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(19)) == request.ItemId && ack[^1] == 0,
            "原生购买回包保持特殊点数字段及123字节核心，以共享家具槽索引呈现实际商品");
        var second = service.Plan(first, after!, request with { Count = 2 }, out var twice, out var secondReceipt);
        That(second.Accepted && twice!.Storage[1].Count == 2 && twice.Members[first.Id].LovePoints == 999 - 3 * cost &&
            secondReceipt!.Position.Index == 1 && BinaryPrimitives.ReadInt16LittleEndian(ShopPackets.Bought(second, secondReceipt).AsSpan(17)) == 1,
            "连续批量购买占用下一共享槽且按商品数量扣费");
        var full = relation with { Storage = Enumerable.Range(0, MarriageCoupleRules.StorageSlots)
            .ToImmutableDictionary(index => (ushort)index, _ => new MarriageFurnitureStack(request.ItemId, 1)) };
        foreach (var invalid in new[] { relation with { State = 1 }, relation with { State = 0 },
            relation with { Members = relation.Members.SetItem(first.Id, new() { LovePoints = cost - 1 }) }, full })
            That(!service.Plan(first, invalid, request, out var rejectedAfter, out var rejectedReceipt).Accepted &&
                rejectedAfter is null && rejectedReceipt is null, "未婚、结束、心意不足和满仓都不产生扣款或商品计划");
        foreach (var invalid in new[] { request with { Count = 0 }, request with { Count = -1 }, request with { NpcId = 1 },
            request with { ShopId = int.MaxValue } })
            That(!service.Plan(first, relation, invalid, out _, out _).Accepted, "商店身份与非法数量在提交前拒绝");
        int ordinary = resources.Shops.Shops.Values.SelectMany(shop => shop.Items).First(id => !service.Handles(id));
        That(!service.Handles(ordinary), "普通商店商品仍走原库存购买路径");
        Persistence();
        Console.WriteLine($"PASS {Count - checks} marriage shop resource and transaction assertions");

        void Persistence()
        {
            string directory = Path.Combine(Path.GetTempPath(), "s5a9-marriage-shop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "game.db");
            var owners = new AccountOwners();
            using var actor = owners.Acquire("marriage-shop-actor", () => { });
            using var partner = owners.Acquire("marriage-shop-partner", () => { });
            try
            {
                var auth = new AuthenticationService(new AuthenticationRepository(path));
                auth.Create(actor.Account, "fixture-password"); auth.Create(partner.Account, "fixture-password");
                var repository = new CharacterRepository(path);
                var a = repository.Create(actor, new("ShopBride", new(1, 0, 50, 50), null) { Job = 1, Level = 30, Inventory = new([], Gold: 1000) });
                var b = repository.Create(partner, new("ShopGroom", new(1, 0, 50, 50), null) { Job = 0, Level = 30, Inventory = new([], Gold: 1000) });
                var created = repository.CommitMarriageTogether(actor, partner, a, b,
                    new(a.Inventory, a.Inventory, "shop_fixture"), new(b.Inventory, b.Inventory, "shop_fixture"), null, Relation(a.Id, b.Id));
                a = created.First;
                var before = created.Record;
                var planned = service.Plan(a, before, request, out var next, out _);
                Sql("CREATE TRIGGER fail_marriage_shop AFTER UPDATE ON marriage_records BEGIN SELECT RAISE(ABORT,'shop rollback'); END");
                bool failed = false;
                try { repository.CommitMarriageSingle(actor, a, planned.Plan!, before, next!); }
                catch (SqliteException) { failed = true; }
                That(failed && repository.FindMarriage(a.Id)!.Members[a.Id].LovePoints == 999 &&
                    repository.FindMarriage(a.Id)!.Storage.Count == 0 && repository.LoadCharacter(actor, a.Slot).Inventory.Gold == 1000,
                    "关系末段失败回滚心意余额、共享家具与库存费用");
                Sql("DROP TRIGGER fail_marriage_shop");
                var saved = repository.CommitMarriageSingle(actor, a, planned.Plan!, before, next!);
                var reopened = new CharacterRepository(path);
                That(reopened.FindMarriage(b.Id)!.Storage[0].ItemId == request.ItemId &&
                    reopened.FindMarriage(a.Id)!.Members[a.Id].LovePoints == 999 - cost &&
                    reopened.LoadCharacter(partner, b.Slot).Revision == created.Second!.Revision &&
                    reopened.LoadCharacter(actor, a.Slot).Inventory.Items.Length == 0,
                    "重开库后双方读取同一共享商品和余额，离线配偶资产版本不变");
                failed = false;
                try { repository.CommitMarriageSingle(actor, saved.First, planned.Plan!, before, next!); }
                catch (InvalidOperationException) { failed = true; }
                That(failed && reopened.FindMarriage(a.Id)!.Storage.Count == 1, "旧关系购买计划重放不重复扣心意或发家具");
                void Sql(string sql)
                {
                    using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                    using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
                }
            }
            finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(directory, true); }
        }
    }

    static MarriageRecord Relation(ushort first, ushort second) => new(Guid.NewGuid(), 1, 1, first, second, 4)
    {
        CreatedAt = 1000, MarriedAt = 1000,
        Members = ImmutableDictionary<ushort, MarriageMemberState>.Empty.Add(first, new() { LovePoints = 999 }).Add(second, new())
    };
}
