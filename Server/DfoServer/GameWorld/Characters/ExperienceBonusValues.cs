namespace DfoServer.GameWorld.Characters;

// 资源奖励与领域结算共用的不可变数值；不包含会话、资产或资格判定。
// 枚举声明不表示生产业务已接入，也不是客户端字段编号；显示映射及未实现系统见角色成长文档。
// 当前ARAD的NOTI37没有旧端member_bonus_exp师徒槽，不得借用Party、Buff或未命名字段。
internal enum ExperienceBonusSource
{
    GrowthContract, Potion, QuestPotion, BlackDiamond, InternetCafe, Equipment, Party, Avatar,
    Creature, Adventure, Guild, FatigueBurning, FatigueBuff, SeriaBlessing, SeriaWelcome, FastGrowth, Event, Charm, ReturningPlayer, Channel, Buff
}
internal sealed record ExperienceBonusPart(ExperienceBonusSource Source, uint Amount);
