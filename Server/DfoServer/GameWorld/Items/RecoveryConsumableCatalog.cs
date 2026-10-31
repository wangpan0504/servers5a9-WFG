using System.Collections.Concurrent;
using System.Collections.Immutable;
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
    public int ExpBonusPermille { get; init; }
    public int QuestExpBonusPermille { get; init; }
    public int ExpBonusMilliseconds { get; init; }
    public int EffectMilliseconds { get; init; }
    public byte EffectMaintenance { get; init; }
    public byte CooltimeMaintenance { get; init; }
    public bool Unlimited { get; init; }
    public int TownGiftItem { get; init; }
    public bool RevivalBlessing { get; init; }
    public BossSelectionBuffDefinition? BossSelectionBuff { get; init; }
    public EpicDropBuffDefinition? EpicDropBuff { get; init; }
    public EnhancementPotionDefinition? EnhancementPotion { get; init; }
    public ImmutableHashSet<uint> ExcludedDungeons { get; init; } = ImmutableHashSet<uint>.Empty;
    public ImmutableHashSet<uint>? AllowedDungeons { get; init; }
    public bool Allows(byte level, byte job) => !UseForbidden && level >= MinimumLevel && level <= MaximumLevel &&
        job < 16 && (Jobs & (1 << job)) != 0;
}

internal sealed class RecoveryConsumableCatalog
{
    readonly PvfArchive? archive;
    readonly IReadOnlyDictionary<int, string>? stackable;
    readonly ItemCatalog? items;
    readonly ConcurrentDictionary<int, Lazy<RecoveryConsumableDefinition?>> definitions = new();
    readonly Lazy<ImmutableDictionary<int, BossSelectionBuffDefinition>> bossSelection;
    readonly Lazy<ImmutableDictionary<int, EpicDropBuffDefinition>> epicDrop;
    readonly Lazy<ImmutableDictionary<int, EnhancementPotionDefinition>> enhancementPotions;
    internal IReadOnlyDictionary<int, EpicDropBuffDefinition> EpicDropBuffs => epicDrop.Value;
    internal IReadOnlyDictionary<int, EnhancementPotionDefinition> EnhancementPotions => enhancementPotions.Value;

    public RecoveryConsumableCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable, ItemCatalog items)
    {
        this.archive = archive;
        this.stackable = stackable;
        this.items = items;
        bossSelection = new(() => BossSelectionBuffDefinition.Load(archive));
        epicDrop = new(() => EpicDropBuffDefinition.Load(archive));
        enhancementPotions = new(() => EnhancementPotionDefinition.Load(archive, stackable));
    }

    internal RecoveryConsumableCatalog(IEnumerable<RecoveryConsumableDefinition> source)
    {
        var rows = source.ToArray();
        bossSelection = new(() => ImmutableDictionary<int, BossSelectionBuffDefinition>.Empty);
        epicDrop = new(() => rows.Where(row => row.EpicDropBuff is not null).ToImmutableDictionary(row => row.ItemId, row => row.EpicDropBuff!));
        enhancementPotions = new(() => rows.Where(row => row.EnhancementPotion is not null)
            .ToImmutableDictionary(row => row.ItemId, row => row.EnhancementPotion!));
        foreach (var definition in rows)
            definitions.TryAdd(definition.ItemId, new(() => definition));
    }

    internal static RecoveryConsumableDefinition? Read(RecoveryConsumableFile file, BossSelectionBuffDefinition? bossBuff = null,
        EpicDropBuffDefinition? epicBuff = null, EnhancementPotionDefinition? enhancementPotion = null)
    {
        var raw = file.Stackable;
        if (file.TemplateId <= 0 || raw.TemplateId != file.TemplateId || LegacyStatusConsumableDefinition.IsNativeItem(file.TemplateId) ||
            (raw.TypeDeclaration is not { Label: "[waste]" or "[unlimited waste]" or "[throw]" or "[set]" } &&
                !(raw.TypeDeclaration is { Label: "[expert town potion]", Parameters: [0] } &&
                    (bossBuff is not null || epicBuff is not null || enhancementPotion is not null)) &&
                !(enhancementPotion is not null && raw.TypeDeclaration is
                    { Label: "[item upgrad buff usual upgrade]" or "[item upgrad buff amplify upgrade]", Parameters: [> 0] })) ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.DeclaredMinimumLevel is < 0 or > 255 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null || raw.DeclaredMaximumLevel < (raw.DeclaredMinimumLevel ?? 0) ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null || file.CoolTime < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null || file.CoolGroup < 0 ||
            file.DeclaredFields.Contains("sub type") && file.SubType is null ||
            file.DeclaredFields.Contains("action usable dungeon") && file.ActionUsableDungeons is null ||
            ConsumableSpecialItems.IsStaminaRecovery(file.TemplateId) &&
                (file.Diagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 }) ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is null ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 || file.DeclaredFields.Contains("action type") || raw.HasBoosterInfo)
            return null;
        if ((bossBuff is not null || epicBuff is not null) && (file.StructureDiagnostics.Length != 0 ||
            raw.DeclaredActionUsablePlaces is not { Count: > 0 } places ||
            !places.Contains("[village]") || places.Any(place => place is not ("[village]" or "[seria room]")))) return null;
        if (epicBuff is not null && (bossBuff is not null || epicBuff.BuffId <= 0 || epicBuff.Percent != 100 ||
            file.Diagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } ||
            file.EffectMaintenance is null or 0)) return null;
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
        if (file.DeclaredFields.Contains("quest exp bonus rate") &&
            (file.QuestExpBonusPermille is null || file.ExpBonusMilliseconds is not > 0)) return null;
        // HP/MP、持续属性、异常状态及随机效果由客户端执行，不以其数值或格式限制消耗资格。
        bool expBonus = file.ExpBonusPermille is > 0 && file.ExpBonusMilliseconds is > 0;
        bool revival = ConsumableSpecialItems.IsRevivalBlessing(file.TemplateId);
        return new(file.TemplateId, raw.DeclaredMinimumLevel ?? 0, file.CoolTime ?? 0, file.CoolGroup ?? -file.TemplateId,
            SummonsApc: file.DeclaredFields.Contains("summon apc"))
        { MaximumLevel = raw.DeclaredMaximumLevel ?? int.MaxValue, Jobs = jobs,
            BossSelectionBuff = bossBuff,
            EpicDropBuff = epicBuff,
            EnhancementPotion = enhancementPotion,
            RequiresDungeon = revival || file.DeclaredFields.Contains("summon npc") || raw.TypeDeclaration.Label is "[throw]" or "[set]", EffectsAfterAcknowledgement = file.SubType == 2,
            ExpBonusPermille = expBonus ? file.ExpBonusPermille!.Value : 0,
            QuestExpBonusPermille = file.QuestExpBonusPermille is > 0 && file.ExpBonusMilliseconds is > 0 ? file.QuestExpBonusPermille.Value : 0,
            ExpBonusMilliseconds = expBonus ? file.ExpBonusMilliseconds!.Value : 0,
            EffectMilliseconds = revival ? ConsumableSpecialItems.RevivalMilliseconds : file.StatChangeDuration?.Milliseconds ?? 0,
            EffectMaintenance = revival ? (byte)0 : file.EffectMaintenance ?? 0,
            CooltimeMaintenance = file.CooltimeMaintenance ?? 0,
            Unlimited = raw.TypeDeclaration.Label == "[unlimited waste]",
            TownGiftItem = raw.TypeDeclaration.Label == "[unlimited waste]" ? ConsumableSpecialItems.TownGiftItem(file.TemplateId) : 0,
            RevivalBlessing = revival,
            AllowedDungeons = file.ActionUsableDungeons?.Select(id => checked((uint)id)).ToImmutableHashSet(),
            ExcludedDungeons = revival ? file.LimitDungeons.Select(id => checked((uint)id)).ToImmutableHashSet() : ImmutableHashSet<uint>.Empty };
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
        var tokens = archive.ReadTokens("stackable/" + path);
        var file = RecoveryConsumableFile.Parse(id, "stackable/" + path, tokens, archive.ResolveString);
        var buff = file.Stackable.TypeDeclaration?.Label == "[expert town potion]" ? bossSelection.Value.GetValueOrDefault(id) : null;
        var epic = file.Stackable.TypeDeclaration?.Label == "[expert town potion]" ? epicDrop.Value.GetValueOrDefault(id) : null;
        if (epic is not null)
        {
            var events = new PvfTokenDocument(tokens, archive.ResolveString).Sections.Where(field => field.Name == "usable event").ToArray();
            // 旧版同族STK可不带事件；出现该声明时必须完整匹配已核2284，不能忽略未知事件。
            if (events.Length != 0 && events is not [{ Children.Length: 0, Values: [{ Type: 0, Value: 2284 }] }]) return null;
        }
        var enhancement = enhancementPotions.Value.GetValueOrDefault(id);
        var definition = Read(file, buff, epic, enhancement);
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
