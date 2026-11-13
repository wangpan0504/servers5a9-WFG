using DfoServer.Development.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    public static async Task RunGuildPartyBuffAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "guild-party-buff"), resources: resources);
        await fixture.StartAsync();
        using var first = Open(fixture, "guildbuffa"); using var second = Open(fixture, "guildbuffb");
        first.Create("GuildBuffA"); second.Create("GuildBuffB");
        var entrance = resources.Towns.Areas.First(row => row.Value.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 8500));
        var map = entrance.Value.Map(new HashSet<int>());
        var bounds = map.DungeonGates.Concat(map.Bounds).First(row => row.Conditions.Length == 0 && row.Quests.Length == 0).Bounds;
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(71,'组队增益','guild-buff',$p0,1000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(71,$p0,1,1000),(71,$p1,4,1000);
            UPDATE characters SET level=95,experience=$p6,accepted_story_level=95,town=$p2,area=$p3,x=$p4,y=$p5 WHERE id IN($p0,$p1);
            """, first.Id, second.Id, entrance.Key.Item1, entrance.Key.Item2, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
            checked((long)SelfTestCatalogs.Growth(resources.Archive, resources.Learning).GetLevelStartExperience(95)));
        foreach (var peer in new[] { first, second }) { peer.Select(prepared: true, resumeTown: true, room: false); Drain(peer); }
        WirePacket[] ReadSettlement(TcpPeer peer)
        {
            peer.SendBarrier();
            var rows = new List<WirePacket>();
            for (int i = 0; i < 200; i++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "公会结算TCP屏障"); return rows.ToArray(); }
                That(packet.Kind == 0 || packet.Opcode is 46 or 140 or 141 or 744 or 905 or 1002 or 1454,
                    $"公会通关结算只接受结算、管理名册、账号公会索引及恶魔卡状态，实际{packet.Kind}/{packet.Opcode}");
                rows.Add(packet);
            }
            throw new InvalidOperationException("FAIL guild settlement packet budget exceeded");
        }
        WirePacket[][] All(TcpPeer? requester = null, bool settling = false)
        {
            requester ??= first;
            var target = ReferenceEquals(requester, first) ? second : first;
            var requested = settling ? ReadSettlement(requester) : Drain(requester);
            var other = settling ? ReadSettlement(target) : Drain(target);
            return ReferenceEquals(requester, first) ? [requested, other] : [other, requested];
        }
        for (int i = 0; i < 2; i++)
        {
            var peer = i == 0 ? first : second;
            peer.Send(2, Build(w => { w.Write((byte)5); w.Write(0x0100007fu); w.Write(0x0100007fu); w.Write((ushort)(30200 + i)); w.Write(512u); }));
            Drain(peer);
        }
        first.Send(10, Build(w => { w.Write(second.Id); w.Write((byte)0); w.Write(0u); w.Write(new byte[6]); })); All();
        second.Send(11, Build(w => { w.Write(first.Id); w.Write((byte)0); w.Write(0u); }));
        That(All(second).All(rows => rows.Any(p => p is { Kind: 0, Opcode: 9 })), "公会增益通过真实邀请组成两人队伍");
        first.Send(15, new byte[4]);
        That(All().All(rows => rows.Any(p => p is { Kind: 0, Opcode: 27 })), "公会正式入口为两人打开选图");
        That(SelfTestCatalogs.Dungeons(resources.Archive).Get(8500).TryGetDesignatedDifficulty(out byte difficulty),
            "公会测试从正式副本资源读取指定难度，不沿普通图默认0");
        first.Send(16, Coop.Selection(8500, 0, difficulty));
        var entered = All();
        That(entered.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 29 })), "真实8500入场冻结双方原会籍");
        byte[] information = entered[0].Single(p => p is { Kind: 0, Opcode: 28 }).Body;
        byte[] currentMap = entered[0].Single(p => p is { Kind: 0, Opcode: 29 }).Body;
        first.Send(37, new byte[8]);
        That(All().All(rows => rows.All(p => p.Opcode != 487)), "首人加载不能提前安装组队增益");
        second.Send(37, new byte[8]);
        var loaded = All(second);
        That(loaded.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 30 }) &&
            rows.Count(p => p is { Kind: 0, Opcode: 487 } && Convert.ToHexString(p.Body) == "0119010000000000000000000000000000") == 1),
            "加载屏障释放后双方各收到一份281场次效果，17字节独立金样，无伪造物品或限时");
        second.Send(37, new byte[8]);
        That(All(second).All(rows => rows.All(p => p.Opcode != 487)), "重报加载不重复安装增益");
        void ClearMap()
        {
            foreach (int at in Enumerable.Range(0, currentMap[18]).Select(i => 19 + 22 * i)
                .Where(at => currentMap[at + 21] == 0 && currentMap[at + 13] != 9)
                .OrderBy(at => currentMap[at + 13] == 3))
            {
                ushort key = U16(currentMap, at + 6);
                first.Send(39, FormalDungeonTcpTests.Death(key, first.Id, currentMap[at + 12]));
                That(All().All(rows => rows.Any(p => p is { Kind: 0, Opcode: 38 } && U16(p.Body) == key)),
                    "公会净化回归沿真实死亡请求清理房间");
            }
        }
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(8500).Mazes.Single(m => m.SourceOrdinal == information[7]);
        var path = QuestStoryRouteTests.Path(maze.StaticTopology, new(currentMap[0], currentMap[1]), new(information[8], information[9]));
        foreach (var next in path.Skip(1))
        {
            ClearMap();
            var move = new byte[100]; move[0] = (byte)next.X; move[1] = (byte)next.Y;
            first.Send(45, move);
            currentMap = All()[0].Single(p => p is { Kind: 0, Opcode: 29 }).Body;
            first.Send(37, new byte[8]); All();
            second.Send(37, new byte[8]); All(second);
        }
        ClearMap();
        byte[] report = DungeonSettlementProtocolTests.Report([(first.Id, (byte)99), (second.Id, (byte)99)]);
        first.Send(46, report);
        var committed = All(settling: true);
        That(committed[0].Any(p => p is { Kind: 0, Opcode: 34 }) && committed.All(rows =>
            rows.Any(p => p is { Kind: 0, Opcode: 744 } && U32(p.Body, 17) == 8500 && U32(p.Body, 21) == 1)),
            "8500真实击杀及结算后本人和同会队友均收到一次净化进度，不必重登");
        second.Send(46, report); All(second, settling: true);
        first.Send(46, report); All(settling: true);
        That(fixture.Query("SELECT clear_count FROM guild_dungeon_progress WHERE guild_id=71 AND dungeon_id=8500") is [[1L]] &&
            fixture.Query("SELECT count(*) FROM guild_dungeon_clear_receipts WHERE guild_id=71") is [[1L]],
            "同会队友结算及重复报告不重复增加净化，真实回执与进度一致");
        second.Send(13);
        var left = All(second);
        That(left.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 488 } &&
                Convert.ToHexString(p.Body) == "03190100001A0100001B010000")) &&
            left[0].All(p => p.Opcode != 487) &&
            left[1].Count(p => p.Opcode == 487) == 1 &&
            left[1].Any(p => p is { Kind: 0, Opcode: 487 } &&
                Convert.ToHexString(p.Body) == "01FC040000000000000000000000000000"),
            "真实队员离场清除全部场次档位；幸存者不重发增益，本人返城仅恢复永久守护石1276");
    }
}
