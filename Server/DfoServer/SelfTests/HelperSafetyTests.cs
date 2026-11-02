using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HelperSafetyTests
{
    internal static void RunCore()
    {
        var equipment = new ItemTemplate(101, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 0, "free", 0, 0, "sample");
        var gear = new InventoryItem(10, new(InventoryListType.Main, 20), new(101, ItemKind.Equipment, 1));
        That(HelperSafetyPolicy.Operations(gear, equipment, false) == 2, "普通未打造装备允许分解或出售");
        That(HelperSafetyPolicy.Operations(gear with { Core = gear.Core with { SealFlag = 1 } }, equipment with { AttachType = "sealing" }, false) == 2,
            "普通封装掉落仍按玩家所选品级处理，不将封装状态误判为打造保护");
        That(HelperSafetyPolicy.Operations(gear, equipment, true) == 0, "换装登记优先保护");
        foreach (byte type in new byte[] { 130, 131, 2, 3 })
        {
            var baseAmplification = gear.Core with { AmplifyType = type, AmplifyValue = 7 };
            That(HelperSafetyPolicy.Operations(gear with { Core = baseAmplification }, equipment, false) == 2,
                "未净化气息及净化基础值不是增幅等级，可按玩家选择处理");
            That(HelperSafetyPolicy.Operations(gear with { Core = baseAmplification with { Attr = 1 } }, equipment, false) == 0,
                "真正强化或净化后增幅等级仍受保护");
        }
        foreach (var core in new[] {
            gear.Core with { EquipmentLockId = 1 }, gear.Core with { SortLockFlag = 1 },
            gear.Core with { IsRental = true }, gear.Core with { ExpireTime = 1 },
            gear.Core with { Attr = 1 }, gear.Core with { EnchantCardId = 100 }, gear.Core with { EnchantUpgrade = 1 },
            gear.Core with { AmplifyType = 128 }, gear.Core with { AmplifyValue = 1 },
            gear.Core with { GenuineUpgrade = 1 }, gear.Core with { EmancipateLevel = 1 },
            gear.Core with { EmblemSocketCount = 1 }, gear.Core with { Emblem1 = 1 }, gear.Core with { Emblem2 = 1 },
            gear.Core with { GuardianGemKeys = 1 }, gear.Core with { Rune = 1 },
            gear.Core with { DisjointForbidden = true } })
            That(HelperSafetyPolicy.Operations(gear with { Core = core }, equipment, false) == 0, "锁、期限、打造、打孔及镶嵌均不可自动销毁");
        foreach (var template in new[] { equipment with { EquipmentSlot = 13 }, equipment with { DisjointForbidden = true },
            equipment with { GrowthEquipment = true }, equipment with { PeriodEquipment = true }, equipment with { AttachType = "trade delete" } })
            That(HelperSafetyPolicy.Operations(gear, template, false) == 0, "称号、特殊及禁止销毁模板受保护");
        foreach (short slot in new short[] { 0, 3, 4, 5, 6, 7, 8, 4096 })
            That(HelperSafetyPolicy.Operations(gear with { Position = new(InventoryListType.Main, slot) }, equipment, false) == 0, "快捷栏和非法槽不能自动处理");
        That(HelperSafetyPolicy.Operations(gear with { Position = new(InventoryListType.Equipment, 12) }, equipment, false) == 0, "已穿戴装备不会进入自动处理");
        var material = equipment with { Id = 3033, Kind = ItemKind.Material, StackLimit = 100, EquipmentSlot = 0 };
        var stack = new InventoryItem(11, new(InventoryListType.Main, 354), new(3033, ItemKind.Material, 12));
        That(HelperSafetyPolicy.Operations(stack, material, false) == 1, "可交易普通材料可入账号仓库");
        That(HelperSafetyPolicy.Operations(stack, material with { AttachType = "[account]" }, false) == 1, "账号绑定材料可入账号仓库");
        foreach (short slot in new short[] { 3, 4, 5, 6, 7, 8 })
            That(HelperSafetyPolicy.Operations(stack with { Position = new(InventoryListType.Main, slot) }, material, false) == 1,
                "快捷栏普通材料可归仓，快捷栏装备保护不扩大至材料");
        foreach (short slot in new short[] { 0, 1, 2 })
            That(HelperSafetyPolicy.Operations(stack with { Position = new(InventoryListType.Main, slot) }, material, false) == 0,
                "货币投影槽不作为材料处理");
        foreach (var template in new[] { material with { AttachType = "sealing" }, material with { AccountCargoForbidden = true },
            material with { StackLimit = 11 }, material with { Kind = ItemKind.Profession }, material with { Kind = ItemKind.Quest } })
            That(HelperSafetyPolicy.Operations(stack with { Core = stack.Core with { Kind = template.Kind } }, template, false) == 0, "不可入仓或非普通材料不授权");
        var entry = HelperSafetyPackets.Entry(new(gear, 2));
        That(entry.StartsWith("20:", StringComparison.Ordinal) && entry.EndsWith(":2", StringComparison.Ordinal) && entry.Length == 21,
            "资格协议包含槽位、完整64位指纹和操作类别");
        That(entry != HelperSafetyPackets.Entry(new(gear with { Position = new(InventoryListType.Main, 21) }, 2)), "同模板换槽必须改变资格指纹");
        That(entry != HelperSafetyPackets.Entry(new(gear with { Core = gear.Core with { Attr = 1 } }, 2)), "物品属性变化必须改变资格指纹");
        That(HelperSafetyPackets.Entry(new(gear, 2, true)) == entry &&
            HelperSafetyPackets.EntryWithWearability(new(gear, 2, true)) == entry + ":1" &&
            HelperSafetyPackets.EntryWithWearability(new(gear, 2)) == entry + ":0",
            "旧资格条目逐字节不变，新字段不改变原指纹或操作授权");
        string page = string.Join('|', "1", new string('a', 32), ushort.MaxValue, long.MaxValue, 341, 342,
            string.Join(',', Enumerable.Repeat("4095:ffffffffffffffff:2:1", 12)));
        ServerNoticePackets.Build("95_HELPER_SAFE/2", page, out bool truncated);
        That(!truncated, "新协议最大身份与十二项分页不会截断");
        Wearability(equipment, gear);
        Console.WriteLine("PASS helper-safety");
    }

    static void Wearability(ItemTemplate equipment, InventoryItem gear)
    {
        var character = new Character(1, 0, "fixture", 0, new(1, 0, 10, 10), null) { Job = 0, GrowType = 1, Level = 1 };
        var epic = equipment with { Rarity = 4, Jobs = 1, MinimumLevel = 95 };
        That(HelperSafetyPolicy.WearableEpic(character, epic), "本职业低等级史诗按未来可穿保护");
        That(!HelperSafetyPolicy.WearableEpic(character with { Job = 1 }, epic) &&
            !HelperSafetyPolicy.WearableEpic(character with { Job = 11 }, epic), "其他职业与男女基础职业位不可混用");
        That(HelperSafetyPolicy.WearableEpic(character, epic with { Jobs = ushort.MaxValue, EquipmentSlot = 22 }),
            "通用防具饰品不按防具精通或未解锁槽位缩小保护");
        foreach (int rarity in new[] { 0, 1, 2, 3, 5, 6 })
            That(!HelperSafetyPolicy.WearableEpic(character, epic with { Rarity = rarity }), "只对史诗品级提供本职业元数据");
        That(!HelperSafetyPolicy.WearableEpic(character, epic with { Kind = ItemKind.Material }), "普通材料不伪装职业史诗");
        var required = epic with { RequiredSkill = 91 };
        SkillLearningDefinition Skill(byte job, ushort skill) => new(job, skill, "fixture.skl", SkillType.Passive,
            null, null, skill == 368 ? [0, 0, 0, 0, 0, 1] : [0, 1, 0, 0, 1, 0], null, null, null, null, false, false, []);
        That(HelperSafetyPolicy.WearableEpic(character, required, Skill) &&
            !HelperSafetyPolicy.WearableEpic(character with { GrowType = 2 }, required, Skill),
            "固定技能成长许可区分本分支与其他分支，不依赖当前已学技能");
        That(HelperSafetyPolicy.WearableEpic(character with { GrowType = 0x21 }, required, Skill), "觉醒不丢失本分支穿戴许可");
        var witch = character with { Job = 3, GrowType = 0x25 };
        var broom = required with { Jobs = 8, WeaponSubType = 4 };
        That(HelperSafetyPolicy.WearableEpic(witch, broom, Skill) &&
            !HelperSafetyPolicy.WearableEpic(witch with { GrowType = 2 }, broom, Skill),
            "小魔女扫把共用穿戴特例，其他女法师分支不扩大准入");
        That(HelperSafetyPolicy.WearableEpic(character, required, (_, _) => throw new KeyNotFoundException()) &&
            HelperSafetyPolicy.WearableEpic(character, required, (job, skill) => new(job, skill, "unknown.skl", SkillType.Unknown,
                null, null, null, null, null, null, null, false, false, [])),
            "技能资源缺失或成长声明未知时保守保护");
        That(HelperSafetyPolicy.Operations(gear, epic, false) == 2 &&
            HelperSafetyPolicy.Operations(gear with { Core = gear.Core with { Attr = 1 } }, epic, false) == 0 &&
            HelperSafetyPolicy.Operations(gear with { Core = gear.Core with { EquipmentLockId = 1 } }, epic, false) == 0,
            "职业元数据不改变出售或手动基础操作资格，锁与打造保护仍优先");
    }
}
