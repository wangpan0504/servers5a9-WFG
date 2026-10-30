using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 这里只记录已下发视图；账号期限和角色选择始终来自Session的正式快照。
internal sealed class PremiumContractProjection(GameConnectionContext context, CubeContractService cubes,
    CharacterStatComputer stats, TimeProvider? time = null, DevilContractService? devil = null,
    Inventory.InventoryProjection? inventory = null, TownProjection? town = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    ImmutableSortedDictionary<int, long>? announced;
    long contractsEpoch = -1;
    CubeContractState? announcedSelection;
    long epoch = -1;
    (bool Growth, sbyte Cube)? announcedEffects;
    long effectsEpoch = -1;
    (long Epoch, byte Percent)? announcedStamina;
    (long Epoch, bool BlackDiamond, bool PcRoom)? announcedAppearance;

    public void RecordPremiumAppearance(Character character, long utcSeconds, bool premiumPcRoom) => context.Owner.Execute(() =>
    {
        if (ReferenceEquals(context.Session.Selected, character))
            announcedAppearance = (context.Session.SelectionEpoch, character.BlackDiamondExpiresAt > utcSeconds, premiumPcRoom);
        return true;
    });

    public async Task ContractsAsync(Character character, DateTimeOffset? frozenAt = null,
        Func<bool>? guard = null, IReadOnlyCollection<int>? resetTypes = null)
    {
        var now = frozenAt ?? clock.GetUtcNow();
        var active = DevilContractPolicy.ClientContracts(character.Inventory.AccountWallet).Where(row => row.Value > now.ToUnixTimeSeconds()).ToImmutableSortedDictionary();
        var previous = announced;
        long currentEpoch = context.Session.SelectionEpoch;
        bool selectionChanged = contractsEpoch != currentEpoch;
        var clear = (resetTypes ?? []).Concat(previous?.Keys.Where(type => !active.ContainsKey(type)) ?? []).Distinct().ToArray();
        bool sent = false;
        await context.Notify(NotiPacketType.PREMIUM_INFO_LIST, PremiumPackets.SelectEntries(character.Inventory.AccountWallet, now, clear),
            "premium_info_committed", () => sent = guard?.Invoke() ?? true);
        if (!sent) return;
        announced = active;
        contractsEpoch = currentEpoch;
        if (previous is not null && previous.GetValueOrDefault(27) != active.GetValueOrDefault(27))
            await context.Notify(NotiPacketType.CERA_SPECIALITEM, SkillPackets.ExpertContract(character.Learning.ExpertContractExpiresAt,
                now.ToUnixTimeSeconds()), "expert_contract_committed", guard);
        await SelectionAsync(character, now.ToUnixTimeSeconds(), guard);
        // 66重置横幅管理时钟；97更新会检查选择集，必须在901及本人效果恢复之后发送。
        foreach (int type in new[] { 84, 97 })
            if (selectionChanged && active.ContainsKey(type) ||
                (previous?.GetValueOrDefault(type) ?? 0) != active.GetValueOrDefault(type))
                await context.Notify(NotiPacketType.CERA_SPECIALITEM,
                    PremiumPackets.Updated(type, active.GetValueOrDefault(type), now.ToUnixTimeSeconds()), "premium_service_updated", guard);
    }

    public async Task RefreshAsync()
    {
        var giftCharacter = context.Owner.Execute(() => context.Session.Ready ? context.Session.Selected : null);
        if (devil is not null && inventory is not null && giftCharacter is not null &&
            DevilContractService.NeedsGift(giftCharacter.Inventory, clock.GetUtcNow()))
        {
            var gift = context.Session.PlanAndCommitInventory(character => devil.DailyGift(character, clock.GetUtcNow()), requireTown: false);
            await inventory.RefreshAsync(context, gift);
        }
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.Ready, context.Session.SelectionEpoch));
        if (!captured.Ready || captured.Selected is not { } character) return;
        bool Guard() => context.Owner.Execute(() => context.Session.SelectionEpoch == captured.SelectionEpoch &&
            ReferenceEquals(context.Session.Selected, character));
        var instant = clock.GetUtcNow();
        long now = instant.ToUnixTimeSeconds();
        await StaminaAsync(character, Guard);
        // 请求完成后检查黑钻到期/撤销；不在业务ACK之前插入会员刷新，也不把魔王子服务扩展为逐请求重发。
        bool changed = announced is null || new[] { 56, 84, 88, 92, 97 }.Any(type =>
            announced.GetValueOrDefault(type) != (character.Inventory.AccountWallet.ContractExpiry(type) > now ?
                character.Inventory.AccountWallet.ContractExpiry(type) : 0));
        if (changed)
            await ContractsAsync(character, instant, Guard);
        else await SelectionAsync(character, now, Guard);
        await AppearanceAsync(character, now, Guard);
    }

    async Task AppearanceAsync(Character character, long now, Func<bool> guard)
    {
        var state = (Epoch: context.Session.SelectionEpoch, BlackDiamond: character.BlackDiamondExpiresAt > now,
            PcRoom: context.Session.PremiumPcRoom);
        if (announcedAppearance == state || !guard()) return;
        var captured = context.Owner.Execute(() => (Epoch: context.Session.SelectionEpoch,
            Phase: context.Session.Phase, Name: context.Session.Phase == SessionPhase.Loading ? "" : context.Session.ReadAdventureName()));
        // 等37完成当前房间加载后再更新Actor，不能重建尚未就绪的场景，也不提前记录已发视图。
        if (captured.Phase == SessionPhase.Loading) return;
        bool CanSend() => context.Owner.Execute(() => guard() && context.Session.SelectionEpoch == captured.Epoch &&
            context.Session.Phase == captured.Phase);
        bool sent = false;
        // 两种身份各自刷新；保留副本场景位，再以Player恢复完整本人属性。
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(character, context.Session.ReadPvpGrade(character), context.Channel, now, captured.Name,
            captured.Phase is SessionPhase.Dungeon or SessionPhase.Settlement ? ActorSceneState.Dungeon : ActorSceneState.Town,
            state.PcRoom), "premium_identity_actor", () => sent = CanSend());
        if (!sent) return;
        sent = false;
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Player(character, stats.Compute(character),
            context.Session.ReadAdventureLevel(), context.Channel, DateTimeOffset.FromUnixTimeSeconds(now)),
            "premium_identity_player", () => sent = CanSend());
        if (!sent) return;
        // 网吧身份真正切换时同步特权buff：打开补发NOTI487，关闭沿NOTI488移除；同值刷新不重发。
        if (state.PcRoom != (announcedAppearance?.PcRoom ?? false))
            await PcRoomBuffProjection.SetAsync(context, state.PcRoom, CanSend);
        announcedAppearance = state;
        context.Owner.Execute(() =>
        {
            if (CanSend() && captured.Phase == SessionPhase.Town) town?.Refresh(context);
            return true;
        });
    }

    public async Task StaminaAsync(Character character, Func<bool>? guard = null, bool force = false)
    {
        if (!force && character.Inventory.StaminaUntil <= 0 && announcedStamina is not { Percent: < 100 }) return;
        var stamina = (context.Session.SelectionEpoch, StaminaRecoveryService.Percent(character.Inventory, clock.GetUtcNow()));
        if (!force && announcedStamina == stamina) return;
        bool sent = false;
        await context.Notify(NotiPacketType.STAMINA, [stamina.Item2], "stamina_natural_recovery", () => sent = guard?.Invoke() ?? true);
        if (sent) announcedStamina = stamina;
    }

    async Task SelectionAsync(Character character, long now, Func<bool>? guard)
    {
        var selected = cubes.ForClient(character.Inventory, now);
        long currentEpoch = context.Session.SelectionEpoch;
        // 没有晶体契约且本连接从未显示过选择，不发送会打开原生选择窗的初始化通知。
        if (!(announcedSelection is null && character.Inventory.AccountWallet.ContractExpiry(97) <= now) &&
            !(epoch == currentEpoch && announcedSelection == selected))
        {
            bool sent = false;
            await context.Notify(NotiPacketType.CONTRACT_OF_CUBE_INFO, CubeContractPackets.Selection(selected),
                "cube_contract_selection", () => sent = guard?.Invoke() ?? true);
            if (sent) { announcedSelection = selected; epoch = currentEpoch; }
        }
        var effects = (Growth: character.Inventory.AccountWallet.ContractExpiry(84) > now, Cube: selected.CubeType);
        if (effectsEpoch == currentEpoch && announcedEffects == effects) return;
        if (!effects.Growth && effects.Cube < 0 && (announcedEffects is null || announcedEffects == effects))
        {
            announcedEffects = effects;
            effectsEpoch = currentEpoch;
            return;
        }
        // 901只更新选择集；本人Player消费者267CC10经277F870实际重装虚拟装备，不重建场景Actor。
        bool refreshed = false;
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Player(character, stats.Compute(character),
            context.Session.ReadAdventureLevel(), context.Channel), "premium_equipment_refresh", () => refreshed = guard?.Invoke() ?? true);
        if (refreshed) { announcedEffects = effects; effectsEpoch = currentEpoch; }
    }
}
