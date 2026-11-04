using DfoServer.Game.Characters;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

// #981：保存资源声明的目录与合成领域模型；没有本源真实CMD160字节，不生成抓包oracle。
internal static class CreatureStackableAvatarPackageTests
{
    const int SourceId = 10310044, AvatarId = 101590523;
    const short SourceSlot = 66;
    internal const string Declared = """
[name]
`name_10310044`
[explain]
`explain_10310044`
[flavor text]
`flavor_text_10310044`
[grade]
1
[rarity]
2
[usable job]
`[all]`
[/usable job]

[attach type]
`[trade]`
[minimum level]
1
[icon]
`Item/stackable/cash_2.img` 36
[stackable type]
`[booster selection]` 0
[move wav]
`BONE_TOUCH`
[booster category num]
2 2 0
[booster selection num]
0
[default select auto job and growType]
1
[booster select category]
0 0
[avatar]
101590523 1 4 0
[/avatar]

[equipment]
100331839 1
[/equipment]

[creature]
100990228 1
[/creature]

[stackable]
10309981 1 10310266 1
[/stackable]

[/booster select category]

[booster select category]
1 0
[avatar]
101590523 1 4 0
[/avatar]

[equipment]
100331840 1
[/equipment]

[creature]
100990229 1
[/creature]

[stackable]
10309982 1 10310048 3
[/stackable]

[/booster select category]

[booster category name]
`booster_category_starter_0` `booster_category_starter_1` `booster_category_starter_2`
[/booster category name]
""";

    static int[] Ids(byte category) => category == 0
        ? [AvatarId, 100331839, 100990228, 10309981, 10310266]
        : [AvatarId, 100331840, 100990229, 10309982, 10310048];

    public static void RunCore()
    {
        var definition = AvatarPackageCatalog.Parse(Declared);
        That(definition.Available && definition.SelectionCount == 0 && definition.Dimensions.SequenceEqual(new[] { 2, 1 }) &&
            definition.Categories.Length == 2, "#981原两分类/selection0保持，不把职业提示变成准入或自动选择");
        for (byte key = 0; key < 2; key++)
        {
            int[] ids = Ids(key);
            var category = definition.Categories.Single(row => row.Category == key && row.Subcategory == 0);
            That(category.PendingRule is null && category.RowCount == 5 &&
                category.Rows.SequenceEqual(new[] { new AvatarPackageRow(AvatarId, 1, 4, 0) }) &&
                category.EquipmentRows.SequenceEqual(new[] { new AvatarPackageEquipmentRow(ids[1], 1) }) && category.MaterialRows.IsEmpty &&
                category.CreatureRows.SequenceEqual(new[] { new AvatarPackageCreatureRow(ids[2], 1) }) &&
                category.StackableRows.SequenceEqual(new[] { new AvatarPackageStackableRow(ids[3], 1), new AvatarPackageStackableRow(ids[4], (ushort)(key == 0 ? 1 : 3)) }),
                "#981完整分类" + key + "/0四typed集合保五行、原数量/type4/default0，不丢宠物/子盒/宝珠");
            That(InventoryAvatarPackageService.TrySelect(definition, category, ids,
                out var avatars, out var equipment, out var materials, out var creatures, out var stackables) &&
                avatars.Length == 1 && equipment.Length == 1 && materials.Length == 0 && creatures.Length == 1 && stackables.Length == 2,
                "#981最全领域选择返回全部五行，数量不是ID向量重复次数");
            That(!InventoryAvatarPackageService.TrySelect(definition, category, ids, out _) &&
                !InventoryAvatarPackageService.TrySelect(definition, category, ids, out _, out _) &&
                !InventoryAvatarPackageService.TrySelect(definition, category, ids, out _, out _, out _),
                "#981三个旧输出重载整拒新增集合，不能静默漏发");
            foreach (var partial in Enumerable.Range(0, ids.Length).Select(index => ids.Where((_, at) => at != index).ToArray())
                .Concat(new[] { ids.Concat(new[] { SourceId }).ToArray(), ids.Concat(new[] { ids[0] }).ToArray(), new[] { AvatarId } }))
                That(!InventoryAvatarPackageService.TrySelect(definition, category, partial, out _, out _, out _, out _, out _),
                    "#981缺任一行、额外ID、重复或纯装扮子集均整拒");
        }
        foreach (string invalid in new[]
        {
            Declared.Replace("100990228 1", "100990228"), Declared.Replace("100990228 1", "100990228 0"),
            Declared.Replace("100990228 1", ""),
            Declared.Replace("100990228 1", "-100990228 1"), Declared.Replace("100990228 1", "100990228 65536"),
            Declared.Replace("100990228 1", "bad 1"), Declared.Replace("100990228 1", "100990228 -1"),
            Declared.Replace("100990228 1", "100990228 1 100990228 1"),
            Declared.Replace("100990228 1", "101590523 1"), Declared.Replace("100990228 1", "100331839 1"),
            Declared.Replace("100990228 1", "10309981 1"), Declared.Replace("[/creature]", ""),
            Declared.Replace("[/creature]", "[/equipment]"),
            Declared.Replace("10309981 1 10310266 1", ""), Declared.Replace("10309981 1 10310266 1", "10309981"),
            Declared.Replace("10309981 1 10310266 1", "10309981 0"),
            Declared.Replace("10309981 1 10310266 1", "10309981 -1"),
            Declared.Replace("10309981 1 10310266 1", "-10309981 1"),
            Declared.Replace("10309981 1 10310266 1", "10309981 65536"),
            Declared.Replace("10309981 1 10310266 1", "10309981 1 10309981 1"),
            Declared.Replace("10309981 1 10310266 1", "101590523 1"),
            Declared.Replace("10309981 1 10310266 1", "100331839 1"),
            Declared.Replace("10309981 1 10310266 1", "100990228 1"), Declared.Replace("[/stackable]", ""),
            Declared.Replace("[/stackable]", "[/creature]"),
            Declared.ReplaceLineEndings("\n").Replace("[booster selection num]\n0", "[booster selection num]\n1")
        })
        {
            That(invalid != Declared, "#981每项非法声明均确实变更独立输入，不用未替换原文制造伪负例");
            var denied = AvatarPackageCatalog.Parse(invalid);
            That(!denied.Available || denied.Categories.Any(category => category.PendingRule is not null),
                "#981新typed块空/残缺/非法参数/未闭合/重复及跨Kind冲突拒绝，不靠实际模板修复语法");
        }
    }

    public static void RunResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 45, 0, TimeSpan.Zero);
        var sourceTemplate = resources.Items.Get(SourceId);
        That(sourceTemplate.Kind == ItemKind.Consumable && sourceTemplate.MinimumLevel == 1 && sourceTemplate.StackLimit >= 1,
            "#981真实源为all/min1普通消耗品，实例仅创建1份，不擅自宣称源上限1或制造多开2份");
        var source = new InventoryItem(981001, new(InventoryListType.Main, SourceSlot),
            InventoryRewardGrantService.Create(sourceTemplate, 1, utcNow: now));
        var sentinel = source with { Uid = 981002, Position = new(InventoryListType.Main, 67) };
        var before = new InventorySnapshot([source, sentinel], Gold: 321) { ReviveCoins = 7, WinPoints = 11 };
        var role = new Character(1, 0, "TypedDomain", 0, towns.Room, towns.InitialReturn) { Job = 0, Level = 4, Inventory = before };
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var definition = packages.Get(SourceId);
        That(definition.Available && definition.Categories.Length == 2 && definition.Dimensions.SequenceEqual(new[] { 2, 1 }),
            "#981正常资源入口保完整两分类；以下Open请求是直接领域对象，不能记为客户端请求通过");
        for (byte key = 0; key < 2; key++)
        {
            var request = DomainRequest(key);
            int[] ids = Ids(key);
            string expectedPetName = resources.Items.Get(ids[2]).CreatureName ??
                throw new InvalidDataException("typed PVF creature has no name");
            foreach (int id in ids)
            {
                var template = resources.Items.Get(id);
                That(CurrencyWallet.RewardKind(id) is null && !resources.EpicPieces.ByPiece.ContainsKey(id) &&
                    !template.IsPremiumService && !template.HasAcquisitionEffect && template.Expiration.SupportsCreation,
                    "#981五奖逐一核普通实物owner和期限创建能力，不走钱包/碎片/即时服务 " + id);
            }
            That(resources.Items.Get(ids[2]) is { Kind: ItemKind.Creature, CreatureEgg: false, CreatureSubType: 0, CreatureInitialMode: 0 } &&
                resources.Items.Get(ids[2]).CreatureName == expectedPetName &&
                resources.Items.Get(ids[3]).Kind == ItemKind.Consumable && resources.Items.Get(ids[4]).Kind == ItemKind.Consumable &&
                packages.GetAvatar(AvatarId) is { PendingRule: null, RequiresAbilityChoice: false, Types.IsEmpty: true },
                "#981实际宠物/宝珠/子盒类型已正常装配，species名字不回退成装备name_ID或空宠物");
            var result = Accepted(role, request, service, "#981真实资源分类" + key + "/0的合成领域五奖规划");
            var after = result.Plan!.After;
            That(after.Items.Length == 6 && after.Items.All(item => item.Uid != source.Uid) && after.Find(sentinel.Position) == sentinel &&
                after.Items.Where(item => item != sentinel).All(item => item.Uid == 0 && item.ExpireTime == 0),
                "#981源单次耗尽、另一同ID哨兵保留、五奖原型无伪UID且原永久期限完整保留");
            var aura = after.Items.Single(item => item.Core.ItemId == AvatarId);
            var pet = after.Items.Single(item => item.Core.ItemId == ids[2]);
            var title = after.Items.Single(item => item.Core.ItemId == ids[1]);
            That(aura is { Position.List: InventoryListType.Avatar, Core.Kind: ItemKind.Avatar, Core.Durability: 0, Core.Count: 1,
                Avatar: { Socket0: { Type: 239, EmblemId: -1 }, Socket1: { Type: 239, EmblemId: -1 }, Socket2.Type: 0, Socket3.Type: 0, Socket4.Type: 0 } } &&
                pet is { Position.List: InventoryListType.Pet, Core.Kind: ItemKind.Creature, Core.Count: 1, Core.ExpireTime: 0,
                    Creature: { Level: 1, Experience: 0, Satiety: 100, Mode: 0, ExpireTime: 0 } } && pet.Creature.Name == expectedPetName &&
                title.Core == InventoryRewardGrantService.Create(resources.Items.Get(ids[1]), utcNow: now) &&
                title.Core.Kind == ItemKind.Equipment && ItemSlotBoundService.Allows(before, title.Position, ItemKind.Equipment),
                "#981光环永久双M/type4/default0、宠物完整初始详情与称号核心/对应容器俱全，不手造50级宠物");
            pet.Creature!.Validate();
            foreach (int id in ids.Skip(3))
            {
                var item = after.Items.Single(row => row.Core.ItemId == id);
                That(item.Core.Kind == ItemKind.Consumable && item.Core.Count == (key == 1 && id == 10310048 ? 3 : 1) &&
                    ItemSlotBoundService.Allows(before, item.Position, ItemKind.Consumable) && item.Avatar is null && item.Creature is null,
                    "#981宝珠/子盒保原1或3数量与普通Main消耗品页，不递归代开 " + id);
            }
            foreach (var kind in new[] { ItemKind.Avatar, ItemKind.Equipment, ItemKind.Creature })
            {
                int itemId = kind == ItemKind.Avatar ? AvatarId : kind == ItemKind.Equipment ? ids[1] : ids[2];
                Rejected(role with { Inventory = before with { Items = [source, sentinel, .. Fill(kind, itemId)] } }, request, service, 4,
                    "#981单独满" + kind + "页整拒，不能仅发其余四奖或借Main源腾格");
            }
            foreach (var invalid in new[]
            {
                request with { ItemIds = ids[..^1] }, request with { ItemIds = [.. ids, SourceId] },
                request with { ItemIds = [AvatarId, ids[1], ids[1], ids[3], ids[4]] },
                request with { ItemIds = Ids((byte)(1-key)) }, request with { Category = 2 }, request with { Subcategory = 1 },
                request with { Choices = [new(ids[2], 0)] }, request with { Choices = [new(ids[3], 0)] },
                request with { Choices = [new(AvatarId, 1)] }, request with { Count = 0 }, request with { Count = 2 }
            }) Rejected(role, invalid, service, 19, "#981错集合/分类/能力或超过实际源1份整拒");
            foreach (var choices in new CeraPackageChoice[][] { [], [new(AvatarId, 0)] })
            {
                var denied = service.OpenCeraPackage(role, new(SourceSlot, choices), out var receipt, now);
                That(!denied.Accepted && denied.Plan is null && receipt is null && ReferenceEquals(role.Inventory, before),
                    "#981518不能发纯装扮后漏宠物/普通奖励");
            }
        }
        foreach (var deniedSource in new[]
        {
            source with { Core = source.Core with { EquipmentLockId = 1 } },
            source with { Core = source.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } }
        }) Rejected(role with { Inventory = before with { Items = [deniedSource, sentinel] } }, DomainRequest(0), service, 19,
            "#981原源锁定或到期，不消费也不造任一奖励");
        Rejected(role with { Level = 0 }, DomainRequest(0), service, 19, "#981保原min1源资格");
        for (byte job = 0; job < 16; job++)
            Accepted(role with { Job = job }, DomainRequest(0), service, "#981源all保职业" + job + "，不反用奖励穿戴职业收窄开盒");

        // 分类1的两个宝珠都是实际StackLimit1000；填满消耗品页，不使用新的placer脚手架。
        var consumableRange = ItemSlotBoundService.Range(ItemKind.Consumable, before);
        int[] freeSlots = Enumerable.Range(consumableRange.Start, consumableRange.End-consumableRange.Start+1)
            .Where(slot => slot != SourceSlot && slot != sentinel.Position.Index).ToArray();
        var full = freeSlots.Select(slot => source with { Uid = 982000 + slot, Position = new(InventoryListType.Main, (short)slot) }).ToArray();
        That(resources.Items.Get(10309982).StackLimit == 1000 && resources.Items.Get(10310048).StackLimit == 1000 && full.Length >= 3,
            "#981分类1实际两宝珠有限上限1000，首兼容堆场景合法");
        var first = Stack(10309982, freeSlots[0], 999, 983001);
        var second = Stack(10310048, freeSlots[1], 997, 983002);
        var stackedRole = role with { Inventory = before with { Items = [source, sentinel, first, second, .. full.Skip(2)] } };
        var stacked = Accepted(stackedRole, DomainRequest(1), service, "#981满消耗品页首兼容堆各恰余1和3，全五奖仍完整成功");
        That(stacked.Plan!.After.Find(first.Position) is { Uid: 983001, Core.Count: 1000 } &&
            stacked.Plan.After.Find(second.Position) is { Uid: 983002, Core.Count: 1000 } &&
            stacked.Plan.After.Items.Count(item => item.Core.ItemId == 10309982) == 1 &&
            stacked.Plan.After.Items.Count(item => item.Core.ItemId == 10310048) == 1,
            "#981堆叠保原UID/位置，仅加原数量，不造额外堆或修改首兼容规则");
        var tooSmall = second with { Core = second.Core with { Value = 998 } };
        var later = Stack(10310048, freeSlots[2], 997, 983003);
        Rejected(role with { Inventory = before with { Items = [source, sentinel, first, tooSmall, later, .. full.Skip(3)] } },
            DomainRequest(1), service, 4, "#981首兼容堆余2不够3，即使后堆够或源腾格仍整拒，不跨堆或退空槽");
        Rejected(role with { Inventory = before with { Items = [source, sentinel, .. full] } }, DomainRequest(1), service, 4,
            "#981满页仅因扣源腾一格而需两个新普通奖堆时整拒，不吞最后一奖");
        var exactRole = role with { Inventory = before with { Items = [source, sentinel,
            .. Fill(ItemKind.Avatar, AvatarId, true), .. Fill(ItemKind.Equipment, 100331840, true),
            .. Fill(ItemKind.Creature, 100990229, true), .. full.Take(full.Length-1)] } };
        Accepted(exactRole, DomainRequest(1), service,
            "#981Avatar/Main装备/Pet各恰剩一格、扣源后Main消耗品恰两格时全五奖成功，不虚增所需容量");

        // 合成模板只用于验证stackable声明按实际Kind使用材料页。
        var materialTemplate = resources.Items.Get(10309981) with { Kind = ItemKind.Material };
        var materialService = new InventoryAvatarPackageService(
            IsolatedItems(materialTemplate), packages, resources.GoldLimits, resources.EpicPieces);
        var materialResult = Accepted(role, DomainRequest(0), materialService,
            "#981合成Material按材料页与四个原奖进入同一计划");
        var materialAfter = materialResult.Plan!.After;
        var materialReward = materialAfter.Items.Single(item => item.Core.ItemId == 10309981);
        That(materialReward.Core.Kind == ItemKind.Material && materialReward.Core.Count == 1 &&
            ItemSlotBoundService.Allows(before, materialReward.Position, ItemKind.Material) &&
            materialAfter.Items.All(item => item.Uid != source.Uid) &&
            materialAfter.Find(sentinel.Position) == sentinel,
            "#981合成材料只发一份到材料页，源扣一且哨兵保持");
        var specialMaterial = materialTemplate with { IsPremiumService = true };
        Rejected(role, DomainRequest(0),
            new InventoryAvatarPackageService(IsolatedItems(specialMaterial), packages,
                resources.GoldLimits, resources.EpicPieces), 19,
            "#981合成Material特殊owner仍整拒并保源");

        string originalDeclaration = Declared.ReplaceLineEndings("\n");
        string mixedDeclaration = originalDeclaration.Replace(
            "[stackable]\n10309981 1 10310266 1\n[/stackable]",
            "[stackable]\n10309981 1\n[/stackable]\n[etc]\n10310266 1\n[/etc]");
        That(mixedDeclaration != originalDeclaration, "合成混合声明确实替换原分类");
        var mixedPackages = new AvatarPackageCatalog(path => path switch
        {
            "stackable/source.stk" => mixedDeclaration,
            "equipment/avatar.equ" => "",
            _ => throw new InvalidDataException("unexpected synthetic path: " + path)
        }, new Dictionary<int, string> { [AvatarId] = "avatar.equ" },
            new Dictionary<int, string> { [SourceId] = "source.stk" });
        int[] mixedIds = [SourceId, AvatarId, 100331839, 100331840, 100990228, 100990229,
            10309981, 10309982, 10310266, 10310048];
        var mixedItems = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        mixedItems.PrefillTemplates(mixedIds.ToDictionary(id => id, id => id is 10309981 or 10310266
            ? resources.Items.Get(id) with { Kind = ItemKind.Material, StackLimit = 100 }
            : resources.Items.Get(id)));
        var mixedService = new InventoryAvatarPackageService(mixedItems, mixedPackages, resources.GoldLimits, resources.EpicPieces);
        InventoryItem MaterialStack(int id, short slot, int amount, long uid) => new(uid,
            new(InventoryListType.Main, slot), InventoryRewardGrantService.Create(mixedItems.Get(id), amount, utcNow: now));
        var firstMaterial = MaterialStack(10309981, 121, 99, 987001);
        var secondMaterial = MaterialStack(10310266, 122, 99, 987002);
        var materialFill = Fill(ItemKind.Material, 3330);
        var mixedRole = role with { Inventory = before with { Items = [source, sentinel, firstMaterial,
            secondMaterial, .. materialFill.Skip(2)] } };
        var mixed = Accepted(mixedRole, DomainRequest(0), mixedService, "#981两种材料同满页各合入自身首兼容堆");
        That(mixed.Plan!.After.Find(firstMaterial.Position) is { Uid: 987001, Core.Count: 100 } &&
            mixed.Plan.After.Find(secondMaterial.Position) is { Uid: 987002, Core.Count: 100 },
            "#981两个不同材料ID各保UID且分别加一");
        var fullFirst = firstMaterial with { Core = firstMaterial.Core with { Value = 100 } };
        var laterFirst = MaterialStack(10309981, 123, 99, 987003);
        var blockedRole = role with { Inventory = before with { Items = [source, sentinel, fullFirst,
            secondMaterial, laterFirst, .. materialFill.Skip(3)] } };
        Rejected(blockedRole, DomainRequest(0), mixedService, 4,
            "#981材料页满且首兼容堆不足时即使后堆够仍整拒");

        // 合成五奖声明只替换一行，印章模板与数量来自已保存真实叶和父声明。
        var stampTemplate = resources.Items.Get(10092947);
        That(stampTemplate is { Id: 10092947, Kind: ItemKind.Material, StackLimit: int.MaxValue,
                IsPremiumService: false, HasAcquisitionEffect: false } &&
            stampTemplate.Expiration.SupportsCreation && CurrencyWallet.RewardKind(10092947) is null &&
            !resources.EpicPieces.ByPiece.ContainsKey(10092947),
            "#981真实印章为可创建实体材料，上限及特殊owner符合保存叶");
        string stampDeclaration = originalDeclaration.Replace("10309981 1 10310266 1", "10092947 1000 10310266 1");
        That(stampDeclaration != originalDeclaration, "#981合成声明确实替换一个奖励ID及数量");
        var stampPackages = new AvatarPackageCatalog(path => path switch
        {
            "stackable/source.stk" => stampDeclaration,
            "equipment/avatar.equ" => "",
            _ => throw new InvalidDataException("unexpected synthetic path: " + path)
        }, new Dictionary<int, string> { [AvatarId] = "avatar.equ" },
            new Dictionary<int, string> { [SourceId] = "source.stk" });
        var stampCategory = stampPackages.Get(SourceId).Categories.Single(row => row.Category == 0 && row.Subcategory == 0);
        That(stampCategory.PendingRule is null && stampCategory.StackableRows.SequenceEqual(new[]
        {
            new AvatarPackageStackableRow(10092947, 1000), new AvatarPackageStackableRow(10310266, 1)
        }), "#981合成分类保留真实印章1000和原子盒1");
        int[] stampIds = [AvatarId, 100331839, 100990228, 10092947, 10310266];
        var stampRequest = DomainRequest(0) with { ItemIds = stampIds };
        var stampService = new InventoryAvatarPackageService(resources.Items, stampPackages, resources.GoldLimits, resources.EpicPieces);
        var stampResult = stampService.Open(role, stampRequest, out var stampReceipt, now);
        That(stampResult.Accepted && stampResult.Plan is not null && stampReceipt is { Rewards.Length: 5 },
            "#981真实印章及四个原奖同一合成领域计划 error=" + stampResult.Error);
        var stampPlan = stampResult.Plan ?? throw new InvalidDataException("accepted without stamp plan");
        var stampAfter = stampPlan.After;
        var stamp = stampAfter.Items.Single(item => item.Core.ItemId == 10092947);
        That(before.Expansion == 24 && stamp.Core.Kind == ItemKind.Material && stamp.Core.Count == 1000 &&
            stamp.Position.List == InventoryListType.Main && stamp.Position.Index is >= 121 and <= 176,
            "#981真实印章原1000数量进入该fixture合法材料页");
        var expectedStampRewards = new[]
        {
            new BoosterGrantedItem(AvatarId, 1), new BoosterGrantedItem(100331839, 1),
            new BoosterGrantedItem(100990228, 1), new BoosterGrantedItem(10092947, 1000),
            new BoosterGrantedItem(10310266, 1)
        };
        That(ReferenceEquals(role.Inventory, before) && ReferenceEquals(stampPlan.Before, before) &&
            stampPlan.OverflowItems.IsEmpty && stampAfter.Items.Length == 6 &&
            stampAfter.Items.All(item => item.Uid != source.Uid) && stampAfter.Find(sentinel.Position) == sentinel &&
            stampAfter.Items.Select(item => item.Core.ItemId).Order().SequenceEqual(stampIds.Append(SourceId).Order()) &&
            stampAfter.Items.Where(item => item.Core.ItemId != 10092947).All(item => item.Core.Count == 1) &&
            Equals(stampAfter with { Items = before.Items }, before) &&
            stampReceipt!.Rewards.OrderBy(item => item.ItemId).SequenceEqual(expectedStampRewards.OrderBy(item => item.ItemId)),
            "#981扣一个源、保同ID哨兵及全部非物品字段，完整五奖原量回执");

        foreach (var changed in new[]
        {
            resources.Items.Get(100990228) with { Kind = ItemKind.Equipment },
            resources.Items.Get(100990228) with { CreatureName = null },
            resources.Items.Get(100990228) with { EquipmentSlot = 0 },
            resources.Items.Get(100990228) with { CreatureInitialMode = 2 },
            resources.Items.Get(100990228) with { IsPremiumService = true },
            resources.Items.Get(100990228) with { PersonalCargoUpgradeTarget = 1 },
            resources.Items.Get(10309981) with { Kind = ItemKind.Equipment },
            resources.Items.Get(10309981) with { IsPremiumService = true },
            resources.Items.Get(10309981) with { StackLimit = 0 },
            resources.Items.Get(10309981) with { PersonalCargoUpgradeTarget = 1 },
            resources.Items.Get(10309981) with { Expiration = new(DailyDelete: 2) }
        })
        {
            var isolated = IsolatedItems(changed);
            var deniedService = new InventoryAvatarPackageService(isolated, packages, resources.GoldLimits, resources.EpicPieces);
            Rejected(role, DomainRequest(0), deniedService, 19, "#981仅独立克隆模板的错Kind/未装配宠物/特殊owner/未知期限整拒 " + changed.Id);
        }
        var limited = IsolatedItems(resources.Items.Get(10310048) with { StackLimit = 2 });
        Rejected(role, DomainRequest(1), new(limited, packages, resources.GoldLimits, resources.EpicPieces), 4,
            "#981仅合成目标上限2，实际资源数量3整拒，不拆成多个堆绕单条上限");
        var finitePet = IsolatedItems(resources.Items.Get(100990228) with { Expiration = new(Days: 1) });
        var finiteSource = source with { Core = source.Core with { ExpireTime = checked((int)now.AddDays(2).ToUnixTimeSeconds()) } };
        var finiteRole = role with { Inventory = before with { Items = [finiteSource, sentinel] } };
        var finite = Accepted(finiteRole, DomainRequest(0), new(finitePet, packages, resources.GoldLimits, resources.EpicPieces),
            "#981合成宠物1天/源2天复用同一完整领域计划");
        long deadline = now.AddDays(1).ToUnixTimeSeconds();
        var finitePlan = finite.Plan!;
        var finitePrecondition = finitePlan.CharacterPrecondition;
        That(finitePlan.After.Items.Single(item => item.Core.ItemId == 100990228) is { Core.ExpireTime: 0, Creature: not null } petWithDeadline &&
            petWithDeadline.ExpireTime == deadline && finitePrecondition is not null && finitePrecondition.Matches(finiteRole, deadline-1) &&
            !finitePrecondition.Matches(finiteRole, deadline),
            "#981宠物详情期限参与全五奖最早提交门，Core为0不等于永久；到期等号拒旧规划");
        var ordinary = resources.Items.Get(10309981);
        That(InventoryAvatarPackageService.StackableRowDefinitionError(new(1, 1), ordinary with { Id = 1 }, resources.EpicPieces) is not null,
            "#981即使合成模板谎称普通消耗品，已知复活币ID仍由钱包owner保护拒绝");
        That(resources.EpicPieces.ByPiece.Count != 0, "#981普通已装配碎片映射存在，直接复用不用加载新增物品叶");
        int pieceId = resources.EpicPieces.ByPiece.Keys.Min();
        That(InventoryAvatarPackageService.StackableRowDefinitionError(new(pieceId, 1), ordinary with { Id = pieceId }, resources.EpicPieces) is not null,
            "#981现有碎片映射owner不按普通stackable直接发实物");

        InventoryResult Accepted(Character actor, AvatarPackageOpenRequest request, InventoryAvatarPackageService use, string label)
        {
            var original = actor.Inventory;
            var result = use.Open(actor, request, out var receipt, now);
            That(result.Accepted && result.Plan is not null && receipt is { Rewards.Length: 5 }, label + " error=" + result.Error);
            var plan = result.Plan ?? throw new InvalidDataException("accepted without plan");
            var expected = Ids(request.Category).Select((id, index) => new BoosterGrantedItem(id, request.Category == 1 && index == 4 ? 3 : 1));
            That(ReferenceEquals(actor.Inventory, original) && ReferenceEquals(plan.Before, original) &&
                plan.OverflowItems.IsEmpty && plan.After.Find(sentinel.Position) == sentinel &&
                plan.After.Gold == original.Gold && plan.After.ReviveCoins == original.ReviveCoins &&
                plan.After.WinPoints == original.WinPoints && Equals(plan.After.AccountWallet, original.AccountWallet) &&
                Equals(plan.After.EpicPieces, original.EpicPieces) &&
                receipt!.Rewards.OrderBy(row => row.ItemId).SequenceEqual(expected.OrderBy(row => row.ItemId)),
                label + "：一个before/Plan/完整五奖回执，哨兵和所有钱包owner不变，提交前角色不变");
            return result;
        }
        void Rejected(Character actor, AvatarPackageOpenRequest request, InventoryAvatarPackageService use, ushort error, string label)
        {
            var original = actor.Inventory;
            var result = use.Open(actor, request, out var receipt, now);
            That(!result.Accepted && result.Error == error && result.Plan is null && receipt is null && ReferenceEquals(actor.Inventory, original),
                label + " error=" + result.Error);
        }
        InventoryItem[] Fill(ItemKind kind, int id, bool leaveLast = false)
        {
            var template = resources.Items.Get(id);
            var range = ItemSlotBoundService.Range(kind, before);
            return Enumerable.Range(range.Start, range.End-range.Start+1-(leaveLast ? 1 : 0)).Select(index =>
            {
                long uid = 984000 + (int)kind*1000 + index;
                var item = InventoryRewardGrantService.CreateItem(template, new(ItemSlotBoundService.List(kind), (short)index),
                    InventoryRewardGrantService.Create(template, utcNow: now));
                return item with { Uid = uid, Core = kind is ItemKind.Avatar or ItemKind.Creature ? item.Core with { Value = checked((int)uid) } : item.Core };
            }).ToArray();
        }
        InventoryItem Stack(int id, int slot, int count, long uid) => new(uid, new(InventoryListType.Main, (short)slot),
            InventoryRewardGrantService.Create(resources.Items.Get(id), count, utcNow: now));
        ItemCatalog IsolatedItems(ItemTemplate changed)
        {
            int[] all = [SourceId, AvatarId, 100331839, 100331840, 100990228, 100990229, 10309981, 10309982, 10310266, 10310048];
            var isolated = new ItemCatalog(null!, new Dictionary<int,string>(), new Dictionary<int,string>(), 0);
            isolated.PrefillTemplates(all.ToDictionary(id => id, id => id == changed.Id ? changed : resources.Items.Get(id)));
            return isolated;
        }
    }
    public static void RunPersistence(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-creature-stackable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool complete = false;
        try
        {
            for (byte category = 0; category < 2; category++) RunCategory(category);
            complete = true;
        }
        finally
        {
            if (complete) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.WriteLine("#981 persistence failure retained: " + root);
        }
        Console.WriteLine($"PASS {Count - checks} #981直接领域/Session/Repository两分类事务与重建恢复断言（无网络请求）");

        void RunCategory(byte category)
        {
            string directory = Path.Combine(root, "category-" + category);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("typed-package-" + category, () => { });
            var now = DateTimeOffset.UtcNow;
            var sourceCore = InventoryRewardGrantService.Create(resources.Items.Get(SourceId), 1, utcNow: now);
            repository.Create(owner, new("TypedSql" + category, towns.Room, towns.InitialReturn)
            {
                Job = 0, Level = 4,
                Inventory = new([new(0, new(InventoryListType.Main, SourceSlot), sourceCore),
                    new(0, new(InventoryListType.Main, 67), sourceCore)], Gold: 321)
            });
            var session = new GameSession(repository, towns, owner);
            session.Select(0); session.MarkReady();
            var saved = session.Selected!;
            var source = saved.Inventory.Find(new(InventoryListType.Main, SourceSlot))!;
            var sentinel = saved.Inventory.Find(new(InventoryListType.Main, 67))!;
            That(source.Uid > 0 && sentinel.Uid > 0 && source.Uid != sentinel.Uid,
                "#981隔离库正常Repository种源与哨兵，先取得真实UID");
            int[] ids = Ids(category);
            string idSet = string.Join(",", ids);
            string expectedPetName = resources.Items.Get(ids[2]).CreatureName ??
                throw new InvalidDataException("typed PVF creature has no name");
            var request = DomainRequest(category);
            var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
            var planned = service.Open(saved, request, out var plannedReceipt, now);
            That(planned.Accepted && planned.Plan is not null && plannedReceipt is { Rewards.Length: 5 },
                "#981持久源的完整五奖直接领域规划，非CMD160字节");
            var frozenPlan = planned.Plan!;
            string before = State(owner);

            // 所有新核心、两种详情、五个真实owner槽与源删除均完成后，才在审计末段制造故障。
            Sql($"""
                CREATE TRIGGER fail_typed_package_late BEFORE INSERT ON inventory_audit_log
                WHEN NEW.action_name='avatar_package_open' BEGIN
                  SELECT CASE WHEN
                    (SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId') IN ({idSet}))=5 AND
                    (SELECT count(*) FROM character_inventory_items WHERE item_uid={source.Uid})=0 AND
                    (SELECT count(*) FROM inventory_avatar_details a JOIN character_inventory_items i USING(item_uid)
                     WHERE json_extract(i.item_core,'$.ItemId')={AvatarId} AND json_extract(i.item_core,'$.Value')=i.item_uid)=1 AND
                    (SELECT count(*) FROM inventory_creature_details p JOIN character_inventory_items i USING(item_uid)
                     WHERE json_extract(i.item_core,'$.ItemId')={ids[2]} AND json_extract(i.item_core,'$.Value')=i.item_uid AND
                       json_extract(p.detail,'$.Name')='{expectedPetName}')=1 AND
                    (SELECT count(*) FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                     WHERE (s.list_type=1 AND json_extract(i.item_core,'$.ItemId')={AvatarId}) OR
                       (s.list_type=7 AND json_extract(i.item_core,'$.ItemId')={ids[2]}) OR
                       (s.list_type=0 AND json_extract(i.item_core,'$.ItemId') IN ({ids[1]},{ids[3]},{ids[4]})))=5
                  THEN RAISE(ABORT,'981 late rollback')
                  ELSE RAISE(ABORT,'981 late prerequisite missing') END;
                END
                """);
            bool lateFailure = false;
            string failure = "no exception";
            try { OpenSession(); }
            catch (SqliteException error)
            {
                failure = error.Message;
                lateFailure = error.SqliteErrorCode == 19 && error.Message.Contains("981 late rollback", StringComparison.Ordinal);
            }
            That(lateFailure && ReferenceEquals(session.Selected, saved) && State(owner) == before,
                "#981五奖核心/详情/owner槽写完后的指定晚审计故障，源/revision/UID序列/钱包/全部资产原子回滚：" + failure);
            Sql("DROP TRIGGER fail_typed_package_late");

            BoosterReceipt? receipt = null;
            var accepted = session.PlanAndCommitBooster(role => service.Open(role, request, out receipt, now),
                resources.Boosters.UsePlaces(SourceId), SourceId);
            That(accepted.Accepted && accepted.Receipt is not null && receipt is { Rewards.Length: 5 } &&
                ReferenceEquals(accepted.Receipt.Character, session.Selected),
                "#981移除故障后完整五奖只发布已提交Session回执，直接领域不是网络ACK");
            var committed = session.Selected!;
            var actual = committed.Inventory;
            var expectedReceipt = ids.Select((id, index) => new BoosterGrantedItem(id, category == 1 && index == 4 ? 3 : 1));
            That(committed.Revision == saved.Revision + 1 && actual.Items.Length == 6 && actual.Items.All(item => item.Uid != source.Uid) &&
                actual.Find(sentinel.Position) == sentinel && actual.Items.All(item => item.Uid > 0) &&
                actual.Items.Select(item => item.Uid).Distinct().Count() == actual.Items.Length &&
                receipt!.Rewards.OrderBy(item => item.ItemId).SequenceEqual(expectedReceipt.OrderBy(item => item.ItemId)) &&
                actual.Gold == saved.Inventory.Gold && actual.ReviveCoins == saved.Inventory.ReviveCoins &&
                actual.WinPoints == saved.Inventory.WinPoints &&
                JsonSerializer.Serialize(actual.AccountWallet) == JsonSerializer.Serialize(saved.Inventory.AccountWallet) &&
                JsonSerializer.Serialize(actual.EpicPieces) == JsonSerializer.Serialize(saved.Inventory.EpicPieces),
                "#981一次revision、源UID只耗一次、五奖正且唯一UID/原数量、哨兵与所有钱包owner保持");
            foreach (int id in ids)
            {
                var expected = frozenPlan.After.Items.Single(item => item.Core.ItemId == id);
                var reward = actual.Items.Single(item => item.Core.ItemId == id);
                var expectedCore = expected.Avatar is not null || expected.Creature is not null
                    ? expected.Core with { Value = checked((int)reward.Uid) } : expected.Core;
                That(reward.Core == expectedCore && reward.Position == expected.Position &&
                    JsonSerializer.Serialize(reward.Avatar) == JsonSerializer.Serialize(expected.Avatar) &&
                    JsonSerializer.Serialize(reward.Creature) == JsonSerializer.Serialize(expected.Creature),
                    "#981提交只给新实例分配UID，完整核心、真实owner槽、Avatar/Pet详情和期限均原样保存 " + id);
                reward.ValidateDetails(allocating: false);
            }
            That(actual.Items.Single(item => item.Core.ItemId == ids[2]) is
                { Core.ExpireTime: 0, Creature: { Name: not null, Level: 1, Experience: 0, Satiety: 100, Mode: 0, ExpireTime: 0 } } pet &&
                pet.Creature.Name == expectedPetName,
                "#981两种species各自真实初始名字/详情完整，不用空Pet或重建50级替代");

            string persisted = State(owner);
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, saved, frozenPlan),
                "#981已提交后旧revision冻结计划不可重复发奖");
            That(State(owner) == persisted, "#981旧revision拒绝不改UID、revision、核心、详情、槽及审计");
            var repeated = OpenSession();
            That(!repeated.Accepted && repeated.Error == 19 && repeated.Plan is null && repeated.Receipt is null && State(owner) == persisted,
                "#981领域耗尽请求重放拒绝，即使源槽被普通奖复用，也不消费哨兵或重发");

            var reopened = new CharacterRepository(path);
            That(JsonSerializer.Serialize(reopened.List(owner).Single()) == JsonSerializer.Serialize(committed),
                "#981新Repository从同隔离数据库恢复原revision/五奖UID/完整详情，不重新创建");
            using var replacement = owners.Acquire(owner.Account, () => { });
            var restoredSession = new GameSession(reopened, towns, replacement);
            restoredSession.Select(0); restoredSession.MarkReady();
            That(JsonSerializer.Serialize(restoredSession.Selected!.Inventory) == JsonSerializer.Serialize(actual) &&
                restoredSession.Selected.Revision == committed.Revision && State(replacement) == persisted,
                "#981同进程owner/Session重建恢复完整五奖；不宣称TCP重连或跨进程服务重启");
            Reject<InvalidOperationException>(() => OpenSession(), "#981旧owner不能在重建会话后再次使用源");
            That(State(replacement) == persisted && Scalar("PRAGMA integrity_check") is "ok" && Rows("PRAGMA foreign_key_check") == "[]" &&
                Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_avatar_details")) == 1 &&
                Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_creature_details")) == 1 &&
                Convert.ToInt64(Scalar("SELECT count(*) FROM mailbox_messages")) == 0 &&
                Convert.ToInt64(Scalar("SELECT count(*) FROM mailbox_attachments")) == 0,
                "#981旧owner失败后资产不变，只有一份Avatar/Pet详情、无邮件旁路且数据库/外键完整");

            InventoryResult OpenSession() => session.PlanAndCommitBooster(role => service.Open(role, request, out _, now),
                resources.Boosters.UsePlaces(SourceId), SourceId);
            string State(AccountOwners.Lease lease) => JsonSerializer.Serialize(new[]
            {
                JsonSerializer.Serialize(new CharacterRepository(path).List(lease)),
                Rows("SELECT * FROM character_inventory_items ORDER BY item_uid"),
                Rows("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index"),
                Rows("SELECT * FROM inventory_avatar_details ORDER BY item_uid"),
                Rows("SELECT * FROM inventory_creature_details ORDER BY item_uid"),
                Rows("SELECT * FROM account_wallets ORDER BY account"),
                Rows("SELECT * FROM account_currency_balances ORDER BY account,currency_kind"),
                Rows("SELECT * FROM inventory_audit_log ORDER BY audit_id"),
                Rows("SELECT * FROM currency_audit_log ORDER BY audit_id"),
                Rows("SELECT * FROM mailbox_messages ORDER BY rowid"),
                Rows("SELECT * FROM mailbox_attachments ORDER BY rowid"),
                Rows("SELECT * FROM sqlite_sequence ORDER BY name")
            });
            string Rows(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                using var reader = command.ExecuteReader(); var values = new List<object[]>();
                while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); values.Add(row); }
                return JsonSerializer.Serialize(values);
            }
            object? Scalar(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
            }
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
    }

    static AvatarPackageOpenRequest DomainRequest(byte category) => new(SourceSlot, 1, category, 0, Ids(category), []);
}
