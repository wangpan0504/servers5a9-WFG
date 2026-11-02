using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.PcRoom;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PcRoomTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "pcroom"), gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var catalog = new PcRoomCatalog(resources.Archive);
        Character original, lowLevel;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            original = repository.Create(owner, CharacterCreation.Prepare("PcRoomTcp", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with { Level = 95, AcceptedStoryLevel = 1 });
            lowLevel = repository.Create(owner, CharacterCreation.Prepare("PcLowTcp", resources.Characters.Get(9),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with { Level = 4, AcceptedStoryLevel = 1 });
        }
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "pc-room on", out string reply) &&
            reply == "OK {\"premiumPcRoom\":true}", "临时服务显式开启网吧环境，不修改现场配置或存档权益");
        long finalUid = 0;
        int rentalDeadline = 0;
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(original.Slot, premiumPcRoom: true);
            AssertBlackDiamondActor(entry[2].Body, active: false, pcRoom: true);
            That(peer.PremiumServices.TryGetValue(128, out long remaining) && remaining > 0,
                "网吧Actor场所位与会员128同时存在，原生菜单和礼盒使用不是仅靠Buff");
            Bytes(peer.PcRoomAccelerationState, [0, 0, 0], "首次未勾选燃烧也在有效128之后收到完整490=000000");
            That(fixture.Query("SELECT enabled FROM character_fatigue_acceleration WHERE character_id=$p0", original.Id) is [] or [[0L]],
                "初始化网吧菜单不把角色保存的燃烧选择强制改成开启");
            peer.Barrier();
            peer.Send(1550);
            var login = Claim(peer, 1550, [1, 0, 0, 0, 0]);
            That(login.Any(packet => packet is { Kind: 0, Opcode: 14 }) &&
                login.FindIndex(packet => packet.Opcode == 14) < login.FindIndex(packet => packet.Opcode == 1517) &&
                login.Single(packet => packet.Opcode == 1517).Body is [0, 0, 0, 0, 1],
                "登录礼盒先真实14入包，再1517已领，最后完整1550ACK驱动窗口刷新");
            peer.Barrier();
            var current = Load();
            var loginBox = current.Inventory.Items.Single(item => item.Core.ItemId == catalog.Premium.LoginItemId);
            That(loginBox.Uid > 0 && !loginBox.Core.IsRental, "登录奖是实际普通礼盒，不伪装租赁或临时UID");
            string before = State();
            peer.Send(1550);
            var repeated = Claim(peer, 1550, [0, 2, 0, 2, 0, 0, 0]);
            That(!repeated.Any(packet => packet.Opcode == 14) && State() == before, "真实重复请求回7B已领取失败，不再次发物品或占UID");
            peer.Barrier();

            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "pc-room off", out reply) &&
                reply == "OK {\"premiumPcRoom\":false}", "关闭当前免费网吧环境以验证原生128完整撤销");
            peer.CheckPcRoomRefresh(enabled: false, blackDiamond: false);
            var ordinaryEntry = peer.Select(original.Slot);
            AssertBlackDiamondActor(ordinaryEntry[2].Body, active: false, pcRoom: false);
            That(!peer.PremiumServices.TryGetValue(128, out remaining) || remaining == 0,
                "ACK7与完整名册后明确重选入城，完整1760不再激活128");
            peer.Barrier();
            before = State();
            peer.Send(160, Build(writer => { writer.Write(loginBox.Position.Index); writer.Write(1); }));
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(State() == before, "重新入城后关闭资格的原登录礼盒拒绝打开，保留物品、UID及revision");
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "pc-room on", out reply) &&
                reply == "OK {\"premiumPcRoom\":true}", "恢复当前免费网吧环境");
            peer.CheckPcRoomRefresh(enabled: true, blackDiamond: false);
            Bytes(peer.PcRoomAccelerationState, [0, 0, 0], "资格恢复后1760先到，490仍回放原未勾选state0");

            // 使用同一原生开盒入口，证明资源上的must use at pcbang在真实网吧环境下可用。
            OpenBox(peer, loginBox.Position.Index);
            current = Load();
            That(current.Inventory.Items.All(item => item.Uid != loginBox.Uid) && current.Inventory.Items.Any(item =>
                item.Core.ItemId is 490007908 or 490007909 && item.Uid > 0), "登录礼盒160实际打开，保留网吧使用限制而非仅发入包");

            peer.Send(1637);
            var rental = Claim(peer, 1637, [1, 0, 0, 0, 0]);
            That(rental.Any(packet => packet.Opcode == 14) && rental.Single(packet => packet.Opcode == 1639).Body is [1],
                "租赁礼盒完整1639状态先于成功ACK到达");
            peer.Barrier();
            current = Load();
            var rentalBox = current.Inventory.Items.Single(item => item.Core.ItemId == catalog.Premium.RentalItemId);
            rentalDeadline = rentalBox.ExpireTime;
            That(rentalBox.Uid > 0 && rentalBox.Core.IsRental && rentalDeadline >= fixture.UtcNow.ToUnixTimeSeconds() + 86390 &&
                rentalDeadline <= fixture.UtcNow.ToUnixTimeSeconds() + 86400, "租赁总盒实际UID及24小时绝对期限已落库");
            OpenBox(peer, rentalBox.Position.Index);
            current = Load();
            int[] childIds = [490010149, 490010161, 490010162, 490010163];
            var children = current.Inventory.Items.Where(item => childIds.Contains(item.Core.ItemId)).ToArray();
            That(current.Inventory.Items.All(item => item.Uid != rentalBox.Uid) && children.Length == 4 &&
                children.All(item => item.Uid > 0 && item.Core.IsRental && item.ExpireTime == rentalDeadline),
                "租赁总盒160生成四类真实子盒，完整继承原截止而不重新计24小时");
            var weaponBox = children.Single(item => item.Core.ItemId == 490010149);
            long[] oldUids = current.Inventory.Items.Select(item => item.Uid).ToArray();
            OpenBox(peer, weaponBox.Position.Index, 401010047);
            var final = Load().Inventory.Items.Single(item => item.Core.ItemId == 401010047 && !oldUids.Contains(item.Uid));
            finalUid = final.Uid;
            That(finalUid > 0 && final.Core is { IsRental: true, Attr: 10, GenuineUpgrade: 3, Value: 999_999_998 } &&
                final.ExpireTime == rentalDeadline,
                "原生武器自选产生强化10、锻造3与固定最高品质装备，租赁标记与总盒原期限均保持");
            foreach (var (childId, products) in new (int, int[])[]
            {
                (490010161, [400150060, 400050076, 400100068, 400200060, 400250066]),
                (490010162, [400300075, 400320079, 400310076]),
                (490010163, [400340208, 400350092])
            })
            {
                current = Load();
                var child = current.Inventory.Items.Single(item => item.Core.ItemId == childId);
                oldUids = current.Inventory.Items.Select(item => item.Uid).ToArray();
                OpenBox(peer, child.Position.Index, products);
                var opened = Load().Inventory.Items;
                var created = opened.Where(item => !oldUids.Contains(item.Uid)).ToArray();
                That(opened.All(item => item.Uid != child.Uid) && created.Length == products.Length &&
                    created.Select(item => item.Core.ItemId).Order().SequenceEqual(products.Order()) &&
                    created.Select(item => item.Uid).Distinct().Count() == products.Length &&
                    created.All(item => item.Uid > 0 && item.Core.IsRental && item.ExpireTime == rentalDeadline),
                    $"子盒{childId}按实际全组选择产生每件独立物理UID，消耗原子盒并继承总盒租赁标记及同一截止");
            }

            peer.Send(493, [1, 0, 0]);
            var weapon = Claim(peer, 493, [1, 0]);
            That(weapon.Any(packet => packet.Opcode == 14), "493新武器独立用14发放，成功ACK旧物扣除表为空");
            peer.Barrier();
            before = State();
            peer.Send(493, [1, 0, 0]);
            Claim(peer, 493, [0, 1, 0]);
            peer.Barrier();
            That(State() == before, "同候选有效租赁重复申请整笔拒绝，不重复发装备");
        }
        string savedAcceleration = fixture.Snapshot("SELECT * FROM character_fatigue_acceleration WHERE character_id=$p0", original.Id);
        using (var peer = Open(fixture))
        {
            peer.Select(original.Slot, premiumPcRoom: true);
            Bytes(peer.PcRoomAccelerationState, [0, 0, 0], "新连接重登入城在有效128之后再次恢复未勾选490=000000");
            That(fixture.Snapshot("SELECT * FROM character_fatigue_acceleration WHERE character_id=$p0", original.Id) == savedAcceleration,
                "重登入城不为恢复菜单改写原角色燃烧选择");
            peer.Barrier();
            peer.Send(1637);
            Claim(peer, 1637, [0, 2, 0, 2, 0, 0, 0]);
            peer.Barrier();
            var restored = Load().Inventory.Items.Single(item => item.Uid == finalUid);
            That(restored.Core.ItemId == 401010047 && restored.Core.IsRental && restored.ExpireTime == rentalDeadline,
                "重连恢复最终租赁装备原UID和原期限，已领租赁礼盒不能重发");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(lowLevel.Slot, combos: StoredCombos(fixture, lowLevel.Id), premiumPcRoom: true);
            peer.Barrier();
            long earliest = fixture.UtcNow.ToUnixTimeSeconds() + 86400;
            peer.Send(493, [2, 0, 0, 0, 1]);
            var granted = Claim(peer, 493, [1, 0]);
            peer.Barrier();
            var current = Load(lowLevel.Slot);
            var rented = current.Inventory.Items.Where(item => item.Core.ItemId is 27163 or 27360).OrderBy(item => item.Core.ItemId).ToArray();
            That(current is { Job: 9, Level: 4 } && granted.Any(packet => packet.Opcode == 14) &&
                rented.Select(item => item.Core.ItemId).SequenceEqual(new[] { 27163, 27360 }) &&
                rented.Select(item => item.Uid).Distinct().Count() == 2 &&
                rented.All(item => item.Uid > 0 && item.Core is { IsRental: true, Attr: 7, GenuineUpgrade: 3 } &&
                    item.ExpireTime == rented[0].ExpireTime && item.ExpireTime >= earliest &&
                    item.ExpireTime <= fixture.UtcNow.ToUnixTimeSeconds() + 86400),
                "4级女鬼剑真实493首档index0/1一次发两件独立物理UID，强化7锻造3及同一24小时截止落库");
            string before = State();
            foreach (var (body, reason) in new (byte[], string)[]
            {
                ([1, 0, 0], "重复未到期index0"),
                ([2, 0, 2, 0, 0], "混合新index2与已有index0"),
                ([1, 0, 3], "首档三候选之外index3")
            })
            {
                peer.Send(493, body);
                var rejected = Claim(peer, 493, [0, 1, 0]);
                peer.Barrier();
                That(!rejected.Any(packet => packet.Opcode == 14) && State() == before,
                    "低等级租赁整笔拒绝且保留原UID、期限与全部资产：" + reason);
            }
            var weapon = rented[0];
            var equipped = current.Inventory.Find(new(InventoryListType.Equipment, 12));
            That(resources.ItemResources.Items.Get(weapon.Core.ItemId).MinimumLevel == 10 &&
                current.Inventory.AccountWallet.ContractExpiry(22) <= fixture.UtcNow.ToUnixTimeSeconds(),
                "低等级穿戴拒绝使用真实10级模板，角色没有降低穿戴等级的契约豁免");
            peer.Send(19, Build(writer =>
            {
                writer.Write((byte)0); writer.Write(weapon.Position.Index); writer.Write(weapon.Core.ItemId); writer.Write(1);
                writer.Write((byte)3); writer.Write((short)12); writer.Write(equipped?.Core.ItemId ?? 0); writer.Write(0);
                writer.Write(-1); writer.Write((ushort)0);
            }));
            peer.Expect(1, 19, [0, 23, 0, 0, 3]);
            peer.Barrier();
            That(State() == before, "493租到10级武器后，4级真实19穿戴仍拒绝并保留原穿戴、租赁UID和期限");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "网吧TCP发奖及原生开盒后完整资产库一致");

        Character Load(ushort? slot = null)
        {
            using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.LoadCharacter(reader, slot ?? original.Slot);
        }
        string State() => string.Concat(new[] { "characters", "character_inventory_items", "character_inventory_slots",
            "account_wallets", "account_currency_balances", "character_fatigue", "account_pcroom_claims",
            "character_pcroom_claims", "inventory_audit_log", "sqlite_sequence" }
            .Select(table => fixture.Snapshot("SELECT * FROM " + table + " ORDER BY rowid")));
    }

    static List<WirePacket> Claim(TcpPeer peer, ushort opcode, byte[] expected)
    {
        var frames = new List<WirePacket>();
        for (int count = 0; count < 16; count++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == opcode)
            {
                Bytes(packet.Body, expected, "网吧原生ACK完整独立预期字节");
                return frames;
            }
            That(packet.Kind == 0 && packet.Opcode is 14 or 490 or 988 or 1517 or 1639,
                "网吧领取只发布实际资产、租赁及面板状态，不夹杂伪成功通知");
            frames.Add(packet);
        }
        throw new InvalidOperationException("网吧领取未在有界状态通知后返回ACK");
    }

    static void OpenBox(TcpPeer peer, short slot, params int[] selectedItems)
    {
        peer.Send(160, Build(writer =>
        {
            writer.Write(slot); writer.Write(1);
            if (selectedItems.Length == 0) return;
            writer.Write((byte)0); writer.Write((byte)0);
            foreach (int id in selectedItems) writer.Write(id);
            writer.Write((ushort)0);
        }));
        That(peer.Expect(1, 160).Body is [1, ..], "网吧环境通过资源限制和完整真实160开盒请求");
        var notices = PartyDungeonTcpTests.Drain(peer);
        That(notices.Any(packet => packet is { Kind: 0, Opcode: 14 }), "开盒提交后同步真实源与产物14，不只返回成功ACK");
    }
}
