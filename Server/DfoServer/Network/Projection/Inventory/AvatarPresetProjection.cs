using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

// 0x063D 不创建窗口，只恢复已应用槽与预设缓存；就绪后必须先发，否则窗口打开为空且点击不发包。
// 整表是客户端唯一的重绘入口：首字段同步已应用槽和浏览槽，随后刷新行文字、名称框、按钮与预览。
// 0x063F 只覆盖客户端已存在的槽位、不重绘，未知槽位会 noreturn，只用于其他提交带来的内容同步。
internal sealed class AvatarPresetProjection
{
    public async Task DeliverAsync(GameConnectionContext context)
    {
        var selected = context.Session.Selected;
        if (selected is null) return;
        long epoch = context.Session.SelectionEpoch;
        bool Guard() => context.Session.Ready && context.Session.SelectionEpoch == epoch &&
            ReferenceEquals(context.Session.Selected, selected);
        await context.Notify(NotiPacketType.AVATAR_PRESET_LIST,
            AvatarPresetPackets.List(selected.Inventory.AvatarPresets), "avatar_preset_deliver", Guard);
    }

    public Task ListAsync(GameConnectionContext context, InventoryCommitReceipt receipt, string action) =>
        context.Notify(NotiPacketType.AVATAR_PRESET_LIST,
            AvatarPresetPackets.List(receipt.Character.Inventory.AvatarPresets), action,
            () => context.Session.CanPublishInventory(receipt));
}
