using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

internal sealed record RaidDungeonResource(int Id, string? FormalPath, string? TutorialPath,
    bool FormalFileExists, bool TutorialFileExists, string? ReferencePath, ImmutableArray<int> MapIds);
internal sealed record RaidResources(RaidScript Script, ImmutableArray<RaidDungeonResource> Dungeons,
    ImmutableArray<int> MissingFormalDungeons, ImmutableArray<int> MissingFormalMaps, ImmutableArray<int> MissingItems,
    ImmutableArray<RaidIssue> Issues)
{
    public RaidDungeonCatalog? EntryCatalog { get; init; }
    public LukeRaidObjectCatalog? LukeObjects { get; init; }
    public LukeCardSetCatalog? LukeCardSet { get; init; }
    public FiendwarScript? Fiendwar { get; init; }
    public RaidResources? FiendwarGuide { get; init; }
    public RaidResources? PreyTeam { get; init; }
    public bool ResourcesReady => Issues.IsEmpty && MissingFormalDungeons.IsEmpty && MissingFormalMaps.IsEmpty && MissingItems.IsEmpty;
    // 普通普雷开战检查业务声明及计费/奖励物品；地图缺项保留诊断，由入图目录逐目标拒绝。
    public bool PreyTeamStartReady => Script.Issues.IsEmpty && MissingItems.IsEmpty && EntryCatalog is { Type: 3 };
    // 即使资源完整，计费、候战室迁移及阶段权威输入仍未闭合，不能把此结果当作开战许可。
    public string StartBlocker => !Issues.IsEmpty ? "formal-raid-definition-unsupported" :
        !MissingFormalDungeons.IsEmpty ? "formal-raid-dungeon-index-missing" :
        !MissingFormalMaps.IsEmpty ? "formal-raid-map-index-missing" :
        !MissingItems.IsEmpty ? "formal-raid-item-resource-missing" : "formal-raid-flow-unavailable";
}

internal static class RaidResourceLoader
{
    internal const string TutorialListPath = "dungeon/tutorial/dungeon.lst";

    public static RaidResources Load(PvfArchive pvf, RaidDefinition raid, DungeonCatalog dungeons, ItemCatalog items,
        Func<int, string?>? mapPath = null, IReadOnlyDictionary<int, string>? supplementalMaps = null)
        => LoadMode(pvf, raid, dungeons, items, mapPath, supplementalMaps, false);

    static RaidResources LoadMode(PvfArchive pvf, RaidDefinition raid, DungeonCatalog dungeons, ItemCatalog items,
        Func<int, string?>? mapPath, IReadOnlyDictionary<int, string>? supplementalMaps, bool guide, bool preyTeam = false)
    {
        mapPath ??= dungeons.MapPath;
        var fiendwar = raid.Type == 2 ? guide
            ? FiendwarScript.ParseGuide(raid.Path, pvf.ReadTokens(raid.Path),
                pvf.Contains(raid.Path + ".symbol") ? pvf.ReadTokens(raid.Path + ".symbol") : [], pvf.ResolveString)
            : FiendwarScript.Parse(raid.Path, pvf.ReadTokens(raid.Path),
                pvf.Contains(raid.Path + ".symbol") ? pvf.ReadTokens(raid.Path + ".symbol") : [], pvf.ResolveString) : null;
        var script = fiendwar?.Script ?? (raid.Type == 3 ? preyTeam
            ? PreyTeamScript.Parse(pvf.ReadTokens(raid.Path), pvf.ReadTokens(raid.Path + ".symbol"), pvf.ResolveString)
            : PreyGuideScript.Parse(pvf.ReadTokens(raid.Path), pvf.ReadTokens(raid.Path + ".symbol"), pvf.ResolveString) :
            RaidScriptParser.Parse(raid.Path, pvf.ReadTokens(raid.Path),
            pvf.Contains(raid.Path + ".symbol") ? pvf.ReadTokens(raid.Path + ".symbol") : [], pvf.ResolveString,
            raid.Type == 1 ? RaidScriptKind.Luke : RaidScriptKind.Anton));
        IReadOnlyDictionary<int, string> tutorial = ImmutableDictionary<int, string>.Empty;
        List<RaidIssue> issues = [];
        try
        {
            // 卢克只核对正式目录，不能因安徒恩备用表缺失而拒绝其资源。
            if (raid.Type == 0 && pvf.Contains(TutorialListPath))
                tutorial = DungeonCatalog.ParseDirectory(pvf.ReadTokens(TutorialListPath), pvf.ResolveString, "dungeon");
            else if (raid.Type == 0) issues.Add(new("tutorial-list-missing", TutorialListPath, 0, "candidate list unavailable"));
        }
        catch (InvalidDataException error) { issues.Add(new("tutorial-list-invalid", TutorialListPath, 0, error.Message)); }
        var result = Check(script, tutorial, dungeons.DungeonPath, mapPath, pvf.Contains,
            pvf.ReadTokens, pvf.ResolveString, ItemExists);
        result = result with { Issues = result.Issues.AddRange(issues), Fiendwar = fiendwar };
        if (raid.Type is 0 or 1 or 2 or 3)
            result = result with { EntryCatalog = RaidDungeonCatalog.Load(pvf, result, mapPath, raid.Type, supplementalMaps, preyTeam) };
        if (raid.Type == 3 && !preyTeam)
            return result with { PreyTeam = LoadMode(pvf, raid, dungeons, items, mapPath, supplementalMaps, false, true) };
        return raid.Type == 2 && !guide
            ? result with { FiendwarGuide = LoadMode(pvf, raid, dungeons, items, mapPath, supplementalMaps, true) } : result;

        bool ItemExists(int id)
        {
            if (!items.Contains(id)) return false;
            try { return items.Get(id).Id == id; }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException) { return false; }
        }
    }

    // 所有查询在装配阶段完成；回调只借用共享目录，不合并或补写正式索引。
    internal static RaidResources Check(RaidScript script, IReadOnlyDictionary<int, string> tutorial,
        Func<int, string?> formalDungeon, Func<int, string?> formalMap, Func<string, bool> exists,
        Func<string, IReadOnlyList<PvfToken>> read, Func<int, string> resolve, Func<int, bool> itemExists)
    {
        List<RaidIssue> issues = [.. script.Issues];
        List<RaidDungeonResource> resources = [];
        List<int> missingDungeons = [];
        var missingMaps = new SortedSet<int>();
        foreach (int id in script.Phases.SelectMany(p => p.Dungeons).Select(d => d.Id).Distinct())
        {
            string? formal = formalDungeon(id), candidate = tutorial.GetValueOrDefault(id);
            bool formalExists = formal is not null && exists(formal), candidateExists = candidate is not null && exists(candidate);
            if (formal is null) missingDungeons.Add(id);
            else if (!formalExists) issues.Add(new("formal-dungeon-file-missing", formal, 0, id.ToString()));
            if (candidate is not null && !candidateExists) issues.Add(new("tutorial-dungeon-file-missing", candidate, 0, id.ToString()));
            // 主表与备用表各自保留来源；备用表只用于指出原脚本的显式引用。
            string? source = formalExists ? formal : candidateExists ? candidate : null;
            ImmutableArray<int> mapIds = [];
            if (source is null) issues.Add(new("dungeon-reference-script-missing", script.Path, 0, id.ToString()));
            else
            {
                mapIds = MapReferences(source, read(source), resolve, issues);
                foreach (int mapId in mapIds)
                {
                    string? path = formalMap(mapId);
                    if (path is null) missingMaps.Add(mapId);
                    else if (!exists(path)) issues.Add(new("formal-map-file-missing", path, 0, mapId.ToString()));
                }
            }
            resources.Add(new(id, formal, candidate, formalExists, candidateExists, source, mapIds));
        }
        foreach (int mapId in script.Phases.SelectMany(p => p.Rules).SelectMany(r => r.Trigger.Concat(r.Behavior))
            .Select(c => c.MapId).OfType<int>().Distinct())
        {
            string? path = formalMap(mapId);
            if (path is null) missingMaps.Add(mapId);
            else if (!exists(path)) issues.Add(new("formal-map-file-missing", path, 0, mapId.ToString()));
        }
        var itemIds = script.Phases.SelectMany(p => p.Rewards).Select(r => r.ItemId)
            .Concat(new[] { script.EntryCost?.ItemId, script.RetryItem?.ItemId }.OfType<int>()).Distinct();
        return new(script, resources.ToImmutableArray(), missingDungeons.ToImmutableArray(), missingMaps.ToImmutableArray(),
            itemIds.Where(id => !itemExists(id)).Order().ToImmutableArray(), issues.ToImmutableArray());
    }

    internal static ImmutableArray<int> MapReferences(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        List<RaidIssue> issues)
    {
        var result = ImmutableArray.CreateBuilder<int>();
        int blocks = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != 3 || RaidScriptParser.Tag(resolve(tokens[i].Value)) != "[map specification]") continue;
            int start = i++;
            int countBefore = result.Count;
            blocks++;
            while (i < tokens.Count && tokens[i].Type != 3)
            {
                if (i + 3 >= tokens.Count || tokens[i].Type != 6 ||
                    resolve(tokens[i].Value).ToLowerInvariant() is not ("map" or "boss") ||
                    tokens[i + 1] is not { Type: 0, Value: >= 0 } || tokens[i + 2] is not { Type: 0, Value: >= 0 } ||
                    tokens[i + 3] is not { Type: 0, Value: >= 0 })
                { issues.Add(new("unsupported-map-reference", path, i, "expected explicit map/boss x y id")); break; }
                result.Add(tokens[i + 3].Value);
                i += 4;
            }
            if (result.Count == countBefore) issues.Add(new("map-reference-block-empty", path, start, "[map specification]"));
            if (i >= tokens.Count || tokens[i].Type != 3 || RaidScriptParser.Tag(resolve(tokens[i].Value)) != "[/map specification]")
                issues.Add(new("map-reference-block-incomplete", path, start, "[/map specification]"));
        }
        if (blocks == 0 || result.Count == 0) issues.Add(new("map-references-missing", path, 0, "[map specification]"));
        return result.ToImmutable();
    }
}
