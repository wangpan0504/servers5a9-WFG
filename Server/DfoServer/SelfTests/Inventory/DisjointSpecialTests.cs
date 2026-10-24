using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Collections.Immutable;
using System.Net;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Network;
using DfoServer.Network.Projection;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class DisjointSpecialTests
{
    const string PieceText = "[piece list]\n2000 9000\n[/piece list]";
    const string Text = """
        [cube index]
        `[no element]` 3037
        [/cube index]
        [cube creation const]
        150 1 1 1 1 1 1 1
        [additional result]
        0 0 0 0 0 0 0
        [additional result const]
        0 0 0 0 0 0 0
        [additional result expand]
        0 0 0 0 0 0 0
        [additional result expand const]
        0 0 0 0 0 0 0
        [independent result]
        [particular item]
        [input index]
        2001
        [/input index]
        [output index]
        8000 1000 10
        [/output index]
        [/particular item]
        [particular item]
        [input index]
        2002
        [/input index]
        [output index]
        8001 1000 4
        [/output index]
        [/particular item]
        [/independent result]
        [event result]
        2003 8000 10
        [/event result]
        [epicpiece disjoint result]
        90 110
        """;

    public static void RunCore()
    {
        int checks = Count;
        var pieces = new EpicPieceCatalog(PieceText);
        var limits = new GoldLimitCatalog("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var equipment = new ItemTemplate(2000, ItemKind.Equipment, 12, 1, 100, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "source");
        var material = equipment with { Id = 8000, Kind = ItemKind.Material, StackLimit = int.MaxValue };
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [2000] = equipment, [2001] = equipment with { Id = 2001 }, [2002] = equipment with { Id = 2002 },
            [2003] = equipment with { Id = 2003 }, [8000] = material, [8001] = material with { Id = 8001 },
            [9000] = material with { Id = 9000 }, [7000] = material with { Id = 7000, PortableDisjointLevel = 95 }
        });
        var request = new DisjointRequest(new(InventoryListType.Main, 3), -1, 0);
        var shared = new DisjointCatalog(Text, PieceText);
        var machineExtra = DisjointResultCalculator.Calculate(shared, equipment with { Id = 2001 }, new EndpointRandom(false), [new(3037, 2)]);
        That(machineExtra.SequenceEqual(new InventoryReward[] { new(3037, 2), new(8000, 10) }) &&
            DisjointResultCalculator.Calculate(shared, equipment with { Id = 2003 }, new EndpointRandom(false), [new(3037, 2)]).Length == 0,
            "玩家机器替换基础公式仍保留独立奖励与活动拒绝，不重复叠加NPC基础量");
        That(DisjointResultCalculator.Calculate(shared, equipment, new EndpointRandom(false), [new(3037, 2)])
            .Any(row => row.ItemId == 9000 && row.Count == 90), "玩家机器规划不丢弃新版公共碎片规则");
        Character Source(int id) => new(1, 0, "Special", 0, new(0, 0, 0, 0), null)
        {
            Inventory = new([new(1, request.Position, InventoryRewardGrantService.Create(items.Get(id))),
                new(2, new(InventoryListType.Main, 4), InventoryRewardGrantService.Create(items.Get(7000), 2))])
        };
        foreach (var (input, output, count) in new[] { (2001, 8000, 10), (2002, 8001, 4) })
        {
            var service = Service(Text, new EndpointRandom(false));
            var current = Source(input);
            var result = service.Disjoint(current, request, out var receipt);
            That(result.Accepted && receipt!.Rewards.Any(row => row.ItemId == output && row.Count == count) &&
                receipt.Rewards.Any(row => row.ItemId == 3037), "独立确定奖励追加普通结果且不漏第二组");
        }
        foreach (bool high in new[] { false, true })
        {
            var random = new EndpointRandom(high);
            var service = Service(Text, random);
            var current = Source(2000);
            var result = service.Disjoint(current, request with { ToolSlot = 4 }, out var receipt);
            int count = high ? 110 : 90;
            That(result.Accepted && result.Plan!.After.EpicPieces.Get(0) == count &&
                result.Plan.After.Items.All(item => item.Core.ItemId != 9000) &&
                receipt!.Rewards.Single(row => row.ItemId == 9000) == new DisjointReward(-1, 9000, count),
                "片区间含两端且只入账号图鉴，ACK使用FFFF和本次增量");
            That(random.Draws == 1 && result.Plan!.After.Find(new(InventoryListType.Main, 4))!.Core.Count == 1,
                "片只抽一次，便携工具只扣一次且无机器倍乘");
            var full = current with { Inventory = current.Inventory with { EpicPieces = current.Inventory.EpicPieces.Set(pieces, 0, int.MaxValue - count + 1) } };
            That(!service.Disjoint(full, request, out _).Accepted && full.Inventory.Find(request.Position) is not null,
                "片余额溢出整笔拒绝并保留原装备");
        }
        foreach (string invalid in new[] { "8000 999 10", "8000 0 10", "8000 1000 0", "8000 1000 -1", "" })
            That(!Service(Text.Replace("8000 1000 10", invalid), new EndpointRandom(false)).Disjoint(Source(2001), request, out _).Accepted,
                "独立未知概率、非正数量和缺失行整笔安全拒绝");
        That(!Service(Text.Replace("8000 1000 10", "8002 1000 10"), new EndpointRandom(false)).Disjoint(Source(2001), request, out _).Accepted,
            "独立产物缺失模板不得漏奖励后销毁原装备");
        That(!Service(Text.Replace("[particular item]", "[unknown item]").Replace("[/particular item]", "[/unknown item]"),
            new EndpointRandom(false)).Disjoint(Source(2001), request, out _).Accepted,
            "有特殊输入声明但不识别奖励结构时拒绝，不能降级普通分解");
        var wrongPieces = new EpicPieceCatalog("[piece list]\n2004 9001\n[/piece list]");
        That(!new InventoryDisjointService(items, new DisjointCatalog(Text, PieceText), limits, wrongPieces, new EndpointRandom(false))
            .Disjoint(Source(2000), request, out _).Accepted, "片目录错装必须拒绝，不能降级伪Main碎片实体");
        That(!Service(Text, new EndpointRandom(false)).Disjoint(Source(2003), request, out _).Accepted,
            "活动替代或追加语义未证继续拒绝");
        var merged = Service(Text.Replace("8000 1000 10", "3037 1000 10"), new EndpointRandom(false)).Disjoint(Source(2001), request, out var mergedReceipt);
        That(merged.Accepted && mergedReceipt!.Rewards.Single(row => row.ItemId == 3037).Count == 11,
            "独立与普通同模板先合计，ACK不重复列出同一增量");
        That(!Service(Text.Replace("8000 1000 10", "3037 1000 2147483647"), new EndpointRandom(false)).Disjoint(Source(2001), request, out _).Accepted,
            "普通与独立合计溢出整笔拒绝");
        var crowded = Source(2001);
        var filled = Enumerable.Range(121, 56).Select(index => new InventoryItem(index + 100, new(InventoryListType.Main, (short)index),
            InventoryRewardGrantService.Create(material with { Id = 8001 }, int.MaxValue))).ToArray();
        crowded = crowded with { Inventory = crowded.Inventory with { Items = [.. crowded.Inventory.Items, .. filled] } };
        That(!Service(Text, new EndpointRandom(false)).Disjoint(crowded, request with { ToolSlot = 4 }, out _).Accepted,
            "特殊实体材料页满时普通钱包奖励也不部分提交");
        var movedSource = crowded.Inventory.Find(request.Position)! with { Position = new(InventoryListType.Main, 121) };
        crowded = crowded with { Inventory = crowded.Inventory with { Items = [movedSource, .. filled.Skip(1)] } };
        var freed = Service(Text, new EndpointRandom(false)).Disjoint(crowded, request with { Position = movedSource.Position }, out _);
        That(freed.Accepted && freed.Plan!.After.Find(movedSource.Position)!.Core.ItemId == 8000,
            "特殊奖励容量规划复用将释放的源槽");
        RunSessionChecks(items, limits, pieces, Source(2000), request);
        RunRejectionChecksAsync(items, limits, pieces, Source(2003), request).GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} special equipment disjoint core assertions");

        InventoryDisjointService Service(string text, Random random) => new(items, new DisjointCatalog(text, PieceText), limits, pieces, random);
    }

    static async Task RunRejectionChecksAsync(ItemCatalog items, GoldLimitCatalog limits, EpicPieceCatalog pieces,
        Character seed, DisjointRequest request)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-disjoint-rejection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("rejection", () => { });
            var source = seed.Inventory.Find(request.Position)!;
            var core = source.Core with { EquipmentLockId = 1, SortLockFlag = 1, Attr = 12, EnchantCardId = 5,
                GuardianGemKeys = 0x1122334455667788, Random0 = new(1, 2, 3), Extension114 = new(9, 17), Extension121 = 0x4321 };
            repo.Create(owner, new("Reject", seed.Position, null) { Inventory = new([source with { Uid = 0, Core = core }]) });
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), seed.Position, seed.Position,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var projection = new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces);
            var service = new InventoryDisjointService(items, new DisjointCatalog(Text, PieceText), limits, pieces);
            var commands = new GameCommandRegistry();
            new DisjointHandler(service, projection).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            Action? afterAck = null;
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (kind, opcode, _, body, action) =>
            {
                sent.Add((kind, opcode, body.ToArray()));
                if (action == "equipment_disjoint_rejected") afterAck?.Invoke();
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var packet = new Packet(1, 26, [3, 0, 0, 255, 255, 0, 0, 0, 0]);
            string before = JsonSerializer.Serialize(repo.List(owner));
            await commands.DispatchAsync(context, packet);
            That(sent.Select(row => (row.Kind, row.Opcode)).SequenceEqual(new (byte, ushort)[] { (1, 26), (0, 14), (1, 713), (0, 251) }) &&
                sent[0].Body.SequenceEqual(new byte[] { 0, 19, 0 }) && sent[1].Body.Length == 249 && sent[1].Body[1] == 2,
                "真实Handler拒绝按ACK→单帧同槽clear/restore→原有锁投影发送");
            That(sent[1].Body.AsSpan(126).SequenceEqual(InventoryPackets.Delta(session.Selected!.Inventory, InventoryListType.Main,
                [request.Position]).AsSpan(3)) && before == JsonSerializer.Serialize(repo.List(owner)),
                "拒绝回填保留全部扩展核心、锁和UID，零领域或数据库修改");
            foreach (bool reselect in new[] { false, true })
            {
                sent.Clear();
                afterAck = () =>
                {
                    if (reselect) { session.ReturnToRoster(); session.Select(0); session.MarkReady(); }
                    else session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character, [new(8001, 1)], items, limits, pieces));
                };
                await commands.DispatchAsync(context, packet);
                That(sent.Count == 1 && sent[0].Opcode == 26,
                    "ACK发送后revision或选角epoch变化，NOTI14整帧及锁均抑制，绝不仅清空一半");
                That(session.Selected!.Inventory.Find(request.Position)!.Core == core, "过期拒绝未恢复旧实例或覆盖当前核心");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static void RunSessionChecks(ItemCatalog items, GoldLimitCatalog limits, EpicPieceCatalog pieces,
        Character seed, DisjointRequest request)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-special-disjoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("special", () => { });
            repo.Create(owner, new("Piece", seed.Position, null)
            { Inventory = seed.Inventory with { Items = seed.Inventory.Items.Select(item => item with { Uid = 0 }).ToImmutableArray() } });
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), seed.Position, seed.Position,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var captured = session.CaptureDisjointRejectedSource(request)!;
            That(session.CanPublishDisjointRejectedSource(captured), "拒绝源捕获当前owner、epoch、phase、revision、UID和完整Core");
            That(!session.CanPublishDisjointRejectedSource(captured with { Source = captured.Source with { Uid = captured.Source.Uid + 1 } }) &&
                !session.CanPublishDisjointRejectedSource(captured with { Source = captured.Source with { Core = captured.Source.Core with { Attr = 1 } } }) &&
                !session.CanPublishDisjointRejectedSource(captured with { Character = captured.Character with { Revision = captured.Character.Revision + 1 } }) &&
                !session.CanPublishDisjointRejectedSource(captured with { Phase = SessionPhase.Dungeon }) &&
                !session.CanPublishDisjointRejectedSource(captured with { SelectionEpoch = captured.SelectionEpoch + 1 }),
                "任一原源身份变化均抑制整帧");
            var config = new DisjointCatalog(Text.Replace("2001", "2000"), PieceText);
            var service = new InventoryDisjointService(items, config, limits, pieces, new EndpointRandom(false));
            string before = Snapshot();
            Query("CREATE TRIGGER fail_piece BEFORE INSERT ON epic_piece_audit_log BEGIN SELECT RAISE(ABORT,'piece rollback'); END");
            bool failed = false;
            try { session.DisjointEquipment(request with { ToolSlot = 4 }, service, out _); }
            catch (SqliteException) { failed = true; }
            That(failed && before == Snapshot() && session.CanPublishDisjointRejectedSource(captured),
                "片审计中断回滚源、工具、普通奖励、图鉴、revision和审计并保留在线原快照");
            Query("DROP TRIGGER fail_piece");
            var conflictRandom = new CallbackRandom(() => session.MutateInventory(character =>
                InventoryRewardGrantService.TryGrantBatch(character, [new(8001, 1)], items, limits, pieces)));
            var conflictService = new InventoryDisjointService(items, config, limits, pieces, conflictRandom);
            var conflict = session.DisjointEquipment(request with { ToolSlot = 4 }, conflictService, out _);
            That(!conflict.Accepted && conflictRandom.Draws == 1 && session.Selected!.Inventory.Find(request.Position) is not null &&
                session.Selected.Inventory.Find(new(InventoryListType.Main, 4))!.Core.Count == 2 && session.Selected.Inventory.EpicPieces.Get(0) == 0 &&
                !session.CanPublishDisjointRejectedSource(captured),
                "随机规划在锁外冻结一次；规划中快照变化不二次抽取、不消费旧源或工具且抑制旧拒绝帧");
            var result = session.DisjointEquipment(request with { ToolSlot = 4 }, service, out var receipt);
            That(result.Accepted && receipt!.Rewards.Single(row => row.ItemId == 9000).Count == 90 &&
                session.Selected!.Inventory.EpicPieces.Get(0) == 90 && session.Selected.Inventory.Items.Any(item => item.Core.ItemId == 8000 && item.Core.Count == 10) &&
                !session.CanPublishDisjointRejectedSource(captured),
                "已提交删除后旧拒绝源不得恢复旧实例");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "独立仓储重载恢复片余额、普通奖励、工具与源删除");
            That(!session.DisjointEquipment(request, service, out _).Accepted, "相同源重放不再发放碎片");
            session.ReturnToRoster();
            session.Select(0);
            session.MarkReady();
            That(session.Selected!.Inventory.EpicPieces.Get(0) == 90 && !session.CanPublishDisjointRejectedSource(captured),
                "重选保持账号绝对片余额且旧epoch不回填原装备");
            using var next = owners.Acquire(owner.Account, () => { });
            That(!session.CanPublishDisjointRejectedSource(captured), "旧owner拒绝帧安静抑制");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") +
                ":" + Query("SELECT count(*) FROM currency_audit_log") + ":" + Query("SELECT count(*) FROM epic_piece_audit_log");
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class EndpointRandom(bool high) : Random
    {
        public int Draws { get; private set; }
        public override int Next(int maxValue) { Draws++; return high ? maxValue - 1 : 0; }
    }
    sealed class CallbackRandom(Action callback) : Random
    {
        public int Draws { get; private set; }
        public override int Next(int maxValue) { Draws++; callback(); return 0; }
    }
}
