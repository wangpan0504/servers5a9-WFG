using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.BoosterTcpTests;
using static DfoServer.Testing.BoosterSelectionTcpTests;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ReturningAdventurerItemTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        ReturningAdventurerItemTests.RunResources(resources.Archive, resources.ItemResources, resources.Towns);
        const int box = 490013849;
        int[] choices = [400310137, 400300136, 400320141];
        var items = resources.ItemResources.Items;
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "returning-items"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            foreach (int index in Enumerable.Range(0, choices.Length))
                repo.Create(owner, new("BuffChoice" + index, resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Job = 15, Level = 85, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                    Inventory = new([new(0, new(InventoryListType.Main, 65),
                        InventoryRewardGrantService.Create(items.Get(box), shopExpiration: true))])
                });
        }
        await fixture.StartAsync();
        for (byte index = 0; index < choices.Length; index++)
        {
            using var peer = Open(fixture);
            peer.Select(index); peer.Barrier();
            string before = fixture.Snapshot();
            SendSelect(peer, 65, choices); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == before, "BUFF首饰盒仍只准三选一，超额失败不扣源");
            if (index == 0)
            {
                fixture.Sql("CREATE TRIGGER buff_box_abort BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'buff box late rollback'); END");
                SendSelect(peer, 65, [choices[index]]); peer.Closed();
                That(fixture.Snapshot() == before, "BUFF盒末段SQL故障回滚源、奖励及UID，无成功ACK");
                fixture.Sql("DROP TRIGGER buff_box_abort");
                continue;
            }
            OpenChoice(peer, index);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); OpenChoice(peer, 0);
        }
        await fixture.StopAsync(); await fixture.StartAsync();
        for (byte index = 0; index < choices.Length; index++)
        {
            using var peer = Open(fixture);
            var full = peer.Select(index)[4].Body;
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            var role = repo.LoadCharacter(owner, index);
            var reward = role.Inventory.Items.Single();
            That(reward.Core.ItemId == choices[index] && reward.Core.DisjointForbidden && reward.Uid > 0 &&
                role.Inventory.Find(new(InventoryListType.Main, 65)) is null,
                "重启后首饰盒消耗和实际选择完整核心保持，不重开或补发");
            That(U32(EquipmentEntry(full, reward.Position.Index, true), 56) == (uint)reward.Core.ExpireTime,
                "重连装备通知恢复同一30天期限，不续期");
            peer.Barrier();
        }

        void OpenChoice(TcpPeer peer, int index)
        {
            var earliest = DateTimeOffset.UtcNow;
            SendSelect(peer, 65, [choices[index]]);
            ExpectAck(peer, box, 65, [(choices[index], 1)]);
            var delta = peer.Expect(0, 14).Body;
            peer.Barrier();
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            var role = repo.LoadCharacter(owner, checked((byte)index));
            var reward = role.Inventory.Items.Single();
            var expected = InventoryRewardGrantService.Create(items.Get(choices[index]), shopExpiration: true, utcNow: earliest)
                with { ExpireTime = reward.Core.ExpireTime, DisjointForbidden = true };
            That(reward.Core == expected && reward.Core.ExpireTime >= earliest.AddDays(30).ToUnixTimeSeconds() &&
                reward.Core.ExpireTime <= DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() && reward.Uid > 0,
                "三种BUFF首饰均经真实CMD160选择，30天产物期限和分解限制同笔持久化");
            Entry(delta, 65, -1, 0);
            Entry(delta, reward.Position.Index, choices[index], InventoryRewardGrantService.InitialQuality);
            string before = fixture.Snapshot();
            SendSelect(peer, 65, [choices[index]]); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == before, "耗尽首饰盒重放不重复发奖");
        }
    }
}
