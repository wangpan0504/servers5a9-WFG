using System.Text;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class GmEventTcpTests
{
    static void VerifyAllDefaultsLogin(TcpTestFixture fixture)
    {
        uint end = checked((uint)Convert.ToInt64(fixture.Query("SELECT end_utc FROM battle_pass_season_rules").Single()[0]));
        using var peer = Open(fixture);
        peer.BeforeAccountMaterialInitialization = current =>
        {
            // 确定性制造旧快照晚读取：先等真实日补给提交，再消费原始563，不能拿此时的新SQL余额回填初始预期。
            That(SpinWait.SpinUntil(() => fixture.Query("""
                SELECT m.balance FROM account_material_balances m JOIN characters c ON c.account=m.account
                WHERE c.id=$p0 AND m.item_id=490013685
                """, current.Id) is [[100L]], TimeSpan.FromSeconds(5)),
                "读取初始化563前真实谋略战日补给已提交100币");
        };
        peer.BeforeMileageInitialization = current =>
        {
            byte[] npc = current.Expect(0, 639).Body;
            That(npc.Length > 5 && npc[0] == 0 && U32(npc, 1) == npc.Length - 5 &&
                Encoding.ASCII.GetString(npc, 5, npc.Length - 5) == "[map] 38 1 [/map] [npc] 400000023 `[right]` 599 240 0 [/npc]",
                "全部默认活动同时开启时原生疲劳商店NPC精确初始化");
            byte[] progress = current.Expect(0, 1103).Body;
            That(progress.Length == 152 && U32(progress) == 2257 && U32(progress, 4) == 0 &&
                U32(progress, 20) == 128 && progress.AsSpan(24).IndexOfAnyExcept((byte)0) == -1,
                "全部默认活动同时开启时无尽大冒险完整零进度初始化");
        };
        peer.ValidateNativePromotionEvents = body =>
        {
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            string Text()
            {
                int length = checked((int)reader.ReadUInt32());
                That(length >= 0 && length <= stream.Length - stream.Position, "活动页字符串长度有界");
                return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(reader.ReadBytes(length));
            }
            var ids = new List<ushort>();
            int count = reader.ReadUInt16();
            That(count is >= 14 and <= 16, "全部默认活动目录有界且保留活动页");
            for (int index = 0; index < count; index++)
            {
                ushort id = reader.ReadUInt16(); ids.Add(id);
                uint metadata = reader.ReadUInt32(); _ = Text(); _ = Text(); byte extended = reader.ReadByte();
                That(metadata == 1 && extended <= 1, "全部活动保留原生记录形状");
                if (extended == 0) continue;
                That(id is 2257 or 10999, "只允许已接通无尽大冒险及礼包累计扩展页");
                That(reader.ReadByte() == 2 && reader.ReadByte() == 4, "原生活动页类型精确为2/4");
                string title = Text(); _ = Text(); _ = Text(); _ = reader.ReadUInt32(); _ = reader.ReadUInt32();
                string route = Text(), content = Text(); byte visible = reader.ReadByte();
                That(visible == 1, "默认活动页可见");
                if (id == 2257) That(title == "无尽大冒险" && route == "fatigue shop event/0/0" &&
                    content.Contains("[show_item_index] 1 490011740", StringComparison.Ordinal),
                    "默认活动共存保留疲劳商店原生路由及真实材料登记");
            }
            ushort[] expected = [2037, 2378, 2395, 282, 2449, 414, 181, 2532, 266, 228, 2370, 2208, 2520,
                .. fixture.Options.EpicRoad2019Enabled ? (ushort[])[10005] : [], .. ids.Contains(10999) ? (ushort[])[10999] : [], 2257];
            That(ids.SequenceEqual(expected), "全部默认活动目录逐项保留，不被谋略战或活动页替换");
            ushort[] active = [2037, 2257, 2395, 228, 2370, 2208, 2532, 2520];
            That(reader.ReadByte() == active.Length, "全部默认活动状态项数完整");
            foreach (ushort id in active)
            {
                That(reader.ReadUInt16() == id, "全部默认活动状态按原生顺序共存");
                for (int index = 0; index < 12; index++)
                    That(reader.ReadUInt32() == (id == 2037 && index == 0 ? 8u :
                        id == 2532 && index == 1 ? 30u : id == 2520 && index == 0 ? end :
                        id == 2370 && index == 0 ? 2u : 0u), "活动12项状态精确保留体力上限及冻结季期限");
            }
            That(stream.Position == stream.Length, "全部活动目录及状态完整消费，无未知尾部");
        };
        peer.Select();
        That(peer.AccountMaterials.Count == 0, "563初始账号快照仍为空，不提前混入后续日补给或角色星河实体");
        byte[] initialCommon = peer.InitialAccountMaterialLists.Single(row => row.Body is [45, ..]).Body;
        That(initialCommon.Length == 126 && U16(initialCommon, 1) == 1 && U16(initialCommon, 3) == 0 &&
            U32(initialCommon, 5) == 490013716 && U32(initialCommon, 9) == 150,
            "全部默认活动登录时108后的list45日历全量保留星河材料490013716，不被活动页清空");
        peer.SendBarrier();
        var packets = new List<WirePacket>();
        for (int index = 0; ; index++)
        {
            That(index < 32, "全活动首次登录后续通知有界");
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "全活动登录后的真实屏障"); break; }
            That(packet.Kind == 0 && packet.Opcode is 1096 or 561 or 1210 or 563 or 1460 or 1461 or 14 or 1766 or 1800 or 1801 or 1802,
                "全部默认活动登录后只接收签到、真实日补给和冻结战令初始化：" + packet.Opcode);
            if (packet.Opcode == 1460) Bytes(packet.Body, peer.RaidAccountMaterialLimits, "谋略战日补给保持账号攻坚额度");
            if (packet.Opcode == 1461) Bytes(packet.Body, peer.RaidCharacterMaterialLimits, "谋略战日补给保持角色攻坚额度");
            if (packet.Opcode == 1096)
            {
                byte[] expected = new byte[459]; expected[0x1c4] = 1;
                Bytes(packet.Body, expected, "全部活动登录时周签到完整第一周零进度");
            }
            if (packet.Opcode == 561)
                That(packet.Body.Length == 17 && U32(packet.Body) <= 10 && packet.Body.AsSpan(4).IndexOfAnyExcept((byte)0) == -1,
                    "全部活动首次在线累计通知保留真实短时段及零领取位");
            packets.Add(packet);
        }
        byte[] wallet = packets.Single(row => row.Opcode == 563).Body;
        Bytes(wallet, Build(writer => { writer.Write((byte)1); writer.Write((byte)1); writer.Write(490013685); writer.Write(100); }),
            "全活动首次登录谋略战100币只发一次，真实563余额完整");
        byte[] materials = packets.Single(row => row.Opcode == 14 && row.Body is [45, ..]).Body;
        That(materials.Length == 126 && U16(materials, 1) == 1 && U16(materials, 3) != 0 &&
            U32(materials, 5) == 490013685 && U32(materials, 9) == 100,
            "全活动首次登录同时刷新活动道具背包100币且避开0槽星河实体材料");
        byte[] mock = packets.Single(row => row.Opcode == 1766).Body;
        That(mock.Length == 980 && U32(mock) == 100 && U32(mock, 4) == 30, "全部默认活动共存时谋略战100币及30体力完整");
        That(packets.Count(row => row.Opcode == 1210 && row.Body.Length == 37 && U32(row.Body) == 2370) == 1 &&
            new ushort[] { 1800, 1801, 1802 }.All(id => packets.Count(row => row.Opcode == id) == 1),
            "签到状态和完整战令三包与谋略战首次日补给共存");
        That(fixture.Query("SELECT balance FROM account_material_balances WHERE account=$p0 AND item_id=490013685", fixture.Options.DevelopmentAccount)
            is [[100L]], "全部默认活动登录后真实材料账本只增加当日100币");
        using var switched = Open(fixture);
        switched.BeforeMileageInitialization = peer.BeforeMileageInitialization;
        switched.ValidateNativePromotionEvents = peer.ValidateNativePromotionEvents;
        switched.Select(1);
        That(switched.AccountMaterials.Count == 1 && switched.AccountMaterials.GetValueOrDefault(490013685) == 100,
            "同账号换角按新的已提交快照精确初始化100币，不复制角色实体或重复日补给");
        byte[] switchedCommon = switched.InitialAccountMaterialLists.Single(row => row.Body is [45, ..]).Body;
        That(switchedCommon.Length == 249 && U16(switchedCommon, 1) == 2 &&
            U16(switchedCommon, 3) == 0 && U32(switchedCommon, 5) == 490013716 && U32(switchedCommon, 9) == 150 &&
            U16(switchedCommon, 3 + 123) == U16(materials, 3) && U32(switchedCommon, 5 + 123) == 490013685 && U32(switchedCommon, 9 + 123) == 100,
            "同账号换角后list45同时保留星河490013716的150数量与谋略战100币，切换角色不丢失星河材料显示");
    }
}
