namespace DfoServer.Game.Characters;

internal sealed record LearnedSkill(byte Slot, ushort Id, byte Level)
{
    // 原始按键字节的规范十六进制，避免快照之间共享可变数组。
    public string Command { get; init; } = "";
}
internal sealed record SkillCombo(ushort Root, IReadOnlyList<ushort> Children);
internal sealed record CharacterSkillTree(byte Context, ushort Sp, ushort Tp, IReadOnlyList<LearnedSkill> Skills)
{
    public IReadOnlyList<SkillCombo> Combos { get; init; } = Array.Empty<SkillCombo>();
    public bool SameCombos(CharacterSkillTree other) => Combos.Count == other.Combos.Count &&
        Combos.Zip(other.Combos).All(pair => pair.First.Root == pair.Second.Root && pair.First.Children.SequenceEqual(pair.Second.Children));
    public CharacterSkillTree Freeze() => this with { Skills = Array.AsReadOnly(Skills.ToArray()),
        Combos = Array.AsReadOnly(Combos.Select(combo => combo with { Children = Array.AsReadOnly(combo.Children.ToArray()) }).ToArray()) };
}
internal sealed record CharacterLearningState(ulong Experience, IReadOnlyList<CharacterSkillTree> Trees)
{
    // null 表示存量角色的额外来源尚未确认；不能把历史余额推断成赠点。
    public int? BonusSp { get; init; }
    public int? BonusTp { get; init; }
    public long ExpertContractExpiresAt { get; init; }
    // 无树表示尚未按匹配资源建立绑定，不向客户端发送空树冒充完整点数初始化。
    public static CharacterLearningState Uninitialized { get; } = new(0, Array.AsReadOnly(Array.Empty<CharacterSkillTree>()));
    public bool Initialized => Trees.Count == 2;

    public void Validate()
    {
        if (Experience > long.MaxValue || Trees.Count is not (0 or 2) || BonusSp is < 0 or > 65535 || BonusTp is < 0 or > 65535 || ExpertContractExpiresAt < 0)
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
            foreach (var skill in tree.Skills)
                if (skill.Command.Length > 510 || skill.Command.Length % 2 != 0 ||
                    skill.Command.Any(value => value is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
                    throw new InvalidDataException("技能指令字节非法");
            if (tree.Combos.Count > 6 || tree.Combos.Select(combo => combo.Root).Distinct().Count() != tree.Combos.Count ||
                tree.Combos.Any(combo => combo.Children.Count > 6 || !tree.Skills.Any(skill => skill.Id == combo.Root) ||
                    combo.Children.Any(id => !tree.Skills.Any(skill => skill.Id == id))) ||
                tree.Combos.SelectMany(combo => combo.Children).Distinct().Count() != tree.Combos.Sum(combo => combo.Children.Count))
                throw new InvalidDataException("连招根、子技能或容量非法");
        }
    }
}
