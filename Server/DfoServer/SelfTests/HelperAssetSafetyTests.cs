using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HelperAssetSafetyTests
{
    static readonly InventorySlot Source = new(InventoryListType.Main, 20);
    static readonly DisjointRequest Request = new(Source, -1, 0);
    const string Pieces = "[piece list]\n60000 70001\n[/piece list]";
    const string DisjointText = """
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
        """;

    internal static void RunCore()
    {
        using (var f = new Fixture())
        {
            That(!f.Disjoint().Accepted, "普通NPC不能直接分解未净化气息");
            var receipt = f.Safety();
            That(receipt.Candidates.Single().Item.Uid == f.Item.Uid && f.Session.CanPublishHelperSafety(receipt),
                "资格来自服务端真实UID和当前城镇快照");
            That(f.Disjoint().Accepted && f.Session.Selected!.Inventory.Find(Source) is null,
                "精确授权允许未净化气息经普通分解产物提交");
            That(CurrencyWallet.Get(f.Session.Selected!.Inventory, CurrencyKind.ClearCube) == 1 &&
                f.Session.Selected.Inventory.Items.All(item => item.Core.ItemId != 2001),
                "助手分解不伪装玩家机器或保留原装备");
            f.Replace(item: f.Seed with { Uid = 0 });
            long replacement = f.Item.Uid;
            That(!f.Disjoint().Accepted && f.Item.Uid == replacement,
                "已消费槽位重放不销毁同槽替换装备");
        }
        using (var f = new Fixture(red: false))
        {
            f.Replace(f.Item with { Core = f.Item.Core with { Attr = 1 } });
            That(f.Safety().Candidates.Length == 0 && f.Disjoint().Accepted,
                "未登记普通手动槽位沿用原NPC规则，不扩大助手保护范围");
        }
        using (var f = new Fixture())
        {
            f.Safety();
            var original = f.Item;
            f.Sql("CREATE TRIGGER helper_fail_commit BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'helper safety fixture'); END");
            Reject<SqliteException>(() => f.Disjoint(), "后段审计失败保持事务失败");
            That(f.Item == original, "事务失败不更新在线源装备");
            f.Sql("DROP TRIGGER helper_fail_commit");
            That(f.Disjoint().Accepted, "事务回滚不消费资格，同一精确源可重试提交");
        }
        using (var f = new Fixture(red: false))
        {
            f.Safety();
            var original = f.Item;
            f.Replace(original with { Uid = 0 });
            That(f.Item.Uid != original.Uid && !f.Disjoint().Accepted,
                "同槽同内容新UID仍拒绝，不能降级普通手动分解");
        }
        using (var f = new Fixture(red: false))
        {
            f.Safety();
            var original = f.Item;
            f.Replace(original with { Core = original.Core with { Durability = 99 } });
            That(f.Item.Uid == original.Uid && !f.Disjoint().Accepted,
                "同UID完整Core变化拒绝旧资格");
            f.Replace(original);
            That(f.Disjoint().Accepted, "资格复核失败不提前消费，精确源恢复后仍可提交");
        }
        using (var f = new Fixture(red: false))
        {
            f.Safety();
            f.Clock.Advance(TimeSpan.FromMinutes(5));
            That(!f.Disjoint().Accepted && f.Session.Selected!.Inventory.Find(Source) is not null,
                "五分钟边界拒绝普通装备旧资格，不静默绕回普通分解");
        }
        using (var f = new Fixture())
        {
            var receipt = f.Safety();
            var original = f.Session.Selected;
            That(!f.Session.CanPublishHelperSafety(receipt with { PhaseEpoch = receipt.PhaseEpoch + 1 }),
                "阶段代次必须精确匹配");
            That(f.Session.BeginDungeonSelection() is not null && f.Session.ReturnFromDungeonSelection(restorePosition: false) is not null,
                "经正式选图入口离城再回城");
            That(ReferenceEquals(original, f.Session.Selected) && !f.Session.CanPublishHelperSafety(receipt) && !f.Disjoint().Accepted,
                "角色引用未变也不能重新发布离城前资格");
            f.Safety();
            That(f.Disjoint().Accepted, "回城重新明确查询后安装当前阶段资格");
        }
        using (var f = new Fixture())
        {
            var receipt = f.Safety();
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(!f.Session.CanPublishHelperSafety(receipt) && !f.Disjoint().Accepted,
                "重新选角使原epoch及气息授权失效");
            f.Safety();
            That(f.Disjoint().Accepted, "新选角代次只能使用自己的新资格");
        }
        using (var f = new Fixture())
        {
            var receipt = f.Safety();
            using var next = f.Owners.Acquire(f.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => f.Session.CanPublishHelperSafety(receipt), "旧owner不能发布资格");
            Reject<InvalidOperationException>(() => f.Disjoint(), "旧owner不能凭缓存分解");
            That(f.Repository.LoadCharacter(next, 0).Inventory.Find(Source) is not null, "接管不改新owner的装备");
        }
        RunCargo();
    }

    internal static void RunShops(PvfArchive archive)
    {
        var shops = new NpcShopCatalog(archive);
        using (var f = new Fixture(red: false, shops: shops))
        {
            f.Safety();
            That(f.Sell().Accepted && f.Session.Selected!.Inventory.Find(Source) is null && f.Session.Selected.Inventory.Gold == 100,
                "授权普通装备经真实出售入口扣源并增加准确金币");
            f.Replace(f.Seed with { Uid = 0 });
            long replacement = f.Item.Uid;
            That(!f.Sell().Accepted && f.Item.Uid == replacement && f.Session.Selected!.Inventory.Gold == 100,
                "已消费出售槽重放不销毁新装备或重复增加金币");
        }
        using (var f = new Fixture(red: false, shops: shops))
        {
            f.Safety();
            long original = f.Item.Uid;
            f.Replace(f.Item with { Uid = 0 });
            That(f.Item.Uid != original && !f.Sell().Accepted && f.Session.Selected!.Inventory.Gold == 0,
                "未消费出售候选被同内容新UID替换仍拒绝且不收物付钱");
        }
        using (var f = new Fixture(red: false, shops: shops))
        {
            f.Safety();
            var original = f.Item;
            f.Sql("CREATE TRIGGER helper_fail_sale BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'helper sale fixture'); END");
            Reject<SqliteException>(() => f.Sell(), "出售审计失败回滚真实事务");
            That(f.Item == original && f.Session.Selected!.Inventory.Gold == 0,
                "出售提交失败源装备和金币均保持");
            f.Sql("DROP TRIGGER helper_fail_sale");
            That(f.Sell().Accepted && f.Session.Selected!.Inventory.Gold == 100,
                "出售失败不提前消费资格，同一精确源可重试");
        }
    }

    static void RunCargo()
    {
        using (var f = new Fixture(material: true))
        {
            f.Safety();
            var original = f.Item;
            That(!f.Deposit(destination: 8).Accepted && f.Item == original,
                "越过账号仓库容量的归仓失败不动源");
            That(f.Deposit().Accepted && f.Session.Selected!.Inventory.Find(f.Position) is null &&
                f.Session.Selected.Inventory.Find(new(InventoryListType.AccountCargo, 0)) is { Core.Count: 12 },
                "失败不消费资格，原整堆材料经真实归仓入口成功入账号库");
            That(!f.Deposit().Accepted, "成功归仓后同源请求重放拒绝");
            f.Replace(f.Seed with { Uid = 0 });
            long replacement = f.Item.Uid;
            That(!f.Deposit().Accepted && f.Item.Uid == replacement &&
                f.Session.Selected!.Inventory.Find(new(InventoryListType.AccountCargo, 0)) is { Core.Count: 12 },
                "已消费归仓槽不继续搬走城镇新材料或叠加仓库数量");
        }
        using (var f = new Fixture(material: true))
        {
            f.Safety();
            long original = f.Item.Uid;
            f.Replace(f.Item with { Uid = 0 });
            That(f.Item.Uid != original && !f.Deposit().Accepted &&
                f.Session.Selected!.Inventory.Find(new(InventoryListType.AccountCargo, 0)) is null,
                "未消费材料候选被同内容新UID替换后拒绝归仓");
        }
    }

    sealed class Clock : TimeProvider
    {
        long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_900_000_000).AddMilliseconds(milliseconds);
        public void Advance(TimeSpan duration) => milliseconds += checked((long)duration.TotalMilliseconds);
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-helper-asset-safety-" + Guid.NewGuid().ToString("N"));
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public Clock Clock { get; } = new();
        public InventoryItem Seed { get; }
        public InventorySlot Position { get; }
        public InventoryItem Item => Session.Selected!.Inventory.Find(Position)!;
        readonly InventoryDisjointService service;
        readonly InventoryService inventory;
        readonly InventoryShopService? shop;

        public Fixture(bool red = true, bool material = false, NpcShopCatalog? shops = null)
        {
            Directory.CreateDirectory(root);
            var location = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), location, location,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            var gear = new ItemTemplate(2001, ItemKind.Equipment, 12, 1, 100, 1, 0, 0, 0, "free", 0, 0, "Gear")
            { DisjointValue = 150, ShopPrice = new(500, 100, [], null) };
            catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
            {
                [2001] = gear,
                [8000] = gear with { Id = 8000, Kind = ItemKind.Material, EquipmentSlot = 0, Durability = 0, StackLimit = 99 }
            });
            var limits = new GoldLimitCatalog("[gold limit from level]\n0 1000000\n[/gold limit from level]");
            var pieces = new EpicPieceCatalog(Pieces);
            service = new(catalog, new DisjointCatalog(DisjointText, Pieces), limits, pieces, clock: Clock);
            inventory = new(catalog, limits, pieces, clock: Clock);
            shop = shops is null ? null : new(catalog, shops, limits, pieces, Clock);
            Repository = new(Path.Combine(root, "game.db"));
            Owner = Owners.Acquire("helper-asset", () => { });
            Position = material ? new(InventoryListType.Main, 121) : Source;
            Seed = material ? new(0, Position, new(8000, ItemKind.Material, 12)) : new(0, Position, new(2001, ItemKind.Equipment, 1)
            { Durability = 100, AmplifyType = red ? (byte)130 : (byte)0, AmplifyValue = red ? (ushort)7 : (ushort)0 });
            var created = Repository.Create(Owner, new("HelperAsset", location, location) { Inventory = new([Seed]) });
            if (material) Repository.CommitInventory(Owner, created, new(created.Inventory,
                created.Inventory with { AccountCargo = created.Inventory.AccountCargo with { Capacity = 8 } }, "helper_cargo_fixture", new(created)));
            // 合成物品目录不带PVF账号材料表；复用透传仓储夹具，只测试本用例的真实库存事务。
            var store = new QuestCompletionRepositoryTests.LostResultRepository(Repository) { LoseCompletionResult = false };
            Session = new(store, towns, Owner, items: catalog, timeProvider: Clock);
            Session.Select(0); Session.MarkReady();
        }

        public GameSession.HelperSafetyReceipt Safety() => Session.ReadHelperSafety(Session.Selected!.Id)
            ?? throw new InvalidOperationException("Expected current helper safety receipt");
        public InventoryResult Disjoint() => Session.DisjointEquipment(Request, service, out _);
        public InventoryResult Sell() => Session.SellAtShop(new(Position, 1, 0, 0, 0),
            shop ?? throw new InvalidOperationException("Expected shop fixture"));
        public InventoryResult Deposit(short destination = 0) => Session.MoveInventory(new(Position, 8000, 12,
            new(InventoryListType.AccountCargo, destination), 0, 0, 0, 0, 0), inventory);
        public void Sql(string text)
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
        }
        public void Replace(InventoryItem item) => That(Session.MutateInventory(character => new(true, 0,
            new(character.Inventory, character.Inventory with { Items = [.. character.Inventory.Items.Where(row => row.Position != Position), item] },
                "helper_safety_fixture", new(character)))).Accepted,
            "替换夹具经正式库存提交入口");
        public void Dispose() { Owner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
}
