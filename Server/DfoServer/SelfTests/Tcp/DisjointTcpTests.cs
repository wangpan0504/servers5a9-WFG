using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DisjointTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await BattlePassPiecesAsync(source, root, resources);
        var itemResources = resources.ItemResources;
        That(itemResources.Disjoint.IndependentResults.Count == 338 && itemResources.Disjoint.PieceResults.Count == 1172 &&
            itemResources.Disjoint.EventResults.Count == 4 && itemResources.EpicPieces.Entries.All(entry =>
                itemResources.Disjoint.PieceResults[entry.EquipmentId] == entry.PieceId),
            "匹配资源338独立和1172片映射完整，4活动仍单列拒绝");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "disjoint"), gm: true, resources: resources);
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
        // 走GM的真实命令与存档入口，不能用手工种默认Core替代发放链路。
        int[] reportedIds = [100060651, 100160525, 100110584, 100260565, 100210549, 100060598];
        foreach (int id in reportedIds)
        {
            int count = id == 100060651 ? 2 : 1;
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, $"grant DisjointTcp {id} {count} 0 0 direct", out string reply) &&
                reply.StartsWith("OK ", StringComparison.Ordinal), "GM直接发放工单装备成功：" + id + " " + reply);
        }
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "grant DisjointTcp 100060651 1 0 0 mail", out string mailReply) &&
            mailReply.StartsWith("OK ", StringComparison.Ordinal), "GM邮件发放95级原始样本成功：" + mailReply);
        InventoryItem[] reportedSources;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            reportedSources = repo.List(owner).Single().Inventory.Items.Where(item => reportedIds.Contains(item.Core.ItemId)).ToArray();
        That(reportedSources.Length == 7 && reportedSources.Select(item => item.Uid).Distinct().Count() == 7 &&
            reportedSources.All(item => item.Core == InventoryRewardGrantService.Create(itemResources.Items.Get(item.Core.ItemId))),
            "GM六种七件装备保存独立UID与完整默认核心，无额外来源、交易、期限或未净化标记");
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
            foreach (var sourceItem in reportedSources)
            {
                int pieceId = itemResources.Disjoint.PieceResults[sourceItem.Core.ItemId];
                Send(peer, sourceItem.Position.Index, -1);
                Receive(peer, sourceItem.Position.Index, -1, 0, [3037, 10158124, pieceId]);
            }
            var mailed = peer.Mailbox.SelectMany(packet => MailboxTcpTests.ReadList(packet).Summaries)
                .Single(row => row.ItemId == 100060651);
            That(mailed.ItemId == 100060651, "选角恢复GM邮件中的95级原始装备");
            peer.Send(95, Build(writer => { writer.Write(1); writer.Write(mailed.ClaimId); }));
            peer.Expect(1, 95, Build(writer =>
            {
                writer.Write((byte)1); writer.Write(1); writer.Write(mailed.MailId); writer.Write(mailed.ClaimId); writer.Write(0);
            }));
            var claimed = peer.Expect(0, 14).Body;
            That(claimed.Length == 126 && U32(claimed, 5) == 100060651 && U32(claimed, 9) == InventoryRewardGrantService.InitialQuality,
                "GM邮件真实领取保持装备ID与默认品级");
            short mailedSlot = (short)U16(claimed, 3);
            Send(peer, mailedSlot, -1);
            Receive(peer, mailedSlot, -1, 0, [3037, 10158124, 10305633]);
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
            That(pieceTotals.All(pair => current.Inventory.EpicPieces.Get(itemResources.EpicPieces.ByPiece[pair.Key].Index) == pair.Value) &&
                current.Inventory.Items.All(item => !pieceTotals.ContainsKey(item.Core.ItemId)) &&
                reportedSources.All(sourceItem => current.Inventory.Items.All(item => item.Uid != sourceItem.Uid)),
                "七件直接发放与一件邮件装备均已删除，全部图鉴绝对余额持久化且Main不存在伪碎片实体");
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
        var occupied = fixture.Query("SELECT slot FROM character_active_main_quests").Select(row=>(long)row[0]).ToHashSet();
        int tutorialSlot = Enumerable.Range(0,30).First(slot=>!occupied.Contains(slot));
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            var after = current.Inventory with { Items = current.Inventory.Items
                .Add(new(0, Bag(21), coat))
                .Add(new(0, Bag(22), InventoryRewardGrantService.Create(itemResources.Items.Get(10114)))) };
            repo.CommitInventory(owner, current, new(current.Inventory, after, "disjoint_profession_fixture"));
        }
        fixture.Sql("INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) " +
            "SELECT id,$p0,10021,1,'11223344556677889900112233445566' FROM characters",tutorialSlot);
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (0, 251)], expertJobInfo: [0, 3, 1, 0, 0, 0, 44, 1, 0, 0]);
            string career = fixture.Snapshot("SELECT * FROM character_expert_job") + fixture.Snapshot("SELECT * FROM character_expert_meta");
            Send(peer, 21, -1);
            Receive(peer, 21, -1, 0, [3037, 10100115]);
            Send(peer, 22, 4);
            Receive(peer, 22, 4, 0, [3037, 10158124]);
            peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_expert_job") + fixture.Snapshot("SELECT * FROM character_expert_meta") == career,
                "已授分解师后NPC与便携仍有完整灵魂产物，且不增长机器熟练度或改变机器耐久");
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
            foreach (int pieceId in expectedItems.Where(itemResources.EpicPieces.ByPiece.ContainsKey))
                CheckPieces(peer.Expect(0, 1152).Body, 1, pieceId);
        }
        void CheckPieces(byte[] packet, byte mode, int? changedPiece = null)
        {
            var expected = changedPiece is { } id ? new Dictionary<int, int> { [id] = pieceTotals[id] } : pieceTotals;
            int count = (int)U32(packet, 1);
            That(packet.Length == 5 + count * 8 && packet[0] == mode && count == expected.Count &&
                Enumerable.Range(0, count).Select(index => (int)U32(packet, 5 + index * 8)).ToHashSet().SetEquals(expected.Keys) &&
                Enumerable.Range(0, count).All(index => U32(packet, 9 + index * 8) == expected[(int)U32(packet, 5 + index * 8)]),
                "NOTI1152按片目录键发送最终绝对余额，重连不重加本次增量");
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
    static async Task BattlePassPiecesAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
        var policy = new DfoServer.Game.BattlePass.BattlePassMissionPolicy("disjoint-pieces", start,
            DfoServer.GameWorld.BattlePass.BattlePassCatalog.Load(resources.Archive));
        var at = Enumerable.Range(0, policy.Days).Select(day => start.AddDays(day).AddHours(1))
            .First(time => policy.Active(time).Any(row => DfoServer.Game.BattlePass.BattlePassEquipmentDisjointSource.Matches(row.Definition)));
        var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(at, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
        var options = source with { BattlePassSeasonKey = policy.SeasonKey, BattlePassSeasonStart = start };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "disjoint-battle-pass-pieces"),
            resources: resources, clock: clock, sharedResourcePath: true);
        var items = resources.ItemResources;
        var epic = items.EpicPieces.Entries.First(entry => items.Disjoint.PieceResults.ContainsKey(entry.EquipmentId));
        var repository = new CharacterRepository(fixture.Database);
        repository.ConfigureBattlePassMissions(policy, clock);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(options.DevelopmentAccount, () => { }))
        {
            var current = repository.Create(owner, new("PiecePass", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(9), InventoryRewardGrantService.Create(items.Items.Get(epic.EquipmentId))),
                    new(0, Bag(10), InventoryRewardGrantService.Create(items.Items.Get(epic.EquipmentId))),
                    new(0, Bag(11), InventoryRewardGrantService.Create(items.Items.Get(10400)))])
            });
            var service = new InventoryDisjointService(items.Items, items.Disjoint, items.GoldLimits, items.EpicPieces, clock: clock);
            var plan = service.Disjoint(current, new(Bag(9), -1, 0), out _).Plan!;
            string before = Snapshot();
            bool rejected = false;
            try { repository.CommitInventory(owner, current, new(plan.Before, plan.After, plan.Reason, plan.CharacterPrecondition) { EquipmentDisjoint = plan.EquipmentDisjoint! with
                { Source = plan.EquipmentDisjoint.Source with { Uid = long.MaxValue } } }); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected && Snapshot() == before, "伪造分解来源使源、图鉴、钱包、版本与战令整笔回滚");
        }
        await fixture.StartAsync();
        long MissionCount() => fixture.Query("SELECT current_count FROM account_battle_pass_mission_progress WHERE season_key=$p0 AND mission_id IN(18,42)", policy.SeasonKey)
            .Sum(row => Convert.ToInt64(row[0]));
        long EventCount() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='equipment_disjoint'")[0][0]);
        long BookRevision() => fixture.Query("SELECT revision FROM account_epic_books") is { Length: > 0 } rows ? Convert.ToInt64(rows[0][0]) : 0;
        using (var peer = Open(fixture))
        {
            peer.ExpectedOpenEvents = [2532, 266, 2520];
            peer.ExpectedBattlePassEnd = checked((uint)policy.End.ToUnixTimeSeconds());
            peer.Select(); peer.Expect(0, 1800); peer.Expect(0, 1801); peer.Expect(0, 1802); peer.Barrier();
            int targets = policy.Active(at).Count(row => DfoServer.Game.BattlePass.BattlePassEquipmentDisjointSource.Matches(row.Definition));
            long mission = MissionCount(), events = EventCount();
            int pieceBalance = 0;
            foreach (short slot in new short[] { 9, 10, 11 })
            {
                long revision = BookRevision();
                peer.Send(26, Build(writer => { writer.Write(slot); writer.Write((byte)0); writer.Write((short)-1); writer.Write(0); }));
                byte[] ack = peer.Expect(1, 26).Body;
                That(ack[0] == 1 && U16(ack, 1) == slot && ack.Length == 7 + ack[6] * 10, "现场同型九字节分解返回完整成功ACK");
                byte[] delta = peer.Expect(0, 14).Body;
                That(U32(delta, Entry(delta, slot) + 2) == uint.MaxValue, "ACK后权威差量移除原装备");
                if (slot != 11)
                {
                    int row = Enumerable.Range(0, ack[6]).Select(index => 7 + index * 10).Single(index => U32(ack, index + 2) == epic.PieceId);
                    byte[] notice = peer.Expect(0, 1152).Body;
                    int balance = System.Text.Json.JsonSerializer.Deserialize<int[]>((string)fixture.Query("SELECT counts FROM account_epic_books")[0][0])![epic.Index];
                    pieceBalance = checked(pieceBalance + (int)U32(ack, row + 6));
                    That(U16(ack, row) == ushort.MaxValue && U32(ack, row + 6) is >= 90 and <= 110 &&
                        notice[0] == 1 && U32(notice, 1) == 1 && U32(notice, 5) == epic.PieceId && U32(notice, 9) == balance &&
                        balance == pieceBalance && BookRevision() == revision + 1, "首次与已有图鉴余额恰加ACK增量、版本恰增一、1152同步绝对余额");
                }
                else That(BookRevision() == revision, "无碎片分解不递增图鉴版本");
                peer.Expect(0, 1801); peer.Barrier();
                mission += targets; events++;
                That(MissionCount() == mission && EventCount() == events, "每件成功分解只写一个战令来源并各计一次");
                string saved = Snapshot();
                Send(peer, slot, -1); peer.Expect(1, 26, [0, 17, 0]); peer.Barrier();
                That(Snapshot() == saved, "重复已消费源返回有界拒绝ACK，资产和战令不重发且连接继续");
            }
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "战令碎片分解真实TCP库完整性通过");

        string Snapshot() => fixture.Snapshot("SELECT * FROM characters") + fixture.Snapshot("SELECT * FROM character_inventory_items") +
            fixture.Snapshot("SELECT * FROM account_epic_books") + fixture.Snapshot("SELECT * FROM epic_piece_audit_log") +
            fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT * FROM inventory_audit_log") +
            fixture.Snapshot("SELECT * FROM currency_audit_log") + fixture.Snapshot("SELECT * FROM account_battle_pass_source_events") +
            fixture.Snapshot("SELECT * FROM account_battle_pass_mission_progress") + fixture.Snapshot("SELECT * FROM account_battle_pass_seasons");
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
