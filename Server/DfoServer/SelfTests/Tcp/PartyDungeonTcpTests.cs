using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    sealed class FrameClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    static bool HasCardSeat(byte[] body, int wanted)
    {
        if (body.Length < 164) return false;
        int offset = checked(156 + (int)U32(body, 151) * 8);
        for (int seat = 0; seat < 8; seat++)
        {
            if (offset >= body.Length) return false;
            int count = body[offset++];
            if (offset + count * 8 > body.Length) return false;
            if (seat == wanted) return count > 0;
            offset += count * 8;
        }
        return false;
    }
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        OrdinaryLukePartyTests.Run(resources);
        await RunLukeAsync(source, root, resources);
        await RunPartyAsync(source, root, resources, 2);
        await RunPartyAsync(source, root, resources, 4);
        await RunPartyAsync(source, root, resources, 2, rollback: true);
        await RunPartyAsync(source, root, resources, 2, rejectRetry: true);
        await RunDeparturesAsync(source, root, resources);
    }

    static async Task RunPartyAsync(ServerOptions source, string root, TcpTestResources resources, int count, bool rollback = false,
        bool rejectRetry = false)
    {
        bool checkFatigue = count == 2 && !rollback;
        bool checkFrames = count == 2 && !rollback && !rejectRetry;
        var frameClock = checkFrames ? new FrameClock() : null;
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "party-dungeon-" + count + (rollback ? "-rollback" : rejectRetry ? "-retry-rejected" : "")),
            gm: checkFatigue, resources: resources, clock: frameClock);
        await fixture.StartAsync();
        if (checkFatigue)
            That(DfoServer.Network.Management.DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
                "组队入场拒绝使用实际疲劳余额");
        using var alpha = Open(fixture, "partyalpha");
        using var beta = Open(fixture, "partybeta");
        using var gamma = count == 4 ? Open(fixture, "partygamma") : null;
        using var delta = count == 4 ? Open(fixture, "partydelta") : null;
        var participants = count == 4 ? new[] { alpha, beta, gamma!, delta! } : new[] { alpha, beta };
        for (int i = 0; i < participants.Length; i++) participants[i].Create("PartyMember" + i);
        Dictionary<TcpPeer, WirePacket[]> All(TcpPeer requester)
        {
            var rows = new Dictionary<TcpPeer, WirePacket[]> { [requester] = Drain(requester) };
            foreach (var peer in participants.Where(p => !ReferenceEquals(p, requester))) rows.Add(peer, Drain(peer));
            return rows;
        }
        foreach (var peer in participants)
        {
            // 只准备普通路线的前置资格；战斗、奖励和清图均经过真实生产协议。
            fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,3145)", peer.Id);
            fixture.Sql("UPDATE characters SET revive_coins=1 WHERE id=$p0", peer.Id);
            if (checkFatigue && ReferenceEquals(peer, rejectRetry ? alpha : beta))
                fixture.Sql("UPDATE character_fatigue SET used=maximum,remaining=0 WHERE character_id=$p0", peer.Id);
            if (count == 4 && ReferenceEquals(peer, participants[^1]))
                fixture.Sql("UPDATE character_dungeon_state SET next_object_key=65536 WHERE character_id=$p0", peer.Id);
            var frames = DfoServer.Game.Inventory.Models.SkinStorageState.Empty with
                { PartyFrames = [new(420000001, 0), new(420000002, checked((int)(frameClock?.Now.ToUnixTimeSeconds() ?? 0) + 120))],
                    Emoticons = checkFrames ? [new(410000046, 0)] : [] };
            if (checkFrames && ReferenceEquals(peer, alpha))
                fixture.Sql("INSERT INTO account_skin_storage(account,state_json) SELECT account,$p1 FROM characters WHERE id=$p0",
                    peer.Id, System.Text.Json.JsonSerializer.Serialize(frames));
            peer.Select(prepared: true, accountSkins: checkFrames && ReferenceEquals(peer, alpha) ? frames : null);
            peer.Send(1426); Drain(peer);
            peer.Send(36, Area(38, 2, 1085, 249, 38, 0, kind: 5)); Drain(peer);
        }
        foreach (var peer in participants) Drain(peer);
        if (checkFrames)
        {
            alpha.Send(1573, Build(w => { w.Write(0); w.Write(420000001); w.Write(new byte[36]); }));
            var selectedFrame = Drain(alpha);
            WeaponSkinTcpTests.AssertFrameActor(selectedFrame.Single(p => p is { Kind: 0, Opcode: 2 }), 420000001, alpha.Id);
        }
        for (int slot = 0; slot < count; slot++)
        {
            participants[slot].Send(2, Build(w =>
            {
                w.Write((byte)5); w.Write(0x0100007fu); w.Write(0x0100007fu);
                w.Write(checked((ushort)(30000 + slot))); w.Write(512u);
            }));
            Drain(participants[slot]);
        }
        byte[] Peer(ushort id, bool invitation) => Build(w =>
        {
            w.Write(id); w.Write((byte)0); w.Write(0u);
            if (invitation) w.Write(new byte[6]);
        });
        foreach (var invited in participants.Skip(1))
        {
            alpha.Send(10, Peer(invited.Id, true));
            That(Drain(alpha).Any(p => p is { Kind: 1, Opcode: 10, Body: [1] }), "队长发起真实组队邀请");
            That(Drain(invited).Any(p => p is { Kind: 0, Opcode: 7 }), "另一连接收到邀请");
            invited.Send(11, Peer(alpha.Id, false));
            var accepted = All(invited);
            That(participants.Take(Array.IndexOf(participants, invited) + 1).All(peer => accepted[peer].Any(p => p is { Kind: 0, Opcode: 9 })),
                "接受后为所有已加入成员更新真实队伍");
            if (checkFrames)
                foreach (var rows in accepted.Values)
                    WeaponSkinTcpTests.AssertFrameActor(rows.Single(p => p is { Kind: 0, Opcode: 2 } && p.Body[0] == 0 &&
                        U16(p.Body, 121) == alpha.Id), 420000001, alpha.Id);
        }
        if (checkFrames)
        {
            SelectFrame(20000, false); SelectFrame(420000001, false);
            beta.Send(36, Area(38, 0, 622, 196, 38, 2, kind: 5)); All(beta);
            beta.Send(36, Area(38, 2, 1085, 249, 38, 0, kind: 5));
            var rejoined = All(beta)[beta];
            WeaponSkinTcpTests.AssertFrameActor(rejoined.Single(p => p is { Kind: 0, Opcode: 2 } && p.Body[0] == 0 &&
                U16(p.Body, 121) == alpha.Id), 420000001, alpha.Id);
        }
        alpha.Send(15, new byte[4]);
        foreach (var peer in participants)
        {
            var opened = Drain(peer);
            That(opened.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }) &&
                opened.Any(p => p is { Kind: 0, Opcode: 27, Body.Length: 42 }), "队长选图同时打开全员客户端");
            var players = opened.Where(p => p is { Kind: 0, Opcode: 2 } && p.Body[0] == 1).ToArray();
            That(players.Length == count && players.Select(p => U16(p.Body, 22)).ToHashSet().SetEquals(participants.Select(p => p.Id)),
                "选图完整安装双方战斗属性与技能，角色ID未套用其他客户端偏移");
            That(opened.Any(p => p is { Kind: 0, Opcode: 6 } && U16(p.Body) != peer.Id),
                "选图实际经过远端角色清场，覆盖客户端队伍槽被移除的现场");
            CheckRoster(opened, 27);
            int host = Array.FindIndex(opened, p => p is { Kind: 0, Opcode: 26, Body: [0] });
            That(host >= 0 && host < Array.FindIndex(opened, p => p is { Kind: 0, Opcode: 27 }),
                "所有客户端必须先安装真实UDP主机槽位再切入选图，队员不能沿用未初始化主机");
        }
        byte[] selection = Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000");
        alpha.Send(15, BitConverter.GetBytes(3u));
        That(Drain(alpha).Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }) &&
            participants.Skip(1).All(peer => !Drain(peer).Any(p => p is { Kind: 0, Opcode: 27 })), "确认所选副本不会重复打开队员选图层");
        if (rollback)
        {
            string before = fixture.Snapshot();
            string objects = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
            fixture.Sql("CREATE TRIGGER reject_party_member BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id=" + beta.Id +
                " BEGIN SELECT RAISE(ABORT,'party entry rollback'); END");
            alpha.Send(16, selection);
            alpha.Expect(0, 9);
            alpha.Closed();
            That(!Drain(beta).Any(p => p is { Kind: 0, Opcode: 28 or 29 }),
                "入场事务失败不向另一成员安装副本或发送入场成功");
            That(fixture.Snapshot() == before && fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == objects &&
                fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts") is [[0L]],
                "后续成员预留失败时，全员revision、尝试号、对象高水位及疲劳整笔回滚");
            return;
        }
        if (checkFatigue)
        {
            string before = fixture.Snapshot();
            alpha.Send(16, selection);
            var rejected = All(alpha);
            byte failedSlot = rejectRetry ? (byte)0 : (byte)1;
            That(rejected[alpha].Any(p => p is { Kind: 1, Opcode: 16 } && p.Body.SequenceEqual(new byte[] { 0, 22, 0, failedSlot })) &&
                rejected.Values.All(rows => rows.All(p => p.Opcode is not (28 or 29))) && fixture.Snapshot() == before,
                "组队疲劳不足的拒绝指向实际失败成员槽，整队不扣费或安装实例");
            That(DfoServer.Network.Management.DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue on", out _),
                "随后开启无限疲劳，验证同一队伍拒绝后仍可重新入场");
        }
        alpha.Send(16, selection);
        var entered = All(alpha); var enteredA = entered[alpha];
        var map = enteredA.Single(p => p is { Kind: 0, Opcode: 29 }).Body;
        foreach (var rows in entered.Values)
        {
            CheckRoster(rows, 28);
            Bytes(map, rows.Single(p => p is { Kind: 0, Opcode: 29 }).Body, "全员收到相同地图、种子及怪物对象key");
        }
        That(fixture.Query("SELECT count(DISTINCT instance_id) FROM character_dungeon_fatigue_receipts") is [[0L]],
            "加载前不提前扣逐房疲劳");
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(3).Mazes[enteredA.Single(p => p.Opcode == 28).Body[7]];
        var visited = new HashSet<DungeonPoint>();
        int consumablePickups = 0;
        for (int room = 0; room < 30; room++)
        {
            if(room==0 && count==2)
            {
                alpha.Send(17,Build(w=> { w.Write((byte)2);w.Write(alpha.Id);w.Write(0u);w.Write(7u);w.Write("loading"u8); }));
                foreach(var rows in All(alpha).Values)
                {
                    var text=rows.Single(p=>p is {Kind:0,Opcode:12}).Body;
                    That(text[0]==2 && U16(text,1)==alpha.Id && U32(text,4)==7 && text.AsSpan(8,7).SequenceEqual("loading"u8),
                        "已进入世界但尚未提交37的副本Loading阶段仍收到真实队伍广播");
                    That(rows.All(p=>p is not {Kind:0,Opcode:30} && p is not {Kind:1,Opcode:37}),
                        "聊天不提前释放副本加载屏障，不把房间Loading混同于选角未Ready");
                }
            }
            foreach (var peer in participants.Take(count - 1)) { peer.Send(37, new byte[8]); Drain(peer); }
            alpha.NoFrame(40, "缺少最后一人加载确认时不能释放屏障", (0, 30), (1, 37));
            participants[^1].Send(37, new byte[8]);
            foreach (var loaded in All(participants[^1]).Values)
                That(loaded.Any(p => p is { Kind: 0, Opcode: 30 }) && loaded.Count(p => p is { Kind: 0, Opcode: 3 }) == count,
                    "全员加载完成后释放屏障并激活全部队员");
            if (room == 0)
            {
                if (checkFrames)
                {
                    string beforeEmoticon = fixture.Snapshot();
                    alpha.Send(1559, Build(w => w.Write(410000046)));
                    var emotes = All(alpha);
                    foreach (var rows in emotes.Values)
                    {
                        Bytes(rows.Single(p => p is { Kind: 0, Opcode: 1535 }).Body,
                            Build(w => { w.Write(alpha.Id); w.Write(410000046); }), "真实组队副本内本人及队友按同一成员编号播放表情");
                        That(rows.All(p => p.Opcode != 1559 && p.Opcode != 14), "即时表情不伪造ACK或发送库存变更");
                    }
                    That(fixture.Snapshot() == beforeEmoticon, "副本播放表情不修改资产、进度或revision");
                    SelectFrame(20000, true); SelectFrame(420000001, true); SelectFrame(420000002, true);
                    frameClock!.Now += TimeSpan.FromSeconds(121);
                    alpha.Send(1426);
                    var expired = All(alpha);
                    That(expired[alpha].Any(p => p is { Kind: 0, Opcode: 1558 } && p.Body[0] == 0 && U32(p.Body, 1) == 20000),
                        "限时边框在线到期恢复仓库默认选择");
                    CheckFrameUpdates(expired, 20000, true);
                    SelectFrame(420000001, true);
                }
                beta.Send(40, [0x74, 0, 4, 1, 0, 0]);
                That(All(beta).Values.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 32 } && U16(p.Body) == beta.Id && p.Body[2] == 0)),
                    "成员死亡向全员同步，其他成员仍可继续战斗");
            }
            if (room == 1)
            {
                beta.Send(41, BitConverter.GetBytes(beta.Id));
                var revived = All(beta);
                That(revived.Values.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 32 } && U16(p.Body) == beta.Id && p.Body[2] == 1)) &&
                    revived[beta].Any(p => p is { Kind: 1, Opcode: 41 }), "死亡队员跟随过房后可扣本人复活币并同步复活");
            }
            var point = new DungeonPoint(map[0], map[1]);
            visited.Add(point);
            if (map.Length > 18)
            {
                int actualMapId = checked((int)U32(map, 14));
                var actualMap = new DungeonMap("map", point, actualMapId,
                    SelfTestCatalogs.Dungeons(resources.Archive).MapPath(actualMapId) ??
                    throw new InvalidDataException("线上地图缺少PVF路径"));
                var roomSource = SelfTestCatalogs.Rooms(resources.Archive).Get(actualMap, requirePassiveDefinitions: false);
                int promoted = 0;
                foreach (int offset in Enumerable.Range(0, map[18]).Select(i => 19 + i * 22)
                    .Where(i => map[i + 13] < 5 && map[i + 14] == 0))
                {
                    var sourceMonster = roomSource.Monsters[checked((int)U32(map, offset + 2))];
                    if (sourceMonster.RankKind == "[normal]")
                    {
                        That(map[offset + 13] <= 1, "组队普通怪只能保持普通或提升为精英，不能变成超级精英或Boss");
                        if (map[offset + 13] == 1) promoted++;
                    }
                    else if (sourceMonster.RankKind == "[boss]")
                        That(map[offset + 13] == 3, "普通组队Boss保持原生类型3");
                }
                That(promoted <= 1, "真实TCP共享地图每房最多一只随机增益怪，全员沿同一冻结演员完成击杀");
            }
            bool boss = false;
            if (map.Length > 18)
            foreach (int offset in Enumerable.Range(0, map[18]).Select(i => 19 + i * 22)
                .Where(i => map[i + 13] != 9 && map[i + 14] == 0).OrderBy(i => map[i + 13] == 3))
            {
                ushort key = U16(map, offset + 6);
                byte[] report = FormalDungeonTcpTests.Death(key, alpha.Id, map[offset + 12]);
                alpha.Send(39, report);
                var deaths = All(alpha).ToDictionary(row => row.Key,
                    row => row.Value.Single(p => p is { Kind: 0, Opcode: 38 } && U16(p.Body) == key));
                That(deaths.Count == count, "同步同一怪物的死亡事实，每人携带自己的掉落集合");
                That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE monster_key=$p0", key) is [[long receipts]] && receipts == count,
                    "共享击杀分别提交所有角色的真实经验账本");
                beta.Send(39, report);
                That(All(beta).Values.All(rows => !rows.Any(p => p is { Kind: 0, Opcode: 38 })) &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE monster_key=$p0", key) is [[long unchanged]] && unchanged == count,
                    "另一连接重复报告不再发奖或重新抽掉落");
                foreach (var (peer, loot) in deaths)
                for (int ordinal = 0; ordinal < U16(loot.Body, 2); ordinal++)
                {
                    int at = 4 + ordinal * 131;
                    uint ground = U32(loot.Body, at);
                    byte[] pickup = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(pickup, ground);
                    var goldBefore = participants.ToDictionary(p => p.Id,
                        p => (long)fixture.Query("SELECT gold FROM characters WHERE id=$p0", p.Id)[0][0]!);
                    peer.Send(43, pickup);
                    var pickups = All(peer); var received = pickups[peer];
                    That(received.Any(p => p is { Kind: 1, Opcode: 43, Body: [1] }) &&
                        received.Any(p => p is { Kind: 0, Opcode: 39 } && U32(p.Body) == ground) &&
                        pickups.Where(row => !ReferenceEquals(row.Key, peer)).All(row => !row.Value.Any(p => p is { Kind: 0, Opcode: 39 })), "每人独立领取，另一客户端的地面物品不被清除");
                    if (U32(loot.Body, at + 6) == 0)
                    {
                        uint goldAmount = U32(loot.Body, at + 10);
                        That(participants.All(p => fixture.Query("SELECT gold FROM characters WHERE id=$p0", p.Id) is [[long gold]] &&
                            gold == goldBefore[p.Id] + (ReferenceEquals(p, peer) ? goldAmount : 0)), "独立金币完整到账，其他队员余额不变");
                        var goldNotice = received.Single(p => p is { Kind: 0, Opcode: 39 }).Body;
                        int cursor = 6;
                        for (int slot = 0; slot < 8; slot++)
                        {
                            bool own = slot == Array.IndexOf(participants, peer);
                            That(goldNotice[cursor++] == (own ? 1 : 0) && U32(goldNotice, cursor) == (own ? goldAmount : 0),
                                "金币原生表现只激活领取者的真实队伍槽位");
                            cursor += own ? 13 : 4;
                        }
                        That(cursor == goldNotice.Length, "完整消费金币通知而不遗漏扩展成员字段");
                    }
                    int itemId = checked((int)U32(loot.Body, at + 6));
                    if (itemId != 0 && resources.ItemResources.Items.Get(itemId).Kind == DfoServer.GameWorld.Items.ItemKind.Consumable)
                    {
                        consumablePickups++;
                        ushort destination = U16(received.Single(p => p is { Kind: 0, Opcode: 39 }).Body, 16);
                        That(destination is >= 65 and <= 120 && fixture.Query("""
                            SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                            WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p1
                            """, peer.Id, itemId).Any(row => Convert.ToInt64(row[0]) == destination),
                            "组队真实消耗品地面拾取进入本人背包，通知槽与独立文件库一致，不自动占空快捷栏");
                    }
                    peer.Send(43, pickup);
                    That(Drain(peer).Any(p => p is { Kind: 1, Opcode: 43, Body: [0, 19, 0] }),
                        "本人不能重复领取自己的地面对象");
                }
                boss |= map[offset + 13] == 3;
            }
            if (boss)
            {
                if (count == 2 && !rollback) await VerifyFavorNpcNativeAsync(alpha, beta, fixture);
                string unsettled = fixture.Snapshot();
                beta.Send(46, DungeonSettlementProtocolTests.Report(alpha.Id, 99));
                That(!Drain(beta).Any(p => p is { Kind: 0, Opcode: 34 or 35 }) && fixture.Snapshot() == unsettled,
                    "报告缺少本人不能借队长行结算");
                beta.Send(46, DungeonSettlementProtocolTests.Report((beta.Id, 20), (ushort.MaxValue, 99)));
                That(!Drain(beta).Any(p => p is { Kind: 0, Opcode: 34 or 35 }) && fixture.Snapshot() == unsettled,
                    "非本次入场成员不能混入报告或写入奖励");
                foreach (var peer in participants)
                {
                    int slot = Array.IndexOf(participants, peer);
                    var report = DungeonSettlementProtocolTests.Report(participants.Select((member, index) =>
                        (member.Id, checked((byte)(99 - index * 20)))).ToArray());
                    peer.Send(46, report);
                    var settledPackets = Drain(peer);
                    var playResult = settledPackets.Single(p => p is { Kind: 0, Opcode: 34 }).Body;
                    That(playResult.Length == 9 + 7 * count && playResult[6] == 99 - slot * 20 && playResult[8] == count &&
                        Enumerable.Range(0, count).All(i => U16(playResult, 9 + 7 * i) == participants[i].Id) &&
                        settledPackets.Any(p => p is { Kind: 0, Opcode: 35 }), "真实多人报告选择本人行、按本人评分提交并发布完整名册与奖励");
                    if (slot > 0)
                        That(settledPackets.Any(p => p is { Kind: 0, Opcode: 35 } && HasCardSeat(p.Body, 0)),
                            "迟到结算的成员收到先翻成员的免费牌");
                    string committedSettlement = fixture.Snapshot();
                    peer.Send(46, report);
                    That(Drain(peer).Any(p => p is { Kind: 0, Opcode: 34 }) && fixture.Snapshot() == committedSettlement,
                        "多人原报告重放回显结果但不重复发奖");
                    report[report.Length - 5] ^= 1;
                    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(report.Length - 4),
                        DfoServer.Network.Parsers.ClientBodyChecksum.Compute(report.AsSpan(0, report.Length - 4)));
                    peer.Send(46, report);
                    That(!Drain(peer).Any(p => p is { Kind: 0, Opcode: 34 or 35 }) && fixture.Snapshot() == committedSettlement,
                        "已冻结的多人报告即使检查值有效也不能更改");
                    if (slot == 0)
                    {
                        peer.Send(69); Drain(peer);
                        peer.Send(70); Drain(peer);
                        peer.Send(71, [0, 0]);
                        That(Drain(peer).Any(p => p is { Kind: 1, Opcode: 71 } && p.Body[1] == 0),
                            "首名成员可在其他成员结算前翻牌");
                    }
                }
                That(fixture.Query("SELECT count(*),count(DISTINCT instance_id) FROM character_dungeon_settlement_receipts") is [[long settled, 1L]] && settled == count,
                    "共享实例有两份角色结算且实例ID一致");
                foreach (var peer in participants)
                {
                    byte slot = checked((byte)Array.IndexOf(participants, peer));
                    if (slot == 0) continue;
                    peer.Send(69); Drain(peer);
                    peer.Send(70);
                    var cardStart = Drain(peer);
                    var rights = cardStart.Single(p => p is { Kind: 1, Opcode: 70 }).Body;
                    That(Enumerable.Range(0, 8).All(i => (U16(rights, 1 + i * 2) != ushort.MaxValue) == (i == slot)),
                        "翻牌资格只开放本人的原始队伍席位");
                    That(cardStart.Any(p => p is { Kind: 1, Opcode: 71 } && p.Body[1] == 0),
                        "迟到进入翻牌页收到已揭示席位");
                    string beforeCard = fixture.Snapshot();
                    peer.Send(71, [0, (byte)((slot + 1) % count)]);
                    That(!Drain(peer).Any(p => p is { Kind: 1, Opcode: 71 }) && fixture.Snapshot() == beforeCard,
                        "不能领取其他成员位置的免费牌");
                    peer.Send(71, [0, slot]);
                    var revealed = Drain(peer).Single(p => p is { Kind: 1, Opcode: 71, Body.Length: 33 }).Body;
                    That(revealed[1 + slot * 4] == slot,
                        "每名队员在真实队伍席位领取并揭示自己的免费牌");
                    foreach (var other in participants.Where(member => !ReferenceEquals(member, peer)))
                    {
                        var updates = Drain(other);
                        That(updates.Any(p => p is { Kind: 0, Opcode: 35 } && HasCardSeat(p.Body, slot)) &&
                            updates.Any(p => p is { Kind: 1, Opcode: 71 } && p.Body[1 + slot * 4] == slot),
                            "队友收到翻牌者的牌面和所属席位揭示");
                    }
                    string claimed = fixture.Snapshot();
                    peer.Send(71, [0, slot]);
                    That(Drain(peer).Any(p => p is { Kind: 1, Opcode: 71 }) && fixture.Snapshot() == claimed,
                        "重复领取本人牌不再次增加金币或物品");
                }
                if (rejectRetry)
                {
                    That(DfoServer.Network.Management.DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
                        "结算时恢复队员的真实零疲劳，验证再挑战失败");
                    string entries = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
                    string fatigue = fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id");
                    string assets = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
                    alpha.Send(72, [1, 0]);
                    var returned = All(alpha);
                    That(returned.Values.All(rows => rows.Any(p => p is { Kind: 1, Opcode: 72, Body: [1, 1, 2] }) &&
                        rows.Any(p => p is { Kind: 0, Opcode: 24 }) && rows.All(p => p.Opcode is not (28 or 29))) &&
                        fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == entries &&
                        fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == fatigue &&
                        fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") == assets,
                        "成员疲劳不足时整队提交回城并确认选项2，不重复扣费、预留或发奖");
                    alpha.Send(72, [1, 0]);
                    That(All(alpha).Values.All(rows => rows.All(p => p.Opcode is not (28 or 29))),
                        "退场后的旧再挑战不会安装新副本");
                    return;
                }
                alpha.Send(72, [1, count == 2 ? (byte)0 : (byte)1]);
                var result = All(alpha);
                That(result.Values.All(rows => rows.Any(p => p is { Kind: 1, Opcode: 72 })), "队长结算操作向全员确认真实结果");
                if (count == 2)
                {
                    foreach (var rows in result.Values)
                    {
                        CheckRoster(rows, 28);
                        That(rows.Any(p => p is { Kind: 0, Opcode: 29 }), "再挑战建立新的共享地图");
                    }
                    alpha.Send(37, new byte[8]); Drain(alpha);
                    beta.Dispose();
                    // 队员在加载阶段断线，剩余已就绪成员必须被释放；等待由真实通知驱动。
                    alpha.Expect(0, 6);
                    alpha.Expect(0, 26, [0]);
                    alpha.Expect(1, 37, [1]); alpha.Expect(0, 30);
                    Drain(alpha);
                    alpha.Send(42);
                    That(Drain(alpha).Any(p => p is { Kind: 1, Opcode: 42, Body: [1] }), "队员掉线后剩余成员可正常退场");
                }
                else
                {
                    foreach (var rows in result.Values) CheckRoster(rows, 27);
                    That(result.Values.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 27 })), "结算返回选图使四人保持同一阶段");
                    alpha.Send(132);
                    That(All(alpha).Values.All(rows => rows.Any(p => p is { Kind: 0, Opcode: 24 })), "队长取消选图，全员返回城镇");
                }
                Console.WriteLine($"INFO 组队{count}人真实消耗品拾取槽断言命中 {consumablePickups} 次（随机掉落；0次不代表覆盖）");
                return;
            }
            var next = maze.StaticTopology.Links!.SelectMany(link => new[] { (link.First, link.Second), (link.Second, link.First) })
                .Where(edge => edge.Item1 == point).Select(edge => edge.Item2).OrderBy(p => visited.Contains(p)).First();
            byte[] move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
            var moving = room == 0 ? alpha : beta;
            moving.Send(45, move);
            var moved = All(moving);
            map = moved[alpha].Single(p => p is { Kind: 0, Opcode: 29 }).Body;
            foreach (var rows in moved.Values)
                Bytes(map, rows.Single(p => p is { Kind: 0, Opcode: 29 }).Body, "队员触门使全员进入同一下一房间");
        }
        throw new InvalidOperationException("FAIL 共享普通副本未在有界路线内完成");

        void SelectFrame(int frame, bool dungeon)
        {
            alpha.Send(1573, Build(w => { w.Write(0); w.Write(frame); w.Write(new byte[36]); }));
            var updates = All(alpha);
            That(updates[alpha].Any(p => p is { Kind: 1, Opcode: 1573 } && p.Body[0] == 1),
                "组队中选择真实账号边框成功，副本不被城镇门禁误拒绝");
            CheckFrameUpdates(updates, frame, dungeon);
        }

        void CheckFrameUpdates(Dictionary<TcpPeer, WirePacket[]> updates, int frame, bool dungeon)
        {
            foreach (var rows in updates.Values)
            {
                int actor = Array.FindIndex(rows, p => p is { Kind: 0, Opcode: 2 });
                int roster = Array.FindIndex(rows, p => p is { Kind: 0, Opcode: 9 });
                That(actor >= 0 && roster > actor && rows[roster].Body[9] == 2,
                    "真实边框先更新角色公开数据再发送成员名册，不要求退队重组");
                WeaponSkinTcpTests.AssertFrameActor(rows[actor], frame, alpha.Id);
                That(rows.Count(p => p is { Kind: 0, Opcode: 2 }) == 1 &&
                    rows.All(p => p.Kind != 0 || p.Opcode is not (11 or 26 or 153)),
                    "单项边框刷新不发送完整Player、端点、host或100%血蓝");
                int sceneOffset = 132 + checked((int)U32(rows[actor].Body, 123));
                That(rows[actor].Body[sceneOffset] == (dungeon ? 1 : 0), "边框刷新保留真实城镇或副本场景");
                That(rows[actor].Body[^18] == (dungeon ? 0 : 100),
                    "队伍边框及到期的Actor→成员增量保持场景血蓝字段，无需153补包，也不把城镇满血值写进副本");
            }
        }

        void CheckRoster(WirePacket[] rows, ushort transition)
        {
            int roster = Array.FindIndex(rows, p => p is { Kind: 0, Opcode: 9 });
            int lastPlayer = Array.FindLastIndex(rows, p => p is { Kind: 0, Opcode: 2 });
            int endpointBefore = Array.FindIndex(rows, p => p is { Kind: 0, Opcode: 11 });
            int endpointAfter = Array.FindLastIndex(rows, p => p is { Kind: 0, Opcode: 11 });
            int realtime = Array.FindIndex(rows, p => p is { Kind: 0, Opcode: 153 });
            int host = Array.FindLastIndex(rows, p => p is { Kind: 0, Opcode: 26 });
            int scene = Array.FindIndex(rows, p => p.Kind == 0 && p.Opcode == transition);
            That(lastPlayer >= 0 && endpointBefore > lastPlayer && roster > endpointBefore && endpointAfter > roster &&
                realtime > endpointAfter && host > realtime && scene > host,
                "角色重建后先恢复端点供名册立即创建peer，再补发端点、实时信息和主机，最后切换场景");
            var endpoints = rows[endpointBefore].Body;
            Bytes(endpoints, rows[endpointAfter].Body, "名册前后恢复同一批真实端点");
            That(endpoints.Length == 1 + count * 22 && endpoints[0] == count, "端点覆盖所有组队成员");
            for (int slot = 0; slot < count; slot++)
            {
                int offset = 1 + slot * 22;
                That(U16(endpoints, offset) == participants[slot].Id && U32(endpoints, offset + 2) == 0x0100007f &&
                    U32(endpoints, offset + 6) == 0x0100007f &&
                    BinaryPrimitives.ReadUInt16BigEndian(endpoints.AsSpan(offset + 10)) == 30000 + slot &&
                    U32(endpoints, offset + 12) != 0,
                    "名册创建peer前已有对应角色的实际内外地址、端口与非零登录关联账号");
            }
            var body = rows[roster].Body;
            // 独立按S5A9读宽消费完整名册，不能仅验证存在opcode9或复用生产Builder自证。
            That(U16(body) == 1 && U16(body, 2) == 9999 && body[9] == 0, "重绑使用完整有效队伍名册");
            int at = checked(17 + (int)U32(body, 13) + 24);
            That(body[at++] == count, "清场后恢复全部成员而非只有本人");
            for (byte slot = 0; slot < count; slot++, at += 8)
                That(body[at] == slot && U16(body, at + 1) == participants[slot].Id,
                    "每个队伍槽重新绑定原角色ID，包含队长及接收者");
            That(body.Length == at + 10 && body[at] == 0 && body[at + 2] == 0,
                "名册无跨频道附加成员，真实队长仍为槽0且完整消费正文");
        }
    }

    internal static WirePacket[] Drain(TcpPeer peer)
    {
        peer.SendBarrier();
        var rows = new List<WirePacket>();
        for (int i = 0; i < 200; i++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "组队TCP屏障"); return rows.ToArray(); }
            That(packet.Kind == 0 || packet.Opcode is 10 or 11 or 12 or 13 or 14 or 15 or 16 or 29 or 31 or 33 or 34 or 37 or 41 or 43 or 45 or 46 or 69 or 70 or 71 or 72 or 42 or 132 or 905 or 1652 or 1573,
                $"组队批量投影只包含已登记的应答或通知，实际{packet.Kind}/{packet.Opcode}");
            rows.Add(packet);
        }
        throw new InvalidOperationException("FAIL 组队通知超过有界预算");
    }
}
