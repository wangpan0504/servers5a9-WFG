namespace DfoServer.Game.Characters;

internal static class CharacterSkillVariant
{
    // 当前匹配PVF的notichangeskillinfo.etc只有这两项；剑魂还要求已觉醒。
    // 这里只投影已保存的当前树，不把装备加级、备用树或同编号的其它职业技能当成已学习。
    public static byte Read(Character character)
    {
        ushort skill = (character.Job, character.GrowType & 15) switch
        {
            (0, 1) when (character.GrowType >> 4) > 0 => 259,
            (4, 1) => 258,
            _ => 0
        };
        if (skill == 0) return 0;
        var learning = character.Learning;
        return (byte)(learning.Initialized
            ? learning.Trees.Single(tree => tree.Context == learning.ActiveContext).Skills.Any(row => row.Id == skill && row.Level > 0) ? 1 : 0
            : character.Skills.Any(row => row.Id == skill && row.Level > 0) ? 1 : 0);
    }
}
