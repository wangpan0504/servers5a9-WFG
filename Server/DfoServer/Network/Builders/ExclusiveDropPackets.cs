using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

internal static class ExclusiveDropPackets
{
    public static byte[] MonsterDeath(ushort monsterKey, IReadOnlyList<DungeonDropPacketItem> ordinary, int? enteredDungeonId,
        DungeonPoint? bossPositionUpdate = null)
    {
        ExclusiveTableNotice.RejectUnproven(ExclusiveNotice.Death, enteredDungeonId);
        return DungeonPackets.MonsterDeath(monsterKey, ordinary, bossPositionUpdate);
    }

    public static byte[] ClearCard(DungeonClearExperienceReward reward, uint monsterExperience, DungeonCardReward? card,
        int? enteredDungeonId, DungeonCardOptions? options = null, DungeonCardReward? blackDiamond = null,
        uint monsterGrowthContractExperience = 0)
    {
        ExclusiveTableNotice.RejectUnproven(ExclusiveNotice.ClearCard, enteredDungeonId);
        return DungeonSettlementPackets.CardReward(reward, monsterExperience, card, options, blackDiamond, monsterGrowthContractExperience);
    }
}
