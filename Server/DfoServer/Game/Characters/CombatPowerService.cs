using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

// 共享只读目录；按冻结快照计算官方风格战斗力，不持有角色、不访问网络/数据库。
internal sealed class CombatPowerService(ItemCatalog items, CharacterInvestmentMetadataCatalog metadata,
    CharacterStatComputer? stats)
{
    public CombatPowerResult Calculate(CharacterInvestmentSnapshot snapshot)
    {
        CharacterStats? level = null;
        try { level = stats?.ComputeLevelBaseStats(snapshot.Job, snapshot.GrowType, snapshot.Level); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentOutOfRangeException or OverflowException)
        { level = null; }
        return CombatPowerScore.Calculate(snapshot, level, items.Get, metadata.ReadCombatEffects,
            metadata.ReadCombatEnchant, metadata.ReadCombatSetId, metadata.ReadCombatSetTiers);
    }
}
