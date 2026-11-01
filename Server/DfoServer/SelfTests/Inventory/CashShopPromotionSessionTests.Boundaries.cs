using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionSessionTests
{
    // 真实Buy和共享PVF；规划回调只用于精确停在Session捕获与提交之间。
    public static void RunBoundaries(TcpTestResources resources)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-promotion-boundaries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var items = resources.ItemResources;
            var catalog = Catalog(items.Items);
            var repository = new CharacterRepository(Path.Combine(root, "game.db"), resources.Quests, promotions: () => catalog);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("promotion-boundaries", () => { });
            var created = repository.Create(owner, DfoServer.Game.Characters.CharacterCreation.Prepare("PromoGate",
                resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get)
                with { AcceptedStoryLevel = 1, ActiveMainQuests = [new(0, 3145, 1)] });
            repository.CommitInventory(owner, created, new(created.Inventory,
                CurrencyWallet.Set(created.Inventory, CurrencyKind.Cera, 10000), "seed"));
            var clock = new PromotionClock();
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock, promotionStore: repository);
            var cash = new InventoryCashShopService(items.Items, Shop, items.GoldLimits, items.EpicPieces, items.Cargo);
            var request = new CashShopPurchaseRequest(0, [new(700101, 0), new(700101, 0)]);
            Action? duringPlan = null;
            var service = new CashShopPromotionService(() => catalog, id =>
            {
                var action = duringPlan;
                duringPlan = null;
                action?.Invoke();
                return items.Items.Get(id);
            });
            string Snapshot() => JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) +
                JsonSerializer.Serialize(repository.Read(owner, Rules.Scope));
            void Reset() { session.ReturnToRoster(); session.Select(0); session.MarkReady(); }
            void Conflict(string name, Action mutate)
            {
                Reset();
                string saved = Snapshot();
                duringPlan = mutate;
                var result = session.BuyCashShop(request, cash, service, out var purchase, out var commit);
                That(!result.Accepted && result.Receipt is null && purchase is null && commit is null && Snapshot() == saved,
                    name + "使捕获后的原Buy计划失效，库存、费用及累计不提交");
            }
            That(!session.BuyCashShop(request, cash, service, out _, out _).Accepted, "未选角不能购买促销");
            session.Select(0);
            That(!session.BuyCashShop(request, cash, service, out _, out _).Accepted, "未Ready不能购买促销");
            Conflict("同角色重新选角", Reset);
            Conflict("GM替换内存快照", () => typeof(GameSession).GetProperty(nameof(GameSession.Selected))!
                .SetValue(session, session.Selected! with { }));
            Conflict("目录关闭", () => catalog = Catalog(items.Items, false));
            catalog = Catalog(items.Items);
            Conflict("目录关闭后重开ABA", () => { catalog = Catalog(items.Items, false); catalog = Catalog(items.Items); });
            Conflict("目录规则变更", () => catalog = Catalog(items.Items, rules: Rules with { MaximumPurchases = 4999 }));
            catalog = Catalog(items.Items);
            Reset();
            duringPlan = () => clock.Now = clock.Now.AddSeconds(2);
            That(session.BuyCashShop(request, cash, service, out _, out var first).Accepted && first?.Progress.Count == 2,
                "实际Session捕获后跨年仍购买成功且不清零");
            Reset();
            string beforeExpiry = Snapshot();
            var cutoff = clock.Now.AddSeconds(1);
            var expiring = new CashShopPromotionService(() => catalog, id =>
            {
                clock.Now = cutoff;
                return items.Items.Get(id) with { Expiration = new(cutoff.ToUnixTimeSeconds()) };
            });
            That(!session.BuyCashShop(request, cash, expiring, out _, out _).Accepted && Snapshot() == beforeExpiry,
                "原Buy规划后赠礼自身在提交等号到期，主货、扣费与全部权益一起拒绝");
            string committedBeforeAdvance = Snapshot();
            clock.Now = clock.Now.AddDays(1);
            That(Snapshot() == committedBeforeAdvance && repository.Read(owner, Rules.Scope).Count == 2,
                "单纯推进时钟不重写既有购买及赠礼事实");

            Reset();
            That(session.LeaveRoom(), "促销边界合法出房");
            That(session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "促销边界合法旅行至入口");
            That(session.EnterFormalDungeon(3, 3145, 0, SelfTestCatalogs.Dungeons(resources.Archive), SelfTestCatalogs.Rooms(resources.Archive)) is not null,
                "促销边界复用正式入图预留：" + session.LastFormalDungeonRejection);
            That(!session.BuyCashShop(request, cash, service, out _, out _).Accepted, "加载中不购买促销");
            That(session.CompleteFormalDungeonLoading() is not null, "促销边界完成真实副本加载");
            var bought = session.BuyCashShop(request, cash, service, out _, out var dungeonCommit);
            That(bought.Accepted && dungeonCommit?.Progress.Count == 4 && bought.Receipt?.Phase == SessionPhase.Dungeon,
                "真实副本Session沿同一规则购买赠礼");
            That(session.ReturnFromFormalDungeon() is not null && session.PendingMailboxRestore() is not null,
                "赠礼提交后尚未发布网络结果，回城仍有持久邮箱恢复提示");
            var oldHint = session.PendingMailboxRestore()!;
            SetPhase(SessionPhase.Dungeon);
            session.NotifyMailbox(created.Id, restoreOnTown: true);
            SetPhase(SessionPhase.Town);
            session.ConfirmMailboxRestore(oldHint);
            That(session.PendingMailboxRestore() is { } newer && !ReferenceEquals(oldHint, newer), "旧恢复回调不可清除较新提示");
            var current = session.PendingMailboxRestore()!;
            SetPhase(SessionPhase.Dungeon);
            session.ConfirmMailboxRestore(current);
            SetPhase(SessionPhase.Town);
            That(ReferenceEquals(current, session.PendingMailboxRestore()), "回城中断不清提示");
            session.ConfirmMailboxRestore(current);
            That(session.PendingMailboxRestore() is null, "成功恢复才清当前提示");

            // 阶段门禁复用同一真实Run；这里只合成Settlement阶段，不冒充真实结算流程。
            Reset();
            That(session.LeaveRoom() && session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)) &&
                session.EnterFormalDungeon(3, 3145, 0, SelfTestCatalogs.Dungeons(resources.Archive), SelfTestCatalogs.Rooms(resources.Archive)) is not null &&
                session.CompleteFormalDungeonLoading() is not null, "第二次正式Run已加载");
            SetPhase(SessionPhase.Settlement);
            That(session.BuyCashShop(request, cash, service, out _, out var settlement).Accepted && settlement?.Progress.Count == 6,
                "原允许的合成结算阶段使用相同赠礼规则");
            SetPhase(SessionPhase.Dungeon);
            string? savedAfterMove = null;
            duringPlan = () => { That(session.ReturnFromFormalDungeon() is not null, "规划期间正式回城成功"); savedAfterMove = Snapshot(); };
            That(!session.BuyCashShop(request, cash, service, out _, out _).Accepted && Snapshot() == savedAfterMove &&
                repository.Read(owner, Rules.Scope).Count == 6, "规划中正式回城的角色更新不被旧购买覆盖");
            Reset();
            string savedBeforeOwner = Snapshot();
            AccountOwners.Lease? replacement = null;
            try
            {
                duringPlan = () => replacement = owners.Acquire(owner.Account, () => { });
                Reject<InvalidOperationException>(() => session.BuyCashShop(request, cash, service, out _, out _), "捕获后owner接管拒绝旧购买");
                That(JsonSerializer.Serialize(repository.LoadCharacter(replacement!, 0)) +
                    JsonSerializer.Serialize(repository.Read(replacement!, Rules.Scope)) == savedBeforeOwner,
                    "旧owner不扣款、不累加、不发奖");
            }
            finally { replacement?.Dispose(); }
            void SetPhase(SessionPhase phase) => typeof(GameSession).GetProperty(nameof(GameSession.Phase))!.SetValue(session, phase);
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"Cash shop promotion boundaries: {Count - before} checks passed.");
    }

    sealed class PromotionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
