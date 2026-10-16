using DfoServer.GameWorld.Characters;

namespace DfoServer.Network.Builders;

internal static class CharacterStatsPackets
{
    public const int BodyLength = 89;
    public static byte[] Build(CharacterStats stats, byte staminaPercent = 100)
    {
        using var stream = new MemoryStream(BodyLength);
        using var writer = new BinaryWriter(stream);
        writer.Write(stats.MaxHp);
        writer.Write(stats.MaxMp);
        writer.Write(stats.PhysicalAttack);
        writer.Write(stats.PhysicalDefense);
        writer.Write(stats.MagicalAttack);
        writer.Write(stats.MagicalDefense);
        writer.Write(stats.FireResistance);
        writer.Write(stats.WaterResistance);
        writer.Write(stats.DarkResistance);
        writer.Write(stats.LightResistance);
        writer.Write(new byte[36]); // 18个有符号short分量，业务标签未闭合；不能当作9个DWORD总属性。
        writer.Write(stats.InventoryLimit);
        writer.Write((ushort)0);
        writer.Write(stats.MpRecovery);
        writer.Write(stats.MoveSpeed);
        writer.Write(stats.AttackSpeed);
        writer.Write(stats.CastSpeed);
        writer.Write(stats.HitRecovery);
        writer.Write(stats.JumpPower);
        writer.Write(stats.Weight);
        writer.Write(staminaPercent); // 本人虚弱百分比与NOTI4共用同一客户端字段。
        writer.Write(0f); // 当前合同固定字段，不对应 CHR 的 jump speed。
        if (stream.Length != BodyLength)
            throw new InvalidDataException("Client character stats size changed");
        return stream.ToArray();
    }
}
