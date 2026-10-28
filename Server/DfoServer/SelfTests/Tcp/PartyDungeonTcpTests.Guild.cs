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
            UPDATE characters SET level=95,accepted_story_level=95,town=$p2,area=$p3,x=$p4,y=$p5 WHERE id IN($p0,$p1);
            """, first.Id, second.Id, entrance.Key.Item1, entrance.Key.Item2, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        foreach (var peer in new[] { first, second }) { peer.Select(prepared: true, resumeTown: true, room: false); Drain(peer); }
        WirePacket[][] All(TcpPeer? requester = null)
        {
            requester ??= first;
            var requested = Drain(requester); var other = Drain(ReferenceEquals(requester, first) ? second : first);
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
        That(All().All(rows => rows.Any(p => p is { Kind: 0, Opcode: 29 })), "真实8500入场冻结双方原会籍");
        first.Send(37, new byte[8]);
        That(All().All(rows => rows.All(p => p.Opcode != 487)), "首人加载不能提前安装组队增益");
        second.Send(37, new byte[8]);
        var loaded = All(second);
        That(loaded.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 30 }) &&
            rows.Count(p => p is { Kind: 0, Opcode: 487 } && Convert.ToHexString(p.Body) == "0119010000000000000000000000000000") == 1),
            "加载屏障释放后双方各收到一份281场次效果，17字节独立金样，无伪造物品或限时");
        second.Send(37, new byte[8]);
        That(All(second).All(rows => rows.All(p => p.Opcode != 487)), "重报加载不重复安装增益");
        second.Send(13);
        var left = All(second);
        That(left.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 488 } &&
                Convert.ToHexString(p.Body) == "03190100001A0100001B010000")) &&
            left.All(rows => rows.All(p => p.Opcode != 487)),
            "真实队员离场及本人返城均清除全部公会场次档位，幸存者不保留两人增益");
    }
}
