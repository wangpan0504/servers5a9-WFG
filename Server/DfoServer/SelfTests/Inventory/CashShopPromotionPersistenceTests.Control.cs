using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionPersistenceTests
{
    public static void RunControlCore()
    {
        int before = Count;
        using var f = new Fixture();
        string root = Path.Combine(Path.GetTempPath(), "promotion-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string json = JsonSerializer.Serialize(new[] { new PromotionConfiguration(f.Rules, true) });
            var shop = CashShopCatalog.Parse("[item]\n1000 51000 1 0 0 100 0 0 0\n");
            string? consumerError = null;
            CashShopPromotionCatalog Parse(string text) => CashShopPromotionCatalog.Parse(text, shop,
                id => new(id, ItemKind.Consumable, 0, 1, 0, 1, 0, 0, 0, "[free]", ushort.MaxValue, 0, "合成外盒"), _ => consumerError);
            string path = Path.Combine(root, "promotions.json");
            var config = new CashShopPromotionConfiguration(path, json, Parse);
            int changes = 0;
            config.Changed += () => changes++;
            var initial = config.Current;
            var off = config.SetEnabled(f.Rules.Scope, initial.Revision, false);
            That(off.Activities[0].Availability == PromotionAvailability.Disabled && changes == 1 &&
                Parse(File.ReadAllText(path)).Activities[0].Availability == PromotionAvailability.Disabled,
                "独立开关原子持久关闭并通知，规则身份保持");
            Reject<InvalidOperationException>(() => config.SetEnabled(f.Rules.Scope, initial.Revision, true), "旧页面版本不能重开礼包");
            Reject<InvalidOperationException>(() => config.SetEnabled(f.Rules.Scope with { Period = "other" }, off.Revision, true), "完整活动身份必须匹配");
            consumerError = "消费者拒绝";
            Reject<InvalidOperationException>(() => config.SetEnabled(f.Rules.Scope, off.Revision, true), "开启必须重新通过实际消费者校验");
            That(ReferenceEquals(config.Current, off) && changes == 1, "校验失败保持旧运行快照且不通知成功");
            consumerError = null;
            File.WriteAllText(path, json);
            Reject<InvalidOperationException>(() => config.SetEnabled(f.Rules.Scope, off.Revision, true), "外部配置变化不能被旧页面覆盖");
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { new PromotionConfiguration(f.Rules, false) }));
            // 恢复与配置实际序列化一致的来源，独立验证最终文件替换失败。
            string blockedPath = Path.Combine(root, "blocked");
            Directory.CreateDirectory(blockedPath);
            var blocked = new CashShopPromotionConfiguration(blockedPath, json, Parse);
            var retained = blocked.Current;
            Reject<UnauthorizedAccessException>(() => blocked.SetEnabled(f.Rules.Scope, retained.Revision, false), "目标为目录时文件原子替换失败");
            That(ReferenceEquals(blocked.Current, retained), "落盘失败不发布新运行开关");

            foreach (bool enabled in new[] { false, true })
            {
                string racePath = Path.Combine(root, "race-" + enabled + ".json");
                var race = new CashShopPromotionConfiguration(racePath, json, Parse);
                f.Catalog = race.Current;
                var plan = f.Plan(2);
                string snapshot = f.Sql.Snapshot();
                var store = new CharacterRepository(f.Sql.Path, promotions: () => race.Current,
                    promotionClock: new FixedClock(f.Now), promotionCommitLease: () =>
                    {
                        race.SetEnabled(f.Rules.Scope, race.Current.Revision, false);
                        if (enabled) race.SetEnabled(f.Rules.Scope, race.Current.Revision, true);
                        return race.AcquireCommitLease();
                    });
                Reject<InvalidOperationException>(() => store.Commit(f.Owner, plan, f.Now), "购买SQL末端遇关闭或关闭重开必须拒绝旧规划");
                That(f.Sql.Snapshot() == snapshot, "最终版本拒绝整段回滚扣款、物品、累计、邮件与回执");

                var ordinary = plan.Inventory.WithAfter(plan.Inventory.After, promotionCatalogRevision: f.Catalog.Revision);
                Reject<InventoryEventRejectedException>(() => store.CommitInventory(f.Owner, f.Character, ordinary), "普通购买同样校验最终礼包目录版本");
                That(f.Sql.Snapshot() == snapshot, "普通购买版本失败也完整回滚");
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS promotion independent controls {Count - before}");
    }
}
