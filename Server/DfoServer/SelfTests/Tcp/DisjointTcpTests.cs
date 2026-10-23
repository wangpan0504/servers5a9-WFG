using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DisjointTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        That(itemResources.Disjoint.IndependentResults.Count == 338 && itemResources.Disjoint.PieceResults.Count == 1172 &&
            itemResources.Disjoint.EventResults.Count == 4 && itemResources.EpicPieces.Entries.All(entry =>
                itemResources.Disjoint.PieceResults[entry.EquipmentId] == entry.PieceId),
            "匹配资源338独立和1172片映射完整，4活动仍单列拒绝");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "disjoint"), resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var coat = InventoryRewardGrantService.Create(itemResources.Items.Get(10400));
        var tool = InventoryRewardGrantService.Create(itemResources.Items.Get(2600468), 2);
        var totals = new Dictionary<int, int> { [3037] = 5, [10100115] = 7, [10158124] = 9, [10099775] = 11 };
        var epic = itemResources.EpicPieces.Entries.First();
        var pieceTotals = new Dictionary<int, int> { [epic.PieceId] = 40 };
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new("DisjointTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = new([new(0, Bag(3), coat), new(0, Bag(4), tool), new(0, Bag(9), coat),
                    new(0, Bag(10), InventoryRewardGrantService.Create(itemResources.Items.Get(10114))),
                    new(0, Bag(11), InventoryRewardGrantService.Create(itemResources.Items.Get(400050000))),
                    new(0, Bag(12), coat with { EquipmentLockId = 1, SortLockFlag = 1, Attr = 12, SealFlag = 1 }),
                    new(0, Bag(13), InventoryRewardGrantService.Create(itemResources.Items.Get(epic.EquipmentId))),
                    new(0, Bag(14), InventoryRewardGrantService.Create(itemResources.Items.Get(100300212))),
                    new(0, Bag(15), InventoryRewardGrantService.Create(itemResources.Items.Get(101020542))),
                    new(0, Bag(16), InventoryRewardGrantService.Create(itemResources.Items.Get(epic.EquipmentId)))])
            });
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                totals.Concat(pieceTotals).Select(pair => new InventoryReward(pair.Key, pair.Value)).ToArray(), itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Send(peer, 3, 4);
            peer.Expect(1, 26, [0, 19, 0]);
            var packets = peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            CheckWallet(packets[4].Body);
            CheckPieces(packets.Single(packet => packet.Opcode == 1152).Body, 0);
            string rejectedBefore = Snapshot();
            Send(peer, 12, -1);
            peer.Expect(1, 26, [0, 19, 0]);
            var rejected = peer.Expect(0, 14).Body;
            That(rejected.Length == 249 && rejected[0] == 0 && U16(rejected, 1) == 2 &&
                U16(rejected, 3) == 12 && U32(rejected, 5) == uint.MaxValue && U16(rejected, 126) == 12,
                "真实拒绝ACK后同一NOTI14有两个同源槽条目，先空后还原");
            var lockedSource = new InventoryItem(0, Bag(12), coat with { EquipmentLockId = 1, SortLockFlag = 1, Attr = 12, SealFlag = 1 });
            var expectedCore = Build(writer => DfoServer.Network.Builders.Inventory.ItemListProtocolWriter.Write(writer, 12, lockedSource.Core));
            That(rejected.AsSpan(126).SequenceEqual(expectedCore), "拒绝重新投影保留完整核心而非重新创建默认装备");
            peer.Expect(1, 713);
            peer.Expect(0, 251);
            That(Snapshot() == rejectedBefore, "拒绝双条投影与锁收尾不写库、不改revision和审计");
            Send(peer, 3, 4);
            Receive(peer, 3, 4, 1, [3037, 10100115]);
            string saved = Snapshot();
            Send(peer, 3, 4);
            peer.Expect(1, 26, [0, 17, 0]);
            peer.Barrier();
            That(Snapshot() == saved, "empty source replay cannot debit tool or duplicate rewards");
            Send(peer, 9, 4);
            Receive(peer, 9, 4, 0, [3037, 10100115]);
            Send(peer, 10, -1);
            Receive(peer, 10, -1, 0, [3037, 10158124]);
            Send(peer, 11, -1);
            Receive(peer, 11, -1, 0, [3037, 3332, 10099775]);
            Send(peer, 13, -1);
            Receive(peer, 13, -1, 0, [3037, 10158124, epic.PieceId]);
            Send(peer, 14, -1);
            Receive(peer, 14, -1, 0, [3037, 10158124, 10093974]);
            Send(peer, 15, -1);
            Receive(peer, 15, -1, 0, [3037, 3332, 10099775, 10161538]);
            peer.Barrier();
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            That(current.Inventory.Find(Bag(12))!.Core.EquipmentLockId == 1 && current.Inventory.Find(Bag(4)) is null,
                "locked source retained and exhausted portable tool removed");
            foreach (var pair in totals)
                That(CurrencyWallet.RewardKind(pair.Key) is { } kind ? CurrencyWallet.Get(current.Inventory, kind) == pair.Value :
                    current.Inventory.Items.Where(item => item.Core.ItemId == pair.Key).Sum(item => item.Core.Count) == pair.Value,
                    "database result equals exact received increments for " + pair.Key);
            That(current.Inventory.EpicPieces.Get(epic.Index) == pieceTotals[epic.PieceId] &&
                current.Inventory.Items.All(item => item.Core.ItemId != epic.PieceId), "图鉴绝对余额持久化且Main不存在伪碎片实体");
            var after = current.Inventory with { Items = current.Inventory.Items.Add(new(0, Bag(3), coat)).Add(new(0, Bag(4), tool)) };
            repo.CommitInventory(owner, current, new(current.Inventory, after, "disjoint_fixture_restore"));
        }
        using (var peer = Open(fixture))
        {
            var selected = peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            CheckWallet(selected[4].Body);
            CheckPieces(selected.Single(packet => packet.Opcode == 1152).Body, 0);
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_piece BEFORE INSERT ON epic_piece_audit_log BEGIN SELECT RAISE(ABORT,'piece failure'); END");
            Send(peer, 16, -1);
            peer.Closed();
            That(Snapshot() == before, "片审计中断回滚普通奖励与源删除且无成功ACK");
            fixture.Sql("DROP TRIGGER fail_tcp_piece");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_disjoint BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'disjoint failure'); END");
            Send(peer, 3, 4);
            peer.Closed();
            That(Snapshot() == before, "failure during wallet audit rolls back source, tool, wallet and inventory audit without ACK");
            fixture.Sql("DROP TRIGGER fail_tcp_disjoint");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            Send(peer, 3, 4);
            Receive(peer, 3, 4, 1, [3037, 10100115]);
            peer.Barrier();
            peer.Send(26, new byte[8]);
            peer.Closed();
        }
        fixture.Sql("INSERT INTO character_expert_job(character_id,account,job_type,expertness) " +
            "SELECT id,account,3,51 FROM characters");
        fixture.Sql("INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) " +
            "SELECT id,0,10021,1,'11223344556677889900112233445566' FROM characters");
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (0, 251)], expertJobInfo: [0, 3, 1, 0, 0, 0, 44, 1, 0, 0]);
            peer.Send(598, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            peer.Expect(1, 598, [1]);
            string before = ProfessionSnapshot();
            foreach (byte[] request in new byte[][]
            {
                Build(writer => { writer.Write(peer.Id); writer.Write((ushort)12); writer.Write((byte)0); }),
                Build(writer => { writer.Write(peer.Id); writer.Write(ushort.MaxValue); writer.Write((byte)0); }),
                [1, 0, 2, 0], [1, 0, 2, 0, 0, 0]
            })
            {
                peer.Send(235, request);
                peer.Expect(1, 235, [0, 19, 0]);
                peer.Barrier();
                That(ProfessionSnapshot() == before, "235 拒绝保持源装备、钱包、revision、熟练度、耐久与教程条件，不发送截断成功");
            }
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "disjoint TCP database integrity");

        void Receive(TcpPeer peer, short sourceSlot, short toolSlot, int toolCount, int[] expectedItems)
        {
            byte[] ack = peer.Expect(1, 26).Body;
            That(ack.Length >= 7 && ack[0] == 1 && U16(ack, 1) == sourceSlot && ack[3] == 0 && U16(ack, 4) == ushort.MaxValue,
                "disjoint ACK source and extra owner word precede result count");
            That(ack.Length == 7 + ack[6] * 10 && ack[6] == expectedItems.Length, "disjoint ACK exact count and row width");
            var rows = Enumerable.Range(0, ack[6]).Select(index => 7 + index * 10).ToArray();
            That(rows.Select(at => (int)U32(ack, at + 2)).SequenceEqual(expectedItems), "disjoint ACK exact output identities");
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && delta.Length == 3 + U16(delta, 1) * 123, "disjoint authoritative main delta layout");
            That(U32(delta, Entry(delta, sourceSlot) + 2) == uint.MaxValue, "committed source cleared after ACK");
            if (toolSlot >= 0)
            {
                int at = Entry(delta, toolSlot);
                That(U32(delta, at + 2) == (toolCount == 0 ? uint.MaxValue : 2600468u) && U32(delta, at + 6) == toolCount,
                    "portable tool decremented exactly once or cleared");
            }
            foreach (int at in rows)
            {
                short slot = (short)U16(ack, at);
                int item = (int)U32(ack, at + 2), count = (int)U32(ack, at + 6);
                That(count > 0, "ACK grant quantity is positive increment");
                if (item is 10093974 or 10161538)
                    That(count == (item == 10093974 ? 10 : 4), "两组独立奖励1000概率追加固定10或4个");
                if (itemResources.EpicPieces.ByPiece.ContainsKey(item))
                {
                    That(slot == -1 && count is >= 90 and <= 110, "ACK片使用FFFF展示本次90至110增量");
                    pieceTotals[item] = checked(pieceTotals.GetValueOrDefault(item) + count);
                    continue;
                }
                totals[item] = checked(totals.GetValueOrDefault(item) + count);
                int target = Entry(delta, slot);
                That(U32(delta, target + 2) == item && U32(delta, target + 6) == totals[item], "ACK increment then exact committed total");
            }
            if (expectedItems.Contains(epic.PieceId))
                CheckPieces(peer.Expect(0, 1152).Body, 1);
        }
        void CheckPieces(byte[] packet, byte mode)
        {
            That(packet.Length == 13 && packet[0] == mode && U32(packet, 1) == 1 && U32(packet, 5) == epic.PieceId &&
                U32(packet, 9) == pieceTotals[epic.PieceId], "NOTI1152按片目录键发送最终绝对余额，重连不重加本次增量");
        }
        void CheckWallet(byte[] full)
        {
            int epic = Entry(full, 365, true), legendary = Entry(full, 364, true);
            That(U32(full, epic + 2) == 10158124 && U32(full, epic + 6) == totals[10158124] &&
                U32(full, legendary + 2) == 10099775 && U32(full, legendary + 6) == totals[10099775],
                "epic and legendary wallet rows retain item identity across reconnect");
        }
        string ProfessionSnapshot() => Snapshot() + fixture.Snapshot("SELECT * FROM character_expert_job") +
            fixture.Snapshot("SELECT * FROM character_expert_meta") +
            fixture.Snapshot("SELECT * FROM character_active_main_quests");
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT currency_kind,balance FROM account_currency_balances ORDER BY currency_kind")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision,order_hash,counts FROM account_epic_books")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM epic_piece_audit_log"));
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static void Send(TcpPeer peer, short slot, short tool) => peer.Send(26, Build(writer =>
    {
        writer.Write(slot);
        writer.Write((byte)0);
        writer.Write(tool);
        writer.Write(0);
    }));
    static int Entry(byte[] packet, short slot, bool full = false)
    {
        int header = full ? 5 : 3;
        int count = U16(packet, full ? 3 : 1);
        return Enumerable.Range(0, count).Select(index => header + 123 * index).Single(at => U16(packet, at) == slot);
    }
}
