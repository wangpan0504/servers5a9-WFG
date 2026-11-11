using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Development;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.CashMileageTcpChecks;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static async Task CapacitiesAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-shop-capacities"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var avatar = r.Items.Get(415600033);
            var material = r.Items.Get(3299);
            var role = repository.Create(owner, new("ShopCapacity", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([
                    InventoryRewardGrantService.CreateItem(avatar, new(InventoryListType.Avatar, 0),
                        InventoryRewardGrantService.Create(avatar) with { EquipmentLockId = 1 }),
                    InventoryRewardGrantService.CreateItem(material, new(InventoryListType.PersonalCargo, 0),
                        InventoryRewardGrantService.Create(material, 2) with { EquipmentLockId = 1 })])
            });
            InventoryService.Commit(repository, owner, role,
                new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Cera, 1_000_000));
        }
        int[] commodities = [270623, 100063, 102925, 104415];
        That(r.CashShop.Bonus(102925).Count == 2 && r.Items.Get(r.CashShop.Product(104415)!.ItemId).AvatarExpansionStage == 16,
            "真实混单包含永久装扮、个人金库直扩、两项附赠和下一档装扮扩展");
        await fixture.StartAsync();
        fixture.Sql("CREATE TRIGGER fail_mixed_capacity BEFORE UPDATE OF avatar_expansion ON characters WHEN NEW.avatar_expansion>OLD.avatar_expansion BEGIN SELECT RAISE(ABORT,'mixed capacity rollback'); END");
        using (var peer = Open(fixture))
        {
            Select(peer); string before = State(); peer.Send(64, Cart()); peer.Closed();
            That(State() == before, "混单晚期SQL异常不发布容量或ACK，容量、装扮详情、赠品、钱包和审计一起回滚");
        }
        fixture.Sql("DROP TRIGGER fail_mixed_capacity");
        using (var peer = Open(fixture))
        {
            Select(peer);
            var mileage = MileageBefore(fixture, 40 + commodities.Skip(1).Sum(id => ExpectedCashMileage(r.CashShop.Product(id)!)));
            peer.Send(64, Cart());
            var avatar = peer.Expect(0, 13).Body;
            That(avatar[0] == 1 && U16(avatar, 1) == 112 && U16(avatar, 3) == 1 && U32(avatar, 7) == 415600033,
                "首个ACK前装扮只安装新容量和原条目，不提前公开混单新增装扮");
            peer.Expect(0, 251, [1, 0, 1, 0, 0, 1]);
            peer.Expect(0, 66, [1, 0, 24, 0]);
            var cargo = peer.Expect(0, 13).Body;
            That(cargo[0] == 2 && U16(cargo, 1) == 24 && U16(cargo, 3) == 1 && U32(cargo, 7) == 3299 && U32(cargo, 11) == 2,
                "首个ACK前个人金库严格66→13，并只恢复原有完整物品");
            peer.Expect(0, 251, [1, 0, 2, 0, 0, 1]);
            foreach (int commodity in commodities)
            {
                var ack = peer.Expect(1, 64).Body;
                var bonuses = r.CashShop.Bonus(commodity);
                That(ack[0] == 1 && U32(ack, 6) == commodity && U16(ack, 22) == bonuses.Count && ack.Length == 24 + 8 * bonuses.Count,
                    "各商品仅一次成功ACK，全部容量已在首个确认前安装 " + commodity);
                for (int i = 0; i < bonuses.Count; i++)
                    That(U32(ack, 24 + i * 8) == bonuses[i].ItemId && U32(ack, 28 + i * 8) == bonuses[i].Count,
                        "附赠仍由原订单ACK完整发布");
            }
            var main = peer.Expect(0, 14).Body;
            That(main[0] == 0 && U16(main, 1) > 0, "普通商品与附赠的Main差量保留在所有ACK之后");
            AvatarRow(peer.Expect(0, 14).Body, 4, 0);
            // 前置全量使用旧索引恢复锁，最终差量之后仍按最终索引恢复完整锁。
            peer.Expect(0, 251, [1, 0, 1, 0, 0, 1]);
            int cost = 1320 + commodities.Skip(1).Sum(id => r.CashShop.Product(id)!.CeraPrice);
            That(U32(peer.Expect(0, 53).Body, 1) == 1_000_000 - cost, "容量前置不提前发布钱包，混单仍原子扣准确费用");
            MileageAfter(peer, fixture, mileage);
            peer.Barrier(packet => That(packet.Opcode is not (13 or 66), "最终投影不重复容量通知"));
            string committed = State();
            peer.Send(64, Cart()); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == committed, "混单任一容量已达档位时整单拒绝，不发布容量、不重扣普通商品或附赠");
            That(fixture.Query("SELECT avatar_expansion,cargo_capacity FROM characters") is [[112L, 24L]] &&
                fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[2L]], "两种容量和混单装扮详情实际同事务落库");
            foreach (var bonus in r.CashShop.Bonus(102925))
                That(fixture.Query("SELECT item_core FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=$p0", bonus.ItemId)
                    .Sum(row => JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count) == bonus.Count,
                    "混单真实赠品只发放一次 " + bonus.ItemId);
        }
        await fixture.StopAsync();
        await CapacityOwnershipAsync(resources, Path.GetDirectoryName(fixture.Database)!);
        await CapacityPartyAsync(resources, Path.GetDirectoryName(fixture.Database)!);

        static void Select(TcpPeer peer)
        {
            peer.Select(inventoryNotices: [(0, 251)]); peer.Barrier();
        }
        byte[] Cart()
        {
            byte[] body = new byte[4 + commodities.Length * 15]; body[2] = checked((byte)commodities.Length);
            for (int i = 0; i < commodities.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7 + 15 * i), commodities[i]);
            body[5] = 4; body[6] = 3;
            return body;
        }
        string State() => fixture.Snapshot("SELECT revision,avatar_expansion,cargo_capacity FROM characters") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT * FROM currency_audit_log") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log");
    }

    static async Task CapacityOwnershipAsync(TcpTestResources resources, string root)
    {
        var r = resources.ItemResources;
        foreach (string change in new[] { "reselect", "snapshot", "takeover", "snapshot-battle-pass", "cargo-window-battle-pass", "cargo-list-battle-pass", "first-ack-battle-pass", "capacity-change-battle-pass" })
        {
            bool formal = change.EndsWith("-battle-pass", StringComparison.Ordinal);
            ushort expectedAvatar = change == "capacity-change-battle-pass" ? (ushort)119 : (ushort)112;
            ushort expectedCargo = change == "capacity-change-battle-pass" ? (ushort)40 : (ushort)24;
            var clock = new CapacityClock(new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8)));
            var policy = formal ? new BattlePassMissionPolicy("capacity-publication", clock.GetUtcNow().AddHours(-1),
                BattlePassCatalog.Load(resources.Archive)) : null;
            string path = Path.Combine(root, "capacity-" + change + ".db");
            var repository = new CharacterRepository(path);
            if (policy is not null) repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("capacity-owner", () => { });
            var oldAvatar = r.Items.Get(415600033);
            var oldMaterial = r.Items.Get(3299);
            var first = repository.Create(owner, new("CapacityOne", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60,
                Inventory = new([
                    InventoryRewardGrantService.CreateItem(oldAvatar, new(InventoryListType.Avatar, 0),
                        InventoryRewardGrantService.Create(oldAvatar) with { SortLockFlag = 1, EquipmentLockId = 1 }),
                    InventoryRewardGrantService.CreateItem(oldMaterial, new(InventoryListType.PersonalCargo, 0),
                        InventoryRewardGrantService.Create(oldMaterial, 2) with { SortLockFlag = 1, EquipmentLockId = 1 })])
            });
            repository.Create(owner, new("CapacityTwo", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 60 });
            InventoryService.Commit(repository, owner, first, new CurrencyService(r.GoldLimits).Grant(first, CurrencyKind.Cera, 100_000));
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            long epoch = session.SelectionEpoch;
            var stats = new CharacterStatComputer(resources.Characters);
            var projection = new InventoryProjection(stats, new TownProjection(resources.Towns, stats, null, r.EpicPieces), r.EpicPieces);
            using var log = new ServerLog(Path.Combine(root, "capacity-log-" + change));
            var commands = new GameCommandRegistry();
            var pass = policy is null ? null : new BattlePassProjection("capacity-publication", policy.Catalog, clock);
            new ShopHandler(new(r.Items, r.Shops, r.GoldLimits, r.EpicPieces),
                new(r.Items, r.CashShop, r.GoldLimits, r.EpicPieces, r.Cargo, contracts: r.Contracts, clock: clock), projection, log,
                battlePassCertificateChanged: pass is null ? null : pass.MissionStateAsync).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new ConcurrentQueue<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            AccountOwners.Lease? replacement = null;
            bool changed = false;
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (kind, opcode, _, payload, action) =>
            {
                sent.Enqueue((kind, opcode, payload.ToArray(), action));
                bool trigger = change switch
                {
                    "cargo-window-battle-pass" => kind == 0 && opcode == 66,
                    "cargo-list-battle-pass" => kind == 0 && opcode == 13 && payload.Span[0] == 2,
                    "first-ack-battle-pass" => kind == 1 && opcode == 64,
                    _ => sent.Count == 1
                };
                if (changed || !trigger) return;
                changed = true;
                if (change == "reselect")
                {
                    session.ReturnToRoster(); session.Select(1); session.MarkReady();
                }
                else if (formal)
                {
                    var committed = session.Selected!;
                    That(DevelopmentGm.Answer("recharge cera 100", "capacity-owner", repository, owners, _ => session)
                        .StartsWith("OK ", StringComparison.Ordinal) && !ReferenceEquals(committed, session.Selected) &&
                        session.Selected!.Id == first.Id && session.SelectionEpoch == epoch,
                        "指定首容量/首ACK回调真实GM充值替换同角色快照且保持选角纪元");
                    if (change == "capacity-change-battle-pass")
                        That(session.PlanAndCommitInventory(role => new(true, 0, new(role.Inventory,
                            role.Inventory with { AvatarExpansion = expectedAvatar, CargoCapacity = expectedCargo },
                            "capacity_publication_fixture"))).Accepted, "隔离并发提交下一合法装扮阶段及个人金库档位");
                }
                else if (change == "snapshot")
                    session.PlanAndCommitInventory(role => new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Gold, 1));
                else replacement = owners.Acquire("capacity-owner", () => { });
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            byte[] body = new byte[34]; body[2] = 2;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), 104415);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(22), 100063);
            try
            {
                try { await commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.BUY_CERASHOP_ITEM, body)).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (change == "takeover" && error is InvalidOperationException or IOException or OperationCanceledException) { }
                var frames = sent.ToArray();
                if (formal)
                {
                    int[] acknowledgements = frames.Select((packet, index) => (packet, index))
                        .Where(row => row.packet is { Kind: 1, Opcode: 64 }).Select(row => row.index).ToArray();
                    That(acknowledgements.Length == 2, "正式战令回调下同角色快照变化仍每商品仅一次ACK");
                    That(changed && U32(frames[acknowledgements[0]].Body, 6) == 104415 &&
                        U32(frames[acknowledgements[1]].Body, 6) == 100063, "确定性交错确已发生，已写ACK索引不回退或重发");
                    foreach (int index in acknowledgements)
                    {
                        That(frames.Take(index).Any(packet => packet is { Kind: 0, Opcode: 13 } &&
                            packet.Body[0] == 1 && U16(packet.Body, 1) == expectedAvatar) &&
                            frames.Take(index).Any(packet => packet is { Kind: 0, Opcode: 66 } && U16(packet.Body, 0) == 1 && U16(packet.Body, 2) == expectedCargo) &&
                            frames.Take(index).Any(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 2 && U16(packet.Body, 1) == expectedCargo),
                            "正式战令双扩容：每个ACK前所有容量均已实际送达，guard拒发不能计为成功");
                        foreach (byte list in new byte[] { 1, 2 })
                            That(frames.Take(index).Any(packet => packet is { Kind: 1, Opcode: 713 } && packet.Body[1] == list) &&
                                frames.Take(index).Any(packet => packet is { Kind: 0, Opcode: 251 } && packet.Body[2] == list),
                                "每个ACK前两种容量链的原排序锁及装备锁均实际完成");
                    }
                    var wallet = frames.Last(packet => packet is { Kind: 0, Opcode: 53 }).Body;
                    That(U32(wallet, 1) == 100_100 - r.CashShop.Product(104415)!.CeraPrice - r.CashShop.Product(100063)!.CeraPrice,
                        "正式恢复保留充值后真实点券余额，不重扣已提交订单");
                    That(frames.Any(packet => packet.Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION),
                        "实际消费经验与正式战令页面恢复保持");
                    That(U16(frames.Last(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 1).Body, 1) == expectedAvatar &&
                        U16(frames.Last(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 2).Body, 1) == expectedCargo,
                        "并发后沿原绝对全量恢复，允许容量重发且最终必须为当前已提交值");
                }
                else
                    That(frames.Select(packet => (packet.Kind, packet.Opcode)).SequenceEqual(new (byte Kind, ushort Opcode)[] { (0, 13) }),
                        "首项容量送达后" + change + "不再发布其余容量、锁、钱包或旧订单ACK");
                // 数据库查询只证明旧订单已提交；不能因发送被守卫拒绝而重放购买。
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = path, Pooling = false }.ToString());
                connection.Open();
                using var query = connection.CreateCommand();
                query.CommandText = "SELECT avatar_expansion=$avatar AND cargo_capacity=$cargo FROM characters WHERE id=$id";
                query.Parameters.AddWithValue("$id", first.Id);
                query.Parameters.AddWithValue("$avatar", expectedAvatar);
                query.Parameters.AddWithValue("$cargo", expectedCargo);
                That(Convert.ToInt64(query.ExecuteScalar()) == 1, "发送身份失效不撤销已提交的两种容量");
                if (formal)
                {
                    query.CommandText = "SELECT count(*) FROM account_battle_pass_cera_orders";
                    That(Convert.ToInt64(query.ExecuteScalar()) == 1, "容量恢复不重放消费经验订单");
                    query.CommandText = "SELECT count(*) FROM currency_audit_log WHERE action_name='cash_shop_buy' AND currency_kind=15";
                    That(Convert.ToInt64(query.ExecuteScalar()) == 1, "容量恢复不重复扣点券");
                }
            }
            finally { replacement?.Dispose(); }
        }
    }

    static async Task CapacityPartyAsync(TcpTestResources resources, string root)
    {
        var r = resources.ItemResources;
        var repository = new CharacterRepository(Path.Combine(root, "capacity-party.db"));
        var owners = new AccountOwners();
        using var slowOwner = owners.Acquire("capacity-party-slow", () => { });
        using var fastOwner = owners.Acquire("capacity-party-fast", () => { });
        var role = repository.Create(slowOwner, new("CapacitySlow", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 60 });
        InventoryService.Commit(repository, slowOwner, role, new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Cera, 100_000));
        repository.Create(fastOwner, new("CapacityFast", resources.Towns.Room, resources.Towns.InitialReturn));
        var party = new DfoServer.Game.Party.PartySnapshot(1, [], 0, 1);
        var slowSession = new GameSession(repository, resources.Towns, slowOwner) { CurrentParty = () => party };
        var fastSession = new GameSession(repository, resources.Towns, fastOwner) { CurrentParty = () => party };
        slowSession.Select(0); slowSession.MarkReady(); fastSession.Select(0); fastSession.MarkReady();
        using var slowStop = new CancellationTokenSource();
        using var fastStop = new CancellationTokenSource();
        using var slowStream = new global::DfoServer.GameSenderBudgetTests.HeldStream();
        using var fastStream = new MemoryStream();
        var sent = new ConcurrentQueue<(byte Kind, ushort Opcode, byte[] Body)>();
        await using var slow = new GameConnectionSender(slowOwner, slowStream, slowStop, 0,
            (kind, opcode, _, body, _) => sent.Enqueue((kind, opcode, body.ToArray())));
        await using var fast = new GameConnectionSender(fastOwner, fastStream, fastStop, 0, (_, _, _, _, _) => { });
        var slowContext = new GameConnectionContext(new(1, IPAddress.Loopback, slowStream, slowStop), slowOwner, slowSession, slow);
        var fastContext = new GameConnectionContext(new(2, IPAddress.Loopback, fastStream, fastStop), fastOwner, fastSession, fast);
        var stats = new CharacterStatComputer(resources.Characters);
        var projection = new InventoryProjection(stats, new TownProjection(resources.Towns, stats, null, r.EpicPieces), r.EpicPieces);
        using var log = new ServerLog(Path.Combine(root, "capacity-party-log"));
        var commands = new GameCommandRegistry();
        new ShopHandler(new(r.Items, r.Shops, r.GoldLimits, r.EpicPieces),
            new(r.Items, r.CashShop, r.GoldLimits, r.EpicPieces, r.Cargo, contracts: r.Contracts, avatars: r.AvatarPackages),
            projection, log).Register(commands);
        commands.Register(CmdPacketType.SET_PARTY_INFO, async (context, _) =>
        {
            await context.Reply(CmdPacketType.SET_PARTY_INFO, [1], "capacity_party_fast_reply");
            return CommandResult.Handled;
        }, nameof(CashShopTcpTests));
        commands.Seal();
        int[] commodities = [270623, 100063, 102925, 104415];
        byte[] cart = new byte[4 + 15 * commodities.Length]; cart[2] = checked((byte)commodities.Length);
        for (int i = 0; i < commodities.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(cart.AsSpan(7 + 15 * i), commodities[i]);
        cart[5] = 4; cart[6] = 3;
        Task purchase = commands.DispatchAsync(slowContext, new(1, (ushort)CmdPacketType.BUY_CERASHOP_ITEM, cart));
        try
        {
            await slowStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            That(!purchase.IsCompleted && slowContext.DispatchGates is null && slowContext.CommandGate.CurrentCount == 1 &&
                party.Commands.CurrentCount == 1, "真实商城首容量写受阻时，AfterDispatch已释放本人和共享队伍门禁");
            await commands.DispatchAsync(fastContext, new(1, (ushort)CmdPacketType.SET_PARTY_INFO, [])).WaitAsync(TimeSpan.FromSeconds(2));
            await fast.Send(1, 1347, [1], "capacity_party_fast_barrier").WaitAsync(TimeSpan.FromSeconds(2));
            That(!purchase.IsCompleted && fastStream.Length > 0 && party.Commands.CurrentCount == 1,
                "商城慢写不阻塞同队另一context完成命令及实际回复");
        }
        finally { slowStream.Release.TrySetResult(); }
        await purchase.WaitAsync(TimeSpan.FromSeconds(5));
        var frames = sent.ToArray();
        That(frames.Take(3).Select(packet => (packet.Kind, packet.Opcode)).SequenceEqual(new (byte, ushort)[] { (0, 13), (0, 66), (0, 13) }),
            "共享门禁释放后仍按两容量及66→13完成再确认购买");
        var acknowledgements = frames.Select((packet, index) => (packet, index)).Where(row => row.packet is { Kind: 1, Opcode: 64 }).ToArray();
        That(acknowledgements.Length == commodities.Length && acknowledgements.Select(row => U32(row.packet.Body, 6))
            .SequenceEqual(commodities.Select(id => (uint)id)), "共享路径每项ACK完整且恰好一次，保留本订单商品顺序");
        int lastAck = acknowledgements[^1].index;
        That(frames.Take(lastAck + 1).All(packet => packet.Opcode is not (14 or 53)) &&
            frames.Skip(lastAck + 1).Count(packet => packet is { Kind: 0, Opcode: 14 } && packet.Body[0] == 0) == 1 &&
            frames.Skip(lastAck + 1).Count(packet => packet is { Kind: 0, Opcode: 14 } && packet.Body[0] == 1) == 1 &&
            frames.Count(packet => packet.Opcode is 13 or 66) == 3 && slow.PendingBytes == 0,
            "稳定共享混单所有奖励和钱包在ACK之后完整一次，容量不重复且发送预算归还");
    }

    sealed class CapacityClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
