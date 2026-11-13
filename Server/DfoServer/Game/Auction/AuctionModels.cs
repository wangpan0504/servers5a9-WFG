using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Auction;

/// <summary>拍卖行搜索结果排序方式。</summary>
internal enum AuctionSortOrder : byte
{
    /// <summary>默认按挂牌 id 升序稳定排序。</summary>
    Default = 0,
    /// <summary>按当前竞拍/显示价升序。</summary>
    PriceAsc = 1,
    /// <summary>按当前竞拍/显示价降序。</summary>
    PriceDesc = 2,
    /// <summary>按一口价总额升序（纯竞拍排后）。</summary>
    BuyoutAsc = 3,
    /// <summary>按一口价总额降序（纯竞拍排后）。</summary>
    BuyoutDesc = 4,
    /// <summary>按到期时间升序。</summary>
    ExpiresAsc = 5,
    /// <summary>按到期时间降序。</summary>
    ExpiresDesc = 6
}

/// <summary>
/// 搜索请求（已解析的字段）。CMD 186（按道具搜索）与 CMD 187（分类搜索）共用：
/// 只有按道具搜索带 itemId，分类搜索的 itemId 为 0。
///
/// <see cref="CategoryId"/> 是客户端分类树节点 id。解析与过滤规则见 <see cref="AuctionCategoryFilter"/>：
/// 顶层大类与**已对账的子叶**按精确 id 过滤，未对账的子叶按所属顶层大类过滤（不是不过滤），
/// 完全没见过的 id 才不过滤。
/// </summary>
internal sealed record AuctionSearchRequest(int Offset, int? ItemTemplateId, int CategoryId)
{
    /// <summary>CMD 186 的完整候选模板；default 沿用单模板入口，空数组表示没有命中候选。</summary>
    public ImmutableArray<int> ItemTemplateIds { get; init; }
    public AuctionSortOrder SortOrder { get; init; }
    public byte? Rarity { get; init; }
    public byte MinLevel { get; init; }
    public byte MaxLevel { get; init; }
    public byte MinUpgrade { get; init; }
    public byte MaxUpgrade { get; init; } = 31;

    public bool HasAttributeFilter =>
        Rarity is < 7 || MinLevel > 0 || (MaxLevel > 0 && MaxLevel < 95) || MinUpgrade > 0 || (MaxUpgrade > 0 && MaxUpgrade < 31);
}

/// <summary>
/// 搜索过滤条件，全部是 SQL 能表达的条件。<see cref="Kinds"/> 为空表示不按物品大类过滤。
/// 装备部位**不在这里**：表里没有部位列，无法下推到 SQL，由 <see cref="AuctionService.Search"/>
/// 在内存里补做（见 <see cref="AuctionCategoryResolver"/>）。
///
/// 分类过滤必须**同时**作用在计数与本页数据上：客户端按「匹配总数」算总页数并直接显示「搜索出 N 个」，
/// 只过滤本页会让总数偏大、页数虚高。因此内存过滤在分页**之前**完成，匹配总数取过滤后的条数。
///
/// 可选排除按卖家角色id；正式搜索保留自己的挂牌，底层排除入口仍保留，NULL机器人不被排除。
/// </summary>
internal readonly record struct AuctionSearchFilter(int? ItemTemplateId, long? ExcludeSellerCharacterId,
    ImmutableArray<byte> Kinds = default, bool? Gold = null, ImmutableArray<int> ItemTemplateIds = default);

/// <summary>分类 id 的解析结果。不过滤不等于失败，日志必须能区分下面三种情形。</summary>
internal enum AuctionCategoryStatus
{
    /// <summary>识别为顶层大类或已确认的子叶：按下面解析出的维度过滤。</summary>
    Mapped,

    /// <summary>已识别的「全部」节点（40119）：本来就该返回全部，不是未映射。</summary>
    All,

    /// <summary>金币寄售分类 40101–40117：只检索金币券，不套用普通拍卖的物品维度。</summary>
    Gold,

    /// <summary>
    /// 未识别的分类 id：**不过滤**。
    /// 两种情形都会落到这里：① 根本不在已采集的 id 列表里；② 在列表里，但**所属顶层大类的子叶维度尚未对账**
    /// （武器、防具、特殊装备、设计图），此时按所属顶层大类过滤，<see cref="AuctionCategoryResolution.Note"/> 说明原因。
    /// </summary>
    Unmapped
}

/// <summary>
/// 分类 id 的解析结果。<see cref="Dimensions"/> 只在 <see cref="AuctionCategoryStatus.Mapped"/> 时有意义。
/// <see cref="MappedId"/> 是命中的顶层大类 id（子叶命中时报它所属的顶层大类，便于按大类聚合核对）。
/// <see cref="Note"/> 是本次解析的取舍说明，直接进日志，让「点了哪个节点 / 为什么不过滤」可逐次追溯。
/// </summary>
internal sealed record AuctionCategoryResolution(int CategoryId, AuctionCategoryStatus Status, int MappedId,
    string? Name, AuctionCategoryDimensions Dimensions, string? Note = null);

/// <summary>
/// 分类过滤的维度：<see cref="Kinds"/> 是物品大类（<c>ItemKind</c>，u8，可下推 SQL），
/// <see cref="Slots"/> 是装备部位（<c>ItemTemplate.EquipmentSlot</c>，u8，只能内存过滤），
/// <see cref="Groups"/> 是武器族 / 防具材质族（按模板 id 的族号判定，见 <see cref="AuctionCategoryFilter.ItemGroupMask"/>），
/// <see cref="Jobs"/> 是武器适用的职业（<c>ItemTemplate.Jobs</c> 的位掩码）。
/// 位掩码可同时表达「若干大类」「若干部位」「若干族」「若干职业」。
/// </summary>
internal sealed record AuctionCategoryDimensions(ulong Kinds, ulong Slots, ulong Groups = 0, ushort Jobs = 0)
{
    public ImmutableArray<byte> KindList => ToList(Kinds);
    public ImmutableArray<byte> SlotList => ToList(Slots);

    /// <summary>位掩码按位升序展开成值列表。</summary>
    public static ImmutableArray<byte> ToList(ulong mask)
    {
        var values = ImmutableArray.CreateBuilder<byte>();
        for (int bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) != 0)
                values.Add((byte)bit);
        }
        return values.ToImmutable();
    }
}

/// <summary>
/// 客户端分类 id → 服务端过滤维度的映射表。**顶层大类 + 已对账的子叶**都按精确 id 命中。
///
/// 数值沿用参考实现的分类 id 空间（见 <c>_review/06-拍卖行协议参考-S4A21.md</c> §7），
/// 但**本仓库客户端（D95）的子叶 id 是十进制、与参考表的十六进制逐个对不上**，因此子叶只认两类证据：
///   1. 本机运行实例日志里**客户端实际发出的 id**（140 个，见 <see cref="CollectedIds"/>）；
///   2. 该 id 在客户端分类树里的**层级位置**能被日志顺序与十进制结构复现出来。
/// 结构可从采集数据直接读出（这是子叶映射的依据，不是猜测）：
///   客户端分类树的十进制 id = <c>顶层anchor + 层级序号</c>：父节点是 <c>…N00</c>，直接子节点从 <c>…N01</c> 起递增。
///   实测证据：`12001` 是首饰父节点 `12000` 的第 1 个子节点，而 D95 抓包确认 `12001` = 项链
///   （187 请求体 `[5..6]` 就是 `0x2EE1`），父节点 `12000` 的序号 `00` 不被任何子节点占用。
///   同一父节点下的显示顺序按子类型升序，与参考实现的叶子列表顺序一致。
///
/// 三类分支的取舍：
///   - **顶层大类 / 已对账子叶** → 过滤。维度取不到时只按物品大类过滤（宁可粗，不可空）。
///   - **40119（全部）** → 不过滤。它是「全部」节点，本来就要返回全部匹配。
///   - **未识别的 id** → 不过滤，并记 <see cref="AuctionCategoryStatus.Unmapped"/> 日志。
///     参考实现对未映射 id 返回空列表，但那是它对自家分类表**已定案**的做法；本仓库的武器/防具/特殊装备/
///     设计图子叶**维度顺序未对账**（详见 <see cref="Unreconciled"/>），一旦猜错就会让界面显示错的子集，
///     且与客户端「总数为 0 时不刷新页数」的现象混在一起，无法分辨。宁可先不过滤，并在日志里暴露 id。
/// </summary>
internal static class AuctionCategoryFilter
{
    public const int All = 40119; // 0x9CB7「全部」

    // 顶层大类 id（十进制，十六进制见行内注释），与参考实现的分类树根节点一一对应。
    const int Weapon = 10000;        // 0x2710
    const int Armor = 11000;         // 0x2AF8
    const int Accessory = 12000;     // 0x2EE0
    const int Title = 12004;         // 0x2EE4
    const int Consumable = 13000;    // 0x32C8
    const int Creature = 14000;      // 0x36B0
    const int Blueprint = 31000;     // 0x7918「设计图」，参考表标注「当前 PVF 无设计图数据，已作废」
    const int SpecialEquipment = 32000; // 0x7D00
    const int SupportEquipment = 32001; // 0x7D01 辅助装备
    const int MagicStone = 32100;       // 0x7D64 魔法石
    const int Earring = 32200;          // 0x7DC8 耳环
    const int ExpertJob = 33000;     // 0x80E8
    const int Other = 34000;         // 0x84D0

    // 已对账的子叶 id。命名规则：<顶层名>_<子叶名>，取值就是客户端实发的那一个十进制 id。
    const int Consumable_Normal = 13001;   // 0x32C9 消耗品
    const int Consumable_Material = 13002; // 0x32CA 材料
    const int Consumable_Throw = 13003;    // 0x32CB 投掷设置
    const int Consumable_Gacha = 13004;    // 0x32CC 抽取
    const int Consumable_Quest = 13005;    // 0x32CD 任务
    const int Consumable_Misc = 13006;     // 0x32CE 其它
    const int Creature_Red = 14001;        // 0x36B1 红色宠物装备
    const int Creature_Blue = 14002;       // 0x36B2 蓝色宠物装备
    const int Creature_Green = 14003;      // 0x36B3 绿色宠物装备
    const int Accessory_Amulet = 12001;    // 0x2EE1 项链（D95 抓包实证）
    const int Accessory_Ring = 12002;      // 0x2EE2 戒指
    const int Accessory_Wrist = 12003;     // 0x2EE3 手镯
    const int ExpertJob_Alchemist = 33001; // 0x80E9 炼金术师
    const int ExpertJob_Doll = 33002;      // 0x80EA 控偶师
    const int ExpertJob_Enchanter = 33003;// 0x80EB 附魔师
    const int ExpertJob_Material = 33004;  // 0x80EC 副职材料

    // 装备部位区间 → 物品种类：0–11 时装、12 武器、13 称号、14–18 防具、19–25 首饰、26 宠物、
    // 27–29 宠物装备、32 公会勋章。用于「按部位过滤时要保留哪些物品大类」（SQL 侧）的判断。
    static readonly (int First, int Last, ItemKind Kind)[] EquipmentSlotKinds =
    [
        (0, 11, ItemKind.Avatar),
        (12, 12, ItemKind.Equipment),
        (13, 13, ItemKind.Equipment),
        (14, 18, ItemKind.Equipment),
        (19, 25, ItemKind.Equipment),
        (26, 26, ItemKind.Creature),
        (27, 29, ItemKind.CreatureEquipment),
        (32, 32, ItemKind.GuildMedal)
    ];

    /// <summary>武器族 = 装备模板 id 的前 3 位数字（<c>id / 1000000</c>）对应的 8 个职业家族。</summary>
    public static class WeaponGroup
    {
        // 类别 id 与物品族号差 1，且族号不连续（103/105/107 在本机 PVF 里没有任何武器条目）：
        // 实测每个族的武器条目只出现在 101/102/104/106/108/109/114/116，正好 8 个族、各 4–5 个物品组。
        public const int Swordman = 101;   // 鬼剑士/黑暗武士（组 ssword/katana/club/lswd/beamswd）
        public const int Fighter = 102;    // 格斗家（knuckle/gauntlet/claw/bglove/tonfa）
        public const int Gunner = 104;     // 神枪手（revolver/automatic/musket/hcannon/bowgun）
        public const int Mage = 106;       // 魔法师/缔造者（spear/pole/rod/staff/broom）
        public const int Priest = 108;     // 圣职者（cross/rosary/totem/scythe/axe）
        public const int Thief = 109;      // 暗夜使者（dagger/twinswd/wand/chakraweapon）
        public const int Lancer = 114;     // 魔枪士（pike/halberd/beamspear/javelin）
        public const int GunBlader = 116;  // 枪剑士（lblade/mswd/sblade/coreswd）

        // 位号 = 界面顺序（与族号的大小顺序一致）。换序时同时改族表顺序与这里。
        public const int BitSwordman = 0;
        public const int BitFighter = 1;
        public const int BitGunner = 2;
        public const int BitMage = 3;
        public const int BitPriest = 4;
        public const int BitThief = 5;
        public const int BitLancer = 6;
        public const int BitGunBlader = 7;
    }

    /// <summary>
    /// 防具材质 = 装备模板 id 的前 3 位数字（<c>id / 1000000</c>）对应的 5 个材质族。
    /// 材质族在客户端同时是「材质节点」（如 11000 布甲）与「材质 × 部位叶子」（如 11002 布甲上衣）。
    /// </summary>
    public static class ArmorMaterial
    {
        public const int Cloth = 110;    // 布甲（PVF 组前缀 `cl`）
        public const int Leather = 111;  // 皮甲（`lt`）
        public const int Light = 112;    // 轻甲（`la`）
        public const int Heavy = 113;    // 重甲（`ha`）
        public const int Plate = 114;    // 板甲（`mt`）
    }

    /// <summary>
    /// **武器族 ↔ 职业 / 防具族 ↔ 材质**的集中映射表（换序只改这里）。
    ///
    /// 族与族内成员都是**本机数据可复核**的：
    ///   - 每个武器族恰好对应 5 个物品组，组名集合互不重叠（族 101 = ssword/katana/club/lswd/beamswd，
    ///     族 102 = knuckle/gauntlet/claw/bglove/tonfa…），因此「哪个族属于哪个职业」由组名集合定死；
    ///   - 族内的**族号顺序**（101→102→…→109）与用户给出的界面顺序一一对应，这一步是用户界面顺序，
    ///     没有逐节点抓包，因此映射表集中在这里，换序只改这一处。
    /// 职业位掩码取 <c>ItemCatalog.JobNames</c> 的下标（`[usable job]` 里写着这些标签），
    /// 因此「按职业过滤」用的是模板自带字段，不是猜的。
    /// </summary>
    static readonly (int Family, string Jobs, int Group, string Name)[] WeaponFamilies =
    [
        (WeaponGroup.Swordman, "[swordman] [demonic swordman] [at swordman] [knight]", WeaponGroup.BitSwordman, "鬼剑士/黑暗武士"),
        (WeaponGroup.Fighter, "[fighter] [at fighter]", WeaponGroup.BitFighter, "格斗家"),
        (WeaponGroup.Gunner, "[gunner] [at gunner]", WeaponGroup.BitGunner, "神枪手"),
        (WeaponGroup.Mage, "[mage] [creator mage] [at mage]", WeaponGroup.BitMage, "魔法师/缔造者"),
        (WeaponGroup.Priest, "[priest] [at priest]", WeaponGroup.BitPriest, "圣职者"),
        (WeaponGroup.Thief, "[thief]", WeaponGroup.BitThief, "暗夜使者"),
        (WeaponGroup.Lancer, "[demonic lancer]", WeaponGroup.BitLancer, "魔枪士"),
        (WeaponGroup.GunBlader, "[gun blader]", WeaponGroup.BitGunBlader, "枪剑士")
    ];

    /// <summary>防具材质族表（顺序 = 界面顺序 = id 升序）。</summary>
    static readonly (int Family, int Group, string Name)[] ArmorFamilies =
    [
        (ArmorMaterial.Cloth, 0, "布甲"),
        (ArmorMaterial.Leather, 1, "皮甲"),
        (ArmorMaterial.Light, 2, "轻甲"),
        (ArmorMaterial.Heavy, 3, "重甲"),
        (ArmorMaterial.Plate, 4, "板甲")
    ];

    /// <summary>武器族掩码（8 个族各占一位，位号 = 界面顺序，占位 0–7）。</summary>
    public static ulong WeaponGroupMask(int group) => 1UL << group;

    /// <summary>
    /// 防具材质族掩码（5 个材质各占一位，占位 8–12）。
    /// **与武器族分开占位**：武器族与材质族共用同一段数值空间（如 114 既是魔枪士族又是板甲材质），
    /// 合并成一套位号会让两个大类互相串味，因此强制分区。
    /// </summary>
    public static ulong ArmorGroupMask(int material) => 1UL << (8 + material);

    /// <summary>所有武器族的并集（武器父节点的维度）。</summary>
    public static ulong AllWeaponGroups
    {
        get
        {
            ulong mask = 0;
            foreach (var (_, _, group, _) in WeaponFamilies)
                mask |= WeaponGroupMask(group);
            return mask;
        }
    }

    /// <summary>所有防具材质族的并集（防具父节点的维度）。</summary>
    public static ulong AllArmorGroups
    {
        get
        {
            ulong mask = 0;
            foreach (var (_, group, _) in ArmorFamilies)
                mask |= ArmorGroupMask(group);
            return mask;
        }
    }

    /// <summary>
    /// 一个武器族的职业位掩码：把族表里的职业标签映射成 <c>ItemCatalog.JobNames</c> 的下标。
    /// 位掩码的位序与 <c>ItemCatalog</c> 的 <c>[usable job]</c> 解析完全一致，因此可以直接与
    /// <c>ItemTemplate.Jobs</c> 求交——「按职业过滤」用的是模板自带字段，不引入第二套职业编号。
    /// </summary>
    public static ushort WeaponJobs(int group)
    {
        foreach (var (_, jobs, number, _) in WeaponFamilies)
        {
            if (number == group)
                return JobMask(jobs);
        }
        throw new InvalidDataException("未登记的武器族：" + group);
    }

    /// <summary>
    /// 把 `[swordman] [demonic swordman]` 这样的标签串映射成职业位掩码。
    /// 只认方括号里的标签（与 <c>ItemCatalog</c> 解析 <c>[usable job]</c> 的写法一致），
    /// 因此族表里写多个人名标签也不会被空格切错（`demonic swordman` 本身含空格）。
    /// </summary>
    static ushort JobMask(string jobs)
    {
        ushort mask = 0;
        foreach (Match tag in Regex.Matches(jobs, @"\[([^\]]+)\]"))
        {
            string name = tag.Groups[1].Value.Trim();
            int index = -1;
            for (int position = 0; position < JobNames.Length; position++)
            {
                if (string.Equals(JobNames[position], name, StringComparison.OrdinalIgnoreCase))
                {
                    index = position;
                    break;
                }
            }
            if (index < 0)
                throw new InvalidDataException("武器族表里的职业标签不在 JobNames 里：" + name);
            mask |= (ushort)(1 << index);
        }
        if (mask == 0)
            throw new InvalidDataException("武器族表的职业标签为空：" + jobs);
        return mask;
    }

    /// <summary>
    /// 职业标签清单，**必须与 <c>ItemCatalog.JobNames</c> 逐项同序**（位掩码按这个顺序建位）。
    /// 自测断言两者一致，避免两处清单漂移后「按职业过滤」静默错位。
    /// </summary>
    public static readonly ImmutableArray<string> JobNames =
    [
        "swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer",
        "at priest", "gun blader"
    ];

    /// <summary>
    /// 一次分类搜索用的合成**武器**模板 id：<c>[族号 3 位][组序号 2 位][部位号 2 位][流水号 2 位]</c>，共 9 位。
    /// 与真实武器 id 同形（`101000001` = 族 101 的第 0 段）。只用于自测与文档构造可核对的样本 id，
    /// 业务判定不依赖它（真实 id 由物品目录提供）。
    /// </summary>
    public static int EquipmentTemplateId(int family, int ordinal, int slot, int serial = 1) =>
        family * 1_000_000 + ordinal * 10_000 + slot * 100 + serial;

    /// <summary>
    /// 合成**防具**模板 id：<c>[材质编号 3 位][部位 2 位][流水 3 位]</c>，前 3 位就是材质号，
    /// 例如 <c>ArmorTemplateId(112, 17)</c> = `112_017_003` = 112017003（轻甲，部位 17）。
    /// 材质编号与物品族号同源（110 布甲 / 111 皮甲 / 112 轻甲 / 113 重甲 / 114 板甲），
    /// 因此族判定（<c>id / 1_000_000</c>）与真实数据一致。
    /// 只用于自测构造可核对的 id，业务判定不依赖它（真实 id 由物品目录提供）。
    /// </summary>
    public static int ArmorTemplateId(int material, int slot, int serial = 3) =>
        material * 1_000_000 + slot * 1_000 + serial;

    /// <summary>
    /// 模板 id 对应的分类「族掩码」：取前 3 位数字（武器 101/102/104/106/108/109/114/116 族、
    /// 防具 110–113 材质），其它物品（首饰 `1003xx`、时装…）返回 0 —— **不认识的 id 一律不过滤**。
    ///
    /// ★ 武器族与材质族共用同一段号码：**材质 114（板甲）与武器族 114（魔枪士）无法只靠 id 区分**，
    /// 这里让武器族优先命中。板甲子叶因此只靠「部位 14–18」收窄——宁可少收，也不猜一个可能
    /// 把其它大类的行放进来的材质掩码。依据与取舍见文档「仍未确认的项」。
    /// </summary>
    public static ulong ItemGroupMask(int itemTemplateId)
    {
        if (itemTemplateId < 100_000_000 || itemTemplateId > 999_999_999)
            return 0;
        int family = itemTemplateId / 1_000_000;
        ulong mask = 0;
        foreach (var (number, _, group, _) in WeaponFamilies)
        {
            if (number == family)
                mask |= WeaponGroupMask(group);
        }
        foreach (var (number, group, _) in ArmorFamilies)
        {
            if (number == family)
                mask |= ArmorGroupMask(group);
        }
        return mask;
    }

    /// <summary>
    /// 客户端实际发出的 140 个分类 id（本机运行实例日志 `auction_search_category` 去重采集，140 个不同 id
    /// = 9 个顶层 + 131 个子叶）。**采集是映射的依据**：表里每个子叶都必须在这份清单里，测试逐 id 锁定。
    /// 采集命令与用法见 Docs/模块/拍卖行.md；超出这份清单的 id 说明客户端界面变了，必须重新采集后再改表。
    /// </summary>
    public static readonly ImmutableArray<int> CollectedIds =
    [
        10000,
        10101, 10102, 10103, 10104, 10105, 10106, 10201, 10202, 10203, 10204, 10205, 10206,
        10302, 10303, 10304, 10305, 10306, 10402, 10403, 10404, 10405, 10406, 10502, 10503, 10504, 10505, 10506,
        10602, 10603, 10604, 10605, 10802, 10803, 10804, 10805, 10902, 10903, 10904, 10905,
        11000, 11002, 11003, 11004, 11005, 11006, 11101, 11102, 11103, 11104, 11105,
        11201, 11202, 11203, 11204, 11205, 11301, 11302, 11303, 11304, 11305, 11401, 11402, 11403, 11404, 11405,
        12000, 12001, 12002, 12003,
        13000, 13001, 13002, 13003, 13004, 13005, 13006,
        14000, 14001, 14002, 14003,
        31000, 31002, 31003, 31004, 31005, 31006, 31007, 31010, 31011,
        31102, 31103, 31104, 31105, 31106, 31202, 31203, 31204, 31302, 31303,
        32000, 32002, 32003, 32004, 32005, 32006, 32007, 32008, 32009, 32010, 32011,
        32012, 32013, 32014, 32015, 32016, 32017,
        32101, 32102, 32103, 32104, 32105, 32106, 32107, 32108, 32109, 32110,
        32111, 32112, 32113, 32114, 32115, 32116, 32200,
        33000, 33001, 33002, 33003, 33004,
        34000
    ];

    /// <summary>
    /// 已对账的子叶：id → 维度。**顺序按顶层大类分组**，便于与采集日志逐组核对。
    /// 每个子叶的 <see cref="AuctionCategoryLeaf.Evidence"/> 写明它凭什么被认下来。
    /// </summary>
    public static readonly ImmutableArray<AuctionCategoryLeaf> Leaves =
    [
        // —— 首饰（父节点 12000，3 个子节点 12001–12003）——
        // 依据：参考表 §7.6 的 3 个叶子顺序（amulet/ring/wrist，子类型升序），其中 12001 有 D95 抓包实证。
        new(Accessory_Amulet, Accessory, "项链", new(0, Bit(19)), "参考 §7.6 叶子 1；D95 187 请求体实测 categoryId=0x2EE1=12001"),
        new(Accessory_Ring, Accessory, "戒指", new(0, Bit(21)), "参考 §7.6 叶子 2（ring），父节点 12000 的第 2 个子节点"),
        new(Accessory_Wrist, Accessory, "手镯", new(0, Bit(20)), "参考 §7.6 叶子 3（wrist），父节点 12000 的第 3 个子节点"),

        // —— 消耗品（父节点 13000，6 个子节点 13001–13006）——
        // 依据：参考表 §7.2 的 6 个子分类，顺序与名称**都已定案**；采集到的 13001–13006 正是这 6 个连续子节点，
        // 个数与顺序一一对应，无多无缺，故按位置逐项对应。过滤维度是 ItemKind，参考表给出的是 [stackable type]，
        // 这里的映射经 ItemCatalog.Load 的既有归类核过（材料族 → Material、[material expert job] → Profession 等）。
        new(Consumable_Normal, Consumable, "消耗品", new(Bit((byte)ItemKind.Consumable), 0), "参考 §7.2 子分类 1（KindConsumable=2）"),
        new(Consumable_Material, Consumable, "材料",
            new(Bit((byte)ItemKind.Material) | Bit((byte)ItemKind.Profession), 0),
            "参考 §7.2 子分类 2：参考把 Material/ExpertJobMaterial/SpecialMaterial 三个 kind 全收；" +
            "本仓库 Catalog 把前两者分别归为 Material、Profession，SpecialMaterial 并入 Material（_review §7.2 round110）"),
        new(Consumable_Throw, Consumable, "投掷设置", new(Bit((byte)ItemKind.Consumable), 0),
            "参考 §7.2 子分类 3（KindThrow=15）。本仓库 Catalog 把 [throw] 归入 Consumable，无法再细分，故按 ItemKind.Consumable 过滤"),
        new(Consumable_Gacha, Consumable, "抽取", new(Bit((byte)ItemKind.Consumable), 0),
            "参考 §7.2 子分类 4（KindGacha=16）。本仓库 Catalog 把 [booster selection] 系归入 Consumable，无法再细分"),
        new(Consumable_Quest, Consumable, "任务", new(Bit((byte)ItemKind.Quest), 0), "参考 §7.2 子分类 5（KindQuest=4）"),
        new(Consumable_Misc, Consumable, "其它", new(Bit((byte)ItemKind.Consumable), 0),
            "参考 §7.2 子分类 6（KindMisc=17）。本仓库 Catalog 把 [waste]/[etc]/[dye]/[contract] 归入 Consumable，无法再细分"),

        // —— 宠物装备（父节点 14000，3 个子节点 14001–14003）——
        // 依据：参考表 §7.3 的 3 个子分类顺序（红/蓝/绿）**2026-09-04 抓包 + 用户确认定案**；
        // 采集到的 14001–14003 是 3 个连续子节点，个数与顺序一一对应。
        // 参考用 [equipment type]（artifact red/blue/green）区分，本仓库模板没有该字段，
        // 但 Catalog 把这三个标签解析成装备部位 27/28/29（见 ItemCatalog.EquipmentSlots），维度等价且更直接。
        new(Creature_Red, Creature, "红色宠物装备", new(0, Bit(27)), "参考 §7.3：filterEquipType=[artifact red] ⇔ 本仓库装备部位 27"),
        new(Creature_Blue, Creature, "蓝色宠物装备", new(0, Bit(28)), "参考 §7.3：filterEquipType=[artifact blue] ⇔ 本仓库装备部位 28"),
        new(Creature_Green, Creature, "绿色宠物装备", new(0, Bit(29)), "参考 §7.3：filterEquipType=[artifact green] ⇔ 本仓库装备部位 29"),

        // —— 副职业（父节点 33000，4 个子节点 33001–33004）——
        // 依据：参考表 §7.7 的 4 个子分类（炼金/控偶/附魔/副职材料）顺序**2026-09-04 用户定案**；
        // 采集到的 33001–33004 是 4 个连续子节点，个数与顺序一一对应。
        // 参考用 [expert type] 区分前三者，该字段没有进 ItemTemplate，故取不到的维度不编，只按物品大类过滤。
        new(ExpertJob_Alchemist, ExpertJob, "炼金术师", new(Bit((byte)ItemKind.Profession), 0),
            "参考 §7.7 子分类 1（[expert type]=[alchemist]）。[expert type] 未进模板，只按 ItemKind.Profession 粗过滤"),
        new(ExpertJob_Doll, ExpertJob, "控偶师", new(Bit((byte)ItemKind.Profession), 0),
            "参考 §7.7 子分类 2（[expert type]=[doll_controller]）。同上，只按 ItemKind.Profession 粗过滤"),
        new(ExpertJob_Enchanter, ExpertJob, "附魔师", new(Bit((byte)ItemKind.Profession), 0),
            "参考 §7.7 子分类 3（[expert type]=[enchanter]）。同上，只按 ItemKind.Profession 粗过滤"),
        new(ExpertJob_Material, ExpertJob, "副职材料", new(Bit((byte)ItemKind.Profession), 0),
            "参考 §7.7 子分类 4（KindExpertJobMaterial=10）⇔ 本仓库 ItemKind.Profession"),

        // —— 武器（父节点 10000，8 个族 101–106/108/109 共 32 个叶子）——
        // 维度 = 武器族掩码（族号来自模板 id，见 ItemGroupMask）+ 装备部位 12（武器部位）。
        // 「族内第 N 项 ↔ 武器类型」由本机 PVF 物品组定死：该族组名升序后与序号一一对应。
        // 例：族 101 的组 = beamswd/club/katana/lswd/ssword，升序后 ssword 在前 ⇒ 10101=短剑，
        // 与实测 `101010001`→`lswd`（巨剑，落在 id 段 10104）自洽。
        // ⚠ 族 ↔ 职业的顺序取自界面顺序（用户给出），**未逐节点抓包**；换序只改 WeaponFamilies 与这里。
        // 同一族的子叶维度相同（族掩码 + 职业位 + 部位 12）：D95 的族内细分（短剑/太刀…）在现有数据里取不到，
        // 见 WeaponLeaf 的说明，因此名称只作显示，不据此过滤。
        new(10101, Weapon, "短剑", WeaponLeaf(WeaponGroup.BitSwordman), "族 101（界面第 1 个武器节点，鬼剑士/黑暗武士）第 1 项；PVF 组 ssword/katana/club/lswd/beamswd"),
        new(10102, Weapon, "太刀", WeaponLeaf(WeaponGroup.BitSwordman), "族 101 第 2 项；族内细分未定案，与 10101 同维（族+职业+部位）"),
        new(10103, Weapon, "钝器", WeaponLeaf(WeaponGroup.BitSwordman), "族 101 第 3 项；同上"),
        new(10104, Weapon, "巨剑", WeaponLeaf(WeaponGroup.BitSwordman), "族 101 第 4 项；同上"),
        new(10105, Weapon, "光剑", WeaponLeaf(WeaponGroup.BitSwordman), "族 101 第 5 项；同上"),
        new(10106, Weapon, "鬼剑士·第 6 个子节点", WeaponLeaf(WeaponGroup.BitSwordman), "族 101 第 6 项：采集到 10106，参考表该职业只有 5 种武器，同样只按族+职业+部位过滤"),
        new(10201, Weapon, "手套", WeaponLeaf(WeaponGroup.BitFighter), "族 102（界面第 2 个武器节点，格斗家）第 1 项；PVF 组 knuckle/gauntlet/claw/bglove/tonfa"),
        new(10202, Weapon, "臂铠", WeaponLeaf(WeaponGroup.BitFighter), "族 102 第 2 项；族内细分未定案，与 10201 同维"),
        new(10203, Weapon, "爪", WeaponLeaf(WeaponGroup.BitFighter), "族 102 第 3 项；同上"),
        new(10204, Weapon, "拳套", WeaponLeaf(WeaponGroup.BitFighter), "族 102 第 4 项；同上"),
        new(10205, Weapon, "东方棍", WeaponLeaf(WeaponGroup.BitFighter), "族 102 第 5 项；同上"),
        new(10206, Weapon, "格斗家·第 6 个子节点", WeaponLeaf(WeaponGroup.BitFighter), "族 102 第 6 项：采集到 10206，同上"),
        new(10301, Weapon, "左轮", WeaponLeaf(WeaponGroup.BitGunner), "族 104（界面第 3 个武器节点，神枪手）第 1 项；PVF 组 revolver/automatic/musket/hcannon/bowgun"),
        new(10302, Weapon, "自动手枪", WeaponLeaf(WeaponGroup.BitGunner), "族 104 第 2 项；族内细分未定案，与 10301 同维"),
        new(10303, Weapon, "步枪", WeaponLeaf(WeaponGroup.BitGunner), "族 104 第 3 项；同上"),
        new(10304, Weapon, "手炮", WeaponLeaf(WeaponGroup.BitGunner), "族 104 第 4 项；同上"),
        new(10305, Weapon, "手弩", WeaponLeaf(WeaponGroup.BitGunner), "族 104 第 5 项；同上"),
        new(10306, Weapon, "神枪手·第 6 个子节点", WeaponLeaf(WeaponGroup.BitGunner), "族 104 第 6 项：采集到 10306，同上"),
        new(10401, Weapon, "长矛", WeaponLeaf(WeaponGroup.BitMage), "族 106（界面第 4 个武器节点，魔法师/缔造者）第 1 项；PVF 组 spear/pole/rod/staff/broom"),
        new(10402, Weapon, "棍棒", WeaponLeaf(WeaponGroup.BitMage), "族 106 第 2 项；族内细分未定案，与 10401 同维"),
        new(10403, Weapon, "魔杖", WeaponLeaf(WeaponGroup.BitMage), "族 106 第 3 项；同上"),
        new(10404, Weapon, "法杖", WeaponLeaf(WeaponGroup.BitMage), "族 106 第 4 项；同上"),
        new(10405, Weapon, "扫把", WeaponLeaf(WeaponGroup.BitMage), "族 106 第 5 项；同上"),
        new(10406, Weapon, "魔法师·第 6 个子节点", WeaponLeaf(WeaponGroup.BitMage), "族 106 第 6 项：采集到 10406，同上"),
        new(10501, Weapon, "十字架", WeaponLeaf(WeaponGroup.BitPriest), "族 108（界面第 5 个武器节点，圣职者）第 1 项；PVF 组 cross/rosary/totem/scythe/axe"),
        new(10502, Weapon, "念珠", WeaponLeaf(WeaponGroup.BitPriest), "族 108 第 2 项；族内细分未定案，与 10501 同维"),
        new(10503, Weapon, "图腾", WeaponLeaf(WeaponGroup.BitPriest), "族 108 第 3 项；同上"),
        new(10504, Weapon, "镰刀", WeaponLeaf(WeaponGroup.BitPriest), "族 108 第 4 项；同上"),
        new(10505, Weapon, "战斧", WeaponLeaf(WeaponGroup.BitPriest), "族 108 第 5 项；同上"),
        new(10506, Weapon, "圣职者·第 6 个子节点", WeaponLeaf(WeaponGroup.BitPriest), "族 108 第 6 项：采集到 10506，同上"),
        new(10601, Weapon, "匕首", WeaponLeaf(WeaponGroup.BitThief), "族 109（界面第 6 个武器节点，暗夜使者）第 1 项；PVF 组 dagger/twinswd/wand/chakraweapon"),
        new(10602, Weapon, "双剑", WeaponLeaf(WeaponGroup.BitThief), "族 109 第 2 项；族内细分未定案，与 10601 同维"),
        new(10603, Weapon, "权杖", WeaponLeaf(WeaponGroup.BitThief), "族 109 第 3 项；同上"),
        new(10604, Weapon, "苦无", WeaponLeaf(WeaponGroup.BitThief), "族 109 第 4 项；同上"),
        new(10605, Weapon, "暗夜使者·第 5 个子节点", WeaponLeaf(WeaponGroup.BitThief), "族 109 第 5 项：采集到 10605，参考表该职业只有 4 种武器，同上"),
        new(10801, Weapon, "长枪", WeaponLeaf(WeaponGroup.BitLancer), "族 114（界面第 7 个武器节点，魔枪士）第 1 项；PVF 组 pike/halberd/beamspear/javelin"),
        new(10802, Weapon, "战戟", WeaponLeaf(WeaponGroup.BitLancer), "族 114 第 2 项；族内细分未定案，与 10801 同维"),
        new(10803, Weapon, "光束矛", WeaponLeaf(WeaponGroup.BitLancer), "族 114 第 3 项；同上"),
        new(10804, Weapon, "标枪", WeaponLeaf(WeaponGroup.BitLancer), "族 114 第 4 项；同上"),
        new(10805, Weapon, "魔枪士·第 5 个子节点", WeaponLeaf(WeaponGroup.BitLancer), "族 114 第 5 项：采集到 10805，参考表该职业只有 2 种武器，同上"),
        new(10901, Weapon, "小太刀", WeaponLeaf(WeaponGroup.BitGunBlader), "族 116（界面第 8 个武器节点，枪剑士）第 1 项；PVF 组 lblade/mswd/sblade/coreswd"),
        new(10902, Weapon, "重剑", WeaponLeaf(WeaponGroup.BitGunBlader), "族 116 第 2 项；族内细分未定案，与 10901 同维"),
        new(10903, Weapon, "光剑", WeaponLeaf(WeaponGroup.BitGunBlader), "族 116 第 3 项；同上"),
        new(10904, Weapon, "源力剑", WeaponLeaf(WeaponGroup.BitGunBlader), "族 116 第 4 项；同上"),
        new(10905, Weapon, "枪剑士·第 5 个子节点", WeaponLeaf(WeaponGroup.BitGunBlader), "族 116 第 5 项：采集到 10905，参考表该职业只有 4 种武器，同上"),

        // —— 防具（父节点 11000，5 个材质族 110–114 共 25 个部位叶子）——
        // 维度 = 材质族掩码（族号来自模板 id）+ 装备部位（模板字段），**两个维度都声明**：
        // 只按材质会让「点布甲」列出布甲的下装/鞋/腰带/头肩，只按部位会把别的材质混进来。
        // 材质由**物品组前缀**定死（本机 PVF 实测 `cl/lt/la/ha/mt`），部位由组名的后缀定死
        // （`coat` 上衣 14 / `shoulder` 头肩 15 / `pants` 下装 16 / `shoes` 鞋 17 / `waist` 腰带 18）。
        // ⚠ 「第 N 个部位子节点 ↔ 哪个部位」的顺序取自界面顺序（参考表 §7.5 的部位顺序与之一致），
        //   未逐节点抓包；换序只改这里的 id 与部位。
        new(11002, Armor, "布甲·上衣", ArmorLeaf(ArmorMaterial.Cloth, 1, 14), "族 110（界面第 1 个防具节点）；PVF 组 `cl coat`；部位 14=上衣"),
        new(11003, Armor, "布甲·头肩", ArmorLeaf(ArmorMaterial.Cloth, 2, 15), "族 110 第 2 项；PVF 组 `cl shoulder`；部位 15=头肩"),
        new(11004, Armor, "布甲·下装", ArmorLeaf(ArmorMaterial.Cloth, 3, 16), "族 110 第 3 项；PVF 组 `cl pants`；部位 16=下装"),
        new(11005, Armor, "布甲·鞋", ArmorLeaf(ArmorMaterial.Cloth, 4, 17), "族 110 第 4 项；PVF 组 `cl shoes`；部位 17=鞋"),
        new(11006, Armor, "布甲·腰带", ArmorLeaf(ArmorMaterial.Cloth, 5, 18), "族 110 第 5 项；PVF 组 `cl waist`；部位 18=腰带"),
        new(11101, Armor, "皮甲·上衣", ArmorLeaf(ArmorMaterial.Leather, 1, 14), "族 111（界面第 2 个防具节点）第 1 项；PVF 组 `lt coat`；部位 14=上衣"),
        new(11102, Armor, "皮甲·头肩", ArmorLeaf(ArmorMaterial.Leather, 2, 15), "族 111 第 2 项；PVF 组 `lt shoulder`；部位 15=头肩"),
        new(11103, Armor, "皮甲·下装", ArmorLeaf(ArmorMaterial.Leather, 3, 16), "族 111 第 3 项；PVF 组 `lt pants`；部位 16=下装"),
        new(11104, Armor, "皮甲·鞋", ArmorLeaf(ArmorMaterial.Leather, 4, 17), "族 111 第 4 项；PVF 组 `lt shoes`；部位 17=鞋"),
        new(11105, Armor, "皮甲·腰带", ArmorLeaf(ArmorMaterial.Leather, 5, 18), "族 111 第 5 项；PVF 组 `lt waist`；部位 18=腰带"),
        new(11201, Armor, "轻甲·上衣", ArmorLeaf(ArmorMaterial.Light, 1, 14), "族 112（界面第 3 个防具节点）第 1 项；PVF 组 `la coat`；部位 14=上衣"),
        new(11202, Armor, "轻甲·头肩", ArmorLeaf(ArmorMaterial.Light, 2, 15), "族 112 第 2 项；PVF 组 `la shoulder`；部位 15=头肩"),
        new(11203, Armor, "轻甲·下装", ArmorLeaf(ArmorMaterial.Light, 3, 16), "族 112 第 3 项；PVF 组 `la pants`；部位 16=下装"),
        new(11204, Armor, "轻甲·鞋", ArmorLeaf(ArmorMaterial.Light, 4, 17), "族 112 第 4 项；PVF 组 `la shoes`；部位 17=鞋"),
        new(11205, Armor, "轻甲·腰带", ArmorLeaf(ArmorMaterial.Light, 5, 18), "族 112 第 5 项；PVF 组 `la waist`；部位 18=腰带"),
        new(11301, Armor, "重甲·上衣", ArmorLeaf(ArmorMaterial.Heavy, 1, 14), "族 113（界面第 4 个防具节点）第 1 项；PVF 组 `ha coat`；部位 14=上衣"),
        new(11302, Armor, "重甲·头肩", ArmorLeaf(ArmorMaterial.Heavy, 2, 15), "族 113 第 2 项；PVF 组 `ha shoulder`；部位 15=头肩"),
        new(11303, Armor, "重甲·下装", ArmorLeaf(ArmorMaterial.Heavy, 3, 16), "族 113 第 3 项；PVF 组 `ha pants`；部位 16=下装"),
        new(11304, Armor, "重甲·鞋", ArmorLeaf(ArmorMaterial.Heavy, 4, 17), "族 113 第 4 项；PVF 组 `ha shoes`；部位 17=鞋"),
        new(11305, Armor, "重甲·腰带", ArmorLeaf(ArmorMaterial.Heavy, 5, 18), "族 113 第 5 项；PVF 组 `ha waist`；部位 18=腰带"),
        new(11401, Armor, "板甲·上衣", ArmorLeaf(ArmorMaterial.Plate, 1, 14), "族 114（界面第 5 个防具节点）第 1 项；PVF 组 `mt coat`；部位 14=上衣"),
        new(11402, Armor, "板甲·头肩", ArmorLeaf(ArmorMaterial.Plate, 2, 15), "族 114 第 2 项；PVF 组 `mt shoulder`；部位 15=头肩"),
        new(11403, Armor, "板甲·下装", ArmorLeaf(ArmorMaterial.Plate, 3, 16), "族 114 第 3 项；PVF 组 `mt pants`；部位 16=下装"),
        new(11404, Armor, "板甲·鞋", ArmorLeaf(ArmorMaterial.Plate, 4, 17), "族 114 第 4 项；PVF 组 `mt shoes`；部位 17=鞋"),
        new(11405, Armor, "板甲·腰带", ArmorLeaf(ArmorMaterial.Plate, 5, 18), "族 114 第 5 项；PVF 组 `mt waist`；部位 18=腰带"),

        // —— 特殊装备：辅助装备（32001 下 16 职业子叶 32002–32017）、魔法石（32100 下 16 职业子叶 32101–32116）、耳环（32200）——
        // 依据：ARAD.exe 0x0371F1B0 分类树构造与 0x0256F400（跳表 0x0256F558）16 职业索引。
        new(32002, SupportEquipment, "辅助装备·鬼剑士(男)", SpecialEquipmentLeaf(22, "[swordman]"), "ARAD.exe 0x0256F400 跳表 #0（0xec67）：鬼剑士(男)"),
        new(32003, SupportEquipment, "辅助装备·格斗家(男)", SpecialEquipmentLeaf(22, "[fighter]"), "ARAD.exe 0x0256F400 跳表 #1（0x186a9）：格斗家(男)"),
        new(32004, SupportEquipment, "辅助装备·格斗家(女)", SpecialEquipmentLeaf(22, "[at fighter]"), "ARAD.exe 0x0256F400 跳表 #2（0xb65d）：格斗家(女)"),
        new(32005, SupportEquipment, "辅助装备·神枪手(男)", SpecialEquipmentLeaf(22, "[gunner]"), "ARAD.exe 0x0256F400 跳表 #3（0xb51f）：神枪手(男)"),
        new(32006, SupportEquipment, "辅助装备·神枪手(女)", SpecialEquipmentLeaf(22, "[at gunner]"), "ARAD.exe 0x0256F400 跳表 #4（0xb520）：神枪手(女)"),
        new(32007, SupportEquipment, "辅助装备·魔法师(女)", SpecialEquipmentLeaf(22, "[mage]"), "ARAD.exe 0x0256F400 跳表 #5（0xb65e）：魔法师(女)"),
        new(32008, SupportEquipment, "辅助装备·魔法师(男)", SpecialEquipmentLeaf(22, "[at mage]"), "ARAD.exe 0x0256F400 跳表 #6（0x186f8）：魔法师(男)"),
        new(32009, SupportEquipment, "辅助装备·圣职者(男)", SpecialEquipmentLeaf(22, "[priest]"), "ARAD.exe 0x0256F400 跳表 #7（0x186ac）：圣职者(男)"),
        new(32010, SupportEquipment, "辅助装备·暗夜使者", SpecialEquipmentLeaf(22, "[thief]"), "ARAD.exe 0x0256F400 跳表 #8（0xb521）：暗夜使者"),
        new(32011, SupportEquipment, "辅助装备·缔造者", SpecialEquipmentLeaf(22, "[creator mage]"), "ARAD.exe 0x0256F400 跳表 #9（0xb3fa）：缔造者"),
        new(32012, SupportEquipment, "辅助装备·黑暗武士", SpecialEquipmentLeaf(22, "[demonic swordman]"), "ARAD.exe 0x0256F400 跳表 #10（0xb51e）：黑暗武士"),
        new(32013, SupportEquipment, "辅助装备·鬼剑士(女)", SpecialEquipmentLeaf(22, "[at swordman]"), "ARAD.exe 0x0256F400 跳表 #11（0x186b4）：鬼剑士(女)"),
        new(32014, SupportEquipment, "辅助装备·守护者", SpecialEquipmentLeaf(22, "[knight]"), "ARAD.exe 0x0256F400 跳表 #12（0x186b3）：守护者"),
        new(32015, SupportEquipment, "辅助装备·魔枪士", SpecialEquipmentLeaf(22, "[demonic lancer]"), "ARAD.exe 0x0256F400 跳表 #13（0x186ee）：魔枪士"),
        new(32016, SupportEquipment, "辅助装备·圣职者(女)", SpecialEquipmentLeaf(22, "[at priest]"), "ARAD.exe 0x0256F400 跳表 #14（0x186f0）：圣职者(女)"),
        new(32017, SupportEquipment, "辅助装备·枪剑士", SpecialEquipmentLeaf(22, "[gun blader]"), "ARAD.exe 0x0256F400 跳表 #15（0x186fb）：枪剑士"),
        new(32101, MagicStone, "魔法石·鬼剑士(男)", SpecialEquipmentLeaf(23, "[swordman]"), "ARAD.exe 0x0256F400 跳表 #0（0xec67）：鬼剑士(男)"),
        new(32102, MagicStone, "魔法石·格斗家(男)", SpecialEquipmentLeaf(23, "[fighter]"), "ARAD.exe 0x0256F400 跳表 #1（0x186a9）：格斗家(男)"),
        new(32103, MagicStone, "魔法石·格斗家(女)", SpecialEquipmentLeaf(23, "[at fighter]"), "ARAD.exe 0x0256F400 跳表 #2（0xb65d）：格斗家(女)"),
        new(32104, MagicStone, "魔法石·神枪手(男)", SpecialEquipmentLeaf(23, "[gunner]"), "ARAD.exe 0x0256F400 跳表 #3（0xb51f）：神枪手(男)"),
        new(32105, MagicStone, "魔法石·神枪手(女)", SpecialEquipmentLeaf(23, "[at gunner]"), "ARAD.exe 0x0256F400 跳表 #4（0xb520）：神枪手(女)"),
        new(32106, MagicStone, "魔法石·魔法师(女)", SpecialEquipmentLeaf(23, "[mage]"), "ARAD.exe 0x0256F400 跳表 #5（0xb65e）：魔法师(女)"),
        new(32107, MagicStone, "魔法石·魔法师(男)", SpecialEquipmentLeaf(23, "[at mage]"), "ARAD.exe 0x0256F400 跳表 #6（0x186f8）：魔法师(男)"),
        new(32108, MagicStone, "魔法石·圣职者(男)", SpecialEquipmentLeaf(23, "[priest]"), "ARAD.exe 0x0256F400 跳表 #7（0x186ac）：圣职者(男)"),
        new(32109, MagicStone, "魔法石·暗夜使者", SpecialEquipmentLeaf(23, "[thief]"), "ARAD.exe 0x0256F400 跳表 #8（0xb521）：暗夜使者"),
        new(32110, MagicStone, "魔法石·缔造者", SpecialEquipmentLeaf(23, "[creator mage]"), "ARAD.exe 0x0256F400 跳表 #9（0xb3fa）：缔造者"),
        new(32111, MagicStone, "魔法石·黑暗武士", SpecialEquipmentLeaf(23, "[demonic swordman]"), "ARAD.exe 0x0256F400 跳表 #10（0xb51e）：黑暗武士"),
        new(32112, MagicStone, "魔法石·鬼剑士(女)", SpecialEquipmentLeaf(23, "[at swordman]"), "ARAD.exe 0x0256F400 跳表 #11（0x186b4）：鬼剑士(女)"),
        new(32113, MagicStone, "魔法石·守护者", SpecialEquipmentLeaf(23, "[knight]"), "ARAD.exe 0x0256F400 跳表 #12（0x186b3）：守护者"),
        new(32114, MagicStone, "魔法石·魔枪士", SpecialEquipmentLeaf(23, "[demonic lancer]"), "ARAD.exe 0x0256F400 跳表 #13（0x186ee）：魔枪士"),
        new(32115, MagicStone, "魔法石·圣职者(女)", SpecialEquipmentLeaf(23, "[at priest]"), "ARAD.exe 0x0256F400 跳表 #14（0x186f0）：圣职者(女)"),
        new(32116, MagicStone, "魔法石·枪剑士", SpecialEquipmentLeaf(23, "[gun blader]"), "ARAD.exe 0x0256F400 跳表 #15（0x186fb）：枪剑士"),
        new(Earring, Earring, "耳环", new(0, Bit(25)), "ARAD.exe 0x0371F1B0 / 0x0256F400（0x7DC8=32200，0x186f3）：耳环部位 25")
    ];

    static AuctionCategoryDimensions SpecialEquipmentLeaf(byte slot, string jobTag) =>
        new(0, Bit(slot), 0, JobMask(jobTag));

    /// <summary>
    /// 武器子叶维度：**本族职业位掩码** + 装备部位 12。
    ///
    /// 为什么用职业位而不是「武器类型」：D95 的武器类型（短剑/太刀…）在现有数据里取不到——
    /// 模板没有 <c>[item group name]</c> 字段，而模板 id 的族内分段与组名不是同一个序
    /// （实测：族 101 的 5 个组各自占用一段 id，段号与组名的排序位置不一致）。
    /// **取不到的维度不编**，因此子叶按族（职业）精确过滤，族内细分统一回该职业的全部武器。
    /// 依据见 <see cref="WeaponFamilies"/> 与文档「仍未确认项」。
    /// </summary>
    static AuctionCategoryDimensions WeaponLeaf(int group) =>
        new(0, Bit(12), WeaponGroupMask(group), WeaponJobs(group));

    /// <summary>防具子叶维度：材质族掩码 + 装备部位（族内次序只用于命名，不参与过滤）。</summary>
    static AuctionCategoryDimensions ArmorLeaf(int material, int ordinal, byte slot)
    {
        _ = ordinal;
        foreach (var (number, group, _) in ArmorFamilies)
        {
            if (number == material)
                return new(0, Bit(slot), ArmorGroupMask(group));
        }
        throw new InvalidDataException("未登记的防具材质族：" + material);
    }
    /// <summary>
    /// 顶层大类的**显示名**（维度不在这里，由 <see cref="Rollup"/> 从子叶合并、再用
    /// <see cref="TopLevelFallback"/> 补齐差集）。清单顺序只影响日志可读性，判定用精确 id 匹配。
    /// </summary>
    static readonly (int Id, string Name)[] TopLevel =
    [
        (Weapon, "武器"),
        (Armor, "防具"),
        (Accessory, "首饰"),
        (Title, "称号"),
        (Consumable, "消耗品"),
        (Creature, "宠物"),
        (SpecialEquipment, "特殊装备"),
        (SupportEquipment, "辅助装备"),
        (MagicStone, "魔法石"),
        (Blueprint, "设计图"),
        (ExpertJob, "副职业"),
        (Other, "其它")
    ];

    /// <summary>
    /// 顶层大类的**补充维度**（键 = 顶层 id）：子叶对账不全时补上子叶覆盖不到的那一维。
    ///   - 子叶**完全没有维度**的大类（武器/防具/称号/特殊装备）→ 整份维度都取自这里；
    ///   - 子叶**只覆盖了部分维度**的大类（首饰：子叶给了 19/20/21，这里补上只有父节点能显示的耳环 25）
    ///     → 与子叶合并（并集），保证父节点不丢东西；
    ///   - 宠物（14000）的子叶只有部位维度、物品大类要靠 <see cref="ItemKind.Creature"/> 补，
    ///     否则父节点会把三色宠物装备之外的一切都当成宠物。
    /// 「其它」是分类树兜底叶子，语义是「不归属任何大类的剩余道具」，本仓库无法枚举该集合，故置 0（不过滤）。
    /// </summary>
    static readonly Dictionary<int, AuctionCategoryDimensions> TopLevelFallback = new()
    {
        // 武器/防具的子叶**已经逐节点对账**（见 Leaves 的武器与防具段），这里只补子叶覆盖不到的部分：
        // 武器族里没有叶子的（107 族）、防具族里没有叶子的部位（族内序号 06/07…）。
        // 注意 Groups 留 0：族掩码在下面的并集分支里补（放在这里会命中「整份维度取自兜底表」的分支，
        // 把子叶已经对账的维度整份替换掉）。
        [Weapon] = new(0, Bit(12)),
        [Armor] = new(0, Bits(14, 15, 16, 17, 18)),
        [Title] = new(0, Bit(13)),                    // 称号部位只有称号会占
        // 耳环（部位 25）挂在自己的部位上、**没有独立子节点**，因此没有子叶能覆盖它；
        // 首饰父节点是唯一能显示耳环的节点，这里显式补上，否则耳环在界面上永远搜不到。
        [Accessory] = new(0, Bit(25)),
        // 32000 是特殊装备的**展示用父节点**：它的两个子族各自有精确 id（32001 辅助装备 / 32100 魔法石），
        // 因此这里同时给出 22/23 两个部位。实际收到 32001/32100 时会分别命中各自的精确条目。
        [SpecialEquipment] = new(0, Bits(22, 23)),
        [SupportEquipment] = new(0, Bit(22)),
        [MagicStone] = new(0, Bit(23)),
        // 宠物：物品大类按 Creature（宠物相关物品），部位 26 是宠物本体、27–29 是三色宠物装备子叶。
        // 宠物蛋与宠物本体同为 Creature，区分字段 [output index] 没有进模板，故本体只能粗过滤。
        [Creature] = new(Bit((byte)ItemKind.Creature), Bit(26)),
        [Blueprint] = new(0, 0),                      // 未对账：不过滤
        [Other] = new(0, 0)                           // 兜底叶子：无法枚举该集合，不过滤
    };

    /// <summary>
    /// 维度**未对账**的大类：这些大类的 id 已全部采集到（见 <see cref="CollectedIds"/>），但「第 N 个子节点 =
    /// 哪个装备类型/材质」无法定案，**保持不过滤子叶细分、按所属顶层维度粗过滤**并把原因写进日志。
    /// <see cref="First"/>–<see cref="Last"/> 是该大类在客户端 id 空间里占的闭区间（按**采集清单**归纳，
    /// 从窄到宽匹配）：顶层 id 与它的各子节点族落在这个区间里，用来把「点具体子叶」翻回顶层。
    ///
    /// ★ 武器的族↔职业、防具的族↔材质**已经对账**（依据见 <see cref="Leaves"/> 的对应段），
    /// 因此这两个区间现在只兜住**族表里没有的 id**（如未采集的 10100 父节点、107 族、规划中的子节点）；
    /// 逐条原因：
    ///   - <b>武器</b>：8 个族（`101`–`106`、`108`、`109`）的族内武器类型已由本机 PVF 物品组定案；
    ///     区间只兜未登记的族（`107`）与未采集的父节点 id。
    ///   - <b>防具</b>：5 个材质族（`110`–`114`）× 5 部位已由 PVF 物品组（`cl/lt/la/ha/mt` + 部位）定案；
    ///     区间只兜族内未登记的序号。
    ///   - <b>特殊装备</b>：参考表 §7.8 是固定 14 职业，采集到 `32002–32017` 14 个 + `32101–32116` 16 个叶子，
    ///     个数对不上，且 `32010`/`32011` 被客户端当成父节点（点击后继续下发别的子节点），
    ///     说明这些 id 里混有非叶子节点，无法离线分辨。
    ///   - <b>设计图</b>：参考表 §7.10 标注 `0x7918`「当前 PVF 无设计图数据，已作废」，未给叶子语义；
    ///     本机另有 20 个 id（`31000`/`31002–31007`/`31010–31303`），无维度依据，因此该大类整段不过滤。
    /// </summary>
    public static readonly ImmutableArray<(int Id, int First, int Last, string Reason)> Unreconciled =
    [
        (Weapon, 10000, 10999,
            "该武器 id 未登记到族表：按武器顶层维度过滤（8 个族 32 个叶子已对账，这里只兜未登记的族/父节点）"),
        (Armor, 11000, 11499,
            "该防具 id 未登记到族表：按防具顶层维度过滤（5 个材质族 × 5 部位已对账，这里只兜未登记的序号）"),
        // 特殊装备有两个平行的 id 空间，按客户端实发的区间分别归族（区间从**采集清单**归纳）：
        // 320xx 是辅助装备（32002–32017），321xx 是魔法石（32101–32116）。
        (SupportEquipment, 32000, 32099,
            "辅助装备子叶（职业）未对账：参考表 14 职业，采集到 32002–32017 且其中混有父节点（32010/32011）"),
        (MagicStone, 32100, 32199,
            "魔法石子叶（职业）未对账：参考表 14 职业，采集到 32101–32116 共 16 个，与 14 职业对不上"),
        (Blueprint, 31000, 31499,
            "设计图子叶未对账：参考表标注该大类已作废、未给叶子语义")
    ];

    /// <summary>
    /// 解析分类 id，四种结果按下面顺序判定：
    ///   ① <see cref="All"/>（40119）→ 不过滤；
    ///   ② **顶层大类精确命中** → 维度 = 子叶合并 ∪ 兜底补齐的差集部位（见 <see cref="TopLevelFallback"/>）；
    ///   ③ **已对账子叶精确命中** → 该子叶自己的维度；
    ///   ④ 其它 → 按**未对账大类的 id 区间**归族（`UnreconciledTopLevel`），再退到**最长的顶层 id 前缀**
    ///      （`PrefixTopLevel`）——两者都只在已登记的 id 空间内推断，不做十进制区间外推；
    ///      命中则按所属顶层过滤，否则记 <see cref="AuctionCategoryStatus.Unmapped"/> 并**不过滤**。
    /// 只做精确 id 与已登记区间，是因为客户端分类 id 不是十进制分段
    /// （武器叶子 0x2776=10102、防具材质 0x2B5C=11100 与十进制 11001 相差很远），
    /// 用十进制区间外推会把不认识的 id 误判成别的大类，反而制造错误过滤。
    /// </summary>
    public static AuctionCategoryResolution Resolve(int categoryId)
    {
        if (categoryId == All)
            return new(categoryId, AuctionCategoryStatus.All, 0, null, new(0, 0), "「全部」节点：不过滤");
        if (AuctionGoldTickets.IsCategory(categoryId))
            return new(categoryId, AuctionCategoryStatus.Gold, 0, "金币寄售", new(0, 0),
                "金币寄售分类 40101–40117：只检索金币券挂牌。各 id 与面额的对应没有抓包，因此这 17 个 id 暂时同一套结果");
        foreach (var (id, name) in TopLevel)
        {
            if (id != categoryId)
                continue;
            var dimensions = Rollup(id);
            if (dimensions.Kinds == 0 && dimensions.Slots == 0)
            {
                // 该大类没有任何已对账子叶：整份维度取自兜底表（武器/防具/称号/特殊装备/设计图/其它）。
                // 此时**不再**从部位反推物品大类：兜底表声明的「只按部位过滤」是刻意的
                // （部位本就隐含物品大类，叠加种类条件只会在客户端把物品判成别的种类时整类消失）。
                if (TopLevelFallback.TryGetValue(id, out var only))
                    dimensions = only;
            }
            else if (TopLevelFallback.TryGetValue(id, out var extra))
            {
                // 子叶覆盖不全：只补子叶没覆盖到的部位（首饰补耳环 25、宠物补本体部位 26、武器/防具补差集族），
                // 并补出这些**多出来**部位隐含的物品大类（宠物补 Creature=5）。族掩码按并集补，
                // 因为族掩码本来就是「本族全部物品组」的并集，不存在部位那类「把子叶已有部位的种类混入」的风险。
                ulong added = extra.Slots & ~dimensions.Slots;
                dimensions = dimensions with
                {
                    Slots = dimensions.Slots | added,
                    Kinds = extra.Kinds | dimensions.Kinds | KindsOfSlots(added),
                    Groups = extra.Groups | dimensions.Groups | GroupsOf(id)
                };
            }
            return new(categoryId, AuctionCategoryStatus.Mapped, id, name, dimensions,
                dimensions.Kinds == 0 && dimensions.Slots == 0
                    ? "顶层大类但无可枚举维度：不过滤（「其它」是分类树兜底叶子，无法枚举该集合）"
                    : HasReconciledLeaves(id)
                        ? "顶层大类：子叶已对账，维度由子叶合并，与子叶口径一致"
                        : "顶层大类：子叶维度未对账，只按顶层维度粗过滤（见文档「仍未映射的子叶」）");
        }
        foreach (var leaf in Leaves)
        {
            if (leaf.Id == categoryId)
                return new(categoryId, AuctionCategoryStatus.Mapped, leaf.TopLevelId, leaf.Name, leaf.Dimensions, leaf.Evidence);
        }
        if (ResolveTitleJobOrArmorMaterialParent(categoryId) is { } specific)
            return specific;
        // 顺序很重要：① 按**未对账区间**归族（区分 320xx 辅助装备与 321xx 魔法石）；
        // ② 再按「最长的顶层 id 前缀」归族——客户端 id 是**扁平**的（父节点 id 本身就是前缀，
        //    如辅助装备 0x7D01、魔法石 0x7D64 = 32001/32100），故前缀就是父节点，且取最长者得最具体的那个；
        // ③ 都没命中才不过滤（客户端界面可能已变化）。
        int? fallbackId = UnreconciledTopLevel(categoryId) ?? PrefixTopLevel(categoryId);
        if (fallbackId is { } top)
        {
            var resolution = Resolve(top);
            return new(categoryId, AuctionCategoryStatus.Mapped, top, resolution.Name, resolution.Dimensions,
                $"该 id 未登记到子叶表：按所属顶层「{resolution.Name}」的维度过滤（{Reason(top)}）");
        }
        return new(categoryId, AuctionCategoryStatus.Unmapped, 0, null, new(0, 0), UnreconciledNote(categoryId));
    }

    /// <summary>
    /// 顶层大类**族掩码的并集**（只有武器与防具是「族」模型，其余返回 0）。
    /// 与 <see cref="Rollup"/> 分开：族掩码来自族表而不是子叶，父节点必须拿到整族（含没有叶子的族内序号）。
    /// </summary>
    static ulong GroupsOf(int topLevelId) => topLevelId switch
    {
        Weapon => AllWeaponGroups,
        Armor => AllArmorGroups,
        _ => 0
    };

    static readonly (string Tag, string Name)[] TitleJobTable =
    [
        ("[swordman]", "称号·鬼剑士(男)"),
        ("[at swordman]", "称号·鬼剑士(女)"),
        ("[fighter]", "称号·格斗家(男)"),
        ("[at fighter]", "称号·格斗家(女)"),
        ("[gunner]", "称号·神枪手(男)"),
        ("[at gunner]", "称号·神枪手(女)"),
        ("[mage]", "称号·魔法师(女)"),
        ("[at mage]", "称号·魔法师(男)"),
        ("[priest]", "称号·圣职者(男)"),
        ("[thief]", "称号·暗夜使者"),
        ("[knight]", "称号·守护者"),
        ("[demonic lancer]", "称号·魔枪士"),
        ("[at priest]", "称号·圣职者(女)"),
        ("[gun blader]", "称号·枪剑士")
    ];

    static AuctionCategoryResolution? ResolveTitleJobOrArmorMaterialParent(int categoryId)
    {
        if (categoryId is >= 12005 and <= 12018)
        {
            var (tag, name) = TitleJobTable[categoryId - 12005];
            return new(categoryId, AuctionCategoryStatus.Mapped, Title, name,
                new(0, Bit(13), 0, JobMask(tag)),
                $"ARAD.exe 0x037180A0 称号职业子叶 #{categoryId - 12005}：{name}");
        }
        return categoryId switch
        {
            11001 => new(categoryId, AuctionCategoryStatus.Mapped, Armor, "布甲",
                new(0, Bits(14, 15, 16, 17, 18), ArmorGroupMask(0)), "ARAD.exe 0x0371EC20 布甲材质父节点"),
            11100 => new(categoryId, AuctionCategoryStatus.Mapped, Armor, "皮甲",
                new(0, Bits(14, 15, 16, 17, 18), ArmorGroupMask(1)), "ARAD.exe 0x0371EC20 皮甲材质父节点"),
            11200 => new(categoryId, AuctionCategoryStatus.Mapped, Armor, "轻甲",
                new(0, Bits(14, 15, 16, 17, 18), ArmorGroupMask(2)), "ARAD.exe 0x0371EC20 轻甲材质父节点"),
            11300 => new(categoryId, AuctionCategoryStatus.Mapped, Armor, "重甲",
                new(0, Bits(14, 15, 16, 17, 18), ArmorGroupMask(3)), "ARAD.exe 0x0371EC20 重甲材质父节点"),
            11400 => new(categoryId, AuctionCategoryStatus.Mapped, Armor, "板甲",
                new(0, Bits(14, 15, 16, 17, 18), ArmorGroupMask(4)), "ARAD.exe 0x0371EC20 板甲材质父节点"),
            _ => null
        };
    }

    /// <summary>
    /// 最长的顶层 id 前缀：客户端分类 id 是扁平的「父节点 id + 序号」（如辅助装备 `32001` 的叶子 `32002–32017`、
    /// 魔法石 `32100` 的叶子 `32101–32116`），因此 id 的十进制前缀就是它的祖先，取最长的即最具体的那个。
    /// 只认本表登记过的顶层 id，不做十进制区间外推；顶层 id 自己也算命中（`32100` 的前缀就是它自己）。
    /// </summary>
    static int? PrefixTopLevel(int categoryId)
    {
        int? best = null;
        int bestDigits = 0;
        foreach (var (id, _) in TopLevel)
        {
            int digits = Digits(id);
            if (digits <= bestDigits || !HasPrefix(categoryId, id, digits))
                continue;
            best = id;
            bestDigits = digits;
        }
        return best;
    }

    /// <summary>
    /// 十进制前缀判定：<paramref name="value"/> 去掉末尾 <paramref name="digits"/> 位后是否等于
    /// <paramref name="prefix"/> 去掉末尾 <paramref name="digits"/> 位——即两者高位数相同。
    /// 例：HasPrefix(32002, 32000, 5) 为真（32002/10⁵ == 32000/10⁵ == 0 不是我们要的），
    /// 因此这里的 scale 取「比 prefix 位数少一位」的 10 的幂，即比较 `value / 10^(digits-1)`。
    /// </summary>
    static bool HasPrefix(int value, int prefix, int digits)
    {
        int scale = 1;
        for (int index = 1; index < digits; index++)
            scale *= 10;
        return value / scale == prefix / scale;
    }

    static int Digits(int value)
    {
        int digits = 1;
        for (int rest = value / 10; rest != 0; rest /= 10)
            digits++;
        return digits;
    }

    /// <summary>
    /// categoryId 是否落在某个「已采集但子叶未对账」的大类的 id 区间里；是则返回该顶层 id。
    /// 多个区间命中时取**最窄**的那个：`34xxx` 同时落在「设计图」的区间与「其它」的窄区间里，
    /// 取窄区间才能把它归到「其它」而不是误报成设计图。
    /// </summary>
    static int? UnreconciledTopLevel(int categoryId)
    {
        int? best = null;
        int bestWidth = int.MaxValue;
        foreach (var (topLevelId, first, last, _) in Unreconciled)
        {
            if (categoryId < first || categoryId > last || last - first >= bestWidth)
                continue;
            best = topLevelId;
            bestWidth = last - first;
        }
        return best;
    }

    /// <summary>未对账大类的日志原因（<see cref="Unreconciled"/> 里登记的那句）。</summary>
    static string Reason(int topLevelId)
    {
        foreach (var (id, _, _, reason) in Unreconciled)
        {
            if (id == topLevelId)
                return reason;
        }
        return "未登记原因";
    }

    /// <summary>
    /// 顶层大类从子叶合并出的维度（子叶为空时返回全 0，由 <see cref="TopLevelFallback"/> 接管）。
    /// 合并而不是各写一份，是为了保证父节点与子节点口径一致。
    /// </summary>
    static AuctionCategoryDimensions Rollup(int topLevelId)
    {
        ulong kinds = 0, slots = 0, groups = 0;
        ushort jobs = 0;
        foreach (var leaf in Leaves)
        {
            if (leaf.TopLevelId != topLevelId)
                continue;
            kinds |= leaf.Dimensions.Kinds;
            slots |= leaf.Dimensions.Slots;
            groups |= leaf.Dimensions.Groups;
            jobs |= leaf.Dimensions.Jobs;   // 职业位按位或：父节点要收本大类下各族的职业
        }
        return new(kinds, slots, groups, jobs);
    }

    /// <summary>未映射 id 的日志说明：在采集清单里的按所属大类说明，其余按「界面变了」处理。</summary>
    static string UnreconciledNote(int categoryId)
    {
        if (UnreconciledTopLevel(categoryId) is { } topLevelId)
            return Reason(topLevelId);
        if (CollectedIds.Contains(categoryId))
            return "id 在采集清单里但未对账：不过滤（表结构未确认，猜错会显示错子集）";
        return "未采集过的分类 id：不过滤（客户端界面可能已变化，需重新采集 id 后再改表）";
    }

    /// <summary>
    /// 过滤实际用到的物品种类（SQL 侧）。给了部位就以**部位能落到的种类**为准，**再并上分类自己声明的种类**：
    /// 并集是必需的——「宠物」同时按物品大类（Creature=5，宠物相关物品）与部位（26 宠物本体、27–29 三色宠物装备）过滤，
    /// 只取部位会把宠物本体整类漏掉，只取种类又会把宠物装备放进来。
    /// 两侧口径必须一致：SQL 侧先剔掉内存侧本该保留的行，表现为「某个分类搜不到东西」；
    /// 反过来让不该出现的种类进入结果，就是串类显示。
    /// </summary>
    public static ImmutableArray<byte> Kinds(AuctionCategoryDimensions dimensions)
    {
        if (dimensions.Slots == 0)
            return dimensions.KindList;
        return AuctionCategoryDimensions.ToList(dimensions.Kinds | KindsOfSlots(dimensions.Slots));
    }

    /// <summary>只有解析出物品大类时才能直接下推到 SQL。</summary>
    public static bool HasKinds(AuctionCategoryDimensions dimensions) => dimensions.Kinds != 0;

    /// <summary>
    /// 一组装备部位**隐含**的物品种类（用于父节点合并兜底维度时补 Kinds）。
    /// 与 <see cref="Kinds"/> 同源，避免「部位 27–29 是宠物装备」这类知识写两遍。
    /// </summary>
    static ulong KindsOfSlots(ulong slots)
    {
        if (slots == 0)
            return 0;
        ulong kinds = 0;
        foreach (var (first, last, kind) in EquipmentSlotKinds)
        {
            if (Overlaps(slots, first, last))
                kinds |= 1UL << (byte)kind;
        }
        return kinds;
    }

    /// <summary>部位位掩码是否与某个部位区间有交集。</summary>
    public static bool Overlaps(ulong slots, int first, int last)
    {
        for (int slot = first; slot <= last; slot++)
        {
            if ((slots & (1UL << slot)) != 0)
                return true;
        }
        return false;
    }

    /// <summary>某大类是否已有对账过的子叶（决定日志措辞与维度来源）。</summary>
    public static bool HasReconciledLeaves(int topLevelId)
    {
        foreach (var leaf in Leaves)
        {
            if (leaf.TopLevelId == topLevelId)
                return true;
        }
        return false;
    }

    static ulong Bit(byte index) => 1UL << index;
    static ulong Bits(params byte[] indexes)
    {
        ulong mask = 0;
        foreach (byte index in indexes)
            mask |= Bit(index);
        return mask;
    }
}

/// <summary>
/// 一个已对账的子叶：客户端 id、所属顶层大类、显示名、过滤维度与**认下来的依据**。
/// <see cref="Evidence"/> 直接进日志与文档，让「为什么这个 id 被认成这个维度」可复核。
/// </summary>
internal sealed record AuctionCategoryLeaf(int Id, int TopLevelId, string Name, AuctionCategoryDimensions Dimensions,
    string Evidence);

/// <summary>
/// 分类 id → 过滤条件的翻译器：把分类节点解析成「物品大类 + 装备部位 + 武器族」。
///
/// 物品大类可以下推到 SQL（<c>auction_listings.item_kind</c> 列），部位与武器族不行：
/// 表里没有这两列，而武器/防具/首饰/宠物装备的子叶**正是按它们区分**的（首饰与宠物装备的
/// item_kind 与父节点相同），且不允许改表结构。把部位展开成「该部位的全部模板 id 再 <c>IN (…)</c>」已被实测否决：
/// 武器一类约 23000 个模板 id，SQLite 每次查询约 12 秒（1 秒 ≈ 2000 个 IN 项），
/// 再加上首次遍历目录解析约 11 秒，代价远超一次搜索能接受的范围。
///
/// 因此这两维在**内存里**过滤：存储层按 SQL 能表达的条件（在售/未过期/物品大类/排除自己/模板）
/// 取回匹配挂牌，本类逐行判定，计数与分页都在过滤后的集合上做——这样匹配总数仍然是过滤后的值，
/// 与 SQL 侧过滤的效果一致（见 <see cref="AuctionService.Search"/>）。
/// 部位按模板 id 查一次缓存一次（<see cref="ConcurrentDictionary{TKey,TValue}"/>），
/// 所以单次搜索只为「本次匹配到的模板」付解析代价，而不是为整个目录付代价。
/// 武器族**不查目录**：族号直接从模板 id 的数字段读出来（见 <see cref="AuctionCategoryFilter.ItemGroupMask"/>），
/// 因此点击武器子叶时不会因为目录里缺某个模板而把整行判错。
/// </summary>
internal sealed class AuctionCategoryResolver(ItemCatalog items)
{
    // 模板 id → 装备部位。解析失败记 -1（不落在任何部位）。字典随进程存活、条目数不超过模板总数，
    // 是**有界只读缓存**：只有查询与插入，没有失效逻辑，因为 PVF 目录在进程内不会变化。
    readonly ConcurrentDictionary<int, int> slots = new();

    // 模板 id → 适用职业位（[usable job]）。与 slots 同一套缓存策略、同样有界。
    readonly ConcurrentDictionary<int, ushort> jobs = new();

    // 模板 id → (品级 Rarity, 最低穿戴等级 MinimumLevel)；未加载记 (-1, -1)。
    readonly ConcurrentDictionary<int, (int Rarity, int MinimumLevel)> attributes = new();

    public AuctionCategoryResolution Resolve(int categoryId) => AuctionCategoryFilter.Resolve(categoryId);

    /// <summary>
    /// 把解析结果转成一次搜索的计划。未映射与「全部」都不带任何维度，从而返回全部匹配；
    /// 两者也都不写部位/族维度，因此不会触发内存过滤。
    /// </summary>
    public AuctionSearchPlan Build(AuctionSearchRequest request, long? excludeSellerCharacterId, bool goldWindow = false)
    {
        bool gold = goldWindow || AuctionGoldTickets.IsCategory(request.CategoryId);
        if (gold)
        {
            var category = AuctionGoldTickets.IsCategory(request.CategoryId)
                ? Resolve(request.CategoryId)
                : new AuctionCategoryResolution(request.CategoryId, AuctionCategoryStatus.Gold, 0, "金币寄售", new(0, 0),
                    "mode=1 金币寄售窗口：只检索金币券挂牌，不套用普通分类维度");
            int? template = request.ItemTemplateIds.IsDefault ? request.ItemTemplateId : null;
            if (template is int itemId && !AuctionGoldTickets.IsTicket(itemId))
                template = AuctionGoldTickets.EmptyTemplateId;
            return new(new(template, excludeSellerCharacterId, Gold: true, ItemTemplateIds: request.ItemTemplateIds), category, null, 0, 0);
        }
        var normal = Resolve(request.CategoryId);
        if (normal.Status != AuctionCategoryStatus.Mapped)
            return new(new(request.ItemTemplateId, excludeSellerCharacterId, Gold: false, ItemTemplateIds: request.ItemTemplateIds), normal, null, 0, 0);
        var dimensions = normal.Dimensions;
        var kinds = AuctionCategoryFilter.Kinds(dimensions);
        var filter = new AuctionSearchFilter(request.ItemTemplateId, excludeSellerCharacterId, kinds, Gold: false,
            ItemTemplateIds: request.ItemTemplateIds);
        bool inMemory = dimensions.Slots != 0 || dimensions.Groups != 0;
        return new(filter, normal, inMemory ? dimensions : null, kinds.Length, dimensions.Groups);
    }

    /// <summary>
    /// 内存判定：模板必须命中分类声明的**每一个非空维度**（部位、族掩码、职业位都要过）。
    /// 只声明一个维度时退化成单维判定；未映射/「全部」没有维度，因此不会被调用。
    /// 宠物装备（红/蓝/绿）与首饰（项链/戒指/手镯）用部位区分，武器子叶用族+职业区分，
    /// 防具子叶用材质+部位区分。
    /// </summary>
    public bool InCategory(AuctionCategoryDimensions dimensions, int templateId)
    {
        // 部位：解析失败的模板一律不匹配（不能因为一个坏模板把整类隐藏成空列表，但也不能放进去）。
        if (dimensions.Slots != 0)
        {
            int slot = Slot(templateId);
            if (slot < 0 || (dimensions.Slots & (1UL << slot)) == 0)
                return false;
        }
        if (dimensions.Groups == 0 && dimensions.Jobs == 0)
            return true;
        // 族掩码：**只有落在族空间里的模板**才参与族判定（族掩码非 0），其它物品（时装、首饰、特殊装备）
        // 的族掩码是 0，交给部位维度决定——否则「武器族过滤 + 部位 12」会把武器时装（模板 id 落在
        // 武器族段但装备部位是 10）在武器子叶下整类挡掉，而它本来就该由部位维度判定。
        ulong groups = AuctionCategoryFilter.ItemGroupMask(templateId);
        if (dimensions.Groups != 0 && groups != 0 && (groups & dimensions.Groups) == 0)
            return false;
        // 职业位：判在**模板自带的 [usable job]** 上（CJob：通用装备不吃职业条件）。
        if (dimensions.Jobs == 0)
            return true;
        ushort jobs = Jobs(templateId);
        return jobs == 0 || (jobs & dimensions.Jobs) != 0;
    }

    /// <summary>
    /// 取模板的适用职业位并缓存（与 <see cref="Slot"/> 同一套有界只读缓存策略）。
    /// 解析失败记 0（= 不限制职业），与「模板解析失败不隐藏整类」的处理一致。
    /// </summary>
    ushort Jobs(int templateId) => jobs.GetOrAdd(templateId, id =>
    {
        try
        {
            return items.Get(id).Jobs;
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or NullReferenceException)
        {
            return 0;
        }
    });

    /// <summary>
    /// 取模板的装备部位并缓存。按模板 id 逐个解析：单次搜索只为「本次匹配到的模板」付代价，
    /// 而不是为整个目录付代价（实测遍历整个目录解析约 11 秒）。
    /// </summary>
    int Slot(int templateId) => slots.GetOrAdd(templateId, id =>
    {
        try
        {
            return items.Get(id).EquipmentSlot;
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or NullReferenceException)
        {
            // 坏模板不能让整个分类搜索失败：按「没有部位」处理，该行不进带部位的分类。
            // 搜索不该依赖资源完整性，但也不能因为一个坏模板把整类隐藏成空列表。
            return -1;
        }
    });

    /// <summary>
    /// 预置模板部位（**仅供核心自测**）：自测没有 PVF，无法从目录取部位，因此把模板表直接灌进同一个缓存。
    /// 只填缓存、不改判定逻辑——解析、SQL 条件、内存过滤、分页仍走真实实现。
    /// </summary>
    internal void PrefillSlots(IEnumerable<KeyValuePair<int, int>> templateSlots)
    {
        foreach (var (templateId, slot) in templateSlots)
            slots[templateId] = slot;
    }

    /// <summary>预置模板职业位（**仅供核心自测**，与 <see cref="PrefillSlots"/> 同理）。</summary>
    internal void PrefillJobs(IEnumerable<KeyValuePair<int, ushort>> templateJobs)
    {
        foreach (var (templateId, job) in templateJobs)
            jobs[templateId] = job;
    }

    /// <summary>预置模板品级与穿戴等级（**仅供核心自测**）。</summary>
    internal void PrefillAttributes(IEnumerable<KeyValuePair<int, (int Rarity, int MinimumLevel)>> templateAttributes)
    {
        foreach (var (templateId, attr) in templateAttributes)
            attributes[templateId] = attr;
    }

    public bool MatchesAttributes(AuctionSearchRequest request, AuctionListing listing)
    {
        if (!request.HasAttributeFilter)
            return true;
        if (request.MinUpgrade > 0 || (request.MaxUpgrade > 0 && request.MaxUpgrade < 31))
        {
            int upgrade = listing.ItemEntry.Length > 10 ? listing.ItemEntry[10] & 0x1F : 0;
            int maxUpgrade = request.MaxUpgrade == 0 ? 31 : request.MaxUpgrade;
            if (upgrade < request.MinUpgrade || upgrade > maxUpgrade)
                return false;
        }
        if (request.Rarity is < 7 || request.MinLevel > 0 || (request.MaxLevel > 0 && request.MaxLevel < 95))
        {
            var (rarity, minLevel) = Attributes(listing.ItemTemplateId);
            if (rarity >= 0 && request.Rarity is < 7 && rarity != request.Rarity.Value)
                return false;
            if (minLevel >= 0)
            {
                int maxLevel = request.MaxLevel == 0 ? int.MaxValue : request.MaxLevel;
                if (minLevel < request.MinLevel || minLevel > maxLevel)
                    return false;
            }
        }
        return true;
    }

    (int Rarity, int MinimumLevel) Attributes(int templateId) => attributes.GetOrAdd(templateId, id =>
    {
        try
        {
            var template = items.Get(id);
            return (template.Rarity, template.MinimumLevel);
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or NullReferenceException)
        {
            return (-1, -1);
        }
    });
}

/// <summary>
/// 一次分类搜索的过滤计划：存储过滤条件 + 解析结果 + 需要在内存里补做的维度
/// （<see cref="SlotDimensions"/> 为 null 表示没有部位/族维度，不需要内存过滤）。
/// <see cref="Kinds"/> 只用于日志，让「客户端点了哪个节点、服务端过滤了什么」可以逐次核对。
/// </summary>
internal sealed record AuctionSearchPlan(AuctionSearchFilter Filter, AuctionCategoryResolution Category,
    AuctionCategoryDimensions? SlotDimensions, int Kinds, ulong Groups = 0);

/// <summary>
/// 一条在售挂牌（<c>auction_listings</c> 的只读投影）。
/// <see cref="ItemEntry"/> 是物品条目的字节快照：本模块不解释它的字段含义，
/// 只在构造拍卖行道具块时按固定偏移取 itemId/value/attr/耐久/封印。
/// </summary>
internal sealed record AuctionListing(
    int ListingId,
    long? SellerCharacterId,
    string SellerName,
    int ItemTemplateId,
    byte ItemKind,
    int ItemCount,
    byte[] ItemEntry,
    int StartingPrice,
    int BuyoutPrice,
    int CurrentBid,
    long PostedAtUnix,
    long ExpiresAtUnix)
{
    /// <summary>
    /// 界面「当前竞拍」列：有出价取最高出价，没有出价取起拍价。
    /// current_bid 为 0 表示还没有人出价，此时用起拍价，不能把 0 当成显示价（起拍价本身可以是 0）。
    /// </summary>
    public int DisplayPrice => CurrentBid > 0 ? CurrentBid : StartingPrice;

    /// <summary>
    /// 表里的 <c>state</c>（<see cref="AuctionListingState"/>）。只读读路径默认 0 = 在售：
    /// 搜索/均价/188 的 WHERE 已经把非在售与过期行挡在外面，只有取消上架（184）需要它来做
    /// 「当前角色 + 在售」这两条校验。
    /// </summary>
    public int State { get; init; }

    /// <summary>
    /// 这一行挂牌**实际收过的保证金**（金币，逐行记录）。到期结算按它原额退还 ——
    /// 不是按常量退：本列出现之前上架的历史行为 0，没交过就不退，绝不凭空造钱。
    /// </summary>
    public int DepositGold { get; init; }
}

/// <summary>
/// 一页搜索结果。<see cref="TotalCount"/> 是过滤后的**匹配总数**，不受 offset/limit 影响：
/// 客户端按每页 10 条算总页数决定「下一页」是否可用，回本批条数会被算成只有一页。
/// <see cref="Plan"/> 是本次分类过滤的说明，由 <see cref="AuctionService.Search"/> 补上（仓储不解释分类），
/// 供 Handler 写与页面数据同源的日志。
/// </summary>
internal sealed record AuctionSearchPage(ImmutableArray<AuctionListing> Listings, int TotalCount)
{
    public AuctionSearchPlan? Plan { get; init; }
}

/// <summary>
/// `auction_listings.state` 的取值（表约束 0–3）。**在售口径只认 <see cref="Active"/>**：
/// 搜索、均价、188「我的上架」都只取 state=0 且未过期的行；190「拍卖历史」才包含其余状态。
/// </summary>
internal static class AuctionListingState
{
    /// <summary>在售。</summary>
    public const int Active = 0;

    /// <summary>已成交（本仓库尚未接入成交/结算）。</summary>
    public const int Sold = 1;

    /// <summary>已下架：CMD 184 取消上架写这个状态。</summary>
    public const int Cancelled = 2;

    /// <summary>已过期（本仓库未接入过期流转，过期只由 expires_at_unix 判定）。</summary>
    public const int Expired = 3;
}

/// <summary>
/// 某个物品模板的当前在售均价（CMD 182 均价查询的数据来源）。
/// <see cref="Average"/> 是整件均价（向下取整），<see cref="Count"/> 是参与统计的在售挂牌数：
/// 没有在售时两者都是 0，应答按「无均价」下发而不是编一个价。
/// </summary>
internal readonly record struct AuctionAveragePrice(int Average, int Count)
{
    /// <summary>无在售挂牌：均价与件数都是 0。</summary>
    public static readonly AuctionAveragePrice None = new(0, 0);
}

/// <summary>CMD 182 应答里 <c>key</c> 字段的取值来源，逐次记进日志便于事后分辨「这个价是哪来的」。</summary>
internal enum AuctionKeySource
{
    /// <summary>有在售挂牌：key = 在售均价。</summary>
    Listings,

    /// <summary>无在售挂牌：key = 该模板的**商店售价**（PVF `[price]` / `[value]`，见 <c>ItemShopPrice.Gold</c>）。</summary>
    ShopPrice,

    /// <summary>无在售且取不到商店价（模板缺失或商店价为 0）：key = 兜底 1。</summary>
    Minimum
}

/// <summary>
/// CMD 182 应答的 <c>key</c>（客户端预填进一口价输入框的参考单价）及其取值依据。
///
/// **为什么无在售也不能回 0**（这是「窗口一直停在『正在统计拍卖行价格……』、售价框预填 0、上架必失败」的根因）：
/// 参考实现 `Data/auction_avg_reply.txt:441` 写明该字段**兼作「有价格数据」的非零门**
/// （客户端 `0x1455900` 判 `[+0x1a4] != 0`），因此**不能下发 0**，其实现在
/// `AuctionHandler.cs:3236-3237, 3469-3472` 里被**夹到至少 1**；
/// 同文件 `:307-312` 的 `keyminprice` 给出无在售时的官方兜底：回落到 `keyprice`（它配置成 1000）而不是 0。
/// 本仓库的兜底取**该模板自己的商店售价**（比固定常数更贴近客户端「不能低于商店销售的价格」这条校验），
/// 取不到才回 1。
/// </summary>
internal readonly record struct AuctionKeyPrice(int Average, int Count, int Floor, int Key, AuctionKeySource Source, string? Note)
{
    /// <summary>兜底下限：客户端把 key=0 当成「没有价格数据」，最小值必须 ≥ 1。</summary>
    public const int MinimumKey = 1;
}

/// <summary>待写入的挂牌（开发种子与后续正式上架共用同一形状）。</summary>
internal sealed record AuctionListingDraft(
    long? SellerCharacterId,
    string SellerName,
    int ItemTemplateId,
    byte ItemKind,
    int ItemCount,
    byte[] ItemEntry,
    int StartingPrice,
    int BuyoutPrice,
    long PostedAtUnix,
    long ExpiresAtUnix)
{
    /// <summary>
    /// 本笔挂牌收取的保证金（金币）。正式上架路径填 <see cref="AuctionService.ListingDepositGold"/>；
    /// 开发种子与历史数据保持 0 —— 到期结算只退这一行真正收过的金额（见 <see cref="AuctionListing.DepositGold"/>）。
    /// </summary>
    public int DepositGold { get; init; }
}

/// <summary>
/// 种子使用的一件物品：模板 id、物品种类、实际数量与条目字节快照。
/// 快照由物品模块的编码入口产生后传进来，因此本层不引用 Network，也不需要 PVF 就能自测种子。
/// </summary>
internal readonly record struct AuctionSeedItem(int ItemTemplateId, byte ItemKind, int Count, byte[] Entry);

/// <summary>
/// 种子结果：<see cref="Created"/> 为 0 表示没有写入（--auction-seed 为 0，或表内已有挂牌），
/// <see cref="ItemIds"/> 是本次写入的挂牌按行对应的物品模板 id，供启动日志核对。
/// </summary>
internal sealed record AuctionSeedResult(int Created, ImmutableArray<int> ItemIds);
