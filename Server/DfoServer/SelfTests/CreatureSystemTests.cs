using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureSystemTests
{
    internal static CreatureCatalog Synthetic()
    {
        var files = new Dictionary<string, string>
        {
            ["equipment/test/pet.equ"] = "[equipment type]\n`[creature]` 0\n[creature species]\n10\n[output index]\n7002",
            ["equipment/test/evolved.equ"] = "[equipment type]\n`[creature]` 0\n[creature species]\n11",
            ["creature/pet.cre"] = "[max level]\n4\n[evolution level]\n2\n[evolution creature id]\n11\n[evolution quest]\n0",
            ["creature/evolved.cre"] = "[max level]\n4",
            ["stackable/feed.stk"] = "[stackable type]\n`[feed]` 1\n[creature minimum level]\n1\n[cool time]\n10000\n[int data]\n300 -20 30\n[/int data]",
            ["stackable/rename.stk"] = "[stackable type]\n`[creature]` 1"
        };
        return new(path => files[path], path => path == "creature/pet.cre" ?
            new PvfLib.CreatureFile { MaximumLevel = 4, EvolutionLevel = 2, EvolutionSpecies = 11 } : new PvfLib.CreatureFile { MaximumLevel = 4 },
            new Dictionary<int, string> { [7001] = "test/pet.equ", [7002] = "test/evolved.equ" },
            new Dictionary<int, string> { [24] = "feed.stk", [25] = "rename.stk" },
            new Dictionary<int, string> { [10] = "pet.cre", [11] = "evolved.cre" }, "#PVF_File\n2\n5\n9");
    }

    public static void RunCore()
    {
        int before = Count;
        var catalog = Synthetic();
        var definition = catalog.Get(7001)!;
        That(definition is { Species: 10, MaximumLevel: 4, EvolutionSpecies: 11, OutputItemId: 7002 } &&
            catalog.Evolution(definition, 11)?.ItemId == 7002, "宠物种类和进化使用CRE与EQU的真实关联");
        That(catalog.Get(1) is null && catalog.Feed(1) is null && !catalog.RenameCard(24) && catalog.RenameCard(25),
            "未知模板不当作普通宠物或换名卡");
        foreach (string table in new[] { "", "0", "2\n2", "5\n2", "2 3 2", "2\nbad" })
            Reject<InvalidDataException>(() => CreatureCatalog.ParseExperience(table), "经验表拒绝缺失、非递增或畸形行");
        That(CreatureCatalog.ParseExperience("2 5 9\r\n15 ").SequenceEqual(new[] { 2, 5, 9, 15 }), "二进制TBL渲染空白分隔不依赖原始换行");
        var detail = new CreatureDetail { Satiety = 10 };
        That(CreatureRules.AdvanceSatiety(detail, 60, true) is { Satiety: 9, SatietyFraction: 0 }, "战斗每分钟减少一点饱食");
        That(CreatureRules.AdvanceSatiety(detail, 60, true, foodRate: 50) is { Satiety: 8, SatietyFraction: 18000 } &&
            CreatureRules.AdvanceSatiety(detail, 60, true, foodRate: -100) == detail,
            "宠物装备饱食倍率参与战斗消耗，小数不按秒丢失且最低不产生负消耗");
        var half = CreatureRules.AdvanceSatiety(detail, 30, true);
        That(half is { Satiety: 9, SatietyFraction: 18000 } && CreatureRules.AdvanceSatiety(half, 180, false) == detail,
            "战斗与休息采用相同分母，切场景不丢失小数余量");
        That(CreatureRules.AdvanceSatiety(detail, long.MaxValue, true).Satiety == 0 &&
            CreatureRules.AdvanceSatiety(detail, long.MaxValue, false).Satiety == 100, "大跨度时钟有界饱和而不溢出");
        That(CreatureRules.AdvanceSatiety(new() { Satiety = 0 }, 0, false, true).Satiety == 1,
            "饥饿宠物回城按旧端业务恢复一点，不永久卡死");
        var feed = catalog.Feed(24)!;
        That(CreatureRules.Feed(detail, feed, 1000) is { Satiety: 40, FeedEffectExpiresAt: 1300, FeedSkillRecoveryRate: -20 },
            "饲料恢复量、持续时间和技能集气修正均取PVF");
        That(CreatureRules.Feed(new() { Satiety = 90, SatietyFraction = 5 }, feed, 1000) is { Satiety: 100, SatietyFraction: 0 },
            "喂食达到上限时清除多余小数");
        That(catalog.Level(1, definition) == 1 && catalog.Level(2, definition) == 2 && catalog.Level(5, definition) == 3 &&
            catalog.Level(100, definition) == 4, "累计经验的精确阈值与等级上限");
        That(CreatureRules.GainExperience(detail, definition, catalog, 2) is { Experience: 2, Level: 2 },
            "宠物经验按独立点数增长，不借角色经验值");
        That(CreatureRules.GainExperience(detail, definition, catalog, 10, 20).Experience == 12 &&
            CreatureRules.GainExperience(detail, definition, catalog, 10, -200).Experience == 0, "宠物装备经验修正保留截断及非负边界");
        That(CreatureRules.GainExperience(detail with { Experience = int.MaxValue - 1 }, definition, catalog, 10).Experience == int.MaxValue,
            "宠物累计经验加法不发生i32溢出");
        foreach (var inactive in new[] { detail with { Satiety = 0 }, detail with { Level = 4 }, detail with { Mode = 1 } })
            That(CreatureRules.GainExperience(inactive, definition, catalog, 3) == inactive, "饥饿、满级与道具成长模式不获得普通疲劳经验");
        That(CreatureRules.ValidName("") && CreatureRules.ValidName("pet") && !CreatureRules.ValidName("a\0b") &&
            !CreatureRules.ValidName(new string('a', 20)), "改名保留空名恢复入口并限制客户端容量和控制字符");
        Bytes(CreaturePackets.State(42, 30), [42, 0, 0, 0, 30, 0, 0, 0], "103读取i32 UID与i32饱食度");
        Bytes(CreaturePackets.Experience(new() { Level = 2, Experience = 5 }), [2, 0, 5, 0, 0, 0], "102包含等级与模式，不使用旧版五字节布局");
        Bytes(CreaturePackets.Experience(new() { Level = 3, Mode = 1, Experience = 25, Mode1Field0 = 7, Mode1Field1 = 9 }),
            [3, 1, 25, 0, 0, 0, 7, 9], "102道具成长扩展保留两个独立字节");
        Bytes(CreaturePackets.Evolution(513, 3), [1, 2, 3, 0], "106种类不得截为u8");
        Bytes(CreaturePackets.Renamed(3, "pet"), [3, 0, 3, 0, 0, 0, 112, 101, 116], "101通知的角色引用与DSTR名称");
        That(CreatureRequests.Rename([189, 0, 7, 3, 0, 0, 0, 112, 101, 116]) ==
            new CreatureRenameRequest(new(InventoryListType.Pet, 189), "pet"), "100源换名卡槽、容器与文本顺序按ARAD发送器");
        That(CreatureRequests.Response([3, 0]) == 3, "101互动请求仍读取客户端u16引用");
        Reject<InvalidDataException>(() => CreatureRequests.Rename([0, 0, 7, 9, 0, 0, 0]), "改名不接受截断字符串");
        Reject<InvalidDataException>(() => CreatureRequests.Response([]), "互动不沿用其它版本的空正文");
        var pet = new InventoryItem(63, CreatureRules.EquippedSlot, new(7001, ItemKind.Creature, 63)
            { Attr = 7, Marker16 = 42, Extension27 = 99 }) { Creature = detail };
        var food = new InventoryItem(64, new(InventoryListType.Pet, 189), new(24, ItemKind.CreatureConsumable, 3));
        Character character = new(1, 0, "CreatureTest", 1, new(1, 0, 10, 10), null) { Inventory = new([pet, food]) };
        var service = new CreatureService(catalog);
        var fed = service.Feed(character, new(food.Position, 1, 24, 0), 1000);
        That(fed.Accepted && fed.Plan!.After.Find(food.Position)?.Core.Count == 2 &&
            fed.Plan.After.Find(pet.Position)?.Creature?.Satiety == 40 && character.Inventory.Find(food.Position)?.Core.Count == 3,
            "饲料扣除与详情在同一纯规划，不改变原快照");
        var grown = service.Experience(character, 2, 1000).Plan!.After.Find(pet.Position)!;
        That(grown.Uid == pet.Uid && grown.Core == pet.Core with { ItemId = 7002, SealFlag = 0 } &&
            grown.Creature is { Level: 2, Experience: 2, Satiety: 10 }, "自动进化保留完整UID、核心扩展、经验与饱食");
        That(!service.Feed(character, new(food.Position, 1, 24, 1), 1000).Accepted &&
            !service.Feed(character with { Inventory = character.Inventory with { Items = [pet, food with { Core = food.Core with { EquipmentLockId = 1 } }] } },
                new(food.Position, 1, 24, 0), 1000).Accepted, "喂食不能绕过未知参数和安全锁");
        var growth = definition with { UsesExperienceItems = true, MaximumLevel = 50, ItemsPerLevel = 30, SelectEvolutionLevel = 2,
            SelectiveGrowth = new(100, 101, 11, 12, 2, 10, -1, -1, 0) };
        var growing = new CreatureDetail { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5, Experience = 29 };
        That(CreatureRules.GainItemExperience(growing, growth, catalog, 100) is { Level: 2, Experience: 30, Mode1Field1: 6 },
            "A类成长道具增加亲密度，升级除数按CRE读取而不是写死25");
        That(CreatureRules.GainItemExperience(growing with { Mode1Field1 = 0 }, growth, catalog, 101).Mode1Field1 == 0 &&
            CreatureRules.GainItemExperience(growing with { Mode1Field1 = 10 }, growth, catalog, 100).Mode1Field1 == 10,
            "B类减少亲密，A/B亲密度夹在0到10");
        Reject<InvalidDataException>(() => CreatureRules.GainItemExperience(growing, growth, catalog, 102), "未声明的成长道具不能加经验");
        Reject<InvalidDataException>(() => CreatureRules.GainItemExperience(growing with { Level = 2 }, growth, catalog, 100),
            "达到分支选择等级后不能绕过进化边界继续喂食");
        var expGrowth = growth with { SelectiveGrowth = growth.SelectiveGrowth! with { ExperienceItem = 102, ExperienceValue = 5 } };
        That(CreatureRules.GainItemExperience(growing, expGrowth, catalog, 102) is { Experience: 34, Level: 2, Mode1Field1: 5 },
            "EXP类成长增量来自CRE，不改变A/B亲密度");
        var limited = character with { Inventory = character.Inventory with { Items =
            [pet with { Creature = detail with { ExpireTime = 1020 } }, food with { Core = food.Core with { ExpireTime = 1010 } }] } };
        var timely = service.Feed(limited, new(food.Position, 1, 24, 0), 1000);
        That(timely.Accepted && timely.Plan!.CharacterPrecondition!.Matches(limited, 1009) &&
            !timely.Plan.CharacterPrecondition.Matches(limited, 1010), "喂食以宠物及源饲料最早期限为提交边界");
        Console.WriteLine($"PASS {Count - before} 宠物资源、成长、饱食与匹配原生协议断言");
    }
}
