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
    long projectedGuildEpoch;
    long guildBuffProjectionVersion;
    readonly HashSet<uint> projectedGuildBuffIds = [];

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
        if (!CanUseStackableItem(expected, expected.Inventory.Find(slot)?.Core.ItemId ?? 0))
            return new(new(19), null, Current);
        GuildMileageGiftPlan plan;
        try { plan = service.PrepareGift(expected, slot); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return new(new(19), null, Current); }
        return owner.Execute(() =>
        {
            if (pendingConsumableUse is not null) return new GuildGiftUseResult(new(19), null, Current, true);
            if (!identity.IsCurrent() || !Current() || !CanUseConsumable()) return new(new(19), null, Current, true);
            if (!CanUseStackableItem(expected, plan.Source.Core.ItemId)) return new(new(19), null, Current);
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

    public GuildBuffProjectionReceipt? PlanGuildBuffProjection(GuildEconomyService service, bool force,
        GuildNpcService? npcs = null)
    {
        try
        {
            return owner.Execute<GuildBuffProjectionReceipt?>(() =>
            {
                if (!Ready || Selected is not { } character) return null;
                long now = service.Now;
                var state = service.Store.ReadGuildEconomy(owner, character.Id, now);
                var desired = state?.Benefits.Where(benefit => benefit.Type is 3 or 5 && benefit.Active(now)).ToArray() ?? [];
                if (state is not null && npcs is not null && CaptureGuildManagement(requireTown: false) is { } actor &&
                    npcs.GuardianBuff(actor) is { } guardian)
                    desired = [.. desired, guardian];
                long? deadline = state?.Benefits.Where(benefit => benefit.ExpiresUnix > now)
                    .Select(benefit => (long?)benefit.ExpiresUnix).Min();
                long epoch = SelectionEpoch;
                bool changed = force || projectedGuildCharacter != character.Id || projectedGuildEpoch != epoch ||
                    !projectedGuildBenefits.SequenceEqual(desired);
                bool sameSelection = projectedGuildCharacter == character.Id && projectedGuildEpoch == epoch;
                if (!sameSelection) projectedGuildBuffIds.Clear();
                // 保留本次选角出现过的资源ID；跳过的旧回执也不能让更早增益残留。
                uint[] removed = changed ? projectedGuildBuffIds.Order().ToArray() : [];
                foreach (var benefit in desired) projectedGuildBuffIds.Add(benefit.Value);
                var added = changed ? desired : [];
                projectedGuildCharacter = character.Id; projectedGuildBenefits = desired;
                projectedGuildEpoch = epoch;
                if (changed) guildBuffProjectionVersion++;
                long version = guildBuffProjectionVersion;
                // 无关角色快照更新不撤销待发增益；真正的增益变化则使旧回执失效。
                bool Current() => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == character.Id &&
                    guildBuffProjectionVersion == version;
                return new GuildBuffProjectionReceipt(removed, added, deadline, Current);
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
