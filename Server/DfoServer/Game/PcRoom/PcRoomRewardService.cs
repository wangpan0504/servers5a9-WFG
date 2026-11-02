using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.PcRoom;

namespace DfoServer.Game.PcRoom;

internal sealed class PcRoomRewardService(PcRoomCatalog catalog, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
{
    public PcRoomCatalog Catalog { get; } = catalog;

    public InventoryResult PlanClaim(Character character, bool rental, DateTimeOffset now)
    {
        if (!rental)
            return InventoryRewardGrantService.TryGrantBatch(character,
                [new(Catalog.Premium.LoginItemId, Catalog.Premium.LoginCount)], items, goldLimits, pieces, now);
        var template = items.Get(Catalog.Premium.RentalItemId);
        if (template.Kind != ItemKind.Consumable) return InventoryResult.Reject(19);
        var item = new InventoryItem(0, default, RentalCore(template, now));
        return InventoryRewardGrantService.TryGrantItems(character, [item], items, goldLimits, pieces, now);
    }

    public InventoryResult PlanWeapons(Character character, IReadOnlyList<byte> candidateIndexes, DateTimeOffset now)
    {
        var choices = Catalog.Weapons(character.Job, character.Level);
        if (candidateIndexes.Count == 0 || candidateIndexes.Count > choices.Length ||
            candidateIndexes.Distinct().Count() != candidateIndexes.Count || candidateIndexes.Any(index => index >= choices.Length))
            return InventoryResult.Reject(1) with { RejectReason = "pcroom-candidate-invalid" };
        var rented = new List<InventoryItem>();
        foreach (byte index in candidateIndexes)
        {
            var choice = choices[index];
            if (character.Inventory.Items.Any(item => item.Core.ItemId == choice.ItemId && item.Core.IsRental &&
                !item.IsExpired(now.ToUnixTimeSeconds())))
                return InventoryResult.Reject(1) with { RejectReason = "pcroom-already-rented" };
            var template = items.Get(choice.ItemId);
            // 租赁按资源的职业/等级档开放；低档可租到高于当前穿戴等级的武器，穿戴时再核对原等级规则。
            if (template.Kind != ItemKind.Equipment || template.EquipmentSlot != 12 || template.UseForbidden ||
                character.Job >= 16 || (template.Jobs & (1 << character.Job)) == 0)
                return InventoryResult.Reject(1) with { RejectReason = "pcroom-weapon-ineligible" };
            rented.Add(new(0, default, RentalCore(template, now, choice.Upgrade, choice.Refinement)));
        }
        return InventoryRewardGrantService.TryGrantItems(character, rented, items, goldLimits, pieces, now);
    }

    public static int Deadline(DateTimeOffset now) => checked((int)Math.Min(ItemExpiration.MaximumUnixTime,
        checked(now.ToUnixTimeSeconds() + 86400)));

    static ItemCore RentalCore(ItemTemplate template, DateTimeOffset now, byte upgrade = 0, byte refinement = 0) =>
        InventoryRewardGrantService.Create(template, utcNow: now, upgrade: upgrade, refinement: refinement,
            ignoreUsablePeriod: true) with { IsRental = true, ExpireTime = Deadline(now) };
}
