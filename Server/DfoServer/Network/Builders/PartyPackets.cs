using DfoServer.Game.Parties;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network.Builders;

internal static class PartyPackets
{
    public static byte[] Rejected() => [0, 19, 0];

    public static byte[] Cleared(ushort id, ChannelIdentity channel) =>
        [1, 0, 15, 39, (byte)id, (byte)(id >> 8), channel.ServerIndex, channel.Number, 0, 3, 0];

    // 023B9E70：本版完整设置、单成员槽表和清理记录，未知附加玩法均按未启用发送。
    public static byte[] Info(SoloPartyReceipt receipt, ChannelIdentity channel)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        byte kind = receipt.Removed ? (byte)3 : receipt.Created ? (byte)0 : (byte)1;
        if (receipt.Removed) return Cleared(receipt.Party.Id, channel);
        writer.Write((ushort)1);
        writer.Write((ushort)9999);
        writer.Write(receipt.Party.Id);
        writer.Write(channel.ServerIndex); writer.Write(channel.Number);
        writer.Write((byte)0); writer.Write(kind); writer.Write((byte)0);
        var settings = receipt.Party.Settings;
        writer.Write((byte)0); writer.Write(settings.TitlePreset);
        if (settings.TitlePreset == 0) PacketEncoding.WriteString(writer, settings.Title!);
        writer.Write((byte)0); writer.Write(settings.MemberCode); // 不伪造回归队伍标记。
        writer.Write(0); writer.Write(settings.SelectionCode); writer.Write(settings.SelectionValue);
        writer.Write(settings.RecruitFlag); writer.Write(settings.TargetMode); writer.Write(settings.TargetId);
        // 037E7160比较模型+116与请求selectionId；不是旧参考写入的前部模型+120。
        writer.Write(new byte[4]); writer.Write(settings.SelectionId); writer.Write((ushort)0); writer.Write(settings.Extra);
        if (kind == 0)
        {
            writer.Write((byte)1); writer.Write((byte)0); writer.Write(receipt.Party.LeaderId);
            writer.Write((byte)0); writer.Write((byte)100); writer.Write(new byte[3]);
            writer.Write((byte)0); // 额外成员行数量。
            writer.Write(new byte[7]); writer.Write(settings.TargetMode); // 队长槽0及附加玩法关闭。
        }
        writer.Write((byte)0); // 可选附加结构选择器。
        return stream.ToArray();
    }
}
