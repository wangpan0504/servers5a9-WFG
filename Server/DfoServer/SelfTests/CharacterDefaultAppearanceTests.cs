using System.Collections.Immutable;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CharacterDefaultAppearanceTests
{
    public static void RunCore()
    {
        int checks = Count;
        CharacterAwakeningEquipment[] rows = [new(14, 1, [100, 101, 102, 103, 104, 105, 106, 107, 108, 109, -1, 110, 111])];
        var appearance = new CharacterDefaultAppearance(rows);
        int[] coats = [40600, 44600, 48600, 52600, 56600, 1640000, 1760000, 29202,
            2120000, 40600, 52600, 112500000, 113500000, 114500000, 115500000, 116500000];
        for (byte job = 0; job < 16; job++)
            That(appearance.GetEquipment(job, 0, 3) == coats[job], "全职业原生基础上衣映射 " + job);
        That(appearance.GetEquipment(14, 1, 3) == 115500000 && appearance.GetEquipment(14, 0x11, 3) == 103 &&
            appearance.GetEquipment(14, 0x21, 3) == 103 && appearance.GetEquipment(14, 0x71, 3) == 103 &&
            appearance.GetEquipment(14, 0x11, 3, awakeningAppearanceEnabled: false) == 115500000,
            "分支与觉醒位独立解释，非零觉醒使用同一资源行，关闭觉醒外观时回基础");
        That(appearance.GetEquipment(14, 0x10, 3) == 115500000 && appearance.GetEquipment(14, 0x12, 3) == 115500000 &&
            appearance.GetEquipment(14, 0x11, 10) == -1 && appearance.GetEquipment(14, 0x11, 12) == 35000,
            "未转职、缺失分支回基础，觉醒表不接管武器装扮和实际武器");
        var clear = new CharacterDefaultAppearance([rows[0] with { Equipment = rows[0].Equipment.SetItem(3, 0).SetItem(8, -1) }]);
        That(clear.GetEquipment(14, 0x11, 3) == 0 && clear.GetEquipment(14, 0x11, 8) == 115580000,
            "资源显式0保留，只有-1才回基础外观");
        That(appearance.GetEquipment(0, 5, 8) == -1 &&
            appearance.GetEquipment(0, 5, 8, clientOption1Enabled: true) == 101581022 &&
            appearance.GetEquipment(0, 5, 8, clientOption1Enabled: false) == 42200 &&
            appearance.GetEquipment(12, 0, 24) == 113370002 && appearance.GetEquipment(16, 0, 3) == -1,
            "保留客户端选项未知的原生回退、骑士默认盾及未知职业边界");
        Console.WriteLine($"PASS {Count - checks} 统一角色普通与觉醒默认外观断言");
    }

    public static void RunResources(PvfArchive archive, CharacterCatalog characters, ItemCatalog items)
    {
        int checks = Count;
        var appearance = characters.DefaultAppearance;
        var rows = CharacterEquipmentFile.ReadAwakeningEquipment(archive.ReadTokens(CharacterDefaultAppearance.AwakeningPath));
        That(rows.Length == 80 && rows.Select(row => row.Job).Distinct().Count() == 16,
            "当前觉醒默认表包含十六职业八十个分支");
        foreach (var row in rows)
            for (byte slot = 0; slot <= 9; slot++)
            {
                byte job = (byte)row.Job, first = (byte)row.GrowType;
                int expected = row.Equipment[slot] == -1
                    ? appearance.GetEquipment(job, first, slot, clientOption1Enabled: false) : row.Equipment[slot];
                That(appearance.GetEquipment(job, (byte)(first | 0x10), slot, clientOption1Enabled: false) == expected &&
                    appearance.GetEquipment(job, (byte)(first | 0x20), slot, clientOption1Enabled: false) == expected,
                    "当前资源各分支一觉和二觉按同一原生规则选择 " + job + "/" + first + "/" + slot);
            }
        That(characters.Get(14).DefaultAvatarEquipment.IsEmpty && appearance.GetEquipment(14, 1, 3) == 115500000 &&
            appearance.GetEquipment(14, 0x11, 3) == 415500023 && appearance.GetEquipment(14, 0x11, 8) == 115580000 &&
            items.Get(115500000) is { Kind: ItemKind.Avatar, EquipmentSlot: 3 } &&
            items.Get(415500023) is { Kind: ItemKind.Avatar, EquipmentSlot: 3 },
            "缺少旧ETC的女圣职者仍有原生基础与觉醒上衣，觉醒缺省皮肤回本职业基础");
        Console.WriteLine($"PASS {Count - checks} 统一角色默认外观真实资源断言");
    }
}
