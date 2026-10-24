using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

// 0x063D 不创建窗口，只填充客户端容量与预设缓存；就绪后必须先发，否则窗口打开为空且点击不发包。
// 整表是客户端唯一的重绘入口：行文字、名称框与装扮栏纸娃娃都由它触发的行刷新重建，但客户端会把
// 当前行设成容量值、越界回退到槽0（FUN_0083E940），选中行与名称框因此总停在第1格。
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
