using DfoServer.Game.Settings;
using DfoServer.Game.Dungeons;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static object SupportCompanionState(GameDebugSettings settings)
    {
        // 名单与兼容档次字段从同一原子快照派生，避免并发保存时混用配置。
        var snapshot = settings.SupportConfiguration;
        int mask = snapshot.SelectionMask;
        return new
        {
            supportCompanion = GameDebugSettings.SupportCompanionName(GameDebugSettings.LegacySupportCompanion(mask)),
            supportCompanions = GameDebugSettings.SupportCompanionNames(mask),
            supportedCompanions = new[] { "wangfugui", "guangbing" },
            unavailableCompanions = new[]
            {
                new { companion = "female-crusader", reason = "当前客户端不支持该支援兵" },
                new { companion = "enchantress", reason = "当前客户端不支持该支援兵" }
            },
            supportCompanionTiers = new Dictionary<string, string>
            {
                ["wangfugui"] = snapshot.WangFuguiTier,
                ["female-crusader"] = snapshot.FemaleCrusaderTier,
                ["enchantress"] = snapshot.EnchantressTier
            },
            supportAuxiliary = GameDebugSettings.SupportCompanionName(GameDebugSettings.LegacySupportCompanion(mask & 13)),
            supportSynergies = GameDebugSettings.SupportCompanionNames(mask & 2),
            supportAuxiliaryLimit = 1,
            supportSynergyLimit = 2,
            summonWangFugui = GameDebugSettings.HasCompanion(mask, SupportCompanionKind.WangFugui),
            summonGuangbing = GameDebugSettings.HasCompanion(mask, SupportCompanionKind.Guangbing),
            summonFemaleCrusader = GameDebugSettings.HasCompanion(mask, SupportCompanionKind.FemaleCrusader),
            summonEnchantress = GameDebugSettings.HasCompanion(mask, SupportCompanionKind.Enchantress),
            profiles = settings.SupportProfiles.Profiles.Select(profile => new
            {
                companion = DungeonCompanion.NameForBaseId(profile.BaseId), tier = profile.Tier,
                templateId = profile.TemplateId, setName = profile.SetName, path = profile.Path, level = profile.Level
            }).ToArray()
        };
    }
}
