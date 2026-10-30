using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed partial class ItemCatalog
{
    readonly PvfArchive pvf;
    readonly IReadOnlyDictionary<int, string> equipment;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly IReadOnlySet<int> premiumServiceItems;
    readonly IReadOnlyDictionary<int, PremiumContract> acquisitionContracts;
    readonly IReadOnlyDictionary<int, AccountCargoAcquisition> acquisitionCargo;
    readonly GameWorld.Creatures.CreatureCatalog? creatures;
    readonly EchonEquipmentCatalog? echon;
    readonly EchonEquipmentCatalog? newEchon;
    readonly ConcurrentDictionary<int, ItemTemplate> templates = new();
    readonly object dropCandidatesGate = new();
    ImmutableArray<ItemTemplate> dropCandidateSnapshot;
    readonly ConcurrentDictionary<int, Lazy<ItemTemplate>> pendingTemplates = new();
    readonly ConcurrentDictionary<int, QuestTicketDefinition> questTickets = new();
    readonly ConcurrentDictionary<int, Lazy<LevelUpTicketDefinition?>> levelUpTickets = new();
    readonly ConcurrentDictionary<int, Lazy<ExperienceConsumableDefinition?>> experienceConsumables = new();
    readonly ConcurrentDictionary<int, Lazy<PermanentGrowthDefinition?>> permanentGrowth = new();
    readonly ConcurrentDictionary<int, Lazy<LegacyStatusConsumableDefinition?>> legacyStatusConsumables = new();
    readonly ConcurrentDictionary<int, Lazy<FatigueConsumableDefinition?>> fatigueConsumables = new();
    readonly ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>> actionConsumables = new();
    readonly ConcurrentDictionary<int, Lazy<InventoryExpansionDefinition?>> inventoryExpansions = new();
    readonly ConcurrentDictionary<int, Lazy<EquipmentPeriodChangeDefinition?>> periodChanges = new();
    readonly ConcurrentDictionary<int, Lazy<StackableFile>> stackableDefinitions = new();
    readonly ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>> levelUpTables = new();
    readonly ConcurrentDictionary<int, Lazy<ClassChangeTicketDefinition?>> classChangeTickets = new();
    readonly ConcurrentDictionary<int, Lazy<GameWorld.Creatures.CreatureChangeDefinition?>> creatureChanges = new();
    readonly ConcurrentDictionary<int, Lazy<GameWorld.Creatures.CreatureGrowthItemDefinition?>> creatureGrowthItems = new();
    readonly ConcurrentDictionary<int, Lazy<TalismanDefinition>> talismans = new();
    readonly ConcurrentDictionary<int, Lazy<RuneDefinition>> runes = new();
    static readonly string[] JobNames = ["swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter", "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer", "at priest", "gun blader"];
    static readonly Dictionary<string, byte> EquipmentSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hat avatar"] = 0, ["hair avatar"] = 1, ["face avatar"] = 2,
        ["coat avatar"] = 3, ["pants avatar"] = 4, ["shoes avatar"] = 5,
        ["breast avatar"] = 6, ["waist avatar"] = 7, ["skin avatar"] = 8,
        ["aurora avatar"] = 9, ["weapon avatar"] = 10,
        ["aura skin avatar"] = 11, ["aurora illusion avatar"] = 11,
        ["aurora skin avatar"] = 11, ["aurora change avatar"] = 11,
        // Client 的 creature 类型为26；耳环占用25，不能沿用其他版本的宠物部位编号。
        ["creature"] = 26, ["artifact red"] = 27, ["artifact blue"] = 28, ["artifact green"] = 29,
        ["flag"] = 32,
        ["name tag"] = 30,
        ["weapon"] = 12,
        ["title"] = 13,
        ["title name"] = 13,
        ["coat"] = 14,
        ["shoulder"] = 15,
        ["pants"] = 16,
        ["shoes"] = 17,
        ["waist"] = 18,
        ["amulet"] = 19,
        ["wrist"] = 20,
        ["ring"] = 21,
        ["support"] = 22,
        ["magic stone"] = 23,
        ["support weapon"] = 24,
        ["earring"] = 25,
        // 声明后的整数是资源子类型；护石模板共用34，实际两个穿戴槽为34/35。
        ["talisman"] = 34,
        ["charm"] = 35
    };

    readonly int equipmentSellRate;
    readonly Lazy<ImmutableArray<CosmoPassCost>> cosmoPasses;
    readonly Lazy<RuneSystemFile> runeSystem;
    readonly Lazy<TalismanRefineCost> talismanRefineCost;
    readonly Lazy<TalismanCompoundCost> talismanCompoundCost;
    readonly Lazy<RuneRandomChooseTable> runeRandomChoices;
    readonly Lazy<FrozenDictionary<byte, ImmutableArray<int>>> runePool;
    readonly Lazy<FrozenDictionary<int, ImmutableArray<int>>> talismanPool;
    public EquipmentGrowthCatalog EquipmentGrowth { get; }

    public ItemCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable, int equipmentSellRate, IReadOnlySet<int>? premiumServiceItems = null,
        IReadOnlyDictionary<int, PremiumContract>? acquisitionContracts = null, GameWorld.Creatures.CreatureCatalog? creatures = null,
        EchonEquipmentCatalog? echon = null, EchonEquipmentCatalog? newEchon = null,
        IReadOnlyDictionary<int, AccountCargoAcquisition>? acquisitionCargo = null)
    {
        pvf = archive;
        EquipmentGrowth = new(archive, equipment);
        this.equipment = equipment;
        this.stackable = stackable;
        this.premiumServiceItems = premiumServiceItems ?? new HashSet<int>();
        this.acquisitionContracts = acquisitionContracts ?? new Dictionary<int, PremiumContract>();
        this.acquisitionCargo = acquisitionCargo ?? new Dictionary<int, AccountCargoAcquisition>();
        this.equipmentSellRate = equipmentSellRate;
        this.creatures = creatures;
        this.echon = echon;
        this.newEchon = newEchon;
        cosmoPasses = new(() => CosmoPassCost.Load(archive));
        runeSystem = new(() => RuneSystemFile.Parse(
            archive.ReadTokens("etc/runesystem.etc"), archive.ResolveString));
        talismanRefineCost = new(() => TalismanRefineCost.Parse(runeSystem.Value));
        talismanCompoundCost = new(() => TalismanCompoundCost.Parse(runeSystem.Value));
        runeRandomChoices = new(() => RuneRandomChooseTable.Parse(runeSystem.Value));
        runePool = new(BuildRunePool);
        talismanPool = new(BuildTalismanPool);
    }

    public ItemTemplate Get(int id) => Get(id, null);

    ItemTemplate Get(int id, PvfDocument? parsed)
    {
        if (templates.TryGetValue(id, out var template))
            return template;
        var pending = pendingTemplates.GetOrAdd(id, key => new(() => templates.GetOrAdd(key, itemId => Load(itemId, parsed))));
        try { return pending.Value; }
        finally
        {
            // 并发首次请求共用解析；完成后只保留业务模板，失败不占缓存。
            pendingTemplates.TryRemove(new KeyValuePair<int, Lazy<ItemTemplate>>(id, pending));
        }
    }

    // 可选权益查询允许资源名册或主文件缺失；已存在模板的解析错误仍沿Get抛出。
    public bool TryGet(int id, [NotNullWhen(true)] out ItemTemplate? template)
    {
        if (templates.TryGetValue(id, out template)) return true;
        bool isEquipment = equipment.TryGetValue(id, out string? path);
        if (!isEquipment && !stackable.TryGetValue(id, out path)) return false;
        if (!pvf.Contains((isEquipment ? "equipment/" : "stackable/") + path)) return false;
        template = Get(id);
        return true;
    }

    /// <summary>
    /// 预置模板（**仅供核心自测**）：核心自测没有 PVF，无法从资源取模板，因此把显式构造的模板直接灌进
    /// 同一个缓存。只填缓存、不改 <see cref="Load"/> 的解析逻辑——调用方走的仍是真实的
    /// <see cref="Get"/> 命中路径，与 <c>AuctionCategoryResolver.PrefillSlots</c> 同一套做法。
    /// 预置项覆盖同名真实模板（自测要的是确定的字段取值，不是现场 PVF 的内容）。
    /// </summary>
    internal void PrefillTemplates(IEnumerable<KeyValuePair<int, ItemTemplate>> prefilled)
    {
        lock (dropCandidatesGate)
        {
            // 枚举器中途失败也可能已覆盖部分模板，必须先失效，不能留下过期成功快照。
            dropCandidateSnapshot = default;
            foreach (var (id, template) in prefilled)
                templates[id] = template;
        }
    }
    public static byte Slot(string tag) => EquipmentSlots.TryGetValue(tag, out var slot) ? slot : throw new InvalidDataException("未知普通装备类型: " + tag);

    public bool ContainsEquipment(int id) => equipment.ContainsKey(id);

    public bool Contains(int id) => equipment.ContainsKey(id) || stackable.ContainsKey(id);

    public bool IsSkillTreeExpansion(int id) => id == 821 && stackable.TryGetValue(id, out var path) &&
        string.Equals(path, "cash/skilltree_plusitem.stk", StringComparison.OrdinalIgnoreCase);

    public TalismanDefinition? Talisman(int id)
    {
        if (talismans.TryGetValue(id, out var cached))
            return cached.Value;
        if (!equipment.TryGetValue(id, out var path) || Get(id).EquipmentSlot != EquipmentSlots["talisman"])
            return null;
        return talismans.GetOrAdd(id, key => new(() => TalismanDefinition.Parse(
            TalismanFile.Parse(key, "equipment/" + path, pvf.ReadTokens("equipment/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    public RuneDefinition? Rune(int id)
    {
        if (runes.TryGetValue(id, out var cached))
            return cached.Value;
        if (StackableDefinition(id)?.TypeDeclaration?.Label != "[rune]")
            return null;
        return runes.GetOrAdd(id, key => new(() => RuneDefinition.Parse(
            RuneFile.Parse(key, "stackable/" + stackable[key], pvf.ReadTokens("stackable/" + stackable[key]),
                pvf.ResolveString), Get(key)))).Value;
    }

    public TalismanRefineCost TalismanRefineCosts() => talismanRefineCost.Value;

    // 无PVF核心自测预置不可变定义；正式路径仍由对应资源名册解析。
    internal void PrefillTalismans(IEnumerable<TalismanDefinition> definitions)
    {
        foreach (var definition in definitions)
            talismans[definition.Item.Id] = new(() => definition);
    }

    internal void PrefillRunes(IEnumerable<RuneDefinition> definitions)
    {
        foreach (var definition in definitions)
            runes[definition.Item.Id] = new(() => definition);
    }

    public TalismanCompoundCost TalismanCompoundCosts() => talismanCompoundCost.Value;

    public RuneRandomChooseTable RuneRandomChoices() => runeRandomChoices.Value;

    // 合成产物池按品级/稀有度预建，首次命中才扫描资源；规划阶段在账号锁外执行，
    // 结果只读缓存，后续请求不再触碰 PVF。
    public IReadOnlyList<int> RuneCandidates(byte grade) =>
        runePool.Value.TryGetValue(grade, out var ids) ? ids : [];

    public IReadOnlyList<int> TalismanCandidates(int rarity) =>
        talismanPool.Value.TryGetValue(rarity, out var ids) ? ids : [];

    FrozenDictionary<byte, ImmutableArray<int>> BuildRunePool()
    {
        var rows = stackable.Where(row => row.Value.StartsWith("rune/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Key).ToArray();
        var found = new List<RuneDefinition>();
        try
        {
            found.AddRange(rows.AsParallel().AsOrdered().WithDegreeOfParallelism(PoolParallelism)
                .Select(row => Rune(row.Key)).Where(definition => definition is not null).Select(definition => definition!));
        }
        catch (AggregateException error) when (error.InnerExceptions.Count >= 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
            throw;
        }
        // 核心自测与预置定义不在路径册里，按已解析缓存补齐，避免正式路径与自测两套语义。
        found.AddRange(runes.Values.Where(value => value.IsValueCreated).Select(value => value.Value));
        return found.GroupBy(definition => definition.Grade)
            .ToFrozenDictionary(group => group.Key, group => group.Select(definition => definition.Item.Id)
                .Distinct().Order().ToImmutableArray());
    }

    FrozenDictionary<int, ImmutableArray<int>> BuildTalismanPool()
    {
        var rows = equipment.OrderBy(row => row.Key).ToArray();
        var found = new List<TalismanDefinition>();
        try
        {
            found.AddRange(rows.AsParallel().AsOrdered().WithDegreeOfParallelism(PoolParallelism)
                .Select(row => Talisman(row.Key)).Where(definition => definition is not null).Select(definition => definition!));
        }
        catch (AggregateException error) when (error.InnerExceptions.Count >= 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
            throw;
        }
        found.AddRange(talismans.Values.Where(value => value.IsValueCreated).Select(value => value.Value));
        return found.Where(definition => !definition.IsPlaceholder)
            .GroupBy(definition => definition.Item.Rarity)
            .ToFrozenDictionary(group => group.Key, group => group.Select(definition => definition.Item.Id)
                .Distinct().Order().ToImmutableArray());
    }

    static int PoolParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    // 公会合成产物池：只按两族在 PVF 里的固定目录取候选，顺序沿用 .lst 首次出现顺序。
    // 不能按路径包含 "flag" 粗筛——武器、时装里也有同名字段，会把非勋章物品带进随机池。
    // 候选数量小（勋章 59、守护珠 52），逐项过模板缓存即可，不另建索引。
    public IReadOnlyList<int> GuildCompoundCandidates(ItemKind kind, int rarity)
    {
        var source = kind == ItemKind.GuildMedal ? equipment : stackable;
        string prefix = kind == ItemKind.GuildMedal ? "character/common/flag/" : "flaggem/";
        var candidates = new List<int>();
        foreach (var (id, path) in source)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var template = Get(id);
            if (template.Kind == kind && template.Rarity == rarity)
                candidates.Add(id);
        }
        return candidates;
    }

    // 在资源装配阶段枚举；正creation rate的普通道具完整解析，失败不得静默删除候选。
    // 逐项解析互不依赖（PVF 按位置读取、缓存与模板表均线程安全），有序并行后结果与串行一致；
    // 原先串行约占服务启动 4 秒。任一项失败仍以原异常终止装配。
    public IEnumerable<ItemTemplate> DropCandidates(bool includeSpecialEquipment = false) =>
        PositiveDropCandidates(null).Where(template => AcceptDropCandidate(template, includeSpecialEquipment)).ToImmutableArray();

    internal IEnumerable<ItemTemplate> DropCandidatesProfiled(ItemDropDiagnostics diagnostics) =>
        PositiveDropCandidates(diagnostics).Where(template => AcceptDropCandidate(template, false)).ToImmutableArray();

    // 同一归档/名册生命周期不变；只缓存成功解析的模板引用，普通与祭坛分别执行原过滤。
    // 失败不发布快照，下一次仍重新预筛；已有Get模板缓存和异常解包合同不变。
    ImmutableArray<ItemTemplate> PositiveDropCandidates(ItemDropDiagnostics? diagnostics)
    {
        lock (dropCandidatesGate)
        {
            if (!dropCandidateSnapshot.IsDefault)
            {
                diagnostics?.Capture(dropCandidateSnapshot, true);
                return dropCandidateSnapshot;
            }
            var rows = equipment.Concat(stackable).OrderBy(row => row.Key).ToArray();
            diagnostics?.Start(rows.Length);
            try
            {
                var result = rows.AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                    .Select(row => diagnostics is null ? DropCandidate(row.Key, row.Value) : ProfiledDropCandidate(row.Key, row.Value, diagnostics.Current)).Where(template => template is not null)
                    .Select(template => template!).ToImmutableArray();
                diagnostics?.Capture(result, false);
                dropCandidateSnapshot = result;
                return result;
            }
            catch (AggregateException error) when (error.InnerExceptions.Count >= 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
                throw;
            }
        }
    }

    internal bool AcceptDropCandidate(ItemTemplate template, bool includeSpecialEquipment) =>
        template.Kind is ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material or ItemKind.Profession or ItemKind.Talisman or ItemKind.Rune ||
        includeSpecialEquipment && equipment.ContainsKey(template.Id) && template.Kind is ItemKind.Avatar or ItemKind.Creature or ItemKind.CreatureEquipment;

    ItemTemplate? ProfiledDropCandidate(int id, string path, ItemDropDiagnostics.Worker worker)
    {
        try { return DropCandidate(id, path, worker); }
        finally { worker.Processed++; }
    }

    ItemTemplate? DropCandidate(int id, string path, ItemDropDiagnostics.Worker? worker = null)
    {
        if (id <= 2)
            return null;
        bool isEquipment = equipment.ContainsKey(id);
        // 缺失的premium STK是可选声明，不能被掉落预热重新变成启动失败。
        if (!isEquipment && premiumServiceItems.Contains(id) && !pvf.Contains("stackable/" + path))
            return null;
        var document = worker is null
            ? (isEquipment ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + path) : new PvfDocument(pvf.ReadText("stackable/" + path)))
            : (isEquipment ? EquipmentFile.ReadScriptProfiled(pvf.ReadText, "equipment/" + path, worker.Observe)
                : PvfDocumentReader.ReadProfiled(pvf.ReadText, "stackable/" + path, false, worker.Observe));
        if (worker is null) return ParsedDropCandidate(id, path, document);
        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        try { return ParsedDropCandidate(id, path, document); }
        finally
        {
            worker.TemplateTicks += Stopwatch.GetTimestamp() - started;
            worker.TemplateBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }

    // 预筛已经读完同一脚本/import链；正候选复用文档进入原single-flight，不保留额外全文缓存。
    internal ItemTemplate? ParsedDropCandidate(int id, string path, PvfDocument document)
    {
        bool isEquipment = equipment.ContainsKey(id);
        int rate = CreationRate(document);
        if (rate == 0)
            return null;
        // 跨名册同ID仍由equipment优先；当前row不是规范路径时沿用原Get重读，不能污染缓存。
        string? canonicalPath = isEquipment ? equipment[id] : stackable.GetValueOrDefault(id);
        return Get(id, string.Equals(path, canonicalPath, StringComparison.Ordinal) ? document : null);
    }

    internal static int CreationRate(PvfDocument document)
    {
        if (!document.Has("creation rate"))
            return 0;
        string value = document.Section("creation rate").Trim();
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int rate) || rate < 0)
            throw new InvalidDataException("物品creation rate必须是单个非负整数");
        return rate;
    }

    public ImmutableArray<CosmoPassCost> CosmoPasses() => cosmoPasses.Value
        .Where(row => stackable.ContainsKey(row.ItemId) && Get(row.ItemId).Kind == ItemKind.Material).ToImmutableArray();

    public IReadOnlyList<(int ItemId, int Count)>? NeededMaterials(int id)
    {
        if (StackableDefinition(id) is not { } raw) return null;
        if (raw.DeclaredNeededMaterials is not { } materials || materials.Count % 2 != 0) return null;
        var pairs = new (int ItemId, int Count)[materials.Count / 2];
        for (int index = 0; index < pairs.Length; index++)
        {
            int itemId = materials[index * 2], count = materials[index * 2 + 1];
            if (itemId <= 0 || count <= 0) return null;
            pairs[index] = (itemId, count);
        }
        return pairs;
    }

    public QuestTicketDefinition QuestTicket(int id) => questTickets.GetOrAdd(id, key =>
    {
        var raw = StackableDefinition(key) ?? throw new InvalidDataException("任务券不是堆叠物品: " + key);
        return QuestTicketDefinition.Parse(raw,Get(key));
    });

    public AdventureNameTicketDefinition? AdventureNameTicket(int id)
    {
        if (StackableDefinition(id) is not { } raw) return null;
        return AdventureNameTicketDefinition.Parse(raw, Get(id));
    }

    public LevelUpTicketDefinition? LevelUpTicket(int id)
    {
        if (!stackable.ContainsKey(id)) return null;
        return levelUpTickets.GetOrAdd(id, key => new(() => LevelUpTicketDefinition.Parse(
            StackableDefinition(key)!, Get(key)))).Value;
    }

    internal static string JobTag(byte job) => job < JobNames.Length ? "[" + JobNames[job] + "]" : throw new InvalidDataException("未知物品使用职业");

    public ExperienceConsumableDefinition? ExperienceConsumable(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return experienceConsumables.GetOrAdd(id, key => new(() => ExperienceConsumableDefinition.Parse(
            ExperienceConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    public PermanentGrowthDefinition? PermanentGrowth(int id)
    {
        if (!PermanentGrowthDefinition.IsNativeItem(id) || !stackable.TryGetValue(id, out var path)) return null;
        return permanentGrowth.GetOrAdd(id, key => new(() => PermanentGrowthDefinition.Parse(
            ExperienceConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    public LevelUpByTableDefinition? LevelUpByTable(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return levelUpTables.GetOrAdd(id, key => new(() => LevelUpByTableDefinition.Parse(
            ExperienceConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    public LegacyStatusConsumableDefinition? LegacyStatusConsumable(int id)
    {
        if (!LegacyStatusConsumableDefinition.IsNativeItem(id) || !stackable.TryGetValue(id, out var path)) return null;
        return legacyStatusConsumables.GetOrAdd(id, key => new(() => LegacyStatusConsumableDefinition.Parse(
            ExperienceConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    public FatigueConsumableDefinition? FatigueConsumable(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return fatigueConsumables.GetOrAdd(id, key => new(() => FatigueConsumableDefinition.Parse(
            RecoveryConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    public InventoryExpansionDefinition? InventoryExpansion(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return inventoryExpansions.GetOrAdd(id, key => new(() => InventoryExpansionDefinition.Parse(
            RecoveryConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    public StackableActionIdentity? StackableAction(int id) => StackableDefinition(id) is { } raw ? StackableActionCatalog.Read(raw) : null;

    // [coupon]首参数是商品映射表索引，不是折扣百分比或库存槽位。
    public int? CashShopCouponKey(int id) => StackableDefinition(id)?.TypeDeclaration is
        { Label: "[coupon]", Parameters.Count: 2 } coupon && coupon.Parameters[0] > 0 ? coupon.Parameters[0] : null;

    public GameWorld.Guilds.GuildMileageGiftDefinition? GuildMileageGift(int id) =>
        StackableDefinition(id) is { } raw ? GameWorld.Guilds.GuildMileageGiftDefinition.Parse(raw, Get(id)) : null;
    public GameWorld.Guilds.GuildExperienceGiftDefinition? GuildExperienceGift(int id) =>
        StackableDefinition(id) is { } raw ? GameWorld.Guilds.GuildExperienceGiftDefinition.Parse(raw, Get(id)) : null;

    readonly ConcurrentDictionary<int, Lazy<PartnerTeleportDefinition?>> partnerTeleports = new();
    public PartnerTeleportDefinition? PartnerTeleport(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return partnerTeleports.GetOrAdd(id, key => new(() => PartnerTeleportDefinition.Parse(
            key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString, Get(key)))).Value;
    }

    // 誓约之缚武器改造只认成对声明；[need material]是商店卖价，不参与使用成本。
    public EquipmentPeriodChangeDefinition? EquipmentPeriodChange(int id)
    {
        if (periodChanges.TryGetValue(id, out var prefilled)) return prefilled.Value;
        if (!stackable.TryGetValue(id, out var path)) return null;
        return periodChanges.GetOrAdd(id, key => new(() => EquipmentPeriodChangeDefinition.Parse(
            RecoveryConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString),
            Get(key)))).Value;
    }

    /// <summary>预置改造定义（**仅供核心自测**）：与 <see cref="PrefillTemplates"/> 同一套做法。</summary>
    internal void PrefillPeriodChanges(IEnumerable<KeyValuePair<int, EquipmentPeriodChangeDefinition>> prefilled)
    {
        foreach (var (id, definition) in prefilled)
            periodChanges[id] = new(definition);
    }

    public ActionConsumableDefinition? ActionConsumable(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return actionConsumables.GetOrAdd(id, key => new(() => ActionConsumableDefinition.Parse(
            RecoveryConsumableFile.Parse(key, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString), Get(key)))).Value;
    }

    StackableFile? StackableDefinition(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return stackableDefinitions.GetOrAdd(id, key => new(() => StackableFile.Parse(key, "stackable/" + path,
            pvf.ReadTokens("stackable/" + path), pvf.ResolveString))).Value;
    }

    public ClassChangeTicketDefinition? ClassChangeTicket(int id)
    {
        if (!stackable.ContainsKey(id)) return null;
        return classChangeTickets.GetOrAdd(id, key => new(() =>
            ClassChangeTicketDefinition.Parse(StackableDefinition(key)!, Get(key)))).Value;
    }

    public GameWorld.Creatures.CreatureChangeDefinition? CreatureChange(int id)
    {
        if (!stackable.ContainsKey(id)) return null;
        return creatureChanges.GetOrAdd(id, key => new(() =>
            GameWorld.Creatures.CreatureChangeDefinition.Parse(StackableDefinition(key)!, Get(key)))).Value;
    }

    public GameWorld.Creatures.CreatureGrowthItemDefinition? CreatureGrowthItem(int id)
    {
        if (!stackable.ContainsKey(id)) return null;
        return creatureGrowthItems.GetOrAdd(id, key => new(() =>
            GameWorld.Creatures.CreatureGrowthItemDefinition.Parse(StackableDefinition(key)!, Get(key)))).Value;
    }

    // 只有唯一、完整的类型声明参与成宠资格；缺项和未知格式不按成宠0补齐。
    internal static int ReadCreatureSubType(PvfDocument document) =>
        document.TryGetInt32("sub type", out int subtype) && subtype is >= 0 and <= byte.MaxValue ? subtype : -1;

    // 仅接入已核对的城镇使用动作；未知地点和发包模式不按同名券推定。
    internal static bool ReadCreatureSkinTicket(PvfDocument document) =>
        StackableActionDeclaration.TryRead(document, out var action) &&
        StackableActionCatalog.Arguments(action) is { Label: "[open creature skin slot]", Parameters.Count: 0 } &&
        document.TryGetString("action usable place", out string place) && place == "[village]" &&
        document.TryGetInt32("use action packet", out int packet) && packet == 0;

    internal static bool ReadAuraSkinAction(PvfDocument document) =>
        StackableActionDeclaration.TryRead(document, out var action) &&
        StackableActionCatalog.Arguments(action) is { Label: "[open aura skin slot]", Parameters.Count: 0 } &&
        document.TryGetString("action usable place", out string place) && place == "[village]" &&
        document.TryGetInt32("use action packet", out int packet) && packet == 0;

    ItemTemplate Load(int id, PvfDocument? parsed)
    {
        bool isEquipment = equipment.TryGetValue(id, out string? path);
        if (!isEquipment && !stackable.TryGetValue(id, out path))
            throw new InvalidDataException("PVF 物品不存在: " + id);
        var text = parsed ?? (isEquipment ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + path) :
            new PvfDocument(pvf.ReadText("stackable/" + path)));
        string stackableType = isEquipment ? "" : ReadStackableType(pvf.ReadText("stackable/" + path));
        byte slot = 0;
        ItemKind kind;
        if (isEquipment)
        {
            string type = Regex.Match(text.Section("equipment type"), @"\[([^\]]+)\]").Groups[1].Value;
            slot = Slot(type);
            kind = type.Contains("avatar", StringComparison.OrdinalIgnoreCase) ? ItemKind.Avatar :
                slot == 26 ? ItemKind.Creature : slot is >= 27 and <= 29 ? ItemKind.CreatureEquipment :
                slot == 32 ? ItemKind.GuildMedal : type == "talisman" ? ItemKind.Talisman : ItemKind.Equipment;
        }
        else
        {
            string type = Regex.Match(stackableType, @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant();
            if (type.Length == 0)
            {
                // 品级箱等只有 action type、没有 stackable type。没有动作声明的仍留给专用容器。
                if (!text.Has("action type"))
                    throw new InvalidDataException("该物品需要独立特殊容器: " + id);
                kind = ItemKind.Consumable;
            }
            else
            {
                kind = type == "rune" ? ItemKind.Rune : type == "flag gem" ? ItemKind.GuardianGem :
                    type is "creature" or "feed" ? ItemKind.CreatureConsumable : type == "avatar emblem" ? ItemKind.Emblem :
                    type == "material expert job" ? ItemKind.Profession :
                    type == "quest" || type.StartsWith("quest ", StringComparison.Ordinal) ? ItemKind.Quest :
                    type == "material" || type.StartsWith("material ", StringComparison.Ordinal) ? ItemKind.Material : ItemKind.Consumable;
            }
        }

        string jobs = text.Section("usable job");
        ushort jobMask = ushort.MaxValue;
        // [account] 是账号级物品的可用职业声明（如新版附魔废料），不限职业。
        if (jobs.Length != 0 && !jobs.Contains("[all]", StringComparison.OrdinalIgnoreCase) && !jobs.Contains("[account]", StringComparison.OrdinalIgnoreCase))
        {
            jobMask = 0;
            foreach (Match tag in Regex.Matches(jobs, @"\[([^\]]+)\]"))
            {
                int job = Array.IndexOf(JobNames, tag.Groups[1].Value.ToLowerInvariant());
                if (job < 0)
                    throw new InvalidDataException("未知可用职业: " + tag.Groups[1].Value);
                jobMask |= (ushort)(1 << job);
            }
        }

        int minimum = Int("minimum level", isEquipment ? 1 : 0), durability = Int("durability", 0);
        int maximum = int.MaxValue;
        if (text.Has("maximum level"))
        {
            if (isEquipment)
            {
                if (!text.TryGetInt32("maximum level", out maximum) || maximum < minimum)
                    throw new InvalidDataException("PVF物品最高等级声明无效: " + id);
            }
            else
            {
                // 次元能量等把目标装备条件嵌在子块中，不能把目标上限当作角色使用上限。
                var root = new PvfTokenDocument(pvf.ReadTokens("stackable/" + path), pvf.ResolveString).Sections
                    .Where(section => section.Name == "maximum level").ToArray();
                if (root.Length != 0)
                {
                    if (root is not [{ Values: [{ Type: 0 } limit] }] || limit.Value < minimum)
                        throw new InvalidDataException("PVF物品最高等级声明无效: " + id);
                    maximum = limit.Value;
                }
            }
        }
        int baseValue = Int("value", -1);
        if (baseValue < 0)
            baseValue = Int("price", 0);
        int stackLimit = isEquipment ? 1 : Int("stack limit", 0);
        if (stackLimit <= 0)
            stackLimit = int.MaxValue;
        if (kind == ItemKind.Rune)
            stackLimit = RuneDefinition.StackLimit;
        // 0 是 stackable.lst 里的金币占位，不是空槽。空槽不调用 Get。
        if (id < 0 || minimum is < 0 or > 255 || durability is < 0 or > ushort.MaxValue)
            throw new InvalidDataException("无效 PVF 物品定义: " + id);
        string impossible = text.Block("impossible contents");
        string name = text.Section("name").Trim('`', ' ', '\r', '\n');
        ushort cargoTarget = 0;
        if (!isEquipment && name.Contains("金库", StringComparison.Ordinal) &&
            !name.Contains("帐号", StringComparison.Ordinal) && !name.Contains("账号", StringComparison.Ordinal) &&
            (name.Contains("升级", StringComparison.Ordinal) || path!.Contains("safe_upgradekit", StringComparison.OrdinalIgnoreCase)))
        {
            // 目标容量来自工具说明；同名道具可能直接升到不同档位，不能一律加16格。
            var match = Regex.Match(text.Section("explain"), @"(?:增加到|升级至|升级到|升级为)\s*(\d+)\s*格");
            if (match.Success)
                cargoTarget = checked((ushort)int.Parse(match.Groups[1].Value));
            // 旧银质工具的32格声明经兼容约定映射到本版40格，不映射其他表外容量。
            if (cargoTarget == 32) cargoTarget = 40;
        }
        return new(id, kind, slot, (byte)minimum, (ushort)durability, stackLimit,
            Int("weight", 0), Int("rarity", 0), Int("grade", 0), text.Section("attach type"),
            jobMask, checked((ushort)Int("required skill", 0)),
            name, stackableType.Replace("`", "").Trim())
        {
            IsPremiumService = premiumServiceItems.Contains(id) || !isEquipment && IsPremiumStackable(text) || cargoTarget > 0 || acquisitionCargo.ContainsKey(id),
            AcquisitionContract = acquisitionContracts.GetValueOrDefault(id),
            AcquisitionAccountCargo = acquisitionCargo.GetValueOrDefault(id),
            OpensCreatureSkinSlot = !isEquipment && ReadCreatureSkinTicket(text),
            OpensAuraSkinSlot = !isEquipment && id == 490700411 && kind == ItemKind.Material && ReadAuraSkinAction(text),
            WeaponSubType = kind == ItemKind.Equipment && slot == 12 &&
                text.TryGetInt32("sub type", out int weaponSubType) && weaponSubType >= 0 ? weaponSubType : -1,
            CreatureSubType = kind == ItemKind.Creature ? ReadCreatureSubType(text) : -1,
            CreatureInitialMode = kind == ItemKind.Creature && creatures?.Get(id) is { Egg: false, UsesExperienceItems: true } ? (byte)1 : (byte)0,
            PersonalCargoForbidden = impossible.Contains("charac cargo", StringComparison.OrdinalIgnoreCase),
            AccountCargoForbidden = impossible.Contains("account cargo", StringComparison.OrdinalIgnoreCase),
            AccountBoundAfterTrade = !isEquipment && StackableTradeLimitDeclaration.Read(text) is { } tradeLimit &&
                tradeLimit.Attachment.Equals("[account]", StringComparison.OrdinalIgnoreCase),
            PersonalCargoUpgradeTarget = cargoTarget,
            ShopPrice = ItemShopPrice.Parse(text, isEquipment, equipmentSellRate),
            Expiration = ItemExpiration.Parse(text),
            RefinementTicket = isEquipment ? null : RefinementTicketDefinition.Parse(text),
            CharacterSlotMaximum = name == "角色栏扩展券" &&
                int.TryParse(Regex.Match(text.Section("explain"), @"总上限为\s*(\d+)").Groups[1].Value, out int maxSlots) ? maxSlots : 0,
            MaximumLevel = maximum,
            AvatarExpansionStage = name.Contains("装扮栏扩展券", StringComparison.Ordinal) &&
                Regex.Match(text.Section("explain"), @"扩展到\s*(\d+)\s*阶段") is { Success: true } stageMatch
                ? int.Parse(stageMatch.Groups[1].Value) : 0,
            QualityBox = isEquipment ? null : EquipmentQualityBox.Parse(id, text),
            AvatarAbilityChange = isEquipment || !text.Has("avatar select ability change") ? null :
                AvatarAbilityChangeDefinition.Parse(new PvfTokenDocument(pvf.ReadTokens("stackable/" + path), pvf.ResolveString), pvf.ResolveString),
            ResealConsumable = isEquipment ? null : EquipmentResealConsumable.Parse(path!, text),
            GrowthEquipment = isEquipment && (text.Has("level section ani") || !string.IsNullOrWhiteSpace(text.Section("level linear ability")) ||
                PvfScript.Integers(text.Section("equipment grow")) is [> 0, > 0]),
            RandomOptionEquipment = isEquipment && text.Has("random option"),
            PeriodEquipment = isEquipment && (PvfScript.Integers(text.Section("usable period")).Any(value => value > 0) ||
                PvfScript.Integers(text.Section("expiration date")).Any(value => value > 0)),

            AvatarSockets = kind == ItemKind.Avatar ? ReadAvatarSockets(text) : default,
            CloneAvatar = kind == ItemKind.Avatar && (Int("clear avatar", 0) == 1 || text.ContainsString("item category", "clear avatar")),
            BorrowAvatarAppearance = kind == ItemKind.Avatar,
            AvatarGiftType = isEquipment ? null : AvatarTypeSelectionFile.GiftType(text),
            EmblemTargetSockets = kind == ItemKind.Emblem ? ReadEmblemTargetSockets(text) : (ushort)0,
            GuardianGemEffects = kind == ItemKind.GuardianGem ? ReadGuardianGemEffects(text) : [],
            ExperiencePercent = isEquipment ? ReadExperiencePercent(text) : 0,
            ExperienceForbiddenModules = Regex.Matches(text.Block("impossible equip module"), @"`([^`]+)`")
                .Select(match => match.Groups[1].Value.Trim().Trim('[', ']')).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            CreatureEgg = kind == ItemKind.Creature && creatures?.Get(id)?.Egg == true,
            RepairPrice = isEquipment ? Int("repair price", 0) : 0,
            CreationRate = CreationRate(text),
            Echon = isEquipment && text.TryGetString("custom ability type", out string abilityType) &&
                abilityType is "warriors dungeon" or "new warriors dungeon" ? ReadEchon(id, text, abilityType) : null,
            PvpFreeEquipment = isEquipment && text.Has("pvp free equip"),
            DisjointForbidden = impossible.Contains("disjoint", StringComparison.OrdinalIgnoreCase),
            IsOperationHope = isEquipment && text.Has("operation hope"),
            BossDrop = isEquipment && text.ContainsString("item category", "boss drop"),
            IsMonsterCard = !isEquipment && text.ContainsString("item category", "monster card"),
            MonsterCardBindType = ReadMonsterCardBindType(isEquipment, text),
            AmplifyPollutionForbidden = isEquipment && text.ContainsString("impossible contents", "pollution"),
            UseForbidden = ForbidsUse(impossible),
            // 分解采用模板价值及基础出售倍率，不使用NPC售出时的耐久和90%折价。
            DisjointValue = isEquipment ? (int)Math.Clamp((long)baseValue * equipmentSellRate / 1000, 1, int.MaxValue) : 0,
            PortableDisjointLevel = Int("portable disjoint", -1) switch { 0 => 30, 1 => 50, 2 => 70, 3 => 85, 4 => 95, _ => -1 }
        };

        int Int(string tag, int fallback) => Integers(text.Section(tag)).FirstOrDefault(fallback);
    }

    static int? ReadMonsterCardBindType(bool isEquipment, PvfDocument text)
    {
        if (isEquipment)
            return null;
        int[] values = Integers(text.Section("monstercard bind"));
        if (values.Length == 0)
            return null;
        if (values is not [>= 0 and <= 5])
            throw new InvalidDataException("怪物卡片合成器类型无效");
        return values[0];
    }

    EchonEquipmentDefinition ReadEchon(int id, PvfDocument document, string type)
    {
        if (type == "new warriors dungeon")
        {
            if (newEchon is null || !document.TryGetInt32("custom ability count", out int abilityCount) || abilityCount != 2)
                throw new InvalidDataException("新版斗气装备缺少已装配的能力资源");
            var raw = new PvfTokenDocument(pvf.ReadTokens("equipment/" + equipment[id]), pvf.ResolveString);
            var fixedOptions = raw.Sections.Where(row => row.Name == "fix custom ability").ToArray();
            if (!raw.Diagnostics.IsEmpty || fixedOptions.Length != 2 || fixedOptions.Any(row => row.IsBlock ||
                    row.Values.Length != 2 || row.Values.Any(value => value.Type != 0) ||
                    row.Values[0].Value is < 0 or > 1 || row.Values[1].Value is <= 0 or > 255 ||
                    !newEchon.Options.Contains((byte)row.Values[1].Value)) ||
                fixedOptions.Select(row => row.Values[0].Value).Distinct().Count() != 2)
                throw new InvalidDataException("新版斗气装备初始能力无效");
            byte Fixed(int position) => checked((byte)fixedOptions.Single(row => row.Values[0].Value == position).Values[1].Value);
            return new(0, document.TryGetInt32("sub type", out int material) ? material : -1, newEchon, Fixed(0), Fixed(1));
        }
        if (echon is null || !document.TryGetInt32("custom ability count", out int count) || count != 2 ||
            !document.TryGetInt32("custom ability part set index", out int index) || index is <= 0 or > 255 ||
            !echon.Sets.ContainsKey((byte)index)) throw new InvalidDataException("艾肯装备缺少已装配的能力资源");
        return new((byte)index, document.TryGetInt32("sub type", out int armor) ? armor : -1, echon);
    }

    internal static decimal ReadExperiencePercent(PvfDocument document)
    {
        if (!document.Has("exp advantage")) return 0;
        if (!decimal.TryParse(document.Section("exp advantage").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out decimal value))
            throw new InvalidDataException("装备exp advantage必须为非负百分比");
        return Math.Max(0, value);
    }

    // `enable` 是限制名单的开关，不是“禁止使用”。只有开关打开且没有具体内容时才禁止使用；
    // 名单里仅有 `hardcore mode` 时，普通角色仍可使用（远古精灵秘药 7377/7429/7440）。
    internal static bool ForbidsUse(string impossible)
    {
        var tags = Regex.Matches(impossible, @"`([^`]+)`").Select(match => match.Groups[1].Value).ToArray();
        if (!tags.Any(tag => tag.Equals("enable", StringComparison.OrdinalIgnoreCase))) return false;
        var restrictions = tags.Where(tag => !tag.Equals("enable", StringComparison.OrdinalIgnoreCase)).ToArray();
        return restrictions.Length == 0 || restrictions.Any(tag => !tag.Equals("hardcore mode", StringComparison.OrdinalIgnoreCase));
    }

    // 客户端依次覆盖堆叠类型；公会硬币先声明material、后声明quest，以最后声明决定分页。
    internal static string ReadStackableType(string script) =>
        Regex.Matches(script, @"^\[stackable type\](.*?)(?=\r?\n\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .LastOrDefault()?.Groups[1].Value.Trim() ?? "";

    internal static bool IsPremiumStackable(PvfDocument text) =>
        Regex.Match(text.Section("stackable type"), @"\[([^\]]+)\]").Groups[1].Value
            .Equals("contract", StringComparison.OrdinalIgnoreCase);

    internal static ImmutableHashSet<string> ReadGuardianGemEffects(PvfDocument text) =>
        Regex.Matches(text.Block("enchant"), @"\[([^\]/]+)\]")
            .Select(match => match.Groups[1].Value.Trim()).Where(value => value.Length != 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    internal static ushort ReadEmblemTargetSockets(PvfDocument text)
    {
        string declaration = text.Block("avatar emblem target type");
        if (declaration.Length == 0)
            declaration = text.Section("avatar emblem target type");
        ushort mask = 0;
        foreach (Match match in Regex.Matches(declaration, @"\[\s*([ABCDSM])\s+socket\s*\]",
            RegexOptions.IgnoreCase))
        {
            mask |= char.ToUpperInvariant(match.Groups[1].Value[0]) switch
            {
                'A' => 1,
                'B' => 2,
                'C' => 4,
                'D' => 8,
                'S' => 16,
                'M' => 239,
                _ => 0
            };
        }
        return mask;
    }

    internal static AvatarSocketTypes ReadAvatarSockets(PvfDocument text)
    {
        var types = new ushort[5];
        int count = Math.Clamp(Integers(text.Section("avatar emblem socket num")).FirstOrDefault(5), 0, 5), index = 0;
        string declaration = text.Block("emblem socket default");
        // 特殊皮肤的孔型也可仅写在永久带孔类型中（如真·桐人、新春皮肤）；普通高级皮肤仍需开孔器。
        if (count > 0 && !text.Has("emblem socket default") && text.ContainsString("equipment type", "[skin avatar]") &&
            text.TryGetInt32("grade", out int grade) && grade >= 2)
        {
            var candidates = AvatarTypeSelectionFile.Parse(text).Where(row => row.Days == 0 && !row.Sockets.IsEmpty)
                .Select(row => string.Join(" ", row.Sockets)).Distinct(StringComparer.Ordinal).ToArray();
            if (candidates.Length > 1)
                throw new InvalidDataException("特殊皮肤的永久孔型声明冲突");
            if (candidates.Length == 1)
                declaration = candidates[0];
        }
        foreach (Match match in Regex.Matches(declaration, @"\[\s*([ABCDSM])\s+socket\s*\]", RegexOptions.IgnoreCase))
        {
            if (index >= count)
                break;
            types[index++] = char.ToUpperInvariant(match.Groups[1].Value[0]) switch
            { 'A' => 1, 'B' => 2, 'C' => 4, 'D' => 8, 'S' => 16, 'M' => 239, _ => 0 };
        }
        return new(types[0], types[1], types[2], types[3], types[4]);
    }
}
