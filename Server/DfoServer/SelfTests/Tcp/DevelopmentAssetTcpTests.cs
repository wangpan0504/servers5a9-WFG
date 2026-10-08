using DfoServer.Network.Management;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DevelopmentAssetTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "gm-assets"), gm: true, resources: resources);
        await fixture.StartAsync();
        string Gm(string command, bool accepted = true)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out string reply), "独立GM TCP已连接");
            That(reply.StartsWith(accepted ? "OK " : "ERR "), "GM资产命令实际返回: " + reply);
            return reply;
        }
        using (var peer = Open(fixture))
        {
            peer.Create("GmA"); peer.Create("GmB"); peer.Select();
            Gm("gold GmA 1000000");
            peer.Barrier();
            Gm("gold GmB 55"); peer.Barrier();
            That(fixture.Query("SELECT name,gold FROM characters ORDER BY slot") is [["GmA", 1000000L], ["GmB", 55L]],
                "同账号两角色金币独立，GM设置不发送即时游戏通知");
            Gm("item GmA 18400 3"); peer.Barrier();
            peer.Send(7); peer.Batch((1, 7), (0, 2));
            var entry = peer.Select();
            That(U16(entry[4].Body, 5) == 0 && U32(entry[4].Body, 11) == 1000000, "GM金币在重新选角后刷新");
            var mail = MailboxTcpTests.ReadList(peer.Mailbox.Single());
            That(mail.Summaries.Length == 3 && mail.Summaries.All(row => row.ItemId == 18400 && U32(row.Core, 6) == 999999998) &&
                mail.Summaries.Select(row => row.ClaimId).Distinct().Count() == 3, "装备件数展开完整核心与独立领取对象");
            uint mailId = mail.Summaries[0].MailId;
            peer.Send(95, Build(w => { w.Write(1); w.Write(mailId); }));
            peer.Expect(1, 95, Build(w => { w.Write((byte)1); w.Write(3); foreach (var row in mail.Summaries) { w.Write(row.MailId); w.Write(row.ClaimId); w.Write(0); } }));
            var delta = peer.Expect(0, 14).Body;
            That(U16(delta, 1) == 3 && new[] { 3, 126, 249 }.All(at => U32(delta, at + 2) == 18400 && U32(delta, at + 6) == 999999998),
                "真实领取按三件装备进入独立槽；开发超携带上限金币不阻止纯物品领取");
            _ = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body); peer.Barrier();
            That(fixture.Query("SELECT count(DISTINCT i.item_uid) FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE i.character_id=1 AND s.list_type=0 AND s.slot_index BETWEEN 9 AND 11") is [[3L]],
                "装备领取三个持久UID无堆叠");
            string before = fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id");
            Gm("item GmA 18400 11", accepted: false); peer.Barrier();
            Gm("account unknown item GmA 18400 1", accepted: false); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id") == before, "拒绝数量或跨账号不发成功通知与邮件");
            Gm("item GmB 3175 7"); peer.Barrier();
            peer.Send(7); peer.Batch((1, 7), (0, 2));
            entry = peer.Select(1);
            That(U32(entry[4].Body, 11) == 55, "同账号其他角色选角加载其独立金币");
            mail = MailboxTcpTests.ReadList(peer.Mailbox.Single());
            That(mail.Summaries is [var material] && material.ItemId == 3175 && U32(material.Core, 6) == 7,
                "同账号未选中角色材料邮件在其上线恢复，数量保留为堆叠数");
            peer.Send(95, Build(w => { w.Write(1); w.Write(mail.Summaries[0].ClaimId); }));
            peer.Expect(1, 95);
            delta = peer.Expect(0, 14).Body;
            That(U16(delta, 3) == 121 && U32(delta, 5) == 3175 && U32(delta, 9) == 7, "绑定材料系统邮件正常领取进入材料页");
            _ = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body); peer.Barrier();
        }
        await fixture.StopAsync();
        That((await fixture.CliAsync(null, "--gm", "gold", "GmB", "0")).StartsWith("OK ") &&
            (await fixture.CliAsync(null, "--gm", "item", "GmB", "18400", "1")).StartsWith("OK "), "停止服务后CLI复用同一金币和系统邮件入口");
        await fixture.StartAsync("restarted");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(1);
            That(U32(entry[4].Body, 11) == 0, "离线清零在服务重启和选角后恢复");
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries.Any(row => row.ItemId == 18400), "离线发放邮件在服务重启后保留");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "GM资产全过程完整性与外键正常");
    }
}
