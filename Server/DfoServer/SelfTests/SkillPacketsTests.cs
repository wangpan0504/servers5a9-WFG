using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;

namespace DfoServer;

internal static class SkillPacketsTests
{
    public static void Run()
    {
        int checks = Count;
        Bytes(SkillPackets.Guide([1, 0x1234], 5), [2, 1, 0, 5, 0x34, 0x12, 5],
            "1497使用u8条数、完整u16技能编号及单字节次数");
        Bytes(SkillPackets.Guide([1], 0), [1, 1, 0, 0], "低等级显式清除同客户端前一角色的计数");
        That(DfoServer.Game.Skills.SkillGuidePolicy.DisplayCount(14) == 0 &&
            DfoServer.Game.Skills.SkillGuidePolicy.DisplayCount(15) == 5 &&
            DfoServer.Game.Skills.SkillGuidePolicy.DisplayCount(95) == 5, "技能引导15级边界包含满级");
        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                That(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL expected packet rejection: " + name);
        }
        Reject(() => SkillPackets.Guide([1, 1], 5), "技能引导拒绝重复编号");
        Reject(() => SkillPackets.Guide(Enumerable.Range(0, 256).Select(id => (ushort)id).ToArray(), 5), "技能引导不截断256项计数");
        var zero = State(new(0, 0, 0, [new(0, 5, 1)]), new(1, 0, 0, [new(0, 5, 1)]));
        byte[] sample = Convert.FromHexString("1E0000000813120C080010001A06080010051801120C080010001A06080010051801");
        That(SkillPackets.Initialized(zero).SequenceEqual(sample), "fixed NOTI19 sample includes zero-valued required fields and two actual bindings");
        var decoded = DecodeInitialized(sample);
        var explicitDefaults = DecodeInitialized(SkillPackets.Initialized(zero, _ => "0C04"));
        That(explicitDefaults.All(tree => tree.Skills.Single().Command == "0C04") &&
            zero.Trees.All(tree => tree.Skills.Single().Command.Length == 0),
            "显式默认指令进入两树field4但不修改学习存档");
        var customized = State(zero.Trees[0] with { Skills = [new(0, 5, 1) { Command = "030304" }] }, zero.Trees[1]);
        var mixed = DecodeInitialized(SkillPackets.Initialized(customized, _ => "0C04"));
        That(mixed[0].Skills.Single().Command == "030304" && mixed[1].Skills.Single().Command == "0C04",
            "每树已有指令优先，另一树独立补默认");
        That(decoded.Length == 2 && decoded.All(tree => tree.Sp == 0 && tree.Tp == 0 && tree.Skills.Single() == new LearnedSkill(0, 5, 1)),
            "independent protobuf reader verifies required presence and real slot0");
        byte[] ack = SkillPackets.Success(0, 0, zero, [new(0, 5, 1)]);
        That(ack.SequenceEqual(Convert.FromHexString("01000000000001000500010000")) && ack.Length == 13, "single ACK29 exact13B sample");
        var parsedAck = DecodeSuccess(ack);
        That(parsedAck.Context == 0 && parsedAck.Mode == 0 && parsedAck.Sp == 0 && parsedAck.Tp == 0 && parsedAck.Skills.Single() == new LearnedSkill(0, 5, 1),
            "ACK slot byte is actual binding not a status placeholder");
        Bytes(SkillPackets.Growth(0, zero, [new(0, 5, 1)]), ack,
            "成长差量复用原生ACK29布局，不新增协议模式");
        var restore = DecodeSuccess(SkillPackets.Growth(0, zero, []), allowEmpty: true);
        Bytes(SkillPackets.Success(0, 1, zero, []), [1, 0, 0, 0, 0, 0, 0, 1], "零变化自动加点保留mode1及零条计数");
        Bytes(SkillPackets.TrainingSuccess(0, 3, zero, []), [1, 0, 0, 0, 0, 0, 0, 3], "零变化训练确认仍走mode3任务完成回调");
        Bytes(SkillPackets.TrainingSuccess(1, 3, zero, []), [1, 1, 0, 0, 0, 0, 0, 3], "第二套训练确认保留原生context1，不混用第一套绑定和余额");
        Bytes(SkillPackets.ResetFailure(), [0,19,0], "491拒绝以原生已确认错误尾结束等待，不伪造清空成功");
        Reject(() => SkillPackets.Success(0, 0, zero, []), "普通学习仍不接受空改变成功包");
        That(restore.Context == 0 && restore.Mode == 0 && restore.Skills.Length == 0 && restore.Sp == 0 && restore.Tp == 0,
            "原生零项更新只恢复主树及绝对点数，不伪造技能改变");
        var rich = State(new(1, 1234, 987, [new(254, 65535, 255), new(7, 300, 128)]),
            new(0, 65535, 65535, [new(0, 128, 1), new(130, 40000, 200)]));
        var richTrees = DecodeInitialized(SkillPackets.Initialized(rich));
        That(richTrees[0].Sp == 65535 && richTrees[0].Tp == 65535 && richTrees[1].Sp == 1234 && richTrees[1].Tp == 987,
            "serialized tree order is context0 then1 even when model order reversed");
        That(richTrees[1].Skills.SequenceEqual(new[] { new LearnedSkill(7, 300, 128), new LearnedSkill(254, 65535, 255) }),
            "multibyte varints preserve high slots IDs levels and canonical slot ordering");
        var singleContext = State(zero.Trees[0], new(1, 0, 0, []));
        That(SkillPackets.Success(0, 0, singleContext, [new(0, 5, 1)]).Length == 13,
            "ACK validates selected committed binding without requiring full other-context initialization projection");
        var manyBindings = Enumerable.Range(0, 100).Select(index => new LearnedSkill((byte)index, (ushort)(50000 + index), 1)).ToArray();
        var many = State(new(0, 65535, 0, manyBindings), new(1, 0, 65535, manyBindings));
        var manyTrees = DecodeInitialized(SkillPackets.Initialized(many));
        That(manyTrees.All(tree => tree.Skills.Length == 100) && manyTrees[0].Skills[99].Id == 50099,
            "multibyte nested message lengths remain bounded and preserve all100 real bindings");
        byte[] multi = SkillPackets.Success(1, 1, rich, [new(254, 65535, 255), new(7, 300, 128)]);
        var multiAck = DecodeSuccess(multi);
        That(multi.Length == 18 && multiAck.Context == 1 && multiAck.Mode == 1 && multiAck.Sp == 1234 && multiAck.Tp == 987 && multiAck.Skills.Length == 2,
            "multi ACK uses5B entries and selected context balances");
        That(multiAck.Skills[0] == new LearnedSkill(254, 65535, 255), "ACK retains accepted command change order");
        foreach (ushort code in new ushort[] { 0x12, 0x15, 0x16 })
            That(SkillPackets.Failure(code).SequenceEqual(new byte[] { 0, (byte)code, 0 }), "known failure complete three-byte contract");
        Reject(() => SkillPackets.Failure(0), "unknown failure code never fabricated");
        Reject(() => SkillPackets.Initialized(CharacterLearningState.Uninitialized), "uninitialized state cannot be serialized");
        Reject(() => SkillPackets.Initialized(State(new(0, 0, 0, []), new(1, 0, 0, []))), "empty trees cannot impersonate full initialized skills");
        Reject(() => SkillPackets.Initialized(State(new(0, 0, 0, [new(255, 5, 1)]), new(1, 0, 0, [new(0, 5, 1)]))), "FF binding rejected");
        Reject(() => SkillPackets.Initialized(State(new(0, 0, 0, [new(0, 5, 1), new(0, 6, 1)]), new(1, 0, 0, [new(0, 5, 1)]))), "duplicate binding slots rejected");
        Reject(() => SkillPackets.Initialized(State(new(0, 0, 0, [new(0, 5, 1), new(1, 5, 1)]), new(1, 0, 0, [new(0, 5, 1)]))), "duplicate binding IDs rejected");
        Reject(() => SkillPackets.Initialized(State(zero.Trees[0], zero.Trees[0])), "duplicate contexts rejected");
        Reject(() => SkillPackets.Success(2, 0, zero, [new(0, 5, 1)]), "invalid ACK context rejected");
        Reject(() => SkillPackets.Success(0, 3, zero, [new(0, 5, 1)]), "unsupported ACK mode rejected");
        Reject(() => SkillPackets.Success(0, 0, zero, []), "empty success changes rejected");
        Reject(() => SkillPackets.Success(0, 0, zero, [new(0, 5, 2)]), "uncommitted new level rejected");
        Reject(() => SkillPackets.Success(0, 0, zero, [new(1, 5, 1)]), "uncommitted slot mapping rejected");
        Reject(() => SkillPackets.Success(0, 0, zero, [new(0, 5, 1), new(0, 5, 1)]), "duplicate ACK changes rejected");
        for (int length = 0; length < sample.Length; length++)
        {
            byte[] shortBody = sample[..length];
            Reject(() => DecodeInitialized(shortBody), "all truncated NOTI19 prefixes rejected");
        }
        Reject(() => DecodeInitialized([.. sample, 0]), "overlong NOTI19 body rejected");
        for (int length = 0; length < ack.Length; length++)
        {
            byte[] shortBody = ack[..length];
            Reject(() => DecodeSuccess(shortBody), "all truncated ACK prefixes rejected");
        }
        Reject(() => DecodeSuccess([.. ack, 0]), "overlong ACK body rejected");
        byte[] absentRequired = Convert.FromHexString("1C0000000813120A08001A06080010051801120C080010001A06080010051801");
        Reject(() => DecodeInitialized(absentRequired), "missing TP required field rejected even when intended value is zero");
        byte[] absentSlot = Convert.FromHexString("1C0000000813120A080010001A0410051801120C080010001A06080010051801");
        Reject(() => DecodeInitialized(absentSlot), "missing slot required field is not replaced by zero");
        byte[] wrongLength = (byte[])sample.Clone();
        wrongLength[7] = 13;
        Reject(() => DecodeInitialized(wrongLength), "nested message overread cannot borrow following tree bytes");
        byte[] badVarint = Convert.FromHexString("1F0000000813120D08800010001A06080010051801120C080010001A06080010051801");
        Reject(() => DecodeInitialized(badVarint), "noncanonical oversized zero varint rejected by independent decoder");
        Console.WriteLine($"PASS {Count - checks} skill packet protobuf and ACK boundary assertions");
    }

    private static CharacterLearningState State(CharacterSkillTree first, CharacterSkillTree second) => new(0, Array.AsReadOnly(new[] { first, second }));
    internal sealed record WireTree(ushort Sp, ushort Tp, LearnedSkill[] Skills);
    internal sealed record WireAck(byte Context, byte Mode, ushort Sp, ushort Tp, LearnedSkill[] Skills);

    internal static WireTree[] DecodeInitialized(ReadOnlySpan<byte> body)
    {
        Require(body.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(body) == body.Length - 4);
        var reader = new PbReader(body[4..]);
        Require(reader.UInt() == 8 && reader.UInt() == 19);
        var trees = new List<WireTree>();
        while (!reader.End)
        {
            Require(reader.UInt() == 18);
            var tree = new PbReader(reader.Message());
            Require(tree.UInt() == 8);
            uint sp = tree.UInt();
            Require(tree.UInt() == 16);
            uint tp = tree.UInt();
            Require(sp <= ushort.MaxValue && tp <= ushort.MaxValue);
            var skills = new List<LearnedSkill>();
            while (!tree.End)
            {
                Require(tree.UInt() == 26);
                var skill = new PbReader(tree.Message());
                Require(skill.UInt() == 8);
                uint slot = skill.UInt();
                Require(skill.UInt() == 16);
                uint id = skill.UInt();
                Require(skill.UInt() == 24);
                uint level = skill.UInt();
                var keys = new List<byte>();
                if (!skill.End)
                {
                    Require(skill.UInt() == 34);
                    var command = new PbReader(skill.Message());
                    while (!command.End)
                    {
                        uint key = command.UInt();
                        Require(key <= byte.MaxValue && keys.Count < 255);
                        keys.Add((byte)key);
                    }
                }
                Require(skill.End && slot < 255 && id <= ushort.MaxValue && level is > 0 and <= 255);
                skills.Add(new((byte)slot, (ushort)id, (byte)level) { Command = Convert.ToHexString(keys.ToArray()) });
            }
            Require(skills.Count is > 0 and <= 255 && skills.Select(skill => skill.Slot).Distinct().Count() == skills.Count &&
                skills.Select(skill => skill.Id).Distinct().Count() == skills.Count);
            trees.Add(new((ushort)sp, (ushort)tp, skills.ToArray()));
        }
        Require(trees.Count == 2);
        return trees.ToArray();
    }

    internal static WireAck DecodeSuccess(ReadOnlySpan<byte> body, bool allowEmpty = false)
    {
        var reader = new PbReader(body);
        Require(reader.Byte() == 1);
        byte context = reader.Byte();
        ushort sp = reader.Word();
        ushort tp = reader.Word();
        byte count = reader.Byte();
        Require(context <= 1 && (count > 0 || allowEmpty));
        var skills = new List<LearnedSkill>();
        for (int index = 0; index < count; index++)
        {
            byte slot = reader.Byte();
            ushort id = reader.Word();
            byte level = reader.Byte();
            byte hasCommand = reader.Byte();
            Require(slot != 255 && hasCommand <= 1);
            byte[] keys = hasCommand == 1 ? new byte[reader.Byte()] : [];
            for (int key = 0; key < keys.Length; key++) keys[key] = reader.Byte();
            skills.Add(new(slot, id, level) { Command = Convert.ToHexString(keys) });
        }
        byte mode = reader.Byte();
        Require(mode <= 1 && reader.End && skills.Select(skill => skill.Slot).Distinct().Count() == count &&
            skills.Select(skill => skill.Id).Distinct().Count() == count);
        return new(context, mode, sp, tp, skills.ToArray());
    }

    private static void Require(bool value)
    {
        if (!value)
            throw new InvalidDataException("技能包独立解码边界或required字段不匹配");
    }

    // 不依赖 Builder 的写入方法；每个内嵌 message 有独立有界 cursor。
    private ref struct PbReader
    {
        private readonly ReadOnlySpan<byte> source;
        private int offset;
        public PbReader(ReadOnlySpan<byte> source)
        {
            this.source = source;
            offset = 0;
        }
        public bool End => offset == source.Length;
        public byte Byte() => Take(1)[0];
        public ushort Word() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public ReadOnlySpan<byte> Message()
        {
            uint length = UInt();
            Require(length <= int.MaxValue);
            return Take((int)length);
        }
        public uint UInt()
        {
            uint value = 0;
            for (int index = 0; index < 5; index++)
            {
                byte next = Byte();
                Require(index < 4 || next < 16);
                value |= (uint)(next & 127) << (7 * index);
                if (next < 128)
                {
                    Require(index == 0 || next != 0);
                    return value;
                }
            }
            throw new InvalidDataException("varint溢出");
        }
        private ReadOnlySpan<byte> Take(int count)
        {
            Require(count >= 0 && count <= source.Length - offset);
            ReadOnlySpan<byte> value = source.Slice(offset, count);
            offset += count;
            return value;
        }
    }
}
