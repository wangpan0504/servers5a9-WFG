using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal Func<bool>? CaptureServerBroadcastGuard(byte server, InventoryCommitReceipt? source = null)
    {
        try
        {
            return owner.Execute<Func<bool>?>(() =>
            {
                if (!Ready || Selected is not { } character || (ChannelEntry?.Identity.ServerIndex ?? 0) != server ||
                    source is not null && (source.Generation != owner.Generation || source.SelectionEpoch != SelectionEpoch ||
                        source.Character.Id != character.Id)) return null;
                long epoch = SelectionEpoch;
                ushort id = character.Id;
                // 公告描述已经提交的历史操作；后续普通资产revision变化不撤销公告，换角和接管必须失效。
                return () => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == id &&
                    (ChannelEntry?.Identity.ServerIndex ?? 0) == server;
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
