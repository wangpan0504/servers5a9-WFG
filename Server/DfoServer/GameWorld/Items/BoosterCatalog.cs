using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record BoosterReward(int ItemId, long Weight, int Count)
{
    public bool EmptyOutcome { get; init; }
    // 多行 [avatar] 与 [special avatar] 的第 4、5 列。null 表示单行 [avatar] 的尾列不参与发放。
    public int? AvatarPeriodDays { get; init; }
    public byte? AvatarAbility { get; init; }
}
internal sealed record BoosterUsePlacesDefinition(bool Village = false, bool SeriaRoom = false, string? PendingRule = null)
{
    public bool Dungeon { get; init; }
    public bool ExcludeAssault { get; init; }
    public bool Pvp { get; init; }
    public bool ExclusionOnly { get; init; }
    public bool Tournament { get; init; }
    public bool DontOpen { get; init; }
    public bool ExcludePractice { get; init; }

    public bool Allows(StackableUseContext context)
    {
        var flags = (Village ? StackableUsePlaceFlags.Village : 0) |
            (SeriaRoom ? StackableUsePlaceFlags.SeriaRoom : 0) | (Dungeon ? StackableUsePlaceFlags.Dungeon : 0) |
            (Pvp ? StackableUsePlaceFlags.Pvp : 0) | (Tournament ? StackableUsePlaceFlags.Tournament : 0) |
            (ExcludeAssault ? StackableUsePlaceFlags.ExcludeAssault : 0) |
            (ExcludePractice ? StackableUsePlaceFlags.ExcludePractice : 0) | (DontOpen ? StackableUsePlaceFlags.DontOpen : 0);
        return PendingRule is null && !(ExclusionOnly && flags == 0) && StackableUsePlaces.Allows(flags, context);
    }
}
internal sealed record BoosterPool(int Draws, ImmutableArray<BoosterReward> Rewards)
{
    public string? JobTag { get; init; }
    public int MinimumLevel { get; init; }
    public int MaximumLevel { get; init; } = int.MaxValue;
    public int GrowType { get; init; } = -1;
    public bool IsCharacterPool { get; init; }
    public ItemKind? RequiredKind { get; init; }

    public bool Matches(byte job, byte growType, byte level) => JobTag is null ||
        (string.Equals(JobTag, ItemCatalog.JobTag(job), StringComparison.OrdinalIgnoreCase) &&
            ((IsCharacterPool && GrowType == -1) || (growType & 15) == GrowType) &&
            level >= MinimumLevel && level <= MaximumLevel);
}
internal sealed record BoosterDefinition(ImmutableArray<BoosterPool> Pools, string? PendingRule = null,
    BoosterSelectionDefinition? Selection = null, ImmutableArray<BoosterReward> PackageRewards = default, bool Lottery = false,
    bool NpcPocketPot = false)
{
    public ImmutableArray<byte> FirstGrowTypes { get; init; } = [];
    public bool AllowsFirstGrowType(byte growType) => FirstGrowTypes.IsEmpty || FirstGrowTypes.Contains((byte)(growType & 15));
    public bool UnsealsRandomOptions { get; init; }
    public int? CustomAbilityGrade { get; init; }
    public bool SetsDisjointInstanceType { get; init; }
    public bool HasFixedOrdinaryRewards => !Lottery && PackageRewards.IsDefaultOrEmpty &&
        !Pools.IsEmpty && Pools.All(pool => pool is
            { Draws: 1, JobTag: null, Rewards: [{ Weight: > 0, Count: > 0 }] });
    public bool ForbidsDestructionSouls { get; init; }
    public bool SetsEpicExchangeInstanceType { get; init; }
    // 休亚商店在售的选组礼盒：PVF 没写交换声明，但产物同样是在休亚处兑换来的。
    public bool FromExchangeShop { get; init; }
    // 显式声明记录禁交换礼盒来源；休亚商店来源另行授予交换资格。
    public bool GrantsEpicExchangeOrigin => SetsEpicExchangeInstanceType || (FromExchangeShop && Selection is not null);
    public byte? DailyRetainedLimit { get; init; }
    public byte? DailyConsumedLimit { get; init; }
    public byte? DailyUseLimit => DailyConsumedLimit ?? DailyRetainedLimit;
    public ItemMaterialCost? LotteryUseNeedItem { get; init; }
    public uint LotteryGoldCost { get; init; }
    public bool AllowsLotteryGold { get; init; }
    // [legacy] 的随机礼盒只带族/档，产物池由 etc/runesystem.etc 的 [random choose info] 决定。
    public (int Family, int Tier)? RuneBox { get; init; }
    public int CoolTime { get; init; }
    public int? CoolGroup { get; init; }
    public byte? CooltimeMaintenance { get; init; }
    public bool Available => PendingRule is null &&
        (Pools.Length != 0 || Selection is not null || !PackageRewards.IsDefaultOrEmpty || RuneBox is not null);
}

internal sealed class BoosterCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable,
    FrozenSet<int>? exchangeShopStock = null)
{
    readonly ConcurrentDictionary<int, BoosterDefinition> definitions = new();
    readonly ConcurrentDictionary<int, BoosterUsePlacesDefinition> usePlaces = new();
    public BoosterUsePlacesDefinition UsePlaces(int itemId) => usePlaces.GetOrAdd(itemId, id =>
        stackable.TryGetValue(id, out string? path) ? ParseUsePlaces(StackableFile.ReadActionUsablePlaces(
            archive.ReadTokens("stackable/" + path), archive.ResolveString)) :
            new(PendingRule: "非礼包源物品"));
    readonly Lazy<FrozenSet<int>> dailyRetainedTemplateIds = new(() => stackable.Select(row =>
        (row.Key, Text: archive.ReadText("stackable/" + row.Value)))
        .Where(row => row.Text.Contains("[total usable count]", StringComparison.OrdinalIgnoreCase) &&
            Parse(row.Text) is { Available: true, DailyRetainedLimit: not null })
        .Select(row => row.Key).ToFrozenSet());
    public IReadOnlyCollection<int> DailyRetainedTemplateIds => dailyRetainedTemplateIds.Value;
    public int DailyRetainedScannedTemplates => stackable.Count;
    // 契约在获取时生效，需要账号期限事务，不能作为普通消耗品发到背包。
    public FrozenSet<int> DirectActivationItems { get; } = ActivationItems(archive);
    public bool AllowsDevilDouble(int itemId) => stackable.TryGetValue(itemId, out string? path) &&
        !path.StartsWith("event/", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("quest/", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("ect/event", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("ect/quest", StringComparison.OrdinalIgnoreCase) && Get(itemId) is { Lottery: true, NpcPocketPot: true };

    // 商店来源只在运行时按在售编号补上：同款礼盒在 PVF 内不带交换声明，判定只依赖商人库存。
    public BoosterDefinition Get(int itemId) => definitions.GetOrAdd(itemId, id => stackable.TryGetValue(id, out string? path)
        ? CorrectShenxuanPackageContents(id, path, Parse(archive.ReadText("stackable/" + path))) with
        { FromExchangeShop = exchangeShopStock?.Contains(id) == true }
        : new([], "非礼包物品"));

    // 神选固定礼包发放前19项内容；只有已确认的56个源及完整末项形态才应用此内容修正。
    // 原始解析和其他礼包继续保留全部声明，未知奖励仍由原发奖校验拒绝。
    internal static BoosterDefinition CorrectShenxuanPackageContents(int itemId, string path, BoosterDefinition definition)
    {
        var rewards = definition.PackageRewards;
        if (itemId is < 490703475 or > 490703530 || path != $"cash/chn_{itemId}.stk" ||
            !definition.Available || rewards.IsDefaultOrEmpty || rewards.Length != 20 ||
            rewards[^1] != new BoosterReward(5050548, 1, 3) || rewards.Take(19).Any(row => row.ItemId == 5050548))
            return definition;
        return definition with { PackageRewards = rewards.RemoveAt(19) };
    }

    internal static BoosterDefinition Parse(string text)
    {
        if (!BoosterFirstGrowTypes.TryParse(text, out var branches)) return new([], "礼包首转资格声明无效");
        string tags = Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
        var declarations = Regex.Matches(tags, @"\[\s*/?\s*random\s+option\s+unseal[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        bool unseal = false;
        if (declarations.Count != 0)
        {
            var declaration = declarations[0];
            int end = declaration.Index + declaration.Length;
            int next = tags.IndexOf('[', end);
            string type = PvfScript.Section(text, "stackable type").Trim();
            if (declarations.Count != 1 || text.Count(value => value == '`') % 2 != 0 ||
                declaration.Value != "[random option unseal]" || !IsRootDeclaration(tags, declaration.Index) ||
                !Regex.IsMatch(type, @"^`\[(?:booster|booster selection)\]`\s+\d+$") ||
                !int.TryParse(text[end..(next < 0 ? text.Length : next)].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int flag) || flag is not (0 or 1))
                return new([], "礼包魔法词条解封声明无效");
            unseal = flag == 1;
        }
        if (!TryParseCooldown(text, tags, out int coolTime, out int? coolGroup, out byte? maintenance))
            return new([], "礼包冷却声明无效");
        return ParseContents(text) with
        {
            FirstGrowTypes = branches, UnsealsRandomOptions = unseal,
            CustomAbilityGrade = int.TryParse(PvfScript.Section(tags, "custom ability grade").Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int abilityGrade) ? abilityGrade : null,
            CoolTime = coolTime, CoolGroup = coolGroup, CooltimeMaintenance = maintenance
        };
    }

    static bool TryParseCooldown(string text, string tags, out int coolTime, out int? coolGroup, out byte? maintenance)
    {
        coolTime = 0;
        coolGroup = null;
        maintenance = null;
        var declarations = Regex.Matches(tags, @"\[\s*/?\s*cooltime\s+maintenance[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        int[] bits = [];
        if (text.Count(value => value == '`') % 2 != 0 ||
            declarations.Count != 0 && (!Read("cooltime maintenance", 3, required: true, out bits) || bits.Any(value => value > 1)) ||
            !Read("cool time", 1, required: false, out var duration, repeated: true) ||
            !Read("cooltime group", 1, required: false, out var group, repeated: true))
            return false;
        coolTime = duration.Length == 0 ? 0 : duration[0];
        coolGroup = group.Length == 0 ? null : group[0];
        maintenance = bits.Length == 0 ? null : checked((byte)(bits[0] | bits[1] << 1 | bits[2] << 2));
        return true;

        bool Read(string name, int width, bool required, out int[] values, bool repeated = false)
        {
            values = [];
            var fields = Regex.Matches(tags, @"\[\s*/?\s*" + name.Replace(" ", @"\s+") + @"[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
            if (fields.Count == 0) return !required;
            if (!repeated && fields.Count != 1) return false;
            foreach (Match field in fields)
            {
                if (!field.Value.Equals("[" + name + "]", StringComparison.OrdinalIgnoreCase) || !IsRootDeclaration(tags, field.Index))
                    return false;
                int start = field.Index + field.Length;
                int end = tags.IndexOf('[', start);
                string[] tokens = text[start..(end < 0 ? text.Length : end)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != width) return false;
                values = new int[width];
                for (int i = 0; i < width; i++)
                    if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                        return false;
            }
            return true;
        }
    }

    static BoosterDefinition ParseContents(string text)
    {
        if (ParseUsePlaces(text).PendingRule is { } placeError) return new([], placeError);
        string type = Regex.Match(PvfScript.Section(text, "stackable type"), @"`\[([^\]]+)\]`").Groups[1].Value;
        if (type is not ("usable cera package" or "booster" or "cera booster" or "booster random" or "booster selection" or
            "upgradable legacy" or "legacy"))
            return new([], "其他开箱入口");
        // 固定商城礼包同样不能跳过资格、账本或实例属性字段。
        // 保留原文偏移，空声明仍须检查原文中的字符串值，不能被字符串掩码吞掉。
        string tags = Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
        bool finiteLegacy = Regex.IsMatch(PvfScript.Section(text, "stackable type").Trim(), @"^`\[legacy\]`\s+1$");
        // 普通实物罐只复用有限奖池；动作型旧物品不能借此跳过独立玩法。
        if (finiteLegacy && Regex.IsMatch(tags, @"\[\s*/?\s*action\s+type[^\]\r\n]*\]?", RegexOptions.IgnoreCase))
            return new([], "普通袖珍罐动作声明未支持");
        ItemMaterialCost? lotteryUseNeedItem = null;
        uint lotteryGoldCost = 0;
        var goldCosts = Regex.Matches(tags,
            @"\[\s*/?\s*lottery\s+use\s+cost[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (goldCosts.Count != 0)
        {
            var declaration = goldCosts[0];
            int end = declaration.Index + declaration.Length;
            int next = tags.IndexOf('[', end);
            // 付费金币奖励的净增量回包尚未闭合；费用只接入类型1的有限实物池。
            if ((!finiteLegacy && !Regex.IsMatch(PvfScript.Section(text, "stackable type").Trim(), @"^`\[upgradable legacy\]`\s+1$")) ||
                goldCosts.Count != 1 || text.Count(value => value == '`') % 2 != 0 ||
                !declaration.Value.Equals("[lottery use cost]", StringComparison.OrdinalIgnoreCase) ||
                !IsRootDeclaration(tags, declaration.Index) ||
                !uint.TryParse(text[end..(next < 0 ? text.Length : next)].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out lotteryGoldCost))
                return new([], "袖珍罐金币费用声明无效");
        }
        var lotteryCosts = Regex.Matches(tags,
            @"\[\s*/?\s*lottery\s+use\s+need\s+item[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (lotteryCosts.Count != 0)
        {
            var declaration = lotteryCosts[0];
            int end = declaration.Index + declaration.Length;
            int next = tags.IndexOf('[', end);
            string[] values = text[end..(next < 0 ? text.Length : next)]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (type != "upgradable legacy" || lotteryCosts.Count != 1 || text.Count(value => value == '`') % 2 != 0 ||
                !declaration.Value.Equals("[lottery use need item]", StringComparison.OrdinalIgnoreCase) ||
                !IsRootDeclaration(tags, declaration.Index) || values.Length != 2 ||
                !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int materialId) || materialId <= 0 ||
                !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int materialCount) || materialCount <= 0)
                return new([], "袖珍罐材料声明无效");
            lotteryUseNeedItem = new(materialId, materialCount);
        }
        byte? dailyLimit = null;
        bool retainsDailySource = false;
        if (tags.Contains("[total usable count]", StringComparison.OrdinalIgnoreCase))
        {
            if (type != "booster" || !DailyRetainedBoosterDefinition.TryParse(text, out byte limit, out retainsDailySource))
                return new([], "每日次数资格字段不完整或含未实现规则");
            dailyLimit = limit;
        }
        var disjointDeclarations = Regex.Matches(tags, @"\[\s*/?\s*set\s+disjoint\s+instance\s+type[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        bool setsDisjointInstanceType = disjointDeclarations.Count != 0;
        if (setsDisjointInstanceType)
        {
            if (type is not ("booster" or "booster selection" or "upgradable legacy"))
                return new([], "set disjoint instance type");
            foreach (Match declaration in disjointDeclarations)
            {
                int end = declaration.Index + declaration.Length;
                int next = tags.IndexOf('[', end);
                if (!declaration.Value.Equals("[set disjoint instance type]", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(text[end..(next < 0 ? text.Length : next)]) ||
                    !IsRootDeclaration(tags, declaration.Index))
                    return new([], "set disjoint instance type");
            }
        }
        var epicDeclarations = Regex.Matches(tags,
            @"\[\s*/?\s*set\s+epic\s+exchange\s+instance\s+type[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        bool setsEpicExchangeInstanceType = epicDeclarations.Count != 0;
        if (setsEpicExchangeInstanceType)
        {
            if (type is not ("booster" or "booster selection") || epicDeclarations.Count != 1 || text.Count(value => value == '`') % 2 != 0)
                return new([], "set epic exchange instance type");
            var declaration = epicDeclarations[0];
            int end = declaration.Index + declaration.Length;
            int next = tags.IndexOf('[', end);
            if (!declaration.Value.Equals("[set epic exchange instance type]", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(text[end..(next < 0 ? text.Length : next)]) ||
                !IsRootDeclaration(tags, declaration.Index))
                return new([], "set epic exchange instance type");
        }
        foreach (string tag in new[] { "effect maintenance", "total weekly usable count",
            "total accumulated usable count", "use count limit all server item", "usable datetime",
            "random box", "random box removal items", "convert boost item", "check usable itemlevel",
            "battle royal" })
            if (tags.Contains("[" + tag + "]", StringComparison.OrdinalIgnoreCase))
                return new([], tag);
        // 团本标记描述门票来源，购买材料与购买次数由商店负责；开启仍校验完整奖池及使用限制。
        var raidDeclarations = Regex.Matches(tags,
            @"\[\s*/?\s*raid\s+booster\s+item[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (raidDeclarations.Count != 0)
        {
            var declaration = raidDeclarations[0];
            int start = declaration.Index + declaration.Length;
            int end = tags.IndexOf('[', start);
            if (type is not ("booster" or "booster selection") || raidDeclarations.Count != 1 ||
                text.Count(value => value == '`') % 2 != 0 || declaration.Value != "[raid booster item]" ||
                !IsRootDeclaration(tags, declaration.Index) ||
                !int.TryParse(text[start..(end < 0 ? text.Length : end)].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int raidType) || raidType is < 1 or > 3)
                return new([], "raid booster item");
        }
        // 黎明材料和净化结晶共用的来源标记，不替代 booster info 的奖励声明。
        // 只接入匹配资源已确认的根级值1；未知形态继续拒绝。
        var daybreakDeclarations = Regex.Matches(tags,
            @"\[\s*/?\s*daybreak\s+drop\s+item[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (daybreakDeclarations.Count != 0)
        {
            var declaration = daybreakDeclarations[0];
            int start = declaration.Index + declaration.Length;
            int end = tags.IndexOf('[', start);
            if (type != "booster" || daybreakDeclarations.Count != 1 || text.Count(value => value == '`') % 2 != 0 ||
                declaration.Value != "[daybreak drop item]" || !IsRootDeclaration(tags, declaration.Index) ||
                text[start..(end < 0 ? text.Length : end)].Trim() != "1")
                return new([], "daybreak drop item");
        }
        // 品级声明由艾肯产物创建消费；其他装备不借这个字段启用斗气能力。
        var gradeDeclarations = Regex.Matches(tags,
            @"\[\s*/?\s*custom\s+ability\s+grade[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (gradeDeclarations.Count != 0)
        {
            if (gradeDeclarations.Count != 1)
                return new([], "custom ability grade");
            var declaration = gradeDeclarations[0];
            int end = declaration.Index + declaration.Length;
            int next = tags.IndexOf('[', end);
            if (!declaration.Value.Equals("[custom ability grade]", StringComparison.OrdinalIgnoreCase) ||
                !IsRootDeclaration(tags, declaration.Index) ||
                !int.TryParse(text[end..(next < 0 ? text.Length : next)].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int abilityGrade) || abilityGrade < 0)
                return new([], "custom ability grade");
        }
        if (type == "legacy" && !finiteLegacy)
        {
            // [legacy] 的开箱语义只认 [action type] `[rune random box]` 族 档；其他动作与参数保持未实现。
            if (!Regex.IsMatch(PvfScript.Section(text, "stackable type").Trim(), @"^`\[legacy\]`\s+0$"))
                return new([], "其他开箱入口");
            if (!TryParseRuneRandomBox(text, tags, out int family, out int tier, out string? pending))
                return new([], pending!);
            return new([], null) { RuneBox = (family, tier), Lottery = true };
        }
        if (type == "upgradable legacy" || finiteLegacy)
        {
            // 此类型经独立的CMD27入口一次抽取一行；不能交给CMD160忽略其回包合同。
            string lotteryType = PvfScript.Section(text, "stackable type").Trim();
            if (!finiteLegacy && !Regex.IsMatch(lotteryType, @"^`\[upgradable legacy\]`\s+[01]$"))
                return new([], "袖珍罐子类型待确认");
            bool allowsGold = lotteryType.EndsWith('0');
            var data = Regex.Matches(text, @"\[int data\](.*?)\[/int data\]", RegexOptions.Singleline);
            if (data.Count != 1 || Regex.Matches(tags, @"\[int data\]").Count != 1 ||
                Regex.Matches(tags, @"\[/int data\]").Count != 1)
                return new([], "袖珍罐奖池缺失或重复");
            string[] tokens = data[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length % 3 != 0)
                return new([], "袖珍罐奖池行宽无效");
            var rewards = ImmutableArray.CreateBuilder<BoosterReward>();
            for (int i = 0; i < tokens.Length; i += 3)
                if (!int.TryParse(tokens[i], out int id) || id < 0 || (id == 0 && !allowsGold) ||
                    !int.TryParse(tokens[i + 1], out int weight) || weight < 0 ||
                    !int.TryParse(tokens[i + 2], out int count) || count <= 0)
                    return new([], "袖珍罐物品、权重或数量无效");
                else
                    rewards.Add(new(id, weight, count));
            return rewards.Sum(row => row.Weight) > 0
                ? new([new(1, rewards.ToImmutable())], Lottery: true,
                    // 当前PVF没有独立契约资格字段；只认五种NPC系列的完整命名，不把任意罐/礼盒按类型放行。
                    NpcPocketPot: Regex.IsMatch(PvfScript.Section(text, "name"), @"^\s*`[土火木水金]罐的(?:高级)?袖珍罐(?:\s|\()"))
                    { LotteryUseNeedItem = lotteryUseNeedItem, LotteryGoldCost = lotteryGoldCost,
                        AllowsLotteryGold = allowsGold, SetsDisjointInstanceType = setsDisjointInstanceType,
                        ForbidsDestructionSouls = setsDisjointInstanceType }
                : new([], "袖珍罐总权重为零");
        }
        if (type == "usable cera package")
        {
            var packageBlocks = Regex.Matches(text, @"\[package data\](.*?)\[/package data\]", RegexOptions.Singleline);
            if (packageBlocks.Count != 1)
                return new([], "礼包内容缺失或重复");
            string package = packageBlocks[0].Groups[1].Value;
            string[] tokens = package.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                return new([], "礼包内容缺失或行宽无效");
            var rewards = ImmutableArray.CreateBuilder<BoosterReward>(tokens.Length / 2);
            for (int i = 0; i < tokens.Length; i += 2)
                if (!int.TryParse(tokens[i], out int id) || id <= 0 || !int.TryParse(tokens[i + 1], out int count) || count <= 0)
                    return new([], "礼包内容物品或数量无效");
                else
                    rewards.Add(new(id, 1, count));
            return new([], null, null, rewards.ToImmutable());
        }
        if (type == "booster selection")
            return BoosterSelectionDefinition.Parse(text) with
            {
                SetsDisjointInstanceType = setsDisjointInstanceType,
                SetsEpicExchangeInstanceType = setsEpicExchangeInstanceType
            };
        var blocks = Regex.Matches(text, @"\[booster info\](.*?)\[/booster info\]", RegexOptions.Singleline);
        if (blocks.Count != 1)
            return new([], "礼包奖池缺失或重复");
        string body = blocks[0].Groups[1].Value;
        var pools = ImmutableArray.CreateBuilder<BoosterPool>();
        bool allowsEmptyBonus = Regex.IsMatch(PvfScript.Section(text, "stackable type").Trim(), @"^`\[booster\]`\s+0$");
        int at = 0;
        foreach (Match match in Regex.Matches(body, @"\[(?<kind>[^/\]\r\n]+)\](?<data>.*?)\[/\k<kind>\]", RegexOptions.Singleline))
        {
            if (!string.IsNullOrWhiteSpace(body[at..match.Index]))
                return new([], "未识别的奖池字段");
            at = match.Index + match.Length;
            string kind = match.Groups["kind"].Value;
            if (kind == "hide booster info")
                continue;
            if (kind == "grow type")
            {
                if (!BoosterGrowTypePools.TryParse(match.Groups["data"].Value, out var growPool, out string? pending))
                    return new([], pending ?? "转职奖池无效");
                if (growPool is not null)
                    pools.Add(growPool);
                continue;
            }
            if (kind == "charactor")
            {
                if (!BoosterGrowTypePools.TryParseCharacter(match.Groups["data"].Value, out var characterPool, out string? pending))
                    return new([], pending ?? "职业奖池无效");
                if (characterPool is not null)
                    pools.Add(characterPool);
                continue;
            }
            if (kind is "avatar" or "special avatar")
            {
                if (!TryParseAvatarPool(match.Groups["data"].Value, kind == "special avatar", out var avatarPool, out string? pending))
                    return new([], pending ?? "装扮奖池无效");
                pools.Add(avatarPool);
                continue;
            }
            if (kind is not ("etc" or "cera" or "equipment" or "stackable" or "booster" or "emblem" or "creature"))
                return new([], kind);
            string[] tokens = match.Groups["data"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || (tokens.Length - 1) % 3 != 0 || !int.TryParse(tokens[0], out int draws) || draws <= 0)
                return new([], "奖池抽取次数或行宽无效");
            if (tokens.Length == 1)
                continue; // 匹配资源中的空占位池不产生奖励，不能制造一件物品。
            var rows = new List<(int Id, decimal Weight, int Count)>();
            int scale = 0;
            for (int i = 1; i < tokens.Length; i += 3)
            {
                if (!int.TryParse(tokens[i], out int id) || id < 0 || !int.TryParse(tokens[i + 2], out int count) || count <= 0 ||
                    id == 0 && (!allowsEmptyBonus || kind != "etc" || count != 1) ||
                    !decimal.TryParse(tokens[i + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal weight) || weight < 0)
                    return new([], "奖池物品、数量或权重无效");
                scale = Math.Max(scale, (decimal.GetBits(weight)[3] >> 16) & 0xff);
                rows.Add((id, weight, count));
            }
            try
            {
                decimal factor = 1;
                for (int digit = 0; digit < scale; digit++)
                    factor = checked(factor * 10);
                // 普通额外池的0号是空奖；袖珍罐的0号金币在独立入口解析，不沿用此标记。
                var rewards = rows.Select(row => new BoosterReward(row.Id, checked((long)(row.Weight * factor)), row.Count)
                    { EmptyOutcome = row.Id == 0 }).ToImmutableArray();
                if (rewards.Sum(row => row.Weight) > 0)
                    pools.Add(new(draws, rewards) { RequiredKind = kind == "creature" ? ItemKind.Creature : null });
            }
            catch (OverflowException)
            {
                return new([], "奖池权重溢出");
            }
        }
        string tail = body[at..];
        if (pools.Any(pool => pool.Rewards.Any(row => row.EmptyOutcome)) &&
            !pools.Any(pool => pool.JobTag is null && pool.Rewards.Where(row => row.Weight > 0).All(row => !row.EmptyOutcome)))
            return new([], "空额外奖缺少独立基础奖池");
        return !string.IsNullOrWhiteSpace(tail) &&
            !Regex.IsMatch(tail, @"^(?:\s*\[hide booster info\](?:\s*\[/hide booster info\])?)+\s*$")
            ? new([], "未识别的奖池尾段") : new(pools.ToImmutable())
                { DailyRetainedLimit = retainsDailySource ? dailyLimit : null,
                    DailyConsumedLimit = retainsDailySource ? null : dailyLimit, SetsDisjointInstanceType = setsDisjointInstanceType,
                    SetsEpicExchangeInstanceType = setsEpicExchangeInstanceType };
    }

    // [avatar] / [special avatar] 行宽为五列：物品、权重、数量、期限天数、属性选项。
    // 单行 [avatar] 仍只发前三列，尾部整数只校验；多行以及 [special avatar] 把后两列带进发放。
    // 同时能被 4 与 5 整除时按五列读。在售海之勇者、野生力量的八行池就是这种长度，四列会拆错属性。
    static bool TryParseAvatarPool(string data, bool special, out BoosterPool pool, out string? pending)
    {
        pool = null!;
        pending = null;
        string[] tokens = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out int draws) || draws <= 0)
        {
            pending = "装扮奖池抽取次数或行宽无效";
            return false;
        }
        int rest = tokens.Length - 1;
        bool widthFive = rest % 5 == 0 && rest >= 5;
        if (special)
        {
            if (!widthFive)
            {
                pending = "装扮奖池行宽有歧义";
                return false;
            }
            return TryParseAvatarRows(tokens, draws, out pool, out pending);
        }
        if (rest is 4 || widthFive && rest == 5)
            return TryParseLegacyAvatarRow(tokens, draws, out pool, out pending);
        if (widthFive)
            return TryParseAvatarRows(tokens, draws, out pool, out pending);
        pending = rest % 4 == 0 ? "装扮奖池多行选择待确认" : "装扮奖池行宽有歧义";
        return false;
    }

    static bool TryParseLegacyAvatarRow(string[] tokens, int draws, out BoosterPool pool, out string? pending)
    {
        pool = null!;
        pending = null;
        if (!int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0 ||
            !int.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0 ||
            !decimal.TryParse(tokens[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal weight) ||
            weight <= 0 || tokens[4..].Any(value => !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            pending = "装扮奖池物品、数量或权重无效";
            return false;
        }
        if (!TryScaleWeight(weight, out long scaled, out pending))
            return false;
        pool = new(draws, [new(id, scaled, count)]);
        return true;
    }

    static bool TryParseAvatarRows(string[] tokens, int draws, out BoosterPool pool, out string? pending)
    {
        pool = null!;
        pending = null;
        var rows = new List<(int Id, decimal Weight, int Count, int Period, byte Ability)>();
        int scale = 0;
        for (int i = 1; i < tokens.Length; i += 5)
        {
            if (!int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0 ||
                !decimal.TryParse(tokens[i + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal weight) ||
                weight <= 0 ||
                !int.TryParse(tokens[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0 ||
                !int.TryParse(tokens[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out int period) || period < 0 ||
                !int.TryParse(tokens[i + 4], NumberStyles.None, CultureInfo.InvariantCulture, out int ability) || ability is < 0 or > byte.MaxValue)
            {
                pending = "装扮奖池物品、数量或权重无效";
                return false;
            }
            scale = Math.Max(scale, (decimal.GetBits(weight)[3] >> 16) & 0xff);
            rows.Add((id, weight, count, period, (byte)ability));
        }
        try
        {
            decimal factor = 1;
            for (int digit = 0; digit < scale; digit++)
                factor = checked(factor * 10);
            var rewards = new BoosterReward[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                rewards[i] = new(row.Id, checked((long)(row.Weight * factor)), row.Count)
                {
                    AvatarPeriodDays = row.Period,
                    AvatarAbility = row.Ability
                };
            }
            if (rewards.Sum(row => row.Weight) <= 0)
            {
                pending = "装扮奖池总权重为零";
                return false;
            }
            pool = new(draws, rewards.ToImmutableArray());
            return true;
        }
        catch (OverflowException)
        {
            pending = "装扮奖池权重溢出";
            return false;
        }
    }

    static bool TryScaleWeight(decimal weight, out long scaled, out string? pending)
    {
        scaled = 0;
        pending = null;
        int scale = Math.Max(0, (decimal.GetBits(weight)[3] >> 16) & 0xff);
        decimal factor = 1;
        try
        {
            for (int digit = 0; digit < scale; digit++)
                factor = checked(factor * 10);
            scaled = checked((long)(weight * factor));
            return true;
        }
        catch (OverflowException)
        {
            pending = "装扮奖池权重溢出";
            return false;
        }
    }

    static bool TryParseRuneRandomBox(string text, string tags, out int family, out int tier, out string? pending)
    {
        family = 0;
        tier = 0;
        pending = null;
        var actions = Regex.Matches(tags, @"\[\s*/?\s*action\s+type[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (actions.Count == 0)
        {
            pending = "其他开箱入口";
            return false;
        }
        if (actions.Count != 2 || actions[0].Value != "[action type]" || actions[1].Value != "[/action type]" ||
            text.Count(value => value == '`') % 2 != 0 || !IsRootDeclaration(tags, actions[0].Index))
        {
            pending = "开箱动作声明未闭合或不唯一";
            return false;
        }
        string body = text[(actions[0].Index + actions[0].Length)..actions[1].Index];
        var match = Regex.Match(body, @"^\s*`\[([^\]]+)\]`\s+(\d+)\s+(\d+)\s*$");
        if (!match.Success || match.Groups[1].Value != "rune random box" ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out family) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out tier))
        {
            pending = "未实现的开箱动作声明";
            return false;
        }
        if (family is not (0 or 1) || tier is < 0 or > byte.MaxValue)
        {
            pending = "开箱动作参数越界";
            return false;
        }
        return true;
    }

    internal static BoosterUsePlacesDefinition ParseUsePlaces(string text)
    {
        var values = new List<string>();
        foreach (Match section in Regex.Matches(text, @"(?im)^\[action usable place\][^\S\r\n]*(?:\r?\n|$)"))
        {
            int end = text.IndexOf("\n[", section.Index + section.Length, StringComparison.Ordinal);
            string body = end < 0 ? text[(section.Index + section.Length)..] : text[(section.Index + section.Length)..end];
            foreach (Match value in Regex.Matches(body, @"\G\s*`([^`]*)`")) values.Add(value.Groups[1].Value);
        }
        return ParseUsePlaces(values);
    }

    internal static BoosterUsePlacesDefinition ParseUsePlaces(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return new();
        var flags = StackableUsePlaces.Parse(values);
        bool all = (flags & StackableUsePlaceFlags.All) != 0;
        bool Has(StackableUsePlaceFlags flag) => all || (flags & flag) != 0;
        bool village = Has(StackableUsePlaceFlags.Village), room = Has(StackableUsePlaceFlags.SeriaRoom),
            dungeon = Has(StackableUsePlaceFlags.Dungeon), pvp = Has(StackableUsePlaceFlags.Pvp), tournament = Has(StackableUsePlaceFlags.Tournament);
        return new(village, room) { Dungeon = dungeon, Pvp = pvp, Tournament = tournament,
            ExcludeAssault = (flags & StackableUsePlaceFlags.ExcludeAssault) != 0,
            ExcludePractice = (flags & StackableUsePlaceFlags.ExcludePractice) != 0,
            DontOpen = (flags & StackableUsePlaceFlags.DontOpen) != 0,
            ExclusionOnly = !village && !room && !dungeon && !pvp && !tournament };
    }

    internal static bool IsRootDeclaration(string tags, int declarationIndex)
    {
        var boundaries = Regex.Matches(tags, @"\[(?<close>/?)(?<name>[^/\]\r\n]+)\]");
        // PVF标量字段通常没有闭合标签；仅跟踪实际出现闭合的名字，不把标量误当外层。
        var pairedNames = boundaries.Where(tag => tag.Groups["close"].Value == "/")
            .Select(tag => tag.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var opened = new Stack<string>();
        foreach (Match boundary in boundaries)
        {
            if (boundary.Index >= declarationIndex)
                return opened.Count == 0;
            string name = boundary.Groups["name"].Value;
            if (!pairedNames.Contains(name))
                continue;
            if (boundary.Groups["close"].Value != "/")
                opened.Push(name);
            else if (!opened.TryPop(out string? current) || !string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return false;
    }

    static FrozenSet<int> ActivationItems(PvfArchive archive)
    {
        var ids = Regex.Matches(archive.ReadText("etc/premiumlist_new.etc"), @"\[(?:target item|item)\]\s*(\d+)")
            .Select(match => int.Parse(match.Groups[1].Value)).Where(id => id > 0).ToHashSet();
        string shop = archive.ReadText("etc/(r)cerashop.etc");
        foreach (string tag in new[] { "selectable character premium", "charac premium package" })
        {
            string[] values = Regex.Matches(PvfScript.Block(shop, tag), @"`[^`]*`|\S+").Select(match => match.Value).ToArray();
            if (values.Length % 9 != 0)
                throw new InvalidDataException("PVF契约商品段宽度无效");
            for (int i = 0; i < values.Length; i += 9)
                ids.Add(int.Parse(values[i + 1]));
        }
        return ids.ToFrozenSet();
    }
}
