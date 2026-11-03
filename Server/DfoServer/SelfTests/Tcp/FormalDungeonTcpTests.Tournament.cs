using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    sealed record TournamentWireOpponent(int Round, ushort Key, int Code, byte Level, byte Type);

    // 仅合成原生格式战斗报告；对手身份必须来自实际386，不能遍历服务端Run冒充网络战斗。
    public static async Task RunTournamentAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        TournamentSettlementTests.RunResources(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "dungeon-tournament"), resources: resources, sharedResourcePath: true);
        await fixture.StartAsync();
        foreach (var sample in new[]
        {
            (Dungeon: 120, Map: 17100, Quest: 0), (Dungeon: 121, Map: 17101, Quest: 0),
            (Dungeon: 123, Map: 95265, Quest: 5302), (Dungeon: 124, Map: 95266, Quest: 5304)
        })
        {
            var definition = dungeons.Get(sample.Dungeon);
            That(DungeonEntryPlan.TryCreate(dungeons,
                new Character(1, 0, "TournamentMap", 0, new(38, 1, 1, 1), null) { Level = 90 },
                checked((uint)sample.Dungeon), 0, 2, out var mapEntry, out var mapReason),
                "大会地图由真实入场计划解析：" + mapReason);
            var resourceMap = mapEntry!.StartMap;
            That(resourceMap.Id == sample.Map, "大会使用候选资源声明的单图");
            var tournamentSource = rooms.ReadTournamentSource(resourceMap);
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == sample.Dungeon));
            var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options
                .Where(option => option.DungeonId == sample.Dungeon).ToArray();
            var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(new[] { 3145, 4873 }).ToHashSet();
            if (sample.Quest == 0)
                done.Add(options.First(option => !option.InProgress && option.QuestId > 0).QuestId);
            else
            {
                That(options.Any(option => option.InProgress && option.QuestId == sample.Quest),
                    "魂大会使用真实进行中二觉入口，不能以完成位替代");
                done.UnionWith(resources.Quests.Get(sample.Quest).Definition!.PrerequisiteGroups.SelectMany(group => group));
            }
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, checked((ushort)(walk.X + walk.Width / 2)),
                checked((ushort)(walk.Y + walk.Height / 2)));
            string account = "tournament" + sample.Dungeon;
            Character created;
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                var candidate = CharacterCreation.Prepare("Tour" + sample.Dungeon, resources.Characters.Get(0),
                    position, position, resources.Learning.Get);
                var repository = new CharacterRepository(fixture.Database, resources.Quests);
                created = repository.Create(owner, candidate with
                {
                    Level = sample.Quest == 0 ? (byte)95 : (byte)90, AcceptedStoryLevel = 95,
                    GrowType = sample.Quest == 0 ? (byte)1 : (byte)19, CompletedQuests = done.ToArray(),
                    ActiveMainQuests = sample.Quest == 0 ? [] : [new(0, checked((ushort)sample.Quest), 1) { AcceptanceId = Guid.NewGuid() }],
                    InitialLearning = SkillPointLedger.Synchronize(0, sample.Quest == 0 ? (byte)1 : (byte)19,
                        sample.Quest == 0 ? (byte)95 : (byte)90, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(sample.Quest == 0 ? 95 : 90) }, resources.Learning)
                });
                if (definition.RequiredItem is { Flag: not 0, Count: > 0 } ticket)
                {
                    var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                        resources.ItemResources.EpicPieces);
                    created = InventoryService.Commit(repository, owner, created,
                        inventory.Grant(created, [new(ticket.ItemId, ticket.Count * 2)]));
                }
            }
            if (definition.RequiredItem is { Flag: not 0, Count: > 0 } initialTicket)
                That(ItemCount(initialTicket.ItemId) == initialTicket.Count * 2,
                    "大会门票在真实入场前已足额持久化，不能把发放或SQL读取错误当作入场扣费");
            using var peer = Open(fixture, account);
            peer.Select(created.Slot, prepared: true, resumeTown: true, room: resources.Towns.IsRoom(position, done));
            SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, created));
            var request = Selection(2);
            BinaryPrimitives.WriteUInt32LittleEndian(request, checked((uint)sample.Dungeon));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 0);
            peer.Send(16, request);
            byte[] information = peer.Expect(0, 28).Body;
            That(information.Length == 36 && U32(information, 0) == sample.Dungeon && information[4] == 2 &&
                information[8] == 0 && information[9] == 0,
                "大会先发布完整28绑定DGN、难度及原单图Boss格，再发布386/387");
            var opponents = ReadTournamentOpponents(peer.Expect(0, 386).Body, sample.Dungeon, tournamentSource);
            byte[] map = peer.Expect(0, 387).Body;
            That(map.Length == 15 && map[0] == 0 && map[1] == 0 && map[6] == 0 && map[7] == 1 &&
                U32(map, 8) == sample.Map && map.AsSpan(12).IndexOfAnyExcept((byte)0) == -1,
                "大会387使用候选真实单图及完整15B，不能退化为普通29");
            var entryNotices = TournamentDrain(peer);
            That(entryNotices.All(packet => packet is { Kind: 0, Opcode: 14 or 550 }),
                "大会入场后只允许真实已提交的门票与次数通知");
            if (definition.RequiredItem is { Flag: not 0, Count: > 0 } required)
            {
                // 大会不走EntryItem，但已装配的次数系统仍按required item同事务扣票。
                int charged = required.Count;
                That(ItemCount(required.ItemId) == required.Count * 2 - charged &&
                    entryNotices.Any(packet => packet.Opcode == 14) == (charged > 0),
                    "大会门票经现有次数系统只扣一次，并发布对应库存通知");
            }
            Load(peer);
            string experienceBefore = ExperienceState();

            // 先报告末轮，并尝试提前46：仅准许当前轮死亡，不能借末轮Boss跳过前三轮。
            var last = opponents[^1];
            string beforeFuture = Ledgers();
            peer.Send(39, Death(last.Key, peer.Id, last.Level));
            peer.Send(46, DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99));
            That(TournamentDrain(peer).Length == 0 && Ledgers() == beforeFuture,
                "真实39未来轮及提前46均静默拒绝，不发38/31/388或保存奖励回执");
            foreach (var opponent in opponents)
            {
                byte[] report = Death(opponent.Key, peer.Id, opponent.Level);
                peer.Send(39, report);
                byte[] death = peer.Expect(0, 38).Body;
                That(death.Length == 9 && U16(death, 0) == opponent.Key && U16(death, 2) == 0 &&
                    death.AsSpan(4).SequenceEqual(new byte[] { 0, 0, 255, 0, 0 }),
                    "实际386当前轮对手39经Handler后得到完整零掉落38，保持原生死亡尾部");
                bool terminal = opponent == last;
                var notices = TournamentDrain(peer);
                That(notices.All(packet => packet is { Kind: 0, Opcode: 31 or 115 } ||
                    packet is { Kind: 0, Opcode: 37 } && sample.Quest != 0 ||
                    packet is { Kind: 1, Opcode: 33, Body.Length: 7 } && sample.Quest != 0 &&
                    packet.Body[0] == 1 && U16(packet.Body, 1) == sample.Quest && U32(packet.Body, 3) == 0),
                    "逐轮死亡不补造Actor或普通奖励页，任务仅同步本次二觉条件");
                CheckZeroExperienceNotices(notices);
                That(notices.Count(packet => packet.Opcode == 31) == (terminal ? 1 : 0) &&
                    notices.Count(packet => packet.Opcode == 115) == (terminal ? 1 : 0),
                    "仅全部合法轮次结束后发送一次115与31许可");
                foreach (var packet in notices.Where(packet => packet.Opcode is 31 or 115))
                    Bytes(packet.Body, packet.Opcode == 31 ? [] : [1, 1, (byte)last.Key, (byte)(last.Key >> 8)],
                        "大会清图许可绑定最终真实对手key");
                string once = Ledgers();
                peer.Send(39, report);
                That(TournamentDrain(peer).Length == 0 && Ledgers() == once,
                    "同轮及末轮重复39不重发38或清图许可，不重复提交成长、结算或翻牌");
            }
            That(SettlementCount() == 0, "末轮死亡只启用结算，真实46之前没有结算资产回执");
            // 复用本次现场53B报告的完整统计尾，只替换隔离角色身份与正文检查值。
            byte[] result = Convert.FromHexString("00C400011C010F0000006363000000000D000000F1FFFFFFE0490000142600003C73000031280000090000000000000000420DA6DA");
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), peer.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(result.Length - 4),
                DfoServer.Network.Parsers.ClientBodyChecksum.Compute(result.AsSpan(0, result.Length - 4)));
            peer.Send(46, result);
            var settled = TournamentDrain(peer);
            That(settled.Count(packet => packet is { Kind: 0, Opcode: 388 }) == 1 &&
                settled.All(packet => packet is { Kind: 0, Opcode: 388 } ||
                    packet is { Kind: 0, Opcode: 37 } && sample.Quest != 0),
                "大会真实46呈现388，90级零经验同步原余额，不发送普通34/35");
            CheckZeroExperienceNotices(settled);
            byte[] clear = settled.Single(packet => packet.Opcode == 388).Body;
            var cards = ReadTournamentCards(clear, definition.TournamentRewards!);
            That(SettlementCount() == 1 && ClaimCount() == 0,
                "388对应唯一结算回执，选牌前没有大会资产领取回执");
            if (sample.Quest != 0)
                That(ExperienceState() == experienceBefore && U32(clear, 1) == 0,
                    "90级魂大会四轮死亡和结算均不加经验，真实零经验46仍成功呈现388");
            string afterSettlement = Ledgers();
            peer.Send(46, result);
            var replay = TournamentDrain(peer);
            That(replay.Count(packet => packet is { Kind: 0, Opcode: 388 }) == 1 &&
                replay.All(packet => packet is { Kind: 0, Opcode: 388 } ||
                    packet is { Kind: 0, Opcode: 37 } && sample.Quest != 0) &&
                replay.Single(packet => packet.Opcode == 388).Body.SequenceEqual(clear) && Ledgers() == afterSettlement,
                "相同46回放冻结388而不重复结算或重抽卡牌");
            CheckZeroExperienceNotices(replay);

            peer.Send(457);
            peer.Expect(1, 457, [1, 1, 255, 255, 255, 1, 255, 255, 255]);
            long gold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id).Single()[0]);
            int item = cards[1][0].Item;
            long itemBefore = item > 0 ? ItemCount(item) : 0;
            string itemsBefore = fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
            peer.Send(458, [0, 0]);
            peer.Expect(1, 458, [1, 2, 0, 255, 2, 255, 255]);
            That(TournamentDrain(peer).Length == 0 && ClaimCount() == 0,
                "457握手后第一组选牌仅更新归属，不能提前发奖");
            peer.Send(458, [1, 0]);
            byte[] chosen = peer.Expect(1, 458, [1, 2, 0, 255, 2, 0, 255]).Body;
            int inventoryNotices = DrainCardInventory(peer);
            bool emptyCards = cards.All(row => row.All(card => card.Item == -1 && card.Count == 0));
            That((emptyCards ? inventoryNotices == 0 : inventoryNotices > 0) && ClaimCount() == 1 &&
                Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id).Single()[0]) == gold + cards[0][0].Count &&
                (emptyCards ? fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id) == itemsBefore :
                    ItemCount(item) == itemBefore + cards[1][0].Count),
                "两组真实458后只提交原牌奖励：普通大会金币曲玉到账，魂空牌不制造资产且领取回执唯一");
            string afterClaim = Ledgers();
            peer.Send(458, [1, 0]);
            peer.Expect(1, 458, chosen);
            That(TournamentDrain(peer).Length == 0 && ClaimCount() == 1 && Ledgers() == afterClaim,
                "重复458只回放归属，不重复发奖或库存通知");

            long ItemCount(int id) => Convert.ToInt64(fixture.Query("""
                SELECT COALESCE(SUM(json_extract(i.item_core,'$.Value')),0)
                FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid
                WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1
                """, created.Id, id).Single()[0]);
            long SettlementCount() => Convert.ToInt64(fixture.Query(
                "SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0", created.Id).Single()[0]);
            long ClaimCount() => Convert.ToInt64(fixture.Query(
                "SELECT count(*) FROM character_dungeon_tournament_rewards WHERE character_id=$p0", created.Id).Single()[0]);
            string ExperienceState() => fixture.Snapshot("SELECT level,experience FROM characters WHERE id=$p0", created.Id) +
                fixture.Snapshot("SELECT proficiency_level,proficiency_experience FROM character_dungeon_state WHERE character_id=$p0", created.Id);
            void CheckZeroExperienceNotices(IReadOnlyList<WirePacket> packets)
            {
                var growth = packets.Where(packet => packet is { Kind: 0, Opcode: 37 }).ToArray();
                That(growth.Length == (sample.Quest == 0 ? 0 : 1) && growth.All(packet =>
                    packet.Body.Length == 91 && packet.Body[0] == 90 &&
                    BinaryPrimitives.ReadUInt64LittleEndian(packet.Body.AsSpan(1)) == created.Learning.Experience),
                    "魂大会未满级成长通知只同步原经验，满级普通大会不补发37");
            }
            string Ledgers() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY event_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0", created.Id) +
                fixture.Snapshot("SELECT * FROM character_dungeon_tournament_rewards WHERE character_id=$p0", created.Id);
        }
        Console.WriteLine($"PASS {Count - checks} 四类素喃大会真实TCP逐轮39/38、未来轮拒绝、重复幂等、388及457/458领奖（合成战斗，非客户端实机）");
    }

    static TournamentWireOpponent[] ReadTournamentOpponents(byte[] body, int dungeon, TournamentSource source)
    {
        That(body.Length >= 6 && U32(body, 0) == dungeon && body[4] == 2 && body[5] == source.PartyCount,
            "实际386头包含请求DGN、固定难度及候选对手表人数");
        int party = body[5], cursor = 6;
        That(party is 1 or 2, "大会386人数有界");
        var expected = new List<int[]>();
        for (int round = 0; round < 4; round++)
        {
            Need(2);
            That(body[cursor++] == round + 1 && body[cursor++] == (16 >> round), "386四轮对阵队数16/8/4/2");
            int teams = 16 >> round, player = -1;
            var codes = new int[teams][];
            for (int team = 0; team < teams; team++)
            {
                Need(1 + 6 * party);
                That(body[cursor++] == team, "386每队位置连续且不越界");
                codes[team] = new int[party];
                for (int member = 0; member < party; member++)
                {
                    int code = checked((int)U32(body, cursor));
                    codes[team][member] = code;
                    That(code == 0 ? U16(body, cursor + 4) == 0 : source.Enemies.Any(enemy => enemy.Code == code),
                        "386玩家使用code0战力0，对手模板来自候选资源");
                    cursor += 6;
                }
                if (codes[team].All(code => code == 0))
                {
                    That(player == -1, "每轮386恰有一支玩家队");
                    player = team;
                }
                else That(codes[team].All(code => code != 0), "对手队不能混入玩家标志");
            }
            That(player >= 0, "每轮386均保留玩家队");
            expected.Add(codes[player ^ 1]);
        }
        var opponents = new List<TournamentWireOpponent>();
        for (int round = 0; round < 4; round++)
        {
            Need(1);
            That(body[cursor++] == round + 1, "386独立四组对手轮号完整");
            for (int member = 0; member < party; member++)
            {
                Need(8);
                var opponent = new TournamentWireOpponent(round + 1, U16(body, cursor),
                    checked((int)U32(body, cursor + 2)), body[cursor + 6], body[cursor + 7]);
                That(opponent.Key is > 0 and < ushort.MaxValue && opponent.Code == expected[round][member] &&
                    opponent.Level > 0 && opponent.Type == (source.Kind == TournamentEnemyKind.AiCharacter
                        ? round == 3 ? 8 : 5 : round == 3 ? 3 : 0),
                    "386实际对象key、模板、等级、品级与本轮对阵一致");
                opponents.Add(opponent);
                cursor += 8;
            }
        }
        That(cursor == body.Length && opponents.Select(row => row.Key).Distinct().Count() == opponents.Count,
            "386完整消费且四轮对象key独立，不能固有复用前轮key");
        return opponents.ToArray();

        void Need(int length) => That(cursor <= body.Length - length, "386字段不得越过实际包体");
    }

    static (int Item, int Count)[][] ReadTournamentCards(byte[] body, DungeonTournamentRewards rewards)
    {
        That(body.Length >= 8 && body[0] == 4 && body[5] == 0, "388声明完成四轮且无额外未知条目");
        int cursor = 6;
        var rows = new (int Item, int Count)[2][];
        for (int row = 0; row < 2; row++)
        {
            That(cursor < body.Length && body[cursor++] == 2, "388普通与冠军各两张牌");
            rows[row] = new (int, int)[2];
            for (int card = 0; card < 2; card++)
            {
                That(cursor <= body.Length - 10, "388每张牌完整10B");
                int item = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(cursor));
                int count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(cursor + 4));
                var weights = rewards.Cards[row == 0 ? 4 : 5];
                That((weights.Empty == weights.Total ? item == -1 && count == 0 :
                    (row == 0 ? item == 0 : rewards.Items.Any(candidate => candidate.ItemId == item)) && count > 0) &&
                    U16(body, cursor + 8) == 0,
                    "388牌面遵守候选奖励池，魂空牌保留-1/0及两个尾零，不补造金币曲玉");
                rows[row][card] = (item, count);
                cursor += 10;
            }
        }
        That(cursor == body.Length, "388两组完整消费，不允许截断或额外字段");
        return rows;
    }

    static WirePacket[] TournamentDrain(TcpPeer peer)
    {
        peer.SendBarrier();
        var packets = new List<WirePacket>();
        for (int i = 0; i < 128; i++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 })
            {
                Bytes(packet.Body, [1], "大会每阶段经真实命令屏障确认处理完成");
                return packets.ToArray();
            }
            packets.Add(packet);
        }
        throw new InvalidDataException("大会阶段通知超过有界预算");
    }
}
