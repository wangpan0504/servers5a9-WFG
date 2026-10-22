using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Skills;

internal static class SkillSlotAllocator
{
    public static int Allocate(SkillLearningDefinition skill, HashSet<byte> occupied, bool initial = false)
    {
        if (skill.SkillId is >= 200 and <= 211)
            return occupied.Contains((byte)(138 + skill.SkillId - 200)) ? -1 : 138 + skill.SkillId - 200;
        if (skill.Job == 9 && !skill.HasComboDeclaration && skill.Type == SkillType.Active && skill.SkillId != 169)
        {
            int arrangement = FirstFree(occupied, 150, 198);
            if (arrangement >= 0)
                return arrangement;
            // 初始建角沿用原布局：下排排满即失败，不落到上排快捷栏。
            if (initial)
                return -1;
            // 新学主动只进下排排列区；六列排满后进入普通技能区，绝不占用上排快捷栏，
            // 避免客户端自动编排把同一技能同时铺进快捷栏和排列造成重复摆放。
            int stored = FirstFree(occupied, 12, 138);
            if (stored >= 0)
                return stored;
            return -1;
        }
        if (skill.Type == SkillType.Active)
        {
            int slot = FirstFree(occupied, 0, 12);
            if (slot >= 0)
                return slot;
        }

        // 初始被动沿用角色初始布局；后续学习按资源分组，不能落进快捷槽。
        int? group = initial && skill.Type == SkillType.Passive ? 2 : skill.SlotGroup;
        return group switch
        {
            0 => FirstFree(occupied, 12, 54),
            1 => FirstFree(occupied, 54, 102),
            // 138–149 是特殊技能映射的预留槽，普通绑定不能占用。
            2 => FirstFree(occupied, 102, 138),
            3 => FirstFree(occupied, 150, 198),
            _ => -1
        };
    }

    // 当前可见12栏使用0–11；旧198–203绑定继续限制为主动，但不作为新分配的快捷空位。
    public static bool QuickSlot(byte slot) => slot < 12 || slot is >= 198 and < 204;

    public static int FirstFree(HashSet<byte> occupied, int start, int end)
    {
        for (int slot = start; slot < end; slot++)
        {
            if (!occupied.Contains((byte)slot))
                return slot;
        }
        return -1;
    }
}
