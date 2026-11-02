using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Characters;

internal sealed class CharacterAntiEvil(ItemCatalog items, AntiEvilUpgradeCatalog upgrades,
    RecoveryConsumableCatalog consumables, TimeProvider? timeProvider = null,
    PremiumContractCatalog? contracts = null, AntiEvilSetCatalog? sets = null)
{
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public int Compute(Character character)
    {
        var now = clock.GetUtcNow();
        long seconds = now.ToUnixTimeSeconds();
        int total = 0;
        var setPieces = new Dictionary<int, int>();
        foreach (var item in character.Inventory.Items)
        {
            // 皮肤槽只提供外观，不能把同一宠物或光环属性再次合入。
            if (item.Position.List != InventoryListType.Equipment || item.Position.Index is 11 or 33)
                continue;
            var template = items.Get(item.Core.ItemId);
            if (item.Core.Kind != template.Kind ||
                !InventoryMoveService.Allows(character, item.Position, item.Core, items, seconds, contracts) ||
                template.Durability != 0 && item.Core.Durability == 0 ||
                item.ExpireTime > 0 && item.ExpireTime < seconds && !template.UsableExpired)
                continue;
            int upgrade = 0;
            if (item.Core.Upgrade != 0 && !AntiEvilUpgradeContribution.TryCalculateForEquipment(
                item.Core.AmplifyType != 0 ? upgrades.Amplify : upgrades.Reinforce,
                template.Rarity, item.Position.Index, item.Core.Upgrade, out upgrade))
                throw new InvalidDataException("装备抗魔升级贡献无效");
            int forge = item.Position.Index == 12
                ? AntiEvilUpgradeContribution.ForgeValue(upgrades.Forge, template.Rarity, item.Core.GenuineUpgrade) : 0;
            total = checked(total + AntiEvilUpgradeContribution.EquipmentValue(template.AntiEvil, upgrade, forge));
            foreach (int setId in template.PartSetIds.Where(id => id > 0).Distinct())
                setPieces[setId] = setPieces.GetValueOrDefault(setId) + 1;
        }
        foreach (var (setId, count) in setPieces)
        {
            if (sets is null) throw new InvalidDataException("角色抗魔缺少共享套装目录");
            foreach (var tier in sets.Get(setId))
                if (count >= tier.RequiredPieces) total = checked(total + tier.AntiEvil);
        }
        int consumable = AntiEvilUpgradeContribution.ConsumableFlatValue(character.Inventory.ConsumableStates,
            now.ToUnixTimeMilliseconds(), id => consumables.TryGet(id, out var definition) ? definition : null);
        return AntiEvilUpgradeContribution.TotalValue([total, consumable], []);
    }
}
