#if DEBUG
using System.Buffers.Binary;
using DfoServer.Game.Chat;
using DfoServer.Game.Dungeons;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    internal static bool IsDebugKillCommand(ChatSend request) => request.TargetName is null &&
        request.Type is 2 or 3 && request.Text is "#kill" or "#raidkill";

    internal async Task DebugKillAsync(GameConnectionContext context)
    {
        var captured = context.Session.CaptureDebugDungeonKill();
        if (captured is null) return;
        int processed = 0;
        while (context.Session.BeginDebugDungeonKillTarget(captured) is { } monster)
        {
            // 同一正式39链依次提交经验、任务、掉落和清图；共享投影全部安装后才处理下一只。
            await DeathAsync(context, new(1, (ushort)CmdPacketType.DIE_MONSTER,
                DebugDeathReport(monster, captured.Scene.Character.Id)));
            if (!context.Session.CompleteDebugDungeonKillTarget(captured)) break;
            processed++;
        }
        log.Write("debug_dungeon_kill", new { Connection = context.Id, captured.Scene.Snapshot.Identity,
            Targets = captured.Targets.Length, Processed = processed });
    }

    static byte[] DebugDeathReport(DungeonPlannedMonster monster, ushort reporter)
    {
        // 匹配本版39的单参与者88B形状；调试数值只用于审计，不伪装成实收伤害。
        byte[] body = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(body, monster.ObjectKey);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(26), 1);
        body[30] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(31), 0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(33), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(35), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(43), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(56), checked((uint)monster.BattleLevel));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(60), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(84), ClientBodyChecksum.Compute(body.AsSpan(0, 84)));
        return body;
    }
}
#endif
