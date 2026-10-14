using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PremiumContractTests
{
    public static void RunCore()
    {
        var catalog = PremiumContractCatalog.Parse("""
            [type] 22
            [target item] 35
            [item] 30 [term] 10 [/term] [/item]
            [item] 10096109 [term] 60 [/term] [is term unit minute] 1 [/is term unit minute] [/item]
            [term] 1
            [type] 27
            [target item] 47
            [item] 43 [term] 3 [is term unit minute] 0 [/item]
            [term] 1
            [type] 84
            [target item] 2660408
            [item] 2660410 [term] 7 [/item]
            [type] 0
            [target item] 999 [term] 10
            """);
        That(catalog.Get(35) == new PremiumContract(35, 22, 86400) && catalog.Get(47) == new PremiumContract(47, 27, 86400),
            "父target只取父期限，不串读前置子物品期限或分钟标志");
        That(catalog.Get(30) == new PremiumContract(30, 22, 864000) && catalog.Get(10096109) == new PremiumContract(10096109, 22, 3600) &&
            catalog.Get(43) == new PremiumContract(43, 27, 259200), "子条目保留各自期限，分钟标志0保持天单位");
        That(catalog.Get(2660408) is null && catalog.Get(2660410) == new PremiumContract(2660410, 84, 604800) && catalog.Get(999) is null,
            "缺失父期限不能由子期限补全，零类型不建立契约");
        VerifyBenefitFields();
        VerifyRedeemableEffects();
        That(ItemCatalog.IsPremiumStackable(new PvfDocument("[stackable type]\n`[contract]` 0\n")) &&
            !ItemCatalog.IsPremiumStackable(new PvfDocument("[stackable type]\n`[etc]` 0\n[name]\n`[contract]`\n")) &&
            !ItemCatalog.IsPremiumStackable(new PvfDocument("[stackable type]\n`[booster]` 0\n")),
            "明确契约类型在缺服务映射时仍识别为权益，普通etc、礼包及名称不能误判");
        Reject<InvalidDataException>(() => PremiumContractCatalog.Parse("[type] 22 [target item] 35 [term] 1 [item] 35 [term] 2 [/item]"),
            "相同物品的冲突期限不能被后段静默覆盖");
        Reject<InvalidDataException>(() => PremiumContractCatalog.Parse("[type] 22 [target item] 35 [term] 1 [is term unit minute] 2"),
            "未知期限单位不能静默转为分钟");
        var projectionWallet = AccountWallet.Empty with { Contracts = System.Collections.Immutable.ImmutableSortedDictionary<int, long>.Empty
            .Add(22, 1000).Add(27, 1010).Add(97, 999).Add(131, 1001) };
        Bytes(PremiumPackets.SelectEntries(projectionWallet, DateTimeOffset.FromUnixTimeSeconds(1000)),
            [2, 27, 10, 0, 0, 0, 0, 0, 0, 0, 131, 1, 0, 0, 0, 0, 0, 0, 0],
            "1760逐项按类型排序，使用u8/u8/i64布局，精确到期及已过期服务不发送");
        Bytes(PremiumPackets.SelectEntries(AccountWallet.Empty, DateTimeOffset.FromUnixTimeSeconds(1000)), [0], "空契约列表保留数量字段");
        var devil = projectionWallet.SetContractExpiry(58,2000);
        That(devil.ContractExpiry(58) == 2000 && devil.ContractExpiry(27) == 1010 && devil.Revision == projectionWallet.Revision &&
            devil.SetContractExpiry(58,0).PremiumContracts.SequenceEqual(projectionWallet.PremiumContracts),
            "58设置/关闭沿统一不可变契约快照，仅改本类型，版本由原持久化事务推进");
        Reject<InvalidDataException>(() => devil.SetContractExpiry(132,2000),"契约GM设置不得越过匹配客户端类型边界");
        Reject<InvalidDataException>(() => devil.SetContractExpiry(58,-1),"账号契约不能保存负期限");
        That(!PremiumContractCatalog.IsConsumableContract(56) && !PremiumContractCatalog.IsConsumableContract(58),
            "56/58显示资格接入不擅自开放未确认的商城或实体服务能力");
        Reject<InvalidDataException>(() => PremiumPackets.SelectEntries(projectionWallet with
        { Contracts = projectionWallet.PremiumContracts.Add(580, 1010) }, DateTimeOffset.FromUnixTimeSeconds(1000)),
            "超出客户端类型边界的活动服务不推测合并成另一种权益");

        var position = new InventorySlot(InventoryListType.Main, 65);
        var request = new ConsumableUseRequest(position, 0, 43, 0);
        var template = new ItemTemplate(43, ItemKind.Consumable, 0, 1, 0, 1000, 0, 2, 1, "[trade]", ushort.MaxValue, 0, "达人契约", "etc");
        var contract = new PremiumContract(43, 27, 86400);
        var usable = new PremiumContractCatalog(new Dictionary<int, PremiumContract> { [contract.ItemId] = contract }, expertSkillLevels: 2);
        var core = new ItemCore(43, ItemKind.Consumable, 2) { SortLockFlag = 1, Marker16 = 77, ExpireTime = 2000 };
        Character character = new(1, 0, "Contract", 0, new(1, 0, 10, 10), null)
        {
            Level = 1,
            Inventory = new([new(11, position, core)])
            { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 100) }
        };
        InventoryResult Plan(Character candidate, ConsumableUseRequest use) => InventoryPremiumContractUseService.Plan(candidate, use, 1000, usable, template);
        var result = Plan(character, request);
        var plan = result.Plan!;
        That(result.Accepted && plan.Changed && plan.CharacterPrecondition!.Matches(character) &&
            plan.After.Items.Single() == new InventoryItem(11, position, core with { Value = 1 }) && character.Inventory.Find(position)!.Core == core,
            "契约使用规划只扣原持久UID一件，保留排序锁和核心，源快照不变");
        That(!plan.CharacterPrecondition!.Matches(character with { Revision = character.Revision + 1 }) &&
            !plan.CharacterPrecondition.Matches(character with { Level = 2 }), "旧revision或资格变更不能重新绑定旧契约规划");
        That(plan.After.AccountWallet.ContractExpiry(27) == 87400 && plan.After.AccountWallet.Get(CurrencyKind.Cera) == 100 &&
            plan.After.AccountWallet.PremiumContracts.Count == 1 && character.Inventory.AccountWallet.PremiumContracts.Count == 0,
            "只激活源物品真实契约类型，不扣点券或激活其他账号服务");
        var last = Plan(character with { Inventory = plan.After }, request with { OpaqueCheck = uint.MaxValue });
        That(last.Accepted && last.Plan!.After.Items.IsEmpty && last.Plan.After.AccountWallet.ContractExpiry(27) == 173800,
            "最后一件清槽，已有有效契约从原到期时间续期，完整u32令牌保留");
        var expiredWallet = character.Inventory.AccountWallet.ActivateContract(27, 1, DateTimeOffset.FromUnixTimeSeconds(1));
        That(Plan(character with { Inventory = character.Inventory with { AccountWallet = expiredWallet } }, request).Plan!.After.AccountWallet.ContractExpiry(27) == 87400,
            "已到期契约从本次服务端时间续期");
        foreach (var invalid in new[] { request with { ItemId = 30 }, request with { Argument = 1 },
            request with { Position = new(InventoryListType.PersonalCargo, 0) }, request with { Position = new(InventoryListType.Main, 1) } })
            That(!Plan(character, invalid).Accepted, "错误模板、参数、仓库或错误物品页拒绝，不改资产");
        foreach (var invalid in new[] { character with { Inventory = InventorySnapshot.Empty },
            character with { Inventory = new([new(0, position, core)]) },
            character with { Inventory = new([new(11, position, core with { EquipmentLockId = 1 })]) },
            character with { Inventory = new([new(11, position, core with { ExpireTime = 1000 })]) },
            character with { Inventory = new([new(11, position, core with { Value = 0 })]) } })
            That(!Plan(invalid, request).Accepted, "空槽、临时UID、安全锁、期限边界和零数量不能消费");
        foreach (var invalid in new[] { template with { MinimumLevel = 2 }, template with { Jobs = 2 }, template with { UseForbidden = true },
            template with { Id = 30 }, template with { Kind = ItemKind.Material } })
            That(!InventoryPremiumContractUseService.Plan(character, request, 1000, usable, invalid).Accepted,
                "真实模板的等级、职业、禁止使用和类别独立参与资格");
        That(!InventoryPremiumContractUseService.Plan(character, request, 1000,
            new PremiumContractCatalog(new Dictionary<int, PremiumContract> { [contract.ItemId] = contract with { Type = 1 } }, expertSkillLevels: 2), template).Accepted,
            "带其他奖励的premium服务不能按普通契约只续期后吞掉源物品");
        var overflow = character with { Inventory = character.Inventory with { AccountWallet = character.Inventory.AccountWallet with
        { Contracts = System.Collections.Immutable.ImmutableSortedDictionary<int, long>.Empty.Add(27, long.MaxValue) } } };
        That(!Plan(overflow, request).Accepted && overflow.Inventory.Find(position)!.Core.Count == 2,
            "到期时间溢出整体拒绝，不先消费源物品");
        var shortcut = character with { Inventory = character.Inventory with { Items = [new(11, new(InventoryListType.Main, 3), core)] } };
        That(Plan(shortcut, request with { Position = new(InventoryListType.Main, 3) }).Accepted,
            "真实快捷栏源按同一契约规划消费，不强制搬回消耗品页");
    }

    public static void RunResources(ItemResources resources, PvfArchive archive)
    {
        string source = archive.ReadText("etc/premiumlist_new.etc");
        var original = PremiumContractCatalog.Parse(source, archive.ReadText("etc/premiumserviceeffect.etc"));
        That(original.ExpertSkillLevels == resources.Contracts.ExpertSkillLevels &&
            original.ConquerorEquipmentLevels.OrderBy(pair => pair.Key).SequenceEqual(resources.Contracts.ConquerorEquipmentLevels.OrderBy(pair => pair.Key)) &&
            original.ConquerorEquipmentGroups.Count == resources.Contracts.ConquerorEquipmentGroups.Count &&
            original.ConquerorEquipmentGroups.All(pair => pair.Value.SequenceEqual(resources.Contracts.ConquerorEquipmentGroups[pair.Key])),
            "资源装配发布的契约等级权益与当前PVF父字段一致");
        int changedSkill = Different(original.ExpertSkillLevels);
        var changedEquipment = original.ConquerorEquipmentLevels.ToDictionary(pair => pair.Key, pair => Different(pair.Value));
        string changed = ReplaceParentBenefit(source, 27, "over skill", changedSkill.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string equipmentValues = string.Join(" ", new[] { changedEquipment.Count }.Concat(changedEquipment.OrderBy(pair => pair.Key)
            .SelectMany(pair => new[] { pair.Key, pair.Value })));
        changed = ReplaceParentBenefit(changed, 22, "over equipinfo", equipmentValues);
        var reparsed = PremiumContractCatalog.Parse(changed);
        That(reparsed.ExpertSkillLevels == changedSkill && reparsed.ConquerorEquipmentLevels.OrderBy(pair => pair.Key)
            .SequenceEqual(changedEquipment.OrderBy(pair => pair.Key)) && reparsed.ByItem.OrderBy(pair => pair.Key).SequenceEqual(original.ByItem.OrderBy(pair => pair.Key)),
            "实际PVF父权益数值改变后重新读取，全部消费映射和父子期限仍保持原值");
        foreach (var (item, type) in new[] { (35, 22), (47, 27), (2660408, 84), (10000387, 97) })
            That(resources.Contracts.Get(item) == new PremiumContract(item, type, 86400), "匹配契约父目标均使用父声明1日");
        foreach (var (item, type) in new[] { (10096109, 22), (10096110, 27), (10096112, 84), (10096113, 97) })
            That(resources.Contracts.Get(item) == new PremiumContract(item, type, 3600), "匹配一分钟单位子条目60分钟，不污染父目标");
        That(resources.Contracts.Get(2660012) == new PremiumContract(2660012, 22, 2592000) &&
            resources.Contracts.Get(2660013) == new PremiumContract(2660013, 27, 2592000) &&
            resources.Contracts.Get(2683457) == new PremiumContract(2683457, 84, 2592000), "原长周期子条目继续保留30日");
        var service = new InventoryPremiumContractUseService(resources.Items, resources.Contracts);
        foreach (int item in new[] { 30, 35, 43, 47 })
            That(service.TryDefinition(item, out _) && resources.Items.Get(item) is { Kind: ItemKind.Consumable, MinimumLevel: 1, Jobs: ushort.MaxValue, UseForbidden: false },
                "匹配霸王/达人具备声明完整的效果和可用物品模板，[etc]不等于未知药剂");
        foreach (int item in new[] { 2660408, 10000387 })
            That(resources.Contracts.Recognizes(item) && resources.Contracts.Get(item) is not null && !service.TryDefinition(item, out _),
                "成长/晶之新获取即时激活，不扫描或处理遗留背包实体");
        foreach (int item in new[] { 9, 11, 137, 1108, 1114 })
            That(!service.TryDefinition(item, out _), "会员赠品、拍卖服务和普通药剂不进入已实现契约消费入口");
        That(resources.Contracts.ByItem.OrderBy(pair => pair.Key).SequenceEqual(original.ByItem.OrderBy(pair => pair.Key)) &&
            resources.Contracts.RecognizedItems.SetEquals(original.RecognizedItems),
            "全部原期限映射及无正期限的直接服务识别均来自当前PVF，不按支持效果过滤原始身份表");

        static int Different(int value) => value == int.MaxValue ? 0 : value + 1;
    }

    static void VerifyBenefitFields()
    {
        var fields = PremiumContractCatalog.Parse("""
            [type] 27
            [item] 901 [term] 3 [over skill] 91 [/item]
            [over skill] 8
            [type] 22
            [item] 902 [term] 2 [over equipinfo] 1 1 99 [/item]
            [over equipinfo] 3 1 2 3 7 9 13
            """);
        That(fields.ExpertSkillLevels == 8 && fields.ConquerorEquipmentLevels.Count == 3 &&
            fields.ConquerorEquipmentLevels[1] == 2 && fields.ConquerorEquipmentLevels[3] == 7 && fields.ConquerorEquipmentLevels[9] == 13 &&
            fields.Get(901)!.DurationSeconds == 259200 && fields.Get(902)!.DurationSeconds == 172800,
            "父等级参数保留变化值与原始组键，子字段不污染父权益或原期限解析");
        var zero = PremiumContractCatalog.Parse("[type] 27 [over skill] 0 [type] 22 [over equipinfo] 2 0 0 7 28");
        That(zero.ExpertSkillLevels == 0 && zero.ConquerorEquipmentLevels[0] == 0 && zero.ConquerorEquipmentLevels[7] == 28,
            "零权益和不同数值组不退回固定技能或装备加成");
        var missing = PremiumContractCatalog.Parse("[type] 27 [item] 903 [term] 1 [over skill] 99 [/item] " +
            "[type] 22 [item] 904 [term] 1 [over equipinfo] 1 2 99 [/item]");
        That(missing.ExpertSkillLevels == 0 && missing.ConquerorEquipmentLevels.Count == 0,
            "缺少父权益字段时保留零/空，不能用子物品配置或固定参数补全");
        var other = PremiumContractCatalog.Parse("[type] 1 [target item] 905 [term] 1 [over skill] 99 [over equipinfo] 1 1 99");
        That(other.ExpertSkillLevels == 0 && other.ConquerorEquipmentLevels.Count == 0 && !other.TryConsumableContract(905, out _),
            "未知服务的同名字段不激活其他类型权益，也不扩大可消费服务名单");
        var strings = PremiumContractCatalog.Parse("[type] 27 [name] `[over skill] 999` [over skill] 6 " +
            "[type] 22 [name] `[over equipinfo] 1 1 999` [over equipinfo] 0");
        That(strings.ExpertSkillLevels == 6 && strings.ConquerorEquipmentLevels.Count == 0, "名称字符串里的标签不作为等级字段");
        foreach (string bad in new[] { "", "-1", "1.5", "word", "2147483648", "1 2", "`1`", "1 `2`" })
            Reject<InvalidDataException>(() => PremiumContractCatalog.Parse("[type] 27 [over skill] " + bad),
                "达人字段必须是唯一非负整数，不接受非整数、尾段或溢出");
        foreach (string bad in new[] { "", "-1", "1 1", "2 1 3", "1 1 3 2", "1 -1 4", "1 2 -1", "1 1.5 3", "1 1 2.5",
            "1 1 word", "2 1 3 1 4", "2147483647", "0 1 2", "1 1 2147483648", "1 `1` 3" })
            Reject<InvalidDataException>(() => PremiumContractCatalog.Parse("[type] 22 [over equipinfo] " + bad),
                "霸王字段校验完整计数、非负整数、唯一组编号与整数上界");
        foreach (string bad in new[] { "[type] 27 [over skill] 1 [over skill] 1", "[type] 27 [over skill] 1 [type] 27 [over skill] 1",
            "[type] 22 [over equipinfo] 1 1 2 [over equipinfo] 1 1 2", "[type] 22 [over equipinfo] 1 1 2 [type] 22 [over equipinfo] 1 1 2" })
            Reject<InvalidDataException>(() => PremiumContractCatalog.Parse(bad), "重复父权益字段或重复服务声明不能静默覆盖");
        var dictionary = new Dictionary<int, int> { [7] = 18 };
        var frozen = new PremiumContractCatalog(new Dictionary<int, PremiumContract>(), 12, dictionary);
        dictionary[7] = 40;
        That(frozen.ExpertSkillLevels == 12 && frozen.ConquerorEquipmentLevels[7] == 18, "公开权益是冻结只读值，不共享外部可变字典");
        Reject<InvalidDataException>(() => new PremiumContractCatalog(new Dictionary<int, PremiumContract>(), -1), "构造注入也拒绝负技能等级");
        Reject<InvalidDataException>(() => new PremiumContractCatalog(new Dictionary<int, PremiumContract>(), 0, new Dictionary<int, int> { [-1] = 2 }),
            "构造注入也拒绝负组编号");
        Reject<InvalidDataException>(() => new PremiumContractCatalog(new Dictionary<int, PremiumContract>(), 0, new Dictionary<int, int> { [1] = -2 }),
            "构造注入也拒绝负装备等级");
    }

    static void VerifyRedeemableEffects()
    {
        var rules = PremiumContractCatalog.Parse("""
            [overequip list]
            1 `[weapon]` 1 `[coat]` 1 `[hat avatar]`
            [/overequip list]
            [type] 22 [target item] 910 [term] 1
            [over equipinfo] 3 1 3 2 7 3 0
            [type] 27 [target item] 911 [term] 2 [over skill] 6
            [type] 84 [target item] 912 [term] 3
            [type] 97 [target item] 913 [term] 4
            """);
        That(rules.HasConquerorEquipmentRule && rules.ConquerorRuleError is null && rules.HasExpertSkillRule &&
            rules.TryConsumableContract(910, out _) && rules.TryConsumableContract(911, out _) &&
            rules.ConquerorEquipmentGroups[1].SequenceEqual(new[] { "[weapon]" }) &&
            rules.ConquerorEquipmentGroups[2].SequenceEqual(new[] { "[coat]" }) &&
            rules.ConquerorEquipmentGroups[3].SequenceEqual(new[] { "[hat avatar]" }),
            "顶层名单保留原始标签，以本服一基列表序号装配明确可兑现的22/27效果");
        That(rules.EquipmentLevelAllowance(ItemKind.Equipment, 12) == 3 && rules.EquipmentLevelAllowance(ItemKind.Equipment, 14) == 7 &&
            rules.EquipmentLevelAllowance(ItemKind.Avatar, 0) == 0 && rules.EquipmentLevelAllowance(ItemKind.Equipment, 13) == 0 &&
            rules.EquipmentLevelAllowance(ItemKind.Avatar, 9) == 0 && rules.EquipmentLevelAllowance(ItemKind.Avatar, 10) == 0 &&
            rules.EquipmentLevelAllowance(ItemKind.Avatar, 11) == 0 && rules.EquipmentLevelAllowance(ItemKind.Creature, 26) == 0 &&
            rules.EquipmentLevelAllowance(ItemKind.GuildMedal, 32) == 0,
            "各声明类型使用各组真实参数；未声明称号、光环、武器装扮、宠物和勋章不取最大或首组加成");
        var template = new ItemTemplate(916, ItemKind.Equipment, 14, 20, 30, 1, 0, 2, 1, "[free]", ushort.MaxValue, 0, "合成上衣");
        That(rules.EquipmentLevelAllowance(template) == 7 && rules.Recognizes(912) && rules.Recognizes(913) &&
            rules.Get(912) is not null && rules.Get(913) is not null && !rules.TryConsumableContract(912, out _) && !rules.TryConsumableContract(913, out _) &&
            !PremiumContractCatalog.IsConsumableContract(84) && !PremiumContractCatalog.IsConsumableContract(97),
            "模板查询按kind/真实部位，未接入成长/晶之效果仅保留身份和期限而不消费");
        var zero = PremiumContractCatalog.Parse("[overequip list] 1 `[weapon]` [/overequip list] " +
            "[type] 22 [target item] 917 [term] 1 [over equipinfo] 1 1 0 [type] 27 [target item] 918 [term] 1 [over skill] 0");
        That(zero.HasExpertSkillRule && zero.ExpertSkillLevels == 0 && zero.TryConsumableContract(918, out _) &&
            zero.TryConsumableContract(917, out _) && zero.EquipmentLevelAllowance(ItemKind.Equipment, 12) == 0,
            "显式零参数仍是完整规则，不等同字段缺失，也不退回固定业务数值");
        foreach (string text in new[]
        {
            "[type] 22 [target item] 919 [term] 1 [over equipinfo] 1 1 3",
            "[overequip list] 1 `[weapon]` [/overequip list] [type] 22 [target item] 919 [term] 1",
            "[type] 22 [target item] 919 [term] 1 [over equipinfo] 1 1 3 [overequip list] 1 `[weapon]` [/overequip list]",
            "[overequip list] 1 `[weapon]` [/overequip list] [type] 22 [target item] 919 [term] 1 [over equipinfo] 1 2 3",
            "[overequip list] 1 `[weapon]` [/overequip list] [type] 22 [target item] 919 [term] 1 [over equipinfo] 1 0 3",
            "[overequip list] 1 `[future equipment]` [/overequip list] [type] 22 [target item] 919 [term] 1 [over equipinfo] 1 1 3",
            "[overequip list] 1 `[creature]` [/overequip list] [type] 22 [target item] 919 [term] 1 [over equipinfo] 1 1 3",
            "[overequip list] 2 `[weapon]` `[weapon]` [/overequip list] [type] 22 [target item] 919 [term] 1 [over equipinfo] 1 1 3",
            "[overequip list] 1 `[weapon]` 1 `[weapon]` [/overequip list] [type] 22 [target item] 919 [term] 1 [over equipinfo] 2 1 3 2 7",
            "[overequip list] 0 [/overequip list] [type] 22 [target item] 919 [term] 1 [over equipinfo] 1 1 3"
        })
        {
            var unavailable = PremiumContractCatalog.Parse(text);
            That(unavailable.Recognizes(919) && unavailable.Get(919) is not null && unavailable.ConquerorRuleError is not null &&
                !unavailable.TryConsumableContract(919, out _) && unavailable.EquipmentLevelAllowance(ItemKind.Equipment, 12) == 0,
                "缺名单、未知组、不可映射或重复类型必须明确拒绝整项霸王效果，不能过滤后部分激活");
        }
        var missingExpert = PremiumContractCatalog.Parse("[type] 27 [target item] 920 [term] 1 [item] 921 [term] 1 [over skill] 6 [/item]");
        That(!missingExpert.HasExpertSkillRule && missingExpert.Recognizes(920) && missingExpert.Get(920) is not null &&
            !missingExpert.TryConsumableContract(920, out _) && !missingExpert.TryConsumableContract(921, out _),
            "缺少父达人效果时不能以子物品参数或期限映射宣称能力已兑现");
        var noTerm = PremiumContractCatalog.Parse("[type] 27 [target item] 922 [over skill] 3 [item] 923 [term] 0 [/item] " +
            "[type] 1 [target item] 924 [term] 0 [name] `[target item] 925 [item] 926 [term] 1 [/item] [type] 27 [target item] 927 [term] 1 [over skill] 8`");
        That(noTerm.Recognizes(922) && noTerm.Recognizes(923) && noTerm.Recognizes(924) && !noTerm.Recognizes(925) &&
            !noTerm.Recognizes(926) && !noTerm.Recognizes(927) && noTerm.ExpertSkillLevels == 3 &&
            noTerm.Get(922) is null && noTerm.Get(923) is null && noTerm.Get(926) is null && !noTerm.TryConsumableContract(922, out _),
            "无正期限的直接服务仍按PVF声明识别并拒绝消费，字符串里的伪物品标签不建立身份");
        var injected = new PremiumContractCatalog(new Dictionary<int, PremiumContract> { [926] = new(926, 27, 3600) }, recognizedItems: [927]);
        That(injected.Recognizes(926) && injected.Recognizes(927) && !injected.TryConsumableContract(926, out _),
            "构造身份表默认含映射键，额外无期限身份保留，缺效果规则不自动激活");
        foreach (string body in new[] { "-1", "1", "2 `[weapon]`", "x `[weapon]`", "1 [weapon]", "1 `weapon`", "1 `[weapon]` tail" })
            Reject<InvalidDataException>(() => PremiumContractCatalog.Parse("[overequip list] " + body + " [/overequip list] [type] 22 [over equipinfo] 1 1 3"),
                "顶层名单必须满足每组完整计数和PVF标签行宽");
        Reject<InvalidDataException>(() => PremiumContractCatalog.Parse("[overequip list] 1 `[weapon]` [type] 22 [over equipinfo] 1 1 3"),
            "顶层名单缺少闭合不能静默当作缺省");
        Reject<InvalidDataException>(() => PremiumContractCatalog.Parse("[overequip list] 1 `[weapon]` [/overequip list] " +
            "[overequip list] 1 `[coat]` [/overequip list] [type] 22 [over equipinfo] 1 1 3"), "重复顶层名单不能取第一份");
    }

    static string ReplaceParentBenefit(string source, int type, string tag, string values)
    {
        var block = Regex.Matches(source, @"\[type\]\s*(\d+)(?<body>.*?)(?=\[type\]|\z)", RegexOptions.Singleline)
            .Cast<Match>().Single(match => int.Parse(match.Groups[1].Value) == type);
        string body = block.Groups["body"].Value;
        var children = Regex.Matches(body, @"\[item\].*?\[/item\]", RegexOptions.Singleline).Cast<Match>().ToArray();
        var field = Regex.Matches(body, @"\[" + Regex.Escape(tag) + @"\](?<values>[^\[]*)")
            .Cast<Match>().Single(match => !children.Any(child => match.Index >= child.Index && match.Index < child.Index + child.Length));
        var span = field.Groups["values"];
        int at = block.Groups["body"].Index + span.Index;
        return source[..at] + "\n" + values + "\n" + source[(at + span.Length)..];
    }

    internal static (ushort Slot, ushort PeerSlot, int NormalSp) VerifyExpertAccounts(string database, string account,
        TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, ItemResources resources)
    {
        var repository = new CharacterRepository(database);
        using var owner = new AccountOwners().Acquire(account, () => { });
        var candidate = SkillRepositoryTests.Fixture("PaidExpert", towns, characters, learning);
        var initial = SkillPointLedger.Synchronize(0, 0, candidate.Level, candidate.InitialLearning with { BonusSp = 137, BonusTp = 0 }, learning);
        candidate = candidate with { InitialLearning = initial, AcceptedStoryLevel = 1, Inventory = candidate.Inventory with
        { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65),
            InventoryRewardGrantService.Create(resources.Items.Get(43), 2))) } };
        var created = repository.Create(owner, candidate);
        var other = repository.Create(owner, candidate with { Name = "ExpertPeer", Inventory = InventorySnapshot.Empty });
        var session = new GameSession(repository, towns, owner);
        session.Select(created.Slot); session.InitializeLearning(learning); session.MarkReady();
        var service = new InventoryPremiumContractUseService(resources.Items, resources.Contracts);
        var activated = session.PlanAndCommitInventory(character => service.Plan(character,
            new(new(InventoryListType.Main, 65), uint.MaxValue, 43, 0), DateTimeOffset.UtcNow.ToUnixTimeSeconds()), 123, requireTown: false);
        long paid = activated.Receipt!.Character.Inventory.AccountWallet.ContractExpiry(27);
        That(activated.Accepted && activated.Receipt.Character.Learning.ExpertContractExpiresAt == paid &&
            session.Selected!.Learning.ExpertContractExpiresAt == paid && repository.LoadCharacter(owner, other.Slot).Learning.ExpertContractExpiresAt == paid,
            "账号钱包提交后立即安装已提交达人期限，同账号另一角色读取同一值，无角色期限副本");
        var beforeGm = session.Selected!;
        var stale = SkillLearning.Plan(beforeGm.Job, beforeGm.GrowType, beforeGm.Level, beforeGm.Learning,
            new(0, 0, [new(5, false, 1)]), learning);
        That(stale.Success, "旧学习CAS代表例先取得有效的普通等级技能规划");
        var longer = session.UpdateProgress(new(CharacterProgressField.ExpertContract, account, created.Name, 172800, 0));
        That(longer.Revision == beforeGm.Revision && longer.Learning.ExpertContractExpiresAt > paid &&
            longer.Inventory.AccountWallet.ContractExpiry(27) == paid, "旧GM账号来源较长时取最大期限，不覆盖购买账本");
        Reject<InvalidOperationException>(() => repository.CommitLearning(owner, beforeGm, stale.State!, stale.TotalSp, stale.TotalTp),
            "同revision的GM账号期限变化仍使旧学习CAS失败，不放宽完整学习前提");
        session.UpdateProgress(new(CharacterProgressField.ExpertContract, account, created.Name, 0, 0));
        That(session.Selected!.Learning.ExpertContractExpiresAt == paid,
            "清除旧GM来源后仍保留已购买账号期限，不误撤销付费权益");
        Sql("UPDATE account_wallets SET premium_contracts=json_set(premium_contracts,'$.\"27\"',NULL) WHERE account=$p0", account);
        Reject<InvalidDataException>(() => repository.LoadCharacter(owner, created.Slot), "显式null是损坏存档，不得默认成缺失的购买期限");
        Sql("UPDATE account_wallets SET premium_contracts=json_set(premium_contracts,'$.\"27\"',$p0) WHERE account=$p1", paid, account);
        session.ReturnToRoster(); session.End();
        Sql("UPDATE account_wallets SET premium_contracts=json_set(premium_contracts,'$.\"27\"',$p0),revision=revision+1 WHERE account=$p1",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1, account);
        int normalSp = learning.TotalSp(candidate.Level) + 137 - learning.Get(0, 5).GetPointCost(1, 0);
        return (created.Slot, other.Slot, normalSp);

        void Sql(string sql, params object[] values)
        {
            using var db = new SqliteConnection("Data Source=" + database + ";Pooling=false");
            db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            for (int index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$p" + index, values[index]);
            command.ExecuteNonQuery();
        }
    }
}
