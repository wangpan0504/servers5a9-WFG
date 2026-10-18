using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ConsumableUseSessionTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        using (var f = new Fixture(towns, characters))
        {
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected, "未选角不消费药剂且返回原状态拒绝");
            f.Session.Select(0);
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected, "选角未Ready不消费药剂");
            f.Session.MarkReady();
            var first = f.Use();
            That(first.Outcome == ConsumableUseOutcome.Committed && f.Read().Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 2 &&
                Same(first.Inventory!.Receipt!.Character, f.Read()) && f.Session.CanPublishConsumable(first.Guard!),
                "实际消费提交后安装库存，冷却开始且回执为真实UID/revision/phase");
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection && f.Count == 2,
                "原完整请求在成功冷却窗口重复不重发成功或失败ACK，不二次扣件");
            That(f.Session.UseConsumable(f.Request, f.Service, activityBlocked: true).Outcome == ConsumableUseOutcome.CloseConnection &&
                f.Session.UseConsumable(f.Request with { OpaqueCheck = 7 }, f.Service, activityBlocked: true).Outcome == ConsumableUseOutcome.Rejected,
                "活动门禁不能解除已消费token歧义，不同请求仍按非法场景完整拒绝");
            That(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2, "同组新请求冷却拒绝不扣件");
            That(f.Session.UseConsumable(new(new(InventoryListType.Main, 66), 0, 1114, 0), f.Service).Outcome == ConsumableUseOutcome.Committed,
                "本服组0与组1独立，另一药组仍能合法消费");
            f.Clock.Advance(TimeSpan.FromMilliseconds(9999));
            That(f.Use(2).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2, "本服10秒间隔未到不能提前消费");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 1, "间隔到期后opaque0可合法复用，不能永久去重");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 0, "换角重置单会话冷却，最后一件真实清槽");
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection, "源槽已耗尽时仍识别刚成功原请求，不能盲回拒绝ACK");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Use(4).Outcome == ConsumableUseOutcome.Rejected && f.Count == 0, "重选后空源合法拒绝，不补药或自动重做前次消费");
            using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => f.Use(), "旧generation不能继续使用或发布");
            That(f.Repository.LoadCharacter(replacement, 0).Inventory.Find(new(InventoryListType.Main, 65)) is null,
                "重开持久库存保留消费至空且无旧owner写入");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); var before = f.Read();
            f.Sql("CREATE TRIGGER potion_final_abort AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='use_recovery_consumable' BEGIN SELECT RAISE(ABORT,'potion rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Use(), "库存审计末段真实SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!) && f.Count == 3,
                "失败回滚原UID/Core/数量/revision，未提前发布或推进冷却");
            f.Sql("DROP TRIGGER potion_final_abort");
            That(f.Use(3).Outcome == ConsumableUseOutcome.CloseConnection, "未知提交结果原会话不自动重试SQL或继续消费");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Use(3).Outcome == ConsumableUseOutcome.Committed && f.Count == 2, "重新装载可从实际回滚存档继续新操作");
        }
        using (var f = new Fixture(towns, characters, lost: true))
        {
            f.Select(); var before = f.Session.Selected!;
            Reject<IOException>(() => f.Use(), "库存实际提交后返回结果丢失");
            That(f.Count == 2 && ReferenceEquals(before, f.Session.Selected) && f.Use().Outcome == ConsumableUseOutcome.CloseConnection,
                "已提交事实保留但不安装规划状态，不自动重做SQL或用旧快照继续");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Count == 2 && f.Use(2).Outcome == ConsumableUseOutcome.Committed && f.Count == 1,
                "重选只读实际提交数量后新请求消费一次，不回放旧ACK");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); var before = f.Read();
            var plan = f.Service.Plan(before, f.Request, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Plan!;
            f.Session.SetCharacterGold(before.Name, 1);
            Reject<InvalidOperationException>(() => f.Repository.CommitInventory(f.Owner, before, plan), "旧角色revision不能提交药剂消费");
            That(f.Count == 3, "资格/版本冲突不扣源数量");
            var receipt = f.Use().Guard!;
            f.Session.ReturnToRoster(); f.Select();
            That(!f.Session.CanPublishConsumable(receipt), "同角色重选epoch使旧消费结果发布失效");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); f.Use();
            var old = f.Read().Inventory.Find(f.Request.Position)!;
            f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
            {
                Items = character.Inventory.Items.Replace(old, new(0, old.Position, old.Core with { Value = 4 }))
            }, "potion_fixture_replace")));
            var replaced = f.Read().Inventory.Find(f.Request.Position)!;
            That(replaced.Uid != old.Uid && f.Use().Outcome == ConsumableUseOutcome.CloseConnection && f.Count == 4,
                "同槽新UID不能解除旧完整请求歧义，冷却窗内无ACK且不消费替换物品");
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 3, "窗口后按新源身份重新验证，同值token可合法使用");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select();
            f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
            {
                Items = character.Inventory.Items.Select(item => item.Position == f.Request.Position ? item with
                { Core = item.Core with { ExpireTime = 1900000002 } } : item).ToImmutableArray()
            }, "potion_fixture_expiry")));
            f.Clock.BeforeUtcRead = () => { if (f.Clock.Reads == 2) f.Clock.Advance(TimeSpan.FromSeconds(3)); };
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected && f.Count == 3, "锁外规划后到期在最终提交前重验，不扣过期药");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); f.Clock.BeforeUtcRead = () =>
            {
                if (f.Clock.Reads == 1) f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, "Potion", 15, 0));
            };
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection && f.Count == 3 && f.Read().Level == 15,
                "锁外规划中GM同revision替换等级快照时关闭原请求，不用旧资格消费");
        }
        using (var f = new Fixture(towns, characters))
        {
            var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive);
            f.Select();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "药剂房间guard用例经合法入口旅行");
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is not null && f.Session.CompleteFormalDungeonLoading() is not null,
                "药剂guard建立真实普通正式实例与房间，不借开发诊断状态");
            var result = f.Use();
            That(result.Outcome == ConsumableUseOutcome.Committed && result.Guard?.DungeonIdentity is not null,
                "正式实例消费回执携带原房间epoch");
            var old = result.Guard!;
            var forged = old with { DungeonIdentity = old.DungeonIdentity! with { Epoch = old.DungeonIdentity!.Epoch + 1 } };
            That(!f.Session.CanPublishConsumable(forged), "同角色快照和同phase仍拒绝另一房间epoch，不能仅按库存phase发布");
            f.Session.ReturnFromFormalDungeon(); f.Session.BeginDungeonSelection();
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection &&
                f.Use(4).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2,
                "成功后进入MapSelect不能让旧完整请求改发failureACK，新非法场景请求仍正常拒绝");
            That(!f.Session.CanPublishConsumable(old), "正式回城使旧实例消费回执失效");
        }
    }

    public static void RunQualificationExpiry(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        using var f = new Fixture(towns, characters);
        f.Select();
        f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
        {
            Items = character.Inventory.Items.Select(item => item.Position == f.Request.Position ? item with
                { Core = item.Core with { ExpireTime = 1900000002 } } : item).ToImmutableArray()
        }, "potion_free_expiry_fixture")));
        That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "免费源期限回归经真实城镇入口旅行");
        f.Session.BeginDungeonSelection();
        That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
            global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive), practice: true) is not null &&
            f.Session.CompleteFormalDungeonLoading() is not null, "免费源期限回归建立真实练习实例");
        string before = JsonSerializer.Serialize(f.Read());
        f.Clock.BeforeUtcRead = () => { if (f.Clock.Reads == 4) f.Clock.Advance(TimeSpan.FromSeconds(3)); };
        That(f.Use().Outcome == ConsumableUseOutcome.Rejected && JsonSerializer.Serialize(f.Read()) == before,
            "免费副本零变化计划在Repository只读复验才跨截止，正常拒绝且原库存和revision不变");
        f.Clock.BeforeUtcRead = null;
        f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
        {
            Items = character.Inventory.Items.Select(item => item.Position == f.Request.Position ? item with
                { Core = item.Core with { ExpireTime = 0 } } : item).ToImmutableArray()
        }, "potion_free_expiry_fixture_restore")), requireTown: false);
        var retry = f.Use(8);
        That(retry.Outcome == ConsumableUseOutcome.Committed && retry.PreserveSource && f.Count == 3,
            "正常期限失败不写冷却或成功token，同连接修复合法源后可继续免费使用");
        InventoryResult Noop(Character character) => new(true, 0, new(character.Inventory, character.Inventory, "booster_scene_probe"));
        That(!f.Session.PlanAndCommitBooster(Noop, new(Village: true, SeriaRoom: true)).Accepted &&
            f.Session.PlanAndCommitBooster(Noop, new()).Accepted,
            "真实副本拒绝带城镇/房间并集声明的盒，而无声明保留既有副本合法地点");
    }

    static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    internal sealed class FakeClock : TimeProvider
    {
        long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public int Reads { get; private set; }
        public Action? BeforeUtcRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++; BeforeUtcRead?.Invoke();
            return DateTimeOffset.FromUnixTimeSeconds(1900000000).AddMilliseconds(milliseconds);
        }
        public void Advance(TimeSpan amount) => milliseconds += checked((long)amount.TotalMilliseconds);
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-potion-session-" + Guid.NewGuid().ToString("N"));
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public FakeClock Clock { get; } = new();
        public InventoryConsumableUseService Service { get; }
        public ConsumableUseRequest Request { get; } = new(new(InventoryListType.Main, 65), 0, 1108, 0);
        public Fixture(TownCatalog towns, CharacterCatalog characters, bool lost = false)
        {
            Directory.CreateDirectory(root); Repository = new(Path.Combine(root, "game.db")); Owner = Owners.Acquire("potion-session", () => { });
            var candidate = CharacterCreation.Prepare("Potion", characters.Get(0), towns.Room, towns.InitialReturn, null);
            Repository.Create(Owner, candidate with { Level = 16, Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, new(InventoryListType.Main, 65), new(1108, ItemKind.Consumable, 3) { Marker16 = 44, SortLockFlag = 1 }))
                .Add(new(0, new(InventoryListType.Main, 66), new(1114, ItemKind.Consumable, 2))) } });
            var store = lost ? new QuestCompletionRepositoryTests.LostResultRepository(Repository) { LoseInventoryResult = true } : null;
            Session = new(store is null ? Repository : store, towns, Owner);
            Service = new(new([ConsumableUseTests.Hp, ConsumableUseTests.Mp]), Clock);
        }
        public void Select() { Session.Select(0); Session.MarkReady(); }
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public int Count => Read().Inventory.Find(new(InventoryListType.Main, 65))?.Core.Count ?? 0;
        public ConsumableUseResult Use(uint check = 0) => Session.UseConsumable(Request with { OpaqueCheck = check }, Service);
        public void Sql(string sql)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root, true); }
    }
}
