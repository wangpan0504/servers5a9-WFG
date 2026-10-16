using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using PvfLib;

namespace DfoServer.Testing;

// 自测共享的只读目录缓存：同一 PvfArchive 实例只解析一次，避免各用例反复从完整 PVF 构建相同目录。
// 目录对象只读；需要改动定义的用例应先复制（如 ToDictionary/with）再构建自己的目录，不得改动共享实例。
// 以 archive 实例为键弱引用保存，合成或临时 archive 释放后缓存随之回收。
internal static class SelfTestCatalogs
{
    sealed class Cache
    {
        readonly ConcurrentDictionary<object, Lazy<object>> entries = new();

        public T Get<T>(object key, Func<T> create) where T : class =>
            (T)entries.GetOrAdd(key, _ => new Lazy<object>(() => create(), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    // 以引用区分调用方传入的技能学习目录，合成目录不会与真实目录混用。
    sealed record GrowthKey(SkillLearningCatalog? Learning)
    {
        public bool Equals(GrowthKey? other) => other is not null && ReferenceEquals(Learning, other.Learning);
        public override int GetHashCode() => Learning is null ? 0 : RuntimeHelpers.GetHashCode(Learning);
    }

    static readonly ConditionalWeakTable<PvfArchive, Cache> caches = new();

    static Cache For(PvfArchive archive) => caches.GetValue(archive, _ => new Cache());

    public static QuestCatalog Quests(PvfArchive archive) => For(archive).Get(typeof(QuestCatalog), () => QuestCatalog.Load(archive));
    public static QuestConnectionCatalog QuestConnections(PvfArchive archive) => For(archive).Get(typeof(QuestConnectionCatalog), () => QuestConnectionCatalog.Load(archive));
    public static QuestRewardParameterCatalog QuestRewardParameters(PvfArchive archive) => For(archive).Get(typeof(QuestRewardParameterCatalog), () => QuestRewardParameterCatalog.Load(archive));
    public static DungeonCatalog Dungeons(PvfArchive archive) => For(archive).Get(typeof(DungeonCatalog), () => DungeonCatalog.Load(archive));
    public static DungeonRoomCatalog Rooms(PvfArchive archive) => For(archive).Get(typeof(DungeonRoomCatalog), () => DungeonRoomCatalog.Load(archive));
    public static DungeonKillExperienceCatalog KillExperience(PvfArchive archive) => For(archive).Get(typeof(DungeonKillExperienceCatalog), () => DungeonKillExperienceCatalog.Load(archive));
    public static DungeonClearExperienceCatalog ClearExperience(PvfArchive archive) => For(archive).Get(typeof(DungeonClearExperienceCatalog), () => DungeonClearExperienceCatalog.Load(archive));
    public static DungeonMonsterDropCatalog MonsterDrops(PvfArchive archive) => For(archive).Get(typeof(DungeonMonsterDropCatalog), () => DungeonMonsterDropCatalog.Load(archive));
    public static DungeonDropRuleCatalog DropRules(PvfArchive archive) => For(archive).Get(typeof(DungeonDropRuleCatalog), () => DungeonDropRuleCatalog.Load(archive));
    public static DungeonCardPriceCatalog CardPrices(PvfArchive archive) => For(archive).Get(typeof(DungeonCardPriceCatalog), () => DungeonCardPriceCatalog.Load(archive));

    // 与 SkillLearningCatalog.Load(pvf, PremiumContractCatalog.Load(pvf).ExpertSkillLevels) 等价；契约目录本身不共享（含检查状态）。
    public static SkillLearningCatalog SkillLearning(PvfArchive archive) => For(archive).Get(typeof(SkillLearningCatalog),
        () => SkillLearningCatalog.Load(archive, DfoServer.GameWorld.Items.PremiumContractCatalog.Load(archive).ExpertSkillLevels));

    public static CharacterGrowthCatalog Growth(PvfArchive archive, SkillLearningCatalog? learning = null) =>
        For(archive).Get(new GrowthKey(learning), () => CharacterGrowthCatalog.Load(archive, learning));
}
