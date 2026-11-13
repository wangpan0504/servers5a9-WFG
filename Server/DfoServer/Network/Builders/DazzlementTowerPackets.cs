using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DazzlementTowerPackets
{
    // 006F8F20的type0读取u8(0)/i32(floor,+68)/u8(cleared,+6C)/i32(usedToday,+70)/u8(rewardClaimed,+78)。
    // 默认参数保持沿006F7D9A..006F7DA3初始化零值，兼容基础日准入投影。
    public static byte[] State(byte usedToday, int floor = 0, byte cleared = 0, byte rewardClaimed = 0)
    {
        if (usedToday > 1 || floor is < 0 or > DazzlementTowerProgress.MaxFloors || cleared > 1 || rewardClaimed > 1)
            throw new InvalidDataException("眩惑日准入或层进度状态越界");
        using var stream = new MemoryStream(11);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(floor);
        writer.Write(cleared);
        writer.Write((int)usedToday);
        writer.Write(rewardClaimed);
        return stream.ToArray();
    }

    public static byte[] StashBox(IReadOnlyList<DazzlementStashCard> cards)
    {
        if (cards.Count > DazzlementTowerProgress.MaxStashCards || cards.Any(c => c.ItemId <= 0 || c.Count <= 0))
            throw new InvalidDataException("眩惑贮藏箱奖励列表越界或非法");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(checked((byte)cards.Count));
        foreach (var card in cards)
        {
            writer.Write(card.Slot);
            writer.Write(card.ItemId);
            writer.Write(card.Count);
            writer.Write((byte)(card.Selected ? 1 : 0));
        }
        return stream.ToArray();
    }

    public static byte[] ReaperKeys(byte normalKeys, byte specialKeys)
    {
        if (normalKeys > DazzlementTowerProgress.MaxFloors || specialKeys > 2)
            throw new InvalidDataException("眩惑死神之钥数量越界");
        return [2, normalKeys, specialKeys];
    }

    public static byte[] SelectedRewards(IReadOnlyList<byte> slots)
    {
        if (slots.Count > DazzlementTowerProgress.MaxSelectionLimit)
            throw new InvalidDataException("眩惑已选奖励槽数量越界");
        using var stream = new MemoryStream(2 + slots.Count);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)3);
        writer.Write(checked((byte)slots.Count));
        foreach (byte slot in slots)
            writer.Write(slot);
        return stream.ToArray();
    }

    public static byte[] SelectRewardReply(byte code, IReadOnlyList<byte>? slots = null)
    {
        slots ??= [];
        if (code != 1)
            return [0, code, 0];
        if (slots.Count > DazzlementTowerProgress.MaxSelectionLimit)
            throw new InvalidDataException("眩惑领奖选择应答数量越界");
        using var stream = new MemoryStream(2 + slots.Count);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(checked((byte)slots.Count));
        foreach (byte slot in slots)
            writer.Write(slot);
        return stream.ToArray();
    }

    // 006F96E0注册NOTI1608→006F8390；count:u8，逐项长度前缀名称及job:u8。
    // 006F7270→006F7080以当前角色名查该名单，存在时跳过绑定确认。
    public static byte[] Bindings(DazzlementTowerBindingNotice notice)
    {
        var members = notice.Bindings.Where(row => row.CharacterId is not null).ToArray();
        if (members.Length > DazzlementTowerState.CharacterLimit || members.Any(row => row.Name.Length == 0 || row.Job > 15))
            throw new InvalidDataException("眩惑绑定通知的角色名单非法");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)members.Length));
        foreach (var member in members)
        {
            PacketEncoding.WriteString(writer, member.Name);
            writer.Write(member.Job);
        }
        return stream.ToArray();
    }
}
