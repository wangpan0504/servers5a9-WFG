using DfoServer.Game.Accounts;
using DfoServer.Game.Events;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 换频道先进赛丽亚房间再出门到频道地区（安徒恩除外）；原生换频道进出卢克 0x20 / 安徒恩 0x17 / 超时空之战 0x2D / 魔界大战 0x36 / 普雷 0x32 的落点与快照（ARAD 01EB5AF0 谓词）：
// 普通→团本页签频道、进出魔界大战客户端不记快照，服务端必须按落点城镇入城；团本页签频道之间互换客户端留在原城，
// 服务端必须保留原位置。每次换频道后按客户端随后的 same-area 位置回放与移动核对服务端接受（不再被 route-not-in-pvf 拒绝）。
internal static partial class RaidChannelLandingTcpTests
{
    static readonly int[] RaidQuests = [12829, 2903, 8672, 8935, 3779, 4872, 4840, 5927, 3280];

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-landing"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "RaidLand");
        var Switch = Switcher(fixture, resources, endpoints, character);

        byte normal = endpoints.Keys.Min();
        Switch(48, land: true, town: 48, "普通→帝国竞技场直接进入48/0准备场景");
        Switch(48, land: false, town: null, "帝国同频道重连保留正确准备场景");
        Switch(normal, land: true, town: 38, "帝国→普通仍回共享房间及原普通地区", area: 1, exit: 38);
        Switch(46, land: true, town: 62, "普通→再战魔界直接进入62/0准备场景");
        Switch(46, land: false, town: null, "再战同频道重连保留正确准备场景");
        fixture.Sql("UPDATE characters SET town=38,area=1,x=433,y=311 WHERE id=$p0", character.Id);
        Switch(46, land: true, town: 62, "同类型重连修复旧版共享房间落点");
        Switch(normal, land: true, town: 38, "再战→普通仍回共享房间及原普通地区", area: 1, exit: 38);
        // 用户确认：换频道先到赛丽亚房间 38/1，再出门才是对应地区。只有安徒恩 0x17（ARAD 03B87FE0 起始地点 19/0）直接落战舰。
        // 团本页签谓词相同（01EB5AF0）的换频道客户端保留城镇快照，服务端保留原位置；在赛丽亚房间里互换则出门按新频道落点。
        Switch(19, land: true, town: 19, "普通→安徒恩 19（0x17 直接落 19/0）");
        Switch(normal, land: true, town: 38, "安徒恩→普通（进赛丽亚房间，出门到艾尔文 38/0）", area: 1, exit: 38);
        Switch(20, land: true, town: 38, "普通→卢克 20（进赛丽亚房间，出门 32/3）", area: 1, exit: 32);
        Switch(39, land: false, town: null, "卢克 20→超时空之战 39（同为团本页签，客户端保留 32/3）");
        Switch(41, land: true, town: 38, "超时空之战 39→魔界大战 41（0x36 谓词不同，出门 67/1）", area: 1, exit: 67);
        Switch(42, land: true, town: 38, "魔界大战 41→普雷 42（进赛丽亚房间，留在房间）", area: 1);
        Switch(39, land: false, town: null, "普雷 42→超时空之战 39（房间快照保留，出门 115/0）", exit: 115);
        Switch(41, land: true, town: 38, "超时空之战 39→魔界大战 41（留在房间）", area: 1);
        const string returnSql = "SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0";
        const string positionSql = "SELECT town,area,x,y,direction FROM characters WHERE id=$p0";
        string beforePreyReturn = fixture.Snapshot(returnSql, character.Id);
        Switch(42, land: true, town: 38, "魔界大战 41→普雷 42（进赛丽亚房间，出门 59/1）", area: 1, exit: 59);
        That(fixture.Snapshot(positionSql, character.Id) == "[[59,1,1217,98,0]]" &&
            fixture.Snapshot(returnSql, character.Id) == beforePreyReturn,
            "普雷1426一次出门保存59/1完整位置，原普通返回地区五字段不变");
        Switch(19, land: false, town: null, "普雷 42→安徒恩 19（同为团本页签，客户端保留 59/1）");
        Location ordinaryReturn;
        // 分支依据原始城镇登记，不用被测目录是否漏装来降低期望。
        // 2026-10-29 正式PVF移除魔兽资源：魔兽换频道与回房段按资源在位判定暂时屏蔽，其余频道段照跑。
        if (!BeastFixtures.Enabled(resources))
        {
            BeastFixtures.Skip("raid-channel-landing 魔兽段");
            return;
        }
        if (PvfScript.List(resources.Archive.ReadText("town/town.lst")).ContainsKey(51))
        {
            That(resources.Towns.Areas.Keys.Where(key => key.Item1 == 51).Select(key => key.Item2).Order().SequenceEqual(new byte[] { 0, 1, 2 }),
                "原始城镇索引已登记51时必须完整装载0/1/2三区，不能把漏组降为缺输入");
            Switch(49, land: true, town: 38, "团队频道→魔兽49（独立谓词，先进共享房间再出门到51/2）", area: 1, exit: 51);
            That(fixture.Snapshot("SELECT town,area FROM characters WHERE id=$p0", character.Id) == "[[51,2]]",
                "完整输入的真实换频道与1426出门仍须落到魔兽等待区51/2");
            ordinaryReturn = resources.Towns.ResolveRoomReturn(new(31,1,0,0),null,character.Level,
                character.CompletedQuests.ToHashSet());
            That(ordinaryReturn.Town == 31,"实际进入51后，普通频道选角归回正常中央公园，仍须有合法的31城镇返回门口");
        }
        else
        {
            var decision = ChannelAreaLanding.Decide(resources.Towns, new(new(2, 49), 40, "[beast]"), 95,
                character.CompletedQuests.ToHashSet());
            That(!resources.Towns.Areas.Keys.Any(key => key.Item1 == 51) &&
                decision is { Spot: null, Reason: "town-not-in-catalog" },
                "真实缺城镇51时魔兽频道落点精确拒绝，不从原生物理地图猜造等待区");
            // 前面的卢克出门已把普通返回地区更新为32/3；后续专属城镇保留该门口，不再沿用CreateRaider初始38/0。
            string returnBefore = fixture.Snapshot(returnSql, character.Id);
            That(returnBefore == "[[32,3,754,211,0]]",
                "进入缺资源魔兽频道前保存卢克路径后的真实普通返回门口32/3(754,211,0)");
            Switch(49, land: true, town: 38, "团队频道→魔兽49（缺51时落点拒绝，共享房间按原返回地区出门；非魔兽落点正向）", area: 1, exit: 32);
            That(fixture.Snapshot("SELECT town,area,x,y,direction,return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", character.Id) == "[[32,3,754,211,0,32,3,754,211,0]]" &&
                fixture.Snapshot(returnSql, character.Id) == returnBefore,
                "缺51时1379与1426沿原普通返回门口32/3落库，返回五字段逐字节保持，不宣称魔兽等待区成功");
            // 此分支从未到51，普通频道不能触发LeavesBeastTown归回31；严格保留已证32/3五字段。
            ordinaryReturn = new(32,3,754,211,0);
        }
        Switch(normal, land: true, town: 38, $"魔兽→普通回房、1379及1426连接已证返回地区{ordinaryReturn.Town}/{ordinaryReturn.Area}", area: 1, exit: ordinaryReturn.Town);
        string expectedReturn = $"[[{ordinaryReturn.Town},{ordinaryReturn.Area},{ordinaryReturn.X},{ordinaryReturn.Y},{ordinaryReturn.Direction}]]";
        That(fixture.Snapshot(positionSql,character.Id) == expectedReturn && fixture.Snapshot(returnSql,character.Id) == expectedReturn,
            "普通频道1379及1426之后完整位置和返回五字段与实际资源分支的唯一预期一致");
        Switch(normal, land: false, town: null, $"普通频道重连仍保留已证返回地区{ordinaryReturn.Town}/{ordinaryReturn.Area}");
        That(fixture.Snapshot(positionSql,character.Id) == $"[[{ordinaryReturn.Town},{ordinaryReturn.Area},{ordinaryReturn.X+2},{ordinaryReturn.Y},5]]" &&
            fixture.Snapshot(returnSql,character.Id) == expectedReturn,
            "普通频道重连保留原位置、same-area回放及原有移动，仅当前位置按Move增加2并转向5，返回五字段不丢失");
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
        That(resources.Towns.IsRoom(new(19, 0, 0, 0), character.CompletedQuests.ToHashSet()),
            "安徒恩战舰入口19/0是房间图，普通选角也必须下发1379");
        foreach (byte channel in new byte[] { endpoints.Keys.Min(), 19 })
        {
            using var peer = Open(fixture, gamePort: endpoints[channel], channelNumber: channel);
            if (channel is 101 or 47 or 51 or 55)
                That(peer.LoginEndpoint[3] == (channel switch { 101 => 24, 47 => 8, 51 => 4, _ => 5 }),
                    "连续换频道LOGIN绑定实际选中频道类型");
            peer.ExpectedOpenEvents = [282, 2449, 414, 181, ServerEventService.NolanVisibilityId];
            // 共享房间38/1与安徒恩战舰入口19/0均为房间图，严格保留两边的1379→事件初始化包序。
            var entry = peer.Select(character.Slot, prepared: true);
            var location = entry.Single(packet => packet is { Kind: 0, Opcode: 24 }).Body;
            That(location[0] == (channel == 19 ? 19 : 38) && location[1] == (channel == 19 ? 0 : 1),
                $"频道 {channel} 普通选角绑定真实初始地区，事件初始化不改变落点");
            peer.Barrier();
            That(peer.EventInfoPayload.Length == 163 + 15 * peer.ExpectedOpenEvents.Length,
                $"频道 {channel} EVENT_INFO 同时保留2037、红包2378、乔纳森2395及卢克282/超时空2449/普雷414/安徒恩181与诺兰2532");
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
            if (channel == 49) That(peer.LoginEndpoint.Length > 3 && peer.LoginEndpoint[3] == 40,
                "真实LOGIN帧保留魔兽类型40，不因目录已配置而遗漏实际应答");
            // 本夹具只有一名角色，TAG准备场景下发空编队目录，不虚造双角色。
            ushort[]? tagNotices = channel switch { 48 => [1403, 1400, 1632], 46 => [1677, 1678], _ => null };
            var packets = land
                ? peer.Select(character.Slot, prepared: true, nativeSwitch: true, room: room, tagEvildom: tagNotices)
                : peer.Select(character.Slot, prepared: true, resumeTown: true, room: room, tagEvildom: tagNotices);
            peer.Barrier();
            // 包序由 TcpPeer.Select 逐包核对：落点走普通选角序列，保留快照走换频道 1379/124/24 恢复序列。
            var actor = packets.Single(packet => packet is { Kind: 0, Opcode: 2 } && packet.Body[0] == 0).Body;
            That(actor[3] == 2 && actor[4] == channel && U16(actor, 121) == character.Id,
                "换频道后本人Actor绑定目标频道，位置快照保留不代表仍在旧频道");
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
                That(!resources.Towns.IsRoom(Position(), completed), $"{label}：一次出门直达街区，不能再进地区房间");
            }
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        };
    }

    // 特殊区域页频道：五陵 43 配合客户端补丁独立比较类型，跨五陵边界先进赛丽亚房间。
    public static async Task RunSpecialAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await PvpLandingAsync(source, root, resources);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "special-channel-landing"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "SpecLand");
        var Switch = Switcher(fixture, resources, endpoints, character);
        byte normal = endpoints.Keys.Min();
        // 没有旧频道历史也不能把普通街道当作五陵快照；先入共享房间，再按五陵出口到 117/1。
        Switch(43, land: true, town: 38, "普通街道→五陵 43（先进赛丽亚房间）", area: 1, exit: 117);
        // 魔界大战 0x36 的 vt+0x4C 与 0x38 不同：客户端不留快照，进赛丽亚房间，出门按频道落点。
        Switch(41, land: true, town: 38, "五陵 43→魔界大战 41（出门 67/1）", area: 1, exit: 67);
        Switch(43, land: true, town: 38, "魔界大战 41→五陵 43（出门 117/1 入口图）", area: 1, exit: 117);
        // 有频道历史时不再按 117 非专属城镇误判为普通频道，离开五陵也进共享房间。
        Switch(normal, land: true, town: 38, "五陵 43→普通（恢复普通房间）", area: 1);
        Switch(43, land: true, town: 38, "普通房间→五陵 43（出门到五陵入口）", area: 1, exit: 117);
        Switch(normal, land: true, town: 38, "再次离开五陵", area: 1);
        foreach (byte channel in new byte[] { 23, 26, 24, 35 })
        {
            byte destination = channel == 23 ? (byte)38 : (byte)17;
            Switch(channel, land: true, town: 38, $"进入特殊频道{channel}", area: 1, exit: destination);
            That(fixture.Snapshot("SELECT town,area FROM characters WHERE id=$p0", character.Id) ==
                (channel == 23 ? "[[38,5]]" : channel == 24 ? "[[17,1]]" : "[[17,2]]"),
                "亡者峡谷、异界、远古及裂缝各自出口与资源入口一致");
        }
        Switch(35, land: true, town: 38, "同type11频道重连也重新入房", area: 1, exit: 17);
        Switch(47, land: true, town: 10, "特殊频道到决斗地区");
        Switch(24, land: true, town: 38, "决斗地区到远古频道一次出门到甲板", area: 1, exit: 17);
        Switch(47, land: true, town: 10, "远古到决斗地区");
        Switch(26, land: true, town: 38, "决斗地区到异界频道一次出门到船尾", area: 1, exit: 17);
        string savedReturn = fixture.Snapshot("SELECT return_town,return_area,return_x,return_y FROM characters WHERE id=$p0", character.Id);
        Switch(43, land: true, town: 38, "异界到五陵", area: 1, exit: 117);
        That(fixture.Snapshot("SELECT return_town,return_area,return_x,return_y FROM characters WHERE id=$p0", character.Id) == savedReturn,
            "五陵入场和出门均保留之前的普通返回地区");
        Switch(normal, land: true, town: 38, "五陵返回普通频道不再回五陵", area: 1, exit: 17);
        fixture.Sql("UPDATE characters SET town=38,area=1,x=450,y=234,return_town=117,return_area=1,return_x=1070,return_y=213 WHERE id=$p0", character.Id);
        Switch(normal, land: false, town: null, "普通房间重连修复旧五陵返回地区", exit: 38);
        // 勇士对决 0x3F/0x3E 在团队页签谓词里：普通→勇士对决不留快照，出门 119；两者之间及与超时空之战 0x2D 互换保留快照。
        Switch(44, land: true, town: 38, "普通→勇士对决-自由 44（出门 119）", area: 1, exit: 119);
        Switch(45, land: false, town: null, "勇士对决-自由 44→排位 45（同为团本页签）");
        Switch(39, land: false, town: null, "勇士对决-排位 45→超时空之战 39（同为团本页签）");
        Switch(41, land: true, town: 38, "超时空之战 39→魔界大战 41（出门 67/1）", area: 1, exit: 67);
        Switch(45, land: true, town: 38, "魔界大战 41→勇士对决-排位 45（出门 119）", area: 1, exit: 119);
    }

    // 五陵频道入城与城内走动：落点 117/1 chn_fiendwar_xian_start.map 回赛丽亚房间的门上（1070,213），
    // 沿 [town movable area] `340 341 100 50 117 2` 进五陵主城 117/2（云珂等 NPC 所在）。
    // 117/0 chn_Gate_xian.map 是 Common/Gate_Seria.map 桩图，下方门通往 1/1，客户端实测发 SET_USER_AREA 01 01 0000 0000 05 75 00…（坐标 0,0）；
    // 这条 0,0 落点解析（#613）仍按存档放在 117/0 的用例保留。
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
            Switch(43, land: true, town: 38, "进赛丽亚房间，出门落五陵 117/1", area: 1, exit: 117);
            var peer = Open(fixture, gamePort: endpoints[43], channelNumber: 43);
            // 117/1 入口图不是 Gate_Seria 房间图，重连恢复不发 1379，按非房间序列核对 124/24。
            peer.Select(character.Slot, prepared: true, resumeTown: true, room: false);
            peer.Barrier();
            That(Where().StartsWith("[[117,1,"), "五陵频道入城在 117/1 入口图 " + Where());
            return peer;
        }
        using (var peer = Enter())
        {
            string ordinaryReturn = fixture.Snapshot("SELECT return_town,return_area,return_x,return_y FROM characters WHERE id=$p0", character.Id);
            // 入口图 117/1 的 `340 341 100 50 117 2` 门：117/2 侧回门 `1478 345 100 50 117 1` 中心 1528,370。
            peer.Travel(117, 2, 1528, 370, 117, 1);
            That(Where() == "[[117,2,1528,370]]", "117/1 入口图进五陵主城 117/2 " + Where());
            peer.Travel(117, 1, 390, 366, 117, 2);
            peer.Travel(38, 1, 450, 234, 117, 1, returnTown: 117);
            That(fixture.Snapshot("SELECT return_town,return_area,return_x,return_y FROM characters WHERE id=$p0", character.Id) == ordinaryReturn,
                "五陵城内往返及步行回房均不覆盖进入前普通返回地区");
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        // #613：把存档放到 117/0 桩图后再走下方门，客户端发 0,0，服务端按 PVF 入口解析落点。
        fixture.Sql("UPDATE characters SET town=117,area=0,x=474,y=234 WHERE id=$p0", character.Id);
        using (var peer = Open(fixture, gamePort: endpoints[43], channelNumber: 43))
        {
            peer.Select(character.Slot, prepared: true, resumeTown: true, room: true);
            peer.Barrier();
            That(Where().StartsWith("[[117,0,"), "存档在 117/0 时保留快照 " + Where());
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

    static Character CreateRaider(TcpTestFixture fixture, TcpTestResources resources, string name, Location? initialPosition = null, string? account = null)
    {
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var position = initialPosition ?? new Location(38, 0, 629, 196);
        var done = new HashSet<int>(RaidQuests);
        done.UnionWith(resources.Towns.SpecialChannelQuests.Values);
        using var owner = new AccountOwners().Acquire(account ?? fixture.Options.DevelopmentAccount, () => { });
        var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, resources.Learning.Get);
        var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
            { Experience = growth.GetLevelStartExperience(95) }, resources.Learning);
        return new CharacterRepository(fixture.Database).Create(owner, candidate with
            { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done.ToArray(), InitialLearning = learning });
    }
}
