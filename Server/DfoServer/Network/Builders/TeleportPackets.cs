using DfoServer.Game.Characters;
using DfoServer.Game.Town;

namespace DfoServer.Network.Builders;

internal static class TeleportPackets
{
    // 0234CC40成功分支读取源槽u16、模板i32；药效及扣减由该源的原生回调继续执行。
    public static byte[] Potion(PotionTeleportRequest source)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(source.Slot); writer.Write(source.ItemId);
        return stream.ToArray();
    }
    public static byte[] PotionRejected() => [0, 23, 0];
    // 01767510失败分支读取i32追加字段；成功仅按已穿戴光环启动冷却。
    public static byte[] SoloRejected() => [0, 1, 0, 0, 0, 0, 0];
    public static byte[] PrecheckRejected() => [0, 1, 0];

    public static byte[] Precheck(TeleportTarget target)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(target.Channel.Number); writer.Write((int)target.CharacterId);
        PacketEncoding.WriteString(writer, target.Name);
        // 027D7F60的等级/职业显示消费者：不是城镇、区域和保留位。
        writer.Write(target.Level); writer.Write(target.Job); writer.Write(target.GrowType);
        writer.Write(target.Channel.ServerIndex);
        return stream.ToArray();
    }

    // 0239D1F0: count:u8、type:u8、剩余秒数:i32；02B643C0再乘1000设置客户端计时器。
    public static byte[] Cooldown(Character character, long now)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write((byte)2);
        writer.Write((int)Math.Clamp((character.TeleportCooldowns.AuraUntil - now + 999) / 1000, 0, int.MaxValue));
        return stream.ToArray();
    }
}
