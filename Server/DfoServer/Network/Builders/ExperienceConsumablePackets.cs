using System.Buffers.Binary;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class ExperienceConsumablePackets
{
    public static byte[] Used(ExperienceConsumablePlan plan)
    {
        byte[] body = new byte[12];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), checked((ushort)plan.Source.Position.Index));
        body[3] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)plan.Growth.GainedExperience));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), checked((ushort)plan.Growth.LevelSpDelta));
        int tp = plan.Growth.Learning.Trees.Single(tree => tree.Context == 0).Tp - plan.Growth.Before.Trees.Single(tree => tree.Context == 0).Tp;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), checked((ushort)tp));
        return body;
    }

    public static byte[] Rejected() => [0, 19, 0];

    public static byte[] Legacy(LegacyStatusConsumablePlan plan)
    {
        byte[] body = new byte[12];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), checked((ushort)plan.Source.Position.Index));
        body[3] = (byte)plan.Definition.Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)plan.Definition.Amount));
        return body;
    }

    public static byte[] Permanent(PermanentGrowthPlan plan)
    {
        byte[] body = new byte[12];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), checked((ushort)plan.Source.Position.Index));
        body[3] = (byte)plan.Definition.Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)plan.Definition.Amount));
        // SP/TP 在 kind0/17 中已增加，尾部两项保持0，避免再次累计。
        return body;
    }
}
