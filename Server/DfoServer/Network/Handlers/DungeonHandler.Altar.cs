using System.Security.Cryptography;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task<CommandResult> AltarDeathAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonAltarRequests.TryParseDeaths(request.Body, out var keys)) return CommandResult.Unhandled;
        var receipt = context.Session.ObserveDungeonAltarDeaths(keys, Convert.ToHexString(SHA256.HashData(request.Body)));
        return await PublishAltarDeathsAsync(context, receipt, keys.Length, CmdPacketType.DIE_BLOOD_MONSTER);
    }

    async Task<CommandResult> AltarMonsterDeathAsync(GameConnectionContext context, NativeDungeonDeathObservation observation)
    {
        var receipt = context.Session.ObserveDungeonAltarDeath(observation);
        if (receipt is not null)
            await context.Notify(NotiPacketType.DIE_MONSTER, ExclusiveDropPackets.MonsterDeath(checked((ushort)observation.MonsterKey), [],
                context.Session.FormalEnteredDungeonId),
                "formal_altar_monster_death", () => context.Session.CanPublishDungeonAltar(receipt));
        return await PublishAltarDeathsAsync(context, receipt, 1, CmdPacketType.DIE_MONSTER, observation.MonsterKey);
    }

    async Task<CommandResult> PublishAltarDeathsAsync(GameConnectionContext context, DungeonAltarReceipt? receipt,
        int count, CmdPacketType opcode, uint? objectKey = null)
    {
        // 重发39/255时也尝试恢复原SQL结果；已经冻结的奖励不会再次抽取。
        if (altarRewards is not null && context.Session.BeginDungeonAltarSettlement(altarRewards, growth) is { } settlement)
            receipt = settlement;
        else if (altarRewards is not null && context.Altar is not null && context.Session.WaitDungeonAltarRevival() is { } waiting)
        {
            // 死亡后的残留攻击与重发255仍保留本次截止，不能用波次计时替换复活窗口。
            await context.Altar.WakeAfterDeathAsync(waiting, () => context.Session.BeginDungeonAltarSettlement(altarRewards, growth));
            return CommandResult.Handled;
        }
        log.Write("formal_altar_deaths", new { Connection = context.Id, Accepted = receipt is not null,
            Count = count, Opcode = (ushort)opcode, ObjectKey = objectKey, receipt?.InstanceId, receipt?.DisplayRound, receipt?.Completed });
        if (receipt is not null && context.Altar is not null) await context.Altar.WakeAsync(receipt);
        return CommandResult.Handled;
    }

    async Task<CommandResult> AltarPreparedAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        var scene = context.Session.CurrentFormalDungeonScene();
        if (scene?.Monsters.Altar is null) return CommandResult.Unhandled;
        if (!context.Session.PrepareDungeonAltarUi(scene)) return CommandResult.Handled;
        if (context.Altar is not null && context.Session.StartDungeonAltar(scene) is { } started)
            await context.Altar.WakeAsync(started);
        return CommandResult.Handled;
    }

    async Task<CommandResult> AltarDifficultyAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body is not [>= 1 and <= 2]) return CommandResult.Unhandled;
        var receipt = context.Session.SelectDungeonAltarDifficulty(request.Body[0]);
        if (receipt is null) return CommandResult.Handled;
        // V31 ACK317的02333CA0先按外层success分派，再读一个难度字节。
        await context.Reply(CmdPacketType.SELECT_ULTIMATE_DIFFICULTY, [1, receipt.Difficulty], "formal_altar_difficulty_ack",
            () => context.Session.CanPublishDungeonAltar(receipt));
        if (context.Altar is not null) await context.Altar.WakeAsync(receipt);
        return CommandResult.Handled;
    }
}
