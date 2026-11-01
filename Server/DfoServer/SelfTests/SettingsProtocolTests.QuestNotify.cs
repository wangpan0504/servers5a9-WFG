using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Settings;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SettingsProtocolTests
{
    static void QuestNotify()
    {
        int[] ids = [29999, 2, 259, 7006];
        for (int count = 0; count <= 4; count++)
        {
            byte[] request = new byte[1 + count * 4]; request[0] = (byte)count;
            for (int slot = 0; slot < count; slot++)
                BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(1 + slot * 4), ids[slot]);
            var parsed = SettingsRequests.QuestNotify(request);
            That(parsed.Length == 16 && Enumerable.Range(0, 4).All(slot =>
                BinaryPrimitives.ReadInt32LittleEndian(parsed.AsSpan()[(slot * 4)..]) == (slot < count ? ids[slot] : 0)),
                "506按count读取原序u32，余槽补零且不排序");
            Reject<InvalidDataException>(() => SettingsRequests.QuestNotify(request[..^1]), "506截断拒绝");
            Reject<InvalidDataException>(() => SettingsRequests.QuestNotify([.. request, 0]), "506多余尾部拒绝");
        }
        foreach (byte[] bad in new byte[][] { [5], [1, 0, 0, 0, 0], [1, 48, 117, 0, 0],
            [1, 255, 255, 255, 255], [2, 1, 0, 0, 0, 1, 0, 0, 0] })
            Reject<InvalidDataException>(() => SettingsRequests.QuestNotify(bad), "506越界、空编号及重复拒绝");
        Reject<InvalidDataException>(() => QuestNotifyRules.Validate(default), "书签存储不接受缺省正文");
        Reject<InvalidDataException>(() => QuestNotifyRules.Validate(ImmutableArray.Create(new byte[15])), "书签存储拒绝非四槽");
        byte[] hole = new byte[16]; hole[4] = 1;
        Reject<InvalidDataException>(() => QuestNotifyRules.Validate([.. hole]), "书签存储拒绝空洞，不变造原序");
    }
}
