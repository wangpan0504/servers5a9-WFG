using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
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
        Console.WriteLine("PASS helper-safety");
    }
}
