using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarPackageTests
{
    public static void RunCore()
    {
        ForcedOptionsParsing();
        var full = Package("101 1 3 0 102 2 4 37", 0);
        That(full.Recognized, "装扮族身份与当前可用性分别保留");
        string firstGrow = "[usable first growtype]\n5\n[/usable first growtype]\n";
        var gated = AvatarPackageCatalog.Parse(PackageText("101 1 3 0", 1) + firstGrow);
        That(gated.Recognized && gated.Available && gated.FirstGrowTypes.SequenceEqual(new byte[] { 5 }) &&
            gated.AllowsFirstGrowType(0x25) && !gated.AllowsFirstGrowType(4), "装扮选择包保留同一首转根资格");
        foreach (string malformed in new[] { firstGrow + firstGrow, "[usable first growtype]\n[/usable first growtype]",
            "[usable first growtype]\n6\n[/usable first growtype]", "[usable first growtype]\n5" })
        {
            var rejected = AvatarPackageCatalog.Parse(PackageText("101 1 3 0", 1) + malformed);
            That(rejected.Recognized && !rejected.Available, "非法首转声明仍保留装扮族身份，不能回落普通开箱");
        }

        foreach (string kind in new[] { "equipment", "stackable" })
        {
            string normal = PackageText("101 1", 1).Replace("[avatar]", "[" + kind + "]").Replace("[/avatar]", "[/" + kind + "]") +
                "\n[name]\n`说明中的[avatar]不是真实装扮奖励`";
            That(!AvatarPackageCatalog.Parse(normal).Recognized, "普通装备或堆叠自选分类不因分类存在或文字伪标签进入装扮路由");
        }
        var pending = AvatarPackageCatalog.Parse(PackageText("101 1 3 0") + "\n[cooltime maintenance]\n1");
        That(pending.Recognized && !pending.Available && pending.Categories.IsEmpty && pending.PendingRule == "cooltime maintenance",
            "上层未闭合资格提前拒绝仍保留装扮身份，不能回落为普通礼包");
        var special = AvatarPackageCatalog.Parse(PackageText("101 1 3 0").Replace("[avatar]", "[special avatar]").Replace("[/avatar]", "[/special avatar]"));
        That(special.Recognized && special.Categories[0].PendingRule == "special avatar", "未实现的special avatar族也保留所属入口");
        That(full.Available && full.Dimensions.SequenceEqual(new[] { 2, 4 }) && full.Categories[0].Rows.SequenceEqual(
            new AvatarPackageRow[] { new(101, 1, 3, 0), new(102, 2, 4, 37) }), "装扮四字段与两个分类坐标完整保留");
        var category = full.Categories[0];
        string implicitCategory = PackageText("101 1 3 0").Replace("2 2 4", "1 0").Replace("1 3\n[avatar]", "0 0\n[avatar]");
        var implicitDefinition = AvatarPackageCatalog.Parse(implicitCategory);
        That(implicitDefinition.Available && implicitDefinition.Dimensions.SequenceEqual(new[] { 1 }) &&
            implicitDefinition.Categories.Single().Category == 0, "零分类维度只开放隐式坐标0");
        That(!AvatarPackageCatalog.Parse(implicitCategory.Replace("0 0\n[avatar]", "1 0\n[avatar]")).Available,
            "零分类不能接受非零分类坐标");
        string rankZero = implicitCategory.Replace("[booster category num]\n1 0", "[booster category num]\n0");
        var rankZeroDefinition = AvatarPackageCatalog.Parse(rankZero);
        That(rankZeroDefinition.Available && rankZeroDefinition.Dimensions.SequenceEqual(new[] { 1 }) &&
            rankZeroDefinition.Categories is [{ Category: 0, Subcategory: 0 }], "无分类选择礼盒唯一的0声明归一为坐标0/0");
        foreach (string invalid in new[] {
            rankZero.Replace("[booster category num]\n0", "[booster category num]\n"),
            rankZero.Replace("[booster category num]\n0", "[booster category num]\n1"),
            rankZero.Replace("[booster category num]\n0", "[booster category num]\n0 0"),
            rankZero + "\n[booster category num]\n0",
            rankZero + "\n[/booster select category]",
            rankZero.Replace("0 0\n[avatar]", "0 1\n[avatar]"),
            rankZero.Replace("0 0\n[avatar]", "1 0\n[avatar]"),
            rankZero + "\n[booster select category]\n0 0\n[avatar]\n102 1 3 0\n[/avatar]\n[/booster select category]" })
            That(!AvatarPackageCatalog.Parse(invalid).Available, "无分类格式拒绝缺失、非零、额外token、重复声明及多余分类");
        That(InventoryAvatarPackageService.TrySelect(full, category, [102, 101], out var rows) && rows.SequenceEqual(category.Rows),
            "全领分类按资源顺序生成完整奖励，不依赖客户端回显顺序");
        foreach (int[] ids in new int[][] { [], [101], [101, 101], [101, 103] })
            That(!InventoryAvatarPackageService.TrySelect(full, category, ids, out _), "全领不能缺项、重复或混合分类");
        var single = Package("101 1 3 0 102 1 4 0", 1);
        That(InventoryAvatarPackageService.TrySelect(single, single.Categories[0], [102], out rows) && rows.Single().Type == 4 &&
            !InventoryAvatarPackageService.TrySelect(single, single.Categories[0], [101, 102], out _), "单件自选保留所选行参数并遵守PVF上限");
        foreach (string malformed in new[] { "101 1 3", "101 0 3 0", "101 65536 3 0", "101 1 65536 0", "101 1 3 256" })
            That(Package(malformed).Categories[0].PendingRule is not null, "装扮行宽及u16/u8字段边界拒绝");
        That(Package("101 1 3 0\n[/avatar]\n[equipment]\n103 1\n[/equipment]\n[avatar]\n102 1 3 0").Categories[0].PendingRule is not null,
            "混合未实现奖励段不生成截断的装扮子集");
        var avatar = AvatarPackageCatalog.ParseAvatar(AvatarText("0 `[HP MAX]` `+` 100 37 `[SKILL_LEVEL]` `[swordman]` 5 1"));
        var implicitType = AvatarPackageCatalog.ParseAvatar("[equipment type]\n`[aurora avatar]` 0");
        That(implicitType.PendingRule is null && implicitType.Types.IsEmpty && !implicitType.RequiresAbilityChoice,
            "无期限表和属性声明时保留模板默认入口，不补造类型行");
        That(AvatarPackageCatalog.ParseAvatar("[avatar type select]\n[/avatar type select]").PendingRule is not null &&
            AvatarPackageCatalog.ParseAvatar("[avatar type select]\n0 0 0 0 0 0 0").PendingRule is not null,
            "明确声明的空表及未闭合类型表不能冒充省略类型表");
        That(AvatarPackageCatalog.ParseAvatar(AvatarText("0 `[HP MAX]` `+` 100") + "\n[import script]\n`equipment/base.equ`")
            .PendingRule == "装扮import继承视图尚未闭合" &&
            AvatarPackageCatalog.ParseAvatar("[name]\n`[import script]是名称文字`\n" + AvatarText("0 `[HP MAX]` `+` 100")).PendingRule is null,
            "真实import声明保留Pending，不能漏继承字段后回退属性0；名称伪标签不阻断独立完整模板");
        var inherited = new AvatarPackageCatalog(path => path == "equipment/child.equ" ?
            "[avatar type select]\n0 0 0 0 0 0 0\n[/avatar type select]\n[import script]\n`equipment/base.equ`\n[ability case index]\n50" :
            throw new InvalidOperationException("import子模板不应提前装载未闭合依赖"),
            new Dictionary<int, string> { [101] = "child.equ" }, new Dictionary<int, string>());
        That(inherited.GetAvatar(101).PendingRule == "装扮import继承视图尚未闭合", "目录入口不将父模板缺失属性视为0，也不提前读取无效共用case");
        That(avatar.PendingRule is null && avatar.Types.Length == 5 && avatar.Types[0].Days == 7 && avatar.Types[1].Days == 7 &&
            avatar.Types[2].Days == 30 && avatar.Types[3].Days == 0 && avatar.Types[3].Sockets == default &&
            avatar.Types[4].Sockets == new AvatarSocketTypes(239, 239, 0, 0, 0) && avatar.Abilities.SetEquals([0, 37]),
            "原生默认类型、PVF行索引、永久双孔及大于31的属性编号保留");
        foreach (string abilities in new[] { "", "0 `[HP MAX]` `+` 100 0 `[MP MAX]` `+` 1", "256 `[HP MAX]` `+` 100",
            "0 `[SKILL_LEVEL]` `[swordman]` 5", "0 `[HP MAX]` `+` bad" })
            That(AvatarPackageCatalog.ParseAvatar(AvatarText(abilities)).PendingRule is not null, "缺失、重复和非法属性声明拒绝");
        string shared = AvatarText("0 `[HP MAX]` `+` 100").Replace("[avatar select ability]\n0 `[HP MAX]` `+` 100\n[/avatar select ability]",
            "[ability case index]\n50");
        That(AvatarPackageCatalog.ParseAvatar(shared).PendingRule is not null && AvatarPackageCatalog.ParseAvatar(shared,
            new Dictionary<int, ImmutableHashSet<byte>> { [50] = [0, 37] }).Abilities.SetEquals([0, 37]),
            "共用属性缺失拒绝，实际case提供全部编号而不回退默认值");
        var cases = AvatarPackageCatalog.ParseAbilityCases("[ability datas]\n[ability case]\n50 1 `[SKILL_LEVEL]` `[swordman]` 41 1 " +
            "140 `[SKILL_LEVEL]` `[swordman]` 261 1\n[/ability case]\n[ability case]\n150 0 `[HP MAX]` `+` 100\n[/ability case]\n[/ability datas]");
        That(cases[50].SetEquals([1, 140]) && !cases[50].Contains(0) && cases[150].SetEquals([0]) &&
            AvatarPackageCatalog.ParseAvatar(shared, cases).Abilities.SetEquals([1, 140]), "case50不补不存在的0，完整保留大于31和127的选项");
        DuplicateAbilityDeclarations();
        DuplicateAbilityCaseIndices();
        RedundantAbilityCloseAtEof();
        byte[] body = Body([101, 102], [new(101, 0), new(102, 37)]);
        var request = AvatarPackageRequests.Read(body, 2, 2);
        That(request is { Slot: 73, Count: 1, Category: 1, Subcategory: 3 } && request.ItemIds.SequenceEqual([101, 102]) &&
            request.Choices.SequenceEqual(new AvatarPackageChoice[] { new(101, 0), new(102, 37) }), "160扩展保留每个5B装扮选项");
        foreach (byte[] invalid in new byte[][] { body[..^1], [.. body, 0], Body([101], [new(102, 0)]), Body([101], [new(101, 0), new(101, 1)]) })
            Invalid(() => AvatarPackageRequests.Read(invalid, 1, 2));
        var tail = body.ToArray(); tail[^1] = 1;
        Invalid(() => AvatarPackageRequests.Read(tail, 2, 2));
        var captured = AvatarPackageRequests.Read(CapturedSelection(), 9, 9);
        That(AvatarPackageRequests.IsSelection(CapturedSelection()) && !AvatarPackageRequests.IsSelection(new byte[9]) &&
            !AvatarPackageRequests.IsSelection(CapturedSelection()[..^1]),
            "耗尽源只辨认完整已支持装扮选择，未知9B或截断扩展保持原未实现边界");
        That(AvatarPackageRequests.ReadHeader(CapturedSelection()) == ((short)73, (byte)0, (byte)0), "分类头由Parser统一读取实际源槽和两个分类键");
        Invalid(() => AvatarPackageRequests.ReadHeader(CapturedSelection()[..7]));
        var negativeSlot = CapturedSelection(); negativeSlot[0] = 255; negativeSlot[1] = 255;
        Invalid(() => AvatarPackageRequests.ReadHeader(negativeSlot));
        That(captured is { Slot: 73, Count: 1, Category: 0, Subcategory: 0 } && captured.Choices.SequenceEqual(CapturedChoices()) &&
            captured.ItemIds.SequenceEqual(CapturedChoices().Select(choice => choice.ItemId)), "实际91B请求完整保留九部位和九项选择，不按普通160零扩展布局截断");
        QualificationAndDeadlines();
    }

    static void ForcedOptionsParsing()
    {
        string Forced(string pairs, string rows = "101 1 4 -1 102 2 4 -1") => PackageText(rows).Replace("[avatar]",
            "[force insert avatar option]\n" + pairs + "\n[/force insert avatar option]\n[avatar]");
        var definition = AvatarPackageCatalog.Parse(Forced("102 0 101 138"));
        That(definition.Available && definition.Categories[0].PendingRule is null && definition.Categories[0].Rows.SequenceEqual(
            new AvatarPackageRow[] { new(101, 1, 4, -1) { ForcedAbility = 138 }, new(102, 2, 4, -1) { ForcedAbility = 0 } }),
            "强制属性按物品编号绑定显式key，保留默认-1、原奖励顺序和全部数量，不按映射顺序或属性序号转换");
        var ordinary = Package("101 1 3 0 102 2 4 37");
        That(ordinary.Categories[0].Rows.All(row => row.ForcedAbility is null) &&
            ordinary.Categories[0].Rows.Select(row => row.DefaultAbility).SequenceEqual(new[] { 0, 37 }),
            "普通装扮默认预览与强制属性独立，未声明force不改变已有玩家选择合同");
        foreach (string invalid in new[]
        {
            Forced(""), Forced("101 138 102"), Forced("0 138 102 0"), Forced("101 -1 102 0"),
            Forced("101 256 102 0"), Forced("101 138"), Forced("101 138 103 0"),
            Forced("101 138 102 0 103 0"), Forced("101 138 101 138 102 0"), Forced("101 138 101 84 102 0"),
            Forced("101 138 102 0").Replace("[avatar]", "[force insert avatar option]\n101 138 102 0\n[/force insert avatar option]\n[avatar]"),
            Forced("101 138 102 0").Replace("[/force insert avatar option]", ""),
            Forced("101 138 102 0").Replace("[/force insert avatar option]", "[/force insert avatar option]\n[/force insert avatar option]"),
            Forced("101 138 102 0", "101 1 4 -2 102 2 4 -1"),
            Forced("101 138 102 0", "101 1 4 256 102 2 4 -1"),
            PackageText("101 1 4 -1 102 2 4 -1")
        })
        {
            var rejected = AvatarPackageCatalog.Parse(invalid);
            That(rejected.Recognized && (!rejected.Available || rejected.Categories.Any(row => row.PendingRule is not null)),
                "残缺、重复、冲突、越界和未覆盖全部奖励的强制属性不得截断发奖或放行默认-1");
        }
    }

    static void DuplicateAbilityDeclarations()
    {
        const string stats = "0 `[HP MAX]` `+` 100 37 `[MP MAX]` `*` 1.5";
        const string skill = "140 `[SKILL_LEVEL]` `[swordman]` 261 1";
        static string Block(string value) => "\n[avatar select ability]\n" + value + "\n[/avatar select ability]";
        foreach (string abilities in new[] { stats, skill })
        {
            var single = AvatarPackageCatalog.ParseAvatar(AvatarText(abilities));
            string repeated = AvatarText(abilities) + "\n[animation job]\n`[demonic swordman]`" +
                Block(" \t" + abilities.Replace(" `", "\r\n`") + "\n") + Block(abilities);
            var actual = AvatarPackageCatalog.ParseAvatar(repeated);
            That(actual.PendingRule is null && actual.RequiresAbilityChoice && actual.Types.SequenceEqual(single.Types) &&
                actual.Abilities.SetEquals(abilities == stats ? new byte[] { 0, 37 } : [140]),
                "不同动画段间完整同token的重复声明保留能力、期限与孔位，普通分隔空白不影响一致性");
        }
        foreach (string changed in new[] {
            "0 `[HP MAX]` `+` 101 37 `[MP MAX]` `*` 1.5",
            "0 `[HP MAX]` `+` 1e2 37 `[MP MAX]` `*` 1.5" })
        {
            var single = AvatarPackageCatalog.ParseAvatar(AvatarText(stats));
            var actual = AvatarPackageCatalog.ParseAvatar(AvatarText(stats) + Block(changed));
            That(actual.PendingRule is null && actual.RequiresAbilityChoice && actual.Types.SequenceEqual(single.Types) &&
                actual.Abilities.SetEquals(new byte[] { 0, 37 }),
                "重复完整块只改变合法非技能数字幅度或其词法时，保留全部能力编号、期限与孔位");
        }
        foreach (string changed in new[] {
            "0 `[HP MAX]` `*` 100 37 `[MP MAX]` `*` 1.5",
            "0 `[HP  MAX]` `+` 100 37 `[MP MAX]` `*` 1.5",
            "0 `[MP MAX]` `+` 100 37 `[MP MAX]` `*` 1.5",
            "37 `[MP MAX]` `*` 1.5 0 `[HP MAX]` `+` 100" })
            That(AvatarPackageCatalog.ParseAvatar(AvatarText(stats) + Block(changed)).PendingRule is not null,
                "能力索引相同仍拒绝操作符、字符串内部空白、种类或顺序冲突");
        foreach (string changed in new[] { "140 `[SKILL_LEVEL]` `[mage]` 261 1", "140 `[SKILL_LEVEL]` `[swordman]` 262 1",
            "140 `[SKILL_LEVEL]` `[swordman]` 261 2" })
            That(AvatarPackageCatalog.ParseAvatar(AvatarText(skill) + Block(changed)).PendingRule is not null,
                "重复技能能力声明的职业、技能编号与等级必须全部相同");
        foreach (string text in new[] {
            AvatarText(stats) + Block(""), AvatarText("") + Block(stats),
            AvatarText(stats) + Block("0 `[HP MAX]` `+`"),
            AvatarText(stats) + Block("0 `[HP MAX]` `+` bad 37 `[MP MAX]` `*` 1.5"),
            AvatarText(stats) + Block("0 `[HP MAX]` `+` `101` 37 `[MP MAX]` `*` 1.5"),
            AvatarText(stats) + Block(stats + "\n[unknown]\n1"),
            AvatarText(stats) + "\n[avatar select ability]\n" + stats,
            "[avatar select ability]\n" + stats + Block(stats),
            AvatarText(stats) + Block(stats) + "\n[/avatar select ability]",
            AvatarText(stats + " " + stats), AvatarText(stats + " " + stats) + Block(stats + " " + stats) })
            That(AvatarPackageCatalog.ParseAvatar(text).PendingRule is not null,
                "任一空段、残缺、未知结构、开闭数量不符及段内重复索引均整件拒绝");
        That(AvatarPackageCatalog.ParseAvatar("[name]\n`[avatar select ability]文字[/avatar select ability]`") is
            { PendingRule: null, RequiresAbilityChoice: false } noChoice && noChoice.Abilities.SetEquals([0]),
            "字符串伪标签仍不构成能力声明，无声明保留属性0");
    }

    static void RedundantAbilityCloseAtEof()
    {
        const string stats = "0 `[HP MAX]` `+` 280 1 `[MP MAX]` `+` 280 2 `[EQUIPMENT_PHYSICAL_DEFENSE]` `+` 660";
        const string close = "[/avatar select ability]";
        string complete = AvatarText(stats);
        foreach (string whitespace in new[] { "\n", " \t\r\n\r\n" })
        {
            var actual = AvatarPackageCatalog.ParseAvatar(complete + whitespace + close + whitespace);
            That(actual is { PendingRule: null, RequiresAbilityChoice: true } &&
                actual.Abilities.SetEquals([0, 1, 2]) && actual.Types.SequenceEqual(AvatarPackageCatalog.ParseAvatar(complete).Types),
                "单个完整合法属性块后的唯一同名EOF尾闭保留全部能力、期限与孔位");
        }
        foreach (string text in new[] {
            complete + "\n1\n" + close,
            complete + "\n[name]\n`tail`\n" + close,
            complete + "\n` `\n" + close,
            complete + "\n" + close + "\n1",
            complete + "\n" + close + "\n[name]\n`tail`",
            complete + "\n" + close + "\n" + close,
            complete + "\n[avatar select ability]\n" + close + "\n" + close,
            AvatarText(stats + "\n0 `[HP MAX]` `+` 280") + "\n" + close,
            AvatarText("0 `[HP MAX]` `+`") + "\n" + close,
            "[avatar select ability]\n" + complete + "\n" + close,
            complete + "\n[import script]\n`other.equ`\n" + close,
            complete + "\n" + close + "\n[import script]\n`other.equ`" })
            That(AvatarPackageCatalog.ParseAvatar(text).PendingRule is not null,
                "尾闭兼容不接受块内坏声明、嵌套、多开始、多尾闭、其他尾token或import");
    }

    static void DuplicateAbilityCaseIndices()
    {
        const string types = "[avatar type select]\n0 0 0 6000 0 0 0\n[/avatar type select]\n";
        var cases = new Dictionary<int, ImmutableHashSet<byte>> { [450] = [2, 109], [451] = [140], [452] = [] };
        static string Index(string value) => "[ability case index]\n" + value + "\n";
        foreach (string text in new[] {
            Index("450") + Index(" \t450\r\n"),
            Index("450") + "[animation job]\n`[priest]`\n" + Index("450") + Index("450"),
            Index("450"), Index("+450"), Index("0450") })
        {
            var actual = AvatarPackageCatalog.ParseAvatar(types + text, cases);
            That(actual.PendingRule is null && actual.RequiresAbilityChoice && actual.Abilities.SetEquals([2, 109]) &&
                !actual.Abilities.Contains(0) && actual.Types is [{ Days: 7 }, { Days: 0, Cera: 6000, Sockets: var sockets }] && sockets == default,
                "完整同token的共享case索引重复保留真实选项及期限价格，不补默认0且保留原单值整数语法");
        }
        foreach (string text in new[] {
            Index("450") + Index("451"), Index("450") + Index("0450"), Index("450") + Index("+450"),
            Index("450") + Index(""), Index("") + Index("450"), Index("450") + Index("450 450"),
            Index("450 450") + Index("450"), Index("450") + Index("bad"), Index("450") + Index("450 bad"),
            Index("450") + Index("450.0"), Index("450") + Index("4.5e2"), Index("450") + Index("`450`"),
            Index("2147483648"), Index("-450"), Index("453") + Index("453"), Index("452") + Index("452") })
            That(AvatarPackageCatalog.ParseAvatar(types + text, cases).PendingRule is not null,
                "任一共享索引冲突、原始词法差异、空段、多token、非法整数、缺case或空case均拒绝");
        That(AvatarPackageCatalog.ParseAvatar(types + Index("450") + Index("450")).PendingRule is not null,
            "同值重复不能绕过共享属性表缺失");
        That(AvatarPackageCatalog.ParseAvatar(types + "[name]\n`[ability case index]文字`\n", cases) is
            { PendingRule: null, RequiresAbilityChoice: false } noChoice && noChoice.Abilities.SetEquals([0]),
            "字符串伪共享索引仍不是能力声明");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        SpecialSkinDefaults(towns, resources);
        ForcedOptionsResources(towns, resources, packages);
        DuplicateConstantMagnitudeResources(towns, resources, packages);
        FirstGrowResources(towns, resources, packages);
        RankZeroResources(towns, resources, packages);
        AbilitySentinelResources(towns, resources, packages);
        var items = resources.Items;
        var service = new InventoryAvatarPackageService(items, packages, resources.GoldLimits, resources.EpicPieces);
        var now = DateTimeOffset.UtcNow;
        var character = new Character(1, 0, "AvatarBox", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        foreach (var sample in new[] { (Id: 490705387, Outputs: 9), (Id: 490705425, Outputs: 8), (Id: 490705426, Outputs: 1) })
        {
            var definition = packages.Get(sample.Id);
            That(definition.Available && definition.Categories.Length == (sample.Id == 490705387 ? 64 : 16), "实际礼盒完整分类来自PVF");
            var category = definition.Categories.Single(row => row.Category == 0 && row.Subcategory == 0);
            var chosen = definition.SelectionCount == 0 ? category.Rows.ToArray() : [category.Rows.Single(row => row.ItemId == 42611)];
            var options = sample.Id == 490705387 ? CapturedChoices() :
                chosen.Select(row => new AvatarPackageChoice(row.ItemId, packages.GetAvatar(row.ItemId).Abilities.Max())).ToArray();
            var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(items.Get(sample.Id), 2, now,
                shopExpiration: true) with { SortLockFlag = 1 });
            var before = new InventorySnapshot([source], Gold: 321);
            character = character with { Inventory = before };
            var request = new AvatarPackageOpenRequest(73, 1, 0, 0, chosen.Select(row => row.ItemId).ToArray(), options);
            var result = service.Open(character, request, out var receipt, now);
            That(result.Accepted && character.Inventory == before && result.Plan!.After.Find(Bag()) is { Uid: 1000, Core.Count: 1 } &&
                result.Plan.After.Items.Count(item => item.Core.Kind == ItemKind.Avatar) == sample.Outputs && receipt!.Rewards.Sum(row => row.Count) == sample.Outputs,
                "九部位套装、八部位克隆和单件自选完整规划，源仅扣一个且排序锁不限制使用");
            foreach (var reward in result.Plan!.After.Items.Where(item => item.Avatar is not null))
            {
                byte option = options.Single(choice => choice.ItemId == reward.Core.ItemId).Ability;
                That(reward.Uid == 0 && reward.Core.Value == 0 && reward.Core.Durability == option && reward.Core.Attr == 0 &&
                    reward.Avatar is { ExpireTime: 0, ClearAvatarId: 0, Color1: 0, Color2: 0 }, "新装扮完整详情与真实属性编号保留待提交UID");
                if (reward.Core.ItemId == 401580026)
                    That(reward.Avatar!.Socket0 == new AvatarSocket(239, -1) && reward.Avatar.Socket1 == new AvatarSocket(239, -1), "皮肤按type4生成两个M孔");
            }
            That(!service.OpenCeraPackage(character, new(73, []), out _, now).Accepted, "518零选择不会消耗自选礼盒或自动决定职业");
            foreach (var bad in new[] { request with { ItemIds = [] }, request with { Choices = [] },
                request with { Choices = [.. options[..^1], new(options[^1].ItemId, 255)] } })
                That(!service.Open(character, bad, out _, now).Accepted && character.Inventory == before, "选择缺失与无效属性不消耗源或改变资产");
            foreach (var core in new[] { source.Core with { EquipmentLockId = 1 }, source.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } })
                That(!service.Open(character with { Inventory = new([source with { Core = core }]) }, request, out _, now).Accepted,
                    "安全锁和源期限在完整奖励前检查");
            var cera = new CeraPackageOpenRequest(73, options.Select(choice => new CeraPackageChoice(choice.ItemId, choice.Ability)).ToArray());
            That(service.OpenCeraPackage(character, cera, out _, now).Accepted, "518识别完整等价职业分类，不因共用套装编号拒绝");
            var firstTemplate = items.Get(chosen[0].ItemId);
            var filled = Enumerable.Range(0, ItemSlotBoundService.AvatarCapacity).Select(slot => InventoryRewardGrantService.CreateItem(firstTemplate,
                new(InventoryListType.Avatar, (short)slot), InventoryRewardGrantService.Create(firstTemplate, utcNow: now)) with
                { Uid = slot + 1, Core = InventoryRewardGrantService.Create(firstTemplate, utcNow: now) with { Value = slot + 1 } }).ToImmutableArray();
            character = character with { Inventory = new([source, .. filled]) };
            That(!service.Open(character, request, out _, now).Accepted, "160容量不足整批保留源和已有装扮");
            result = service.OpenCeraPackage(character, cera, out _, now);
            That(result.Accepted && result.Plan!.OverflowItems.Length == sample.Outputs && result.Plan.After.Items.Length == ItemSlotBoundService.AvatarCapacity + 1 &&
                result.Plan.OverflowItems.All(item => item.Uid == 0 && item.Avatar is not null &&
                    item.Core.Durability == options.Single(choice => choice.ItemId == item.Core.ItemId).Ability),
                "518满装扮页将所有完整原型移交邮件，不丢属性、详情或任何奖励");
        }
        PeriodDeadlinePlans(towns, resources);
        CapturedNoAbilitySelections(towns, resources, packages);
        LegacySelections(towns, resources, packages);
    }

    static void SpecialSkinDefaults(TownCatalog towns, ItemResources resources)
    {
        const int skinId = 101580164, boxId = 490704635;
        foreach (ushort type in new ushort[] { 1, 2 })
        {
            var packages = new AvatarPackageCatalog(path => path.StartsWith("stackable/", StringComparison.Ordinal)
                ? PackageText($"{skinId} 1 {type} 0")
                : "[avatar type select]\n0 0 0 0 0 0 0 0 0 0 0 0 0 2 `[M socket]` `[M socket]`\n[/avatar type select]\n" +
                    "[avatar select ability]\n0 `[PHYSICAL ABSOLUTE DEFENSE]` `+` 3.5\n[/avatar select ability]",
                new Dictionary<int, string> { [skinId] = "skin.equ" }, new Dictionary<int, string> { [boxId] = "box.stk" });
            var source = new InventoryItem(1000, Bag(), new(boxId, ItemKind.Consumable, 1));
            var character = new Character(1, 0, "SkinDefaults", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
            var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
            var opened = service.Open(character, new AvatarPackageOpenRequest(73, 1, 1, 3, [skinId], [new(skinId, 0)]), out _);
            That(opened.Accepted && opened.Plan!.After.Items.Single().Avatar is
                { Socket0: { Type: 239, EmblemId: -1 }, Socket1: { Type: 239, EmblemId: -1 }, Socket2.Type: 0 },
                "特殊皮肤礼包沿模板保留双彩孔，未开孔类型不覆盖默认孔，带孔类型也不重复加孔");
        }
    }

    static void ForcedOptionsResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        // 两个分类回显相同ID但两件上衣的强制key不同；预期来自各自完整资源行。
        AvatarPackageChoice[] first = [new(101500896, 70), new(101501102, 76), new(101550946, 0), new(101561081, 0),
            new(101570842, 2), new(101520903, 2), new(101511262, 0), new(101530865, 4), new(101541018, 0)];
        AvatarPackageChoice[] second = [new(101501102, 78), new(101500896, 84), new(101550946, 0), new(101561081, 0),
            new(101570842, 2), new(101520903, 2), new(101511262, 0), new(101530865, 4), new(101541018, 0)];
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(10305409), utcNow: now));
        var before = new InventorySnapshot([source], Gold: 321);
        var character = new Character(1, 0, "ForcedAvatar", 0, towns.Room, towns.InitialReturn) { Job = 0, Level = 95, Inventory = before };
        foreach (var sample in new[] { (Subcategory: (byte)0, Choices: first), (Subcategory: (byte)1, Choices: second) })
        {
            int[] ids = sample.Choices.Select(choice => choice.ItemId).ToArray();
            var request = new AvatarPackageOpenRequest(73, 1, 0, sample.Subcategory, ids, sample.Choices);
            var result = service.Open(character, request, out var receipt, now);
            AssertFull(result, receipt, sample.Choices, "160");
            var cera = new CeraPackageOpenRequest(73, sample.Choices.Select(choice => new CeraPackageChoice(choice.ItemId, choice.Ability)).ToArray());
            result = service.OpenCeraPackage(character, cera, out receipt, now);
            AssertFull(result, receipt, sample.Choices, "518");
        }
        // 84是第一件上衣的正式合法key，但不属于0/0；不能只验证目标能力集合。
        var wrong = first.Select(choice => choice.ItemId == 101500896 ? new AvatarPackageChoice(choice.ItemId, 84) : choice).ToArray();
        foreach (var invalid in new[] { wrong, first[..^1], first.Select(choice => choice.ItemId == 101500896 ? new AvatarPackageChoice(choice.ItemId, byte.MaxValue) : choice).ToArray() })
        {
            var request = new AvatarPackageOpenRequest(73, 1, 0, 0, first.Select(choice => choice.ItemId).ToArray(), invalid);
            var denied = service.Open(character, request, out var receipt, now);
            That(!denied.Accepted && denied.Plan is null && receipt is null && character.Inventory == before,
                "160拒绝合法但不符本分类的force、缺项和伪造选项，保留完整源与资产");
        }
        var mixed = first.Select(choice => choice.ItemId == 101501102 ? new AvatarPackageChoice(choice.ItemId, 78) : choice).ToArray();
        var conflict = service.OpenCeraPackage(character, new(73, mixed.Select(choice => new CeraPackageChoice(choice.ItemId, choice.Ability)).ToArray()), out var conflictReceipt, now);
        That(!conflict.Accepted && conflict.Plan is null && conflictReceipt is null && character.Inventory == before,
            "518不能混用两个分类的合法key拼成无原始完整分类的请求");

        void AssertFull(InventoryResult result, BoosterReceipt? receipt, AvatarPackageChoice[] choices, string command)
        {
            That(result.Accepted && result.Plan is not null && receipt is not null && character.Inventory == before &&
                result.Plan.After.Find(Bag()) is null && result.Plan.After.Gold == 321 && result.Plan.After.Items.Length == 9 &&
                receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(choices.OrderBy(row => row.ItemId).Select(row => new BoosterGrantedItem(row.ItemId, 1))),
                command + "按完整force匹配分类，单次消费源并保留全部九件奖励，不因同ID异force错误拒绝");
            foreach (var choice in choices)
            {
                var reward = result.Plan!.After.Items.Single(item => item.Core.ItemId == choice.ItemId);
                var type = packages.GetAvatar(choice.ItemId).Types[4];
                That(reward.Core.Durability == choice.Ability && reward.Core.ExpireTime == 0 && reward.Avatar is { ExpireTime: 0 } &&
                    reward.Avatar.Socket0 == AvatarSocket.Empty(type.Sockets.First) && reward.Avatar.Socket1 == AvatarSocket.Empty(type.Sockets.Second) &&
                    reward.Avatar.Socket2 == AvatarSocket.Empty(type.Sockets.Third) && reward.Avatar.Socket3 == AvatarSocket.Empty(type.Sockets.Fourth) &&
                    reward.Avatar.Socket4 == AvatarSocket.Empty(type.Sockets.Fifth),
                    command + "完整保存该分类明确key、type4永久期限与五个原始孔位");
            }
        }
    }

    static void DuplicateConstantMagnitudeResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        const int sourceId = 490702666, avatarId = 39952;
        var definition = packages.Get(sourceId);
        var category = definition.Categories.Single(value => value.Category == 0 && value.Subcategory == 0);
        That(definition.Available && definition.SelectionCount == 1 && definition.Dimensions.SequenceEqual(new[] { 7, 2 }) &&
            definition.Categories.Length == 14 && category.Rows.SequenceEqual(new AvatarPackageRow[] {
                new(39186, 1, 3, 0), new(39601, 1, 3, 0), new(39952, 1, 3, 0), new(40342, 1, 3, 0),
                new(40825, 1, 3, 0), new(41220, 1, 3, 0), new(41545, 1, 3, 0), new(41997, 1, 3, 0) }),
            "绝版装扮单件自选真实0/0分类完整保留八部位、永久无孔类型及选择上限");
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(sourceId), 2, now));
        var before = new InventorySnapshot([source], Gold: 321);
        Character character = new(1, 0, "DupAvatar", 0, towns.Room, towns.InitialReturn)
            { Job = 0, Level = 99, Inventory = before };
        var sourceTemplate = resources.Items.Get(sourceId);
        var targetTemplate = resources.Items.Get(avatarId);
        That(InventoryAvatarPackageService.SourceEligible(character, source, sourceTemplate) &&
            definition.AllowsFirstGrowType(character.GrowType) &&
            source.Core is { IsStackable: true, Count: 2, EquipmentLockId: 0 } && !source.IsExpired(now.ToUnixTimeSeconds()) &&
            targetTemplate.Kind == ItemKind.Avatar && targetTemplate.Expiration.SupportsCreation,
            "RED前提：真实源资格、首转、数量、锁及期限合法，目标是支持创建的装扮");
        // 39952两个完整属性块只有ATTACK_SPEED幅度50/55不同；先验证实际选择2，不由默认属性代选。
        var request = new AvatarPackageOpenRequest(73, 1, 0, 0, [avatarId], [new(avatarId, 2)]);
        foreach (byte ability in new byte[] { 2, 0, 1, 3 })
        {
            var chosen = request with { Choices = [new(avatarId, ability)] };
            var result = service.Open(character, chosen, out var receipt, now);
            if (!result.Accepted)
                That(packages.GetAvatar(avatarId).PendingRule == "装扮属性选项无效",
                    "真实39952合法选项被拒绝时，阻断应明确来自重复属性块校验");
            That(result.Accepted,
                $"真实490702666选择39952合法属性{ability}应可开启：accepted={result.Accepted} error={result.Error} metadata={packages.GetAvatar(avatarId).PendingRule}");
            var after = result.Plan!.After;
            var reward = after.Items.Single(item => item.Core.ItemId == avatarId);
            That(character.Inventory == before && after.Items.Length == 2 &&
                after.Find(Bag()) is { Uid: 1000, Core.Count: 1 } && after.Gold == 321 &&
                reward.Uid == 0 && reward.Core is { Value: 0, Attr: 0, ExpireTime: 0 } &&
                reward.Core.Durability == ability && reward.Avatar == new AvatarDetail() &&
                receipt!.ItemId == sourceId && receipt.Rewards.Single() is { ItemId: avatarId, Count: 1 },
                "常量幅度差异不改写玩家选择编号，单次扣源并生成一件永久无孔完整装扮原型");
        }
        var avatar = packages.GetAvatar(avatarId);
        That(avatar.PendingRule is null && avatar.RequiresAbilityChoice && avatar.Abilities.SetEquals([0, 1, 2, 3]) &&
            avatar.Types.Length == 5 && avatar.Types[3] is { Days: 0, Sockets: var sockets } && sockets == default,
            "真实重复属性块保留全部四个合法编号及原type3期限孔位，不选取或改写效果幅度");
        foreach (var bad in new[] { request with { Choices = [] }, request with { Choices = [new(avatarId, 4)] },
            request with { Choices = [new(avatarId, byte.MaxValue)] } })
        {
            var rejected = service.Open(character, bad, out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && character.Inventory == before,
                "真实常量重复声明仍拒绝缺失、未知与哨兵属性，失败不消耗源或生成奖励");
        }
    }

    static void LegacySelections(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        var now = DateTimeOffset.UtcNow;
        var debug = new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true };
        var avatars = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces, debug);
        var boosters = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            debugSettings: debug);
        Character character = new(1, 0, "LegacyAvatar", 0, towns.Room, towns.InitialReturn) { Job = 0, Level = 99 };
        var parent = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(2683776), 2, now,
            shopExpiration: true));
        for (byte category = 0; category < 4; category++)
        {
            int childId = 2683777 + category;
            var before = new InventorySnapshot([parent]);
            var result = boosters.Open(character with { Inventory = before }, new(73, 1, new(category, 0, [childId])), out var receipt, now);
            That(result.Accepted && result.Plan!.After.Find(Bag()) is { Uid: 1000, Core.Count: 1 } &&
                result.Plan.After.Items.Single(item => item.Core.ItemId == childId) is { Core.Count: 1, Avatar: null } &&
                result.Plan.After.Items.Length == 2 && receipt!.Rewards.Single().ItemId == childId,
                "追忆父盒四种合法分类仅扣一个源并生成所选实体子盒，不递归开启或提前发装扮");
        }
        foreach (int id in new[] { 490008059, 2683777, 2683778, 2683779, 2683780 })
        {
            var package = packages.Get(id);
            That(package.Available && package.SelectionCount == 0, "追忆装扮盒完整分类沿既有160全领合同");
            foreach (var category in package.Categories)
            {
                var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(id), 2, now,
                    shopExpiration: true));
                var before = new InventorySnapshot([source]);
                var choices = category.Rows.Select(row => new AvatarPackageChoice(row.ItemId, packages.GetAvatar(row.ItemId).Abilities.Max())).ToArray();
                var request = new AvatarPackageOpenRequest(73, 1, category.Category, category.Subcategory,
                    category.Rows.Select(row => row.ItemId).ToArray(), choices);
                var result = avatars.Open(character with { Inventory = before }, request, out var receipt, now);
                That(result.Accepted && result.Plan!.After.Find(Bag()) is { Uid: 1000, Core.Count: 1 } &&
                    result.Plan.After.Items.Length == 9 && receipt!.Rewards.Sum(row => row.Count) == 8 &&
                    result.Plan.After.Items.Where(item => item.Avatar is not null).All(item => item.Uid == 0 &&
                        item.Core is { Value: 0, Attr: 0, ExpireTime: 0 } && item.Avatar == new AvatarDetail() &&
                        item.Core.Durability == choices.Single(choice => choice.ItemId == item.Core.ItemId).Ability),
                    $"追忆各分类八部位保留合法能力、永久无孔完整原型且源仅扣一次 id={id} category={category.Category} accepted={result.Accepted} error={result.Error}");
            }
        }
    }

    static void CapturedNoAbilitySelections(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces,
            new() { IgnoreCashShopAbsoluteExpiration = true });
        foreach (int id in new[] { 401590018, 401590019 })
        {
            var template = resources.Items.Get(id);
            That(template is { Kind: ItemKind.Avatar, EquipmentSlot: 9 } &&
                template.AvatarSockets == new AvatarSocketTypes(239, 239, 0, 0, 0) &&
                packages.GetAvatar(id).Types.IsEmpty &&
                packages.Get(490704635).Categories.Single().Rows.Single(row => row.ItemId == id).Type == 4,
                "金秋光环礼盒type4没有对应期限表，须使用模板声明的永久双M孔");
            var direct = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, 0),
                InventoryRewardGrantService.Create(template, utcNow: now));
            That(direct.Avatar == new AvatarDetail { Socket0 = new(239, -1), Socket1 = new(239, -1) },
                "金秋光环直接发放保留两个开放空孔，其余孔位关闭且永久");
        }
        That(resources.Items.Get(101590015).AvatarSockets == new AvatarSocketTypes(239, 239, 0, 0, 0) &&
            resources.Items.Get(401590017).AvatarSockets == default,
            "带孔光环按声明读取，无孔光环不因同部位而补孔");
        foreach (var sample in new[] {
            (Id: 490705389, Body: "450001000000000014C7EF170000"),
            (Id: 490705389, Body: "450001000000000015C7EF170000"),
            (Id: 490704635, Body: "450001000000000002C7EF170000"),
            (Id: 490704635, Body: "450001000000000003C7EF170000"),
            (Id: 490705413, Body: "4400010000000900A18DC5180000") })
        {
            var request = AvatarPackageRequests.Read(Convert.FromHexString(sample.Body), 1, 1);
            var source = new InventoryItem(1000, new(InventoryListType.Main, request.Slot),
                InventoryRewardGrantService.Create(resources.Items.Get(sample.Id), shopExpiration: true));
            Character character = new(1, 0, "NoOption", 0, towns.Room, towns.InitialReturn) { Job = 15, Inventory = new([source]) };
            var result = service.Open(character, request, out var receipt, now);
            That(result.Accepted && result.Plan!.After.Items.Single() is { Avatar: not null, Core.Durability: 0 } reward &&
                reward.Core.ItemId == request.ItemIds.Single() && receipt!.Rewards.Single().Count == 1 &&
                character.Inventory.Items.Single() == source, $"真实光环及武器装扮请求不携带属性选项，原子规划完整详情且不直接修改原背包 id={sample.Id} accepted={result.Accepted}");
            if (sample.Id == 490705389)
                That(result.Plan!.After.Items.Single().Avatar is { Socket0.Type: 239, Socket1.Type: 239 },
                    "无期限表光环保留模板声明的两个M孔，不虚构期限类型表");
            if (sample.Id == 490704635)
                That(result.Plan!.After.Items.Single().Avatar ==
                    new AvatarDetail { Socket0 = new(239, -1), Socket1 = new(239, -1) },
                    "金秋礼盒两项选择均生成永久双M空孔，不被奖励行type4覆盖成无孔");
            That(!packages.GetAvatar(request.ItemIds[0]).RequiresAbilityChoice, "实际产物没有可选属性声明");
            foreach (var bad in new[] { request with { Choices = [new(request.ItemIds[0], 1)] },
                request with { Choices = [new(1, 0)] }, request with { Category = 255 } })
                That(!service.Open(character, bad, out _, now).Accepted, "省略属性兼容不放行伪造属性、外来编号或非法分类");
        }
    }

    static void FirstGrowResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces,
            new() { IgnoreCashShopAbsoluteExpiration = true });
        var now = DateTimeOffset.UtcNow;
        foreach (int id in new[] { 490703950, 490703951 })
        {
            var definition = packages.Get(id);
            That(definition.Available && definition.FirstGrowTypes.SequenceEqual(new byte[] { 5 }), "两种像素帽真实第五分支资格 " + id);
            var category = definition.Categories.Single();
            var row = category.Rows.Single();
            var avatar = packages.GetAvatar(row.ItemId);
            var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(id), utcNow: now, shopExpiration: true));
            var role = new Character(1, 0, "BranchHat", 0, towns.Room, towns.InitialReturn)
                { Job = id == 490703950 ? (byte)0 : (byte)3, GrowType = 0x25, Level = 95, Inventory = new([source]) };
            var request = new AvatarPackageOpenRequest(73, 1, category.Category, category.Subcategory, [row.ItemId],
                avatar.RequiresAbilityChoice ? [new(row.ItemId, row.ForcedAbility ?? checked((byte)row.DefaultAbility))] : []);
            That(!service.Open(role with { GrowType = 4 }, request, out _, now).Accepted,
                "错误分支不能通过选择装扮入口绕过资格 " + id);
            var opened = service.Open(role, request, out var receipt, now);
            That(opened.Accepted && receipt is { Rewards.Length: 1 } && receipt.Rewards[0].ItemId == row.ItemId &&
                opened.Plan!.After.Find(Bag()) is null && opened.Plan.After.Items.Single() is { Avatar: not null } &&
                !opened.Plan.CharacterPrecondition!.MatchesIdentity(role with { GrowType = 4 }),
                "合法第五分支获得完整装扮并绑定提交资格 " + id);
        }
    }

    static void RankZeroResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        int[] ids = [2683028, .. Enumerable.Range(2683229, 10), .. Enumerable.Range(2683789, 10), .. Enumerable.Range(2683883, 10)];
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces,
            new() { IgnoreCashShopAbsoluteExpiration = true });
        var rewards = new HashSet<int>();
        foreach (int id in ids)
        {
            var definition = packages.Get(id);
            That(definition.Available && definition.Categories is [{ Category: 0, Subcategory: 0 }] && definition.SelectionCount == 1,
                "实际无分类自选礼盒完整解析 " + id);
            foreach (var row in definition.Categories.Single().Rows)
            {
                var template = resources.Items.Get(row.ItemId);
                var avatar = packages.GetAvatar(row.ItemId);
                That(template.Kind == ItemKind.Avatar && avatar.PendingRule is null && (avatar.Types.IsEmpty || row.Type < avatar.Types.Length) &&
                    avatar.Abilities.Contains(row.ForcedAbility ?? checked((byte)row.DefaultAbility)), "全部实际无分类奖励的类型、期限及默认属性有效 " + row.ItemId);
                var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(id), 2, shopExpiration: true));
                var role = new Character(1, 0, "RankZero", 0, towns.Room, towns.InitialReturn) { Job = template.Jobs == 0 ? (byte)0 :
                    (byte)Enumerable.Range(0, 16).First(job => (template.Jobs & (1 << job)) != 0), Level = 95, Inventory = new([source]) };
                var request = new AvatarPackageOpenRequest(73, 1, 0, 0, [row.ItemId],
                    avatar.RequiresAbilityChoice ? [new(row.ItemId, row.ForcedAbility ?? checked((byte)row.DefaultAbility))] : []);
                var result = service.Open(role, request, out var receipt);
                That(result.Accepted && result.Plan!.After.Find(Bag())!.Core.Count == 1 &&
                    result.Plan.After.Items.Single(item => item.Core.Kind == ItemKind.Avatar) is { Avatar: not null } &&
                    receipt!.Rewards.Single().ItemId == row.ItemId && role.Inventory.Items.Single() == source,
                    "实际无分类自选奖励保留完整详情及源数量，提交前不改背包 " + id + "/" + row.ItemId);
                That(!service.Open(role, request with { Category = 1 }, out _).Accepted &&
                    !service.Open(role, request with { Subcategory = 1 }, out _).Accepted,
                    "无分类礼盒不能伪造非零坐标");
                var sourceTemplate = resources.Items.Get(id);
                for (byte job = 0; job < 16; job++)
                {
                    var candidate = role with { Job = job };
                    That(service.Open(candidate, request, out _).Accepted ==
                        InventoryAvatarPackageService.SourceEligible(candidate, source, sourceTemplate),
                        "无分类礼盒对全部职业沿源资格判断，不把奖励穿戴职业当成领取资格 " + id + "/" + job);
                }
                rewards.Add(row.ItemId);
            }
        }
        That(ids.Length == 31 && rewards.Count == 83, "31个无分类礼盒的83种实际奖励全量覆盖");
    }

    static void QualificationAndDeadlines()
    {
        var source = new InventoryItem(1000, Bag(), new(990301, ItemKind.Consumable, 2) { SortLockFlag = 1, ExpireTime = 100 });
        var template = new ItemTemplate(990301, ItemKind.Consumable, 0, 80, 0, 1000, 1, 0, 1, "[free]", 1 << 15, 77, "AvatarBox");
        Character character = new(1, 0, "AvatarUse", 0, new(0, 0, 0, 0), null)
        { Job = 15, Level = 80, Skills = [new(77, 1)], Inventory = new([source]) };
        That(InventoryAvatarPackageService.SourceEligible(character, source, template), "源礼盒的资源等级、职业和已学技能同时满足；排序锁允许使用");
        foreach (var denied in new[] { template with { UseForbidden = true }, template with { MinimumLevel = 81 },
            template with { Jobs = 1 }, template with { RequiredSkill = 78 }, template with { Id = 990302 }, template with { Kind = ItemKind.Avatar } })
            That(!InventoryAvatarPackageService.SourceEligible(character, source, denied), "禁用、等级、职业、技能和模板身份资格拒绝");
        foreach (var denied in new[] { character with { Level = 79 }, character with { Job = 0 }, character with { Job = 47 },
            character with { Skills = [] }, character with { Skills = [new(77, 0)] } })
            That(!InventoryAvatarPackageService.SourceEligible(denied, source, template), "角色未满足真实使用资格不能开启礼盒");
        var permanent = new InventoryItem(0, new(InventoryListType.Avatar, 0), new(990303, ItemKind.Avatar, 0)) { Avatar = new() };
        var earlier = permanent with { Avatar = new() { ExpireTime = 60 } };
        var later = permanent with { Avatar = new() { ExpireTime = 80 } };
        long? deadline = InventoryAvatarPackageService.EarliestDeadline(source, [permanent, later, earlier]);
        var condition = new InventoryCharacterPrecondition(character, deadline);
        That(deadline == 60 && condition.Matches(character, 59) && !condition.Matches(character, 60) && !condition.Matches(character, 61),
            "核心期限为0的装扮从详情取最早到期时间；提交到期前一秒可用，等于和超过到期拒绝");
        That(InventoryAvatarPackageService.EarliestDeadline(source with { Core = source.Core with { ExpireTime = 30 } }, [earlier, later]) == 30 &&
            InventoryAvatarPackageService.EarliestDeadline(source with { Core = source.Core with { ExpireTime = 0 } }, [permanent]) is null,
            "源先到期时仍绑定源期限；源和全部奖励永久时不制造截止时间");
    }

    // 使徒降临至尊装扮套装礼盒：分类内九件含无属性声明的武器装扮，客户端对它发255哨兵，
    // 不能把哨兵当成非法属性而整箱拒绝，也不能把它写进奖励核心。
    static void AbilitySentinelResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        const int packageId = 490705439, weaponId = 401600014;
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        var definition = packages.Get(packageId);
        That(definition.Available, "使徒降临礼盒按真实资源进入装扮入口，而不是回落普通开箱");
        var category = definition.Categories.Single(row => row.Category == 0 && row.Subcategory == 0);
        That(category.Rows.Length == 9 && category.Rows.Any(row => row.ItemId == weaponId), "零选择要求发放分类内全部九件");
        // 实际490705439请求：源槽67、九项ID清单、九项5B选项，武器装扮的选项为255。
        var captured = Convert.FromHexString("4300010000000000642BEF173552EF172179EF17E5B5EE17F767EE17EFDCEE17ED8EEE17" +
            "1A04EF170EEEEF1709642BEF17043552EF17042179EF1702E5B5EE1702F767EE170FEFDCEE1704ED8EEE17001A04EF17000EEEEF17FF00");
        var parsed = AvatarPackageRequests.Read(captured, 9, 9);
        That(captured.Length == 91 && parsed.Slot == 67 && parsed.Category == 0 && parsed.Subcategory == 0 &&
            parsed.Count == 1 && parsed.ItemIds.SequenceEqual(category.Rows.Select(row => row.ItemId)) &&
            parsed.Choices.Length == 9 && parsed.Choices[^1] == new AvatarPackageChoice(weaponId, byte.MaxValue),
            "★ 真实91B装扮选择正文完整解析出九项并原样保留武器哨兵");
        var weapon = packages.GetAvatar(weaponId);
        That(!weapon.RequiresAbilityChoice && weapon.Abilities.SequenceEqual(new byte[] { 0 }) && weapon.PendingRule is null,
            "★ 武器装扮没有可选属性声明");
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(packageId), 1,
            shopExpiration: true));
        var character = new Character(1, 0, "Sentinel", 0, towns.Room, towns.InitialReturn) { Job = 0, Inventory = new([source]) };
        var chosen = category.Rows.ToArray();
        AvatarPackageChoice[] Choices(byte weaponAbility) => chosen.Select(row => new AvatarPackageChoice(row.ItemId,
            row.ItemId == weaponId ? weaponAbility : packages.GetAvatar(row.ItemId).Abilities.Min())).ToArray();
        var request = new AvatarPackageOpenRequest(73, 1, 0, 0, chosen.Select(row => row.ItemId).ToArray(), Choices(byte.MaxValue));
        var result = service.Open(character, request, out var receipt, now);
        That(result.Accepted && result.Plan!.After.Items.Count(item => item.Core.Kind == ItemKind.Avatar) == 9 &&
            result.Plan.After.Items.Where(item => item.Core.ItemId == weaponId).All(item => item.Core.Durability == 0),
            "★ 武器装扮的255哨兵按未选择处理，九件完整发放且奖励核心不写入255");
        That(receipt!.Rewards.Sum(row => row.Count) == 9 &&
            result.Plan!.Before.Find(Bag()) is { Core.Count: 1 } && character.Inventory.Find(Bag()) is { Core.Count: 1 },
            "整箱九件只扣一个源，规划前后源数量一致");
        var zero = service.Open(character, request with { Choices = Choices(0) }, out _, now);
        That(zero.Accepted, "哨兵位置写0同样表示未选择，不因来源不同拒绝");
        var bogus = service.Open(character, request with { Choices = Choices(7) }, out _, now);
        That(!bogus.Accepted, "无属性部位收到其他数值仍拒绝，不能借哨兵放宽到任意字节");
        // 有属性声明的部位不受影响，255仍然非法。
        var hat = category.Rows.First(row => packages.GetAvatar(row.ItemId).RequiresAbilityChoice);
        var strict = Choices(byte.MaxValue).Select(choice => choice.ItemId == hat.ItemId
            ? new AvatarPackageChoice(hat.ItemId, byte.MaxValue) : choice).ToArray();
        That(!service.Open(character, request with { Choices = strict }, out _, now).Accepted,
            "有属性声明的部位发送255仍被拒绝");
    }

    static void PeriodDeadlinePlans(TownCatalog towns, ItemResources resources)    {
        const int packageId = 490705387, hatId = 401550176, skinId = 401580026;
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        string package = "[stackable type]\n`[booster selection]` 0\n[booster category num]\n1 1\n[booster selection num]\n0\n" +
            $"[booster select category]\n0 0\n[avatar]\n{hatId} 1 1 0 {skinId} 1 2 0\n[/avatar]\n[/booster select category]";
        string avatar = AvatarText("0 `[HP MAX]` `+` 100");
        var packages = new AvatarPackageCatalog(path => path == "stackable/box.stk" ? package : avatar,
            new Dictionary<int, string> { [hatId] = "hat.equ", [skinId] = "skin.equ" }, new Dictionary<int, string> { [packageId] = "box.stk" });
        var debug = new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = false };
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces, debug);
        var source = new InventoryItem(1000, Bag(), InventoryRewardGrantService.Create(resources.Items.Get(packageId), utcNow: now,
            shopExpiration: true));
        Character character = new(1, 0, "AvatarTime", 0, towns.Room, towns.InitialReturn) { Job = 15, Inventory = new([source]) };
        var request = new AvatarPackageOpenRequest(73, 1, 0, 0, [hatId, skinId], [new(hatId, 0), new(skinId, 0)]);
        var result = service.Open(character, request, out _, now);
        long expires = now.ToUnixTimeSeconds() + 7 * 86400;
        That(result.Accepted && result.Plan!.After.Items.Where(item => item.Avatar is not null).Select(item => item.ExpireTime).Order()
            .SequenceEqual(new[] { (int)expires, (int)(now.ToUnixTimeSeconds() + 30 * 86400) }) &&
            result.Plan.CharacterPrecondition!.Matches(character, expires - 1) && !result.Plan.CharacterPrecondition.Matches(character, expires),
            "160期限类型生成七天和三十天奖励，实际计划绑定全部产物的最早截止");
        debug.IgnoreCashShopAbsoluteExpiration = true;
        result = service.Open(character, request, out _, now);
        That(result.Accepted && result.Plan!.After.Items.Select(item => item.ExpireTime).Order().SequenceEqual(
            new[] { (int)expires, (int)now.AddDays(30).ToUnixTimeSeconds() }), "只忽略绝对日期不改变装扮类型七天/三十天期限");
        debug.IgnoreCashShopUsablePeriod = true;
        int shopExpiry = ItemExpiration.MaximumUnixTime;
        result = service.Open(character, request, out _, now);
        That(result.Accepted && result.Plan!.After.Items.All(item => item.Avatar?.ExpireTime == shopExpiry) &&
            result.Plan.CharacterPrecondition!.Matches(character, shopExpiry - 1L) && !result.Plan.CharacterPrecondition.Matches(character, shopExpiry),
            "独立开启相对忽略后，装扮类型期限使用协议上限及真实提交截止");
        var frozenPackages = new AvatarPackageCatalog(path =>
        {
            debug.IgnoreCashShopAbsoluteExpiration = false;
            debug.IgnoreCashShopUsablePeriod = false;
            return path == "stackable/box.stk" ? package : avatar;
        }, new Dictionary<int, string> { [hatId] = "hat.equ", [skinId] = "skin.equ" }, new Dictionary<int, string> { [packageId] = "box.stk" });
        var frozenService = new InventoryAvatarPackageService(resources.Items, frozenPackages, resources.GoldLimits, resources.EpicPieces, debug);
        result = frozenService.Open(character, request, out _, now);
        That(result.Accepted && !debug.IgnoreCashShopAbsoluteExpiration && result.Plan!.After.Items.All(item => item.Avatar?.ExpireTime == shopExpiry),
            "资源解析期间切换两个GM开关，整次开包仍使用入口冻结的各自状态");
        var filled = Enumerable.Range(0, ItemSlotBoundService.AvatarCapacity).Select(slot => InventoryRewardGrantService.CreateItem(resources.Items.Get(hatId),
            new(InventoryListType.Avatar, (short)slot), InventoryRewardGrantService.Create(resources.Items.Get(hatId))) with
            { Uid = slot + 1, Core = InventoryRewardGrantService.Create(resources.Items.Get(hatId)) with { Value = slot + 1 } }).ToImmutableArray();
        character = character with { Inventory = new([source, .. filled]) };
        result = service.OpenCeraPackage(character, new(73, [new(hatId, 0), new(skinId, 0)]), out _, now);
        That(result.Accepted && result.Plan!.OverflowItems.Length == 2 && result.Plan.OverflowItems.Min(item => item.ExpireTime) == expires &&
            result.Plan.CharacterPrecondition!.Matches(character, expires - 1) && !result.Plan.CharacterPrecondition.Matches(character, expires),
            "518全部溢出到邮件时，原型期限及实际提交前置条件仍包含最早奖励期限");
        debug.IgnoreCashShopAbsoluteExpiration = true;
        debug.IgnoreCashShopUsablePeriod = true;
        result = service.OpenCeraPackage(character, new(73, [new(hatId, 0), new(skinId, 0)]), out _, now);
        That(result.Accepted && result.Plan!.OverflowItems.Length == 2 && result.Plan.OverflowItems.All(item => item.Avatar?.ExpireTime == shopExpiry) &&
            result.Plan.CharacterPrecondition!.Matches(character, shopExpiry - 1L) && !result.Plan.CharacterPrecondition.Matches(character, shopExpiry),
            "518开启相对忽略后覆盖邮件完整详情期限，最早奖励截止不丢失");
        debug.IgnoreCashShopAbsoluteExpiration = false;
        debug.IgnoreCashShopUsablePeriod = false;
        long sourceExpiry = now.ToUnixTimeSeconds() + 1;
        character = character with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)sourceExpiry } }]) };
        result = service.Open(character, request, out _, now);
        That(result.Accepted && result.Plan!.CharacterPrecondition!.Matches(character, sourceExpiry - 1) &&
            !result.Plan.CharacterPrecondition.Matches(character, sourceExpiry) &&
            !service.Open(character, request, out _, DateTimeOffset.FromUnixTimeSeconds(sourceExpiry)).Accepted,
            "源礼盒先到期时，规划后等待和在到期边界重新规划均拒绝");
    }

    static AvatarPackageDefinition Package(string rows, int selection = 0) => AvatarPackageCatalog.Parse(PackageText(rows, selection));
    static string PackageText(string rows, int selection = 0) =>
        "[stackable type]\n`[booster selection]` 0\n[booster category num]\n2 2 4\n[booster selection num]\n" + selection +
        "\n[booster select category]\n1 3\n[avatar]\n" + rows + "\n[/avatar]\n[/booster select category]";
    // 匹配客户端真实CMD160抓包；源前Main13/14确认slot73=490705387。日志属于开发者本地，正文无凭据。
    internal static byte[] CapturedSelection() => Convert.FromHexString(
        "4900010000000000602BEF173152EF171D79EF17E1B5EE17F367EE17EBDCEE17E98EEE171604EF17FA9FEF17" +
        "09602BEF17003152EF17001D79EF1702E1B5EE1702F367EE1713EBDCEE1704E98EEE17001604EF1702FA9FEF170000");
    internal static AvatarPackageChoice[] CapturedChoices() =>
        [new(401550176, 0), new(401560113, 0), new(401570077, 2), new(401520097, 2), new(401500147, 19),
         new(401530091, 4), new(401510121, 0), new(401540118, 2), new(401580026, 0)];
    static string AvatarText(string abilities) => "[avatar type select]\n7 0 0 300 0 0 0 30 0 0 660 0 0 0 0 0 0 1980 0 0 0 " +
        "0 0 0 4500 0 0 2 `[M socket]` `[M socket]`\n[/avatar type select]\n[avatar select ability]\n" + abilities + "\n[/avatar select ability]";
    static InventorySlot Bag() => new(InventoryListType.Main, 73);
    static void Invalid(Action action)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        That(rejected, "不完整或冲突的160装扮选择段拒绝");
    }
    static byte[] Body(int[] ids, AvatarPackageChoice[] choices)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)73); writer.Write(1); writer.Write((byte)1); writer.Write((byte)3);
        foreach (int id in ids) writer.Write(id);
        writer.Write((byte)choices.Length);
        foreach (var choice in choices) { writer.Write(choice.ItemId); writer.Write(choice.Ability); }
        writer.Write((byte)0);
        return stream.ToArray();
    }
}
