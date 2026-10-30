using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ConsumableUseTests
{
    internal static RecoveryConsumableDefinition Hp { get; } = new(1108, 16, 10000, 0);
    internal static RecoveryConsumableDefinition Mp { get; } = new(1114, 16, 10000, 1);

    public static void RunCore()
    {
        BossSelectionMappings();
        EpicDropMappings();
        var bossBuff = new BossSelectionBuff(490705256, 5, 2000,
            new(300, 7191, 5, [new(92689, 40), new(92690, 40), new(92697, 20)]));
        bossBuff.Validate();
        That(bossBuff.Allows(7191, false, false, 1999) && !bossBuff.Allows(7191, false, false, 2000) &&
            !bossBuff.Allows(7191, true, false, 1000) && !bossBuff.Allows(7191, false, true, 1000) &&
            !bossBuff.Allows(10, false, false, 1000) && !(bossBuff with { Remaining = 0 }).Active(1000),
            "白斯药效仅单人正式超星空触发，到期等号、组队、练习、普通副本和耗尽均不生效");
        var restoredBuff = System.Text.Json.JsonSerializer.Deserialize<BossSelectionBuff>(System.Text.Json.JsonSerializer.Serialize(bossBuff));
        That(BossSelectionBuff.Same(bossBuff, restoredBuff) && !BossSelectionBuff.Same(bossBuff, bossBuff with { Remaining = 4 }),
            "重读后的完整权重按内容核对，旧次数快照不能提交");
        foreach (var definition in new[]
        {
            new BossSelectionBuffDefinition(1275, 7191, 5, [new(92689, 0), new(92690, 0), new(92697, 100)]),
            new BossSelectionBuffDefinition(1282, 7191, 1, [new(92689, 40), new(92690, 40), new(92697, 20)])
        })
        {
            var effect = bossBuff with { Remaining = definition.Entries, Definition = definition };
            effect.Validate();
            That(effect.Allows(7191, false, false, 1999), "强力五次与普通一次效果保持原单人准入");
            Reject<InvalidDataException>(() => (effect with { Remaining = definition.Entries + 1 }).Validate(), "持久次数不能超过资源上限");
        }
        byte[] body = [65, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0, 0, 0, 0, 0];
        var request = ConsumableUseRequests.Read(body);
        That(request.Position == new InventorySlot(InventoryListType.Main, 65) && request.OpaqueCheck == 0x89ABCDEF && request.ItemId == 1108 && request.Argument == 0,
            "普通44正文按已证15B独立字段解析，不把opaque当时间戳或数量");
        Bytes(ConsumableUsePackets.Ack(request, true), [1, 65, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "成功ACK44完整12B保留原slot/list/check/itemId");
        Bytes(ConsumableUsePackets.Ack(request, false), [0, 123, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "失败ACK44完整12B用error替代slot，仍保留原核验字段");
        Bytes(ConsumableUsePackets.Ack(request, true, noConsumption: true),
            [0, 0, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "原生免扣保留本版u16零错误码、list及token，不传数量或套旧端11B");
        Bytes(ConsumableUsePackets.Ack(request, false, noConsumption: true), ConsumableUsePackets.Ack(request, false),
            "免耗模式不把非法源或冷却拒绝伪装为业务已接受");
        foreach (var bad in new[] { body[..10], body[..14], body.Concat(new byte[] { 0 }).ToArray() })
            Reject<InvalidDataException>(() => ConsumableUseRequests.Read(bad), "普通44不截断兼容特殊宽度或未知尾");
        request = request with { OpaqueCheck = 0x1234 };
        var service = new InventoryConsumableUseService(new([Hp, Mp]));
        var declaration = new StackableFile(1108, "stackable/synthetic.stk", new("[waste]", [4]), 1000, 400, null, 16, "potion", [])
        { DeclaredUsableJobs = ["[all]"], HasUsableJobs = true };
        var raw = new RecoveryConsumableFile(1108, declaration.ScriptPath, declaration, 10000, 0,
            new("+", 410, 1000, "myself"), null, ["stackable type", "usable job", "minimum level", "cool time", "cooltime group", "hp recovery"], []);
        That(RecoveryConsumableCatalog.Read(raw) == Hp && RecoveryConsumableCatalog.Read(raw with { DeclaredFields = raw.DeclaredFields.Add("appendage") }) == Hp &&
            RecoveryConsumableCatalog.Read(raw with { HpRecovery = new("%", 95, 333, "party") }) == Hp &&
            RecoveryConsumableCatalog.Read(raw with { HpRecovery = null, Diagnostics = ["客户端效果形状不由服务端核验"] }) == Hp,
            "服务端只取使用资格及冷却，恢复数值、目标、附加效果和客户端效果格式不构成使用白名单");
        foreach (int subtype in new[] { 0, 1, 4, 5, 6, 99 })
        {
            var other = raw with
            {
                TemplateId = 500001,
                ScriptPath = "stackable/other/place.stk",
                Stackable = declaration with { TemplateId = 500001, TypeDeclaration = new("[waste]", [subtype]) }
            };
            That(RecoveryConsumableCatalog.Read(other) is { ItemId: 500001, MinimumLevel: 16, CoolTime: 10000 },
                "普通waste按大类识别，不按目录、ID、分组名称或子型固定值限制");
        }
        foreach (string category in new[] { "[etc]", "[contract]", "[booster]", "[creature]", "[material]", "[expert town potion]" })
            That(RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { TypeDeclaration = new(category, [4]) } }) is null,
                "其他业务大类不能通过普通消耗应答伪造已实现");
        foreach (string category in new[] { "[throw]", "[set]" })
        {
            That(RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { TypeDeclaration = new(category, [3]) } }) is
                { ItemId: 1108, RequiresDungeon: true, CoolTime: 10000 },
                "投掷及放置按资源大类接入原单件消费，不按特效数据或目录建立白名单");
            That(RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { TypeDeclaration = new(category, [3]),
                HasUseActionPacket = true, DeclaredUseActionPacket = 1 } }) is null,
                "投掷及放置类中另走专用动作请求的道具不能由普通44冒充成功");
        }
        That(RecoveryConsumableCatalog.Read(raw with { SubType = 2, DeclaredFields = raw.DeclaredFields.Add("sub type") }) is
            { EffectsAfterAcknowledgement: true } && RecoveryConsumableCatalog.Read(raw with { SubType = 0 }) is
            { EffectsAfterAcknowledgement: false }, "sub type2的应答后效果不能套发送端即时效果的原生免扣");
        That(RecoveryConsumableCatalog.Read(raw with { DeclaredFields = raw.DeclaredFields.Add("sub type") }) is null,
            "畸形子类型不能当作缺省0而错误选择免扣应答");
        foreach (int id in new[] { 2600020, 2680783, 2749063, 2749165 })
            That(RecoveryConsumableCatalog.Read(raw with
            {
                TemplateId = id, Stackable = declaration with { TemplateId = id },
                StatChangeDuration = new(60000, "myself", null), EffectMaintenance = 7
            }) is { RevivalBlessing: true, EffectMilliseconds: 1200000, EffectMaintenance: 0 },
                "神圣祝福按原生特殊ID建立固定二十分钟单次资格，不借普通属性维护位跨回城或换角保留");
        That(!ItemCatalog.ForbidsUse("[impossible contents]\n`enable` `hardcore mode`\n[/impossible contents]") &&
            ItemCatalog.ForbidsUse("[impossible contents]\n`enable`\n[/impossible contents]") &&
            !ItemCatalog.ForbidsUse("[impossible contents]\n`hardcore mode`\n[/impossible contents]"),
            "impossible contents 的 enable 是开关；只有硬核限制时普通使用仍放行，空开关继续禁止");
        That(RecoveryConsumableCatalog.Read(raw with { CoolTime = -1 }) is null &&
            RecoveryConsumableCatalog.Read(raw with { CoolTime = null }) is null &&
            RecoveryConsumableCatalog.Read(raw with { CoolGroup = null }) is null &&
            RecoveryConsumableCatalog.Read(raw with { DeclaredFields = raw.DeclaredFields.Add("action type") }) is null &&
            RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { HasUseActionPacket = true, DeclaredUseActionPacket = 1 } }) is null,
            "错误扣费元数据、冷却声明和使用其他请求的服务端动作不套普通客户端效果路径");
        var epicRaw = raw with { Stackable = declaration with { TypeDeclaration = new("[expert town potion]", [0]),
                DeclaredActionUsablePlaces = ["[village]", "[seria room]"] },
            StatChangeDuration = new(600000, "myself", null), EffectMaintenance = 15 };
        var epic = new EpicDropBuffDefinition(1136, 100) { ItemIds = ImmutableHashSet.Create(1108, 490000413) };
        That(RecoveryConsumableCatalog.Read(epicRaw, epicBuff: epic) is { EpicDropBuff.Percent: 100, EffectMilliseconds: 600000, EffectMaintenance: 15 },
            "已核固定史诗效果才允许城镇药剂0并沿资源持续时间/维护位");
        That(RecoveryConsumableCatalog.Read(epicRaw, epicBuff: epic with { Percent = 150 }) is null,
            "随机中级倍率不借固定百分百合同放行");
        foreach (var invalid in new[] { epicRaw with { EffectMaintenance = null }, epicRaw with { EffectMaintenance = 0 },
            epicRaw with { StatChangeDuration = new(600000, "party", null) }, epicRaw with { StatChangeDuration = null },
            epicRaw with { Diagnostics = ["invalid field"] }, epicRaw with { Stackable = epicRaw.Stackable with
                { DeclaredActionUsablePlaces = ["[dungeon]"] } } })
            That(RecoveryConsumableCatalog.Read(invalid, epicBuff: epic) is null, "固定史诗药剂拒绝坏持续时间、维护位、使用场景或声明");
        var effectInventory = InventorySnapshot.Empty with { ConsumableStates = [
            new(ConsumableStateKind.Effect, 490000413, 9999999, 15, 253),
            new(ConsumableStateKind.Cooldown, 490000413, 9999999, 7, 253),
            new(ConsumableStateKind.Effect, 7440, 9999999, 15, 99)] };
        var refreshed = ConsumableStatePolicy.Activate(effectInventory, RecoveryConsumableCatalog.Read(epicRaw, epicBuff: epic)!, 1000)!;
        That(refreshed.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect && epic.IsFamilyItem(row.ItemId)) is
                { ItemId: 1108, UntilMilliseconds: 601000 } &&
            refreshed.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Cooldown && row.ItemId == 490000413) &&
            refreshed.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Effect && row.ItemId == 7440) &&
            effectInventory.ConsumableStates.Length == 3,
            "固定史诗同族只覆盖效果期限，不叠加倍率、不删原冷却或无关效果、不改原快照");
        var limited = RecoveryConsumableCatalog.Read(raw with
        {
            Stackable = declaration with
            { HasMaximumLevel = true, DeclaredMaximumLevel = 20, DeclaredUsableJobs = ["[mage]"] }
        })!;
        That(limited.Allows(16, 3) && !limited.Allows(21, 3) && !limited.Allows(16, 0),
            "物品等级与职业仍是服务端使用资格，不与客户端恢复效果混为一谈");
        var core = new ItemCore(1108, ItemKind.Consumable, 2) { SortLockFlag = 1, Marker16 = 44, ExpireTime = 2000 };
        Character character = new(1, 0, "Potion", 7, new(1, 0, 10, 10), null)
        {
            Level = 16,
            Inventory = new([new(11, request.Position, core)])
        };
        var plan = service.Plan(character, request, 1000).Plan!;
        That(plan.CharacterPrecondition!.Matches(character, 1999) && !plan.CharacterPrecondition.Matches(character, 2000),
            "普通消耗计划绑定真实源截止，提交时恰到期不能继续使用");
        That(plan.After.Items.Single() == new InventoryItem(11, request.Position, core with { Value = 1 }) && plan.CharacterPrecondition!.Matches(character, 1000),
            "单件消费仅改原指定UID数量，保留排序锁、期限和全部核心属性及角色资格");
        var last = service.Plan(character with { Inventory = plan.After }, request with { OpaqueCheck = 0 }, 1000);
        That(last.Accepted && last.Plan!.After.Items.IsEmpty, "最后一件清槽且opaque0是合法普通请求");
        That(service.Plan(character, request with { OpaqueCheck = 32767 }, 1000).Accepted &&
            !service.Plan(character, request with { OpaqueCheck = 32768 }, 1000).Accepted,
            "首批普通核验值范围0..32767，不把未知producer高值当成功入口");
        foreach (var invalid in new[] { request with { ItemId = 1114 }, request with { ItemId = 1016 }, request with { Argument = 1 },
            request with { Position = new(InventoryListType.PersonalCargo, 0) }, request with { Position = new(InventoryListType.Main, 1) } })
            That(!service.Plan(character, invalid, 1000).Accepted, "错误源模板、未知药型、参数或持有域拒绝");
        foreach (var invalid in new[] { character with { Level = 15 }, character with { Inventory = InventorySnapshot.Empty },
            character with { Inventory = new([new(11, request.Position, core with { EquipmentLockId = 1 })]) },
            character with { Inventory = new([new(11, request.Position, core with { ExpireTime = 999 })]) } })
            That(!service.Plan(invalid, request, 1000).Accepted, "等级、空槽、安全锁和过期状态不因Consumable类别取得使用资格");
        var shortcut = character with { Inventory = new([new(11, new(InventoryListType.Main, 3), core)]) };
        That(service.Plan(shortcut, request with { Position = new(InventoryListType.Main, 3) }, 1000).Accepted,
            "普通快捷栏是实际源槽，沿同一单件消费规则");
    }

    public static void RunResources(ItemResources resources)
    {
        foreach (var (id, duration, mode, bonus) in new[]
        {
            (490004594, 1800000, EnhancementPotionMode.Reinforce, 1000),
            (10004688, 180000, EnhancementPotionMode.Amplify, 5000),
            (10004745, 180000, EnhancementPotionMode.Amplify, 3000),
            (490001534, 1800000, EnhancementPotionMode.Amplify, 1000),
            (490010213, 600000, EnhancementPotionMode.Amplify, 500),
            (490012609, 600000, EnhancementPotionMode.Amplify, 500),
            (2600025, 1800000, EnhancementPotionMode.Reinforce, 1000),
            (2749959, 1800000, EnhancementPotionMode.Reinforce, 1000),
            (490012267, 1800000, EnhancementPotionMode.Reinforce, 1000),
            (490012268, 1800000, EnhancementPotionMode.Reinforce, 1000)
        })
            That(resources.RecoveryConsumables.TryGet(id, out var potion) && potion.EnhancementPotion is { } effect &&
                effect.Mode == mode && effect.SuccessBonusWeight == bonus && potion.EffectMilliseconds == duration &&
                potion.CoolTime == duration && potion.EffectMaintenance == 7 && potion.CooltimeMaintenance == 7 &&
                ReferenceEquals(effect, resources.RecoveryConsumables.EnhancementPotions[id]),
                "升级药剂消费目录复用同一个已核效果实例，期限与保留位取各自正式STK：" + id);
        RunStaminaResources(resources);
        foreach (int orphan in new[] { 490001355,490001358,490001356,490001359 })
            That(!resources.RecoveryConsumables.TryGet(orphan, out _),
                "未被正式物品索引收录的旧STK不能成为可消费升级药剂：" + orphan);
        That(resources.RecoveryConsumables.TryGet(490000413, out var fixedEpic) && fixedEpic is
            { EpicDropBuff: { BuffId: 1136, Percent: 100 }, EffectMilliseconds: 600000 },
            "现场490000413仅通过明确缺映射兼容接入已核固定1136，保留实际十分钟");
        int[] fixedPeers = [490000413, 490002458, 490003224, 490003595, 490003686, 490004407, 490004506, 490005308,
            490005491, 490005778, 490701639, 490006699, 490006963, 490007961, 490008359, 490008470, 490008903,
            490009005, 490703161, 490009334, 490009727, 490702974];
        That(fixedEpic!.EpicDropBuff!.ItemIds!.SetEquals(fixedPeers) &&
            resources.RecoveryConsumables.EpicDropBuffs.Keys.Order().SequenceEqual(fixedPeers.Order()),
            "十九条闭合固定映射、两条已核缺尾修复和唯一缺映射兼容完整装配，不吞下条");
        foreach (int id in fixedPeers)
            That(resources.RecoveryConsumables.TryGet(id, out var peer) && peer.EpicDropBuff is { BuffId: 1136, Percent: 100 } &&
                peer.EpicDropBuff.IsFamilyItem(490000413) && peer.EffectMilliseconds == (id == 490005778 ? 1800000 : 600000) &&
                peer.EffectMaintenance > 0 && !peer.RequiresDungeon && peer.Allows(95, 0),
                "全部固定1136同类真实药剂沿各自时长与资格，旧版不强求可选事件标签：" + id);
        That(!resources.RecoveryConsumables.TryGet(10008219, out _) && !resources.RecoveryConsumables.TryGet(490703675, out _),
            "随机药剂与星空效果不借两条明确缺尾修复冒充固定1136药剂");
        That(resources.Items.Get(490705256) is { Name: "白斯追击药水", MinimumLevel: 95 } &&
            resources.RecoveryConsumables.TryGet(490705256, out var bossPotion) && bossPotion is
            { MinimumLevel: 95, CoolTime: 60000, BossSelectionBuff: { BuffId: 300, DungeonId: 7191, Entries: 5 } } &&
            bossPotion.BossSelectionBuff.Weights.SequenceEqual(new BossSelectionWeight[] { new(92689, 40), new(92690, 40), new(92697, 20) }),
            "截图490705256由真实STK及两份variousbuff表解释为95级城镇药、40/40/20和5次，不按名称猜规则");
        var use = new InventoryConsumableUseService(resources.RecoveryConsumables);
        int seconds = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (int id in new[] { 10309619, 490011948, 490012146, 490012453, 490012682, 490012695,
            490013034, 490013050, 490013290, 490013596, 490014083, 490704250, 490704357, 490704395,
            490705059, 490705069, 490705106, 490705137, 490705256 })
        {
            bool strong = id is 490012682 or 490705137;
            var template = resources.Items.Get(id);
            That(resources.RecoveryConsumables.TryGet(id, out var extra) && extra is
                { MinimumLevel: 95, CoolTime: 60000, RequiresDungeon: false, UseForbidden: false } &&
                extra.CoolGroup == -id && extra.Jobs == ushort.MaxValue &&
                extra.BossSelectionBuff is { } effect && effect.BuffId == (strong ? 1275 : id == 490705059 ? 1282 : 300) &&
                effect.DungeonId == 7191 && effect.Entries == (id == 490705059 ? 1 : 5) &&
                effect.Weights.SequenceEqual(new BossSelectionWeight[] {
                    new(92689, strong ? 0 : 40), new(92690, strong ? 0 : 40), new(92697, strong ? 100 : 20) }),
                "19款实际来源逐项保留普通、强力和一次药型的完整权重、次数、等级及冷却 " + id);
            That(template.Kind == ItemKind.Consumable, "已绑定白斯药剂保留消耗品类型 " + id);
            That(Enumerable.Range(0, 16).All(job => extra!.Allows(95, (byte)job)), "已绑定白斯药剂允许正常95级角色使用 " + id);
            var position = new InventorySlot(InventoryListType.Main, 3);
            // 本用例只验证正常使用和完整实例保留，不把历史活动截止日期当作药剂识别条件。
            var core = new ItemCore(id, ItemKind.Consumable, 2)
                { SortLockFlag = 1, Marker16 = 44, ExpireTime = checked(seconds + 3600) };
            Character character = new(1, 0, "Chase", 7, new(1, 0, 10, 10), null)
                { Level = 95, Inventory = new([new(11, position, core)]) };
            var request = new ConsumableUseRequest(position, 0, id, 0);
            var planned = use.Plan(character, request, seconds);
            That(planned.Accepted && planned.Plan!.After.Items.Single() == new InventoryItem(11, position, core with { Value = 1 }) &&
                character.Inventory.Items.Single().Core == core && planned.Plan.CharacterPrecondition!.Matches(character, seconds),
                "每款真实药水均经正规Plan仅扣指定UID一件并保留核心、原期限与前置 " + id);
        }
        That(resources.RecoveryConsumables.TryGet(1108, out var hp) && hp == Hp &&
            resources.RecoveryConsumables.TryGet(1114, out var mp) && mp == Mp,
            "匹配PVF两种药只保留最低等级和冷却原值/组，不把恢复效果带入服务端使用定义");
        That(resources.RecoveryConsumables.TryGet(1107, out var lowHp) &&
            lowHp == new RecoveryConsumableDefinition(1107, 6, 10000, 0) &&
            resources.RecoveryConsumables.TryGet(1065, out var flower) &&
            flower == new RecoveryConsumableDefinition(1065, 1, 10000, 1),
            "现场1107入门HP药剂及1065魔力之花沿普通类别读取PVF资格");
        That(resources.RecoveryConsumables.TryGet(1113, out var beginnerMp) && beginnerMp == new RecoveryConsumableDefinition(1113, 6, 10000, 1),
            "现场1113入门MP药剂无需加入ID白名单即可获得普通消费资格");
        That(resources.Items.Get(7440) is { UseForbidden: false, Name: "远古精灵的秘药" } &&
            resources.RecoveryConsumables.TryGet(7440, out var secret) && secret is { ExpBonusPermille: 1000, ExpBonusMilliseconds: 7200000, CoolGroup: 99, CoolTime: 7200000 } &&
            resources.Items.Get(7377).UseForbidden == false && resources.RecoveryConsumables.TryGet(7377, out var older) && older.ExpBonusPermille == 1000 &&
            resources.RecoveryConsumables.TryGet(7429, out var grey) && grey.ExpBonusPermille == 200 && grey.CoolGroup == 99,
            "远古精灵秘药可使用，2小时加成按脚本倍率，冷却组99不与HP/MP的0/1共用");
        foreach (int id in new[] { 1000, 1016, 1047, 1051, 1065, 1107, 1108, 1113, 1114, 1073, 6001 })
            That(resources.RecoveryConsumables.TryGet(id, out var definition) && resources.Items.Get(id).Kind == ItemKind.Consumable &&
                resources.Items.Get(id).MinimumLevel == definition.MinimumLevel,
                "使用目录与现有物品目录实际类别/等级一致，不能替代库存实例校验");
        That(resources.RecoveryConsumables.TryGet(10087825, out var antiWeakness) && antiWeakness is
            { EffectMilliseconds: 1800000, EffectMaintenance: 7, CooltimeMaintenance: 7, ExpBonusPermille: 0 },
            "防虚弱药剂沿通用持续效果定义，时长和三种保留位来自真实PVF，不伪装经验秘药或会员资格");
        That(resources.RecoveryConsumables.TryGet(10087828, out var courage) && courage.EffectMilliseconds == 1800000 &&
            resources.RecoveryConsumables.TryGet(10087916, out var stats) && stats.EffectMilliseconds == 600000,
            "不同目录、子型和客户端属性效果走同一持续状态入口，不把持续时间专用于单个ID");
        That(resources.RecoveryConsumables.TryGet(1073, out var set) && set.RequiresDungeon && set.MinimumLevel == 20 && set.CoolTime == 5000 &&
            resources.RecoveryConsumables.TryGet(6001, out var projectile) && projectile.RequiresDungeon && projectile.MinimumLevel == 12 && projectile.CoolTime == 3000,
            "藤蔓种子set及飞盘throw从当前PVF读取等级与冷却，不解析或伪造客户端物件效果");
        int assistantId = resources.DevilContracts.Assistants.Keys.Order().First();
        That(resources.RecoveryConsumables.TryGet(assistantId, out var assistant) && assistant is
            { SummonsApc: true, EffectsAfterAcknowledgement: true } && !beginnerMp.EffectsAfterAcknowledgement,
            "真实PVF任务助手sub type2等待成功应答，普通1113保留发送端即时效果路径");
        foreach (int id in new[] { 25, 3037, 2660408, int.MaxValue })
            That(!resources.RecoveryConsumables.TryGet(id, out _), "宠物、材料、契约和未知ID不作为普通客户端效果物品消费");
        foreach (int id in new[] { 7789,7790,7791,7792,7793,7794,7795,8004 })
            That(resources.RecoveryConsumables.TryGet(id, out var gift) && gift.Unlimited && gift.MinimumLevel == 70 &&
                gift.CoolTime == 43200000 && gift.TownGiftItem == (id == 8004 ? 8005 : id + 7),
                "高人的恩赐匹配当前PVF原物品与dummy，保留70级资格、十二小时冷却及不扣数量");
        foreach (int id in new[] { 2600020,2680783,2749063,2749165 })
            That(resources.RecoveryConsumables.TryGet(id, out var blessing) && blessing.RevivalBlessing &&
                blessing.RequiresDungeon && blessing.EffectMilliseconds == 1200000,
                "旧端四种神圣祝福药水在当前PVF仍存在，服务端补二十分钟单次复活窗口");
    }

    static void RunStaminaResources(ItemResources resources)
    {
        (int Duration, int[] Ids)[] families =
        [
            (1800000, [10087825,490001415,490002087,490002492,490003164,490003223,490003267,490003594,
                490003693,490003991,490004184,490004314,490004398,490004641,490005155,490005299,490005699,
                490005942,490006116,490006128,490006403,490006573,490006790,490007912,490007926,490008358,
                490008976,490008995,490010881,490700706,490701082,490701255,490701313,490701641,490701808,
                490702160,490702311,490702532,490702879,490702976,490000348,10005909,10005376,10006075,490000113,10006487]),
            (900000, [490000702,490008213,10006572]),
            (600000, [490003621,490003699,490004191,490005202,490005338,490005630,490005756,490006356,
                490006758,490006925,490007035,490007360,490007515,490007894,490008119,490008441,490008541,
                490009118,490009722,490009983,490010141,490010243,490701627,490701705,490702200]),
            (3600000, [490007908,490007910]), (540000, [490702714])
        ];
        That(families.Sum(family => family.Ids.Length) == 77, "防虚弱正式资源证据覆盖七十七个明确编号");
        foreach (var family in families)
            foreach (int id in family.Ids)
                That(ConsumableSpecialItems.IsStaminaRecovery(id) && resources.RecoveryConsumables.TryGet(id, out var potion) &&
                    potion.EffectMilliseconds == family.Duration && potion.CoolTime == family.Duration &&
                    potion.EffectMaintenance == 7 && potion.CooltimeMaintenance == 7 && potion.ExpBonusPermille == 0,
                    "全部活动防虚弱使用各自九分钟、十分钟、十五分钟、三十分钟或一小时原期限：" + id);
        That(!ConsumableSpecialItems.IsStaminaRecovery(490003620) && !ConsumableSpecialItems.IsStaminaRecovery(490000413),
            "邻接编号和史诗药不能借固定防虚弱清单取得免虚弱资格");
    }

    static void BossSelectionMappings()
    {
        var ordinary = new BossSelectionBuffDefinition(300, 7191, 5, [new(92689, 40), new(92690, 40), new(92697, 20)]);
        var strong = new BossSelectionBuffDefinition(1275, 7191, 5, [new(92689, 0), new(92690, 0), new(92697, 100)]);
        var definitions = new Dictionary<int, BossSelectionBuffDefinition> { [300] = ordinary, [1275] = strong };
        int[] compatibility = [490012146, 490012453, 490012695, 490704250, 490704357, 490704395];
        var absent = BossSelectionBuffDefinition.ParseMappings([], definitions);
        That(absent.Count == 6 && compatibility.All(id => absent[id] == ordinary) && !absent.ContainsKey(500001),
            "仅六个明确缺映射的普通药水复用已验证300，未知药剂不按类别或名称扩展");
        That(BossSelectionBuffDefinition.ParseMappings([], new Dictionary<int, BossSelectionBuffDefinition> { [1275] = strong }).Count == 0,
            "规范普通300缺失或未通过原合同验证时不创建兼容映射");
        PvfTokenSection Row(int id, int buff) => new("index", [new(0, id)], [new("buff list", [new(0, buff)], [])]);
        That(BossSelectionBuffDefinition.ParseMappings([Row(10309619, 300)], definitions)[10309619] == ordinary,
            "10309619按物品关联表绑定已注册300，属于合法白斯追击药水");
        foreach (int id in compatibility)
        {
            foreach (var rows in new ImmutableArray<PvfTokenSection>[]
            {
                [Row(id, 9999)], [Row(id, 300), Row(id, 300)], [Row(id, 300), Row(id, 1275)],
                [new("index", [new(0, id)], [])], [new("index", [new(0, id), new(0, 7)], [])]
            })
                That(!BossSelectionBuffDefinition.ParseMappings(rows, definitions).ContainsKey(id),
                    "已有未知、重复、冲突或畸形声明均不被普通兼容覆盖 " + id);
            That(BossSelectionBuffDefinition.ParseMappings([Row(id, 1275)], definitions)[id] == strong,
                "已有合法显式映射按原定义装配，不强制替换为普通300 " + id);
        }
    }

    static void EpicDropMappings()
    {
        var canonical = new EpicDropBuffDefinition(1136, 100);
        var definitions = new Dictionary<int, EpicDropBuffDefinition> { [1136] = canonical };
        PvfTokenSection Row(int item, int buff = 1136, int probability = 100) => new("index", [new(0, item)],
            [new("probability buff list", [new(0, buff), new(0, probability)], [])]);
        var absent = EpicDropBuffDefinition.ParseMappings([], definitions);
        That(absent.Count == 1 && absent[490000413] is { BuffId: 1136, Percent: 100 } &&
            EpicDropBuffDefinition.ParseMappings([], new Dictionary<int, EpicDropBuffDefinition>()).IsEmpty &&
            EpicDropBuffDefinition.ParseMappings([], new Dictionary<int, EpicDropBuffDefinition> { [1136] = new(1136, 99) }).IsEmpty,
            "只有缺失490000413映射且规范1136百分百通过才创建本服兼容");
        foreach (var rows in new ImmutableArray<PvfTokenSection>[] { [Row(490000413, 1271)], [Row(490000413, probability: 99)],
            [Row(490000413), Row(490000413)], [new("index", [new(0, 490000413)], [])],
            [new("index", [new(0, 490000413), new(0, 7)], [])],
            [Row(490000413), new("index", [new(0, 490000413), new(0, 7)], [])] })
            That(!EpicDropBuffDefinition.ParseMappings(rows, definitions).ContainsKey(490000413),
                "已声明的未知、随机、重复或畸形映射不被缺省兼容覆盖");
        var mapping = EpicDropBuffDefinition.ParseMappings([Row(500001), new("index", [new(0, 500002)],
            [new("buff list", [new(0, 1136)], [])]), new("index", [new(0, 10008219)],
            [new("probability buff list", [new(0, 1136), new(0, 60), new(0, 1271), new(0, 40)], [])]), Row(500003, 1271)], definitions);
        That(mapping.Count == 3 && mapping[500001].IsFamilyItem(500002) && mapping[500002].IsFamilyItem(490000413) &&
            !mapping.ContainsKey(10008219) && !mapping.ContainsKey(500003),
            "确定单映射加入同族；随机多分支和星空不同效果不冒充固定史诗倍率");
        var strings = new[] { "[index]", "[probability buff list]", "[/probability buff list]", "[/index]" };
        PvfToken[] tokens = [new(3, 0), new(0, 490000413), new(3, 1), new(0, 1136), new(0, 100), new(3, 2),
            new(3, 0), new(0, 500001), new(3, 1), new(0, 1136), new(0, 100), new(3, 2), new(3, 3)];
        var isolated = EpicDropBuffDefinition.ParseMappings(EpicDropBuffDefinition.ReadIndices(tokens, id => strings[id]), definitions);
        That(!isolated.ContainsKey(490000413) && isolated.ContainsKey(500001),
            "未闭合index保留无效身份且不吞下一条合法固定映射");
    }
}
