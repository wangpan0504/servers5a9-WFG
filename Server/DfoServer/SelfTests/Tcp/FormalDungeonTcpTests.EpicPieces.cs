using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunEpicPiecesAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        var items = resources.ItemResources;
        var definition = SelfTestCatalogs.Dungeons(resources.Archive).Get(101);
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world] is { HellDungeon: true } selection &&
            selection.Options.Any(option => option.DungeonId == 101 && option.QuestId == -1));
        var world = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value];
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(world.HellQuests).ToHashSet();
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "epic-piece-drops"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var roles = new List<(string Account, Character Role)>();
        for (int index = 0; index < 3; index++)
        {
            string account = "epicpiece" + index;
            using var owner = new AccountOwners().Acquire(account, () => { });
            var candidate = CharacterCreation.Prepare("EpicGround" + index, resources.Characters.Get(0), position, position, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            var created = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                CompletedQuests = done.Order().ToArray(), InitialLearning = learning });
            var book = created.Inventory.EpicPieces;
            if (index == 0)
                foreach (var entry in items.EpicPieces.Entries) book = book.Set(items.EpicPieces, entry.Index, 10);
            int ticket = world.HellItemConditions.First();
            var gear = InventoryRewardGrantService.Create(items.Items.Get(101010912));
            var material = InventoryRewardGrantService.Create(items.Items.Get(3167));
            var contents = created.Inventory.Items.Where(item => item.Position.List != InventoryListType.Main)
                .Concat(Enumerable.Range(9, 56).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot), gear)))
                .Concat(Enumerable.Range(121, 56).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot),
                    slot == 121 ? InventoryRewardGrantService.Create(items.Items.Get(ticket), 1000) : material))).ToImmutableArray();
            if (index > 0)
                contents = contents.Where(item => item.Position != new InventorySlot(InventoryListType.Equipment, 12))
                    .Append(new(0, new(InventoryListType.Equipment, 12),
                        InventoryRewardGrantService.Create(items.Items.Get(401000048)) with { SealFlag = 0 })).ToImmutableArray();
            if (index == 2)
                contents = contents.Select(item => item.Position == new InventorySlot(InventoryListType.Main, 122)
                    ? item with { Core = InventoryRewardGrantService.Create(items.Items.Get(490008657)) } : item).ToImmutableArray();
            var seeded = created.Inventory with { Items = contents, Gold = 200000, EpicPieces = book,
                AccountWallet = created.Inventory.AccountWallet.Set(CurrencyKind.EpicSoul, index == 0 ? 0 : 2) };
            roles.Add((account, InventoryService.Commit(repository, owner, created,
                new(true, 0, new(created.Inventory, seeded, "epic-piece-tcp-fixture", new(created))))));
        }
        string? previousAppearance = Environment.GetEnvironmentVariable("DFO_EPIC_PIECE_SHOP_APPEAR_PERCENT");
        string? previousRate = Environment.GetEnvironmentVariable("DFO_HELL_EPIC_FRAGMENT_PERMILLE");
        try
        {
            Environment.SetEnvironmentVariable("DFO_EPIC_PIECE_SHOP_APPEAR_PERCENT", "100");
            Environment.SetEnvironmentVariable("DFO_HELL_EPIC_FRAGMENT_PERMILLE", "1000");
            await fixture.StartAsync();
            for (int scenario = 0; scenario < roles.Count; scenario++)
            {
                var (account, role) = roles[scenario];
                byte[] selectPiece = Convert.FromHexString("A62C9D00");
                const int targetPiece = 10300582;
                That(items.EpicPieces.ByPiece[targetPiece].EquipmentId == 104020146,
                    "实际ARAD选择样本10300582是STK碎片，piece list对应装备104020146");
                using var peer = TcpPeer.Open(fixture, account);
                peer.Select(role.Slot, prepared: true, resumeTown: true, room: false);
                peer.Send(818, selectPiece); peer.Expect(1, 818, [0, 19, 0]);
                peer.Send(819); peer.Expect(1, 819, [0, 19, 0]);
                peer.Send(820, [1]); peer.Expect(1, 820, [0, 19, 0]);
                peer.Barrier();
                SelectMap(peer);
                var selection = new byte[25];
                BinaryPrimitives.WriteInt32LittleEndian(selection, definition.Id);
                selection[7] = 1; selection[9] = selection[10] = byte.MaxValue;
                peer.Send(16, selection);
                var information = peer.Expect(0, 28).Body;
                var map = peer.Expect(0, 29).Body;
                peer.Barrier(packet => That(packet is { Kind: 0, Opcode: 14 or 679 }, "真实入场只追加票据差量及可能的深渊模板表"));
                var maze = definition.Mazes.Single(row => row.SourceOrdinal == information[7]);
                var path = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), maze.HellParty!.Room);
                int fragments = 0, fragmentDeaths = 0;
                bool shown = false;
                for (int room = 0; room < path.Length; room++)
                {
                    if (room != 0)
                    {
                        var move = new byte[100]; move[0] = (byte)path[room].X; move[1] = (byte)path[room].Y;
                        peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                        peer.Barrier(packet => That(packet is { Kind: 0, Opcode: 679 }, "深渊房间模板通知来自本次真实地图"));
                    }
                    Load(peer); peer.Barrier(Observe);
                    var actors = Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                        .Where(at => map[at + 13] != 9 && map[at + 12] > 0 && map[at + 21] == 0)
                        .OrderBy(at => map[at + 14] == 1 && map[at + 15] == 255 ? 10 + U16(map, at) : 0).ToArray();
                    foreach (int at in actors)
                    {
                        peer.Send(39, Death(U16(map, at + 6), peer.Id, map[at + 12]));
                        var death = peer.Expect(0, 38).Body;
                        peer.Barrier(Observe);
                        var drops = Enumerable.Range(0, U16(death, 2)).Select(index => 4 + index * 131)
                            .Where(offset => items.EpicPieces.ByPiece.ContainsKey(checked((int)U32(death, offset + 6)))).ToArray();
                        if (drops.Length > 0) fragmentDeaths++;
                        foreach (int offset in drops)
                        {
                            uint key = U32(death, offset);
                            int id = checked((int)U32(death, offset + 6)), amount = checked((int)U32(death, offset + 10));
                            var entry = items.EpicPieces.ByPiece[id];
                            var range = DungeonHellDropRules.TryLoad(resources.Archive)!.GradeRange(definition.BasisLevel ?? map[at + 12])!.Value;
                            That(items.Items.Get(entry.EquipmentId).MinimumLevel >= range.Minimum &&
                                items.Items.Get(entry.EquipmentId).MinimumLevel <= range.Maximum && amount == 1,
                                "旧地区深渊碎片按对应装备等级进入PVF引用区间，不按1级STK混池");
                            string physical = Physical(); int balance = Book(account)[entry.Index];
                            var request = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(request, key);
                            peer.Send(43, request);
                            var picked = peer.Expect(0, 39).Body;
                            That(picked.Length == 19 && U32(picked, 0) == key && U16(picked, 16) == ushort.MaxValue,
                                "碎片拾取不占真实Main槽位，完整19B通知只消费原地面key");
                            peer.Expect(0, 1152, Build(writer => { writer.Write((byte)1); writer.Write(1); writer.Write(id); writer.Write(balance + amount); }));
                            peer.Expect(1, 43, [1]); peer.Barrier();
                            That(Physical() == physical && Book(account)[entry.Index] == balance + amount,
                                "满背包装备页与材料页时仍完整入图鉴钱包，UID与物理实例均不变");
                            string saved = fixture.Snapshot();
                            peer.Send(43, request); peer.Expect(1, 43, [0, 19, 0]); peer.Barrier();
                            That(fixture.Snapshot() == saved, "重复地面key不重复增加碎片钱包或资产版本");
                            fragments += amount;
                        }
                    }
                }
                That(fragmentDeaths == 1 && fragments == (map[7] == 1 ? 6 : 5) + scenario && shown,
                    "末波末杀保留原5/6基数，无来源/武器/武器加丹石分别额外0/1/2片，同源随机及钱包拾取完整");
                string beforeChoice = fixture.Snapshot();
                peer.Send(820, [1]); peer.Expect(1, 820, [0, 19, 0]); peer.Barrier();
                peer.Send(818, Build(writer => writer.Write(104020146))); peer.Expect(1, 818, [0, 19, 0]); peer.Barrier();
                That(fixture.Snapshot() == beforeChoice, "未选目标的820及错误装备ID的818不扣灵魂、金币或碎片，同连接仍可正确选择");
                peer.Send(818, selectPiece); peer.Expect(1, 818, [1]);
                var quote = peer.Expect(0, 748).Body;
                That(quote.Length == 41 && quote[0] == 5 && Enumerable.Range(0, 5).All(index => U32(quote, 5 + 8 * index) == 10),
                    "真实CMD818成功后以748提供五类完整成本，不回空泛成功");
                peer.Send(818, selectPiece); peer.Expect(1, 818, [1]); peer.Expect(0, 748, quote);
                peer.Send(819); peer.Expect(1, 819, [1]); peer.Expect(0, 14); quote = peer.Expect(0, 748).Body; peer.Barrier();
                That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[100000L]], "CMD819只扣一次PVF刷新费用");
                var oldBook = Book(account); string oldItems = Physical();
                byte shortageMode = scenario == 0 ? (byte)1 : (byte)0;
                byte[] shortageNotice = Convert.FromHexString(shortageMode == 0
                    ? "0018000000BDBBBBBBCBF9D0E8B5C4CAB7CAABCBE9C6ACB2BBD7E3A1A3"
                    : "000E000000CAB7CAABC1E9BBEAB2BBD7E3A1A3");
                string beforeShortage = fixture.Snapshot();
                for (int retry = 0; retry < 2; retry++)
                {
                    peer.Send(820, [shortageMode]); peer.Expect(1, 820, [0, 19, 0]);
                    peer.Expect(0, 502, shortageNotice); peer.Barrier();
                    That(fixture.Snapshot() == beforeShortage && Physical() == oldItems && Book(account).SequenceEqual(oldBook),
                        "真实材料或灵魂不足先失败ACK再原生提示，不发成功、扣费或图鉴变更，重试也不改资产");
                }
                peer.Send(818, selectPiece); peer.Expect(1, 818, [1]); peer.Expect(0, 748, quote);
                if (scenario == 2)
                {
                    string unchanged = fixture.Snapshot();
                    fixture.Sql("CREATE TRIGGER fail_epic_piece_shop BEFORE INSERT ON epic_piece_audit_log WHEN NEW.action_name='epic_piece_shop_exchange' BEGIN SELECT RAISE(ABORT,'late epic piece exchange failure'); END");
                    peer.Send(820, [1]); peer.Closed();
                    That(fixture.Snapshot() == unchanged && Physical() == oldItems && Book(account).SequenceEqual(oldBook),
                        "交换晚期SQL异常回滚史诗灵魂、图鉴及版本，不发布成功或保留半笔资产");
                    fixture.Sql("DROP TRIGGER fail_epic_piece_shop");
                    continue;
                }
                byte mode = checked((byte)scenario);
                peer.Send(820, [mode]); peer.Expect(1, 820, [1]);
                if (mode == 1) peer.Expect(0, 14);
                var changed = peer.Expect(0, 1152).Body; peer.Barrier();
                var actual = Book(account);
                int targetIndex = items.EpicPieces.ByPiece[targetPiece].Index;
                That(changed[0] == 1 && U32(changed, 1) == (mode == 0 ? 6 : 1) && actual[targetIndex] == oldBook[targetIndex] + 5 && Physical() == oldItems,
                    "两种真实交换方式均只提交数量钱包，不生成附加物品或改变物理UID");
                foreach (int id in Enumerable.Range(0, 5).Select(index => checked((int)U32(quote, 1 + index * 8))))
                    That(actual[items.EpicPieces.ByPiece[id].Index] == oldBook[items.EpicPieces.ByPiece[id].Index] - (mode == 0 ? 10 : 0),
                        "普通方式扣清单，灵魂方式保留清单中的全部碎片");
                string final = fixture.Snapshot();
                peer.Send(820, [mode]); peer.Expect(1, 820, [0, 19, 0]); peer.Barrier();
                That(fixture.Snapshot() == final, "本次NPC已兑换后重放不重复扣费或入账");

                void Observe(WirePacket packet)
                {
                    if (packet is { Kind: 0, Opcode: 1349 })
                    {
                        AssertAdventureProgress(fixture, resources, peer, packet);
                        return;
                    }
                    That(packet is { Kind: 0, Opcode: 36 or 115 or 31 or 261 or 789 or 747 }, "深渊清房只接收明确的原业务通知和碎片NPC通知");
                    if (packet.Opcode == 747)
                    {
                        That(packet.Body is [0] or [1], "747是单字节可见标志");
                        shown |= packet.Body[0] == 1;
                    }
                }
            }
            using (var peer = TcpPeer.Open(fixture, roles[0].Account))
            {
                var entry = peer.Select(roles[0].Role.Slot, prepared: true, resumeTown: true, room: false);
                var book = entry.Single(packet => packet is { Kind: 0, Opcode: 1152 }).Body;
                var saved = Book(roles[0].Account);
                That(book[0] == 0 && U32(book, 1) == saved.Count(value => value != 0) && Enumerable.Range(0, checked((int)U32(book, 1))).All(index =>
                    saved[items.EpicPieces.ByPiece[checked((int)U32(book, 5 + index * 8))].Index] == U32(book, 9 + index * 8)),
                    "重登完整回放同一账号图鉴钱包的最终余额，不重做拾取或交换");
                peer.Barrier();
            }
        }
        finally
        {
            await fixture.StopAsync();
            Environment.SetEnvironmentVariable("DFO_EPIC_PIECE_SHOP_APPEAR_PERCENT", previousAppearance);
            Environment.SetEnvironmentVariable("DFO_HELL_EPIC_FRAGMENT_PERMILLE", previousRate);
        }
        Console.WriteLine($"PASS {Count - checks} 旧深渊碎片、满包钱包拾取及加百利两种交换真实TCP断言");

        int[] Book(string account)
        {
            var rows = fixture.Query("SELECT counts FROM account_epic_books WHERE account=$p0", account);
            return rows.Length == 0 ? new int[items.EpicPieces.Entries.Length] :
                JsonSerializer.Deserialize<int[]>((string)rows[0][0])!;
        }
        string Physical() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
    }
}
