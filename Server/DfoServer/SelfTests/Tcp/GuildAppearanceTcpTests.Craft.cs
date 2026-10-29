using System.Buffers.Binary;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class GuildAppearanceTcpTests
{
    static void Crafts(TcpPeer master, TcpPeer member, TcpTestFixture fixture)
    {
        byte[] request = Build(writer =>
        {
            writer.Write(new byte[13]); writer.Write(4u); writer.Write(0u); writer.Write(1004u); writer.Write(new byte[44]);
        });
        member.Send(1738, request);
        member.Expect(1, 1738, [1, 4, 0, 0, 0, 14, 0, 0, 0]);
        That(fixture.Query("SELECT count(*) FROM guild_npc_crafts") is [[0L]], "无NPC权限成员不能使用生产请求扣公共资金");
        master.Send(1738, request);
        master.Expect(1, 1738, [1, 4, 0, 0, 0, 17, 0, 0, 0]);
        That(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=96") is [[4000L]], "生产费用不足拒绝且不扣款");
        fixture.Sql("UPDATE guilds SET public_funds=10000 WHERE guild_id=96");
        long lower = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400;
        master.SendAtSequence(1738, 62000, request);
        master.Expect(1, 1738, [1, 4, 0, 0, 0, 0, 0, 0, 0]);
        byte[] active = ReadCraftSnapshot(master, true);
        ReadCraftSnapshot(member, true);
        long deadline = BinaryPrimitives.ReadInt32LittleEndian(active.AsSpan(8));
        That(deadline >= lower && deadline <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400 &&
            fixture.Query("SELECT public_funds,guild_crystal FROM guilds WHERE guild_id=96") is [[0L, 0L]] &&
            fixture.Query("SELECT benefit_id,reward_count FROM guild_npc_crafts WHERE guild_id=96") is [[1004L, 1L]],
            "真实1738开始制造持久24小时且只扣公款，1772和1773向同会两人投影生产时间");
        master.SendAtSequence(1738, 62000, request);
        master.Expect(1, 1738, [1, 4, 0, 0, 0, 0, 0, 0, 0]);
        ReadCraftSnapshot(master, true); ReadCraftSnapshot(member, true);
        That(fixture.Query("SELECT count(*) FROM guild_npc_craft_receipts") is [[1L]] &&
            fixture.Query("SELECT count(*) FROM guild_npc_crafts") is [[1L]], "同一真实序号重放不重复扣费或重建生产");
        byte[] malformed = request.ToArray(); malformed[68] = 1;
        master.Send(1738, malformed);
        master.Expect(1, 1738, [1, 0, 0, 0, 0, 14, 0, 0, 0]);
        That(fixture.Query("SELECT count(*) FROM guild_npc_crafts") is [[1L]], "畸形生产正文拒绝且不破坏已有生产");
        // 仅隔离夹具推进持久deadline，验证真实Runtime单计时器而不等待24小时。
        fixture.Sql("UPDATE guild_npc_crafts SET deadline=$p0 WHERE guild_id=96", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        ReadCraftSnapshot(master, false); ReadCraftSnapshot(member, false);
        That(fixture.Query("SELECT guild_crystal FROM guilds WHERE guild_id=96") is [[1L]] &&
            fixture.Query("SELECT count(*) FROM guild_npc_crafts") is [[0L]] &&
            fixture.Query("SELECT reward_count,before_crystal,after_crystal FROM guild_npc_craft_completions") is [[1L, 0L, 1L]],
            "真实Runtime到期自动增加公共结晶、清生产并记录完整账本，不伪造手动领取");
        fixture.Sql("UPDATE guilds SET public_funds=10000 WHERE guild_id=96");
        master.SendAtSequence(1738, 62001, request);
        master.Expect(1, 1738, [1, 4, 0, 0, 0, 0, 0, 0, 0]);
        ReadCraftSnapshot(master, true); ReadCraftSnapshot(member, true);
        byte[] cancel = request.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(cancel.AsSpan(13), 5);
        member.Send(1738, cancel);
        member.Expect(1, 1738, [1, 5, 0, 0, 0, 14, 0, 0, 0]);
        master.SendAtSequence(1738, 62001, cancel);
        master.Expect(1, 1738, [1, 5, 0, 0, 0, 14, 0, 0, 0]);
        master.SendAtSequence(1738, 62002, cancel);
        master.Expect(1, 1738, [1, 5, 0, 0, 0, 0, 0, 0, 0]);
        ReadCraftSnapshot(master, false); ReadCraftSnapshot(member, false);
        master.SendAtSequence(1738, 62002, cancel);
        master.Expect(1, 1738, [1, 5, 0, 0, 0, 0, 0, 0, 0]);
        ReadCraftSnapshot(master, false); ReadCraftSnapshot(member, false);
        That(fixture.Query("SELECT public_funds,guild_crystal FROM guilds WHERE guild_id=96") is [[0L, 1L]] &&
            fixture.Query("SELECT count(*) FROM guild_npc_crafts") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM guild_npc_craft_receipts WHERE action=5") is [[1L]],
            "真实取消及重放不退款、不改已完成结晶，清除同会两人的倒计时且回执唯一");
        fixture.Sql("UPDATE guilds SET public_funds=4000,guild_crystal=0 WHERE guild_id=96");
        Drain(master); Drain(member);
    }
    static byte[] ReadCraftSnapshot(TcpPeer peer, bool active)
    {
        byte[]? changed = null;
        for (int guard = 0; guard < 128; guard++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 487 }) { peer.CheckGuildPassiveBuffs(packet); continue; }
            if (packet is { Kind: 0, Opcode: 488 })
            {
                Bytes(packet.Body, Convert.FromHexString("01FC040000"), "制造刷新只移除原守护石1276再恢复");
                continue;
            }
            if (packet.Kind == 0 && packet.Opcode == 744)
            {
                peer.CheckGuildDungeon(packet.Body);
                continue;
            }
            That(packet.Kind == 0 && packet.Opcode is 2 or 70 or 127 or 1771 or 1772 or 1773 or 1774 ||
                packet.Kind == 1 && packet.Opcode is 140 or 744 or 1454,
                "生产广播只消费已确认的公会刷新与福利状态，不吞任意应答");
            if (packet.Opcode == 1772)
            {
                That(packet.Body.Length == 12 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Body) == 1004 &&
                    BinaryPrimitives.ReadInt16LittleEndian(packet.Body.AsSpan(4)) == 0 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(6)) == 0,
                    "真实1772严格消费12字节及对齐字段");
                changed = packet.Body;
            }
            if (packet.Opcode != 1773) continue;
            That(packet.Body.Length == 1080, "真实1773固定1080字节完整消费");
            if (active)
                That(changed is not null && packet.Body.AsSpan(0, 12).SequenceEqual(changed) &&
                    packet.Body.AsSpan(12).IndexOfAnyExcept((byte)0) == -1,
                    "真实1773首公有福利与1772相同，剩余39+50条严格归零");
            else
                That(packet.Body.All(value => value == 0), "到期空福利快照清除原生生产倒计时");
            That(peer.Expect(0, 1774).Body.Length == 16, "制造刷新同时恢复训练快照");
            return packet.Body;
        }
        throw new InvalidOperationException("Guardian craft snapshot was not received");
    }
}
