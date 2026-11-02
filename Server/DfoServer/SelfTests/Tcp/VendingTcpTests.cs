using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class VendingTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        VendingTests.Run(resources);
        var items = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "vending"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var machines = items.VendingMachines.All.OrderBy(row => row.Id).ToArray();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            foreach (var machine in machines)
            {
                var area = resources.Towns.Areas.Values.First(row => row.Level <= 95 &&
                    row.Map(row.Quests.ToHashSet()).Npcs.Any(npc => npc.Id == machine.NpcId));
                var done = area.Quests.ToHashSet();
                var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
                    checked((ushort)(bounds.Y + bounds.Height / 2)));
                repo.Create(owner, new("Vending" + machine.Id, position, position)
                {
                    Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, CompletedQuests = done.ToArray(),
                    Inventory = new([new(0, Bag(128), Core(7454, 2)), new(0, Bag(129), Core(2749933, 2))])
                });
            }
            repo.Create(owner, new("VendingAway", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(128), Core(7454, 2))])
            });
            repo.SetCardMembership(owner, "Vending1", CardMembershipKind.BlackDiamond, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Send(peer, 1, 128);
            peer.Expect(1, 215, [0, 1, 0]);
            peer.Select(2, premiumContracts: true);
            string before = State();
            Send(peer, 1, 128);
            peer.Expect(1, 215, [0, 1, 0]); peer.Barrier();
            That(State() == before, "不在售货机NPC场景时拒绝，连接和硬币保持不变");
        }
        for (ushort index = 0; index < machines.Length; index++)
        {
            var machine = machines[index];
            short coinSlot = machine.Id == 1 ? (short)128 : (short)129;
            using (var peer = Open(fixture))
            {
                peer.Select(index, resumeTown: true, room: false, premiumContracts: true,
                    expertContract: Read(index).Learning.ExpertContractExpiresAt > 0);
                string before = State();
                foreach (byte[] malformed in new[] { Array.Empty<byte>(), new byte[9], new byte[11] })
                {
                    peer.Send(215, malformed);
                    peer.Expect(1, 215, [0, 1, 0]);
                }
                Send(peer, machine.Id, coinSlot == 128 ? (short)129 : (short)128);
                peer.Expect(1, 215, [0, 1, 0]); peer.Barrier();
                That(State() == before, "畸形包及另一种硬币不扣币、不发奖并保持连接");
                fixture.Sql("CREATE TRIGGER fail_vending BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'vending rollback'); END");
                Send(peer, machine.Id, coinSlot);
                peer.Closed();
                That(State() == before, "末段SQL失败原子回滚硬币、完整奖励、契约、revision及UID，不回成功");
                fixture.Sql("DROP TRIGGER fail_vending");
            }
            using (var peer = Open(fixture))
            {
                peer.Select(index, resumeTown: true, room: false, premiumContracts: true,
                    expertContract: Read(index).Learning.ExpertContractExpiresAt > 0);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var before = Read(index);
                    Send(peer, machine.Id, coinSlot);
                    var ack = peer.Expect(1, 215).Body;
                    That(ack.Length >= 25 && ack[0] == 1 && U16(ack, 1) == coinSlot && U32(ack, 3) == 1 - attempt &&
                        U32(ack, 15) == 0 && U32(ack, 19) == 0, "原生ACK215先返回旧币槽和实际剩余数量");
                    int id = checked((int)U32(ack, 7)); int count = checked((int)U32(ack, 11));
                    That(machine.Prizes.Any(row => row.Weight > 0 && row.ItemId == id && row.Count == count), "奖品与数量来自对应PVF正权重行");
                    peer.Expect(0, 14);
                    peer.Barrier(packet =>
                    {
                        That(items.Contracts.TryAcquisitionContract(id, out _) && packet.Opcode is 1760 or 66,
                            "售货机仅在契约实际变更后发布既有期限通知");
                        if (packet.Opcode == 1760)
                            That(packet.Body.Length == 1 + packet.Body[0] * 9, "契约总表数量及每条期限字段完整");
                        else
                            That(packet.Body.Length == 11 && U16(packet.Body, 0) == 2, "契约通知沿既有66完整字段");
                    });
                    var after = Read(index);
                    That((after.Inventory.Find(Bag(coinSlot))?.Core.Count ?? 0) == 1 - attempt, "ACK后权威差量及数据库反映同一次扣币");
                    if (items.Contracts.TryAcquisitionContract(id, out var contract))
                        That(ack.Length == 34 && U16(ack, 23) == 1 && ack[25] == 6 && U32(ack, 26) == id && U32(ack, 30) == count &&
                            after.Inventory.AccountWallet.ContractExpiry(contract.Type) > before.Inventory.AccountWallet.ContractExpiry(contract.Type),
                            "契约记录为6加ID数量，并由同事务延长期限");
                    else if (CurrencyWallet.RewardKind(id) is { } kind)
                        That(CurrencyWallet.Get(after.Inventory, kind) - CurrencyWallet.Get(before.Inventory, kind) == count &&
                            ack.Length == 149 && U16(ack, 23) == 1 && U32(ack, 32) == count, "虚拟货币按本次增量到账");
                    else
                    {
                        That(after.Inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count) -
                            before.Inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count) == count,
                            "真实网络抽取后完整实体奖励到账");
                        That(U16(ack, 23) > 0 && ack.Length == 25 + U16(ack, 23) * 124 && U32(ack, 28) == id,
                            "奖励列表每行含容器字节和本版123B物品记录");
                    }
                }
                string exhausted = State();
                Send(peer, machine.Id, coinSlot);
                peer.Expect(1, 215, [0, 1, 0]); peer.Barrier();
                That(State() == exhausted, "相同请求每次正常扣一币，耗尽后不重复发奖");
            }
        }
        string committed = State();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(resumeTown: true, room: false, premiumContracts: true,
                expertContract: Read(0).Learning.ExpertContractExpiresAt > 0); peer.Barrier();
            That(State() == committed, "服务重建重连后完整资产、期限及UID保持已提交结果");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "售货机事务后的数据库完整性");

        Character Read(ushort slot)
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.List(owner).Single(row => row.Slot == slot);
        }
        string State() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_wallets")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_currency_balances")) +
            JsonSerializer.Serialize(fixture.Query("SELECT id,revision,gold,revive_coins FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM sqlite_sequence"));
        ItemCore Core(int id, int count) => InventoryRewardGrantService.Create(items.Items.Get(id), count, shopExpiration: true);
    }
    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);
    static void Send(TcpPeer peer, int machine, short slot) => peer.Send(215, Build(w => { w.Write(machine); w.Write(1); w.Write(slot); }));
}
