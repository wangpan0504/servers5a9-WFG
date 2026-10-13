using DfoServer.Game.Currency;

namespace DfoServer.Network.Builders.Inventory;

internal static class PremiumPackets
{
    // 023D0A00：u8数量后逐项读取u8服务类型和8B期限，028FC370只接受0..131类型。
    // 028F9EE0从原i64扣除(GetTickCount差值/1000)，因此期限是剩余秒而非绝对时间或毫秒。
    public static byte[] SelectEntries(AccountWallet wallet, DateTimeOffset? utcNow = null, IReadOnlyCollection<int>? resetTypes = null)
    {
        long now = (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var active = wallet.PremiumContracts.Where(pair => pair.Value > now)
            .OrderBy(pair => pair.Key).ToList();
        foreach (int type in resetTypes ?? [])
        {
            if (type is <= 0 or >= 132) throw new InvalidDataException("需清除的契约类型超出客户端范围");
            if (active.All(pair => pair.Key != type)) active.Add(new(type, now));
        }
        if (resetTypes?.Count > 0) active.Sort((left, right) => left.Key.CompareTo(right.Key));
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
