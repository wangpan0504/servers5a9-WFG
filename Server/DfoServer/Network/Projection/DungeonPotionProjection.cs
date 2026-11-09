using System.Buffers.Binary;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DungeonPotionProjection
{
    internal static async Task LoadedAsync(GameConnectionContext context, Func<bool> current)
    {
        if (context.Session.RemainingDungeonPotions() is not { } remaining) return;
        byte[] body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, remaining);
        // 0237FAF0读取一个DWORD；02D834A0把本地已用清零。因此加载时发剩余，不在每次成功后重发总额。
        await context.Notify(NotiPacketType.STACKABLE_DUNGEON_LIMIT, body, "dungeon_potion_limit_loaded", current);
    }
}
