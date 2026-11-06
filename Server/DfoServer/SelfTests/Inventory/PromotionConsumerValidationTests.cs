using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionConsumerValidationTests
{
    public static void RunCore()
    {
        var equipment = Template(880001101, ItemKind.Equipment, 12);
        InventoryRewardGrantService.ValidateCreationArguments(equipment, upgrade: 31, refinement: 127);
        foreach (var invalid in new Action[]
        {
            () => InventoryRewardGrantService.ValidateCreationArguments(equipment, 0),
            () => InventoryRewardGrantService.ValidateCreationArguments(equipment, 2),
            () => InventoryRewardGrantService.ValidateCreationArguments(equipment, upgrade: 32),
            () => InventoryRewardGrantService.ValidateCreationArguments(equipment, refinement: 128),
            () => InventoryRewardGrantService.ValidateCreationArguments(Template(880001102, ItemKind.Material), upgrade: 1),
            () => InventoryRewardGrantService.ValidateCreationArguments(Template(880001102, ItemKind.Material), quality: 1)
        })
        {
            bool rejected = false;
            try { invalid(); } catch (InvalidDataException) { rejected = true; }
            That(rejected, "统一创建纯守卫拒绝真实无效数量和属性，不依赖随机实例");
        }
        That(InventoryRewardGrantService.GrantDefinitionError(equipment, new(equipment.Id, 2)) is null &&
            InventoryRewardGrantService.GrantDefinitionError(Template(880001102, ItemKind.Material), new(880001102, 2001)) is null,
            "整笔发放Count沿真实放置器拆多件/多堆，不错误等同单Core上限");
        That(InventoryRewardGrantService.GrantDefinitionError(equipment, new(equipment.Id, 0)) is not null,
            "整笔发放仍拒绝真实非正Count");
        var row = new AvatarPackageRow(880001103, 1, 0, 0);
        var avatar = new AvatarPackageItemDefinition([], [0]) { RequiresAbilityChoice = false };
        var template = Template(row.ItemId, ItemKind.Avatar, 9);
        That(InventoryAvatarPackageService.ValidateRowDefinition(row, template, avatar) is null,
            "模板期限孔位和无选择光环沿原装扮消费者合法");
        That(InventoryAvatarPackageService.ValidateRowDefinition(row with { Count = 0 }, template, avatar) is not null &&
            InventoryAvatarPackageService.ValidateRowDefinition(row, template, avatar with { PendingRule = "unknown" }) is not null &&
            InventoryAvatarPackageService.ValidateRowDefinition(row with { ForcedAbility = 1 }, template, avatar) is not null,
            "装扮行保留数量、PendingRule及强制能力资格");
    }

    public static void RunResources(ItemResources resources)
    {
        PromotionCreatureRenameConsumerTests.RunResources(resources);
        PromotionFatigueConsumerTests.RunResources(resources);
        PromotionAvatarGiftConsumerTests.RunResources(resources);
        PromotionCreatureEggConsumerTests.RunResources(resources);
        PromotionLevelUpByTableConsumerTests.RunResources(resources);
        PromotionDimensionEntryConsumerTests.RunResources(resources);
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var enabled = ServerRuntime.LoadCashShopPromotions(resources,
            JsonSerializer.Serialize(shipped.Select(configuration => configuration with { Enabled = true }).ToArray()));
        That(enabled.Activities.Length == shipped.Length && enabled.Activities.All(entry => entry.Availability == PromotionAvailability.Active),
            "真实唯一loader已启用原配置完整图: " + string.Join("; ", enabled.Activities.Select(entry => entry.Error ?? entry.Availability.ToString())));
        PromotionRuneConsumerTests.RunResources(resources);
        RealEmptySelectionCoordinates(resources);
        RealFixedDisjointParents(resources, shipped);
        // 仅fixture隔离目录使用已有archive；不重读PVF、不覆盖真实模板和目录缓存。
        var archive = (PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resources.Items)!;
        const int couponId = 490703365;
        var paths = (IReadOnlyDictionary<int, string>)typeof(ItemCatalog)
            .GetField("stackable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resources.Items)!;
        var items = new ItemCatalog(archive, new Dictionary<int, string>(),
            new[] { couponId, 490705399, 490705410, 490705411, 490705434, 490705433, 490705412 }
                .ToDictionary(id => id, id => paths[id]), 100);
        int[] roots = [880001001, 880001002, 880001003];
        const int leaf = 880001101, unsupported = 880001102;
        items.PrefillTemplates(roots.Select(id => KeyValuePair.Create(id, Template(id, ItemKind.Consumable) with { StackableType = "[booster] 0" }))
            .Concat([KeyValuePair.Create(leaf, Template(leaf, ItemKind.Equipment, 12)),
                KeyValuePair.Create(unsupported, Template(unsupported, ItemKind.Material)), KeyValuePair.Create(couponId, resources.Items.Get(couponId))]));
        var boosters = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boosters)!;
        var avatars = new AvatarPackageCatalog(_ => throw new InvalidDataException("fixture unexpected raw read"),
            new Dictionary<int, string>(), new Dictionary<int, string>());
        var shop = CashShopCatalog.Parse($"[item]\n1 {roots[0]} 1 0 0 100 0 0 0\n");
        var rules = new PromotionRules(new("consumer-fixture", "period", "v1", "default", "package"),
            10, 3, 31, roots[2], 1, 5000, [new(1, roots[0], 1)], [new(2, roots[1], 1)]);
        PromotionConsumerValidation Validator(CashShopCatalog? selected = null, AmplifyUpgradeCatalog? selectedUpgrades = null) => new(items, boosters, avatars, selected ?? shop,
            resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
            resources.Creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments, resources.Enhancements, resources.RecoveryConsumables,
            resources.Amplifications, selectedUpgrades ?? resources.AmplifyUpgrades);
        void Reset()
        {
            definitions.Clear();
            foreach (int root in roots) definitions[root] = new([new(1, [new(leaf, 1, 1)])]);
        }
        Reset();
        var validator = Validator();
        {
            const int nestedChild = 880001201;
            items.PrefillTemplates([KeyValuePair.Create(nestedChild,
                Template(nestedChild, ItemKind.Consumable) with { StackableType = "[booster selection] 0" })]);
            var nestedSelection = new BoosterDefinition([], Selection: new(1, [new(0, 0, [new(leaf, 1)])]));
            var fixedParent = new BoosterDefinition([new(1, [new(nestedChild, 1, 1)])])
                { SetsDisjointInstanceType = true };
            var selectionParent = new BoosterDefinition([], Selection: new(1, [new(0, 0, [new(nestedChild, 1)])]))
                { SetsDisjointInstanceType = true };

            string? ValidateNested(BoosterDefinition parent, BoosterDefinition child)
            {
                Reset();
                definitions[roots[0]] = parent;
                definitions[nestedChild] = child;
                return validator.Validate(rules);
            }

            foreach (var (parent, label) in new[]
            {
                (fixedParent, "固定普通父盒"),
                (selectionParent, "原自选父盒")
            })
            {
                string? error = ValidateNested(parent, nestedSelection);
                That(error is null, label + "的分解声明允许完整受支持自选子盒，真实validator无误拒: " + error);
            }

            // 每项仅改变具名父盒边界，仍执行同一个真实校验器。
            foreach (var (parent, label) in new[]
            {
                (fixedParent with { Lottery = true }, "Lottery"),
                (fixedParent with { Pools = [new(2, [new(nestedChild, 1, 1)])] }, "多抽"),
                (fixedParent with { Pools = [new(1, [new(nestedChild, 1, 1), new(leaf, 1, 1)])] }, "双行随机池"),
                (fixedParent with { Pools = [new(1, [new(nestedChild, 1, 1)]) { JobTag = "[swordman]" }] }, "职业池"),
                (fixedParent with { PackageRewards = [new(leaf, 1, 1)] }, "混合PackageRewards"),
                (fixedParent with { SetsEpicExchangeInstanceType = true }, "史诗实例声明")
            })
            {
                string? error = ValidateNested(parent, nestedSelection);
                That(error?.Contains(nestedChild + ": unsupported_instance_type_reward", StringComparison.Ordinal) == true,
                    label + "不借固定普通父盒规则放行自选子盒: " + error);
            }

            foreach (var (child, label) in new[]
            {
                (new BoosterDefinition([new(1, [new(leaf, 1, 1)])]), "只有普通池、无Selection的子盒"),
                (nestedSelection with { PendingRule = "nested-child-pending" }, "Pending子盒"),
                (new BoosterDefinition([]), "不可用子盒")
            })
            {
                string? error = ValidateNested(fixedParent, child);
                That(error?.Contains(nestedChild + ": unsupported_instance_type_reward", StringComparison.Ordinal) == true,
                    label + "仍在实际父子奖励边拒绝: " + error);
            }

            string? noUsable = ValidateNested(fixedParent,
                nestedSelection with { Selection = new(1, [new(0, 0, [])]) });
            That(noUsable is not null && noUsable.Contains("selection_has_no_usable_category", StringComparison.Ordinal) &&
                !noUsable.Contains("unsupported_instance_type_reward", StringComparison.Ordinal),
                "目录Available但无可用分类的子盒必须继续展开并拒绝，不把父子边放行等同整图通过: " + noUsable);

            string? deep = ValidateNested(fixedParent,
                nestedSelection with { Selection = new(1, [new(0, 0, [new(unsupported, 1)])]) });
            That(deep is not null && deep.Contains(unsupported + ": material_consumer_not_connected", StringComparison.Ordinal) &&
                !deep.Contains("unsupported_instance_type_reward", StringComparison.Ordinal),
                "允许真实父子边后仍核未支持深叶，不把子盒Selection存在等同消费者闭环: " + deep);
            Reset();
        }

        That(validator.Validate(rules) is null, "真实消费者完整遍历合法已支持图可通过，非恒错误门");
        string json = JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, true) });
        That(CashShopPromotionCatalog.Parse(json, shop, items.Get, validator.Validate).Product(1)?.Availability == PromotionAvailability.Active &&
            CashShopPromotionCatalog.Parse(json, shop, items.Get).Product(1)?.Availability == PromotionAvailability.Invalid,
            "实际生产消费者回调可授予Active，缺消费者仍Invalid；未用恒成功lambda");
        definitions[roots[0]] = new([new(1, [new(leaf, 1, 1), new(unsupported, 1, 1)])]);
        That(validator.Validate(rules)?.Contains(unsupported.ToString(), StringComparison.Ordinal) == true,
            "非首正权重深层未接用途拒绝整活动");
        Reset();
        definitions[roots[1]] = new([new(1, [new(unsupported, 1, 1)])]);
        That(validator.Validate(rules) is not null, "单独档位根也展开，不只检查商城产品");
        Reset();
        definitions[roots[2]] = new([new(1, [new(unsupported, 1, 1)])]);
        That(validator.Validate(rules) is not null, "溢出根也展开，不只检查前27奖");
        Reset();
        definitions[roots[1]] = new([new(1, [new(leaf, 1, 1)]) { RequiredKind = ItemKind.Creature }]);
        That(validator.Validate(rules) is not null, "共享叶已展开仍核每条来源边的RequiredKind");
        Reset();
        definitions[roots[1]] = new([new(1, [new(unsupported, 0, 1), new(leaf, 1, 1)])]);
        That(validator.Validate(rules) is null, "零权重不可达候选不虚构实际发奖");
        Reset();
        definitions[roots[1]] = new([new(1, [new(unsupported, 1, 1)])
            { JobTag = "[swordman]", GrowType = 0, MinimumLevel = 1, MaximumLevel = 95 }]);
        That(validator.Validate(rules) is not null, "未用单一测试角色过滤其它职业合法候选支路");
        Reset();
        definitions[roots[0]] = new([new(1, [new(roots[1], 1, 1)])]);
        definitions[roots[1]] = new([new(1, [new(roots[0], 1, 1), new(leaf, 1, 1)])]);
        That(validator.Validate(rules)?.Contains("free_reward_cycle", StringComparison.Ordinal) == true, "免费发奖A→B→A明确拒绝，不以visited掩盖");
        Reset();
        definitions[roots[0]] = new([new(1, [new(leaf, 1, 1)])], Lottery: true)
            { LotteryUseNeedItem = new(unsupported, 1) };
        definitions[roots[1]] = new([new(1, [new(unsupported, 1, 1)])]);
        That(validator.Validate(rules) is null, "可达真实开罐typed成本声明材料用途，出队顺序不误拒");
        Reset();
        // 原生坐标由最后同键块生效；空坐标本身不可选，不是整盒消费者未接。
        definitions[roots[0]] = new([], Selection: new(1,
            [new(0, 0, []), new(0, 0, [new(leaf, 2)]), new(1, 0, [])]));
        That(validator.Validate(rules) is null, "最终非空坐标和不可选空坐标可共存，覆盖前块不被当作新用途");
        var now = DateTimeOffset.UtcNow;
        var source = new DfoServer.Game.Inventory.Models.InventoryItem(1, new(DfoServer.Game.Inventory.Models.InventoryListType.Main, 65),
            InventoryRewardGrantService.Create(items.Get(roots[0]), utcNow: now));
        var role = new DfoServer.Game.Characters.Character(1, 0, "SelectionConsumer", 0, new(1, 1, 0, 0), null)
            { Job = 0, Level = 95, Inventory = new([source], Gold: 12345) };
        var service = new InventoryBoosterService(items, boosters, resources.GoldLimits, resources.EpicPieces,
            randomOptions: resources.RandomOptions, attributePairs: resources.AttributePairs);
        var opened = service.Open(role, new(65, 1, new(0, 0, [leaf])), out var openedReceipt, now);
        That(opened.Accepted && openedReceipt?.Rewards is [{ ItemId: leaf, Count: 2 }] &&
            opened.Plan!.After.Items.Count(item => item.Core.ItemId == leaf) == 2 &&
            opened.Plan.After.Items.All(item => item.Core.ItemId != roots[0]) && opened.Plan.After.Gold == 12345 &&
            role.Inventory.Items.Single() == source && role.Inventory.Gold == 12345,
            "真实Open按最后坐标保Count2拆两件，单扣源，纯计划不改输入及钱包");
        foreach (int[] choice in new[] { Array.Empty<int>(), new[] { leaf } })
        {
            var denied = service.Open(role, new(65, 1, new(1, 0, choice)), out var deniedReceipt, now);
            That(!denied.Accepted && denied.Plan is null && deniedReceipt is null &&
                role.Inventory.Items.Single() == source && role.Inventory.Gold == 12345,
                "真实Open对空或伪造选择拒绝，不扣源、不发奖、不改钱包");
        }
        definitions[roots[0]] = new([], Selection: new(1, [new(0, 0, [])]));
        That(validator.Validate(rules)?.Contains("selection_has_no_usable_category", StringComparison.Ordinal) == true,
            "全部有效坐标都空仍拒绝整活动，不无条件跳空");
        definitions[roots[0]] = new([], Selection: new(1,
            [new(0, 0, [new(leaf, 1)]), new(1, 0, [], "unknown-category-rule")]));
        That(validator.Validate(rules)?.Contains("unknown-category-rule", StringComparison.Ordinal) == true,
            "空坐标的真实PendingRule仍拒绝，不借空类忽略未知资格");
        definitions[roots[0]] = new([], Selection: new(1,
            [new(0, 0, [new(leaf, 1)]), new(1, 0, [new(unsupported, 1)])]));
        That(validator.Validate(rules) is not null, "全部实际非空坐标继续展开，不能只留第一个合法职业类");
        Reset();
        var bonusShop = CashShopCatalog.Parse($"[item]\n1 {roots[0]} 1 0 0 100 0 0 0\n",
            $"[bonus item]\n1 {unsupported} 1\n[/bonus item]");
        That(Validator(bonusShop).Validate(rules) is not null, "实际商城Bonus是独立交付根，不能遗漏");
        Reset();
        int couponKey = items.CashShopCouponKey(couponId) ?? throw new InvalidDataException("real coupon fixture missing typed key");
        definitions[roots[0]] = new([new(1, [new(couponId, 1, 1)])]);
        CashShopCatalog CouponShop(string second) => CashShopCatalog.Parse(
            $"[item]\n1 {roots[0]} 1 0 0 100 0 0 0\n" + second,
            couponText: $"[coupon]\n{couponKey} 900 2\n[/coupon]");
        var positiveCoupon = CouponShop($"2 {roots[0]} 1 0 0 100 0 0 0\n");
        That(Validator(positiveCoupon).Validate(rules) is null,
            "真实typed券映射正价再次购买可回到同盒，不误报免费环");
        var freeCoupon = CouponShop($"2 {roots[0]} 1 0 0 0 0 0 0\n");
        That(Validator(freeCoupon).Validate(rules) is not null,
            "零价映射不能冒成paid断边绕过免费奖励循环");
        var avatarCoupon = CouponShop($"[avatar]\n2 {roots[0]} 1 0 0 100\n");
        That(Validator(avatarCoupon).Validate(rules) is not null,
            "券映射avatar section依真实购买入口明确拒绝");
        var unknownPremium = CouponShop($"[premium]\n2 {roots[0]} 1 0 0 100 0 0 0\n");
        That(Validator(unknownPremium).Validate(rules) is not null,
            "未知premium section不以普通盒grant替代真实服务");
        foreach (int consumerId in new[] { 490705399, 490705410, 490705411, 490705434, 490705433, 490705412 })
        {
            Reset();
            items.PrefillTemplates([KeyValuePair.Create(consumerId, resources.Items.Get(consumerId))]);
            definitions[roots[1]] = new([new(1, [new(leaf, 1, 1), new(consumerId, 1, 1)])]);
            string? error = validator.Validate(rules);
            That(error is null, $"已交付消费者{consumerId}沿真实typed目录接入非首深层奖励边: {error}");
        }
        const int pendingId = 880001106;
        Reset();
        items.PrefillTemplates([KeyValuePair.Create(pendingId, Template(pendingId, ItemKind.Consumable))]);
        string PendingResolve(int value) => value switch
        {
            -1 => "[action type]", -2 => "[amplify protect equipment]", -3 => "[/action type]",
            -4 => "[total usable count]", _ => archive.ResolveString(value)
        };
        var pendingDocument = new PvfTokenDocument([new(3, -1), new(6, -2), new(3, -3), new(3, -4), new(0, 1)], PendingResolve);
        var pendingCatalog = new AmplifyUpgradeCatalog(path => path == "stackable/pending.stk" ? pendingDocument :
            new PvfTokenDocument(archive.ReadTokens(path), archive.ResolveString), PendingResolve,
            new Dictionary<int, string> { [pendingId] = "pending.stk" });
        definitions[roots[1]] = new([new(1, [new(leaf, 1, 1), new(pendingId, 1, 1)])]);
        That(Validator(selectedUpgrades: pendingCatalog).Validate(rules)?.Contains("total usable count", StringComparison.Ordinal) == true,
            "独立增幅consumer仍拒真实PendingRule，不因Kind已支持放行未接次数限制");
        const int emblemId = 880001105;
        Reset();
        items.PrefillTemplates([KeyValuePair.Create(emblemId, Template(emblemId, ItemKind.Emblem) with { EmblemTargetSockets = 1 })]);
        definitions[roots[1]] = new([new(1, [new(leaf, 1, 1), new(emblemId, 1, 1)])]);
        That(validator.Validate(rules) is null, "真实非零孔型徽章可沿深层图到达已支持的镶嵌消费者");
        items.PrefillTemplates([KeyValuePair.Create(emblemId, Template(emblemId, ItemKind.Emblem))]);
        That(validator.Validate(rules)?.Contains("emblem_socket_definition", StringComparison.Ordinal) == true,
            "孔型缺失拒绝整活动，不仅凭Emblem种类放行");
        Reset();
        var avatarPackages = (ConcurrentDictionary<int, Lazy<AvatarPackageDefinition>>)typeof(AvatarPackageCatalog)
            .GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(avatars)!;
        avatarPackages[roots[0]] = new(() => new(0, [], [], "typed-avatar-invalid") { Recognized = true });
        That(validator.Validate(rules)?.Contains("typed-avatar-invalid", StringComparison.Ordinal) == true,
            "装扮已识别却不支持时拒绝，不回落已合法普通booster");

        // 只替换当前fixture目录缓存，验证[stackable]材料边而不读取新的资源叶。
        Reset();
        const int avatarId = 880001103;
        items.PrefillTemplates([KeyValuePair.Create(avatarId, Template(avatarId, ItemKind.Avatar, 9)),
            KeyValuePair.Create(unsupported, Template(unsupported, ItemKind.Material))]);
        var avatarItems = (ConcurrentDictionary<int, Lazy<AvatarPackageItemDefinition>>)typeof(AvatarPackageCatalog)
            .GetField("avatars", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(avatars)!;
        avatarItems[avatarId] = new(() => new([], [0]) { RequiresAbilityChoice = false });
        var materialCategory = new AvatarPackageCategory(0, 0, [new(avatarId, 1, 0, 0)])
            { StackableRows = [new(unsupported, 1)] };
        avatarPackages[roots[0]] = new(() => new(0, [1, 1], [materialCategory]) { Recognized = true });
        definitions[roots[1]] = new([new(1, [new(leaf, 1, 1)])], Lottery: true)
            { LotteryUseNeedItem = new(unsupported, 1) };
        string? connected = validator.Validate(rules);
        That(connected is null, "[stackable]材料按实际Kind经Edge并连接已有材料用途: " + connected);

        Reset();
        string? disconnected = validator.Validate(rules);
        That(disconnected?.Contains("material_consumer_not_connected", StringComparison.Ordinal) == true &&
            disconnected.Contains(unsupported.ToString(), StringComparison.Ordinal) &&
            disconnected.Contains(":stackable:", StringComparison.Ordinal) &&
            !disconnected.Contains("reward_template_kind", StringComparison.Ordinal),
            "材料类型边通过后，未连接用途仍因该材料拒绝");

        items.PrefillTemplates([KeyValuePair.Create(unsupported,
            Template(unsupported, ItemKind.Material) with { IsPremiumService = true })]);
        string? special = validator.Validate(rules);
        That(special?.Contains("avatar_material_row_definition", StringComparison.Ordinal) == true &&
            special.Contains(unsupported.ToString(), StringComparison.Ordinal),
            "[stackable]材料的特殊owner仍由材料行守卫拒绝");
        const int walletId = 3037;
        foreach (bool stackableWallet in new[] { false, true })
        {
            Reset();
            items.PrefillTemplates([KeyValuePair.Create(walletId, Template(walletId, ItemKind.Material))]);
            var walletCategory = new AvatarPackageCategory(0, 0, [new(avatarId, 1, 0, 0)])
            {
                MaterialRows = stackableWallet ? [] : [new(walletId, 20)],
                StackableRows = stackableWallet ? [new(walletId, 20)] : []
            };
            avatarPackages[roots[0]] = new(() => new(0, [1, 1], [walletCategory]) { Recognized = true });
            string? walletError = validator.Validate(rules);
            That(walletError is null,
                "装扮钱包" + (stackableWallet ? "stackable" : "etc") +
                "边使用实际钱包owner，不误报typed实体或未连接材料: " + walletError);
            items.PrefillTemplates([KeyValuePair.Create(walletId,
                Template(walletId, ItemKind.Material) with { IsPremiumService = true })]);
            string? walletDenied = validator.Validate(rules);
            That(walletDenied?.Contains("avatar_wallet_row_definition", StringComparison.Ordinal) == true &&
                walletDenied.Contains(walletId.ToString(), StringComparison.Ordinal),
                "钱包边仍先检查真实模板，不用RewardKind快捷返回绕过付费服务守卫");
        }
    }

    static void RealFixedDisjointParents(ItemResources resources, PromotionConfiguration[] shipped)
    {
        foreach (int parentId in new[] { 490011382, 490012852 })
        {
            var original = shipped.Single();
            var configuration = original with
            {
                Enabled = true,
                Rules = original.Rules with
                {
                    Tiers = original.Rules.Tiers.SetItem(0, original.Rules.Tiers[0] with { ItemId = parentId, Count = 1 })
                }
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources, JsonSerializer.Serialize(new[] { configuration }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                $"已支持固定分解声明父盒及全部真实自选子盒经正式活动loader可用 {parentId}: {entry.Error}");
        }
    }

    static void RealEmptySelectionCoordinates(ItemResources resources)
    {
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, randomOptions: resources.RandomOptions, attributePairs: resources.AttributePairs);
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, categoryCount, emptyCount) in new[] { (490705401, 84, 23), (490705453, 80, 19) })
        {
            var definition = resources.Boosters.Get(id);
            var selection = definition.Selection ?? throw new InvalidDataException("真实白金徽章源缺少分类: " + id);
            var effective = selection.Categories.GroupBy(category => (category.Category, category.Subcategory))
                .Select(group => group.Last()).ToArray();
            That(definition.Available && selection.SelectionCount == 1 && selection.Categories.Length == categoryCount &&
                effective.Length == categoryCount && effective.Count(category => category.Rewards.IsEmpty) == emptyCount &&
                effective.All(category => category.PendingRule is null) && effective.Sum(category => category.Rewards.Length) == 973,
                "真实白金徽章源保留全部静态职业坐标、空槽和973行，不虚构动态候选 " + id);
            var source = new DfoServer.Game.Inventory.Models.InventoryItem(1,
                new(DfoServer.Game.Inventory.Models.InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(resources.Items.Get(id), utcNow: now));
            var role = new DfoServer.Game.Characters.Character(1, 0, "RealEmptySelection", 0, new(1, 1, 0, 0), null)
                { Job = 0, Level = 95, Inventory = new([source], Gold: 12345) };
            That(!source.IsExpired(now.ToUnixTimeSeconds()) &&
                InventoryAvatarPackageService.SourceEligible(role, source, resources.Items.Get(id)),
                "真实分类用例先证明源完整实例合法，空选择拒绝不借无效源通过 " + id);
            int attempted = 0;
            foreach (var category in effective)
            {
                foreach (var choice in category.Rewards)
                {
                    attempted++;
                    var template = resources.Items.Get(choice.ItemId);
                    That(template.Kind == ItemKind.Emblem && template.EmblemTargetSockets != 0 && choice.Count == 1 &&
                        choice.Upgrade == 0 && choice.Refinement == 0 && choice.UpgradeChances.IsEmpty,
                        "每条原行是可由真实镶嵌消费者接收的确定白金徽章 " + id + ":" + choice.ItemId);
                    var result = service.Open(role, new(65, 1,
                        new(category.Category, category.Subcategory, [choice.ItemId])), out var receipt, now);
                    var expected = InventoryRewardGrantService.Create(template, choice.Count, now);
                    That(result.Accepted && receipt is { Slot: 65 } && receipt.ItemId == id &&
                        receipt.Rewards is [{ } reward] && reward.ItemId == choice.ItemId && reward.Count == choice.Count &&
                        result.Plan!.Before == role.Inventory && result.Plan.After.Items.Length == 1 &&
                        result.Plan.After.Items.Single().Core == expected &&
                        result.Plan.After.Items.Single().Position == new DfoServer.Game.Inventory.Models.InventorySlot(
                            DfoServer.Game.Inventory.Models.InventoryListType.Main, 289) &&
                        result.Plan.After.Gold == 12345 && role.Inventory.Items.Single() == source && role.Inventory.Gold == 12345,
                        "全部原奖励行实际Open保完整Core、数量、徽章槽及单扣源，不改输入或钱包 " + id + ":" + attempted);
                }
                if (!category.Rewards.IsEmpty) continue;
                foreach (int[] ids in new[] { Array.Empty<int>(), new[] { 2540007 } })
                {
                    var denied = service.Open(role, new(65, 1,
                        new(category.Category, category.Subcategory, ids)), out var receipt, now);
                    That(!denied.Accepted && denied.Plan is null && receipt is null &&
                        role.Inventory.Items.Single() == source && role.Inventory.Gold == 12345,
                        "真实空职业坐标拒绝空/伪造选择，不扣源、不发奖、不改钱包 " + id + ":" + category.Category + ":" + category.Subcategory);
                }
            }
            That(attempted == 973, "真实源全部973条原始奖励行均执行Open " + id);
        }
    }

    static ItemTemplate Template(int id, ItemKind kind, byte slot = 0) => new(id, kind, slot, 1, 0,
        kind is ItemKind.Equipment or ItemKind.Avatar ? 1 : 1000, 0, 0, 0, "[free]", ushort.MaxValue, 0, "consumer fixture");
}
