namespace DfoServer.GameWorld.Characters;

// 资源奖励与领域结算共用的不可变数值；不包含会话、资产或资格判定。
internal enum ExperienceBonusSource
{
    GrowthContract, Potion, QuestPotion, BlackDiamond, InternetCafe, Equipment, Party, Avatar,
    Creature, Adventure, Guild, FatigueBurning, FatigueBuff, SeriaBlessing, SeriaWelcome, FastGrowth, Event, Charm, ReturningPlayer, Channel, Buff
}
internal sealed record ExperienceBonusPart(ExperienceBonusSource Source, uint Amount);
