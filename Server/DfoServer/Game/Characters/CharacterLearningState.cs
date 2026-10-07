namespace DfoServer.Game.Characters;

internal sealed record LearnedSkill(byte Slot, ushort Id, byte Level);
internal sealed record CharacterSkillTree(byte Context, ushort Sp, ushort Tp, IReadOnlyList<LearnedSkill> Skills);
internal sealed record CharacterLearningState(ulong Experience, IReadOnlyList<CharacterSkillTree> Trees)
{
    // 无树表示尚未按匹配资源建立绑定，不向客户端发送空树冒充完整点数初始化。
    public static CharacterLearningState Uninitialized { get; } = new(0, Array.AsReadOnly(Array.Empty<CharacterSkillTree>()));
    public bool Initialized => Trees.Count == 2;

    public void Validate()
    {
        if (Experience > long.MaxValue || Trees.Count is not (0 or 2))
            throw new InvalidDataException("经验或技能树状态非法");
        if (Trees.Count == 0)
            return;
        if (!Trees.Select(tree => tree.Context).Order().SequenceEqual(new byte[] { 0, 1 }))
            throw new InvalidDataException("技能树上下文必须为0与1");
        foreach (var tree in Trees)
        {
            if (tree.Skills.Count > 255 || tree.Skills.Any(skill => skill.Slot == byte.MaxValue || skill.Level == 0) ||
                tree.Skills.Select(skill => skill.Slot).Distinct().Count() != tree.Skills.Count ||
                tree.Skills.Select(skill => skill.Id).Distinct().Count() != tree.Skills.Count)
                throw new InvalidDataException("技能树绑定、等级或数量非法");
        }
    }
}
