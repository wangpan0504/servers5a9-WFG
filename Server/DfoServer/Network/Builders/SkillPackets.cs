using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class SkillPackets
{
    public static byte[] Initialized(CharacterLearningState state)
    {
        Validate(state, requireFullTrees: true);
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            Field(writer, 1, 19);
            foreach (CharacterSkillTree tree in state.Trees.OrderBy(tree => tree.Context))
            {
                using var treeBytes = new MemoryStream();
                using (var treeWriter = new BinaryWriter(treeBytes, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    // required 字段即使值为零，也必须有 tag 和值。
                    Field(treeWriter, 1, tree.Sp);
                    Field(treeWriter, 2, tree.Tp);
                    foreach (LearnedSkill skill in tree.Skills.OrderBy(skill => skill.Slot))
                    {
                        using var skillBytes = new MemoryStream();
                        using (var skillWriter = new BinaryWriter(skillBytes, System.Text.Encoding.UTF8, leaveOpen: true))
                        {
                            Field(skillWriter, 1, skill.Slot);
                            Field(skillWriter, 2, skill.Id);
                            Field(skillWriter, 3, skill.Level);
                        }
                        Message(treeWriter, 3, skillBytes.ToArray());
                    }
                }
                Message(writer, 2, treeBytes.ToArray());
            }
        }
        using var body = new MemoryStream();
        using var output = new BinaryWriter(body);
        output.Write(checked((uint)payload.Length));
        output.Write(payload.ToArray());
        return body.ToArray();
    }

    public static byte[] Success(byte context, byte mode, CharacterLearningState state, IReadOnlyList<LearnedSkill> changes)
    {
        Validate(state, requireFullTrees: false);
        ArgumentNullException.ThrowIfNull(changes);
        if (context > 1 || mode > 1 || changes.Count is < 1 or > 255 ||
            changes.Any(skill => skill is null || skill.Slot == byte.MaxValue || skill.Level == 0) ||
            changes.Select(skill => skill.Slot).Distinct().Count() != changes.Count ||
            changes.Select(skill => skill.Id).Distinct().Count() != changes.Count)
            throw new InvalidDataException("学习成功回复的上下文、模式或改变列表非法");
        CharacterSkillTree tree = state.Trees.Single(tree => tree.Context == context);
        if (changes.Any(change => !tree.Skills.Contains(change)))
            throw new InvalidDataException("学习成功改变不匹配已提交绑定");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(context);
        writer.Write(tree.Sp);
        writer.Write(tree.Tp);
        writer.Write(checked((byte)changes.Count));
        foreach (LearnedSkill skill in changes)
        {
            writer.Write(skill.Slot);
            writer.Write(skill.Id);
            writer.Write(skill.Level);
            writer.Write((byte)0); // 普通切片不伪造 hasExtra 扩展。
        }
        writer.Write(mode);
        return stream.ToArray();
    }

    public static byte[] Failure(ushort knownCode)
    {
        if (knownCode is not (0x12 or 0x15 or 0x16))
            throw new InvalidDataException("学习失败回复只能使用已确认错误码");
        return [0, (byte)knownCode, (byte)(knownCode >> 8)];
    }

    private static void Validate(CharacterLearningState state, bool requireFullTrees)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Trees is null || state.Trees.Any(tree => tree is null || tree.Skills is null || (requireFullTrees && tree.Skills.Count == 0) || tree.Skills.Any(skill => skill is null)))
            throw new InvalidDataException("完整学习投影缺少真实技能绑定");
        state.Validate();
        if (!state.Initialized)
            throw new InvalidDataException("技能树尚未初始化");
    }

    private static void Field(BinaryWriter writer, uint field, uint value)
    {
        Varint(writer, field << 3);
        Varint(writer, value);
    }

    private static void Message(BinaryWriter writer, uint field, byte[] message)
    {
        Varint(writer, (field << 3) | 2);
        Varint(writer, checked((uint)message.Length));
        writer.Write(message);
    }

    private static void Varint(BinaryWriter writer, uint value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }
}
