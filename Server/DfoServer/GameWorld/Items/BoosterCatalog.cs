using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record BoosterReward(int ItemId, long Weight, int Count);
internal sealed record BoosterUsePlacesDefinition(bool Village = false, bool SeriaRoom = false, string? PendingRule = null);
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
    public bool ForbidsDestructionSouls { get; init; }
    public bool SetsEpicExchangeInstanceType { get; init; }
    // 休亚商店在售的选组礼盒：PVF 没写交换声明，但产物同样是在休亚处兑换来的。
    public bool FromExchangeShop { get; init; }
    // 产物装备一律带交换资格位；随机奖池与固定礼包不在支持范围内。
    public bool GrantsEpicExchangeOrigin => SetsEpicExchangeInstanceType || (FromExchangeShop && Selection is not null);
    public byte? DailyRetainedLimit { get; init; }
    public ItemMaterialCost? LotteryUseNeedItem { get; init; }
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
        stackable.TryGetValue(id, out string? path) ? ParseUsePlaces(archive.ReadText("stackable/" + path)) :
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
            return new([], "cooltime maintenance");
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
        // 未声明维护位的礼盒保持原有路径；本次不把普通冷却字段扩大为新的使用限制。
        if (declarations.Count == 0) return true;
        if (text.Count(value => value == '`') % 2 != 0 ||
            !Read("cooltime maintenance", 3, required: true, out var bits) || bits.Any(value => value > 1) ||
            !Read("cool time", 1, required: false, out var duration) ||
            !Read("cooltime group", 1, required: false, out var group))
            return false;
        coolTime = duration.Length == 0 ? 0 : duration[0];
        coolGroup = group.Length == 0 ? null : group[0];
        maintenance = checked((byte)(bits[0] | bits[1] << 1 | bits[2] << 2));
        return true;

        bool Read(string name, int width, bool required, out int[] values)
        {
            values = [];
            var fields = Regex.Matches(tags, @"\[\s*/?\s*" + name.Replace(" ", @"\s+") + @"[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
            if (fields.Count == 0) return !required;
            if (fields.Count != 1 || !fields[0].Value.Equals("[" + name + "]", StringComparison.OrdinalIgnoreCase) ||
                !IsRootDeclaration(tags, fields[0].Index))
                return false;
            int start = fields[0].Index + fields[0].Length;
            int end = tags.IndexOf('[', start);
            string[] tokens = text[start..(end < 0 ? text.Length : end)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != width) return false;
            values = new int[width];
            for (int i = 0; i < width; i++)
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                    return false;
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
        ItemMaterialCost? lotteryUseNeedItem = null;
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
        if (tags.Contains("[total usable count]", StringComparison.OrdinalIgnoreCase))
        {
            if (type != "booster" || !DailyRetainedBoosterDefinition.TryParse(text, out byte limit))
                return new([], "每日保留资格字段不完整或含未实现规则");
            dailyLimit = limit;
        }
        var disjointDeclarations = Regex.Matches(tags, @"\[\s*/?\s*set\s+disjoint\s+instance\s+type[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        bool setsDisjointInstanceType = disjointDeclarations.Count != 0;
        if (setsDisjointInstanceType)
        {
            if (type is not ("booster selection" or "upgradable legacy"))
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
            if (type != "booster selection" || epicDeclarations.Count != 1 || text.Count(value => value == '`') % 2 != 0)
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
            "random box", "random box removal items", "raid booster item", "convert boost item", "check usable itemlevel",
            "battle royal", "usable game server type", "usable server type" })
            if (tags.Contains("[" + tag + "]", StringComparison.OrdinalIgnoreCase))
                return new([], tag);
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
        var serverDeclarations = Regex.Matches(tags,
            @"\[\s*/?\s*action\s+usable\s+server\s+type[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (serverDeclarations.Count != 0)
        {
            if (text.Count(value => value == '`') % 2 != 0 || serverDeclarations.Count != 2 ||
                serverDeclarations[0].Value != "[action usable server type]" ||
                serverDeclarations[1].Value != "[/action usable server type]" ||
                !IsRootDeclaration(tags, serverDeclarations[0].Index))
                return new([], "action usable server type");
            int start = serverDeclarations[0].Index + serverDeclarations[0].Length;
            string serverBody = text[start..serverDeclarations[1].Index];
            if (!Regex.IsMatch(serverBody, @"^\s*(?:`\[(?:general|event) server\]`\s*)+$"))
                return new([], "action usable server type");
            var servers = Regex.Matches(serverBody, "`([^`]*)`").Select(match => match.Groups[1].Value).ToArray();
            if (!servers.Contains("[general server]") || servers.Length != servers.Distinct().Count())
                return new([], "action usable server type");
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
        if (type == "legacy")
        {
            // [legacy] 的开箱语义只认 [action type] `[rune random box]` 族 档；其他动作与参数保持未实现。
            if (!Regex.IsMatch(PvfScript.Section(text, "stackable type").Trim(), @"^`\[legacy\]`\s+0$"))
                return new([], "其他开箱入口");
            if (!TryParseRuneRandomBox(text, tags, out int family, out int tier, out string? pending))
                return new([], pending!);
            return new([], null) { RuneBox = (family, tier), Lottery = true };
        }
        if (type == "upgradable legacy")
        {
            // 此类型经独立的CMD27入口一次抽取一行；不能交给CMD160忽略其回包合同。
            if (!Regex.IsMatch(PvfScript.Section(text, "stackable type").Trim(), @"^`\[upgradable legacy\]`\s+1$"))
                return new([], "袖珍罐子类型待确认");
            var data = Regex.Matches(text, @"\[int data\](.*?)\[/int data\]", RegexOptions.Singleline);
            if (data.Count != 1 || Regex.Matches(tags, @"\[int data\]").Count != 1 ||
                Regex.Matches(tags, @"\[/int data\]").Count != 1)
                return new([], "袖珍罐奖池缺失或重复");
            string[] tokens = data[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length % 3 != 0)
                return new([], "袖珍罐奖池行宽无效");
            var rewards = ImmutableArray.CreateBuilder<BoosterReward>();
            for (int i = 0; i < tokens.Length; i += 3)
                if (!int.TryParse(tokens[i], out int id) || id <= 0 ||
                    !int.TryParse(tokens[i + 1], out int weight) || weight < 0 ||
                    !int.TryParse(tokens[i + 2], out int count) || count <= 0)
                    return new([], "袖珍罐物品、权重或数量无效");
                else
                    rewards.Add(new(id, weight, count));
            return rewards.Sum(row => row.Weight) > 0
                ? new([new(1, rewards.ToImmutable())], Lottery: true,
                    // 当前PVF没有独立契约资格字段；只认五种NPC系列的完整命名，不把任意罐/礼盒按类型放行。
                    NpcPocketPot: Regex.IsMatch(PvfScript.Section(text, "name"), @"^\s*`[土火木水金]罐的(?:高级)?袖珍罐(?:\s|\()"))
                    { LotteryUseNeedItem = lotteryUseNeedItem, SetsDisjointInstanceType = setsDisjointInstanceType,
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
            if (kind == "avatar")
            {
                if (!TryParseAvatarPool(match.Groups["data"].Value, out var avatarPool, out string? pending))
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
                if (!int.TryParse(tokens[i], out int id) || id <= 0 || !int.TryParse(tokens[i + 2], out int count) || count <= 0 ||
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
                var rewards = rows.Select(row => new BoosterReward(row.Id, checked((long)(row.Weight * factor)), row.Count)).ToImmutableArray();
                if (rewards.Sum(row => row.Weight) > 0)
                    pools.Add(new(draws, rewards) { RequiredKind = kind == "creature" ? ItemKind.Creature : null });
            }
            catch (OverflowException)
            {
                return new([], "奖池权重溢出");
            }
        }
        string tail = body[at..];
        return !string.IsNullOrWhiteSpace(tail) &&
            !Regex.IsMatch(tail, @"^(?:\s*\[hide booster info\](?:\s*\[/hide booster info\])?)+\s*$")
            ? new([], "未识别的奖池尾段") : new(pools.ToImmutable()) { DailyRetainedLimit = dailyLimit };
    }

    // 普通装扮礼包的 [avatar] 段把外观写成 (物品, 权重, 数量) 加资源特定的尾部整数列，
    // 与选择型 [booster select category] 的四列行不同。单行奖池没有选择语义，尾部列只校验为
    // 整数、不参与发奖；多行或行宽有歧义时保持拒绝，避免把随机外观误发成固定一件。
    static bool TryParseAvatarPool(string data, out BoosterPool pool, out string? pending)
    {
        pool = null!;
        pending = null;
        string[] tokens = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4 || !int.TryParse(tokens[0], out int draws) || draws <= 0)
        {
            pending = "装扮奖池抽取次数或行宽无效";
            return false;
        }
        int rest = tokens.Length - 1;
        int width = rest % 5 == 0 && rest % 4 != 0 ? 5 : rest % 4 == 0 && rest % 5 != 0 ? 4 : 0;
        if (width == 0)
        {
            pending = "装扮奖池行宽有歧义";
            return false;
        }
        if (rest != width)
        {
            pending = "装扮奖池多行选择待确认";
            return false;
        }
        if (!int.TryParse(tokens[1], out int id) || id <= 0 || !int.TryParse(tokens[3], out int count) || count <= 0 ||
            !decimal.TryParse(tokens[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal weight) ||
            weight <= 0 || tokens[4..].Any(value => !int.TryParse(value, out _)))
        {
            pending = "装扮奖池物品、数量或权重无效";
            return false;
        }
        // 小数位权重与随机池保持同一缩放约定，单行权重恒为正。
        int scale = Math.Max(0, (decimal.GetBits(weight)[3] >> 16) & 0xff);
        decimal factor = 1;
        for (int digit = 0; digit < scale; digit++)
            factor = checked(factor * 10);
        try
        {
            pool = new(draws, [new(id, checked((long)(weight * factor)), count)]);
        }
        catch (OverflowException)
        {
            pending = "装扮奖池权重溢出";
            return false;
        }
        return true;
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
        string tags = Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
        var declarations = Regex.Matches(tags, @"\[\s*/?\s*action\s+usable\s+place[^\]\r\n]*\]?", RegexOptions.IgnoreCase);
        if (declarations.Count == 0) return new();
        if (text.Count(value => value == '`') % 2 != 0 || declarations.Count != 2 ||
            declarations[0].Value != "[action usable place]" || declarations[1].Value != "[/action usable place]" ||
            !IsRootDeclaration(tags, declarations[0].Index)) return new(PendingRule: "礼包使用地点声明未闭合或不唯一");
        int start = declarations[0].Index + declarations[0].Length;
        string body = text[start..declarations[1].Index];
        if (!Regex.IsMatch(body, @"^\s*(?:`\[(?:village|seria room|dont use assult)\]`\s*)+$"))
            return new(PendingRule: "礼包使用地点包含未支持声明");
        var values = Regex.Matches(body, "`([^`]*)`").Select(match => match.Groups[1].Value).ToArray();
        if (values.Length != values.Distinct().Count()) return new(PendingRule: "礼包使用地点重复");
        bool village = values.Contains("[village]"), seriaRoom = values.Contains("[seria room]");
        if (!village && !seriaRoom) return new(PendingRule: "礼包使用地点缺少允许地点");
        return new(village, seriaRoom);
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
