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
    readonly ConcurrentDictionary<int, ItemTemplate> templates = new();
    readonly ConcurrentDictionary<int, Lazy<ItemTemplate>> pendingTemplates = new();
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
        // 耳环可作为普通装备入包；穿戴槽尚未核实，0不映射到任何已支持的穿戴位置。
        ["earring"] = 0
    };

    readonly int equipmentSellRate;

    public ItemCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable, int equipmentSellRate)
    {
        pvf = archive;
        this.equipment = equipment;
        this.stackable = stackable;
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
    public static byte Slot(string tag) => EquipmentSlots.TryGetValue(tag, out var slot) ? slot : throw new InvalidDataException("未知普通装备类型: " + tag);

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
            DisjointForbidden = impossible.Contains("disjoint", StringComparison.OrdinalIgnoreCase),
            // 分解采用模板价值及基础出售倍率，不使用NPC售出时的耐久和90%折价。
            DisjointValue = isEquipment ? (int)Math.Clamp((long)baseValue * equipmentSellRate / 1000, 1, int.MaxValue) : 0,
            PortableDisjointLevel = Int("portable disjoint", -1) switch { 0 => 30, 1 => 50, 2 => 70, 3 => 85, 4 => 95, _ => -1 }
        };

        int Int(string tag, int fallback) => Integers(text.Section(tag)).FirstOrDefault(fallback);
    }

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
