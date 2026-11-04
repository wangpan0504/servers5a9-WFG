using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class MonsterAiProjection
{
    // 客户端在本次房间的原生29之后才应用四项参数，因此私有包必须先于该29进入同一连接的FIFO；
    // 入队失败不能当作已应用继续发29，交给同一发送错误结束本次命令。
    public static async Task SendAsync(GameConnectionContext context, FormalDungeonReceipt receipt, string action, Func<bool>? canSend = null)
    {
        bool antonRoom = receipt.Entry is { DungeonId: 218, RaidEntry.Kind: Game.Raids.RaidKind.Anton };
        if (!antonRoom) context.AntonNativeRoom = null;
        bool counterRoom = receipt.Entry.RaidEntry?.Kind == Game.Raids.RaidKind.Fiendwar &&
            (Game.Titles.FiendwarTitleFacts.IsTeamDungeon(receipt.Entry.DungeonId) || receipt.Entry.DungeonId is >= 5350 and <= 5358);
        bool dynamicRoom = counterRoom || receipt.Entry.DungeonId == EvildomWarChallengeEntryPolicy.DungeonId &&
            receipt.Entry.Difficulty == 2 && EvildomWarChallengeEntryPolicy.AllowsPartyCount(receipt.Entry.PartyMemberCount) && receipt.Entry.RaidEntry is null;
        if (receipt.MonsterAi is not { } && !dynamicRoom && !antonRoom) return;
        var profile = receipt.MonsterAi ?? MonsterAiProfile.Native;
        DungeonMap map = receipt.Snapshot.CurrentMap;
        uint serial = context.NextMonsterAiSerial();
        byte[] body = MonsterAiPackets.Encode(serial, receipt.Entry.DungeonId, checked((uint)map.Id),
            receipt.MapSeed, map.Position, profile);
        context.FiendwarCounterRoom = dynamicRoom ? new(serial, receipt) : null;
        if (antonRoom)
        {
            var room = new AntonNativeRoom(serial, receipt);
            if (!context.Sender.TryPost(0, MonsterAiPackets.Opcode, body, action,
                () => context.Session.CanPublishNativeScene(receipt) && canSend?.Invoke() != false,
                written: () => context.AntonNativeRoom = room))
                throw new IOException("安徒恩对象观测场景入队失败");
            return;
        }
        await context.Sender.Send(0, MonsterAiPackets.Opcode, body, action, canSend);
        if (dynamicRoom && context.Session.FiendwarSceneKeyOffer(receipt, serial) is { } offer)
            await context.Sender.Send(0, FiendwarKeyLeasePackets.Opcode, FiendwarKeyLeasePackets.Build(offer),
                "fiendwar_key_scene_offer", () => canSend?.Invoke() != false && context.Session.CanPublishFiendwarKeyMessage(offer));
    }
}
