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
    public ItemKind? RequiredKind { get; init; }

    public bool Matches(byte job, byte growType, byte level) => JobTag is null ||
        (string.Equals(JobTag, ItemCatalog.JobTag(job), StringComparison.OrdinalIgnoreCase) &&
            (growType & 15) == GrowType && level >= MinimumLevel && level <= MaximumLevel);
}
internal sealed record BoosterDefinition(ImmutableArray<BoosterPool> Pools, string? PendingRule = null,
    BoosterSelectionDefinition? Selection = null, ImmutableArray<BoosterReward> PackageRewards = default, bool Lottery = false,
    bool NpcPocketPot = false)
{
    public ImmutableArray<byte> FirstGrowTypes { get; init; } = [];
    public bool AllowsFirstGrowType(byte growType) => FirstGrowTypes.IsEmpty || FirstGrowTypes.Contains((byte)(growType & 15));
    public bool UnsealsRandomOptions { get; init; }
    public bool SetsDisjointInstanceType { get; init; }
    public bool SetsEpicExchangeInstanceType { get; init; }
    // 休亚商店在售的选组礼盒：PVF 没写交换声明，但产物同样是在休亚处兑换来的。
    public bool FromExchangeShop { get; init; }
    // 产物装备一律带交换资格位；随机奖池与固定礼包不在支持范围内。
    public bool GrantsEpicExchangeOrigin => SetsEpicExchangeInstanceType || (FromExchangeShop && Selection is not null);
    public byte? DailyRetainedLimit { get; init; }
    public bool Available => PendingRule is null && (Pools.Length != 0 || Selection is not null || !PackageRewards.IsDefaultOrEmpty);
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
        ? Parse(archive.ReadText("stackable/" + path)) with { FromExchangeShop = exchangeShopStock?.Contains(id) == true }
        : new([], "非礼包物品"));

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
        return ParseContents(text) with { FirstGrowTypes = branches, UnsealsRandomOptions = unseal };
    }

    static BoosterDefinition ParseContents(string text)
    {
        if (ParseUsePlaces(text).PendingRule is { } placeError) return new([], placeError);
        string type = Regex.Match(PvfScript.Section(text, "stackable type"), @"`\[([^\]]+)\]`").Groups[1].Value;
        if (type is not ("usable cera package" or "booster" or "cera booster" or "booster random" or "booster selection" or "upgradable legacy"))
            return new([], "其他开箱入口");
        // 固定商城礼包同样不能跳过资格、账本或实例属性字段。
        // 保留原文偏移，空声明仍须检查原文中的字符串值，不能被字符串掩码吞掉。
        string tags = Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
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
            if (type != "booster selection" || disjointDeclarations.Count != 1)
                return new([], "set disjoint instance type");
            var declaration = disjointDeclarations[0];
            int end = declaration.Index + declaration.Length;
            int next = tags.IndexOf('[', end);
            if (!declaration.Value.Equals("[set disjoint instance type]", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(text[end..(next < 0 ? text.Length : next)]) ||
                !IsRootDeclaration(tags, declaration.Index))
                return new([], "set disjoint instance type");
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
        foreach (string tag in new[] { "cooltime maintenance", "effect maintenance", "total weekly usable count",
            "total accumulated usable count", "use count limit all server item", "usable datetime",
            "random box", "random box removal items", "raid booster item", "convert boost item", "check usable itemlevel",
            "daybreak drop item", "battle royal", "action usable server type", "usable game server type", "usable server type" })
            if (tags.Contains("[" + tag + "]", StringComparison.OrdinalIgnoreCase))
                return new([], tag);
        // [custom ability grade] 规定发放装备的品级。本服没有实例级品级字段，只校验声明形态，
        // 数值不写入实例，也不据此收窄可领取范围；形态不合法仍整包拒绝。
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
        if (!Regex.IsMatch(body, @"^\s*(?:`\[(?:village|seria room)\]`\s*)+$"))
            return new(PendingRule: "礼包使用地点包含未支持声明");
        var values = Regex.Matches(body, "`([^`]*)`").Select(match => match.Groups[1].Value).ToArray();
        if (values.Length != values.Distinct().Count()) return new(PendingRule: "礼包使用地点重复");
        return new(values.Contains("[village]"), values.Contains("[seria room]"));
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
