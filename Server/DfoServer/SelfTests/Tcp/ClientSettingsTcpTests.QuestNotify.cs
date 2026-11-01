using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class ClientSettingsTcpTests
{
    static void QuestNotifyScope(TcpTestFixture fixture, ClientSettingsTimings timings)
    {
        const string account = "questnotify";
        byte[] request = [4, 2, 0, 0, 0, 47, 117, 0, 0, 3, 1, 0, 0, 94, 27, 0, 0];
        byte[] saved = request[1..];
        ushort firstId;
        void CheckSelection(TcpPeer peer, ushort slot, byte[] expected)
        {
            var packet = peer.Select(slot)[0];
            That(packet.Kind == 1 && packet.Opcode == 4 && packet.Body.Length == 235 &&
                packet.Body.AsSpan(206, 16).SequenceEqual(expected), "真实选角在原四书签槽恢复当前角色顺序");
            peer.Barrier();
        }
        using (var peer = Open(fixture, account))
        {
            peer.Create("NotifyFirst"); firstId = peer.Id;
            peer.Create("NotifySecond");
            CheckSelection(peer, 0, new byte[16]);
            string assets = Assets(fixture, timings);
            peer.Send(506, request); peer.Barrier();
            That(fixture.Query("SELECT account,entries FROM character_quest_notify WHERE character_id=$p0", firstId)
                is [[string owner, byte[] entries]] && owner == account && entries.SequenceEqual(saved) &&
                Assets(fixture, timings) == assets, "506真实注册且提交后不伪造ACK、不改任务及资产");
            peer.Send(506, request); peer.Barrier();
            That(Assets(fixture, timings) == assets, "重复书签不改变资产或revision");
            ReturnSelection(peer);
            peer.Send(506, [0]);
            peer.Send(692, Name("NotifyProbe")); peer.Expect(1, 692, [1]);
            CheckSelection(peer, 0, saved);
            ReturnSelection(peer);
            CheckSelection(peer, 1, new byte[16]);
            Exit(peer);
        }
        using (var peer = Open(fixture, account))
        {
            CheckSelection(peer, 0, saved);
            peer.Send(506, [0]); peer.Barrier();
            ReturnSelection(peer);
            CheckSelection(peer, 0, new byte[16]);
            Exit(peer);
        }
        using (var peer = Open(fixture, "othernotify"))
        {
            peer.Create("NotifyOther");
            CheckSelection(peer, 0, new byte[16]);
            Exit(peer);
        }
    }
}
