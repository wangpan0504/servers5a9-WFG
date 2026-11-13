using System.Buffers.Binary;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static async Task LandTeamAsync(TcpTestFixture fixture, TcpPeer captain, TcpPeer member, TownCatalog towns,
        RaidResources resources, LukeRaidClockTests.Clock clock, Dictionary<int, int> symbols, uint key)
    {
        TcpPeer[] peers = [captain, member];
        That(Cross(fixture, captain, towns, 4).All(row => row is not { Kind: 0, Opcode: 9 }),
            "普通攻坚成员先到左侧时单独等待，不提前创建普通队伍");
        ushort party = BoardingParty(Cross(fixture, member, towns, 4), 0, (0, captain.Id), (1, member.Id));
        That(BoardingParty(Read(captain, "natural landing party formed"), 0, (0, captain.Id), (1, member.Id)) == party,
            "不同标记的攻坚团长后到时加入先到普通成员的队伍，由普通成员担任实际队长");
        await FinishLandedPartyAsync(fixture, peers, resources, towns, clock, symbols, key, party, [21, 11]);
    }

    static async Task FinishLandedPartyAsync(TcpTestFixture fixture, TcpPeer[] peers, RaidResources resources, TownCatalog towns,
        LukeRaidClockTests.Clock clock, Dictionary<int, int> symbols, uint key, ushort party, int[] positions, bool defence = false)
    {
        string tickets = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid");
        long landingStart = clock.GetTimestamp();
        var landed = await AwaitLandingAsync(peers, resources, clock, symbols, defence ? (byte)5 : (byte)4);
        if (defence)
        {
            int elapsed = checked((int)clock.GetElapsedTime(landingStart).TotalSeconds);
            int decrease = resources.Script.Headers.Single(row => row.Tag == "[hp decrease]").Values[0].Number;
            int hpKey = resources.Script.Symbols["[slenikon hp]"];
            That(landed.Packets.All(rows => Symbols(rows.Last(row => row is { Kind: 0, Opcode: 583 }).Body)[hpKey] == symbols[hpKey] - elapsed * decrease),
                "右侧队伍自然进入阻击时船体只按原每秒速率扣血，不追加1300漏守罚");
        }
        byte[][] information = landed.Packets.Select(rows => rows.Single(row => row is { Kind: 0, Opcode: 28 }).Body).ToArray();
        byte[][] maps = landed.Packets.Select(rows => rows.Single(row => row is { Kind: 0, Opcode: 29 }).Body).ToArray();
        foreach (var map in maps.Skip(1))
            Bytes(maps[0], map, "不同标记成员自然进入同一物理地图、种子及怪物对象key");
        string runs = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
        clock.Advance(1);
        var repeated = peers.Select(peer => Read(peer, "natural team landing same stop")).ToArray();
        That(repeated.All(rows => rows.All(row => row is not { Kind: 0, Opcode: 27 or 28 or 29 })) &&
            runs == fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") &&
            tickets == fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid"),
            "同次到达不会重复初始化任一成员Run或再次扣取门票");

        var waiting = peers.Select(_ => new List<WirePacket>()).ToArray();
        foreach (var early in peers.SkipLast(1))
        {
            early.Send(37, new byte[8]);
            for (int index = 0; index < peers.Length; index++)
                waiting[index].AddRange(Read(peers[index], "natural team awaiting last loader"));
            That(waiting.All(rows => rows.All(row => row is not { Kind: 0, Opcode: 30 or 785 } && row is not { Kind: 1, Opcode: 37 })),
                "实际队长及较早成员完成37仍等待最后一人，不提前发送任一成员加载许可");
        }
        peers[^1].Send(37, new byte[8]);
        var loaded = new List<WirePacket>[peers.Length];
        // 最后一个加载请求的本地屏障完成后，才读取其它连接的共享加载投影。
        loaded[^1] = Read(peers[^1], "natural team last loader completed");
        for (int index = 0; index < peers.Length - 1; index++)
            loaded[index] = Read(peers[index], "natural team loaded");
        foreach (var rows in loaded)
        {
            Ack(rows, 37, [1], "原共享Run在全员37后回复加载成功");
            That(rows.Count(row => row is { Kind: 0, Opcode: 30 }) == 1,
                "最后一名成员加载后只释放一次本人的共享加载屏障");
            var time = rows.Single(row => row is { Kind: 0, Opcode: 785 });
            That(time.Body.Length == 4 && U32(time.Body) == resources.EntryCatalog!.Find(landed.Dungeon)!.LimitTimeSeconds - 1 &&
                rows.IndexOf(time) > rows.FindIndex(row => row is { Kind: 0, Opcode: 30 }),
                "全员加载后785独立初始化单图HUD，已等待的一秒不会从DGN总期限中补回");
        }
        peers[0].Send(37, new byte[8]);
        That(peers.SelectMany(peer => Read(peer, "prey repeated loaded timer")).All(row => row is not { Kind: 0, Opcode: 785 }),
            "重复37不重新启动单图显示时钟");
        for (int index = 0; index < peers.Length; index++)
            That(landed.Packets[index].Concat(loaded[index]).Any(row => row is { Kind: 1, Opcode: 1361 } &&
                row.Body[1] == 2 && peers.All(peer => RosterLocations(row.Body).GetValueOrDefault(peer.Id) == landed.From)),
                "全员战略地点快照共同指向真实入图的同一节点");

        if (!defence) SharedCoins(fixture, peers, resources.EntryCatalog!.Find(landed.Dungeon)!.CoinLimit!.Value);

        var packets = peers.Select(_ => new List<WirePacket>()).ToArray();
        var reports = peers.Select((_, index) => new AntonTcpProbe.AntonProbeReport { Observe = packets[index].Add }).ToArray();
        for (int index = 0; index < peers.Length; index++)
            AntonTcpProbe.Note(reports[index], landed.Packets[index].Concat(repeated[index]).Concat(waiting[index]).Concat(loaded[index]).ToList());
        string uncleared = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id");
        peers[0].Send(46, DungeonSettlementProtocolTests.Report(peers[0].Id, 80));
        foreach (var (peer, index) in peers.Select((peer, index) => (peer, index)))
        {
            var premature = Read(peer, "uncleared raid native result rejected");
            AntonTcpProbe.Note(reports[index], premature);
            That(premature.All(row => row.Opcode is not (24 or 115 or 31 or 600 or 601)),
                "击杀前合法46不能返回城镇或伪造共享清图与奖励");
        }
        That(uncleared == fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id"),
            "击杀前46不改正式副本进度或攻坚资格");
        ClearLandedTeam(peers, resources.EntryCatalog!, landed.Dungeon, information[0], maps[0], reports, clock);
        for (int index = 0; index < peers.Length; index++)
            AntonTcpProbe.Note(reports[index], Read(peers[index], "natural team final shared state"));
        int hp = resources.Script.Symbols[resources.Script.Phases[0].Dungeons.Single(row => row.Id == landed.Dungeon).HpSymbol];
        var finalSymbols = reports.Select(report => Symbols(report.Symbols.Last())).ToArray();
        int area = resources.Script.Symbols[$"[area_{landed.From - 300}]"];
        int type = resources.Script.Symbols[$"[area_{landed.From - 300}_dgn_type]"];
        That(finalSymbols.All(state => defence ? state[area] == 0 && state[type] == 7 : state[hp] == 0) && finalSymbols.Skip(1).All(state =>
            finalSymbols[0].OrderBy(row => row.Key).SequenceEqual(state.OrderBy(row => row.Key))),
            "共享39死亡提交后全员状态一致，普通图清HP、阻击直接移除AREA并发空类型7");
        That(peers.Select((peer, index) => reports[index].Opcode115 == 1 && reports[index].Opcode31 == 1 &&
                packets[index].Any(row => row is { Kind: 0, Opcode: 38 }) &&
                packets[index].Any(row => row is { Kind: 0, Opcode: 9 } && row.Body[9] == 3 && U16(row.Body, 4) == party) &&
                fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[59L, 3L]]).All(value => value),
            "共享Run只向实际成员各发一次死亡许可，正式退场回鸟背中间并解除原动态队伍");
        var roster = ReadRoster(peers[^1], key);
        That(peers.Select((peer, index) => roster[peer.Id].Squad == positions[index] / 10 &&
                roster[peer.Id].Position == positions[index] && roster[peer.Id].Clears == 1).All(value => value) &&
            tickets == fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[0L]],
            "跨标记自然组队与共享清图不改原标记、不重扣门票、不伪造阶段奖励");
        if (defence)
            VerifyClearedDefenceOrbit(fixture, peers, resources, towns, clock, landed.From, landed.Dungeon, area, type);
    }

    static void SharedCoins(TcpTestFixture fixture, TcpPeer[] peers, int limit)
    {
        That(limit == 4, "本次原普通DGN声明共享复活额度4，流程视频数值不覆盖资源");
        for (int used = 0; used <= limit; used++)
        {
            var peer = peers[used == limit ? peers.Length - 1 : used % peers.Length];
            var ordered = new[] { peer }.Concat(peers.Where(member => member != peer)).ToArray();
            long before = Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]);
            peer.Send(40, [0x74, 0, 4, 1, 0, 0]);
            var deaths = ordered.Select(member => Read(member, "prey shared coin death")).ToArray();
            That(deaths.All(rows => rows.Any(row => row is { Kind: 0, Opcode: 32 } && U16(row.Body) == peer.Id && row.Body[2] == 0)),
                "真实40将本人死亡同步给当前共享Run成员");
            peer.Send(41, Build(writer => writer.Write(peer.Id)));
            var replies = ordered.Select(member => Read(member, "prey shared coin use")).ToArray();
            if (used == limit)
            {
                Ack(replies[0], 41, [0, 1, 0], "小队已满4次，第5次即使本人有币且个人次数未满也拒绝");
                That(Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]) == before &&
                    replies.All(rows => rows.All(row => row.Opcode != 14 && row.Opcode != 778 &&
                        row is not { Kind: 0, Opcode: 32, Body: [_, _, 1, ..] })),
                    "共享上限拒绝不扣库存币、不广播复活或卢克阶段池");
                break;
            }
            Ack(replies[0], 41, Build(writer => { writer.Write((byte)1); writer.Write(peer.Id); writer.Write((byte)0); }),
                "真实41只提交一次本人扣币并回复原ACK");
            That(Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]) == before - 1 &&
                replies[0].Any(row => row is { Kind: 0, Opcode: 14 }) && replies.All(rows => rows.All(row => row.Opcode != 778)),
                "交替成员复活只扣各自原库存币，不借用卢克778");
            foreach (var reply in replies)
            {
                var status = reply.Last(row => row is { Kind: 0, Opcode: 634 }).Body;
                using var reader = new BinaryReader(new MemoryStream(status));
                That(reader.ReadByte() == 1, "当前两成员只有一条共享Run战况");
                reader.ReadUInt16();
                var members = Enumerable.Range(0, reader.ReadByte()).Select(_ => reader.ReadUInt16()).ToArray();
                That(members.Order().SequenceEqual(peers.Select(member => member.Id).Order()) && reader.ReadUInt32() == used + 1,
                    "634向每个成员同步该Run的权威共享已用数");
                reader.ReadUInt32();
                int values = reader.ReadByte();
                reader.ReadBytes(values * 4);
                That(reader.BaseStream.Position == reader.BaseStream.Length, "634保留上下文与完整血量尾部，无截断");
            }
            peer.Send(41, Build(writer => writer.Write(peer.Id)));
            That(ordered.SelectMany(member => Read(member, "prey duplicate coin")).All(row => row.Opcode is not (41 or 634 or 14)) &&
                Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]) == before - 1,
                "重复41不重复扣币或增加共享计数，连接仍可继续清图");
        }
    }

    static void ClearLandedTeam(TcpPeer[] peers, RaidDungeonCatalog catalog, int dungeon, byte[] info, byte[] initialMap,
        AntonTcpProbe.AntonProbeReport[] reports, LukeRaidClockTests.Clock clock)
    {
        byte[] map = initialMap;
        var path = AntonTcpProbe.Route(catalog, dungeon, info[7], map[0], map[1], info[8], info[9]);
        ushort bossKey = 0; byte bossLevel = 0;
        for (int step = 0; step < path.Length; step++)
        {
            if (step != 0)
            {
                var move = new byte[100]; move[0] = checked((byte)path[step].X); move[1] = checked((byte)path[step].Y);
                peers[0].Send(45, move);
                var moved = peers.Select(peer => Read(peer, "natural team route move")).ToArray();
                for (int index = 0; index < peers.Length; index++) AntonTcpProbe.Note(reports[index], moved[index]);
                map = moved[0].Single(row => row is { Kind: 0, Opcode: 29 }).Body;
                foreach (var rows in moved.Skip(1))
                    Bytes(map, rows.Single(row => row is { Kind: 0, Opcode: 29 }).Body,
                        "实际队长切房后全员继续共用同一地图、种子及怪物key");
                clock.Advance(1);
                foreach (var peer in peers) peer.Send(37, new byte[8]);
                var roomLoads = peers.Select(peer => Read(peer, "natural team route load request completed")).ToArray();
                for (int index = 0; index < peers.Length; index++)
                {
                    // 全部发送者先各自完成请求屏障，再收齐可能晚于首个成员屏障的共享响应。
                    var loaded = roomLoads[index];
                    loaded.AddRange(Read(peers[index], "natural team route loaded"));
                    Ack(loaded, 37, [1], "共享路线下一房正常完成全员加载");
                    var time = loaded.Single(row => row is { Kind: 0, Opcode: 785 });
                    That(time.Body.Length == 4 && U32(time.Body) == catalog.Find(dungeon)!.LimitTimeSeconds - 1 - step,
                        "换房785仅恢复同一Run持续递减的剩余时限，不刷新入场期限");
                    AntonTcpProbe.Note(reports[index], loaded);
                }
            }
            AntonTcpProbe.ClearRoom(peers[0], map, reports[0], out ushort key, out byte level, step + 1 == path.Length,
                peers.Length > 1 ? peers[1] : null, peers.Length > 1 ? reports[1] : null, requireSettlementOrder: true);
            for (int index = 2; index < peers.Length; index++)
            {
                var deaths = Read(peers[index], "natural team additional death witness");
                AntonTcpProbe.Note(reports[index], deaths);
                ushort[] expected = Enumerable.Range(0, map[18]).Where(row => map[19 + row * 22 + 21] != 1 && map[19 + row * 22 + 13] != 9)
                    .Select(row => U16(map, 19 + row * 22 + 6)).Order().ToArray();
                That(deaths.Where(row => row is { Kind: 0, Opcode: 38 }).Select(row => U16(row.Body)).Order().SequenceEqual(expected),
                    "额外成员见证本房同一组怪物key的真实39死亡广播");
                if (step + 1 == path.Length)
                {
                    int death = deaths.FindIndex(row => row is { Kind: 0, Opcode: 38 } && U16(row.Body) == key);
                    int boss = deaths.FindIndex(row => row is { Kind: 0, Opcode: 115 });
                    int clear = deaths.FindIndex(row => row is { Kind: 0, Opcode: 31 });
                    That(death >= 0 && boss > death && clear > boss && U16(deaths[boss].Body, 2) == key,
                        "额外成员也按最终怪物38、Boss确认115、单次31的顺序完成共享Run");
                }
            }
            if (step + 1 == path.Length) { bossKey = key; bossLevel = level; }
        }
        var confirmation = new byte[39];
        BinaryPrimitives.WriteUInt16LittleEndian(confirmation.AsSpan(2), bossKey);
        BinaryPrimitives.WriteUInt32LittleEndian(confirmation.AsSpan(13), bossLevel);
        BinaryPrimitives.WriteUInt64LittleEndian(confirmation.AsSpan(17), 504);
        foreach (var sender in peers)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(confirmation, sender.Id);
            sender.Send(117, confirmation);
            for (int index = 0; index < peers.Length; index++)
            {
                var replay = Read(peers[index], "natural team confirmation replay");
                AntonTcpProbe.Note(reports[index], replay);
                That(replay.All(row => row is not { Kind: 0, Opcode: 115 or 31 }),
                    "任一成员117重放不能重复签发共享Run死亡许可");
            }
        }
        foreach (var peer in peers) peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 80));
        for (int index = 0; index < peers.Length; index++)
        {
            var returned = Read(peers[index], "natural team native result return to middle");
            AntonTcpProbe.Note(reports[index], returned);
            That(returned.Any(row => row is { Kind: 0, Opcode: 24, Body: [59, 3, ..] } && U16(row.Body, 4) == peers[index].Id) &&
                returned.All(row => row.Opcode is not (34 or 35 or 115 or 31 or 600 or 601)),
                "每名成员清图后的原53B46返回本人鸟背中间，不重复死亡许可或普通及阶段奖励");
        }
    }
}
