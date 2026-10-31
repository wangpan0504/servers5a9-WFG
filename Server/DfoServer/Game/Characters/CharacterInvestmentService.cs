using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

// 显式共享只读领域目录；仅按当前冻结穿戴来源查询，不持有角色或装配集合。
internal sealed class CharacterInvestmentService(ItemCatalog items, CharacterInvestmentMetadataCatalog metadata,
    CollectBoxCatalog boxes, RandomOptionCatalog random)
{
    public long? BoxExpiration(CharacterInvestmentSnapshot snapshot) => snapshot.CollectBox.SelectedBox == 0 ? null :
        boxes.Get(snapshot.CollectBox.SelectedBox).ResolveExpiration(snapshot.CollectBox.Expiration(snapshot.CollectBox.SelectedBox));

    public CharacterInvestmentResult Calculate(CharacterInvestmentSnapshot snapshot)
    {
        return CharacterInvestmentScore.Calculate(snapshot, items.Get,
            (card, index, kind, slot) =>
            {
                var definition = metadata.ReadEnchantment(card, index);
                return definition.ValidIndex && definition.Targets.Contains((kind, slot));
            }, metadata.ValidateRandomTitle, metadata.ReadSet, items.Talisman, items.Rune,
            boxes.Get, (item, type) =>
            {
                var original = random.Resolve(item);
                if (original?.Pools.Any(pool => pool.Groups.Any(group => group.Any(option => option.Type == type && option.Weight > 0))) == true)
                    return true;
                var changed = random.ResolveChange(item);
                return changed?.Types.Any(row => row.Weight > 0 && row.Groups.Any(group => group.Weight > 0 &&
                    group.Options.Any(option => option.Type == type && option.Weight > 0))) == true;
            }, option => metadata.ReadInstalledChronicle(option.Id, option.Job, option.GrowType, option.EquipmentType, option.Number), metadata.ReadDimensionEnergy, includeQualitySeedTier: true, includeAllWorn: true);
    }

}
