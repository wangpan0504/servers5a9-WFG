using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ParticularHellPartyTests
{
    public static void Run()
    {
        var valid = new Script();
        valid.Tag("[daily open chance]"); valid.Int(3);
        valid.Tag("[required item weight]"); valid.Int(1);
        valid.Tag("[dungeon]");
        valid.Tag("[index]"); valid.Int(7);
        valid.Tag("[item list]"); valid.Int(42);
        valid.Tag("[/item list]");
        valid.Tag("[/dungeon]");
        valid.Tag("[inferno weapon creation rate]"); valid.Float(0.5f);
        var loaded = ParticularHellPartyCatalog.Parse(valid.Tokens, valid.Resolve, id => id == 42);
        That(loaded.Items(7).SequenceEqual([42]) && loaded.Draw(7, 0) == 42 && loaded.RequiredItemWeight == 1,
            "合法专表按副本编号装入自己的物品");

        var missing = new Script();
        missing.Tag("[daily open chance]"); missing.Int(3);
        missing.Tag("[required item weight]"); missing.Int(1);
        missing.Tag("[dungeon]");
        missing.Tag("[index]"); missing.Int(7);
        missing.Tag("[/dungeon]");
        missing.Tag("[inferno weapon creation rate]"); missing.Float(0.5f);
        Reject<InvalidDataException>(() => ParticularHellPartyCatalog.Parse(missing.Tokens, missing.Resolve, _ => true),
            "缺物品表不能变成空表");
        Console.WriteLine("particular-hell load-failure missing block threw");

        var weight = new Script();
        weight.Tag("[daily open chance]"); weight.Int(3);
        weight.Tag("[required item weight]"); weight.Int(0);
        weight.Tag("[dungeon]");
        weight.Tag("[index]"); weight.Int(7);
        weight.Tag("[item list]"); weight.Int(42);
        weight.Tag("[/item list]");
        weight.Tag("[/dungeon]");
        weight.Tag("[inferno weapon creation rate]"); weight.Float(0.5f);
        Reject<InvalidDataException>(() => ParticularHellPartyCatalog.Parse(weight.Tokens, weight.Resolve, _ => true),
            "非法权重不能变成空表");
        Console.WriteLine("particular-hell load-failure illegal weight threw");

        var unknown = new Script();
        unknown.Tag("[daily open chance]"); unknown.Int(3);
        unknown.Tag("[required item weight]"); unknown.Int(1);
        unknown.Tag("[dungeon]");
        unknown.Tag("[index]"); unknown.Int(7);
        unknown.Tag("[item list]"); unknown.Int(42);
        unknown.Tag("[/item list]");
        unknown.Tag("[/dungeon]");
        unknown.Tag("[inferno weapon creation rate]"); unknown.Float(0.5f);
        Reject<InvalidDataException>(() => ParticularHellPartyCatalog.Parse(unknown.Tokens, unknown.Resolve, _ => false),
            "不是物品的 id 不能变成空表");
        Console.WriteLine("particular-hell load-failure non-item threw");

        var crack = CrackFixture();
        var parsed = CrackDimensionCatalog.Parse(crack.Tokens, crack.Resolve, id => id is 42 or 43);
        var first = parsed.Decide(55, 0, false);
        var daily = parsed.Decide(55, 1, false);
        That(first.ClearItems.SequenceEqual([new CrackItemReward(42, 1)]) && first.Daily is null && first.ClearsAfter == 1,
            "裂缝通关奖励按等级段给物品，未到次数不给每日奖励");
        That(daily.Daily == new CrackItemReward(43, 4) && daily.DailyGranted && daily.Granted.Length == 2,
            "达到每日通关次数后追加每日奖励");
        That(parsed.Decide(54, 1, false).ClearItems.IsEmpty && parsed.Decide(55, 1, true).Daily is null,
            "等级不够没有通关物品，当日奖励只发一次");
        Reject<InvalidDataException>(() => CrackDimensionCatalog.Parse(CrackFixture(oddReward: true).Tokens, crack.Resolve, id => id is 42 or 43),
            "通关奖励不成对不能当成物品");
        Console.WriteLine("crack-dimension load-failure odd reward threw");
        Reject<InvalidDataException>(() => CrackDimensionCatalog.Parse(crack.Tokens, crack.Resolve, _ => false),
            "裂缝奖励里不是物品的 id 不能发放");
        Console.WriteLine("crack-dimension load-failure non-item threw");
        Reject<InvalidDataException>(() => CrackDimensionCatalog.Parse(CrackFixture(dailyMismatch: true).Tokens, crack.Resolve, id => id is 42 or 43),
            "每日奖励次数和每日通关次数不一致不能发放");
        Console.WriteLine("crack-dimension load-failure daily count mismatch threw");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var items = new ItemCatalog(pvf, PvfScript.List(pvf.ReadText("equipment/equipment.lst")),
            PvfScript.List(pvf.ReadText("stackable/stackable.lst")), 0);
        var catalog = ParticularHellPartyCatalog.Load(pvf, items.Contains);
        string text = pvf.ReadText(ParticularHellPartyCatalog.Path);
        int[] indexes = FileIndexes(text);
        That(catalog.ItemsByDungeon.Keys.Order().SequenceEqual(indexes.Order()),
            "每个副本块都按自己的编号装入");
        var hell = catalog.Items(316);
        foreach (int index in indexes)
        {
            int[] raw = RawItemList(text, index);
            var own = catalog.Items(index);
            That(own.SequenceEqual(raw), "装入列表等于文件里的物品表 " + index);
            for (int roll = 0; roll < raw.Length; roll++)
                That(catalog.Draw(index, roll) == raw[roll], "抽取留在该副本自己的表里 " + index);
            if (index != 316)
                That(!SharesStorage(hell, own), "副本 " + index + " 没有套用 316 的列表对象");
        }
        var dawn = catalog.Items(530);
        That(!dawn.Contains(hell[0]) && !hell.Contains(dawn[0]) && hell[0] != dawn[0],
            "316 与 530 的表互不包含对方的首个物品");
        Console.WriteLine($"particular-hell isolation checks passed dungeon 316 head {hell[0]} {hell[1]} count {hell.Length}");
        Console.WriteLine($"particular-hell isolation checks passed dungeon 530 head {dawn[0]} {dawn[1]} count {dawn.Length}");
        foreach (int sibling in new[] { 6063, 6064, 6065 })
        {
            var own = catalog.Items(sibling);
            That(own.SequenceEqual(RawItemList(text, sibling)) && !SharesStorage(hell, own),
                "6063-6065 使用文件里自己的块，不和 316 共用列表对象 " + sibling);
            Console.WriteLine($"particular-hell own-list dungeon {sibling} head {own[0]} {own[1]} count {own.Length} not-aliased-to-316");
        }
        RejectLiterals(hell[0], hell[1], dawn[0], dawn[1]);

        int? notEntered = null;
        byte[] death = ExclusiveDropPackets.MonsterDeath(1, [], 316);
        byte[] deathAbsent = ExclusiveDropPackets.MonsterDeath(1, [], notEntered);
        That(death.SequenceEqual(deathAbsent) && death.Length == 9 && !Contains(death, hell[0]) && !Contains(death, dawn[0]),
            "未证明时 DIE_MONSTER 不带专表物品，没进的副本也不带");
        ExclusiveTableNotice.RejectUnproven(ExclusiveNotice.Discard, 316);
        ExclusiveTableNotice.RejectUnproven(ExclusiveNotice.Discard, notEntered);
        var experience = new DungeonClearExperienceReward(0, 0, 0, 0, "rule", "source");
        byte[] card = ExclusiveDropPackets.ClearCard(experience, 0, null, 316);
        byte[] cardAbsent = ExclusiveDropPackets.ClearCard(experience, 0, null, notEntered);
        That(card.SequenceEqual(cardAbsent) && !Contains(card, hell[0]) && !Contains(card, dawn[0]),
            "未证明时通关卡不带专表物品");
        That(!ExclusiveTableNotice.ParticularHellTriggerProven && ExclusiveTableNotice.ParticularHellItemIds(316).Count == 0 &&
            ExclusiveTableNotice.ParticularHellItemIds(notEntered).Count == 0,
            "没进图的副本不领取专表");
        Console.WriteLine("particular-hell no-grant check passed");

        var sections = ClearRewardSections.Walk(pvf.ReadTokens(ClearRewardSections.Path), pvf.ResolveString);
        That(sections.Any(row => row.Name == "dungeon index privilege service reward rate" && row.Use == ClearRewardSectionUse.ReadAlready) &&
            sections.Any(row => row.Name == "gold card cost table" && row.Use == ClearRewardSectionUse.ReadAlready),
            "强袭段和付费翻牌价格仍是原来已读的段");
        That(sections.Any(row => row.Name == "dungeon_index specific reward rate" && row.Use == ClearRewardSectionUse.PartlyRead) &&
            DungeonCardPriceCatalog.Load(pvf).SpecificRewards is var specific && specific.ContainsKey(530) &&
            new[] { 291100320, 291100322, 5349 }.All(specific.ContainsKey),
            "副本专表保留黎明裂缝并接入三种泰波尔斯固定奖励，未解释子表仍未发放");
        That(sections.Any(row => row.Name == "specific goldcard group" && row.Use == ClearRewardSectionUse.PartlyRead) &&
            DungeonCardPriceCatalog.Load(pvf).TayberrsPaidRewards.Length == 4,
            "金币奖励只接入类型71的101组，其余组保留未发放状态");
        foreach (string name in new[] { "special dungeon clear reward",
            "chronicle set reward rate", "reward dungeon type list", "reward item list" })
            That(sections.Any(row => row.Name == name && row.Use == ClearRewardSectionUse.NotGranted),
                "未证明的通关奖励段不发放 " + name);
        That(ClearRewardSections.ExtraItemGrants().Count == 0, "未证明的通关奖励段不产生物品");
        var prices = DungeonCardPriceCatalog.Load(pvf);
        That(prices.Price(1) == DungeonCardPriceCatalog.Parse(pvf.ReadTokens(DungeonCardPriceCatalog.ResourcePath), pvf.ResolveString).Price(1),
            "付费翻牌价格表仍按原解析器读出");
        var dungeons = DungeonCatalog.Load(pvf);
        var assault = AssaultModeCatalog.Load(pvf, dungeons);
        That(assault.ClearRewards.Count > 0, "强袭通关奖励段仍按原解析器读出");
        foreach (var row in sections)
            Console.WriteLine($"clearreward section {row.Name} {row.Use}");
        foreach (string name in sections.Where(row => row.Use == ClearRewardSectionUse.NotGranted).Select(row => row.Name).Distinct())
            Console.WriteLine("clearreward no-grant " + name);

        var crack = CrackDimensionCatalog.Load(pvf, items.Contains);
        That(dungeons.Get(1).IsCrackOfDimension && !dungeons.Get(3).IsCrackOfDimension,
            "带裂缝标记的副本才领这张表");
        var band = crack.Bands.Single(row => row.MinLevel <= 55 && row.MaxLevel >= 55);
        var clear = crack.Decide(band.MinLevel, 0, false);
        var bonus = crack.Decide(band.MinLevel, crack.DailyClearCount - 1, false);
        var again = crack.Decide(band.MinLevel, crack.DailyClearCount, true);
        That(clear.ClearItems.SequenceEqual(band.Items) && clear.Daily is null && band.Items.All(row => items.Contains(row.ItemId) && row.Count > 0),
            "等级段通关奖励是文件里的物品和数量");
        That(bonus.Daily == new CrackItemReward(crack.Daily.ItemId, crack.Daily.Count) && items.Contains(crack.Daily.ItemId) &&
            again.Daily is null && again.ClearItems.SequenceEqual(band.Items),
            "每日奖励在达到通关次数时发一次");
        That(!Contains(death, band.Items[0].ItemId) && !Contains(card, band.Items[0].ItemId) &&
            !Contains(death, crack.Daily.ItemId) && !Contains(card, crack.Daily.ItemId),
            "裂缝奖励不写进死亡包或翻牌包");
        Console.WriteLine($"crack-dimension grant reward int data item {band.Items[0].ItemId} count {band.Items[0].Count} levels {band.MinLevel}-{band.MaxLevel}");
        Console.WriteLine($"crack-dimension grant daily reward info clears {crack.Daily.Clears} item {crack.Daily.ItemId} count {crack.Daily.Count}");
        That(!File.ReadAllText(Path.Combine(ServerRoot(), "Network", "Projection", "TownProjection.cs")).Contains("PARTICULAR_HELL", StringComparison.Ordinal) &&
            !File.ReadAllText(Path.Combine(ServerRoot(), "Network", "Projection", "TownProjection.cs")).Contains("CRACK_OF_DAYBREAK", StringComparison.Ordinal),
            "进城初始化不发送裂缝次数包");
        Console.WriteLine("rift count packets stay test-only; town init does not send PARTICULAR_HELL or CRACK_OF_DAYBREAK_INFO");
    }

    static string ServerRoot([CallerFilePath] string thisFile = "")
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DfoServer.csproj")))
            root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("找不到 DfoServer 源码目录");
    }

    static void RejectLiterals(int hellFirst, int hellSecond, int dawnFirst, int dawnSecond, [CallerFilePath] string thisFile = "")
    {
        var root = new DirectoryInfo(ServerRoot(thisFile));
        string[] needles = [hellFirst.ToString(CultureInfo.InvariantCulture), hellSecond.ToString(CultureInfo.InvariantCulture),
            dawnFirst.ToString(CultureInfo.InvariantCulture), dawnSecond.ToString(CultureInfo.InvariantCulture)];
        foreach (string file in Directory.EnumerateFiles(root.FullName, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            foreach (string needle in needles)
                That(!source.Contains(needle, StringComparison.Ordinal), "C# 不写死专表物品 " + needle);
        }
    }

    static int[] FileIndexes(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var indexes = new List<int>();
        for (int i = 0; i < lines.Length - 1; i++)
            if (lines[i].Trim().Equals("[index]", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(lines[i + 1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                indexes.Add(index);
        return indexes.ToArray();
    }

    static int[] RawItemList(string text, int index)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].Trim().Equals("[index]", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(lines[i + 1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int found) || found != index)
                continue;
            int start = Array.FindIndex(lines, i, line => line.Trim().Equals("[item list]", StringComparison.OrdinalIgnoreCase));
            int end = Array.FindIndex(lines, start + 1, line => line.Trim().Equals("[/item list]", StringComparison.OrdinalIgnoreCase));
            var ids = new List<int>();
            for (int line = start + 1; line < end; line++)
                foreach (string part in lines[line].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    ids.Add(int.Parse(part, CultureInfo.InvariantCulture));
            return ids.ToArray();
        }
        throw new InvalidOperationException("文件里没有副本块 " + index);
    }

    static bool SharesStorage(ImmutableArray<int> left, ImmutableArray<int> right)
    {
        var field = typeof(ImmutableArray<int>).GetField("array", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ImmutableArray 存储字段缺失，无法检查列表是否共用");
        return ReferenceEquals(field.GetValue(left), field.GetValue(right));
    }

    static bool Contains(ReadOnlySpan<byte> body, int itemId)
    {
        var needle = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(needle, itemId);
        return body.IndexOf(needle) >= 0;
    }

    static Script CrackFixture(bool oddReward = false, bool dailyMismatch = false)
    {
        var script = new Script();
        script.Tag("[limit min level]"); script.Int(55); script.Tag("[/limit min level]");
        script.Tag("[daily enter count]"); script.Int(10); script.Tag("[/daily enter count]");
        script.Tag("[reward data]");
        script.Tag("[min level]"); script.Int(55);
        script.Tag("[max level]"); script.Int(200);
        script.Tag("[reward int data]"); script.Int(42); if (!oddReward) script.Int(1); script.Tag("[/reward int data]");
        script.Tag("[/reward data]");
        script.Tag("[daily clear count]"); script.Int(2); script.Tag("[/daily clear count]");
        script.Tag("[daily reward info]"); script.Int(dailyMismatch ? 9 : 2); script.Int(43); script.Int(4); script.Tag("[/daily reward info]");
        script.Tag("[crack buff list]"); script.Int(1); script.Tag("[/crack buff list]");
        script.Tag("[crack info list]"); script.Int(1); script.Tag("[/crack info list]");
        return script;
    }

    sealed class Script
    {
        readonly List<string> strings = [];
        readonly List<PvfToken> tokens = [];
        public void Tag(string name) { strings.Add(name); tokens.Add(new(3, strings.Count - 1)); }
        public void Int(int value) => tokens.Add(new(0, value));
        public void Float(float value) => tokens.Add(new(2, BitConverter.SingleToInt32Bits(value)));
        public PvfToken[] Tokens => tokens.ToArray();
        public string Resolve(int index) => strings[index];
    }
}
