using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 原生 020BE890/03DAAB80：物品记录 offset120 的低四位为已交易次数，
// 高四位为次数耗尽后的绑定类型；5 表示账号绑定。不同于旧装备 Attr 高位剩余次数。
internal static class StackableTradePolicy
{
    public static bool Applies(ItemCore core, ItemTemplate template) =>
        core.ItemId == template.Id && core.Kind == template.Kind && core.IsStackable &&
        template.AccountBoundAfterTrade && template.AccountBindingTradeLimit is > 0 and <= 15 &&
        string.IsNullOrWhiteSpace(template.AttachType);

    public static bool CanTransfer(ItemCore core, ItemTemplate template, bool sameAccount) =>
        Applies(core, template) && (core.Extension120 >> 4) is 0 or 5 &&
        ((core.Extension120 >> 4) == 5 || (core.Extension120 & 15) >= template.AccountBindingTradeLimit
            ? sameAccount : true);

    public static ItemCore AfterTransfer(ItemCore core, ItemTemplate template)
    {
        if (!Applies(core, template) || (core.Extension120 >> 4) == 5 ||
            (core.Extension120 & 15) >= template.AccountBindingTradeLimit)
            return core;
        int used = (core.Extension120 & 15) + 1;
        return core with { Extension120 = (byte)((used >= template.AccountBindingTradeLimit ? 0x50 : core.Extension120 & 0xF0) | used) };
    }
}
