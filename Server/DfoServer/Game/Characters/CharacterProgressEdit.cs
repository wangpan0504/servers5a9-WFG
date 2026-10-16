using System.Collections.Immutable;

namespace DfoServer.Game.Characters;

internal enum CharacterProgressField
{
    Level,
    Story,
    Grow,
    QuestAdd,
    QuestRemove,
    QuestClear,
    Skill,
    BonusSp,
    ExpertContract
}

internal sealed record CharacterProgressEdit(CharacterProgressField Field, string Account, string Name, int Primary, int Secondary)
{
    // 仅 QuestAdd：同一事务写入的完成标记集合（整链或批量）；为空时使用 Primary。
    public ImmutableArray<int> Quests { get; init; } = [];
    public IEnumerable<int> QuestIds => Quests.IsDefaultOrEmpty ? [Primary] : Quests;
    public void Validate()
    {
        if (!Quests.IsDefaultOrEmpty && (Field != CharacterProgressField.QuestAdd || Quests.Length > 2000 ||
            Quests.Any(id => id is < 1 or > 29999) || Quests.Distinct().Count() != Quests.Length))
            throw new InvalidDataException("批量任务编号无效，范围1-29999且不能重复，最多2000个");
        if (Account.Length is < 1 or > 64 || Account.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidDataException("账号无效");
        if (!CharacterName.IsValid(Name))
            throw new InvalidDataException("角色名无效");
        switch (Field)
        {
            case CharacterProgressField.Level when Primary is < 1 or > 95:
                throw new InvalidDataException("等级范围是 1-95");
            case CharacterProgressField.Story when Primary is < 0 or > 95:
                throw new InvalidDataException("剧情接受等级范围是 0-95");
            case CharacterProgressField.Grow when Primary is < 0 or > 127:
                throw new InvalidDataException("觉醒字节范围是 0-127");
            case CharacterProgressField.QuestAdd or CharacterProgressField.QuestRemove when Primary is < 1 or > 29999:
                throw new InvalidDataException("任务编号范围是 1-29999");
            case CharacterProgressField.Skill when Primary is < 0 or > 65535 || Secondary is < 0 or > 255:
                throw new InvalidDataException("技能编号范围是 0-65535，等级范围是 0-255；0 表示删除");
            case CharacterProgressField.BonusSp when Primary is < 0 or > 50000:
                throw new InvalidDataException("额外SP范围是0-50000");
            case CharacterProgressField.ExpertContract when Primary < 0:
                throw new InvalidDataException("契约剩余秒数不能为负，0表示到期");
        }

        if (!Enum.IsDefined(Field))
            throw new InvalidDataException("不是写入命令");
    }
}
