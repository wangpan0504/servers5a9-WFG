using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SkillMaterialTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "skill-material"), resources: resources);
        await fixture.StartAsync();
        byte[] request = Convert.FromHexString("1000000010001A0A080210E60218DD172005200001");
        using (var peer = Open(fixture))
        {
            peer.Create("MaterialTcp");
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repo = new CharacterRepository(fixture.Database);
                var role = repo.LoadCharacter(owner, 0);
                var items = resources.ItemResources;
                var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                InventoryService.Commit(repo, owner, role, inventory.Grant(role, [new(3037, 20), new(3036,10), new(1000, 6)]));
            }
            peer.Create("MaterialTwin");
            peer.Select();
            string skills = Progress();
            peer.Send(18, Convert.FromHexString("1A00000010001A0A080210E60218DD1720051A080800104118062001200001"));
            peer.Expect(1, 18, Convert.FromHexString("01150000000800120708E6021005180212060841100118001800"));
            byte[] ordinary = peer.Expect(0, 14).Body;
            That(ordinary.Length == 126 && ordinary[0] == 0 && U16(ordinary, 1) == 1 && U16(ordinary, 3) == 65 &&
                U32(ordinary, 5) == 1000 && U32(ordinary, 9) == 5 && Balance() == 15,
                "混合删除只刷新普通丢弃槽，不将剩余无色数量通知为获得物品");
            peer.Barrier();
            peer.Send(18, Convert.FromHexString("0E00000010001A080802104118052001200001"));
            peer.Expect(1, 18, Convert.FromHexString("010C000000080012060841100118021800"));
            peer.Barrier();
            That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=1000") is [[4L]],
                "实体技能材料同样只回ACK，实际数量足额扣除");
            peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
            peer.Travel(38, 2, 1085, 249, 38, 0, kind: 5);
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]); peer.Expect(0,3); peer.Expect(0,27);
            var selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            Array.Clear(selection,16,4);
            peer.Send(16,selection); peer.Expect(0,28); peer.Expect(0,29);
            FormalDungeonTcpTests.Load(peer);
            peer.Expect(0,36);
            Spend(peer, 10);
            peer.Send(18,Convert.FromHexString("1000000010001A0A080210E50218DC172005200001"));
            peer.Expect(1,18,Convert.FromHexString("010D0000000800120708E502100518021800")); peer.Barrier();
            That(fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=6") is [[5L]],
                "普通副本有色技能晶块同样实际扣5，免耗玩法的策略不泄漏到普通Run");
            peer.Send(38, Convert.FromHexString("004C007017096C00150053FDD425")); peer.Barrier();
            That(Progress() == skills, "城镇和地下城扣无色不修改等级、经验或双技能树");
            string before = WalletState();
            byte[] tooMuch = (byte[])request.Clone(); tooMuch[17] = 11;
            peer.Send(18, tooMuch); peer.Expect(1, 18, [0, 4, 0, 0, 0, 8, 0, 24, 1]); peer.Barrier();
            That(WalletState() == before, "余额不足回完整失败protobuf，连接保持且钱包与审计不变");
            fixture.Sql("CREATE TRIGGER fail_skill_material BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='delete_skill_material' BEGIN SELECT RAISE(ABORT,'material fault'); END");
            peer.Send(18, request); peer.Closed();
            That(WalletState() == before, "后段审计SQL异常回滚角色revision、账号钱包和全部审计");
            fixture.Sql("DROP TRIGGER fail_skill_material");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(Balance() == 10, "断线后重登恢复已提交余额，不重扣异常请求");
            Spend(peer, 5); Spend(peer, 0);
            peer.Send(18, request); peer.Expect(1, 18, [0, 4, 0, 0, 0, 8, 0, 24, 1]); peer.Barrier();
        }
        await fixture.StopAsync();
        await fixture.StartAsync("skill-material-restart");
        using (var peer = Open(fixture))
        {
            var packets = peer.Select(1);
            byte[] main = packets[4].Body;
            int cube = Enumerable.Range(0, U16(main, 3)).Select(index => 5 + index * 123).Single(offset => U16(main, offset) == 358);
            That(Balance() == 0 && U32(main, cube + 2) == 3037 && U32(main, cube + 6) == 0,
                "账号第二角色和服务重启读取同一钱包，无色耗尽没有实体占槽");
            That(fixture.Query("SELECT count(*) FROM currency_audit_log WHERE action_name='delete_skill_material'") is [[5L]],
                "四次无色及一次蓝色成功消费记入审计，拒绝和异常不记入");
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP 无色及实体材料无获得通知、混合丢弃、战斗连续请求、失败回滚和账号重登/重启；非Windows实机");

        long Balance() => fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=7") is [[var balance]] ? Convert.ToInt64(balance) : 0;
        string Progress() => fixture.Snapshot("SELECT id,experience,level,grow_type FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context") +
            fixture.Snapshot("SELECT * FROM character_skill_bindings ORDER BY character_id,context,slot");
        string WalletState() => fixture.Snapshot("SELECT id,revision FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id");
        void Spend(TcpPeer peer, byte remaining)
        {
            peer.Send(18, request);
            peer.Expect(1, 18, [1, 13, 0, 0, 0, 8, 0, 18, 7, 8, 230, 2, 16, 5, 24, 2, 24, 0]);
            peer.Barrier();
            That(Balance() == remaining, "ACK回实际消耗量，SQL保存真实余额，屏障确认没有多发获得物品通知");
        }
    }
}
