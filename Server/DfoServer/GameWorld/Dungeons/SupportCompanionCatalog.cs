using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 档次与整套外观由配套 AIC 资源定义；服务端只选择真实注册模板，不在通知中伪造技能或属性。
internal sealed record SupportCompanionProfile(int BaseId, string Tier, int TemplateId, string SetName, string Path, int Level);

// 四类配套基础编号；5409人偶和相邻剧情演员不属于专用支援兵。
internal static class SupportCompanionResources
{
    public static bool IsSupportedBase(int id) => id is 20900 or 9986 or 74990 or 19973;
}

internal sealed class SupportCompanionCatalog
{
    public const string ResourcePath = "etc/support_companion_profiles.etc";
    public static SupportCompanionCatalog Empty { get; } = new([]);
    public ImmutableArray<SupportCompanionProfile> Profiles { get; }
    public bool EqualTiers { get; }
    private SupportCompanionCatalog(ImmutableArray<SupportCompanionProfile> profiles, bool equalTiers = false)
        => (Profiles, EqualTiers) = (profiles, equalTiers);

    public static SupportCompanionCatalog Load(PvfArchive archive)
    {
        if (!archive.Contains(ResourcePath)) return Empty;
        var document = new PvfTokenDocument(archive.ReadTokens(ResourcePath), archive.ResolveString);
        if (!document.Diagnostics.IsEmpty || document.Sections.Length is < 1 or > 513)
            throw new InvalidDataException("支援兵档次资源目录不完整或超出容量");
        var equality = document.Sections.Where(section => section.Name == "equal tiers").ToArray();
        if (equality.Length > 1 || equality.Any(section => section.IsBlock || !section.Children.IsEmpty ||
            section.Values.Length != 1 || section.Values[0].Type != 0 || section.Values[0].Value != 1))
            throw new InvalidDataException("支援兵三档统一标记须为单个整数1");
        var registry = DungeonCatalog.ParseDirectory(archive.ReadTokens("aicharacter/aicharacter.lst"), archive.ResolveString, "aicharacter");
        var entries = ImmutableArray.CreateBuilder<SupportCompanionProfile>();
        var used = new HashSet<int>();
        foreach (var section in document.Sections)
        {
            if (section.Name == "equal tiers") continue;
            var row = section.Values;
            if (section.Name != "profile" || !section.IsBlock || !section.Children.IsEmpty || row.Length != 4 ||
                row[0].Type != 0 || row[1].Type != 6 || row[2].Type != 0 || row[3].Type != 6)
                throw new InvalidDataException("支援兵档次资源行须为基础编号、档次、实际模板编号和套装名称");
            int baseId = row[0].Value, templateId = row[2].Value;
            string tier = archive.ResolveString(row[1].Value), setName = archive.ResolveString(row[3].Value);
            if (!SupportCompanionResources.IsSupportedBase(baseId) ||
                tier is not ("entry" or "graduate" or "huge") ||
                baseId == 9986 && tier != "entry" || templateId / 10000 != baseId / 10000 || string.IsNullOrWhiteSpace(setName) || setName.Length > 80 ||
                !used.Add(templateId) || !registry.TryGetValue(baseId, out string? basePath) || !archive.Contains(basePath) ||
                !registry.TryGetValue(templateId, out string? path) || !archive.Contains(path))
                throw new InvalidDataException("支援兵档次资源包含无效、重复或未注册模板");
            var minimum = PvfAiCharacterScript.MinimumInfo(archive.ReadTokens(path), archive.ResolveString);
            var original = PvfAiCharacterScript.MinimumInfo(archive.ReadTokens(basePath), archive.ResolveString);
            if (minimum.Length < 6 || original.Length < 6 || minimum[1].Type != 0 || original[1].Type != 0 ||
                minimum[1].Value != original[1].Value || minimum[2].Type != 0 || original[2].Type != 0 ||
                minimum[2].Value != original[2].Value || minimum[5].Type != 0 || minimum[5].Value is < 1 or > 255)
                throw new InvalidDataException("支援兵档次模板职业或等级与基础模板不匹配");
            entries.Add(new(baseId, tier, templateId, setName, path, minimum[5].Value));
        }
        if (entries.Count is < 1 or > 512)
            throw new InvalidDataException("支援兵模板数超出容量");
        return new(entries.ToImmutable(), equality.Length != 0);
    }

    public bool HasProfile(int baseId, string tier) => SupportCompanionResources.IsSupportedBase(baseId) &&
        Profiles.Any(profile => profile.BaseId == baseId && profile.Tier == tier);
    public SupportCompanionProfile? Pick(int baseId, string tier, Func<int, int> draw)
    {
        if (!HasProfile(baseId, tier)) return null;
        // 兼容解析旧统一标记，但请求档次始终限定真实模板池。
        var candidates = Profiles.Where(profile => profile.BaseId == baseId && profile.Tier == tier).ToArray();
        return candidates.Length == 0 ? null : candidates[draw(candidates.Length)];
    }
}
