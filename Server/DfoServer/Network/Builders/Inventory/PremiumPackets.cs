using DfoServer.Game.Currency;

namespace DfoServer.Network.Builders.Inventory;

internal static class PremiumPackets
{
    // 023A90A0的type2同时触发契约提示和效果管理刷新，1760仅初始化期限目录。
    public static byte[] Updated(int type, long expiresAt, long now)
    {
        if (type is <= 0 or >= 132) throw new InvalidDataException("契约更新类型超出客户端范围");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)2);
        writer.Write((byte)type);
        writer.Write(Math.Max(0, expiresAt - now));
        return stream.ToArray();
    }

    // 023D0A00：u8数量后逐项读取u8服务类型和8B期限，028FC370只接受0..131类型。
    // 028F9EE0从原i64扣除(GetTickCount差值/1000)，因此期限是剩余秒而非绝对时间或毫秒。
    internal static long PcRoomExpiry(AccountWallet wallet, long now, bool premiumPcRoom) =>
        wallet.ContractExpiry(128) > now ? wallet.ContractExpiry(128) : premiumPcRoom ? int.MaxValue : 0;

    public static byte[] SelectEntries(AccountWallet wallet, DateTimeOffset? utcNow = null,
        IReadOnlyCollection<int>? resetTypes = null, bool premiumPcRoom = false)
    {
        long now = (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var active = DfoServer.Game.Inventory.Services.DevilContractPolicy.ClientContracts(wallet).Where(pair => pair.Value > now)
            .OrderBy(pair => pair.Key).ToList();
        // 实例免费身份只作网络投影；不写账号期限，也不覆盖尚有效的购买128。
        if (PcRoomExpiry(wallet, now, premiumPcRoom) is > 0 and var pcRoom && active.All(pair => pair.Key != 128))
            active.Add(new(128, pcRoom));
        foreach (int type in resetTypes ?? [])
        {
            if (type is <= 0 or >= 132) throw new InvalidDataException("需清除的契约类型超出客户端范围");
            if (active.All(pair => pair.Key != type)) active.Add(new(type, now));
        }
        active.Sort((left, right) => left.Key.CompareTo(right.Key));
        if (active.Any(pair => pair.Key is <= 0 or >= 132))
            throw new InvalidDataException("契约服务类型超出匹配客户端范围");
        writer.Write(checked((byte)active.Count));
        foreach (var pair in active)
        {
            writer.Write(checked((byte)pair.Key));
            writer.Write(Math.Max(0, pair.Value - now));
        }
        return stream.ToArray();
    }
}
