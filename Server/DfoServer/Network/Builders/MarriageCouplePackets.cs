using DfoServer.Game.Characters;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Marriage;

namespace DfoServer.Network.Builders;

internal readonly record struct MarriageCoupleItemUpdate(byte Container, int ItemId, ushort Slot, int Quantity);

internal static class MarriageCouplePackets
{
    public static byte[] Result(int result) => Build(writer => { writer.Write((byte)1); writer.Write(result); });
    public static byte[] EmptyAck() => [1];

    public static byte[] OpenCloseAck(int result, bool close) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(result); writer.Write(close ? (byte)1 : (byte)0);
    });

    public static byte[] DeleteCommentAck(int result, uint commentId) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(result); writer.Write(commentId);
    });

    public static byte[] CommentsAck(int result, uint contextId, IReadOnlyList<MarriageComment> comments) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(result);
        if (result != 0) return;
        writer.Write(contextId); writer.Write(comments.Count);
        foreach (var comment in comments)
        {
            byte[] text = Text(comment.Text, MarriageCoupleRules.MaxCommentBytes);
            writer.Write(comment.Id); FixedName(writer, comment.Name); writer.Write(checked((uint)comment.Timestamp));
            writer.Write(text.Length); writer.Write(text);
        }
    });

    public static byte[] PartnerCheckAck(int result, ushort channel, uint opaque, int town, int area, short x, short y) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(result); writer.Write(channel); writer.Write(opaque);
        writer.Write(town); writer.Write(area); writer.Write(x); writer.Write(y);
    });

    public static byte[] CapsuleAck(ushort command, int result, MarriageMemberState member) => Build(writer =>
    {
        if (command is < 1054 or > 1056) throw new ArgumentOutOfRangeException(nameof(command));
        writer.Write((byte)1); writer.Write(result);
        if (result != 0) return;
        if (command == 1054) writer.Write(checked((uint)member.CapsuleGauge));
        writer.Write(checked((byte)(command == 1056 ? member.CapsulesTaken : member.CapsulesOwned)));
    });

    public static byte[] RoomInformation(MarriageRecord record) => Build(writer =>
    {
        writer.Write(record.Furniture.Count);
        foreach (var (slot, item) in record.Furniture.OrderBy(row => row.Key))
        {
            writer.Write(slot); writer.Write(checked((uint)item.ItemId));
        }
        Tree(writer, record.Tree);
    });

    public static byte[] Storage(MarriageRecord record, uint tail) => Build(writer =>
    {
        writer.Write(record.Storage.Count);
        foreach (var (slot, item) in record.Storage.OrderBy(row => row.Key))
        {
            writer.Write(slot); writer.Write(checked((uint)item.ItemId)); writer.Write(checked((uint)item.Count));
        }
        writer.Write(tail);
    });

    public static byte[] MoveUpdate(byte operation, MarriageCoupleItemUpdate source, MarriageCoupleItemUpdate target) => Build(writer =>
    {
        writer.Write(operation); Item(writer, source); Item(writer, target);
    });

    public static byte[] ItemUpdates(IReadOnlyList<MarriageCoupleItemUpdate> updates) => Build(writer =>
    {
        writer.Write(updates.Count);
        foreach (var update in updates) Item(writer, update);
    });

    public static byte[] TreeInformation(MarriageTreeState? tree) => Build(writer => Tree(writer, tree));

    public static byte[] MarriageAttributes(uint actorId, uint partnerId, byte state, byte ring, uint decorationScore) => Build(writer =>
    {
        writer.Write(actorId); writer.Write(partnerId); writer.Write(state); writer.Write(ring); writer.Write(decorationScore);
    });

    public static byte[] HarvestRewards(IReadOnlyList<MarriageItemCount> rewards) => Build(writer =>
    {
        writer.Write(checked((ushort)rewards.Count));
        foreach (var item in rewards)
        {
            writer.Write(checked((uint)item.ItemId)); writer.Write(checked((uint)item.Count));
        }
    });

    public static byte[] CapsuleInformation(MarriageMemberState member, bool showNotice) => Build(writer =>
    {
        writer.Write(checked((uint)member.CapsuleGauge)); writer.Write(checked((byte)member.CapsulesOwned));
        writer.Write(checked((byte)member.CapsulesTaken)); writer.Write(showNotice ? (byte)1 : (byte)0);
    });

    public static byte[] CapsuleGauge(int gauge) => Build(writer => writer.Write(checked((uint)gauge)));

    static void Tree(BinaryWriter writer, MarriageTreeState? tree)
    {
        writer.Write(checked((uint)(tree?.Seed ?? 0))); writer.Write(checked((uint)(tree?.Expiry ?? 0)));
        writer.Write(checked((uint)(tree?.Growth ?? 0))); writer.Write(checked((byte)(tree?.Waters ?? 0)));
    }

    static void Item(BinaryWriter writer, MarriageCoupleItemUpdate item)
    {
        if (item.Container > 1) throw new ArgumentOutOfRangeException(nameof(item));
        writer.Write(item.Container); writer.Write(checked((uint)item.ItemId)); writer.Write(item.Slot);
        writer.Write(checked((uint)item.Quantity));
    }

    static byte[] Text(string value, int maximum)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(value);
        if (bytes.Length > maximum || bytes.Contains((byte)0)) throw new InvalidDataException("结婚系统文本超过协议宽度");
        return bytes;
    }

    static void FixedName(BinaryWriter writer, string name)
    {
        byte[] bytes = Text(name, 29);
        writer.Write(bytes); writer.Write(new byte[30 - bytes.Length]);
    }

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
}
