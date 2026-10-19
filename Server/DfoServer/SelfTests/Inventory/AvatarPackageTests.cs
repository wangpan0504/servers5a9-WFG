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
        var full = Package("101 1 3 0 102 2 4 37", 0);
        That(full.Recognized, "装扮族身份与当前可用性分别保留");
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
            "0 `[HP MAX]` `*` 100 37 `[MP MAX]` `*` 1.5",
            "0 `[HP MAX]` `+` 1e2 37 `[MP MAX]` `*` 1.5",
            "0 `[HP  MAX]` `+` 100 37 `[MP MAX]` `*` 1.5",
            "0 `[MP MAX]` `+` 100 37 `[MP MAX]` `*` 1.5",
            "37 `[MP MAX]` `*` 1.5 0 `[HP MAX]` `+` 100" })
            That(AvatarPackageCatalog.ParseAvatar(AvatarText(stats) + Block(changed)).PendingRule is not null,
                "能力索引相同仍拒绝数值、操作符、词法表示、字符串内部空白、种类或顺序冲突");
        foreach (string changed in new[] { "140 `[SKILL_LEVEL]` `[mage]` 261 1", "140 `[SKILL_LEVEL]` `[swordman]` 262 1",
            "140 `[SKILL_LEVEL]` `[swordman]` 261 2" })
            That(AvatarPackageCatalog.ParseAvatar(AvatarText(skill) + Block(changed)).PendingRule is not null,
                "重复技能能力声明的职业、技能编号与等级必须全部相同");
        foreach (string text in new[] {
            AvatarText(stats) + Block(""), AvatarText("") + Block(stats),
            AvatarText(stats) + Block("0 `[HP MAX]` `+`"),
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

    public static void RunResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
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
        foreach (var sample in new[] {
            (Id: 490705389, Body: "450001000000000014C7EF170000"),
            (Id: 490705389, Body: "450001000000000015C7EF170000"),
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
            That(!packages.GetAvatar(request.ItemIds[0]).RequiresAbilityChoice, "实际产物没有可选属性声明");
            foreach (var bad in new[] { request with { Choices = [new(request.ItemIds[0], 1)] },
                request with { Choices = [new(1, 0)] }, request with { Category = 255 } })
                That(!service.Open(character, bad, out _, now).Accepted, "省略属性兼容不放行伪造属性、外来编号或非法分类");
        }
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

    static void PeriodDeadlinePlans(TownCatalog towns, ItemResources resources)
    {
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
