using System.Text.Json;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class NativePromotionPageSessionTests
{
    static CashShopPromotionCatalog Catalog()
    {
        var rules = CashShopPromotionTests.PilotRules();
        return CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, false) }),
            CashShopCatalog.Parse("[item]\n" + string.Concat(rules.Products.Select(product =>
                $"{product.CommodityId} {product.ItemId} 1 0 0 100 0 0 0\n"))),
            id => new(id, ItemKind.Consumable, 0, 1, 0, 1, 0, 0, 0, "[free]", ushort.MaxValue, 0, "原生页面测试物品"), _ => null);
    }

    static PromotionPageSnapshot Snapshot(CashShopPromotionCatalog catalog, long? revision = 0)
    {
        var scope = CashShopPromotionPageService.Scope;
        return new(scope.Campaign, scope.Period, scope.RulesVersion, scope.Realm, scope.Group, catalog.Revision,
            revision, "unavailable", 0, 5000, null, null, 10000, [], null);
    }

    static NativePromotionPageCapture Capture(GameSession session, CashShopPromotionCatalog catalog)
    {
        var capture = session.CaptureNativePromotionPage(catalog);
        That(capture is not null, "原生页面捕获当前真实Selected身份");
        return capture!;
    }

    public static void RunCore()
    {
        int before = Count;
        using (var f = new CharacterScoreNoticeTests.Fixture())
        {
            var catalog = Catalog();
            f.Session.ReturnToRoster();
            That(f.Session.CaptureNativePromotionPage(catalog) is null, "原生页面不在未选角色名册生成个人快照");
            f.Session.Select(0);
            var capture = Capture(f.Session, catalog);
            var snapshot = Snapshot(catalog);
            That(!f.Session.Ready && f.Session.Phase == SessionPhase.Unselected &&
                f.Session.CanPublishNativePromotionPage(capture, catalog, snapshot),
                "真实入城初始化Ready=false/Unselected但已选角色仍可下发108");
            f.Session.MarkReady();
            That(!f.Session.CanPublishNativePromotionPage(capture, catalog, snapshot), "初始化结束后不能发送旧阶段快照");
            var ready = Capture(f.Session, catalog);
            That(f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { Status = "disabled" }),
                "购买Disabled不阻止当前本人只读页面");
            That(f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { Count = 7, ProgressRevision = null }),
                "unavailable保留真实已知累计且不伪造缺失进度revision");
            That(!f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { ProgressRevision = -1 }) &&
                !f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { CatalogRevision = catalog.Revision + 1 }) &&
                !f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { Group = "other" }),
                "拒绝负进度revision、目录revision错配及非固定scope结果");
            That(!f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot, 1) &&
                !f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { ProgressRevision = null }, 1) &&
                f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { ProgressRevision = 1 }, 1) &&
                f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot with { ProgressRevision = 2 }, 1),
                "购买刷新只接受实际提交revision或更高真实账本，未知与旧revision不冒更新");
            That(!f.Session.CanPublishNativePromotionPage(ready, Catalog(), snapshot), "同内容新目录对象/revision撤旧原生快照");
            f.Clock.Advance(9); f.Clock.RollBack(100);
            That(f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot), "墙钟回拨不提前撤单调时钟有效快照");
            f.Clock.Advance(1);
            That(!f.Session.CanPublishNativePromotionPage(ready, catalog, snapshot), "写前单调时钟10秒到期拒绝旧原生快照");
        }
        foreach (string change in new[] { "same-role", "other-role", "end", "owner" })
        {
            using var f = new CharacterScoreNoticeTests.Fixture();
            var catalog = Catalog();
            var capture = Capture(f.Session, catalog);
            DfoServer.Game.Accounts.AccountOwners.Lease? replacement = null;
            try
            {
                if (change == "owner") replacement = f.Owners.Acquire(f.Owner.Account, () => { });
                else if (change == "end") f.Session.End();
                else
                {
                    f.Session.ReturnToRoster(); f.Session.Select(change == "same-role" ? (ushort)0 : (ushort)1); f.Session.MarkReady();
                }
                That(!f.Session.CanPublishNativePromotionPage(capture, catalog, Snapshot(catalog)),
                    "原生写前拒绝旧generation/selection epoch/角色/结束状态：" + change);
            }
            finally { replacement?.Dispose(); }
        }
        using (var f = new CharacterScoreNoticeTests.Fixture())
        {
            var catalog = Catalog();
            var stale = Capture(f.Session, catalog);
            var saved = f.Session.MutateInventory(character => new(true, 0,
                new(character.Inventory, character.Inventory with { Gold = character.Inventory.Gold + 1 }, "native-page-test")));
            That(saved.Accepted && saved.Receipt is not null &&
                !f.Session.CanPublishNativePromotionPage(stale, catalog, Snapshot(catalog)),
                "真实资产提交替换Selected后旧原生累计快照失效");
            var fresh = Capture(f.Session, catalog);
            var snapshot = Snapshot(catalog, 1);
            That(f.Session.CanPublishInventory(saved.Receipt!) &&
                f.Session.CanPublishNativePromotionPage(fresh, catalog, snapshot, 1),
                "提交回执与原生身份/revision复合守卫允许真实新快照");
            f.Session.MutateInventory(character => new(true, 0,
                new(character.Inventory, character.Inventory with { Gold = character.Inventory.Gold + 1 }, "native-page-newer")));
            That(!f.Session.CanPublishInventory(saved.Receipt!) &&
                !f.Session.CanPublishNativePromotionPage(fresh, catalog, snapshot, 1),
                "后续真实提交同时撤销旧receipt与旧原生快照");
        }
        Console.WriteLine($"Native promotion page session: {Count - before} checks passed.");
    }
}
