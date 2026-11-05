using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 合成商人仅覆盖真实Handler、Sender编码及原资产事务，不代表原入口或socket TCP。
internal static class EpicRoad2019ShopHandlerTests
{
    const int ShopId = 400000098, NpcId = 400001105;
    public static void RunResources(ItemResources resources, CharacterCatalog characters) =>
        RunAsync(resources, characters).GetAwaiter().GetResult();

    static async Task RunAsync(ItemResources resources, CharacterCatalog characters)
    {
        int checks = Count;
        foreach (string mode in new[] { "success", "event-closed", "merchant-missing", "resources-default-closed" })
        {
            var point = new Location(1, 0, 100, 100);
            var map = new TownMap("synthetic/epic-shop-handler.map",
                [new(new(0, 0, 1000, 1000), [], [])], [], false,
                mode == "merchant-missing" ? [new(1, 100, 100)] : [new(NpcId, 100, 100), new(1, 100, 100)]);
            var area = new TownArea(1, 0, 1, [], [], [], point, map, [], []);
            var towns = new TownCatalog(new() { [(1, 0)] = area }, point, point, new(0, [], []), [],
                new Dictionary<uint, uint[]>());
            using var f = new Fixture(towns);
            f.Events.Override(ServerEventService.EpicRoadId, mode != "event-closed");
            // 默认关闭分支使用无显式可用值的真实默认构造，不能传false冒测默认。
            var shop = mode == "resources-default-closed"
                ? new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, f.Clock)
                : new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces,
                    f.Clock, epicRoadAvailable: true);
            var session = new GameSession(f.Repository, towns, f.Owner, timeProvider: f.Clock, serverEvents: f.Events);
            try
            {
                session.Select(f.Current.Slot); session.MarkReady();
                var computer = new CharacterStatComputer(characters);
                var town = new TownProjection(towns, computer, null, resources.EpicPieces, clock: f.Clock);
                var projection = new InventoryProjection(computer, town, resources.EpicPieces);
                using var stop = new CancellationTokenSource();
                using var output = new MemoryStream();
                var sent = new List<(byte Kind, ushort Opcode, ushort Sequence, byte[] Body)>();
                await using var sender = new GameConnectionSender(f.Owner, output, stop, 0,
                    (kind, opcode, sequence, body, _) => sent.Add((kind, opcode, sequence, body.ToArray())));
                var context = new GameConnectionContext(new(921, IPAddress.Loopback, output, stop), f.Owner, session, sender);
                using var log = new ServerLog(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "handler-log"));
                var commands = new GameCommandRegistry();
                new ShopHandler(shop, new(resources.Items, resources.CashShop, resources.GoldLimits,
                    resources.EpicPieces, resources.Cargo), projection, log,
                    serverEvents: f.Events, strongwayShops: resources.Shops).Register(commands);
                commands.Seal();
                var before = session.Selected!;
                string assets = f.Snapshot();
                await Buy(new(490013729, 1, ShopId, NpcId));
                if (mode == "success")
                {
                    CheckSuccess(490013729, before, 80);
                    var saved = new CharacterRepository(f.Path).LoadCharacter(f.Owner, before.Slot);
                    That(QuestCompletionRepositoryTests.Same(session.Selected!, saved) &&
                        saved.Inventory.ShopPurchases.Any(row => row.ItemId == 490013729 && !row.AccountWide &&
                            row.Period == ItemPurchasePeriod.Daily && row.Count == 1),
                        "Handler真实729购买、每日限购及完整实例从独立Repository恢复");
                    for (int purchase = 2; purchase <= 5; purchase++)
                    {
                        before = session.Selected!; Reset();
                        await Buy(new(490013729, 1, ShopId, NpcId));
                        CheckSuccess(490013729, before, 80);
                    }
                    // 同角色满五次后拒绝，不能继续扣费或发货。
                    assets = f.Snapshot(); Reset();
                    await Buy(new(490013729, 1, ShopId, NpcId));
                    CheckFailure(0x85, assets, "同角色每日第六次购买");
                }
                else CheckFailure(17, assets, mode);

                // 每个状态之后都提交普通商品，证明新分派不占用普通店。
                before = session.Selected!; Reset();
                That(resources.Shops.Allows(1, 2, 3175) && resources.Items.Get(3175).ShopPrice.Gold == 10,
                    "普通商品对照是原NPC1/店2/3175的真实10金币商品");
                await Buy(new(3175, 1, 2, 1));
                CheckSuccess(3175, before, 10);
                That(QuestCompletionRepositoryTests.Same(session.Selected!,
                    new CharacterRepository(f.Path).LoadCharacter(f.Owner, before.Slot)),
                    "普通商品Handler成功在全部活动状态下持久恢复");
                f.Passed = true;

                async Task Buy(BuyItemRequest request)
                {
                    byte[] body = new byte[16];
                    BinaryPrimitives.WriteInt32LittleEndian(body, request.ItemId);
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), request.Count);
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), request.ShopId);
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), request.NpcId);
                    That(await commands.DispatchAsync(context, new Packet(1, 21, body)) == CommandResult.Handled,
                        "真实16B CMD21经唯一注册解析并处理：" + mode);
                    await sender.DrainAsync();
                    // callback来自实际写帧后；这里读取实际MemoryStream，不把排队当已发送。
                    byte[] expected = sent.SelectMany(row => GameFrame.Build(row.Kind, row.Opcode, row.Sequence, row.Body)).ToArray();
                    That(output.ToArray().AsSpan().SequenceEqual(expected) && expected.Length > 0,
                        "实际Sender写出可核对的完整帧及顺序：" + mode);
                }
                void Reset() { sent.Clear(); output.SetLength(0); output.Position = 0; }
                void CheckFailure(ushort error, string snapshot, string reason)
                {
                    byte[] ack = [0, (byte)error, (byte)(error >> 8)];
                    That(sent.Count == 1 && sent[0].Kind == 1 && sent[0].Opcode == 21 &&
                        sent[0].Body.AsSpan().SequenceEqual(ack) && f.Snapshot() == snapshot,
                        "ACK21真实拒绝且不扣费、不发货、不改完整限购/资产：" + reason);
                }
                void CheckSuccess(int itemId, Character prior, int cost)
                {
                    That(sent.Count >= 2 && sent[0].Kind == 1 && sent[0].Opcode == 21,
                        "成功ACK21先于库存投影：" + mode);
                    var ack = sent[0].Body;
                    var current = session.Selected!;
                    var item = current.Inventory.Items.Single(row => row.Core.ItemId == itemId);
                    That(item.Uid > 0 && current.Inventory.Gold == prior.Inventory.Gold - cost &&
                        CountItem(current.Inventory, itemId) - CountItem(prior.Inventory, itemId) == 1 &&
                        ack.Length == 141 && ack[0] == 1 && U32(ack, 1) == current.Inventory.Gold &&
                        U16(ack, 17) == item.Position.Index && U32(ack, 19) == itemId &&
                        U32(ack, 23) == 1 && ack[140] == 0,
                        "ACK21真实金币、持久槽位、商品与本次完整增量：" + itemId);
                    var delta = sent.Single(row => row.Kind == 0 && row.Opcode == 14).Body;
                    That(delta[0] == 0 && delta.Length == 3 + 123 * U16(delta, 1),
                        "NOTI14保留完整Main差量核心");
                    CheckDelta(0, 0, current.Inventory.Gold);
                    CheckDelta(item.Position.Index, itemId, checked((uint)item.Core.Count));
                    void CheckDelta(short slot, int id, uint value)
                    {
                        int offset = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123)
                            .Single(offset => U16(delta, offset) == slot);
                        That(U32(delta, offset + 2) == id && U32(delta, offset + 6) == value,
                            "NOTI14反映已提交金币/商品的最终值：" + id);
                    }
                }
            }
            finally { session.End(); }
        }
        Console.WriteLine($"PASS {Count - checks} 星河合成商人Handler/ACK21/NOTI14及持久恢复断言（非原入口、非socket TCP）");
        static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    static int CountItem(InventorySnapshot inventory, int id) =>
        inventory.Items.Where(row => row.Core.ItemId == id).Sum(row => row.Core.Count);
    sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(8));
    }
    sealed class Fixture : IDisposable
    {
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s5a9-epic-shop-handler-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners.Lease Owner { get; }
        public ServerEventService Events { get; }
        public Clock Clock { get; } = new();
        public Character Current { get; }
        public bool Passed { get; set; }
        public Fixture(TownCatalog towns)
        {
            Path = System.IO.Path.Combine(directory, "game.db");
            Repository = new(Path);
            Owner = new AccountOwners().Acquire("epic-handler", () => { });
            Current = Repository.Create(Owner, new("EpicWireA", towns.Room, towns.InitialReturn)
                { Level = 95, Inventory = InventorySnapshot.Empty with { Gold = 100_000 } });
            Events = new(new ServerEventRepository(Path), Clock);
        }
        public string Snapshot() => JsonSerializer.Serialize(new
        {
            Characters = Repository.List(Owner),
            Wallet = Repository.LoadAccountWallet(Owner)
        });
        public void Dispose()
        {
            Owner.Dispose();
            if (Passed) TestFiles.DeleteDirectory(directory, true);
            else Console.WriteLine("FAIL fixture: " + directory);
        }
    }
}
