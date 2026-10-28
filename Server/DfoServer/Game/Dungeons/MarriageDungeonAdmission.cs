using DfoServer.Game.Marriage;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 只绑定本版夫妻副本，关系版本随双方入场费用一起在组队事务内复验。
internal sealed record MarriageDungeonAdmission(Guid Relation, long Revision, ushort FirstId, ushort SecondId,
    DungeonRequiredItemRow Ticket)
{
    public const uint DungeonId = 3541;

    public static bool Applies(uint dungeon) => dungeon == DungeonId;

    public static bool ResourceMatches(DungeonDefinition definition) => definition.IsWeddingCouple &&
        definition.MinimumLevel is > 0 && definition.DisableFatigueConsumption && definition.DisableClearReward &&
        definition.DisableMonsterAndClearExperience && !definition.AllowsPractice &&
        definition.RequiredItemRows is [{ Count: > 0, Third: 1 }] && definition.HasWeddingCouplePartyLimit &&
        definition.TryGetDesignatedDifficulty(out _);

    public static MarriageDungeonAdmission? Prepare(DungeonDefinition definition, MarriageRecord? record,
        IReadOnlyList<ushort> members, uint quest, byte difficulty, bool specialMode)
    {
        if (!ResourceMatches(definition) || record is not { State: 4 } || members.Count != 2 ||
            members[0] == members[1] || !members.All(record.IsMember) || quest != 0 || specialMode ||
            !definition.TryGetDesignatedDifficulty(out byte designated) || difficulty != designated)
            return null;
        return new(record.Id, record.Revision, record.FirstId, record.SecondId, definition.RequiredItemRows[0]);
    }

    public bool Matches(MarriageRecord? record, IReadOnlyList<ushort> members) =>
        record is { State: 4 } && record.Id == Relation && record.Revision == Revision &&
        record.FirstId == FirstId && record.SecondId == SecondId && members.Count == 2 &&
        members[0] != members[1] && members.All(record.IsMember);

    public bool HasTicketPayment(InventoryPlan? plan)
    {
        if (plan is null) return false;
        long Count(InventorySnapshot inventory) => inventory.Items.Where(item =>
            item.Position.List == InventoryListType.Main && item.Core.ItemId == Ticket.ItemId).Sum(item => (long)item.Core.Count);
        return Count(plan.Before) - Count(plan.After) == Ticket.Count;
    }
}
