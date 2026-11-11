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

internal static partial class ConsumableUseTests
{
    internal static RecoveryConsumableDefinition Hp { get; } = new(1108, 16, 10000, 0);
    internal static RecoveryConsumableDefinition Mp { get; } = new(1114, 16, 10000, 1);

    public static void RunCore()
    {
        RandomDisguiseRepeatedStackLimit();
        DisguiseKinds();
        BossSelectionMappings();
        EpicDropMappings();
        HellLegendMappings();
        var hellDisplay = DfoServer.Network.Projection.HellLegendBuffProjection.AddedBody(2_000_000_300);
        That(hellDisplay.Length == 17 && hellDisplay[0] == 1 && BitConverter.ToInt32(hellDisplay, 1) == 1271 &&
            BitConverter.ToInt32(hellDisplay, 5) == 2_000_000_300 && hellDisplay.AsSpan(9).SequenceEqual(new byte[8]),
            "星空状态487携带1271与绝对截止秒，不将剩余时长误写入截止字段");
        Reject<InvalidDataException>(() => DfoServer.Network.Projection.HellLegendBuffProjection.AddedBody(0),
            "过期星空状态只能移除，不创建永久图标");
        VillageMoveSpeedMappings();
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
        // ARAD 02E5127E..02E512C7原生producer：monster=2，形态63039，源药剂10312386。
        var (disguiseType, disguiseFormId, disguiseItemId) =
            ConsumableDisguiseRequests.Read([2,0x3F,0xF6,0,0,0xC2,0x5A,0x9D,0]);
        That((disguiseType, disguiseFormId, disguiseItemId) == ((byte)2, 63039, 10312386),
            "211独立9B金值保留wire类型、形态ID与源模板，持续秒数不在请求中");
        foreach (int length in new[] {0,8,10})
            Reject<InvalidDataException>(() => ConsumableDisguiseRequests.Read(new byte[length]),"211拒绝截断及未知尾");
        ConsumableDisguiseRequests.Cancel([]);
        Reject<InvalidDataException>(() => ConsumableDisguiseRequests.Cancel([0]),"212只接受原生空正文");
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
        var dungeonRestricted = raw with { DeclaredFields = raw.DeclaredFields.Add("action usable dungeon"),
            ActionUsableDungeons = [291100339, 5350] };
        That(RecoveryConsumableCatalog.Read(dungeonRestricted)?.AllowedDungeons is { } allowed &&
            allowed.SetEquals(new uint[] { 291100339, 5350 }) && RecoveryConsumableCatalog.Read(raw)?.AllowedDungeons is null,
            "保留资源副本名单元数据；实际匹配由原生客户端完成，不为普通药另建资格分支");
        That(RecoveryConsumableCatalog.Read(dungeonRestricted with { ActionUsableDungeons = null }) is null,
            "存在但解析失败的可用地下城声明不能回退成任意场景使用");
        var nativeStat = dungeonRestricted with
        {
            Stackable = declaration with { HasUseActionPacket = true, DeclaredUseActionPacket = 1 },
            StatChangeDuration = new(60000, "myself", null),
            StatChanges = [new("+",175,"physical attack"),new("%",12,"physical attack bonus")]
        };
        That(RecoveryConsumableCatalog.Read(nativeStat) is { EffectMilliseconds: 60000, AllowedDungeons.Count: 2 },
            "非零action标记不误拦原生优先构造的普通持续属性药，保留时限和真实副本名单");
        foreach (var invalid in new[] { nativeStat with { StatChanges = [] }, nativeStat with { StatChangeDuration = null },
            nativeStat with { StructureDiagnostics = ["invalid structure"] },
            nativeStat with { Stackable = nativeStat.Stackable with { DeclaredUseActionPacket = null } },
            nativeStat with { Stackable = nativeStat.Stackable with { DeclaredUseActionPacket = 2 } },
            nativeStat with { DeclaredFields = nativeStat.DeclaredFields.Add("action type") },
            nativeStat with { Stackable = nativeStat.Stackable with { TypeDeclaration = new("[disguise]",[0]) } } })
            That(RecoveryConsumableCatalog.Read(invalid) is null,
                "属性药分支不放行缺效果、未知action标记、资产动作、变身或损坏结构");
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
        That(RecoveryConsumableCatalog.Read(raw with
        {
            TemplateId = 6516,
            Stackable = declaration with { TemplateId = 6516, TypeDeclaration = new("[waste instant item]", [1]) }
        }) is { ItemId: 6516, MinimumLevel: 16, CoolTime: 10000, EffectMilliseconds: 0 } &&
            RecoveryConsumableCatalog.Read(raw with
            {
                TemplateId = 6516,
                Stackable = declaration with { TemplateId = 6516, TypeDeclaration = new("[waste instant item]", [1]) },
                DeclaredFields = raw.DeclaredFields.Add("action type")
            }) is null,
            "塔内即时药剂按 waste instant 大类走普通44；带 action type 的房产即时物仍拒绝");
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
        var durationBinding = VariousBuffAssociationTests.Binding(epicRaw.TemplateId, [VariousBuffAssociationTests.Definition(90010, count: 10)]);
        That(RecoveryConsumableCatalog.Read(epicRaw, variousBuff: durationBinding) is { VariousDurationMilliseconds: 600000 } &&
            RecoveryConsumableCatalog.Read(epicRaw with { StatChangeDuration = null,
                DeclaredFields = epicRaw.DeclaredFields.Remove("stat change duration") }, variousBuff: durationBinding) is { VariousDurationMilliseconds: null } &&
            RecoveryConsumableCatalog.Read(epicRaw with { StatChangeDuration = new(0, "myself", null),
                DeclaredFields = epicRaw.DeclaredFields.Add("stat change duration") }, variousBuff: durationBinding) is { VariousDurationMilliseconds: 0 },
            "通用Buff资源装配保留STK声明时长、缺失和显式0的区别");
        That(RecoveryConsumableCatalog.Read(epicRaw, epicBuff: epic) is { EpicDropBuff.Percent: 100, EffectMilliseconds: 600000, EffectMaintenance: 15 },
            "已核固定史诗效果才允许城镇药剂0并沿资源持续时间/维护位");
        That(RecoveryConsumableCatalog.Read(epicRaw, epicBuff: epic with { Percent = 150 }) is { EpicDropBuff.Percent: 150 },
            "效果百分比来自元数据，合法150参数不被固定编号合同拒绝");
        foreach (var invalid in new[] { epicRaw with { EffectMaintenance = null }, epicRaw with { EffectMaintenance = 0 },
            epicRaw with { StatChangeDuration = new(600000, "party", null) }, epicRaw with { StatChangeDuration = null },
            epicRaw with { Diagnostics = ["invalid field"] } })
            That(RecoveryConsumableCatalog.Read(invalid, epicBuff: epic) is null, "固定史诗药剂拒绝坏持续时间、维护位或声明");
        That(RecoveryConsumableCatalog.Read(epicRaw with { Stackable = epicRaw.Stackable with
            { DeclaredActionUsablePlaces = ["[dungeon]"] } }, epicBuff: epic) is not null,
            "使用地点不改变史诗药剂身份");
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
        var hellRaw = epicRaw with { CooltimeMaintenance = 7, EffectMaintenance = null,
            StatChangeDuration = new(300000, "myself", null) };
        var hell = new HellLegendEpicDropBuffDefinition(1271, 6520) { ItemIds = ImmutableHashSet.Create(490704393, 490703675) };
        That(RecoveryConsumableCatalog.Read(hellRaw, hellLegendBuff: hell) is
            { HellLegendEpicDropBuff.Param: 6520, EffectMilliseconds: 300000, EffectMaintenance: 7 },
            "星空1271城镇药剂缺 effect maintenance 时沿 cooltime maintenance 7 保留死亡/回城/重选");
        That(RecoveryConsumableCatalog.Read(hellRaw with { EffectMaintenance = 7 }, hellLegendBuff: hell) is { EffectMaintenance: 7 } &&
            RecoveryConsumableCatalog.Read(hellRaw, epicBuff: epic, hellLegendBuff: hell) is null &&
            RecoveryConsumableCatalog.Read(hellRaw, hellLegendBuff: hell with { Param = 100 }) is { HellLegendEpicDropBuff.Param: 100 },
            "显式维护位和任意非负百分比沿用资源值，单项旧视图不能重复挂在一瓶上");
        foreach (var invalid in new[] { hellRaw with { CooltimeMaintenance = null }, hellRaw with { EffectMaintenance = 0 },
            hellRaw with { StatChangeDuration = new(300000, "party", null) }, hellRaw with { Diagnostics = ["invalid field"] } })
            That(RecoveryConsumableCatalog.Read(invalid, hellLegendBuff: hell) is null, "星空药剂拒绝坏持续时间、维护位或声明");
        var hellInventory = InventorySnapshot.Empty with { ConsumableStates = [
            new(ConsumableStateKind.Effect, 490703675, 9999999, 7, 253),
            new(ConsumableStateKind.Cooldown, 490703675, 9999999, 7, 253),
            new(ConsumableStateKind.Effect, 490000413, 9999999, 15, 251)] };
        var hellRefreshed = ConsumableStatePolicy.Activate(hellInventory,
            RecoveryConsumableCatalog.Read(hellRaw with { Stackable = hellRaw.Stackable }, hellLegendBuff: hell)! with
            { ItemId = 490704393 }, 1000)!;
        That(hellRefreshed.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect && hell.IsFamilyItem(row.ItemId)) is
                { ItemId: 490704393, UntilMilliseconds: 301000 } &&
            hellRefreshed.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Effect && row.ItemId == 490000413) &&
            hellInventory.ConsumableStates.Length == 3,
            "星空同族只覆盖1271效果，不删固定1136或改原快照");
        var villageRaw = epicRaw with { CooltimeMaintenance = 7, EffectMaintenance = 7,
            StatChangeDuration = new(300000, "myself", null) };
        var village = new VillageMoveSpeedBuffDefinition(1244, 20) { ItemIds = ImmutableHashSet.Create(490006630, 10306776) };
        That(RecoveryConsumableCatalog.Read(villageRaw, villageSpeedBuff: village) is
            { VillageMoveSpeedBuff.Percent: 20, EffectMilliseconds: 300000, EffectMaintenance: 7 },
            "已核城镇移速效果才允许城镇药剂0并沿STK时长和维护位");
        That(RecoveryConsumableCatalog.Read(villageRaw, epicBuff: epic, villageSpeedBuff: village) is null &&
            RecoveryConsumableCatalog.Read(villageRaw, villageSpeedBuff: village with { Percent = 0 }) is null,
            "1136 与城镇移速不能挂在同一瓶上，零移速参数拒绝");
        foreach (var invalid in new[] { villageRaw with { EffectMaintenance = null, CooltimeMaintenance = null },
            villageRaw with { EffectMaintenance = 0, CooltimeMaintenance = 0 },
            villageRaw with { StatChangeDuration = new(300000, "party", null) },
            villageRaw with { StatChangeDuration = null },
            villageRaw with { Diagnostics = ["invalid field"] } })
            That(RecoveryConsumableCatalog.Read(invalid, villageSpeedBuff: village) is null,
                "城镇移速药剂拒绝坏持续时间、维护位或效果声明");
        That(RecoveryConsumableCatalog.Read(villageRaw with { Stackable = villageRaw.Stackable with
            { DeclaredActionUsablePlaces = ["[dungeon]"] } }, villageSpeedBuff: village) is
            { VillageMoveSpeedBuff.Percent:20, Places:["[dungeon]"] },
            "地点声明不改变已支持的药剂身份，使用资格交给统一门禁与实际效果入口");
        var villageInventory = InventorySnapshot.Empty with { ConsumableStates = [
            new(ConsumableStateKind.Effect, 10306776, 9999999, 7, -10306776),
            new(ConsumableStateKind.Cooldown, 10306776, 9999999, 7, -10306776),
            new(ConsumableStateKind.Effect, 490000413, 9999999, 15, 251)] };
        var villageRefreshed = ConsumableStatePolicy.Activate(villageInventory,
            RecoveryConsumableCatalog.Read(villageRaw, villageSpeedBuff: village)! with { ItemId = 490006630 }, 1000)!;
        That(villageRefreshed.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect && village.IsFamilyItem(row.ItemId)) is
                { ItemId: 490006630, UntilMilliseconds: 301000 } &&
            villageRefreshed.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Effect && row.ItemId == 490000413) &&
            villageInventory.ConsumableStates.Length == 3,
            "城镇移速同族只覆盖效果期限，不叠加走速、不删固定1136或改原快照");
        var limited = RecoveryConsumableCatalog.Read(raw with
        {
            Stackable = declaration with
            { HasMaximumLevel = true, DeclaredMaximumLevel = 20, DeclaredUsableJobs = ["[mage]"] }
        })!;
        That(limited.Allows(16) && !limited.Allows(21) && limited.Jobs == 1 << 3,
            "保留等级上下限和职业元数据，使用流程不重复客户端的职业匹配");
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
        AllDisguiseResources(resources);
        uint[] fiendwarDungeons = [..Enumerable.Range(291100339, 26).Select(id => checked((uint)id)),
            ..Enumerable.Range(5350, 9).Select(id => checked((uint)id))];
        foreach (int id in new[] { 10312384, 10312387 })
            That(resources.RecoveryConsumables.TryGet(id, out var potion) && potion.AllowedDungeons is { } dungeons &&
                dungeons.SetEquals(fiendwarDungeons) && potion.CoolTime == 10000,
                "漩涡实际普通恢复及解状态补给保留团队26图与引导9图名单和原冷却");
        That(resources.RecoveryConsumables.TryGet(10312385, out var potential) &&
            potential.AllowedDungeons is { } potentialDungeons && potentialDungeons.SetEquals(fiendwarDungeons) &&
            potential is { CoolTime: 60000, CoolGroup: 10, EffectMilliseconds: 60000 },
            "潜能药按原生持续属性使用分支保留团队及引导35图、60秒持续和组10冷却");
        That(resources.RecoveryConsumables.TryGet(10312386, out var orc) &&
            orc is { CoolTime: 60000, Disguise: { Type: 2, FormId: 63039, Seconds: 60 }, AllowedDungeons: null },
            "兽人药从真实资源解析城镇怪物形态和时长，44扣药后仍须211兑现，不借属性药分支伪造效果");
        That(resources.RecoveryConsumables.TryGet(490013028, out var randomCos) &&
            randomCos is { CoolTime: 10000, CoolGroup: -490013028, CooltimeMaintenance: 0, Unlimited: true, Disguise: { Type: 2 }, AllowedDungeons: null },
            "惊喜COS装扮按真实random disguise资源保留直接211资格、模板默认独立组的10秒会话冷却及顶层免耗声明，完整九形态由TCP验证");
        var mentalUse = new InventoryConsumableUseService(resources.RecoveryConsumables);
        foreach (var (id, group, milliseconds) in new[]
        {
            (2600021, 13, 1800000), (10006457, 13, 1800000),
            (7463, 0, 10000), (7464, 1, 10000), (7740, 0, 10000), (7741, 1, 10000),
            (2680327, 2, 5000), (2680376, 2, 10000), (2680383, 2, 10000), (2680385, 2, 10000),
            (2680452, 2, 10000), (2680751, 2, 10000), (2749001, 2, 10000), (2749003, 2, 10000),
            (2749367, 2, 10000), (2749743, 2, 10000), (10002988, 2, 10000)
        })
        {
            That(resources.RecoveryConsumables.TryGet(id, out var mental) && !mental.Unlimited &&
                mental.CoolGroup == group && mental.CoolTime == milliseconds,
                "重复冷却声明的原生消耗品仍取得正确资格及最终冷却：" + id);
            var source = new InventoryItem(1, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(resources.Items.Get(id), 2));
            Character character = new(1, 0, "Mental", 0, new(1, 0, 10, 10), null)
                { Level = 95, Inventory = new([source]) };
            var result = mentalUse.Plan(character, new(source.Position, 0, id, 0), 0);
            That(result.Accepted && result.Plan!.After.Find(source.Position)?.Core.Count == 1 &&
                result.Plan.After.Find(source.Position)?.Uid == source.Uid,
                "重复冷却声明消耗品使用规划严格扣同UID一件：" + id);
        }
        foreach (var (id, duration, mode, bonus) in new[]
        {
            (490004594, 1800000, EnhancementPotionMode.Reinforce, 1000),
            (10004688, 180000, EnhancementPotionMode.Amplify, 5000),
            (10004745, 180000, EnhancementPotionMode.Amplify, 3000),
            (490001534, 1800000, EnhancementPotionMode.Amplify, 1000),
            (490010213, 600000, EnhancementPotionMode.Amplify, 500),
            (490012609, 600000, EnhancementPotionMode.Amplify, 500),
            (2600025, 1800000, EnhancementPotionMode.Reinforce, 1000),
            (2749959, 1800000, EnhancementPotionMode.Reinforce, 10000),
            (490012267, 1800000, EnhancementPotionMode.Reinforce, 10000),
            (490012268, 1800000, EnhancementPotionMode.Reinforce, 10000)
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
        int[] allEpicPeers = [.. fixedPeers, 490005358, 490005359];
        DungeonEpicDropBuffTests.RunResources(resources);
        VariousBuffAssociationTests.RunResources(resources);
        That(allEpicPeers.All(fixedEpic!.EpicDropBuff!.ItemIds!.Contains) &&
            allEpicPeers.All(resources.RecoveryConsumables.EpicDropBuffs.ContainsKey),
            "十九条闭合固定映射、两条已核缺尾修复和唯一缺映射兼容完整装配，不吞下条");
        foreach (int id in fixedPeers)
            That(resources.RecoveryConsumables.TryGet(id, out var peer) && peer.EpicDropBuff is { BuffId: 1136, Percent: 100 } &&
                peer.EpicDropBuff.IsFamilyItem(490000413) && peer.EffectMilliseconds == (id == 490005778 ? 1800000 : 600000) &&
                peer.EffectMaintenance > 0 && !peer.RequiresDungeon && peer.Allows(95),
                "全部固定1136同类真实药剂沿各自时长与资格，旧版不强求可选事件标签：" + id);
        foreach (var (id, buff, percent) in new[] { (490005358, 1239, 10), (490005359, 1240, 5) })
            That(resources.RecoveryConsumables.TryGet(id, out var lantern) && lantern is
                { EpicDropBuff.HellRewardOnly: true, EffectMilliseconds: 600000, CoolTime: 600000,
                    CoolGroup: 251, EffectMaintenance: 15, CooltimeMaintenance: 7 } &&
                lantern.EpicDropBuff.BuffId == buff && lantern.EpicDropBuff.Percent == percent &&
                lantern.EpicDropBuff.ItemIds!.SetEquals(allEpicPeers),
                "小灯笼按真实映射绑定深渊专用10/5参数、十分钟持续及共享冷却，同固定史诗药互斥：" + id);
        That(resources.RecoveryConsumables.TryGet(10008219, out var randomEpic) && randomEpic.VariousBuff is { Association.Random: true },
            "随机倍率来源按通用概率关联接入，不冒充单项固定视图");
        That(resources.RecoveryConsumables.TryGet(490703675, out var starrySample) && starrySample is
            { HellLegendEpicDropBuff: { BuffId: 1271, Param: 6520 }, EpicDropBuff: null, EffectMilliseconds: 300000,
                CoolTime: 300000, CoolGroup: 253, EffectMaintenance: 7, CooltimeMaintenance: 7 },
            "星空裂缝史诗Buff药剂走已核1271合同，不冒充固定1136");
        int[] starryPeers = [490703675, 490703918, 490703948, 490011680, 490011769, 490011665, 490012202, 490704393, 490011326];
        That(starryPeers.All(starrySample!.HellLegendEpicDropBuff!.ItemIds!.Contains) &&
            resources.RecoveryConsumables.HellLegendEpicDropBuffs.Keys.Order().SequenceEqual(starryPeers.Order()),
            "八条闭合1271映射（含两份相同的490703948）和唯一缺映射兼容完整装配");
        foreach (int id in starryPeers)
            That(resources.RecoveryConsumables.TryGet(id, out var peer) && peer.HellLegendEpicDropBuff is { BuffId: 1271, Param: 6520 } &&
                peer.HellLegendEpicDropBuff.IsFamilyItem(490704393) && peer.EpicDropBuff is null &&
                peer.EffectMilliseconds == 300000 && peer.EffectMaintenance == 7 && peer.CooltimeMaintenance == 7 &&
                peer.CoolGroup == 253 && !peer.RequiresDungeon && peer.Allows(95) &&
                (id != 490704393 || peer.MinimumLevel == 95 && !peer.Allows(94)),
                "全部星空1271同类真实药剂沿五分钟、组253和资格，截图490704393保留95级门槛：" + id);
        That(resources.RecoveryConsumables.TryGet(490006630, out var villageSample) && villageSample is
            { VillageMoveSpeedBuff: { BuffId: 1244, Percent: 20 }, EpicDropBuff: null, HellLegendEpicDropBuff: null,
                EffectMilliseconds: 300000, CoolTime: 300000, CoolGroup: 11, EffectMaintenance: 7, CooltimeMaintenance: 7 },
            "城镇移速药剂走已核 village_move_speed 合同，时长取STK五分钟");
        foreach (var (id, duration, group, percent, buff) in new (int, int, int, int, int)[]
        {
            (490006630, 300000, 11, 20, 1244),
            (490007529, 300000, 11, 20, 1244),
            (490008899, 60000, 11, 20, 1244),
            (10306776, 1200000, -10306776, 15, 291),
            (10309246, 300000, -10309246, 20, 299)
        })
            That(resources.RecoveryConsumables.TryGet(id, out var peer) && peer.VillageMoveSpeedBuff is { } speed &&
                speed.BuffId == buff && speed.Percent == percent && speed.IsFamilyItem(490006630) &&
                peer.EffectMilliseconds == duration && peer.CoolGroup == group && peer.EffectMaintenance == 7 &&
                !peer.RequiresDungeon && peer.Allows(1),
                "全部已核STK时长的城镇移速药剂沿各自时长、组和资格：" + id);
        int[] villagePeers = [490006630, 490007529, 490008899, 10306776, 10309246, 490010229, 490011339, 490703721];
        That(villageSample!.VillageMoveSpeedBuff!.ItemIds!.SetEquals(villagePeers) &&
            resources.RecoveryConsumables.VillageMoveSpeedBuffs.Keys.Order().SequenceEqual(villagePeers.Order()),
            "五条已核STK时长、两条缺时长1245与一条scale胶囊映射同族装配，随机0秒expire与空buff不进表");
        foreach (int id in new[] { 490010229, 490011339, 10008221 })
            That(resources.RecoveryConsumables.TryGet(id, out var associated) && associated.VariousBuff is { Ready: true },
                "缺STK计时或概率来源自动由Buff元数据接入，无需补该编号：" + id);
        That(!resources.RecoveryConsumables.TryGet(490003403, out _) && !resources.RecoveryConsumables.TryGet(490703721, out _),
            "空Buff和其它生产者类型不伪造为已执行的通用资格");
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
            That(extra!.Allows(95), "已绑定白斯药剂允许正常95级角色使用 " + id);
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
        That(resources.RecoveryConsumables.TryGet(6516, out var towerHp) && towerHp is
            { MinimumLevel: 1, CoolTime: 10000, CoolGroup: 1, EffectMilliseconds: 0, RequiresDungeon: false } &&
            resources.RecoveryConsumables.TryGet(6517, out var towerMp) && towerMp is { CoolGroup: 2, CoolTime: 10000 },
            "塔内特级生命/魔力即时药剂取得普通消费资格，HP/MP仍由客户端执行");
        foreach (int id in new[] { 6518, 6519, 6520, 6521, 6522, 6523, 6524, 6525 })
            That(resources.RecoveryConsumables.TryGet(id, out var tower) && tower.CoolTime == 10000 &&
                resources.Items.Get(id).Kind == ItemKind.Consumable,
                "塔内迅捷/圣护及上下级即时药剂同样可CMD44：" + id);
        foreach (int id in new[] { 10148309, 10148310, 10148311, 10148312, 10148313, 10148314, 490001782 })
            That(!resources.RecoveryConsumables.TryGet(id, out _),
                "房产动作即时物仍走CMD515，不由普通44冒充：" + id);
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

    static void RandomDisguiseRepeatedStackLimit()
    {
        string[] strings = ["[stackable type]","[random disguise]","[stack limit]","[string data]",
            "monster","[/string data]","[int data]","[/int data]","[unlimited waste]"];
        RecoveryConsumableFile Read(params PvfToken[] secondLimit) => RecoveryConsumableFile.Parse(490013028,
            "stackable/synthetic-cos.stk", [new(3,0),new(6,1),new(0,0),new(3,2),new(0,1),
                new(3,2),..secondLimit,new(3,3),new(6,4),new(3,5),new(3,6),new(0,58026),new(0,300),
                new(3,7),new(3,8),new(0,1)], id=>strings[id]);
        var complete = Read(new PvfToken(0,1));
        That(complete.Diagnostics.IsEmpty && complete.Stackable.DeclaredStackLimit is null &&
            complete.Stackable.UnsupportedFields.Contains("stack limit"),
            "真实COS重复stack limit1/1在恢复目录兼容，通用StackableFile仍保留严格重复诊断与空上限");
        That(RecoveryConsumableCatalog.Read(complete) is
            { Unlimited: true, Disguise: { Type: 2, FormId: 58026, Seconds: 300 } },
            "monster声明映射原生Actor类型2的Monster.lst，不能送入类型1的NPC资源链");
        foreach (var invalid in new PvfToken[][] { [],[new(0,2)],[new(0,0)],[new(0,-1)],
            [new(6,4)],[new(0,1),new(0,1)] })
            That(!Read(invalid).Diagnostics.IsEmpty,
                "重复堆叠上限冲突、空值、坏类型或非完整标量继续拒绝，不能只取某个合法值");
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
        var names = new[] { "random_box_buff", "epic_rarity_item_drop_rate", "expire_time", "all" };
        PvfTokenSection Text(string name, int value) => new(name, [new(6, value)], []);
        PvfTokenSection Numbers(string name, params int[] values) => new(name, [.. values.Select(value => new PvfToken(0, value))], []);
        PvfTokenSection Definition(int id, int percent, int overlap = 0, int duration = 10) => new("index", [new(0, id)],
            [Text("system group", 0), new("effect", [], [Text("name", 1), Numbers("param", percent)], true),
                new("apply type", [], [Text("type", 2), Numbers("param", duration, 0)], true), Numbers("overlap", overlap)], true);
        var lanterns = EpicDropBuffDefinition.ReadDefinitions([Definition(1239, 10), Definition(1240, 5)], id => names[id]);
        That(lanterns.Count == 2 && lanterns[1239] is { Percent: 10, HellRewardOnly: false } &&
            lanterns[1240] is { Percent: 5, HellRewardOnly: false }, "资源定义只解释effect，单看编号或计时参数不猜特殊范围");
        That(EpicDropBuffDefinition.ReadDefinitions([Definition(9999, 150)], id => names[id])[9999] is { Percent: 150, HellRewardOnly: false } &&
            EpicDropBuffDefinition.ReadDefinitions([Definition(1239, 5)], id => names[id])[1239].Percent == 5,
            "按effect读取任意编号和参数；小灯笼的深渊范围策略不绑死其效果参数");
        foreach (var invalid in new[] { Definition(9999, -1), Definition(9999, 10) with { Children = Definition(9999, 10).Children.Add(Numbers("overlap max", 0)) },
            Definition(9999, 10) with { Children = Definition(9999, 10).Children.Add(Numbers("unknown field", 1)) } })
            That(EpicDropBuffDefinition.ReadDefinitions([invalid], id => names[id]).IsEmpty,
                "未知元数据、非法叠加或负概率保留不支持状态，不因effect已知而放行");
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
        var combined = new Dictionary<int, EpicDropBuffDefinition>(lanterns) { [1136] = canonical };
        var lanternMapping = EpicDropBuffDefinition.ParseMappings([Row(490005358, 1239), Row(490005359, 1240)], combined);
        That(lanternMapping.Count == 3 && lanternMapping[490005358].IsFamilyItem(490005359) &&
            lanternMapping[490005359].IsFamilyItem(490000413) && lanternMapping[490005358].HellRewardOnly &&
            lanternMapping[490005359].HellRewardOnly && !lanternMapping[490000413].HellRewardOnly,
            "大小瓶与固定史诗药共用替换家族；明确的本服道具范围覆盖与通用effect分发分开");
        var previousEffects = InventorySnapshot.Empty with { ConsumableStates = [
            new(ConsumableStateKind.Effect, 490000413, 900000, 15, 252),
            new(ConsumableStateKind.Effect, 490005358, 900000, 15, 251),
            new(ConsumableStateKind.Effect, 490005359, 900000, 15, 251),
            new(ConsumableStateKind.Effect, 7440, 900000, 15, 99)] };
        foreach (int id in new[] { 490005358, 490005359, 490000413 })
        {
            var next = ConsumableStatePolicy.Activate(previousEffects,
                new(id, 1, 600000, 251) { EpicDropBuff = lanternMapping[id], EffectMilliseconds = 600000,
                    EffectMaintenance = 15, CooltimeMaintenance = 7 }, 100000)!;
            That(next.ConsumableStates.Count(row => row.Kind == ConsumableStateKind.Effect) == 2 &&
                next.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect && row.ItemId == id).UntilMilliseconds == 700000 &&
                next.ConsumableStates.Any(row => row.ItemId == 7440) && previousEffects.ConsumableStates.Length == 4,
                "小灯笼大小瓶及原固定史诗药双向覆盖，不叠加旧倍率或剩余时长，不删无关药效");
        }
        var strings = new[] { "[index]", "[probability buff list]", "[/probability buff list]", "[/index]" };
        PvfToken[] tokens = [new(3, 0), new(0, 490000413), new(3, 1), new(0, 1136), new(0, 100), new(3, 2),
            new(3, 0), new(0, 500001), new(3, 1), new(0, 1136), new(0, 100), new(3, 2), new(3, 3)];
        var isolated = EpicDropBuffDefinition.ParseMappings(EpicDropBuffDefinition.ReadIndices(tokens, id => strings[id]), definitions);
        That(!isolated.ContainsKey(490000413) && isolated.ContainsKey(500001),
            "未闭合index保留无效身份且不吞下一条合法固定映射");
    }

    static void HellLegendMappings()
    {
        var canonical = new HellLegendEpicDropBuffDefinition(1271, 6520);
        var definitions = new Dictionary<int, HellLegendEpicDropBuffDefinition> { [1271] = canonical };
        PvfTokenSection Row(int item, int buff = 1271) => new("index", [new(0, item)],
            [new("buff list", [new(0, buff)], [])]);
        var absent = HellLegendEpicDropBuffDefinition.ParseMappings([], definitions);
        That(absent.Count == 1 && absent[490011326] is { BuffId: 1271, Param: 6520 } &&
            HellLegendEpicDropBuffDefinition.ParseMappings([], new Dictionary<int, HellLegendEpicDropBuffDefinition>()).IsEmpty &&
            HellLegendEpicDropBuffDefinition.ParseMappings([], new Dictionary<int, HellLegendEpicDropBuffDefinition>
                { [1271] = new(1271, 1) }).IsEmpty,
            "只有缺失490011326映射且规范1271参数6520通过才创建本服兼容");
        foreach (var rows in new ImmutableArray<PvfTokenSection>[]
        {
            [Row(490011326, 1136)], [Row(490011326), Row(490011326, 1136)],
            [new("index", [new(0, 490011326)], [])],
            [new("index", [new(0, 490011326), new(0, 7)], [])]
        })
            That(!HellLegendEpicDropBuffDefinition.ParseMappings(rows, definitions).ContainsKey(490011326),
                "已声明的未知、冲突或畸形映射不被缺省兼容覆盖");
        var mapping = HellLegendEpicDropBuffDefinition.ParseMappings(
            [Row(490703948), Row(490703948), Row(500001), new("index", [new(0, 10008219)],
                [new("probability buff list", [new(0, 1136), new(0, 60), new(0, 1271), new(0, 40)], [])])],
            definitions);
        That(mapping.Count == 3 && mapping[490703948].IsFamilyItem(500001) && mapping[500001].IsFamilyItem(490011326) &&
            !mapping.ContainsKey(10008219),
            "两份相同1271映射按一份装配；随机多分支不冒充星空效果");
        var conflict = HellLegendEpicDropBuffDefinition.ParseMappings([Row(490703948), Row(490703948, 1136)], definitions);
        That(!conflict.ContainsKey(490703948) && conflict.ContainsKey(490011326),
            "冲突重复拒绝该物品，不影响缺映射兼容");
    }

    static void VillageMoveSpeedMappings()
    {
        var speed = new VillageMoveSpeedBuffDefinition(1244, 20);
        var definitions = new Dictionary<int, VillageMoveSpeedBuffDefinition> { [1244] = speed, [291] = new(291, 15) };
        PvfTokenSection Probability(int item, int buff = 1244, int probability = 100) => new("index", [new(0, item)],
            [new("probability buff list", [new(0, buff), new(0, probability)], [])]);
        PvfTokenSection BuffList(int item, int buff) => new("index", [new(0, item)],
            [new("buff list", [new(0, buff)], [])]);
        var mapping = VillageMoveSpeedBuffDefinition.ParseMappings(
            [Probability(490006630), BuffList(10306776, 291), Probability(10008221, 1100, 60),
                new("index", [new(0, 500001)], [new("probability buff list",
                    [new(0, 1244), new(0, 60), new(0, 291), new(0, 40)], [])])],
            definitions);
        That(mapping.Count == 2 && mapping[490006630].IsFamilyItem(10306776) && mapping[10306776].Percent == 15 &&
            !mapping.ContainsKey(10008221) && !mapping.ContainsKey(500001),
            "确定单映射加入城镇移速同族；随机多分支不冒充固定走速");
        That(VillageMoveSpeedBuffDefinition.ParseMappings([Probability(490006630, probability: 99)], definitions).IsEmpty,
            "非百分百概率分支不能当作确定城镇移速");
    }
}
