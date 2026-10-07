using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

internal static class CharacterSelection
{
    public static Character Prepare(ICharacterRepository repository, AccountOwners.Lease owner, Character character,
        TownCatalog towns, IReadOnlySet<int> completed, GoldLimitCatalog? goldLimits) => owner.Execute(() =>
    {
        var back = towns.ResolveRoomReturn(character.Position, character.Return, character.Level, completed);
        if (character.Position != towns.Room || character.Return != back)
            character = repository.Save(owner, character, towns.Room, back);
        // 每一步使用上一笔实际提交的快照；无变化不写库，后续失败不重放已提交的清理。
        character = InventoryService.Commit(repository, owner, character,
            InventoryExpiryService.Plan(character.Inventory, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (goldLimits is not null)
            character = InventoryService.Commit(repository, owner, character, new CurrencyService(goldLimits).Initialize(character));
        return character;
    });
}
