using System.Security.Cryptography;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Handlers;

internal sealed class DungeonHandler(ServerLog log, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
    CharacterGrowthCatalog growth, DungeonKillExperienceCatalog rewards, TownProjection town,
    DungeonMonsterDropCatalog? drops = null, ItemCatalog? items = null, EpicPieceCatalog? pieces = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_DUNGEON, SelectAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.FINISH_LOADING, LoadedAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.DIE_MONSTER, DeathAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.MOVE_MAP, MoveAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.GIVEUP_GAME, ReturnAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.GET_ITEM, PickupAsync, nameof(DungeonHandler));
    }

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseSelection(request.Body, out var selection)) return CommandResult.Unhandled;
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.Ready,
            context.Session.SelectionEpoch, context.Session.Phase, context.Session.CurrentDungeonSelection));
        var character = before.Selected;
        if (character is null || !before.Ready || before.Phase is not (SessionPhase.Town or SessionPhase.MapSelect) ||
            context.BlocksTownActions)
            return CommandResult.Unhandled;
        // 匹配普通单人3145形状；未知模式和费用资格不套用该入口。
        if (selection.Unknown5 != 0 || selection.Unknown7 != 0 || selection.Unknown8 != 0 || selection.Unknown9 != ushort.MaxValue ||
            selection.Unknown11 != 0 || selection.Unknown15 != 0 || selection.Unknown20 != 0 || selection.Unknown21 != 0)
            return await RejectAsync();
        var receipt = context.Session.EnterFormalDungeon(selection.DungeonIndex, selection.Unknown16, selection.Parameter, dungeons, rooms, drops, items);
        log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = receipt is not null, Request = selection, receipt?.Snapshot.Identity });
        if (receipt is null) return await RejectAsync();
        await town.DetachAsync(context);
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(receipt.Entry), "formal_dungeon_information", Guard);
        await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed), "formal_start_map", Guard);
        return CommandResult.Handled;

        async Task<CommandResult> RejectAsync()
        {
            bool CanSend() => context.Session.CanPublishFormalDungeonRejection(character, before.SelectionEpoch,
                before.Phase, before.CurrentDungeonSelection);
            // 只结束本次入场等待，保留原选图和回城锚点；不发送成功、弹窗或自动回城通知。
            await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "formal_dungeon_rejected", CanSend);
            return CommandResult.Handled;
        }
    }

    async Task<CommandResult> LoadedAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 8 || request.Body.Any(value => value != 0)) return CommandResult.Unhandled;
        var receipt = context.Session.CompleteFormalDungeonLoading();
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.FINISH_LOADING, [1], "formal_loading_ack", Guard);
        await context.Notify(NotiPacketType.FINISH_LOADING, DungeonPackets.LoadingNotice(), "formal_loading_notice", Guard);
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "formal_player_state", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeathAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseDeath(request.Body, out var parsed) || parsed!.SinglePlayerStatistics is not { } statistics)
            return CommandResult.Unhandled;
        var part = statistics.Participant;
        var observation = new NativeDungeonDeathObservation(parsed.ObjectKey, parsed.Reporter, part.Kind, part.ObjectKey,
            part.CumulativeHpDecrease, part.PositiveHpDecreaseCount, statistics.RewriteFlag, statistics.SpawnLevel,
            statistics.SpawnHpSnapshot, statistics.Unknown18, statistics.Unknown26, statistics.ChecksumMatches,
            Convert.ToHexString(SHA256.HashData(request.Body)));
        var result = context.Session.ObserveFormalDungeonDeath(observation, rewards, growth);
        log.Write("formal_dungeon_death", new { Connection = context.Id, result.Outcome, parsed.ObjectKey, result.Receipt?.Snapshot.Identity, result.Reward, result.ClearFact });
        if (result.Outcome == DungeonRunOutcome.Duplicate) return CommandResult.Handled;
        if (result.Outcome != DungeonRunOutcome.Accepted || result.Receipt is not { } receipt) return CommandResult.Unhandled;
        var state = receipt.Character.DungeonState ?? throw new InvalidOperationException("已提交角色缺成长附属owner");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)receipt.Character.Inventory.WinPoints), state.GrowthCapsuleExperience,
            state.ProficiencyLevel, state.ProficiencyExperience);
        // 本服单人倍率1，显示本次额度；无额外奖励分量显式为0，不覆盖其他owner余额。
        var display = new CharacterExperienceDisplay(result.Reward!.Amount, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0, 0);
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        DungeonDropPacketItem[] dropRows = result.Drop is { } drop ?
            [new(drop.Reservation.GroundKey, drop.Reservation.RecordSlot, drop.Core, drop.Reservation.X, drop.Reservation.Y)] : [];
        await context.Notify(NotiPacketType.DIE_MONSTER, DungeonPackets.MonsterDeath(result.MonsterKey, dropRows), "formal_monster_death", Guard);
        await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(receipt.Character.Level, receipt.Character.Learning, auxiliary, display), "formal_kill_experience", Guard);
        if (result.ClearFact is not null && context.Session.EnableFormalDungeonSettlement(result.MonsterKey) is { } completion)
        {
            bool CompletionGuard() => context.Session.CanPublishDungeonSettlement(completion.Receipt);
            await context.Notify(NotiPacketType.BOSS_DIE_CHECK, DungeonSettlementPackets.BossAccepted(completion.BossKey), "formal_boss_confirmed", CompletionGuard);
            bool enabled = false;
            await context.Notify(NotiPacketType.ENABLE_CLEAR_DUNGEON, [], "formal_clear_enabled", () => enabled = CompletionGuard());
            if (enabled) context.Session.ConfirmFormalDungeonSettlementEnabled(completion.Receipt);
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> PickupAsync(GameConnectionContext context, Packet request)
    {
        if (items is null || pieces is null || !DungeonPickupRequests.TryParseOrdinary(request.Body, out var parsed))
            return CommandResult.Unhandled;
        var result = context.Session.PickFormalDungeonItem(parsed.GroundObjectKey, items, pieces);
        if (result.Outcome != DungeonRunOutcome.Accepted || result.Receipt is not { } receipt || result.Commit is not { } commit)
        {
            // 本服普通拒绝用已证可解除等待且保留原地面核心的22，不命名为原版满包错误。
            await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Rejected(22), "formal_pickup_rejected");
            return CommandResult.Handled;
        }
        if (!commit.ProjectionCurrent) return CommandResult.CloseConnection;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        // 普通consumer不使用这九个局部值。本服无扩展贡献，明确投影零，不作Builder默认。
        await context.Notify(NotiPacketType.GET_ITEM, DungeonPickupPackets.OrdinaryNotification(parsed.GroundObjectKey,
            receipt.Character.Id, new byte[8], receipt.Character.Id, checked((ushort)commit.Destination.Index), 0), "formal_pickup_notice", Guard);
        await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
            InventoryListType.Main, [commit.Destination]), "formal_pickup_inventory", Guard);
        await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Accepted(), "formal_pickup_ack", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> MoveAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseMove(request.Body, out var move)) return CommandResult.Unhandled;
        var receipt = context.Session.MoveFormalDungeon(move!.X, move.Y);
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveAccepted(), "formal_move_ack", Guard);
        await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed), "formal_next_map", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ReturnAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        var receipt = context.Session.ReturnFromFormalDungeon();
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishTravel(receipt);
        await context.Reply(CmdPacketType.GIVEUP_GAME, DungeonPackets.GiveUpAccepted(), "formal_giveup_ack", Guard);
        await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(receipt.Character.Id, receipt.Character.Position), "formal_return_town", Guard);
        context.Owner.Execute(() =>
        {
            if (Guard()) town.Join(context);
            return true;
        });
        return CommandResult.Handled;
    }
}
