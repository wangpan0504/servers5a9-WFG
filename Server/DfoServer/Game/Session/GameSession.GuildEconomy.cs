using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record GuildGiftUseResult(GuildEconomyCommit Commit, InventoryResult? Inventory,
    Func<bool>? IsCurrent, bool CloseConnection = false);
internal sealed record GuildBuffProjectionReceipt(uint[] Removed, GuildBenefit[] Added, long? Deadline, Func<bool> IsCurrent);

internal sealed partial class GameSession
{
    GuildBenefit[] projectedGuildBenefits = [];
    ushort projectedGuildCharacter;

    public GuildGiftUseResult UseGuildMileageGift(InventorySlot slot, GuildEconomyService service,
        string requestId, bool blocked = false)
    {
        var capture = owner.Execute(() => (Identity: CaptureGuildManagement(), Character: Selected, Guard: UseGuard(),
            Pending: pendingConsumableUse is not null, Allowed: CanUseConsumable()));
        bool Current() => CanPublishConsumable(capture.Guard);
        if (capture.Pending) return new(new(19), null, Current, true);
        if (blocked || !capture.Allowed || capture.Identity is not { } identity || capture.Character is not { } expected)
            return new(new(19), null, Current);
        var replay = service.Store.ReplayGuildMileageGift(identity, requestId, slot.Index, service.Now);
        if (replay is not null) return new(replay, null, Current);
        GuildMileageGiftPlan plan;
        try { plan = service.PrepareGift(expected, slot); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return new(new(19), null, Current); }
        return owner.Execute(() =>
        {
            if (pendingConsumableUse is not null) return new GuildGiftUseResult(new(19), null, Current, true);
            if (!identity.IsCurrent() || !Current() || !CanUseConsumable()) return new(new(19), null, Current, true);
            pendingConsumableUse = plan.Inventory;
            var commit = service.Store.CommitGuildMileageGift(identity, plan, requestId, service.Now);
            pendingConsumableUse = null;
            if (!commit.Success || commit.Replayed || commit.Character is null) return new(commit, null, Current);
            Selected = commit.Character;
            var guard = UseGuard();
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(commit.Character, owner.Generation, SelectionEpoch)
                { Phase = Phase, PreviousQuests = expected.ActiveMainQuests, PreviousSkillSwitchInventory = expected.SkillSwitchInventory }
            };
            return new(commit, inventory, () => CanPublishConsumable(guard));
        });
    }

    public GuildBuffProjectionReceipt? PlanGuildBuffProjection(GuildEconomyService service, bool force)
    {
        try
        {
            return owner.Execute<GuildBuffProjectionReceipt?>(() =>
            {
                if (!Ready || Selected is not { } character) return null;
                long now = service.Now;
                var state = service.Store.ReadGuildEconomy(owner, character.Id, now);
                var desired = state?.Benefits.Where(benefit => benefit.Type is 3 or 5 && benefit.Active(now)).ToArray() ?? [];
                long? deadline = state?.Benefits.Where(benefit => benefit.ExpiresUnix > now)
                    .Select(benefit => (long?)benefit.ExpiresUnix).Min();
                bool changed = force || projectedGuildCharacter != character.Id || !projectedGuildBenefits.SequenceEqual(desired);
                uint[] removed = changed ? projectedGuildBenefits.Select(benefit => benefit.Value).ToArray() : [];
                var added = changed ? desired : [];
                projectedGuildCharacter = character.Id; projectedGuildBenefits = desired;
                long epoch = SelectionEpoch;
                bool Current() => owner.IsCurrent && Ready && SelectionEpoch == epoch && ReferenceEquals(Selected, character);
                return new GuildBuffProjectionReceipt(removed, added, deadline, Current);
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
