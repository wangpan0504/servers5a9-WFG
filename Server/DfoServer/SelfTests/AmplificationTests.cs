using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class AmplificationTests
{
    public static void RunCore()
    {
        int checks = Count;
        var body = Request(2, 10, 31440, 77, 8238, 4, "amplify-test-item"u8.ToArray());
        var request = AmplificationRequests.Invest(body);
        That(body.Length == 35 && request is { Operation: AmplificationOperation.GoldInvest, TargetSlot: 10, TargetItemId: 31440,
            MaterialSlot: 77, MaterialItemId: 8238, SelectedOption: 4 } && request.TargetText.Length == 17,
            "CMD205黄金书保留14B固定字段、4B名称长度及原始名称，不作为鉴权");
        foreach (byte action in new byte[] { 0, 1 })
            That(AmplificationRequests.Invest(Request(action, 9, 31440, 65, 1286, 3)).TargetText.IsEmpty,
                "CMD205普通赋予和扭转为14B，无黄金书名称尾部");
        var purify = AmplificationRequests.Purify(PurifyRequest(9, 31440, 65, 1183));
        That(purify is { Operation: AmplificationOperation.Purify, TargetSlot: 9, TargetItemId: 31440, MaterialSlot: 65, MaterialItemId: 1183 },
            "CMD204独立12B目标与材料身份");
        foreach (var invalid in new[] { body[..^1], body.Concat(new byte[1]).ToArray(), new byte[13],
            Request(3, 9, 31440, 65, 1286, 3), Request(0, 9, 31440, 65, 1286, 5),
            Request(0, -1, 31440, 65, 1286, 0), Request(2, 9, 31440, 65, 8238, 4, new byte[257]) })
            Reject<InvalidDataException>(() => AmplificationRequests.Invest(invalid), "异次元属性正文边界严格拒绝");
        Reject<InvalidDataException>(() => AmplificationRequests.Purify(new byte[11]));
        Reject<InvalidDataException>(() => AmplificationRequests.Purify(new byte[13]));
        Bytes(AmplificationPackets.Ack(InventoryResult.Reject(17), null), [0, 17, 0], "204/205失败必须包含u16，不复制旧端单字节错误码");
        Bytes(AmplificationPackets.Ack(new(true, 0), new(AmplificationOperation.GoldInvest, 65, 1, 9, 4, 6, 5, false)),
            [1, 2, 65, 0, 1, 0, 0, 0, 9, 0, 4, 6, 0, 5], "黄金增幅书14B回执含最终等级，源数量为绝对剩余量");
        Bytes(AmplificationPackets.Ack(new(true, 0), new(AmplificationOperation.Invest, 65, 0, 9, 3, 6, 0, false)),
            [1, 0, 65, 0, 0, 0, 0, 0, 9, 0, 3, 6, 0], "普通赋予13B不带等级尾部");
        Bytes(AmplificationPackets.Ack(new(true, 0), new(AmplificationOperation.Clear, 65, 0, 9, 0, 0, 0, false)),
            [1, 65, 0, 0, 0, 0, 0, 9, 0, 0, 0, 0], "净化与清除12B没有205的action字段，清除显式返回零属性");
        PublicFields();
        Plans();
        Console.WriteLine($"PASS {Count - checks} 异次元属性协议、赋予、净化、扭转及清除断言");
    }

    static void PublicFields()
    {
        foreach (byte hiddenType in new byte[] { 0x81, 0x82, 0x83, 0x84 })
        {
            var core = new ItemCore(31440, ItemKind.Equipment, 999999998)
            { AmplifyType = hiddenType, AmplifyValue = 51, Durability = 40, EnchantCardId = 3175 };
            var item = new InventoryItem(501, MainSlot(9), core);
            var row = Build(writer => ItemListProtocolWriter.Write(writer, (short)9, core));
            That(row.Length == 123 && row[19] == 128 && BitConverter.ToUInt16(row, 20) == 0,
                "未净化123B条目只下发独立128标志及零数值，不泄漏隐藏属性索引 " + hiddenType);
            var player = Build(writer => EquipmentProtocolWriter.Player(writer,
                new([item with { Position = new(InventoryListType.Equipment, 12) }])));
            That(player[24] == 128 && BitConverter.ToUInt16(player, 25) == 0,
                "Player沿独立字段宽度隐藏未净化属性，不把内部类型用于客户端增幅数组");
            var quest = QuestPackets.FixedRewardRows([new(item, 1)]);
            That(quest[11] == 128 && BitConverter.ToUInt16(quest, 12) == 0, "任务短行也使用公共未净化语义");
            var title = TitlePackets.Book(new([item with { Position = new(InventoryListType.TitleGeneral, 0) }]), 0);
            That(title[30] == 128 && BitConverter.ToUInt16(title, 31) == 0, "称号簿投影不将隐藏状态裸发到普通物品消费者");
            var ground = DungeonPackets.MonsterDeath(1, [new(123, 0, core, 0, 0)]);
            That(ground[27] == 128 && BitConverter.ToUInt16(ground, 28) == 0, "地面完整物品条目与背包使用相同未净化编码");
            var mail = new MailboxEntry(1, 0, 1, 0, "GM", "test", 0, 1000, 2000, 1, true, [new(1, item)]);
            MailboxProtocolTests.CheckList(MailboxPackets.List(new([mail], 0), 1100), [mail], 0, 1100);
            That(item.Core.AmplifyType == hiddenType && item.Core.AmplifyValue == 51 && item.Uid == 501,
                "构包不改写存档类型、隐藏基础值或UID，后续净化可以恢复原属性");
        }
        Bytes(Build(writer => ItemListProtocolWriter.WriteAmplification(writer, null)), [0, 0, 0], "无物品时公共异次元字段为零");
        Bytes(Build(writer => ItemListProtocolWriter.WriteAmplification(writer, new(31440, ItemKind.Equipment, 1)
            { AmplifyType = 3, AmplifyValue = 51 })), [3, 51, 0], "已净化属性保持原类型和数值");
    }

    static void Plans()
    {
        const int sourceId = 990201, targetId = 990202;
        const long now = 2_000_000_000;
        var sourceTemplate = new ItemTemplate(sourceId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Book");
        var targetTemplate = new ItemTemplate(targetId, ItemKind.Equipment, 12, 60, 40, 1, 20, 3, 64, "[free]", ushort.MaxValue, 0, "Target");
        var source = new InventoryItem(101, MainSlot(65), new(sourceId, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        var target = new InventoryItem(102, MainSlot(9), new(targetId, ItemKind.Equipment, 1234567)
        { Attr = 0xE0, Durability = 40, SealFlag = 1, EnchantCardId = 3175, EnchantUpgrade = 2, GenuineUpgrade = 4,
            Rune = 8, Random0 = new(1, 2, 3), Extension114 = new(1, 333), SortLockFlag = 1 });
        Character role = new(1, 0, "Amplify", 0, new(1, 0, 0, 0), null)
        { Job = 2, Level = 75, Inventory = new([source, target], Gold: 3456) };
        var rules = Rules();
        That(rules.BaseValue(3, 3) == 6 && rules.BaseValue(3, 4) == 7 && rules.BaseValue(3, 6) == 6,
            "单精度1.3/1.5/1.4倍率以x87同等精度乘5后截断，不能先舍入乘积或改用十进制double");
        var use = new AmplificationRequest(AmplificationOperation.Invest, 9, targetId, 65, sourceId, 0);
        var definition = new AmplificationMaterial(AmplificationOperation.Invest, 1, 3, []);
        int randomCalls = 0;
        InventoryResult Plan(Character owner, AmplificationRequest request, out AmplificationReceipt? receipt,
            AmplificationMaterial? material = null, ItemTemplate? gear = null, long draw = 0, bool allowed = true, bool inRoom = false,
            ItemTemplate? book = null) =>
            InventoryAmplificationService.Plan(owner, request, now, material ?? definition, rules, book ?? sourceTemplate, gear ?? targetTemplate,
                allowed, false, inRoom, total => { randomCalls++; return draw; }, out receipt);
        var result = Plan(role, use, out var receipt);
        var after = result.Plan!.After;
        var identified = target with { Core = target.Core with { AmplifyType = 3, AmplifyValue = 6 } };
        That(result.Accepted && after.Find(MainSlot(9)) == identified && after.Find(MainSlot(65)) == source with { Core = source.Core with { Value = 1 } } &&
            after.Gold == 3456 && after.AccountWallet.Same(role.Inventory.AccountWallet) && receipt is { MaterialRemaining: 1, Type: 3, Value: 6 },
            "赋予仅改变异次元字段并扣一张源，完整核心、高位、UID和钱包保留");
        That(Plan(role, use with { SelectedOption = 4 }, out _).Error == 17 &&
            Plan(role, use, out _, definition with { Option = 5 }).Error == 17,
            "固定属性书不接收伪造选择，自选书必须明确选择1..4");
        var selected = Plan(role, use with { SelectedOption = 4 }, out receipt, definition with { Option = 5 });
        That(selected.Accepted && receipt is { Type: 4, Value: 6 }, "自选赋予按配置all映射，不按物品编号硬编码");
        That(Plan(role with { Level = 1 }, use, out _, book: sourceTemplate with
            { MinimumLevel = 95, MaximumLevel = 95, Jobs = 1, RequiredSkill = ushort.MaxValue }).Accepted,
            "不额外验证角色与消耗品等级、职业、技能匹配；普通目标仍按装备自身资格判断");
        var amplified = role with { Inventory = new([source, identified]) };
        var twist = use with { Operation = AmplificationOperation.Twist, SelectedOption = 4 };
        var twistRule = definition with { Operation = AmplificationOperation.Twist, Option = 5 };
        That(Plan(amplified, twist, out receipt, twistRule).Accepted && receipt is { Type: 4, Value: 6 } &&
            Plan(amplified, twist with { SelectedOption = 3 }, out _, twistRule).Error == 23,
            "扭转重算选中属性基础值；相同属性拒绝且不扣书");
        That(Plan(amplified, use, out _).Error == 20 && Plan(role, twist, out _, twistRule).Error == 21,
            "普通赋予不能覆盖已有红字，扭转不能作用于普通装备");
        var unidentified = identified with { Core = identified.Core with { AmplifyType = 0x83, AmplifyValue = 51 } };
        var infectedRole = role with { Inventory = new([source, unidentified]) };
        var purify = use with { Operation = AmplificationOperation.Purify };
        var purifyRule = definition with { Operation = AmplificationOperation.Purify, Option = 0 };
        var purified = Plan(infectedRole, purify, out receipt, purifyRule);
        That(purified.Accepted && purified.Plan!.After.Find(MainSlot(9)) == unidentified with
            { Core = unidentified.Core with { AmplifyType = 3 } } && randomCalls == 0,
            "净化只清0x80位，原类型和51基础值不被重新随机或覆盖成默认值");
        var cleared = Plan(infectedRole, purify, out receipt, purifyRule with { Operation = AmplificationOperation.Clear });
        That(cleared.Accepted && cleared.Plan!.After.Find(MainSlot(9))!.Core == target.Core && receipt is { Type: 0, Value: 0 },
            "普通清除只对未净化装备清除属性，保留完整其他字段");
        That(!Plan(amplified, purify, out _, purifyRule with { Operation = AmplificationOperation.Clear }).Accepted &&
            !Plan(infectedRole, twist, out _, twistRule).Accepted, "普通清除不能清已净化红字，未净化装备不能直接扭转");
        var high = identified with { Core = identified.Core with { Attr = 0xEC } };
        var highRole = role with { Inventory = new([source, high]) };
        var goldClear = Plan(highRole, purify, out receipt, purifyRule with { Operation = AmplificationOperation.GoldClear });
        That(goldClear.Accepted && goldClear.Plan!.After.Find(MainSlot(9))!.Core == target.Core && receipt is { Level: 0, RefreshTarget: true },
            "黄金清除允许已净化装备，清增幅等级且保留Attr高位，要求额外目标完整差量");
        var goldRule = definition with { Operation = AmplificationOperation.GoldInvest, Option = 5,
            Levels = [new(3, 50), new(4, 30), new(5, 15), new(6, 5)] };
        var goldUse = use with { Operation = AmplificationOperation.GoldInvest, SelectedOption = 4 };
        foreach (var (draw, expected) in new (long, byte)[] { (0, 3), (49, 3), (50, 4), (79, 4), (80, 5), (94, 5), (95, 6), (99, 6) })
        {
            var gold = Plan(highRole, goldUse, out receipt, goldRule, draw: draw);
            That(gold.Accepted && receipt!.Level == expected && gold.Plan!.After.Find(MainSlot(9))!.Core == target.Core with
                { Attr = (byte)(0xE0 | expected), AmplifyType = 4, AmplifyValue = 6 },
                "黄金书权重区间两端准确，覆盖原+12并保留高位及完整核心");
        }
        int rolled = randomCalls;
        foreach (var bad in new[] { role with { Inventory = new([source with { Uid = 0 }, target]) },
            role with { Inventory = new([source with { Core = source.Core with { EquipmentLockId = 1 } }, target]) },
            role with { Inventory = new([source, target with { Core = target.Core with { EquipmentLockId = 1 } }]) },
            role with { Inventory = new([source, target with { Core = target.Core with { ExpireTime = (int)now } }]) },
            role with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)now } }, target]) } })
            That(!Plan(bad, goldUse, out _, goldRule).Accepted, "旧或临时实例、锁、源和目标准确到期均在随机消费前拒绝");
        That(!Plan(role, goldUse with { TargetItemId = targetId + 1 }, out _, goldRule).Accepted &&
            !Plan(role, goldUse with { MaterialSlot = 9 }, out _, goldRule).Accepted &&
            !Plan(role, goldUse, out _, goldRule, targetTemplate with { MinimumLevel = 54 }).Accepted &&
            !Plan(role, goldUse, out _, goldRule, targetTemplate with { Grade = 58 }).Accepted &&
            !Plan(role, goldUse, out _, goldRule, targetTemplate with { Rarity = 1 }).Accepted &&
            !Plan(role, goldUse, out _, goldRule, allowed: false).Accepted && randomCalls == rolled,
            "伪造目标、源目标重叠、最低等级、修正品级、稀有度与禁止内容不进入随机");
        var damaged = role with { Inventory = new([source, target with { Core = target.Core with { Durability = 39 } }]) };
        That(Plan(damaged, use, out _).Error == 8 && Plan(highRole, twist, out _, twistRule).Error == 18,
            "赋予和转换要求满耐久，普通扭转保持零增幅等级要求");
        var roomOnly = definition with { Village = false, SeriaRoom = true };
        That(!Plan(role, use, out _, roomOnly).Accepted && Plan(role, use, out _, roomOnly, inRoom: true).Accepted,
            "仅赛丽亚房间的声明不扩大为所有城镇，也不拒绝合法房间");
        var timed = role with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)now + 10 } }, target]) };
        var timePlan = Plan(timed, use, out _);
        That(timePlan.Accepted && timePlan.Plan!.CharacterPrecondition!.Matches(timed, now + 9) &&
            !timePlan.Plan.CharacterPrecondition.Matches(timed, now + 10), "源期限冻结到提交前置条件，到期等号拒绝");
        ClockCommit(role, sourceTemplate, targetTemplate, definition, rules, use);
        var lastRole = role with { Inventory = new([source with { Core = source.Core with { Value = 1 } }, target]) };
        var last = Plan(lastRole, use, out receipt);
        That(last.Accepted && last.Plan!.After.Find(MainSlot(65)) is null && receipt!.MaterialRemaining == 0 &&
            !Plan(role with { Inventory = last.Plan.After }, use, out _).Accepted, "耗尽源清槽，重放拒绝且不重复赋予或扣除");
    }

    static void ClockCommit(Character seed, ItemTemplate book, ItemTemplate gear, AmplificationMaterial definition,
        AmplificationRules rules, AmplificationRequest request)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-amplification-clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database);
            var owners = new AccountOwners();
            foreach (short slot in new short[] { request.MaterialSlot, request.TargetSlot })
            {
                var clock = new ConsumableUseSessionTests.FakeClock();
                using var owner = owners.Acquire("amplification-clock-" + slot, () => { });
                int expires = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 10);
                var role = repository.Create(owner, new("Clock" + slot, seed.Position, seed.Return)
                {
                    Job = seed.Job, Level = seed.Level,
                    Inventory = seed.Inventory with { Items = seed.Inventory.Items.Select(item => item with
                        { Uid = 0, Core = item.Position.Index == slot ? item.Core with { ExpireTime = expires } : item.Core }).ToImmutableArray() }
                });
                var result = InventoryAmplificationService.Plan(role, request, clock.GetUtcNow().ToUnixTimeSeconds(), definition,
                    rules, book, gear, true, false, false, _ => 0, out _, clock);
                var guard = result.Plan!.CharacterPrecondition!;
                That(result.Accepted && guard.Matches(role) &&
                    !guard.Matches(role with { Revision = role.Revision + 1 }) && !guard.Matches(role with { Level = 74 }),
                    "异次元源或目标准确绑定原角色与注入时钟，角色资格变化拒绝旧计划");
                string before = Snapshot();
                clock.Advance(TimeSpan.FromSeconds(9));
                That(guard.Matches(role), "异次元提交使用原时钟，截止前一秒仍有效");
                clock.Advance(TimeSpan.FromSeconds(1));
                That(!guard.Matches(role), "异次元提交沿注入时钟在源或目标到期等号拒绝");
                Reject<InventoryQualificationExpiredException>(() => InventoryService.Commit(repository, owner, role, result),
                    "异次元文件库不能用系统时钟放行已经到期的规划");
                That(Snapshot() == before, "到期拒绝保留完整核心、UID、钱包、金币、revision及审计");

                string Snapshot()
                {
                    using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT (SELECT count(*) FROM inventory_audit_log)||':'||" +
                        "(SELECT count(*) FROM currency_audit_log)||':'||(SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items')";
                    return JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) + ":" + command.ExecuteScalar();
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static AmplificationRules Rules() => new(55,
        Enumerable.Range(1, 4).ToImmutableDictionary(value => (byte)value, _ => 5),
        new float[] { 1, 1, 1, 1.3f, 1.5f, 1.1f, 1.4f }.Select((value, rarity) => (value, rarity)).ToImmutableDictionary(row => row.rarity, row => row.value),
        new[] { 1, 1, 2, 4, 5, 3, 4 }.Select((value, rarity) => (value, rarity)).ToImmutableDictionary(row => row.rarity, row => row.value),
        ImmutableDictionary<(AmplificationOperation, int), AmplificationMaterial>.Empty);

    public static void RunResources(ItemResources resources)
    {
        var catalog = resources.Amplifications;
        That(catalog.Get(AmplificationOperation.Purify, 1183) is { Operation: AmplificationOperation.Purify, Count: 1 } &&
            catalog.Get(AmplificationOperation.Purify, 10000704) is { Operation: AmplificationOperation.Clear, Count: 1 } &&
            catalog.Get(AmplificationOperation.Purify, 490705006) is { Operation: AmplificationOperation.GoldClear, Count: 1 },
            "真实PVF分别装配净化、未净化清除及黄金清除，允许相同重复配置而不混用模式");
        That(catalog.Get(AmplificationOperation.Invest, 1184) is { Option: 3 } &&
            catalog.Get(AmplificationOperation.Invest, 1286) is { Option: 5 } &&
            catalog.Get(AmplificationOperation.Twist, 1284) is { Option: 5 } && catalog.Get(AmplificationOperation.GoldInvest, 8238) is { Levels.Length: 4 },
            "真实四维、定向及黄金书按etc映射与STK表装配，不硬编码名单");
        That(catalog.Rules.BaseValue(3, 3) == 6 && catalog.Rules.MinimumLevel == 55 && catalog.Rules.GradeCorrections[3] == 4,
            "真实倍率保留float32、55级与增幅表品级修正");
    }

    internal static byte[] Request(byte action, short target, int targetId, short material, int materialId, byte option, byte[]? text = null) => Build(writer =>
    {
        writer.Write(action); writer.Write(target); writer.Write(targetId); writer.Write(material); writer.Write(materialId); writer.Write(option);
        if (action == 2) { writer.Write(text?.Length ?? 0); writer.Write(text ?? []); }
    });
    internal static byte[] PurifyRequest(short target, int targetId, short material, int materialId) => Build(writer =>
    { writer.Write(target); writer.Write(targetId); writer.Write(material); writer.Write(materialId); });
    static InventorySlot MainSlot(short slot) => new(InventoryListType.Main, slot);
}
