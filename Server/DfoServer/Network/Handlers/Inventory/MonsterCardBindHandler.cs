using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class MonsterCardBindHandler(InventoryMonsterCardBindService compounds,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.MONSTERCARD_BIND, (context, packet) => Handle(context, packet, false), nameof(MonsterCardBindHandler));
        commands.Register(CmdPacketType.CARD_COMPOUND, (context, packet) => Handle(context, packet, true), nameof(MonsterCardBindHandler));
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet, bool legacyCompound)
    {
        var opcode = legacyCompound ? CmdPacketType.CARD_COMPOUND : CmdPacketType.MONSTERCARD_BIND;
        MonsterCardBindRequest request;
        try { request = MonsterCardBindRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("monster_card_bind", new
            {
                Connection = context.Id,
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(opcode, MonsterCardBindPackets.Failure(),
                "monster_card_bind_malformed");
            return CommandResult.Handled;
        }

        var original = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        MonsterCardBindReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(22)
            : context.Session.BindMonsterCard(request, compounds, out receipt, out rejection);
        rejection ??= result.Accepted ? null : "blocked-state";
        bool CanSend() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : context.Owner.Execute(() => context.Session.SelectionEpoch == original.SelectionEpoch &&
                context.Session.Phase == original.Phase && ReferenceEquals(context.Session.Selected, original.Selected));

        log.Write("monster_card_bind", new
        {
            Connection = context.Id,
            result.Accepted,
            result.Error,
            Reason = result.Accepted ? "committed" : rejection,
            Synthesizer = request.SynthesizerSlot,
            Cards = request.CardSlots.Length,
            BindType = receipt?.BindType,
            InputRarity = receipt?.InputRarity,
            OutputRarity = receipt?.OutputRarity,
            receipt?.RarityUpgraded,
            Reward = receipt is { Rewards: [{ ItemId: var rewardId }, ..] } ? rewardId : 0,
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(opcode,
                MonsterCardBindPackets.Failure(result.Error == 23 ? (ushort)23 : (ushort)22),
                "monster_card_bind_rejected", CanSend);
            return CommandResult.Handled;
        }
        if (receipt is null)
            throw new InvalidOperationException("怪物卡片合成成功缺少业务回执");

        await context.Reply(opcode,
            legacyCompound ? MonsterCardBindPackets.CompoundSuccess(receipt) : MonsterCardBindPackets.Success(receipt),
            "monster_card_bind_committed", CanSend);
        try
        {
            await projection.RefreshAsync(context, result, result.Plan!.ChangedSlots.ToHashSet());
            if (result.Plan.MonsterCardBind is { } fact && CanSend())
                await DfoServer.Network.Projection.BattlePassMissionProjection.RefreshAsync(context, fact.Clock.GetUtcNow());
        }
        catch (System.Data.Common.DbException error) when (result.Plan?.MonsterCardBind is not null)
        {
            // 原合成器、卡片和随机奖励已提交，发布失败不能重抽或重扣。
            throw new IOException("Committed monster card bind publication failed", error);
        }
        return CommandResult.Handled;
    }
}
