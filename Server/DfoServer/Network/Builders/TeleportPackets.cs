using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Items;

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

    // ARAD 0234F540：失败公共error:u16后仍读town:u8、剩余秒:i32及请求来源:u8，并清理本地等待。
    public static byte[] PartyRejected(PartyTeleportRequest request, int remainingSeconds = 0, ushort error = 19)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)0); writer.Write(error); writer.Write(request.Target.Town); writer.Write(remainingSeconds); writer.Write(request.Source);
        return stream.ToArray();
    }

    // 023D5340：发起人是队伍槽号，随后固定八行；唯一成员为已同意，其余用FFFF空槽。
    public static byte[] PartyReady(Character character)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(character.Position.Town); writer.Write((byte)1); writer.Write((byte)1); writer.Write((ushort)0);
        for (int slot = 0; slot < 8; slot++)
        {
            writer.Write(slot == 0 ? character.Id : ushort.MaxValue); writer.Write((byte)(slot == 0 ? 1 : 0));
        }
        writer.Write((byte)0);
        return stream.ToArray();
    }

    public static byte[] PrepareAuraEffect(ushort actor) => [3, 1, (byte)actor, (byte)(actor >> 8)];

    // 023AA760：阶段1出发、2到达，随后为u8数量及u16角色ID；不是坐标或光环物品编号。
    public static byte[] AuraEffect(ushort actor, bool arrival) => [(byte)(arrival ? 2 : 1), 1, (byte)actor, (byte)(actor >> 8)];

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
    public static byte[] Cooldown(Character character, long now, AuraAbilityKind? refreshed = null)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var entries = new[]
        {
            (Kind: AuraAbilityKind.PartyTeleport, Type: (byte)0, Until: character.TeleportCooldowns.PartyAuraUntil),
            (Kind: AuraAbilityKind.SoloTeleport, Type: (byte)2, Until: character.TeleportCooldowns.AuraUntil),
            (Kind: AuraAbilityKind.UpgradeSoloTeleport, Type: (byte)3, Until: character.TeleportCooldowns.UpgradeAuraUntil)
        }.Where(row => row.Until > now || row.Kind == refreshed).ToArray();
        writer.Write((byte)entries.Length);
        foreach (var row in entries)
        {
            writer.Write(row.Type);
            writer.Write(RemainingSeconds(row.Until, now));
        }
        return stream.ToArray();
    }

    public static int RemainingSeconds(long until, long now)
    {
        long milliseconds = until <= now ? 0 : until >= now + int.MaxValue / 1000L * 1000
            ? int.MaxValue / 1000L * 1000 : until - now;
        return (int)((milliseconds + 999) / 1000);
    }
}
