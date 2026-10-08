using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Testing;

namespace DfoServer;

internal static class SkillLayoutTests
{
    public static void Run()
    {
        int before = TestCheck.Count;
        var second = new List<LearnedSkill> { new(1, 5, 3) };
        var source = new CharacterLearningState(2035, Array.AsReadOnly<CharacterSkillTree>([
            new(0, 40, 4, Array.AsReadOnly<LearnedSkill>([new(0, 5, 1), new(1, 6, 2), new(102, 7, 1)])),
            new(1, 30, 2, second)]));
        SkillLayoutResult Move(byte from, byte to, byte context = 0, int index = -1, byte mode = 0) =>
            SkillLayout.Move(0, source, new(context, from, to, index, mode), Resolve);
        var swapped = Move(0, 1);
        TestCheck.That(swapped.Success && swapped.State!.Trees[0].Skills.Single(skill => skill.Id == 5) == new LearnedSkill(1, 5, 1) &&
            swapped.State.Trees[0].Skills.Single(skill => skill.Id == 6) == new LearnedSkill(0, 6, 2), "已占槽交换两项，保留ID和等级");
        TestCheck.That(swapped.State!.Experience == 2035 && swapped.State.Trees[0].Sp == 40 && swapped.State.Trees[0].Tp == 4 &&
            swapped.State.Trees[1].Sp == 30 && swapped.State.Trees[1].Tp == 2 && source.Trees[0].Skills[0].Slot == 0,
            "换槽不改经验、两树余额或原快照");
        var moved = Move(0, 6);
        TestCheck.That(moved.Success && moved.State!.Trees[0].Skills.Single(skill => skill.Id == 5).Slot == 6 &&
            moved.State.Trees[0].Skills.All(skill => skill.Slot != 0), "现场主快捷槽到扩展快捷空槽只移动源绑定");
        TestCheck.That(Move(0, 198).State!.Trees[0].Skills.Single(skill => skill.Id == 5).Slot == 198, "普通主动的旧198槽绑定仍可操作");
        TestCheck.That(Move(102, 105).State!.Trees[0].Skills.Single(skill => skill.Id == 7).Slot == 105, "被动在非快捷区域移动仍保留等级");
        TestCheck.That(Move(102, 0).Failure == SkillLearningFailure.Unsupported && Move(0, 102).Failure == SkillLearningFailure.Unsupported &&
            Move(102, 198).Failure == SkillLearningFailure.Unsupported, "交换两个方向均不得把被动放入主或扩展快捷槽");
        foreach (byte quick in Enumerable.Range(6, 6).Select(slot => (byte)slot))
            TestCheck.That(Move(102, quick).Failure == SkillLearningFailure.Unsupported && Move(0, quick).Success,
                "实际扩展快捷槽只接受主动 " + quick);
        TestCheck.That(Move(0, 0).Success && ReferenceEquals(Move(0, 0).State, source), "同槽确认复用原快照，不制造变化");
        TestCheck.That(Move(9, 10).Failure == SkillLearningFailure.MissingSkill, "空源槽不能伪造已有技能");
        foreach (byte invalid in new byte[] { 138, 149, 204, 255 })
            TestCheck.That(Move(0, invalid).Failure == SkillLearningFailure.InvalidRequest && Move(invalid, 0).Failure == SkillLearningFailure.InvalidRequest,
                "预留或范围外槽位双向拒绝 " + invalid);
        TestCheck.That(Move(0, 1, context: 2).Failure == SkillLearningFailure.InvalidRequest, "未知上下文不能改其他树");
        TestCheck.That(Move(0, 1, index: 0).Failure == SkillLearningFailure.Unsupported && Move(0, 1, mode: 2).Failure == SkillLearningFailure.Unsupported,
            "未闭合额外上下文与模式不套普通交换");
        TestCheck.That(Move(1, 6, context: 1).State!.Trees[1].Skills.Single() == new LearnedSkill(6, 5, 3), "独立context1只改对应布局");
        var reversed = SkillLayout.Move(0, swapped.State!, new(0, 0, 1, -1, 0), Resolve);
        TestCheck.That(reversed.State!.Trees[0].Skills.SequenceEqual(source.Trees[0].Skills), "再次相同交换是反向操作，不按报文伪造幂等");
        second.Clear();
        TestCheck.That(swapped.State!.Trees[1].Skills.Single() == new LearnedSkill(1, 5, 3), "结果冻结未修改的上下文集合");
        var legacy = source with { Trees = Array.AsReadOnly<CharacterSkillTree>([
            source.Trees[0] with { Skills = Array.AsReadOnly<LearnedSkill>([new(0, 5, 1), new(3, 7, 1)]) }, source.Trees[1]]) };
        var kept = SkillLayout.Move(0, legacy, new(0, 0, 6, -1, 0), Resolve);
        TestCheck.That(kept.Success && kept.State!.Trees[0].Skills.Single(skill => skill.Id == 7).Slot == 3,
            "无关旧错误绑定保持原样，不附带存量迁移");
        TestCheck.That(SkillLayout.Move(9, source, new(0, 0, 1, -1, 0), Resolve).Success &&
            SkillLayout.Move(0, CharacterLearningState.Uninitialized, new(0, 0, 1, -1, 0), Resolve).Failure == SkillLearningFailure.Unsupported,
            "特殊职业普通换槽可用，未初始化角色保持拒绝");
        TestCheck.That(SkillLayout.Move(0, source, new(0, 0, 1, -1, 0), (_, _) => throw new KeyNotFoundException()).Failure == SkillLearningFailure.MissingSkill,
            "缺资源类型不猜快捷栏资格");

        byte[] wire = [0, 0, 6, 255, 255, 255, 255, 0, 0x28, 0x25, 0x86, 0xAC];
        var parsed = SkillRequests.ParseSlotChange(wire);
        TestCheck.That(parsed.Context == 0 && parsed.From == 0 && parsed.To == 6 && parsed.ContextIndex == -1 && parsed.Mode == 0 &&
            parsed.BodyChecksum == 0xAC862528 && parsed.ChecksumMatches == true, "现场12字节正文正确分离8字节字段与检查尾");
        var bare = SkillRequests.ParseSlotChange(wire[..8]);
        TestCheck.That(bare.ContextIndex == -1 && bare.BodyChecksum is null && bare.ChecksumMatches is null, "直接8字节写出形状不伪造检查值");
        wire[^1] ^= 1;
        TestCheck.That(SkillRequests.ParseSlotChange(wire).ChecksumMatches == false && parsed.ChecksumMatches == true,
            "检查尾仅报告匹配状态，解析结果不依赖可变输入");
        foreach (int length in new[] { 0, 3, 7, 9, 11, 13 })
            Reject(() => SkillRequests.ParseSlotChange(new byte[length]), "未知换槽宽度拒绝 " + length);
        TestCheck.Bytes(SkillPackets.SlotChanged(0, 0, 6), [1, 0, 0, 6], "ACK28仅成功前缀与三个真实槽位字段");
        Reject(() => SkillPackets.SlotChanged(2, 0, 1), "ACK不规范化未知上下文");
        Console.WriteLine($"PASS {TestCheck.Count - before} 技能换槽纯规则、实收正文和ACK断言");
    }

    static SkillLearningDefinition Resolve(byte job, ushort id) => new(job, id, "synthetic.skl",
        id == 7 ? SkillType.Passive : SkillType.Active, [5], [10, 10, 10, 10, 10, 10], [10, 10, 10, 10, 10, 10],
        1, 0, [], [0, 0, 0, 0, 0, 0], false, false, [], 4, 6);

    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { TestCheck.That(true, name); return; }
        throw new InvalidOperationException("Expected rejection: " + name);
    }
}
