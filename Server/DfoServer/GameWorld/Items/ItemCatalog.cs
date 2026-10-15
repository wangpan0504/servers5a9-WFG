using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed class ItemCatalog
{
    readonly PvfArchive pvf;
    readonly IReadOnlyDictionary<int, string> equipment;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly IReadOnlySet<int> premiumServiceItems;
    readonly IReadOnlyDictionary<int, PremiumContract> acquisitionContracts;
    readonly ConcurrentDictionary<int, ItemTemplate> templates = new();
    readonly ConcurrentDictionary<int, Lazy<ItemTemplate>> pendingTemplates = new();
    readonly ConcurrentDictionary<int, QuestTicketDefinition> questTickets = new();
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
        ["earring"] = 25
    };

    readonly int equipmentSellRate;

    public ItemCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable, int equipmentSellRate, IReadOnlySet<int>? premiumServiceItems = null,
        IReadOnlyDictionary<int, PremiumContract>? acquisitionContracts = null)
    {
        pvf = archive;
        this.equipment = equipment;
        this.stackable = stackable;
        this.premiumServiceItems = premiumServiceItems ?? new HashSet<int>();
        this.acquisitionContracts = acquisitionContracts ?? new Dictionary<int, PremiumContract>();
        this.equipmentSellRate = equipmentSellRate;
    }

    public ItemTemplate Get(int id)
    {
        if (templates.TryGetValue(id, out var template))
            return template;
        var pending = pendingTemplates.GetOrAdd(id, key => new(() => templates.GetOrAdd(key, Load)));
        try { return pending.Value; }
        finally
        {
            // 并发首次请求共用解析；完成后只保留业务模板，失败不占缓存。
            pendingTemplates.TryRemove(new KeyValuePair<int, Lazy<ItemTemplate>>(id, pending));
        }
    }

    /// <summary>
    /// 预置模板（**仅供核心自测**）：核心自测没有 PVF，无法从资源取模板，因此把显式构造的模板直接灌进
    /// 同一个缓存。只填缓存、不改 <see cref="Load"/> 的解析逻辑——调用方走的仍是真实的
    /// <see cref="Get"/> 命中路径，与 <c>AuctionCategoryResolver.PrefillSlots</c> 同一套做法。
    /// 预置项覆盖同名真实模板（自测要的是确定的字段取值，不是现场 PVF 的内容）。
    /// </summary>
    internal void PrefillTemplates(IEnumerable<KeyValuePair<int, ItemTemplate>> prefilled)
    {
        foreach (var (id, template) in prefilled)
            templates[id] = template;
    }
    public static byte Slot(string tag) => EquipmentSlots.TryGetValue(tag, out var slot) ? slot : throw new InvalidDataException("未知普通装备类型: " + tag);

    // 在资源装配阶段枚举；正creation rate的普通道具完整解析，失败不得静默删除候选。
    public IEnumerable<ItemTemplate> DropCandidates()
    {
        foreach (var (id, path) in equipment.Concat(stackable).OrderBy(row => row.Key))
        {
            if (id <= 2)
                continue;
            bool isEquipment = equipment.ContainsKey(id);
            // 缺失的premium STK是可选声明，不能被掉落预热重新变成启动失败。
            if (!isEquipment && premiumServiceItems.Contains(id) && !pvf.Contains("stackable/" + path))
                continue;
            var document = isEquipment ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + path) :
                new PvfDocument(pvf.ReadText("stackable/" + path));
            int rate = CreationRate(document);
            if (rate == 0)
                continue;
            var template = Get(id);
            if (template.Kind is ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material or ItemKind.Profession)
                yield return template;
        }
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

    public QuestTicketDefinition QuestTicket(int id) => questTickets.GetOrAdd(id, key =>
    {
        if (!stackable.TryGetValue(key,out var path)) throw new InvalidDataException("任务券不是堆叠物品: " + key);
        var raw = StackableFile.Parse(key,"stackable/" + path,pvf.ReadTokens("stackable/" + path),pvf.ResolveString);
        return QuestTicketDefinition.Parse(raw,Get(key));
    });

    public AdventureNameTicketDefinition? AdventureNameTicket(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        var raw = StackableFile.Parse(id, "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString);
        return AdventureNameTicketDefinition.Parse(raw, Get(id));
    }

    // 只有唯一、完整的类型声明参与成宠资格；缺项和未知格式不按成宠0补齐。
    internal static int ReadCreatureSubType(PvfDocument document) =>
        document.TryGetInt32("sub type", out int subtype) && subtype is >= 0 and <= byte.MaxValue ? subtype : -1;

    ItemTemplate Load(int id)
    {
        bool isEquipment = equipment.TryGetValue(id, out string? path);
        if (!isEquipment && !stackable.TryGetValue(id, out path))
            throw new InvalidDataException("PVF 物品不存在: " + id);
        var text = isEquipment ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + path) :
            new PvfDocument(pvf.ReadText("stackable/" + path));
        byte slot = 0;
        ItemKind kind;
        if (isEquipment)
        {
            string type = Regex.Match(text.Section("equipment type"), @"\[([^\]]+)\]").Groups[1].Value;
            slot = Slot(type);
            kind = type.Contains("avatar", StringComparison.OrdinalIgnoreCase) ? ItemKind.Avatar :
                slot == 26 ? ItemKind.Creature : slot is >= 27 and <= 29 ? ItemKind.CreatureEquipment :
                slot == 32 ? ItemKind.GuildMedal : ItemKind.Equipment;
        }
        else
        {
            string type = Regex.Match(text.Section("stackable type"), @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant();
            if (type.Length == 0)
                throw new InvalidDataException("该物品需要独立特殊容器: " + id);
            kind = type == "flag gem" ? ItemKind.GuardianGem :
                type is "creature" or "feed" ? ItemKind.CreatureConsumable : type == "avatar emblem" ? ItemKind.Emblem :
                type == "material expert job" ? ItemKind.Profession :
                type == "quest" || type.StartsWith("quest ", StringComparison.Ordinal) ? ItemKind.Quest :
                type == "material" || type.StartsWith("material ", StringComparison.Ordinal) ? ItemKind.Material : ItemKind.Consumable;
        }

        string jobs = text.Section("usable job");
        ushort jobMask = ushort.MaxValue;
        if (jobs.Length != 0 && !jobs.Contains("[all]", StringComparison.OrdinalIgnoreCase))
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
        int baseValue = Int("value", -1);
        if (baseValue < 0)
            baseValue = Int("price", 0);
        int stackLimit = isEquipment ? 1 : Int("stack limit", 0);
        if (stackLimit <= 0)
            stackLimit = int.MaxValue;
        if (id <= 0 || minimum is < 0 or > 255 || durability is < 0 or > ushort.MaxValue)
            throw new InvalidDataException("无效 PVF 物品定义: " + id);
        string impossible = text.Block("impossible contents");
        string name = text.Section("name").Trim('`', ' ', '\r', '\n');
        ushort cargoTarget = 0;
        if (!isEquipment && name.Contains("金库", StringComparison.Ordinal) &&
            !name.Contains("帐号", StringComparison.Ordinal) && !name.Contains("账号", StringComparison.Ordinal) &&
            (name.Contains("升级", StringComparison.Ordinal) || path!.Contains("safe_upgradekit", StringComparison.OrdinalIgnoreCase)))
        {
            // 目标容量来自工具说明；同名道具可能直接升到不同档位，不能一律加16格。
            var match = Regex.Match(text.Section("explain"), @"(?:增加到|升级至|升级到)\s*(\d+)\s*格");
            if (match.Success)
                cargoTarget = checked((ushort)int.Parse(match.Groups[1].Value));
        }
        return new(id, kind, slot, (byte)minimum, (ushort)durability, stackLimit,
            Int("weight", 0), Int("rarity", 0), Int("grade", 0), text.Section("attach type"),
            jobMask, checked((ushort)Int("required skill", 0)),
            name, text.Section("stackable type").Replace("`", "").Trim())
        {
            IsPremiumService = premiumServiceItems.Contains(id) || !isEquipment && IsPremiumStackable(text),
            AcquisitionContract = acquisitionContracts.GetValueOrDefault(id),
            WeaponSubType = kind == ItemKind.Equipment && slot == 12 &&
                text.TryGetInt32("sub type", out int weaponSubType) && weaponSubType >= 0 ? weaponSubType : -1,
            CreatureSubType = kind == ItemKind.Creature ? ReadCreatureSubType(text) : -1,
            PersonalCargoForbidden = impossible.Contains("charac cargo", StringComparison.OrdinalIgnoreCase),
            AccountCargoForbidden = impossible.Contains("account cargo", StringComparison.OrdinalIgnoreCase),
            PersonalCargoUpgradeTarget = cargoTarget,
            ShopPrice = ItemShopPrice.Parse(text, isEquipment, equipmentSellRate),
            Expiration = ItemExpiration.Parse(text),
            RefinementTicket = isEquipment ? null : RefinementTicketDefinition.Parse(text),
            AvatarSockets = kind == ItemKind.Avatar ? ReadAvatarSockets(text) : default,
            CloneAvatar = Int("clear avatar", 0) == 1 || text.Section("item category").Contains("clear avatar", StringComparison.OrdinalIgnoreCase),
            BorrowAvatarAppearance = kind == ItemKind.Avatar && slot != 9,
            GuardianGemEffects = kind == ItemKind.GuardianGem ? ReadGuardianGemEffects(text) : [],
            RepairPrice = isEquipment ? Int("repair price", 0) : 0,
            CreationRate = CreationRate(text),
            DisjointForbidden = impossible.Contains("disjoint", StringComparison.OrdinalIgnoreCase),
            UseForbidden = Regex.Matches(impossible, @"`([^`]+)`").Any(match => match.Groups[1].Value.Equals("enable", StringComparison.OrdinalIgnoreCase)),
            // 分解采用模板价值及基础出售倍率，不使用NPC售出时的耐久和90%折价。
            DisjointValue = isEquipment ? (int)Math.Clamp((long)baseValue * equipmentSellRate / 1000, 1, int.MaxValue) : 0,
            PortableDisjointLevel = Int("portable disjoint", -1) switch { 0 => 30, 1 => 50, 2 => 70, 3 => 85, 4 => 95, _ => -1 }
        };

        int Int(string tag, int fallback) => Integers(text.Section(tag)).FirstOrDefault(fallback);
    }

    internal static bool IsPremiumStackable(PvfDocument text) =>
        Regex.Match(text.Section("stackable type"), @"\[([^\]]+)\]").Groups[1].Value
            .Equals("contract", StringComparison.OrdinalIgnoreCase);

    internal static ImmutableHashSet<string> ReadGuardianGemEffects(PvfDocument text) =>
        Regex.Matches(text.Block("enchant"), @"\[([^\]/]+)\]")
            .Select(match => match.Groups[1].Value.Trim()).Where(value => value.Length != 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    internal static AvatarSocketTypes ReadAvatarSockets(PvfDocument text)
    {
        var types = new ushort[5];
        int count = Math.Clamp(Integers(text.Section("avatar emblem socket num")).FirstOrDefault(5), 0, 5), index = 0;
        foreach (Match match in Regex.Matches(text.Block("emblem socket default"), @"\[\s*([ABCDSM])\s+socket\s*\]", RegexOptions.IgnoreCase))
        {
            if (index >= count)
                break;
            types[index++] = char.ToUpperInvariant(match.Groups[1].Value[0]) switch
            { 'A' => 1, 'B' => 2, 'C' => 4, 'D' => 8, 'S' => 16, 'M' => 239, _ => 0 };
        }
        return new(types[0], types[1], types[2], types[3], types[4]);
    }
}
