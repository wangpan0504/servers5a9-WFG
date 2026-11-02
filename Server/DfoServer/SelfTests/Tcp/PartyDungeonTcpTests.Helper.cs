using System.Buffers.Binary;
using System.Text;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    internal static async Task RunHelperHellAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "helper-hell"), resources: resources, gm: true);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _), "轮换TCP使用正常疲劳");
        const uint initial = 291100308;
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == initial));
        var selection = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value];
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(selection.HellQuests).ToHashSet();
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
        using var group = new Coop(fixture, resources, "helperhell", position, done.ToArray(), 95, 1, tickets: 90);
        var peer = group.Peers[0]; group.Join();
        Set(true, 'a');
        peer.Send(17, Chat($"#95-helper/1 {new string('b', 32)} {peer.Id + 1} hell 0"));
        That(!Drain(peer).Any(p => p.Opcode is 12 or 1709), "错误角色命令不改设置、不广播或伪造回执");
        group.Open(); peer.Send(16, Coop.Selection(initial, 0, 4, true)); group.ReadEntry(group.All()); group.Load();
        That(Tickets() == 60, "首次正常入场只扣一份30张门票");
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var maze = catalog.Get((int)initial).Mazes.Single(row => row.SourceOrdinal == group.Information[7]);
        var path = QuestStoryRouteTests.Path(maze.StaticTopology, new(group.Map[0], group.Map[1]), maze.HellParty!.Room);
        That(path.Length > 1, "首次仍须正常走到柱子房");
        foreach (var next in path.Skip(1)) { group.Clear(); group.Move((byte)next.X, (byte)next.Y); group.Load(); }
        var target = path[^2];
        string before = fixture.Snapshot();
        var blocked = group.Move((byte)target.X, (byte)target.Y, expect: false)[0];
        That(blocked.Any(p => p is { Kind: 1, Opcode: 45, Body: [0, 19, 0] }) && !blocked.Any(p => p.Opcode is 27 or 28 or 29) &&
            fixture.Snapshot() == before, "未清波次走门拒绝，保持旧图、门票及疲劳");
        uint previous = initial;
        for (int rotation = 0; rotation < 2; rotation++)
        {
            group.Clear();
            var oldKeys = Enumerable.Range(0, group.Map[18]).Select(i => U16(group.Map, 19 + i * 22 + 6)).ToHashSet();
            long fatigue = Fatigue();
            var rows = group.Move((byte)target.X, (byte)target.Y, expect: false);
            var packets = rows[0];
            int ack = Array.FindIndex(packets, p => p is { Kind: 1, Opcode: 45, Body: [1] });
            int scene = Array.FindIndex(packets, p => p is { Kind: 0, Opcode: 27 });
            int info = Array.FindIndex(packets, p => p is { Kind: 0, Opcode: 28 });
            int start = Array.FindIndex(packets, p => p is { Kind: 0, Opcode: 29 });
            That(ack >= 0 && scene > ack && info > scene && start > info &&
                packets.Count(p => p is { Kind: 0, Opcode: 29 }) == 1, "真实45依次确认、重建27场景、28地下城、29首图，只有一次加载");
            group.ReadEntry(rows);
            uint nextDungeon = U32(group.Information);
            var nextMaze = catalog.Get((int)nextDungeon).Mazes.Single(row => row.SourceOrdinal == group.Information[7]);
            That(nextDungeon != previous && selection.Options.Any(row => row.DungeonId == nextDungeon) &&
                nextMaze.HellParty is { } hell && group.Map[0] == hell.Room.X && group.Map[1] == hell.Room.Y &&
                U32(group.Map, 14) == hell.MapId && group.Information[4] == 4 && group.Map[7] == 1,
                "目标为同地区另一真实柱子图，固定首次难度且直接加载柱子房");
            That(Enumerable.Range(0, group.Map[18]).All(i => !oldKeys.Contains(U16(group.Map, 19 + i * 22 + 6))) &&
                Tickets() == 30 - rotation * 30 && Fatigue() == fatigue, "新实例演员key独立，入场扣一次票，加载完成前不扣房间疲劳");
            group.Load();
            That(Fatigue() == fatigue - 1, "完整加载37后沿普通链只扣一次房间疲劳");
            peer.Send(37, new byte[8]); Drain(peer);
            That(Fatigue() == fatigue - 1, "重复加载不重复扣疲劳或发奖");
            previous = nextDungeon;
            var pillar = nextMaze.HellParty!.Room;
            target = (nextMaze.StaticTopology.Links ?? []).Select(link => link.First == pillar ? (DungeonPoint?)link.Second :
                link.Second == pillar ? link.First : null).First(point => point is not null)!.Value;
        }
        group.Clear(); before = fixture.Snapshot();
        var exhausted = group.Move((byte)target.X, (byte)target.Y, expect: false)[0];
        That(exhausted.Any(p => p is { Kind: 1, Opcode: 45, Body: [0, 19, 0] }) &&
            !exhausted.Any(p => p.Opcode is 27 or 28 or 29) && fixture.Snapshot() == before,
            "票不足留在当前房间，不加载、不额外扣费、不降级成普通过门");
        Set(false, 'c');
        var ordinary = group.Move((byte)target.X, (byte)target.Y, expect: false)[0];
        That(ordinary.Any(p => p is { Kind: 1, Opcode: 45, Body: [1] }) && ordinary.Any(p => p is { Kind: 0, Opcode: 29 }) &&
            !ordinary.Any(p => p.Opcode is 27 or 28), "明确关闭后恢复普通过门，无总暂停恢复模式");

        long Tickets() => Convert.ToInt64(fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=3330", peer.Id)[0][0]);
        long Fatigue() => Convert.ToInt64(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", peer.Id)[0][0]);
        void Set(bool enabled, char nonceCharacter)
        {
            string nonce = new(nonceCharacter, 32);
            peer.Send(17, Chat($"#95-helper/1 {nonce} {peer.Id} hell {(enabled ? 1 : 0)}"));
            var replies = Drain(peer);
            That(!replies.Any(p => p.Opcode == 12) && replies.Count(p => p is { Kind: 0, Opcode: 1709 }) == 1,
                "机器命令只回当前连接，不进入普通聊天或广播");
            var body = replies.Single(p => p.Opcode == 1709).Body;
            int titleLength = checked((int)U32(body));
            int textLength = checked((int)U32(body, 4 + titleLength));
            string text = Encoding.UTF8.GetString(body, 8 + titleLength, textLength);
            var fields = text.Split('|');
            That(Encoding.UTF8.GetString(body, 4, titleLength) == "95_HELPER/1" && fields.Length == 6 &&
                fields[0] == "1" && fields[1] == nonce && fields[2] == peer.Id.ToString() && long.Parse(fields[3]) > 0 &&
                fields[4] == "ok" && fields[5] == (enabled ? "1" : "0"), "真实聊天Handler返回匹配nonce、角色、epoch和明确开关");
        }
        static byte[] Chat(string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text); byte[] body = new byte[11 + bytes.Length]; body[0] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), (uint)bytes.Length); bytes.CopyTo(body, 11); return body;
        }
    }
}
