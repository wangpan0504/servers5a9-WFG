using System.Collections.Concurrent;
using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.Testing.Inventory;

// 全量道具兼容扫描：equipment.lst / stackable.lst 中每个道具都必须能被 ItemCatalog.Get 解析。
internal static class PvfItemCompatTests
{
    public static void Run(PvfArchive pvf, ItemResources resources)
    {
        That(resources.Items.Get(2683657).Id == 2683657, "分块 impossible contents 的道具可加载");
        That(resources.Items.Get(10310207).Jobs == ushort.MaxValue, "[account] 可用职业按全职业读取");
        That(resources.Items.Get(100400000).EquipmentSlot == 34, "护石按独立槽位 34 加载");
        That(resources.Items.Get(100360001).EquipmentSlot == 35, "符咒按独立槽位 35 加载");
        That(resources.Items.Get(0).Id == 0, "编号 0 的金币占位可以加载");
        That(resources.Items.Get(490008569).QualityBox is { MinimumPercent: 100, MaximumPercent: 100 }, "黄金品级箱取最高品级");
        That(resources.Items.Get(10004946).ShopPrice.Limit is { AccountWide: true, Count: 2 }, "trade 限购按账号上限读取");
        That(resources.Items.Get(10094768) is { Kind: GameWorld.Items.ItemKind.Consumable, AccountBoundAfterTrade: true, AccountCargoForbidden: false,
            AttachType.Length: 0, StackableType: "[enchant waste] 0" }, "现场宝珠10094768的限次交易后账号绑定声明进入真实物品模板");
        That(resources.Items.Get(10094771) is { Kind: GameWorld.Items.ItemKind.Profession, AccountBoundAfterTrade: true, AttachType.Length: 0 },
            "同类附魔卡片10094771也按资源声明获得账号仓库资格，不按宝珠类型或编号特判");
        That(resources.Items.Get(10002395) is { Kind: GameWorld.Items.ItemKind.Consumable, AccountCargoForbidden: false, AccountBoundAfterTrade: false } capsule &&
            capsule.AttachType.Contains("[trade limit]", StringComparison.Ordinal), "真实宠物胶囊按限次交易模板读取，不伪装成无条件账号绑定");
        foreach (var (id, kind, materialId, cost, declaration) in new[]
        {
            (10161087, ItemKind.Consumable, 10160847, 20, "`character` `agitwarseason` 5"),
            (10161088, ItemKind.Consumable, 10160847, 20, "`character` `agitwarseason` 5"),
            (490009846, ItemKind.Consumable, 10160887, 10, ""),
            (490009852, ItemKind.Material, 490009179, 5, "")
        })
        {
            var item = resources.Items.Get(id);
            That(item.Id == id && item.Kind == kind && item.ShopPrice.Materials is [{ ItemId: var actualMaterial, Count: var actualCost }] &&
                actualMaterial == materialId && actualCost == cost && item.ShopPrice.PeriodicLimit is null &&
                item.ShopPrice.UnresolvedPurchaseLimits.Length == 1 && item.ShopPrice.UnresolvedPurchaseLimits.Single().Trim() == declaration,
                "真实四种模板可完整加载并保留类型、材料价和未解释限购，不冒已支持赛季购买");
            var snapshot = InventorySnapshot.Empty;
            That(!ShopPurchaseLimitService.TryPlan(snapshot, item.ShopPrice, 1, id, 1, DateTimeOffset.UtcNow,
                out var after, out var deadline) && ReferenceEquals(after, snapshot) && deadline is null,
                "真实空或赛季限购商品购买拒绝且原库存及计数不变");
        }
        var rows = PvfScript.List(pvf.ReadText("equipment/equipment.lst")).Concat(PvfScript.List(pvf.ReadText("stackable/stackable.lst"))).ToArray();
        var unexpected = new ConcurrentBag<string>();
        Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) }, row =>
        {
            try { resources.Items.Get(row.Key); }
            catch (Exception error) { unexpected.Add(row.Key + ": " + error.GetType().Name + " " + error.Message); }
        });
        That(unexpected.IsEmpty, $"全量道具解析失败 {unexpected.Count}: " + string.Join(" | ", unexpected.OrderBy(x => x).Take(10)));
    }
}

