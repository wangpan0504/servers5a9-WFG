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
    public ImmutableArray<string> Places { get; init; } = [];
    public bool EffectsAfterAcknowledgement { get; init; }
    public int ExpBonusPermille { get; init; }
    public int QuestExpBonusPermille { get; init; }
    public int ExpBonusMilliseconds { get; init; }
    public int EffectMilliseconds { get; init; }
    public ImmutableArray<ConsumableStatChange> StatChanges { get; init; } = [];
    public byte EffectMaintenance { get; init; }
    public byte CooltimeMaintenance { get; init; }
    public bool Unlimited { get; init; }
    public bool OpensPortableCargo { get; init; }
    public int TownGiftItem { get; init; }
    public bool RevivalBlessing { get; init; }
    public bool RaidUseForbidden { get; init; }
    public BossSelectionBuffDefinition? BossSelectionBuff { get; init; }
    public EpicDropBuffDefinition? EpicDropBuff { get; init; }
    public DungeonEpicDropBuffDefinition? DungeonEpicDropBuff { get; init; }
    public VariousBuffBinding? VariousBuff { get; init; }
    public int? VariousDurationMilliseconds { get; init; }
    public HellLegendEpicDropBuffDefinition? HellLegendEpicDropBuff { get; init; }
    public VillageMoveSpeedBuffDefinition? VillageMoveSpeedBuff { get; init; }
    public EnhancementPotionDefinition? EnhancementPotion { get; init; }
    public TreasureHunterPotionDefinition? TreasureHunterPotion { get; init; }
    public ConsumableDisguiseDefinition? Disguise { get; init; }
    public ImmutableHashSet<uint> ExcludedDungeons { get; init; } = ImmutableHashSet<uint>.Empty;
    public ImmutableHashSet<uint>? AllowedDungeons { get; init; }
    // 原生客户端负责职业及模板副本名单；服务端保留本流程仍需处理的等级与禁用状态。
    public bool Allows(byte level) => !UseForbidden && level >= MinimumLevel && level <= MaximumLevel;
}

internal readonly record struct ConsumableDisguiseForm(byte Type, int FormId);

internal sealed record ConsumableDisguiseDefinition(byte Type, int FormId, int Seconds)
{
    public ImmutableArray<ConsumableDisguiseForm> Forms { get; init; } = [];
    public ImmutableArray<ConsumableStatChange> Appendages { get; init; } = [];
    public bool Allows(byte type, int formId) => Forms.IsEmpty
        ? type == Type && formId == FormId : Forms.Contains(new(type, formId));
}
internal sealed record ConsumableStatChange(string Operation, int Amount, string Stat);

internal sealed class RecoveryConsumableCatalog
{
    readonly PvfArchive? archive;
    readonly IReadOnlyDictionary<int, string>? stackable;
    readonly ItemCatalog? items;
    readonly ConcurrentDictionary<int, Lazy<RecoveryConsumableDefinition?>> definitions = new();
    readonly Lazy<ImmutableDictionary<int, BossSelectionBuffDefinition>> bossSelection;
    readonly Lazy<ImmutableDictionary<int, EpicDropBuffDefinition>> epicDrop;
    readonly Lazy<VariousBuffCatalog> variousBuffs;
    readonly Lazy<ImmutableDictionary<int, DungeonEpicDropBuffDefinition>> dungeonEpicDrop;
    readonly Lazy<ImmutableDictionary<int, HellLegendEpicDropBuffDefinition>> hellLegendEpicDrop;
    readonly Lazy<ImmutableDictionary<int, VillageMoveSpeedBuffDefinition>> villageMoveSpeed;
    readonly Lazy<ImmutableDictionary<int, EnhancementPotionDefinition>> enhancementPotions;
    internal IReadOnlyDictionary<int, EpicDropBuffDefinition> EpicDropBuffs => epicDrop.Value;
    internal IReadOnlyDictionary<int, DungeonEpicDropBuffDefinition> DungeonEpicDropBuffs => dungeonEpicDrop.Value;
    internal IReadOnlyDictionary<int, HellLegendEpicDropBuffDefinition> HellLegendEpicDropBuffs => hellLegendEpicDrop.Value;
    internal IReadOnlyDictionary<int, VillageMoveSpeedBuffDefinition> VillageMoveSpeedBuffs => villageMoveSpeed.Value;
    internal IReadOnlyDictionary<int, EnhancementPotionDefinition> EnhancementPotions => enhancementPotions.Value;
    internal VariousBuffCatalog VariousBuffs => variousBuffs.Value;
    internal IReadOnlyDictionary<int, int> AlchemistPotionInfo { get; }
    internal IReadOnlyDictionary<int, TreasureHunterPotionDefinition> TreasureHunterPotions { get; }

    public RecoveryConsumableCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable, ItemCatalog items)
    {
        this.archive = archive;
        this.stackable = stackable;
        this.items = items;
        bossSelection = new(() => BossSelectionBuffDefinition.Load(archive));
        variousBuffs = new(() => VariousBuffCatalog.Load(archive));
        epicDrop = new(() => EpicDropBuffDefinition.Load(variousBuffs.Value));
        hellLegendEpicDrop = new(() => HellLegendEpicDropBuffDefinition.Load(variousBuffs.Value));
        villageMoveSpeed = new(() => VillageMoveSpeedBuffDefinition.Load(archive));
        // 已有单项城镇速度入口保持；复合定义才由通用生命周期承接，避免同一源被注册两次。
        dungeonEpicDrop = new(() => DungeonEpicDropBuffDefinition.Load(variousBuffs.Value)
            .Where(pair => !villageMoveSpeed.Value.ContainsKey(pair.Key)).ToImmutableDictionary());
        // 监听前完成小型参数表与已知变体的装配；击杀和道具使用不扫描归档。
        AlchemistPotionInfo = archive.Contains(TreasureHunterPotionDefinition.AlchemistPath)
            ? AlchemistPotionInfoFile.Parse(archive.ReadTokens(TreasureHunterPotionDefinition.AlchemistPath), archive.ResolveString).PotionInfo
            : ImmutableDictionary<int, int>.Empty;
        enhancementPotions = new(() => EnhancementPotionDefinition.Load(archive, stackable, AlchemistPotionInfo));
        TreasureHunterPotions = TreasureHunterPotionDefinition.Load(archive, stackable, AlchemistPotionInfo);
        // 关联表及其兼容视图在资源装配时完成，登录投影和首次使用不在账号锁内解析整张表。
        _ = variousBuffs.Value;
        _ = epicDrop.Value;
        _ = hellLegendEpicDrop.Value;
        _ = dungeonEpicDrop.Value;
    }

    internal RecoveryConsumableCatalog(IEnumerable<RecoveryConsumableDefinition> source)
    {
        var rows = source.Select(row =>
        {
            if (row.VariousBuff is not null || row.DungeonEpicDropBuff?.Resource is not { } resource) return row;
            var association = new VariousBuffAssociation(row.ItemId, [new([resource.BuffId], 100)], false, []);
            return row with { VariousBuff = new(association, ImmutableDictionary<int, VariousBuffDefinition>.Empty.Add(resource.BuffId, resource)),
                VariousDurationMilliseconds = row.VariousDurationMilliseconds ??
                    (row.EffectMilliseconds > 0 ? row.EffectMilliseconds : (int?)null),
                EffectMilliseconds = 0 };
        }).ToArray();
        TreasureHunterPotions = rows.Where(row => row.TreasureHunterPotion is not null)
            .ToImmutableDictionary(row => row.ItemId, row => row.TreasureHunterPotion!);
        AlchemistPotionInfo = TreasureHunterPotions.Where(row => row.Value.FromExj)
            .ToImmutableDictionary(row => row.Key, row => row.Value.BonusRate);
        bossSelection = new(() => ImmutableDictionary<int, BossSelectionBuffDefinition>.Empty);
        variousBuffs = new(() =>
        {
            var bindings = rows.Where(row => row.VariousBuff is not null).Select(row => row.VariousBuff!).ToArray();
            var sourceDefinitions = bindings.SelectMany(binding => binding.Definitions).GroupBy(pair => pair.Key)
                .ToImmutableDictionary(group => group.Key, group => group.First().Value);
            var sourceBuilder = sourceDefinitions.ToBuilder();
            var associationBuilder = bindings.ToImmutableDictionary(binding => binding.Association.ItemId, binding => binding.Association).ToBuilder();
            foreach (var row in rows.Where(row => row.VariousBuff is null))
            {
                int id = row.EpicDropBuff?.BuffId ?? row.HellLegendEpicDropBuff?.BuffId ?? row.VillageMoveSpeedBuff?.BuffId ?? 0;
                if (id == 0) continue;
                string name = row.EpicDropBuff is not null ? "epic_rarity_item_drop_rate" : row.HellLegendEpicDropBuff is not null ?
                    "hell_legend_dungeon_epic_drop_rate_up" : "village_move_speed";
                int value = row.EpicDropBuff?.Percent ?? row.HellLegendEpicDropBuff?.Param ?? row.VillageMoveSpeedBuff!.Percent;
                string group = row.EpicDropBuff?.SystemGroup ?? row.HellLegendEpicDropBuff?.SystemGroup ?? "village_move_speed";
                var scope = row.HellLegendEpicDropBuff is not null ? VariousBuffDungeonScope.HellLegend : VariousBuffDungeonScope.All;
                sourceBuilder[id] = new(id, group, [VariousBuffCatalog.ReadEffect(name, [value])], "expire_time", [row.EffectMilliseconds / 1000, 0], scope, [])
                    { ScopeRules = scope == VariousBuffDungeonScope.All ? [] : [new(scope, [])], Overlap = true };
                associationBuilder[row.ItemId] = new(row.ItemId, [new([id], 100)], false, []);
            }
            sourceDefinitions = sourceBuilder.ToImmutable();
            var associations = associationBuilder.ToImmutable();
            var fixedItems = associations.Values.Where(association => !association.Random && association.Choices is [{ BuffIds.Length: 1 }])
                .ToImmutableDictionary(association => association.ItemId, association => sourceDefinitions[association.Choices[0].BuffIds[0]]);
            return new(sourceDefinitions, fixedItems, associations);
        });
        epicDrop = new(() => rows.Where(row => row.EpicDropBuff is not null).ToImmutableDictionary(row => row.ItemId, row => row.EpicDropBuff!));
        dungeonEpicDrop = new(() => rows.Where(row => row.DungeonEpicDropBuff is not null)
            .ToImmutableDictionary(row => row.ItemId, row => row.DungeonEpicDropBuff!));
        hellLegendEpicDrop = new(() => rows.Where(row => row.HellLegendEpicDropBuff is not null)
            .ToImmutableDictionary(row => row.ItemId, row => row.HellLegendEpicDropBuff!));
        villageMoveSpeed = new(() => rows.Where(row => row.VillageMoveSpeedBuff is not null)
            .ToImmutableDictionary(row => row.ItemId, row => row.VillageMoveSpeedBuff!));
        enhancementPotions = new(() => rows.Where(row => row.EnhancementPotion is not null)
            .ToImmutableDictionary(row => row.ItemId, row => row.EnhancementPotion!));
        foreach (var definition in rows)
            definitions.TryAdd(definition.ItemId, new(() => definition));
    }

    internal static RecoveryConsumableDefinition? Read(RecoveryConsumableFile file, BossSelectionBuffDefinition? bossBuff = null,
        EpicDropBuffDefinition? epicBuff = null, EnhancementPotionDefinition? enhancementPotion = null,
        HellLegendEpicDropBuffDefinition? hellLegendBuff = null, VillageMoveSpeedBuffDefinition? villageSpeedBuff = null,
        TreasureHunterPotionDefinition? treasureHunter = null, DungeonEpicDropBuffDefinition? dungeonEpicBuff = null,
        VariousBuffBinding? variousBuff = null)
    {
        var raw = file.Stackable;
        // 原生工厂优先构造普通持续属性对象；use action packet并不将它改成515动作类。
        bool nativeStatUse = raw.TypeDeclaration?.Label is "[waste]" or "[unlimited waste]" or "[waste instant item]" &&
            file.StatChangeDuration is { Milliseconds: > 0 } && !file.StatChanges.IsEmpty &&
            file.StructureDiagnostics.IsEmpty;
        bool nativeSingleDisguise = raw.TypeDeclaration is { Label: "[disguise]", Parameters: [0] } &&
            file.Disguise is { Kind: "npc" or "monster" or "creature", FormId: > 0, Seconds: > 0 and <= int.MaxValue / 1000 };
        // 211保留每个候选的资源种类；仅装配持续时间一致的池，不猜测异长候选的截止。
        bool nativeRandomDisguise = raw.TypeDeclaration is { Label: "[random disguise]", Parameters: [0] } &&
            !file.RandomDisguises.IsEmpty && file.RandomDisguises.All(row =>
                row is { Kind: "npc" or "monster" or "creature", FormId: > 0, Seconds: > 0 and <= int.MaxValue / 1000 } &&
                row.Seconds == file.RandomDisguises[0].Seconds);
        bool nativeDisguise = (nativeSingleDisguise || nativeRandomDisguise) && file.Diagnostics.IsEmpty &&
            file.UnlimitedWaste is null or 0 or 1;
        if (file.TemplateId <= 0 || raw.TemplateId != file.TemplateId || raw.TypeDeclaration is null || LegacyStatusConsumableDefinition.IsNativeItem(file.TemplateId) ||
            (raw.TypeDeclaration is not { Label: "[waste]" or "[unlimited waste]" or "[waste instant item]" or "[throw]" or "[set]" } && !nativeDisguise &&
                !(raw.TypeDeclaration is { Label: "[expert town potion]", Parameters: [0] } &&
                    (bossBuff is not null || epicBuff is not null || enhancementPotion is not null || hellLegendBuff is not null ||
                        villageSpeedBuff is not null || dungeonEpicBuff is not null || variousBuff is not null)) &&
                !(enhancementPotion is not null && raw.TypeDeclaration is
                    { Label: "[item upgrad buff usual upgrade]" or "[item upgrad buff amplify upgrade]", Parameters: [> 0] })) ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.DeclaredMinimumLevel is < 0 or > 255 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null || raw.DeclaredMaximumLevel < (raw.DeclaredMinimumLevel ?? 0) ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null || file.CoolTime < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null || file.CoolGroup < 0 ||
            file.DeclaredFields.Contains("sub type") && file.SubType is null ||
            file.DeclaredFields.Contains("action usable dungeon") && file.ActionUsableDungeons is null ||
            file.LimitDungeons.IsDefault || file.LimitDungeons.Any(id => id <= 0) || file.ImpossibleContents.IsDefault ||
            ConsumableSpecialItems.IsStaminaRecovery(file.TemplateId) &&
                (file.Diagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 }) ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is null ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 &&
                !(raw.DeclaredUseActionPacket == 1 && nativeStatUse) ||
            file.DeclaredFields.Contains("action type") || raw.HasBoosterInfo)
            return null;
        if ((bossBuff is not null || epicBuff is not null || hellLegendBuff is not null || villageSpeedBuff is not null || dungeonEpicBuff is not null) &&
            file.StructureDiagnostics.Length != 0) return null;
        if (variousBuff is not null && (!variousBuff.Ready || variousBuff.Association.ItemId != file.TemplateId ||
            bossBuff is not null || epicBuff is not null || hellLegendBuff is not null ||
            villageSpeedBuff is not null || enhancementPotion is not null || file.StructureDiagnostics.Length != 0 ||
            file.DeclaredFields.Contains("stat change duration") && file.StatChangeDuration is not { Target: "myself", Milliseconds: >= 0 })) return null;
        if (variousBuff is null && dungeonEpicBuff is not null && (bossBuff is not null || epicBuff is not null || hellLegendBuff is not null ||
            villageSpeedBuff is not null || enhancementPotion is not null || dungeonEpicBuff.BuffId <= 0 ||
            file.Diagnostics.Length != 0 || (dungeonEpicBuff.Timed
                ? file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } || (file.EffectMaintenance ?? file.CooltimeMaintenance ?? 0) == 0
                : file.StatChangeDuration is not null || file.EffectMaintenance is not null))) return null;
        if (epicBuff is not null && (bossBuff is not null || hellLegendBuff is not null || villageSpeedBuff is not null ||
            epicBuff.BuffId <= 0 || !epicBuff.HasSupportedParameters ||
            file.Diagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } ||
            file.EffectMaintenance is null or 0)) return null;
        byte hellLegendMaintenance = file.EffectMaintenance ?? file.CooltimeMaintenance ?? 0;
        if (hellLegendBuff is not null && (bossBuff is not null || epicBuff is not null || enhancementPotion is not null ||
            villageSpeedBuff is not null ||
            hellLegendBuff.BuffId <= 0 || hellLegendBuff.Param < 0 ||
            file.Diagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } ||
            hellLegendMaintenance == 0)) return null;
        byte villageSpeedMaintenance = file.EffectMaintenance ?? file.CooltimeMaintenance ?? 0;
        if (villageSpeedBuff is not null && (bossBuff is not null || epicBuff is not null || hellLegendBuff is not null ||
            enhancementPotion is not null || villageSpeedBuff.BuffId <= 0 || villageSpeedBuff.Percent <= 0 ||
            file.Diagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } ||
            villageSpeedMaintenance == 0)) return null;
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
            Places = raw.DeclaredActionUsablePlaces?.ToImmutableArray() ?? [],
            BossSelectionBuff = bossBuff,
            EpicDropBuff = epicBuff,
            DungeonEpicDropBuff = dungeonEpicBuff,
            VariousBuff = variousBuff,
            VariousDurationMilliseconds = file.StatChangeDuration?.Milliseconds,
            HellLegendEpicDropBuff = hellLegendBuff,
            VillageMoveSpeedBuff = villageSpeedBuff,
            EnhancementPotion = enhancementPotion,
            Disguise = nativeDisguise ? nativeRandomDisguise
                ? new(DisguiseType(file.RandomDisguises[0].Kind), file.RandomDisguises[0].FormId, file.RandomDisguises[0].Seconds)
                    { Forms = file.RandomDisguises.Select(row => new ConsumableDisguiseForm(DisguiseType(row.Kind), row.FormId)).ToImmutableArray(),
                        Appendages = file.DisguiseAppendages.Select(change => new ConsumableStatChange(change.Operation, change.Amount, change.Stat)).ToImmutableArray() }
                : new(DisguiseType(file.Disguise!.Kind), file.Disguise.FormId, file.Disguise.Seconds)
                    { Appendages = file.DisguiseAppendages.Select(change => new ConsumableStatChange(change.Operation, change.Amount, change.Stat)).ToImmutableArray() } : null,
            RequiresDungeon = revival || file.DeclaredFields.Contains("summon npc") || raw.TypeDeclaration.Label is "[throw]" or "[set]", EffectsAfterAcknowledgement = file.SubType == 2,
            ExpBonusPermille = expBonus ? file.ExpBonusPermille!.Value : 0,
            QuestExpBonusPermille = file.QuestExpBonusPermille is > 0 && file.ExpBonusMilliseconds is > 0 ? file.QuestExpBonusPermille.Value : 0,
            ExpBonusMilliseconds = expBonus ? file.ExpBonusMilliseconds!.Value : 0,
            EffectMilliseconds = variousBuff is not null ? 0 : revival ? ConsumableSpecialItems.RevivalMilliseconds : file.StatChangeDuration?.Milliseconds ?? 0,
            StatChanges = file.StatChanges.Select(change => new ConsumableStatChange(change.Operation, change.Amount, change.Stat)).ToImmutableArray(),
            EffectMaintenance = revival ? (byte)0 : hellLegendBuff is not null ? hellLegendMaintenance :
                dungeonEpicBuff?.Timed == true ? file.EffectMaintenance ?? file.CooltimeMaintenance ?? 0 :
                villageSpeedBuff is not null ? villageSpeedMaintenance : file.EffectMaintenance ?? 0,
            CooltimeMaintenance = file.CooltimeMaintenance ?? 0,
            Unlimited = raw.TypeDeclaration.Label == "[unlimited waste]" || nativeDisguise && file.UnlimitedWaste == 1,
            TownGiftItem = raw.TypeDeclaration.Label == "[unlimited waste]" ? ConsumableSpecialItems.TownGiftItem(file.TemplateId) : 0,
            RevivalBlessing = revival,
            TreasureHunterPotion = treasureHunter,
            AllowedDungeons = file.ActionUsableDungeons?.Select(id => checked((uint)id)).ToImmutableHashSet(),
            ExcludedDungeons = file.LimitDungeons.Select(id => checked((uint)id)).ToImmutableHashSet(),
            RaidUseForbidden = file.ImpossibleContents.Contains("raid dungeon", StringComparer.OrdinalIgnoreCase) };
    }

    static byte DisguiseType(string kind) => kind switch
    {
        "npc" => 1, "monster" => 2, "creature" => 3,
        _ => throw new InvalidDataException("未知变身资源种类")
    };

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
        var treasure = TreasureHunterPotions.GetValueOrDefault(id);
        // 身份已知但资源声明不完整时，不按普通空效果药消费；缺少EXJ行已由目录显式兜底。
        if (TreasureHunterPotionDefinition.FamilyItems.Contains(id) && treasure is null) return null;
        var buff = file.Stackable.TypeDeclaration?.Label == "[expert town potion]" ? bossSelection.Value.GetValueOrDefault(id) : null;
        var epic = file.Stackable.TypeDeclaration?.Label == "[expert town potion]" ? epicDrop.Value.GetValueOrDefault(id) : null;
        var dungeonEpic = file.Stackable.TypeDeclaration?.Label == "[expert town potion]" ? dungeonEpicDrop.Value.GetValueOrDefault(id) : null;
        var hellLegend = file.Stackable.TypeDeclaration?.Label == "[expert town potion]"
            ? hellLegendEpicDrop.Value.GetValueOrDefault(id) : null;
        var villageSpeed = file.Stackable.TypeDeclaration?.Label == "[expert town potion]"
            ? villageMoveSpeed.Value.GetValueOrDefault(id) : null;
        if (epic is not null || hellLegend is not null)
        {
            var events = new PvfTokenDocument(tokens, archive.ResolveString).Sections.Where(field => field.Name == "usable event").ToArray();
            // 旧版同族STK可不带事件；出现该声明时必须完整匹配已核2284，不能忽略未知事件。
            if (events.Length != 0 && events is not [{ Children.Length: 0, Values: [{ Type: 0, Value: 2284 }] }]) return null;
        }
        var enhancement = enhancementPotions.Value.GetValueOrDefault(id);
        var association = file.Stackable.TypeDeclaration?.Label == "[expert town potion]" ? variousBuffs.Value.Bind(id) : null;
        // 专用效果先保留原消费合同；其余关联不按编号列举，新增name支持后自动取得同一通用链路。
        var generic = buff is null && epic is null && hellLegend is null && villageSpeed is null && enhancement is null ? association : null;
        if (generic is not null && !generic.Ready) return null;
        var definition = Read(file, buff, epic, enhancement, hellLegend, villageSpeed, treasure, dungeonEpic, generic);
        // 老视图要求STK计时；缺少时长但Buff模板本身完整时，由通用生命周期解释，而不是拒绝合法关联。
        if (definition is null && association is { Ready: true } && buff is null && enhancement is null)
            definition = Read(file, treasureHunter: treasure, variousBuff: association);
        if (definition is null)
            return null;
        var template = items!.Get(id);
        return template.Kind == ItemKind.Consumable ? definition with
        {
            // 仅匹配名册中已核对的猫头鹰模板；普通药剂及同名图标不产生仓库资格。
            OpensPortableCargo = path is "cash/owl.stk" or "cash/owl_unlimited.stk" or "cash/owl_new_unlimited.stk",
            MinimumLevel = template.MinimumLevel,
            MaximumLevel = template.MaximumLevel,
            Jobs = template.Jobs,
            UseForbidden = template.UseForbidden
        } : null;
    }
}
