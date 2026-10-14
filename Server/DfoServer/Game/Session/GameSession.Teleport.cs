using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    bool CanTeleport() => pendingRental is null && Ready && Phase == SessionPhase.Town && Selected is not null && !HasPendingQuestScene &&
        pendingQuestCompletion is null && !HasPendingFormalCard;

    public TeleportResult TeleportPotion(PotionTeleportRequest request, TeleportService service, bool blocked = false) =>
        PlanTeleport(character => service.Potion(character, request, service.Clock.GetUtcNow().ToUnixTimeMilliseconds()), service.Clock, blocked);

    public TeleportResult TeleportSolo(SoloTeleportRequest request, TeleportService service, TeleportTarget? target,
        Func<bool>? targetCurrent = null, bool blocked = false) => PlanTeleport(character =>
        service.Solo(character, request, target, service.Clock.GetUtcNow().ToUnixTimeMilliseconds()), service.Clock, blocked, targetCurrent);

    TeleportResult PlanTeleport(Func<Character, (string Reason, TeleportPlan? Plan)> prepare, TimeProvider clock, bool blocked, Func<bool>? targetCurrent = null)
    {
        var capture = owner.Execute(() => (Character: CanTeleport() && !blocked ? Selected : null, SelectionEpoch));
        if (capture.Character is not { } character) return new("invalid-state");
        var prepared = prepare(character);
        return owner.Execute(() =>
        {
            if (!CanTeleport() || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("传送规划期间角色状态已变化");
            if (prepared.Plan is not { } plan) return new TeleportResult(prepared.Reason);
            if (targetCurrent?.Invoke() == false) return new TeleportResult("player-target-changed");
            // 冷资源解析耗时不占用成功后的冷却时长；只平移本次实际启动的那一类。
            long elapsed = Math.Max(0, clock.GetUtcNow().ToUnixTimeMilliseconds() - plan.Now);
            plan = plan with { Cooldowns = new(
                plan.Cooldowns.PotionUntil == character.TeleportCooldowns.PotionUntil ? plan.Cooldowns.PotionUntil : checked(plan.Cooldowns.PotionUntil + elapsed),
                plan.Cooldowns.AuraUntil == character.TeleportCooldowns.AuraUntil ? plan.Cooldowns.AuraUntil : checked(plan.Cooldowns.AuraUntil + elapsed)) };
            Selected = repository.CommitTeleport(owner, plan);
            InvalidateImageCommunication();
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(Selected, owner.Generation, SelectionEpoch) { PreviousQuests = character.ActiveMainQuests }
            };
            return new TeleportResult("committed", new(new(Selected, owner.Generation, SelectionEpoch), inventory));
        });
    }

    public bool CanPrecheckTeleport(TeleportService service, bool blocked = false)
    {
        var captured = owner.Execute(() => CanTeleport() && !blocked ? Selected : null);
        if (captured is null) return false;
        long now = service.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        bool qualified = service.Aura(captured, now) is not null && captured.TeleportCooldowns.AuraUntil <= now;
        return owner.Execute(() => qualified && CanTeleport() && ReferenceEquals(Selected, captured));
    }
}
