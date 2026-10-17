using DfoServer.Game.Characters;
using DfoServer.Game.CollectBox;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class CollectBoxProjection(CollectBoxCatalog boxes, TimeProvider? clock = null)
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;

    public async Task InitializeAsync(GameConnectionContext context)
    {
        var character = context.Session.Selected ?? throw new InvalidOperationException("宝珠收集箱初始化缺少角色");
        byte selected = Selected(character);
        long now = timer.GetUtcNow().ToUnixTimeSeconds();
        foreach (var box in boxes.Entries)
            await context.Notify(NotiPacketType.COLLECT_BOX,
                CollectBoxPackets.State(box, character.CollectBox, selected, now), "collectbox_initialize");
    }

    public Task RefreshAsync(GameConnectionContext context, CollectBoxCommitReceipt receipt, byte box)
    {
        if (!boxes.TryGet(box, out var entry))
            throw new InvalidOperationException("提交结果引用未配置宝珠收集箱");
        byte selected = Selected(receipt.Character);
        return context.Notify(NotiPacketType.COLLECT_BOX,
            CollectBoxPackets.State(entry, receipt.Character.CollectBox, selected, timer.GetUtcNow().ToUnixTimeSeconds()),
            "collectbox_refresh", () => context.Session.CanPublishCollectBox(receipt));
    }

    byte Selected(Character character) => boxes.TryGet(character.CollectBox.SelectedBox, out _)
        ? character.CollectBox.SelectedBox : boxes.Entries[0].Index;
}
