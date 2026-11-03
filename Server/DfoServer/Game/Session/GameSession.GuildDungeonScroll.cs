using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record GuildDungeonScrollUseResult(GuildDungeonScrollCommit Commit, InventoryResult? Inventory,
    Func<bool>? IsCurrent, bool CloseConnection = false);

internal sealed partial class GameSession
{
    public GuildDungeonScrollUseResult UseGuildDungeonScroll(InventorySlot slot, GuildDungeonScrollService service,
        string requestId, bool blocked = false)
    {
        var capture = owner.Execute(() => (Identity: CaptureGuildManagement(), Character: Selected, Guard: UseGuard(),
            Pending: pendingConsumableUse is not null, Allowed: CanUseConsumable()));
        bool Current() => CanPublishConsumable(capture.Guard);
        if (capture.Pending) return new(new(19), null, Current, true);
        if (blocked || !capture.Allowed || capture.Identity is not { } identity || capture.Character is not { } expected)
            return new(new(19), null, Current);
        var replay = service.Store.ReplayGuildDungeonScroll(identity, requestId, slot.Index, service.Now);
        if (replay is not null) return new(replay, null, Current);
        if (!CanUseStackableItem(expected, expected.Inventory.Find(slot)?.Core.ItemId ?? 0))
            return new(new(19), null, Current);
        GuildDungeonScrollPlan plan;
        try { plan = service.Prepare(identity, expected, slot); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return new(new(19), null, Current); }
        return owner.Execute(() =>
        {
            if (pendingConsumableUse is not null) return new GuildDungeonScrollUseResult(new(19), null, Current, true);
            if (!identity.IsCurrent() || !Current() || !CanUseConsumable()) return new(new(19), null, Current, true);
            if (!CanUseStackableItem(expected, plan.Source.Core.ItemId)) return new(new(19), null, Current);
            pendingConsumableUse = plan.Inventory;
            GuildDungeonScrollCommit commit;
            try { commit = service.Commit(identity, plan, requestId); }
            finally { pendingConsumableUse = null; }
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
}
