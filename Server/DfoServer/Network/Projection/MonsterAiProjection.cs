using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class MonsterAiProjection
{
    // 客户端在本次房间的原生29之后才应用四项参数，因此私有包必须先于该29进入同一连接的FIFO；
    // 入队失败不能当作已应用继续发29，交给同一发送错误结束本次命令。
    public static Task SendAsync(GameConnectionContext context, FormalDungeonReceipt receipt, string action, Func<bool>? canSend = null)
    {
        bool counterRoom = receipt.Entry.RaidEntry?.Kind == Game.Raids.RaidKind.Fiendwar &&
            Game.Titles.FiendwarTitleFacts.IsTeamDungeon(receipt.Entry.DungeonId);
        if (receipt.MonsterAi is not { } && !counterRoom) return Task.CompletedTask;
        var profile = receipt.MonsterAi ?? MonsterAiProfile.Native;
        DungeonMap map = receipt.Snapshot.CurrentMap;
        uint serial = context.NextMonsterAiSerial();
        byte[] body = MonsterAiPackets.Encode(serial, receipt.Entry.DungeonId, checked((uint)map.Id),
            receipt.MapSeed, map.Position, profile);
        context.FiendwarCounterRoom = counterRoom ? new(serial, receipt) : null;
        return context.Sender.Send(0, MonsterAiPackets.Opcode, body, action, canSend);
    }
}
