using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Marriage;

internal readonly record struct MarriageItemCount(int ItemId, int Count);
internal readonly record struct MarriageLetter(int FirstRaw, int SecondRaw);
internal readonly record struct MarriageForceMove(int X, int Y, int ThirdRaw);
internal sealed record MarriageCeremonyDefinition(int Type, int Gold, int AvatarType, int LettersPerPersonRaw);
internal sealed record MarriageTiming(ImmutableArray<int> SelectionSeconds, int NotifyEndSeconds,
    int OnceHowManyDays, int DaysAfterDivorce, ImmutableArray<MarriageForceMove> ForceMove);
internal sealed record MarriageGuestGiftTier(int UpperBound,
    ImmutableDictionary<int, ImmutableArray<MarriageItemCount>> GiftsByWeddingType);
internal sealed record MarriageLovePoints(int DailyRaw, int Max, ImmutableDictionary<int, int> ItemPoints);
internal readonly record struct MarriageBuffEffect(string Name, int Value);
internal sealed record MarriageRingDefinition(int Level, string NametagAction, ImmutableArray<MarriageBuffEffect> Buffs);
internal readonly record struct MarriageTreeStep(int GrowPoint, string Animation);
internal readonly record struct MarriageTreeReward(int ItemId, int Weight, int Count);
internal sealed record MarriageTreeDefinition(int Index, string Name, int ExpireDays, int DayWaterCount,
    ImmutableDictionary<int, int> GrowPoints, ImmutableArray<MarriageTreeStep> Steps,
    ImmutableArray<MarriageTreeReward> Rewards)
{
    public long TotalRewardWeight => Rewards.Sum(row => (long)row.Weight);
}
internal readonly record struct MarriageRoomPosition(int Id, int X, int Y, int ThirdRaw);
internal readonly record struct MarriageFurniturePlacement(int PositionId, int FirstRaw, int SecondRaw, int ThirdRaw, int FourthRaw);
internal sealed record MarriageFurnitureDefinition(int ItemId, ImmutableArray<MarriageFurniturePlacement> Placements,
    int TailRaw, string Action);
internal sealed record MarriageRoomBuffTier(int Threshold, ImmutableArray<MarriageBuffEffect> Buffs);
internal sealed record MarriageGrowthCapsule(int MaxOwnCount, int MaxTakenCount, int Gage, int MaxGage, int CapsuleId);

// 仅解释匹配资源中的声明；关系、每日额度和当前布置由业务与持久化层拥有。
internal sealed class MarriageCatalog
{
    public const string WeddingResourcePath = "etc/weddingsystem.etc";
    public const string AvatarResourcePath = "etc/weddingsystemavatar.etc";
    public const string RoomResourcePath = "decoration/coupleroomiteminfo.etc";
    public const string TreeResourcePath = "decoration/coupletree.etc";
    public const string CapsuleResourcePath = "etc/couplegrowthcapsule.etc";

    public ImmutableArray<int> Quests { get; }
    public int MinLevel { get; }
    public ImmutableDictionary<int, MarriageCeremonyDefinition> Ceremonies { get; }
    public MarriageTiming Timing { get; }
    public int MoneyGiftMin { get; }
    public int MoneyGiftMax { get; }
    // 资源未在此层认定其百分比、舍入或收款对象语义。
    public int MoneyGiftChargeRaw { get; }
    public ImmutableDictionary<int, MarriageItemCount> WeddingGifts { get; }
    public ImmutableArray<MarriageGuestGiftTier> GuestGiftTiers { get; }
    public ImmutableArray<MarriageLetter> Letters { get; }
    public ImmutableArray<PvfToken> InvitationGiftRaw { get; }
    public ImmutableArray<MarriageItemCount> CeremonyGifts { get; }
    public ImmutableArray<int> AlimonyRaw { get; }
    public ImmutableDictionary<int, MarriageItemCount> ChargedWeddingTickets { get; }
    // 首项可为 0/0；保留资源顺序，不把空券项伪造成可消耗物品。
    public ImmutableArray<MarriageItemCount> UpgradeRingTickets { get; }
    public ImmutableDictionary<int, MarriageRingDefinition> Rings { get; }
    public ImmutableDictionary<(int Job, int WeddingType), ImmutableArray<int>> WeddingAvatars { get; }
    public MarriageLovePoints LovePoints { get; }
    // [cerashop restrict item] 为仅限已婚角色购买的商城商品编号，不是STK物品编号。
    public ImmutableArray<int> CeraShopRestrictItems { get; }
    public ImmutableDictionary<int, int> Seeds { get; }
    public MarriageLetter TreeRewardLetter { get; }
    public ImmutableDictionary<int, MarriageTreeDefinition> Trees { get; }
    public ImmutableDictionary<int, MarriageRoomPosition> RoomPositions { get; }
    public ImmutableDictionary<int, MarriageFurnitureDefinition> Furniture { get; }
    public ImmutableArray<MarriageRoomBuffTier> RoomBuffs { get; }
    public MarriageGrowthCapsule GrowthCapsule { get; }
    // 未解释字段仍保留其 token、顺序与重复块，不能静默丢掉后再声称支持其业务效果。
    public ImmutableDictionary<string, ImmutableArray<PvfTokenSection>> RawResources { get; }

    public static MarriageCatalog Load(PvfArchive pvf) => Parse(pvf.ReadTokens, pvf.ResolveString);

    public static MarriageCatalog Parse(Func<string, IReadOnlyList<PvfToken>> read, Func<int, string> resolve) => new(read, resolve);

    MarriageCatalog(Func<string, IReadOnlyList<PvfToken>> read, Func<int, string> resolve)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(resolve);
        var raw = ImmutableDictionary.CreateBuilder<string, ImmutableArray<PvfTokenSection>>(StringComparer.Ordinal);
        ImmutableArray<PvfTokenSection> Read(string path)
        {
            var document = new PvfTokenDocument(read(path), resolve);
            if (!document.Diagnostics.IsEmpty) throw Invalid(path + " 声明未正确闭合");
            raw.Add(path, document.Sections);
            return document.Sections;
        }

        var wedding = Read(WeddingResourcePath);
        Quests = Numbers(One(wedding, "quest", true));
        if (Quests.IsEmpty || Quests.Any(id => id <= 0) || Quests.Distinct().Count() != Quests.Length)
            throw Invalid("婚礼任务列表无效");
        MinLevel = Scalar(wedding, "minimum level", true, positive: true);
        var ceremonies = ImmutableDictionary.CreateBuilder<int, MarriageCeremonyDefinition>();
        var ceremonyValues = Numbers(One(wedding, "ceremony", true));
        RequireGroups(ceremonyValues.Length, 4, "ceremony");
        for (int at = 0; at < ceremonyValues.Length; at += 4)
        {
            int type = ceremonyValues[at], gold = ceremonyValues[at + 1], avatar = ceremonyValues[at + 2], letters = ceremonyValues[at + 3];
            if (type < 0 || gold < 0 || avatar < 0 || letters < 0 || !ceremonies.TryAdd(type, new(type, gold, avatar, letters)))
                throw Invalid("婚礼类型重复或参数无效");
        }
        Ceremonies = ceremonies.ToImmutable();
        var seconds = Numbers(One(wedding, "minute selection", true));
        Increasing(seconds, "minute selection");
        var moves = Numbers(One(wedding, "force move", true), 6);
        if (moves[0] < 0 || moves[1] < 0 || moves[3] < 0 || moves[4] < 0) throw Invalid("婚礼移动坐标无效");
        Timing = new(seconds, Scalar(wedding, "noti end", true), Scalar(wedding, "once how many days", true),
            Scalar(wedding, "days after divorce", true), [new(moves[0], moves[1], moves[2]), new(moves[3], moves[4], moves[5])]);
        var money = Numbers(One(wedding, "money gift limit", true), 2);
        if (money[0] < 0 || money[1] < money[0]) throw Invalid("礼金上下限无效");
        MoneyGiftMin = money[0]; MoneyGiftMax = money[1];
        MoneyGiftChargeRaw = Scalar(wedding, "money gift charge", true);
        WeddingGifts = KindItems(Numbers(One(wedding, "wedding gift", true)), Ceremonies.Keys);
        ChargedWeddingTickets = KindItems(Numbers(One(wedding, "charged wedding ticket", true)), Ceremonies.Keys);
        var letterValues = Numbers(One(wedding, "letter", true));
        RequireGroups(letterValues.Length, 2, "letter");
        if (letterValues.Any(value => value < 0)) throw Invalid("婚礼信件参数无效");
        Letters = letterValues.Chunk(2).Select(pair => new MarriageLetter(pair[0], pair[1])).ToImmutableArray();
        InvitationGiftRaw = One(wedding, "invitation gift", true).Values;
        CeremonyGifts = ItemPairs(Numbers(One(wedding, "ceremony gift", true)));
        AlimonyRaw = Numbers(One(wedding, "alimony", true), 2);
        if (AlimonyRaw.Any(value => value < 0)) throw Invalid("离婚费用无效");
        GuestGiftTiers = ReadGuestGifts(wedding, Ceremonies.Keys);
        if (GuestGiftTiers[^1].UpperBound < MoneyGiftMax) throw Invalid("来宾礼物档位未覆盖礼金上限");

        UpgradeRingTickets = ItemPairs(Numbers(One(wedding, "upgrade ring ticket", true)), allowEmptyPair: true);
        var actions = One(wedding, "wedding ring nametag action", true).Values;
        RequireGroups(actions.Length, 2, "wedding ring nametag action");
        var names = ImmutableDictionary.CreateBuilder<int, string>();
        for (int at = 0; at < actions.Length; at += 2)
        {
            int level = Integer(actions[at]);
            if (level < 0 || !names.TryAdd(level, Text(actions[at + 1], resolve))) throw Invalid("婚戒名牌等级重复或无效");
        }
        var ringBuffs = ReadBuffs(One(wedding, "wedding ring buff", true).Values, resolve, allowZeroKey: true);
        if (names.Count != ringBuffs.Length || UpgradeRingTickets.Length != ringBuffs.Length ||
            ringBuffs.Any(row => !names.ContainsKey(row.Threshold))) throw Invalid("婚戒券、名牌与增益等级不一致");
        Rings = ringBuffs.ToImmutableDictionary(row => row.Threshold,
            row => new MarriageRingDefinition(row.Threshold, names[row.Threshold], row.Buffs));
        var loveItems = IntegerPairs(Numbers(One(wedding, "love point item", true)), "love point item");
        LovePoints = new(Scalar(wedding, "love point", true), Scalar(wedding, "max love point", true, positive: true), loveItems);
        if (LovePoints.DailyRaw > LovePoints.Max) throw Invalid("每日爱心参数超过上限");
        CeraShopRestrictItems = Numbers(One(wedding, "cerashop restrict item", true));
        if (CeraShopRestrictItems.Any(id => id <= 0) || CeraShopRestrictItems.Distinct().Count() != CeraShopRestrictItems.Length)
            throw Invalid("婚礼商城限制列表无效");
        WeddingAvatars = ReadAvatars(Read(AvatarResourcePath), Ceremonies.Values.Select(row => row.AvatarType));

        var tree = Read(TreeResourcePath);
        Seeds = IntegerPairs(Numbers(One(tree, "seed", true)), "seed");
        var rewardLetter = Numbers(One(tree, "reward letter", true), 2);
        if (rewardLetter.Any(value => value < 0)) throw Invalid("树奖励信件参数无效");
        TreeRewardLetter = new(rewardLetter[0], rewardLetter[1]);
        Trees = ReadTrees(tree, resolve);
        if (Seeds.Values.Any(id => !Trees.ContainsKey(id))) throw Invalid("种子引用未知夫妻树");

        var room = Read(RoomResourcePath);
        var positionValues = Numbers(One(room, "position list", true));
        RequireGroups(positionValues.Length, 4, "position list");
        var positions = ImmutableDictionary.CreateBuilder<int, MarriageRoomPosition>();
        for (int at = 0; at < positionValues.Length; at += 4)
        {
            int id = positionValues[at];
            if (id < 0 || !positions.TryAdd(id, new(id, positionValues[at + 1], positionValues[at + 2], positionValues[at + 3])))
                throw Invalid("夫妻房间位置编号重复或无效");
        }
        RoomPositions = positions.ToImmutable();
        Furniture = ReadFurniture(room, RoomPositions.Keys, resolve);
        RoomBuffs = ReadBuffs(One(room, "couple room buff", true).Values, resolve, allowZeroKey: false);
        var capsule = Read(CapsuleResourcePath);
        GrowthCapsule = new(Scalar(capsule, "max own count", false, positive: true), Scalar(capsule, "max taken count", false, positive: true),
            Scalar(capsule, "gage", false, positive: true), Scalar(capsule, "max gage", false, positive: true),
            Scalar(capsule, "capsule id", false, positive: true));
        if (GrowthCapsule.Gage > GrowthCapsule.MaxGage) throw Invalid("成长胶囊单次刻度超过上限");
        RawResources = raw.ToImmutable();
    }

    static ImmutableArray<MarriageGuestGiftTier> ReadGuestGifts(ImmutableArray<PvfTokenSection> fields, IEnumerable<int> kinds)
    {
        var tiers = ImmutableArray.CreateBuilder<MarriageGuestGiftTier>();
        foreach (var field in fields.Where(row => row.Name == "guest gift"))
        {
            Flat(field, true);
            var values = Numbers(field);
            if (values.Length < 4 || (values.Length - 1) % 3 != 0 || values[0] < 0 ||
                tiers.Count > 0 && values[0] <= tiers[^1].UpperBound) throw Invalid("来宾礼物阈值或记录长度无效");
            var gifts = new Dictionary<int, ImmutableArray<MarriageItemCount>.Builder>();
            for (int at = 1; at < values.Length; at += 3)
            {
                int kind = values[at];
                if (!kinds.Contains(kind)) throw Invalid("来宾礼物引用未知婚礼类型");
                if (!gifts.TryGetValue(kind, out var items)) gifts.Add(kind, items = ImmutableArray.CreateBuilder<MarriageItemCount>());
                var item = Item(values[at + 1], values[at + 2]);
                if (items.Any(row => row.ItemId == item.ItemId)) throw Invalid("同档同类来宾礼物重复");
                items.Add(item);
            }
            if (kinds.Any(kind => !gifts.ContainsKey(kind))) throw Invalid("来宾礼物缺少婚礼类型");
            tiers.Add(new(values[0], gifts.ToImmutableDictionary(row => row.Key, row => row.Value.ToImmutable())));
        }
        if (tiers.Count == 0) throw Invalid("缺少来宾礼物档位");
        return tiers.ToImmutable();
    }

    static ImmutableDictionary<(int Job, int WeddingType), ImmutableArray<int>> ReadAvatars(
        ImmutableArray<PvfTokenSection> fields, IEnumerable<int> avatarTypes)
    {
        var block = Block(fields, "wedding avatar");
        var result = ImmutableDictionary.CreateBuilder<(int, int), ImmutableArray<int>>();
        if (block.Children.IsEmpty || block.Children.Length % 2 != 0) throw Invalid("婚礼装扮职业与列表未成对");
        for (int at = 0; at < block.Children.Length; at += 2)
        {
            var job = block.Children[at]; var list = block.Children[at + 1];
            if (job.Name != "job wedding" || list.Name != "avatar list") throw Invalid("婚礼装扮声明顺序无效");
            Flat(job, false); Flat(list, true);
            var key = Numbers(job, 2); var ids = Numbers(list, 11);
            if (key[0] < 0 || !avatarTypes.Contains(key[1]) || ids.Any(id => id != -1 && id <= 0) ||
                !result.TryAdd((key[0], key[1]), ids)) throw Invalid("婚礼装扮职业、类型或槽位无效");
        }
        foreach (int job in result.Keys.Select(key => key.Item1).Distinct())
            if (avatarTypes.Any(type => !result.ContainsKey((job, type)))) throw Invalid("婚礼装扮职业缺少类型");
        return result.ToImmutable();
    }

    static ImmutableDictionary<int, MarriageTreeDefinition> ReadTrees(ImmutableArray<PvfTokenSection> fields, Func<int, string> resolve)
    {
        var result = ImmutableDictionary.CreateBuilder<int, MarriageTreeDefinition>();
        foreach (var tree in fields.Where(row => row.Name == "tree"))
        {
            if (!tree.IsBlock || !tree.Values.IsEmpty) throw Invalid("夫妻树块结构无效");
            int id = Scalar(tree.Children, "index", false, positive: true);
            int days = Scalar(tree.Children, "expire day", false, positive: true);
            int water = Scalar(tree.Children, "day water count", false, positive: true);
            var name = One(tree.Children, "name", false).Values;
            if (name.Length != 1) throw Invalid("夫妻树名称无效");
            var grow = IntegerPairs(Numbers(One(tree.Children, "grow point", true)), "grow point");
            var stageValues = One(tree.Children, "step", true).Values;
            RequireGroups(stageValues.Length, 2, "step");
            var stages = ImmutableArray.CreateBuilder<MarriageTreeStep>();
            for (int at = 0; at < stageValues.Length; at += 2)
            {
                int point = Integer(stageValues[at]);
                if (point <= 0 || stages.Count > 0 && point <= stages[^1].GrowPoint) throw Invalid("夫妻树阶段刻度无效");
                stages.Add(new(point, Text(stageValues[at + 1], resolve)));
            }
            var rewardValues = Numbers(One(tree.Children, "reward", true));
            RequireGroups(rewardValues.Length, 3, "reward");
            var rewards = ImmutableArray.CreateBuilder<MarriageTreeReward>();
            for (int at = 0; at < rewardValues.Length; at += 3)
            {
                _ = Item(rewardValues[at], rewardValues[at + 2]);
                if (rewardValues[at + 1] <= 0) throw Invalid("夫妻树奖励权重无效");
                rewards.Add(new(rewardValues[at], rewardValues[at + 1], rewardValues[at + 2]));
            }
            if (!result.TryAdd(id, new(id, Text(name[0], resolve), days, water, grow, stages.ToImmutable(), rewards.ToImmutable())))
                throw Invalid("夫妻树编号重复");
        }
        if (result.Count == 0) throw Invalid("缺少夫妻树定义");
        return result.ToImmutable();
    }

    static ImmutableDictionary<int, MarriageFurnitureDefinition> ReadFurniture(ImmutableArray<PvfTokenSection> fields,
        IEnumerable<int> positions, Func<int, string> resolve)
    {
        var block = Block(fields, "coupleroom item list");
        var result = ImmutableDictionary.CreateBuilder<int, MarriageFurnitureDefinition>();
        foreach (var field in block.Children.Where(row => row.Name == "coupleroom item"))
        {
            Flat(field, true);
            var values = field.Values;
            if (values.Length < 9) throw Invalid("夫妻家具记录不完整");
            int id = Integer(values[0]), count = Integer(values[1]);
            if (id <= 0 || count <= 0 || (long)count * 5 + 4 != values.Length) throw Invalid("夫妻家具位置数量不符");
            var placements = ImmutableArray.CreateBuilder<MarriageFurniturePlacement>();
            for (int at = 2; at < 2 + count * 5; at += 5)
            {
                int position = Integer(values[at]);
                if (!positions.Contains(position) || placements.Any(row => row.PositionId == position)) throw Invalid("夫妻家具位置未知或重复");
                placements.Add(new(position, Integer(values[at + 1]), Integer(values[at + 2]), Integer(values[at + 3]), Integer(values[at + 4])));
            }
            if (!result.TryAdd(id, new(id, placements.ToImmutable(), Integer(values[^2]), Text(values[^1], resolve))))
                throw Invalid("夫妻家具物品编号重复");
        }
        if (result.Count == 0) throw Invalid("缺少夫妻家具定义");
        return result.ToImmutable();
    }

    static ImmutableArray<MarriageRoomBuffTier> ReadBuffs(ImmutableArray<PvfToken> values, Func<int, string> resolve, bool allowZeroKey)
    {
        var result = ImmutableArray.CreateBuilder<MarriageRoomBuffTier>();
        for (int at = 0; at < values.Length;)
        {
            if (values.Length - at < 2) throw Invalid("增益表缺少头部");
            int key = Integer(values[at++]), count = Integer(values[at++]);
            if (key < (allowZeroKey ? 0 : 1) || count <= 0 || count > (values.Length - at) / 2 ||
                result.Count > 0 && key <= result[^1].Threshold) throw Invalid("增益等级、顺序或数量无效");
            var effects = ImmutableArray.CreateBuilder<MarriageBuffEffect>();
            for (int index = 0; index < count; index++)
            {
                string name = Text(values[at++], resolve);
                int value = Integer(values[at++]);
                if (effects.Any(row => row.Name == name)) throw Invalid("同档增益字段重复");
                effects.Add(new(name, value));
            }
            result.Add(new(key, effects.ToImmutable()));
        }
        if (result.Count == 0) throw Invalid("缺少增益表");
        return result.ToImmutable();
    }

    static ImmutableDictionary<int, MarriageItemCount> KindItems(ImmutableArray<int> values, IEnumerable<int> kinds)
    {
        RequireGroups(values.Length, 3, "婚礼类型物品");
        var result = ImmutableDictionary.CreateBuilder<int, MarriageItemCount>();
        for (int at = 0; at < values.Length; at += 3)
            if (!kinds.Contains(values[at]) || !result.TryAdd(values[at], Item(values[at + 1], values[at + 2])))
                throw Invalid("婚礼物品类型重复或未知");
        return result.ToImmutable();
    }

    static ImmutableArray<MarriageItemCount> ItemPairs(ImmutableArray<int> values, bool allowEmptyPair = false)
    {
        RequireGroups(values.Length, 2, "物品数量对");
        var items = ImmutableArray.CreateBuilder<MarriageItemCount>();
        for (int at = 0; at < values.Length; at += 2)
            items.Add(allowEmptyPair && values[at] == 0 && values[at + 1] == 0 ? new(0, 0) : Item(values[at], values[at + 1]));
        return items.ToImmutable();
    }

    static MarriageItemCount Item(int itemId, int count) => itemId > 0 && count > 0 ? new(itemId, count) : throw Invalid("物品或数量不是正整数");

    static ImmutableDictionary<int, int> IntegerPairs(ImmutableArray<int> values, string name)
    {
        RequireGroups(values.Length, 2, name);
        var result = ImmutableDictionary.CreateBuilder<int, int>();
        for (int at = 0; at < values.Length; at += 2)
            if (values[at] <= 0 || values[at + 1] <= 0 || !result.TryAdd(values[at], values[at + 1])) throw Invalid(name + "重复或非正整数");
        return result.ToImmutable();
    }

    static PvfTokenSection One(ImmutableArray<PvfTokenSection> fields, string name, bool block)
    {
        var matches = fields.Where(row => row.Name == name).ToArray();
        if (matches is not [var field]) throw Invalid("字段缺失或重复: " + name);
        Flat(field, block);
        return field;
    }

    static PvfTokenSection Block(ImmutableArray<PvfTokenSection> fields, string name)
    {
        var matches = fields.Where(row => row.Name == name).ToArray();
        if (matches is not [var field] || !field.IsBlock || !field.Values.IsEmpty) throw Invalid("资源块缺失、重复或结构无效: " + name);
        return field;
    }

    static void Flat(PvfTokenSection field, bool block)
    {
        if (field.IsBlock != block || !field.Children.IsEmpty) throw Invalid("字段结构无效: " + field.Name);
    }

    static ImmutableArray<int> Numbers(PvfTokenSection field, int? count = null)
    {
        if (count is { } length && field.Values.Length != length) throw Invalid("字段长度无效: " + field.Name);
        return field.Values.Select(Integer).ToImmutableArray();
    }

    static int Scalar(ImmutableArray<PvfTokenSection> fields, string name, bool block, bool positive = false)
    {
        int value = Numbers(One(fields, name, block), 1)[0];
        if (value < (positive ? 1 : 0)) throw Invalid("单值字段范围无效: " + name);
        return value;
    }

    static int Integer(PvfToken token) => token.Type == 0 ? token.Value : throw Invalid("字段不是整数token");
    static string Text(PvfToken token, Func<int, string> resolve) => token.Type == 6 && !string.IsNullOrWhiteSpace(resolve(token.Value))
        ? resolve(token.Value) : throw Invalid("字段不是非空字符串token");
    static void RequireGroups(int count, int stride, string name)
    {
        if (count == 0 || count % stride != 0) throw Invalid("字段记录长度无效: " + name);
    }
    static void Increasing(ImmutableArray<int> values, string name)
    {
        if (values.IsEmpty || values[0] <= 0 || values.Zip(values.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw Invalid("字段必须是递增正整数: " + name);
    }
    static InvalidDataException Invalid(string message) => new("结婚系统资源无效: " + message);
}
