using System.Text.Json;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class TownProtocolTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var towns = resources.Towns;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "town"), resources: resources);
        await fixture.StartAsync();
        ChannelTcpTests.Run(fixture);
        using (var peer = Open(fixture))
        {
            foreach (ushort opcode in new ushort[]
            {
                1723,
                1748,
                65535
            })
                peer.Send(opcode);
            peer.Create("DoorTest", variant: 1);
            peer.Select();
            DfoServer.WorldMapHoverTests.AssertPermissionBody(peer.DungeonPermission, towns);
            peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
            peer.Travel(38, 1, 873, 253, 38, 0, returnTown: 38);
            peer.Travel(38, 0, 645, 251, 38, 1, kind: 6);
            GateReturn(fixture, peer);
            string before = fixture.Snapshot();
            for (int i = 0; i < 2; i++)
            {
                peer.Send(7);
                peer.Expect(1, 7, [1]);
                byte[] returnedRoster = peer.Expect(0, 2).Body;
                AssetWireResult returnedEquipment = AssetWireChecks.ActorEntries(returnedRoster, 36 + NameWidth("DoorTest"));
                That(returnedEquipment.Entries.Length == 5, "return selection pushes all five equipment entries");
                CheckAssetRows(fixture, 0, returnedEquipment, false);
                if (i == 0)
                {
                    peer.Send(8, [255, 255, 2]);
                    peer.Send(692, Name("DoorTest"));
                    peer.Expect(1, 692, [0, 24, 0]);
                }
            }

            peer.Send(35, Move(461, 234));
            peer.Send(692, Name("DoorTest"));
            peer.Expect(1, 692, [0, 24, 0]);
            That(fixture.Snapshot() == before, "return selection and unselected movement preserve all persisted fields");
            peer.Select();
        }

        foreach (byte variant in new byte[]
        {
            6,
            1
        })
        {
            using var peer = Open(fixture);
            That(peer.Roster[1] == 1, "roster after failure");
            peer.Send(5, CreateBody("DoorTest", variant: variant));
            peer.Closed();
            That(fixture.Query("SELECT name FROM characters") is [["DoorTest"]], "bad tail and duplicate do not partially create");
        }

        using (var peer = Open(fixture))
        {
            string before = fixture.Snapshot();
            for (int i = 0; i < 2; i++)
            {
                peer.Send(692, Name("DoorTest"));
                peer.Expect(1, 692, [0, 24, 0]);
            }
            peer.Send(692, Name("AfterFailure"));
            peer.Expect(1, 692, [1]);
            That(fixture.Snapshot() == before, "repeated duplicate and available name checks preserve the database");
        }

        foreach (byte mode in new byte[]
        {
            0,
            1
        })
        {
            using var peer = Open(fixture);
            peer.Select();
            peer.Send(3, [mode]);
            peer.Expect(1, 3, [1]);
            peer.Closed();
        }

        (ushort Opcode, byte[] Body)[] invalid = [(7, [0]), (3, []), (3, [2]), (3, [0, 0]), (4, [0]), (1426, [0])];
        foreach (var request in invalid)
        {
            using var peer = Open(fixture);
            peer.Send(request.Opcode, request.Body);
            peer.Closed();
        }

        using (var peer = Open(fixture))
        {
            peer.Send(692, Name("DoorTest"));
            peer.Expect(1, 692, [0, 24, 0]);
            // GET_USERINFO/AGIT 的未知子型不回通用成功，后续名称检查作为处理屏障。
            peer.Send(8, [0]);
            peer.Send(1347, [2, 0, 0, 0]);
            peer.Send(692, Name("Other"));
            peer.Expect(1, 692, [1]);
        }

        DeleteCharacters(fixture);

        using (var waiting = Open(fixture))
        {
            await fixture.StopAsync();
            waiting.Closed();
        }

        var events = fixture.Events();
        var sent = events.Where(e => e.GetProperty("Kind").GetString() == "send").Select(e => e.GetProperty("Detail")).ToArray();
        That(sent.Any(p => p.GetProperty("Kind").GetInt32() == 1 && p.GetProperty("Opcode").GetInt32() == 3 && p.GetProperty("Name").GetString() == "EXIT"), "CMD3 log name");
        That(sent.Any(p => p.GetProperty("Kind").GetInt32() == 0 && p.GetProperty("Opcode").GetInt32() == 3 && p.GetProperty("Name").GetString() == "USER_STATE"), "NOTI3 log name");
        foreach (var (opcode, name) in new[]
        {
            (1723, "PING"),
            (1748, "END"),
            (65535, "65535")
        })
        {
            foreach (string kind in new[]
            {
                "recv",
                "unimplemented"
            })
                That(events.Any(e => e.GetProperty("Kind").GetString() == kind && e.GetProperty("Detail").GetProperty("Opcode").GetInt32() == opcode && e.GetProperty("Detail").GetProperty("Name").GetString() == name), "unimplemented name " + name);
            That(!sent.Any(p => p.GetProperty("Kind").GetInt32() == 1 && p.GetProperty("Opcode").GetInt32() == opcode), "unimplemented has no fabricated ACK");
        }

        await PreparedAsync(fixture, towns);
        Console.WriteLine("PASS C# TCP town: entry, exit, raw travel, quests, docks, portals, NPC, dungeon select notice and reconnect");
    }

    static void GateReturn(TcpTestFixture fixture, TcpPeer peer)
    {
        peer.Travel(38, 2, 1085, 249, 38, 0);
        string[] tables = fixture.Query("SELECT name FROM sqlite_schema WHERE type='table' AND name!='characters' ORDER BY name")
            .Select(row => (string)row[0]).ToArray();
        string assets = JsonSerializer.Serialize(tables.Select(table => fixture.Query($"SELECT * FROM \"{table}\" ORDER BY rowid")));
        string columns = string.Join(',', fixture.Query("PRAGMA table_info(characters)").Select(row => (string)row[1])
            .Where(name => name is not ("town" or "area" or "x" or "y" or "revision")).Select(name => '"' + name + '"'));
        string character = fixture.Snapshot($"SELECT {columns} FROM characters WHERE id=$p0", peer.Id);
        foreach (ushort x in new ushort[] { 90, 79 })
        {
            peer.Send(35, Move(x, 249));
            peer.Barrier();
            string before = fixture.Snapshot();
            long revision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0]);
            peer.Send(15, new byte[4]);
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            peer.Expect(0, 27, new byte[42]);
            peer.Send(15, new byte[4]);
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            peer.Expect(0, 27, new byte[42]);
            That(fixture.Snapshot() == before, "入口重复请求冻结返回点，不提前写库");
            peer.Send(132);
            peer.Expect(1, 132, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 23, Build(w => { w.Write(peer.Id); w.Write((byte)38); w.Write((byte)2); w.Write((ushort)89); w.Write((ushort)249); w.Write((byte)0); w.Write((byte)3); }));
            peer.Expect(0, 24, Build(w => { w.Write((byte)38); w.Write((byte)2); w.Write((ushort)1); w.Write(peer.Id); w.Write((ushort)89); w.Write((ushort)249); w.Write(new byte[] { 0, 3, 1 }); }));
            That(fixture.Snapshot("SELECT town,area,x,y,revision FROM characters WHERE id=$p0", peer.Id) == $"[[38,2,89,249,{revision + 1}]]",
                "边界内外返回提交安全落点，包体与落库一致");
            before = fixture.Snapshot();
            peer.Send(132);
            peer.Barrier();
            That(fixture.Snapshot() == before, "重复132无额外包和位置提交");
            That(fixture.Snapshot($"SELECT {columns} FROM characters WHERE id=$p0", peer.Id) == character &&
                JsonSerializer.Serialize(tables.Select(table => fixture.Query($"SELECT * FROM \"{table}\" ORDER BY rowid"))) == assets,
                "安全回城保持全部角色资产、任务、物品UID和其他数据库表");
            peer.Send(631, Move(100, 249, motion: true));
            peer.Barrier();
            That(fixture.Snapshot("SELECT x,y FROM characters WHERE id=$p0", peer.Id) == "[[100,249]]", "回城后移动恢复，可以走离入口");
        }
        peer.Travel(38, 0, 645, 251, 38, 2);
    }

    sealed record Trip(byte Town, byte Area, ushort X, ushort Y, ushort PreviousTown, ushort PreviousArea, bool Bottom = false, uint? ReturnTown = null, byte Kind = 0, uint Npc = 0);
    static string AtPosition(TcpTestFixture fixture, ushort id, Location target)
    {
        var rows = fixture.Query("SELECT * FROM characters ORDER BY id");
        var columns = fixture.Query("PRAGMA table_info(characters)").Select(row => (string)row[1]).ToArray();
        var row = rows.Single(row => Convert.ToUInt16(row[Array.IndexOf(columns, "id")]) == id);
        var fields = new (string Name, int Value)[] { ("town", target.Town), ("area", target.Area), ("x", target.X), ("y", target.Y) };
        if (fields.Any(field => Convert.ToInt32(row[Array.IndexOf(columns, field.Name)]) != field.Value))
            row[Array.IndexOf(columns, "revision")] = Convert.ToInt64(row[Array.IndexOf(columns, "revision")]) + 1;
        foreach (var field in fields)
            row[Array.IndexOf(columns, field.Name)] = field.Value;
        return JsonSerializer.Serialize(rows);
    }

    static async Task PreparedAsync(TcpTestFixture fixture, TownCatalog towns)
    {
        string original = fixture.Snapshot();
        string audit = Path.Combine(fixture.Root, "preparation");
        using var result = JsonDocument.Parse(await fixture.CliAsync(null, "--prepare-jianying", "剑影", audit));
        var prepared = result.RootElement;
        That(prepared.GetProperty("Name").GetString() == "剑影" && prepared.GetProperty("Level").GetInt32() == 95 && prepared.GetProperty("GrowType").GetInt32() == 0x25, "prepared CLI identity");
        string backup = Path.Combine(audit, "game-before.db");
        That(JsonSerializer.Serialize(TcpTestFixture.Rows(backup, "SELECT * FROM characters ORDER BY id")) == original, "preparation backup matches original");
        That(TcpTestFixture.Rows(backup, "PRAGMA integrity_check") is [["ok"]], "backup integrity");
        That(fixture.Snapshot("SELECT * FROM characters WHERE slot=0") == original, "preparation preserves existing role");
        ushort id = prepared.GetProperty("Id").GetUInt16(), slot = prepared.GetProperty("Slot").GetUInt16();
        var quests = fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0", id).Select(r => Convert.ToInt32(r[0])).ToHashSet();
        byte[] completed;
        await fixture.StartAsync("prepared");
        using (var peer = Open(fixture))
        {
            That(peer.Roster[1] == 2, "prepared roster count");
            int at = 15;
            foreach (var identity in new[]
            {
                (Slot: 0, Name: "DoorTest", Grow: 0, Level: 1),
                (Slot: 1, Name: "剑影", Grow: 0x25, Level: 95)
            })
            {
                int width = (int)U32(peer.Roster, at + 2);
                That(U16(peer.Roster, at) == identity.Slot && DecodeName(peer.Roster, at + 6, width) == identity.Name, "prepared roster name/slot");
                Bytes(peer.Roster.AsSpan(at + 8 + width, 3), [0, (byte)identity.Grow, (byte)identity.Level], "prepared roster identity");
                at += 70 + width + peer.Roster[at + 21 + width] * 25;
            }

            string beforeNameCheck = fixture.Snapshot();
            peer.Send(692, Name("剑影"));
            peer.Expect(1, 692, [0, 24, 0]);
            peer.Send(692, Name("备用角色"));
            peer.Expect(1, 692, [1]);
            That(fixture.Snapshot() == beforeNameCheck, "Chinese duplicate and available name checks preserve all character data");
            var packets = peer.Select(slot, prepared: true);
            Bytes(packets[1].Body, [95, 0, 0, 0], "accepted story level");
            That(DecodeName(packets[2].Body, 127, 4) == "剑影", "Chinese actor name");
            Bytes(packets[2].Body.AsSpan(131, 3), [0, 0x25, 95], "prepared actor identity");
            var player = packets[3].Body;
            That(player.Length == 257 && player[143] == 0 && player[144] == 15 && player[190] == 15, "prepared skill trees");
            completed = packets[6].Body;
            That(completed.Length == 30004 && U32(completed) == 30000 && quests.Count == 943 && completed.AsSpan(4).ToArray().Select((b, i) => (b, i)).Where(p => p.b != 0).Select(p => p.i).ToHashSet().SetEquals(quests), "complete quest bitmap");
            Bytes(packets[^2].Body, [38, 0, 0, 0], "initial return town");
            peer.Barrier();
            Trip[] trips = [new(38, 0, 622, 196, 38, 1, true), new(38, 3, 176, 154, 38, 0), new(39, 0, 330, 130, 38, 3), new(40, 0, 55, 270, 39, 0), new(40, 1, 1142, 124, 40, 0), new(54, 1, 201, 570, 40, 1), new(31, 1, 2508, 220, 54, 1), new(6, 3, 1668, 213, 31, 1), new(11, 1, 315, 215, 6, 3), new(17, 1, 1409, 346, 11, 1), new(22, 3, 77, 256, 17, 1), new(40, 1, 1672, 200, 22, 3), new(40, 0, 1519, 489, 40, 1), new(39, 0, 3547, 270, 40, 0), new(38, 1, 433, 311, 39, 0, ReturnTown: 39), new(39, 0, 596, 144, 38, 1, true), new(38, 1, 433, 311, 39, 0, ReturnTown: 39), new(39, 0, 596, 144, 38, 1), new(38, 3, 780, 362, 39, 0), new(38, 0, 1407, 360, 38, 3), new(38, 1, 873, 253, 38, 0, ReturnTown: 38), new(40, 1, 1782, 193, 38, 1, Kind: 5), new(54, 1, 201, 570, 40, 1, Kind: 255), new(40, 1, 1672, 200, 54, 1, Kind: 254), new(40, 0, 1519, 489, 40, 1, Kind: 253), new(38, 1, 433, 311, 40, 0, ReturnTown: 40, Kind: 252), new(39, 2, 552, 218, 38, 1, Kind: 5)];
            foreach (var trip in trips)
            {
                if (trip.Town == 54 && trip.Kind == 0)
                {
                    var bad = Area(trip.Town, trip.Area, trip.X, trip.Y, trip.PreviousTown, trip.PreviousArea);
                    bad[0] = 255;
                    string before = fixture.Snapshot();
                    peer.Send(36, bad);
                    peer.Barrier();
                    That(fixture.Snapshot() == before, "invalid dock destination preserves row");
                }

                if (trip.Town == 40 && trip.PreviousTown == 38 && trip.Kind == 5)
                {
                    string before = fixture.Snapshot();
                    // 纯规则覆盖完整字节域，TCP 保留房间、菜单和高位 kind 的拒绝链。
                    foreach (byte kind in new byte[] { 0, 1, 5, 6, 127, 255 })
                        peer.Send(36, Area(255, 250, 1900, 250, 38, 1, kind));
                    peer.Barrier();
                    That(fixture.Snapshot() == before, "representative kind values survive route rejection without writes");
                }

                Travel(peer, trip);
            }
        }

        (uint District, Trip[] Routes)[] respawns = [(39, [new(40, 1, 1782, 193, 38, 1, Kind: 5), new(54, 1, 201, 570, 40, 1), new(60, 3, 202, 59, 54, 1), new(60, 1, 538, 73, 60, 3), new(118, 0, 379, 229, 60, 1, Npc: 400000062), new(60, 1, 1140, 90, 118, 0, Kind: 255, Npc: 8001), new(118, 0, 379, 229, 60, 1, Npc: 400000062)]), (60, [new(60, 1, 1241, 139, 38, 1, Kind: 6)]), (60, [])];
        foreach (var respawn in respawns)
        {
            using var peer = Open(fixture);
            var packets = peer.Select(slot, prepared: true);
            Bytes(packets.Single(packet => packet.Kind == 0 && packet.Opcode == 23).Body.AsSpan(0, 8), Build(w =>
            {
                w.Write(id);
                w.Write((byte)38);
                w.Write((byte)1);
                w.Write((ushort)450);
                w.Write((ushort)234);
            }), "relogin normalized room");
            Bytes(packets.Single(packet => packet.Kind == 0 && packet.Opcode == 1379).Body, Build(w => w.Write(respawn.District)), "district room return");
            Bytes(packets[1].Body, [95, 0, 0, 0], "story survives reconnect");
            Bytes(packets[6].Body, completed, "quests survive reconnect");
            That(fixture.Snapshot("SELECT town,area,x,y,return_town FROM characters WHERE id=$p0", id) == $"[[38,1,450,234,{respawn.District}]]", "respawn persisted");
            foreach (var trip in respawn.Routes)
            {
                if (trip.Npc == 400000062)
                {
                    string before = fixture.Snapshot();
                    peer.Send(36, Area(trip.Town, trip.Area, trip.X, trip.Y, trip.PreviousTown, trip.PreviousArea, trip.Kind, uint.MaxValue));
                    peer.Barrier();
                    That(fixture.Snapshot() == before, "unknown NPC cannot commit travel");
                }

                Travel(peer, trip);
                if (trip.Npc == 400000062)
                {
                    string before = fixture.Snapshot();
                    peer.Send(15, new byte[4]);
                    peer.Send(16, new byte[4]);
                    peer.SendBarrier();
                    peer.Expect(1, 15, [1]);
                    peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
                    peer.Expect(0, 27, new byte[42]);
                    peer.Expect(1, 1347, [1]);
                    That(fixture.Snapshot() == before, "opening dungeon select preserves town state and does not answer a short opcode 16");
                    peer.Send(16, [3, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0x49, 0x0C, 0, 0, 0, 0, 0, 0, 0]);
                    peer.SendBarrier();
                    peer.Expect(1, 16, [0, 9, 0, 0]);
                    peer.Expect(1, 1347, [1]);
                    That(fixture.Snapshot() == before, "入场资格不符时静默拒绝，不发成功或修改存档");
                    peer.Send(16, [3, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0x49, 0x0C, 0, 0, 0, 0, 0, 0, 0]);
                    peer.Send(37, new byte[8]);
                    peer.SendBarrier();
                    peer.Expect(1, 16, [0, 9, 0, 0]);
                    peer.Expect(1, 1347, [1]);
                    That(fixture.Snapshot() == before, "repeated selection and unsolicited loading completion cannot publish a dungeon or advance quests");
                    peer.Send(15, [3, 0, 0, 0]);
                    peer.SendBarrier();
                    peer.Expect(1, 15, [1]);
                    peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
                    peer.Expect(1, 1347, [1]);
                    That(fixture.Snapshot() == before, "chosen dungeon does not reopen the empty select list");
                    peer.Send(631, Move(854, 261, motion: true));
                    peer.Barrier();
                    That(fixture.Snapshot() == before, "选图坐标不能覆盖持久城镇返回点");
                    peer.Send(132, [0]);
                    peer.Barrier();
                    var back = towns.SelectionReturn(new(trip.Town, trip.Area, trip.X, trip.Y), quests)!;
                    string afterReturn = AtPosition(fixture, id, back);
                    peer.Send(132);
                    peer.Expect(1, 132, [1]);
                    peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
                    peer.Expect(0, 23, [(byte)peer.Id, (byte)(peer.Id >> 8), back.Town, back.Area, (byte)back.X, (byte)(back.X >> 8), (byte)back.Y, (byte)(back.Y >> 8), 0, 3]);
                    peer.Expect(0, 24, [back.Town, back.Area, 1, 0, (byte)peer.Id, (byte)(peer.Id >> 8), (byte)back.X, (byte)(back.X >> 8), (byte)back.Y, (byte)(back.Y >> 8), 0, 3, 1]);
                    peer.Send(132);
                    peer.Barrier();
                    That(fixture.Snapshot() == afterReturn, "返回132只提交资源落点，重复点击不重发和写库");
                    peer.Send(15, new byte[4]);
                    peer.Expect(1, 15, [1]);
                    peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
                    peer.Expect(0, 27, new byte[42]);
                    peer.Send(132);
                    peer.Batch((1, 132), (0, 3), (0, 23), (0, 24));
                    peer.Send(631, Move(854, 261, motion: true));
                    peer.Barrier();
                    That(fixture.Snapshot("SELECT town,area,x,y FROM characters WHERE id=$p0", id) == "[[118,0,854,261]]", "movement works after dungeon failure");
                    string active = fixture.Snapshot("SELECT slot,quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", id);
                    string done = fixture.Snapshot("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0", id);
                    peer.Send(33, [0x21, 0x00, 0x49, 0x0C, 0x00, 0x00]);
                    peer.Expect(1, 33, [0, 19, 0]);
                    peer.Barrier();
                    peer.Send(33, new byte[7]);
                    peer.Barrier();
                    That(fixture.Snapshot("SELECT slot,quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", id) == active
                        && fixture.Snapshot("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0", id) == done,
                        "quest trigger record does not reward or rewrite conditions");
                }
            }

            if (respawn.District == 60 && respawn.Routes.Length != 0)
            {
                peer.Send(35, Move(1110, 257));
                peer.Barrier();
            }
        }

        await fixture.StopAsync();
    }

    static void Travel(TcpPeer peer, Trip t) => peer.Travel(t.Town, t.Area, t.X, t.Y, t.PreviousTown, t.PreviousArea, t.Bottom, t.ReturnTown, t.Kind, t.Npc);
    public static async Task JobsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "jobs"), resources: resources);
        await fixture.StartAsync();
        int[] counts = [6, 6, 6, 6, 6, 6, 6, 6, 8, 21, 8, 7, 6, 6, 6, 6];
        for (byte job = 0; job < 16; job++)
        {
            string name = job == 15 ? "qiangzhansh" : "Class" + job;
            byte[] player;
            ushort characterId;
            using (var peer = Open(fixture))
            {
                That(peer.Roster[1] == job, "job roster before create");
                peer.Send(692, Name(name));
                peer.Expect(1, 692, [1]);
                byte preview = job == 0 ? (byte)2 : job == 15 ? (byte)4 : (byte)(job % 6);
                byte[] roster = peer.Create(name, job, variant: preview);
                characterId = peer.Id;
                That(roster[1] == job + 1, "job roster after create");
                string beforeNameCheck = fixture.Snapshot();
                peer.Send(692, Name(name));
                peer.Expect(1, 692, [0, 24, 0]);
                That(fixture.Snapshot() == beforeNameCheck, "each job name check never creates or changes a character");
                int at = 15;
                for (int slot = 0; slot <= job; slot++)
                {
                    int width = (int)U32(roster, at + 2);
                    That(U16(roster, at) == slot, "contiguous roster slots");
                    Bytes(roster.AsSpan(at + 8 + width, 3), [(byte)slot, 0, 1], "job roster identity");
                    at += 70 + width + peer.Roster[at + 21 + width] * 25;
                }
                var packets = peer.Select(job, combos: job == 9 ? TcpPeer.StoredCombos(fixture, characterId) : null);
                That(packets[0].Body[222] == 38 && packets.Single(packet => packet.Kind == 0 && packet.Opcode == 23).Body[2] == 38, "select initial town agrees with USER_AREA for job " + job);
                That(U16(packets[0].Body, 22) == ushort.MaxValue && U32(packets[0].Body, 24) == 0, "新角色没有按固定编号自动接取的任务");
                Bytes(packets[1].Body, [1, 0, 0, 0], "accepted story level");
                Bytes(packets[2].Body.AsSpan(127 + NameWidth(name), 3), [job, 0, 1], "actor job");
                That(packets.Single(packet => packet.Kind == 0 && packet.Opcode == 23).Body[9] == 3 &&
                    packets.Single(packet => packet.Kind == 0 && packet.Opcode == 24).Body[11] == 3,
                    "各职业首次地区绑定保留默认开启的转职显示位 " + job);
                AssetWireResult actorEquipment = AssetWireChecks.ActorEntries(packets[2].Body, 133 + NameWidth(name));
                That(actorEquipment.Entries.Length == 5 && actorEquipment.ConsumedBytes == 126, "town Actor consumes five complete entries");
                CheckAssetRows(fixture, job, actorEquipment, false);
                player = packets[3].Body;
                That(U32(player, 32) == 89 && player.Length == 607 + 6 * counts[job] && player[583] == (counts[job] == 0 ? 255 : 0), "player stat/skill width");
                at = 584;
                for (int tree = 0; tree < 2; tree++)
                {
                    int count = player[at++];
                    That(count == counts[job], "PVF initial skill count");
                    if (job is 0 or 15)
                    {
                        (ushort Id, byte Level)[] expected = job == 0
                            ? [(5, 1), (46, 1), (169, 1), (174, 1), (179, 7), (511, 1)]
                            : [(1, 1), (50, 1), (169, 1), (174, 1), (179, 7), (511, 1)];
                        for (int i = 0; i < count; i++)
                            That((U16(player, at + i * 3), player[at + i * 3 + 2]) == expected[i], "recorded initial skill");
                    }

                    at += count * 3;
                }

                if (job == 15)
                    That(U32(player, 36) == 1700 && U32(player, 40) == 1500 && U16(player, 108) == 9000 && U16(player, 110) == 8000, "gunblade PVF stats");
                peer.Send(35, Move(461, 234));
                peer.Barrier();
            }

            using (var peer = Open(fixture))
            {
                var packets = peer.Select(job, combos: job == 9 ? TcpPeer.StoredCombos(fixture, characterId) : null);
                That(packets[0].Body[222] == 38 && packets.Single(packet => packet.Kind == 0 && packet.Opcode == 23).Body[2] == 38, "relogin initial town agrees with USER_AREA for job " + job);
                That(U16(packets[0].Body, 22) == ushort.MaxValue && U32(packets[0].Body, 24) == 0, "重登保留空任务槽，不自动接取任务");
                Bytes(packets[3].Body, player, "relogin skills and stats");
                That(packets.Single(packet => packet.Kind == 0 && packet.Opcode == 23).Body[9] == 3 &&
                    packets.Single(packet => packet.Kind == 0 && packet.Opcode == 24).Body[11] == 3,
                    "各职业重连地区绑定保留转职显示位 " + job);
            }

            using (var peer = Open(fixture))
            {
                peer.Send(5, CreateBody(name, job));
                peer.Closed();
            }

            var identities = fixture.Query("SELECT job,grow_type,level FROM characters ORDER BY slot");
            That(identities.Length == job + 1 && identities.Select((r, i) => Convert.ToInt32(r[0]) == i && Convert.ToInt32(r[1]) == 0 && Convert.ToInt32(r[2]) == 1).All(b => b), "persisted job identities");
            That(Convert.ToInt32(fixture.Query("SELECT count(*) FROM character_skills WHERE character_id=(SELECT id FROM characters WHERE slot=$p0)", job)[0][0]) == counts[job], "persisted starter skills");
            That(Convert.ToInt32(fixture.Query("SELECT count(*) FROM character_inventory_items")[0][0]) == 5 * (job + 1), "duplicates never issue a second starter set");
            That(fixture.Query("SELECT gold FROM characters").All(row => Convert.ToUInt32(row[0]) == 0), "ordinary creation does not grant gold");
        }

        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP 16 jobs: creation, identity, stats, skills, movement, duplicates and reconnect");
    }

    private static void CheckAssetRows(TcpTestFixture fixture, int characterSlot, AssetWireResult wire, bool player)
    {
        var rows = fixture.Query("""
            SELECT s.slot_index,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value'),
              json_extract(i.item_core,'$.Durability'),1,i.item_uid
            FROM character_inventory_items i JOIN characters c ON c.id=i.character_id
            JOIN character_inventory_slots s ON s.item_uid=i.item_uid
            WHERE c.slot=$p0 AND s.list_type=3 ORDER BY s.slot_index
            """, characterSlot);
        That(rows.Length == wire.Entries.Length, "projected equipment count matches committed instances");
        for (int index = 0; index < rows.Length; index++)
        {
            var expected = rows[index];
            AssetWireEntry actual = wire.Entries[index];
            That(actual.Slot == Convert.ToByte(expected[0]) && actual.TemplateId == Convert.ToInt32(expected[1]) &&
                Convert.ToUInt32(expected[4]) == 1 && Convert.ToInt64(expected[5]) > 0,
                "wire slot/template matches an owned single committed item");
            if (player)
                That(actual.Data == Convert.ToUInt32(expected[2]) && actual.Durability == Convert.ToUInt16(expected[3]),
                    "Player random Data and durability match committed instance");
        }
    }
}
