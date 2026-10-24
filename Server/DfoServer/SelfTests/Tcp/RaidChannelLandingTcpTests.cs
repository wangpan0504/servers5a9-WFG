using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 换频道先进赛丽亚房间再出门到频道地区（安徒恩除外）；原生换频道进出卢克 0x20 / 安徒恩 0x17 / 超时空之战 0x2D / 魔界大战 0x36 / 普雷 0x32 的落点与快照（ARAD 01EB5AF0 谓词）：
// 普通→团本页签频道、进出魔界大战客户端不记快照，服务端必须按落点城镇入城；团本页签频道之间互换客户端留在原城，
// 服务端必须保留原位置。每次换频道后按客户端随后的 same-area 位置回放与移动核对服务端接受（不再被 route-not-in-pvf 拒绝）。
internal static class RaidChannelLandingTcpTests
{
    static readonly int[] RaidQuests = [12829, 2903, 8672, 8935, 3779, 4872];

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-landing"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "RaidLand");
        var Switch = Switcher(fixture, resources, endpoints, character);

        byte normal = endpoints.Keys.Min();
        // 用户确认：换频道先到赛丽亚房间 38/1，再出门才是对应地区。只有安徒恩 0x17（ARAD 03B87FE0 起始地点 19/0）直接落战舰。
        // 团本页签谓词相同（01EB5AF0）的换频道客户端保留城镇快照，服务端保留原位置；在赛丽亚房间里互换则出门按新频道落点。
        Switch(19, land: true, town: 19, "普通→安徒恩 19（0x17 直接落 19/0）");
        Switch(normal, land: true, town: 38, "安徒恩→普通（进赛丽亚房间，出门到艾尔文 38/0）", area: 1, exit: 38);
        Switch(20, land: true, town: 38, "普通→卢克 20（进赛丽亚房间，出门 32/0）", area: 1, exit: 32);
        Switch(39, land: false, town: null, "卢克 20→超时空之战 39（同为团本页签，客户端保留 32/0）");
        Switch(41, land: true, town: 38, "超时空之战 39→魔界大战 41（0x36 谓词不同，出门 67/0）", area: 1, exit: 67);
        Switch(42, land: true, town: 38, "魔界大战 41→普雷 42（进赛丽亚房间，留在房间）", area: 1);
        Switch(39, land: false, town: null, "普雷 42→超时空之战 39（房间快照保留，出门 115/0）", exit: 115);
        Switch(41, land: true, town: 38, "超时空之战 39→魔界大战 41（留在房间）", area: 1);
        Switch(42, land: true, town: 38, "魔界大战 41→普雷 42（进赛丽亚房间，出门 59/0）", area: 1, exit: 59);
        Switch(19, land: false, town: null, "普雷 42→安徒恩 19（同为团本页签，客户端保留 59/0）");
    }

    // 安徒恩频道页依赖 EVENT_INFO 事件 181（0xB5）与任务 12829：时段全开时入城事件表必须带齐四张团本卡与安徒恩事件，频道目录列出 19。
    public static async Task RunEventsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-events"), resources: resources);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        string allDay = string.Join(",", Enumerable.Range(1, 7).Select(day => $"{{\"weekday\":{day},\"start\":\"00:00\",\"end\":\"24:00\"}}"));
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "gm-raid-windows.json"),
            "{" + string.Join(",", new[] { "luke", "hyperspace", "prey", "anton" }.Select(raid => $"\"{raid}\":[{allDay}]")) + "}");
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        That(endpoints.ContainsKey(19) && endpoints.ContainsKey(20) && endpoints.ContainsKey(39) && endpoints.ContainsKey(41) && endpoints.ContainsKey(42),
            "频道目录列出安徒恩 19、卢克 20、超时空之战 39、魔界大战 41、普雷 42");
        var character = CreateRaider(fixture, resources, "RaidEvt");
        foreach (byte channel in new byte[] { endpoints.Keys.Min(), 19 })
        {
            using var peer = Open(fixture, gamePort: endpoints[channel], channelNumber: channel);
            peer.ExpectedOpenEvents = [282, 2449, 414, 181];
            peer.Select(character.Slot, prepared: true, room: channel == 19);
            peer.Barrier();
            That(peer.EventInfoPayload.Length == 68 + 15 * 4, $"频道 {channel} EVENT_INFO 含 2037 + 卢克282/超时空2449/普雷414/安徒恩181");
            That(peer.CompletedQuestHistory.Length > 0, $"频道 {channel} 下发已完成任务表");
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
            fixture.Sql("UPDATE characters SET town=38,area=0,x=629,y=196 WHERE id=$p0", character.Id);
        }
    }

    // 换频道一次并核对落点/快照、same-area 回放与移动；land=true 时服务端必须按频道落点城镇入城，否则保留原位置。
    // area：落点区域（land=true 时用于判定落点是否房间图，决定 1379 是否出现）。
    // exit：入城后在赛丽亚房间发 CMD 1426 出门，核对 1379 携带的出门城镇与出门后城镇都是该频道地区。
    delegate void ChannelSwitch(byte channel, bool land, byte? town, string label, byte area = 0, byte? exit = null);

    static ChannelSwitch Switcher(TcpTestFixture fixture, TcpTestResources resources, Dictionary<byte, int> endpoints, Character character)
    {
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(character);
        Location Position()
        {
            var row = fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", character.Id)[0];
            return new((byte)(long)row[0]!, (byte)(long)row[1]!, (ushort)(long)row[2]!, (ushort)(long)row[3]!);
        }
        string Where() => fixture.Snapshot("SELECT town,area,x,y FROM characters WHERE id=$p0", character.Id);
        byte Town() => (byte)(long)fixture.Query("SELECT town FROM characters WHERE id=$p0", character.Id)[0][0]!;
        return (channel, land, town, label, area, exit) =>
        {
            string before = Where();
            byte previous = Town();
            // 1379 只在入城位置是房间图时下发：保留快照看原位置，落点看频道落点区域。
            bool room = resources.Towns.IsRoom(land ? new Location(town!.Value, area, 0, 0) : Position(), completed);
            using var peer = Open(fixture, gamePort: endpoints[channel], channelNumber: channel);
            var packets = land
                ? peer.Select(character.Slot, prepared: true, nativeSwitch: true, room: room)
                : peer.Select(character.Slot, prepared: true, resumeTown: true, room: room);
            peer.Barrier();
            // 包序由 TcpPeer.Select 逐包核对：落点走普通选角序列，保留快照走换频道 1379/124/24 恢复序列。
            var row = fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", character.Id)[0];
            byte t = (byte)(long)row[0]!, a = (byte)(long)row[1]!;
            ushort x = (ushort)(long)row[2]!, y = (ushort)(long)row[3]!;
            if (land)
                That(t == town, $"{label}：按频道落点城镇 {town} 入城（实际 {Where()}）");
            else
                That(Where() == before && t == previous, $"{label}：保留原城镇位置 {before}（实际 {Where()}）");
            var final = packets.Last(p => p is { Kind: 0, Opcode: 24 });
            That(final is { Kind: 0, Opcode: 24 } && final.Body[0] == t && final.Body[1] == a,
                $"{label}：最终 AREA_USERS 绑定服务端城镇 {t}/{a}");
            // 客户端入城后按自己所在城镇回放同图位置；服务端城镇与客户端一致时属于 same-area 零变化。
            peer.Send(36, Area(t, a, x, y, t, a)); peer.Barrier();
            That(Where() == $"[[{t},{a},{x},{y}]]", $"{label}：same-area 位置回放被接受且不改位置");
            ushort nx = (ushort)(x + 2);
            peer.Send(35, Move(nx, y)); peer.Barrier();
            That(Where() == $"[[{t},{a},{nx},{y}]]", $"{label}：换频道后可以继续在 {t}/{a} 移动（实际 {Where()}）");
            if (exit is byte exitTown)
            {
                That(t == 38 && a == 1, $"{label}：出门前在赛丽亚房间 38/1（实际 {Where()}）");
                var notice = packets.LastOrDefault(p => p is { Kind: 0, Opcode: 1379 });
                That(notice is not null && U32(notice.Body) == exitTown, $"{label}：1379 出门城镇 {exitTown}（实际 {(notice is null ? "无" : U32(notice.Body).ToString())}）");
                peer.Send(1426, []); peer.SendBarrier();
                var location = peer.Expect(0, 23).Body;
                peer.Expect(0, 24);
                var next = peer.Read();
                if (next is { Kind: 0, Opcode: 1379 }) next = peer.Read();
                That(next is { Kind: 1, Opcode: 1347 }, $"{label}：出门包序 23/24/(1379)/1347");
                That(location[2] == exitTown && Town() == exitTown, $"{label}：赛丽亚房间出门到 {exitTown}（实际 {Where()}）");
            }
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        };
    }

    // #613 特殊区域页频道：五陵 43（0x38，谓词与普通相同）。
    public static async Task RunSpecialAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "special-channel-landing"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "SpecLand");
        var Switch = Switcher(fixture, resources, endpoints, character);
        byte normal = endpoints.Keys.Min();
        // 艾尔文防线 38/0 的普通频道角色换入五陵：0x38 与普通 0x16 的 01EB5AF0 谓词全同，客户端留快照，服务端保留原位置。
        Switch(43, land: false, town: null, "普通→五陵 43（0x38 谓词同普通）");
        // 魔界大战 0x36 的 vt+0x4C 与 0x38 不同：客户端不留快照，进赛丽亚房间，出门按频道落点。
        Switch(41, land: true, town: 38, "五陵 43→魔界大战 41（出门 67/0）", area: 1, exit: 67);
        Switch(43, land: true, town: 38, "魔界大战 41→五陵 43（出门 117/0）", area: 1, exit: 117);
        // 117 不是频道专属城镇，五陵→普通谓词全同，保留五陵位置。
        Switch(normal, land: false, town: null, "五陵 43→普通（保留 117）");
        // 勇士对决 0x3F/0x3E 在团队页签谓词里：普通→勇士对决不留快照，出门 119；两者之间及与超时空之战 0x2D 互换保留快照。
        Switch(44, land: true, town: 38, "普通→勇士对决-自由 44（出门 119）", area: 1, exit: 119);
        Switch(45, land: false, town: null, "勇士对决-自由 44→排位 45（同为团本页签）");
        Switch(39, land: false, town: null, "勇士对决-排位 45→超时空之战 39（同为团本页签）");
        Switch(41, land: true, town: 38, "超时空之战 39→魔界大战 41（出门 67/0）", area: 1, exit: 67);
        Switch(45, land: true, town: 38, "魔界大战 41→勇士对决-排位 45（出门 119）", area: 1, exit: 119);
    }

    // #613 选五陵出不去门：117/0 chn_Gate_xian.map 下方门通往 1/1，客户端实测发 SET_USER_AREA 01 01 0000 0000 05 75 00…（坐标 0,0）。
    public static async Task RunXianDoorAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "xian-village-door"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "XianDoor");
        var Switch = Switcher(fixture, resources, endpoints, character);
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(character);
        var towns = resources.Towns;
        // 117/0 chn_Gate_xian.map 的门：下方 `330 324 289 24 1 1`（银色村庄大街）与右侧 -2（返回地区），没有通往 117 其他区域的门。
        var xianGate = towns.Areas[(117, 0)].Map(completed);
        That(xianGate.IsRoom && xianGate.Links.Any(l => l.Gate.Door && l.Town == 1 && l.Area == 1) &&
            !xianGate.Links.Any(l => l.Gate.Door && l.Town == 117), "117/0 是房间图，门通往 1/1，不直连 117 其他区域");
        var gate = new Location(117, 0, 474, 234);
        var xianReturn = new Location(117, 1, 1070, 213);
        That(towns.Admission(gate, new(1, 1, 0, 0), 95, completed, xianReturn) == "ready",
            "117/0 下方门按地图放行到 1/1，不按房间返回地区拒绝: " + towns.Admission(gate, new(1, 1, 0, 0), 95, completed, xianReturn));
        That(towns.Admission(gate, new(8, 0, 0, 0), 95, completed, xianReturn) == "room-return-mismatch",
            "117/0 地图没有通往 8/0 的门，仍按房间返回地区拒绝: " + towns.Admission(gate, new(8, 0, 0, 0), 95, completed, xianReturn));
        That(towns.Admission(new(38, 1, 433, 311), new(1, 1, 0, 0), 95, completed, new(38, 0, 622, 196)) == "room-return-mismatch",
            "共享房间 38/1 仍只走返回地区");
        string Where() => fixture.Snapshot("SELECT town,area,x,y FROM characters WHERE id=$p0", character.Id);
        TcpPeer Enter()
        {
            Switch(41, land: true, town: 38, "进五陵前先离开普通谓词", area: 1);
            Switch(43, land: true, town: 38, "进赛丽亚房间，出门落五陵 117/0", area: 1, exit: 117);
            var peer = Open(fixture, gamePort: endpoints[43], channelNumber: 43);
            peer.Select(character.Slot, prepared: true, resumeTown: true, room: true);
            peer.Barrier();
            // Switcher 在落点后右移 2 验证可走，这里只核对仍在 117/0 房间图。
            That(Where().StartsWith("[[117,0,"), "五陵频道入城在 117/0 " + Where());
            return peer;
        }
        using (var peer = Enter())
        {
            // 右侧 -2 门：CMD 1426 回存档返回地区 117/1 的入口 1070,213。
            peer.Travel(117, 1, 1070, 213, 117, 0, bottom: true);
            That(Where() == "[[117,1,1070,213]]", "117/0 返回门进五陵 117/1 " + Where());
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        using (var peer = Enter())
        {
            var arrival = resources.Towns.Entrance(new(117, 0, 474, 234), new(1, 1, 0, 0), completed)
                ?? throw new InvalidOperationException("FAIL: 1/1 没有可用入口");
            That(arrival is { Town: 1, Area: 1 } && resources.Towns.IsPosition(arrival, completed), "1/1 入口按 PVF 门口解析 " + arrival);
            peer.Send(36, Convert.FromHexString("01010000000005750000000000000000"));
            peer.SendBarrier();
            var location = peer.Expect(0, 23).Body;
            That(location[2] == 1 && location[3] == 1 && TcpPeer.U16(location, 4) == arrival.X && TcpPeer.U16(location, 6) == arrival.Y,
                $"117/0 下方门按地图进 1/1，0,0 改为入口 {arrival}");
            peer.Expect(0, 24);
            peer.Expect(1, 1347, [1]);
            That(Where() == $"[[1,1,{arrival.X},{arrival.Y}]]", "下方门出五陵门口后存档在 1/1 " + Where());
            peer.Send(35, Move((ushort)(arrival.X + 2), arrival.Y)); peer.Barrier();
            That(Where() == $"[[1,1,{arrival.X + 2},{arrival.Y}]]", "到 1/1 后可以继续移动 " + Where());
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }
    }

    static Character CreateRaider(TcpTestFixture fixture, TcpTestResources resources, string name)
    {
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var position = new Location(38, 0, 629, 196);
        var done = new HashSet<int>(RaidQuests);
        done.UnionWith(resources.Towns.SpecialChannelQuests.Values);
        using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
        var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, resources.Learning.Get);
        var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
            { Experience = growth.GetLevelStartExperience(95) }, resources.Learning);
        return new CharacterRepository(fixture.Database).Create(owner, candidate with
            { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done.ToArray(), InitialLearning = learning });
    }
}