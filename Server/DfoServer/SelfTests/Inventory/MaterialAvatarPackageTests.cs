using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

// #976：预期来自本源真实27B请求，不从待测目录反推ID或装扮能力。
internal static class MaterialAvatarPackageTests
{
    const int SourceId = 10150804, AvatarId = 101521039, TitleId = 100331476, MaterialId = 3330;
    const short SourceSlot = 66;
    internal static byte[] CapturedBody() => Convert.FromHexString("42000100000000008F160D06D4EFFA05020D0000018F160D060200");

    public static void RunCore()
    {
        var request = AvatarPackageRequests.Read(CapturedBody(), 3, 3);
        That(CapturedBody().Length == 27 && request is { Slot: SourceSlot, Count: 1, Category: 0, Subcategory: 0 } &&
            request.ItemIds.SequenceEqual(new[] { AvatarId, TitleId, MaterialId }) &&
            request.Choices is [{ ItemId: AvatarId, Ability: 2 }],
            "#976真实27B保留全部三主ID与一项装扮能力2，材料不被截去或当选项");
        var body = CapturedBody();
        var badTail = (byte[])body.Clone();
        badTail[^1] = 1;
        var orphan = (byte[])body.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(orphan.AsSpan(21), SourceId);
        foreach (var invalid in new[] { body[..^1], body.Concat(new byte[1]).ToArray(), badTail, orphan })
            Reject<InvalidDataException>(() => AvatarPackageRequests.Read(invalid, 3, 3),
                "#976原27B边界拒绝截断、额外尾字节、非零终止和非主ID选择，不扩大包长猜字段");
        const string declared = "[stackable type]\n`[booster selection]` 0\n[booster category num]\n2 1 1\n" +
            "[booster selection num]\n0\n[booster select category]\n0 0\n" +
            "[avatar]\n101521039 1 4 0\n[/avatar]\n[equipment]\n100331476 1\n[/equipment]\n" +
            "[etc]\n3330 100\n[/etc]\n[/booster select category]\n";
        var definition = AvatarPackageCatalog.Parse(declared);
        That(definition.Available && definition.Categories is [{ PendingRule: null, RowCount: 3 }],
            "#976完整三Kind声明总行数3，etc不能只删Pending后从解析结果丢失");
        foreach (string invalid in new[]
        {
            declared.Replace("3330 100", "3330"),
            declared.Replace("3330 100", "3330 0"),
            declared.Replace("3330 100", "3330 -1"),
            declared.Replace("3330 100", "3330 65536"),
            declared.Replace("3330 100", "-3330 100"),
            declared.Replace("3330 100", "bad 100"),
            declared.Replace("3330 100", "3330 100 3330 100"),
            declared.Replace("3330 100", "101521039 100"),
            declared.Replace("3330 100", "100331476 100"),
            declared.Replace("[/etc]", ""),
            declared.Replace("[/etc]", "[/equipment]"),
            declared.Replace("[/etc]", "[/etc]\n[etc]\n3330 100\n[/etc]"),
            declared.Replace("[booster selection num]\n0", "[booster selection num]\n1"),
            declared.Replace("[etc]\n3330 100\n[/etc]", "[creature]\n710 1\n[/creature]"),
            declared.Replace("[etc]\n3330 100\n[/etc]", "[stackable]\n10310265 1\n[/stackable]"),
            declared.Replace("[etc]", "[unknown]\n1\n[/unknown]\n[etc]")
        })
        {
            var denied = AvatarPackageCatalog.Parse(invalid);
            That(!denied.Available || denied.Categories.Any(category => category.PendingRule is not null || category.Rows.IsEmpty),
                "#976奇数/零/负/溢出/重复/跨Kind冲突/未闭合etc及混合自选整拒，不接受奖励子集");
        }
    }

    public static void RunResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        RunCore();
        var now = new DateTimeOffset(2026, 10, 31, 12, 45, 0, TimeSpan.Zero);
        var template = resources.Items.Get(SourceId);
        That(template.Kind == ItemKind.Consumable && template.MinimumLevel == 1 &&
            resources.Items.Get(AvatarId).Kind == ItemKind.Avatar &&
            resources.Items.Get(TitleId).Kind == ItemKind.Equipment &&
            resources.Items.Get(MaterialId).Kind == ItemKind.Material,
            "#976源及三种奖励Kind按实际资源存在，不能先因目录拒绝跳过业务断言");
        var source = new InventoryItem(976001, new(InventoryListType.Main, SourceSlot),
            InventoryRewardGrantService.Create(template, utcNow: now));
        var sentinel = source with { Uid = 976002, Position = new(InventoryListType.Main, 67) };
        var before = new InventorySnapshot([source, sentinel], Gold: 321);
        var role = new Character(1, 0, "MaterialObserved", 0, towns.Room, towns.InitialReturn)
            { Job = 0, Level = 4, Inventory = before };
        That(InventoryAvatarPackageService.SourceEligible(role, source, template) && !source.IsExpired(now.ToUnixTimeSeconds()),
            "#976真实等级资格和源期限成立，三个目标分页为空");
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var request = AvatarPackageRequests.Read(CapturedBody(), 3, 3);
        var result = service.Open(role, request, out var receipt, now);
        That(result.Accepted,
            "MATERIAL_OBSERVED_OPEN_REJECTED error=" + result.Error +
            "：真实27B请求应完整规划装扮、称号、3330材料100，不截断etc且不发通用成功");
        That(result.Plan is not null && ReferenceEquals(result.Plan.Before, before) && ReferenceEquals(role.Inventory, before) &&
            before.Find(source.Position) == source && before.Find(sentinel.Position) == sentinel,
            "#976提交前源、哨兵与角色快照保持不变");
        var after = result.Plan!.After;
        That(after.Find(source.Position) is null && after.Find(sentinel.Position) == sentinel && after.Gold == before.Gold &&
            result.Plan.OverflowItems.IsEmpty && receipt is { Rewards.Length: 3 } &&
            receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new[] {
                new BoosterGrantedItem(AvatarId, 1), new BoosterGrantedItem(TitleId, 1), new BoosterGrantedItem(MaterialId, 100)
            }.OrderBy(row => row.ItemId)), "#976只减所选源1个，三奖回执完整且钱包和无关实例不变");
    }

    public static void RunBoundaryResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        RunResources(towns, resources, packages);
        var now = new DateTimeOffset(2026, 10, 31, 12, 45, 0, TimeSpan.Zero);
        var sourceTemplate = resources.Items.Get(SourceId);
        var materialTemplate = resources.Items.Get(MaterialId);
        var titleTemplate = resources.Items.Get(TitleId);
        That(sourceTemplate.StackLimit == 1 && materialTemplate.StackLimit >= 100 && titleTemplate.DisjointForbidden,
            "#976真实源StackLimit1、材料单盒100份可堆叠，称号模板保禁分解");
        var source = new InventoryItem(976001, new(InventoryListType.Main, SourceSlot),
            InventoryRewardGrantService.Create(sourceTemplate, count: 1, utcNow: now));
        var sentinel = source with { Uid = 976002, Position = new(InventoryListType.Main, 67) };
        var before = new InventorySnapshot([source, sentinel], Gold: 321) { ReviveCoins = 7, WinPoints = 11 };
        var role = new Character(1, 0, "MaterialBoundary", 0, towns.Room, towns.InitialReturn)
            { Job = 0, Level = 4, Inventory = before };
        var request = AvatarPackageRequests.Read(CapturedBody(), 3, 3);
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var normal = Accepted(role, request, 1, "#976真实单个源一次耗尽且完整三奖，另一独立源保留");
        var avatar = normal.Plan!.After.Items.Single(item => item.Core.ItemId == AvatarId);
        var declared = packages.GetAvatar(AvatarId).Types[4];
        That(avatar.Core.Durability == 2 && avatar.ExpireTime == 0 && avatar.Core.ExpireTime == 0 &&
            avatar.Avatar is { Socket0: { Type: 2, EmblemId: -1 }, Socket1: { Type: 2, EmblemId: -1 },
                Socket2.Type: 0, Socket3.Type: 0, Socket4.Type: 0 } && declared.Days == 0 &&
            declared.Sockets.First == 2 && declared.Sockets.Second == 2,
            "#976本源type4为永久双B空孔，真实能力2保留，不继承源30天或默认预览0");
        var title = normal.Plan.After.Items.Single(item => item.Core.ItemId == TitleId);
        var material = normal.Plan.After.Items.Single(item => item.Core.ItemId == MaterialId);
        That(resources.Items.Get(title.Core.ItemId).DisjointForbidden && titleTemplate.DisjointForbidden &&
            ItemSlotBoundService.Allows(before, title.Position, ItemKind.Equipment) &&
            material.Position.List == InventoryListType.Main &&
            material.Position.Index >= ItemSlotBoundService.Range(ItemKind.Material, before).Start &&
            material.Position.Index <= ItemSlotBoundService.Range(ItemKind.Material, before).End &&
            material.Core.Kind == ItemKind.Material && material.Core.Count == 100,
            "#976称号保模板禁分解，材料真实进入Main材料分页且总量100");
        That(normal.Plan.CharacterPrecondition is not null &&
            normal.Plan.CharacterPrecondition.Matches(role, source.ExpireTime - 1) &&
            !normal.Plan.CharacterPrecondition.Matches(role, source.ExpireTime),
            "#976三奖同计划保源最早到期门，不允许旧规划跨到期提交");

        foreach (var denied in new[]
        {
            request with { ItemIds = [AvatarId, TitleId] },
            request with { ItemIds = [AvatarId, MaterialId] },
            request with { ItemIds = [TitleId, MaterialId] },
            request with { ItemIds = [AvatarId, TitleId, MaterialId, SourceId] },
            request with { ItemIds = [AvatarId, TitleId, TitleId] },
            request with { ItemIds = [AvatarId, AvatarId, MaterialId] },
            request with { Choices = [] },
            request with { Choices = [new(AvatarId, byte.MaxValue)] },
            request with { Choices = [new(AvatarId, 2), new(AvatarId, 2)] },
            request with { Choices = [new(TitleId, 2)] },
            request with { Choices = [new(MaterialId, 2)] },
            request with { Category = 11 },
            request with { Subcategory = 2 },
            request with { Count = 0 },
            request with { Count = -1 },
            request with { Count = 2 }
        }) Rejected(role, denied, 19, "#976不完整/多余/重复ID、错能力、越界分类和不合法份数整拒");
        // 同集合不同次序仍是完整全领；真实wire的顺序另由RunCore固定。
        Accepted(role, request with { ItemIds = [MaterialId, AvatarId, TitleId] }, 1,
            "#976全领按完整ID集合核对，不依赖人为排序");
        foreach (var badSource in new[]
        {
            source with { Core = source.Core with { EquipmentLockId = 1 } },
            source with { Core = source.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } }
        }) Rejected(role with { Inventory = before with { Items = [badSource, sentinel] } }, request, 19,
            "#976锁定或到期源不扣、不发任一奖");
        Rejected(role with { Level = 0 }, request, 19, "#976原min1源资格不得删除");
        for (byte job = 0; job < 16; job++)
            Accepted(role with { Job = job }, request, 1, "#976源all覆盖job=" + job + "，奖励穿戴职业不能反向限制开盒");
        foreach (var choices in new CeraPackageChoice[][] { [], [new(AvatarId, 2)], [new(AvatarId, 0)] })
        {
            var result = service.OpenCeraPackage(role, new(SourceSlot, choices), out var receipt, now);
            That(!result.Accepted && result.Plan is null && receipt is null && ReferenceEquals(role.Inventory, before),
                "#976未经证明的518纯装扮提交拒绝混合三奖，不能只发装扮而漏称号材料");
        }

        foreach (var kind in new[] { ItemKind.Avatar, ItemKind.Equipment, ItemKind.Material })
        {
            var actor = role with { Inventory = before with { Items = [source, sentinel, .. Filled(kind, 0)] } };
            Rejected(actor, request, 4, "#976单独填满" + kind + "页必须整拒，源消耗品腾格不能代替目标分页");
        }
        // 材料旧堆要与永久新奖不兼容，否则首个兼容满堆会先整拒，不能因空格而另起一堆。
        int fillerDeadline = checked((int)now.AddDays(1).ToUnixTimeSeconds());
        var exactMaterials = Filled(ItemKind.Material, 1).Select(item => item with
            { Core = item.Core with { ExpireTime = fillerDeadline } }).ToArray();
        That(exactMaterials.All(item => !item.IsExpired(now.ToUnixTimeSeconds()) &&
            !InventoryStackRuleService.CanShareStack(material.Core, item.Core)),
            "#976恰空间例的已有材料均合法未到期，但Core期限不同使其与本次永久新奖不兼容");
        var exact = role with { Inventory = before with { Items = [source, sentinel,
            .. Filled(ItemKind.Avatar, 1), .. Filled(ItemKind.Equipment, 1), .. exactMaterials] } };
        var exactResult = Accepted(exact, request, 1, "#976三个目标分页各恰剩一格且无兼容材料旧堆时完整成功");
        var exactMaterialSlot = new InventorySlot(InventoryListType.Main, ItemSlotBoundService.Range(ItemKind.Material, before).End);
        That(exactResult.Plan!.After.Find(exactMaterialSlot) is { Core.ItemId: MaterialId, Core.Count: 100, Core.ExpireTime: 0 } &&
            exactMaterials.All(item => exactResult.Plan.After.Find(item.Position) == item),
            "#976唯一空材料格接收永久100，所有不同期限旧堆完整保留，不改变首兼容堆规则");

        var filledMaterials = Filled(ItemKind.Material, 0);
        // 沿现有RewardPlacement：一次原型只尝试首个兼容堆，不能跨多个余量不足堆分摊。
        That(materialTemplate.StackLimit > 100, "#976实际材料堆上限足以构造非零已有堆恰余100");
        var first = filledMaterials[0] with { Core = filledMaterials[0].Core with { Value = materialTemplate.StackLimit - 100 } };
        var stackActor = role with { Inventory = before with { Items = [source, sentinel, first, .. filledMaterials.Skip(1)] } };
        var stacked = Accepted(stackActor, request, 1, "#976材料页满且首兼容堆恰余100时成功");
        That(stacked.Plan!.After.Items.Count(item => item.Core.ItemId == MaterialId) == filledMaterials.Length &&
            stacked.Plan.After.Find(first.Position)!.Core.Count == materialTemplate.StackLimit &&
            stacked.Plan.After.Find(first.Position)!.Uid == first.Uid,
            "#976完整100份合入首兼容堆保原UID/槽，不新增一格或改placer规则");
        var half0 = filledMaterials[0] with { Core = filledMaterials[0].Core with { Value = materialTemplate.StackLimit - 50 } };
        var half1 = filledMaterials[1] with { Core = filledMaterials[1].Core with { Value = materialTemplate.StackLimit - 50 } };
        Rejected(role with { Inventory = before with { Items = [source, sentinel,
            half0, half1, .. filledMaterials.Skip(2)] } }, request, 4,
            "#976材料页满即使两兼容堆各余50也不跨堆分摊，首堆不能全合则整拒");
        var first99 = first with { Core = first.Core with { Value = materialTemplate.StackLimit - 99 } };
        var later100 = filledMaterials[1] with { Core = filledMaterials[1].Core with { Value = materialTemplate.StackLimit - 100 } };
        Rejected(role with { Inventory = before with { Items = [source, sentinel,
            first99, later100, .. filledMaterials.Skip(2)] } }, request, 4,
            "#976材料页满首兼容堆余99即使后堆可容100仍整拒，不迁就测试改变首堆规则");
        That(materialTemplate.StackLimit == int.MaxValue,
            "#976真实3330未声明stack limit，当前模板默认为int.MaxValue；不冒有限上限资源事实");
        // 多开仅在独立合成源StackLimit3中测试；真实源始终StackLimit1，不给真实模板放宽。
        // 材料单条上限按Docs/模块/背包与装备.md:211；所有预填项只在独立测试目录缓存。
        var limitedItems = SyntheticItems(3);
        var limitedSource = source with { Core = InventoryRewardGrantService.Create(limitedItems.Get(SourceId), 3, utcNow: now) };
        var limitedBefore = before with { Items = [limitedSource, sentinel] };
        var limitedRole = role with { Inventory = limitedBefore };
        var limitedService = new InventoryAvatarPackageService(limitedItems, packages, resources.GoldLimits, resources.EpicPieces);
        var doubled = limitedService.Open(limitedRole, request with { Count = 2 }, out var doubledReceipt, now);
        That(doubled.Accepted && doubled.Plan is not null && doubledReceipt is { Rewards.Length: 3 } &&
            ReferenceEquals(limitedRole.Inventory, limitedBefore) && ReferenceEquals(doubled.Plan.Before, limitedBefore),
            "#976仅合成源StackLimit3的多开2份可规划，不能称真实源合法多开");
        var doubledAfter = doubled.Plan!.After;
        That(doubledAfter.Find(limitedSource.Position)!.Core.Count == 1 && doubledAfter.Find(sentinel.Position) == sentinel &&
            doubledAfter.Items.Count(item => item.Core.ItemId == AvatarId) == 2 &&
            doubledAfter.Items.Count(item => item.Core.ItemId == TitleId) == 2 &&
            doubledAfter.Items.Single(item => item.Core.ItemId == MaterialId).Core.Count == 200 &&
            doubledAfter.Gold == limitedBefore.Gold && doubledAfter.ReviveCoins == limitedBefore.ReviveCoins &&
            doubledAfter.WinPoints == limitedBefore.WinPoints && Equals(doubledAfter.AccountWallet, limitedBefore.AccountWallet) &&
            Equals(doubledAfter.EpicPieces, limitedBefore.EpicPieces) && doubled.Plan.OverflowItems.IsEmpty &&
            doubledReceipt!.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new[] {
                new BoosterGrantedItem(AvatarId, 2), new BoosterGrantedItem(TitleId, 2), new BoosterGrantedItem(MaterialId, 200)
            }.OrderBy(row => row.ItemId)),
            "#976合成双开准确三奖倍数/源3减2余1/材料恰上限200，钱包哨兵与提交前库存保持");
        var overLimit = limitedService.Open(limitedRole, request with { Count = 3 }, out var overLimitReceipt, now);
        That(!overLimit.Accepted && overLimit.Error == 4 && overLimit.Plan is null && overLimitReceipt is null &&
            ReferenceEquals(limitedRole.Inventory, limitedBefore) && limitedBefore.Find(limitedSource.Position) == limitedSource &&
            limitedBefore.Find(sentinel.Position) == sentinel && limitedBefore.Gold == before.Gold &&
            limitedBefore.ReviveCoins == before.ReviveCoins && limitedBefore.WinPoints == before.WinPoints &&
            Equals(limitedBefore.AccountWallet, before.AccountWallet) && Equals(limitedBefore.EpicPieces, before.EpicPieces) &&
            resources.Items.Get(SourceId).StackLimit == 1 && resources.Items.Get(MaterialId).StackLimit == int.MaxValue,
            "#976合成源上限3/材料上限200：输入源3本来合法，但材料单条300须Error4整拒，不拆原型绕上限");
        // 极值也是独立合成源；必须由Create确认count在该合成模板上限内，再测checked总量拒绝。
        var extremeItems = SyntheticItems(int.MaxValue);
        var extreme = source with { Core = InventoryRewardGrantService.Create(extremeItems.Get(SourceId), int.MaxValue, utcNow: now) };
        var extremeBefore = before with { Items = [extreme, sentinel] };
        var extremeRole = role with { Inventory = extremeBefore };
        var extremeService = new InventoryAvatarPackageService(extremeItems, packages, resources.GoldLimits, resources.EpicPieces);
        var overflow = extremeService.Open(extremeRole, request with { Count = int.MaxValue }, out var overflowReceipt, now);
        That(!overflow.Accepted && overflow.Plan is null && overflowReceipt is null &&
            ReferenceEquals(extremeRole.Inventory, extremeBefore) && extremeBefore.Find(extreme.Position) == extreme &&
            extremeBefore.Find(sentinel.Position) == sentinel && extremeBefore.Gold == before.Gold &&
            Equals(extremeBefore.AccountWallet, before.AccountWallet) && Equals(extremeBefore.EpicPieces, before.EpicPieces) &&
            resources.Items.Get(SourceId).StackLimit == 1,
            "#976仅合成极值源验证总量不整数回绕或部分消费，真实源上限1不改、不冒合法实机多开");
        // 预期22个叶ID固定来自保存的本源声明；不读取待测MaterialRows生成oracle。
        int[] expectedAvatars = [101521039,101521040,112520453,112520454,103520734,103520735,
            102521035,102521036,104521045,104521046,105520947,105520948,107520624,107520625,
            106521024,106521025,108521100,108521101,109520861,109520862,113521150,113521151];
        var definition = packages.Get(SourceId);
        That(definition.Available && definition.SelectionCount == 0 && definition.Dimensions.SequenceEqual(new[] { 11, 2 }) &&
            definition.Categories.Length == 22, "#976本源原2维11x2全22分类保留，未裁成已观察0/0");
        for (int index = 0; index < expectedAvatars.Length; index++)
        {
            int id = expectedAvatars[index];
            var category = definition.Categories.Single(row => row.Category == index / 2 && row.Subcategory == index % 2);
            That(category.Rows.SequenceEqual(new[] { new AvatarPackageRow(id, 1, 4, 0) }) &&
                category.EquipmentRows.SequenceEqual(new[] { new AvatarPackageEquipmentRow(TitleId, 1) }),
                "#976保存原声明分类=" + index / 2 + "/" + index % 2 + "装扮四列及共同称号完整");
            var leaf = packages.GetAvatar(id);
            That(leaf.PendingRule is null && !leaf.Abilities.IsEmpty && leaf.Types.Length > 4,
                "#976分类" + index + "实际资源叶可读并有合法能力和type4，不借用第一叶能力集合");
            byte ability = leaf.Abilities.Min();
            var selected = request with { Category = checked((byte)(index / 2)), Subcategory = checked((byte)(index % 2)),
                ItemIds = [id, TitleId, MaterialId], Choices = [new(id, ability)] };
            var opened = Accepted(role, selected, 1, "#976资源领域逐分类=" + index + "完整三奖");
            var reward = opened.Plan!.After.Items.Single(item => item.Core.ItemId == id);
            That(reward.Core.Durability == ability && reward.ExpireTime == 0 && reward.Avatar is
                { Socket0.Type: 2, Socket1.Type: 2 },
                "#976各叶选择自身合法能力、永久双B，资源领域覆盖不冒22客户端实机");
        }

        ItemCatalog SyntheticItems(int sourceLimit)
        {
            var isolated = new ItemCatalog(null!,
                new Dictionary<int, string> { [AvatarId] = "synthetic/avatar.equ", [TitleId] = "synthetic/title.equ" },
                new Dictionary<int, string> { [SourceId] = "synthetic/source.stk", [MaterialId] = "synthetic/material.stk" }, 0);
            isolated.PrefillTemplates(new Dictionary<int, ItemTemplate>
            {
                [SourceId] = sourceTemplate with { StackLimit = sourceLimit },
                [AvatarId] = resources.Items.Get(AvatarId), [TitleId] = titleTemplate,
                [MaterialId] = materialTemplate with { StackLimit = 200 }
            });
            return isolated;
        }
        InventoryResult Accepted(Character actor, AvatarPackageOpenRequest selected, int count, string label)
        {
            var original = actor.Inventory;
            var originalSource = original.Find(new(InventoryListType.Main, SourceSlot))!;
            var result = service.Open(actor, selected, out var receipt, now);
            That(result.Accepted && result.Plan is not null && receipt is { Rewards.Length: 3 }, label + " error=" + result.Error);
            var after = result.Plan!.After;
            int avatarId = selected.ItemIds.Single(id => id != TitleId && id != MaterialId);
            That(ReferenceEquals(actor.Inventory, original) && ReferenceEquals(result.Plan.Before, original) &&
                original.Find(originalSource.Position) == originalSource &&
                after.Find(sentinel.Position) == sentinel &&
                (after.Find(originalSource.Position)?.Core.Count ?? 0) == originalSource.Core.Count - count &&
                MaterialTotal(after) - MaterialTotal(original) == checked(100L * count) &&
                after.Items.Count(item => item.Core.ItemId == avatarId) - original.Items.Count(item => item.Core.ItemId == avatarId) == count &&
                after.Items.Count(item => item.Core.ItemId == TitleId) - original.Items.Count(item => item.Core.ItemId == TitleId) == count &&
                after.Gold == original.Gold && after.ReviveCoins == original.ReviveCoins && after.WinPoints == original.WinPoints &&
                Equals(after.AccountWallet, original.AccountWallet) && Equals(after.EpicPieces, original.EpicPieces) &&
                result.Plan.OverflowItems.IsEmpty && receipt!.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new[] {
                    new BoosterGrantedItem(avatarId, count), new BoosterGrantedItem(TitleId, count),
                    new BoosterGrantedItem(MaterialId, checked(count * 100)) }.OrderBy(row => row.ItemId)),
                label + "：三奖量/源差/哨兵/全部钱包及提交前快照原子规划");
            foreach (var item in after.Items.Where(item => item.Core.ItemId == MaterialId))
                That(item.Core.Count <= materialTemplate.StackLimit && item.Core.Count > 0 && item.Position.List == InventoryListType.Main &&
                    item.Position.Index >= ItemSlotBoundService.Range(ItemKind.Material, after).Start &&
                    item.Position.Index <= ItemSlotBoundService.Range(ItemKind.Material, after).End,
                    label + "：每个材料堆合法且都在真实材料分页");
            return result;
        }
        void Rejected(Character actor, AvatarPackageOpenRequest selected, ushort error, string label)
        {
            var original = actor.Inventory;
            var result = service.Open(actor, selected, out var receipt, now);
            That(!result.Accepted && result.Error == error && result.Plan is null && receipt is null &&
                ReferenceEquals(actor.Inventory, original) && original.Find(sentinel.Position) == sentinel && original.Gold == 321 &&
                original.ReviveCoins == 7 && original.WinPoints == 11, label);
        }
        long MaterialTotal(InventorySnapshot inventory) => inventory.Items.Where(item => item.Core.ItemId == MaterialId).Sum(item => (long)item.Core.Count);
        InventoryItem[] Filled(ItemKind kind, int emptySlots)
        {
            var template = resources.Items.Get(kind == ItemKind.Avatar ? AvatarId : kind == ItemKind.Equipment ? TitleId : MaterialId);
            var range = ItemSlotBoundService.Range(kind, before);
            return Enumerable.Range(range.Start, range.End - range.Start + 1 - emptySlots).Select(index =>
            {
                long uid = 1000000 + (long)kind * 1000 + index;
                var item = InventoryRewardGrantService.CreateItem(template,
                    new(ItemSlotBoundService.List(kind), checked((short)index)),
                    InventoryRewardGrantService.Create(template, kind == ItemKind.Material ? template.StackLimit : 1, utcNow: now));
                return item with { Uid = uid, Core = kind == ItemKind.Avatar ? item.Core with { Value = checked((int)uid) } : item.Core };
            }).ToArray();
        }
    }
}
