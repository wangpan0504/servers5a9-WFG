using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildTeleportTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-teleport"), resources: resources);
        var repository = new CharacterRepository(fixture.Database); var owners = new AccountOwners();
        Character Create(string account, string name)
        {
            using var owner = owners.Acquire(account, static () => { });
            var definition = resources.Characters.Get(0);
            var inventory = InventoryRewardGrantService.CreateStarter(definition);
            if (account == "10000")
            {
                var coin = resources.ItemResources.Items.Get(490011648);
                inventory = inventory with { Items = inventory.Items.Add(new(0,
                    new(Game.Inventory.Models.InventoryListType.Main, ItemSlotBoundService.Range(coin.Kind, inventory).Start),
                    new(coin.Id, coin.Kind, 10))) };
            }
            return repository.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1,
                CompletedQuests = new[] { 3156 }.Concat(resources.Towns.Areas[(38, 5)].Quests)
                    .Concat(resources.Towns.Areas[(38, 5)].AnyQuests).Distinct().ToArray(), Skills = definition.Skills,
                Inventory = inventory
            });
        }
        var master = Create("10000", "TeleportWire"); var member = Create("tp-wire-beta", "TeleportPeer");
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
            VALUES(95,'传送测试','tp-wire',$p0,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(95,$p0,1,1600000000);
            INSERT INTO guild_benefits(guild_id,type,value,expires_unix) VALUES(95,7,3,0);
            """, master.Id);
        await fixture.StartAsync();
        using (var alpha = Open(fixture))
        using (var beta = Open(fixture, "tp-wire-beta"))
        {
            var sequences = new Dictionary<TcpPeer, ushort> { [alpha] = 100, [beta] = 100 };
            void Send(TcpPeer peer, ushort opcode, byte[]? body = null) => peer.SendAtSequence(opcode, sequences[peer]++, body);
            void Enter(TcpPeer peer)
            {
                peer.Send(8, [255, 255, 2]); peer.Send(4, Build(writer => writer.Write((ushort)0)));
                bool entered = false;
                for (int i = 0; i < 256; i++)
                    if (peer.Read() is { Kind: 0, Opcode: 124 }) { peer.ReadWeeklyDungeon(); entered = true; break; }
                That(entered, "传送TCP完成真实选角入城"); Flush(peer);
            }
            WirePacket[] Flush(TcpPeer peer)
            {
                Send(peer, 156, Build(writer =>
                {
                    byte[] name = CharacterName.Encoding.GetBytes("TpBarrier"); writer.Write(name.Length); writer.Write(name);
                }));
                var rows = new List<WirePacket>();
                for (int i = 0; i < 256; i++)
                {
                    var packet = peer.Read();
                    if (packet is { Kind: 1, Opcode: 156 }) { Bytes(packet.Body, [1], "传送后继命令哨兵"); return rows.ToArray(); }
                    That(Allowed(peer, packet), "传送只允许明确的场景、公会与真实队伍刷新"); rows.Add(packet);
                }
                throw new InvalidOperationException("公会传送刷新超过帧预算");
            }
            static WirePacket Ack(WirePacket[] packets, ushort opcode)
            {
                var matches = packets.Where(packet => packet.Kind == 1 && packet.Opcode == opcode).ToArray();
                That(matches.Length == 1, "公会传送只有一份请求确认"); return matches[0];
            }
            long Remaining() => Convert.ToInt64(fixture.Query("SELECT value FROM guild_benefits WHERE guild_id=95 AND type=7")[0][0]);
            string Positions() => fixture.Snapshot("SELECT id,revision,town,area,x,y FROM characters ORDER BY id");
            Enter(alpha); Enter(beta);
            string assets = fixture.Snapshot("SELECT id,gold,revive_coins FROM characters ORDER BY id");
            byte[] solo = Request(38, 0, 1327, 234);
            ushort soloSequence = sequences[alpha]; Send(alpha, 434, solo);
            var soloPackets = Flush(alpha);
            Bytes(Ack(soloPackets, 434).Body, [1], "单人434公会传送成功确认");
            That(soloPackets.Any(packet => packet is { Kind: 0, Opcode: 24 }) && Remaining() == 2 &&
                fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", master.Id) is [[38L, 0L, 1327L, 234L]],
                "单人真实source0下发切图且同事务扣一次公会传送次数");
            string committed = Positions();
            alpha.SendAtSequence(434, soloSequence, solo);
            var duplicate = Flush(alpha);
            Bytes(Ack(duplicate, 434).Body, [1], "原序号434返回已提交回执");
            That(Positions() == committed && Remaining() == 2 && !duplicate.Any(packet => packet is { Kind: 0, Opcode: 24 }),
                "跨网络重放原434不重复扣次数、写revision或再次切图");
            fixture.Sql("CREATE TRIGGER tp_wire_fail BEFORE INSERT ON guild_teleport_receipts BEGIN SELECT RAISE(ABORT,'wire rollback'); END");
            Send(alpha, 434, Request(39, 3, 783, 205));
            Bytes(Ack(Flush(alpha), 434).Body, [0, 7, 0, 39, 0, 0, 0, 0, 0], "晚期SQL失败回复95原生失败包");
            That(Positions() == committed && Remaining() == 2, "真实TCP末段SQL失败保持位置、revision与次数");
            fixture.Sql("DROP TRIGGER tp_wire_fail");
            Send(beta, 1426); Flush(beta); Flush(alpha);
            byte[] Peer(ushort id, bool invite) => Build(writer =>
            {
                writer.Write(id); writer.Write((byte)0); writer.Write(0u); if (invite) writer.Write(new byte[6]);
            });
            Send(alpha, 10, Peer(member.Id, true));
            Bytes(Ack(Flush(alpha), 10).Body, [1], "公会传送测试发起真实组队邀请");
            That(Flush(beta).Any(packet => packet is { Kind: 0, Opcode: 7 }), "队员连接实际收到组队邀请");
            Send(beta, 11, Peer(master.Id, false)); Flush(beta);
            That(Flush(alpha).Any(packet => packet is { Kind: 0, Opcode: 8, Body.Length: 7 } && U16(packet.Body, 0) == member.Id),
                "队长连接收到目标成员真实组队接受通知");
            string partyBefore = Positions();
            Send(alpha, 434, Request(39, 3, 783, 205));
            var opening = Flush(alpha); Bytes(Ack(opening, 434).Body, [1], "两人公会传送先确认真实窗口");
            var invitation = Flush(beta).Single(packet => packet is { Kind: 0, Opcode: 377 });
            That(invitation.Body.Length == 30 && invitation.Body[1] == 0 && U16(invitation.Body, 5) == master.Id &&
                U16(invitation.Body, 8) == member.Id && invitation.Body[7] == 1 && invitation.Body[10] == 0,
                "另一连接收到377八行名册，只有真实队长预同意");
            Send(beta, 435, [0]); var refused = Flush(beta); Flush(alpha);
            That(refused.Any(packet => packet is { Kind: 0, Opcode: 377 } && packet.Body[1] == 2) &&
                Positions() == partyBefore && Remaining() == 2, "435拒绝取消两人窗口，全员不移动也不消费");
            Send(alpha, 434, Request(39, 3, 783, 205));
            Bytes(Ack(Flush(alpha), 434).Body, [1], "拒绝后新请求可重新开启"); Flush(beta);
            Send(beta, 435, [1]); var arrivedBeta = Flush(beta); var arrivedAlpha = Flush(alpha);
            That(new[] { arrivedAlpha, arrivedBeta }.All(packets =>
                packets.Any(packet => packet is { Kind: 0, Opcode: 377 } && packet.Body[1] == 1) &&
                packets.Any(packet => packet is { Kind: 0, Opcode: 24 })) && Remaining() == 1 &&
                fixture.Query("SELECT town,area,x,y FROM characters ORDER BY id") is
                    [[39L, 3L, 783L, 205L], [39L, 3L, 783L, 205L]],
                "最后一位435同意后两连接收到真实377完成和24，全员位置落库只扣一次");
            That(fixture.Query("SELECT count(*) FROM guild_teleport_receipts") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM character_teleport_cooldowns") is [[0L]] &&
                fixture.Snapshot("SELECT id,gold,revive_coins FROM characters ORDER BY id") == assets,
                "公会传送耐久消费两次，不使用个人光环冷却也不改个人资产");
            long Coins() => Convert.ToInt64(fixture.Query("""
                SELECT COALESCE(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items
                WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648
                """, master.Id)[0][0]);
            byte[] native = Convert.FromHexString("0000000000000026059E01950103");
            byte[] nativeNext = Convert.FromHexString("000000000000002605AA02580103");
            foreach (int field in new[] { 0, 6, 13 })
            {
                var otherSource = native.ToArray(); otherSource[field] = field == 13 ? (byte)2 : (byte)1;
                Send(alpha, 237, otherSource);
                Bytes(Ack(Flush(alpha), 237).Body, [0, 23, 0], "其他槽位、模式和地图来源不取得公会237资格");
            }
            That(Coins() == 10, "来源2与伪造槽位、模式不扣公会贡献币");
            ushort nativeSequence = sequences[alpha];
            Send(alpha, 237, native);
            var nativePackets = Flush(alpha); Flush(beta);
            Bytes(Ack(nativePackets, 237).Body, [1, 0, 0, 0, 0, 0, 0], "实收零槽零物品237回正确7字节成功包");
            That(Coins() == 5 && Remaining() == 1 &&
                fixture.Query("SELECT town,area,x,y,direction FROM characters WHERE id=$p0", master.Id) is [[38L, 5L, 414L, 405L, 5L]] &&
                nativePackets.Any(packet => packet is { Kind: 0, Opcode: 1681 }) &&
                nativePackets.Any(packet => packet is { Kind: 0, Opcode: 24 }) &&
                !nativePackets.Any(packet => packet.Opcode is 377 or 378 or 379),
                "实机237原样落点并扣5贡献币，发布库存和切图，不消费434公共次数或伪造光环");
            committed = Positions();
            alpha.SendAtSequence(237, nativeSequence, native);
            var replayed = Flush(alpha); Flush(beta);
            Bytes(Ack(replayed, 237).Body, [1, 0, 0, 0, 0, 0, 0], "原237序号重放已提交确认");
            That(Coins() == 5 && Positions() == committed && !replayed.Any(packet => packet.Opcode == 24),
                "237重放不重复扣币、移动或写角色版本");
            alpha.SendAtSequence(237, nativeSequence, nativeNext);
            Bytes(Ack(Flush(alpha), 237).Body, [0, 0, 0], "相同序号换目的地拒绝且不误报匹配中");
            fixture.Sql("CREATE TRIGGER guild_map_fail BEFORE INSERT ON guild_teleport_receipts BEGIN SELECT RAISE(ABORT,'guild map rollback'); END");
            Send(alpha, 237, nativeNext);
            Bytes(Ack(Flush(alpha), 237).Body, [0, 0, 0], "237末段SQL失败返回中性拒绝而不断线");
            That(Coins() == 5 && Positions() == committed && Remaining() == 1,
                "237扣币、位置、版本与末段回执失败共同回滚");
            fixture.Sql("DROP TRIGGER guild_map_fail");
            nativeSequence = sequences[alpha]; Send(alpha, 237, nativeNext);
            Bytes(Ack(Flush(alpha), 237).Body, [1, 0, 0, 0, 0, 0, 0], "失败后新237可正常花费最后5币"); Flush(beta);
            committed = Positions();
            alpha.SendAtSequence(237, nativeSequence, nativeNext);
            Bytes(Ack(Flush(alpha), 237).Body, [1, 0, 0, 0, 0, 0, 0], "余额归零后原成功237仍重放确认");
            Send(alpha, 237, native);
            Bytes(Ack(Flush(alpha), 237).Body, [0, 0, 0], "不足5币的全新237中性拒绝");
            Send(beta, 237, native);
            Bytes(Ack(Flush(beta), 237).Body, [0, 0, 0], "非公会成员不能借零槽237免费传送");
            That(Coins() == 0 && Positions() == committed && Remaining() == 1 &&
                fixture.Query("SELECT count(*) FROM guild_teleport_receipts") is [[4L]],
                "两次个人地图传送只扣10币，失败和重放零资产影响且保护公共次数");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "传送TCP结束数据库与外键完整");
        Console.WriteLine($"PASS {Count - before} 公会单人及真实双连接确认传送TCP断言");
    }

    static byte[] Request(byte town, byte area, ushort x, ushort y) => Build(writer =>
    {
        writer.Write(town); writer.Write(area); writer.Write(x); writer.Write(y); writer.Write((byte)5); writer.Write((byte)0);
    });
    static bool Allowed(TcpPeer peer, WirePacket packet)
    {
        if (packet.Kind == 0 && packet.Opcode == 744)
        {
            peer.CheckGuildDungeon(packet.Body);
            return true;
        }
        return packet.Kind == 0 && packet.Opcode is
            2 or 3 or 6 or 7 or 8 or 9 or 11 or 22 or 23 or 24 or 26 or 58 or 70 or 127 or 151 or 153 or 377 or 379 or 487 or 488 or 1681 ||
            packet.Kind == 1 && packet.Opcode is 10 or 11 or 140 or 141 or 237 or 434 or 744 or 1454;
    }
}
