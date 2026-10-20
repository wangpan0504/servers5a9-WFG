using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

internal static class CharacterSelection
{
    public static Character Prepare(ICharacterRepository repository, AccountOwners.Lease owner, Character character,
        TownCatalog towns, IReadOnlySet<int> completed, GoldLimitCatalog? goldLimits, bool resumeTown = false,
        ItemCatalog? items = null, TimeProvider? clock = null) => owner.Execute(() =>
    {
        if (resumeTown)
        {
            // 换频道保留已经提交的城镇快照，不借重连传送到新区域，也不覆盖原返回地区。
            if (!towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area)
                || area.Map(completed).Error is not null)
                throw new InvalidDataException("频道重连的城镇地图不可用");
        }
        else
        {
            var back = towns.ResolveRoomReturn(character.Position, character.Return, character.Level, completed);
            if (character.Position != towns.Room || character.Return != back)
                character = repository.Save(owner, character, towns.Room, back);
        }
        // 每一步使用上一笔实际提交的快照；无变化不写库，后续失败不重放已提交的清理。
        character = InventoryService.Commit(repository, owner, character,
            InventoryExpiryService.RepairZeroPeriod(character.Inventory, items));
        character = InventoryService.Commit(repository, owner, character,
            InventoryExpiryService.Plan(character.Inventory, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (!character.Inventory.ConsumableStates.IsEmpty)
        {
            var restored = ConsumableStatePolicy.CrossBoundary(character.Inventory,
                DfoServer.Game.Inventory.Models.ConsumableMaintenance.Reselect, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
            if (restored != character.Inventory)
                character = InventoryService.Commit(repository, owner, character, new(true, 0,
                    new(character.Inventory, restored, "consumable_reselect", new(character))));
        }
        if (goldLimits is not null)
            character = InventoryService.Commit(repository, owner, character, new CurrencyService(goldLimits).Initialize(character));
        return character;
    });
}
