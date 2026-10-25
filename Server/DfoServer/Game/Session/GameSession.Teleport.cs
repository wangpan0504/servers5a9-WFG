using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 未决提交和已冻结连图/回放依赖原快照；未提交手册及已提交回答缓存仍可随普通操作失效。
    bool CanTeleport() => pendingRental is null && Ready && Phase == SessionPhase.Town && Selected is not null && !HasPendingTownQuestCommit &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay && !HasPendingFormalCard;

    public TeleportResult TeleportPotion(PotionTeleportRequest request, TeleportService service, bool blocked = false) =>
        PlanTeleport(character => service.Potion(character, request, service.Clock.GetUtcNow().ToUnixTimeMilliseconds()), service.Clock, blocked);

    public TeleportResult TeleportSolo(SoloTeleportRequest request, TeleportService service, TeleportTarget? target,
        Func<bool>? targetCurrent = null, bool blocked = false) => PlanTeleport(character =>
        service.Solo(character, request, target, service.Clock.GetUtcNow().ToUnixTimeMilliseconds()), service.Clock, blocked, targetCurrent);

    public TeleportResult TeleportSingleMemberParty(PartyTeleportRequest request, TeleportService service, bool blocked = false)
    {
        var captured = CurrentParty?.Invoke();
        if (captured is not { Members.Length: 1 } ||
            !ReferenceEquals(captured.Members[0].Actor.Session, this) || captured.Leader != captured.Members[0].Slot) return new("party-context-unavailable");
        // 原生流程将队长视为已同意；当前队伍确实只有本人，不创建虚假的待确认成员。
        return PlanTeleport(character => service.Party(character, request, service.Clock.GetUtcNow().ToUnixTimeMilliseconds()),
            service.Clock, blocked, contextCurrent: () => ReferenceEquals(CurrentParty?.Invoke(), captured) &&
                captured.Members[0].IsCurrent());
    }

    TeleportResult PlanTeleport(Func<Character, (string Reason, TeleportPlan? Plan)> prepare, TimeProvider clock, bool blocked,
        Func<bool>? targetCurrent = null, Func<bool>? contextCurrent = null)
    {
        var capture = owner.Execute(() => (Character: CanTeleport() && !blocked && contextCurrent?.Invoke() != false ? Selected : null, SelectionEpoch));
        if (capture.Character is not { } character) return new("invalid-state");
        var prepared = prepare(character);
        return owner.Execute(() =>
        {
            if (!CanTeleport() || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("传送规划期间角色状态已变化");
            if (prepared.Plan is not { } plan) return new TeleportResult(prepared.Reason);
            if (contextCurrent?.Invoke() == false) return new TeleportResult("party-context-changed");
            if (targetCurrent?.Invoke() == false) return new TeleportResult("player-target-changed");
            // 冷资源解析耗时不占用成功后的冷却时长；只平移本次实际启动的那一类。
            long elapsed = Math.Max(0, clock.GetUtcNow().ToUnixTimeMilliseconds() - plan.Now);
            plan = plan with { Cooldowns = new(
                plan.Cooldowns.PotionUntil == character.TeleportCooldowns.PotionUntil ? plan.Cooldowns.PotionUntil : checked(plan.Cooldowns.PotionUntil + elapsed),
                plan.Cooldowns.AuraUntil == character.TeleportCooldowns.AuraUntil ? plan.Cooldowns.AuraUntil : checked(plan.Cooldowns.AuraUntil + elapsed),
                plan.Cooldowns.UpgradeAuraUntil == character.TeleportCooldowns.UpgradeAuraUntil ? plan.Cooldowns.UpgradeAuraUntil : checked(plan.Cooldowns.UpgradeAuraUntil + elapsed),
                plan.Cooldowns.PartyAuraUntil == character.TeleportCooldowns.PartyAuraUntil ? plan.Cooldowns.PartyAuraUntil : checked(plan.Cooldowns.PartyAuraUntil + elapsed)) };
            Selected = repository.CommitTeleport(owner, plan);
            InvalidateImageCommunication();
            InvalidateExpertJobStore();
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(Selected, owner.Generation, SelectionEpoch)
                {
                    PreviousQuests = character.ActiveMainQuests,
                    PreviousSkillSwitchInventory = character.SkillSwitchInventory
                }
            };
            return new TeleportResult("committed", new(new(Selected, owner.Generation, SelectionEpoch), inventory) { AuraKind = plan.AuraKind });
        });
    }

    public bool CanPrecheckTeleport(TeleportService service, bool blocked = false)
    {
        var captured = owner.Execute(() => CanTeleport() && !blocked ? Selected : null);
        if (captured is null) return false;
        long now = service.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var ability = service.Aura(captured, now);
        bool qualified = ability?.Personal == true && captured.TeleportCooldowns.Until(ability.Kind) <= now;
        return owner.Execute(() => qualified && CanTeleport() && ReferenceEquals(Selected, captured));
    }
}
