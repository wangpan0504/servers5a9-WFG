using System.Buffers.Binary;
using System.Security.Cryptography;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Development.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class FormalDungeonTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var growth = CharacterGrowthCatalog.Load(resources.Archive);
        var kills = DungeonKillExperienceCatalog.Load(resources.Archive);
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled }, Path.Combine(root, "formal-dungeon"));
        await fixture.StartAsync();
        byte[]? oldReport = null;
        ulong experience = 0;
        ushort nextKey;
        int picked = 0;
        using (var peer = Open(fixture))
        {
            peer.Create("FormalTcp");
            peer.Select();
            EntryTown(peer);
            var map = Enter(peer);
            var target = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
            int[] ids = [76121, 76122, 76123, 76124, 76125, 76126];
            for (int room = 0; room < ids.Length; room++)
            {
                That(U32(map, 14) == ids[room], "正式TCP依匹配3145六房顺序加载");
                Load(peer);
                string beforeMovement = fixture.Snapshot();
                peer.Send(35, Move(1000, 249));
                peer.Barrier();
                That(fixture.Snapshot() == beforeMovement, "正式副本阻止城镇位置保存");
                if (oldReport is not null)
                {
                    peer.Send(39, oldReport);
                    peer.Barrier();
                    That(Experience(fixture, peer.Id) == experience, "新房旧key没有经验或死亡通知");
                }
                int count = map[18];
                for (int index = 0; index < count; index++)
                {
                    int offset = 19 + index * 22;
                    ushort key = U16(map, offset + 6);
                    byte level = map[offset + 12];
                    byte[] report = Death(key, peer.Id, level);
                    if (oldReport is null)
                    {
                        byte[] altered = (byte[])report.Clone();
                        altered[84] ^= 1;
                        peer.Send(39, altered);
                        peer.Barrier();
                        That(Experience(fixture, peer.Id) == 0, "不匹配正文检查值不能发奖");
                        var insufficient = Death(key, peer.Id, level, 503);
                        peer.Send(39, insufficient);
                        peer.Barrier();
                        That(Experience(fixture, peer.Id) == 0, "初始HP未被累计降低量覆盖不能发奖");
                    }
                    peer.Send(39, report);
                    byte[] deathNotice = peer.Expect(0, 38).Body;
                    int dropCount = U16(deathNotice, 2);
                    That(U16(deathNotice, 0) == key && dropCount is 0 or 1 && deathNotice.Length == 9 + 131 * dropCount &&
                        deathNotice.AsSpan(deathNotice.Length - 5).SequenceEqual(new byte[] { 0, 0, 255, 0, 0 }),
                        "正式死亡投影保留完整可变掉落行及尾部，不将随机零掉落写死");
                    byte[] notice = peer.Expect(0, 37).Body;
                    experience += kills.GetExperience(level);
                    byte expectedLevel = 1;
                    while (experience >= growth.GetCumulativeExperienceBoundary(expectedLevel)) expectedLevel++;
                    That(notice.Length == 91 && notice[0] == expectedLevel && U64(notice, 1) == experience &&
                        U32(notice, 9) == kills.GetExperience(level) && U16(notice, 13) == growth.GetSpDelta(1, expectedLevel) &&
                        U16(notice, 15) == growth.GetSpDelta(1, expectedLevel), "完整NOTI37绝对经验等级及两树SP来自已提交角色");
                    if (dropCount != 0)
                    {
                        uint ground = U32(deathNotice, 4);
                        int itemId = checked((int)U32(deathNotice, 10));
                        That(itemId is 1000 or 1047 or 1004 && U32(deathNotice, 14) == 1,
                            "本服正式随机仅生成获授权的一件普通物品");
                        byte[] getItem = new byte[20];
                        BinaryPrimitives.WriteUInt32LittleEndian(getItem, ground);
                        peer.Send(43, getItem);
                        byte[] got = peer.Expect(0, 39).Body;
                        That(got.Length == 19 && U32(got, 0) == ground && U16(got, 4) == peer.Id && U16(got, 14) == peer.Id,
                            "实际普通20B拾取获得完整19B NOTI39，不追加空42");
                        ushort slot = U16(got, 16);
                        peer.Expect(0, 14);
                        peer.Expect(1, 43, [1]);
                        That(fixture.Query("SELECT destination_slot,item_uid FROM character_dungeon_pickup_receipts WHERE character_id=$p0 AND ground_key=$p1", peer.Id, ground)
                            is [{ Length: 2 } row] && Convert.ToUInt16(row[0]) == slot && Convert.ToInt64(row[1]) > 0,
                            "拾取通知使用同事务实际UID与目的槽");
                        string assets = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
                        peer.Send(43, getItem);
                        peer.Expect(1, 43, [0, 22, 0]);
                        peer.Barrier();
                        That(fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == assets,
                            "重复拾取明确拒绝并恢复等待，库存不再增加");
                        picked++;
                    }
                    peer.Send(39, report);
                    peer.Barrier();
                    That(Experience(fixture, peer.Id) == experience, "精确重复不通知或重复发奖");
                    oldReport = report;
                }
                if (room < target.Length)
                {
                    byte[] move = new byte[100];
                    move[0] = target[room].X;
                    move[1] = target[room].Y;
                    peer.Send(45, move);
                    peer.Expect(1, 45, [1]);
                    map = peer.Expect(0, 29).Body;
                }
            }
            That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id) is [[29L]],
                "实际默认服务六房共有29条唯一成长事实");
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0, 24);
            peer.Barrier();
            nextKey = checked((ushort)Convert.ToInt64(fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]));
        }
        string persisted = fixture.Snapshot("SELECT level,experience FROM characters");
        string inventory = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
        string points = fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context");
        await fixture.StopAsync();
        await fixture.StartAsync("actual-restart");
        That(fixture.Snapshot("SELECT level,experience FROM characters") == persisted &&
            fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context") == points &&
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == inventory,
            "真实停服重启保留经验等级及两树SP");
        using (var peer = Open(fixture))
        {
            peer.Select();
            EntryTown(peer);
            var map = Enter(peer);
            That(U16(map, 25) >= nextKey, "重登及服务重启不复用旧实例怪key");
            Load(peer);
            peer.Send(39, oldReport!);
            peer.Barrier();
            That(Experience(fixture, peer.Id) == experience, "新实例拒绝上轮死亡请求，不增加资产经验");
        }
        await fixture.StopAsync();
        Console.WriteLine($"PASS 正式TCP原生统计合成击杀、六房升级、真实随机拾取{picked}件、重复拒绝及服务重启（非Windows实机；零样本不作拾取覆盖）");
    }

    static ulong Experience(TcpTestFixture fixture, ushort id) => checked((ulong)Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", id)[0][0]));
    static ulong U64(byte[] body, int at) => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at));
    static void EntryTown(TcpPeer peer)
    {
        peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
        peer.Travel(38, 2, 1085, 249, 38, 0, kind: 5);
    }
    static byte[] Enter(TcpPeer peer)
    {
        peer.Send(15, new byte[4]);
        peer.Expect(1, 15, [1]);
        peer.Expect(0, 27, new byte[42]);
        peer.Send(16, Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000"));
        That(peer.Expect(0, 28).Body.Length == 36, "正式NOTI28完整36B");
        return peer.Expect(0, 29).Body;
    }
    static void Load(TcpPeer peer)
    {
        peer.Send(37, new byte[8]);
        peer.Expect(1, 37, [1]);
        peer.Expect(0, 30, new byte[5]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
    }
    // 明确合成的网络统计。独立正文编码/MD5折叠，不调用服务Parser/Builder自证。
    static byte[] Death(ushort key, ushort reporter, byte level, ulong decrease = 600)
    {
        byte[] bytes = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), decrease + 50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26), 1);
        bytes[30] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(31), 0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(33), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(35), decrease);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(43), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), level);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(60), 504);
        byte[] digest = MD5.HashData(bytes.AsSpan(0, 84));
        bytes[84] = (byte)(digest[0] ^ digest[13] ^ digest[8] ^ digest[10] ^ 0x81);
        bytes[85] = (byte)(digest[1] ^ digest[9] ^ digest[5] ^ digest[12] ^ 0x78);
        bytes[86] = (byte)(digest[2] ^ digest[6] ^ digest[14] ^ digest[4] ^ 0x1A);
        bytes[87] = (byte)(digest[3] ^ digest[15] ^ digest[11] ^ digest[7] ^ 0xBF);
        return bytes;
    }
}
