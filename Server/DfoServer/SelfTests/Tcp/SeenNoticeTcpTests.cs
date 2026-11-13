using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SeenNoticeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var f = new TcpTestFixture(source, Path.Combine(root, "notice"), resources: resources);
        await f.StartAsync();
        using (var peer = Open(f, "noticeaccount"))
        {
            peer.Create("NoticeFirst");
            peer.Create("NoticeSecond");
            peer.Select(0);
            Bytes(peer.SeenNotices, [0], "新账号440只回放空集合");
            peer.Send(503, [42,0,0,0]);
            peer.Send(503, [42,0,0,0]);
            peer.Barrier();
            That(f.Query("SELECT notice_id FROM account_seen_notices WHERE account=$p0", "noticeaccount") is [[42L]], "503真实分派静默提交、重复不新增");
            peer.Send(3, [0]);
            peer.Expect(1, 3, [1]);
            peer.Closed();
        }
        await f.StopAsync();
        await f.StartAsync("restart");
        using (var peer = Open(f, "noticeaccount"))
        {
            peer.Select(1);
            Bytes(peer.SeenNotices, [1,42], "服务重启后同账号另一个角色恢复一次提示");
            peer.Send(503, [128,0,0,0]);
            peer.Closed();
            That(f.Query("SELECT count(*) FROM account_seen_notices") is [[1L]], "非法提示不改变已存状态");
        }
        using (var peer = Open(f, "noticeother"))
        {
            peer.Create("NoticeOther");
            peer.Select();
            Bytes(peer.SeenNotices, [0], "其他账号独立获得首次引导资格");
            peer.Send(3, [0]);
            peer.Expect(1, 3, [1]);
            peer.Closed();
        }
    }
}
