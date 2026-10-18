using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.Testing;

// 2026-10 更新后的 Script.pvf 去掉了全部商城/活动物品的 [expiration date]（stk 仅剩 10004550 一项，equ 为 0）。
// 过期拒绝类用例需要“带绝对期限的真实物品”，这里按旧版 PVF 的原始日期（UTC+8 06:00）显式恢复这几项的期限，
// 只覆盖共享目录缓存里的模板期限，不改 PVF，也不改生产解析与期限规则。
internal static class LegacyExpiryFixture
{
    public static void Restore(ItemCatalog catalog, params (int ItemId, string LocalTime)[] rows)
    {
        catalog.PrefillTemplates(rows.Select(row => KeyValuePair.Create(row.ItemId,
            catalog.Get(row.ItemId) with { Expiration = ItemExpiration.Parse(new($"[expiration date]\n`{row.LocalTime}`\n")) })));
    }
}