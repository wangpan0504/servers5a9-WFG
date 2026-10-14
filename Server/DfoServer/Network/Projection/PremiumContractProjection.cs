using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 这里只记录已下发视图；账号期限和角色选择始终来自Session的正式快照。
internal sealed class PremiumContractProjection(GameConnectionContext context, CubeContractService cubes,
    CharacterStatComputer stats, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    ImmutableSortedDictionary<int, long>? announced;
    long contractsEpoch = -1;
    CubeContractState? announcedSelection;
    long epoch = -1;
    (bool Growth, sbyte Cube)? announcedEffects;
    long effectsEpoch = -1;

    public async Task ContractsAsync(Character character, DateTimeOffset? frozenAt = null,
        Func<bool>? guard = null, IReadOnlyCollection<int>? resetTypes = null)
    {
        var now = frozenAt ?? clock.GetUtcNow();
        var active = character.Inventory.AccountWallet.PremiumContracts.Where(row => row.Value > now.ToUnixTimeSeconds()).ToImmutableSortedDictionary();
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
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.Ready, context.Session.SelectionEpoch));
        if (!captured.Ready || captured.Selected is not { } character) return;
        bool Guard() => context.Owner.Execute(() => context.Session.SelectionEpoch == captured.SelectionEpoch &&
            ReferenceEquals(context.Session.Selected, character));
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        // 56/58及已有玩法继续在各自原包序刷新；不能因任意请求把过期会员通知插入普通副本ACK前后。
        bool changed = announced is null || new[] { 84, 97 }.Any(type =>
            announced.GetValueOrDefault(type) != (character.Inventory.AccountWallet.ContractExpiry(type) > now ?
                character.Inventory.AccountWallet.ContractExpiry(type) : 0));
        if (changed)
            await ContractsAsync(character, guard: Guard);
        else await SelectionAsync(character, now, Guard);
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
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Player(character, stats.Compute(character), context.Channel),
            "premium_equipment_refresh", () => refreshed = guard?.Invoke() ?? true);
        if (refreshed) { announcedEffects = effects; effectsEpoch = currentEpoch; }
    }
}
