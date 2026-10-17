using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record RecoveryConsumableDefinition(int ItemId, int MinimumLevel, int CoolTime, int CoolGroup,
    bool SummonsApc = false)
{
    public int MaximumLevel { get; init; } = int.MaxValue;
    public ushort Jobs { get; init; } = ushort.MaxValue;
    public bool UseForbidden { get; init; }
    public bool RequiresDungeon { get; init; }
    public bool EffectsAfterAcknowledgement { get; init; }
    public bool Allows(byte level, byte job) => !UseForbidden && level >= MinimumLevel && level <= MaximumLevel &&
        job < 16 && (Jobs & (1 << job)) != 0;
}

internal sealed class RecoveryConsumableCatalog
{
    readonly PvfArchive? archive;
    readonly IReadOnlyDictionary<int, string>? stackable;
    readonly ItemCatalog? items;
    readonly ConcurrentDictionary<int, Lazy<RecoveryConsumableDefinition?>> definitions = new();

    public RecoveryConsumableCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable, ItemCatalog items)
    {
        this.archive = archive;
        this.stackable = stackable;
        this.items = items;
    }

    internal RecoveryConsumableCatalog(IEnumerable<RecoveryConsumableDefinition> source)
    {
        foreach (var definition in source)
            definitions.TryAdd(definition.ItemId, new(() => definition));
    }

    internal static RecoveryConsumableDefinition? Read(RecoveryConsumableFile file)
    {
        var raw = file.Stackable;
        if (file.TemplateId <= 0 || raw.TemplateId != file.TemplateId ||
            raw.TypeDeclaration is not { Label: "[waste]" or "[throw]" or "[set]" } ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.DeclaredMinimumLevel is < 0 or > 255 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null || raw.DeclaredMaximumLevel < (raw.DeclaredMinimumLevel ?? 0) ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null || file.CoolTime < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null || file.CoolGroup < 0 ||
            file.DeclaredFields.Contains("sub type") && file.SubType is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is null ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 || file.DeclaredFields.Contains("action type") || raw.HasBoosterInfo)
            return null;
        ushort jobs = ushort.MaxValue;
        if (raw.DeclaredUsableJobs is { Count: > 0 } tags && !tags.Contains("[all]"))
        {
            jobs = 0;
            foreach (string tag in tags)
            {
                int job = Enumerable.Range(0, 16).FirstOrDefault(value => ItemCatalog.JobTag((byte)value) == tag, -1);
                if (job < 0)
                    return null;
                jobs |= checked((ushort)(1 << job));
            }
        }
        // HP/MP、持续属性、异常状态及随机效果由客户端执行，不以其数值或格式限制消耗资格。
        return new(file.TemplateId, raw.DeclaredMinimumLevel ?? 0, file.CoolTime ?? 0, file.CoolGroup ?? -file.TemplateId,
            SummonsApc: file.DeclaredFields.Contains("summon apc"))
        { MaximumLevel = raw.DeclaredMaximumLevel ?? int.MaxValue, Jobs = jobs,
            RequiresDungeon = raw.TypeDeclaration.Label is "[throw]" or "[set]", EffectsAfterAcknowledgement = file.SubType == 2 };
    }

    public bool TryGet(int id, out RecoveryConsumableDefinition definition)
    {
        definition = null!;
        if (!definitions.TryGetValue(id, out var cached))
        {
            // 仅缓存资源名册中的条目；同一资源并发首次访问共用解析，不为未知请求ID建立无界负缓存。
            if (archive is null || stackable is null || !stackable.TryGetValue(id, out var path))
                return false;
            cached = definitions.GetOrAdd(id, _ => new(() => Load(id, path)));
        }
        definition = cached.Value!;
        return definition is not null;
    }

    RecoveryConsumableDefinition? Load(int id, string path)
    {
        if (!archive!.Contains("stackable/" + path))
            return null;
        var definition = Read(RecoveryConsumableFile.Parse(id, "stackable/" + path, archive.ReadTokens("stackable/" + path), archive.ResolveString));
        if (definition is null)
            return null;
        var template = items!.Get(id);
        return template.Kind == ItemKind.Consumable ? definition with
        {
            MinimumLevel = template.MinimumLevel,
            MaximumLevel = template.MaximumLevel,
            Jobs = template.Jobs,
            UseForbidden = template.UseForbidden
        } : null;
    }
}
