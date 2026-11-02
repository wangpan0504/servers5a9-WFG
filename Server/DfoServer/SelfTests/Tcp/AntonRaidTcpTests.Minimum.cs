using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidTcpTests
{
    public static async Task RunMinimumAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-start-minimum"), resources: resources);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "raid-start.json"), """{"StartMinimumOverride":1}""");
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire("antonminimum", () => { }))
        {
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("AntSolo", resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
            var created = repository.Create(owner, candidate with
                { Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [12829, 3779], InitialLearning = learning });
            var items = resources.ItemResources;
            InventoryService.Commit(repository, owner, created,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(created, [new(10096296, 2)]));
        }
        using var peer = Open(fixture, "antonminimum", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        peer.Select(); Drain(peer);
        byte[] Search(string name) => Build(w => { w.Write((byte)0); w.Write(0u); w.Write(0x100u); w.Write(0u); w.Write(Name(name)); });
        peer.Send(833, Search("")); Bytes(Until(peer, 0, 756).Body, new byte[12], "寻找攻坚队返回真实空招募列表并释放等待");
        peer.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Solo")); w.Write(new byte[] { 0, 0, 0, 7 }); }));
        Until(peer, 0, 590); Until(peer, 0, 591); Until(peer, 1, 664);
        peer.Send(833, Search("Solo"));
        var found = Until(peer, 0, 756).Body;
        int at = 12; uint key = U32(found, at); ReadRecord(found, ref at, key);
        That(U32(found) == 1 && U32(found, 4) == 0 && U32(found, 8) == 1 && found.Length == at + 2 && found[at] == 1 && found[at + 1] == 0,
            "寻找攻坚队按标题列出同频道实际单人攻坚队，完整消费记录与逐成员附加状态");
        peer.Send(833, Search("absent")); Bytes(Until(peer, 0, 756).Body, new byte[12], "标题未命中返回真实空列表");
        peer.Send(666); Bytes(Until(peer, 1, 666).Body, [0, 19, 0], "单人覆盖仍要求实际编队");
        peer.Send(669, Build(w => { w.Write(0u); w.Write((uint)peer.Id); w.Write(1u); }));
        Bytes(Until(peer, 1, 669).Body, [1], "单人编入真实第一分队"); Drain(peer);
        peer.Send(666);
        var packets = new List<WirePacket>();
        for (int n = 0; n < 64 && !packets.Any(p => p is { Kind: 1, Opcode: 666 }); n++) packets.Add(peer.Read());
        Bytes(packets.Single(p => p is { Kind: 1, Opcode: 666 }).Body, [1], "配置覆盖从正常Runtime与666完成单人开战");
        Bytes(packets.Single(p => p.Opcode == 587).Body, [2, 0], "单人收到真实阶段通知");
        VerifyMap(packets.Single(p => p.Opcode == 585).Body);
        var symbols = packets.Single(p => p.Opcode == 583).Body;
        That(symbols.Length == 1 + symbols[0] * 8, "开战583长度与实际条目数严格对应");
        // 初始非事件全集：领域先将符号归零，再写入首阶段七图原始HP；影片事件不属于状态快照。
        (int Id, int Value)[] expectedSymbols =
        [
            (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0),
            (7, 0), (8, 0), (9, 0), (10, 0), (11, 0), (12, 0),
            (50, 4), (51, 1), (52, 1), (53, 2), (54, 1), (55, 2), (56, 1),
            (57, 0), (58, 0), (59, 0), (60, 0), (61, 0), (62, 0), (63, 0),
            (103, 0), (104, 0), (105, 0), (106, 0), (110, 0), (120, 0), (126, 0), (127, 0)
        ];
        var actualSymbols = Enumerable.Range(0, symbols[0]).Select(i =>
            (Id: System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(symbols.AsSpan(1 + 8 * i)),
             Value: System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(symbols.AsSpan(5 + 8 * i)))).ToArray();
        That(actualSymbols.SequenceEqual(expectedSymbols) &&
            actualSymbols.All(row => row.Id is not (102 or >= 121 and <= 125 or 128)),
            "开战583逐项保留完整非事件ID、顺序和初值：感染位7=0、首图HP50=4，排除102与六个零影片事件：" +
            string.Join(',', actualSymbols.Select(row => $"{row.Id}={row.Value}")));
        That(packets.Any(p => p.Opcode == 9) && packets.Any(p => p.Opcode == 594) && packets.Any(p => p.Opcode == 24),
            "单人完整分队、准备和迁移链");
        VerifyCosts(packets.Single(p => p.Opcode == 598).Body, [peer.Id], 2, 1);
        That(fixture.Query("SELECT count(*) FROM characters WHERE town=19 AND area=2") is [[1L]] &&
            fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[1L]],
            "单人持久扣一张门票并落候战室");
        string saved = fixture.Snapshot(), inventory = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2");
        // 594的单人消费者会再发普通12；必须与服务端已经建立的同分队幂等收敛。
        peer.Send(12, Build(w => { w.Write((byte)0); w.Write((byte)0); w.Write(Name("Party : 1")); w.Write((byte)4);
            w.Write(uint.MaxValue); w.Write((byte)5); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)7); }));
        Drain(peer);
        peer.Send(666); Bytes(Until(peer, 1, 666).Body, [0, 19, 0], "单人战斗中重复开战明确拒绝"); Drain(peer);
        That(fixture.Snapshot() == saved && fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") == inventory,
            "普通12回应与重复666不重扣、不迁移或重建资产");
        for (int round = 0; round < 2; round++)
        {
            peer.Send(15, new byte[4]);
            var selected = new List<WirePacket>();
            for (int n = 0; n < 64 && !selected.Any(p => p is { Kind: 0, Opcode: 27 }); n++) selected.Add(peer.Read());
            // 开团已逐项验证十四图；同实例无状态变化的选图仅发送空增量。
            Bytes(selected.Single(p => p.Opcode == 585).Body, [0, 0, 0, 0, 0, 0],
                "重复选图585严格保留空变化列表与感染索引0，沿用已验证的开团地图状态");
            Bytes(selected.Single(p => p is { Kind: 1, Opcode: 15 }).Body, [1], "候战室正常打开选图");
            Bytes(selected.Single(p => p.Opcode == 27).Body, new byte[42], "27保留场景合同，地下城状态由585单独提供");
            That(selected.FindIndex(p => p.Opcode == 585) < selected.FindIndex(p => p.Opcode == 27) &&
                selected.All(p => p.Opcode is not (587 or 28 or 29)), "重复打开只重同步地图，不重播开战或伪造入场");
            peer.Send(16, Convert.FromHexString("D20000000200000000FFFF0000000000000000000000000000"));
            Bytes(Until(peer, 1, 16).Body, [0, 9, 0, 0], "缺省关闭入口沿已证ACK16拒绝");
            Drain(peer);
            That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[0L]],
                "默认关闭不创建Run或尝试号，仍可原样132返回");
            peer.Send(132);
            Bytes(peer.Expect(1, 132).Body, [1], "选图返回保持原ACK132");
            peer.Expect(0, 3); peer.Expect(0, 23); peer.Expect(0, 24); Drain(peer);
        }
        That(fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") == inventory &&
            fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'anton_raid_start:%'") is [[1L]],
            "选图重同步与返回不重复扣票或改变资产");
    }
}
